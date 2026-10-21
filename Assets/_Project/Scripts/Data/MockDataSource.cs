using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using UnityEngine;
using SchoolTwin.Core;
using SchoolTwin.Twin;
using Debug = UnityEngine.Debug;

namespace SchoolTwin.Data
{
    /// <summary>
    /// Mock 数据源：不接任何真实设备，用正弦波 + 噪声按点位目录生成数据。
    ///
    /// 它的价值不是“假数据”，而是让「数据 → 场景 → 交互」这条链路先跑通：
    ///   · 链路对了，P2 把这里换成 MQTT 客户端即可，场景侧一行不用改；
    ///   · 链路错了，在 Mock 阶段发现，比接上真实设备再排查便宜得多。
    /// </summary>
    [AddComponentMenu("SchoolTwin/数据源 - Mock (MockDataSource)")]
    public class MockDataSource : DataSourceBase
    {
        [Header("Mock 生成")]
        [Tooltip("点位目录：决定生成哪些点位、哪些指标、量程多少")]
        [SerializeField] private TwinCatalog _catalog;

        [Tooltip("每秒生成批数。P1 验收用 10Hz")]
        [SerializeField] private float _hz = 10f;

        [Tooltip("噪声幅度（占量程比例）")]
        [Range(0f, 0.3f)]
        [SerializeField] private float _noiseRatio = 0.03f;

        [Tooltip("不同指标的相位错开，避免所有曲线整齐划一")]
        [SerializeField] private bool _staggerPhase = true;

        [Header("偶发异常（让演示中能看到告警，而不是一屋子同时报警）")]
        [Tooltip("异常事件周期（秒）。每隔这么久制造一次短暂越界。<=1 表示永不越界")]
        [SerializeField] private float _excursionPeriodSeconds = 45f;

        [Tooltip("每次异常事件持续秒数")]
        [SerializeField] private float _excursionSeconds = 6f;

        [Tooltip("异常峰值冲向量程边界的深度。0.95 ≈ 冲到 Min/Max 附近")]
        [Range(0.3f, 1f)]
        [SerializeField] private float _excursionDepth = 0.95f;

        [Tooltip("模拟丢包率，用于验证脏数据处理")]
        [Range(0f, 1f)]
        [SerializeField] private float _dropRate = 0f;

        private static readonly ConcurrentDictionary<string, float> s_overrides =
            new ConcurrentDictionary<string, float>();

        /// <summary>
        /// 被「断电」的点位：**整台设备一条样本都不发**。
        ///
        /// 【为什么用「停发」而不是「发一个 0」】这是断电和关灯的本质区别：
        /// 关灯时设备还在，会继续上报 `Power=0`；断电时设备不在了，一个字节都没有。
        /// 如果断电也发 0，那么"沉默 8 秒判离线"这条判据就永远走不到 ——
        /// 而 P15 要证明的恰恰是"断电会表现为离线，但系统显示的是断电"。
        ///
        /// 【为什么键只有 TwinId、不带 metric】断电是整台设备的事，
        /// 按指标停发会出现"功率停了、温度还在报"这种物理上不存在的状态。
        /// </summary>
        private static readonly ConcurrentDictionary<string, byte> s_muted =
            new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

        private long _silenceUntilTicks;
        private volatile int _emittedTotal;

        /// <summary>
        /// 每次进入 Play 都把两张静态表清空。
        ///
        /// 【为什么这条不能省】「停发」的残留后果极其隐蔽：上一轮断过电的设备，
        /// 这一轮开局就一个字节都不发，场景里表现为"这几个点位永远没数据"，
        /// 而 Console 干干净净 —— 排查方向会被引到数据源/点位上，而不是这里。
        /// 顺手把 override 也清了（它原来只在手动菜单里清，同一个坑）。
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticsOnLoad()
        {
            s_muted.Clear();
            s_overrides.Clear();
        }

        public override string SourceName { get { return "Mock"; } }
        public int EmittedTotal { get { return _emittedTotal; } }
        public float Frequency { get { return _hz; } }

        public TwinCatalog Catalog
        {
            get { return _catalog; }
            set { _catalog = value; }
        }

        public override string StatusText
        {
            get
            {
                string s = base.StatusText + " " + _hz.ToString("0.#") + "Hz";
                if (IsSilencing) s += " (演练静默中)";
                return s;
            }
        }

        public bool IsSilencing
        {
            get { return DateTime.UtcNow.Ticks < Interlocked.Read(ref _silenceUntilTicks); }
        }

        // ---------------------------------------------------------------- 后台循环

        protected override void RunWorker(CancellationToken token)
        {
            TwinPointDefinition[] points = CollectPoints();

            if (points.Length == 0)
            {
                Debug.LogWarning("[Twin] MockDataSource 没有可生成的点位。请在 Inspector 指定 TwinCatalog，" +
                                 "或使用菜单「P1 孪生框架 / ① 生成 Mock 闭环」自动创建。");
                SetState(DataSourceState.Degraded);
                while (!token.WaitHandle.WaitOne(500)) { }
                return;
            }

            Stopwatch clock = Stopwatch.StartNew();
            System.Random rng = new System.Random(20261014);
            int intervalMs = Mathf.Max(1, Mathf.RoundToInt(1000f / Mathf.Max(0.1f, _hz)));
            int totalMetrics = CountMetrics(points);

            SetState(DataSourceState.Running);
            Debug.Log("[Twin] Mock 数据源启动：" + points.Length + " 个点位 / " + totalMetrics +
                      " 个指标 / " + _hz.ToString("0.#") + "Hz");

            while (!token.IsCancellationRequested)
            {
                double t = clock.Elapsed.TotalSeconds;

                // 断线演练：静默期内不发数据，让场景侧自然进入 Stale/Offline
                if (DateTime.UtcNow.Ticks < Interlocked.Read(ref _silenceUntilTicks))
                {
                    if (token.WaitHandle.WaitOne(intervalMs)) break;
                    continue;
                }

                // 丢包演练
                if (_dropRate > 0f && rng.NextDouble() < _dropRate)
                {
                    if (token.WaitHandle.WaitOne(intervalMs)) break;
                    continue;
                }

                double nowUtc = NowUtcSeconds();
                int seq = 0;

                for (int i = 0; i < points.Length; i++)
                {
                    TwinPointDefinition point = points[i];
                    if (point == null || point.Metrics == null) continue;

                    // 断电点位整台跳过：一条样本都不发。
                    // 这个判断放在最外层（而不是逐个 metric），是为了让"整台设备消失"
                    // 这件事在代码里也只有一处表达。
                    if (s_muted.ContainsKey(point.TwinId)) continue;

                    for (int k = 0; k < point.Metrics.Count; k++)
                    {
                        TwinMetricDefinition metric = point.Metrics[k];
                        if (metric == null) continue;

                        float span = metric.Max - metric.Min;

                        // 平时在正常区间内波动，偶发短暂越界。
                        // 与 MqttLoopbackPublisher 共用 MockSignalGenerator，
                        // 保证「换数据源」时画面表现一致（P1 架构承诺的延伸）。
                        MockSignalGenerator.Config cfg = new MockSignalGenerator.Config
                        {
                            PeriodSeconds = 6.2831853f / OmegaOf(metric),
                            ExcursionPeriodSeconds = _excursionPeriodSeconds,
                            ExcursionSeconds = _excursionSeconds,
                            ExcursionDepth = _excursionDepth
                        };
                        float value = MockSignalGenerator.Next(metric, t, PhaseOf(i, k, seq), in cfg);
                        value += (float)((rng.NextDouble() - 0.5) * 2.0) * span * _noiseRatio;

                        float overridden;
                        if (s_overrides.TryGetValue(Key(point.TwinId, metric.Metric), out overridden))
                            value = overridden;

                        TwinSample sample = new TwinSample(point.TwinId, metric.Metric, value, nowUtc);
                        Sink.Emit(in sample);

                        Interlocked.Increment(ref _emittedTotal);
                        seq++;
                    }
                }

                if (token.WaitHandle.WaitOne(intervalMs)) break;
            }
        }

        private TwinPointDefinition[] CollectPoints()
        {
            if (_catalog == null || _catalog.Points == null) return new TwinPointDefinition[0];

            List<TwinPointDefinition> list = new List<TwinPointDefinition>(_catalog.Points.Count);
            for (int i = 0; i < _catalog.Points.Count; i++)
            {
                TwinPointDefinition p = _catalog.Points[i];
                if (p == null || string.IsNullOrEmpty(p.TwinId)) continue;
                if (p.Metrics == null || p.Metrics.Count == 0) continue;
                list.Add(p);
            }
            return list.ToArray();
        }

        private static int CountMetrics(TwinPointDefinition[] points)
        {
            int n = 0;
            for (int i = 0; i < points.Length; i++)
            {
                if (points[i] != null && points[i].Metrics != null) n += points[i].Metrics.Count;
            }
            return n;
        }

        private float PhaseOf(int pointIndex, int metricIndex, int seq)
        {
            if (!_staggerPhase) return 0f;
            return pointIndex * 1.1f + metricIndex * 2.3f + seq * 0.37f;
        }

        /// <summary>周期 20~60 秒，用指标名做稳定散列，保证每次运行曲线可复现。</summary>
        private static float OmegaOf(TwinMetricDefinition metric)
        {
            int h = StableHash(metric.Metric);
            float period = 20f + (h % 41);
            return 6.2831853f / period;
        }

        private static int StableHash(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            unchecked
            {
                int h = 17;
                for (int i = 0; i < s.Length; i++) h = h * 31 + s[i];
                return h & 0x7FFFFFFF;
            }
        }

        private static string Key(string twinId, string metric)
        {
            return twinId + "|" + metric;
        }

        // ---------------------------------------------------------------- 演示 / 调试 API

        /// <summary>手动把一个指标钉在指定值（会覆盖正弦波），用于“改一个数看场景反应”。</summary>
        public static void SetOverride(string twinId, string metric, float value)
        {
            s_overrides[Key(twinId, metric)] = value;
        }

        public static bool ClearOverride(string twinId, string metric)
        {
            float ignored;
            return s_overrides.TryRemove(Key(twinId, metric), out ignored);
        }

        public static void ClearAllOverrides()
        {
            s_overrides.Clear();
        }

        /// <summary>
        /// 停发 / 恢复某台设备的全部样本（断电模拟用）。
        ///
        /// 幂等：重复设同一个值什么都不发生 —— 调用方是每 0.5 秒对账一次的，
        /// 不幂等的话日志和集合操作都会被无意义地重复触发。
        /// </summary>
        public static void SetMuted(string twinId, bool muted)
        {
            if (string.IsNullOrEmpty(twinId)) return;

            if (muted) s_muted.TryAdd(twinId, 0);
            else
            {
                byte ignored;
                s_muted.TryRemove(twinId, out ignored);
            }
        }

        public static bool IsMuted(string twinId)
        {
            return !string.IsNullOrEmpty(twinId) && s_muted.ContainsKey(twinId);
        }

        /// <summary>当前停发的点位数（自检 / 调试用）。</summary>
        public static int MutedCount { get { return s_muted.Count; } }

        public static void ClearAllMuted()
        {
            s_muted.Clear();
        }

        /// <summary>让数据源静默若干秒 —— 用于验收「拔掉数据源 → 显示离线而不是崩溃」。</summary>
        public void SimulateSilence(float seconds)
        {
            long until = DateTime.UtcNow.AddSeconds(Mathf.Max(0f, seconds)).Ticks;
            Interlocked.Exchange(ref _silenceUntilTicks, until);
            Debug.Log("[Twin] Mock 数据源静默 " + seconds.ToString("0.#") + " 秒，观察场景应变为「陈旧/离线」");
        }

        [ContextMenu("演练：静默 8 秒")]
        private void SimulateSilenceMenu()
        {
            SimulateSilence(8f);
        }

        [ContextMenu("演练：把第一个点位推到告警")]
        private void SimulateAlarmMenu()
        {
            if (_catalog == null || _catalog.Count == 0)
            {
                Debug.LogWarning("[Twin] 未指定点位目录，无法演练告警。");
                return;
            }
            TwinPointDefinition point = _catalog.Points[0];
            if (point == null || point.Metrics == null || point.Metrics.Count == 0) return;
            SimulateAlarm(point.TwinId, point.Metrics[0].Metric);
        }

        [ContextMenu("清除所有手动注入值")]
        private void ClearOverridesMenu()
        {
            ClearAllOverrides();
            Debug.Log("[Twin] 已清除所有手动注入值，数据恢复为模拟波形。");
        }

        /// <summary>把一个指标推到告警区间外，用于验收「越界 → 变色/告警」。</summary>
        public void SimulateAlarm(string twinId, string metric)
        {
            TwinMetricDefinition def = null;
            if (_catalog != null)
            {
                TwinPointDefinition point = _catalog.Find(twinId);
                if (point != null) def = point.FindMetric(metric);
            }

            float value = 999f;
            if (def != null) value = def.AlarmHigh + Mathf.Max(0.1f, (def.Max - def.Min) * 0.1f);

            SetOverride(twinId, metric, value);
            Debug.Log("[Twin] 已注入越界值 " + twinId + "." + metric + " = " + value.ToString("0.##"));
        }

        [ContextMenu("打印点位目录")]
        private void PrintCatalog()
        {
            if (_catalog == null)
            {
                Debug.LogWarning("[Twin] MockDataSource 未指定 TwinCatalog");
                return;
            }
            string text = "[Twin] 点位目录 " + _catalog.name + "：" + _catalog.Count + " 个点位\n";
            for (int i = 0; i < _catalog.Points.Count; i++)
            {
                TwinPointDefinition p = _catalog.Points[i];
                if (p == null) continue;
                text += "  " + p.TwinId + " (" + p.DisplayName + ")";
                if (p.Metrics != null)
                {
                    for (int k = 0; k < p.Metrics.Count; k++)
                    {
                        TwinMetricDefinition m = p.Metrics[k];
                        if (m != null) text += "  [" + m.Metric + " " + m.Min + "~" + m.Max + m.Unit + "]";
                    }
                }
                text += "\n";
            }
            Debug.Log(text);
        }
    }
}

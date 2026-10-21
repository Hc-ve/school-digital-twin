using System;
using System.Collections.Generic;
using UnityEngine;
using SchoolTwin.Core;

namespace SchoolTwin.Twin
{
    /// <summary>
    /// 孪生锚点：场景里「一个可被数据驱动的对象」。
    ///
    /// 职责边界（刻意做窄）：
    ///   · 只负责“持有数据 + 判定状态 + 广播变化”
    ///   · 不负责怎么显示（那是 TwinStatusColor / TwinValueLabel 的事）
    ///   · 不负责数据从哪来（那是 IDataSource 的事）
    /// 这样换可视化方式、换数据源都不用动它。
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("SchoolTwin/孪生锚点 (TwinAnchor)")]
    public class TwinAnchor : MonoBehaviour, ITwinConsumer
    {
        [Header("身份（对应《点位映射表》）")]
        [SerializeField] private string _twinId = "Class2-1/Sensor/Temp_01";
        [SerializeField] private string _displayName = "";
        [Tooltip("留空则自动取 TwinId 的第一段，如 Class2-1/Sensor/Temp_01 → Class2-1")]
        [SerializeField] private string _zone = "";
        [Tooltip("留空则自动取 TwinId 的第二段")]
        [SerializeField] private string _category = "";

        [Header("视觉绑定（留空 = 本物体）")]
        [SerializeField] private GameObject _visualTarget;
        [Tooltip("用于引用资源包 Assets/TirGames 内的只读对象，填场景层级路径，如 SchoolSceneDay/Class2-1/Desk")]
        [SerializeField] private string _visualTargetPath = "";

        [Header("时效与通知")]
        [Tooltip("超过这个秒数没收到数据 → Stale（陈旧，灰）")]
        [SerializeField] private float _staleAfterSeconds = 3f;
        [Tooltip("在 Stale 基础上再沉默这么久 → Offline（离线，暗）。判定的是「沉默时长」，与数据源类型无关")]
        [SerializeField] private float _offlineAfterSeconds = 5f;
        [Tooltip("两次刷新事件的最小间隔，防止高频抖动导致 UI 反复重绘")]
        [SerializeField] private float _minNotifyInterval = 0.05f;
        [SerializeField] private bool _logEverySample = false;

        private readonly Dictionary<string, TwinValue> _values =
            new Dictionary<string, TwinValue>(StringComparer.Ordinal);

        /// <summary>每个指标上一次的判定结果 —— 回差判定要用它（见 ApplyDeadband）。</summary>
        private readonly Dictionary<string, TwinStatus> _metricStatus =
            new Dictionary<string, TwinStatus>(4, StringComparer.Ordinal);

        /// <summary>已经提醒过「未配置阈值」的 (TwinId|Metric)。跨实例共享，避免刷屏。</summary>
        private static readonly HashSet<string> s_unconfiguredWarned =
            new HashSet<string>(StringComparer.Ordinal);

        private TwinCatalog _catalog;
        private GameObject _resolvedTarget;
        private float _lastSampleRealtime = -1f;
        private float _lastNotifyRealtime = -999f;
        private TwinStatus _status = TwinStatus.Unknown;
        private TwinStatus _reportedStatus = TwinStatus.Unknown;

        /// <summary>某个指标发生“有意义的变化”时触发（已按 epsilon 与最小间隔节流）。</summary>
        public event Action<TwinAnchor, string, TwinValue> MetricChanged;
        /// <summary>整体状态变化时触发。</summary>
        public event Action<TwinAnchor, TwinStatus> StatusChanged;

        public string TwinId { get { return _twinId; } }
        public string DisplayName { get { return string.IsNullOrEmpty(_displayName) ? _twinId : _displayName; } }
        public string ConsumerName { get { return "TwinAnchor(" + _twinId + ")"; } }
        public TwinStatus Status { get { return _status; } }
        public int MetricCount { get { return _values.Count; } }
        public IReadOnlyDictionary<string, TwinValue> Values { get { return _values; } }
        public bool HasData { get { return _values.Count > 0; } }

        public string Zone
        {
            get { return string.IsNullOrEmpty(_zone) ? Segment(_twinId, 0) : _zone; }
        }

        public string Category
        {
            get { return string.IsNullOrEmpty(_category) ? Segment(_twinId, 1) : _category; }
        }

        /// <summary>
        /// 数据是否已陈旧（本机接收时刻判定，不依赖数据源时钟）。
        /// 注意这是**第一段**：陈旧之后若继续沉默，Update() 会把状态推进到 Offline。
        /// </summary>
        public bool IsStale
        {
            get
            {
                if (_lastSampleRealtime < 0f) return false;
                return (Time.realtimeSinceStartup - _lastSampleRealtime) > _staleAfterSeconds;
            }
        }

        /// <summary>距上一次收到数据的秒数；-1 表示至今没收到过任何数据。UI 用来显示「数据新鲜度」。</summary>
        public float SecondsSinceLastSample
        {
            get
            {
                if (_lastSampleRealtime < 0f) return -1f;
                return Time.realtimeSinceStartup - _lastSampleRealtime;
            }
        }

        public GameObject VisualTarget
        {
            get
            {
                if (_visualTarget != null) return _visualTarget;
                if (!string.IsNullOrEmpty(_visualTargetPath))
                {
                    if (_resolvedTarget == null)
                    {
                        _resolvedTarget = GameObject.Find(_visualTargetPath);
                        if (_resolvedTarget == null)
                        {
                            Debug.LogWarning("[Twin] TwinAnchor(" + _twinId + ") 找不到路径：" +
                                             _visualTargetPath + "，回退到本物体");
                        }
                    }
                    if (_resolvedTarget != null) return _resolvedTarget;
                }
                return gameObject;
            }
        }

        // ---------------------------------------------------------------- 生命周期

        private void OnEnable()
        {
            // 注册挪到 Start()：OnEnable 时 TwinRuntime.Awake 可能还没跑，Registry 是 null
        }

        private void Start()
        {
            TwinRuntime runtime = TwinRuntime.Ensure();
            _catalog = runtime.Catalog;
            try { runtime.Registry.Register(this); }
            catch (System.Exception e) { Debug.LogWarning("[Twin] " + _twinId + " 注册警告：" + e.Message); }
            runtime.Bus.Subscribe(_twinId, this);
        }

        private void OnDisable()
        {
            TwinRuntime runtime = TwinRuntime.Instance;
            if (runtime == null) return;
            runtime.Bus.Unsubscribe(_twinId, this);
            runtime.Registry.Unregister(this);
        }

        private void Update()
        {
            if (_lastSampleRealtime < 0f) return;

            if (!IsStale)
            {
                // 数据刚恢复：把 Stale/Offline 清掉，重新按阈值评估状态
                if (_status == TwinStatus.Stale || _status == TwinStatus.Offline)
                {
                    EvaluateStatus();
                }
                return;
            }

            // 两段式时效：沉默超过 _staleAfterSeconds → Stale；再持续沉默 _offlineAfterSeconds → Offline。
            //
            // 【为什么不用数据源的 StateChanged 事件来判离线】
            // 那样会让「离线」这个状态依赖具体的数据源实现（MQTT / 回放 / 真实网关各不相同），
            // 于是每接一种新数据源都要重新实现一遍离线判定。
            // 改成「沉默时长」判定后，离线语义与数据源完全解耦：
            // 不管是 broker 挂了、进程被杀、还是网线被拔，只要数据不再到达，就一定会走到 Offline。
            //
            // 【为什么不能一步到位直接 Offline】
            // 短暂丢包（一两秒）应该是可恢复的「陈旧」，不该立刻渲染成「离线」——
            // 否则网络一抖全场景变黑，运维会误判成大面积设备故障。
            TwinStatus next = SilentSeconds > (_staleAfterSeconds + _offlineAfterSeconds)
                ? TwinStatus.Offline
                : TwinStatus.Stale;

            if (_status == next) return;
            SetStatus(next);
            ReportIfNeeded(true);
        }

        /// <summary>距上次收到数据已沉默的秒数；未收到过任何数据时为 0。</summary>
        private float SilentSeconds
        {
            get
            {
                if (_lastSampleRealtime < 0f) return 0f;
                return Time.realtimeSinceStartup - _lastSampleRealtime;
            }
        }

        private void OnValidate()
        {
            _staleAfterSeconds = Mathf.Max(0.1f, _staleAfterSeconds);
            _offlineAfterSeconds = Mathf.Max(0f, _offlineAfterSeconds);
            _minNotifyInterval = Mathf.Max(0f, _minNotifyInterval);
        }

        // ---------------------------------------------------------------- 数据入口

        public void OnSample(in TwinSample sample)
        {
            if (!string.Equals(sample.TwinId, _twinId, StringComparison.Ordinal)) return;

            _lastSampleRealtime = Time.realtimeSinceStartup;

            bool significant;
            TwinValue value;
            TwinValue prev;
            if (_values.TryGetValue(sample.Metric, out prev))
            {
                significant = Mathf.Abs(sample.Value - prev.Value) > GetEpsilon(sample.Metric)
                              || prev.Quality != sample.Quality;
                value.PreviousValue = prev.Value;
            }
            else
            {
                significant = true;
                value.PreviousValue = sample.Value;
            }

            value.Value = sample.Value;
            value.TimestampUtc = sample.TimestampUtc;
            value.Quality = sample.Quality;
            _values[sample.Metric] = value;

            if (_logEverySample) Debug.Log("[Twin] " + sample);

            EvaluateStatus();

            if (significant)
            {
                ReportIfNeeded(false);
            }
        }

        public bool TryGetValue(string metric, out TwinValue value)
        {
            return _values.TryGetValue(metric, out value);
        }

        /// <summary>取“最差”的一个指标状态，用于整机着色。</summary>
        public TwinStatus StatusOfWorst()
        {
            return _status;
        }

        /// <summary>
        /// 取单个指标的状态。
        /// 面板需要「按指标分别着色」（一个点位可能温度正常、CO₂ 告警），
        /// 所以不能只暴露整体最差状态。这里把「数据陈旧 / 离线」也计入。
        /// </summary>
        public TwinStatus StatusOf(string metric)
        {
            TwinValue value;
            if (!_values.TryGetValue(metric, out value)) return TwinStatus.Unknown;

            TwinStatus status = StatusOf(metric, value);

            // 沉默判定要覆盖到「按指标着色」这条路径，否则会出现
            // 「整机已是 Offline，但面板里每个指标还各自显示正常/告警」的矛盾画面。
            // 注意：只有真的已陈旧（IsStale）才让沉默状态参与，否则数据新鲜时
            // 也会被误判成 Stale（那是「没数据」的状态，不能盖在新鲜数据上）。
            if (IsStale)
            {
                TwinStatus silence = SilenceStatus();
                if (Severity(silence) > Severity(status)) status = silence;
            }
            return status;
        }

        /// <summary>纯由「沉默时长」推出的状态。调用方需先用 IsStale 确认已进入沉默期。</summary>
        private TwinStatus SilenceStatus()
        {
            return SilentSeconds > (_staleAfterSeconds + _offlineAfterSeconds)
                ? TwinStatus.Offline
                : TwinStatus.Stale;
        }

        // ---------------------------------------------------------------- 内部

        private void EvaluateStatus()
        {
            TwinStatus worst = TwinStatus.Unknown;
            int worstRank = -1;

            foreach (KeyValuePair<string, TwinValue> kv in _values)
            {
                TwinStatus s = StatusOf(kv.Key, kv.Value);
                int rank = Severity(s);
                if (rank > worstRank)
                {
                    worstRank = rank;
                    worst = s;
                }
                if (rank >= Severity(TwinStatus.Offline)) break;
            }

            SetStatus(worst);
        }

        private TwinStatus StatusOf(string metric, TwinValue value)
        {
            TwinStatus raw = RawStatusOf(metric, value);

            TwinStatus prev;
            if (_metricStatus.TryGetValue(metric, out prev))
            {
                raw = ApplyDeadband(metric, value.Value, raw, prev);
            }
            _metricStatus[metric] = raw;
            return raw;
        }

        /// <summary>
        /// 回差（死区）：**状态变差立刻生效，变好要等值真的离开阈值一段距离**。
        ///
        /// 【为什么必须有】温度在 27.9 / 28.1 之间抖（阈值 28），没有回差就是
        /// 「警告 → 正常 → 警告 → 正常」每 100ms 一次，告警历史会被同一条事件刷满，
        /// 声音也会变成连续的滴滴声。这不是数据问题，是判定方式问题。
        ///
        /// 【为什么不直接改阈值】阈值是「业务上什么算越界」的声明，改它等于把标准放宽了。
        /// 回差是「判定方式」，两件事分开，阈值才敢照实写。
        /// </summary>
        private TwinStatus ApplyDeadband(string metric, float value, TwinStatus raw, TwinStatus prev)
        {
            // 变差或不变：立刻生效，不许延迟（安全方向永远是即时的）
            if (Severity(raw) >= Severity(prev)) return raw;

            TwinMetricDefinition def = FindMetricDefinition(metric);
            if (def == null) return raw;

            float db = DeadbandOf(def);
            if (db <= 0f) return raw;

            if (prev == TwinStatus.Alarm)
            {
                // 必须离告警线一个死区才算真的退出告警
                if (value < def.AlarmLow + db || value > def.AlarmHigh - db) return TwinStatus.Alarm;
                return raw;
            }

            if (prev == TwinStatus.Warning && raw == TwinStatus.Normal)
            {
                if (value < def.WarnLow + db || value > def.WarnHigh - db) return TwinStatus.Warning;
            }
            return raw;
        }

        /// <summary>回差大小。没配就用警告带宽的 5%（与 Spec §8.7 的 0.5°C / 2%RH 同量级）。</summary>
        private static float DeadbandOf(TwinMetricDefinition def)
        {
            if (def == null) return 0f;
            if (def.Hysteresis > 0f) return def.Hysteresis;
            float span = def.WarnHigh - def.WarnLow;
            return span > 0f ? span * 0.05f : 0f;
        }

        /// <summary>纯阈值判定，不含回差。回差逻辑包在 <see cref="StatusOf(string, TwinValue)"/> 外面。</summary>
        private TwinStatus RawStatusOf(string metric, TwinValue value)
        {
            if (value.Quality == DataQuality.Offline) return TwinStatus.Offline;
            if (value.Quality == DataQuality.Bad) return TwinStatus.Warning;
            if (value.Quality == DataQuality.Uncertain) return TwinStatus.Warning;

            TwinMetricDefinition def = FindMetricDefinition(metric);
            if (def != null)
            {
                if (value.Value < def.AlarmLow || value.Value > def.AlarmHigh) return TwinStatus.Alarm;
                if (value.Value < def.WarnLow || value.Value > def.WarnHigh) return TwinStatus.Warning;
                return TwinStatus.Normal;
            }

            // 未配置阈值 → Unknown，**绝不能返回 Normal**。
            //
            // 这是整个链路里最危险的静默失败方向：真实设备发来一个点位目录里没有的
            // 点位/指标时，数值再离谱也会被显示成绿色「正常」。运维看到一片绿，
            // 实际问题被完全掩盖 —— 比报错难查得多。
            //
            // 用 Unknown 而不是 Warning：这是「配置缺失」不是「设备异常」，
            // 不该触发告警音（见 TwinStatusPalette.NeedsAttention），
            // 但必须在画面上呈现为灰色，让人一眼看出「这个点位没被纳入监管」。
            WarnUnconfiguredOnce(metric);
            return TwinStatus.Unknown;
        }

        /// <summary>
        /// 未配置阈值的警告只打一次。StatusOf 每个数据周期都会被调用，
        /// 不去重的话 Console 会被刷屏，反而把真正的问题淹掉。
        /// 静态集合跨实例共享 —— 同一 (TwinId, Metric) 全局只提醒一次。
        /// </summary>
        private void WarnUnconfiguredOnce(string metric)
        {
            string key = _twinId + "|" + metric;
            if (!s_unconfiguredWarned.Add(key)) return;
            Debug.LogWarning("[Twin] 点位 " + _twinId + " 的指标 " + metric +
                             " 未在点位目录中配置阈值，状态显示为「未知」（灰）。" +
                             "请在 TwinCatalog 里补上该指标的阈值。");
        }

        private TwinMetricDefinition FindMetricDefinition(string metric)
        {
            if (_catalog == null) return null;
            TwinPointDefinition point = _catalog.Find(_twinId);
            if (point == null) return null;
            return point.FindMetric(metric);
        }

        private float GetEpsilon(string metric)
        {
            TwinMetricDefinition def = FindMetricDefinition(metric);
            return def == null ? 0.01f : Mathf.Max(0f, def.Epsilon);
        }

        private void SetStatus(TwinStatus status)
        {
            if (_status == status) return;
            _status = status;
        }

        private void ReportIfNeeded(bool force)
        {
            bool statusChanged = _status != _reportedStatus;
            if (!force && !statusChanged &&
                (Time.realtimeSinceStartup - _lastNotifyRealtime) < _minNotifyInterval) return;

            _lastNotifyRealtime = Time.realtimeSinceStartup;

            if (MetricChanged != null)
            {
                foreach (KeyValuePair<string, TwinValue> kv in _values)
                {
                    MetricChanged(this, kv.Key, kv.Value);
                }
            }

            if (statusChanged)
            {
                _reportedStatus = _status;
                if (StatusChanged != null) StatusChanged(this, _status);
            }
        }

        private static int Severity(TwinStatus s)
        {
            switch (s)
            {
                case TwinStatus.Offline: return 4;
                case TwinStatus.Alarm: return 3;
                case TwinStatus.Warning: return 2;
                case TwinStatus.Stale: return 1;
                // Unknown 与 Stale 同级（都在 Normal 之上）：
                // 「未配置阈值」和「数据陈旧」一样，都不是「一切正常」，
                // 但也都不是需要立刻处置的故障。
                //
                // 【为什么必须 > Normal】EvaluateStatus 取「最差指标」作为整机状态。
                // 若 Unknown 排在 Normal 之下，一个点只要有一个配置好的指标是正常的，
                // 未配置的那个指标就会被盖住，整机仍显示绿色 —— 安全告警形同虚设。
                case TwinStatus.Unknown: return 1;
                case TwinStatus.Normal: return 0;
                default: return -1;
            }
        }

        private static string Segment(string id, int index)
        {
            if (string.IsNullOrEmpty(id)) return "";
            string[] parts = id.Split('/');
            return index < parts.Length ? parts[index] : "";
        }

        [ContextMenu("打印当前数据")]
        private void PrintValues()
        {
            string text = "[Twin] " + _twinId + "  状态=" + _status + "  陈旧=" + IsStale + "\n";
            foreach (KeyValuePair<string, TwinValue> kv in _values)
            {
                text += "    " + kv.Key + " = " + kv.Value + "\n";
            }
            Debug.Log(text);
        }
    }
}

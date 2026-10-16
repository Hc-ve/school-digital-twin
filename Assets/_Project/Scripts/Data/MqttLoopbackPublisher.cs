using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using UnityEngine;
using SchoolTwin.Twin;
using Debug = UnityEngine.Debug;

namespace SchoolTwin.Data
{
    /// <summary>
    /// 本地 MQTT 报文模拟器：按真实报文格式往 broker 发消息。
    ///
    /// 用途（P2 验收的关键工具）：
    ///   · 没有真实设备时，用它产生与设备完全一致格式的报文，验证「MQTT → 场景」链路；
    ///   · 它**完全绕过** MqttDataSource 的解析代码（自己拼 JSON 字符串），
    ///     因此能真正测出解析是否有 bug —— 如果复用它自己的解析器就是自欺欺人。
    ///   · 它发布到 `_topicFilter` 匹配的主题上，格式与 P2 文档约定的载荷一致。
    ///
    /// 使用方式：
    ///   1. 场景里加一个空物体，挂上本组件；
    ///   2. 确认 MqttDataSource 的 _host/_port 与这里一致；
    ///   3. 勾上 _autoStart（或运行时调用 StartPublishing()）；
    ///   4. 观察场景点位是否开始变色、`recv` 是否增长。
    ///
    /// 注意：本组件不依赖 MQTTnet！它只拼字符串，通过 MqttClientBridge 发送。
    /// 这样可以独立验证「发送端格式」与「接收端解析」是否一致。
    /// </summary>
    [AddComponentMenu("SchoolTwin/调试 - MQTT 本地报文模拟器 (MqttLoopbackPublisher)")]
    public class MqttLoopbackPublisher : MonoBehaviour
    {
        [Header("Broker（与 MqttDataSource 保持一致）")]
        [SerializeField] private string _host = "127.0.0.1";
        [SerializeField] private int _port = 1883;
        [SerializeField] private string _username = "";
        [SerializeField] private string _password = "";

        [Header("发布目标")]
        [Tooltip("主题前缀。最终主题 = 前缀 + TwinId + \"/\" + Metric，例如 school/Class2-1/Sensor/Temp_01/Temperature")]
        [SerializeField] private string _topicPrefix = "school/";

        [Tooltip("点位目录。用它决定发哪些点位、哪些指标、数值范围")]
        [SerializeField] private TwinCatalog _catalog;

        [Tooltip("发布频率（Hz）")]
        [SerializeField] private float _hz = 2f;

        [Tooltip("周期（秒），数值按正弦波变化。用于观察曲线是否随时间变化")]
        [SerializeField] private float _periodSeconds = 30f;

        [Tooltip("自动在 OnEnable 时开始发布")]
        [SerializeField] private bool _autoStart = true;

        [Tooltip("额外发布一些『点位表里不存在』的假主题，用于验证 orphan 计数是否正常")]
        [SerializeField] private int _fakeTopicCount = 0;

        [Tooltip("QoS。与 MqttDataSource 的订阅 QoS 对齐即可，0 最省资源")]
        [Range(0, 2)]
        [SerializeField] private int _qos = 0;

        private IMqttBridge _bridge;
        private Thread _worker;
        private CancellationTokenSource _cts;
        private volatile bool _running;
        private volatile int _sentCount;
        private double _startUtc;

        public int SentCount { get { return _sentCount; } }
        public bool IsPublishing { get { return _running; } }

        private void OnEnable()
        {
            if (_autoStart) StartPublishing();
        }

        private void OnDisable()
        {
            StopPublishing();
        }

        private void OnDestroy()
        {
            StopPublishing();
        }

        // ---------------------------------------------------------------- 启停

        [ContextMenu("开始发布")]
        public void StartPublishing()
        {
            if (_running) return;
            if (_catalog == null || _catalog.Count == 0)
            {
                Debug.LogWarning("[Twin] MqttLoopbackPublisher 未指定点位目录，无法发布。" +
                                 "请在 Inspector 指定 TwinCatalog。");
                return;
            }

            _cts = new CancellationTokenSource();
            _running = true;
            _startUtc = (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;

            _worker = new Thread(WorkerEntry);
            _worker.IsBackground = true;
            _worker.Name = "TwinMqttLoopback";
            _worker.Start();

            Debug.Log("[Twin] MQTT 本地报文模拟器已启动：" + _host + ":" + _port +
                      " 前缀=" + _topicPrefix + " " + _hz.ToString("0.#") + "Hz");
        }

        [ContextMenu("停止发布")]
        public void StopPublishing()
        {
            if (!_running) return;
            _running = false;

            try { if (_cts != null) _cts.Cancel(); } catch (Exception) { }

            try
            {
                if (_worker != null && _worker.IsAlive) _worker.Join(500);
            }
            catch (Exception) { }

            _worker = null;
            if (_cts != null)
            {
                try { _cts.Dispose(); } catch (Exception) { }
                _cts = null;
            }

            // 关闭连接必须在主线程之外做也行，但这里在主线程（OnDisable）调用，
            // 用 GetAwaiter().GetResult() 有死锁风险，因此放到独立线程里关。
            IMqttBridge b = _bridge;
            _bridge = null;
            if (b != null)
            {
                Thread t = new Thread(() => { try { b.Close(); } catch (Exception) { } });
                t.IsBackground = true;
                t.Start();
                t.Join(1000);
            }

            Debug.Log("[Twin] MQTT 本地报文模拟器已停止，累计发布 " + _sentCount + " 条");
        }

        // ---------------------------------------------------------------- 后台线程

        private void WorkerEntry()
        {
            CancellationToken token = _cts != null ? _cts.Token : CancellationToken.None;
            int attempt = 0;

            try
            {
                while (!token.IsCancellationRequested)
                {
                    attempt++;

                    IMqttBridge bridge = null;
                    try
                    {
                        bridge = new MqttClientBridge();
                        _bridge = bridge;

                        bool ok = bridge.Connect(_host, _port,
                                                 "twin-loopback-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                                                 _username, _password,
                                                 10, 30, null, 0, token);

                        if (!ok)
                        {
                            if (token.IsCancellationRequested) break;
                            float wait = 1f; // 本地模拟器：固定1秒重连，不等指数退避
                            Debug.LogWarning("[Twin] 模拟器连不上 broker，将等 " + wait.ToString("0.#") +
                                             " 秒后重试（broker 起了吗？）");
                            if (token.WaitHandle.WaitOne(TimeSpan.FromSeconds(wait))) break;
                            continue;
                        }

                        attempt = 0;
                        PublishLoop(bridge, token);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception e)
                    {
                        if (token.IsCancellationRequested) break;
                        Debug.LogError("[Twin] 模拟器异常：" + e.Message);
                    }
                    finally
                    {
                        if (bridge != null)
                        {
                            try { bridge.Close(); } catch (Exception) { }
                        }
                        _bridge = null;
                    }

                    if (token.IsCancellationRequested) break;
                }
            }
            finally
            {
                _running = false;
            }
        }

        private void PublishLoop(IMqttBridge bridge, CancellationToken token)
        {
            int intervalMs = Mathf.Max(50, Mathf.RoundToInt(1000f / Mathf.Max(0.1f, _hz)));
            int consecutiveFails = 0;

            while (!token.IsCancellationRequested && _running)
            {
                double now = (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                double t = now - _startUtc;
                long tsUnix = (long)now;

                int sent = 0;
                List<TwinPointDefinition> points = _catalog.Points;

                for (int i = 0; i < points.Count; i++)
                {
                    TwinPointDefinition point = points[i];
                    if (point == null || string.IsNullOrEmpty(point.TwinId)) continue;
                    if (point.Metrics == null) continue;

                    for (int k = 0; k < point.Metrics.Count; k++)
                    {
                        TwinMetricDefinition metric = point.Metrics[k];
                        if (metric == null || string.IsNullOrEmpty(metric.Metric)) continue;

                        float value = MakeValue(point, metric, t, i, k);
                        string topic = _topicPrefix + point.TwinId + "/" + metric.Metric;
                        string payload = MakePayload(value, tsUnix, metric.Metric);

                        if (bridge.Publish(topic, payload, _qos))
                        {
                            Interlocked.Increment(ref _sentCount);
                            sent++;
                        }
                    }
                }

                // 假主题：验证 orphan 计数
                if (_fakeTopicCount > 0)
                {
                    for (int f = 0; f < _fakeTopicCount; f++)
                    {
                        string fakeTopic = _topicPrefix + "Ghost/Unknown/GhostSensor_99/Metric" + f;
                        bridge.Publish(fakeTopic, MakePayload(1f, tsUnix, "Metric" + f), _qos);
                    }
                }

                // 连续发布失败 → 认为连接断了，退出循环让外层重连
                if (sent == 0)
                {
                    consecutiveFails++;
                    if (consecutiveFails >= 3)
                    {
                        Debug.LogWarning("[Twin] 模拟器连续 " + consecutiveFails + " 次发布失败，连接可能已断，准备重连");
                        break;
                    }
                }
                else
                {
                    consecutiveFails = 0;
                }

                if (token.WaitHandle.WaitOne(intervalMs)) break;
            }
        }

        /// <summary>按正弦波生成数值，落在指标的 [Min, Max] 之间。</summary>
        private float MakeValue(TwinPointDefinition point, TwinMetricDefinition metric,
                                double t, int pointIndex, int metricIndex)
        {
            float period = Mathf.Max(5f, _periodSeconds);
            // 每个点位/指标错开相位，避免所有曲线同步
            double ph = pointIndex * 1.7 + metricIndex * 2.9;
            double norm = 0.5 + 0.5 * Math.Sin(2.0 * Math.PI * (t / period) + ph);

            float min = metric.Min;
            float max = metric.Max;
            return (float)(min + norm * (max - min));
        }

        /// <summary>
        /// 手工拼 JSON —— 刻意不复用 MiniJson（那是解析器），
        /// 确保「发送格式」与「接收解析」是两条独立实现的路径，能互相校验。
        /// </summary>
        private static string MakePayload(float value, long tsUnix, string metric)
        {
            StringBuilder sb = new StringBuilder(64);
            sb.Append("{\"value\":");
            sb.Append(value.ToString("0.###", CultureInfo.InvariantCulture));
            sb.Append(",\"ts\":");
            sb.Append(tsUnix.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"metric\":\"");
            sb.Append(metric);
            sb.Append("\"}");
            return sb.ToString();
        }

        [ContextMenu("打印发布配置")]
        private void PrintConfig()
        {
            Debug.Log("[Twin] 模拟器配置：broker=" + _host + ":" + _port +
                      " 前缀=" + _topicPrefix + " " + _hz + "Hz 已发=" + _sentCount +
                      " 点位目录=" + (_catalog == null ? "(未指定)" : _catalog.name + "(" + _catalog.Count + ")"));
        }
    }
}

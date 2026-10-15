using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using UnityEngine;
using SchoolTwin.Core;
using SchoolTwin.Twin;
using Debug = UnityEngine.Debug;

namespace SchoolTwin.Data
{
    /// <summary>
    /// MQTT 数据源：从真实 broker（本地 EMQX / 网关）订阅点位数据。
    ///
    /// 设计边界（与 MockDataSource 完全一致，这就是 P1 承诺的“换数据源场景侧一行不改”）：
    ///   · 它只负责“把 MQTT 报文变成 TwinSample 并 Emit”，不管点位是否存在、阈值几何；
    ///   · 点位的阈值/量程/显示名一律来自 TwinCatalog，MQTT 只提供“值”和“时间”；
    ///   · 找不到对应点位的报文会照常 Emit，由数据总线记入 orphan 计数 —— 点位表写错能立刻发现。
    ///
    /// 线程模型（关键，违反必出诡异 bug）：
    ///   · RunWorker 跑在 DataSourceBase 起的后台线程上，**不是** Unity 主线程；
    ///   · 因此这里不能碰任何 Unity API，包括 Debug.Log 以外的 Time/Transform/Random；
    ///   · MQTTnet 是纯 .NET 库，可以在后台线程自由使用。
    ///
    /// 同步/异步桥接（方案 A，见 P2 文档 4.2 节）：
    ///   MQTTnet 4.x 是 async Task API，而 RunWorker 是同步阻塞签名。
    ///   这里用 GetAwaiter().GetResult() 把异步操作“就地等待完成”。
    ///   安全性依据：本线程是 Thread 新建的独立后台线程，没有 SynchronizationContext，
    ///   不会出现“UI 线程同步等异步 → 互等死锁”的经典问题。
    ///   （反例：在 Awake/Start 这类主线程方法里这样写就会死锁。）
    /// </summary>
    [AddComponentMenu("SchoolTwin/数据源 - MQTT (MqttDataSource)")]
    public class MqttDataSource : DataSourceBase
    {
        // ================================================================ 连接配置

        [Header("Broker")]
        [Tooltip("broker 地址。本地 EMQX 用 127.0.0.1；远程网关填实际 IP")]
        [SerializeField] private string _host = "127.0.0.1";

        [Tooltip("MQTT TCP 端口。EMQX 默认 1883（非加密）")]
        [SerializeField] private int _port = 1883;

        [Tooltip("客户端 ID。留空则自动生成（避免多客户端撞 ID 导致互踢）")]
        [SerializeField] private string _clientId = "";

        [Header("认证（EMQX 默认允许匿名，留空即可）")]
        [SerializeField] private string _username = "";
        [SerializeField] private string _password = "";

        [Tooltip("连接超时（秒）")]
        [SerializeField] private int _timeoutSeconds = 10;

        [Tooltip("KeepAlive 周期（秒）。小于 broker 的 1.5 倍超时时间")]
        [SerializeField] private int _keepAliveSeconds = 30;

        // ================================================================ 订阅

        [Header("订阅")]
        [Tooltip("订阅主题过滤器（支持 MQTT 通配符 + / #）。" +
                 "注意：层数必须与发布主题完全一致，多一层少一层都订阅不到。" +
                 "默认 topic 结构为 school/{Zone}/{Category}/{Point}/{Metric} 共 5 段，故用 5 个 +")]
        [SerializeField] private string _topicFilter = "school/+/+/+/+";

        [Tooltip("订阅 QoS。0=AtMostOnce 最多一次，1=AtLeastOnce 至少一次（推荐），2=ExactlyOnce")]
        [Range(0, 2)]
        [SerializeField] private int _qos = 1;

        // ================================================================ 主题 → 点位映射

        [Header("主题 → 点位映射")]
        [Tooltip("开启后，直接从 MQTT 主题路径推导 TwinId，无需在点位表里逐条填 SourceTopic")]
        [SerializeField] private bool _deriveTwinIdFromTopic = true;

        [Tooltip("主题中 TwinId 起始层（0 基）。school/Class2-1/Sensor/Temp_01 里 TwinId = Class2-1/Sensor/Temp_01，故填 1")]
        [SerializeField] private int _twinIdSegmentStart = 1;

        [Tooltip("主题中指标名所在层。取最后一段作为指标名；若为 -1 则改用 JSON 里的 metric 字段")]
        [SerializeField] private int _metricSegmentIndex = -1;

        [Header("载荷解析")]
        [Tooltip("载荷格式。JSON = {\"value\":25.3,\"ts\":1730000000}；PlainNumber = 裸数字 25.3")]
        [SerializeField] private PayloadFormat _payloadFormat = PayloadFormat.Json;

        [Tooltip("JSON 里的数值字段名")]
        [SerializeField] private string _valueField = "value";

        [Tooltip("JSON 里的时间戳字段名（Unix 秒）。留空则用本机接收时刻")]
        [SerializeField] private string _timestampField = "ts";

        [Tooltip("JSON 里的指标名字段名。当主题里推不出指标时用它")]
        [SerializeField] private string _metricField = "metric";

        [Header("诊断")]
        [Tooltip("每秒最多打印多少条无法解析的报文，避免刷屏。0 = 不打印")]
        [SerializeField] private int _badMessageLogPerSecond = 3;

        [Tooltip("打印收到的前 N 条原始报文，用于首次联调")]
        [SerializeField] private int _logFirstMessages = 5;

        public enum PayloadFormat
        {
            Json = 0,
            PlainNumber = 1,
        }

        // ================================================================ 运行期状态

        private IMqttBridge _bridge;                 // 真实实现见 MqttClientBridge（与 Unity 解耦，便于单测）
        private volatile int _recvCount;
        private volatile int _parseFailCount;
        private volatile int _lastBadLogSecond;
        private long _connectedAtTicks;

        public override string SourceName { get { return "MQTT"; } }

        public int ReceivedCount { get { return _recvCount; } }
        public int ParseFailCount { get { return _parseFailCount; } }
        public bool IsConnected { get { return _bridge != null && _bridge.IsConnected; } }

        public override string StatusText
        {
            get
            {
                string s = base.StatusText + " " + _host + ":" + _port + " [" + _topicFilter + "]";
                if (_recvCount > 0) s += " recv=" + _recvCount;
                return s;
            }
        }

        // ================================================================ 主循环

        protected override void RunWorker(CancellationToken token)
        {
            int attempt = 0;

            while (!token.IsCancellationRequested)
            {
                attempt++;
                if (attempt > 1)
                {
                    float wait = BackoffSeconds(attempt);
                    Debug.LogWarning("[Twin] MQTT 第 " + (attempt - 1) + " 次重连，等待 " +
                                     wait.ToString("0.#") + " 秒后重试");
                    if (token.WaitHandle.WaitOne(TimeSpan.FromSeconds(wait))) break;
                }

                IMqttBridge bridge = null;
                try
                {
                    SetState(DataSourceState.Connecting);

                    bridge = CreateBridge();
                    _bridge = bridge;

                    // ---- 订阅（必须在 Connect 之后，这里由 bridge 内部保证顺序）----
                    bridge.MessageReceived += OnMessageReceived;

                    bool ok = bridge.Connect(_host, _port, _clientId, _username, _password,
                                             _timeoutSeconds, _keepAliveSeconds,
                                             _topicFilter, _qos, token);

                    if (!ok)
                    {
                        if (token.IsCancellationRequested) break;
                        SetState(DataSourceState.Degraded);
                        CleanupBridge(ref bridge);
                        continue;
                    }

                    Interlocked.Exchange(ref _connectedAtTicks, DateTime.UtcNow.Ticks);
                    attempt = 0;  // 连上就重置退避
                    SetState(DataSourceState.Running);
                    Debug.Log("[Twin] MQTT 已连接 " + _host + ":" + _port +
                              "，订阅 " + _topicFilter + "（QoS " + _qos + "）");

                    // ---- 阻塞直到断开或取消 ----
                    WaitWhileConnected(bridge, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception e)
                {
                    if (token.IsCancellationRequested) break;
                    Debug.LogError("[Twin] MQTT 连接异常：" + e.Message);
                    SetState(DataSourceState.Degraded);
                }
                finally
                {
                    CleanupBridge(ref bridge);
                    _bridge = null;
                }

                if (token.IsCancellationRequested) break;
            }

            SetState(DataSourceState.Stopped);
        }

        /// <summary>连接期间的占位循环：只轮询连接状态，真正的收包在 MQTTnet 回调里。</summary>
        private static void WaitWhileConnected(IMqttBridge bridge, CancellationToken token)
        {
            while (!token.IsCancellationRequested && bridge.IsConnected)
            {
                if (token.WaitHandle.WaitOne(200)) break;
            }
        }

        private void CleanupBridge(ref IMqttBridge bridge)
        {
            if (bridge == null) return;
            try
            {
                bridge.MessageReceived -= OnMessageReceived;
                bridge.Close();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Twin] MQTT 关闭时出错（已忽略）：" + e.Message);
            }
            bridge = null;
        }

        private IMqttBridge CreateBridge()
        {
            return new MqttClientBridge();
        }

        protected override void OnStopped()
        {
            Interlocked.Exchange(ref _recvCount, 0);
        }

        // ================================================================ 报文 → TwinSample

        /// <summary>
        /// 由 MQTTnet 回调线程调用。**禁止在此调用 Unity API**（Sink.Emit 本身是线程安全的）。
        /// </summary>
        private void OnMessageReceived(string topic, byte[] payload)
        {
            if (payload == null || payload.Length == 0) return;

            int shown = _logFirstMessages;
            if (shown > 0 && _recvCount < shown)
            {
                Debug.Log("[Twin] MQTT ← " + topic + " : " + Encoding.UTF8.GetString(payload));
            }

            float value;
            double ts;
            string metricFromPayload;
            if (!TryParsePayload(payload, out value, out ts, out metricFromPayload))
            {
                Interlocked.Increment(ref _parseFailCount);
                LogBadMessage(topic, payload);
                return;
            }

            string twinId;
            string metric;
            if (!TryResolveIds(topic, metricFromPayload, out twinId, out metric))
            {
                Interlocked.Increment(ref _parseFailCount);
                LogBadMessage(topic, payload);
                return;
            }

            // 时效判定用**本机接收时刻**，而不是设备时间戳。
            // 原因：设备时钟可能没同步（差几分钟很常见），用它会算错“是否陈旧”。
            // 设备时间戳只用于记录/回溯，不参与实时性判断。
            double localNow = NowUtcSeconds();

            TwinSample sample = new TwinSample(twinId, metric, value, localNow, DataQuality.Good);
            Sink.Emit(in sample);
            Interlocked.Increment(ref _recvCount);

            // ts 与本地时间偏差过大时给一次提醒（不参与判定，仅提示时钟未同步）
            if (ts > 0d)
            {
                double skew = Math.Abs(localNow - ts);
                if (skew > 300d && _recvCount == 1)
                {
                    Debug.LogWarning("[Twin] 设备时间戳与本机相差 " + skew.ToString("0") +
                                     " 秒，疑似设备时钟未同步。实时性判定仍使用本机时刻。");
                }
            }
        }

        private bool TryParsePayload(byte[] payload, out float value, out double ts, out string metric)
        {
            value = 0f;
            ts = 0d;
            metric = null;

            if (_payloadFormat == PayloadFormat.PlainNumber)
            {
                string text = Encoding.UTF8.GetString(payload).Trim();
                return float.TryParse(text, System.Globalization.NumberStyles.Float,
                                      System.Globalization.CultureInfo.InvariantCulture, out value);
            }

            // ---- JSON ----
            // 用手写极小解析器而不是 Newtonsoft：本路径在高速报文下每帧可能被调用上千次，
            // 且只需要 3 个数值/字符串字段。手写解析零装箱、零反射、零额外分配压力。
            if (!MiniJson.TryParse(payload, out MiniJson.Object obj)) return false;

            if (!obj.TryGetNumber(_valueField, out value)) return false;

            double rawTs;
            if (!string.IsNullOrEmpty(_timestampField) && obj.TryGetNumber(_timestampField, out rawTs))
            {
                ts = NormalizeUnixTimestamp(rawTs);
            }

            string m;
            if (obj.TryGetString(_metricField, out m)) metric = m;
            return true;
        }

        /// <summary>把 10 位（秒）/13 位（毫秒）时间戳统一成秒。</summary>
        private static double NormalizeUnixTimestamp(double raw)
        {
            if (raw <= 0d) return 0d;
            // 1e11 秒 ≈ 公元 5138 年，超过它基本可判定传的是毫秒
            return raw > 1e11 ? raw / 1000d : raw;
        }

        /// <summary>
        /// 由主题与载荷共同确定 TwinId / Metric。
        ///
        /// 约定：topic = school/{Zone}/{Category}/{Point}/{Metric}     —— 共 5 段
        ///       例如 school/Class2-1/Sensor/Temp_01/Temperature
        ///       → TwinId = Class2-1/Sensor/Temp_01（去掉 school/ 前缀与最后的 Metric 段）
        ///       → Metric = Temperature
        ///
        /// 这与 TwinCatalog 里 “Class2-1/Sensor/Temp_01” 的点位 ID 天然对齐，
        /// 因此迁移期不需要逐条填 SourceTopic。
        ///
        /// 【重要约束】TwinId 本身含斜杠，所以“TwinId 占几段”由 _metricSegmentIndex 决定：
        ///   · _metricSegmentIndex = -1 → 用最后一段当 Metric，TwinId 取 [start, 末尾-1]
        ///     这要求所有点位的层级深度一致。若将来出现 Class2-1/Sensor/Temp_01/Sub_01
        ///     这种更深一层**且 Sub_01 是 TwinId 一部分**的点位，本推导会把 Sub_01 误当 Metric。
        ///     届时改法：把 _metricSegmentIndex 固定成具体层号（如 5），
        ///     或在点位表里逐条填 SourceTopic 走精确匹配。
        ///   · 因此本类的定位是“约定优先”的轻量映射，不是通用 MQTT 路由。
        /// </summary>
        private bool TryResolveIds(string topic, string metricFromPayload, out string twinId, out string metric)
        {
            twinId = null;
            metric = metricFromPayload;

            string[] segs = topic.Split('/');
            if (segs.Length == 0) return false;

            if (_deriveTwinIdFromTopic)
            {
                int start = Mathf.Clamp(_twinIdSegmentStart, 0, segs.Length - 1);
                int end = segs.Length;      // exclusive

                // 若指标名从主题取，则 TwinId 不含最后一段
                int metricIdx = _metricSegmentIndex;
                if (metricIdx < 0)
                {
                    // -1 = 自动模式：最后一段永远是 metric 段
                    metricIdx = segs.Length - 1;
                }

                if (metricIdx < start || metricIdx >= segs.Length) return false;
                // metric 优先用载荷里的，载荷没有才从主题取
                if (string.IsNullOrEmpty(metric))
                {
                    metric = segs[metricIdx];
                }
                end = metricIdx;

                if (end <= start) return false;

                var sb = new StringBuilder(48);
                for (int i = start; i < end; i++)
                {
                    if (i > start) sb.Append('/');
                    sb.Append(segs[i]);
                }
                twinId = sb.ToString();
            }

            if (string.IsNullOrEmpty(twinId) || string.IsNullOrEmpty(metric)) return false;
            twinId = twinId.Trim();
            metric = metric.Trim();
            return twinId.Length > 0 && metric.Length > 0;
        }

        private void LogBadMessage(string topic, byte[] payload)
        {
            if (_badMessageLogPerSecond <= 0) return;

            int second = (int)(DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond);
            if (Interlocked.Exchange(ref _lastBadLogSecond, second) == second) return;

            string preview = payload.Length > 160
                ? Encoding.UTF8.GetString(payload, 0, 160) + "…"
                : Encoding.UTF8.GetString(payload);
            Debug.LogWarning("[Twin] MQTT 报文无法映射到点位（累计 " + _parseFailCount + " 条）。topic=" +
                             topic + " payload=" + preview);
        }

        // ================================================================ 编辑器便利方法

        /// <summary>运行期改 broker 地址（重连后生效）。</summary>
        public void ConfigureEndpoint(string host, int port, string topicFilter)
        {
            if (!string.IsNullOrEmpty(host)) _host = host;
            if (port > 0) _port = port;
            if (!string.IsNullOrEmpty(topicFilter)) _topicFilter = topicFilter;
        }

        [ContextMenu("打印当前 MQTT 配置")]
        private void PrintConfig()
        {
            Debug.Log("[Twin] MQTT 配置：broker=" + _host + ":" + _port +
                      " clientId=" + (string.IsNullOrEmpty(_clientId) ? "(自动)" : _clientId) +
                      " topic=" + _topicFilter + " qos=" + _qos +
                      " payload=" + _payloadFormat + " 已收=" + _recvCount +
                      " 解析失败=" + _parseFailCount);
        }
    }
}

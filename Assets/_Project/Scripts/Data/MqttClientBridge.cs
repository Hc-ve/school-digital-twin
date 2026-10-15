using System;
using System.Text;
using System.Threading;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;

namespace SchoolTwin.Data
{
    /// <summary>
    /// MQTT 桥接接口。把“第三方 MQTT 库”挡在业务代码之外，
    /// 好处：① 解析逻辑可脱离 Unity 单测；② 将来换库（如 MQTTnet 5.x / 自研）
    /// 只改这一处实现，MqttDataSource 一行不动。
    /// </summary>
    public interface IMqttBridge
    {
        bool IsConnected { get; }
        event Action<string, byte[]> MessageReceived;

        /// <summary>连接 + 订阅。返回 true 表示已连上且订阅成功。阻塞直到完成。</summary>
        bool Connect(string host, int port, string clientId, string username, string password,
                     int timeoutSeconds, int keepAliveSeconds,
                     string topicFilter, int qos, CancellationToken token);

        /// <summary>
        /// 发布一条消息。主要用于本地报文模拟器（MqttLoopbackPublisher）。
        /// topicFilter 传空时表示“只连不订阅”（模拟器自己不需要订阅）。
        /// </summary>
        bool Publish(string topic, string payload, int qos);

        void Close();
    }

    /// <summary>
    /// 基于 MQTTnet 4.3.7 的实现。
    ///
    /// API 版本说明（踩坑记录，换版本时必读）：
    ///   MQTTnet 5.x 只提供 net8.0 / net10.0 构建，Unity 的 .NET Standard 2.1 用不了。
    ///   4.3.7.1207 是最后一个带 netstandard2.0 / 2.1 的版本，因此本工程锁死 4.3.7。
    ///
    /// 4.x 与 5.x 的 API 差异（本文件全部按 4.x 写）：
    ///   · 事件名带 Async 后缀：ApplicationMessageReceivedAsync（5.x 叫 ApplicationMessageReceived）
    ///   · 事件签名是 Func<..., Task>，必须返回 Task.CompletedTask
    ///   · Payload 是 byte[] / ArraySegment<byte>，不是 ReadOnlySequence
    ///   · MqttClientOptionsBuilder.Build() 返回 MqttClientOptions
    /// </summary>
    internal sealed class MqttClientBridge : IMqttBridge
    {
        private IMqttClient _client;
        private volatile bool _connected;

        public bool IsConnected { get { return _connected; } }

        public event Action<string, byte[]> MessageReceived;

        public bool Connect(string host, int port, string clientId, string username, string password,
                            int timeoutSeconds, int keepAliveSeconds,
                            string topicFilter, int qos, CancellationToken token)
        {
            MqttFactory factory = new MqttFactory();
            _client = factory.CreateMqttClient();

            _client.ApplicationMessageReceivedAsync += OnApplicationMessageReceivedAsync;
            _client.DisconnectedAsync += OnDisconnectedAsync;

            if (string.IsNullOrEmpty(clientId))
            {
                clientId = "school-twin-" + Guid.NewGuid().ToString("N").Substring(0, 12);
            }

            MqttClientOptionsBuilder builder = new MqttClientOptionsBuilder()
                .WithTcpServer(host, port)
                .WithClientId(clientId)
                .WithCleanSession(true)
                .WithKeepAlivePeriod(TimeSpan.FromSeconds(Math.Max(5, keepAliveSeconds)))
                .WithTimeout(TimeSpan.FromSeconds(Math.Max(2, timeoutSeconds)));

            if (!string.IsNullOrEmpty(username))
            {
                builder = builder.WithCredentials(username, password);
            }

            MqttClientOptions options = builder.Build();

            // ---------------------------------------------------------------
            // 同步等待异步：GetAwaiter().GetResult()
            //
            // 安全性依据（见 P2 文档 4.2 节）：本方法运行在 DataSourceBase 起的
            // 独立后台线程上，该线程没有 SynchronizationContext，
            // 不存在“UI 线程等后台任务、后台任务等 UI 线程”的互等死锁条件。
            //
            // 为什么不用 .Result / .Wait()：.Result 会把异常包成 AggregateException，
            // 报错信息难读；GetAwaiter().GetResult() 抛出原始异常。
            // ---------------------------------------------------------------
            MqttClientConnectResult result = _client.ConnectAsync(options, token)
                                                    .GetAwaiter().GetResult();

            if (result == null)
            {
                UnityEngine.Debug.LogError("[Twin] MQTT ConnectAsync 返回 null");
                return false;
            }

            if (result.ResultCode != MqttClientConnectResultCode.Success)
            {
                UnityEngine.Debug.LogError("[Twin] MQTT 连接被拒绝：" + result.ResultCode +
                                           "（常见原因：用户名/密码错、broker 未启动、端口不对）");
                return false;
            }

            _connected = true;

            // ---- 订阅 ----
            // topicFilter 为空 = 只连不订阅（本地模拟器只需要连上后发消息）
            if (string.IsNullOrEmpty(topicFilter)) return true;

            MqttClientSubscribeOptions subOptions = factory.CreateSubscribeOptionsBuilder()
                .WithTopicFilter(b => b.WithTopic(topicFilter)
                                       .WithQualityOfServiceLevel(ToQos(qos)))
                .Build();

            MqttClientSubscribeResult subResult = _client.SubscribeAsync(subOptions, token)
                                                         .GetAwaiter().GetResult();

            if (subResult != null && subResult.Items != null)
            {
                for (int i = 0; i < subResult.Items.Count; i++)
                {
                    MqttClientSubscribeResultItem item = subResult.Items[i];
                    if (item == null) continue;
                    // 4.x：订阅失败体现在 ResultCode 上，不会抛异常，必须显式检查
                    if ((int)item.ResultCode >= 128)
                    {
                        UnityEngine.Debug.LogError("[Twin] MQTT 订阅被拒绝：" +
                                                   item.TopicFilter + " -> " + item.ResultCode);
                        return false;
                    }
                }
            }

            return true;
        }

        private static MqttQualityOfServiceLevel ToQos(int qos)
        {
            switch (qos)
            {
                case 0: return MqttQualityOfServiceLevel.AtMostOnce;
                case 2: return MqttQualityOfServiceLevel.ExactlyOnce;
                default: return MqttQualityOfServiceLevel.AtLeastOnce;
            }
        }

        /// <summary>
        /// 4.x 事件签名是 Func<MqttApplicationMessageReceivedEventArgs, Task>，
        /// 必须返回 Task（不能是 void），否则编译不过。
        /// </summary>
        private System.Threading.Tasks.Task OnApplicationMessageReceivedAsync(
            MqttApplicationMessageReceivedEventArgs e)
        {
            try
            {
                if (e == null || e.ApplicationMessage == null) return System.Threading.Tasks.Task.CompletedTask;

                string topic = e.ApplicationMessage.Topic;
                byte[] payload = ToByteArray(e.ApplicationMessage);

                if (payload != null && payload.Length > 0 && MessageReceived != null)
                {
                    MessageReceived(topic, payload);
                }
            }
            catch (Exception ex)
            {
                // 回调里抛异常会污染 MQTTnet 内部循环，必须在这里兜住
                UnityEngine.Debug.LogError("[Twin] MQTT 回调处理异常：" + ex.Message);
            }
            return System.Threading.Tasks.Task.CompletedTask;
        }

        private System.Threading.Tasks.Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs e)
        {
            _connected = false;
            if (e != null && e.ClientWasConnected == false)
            {
                // 从未连上过：属于连接阶段失败，由 Connect 的返回值处理，这里不重复报错
                return System.Threading.Tasks.Task.CompletedTask;
            }
            // 连上后掉线：RunWorker 的轮询会发现 IsConnected=false 并触发重连
            UnityEngine.Debug.LogWarning("[Twin] MQTT 连接已断开，将自动重连");
            return System.Threading.Tasks.Task.CompletedTask;
        }

        /// <summary>
        /// 4.x 里 ApplicationMessage.Payload 是 byte[]；某些构建下是 ArraySegment&lt;byte&gt;。
        /// 这里用 PayloadSegment 做兼容，避免版本差异导致取不到数据。
        /// </summary>
        private static byte[] ToByteArray(MqttApplicationMessage msg)
        {
            ArraySegment<byte> seg = msg.PayloadSegment;
            if (seg.Array != null && seg.Count > 0)
            {
                byte[] copy = new byte[seg.Count];
                Buffer.BlockCopy(seg.Array, seg.Offset, copy, 0, seg.Count);
                return copy;
            }
            return null;
        }

        public void Close()
        {
            _connected = false;
            if (_client == null) return;

            try
            {
                if (_client.IsConnected)
                {
                    // 注意：4.3.7 的 DisconnectAsync 签名是
                    //   DisconnectAsync(MqttClientDisconnectOptions options, CancellationToken ct = default)
                    // options 没有默认值，且传 null 会抛 ArgumentNullException（见 MqttClient.cs L191）。
                    // 所以必须显式构造一个 options —— 这是 4.x 与 5.x 的常见差异点。
                    MqttClientDisconnectOptions disconnectOptions = new MqttClientDisconnectOptionsBuilder()
                        .WithReason(MqttClientDisconnectOptionsReason.NormalDisconnection)
                        .Build();

                    _client.DisconnectAsync(disconnectOptions).GetAwaiter().GetResult();
                }
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning("[Twin] MQTT 断开时异常（已忽略）：" + e.Message);
            }

            try
            {
                _client.ApplicationMessageReceivedAsync -= OnApplicationMessageReceivedAsync;
                _client.DisconnectedAsync -= OnDisconnectedAsync;
                _client.Dispose();
            }
            catch (Exception) { }
            _client = null;
        }

        /// <summary>
        /// 发布一条消息。用 MqttApplicationMessageBuilder 构造（4.3.7 的正确姿势）。
        /// Payload 用 byte[]，MQTTnet 内部会包装成 ArraySegment。
        /// </summary>
        public bool Publish(string topic, string payload, int qos)
        {
            if (_client == null || !_connected) return false;
            if (string.IsNullOrEmpty(topic)) return false;

            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(payload ?? string.Empty);

                MqttApplicationMessage message = new MqttApplicationMessageBuilder()
                    .WithTopic(topic)
                    .WithPayload(bytes)
                    .WithQualityOfServiceLevel(ToQos(qos))
                    .Build();

                _client.PublishAsync(message).GetAwaiter().GetResult();
                return true;
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning("[Twin] MQTT 发布失败：" + topic + " —— " + e.Message);
                return false;
            }
        }
    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using SchoolTwin.Data;

namespace SchoolTwin.Command
{
    /// <summary>
    /// 命令传输层。**把「命令怎么送到设备」这件事挡在业务代码之外。**
    ///
    /// 【为什么需要抽象这一层】
    /// 协议契约（第 9 章）规定的是「主题 + 载荷」，不是「走哪个进程」。
    /// Mock 模式下一台虚拟设备就在本进程里，为了它去连一个 broker，会让
    /// 「不装 mosquitto 就跑不起来」—— 而这台机器现在没有 broker 也能演示。
    /// 所以：
    ///   · Mock 模式 → 进程内传输（主题照样是 twin/cmd/...，只是不经过网络）
    ///   · MQTT 模式 → 真发到 broker
    /// 两条路对上层是同一个接口，切数据源时命令链路不用改一行。
    ///
    /// 【为什么 Send 带回调而不是返回 bool】
    /// MQTT 发布在后台线程，结果是异步的。上层要的是「发成功了没有」，
    /// 而这个答案可能 200ms 后才回来。回调统一由上层 marshal 回主线程。
    /// </summary>
    public interface ICommandTransport
    {
        string Name { get; }

        // 【为什么这里没有 MessageReceived】
        // 两种传输的「收」形状根本不同：进程内是「按主题前缀订阅」，MQTT 是「连上之后推事件」。
        // 硬塞一个共同的 event，只会让 LocalCommandTransport 声明一个永远不会被 raise 的事件
        // （编译器报 CS0067），而且哪天有人两边都订阅了，同一条 ack 会被处理两遍。
        // 所以接口只统一「发」，收各用各的：LocalCommandTransport.Subscribe / MqttCommandTransport.MessageReceived。

        /// <summary>发布一条消息。返回 false = 传输层直接拒绝了（连队列都进不去）。</summary>
        bool Send(string topic, string payload, Action<bool, string> onDone);

        void Shutdown();
    }

    /// <summary>
    /// 进程内传输：按主题前缀分发给本进程里的订阅者。
    ///
    /// 【它是不是「假的」】不是。主题、载荷、幂等键、回执、超时全是真的，
    /// 只是「网络」这一跳换成了函数调用 —— 等价于 broker 和客户端在同一台机器上。
    /// 接真实设备时换成 <see cref="MqttCommandTransport"/>，上层一行不改。
    /// </summary>
    public sealed class LocalCommandTransport : ICommandTransport
    {
        private sealed class Subscriber
        {
            public string Prefix;
            public Action<string, string> Handler;
        }

        private static LocalCommandTransport _instance;

        public static LocalCommandTransport Instance
        {
            get
            {
                if (_instance == null) _instance = new LocalCommandTransport();
                return _instance;
            }
        }

        private readonly List<Subscriber> _subs = new List<Subscriber>(4);
        private readonly List<Subscriber> _dispatchBuf = new List<Subscriber>(4);

        public string Name { get { return "进程内"; } }

        public void Subscribe(string topicPrefix, Action<string, string> handler)
        {
            if (string.IsNullOrEmpty(topicPrefix) || handler == null) return;
            for (int i = 0; i < _subs.Count; i++)
            {
                if (_subs[i].Handler == handler && string.Equals(_subs[i].Prefix, topicPrefix, StringComparison.Ordinal))
                    return;
            }
            Subscriber s = new Subscriber();
            s.Prefix = topicPrefix;
            s.Handler = handler;
            _subs.Add(s);
        }

        public void Unsubscribe(Action<string, string> handler)
        {
            if (handler == null) return;
            for (int i = _subs.Count - 1; i >= 0; i--)
            {
                if (_subs[i].Handler == handler) _subs.RemoveAt(i);
            }
        }

        public bool Send(string topic, string payload, Action<bool, string> onDone)
        {
            if (string.IsNullOrEmpty(topic)) return false;

            // 【为什么要先复制一份】订阅者在回调里可能立刻发下一条消息（ack → 状态变化），
            // 直接遍历 _subs 会在「遍历中修改集合」上抛异常。
            _dispatchBuf.Clear();
            _dispatchBuf.AddRange(_subs);

            int matched = 0;
            for (int i = 0; i < _dispatchBuf.Count; i++)
            {
                Subscriber s = _dispatchBuf[i];
                if (s.Handler == null) continue;
                if (topic.StartsWith(s.Prefix, StringComparison.Ordinal))
                {
                    matched++;
                    try { s.Handler(topic, payload); }
                    catch (Exception e) { Debug.LogError("[TwinCmd] 本地订阅者抛异常：" + e); }
                }
            }

            // 【为什么这里要报警】进程内传输的唯一职责就是「交给本进程里的订阅者」。
            // 一个订阅者都没匹配上，说明设备代理没在监听 —— 这是**本机配置/初始化出错**，
            // 不是「设备离线」。不报的话上层只能等 3 秒 ack 超时，然后报「设备无响应」，
            // 那句话会把排查方向引到网络上去（P12 实测被这个引偏了一轮）。
            if (matched == 0)
            {
                Debug.LogWarning("[TwinCmd] 进程内传输：主题 " + topic + " 没有任何订阅者。" +
                                 "MockDeviceAgent 没在监听 twin/cmd/ 前缀 —— 命令不会有人应答。");
            }

            // 进程内投递不会失败。没有订阅者时也不在发送端假装失败 ——
            // 失败由 ack 超时判定，这里只负责把「没人听」这件事说清楚。
            if (onDone != null) onDone(true, null);
            return true;
        }

        public void Shutdown()
        {
            _subs.Clear();
        }
    }

    /// <summary>
    /// MQTT 传输：后台线程持有一条只发命令 / 收回执的连接。
    ///
    /// 【为什么必须用后台线程】`MqttClientBridge.Publish` 内部是
    /// `PublishAsync(...).GetAwaiter().GetResult()`，连接异常时会阻塞到超时（2~5 秒）。
    /// 在主线程调用就等于点一下按钮卡几秒 —— 而 P12 的验收项里就有
    /// 「切到 MQTT 源，命令链路不报错」，那种场景下连接必然失败，正好会踩到这一点。
    ///
    /// 【P12 的范围】只实现「发命令 + 收回执」。真实设备的回执订阅是同一个连接做的，
    /// 但本机没有真设备，所以 MQTT 模式下的预期行为就是**全部超时失败**。
    /// </summary>
    public sealed class MqttCommandTransport : ICommandTransport
    {
        private struct Outbound
        {
            public string Topic;
            public string Payload;
            public Action<bool, string> OnDone;
        }

        private readonly IMqttBridge _bridge = new MqttClientBridge();
        private readonly ConcurrentQueue<Outbound> _queue = new ConcurrentQueue<Outbound>();
        private readonly AutoResetEvent _signal = new AutoResetEvent(false);

        private Thread _worker;
        private volatile bool _stop;
        private volatile bool _connected;

        private string _host = "127.0.0.1";
        private int _port = 1883;
        private string _clientId = "";
        private string _ackFilter = "twin/ack/#";
        private string _username = "";
        private string _password = "";

        public string Name { get { return "MQTT"; } }

        public event Action<string, string> MessageReceived;

        public void Start(string host, int port, string clientId, string username, string password)
        {
            _host = string.IsNullOrEmpty(host) ? "127.0.0.1" : host;
            _port = port > 0 ? port : 1883;
            _clientId = string.IsNullOrEmpty(clientId) ? "school-twin-cmd-" + Guid.NewGuid().ToString("N").Substring(0, 8) : clientId;
            _username = username;
            _password = password;

            if (_worker != null) return;
            _stop = false;
            _worker = new Thread(WorkerEntry);
            _worker.IsBackground = true;
            _worker.Name = "TwinCmdTransport";
            _worker.Start();
        }

        public bool Send(string topic, string payload, Action<bool, string> onDone)
        {
            if (_stop) return false;
            Outbound o;
            o.Topic = topic;
            o.Payload = payload;
            o.OnDone = onDone;
            _queue.Enqueue(o);
            _signal.Set();
            return true;
        }

        private void WorkerEntry()
        {
            try
            {
                while (!_stop)
                {
                    _signal.WaitOne(200);
                    if (_stop) break;

                    Outbound o;
                    while (_queue.TryDequeue(out o))
                    {
                        if (!EnsureConnected())
                        {
                            if (o.OnDone != null) o.OnDone(false, "连不上 MQTT broker");
                            continue;
                        }
                        bool ok = _bridge.Publish(o.Topic, o.Payload, 1);
                        if (o.OnDone != null) o.OnDone(ok, ok ? null : "MQTT 发布失败");
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[TwinCmd] MQTT 传输线程异常：" + e);
            }
        }

        private DateTime _lastAttemptUtc = DateTime.MinValue;

        private bool EnsureConnected()
        {
            if (_connected && _bridge.IsConnected) return true;

            // 【为什么要退避】连接失败一次要等 3 秒超时。如果每条命令都重试，
            // 队列会越积越长，用户看到的是「点了没反应」。5 秒内只试一次。
            if ((DateTime.UtcNow - _lastAttemptUtc).TotalSeconds < 5.0) return false;
            _lastAttemptUtc = DateTime.UtcNow;

            try
            {
                _bridge.MessageReceived -= OnBridgeMessage;
                _bridge.MessageReceived += OnBridgeMessage;

                // 空 topicFilter = 只连不订阅；这里要收回执，所以订阅 twin/ack/#
                bool ok = _bridge.Connect(_host, _port, _clientId, _username, _password,
                                          3, 30, _ackFilter, 1, CancellationToken.None);
                _connected = ok;
                if (!ok) Debug.LogWarning("[TwinCmd] MQTT 命令通道连接失败（" + _host + ":" + _port +
                                          "）。MQTT 模式下命令会全部超时失败 —— 本机没有真设备时这是正确行为。");
                return ok;
            }
            catch (Exception e)
            {
                _connected = false;
                Debug.LogWarning("[TwinCmd] MQTT 命令通道连接异常：" + e.Message);
                return false;
            }
        }

        private void OnBridgeMessage(string topic, byte[] payload)
        {
            Action<string, string> handler = MessageReceived;
            if (handler == null || payload == null || payload.Length == 0) return;
            string text = System.Text.Encoding.UTF8.GetString(payload);
            try { handler(topic, text); }
            catch (Exception e) { Debug.LogError("[TwinCmd] 回执处理异常：" + e.Message); }
        }

        public void Shutdown()
        {
            _stop = true;
            _signal.Set();

            try
            {
                if (_worker != null && _worker.IsAlive) _worker.Join(300);
            }
            catch (Exception) { }
            _worker = null;

            try
            {
                _bridge.MessageReceived -= OnBridgeMessage;
                _bridge.Close();
            }
            catch (Exception) { }
            _connected = false;

            Outbound o;
            while (_queue.TryDequeue(out o))
            {
                if (o.OnDone != null) o.OnDone(false, "传输通道已关闭");
            }
        }
    }
}

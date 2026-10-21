using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using SchoolTwin.Core;
using SchoolTwin.Data;
using SchoolTwin.Twin;

namespace SchoolTwin.Command
{
    /// <summary>
    /// 命令中心：整个项目里**唯一**能把控制意图变成动作的地方。
    ///
    /// 职责（ISO 23247 里它是 DCDE 的一部分：数据与控制的交汇点）：
    ///   1. 校验（设备是否可控 / 动作是否支持 / 参数是否越界 / 是否已有命令在飞）
    ///   2. 下发（Pending）
    ///   3. 收 ack（Accepted → Actuated）
    ///   4. **回到数据链路做回读比对**（Actuated → Confirmed）
    ///   5. 分段超时 → Failed + 人话原因
    ///   6. 审计日志
    ///
    /// 【最重要的一条】`Confirmed` 的唯一判据是「数据链路上读到一条比发送时刻更新的样本，
    /// 且值落在期望区间内」。**绝不允许**由 ack 直接跳到 Confirmed。
    /// 这是「系统」和「动画」的分界线 —— 少了这一步，按钮变绿只是因为它被点过。
    ///
    /// 三维层不许调用这里（Spec §6 铁律 5）：点设备只产生选中，控制只能从面板发起。
    /// </summary>
    [AddComponentMenu("SchoolTwin/命令中心 (TwinCommandCenter)")]
    public sealed class TwinCommandCenter : MonoBehaviour
    {
        public static TwinCommandCenter Instance { get; private set; }

        [Header("分段超时（秒）")]
        [Tooltip("1 秒内没发到传输层 → 失败")]
        [SerializeField] private float _publishTimeout = 1f;
        [Tooltip("3 秒内没收到「已接受」回执 → 设备无响应")]
        [SerializeField] private float _ackTimeout = 3f;
        [Tooltip("收到「已接受」后再等这么久没收到「已执行」→ 设备执行失败")]
        [SerializeField] private float _actuatedTimeout = 3f;
        [Tooltip("「已执行」后 5 秒内回读不到期望值 → 执行了但状态没变")]
        [SerializeField] private float _readbackTimeout = 5f;

        [Header("失败 / 成功提示的保持时长")]
        [SerializeField] private float _failHoldSeconds = 3f;
        [SerializeField] private float _confirmHoldSeconds = 1.2f;

        [Tooltip("命令超时后，面板把该设备标成「无响应」保持多久（秒）")]
        [SerializeField] private float _unreachableHoldSeconds = 20f;

        [Header("MQTT 命令通道")]
        [SerializeField] private string _mqttHost = "127.0.0.1";
        [SerializeField] private int _mqttPort = 1883;
        [SerializeField] private string _mqttUser = "";
        [SerializeField] private string _mqttPassword = "";

        [Tooltip("P12 恒为 false。结构位留着：真实部署时 twin/cmd/** 必须走 TLS 且写受限（Spec §9.6）")]
        [SerializeField] private bool _useTls = false;

        [Tooltip("操作者标识（权限位预留）。P12 固定 local-user")]
        [SerializeField] private string _principal = "local-user";

        /// <summary>命令状态变化（UI 用，主线程）。</summary>
        public event Action<TwinCommand> Changed;

        public TwinCommandLog Log { get { return _log; } }
        public string Principal { get { return _principal; } }
        public bool UseTls { get { return _useTls; } }

        /// <summary>当前数据源。控制区据此提示「MQTT 模式下没有真设备」。</summary>
        public TwinSourceSwitcher.SourceKind CurrentSource { get { return CurrentSourceKind(); } }

        private readonly Dictionary<string, TwinCommand> _inFlight =
            new Dictionary<string, TwinCommand>(8, StringComparer.Ordinal);
        private readonly Dictionary<string, TwinCommand> _byId =
            new Dictionary<string, TwinCommand>(8, StringComparer.Ordinal);
        private readonly Dictionary<string, TwinCommand> _last =
            new Dictionary<string, TwinCommand>(8, StringComparer.Ordinal);
        private readonly List<TwinCommand> _tickBuf = new List<TwinCommand>(8);
        private readonly ConcurrentQueue<Action> _mainThread = new ConcurrentQueue<Action>();

        private TwinCommandLog _log;
        private TwinRuntime _runtime;
        private TwinSourceSwitcher _switcher;
        private LocalCommandTransport _local;
        private MqttCommandTransport _mqtt;
        private Action<string, string> _localAckHandler;
        private int _seq;
        private float _nextTick;

        // ---------------------------------------------------------------- 生命周期

        /// <summary>拿到实例；场景里没有就挂在 TwinRuntime 上。调用方永远不需要判空。</summary>
        public static TwinCommandCenter Ensure()
        {
            if (Instance != null) return Instance;

            TwinRuntime runtime = TwinRuntime.Ensure();
            TwinCommandCenter found = runtime.GetComponent<TwinCommandCenter>();
            if (found == null) found = runtime.gameObject.AddComponent<TwinCommandCenter>();
            return found;
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning("[TwinCmd] 场景中存在多个 TwinCommandCenter，已销毁重复的：" + name);
                Destroy(this);
                return;
            }

            Instance = this;
            _log = new TwinCommandLog();
            _runtime = TwinRuntime.Instance;

            // Mock 模式：命令与回执都走进程内总线。主题仍然是 twin/cmd/**、twin/ack/**，
            // 只是「网络」这一跳换成了函数调用。
            _local = LocalCommandTransport.Instance;
            _localAckHandler = OnLocalMessage;
            _local.Subscribe("twin/ack/", _localAckHandler);

            // 协议的另一端。它自己会订阅 twin/cmd/**，所以「有命令中心 = 有设备在听」。
            // MQTT 模式下命令走 broker，这个本地设备听不到，也不该听到。
            MockDeviceAgent.Ensure();
        }

        private void OnDestroy()
        {
            if (_local != null && _localAckHandler != null) _local.Unsubscribe(_localAckHandler);
            if (_mqtt != null)
            {
                _mqtt.MessageReceived -= OnMqttMessage;
                _mqtt.Shutdown();
                _mqtt = null;
            }
            _inFlight.Clear();
            _byId.Clear();
            if (Instance == this) Instance = null;
        }

        // ---------------------------------------------------------------- 查询（UI 用）

        /// <summary>某点位当前的命令状态。没有命令在飞时，成功/失败提示会在保持期后回到 Idle。</summary>
        public CommandState StateOf(string twinId)
        {
            TwinCommand c;
            if (_inFlight.TryGetValue(twinId, out c)) return c.State;

            if (_last.TryGetValue(twinId, out c))
            {
                double age = TwinClock.NowUtcSeconds() - c.CompletedAtUtc;
                if (c.State == CommandState.Failed && age < _failHoldSeconds) return CommandState.Failed;
                if (c.State == CommandState.Confirmed && age < _confirmHoldSeconds) return CommandState.Confirmed;
            }
            return CommandState.Idle;
        }

        /// <summary>最近一条命令（在飞优先）。UI 用它取失败原因。</summary>
        public TwinCommand Current(string twinId)
        {
            TwinCommand c;
            if (_inFlight.TryGetValue(twinId, out c)) return c;
            if (_last.TryGetValue(twinId, out c)) return c;
            return null;
        }

        public bool IsBusy(string twinId)
        {
            return _inFlight.ContainsKey(twinId);
        }

        // ---------------------------------------------------------------- 下发

        /// <summary>
        /// 下发一条命令。返回 false 表示**本机就拒绝了**（不会产生任何命令对象），
        /// 拒绝原因在 reject 里，可以直接显示给用户。
        /// </summary>
        public bool TrySend(string twinId, CommandAction action, float value, out string reject)
        {
            reject = null;

            if (string.IsNullOrEmpty(twinId)) { reject = "点位为空"; return false; }

            TwinSourceSwitcher.SourceKind kind = CurrentSourceKind();
            if (kind == TwinSourceSwitcher.SourceKind.Playback)
            {
                // 回放是历史，不许产生新意图。
                reject = CommandText.FailReason(CommandFailCode.PlaybackBlocked);
                return false;
            }

            TwinAnchor anchor;
            TwinDeviceModel model;
            if (!ResolveDevice(twinId, out anchor, out model))
            {
                reject = CommandText.FailReason(CommandFailCode.NotSupported);
                return false;
            }

            // ---- 断电拦截（P15）----
            //
            // 【为什么必须在这里挡，而不是等 ack 超时】断电的设备不会回任何东西，
            // 不挡的话用户要等 3 秒才看到「设备无响应」—— 而真实原因就摆在面板上：
            // 这一层楼断电了。等 3 秒还给出错误原因，是拿用户的时间换一句不准确的话。
            //
            // 走「本机拒绝」而不是生成一条 Failed 命令：断电期间**根本不该产生意图**，
            // 产生一条失败记录反而会污染审计日志（看起来像有人试图操作一台不存在的设备）。
            if (!TwinCircuit.IsPowered(twinId))
            {
                reject = CommandText.FailReason(CommandFailCode.PowerOff);
                return false;
            }

            string actionName = CommandText.ActionName(action);
            TwinActionDefinition def = model.FindAction(actionName);
            if (def == null)
            {
                reject = CommandText.FailReason(CommandFailCode.NotSupported);
                return false;
            }

            if (value < def.Min - 0.001f || value > def.Max + 0.001f)
            {
                reject = CommandText.FailReason(CommandFailCode.OutOfRange);
                return false;
            }

            // 串行：同一设备上一条没结束，下一条直接拒绝（不是排队 —— 排队会让「点了三次」变成三次执行）
            if (_inFlight.ContainsKey(twinId))
            {
                reject = "上一条命令还没结束";
                return false;
            }

            string metric;
            float lo, hi;
            if (!TwinDeviceModels.TryBuildExpectation(model, actionName, value, out metric, out lo, out hi))
            {
                reject = "该动作没有回读判据，无法确认执行结果";
                return false;
            }

            if (!TwinControlScope.IsEnabled(twinId))
            {
                reject = "该设备暂未接入控制";
                return false;
            }

            double now = TwinClock.NowUtcSeconds();

            TwinCommand cmd = new TwinCommand();
            cmd.Id = "c-" + (++_seq).ToString("D6");
            cmd.TwinId = twinId;
            cmd.Action = action;
            cmd.Value = value;
            cmd.Priority = 8;
            cmd.SentAtUtc = now;
            cmd.StateSinceUtc = now;
            cmd.State = CommandState.Pending;
            cmd.ExpectMetric = metric;
            cmd.ExpectMin = lo;
            cmd.ExpectMax = hi;

            _inFlight[twinId] = cmd;
            _byId[cmd.Id] = cmd;

            ICommandTransport transport = kind == TwinSourceSwitcher.SourceKind.Mqtt
                ? (ICommandTransport)MqttTransport
                : _local;

            string topic = "twin/cmd/" + twinId + "/" + actionName;
            string payload = BuildCommandJson(cmd, actionName);

            // 一行说明「这条命令走了哪条路」。
            // 【为什么值得占一行日志】命令失败最常见的原因不是设备不回，而是**走错了通道**
            // （数据源是 A、命令发到 B）。没有这一行的话，排查只能从「设备无响应」倒推，
            // 而那句话听起来像网络问题，实际可能是本机路由选错了。
            Debug.Log("[TwinCmd] 下发 " + cmd.Id + "  " + twinId + "  " + actionName +
                      "(" + value.ToString("0.###", CultureInfo.InvariantCulture) + ")  →  " +
                      transport.Name + "（数据源 " + TwinSourceSwitcher.ShortName(kind) + "）");

            bool queued = transport.Send(topic, payload, delegate (bool ok, string err)
            {
                EnqueueMain(delegate { OnPublishDone(cmd, ok, err); });
            });

            if (!queued) Fail(cmd, CommandFailCode.SendFailed, "传输通道不可用");

            RaiseChanged(cmd);
            return true;
        }

        // ---------------------------------------------------------------- 主循环

        private void Update()
        {
            Action a;
            while (_mainThread.TryDequeue(out a))
            {
                try { a(); }
                catch (Exception e) { Debug.LogError("[TwinCmd] 主线程回调异常：" + e); }
            }

            if (Time.unscaledTime < _nextTick) return;
            _nextTick = Time.unscaledTime + 0.1f;

            double now = TwinClock.NowUtcSeconds();

            _tickBuf.Clear();
            foreach (KeyValuePair<string, TwinCommand> kv in _inFlight) _tickBuf.Add(kv.Value);
            for (int i = 0; i < _tickBuf.Count; i++) TickOne(_tickBuf[i], now);
        }

        private void TickOne(TwinCommand c, double now)
        {
            switch (c.State)
            {
                case CommandState.Pending:
                    if (!c.Published && now - c.SentAtUtc > _publishTimeout)
                    {
                        Fail(c, CommandFailCode.SendFailed, "命令没能发出去");
                        return;
                    }
                    if (now - c.SentAtUtc > _ackTimeout)
                    {
                        // 注意：这里说的是「无响应」而不是「执行失败」—— 我们确实不知道设备是没收到、
                        // 还是收到了但没回。把不确定性如实说出来，比编一个原因好。
                        //
                        // 【P15：用心跳把不确定性收窄一格】心跳是设备自己说"我在"。
                        //   · 心跳说不在 → 命令不可能送到，原因就是"设备不在"
                        //   · 心跳说在   → 命令可能没送到，也可能是设备卡住了
                        // 这两句话给运维的下一步动作完全不同（去现场 vs 查网络），
                        // 而只靠沉默判定这两件事永远分不开。
                        bool online = TwinPowerController.IsOnline(c.TwinId);
                        TwinCircuit.MarkUnreachable(c.TwinId, _unreachableHoldSeconds);
                        Fail(c, CommandFailCode.Offline,
                             online ? "设备在线但没回执（命令可能没送到）" : "设备不在线（心跳已断）");
                    }
                    break;

                case CommandState.Accepted:
                    if (now - c.StateSinceUtc > _actuatedTimeout)
                        Fail(c, CommandFailCode.ExecFailed, "设备接受了命令但没回报执行完成");
                    break;

                case CommandState.Actuated:
                    if (TryReadback(c)) { Confirm(c); return; }
                    if (now - c.StateSinceUtc > _readbackTimeout)
                    {
                        Fail(c, c.SawChange ? CommandFailCode.Mismatch : CommandFailCode.NoEffect, null);
                    }
                    break;
            }
        }

        /// <summary>
        /// 回读比对：唯一能让命令变成 Confirmed 的地方。
        ///
        /// 两个条件缺一不可：
        ///   ① 样本时间戳**晚于**命令发出时刻 —— 否则读到的是命令之前的旧值，
        ///      会出现「刚发命令就立刻确认」的假成功（总线上排队的数据比命令还早产生）。
        ///   ② 值落在期望区间内。
        /// </summary>
        private bool TryReadback(TwinCommand c)
        {
            if (_runtime == null) _runtime = TwinRuntime.Instance;
            if (_runtime == null || _runtime.Registry == null) return false;

            TwinAnchor anchor;
            if (!_runtime.Registry.TryGet(c.TwinId, out anchor) || anchor == null) return false;

            TwinValue v;
            if (!anchor.TryGetValue(c.ExpectMetric, out v)) return false;
            if (v.TimestampUtc <= c.SentAtUtc) return false;

            if (v.Value < c.ExpectMin || v.Value > c.ExpectMax)
            {
                c.SawChange = true;
                return false;
            }
            return true;
        }

        // ---------------------------------------------------------------- 回执

        private void OnLocalMessage(string topic, string payload)
        {
            ParseAck(payload);
        }

        private void OnMqttMessage(string topic, string payload)
        {
            // 来自 MQTTnet 的线程 → 必须 marshal 回主线程再改状态
            EnqueueMain(delegate { ParseAck(payload); });
        }

        private void ParseAck(string payload)
        {
            if (string.IsNullOrEmpty(payload)) return;

            MiniJson.Object o;
            if (!MiniJson.TryParse(Encoding.UTF8.GetBytes(payload), out o))
            {
                Debug.LogWarning("[TwinCmd] 回执不是合法 JSON，已忽略：" + payload);
                return;
            }

            string id;
            if (!o.TryGetString("id", out id) || string.IsNullOrEmpty(id)) return;

            TwinCommand cmd;
            if (!_byId.TryGetValue(id, out cmd)) return;   // 迟到 / 未知回执：静默忽略

            bool ok = true;
            o.TryGetBool("ok", out ok);

            int code = 0;
            o.TryGetInt("code", out code);

            string msg;
            o.TryGetString("msg", out msg);

            string phase;
            o.TryGetString("phase", out phase);

            if (!ok)
            {
                Fail(cmd, (CommandFailCode)code, msg);
                return;
            }

            bool actuated = string.Equals(phase, "actuated", StringComparison.OrdinalIgnoreCase);
            if (actuated)
            {
                if (cmd.State == CommandState.Pending || cmd.State == CommandState.Accepted)
                    SetState(cmd, CommandState.Actuated);
            }
            else
            {
                if (cmd.State == CommandState.Pending) SetState(cmd, CommandState.Accepted);
            }
        }

        // ---------------------------------------------------------------- 状态迁移

        private void OnPublishDone(TwinCommand cmd, bool ok, string error)
        {
            if (cmd == null || cmd.State != CommandState.Pending) return;

            if (!ok)
            {
                Fail(cmd, CommandFailCode.SendFailed, string.IsNullOrEmpty(error) ? null : error);
                return;
            }
            cmd.Published = true;
        }

        private void SetState(TwinCommand cmd, CommandState state)
        {
            if (cmd.State == state) return;
            cmd.State = state;
            cmd.StateSinceUtc = TwinClock.NowUtcSeconds();
            RaiseChanged(cmd);
        }

        private void Confirm(TwinCommand cmd)
        {
            cmd.State = CommandState.Confirmed;
            cmd.Code = CommandFailCode.None;
            cmd.Reason = "";
            cmd.CompletedAtUtc = TwinClock.NowUtcSeconds();
            cmd.StateSinceUtc = cmd.CompletedAtUtc;
            Complete(cmd);
        }

        private void Fail(TwinCommand cmd, CommandFailCode code, string reason)
        {
            if (cmd == null || cmd.IsTerminal) return;

            cmd.State = CommandState.Failed;
            cmd.Code = code;
            cmd.Reason = string.IsNullOrEmpty(reason) ? CommandText.FailReason(code) : reason;
            cmd.CompletedAtUtc = TwinClock.NowUtcSeconds();
            cmd.StateSinceUtc = cmd.CompletedAtUtc;
            Complete(cmd);
        }

        private void Complete(TwinCommand cmd)
        {
            _inFlight.Remove(cmd.TwinId);
            _byId.Remove(cmd.Id);
            _last[cmd.TwinId] = cmd;

            if (_log != null) _log.Add(cmd, _principal);
            RaiseChanged(cmd);
        }

        private void RaiseChanged(TwinCommand cmd)
        {
            Action<TwinCommand> handler = Changed;
            if (handler == null) return;
            try { handler(cmd); }
            catch (Exception e) { Debug.LogError("[TwinCmd] Changed 订阅者异常：" + e); }
        }

        // ---------------------------------------------------------------- 内部工具

        private void EnqueueMain(Action action)
        {
            if (action != null) _mainThread.Enqueue(action);
        }

        private TwinSourceSwitcher.SourceKind CurrentSourceKind()
        {
            if (_switcher == null) _switcher = FindObjectOfType<TwinSourceSwitcher>();
            if (_switcher == null) return TwinSourceSwitcher.SourceKind.Mock;
            return _switcher.CurrentKind;
        }

        private bool ResolveDevice(string twinId, out TwinAnchor anchor, out TwinDeviceModel model)
        {
            anchor = null;
            model = null;

            if (_runtime == null) _runtime = TwinRuntime.Instance;
            if (_runtime == null || _runtime.Registry == null) return false;
            if (!_runtime.Registry.TryGet(twinId, out anchor) || anchor == null) return false;

            model = TwinDeviceModels.Resolve(twinId);
            return model != null && model.CanCommand;
        }

        private MqttCommandTransport MqttTransport
        {
            get
            {
                if (_mqtt == null)
                {
                    _mqtt = new MqttCommandTransport();
                    _mqtt.MessageReceived += OnMqttMessage;
                    _mqtt.Start(_mqttHost, _mqttPort, "", _mqttUser, _mqttPassword);
                }
                return _mqtt;
            }
        }

        private static string BuildCommandJson(TwinCommand cmd, string actionName)
        {
            StringBuilder sb = new StringBuilder(128);
            sb.Append("{\"id\":\"").Append(cmd.Id).Append("\"");
            sb.Append(",\"action\":\"").Append(actionName).Append("\"");
            sb.Append(",\"value\":").Append(cmd.Value.ToString("0.###", CultureInfo.InvariantCulture));
            sb.Append(",\"priority\":").Append(cmd.Priority);
            sb.Append(",\"ts\":").Append(cmd.SentAtUtc.ToString("0.000", CultureInfo.InvariantCulture));
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>把最近一条命令的整行文本打出来（调试 / 汇报用）。</summary>
        [ContextMenu("打印命令审计日志")]
        private void PrintLog()
        {
            if (_log == null) return;
            Debug.Log("[TwinCmd] 审计日志共 " + _log.Count + " 条：\n" + _log.LastLine);
        }
    }
}

using System;

namespace SchoolTwin.Command
{
    /// <summary>
    /// 命令状态。**四态 + 两端**：Idle 是「没有命令」，Failed 是「有命令但没成」。
    ///
    /// 【为什么不是三态】收到 ack 只能证明「设备收到了」，不能证明「设备动了」。
    /// Modbus 里「主站收到正常响应、从站线圈没变」是典型故障；
    /// BACnet 里高优先级写入会覆盖你的写入，ack 是真的、生效的不是你。
    /// 所以最终确认必须回到数据链路读状态位，中间必须留一格 Actuated。
    /// </summary>
    public enum CommandState
    {
        Idle = 0,
        Pending = 1,
        Accepted = 2,
        Actuated = 3,
        Confirmed = 4,
        Failed = 5,
    }

    /// <summary>下行动作枚举。名字必须与协议里的 Action 字符串一致（见 CommandText.ActionName）。</summary>
    public enum CommandAction
    {
        SetPower = 0,
        SetTemperature = 1,
        SetMode = 2,
        SetOpen = 3,
        SetValve = 4,
        /// <summary>【P19】打铃。与其他动作不同：它不是"保持某个稳态"，而是"响一下然后自己停"。</summary>
        SetRing = 5,
    }

    /// <summary>失败原因码。数值与协议错误码对齐（0~5），6 以后是本机内部的。</summary>
    public enum CommandFailCode
    {
        None = 0,
        NotSupported = 1,   // 设备不支持该动作
        OutOfRange = 2,     // 参数越界
        Offline = 3,        // 设备无响应（没回 ack）
        PowerOff = 4,       // 断电不可控（P15）
        ExecFailed = 5,     // 设备执行失败
        NoEffect = 6,       // 执行了但状态没变（回读没动）
        Mismatch = 7,       // 状态变了但没到期望值
        SendFailed = 8,     // 命令没能发出去
        Rejected = 9,       // 本机拒绝（串行锁 / 不可控）
        PlaybackBlocked = 10, // 回放模式下不允许控制
    }

    /// <summary>回执阶段。**这是相对 v1.1 蓝图的一处协议增量**（缺省 = Accepted，旧格式仍可解析）。</summary>
    public enum AckPhase
    {
        Accepted = 0,
        Actuated = 1,
    }

    /// <summary>
    /// 一条下行命令。纯数据，不依赖 Unity。
    ///
    /// ExpectMetric / ExpectMin / ExpectMax 是这条命令的**验收判据**：
    /// 只有数据链路上读到「比发送时刻更新的样本」且值落在区间内，才算 Confirmed。
    /// </summary>
    public sealed class TwinCommand
    {
        public string Id = "";
        public string TwinId = "";
        public CommandAction Action = CommandAction.SetPower;
        public float Value = 0f;

        /// <summary>BACnet 惯例：面板人工操作固定 8。P12 只带不发（不做仲裁）。</summary>
        public int Priority = 8;

        /// <summary>发出时刻（Unix 秒，与数据源的 ts 同一时钟）。回读样本必须比它新。</summary>
        public double SentAtUtc;

        /// <summary>回读判据。</summary>
        public string ExpectMetric = "";
        public float ExpectMin = 0f;
        public float ExpectMax = 0f;

        public CommandState State = CommandState.Pending;
        public CommandFailCode Code = CommandFailCode.None;

        /// <summary>人话原因。失败时必有值（面板直接显示它）。</summary>
        public string Reason = "";

        /// <summary>进入当前状态的时刻（用于分段超时）。</summary>
        public double StateSinceUtc;

        /// <summary>完成时刻（成功或失败）。0 = 还没结束。</summary>
        public double CompletedAtUtc;

        /// <summary>是否已经真正发到传输层（1 秒内没发出去要判失败）。</summary>
        public bool Published;

        /// <summary>回读期间是否见过「值变了但不是期望值」——决定失败原因说「没变」还是「不一致」。</summary>
        public bool SawChange;

        public bool IsTerminal
        {
            get { return State == CommandState.Confirmed || State == CommandState.Failed; }
        }

        /// <summary>动作的显示形式，如 SetTemperature(28)。审计日志和面板共用。</summary>
        public string ActionText
        {
            get
            {
                string name = CommandText.ActionName(Action);
                if (Action == CommandAction.SetPower || Action == CommandAction.SetOpen || Action == CommandAction.SetMode)
                    return name + "(" + (Value >= 0.5f ? "1" : "0") + ")";
                return name + "(" + Value.ToString("0.#") + ")";
            }
        }

        public float ElapsedSeconds
        {
            get
            {
                if (CompletedAtUtc <= 0d) return 0f;
                return (float)(CompletedAtUtc - SentAtUtc);
            }
        }
    }

    /// <summary>设备回执。</summary>
    public struct TwinAck
    {
        public string Id;
        public bool Ok;
        public int Code;
        public string Msg;
        public AckPhase Phase;
        public double TsUtc;
    }

    /// <summary>状态 / 动作 / 失败原因的**唯一文案来源**。面板与日志都从这里取，不许各写一份。</summary>
    public static class CommandText
    {
        public static string ActionName(CommandAction action)
        {
            switch (action)
            {
                case CommandAction.SetPower: return "SetPower";
                case CommandAction.SetTemperature: return "SetTemperature";
                case CommandAction.SetMode: return "SetMode";
                case CommandAction.SetOpen: return "SetOpen";
                case CommandAction.SetValve: return "SetValve";
                case CommandAction.SetRing: return "SetRing";
                default: return action.ToString();
            }
        }

        public static bool TryParseAction(string name, out CommandAction action)
        {
            action = CommandAction.SetPower;
            if (string.IsNullOrEmpty(name)) return false;
            switch (name)
            {
                case "SetPower": action = CommandAction.SetPower; return true;
                case "SetTemperature": action = CommandAction.SetTemperature; return true;
                case "SetMode": action = CommandAction.SetMode; return true;
                case "SetOpen": action = CommandAction.SetOpen; return true;
                case "SetValve": action = CommandAction.SetValve; return true;
                case "SetRing": action = CommandAction.SetRing; return true;
                default: return false;
            }
        }

        /// <summary>动作的中文名（按钮与日志用）。</summary>
        public static string ActionLabel(CommandAction action)
        {
            switch (action)
            {
                case CommandAction.SetPower: return "开关";
                case CommandAction.SetTemperature: return "设定温度";
                case CommandAction.SetMode: return "设定模式";
                case CommandAction.SetOpen: return "开门";
                case CommandAction.SetValve: return "设定阀门";
                case CommandAction.SetRing: return "打铃";
                default: return action.ToString();
            }
        }

        /// <summary>
        /// 命令进行中的文字。**这三种必须在界面上分别可见** ——
        /// 合成一个「处理中」，四态就退化成了两态，等于白做。
        /// </summary>
        public static string ProgressLabel(CommandState state)
        {
            switch (state)
            {
                case CommandState.Pending: return "发送中";
                case CommandState.Accepted: return "执行中";
                case CommandState.Actuated: return "确认中";
                default: return "";
            }
        }

        public static bool IsProgress(CommandState state)
        {
            return state == CommandState.Pending || state == CommandState.Accepted || state == CommandState.Actuated;
        }

        /// <summary>
        /// 失败原因。**只许出现人话**，不许出现 timeout / code 3 这类东西 ——
        /// 面板上出现英文错误码，等于告诉观众「这块没做完」。
        /// </summary>
        public static string FailReason(CommandFailCode code)
        {
            switch (code)
            {
                case CommandFailCode.NotSupported: return "该设备不支持此操作";
                case CommandFailCode.OutOfRange: return "参数超出允许范围";
                case CommandFailCode.Offline: return "设备无响应";
                case CommandFailCode.PowerOff: return "设备已断电";
                case CommandFailCode.ExecFailed: return "设备执行失败";
                case CommandFailCode.NoEffect: return "执行了但状态没变";
                case CommandFailCode.Mismatch: return "设备状态与期望不一致";
                case CommandFailCode.SendFailed: return "命令没能发出去";
                case CommandFailCode.Rejected: return "操作被拒绝";
                case CommandFailCode.PlaybackBlocked: return "回放模式下不可控制";
                default: return "未知失败";
            }
        }
    }

    /// <summary>统一时钟。命令的 sentAt 与数据的 ts 必须同源，否则「回读样本是否更新」判断会失效。</summary>
    public static class TwinClock
    {
        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static double NowUtcSeconds()
        {
            return (DateTime.UtcNow - Epoch).TotalSeconds;
        }
    }
}

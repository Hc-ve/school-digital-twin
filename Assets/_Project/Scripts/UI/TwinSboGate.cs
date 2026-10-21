using System;
using System.Collections.Generic;

namespace SchoolTwin.UI
{
    /// <summary>SBO 各阶段。</summary>
    public enum SboPhase
    {
        /// <summary>没有正在进行的 SBO。</summary>
        Idle = 0,

        /// <summary>弹窗已列出影响面，等用户点「选择」。</summary>
        AwaitSelect = 1,

        /// <summary>已发出选择请求，等被控端回「选择确认」。</summary>
        Selecting = 2,

        /// <summary>已就绪，10 秒内可以点「执行」。超时自动作废。</summary>
        Armed = 3,

        /// <summary>执行完了。</summary>
        Done = 4,

        /// <summary>超时作废（10 秒没点执行，或选择请求 3 秒没回）。</summary>
        Expired = 5,

        /// <summary>用户取消。</summary>
        Cancelled = 6,
    }

    /// <summary>
    /// 一次 SBO 请求：要做什么、影响谁、真的执行时调用哪个回调。
    ///
    /// 【为什么把「影响面」当成一等公民】SBO 的全部价值在于**在执行之前让人看清要动多少东西**。
    /// 弹窗里只写「断 1F 电」是不够的 —— 值班的人需要看到"这 12 台设备会全黑"，
    /// 才会停下来想一下。所以 Targets 不是装饰，它是这个功能存在的理由。
    /// </summary>
    public sealed class TwinSboRequest
    {
        /// <summary>动作名：「断 1F 电」「恢复 2-1班 供电」。</summary>
        public string Title = "";

        /// <summary>作用对象：「1F」「2-1班」「教学楼A」。</summary>
        public string Subject = "";

        /// <summary>回路 id（执行时用它）。</summary>
        public string CircuitId = "";

        /// <summary>目标是通电还是断电。</summary>
        public bool PowerOn;

        /// <summary>将被影响的设备名（给人看的）。</summary>
        public List<string> Targets = new List<string>();

        /// <summary>真正执行。**只会被调用一次**，且只会在 Armed 阶段被调用。</summary>
        public Action Operate;
    }

    /// <summary>
    /// Select-Before-Operate 状态机。**纯逻辑，不碰 Unity** —— 所以能跑自检。
    ///
    /// ── 为什么要有 SBO（别把它当流程仪式）──────────────────────────
    /// 远程操作的经典事故是「点错了图元」和「通信误码」：一条 Modbus 写线圈的报文
    /// 在干扰下变成另一台设备的地址，结果拉错了闸。SBO 的做法是把一次操作拆成两段：
    /// 先"选择"—— 被控端校验对象存在、无闭锁、通信正常，回一个**不动作**的确认；
    /// 再"执行"才真的动作。中间隔一个 10 秒的窗口，任何一环对不上就什么都不发生。
    ///
    /// ── 诚实的边界 ────────────────────────────────────────────────
    /// 本项目没有真实断路器，所以「选择」这一步是**本机预检**（目标存在、有设备、
    /// 没有命令在飞），并模拟 0.4 秒的往返。真实部署时这一步必须发到被控端由它回执 ——
    /// 结构位已经留好：`Select()` 是唯一需要替换成远程调用的地方，
    /// 上面那套「AwaitSelect → Selecting → Armed → Expired」的状态机一行不用改。
    ///
    /// ── 验收要看的不是"能不能断"，是"不点执行会不会真的什么都不发生"────
    /// Spec §11 那条验收：点「选择」→ 变成「已就绪，10 秒内确认」→ 什么都不点
    /// → 10 秒后选择作废、设备一台没动。**这条过不了，SBO 就只是个确认弹窗。**
    /// </summary>
    public sealed class TwinSboGate
    {
        /// <summary>选择请求的预算（秒）。超时没回「选择确认」→ 直接作废。</summary>
        public const float SelectBudgetSeconds = 3f;

        /// <summary>模拟的选择往返耗时（秒）。真实网络里是几十毫秒，这里放慢一点让人看得见过程。</summary>
        public const float SelectRoundTripSeconds = 0.4f;

        /// <summary>就绪窗口（秒）。</summary>
        public const float ArmedSeconds = 10f;

        public SboPhase Phase { get; private set; }
        public TwinSboRequest Request { get; private set; }

        /// <summary>当前阶段的说明文字（弹窗直接显示它）。</summary>
        public string Message { get; private set; }

        /// <summary>当前阶段的截止时刻（unscaledTime）。Idle/Done 时无意义。</summary>
        public float Deadline { get; private set; }

        private float _selectAckAt;

        public TwinSboGate()
        {
            Phase = SboPhase.Idle;
            Message = "";
            Request = null;
        }

        /// <summary>是否占着一个操作（弹窗要挡住面板）。</summary>
        public bool IsActive
        {
            get
            {
                return Phase == SboPhase.AwaitSelect
                    || Phase == SboPhase.Selecting
                    || Phase == SboPhase.Armed;
            }
        }

        /// <summary>是否已经结束（用于「结束语显示几秒再关」）。</summary>
        public bool IsFinished
        {
            get { return Phase == SboPhase.Done || Phase == SboPhase.Expired || Phase == SboPhase.Cancelled; }
        }

        public void Reset()
        {
            Phase = SboPhase.Idle;
            Request = null;
            Message = "";
            Deadline = 0f;
            _selectAckAt = 0f;
        }

        /// <summary>开始一次 SBO。会打断上一次未结束的（上一次作废，不执行）。</summary>
        public void Begin(TwinSboRequest request, float now)
        {
            Request = request;
            Phase = SboPhase.AwaitSelect;
            Message = "请确认影响范围后点「选择」";
            Deadline = 0f;
            _selectAckAt = 0f;
        }

        /// <summary>
        /// 「选择」：预检 + 请求选择确认。返回 false = 本机就拒了，弹窗应显示 Message。
        ///
        /// 预检的每一条都对应一类真实事故：
        ///   · 没有执行回调 → 点了也什么都不发生（比报错更糟）
        ///   · 影响 0 台设备 → 目标选错了（回路 id 拼错、房间没设备）
        /// </summary>
        public bool Select(float now)
        {
            if (Phase != SboPhase.AwaitSelect) return false;

            if (Request == null || Request.Operate == null)
            {
                Phase = SboPhase.Expired;
                Message = "没有可执行的动作";
                return false;
            }

            if (Request.Targets == null || Request.Targets.Count == 0)
            {
                Phase = SboPhase.Expired;
                Message = "这个范围内没有设备，已取消";
                return false;
            }

            Phase = SboPhase.Selecting;
            Message = "正在选择…";
            _selectAckAt = now + SelectRoundTripSeconds;
            Deadline = now + SelectBudgetSeconds;
            return true;
        }

        /// <summary>推进状态机。必须每帧调 —— 不调的话倒计时永远不会走完，选择也永远作废不了。</summary>
        public void Tick(float now)
        {
            switch (Phase)
            {
                case SboPhase.Selecting:
                    if (now >= _selectAckAt)
                    {
                        Phase = SboPhase.Armed;
                        Message = "已就绪";
                        Deadline = now + ArmedSeconds;
                        return;
                    }
                    if (now >= Deadline)
                    {
                        Phase = SboPhase.Expired;
                        Message = "选择超时（3 秒内没收到选择确认），已作废";
                    }
                    return;

                case SboPhase.Armed:
                    if (now >= Deadline)
                    {
                        Phase = SboPhase.Expired;
                        Message = "选择已作废（10 秒内没有确认执行）";
                    }
                    return;
            }
        }

        /// <summary>
        /// 「执行」。只有在 Armed 且没超时的情况下才会真的动作 —— 这一句就是 SBO 的全部意义。
        /// </summary>
        public bool Operate(float now)
        {
            if (Phase != SboPhase.Armed) return false;

            if (now > Deadline)
            {
                Phase = SboPhase.Expired;
                Message = "选择已作废（超时）";
                return false;
            }

            Phase = SboPhase.Done;
            Message = "已执行";

            // 先置状态再回调：回调里可能会开下一次 SBO（比如"断电后再恢复"），
            // 顺序反了就会被这一次的状态覆盖。
            TwinSboRequest req = Request;
            if (req != null && req.Operate != null) req.Operate();
            return true;
        }

        public void Cancel()
        {
            if (!IsActive) return;
            Phase = SboPhase.Cancelled;
            Message = "已取消，什么都没发生";
        }

        /// <summary>剩余秒数（就绪窗口倒计时）。非 Armed 时返回 0。</summary>
        public float Remaining(float now)
        {
            if (Phase != SboPhase.Armed) return 0f;
            float left = Deadline - now;
            return left > 0f ? left : 0f;
        }

        /// <summary>弹窗底部的状态行：「已就绪 · 8 秒内确认执行」。</summary>
        public string StatusLine(float now)
        {
            if (Phase == SboPhase.Armed)
            {
                int left = (int)Math.Ceiling(Remaining(now));
                if (left < 0) left = 0;
                return "已就绪 · " + left + " 秒内确认执行";
            }
            return Message;
        }
    }
}

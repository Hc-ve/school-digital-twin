using System;
using SchoolTwin.Twin;

namespace SchoolTwin.Data
{
    /// <summary>
    /// Mock 数值生成器：把「正弦波走遍全量程」换成
    /// 「平时在正常区间内缓慢波动 + 偶发短暂越界」。
    ///
    /// ── 为什么必须改 ──────────────────────────────────────────────
    /// 旧实现：value = Min + norm * (Max - Min)，正弦覆盖**整个量程**；
    /// 而阈值（WarnLow/WarnHigh/AlarmLow/AlarmHigh）卡在量程中段，
    /// 于是数值只要往两侧摆，就必然长时间落在警告/告警区。
    ///
    /// 实测（2026-10-20）：5 个点位常态有 3 个异常
    ///   CO2 1990ppm（量程 400~2000，告警线 1500）
    ///   亮度 2.22%（量程 0~100，警告线 20）
    ///   温度 33.11℃（量程 10~35，告警线 32）
    /// 而 P3 验收标准是「非技术人员 3 分钟内**找到**告警房间」——
    /// 一半房间同时在报警，「找到」这个动作就失去了意义。
    ///
    /// ── 新实现分两层 ──────────────────────────────────────────────
    /// ① 基线：在 [WarnLow, WarnHigh] 内收 12% 的区间里做慢速正弦
    ///          → 平时全绿，且永远不会踩到阈值线；
    /// ② 事件：每个指标按各自相位、每 ExcursionPeriodSeconds 秒触发一次
    ///          短促偏移，把数值推向量程边界
    ///          → 偶发一次警告/告警，随即自动恢复。
    ///
    /// ── 设计取舍：为什么不新增「模拟区间」配置项 ──────────────────
    /// 阈值就是「业务上什么算正常」的唯一声明，模拟数据理应服从它。
    /// 若再单独配一个模拟区间，就会出现「阈值改了、模拟数据还在老区间跑」
    /// 的分裂状态。所以这里让模拟区间**由阈值派生**：改阈值即改模拟行为。
    ///
    /// ⚠️ 线程约束：本类只用 System.Math 与自定义比较，**不触碰任何 Unity API**，
    /// 可安全在后台数据线程调用。
    /// （P1-D 曾因在写线程里读 Time.realtimeSinceStartup 导致写线程静默死掉。）
    /// </summary>
    public static class MockSignalGenerator
    {
        /// <summary>
        /// 这个指标属于哪种**物理形态**。
        ///
        /// ── 为什么必须分类（主理人 2026-11-03）──────────────────────────
        /// 「数据展示波动范围频率全部依照现实逻辑来处理」。
        ///
        /// 原来的做法是**所有连续量共用一条正弦波**，周期由指标名哈希到 20~60 秒。
        /// 这个做法对"功率、亮度、阀门开度"勉强能看，但对**环境量**是错的：
        ///
        ///   · 空气温度：房间热惯性极大（几十吨空气 + 墙体 + 家具），
        ///     真实室温在十几分钟尺度上才变化 1°C，绝不可能 20 秒走完一个来回；
        ///   · CO₂：上课时只**单调上升**（人呼吸持续累积），
        ///     只有开门/课间/新风启动才断崖式下跌。它有一整套缓慢的锯齿包络，
        ///     根本不是正弦 —— 用正弦会画出"CO₂ 自己涨上去又自己降下来"，物理上不成立；
        ///   · 暖气供水温度：锅炉出水温度由燃烧/循环泵控制，
        ///     在 60~80°C 之间随负荷缓慢漂移，分钟级变化。
        ///
        /// 所以这里按**指标的物理含义**分类，各类走各自的波形模型。
        /// 分类判据只看指标名，不看设备类型 —— 同一个 `Temperature` 无论挂在
        /// 传感器的"室温"还是别处，物理规律是同一个。
        /// </summary>
        public enum MetricShape
        {
            /// <summary>一般工程量（功率 / 亮度 / 开度 / 音量）：现有正弦 + 事件模型够用。</summary>
            Default = 0,

            /// <summary>室内空气温度：极慢漂移，几乎无高频抖动。</summary>
            RoomTemperature = 1,

            /// <summary>暖气供水温度：缓慢但幅度可见的漂移（随锅炉负荷）。</summary>
            SupplyTemperature = 2,

            /// <summary>CO₂：缓慢单调爬升 + 偶发断崖回落（换气事件）。</summary>
            CarbonDioxide = 3,

            /// <summary>相对湿度：与室温反相关，跟随缓慢变化。</summary>
            Humidity = 4,

            /// <summary>
            /// CPU 负载：**开机瞬间忙一阵 → 很快回落到空闲基线**，空闲时平稳。
            ///
            /// ── 为什么它也不能套通用正弦（主理人 2026-11-03 第二张截图）──
            /// 截图里刚点完「开机」，CPU 负载 **83.88%** 正在擦公告警线 85。
            /// 而通用波形给它的基线是 10~75% 的正弦 —— 意味着**一台只开了机、
            /// 什么程序都没跑的电脑，负载常态在 30~60% 之间到处飘**。
            /// 真实台式机开机后空闲负载是 5~15%，只有开机那几秒到几十秒会忙到 80%+。
            ///
            /// 这个形态解决的是三件事：
            ///   ① 开机瞬间冲高（60~90%），符合"系统在起服务"；
            ///   ② 几十秒内按指数回落到空闲（5~15%），不是一直高；
            ///   ③ 空闲时只有小幅缓动，不再周期性冲上限 —— 那本来就不该发生。
            ///
            /// 关机时归 0 由 `MockDeviceAgent.SetPcRunning` 的 override 负责，
            /// 与本模型无关（override 优先于波形）。
            /// </summary>
            CpuLoad = 5,
        }

        /// <summary>
        /// 指标名 → 物理形态。
        ///
        /// ⚠️ 用**精确名**匹配，不做 `Contains("Temp")` 这种模糊判断 ——
        /// `SupplyTemp` 和 `Temperature` 语义完全不同（前者是暖气出水，后者是室温），
        /// 模糊匹配会把它们混成一类，而它们的合理周期差 3 倍以上。
        /// </summary>
        public static MetricShape ShapeOf(TwinMetricDefinition metric)
        {
            if (metric == null) return MetricShape.Default;

            switch (metric.Metric)
            {
                case "Temperature": return MetricShape.RoomTemperature;
                case "SupplyTemp": return MetricShape.SupplyTemperature;
                case "CO2": return MetricShape.CarbonDioxide;
                case "Humidity": return MetricShape.Humidity;
                case "CPULoad": return MetricShape.CpuLoad;
                default: return MetricShape.Default;
            }
        }

        /// <summary>
        /// 生成参数。用对象初始化器构造即可，零 GC。
        /// </summary>
        public struct Config
        {
            /// <summary>基线波动周期（秒）。平时数值在这个周期内缓慢起伏。</summary>
            public float PeriodSeconds;

            /// <summary>异常事件周期（秒）。每隔这么久制造一次短暂越界。&lt;= 1 表示永不越界。</summary>
            public float ExcursionPeriodSeconds;

            /// <summary>每次异常事件持续的秒数。</summary>
            public float ExcursionSeconds;

            /// <summary>事件峰值冲向量程边界的深度（0~1）。0.95 ≈ 冲到 Min/Max 附近。</summary>
            public float ExcursionDepth;
        }

        /// <summary>
        /// 生成一个数值。
        /// </summary>
        /// <param name="metric">指标定义，提供 Min/Max 与四个阈值。</param>
        /// <param name="t">数据源启动至今的秒数（不是 Unix 时间戳，避免精度丢失）。</param>
        /// <param name="phase">相位偏移，用于让不同点位/指标的曲线互相错开。</param>
        /// <param name="cfg">生成参数。</param>
        /// <param name="twinId">
        /// 点位编号。只有「需要按机器记账」的形态才用得到（目前是 `CPULoad`：
        /// 它要知道"这台机器开了多久"）。其余形态忽略它。
        /// 允许传 null —— 传了就按"已长时间运行"处理，不会崩。
        /// </param>
        public static float Next(TwinMetricDefinition metric, double t, double phase, in Config cfg,
                                 string twinId = null)
        {
            if (metric == null) return 0f;

            // ── ⓪ 开关量不发波形 ──
            // 见 IsDiscrete 的说明：0/1 信号被正弦压成 0.12~0.88，
            // 界面按「≥0.5 算开」判断 → 门一会儿「已开」一会儿「已关」。
            if (IsDiscrete(metric)) return metric.Max;

            float lo = metric.Min;
            float hi = metric.Max;
            if (hi <= lo) return lo;

            // ── ① 基线：夹在警告阈值之内，再向内收 12%，平时不踩线 ──
            float warnLo = Clamp(metric.WarnLow, lo, hi);
            float warnHi = Clamp(metric.WarnHigh, lo, hi);
            if (warnHi <= warnLo)
            {
                // 阈值区间无效（例如 WarnLow >= WarnHigh）→ 退回全量程
                warnLo = lo;
                warnHi = hi;
            }

            float inset = (warnHi - warnLo) * 0.12f;
            float nLo = warnLo + inset;
            float nHi = warnHi - inset;
            if (nHi <= nLo)
            {
                nLo = warnLo;
                nHi = warnHi;
            }

            float period = cfg.PeriodSeconds > 5f ? cfg.PeriodSeconds : 5f;

            // ── 按物理形态分派 ──────────────────────────────────────────
            //
            // 环境量（室温 / 暖气出水 / CO2 / 湿度）**不共用** "正弦 + 事件" 那套：
            // 它们的合理时间尺度是分钟到小时，而且 CO2 还有方向性（只涨不跌，
            // 除非有人换气）。硬套功率那套模型会画出物理上不存在的曲线。
            //
            // 各类模型的周期**写死在这里**，不读 `cfg.PeriodSeconds`：
            // 调用方（`MockDataSource.OmegaOf`）是按指标名哈希出 20~60s 的，
            // 那个数量级本来就不属于环境量 —— 让它覆盖会把刚修好的又改回错的。
            switch (ShapeOf(metric))
            {
                case MetricShape.RoomTemperature:
                    return GenerateRoomTemperature(metric, t, phase, nLo, nHi, lo, hi);

                case MetricShape.SupplyTemperature:
                    return GenerateSupplyTemperature(metric, t, phase, nLo, nHi, lo, hi);

                case MetricShape.CarbonDioxide:
                    return GenerateCarbonDioxide(metric, t, phase, nLo, nHi, lo, hi);

                case MetricShape.Humidity:
                    return GenerateHumidity(metric, t, phase, nLo, nHi, lo, hi, cfg);

                case MetricShape.CpuLoad:
                    return GenerateCpuLoad(metric, t, phase, nLo, nHi, lo, hi, twinId);
            }

            // ── 以下为 Default：一般工程量，沿用"基线正弦 + 偶发越界" ──
            double baseWave = 0.5 + 0.5 * Math.Sin(2.0 * Math.PI * t / period + phase);
            float value = (float)(nLo + baseWave * (nHi - nLo));

            // ── ② 事件：按相位错开，每 ExcursionPeriodSeconds 秒来一次短促偏移 ──
            if (cfg.ExcursionPeriodSeconds > 1f && cfg.ExcursionSeconds > 0.1f)
            {
                // 事件时长不得超过半个周期：否则 cycle < dur 恒成立，
                // 退化成「一直越界」，比原来的常态告警还糟。
                float dur = cfg.ExcursionSeconds;
                float half = cfg.ExcursionPeriodSeconds * 0.5f;
                if (dur > half) dur = half;

                double cycle = (t + phase * 3.7) % cfg.ExcursionPeriodSeconds;
                if (cycle < 0.0) cycle += cfg.ExcursionPeriodSeconds;

                if (cycle < dur)
                {
                    // 0 → 1 → 0 的平滑包络，进出都不突兀
                    double k = Math.Sin(Math.PI * cycle / dur);
                    k *= k;   // 平方 → 上升更快、峰值停留更短

                    // 往「余量更大」的一侧冲，保证一定能越过阈值
                    bool goUp = (hi - nHi) >= (nLo - lo);
                    float target = goUp ? hi : lo;
                    value = (float)(value + (target - value) * k * cfg.ExcursionDepth);
                }
            }

            return Clamp(value, lo, hi);
        }

        // ================================================================ 环境量专用波形
        //
        // 四个方法共用一条设计原则：**只用叠加的低频正弦，不加高频噪声**。
        // ── 为什么不能加噪声（这一条最容易做错）─────────────────────────
        // `MockDataSource` 原本给所有连续量加 `±span × 3%` 的均匀噪声。
        // 对功率（0~120W）那是 ±3.6W，看起来像负载抖动，合理；
        // 但对温度（量程 10~40，span=30）就是 **±0.9°C 的逐样本抖动** ——
        // 10Hz 下每 0.1 秒跳 0.9°C，一条毛刺密布的曲线。
        // 真实的 PT100 温度传感器分辨率 0.1°C、时间常数几十秒，
        // 曲线是**平滑**的。加了噪声就等于给一个慢变量画了一张示波器图。
        //
        // 所以这四个方法都在内部自己控制细节，并且：
        // **调用方必须跳过噪声叠加**（见 `MockDataSource` 里的 `SmoothOf` 判定）。

        /// <summary>
        /// 室内空气温度：由 `TwinClimate` 的**室外模型**派生。
        ///
        /// ── 我第一版这里写错过，记下来 ────────────────────────────────
        /// 第一版用的是"22 分钟周期正弦 + 5.5 分钟次波"，理由是"房间有热惯性"。
        /// 推理对了一半、时间尺度完全错了：热惯性决定的是
        /// "室温**跟随室外**时会被衰减和滞后"，不是"室温自己以 22 分钟为周期起伏"。
        /// 22 分钟这个周期没有任何物理来源 —— 那正是"胡编乱造"。
        ///
        /// 主理人随即问了关键的一句：
        ///   「这个室温的数据检测后面做时间天气系统，会随着天气时间温度变化而变化吗？」
        /// 如果维持第一版，答案会是"不会" —— 天气系统做完还得把这段删掉重写。
        ///
        /// ── 现在 ─────────────────────────────────────────────────────
        /// 室温 = `TwinClimate.IndoorNowC()`（室外温度 → 热惯性 → 得热 → 供暖钳制），
        /// 数据源只负责把它送出去。天气/时间系统上线后调
        /// `TwinClimate.SetWeather()` / `SetHourOverride()`，
        /// 室温曲线**立刻跟着变**，本方法一行都不用改。
        ///
        /// ── 这里的 `nLo/nHi` 还有什么用 ─────────────────────────────
        /// 不用了。室内温度的实际范围由模型决定（供暖季 19~21°C，
        /// 非供暖季随室外走），不该被"正常区"夹住 ——
        /// 夹住就等于强行把夏天也拉到 16~28，那是篡改数据。
        /// 真超出了 `WarnHigh`，就让它报警 —— 那才是真实会发生的事（夏天教室 31°C）。
        /// </summary>
        private static float GenerateRoomTemperature(TwinMetricDefinition metric, double t, double phase,
                                                    float nLo, float nHi, float lo, float hi)
        {
            // 每个点位错开一点点（不同朝向的房间差 0.3~0.8°C），
            // 但**共享同一个物理模型** —— 不是各自一条独立正弦。
            double offset = ((phase % 1.0) - 0.5) * 1.6;

            double c = TwinClimate.IndoorNowC() + offset;

            // 量化到 0.01°C：真实温度传感器分辨率有限，不做无意义的浮点尾数抖动
            float v = (float)(Math.Round(c * 100.0) / 100.0);

            return Clamp(v, lo, hi);
        }

        /// <summary>
        /// 暖气供水温度：由**室外温度**决定（供暖曲线的标准做法）。
        ///
        /// ── 现实依据 ─────────────────────────────────────────────────
        /// 集中供暖的供水温度不是固定值，而是按「室外温度—供水温度」曲线调节的：
        /// 外面越冷，锅炉出水越热。典型对应关系（一次侧）：
        ///
        ///     室外 −10°C  →  供水 75°C
        ///     室外   0°C  →  供水 65°C
        ///     室外  +8°C  →  供水 55°C
        ///     室外 +15°C  →  供水 45°C（供暖临界，再暖就停炉）
        ///
        /// 这个关系叫「质调节」，是供热行业的标准运行方式。
        /// 所以暖气水温与室温**同源**（都来自室外温度），但两者形态不同：
        /// 水温对室外变化是**即时响应**（几十分钟），室温要慢得多（几小时）。
        ///
        /// ── 非供暖季的坑（实测踩到）───────────────────────────────────
        /// 质调节公式 `75 + (室外+10)×(−1.2)` 在 7 月会算出 **22~33°C** ——
        /// 因为那时室外 25~34°C，公式外推出"供水比室温还低"。
        /// 但 7 月锅炉根本没开，这个数没有意义。
        ///
        /// 处理办法不是改公式（公式在供暖温度区间内是对的），
        /// 而是**分两种运行状态**：
        ///   · 供暖季：按质调节曲线走，出水 45~75°C
        ///   · 非供暖季：生活热水循环，稳定在 48°C 附近（锅炉只保生活热水，
        ///     不做采暖循环，所以没有随室外变化的质调节）
        /// 这样夏天不会出现"暖气供水 22°C"这种一眼假的数。
        /// </summary>
        private static float GenerateSupplyTemperature(TwinMetricDefinition metric, double t, double phase,
                                                       float nLo, float nHi, float lo, float hi)
        {
            double supply;

            if (TwinClimate.HeatingSeasonOn)
            {
                // 采暖季：质调节。室外越冷，出水越热。
                double outdoor = TwinClimate.OutdoorNowC();
                // 斜率 = (45-75)/(15-(-10)) = -1.2
                supply = 75.0 + (outdoor + 10.0) * (-1.2);
            }
            else
            {
                // 非采暖季：只供生活热水，锅炉定温运行，不跟室外走。
                supply = 48.0;
            }

            // 锅炉控制的小波动（±1.5°C，6 分钟周期，各台错开相位）
            supply += 1.5 * Math.Sin(2.0 * Math.PI * t / 360.0 + phase * 2.3);

            float v = (float)(Math.Round(supply * 10.0) / 10.0);
            return Clamp(v, lo, hi);
        }

        /// <summary>
        /// CO₂：**缓慢单调爬升 + 偶发断崖回落**。
        ///
        /// ── 为什么正弦是错的（三类里最明显的一个）─────────────────────
        /// CO₂ 的唯一来源是人的呼吸，唯一去处是换气（开窗、课间、新风系统）。
        /// 只要教室里有人，浓度就**只会往上涨**，涨的速度 ≈
        /// (人数 × 人均呼出量 − 新风稀释量) / 房间体积。
        /// 它不会"自己涨上去又自己降回来"—— 那需要负排放，物理上不存在。
        ///
        /// 真实曲线形态：
        ///   · 上课期间：从 500ppm 缓慢爬到 1200~1800ppm，斜率基本恒定（20~40 分钟）
        ///   · 课间/开窗：几分钟内**断崖式**跌回 500~600ppm
        ///   · 一天里有 6~8 个这样"爬升-跌落"的锯齿
        ///
        /// ── 与作息对齐（不只是"每 12 分钟来一次"）────────────────────
        /// 锯齿的**起点**跟 `TwinClimate` 的时刻走：上课开始（8:00 / 13:30）之后
        /// 开始爬升，课间（每 45 分钟）回落。这样 CO₂ 曲线与室温曲线的
        /// "得热分段"用的是同一个 `HourOfDay` —— 两条曲线在同一条时间轴上叙事，
        /// 不会出现"室温说在上课、CO₂ 说空无一人"的分裂。
        ///
        /// 夜间（没人的时段）直接保持低值，不爬升 —— 空教室的 CO₂ 就是 450ppm 左右。
        /// </summary>
        private static float GenerateCarbonDioxide(TwinMetricDefinition metric, double t, double phase,
                                                   float nLo, float nHi, float lo, float hi)
        {
            double hour = TwinClimate.HourOfDay;

            // 是否有人：**问 TwinClimate 的得热表**，不在这里另写一套时段。
            //
            // 原来这里硬编码了 `(hour>=7.5 && hour<12.0) || ...`，与 TwinClimate 的
            // `OccupancyGainC` 是两份互相不知情的作息表。只要哪天改了一边，
            // 就会出现"室温说上午满课、CO₂ 说空无一人"的分裂 ——
            // 而两条曲线画在同一块面板上，一眼就能看出来。
            //
            // 判据：得热明显高于空楼本底（0.2）就认为有人。
            // 阈值 0.35 落在 0.2 与 0.4（午休）之间，午休算有人（教室确实还有人自习）。
            bool occupied = TwinClimate.GainAt(hour) > 0.35;

            double norm;

            if (!occupied)
            {
                // 空教室：只有极慢的本底漂移（室外渗风带来的），不爬升
                double slow = 0.5 + 0.5 * Math.Sin(2.0 * Math.PI * t / 5400.0 + phase);
                norm = 0.06 + 0.04 * slow;          // ≈ 量程的 6~10%，即 470~530 ppm
            }
            else
            {
                // 有人：按 45 分钟一节课的节奏做锯齿（爬升 41 分钟 + 换气 4 分钟）
                const double ClassCycle = 45.0 * 60.0;      // 2700 秒
                const double PurgeFraction = 0.09;          // 换气约占 4 分钟

                // 用全局时刻对齐到"整点上课"，各点位按 phase 错开 0~90 秒（不同教室的传感器不同步）
                double phaseOffset = (phase % 1.0) * 90.0;
                double cycle = ((t + phaseOffset) % ClassCycle + ClassCycle) % ClassCycle;

                double riseSpan = ClassCycle * (1.0 - PurgeFraction);

                if (cycle < riseSpan)
                {
                    // 爬升段：**线性**。斜率恒定是 CO₂ 最典型的特征 ——
                    // 人一直呼出，浓度就匀速涨。
                    norm = cycle / riseSpan;
                }
                else
                {
                    // 换气段：指数衰减，快起慢收
                    double u = (cycle - riseSpan) / (ClassCycle - riseSpan);
                    norm = Math.Exp(-4.5 * u);
                }

                // 峰值在 55%~100% 之间浮动（人数不同：满班 vs 几个人自习）
                double slow = 0.5 + 0.5 * Math.Sin(2.0 * Math.PI * t / 2400.0 + phase * 0.6);
                norm = 0.12 + norm * (0.55 + 0.45 * slow) * 0.88;
            }

            // 映射到 [nLo, nHi]。CO₂ 的 WarnLow = 400（量程下限）、WarnHigh = 1000，
            // 所以 nLo≈472、nHi≈928。满班时爬到约 900，擦到警告线但不停留 ——
            // 这符合"通风不良的教室偶尔超标"的真实情况。
            float v = (float)(nLo + norm * (nHi - nLo));

            // CO₂ 传感器分辨率通常几十 ppm，量化到 5ppm 更真实
            v = (float)(Math.Round(v / 5.0) * 5.0);

            return Clamp(v, lo, hi);
        }

        /// <summary>
        /// 相对湿度：由 `TwinClimate` 的**物理模型**派生，这里只加点位差异。
        ///
        /// ── 我在这里错过两次，都记下来 ────────────────────────────────
        /// **第一错**：写成 `52.0 - (室内温度 - 20) * 4.5` 的线性式。
        /// 看着"反相关"对了，但把相对湿度当成了可以线性外推的量。
        /// 实算 7 月（室温 30.5°C）得到 **−3%RH** —— 负数。
        ///
        /// **第二错**：以为把系数调小就行。相对湿度不是温度的函数，
        /// 它是「含湿量 / 饱和含湿量」，而饱和含湿量随温度**指数增长**
        /// （20°C→17.3 g/m³，30°C→30.3 g/m³）。线性式在小范围内近似，
        /// 跨度 10°C 就崩了。
        ///
        /// ── 现在 ─────────────────────────────────────────────────────
        /// 全部交给 `TwinClimate.IndoorRhNowC()`：室外含湿量 → 渗入 → 产湿
        /// → 物理上下限 → 除以室内温度的饱和含湿量。
        /// 「与室温反相关」在这个结构里是**自洽**的，不是硬凑的相位。
        ///
        /// 本方法只负责一件事：不同朝向/楼层的点位差 ±1.2%RH。
        /// 这个差异真实存在（同一层靠窗与靠内的房间湿度能差 2~3%RH）。
        /// </summary>
        private static float GenerateHumidity(TwinMetricDefinition metric, double t, double phase,
                                              float nLo, float nHi, float lo, float hi, in Config cfg)
        {
            double baseRh = TwinClimate.IndoorRhNowC();

            // 点位差异：±1.2%RH，用相位做稳定散列（同一台设备每次算出来一样）
            baseRh += ((phase % 1.0) - 0.5) * 2.4;

            float v = (float)(Math.Round(baseRh * 10.0) / 10.0);
            return Clamp(v, lo, hi);
        }

        /// <summary>
        /// CPU 负载（%）：**开机瞬间忙一阵 → 指数回落到空闲基线**。
        ///
        /// ── 为什么单独建模（主理人 2026-11-03 第二张截图）─────────────────
        /// 截图：刚点「开机」，CPU 负载 **83.88%**，正在擦告警线 85。
        ///
        /// 通用波形的两处不合现实：
        ///   ① 基线被夹到 10~75%（正常区 0~85 再内收 12%），
        ///      一台只开了机、没跑任何程序的电脑，负载常态在 30~60% —— 太高了；
        ///   ② 每 45 秒还有一次冲向 100% 的"越界事件"，
        ///      于是 CPU 会周期性逼近 85% 的告警线。真实空闲 PC 不会这样。
        ///
        /// ── 真实形态 ─────────────────────────────────────────────────
        /// 开机 0~20 秒：系统起服务、加载启动项，负载 60~90%；
        /// 20~60 秒：按指数回落；
        /// 之后：空闲 5~15%，只有缓慢小幅起伏（后台任务）。
        ///
        /// ── `t` 怎么用 ───────────────────────────────────────────────
        /// 本方法拿到的 `t` 是**数据源启动至今的秒数**，不是"这台机器开了多久"。
        /// 所以不能直接拿 `t` 当开机时长 —— 那会导致"跑了一会儿之后所有机器
        /// 都自动变成空闲状态"，即使你刚点亮它。
        ///
        /// 真正的开机时刻来自 `MockDeviceAgent.SetPcRunning`：它开机时清掉 override，
        /// 本方法从那一刻起才被调用。用一个按 `twinId` 记录的首次调用时刻来推
        /// 「开机了多久」，比往生成器里塞状态更简单，也不用改 override 的机制。
        /// 为什么不能拿 `phase` 当这个 key，见下面 `BootElapsedSeconds` 的说明。
        /// </summary>
        private static float GenerateCpuLoad(TwinMetricDefinition metric, double t, double phase,
                                             float nLo, float nHi, float lo, float hi, string twinId)
        {
            // 开机后经过的秒数（按 twinId 记账，见 BootElapsedSeconds）
            double sinceOn = BootElapsedSeconds(twinId, t);

            // ── ① 启动冲刺：80% 起，指数衰减 ──
            // τ = 12 秒：开机 12 秒后剩 37%，36 秒后剩 5%
            const double BootTau = 12.0;
            double boot = sinceOn < 0.0 ? 0.0 : 0.80 * Math.Exp(-sinceOn / BootTau);

            // ── ② 空闲基线：8~14%，缓慢起伏（后台任务、杀毒扫描之类）──
            // 90 秒周期、幅度 ±3%，**不再周期性冲上限**
            double idle = 0.11 + 0.03 * Math.Sin(2.0 * Math.PI * t / 90.0 + phase);

            // ③ 取较大者：开机头几十秒冲刺压过空闲，之后自然由空闲主导
            double load = boot > idle ? boot : idle;

            // 量化到 0.1%（任务管理器显示一位小数，再细就是假精度）
            float v = (float)(Math.Round(load * 1000.0) / 10.0);

            // 上限 92%：真实 CPU 极少长时间 100%（那是卡死，不是正常负载）
            if (v > 92f) v = 92f;

            return Clamp(v, lo, hi);
        }

        /// <summary>
        /// 自「这台机器开机」起经过的秒数。
        ///
        /// ── 为什么用 `twinId` 当 key（phase 做不到这件事）──────────────────
        /// `phase` 是一个**数值**，不是**身份**。同一台机器每一拍拿到的 phase 是同一个
        /// 近似常数（`MockDataSource.PhaseOf` = `pointIndex*1.1 + metricIndex*2.3 + seq*0.37`，
        /// 其中 `seq` 每帧开头都重置为 0、对固定 (i,k) 等价于常量；`MqttLoopbackPublisher`
        /// 更是直接 `pointIndex*1.7 + metricIndex*2.9`）。
        ///
        /// 所以拿 phase 当 key 的问题不是"每帧都变"，而是听起来更亏的另一种：
        /// **两台不同的机器只要算出了同一个 phase，就会被记成同一次开机**。
        /// 这里恰好不会撞（两个数据源的步长 1.1/2.3 与 1.7/2.9 在 149 个指标范围内
        /// 基本都是唯一的），但那是运气，不是设计 —— 一旦有人改了步长就可能悄悄撞上，
        /// 表现为"两台机器同时开机，一台的 CPU 直接是空闲值"。
        ///
        /// 用 `twinId`：每台点位一个固定字符串，天生唯一，和相位怎么调无关。
        ///
        /// ⚠️ 线程约束：`MockDataSource` 与 `MqttLoopbackPublisher` 各有一条后台线程，
        /// 用 `ConcurrentDictionary` 避免并发写同一个 key 抛异常。
        /// </summary>
        private static double BootElapsedSeconds(string twinId, double t)
        {
            if (string.IsNullOrEmpty(twinId)) return 300.0;   // 不知道是哪台 → 直接按"已空闲"

            double start;
            if (!BootStart.TryGetValue(twinId, out start))
            {
                BootStart[twinId] = t;      // 第一次见到 → 认为刚刚开机
                return 0.0;
            }

            double elapsed = t - start;
            if (elapsed < 0.0) elapsed = 0.0;        // 数据源重启时 t 会倒退
            if (elapsed > 300.0) elapsed = 300.0;    // 5 分钟后一律按已稳定处理
            return elapsed;
        }

        /// <summary>每个点位的「开机时刻」（数据源时间轴）。见 BootElapsedSeconds。</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, double> BootStart =
            new System.Collections.Concurrent.ConcurrentDictionary<string, double>();

        /// <summary>
        /// 关机 → 开机时清掉这台机器的开机时刻，
        /// 否则它记的还是上次那条，一开机就直接显示"已经空闲"、看不到启动冲刺。
        ///
        /// `MockDeviceAgent.SetPcRunning` 开机时调这个。
        /// </summary>
        public static void ResetBootClock(string twinId)
        {
            if (string.IsNullOrEmpty(twinId)) return;
            double ignored;
            BootStart.TryRemove(twinId, out ignored);
        }

        /// <summary>
        /// 清空**所有**开机时刻。由 `MockDataSource.ResetStaticsOnLoad` 在每次进 Play 时调用。
        ///
        /// ── 为什么需要（与 `s_muted` / `s_overrides` 是同一类坑）───────────
        /// `BootStart` 里存的是**数据源时间轴上的 t**，而 t 在每次 Play 都从 0 重新开始。
        /// 于是上一轮记下的 `start = 42.0`，这一轮算出来 `elapsed = 0 - 42 = -42`
        /// → 被 `clamp 到 0` → 判定为"刚刚开机" → CPU 直接从 80% 起跳。
        ///
        /// 目前**不会**表现出来，因为开局播种把 21 台 PC 的 `CPULoad` 压成了 override 0，
        /// 而 override 优先于波形；用户真去点开机时 `SetPcRunning` 又会 `ResetBootClock`。
        /// 也就是说这条现在是"纯卫生"——但那正是 `s_muted` 当初的样子：
        /// 只有当某天有人加了一条"不经过 SetPcRunning 就能通电"的路径，它才会变成一个
        /// 「第一个 Play 正常、第二个 Play 才开始怪」的 bug，而那种 bug 极难查。
        /// 四行代码买断这一类，值得。
        /// </summary>
        public static void ResetAllBootClocks()
        {
            BootStart.Clear();
        }

        /// <summary>
        /// 这个指标的数据是否需要**跳过外部噪声叠加**（返回 true = 不要加噪声）。
        ///
        /// 调用方（`MockDataSource` / `MqttLoopbackPublisher`）在生成值之后会加
        /// `±span × noiseRatio` 的均匀噪声。对功率/亮度合理，对环境量是灾难：
        /// 温度量程 30，3% 就是 ±0.9°C，10Hz 下曲线会变成一条毛刺。
        ///
        /// 所以把"要不要加噪声"这件事也交给本类判断，与波形模型放在一起 ——
        /// 分成两处判断，迟早出现"波形改对了、噪声还在"的组合。
        /// </summary>
        public static bool IsSmooth(TwinMetricDefinition metric)
        {
            switch (ShapeOf(metric))
            {
                case MetricShape.RoomTemperature:
                case MetricShape.SupplyTemperature:
                case MetricShape.CarbonDioxide:
                case MetricShape.Humidity:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// 这个指标是不是**开关量**（Bool）。
        ///
        /// 判据：量程从 0 起、宽度不超过 1。本项目里只有门的 `Open`（0/1）满足。
        ///
        /// 【为什么必须区分】2026-10-22 用户报「2-1教室里的门明明处于开启状态，状态却写的关闭」。
        /// 根因不是绑定错、也不是阈值错，而是**开关量被当成连续量模拟**：
        /// 阈值带是 0~1，基线内收 12% 之后在 0.12~0.88 之间做正弦，
        /// 而界面（`TwinControlRow.StateText` / 卡片 / 标记牌）都按「≥0.5 算开」判断 ——
        /// 于是这扇门每隔十几秒就自己翻一次"开/关"。
        ///
        /// 开关量只有一个稳定值，不存在"中间态"。Mock 阶段固定为量程上限（开），
        /// 真实状态由设备代理收到命令后改变（`MockDataSource.SetOverride` 优先于波形）。
        ///
        /// ⚠️ 这个判据只影响量程宽度 ≤ 1 的指标：功率 0~120W、亮度 0~100%、
        /// 阀门 0~100%、温度 10~35℃ 全都不受影响。将来真要加一个 0~1 的**连续**指标
        /// （比如归一化系数），得在这里开个口子，不能默默按开关量处理。
        /// </summary>
        public static bool IsDiscrete(TwinMetricDefinition metric)
        {
            if (metric == null) return false;
            if (metric.Min < -0.0001f || metric.Min > 0.0001f) return false;
            return metric.Max - metric.Min <= 1.0001f;
        }

        /// <summary>自定义 Clamp —— 刻意不用 Mathf，保证本类不依赖 Unity 运行时。</summary>
        private static float Clamp(float v, float lo, float hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }
    }
}

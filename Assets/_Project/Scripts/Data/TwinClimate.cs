using System;

namespace SchoolTwin.Data
{
    /// <summary>
    /// 室外气候模型：**室温数据的上游**，也是「天气 / 时间系统」的接入点。
    ///
    /// ══════════════════════════════════════════════════════════════
    /// 【为什么要单独建这个类（主理人 2026-11-03 的提问）】
    ///
    ///    「这个室温的数据检测后面做时间天气系统，
    ///      会随着天气时间温度变化而变化吗？」
    ///
    /// 答案是：**必须会，而且必须从一开始就按这个结构写**。
    ///
    /// ── 我第一版写错在哪（诚实记下来）────────────────────────────
    /// 第一版把室温做成"22 分钟周期的正弦 + 5.5 分钟次波"，
    /// 理由是"房间有热惯性，十几分钟变 1°C"。
    ///
    /// 那个推理**对了一半，错在把时间尺度搞反了**：
    /// 热惯性决定的不是"室温自己以 22 分钟为周期起伏"，
    /// 而是"室温**跟随室外温度**变化时会衰减、会滞后"。
    ///
    /// 室温的驱动源只有一个：**室外的天气与时刻**（加上室内得热）。
    /// 22 分钟的正弦波没有物理来源 —— 那是我编的，正属于主理人说的
    /// 「不要胡编乱造」。
    ///
    /// ── 正确的结构：室外 → 建筑惯性 → 室内 ─────────────────────
    ///
    ///     室外温度（由时刻 + 天气决定，24 小时周期）
    ///          │
    ///          ├─ 一阶热惯性：幅度衰减到 ~30%，相位滞后 ~3 小时
    ///          │
    ///          ├─ 室内得热：人、设备、照明（上课时段更高）
    ///          │
    ///          └─ 供暖/空调：把室温拉回设定区间（只在运行时生效）
    ///                      ↓
    ///                  室内温度
    ///
    /// 这样做的直接好处：**天气系统一旦上线，只改 `Weather` 与 `DayMeanC` 两个值，
    /// 室温曲线立刻跟着变**，不需要碰数据源、不需要碰 UI。
    /// 如果按第一版那种"室温自己一条正弦"，天气系统做完还得把这段代码删掉重写。
    ///
    /// ── 现在的默认行为（天气系统还没做）──────────────────────────
    /// 用**真实时钟**取当前小时，按月给一个本季节的日均气温，
    /// 天气默认"多云"。所以在没有天气系统的情况下，室温也是
    /// "按当前月份 + 当前时刻"算出来的，不是拍脑袋的数。
    ///
    /// ⚠️ 线程约束：本类只读 `DateTime`、只用 `System.Math`，
    /// **不触碰任何 Unity API**，可在 Mock 数据源的后台线程调用。
    /// （P1-D 曾因在写线程里读 Time.realtimeSinceStartup 导致写线程静默死掉。）
    /// ══════════════════════════════════════════════════════════════
    /// </summary>
    public static class TwinClimate
    {
        /// <summary>天气类型。天气系统上线后由它来设。</summary>
        public enum Weather
        {
            /// <summary>晴：日较差大（太阳一出来就升温，日落后掉得快）。</summary>
            Sunny = 0,

            /// <summary>多云 / 阴：日较差小，整体略低。</summary>
            Cloudy = 1,

            /// <summary>雨：日较差很小，湿度高，气温偏低。</summary>
            Rainy = 2,

            /// <summary>雪：日均在冰点附近，日较差小。</summary>
            Snowy = 3,
        }

        // ---------------------------------------------------------------- 外部可写状态

        /// <summary>
        /// 当前天气。天气系统上线后调 <see cref="SetWeather"/> 改它。
        ///
        /// 用 volatile：数据源在后台线程读，UI / 天气系统在主线程写。
        /// enum 的底层是 int，32 位读写本身是原子的，volatile 只是防重排与缓存。
        /// </summary>
        private static volatile int _weather = (int)Weather.Cloudy;

        /// <summary>
        /// 是否用真实时钟。天气系统若自带"可拖动的时刻"，把它设成 false
        /// 并调 <see cref="SetHourOverride"/>，室温就会跟着拖动条走。
        /// </summary>
        private static volatile bool _useRealClock = true;

        /// <summary>时间系统给出的当前小时（0~24）。仅当 _useRealClock == false 时生效。</summary>
        private static double _hourOverride = 12.0;

        /// <summary>
        /// 供暖季模式。**默认按月份自动判定**，见 <see cref="IsHeatingSeason"/>。
        ///
        /// ⚠️ 这里原来写的是 `= true`（默认永远供暖），是个明确的错误：
        /// 7 月室温会被暖气钳到 20°C，而夏天真实的教室是 28~31°C。
        /// 主理人这轮要求的正是「数据符合现实逻辑」，这种"全年供暖"就是最典型的
        /// 不符合现实的写法。现在改成三态：自动 / 强制开 / 强制关。
        /// </summary>
        private static volatile int _heatingMode = HeatingAuto;

        /// <summary>按月份自动判定供暖季。</summary>
        private const int HeatingAuto = -1;

        /// <summary>强制不进供暖季（南方学校 / 夏季演示用）。</summary>
        private const int HeatingOff = 0;

        /// <summary>强制进供暖季。</summary>
        private const int HeatingOn = 1;

        /// <summary>供暖季室温目标（°C）。暖气把房间稳在 20°C 附近，是校园的常态。</summary>
        private const double HeatingTargetC = 20.0;

        /// <summary>
        /// 供暖对「室外渗入项」的**残余系数**：0 = 暖气完全压住，1 = 完全不受暖气影响。
        ///
        /// ── 这个值是怎么定的（改了两次，记下推理）─────────────────────
        /// 第一版取 **0.2**，理由是"暖气很强但不是绝对的（靠窗低 1~2°C）"。
        /// 实算一月份：室外 −4~0°C → 室内 **15.5~16.2°C** —— 供暖季的教室 16°C 是错的，
        /// 那正是主理人说的"不符合现实"。
        ///
        /// 错在**把「得热」也一起压掉了**。暖气的作用对象是"室外渗进来的冷"；
        /// 而人、电脑、照明的得热是房间自己产生的，暖气既不加也不减。
        /// 所以压的只能是天气项，得热必须全额保留（见 IndoorC 的分步）。
        ///
        /// 改后取 **0.04**，实算：
        ///   1 月 多云  室内 19.3~20.3°C，日内变幅 **1.0°C**
        ///   11 月 多云 室内 19.7~20.7°C
        /// 这与真实暖气教室一致：一整天温度几乎不动，稳定在 20°C 上下。
        /// 残余 4% 留给"靠窗位置偏低"和"教室之间的差异"。
        /// </summary>
        private const double HeatingStiffness = 0.04;

        // ================================================================ 对外（天气系统接入）

        /// <summary>天气系统调这个。</summary>
        public static void SetWeather(Weather w)
        {
            _weather = (int)w;
        }

        public static Weather CurrentWeather
        {
            get { return (Weather)_weather; }
        }

        /// <summary>
        /// 时间系统调这个：给它一个"现在几点"，之后室温就跟着走。
        /// <paramref name="enable"/> = false 表示恢复用真实时钟。
        /// </summary>
        public static void SetHourOverride(double hour, bool enable)
        {
            if (hour < 0.0) hour = 0.0;
            if (hour >= 24.0) hour = hour % 24.0;
            _hourOverride = hour;
            _useRealClock = !enable;
        }

        /// <summary>供暖季开关。<see cref="SetHeatingSeason"/> 有对应说明。</summary>
        public static bool HeatingSeasonOn
        {
            get { return ResolveHeatingSeason(); }
        }

        /// <summary>
        /// 设置供暖季。
        /// <paramref name="on"/> = true / false 是**强制**开或关；
        /// 传 null 表示恢复「按月份自动判定」。
        /// </summary>
        public static void SetHeatingSeason(bool? on)
        {
            _heatingMode = on.HasValue ? (on.Value ? HeatingOn : HeatingOff) : HeatingAuto;
        }

        /// <summary>恢复自动按月份判定供暖季。</summary>
        public static void SetHeatingSeasonAuto()
        {
            _heatingMode = HeatingAuto;
        }

        /// <summary>
        /// 当前是否处于供暖季。
        ///
        /// 现实依据：北方集中供暖期通常为 **11 月 15 日 ~ 次年 3 月 15 日**，
        /// 学校往往提前几天开炉。这里按整月取 **11 / 12 / 1 / 2 / 3 月**，
        /// 便于演示又不违背常识（3 月中旬可能还在供暖）。
        ///
        /// 非供暖季则**完全不钳制** —— 春天地板 15°C、夏天 31°C 都是真的，
        /// 不该为了"好看"篡改成 20°C。
        /// </summary>
        private static bool ResolveHeatingSeason()
        {
            if (_heatingMode == HeatingOn) return true;
            if (_heatingMode == HeatingOff) return false;
            return IsHeatingMonth(DateTime.Now.Month);
        }

        /// <summary>哪些月份属于供暖季。抽成方法便于 <see cref="IndoorC"/> 与外部查询共用同一口径。</summary>
        public static bool IsHeatingMonth(int month)
        {
            return month == 11 || month == 12 || month == 1 || month == 2 || month == 3;
        }

        /// <summary>当前小时（0~24）。这是**全局唯一的时刻来源**。</summary>
        public static double HourOfDay
        {
            get
            {
                if (!_useRealClock) return _hourOverride;
                DateTime now = DateTime.Now;
                return now.Hour + now.Minute / 60.0 + now.Second / 3600.0;
            }
        }

        // ================================================================ 室外温度

        /// <summary>
        /// 本季节的**日均气温**（°C）。
        ///
        /// ── 取值依据（华北地区，校园场景）────────────────────────────
        ///   · 12 / 1 / 2 月：−2°C   隆冬，供暖期核心
        ///   · 3 / 11 月：     8°C   供暖期边缘，早晚接近 0
        ///   · 4 / 5 / 10 月：16°C   春秋，昼夜温差最大
        ///   · 6 / 9 月：     24°C   初夏 / 初秋
        ///   · 7 / 8 月：     29°C   盛夏
        /// 未知月份退回 18°C（温和，不出错）。
        ///
        /// ⚠️ 天气系统上线后，这个方法应当改读它的"当日预报"，
        ///    这里保留季节表是为了**在没有天气系统时也说得通**。
        /// </summary>
        public static double SeasonMeanC(int month)
        {
            switch (month)
            {
                case 12: case 1: case 2: return -2.0;
                case 3: case 11: return 8.0;
                case 4: case 5: case 10: return 16.0;
                case 6: case 9: return 24.0;
                case 7: case 8: return 29.0;
                default: return 18.0;
            }
        }

        /// <summary>
        /// 日较差（当天最高气温 − 最低气温，°C）—— 由**天气**决定。
        ///
        /// 这是天气影响室温最直接的一条：
        ///   · 晴天日较差大（9°C）：太阳一出来就热，日落后掉得快；
        ///   · 阴天 / 雨天日较差小（3~4°C）：云层像一床被子，白天挡太阳、夜里保温；
        ///   · 下雪天日较差也不大（5°C）。
        ///
        /// 所以"今天阴天"和"今天晴天"的室温曲线**形状不同**，
        /// 不只是整体平移 —— 这一条正是天气系统该带来的效果。
        /// </summary>
        public static double DailyRangeC(Weather w)
        {
            switch (w)
            {
                case Weather.Sunny: return 9.0;
                case Weather.Cloudy: return 4.0;
                case Weather.Rainy: return 3.0;
                case Weather.Snowy: return 5.0;
                default: return 5.0;
            }
        }

        /// <summary>天气带来的整体偏移（°C）：阴天比晴冷，雨天更冷。</summary>
        private static double WeatherBiasC(Weather w)
        {
            switch (w)
            {
                case Weather.Sunny: return 0.5;
                case Weather.Cloudy: return 0.0;
                case Weather.Rainy: return -1.0;
                case Weather.Snowy: return -0.5;
                default: return 0.0;
            }
        }

        // ================================================================ 湿度（物理量，不是百分比拍脑袋）

        /// <summary>
        /// 室外相对湿度（%）。按**华北气候**取值。
        ///
        /// ── 为什么不能用一个常数 ─────────────────────────────────────
        /// 第一版把湿度写成 `52.0 - (室内温度 - 20) * 4.5` 的线性式，
        /// 结果 7 月算出 **−3%RH**（负数）、4 月雨天算出 **115%RH**（超过饱和）。
        /// 根源是把"相对湿度"当成了可以随便线性外推的数 ——
        /// 它其实是「含湿量 ÷ 饱和含湿量」，而饱和含湿量随温度**指数增长**。
        ///
        /// ── 华北取值 ─────────────────────────────────────────────────
        ///   · 晴：38%（干燥，尤其春秋）
        ///   · 多云：52%
        ///   · 雨：90%（几乎饱和）
        ///   · 雪：72%
        /// 春秋取 38/52 而不是通用教材的 50/65 —— 华北春天真的很干。
        /// </summary>
        public static double OutdoorRhOf(Weather w)
        {
            switch (w)
            {
                case Weather.Sunny: return 38.0;
                case Weather.Cloudy: return 52.0;
                case Weather.Rainy: return 90.0;
                case Weather.Snowy: return 72.0;
                default: return 52.0;
            }
        }

        /// <summary>
        /// 饱和水汽密度（g/m³）。用 Magnus 公式算饱和水汽压，再换算成密度。
        ///
        /// 这是湿度模型的**地基**：20°C 约 17.3 g/m³、30°C 约 30.3 g/m³。
        /// 温度升 10°C，饱和含湿量几乎翻倍 —— 所以同样是 9 g/m³ 的绝对含湿量，
        /// 在 10°C 是 96%RH（快结露），在 20°C 只有 52%RH。
        /// 这也是"冬天暖气房很干"的原因：室外冷空气含湿量本来就低，
        /// 进屋加热后相对湿度被稀释了。
        /// </summary>
        public static double SatVaporDensity(double tempC)
        {
            // Magnus/Tetens：es 单位 hPa
            double es = 6.112 * Math.Exp(17.67 * tempC / (tempC + 243.5));
            // 理想气体：ρ = 216.7 · es / T(K)，单位 g/m³
            return 216.7 * es / (tempC + 273.15);
        }

        /// <summary>
        /// 建筑蓄湿下限（g/m³ 绝对含湿量）。
        ///
        /// 现实里没有"绝对干燥"的房间：墙体、地面、家具、绿植、
        /// 保洁拖地、管道漏水都会持续释放水汽。
        /// 北方冬季室外可能只有 2~3 g/m³，但室内实测很少低于 30%RH@20°C
        /// （≈ 5.2 g/m³）。取 5.5 让 1 月晴天落在 31~33%RH 而不是 12% ——
        /// 12% 是"沙漠级干燥"，那个数会让温度传感器整个冬天都挂"偏低"，不真实。
        ///
        /// ── 【P27 收工复核】5.5 → 6.5，因为余量只剩 0.09%RH ──────────────
        /// 5.5 本身是对的，但它贴着阈值：`Humidity.WarnLow = 30`，
        /// 而 11 月晴天（日均 8°C、室外 RH 38%）算出的**最干时刻**是 31.3%RH，
        /// 再叠上 `GenerateHumidity` 里 ±1.2%RH 的点位差，最不利的那一台到 **30.09**。
        /// 只剩 0.09 —— 也就意味着：
        ///   · 将来天气系统给出一个比"华北晴 38%"更干的日子（比如春季沙尘 30%），
        ///     湿度会立刻挂 Warning，而那是**天气**的表现，不该表现成"传感器故障"；
        ///   · 阈值口径一旦被收紧（比如按国标 GB/T 18883 改到 40%），
        ///     这段会整体失守，而失守的方向看起来像"数据错了"。
        ///
        /// 取 6.5 后的实算（同样 11 月晴天最干时刻）：36.4%RH → 点位差后 35.2，
        /// 余量 **5.2%RH**。物理含义也站得住：6.5 g/m³ @20°C ≈ 37%RH，
        /// 正是北方供暖季室内加湿前的典型值（有暖气、没加湿器，实测 30~40%）。
        /// </summary>
        private const double MoistureFloorGm3 = 6.5;

        /// <summary>
        /// 室内相对湿度（%）。**由室外含湿量 + 室内产湿派生**，与室温／天气同源。
        ///
        /// ── 五个环节 ────────────────────────────────────────────────
        /// ① 室外含湿量 = 室外RH% × 室外温度的饱和含湿量
        /// ② 渗入：室外空气进来，含湿量一并带进来
        /// ③ 产湿：人呼吸/出汗、饮水、拖地。满员（得热 1.2）时 +3 g/m³ 左右，
        ///    空楼（得热 0.2）时接近 0
        /// ④ 蓄湿下限：建筑本身持续释放水汽，不允许算出"沙漠"
        /// ⑤ 凝结上限：含湿量不可能超过饱和值，超了就析出成水
        ///    （没有这一步，4 月雨天会算出 115%RH —— 物理上不可能）
        /// 最后 ⑥ 除以**室内温度**的饱和含湿量 → 相对湿度。
        ///
        /// ⚠️ 第⑥步用的是室内温度，这就是"湿度与室温反相关"的**真正来源** ——
        ///    不是硬编码一个负斜率，而是同一份含湿量除以一个更大的分母。
        ///    所以天气一变冷、室温一降，湿度会自洽地往上走，不需要额外调参。
        /// </summary>
        public static double IndoorRhNowC()
        {
            Weather w = CurrentWeather;
            double hour = HourOfDay;

            double outdoor = OutdoorC(hour, CurrentDayMeanC(), w);
            double indoor = IndoorC(hour, CurrentDayMeanC(), w);

            // ①② 室外渗入的含湿量
            double absMoisture = OutdoorRhOf(w) / 100.0 * SatVaporDensity(outdoor);

            // ③ 室内产湿：与得热共用同一张作息表（有人在就有水汽）
            absMoisture += 0.5 + 3.0 * (OccupancyGainC(hour) - 0.2);

            // ④⑤ 物理上下限
            if (absMoisture < MoistureFloorGm3) absMoisture = MoistureFloorGm3;
            double sat = SatVaporDensity(indoor);
            if (absMoisture > sat * 0.94) absMoisture = sat * 0.94;   // 凝结

            // ⑥ 换算成相对湿度
            return absMoisture / sat * 100.0;
        }

        /// <summary>
        /// 室外温度（°C）：**唯一的驱动源**。
        ///
        /// 形状：24 小时正弦，峰值在 **14:30**（地面蓄热 + 太阳滞后），
        /// 谷值在 **02:30**。真实气象站的最低气温通常在日出前（5~6 点），
        /// 但用单正弦表达会有这个偏差 —— 要精确就该上双谐波，
        /// 那对这个项目属于过度设计，这里取"形状对、量级对、周期对"。
        /// </summary>
        public static double OutdoorC(double hour, double dayMean, Weather w)
        {
            double amp = DailyRangeC(w) * 0.5;
            double phase = 2.0 * Math.PI * (hour - 14.5) / 24.0;
            return dayMean + WeatherBiasC(w) + amp * Math.Cos(phase);
        }

        // ================================================================ 室内温度

        /// <summary>
        /// 室内温度（°C）—— **由室外温度派生，不是独立正弦**。
        ///
        /// ── 三步（**顺序不能换**）─────────────────────────────────────
        /// ① **热惯性 → 天气项**：建筑对室外温度做一阶低通。周期性的输入经过一阶系统后，
        ///    幅度衰减到 `1/√(1+(ωτ)²)`、相位滞后 `arctan(ωτ)`。
        ///    取时间常数 τ ≈ 11.5 小时（砖混结构 + 家具 + 空气的等效值）：
        ///      室外 24h 周期 → 室内幅度 ≈ 30%，峰值从 14:30 推到 **17:30**；
        ///    这就是"下午最热的时候，教室里还没热透"的物理来源。
        /// ② **供暖钳制（只压天气项）**：暖气对抗的是室外渗入的冷，
        ///    所以它只作用在①这一项上，把它压到 20°C 上下。
        /// ③ **室内得热**：人、电脑、充电宝、照明都在放热。
        ///    这是**房间内部产热**，暖气既不加也不减 → 必须全额保留、放在最后加。
        ///
        /// ⚠️ ②③ 的顺序是第一版的错误来源：原来先加得热再整体钳制，
        ///    等于把"人产生的热"也压掉了，一月份算出 15.5°C（供暖季教室偏冷 4°C）。
        ///
        /// ── 结果示例 ─────────────────────────────────────────────────
        /// **供暖季（1 月，多云，日均 −2°C）**
        ///   室外 −4 ~ 0°C；室内 **19.3 ~ 20.3°C，日内变幅 1.0°C**
        ///   ← 真实暖气教室就是这样：一整天几乎不动，稳定在 20°C 附近
        ///
        /// **非供暖季（7 月，晴，日均 29°C）**
        ///   室外 25 ~ 34°C；室内 **28.3 ~ 31.9°C，日内变幅 3.6°C**
        ///   ← 没有空调的教室，下午真的热（峰值滞后到 17:30）
        ///
        /// 这两个例子正是"供暖季 / 非供暖季"必须分开的原因：
        /// 如果全年钳到 20°C，夏天那一段就成了假数据。
        /// </summary>
        /// <param name="hour">当前小时（0~24）。</param>
        /// <param name="dayMean">当日室外均温。</param>
        /// <param name="w">天气。</param>
        public static double IndoorC(double hour, double dayMean, Weather w)
        {
            // ---- ① 热惯性：滞后 3 小时 + 幅度 ×0.3 ----
            double lagged = hour - 3.0;
            if (lagged < 0.0) lagged += 24.0;

            double outAmp = DailyRangeC(w) * 0.5;
            double inAmp = outAmp * 0.30;
            double phase = 2.0 * Math.PI * (lagged - 14.5) / 24.0;

            double indoors = dayMean + WeatherBiasC(w) + inAmp * Math.Cos(phase);

            // ---- ② 供暖季钳制：**只压天气项，不动得热** ----
            //
            // ⚠️ 顺序很重要。第一版是先加得热再整体钳制，等于把"人产生的热"
            // 也一起压掉了 —— 一月份算出 15.5~16.2°C，供暖季教室偏冷 4°C。
            //
            // 物理上正确的对应关系：暖气对抗的是"室外渗入的冷"，
            // 而人/电脑/照明的得热是房间内部的产热，暖气既不加也不减。
            // 所以钳制必须放在得热**之前**。
            if (ResolveHeatingSeason())
                indoors = HeatingTargetC + (indoors - HeatingTargetC) * HeatingStiffness;

            // ---- ③ 室内得热（按校园作息）----
            //
            // 全额保留，不受供暖影响。这是"上课时教室里温度会往上走一点"的来源，
            // 也是供暖季日内 1°C 变幅的主要贡献者（暖气把天气项压平了，剩下能动的就是它）。
            indoors += OccupancyGainC(hour);

            return indoors;
        }

        /// <summary>
        /// 室内得热（°C）。按校园作息分段，段间**平滑过渡** —— 这张表本身就是"人在不在楼里"的写照。
        ///
        /// ── 逐段依据 ─────────────────────────────────────────────────
        ///   · 00:00~06:00  空楼（只有待机设备）        +0.20
        ///   · 07:00        陆续到校，照明打开          +0.50
        ///   · 08:30~11:30  上午满课，人和设备满载      +1.20
        ///   · 12:30        午休，人离开大半            +0.40
        ///   · 14:00~17:00  下午课                      +1.10
        ///   · 18:00        放学后，社团/自习           +0.60
        ///   · 20:00~       熄灯，只剩值班              +0.20
        ///
        /// ── 为什么必须平滑（改过两次）────────────────────────────────
        /// 第一版是纯阶跃：`if (hour < 12.0) return 1.2; if (hour < 13.5) return 0.4;`。
        /// 后果是 **12:00:00 那一刻室温瞬间掉 0.8°C** —— 一个 50 吨的建筑不可能这样。
        /// 而且"峰值出现在 11.8 时、然后陡降"正是阶跃造成的假峰，
        /// 真正的峰值应该在 17 时附近（滞后室外 3 小时）。
        ///
        /// 原注释里写着"分段 + 前端的小过渡（±0.1）足够"，但**代码里根本没有过渡** ——
        /// 注释描述了不存在的实现，比没有注释更坏。
        ///
        /// ── 现在 ─────────────────────────────────────────────────────
        /// 用一张时刻表 + smoothstep 插值。实测最大变化速率约 **1.1°C/小时**，
        /// 与真实房间（墙体蓄热决定）同一量级。房间不会因为下课铃响就把热扔出去。
        /// </summary>
        private static double OccupancyGainC(double hour)
        {
            if (hour < 0.0) hour += 24.0;
            if (hour >= 24.0) hour -= 24.0;

            // 时刻表（小时）与对应的得热（°C）。首尾都是 0.2，保证 24:00 接回 0:00 不断开。
            // 与 GainAt 共用 —— 缓动区间直接由相邻两点得出，不另写一份比例。
            for (int i = 0; i < GainHours.Length - 1; i++)
            {
                double h0 = GainHours[i];
                double h1 = GainHours[i + 1];
                if (hour < h1)
                {
                    // smoothstep：两端切线为 0，不会在段边界留折角
                    double t = (hour - h0) / (h1 - h0);
                    if (t < 0.0) t = 0.0;
                    if (t > 1.0) t = 1.0;
                    t = t * t * (3.0 - 2.0 * t);
                    return GainValues[i] + (GainValues[i + 1] - GainValues[i]) * t;
                }
            }
            return GainValues[GainValues.Length - 1];
        }

        /// <summary>得热时刻表：整点边界（小时）。</summary>
        private static readonly double[] GainHours =
        { 0.0, 6.0, 7.0, 8.5, 11.5, 12.5, 13.0, 14.0, 17.0, 18.0, 20.0, 24.0 };

        /// <summary>得热时刻表：对应得热（°C）。与 <see cref="GainHours"/> 一一对应。</summary>
        private static readonly double[] GainValues =
        { 0.2, 0.2, 0.5, 1.2, 1.2, 0.4, 0.4, 1.1, 1.1, 0.6, 0.2, 0.2 };

        /// <summary>
        /// 任意时刻的得热（°C）。**与 <see cref="OccupancyGainC"/> 同一张表**。
        ///
        /// CO₂ 的"有人/没人"判定原先在 MockSignalGenerator 里另写了一套硬编码时段，
        /// 于是室温说"上午满课"、CO₂ 说"没人"这类分裂随时可能出现。
        /// 现在两处都问这里：CO₂ = 得热是否明显高于空楼本底。
        /// </summary>
        public static double GainAt(double hour)
        {
            return OccupancyGainC(hour);
        }

        // ================================================================ 便捷入口

        /// <summary>
        /// 当前时刻的室内温度（°C）。数据源直接调这个。
        ///
        /// 内部把 `DateTime` / 覆盖时刻统一成 hour，再走上面那套模型 ——
        /// 这样"真实时钟"与"时间系统拖动"两条路径共用同一份物理逻辑，
        /// 不会出现"演示拖了时间、室温却没动"的分裂。
        /// </summary>
        public static double IndoorNowC()
        {
            return IndoorC(HourOfDay, CurrentDayMeanC(), CurrentWeather);
        }

        /// <summary>当前时刻的室外温度（°C）。面板要显示"室外 8°C"时用它。</summary>
        public static double OutdoorNowC()
        {
            return OutdoorC(HourOfDay, CurrentDayMeanC(), CurrentWeather);
        }

        /// <summary>
        /// 当日室外均温（°C）。
        ///
        /// 月份一律取**真实日历** —— 即使时间系统只提供了"几点"（`SetHourOverride`），
        /// 季节仍由真实日期决定。这样"拖动时刻看傍晚的室温"能正常工作，
        /// 不会因为"没有月份"就退回一个拍脑袋的常数。
        ///
        /// ⚠️ 天气系统上线后，这个方法应当改读它给的"当日平均气温"，
        /// 并给时间系统补一个 `SetMonth`。届时只改这一个方法即可，
        /// `IndoorNowC` / `OutdoorNowC` 的调用方一行都不用动。
        /// </summary>
        public static double CurrentDayMeanC()
        {
            return SeasonMeanC(DateTime.Now.Month);
        }
    }
}
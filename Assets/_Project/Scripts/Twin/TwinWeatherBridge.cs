using UnityEngine;
using SchoolTwin.Data;
using SchoolTwin.Weather;

namespace SchoolTwin.Twin
{
    /// <summary>
    /// 【P13】时间天气桥：把「视觉 7 档 + 昼夜 3 档」和「数据 4 档 + 连续小时数」对接起来。
    ///
    /// ══════════════════════════════════════════════════════════════════
    /// 【为什么必须有这层，而不是让面板直接调两个 Set】
    ///
    /// 本项目反复踩过的同一种病：「同一件事有两个状态源」。天气这件事上天然就有两套：
    ///
    ///   视觉侧  SchoolTwin.Weather.EnvironmentState —— 7 档天气 + 3 档昼夜，
    ///           管天空盒、平行光、雾、雨雪粒子、云穹顶、积水、积雪
    ///   数据侧  SchoolTwin.Data.TwinClimate      —— 4 档天气 + 1 个连续小时数，
    ///           管室温、湿度、室外温度（数据源后台线程每帧在读）
    ///
    /// 如果 UI 里分别写两行：
    ///     env.SetWeather(Weather.Snow);
    ///     TwinClimate.SetWeather(TwinClimate.Weather.Snowy);
    /// 那只要有人在别处只调了其中一行（自动轮换 WeatherDirector 会调第一行！
    /// 见 WeatherDirector.Rotate），就会出现「天在下雪、室温按晴天算」——
    /// 两条数据都"正常"，只有把它们放一起看才发现矛盾。
    ///
    /// 所以本类收口所有对外写入：**任何一次改天气/改时刻，两个 Set 都在同一个方法体内完成，
    /// 不存在只调一半的路径**。面板、自动轮换、将来的语音指令全都走这里。
    ///
    /// ══════════════════════════════════════════════════════════════════
    /// 【7 档 ↔ 4 档 的映射：为什么是多对一】
    ///
    ///   视觉档（7）      →  数据档（4）        理由
    ///   Clear            →  Sunny            晴就是晴
    ///   Cloudy           →  Cloudy           多云
    ///   Overcast         →  Cloudy           阴：日照少、日较差小，对室温的作用与多云同族
    ///   Rain             →  Rainy            雨
    ///   Storm            →  Rainy            雷雨：对室温的唯一附加影响是雨量，模型里没有雨量项
    ///   Fog              →  Cloudy           雾：同样是"太阳被挡"，且雾常伴阴天
    ///   Snow             →  Snowy            雪
    ///
    /// 多对一是有意的，不是偷懒：TwinClimate 的 WeatherBiasC 只区分
    /// 「晴天日较差大 / 阴天日较差小 / 雨天整体偏低 / 雪天接近冰点」四种物理行为，
    /// 硬给雾和雷雨各分一档，唯一结果是多两个数值几乎相同的分支。
    ///
    /// 反方向（4 → 7）必须给**代表性**视觉档，因为从数据档推不出更细的信息：
    ///   Sunny → Clear，Cloudy → Cloudy，Rainy → Rain，Snowy → Snow
    /// 注意 Cloudy 回到 **Cloudy** 而不是 Overcast —— 取"信息量最少的那个"，
    /// 避免把"阴"这个更强的视觉断言凭空塞进去。
    ///
    /// ══════════════════════════════════════════════════════════════════
    /// 【时刻 → 昼夜 的映射：数据侧只有一个连续小时数】
    ///
    /// 视觉侧要的是离散的 Day/Dusk/Night，数据侧只有 HourOfDay（0~24 连续）。
    /// 分段按真实天文常识定（华北，非赤道）：
    ///   05:00–07:00  过渡到白天，取 06:00 为界
    ///   17:00–19:00  过渡到黄昏/夜晚，取 18:00 为界
    /// 具体：
    ///   06:00 ≤ h < 17:00  → Day
    ///   17:00 ≤ h < 19:00  → Dusk
    ///   其余（19:00–次日 06:00）→ Night
    ///
    /// Dusk 只占 2 小时是刻意的：黄昏在视觉上就是个短促过渡，
    /// 给它 4 小时会让"晚上 8 点天还橙着"，答辩现场反而像 bug。
    /// </summary>
    public static class TwinWeatherBridge
    {
        // ================================================================ 7 → 4

        /// <summary>视觉 7 档 → 数据 4 档。多对一见类注释的表。</summary>
        public static TwinClimate.Weather ToData(Weather v)
        {
            switch (v)
            {
                case Weather.Clear: return TwinClimate.Weather.Sunny;
                case Weather.Cloudy: return TwinClimate.Weather.Cloudy;
                case Weather.Overcast: return TwinClimate.Weather.Cloudy;
                case Weather.Rain: return TwinClimate.Weather.Rainy;
                case Weather.Storm: return TwinClimate.Weather.Rainy;
                case Weather.Fog: return TwinClimate.Weather.Cloudy;
                case Weather.Snow: return TwinClimate.Weather.Snowy;
                default: return TwinClimate.Weather.Cloudy;
            }
        }

        // ================================================================ 4 → 7

        /// <summary>数据 4 档 → 视觉 7 档（取代表性档，见类注释）。</summary>
        public static Weather ToVisual(TwinClimate.Weather v)
        {
            switch (v)
            {
                case TwinClimate.Weather.Sunny: return Weather.Clear;
                case TwinClimate.Weather.Cloudy: return Weather.Cloudy;
                case TwinClimate.Weather.Rainy: return Weather.Rain;
                case TwinClimate.Weather.Snowy: return Weather.Snow;
                default: return Weather.Cloudy;
            }
        }

        // ================================================================ 时刻 → 昼夜

        /// <summary>把连续小时数折成视觉侧的昼夜三档。分段见类注释。</summary>
        public static TimeOfDay ToTimeOfDay(double hour)
        {
            // 先规整到 [0,24)：SetHourOverride 已经做过一次，但本方法也可能被
            // 面板直接调用（拖动时每帧一次），不能假设调用方规整过。
            double h = hour % 24.0;
            if (h < 0.0) h += 24.0;

            if (h >= 6.0 && h < 17.0) return TimeOfDay.Day;
            if (h >= 17.0 && h < 19.0) return TimeOfDay.Dusk;
            return TimeOfDay.Night;
        }

        // ================================================================ 唯一写入出口

        /// <summary>
        /// **改天气的唯一入口**。两个 Set 在同一帧、同一个方法体内完成。
        ///
        /// 顺序说明：先写数据侧再写视觉侧。看起来无所谓，但反过来的话，
        /// EnvironmentState.Set 末尾会触发 OnChanged 事件，订阅者（面板/自动轮换）
        /// 若在回调里读 TwinClimate 会读到**旧的**天气值 —— 那一瞬间两套系统矛盾。
        /// 先写数据侧就没有这个窗口。
        /// </summary>
        public static void ApplyWeather(Weather v)
        {
            TwinClimate.SetWeather(ToData(v));

            EnvironmentState env = EnvironmentState.Instance;
            if (env == null) env = Object.FindObjectOfType<EnvironmentState>(true);
            if (env != null) env.SetWeather(v);
        }

        /// <summary>
        /// **改时刻的唯一入口**。<paramref name="hour"/> 可为小数（表盘拖动是连续的）。
        /// <paramref name="enable"/>=false 表示恢复真实时钟（Esc / 点遮罩 / 点× 走这条）。
        ///
        /// 视觉侧的昼夜是**离散**的，所以只有在跨过分段边界时才调 SetTime ——
        /// 否则拖动的每一帧都会调一次 EnvironmentState.Set，虽然 Set 内部有
        /// same 判断（不重复播过渡），但每帧一次多余的 Set 仍会重置
        /// from/to 插值，导致"拖动时天色一直在重新开始渐变"，永远到不了目标色。
        /// 这是本方法里唯一一处不显然的地方。
        /// </summary>
        public static void ApplyHour(double hour, bool enable)
        {
            TwinClimate.SetHourOverride(hour, enable);

            EnvironmentState env = EnvironmentState.Instance;
            if (env == null) env = Object.FindObjectOfType<EnvironmentState>(true);
            if (env == null) return;

            double now = enable ? hour : TwinClimate.HourOfDay;
            TimeOfDay want = ToTimeOfDay(now);
            if (env.timeOfDay != want) env.SetTime(want);
        }

        /// <summary>当前时刻的小时数（0~24）。真实时钟与覆盖时刻两个来源由 TwinClimate 收口。</summary>
        public static double CurrentHour
        {
            get { return TwinClimate.HourOfDay; }
        }

        /// <summary>当前是否在用真实时钟。面板用它决定提示行文案。</summary>
        public static bool UsingRealClock
        {
            get { return true; }
        }

        /// <summary>当前视觉档天气。没挂 EnvironmentState 时退回由数据档反推。</summary>
        public static Weather CurrentVisual
        {
            get
            {
                EnvironmentState env = EnvironmentState.Instance;
                if (env != null) return env.weather;
                return ToVisual(TwinClimate.CurrentWeather);
            }
        }
    }
}
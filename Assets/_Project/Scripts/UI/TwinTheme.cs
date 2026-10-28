using UnityEngine;

namespace SchoolTwin.UI
{
    /// <summary>
    /// 【P28】全项目唯一的视觉 token 表。
    ///
    /// ── 为什么要这个东西 ────────────────────────────────────────────
    /// 重构前的情况：`TwinStatusPalette` 只管状态色和面板底色，
    /// 字号有 10 档（11/12/13/14/15/16/17/18/22/23）、圆角 3 套（10/12/14）、
    /// 间距靠各面板自己拍脑袋，同一个"标题"在详情卡是 22、在总控是 17。
    ///
    /// 这里把**颜色 / 字号 / 圆角 / 间距 / 动效**五类全部收编成 token，
    /// 新面板一律引用，不许再写字面量。
    ///
    /// ── 颜色策略（调研结论 6-3-1）────────────────────────────────────
    /// 深色基底 60% + 低饱和科技青蓝主色 30% + 告警橙 10%。
    /// 「深色是画布，数据与告警才是光源」—— 所以 Accent 只给"选中/进度/图表主线"，
    /// Warn 只给告警，绝不复用为强调色。
    ///
    /// ── 为什么不直接用 URP/URP 的 Color 属性 ─────────────────────────
    /// 本项目是**内置管线**，全部 UGUI 程序化绘制，颜色就是顶点色，
    /// 不存在材质属性可继承，只能靠统一常量。
    /// </summary>
    public static class TwinTheme
    {
        // ================================================================ 颜色

        /// <summary>抽屉底。比 PanelBottom 再深一档，让抽屉从场景里"浮"出来。</summary>
        public static readonly Color ScrimBase = new Color(0.043f, 0.055f, 0.078f, 0.98f);

        // ---- 面板（沿用既有，不动）----
        public static readonly Color PanelTop = new Color(0.078f, 0.106f, 0.157f, 0.94f);
        public static readonly Color PanelBottom = new Color(0.055f, 0.075f, 0.114f, 0.96f);
        public static readonly Color Border = new Color(0.165f, 0.212f, 0.282f, 0.80f);

        // ---- 卡片：比面板亮半档，形成"面板 > 卡片"的层次 ----
        public static readonly Color CardTop = new Color(0.098f, 0.129f, 0.184f, 0.92f);
        public static readonly Color CardBottom = new Color(0.071f, 0.094f, 0.137f, 0.94f);

        // ---- 主色：选中 / 高亮 / 进度 / 图表主线 ----
        public static readonly Color Accent = new Color(0.20f, 0.75f, 1.00f, 1f);
        public static readonly Color AccentDim = new Color(0.20f, 0.75f, 1.00f, 0.16f);
        public static readonly Color AccentLine = new Color(0.20f, 0.75f, 1.00f, 0.55f);

        // ---- 状态色（与 TwinStatusPalette 同值，那边后续改为转发到这里）----
        public static readonly Color StatusUnknown = new Color(0.35f, 0.38f, 0.45f, 1f);
        public static readonly Color StatusNormal = new Color(0.20f, 0.80f, 0.35f, 1f);
        public static readonly Color StatusWarning = new Color(1.00f, 0.62f, 0.15f, 1f);
        public static readonly Color StatusAlarm = new Color(0.90f, 0.20f, 0.20f, 1f);
        public static readonly Color StatusStale = new Color(0.50f, 0.50f, 0.55f, 1f);
        public static readonly Color StatusOffline = new Color(0.28f, 0.28f, 0.30f, 1f);

        /// <summary>告警橙。**只给告警用**，不要拿来当强调色。</summary>
        public static readonly Color Warn = StatusWarning;

        // ---- 文字三级 ----
        public static readonly Color TextPrimary = new Color(0.94f, 0.96f, 0.99f, 1f);
        public static readonly Color TextSecondary = new Color(0.62f, 0.69f, 0.79f, 1f);
        public static readonly Color TextMuted = new Color(0.46f, 0.52f, 0.61f, 1f);

        // ---- 交互态（白色叠加，任何底色上都能用）----
        public static readonly Color Divider = new Color(1f, 1f, 1f, 0.10f);
        public static readonly Color Track = new Color(1f, 1f, 1f, 0.09f);
        public static readonly Color Hover = new Color(1f, 1f, 1f, 0.08f);
        public static readonly Color Pressed = new Color(1f, 1f, 1f, 0.14f);
        public static readonly Color Disabled = new Color(1f, 1f, 1f, 0.04f);

        // ================================================================ 字号（10 档 → 6 档）

        public const int FsCaption = 11;      // 脚注、单位、次要说明
        public const int FsBody = 13;         // 正文、列表副标题、图标名
        public const int FsBodyStrong = 15;   // 列表主文案、按钮
        public const int FsSubtitle = 17;     // 区块标题、模块名
        public const int FsTitle = 22;        // 页面标题、大数值
        public const int FsDisplay = 28;      // 首页 KPI 数字

        // ================================================================ 圆角（3 套 → 4 token）

        public const float R8 = 8f;      // 行、按钮
        public const float R12 = 12f;    // 卡片、胶囊组
        public const float R16 = 16f;    // 抽屉、大容器

        /// <summary>胶囊：半径 = 高度一半。调用方自己算，这里只做语义说明。</summary>
        public static float Pill(float height) { return Mathf.Max(0f, height * 0.5f); }

        // ================================================================ 间距（4 的倍数）

        public const float S4 = 4f;
        public const float S8 = 8f;
        public const float S12 = 12f;
        public const float S16 = 16f;
        public const float S24 = 24f;
        public const float S32 = 32f;

        // ================================================================ 动效（秒）

        public const float Fast = 0.12f;
        public const float Mid = 0.20f;
        public const float Slow = 0.32f;

        /// <summary>抽屉滑入 / 滑出。</summary>
        public const float DrawerIn = 0.24f;
        public const float DrawerOut = 0.18f;

        // ================================================================ 缓动

        /// <summary>easeOutCubic：快进慢出，界面"入场"的默认选择。</summary>
        public static float EaseOutCubic(float t)
        {
            t = Mathf.Clamp01(t);
            float u = 1f - t;
            return 1f - u * u * u;
        }

        /// <summary>easeInCubic：慢进快出，用于"退场"。</summary>
        public static float EaseInCubic(float t)
        {
            t = Mathf.Clamp01(t);
            return t * t * t;
        }

        /// <summary>easeInOutCubic：两端缓，用于宽度变化这类"形变"。</summary>
        public static float EaseInOutCubic(float t)
        {
            t = Mathf.Clamp01(t);
            return t < 0.5f
                ? 4f * t * t * t
                : 1f - Mathf.Pow(-2f * t + 2f, 3f) * 0.5f;
        }

        // ================================================================ 工具

        /// <summary>同色改透明度。比 `new Color(c.r,c.g,c.b,a)` 少写三个字段，也不容易写错。</summary>
        public static Color WithAlpha(Color c, float a)
        {
            c.a = a;
            return c;
        }

        /// <summary>在底色上叠一层白（Hover/Pressed 用）。</summary>
        public static Color Lighten(Color c, float amount)
        {
            return new Color(
                Mathf.Lerp(c.r, 1f, amount),
                Mathf.Lerp(c.g, 1f, amount),
                Mathf.Lerp(c.b, 1f, amount),
                c.a);
        }
    }
}

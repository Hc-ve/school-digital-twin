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
    ///
    /// ── P29 补充（科技风统一）──────────────────────────────────────
    /// 新增主色渐变两端（AccentHi/AccentDeep）、发光双层（GlowOuter/GlowInner）、
    /// 底纹三件套（GridLine/GridMajor/TopHighlight）、描边四件套
    /// （StrokeHair/StrokeAccent/BarAccent/BarStatus）、呼吸周期 Breathe。
    /// 描边类 token 是「颜色 + 线宽」的 StrokeStyle 结构体 —— 分开写两个常量
    /// 迟早会出现「1px 配 AccentLine、2px 配 Border」这种错配。
    /// 已有的字段名一个都没改，调用点不受影响。
    ///
    /// ── 【硬约定】Linear 色彩空间：半透明 token 必须按「屏幕取色」标定 ─────────
    /// 本工程 `m_ActiveColorSpace = 1`（Linear），UI 混色在线性空间里做，
    /// 同一个 alpha 上屏后的观感比设计稿上的 sRGB 百分比亮 2~3 倍。实测（1000×720 截图取色）：
    ///   alpha 0.16 → 屏幕约 102/255；alpha 0.37 → 约 148/255。
    /// 所以：
    ///   ① **禁止**把 sRGB 稿上的 alpha 百分比直接抄进代码；
    ///   ② 所有半透明 token 以「截图取色的亮度差（Δ/255）」标定，不认百分比；
    ///   ③ 新增半透明 token 必须附一张取色验证截图，写明采样点与 Δ 值；
    ///   ④ 烘进 8bit 贴图的 token（网格、渐变）实际生效值是 round(alpha×255)/255，
    ///      相邻两档可能差 5 个 Δ —— 标定时要按「档位」反解，别按小数点连续微调。
    /// 参考量级（面板底 ≈ 22/255 时的 Δ）：细网格 6~12、主网格 14~22、顶部高光 28~40。
    /// 这条约定对 P29-2 ~ P29-5 同样有效，别在后几轮又掏出个 0.1 的描边。
    /// </summary>
    public static class TwinTheme
    {
        // ================================================================ 颜色

        /// <summary>抽屉底。比 PanelBottom 再深一档，让抽屉从场景里"浮"出来。</summary>
        public static readonly Color ScrimBase = new Color(0.043f, 0.055f, 0.078f, 0.98f);

        // ---- 面板（沿用既有，不动）----
        public static readonly Color PanelTop = new Color(0.078f, 0.106f, 0.157f, 0.94f);
        public static readonly Color PanelBottom = new Color(0.055f, 0.075f, 0.114f, 0.96f);

        /// <summary>
        /// 结构线 / hairline 1px。P29-2 起改为「白 @0.006」（实测 Δ11.5），
        /// 不再是设计稿的蓝灰 #2A3648@0.80 —— 后者在线性空间里是一道实打实的亮边。
        /// 影响面：TwinButton / TwinHomePage / TwinSystemPage 的 1px 描边会一起变淡，这是 P29 的预期。
        /// </summary>
        public static readonly Color Border = WithAlpha(Color.white, 0.006f);

        // ---- 卡片：比面板亮半档，形成"面板 > 卡片"的层次 ----
        public static readonly Color CardTop = new Color(0.098f, 0.129f, 0.184f, 0.92f);
        public static readonly Color CardBottom = new Color(0.071f, 0.094f, 0.137f, 0.94f);

        // ---- 主色：选中 / 高亮 / 进度 / 图表主线 ----
        public static readonly Color Accent = new Color(0.20f, 0.75f, 1.00f, 1f);

        /// <summary>
        /// 选中行底 / 选中 Tab 底。P29-2 标定：Accent @0.035（亮度差 Δ18）。
        /// **不要按 R 通道标** —— 主色 R 分量最低，按 R 口径要 alpha 0.30 才达标，
        /// 那一档上屏是一整条饱和蓝（见 Docs 附录 A 的色块证据），和"选中行"的语义不符。
        /// </summary>
        public static readonly Color AccentDim = WithAlpha(Accent, 0.035f);

        public static readonly Color AccentLine = new Color(0.20f, 0.75f, 1.00f, 0.55f);

        /// <summary>主色渐变亮端 #6FDCFF。**只给图标底 / 进度条**，不要当文字色用。</summary>
        public static readonly Color AccentHi = new Color(0.435f, 0.863f, 1.00f, 1f);

        /// <summary>主色渐变暗端 #1E90FF。与 AccentHi 配对做垂直渐变（只能垂直）。</summary>
        public static readonly Color AccentDeep = new Color(0.118f, 0.565f, 1.00f, 1f);

        // ---- 三层发光公式（§3.4）：外层柔光 + 内层描边，零 bloom ----
        /// <summary>发光外层：Accent @0.008（实测 Δ5.6，半径 +3、描边 3px）。再高就成雾，再低就没有"光"。</summary>
        public static readonly Color GlowOuter = WithAlpha(Accent, 0.008f);

        /// <summary>发光内层：Accent @0.35（实测 Δ75.8，半径不变、描边 1px）。告警呼吸只动这一层。</summary>
        public static readonly Color GlowInner = WithAlpha(Accent, 0.35f);

        // ---- 入口格（P29-3）----
        /// <summary>主入口「校园管理系统」低透明底：Accent @0.030，实测 Δ14.8（目标 12–18）。</summary>
        public static readonly Color EntryFill = WithAlpha(Accent, 0.030f);

        /// <summary>入口图标底（56×56 R14）：Accent @0.050，实测 Δ20.4（目标 18–24）。</summary>
        public static readonly Color EntryIconFill = WithAlpha(Accent, 0.050f);

        // ================================================================ 图表色序（P5 大屏）
        //
        // 【为什么是单色相 4 级而不是彩虹色】
        // 见微图纸 §4.2：donut 原来用蓝/绿/橙/紫四色，和告警色（红）、状态色（绿/橙）
        // 撞语义 —— 一段 22.9% 的"灯具"用了绿色，读起来像"正常/通过"。
        // 改成同一色相（Accent 青蓝）的 4 级明度，靠明暗区分序列，不占语义色。
        //
        // 值照抄图纸 §4.2，**不要自己调**：4 级之间的 Δ 是按深色卡底标定过的，
        // 调一格就会和相邻段糊在一起（P00 §C6：8bit 只能按 1/255 跳档）。
        public static readonly Color ChartSeq1 = Accent;                            // (0.20, 0.75, 1.00)
        public static readonly Color ChartSeq2 = new Color(0.16f, 0.60f, 0.80f, 1f);
        public static readonly Color ChartSeq3 = new Color(0.13f, 0.47f, 0.63f, 1f);
        public static readonly Color ChartSeq4 = new Color(0.10f, 0.35f, 0.47f, 1f);

        /// <summary>
        /// 曲线下方填充：Accent @0.012。
        ///
        /// 【为什么这么淡】Linear 色彩空间会把半透明 alpha 的视觉重量抬高 2–4 倍（P00 §C5），
        /// 0.012 在 sRGB 直觉里"几乎看不见"，实际屏幕上是 Δ10–14 的一层薄雾 ——
        /// 刚好托住曲线又不抢网格线。
        ///
        /// 若截图取色 Δ 落在 10–14 之外，按 **1/255 档** 跳到 0.008 或 0.016，
        /// 不要做 0.010/0.014 这种连续微调（P00 §C6：8bit 表达不出来，改了也是白改）。
        /// </summary>
        public static readonly Color ChartFill = WithAlpha(Accent, 0.012f);

        // ---- 状态色（与 TwinStatusPalette 同值，那边后续改为转发到这里）----
        public static readonly Color StatusUnknown = new Color(0.35f, 0.38f, 0.45f, 1f);
        public static readonly Color StatusNormal = new Color(0.20f, 0.80f, 0.35f, 1f);
        public static readonly Color StatusWarning = new Color(1.00f, 0.62f, 0.15f, 1f);
        public static readonly Color StatusAlarm = new Color(0.90f, 0.20f, 0.20f, 1f);
        public static readonly Color StatusStale = new Color(0.50f, 0.50f, 0.55f, 1f);
        public static readonly Color StatusOffline = new Color(0.28f, 0.28f, 0.30f, 1f);

        /// <summary>
        /// donut 第 5 段「其他」的颜色：`(0.26, 0.28, 0.33)`。
        ///
        /// ── 为什么不直接用 `StatusUnknown` ──────────────────────────────
        /// `StatusUnknown` 是**状态语义色**（设备状态未知），全项目多处在用。
        /// donut 那段是"构成占比"里的聚合项，两者语义不同 ——
        /// 共用会让"改状态色"意外改掉图表，"改图表"意外改掉状态。
        ///
        /// ── 数值怎么定的（P5-1 P1-3 + 负责人裁决）───────────────────────
        /// 验收要求：「其他」是 5 段中**亮度最低**的一段，
        /// 亮度按 `0.2126R + 0.7152G + 0.0722B` 算。
        ///
        ///   原 StatusUnknown (0.35,0.38,0.45) → 0.3787  ← 比 Seq4 还亮，反抢第一眼
        ///   优化单建议   (0.29,0.31,0.36) → **0.3094  ← 仍高于 Seq4 的 0.3055，过不了验收**
        ///   现在采用     (0.26,0.28,0.33) → 0.2794  ← 低于 Seq4 ✔
        ///
        /// 所以**不要照优化单的 0.29/0.31/0.36 改**，那个值自相矛盾。
        /// </summary>
        public static readonly Color ChartSeqOther = new Color(0.26f, 0.28f, 0.33f, 1f);

        /// <summary>
        /// 【P5】donut / 分段图表的**唯一**色板：单色相 4 级明度 + 灰。
        ///
        /// ── 为什么要收到这里（2026-11-02）──────────────────────────────
        /// 全屏 `TwinFullLayer.Pages` 与窄态 `TwinSystemPage` 原来各存一份手写实现
        /// （前者的 `DonutPalette` 数组、后者的 `NDonutColor` switch），
        /// 靠注释「必须保持一致」互相提醒 —— 那是靠人记，不是靠编译器拦。
        /// 主理人明确要求「窄态 donut 配色必须和全屏完全一致」，
        /// 最可靠的做法就是**只留一份**，两边都来这儿取。
        ///
        /// ── 为什么是 5 段而不是 4 段 ──────────────────────────────────
        /// 第 5 位是「其他」聚合项（合并第 5 名及以后的类型）。
        /// 它表示的不是某个类型而是一堆剩下的，所以用灰 `ChartSeqOther`
        /// （不是 `StatusUnknown`，理由见那个常量的注释），
        /// 与 4 级彩阶在视觉上区分开。段序固定按台数降序（图纸 §2.3）。
        ///
        /// ── 挪动这里时的注意事项 ──────────────────────────────────────
        /// 声明位置必须在 `ChartSeq1~4` 与 `ChartSeqOther` **之后** ——
        /// C# 静态字段按文本顺序初始化，提前声明会读到 (0,0,0,0) 的全黑。
        ///
        /// 色板本体**故意 private**：数组是可变引用，公开出去就可能有人
        /// `ChartSeqPalette[2] = ...` 就地改，又回到"两态不一致"的老路。
        /// 要加段/改色就改这一处，改完全项目一起变。
        /// </summary>
        private static readonly Color[] ChartSeqPalette =
        {
            ChartSeq1,          // 第 1 大类型
            ChartSeq2,
            ChartSeq3,
            ChartSeq4,
            ChartSeqOther,      // 「其他」聚合项（亮度必须低于上面 4 段）
        };

        /// <summary>
        /// 按段序取色，越界自动夹到末位（灰）。
        /// 全屏与窄态**都必须**走这个入口，不许再写 switch 或第二份数组。
        /// </summary>
        public static Color ChartSeqAt(int i)
        {
            return ChartSeqPalette[Mathf.Clamp(i, 0, ChartSeqPalette.Length - 1)];
        }

        /// <summary>告警橙。**只给告警用**，不要拿来当强调色。</summary>
        public static readonly Color Warn = StatusWarning;

        /// <summary>
        /// 胶囊底：状态色 @0.040（实测 Δ18，绿色基准）。
        /// 注意红色 StatusAlarm 亮度低，同样 alpha 下 Δ 会比绿色低 ~35%，
        /// 需要等亮的话由调用方传 `CapsuleFill(status)` 后再单独抬，不要改这里。
        /// </summary>
        public static Color CapsuleFill(Color status)
        {
            return WithAlpha(status, 0.040f);
        }

        /// <summary>
        /// 告警卡描边：StatusAlarm @0.55，实测 Δ≈53（目标 45–60，本轮自定 —— 需求只说"Alarm@标定值"）。
        /// 呼吸时只动这一层的 canvasRenderer alpha（0.55↔1.0），**不要**每帧调 SetBorder ——
        /// 那会触发 SetVerticesDirty，一秒 60 次重建网格，正好撞在 §6.2-7 的性能红线上。
        /// </summary>
        public static readonly Color AlarmStroke = WithAlpha(StatusAlarm, 0.55f);

        // ---- 文字三级 ----
        public static readonly Color TextPrimary = new Color(0.94f, 0.96f, 0.99f, 1f);
        public static readonly Color TextSecondary = new Color(0.62f, 0.69f, 0.79f, 1f);
        public static readonly Color TextMuted = new Color(0.46f, 0.52f, 0.61f, 1f);

        // ---- 交互态（白色叠加，任何底色上都能用）----
        // 【P29-2 标定】按亮度差 Δ 反解，不再是设计稿的 sRGB 百分比（实测见附录 A）：
        //   Divider Δ6.5 / Hover Δ11.5。Pressed / Track / Disabled 本轮没进标定清单，暂留原值。
        public static readonly Color Divider = WithAlpha(Color.white, 0.005f);
        public static readonly Color Track = new Color(1f, 1f, 1f, 0.09f);
        public static readonly Color Hover = WithAlpha(Color.white, 0.006f);
        public static readonly Color Pressed = new Color(1f, 1f, 1f, 0.14f);
        public static readonly Color Disabled = new Color(1f, 1f, 1f, 0.04f);

        // ---- 面板底纹（§6.2-1）：24 单位细网格 + 120 单位主网格 ----
        // 【按屏幕取色标定，不是抄设计稿百分比】见类注释的硬约定。
        // 三个值的验收区间（Δ = 采样点亮度 − 面板底色亮度，面板底取面板上下两点均值 22.6/255）：
        //   细网格 6~12 / 主网格 14~22 / 顶部高光 28~40 —— 实测 7.5 / 16.0 / 32.1，全部落在区间内。
        // 注意：网格线是烘进 8bit 贴图的，实际生效值是 round(alpha×255)/255，
        // 所以 0.004 与 0.006 会分别落到 1/255 与 2/255 两档（Δ7.5 与 Δ12.7），别以为能连续微调。
        /// <summary>
        /// 细网格线：白 @0.004（烘图后 = 1/255，实测 Δ7.5/255）。
        /// **【已停用 · P29-3.5 裁决 C-01】不再参与烘焙，新代码禁止引用。**
        /// 停用原因：投影仪缩放下 24px 细线会产生摩尔纹，并抢面板视觉权重。
        /// 保留常量是为了让 `GridSprite(cell, major)` 的双周期签名在 P3 之后仍可用
        /// （若哪天要恢复细线，只需把 ApplyGrid 的 cell 从 0 改回 24，不必再补 token）。
        /// </summary>
        public static readonly Color GridLine = WithAlpha(Color.white, 0.004f);

        /// <summary>主网格线：白 @0.012（烘图后 = 3/255，实测 Δ16.0/255）。每 120 单位一条。</summary>
        public static readonly Color GridMajor = WithAlpha(Color.white, 0.012f);

        /// <summary>面板顶部 1px 高光：白 @0.028（顶点色，不量化；实测 Δ28~32/255）。</summary>
        public static readonly Color TopHighlight = WithAlpha(Color.white, 0.028f);

        /// <summary>
        /// 图表内部网格线（横向 1px），顶点色。**图表专用**，和面板底纹 GridLine 分家：
        /// GridLine 只服务 TwinPaint.GridSprite 烘的那张贴图，画在图表里会跟卡片底纹打架。
        /// 数值按 Δ11.5 标定 —— 需求里写的 0.09 实测 Δ≈64，比卡片描边还亮，会盖过折线。
        /// </summary>
        public static readonly Color ChartGrid = WithAlpha(Color.white, 0.006f);

        /// <summary>
        /// 实时动态的竖线（时间轴主干）。实测 Δ11.5（目标 10–14）。
        /// 比 Divider（Δ6.5）实一档：Divider 是"分隔"，这条是"时间轴"，得看得出是一条线。
        /// </summary>
        public static readonly Color TimelineSpine = WithAlpha(Color.white, 0.006f);

        // ================================================================ 描边 / 色条（§3.3）

        /// <summary>
        /// 「颜色 + 线宽」成对出现，避免调用方自己拼 1f/2f/3f 又拼错色。
        /// 用 `TwinTheme.StrokeHair.Color` / `.Width` 取值。
        /// </summary>
        public readonly struct StrokeStyle
        {
            public readonly Color Color;
            public readonly float Width;

            public StrokeStyle(Color color, float width)
            {
                Color = color;
                Width = Mathf.Max(0f, width);
            }

            /// <summary>给 Image / TwinRoundedRect 用的一行取值：`SetBorder(s.Color, s.Width)`。</summary>
            public void ApplyTo(TwinRoundedRect target)
            {
                if (target == null) return;
                target.SetBorder(Color, Width);
            }
        }

        /// <summary>结构线：1px Border。所有卡片 / 行的默认描边。</summary>
        public static readonly StrokeStyle StrokeHair = new StrokeStyle(Border, 1f);

        /// <summary>选中描边：1px Accent@0.55。**只给选中态 / 主入口**。</summary>
        public static readonly StrokeStyle StrokeAccent = new StrokeStyle(AccentLine, 1f);

        /// <summary>抽屉左缘竖条：2px Accent@0.38。面板唯一的「边界信号」，别处不要复用。</summary>
        public static readonly StrokeStyle BarAccent = new StrokeStyle(WithAlpha(Accent, 0.38f), 2f);

        /// <summary>状态色条宽度：3px。颜色由调用方按状态传入（`BarStatus(Color)`）。</summary>
        public const float BarStatusWidth = 3f;

        /// <summary>3px 状态色条。卡片 / KPI / 列表行左侧，颜色 = 该行状态色。</summary>
        public static StrokeStyle BarStatus(Color statusColor)
        {
            return new StrokeStyle(statusColor, BarStatusWidth);
        }

        // ================================================================ 字号（10 档 → 6 档）

        public const int FsCaption = 11;      // 脚注、单位、次要说明
        public const int FsBody = 13;         // 正文、列表副标题、图标名
        public const int FsBodyStrong = 15;   // 列表主文案、按钮
        public const int FsSubtitle = 17;     // 区块标题、模块名
        public const int FsTitle = 22;        // 页面标题、大数值
        public const int FsDisplay = 28;      // 首页 KPI 数字

        /// <summary>
        /// 9 号：时间线副文案 / 时间戳（文档 §4.1 写 9.5）。
        /// 为什么是 9 不是 9.5：UGUI 的 legacy `Text.fontSize` 是 **int**，9.5 根本表达不出来；
        /// 换 TMP 才能用小数。这是 6 档之外的唯一例外，只给"时间戳/两行小字"用。
        /// </summary>
        public const int FsMicro = 9;

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

        /// <summary>告警呼吸周期（秒）：alpha 0.55↔1.0，sin 驱动，**只动描边层**。</summary>
        public const float Breathe = 2.0f;

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

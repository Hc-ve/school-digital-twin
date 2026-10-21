using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using SchoolTwin.Core;
using SchoolTwin.Interaction;
using SchoolTwin.Twin;

namespace SchoolTwin.UI
{
    /// <summary>
    /// P1-C 正式信息面板（UGUI）。
    ///
    /// 结构（全部由代码在 Awake 构建，不依赖任何预制体与图片资源）：
    ///
    ///   [TwinUI] Canvas(Screen Space Overlay, 1920x1080 自适应)
    ///     └ TwinDetailPanel
    ///         ├ DetailCard  详情卡片（屏幕右上）
    ///         │   ├ 状态胶囊 + 点位名称 + TwinId
    ///         │   ├ 每个指标一行：指标名 / 数值+单位 / 量程条（阈值带 + 游标）/ 量程与阈值说明
    ///         │   └ 页脚：数据新鲜度 + 数据质量
    ///         └ AlarmPanel  告警列表（卡片下方），条目可点击 → 相机飞过去
    ///
    /// 几个刻意的取舍：
    ///   1. **代码构建界面**：预制体引用断裂会让面板静默变空白，代码构建则「有脚本就有界面」。
    ///   2. **手工绝对布局，不用 LayoutGroup**：数值每 0.1s 变一次，LayoutGroup 会连带触发
    ///      Canvas 重排（Rebuild Layout），点位一多就是持续的 CPU 开销；绝对布局只改
    ///      几个数字，重排范围最小。
    ///   3. **文本变化才赋值**：所有 Text 都带缓存，内容相同就不写，避免无谓的 Canvas 重建。
    ///   4. **阈值带只在选中变化时重算**，只有量程游标按 10Hz 更新 —— 静态的东西不要每帧算。
    ///   5. **面板背景 raycastTarget = true**：配合 TwinPicker 的「指针在 UI 上不拾取」判断，
    ///      点面板不会误选到面板背后的三维物体。
    /// </summary>
    [AddComponentMenu("SchoolTwin/UI/孪生信息面板 (TwinDetailPanel)")]
    [DisallowMultipleComponent]
    public class TwinDetailPanel : MonoBehaviour
    {
        // ================================================================ 配置

        [Header("显示")]
        [SerializeField] private bool _visible = true;
        [Tooltip("面板开关快捷键（注意：数字键 1-9 已被预设机位占用，F3 被调试 HUD 占用）")]
        [SerializeField] private KeyCode _toggleKey = KeyCode.F4;

        [Tooltip("整体缩放。1 = 基准大小。字体看不清就调大，例如 1.2 / 1.5 / 1.8。\n" +
                 "缩放会同步放大字号、行高、卡片宽度与内边距，不会出现「字大了框没大」的错位。\n" +
                 "注意：无需按分辨率调 —— Canvas 已做分辨率适配，同一数值在任何屏幕上观感一致。\n" +
                 "若内容过多顶出屏幕，会被自动压回放得下的上限（见 MaxScaleByVerticalSpace）。")]
        [Range(0.75f, 3f)]
        [SerializeField] private float _uiScale = 1.25f;

        [Tooltip("面板距屏幕边缘的留白（未缩放）。实际值 = 该值 × 整体缩放，且不低于代码里的下限")]
        [SerializeField] private float _screenMargin = 20f;

        [Header("位置微调（正数 = 更靠左 / 更靠下）")]
        [Tooltip("面板再往**左**挪多少（未缩放，会乘整体缩放）。\n" +
                 "用于避开 Game 视图右上角被工具栏 / 状态栏压住的情况。")]
        [SerializeField] private float _extraRight = 16f;

        [Tooltip("面板再往**下**挪多少（未缩放，会乘整体缩放）。\n" +
                 "卡片标题（设备名）被顶部挡住时调大这个值。")]
        [SerializeField] private float _extraTop = 28f;

        [Tooltip("留空则自动向系统申请含中文字形的字体（微软雅黑等）")]
        [SerializeField] private Font _fontOverride;

        [Header("刷新节流")]
        [Tooltip("选中点位的数值刷新间隔。10Hz 足够看清变化，又不会让 Canvas 频繁重排")]
        [SerializeField] private float _valueRefreshInterval = 0.1f;
        [Tooltip("告警列表刷新间隔（要遍历全部点位，频率可以更低）")]
        [SerializeField] private float _alarmRefreshInterval = 0.25f;

        [Header("交互")]
        [Tooltip("点击告警条目时把相机飞过去")]
        [SerializeField] private bool _flyToOnClick = true;
        [SerializeField] private float _flyDistance = 6f;

        [Header("告警列表")]
        [Tooltip("最多显示多少条告警。这直接决定告警面板高度 ——\n" +
                 "在 1080p 下，指标占满 6 行时告警最多能放 6~7 条，再多会把面板顶出屏幕。\n" +
                 "超出部分不会丢，会折叠成一行「… 另有 N 个异常点位」。")]
        [SerializeField] private int _maxAlarmRows = 6;

        // ================================================================ 基础尺寸（未缩放）
        //
        // 【为什么要有一个缩放因子】
        // 面板尺寸由「字号 + 行高 + Y 坐标」三组数字共同决定，它们必须成比例。
        // 只把字号从 19 改成 26，文字会溢出到下一行、量程条会被压住 —— 因为坐标没动。
        // 所以这里把所有尺寸都定义成「基准值」，再统一乘 _uiScale，
        // 改一个滑块就能整体放大而不跑版。

        /// <summary>
        /// 卡片宽度（基准值，未缩放）。实际宽度 = 该值 × _activeScale。
        ///
        /// 【注意：这里不再有 _cardWidth 这个 Inspector 字段】
        /// 原先 `BaseCardWidth` 与 `_cardWidth` 并存，本意是「常量给默认值、
        /// 字段给用户调」。但场景里早就序列化了 `_cardWidth: 384` ——
        /// Unity 用序列化值覆盖脚本默认值，于是改 `BaseCardWidth` 完全不生效，
        /// 而这一差异在代码里毫无迹象（读代码只会看到那个常量）。
        /// 曾因此白改一轮。现在只留常量，单一事实来源。
        ///
        /// 420 → 470：用户反馈「还是小」，且对比截图看卡片偏瘦长。
        /// 加宽比加高划算 —— 宽不占垂直空间，不会被 MaxScaleByVerticalSpace 压。
        /// </summary>
        private const float BaseCardWidth = 470f;

        /// <summary>
        /// 面板距屏幕边缘的**下限**（基准单位，会乘 _activeScale）。
        ///
        /// 【为什么要有下限】场景里 `_screenMargin` 序列化的是 20，
        /// 在 s=0.75 时只有 15 个单位。实测 682px 宽的画布下，15 单位 = **10.2px** ——
        /// 圆角和边框几乎贴着屏幕边缘，看起来就是「面板被切了一点」。
        ///
        /// 所以取 `max(_screenMargin, BaseScreenMargin)`：Inspector 里调大仍然生效，
        /// 但调小于这个值不再起作用（低于它就不好看了）。
        /// 32 × 0.75 ≈ 24 单位 ≈ 屏宽的 2.4%。
        /// </summary>
        private const float BaseScreenMargin = 32f;
        private const float BasePad = 18f;
        private const float BaseTitleY = 16f;
        private const float BaseSubtitleY = 50f;
        private const float BaseDivider1Y = 80f;
        private const float BaseMetricsTop = 90f;
        private const float BaseRowHeight = 90f;
        private const float BaseRowGap = 7f;
        private const float BaseBarY = 30f;
        private const float BaseBarHeight = 9f;

        // 迷你趋势线：一行 62 → 90，多出来的 28 全给趋势线。
        // 为什么放在量程条和提示文字**下面**：上面三行（名称/数值、量程条、阈值提示）
        // 已经被占满，只有这里还有整行宽度的空白，且不与任何文字抢位置。
        // 20 单位 × 0.75 只有 15px，太扁看不出形状 —— 给到 24。
        private const float BaseSparkY = 62f;
        private const float BaseSparkHeight = 24f;

        /// <summary>趋势线采样间隔（秒）。60 秒窗口按 5Hz 存 300 个点。</summary>
        private const float SparkSampleInterval = 0.2f;
        private const float BaseFooterHeight = 18f;
        private const float BaseAlarmTitleY = 18f;
        private const float BaseAlarmRowsTop = 48f;
        private const float BaseAlarmRowHeight = 30f;
        private const float BaseAlarmRowGap = 4f;
        private const float BasePanelGap = 12f;
        private const float BaseChipWidth = 86f;
        private const float BaseChipHeight = 28f;

        // ---- 字号（未缩放）----
        //
        // 【为什么这里整体比最初版本大一圈】最初版本是 19/13/12/11 这一档，
        // 用户实测「数据看不清」。此时能拉的杠杆有两根：
        //   ① 调大 _uiScale —— 但会等比撑高面板，垂直空间不够（见 MaxScaleByVerticalSpace）
        //   ② 调大基准字号 —— 只撑高一点行高，性价比高得多
        // 所以这里把基准字号直接抬一档，缩放再在其上做微调。
        private const int FsTitle = 22;
        private const int FsChip = 14;
        private const int FsSubtitle = 14;
        private const int FsHint = 15;
        private const int FsFooter = 13;
        private const int FsAlarmTitle = 16;
        private const int FsAlarmEmpty = 14;
        private const int FsAlarmMore = 14;
        private const int FsMetricName = 15;
        private const int FsMetricValue = 23;
        private const int FsMetricHint = 13;
        private const int FsAlarmRow = 14;

        private const int MaxMetricRows = 6;
        private const int MaxBands = 5;

        // ================================================================ 缩放后的尺寸（构造时算一次）
        //
        // 只算一次而不是每次排版都乘：布局函数在数值刷新路径之外也会被调用，
        // 反复做乘法除了浪费没有收益。OnValidate 改了 _uiScale 会重建界面。

        /// <summary>本次实际生效的缩放系数（手动值，可能被垂直上限压低）。由 ApplyScale() 写入。</summary>
        private float _activeScale = 1f;

        private float _scaledCardWidth;
        private float _scaledScreenMargin;
        /// <summary>位置微调展开后的实际值（已乘整体缩放）。</summary>
        private float _scaledExtraRight;
        private float _scaledExtraTop;
        private float _scaledPad;
        private float _scaledTitleY;
        private float _scaledSubtitleY;
        private float _scaledDivider1Y;
        private float _scaledMetricsTop;
        private float _scaledRowHeight;
        private float _scaledRowGap;
        private float _scaledBarY;
        private float _scaledFooterHeight;
        private float _scaledAlarmTitleY;
        private float _scaledAlarmRowsTop;
        private float _scaledAlarmRowHeight;
        private float _scaledAlarmRowGap;
        private float _scaledBarHeight;
        private float _scaledSparkY;
        private float _scaledSparkHeight;
        private float _scaledPanelGap;
        private float _scaledChipWidth;
        private float _scaledChipHeight;
        /// <summary>标题右侧要让出的宽度（状态胶囊 + 间隙），供 Truncate 计算可用字数。</summary>
        private float _scaledChipSpace;

        private int _scaledFsTitle;
        private int _scaledFsChip;
        private int _scaledFsSubtitle;
        private int _scaledFsHint;
        private int _scaledFsFooter;
        private int _scaledFsAlarmTitle;
        private int _scaledFsAlarmEmpty;
        private int _scaledFsAlarmMore;
        private int _scaledFsMetricName;
        private int _scaledFsMetricValue;
        private int _scaledFsMetricHint;
        private int _scaledFsAlarmRow;

        /// <summary>
        /// 求本次实际生效的缩放系数。
        ///
        /// ── 这里踩过一个坑，说明白免得以后再犯 ──
        ///
        /// 最初想「按屏幕像素高度自动适配」：`s = Screen.height / 1080 × 1.6`。
        /// 看起来合理，实际是错的。因为 Canvas 已经配了
        /// `ScaleWithScreenSize` + 参考分辨率 `1920×1080`：
        /// **UGUI 会先把实际分辨率归一化到 1080 高的坐标系**，
        /// 同一份 UI 在任何窗口尺寸下看起来都「一样大」（占屏比例相同）。
        ///
        /// 所以再乘一次 `Screen.height / 1080` 等于**重复缩放**。
        /// 在 1080p 全屏下恰好等于 1（看不出问题），
        /// 但 Game 视图开成小窗口时（比如 1084×357），
        /// `357/1080 = 0.33` —— 面板直接被缩小到三分之一。
        /// 这就是「越调越小」的直接原因。
        ///
        /// ── 正确做法 ──
        ///
        /// 默认就用一个**固定值**（`_uiScale`）当作缩放，与分辨率无关 ——
        /// 因为 Canvas 已经负责了分辨率适配。文档里那一节「各分辨率对照」
        /// 其实是个幻觉：不同分辨率下 UGUI 看到的坐标系都是 1080 高，
        /// 面板大小本来就一样。唯一真正会变的是**窗口宽高比**，
        /// 那由 CanvasScaler 的 matchWidthOrHeight 处理，不归我们管。
        ///
        /// 唯一必须动态计算的，是「垂直方向放不放得下」这道硬约束：
        /// 内容太多时削掉一点，免得顶出屏幕。
        /// </summary>
        private float EffectiveScale()
        {
            float manual = Mathf.Clamp(_uiScale, 0.5f, 3f);

            // 两个方向的硬约束都要满足，取更紧的那个：
            //   · 垂直：内容（卡片 + 告警面板）不能顶出屏幕下沿
            //   · 水平：卡片不能顶出屏幕右沿（以前漏了这条，右侧会被切）
            float hardCap = Mathf.Max(0.75f,
                Mathf.Min(MaxScaleByVerticalSpace(), MaxScaleByHorizontalSpace()));

            return Mathf.Clamp(Mathf.Min(manual, hardCap), 0.75f, 3f);
        }

        /// <summary>
        /// Canvas 的「参考高度」——UI 坐标系里竖直方向有多少可用空间。
        ///
        /// 以当前配置推算：`ScaleWithScreenSize` + 参考 `1920×1080` +
        /// `matchWidthOrHeight = 0.5`（宽高各占一半权重）。
        /// 屏幕按 `factor = lerp(w/1920, h/1080, 0.5)` 缩放，所以
        ///   视口高 = 屏幕高 / factor
        ///
        /// · 标准 16:9（1280×720 / 1920×1080 / 2560×1440）→ factor = h/1080 → 视口高恒为 1080
        /// · 宽高比偏离 16:9 时会变：比如 1084×357（约 3:1 扁条）→ 视口高只有约 798
        ///
        /// 【为什么这个值有意义】Canvas 已经做了分辨率适配，所以「面板多大」
        /// 在标准 16:9 各分辨率下是一样的。唯一会变的就是宽高比 ——
        /// 扁长的窗口纵向空间少，面板自然要收敛。这不是 bug，是物理限制。
        /// </summary>
        private float ViewportHeight()
        {
            if (Screen.height <= 0) return 1080f;

            // 首选：直接问画布「一个 UI 单位等于多少屏幕像素」，再反推。
            // 这比下面按 referenceResolution + matchWidthOrHeight 手算更可靠 ——
            // 手算要求我们把 CanvasScaler 的匹配语义完全复刻一遍，一旦场景里
            // 参考分辨率/匹配值被改过（本工程就被改成 1000×720、匹配值 0），
            // 手算和 Unity 实际结果一旦不一致，面板尺寸就会跟着算错。
            Canvas canvas = _uiRoot != null ? _uiRoot.GetComponentInParent<Canvas>() : null;
            if (canvas != null && canvas.scaleFactor > 0.0001f)
                return Screen.height / canvas.scaleFactor;

            CanvasScaler scaler = _uiRoot != null
                ? _uiRoot.GetComponentInParent<CanvasScaler>()
                : null;
            if (scaler == null || scaler.uiScaleMode != CanvasScaler.ScaleMode.ScaleWithScreenSize)
                return Screen.height;

            Vector2 refRes = scaler.referenceResolution;
            float sx = Screen.width / Mathf.Max(1f, refRes.x);
            float sy = Screen.height / Mathf.Max(1f, refRes.y);
            float factor = Mathf.Lerp(sx, sy, Mathf.Clamp01(scaler.matchWidthOrHeight));
            if (factor <= 0.0001f) return 1080f;
            return Screen.height / factor;
        }

        /// <summary>
        /// 在「最坏情况内容」下，缩放最大能到多少才不把面板顶出屏幕。
        ///
        /// 最坏情况 = 指标行占满 MaxMetricRows（6 行）+ 告警面板占满 _maxAlarmRows。
        /// 这是常驻显示的上限；实际内容少的时候面板更矮，自然放得下。
        ///
        /// 推导：整条竖直排列是
        ///   上边距 + 卡片高 + 面板间隙 + 告警面板高 + 下边距 ≤ 视口高
        /// 其中卡片高与告警面板高都是「基准值 × s」，所以能把 s 解出来。
        /// </summary>
        private float MaxScaleByVerticalSpace()
        {
            float viewport = ViewportHeight();
            // 上下各留一点呼吸空间（不参与缩放，用基准值的 1 倍）
            float reserve = 24f;

            // --- 分子：s = 1 时的面板总高 ---
            // 卡片：metricsTop + 6 行 + 间隙 + div2 + footer + 底部留白
            int slots = Mathf.Max(1, MaxMetricRows);
            float cardAt1 = BaseMetricsTop
                            + slots * BaseRowHeight + (slots - 1) * BaseRowGap
                            + 8f + 1f + 6f + BaseFooterHeight + 14f;

            // 告警面板：标题区 + N 行 + 「还有更多」行 + 底部留白
            int rows = Mathf.Max(1, _maxAlarmRows);
            float alarmAt1 = BaseAlarmRowsTop
                             + rows * BaseAlarmRowHeight + (rows - 1) * BaseAlarmRowGap
                             + 14f + FsAlarmMore + 8f;

            float panelAt1 = cardAt1 + alarmAt1 + BasePanelGap;

            // 还有上边距（含下移量）、下边距（各 BaseScreenMargin）
            float fixedAt1 = panelAt1 + BaseScreenMargin * 2f + Mathf.Max(0f, _extraTop);

            if (fixedAt1 <= 0f) return 3f;

            float room = viewport - reserve;
            if (room <= 0f) return 0.75f;

            // room = fixedAt1 * s  →  s = room / fixedAt1
            return room / fixedAt1;
        }

        /// <summary>
        /// 水平方向的硬上限：卡片再宽也不能把左右边距吃掉。
        ///
        /// 【为什么必须单独算】`MaxScaleByVerticalSpace()` 只看高度。
        /// 画布一窄（窄窗口、扁长 Game 视图、参考分辨率调小），
        /// 卡片会横向顶出屏幕，而垂直方向算出来「还放得下」→ 完全没有约束。
        ///
        /// 【为什么用父 rect 而不是 Screen.width / scaleFactor】卡片是用
        /// `anchor=(1,1)` 摆在父节点里的，宽度约束必须和它用同一个坐标系（父 rect）。
        /// 拿屏幕像素反推的宽度去约束一个按父 rect 摆放的元素，两者不一致时就会算错 ——
        /// 2026-10-21 那次「面板跑到画面外」就是这么来的。
        /// 父 rect 万一本身就不贴合屏幕，交给 `ClampIntoCanvas()` 兜底。
        /// </summary>
        private float MaxScaleByHorizontalSpace()
        {
            float avail = _uiRoot != null ? _uiRoot.rect.width : 0f;
            if (avail <= 0f) return 3f;

            // 左边距 + 右边距（含左移量）
            float room = avail - BaseScreenMargin * 2f - Mathf.Max(0f, _extraRight);
            if (room <= 0f) return 0.75f;

            return room / BaseCardWidth;
        }

        /// <summary>把基准尺寸按实际缩放展开成实际尺寸。BuildUI 之前必须调用。</summary>
        private void ApplyScale()
        {
            float s = EffectiveScale();
            _activeScale = s;   // 存下来，给圆角、游标这类零散计算复用

            _scaledCardWidth = BaseCardWidth * s;
            // 取 max：Inspector 里调大有效，调小于下限无效（否则会贴边，看着像被切）
            float baseMargin = Mathf.Max(_screenMargin, BaseScreenMargin);
            _scaledScreenMargin = baseMargin * s;
            _scaledExtraRight = _extraRight * s;
            _scaledExtraTop = _extraTop * s;
            _scaledPad = BasePad * s;
            _scaledTitleY = BaseTitleY * s;
            _scaledSubtitleY = BaseSubtitleY * s;
            _scaledDivider1Y = BaseDivider1Y * s;
            _scaledMetricsTop = BaseMetricsTop * s;
            _scaledRowHeight = BaseRowHeight * s;
            _scaledRowGap = BaseRowGap * s;
            _scaledBarY = BaseBarY * s;
            _scaledFooterHeight = BaseFooterHeight * s;
            _scaledAlarmTitleY = BaseAlarmTitleY * s;
            _scaledAlarmRowsTop = BaseAlarmRowsTop * s;
            _scaledAlarmRowHeight = BaseAlarmRowHeight * s;
            _scaledAlarmRowGap = BaseAlarmRowGap * s;
            _scaledBarHeight = BaseBarHeight * s;
            _scaledSparkY = BaseSparkY * s;
            _scaledSparkHeight = BaseSparkHeight * s;
            _scaledPanelGap = BasePanelGap * s;
            _scaledChipWidth = BaseChipWidth * s;
            _scaledChipHeight = BaseChipHeight * s;
            _scaledChipSpace = _scaledChipWidth + 8f * s;

            _scaledFsTitle = ScaleFont(FsTitle, s);
            _scaledFsChip = ScaleFont(FsChip, s);
            _scaledFsSubtitle = ScaleFont(FsSubtitle, s);
            _scaledFsHint = ScaleFont(FsHint, s);
            _scaledFsFooter = ScaleFont(FsFooter, s);
            _scaledFsAlarmTitle = ScaleFont(FsAlarmTitle, s);
            _scaledFsAlarmEmpty = ScaleFont(FsAlarmEmpty, s);
            _scaledFsAlarmMore = ScaleFont(FsAlarmMore, s);
            _scaledFsMetricName = ScaleFont(FsMetricName, s);
            _scaledFsMetricValue = ScaleFont(FsMetricValue, s);
            _scaledFsMetricHint = ScaleFont(FsMetricHint, s);
            _scaledFsAlarmRow = ScaleFont(FsAlarmRow, s);
        }

        /// <summary>字号缩放：取整（UGUI 的 fontSize 是 int），并保证不小于 8 免得看不清。</summary>
        private static int ScaleFont(int baseSize, float scale)
        {
            return Mathf.Max(8, Mathf.RoundToInt(baseSize * scale));
        }


        // ================================================================ 运行时引用

        private RectTransform _uiRoot;
        private RectTransform _card;
        private CanvasGroup _cardGroup;
        private TwinRoundedRect _cardBg;
        private TwinRoundedRect _chip;
        private Text _chipText;
        private Text _title;
        private Text _subtitle;
        private Text _hint;
        private Image _divider1;
        private Image _divider2;
        private Text _footer;

        private RectTransform _alarmPanel;
        private Text _alarmTitle;
        private Text _alarmEmpty;
        private Text _alarmMore;

        private readonly List<MetricRow> _metricRows = new List<MetricRow>(4);
        private readonly List<TwinAlarmRow> _alarmRows = new List<TwinAlarmRow>(8);
        private readonly List<TwinMetricDefinition> _metricDefBuf = new List<TwinMetricDefinition>(6);

        /// <summary>
        /// 告警展示缓冲。
        ///
        /// 【为什么不是「全部收集后排序」】P1-C 只有 5~6 个点位时怎么排都无所谓，
        /// 但 P1-E 要压到 100/200/500 个点位，且每 0.25s 刷一次：
        ///   · 全收集 → N 个元素；
        ///   · 全排序 → N log N 次比较，每次比较都要重算状态（读 _values 字典）；
        ///   · 而实际只需要前 _maxAlarmRows(8) 行。
        /// 500 点位 @ 4Hz 下，这就是 500×9≈4500 次比较/秒 vs 下面「维持 top-8 插入」的
        /// 500×8≈4000 次**简单比较**——差距不大，但关键是我们不再为「不显示的 492 行」排序，
        /// 而且状态只算一次、结果直接带着走，省掉了排序期间的反复重算。
        ///
        /// 结构体 + 预分配 List：整个过程零 GC。
        /// </summary>
        private struct AlarmEntry
        {
            public TwinAnchor Anchor;
            public TwinStatus Status;
            public int Severity;
        }

        private readonly List<AlarmEntry> _alarmTop = new List<AlarmEntry>(16);
        private int _alarmTotal;        // 异常点位的**总**数（不只是显示出来的）
        private int _twinTotal;         // 场景注册的点位总数

        private readonly Dictionary<string, TwinMetricDefinition> _syntheticMetrics =
            new Dictionary<string, TwinMetricDefinition>(StringComparer.Ordinal);

        private readonly StringBuilder _sb = new StringBuilder(192);
        private readonly StringBuilder _sbAlarm = new StringBuilder(128);

        private TwinAnchor _selected;
        private float _nextValueRefresh;
        private float _nextAlarmRefresh;
        private float _cardAlpha = 1f;
        private bool _built;
        /// <summary>上次打布局自检时的屏幕尺寸。分辨率变了就重打一次。</summary>
        private int _loggedW = -1;
        private int _loggedH = -1;
        /// <summary>夹边用的画布引用。缓存一份，避免每帧 GetComponentInParent。</summary>
        private Canvas _canvasRef;
        /// <summary>上一次排版时选中点位是否已有数据。用于捕捉「等待数据 → 有数据」的转变。</summary>
        private bool _hasData;

        // 文本缓存：内容不变就不写 Text
        private string _cachedTitle;
        private string _cachedSubtitle;
        private string _cachedHint;
        private string _cachedFooter;
        private string _cachedChip;
        private string _cachedAlarmTitle;
        private string _cachedAlarmEmpty;
        private string _cachedAlarmMore;

        /// <summary>单个指标的一行 UI。</summary>
        private sealed class MetricRow
        {
            public RectTransform Root;
            public Text Name;
            public Text Value;
            public Text Hint;
            public Image Track;
            public Image Marker;
            public Image[] Bands;
            /// <summary>最近 60 秒的迷你趋势线。按量程归一化，和量程条同一个纵轴含义。</summary>
            public TwinSparkline Spark;
            /// <summary>上一次压入趋势线采样的时间（unscaledTime）。</summary>
            public float LastPushTime;

            public string MetricName = "";
            public string Unit = "";
            public float Min;
            public float Max;
            public float WarnLow;
            public float WarnHigh;
            public float AlarmLow;
            public float AlarmHigh;
            public float BarWidth;

            public string LastNameText;
            public string LastValueText;
            public string LastHintText;
        }

        // ================================================================ 生命周期

        private void Awake()
        {
            _uiRoot = EnsureCanvas();
            EnsureEventSystem();

            if (_fontOverride != null) TwinUIFactory.OverrideFont(_fontOverride);

            BuildUI();
            _selected = TwinPicker.Selected;
            ApplySelection();
            RefreshAlarms();
            SetVisible(_visible);
        }

#if UNITY_EDITOR
        /// <summary>
        /// 编辑器里拖动「整体缩放」滑块后立刻重建界面，不用退出播放模式再看效果。
        ///
        /// 【为什么只在播放模式重建】编辑模式下面板压根没构建（_built 是 false），
        /// 此时销毁再重建没有意义，反而会在场景里留下一堆没用的 UI 节点。
        /// 另外 OnValidate 在脚本重编译、资源导入时也会被 Unity 调用，
        /// 所以必须用 Application.isPlaying 挡一道，避免误触发。
        /// </summary>
        private void OnValidate()
        {
            if (!Application.isPlaying) return;
            if (!_built) return;              // 还没构建过，等 Awake 自己来
            if (_uiRoot == null) return;

            // 延迟到下一帧：OnValidate 触发时 Unity 仍在序列化流程中，
            // 立刻 Destroy 会打出「Destroying object during serialization」告警。
            UnityEditor.EditorApplication.delayCall += RebuildAfterScaleChange;
        }

        private void RebuildAfterScaleChange()
        {
            // delayCall 是全局回调，可能在本对象已被销毁后才执行
            if (this == null) return;
            if (!Application.isPlaying) return;
            if (!_built || _uiRoot == null) return;

            TeardownUI();
            BuildUI();
            ApplySelection();
            RefreshAlarms();
            SetVisible(_visible);
        }
#endif

        /// <summary>
        /// 拆掉上一次构建出来的界面，并把所有运行时引用清空。
        /// 编辑器的 OnValidate 重建与运行时 RebuildUI 共用这一段，所以不能放进 #if UNITY_EDITOR。
        ///
        /// 【为什么要逐个清引用】Destroy 只销毁 GameObject，C# 侧的字段仍然指向
        /// 已销毁对象（Unity 的「假 null」）。这里若不清，重建后 _metricRows / _alarmRows
        /// 里会残留旧的（已销毁的）行对象，EnsureMetricRow 的 while 循环会误判
        /// 「已经有了」而不再创建新行 —— 表现就是放大后指标行集体消失。
        /// </summary>
        private void TeardownUI()
        {
            if (_uiRoot != null)
            {
                for (int i = _uiRoot.childCount - 1; i >= 0; i--)
                {
                    Transform child = _uiRoot.GetChild(i);
                    if (child == null) continue;
                    // 运行时用 Destroy（延迟到帧末，安全）；只有编辑器编辑模式下
                    // 才会出现「非播放」状态，此时必须用 DestroyImmediate，
                    // 否则新节点会和待销毁的旧节点并存一帧。
                    if (Application.isPlaying) Destroy(child.gameObject);
#if UNITY_EDITOR
                    else DestroyImmediate(child.gameObject);
#endif
                }
            }

            _card = null;
            _cardGroup = null;
            _cardBg = null;
            _chip = null;
            _chipText = null;
            _title = null;
            _subtitle = null;
            _hint = null;
            _divider1 = null;
            _divider2 = null;
            _footer = null;
            _alarmPanel = null;
            _alarmTitle = null;
            _alarmEmpty = null;
            _alarmMore = null;

            _metricRows.Clear();
            _alarmRows.Clear();

            // 文本缓存也要清：重建后是全新的 Text 组件，旧缓存内容相同就不会再写入，
            // 结果是新 Text 一片空白。
            _cachedTitle = null;
            _cachedSubtitle = null;
            _cachedChip = null;
            _cachedHint = null;
            _cachedFooter = null;

            _built = false;
            _cardAlpha = 0f;
        }

        private void Update()
        {
            if (Input.GetKeyDown(_toggleKey)) SetVisible(!_visible);
            if (!_visible) return;

            // 选中变化（TwinPicker 是唯一的选择来源，面板只读它）
            TwinAnchor current = TwinPicker.Selected;
            if (!ReferenceEquals(current, _selected))
            {
                _selected = current;
                ApplySelection();
                RefreshAlarms();
            }
            else if (_selected != null && _selected.HasData != _hasData)
            {
                // 「等待数据…」→ 真的有数据了（或反过来）：指标行要跟着出现/收起，必须重新排版
                ApplySelection();
            }

            float now = Time.unscaledTime;

            if (_selected != null && now >= _nextValueRefresh)
            {
                _nextValueRefresh = now + Mathf.Max(0.02f, _valueRefreshInterval);
                RefreshValues();
            }

            if (now >= _nextAlarmRefresh)
            {
                _nextAlarmRefresh = now + Mathf.Max(0.05f, _alarmRefreshInterval);
                RefreshAlarms();
            }

            // 卡片轻微淡入：选中时更实，未选中时压暗一点，避免静止时太抢眼
            float target = _selected != null ? 1f : 0.72f;
            if (_cardGroup != null && Mathf.Abs(_cardAlpha - target) > 0.002f)
            {
                _cardAlpha = Mathf.Lerp(_cardAlpha, target, 1f - Mathf.Exp(-12f * Time.unscaledDeltaTime));
                _cardGroup.alpha = _cardAlpha;
            }

            LogLayoutOnce();   // 打完一次就永久短路，开销可以忽略
        }

        /// <summary>
        /// 每帧末尾把卡片与告警面板夹回画布可见区域内。
        ///
        /// 【为什么必须有这一层】靠算坐标定位（不管是按父 rect 算，还是按
        /// `Screen.width / scaleFactor` 算）都依赖一个前提：算出来的数和父节点的坐标系一致。
        /// 这个前提一旦不成立，面板就会跑到画面外，而且错得很远 —— 已经踩过两次。
        ///
        /// 夹边不依赖任何前提：直接比较**世界坐标下的包围盒**，
        /// 「卡片右边缘在不在画布右边缘以内」是事实判断，不是推算。
        /// 正常情况下它一次都不会动（越界量为 0），只在真越界时纠偏。
        /// </summary>
        private void LateUpdate()
        {
            if (!_visible || !_built) return;

            ClampIntoCanvas(_card);
            ClampIntoCanvas(_alarmPanel);
        }

        private void ClampIntoCanvas(RectTransform rt)
        {
            if (rt == null) return;

            if (_canvasRef == null && _uiRoot != null)
                _canvasRef = _uiRoot.GetComponentInParent<Canvas>();
            if (_canvasRef == null) return;

            RectTransform cv = _canvasRef.transform as RectTransform;
            if (cv == null) return;

            // GetWorldCorners 顺序固定：0=左下 1=左上 2=右上 3=右下
            Vector3[] cc = new Vector3[4];
            Vector3[] vc = new Vector3[4];
            rt.GetWorldCorners(cc);
            cv.GetWorldCorners(vc);

            float dx = 0f;
            float dy = 0f;

            // 横向：优先保证右边不越界（面板是靠右摆的），左边越界只在极端窄窗口出现
            if (cc[2].x > vc[2].x + 0.5f) dx = vc[2].x - cc[2].x;
            else if (cc[0].x < vc[0].x - 0.5f) dx = vc[0].x - cc[0].x;

            // 纵向：优先保证顶部不越界（标题行最要紧）
            if (cc[1].y > vc[1].y + 0.5f) dy = vc[1].y - cc[1].y;
            else if (cc[3].y < vc[3].y - 0.5f)
            {
                // 底部越界时上移，但上移量不能把顶边顶出去
                float up = vc[3].y - cc[3].y;
                float room = Mathf.Max(0f, cc[1].y - vc[1].y);
                dy = Mathf.Min(up, room);
            }

            if (Mathf.Abs(dx) > 0.5f || Mathf.Abs(dy) > 0.5f)
                rt.position += new Vector3(dx, dy, 0f);
        }

        // ================================================================ 环境准备

        private RectTransform EnsureCanvas()
        {
            Canvas canvas = GetComponent<Canvas>();
            RectTransform root = transform as RectTransform;

            if (canvas == null)
            {
                if (root != null)
                {
                    // 常见路径：编辑器菜单 ⑥ 建的 [TwinUI]，本身就是 Canvas
                    canvas = gameObject.AddComponent<Canvas>();
                }
                else
                {
                    // 兜底：被挂到普通物体上（没有 RectTransform）。另建子节点承载 Canvas，
                    // 不去改动物体本身，避免把别人的 Transform 结构搞乱。
                    GameObject child = new GameObject("TwinUICanvas", typeof(RectTransform));
                    child.transform.SetParent(transform, false);
                    canvas = child.AddComponent<Canvas>();
                    root = (RectTransform)child.transform;
                }
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 100;
            }

            if (canvas.GetComponent<CanvasScaler>() == null)
            {
                CanvasScaler scaler = canvas.gameObject.AddComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920f, 1080f);
                scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
                scaler.matchWidthOrHeight = 0.5f;
            }

            if (canvas.GetComponent<GraphicRaycaster>() == null)
                canvas.gameObject.AddComponent<GraphicRaycaster>();

            if (root == null) root = canvas.transform as RectTransform;
            if (root == null) Debug.LogError("[TwinUI] 取不到 RectTransform，界面无法构建。请用菜单「P1 孪生框架 / ⑥」创建面板。");
            return root;
        }

        /// <summary>没有 EventSystem 就没有点击事件（告警条目点不动）。项目用旧版 Input Manager，用 StandaloneInputModule。</summary>
        private static void EnsureEventSystem()
        {
            if (EventSystem.current != null) return;
            if (FindObjectOfType<EventSystem>() != null) return;

            GameObject go = new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
            Debug.Log("[TwinUI] 场景缺少 EventSystem，已自动创建（否则 UI 点击无效）。");
        }

        // ================================================================ 构建界面

        /// <summary>
        /// 外部（菜单、自定义脚本）要求重建界面时用这个。
        /// 会先拆干净再重建 —— 直接调 BuildUI 会被 _built 挡掉，什么都不发生。
        /// </summary>
        public void RebuildUI()
        {
            if (!_built)
            {
                // 还没构建过（Awake 尚未跑或运行中刚挂上）：直接构建即可
                BuildUI();
            }
            else
            {
                TeardownUI();
                BuildUI();
            }
            ApplySelection();
            RefreshAlarms();
        }

        private void BuildUI()
        {
            if (_uiRoot == null) return;
            if (_built) return;
            _built = true;

            ApplyScale();   // 尺寸必须先展开，后面所有 Place/Label 都依赖它

            // ---------------- 详情卡片 ----------------
            _card = (RectTransform)TwinUIFactory.Node("DetailCard", _uiRoot).transform;
            _cardBg = TwinUIFactory.Rounded(_card, "Bg", 14f * _activeScale,
                TwinStatusPalette.PanelTop, TwinStatusPalette.PanelBottom,
                TwinStatusPalette.PanelBorder, 1f, true);
            TwinUIFactory.Stretch(_cardBg.rectTransform);
            _cardGroup = _card.gameObject.AddComponent<CanvasGroup>();

            _title = TwinUIFactory.Label(_card, "Title", _scaledFsTitle, TextAnchor.UpperLeft,
                TwinStatusPalette.TextPrimary, FontStyle.Bold);
            TwinUIFactory.Place(_title.rectTransform, _scaledPad, _scaledTitleY,
                _scaledCardWidth - _scaledPad * 2f - _scaledChipSpace, _scaledTitleY + _scaledFsTitle + 6f);

            _chip = TwinUIFactory.Rounded(_card, "StatusChip", 12f * _activeScale,
                TwinStatusPalette.Unknown, TwinStatusPalette.Unknown,
                TwinStatusPalette.Unknown, 1f, false);
            TwinUIFactory.Place(_chip.rectTransform, _scaledCardWidth - _scaledPad - _scaledChipWidth,
                _scaledTitleY + 1f, _scaledChipWidth, _scaledChipHeight);

            _chipText = TwinUIFactory.Label(_chip.transform, "ChipText", _scaledFsChip, TextAnchor.MiddleCenter,
                TwinStatusPalette.TextPrimary, FontStyle.Bold);
            TwinUIFactory.Stretch(_chipText.rectTransform);

            _subtitle = TwinUIFactory.Label(_card, "Subtitle", _scaledFsSubtitle, TextAnchor.UpperLeft,
                TwinStatusPalette.TextSecondary);
            TwinUIFactory.Place(_subtitle.rectTransform, _scaledPad, _scaledSubtitleY,
                _scaledCardWidth - _scaledPad * 2f, _scaledFsSubtitle + 7f);

            _divider1 = TwinUIFactory.Block(_card, "Divider1", TwinStatusPalette.Divider, false);
            TwinUIFactory.Place(_divider1.rectTransform, _scaledPad, _scaledDivider1Y,
                _scaledCardWidth - _scaledPad * 2f, 1f);

            _hint = TwinUIFactory.Label(_card, "Hint", _scaledFsHint, TextAnchor.UpperLeft,
                TwinStatusPalette.TextMuted, FontStyle.Normal, HorizontalWrapMode.Wrap);
            TwinUIFactory.Place(_hint.rectTransform, _scaledPad, _scaledMetricsTop,
                _scaledCardWidth - _scaledPad * 2f, _scaledRowHeight * 2f);

            _divider2 = TwinUIFactory.Block(_card, "Divider2", TwinStatusPalette.Divider, false);
            TwinUIFactory.Place(_divider2.rectTransform, _scaledPad, 0f,
                _scaledCardWidth - _scaledPad * 2f, 1f);

            _footer = TwinUIFactory.Label(_card, "Footer", _scaledFsFooter, TextAnchor.UpperLeft,
                TwinStatusPalette.TextMuted);
            TwinUIFactory.Place(_footer.rectTransform, _scaledPad, 0f,
                _scaledCardWidth - _scaledPad * 2f, _scaledFooterHeight);

            // ---------------- 告警面板 ----------------
            _alarmPanel = (RectTransform)TwinUIFactory.Node("AlarmPanel", _uiRoot).transform;
            TwinRoundedRect alarmBg = TwinUIFactory.Rounded(_alarmPanel, "Bg", 14f * _activeScale,
                TwinStatusPalette.PanelTop, TwinStatusPalette.PanelBottom,
                TwinStatusPalette.PanelBorder, 1f, true);
            TwinUIFactory.Stretch(alarmBg.rectTransform);

            _alarmTitle = TwinUIFactory.Label(_alarmPanel, "AlarmTitle", _scaledFsAlarmTitle, TextAnchor.UpperLeft,
                TwinStatusPalette.TextSecondary, FontStyle.Bold);
            TwinUIFactory.Place(_alarmTitle.rectTransform, _scaledPad, _scaledAlarmTitleY,
                _scaledCardWidth - _scaledPad * 2f, _scaledFsAlarmTitle + 8f);

            _alarmEmpty = TwinUIFactory.Label(_alarmPanel, "AlarmEmpty", _scaledFsAlarmEmpty, TextAnchor.UpperLeft,
                TwinStatusPalette.TextMuted);
            TwinUIFactory.Place(_alarmEmpty.rectTransform, _scaledPad, _scaledAlarmRowsTop,
                _scaledCardWidth - _scaledPad * 2f, _scaledFsAlarmEmpty + 8f);

            _alarmMore = TwinUIFactory.Label(_alarmPanel, "AlarmMore", _scaledFsAlarmMore, TextAnchor.UpperLeft,
                TwinStatusPalette.TextMuted);
            TwinUIFactory.Place(_alarmMore.rectTransform, _scaledPad, _scaledAlarmRowsTop,
                _scaledCardWidth - _scaledPad * 2f, _scaledFsAlarmMore + 6f);
            _alarmMore.gameObject.SetActive(false);
        }

        // ================================================================ 选中变化 → 重建静态部分

        private void ApplySelection()
        {
            if (!_built) return;

            List<TwinMetricDefinition> defs = CollectMetrics(_selected, _metricDefBuf);

            // 只有「选中了且确实收到过数据」才铺指标行；
            // 否则指标行与「等待数据…」提示会画在同一块区域里互相压字。
            _hasData = _selected != null && _selected.HasData;
            int shown = _hasData ? defs.Count : 0;

            // ---- 标题区 ----
            if (_selected == null)
            {
                SetCached(_title, ref _cachedTitle, "未选中点位");
                SetCached(_subtitle, ref _cachedSubtitle, "在场景中左键点击孪生点位");
                _chip.gameObject.SetActive(false);
                SetCached(_hint, ref _cachedHint,
                    "漫游：WASD + 右键拖动，Q/E 升降，滚轮推进\n取消选择：Esc      调试 HUD：F3      面板开关：F4");
                _hint.gameObject.SetActive(true);
                _footer.gameObject.SetActive(false);
            }
            else
            {
                // 截断宽度与字号都用缩放后的值，否则放大后标题会溢出到状态胶囊上
                SetCached(_title, ref _cachedTitle, TwinUIFactory.Truncate(
                    _selected.DisplayName, _scaledFsTitle,
                    _scaledCardWidth - _scaledPad * 2f - _scaledChipSpace));
                SetCached(_subtitle, ref _cachedSubtitle,
                    _selected.TwinId + "   ·   " + _selected.Zone + " / " + _selected.Category);

                _chip.gameObject.SetActive(true);
                RefreshChip();

                bool waiting = !_selected.HasData;
                _hint.gameObject.SetActive(waiting);
                if (waiting) SetCached(_hint, ref _cachedHint, "等待数据…\n请确认数据源已启动（TwinRuntime 下的 MockDataSource）。");

                _footer.gameObject.SetActive(true);
            }

            // ---- 指标行 ----
            for (int i = 0; i < _metricRows.Count; i++)
                _metricRows[i].Root.gameObject.SetActive(i < shown);

            for (int i = 0; i < shown; i++)
            {
                MetricRow row = EnsureMetricRow(i);
                row.Root.gameObject.SetActive(true);
                ConfigureMetricRow(row, defs[i]);
            }
            // ---- 卡片高度与底部元素 ----
            int slots = Mathf.Max(1, shown);
            float metricsBlock = slots * _scaledRowHeight + (slots - 1) * _scaledRowGap;
            float divider2Y = _scaledMetricsTop + metricsBlock + 8f * _activeScale;
            float footerY = divider2Y + 1f + 6f * _activeScale;
            float cardHeight = footerY + _scaledFooterHeight + 14f * _activeScale;

            TwinUIFactory.Place(_divider2.rectTransform, _scaledPad, divider2Y,
                _scaledCardWidth - _scaledPad * 2f, 1f);
            TwinUIFactory.Place(_footer.rectTransform, _scaledPad, footerY,
                _scaledCardWidth - _scaledPad * 2f, _scaledFooterHeight);

            TwinUIFactory.AnchorTopRight(_card,
                _scaledScreenMargin + _scaledExtraRight,
                _scaledScreenMargin + _scaledExtraTop,
                _scaledCardWidth, cardHeight);
        }

        /// <summary>
        /// 布局自检：把「画布以为的尺寸」和「屏幕实际可见的尺寸」都打出来。
        ///
        /// 【为什么要打】面板被屏幕边缘切掉时，光看代码判断不出是尺寸算错还是定位偏了。
        /// 有这一行就能直接看出：
        ///   · 画布 rect 与可见尺寸不一致 → 画布没按当前分辨率刷新（定位会偏）
        ///   · 卡片 x + w 超过可见宽度     → 尺寸约束没生效
        /// 只打一次，不影响性能。
        /// </summary>
        private void LogLayoutOnce()
        {
            if (_uiRoot == null) return;

            Canvas canvas = _uiRoot.GetComponentInParent<Canvas>();
            // 画布的 scaleFactor 要到第一次布局之后才算得出来，
            // 太早打出来是一堆 0，反而误导 —— 等它有效再打。
            if (canvas == null || canvas.scaleFactor <= 0.0001f) return;
            if (_card == null || _card.sizeDelta.y <= 0f) return;

            // 【为什么不是「只打一次」】用户在 Play 里拖 Game 视图大小后，
            // 一辈子只打一次的日志就和现场对不上了，会被拿去当证据得出错误结论
            // （2026-10-21 就因此误判过一次）。改成「分辨率变了就重打」。
            if (_loggedW == Screen.width && _loggedH == Screen.height) return;
            _loggedW = Screen.width;
            _loggedH = Screen.height;

            // 世界坐标包围盒：这是「事实」，不依赖任何坐标系换算假设。
            // 卡片右上角 x 若大于画布右上角 x，就是真的出界了。
            RectTransform cv = canvas.transform as RectTransform;
            Vector3[] cc = new Vector3[4];
            Vector3[] vc = new Vector3[4];
            _card.GetWorldCorners(cc);
            if (cv != null) cv.GetWorldCorners(vc);

            Debug.Log(string.Format(
                "[TwinUI] 布局自检  Screen={0}×{1}  scaleFactor={2:0.###}  " +
                "画布rect={3}  画布世界右缘={4:0.#}  缩放={5:0.###}  边距={6:0.#}(左移{7:0.#}/下移{8:0.#})  " +
                "卡片 rect={9}  卡片世界右缘={10:0.#} 上缘={11:0.#}  " +
                "越界=右{12:0.#}/上{13:0.#}",
                Screen.width, Screen.height, canvas.scaleFactor,
                _uiRoot.rect, vc[2].x, _activeScale,
                _scaledScreenMargin, _scaledExtraRight, _scaledExtraTop,
                _card.rect, cc[2].x, cc[1].y,
                cc[2].x - vc[2].x, cc[1].y - vc[1].y));
        }

        private List<TwinMetricDefinition> CollectMetrics(TwinAnchor anchor, List<TwinMetricDefinition> buffer)
        {
            buffer.Clear();
            if (anchor == null) return buffer;

            TwinRuntime runtime = TwinRuntime.Instance;
            TwinCatalog catalog = runtime != null ? runtime.Catalog : null;
            TwinPointDefinition point = catalog != null ? catalog.Find(anchor.TwinId) : null;

            // 首选点位目录：它是单位、量程、阈值的唯一来源，顺序也稳定
            if (point != null && point.Metrics != null)
            {
                for (int i = 0; i < point.Metrics.Count && buffer.Count < MaxMetricRows; i++)
                {
                    if (point.Metrics[i] != null) buffer.Add(point.Metrics[i]);
                }
                if (buffer.Count > 0) return buffer;
            }

            // 兜底：目录里没有这个点位，就按实际收到的数据列出（无量程/阈值信息）
            foreach (KeyValuePair<string, TwinValue> kv in anchor.Values)
            {
                if (buffer.Count >= MaxMetricRows) break;
                buffer.Add(SyntheticMetric(kv.Key));
            }
            return buffer;
        }

        /// <summary>目录缺失时的兜底定义。缓存复用，避免每次选中都 new。</summary>
        private TwinMetricDefinition SyntheticMetric(string metric)
        {
            TwinMetricDefinition def;
            if (_syntheticMetrics.TryGetValue(metric, out def)) return def;

            def = new TwinMetricDefinition();
            def.Metric = metric;
            def.Unit = "";
            def.Min = 0f;
            def.Max = 100f;
            def.WarnLow = 0f;
            def.WarnHigh = 100f;
            def.AlarmLow = 0f;
            def.AlarmHigh = 100f;
            def.Epsilon = 0.01f;
            _syntheticMetrics[metric] = def;
            return def;
        }

        // ================================================================ 指标行

        private MetricRow EnsureMetricRow(int index)
        {
            while (_metricRows.Count <= index)
            {
                int i = _metricRows.Count;
                MetricRow row = new MetricRow();
                float s = _activeScale;
                float width = _scaledCardWidth - _scaledPad * 2f;
                float top = _scaledMetricsTop + i * (_scaledRowHeight + _scaledRowGap);

                GameObject go = TwinUIFactory.Node("Metric" + i, _card);
                row.Root = (RectTransform)go.transform;
                TwinUIFactory.Place(row.Root, _scaledPad, top, width, _scaledRowHeight);

                // 先建量程条，后建文字 —— UGUI 里后建的兄弟节点渲染在上层，
                // 这样文字永远不会被量程游标盖住。
                row.BarWidth = width;

                row.Track = TwinUIFactory.Block(go.transform, "Track", TwinStatusPalette.Track, false);
                TwinUIFactory.Place(row.Track.rectTransform, 0f, _scaledBarY, width, _scaledBarHeight);

                row.Bands = new Image[MaxBands];
                for (int b = 0; b < MaxBands; b++)
                {
                    row.Bands[b] = TwinUIFactory.Block(go.transform, "Band" + b, TwinStatusPalette.Transparent, false);
                    TwinUIFactory.Place(row.Bands[b].rectTransform, 0f, _scaledBarY, 0f, _scaledBarHeight);
                }

                row.Marker = TwinUIFactory.Block(go.transform, "Marker", TwinStatusPalette.TextPrimary, false);
                TwinUIFactory.Place(row.Marker.rectTransform, 0f, _scaledBarY - 3f * s, 3f * s, 14f * s);

                row.Name = TwinUIFactory.Label(go.transform, "Name", _scaledFsMetricName, TextAnchor.UpperLeft,
                    TwinStatusPalette.TextSecondary);
                TwinUIFactory.Place(row.Name.rectTransform, 0f, 1f, width * 0.62f, _scaledFsMetricName + 9f);

                row.Value = TwinUIFactory.Label(go.transform, "Value", _scaledFsMetricValue, TextAnchor.UpperRight,
                    TwinStatusPalette.TextPrimary, FontStyle.Bold);
                TwinUIFactory.Place(row.Value.rectTransform, 0f, 0f, width, _scaledFsMetricValue + 8f);

                row.Hint = TwinUIFactory.Label(go.transform, "Hint", _scaledFsMetricHint, TextAnchor.UpperLeft,
                    TwinStatusPalette.TextMuted);
                TwinUIFactory.Place(row.Hint.rectTransform, 0f, _scaledBarY + 12f * s, width, _scaledFsMetricHint + 5f);

                // 趋势线最后建 —— 它铺满整行宽度，但要留在文字下层不挡字。
                // 实际上它画在 y=62 以下的空白区，和上面三行不重叠。
                GameObject sparkGo = TwinUIFactory.Node("Spark", go.transform);
                row.Spark = sparkGo.AddComponent<TwinSparkline>();
                TwinUIFactory.Place((RectTransform)sparkGo.transform, 0f, _scaledSparkY,
                    width, _scaledSparkHeight);

                _metricRows.Add(row);
            }
            return _metricRows[index];
        }

        private void ConfigureMetricRow(MetricRow row, TwinMetricDefinition def)
        {
            row.MetricName = def.Metric;
            row.Unit = def.Unit;
            row.Min = def.Min;
            row.Max = def.Max;
            row.WarnLow = def.WarnLow;
            row.WarnHigh = def.WarnHigh;
            row.AlarmLow = def.AlarmLow;
            row.AlarmHigh = def.AlarmHigh;

            SetCached(row.Name, ref row.LastNameText, MetricLabel(def.Metric));

            // 趋势线的纵轴跟量程条一致；换了指标就清空历史（否则会拿旧指标的数据画新线）
            if (row.Spark != null)
            {
                row.Spark.SetRange(def.Min, def.Max);
                row.Spark.Clear();
            }

            BuildBands(row);
            row.LastValueText = null;   // 强制下一帧刷新数值与提示
            row.LastHintText = null;
        }

        /// <summary>
        /// 阈值带：把量程按 告警 / 警告 / 正常 切成最多 5 段铺在量程条上。
        /// 只在选中变化时算一次 —— 阈值不随数据变，没必要每帧重算。
        /// </summary>
        private void BuildBands(MetricRow row)
        {
            float span = row.Max - row.Min;
            int used = 0;

            if (span > 0.0001f)
            {
                used += EmitBand(row, used, row.Min, row.AlarmLow, TwinStatus.Alarm);
                used += EmitBand(row, used, row.AlarmLow, row.WarnLow, TwinStatus.Warning);
                used += EmitBand(row, used, row.WarnLow, row.WarnHigh, TwinStatus.Normal);
                used += EmitBand(row, used, row.WarnHigh, row.AlarmHigh, TwinStatus.Warning);
                used += EmitBand(row, used, row.AlarmHigh, row.Max, TwinStatus.Alarm);
            }

            for (int i = used; i < MaxBands; i++)
                row.Bands[i].gameObject.SetActive(false);
        }

        private int EmitBand(MetricRow row, int slot, float from, float to, TwinStatus status)
        {
            if (slot >= MaxBands) return 0;

            float span = row.Max - row.Min;
            if (span <= 0.0001f) return 0;

            float a = Mathf.Clamp01((from - row.Min) / span);
            float b = Mathf.Clamp01((to - row.Min) / span);
            float x = Mathf.Min(a, b) * row.BarWidth;
            float w = Mathf.Abs(b - a) * row.BarWidth;

            if (w < 1f)
            {
                row.Bands[slot].gameObject.SetActive(false);
                return 1;   // 占位但不可见，保持槽位顺序稳定
            }

            Image band = row.Bands[slot];
            band.gameObject.SetActive(true);
            band.color = TwinStatusPalette.Of(status, 0.38f);
            TwinUIFactory.Place(band.rectTransform, x, _scaledBarY, w, _scaledBarHeight);
            return 1;
        }

        // ================================================================ 状态徽章

        /// <summary>
        /// 刷新标题右侧的状态胶囊。
        ///
        /// 【为什么必须单独抽出来、并且每 0.1s 调一次】
        /// 它原先写在 ApplySelection() 里，而 ApplySelection() 只在
        /// 「选中变化 / 有无数据变化」时才跑。于是胶囊被冻结在**点选那一刻**的状态：
        ///
        ///   选中时正常（绿）→ 之后这个点位真的告警了 → 头部还是绿的
        ///
        /// 这是最坏的方向 —— 告警列表里已经红了，面板顶部却还在说「正常」，
        /// 用户盯着标题看就以为没事。反之（选中时告警、后来恢复）也会一直红着，
        /// 让人以为修不好。
        ///
        /// 刷新成本可控：SetFill / SetBorder 内部会比对颜色，值没变就提前返回，
        /// 不会每个周期重建网格；文字走 SetCached，内容相同不重写。
        /// </summary>
        private void RefreshChip()
        {
            if (_chip == null || _selected == null) return;

            TwinStatus status = CurrentStatus(_selected);
            Color statusColor = TwinStatusPalette.Of(status);

            _chip.SetFill(TwinStatusPalette.Of(status, 0.20f), TwinStatusPalette.Of(status, 0.20f));
            _chip.SetBorder(TwinStatusPalette.Of(status, 0.65f), 1f);
            SetCached(_chipText, ref _cachedChip, TwinStatusPalette.Label(status));

            if (_chipText == null) return;

            // 文字颜色 = 状态色，但**底色太暗时必须改用主文字色**。
            // Offline(0.28) / Unknown(0.38) / Alarm(0.41) 这几个状态色按 BT.601
            // 算出来的感知亮度都不到 0.45，压在 20% 同色底的胶囊上几乎看不见 ——
            // 「离线」这两个字看不清，等于这个状态白设了。
            float lum = 0.299f * statusColor.r + 0.587f * statusColor.g + 0.114f * statusColor.b;
            Color textColor = lum < 0.45f ? TwinStatusPalette.TextPrimary : statusColor;
            if (_chipText.color != textColor) _chipText.color = textColor;
        }

        // ================================================================ 数值刷新（10Hz）

        private void RefreshValues()
        {
            if (_selected == null) return;

            // 状态会随数据变，胶囊必须跟着走（见 RefreshChip 的注释）
            RefreshChip();

            // 趋势线的时间轴要一直往前走：即使这个指标这一刻没有新数据，
            // 线也得继续往左滚，否则「数据停了」会看起来像「数据没变」。
            float now = Time.unscaledTime;

            for (int i = 0; i < _metricRows.Count; i++)
            {
                MetricRow row = _metricRows[i];
                if (row.Root == null || !row.Root.gameObject.activeSelf) continue;
                if (string.IsNullOrEmpty(row.MetricName)) continue;

                if (row.Spark != null) row.Spark.Tick(now);

                TwinValue value;
                if (!_selected.TryGetValue(row.MetricName, out value))
                {
                    SetCached(row.Value, ref row.LastValueText, "--");
                    continue;
                }

                // 趋势线采样：RefreshValues 是 10Hz，但曲线没必要 10Hz 存 ——
                // 60 秒窗口按 5Hz 存 300 个点，顶点数减半，肉眼完全看不出差别。
                // 用 unscaledTime 而不是缩放时间：暂停/慢放时曲线不该跟着停。
                if (row.Spark != null && now - row.LastPushTime >= SparkSampleInterval)
                {
                    row.LastPushTime = now;
                    row.Spark.Push(now, value.Value);
                }

                // 数值文字按该指标自己的状态着色
                TwinStatus status = _selected.StatusOf(row.MetricName);
                Color color = TwinStatusPalette.Of(status);
                if (row.Value.color != color) row.Value.color = color;

                _sb.Length = 0;
                _sb.Append(FormatValue(row.Unit, value.Value));
                SetCached(row.Value, ref row.LastValueText, _sb.ToString());

                // 量程游标
                float t = row.Max > row.Min ? Mathf.Clamp01((value.Value - row.Min) / (row.Max - row.Min)) : 0.5f;
                row.Marker.rectTransform.anchoredPosition =
                    new Vector2(t * row.BarWidth - 1.5f * _activeScale,
                                -(_scaledBarY - 3f * _activeScale));
                row.Marker.color = color;

                _sb.Length = 0;
                AppendHint(_sb, row, value);
                SetCached(row.Hint, ref row.LastHintText, _sb.ToString());
            }

            // 页脚：数据新鲜度 + 数据质量
            _sb.Length = 0;
            _sb.Append("数据 ");
            float age = _selected.SecondsSinceLastSample;
            _sb.Append(age < 0f ? "尚未收到" : age.ToString("0.0") + " 秒前");
            _sb.Append("   ·   质量 ").Append(TwinStatusPalette.QualityLabel(WorstQuality(_selected)));
            _sb.Append("   ·   指标 ").Append(_selected.MetricCount).Append(" 项");
            SetCached(_footer, ref _cachedFooter, _sb.ToString());
        }

        private static string FormatValue(string unit, float value)
        {
            if (string.Equals(unit, "0/1", StringComparison.Ordinal))
                return value >= 0.5f ? "开启" : "关闭";

            string text = value.ToString("0.##");
            if (!string.IsNullOrEmpty(unit)) text += " " + unit;
            return text;
        }

        private static void AppendHint(StringBuilder sb, MetricRow row, TwinValue value)
        {
            bool binary = string.Equals(row.Unit, "0/1", StringComparison.Ordinal);

            sb.Append("量程 ").Append(row.Min.ToString("0.#")).Append('~').Append(row.Max.ToString("0.#"));
            if (!binary && !string.IsNullOrEmpty(row.Unit)) sb.Append(row.Unit);

            sb.Append("   ·   正常 ").Append(row.WarnLow.ToString("0.#")).Append('~').Append(row.WarnHigh.ToString("0.#"));

            float delta = value.Delta;
            if (Mathf.Abs(delta) < 0.005f)
            {
                sb.Append("   ·   稳定");
            }
            else
            {
                sb.Append("   ·   ").Append(delta > 0f ? "▲" : "▼").Append(Mathf.Abs(delta).ToString("0.##"));
            }

            if (value.Quality != DataQuality.Good)
                sb.Append("   ·   ").Append(TwinStatusPalette.QualityLabel(value.Quality));
        }

        // ================================================================ 告警列表（4Hz）

        private void RefreshAlarms()
        {
            if (!_built) return;

            TwinRuntime runtime = TwinRuntime.Instance;
            _alarmTop.Clear();
            _alarmTotal = 0;
            _twinTotal = 0;

            int worstSeverity = -1;
            TwinStatus worstStatus = TwinStatus.Unknown;

            // 只维持「前 capacity 名」的有序小数组，其余只计数。
            // 这样：① 不为不显示的行排序；② 每个点位的状态只算一次。
            // capacity 会被屏幕高度裁剪 —— 6 个指标 + 8 条告警在 1080p 下会超出屏幕，
            // 所以先算「卡片下方还剩多少高度」再决定能放几行。
            int capacity = FitAlarmCapacity(Mathf.Max(1, _maxAlarmRows));

            if (runtime != null && runtime.Registry != null)
            {
                IReadOnlyList<TwinAnchor> all = runtime.Registry.All;
                _twinTotal = all.Count;

                for (int i = 0; i < all.Count; i++)
                {
                    TwinAnchor anchor = all[i];
                    if (anchor == null) continue;

                    TwinStatus status = CurrentStatus(anchor);

                    // 未配置阈值的点位（Unknown 且有数据）也要进列表：
                    // 它有数据、但我们判不了好坏，不能让它悄悄从告警列表里消失 ——
                    // 那等于「这个点位没被监管」，却还占着「一切正常」的名额。
                    // 注意：Unknown 不在 NeedsAttention 里，所以它只进列表、不触发告警音
                    // （配置缺失是开发期问题，不该在演示/值班时一直响）。
                    bool needsLook = TwinStatusPalette.NeedsAttention(status)
                                     || (status == TwinStatus.Unknown && anchor.HasData);
                    if (!needsLook) continue;

                    _alarmTotal++;

                    int severity = TwinStatusPalette.Severity(status);
                    if (severity > worstSeverity)
                    {
                        worstSeverity = severity;
                        worstStatus = status;
                    }

                    InsertTopAlarm(anchor, status, severity, capacity);
                }
            }

            int shown = _alarmTop.Count;

            for (int i = 0; i < shown; i++)
            {
                TwinAlarmRow row = EnsureAlarmRow(i);
                row.gameObject.SetActive(true);

                AlarmEntry entry = _alarmTop[i];
                Color color = TwinStatusPalette.Of(entry.Status);

                row.Target = entry.Anchor;
                row.SetBaseColor(new Color(color.r, color.g, color.b, 0.10f));
                if (row.Dot != null) row.Dot.color = color;
                if (row.Label != null) row.Label.color = TwinStatusPalette.TextPrimary;

                _sbAlarm.Length = 0;
                _sbAlarm.Append(TwinStatusPalette.Label(entry.Status)).Append("  ·  ")
                        .Append(entry.Anchor.DisplayName).Append("   ")
                        .Append(DescribeAlarm(entry.Anchor, entry.Status));
                SetCached(row.Label, ref row.LastText, _sbAlarm.ToString());
            }

            for (int i = shown; i < _alarmRows.Count; i++) _alarmRows[i].gameObject.SetActive(false);

            bool hasAlarm = _alarmTotal > 0;

            if (_alarmEmpty != null)
            {
                _alarmEmpty.gameObject.SetActive(!hasAlarm);
                if (!hasAlarm) SetCached(_alarmEmpty, ref _cachedAlarmEmpty, "当前无告警 · 全部点位正常");
            }

            if (_alarmTitle != null)
            {
                // 压测观察窗口：点位总数也要显示出来。
                // P1-C 时 5 个点位，「(2)」就够了；P1-E 压到 500 个点位时，
                // 若只显示「(37)」会让人以为是 37/37，看不出「37 / 500」的全局比例。
                _sbAlarm.Length = 0;
                _sbAlarm.Append("告警与异常");
                if (hasAlarm) _sbAlarm.Append("  (").Append(_alarmTotal).Append(')');
                if (_twinTotal > 0) _sbAlarm.Append("  ·  共 ").Append(_twinTotal).Append(" 点位");
                SetCached(_alarmTitle, ref _cachedAlarmTitle, _sbAlarm.ToString());

                _alarmTitle.color = hasAlarm
                    ? TwinStatusPalette.Of(worstStatus)
                    : TwinStatusPalette.TextSecondary;
            }

            // 行数或溢出数量变化都要重排；这里直接每次都重排 —— 4Hz、单个 RectTransform，
            // 开销可忽略，换来的好处是逻辑不用记状态。
            PlaceAlarmPanel(shown);
        }

        /// <summary>
        /// 把一条告警插入「前 capacity 名」的有序数组。
        ///
        /// 小数组插入排序：容量固定为 _maxAlarmRows（默认 8），
        /// 所以最坏情况 8 次比较，远优于对全部异常点位排序。
        /// 排序键：严重度降序 → TwinId 升序（保证同一严重度下顺序稳定，界面不闪）。
        /// </summary>
        private void InsertTopAlarm(TwinAnchor anchor, TwinStatus status, int severity, int capacity)
        {
            int insertAt = _alarmTop.Count;

            for (int i = 0; i < _alarmTop.Count; i++)
            {
                AlarmEntry e = _alarmTop[i];
                if (severity > e.Severity)
                {
                    insertAt = i;
                    break;
                }
                if (severity == e.Severity)
                {
                    string a = anchor != null ? anchor.TwinId : "";
                    string b = e.Anchor != null ? e.Anchor.TwinId : "";
                    if (string.CompareOrdinal(a, b) < 0)
                    {
                        insertAt = i;
                        break;
                    }
                }
            }

            // 已经填满且排不进去，直接丢弃（这类点位已经在 _alarmTotal 里计过数了）
            if (insertAt >= capacity) return;

            AlarmEntry entry;
            entry.Anchor = anchor;
            entry.Status = status;
            entry.Severity = severity;

            if (insertAt >= _alarmTop.Count) _alarmTop.Add(entry);
            else _alarmTop.Insert(insertAt, entry);

            // 超出容量则裁掉末尾
            if (_alarmTop.Count > capacity) _alarmTop.RemoveAt(_alarmTop.Count - 1);
        }

        private TwinAlarmRow EnsureAlarmRow(int index)
        {
            while (_alarmRows.Count <= index)
            {
                int i = _alarmRows.Count;
                float s = _activeScale;
                float width = _scaledCardWidth - _scaledPad * 2f;

                GameObject go = TwinUIFactory.Node("AlarmRow" + i, _alarmPanel);
                TwinUIFactory.Place((RectTransform)go.transform, _scaledPad,
                    _scaledAlarmRowsTop + i * (_scaledAlarmRowHeight + _scaledAlarmRowGap),
                    width, _scaledAlarmRowHeight);

                // 行背景必须是 raycastTarget，否则收不到点击
                Image bg = TwinUIFactory.Block(go.transform, "Bg", TwinStatusPalette.Transparent, true);
                TwinUIFactory.Stretch(bg.rectTransform);

                float dotSize = 8f * s;
                Image dot = TwinUIFactory.Block(go.transform, "Dot", TwinStatusPalette.Unknown, false);
                TwinUIFactory.Place(dot.rectTransform, 8f * s, (_scaledAlarmRowHeight - dotSize) * 0.5f,
                    dotSize, dotSize);

                Text label = TwinUIFactory.Label(go.transform, "Label", _scaledFsAlarmRow, TextAnchor.MiddleLeft,
                    TwinStatusPalette.TextSecondary);
                TwinUIFactory.Place(label.rectTransform, 24f * s, 0f, width - 32f * s, _scaledAlarmRowHeight);

                TwinAlarmRow row = go.AddComponent<TwinAlarmRow>();
                row.Owner = this;
                row.Background = bg;
                row.Dot = dot;
                row.Label = label;
                _alarmRows.Add(row);
            }
            return _alarmRows[index];
        }

        /// <summary>
        /// 按「屏幕上还剩多少高度」裁剪告警行数。
        ///
        /// 【与 MaxScaleByVerticalSpace 的分工】
        /// MaxScaleByVerticalSpace 保证「满配内容」不溢出，所以正常情况下这里不会削掉任何行。
        /// 但它在算上限时用的是「指标占满 6 行」的假设；若某个点位只有 1~2 个指标，
        /// 卡片会矮很多，此时可以多显示几条告警 —— 反过来也成立。
        /// 所以这里按**当前实际卡片高度**再算一次，两头都不浪费。
        ///
        /// 【为什么不用 _card.sizeDelta.y】RefreshAlarms 可能先于 ApplySelection 跑，
        /// 那时卡片高度还是旧值。这里用同一个公式现算，不依赖时序。
        /// </summary>
        private int FitAlarmCapacity(int desired)
        {
            float s = _activeScale;
            float viewport = ViewportHeight();

            // 当前实际卡片高度（与 ApplySelection 用同一套公式）
            int slots = Mathf.Max(1, _hasData ? _metricRows.Count : 1);
            float metricsBlock = slots * _scaledRowHeight + (slots - 1) * _scaledRowGap;
            float divider2Y = _scaledMetricsTop + metricsBlock + 8f * s;
            float footerY = divider2Y + 1f + 6f * s;
            float cardH = footerY + _scaledFooterHeight + 14f * s;
            if (_selected == null) cardH = _scaledMetricsTop + _scaledRowHeight * 2f + 14f * s;

            float avail = viewport - _scaledScreenMargin - cardH - _scaledPanelGap - _scaledScreenMargin;

            // 面板自身固定开销：标题区 + 1 行 + 底部预留 + 「还有更多」提示行
            float overhead = _scaledAlarmRowsTop + _scaledAlarmRowHeight + 14f * s
                             + _scaledFsAlarmMore + 8f;

            float rowStep = _scaledAlarmRowHeight + _scaledAlarmRowGap;
            int fit = Mathf.FloorToInt((avail - overhead) / Mathf.Max(1f, rowStep)) + 1;

            return Mathf.Clamp(fit, 1, desired);
        }

        private void PlaceAlarmPanel(int rows)
        {
            if (_alarmPanel == null) return;

            float s = _activeScale;
            bool overflow = rows > 0 && _alarmTotal > rows;
            float height;
            if (rows <= 0)
            {
                height = _scaledAlarmRowsTop + _scaledFsAlarmEmpty + 8f + 14f * s;
            }
            else
            {
                height = _scaledAlarmRowsTop + rows * _scaledAlarmRowHeight
                         + (rows - 1) * _scaledAlarmRowGap + 14f * s;
                if (overflow) height += _scaledFsAlarmMore + 8f;
            }

            float top = _scaledScreenMargin + _scaledExtraTop
                        + (_card != null ? _card.sizeDelta.y : 0f) + _scaledPanelGap;
            TwinUIFactory.AnchorTopRight(_alarmPanel,
                _scaledScreenMargin + _scaledExtraRight, top, _scaledCardWidth, height);

            if (_alarmMore != null)
            {
                _alarmMore.gameObject.SetActive(overflow);
                if (overflow)
                {
                    float y = _scaledAlarmRowsTop + rows * _scaledAlarmRowHeight
                              + (rows - 1) * _scaledAlarmRowGap + 2f;
                    TwinUIFactory.Place(_alarmMore.rectTransform, _scaledPad, y,
                        _scaledCardWidth - _scaledPad * 2f, _scaledFsAlarmMore + 6f);
                    _sbAlarm.Length = 0;
                    _sbAlarm.Append("… 另有 ").Append(_alarmTotal - rows).Append(" 个异常点位");
                    SetCached(_alarmMore, ref _cachedAlarmMore, _sbAlarm.ToString());
                }
            }
        }

        private string DescribeAlarm(TwinAnchor anchor, TwinStatus status)
        {
            if (status == TwinStatus.Stale)
            {
                float age = anchor.SecondsSinceLastSample;
                return age < 0f ? "尚无数据" : age.ToString("0.#") + "s 未更新";
            }
            if (status == TwinStatus.Offline) return "数据源离线";

            // Unknown + 已有数据 = 点位目录里查不到阈值定义。
            // 必须说清是「没配置」而不是「正常」—— 否则运维会以为这个点位已被监管。
            // Unknown 且没有数据 = 还没收到过数据，交给下面的通用文案。
            if (status == TwinStatus.Unknown)
                return anchor.HasData ? "未配置阈值" : TwinStatusPalette.Label(status);

            // 找出最差的那个指标，把它的值一并显示，省得还要点进去看
            string worstMetric = null;
            int worstRank = -1;
            foreach (KeyValuePair<string, TwinValue> kv in anchor.Values)
            {
                int rank = TwinStatusPalette.Severity(anchor.StatusOf(kv.Key));
                if (rank > worstRank)
                {
                    worstRank = rank;
                    worstMetric = kv.Key;
                }
            }
            if (string.IsNullOrEmpty(worstMetric)) return TwinStatusPalette.Label(status);

            // worstRank <= 0 表示所有指标都已回到正常，但 anchor.Status 还停留在上一拍
            // （它是 OnSample 时算出的整机最差，最多滞后一个数据周期）。
            // 此时若仍去显示某个指标的数值，就会出现「警告 · 温度 26.32℃」这种自相矛盾的文案
            // —— 26.32 < WarnHigh(28)，用户一定会问「这为什么是警告」。
            if (worstRank <= 0) return "已恢复";

            TwinValue value;
            if (!anchor.TryGetValue(worstMetric, out value)) return TwinStatusPalette.Label(status);

            TwinRuntime runtime = TwinRuntime.Instance;
            TwinCatalog catalog = runtime != null ? runtime.Catalog : null;
            TwinPointDefinition point = catalog != null ? catalog.Find(anchor.TwinId) : null;
            TwinMetricDefinition def = point != null ? point.FindMetric(worstMetric) : null;

            return MetricLabel(worstMetric) + " " + FormatValue(def != null ? def.Unit : "", value.Value);
        }

        // ================================================================ 对外交互

        /// <summary>告警条目点击回调：选中该点位，并把相机飞过去。</summary>
        public void FocusOn(TwinAnchor anchor)
        {
            if (anchor == null) return;

            TwinPicker.Select(anchor);

            if (!_flyToOnClick) return;
            FreeCameraController controller = FindObjectOfType<FreeCameraController>();
            if (controller != null) controller.FocusOn(anchor.transform, _flyDistance);
        }

        public void SetVisible(bool visible)
        {
            _visible = visible;
            if (_card != null) _card.gameObject.SetActive(visible);
            if (_alarmPanel != null) _alarmPanel.gameObject.SetActive(visible);
        }

        // ================================================================ 工具

        private static void SetCached(Text target, ref string cache, string value)
        {
            if (target == null) return;
            if (string.Equals(cache, value, StringComparison.Ordinal)) return;
            cache = value;
            target.text = value;
        }

        /// <summary>点位整体状态，并把「数据陈旧」也算进去（锚点自身可能还没走到 Stale）。</summary>
        private static TwinStatus CurrentStatus(TwinAnchor anchor)
        {
            if (anchor == null) return TwinStatus.Unknown;
            TwinStatus status = anchor.Status;
            if (anchor.IsStale && status != TwinStatus.Offline) status = TwinStatus.Stale;
            return status;
        }

        private static DataQuality WorstQuality(TwinAnchor anchor)
        {
            DataQuality worst = DataQuality.Good;
            bool any = false;
            foreach (KeyValuePair<string, TwinValue> kv in anchor.Values)
            {
                any = true;
                if (kv.Value.Quality > worst) worst = kv.Value.Quality;
            }
            return any ? worst : DataQuality.Offline;
        }

        /// <summary>指标名 → 中文显示名。目录里存的是英文标识（也是数据字段名），界面上换成人话。</summary>
        private static string MetricLabel(string metric)
        {
            switch (metric)
            {
                case "Temperature": return "温度";
                case "Humidity": return "湿度";
                case "CO2": return "CO₂ 浓度";
                case "Brightness": return "亮度";
                case "Power": return "功率";
                case "SetTemp": return "设定温度";
                case "Open": return "门状态";
                case "Voltage": return "电压";
                case "Current": return "电流";
                case "Pressure": return "压力";
                case "Flow": return "流量";
                case "Level": return "液位";
                default: return string.IsNullOrEmpty(metric) ? "指标" : metric;
            }
        }

        [ContextMenu("打印当前面板状态")]
        private void PrintState()
        {
            Debug.Log("[TwinUI] 可见=" + _visible + "  选中=" + (_selected != null ? _selected.TwinId : "(无)") +
                      "  指标行=" + _metricRows.Count + "  告警行=" + _alarmRows.Count +
                      "  异常点位=" + _alarmTotal + "/" + _twinTotal);
        }
    }
}

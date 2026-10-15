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

        [Tooltip("整体缩放。1 = 原始大小。字体看不清就调大，例如 1.3 / 1.5。\n" +
                 "缩放会同步放大字号、行高、卡片宽度与内边距，不会出现「字大了框没大」的错位。")]
        [Range(0.75f, 2f)]
        [SerializeField] private float _uiScale = 1.35f;

        [Tooltip("卡片宽度（未缩放）。实际宽度 = 该值 × 整体缩放")]
        [SerializeField] private float _cardWidth = 384f;
        [SerializeField] private float _screenMargin = 20f;
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
        [SerializeField] private int _maxAlarmRows = 8;

        // ================================================================ 基础尺寸（未缩放）
        //
        // 【为什么要有一个缩放因子】
        // 面板尺寸由「字号 + 行高 + Y 坐标」三组数字共同决定，它们必须成比例。
        // 只把字号从 19 改成 26，文字会溢出到下一行、量程条会被压住 —— 因为坐标没动。
        // 所以这里把所有尺寸都定义成「基准值」，再统一乘 _uiScale，
        // 改一个滑块就能整体放大而不跑版。

        private const float BaseCardWidth = 384f;
        private const float BaseScreenMargin = 20f;
        private const float BasePad = 16f;
        private const float BaseTitleY = 14f;
        private const float BaseSubtitleY = 44f;
        private const float BaseDivider1Y = 72f;
        private const float BaseMetricsTop = 82f;
        private const float BaseRowHeight = 52f;
        private const float BaseRowGap = 6f;
        private const float BaseBarY = 26f;
        private const float BaseBarHeight = 8f;
        private const float BaseFooterHeight = 16f;
        private const float BaseAlarmTitleY = 16f;
        private const float BaseAlarmRowsTop = 44f;
        private const float BaseAlarmRowHeight = 26f;
        private const float BaseAlarmRowGap = 3f;
        private const float BasePanelGap = 12f;
        private const float BaseChipWidth = 76f;
        private const float BaseChipHeight = 24f;

        // ---- 字号（未缩放）----
        private const int FsTitle = 19;
        private const int FsChip = 12;
        private const int FsSubtitle = 12;
        private const int FsHint = 13;
        private const int FsFooter = 11;
        private const int FsAlarmTitle = 14;
        private const int FsAlarmEmpty = 12;
        private const int FsAlarmMore = 12;
        private const int FsMetricName = 13;
        private const int FsMetricValue = 19;
        private const int FsMetricHint = 11;
        private const int FsAlarmRow = 12;

        private const int MaxMetricRows = 6;
        private const int MaxBands = 5;

        // ================================================================ 缩放后的尺寸（构造时算一次）
        //
        // 只算一次而不是每次排版都乘：布局函数在数值刷新路径之外也会被调用，
        // 反复做乘法除了浪费没有收益。OnValidate 改了 _uiScale 会重建界面。

        private float _scaledCardWidth;
        private float _scaledScreenMargin;
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

        /// <summary>把基准尺寸按 _uiScale 展开成实际尺寸。BuildUI 之前必须调用。</summary>
        private void ApplyScale()
        {
            float s = Mathf.Clamp(_uiScale, 0.5f, 3f);

            _scaledCardWidth = _cardWidth > 0f ? _cardWidth * s : BaseCardWidth * s;
            _scaledScreenMargin = _screenMargin > 0f ? _screenMargin * s : BaseScreenMargin * s;
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
            _cardBg = TwinUIFactory.Rounded(_card, "Bg", 14f * Mathf.Clamp(_uiScale, 0.5f, 3f),
                TwinStatusPalette.PanelTop, TwinStatusPalette.PanelBottom,
                TwinStatusPalette.PanelBorder, 1f, true);
            TwinUIFactory.Stretch(_cardBg.rectTransform);
            _cardGroup = _card.gameObject.AddComponent<CanvasGroup>();

            _title = TwinUIFactory.Label(_card, "Title", _scaledFsTitle, TextAnchor.UpperLeft,
                TwinStatusPalette.TextPrimary, FontStyle.Bold);
            TwinUIFactory.Place(_title.rectTransform, _scaledPad, _scaledTitleY,
                _scaledCardWidth - _scaledPad * 2f - _scaledChipSpace, _scaledTitleY + _scaledFsTitle + 6f);

            _chip = TwinUIFactory.Rounded(_card, "StatusChip", 12f * Mathf.Clamp(_uiScale, 0.5f, 3f),
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
            TwinRoundedRect alarmBg = TwinUIFactory.Rounded(_alarmPanel, "Bg", 14f * Mathf.Clamp(_uiScale, 0.5f, 3f),
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
                TwinStatus status = CurrentStatus(_selected);
                Color statusColor = TwinStatusPalette.Of(status);
                _chip.SetFill(TwinStatusPalette.Of(status, 0.20f), TwinStatusPalette.Of(status, 0.20f));
                _chip.SetBorder(TwinStatusPalette.Of(status, 0.65f), 1f);
                SetCached(_chipText, ref _cachedChip, TwinStatusPalette.Label(status));
                _chipText.color = statusColor;

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
            float divider2Y = _scaledMetricsTop + metricsBlock + 8f * Mathf.Clamp(_uiScale, 0.5f, 3f);
            float footerY = divider2Y + 1f + 6f * Mathf.Clamp(_uiScale, 0.5f, 3f);
            float cardHeight = footerY + _scaledFooterHeight + 14f * Mathf.Clamp(_uiScale, 0.5f, 3f);

            TwinUIFactory.Place(_divider2.rectTransform, _scaledPad, divider2Y,
                _scaledCardWidth - _scaledPad * 2f, 1f);
            TwinUIFactory.Place(_footer.rectTransform, _scaledPad, footerY,
                _scaledCardWidth - _scaledPad * 2f, _scaledFooterHeight);

            TwinUIFactory.AnchorTopRight(_card, _scaledScreenMargin, _scaledScreenMargin,
                _scaledCardWidth, cardHeight);
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
                float s = Mathf.Clamp(_uiScale, 0.5f, 3f);
                float width = _scaledCardWidth - _scaledPad * 2f;
                float top = _scaledMetricsTop + i * (_scaledRowHeight + _scaledRowGap);

                GameObject go = TwinUIFactory.Node("Metric" + i, _card);
                row.Root = (RectTransform)go.transform;
                TwinUIFactory.Place(row.Root, _scaledPad, top, width, _scaledRowHeight);

                // 先建量程条，后建文字 —— UGUI 里后建的兄弟节点渲染在上层，
                // 这样文字永远不会被量程游标盖住。
                row.BarWidth = width;

                float barH = _scaledBarHeight;
                row.Track = TwinUIFactory.Block(go.transform, "Track", TwinStatusPalette.Track, false);
                TwinUIFactory.Place(row.Track.rectTransform, 0f, _scaledBarY, width, barH);

                row.Bands = new Image[MaxBands];
                for (int b = 0; b < MaxBands; b++)
                {
                    row.Bands[b] = TwinUIFactory.Block(go.transform, "Band" + b, TwinStatusPalette.Transparent, false);
                    TwinUIFactory.Place(row.Bands[b].rectTransform, 0f, _scaledBarY, 0f, barH);
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
            TwinUIFactory.Place(band.rectTransform, x, _scaledBarY, w, BarHeight);
            return 1;
        }

        // ================================================================ 数值刷新（10Hz）

        private void RefreshValues()
        {
            if (_selected == null) return;

            for (int i = 0; i < _metricRows.Count; i++)
            {
                MetricRow row = _metricRows[i];
                if (row.Root == null || !row.Root.gameObject.activeSelf) continue;
                if (string.IsNullOrEmpty(row.MetricName)) continue;

                TwinValue value;
                if (!_selected.TryGetValue(row.MetricName, out value))
                {
                    SetCached(row.Value, ref row.LastValueText, "--");
                    continue;
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
                    new Vector2(t * row.BarWidth - 1.5f * Mathf.Clamp(_uiScale, 0.5f, 3f),
                                -(_scaledBarY - 3f * Mathf.Clamp(_uiScale, 0.5f, 3f)));
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

            // 只维持「前 _maxAlarmRows 名」的有序小数组，其余只计数。
            // 这样：① 不为不显示的行排序；② 每个点位的状态只算一次。
            int capacity = Mathf.Max(1, _maxAlarmRows);

            if (runtime != null && runtime.Registry != null)
            {
                IReadOnlyList<TwinAnchor> all = runtime.Registry.All;
                _twinTotal = all.Count;

                for (int i = 0; i < all.Count; i++)
                {
                    TwinAnchor anchor = all[i];
                    if (anchor == null) continue;

                    TwinStatus status = CurrentStatus(anchor);
                    if (!TwinStatusPalette.NeedsAttention(status)) continue;

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
                float s = Mathf.Clamp(_uiScale, 0.5f, 3f);
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

        private void PlaceAlarmPanel(int rows)
        {
            if (_alarmPanel == null) return;

            float s = Mathf.Clamp(_uiScale, 0.5f, 3f);
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

            float top = _scaledScreenMargin + (_card != null ? _card.sizeDelta.y : 0f) + _scaledPanelGap;
            TwinUIFactory.AnchorTopRight(_alarmPanel, _scaledScreenMargin, top, _scaledCardWidth, height);

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

using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using SchoolTwin.Command;
using SchoolTwin.Core;
using SchoolTwin.Twin;

namespace SchoolTwin.UI
{
    /// <summary>
    /// 校园总控面板（按 G 开关）：左层级树 + 右设备表 + 房间级批量控制。
    ///
    /// ── 它和另外两个面板的分工 ────────────────────────────────────
    ///   总控面板（G）：管**面** —— 按楼层/房间浏览、批量控制、全局异常汇总
    ///   详情卡片（F4）：管**点** —— 当前选中点位的全部指标与告警
    ///   点位清单（T）：管**找** —— 平铺全部点位、点一下飞过去（只读）
    /// 「面板太多」的解法不是合并，是分层。所以这里**不做单点详情**，只给一行摘要。
    ///
    /// ── 三条不可协商的界面规则（Spec §10.2）────────────────────────
    /// 1. **不可控设备没有按钮**，不是按钮变灰。灰按钮会让人以为「等会儿能点」，
    ///    而传感器永远不能点。所以按钮是按设备模型**建/不建**，不是 enable/disable。
    /// 2. **批量要有确认**，且逐台显示结果。5 台里 1 台失败必须看得见是哪台。
    /// 3. **命令进行中不许变绿**。颜色只表示结果，过程用文字（Spec §10.5）。
    ///    Pending / Accepted / Actuated 三态全部用次要色 + 文案，只有 Confirmed 才上状态色。
    ///
    /// ── 为什么批量是「一次发完、各自收敛」而不是「排队逐条」──────────
    /// 命令中心已经保证**同设备串行**（IsBusy），不同设备之间没有共享状态，
    /// 排队只会让人等 5 秒还看不到结果。所以确认后一次发完，每行自己显示进度，
    /// 汇总行等全部进入终态（Confirmed / Failed）再给结论。
    ///
    /// ── 排版三层，别混 ───────────────────────────────────────────
    /// `Layout()`     ：窗口几何（标题栏、楼层条、树、表头、视口、手柄），拖动时每帧跑
    /// `LayoutRows()` ：行几何（树行 / 设备行的 y 累加、按钮从右往左排）
    /// `Refresh()`    ：内容（选中哪间房、每行文字与颜色），0.2s 一次
    /// </summary>
    [AddComponentMenu("SchoolTwin/校园总控面板 (TwinControlPanel)")]
    [DisallowMultipleComponent]
    public class TwinControlPanel : MonoBehaviour
    {
        [Header("开关")]
        [SerializeField] private KeyCode _toggleKey = KeyCode.None;   // P28：并入总控抽屉，快捷键停用

        [Header("刷新")]
        [Tooltip("状态刷新间隔（秒）。0.2 与告警区的节奏一致")]
        [SerializeField] private float _refreshInterval = 0.2f;

        // ---------------- 版式常量（未缩放，全部 × _scale）----------------
        private const float BaseWidth = 780f;
        private const float BaseHeight = 440f;
        private const float BasePad = 16f;
        private const float BaseHeaderH = 86f;
        private const float BaseTreeW = 176f;
        private const float BaseGap = 12f;
        private const float BaseRoomRowH = 30f;
        private const float BaseDevRowH = 32f;
        private const float BaseRightHeadH = 30f;
        private const float BaseBottomPad = 24f;
        private const float BaseCloseSize = 22f;
        private const float BaseGripSize = 22f;
        private const float BaseFloorBtnW = 52f;
        private const float BaseFloorBtnH = 20f;
        private const float BaseBatchBtnW = 54f;
        private const float BaseBatchBtnH = 22f;
        private const float BasePowerBtnW = 78f;
        private const float BaseBtnW = 38f;
        private const float BaseSmallBtnW = 20f;
        private const float BaseTempW = 44f;
        private const float BaseBtnH = 22f;
        private const float BaseBtnGap = 5f;
        private const float BaseEmptyH = 24f;

        private const float MinWidth = 560f;
        private const float MinHeight = 260f;

        private const int FsTitle = 17;
        private const int FsSub = 12;
        private const int FsRow = 13;
        private const int FsSmall = 11;

        // ---------------- 外部参数 ----------------
        private float _scale = 1f;
        private float _margin;
        private float _maxWidth = 9999f;
        private float _maxHeight = 9999f;

        // ---------------- 状态 ----------------
        private bool _visible;
        private float _nextRefresh;
        private int _lastRegistryCount = -1;
        private bool _entriesDirty = true;

        /// <summary>楼层筛选：0 = 全部，其它 = 具体楼层。</summary>
        private int _floorFilter;

        /// <summary>当前选中的房间；null = 全部房间。</summary>
        private string _selectedZone;

        private bool _userMoved;
        private Vector2 _userPos;
        private Vector2 _userSize;

        /// <summary>「就绪」日志只打一次，避免 0.2s 刷屏。</summary>
        private bool _loggedOnce;

        // ---------------- 批量状态 ----------------
        private bool _confirming;
        private bool _confirmValue;
        private readonly List<string> _batchIds = new List<string>(8);
        private readonly List<string> _batchRejects = new List<string>(4);
        private int _batchOk;
        private int _batchFail;
        private float _batchDoneAt;
        private string _batchResult = "";
        private float _batchResultUntil;

        // ---------------- 节点 ----------------
        private RectTransform _panel;
        private RectTransform _dragBar;
        private Text _title;
        private Text _sub;
        private Text _hint;
        private RectTransform _closeBg;
        private Text _closeText;
        private Image _divider;

        private readonly List<Image> _floorBtnBgs = new List<Image>(4);
        private readonly List<Text> _floorBtnTexts = new List<Text>(4);
        private readonly List<int> _floorBtnValues = new List<int>(4);
        private Text _summary;

        private RectTransform _treeViewport;
        private RectTransform _treeContent;
        private ScrollRect _treeScroll;

        private Text _rightTitle;
        private Image _batchOnBg, _batchOffBg, _okBg, _cancelBg;
        private Text _batchOnText, _batchOffText, _okText, _cancelText;
        private Button _batchOnBtn, _batchOffBtn, _okBtn, _cancelBtn;

        /// <summary>回路开关按钮（P15）：[断 1F 电] / [恢复供电]。走 SBO 两步式。</summary>
        private Image _powerBg;
        private Text _powerText;
        private Button _powerBtn;

        /// <summary>SBO 弹窗。和面板同一个 GameObject，由面板负责建/拆。</summary>
        private TwinSboDialog _sbo;

        private RectTransform _devViewport;
        private RectTransform _devContent;
        private ScrollRect _devScroll;
        private Text _empty;

        private RectTransform _grip;

        // ---------------- 行 ----------------
        private readonly List<RoomRow> _roomRows = new List<RoomRow>(12);
        private readonly List<DevRow> _devRows = new List<DevRow>(40);

        private readonly List<TwinRoomInfo> _roomList = new List<TwinRoomInfo>(12);
        private readonly List<DevEntry> _devs = new List<DevEntry>(40);
        private readonly List<string> _extraZones = new List<string>(4);

        private readonly StringBuilder _sb = new StringBuilder(96);
        private TwinCommandCenter _center;

        // ================================================================ 数据

        private sealed class RoomRow
        {
            public RectTransform Root;
            public Image Bg;
            public Text Name;
            public Text Count;
            public Button Btn;
            /// <summary>null = 「全部房间」那一行。</summary>
            public string Zone;
        }

        private sealed class DevEntry
        {
            public TwinAnchor Anchor;
            public TwinDeviceModel Model;
            public bool CanPower;
            public bool CanTemp;
            public bool InScope;

            /// <summary>
            /// 可用性（P15）：通电 / 断电 / 无响应。
            ///
            /// 【为什么存一份而不是每次现查】Refresh 里一行要读三四次（状态点、文字、按钮），
            /// 每次都走一遍「房间 → 楼层 → 楼栋」的回路链，等于把同一件事算四遍。
            /// 每轮 Refresh 算一次放进这里，行内所有判断都读它。
            /// </summary>
            public DeviceAvailability Avail;

            /// <summary>
            /// 最近一次被本机拒绝的原因（串行锁 / 不在白名单 / 回放中）。
            ///
            /// 【为什么要存在行上】被拒的命令根本没进命令中心，`StateOf` 仍是 Idle，
            /// 界面上表现为「点了没反应」。P12 实测就是这么丢掉一轮的。
            /// </summary>
            public string RejectText;
            public float RejectUntil;
        }

        private sealed class DevRow
        {
            public RectTransform Root;
            public Image Bg;
            public Image Dot;
            public Text Name;
            public Text Status;
            public Text TempText;

            public Image BtnOn, BtnOff, BtnMinus, BtnPlus;
            public Text TxtOn, TxtOff, TxtMinus, TxtPlus;
            public Button BtnOnC, BtnOffC, BtnMinusC, BtnPlusC;
        }

        public bool IsVisible { get { return _visible; } }

        // ================================================================ 构建

        /// <summary>由 <see cref="TwinDetailPanel"/> 在构建界面时调用（可重复调用）。</summary>
        public void Build(RectTransform root, float scale, float margin, float maxWidth, float maxHeight)
        {
            if (root == null) return;

            bool same = _panel != null
                        && Mathf.Approximately(_scale, scale)
                        && Mathf.Approximately(_margin, margin)
                        && Mathf.Approximately(_maxWidth, maxWidth)
                        && Mathf.Approximately(_maxHeight, maxHeight);
            if (same) return;

            Teardown();

            _scale = scale;
            _margin = margin;
            _maxWidth = maxWidth;
            _maxHeight = maxHeight;
            _entriesDirty = true;

            BuildFresh(root);
        }

        private void Teardown()
        {
            if (_panel != null)
            {
                _panel.gameObject.SetActive(false);
                TwinUIFactory.DestroySafe(_panel.gameObject);
            }
            Detach();
        }

        /// <summary>
        /// 父面板重建 UI 时调用：节点会被父级一起销毁，这里只清缓存引用。
        ///
        /// 【为什么必须显式清】`Destroy` 延迟到帧末才真正销毁。只依赖「== null 就重建」
        /// 的话，同一帧内重建时 `_panel != null` 仍成立 → Build 直接返回 →
        /// 帧末旧节点没了、新节点没建 → 面板永久消失且不报错。
        /// </summary>
        public void Detach()
        {
            _panel = null;
            _dragBar = null;
            _title = null;
            _sub = null;
            _hint = null;
            _closeBg = null;
            _closeText = null;
            _divider = null;
            _summary = null;
            _treeViewport = null;
            _treeContent = null;
            _treeScroll = null;
            _rightTitle = null;
            _batchOnBg = null; _batchOffBg = null; _okBg = null; _cancelBg = null;
            _batchOnText = null; _batchOffText = null; _okText = null; _cancelText = null;
            _batchOnBtn = null; _batchOffBtn = null; _okBtn = null; _cancelBtn = null;
            _powerBg = null; _powerText = null; _powerBtn = null;
            _devViewport = null;
            _devContent = null;
            _devScroll = null;
            _empty = null;
            _grip = null;

            // 弹窗的节点是面板的子节点，会跟着一起被销毁；这里只清引用并复位状态机，
            // 否则重建面板后会冒出一个"上次没做完的断电操作"。
            if (_sbo != null) _sbo.Detach();

            _floorBtnBgs.Clear();
            _floorBtnTexts.Clear();
            _floorBtnValues.Clear();
            _roomRows.Clear();
            _devRows.Clear();
            _roomList.Clear();
            _devs.Clear();
            _extraZones.Clear();
            _entriesDirty = true;
            _lastRegistryCount = -1;
            _loggedOnce = false;
        }

        private void BuildFresh(RectTransform root)
        {
            float s = _scale;

            float w = Mathf.Min(BaseWidth * s, Mathf.Max(MinWidth, _maxWidth));
            float h = Mathf.Min(BaseHeight * s, Mathf.Max(MinHeight, _maxHeight));

            _panel = (RectTransform)TwinUIFactory.Node("ControlPanel", root).transform;
            // 先关掉再搭：ScrollRect 在 OnEnable 里就检查 content，而我们是先 AddComponent 后赋 content
            _panel.gameObject.SetActive(false);

            // 居中放：总控面板是「面」的视图，压在画面中央比贴边更像总控台。
            //
            // 【为什么不用 parent.rect 算居中】面板是在 TwinDetailPanel.Start 里建的，
            // 那时 CanvasScaler 还没跑过一次布局，根 RectTransform 的 rect 可能还是 0 ——
            // 用它会算出 (0,0)，面板就贴在左上角。改用调用方传进来的 maxWidth/maxHeight
            // （它们来自 ViewportWidth()，是已经算好的可用区域）。
            float vw = _maxWidth + _margin * 2f;
            float vh = _maxHeight + _margin * 2f;
            float px = Mathf.Max(_margin, (vw - w) * 0.5f);
            float py = Mathf.Max(_margin, (vh - h) * 0.5f);
            TwinUIFactory.Place(_panel, px, py, w, h);

            TwinRoundedRect bg = TwinUIFactory.Rounded(_panel, "Bg", 14f * s,
                TwinStatusPalette.PanelTop, TwinStatusPalette.PanelBottom,
                TwinStatusPalette.PanelBorder, 1f, true);
            TwinUIFactory.Stretch(bg.rectTransform);

            BuildChrome();
            BuildFloorBar();
            BuildTree();
            BuildRightHeader();
            BuildDevArea();
            BuildGrip();
            BuildSbo();

            if (_userMoved)
            {
                TwinWindowHandle.FreeAnchor(_panel);
                _panel.sizeDelta = _userSize;
                _panel.anchoredPosition = _userPos;
                TwinWindowHandle.ClampToParent(_panel);
            }

            _panel.gameObject.SetActive(_visible);
            Layout();
            Refresh(true);
        }

        private void BuildChrome()
        {
            float s = _scale;

            Image dragImg = TwinUIFactory.Block(_panel, "DragBar", TwinStatusPalette.Transparent, true);
            _dragBar = dragImg.rectTransform;
            TwinWindowHandle drag = _dragBar.gameObject.AddComponent<TwinWindowHandle>();
            drag.Window = _panel;
            drag.HandleMode = TwinWindowHandle.Mode.Move;
            drag.OnChanged = OnGeometryChanged;
            drag.HoverTarget = dragImg;

            _title = TwinUIFactory.Label(_panel, "Title", Mathf.Max(8, Mathf.RoundToInt(FsTitle * s)),
                TextAnchor.UpperLeft, TwinStatusPalette.TextPrimary, FontStyle.Bold);
            _title.raycastTarget = false;
            _title.text = "校园总控";

            _sub = TwinUIFactory.Label(_panel, "Sub", Mathf.Max(8, Mathf.RoundToInt(FsSub * s)),
                TextAnchor.UpperLeft, TwinStatusPalette.TextSecondary);
            _sub.raycastTarget = false;

            _hint = TwinUIFactory.Label(_panel, "Hint", Mathf.Max(8, Mathf.RoundToInt(FsSub * s)),
                TextAnchor.UpperRight, TwinStatusPalette.TextMuted);
            _hint.raycastTarget = false;
            _hint.text = "已并入右侧总控面板";

            Image closeImg = TwinUIFactory.Block(_panel, "Close", TwinStatusPalette.Track, true);
            _closeBg = closeImg.rectTransform;
            Button closeBtn = closeImg.gameObject.AddComponent<Button>();
            closeBtn.targetGraphic = closeImg;
            closeBtn.transition = Selectable.Transition.None;
            closeBtn.onClick.AddListener(OnCloseClicked);

            _closeText = TwinUIFactory.Label(_closeBg, "X", Mathf.Max(9, Mathf.RoundToInt(16f * s)),
                TextAnchor.MiddleCenter, TwinStatusPalette.TextSecondary);
            TwinUIFactory.Stretch(_closeText.rectTransform);
            _closeText.raycastTarget = false;
            _closeText.text = "×";

            Image div = TwinUIFactory.Block(_panel, "Divider", TwinStatusPalette.Divider, false);
            _divider = div;
        }

        /// <summary>
        /// 楼层筛选条：[全部][1F][2F]。
        ///
        /// 【为什么按钮是「建出来」的】楼层来自 <see cref="TwinSiteLayout.Floors"/>，
        /// 将来加一层楼不用改这里。写死两个按钮的话，新增楼层会静默不出现。
        /// </summary>
        private void BuildFloorBar()
        {
            float s = _scale;

            MakeFloorButton(0, "全部");

            int[] floors = TwinSiteLayout.Floors();
            for (int i = 0; i < floors.Length; i++) MakeFloorButton(floors[i], floors[i] + "F");

            _summary = TwinUIFactory.Label(_panel, "Summary", Mathf.Max(8, Mathf.RoundToInt(FsSub * s)),
                TextAnchor.MiddleRight, TwinStatusPalette.TextSecondary);
            _summary.raycastTarget = false;
        }

        private void MakeFloorButton(int value, string caption)
        {
            float s = _scale;
            Image bg = TwinUIFactory.Block(_panel, "Floor" + value, TwinStatusPalette.Track, true);
            Button btn = bg.gameObject.AddComponent<Button>();
            btn.targetGraphic = bg;
            btn.transition = Selectable.Transition.ColorTint;
            ApplyTint(btn);

            Text txt = TwinUIFactory.Label(bg.transform, "Text", Mathf.Max(8, Mathf.RoundToInt(FsSmall * s)),
                TextAnchor.MiddleCenter, TwinStatusPalette.TextSecondary);
            TwinUIFactory.Stretch(txt.rectTransform);
            txt.raycastTarget = false;
            txt.text = caption;

            int captured = value;
            btn.onClick.AddListener(delegate { OnFloorClicked(captured); });

            _floorBtnBgs.Add(bg);
            _floorBtnTexts.Add(txt);
            _floorBtnValues.Add(value);
        }

        private void BuildTree()
        {
            float s = _scale;

            // 视口必须有能挡射线的 Graphic，否则在列表空白处滚轮没接收者。
            // 走 Block 而不是手写 AddComponent<Image>：后者不补 CanvasRenderer，图形会静默不渲染。
            Image vpImg = TwinUIFactory.Block(_panel, "TreeViewport", TwinStatusPalette.Transparent, true);
            _treeViewport = vpImg.rectTransform;
            vpImg.gameObject.AddComponent<RectMask2D>();

            _treeContent = (RectTransform)TwinUIFactory.Node("TreeContent", _treeViewport).transform;
            _treeContent.anchorMin = new Vector2(0f, 1f);
            _treeContent.anchorMax = new Vector2(1f, 1f);
            _treeContent.pivot = new Vector2(0.5f, 1f);
            _treeContent.anchoredPosition = Vector2.zero;
            _treeContent.sizeDelta = Vector2.zero;

            _treeScroll = vpImg.gameObject.AddComponent<ScrollRect>();
            _treeScroll.viewport = _treeViewport;
            _treeScroll.content = _treeContent;
            _treeScroll.horizontal = false;
            _treeScroll.vertical = true;
            _treeScroll.movementType = ScrollRect.MovementType.Clamped;
            _treeScroll.inertia = true;
            _treeScroll.decelerationRate = 0.12f;
            _treeScroll.scrollSensitivity = 30f;
        }

        private void BuildRightHeader()
        {
            float s = _scale;

            _rightTitle = TwinUIFactory.Label(_panel, "RightTitle", Mathf.Max(8, Mathf.RoundToInt(FsRow * s)),
                TextAnchor.MiddleLeft, TwinStatusPalette.TextPrimary, FontStyle.Bold);
            _rightTitle.raycastTarget = false;

            _batchOnBg = MakeBatchButton("BatchOn", "全开", out _batchOnText, out _batchOnBtn);
            _batchOnBtn.onClick.AddListener(delegate { BeginConfirm(true); });

            _batchOffBg = MakeBatchButton("BatchOff", "全关", out _batchOffText, out _batchOffBtn);
            _batchOffBtn.onClick.AddListener(delegate { BeginConfirm(false); });

            _okBg = MakeBatchButton("ConfirmOk", "确认", out _okText, out _okBtn);
            _okBtn.onClick.AddListener(OnConfirmClicked);

            _cancelBg = MakeBatchButton("ConfirmCancel", "取消", out _cancelText, out _cancelBtn);
            _cancelBtn.onClick.AddListener(OnCancelClicked);

            // ---- 回路开关（P15）----
            //
            // 【为什么放在表头而不是左树每一行】左树的每一行都是"点一下切筛选"，
            // 在同一个矩形里再塞一个按钮，误触率会高到不可接受 —— 而误触的后果是断电。
            // 表头这个位置的含义很清楚：「对当前选中的范围做一件重事」，
            // 和旁边的[全开][全关]是同一类操作，只是影响面更大。
            _powerBg = TwinUIFactory.Block(_panel, "PowerSwitch", TwinStatusPalette.Track, true);
            TwinUIFactory.Place(_powerBg.rectTransform, 0f, 0f, BasePowerBtnW * _scale, BaseBatchBtnH * _scale);

            _powerBtn = _powerBg.gameObject.AddComponent<Button>();
            _powerBtn.targetGraphic = _powerBg;
            _powerBtn.transition = Selectable.Transition.ColorTint;
            ApplyTint(_powerBtn);
            _powerBtn.onClick.AddListener(OnPowerClicked);

            _powerText = TwinUIFactory.Label(_powerBg.transform, "Text",
                Mathf.Max(8, Mathf.RoundToInt(FsSmall * _scale)),
                TextAnchor.MiddleCenter, TwinStatusPalette.TextPrimary);
            TwinUIFactory.Stretch(_powerText.rectTransform);
            _powerText.raycastTarget = false;
            _powerText.text = "断电";
        }

        private Image MakeBatchButton(string name, string caption, out Text text, out Button button)
        {
            float s = _scale;
            Image bg = TwinUIFactory.Block(_panel, name, TwinStatusPalette.Track, true);
            TwinUIFactory.Place(bg.rectTransform, 0f, 0f, BaseBatchBtnW * s, BaseBatchBtnH * s);

            button = bg.gameObject.AddComponent<Button>();
            button.targetGraphic = bg;
            button.transition = Selectable.Transition.ColorTint;
            ApplyTint(button);

            text = TwinUIFactory.Label(bg.transform, "Text", Mathf.Max(8, Mathf.RoundToInt(FsSmall * s)),
                TextAnchor.MiddleCenter, TwinStatusPalette.TextPrimary);
            TwinUIFactory.Stretch(text.rectTransform);
            text.raycastTarget = false;
            text.text = caption;
            return bg;
        }

        private void BuildDevArea()
        {
            Image vpImg = TwinUIFactory.Block(_panel, "DevViewport", TwinStatusPalette.Transparent, true);
            _devViewport = vpImg.rectTransform;
            vpImg.gameObject.AddComponent<RectMask2D>();

            _devContent = (RectTransform)TwinUIFactory.Node("DevContent", _devViewport).transform;
            _devContent.anchorMin = new Vector2(0f, 1f);
            _devContent.anchorMax = new Vector2(1f, 1f);
            _devContent.pivot = new Vector2(0.5f, 1f);
            _devContent.anchoredPosition = Vector2.zero;
            _devContent.sizeDelta = Vector2.zero;

            _devScroll = vpImg.gameObject.AddComponent<ScrollRect>();
            _devScroll.viewport = _devViewport;
            _devScroll.content = _devContent;
            _devScroll.horizontal = false;
            _devScroll.vertical = true;
            _devScroll.movementType = ScrollRect.MovementType.Clamped;
            _devScroll.inertia = true;
            _devScroll.decelerationRate = 0.12f;
            _devScroll.scrollSensitivity = 30f;

            _empty = TwinUIFactory.Label(_panel, "Empty", Mathf.Max(8, Mathf.RoundToInt(FsRow * _scale)),
                TextAnchor.MiddleLeft, TwinStatusPalette.TextMuted);
            _empty.raycastTarget = false;
            _empty.text = "这个房间里没有点位";
        }

        private void BuildGrip()
        {
            float s = _scale;
            float g = BaseGripSize * s;

            Image hit = TwinUIFactory.Block(_panel, "Grip", TwinStatusPalette.Transparent, true);
            _grip = hit.rectTransform;

            Color line = new Color(TwinStatusPalette.TextMuted.r, TwinStatusPalette.TextMuted.g,
                                   TwinStatusPalette.TextMuted.b, 0.55f);

            Image hLine = TwinUIFactory.Block(_grip, "GripH", line, false);
            TwinUIFactory.Place(hLine.rectTransform, 0f, g - 1f, g * 0.55f, 1f);

            Image vLine = TwinUIFactory.Block(_grip, "GripV", line, false);
            TwinUIFactory.Place(vLine.rectTransform, g - 1f, g * 0.45f, 1f, g * 0.55f);

            TwinWindowHandle resize = hit.gameObject.AddComponent<TwinWindowHandle>();
            resize.Window = _panel;
            resize.HandleMode = TwinWindowHandle.Mode.ResizeBottomRight;
            resize.MinSize = new Vector2(MinWidth * s, MinHeight * s);
            resize.OnChanged = OnGeometryChanged;

            _grip.SetAsLastSibling();
        }

        /// <summary>
        /// SBO 弹窗（P15）。挂在面板自己身上，由面板建、由面板拆 ——
        /// 和告警历史/点位清单/总控面板同一个思路：不依赖 Inspector 配置。
        /// </summary>
        private void BuildSbo()
        {
            if (_sbo == null) _sbo = GetComponent<TwinSboDialog>();
            if (_sbo == null) _sbo = gameObject.AddComponent<TwinSboDialog>();
            _sbo.Build(_panel, _scale);
        }

        /// <summary>
        /// 按钮配色。Track 是 9% 白的半透明块，ColorTint 是**乘算**，
        /// 所以高亮色给 &gt;1 的白（会被钳到 1，但 alpha 也跟着放大，视觉上就是「亮了一点」）。
        /// </summary>
        private static void ApplyTint(Button button)
        {
            ColorBlock colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.8f, 1.8f, 1.8f, 1f);
            colors.pressedColor = new Color(0.7f, 0.7f, 0.7f, 1f);
            colors.disabledColor = new Color(0.55f, 0.55f, 0.55f, 0.45f);
            colors.colorMultiplier = 1f;
            colors.fadeDuration = 0.06f;
            button.colors = colors;
        }

        // ================================================================ 行

        private RoomRow EnsureRoomRow(int index)
        {
            while (_roomRows.Count <= index)
            {
                int i = _roomRows.Count;
                float s = _scale;
                int fs = Mathf.Max(8, Mathf.RoundToInt(FsRow * s));

                GameObject go = TwinUIFactory.Node("RoomRow" + i, _treeContent);
                RoomRow r = new RoomRow();
                r.Root = (RectTransform)go.transform;

                Image bg = TwinUIFactory.Block(go.transform, "Bg", TwinStatusPalette.Transparent, true);
                TwinUIFactory.Stretch(bg.rectTransform);
                r.Bg = bg;

                r.Name = TwinUIFactory.Label(go.transform, "Name", fs, TextAnchor.MiddleLeft,
                    TwinStatusPalette.TextPrimary);
                r.Name.raycastTarget = false;

                r.Count = TwinUIFactory.Label(go.transform, "Count",
                    Mathf.Max(8, Mathf.RoundToInt(FsSmall * s)), TextAnchor.MiddleRight,
                    TwinStatusPalette.TextMuted);
                r.Count.raycastTarget = false;

                r.Btn = bg.gameObject.AddComponent<Button>();
                r.Btn.targetGraphic = bg;
                r.Btn.transition = Selectable.Transition.None;

                int captured = i;
                r.Btn.onClick.AddListener(delegate { OnRoomRowClicked(captured); });

                _roomRows.Add(r);
            }
            return _roomRows[index];
        }

        private DevRow EnsureDevRow(int index)
        {
            while (_devRows.Count <= index)
            {
                int i = _devRows.Count;
                float s = _scale;
                int fs = Mathf.Max(8, Mathf.RoundToInt(FsRow * s));

                GameObject go = TwinUIFactory.Node("DevRow" + i, _devContent);
                DevRow r = new DevRow();
                r.Root = (RectTransform)go.transform;

                Image bg = TwinUIFactory.Block(go.transform, "Bg", TwinStatusPalette.Transparent, true);
                TwinUIFactory.Stretch(bg.rectTransform);
                r.Bg = bg;

                r.Dot = TwinUIFactory.Block(go.transform, "Dot", TwinStatusPalette.Unknown, false);

                r.Name = TwinUIFactory.Label(go.transform, "Name", fs, TextAnchor.MiddleLeft,
                    TwinStatusPalette.TextPrimary);
                r.Name.raycastTarget = false;

                r.Status = TwinUIFactory.Label(go.transform, "Status",
                    Mathf.Max(8, Mathf.RoundToInt(FsSmall * s)), TextAnchor.MiddleRight,
                    TwinStatusPalette.TextSecondary);
                r.Status.raycastTarget = false;

                r.TempText = TwinUIFactory.Label(go.transform, "Temp", fs, TextAnchor.MiddleCenter,
                    TwinStatusPalette.TextPrimary);
                r.TempText.raycastTarget = false;

                r.BtnOn = MakeSmallBtn(go.transform, "BtnOn", "开", out r.TxtOn, out r.BtnOnC);
                r.BtnOff = MakeSmallBtn(go.transform, "BtnOff", "关", out r.TxtOff, out r.BtnOffC);
                r.BtnMinus = MakeSmallBtn(go.transform, "BtnMinus", "-", out r.TxtMinus, out r.BtnMinusC);
                r.BtnPlus = MakeSmallBtn(go.transform, "BtnPlus", "+", out r.TxtPlus, out r.BtnPlusC);

                int captured = i;
                r.BtnOnC.onClick.AddListener(delegate { SendPower(captured, true); });
                r.BtnOffC.onClick.AddListener(delegate { SendPower(captured, false); });
                r.BtnMinusC.onClick.AddListener(delegate { StepTemperature(captured, -1f); });
                r.BtnPlusC.onClick.AddListener(delegate { StepTemperature(captured, 1f); });

                _devRows.Add(r);
            }
            return _devRows[index];
        }

        private Image MakeSmallBtn(Transform parent, string name, string caption,
            out Text text, out Button button)
        {
            float s = _scale;
            bool wide = caption == "开" || caption == "关";
            float w = (wide ? BaseBtnW : BaseSmallBtnW) * s;

            Image bg = TwinUIFactory.Block(parent, name, TwinStatusPalette.Track, true);
            TwinUIFactory.Place(bg.rectTransform, 0f, 0f, w, BaseBtnH * s);

            button = bg.gameObject.AddComponent<Button>();
            button.targetGraphic = bg;
            button.transition = Selectable.Transition.ColorTint;
            ApplyTint(button);

            text = TwinUIFactory.Label(bg.transform, "Text", Mathf.Max(8, Mathf.RoundToInt(FsSmall * s)),
                TextAnchor.MiddleCenter, TwinStatusPalette.TextPrimary);
            TwinUIFactory.Stretch(text.rectTransform);
            text.raycastTarget = false;
            text.text = caption;
            return bg;
        }

        // ================================================================ 排版

        private void Layout()
        {
            if (_panel == null) return;

            float s = _scale;
            float pad = BasePad * s;
            float headerH = BaseHeaderH * s;
            float w = _panel.sizeDelta.x;
            float h = _panel.sizeDelta.y;
            float closeSize = BaseCloseSize * s;
            float gripSize = BaseGripSize * s;
            float innerW = Mathf.Max(0f, w - pad * 2f);

            // ---- 标题栏 ----
            float dragX = 6f * s;
            float dragRight = w - pad - closeSize - 4f * s;
            TwinUIFactory.Place(_dragBar, dragX, 4f * s,
                Mathf.Max(0f, dragRight - dragX), Mathf.Max(1f, 40f * s));

            TwinUIFactory.Place(_closeBg, w - pad - closeSize, 8f * s, closeSize, closeSize);

            float titleW = Mathf.Max(40f, w - pad * 2f - closeSize - 8f * s);
            TwinUIFactory.Place(_title.rectTransform, pad, 10f * s, titleW, FsTitle * s + 6f);
            TwinUIFactory.Place(_sub.rectTransform, pad, 10f * s + FsTitle * s + 5f, titleW, FsSub * s + 6f);

            float hintRight = w - pad - closeSize - 6f * s;
            TwinUIFactory.Place(_hint.rectTransform, pad, 12f * s,
                Mathf.Max(0f, hintRight - pad), FsSub * s + 6f);

            // ---- 楼层筛选条 ----
            float fbW = BaseFloorBtnW * s;
            float fbH = BaseFloorBtnH * s;
            float fbY = 52f * s;
            float fbX = pad;
            for (int i = 0; i < _floorBtnBgs.Count; i++)
            {
                TwinUIFactory.Place(_floorBtnBgs[i].rectTransform, fbX, fbY, fbW, fbH);
                fbX += fbW + 6f * s;
            }
            TwinUIFactory.Place(_summary.rectTransform, fbX, fbY,
                Mathf.Max(0f, w - pad - fbX), fbH);

            TwinUIFactory.Place(_divider.rectTransform, pad, headerH - 1f, innerW, 1f);

            // ---- 左侧树 ----
            float bodyY = headerH;
            float bodyH = Mathf.Max(1f, h - headerH - BaseBottomPad * s);
            float treeW = Mathf.Min(BaseTreeW * s, innerW * 0.4f);
            TwinUIFactory.Place(_treeViewport, pad, bodyY, treeW, bodyH);

            // ---- 右侧 ----
            float gap = BaseGap * s;
            float rightX = pad + treeW + gap;
            float rightW = Mathf.Max(60f, w - pad - rightX);
            float headH = BaseRightHeadH * s;

            float btnW = BaseBatchBtnW * s;
            float btnH = BaseBatchBtnH * s;
            float btnY = bodyY + (headH - btnH) * 0.5f;
            float bx = rightX + rightW - btnW;
            TwinUIFactory.Place(_batchOffBg.rectTransform, bx, btnY, btnW, btnH);
            TwinUIFactory.Place(_cancelBg.rectTransform, bx, btnY, btnW, btnH);
            bx -= btnW + 6f * s;
            TwinUIFactory.Place(_batchOnBg.rectTransform, bx, btnY, btnW, btnH);
            TwinUIFactory.Place(_okBg.rectTransform, bx, btnY, btnW, btnH);

            // 回路开关在最左边：它影响的是整个范围（可能是整层楼），
            // 和[全开][全关]（只影响当前表里的可控设备）不是同一量级，视觉上要分开。
            float powerW = BasePowerBtnW * s;
            bx -= powerW + 10f * s;
            TwinUIFactory.Place(_powerBg.rectTransform, bx, btnY, powerW, btnH);

            float headTextW = Mathf.Max(40f, (bx - 6f * s) - rightX);
            TwinUIFactory.Place(_rightTitle.rectTransform, rightX, bodyY, headTextW, headH);

            float vpY = bodyY + headH + 4f * s;
            float vpH = Mathf.Max(1f, h - vpY - BaseBottomPad * s);
            TwinUIFactory.Place(_devViewport, rightX, vpY, rightW, vpH);
            TwinUIFactory.Place(_empty.rectTransform, rightX, vpY + 4f * s, rightW, BaseEmptyH * s);

            // ---- 缩放手柄 ----
            TwinUIFactory.Place(_grip, w - gripSize - 2f * s, h - gripSize - 2f * s, gripSize, gripSize);

            // ---- SBO 弹窗 ----
            // 跟着面板一起变：面板拉大，弹窗的名单就能多显示几行。
            if (_sbo != null) _sbo.Layout(w, h, headerH, pad);

            LayoutRooms(treeW);
            LayoutRows(rightW, vpH);
        }

        private void LayoutRooms(float contentW)
        {
            float s = _scale;
            float rowH = BaseRoomRowH * s;
            float nameW = Mathf.Max(40f, contentW - 56f * s);

            for (int i = 0; i < _roomRows.Count; i++)
            {
                RoomRow r = _roomRows[i];
                if (r == null) continue;
                TwinUIFactory.Place(r.Root, 0f, i * rowH, contentW, rowH);
                TwinUIFactory.Place(r.Name.rectTransform, 10f * s, 0f, nameW, rowH);
                TwinUIFactory.Place(r.Count.rectTransform, contentW - 52f * s, 0f, 46f * s, rowH);
            }

            float total = _roomRows.Count * rowH;
            _treeContent.sizeDelta = new Vector2(0f, total);
        }

        private void LayoutRows(float contentW, float viewportH)
        {
            float s = _scale;
            float rowH = BaseDevRowH * s;
            float btnW = BaseBtnW * s;
            float smallW = BaseSmallBtnW * s;
            float tempW = BaseTempW * s;
            float btnH = BaseBtnH * s;
            float gapB = BaseBtnGap * s;
            float dotSize = 8f * s;
            int fsName = Mathf.Max(8, Mathf.RoundToInt(FsRow * s));

            for (int i = 0; i < _devRows.Count; i++)
            {
                DevRow r = _devRows[i];
                if (r == null) continue;

                TwinUIFactory.Place(r.Root, 0f, i * rowH, contentW, rowH);
                TwinUIFactory.Place(r.Dot.rectTransform, 6f * s, (rowH - dotSize) * 0.5f, dotSize, dotSize);

                // 按钮从右往左摆，这样「有没有按钮」不会影响名字的起点。
                //
                // 【判据必须来自数据，不能来自 activeSelf】首次构建时所有按钮都是激活的，
                // 若按 activeSelf 排版，每一行都会先按「有温度按钮」留出宽度；
                // 等 ApplyDevRows 把按钮关掉，右边就空出一块。Layout 只看数据，才不会互相打架。
                float x = contentW;
                bool hasTemp = i < _devs.Count && _devs[i] != null && _devs[i].CanTemp;
                bool hasPower = i < _devs.Count && _devs[i] != null && _devs[i].CanPower;

                float by = (rowH - btnH) * 0.5f;

                if (hasTemp)
                {
                    x -= smallW;
                    TwinUIFactory.Place(r.BtnPlus.rectTransform, x, by, smallW, btnH);
                    x -= gapB + tempW;
                    TwinUIFactory.Place(r.TempText.rectTransform, x, 0f, tempW, rowH);
                    x -= gapB + smallW;
                    TwinUIFactory.Place(r.BtnMinus.rectTransform, x, by, smallW, btnH);
                    x -= gapB;
                }

                if (hasPower)
                {
                    x -= btnW;
                    TwinUIFactory.Place(r.BtnOff.rectTransform, x, by, btnW, btnH);
                    x -= gapB + btnW;
                    TwinUIFactory.Place(r.BtnOn.rectTransform, x, by, btnW, btnH);
                    x -= gapB;
                }

                float statusW = Mathf.Min(160f * s, Mathf.Max(70f * s, x * 0.42f));
                float statusX = Mathf.Max(0f, x - statusW);
                TwinUIFactory.Place(r.Status.rectTransform, statusX, 0f, statusW, rowH);

                float nameX = 22f * s;
                float nameW = Mathf.Max(40f, statusX - nameX - 4f * s);
                TwinUIFactory.Place(r.Name.rectTransform, nameX, 0f, nameW, rowH);
                r.Name.fontSize = fsName;
            }

            float total = _devRows.Count * rowH;
            _devContent.sizeDelta = new Vector2(0f, Mathf.Max(total, viewportH));
        }

        private void OnGeometryChanged()
        {
            // 用户拖过/拉过 → 记住，重建 UI 时还原
            if (_panel == null) return;
            _userMoved = true;
            _userPos = _panel.anchoredPosition;
            _userSize = _panel.sizeDelta;
            Layout();
        }

        // ================================================================ 交互

        private void OnCloseClicked()
        {
            SetVisible(false);
        }

        public void Toggle()
        {
            SetVisible(!_visible);
        }

        public void SetVisible(bool visible)
        {
            _visible = visible;
            if (_panel != null) _panel.gameObject.SetActive(visible);
            if (visible)
            {
                _entriesDirty = true;
                Refresh(true);
            }
            else
            {
                // 关掉时取消未确认的批量，避免「关掉面板再打开，突然冒出一个确认框」
                _confirming = false;

                // SBO 同理，而且更重要：**关窗等于放弃**。
                // 不取消的话，10 秒倒计时会在面板关着的时候继续走，
                // 用户再按 G 打开会看到一个"已就绪"的弹窗，而他已经忘了这件事。
                if (_sbo != null) _sbo.Hide();
            }
        }

        private void OnFloorClicked(int floor)
        {
            if (_floorFilter == floor) return;

            // SBO 进行中不许换楼层：弹窗里列的是"当前范围内的 N 台设备"，
            // 底下悄悄换了范围，那张名单就成了假的 —— 而它正是让人做决定的依据。
            if (_sbo != null && _sbo.IsActive) return;

            _floorFilter = floor;
            // 切楼层时如果当前选中的房间不在这一层，退回「全部」
            if (_selectedZone != null)
            {
                int f = TwinSiteLayout.FloorOf(_selectedZone);
                if (floor != 0 && f != floor) _selectedZone = null;
            }
            _confirming = false;
            _entriesDirty = true;
            Refresh(true);
        }

        private void OnRoomRowClicked(int index)
        {
            if (index == 0) _selectedZone = null;
            else if (index - 1 < _roomList.Count) _selectedZone = _roomList[index - 1].Zone;

            _confirming = false;
            _entriesDirty = true;
            Refresh(true);
        }

        private void SendPower(int rowIndex, bool on)
        {
            if (rowIndex < 0 || rowIndex >= _devs.Count) return;
            DevEntry e = _devs[rowIndex];
            if (e == null || e.Anchor == null) return;

            TwinCommandCenter center = EnsureCenter();
            if (center == null) return;

            string twinId = e.Anchor.TwinId;

            // 【P3】单台座位关机：先播关机视频，播完再断电（和「全部关机」一致）
            if (!on && twinId.StartsWith("ClassComputer/PC/Seat_", System.StringComparison.Ordinal))
            {
                SchoolTwin.Present.TwinDevicePresenter pres =
                    SchoolTwin.Present.TwinDevicePresenter.Instance;
                if (pres != null)
                {
                    DevEntry captured = e;
                    pres.BeginSeatShutdown(twinId, () => SendPowerNow(captured, twinId, false));
                    return;
                }
            }

            SendPowerNow(e, twinId, on);
        }

        private void SendPowerNow(DevEntry e, string twinId, bool on)
        {
            if (e == null) return;

            TwinCommandCenter center = EnsureCenter();
            if (center == null) return;

            string reject;
            if (!center.TrySend(twinId, SwitchActionOf(e), on ? 1f : 0f, out reject))
            {
                // 被拒的原因直接写在这一行上，不要只丢进 Console
                e.RejectText = string.IsNullOrEmpty(reject) ? "操作被拒绝" : reject;
                e.RejectUntil = Time.unscaledTime + 3f;
            }
            Refresh(true);
        }

        /// <summary>
        /// 这一行的「开 / 关」该发哪个动作 —— 从设备模型取，不写死。
        ///
        /// 门只有 `SetOpen`。写死 `SetPower` 的话，总控面板上门的按钮会被
        /// 命令中心以「不支持」拒掉，而界面上只是闪一下红字。
        /// </summary>
        private static CommandAction SwitchActionOf(DevEntry e)
        {
            TwinActionDefinition def = e != null && e.Model != null ? e.Model.PrimarySwitchAction : null;
            if (def != null)
            {
                CommandAction parsed;
                if (CommandText.TryParseAction(def.Name, out parsed)) return parsed;
            }
            return CommandAction.SetPower;
        }

        private void StepTemperature(int rowIndex, float delta)
        {
            if (rowIndex < 0 || rowIndex >= _devs.Count) return;
            DevEntry e = _devs[rowIndex];
            if (e == null || e.Anchor == null) return;

            // 基准取**回读值**，不取本地计数器 —— 否则连点三次会算成 26+3，
            // 而设备实际可能只执行到 27（同设备命令串行，后两条被拒）
            float current = 26f;
            TwinValue temp;
            if (e.Anchor.TryGetValue("SetTemp", out temp)) current = temp.Value;

            float next = Mathf.Clamp(Mathf.Round(current) + delta, 16f, 30f);
            if (Mathf.Abs(next - current) < 0.01f) return;

            TwinCommandCenter center = EnsureCenter();
            if (center == null) return;

            string reject;
            if (!center.TrySend(e.Anchor.TwinId, CommandAction.SetTemperature, next, out reject))
            {
                e.RejectText = string.IsNullOrEmpty(reject) ? "操作被拒绝" : reject;
                e.RejectUntil = Time.unscaledTime + 3f;
            }
            Refresh(true);
        }

        private void BeginConfirm(bool value)
        {
            if (CountBatchTargets() == 0) return;
            _confirming = true;
            _confirmValue = value;
            _batchResult = "";
            Refresh(true);
        }

        private void OnCancelClicked()
        {
            _confirming = false;
            Refresh(true);
        }

        private void OnConfirmClicked()
        {
            _confirming = false;

            TwinCommandCenter center = EnsureCenter();
            if (center == null) return;

            _batchIds.Clear();
            _batchRejects.Clear();
            _batchOk = 0;
            _batchFail = 0;
            _batchDoneAt = 0f;
            _batchResult = "";

            float value = _confirmValue ? 1f : 0f;
            for (int i = 0; i < _devs.Count; i++)
            {
                DevEntry e = _devs[i];
                if (e == null || e.Anchor == null || !e.CanPower) continue;

                string reject;
                if (center.TrySend(e.Anchor.TwinId, SwitchActionOf(e), value, out reject))
                {
                    _batchIds.Add(e.Anchor.TwinId);
                }
                else
                {
                    _batchRejects.Add(e.Anchor.DisplayName + "：" +
                                      (string.IsNullOrEmpty(reject) ? "被拒绝" : reject));
                }
            }

            _batchFail = _batchRejects.Count;
            _batchDoneAt = 0f;
            Debug.Log("[TwinPanel] 批量" + (_confirmValue ? "全开" : "全关") +
                      "：下发 " + _batchIds.Count + " 条，立即被拒 " + _batchRejects.Count + " 条");
            Refresh(true);
        }

        private int CountBatchTargets()
        {
            int n = 0;
            for (int i = 0; i < _devs.Count; i++)
            {
                if (_devs[i] == null || !_devs[i].CanPower) continue;

                // 断电的设备不算「可控目标」：点了也只会被命令中心拒掉。
                // 把它算进「开 3 台设备？」里，用户点完发现只动了 2 台，会以为有 bug。
                if (!TwinCircuit.IsPowered(_devs[i].Anchor.TwinId)) continue;
                n++;
            }
            return n;
        }

        /// <summary>当前表里因断电而不能操作的设备数（表头提示用）。</summary>
        private int CountPowerOffTargets()
        {
            int n = 0;
            for (int i = 0; i < _devs.Count; i++)
            {
                if (_devs[i] == null || !_devs[i].CanPower) continue;
                if (!TwinCircuit.IsPowered(_devs[i].Anchor.TwinId)) n++;
            }
            return n;
        }

        private TwinCommandCenter EnsureCenter()
        {
            if (_center == null) _center = TwinCommandCenter.Ensure();
            return _center;
        }

        // ================================================================ 回路（P15）

        /// <summary>
        /// 点表头的回路开关。
        ///
        /// 【为什么一定要走 SBO，不能像[全开]那样一句确认就完】影响面不同量级：
        /// 房间级全开是 5 台灯/空调，楼层级断电是整层 15 台设备 + 5 个房间全黑。
        /// 后者点错一次要现场去合闸，所以按 Spec §8.5 走「选择 → 就绪 → 执行」三段，
        /// 中间那 10 秒就是留给人反悔的。
        /// </summary>
        private void OnPowerClicked()
        {
            if (_sbo == null) return;

            string circuitId = TwinCircuit.CircuitOfSelection(_selectedZone, _floorFilter);
            TwinCircuitNode node = TwinCircuit.Find(circuitId);
            if (node == null) return;

            bool targetOn = !node.On;    // 现在是合闸 → 这次是断电

            List<string> names = new List<string>(16);
            TwinCircuit.CollectDeviceNames(circuitId, names);

            if (names.Count == 0)
            {
                // 不弹窗。弹出来再告诉人"这里没有设备"是浪费一次点击。
                _batchResult = node.Title + " 范围内没有设备";
                _batchResultUntil = Time.unscaledTime + 3f;
                // 用 1 而不是 0：这样表头是警示色（黄）。写"没有设备"却显示绿色，
                // 会让人以为是"一切正常"而不是"这里点不动"。
                _batchFail = 1;
                Refresh(true);
                return;
            }

            TwinSboRequest req = new TwinSboRequest();
            req.CircuitId = circuitId;
            req.PowerOn = targetOn;
            req.Subject = node.Title;
            req.Title = (targetOn ? "断开 " : "恢复 ") + node.Title + (targetOn ? " 的电源" : " 的供电");
            req.Targets = names;

            string capturedId = circuitId;
            bool capturedOn = targetOn;
            req.Operate = delegate
            {
                TwinCircuit.SetSwitch(capturedId, capturedOn);
                // 断/恢复都会改变整张表的可用性，必须重建条目（而不只是重刷文字）
                _entriesDirty = true;
            };

            // 开始一次重操作时，先把 P14 的批量确认收掉 —— 两个"待确认"同时在屏幕上
            // 是最容易点错的状态。
            _confirming = false;
            _sbo.Show(req);
            Refresh(true);
        }

        /// <summary>表头那个按钮的文案与颜色：合闸时是「断电」（警示），断开时是「恢复供电」（正常）。</summary>
        private void ApplyPowerButton()
        {
            if (_powerBg == null || _powerBtn == null) return;

            string circuitId = TwinCircuit.CircuitOfSelection(_selectedZone, _floorFilter);
            TwinCircuitNode node = TwinCircuit.Find(circuitId);

            bool on = node == null || node.On;
            bool parentDead = node != null && !TwinCircuit.ParentPowered(circuitId);

            string caption;
            if (node == null)
            {
                caption = "断电";
            }
            else if (parentDead)
            {
                // 上级断电时自己的闸没有意义：点了也不会亮，别让人白点两次。
                caption = "上级已断电";
            }
            else if (!on)
            {
                caption = "恢复供电";
            }
            else if (_selectedZone != null)
            {
                caption = "断房间电";
            }
            else if (_floorFilter > 0)
            {
                caption = "断" + _floorFilter + "F电";
            }
            else
            {
                caption = "断全楼电";
            }

            SetCached(_powerText, caption);

            // 断电动作是警示色（黄），恢复供电是正常色（绿）——
            // 方向不同，颜色就不该一样，否则很容易点反。
            Color bg;
            if (parentDead) bg = TwinStatusPalette.Track;
            else if (on)
                bg = new Color(TwinStatusPalette.Warning.r, TwinStatusPalette.Warning.g,
                               TwinStatusPalette.Warning.b, 0.20f);
            else
                bg = new Color(TwinStatusPalette.Normal.r, TwinStatusPalette.Normal.g,
                               TwinStatusPalette.Normal.b, 0.22f);
            if (_powerBg.color != bg) _powerBg.color = bg;

            bool blocked = _sbo != null && _sbo.IsActive;
            bool usable = node != null && !blocked && !parentDead;
            if (_powerBtn.interactable != usable) _powerBtn.interactable = usable;
            SetAlpha(_powerText, usable ? 1f : 0.45f);
        }

        // ================================================================ 刷新

        private void Update()
        {
            // 【P29-4 P0-1】抽屉 / 模态在前台时，旧面板必须让位 ——
            // 否则它会和抽屉并排显示两块「校园总控」，看起来像两套系统。
            // 只改 _panel 的显隐，不动 _visible：关抽屉后按原状态回来。
            SyncOcclusion();

            if (_toggleKey != KeyCode.None && Input.GetKeyDown(_toggleKey)) Toggle();
            if (!_visible || _panel == null) return;

            float now = Time.unscaledTime;
            if (now < _nextRefresh) return;
            _nextRefresh = now + Mathf.Max(0.05f, _refreshInterval);
            Refresh(false);
        }

        /// <summary>按当前 UI 层状态决定面板是否显示（P29-4 P0-1，与 TwinDetailPanel 同口径）。</summary>
        private void SyncOcclusion()
        {
            if (_panel == null) return;

            bool occluded = TwinUIState.Current != TwinUILayer.None;
            bool want = _visible && !occluded;

            if (_panel.gameObject.activeSelf != want) _panel.gameObject.SetActive(want);
        }

        private void Refresh(bool force)
        {
            if (_panel == null) return;

            TwinRuntime runtime = TwinRuntime.Instance;
            TwinRegistry registry = runtime != null ? runtime.Registry : null;
            int count = registry != null ? registry.Count : 0;

            if (_entriesDirty || count != _lastRegistryCount)
            {
                RebuildEntries(registry);
                _entriesDirty = false;
                _lastRegistryCount = count;
                force = true;
            }

            ApplyFloorButtons();
            ApplyRoomRows(registry);
            ApplyDevRows(force);
            ApplyBatchLine();
            ApplyPowerButton();

            // 顶部汇总：总点位 / 异常 / 断电
            //
            // 【为什么断电要单独数，不算进异常】断一层电会让 15 台设备全部 Offline，
            // 混进「异常」里的话，一次人为拉闸看起来像 15 个故障 —— 而这正是 P15
            // 要拆开的两件事：异常是"要人处理"，断电是"已知状态"。
            int abnormal = 0, total = 0, powerOff = 0;
            if (registry != null)
            {
                IReadOnlyList<TwinAnchor> all = registry.All;
                for (int i = 0; i < all.Count; i++)
                {
                    TwinAnchor a = all[i];
                    if (a == null) continue;
                    if (a.TwinId != null && a.TwinId.StartsWith("BAK", StringComparison.Ordinal)) continue;
                    total++;
                    if (!TwinCircuit.IsPowered(a.TwinId)) { powerOff++; continue; }
                    if (TwinStatusPalette.NeedsAttention(a.Status)) abnormal++;
                }
            }

            _sb.Length = 0;
            _sb.Append(TwinSiteLayout.DefaultBuilding).Append(" · ").Append(total).Append(" 个点位");
            if (abnormal > 0) _sb.Append(" · ").Append(abnormal).Append(" 个异常");
            if (powerOff > 0) _sb.Append(" · ").Append(powerOff).Append(" 台断电");
            SetCached(_sub, _sb.ToString());

            // 正向确认：让「面板到底有没有数据」在 Console 里有一个明确答案。
            // 没有这一行的话，「按 G 弹出来一片空白」和「按 G 没反应」在界面上是同一件事。
            if (!_loggedOnce)
            {
                _loggedOnce = true;
                int controllable = CountBatchTargets();
                Debug.Log("[TwinPanel] 总控面板就绪：" + _roomList.Count + " 个房间 / " +
                          _devs.Count + " 台设备（当前筛选）/ " + total + " 个点位 / " +
                          controllable + " 台可控 · 已并入右侧总控面板");
            }

            _sb.Length = 0;
            if (abnormal > 0) _sb.Append("异常 ").Append(abnormal).Append(" 个");
            else if (powerOff > 0) _sb.Append(powerOff).Append(" 台断电");
            else _sb.Append("全部正常");
            SetCached(_summary, _sb.ToString());
            if (_summary != null)
            {
                _summary.color = abnormal > 0
                    ? TwinStatusPalette.Warning
                    : (powerOff > 0 ? TwinCircuit.AvailabilityColor(DeviceAvailability.PowerOff)
                                    : TwinStatusPalette.TextMuted);
            }

            if (_empty != null) _empty.gameObject.SetActive(_devs.Count == 0);
        }

        /// <summary>
        /// 重建「房间列表 + 设备列表」。只在选中项、楼层筛选、点位数量变化时跑。
        ///
        /// 【为什么房间列表要包含「没有点位的房间」】不包含的话，一个空房间在总控面板上
        /// 直接消失 —— 而「这间房一个点位都没有」本身就是要看见的信息。
        /// </summary>
        private void RebuildEntries(TwinRegistry registry)
        {
            _roomList.Clear();
            _extraZones.Clear();

            // 1. 表内房间（按楼层筛选）
            IReadOnlyList<TwinRoomInfo> rooms = TwinSiteLayout.Rooms;
            for (int i = 0; i < rooms.Count; i++)
            {
                TwinRoomInfo info = rooms[i];
                if (_floorFilter != 0 && info.Floor != _floorFilter) continue;
                _roomList.Add(info);
            }

            // 2. 表外分区（有锚点但没登记楼层）—— 不能丢
            if (registry != null)
            {
                IReadOnlyList<TwinAnchor> all = registry.All;
                for (int i = 0; i < all.Count; i++)
                {
                    TwinAnchor a = all[i];
                    if (a == null || string.IsNullOrEmpty(a.Zone)) continue;
                    if (a.TwinId != null && a.TwinId.StartsWith("BAK", StringComparison.Ordinal)) continue;
                    if (TwinSiteLayout.FloorOf(a.Zone) != TwinSiteLayout.UnknownFloor) continue;
                    if (_extraZones.Contains(a.Zone)) continue;
                    _extraZones.Add(a.Zone);
                }
                for (int i = 0; i < _extraZones.Count; i++)
                {
                    if (_floorFilter != 0) continue;   // 楼层不明的不参与楼层筛选
                    _roomList.Add(TwinSiteLayout.Find(_extraZones[i]));
                }
            }

            // 3. 设备列表
            _devs.Clear();
            if (registry != null)
            {
                IReadOnlyList<TwinAnchor> all = registry.All;
                for (int i = 0; i < all.Count; i++)
                {
                    TwinAnchor a = all[i];
                    if (a == null) continue;
                    if (a.TwinId != null && a.TwinId.StartsWith("BAK", StringComparison.Ordinal)) continue;

                    if (_selectedZone != null)
                    {
                        if (!string.Equals(a.Zone, _selectedZone, StringComparison.Ordinal)) continue;
                    }
                    else if (_floorFilter != 0)
                    {
                        if (TwinSiteLayout.FloorOf(a.Zone) != _floorFilter) continue;
                    }

                    DevEntry e = new DevEntry();
                    e.Anchor = a;
                    e.Model = TwinDeviceModels.Resolve(a.TwinId);
                    e.InScope = TwinControlScope.IsEnabled(a.TwinId);
                    e.CanPower = e.InScope && e.Model != null && e.Model.PrimarySwitchAction != null;
                    e.CanTemp = e.InScope && e.Model != null && e.Model.FindAction("SetTemperature") != null;
                    e.RejectText = "";
                    e.RejectUntil = 0f;
                    _devs.Add(e);
                }

                _devs.Sort(CompareDev);
            }

            // 行池
            EnsureRowPools();

            // 滚动位置回到顶部（换了房间还停在原来的偏移会看到一片空白）
            if (_devScroll != null) _devScroll.verticalNormalizedPosition = 1f;
            if (_treeScroll != null) _treeScroll.verticalNormalizedPosition = 1f;

            // 行池刚变过，几何要跟着重排一次。
            // 【为什么必须在这里调】Layout 用的是「行数 × 行高」，行是上面 EnsureRowPools
            // 才建出来的；只靠 BuildFresh 里那一次 Layout，新增的行会全部堆在 (0,0)。
            Layout();
        }

        private void EnsureRowPools()
        {
            int roomCount = _roomList.Count + 1;      // +1 = 「全部房间」
            for (int i = 0; i < roomCount; i++) EnsureRoomRow(i);
            for (int i = 0; i < _roomRows.Count; i++)
                _roomRows[i].Root.gameObject.SetActive(i < roomCount);

            for (int i = 0; i < _devs.Count; i++) EnsureDevRow(i);
            for (int i = 0; i < _devRows.Count; i++)
                _devRows[i].Root.gameObject.SetActive(i < _devs.Count);
        }

        /// <summary>设备排序：异常在前 → 同状态按 分区顺序 → 类别固定序 → 名字。</summary>
        private static int CompareDev(DevEntry x, DevEntry y)
        {
            int sx = TwinStatusPalette.Severity(x.Anchor.Status);
            int sy = TwinStatusPalette.Severity(y.Anchor.Status);
            if (sx != sy) return sy.CompareTo(sx);

            int ox = TwinSiteLayout.OrderOf(x.Anchor.Zone);
            int oy = TwinSiteLayout.OrderOf(y.Anchor.Zone);
            if (ox != oy) return ox.CompareTo(oy);

            int cx = CategoryRank(x.Anchor.Category);
            int cy = CategoryRank(y.Anchor.Category);
            if (cx != cy) return cx.CompareTo(cy);

            return string.CompareOrdinal(x.Anchor.TwinId, y.Anchor.TwinId);
        }

        private static int CategoryRank(string category)
        {
            switch (category)
            {
                case "Light": return 0;
                case "AC": return 1;
                case "Heat": return 2;
                case "Door": return 3;
                case "PC": return 4;
                case "Sensor": return 5;
                case "Alarm": return 6;
                default: return 7;
            }
        }

        private void ApplyFloorButtons()
        {
            for (int i = 0; i < _floorBtnBgs.Count; i++)
            {
                bool active = _floorBtnValues[i] == _floorFilter;

                // 断电的楼层直接在筛选条上标出来：不然要一路点进去才发现"整层都是黑的"。
                TwinCircuitNode floorNode = _floorBtnValues[i] > 0
                    ? TwinCircuit.Find(TwinCircuit.FloorId(_floorBtnValues[i]))
                    : null;
                bool dead = floorNode != null && !floorNode.On;

                Color bg;
                Color fg;
                if (dead)
                {
                    bg = new Color(TwinStatusPalette.Warning.r, TwinStatusPalette.Warning.g,
                                   TwinStatusPalette.Warning.b, active ? 0.30f : 0.16f);
                    fg = TwinStatusPalette.Warning;
                }
                else
                {
                    bg = active ? new Color(1f, 1f, 1f, 0.18f) : TwinStatusPalette.Track;
                    fg = active ? TwinStatusPalette.TextPrimary : TwinStatusPalette.TextSecondary;
                }

                if (_floorBtnBgs[i] != null && _floorBtnBgs[i].color != bg) _floorBtnBgs[i].color = bg;
                if (_floorBtnTexts[i] != null && _floorBtnTexts[i].color != fg) _floorBtnTexts[i].color = fg;
            }
        }

        private void ApplyRoomRows(TwinRegistry registry)
        {
            float s = _scale;

            for (int i = 0; i < _roomRows.Count; i++)
            {
                RoomRow r = _roomRows[i];
                if (r == null) continue;

                bool isAll = i == 0;
                if (!isAll && i - 1 >= _roomList.Count) continue;

                string zone = isAll ? null : _roomList[i - 1].Zone;
                string name = isAll ? "全部房间" : _roomList[i - 1].RoomName;
                // 「全部房间」那行的台数要算**全部**（且跟随楼层筛选），
                // 不能用 _devs.Count —— 那是「当前选中项」的设备数，选中某间房时就对不上了
                int devices = isAll ? CountAllDevices(registry) : CountDevicesOf(registry, zone);

                if (!string.Equals(r.Name.text, name, StringComparison.Ordinal)) r.Name.text = name;

                // 断电的房间在左树里就要看得出来，否则用户得逐个点进去才发现"这间是黑的"。
                // 用「断电」顶掉台数而不是追加：一行只有这么宽，两个信息抢位置时
                // 「这间没电」比「这间有几台」重要得多。
                bool roomDead = !isAll && !TwinCircuit.IsPoweredZone(zone);

                if (roomDead)
                {
                    SetCached(r.Count, "断电");
                    if (r.Count != null) r.Count.color = TwinCircuit.AvailabilityColor(DeviceAvailability.PowerOff);
                }
                else
                {
                    _sb.Length = 0;
                    _sb.Append(devices).Append(" 台");
                    SetCached(r.Count, _sb.ToString());
                    if (r.Count != null && r.Count.color != TwinStatusPalette.TextMuted)
                        r.Count.color = TwinStatusPalette.TextMuted;
                }

                bool selected = isAll
                    ? _selectedZone == null
                    : string.Equals(_selectedZone, zone, StringComparison.Ordinal);
                Color bg = selected ? new Color(1f, 1f, 1f, 0.10f) : TwinStatusPalette.Transparent;
                if (r.Bg.color != bg) r.Bg.color = bg;

                // 选中项文字提亮到主色，未选中压到次要色 —— 只靠 10% 白的底差在小面板上不够醒目。
                // 断电的房间再压一档（比未选中还暗），一眼能看出"这间现在不能操作"。
                r.Name.color = roomDead
                    ? TwinStatusPalette.TextMuted
                    : (selected ? TwinStatusPalette.TextPrimary : TwinStatusPalette.TextSecondary);
            }
        }

        private static int CountDevicesOf(TwinRegistry registry, string zone)
        {
            if (registry == null || string.IsNullOrEmpty(zone)) return 0;
            int n = 0;
            IReadOnlyList<TwinAnchor> all = registry.All;
            for (int i = 0; i < all.Count; i++)
            {
                TwinAnchor a = all[i];
                if (a == null || a.TwinId == null) continue;
                if (a.TwinId.StartsWith("BAK", StringComparison.Ordinal)) continue;
                if (string.Equals(a.Zone, zone, StringComparison.Ordinal)) n++;
            }
            return n;
        }

        /// <summary>「全部房间」那行的台数：全部点位，但跟随楼层筛选。</summary>
        private int CountAllDevices(TwinRegistry registry)
        {
            if (registry == null) return 0;
            int n = 0;
            IReadOnlyList<TwinAnchor> all = registry.All;
            for (int i = 0; i < all.Count; i++)
            {
                TwinAnchor a = all[i];
                if (a == null || a.TwinId == null) continue;
                if (a.TwinId.StartsWith("BAK", StringComparison.Ordinal)) continue;
                if (_floorFilter != 0 && TwinSiteLayout.FloorOf(a.Zone) != _floorFilter) continue;
                n++;
            }
            return n;
        }

        private void ApplyDevRows(bool force)
        {
            float s = _scale;
            int fsName = Mathf.Max(8, Mathf.RoundToInt(FsRow * s));
            TwinCommandCenter center = EnsureCenter();

            for (int i = 0; i < _devs.Count && i < _devRows.Count; i++)
            {
                DevEntry e = _devs[i];
                DevRow r = _devRows[i];
                if (e == null || r == null || e.Anchor == null) continue;

                TwinAnchor a = e.Anchor;

                // ---- 可用性（P15）：每轮算一次，行内所有判断都读它 ----
                e.Avail = TwinCircuit.AvailabilityOf(a.TwinId);
                bool dead = e.Avail == DeviceAvailability.PowerOff;

                // ---- 状态点 ----
                // 断电时不取 TwinStatus 的颜色：断电 8 秒后状态会自己变 Offline，
                // 但"断电"和"离线"要给不同的灰 —— 前者是人为状态，后者是链路故障。
                Color dotColor = dead
                    ? TwinCircuit.AvailabilityColor(DeviceAvailability.PowerOff)
                    : TwinStatusPalette.Of(a.Status);
                if (r.Dot.color != dotColor) r.Dot.color = dotColor;

                // ---- 名字（窗口拉窄要截断，不能溢出到按钮上）----
                float nameW = r.Name.rectTransform.sizeDelta.x;
                string name = TwinUIFactory.Truncate(a.DisplayName, fsName, nameW);
                if (!string.Equals(r.Name.text, name, StringComparison.Ordinal)) r.Name.text = name;
                r.Name.color = dead ? TwinStatusPalette.TextMuted : TwinStatusPalette.TextPrimary;

                // ---- 状态文字：颜色只表示结果，过程用文字 ----
                string text;
                Color color;
                CommandState st = center != null ? center.StateOf(a.TwinId) : CommandState.Idle;

                if (e.RejectUntil > Time.unscaledTime)
                {
                    text = e.RejectText;
                    color = TwinStatusPalette.Alarm;
                }
                else if (dead)
                {
                    // 断电优先于命令状态：断电期间点按钮会被命令中心当场拒掉，
                    // 那一行本来会显示"设备已断电"三秒然后回到"已开"—— 而"已开"是断电前的旧值。
                    text = TwinCircuit.AvailabilityLabel(e.Avail);
                    color = TwinCircuit.AvailabilityColor(e.Avail);
                }
                else
                {
                    switch (st)
                    {
                        case CommandState.Pending:
                            text = "下发中…";
                            color = TwinStatusPalette.TextSecondary;
                            break;
                        case CommandState.Accepted:
                            text = "已收到，等执行…";
                            color = TwinStatusPalette.TextSecondary;
                            break;
                        case CommandState.Actuated:
                            text = "已执行，等回读…";
                            color = TwinStatusPalette.TextSecondary;
                            break;
                        case CommandState.Confirmed:
                            text = PowerSummary(e);
                            color = TwinStatusPalette.Of(a.Status);
                            break;
                        case CommandState.Failed:
                            text = "失败：" + FailReason(center, a.TwinId);
                            color = TwinStatusPalette.Alarm;
                            break;
                        default:
                            text = PowerSummary(e);
                            // 通电但命令超时 → 补一句"无响应"。它不改按钮可用性
                            // （设备可能只是慢），但能解释"为什么刚才点了没反应"。
                            if (e.Avail == DeviceAvailability.Unreachable) text += " · 无响应";
                            color = TwinStatusPalette.TextSecondary;
                            break;
                    }
                }

                if (r.Status != null)
                {
                    string fitted = TwinUIFactory.Truncate(text, r.Status.fontSize, r.Status.rectTransform.sizeDelta.x);
                    if (!string.Equals(r.Status.text, fitted, StringComparison.Ordinal)) r.Status.text = fitted;
                    if (r.Status.color != color) r.Status.color = color;
                }

                // ---- 按钮：不可控 = 根本没有按钮 ----
                bool hasPower = e.CanPower;
                if (r.BtnOn.gameObject.activeSelf != hasPower) r.BtnOn.gameObject.SetActive(hasPower);
                if (r.BtnOff.gameObject.activeSelf != hasPower) r.BtnOff.gameObject.SetActive(hasPower);

                bool hasTemp = e.CanTemp;
                if (r.BtnMinus.gameObject.activeSelf != hasTemp) r.BtnMinus.gameObject.SetActive(hasTemp);
                if (r.BtnPlus.gameObject.activeSelf != hasTemp) r.BtnPlus.gameObject.SetActive(hasTemp);
                if (r.TempText.gameObject.activeSelf != hasTemp) r.TempText.gameObject.SetActive(hasTemp);

                bool busy = center != null && center.IsBusy(a.TwinId);

                if (hasPower)
                {
                    bool on = false;
                    TwinValue power;
                    if (a.TryGetValue("Power", out power)) on = power.Value > 0.5f;

                    // 没有数据时两个都允许点（状态未知不等于状态是关）
                    bool known = a.HasData;
                    if (r.BtnOnC != null) r.BtnOnC.interactable = !busy && !dead && (!known || !on);
                    if (r.BtnOffC != null) r.BtnOffC.interactable = !busy && !dead && (!known || on);
                    SetAlpha(r.TxtOn, r.BtnOnC != null && r.BtnOnC.interactable ? 1f : 0.4f);
                    SetAlpha(r.TxtOff, r.BtnOffC != null && r.BtnOffC.interactable ? 1f : 0.4f);
                }

                if (hasTemp)
                {
                    // 注意不能写成 `bool ok = !dead && a.TryGetValue(..., out temp);`：
                    // 短路求值时 temp 没有被赋值，C# 会直接报 CS0170。先给默认值。
                    TwinValue temp = default(TwinValue);
                    bool hasTempValue = !dead && a.TryGetValue("SetTemp", out temp);
                    if (hasTempValue)
                    {
                        _sb.Length = 0;
                        _sb.Append(Mathf.Round(temp.Value).ToString("0")).Append("°C");
                        SetCached(r.TempText, _sb.ToString());

                        float rounded = Mathf.Round(temp.Value);
                        if (r.BtnMinusC != null) r.BtnMinusC.interactable = !busy && rounded > 16f;
                        if (r.BtnPlusC != null) r.BtnPlusC.interactable = !busy && rounded < 30f;
                    }
                    else
                    {
                        SetCached(r.TempText, "--");
                        if (r.BtnMinusC != null) r.BtnMinusC.interactable = false;
                        if (r.BtnPlusC != null) r.BtnPlusC.interactable = false;
                    }
                }
            }
        }

        /// <summary>
        /// 右侧表头那行：正常显示「房间 · N 台设备」，确认时显示「开 5 台设备？」，
        /// 批量结束后显示「4 成功 / 1 失败（主照明：设备无响应）」。
        /// </summary>
        private void ApplyBatchLine()
        {
            if (_rightTitle == null) return;

            // 批量结果优先显示几秒，然后再回到房间标题
            if (!string.IsNullOrEmpty(_batchResult) && Time.unscaledTime < _batchResultUntil)
            {
                SetTitleText(_batchResult);
                _rightTitle.color = _batchFail > 0 ? TwinStatusPalette.Warning : TwinStatusPalette.Normal;
                SetBatchButtonsVisible(false, false);
                SetPowerButtonVisible(false);
                return;
            }
            if (!string.IsNullOrEmpty(_batchResult)) _batchResult = "";

            int targets = CountBatchTargets();

            if (_confirming)
            {
                _sb.Length = 0;
                _sb.Append(_confirmValue ? "开 " : "关 ").Append(targets).Append(" 台设备？");
                SetTitleText(_sb.ToString());
                _rightTitle.color = TwinStatusPalette.TextPrimary;
                SetBatchButtonsVisible(false, true);
                SetPowerButtonVisible(false);
                return;
            }

            // 批量进行中：显示进度，不显示房间名 —— 用户此刻关心的是「下完了没有」
            if (_batchIds.Count > 0 && _batchDoneAt <= 0f)
            {
                int ok, fail, pending;
                TallyBatch(out ok, out fail, out pending);
                _batchOk = ok;
                _batchFail = fail + _batchRejects.Count;

                if (pending == 0)
                {
                    _batchDoneAt = Time.unscaledTime;
                    _batchResult = ComposeBatchResult();
                    _batchResultUntil = Time.unscaledTime + 8f;
                    _batchIds.Clear();
                    Debug.Log("[TwinPanel] " + _batchResult);

                    // 立刻显示结果，不要「先闪一帧房间名、0.2 秒后才变结果」
                    SetTitleText(_batchResult);
                    _rightTitle.color = _batchFail > 0 ? TwinStatusPalette.Warning : TwinStatusPalette.Normal;
                    SetBatchButtonsVisible(false, false);
                    SetPowerButtonVisible(false);
                    return;
                }
                else
                {
                    _sb.Length = 0;
                    _sb.Append("执行中 ").Append(ok + fail).Append("/").Append(ok + fail + pending);
                    SetTitleText(_sb.ToString());
                    _rightTitle.color = TwinStatusPalette.TextSecondary;
                    SetBatchButtonsVisible(false, false);
                    SetPowerButtonVisible(false);
                    return;
                }
            }

            // 常态：房间标题
            _sb.Length = 0;
            if (_selectedZone == null)
            {
                _sb.Append(_floorFilter == 0 ? "全部设备" : _floorFilter + "F 全部设备");
            }
            else
            {
                _sb.Append(TwinSiteLayout.Find(_selectedZone).RoomName);
            }
            _sb.Append(" · ").Append(_devs.Count).Append(" 台");

            int off = CountPowerOffTargets();
            if (targets > 0 || off > 0)
            {
                _sb.Append("（");
                if (targets > 0) _sb.Append("可控 ").Append(targets);
                if (targets > 0 && off > 0) _sb.Append(" · ");
                if (off > 0) _sb.Append("断电 ").Append(off);
                _sb.Append("）");
            }
            SetTitleText(_sb.ToString());
            _rightTitle.color = TwinStatusPalette.TextPrimary;

            SetBatchButtonsVisible(targets > 0, false);
            SetPowerButtonVisible(true);

            if (_batchOnBtn != null) _batchOnBtn.interactable = targets > 0;
            if (_batchOffBtn != null) _batchOffBtn.interactable = targets > 0;
        }

        /// <summary>
        /// 表头那行文字。**必须截断** —— Legacy Text 的 Overflow 模式不会换行也不会省略，
        /// 表头挤了三个按钮之后，长房间名会直接画到按钮上面（P4 的老毛病）。
        /// </summary>
        private void SetTitleText(string value)
        {
            if (_rightTitle == null) return;
            string fitted = TwinUIFactory.Truncate(value, _rightTitle.fontSize,
                _rightTitle.rectTransform.sizeDelta.x);
            SetCached(_rightTitle, fitted);
        }

        private void SetPowerButtonVisible(bool visible)
        {
            if (_powerBg == null) return;
            if (_powerBg.gameObject.activeSelf != visible) _powerBg.gameObject.SetActive(visible);
        }

        private void SetBatchButtonsVisible(bool batch, bool confirm)
        {
            if (_batchOnBg != null && _batchOnBg.gameObject.activeSelf != batch)
                _batchOnBg.gameObject.SetActive(batch);
            if (_batchOffBg != null && _batchOffBg.gameObject.activeSelf != batch)
                _batchOffBg.gameObject.SetActive(batch);
            if (_okBg != null && _okBg.gameObject.activeSelf != confirm)
                _okBg.gameObject.SetActive(confirm);
            if (_cancelBg != null && _cancelBg.gameObject.activeSelf != confirm)
                _cancelBg.gameObject.SetActive(confirm);
        }

        private void TallyBatch(out int ok, out int fail, out int pending)
        {
            ok = 0; fail = 0; pending = 0;
            TwinCommandCenter center = _center;
            if (center == null) return;

            for (int i = 0; i < _batchIds.Count; i++)
            {
                CommandState st = center.StateOf(_batchIds[i]);
                if (st == CommandState.Confirmed) ok++;
                else if (st == CommandState.Failed) fail++;
                else pending++;
            }
        }

        /// <summary>
        /// 「4 成功 / 1 失败（主照明：设备无响应）」。
        ///
        /// 【为什么必须带失败的那台是谁】只说「1 失败」等于没说 ——
        /// 用户还得自己去翻 5 行找哪一行是红的。Spec §11 的验收项就是这一条。
        /// </summary>
        private string ComposeBatchResult()
        {
            int ok, fail, pending;
            TallyBatch(out ok, out fail, out pending);
            int totalFail = fail + _batchRejects.Count;

            _sb.Length = 0;
            _sb.Append(_confirmValue ? "全开：" : "全关：");
            _sb.Append(ok).Append(" 成功");
            if (totalFail > 0) _sb.Append(" / ").Append(totalFail).Append(" 失败");

            if (totalFail > 0)
            {
                _sb.Append("（");
                bool first = true;
                TwinCommandCenter center = _center;

                if (center != null)
                {
                    for (int i = 0; i < _batchIds.Count; i++)
                    {
                        if (center.StateOf(_batchIds[i]) != CommandState.Failed) continue;
                        if (!first) _sb.Append("；");
                        _sb.Append(ShortName(_batchIds[i])).Append("：").Append(FailReason(center, _batchIds[i]));
                        first = false;
                    }
                }
                for (int i = 0; i < _batchRejects.Count; i++)
                {
                    if (!first) _sb.Append("；");
                    _sb.Append(_batchRejects[i]);
                    first = false;
                }
                _sb.Append("）");
            }
            return _sb.ToString();
        }

        private static string ShortName(string twinId)
        {
            if (string.IsNullOrEmpty(twinId)) return "?";
            int slash = twinId.IndexOf('/');
            if (slash < 0 || slash + 1 >= twinId.Length) return twinId;
            return twinId.Substring(slash + 1);
        }

        private static string FailReason(TwinCommandCenter center, string twinId)
        {
            if (center == null) return "未知";
            TwinCommand cmd = center.Current(twinId);
            if (cmd == null) return "未知";
            if (!string.IsNullOrEmpty(cmd.Reason)) return cmd.Reason;

            switch (cmd.Code)
            {
                case CommandFailCode.NotSupported: return "设备不支持";
                case CommandFailCode.OutOfRange: return "参数越界";
                case CommandFailCode.Offline: return "设备无响应";
                case CommandFailCode.PowerOff: return "设备已断电";
                case CommandFailCode.ExecFailed: return "设备执行失败";
                case CommandFailCode.NoEffect: return "执行了但状态没变";
                case CommandFailCode.Mismatch: return "状态没到期望值";
                case CommandFailCode.SendFailed: return "命令没发出去";
                case CommandFailCode.Rejected: return "本机拒绝";
                case CommandFailCode.PlaybackBlocked: return "回放中不可控";
                default: return "未知原因";
            }
        }

        /// <summary>这一行设备现在的开关状态摘要（只在「没有进行中的命令」时用）。</summary>
        private static string PowerSummary(DevEntry e)
        {
            TwinValue power;
            if (!e.Anchor.TryGetValue("Power", out power)) return "无数据";

            bool on = power.Value > 0.5f;
            if (!on) return "已关";

            TwinValue temp;
            if (e.CanTemp && e.Anchor.TryGetValue("SetTemp", out temp))
                return "已开 · " + Mathf.Round(temp.Value).ToString("0") + "°C";
            return "已开";
        }

        // ================================================================ 小工具

        private static void SetCached(Text target, string value)
        {
            if (target == null) return;
            if (string.Equals(target.text, value, StringComparison.Ordinal)) return;
            target.text = value;
        }

        private static void SetAlpha(Text text, float alpha)
        {
            if (text == null) return;
            Color c = text.color;
            if (Mathf.Abs(c.a - alpha) < 0.01f) return;
            c.a = alpha;
            text.color = c;
        }
    }
}

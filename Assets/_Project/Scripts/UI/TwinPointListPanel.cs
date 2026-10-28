using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using SchoolTwin.Core;
using SchoolTwin.Interaction;
using SchoolTwin.Twin;

namespace SchoolTwin.UI
{
    /// <summary>
    /// 点位清单浮窗（按 T 开关）：把场景里所有孪生点位按「分区」列出来，点一下飞过去。
    ///
    /// ── 它解决什么问题 ──────────────────────────────────────────────
    /// 之前想找某个点位只能靠鼠标在三维场景里点 —— 点位挂在墙上、天花板上、
    /// 甚至门框里，稍微转个角度就找不到；教室里 32 个点位更是没法「一眼看全」。
    /// 有了这张表，找点位变成「看名字 → 点一下」。
    ///
    /// ── 它也是一个「窗口」──────────────────────────────────────────
    /// 拖动 / 拉右下角改大小 / 点 × 关闭（T 重新打开），能力全部来自
    /// <see cref="TwinWindowHandle"/>，和告警历史浮层、详情卡片是同一套。
    /// 初始位置在**屏幕左侧**：右侧是详情卡片、右上偏左是告警历史，左边是空的。
    ///
    /// ── 排版分三层，别混 ──────────────────────────────────────────
    /// `Layout()`      ：只管窗口几何（标题栏、关闭按钮、滚动视口、缩放手柄的位置），
    ///                   拖动缩放时每帧都跑。
    /// `LayoutRows()`  ：行几何（每行的 y 累加、名称列宽度），窗口一改宽就要重算。
    /// `Refresh()`     ：内容（哪些行、文字、状态色），0.4s 一次。
    /// 混在一起的话，拖窗口时每帧都要把 32 个点位重新分一次组。
    ///
    /// ── 滚动为什么不用 LayoutGroup ────────────────────────────────
    /// 行是手工 Place 的（本工程一律不用 LayoutGroup：10Hz 刷新的数值会反复触发布局重排）。
    /// 所以滚动用 ScrollRect + RectMask2D，但 content 的尺寸由我们自己算，
    /// ScrollRect 只负责挪 content.anchoredPosition。
    /// </summary>
    [AddComponentMenu("SchoolTwin/点位清单浮窗 (TwinPointListPanel)")]
    [DisallowMultipleComponent]
    public class TwinPointListPanel : MonoBehaviour
    {
        [Header("开关")]
        [SerializeField] private KeyCode _toggleKey = KeyCode.None;   // P28：并入总控抽屉，快捷键停用

        // ---------------- 版式常量（未缩放，全部 × _scale）----------------
        private const float BaseWidth = 380f;
        private const float BasePad = 16f;
        private const float BaseHeaderH = 56f;
        private const float BaseGroupH = 26f;
        private const float BaseRowH = 28f;
        /// <summary>底部留白。不能小于缩放手柄高度，否则最后一行会盖住「◢」。</summary>
        private const float BaseBottomPad = 26f;
        private const float BaseCloseSize = 22f;
        private const float BaseGripSize = 22f;
        private const float BaseEmptyH = 26f;
        private const float MinWidth = 300f;

        private const int FsTitle = 17;
        private const int FsSub = 12;
        private const int FsGroup = 12;
        private const int FsRow = 13;

        /// <summary>
        /// 分区的显示顺序与中文名。
        ///
        /// 【为什么不在这里写死】这张表原来在这里手写了一份，和总控面板（P14）要用的
        /// `TwinSiteLayout` 完全重复。两份表一定会漂移（改了一处忘了另一处），
        /// 所以改成从 `TwinSiteLayout` 取 —— 那边还多了楼层信息，P14 的楼层筛选要用。
        ///
        /// 表里没写的新分区不会丢 —— 见 <see cref="AppendUnknownZones"/>。
        /// </summary>
        private static readonly string[][] ZoneTable = TwinSiteLayout.ZoneTitleTable();

        // ---------------- 外部参数 ----------------
        private float _scale = 1f;
        private float _margin;
        private float _maxWidth = 9999f;
        private float _maxHeight = 9999f;
        private TwinDetailPanel _detail;

        // ---------------- 状态 ----------------
        private bool _visible;
        private float _nextRefresh;
        private int _lastRegistryCount = -1;
        private bool _entriesDirty = true;

        /// <summary>用户动过窗口没有。动过之后重建 UI 也要保留他摆的位置和大小。</summary>
        private bool _userMoved;
        private Vector2 _userPos;
        private Vector2 _userSize;

        /// <summary>
        /// 用户**亲手拉过高度**没有。
        ///
        /// 【为什么必须和「拖动」分开】窗口高度默认是「按内容算」的。
        /// 如果用户只是把窗口拖到别处（没改尺寸），之后点位数变了还是要按内容自动长高；
        /// 只有他真的拉过右下角，才该锁住他给的高度。
        /// 判据是「当前高度 vs 上一次自动算出来的高度」差多少。
        /// </summary>
        private bool _userSized;
        private float _autoH;

        // ---------------- 节点 ----------------
        private RectTransform _panel;
        private RectTransform _dragBar;
        private Text _title;
        private Text _sub;
        private Text _hint;
        private RectTransform _closeBg;
        private Text _closeText;
        private Image _divider;
        private Text _empty;
        private RectTransform _grip;
        private RectTransform _viewport;
        private RectTransform _content;
        private ScrollRect _scroll;

        private readonly List<TwinClickRow> _rows = new List<TwinClickRow>(48);
        private readonly List<Entry> _entries = new List<Entry>(48);
        private readonly List<TwinAnchor> _bucket = new List<TwinAnchor>(8);
        private readonly List<string> _extraZones = new List<string>(4);
        private readonly System.Text.StringBuilder _sb = new System.Text.StringBuilder(96);

        /// <summary>列表里的一行：要么是分区标题，要么是一个点位。</summary>
        private sealed class Entry
        {
            public bool Header;
            public string Title;
            public int Count;
            /// <summary>仅组头用：这个分区里需要关注的点位有几个。</summary>
            public int Abnormal;
            public TwinAnchor Anchor;
            public TwinStatus Status;

            /// <summary>名称列宽度与字号：窗口被拉窄时要靠它把名字截断。</summary>
            public float NameWidth;
            public int NameFontSize;
        }

        public bool IsVisible { get { return _visible; } }

        // ================================================================ 构建

        /// <summary>由 <see cref="TwinDetailPanel"/> 在构建界面时调用（可重复调用）。</summary>
        /// <param name="root">挂载父节点（画布根）。</param>
        /// <param name="scale">当前 UI 缩放。</param>
        /// <param name="margin">距父节点左上角的边距。</param>
        /// <param name="maxWidth">可用宽度，超出就收窄。</param>
        /// <param name="maxHeight">可用高度，超出就变成滚动。</param>
        /// <param name="detail">点行之后把相机飞过去，复用它的 FocusOn（同一套飞行参数）。</param>
        public void Build(RectTransform root, float scale, float margin,
                          float maxWidth, float maxHeight, TwinDetailPanel detail)
        {
            if (root == null) return;

            bool same = _panel != null
                        && Mathf.Approximately(_scale, scale)
                        && Mathf.Approximately(_margin, margin)
                        && Mathf.Approximately(_maxWidth, maxWidth)
                        && Mathf.Approximately(_maxHeight, maxHeight);
            if (same) return;

            // 父面板重建 UI 时会把我们连同节点一起销毁，此时缓存的引用是「假 null」
            // （C# 引用非 null，但 Unity 的 == 返回 true）。所以这里必须用 == null 判断。
            Teardown();

            _scale = scale;
            _margin = margin;
            _maxWidth = maxWidth;
            _maxHeight = maxHeight;
            _detail = detail;
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
        /// 父面板重建 UI 时由 <see cref="TwinDetailPanel"/> 调用：节点会被父级一起销毁，
        /// 这里只把缓存引用清掉。
        ///
        /// 【为什么必须显式清】`Object.Destroy` 是**延迟到帧末**才真正销毁的。
        /// 若只依赖「_panel == null 就重建」，同一帧内重建时 `_panel != null` 仍然成立
        /// → Build 直接返回 → 帧末旧节点被销毁、新节点却没建出来，浮窗从此不再出现且不报错。
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
            _empty = null;
            _grip = null;
            _viewport = null;
            _content = null;
            _scroll = null;
            _rows.Clear();
            _entries.Clear();
            _entriesDirty = true;
            _lastRegistryCount = -1;
        }

        private void BuildFresh(RectTransform root)
        {
            float s = _scale;

            // 先算内容有多高，才能决定窗口初始高度（内容少就别撑满屏幕）
            RebuildEntries();
            _entriesDirty = false;
            float w = Mathf.Min(BaseWidth * s, Mathf.Max(MinWidth, _maxWidth));
            float h = AutoHeight();
            _autoH = h;

            _panel = (RectTransform)TwinUIFactory.Node("PointListPanel", root).transform;
            // 先关掉再搭：ScrollRect 在 OnEnable 里就检查 content 有没有赋值，
            // 而我们是「先 AddComponent 再赋 content」—— 节点活着的话会先报一次
            // 「missing Content reference」。整块搭完再按 _visible 打开。
            _panel.gameObject.SetActive(false);
            // 左上角锚点语义：Place 就是「距父节点左上角 (x, y)」，拖动时不用换锚
            TwinUIFactory.Place(_panel, _margin, _margin, w, h);

            // 背景要能挡住点击，否则点浮窗会穿透到三维场景里选中别的点位
            TwinRoundedRect bg = TwinUIFactory.Rounded(_panel, "Bg", 14f * s,
                TwinStatusPalette.PanelTop, TwinStatusPalette.PanelBottom,
                TwinStatusPalette.PanelBorder, 1f, true);
            TwinUIFactory.Stretch(bg.rectTransform);

            BuildChrome();
            BuildScrollArea();
            BuildRows();
            BuildGrip();

            // 用户之前拖过 → 还原他摆的位置和大小（重建不该把他的布局重置掉）
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

        /// <summary>标题栏（拖动把手）、关闭按钮。</summary>
        private void BuildChrome()
        {
            float s = _scale;

            // 拖动区必须**先建**（在最底层）：标题文字压在上面，
            // 所以那几个 Text 的 raycastTarget 全部关掉，否则点在字上拖不动窗口。
            Image dragImg = TwinUIFactory.Block(_panel, "DragBar", TwinStatusPalette.Transparent, true);
            _dragBar = dragImg.rectTransform;
            TwinWindowHandle drag = _dragBar.gameObject.AddComponent<TwinWindowHandle>();
            drag.Window = _panel;
            drag.HandleMode = TwinWindowHandle.Mode.Move;
            drag.OnChanged = OnGeometryChanged;
            drag.HoverTarget = dragImg;   // 悬停提亮：否则「这里能拖」没有视觉暗示

            _title = TwinUIFactory.Label(_panel, "Title", Mathf.Max(8, Mathf.RoundToInt(FsTitle * s)),
                TextAnchor.UpperLeft, TwinStatusPalette.TextPrimary, FontStyle.Bold);
            _title.raycastTarget = false;
            _title.text = "点位清单";

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

            _divider = TwinUIFactory.Block(_panel, "Divider", TwinStatusPalette.Divider, false);

            // 空态文字压在视口上方（raycastTarget 关掉，否则滚轮会打在它身上）
            _empty = TwinUIFactory.Label(_panel, "Empty", Mathf.Max(8, Mathf.RoundToInt(FsRow * s)),
                TextAnchor.UpperLeft, TwinStatusPalette.TextMuted);
            _empty.raycastTarget = false;
            _empty.text = "场景里还没有注册任何点位";
        }

        /// <summary>
        /// 滚动视口：RectMask2D 负责裁剪，ScrollRect 负责挪 content。
        ///
        /// 【为什么 viewport 自己挂 ScrollRect】这样滚轮事件由它接住，
        /// 不用再写一个 IScrollHandler。行上的滚轮会沿层级冒泡上来。
        /// </summary>
        private void BuildScrollArea()
        {
            // 视口必须有个能挡射线的 Graphic：否则鼠标压在列表空白处滚轮没有接收者。
            // 走 TwinUIFactory.Block 而不是手写 AddComponent<Image> —— 后者不会补
            // CanvasRenderer，图形会静默不渲染（本工程踩过两次，见 TwinUIFactory.Graphic 注释）。
            Image vpImg = TwinUIFactory.Block(_panel, "Viewport", TwinStatusPalette.Transparent, true);
            _viewport = vpImg.rectTransform;
            vpImg.gameObject.AddComponent<RectMask2D>();

            _content = (RectTransform)TwinUIFactory.Node("Content", _viewport).transform;
            // 横向铺满视口、纵向由我们给高度：pivot 在顶部，滚下去就是 anchoredPosition.y 变大
            _content.anchorMin = new Vector2(0f, 1f);
            _content.anchorMax = new Vector2(1f, 1f);
            _content.pivot = new Vector2(0.5f, 1f);
            _content.anchoredPosition = Vector2.zero;
            _content.sizeDelta = new Vector2(0f, 0f);

            _scroll = vpImg.gameObject.AddComponent<ScrollRect>();
            _scroll.viewport = _viewport;
            _scroll.content = _content;
            _scroll.horizontal = false;
            _scroll.vertical = true;
            _scroll.movementType = ScrollRect.MovementType.Clamped;
            _scroll.inertia = true;
            _scroll.decelerationRate = 0.12f;
            _scroll.scrollSensitivity = 30f;
        }

        /// <summary>
        /// 右下角的缩放手柄：两根 1px 细线拼成「◢」的暗示（纯色块会显得像内容）。
        /// </summary>
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
            // 下限：标题栏 + 一行 + 底部留白 —— 再小就什么都看不见了
            resize.MinSize = new Vector2(MinWidth * s,
                (BaseHeaderH + BaseRowH + BaseBottomPad) * s);
            resize.OnChanged = OnGeometryChanged;

            // 行是后建的（在层级里压在手柄上面），靠 BaseBottomPad 留出垂直空间才不会盖住它；
            // 这里再把层级提到最上，双保险。
            _grip.SetAsLastSibling();
        }

        private void BuildRows()
        {
            EnsureRows(_entries.Count);
            for (int i = 0; i < _rows.Count; i++)
                _rows[i].gameObject.SetActive(i < _entries.Count);
        }

        private TwinClickRow EnsureRow(int index)
        {
            while (_rows.Count <= index)
            {
                int i = _rows.Count;
                float s = _scale;
                int fs = Mathf.Max(8, Mathf.RoundToInt(FsRow * s));

                GameObject go = TwinUIFactory.Node("PtRow" + i, _content);

                // 行背景必须是 raycastTarget，否则收不到点击
                Image bg = TwinUIFactory.Block(go.transform, "Bg", TwinStatusPalette.Transparent, true);
                TwinUIFactory.Stretch(bg.rectTransform);

                Image dot = TwinUIFactory.Block(go.transform, "Dot", TwinStatusPalette.Unknown, false);

                Text label = TwinUIFactory.Label(go.transform, "Name", fs, TextAnchor.MiddleLeft,
                    TwinStatusPalette.TextPrimary);
                label.raycastTarget = false;

                Text badge = TwinUIFactory.Label(go.transform, "Badge",
                    Mathf.Max(8, Mathf.RoundToInt(FsGroup * s)), TextAnchor.MiddleRight,
                    TwinStatusPalette.TextMuted);
                badge.raycastTarget = false;

                TwinClickRow row = go.AddComponent<TwinClickRow>();
                row.Background = bg;
                row.Dot = dot;
                row.Label = label;
                row.Badge = badge;
                row.OnClick = OnRowClicked;
                row.SetBaseColor(TwinStatusPalette.Transparent);

                _rows.Add(row);
            }
            return _rows[index];
        }

        private void EnsureRows(int count)
        {
            while (_rows.Count < count) EnsureRow(_rows.Count);
        }

        // ================================================================ 内容

        /// <summary>
        /// 把注册表里的点位按分区整理成「组头 + 条目」的扁平列表。
        ///
        /// 【只在点位数变化时重建】这个列表有 40 多行、每行一个对象，
        /// 每 0.4s 重建一次纯属浪费。状态是每帧另外刷的（<see cref="UpdateStatuses"/>），
        /// 所以「点位状态变了」不需要重建结构。
        /// </summary>
        private void RebuildEntries()
        {
            _entries.Clear();

            TwinRuntime runtime = TwinRuntime.Instance;
            TwinRegistry registry = runtime != null ? runtime.Registry : null;
            if (registry == null) return;

            for (int i = 0; i < ZoneTable.Length; i++) AppendZone(registry, ZoneTable[i][0], ZoneTable[i][1]);
            AppendUnknownZones(registry);
        }

        private void AppendZone(TwinRegistry registry, string zone, string title)
        {
            _bucket.Clear();
            IReadOnlyList<TwinAnchor> all = registry.All;
            for (int i = 0; i < all.Count; i++)
            {
                TwinAnchor a = all[i];
                if (a != null && string.Equals(a.Zone, zone, StringComparison.Ordinal)) _bucket.Add(a);
            }
            if (_bucket.Count == 0) return;

            _bucket.Sort(CompareAnchor);

            // 组头顺便统计「这个房间有几个点位要关注」。
            // 这比「找到点位」更常用：一眼就能看出哪个房间有事，不用逐条往下读。
            int abnormal = 0;
            TwinStatus worst = TwinStatus.Normal;
            for (int i = 0; i < _bucket.Count; i++)
            {
                TwinStatus st = CurrentStatus(_bucket[i]);
                if (TwinStatusPalette.NeedsAttention(st)) abnormal++;
                if (TwinStatusPalette.Severity(st) > TwinStatusPalette.Severity(worst)) worst = st;
            }

            Entry header = new Entry();
            header.Header = true;
            header.Title = title;
            header.Count = _bucket.Count;
            header.Abnormal = abnormal;
            header.Status = worst;
            _entries.Add(header);

            for (int i = 0; i < _bucket.Count; i++)
            {
                TwinAnchor a = _bucket[i];
                Entry e = new Entry();
                e.Header = false;
                e.Anchor = a;
                e.Status = CurrentStatus(a);
                // DisplayName 是「2-1班 温湿度传感器」，组头已经把「2-1班」写出来了，
                // 条目里再重复一遍只是噪声。
                e.Title = StripZonePrefix(a.DisplayName, title);
                _entries.Add(e);
            }
        }

        /// <summary>
        /// 表里没登记的分区也要显示出来。
        /// 漏了它的后果很隐蔽：新建的点位在场景里有、在数据里跑，但清单里就是找不到。
        /// </summary>
        private void AppendUnknownZones(TwinRegistry registry)
        {
            _extraZones.Clear();
            IReadOnlyList<TwinAnchor> all = registry.All;
            for (int i = 0; i < all.Count; i++)
            {
                TwinAnchor a = all[i];
                if (a == null) continue;
                string zone = a.Zone;
                if (string.IsNullOrEmpty(zone)) continue;
                if (ZoneTitle(zone) != zone) continue;          // 已登记
                if (!_extraZones.Contains(zone)) _extraZones.Add(zone);
            }
            if (_extraZones.Count == 0) return;

            _extraZones.Sort(StringComparer.Ordinal);
            for (int i = 0; i < _extraZones.Count; i++) AppendZone(registry, _extraZones[i], _extraZones[i]);
        }

        /// <summary>组内排序：先按类别（传感器 → 照明 → 空调 → 暖气 → 电脑 → 门禁 → 消防），再按 id。</summary>
        private static int CompareAnchor(TwinAnchor x, TwinAnchor y)
        {
            int cx = CategoryRank(x.Category);
            int cy = CategoryRank(y.Category);
            if (cx != cy) return cx.CompareTo(cy);
            return string.CompareOrdinal(x.TwinId, y.TwinId);
        }

        private static int CategoryRank(string category)
        {
            switch (category)
            {
                case "Sensor": return 0;
                case "Light": return 1;
                case "AC": return 2;
                case "Heat": return 3;
                case "PC": return 4;
                case "Door": return 5;
                case "Alarm": return 6;
                default: return 9;
            }
        }

        private static string ZoneTitle(string zone)
        {
            for (int i = 0; i < ZoneTable.Length; i++)
            {
                if (string.Equals(ZoneTable[i][0], zone, StringComparison.Ordinal)) return ZoneTable[i][1];
            }
            return zone;
        }

        private static string StripZonePrefix(string displayName, string zoneTitle)
        {
            if (string.IsNullOrEmpty(displayName)) return "";
            if (string.IsNullOrEmpty(zoneTitle)) return displayName;
            if (!displayName.StartsWith(zoneTitle, StringComparison.Ordinal)) return displayName;

            int cut = zoneTitle.Length;
            // 去掉紧跟的一个空格（全角空格也认）
            while (cut < displayName.Length && (displayName[cut] == ' ' || displayName[cut] == '\u3000')) cut++;
            return cut >= displayName.Length ? displayName : displayName.Substring(cut);
        }

        private float ContentHeight()
        {
            float s = _scale;
            float total = 0f;
            for (int i = 0; i < _entries.Count; i++)
                total += (_entries[i].Header ? BaseGroupH : BaseRowH) * s;
            return total;
        }

        /// <summary>
        /// 「按内容算」的窗口高度：内容多高窗口就多高，最多到屏幕可用高度。
        ///
        /// 【为什么要单独一个方法，而且每次重建列表都要重算】
        /// 第一版只在建窗口时算一次，结果窗口永远停在「建的时候有几个点位」的高度。
        /// 而面板是在 `Awake` 早期建的，那一刻点位还没注册完 → 内容高度是 0
        /// → 窗口被压成两条标题那么高（约 60 单位），视口高度算出来是 0，
        /// **整张列表一个字都看不见**，只有标题栏浮在那儿。
        /// 后来 Refresh 里查到了 32 个点位，但没人再去改窗口高度，于是一直错着。
        /// </summary>
        private float AutoHeight()
        {
            float s = _scale;
            float want = ContentHeight() + BaseHeaderH * s + BaseBottomPad * s;
            return Mathf.Min(want, Mathf.Max(160f * s, _maxHeight));
        }

        // ================================================================ 排版（几何）

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

            // ---- 拖动区：标题栏 ----
            // ① 往里缩一点：它悬停时会染成一层极淡的白，铺满整宽的话这层白会盖到
            //    圆角外面，窗口四个角冒出方角。
            // ② 右端停在关闭按钮左侧：重叠的话，鼠标一移到 × 上就退出拖动区，高亮会闪掉。
            float dragX = 6f * s;
            float dragRight = w - pad - closeSize - 4f * s;
            TwinUIFactory.Place(_dragBar, dragX, 4f * s,
                Mathf.Max(0f, dragRight - dragX), Mathf.Max(1f, headerH - 6f * s));

            TwinUIFactory.Place(_closeBg, w - pad - closeSize, 8f * s, closeSize, closeSize);

            float titleW = Mathf.Max(40f, w - pad * 2f - closeSize - 8f * s);
            TwinUIFactory.Place(_title.rectTransform, pad, 10f * s, titleW, FsTitle * s + 6f);
            TwinUIFactory.Place(_sub.rectTransform, pad, 10f * s + FsTitle * s + 6f, titleW, FsSub * s + 6f);

            float hintRight = w - pad - closeSize - 6f * s;
            TwinUIFactory.Place(_hint.rectTransform, pad, 12f * s,
                Mathf.Max(0f, hintRight - pad), FsSub * s + 6f);

            TwinUIFactory.Place(_divider.rectTransform, pad, headerH - 1f, innerW, 1f);

            // ---- 滚动视口 ----
            float vpH = Mathf.Max(1f, h - headerH - BaseBottomPad * s);
            TwinUIFactory.Place(_viewport, pad, headerH, innerW, vpH);

            TwinUIFactory.Place(_empty.rectTransform, pad, headerH + 6f * s, innerW, BaseEmptyH * s);

            // ---- 缩放手柄（右下角）----
            TwinUIFactory.Place(_grip, w - gripSize - 2f * s, h - gripSize - 2f * s, gripSize, gripSize);

            LayoutRows(innerW, vpH);
        }

        /// <summary>行几何：每行的 y 是「上面所有行的高度之和」，所以必须按顺序累加。</summary>
        private void LayoutRows(float contentW, float viewportH)
        {
            float s = _scale;
            float y = 0f;

            // 组头的徽标要放「5 个 · 2 异常」，条目只要放「数据陈旧」——宽度分开算。
            // 给窄了的话（MiddleRight + Overflow）文字会往左画，直接压到名字上。
            float badgeWItem = Mathf.Min(60f * s, contentW * 0.30f);
            float badgeWHead = Mathf.Min(110f * s, contentW * 0.50f);
            float nameX = 22f * s;
            float nameWItem = Mathf.Max(40f, contentW - nameX - badgeWItem - 6f * s);
            float nameWHead = Mathf.Max(40f, contentW - badgeWHead - 8f * s);
            int nameFs = Mathf.Max(8, Mathf.RoundToInt(FsRow * s));

            for (int i = 0; i < _entries.Count && i < _rows.Count; i++)
            {
                Entry e = _entries[i];
                TwinClickRow row = _rows[i];
                if (row == null) continue;

                float rowH = (e.Header ? BaseGroupH : BaseRowH) * s;
                TwinUIFactory.Place((RectTransform)row.transform, 0f, y, contentW, rowH);

                float dotSize = 8f * s;
                TwinUIFactory.Place(row.Dot.rectTransform, 4f * s, (rowH - dotSize) * 0.5f, dotSize, dotSize);

                // 组头把状态点让出来给文字用（组头没有状态）
                float labelX = e.Header ? 4f * s : nameX;
                float labelW = e.Header ? nameWHead : nameWItem;
                TwinUIFactory.Place(row.Label.rectTransform, labelX, 0f, labelW, rowH);

                float badgeW = e.Header ? badgeWHead : badgeWItem;
                TwinUIFactory.Place(row.Badge.rectTransform, contentW - badgeW, 0f, badgeW, rowH);

                e.NameWidth = labelW;
                e.NameFontSize = nameFs;
                y += rowH;
            }

            // content 的总高度：ScrollRect 靠它决定能滚多远
            if (_content != null)
            {
                _content.sizeDelta = new Vector2(0f, Mathf.Max(y, 0f));
                // 内容变短（点位被删掉）时把滚动位置拉回来，否则会停在一片空白上
                float maxScroll = Mathf.Max(0f, y - viewportH);
                Vector2 pos = _content.anchoredPosition;
                if (pos.y > maxScroll) _content.anchoredPosition = new Vector2(pos.x, maxScroll);
            }
        }

        /// <summary>拖动/缩放之后由 <see cref="TwinWindowHandle"/> 回调。</summary>
        private void OnGeometryChanged()
        {
            if (_panel == null) return;

            _userMoved = true;
            _userPos = _panel.anchoredPosition;
            _userSize = _panel.sizeDelta;

            // 只有「高度真的和自动算出来的不一样」才算用户亲手改过尺寸。
            // 否则单纯拖动一下窗口，之后点位增减就再也不自动长高了。
            if (Mathf.Abs(_panel.sizeDelta.y - _autoH) > 1f) _userSized = true;

            Layout();

            // 别在这里直接 Refresh：拖动时每帧都会调到这里，
            // 而 Refresh 要重新走一遍 32 个点位的状态。交给 Update 的节流即可。
            _nextRefresh = 0f;
        }

        // ================================================================ 开关

        public void Toggle() { Show(!_visible); }

        public void Show(bool visible)
        {
            _visible = visible;
            if (_panel != null) _panel.gameObject.SetActive(visible);
            if (visible) Refresh(true);
        }

        private void OnCloseClicked()
        {
            Show(false);
        }

        private void OnRowClicked(TwinAnchor anchor)
        {
            if (anchor == null) return;

            // 走详情面板的 FocusOn：选中 + 相机飞过去，飞行距离和点场景物体时一致
            if (_detail != null) _detail.FocusOn(anchor);
            else TwinPicker.Select(anchor);

            Refresh(true);      // 立刻把选中高亮刷出来，不用等 0.4s
        }

        private void Update()
        {
            if (_toggleKey != KeyCode.None && Input.GetKeyDown(_toggleKey)) Toggle();
            if (!_visible || _panel == null) return;

            float now = Time.unscaledTime;
            if (now < _nextRefresh) return;
            _nextRefresh = now + 0.4f;
            Refresh(false);
        }

        // ================================================================ 刷新

        private void Refresh(bool force)
        {
            if (_panel == null) return;

            TwinRuntime runtime = TwinRuntime.Instance;
            TwinRegistry registry = runtime != null ? runtime.Registry : null;
            int count = registry != null ? registry.Count : 0;

            // 点位数变了 → 结构要重排（新增/删除点位、场景重载）
            if (_entriesDirty || count != _lastRegistryCount)
            {
                RebuildEntries();
                _entriesDirty = false;
                _lastRegistryCount = count;
                EnsureRows(_entries.Count);
                for (int i = 0; i < _rows.Count; i++)
                    _rows[i].gameObject.SetActive(i < _entries.Count);
                force = true;
            }
            else
            {
                UpdateStatuses();
            }

            int zones = 0, abnormal = 0;
            for (int i = 0; i < _entries.Count; i++)
            {
                if (_entries[i].Header) { zones++; continue; }
                if (TwinStatusPalette.NeedsAttention(_entries[i].Status)) abnormal++;
            }

            _sb.Length = 0;
            _sb.Append(count).Append(" 个点位 · ").Append(zones).Append(" 个分区");
            if (abnormal > 0) _sb.Append(" · ").Append(abnormal).Append(" 个异常");
            SetCached(_sub, _sb.ToString());

            if (_empty != null) _empty.gameObject.SetActive(_entries.Count == 0);

            // 点位数变了 → 窗口高度也要跟着长（用户没亲手拉过高度时才自动改）
            if (!_userSized)
            {
                float want = AutoHeight();
                if (Mathf.Abs(_panel.sizeDelta.y - want) > 0.5f)
                {
                    _panel.sizeDelta = new Vector2(_panel.sizeDelta.x, want);
                    _autoH = want;
                }
            }

            // 【顺序不能反】Layout 会把「名称列当前宽度」写进每个 Entry，
            // ApplyRows 靠那个宽度截断文字。反过来做的话，新重建的 Entry 宽度还是 0，
            // FitText 会返回空串 —— 表现为「刚打开时列表里一个字都没有」。
            Layout();
            ApplyRows();
        }

        private void UpdateStatuses()
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                Entry h = _entries[i];
                if (!h.Header)
                {
                    if (h.Anchor != null) h.Status = CurrentStatus(h.Anchor);
                    continue;
                }

                // 组头：把它后面连续的点位扫一遍，重算「几个异常 + 最差状态」
                int abnormal = 0;
                TwinStatus worst = TwinStatus.Normal;
                for (int k = i + 1; k < _entries.Count && !_entries[k].Header; k++)
                {
                    Entry e = _entries[k];
                    if (e.Anchor != null) e.Status = CurrentStatus(e.Anchor);
                    if (TwinStatusPalette.NeedsAttention(e.Status)) abnormal++;
                    if (TwinStatusPalette.Severity(e.Status) > TwinStatusPalette.Severity(worst)) worst = e.Status;
                }
                h.Abnormal = abnormal;
                h.Status = worst;
            }
        }

        private void ApplyRows()
        {
            TwinAnchor selected = TwinPicker.Selected;

            for (int i = 0; i < _entries.Count && i < _rows.Count; i++)
            {
                Entry e = _entries[i];
                TwinClickRow row = _rows[i];
                if (row == null) continue;

                if (e.Header)
                {
                    // 组头：不响应点击、不悬停提亮（亮了却不能点，看着像坏了）
                    row.Target = null;
                    row.SetHoverEnabled(false);
                    row.Background.raycastTarget = false;
                    row.SetBaseColor(TwinStatusPalette.Transparent);
                    row.Dot.gameObject.SetActive(false);

                    SetCached(row.Label, e.Title);
                    row.Label.color = TwinStatusPalette.TextSecondary;
                    row.Label.fontStyle = FontStyle.Bold;

                    _sb.Length = 0;
                    _sb.Append(e.Count).Append(" 个");
                    if (e.Abnormal > 0) _sb.Append(" · ").Append(e.Abnormal).Append(" 异常");
                    SetCached(row.Badge, _sb.ToString());
                    row.Badge.color = e.Abnormal > 0 ? BadgeColor(e.Status) : TwinStatusPalette.TextMuted;
                    continue;
                }

                TwinAnchor anchor = e.Anchor;
                bool isSelected = anchor != null && anchor == selected;

                row.Target = anchor;
                row.SetHoverEnabled(true);
                row.Background.raycastTarget = true;
                // 选中的那行给一层极淡的底：点完行之后能看出「现在说的是哪一条」
                row.SetBaseColor(isSelected ? TwinStatusPalette.RowHover : TwinStatusPalette.Transparent);

                row.Dot.gameObject.SetActive(true);
                row.Dot.color = TwinStatusPalette.Of(e.Status);

                row.Label.fontStyle = isSelected ? FontStyle.Bold : FontStyle.Normal;
                row.Label.color = isSelected ? TwinStatusPalette.TextPrimary : TwinStatusPalette.TextSecondary;
                SetCached(row.Label, FitText(e.Title, e.NameFontSize, e.NameWidth));

                SetCached(row.Badge, TwinStatusPalette.Label(e.Status));
                row.Badge.color = BadgeColor(e.Status);
            }
        }

        // ================================================================ 工具

        /// <summary>
        /// 状态文字的颜色。
        ///
        /// 【为什么不能直接用状态色】Offline / Unknown / Stale 的 BT.601 亮度都低于 0.4，
        /// 压在深色面板上基本看不见。这几个状态统一用次要文字色 —— 状态本身由
        /// 左边的圆点表达，文字只需要「读得出来」。
        /// </summary>
        private static Color BadgeColor(TwinStatus status)
        {
            switch (status)
            {
                case TwinStatus.Normal: return TwinStatusPalette.Normal;
                case TwinStatus.Warning: return TwinStatusPalette.Warning;
                case TwinStatus.Alarm: return TwinStatusPalette.Alarm;
                default: return TwinStatusPalette.TextMuted;
            }
        }

        /// <summary>点位整体状态，并把「数据陈旧」也算进去（锚点自身可能还没走到 Stale）。</summary>
        private static TwinStatus CurrentStatus(TwinAnchor anchor)
        {
            if (anchor == null) return TwinStatus.Unknown;
            TwinStatus status = anchor.Status;
            if (anchor.IsStale && status != TwinStatus.Offline) status = TwinStatus.Stale;
            return status;
        }

        private static void SetCached(Text target, string value)
        {
            if (target == null) return;
            if (string.Equals(target.text, value, StringComparison.Ordinal)) return;
            target.text = value;
        }

        /// <summary>
        /// 按列宽截断文本，超出的用「…」收尾。
        ///
        /// 【为什么不用 TwinUIFactory.Truncate】它按 `fontSize * 0.62` 估每个字符的宽度，
        /// 那个系数是给英文/数字用的。中文是方块字，宽度接近整个字号，
        /// 用 0.62 估会少算一半 —— 名字照样溢出（Legacy Text 是 Overflow 模式，
        /// 不会换行，直接画到右边的状态文字上）。
        /// </summary>
        private static string FitText(string text, int fontSize, float maxWidth)
        {
            if (string.IsNullOrEmpty(text)) return "";
            if (maxWidth <= 1f) return "";

            float fs = Mathf.Max(1, fontSize);
            float w = 0f;
            for (int i = 0; i < text.Length; i++)
            {
                float cw = IsWideChar(text[i]) ? fs : fs * 0.55f;
                if (w + cw > maxWidth)
                {
                    if (i <= 0) return "";
                    return text.Substring(0, i) + "…";
                }
                w += cw;
            }
            return text;
        }

        /// <summary>中日韩统一表意文字 + 全角标点的起始区段（近似判据，够用）。</summary>
        private static bool IsWideChar(char c)
        {
            return c >= 0x2E80;
        }
    }
}

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using SchoolTwin.Core;
using SchoolTwin.Twin;

namespace SchoolTwin.UI
{
    /// <summary>
    /// 告警历史浮层（按 H 开关）：把 <see cref="TwinAlarmHistory"/> 记下的时间线画出来。
    ///
    /// ── 它是一个「窗口」，不是一块固定面板 ─────────────────────────
    /// 可以拖标题栏移动、拖右下角改大小、点 × 关闭（H 重新打开）。
    /// 拖动/缩放能力由 <see cref="TwinWindowHandle"/> 提供 —— 那是通用组件，
    /// 以后新增面板直接挂一个就行，不用再写一遍坐标换算。
    ///
    /// ── 位置：初始锚在「详情卡片的左侧」────────────────────────────
    /// 第一版用「画布正中」，结果右半边压到详情卡片上（重叠约 184 单位）。
    /// 原因：卡片占掉画布右侧约 40%，画布中线离卡片左边缘比半个浮层还近，
    /// 所以任何「居中 + 有点宽度」的浮层都必然压上去。
    /// 现在初始位置用 `anchor=(1,1)+pivot=(1,1)+anchoredPosition=(-rightInset,-topInset)`，
    /// 语义是「右上角对齐父节点右上角，往左让开 屏幕边距 + 卡片宽 + 间隙」。
    /// **用户一旦拖动，就切成左上锚点的自由定位**（见 TwinWindowHandle.FreeAnchor）。
    ///
    /// ── 排版分两层，别混 ──────────────────────────────────────────
    /// `Layout()`：只管几何（位置、宽高、列宽、能放几行），拖动缩放时每帧都会跑。
    /// `Refresh()`：只管内容（文字、颜色、显示哪几条），0.25s 一次。
    /// 混在一起的话，拖窗口时每帧都要重新查一次告警历史并排序，纯浪费。
    /// </summary>
    [AddComponentMenu("SchoolTwin/告警历史浮层 (TwinAlarmHistoryPanel)")]
    [DisallowMultipleComponent]
    public class TwinAlarmHistoryPanel : MonoBehaviour
    {
        [Header("开关")]
        [SerializeField] private KeyCode _toggleKey = KeyCode.H;

        [Header("容量")]
        [Tooltip("最多显示几条记录（窗口拉高也不会超过这个数）")]
        [SerializeField] private int _maxRows = 8;

        // ---- 版式常量（未缩放，全部 × _scale）----
        private const float BaseWidth = 720f;
        private const float BasePad = 16f;
        private const float BaseHeaderH = 56f;
        private const float BaseRowH = 30f;
        /// <summary>
        /// 底部留白。**不能小于缩放手柄的高度** —— 手柄摆在右下角，
        /// 行是后建的（在层级里压在手柄上面），留白不够时最后一行就会盖住「◢」。
        /// 26 是在 s=0.75 下手柄（22×0.75≈17）之外再留 2 个单位余量反推出来的。
        /// </summary>
        private const float BaseBottomPad = 26f;
        private const float BaseEmptyH = 26f;
        private const float BaseCloseSize = 22f;
        private const float BaseGripSize = 22f;
        private const float MinWidth = 320f;

        private const int FsTitle = 17;
        private const int FsSub = 12;
        private const int FsRow = 13;

        private float _scale = 1f;
        private float _rightInset;
        private float _topInset;
        private float _maxWidth = 9999f;

        private bool _visible;
        private float _nextRefresh;

        /// <summary>用户动过窗口没有。动过之后重建 UI 也要保留他摆的位置。</summary>
        private bool _userMoved;
        private Vector2 _userPos;
        private Vector2 _userSize;

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
        private readonly List<Row> _rows = new List<Row>(8);

        private readonly List<TwinAlarmHistory.AlarmRecord> _buffer =
            new List<TwinAlarmHistory.AlarmRecord>(16);
        private readonly System.Text.StringBuilder _sb = new System.Text.StringBuilder(96);

        private sealed class Row
        {
            public RectTransform Root;
            public Image Bg;
            public Image Dot;
            public Text Clock;
            public Text Name;
            public Text Status;
            public Text Duration;

            /// <summary>名称列当前宽度与字号。窗口被拉窄时要靠它把名字截断。</summary>
            public float NameWidth;
            public int NameFontSize;
        }

        public bool IsVisible { get { return _visible; } }

        /// <summary>由 <see cref="TwinDetailPanel"/> 在构建界面时调用（可重复调用）。</summary>
        /// <param name="rightInset">面板右上角距父节点右缘的距离（让开详情卡片）。</param>
        /// <param name="topInset">面板上缘距父节点上缘的距离。</param>
        /// <param name="maxWidth">可用横向空间；超出就收窄，避免顶出画布左缘。</param>
        public void Build(RectTransform root, float scale, float rightInset, float topInset, float maxWidth)
        {
            if (root == null) return;

            bool same = _panel != null
                        && Mathf.Approximately(_scale, scale)
                        && Mathf.Approximately(_rightInset, rightInset)
                        && Mathf.Approximately(_topInset, topInset)
                        && Mathf.Approximately(_maxWidth, maxWidth);
            if (same) return;

            // 父面板重建 UI 时会把我们连同节点一起销毁，此时缓存的引用是「假 null」
            // （C# 引用非 null，但 Unity 的 == 返回 true）。所以这里必须用 == null 判断。
            Teardown();

            _scale = scale;
            _rightInset = rightInset;
            _topInset = topInset;
            _maxWidth = maxWidth;
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
        /// 若只依赖「_panel == null 就重建」，在同一帧内重建（RebuildUI）时
        /// `_panel != null` 仍然成立 → Build 直接返回 → 帧末旧节点被销毁，
        /// 新节点却从没建出来，历史浮层从此再也不出现，而且不报错。
        /// 这和卡片/告警面板「重建要手动清引用」是同一个坑。
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
            _rows.Clear();
        }

        // ================================================================ 构建

        private void BuildFresh(RectTransform root)
        {
            float s = _scale;
            int rows = Mathf.Max(1, _maxRows);
            float w = Mathf.Min(BaseWidth * s, Mathf.Max(MinWidth, _maxWidth));
            float h = BaseHeaderH * s + rows * BaseRowH * s + BaseBottomPad * s;

            _panel = (RectTransform)TwinUIFactory.Node("AlarmHistoryPanel", root).transform;
            TwinUIFactory.AnchorTopRight(_panel, _rightInset, _topInset, w, h);

            // 背景要能挡住点击，否则会点到浮层底下的告警行
            TwinRoundedRect bg = TwinUIFactory.Rounded(_panel, "Bg", 14f * s,
                TwinStatusPalette.PanelTop, TwinStatusPalette.PanelBottom,
                TwinStatusPalette.PanelBorder, 1f, true);
            TwinUIFactory.Stretch(bg.rectTransform);

            BuildChrome();
            BuildRowsSkeleton();

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

        /// <summary>标题栏（拖动把手）、关闭按钮、右下角缩放手柄。</summary>
        private void BuildChrome()
        {
            float s = _scale;

            // 拖动区必须**先建**（在最底层）：标题/副标题文字压在上面，
            // 所以那几个 Text 的 raycastTarget 全部关掉，否则点在字上拖不动窗口。
            Image dragImg = TwinUIFactory.Block(_panel, "DragBar", TwinStatusPalette.Transparent, true);
            _dragBar = dragImg.rectTransform;
            TwinWindowHandle drag = _dragBar.gameObject.AddComponent<TwinWindowHandle>();
            drag.Window = _panel;
            drag.HandleMode = TwinWindowHandle.Mode.Move;
            drag.OnChanged = OnGeometryChanged;
            // 悬停时标题栏微微提亮 —— 否则「这里能拖」完全没有视觉暗示
            drag.HoverTarget = dragImg;

            _title = TwinUIFactory.Label(_panel, "Title", Mathf.Max(8, Mathf.RoundToInt(FsTitle * s)),
                TextAnchor.UpperLeft, TwinStatusPalette.TextPrimary, FontStyle.Bold);
            _title.raycastTarget = false;
            _title.text = "告警历史";

            _sub = TwinUIFactory.Label(_panel, "Sub", Mathf.Max(8, Mathf.RoundToInt(FsSub * s)),
                TextAnchor.UpperLeft, TwinStatusPalette.TextSecondary);
            _sub.raycastTarget = false;

            _hint = TwinUIFactory.Label(_panel, "Hint", Mathf.Max(8, Mathf.RoundToInt(FsSub * s)),
                TextAnchor.UpperRight, TwinStatusPalette.TextMuted);
            _hint.raycastTarget = false;
            _hint.text = _toggleKey + " 键开关";

            // 关闭按钮
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

            _empty = TwinUIFactory.Label(_panel, "Empty", Mathf.Max(8, Mathf.RoundToInt(FsRow * s)),
                TextAnchor.UpperLeft, TwinStatusPalette.TextMuted);
            _empty.raycastTarget = false;
            _empty.text = "暂无告警记录 · 出现异常时会自动记下";

            BuildGrip();
        }

        /// <summary>
        /// 右下角的缩放手柄：两根 1px 细线拼成一个「◢」的暗示。
        /// 纯色块会显得像内容，细线看起来才像把手。
        /// </summary>
        private void BuildGrip()
        {
            float s = _scale;
            float g = BaseGripSize * s;

            Image hit = TwinUIFactory.Block(_panel, "Grip", TwinStatusPalette.Transparent, true);
            _grip = hit.rectTransform;

            Color line = new Color(TwinStatusPalette.TextMuted.r, TwinStatusPalette.TextMuted.g,
                                   TwinStatusPalette.TextMuted.b, 0.55f);

            Image h = TwinUIFactory.Block(_grip, "GripH", line, false);
            TwinUIFactory.Place(h.rectTransform, 0f, g - 1f, g * 0.55f, 1f);

            Image v = TwinUIFactory.Block(_grip, "GripV", line, false);
            TwinUIFactory.Place(v.rectTransform, g - 1f, g * 0.45f, 1f, g * 0.55f);

            TwinWindowHandle resize = hit.gameObject.AddComponent<TwinWindowHandle>();
            resize.Window = _panel;
            resize.HandleMode = TwinWindowHandle.Mode.ResizeBottomRight;
            resize.MinSize = new Vector2(400f * s, 150f * s);
            resize.OnChanged = OnGeometryChanged;
        }

        private void BuildRowsSkeleton()
        {
            int rows = Mathf.Max(1, _maxRows);
            for (int i = 0; i < rows; i++) EnsureRow(i);
        }

        private Row EnsureRow(int index)
        {
            while (_rows.Count <= index)
            {
                int i = _rows.Count;
                float s = _scale;
                int fs = Mathf.Max(8, Mathf.RoundToInt(FsRow * s));

                Row row = new Row();

                GameObject go = TwinUIFactory.Node("HistRow" + i, _panel);
                row.Root = (RectTransform)go.transform;

                row.Bg = TwinUIFactory.Block(go.transform, "Bg", TwinStatusPalette.Transparent, false);
                TwinUIFactory.Stretch(row.Bg.rectTransform);

                row.Dot = TwinUIFactory.Block(go.transform, "Dot", TwinStatusPalette.Unknown, false);

                row.Clock = TwinUIFactory.Label(go.transform, "Clock", fs, TextAnchor.MiddleLeft,
                    TwinStatusPalette.TextSecondary);
                row.Clock.raycastTarget = false;

                row.Name = TwinUIFactory.Label(go.transform, "Name", fs, TextAnchor.MiddleLeft,
                    TwinStatusPalette.TextPrimary);
                row.Name.raycastTarget = false;

                row.Status = TwinUIFactory.Label(go.transform, "Status", fs, TextAnchor.MiddleLeft,
                    TwinStatusPalette.TextSecondary);
                row.Status.raycastTarget = false;

                row.Duration = TwinUIFactory.Label(go.transform, "Duration", fs, TextAnchor.MiddleRight,
                    TwinStatusPalette.TextMuted);
                row.Duration.raycastTarget = false;

                _rows.Add(row);
            }
            return _rows[index];
        }

        // ================================================================ 排版（只管几何）

        /// <summary>当前尺寸下最多能放几行。窗口被拉矮就自动少放几行。</summary>
        private int RowCapacity()
        {
            if (_panel == null) return 1;
            float s = _scale;
            float avail = _panel.sizeDelta.y - BaseHeaderH * s - BaseBottomPad * s;
            int cap = Mathf.FloorToInt(avail / Mathf.Max(1f, BaseRowH * s));
            return Mathf.Clamp(cap, 1, Mathf.Max(1, _maxRows));
        }

        private void Layout()
        {
            if (_panel == null) return;

            float s = _scale;
            float pad = BasePad * s;
            float headerH = BaseHeaderH * s;
            float rowH = BaseRowH * s;
            float w = _panel.sizeDelta.x;
            float h = _panel.sizeDelta.y;
            float closeSize = BaseCloseSize * s;
            float gripSize = BaseGripSize * s;
            float innerW = Mathf.Max(0f, w - pad * 2f);

            // ---- 拖动区：标题栏 ----
            // ① 往里缩一点：它悬停时会染成一层极淡的白，铺满整宽的话这层白会盖到
            //    圆角外面，窗口四个角冒出方角。
            // ② 右端停在关闭按钮左侧：重叠的话，鼠标一移到 × 上就退出拖动区，
            //    标题栏的高亮会在这一瞬间闪掉。
            float dragX = 6f * s;
            float dragRight = w - pad - closeSize - 4f * s;
            TwinUIFactory.Place(_dragBar, dragX, 4f * s,
                Mathf.Max(0f, dragRight - dragX), Mathf.Max(1f, headerH - 6f * s));

            // ---- 关闭按钮 ----
            TwinUIFactory.Place(_closeBg, w - pad - closeSize, 8f * s, closeSize, closeSize);

            // ---- 标题 / 副标题 / 提示 ----
            float titleW = Mathf.Max(40f, w - pad * 2f - closeSize - 8f * s);
            TwinUIFactory.Place(_title.rectTransform, pad, 10f * s, titleW, FsTitle * s + 6f);
            TwinUIFactory.Place(_sub.rectTransform, pad, 10f * s + FsTitle * s + 6f,
                titleW, FsSub * s + 6f);

            float hintRight = w - pad - closeSize - 6f * s;
            TwinUIFactory.Place(_hint.rectTransform, pad, 12f * s,
                Mathf.Max(0f, hintRight - pad), FsSub * s + 6f);

            TwinUIFactory.Place(_divider.rectTransform, pad, headerH - 1f, innerW, 1f);

            // ---- 空态文字 ----
            TwinUIFactory.Place(_empty.rectTransform, pad, headerH + 6f * s, innerW, BaseEmptyH * s);

            // ---- 缩放手柄（右下角）----
            // 行是后建的，在层级里压在手柄上面；靠 BaseBottomPad 留出垂直空间才不会盖住它。
            TwinUIFactory.Place(_grip, w - gripSize - 2f * s, h - gripSize - 2f * s, gripSize, gripSize);

            // ---- 行 ----
            int cap = RowCapacity();
            for (int i = 0; i < _rows.Count; i++)
            {
                Row row = _rows[i];
                if (i >= cap)
                {
                    row.Root.gameObject.SetActive(false);
                    continue;
                }
                LayoutRow(row, i, pad, headerH, rowH, innerW, s);
            }
        }

        private void LayoutRow(Row row, int index, float pad, float headerH, float rowH, float innerW, float s)
        {
            TwinUIFactory.Place(row.Root, pad, headerH + index * rowH, innerW, rowH);

            // 列宽**从右往左**算：时刻和时长是定宽的（定长字符串），
            // 中间的名称吃掉剩下的全部空间。这样窗口一窄，先被挤的是名称列，
            // 而不是让时长列被推出窗口外 —— 那正是第一版压到卡片上的形态。
            float clockX = 20f * s;
            float clockW = 70f * s;
            float nameX = clockX + clockW + 6f * s;

            float durW = Mathf.Min(160f * s, innerW * 0.34f);
            float durX = innerW - durW;

            float statusW = 64f * s;
            float statusX = durX - 6f * s - statusW;

            float nameW = Mathf.Max(40f, statusX - 6f * s - nameX);

            row.NameWidth = nameW;
            row.NameFontSize = Mathf.Max(8, Mathf.RoundToInt(FsRow * s));

            float dotSize = 8f * s;
            TwinUIFactory.Place(row.Dot.rectTransform, 2f * s, (rowH - dotSize) * 0.5f, dotSize, dotSize);
            TwinUIFactory.Place(row.Clock.rectTransform, clockX, 0f, clockW, rowH);
            TwinUIFactory.Place(row.Name.rectTransform, nameX, 0f, nameW, rowH);
            TwinUIFactory.Place(row.Status.rectTransform, statusX, 0f, statusW, rowH);
            TwinUIFactory.Place(row.Duration.rectTransform, durX, 0f, durW, rowH);
        }

        /// <summary>拖动/缩放之后由 <see cref="TwinWindowHandle"/> 回调。</summary>
        private void OnGeometryChanged()
        {
            if (_panel == null) return;

            _userMoved = true;
            _userPos = _panel.anchoredPosition;
            _userSize = _panel.sizeDelta;

            Layout();

            // 别在这里直接 Refresh：拖动时每帧都会调到这里，
            // 而 Refresh 要重排 200 条历史记录。交给 Update 的 0.25s 节流即可，
            // 把下一次刷新提前到下一帧就行。
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

        private void Update()
        {
            if (Input.GetKeyDown(_toggleKey)) Toggle();
            if (!_visible) return;

            float now = Time.unscaledTime;
            if (now < _nextRefresh) return;
            _nextRefresh = now + 0.25f;
            Refresh(false);
        }

        // ================================================================ 内容

        private void Refresh(bool force)
        {
            if (_panel == null) return;

            TwinAlarmHistory history = TwinAlarmHistory.Instance;
            float now = Time.realtimeSinceStartup;
            int capacity = RowCapacity();

            int total = history != null ? history.TotalCount : 0;
            int ongoing = history != null ? history.OngoingCount : 0;

            _sb.Length = 0;
            _sb.Append("累计 ").Append(total).Append(" 次");
            if (ongoing > 0) _sb.Append("   ·   进行中 ").Append(ongoing).Append(" 个");
            _sb.Append("   ·   显示 ").Append(capacity).Append(" 条");
            if (history == null || !history.IsHooked) _sb.Append("   ·   告警中心未连接");
            SetCached(_sub, _sb.ToString());

            _buffer.Clear();
            if (history != null) history.GetRecent(_buffer, capacity);

            int shown = Mathf.Min(_buffer.Count, capacity);
            if (_empty != null) _empty.gameObject.SetActive(shown == 0);

            for (int i = 0; i < shown; i++) FillRow(i, _buffer[i], now);
            for (int i = shown; i < _rows.Count; i++) _rows[i].Root.gameObject.SetActive(false);
        }

        private void FillRow(int index, TwinAlarmHistory.AlarmRecord rec, float now)
        {
            Row row = EnsureRow(index);
            row.Root.gameObject.SetActive(true);

            Color color = TwinStatusPalette.Of(rec.PeakStatus);
            bool live = rec.Ongoing;

            // 进行中的行给一层极淡的状态色底：一眼就能分出「还在报」和「已经过去」。
            // 已结束的行不加底 —— 历史里绝大多数是已结束的，全加上等于没有强调。
            row.Bg.color = live ? new Color(color.r, color.g, color.b, 0.10f) : TwinStatusPalette.Transparent;
            row.Dot.color = live ? color : new Color(color.r, color.g, color.b, 0.55f);

            SetCached(row.Clock, rec.Clock);

            // 只显示 DisplayName。不追加 Zone —— 它在这里是 `ClassComputer` 这种英文 id，
            // 而 DisplayName 已经写成「计算机教室 环境传感器」，两串并排只是噪声。
            // 用户可以把窗口拉窄，所以这里必须按当前列宽截断：
            // Legacy Text 是 Overflow 模式，不截断就会直接画进状态列里。
            SetCached(row.Name, FitText(rec.DisplayName, row.NameFontSize, row.NameWidth));

            SetCached(row.Status, TwinStatusPalette.Label(rec.PeakStatus));
            row.Status.color = live ? color : TwinStatusPalette.TextSecondary;

            _sb.Length = 0;
            if (live) _sb.Append("进行中 ");
            _sb.Append(TwinAlarmHistory.FormatDuration(rec.Duration(now)));
            SetCached(row.Duration, _sb.ToString());
            row.Duration.color = live ? TwinStatusPalette.TextPrimary : TwinStatusPalette.TextMuted;
        }

        private static void SetCached(Text target, string value)
        {
            if (target == null) return;
            if (target.text == value) return;
            target.text = value;
        }

        /// <summary>
        /// 按列宽截断文本，超出的用「…」收尾。
        ///
        /// 【为什么不用 TwinUIFactory.Truncate】它按 `fontSize * 0.62` 估算每个字符的宽度，
        /// 那个系数是给英文/数字用的。中文是方块字，宽度接近整个字号，
        /// 用 0.62 估会少算一半 —— 名字照样溢出。这里按「中日韩字符 1.0×字号、
        /// 其余 0.55×字号」分档估，和实际渲染基本对得上。
        /// </summary>
        private static string FitText(string text, int fontSize, float maxWidth)
        {
            if (string.IsNullOrEmpty(text)) return "";
            if (maxWidth <= 1f) return "";

            float fs = Mathf.Max(1, fontSize);
            float w = 0f;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                float cw = IsWideChar(c) ? fs : fs * 0.55f;
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

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
    /// ── 为什么是「浮层」而不是「右栏再加一块」──────────────────────
    /// 右栏已经挤满了：详情卡片 + 告警面板在视口里几乎占满，
    /// 再加一块必然触发垂直上限（`_uiScale` 早就被压到下限 0.75 了）。
    /// 历史是**按需查看**的信息，不该常驻占位 —— 做成浮层，开关一次不影响任何现有布局。
    ///
    /// ── 位置：锚在「详情卡片的左侧」，不是画布居中 ─────────────────
    /// 2026-10-21 第一版用的是「画布正中」，结果右半边压到详情卡片上，
    /// 时长列直接画在卡片上面（实测重叠约 184 单位）。
    /// 原因：卡片占掉画布右侧约 40%，**画布中线离卡片左边缘比半个浮层还近**，
    /// 所以任何「居中 + 有点宽度」的浮层都必然压上去 —— 这不是宽度没调好，是定位方式错了。
    ///
    /// 改成 `anchor=(1,1) + pivot=(1,1) + anchoredPosition=(-rightInset, -topInset)`：
    /// 语义是「我的右上角，对齐父节点右上角往左/往下各让一段」，
    /// 让开的量 = 屏幕边距 + 卡片宽 + 间隙。与画布多宽完全无关，永远不会算错。
    ///
    /// ── 刷新策略 ──────────────────────────────────────────────────
    /// 隐藏时 Update 直接 return（零开销）。显示时 0.25s 刷一次：
    /// 时长精确到秒，0.25s 足够跟上；再快只会让整块面板每秒重绘 60 次。
    /// </summary>
    [AddComponentMenu("SchoolTwin/告警历史浮层 (TwinAlarmHistoryPanel)")]
    [DisallowMultipleComponent]
    public class TwinAlarmHistoryPanel : MonoBehaviour
    {
        [Header("开关")]
        [SerializeField] private KeyCode _toggleKey = KeyCode.H;

        [Header("容量")]
        [Tooltip("浮层里最多显示几条记录")]
        [SerializeField] private int _maxRows = 8;

        // ---- 版式常量（未缩放，全部 × _scale）----
        private const float BaseWidth = 720f;
        private const float BasePad = 16f;
        private const float BaseHeaderH = 56f;
        private const float BaseRowH = 30f;
        private const float BaseBottomPad = 16f;
        private const float BaseEmptyH = 26f;
        private const float MinWidth = 320f;

        private const int FsTitle = 17;
        private const int FsSub = 12;
        private const int FsRow = 13;

        private float _scale = 1f;
        private float _rightInset;
        private float _topInset;
        private float _maxWidth = 9999f;
        /// <summary>实际宽度（已按可用空间收窄）。列宽全部由它推出来。</summary>
        private float _width;

        private bool _visible;
        private float _nextRefresh;

        private RectTransform _panel;
        private Text _title;
        private Text _sub;
        private Text _empty;
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
            _title = null;
            _sub = null;
            _empty = null;
            _rows.Clear();
        }

        private void BuildFresh(RectTransform root)
        {
            float s = _scale;
            float headerH = BaseHeaderH * s;
            float rowH = BaseRowH * s;
            float bottomPad = BaseBottomPad * s;
            int rows = Mathf.Max(1, _maxRows);

            // 先按基准宽度算，再用「卡片左侧还剩多少」收窄
            _width = Mathf.Min(BaseWidth * s, Mathf.Max(MinWidth, _maxWidth));
            float w = _width;
            float h = headerH + rows * rowH + bottomPad;

            _panel = (RectTransform)TwinUIFactory.Node("AlarmHistoryPanel", root).transform;
            TwinUIFactory.AnchorTopRight(_panel, _rightInset, _topInset, w, h);

            // 背景要能挡住点击，否则会点到浮层底下的告警行
            TwinRoundedRect bg = TwinUIFactory.Rounded(_panel, "Bg", 14f * s,
                TwinStatusPalette.PanelTop, TwinStatusPalette.PanelBottom,
                TwinStatusPalette.PanelBorder, 1f, true);
            TwinUIFactory.Stretch(bg.rectTransform);

            float pad = BasePad * s;

            _title = TwinUIFactory.Label(_panel, "Title", Mathf.Max(8, Mathf.RoundToInt(FsTitle * s)),
                TextAnchor.UpperLeft, TwinStatusPalette.TextPrimary, FontStyle.Bold);
            TwinUIFactory.Place(_title.rectTransform, pad, 10f * s, w - pad * 2f, FsTitle * s + 6f);
            _title.text = "告警历史";

            _sub = TwinUIFactory.Label(_panel, "Sub", Mathf.Max(8, Mathf.RoundToInt(FsSub * s)),
                TextAnchor.UpperLeft, TwinStatusPalette.TextSecondary);
            TwinUIFactory.Place(_sub.rectTransform, pad, 10f * s + FsTitle * s + 6f, w - pad * 2f, FsSub * s + 6f);

            Text hint = TwinUIFactory.Label(_panel, "Hint", Mathf.Max(8, Mathf.RoundToInt(FsSub * s)),
                TextAnchor.UpperRight, TwinStatusPalette.TextMuted);
            TwinUIFactory.Place(hint.rectTransform, pad, 12f * s, w - pad * 2f, FsSub * s + 6f);
            hint.text = _toggleKey + " 键关闭";

            Image divider = TwinUIFactory.Block(_panel, "Divider", TwinStatusPalette.Divider, false);
            TwinUIFactory.Place(divider.rectTransform, pad, headerH - 1f, w - pad * 2f, 1f);

            _empty = TwinUIFactory.Label(_panel, "Empty", Mathf.Max(8, Mathf.RoundToInt(FsRow * s)),
                TextAnchor.UpperLeft, TwinStatusPalette.TextMuted);
            TwinUIFactory.Place(_empty.rectTransform, pad, headerH + 6f * s, w - pad * 2f, BaseEmptyH * s);
            _empty.text = "暂无告警记录 · 出现异常时会自动记下";

            _panel.gameObject.SetActive(_visible);
            Refresh(true);
        }

        public void Toggle() { Show(!_visible); }

        public void Show(bool visible)
        {
            _visible = visible;
            if (_panel != null) _panel.gameObject.SetActive(visible);
            if (visible) Refresh(true);
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

        // ================================================================ 刷新

        private void Refresh(bool force)
        {
            if (_panel == null) return;

            TwinAlarmHistory history = TwinAlarmHistory.Instance;
            float now = Time.realtimeSinceStartup;

            int total = history != null ? history.TotalCount : 0;
            int ongoing = history != null ? history.OngoingCount : 0;

            _sb.Length = 0;
            _sb.Append("累计 ").Append(total).Append(" 次");
            if (ongoing > 0) _sb.Append("   ·   进行中 ").Append(ongoing).Append(" 个");
            _sb.Append("   ·   显示最近 ").Append(Mathf.Max(1, _maxRows)).Append(" 条");
            if (history == null || !history.IsHooked) _sb.Append("   ·   告警中心未连接");
            SetCached(_sub, _sb.ToString());

            _buffer.Clear();
            if (history != null) history.GetRecent(_buffer, Mathf.Max(1, _maxRows));

            int shown = _buffer.Count;
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
            // 而 DisplayName 已经写成「计算机教室 环境传感器」，两串并排只是噪声，
            // 还会把最长的那几个名字顶出列宽。
            SetCached(row.Name, rec.DisplayName);

            SetCached(row.Status, TwinStatusPalette.Label(rec.PeakStatus));
            row.Status.color = live ? color : TwinStatusPalette.TextSecondary;

            _sb.Length = 0;
            if (live) _sb.Append("进行中 ");
            _sb.Append(TwinAlarmHistory.FormatDuration(rec.Duration(now)));
            SetCached(row.Duration, _sb.ToString());
            row.Duration.color = live ? TwinStatusPalette.TextPrimary : TwinStatusPalette.TextMuted;
        }

        private Row EnsureRow(int index)
        {
            while (_rows.Count <= index)
            {
                int i = _rows.Count;
                float s = _scale;
                float pad = BasePad * s;
                float headerH = BaseHeaderH * s;
                float rowH = BaseRowH * s;
                int fs = Mathf.Max(8, Mathf.RoundToInt(FsRow * s));

                float innerW = _width - pad * 2f;

                // 列宽**从右往左**算：时刻和时长是定宽的（定长字符串），
                // 中间的名称吃掉剩下的全部空间。这样窗口一窄，先被挤的是名称列，
                // 而不是让时长列被推出面板外 —— 那正是第一版压到卡片上的形态。
                float clockX = 20f * s;
                float clockW = 70f * s;
                float nameX = clockX + clockW + 6f * s;

                float durW = Mathf.Min(160f * s, innerW * 0.34f);
                float durX = innerW - durW;

                float statusW = 64f * s;
                float statusX = durX - 6f * s - statusW;

                float nameW = Mathf.Max(40f, statusX - 6f * s - nameX);

                Row row = new Row();

                GameObject go = TwinUIFactory.Node("HistRow" + i, _panel);
                row.Root = (RectTransform)go.transform;
                TwinUIFactory.Place(row.Root, pad, headerH + i * rowH, innerW, rowH);

                row.Bg = TwinUIFactory.Block(go.transform, "Bg", TwinStatusPalette.Transparent, false);
                TwinUIFactory.Stretch(row.Bg.rectTransform);

                float dotSize = 8f * s;
                row.Dot = TwinUIFactory.Block(go.transform, "Dot", TwinStatusPalette.Unknown, false);
                TwinUIFactory.Place(row.Dot.rectTransform, 2f * s, (rowH - dotSize) * 0.5f, dotSize, dotSize);

                row.Clock = TwinUIFactory.Label(go.transform, "Clock", fs, TextAnchor.MiddleLeft,
                    TwinStatusPalette.TextSecondary);
                TwinUIFactory.Place(row.Clock.rectTransform, clockX, 0f, clockW, rowH);

                row.Name = TwinUIFactory.Label(go.transform, "Name", fs, TextAnchor.MiddleLeft,
                    TwinStatusPalette.TextPrimary);
                TwinUIFactory.Place(row.Name.rectTransform, nameX, 0f, nameW, rowH);

                row.Status = TwinUIFactory.Label(go.transform, "Status", fs, TextAnchor.MiddleLeft,
                    TwinStatusPalette.TextSecondary);
                TwinUIFactory.Place(row.Status.rectTransform, statusX, 0f, statusW, rowH);

                row.Duration = TwinUIFactory.Label(go.transform, "Duration", fs, TextAnchor.MiddleRight,
                    TwinStatusPalette.TextMuted);
                TwinUIFactory.Place(row.Duration.rectTransform, durX, 0f, durW, rowH);

                _rows.Add(row);
            }
            return _rows[index];
        }

        private static void SetCached(Text target, string value)
        {
            if (target == null) return;
            if (target.text == value) return;
            target.text = value;
        }
    }
}

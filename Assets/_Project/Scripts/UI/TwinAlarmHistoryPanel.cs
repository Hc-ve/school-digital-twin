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
    /// ── 为什么是「居中浮层」而不是「右栏再加一块」──────────────────
    /// 右栏已经挤满了：详情卡片 + 告警面板在 1000×456 的视口里几乎占满，
    /// 再加一块必然触发垂直上限（_uiScale 早就被压到下限 0.75 了）。
    /// 历史是**按需查看**的信息，不该常驻占位 —— 所以做成浮层，
    /// 开关一次不影响任何现有布局。
    ///
    /// ── 位置为什么用锚点而不是算坐标 ────────────────────────────────
    /// `anchor=0.5 + pivot=0.5` 的语义就是「我的中心对准父节点中心」，
    /// 与父节点多大、屏幕多大完全无关。P4-5 已经证明「按屏幕像素反推坐标」必错，
    /// 这里一开始就不走那条路。
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

        private const int FsTitle = 17;
        private const int FsSub = 12;
        private const int FsRow = 13;

        private float _scale = 1f;
        private bool _visible;
        private float _nextRefresh;

        private RectTransform _panel;
        private Text _title;
        private Text _sub;
        private Text _empty;
        private RectTransform _rowsRoot;
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
        public void Build(RectTransform root, float scale)
        {
            if (root == null) return;

            // 父面板重建 UI 时会把我们连同节点一起销毁，此时缓存的引用是「假 null」
            // （C# 引用非 null，但 Unity 的 == 返回 true）。所以这里必须用 == null 判断。
            if (_panel != null && _scale == scale) return;

            Teardown();
            _scale = scale;
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
            _rowsRoot = null;
            _rows.Clear();
        }

        private void BuildFresh(RectTransform root)
        {
            float s = _scale;
            float w = BaseWidth * s;
            float headerH = BaseHeaderH * s;
            float rowH = BaseRowH * s;
            float bottomPad = BaseBottomPad * s;
            int rows = Mathf.Max(1, _maxRows);

            float h = headerH + rows * rowH + bottomPad;

            _panel = (RectTransform)TwinUIFactory.Node("AlarmHistoryPanel", root).transform;
            TwinUIFactory.AnchorCenter(_panel, 0f, 0f, w, h);

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

            _rowsRoot = _panel;   // 行直接挂在面板下，用绝对坐标摆放

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
            float s = _scale;

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

            for (int i = 0; i < shown; i++) FillRow(i, _buffer[i], now, s);
            for (int i = shown; i < _rows.Count; i++) _rows[i].Root.gameObject.SetActive(false);
        }

        private void FillRow(int index, TwinAlarmHistory.AlarmRecord rec, float now, float s)
        {
            Row row = EnsureRow(index, s);
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

        private Row EnsureRow(int index, float s)
        {
            while (_rows.Count <= index)
            {
                int i = _rows.Count;
                float w = BaseWidth * s;
                float pad = BasePad * s;
                float headerH = BaseHeaderH * s;
                float rowH = BaseRowH * s;
                int fs = Mathf.Max(8, Mathf.RoundToInt(FsRow * s));

                Row row = new Row();

                GameObject go = TwinUIFactory.Node("HistRow" + i, _rowsRoot);
                row.Root = (RectTransform)go.transform;
                TwinUIFactory.Place(row.Root, pad, headerH + i * rowH, w - pad * 2f, rowH);

                row.Bg = TwinUIFactory.Block(go.transform, "Bg", TwinStatusPalette.Transparent, false);
                TwinUIFactory.Stretch(row.Bg.rectTransform);

                float dotSize = 8f * s;
                row.Dot = TwinUIFactory.Block(go.transform, "Dot", TwinStatusPalette.Unknown, false);
                TwinUIFactory.Place(row.Dot.rectTransform, 2f * s, (rowH - dotSize) * 0.5f, dotSize, dotSize);

                // 四列固定宽度：时刻 / 名称 / 状态 / 时长。
                // 用固定列宽而不是自适应，是为了让 8 行上下对齐 —— 时间线一旦错位就没法扫读。
                row.Clock = TwinUIFactory.Label(go.transform, "Clock", fs, TextAnchor.MiddleLeft,
                    TwinStatusPalette.TextSecondary);
                TwinUIFactory.Place(row.Clock.rectTransform, 18f * s, 0f, 58f * s, rowH);

                row.Name = TwinUIFactory.Label(go.transform, "Name", fs, TextAnchor.MiddleLeft,
                    TwinStatusPalette.TextPrimary);
                TwinUIFactory.Place(row.Name.rectTransform, 80f * s, 0f, 310f * s, rowH);

                row.Status = TwinUIFactory.Label(go.transform, "Status", fs, TextAnchor.MiddleLeft,
                    TwinStatusPalette.TextSecondary);
                TwinUIFactory.Place(row.Status.rectTransform, 392f * s, 0f, 70f * s, rowH);

                row.Duration = TwinUIFactory.Label(go.transform, "Duration", fs, TextAnchor.MiddleRight,
                    TwinStatusPalette.TextMuted);
                TwinUIFactory.Place(row.Duration.rectTransform, 464f * s, 0f,
                    BaseWidth * s - pad * 2f - 464f * s, rowH);

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

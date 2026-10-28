using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SchoolTwin.UI
{
    /// <summary>
    /// 【P28】首页图标格：4 列 × 3 行/页 + 底部页码点，参照《异环》的手机式面板。
    ///
    /// ── 为什么是 4×3 而不是列表 ────────────────────────────────────
    /// 列表一屏最多放 5~6 项，功能一多就要滚动；图标格一屏 12 项，5 个模块 + 预留
    /// 一页就够，且"图标 + 短名"扫视效率比列表高（异环、原神、主机游戏的系统菜单都是格）。
    ///
    /// ── 为什么保留页码点 ──────────────────────────────────────────
    /// 现在只有 6 项、一页就够，但需求明确要"预留天气等新增功能"。
    /// 页码点先做出来（1 页时自动隐藏），加第 7 项时不用改结构。
    /// </summary>
    public sealed class TwinModuleGrid : MonoBehaviour
    {
        private const int Columns = 4;
        private const int Rows = 3;
        private const float CellW = 88f;
        private const float CellH = 88f;
        private const float GapX = 12f;
        private const float GapY = 12f;

        private sealed class Entry
        {
            public string Key;
            public string Name;
            public string Glyph;
            public bool Enabled;
            public int Badge;          // >0 显示角标数字
            public Action OnPick;
        }

        private readonly List<Entry> _entries = new List<Entry>(12);
        private readonly List<GameObject> _cells = new List<GameObject>(12);

        private RectTransform _root;
        private RectTransform _dots;
        private int _page;
        private int _pageCount = 1;

        /// <summary>点某个图标时回调（key）。</summary>
        public Action<string> Picked;

        // ---------------------------------------------------------------- 构建

        public static TwinModuleGrid Create(Transform parent)
        {
            GameObject go = new GameObject("ModuleGrid", typeof(RectTransform));
            go.transform.SetParent(parent, false);

            TwinModuleGrid grid = go.AddComponent<TwinModuleGrid>();
            grid._root = go.transform as RectTransform;
            TwinUIFactory.Stretch(grid._root);
            return grid;
        }

        /// <summary>加一个模块入口。</summary>
        public TwinModuleGrid Add(string key, string name, string glyph, bool enabled, Action onPick, int badge = 0)
        {
            Entry e = new Entry();
            e.Key = key;
            e.Name = name;
            e.Glyph = glyph;
            e.Enabled = enabled;
            e.Badge = badge;
            e.OnPick = onPick;
            _entries.Add(e);
            return this;
        }

        /// <summary>所有入口加完后调一次，真正生成格子。</summary>
        public void Build()
        {
            ClearCells();

            _pageCount = Mathf.Max(1, Mathf.CeilToInt(_entries.Count / (float)(Columns * Rows)));
            if (_page > _pageCount - 1) _page = 0;

            int perPage = Columns * Rows;
            int start = _page * perPage;
            int end = Mathf.Min(_entries.Count, start + perPage);

            // 网格整体居中：宽 = 4*88 + 3*12 = 388
            float gridW = Columns * CellW + (Columns - 1) * GapX;
            float gridH = Rows * CellH + (Rows - 1) * GapY;

            // ⚠️ 创建当帧 `rect` 可能还是 0（布局没跑完）—— 那样算出来的
            // origin 是 0，格子会全部挤在左上角。用抽屉首页的已知尺寸兜底。
            float availW = _root.rect.width;
            float availH = _root.rect.height;
            if (availW < 1f) availW = 420f;
            if (availH < 1f) availH = 472f;

            float originX = (availW - gridW) * 0.5f;
            float originY = Mathf.Max(0f, (availH - gridH - 24f) * 0.5f);

            for (int i = start; i < end; i++)
            {
                int local = i - start;
                int col = local % Columns;
                int row = local / Columns;

                float x = originX + col * (CellW + GapX);
                float y = originY + row * (CellH + GapY);

                _cells.Add(BuildCell(_entries[i], x, y));
            }

            BuildDots();
        }

        private GameObject BuildCell(Entry e, float x, float y)
        {
            GameObject go = new GameObject("Cell_" + e.Key, typeof(RectTransform));
            go.transform.SetParent(_root, false);

            RectTransform rt = go.transform as RectTransform;
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = new Vector2(CellW, CellH);
            rt.anchoredPosition = new Vector2(x, -y);

            // 悬停高亮底（默认透明，靠 TwinCellHover 控制）
            TwinRoundedRect bg = TwinUIFactory.Rounded(rt, "Hover", TwinTheme.R12,
                new Color(0f, 0f, 0f, 0f), new Color(0f, 0f, 0f, 0f),
                new Color(0f, 0f, 0f, 0f), 0f, true);
            TwinUIFactory.Stretch(bg.rectTransform);

            Color iconBg = e.Enabled
                ? TwinTheme.WithAlpha(TwinTheme.Accent, 0.14f)
                : TwinTheme.WithAlpha(TwinTheme.TextMuted, 0.10f);
            Color iconFg = e.Enabled ? TwinTheme.Accent : TwinTheme.TextMuted;

            RectTransform icon = TwinIconGlyph.Create(rt, e.Glyph, 44f, iconBg, iconFg, "Icon");
            icon.anchorMin = icon.anchorMax = new Vector2(0.5f, 1f);
            icon.pivot = new Vector2(0.5f, 1f);
            icon.anchoredPosition = new Vector2(0f, -8f);

            Text label = TwinUIFactory.Label(rt, "Name", TwinTheme.FsBody,
                TextAnchor.MiddleCenter, e.Enabled ? TwinTheme.TextPrimary : TwinTheme.TextMuted);
            TwinUIFactory.Place(label.rectTransform, 0f, 58f, CellW, 18f);
            label.text = e.Name;

            // 角标（告警数）
            if (e.Badge > 0)
            {
                TwinRoundedRect badge = TwinUIFactory.Rounded(rt, "Badge", 8f,
                    TwinTheme.StatusAlarm, TwinTheme.StatusAlarm,
                    TwinTheme.WithAlpha(Color.white, 0.75f), 1f, false);
                RectTransform br = badge.rectTransform;
                br.anchorMin = br.anchorMax = new Vector2(0.5f, 1f);
                br.pivot = new Vector2(0.5f, 0.5f);
                br.sizeDelta = new Vector2(e.Badge > 9 ? 22f : 16f, 16f);
                br.anchoredPosition = new Vector2(26f, -6f);

                Text n = TwinUIFactory.Label(br, "N", 10, TextAnchor.MiddleCenter, Color.white);
                TwinUIFactory.Stretch(n.rectTransform);
                n.text = e.Badge > 99 ? "99+" : e.Badge.ToString();
            }

            // 交互：启用才挂
            if (e.Enabled)
            {
                TwinCellClick click = go.AddComponent<TwinCellClick>();
                click.Bind(bg, e.Key, () =>
                {
                    Action a = e.OnPick;
                    if (a != null) a();
                    Action<string> picked = Picked;
                    if (picked != null) picked(e.Key);
                });
            }

            return go;
        }

        // ---------------------------------------------------------------- 页码点

        private void BuildDots()
        {
            if (_dots != null)
            {
                TwinUIFactory.DestroySafe(_dots.gameObject);
                _dots = null;
            }

            // 只有一页就不显示（现在就是这种情况，但结构留着）
            if (_pageCount <= 1) return;

            GameObject go = new GameObject("Dots", typeof(RectTransform));
            go.transform.SetParent(_root, false);
            _dots = go.transform as RectTransform;
            _dots.anchorMin = _dots.anchorMax = new Vector2(0.5f, 0f);
            _dots.pivot = new Vector2(0.5f, 0f);
            _dots.sizeDelta = new Vector2(120f, 12f);
            _dots.anchoredPosition = new Vector2(0f, 12f);

            float totalW = _pageCount * 6f + (_pageCount - 1) * 8f;
            float x = (120f - totalW) * 0.5f;

            for (int i = 0; i < _pageCount; i++)
            {
                bool active = i == _page;
                TwinRoundedRect dot = TwinUIFactory.Rounded(_dots, "Dot" + i, 3f,
                    active ? TwinTheme.Accent : TwinTheme.Track,
                    active ? TwinTheme.Accent : TwinTheme.Track,
                    new Color(0f, 0f, 0f, 0f), 0f, false);

                RectTransform dr = dot.rectTransform;
                dr.anchorMin = dr.anchorMax = new Vector2(0f, 0.5f);
                dr.pivot = new Vector2(0f, 0.5f);
                dr.sizeDelta = new Vector2(active ? 18f : 6f, 6f);
                dr.anchoredPosition = new Vector2(x, 0f);

                x += (active ? 18f : 6f) + 8f;
            }
        }

        // ---------------------------------------------------------------- 翻页

        /// <summary>翻页。delta = +1 下一页 / -1 上一页。返回是否真的翻了。</summary>
        public bool TurnPage(int delta)
        {
            if (_pageCount <= 1) return false;

            int next = Mathf.Clamp(_page + delta, 0, _pageCount - 1);
            if (next == _page) return false;

            _page = next;
            Build();
            return true;
        }

        public void SetAlarmBadge(string key, int count)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                if (!string.Equals(_entries[i].Key, key, StringComparison.Ordinal)) continue;
                if (_entries[i].Badge == count) return;
                _entries[i].Badge = count;
                Build();
                return;
            }
        }

        private void ClearCells()
        {
            for (int i = 0; i < _cells.Count; i++)
                if (_cells[i] != null) TwinUIFactory.DestroySafe(_cells[i]);
            _cells.Clear();
        }
    }

    /// <summary>
    /// 图标格的悬停 + 点击。单独一个组件是因为格子不是一个"按钮"：
    /// 它没有实心底色，悬停只亮一层半透明圆角，点击才触发。
    /// 复用 `TwinButton` 会带进来一套按钮视觉（描边、按下变暗），不合适。
    /// </summary>
    public sealed class TwinCellClick : MonoBehaviour,
        IPointerEnterHandler, IPointerExitHandler, IPointerClickHandler
    {
        private TwinRoundedRect _hover;
        private string _key;
        private Action _onClick;
        private bool _hovering;

        public void Bind(TwinRoundedRect hoverBg, string key, Action onClick)
        {
            _hover = hoverBg;
            _key = key;
            _onClick = onClick;
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            if (_hovering) return;
            _hovering = true;
            if (_hover != null)
            {
                _hover.SetFill(TwinTheme.Hover);
                _hover.SetBorder(TwinTheme.WithAlpha(TwinTheme.Accent, 0.30f), 1f);
            }
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            if (!_hovering) return;
            _hovering = false;
            if (_hover != null)
            {
                Color clear = new Color(0f, 0f, 0f, 0f);
                _hover.SetFill(clear);
                _hover.SetBorder(clear, 0f);
            }
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left) return;
            Action cb = _onClick;
            if (cb != null) cb();
        }
    }
}

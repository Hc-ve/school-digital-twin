using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SchoolTwin.UI
{
    /// <summary>
    /// 【P28】图标格：4 列 × 3 行/页 + 底部页码点，参照《异环》的手机式面板。
    ///
    /// ── 2026-10-29 重写：上一版的格子会跑到抽屉外面 ──────────────────
    /// 上一版用 `_root.rect.width` 算居中：`originX = (availW - gridW) * 0.5f`。
    /// 但 `Build()` 是在页面被 `Stretch` **之前**调的（TwinDrawer 里先 Build 后 Push），
    /// 那时 RectTransform 还是默认尺寸（约 100），于是 originX ≈ -144，
    /// 整组格子被推到抽屉左外侧 —— 就是用户看到的"按钮溢出来了"。
    ///
    /// 现在改成**固定尺寸布局**，一个像素都不从父节点量：
    ///   格子总宽 4×92 + 3×8 = 392，抽屉 420 → 左右各留 14
    ///   格子总高 3×96 + 2×8 = 304
    /// 父容器给死尺寸，格子用绝对坐标从左上角排。
    /// 这样不管什么时候 Build、父节点多宽，位置都不会变。
    ///
    /// ── 视觉：圆角方块而不是正圆 ────────────────────────────────────
    /// 《异环》的功能图标是**圆角方块**（手机 App 图标的感觉），不是圆形。
    /// 圆形在深色底上显得"散"，方块更容易形成整齐的阵列。
    /// </summary>
    public sealed class TwinModuleGrid : MonoBehaviour
    {
        private const int Columns = 4;
        private const int Rows = 3;

        private const float CellW = 92f;
        private const float CellH = 96f;
        private const float GapX = 8f;
        private const float GapY = 8f;
        private const float IconSize = 48f;

        /// <summary>一页的总尺寸（父容器应该给这个大小）。</summary>
        public static Vector2 GridSize
        {
            get
            {
                return new Vector2(
                    Columns * CellW + (Columns - 1) * GapX,
                    Rows * CellH + (Rows - 1) * GapY);
            }
        }

        private sealed class Entry
        {
            public string Key;
            public string Name;
            public string Glyph;
            public bool Enabled;
            public int Badge;
            public Action OnPick;
        }

        private readonly List<Entry> _entries = new List<Entry>(12);
        private readonly List<GameObject> _cells = new List<GameObject>(12);

        private RectTransform _root;
        private RectTransform _dots;
        private int _page;
        private int _pageCount = 1;

        public Action<string> Picked;

        // ---------------------------------------------------------------- 构建

        public static TwinModuleGrid Create(Transform parent)
        {
            GameObject go = new GameObject("ModuleGrid", typeof(RectTransform));
            go.transform.SetParent(parent, false);

            TwinModuleGrid grid = go.AddComponent<TwinModuleGrid>();
            grid._root = go.transform as RectTransform;

            // 固定尺寸 + 居中靠上：父节点多大都不影响内部布局
            Vector2 size = GridSize;
            grid._root.anchorMin = new Vector2(0.5f, 1f);
            grid._root.anchorMax = new Vector2(0.5f, 1f);
            grid._root.pivot = new Vector2(0.5f, 1f);
            grid._root.sizeDelta = size;
            grid._root.anchoredPosition = Vector2.zero;
            return grid;
        }

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

        public void Build()
        {
            ClearCells();

            _pageCount = Mathf.Max(1, Mathf.CeilToInt(_entries.Count / (float)(Columns * Rows)));
            if (_page > _pageCount - 1) _page = 0;

            int perPage = Columns * Rows;
            int start = _page * perPage;
            int count = Mathf.Min(_entries.Count - start, perPage);

            // 逐行排，**不满一行就居中**。
            // 现在 L1 只有 2 个入口，左对齐会显得"缺了一块"；
            // 居中是手机 App 图标网格的标准处理（异环首页不满页时也是居中）。
            for (int r = 0; r < Rows; r++)
            {
                int rowStart = r * Columns;
                if (rowStart >= count) break;

                int inRow = Mathf.Min(Columns, count - rowStart);
                float rowOffset = (Columns - inRow) * (CellW + GapX) * 0.5f;

                for (int c = 0; c < inRow; c++)
                {
                    int i = start + rowStart + c;
                    float x = rowOffset + c * (CellW + GapX);
                    float y = r * (CellH + GapY);
                    _cells.Add(BuildCell(_entries[i], x, y));
                }
            }

            BuildDots();
        }

        private GameObject BuildCell(Entry e, float x, float y)
        {
            GameObject go = new GameObject("Cell_" + e.Key, typeof(RectTransform));
            go.transform.SetParent(_root, false);

            RectTransform rt = go.transform as RectTransform;
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);   // 父节点左上角
            rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = new Vector2(CellW, CellH);
            rt.anchoredPosition = new Vector2(x, -y);

            // 悬停底（默认全透明，TwinCellClick 控制）
            TwinRoundedRect bg = TwinUIFactory.Rounded(rt, "Hover", TwinTheme.R12,
                new Color(0f, 0f, 0f, 0f), new Color(0f, 0f, 0f, 0f),
                new Color(0f, 0f, 0f, 0f), 0f, true);
            TwinUIFactory.Stretch(bg.rectTransform);

            // 图标：圆角方块（手机 App 图标的感觉），不是正圆
            Color iconBg = e.Enabled
                ? TwinTheme.WithAlpha(TwinTheme.Accent, 0.16f)
                : TwinTheme.WithAlpha(TwinTheme.TextMuted, 0.10f);
            Color iconFg = e.Enabled ? TwinTheme.Accent : TwinTheme.TextMuted;

            // 图标底用**上下渐变**（上亮下暗）：纯色方块在深色底上像贴纸，
            // 一点渐变就有亚克力/玻璃的质感，和按钮 Primary 的处理一致。
            TwinRoundedRect icon = TwinUIFactory.Rounded(rt, "Icon", TwinTheme.R12,
                iconBg,
                TwinTheme.WithAlpha(iconFg, e.Enabled ? 0.06f : 0.04f),
                TwinTheme.WithAlpha(iconFg, e.Enabled ? 0.35f : 0.15f), 1f, false);

            RectTransform ir = icon.rectTransform;
            ir.anchorMin = ir.anchorMax = new Vector2(0.5f, 1f);
            ir.pivot = new Vector2(0.5f, 1f);
            ir.sizeDelta = new Vector2(IconSize, IconSize);
            ir.anchoredPosition = new Vector2(0f, -8f);

            Text glyph = TwinUIFactory.Label(ir, "Glyph", 22, TextAnchor.MiddleCenter, iconFg);
            TwinUIFactory.Stretch(glyph.rectTransform);
            glyph.text = e.Glyph;

            // 名称：11 号，压在图标下面
            Text label = TwinUIFactory.Label(rt, "Name", TwinTheme.FsCaption,
                TextAnchor.MiddleCenter, e.Enabled ? TwinTheme.TextSecondary : TwinTheme.TextMuted);
            TwinUIFactory.Place(label.rectTransform, 0f, 64f, CellW, 16f);
            label.text = e.Name;

            // 角标
            if (e.Badge > 0)
            {
                TwinRoundedRect badge = TwinUIFactory.Rounded(rt, "Badge", 8f,
                    TwinTheme.StatusAlarm, TwinTheme.StatusAlarm,
                    TwinTheme.WithAlpha(Color.white, 0.75f), 1f, false);
                RectTransform br = badge.rectTransform;
                br.anchorMin = br.anchorMax = new Vector2(0.5f, 1f);
                br.pivot = new Vector2(0.5f, 0.5f);
                br.sizeDelta = new Vector2(e.Badge > 9 ? 22f : 16f, 16f);
                br.anchoredPosition = new Vector2(30f, -8f);

                Text n = TwinUIFactory.Label(br, "N", 10, TextAnchor.MiddleCenter, Color.white);
                TwinUIFactory.Stretch(n.rectTransform);
                n.text = e.Badge > 99 ? "99+" : e.Badge.ToString();
            }

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

            if (_pageCount <= 1) return;   // 一页就不显示

            GameObject go = new GameObject("Dots", typeof(RectTransform));
            go.transform.SetParent(_root, false);
            _dots = go.transform as RectTransform;
            _dots.anchorMin = new Vector2(0f, 0f);
            _dots.anchorMax = new Vector2(1f, 0f);
            _dots.pivot = new Vector2(0.5f, 0f);
            _dots.sizeDelta = new Vector2(0f, 10f);
            _dots.anchoredPosition = new Vector2(0f, -14f);

            float totalW = _pageCount * 6f + (_pageCount - 1) * 8f;
            float x = (GridSize.x - totalW) * 0.5f;

            for (int i = 0; i < _pageCount; i++)
            {
                bool active = i == _page;
                float w = active ? 18f : 6f;

                TwinRoundedRect dot = TwinUIFactory.Rounded(_dots, "Dot" + i, 3f,
                    active ? TwinTheme.Accent : TwinTheme.Track,
                    active ? TwinTheme.Accent : TwinTheme.Track,
                    new Color(0f, 0f, 0f, 0f), 0f, false);

                RectTransform dr = dot.rectTransform;
                dr.anchorMin = dr.anchorMax = new Vector2(0f, 0.5f);
                dr.pivot = new Vector2(0f, 0.5f);
                dr.sizeDelta = new Vector2(w, 6f);
                dr.anchoredPosition = new Vector2(x, 0f);

                x += w + 8f;
            }
        }

        // ---------------------------------------------------------------- 对外

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
    /// 它没有实心底色，悬停只亮一层半透明圆角。
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

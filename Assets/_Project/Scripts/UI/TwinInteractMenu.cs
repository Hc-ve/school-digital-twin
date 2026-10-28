using System;
using System.Collections.Generic;
using SchoolTwin.Player;
using UnityEngine;
using UnityEngine.UI;

namespace SchoolTwin.UI
{
    /// <summary>
    /// 【P27】统一交互按钮菜单 —— 参照《异环》(NTE) 的交互按钮风格。
    ///
    /// ── 视觉（与参考图一致）─────────────────────────────────────────
    /// 竖排圆角条形按钮；深色半透明底 + 白描边；左侧图标/键位圈；
    /// **选中项高亮**（粉色渐变条），其余保持深灰。
    ///
    /// ── 交互 ────────────────────────────────────────────────────────
    /// · 鼠标滚轮上下切换选中项（面板激活时滚轮归面板，相机缩放暂停）
    /// · E 键确认执行选中项；单条目时 E 直接执行（保持旧版"走近按 E"的手感）
    /// · 鼠标 hover 同步选中；点击按钮仍然可用（鼠标党零学习成本）
    /// · 选中到顶/到底就停（夹边不循环 —— "开机"和"全部关机"贴着会误触）
    ///
    /// ── 架构定位：菜单不懂业务 ──────────────────────────────────────
    /// 本组件**只管画**：显示条目、高亮选中、处理输入、回调。
    /// 业务逻辑全部在"驱动器"（TwinPcMenuDriver / TwinInteractPrompt）里：
    /// 驱动器每帧构造 `Item` 列表调 `Show()`，菜单负责渲染和输入。
    /// **以后新增交互按钮 = 写一个驱动器调 Show()，不改本文件。**
    ///
    /// ── 同帧互斥 ────────────────────────────────────────────────────
    /// 门灯驱动器和主脑驱动器可能同帧都想 Show（理论上不会，但防御一下）。
    /// 规则：同帧第一个 Show 生效，后来者拒收并打 log —— 驱动器之间不用互相感知。
    /// </summary>
    public sealed class TwinInteractMenu : MonoBehaviour
    {
        // ================================================================ 公开类型

        /// <summary>程序化图标种类。全部零资源，用 TwinUIFactory 的 Block/Text 拼。</summary>
        public enum IconKind
        {
            None,
            KeyE,       // 绿色圆 + "E"（旧单按钮的键位提示，保留这个记忆点）
            Power,      // "开" 字圈
            PowerOff,   // "关" 字圈
            Door,       // "门" 字圈
            Screen,     // "投" 字圈（投影/PPT）
            Play,       // "▶"
            Pause,      // 两条竖条
            Prev,       // "‹"
            Next,       // "›"
            Exit,       // "×"
            Letter,     // 首字母/首字圈（用 Item.Letter）
        }

        /// <summary>一条交互按钮。驱动器构造，菜单渲染。</summary>
        public struct Item
        {
            public string Label;      // 按钮文案
            public IconKind Icon;     // 图标类型
            public string Letter;     // Icon == Letter 时圈里的字
            public Action OnClick;    // 回调（点击 / E 确认）
            public bool Enabled;      // false = 灰显不可选（如「正在关机…」）

            public Item(string label, IconKind icon, Action onClick, bool enabled = true)
            {
                Label = label;
                Icon = icon;
                Letter = null;
                OnClick = onClick;
                Enabled = enabled;
            }

            public static Item LetterItem(string label, string letter, IconKind fallback, Action onClick)
            {
                Item it = new Item(label, IconKind.Letter, onClick);
                it.Letter = letter;
                if (string.IsNullOrEmpty(letter)) it.Icon = fallback;
                return it;
            }
        }

        // ================================================================ 状态

        /// <summary>面板当前是否可见 —— 相机滚轮 / E 键仲裁都看这个。</summary>
        public static bool IsBlocking { get; private set; }

        private static TwinInteractMenu _instance;

        /// <summary>自举入口。驱动器不持有引用，直接 TwinInteractMenu.Ensure().Show(...)。</summary>
        public static TwinInteractMenu Ensure()
        {
            if (_instance != null) return _instance;
            Bootstrap();
            return _instance;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_instance != null) return;

            // 场景里可能已经有旧面板建好的 Overlay Canvas，直接挂上去
            Canvas canvas = FindOverlayCanvas();
            GameObject go = new GameObject("TwinInteractMenu", typeof(RectTransform));
            go.transform.SetParent(canvas != null ? canvas.transform : null, false);
            _instance = go.AddComponent<TwinInteractMenu>();
            Debug.Log("[P27] 统一交互菜单已挂载（TwinInteractMenu）");
        }

        private static Canvas FindOverlayCanvas()
        {
            Canvas[] all = FindObjectsOfType<Canvas>(true);
            for (int i = 0; i < all.Length; i++)
                if (all[i] != null && all[i].renderMode == RenderMode.ScreenSpaceOverlay) return all[i];

            // 一个 Overlay Canvas 都没有 → 自己建（沿用 TwinPcMenuPanel 的参数）
            GameObject go = new GameObject("TwinPcCanvas", typeof(RectTransform));
            Canvas canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 96;

            CanvasScaler scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            if (go.GetComponent<GraphicRaycaster>() == null) go.AddComponent<GraphicRaycaster>();
            return canvas;
        }

        // ---- 布局常量（沿用 TwinPcMenuPanel 的视觉规范）----
        private const float BtnW = 220f;
        private const float BtnH = 44f;
        private const float BtnGap = 10f;
        private const int FontSize = 15;

        // ---- 颜色：未选中（旧规范）与选中（异环式粉渐变）----
        private static readonly Color IdleTop = new Color(0.13f, 0.14f, 0.16f, 0.92f);
        private static readonly Color IdleBottom = new Color(0.13f, 0.14f, 0.16f, 0.92f);
        private static readonly Color IdleBorder = new Color(1f, 1f, 1f, 0.22f);
        private static readonly Color SelTop = new Color(0.90f, 0.30f, 0.45f, 0.95f);
        private static readonly Color SelBottom = new Color(0.75f, 0.18f, 0.35f, 0.95f);
        private static readonly Color SelBorder = new Color(1f, 1f, 1f, 0.85f);
        private static readonly Color IconGreen = new Color(0.16f, 0.72f, 0.52f, 1f);
        private static readonly Color IconIdle = new Color(0.30f, 0.33f, 0.38f, 1f);
        private static readonly Color TextColor = Color.white;

        // ---- 运行期 ----
        private RectTransform _root;
        private CanvasGroup _group;
        private Canvas _canvas;

        /// <summary>一排按钮的渲染对象。对象池：条目数不变时复用，不重建。</summary>
        private readonly List<Row> _rows = new List<Row>(6);

        private sealed class Row
        {
            public GameObject Go;
            public TwinRoundedRect Bg;
            public Text Label;
            public Image IconCircle;      // 圆形图标底（Block）
            public Text IconText;         // 圈里的字/符号
            public GameObject IconPauseA; // Pause 图标的两条竖条
            public GameObject IconPauseB;
            public Button Btn;
        }

        private bool _visible;              // 本帧是否应该显示
        private bool _shownThisFrame;       // 同帧互斥标记
        private int _lastFrame;             // 上次 Show 的 Time.frameCount
        private float _lastRejectLog;       // 互斥日志节流
        private Vector2 _anchor = new Vector2(0.26f, 0.62f);
        private readonly List<Item> _items = new List<Item>(8);
        private int _selectedIndex;

        // 输入
        private float _wheelAccum;
        private const float WheelStep = 0.12f;   // 一格 notch ≈ ±0.1，高分滚轮是小步进，累积过阈值才算切一格
        private float _nextUse;
        private const float UseCooldown = 0.35f;

        // 淡入淡出
        private float _alpha;
        private float _alphaTarget;

        // ================================================================ 公开 API

        /// <summary>
        /// 显示按钮列表。驱动器**每帧调用**等价于刷新 —— 菜单内部用对象池，
        /// 条目数不变时只改文案/选中色，不重建 GameObject。
        /// 同帧第二次 Show 会被拒（两个驱动器抢面板时保持先到先得）。
        /// </summary>
        public void Show(IReadOnlyList<Item> items, Vector2 screenAnchor)
        {
            if (items == null || items.Count == 0) { Hide(); return; }

            // ── 同帧互斥 ──
            // 两个驱动器同帧都想显示时，先到先得。
            // 【为什么只警告不报错】站在主脑和门中间时两个都在范围内，
            // 这是**正常的**（不是 bug），只是面板一次只能显示一组。
            // 日志做节流，否则每帧刷一条把 Console 冲爆。
            if (_shownThisFrame && _lastFrame == Time.frameCount)
            {
                if (Time.unscaledTime - _lastRejectLog > 2f)
                {
                    _lastRejectLog = Time.unscaledTime;
                    Debug.Log("[P27] 两个驱动器同时在范围内，面板显示先到的那一组（正常，非错误）");
                }
                return;
            }
            _shownThisFrame = true;
            _lastFrame = Time.frameCount;

            EnsureBuilt();

            // 条目数变化时把选中项夹回有效范围；内容变化不重置（滚轮连续切换时列表
            // 因为页码变化每帧都不同 —— 如果重置选中项，滚轮永远停在第 0 条）
            if (_selectedIndex >= items.Count) _selectedIndex = 0;

            bool rebuild = _items.Count != items.Count || !_visible;
            _items.Clear();
            _items.AddRange(items);
            _anchor = screenAnchor;

            if (rebuild) RebuildRows();
            else RefreshRows();

            ApplyAnchor();
            _visible = true;
            _alphaTarget = 1f;
            IsBlocking = true;
            // 角色降显不在这里做 —— 交给 LateUpdate 按"面板矩形 vs 角色屏幕包围盒"
            // 是否相交决定（见 TwinPlayerFade.Update）。面板没压住人就别淡。
        }

        /// <summary>
        /// 请求隐藏面板。
        ///
        /// ── 为什么只是"请求"而不是立即收起 ──────────────────────────────
        /// 面板有**两个驱动器**（主脑 TwinPcMenuDriver、门灯 TwinInteractPrompt），
        /// 各自在自己的 Update 里调 Show / Hide，而 Unity **不保证**两者的执行顺序。
        ///
        /// 2026-10-28 用户报"大部分交互按钮丢失"就是栽在这：
        /// ```
        /// TwinInteractPrompt.Update  → 门在范围内 → Show(单按钮)   面板显示
        /// TwinPcMenuDriver.Update    → 主脑不在范围 → Hide()       面板被关掉
        /// ```
        /// 一帧之内自己人打自己人，最后谁跑在后面谁说了算 —— 表现为按钮闪烁/丢失。
        ///
        /// 所以 Hide 只表示"我这边不需要面板了"，真正的裁决放在 LateUpdate：
        /// **本帧只要有任何一个驱动器 Show 过，面板就显示**；全都没有才收起。
        /// 这样多驱动器天然共存，驱动器之间也不需要互相知道对方存在。
        /// </summary>
        public void Hide()
        {
            // 故意留空：见上面的说明。真正的收起在 LateUpdate 里按"本帧有没有 Show"裁决。
        }

        // ================================================================ 生命周期

        private void Awake()
        {
            _root = transform as RectTransform;
            _canvas = GetComponentInParent<Canvas>();
            _group = gameObject.AddComponent<CanvasGroup>();
            _group.alpha = 0f;
            _group.blocksRaycasts = false;
            _group.interactable = false;
        }

        private void LateUpdate()
        {
            // ── 可见性裁决 ──
            // 驱动器必须在 Update 里**每帧**调一次 Show 或 Hide（二选一）。
            // 本帧有 Show → 显示；一个 Show 都没有 → 收起（Hide 调用者/驱动器被销毁
            // 都属于这一类）。这样两个驱动器交替 Update 也不会互相覆盖。
            bool shownThisFrame = (_lastFrame == Time.frameCount);

            if (!shownThisFrame && _visible)
            {
                _visible = false;
                _alphaTarget = 0f;
                IsBlocking = false;
            }

            _shownThisFrame = false;

            // ── 角色降显：只在面板**真的压在角色身上**时才淡 ──
            // 第一版是"面板一出现就淡"，用户实测"一靠近门就隐身"——
            // 面板在屏幕右侧、角色在中间，根本没重叠。现在按屏幕矩形相交判断。
            TwinPlayerFade.Update(_visible, GetScreenRect());

            TickFade();

            if (!_visible || _group.alpha < 0.6f) return;   // 淡出过半就不吃输入

            HandleInput();
        }

        /// <summary>
        /// 面板在屏幕上的像素矩形（左下原点，与 Input.mousePosition 同一坐标系）。
        /// Overlay Canvas 的 world corners 就是屏幕像素，不需要转换。
        /// </summary>
        private Rect GetScreenRect()
        {
            if (_root == null) return new Rect(0f, 0f, 0f, 0f);

            _cornerBuf[0] = Vector3.zero;
            _root.GetWorldCorners(_cornerBuf);      // 0=左下 1=左上 2=右上 3=右下
            return Rect.MinMaxRect(
                Mathf.Min(_cornerBuf[0].x, _cornerBuf[2].x), Mathf.Min(_cornerBuf[0].y, _cornerBuf[2].y),
                Mathf.Max(_cornerBuf[0].x, _cornerBuf[2].x), Mathf.Max(_cornerBuf[0].y, _cornerBuf[2].y));
        }

        /// <summary>GetWorldCorners 的复用缓冲（每帧 new Vector3[4] 会持续产生 GC）。</summary>
        private readonly Vector3[] _cornerBuf = new Vector3[4];

        // ================================================================ 输入

        private void HandleInput()
        {
            int count = _items.Count;
            if (count == 0) return;

            // ── hover = 选中（异环行为：鼠标指到哪条，哪条亮）──
            for (int i = 0; i < _rows.Count && i < count; i++)
            {
                Row r = _rows[i];
                if (r == null || r.Bg == null) continue;
                if (!_items[i].Enabled) continue;             // 灰显条不吃 hover
                if (RectTransformUtility.RectangleContainsScreenPoint(
                        r.Bg.rectTransform, Input.mousePosition, _canvas != null ? _canvas.worldCamera : null))
                {
                    if (_selectedIndex != i) SetSelected(i);
                }
            }

            // ── 滚轮切换（累积阈值，防高分滚轮一格跳多档）──
            // 面板激活（IsBlocking）时相机脚本会跳过缩放，滚轮独占归这里。
            float scroll = Input.mouseScrollDelta.y;
            if (Mathf.Abs(scroll) > 0.0001f)
            {
                _wheelAccum += scroll;
                if (_wheelAccum > WheelStep) { MoveSelect(+1); _wheelAccum = 0f; }
                else if (_wheelAccum < -WheelStep) { MoveSelect(-1); _wheelAccum = 0f; }
            }
            else
            {
                _wheelAccum = 0f;   // 没有滚动就清零，防半格残留漂移
            }

            // ── E 确认 ──
            // 单条目：E = 直接执行（老玩家"走近按 E"的手感不变）。
            // 多条目：E = 执行当前选中项。
            // 「正在关机…」这类 Enabled=false 的条目不响应。
            // 【P28】模态打开时 E 归模态（确认/取消），不执行交互
            if (Input.GetKeyDown(KeyCode.E) && Time.unscaledTime >= _nextUse
                && !TwinUIState.BlocksHotkeys)
            {
                Item it = _items[_selectedIndex];
                if (it.Enabled && it.OnClick != null)
                {
                    _nextUse = Time.unscaledTime + UseCooldown;
                    Debug.Log("[P27] E 确认 → " + it.Label);
                    it.OnClick();
                }
            }
        }

        private void MoveSelect(int delta)
        {
            int count = _items.Count;
            // 跳过灰显条；到顶/到底就停（不循环）
            int next = _selectedIndex;
            for (int step = 0; step < count; step++)
            {
                next += delta;
                if (next < 0 || next >= count) return;        // 夹边
                if (_items[next].Enabled) { SetSelected(next); return; }
            }
        }

        private void SetSelected(int index)
        {
            if (index == _selectedIndex) return;
            int old = _selectedIndex;
            _selectedIndex = Mathf.Clamp(index, 0, _items.Count - 1);
            ApplyRowStyle(old);
            ApplyRowStyle(_selectedIndex);
        }

        // ================================================================ 行构建 / 刷新

        private void EnsureBuilt()
        {
            if (_root != null) return;
            _root = transform as RectTransform;
            _canvas = GetComponentInParent<Canvas>();
        }

        /// <summary>屏幕归一化锚点 → 定位。pivot=(0,1)：锚点是列表**左上角**。</summary>
        private void ApplyAnchor()
        {
            _root.anchorMin = _root.anchorMax = _anchor;
            _root.pivot = new Vector2(0f, 1f);
            _root.anchoredPosition = Vector2.zero;

            // 根节点必须有真实尺寸 —— 否则 `GetWorldCorners` 返回的是个零面积矩形，
            // 角色降显的"矩形相交"判断永远为 false（面板压在角色身上也不淡）。
            // 高度 = 条目数 × (行高 + 间距)，行本身是绝对定位的子节点。
            int n = Mathf.Max(1, _items.Count);
            _root.sizeDelta = new Vector2(BtnW, n * (BtnH + BtnGap));
        }

        /// <summary>条目数变了（或首次显示）→ 重建所有行。</summary>
        private void RebuildRows()
        {
            ClearRows();

            for (int i = 0; i < _items.Count; i++)
                _rows.Add(BuildRow(i));

            ApplyAnchor();
            RefreshRows();
        }

        private Row BuildRow(int index)
        {
            Row row = new Row();

            // 底板：胶囊圆角（radius = 半高），选中态靠换色实现
            row.Bg = TwinUIFactory.Rounded(_root, "Row" + index, BtnH * 0.5f,
                IdleTop, IdleBottom, IdleBorder, 1f, true);
            RectTransform rt = row.Bg.rectTransform;
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = new Vector2(BtnW, BtnH);
            rt.anchoredPosition = new Vector2(0f, -index * (BtnH + BtnGap));
            row.Go = row.Bg.gameObject;

            // 图标圈：24×24 圆，挂在**行**节点下（不是菜单根），x=20 居中在行左端。
            // 注意：行节点锚点是 (0,1)/pivot=(0,1)，圈用 anchor (0,0.5) 挂在行的左边缘垂直居中。
            row.IconCircle = TwinUIFactory.Block(rt, "Icon", IconIdle, false);
            RectTransform ir = row.IconCircle.rectTransform;
            ir.anchorMin = ir.anchorMax = new Vector2(0f, 0.5f);
            ir.pivot = new Vector2(0.5f, 0.5f);
            ir.sizeDelta = new Vector2(24f, 24f);
            ir.anchoredPosition = new Vector2(20f, 0f);

            row.IconText = TwinUIFactory.Label(ir, "Glyph", 13, TextAnchor.MiddleCenter, Color.white);
            TwinUIFactory.Stretch(row.IconText.rectTransform);

            // Pause 图标的两条竖条（按需显示）。
            // Place 的 y 是"从父节点顶部往下量"，所以竖条要 y = (24-12)/2 = 6 才垂直居中。
            row.IconPauseA = TwinUIFactory.Block(ir, "BarA", Color.white, false).gameObject;
            RectTransform pa = row.IconPauseA.GetComponent<RectTransform>();
            TwinUIFactory.Place(pa, 7f, 6f, 3f, 12f);

            row.IconPauseB = TwinUIFactory.Block(ir, "BarB", Color.white, false).gameObject;
            RectTransform pb = row.IconPauseB.GetComponent<RectTransform>();
            TwinUIFactory.Place(pb, 14f, 6f, 3f, 12f);

            // 文案
            row.Label = TwinUIFactory.Label(rt, "Label", FontSize, TextAnchor.MiddleLeft, TextColor);
            TwinUIFactory.Place(row.Label.rectTransform, 38f, 0f, BtnW - 46f, BtnH);

            // 点击（鼠标党路径）
            row.Btn = row.Go.GetComponent<Button>();
            if (row.Btn == null) row.Btn = row.Go.AddComponent<Button>();
            row.Btn.transition = Selectable.Transition.None;
            int captured = index;   // 闭包捕获
            row.Btn.onClick.AddListener(() =>
            {
                if (captured < 0 || captured >= _items.Count) return;
                if (!_items[captured].Enabled) return;
                if (Time.unscaledTime < _nextUse) return;
                _nextUse = Time.unscaledTime + UseCooldown;
                Action cb = _items[captured].OnClick;
                if (cb != null) cb();
            });

            return row;
        }

        /// <summary>条目数没变 → 只刷文案、图标、选中色。每帧调用，代价≈0。</summary>
        private void RefreshRows()
        {
            for (int i = 0; i < _rows.Count; i++)
            {
                if (i >= _items.Count) break;
                Row r = _rows[i];
                Item it = _items[i];

                if (!string.Equals(r.Label.text, it.Label, StringComparison.Ordinal))
                    r.Label.text = it.Label;

                ApplyRowIcon(r, it);
                ApplyRowStyle(i);
            }
        }

        private void ApplyRowIcon(Row r, Item it)
        {
            string glyph = null;
            Color circleColor = IconIdle;

            switch (it.Icon)
            {
                case IconKind.KeyE:
                    glyph = "E";
                    circleColor = IconGreen;
                    break;
                case IconKind.Power: glyph = "开"; break;
                case IconKind.PowerOff: glyph = "关"; break;
                case IconKind.Door: glyph = "门"; break;
                case IconKind.Screen: glyph = "投"; break;
                case IconKind.Letter: glyph = string.IsNullOrEmpty(it.Letter) ? "·" : it.Letter; break;
                case IconKind.Prev: glyph = "‹"; break;
                case IconKind.Next: glyph = "›"; break;
                case IconKind.Exit: glyph = "×"; break;
                case IconKind.Play: glyph = "▶"; break;
                case IconKind.Pause:
                    // 两条竖条 = 暂停符号，圈里不放字
                    glyph = null;
                    break;
                default:
                    glyph = null;   // None：空圈
                    break;
            }

            r.IconCircle.color = circleColor;

            // 【为什么全部要判"变了才写"】这些方法每帧都会被 RefreshRows 调一次。
            // UGUI 的 Text.text / Graphic.color 是**无条件标脏**的 —— 每帧赋值会让
            // Canvas 每帧重建，轻则掉帧，重则因为重建时序出现闪烁。
            bool showBars = it.Icon == IconKind.Pause;
            if (r.IconPauseA != null && r.IconPauseA.activeSelf != showBars)
                r.IconPauseA.SetActive(showBars);
            if (r.IconPauseB != null && r.IconPauseB.activeSelf != showBars)
                r.IconPauseB.SetActive(showBars);

            if (r.IconText != null)
            {
                string want = glyph ?? "";
                if (!string.Equals(r.IconText.text, want, StringComparison.Ordinal))
                    r.IconText.text = want;

                bool textVisible = !showBars;
                if (r.IconText.gameObject.activeSelf != textVisible)
                    r.IconText.gameObject.SetActive(textVisible);
            }
        }

        /// <summary>按"是否选中"刷一条的底色/描边。切换选中时只动两行，代价≈0。</summary>
        private void ApplyRowStyle(int index)
        {
            if (index < 0 || index >= _rows.Count || index >= _items.Count) return;
            Row r = _rows[index];
            Item it = _items[index];

            if (!it.Enabled)
            {
                // 灰显：底更暗、字压暗
                r.Bg.SetFill(new Color(0.10f, 0.11f, 0.12f, 0.55f));
                r.Bg.SetBorder(new Color(1f, 1f, 1f, 0.10f), 1f);
                if (r.Label != null) r.Label.color = new Color(1f, 1f, 1f, 0.45f);
                if (r.IconCircle != null) r.IconCircle.color = new Color(0.30f, 0.33f, 0.38f, 0.45f);
                return;
            }

            if (index == _selectedIndex)
            {
                r.Bg.SetFill(SelTop, SelBottom);            // 粉色渐变（异环式）
                r.Bg.SetBorder(SelBorder, 1.5f);
                if (r.Label != null) r.Label.color = Color.white;
            }
            else
            {
                r.Bg.SetFill(IdleTop, IdleBottom);
                r.Bg.SetBorder(IdleBorder, 1f);
                if (r.Label != null) r.Label.color = TextColor;
            }
        }

        private void ClearRows()
        {
            for (int i = 0; i < _rows.Count; i++)
                if (_rows[i] != null && _rows[i].Go != null)
                    TwinUIFactory.DestroySafe(_rows[i].Go);
            _rows.Clear();
        }

        // ================================================================ 淡入淡出

        private void TickFade()
        {
            if (_group == null) return;
            _alpha = Mathf.MoveTowards(_alpha, _alphaTarget, Time.unscaledDeltaTime * 9f);
            _group.alpha = _alpha;

            bool live = _alphaTarget > 0.5f;
            _group.blocksRaycasts = live;
            _group.interactable = live;
        }
    }
}

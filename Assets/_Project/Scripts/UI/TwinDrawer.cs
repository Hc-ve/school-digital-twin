using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using SchoolTwin.Core;      // TwinStatus 在 Core，不在 Twin（踩过：CS0246/CS0103）
using SchoolTwin.Twin;

namespace SchoolTwin.UI
{
    /// <summary>
    /// 【P28】收纳式总控抽屉 —— 整个系统的唯一入口。
    ///
    /// ── 结构（参照《异环》手机式面板）────────────────────────────────
    /// ```
    /// ┌──────────────────────────────┐  贴屏幕右缘，只有左侧圆角
    /// │ ‹ 返回        校园总控    × │  头部 96
    /// │ 1号教学楼 · 在线 53 / 告警 2 │
    /// │ [在线 53][告警 2][受控 38]   │
    /// ├──────────────────────────────┤
    /// │                              │
    /// │        页栈（首页/模块页）    │  内容区
    /// │                              │
    /// ├──────────────────────────────┤
    /// │ 数据源   录制   帮助   设置   │  工具行 56
    /// └──────────────────────────────┘
    /// ```
    ///
    /// ── 为什么宽度会变（420 ↔ 640）──────────────────────────────────
    /// 首页是 4 列图标格，420 够用；模块页有表格/图表，420 太窄。
    /// 变宽而不是"固定 640"：抽屉开着时还要能看 3D 场景，
    /// 参考分辨率只有 1000 宽，常驻 640 会吃掉 2/3 画面。
    ///
    /// ── 为什么没有全屏遮罩（scrim）──────────────────────────────────
    /// 常规抽屉点外部要关闭。但这里抽屉开着时用户的主要动作是
    /// **点场景里的设备 → 详情卡更新**，加遮罩等于把这个动作废掉。
    /// 所以只挡滚轮和移动，不挡拾取（见 `TwinUIState.BlocksPick`）。
    /// </summary>
    public sealed class TwinDrawer : MonoBehaviour
    {
        public const float WidthHome = 420f;
        public const float WidthModule = 640f;
        private const float HeightRef = 720f;
        private const float HeaderH = 96f;
        private const float ToolRowH = 56f;

        private static TwinDrawer _instance;

        // ---- 结构 ----
        private RectTransform _root;        // 抽屉本体（右缘锚定）
        private RectTransform _content;     // 页栈容器
        private TwinPageStack _stack;
        private TwinModuleGrid _homeGrid;

        private Text _title;
        private Text _subtitle;
        private TwinButton _backBtn;
        private TwinChip _chipOnline;
        private TwinChip _chipAlarm;
        private TwinChip _chipScope;

        private TwinTriggerButton _trigger;

        // ---- 状态 ----
        private bool _open;
        private float _widthCur = WidthHome;
        private float _widthTarget = WidthHome;
        private float _slide;               // 0 = 完全收起（贴到屏幕外），1 = 完全展开
        private float _slideTarget;

        private float _nextSummaryAt;
        private int _lastAlarmBadge = -1;

        public static bool IsOpen { get { return _instance != null && _instance._open; } }

        // ================================================================ 自举

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_instance != null) return;

            Canvas canvas = FindOverlayCanvas();
            if (canvas == null) return;

            GameObject go = new GameObject("TwinDrawer", typeof(RectTransform));
            go.transform.SetParent(canvas.transform, false);
            _instance = go.AddComponent<TwinDrawer>();
            _instance.Build();
            Debug.Log("[P28] 总控抽屉已挂载（右缘触发钮 / Esc 逐层退）");
        }

        /// <summary>
        /// 找主 Overlay Canvas。场景里是 `[TwinUI]`（1000×720 match=0）。
        /// **不要自建 1920×1080 的 Canvas** —— 会和主画布两套缩放，观感对不上。
        /// </summary>
        private static Canvas FindOverlayCanvas()
        {
            Canvas[] all = FindObjectsOfType<Canvas>(true);
            for (int i = 0; i < all.Length; i++)
                if (all[i] != null && all[i].renderMode == RenderMode.ScreenSpaceOverlay) return all[i];
            return null;
        }

        // ================================================================ 构建

        private void Build()
        {
            _root = transform as RectTransform;
            _root.anchorMin = new Vector2(1f, 0f);
            _root.anchorMax = new Vector2(1f, 1f);
            _root.pivot = new Vector2(1f, 0.5f);
            _root.sizeDelta = new Vector2(WidthHome, 0f);
            _root.anchoredPosition = new Vector2(WidthHome, 0f);   // 初始完全收在屏幕外

            BuildShell();
            BuildHeader();
            BuildContent();
            BuildToolRow();

            // 触发钮是抽屉的兄弟节点（抽屉收起时它要还在）
            _trigger = TwinTriggerButton.Create(_root.parent, Toggle);

            // 轻提示：工具行/模块点击的反馈都走它。
            // 必须在这里 Ensure —— `TwinToast.Show` 在实例为空时是静默 no-op，
            // 忘了建就会"点了没反应"且没有任何报错。
            TwinToast.Ensure(_root.parent);

            // 首页
            BuildHomePage();

            _escapeHook = OnEscape;                 // 存成字段，OnDestroy 里好比对
            TwinUIState.EscapeRouter = _escapeHook;
            TwinUIState.Set(TwinUILayer.None);
        }

        /// <summary>Esc 路由的委托缓存。直接比 `== OnEscape` 依赖委托相等语义，容易踩坑。</summary>
        private Func<bool> _escapeHook;

        private void BuildShell()
        {
            // 抽屉底：只有左侧圆角（右侧贴边）
            TwinRoundedRect bg = TwinUIFactory.Rounded(_root, "Bg", TwinTheme.R16,
                TwinTheme.ScrimBase, TwinTheme.ScrimBase,
                TwinTheme.WithAlpha(TwinTheme.Accent, 0.22f), 1f, true);
            TwinUIFactory.Stretch(bg.rectTransform);
        }

        private void BuildHeader()
        {
            RectTransform header = NewNode("Header", _root);
            TwinUIFactory.Place(header, 0f, 0f, 0f, HeaderH);
            header.anchorMin = new Vector2(0f, 1f);
            header.anchorMax = new Vector2(1f, 1f);
            header.pivot = new Vector2(0f, 1f);
            header.sizeDelta = new Vector2(0f, HeaderH);

            // 返回键（栈深 > 1 才显示）
            _backBtn = TwinButton.Create(header, "‹", TwinButtonStyle.Ghost, 28f, 28f, OnBackClicked, "Back");
            TwinUIFactory.Place(_backBtn.Rect, 12f, 12f, 28f, 28f);
            _backBtn.gameObject.SetActive(false);

            _title = TwinUIFactory.Label(header, "Title", TwinTheme.FsTitle,
                TextAnchor.UpperLeft, TwinTheme.TextPrimary);
            TwinUIFactory.Place(_title.rectTransform, 16f, 12f, 300f, 28f);
            _title.text = "校园总控";

            _subtitle = TwinUIFactory.Label(header, "Subtitle", TwinTheme.FsBody,
                TextAnchor.UpperLeft, TwinTheme.TextSecondary);
            TwinUIFactory.Place(_subtitle.rectTransform, 16f, 42f, 320f, 18f);
            _subtitle.text = "1号教学楼";

            // 摘要胶囊（0.5s 刷新）
            _chipOnline = TwinChip.Create(header, "在线 0", TwinTheme.StatusNormal, "ChipOnline");
            TwinUIFactory.Place(_chipOnline.Rect, 16f, 66f, 70f, TwinChip.Height);

            _chipAlarm = TwinChip.Create(header, "告警 0", TwinTheme.StatusAlarm, "ChipAlarm");
            TwinUIFactory.Place(_chipAlarm.Rect, 92f, 66f, 70f, TwinChip.Height);

            _chipScope = TwinChip.Create(header, "受控 0", TwinTheme.Accent, "ChipScope");
            TwinUIFactory.Place(_chipScope.Rect, 168f, 66f, 70f, TwinChip.Height);

            // 关闭
            TwinButton close = TwinButton.Create(header, "×", TwinButtonStyle.Ghost, 28f, 28f, Close, "Close");
            TwinUIFactory.AnchorTopRight(close.Rect, 12f, 12f, 28f, 28f);

            // 分隔线
            Image line = TwinUIFactory.Block(_root, "HeaderLine", TwinTheme.Divider, false);
            RectTransform lr = line.rectTransform;
            lr.anchorMin = new Vector2(0f, 1f);
            lr.anchorMax = new Vector2(1f, 1f);
            lr.pivot = new Vector2(0f, 1f);
            lr.sizeDelta = new Vector2(0f, 1f);
            lr.anchoredPosition = new Vector2(0f, -HeaderH);
        }

        private void BuildContent()
        {
            GameObject go = new GameObject("Content", typeof(RectTransform));
            go.transform.SetParent(_root, false);

            _content = go.transform as RectTransform;
            _content.anchorMin = new Vector2(0f, 0f);
            _content.anchorMax = new Vector2(1f, 1f);
            _content.pivot = new Vector2(0f, 1f);
            _content.offsetMin = new Vector2(0f, ToolRowH);      // 下边留出工具行
            _content.offsetMax = new Vector2(0f, -HeaderH);      // 上边留出头部

            _stack = TwinPageStack.Create(_content);
            _stack.Changed += OnStackChanged;
        }

        private void BuildToolRow()
        {
            RectTransform row = NewNode("ToolRow", _root);
            row.anchorMin = new Vector2(0f, 0f);
            row.anchorMax = new Vector2(1f, 0f);
            row.pivot = new Vector2(0f, 0f);
            row.sizeDelta = new Vector2(0f, ToolRowH);
            row.anchoredPosition = Vector2.zero;

            Image line = TwinUIFactory.Block(row, "Line", TwinTheme.Divider, false);
            RectTransform lr = line.rectTransform;
            lr.anchorMin = new Vector2(0f, 1f);
            lr.anchorMax = new Vector2(1f, 1f);
            lr.pivot = new Vector2(0f, 1f);
            lr.sizeDelta = new Vector2(0f, 1f);
            lr.anchoredPosition = Vector2.zero;

            // 工具行：第 3 个是"旧面板"——**迁移期的临时入口**。
            // 旧面板的快捷键（F4/G/T/H）在 P1 已停用，但新模块页要到 P2~P4 才陆续接上，
            // 这段时间如果完全打不开旧总控，用户就没法控设备、没法演示。
            // 所以留一个口子：功能照旧，入口收敛到抽屉里。P6 删掉这个按钮。
            string[] names = { "数据源", "录制", "旧面板", "帮助" };
            const float btnW = 72f;
            float x = 0f;   // 左侧起排，右对齐留白 —— 抽屉宽度会变，用左对齐更稳

            for (int i = 0; i < names.Length; i++)
            {
                string n = names[i];
                Action act;
                if (string.Equals(n, "旧面板", StringComparison.Ordinal)) act = ToggleLegacyPanel;
                else act = () => TwinToast.Show(n + "：P2 接入");

                TwinButton b = TwinButton.Create(row, n, TwinButtonStyle.Ghost, btnW, 36f,
                    act, "Tool_" + n);
                TwinUIFactory.Place(b.Rect, x + 8f, 10f, btnW, 36f);
                x += btnW;
            }
        }

        /// <summary>迁移期临时入口：直接开旧的总控面板（不经过它自己的快捷键）。</summary>
        private void ToggleLegacyPanel()
        {
            TwinControlPanel legacy = FindObjectOfType<TwinControlPanel>(true);
            if (legacy == null)
            {
                TwinToast.Show("旧总控面板未创建（TwinDetailPanel 未初始化）");
                return;
            }
            legacy.Toggle();
            TwinToast.Show("旧面板是迁移期临时入口，P6 移除");
        }

        // ================================================================ 首页

        private void BuildHomePage()
        {
            GameObject go = new GameObject("HomePage", typeof(RectTransform));
            go.transform.SetParent(_content, false);

            TwinHomePage page = go.AddComponent<TwinHomePage>();
            page.BuildModules((key) =>
            {
                // P1 只做骨架：点模块先给提示，P2 起逐个换成真实页面
                TwinToast.Show("「" + TwinHomePage.NameOf(key) + "」将在 P2~P5 接入");
            });

            _homeGrid = page.Grid;
            _homePage = page;
            _stack.Push(page);
        }

        // ================================================================ 开 / 关

        public void Toggle()
        {
            if (_open) Close();
            else Open();
        }

        public void Open()
        {
            if (_open) return;
            _open = true;
            _slideTarget = 1f;
            _widthTarget = WidthHome;

            TwinUIState.Set(TwinUILayer.Drawer);
            if (_trigger != null) _trigger.SetOpen(true);
            RefreshSummary(true);
        }

        public void Close()
        {
            if (!_open) return;
            _open = false;
            _slideTarget = 0f;

            // 关抽屉 = 清栈回首页（下次打开从首页开始，不会停在上次的深层页）
            if (_stack != null) _stack.Clear();
            if (_homeGrid != null) _stack.Push(FindHomePage());

            TwinUIState.Reset();
            if (_trigger != null) _trigger.SetOpen(false);
        }

        private TwinPageBase _homePage;
        private TwinPageBase FindHomePage()
        {
            if (_homePage == null)
            {
                TwinHomePage p = _content.GetComponentInChildren<TwinHomePage>(true);
                if (p != null) _homePage = p;
            }
            return _homePage;
        }

        private void OnBackClicked()
        {
            if (_stack == null) return;
            if (_stack.Depth > 1) _stack.Pop();
            else Close();
        }

        private void OnStackChanged()
        {
            if (_stack == null) return;

            TwinPageBase top = _stack.Top;
            bool isHome = top is TwinHomePage;

            if (_title != null)
            {
                string t = top != null ? top.Title : "校园总控";
                if (!string.Equals(_title.text, t, StringComparison.Ordinal)) _title.text = t;
            }
            if (_subtitle != null)
            {
                string s = top != null ? (top.Subtitle ?? "1号教学楼") : "1号教学楼";
                if (!string.Equals(_subtitle.text, s, StringComparison.Ordinal)) _subtitle.text = s;
            }
            if (_backBtn != null && _backBtn.gameObject.activeSelf == isHome)
                _backBtn.gameObject.SetActive(!isHome);

            // 首页 420，模块页 640
            _widthTarget = isHome ? WidthHome : WidthModule;
        }

        // ================================================================ Esc

        private bool OnEscape()
        {
            if (!_open) return false;

            // 先退一层页；已经在首页就收抽屉
            if (_stack != null && _stack.Depth > 1)
            {
                _stack.Pop();
                return true;
            }

            Close();
            return true;
        }

        // ================================================================ 每帧

        private void Update()
        {
            // 滑动
            if (Mathf.Abs(_slide - _slideTarget) > 0.001f)
            {
                float speed = 1f / (_slideTarget > _slide ? TwinTheme.DrawerIn : TwinTheme.DrawerOut);
                _slide = Mathf.MoveTowards(_slide, _slideTarget, Time.unscaledDeltaTime * speed);
            }

            // 宽度（形变用 easeInOut，但这里用 MoveTowards 近似，避免再存一个 tween 起点）
            if (Mathf.Abs(_widthCur - _widthTarget) > 0.5f)
            {
                float wSpeed = (WidthModule - WidthHome) / TwinTheme.Mid;
                _widthCur = Mathf.MoveTowards(_widthCur, _widthTarget, Time.unscaledDeltaTime * wSpeed);
            }
            else if (Mathf.Abs(_widthCur - _widthTarget) > 0.001f)
            {
                _widthCur = _widthTarget;
            }

            // 收起时 x 向右推到屏幕外（pivot 在右边，x 越大越靠外）
            float hiddenX = _widthCur;
            _root.sizeDelta = new Vector2(_widthCur, 0f);
            _root.anchoredPosition = new Vector2(Mathf.Lerp(hiddenX, 0f, TwinTheme.EaseOutCubic(_slide)), 0f);

            // 摘要 0.5s 刷新（只在打开时）
            if (_open && Time.unscaledTime >= _nextSummaryAt)
            {
                _nextSummaryAt = Time.unscaledTime + 0.5f;
                RefreshSummary(false);
            }
        }

        // ================================================================ 摘要

        private void RefreshSummary(bool force)
        {
            TwinRuntime rt = TwinRuntime.Instance;
            if (rt == null || rt.Registry == null) return;

            int online = 0, alarm = 0, total = 0;
            IReadOnlyList<TwinAnchor> all = rt.Registry.All;
            if (all != null)
            {
                for (int i = 0; i < all.Count; i++)
                {
                    TwinAnchor a = all[i];
                    if (a == null) continue;
                    total++;
                    TwinStatus s = a.StatusOfWorst();
                    if (s == TwinStatus.Alarm || s == TwinStatus.Warning) alarm++;
                    if (s != TwinStatus.Offline && s != TwinStatus.Stale) online++;
                }
            }

            if (_chipOnline != null) _chipOnline.Set("在线 " + online, TwinTheme.StatusNormal);
            if (_chipAlarm != null) _chipAlarm.Set("告警 " + alarm, alarm > 0 ? TwinTheme.StatusAlarm : TwinTheme.StatusNormal);
            if (_chipScope != null) _chipScope.Set("受控 " + TwinControlScope.Count, TwinTheme.Accent);

            // 触发钮角标
            if (_trigger != null && (force || alarm != _lastAlarmBadge))
            {
                _lastAlarmBadge = alarm;
                _trigger.SetAlarmCount(alarm);
            }

            if (force) _nextSummaryAt = Time.unscaledTime + 0.5f;
        }

        // ================================================================ 工具

        private static RectTransform NewNode(string name, Transform parent)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return go.transform as RectTransform;
        }

        private void OnDestroy()
        {
            if (_escapeHook != null && TwinUIState.EscapeRouter == _escapeHook)
                TwinUIState.EscapeRouter = null;
            if (_instance == this) _instance = null;
        }
    }
}

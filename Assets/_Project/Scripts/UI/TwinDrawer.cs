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

        /// <summary>
        /// 抽屉设计高度。**96 + 544 + 80 = 720** 这条不变量（P29-3.5 规格 §1.2 I1）的唯一依据。
        /// 它不是"参考分辨率的高度"这么简单 —— 它是内容区 544 的分母，必须写死。
        /// </summary>
        public const float HeightRef = 720f;

        private const float HeaderH = 96f;

        /// <summary>
        /// 工具行高。§4.1 给了两个坐标：分隔线 y=640、文字 y=672 —— 720−640=80，
        /// 所以行高必须是 80（文字落在行内 32 处），不是文档表头写的 56。
        /// 按 56 建的话分隔线会跑到内容区里悬空 24px，一眼就错。
        /// </summary>
        private const float ToolRowH = 80f;

        /// <summary>面板左右内边距。分隔线、顶部高光、摘要右对齐都按它算。</summary>
        private const float SidePad = 16f;

        private static TwinDrawer _instance;

        // ---- 结构 ----
        private RectTransform _root;        // 抽屉本体（右缘锚定）
        private RectTransform _content;     // 页栈容器
        private TwinPageStack _stack;

        private Text _title;
        private Text _subtitle;
        private Image _titleBar;            // 标题左侧 3×22 主色小竖条（L1 才有）

        /// <summary>头部容器。最大化时要跟着内容一起内缩（见 ApplyMaximizedLayout）。</summary>
        private RectTransform _header;

        /// <summary>最大化按钮的字形（□ / ▣）。切换时直接改 text，不引新贴图。</summary>
        private Text _maxGlyph;

        /// <summary>是否处于最大化（铺满画布宽度）。</summary>
        private bool _maximized;

        /// <summary>面板底。最大化时要把它拉实（见 ApplyMaximizedLayout），所以要存引用。</summary>
        private TwinRoundedRect _bg;

        /// <summary>全屏态的不透明实底（垫在 Bg 之下，窄态关闭）。</summary>
        private Image _backdrop;

        /// <summary>内容区当前的水平内缩（最大化时用来居中，非最大化恒为 0）。</summary>
        private float _contentPad = -1f;

        private TwinButton _backBtn;

        // ---- 头部右侧摘要（替代原来的三颗胶囊）----
        private Text _summary;
        private TwinRoundedRect _summaryDot;
        private int _sumOnline;
        private int _sumAlarm;
        private int _sumScope;

        // ---- 工具行 ----
        private Text[] _toolLabels;
        private GameObject[] _toolUnderlineObjects;
        private int _toolSelected = -1;

        /// <summary>工具行四项，顺序固定。改动顺序会破坏 L1/L2 的逐像素一致。</summary>
        private static readonly string[] _toolNames = { "数据源", "录制", "帮助", "设置" };

        /// <summary>四项的绝对 x（§4.1）。间距 96，从左边距 34 起排。</summary>
        private static readonly float[] ToolXs = { 34f, 130f, 226f, 322f };

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

        /// <summary>
        /// 【2026-10-29 修】自举**不能假设画布已经存在**。
        ///
        /// 主画布 `TwinPcCanvas` 是 `TwinInteractMenu` 的自举建的，而两个自举都是
        /// `AfterSceneLoad` —— **Unity 不保证同一批 RuntimeInitializeOnLoadMethod 的执行顺序**，
        /// 它按编译产物的类型顺序跑。P29-3 新增两个文件后顺序一变，本类的自举就排到了前面，
        /// `FindOverlayCanvas()` 返回 null、直接 return —— 结果整个抽屉（连同触发钮）根本没建出来，
        /// 表现为"进 Play 后右缘没有入口"。这种错**不报任何异常**，最难查。
        ///
        /// 所以改成：拿不到画布就挂一个重试器，逐帧等到画布出现（最多 5 秒）。
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_instance != null) return;
            if (TryBuild()) return;

            GameObject runner = new GameObject("TwinDrawerBootstrap");
            UnityEngine.Object.DontDestroyOnLoad(runner);   // 全限定：文件里有 using System，Object 会歧义
            runner.AddComponent<BootstrapRetry>();
        }

        /// <summary>画布就绪就建抽屉。返回 false 表示画布还没出来，调用方该等下一帧。</summary>
        private static bool TryBuild()
        {
            if (_instance != null) return true;

            Canvas canvas = FindOverlayCanvas();
            if (canvas == null) return false;

            GameObject go = new GameObject("TwinDrawer", typeof(RectTransform));
            go.transform.SetParent(canvas.transform, false);
            _instance = go.AddComponent<TwinDrawer>();
            _instance.Build();
            Debug.Log("[P28] 总控抽屉已挂载（右缘触发钮 / Esc 逐层退）");
            return true;
        }

        /// <summary>等画布的自举重试器：最多等 5 秒，超时就明确报一条警告（别再静默失败）。</summary>
        private sealed class BootstrapRetry : MonoBehaviour
        {
            private float _deadline;

            private void Awake()
            {
                _deadline = Time.realtimeSinceStartup + 5f;
            }

            private void Update()
            {
                if (TryBuild())
                {
                    Destroy(gameObject);
                    return;
                }

                if (Time.realtimeSinceStartup >= _deadline)
                {
                    Debug.LogWarning("[P28] 5 秒内没找到 ScreenSpaceOverlay 画布，总控抽屉未创建。" +
                                     "检查 TwinInteractMenu 的自举是否被异常打断。");
                    Destroy(gameObject);
                }
            }
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

            // ── 抽屉高度：跟随画布（已回退 P29-3.5 的「锁死 720」）──────────────────
            //
            // 【为什么撤销】上一版把高度写死 720 并垂直居中，理由是「保证内容区稳定拿到 544」。
            // 实测 1069×654 窗口下画布只有 1004×614 —— 720 高的抽屉上下各溢 53px，
            // 结果**头部 96 被裁掉上半（标题、关闭 × 看不见）、工具行 80 被裁掉下半
            // （数据源/录制/帮助/设置 四项看不见）**。主理人 play 实测确认。
            //
            // 结论：抽屉高度必须跟随画布。画布高 < 720 时内容区会不足 544，
            // 那是 CanvasScaler（match=0 只按宽度缩放）带来的 R7 问题，
            // 要根治得改 CanvasScaler 的 match 或整体缩放 —— 属打包阶段的多分辨率适配，
            // 不能靠在 UI 里写死 720 来绕。
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
            // 抽屉底：PanelTop → PanelBottom（§4.1）。纯单色会像"贴了张纸"，
            // 一点纵向渐变才有"浮在场景上"的层次。
            //
            // 只留左侧圆角：TwinRoundedRect 只支持统一半径，而抽屉右缘永远贴着屏幕右缘，
            // 所以把底往右多铺 16 —— 右侧两个圆角被推到画布外，看得见的只有左边的圆角。
            // 这比新写一个"四角不同半径"的 Graphic 便宜得多，也不用改 TwinRoundedRect。
            // 【P29-4 P0-2】raycast 必须为 true：抽屉底要挡住鼠标，
            // 否则点击会穿透面板打到场景里的物体（实测"能透过面板点选设备"）。
            // 背景是父节点，子元素按钮在它之上，UGUI 按层级从前往后判定，不会被它吃掉。
            _bg = TwinUIFactory.Rounded(_root, "Bg", TwinTheme.R16,
                TwinTheme.PanelTop, TwinTheme.PanelBottom, TwinTheme.Border, TwinTheme.StrokeHair.Width, true);
            RectTransform br = _bg.rectTransform;
            br.anchorMin = Vector2.zero;
            br.anchorMax = Vector2.one;
            br.pivot = new Vector2(0.5f, 0.5f);
            br.offsetMin = Vector2.zero;
            br.offsetMax = new Vector2(TwinTheme.R16, 0f);

            // 全屏态的实底兜底（默认关闭）。
            //
            // 为什么需要：PanelTop/PanelBottom 的 alpha 是 0.94/0.96，420~640 宽时只盖住
            // 右缘一条，透出去的 6% 落在暗处看不出来；一点最大化铺满整屏，整片明亮走廊
            // 都会从那 6% 里透出来，观感就是"面板是透明的"。
            // 这里铺一块不透明的同色矩形垫在 Bg 之下，**只在最大化时启用** ——
            // 窄态保持原样（玻璃感不动），全屏态才做实。
            // 四边内缩 R16 是为了不破坏 Bg 的圆角轮廓。
            Image backdrop = TwinUIFactory.Block(_root, "Backdrop", Opaque(TwinTheme.PanelBottom), false);
            RectTransform bk = backdrop.rectTransform;
            bk.anchorMin = Vector2.zero;
            bk.anchorMax = Vector2.one;
            bk.pivot = new Vector2(0.5f, 0.5f);
            bk.offsetMin = new Vector2(TwinTheme.R16, TwinTheme.R16);
            bk.offsetMax = new Vector2(-TwinTheme.R16, -TwinTheme.R16);
            backdrop.transform.SetSiblingIndex(0);      // 压到 Bg 之下
            _backdrop = backdrop;
            _backdrop.gameObject.SetActive(false);

            // 24 单位网格底纹。**必须紧跟背景、在头部/内容/工具行之前**：
            // ApplyGrid 不抢 sibling 位置（抢了会在"背景也是子节点"的面板里被压到底下），
            // 它按调用顺序入栈，所以后建的内容才压在网格之上。
            TwinPaint.ApplyGrid(_root);

            // 左缘 2px 主色竖条：面板从屏幕边缘"长出来"的暗示，也是深色面板与深色场景之间
            // 唯一的分界线。圆角 2 —— 2px 宽的条加圆角就是个胶囊端头，硬切角会显得"刀切"。
            TwinRoundedRect edge = TwinUIFactory.Rounded(_root, "AccentEdge", 2f,
                TwinTheme.BarAccent.Color, TwinTheme.BarAccent.Color, Color.clear, 0f, false);
            RectTransform er = edge.rectTransform;
            er.anchorMin = new Vector2(0f, 0f);
            er.anchorMax = new Vector2(0f, 1f);
            er.pivot = new Vector2(0f, 0.5f);
            er.sizeDelta = new Vector2(TwinTheme.BarAccent.Width, 0f);
            er.anchoredPosition = Vector2.zero;

            // 顶部 1px 高光：只铺在左右内边距之间（不压到左侧色条，也不顶到右边缘）
            Image top = TwinUIFactory.Block(_root, "TopGlow", TwinTheme.TopHighlight, false);
            RectTransform tr = top.rectTransform;
            tr.anchorMin = new Vector2(0f, 1f);
            tr.anchorMax = new Vector2(1f, 1f);
            tr.pivot = new Vector2(0.5f, 1f);
            tr.sizeDelta = new Vector2(-SidePad * 2f, 1f);
            tr.anchoredPosition = new Vector2(0f, -1f);
        }

        /// <summary>
        /// 头部 96 高，L1 / L2 共用这一个构建函数（差别只在三个位置：色条 / 返回键 / 标题 x）。
        /// 同一份代码出两套头部，才谈得上"L1 与 L2 逐像素一致"。
        /// </summary>
        private void BuildHeader()
        {
            RectTransform header = NewNode("Header", _root);
            header.anchorMin = new Vector2(0f, 1f);
            header.anchorMax = new Vector2(1f, 1f);
            header.pivot = new Vector2(0f, 1f);
            header.sizeDelta = new Vector2(0f, HeaderH);
            header.anchoredPosition = Vector2.zero;
            _header = header;

            // 标题左侧 3×22 主色小竖条（§4.1：x=20，y=24–46）。L2 让位给返回键。
            Image bar = TwinUIFactory.Block(header, "TitleBar", TwinTheme.Accent, false);
            TwinUIFactory.Place(bar.rectTransform, 20f, 24f, 3f, 22f);
            _titleBar = bar;

            // 返回 ‹：文档给的是 14×16（x=22，y=34）。这里做成 28×28 的透明点击区、
            // 字形仍落在 x≈22–36 —— 14×16 在 1000×720 的参考分辨率下只有 3.5mm，太难点。
            _backBtn = TwinButton.Create(header, "‹", TwinButtonStyle.Ghost, 28f, 28f, OnBackClicked, "Back");
            TwinUIFactory.Place(_backBtn.Rect, 15f, 20f, 28f, 28f);
            _backBtn.gameObject.SetActive(false);

            // 标题 22 白，纵向中心落在 y=34
            _title = TwinUIFactory.Label(header, "Title", TwinTheme.FsTitle,
                TextAnchor.MiddleLeft, TwinTheme.TextPrimary);
            TwinUIFactory.Place(_title.rectTransform, 32f, 20f, 300f, 28f);
            _title.text = "校园总控";

            // 副标 11 TextMuted，y=62
            _subtitle = TwinUIFactory.Label(header, "Subtitle", TwinTheme.FsCaption,
                TextAnchor.UpperLeft, TwinTheme.TextMuted);
            TwinUIFactory.Place(_subtitle.rectTransform, 32f, 62f, 260f, 16f);
            _subtitle.text = "1号教学楼";

            // 右侧摘要：5px 色点 + 一行 11px（替代原来的三颗胶囊）。
            // 位置由 LayoutSummary() 现算 —— 文案长度会变，右对齐不能用固定坐标。
            _summaryDot = TwinUIFactory.Rounded(header, "SummaryDot", 2.5f,
                TwinTheme.StatusNormal, TwinTheme.StatusNormal, Color.clear, 0f, false);
            _summary = TwinUIFactory.Label(header, "Summary", TwinTheme.FsCaption,
                TextAnchor.MiddleRight, TwinTheme.TextSecondary);
            _summary.text = "";
            LayoutSummary();

            // 关闭 ×：文档 16×16（右 24，y=34），同样放大到 28×28 的点击区
            TwinButton close = TwinButton.Create(header, "×", TwinButtonStyle.Ghost, 28f, 28f, Close, "Close");
            TwinUIFactory.AnchorTopRight(close.Rect, 18f, 20f, 28f, 28f);

            // 最大化 □（P29-4 C 方案）：只加这一个。
            // **不加「最小化 —」** —— 它的行为和关闭 × 完全重叠，两个入口做一件事只会让人分不清。
            // 位置在 × 左边 8px（18 + 28 + 8 = 54），同样 28×28 命中区。
            GameObject maxGo = TwinUIFactory.Node("MaxBtn", header);
            RectTransform maxRt = (RectTransform)maxGo.transform;
            TwinUIFactory.AnchorTopRight(maxRt, 54f, 20f, 28f, 28f);

            Image maxHit = TwinUIFactory.Block(maxRt, "Hit", new Color(0f, 0f, 0f, 0f), true);
            TwinUIFactory.Stretch(maxHit.rectTransform);

            Button maxBtn = maxGo.AddComponent<Button>();
            maxBtn.targetGraphic = maxHit;
            maxBtn.transition = Selectable.Transition.None;
            maxBtn.onClick.AddListener(ToggleMaximize);

            _maxGlyph = TwinUIFactory.Label(maxRt, "Glyph", TwinTheme.FsBody,
                TextAnchor.MiddleCenter, TwinTheme.TextMuted);
            TwinUIFactory.Stretch(_maxGlyph.rectTransform);
            _maxGlyph.text = "□";
            _maxGlyph.raycastTarget = false;

            // 分隔线 1px（§4.1：y=88，左右内边距 16）
            Image line = TwinUIFactory.Block(_root, "HeaderLine", TwinTheme.Divider, false);
            RectTransform lr = line.rectTransform;
            lr.anchorMin = new Vector2(0f, 1f);
            lr.anchorMax = new Vector2(1f, 1f);
            lr.pivot = new Vector2(0.5f, 1f);
            lr.sizeDelta = new Vector2(-SidePad * 2f, 1f);
            lr.anchoredPosition = new Vector2(0f, -88f);
        }

        /// <summary>
        /// 摘要那一行的排版（P29-3.5 规格三）：**只显示「当前页没有用 KPI 卡表达过」的全局数字**。
        ///
        /// 逐项判定，不做整行开关：
        ///   L1 总控       在线/告警已有 KPI 卡，受控本页不出现  → 整行隐藏（右侧只留 ×）
        ///   L2 数据监测    KPI 2×2 已有在线/告警                → 只剩「受控 N」
        ///   L2 告警中心    页内自己表达告警总数                  → 「在线 / 受控」
        ///   其余 L2 页     三项都没有                           → 三项齐全
        ///
        /// 上一版是「L1 隐藏、L2 全显」的整行开关，于是数据监测页把 KPI 卡上的
        /// 在线/告警又用 11px 小字复读了一遍 —— 这正是规格 §3 要消掉的重复。
        /// </summary>
        private void LayoutSummary()
        {
            if (_summary == null || _summaryDot == null) return;

            bool showOnline, showAlarm, showScope;
            ResolveSummaryItems(out showOnline, out showAlarm, out showScope);

            string text = BuildSummaryText(showOnline, showAlarm, showScope, false);
            bool visible = text.Length > 0;

            // 摘要与色点必须一起收 —— 只收文字会留一个孤零零的绿点
            if (_summary.gameObject.activeSelf != visible) _summary.gameObject.SetActive(visible);
            if (_summaryDot.gameObject.activeSelf != visible) _summaryDot.gameObject.SetActive(visible);
            if (!visible) return;

            // 右缘必须让开关闭键。
            //
            // 【为什么是 58 不是规格写的 54】规格 §3.3 同时给了两个约束：
            //   ①「右对齐 width−54」 ②「与 × 间距 ≥12px（× 占 380–396）」。
            // 这两个在代码里对不上：× 的**点击区**是 28×28（AnchorTopRight 18 → 右端 width−18、
            // 左端 width−46），比规格假设的 16×16 更宽。按 54 排，摘要右端在 width−54、
            // × 左端在 width−46，间距只有 8px —— 不满足 ②。
            // ① 是排版值、② 是硬指标，冲突时保 ②：rightInset = 46 + 12 = 58。
            const float rightInset = 58f;

            // 可用宽度 = 抽屉宽 − 右缘让位 − 左边留白（标题区最多占到 332）
            float avail = _widthCur - rightInset - 332f;

            _summary.text = text;
            float w = _summary.preferredWidth;

            // 放不下就换紧凑文案（`受控 38` → `38 受控`），**不换行、不缩字号**（规格 §3.3）
            if (w > avail)
            {
                _summary.text = BuildSummaryText(showOnline, showAlarm, showScope, true);
                w = _summary.preferredWidth;
            }

            RectTransform tr = _summary.rectTransform;
            tr.anchorMin = new Vector2(1f, 1f);
            tr.anchorMax = new Vector2(1f, 1f);
            tr.pivot = new Vector2(1f, 1f);
            tr.sizeDelta = new Vector2(w + 2f, 16f);
            tr.anchoredPosition = new Vector2(-rightInset, -26f);

            RectTransform dr = _summaryDot.rectTransform;
            dr.anchorMin = new Vector2(1f, 1f);
            dr.anchorMax = new Vector2(1f, 1f);
            dr.pivot = new Vector2(0.5f, 0.5f);
            dr.sizeDelta = new Vector2(5f, 5f);
            dr.anchoredPosition = new Vector2(-(rightInset + w + 8.5f), -34f);
        }

        /// <summary>按「当前页」决定摘要里哪几项该出现。三项全 false 即整行隐藏。</summary>
        private void ResolveSummaryItems(out bool showOnline, out bool showAlarm, out bool showScope)
        {
            // Registry 未就绪（启动首帧）→ 整行隐藏，不显示「在线 0」这种假数（规格 §2.4）
            if (TwinDeviceStats.ActiveDeviceCount <= 0)
            {
                showOnline = false;
                showAlarm = false;
                showScope = false;
                return;
            }

            TwinPageBase top = _stack != null ? _stack.Top : null;

            if (top is TwinHomePage)
            {
                // L1：在线/告警已在三张 KPI 卡上；受控本页不出现 → 整行隐藏
                showOnline = false;
                showAlarm = false;
                showScope = false;
                return;
            }

            TwinSystemPage sys = top as TwinSystemPage;
            string key = sys != null ? sys.CurrentModuleKey : null;

            if (string.Equals(key, "monitor", StringComparison.Ordinal))
            {
                // 数据监测：KPI 2×2 已表达在线/告警，只剩受控
                showOnline = false;
                showAlarm = false;
                showScope = true;
                return;
            }

            if (string.Equals(key, "alarm", StringComparison.Ordinal))
            {
                // 告警中心：页内自己表达告警总数，摘要不再重复
                showOnline = true;
                showAlarm = false;
                showScope = true;
                return;
            }

            // 设备管理 / 电力管理 / 数据统计 / 天气（预留）
            showOnline = true;
            showAlarm = true;
            showScope = true;
        }

        /// <summary>
        /// 拼摘要文案。分隔符固定 ` / `（前后各 1 个半角空格），顺序固定 在线 → 告警 → 受控。
        /// <paramref name="compact"/> = true 时用「N 在线」的紧凑写法。
        /// </summary>
        private string BuildSummaryText(bool online, bool alarm, bool scope, bool compact)
        {
            string text = "";

            if (online) text += compact ? _sumOnline + " 在线" : "在线 " + _sumOnline;
            if (alarm) text += (text.Length > 0 ? " / " : "") + (compact ? _sumAlarm + " 告警" : "告警 " + _sumAlarm);
            if (scope) text += (text.Length > 0 ? " / " : "") + (compact ? _sumScope + " 受控" : "受控 " + _sumScope);

            return text;
        }

        /// <summary>
        /// 副标同步。L1 的副标含设备总数（`已接入 N 个点位`），会随数据变化 ——
        /// 跟着摘要一起 0.5s 对一次，变了才写。
        /// </summary>
        private void SyncSubtitle()
        {
            if (_subtitle == null || _stack == null) return;

            TwinPageBase top = _stack.Top;
            string text = top != null ? (top.Subtitle ?? "1号教学楼") : "1号教学楼";
            if (!string.Equals(_subtitle.text, text, StringComparison.Ordinal)) _subtitle.text = text;
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
            // 高度恒为 720 − 80 − 96 = 544。这是内容区硬预算，页面根不得再设固定高度。

            // 【P29-3.5 规格一 A2】裁剪加在 _content，**不加 _root**。
            // 加在 root 会把主入口的双层发光描边（外层向外扩 3px）一起裁掉，发光就没了；
            // 加在 content 正好把越界内容切在工具行之上 ——
            // 这是"就算哪天又算错了，也绝不会出现内容压在工具行上"的兜底。
            go.AddComponent<RectMask2D>();

            _stack = TwinPageStack.Create(_content);
            _stack.Changed += OnStackChanged;
        }

        /// <summary>
        /// 工具行：高 80（640→720），四项 x=34/130/226/322、文字 y=672、字号 11。
        /// L1 与 L2 用同一个函数、同一组绝对 x —— 抽屉变宽时工具行不动，这是"逐像素一致"的前提。
        /// 「旧面板」不再占位，收进「设置」（迁移期点设置直接开旧面板，P6 换成正式设置页）。
        /// </summary>
        private void BuildToolRow()
        {
            RectTransform row = NewNode("ToolRow", _root);
            row.anchorMin = new Vector2(0f, 0f);
            row.anchorMax = new Vector2(1f, 0f);
            row.pivot = new Vector2(0f, 0f);
            row.sizeDelta = new Vector2(0f, ToolRowH);
            row.anchoredPosition = Vector2.zero;

            // 顶部分隔线（左右内边距 16，和头部那条一致）
            Image line = TwinUIFactory.Block(row, "Line", TwinTheme.Divider, false);
            RectTransform lr = line.rectTransform;
            lr.anchorMin = new Vector2(0f, 1f);
            lr.anchorMax = new Vector2(1f, 1f);
            lr.pivot = new Vector2(0.5f, 1f);
            lr.sizeDelta = new Vector2(-SidePad * 2f, 1f);
            lr.anchoredPosition = Vector2.zero;

            _toolLabels = new Text[_toolNames.Length];
            _toolUnderlineObjects = new GameObject[_toolNames.Length];

            for (int i = 0; i < _toolNames.Length; i++)
            {
                int index = i;   // 闭包捕获循环变量：不落局部变量会全部拿到最后一个
                float x = ToolXs[i];

                // 透明命中区（28 高、96 宽）：文字只有 11px 高，不加这个点起来很别扭
                Image hit = TwinUIFactory.Block(row, "Hit_" + _toolNames[i], Color.clear, true);
                RectTransform hr = hit.rectTransform;
                TwinUIFactory.Place(hr, x - 8f, 16f, 96f, 40f);

                Button btn = hit.gameObject.AddComponent<Button>();
                btn.targetGraphic = hit;
                btn.transition = Selectable.Transition.None;   // 悬停/按下自己画，不用 ColorTint
                btn.onClick.AddListener(() => OnToolClicked(index));

                Text label = TwinUIFactory.Label(hr, "Label", TwinTheme.FsCaption,
                    TextAnchor.MiddleLeft, TwinTheme.TextMuted);
                TwinUIFactory.Place(label.rectTransform, 8f, 8f, 80f, 16f);
                label.text = _toolNames[i];
                label.raycastTarget = false;

                // 选中下划线：宽 28、高 2、圆角 1（2px 高 + 半径 1 = 胶囊端头）
                // 【P29-3 补丁 A5】压在文字基线下方 6px（抽屉 y≈678）：
                // 原来在 684，离字太远，看着像"另一行元素"而不是下划线。
                TwinRoundedRect ul = TwinUIFactory.Rounded(hr, "Underline", 1f,
                    TwinTheme.Accent, TwinTheme.Accent, Color.clear, 0f, false);
                TwinUIFactory.Place(ul.rectTransform, 8f, 22f, 28f, 2f);
                ul.gameObject.SetActive(false);

                _toolLabels[i] = label;
                _toolUnderlineObjects[i] = ul.gameObject;
            }

            SelectTool(0);
        }

        private void OnToolClicked(int index)
        {
            SelectTool(index);
            if (index == 3)
            {
                // 迁移期：「设置」暂时映射到旧总控面板（原「旧面板」按钮的位置让出来了）
                ToggleLegacyPanel();
                return;
            }
            TwinToast.Show(_toolNames[index] + "：P2 接入");
        }

        private void SelectTool(int index)
        {
            if (_toolLabels == null) return;
            _toolSelected = index;
            for (int i = 0; i < _toolLabels.Length; i++)
            {
                bool on = i == index;
                if (_toolLabels[i] != null) _toolLabels[i].color = on ? TwinTheme.Accent : TwinTheme.TextMuted;
                if (_toolUnderlineObjects[i] != null) _toolUnderlineObjects[i].SetActive(on);
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

        // ================================================================ 页面

        private void BuildHomePage()
        {
            GameObject go = new GameObject("HomePage", typeof(RectTransform));
            go.transform.SetParent(_content, false);

            TwinHomePage page = go.AddComponent<TwinHomePage>();
            page.OnOpenSystem = OpenSystemPage;

            _homePage = page;
            _stack.Push(page);   // Push 里会 Stretch；页面内容在 OnShow 才构建
        }

        /// <summary>L1 → L2：打开「校园管理系统」面板。</summary>
        private void OpenSystemPage()
        {
            TwinSystemPage page = GetComponentInChildren<TwinSystemPage>(true);
            if (page == null)
            {
                GameObject go = new GameObject("SystemPage", typeof(RectTransform));
                go.transform.SetParent(_content, false);
                page = go.AddComponent<TwinSystemPage>();
                page.OnModulePicked = (key) => TwinToast.Show("「" + ModuleName(key) + "」在 P2~P5 接入");
            }
            _stack.Push(page);
        }

        private static string ModuleName(string key)
        {
            switch (key)
            {
                case "monitor": return "数据监测";
                case "device": return "设备管理";
                case "power": return "电力管理";
                case "alarm": return "告警中心";
                case "stats": return "数据统计";
                default: return key;
            }
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
            _widthTarget = ResolveWidthTarget();

            TwinUIState.Set(TwinUILayer.Drawer);
            RefreshSummary(true);

            // 触发钮和抽屉右上角的关闭键位置重叠（都在右上角），
            // 抽屉打开时把钮收起来 —— 关闭由抽屉自己的 × 和 Esc 负责。
            // 《异环》也是面板展开后入口图标被面板盖住。
            if (_trigger != null) _trigger.gameObject.SetActive(false);

            // 注意：场景层详情卡的收起**不在这里做** ——
            // 改由 TwinDetailPanel.SyncCardVisibility() 每帧按 TwinUIState.Current 自检。
            // 放在抽屉里会有两个问题：① 依赖 FindObjectOfType 找到对方；
            // ② 抽屉 Close 时的「还原」会和自检打架。见 P29-4 P0-1。
        }

        // 场景层 UI 的收起统一交给 TwinDetailPanel.SyncCardVisibility()（按 TwinUIState.Current 自检），
        // 抽屉这边不再插手 —— 双头管理会互相打架。见 P29-4 P0-1。

        // ================================================================ 最大化

        /// <summary>画布逻辑宽度 —— 最大化时的目标宽度。</summary>
        private float CanvasWidth()
        {
            RectTransform parent = _root != null ? _root.parent as RectTransform : null;
            float w = parent != null ? parent.rect.width : 0f;
            return w > 1f ? w : WidthModule;
        }

        /// <summary>
        /// 当前应该用多宽：**最大化优先**，否则按页（首页 420 / 模块页 640）。
        ///
        /// 收成一处是为了避免「切页时把最大化宽度冲掉」—— 原来 `Open()` 和
        /// `OnStackChanged()` 各写一遍 `_widthTarget = ...`，加最大化后必须都走这里判。
        /// </summary>
        private float ResolveWidthTarget()
        {
            return _maximized ? CanvasWidth() : DesignWidth();
        }

        /// <summary>当前页的设计宽度（首页 420 / 模块页 640）。最大化时内容仍按它居中。</summary>
        private float DesignWidth()
        {
            bool isHome = _stack == null || _stack.Top == null || _stack.Top is TwinHomePage;
            return isHome ? WidthHome : WidthModule;
        }

        /// <summary>切最大化：展开铺满画布，再点还原。</summary>
        private void ToggleMaximize()
        {
            _maximized = !_maximized;
            _widthTarget = ResolveWidthTarget();

            if (_maxGlyph != null) _maxGlyph.text = _maximized ? "▣" : "□";
        }

        public void Close()
        {
            if (!_open) return;
            _open = false;
            _slideTarget = 0f;

            // 关抽屉后回到普通宽度 —— 下次打开不该还是最大化状态
            if (_maximized)
            {
                _maximized = false;
                if (_maxGlyph != null) _maxGlyph.text = "□";
            }

            // 关抽屉 = 清栈回首页（下次打开从首页开始，不会停在上次的深层页）
            if (_stack != null)
            {
                _stack.Clear();
                if (_homePage != null) _stack.Push(_homePage);
            }

            TwinUIState.Reset();
            if (_trigger != null)
            {
                _trigger.gameObject.SetActive(true);
                _trigger.SetOpen(false);
            }
        }

        private TwinPageBase _homePage;

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

            // 头部三处差异：L1 有 3px 色条 + 标题 x=32；L2 让位给返回键，标题右移到 46。
            // （文档写 L2 标题 x=30，但返回键占 22–36，30 会压在箭头上，这里取 46。）
            if (_titleBar != null) _titleBar.gameObject.SetActive(isHome);
            float titleX = isHome ? 32f : 46f;
            if (_title != null) TwinUIFactory.Place(_title.rectTransform, titleX, 20f, 300f, 28f);
            if (_subtitle != null) TwinUIFactory.Place(_subtitle.rectTransform, titleX, 62f, 260f, 16f);

            // 【P29-3.5 规格三】摘要按页逐项判定 —— L1 整行隐藏、数据监测只剩受控、其余三项齐全。
            // 显示与否交给 LayoutSummary 统一裁决，这里不再单独 SetActive（两处都管会互相打）。
            LayoutSummary();
            SyncSubtitle();

            // 首页 420，模块页 640；最大化时铺满画布（统一走 ResolveWidthTarget）
            _widthTarget = ResolveWidthTarget();
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
            // 高度仍然跟随画布（sizeDelta.y = 0），不写死 —— 见 Build() 里的回退说明
            float hiddenX = _widthCur;
            _root.sizeDelta = new Vector2(_widthCur, 0f);
            _root.anchoredPosition = new Vector2(Mathf.Lerp(hiddenX, 0f, TwinTheme.EaseOutCubic(_slide)), 0f);

            ApplyMaximizedLayout();

            // 摘要 0.5s 刷新（只在打开时）
            if (_open && Time.unscaledTime >= _nextSummaryAt)
            {
                _nextSummaryAt = Time.unscaledTime + 0.5f;
                RefreshSummary(false);
            }
        }

        /// <summary>同色但不透明（alpha 拉满）。只用于全屏实底，不改动任何 token。</summary>
        private static Color Opaque(Color c)
        {
            c.a = 1f;
            return c;
        }

        /// <summary>
        /// 最大化（铺满画布）态的收尾。非最大化时逐值回到原样。
        ///
        /// ① **面板底做实。** PanelTop/PanelBottom 的 alpha 是 0.94 / 0.96 ——
        ///    抽屉只有 420~640 宽时只盖住右缘一条，透出去的 6% 落在暗处看不出来；
        ///    铺满整屏后，整片明亮的走廊都会从那 6% 里透出来，观感就是"面板是透明的"。
        ///    全屏态把 alpha 补到 1，两个 token 本身不动（420/640 窄态仍是原值）。
        ///
        /// ② **内容按设计宽度水平居中。** L1/L2 是固定宽度设计（KPI 121×3、右区 440），
        ///    最大化只放大了容器，内容仍从左边起排 → 全部堆在左侧、右边一大片空。
        ///    这里给 _content 加左右等量内缩，让内容整体居中。
        ///    注意是"居中"不是"拉伸重排" —— 真自适应要按可用宽度重新分配列宽，
        ///    那要动布局常量与页面内部坐标，属重新设计，不在这里做。
        /// </summary>
        private void ApplyMaximizedLayout()
        {
            if (_bg != null)
            {
                Color top = TwinTheme.PanelTop;
                Color bottom = TwinTheme.PanelBottom;
                if (_maximized)
                {
                    top.a = 1f;
                    bottom.a = 1f;
                }
                _bg.SetFill(top, bottom);
            }

            if (_backdrop != null && _backdrop.gameObject.activeSelf != _maximized)
                _backdrop.gameObject.SetActive(_maximized);

            if (_content == null) return;

            float pad = Mathf.Max(0f, (_widthCur - DesignWidth()) * 0.5f);
            if (Mathf.Abs(_contentPad - pad) < 0.5f) return;

            _contentPad = pad;
            _content.offsetMin = new Vector2(pad, ToolRowH);
            _content.offsetMax = new Vector2(-pad, -HeaderH);

            // 头部整体内缩同样的量 —— 否则最大化后标题贴屏幕最左、内容在中间，会脱节。
            // header 的 pivot 在左上、anchor 横跨左右，所以「左右各缩 pad」= sizeDelta.x = -2pad
            // 且 anchoredPosition.x = pad。pad 为 0 时逐值回到原始状态。
            if (_header != null)
            {
                _header.sizeDelta = new Vector2(-2f * pad, HeaderH);
                _header.anchoredPosition = new Vector2(pad, 0f);
            }
        }

        // ================================================================ 摘要

        private void RefreshSummary(bool force)
        {
            // 【P29-3.6】三个数全部来自快照，本类不再遍历 Registry。
            // 快照 0.2s 更新一次，这里 0.5s 读一次，读的是同一个数 —— 所以头部摘要
            // 与 L1 KPI 卡、L2 数据监测 KPI 永远是同一个值。
            int online = TwinDeviceStats.OnlineDeviceCount;
            int alarm = TwinDeviceStats.AlarmDeviceCount;
            int scope = TwinDeviceStats.ControlScopeCount;

            bool changed = force || online != _sumOnline || alarm != _sumAlarm || scope != _sumScope;

            if (changed)
            {
                _sumOnline = online;
                _sumAlarm = alarm;
                _sumScope = scope;
                LayoutSummary();
            }

            // 色点跟着最坏状态走：有告警就红，否则绿（受控是"操作量"，不是健康度，不参与取色）
            if (_summaryDot != null)
            {
                Color dot = alarm > 0 ? TwinTheme.StatusAlarm : TwinTheme.StatusNormal;
                _summaryDot.SetFill(dot, dot);
            }

            // 触发钮角标
            if (_trigger != null && (force || alarm != _lastAlarmBadge))
            {
                _lastAlarmBadge = alarm;
                _trigger.SetAlarmCount(alarm);
            }

            // 副标含设备总数（L1 是「已接入 N 个点位」），跟着摘要一起 0.5s 对一次
            SyncSubtitle();

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

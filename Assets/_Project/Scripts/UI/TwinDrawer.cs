using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using SchoolTwin.Core;      // TwinStatus 在 Core，不在 Twin（踩过：CS0246/CS0103）
using SchoolTwin.Twin;
using SchoolTwin.Weather;   // P13：EnvironmentState

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
    /// │ 数据源   帮助                 │  工具行 80
    /// └──────────────────────────────┘
    /// ```
    ///
    /// 【2026-11-04 P12】工具行由「数据源 录制 帮助 设置」四项减为「数据源 帮助」两项，
    /// 行高仍是 80（C-2 裁决值）。这张图原写"工具行 56"，与代码里的 ToolRowH=80 不符 ——
    /// 那是 P29-1 时期的旧值，一并改正（G5：注释描述的实现必须和代码是同一件事）。
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
        /// 全屏态逻辑尺寸（P29-6 §2）：横向 200 + 600 + 200 = 1000，纵向 80 + 580 + 60 = 720。
        /// 全屏**锁死这两个值并居中**，不跟随画布宽度 —— 见 Update 里的说明。
        /// </summary>
        public const float FullW = 1000f;
        public const float FullH = 720f;

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

        /// <summary>最大化按钮的字形（□ / ❐）。切换时直接改 text，不引新贴图。</summary>
        private Text _maxGlyph;

        /// <summary>
        /// 最大化按钮本体（□）。**必须存引用** —— `ApplyHeaderInsets` 要跟 × 一起挪。
        ///
        /// 【为什么不存会在全屏态错位】原来这里没有字段，MaxBtn 只在 BuildHeader 里
        /// `AnchorTopRight(56, 28, 24, 24)` 定一次，之后再没人管它。而 P5-1 P0-3 把
        /// × 改成了「右距 32 / y=54 / 32×32」，于是两个按钮被拉到了**两行**：
        ///   □ 占 y 28–52，× 占 y 54–86，横向 24+8+32 还重叠 8px。
        /// 全屏截图里右上角出现"两个 ×"，就是 □ 在全屏下变成 ▣（方块套方块，
        /// 缩到 70% 后读作叉）＋ 两个按钮错行造成的。见 2026-11-02 主理人反馈。
        /// </summary>
        private RectTransform _maxBtnRt;

        /// <summary>关闭 ×（P5-1 P0-3：全屏 32×32，窄态保持 24×24）。</summary>
        private TwinButton _closeBtn;

        /// <summary>是否处于最大化（铺满画布宽度）。</summary>
        private bool _maximized;

        /// <summary>面板底。最大化时要把它拉实（见 ApplyMaximizedLayout），所以要存引用。</summary>
        private TwinRoundedRect _bg;

        /// <summary>
        /// 全屏 L1 的 4 块实底（中间留空让 3D 透出来）。见 BuildShell 里的说明。
        /// L2 全屏不用它们，走整块 Bg 不透明。
        ///
        /// 用 <see cref="TwinRoundedRect"/> 而不是 Image：**圆角是关键**。
        /// 直角实底拼出来的中间区是一个规规矩矩的矩形窗口，看起来像"3D 被一个边框框住了"；
        /// 圆角卡片压在 3D 上时，四个角会露出场景，读起来才是"卡片浮在场景之上"。
        /// </summary>
        private TwinRoundedRect _bdTop;
        private TwinRoundedRect _bdLeft;
        private TwinRoundedRect _bdRight;
        private TwinRoundedRect _bdBottom;

        /// <summary>当前全屏层是不是 L1（决定"挖洞"还是"整块不透明"）。</summary>
        private bool _fullIsHome;

        /// <summary>内容区当前的水平内缩（最大化时用来居中，非最大化恒为 0）。</summary>
        private float _contentPad = -1f;

        /// <summary>全屏态（20/60/20 三栏）内容层。窄态时整层关闭，窄态布局一行不改。</summary>
        private TwinFullLayer _fullLayer;

        /// <summary>头部/底部当前高度（缓动值）。96→80、80→60 靠它逐帧逼近，不瞬跳。</summary>
        private float _headHCur = HeaderH;
        private float _footHCur = ToolRowH;

        /// <summary>底部工具行容器，全屏时要改高度与四项 x。</summary>
        private RectTransform _toolRow;

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

        /// <summary>
        /// 工具行两项，顺序固定。
        ///
        /// 【2026-11-04 P12 从四项减到两项】原为 { 数据源, 录制, 帮助, 设置 }，四项无一有真功能：
        ///   · 「录制」→ Toast "录制：P2 接入"。
        ///     这一项**必须删**：它和窄态浮卡底部那条真横条里的「录制」**同名但能力完全不同**，
        ///     浮卡那条走 TwinSourceSwitcher.ToggleRecord()（自研 TwinRecorder 落 JSONL，
        ///     喂 TwinPlayer 回放），是真功能；抽屉这条只会弹假 Toast。
        ///     同一个词在同一个屏幕上两种行为，是答辩现场最容易被追问的一类问题。
        ///   · 「设置」→ 唯一存活的行为是 ToggleLegacyPanel() 打开旧 TwinControlPanel，
        ///     而那个面板本轮已删，它连假 Toast 都不剩，只会弹"旧总控面板未创建"。
        ///     设置本身也确实没有内容可设（亮度/语言/分辨率一概没有实现）。
        ///   · 「数据源」「帮助」原来是 Toast 占位，本轮**都接上了真实现**，见 OnToolClicked。
        ///
        /// 保留两项的顺序不能换：「数据源」在左，是四项时期就在的位置（x=34），
        /// 观众的记忆和截图对照都靠这个锚。换序会让"工具行变短了"看起来像"整行挪位了"。
        /// </summary>
        private static readonly string[] _toolNames = { "数据源", "帮助", "时间天气" };

        /// <summary>
        /// 各项的绝对 x（§4.1）。间距 96，从左边距 34 起排。
        ///
        /// 【为什么是两项绝对 x 而不是"居中均分"】这条是刻意选的左对齐，理由三条：
        ///   1. 抽屉左边缘的头部（标题/副标）和工具行原本就是同一个 34 左边距，
        ///      左对齐后整条竖线的视觉重量是连续的；居中会让工具行与头部错开。
        ///   2. 两项居中后位置会落在 x≈110 和 x≈206，**每加一项都要全行重算**，
        ///      而且和四项时期的截图完全对不上，验收时无法逐像素对照。
        ///   3. 用户视线从左侧主入口按钮进来，工具行靠左更顺 —— 右侧那一大片
        ///      留给以后真正需要的功能（比如"演示模式"开关），不预占。
        /// 所以：仍从 34 起排，间距仍 96（**沿用原值，不为了显得紧凑去改**）。
        /// 长度必须与 _toolNames 一致，否则 ApplyToolXs 里按 i 取值会越界。
        ///
        /// 【2026-11-04 P13】两项 → 三项，新增「时间天气」x=226（= 130 + 96）。
        /// 这里 `ApplyToolXs` 其实是**按 `baseX + i*96` 重算的**，`ToolXs` 只在
        /// `BuildToolRow` 建节点时用一次；两处必须同时加，否则建时按旧值摆、
        /// 每帧又被 ApplyToolXs 挪到新位置，第一帧会闪一下。
        /// </summary>
        private static readonly float[] ToolXs = { 34f, 130f, 226f };

        /// <summary>「帮助」浮层。null = 没开。每次打开重建、点任意处或 Esc 即销毁。</summary>
        private RectTransform _helpOverlay;

        /// <summary>「时间天气」居中模态卡。null = 没开。同一时刻最多一张（图纸 §4）。</summary>
        private TwinTimeWeatherCard _timeCard;

        /// <summary>
        /// 工具行「数据源」的轮换游标：上次**尝试**切到哪个源（不是"当前生效的源"）。
        /// -1 = 还没点过，首次点击时用 switcher 的真实当前值作起点。
        ///
        /// 【为什么不用 _switcher.CurrentKind 当基准】切换失败时它是**不变**的
        /// （SwitchTo 只在成功路径上写 _current）。若拿它推下一个，
        /// "当前 MQTT + 回放无录像"会永久卡在"每点一次都去试回放、每次都失败"，
        /// 用户再也回不到 Mock。详见 CycleDataSource 里的实算记录。
        /// </summary>
        private int _srcCursor = -1;

        /// <summary>
        /// 工具行「数据源」用的切换器。**与窄态浮卡底部那条横条是同一个组件** ——
        /// 懒查 FindObjectOfType，找不到才在自身上挂一个（切换器的依赖都能自找，
        /// 不需要 Inspector 配置，这一点与 TwinDetailPanel.EnsureSwitcher 同口径）。
        /// </summary>
        private TwinSourceSwitcher _switcher;

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

        /// <summary>当前是否全屏（最大化）态。小地图靠它决定"让位"还是"移到 3D 左上"。</summary>
        public static bool IsFullScreen { get { return _instance != null && _instance._maximized; } }

        /// <summary>
        /// 当前是否「L1 全屏」（三栏监控态，中间露着 3D）。
        ///
        /// 小地图只在**这一态**显示。L2 全屏是数据态、整块 1000×720 不透明、3D 根本看不见，
        /// 小地图浮上去只会压住「数据监测」标题和 KPI 卡（主理人截图实测）。
        /// </summary>
        public static bool IsFullScreenHome
        {
            get { return _instance != null && _instance._maximized && _instance._fullIsHome; }
        }

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
            // （工具行四项看不见）**。主理人 play 实测确认。
            // ↑ 当时是四项；2026-11-04 P12 已减为两项，这段记录的是那次的实测现象，
            //   保留原文不改（改了就变成"没发生过"）。现在的项是 数据源 / 帮助。
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

            // 全屏态内容层（P29-6 v2）。挂在最后 = 画在所有窄态元素之上。
            // 平时整层关闭，只有最大化时 Show —— 窄态布局一个像素都不动。
            GameObject fullGo = new GameObject("FullLayer", typeof(RectTransform));
            _fullLayer = fullGo.AddComponent<TwinFullLayer>();
            _fullLayer.Build(_root);

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

            // 全屏态的实底 —— **拆成 4 块，中间留空**（见微 P0-1）。
            //
            // 为什么不能铺一块 1000×720 的整底：UGUI 的 Image 是矩形，做不出"挖洞"。
            // 铺整块会把中间 200…800 × 80…660 的 3D 区一起盖住 ——
            // L1 全屏的卖点（看场景、点设备）直接没了（主理人 play 实测：中间一片纯色）。
            //
            // 拆法：顶带 0…1000×0…80 / 左栏 0…200×80…660 / 右栏 800…1000×80…660 / 底带 0…1000×660…720。
            // 中间不放任何 Image，让 3D 相机透出来。
            // 只在 **L1 全屏** 启用；L2 全屏是数据态，用整块 Bg 不透明（两者不共用同一套底）。
            _bdTop = MakeBackdrop("BdTop", 0f, 0f, 1000f, 80f);
            _bdLeft = MakeBackdrop("BdLeft", 0f, 80f, 200f, 580f);
            _bdRight = MakeBackdrop("BdRight", 800f, 80f, 200f, 580f);
            _bdBottom = MakeBackdrop("BdBottom", 0f, 660f, 1000f, 60f);

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

            // 关闭 ×：全屏 24×24、右距 24、y=28（见微 P1-3 / 规格 §3.1）；
            // 用 GhostOnDark 变体 —— 纯 Ghost 在深色标题栏上是"看不见底、只能看见字"，
            // 而这里的字本身又很淡，等于整颗按钮消失。
            //
            // 【P5-1 P0-3】全屏态改成 32×32、右距 16。两态边距由
            // `ApplyHeaderInsets()` 分别下发 —— **窄态一个数字都不动**（窄态已验收，
            // 挪一次就要重验一次，见 ApplyFullMode 的头注释）。
            _closeBtn = TwinButton.Create(header, "×", TwinButtonStyle.GhostOnDark,
                24f, 24f, Close, "Close");
            TwinUIFactory.AnchorTopRight(_closeBtn.Rect, 24f, 28f, 24f, 24f);

            // 最大化 □（P29-4 C 方案）：只加这一个。
            // **不加「最小化 —」** —— 它的行为和关闭 × 完全重叠，两个入口做一件事只会让人分不清。
            // 位置：× 左边 8px（24 + 24 + 8 = 56 右距），同为 24×24。
            //
            // 【P5-1 补】这行的 56/28/24 只是**窄态初值**，全屏态由
            // `ApplyHeaderInsets()` 重新下发（右距 72 / y=54 / 32×32，与 × 同行同高）。
            // 千万别只在这里定一次就以为完事 —— 那正是"两个 × 错行"的成因。
            GameObject maxGo = TwinUIFactory.Node("MaxBtn", header);
            RectTransform maxRt = (RectTransform)maxGo.transform;
            _maxBtnRt = maxRt;
            TwinUIFactory.AnchorTopRight(maxRt, 56f, 28f, 24f, 24f);

            // 与「关闭」同一套底：白 @0.04 + 白 @0.10 描边。
            // 这个按钮是手写的（不是 TwinButton），所以要自己画底 —— 但视觉必须和 GhostOnDark 一致。
            TwinRoundedRect maxBg = TwinUIFactory.Rounded(maxRt, "Bg", TwinTheme.R8,
                TwinTheme.WithAlpha(Color.white, 0.04f), TwinTheme.WithAlpha(Color.white, 0.04f),
                TwinTheme.WithAlpha(Color.white, 0.10f), 1f, false);
            TwinUIFactory.Stretch(maxBg.rectTransform);

            Image maxHit = TwinUIFactory.Block(maxRt, "Hit", new Color(0f, 0f, 0f, 0f), true);
            TwinUIFactory.Stretch(maxHit.rectTransform);

            Button maxBtn = maxGo.AddComponent<Button>();
            maxBtn.targetGraphic = maxHit;
            maxBtn.transition = Selectable.Transition.None;
            maxBtn.onClick.AddListener(ToggleMaximize);

            _maxGlyph = TwinUIFactory.Label(maxRt, "Glyph", TwinTheme.FsBody,
                TextAnchor.MiddleCenter, TwinTheme.TextSecondary);
            TwinUIFactory.Stretch(_maxGlyph.rectTransform);
            _maxGlyph.text = "□";
            _maxGlyph.raycastTarget = false;
            // 【字号为什么两态不同】全屏时按钮放大到 32×32，13px 的字形缩在
            // 32 的框里只剩中间一点，远看像杂点。所以全屏提到 FsBodyStrong(15)，
            // 窄态仍是 FsBody(13)（24 的框配 13px 正好）。
            // 两个值直接用 `TwinTheme` 常量，不另存字段 —— 存一份就和 token 脱钩了。

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
            //
            // 【P5-1 P0-3】全屏态改用绝对右缘 888（相对 1000 宽）：
            //   rightInset = 1000 − 888 = 112。
            // 窄态仍走上面那套 58 —— 窄态的 × 是 24×24、右距 24，
            // 58 已经和它对过账，不动。
            float rightInset = _maximized ? (FullW - FullSummaryRight) : 58f;

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
        /// 工具行：高 80（640→720，C-2 裁决值，**不改**），两项 x=34/130、文字 y=672、字号 11。
        /// L1 与 L2 用同一个函数、同一组绝对 x —— 抽屉变宽时工具行不动，这是"逐像素一致"的前提。
        ///
        /// 【2026-11-04 P12】四项减两项，见 _toolNames 的说明。
        /// 行高 80 一个字都没动：那是靠"文字 y=672、命中区 y=16 高 40"这套绝对坐标撑起来的，
        /// 改行高就得连文字和命中区一起重排，而统计页/详情页的视觉已经验收过（P5）。
        /// 减项只是循环少跑两轮，坐标体系完全不动，所以这轮不存在"减法把视觉搞坏"的风险。
        /// </summary>
        private void BuildToolRow()
        {
            RectTransform row = NewNode("ToolRow", _root);
            row.anchorMin = new Vector2(0f, 0f);
            row.anchorMax = new Vector2(1f, 0f);
            row.pivot = new Vector2(0f, 0f);
            row.sizeDelta = new Vector2(0f, ToolRowH);
            row.anchoredPosition = Vector2.zero;
            _toolRow = row;

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
                _toolHits[i] = hr;
            }

            SelectTool(0);
        }

        /// <summary>
        /// 工具行点击分发。
        ///
        /// 【2026-11-04 P12 重写】原来是「index==3 → ToggleLegacyPanel，其余 → Toast 'XX：P2 接入'」，
        /// 四项无一有真功能。现在两项**都有真实现**，不再有任何占位 Toast。
        ///
        /// 为什么不再用 index 魔法数字：改成两项后 index 只剩 0/1，而
        /// "0=数据源、1=帮助"这种约定只存在于本方法和 _toolNames 的相对顺序里。
        /// 一旦有人调了 _toolNames 的顺序，index 判断会静默错位 ——
        /// 所以按**名字**分派，顺序怎么调都不会错。这是刻意多写的两行。
        /// </summary>
        private void OnToolClicked(int index)
        {
            if (index < 0 || index >= _toolNames.Length) return;
            SelectTool(index);

            string name = _toolNames[index];
            if (name == "数据源") { CycleDataSource(); return; }
            if (name == "帮助") { ShowHelpOverlay(); return; }
            if (name == "时间天气") { ShowTimeWeatherCard(); return; }

            // 走到这里说明 _toolNames 加了新项但没接实现。
            // 不弹"XX：待接入"那种假 Toast —— 那正是本轮要清掉的东西。
            // 改成一条明确指向开发者的警告，答辩现场看不见（Console 里），
            // 但开发时会立刻发现"我加了一项却没接功能"。
            Debug.LogWarning("[P12] 工具行「" + name + "」没有绑定任何行为，请在 OnToolClicked 里接上。");
        }

        // ================================================================ 工具行 · 时间天气

        /// <summary>
        /// 「时间天气」：弹出居中模态卡（图纸 `Docs/P13-1_时间天气面板设计图纸_见微.md`）。
        ///
        /// 【为什么挂在画布根、不挂在抽屉里】
        /// 图纸 §1 要求遮罩盖住整个 1000×720。抽屉只有 440 宽，挂在抽屉里遮罩盖不满 ——
        /// 右上角会露出一块没变暗的场景，看起来像"遮罩画错了"。
        /// 所以父节点取 `_root.parent`（画布根），和帮助浮层同一个做法。
        ///
        /// 【为什么再点一次就关】与「帮助」一致：工具行是动作项不是开关面板，
        /// 连点两次的预期是"关掉"，而不是叠出第二张卡。
        /// </summary>
        private void ShowTimeWeatherCard()
        {
            if (_root == null) return;

            if (_timeCard != null)
            {
                _timeCard.Close();     // Close 内部会回调清引用，不能在这里直接置 null 了事
                return;
            }

            // 打开前先保证环境系统存在：没挂 EnvironmentState 时天气切换不会有任何视觉反应，
            // 用户会以为按钮坏了。Ensure 一下比"什么都不发生"诚实。
            if (EnvironmentState.Instance == null &&
                FindObjectOfType<EnvironmentState>(true) == null)
            {
                GameObject envGo = new GameObject("Environment");
                envGo.AddComponent<EnvironmentState>();
            }

            _timeCard = TwinTimeWeatherCard.Open(_root.parent, OnTimeCardClosed);
        }

        /// <summary>卡片关闭回调（Esc / 点遮罩 / 点 × 三处都会走到）。只清引用，不销毁 —— 销毁由卡片自己负责。</summary>
        private void OnTimeCardClosed()
        {
            _timeCard = null;
        }

        // ================================================================ 工具行 · 数据源

        /// <summary>
        /// 「数据源」：点一次在 Mock → MQTT → 回放 之间轮换，走的是窄态浮卡底部那条
        /// **同一个** TwinSourceSwitcher（不是另建一套）。
        ///
        /// 【为什么要复用浮卡那套】同一件事只能有一个出口（本项目反复踩过的教训）：
        /// 数据源状态是全局唯一的一份，若工具行自己维护一个"当前选的是谁"，
        /// 就会出现"抽屉说 Mock、浮卡说 MQTT、实际数据来自回放"的三方不一致。
        /// 这里只是换个入口去调同一个 SwitchTo()。
        ///
        /// 【为什么用轮换而不是弹三选一】抽屉工具行只有 80 高、命中区 96×40，
        /// 塞三个按钮会小到点不中；而且这里不是"设置数据源"的主入口（浮卡那条才是），
        /// 它是答辩演示时的快捷动作 —— "切一下给大家看"。
        /// 轮换还有一个好处：不需要额外的浮层控件，零新增 UI 元素。
        ///
        /// 【切不过去要说原因】MQTT 未配置服务器、回放没有录像文件时 SwitchTo 会返回 false。
        /// 这时只弹"失败"是没用的，得说清是缺录像还是没接 MQTT —— 同浮卡的处理口径一致。
        /// </summary>
        private void CycleDataSource()
        {
            // 【必须是同一个实例，不能再 AddComponent 第二个】
            //
            // TwinSourceSwitcher 的 `_current` 是**实例字段**，而 MockDataSource /
            // MqttDataSource 的启停是**场景级**的。挂两份的话：
            //   · A 实例 SwitchTo(Mock) → 真把 Mock 开起来、A._current = Mock
            //   · B 实例的 _current 还是旧值 → 浮卡底部那条横条的高亮不变
            // 结果就是"点了切换、数据真的变了、但界面显示没变"。这正是本项目
            // 反复踩的同一种病（同一件事有两个状态源）。见 §1.6 F16「'当前显示什么'要有唯一权威」。
            //
            // 所以这里**只 Find、不建**。找不到就直接说清楚，不要偷偷挂一份新的
            // —— 那份新的会让上面的症状以"更难查"的形式复现。
            if (_switcher == null) _switcher = FindObjectOfType<TwinSourceSwitcher>(true);
            if (_switcher == null)
            {
                // 正常情况下不会走到这里：只要窄态浮卡被构建过一次，
                // TwinDetailPanel.EnsureSwitcher() 就已经挂上了。走到的唯一可能是
                // 抽屉先于浮卡打开 —— 那是可以接受的（用户还没点过任何设备）。
                TwinToast.Show("数据源切换器未就绪 · 先点一台设备");
                return;
            }

            // 【轮换基准不能直接读 _current，否则会卡死】
            //
            // 实算发现的死锁：SwitchTo 失败时 `_switcher._current` **不变**。
            // 若每次都从 _current 推 next，那么"当前=MQTT、回放无录像"时：
            //   点一次 → next=回放 → 失败 → _current 仍是 MQTT
            //   再点   → next 还是回放 → 再失败 …
            // 用户被永久困在 MQTT，**再也回不到 Mock**。
            //
            // 修法：抽屉自己记一个 `_srcCursor`（上次尝试到哪），用它推下一个，
            // 而不是读"当前真实生效的源"。两者在成功时相同，失败时才分道 ——
            // 而失败恰恰是最需要继续能往下点的时候。
            //
            // 为什么不干脆"失败就重置成 Mock"：那会让"连点两下"的语义变成
            // "第二下必然跳回 Mock"，用户以为按钮坏了。用游标是唯一能表达
            // "我想切到第三个，它现在不可用，我再点一下去下一个" 的做法。
            TwinSourceSwitcher.SourceKind from = _srcCursor >= 0
                ? (TwinSourceSwitcher.SourceKind)_srcCursor
                : _switcher.CurrentKind;

            TwinSourceSwitcher.SourceKind next = NextSource(from);
            _srcCursor = (int)next;

            bool ok = _switcher.SwitchTo(next);
            if (ok)
            {
                TwinToast.Show("数据源 → " + TwinSourceSwitcher.ShortName(next));
            }
            else if (next == TwinSourceSwitcher.SourceKind.Playback)
            {
                TwinToast.Show("无录像 · 先在浮卡底部点「录制」");
            }
            else
            {
                TwinToast.Show(TwinSourceSwitcher.ShortName(next) + " 未接入");
            }
        }

        /// <summary>
        /// 按 枚举顺序回绕 取下一个数据源。
        ///
        /// 用枚举的 min/max 而不是写死 (cur+1)%3：以后加第四个来源（比如 WebSocket）
        /// 时这里自动跟上，不用回来改。SourceKind 的定义见 TwinSourceSwitcher.
        /// </summary>
        private static TwinSourceSwitcher.SourceKind NextSource(TwinSourceSwitcher.SourceKind cur)
        {
            int n = System.Enum.GetValues(typeof(TwinSourceSwitcher.SourceKind)).Length;
            if (n <= 0) return cur;
            int i = (int)cur;
            return (TwinSourceSwitcher.SourceKind)((i + 1) % n);
        }

        // ================================================================ 工具行 · 帮助

        /// <summary>
        /// 「帮助」：在抽屉内弹一层半透明浮层，列出操作说明。
        ///
        /// 【为什么不用 TwinToast】Toast 是单行、2 秒自动消失，讲不完一套操作。
        /// 而这个浮层要回答的是"这东西怎么用"—— 答辩时评委第一次上手，
        /// 或者换个人来点，都需要能自己看到"滚轮做什么、Esc 做什么"。
        ///
        /// 【为什么不引新控件】全部用 TwinUIFactory 现成的 Rounded + Label + Block 拼，
        /// 零新增资产。本项目是"零图片资源"路线（所有圆角代码生成），引一张帮助图
        /// 就破坏了这个约定。
        ///
        /// 【它挂在 _root 下、不在 _content 下】_content 上有 RectMask2D（裁剪），
        /// 挂进去会被裁；而且它要盖住工具行与头部，属于"抽屉级"而非"页面级"。
        /// 每次打开重建、关闭即销毁 —— 不留常驻节点，也不进对象池：
        /// 一年点不了几次，池化反而是负担。
        /// </summary>
        private void ShowHelpOverlay()
        {
            if (_root == null) return;

            // 已经开着就关掉（再点一次「帮助」= 关闭），避免叠出第二层
            if (_helpOverlay != null)
            {
                Destroy(_helpOverlay.gameObject);
                _helpOverlay = null;
                return;
            }

            const float w = 420f;
            const float h = 300f;

            Image dim = TwinUIFactory.Block(_root, "HelpOverlay", new Color(0f, 0f, 0f, 0.55f), true);
            _helpOverlay = dim.rectTransform;
            TwinUIFactory.Stretch(_helpOverlay);

            // 【必须显式置顶】新节点是 _root 的最后一个孩子，但工具行 / 头部是在 Build()
            // 里先建的兄弟节点 —— 若哪天 Build 顺序变了（比如把工具行挪到后面建），
            // 本浮层就会被压在工具行下面，表现为"点了帮助，只有半边变暗"。
            // 用 SetAsLastSibling 把"我在最上面"这件事写成显式声明，不靠建顺序侥幸。
            _helpOverlay.SetAsLastSibling();

            // 点浮层任意处关闭。用整块 dim 当按钮 —— 比只点 × 更好按，
            // 也避免"点到字上没反应"（文字 raycastTarget 已关）。
            Button close = dim.gameObject.AddComponent<Button>();
            close.targetGraphic = dim;
            close.transition = Selectable.Transition.None;
            close.onClick.AddListener(ShowHelpOverlay);   // 复用自己的"再点即关"

            TwinRoundedRect panel = TwinUIFactory.Rounded(_helpOverlay, "Panel", 10f,
                TwinTheme.PanelTop, TwinTheme.PanelBottom, TwinTheme.AccentLine, 1f, true);
            RectTransform pr = panel.rectTransform;
            pr.anchorMin = new Vector2(0.5f, 0.5f);
            pr.anchorMax = new Vector2(0.5f, 0.5f);
            pr.pivot = new Vector2(0.5f, 0.5f);
            pr.sizeDelta = new Vector2(w, h);
            pr.anchoredPosition = Vector2.zero;

            Text title = TwinUIFactory.Label(pr, "Title", TwinTheme.FsSubtitle, TextAnchor.UpperLeft,
                TwinTheme.Accent, FontStyle.Bold);
            // ⚠️ Place 的 y 是「从父节点顶边往下量」的**正数**（内部写成 anchoredPosition.y = -y），
            // 所以这里必须传 16 而不是 -16 —— 传 -16 会把标题推到框外上方 16px，看不见。
            TwinUIFactory.Place(title.rectTransform, 20f, 16f, w - 40f, 24f);
            title.text = "操作说明";

            // 逐行写清"按什么 / 做什么"。不用富文本对齐 —— legacy Text 不支持，
            // 靠固定宽度 + 两段拼接已经够读。
            string[] lines =
            {
                "滚轮 / ↑↓     切换选中项",
                "E / 回车       确认（打开那台设备）",
                "Esc            返回上一页，首页再按即收起抽屉",
                "H              打开／关闭本帮助",
                "",
                "左侧五项：数据监测 · 设备管理 · 电力 · 告警 · 统计",
                "底部「数据源」：Mock → MQTT → 回放 轮换",
                "",
                "窄态浮卡底部另有一条数据源横条，与本处联动",
            };

            Text body = TwinUIFactory.Label(pr, "Body", TwinTheme.FsBody, TextAnchor.UpperLeft,
                TwinTheme.TextSecondary);
            TwinUIFactory.Place(body.rectTransform, 20f, 48f, w - 40f, h - 76f);
            body.text = string.Join("\n", lines);
            body.lineSpacing = 1.45f;

            // 底部提示贴在框内右下：pivot 用 (1,0) 直接从右下角量 20/14，
            // 不去算 w-160-20 这种"用减法凑右对齐"的坐标（改宽度就会算错）。
            Text hint = TwinUIFactory.Label(pr, "Hint", TwinTheme.FsMicro, TextAnchor.LowerRight,
                TwinTheme.TextMuted);
            hint.rectTransform.anchorMin = new Vector2(1f, 0f);
            hint.rectTransform.anchorMax = new Vector2(1f, 0f);
            hint.rectTransform.pivot = new Vector2(1f, 0f);
            hint.rectTransform.sizeDelta = new Vector2(160f, 16f);
            hint.rectTransform.anchoredPosition = new Vector2(-16f, 12f);
            hint.text = "点任意处或按 H 关闭";
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

                // 【P5 收口】不再挂「在 P2~P5 接入」的提示。
                //
                // 这条 Toast 是 P2 阶段的占位：那时五个模块只有数据监测有内容，
                // 点其他 Tab 弹一句"将在后续阶段接入"。现在五个页签都接了真实数据
                // （监测 KPI/健康条/Top3、设备列表、电力树、告警、统计大屏），
                // 再弹就是**过期文案** —— 主理人 2026-11-02 截图指出"每点一个都有"。
                //
                // `OnModulePicked` 字段保留（默认 null），以后要做埋点或联动再挂。
                page.OnModulePicked = null;
            }
            _stack.Push(page);
        }

        // ================================================================ 设备详情页（P3-B）

        /// <summary>
        /// 【P3-B】打开单台设备详情 —— **页栈深一层**，不是和五个模块平级的第六页。
        ///
        /// 主理人裁决：「页面层级不一样，不要所有功能都套在同一页面层级上」。
        /// 详情是"从列表钻进去"的下一层，所以走 `Push`；返回按钮 `Pop` 回管理系统。
        ///
        /// ── 为什么复用同一个页面实例 ────────────────────────────────────
        /// 每点一台设备就 new 一个页面，会积一堆隐藏的 GameObject，
        /// 而且趋势线缓存（`TwinSparkline` 的采样点）也会跟着泄漏。
        /// 复用同一个实例、只换绑定的设备，是这类"钻取页"的标准做法。
        /// </summary>
        public static void OpenDeviceDetail(TwinAnchor anchor)
        {
            if (_instance == null || anchor == null) return;

            TwinDeviceDetailPage page = _instance.GetComponentInChildren<TwinDeviceDetailPage>(true);
            if (page == null)
            {
                GameObject go = new GameObject("DeviceDetailPage", typeof(RectTransform));
                go.transform.SetParent(_instance._content, false);
                page = go.AddComponent<TwinDeviceDetailPage>();
            }

            page.Bind(anchor);

            // 已经在栈顶就只换绑定，不重复入栈（连点同一台设备时会走到这里）
            //
            // 【P11】但**必须**补一次 ApplyFullMode：连点另一台设备时，
            // 页栈没变、栈顶还是详情页，可"当前是哪台"变了 —— 若不做任何事，
            // 详情页会停在上一台（因为 Bind 里只在 `_built` 时刷新，
            // 而 OnShow 才是重排入口）。这里让它重新 OnShow 一次。
            if (_instance._stack.Top == page)
            {
                page.OnShow();
                _instance.ApplyFullMode();
                return;
            }

            _instance._stack.Push(page);

            // 【P11 关键一步】Push 之后立刻重算"谁占屏"。
            //
            // 原来没有这句：全屏态下详情页被 Push 进 `_content`，而 `_content`
            // 在全屏是关着的 → 详情页虽然激活了，但父节点不可见 → 用户看不到，
            // 观感还是"点了没反应"。`.Push` 本身只改页栈，不知道全屏层的存在。
            //
            // 放在 Push 之后、并在里面处理"全屏层让位 + 打开 _content"，
            // 两态（窄态 Push / 全屏 Push）从此走同一个收口。
            _instance.ApplyFullMode();
        }

        /// <summary>【P3-B】返回上一页。详情页左上角的返回按钮调它。</summary>
        public static void PopPage()
        {
            if (_instance == null) return;
            if (_instance._stack.Depth > 1)
            {
                _instance._stack.Pop();

                // 【P11】Pop 同样要重算"谁占屏"：从详情页返回「设备管理」时，
                // 全屏层必须重新出场把 200/600/200 三栏铺回来。
                // 少了这句，返回后画面会停在一张空的内容区上（全屏层是关着的）。
                _instance.ApplyFullMode();
            }
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
            return _maximized ? FullW : DesignWidth();
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

            // 【P3】两态切换时楼层筛选复位到「全部」。
            //
            // 【为什么必须复位】两套视图（窄态 TwinSystemPage / 全屏 TwinFullLayer）
            // 本轮不合并状态 —— 全屏读自己的 _chipFilter，窄态读 TwinSystemState.FloorFilter。
            // 如果带着「1F」切过去，全屏会显示"全部楼层"但窄态还记着 1F，
            // 切回来时列表突然少了一大半，用户会以为设备丢了。
            // 复位成「全部」是两边都能对上的一致行为。
            TwinSystemState.FloorFilter = 0;
            if (_fullLayer != null) _fullLayer.ResetFloorFilter();

            // 【2026-11-02】全屏态的"还原"标记从 `▣` 改成 `❐`。
            //
            // `▣` 是"实心方块套一个小实心方块"，在 32×32 里几乎糊成一坨，
            // 缩到 70% 显示后读起来就是一个叉 —— 与旁边的 × 撞脸，
            // 主理人截图里报的"两个 ×"有一半是这个原因（另一半是两个按钮错行）。
            //
            // `❐` 是"两个错位叠放的方框"，也就是业界通用的 restore 图标形态，
            // 语义明确、笔画之间有空隙，缩到 70% 仍能读出"两层"。
            // 若字体缺这个字形会显示成方框，届时退回 `□`（狨态同款）也不突兀。
            if (_maxGlyph != null) _maxGlyph.text = _maximized ? "❐" : "□";
            ApplyFullMode();
        }

        /// <summary>
        /// 全屏态与窄态的切换（P29-6 §2）。
        ///
        /// 两态是**两套骨架**，不是同一套改尺寸：全屏头 80 / 底 60 / 左 200 + 中 600 + 右 200，
        /// 窄态头 96 / 底 80 / 单侧 420 或 640。所以切的是"哪一层显示"，
        /// 而不是去挪窄态的坐标 —— 窄态已验收，挪一次就要重验一次。
        /// </summary>
        private void ApplyFullMode()
        {
            if (_fullLayer == null) return;

            // 全屏 = 逛场景，允许 WASD（TwinUIState.BlocksMovement 会读这个标记）。
            // 窄态 = 用面板，保持禁走。
            TwinUIState.FullScreen = _maximized;

            // 【2026-11-03 P11】栈顶是「设备详情页」时，全屏层必须让位。
            //
            // ── 为什么需要这个判断 ──────────────────────────────────
            // 全屏只显示 `_fullLayer`（它自己一套骨架），`_content` 是关的；
            // 而详情页挂在 `_content` 里 → Push 了也看不见，等于"点了没反应"。
            // 反过来，如果只是把 `_content` 打开而不关全屏层，两套字会叠在一起。
            //
            // ── 为什么不干脆让详情页也进全屏层 ──────────────────────
            // 那就又变成"两套实现"—— 窄态一份、全屏一份，正是主理人这次报的问题。
            // 现在详情页只有一份，靠 `IsFullScreen` 自己切换宽度；
            // 全屏层退场，让这一页占满 1000×720。
            bool detailOnTop = IsDetailOnTop();

            if (_maximized && !detailOnTop)
            {
                TwinFullLayer.Mode mode = (_stack != null && _stack.Top != null
                                           && !(_stack.Top is TwinHomePage))
                    ? TwinFullLayer.Mode.System
                    : TwinFullLayer.Mode.Home;

                _fullIsHome = mode == TwinFullLayer.Mode.Home;
                _fullLayer.Show(mode);
            }
            else
            {
                // 【P11】详情页占屏时**必须把 `_fullIsHome` 清掉**，不能留着上一个值。
                //
                // 它有两个下游读者，留着旧值两处都会错：
                //   ① `ApplyMaximizedLayout` 的 `hole = _maximized && _fullIsHome` ——
                //      若从 L1（Home）进来，hole 为真 → 面板底填透明、4 块 backdrop 亮起，
                //      中间 3D 会从详情页左右各 16 的缝里透出来，看着像"底没做实"。
                //   ② `TwinDrawer.IsFullScreenHome` —— 小地图靠它决定是否显示。
                //      详情页是数据态，小地图浮上去会压住量程条。
                //
                // 详情页是"数据态"，语义上等于 L2，所以这里一律按 L2 处理（solid）。
                _fullIsHome = false;
                _fullLayer.Hide();
            }

            // 【全屏层与内容区互斥改成三态】
            //   全屏 + 无详情 → 只开全屏层（关 _content）
            //   全屏 + 有详情 → 只开 _content（关全屏层），详情页铺满
            //   窄态         → 只开 _content
            bool wantContent = !_maximized || detailOnTop;
            if (_content != null && _content.gameObject.activeSelf != wantContent)
                _content.gameObject.SetActive(wantContent);
        }

        /// <summary>栈顶是不是「设备详情页」—— 它决定全屏下由谁占屏。</summary>
        private bool IsDetailOnTop()
        {
            return _stack != null && _stack.Top is TwinDeviceDetailPage;
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
                // 【2026-11-02 补】只复位字形不复位位置，下次打开会停在全屏尺寸：
                // × 还是 32×32/右距32，□ 还是 32×32/右距72，两个按钮比标题条还大。
                // 这里补一次 ApplyHeaderInsets()，把两个按钮和摘要右缘一起拉回窄态值。
                ApplyHeaderInsets();
            }
            // 收起全屏层并恢复窄态内容（否则下次打开是一块空白）
            ApplyFullMode();

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
            if (_stack.Depth > 1)
            {
                _stack.Pop();
                // 【P11】与 `PopPage()` 同一个收口：返回后要重新决定谁占屏，
                // 否则从全屏详情页返回会停在一张空内容区上。
                ApplyFullMode();
            }
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

            LayoutHeaderText(isHome);
            _lastFullForHeader = _maximized;


            // 【P29-3.5 规格三】摘要按页逐项判定 —— L1 整行隐藏、数据监测只剩受控、其余三项齐全。
            // 显示与否交给 LayoutSummary 统一裁决，这里不再单独 SetActive（两处都管会互相打）。
            LayoutSummary();
            SyncSubtitle();

            // 首页 420，模块页 640；最大化时铺满画布（统一走 ResolveWidthTarget）
            _widthTarget = ResolveWidthTarget();

            // 全屏态下切页要换内容层（L1 ↔ L2）；窄态下这里是空操作
            if (_maximized) ApplyFullMode();
        }

        // ================================================================ Esc

        private bool OnEscape()
        {
            if (!_open) return false;

            // 【2026-11-04 P12】帮助浮层是最上层，Esc 必须先关它。
            // 放在最前面：它盖住了整个抽屉，此时按 Esc 若去退页面栈，
            // 用户会看到"浮层还在、下面的页面变了"—— 视觉上像卡死。
            if (_helpOverlay != null)
            {
                Destroy(_helpOverlay.gameObject);
                _helpOverlay = null;
                return true;
            }

            // 【2026-11-04 P13】时间天气卡也是"最上层"，同样必须先吃 Esc。
            // 顺序放在帮助之后：帮助浮层是抽屉内的，卡片挂在画布根上、层级更高，
            // 但两者不会同时打开（都是工具行动作，点另一个时抽屉不会自动关前一个）。
            // 若将来真的同时开着，先关层高的那张才对，所以这里把卡片放在帮手之后判断
            // 仍然正确 —— 帮助只有在卡片已关时才可能被看到。
            if (_timeCard != null)
            {
                _timeCard.Close();
                return true;
            }

            // 【P10 → P11 改】全屏 L2 里先退「设备详情」这一层，再退页 / 收抽屉。
            //
            // ── 为什么这一句现在几乎不会命中 ────────────────────────────
            // P10 时"设备详情"是右栏的一个**组**（`_detailGroup`），不进页栈，
            // 所以需要一个额外的 Esc 出口把它退掉，否则「Esc 返回」那行是假文案。
            //
            // P11 之后详情变成了页栈里的**真实一页**。详情页占屏时全屏层被 `Hide()`
            // （`_mode` 置 `None`、`gameObject` 关掉），而 `HandleEscape()` 现在的判据是
            // 「`_detailGroup` 可不可见」—— 全屏层关着，那个判定必然 false。
            //
            // 所以这一句**保留但不再是主路径**：它现在只在一种情况下兜底 ——
            // 详情组还亮着、而页栈已经回到列表（比如以后又有人在右栏挂回详情预览）。
            // 真正的"退详情"由下面 `_stack.Pop()` 承担，与窄态完全一致。
            if (_fullLayer != null && _fullLayer.HandleEscape()) return true;

            // 先退一层页；已经在首页就收抽屉
            if (_stack != null && _stack.Depth > 1)
            {
                _stack.Pop();

                // 【P11 必须补】Pop 之后重算"谁占屏"。
                //
                // 少了这句，从全屏详情页 Esc 返回时：详情页出栈、全屏层还关着 →
                // 画面停在一张空的内容区上（三栏没了、页也没了）。
                // 和 `PopPage()` 里那句是同一个理由，两条出口都要收口。
                ApplyFullMode();
                return true;
            }

            Close();
            return true;
        }

        // ================================================================ 每帧

        private void Update()
        {
            // 【2026-11-04 P12】H 开关帮助浮层。
            //
            // 为什么放在抽屉自己的 Update 里，而不去 TwinPicker 的快捷键分支：
            // TwinPicker 那边是"点场景 + Esc"，它属于三维交互侧；H 是纯 UI 动作，
            // 且要求"抽屉没开时不响应"—— 把判据放在这里最自然，也不用再往
            // TwinUIState 加一个 HotkeyRouter（那条路由是为模态 UI 准备的）。
            //
            // 留了 _open 判据：抽屉收起时按 H 不应该凭空弹出一层黑罩，
            // 那会挡住三维画面而用户根本没有打开任何面板。
            //
            // 【没有做"正在输入框里打字就不响应"】本项目零 InputField（已 grep 确认，
            // Assets 下一个都没有；所有输入都是按钮/滚轮）。写一个 IsTypingInInput()
            // 会是纯想象出来的防御 —— 那种代码比没有更坏，它暗示"这里有输入框"。
            // 将来真加了输入框，记得回来补这一句。
            if (_open && Input.GetKeyDown(KeyCode.H))
                ShowHelpOverlay();

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

            if (_maximized)
            {
                // 【全屏态】锁 1000×720 居中，**不跟随画布宽度**。
                // 规格 §4.1 要求全屏在 1000×720 逻辑坐标内重排（三栏 200/600/200 是绝对坐标）。
                // 而 Expand 下画布宽可能是 1176.9（1069×654 窗口）—— 若让 _root 铺满画布，
                // 三栏会全部靠左、右侧空出一大块，正是上一版"内容堆在左边"的翻版。
                // 画布高在 Expand 下恒 ≥720，所以 720 高不会溢出。
                //
                // ⚠️ **pivot 必须一起改**。窄态 pivot 是 (1, 0.5)（贴右缘），
                // 只改 anchor 不改 pivot 的话，"右边缘"会对齐到画布中心，
                // 面板从中心往左铺 1000 → 左缘跑到画布外，左轨 5 个 Tab 全被裁掉。
                // 这就是 P0-1 的根因（主理人 play 实测：整体左溢约 200 单位）。
                _root.anchorMin = new Vector2(0.5f, 0.5f);
                _root.anchorMax = new Vector2(0.5f, 0.5f);
                _root.pivot = new Vector2(0.5f, 0.5f);
                _root.sizeDelta = new Vector2(FullW, FullH);
                _root.anchoredPosition = Vector2.zero;
            }
            else
            {
                // 窄态还原：锚点、pivot、尺寸三样都要回原值，缺一个就错位
                _root.anchorMin = new Vector2(1f, 0f);
                _root.anchorMax = new Vector2(1f, 1f);
                _root.pivot = new Vector2(1f, 0.5f);
                _root.sizeDelta = new Vector2(_widthCur, 0f);
                _root.anchoredPosition = new Vector2(Mathf.Lerp(hiddenX, 0f, TwinTheme.EaseOutCubic(_slide)), 0f);
            }

            // 头/底高度缓动：96↔80、80↔60。**必须缓动不能瞬跳**（负责人裁决）——
            // 16px 的高度突变会让整块内容在切换瞬间"抖"一下，看起来像闪屏。
            float headTarget = _maximized ? TwinFullLayer.HeadH : HeaderH;
            float footTarget = _maximized ? TwinFullLayer.FootH : ToolRowH;
            float step = Time.unscaledDeltaTime * 320f;

            if (Mathf.Abs(_headHCur - headTarget) > 0.01f)
                _headHCur = Mathf.MoveTowards(_headHCur, headTarget, step);
            if (Mathf.Abs(_footHCur - footTarget) > 0.01f)
                _footHCur = Mathf.MoveTowards(_footHCur, footTarget, step);

            ApplyMaximizedLayout();

            if (_fullLayer != null && _maximized)
            {
                _fullLayer.TickFade();
                _fullLayer.Refresh(false);
            }

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
        /// 造一块全屏实底卡片。固定坐标（全屏 _root 恒 1000×720），默认关闭。
        ///
        /// 用 CardTop→CardBottom 渐变 + R12 圆角 + 1px 描边，而不是纯色直角 Image：
        /// 圆角会让四个角露出 3D，观感是"卡片浮在场景上"，而不是"3D 被框在一个窗口里"。
        /// </summary>
        private TwinRoundedRect MakeBackdrop(string name, float x, float y, float w, float h)
        {
            TwinRoundedRect card = TwinUIFactory.Rounded(_root, name, TwinTheme.R12,
                Opaque(TwinTheme.CardTop), Opaque(TwinTheme.CardBottom),
                TwinTheme.Border, TwinTheme.StrokeHair.Width, false);
            TwinUIFactory.Place(card.rectTransform, x, y, w, h);
            card.transform.SetSiblingIndex(0);      // 压到 Bg 之下
            card.gameObject.SetActive(false);
            return card;
        }

        private static void SetActiveIfChanged(Component c, bool on)
        {
            if (c != null && c.gameObject.activeSelf != on) c.gameObject.SetActive(on);
        }

        /// <summary>上次给头部定位时是否全屏。两态标题 y 不同（30/56 vs 20/62），只在切换时重排。</summary>
        private bool _lastFullForHeader;

        // ---- 全屏态头部右上角三段（P5-1 P0-3）----
        //
        // 现状（相对 1000 宽的全屏，从左到右排开）：
        //   状态文字  右端 888
        //   □ 最大化  左缘 904 … 右缘 936   (右距 72, y=54, 32×32)
        //   × 关闭    左缘 936 … 右缘 968   (右距 32, y=54, 32×32)
        //   → 文字与 □ 间距 16 ✔、□ 与 × 间距 8 ✔、× 右缘距面板边界 32
        //
        // 【原来那处「x=936 与 间距 16 互斥」的死结，已解开】────────────
        // 第一版只把 × 挪到 x=936、状态文字右端 888，中间空 48，当时判定
        // 「x=936 与『右端 888 + 间距 16』数学互斥，必须放弃一个」。
        // 那个结论错在**假设右上角只有一个按钮**。实际还有一个 □ 最大化，
        // 它本来就在 × 左边。把它纳入同一行之后：
        //   888 + 16 = 904（□ 左缘）→ 936（□ 右缘）→ 968（× 右缘）
        // 三个数字全部成立，一个都不用放弃。
        //
        // 教训：算间距前先把这一片**所有**控件数清楚，别只盯着要改的那一个 ——
        // 只挪 × 不动 □ 的直接后果就是两个按钮错行、□ 缩成 ▣ 被读成第二个 ×。
        private const float FullCloseSize = 32f;
        private const float FullCloseY = 54f;
        private const float FullSummaryRight = 888f;
        /// <summary>右上角两个按钮之间的横向间距（与窄态一致，视觉节奏统一）。</summary>
        private const float FullBtnGap = 8f;

        /// <summary>
        /// 全屏态右上角：关闭键放大到 32×32、最大化键跟着走、状态文字右端收到 888。
        ///
        /// 只在 `_maximized` 变化时调，不每帧写 —— 这几个值不会中途变。
        /// 注意 `_summary` 的右缘是由 `LayoutSummary()` 用 `_widthCur` **现算**的
        /// （文案长度会变），所以这里不能直接写它的 x，只能改它用的 `rightInset`。
        ///
        /// ── 【2026-11-02 修】□ 必须和 × 一起下发 ──────────────────────
        /// 第一版只挪了 ×，□ 还停在 BuildHeader 里的「右距 56 / y=28 / 24×24」，
        /// 于是全屏态右上角变成上下两行：□ 在 y 28–52、× 在 y 54–86，
        /// 横向 24+8+32 还重叠 8px。而全屏时 □ 的字形是 `▣`（方块套方块），
        /// 缩到 70% 读起来就是一个叉 —— 主理人看到的就是"两个 ×"。
        ///
        /// 现在两个按钮同一行中心（y 都是 54）、同高 32、间距 8：
        ///   □  左缘 904 … 右缘 936
        ///   ×  左缘 936 … 右缘 968
        /// 状态文字右端仍是 888，与 □ 左缘 904 间距 16 —— 正好是优化单要的 16。
        /// </summary>
        private void ApplyHeaderInsets()
        {
            if (_closeBtn == null) return;

            if (_maximized)
            {
                // × 右缘 968 → marginRight = 1000 − 968 = 32
                TwinUIFactory.AnchorTopRight(_closeBtn.Rect, 32f, FullCloseY,
                    FullCloseSize, FullCloseSize);

                // □ 放在 × 左侧，间距 FullBtnGap：
                //   右距 = 32(× 右距) + 32(× 宽) + 8(间距) = 72
                // 宽高与 × 相同、y 也相同 —— 三个值全部由常量推出来，不写死 72
                if (_maxBtnRt != null)
                {
                    TwinUIFactory.AnchorTopRight(_maxBtnRt,
                        32f + FullCloseSize + FullBtnGap, FullCloseY,
                        FullCloseSize, FullCloseSize);
                    // 字形字号跟着放大（32 的框配 13px 太小）
                    if (_maxGlyph != null) _maxGlyph.fontSize = TwinTheme.FsBodyStrong;
                }
            }
            else
            {
                TwinUIFactory.AnchorTopRight(_closeBtn.Rect, 24f, 28f, 24f, 24f);

                // 窄态回到初值：× 右距 24、□ 右距 24+24+8 = 56，同为 24×24
                if (_maxBtnRt != null)
                {
                    TwinUIFactory.AnchorTopRight(_maxBtnRt,
                        24f + 24f + FullBtnGap, 28f, 24f, 24f);
                    if (_maxGlyph != null) _maxGlyph.fontSize = TwinTheme.FsBody;
                }
            }

            LayoutSummary();
        }

        /// <summary>
        /// 标题与副标定位。两态不同（P29-6 §3.1）：
        ///   窄态 头 96 → 标题 y=20、副标 y=62
        ///   全屏 头 80 → 标题 y=30、副标 y=56（顶带收窄 16px，标题下移保持视觉居中）
        /// 左基准：L1 窄态 32（让开 3px 色条）/ 全屏 20（规格：内容左缘 = 20）；L2 恒 46（让开返回键）。
        /// </summary>
        private void LayoutHeaderText(bool isHome)
        {
            float titleX = isHome ? (_maximized ? 20f : 32f) : 46f;
            float titleY = _maximized ? 30f : 20f;
            float subY = _maximized ? 56f : 62f;

            if (_title != null) TwinUIFactory.Place(_title.rectTransform, titleX, titleY, 300f, 28f);
            if (_subtitle != null) TwinUIFactory.Place(_subtitle.rectTransform, titleX, subY, 260f, 16f);

            // 全屏时摘要提到主色级：全屏顶带更长、右侧更空，TextSecondary 在那片深底上偏灰，
            // 和「按钮看不见」是同一类问题（亮度不够）。
            if (_summary != null)
                _summary.color = _maximized ? TwinTheme.TextPrimary : TwinTheme.TextSecondary;
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
            // 顶带高度变了，标题/副标的 y 要跟着换（只在切换那一下重排，不每帧写）
            if (_lastFullForHeader != _maximized)
            {
                _lastFullForHeader = _maximized;
                bool isHome = _stack == null || _stack.Top == null || _stack.Top is TwinHomePage;
                LayoutHeaderText(isHome);

                // 【P5-1 P0-3】右上角三段（关闭键尺寸/边距 + 状态文字右缘）跟着两态换。
                // 和 LayoutHeaderText 同一个时机 —— 都是"只在切换那一下重排"。
                ApplyHeaderInsets();
            }

            // 底分三种形态（见微 P0-1 / P0-2）：
            //   L1 全屏 → Bg 填充透明（只留描边）+ 4 块实底围出中间 3D 窗口
            //   L2 全屏 → Bg 整块不透明（数据态，3D 不可见）
            //   窄态   → Bg 原样 0.94 / 0.96
            bool hole = _maximized && _fullIsHome;
            bool solid = _maximized && !_fullIsHome;

            if (_bg != null)
            {
                Color top = TwinTheme.PanelTop;
                Color bottom = TwinTheme.PanelBottom;
                if (solid)
                {
                    top.a = 1f;
                    bottom.a = 1f;
                }
                else if (hole)
                {
                    // 填充透明、描边保留 —— 描边只在矩形外框，不会挡住中间的 3D
                    top = Color.clear;
                    bottom = Color.clear;
                }
                _bg.SetFill(top, bottom);
            }

            SetActiveIfChanged(_bdTop, hole);
            SetActiveIfChanged(_bdLeft, hole);
            SetActiveIfChanged(_bdRight, hole);
            SetActiveIfChanged(_bdBottom, hole);

            if (_content == null) return;

            // 窄态内容与全屏层必须互斥。这里做成**幂等**的每帧检查（只比 activeSelf，很便宜）：
            // 只要某处又把 _content 激活了（切页 / Push / 恢复），下一帧就会被重新关掉。
            // 上一版只在 ApplyFullMode 里切一次，一旦有第二条路径激活它，
            // 就会出现"窄态 440 页面 + 全屏三栏"叠在一起（见微 P0-3 / P1-2 的橙色段就是这么来的）。
            //
            // 【P11 补】例外：全屏 + 栈顶是详情页时，`_content` **本来就该开着** ——
            // 那一刻全屏层已经让位（`ApplyFullMode` 三态里的第二态），画面由详情页独占。
            // 不排掉这个例外，这里的 `SetActive(!_maximized)` 会在下一帧把详情页连父节点一起关掉，
            // 表现就是"全屏点行 → 一闪 → 又回到三栏"。
            bool contentExpected = !_maximized || IsDetailOnTop();
            if (_content.gameObject.activeSelf != contentExpected)
                _content.gameObject.SetActive(contentExpected);

            // 全屏态内容交给 FullLayer 的三栏，窄态内容已隐藏 —— 所以全屏时头部**通栏**（pad=0），
            // 不能再按"内容居中"去内缩，否则标题会缩到中间、和左右栏对不齐。
            float pad = _maximized
                ? 0f
                : Mathf.Max(0f, (_widthCur - DesignWidth()) * 0.5f);
            // ⚠️ 这里**不能**用「pad 没变就 return」的早退。
            // L1 的窄态 pad 是 0（420−420），全屏 pad 也是 0 —— pad 永远不变，
            // 早退会让后面的头部高度、底带高度、工具行 x 全部永不执行：
            // 表现就是"全屏了但工具行还停在窄态的 34/130/226/322，头部还是 96"。
            // （见微第三轮 P0-2 的"整体偏右且疏密不均"就是这个）
            // 改成三块各自做变化检测，互不牵连。

            if (Mathf.Abs(_contentPad - pad) >= 0.5f
                || Mathf.Abs(_lastHeadH - _headHCur) >= 0.01f
                || Mathf.Abs(_lastFootH - _footHCur) >= 0.01f)
            {
                _contentPad = pad;
                _content.offsetMin = new Vector2(pad, _footHCur);
                _content.offsetMax = new Vector2(-pad, -_headHCur);
            }

            // 头部：高度或内缩变化才写（pivot 在左上、anchor 横跨左右，
            // 所以「左右各缩 pad」= sizeDelta.x = −2pad 且 anchoredPosition.x = pad）
            if (_header != null
                && (Mathf.Abs(_lastHeadH - _headHCur) >= 0.01f
                    || Mathf.Abs(_lastHeadPad - pad) >= 0.01f))
            {
                _lastHeadH = _headHCur;
                _lastHeadPad = pad;
                _header.sizeDelta = new Vector2(-2f * pad, _headHCur);
                _header.anchoredPosition = new Vector2(pad, 0f);
            }

            // 底带高度（缓动中每帧都在变，所以要逐帧对齐）
            if (_toolRow != null && Mathf.Abs(_lastFootH - _footHCur) >= 0.01f)
            {
                _lastFootH = _footHCur;
                _toolRow.sizeDelta = new Vector2(0f, _footHCur);
            }

            // 工具行四项基准：窄态 34 / 全屏 20（内部有 _toolBaseX 变化检测，不会重复写）
            ApplyToolXs(_maximized ? 20f : 34f);
        }

        /// <summary>上次写入头部高度 / 内缩 / 底带高度的值，用于变化检测（避免每帧刷布局）。</summary>
        private float _lastHeadH = -1f;
        private float _lastFootH = -1f;
        private float _lastHeadPad = -1f;

        /// <summary>工具行的命中区。移动位置要动它，**不是动 label** ——
        /// label 在命中区内部（相对 8,-8），改它只会让文字在格子内偏移，看起来像"均分"。
        ///
        /// 【长度跟随 _toolNames，不要再写死 4】原来是 new RectTransform[4]，
        /// 与本轮"四项减两项"直接冲突：多出来的两格会是 null，
        /// ApplyToolXs 里虽然有空判断不会崩，但 SelectTool 会去碰它 ——
        /// 这类"数组长度和配置表长度各写一份"的坑本项目踩过（G1：分支存在但没人调用）。
        /// 静态字段初始化顺序：本字段在 _toolNames 之后声明，可以安全取它的长度。</summary>
        private readonly RectTransform[] _toolHits = new RectTransform[_toolNames.Length];

        /// <summary>
        /// 工具行的起点 x。窄态 34 / 全屏 20（P29-6 §3.1 底带），间距恒 96。
        /// **改的是命中区 hr 的 anchoredPosition**，label 在 hr 内部不动。
        /// 第一版改错成 label，结果四项变成"格子内右移"，肉眼读作间距 250/210/180。
        /// </summary>
        private float _toolBaseX = -1f;

        private void ApplyToolXs(float baseX)
        {
            // 用 Place 而不是只改 anchoredPosition：Place 会把 anchorMin/anchorMax/pivot/sizeDelta
            // 全部重设一遍。只改 anchoredPosition 时，任何一次"别处改过 pivot 或 anchor"
            // 都会让实际位置与算出来的值脱钩（实测间距 118 而不是 96，像被父节点缩放过）。
            for (int i = 0; i < _toolHits.Length; i++)
            {
                if (_toolHits[i] == null) continue;

                float want = baseX + i * 96f - 8f;
                Vector2 p = _toolHits[i].anchoredPosition;
                bool wrong = Mathf.Abs(p.x - want) > 0.01f
                             || Mathf.Abs(_toolHits[i].sizeDelta.x - 96f) > 0.01f;

                if (wrong) TwinUIFactory.Place(_toolHits[i], want, 16f, 96f, 40f);

                // label 在命中区内固定 x=8（创建时的值），被污染过就复位
                if (_toolLabels != null && i < _toolLabels.Length && _toolLabels[i] != null)
                {
                    RectTransform lr = _toolLabels[i].rectTransform;
                    if (Mathf.Abs(lr.anchoredPosition.x - 8f) > 0.01f)
                        lr.anchoredPosition = new Vector2(8f, lr.anchoredPosition.y);
                }
            }

            _toolBaseX = baseX;   // 只留作诊断参考，不参与判断

            // 诊断：L1 / L2 各打一次（用位掩码记"打没打过"，切回去不重复刷）。
            // 两页各出一行，值一对比就知道"两页是不是同一个位置"——
            // 之前只在第一次全屏打，只拿到 L1 的数，L2 对不对没法核对。
            if (baseX < 34f)
            {
                int bit = _fullIsHome ? 1 : 2;
                if ((_toolDiagMask & bit) == 0)
                {
                    _toolDiagMask |= bit;

                    // 【2026-11-04 P12】诊断串按实际项数拼，不再写死 "/"+HitX(2)+"/"+HitX(3)。
                    // 四项减两项后那两个索引恒为 "-"，留着会让人以为"少了两项 = 位置错了"。
                    System.Text.StringBuilder hx = new System.Text.StringBuilder();
                    for (int i = 0; i < _toolHits.Length; i++)
                    {
                        if (i > 0) hx.Append("/");
                        hx.Append(HitX(i));
                    }

                    Debug.Log("[P29-6] 工具行诊断(" + (_fullIsHome ? "L1" : "L2") + ")："
                        + "rowW=" + _toolRow.rect.width.ToString("0.0")
                        + " rowH=" + _toolRow.rect.height.ToString("0.0")
                        + " rowScale=" + _toolRow.localScale.x.ToString("0.000")
                        + " baseX=" + baseX.ToString("0.0")
                        + " hits=" + hx);
                }
            }
        }

        /// <summary>诊断位掩码：bit1 = L1 已打、bit2 = L2 已打。确认无问题后可整段删。</summary>
        private int _toolDiagMask;

        /// <summary>诊断用：取第 i 个命中区的实际 anchoredPosition.x（保留 1 位）。</summary>
        private string HitX(int i)
        {
            if (_toolHits == null || i >= _toolHits.Length || _toolHits[i] == null) return "-";
            return _toolHits[i].anchoredPosition.x.ToString("0.0");
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

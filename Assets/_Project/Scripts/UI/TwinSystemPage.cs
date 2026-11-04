using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using SchoolTwin.Core;
using SchoolTwin.Twin;

namespace SchoolTwin.UI
{
    /// <summary>
    /// 【P29-4】L2 校园管理系统：左轨 168 + 右区 440。
    ///
    /// ── 结构（§4.2）────────────────────────────────────────────────
    /// ```
    /// ┌──────────┬────────────────────────────────────┐
    /// │ 数据监测▌ │ 模块头 17 + 说明 11                 │
    /// │ 设备管理  │ [KPI 216×88][KPI 216×88]           │  右区 x=184 宽 440
    /// │ 电力管理  │ [KPI 216×88][KPI 216×88]           │
    /// │ 告警中心  │ [楼层健康 440×88]                   │
    /// │ 数据统计  │ [异常 Top3 3×40 gap 6]              │
    /// └──────────┴────────────────────────────────────┘
    ///   左轨 168（Tab 152×44，gap 4）
    /// ```
    ///
    /// ── 选中态为什么必须"三件套"──────────────────────────────────
    /// `AccentDim` 现在是 Δ18 的极淡蓝（P29-2 标定的），单靠底色**看不出选中** ——
    /// 在深色面板上那点蓝跟"稍微亮一点"没区别。所以：
    ///   ① AccentDim 底 ② 左缘 3px 主色竖条 ③ 文字 TextPrimary
    /// 三个一起上才成立，少一个就会有人问"我现在在哪个模块"。
    ///
    /// ── 只有「数据监测」有内容 ────────────────────────────────────
    /// 其余四页走 `TwinEmptyState`，说明里写清 P3/P4/P5 接入 + 接入后显示什么。
    /// </summary>
    public sealed class TwinSystemPage : TwinPageBase
    {
        public override string Title { get { return "校园管理系统"; } }
        public override string Subtitle { get { return "1号教学楼 · 5 个模块"; } }

        public Action<string> OnModulePicked;

        /// <summary>当前选中的模块 key（monitor / device / power / alarm / stats）。抽屉用它做「摘要按页逐项判定」。</summary>
        public string CurrentModuleKey
        {
            get { return _selected >= 0 && _selected < Modules.Length ? Modules[_selected].Key : null; }
        }

        // ---- 左轨 ----
        private const float RailX = 8f;
        private const float TabW = 152f;
        private const float TabH = 44f;
        private const float TabGap = 4f;

        // ---- 右区（横向）----
        private const float RightX = 184f;
        private const float RightW = 440f;
        private const float CardW = 216f;
        private const float CardH = 88f;

        // ---- 右区（纵向，P29-3.5 规格一 §1.5 权威预算表 + 基线 B15 余量规则）----
        //   8   模块标题 17        8 → 32
        //  34   模块说明 11       34 → 52（留 2 行空间，这是整体下移 8px 的原因）
        //  60   KPI 行 1         60 → 148
        // 156   KPI 行 2        156 → 244
        // 260   楼层健康卡       260 → 348
        // 364   「异常 Top3」    364 → 380
        // 380   异常行 1         380 → 420
        // 426   异常行 2         426 → 466
        // 472   异常行 3         472 → 512
        // 544   内容底（安全区 32）
        //
        // 【基线 §1.6 B15】异常行组由规格的 384/430/476 **整体上移 4px** 到 380/426/472。
        // 规格给的 476 让第 3 行下沿正好压在 516 —— 等于上限，等于没过。
        // 任何分辨率缩放、字体差异、DPI 变化都会把它顶出去，所以余量至少留 4。
        // 这里取 512（余 32），标题到首行的间距随之由 20 变 16，仍是 4 的倍数。
        private const float ModTitleY = 8f;
        private const float ModDescY = 34f;
        private const float KpiY1 = 60f;
        private const float KpiY2 = 156f;
        private const float HealthY = 260f;
        private const float AbnTitleY = 364f;
        private const float AbnRowY = 380f;
        private const float AbnRowH = 40f;
        private const float AbnRowGap = 6f;

        /// <summary>
        /// 异常 Top3 第 3 行下沿红线。**512，不是规格的 516** ——
        /// 516 是「等于上限」，按 B15 不予通过；512 留 32px 安全区。
        /// </summary>
        public const float AbnLastRowBottom = 512f;

        // ---- 设备管理（规格 §3.2；下沿 506）----
        private const float DevSearchY = 60f;
        private const float DevCountY = 108f;
        private const float DevHeadY = 134f;
        private const float DevListY = 158f;
        private const float DevRowH = 44f;
        private const int DevRows = 64;         // 【C-31】对象池容量（53 台设备），原来固定 7 行看不到后面的
        private const float DevListViewH = 348f; // 可视区高度：158 → 506

        // ---- 电力管理（规格 §3.3；下沿 508）----
        private const float PwrTreeY = 60f;
        private const float PwrTreeH = 360f;
        private const float PwrImpactY = 428f;
        private const float PwrImpactH = 80f;

        // ---- 告警中心（规格 §3.4；下沿 508）----
        private const float AlmSegY = 60f;
        private const float AlmSegH = 32f;
        private const float AlmListY = 100f;
        private const float AlmListH = 348f;
        private const float AlmRowH = 52f;
        private const int AlmRows = 6;          // 100 + 6×52 = 412，视口 348 内 6 行
        private const float AlmTraceY = 456f;
        private const float AlmTraceH = 52f;

        private sealed class ModuleDef
        {
            public string Key;
            public string Name;
            public string Glyph;
            public string Desc;
            public string Phase;      // 空状态里写的接入批次
            public string AfterText;  // 接入后这里显示什么
        }

        private static readonly ModuleDef[] Modules =
        {
            new ModuleDef { Key = "monitor", Name = "数据监测", Glyph = "监",
                Desc = "全场景设备实时数据：综合概览 + 单设备详情（用电量 / 功率 / 温度…）" },
            new ModuleDef { Key = "device", Name = "设备管理", Glyph = "设",
                Desc = "设备台账与开关控制，按「场景-位置-类型-序号」统一编号",
                Phase = "P3", AfterText = "接入后这里显示全部设备的台账与开关控制" },
            new ModuleDef { Key = "power", Name = "电力管理", Glyph = "电",
                Desc = "分级停电：整层批量分合闸 / 单个场景（如机房）独立停电",
                Phase = "P4", AfterText = "接入后这里显示分级回路与批量分合闸" },
            new ModuleDef { Key = "alarm", Name = "告警中心", Glyph = "警",
                Desc = "异常精准定位 + 单台设备全链路追溯（通知 / 报文 / 日志）",
                Phase = "P4", AfterText = "接入后这里显示告警列表与单设备追溯" },
            new ModuleDef { Key = "stats", Name = "数据统计", Glyph = "图",
                Desc = "全量设备数据转可视化：趋势 / 占比 / 排行",
                Phase = "P5", AfterText = "接入后这里显示用电趋势与楼层排行" },
        };

        private readonly RectTransform[] _tabs = new RectTransform[5];
        private readonly TwinRoundedRect[] _tabBg = new TwinRoundedRect[5];
        private readonly Image[] _tabBar = new Image[5];
        private readonly Text[] _tabLabel = new Text[5];
        private readonly TwinRoundedRect[] _tabIcon = new TwinRoundedRect[5];
        private readonly Text[] _tabGlyph = new Text[5];
        private readonly RectTransform[] _pages = new RectTransform[5];

        /// <summary>告警中心 Tab 的右上红点（未确认告警 &gt; 0 时显示）。</summary>
        private RectTransform _alarmDot;

        /// <summary>图标底未选态：白 @0.10（规格 §2）。</summary>
        private static readonly Color IconFillIdle = TwinTheme.WithAlpha(Color.white, 0.10f);

        /// <summary>图标底选中态：主色 @0.16（规格 §2）。</summary>
        private static readonly Color IconFillOn = TwinTheme.WithAlpha(TwinTheme.Accent, 0.16f);

        private TwinMetricCard _kpiPower;
        private TwinMetricCard _kpiEnergy;
        private TwinMetricCard _kpiOnline;
        private TwinMetricCard _kpiAlarm;

        // ---- 楼层健康卡（三段条 + 图例都要能刷新，所以留引用）----
        private readonly TwinRoundedRect[] _healthSeg = new TwinRoundedRect[3];
        private readonly Text[] _healthLeg = new Text[3];
        private Text _healthPct;

        // ---- 异常 Top3（真实数据，不再是写死的三行示例）----
        private readonly RectTransform[] _abnRow = new RectTransform[3];
        private readonly Image[] _abnBar = new Image[3];
        private readonly Text[] _abnId = new Text[3];
        private readonly Text[] _abnMetric = new Text[3];
        private readonly Text[] _abnTag = new Text[3];
        private Text _abnEmpty;

        private readonly List<TwinAlarmHistory.AlarmRecord> _abnBuf =
            new List<TwinAlarmHistory.AlarmRecord>(64);
        private readonly List<TwinAlarmHistory.AlarmRecord> _abnTop =
            new List<TwinAlarmHistory.AlarmRecord>(3);

        // ---- 设备管理：7 行台账（数据由 RefreshDevice 填）----
        private Text _devCount;
        private readonly RectTransform[] _devRow = new RectTransform[DevRows];
        private readonly Image[] _devBar = new Image[DevRows];
        private readonly Text[] _devId = new Text[DevRows];
        private readonly Text[] _devName = new Text[DevRows];
        private readonly Text[] _devPos = new Text[DevRows];
        private readonly Text[] _devTag = new Text[DevRows];
        private readonly TwinRoundedRect[] _devRowBg = new TwinRoundedRect[DevRows];
        /// <summary>【C-31】台账滚动内容容器（刷新时按行数设高度）。</summary>
        private RectTransform _devContent;

        // 【C-30】楼层筛选 chip（全部 / 1F / 2F）。状态存在 TwinSystemState，与全屏共用一份。
        private readonly TwinRoundedRect[] _chipBg = new TwinRoundedRect[3];
        private readonly Text[] _chipText = new Text[3];

        // 【C-30】电力树的内容容器（合闸后要重画）。原来只有 Build 时跑一次。
        private RectTransform _pwrContent;

        // 【P3】SBO 弹窗（惰性创建，只在第一次点合闸时建）。
        private TwinSboDialog _sbo;

        // 【P3-B】设备详情已改为**页栈里更深一层的独立页面**（TwinDeviceDetailPage），
        // 不再放在本页的容器里 —— 主理人裁决「页面层级不一样，不要所有功能都套在同一层」。
        // 打开入口见 TwinDrawer.OpenDeviceDetail()。

        // ---- 告警中心：6 行列表 ----
        private readonly RectTransform[] _almRow = new RectTransform[AlmRows];
        private readonly Image[] _almBar = new Image[AlmRows];
        private readonly Text[] _almId = new Text[AlmRows];
        private readonly Text[] _almName = new Text[AlmRows];
        private readonly Text[] _almDesc = new Text[AlmRows];
        private readonly Text[] _almDur = new Text[AlmRows];
        /// <summary>【告警可点】选中态高亮底（AccentDim），默认全透明。</summary>
        private readonly TwinRoundedRect[] _almRowBg = new TwinRoundedRect[AlmRows];

        /// <summary>
        /// 【告警可点】每行当前对应的 TwinId。
        ///
        /// ── 为什么不能存行号反查 ────────────────────────────────────────
        /// `_abnBuf` 每 0.5s 重排一次（新告警插到最前），行号会漂。
        /// 点了第 3 行却定位到另一台设备，比"点不动"更难解释。
        /// 所以在 RefreshAlarm 里把当次渲染的 TwinId 一起存下来，
        /// 点击时按它去注册表查锚点 —— 和数据同源，不会错位。
        /// </summary>
        private readonly string[] _almTwinId = new string[AlmRows];

        private Text _almSegOn;
        private Text _almSegHis;

        // 【C-30】告警页签的选中态底色（要在切换时挪位置）
        private TwinRoundedRect _almSegBg;
        private TwinRoundedRect _almSegOnBg;

        private readonly List<TwinAnchor> _devBuf = new List<TwinAnchor>(64);

        private float _nextRefresh;
        private int _selected = -1;
        private bool _built;

        // ================================================================ 统计（P5 窄态）
        //
        // ── 为什么这版加了滚动条 ──────────────────────────────────────
        // 上一版是"把 5 块硬塞进 544"：KPI 2×2 + 曲线 + 类型 Top3 + 楼层 4 行 + Top3，
        // 行高压到 20、块间距压到 8，内容实测到 530 —— 可用底 544，只剩 14px。
        // 主理人 2026-11-02：「窄态要是放数据图表太挤的话，你完全可以做一个滑动条，
        // 容纳起来，而不是把一堆表挤到一块，只有条形图吗，数据表现形式要多元化」。
        //
        // 所以这一版三件事一起做：
        //   ① 内容整体进 ScrollRect：视口 60→544（484），内容 672，
        //      行高 20 → 26、块间距 8 → 16，不再靠压行距换空间；
        //   ② 类型分布从"横条 Top3"换成 **donut 120 + 5 行图例** —— 于是窄态里
        //      数字强调 / 折线 / 环形 / 横条四种形态各出现一次，不再全是横条；
        //   ③ 配色与合并规则全部走 `TwinTheme.ChartSeq*` 和全屏同一套，
        //      两态在同样数据下**数字必须相等**（项目级约定）。
        private const int NTypeMax = 5;          // 前 4 类 + "其他"，与全屏 StatsTypeMax 对齐
        private readonly Image[] _nTypeSeg = new Image[NTypeMax];
        private readonly Text[] _nTypeName = new Text[NTypeMax];
        /// <summary>
        /// 台数列（`20 台`）。2026-11-02 从"名称 + 台数拼一串"拆出来独立一列。
        ///
        /// 【为什么要拆】原来名称列写 `名称`、占比列写 `20 台 · 29%`，
        /// 台数被塞在右对齐的占比列里 —— 5 行的台数位没法纵向对齐，
        /// 读起来要一行一行找。拆成三段后：名称左对齐 / 台数右对齐 / 占比右对齐，
        /// 三列各自成轴线（图纸 §2.3 的三列结构）。
        /// </summary>
        private readonly Text[] _nTypeCount = new Text[NTypeMax];
        /// <summary>占比列（只有 `29%`，台数已经挪到 `_nTypeCount`）。</summary>
        private readonly Text[] _nTypeVal = new Text[NTypeMax];
        private readonly TwinRoundedRect[] _nTypeDot = new TwinRoundedRect[NTypeMax];
        private readonly List<KeyValuePair<string, int>> _nTypeBuf =
            new List<KeyValuePair<string, int>>(NTypeMax + 2);
        private Text _nDonutCenter;

        private readonly Text[] _nKpiValue = new Text[4];
        private readonly Text[] _nKpiLabel = new Text[4];
        /// <summary>窄态 KPI 单位（kW / %），13 号次级色，位置跟随数值宽度。</summary>
        private readonly Text[] _nKpiUnit = new Text[4];
        private TwinSparkline _nSpark;
        private Text _nSparkNow;
        /// <summary>「正在采样…」占位（P5-1 P0-2）。</summary>
        private Text _nSparkWarmingText;

        // ---- 窄态楼层能耗（4 行，按功率降序，图纸 §3.5）----
        private const int NFloorRows = 4;
        private readonly Text[] _nFloorName = new Text[NFloorRows];
        private readonly Text[] _nFloorVal = new Text[NFloorRows];
        private readonly Text[] _nFloorPct = new Text[NFloorRows];
        private readonly Image[] _nFloorBar = new Image[NFloorRows];
        private readonly TwinRoundedRect[] _nFloorTrack = new TwinRoundedRect[NFloorRows];

        // ---- 窄态 Top3 设备（3 行，按实时功率降序，图纸 §3.6）----
        private const int NTopRows = 3;
        private readonly Text[] _nTopRank = new Text[NTopRows];
        private readonly Text[] _nTopName = new Text[NTopRows];
        private readonly Text[] _nTopVal = new Text[NTopRows];
        private readonly Text[] _nTopPct = new Text[NTopRows];
        private readonly Image[] _nTopBar = new Image[NTopRows];
        private readonly TwinRoundedRect[] _nTopTrack = new TwinRoundedRect[NTopRows];
        private readonly List<KeyValuePair<TwinAnchor, float>> _nTopBuf =
            new List<KeyValuePair<TwinAnchor, float>>(64);

        /// <summary>
        /// Top3 的**重排节流**：≥3 秒才重新排序。
        ///
        /// 【为什么必须节流】Top 排名是按实时功率排的，而功率每帧都在微动。
        /// 不节流的话名次会每秒跳十几次，读者眼睛根本跟不上 ——
        /// 第 2 名和第 3 名来回换位，看起来像界面在抖。
        /// 3 秒是"能反映变化、又不抖"的折中：设备真开关时 3 秒内也能看到换位。
        /// </summary>
        private float _nTopResortAt;
        private readonly List<KeyValuePair<TwinAnchor, float>> _nTopCache =
            new List<KeyValuePair<TwinAnchor, float>>(NTopRows + 2);

        private Text _nEmpty;

        /// <summary>
        /// 窄态「能耗 Top3」**块内**空状态（区别于整页的 <see cref="_nEmpty"/>）。
        ///
        /// 【2026-11-03 P8 修·拉闸后白框】整栋拉闸后 70 台设备仍在册，
        /// `_nEmpty` 的判据是"设备数为 0"——不成立，于是不显示；
        /// 而 Top3 的内容被 `HasPowerReading` 全挡掉（断电无读数），
        /// 三行全 SetActive(false) → 标题还在、下面一片空白。
        /// 与全屏 `RefreshStatsPage` 同一个缺陷、同一个修法，两态必须同步。
        /// </summary>
        private Text _nTopEmpty;
        /// <summary>
        /// 窄态统计页底部的「数据源健康度」卡片（P12 补齐）。
        ///
        /// 【2026-11-04 为什么独立成类】它要查 5 个数据源组件（Mock / MQTT /
        /// Player / Recorder / Switcher），逻辑比一个 Text 复杂得多；塞进本文件
        /// （已 2700 行）会让"统计页"和"数据源自检"两件事缠在一起。
        /// 【全屏为什么不放】L2 大屏四宫格已经排满（KPI/类型/曲线/楼层/排名），
        /// 健康度属于运维视角，窄态右栏才是运维面板。
        /// </summary>
        private TwinSourceHealthCard _nSourceHealth;
        private float _nSparkNextAt;
        private float _nSparkLo = -1f;
        private float _nSparkHi = -1f;
        private float _nPowerSum;
        /// <summary>
        /// 窄态曲线是否已从常驻历史层整条载入（一次进入只做一次）。
        /// 与全屏 `_sparkHistoryLoaded` 同一件事、同一套理由 —— 项目级约定：两态必须同步。
        /// </summary>
        private bool _nSparkHistoryLoaded;
        /// <summary>
        /// 窄态曲线是否还在「攒样本」（历史层不足 2 点）。
        /// 为 true 时不画线，改显「正在采样…」—— 与全屏同一套规则（P5-1 P0-2）。
        /// </summary>
        private bool _nSparkWarming;

        // ---- 窄态统计滚动容器 ----
        //
        // 视口 y 60 → 544（484 高），content 672 高。坐标全部相对 content 左上角，
        // 所以下面所有子元素的 y 都从 0 起算（不再从 60 起）。
        private const float NStatsViewY = 60f;
        private const float NStatsViewH = 484f;

        // 【2026-11-04 P12 改动】672 → 802 → 推导式
        //
        // 底部有「数据源健康度」卡片，从 NStatsHealthY 起。
        //
        // 【上一版为什么显示不全——必须记下来】上一版把这里硬写成 802，
        // 而卡片实际占 860（690 + 170 高）。结果是第 4 行「录制」正好压在 802 上，
        // 整行被 RectMask2D 裁掉、脚注也没了 —— 主理人 play 截图里只看到 3 行。
        // 硬抄的数字抄错**不报错、不警告**，只表现为"少了一行"，极难发现。
        //
        // 现在改成从卡片自己的常量推导，只有一个来源：
        //   内容高 = 卡片起点 + 卡片高 + 底部留白
        // 以后改卡片行数，只要动 `TwinSourceHealthCard.Rows`，这里自动跟上。
        private const float NStatsHealthY = 690f;
        private const float NStatsContentH =
            NStatsHealthY + TwinSourceHealthCard.TotalHeight + 24f;   // 底部留白 24
        /// <summary>滚动条宽。</summary>
        private const float NStatsBarW = 4f;
        /// <summary>窄态 KPI 卡宽：2×210 + 14 间隙 = 434，正好塞满视口宽（440-6 滚动条）。</summary>
        private const float NKpiW = 210f;
        private RectTransform _nStatsContent;
        private Image _nStatsBar;
        private float _nStatsBarH;

        // ---------------------------------------------------------------- 构建

        public override void OnShow()
        {
            if (_built)
            {
                // 【P3】已建过：从全屏切回来时要把筛选 chip 重画一遍 ——
                // ToggleMaximize 已把 FloorFilter 复位成 0，但 chip 的底色/描边
                // 只在 Build 和点击时更新，不重画就会停在"1F 还亮着"的旧样子。
                RefreshFloorChips();
                RefreshDevice();
                return;
            }
            _built = true;

            RectTransform root = transform as RectTransform;
            if (root == null) return;

            BuildTabs(root);
            Select(0);
        }

        private void BuildTabs(RectTransform root)
        {
            for (int i = 0; i < Modules.Length; i++)
            {
                int index = i;
                ModuleDef m = Modules[i];

                GameObject go = TwinUIFactory.Node("Tab_" + m.Key, root);
                RectTransform rt = (RectTransform)go.transform;
                TwinUIFactory.Place(rt, RailX, 8f + i * (TabH + TabGap), TabW, TabH);
                _tabs[i] = rt;

                // 选中底：AccentDim（Δ18，极淡）—— 三件套之一，单独用看不出来
                TwinRoundedRect bg = TwinUIFactory.Rounded(rt, "Bg", TwinTheme.R8,
                    Color.clear, Color.clear, Color.clear, 0f, true);
                TwinUIFactory.Stretch(bg.rectTransform);
                _tabBg[i] = bg;

                Button btn = go.AddComponent<Button>();
                btn.targetGraphic = bg;
                btn.transition = Selectable.Transition.None;
                btn.onClick.AddListener(() => Select(index));

                // 三件套之二：左缘 3px 主色竖条（y+8 .. y+36，高 28）
                Image bar = TwinUIFactory.Block(rt, "Bar", TwinTheme.Accent, false);
                TwinUIFactory.Place(bar.rectTransform, 0f, 8f, TwinTheme.BarStatusWidth, 28f);
                bar.gameObject.SetActive(false);
                _tabBar[i] = bar;

                // 图标 20×20 R6（规格 §2）：底 白@0.10（未选）/ 主色@0.16（选中），字形 11 居中
                TwinRoundedRect icon = TwinUIFactory.Rounded(rt, "Icon", 6f,
                    IconFillIdle, IconFillIdle, Color.clear, 0f, false);
                TwinUIFactory.Place(icon.rectTransform, 12f, 12f, 20f, 20f);
                _tabIcon[i] = icon;

                Text g = TwinUIFactory.Label(icon.rectTransform, "Glyph", TwinTheme.FsCaption,
                    TextAnchor.MiddleCenter, TwinTheme.TextMuted);
                TwinUIFactory.Stretch(g.rectTransform);
                g.text = m.Glyph;
                g.raycastTarget = false;
                _tabGlyph[i] = g;

                // 三件套之三：文字（选中 TextPrimary / 未选 TextSecondary）
                // 规格写 12.5，legacy Text 的 fontSize 只吃整数，取 13（FsBody）最接近
                Text label = TwinUIFactory.Label(rt, "Label", TwinTheme.FsBody,
                    TextAnchor.MiddleLeft, TwinTheme.TextSecondary);
                TwinUIFactory.Place(label.rectTransform, 40f, 0f, TabW - 44f, TabH);
                label.text = m.Name;
                label.raycastTarget = false;
                _tabLabel[i] = label;

                // 告警中心角标：右上 6×6 红点（未确认 >0 时显示，见 RefreshTabBadge）
                if (string.Equals(m.Key, "alarm", StringComparison.Ordinal))
                {
                    TwinRoundedRect dot = TwinUIFactory.Rounded(rt, "AlarmDot", 3f,
                        TwinTheme.StatusAlarm, TwinTheme.StatusAlarm, Color.clear, 0f, false);
                    TwinUIFactory.Place(dot.rectTransform, TabW - 14f, 8f, 6f, 6f);
                    dot.gameObject.SetActive(false);
                    _alarmDot = dot.rectTransform;
                }
            }
        }

        private void Select(int index)
        {
            if (index < 0 || index >= Modules.Length) return;
            if (_selected == index) return;

            if (_selected >= 0 && _selected < _pages.Length && _pages[_selected] != null)
                _pages[_selected].gameObject.SetActive(false);

            _selected = index;

            for (int i = 0; i < Modules.Length; i++)
            {
                bool on = i == index;
                // 选中三件套：底 + 竖条 + 字色，一个都不能少
                _tabBg[i].SetFill(on ? TwinTheme.AccentDim : Color.clear,
                                  on ? TwinTheme.AccentDim : Color.clear);
                _tabBar[i].gameObject.SetActive(on);
                _tabLabel[i].color = on ? TwinTheme.TextPrimary : TwinTheme.TextSecondary;

                // 图标底 / 字形跟着选中态走（规格 §2：底 主色@0.16、字形 主色）
                if (_tabIcon[i] != null)
                    _tabIcon[i].SetFill(on ? IconFillOn : IconFillIdle, on ? IconFillOn : IconFillIdle);
                if (_tabGlyph[i] != null)
                    _tabGlyph[i].color = on ? TwinTheme.Accent : TwinTheme.TextMuted;
            }

            RefreshTabBadge();

            RectTransform page = GetPage(index);
            if (page != null) page.gameObject.SetActive(true);

            // 首次进入该页立刻填一次数据，不等 0.5s 周期 —— 否则切过去会空白半秒
            switch (index)
            {
                case 1: RefreshDevice(); break;
                case 3: RefreshAlarm(); break;
                case 4: ResetNarrowSpark(); RefreshStats(); break;
            }

            Action<string> cb = OnModulePicked;
            if (cb != null) cb(Modules[index].Key);
        }

        /// <summary>
        /// 告警中心 Tab 的红点：未确认告警 &gt; 0 时亮。
        /// 由 <see cref="RefreshMonitor"/> 每 0.5s 带一次，不单独开 Update ——
        /// 与快照同频，不会出现「角标亮了但数字还没更新」的错拍。
        /// </summary>
        private void RefreshTabBadge()
        {
            if (_alarmDot == null) return;

            bool show = TwinDeviceStats.UnackedAlarmEventCount > 0;
            if (_alarmDot.gameObject.activeSelf != show) _alarmDot.gameObject.SetActive(show);
        }

        private RectTransform GetPage(int index)
        {
            if (_pages[index] != null) return _pages[index];

            GameObject go = new GameObject("Page_" + Modules[index].Key, typeof(RectTransform));
            RectTransform rt = (RectTransform)go.transform;
            rt.SetParent(transform, false);
            TwinUIFactory.Stretch(rt);
            _pages[index] = rt;

            BuildModuleHeader(rt, Modules[index]);

            // 五页结构（规格 §3.1~§3.5）。数据统计本轮只出空状态（§3.5「不出图表」）
            switch (index)
            {
                case 0: BuildMonitor(rt); break;
                case 1: BuildDevice(rt); break;
                case 2: BuildPower(rt); break;
                case 3: BuildAlarm(rt); break;
                default: BuildStats(rt); break;
            }

            return rt;
        }

        private static void BuildModuleHeader(RectTransform root, ModuleDef m)
        {
            Text title = TwinUIFactory.Label(root, "ModTitle", TwinTheme.FsSubtitle,
                TextAnchor.UpperLeft, TwinTheme.TextPrimary);
            TwinUIFactory.Place(title.rectTransform, RightX, ModTitleY, RightW, 24f);
            title.text = m.Name;

            // 说明 10.5 → 11（legacy Text 只吃整数）。高度 18 给它 2 行空间 ——
            // 「分级停电：整层批量分合闸 / 单个场景（如机房）独立停电」在 440 宽下会折行。
            Text desc = TwinUIFactory.Label(root, "ModDesc", TwinTheme.FsCaption,
                TextAnchor.UpperLeft, TwinTheme.TextMuted);
            TwinUIFactory.Place(desc.rectTransform, RightX, ModDescY, RightW, 18f);
            desc.horizontalOverflow = HorizontalWrapMode.Wrap;
            desc.text = m.Desc;
        }

/// <summary>
        /// 数据统计（P5 窄态，可滚动）。
        ///
        /// ── 为什么窄态不是"待接入"了 ──────────────────────────────────
        /// 原来的文案是"无时序存储，用电量只能按功率积分估算，P5 接入后显示趋势"。
        /// 现在 P5 已经做完了（`TwinMetricHistory` 常驻采样 + 统计页大屏），
        /// 窄态再挂"待接入"就是**过期文案**，主理人 2026-11-02 一眼看出来了。
        ///
        /// ── 为什么这一版加了滚动条 ────────────────────────────────────
        /// 上一版把 5 块硬塞进 544：行高压到 20、块间距压到 8，内容实测到 530，
        /// 可用底 544 —— 只剩 14px，再想加图表只能继续压行距，那就成了
        /// 主理人说的"把一堆表挤到一块"。现在整块进 ScrollRect：
        /// 视口高 484、内容高 672，行高 20→26、块间距 8→16，空间换回来 140px。
        ///
        /// 版式（坐标**相对 content 左上角**，content y=0 对应屏幕 60；视口宽 434 = 440-6 滚动条）：
        ///   KPI 2×2     210×52 @ (0/224, 0/62)
        ///   功率曲线     434×64 @ 138（标题 120，曲线 138，X 轴 208）
        ///   类型 donut   120 @ (0, 266) + 5 行图例 @ 266/296/326/356/386
        ///   楼层能耗     4 行 × 26 @ 438
        ///   Top3        3 行 × 26 @ 576
        /// 内容实际底 650，声明高 672（余 22），可滚 166。
        /// </summary>
        private void BuildStats(RectTransform root)
        {
            // ================================================================ 滚动容器
            //
            // 结构照抄设备页/电力页的成熟写法：Scroll → Hit(透明垫) → Viewport(RectMask2D) → Content。
            // 三处细节一个都不能少：
            //   ① Hit 垫片：ScrollRect 的滚轮事件沿**射线命中的 Graphic** 派发，
            //      容器本身没有 Graphic 就收不到 OnScroll（电力页同一个坑）；
            //   ② RectMask2D 在 Viewport 上而不是 Content 上 —— 挂在 Content 上
            //      会把裁剪框跟着内容一起滚走，等于没裁；
            //   ③ Content 的 pivot 必须是 (0.5, 1) 且 anchor 顶到顶边，
            //      否则滚动方向会反、或起点不是从顶部开始。
            GameObject scrollGo = TwinUIFactory.Node("NStatsScroll", root);
            RectTransform scrollRt = (RectTransform)scrollGo.transform;
            TwinUIFactory.Place(scrollRt, RightX, NStatsViewY, RightW, NStatsViewH);

            Image scrollHit = TwinUIFactory.Block(scrollRt, "Hit", new Color(0f, 0f, 0f, 0f), true);
            TwinUIFactory.Stretch(scrollHit.rectTransform);

            GameObject viewGo = TwinUIFactory.Node("Viewport", scrollRt);
            RectTransform viewRt = (RectTransform)viewGo.transform;
            // 视口让出右侧 6px 给滚动条，免得内容条压到滚动条底下
            TwinUIFactory.Place(viewRt, 0f, 0f, RightW - 6f, NStatsViewH);
            viewGo.AddComponent<RectMask2D>();

            GameObject contentGo = TwinUIFactory.Node("Content", viewRt);
            RectTransform contentRt = (RectTransform)contentGo.transform;
            contentRt.anchorMin = new Vector2(0f, 1f);
            contentRt.anchorMax = new Vector2(1f, 1f);
            contentRt.pivot = new Vector2(0.5f, 1f);
            contentRt.anchoredPosition = Vector2.zero;
            contentRt.sizeDelta = new Vector2(0f, NStatsContentH);
            _nStatsContent = contentRt;

            ScrollRect sr = scrollGo.AddComponent<ScrollRect>();
            sr.viewport = viewRt;
            sr.content = contentRt;
            sr.horizontal = false;
            sr.vertical = true;
            sr.movementType = ScrollRect.MovementType.Clamped;
            sr.scrollSensitivity = 24f;

            // 滚动条：宽 4、白@0.08（与电力页同规格）。只做"可滚"的视觉提示，
            // 长度按 视口/内容 比例算，滚到哪跟到哪（见 UpdateStatsBar）。
            _nStatsBarH = Mathf.Max(24f, NStatsViewH * NStatsViewH / NStatsContentH);
            _nStatsBar = TwinUIFactory.Block(scrollRt, "Scrollbar",
                TwinTheme.WithAlpha(Color.white, 0.08f), false);
            TwinUIFactory.Place(_nStatsBar.rectTransform, RightW - NStatsBarW, 0f, NStatsBarW, _nStatsBarH);

            // 后面所有内容都挂到 content 上，坐标从 0 起算
            RectTransform body = contentRt;

            // ================================================================ KPI 2×2
            string[] labels = { "设备总数", "在线率", "告警中", "当前总功率" };

            for (int i = 0; i < 4; i++)
            {
                float x = (i % 2) * (NKpiW + 14f);
                float y = (i / 2) * 62f;

                TwinRoundedRect card = TwinUIFactory.Rounded(body, "NKpi_" + i, TwinTheme.R12,
                    TwinTheme.CardTop, TwinTheme.CardBottom, Color.clear, 0f, false);
                TwinUIFactory.Place(card.rectTransform, x, y, NKpiW, 52f);

                Text v = TwinUIFactory.Label(card.rectTransform, "V", TwinTheme.FsTitle,
                    TextAnchor.UpperLeft, TwinTheme.TextPrimary);
                TwinUIFactory.Place(v.rectTransform, 12f, 4f, NKpiW - 24f, 28f);
                v.text = "—";
                _nKpiValue[i] = v;

                // 【与全屏同步】单位不再拼进数值串，单独一个 13 号次级色小字，
                // 位置按数值 `preferredWidth` 跟随（见 SetNKpi）。窄态卡宽 210，
                // 单位最多占到 198 右缘之前，不会压到旁边那张卡。
                // 窄态卡高只有 52（全屏 60），所以单位底对齐 32、标签让到 34 ——
                // 不能照抄全屏的 y=20/34，那样单位下沿会压到标签上。
                Text u = TwinUIFactory.Label(card.rectTransform, "U", TwinTheme.FsBody,
                    TextAnchor.LowerLeft, TwinTheme.TextMuted);
                TwinUIFactory.Place(u.rectTransform, 12f, 16f, 44f, 16f);
                u.text = "";
                _nKpiUnit[i] = u;

                Text l = TwinUIFactory.Label(card.rectTransform, "L", TwinTheme.FsMicro,
                    TextAnchor.UpperLeft, TwinTheme.TextMuted);
                TwinUIFactory.Place(l.rectTransform, 12f, 34f, NKpiW - 24f, 12f);
                l.text = labels[i];
                _nKpiLabel[i] = l;
            }

            // ================================================================ 全楼实时功率
            Text sparkTitle = TwinUIFactory.Label(body, "NSparkTitle", TwinTheme.FsBodyStrong,
                TextAnchor.UpperLeft, TwinTheme.TextPrimary);
            TwinUIFactory.Place(sparkTitle.rectTransform, 0f, 120f, 180f, 18f);
            sparkTitle.text = "全楼实时功率";

            _nSparkNow = TwinUIFactory.Label(body, "NSparkNow", TwinTheme.FsBodyStrong,
                TextAnchor.UpperRight, TwinTheme.TextPrimary);
            TwinUIFactory.Place(_nSparkNow.rectTransform, 180f, 120f, 140f, 18f);
            _nSparkNow.text = "";

            Text sparkSrc = TwinUIFactory.Label(body, "NSparkSrc", TwinTheme.FsMicro,
                TextAnchor.UpperRight, TwinTheme.TextMuted);
            TwinUIFactory.Place(sparkSrc.rectTransform, 320f, 123f, 114f, 12f);
            sparkSrc.text = "实时 · 60s";

            // 同全屏：必须走 `TwinUIFactory.Graphic<T>` 补 CanvasRenderer，
            // 否则 `OnPopulateMesh()` 永远不跑（Console 会刷 MissingComponentException）。
            _nSpark = TwinUIFactory.Graphic<TwinSparkline>(body, "NSpark");
            // 【P5-08 / R3】宽度 432 → **434**，与图纸 §3.3 统一。
            //
            // 原来写 `RightW - 8f` = 432，而同一块的来源角标（x=320..434）
            // 和 X 轴右标（右对齐到 434）都按 434 落位 —— 于是曲线右端比下面
            // 那行 X 轴标左缩了 2px，成品看起来"线没画满"。
            //
            // 图纸 §3.3 的原话是「曲线宽 434 满宽（代码原为 RightW−8=432，
            // 本轮统一到 434）」并强调「统一之后来源角标、曲线右端、X 轴右标
            // **三者同为 434**，断点不用记『432 是图、434 是字』这种例外」。
            //
            // `RightW - 6f` = 440 − 6 = 434，与滚动条预留（440−6）同一个数，
            // 所以这里不写字面量 434，保留 `RightW - 6f` 表达"视口内满宽"的意图。
            TwinUIFactory.Place(_nSpark.rectTransform, 0f, 138f, RightW - 6f, 64f);
            _nSpark.SetRange(0f, 30f);

            // 与全屏同一套配色（全屏/窄态本质是一个面板，颜色不能两样）
            _nSpark.SetColors(
                TwinTheme.ChartSeq1,
                TwinTheme.ChartFill,
                TwinTheme.WithAlpha(Color.white, 0.09f),
                TwinTheme.WithAlpha(Color.white, 0.22f));

            // X 轴只标首尾。时间窗是自适应的（`span = clamp(now - t0, 1, 60)`），
            // 写死 "-45s" 会在刚启动时撒谎。
            Text sparkX0 = TwinUIFactory.Label(body, "NSparkX0", TwinTheme.FsMicro,
                TextAnchor.UpperLeft, TwinTheme.TextMuted);
            TwinUIFactory.Place(sparkX0.rectTransform, 0f, 208f, 56f, 12f);
            sparkX0.text = "60s 前";

            Text sparkX1 = TwinUIFactory.Label(body, "NSparkX1", TwinTheme.FsMicro,
                TextAnchor.UpperRight, TwinTheme.TextMuted);
            TwinUIFactory.Place(sparkX1.rectTransform, 376f, 208f, 56f, 12f);
            sparkX1.text = "现在";

            // 【P5-1 P0-2】「正在采样…」占位（历史层不足 2 点时才显示，与全屏同一套规则）
            Text sparkWarming = TwinUIFactory.Label(body, "NSparkWarming", TwinTheme.FsCaption,
                TextAnchor.MiddleCenter, TwinTheme.TextMuted);
            TwinUIFactory.Place(sparkWarming.rectTransform, 0f, 138f, RightW - 8f, 64f);
            sparkWarming.text = "正在采样…";
            sparkWarming.gameObject.SetActive(false);
            _nSparkWarmingText = sparkWarming;

            // ================================================================ 设备类型分布（donut + 图例）
            //
            // 【为什么从横条换成 donut】主理人 2026-11-02：「只有条形图吗，数据表现形式要多元化」。
            // 窄态在有滚动之后高度不再是硬约束，132 的 donut 放得下了。
            // 环形只占左半边 120 宽，右边 300 放 5 行图例 —— 440 宽刚好，不需要再压。
            Text typeTitle = TwinUIFactory.Label(body, "NTypeTitle", TwinTheme.FsBodyStrong,
                TextAnchor.UpperLeft, TwinTheme.TextPrimary);
            TwinUIFactory.Place(typeTitle.rectTransform, 0f, 236f, 200f, 18f);
            typeTitle.text = "设备类型分布";

            Text typeSrc = TwinUIFactory.Label(body, "NTypeSrc", TwinTheme.FsMicro,
                TextAnchor.UpperRight, TwinTheme.TextMuted);
            TwinUIFactory.Place(typeSrc.rectTransform, 320f, 239f, 114f, 12f);
            typeSrc.text = "实时 · 全量";

            GameObject donutGo = TwinUIFactory.Node("NDonut", body);
            RectTransform donutRt = (RectTransform)donutGo.transform;
            TwinUIFactory.Place(donutRt, 0f, 266f, 120f, 120f);

            Sprite ring = TwinPaint.RingSprite(112, 0.62f);
            for (int i = 0; i < NTypeMax; i++)
            {
                Image seg = TwinUIFactory.Block(donutRt, "Seg_" + i, TwinTheme.Accent, false);
                TwinUIFactory.Stretch(seg.rectTransform);
                seg.sprite = ring;
                seg.type = Image.Type.Filled;
                seg.fillMethod = Image.FillMethod.Radial360;
                seg.fillOrigin = (int)Image.Origin360.Top;
                seg.fillAmount = 0f;
                _nTypeSeg[i] = seg;
            }

            // 环心：设备台数（和全屏一样，中间写总数）
            _nDonutCenter = TwinUIFactory.Label(donutRt, "C", TwinTheme.FsSubtitle,
                TextAnchor.MiddleCenter, TwinTheme.TextPrimary);
            TwinUIFactory.Stretch(_nDonutCenter.rectTransform);
            _nDonutCenter.text = "0";

            // 图例 5 行 × 30：色点 + 名称 + 台数 + 占比（三段式）
            //
            // ── 横向预算：按 **P5-1 附录 A** 逐格落位（右缘 434 = RightW 440 − 6）──
            //   dot   132 … 140
            //   名称  146 … 230   (w 84)
            //   台数  234 … 306   (w 72)
            //   占比  310 … 434   (w 124，右对齐到视口右缘)
            //
            // 【为什么台数是左对齐钉在 234，而不是右对齐】
            // 初版做成"台数右对齐到 294"，结果「电脑」和「20 台」之间空出 66px，
            // 而「20 台」又紧贴占比列 —— 读起来台数像是属于占比那一列的。
            // 改成左对齐后，5 行的台数左缘对齐成一条竖轴，
            // 与名称右端（230）只差 4px，视觉上「电脑 20 台」是一个词组。
            // 这也正是 P0-1 验收①要求的「名称右端到台数左端 ≤ 8px」。
            //
            // 【字号为什么是 9 / 11 而不是 11 / 13】
            // 优化单正文 P0-1 的改法表写 11 / 13，附录 A 写 9 / 11，两处冲突。
            // 本轮按负责人裁决「以附录 A 为准」取 9 / 11：
            // 台数是辅助量级、占比是主读数列，靠字号差区分，不靠加粗。
            for (int i = 0; i < NTypeMax; i++)
            {
                float y = 266f + i * 30f;

                TwinRoundedRect dot = TwinUIFactory.Rounded(body, "NDot_" + i, 3f,
                    NDonutColor(i), NDonutColor(i), Color.clear, 0f, false);
                TwinUIFactory.Place(dot.rectTransform, 132f, y + 7f, 8f, 8f);
                _nTypeDot[i] = dot;

                Text nm = TwinUIFactory.Label(body, "NTypeName_" + i, TwinTheme.FsCaption,
                    TextAnchor.MiddleLeft, TwinTheme.TextSecondary);
                TwinUIFactory.Place(nm.rectTransform, 146f, y, 84f, 22f);
                _nTypeName[i] = nm;

                // 台数列：左对齐，左缘钉 234（名称右端 230 + 4）
                Text cnt = TwinUIFactory.Label(body, "NTypeCount_" + i, TwinTheme.FsMicro,
                    TextAnchor.MiddleLeft, TwinTheme.TextMuted);
                TwinUIFactory.Place(cnt.rectTransform, 234f, y, 72f, 22f);
                _nTypeCount[i] = cnt;

                // 占比列：只剩百分比，右对齐到视口右缘 434
                Text v = TwinUIFactory.Label(body, "NTypeVal_" + i, TwinTheme.FsCaption,
                    TextAnchor.MiddleRight, TwinTheme.TextSecondary);
                TwinUIFactory.Place(v.rectTransform, 310f, y, 124f, 22f);
                _nTypeVal[i] = v;
            }

            // ================================================================ 楼层能耗（4 行）
            Text floorTitle = TwinUIFactory.Label(body, "NFloorTitle", TwinTheme.FsBodyStrong,
                TextAnchor.UpperLeft, TwinTheme.TextPrimary);
            TwinUIFactory.Place(floorTitle.rectTransform, 0f, 412f, 200f, 18f);
            floorTitle.text = "楼层能耗对比";

            Text floorSrc = TwinUIFactory.Label(body, "NFloorSrc", TwinTheme.FsMicro,
                TextAnchor.UpperRight, TwinTheme.TextMuted);
            TwinUIFactory.Place(floorSrc.rectTransform, 320f, 415f, 114f, 12f);

            // 【P5-11 / R5】窄态两处来源行统一成「实时 · 含估算」，与全屏同一句话。
            //
            // 原来是「实时 · 估算」—— 把主口径（实时读数）抹掉了，
            // 读起来像"这些数全是估的"。而实际 `LivePowerOf` 主线就是实时读数，
            // 只有暖气 / 传感器这类**没有 Power 测点**的设备才走 `额定 × 0.15`。
            //
            // 全屏卡 C / 卡 D 现在也都是「实时 · 含估算」（见 `BuildBlockTitle` 那两处），
            // 两态 + 四卡共五种表达统一到一个说法，不会再出现"同一份数据两种口径"。
            floorSrc.text = "实时 · 含估算";

            for (int i = 0; i < NFloorRows; i++)
            {
                float y = 438f + i * 26f;

                Text nm = TwinUIFactory.Label(body, "NFloorName_" + i, TwinTheme.FsCaption,
                    TextAnchor.MiddleLeft, TwinTheme.TextSecondary);
                TwinUIFactory.Place(nm.rectTransform, 0f, y, 40f, 22f);
                _nFloorName[i] = nm;

                TwinRoundedRect track = TwinUIFactory.Rounded(body, "NFloorTrack_" + i, 3f,
                    TwinTheme.ChartGrid, TwinTheme.ChartGrid, Color.clear, 0f, false);
                TwinUIFactory.Place(track.rectTransform, 44f, y + 8f, 236f, 6f);
                _nFloorTrack[i] = track;

                Image fill = TwinUIFactory.Block(track.rectTransform, "Fill", TwinTheme.Accent, false);
                TwinUIFactory.Place(fill.rectTransform, 0f, 0f, 236f, 6f);
                _nFloorBar[i] = fill;

                Text v = TwinUIFactory.Label(body, "NFloorVal_" + i, TwinTheme.FsCaption,
                    TextAnchor.MiddleRight, TwinTheme.TextSecondary);
                TwinUIFactory.Place(v.rectTransform, 286f, y, 80f, 22f);
                _nFloorVal[i] = v;

                Text pct = TwinUIFactory.Label(body, "NFloorPct_" + i, TwinTheme.FsMicro,
                    TextAnchor.MiddleRight, TwinTheme.TextMuted);
                TwinUIFactory.Place(pct.rectTransform, 370f, y + 3f, 64f, 16f);
                _nFloorPct[i] = pct;
            }

            // ================================================================ Top3 设备能耗
            Text topTitle = TwinUIFactory.Label(body, "NTopTitle", TwinTheme.FsBodyStrong,
                TextAnchor.UpperLeft, TwinTheme.TextPrimary);
            TwinUIFactory.Place(topTitle.rectTransform, 0f, 550f, 200f, 18f);
            topTitle.text = "能耗 Top3 设备";

            Text topSrc = TwinUIFactory.Label(body, "NTopSrc", TwinTheme.FsMicro,
                TextAnchor.UpperRight, TwinTheme.TextMuted);
            TwinUIFactory.Place(topSrc.rectTransform, 320f, 553f, 114f, 12f);
            // 与上面 `floorSrc` 同一句 —— 窄态内部两处也不能两种说法。
            topSrc.text = "实时 · 含估算";

            for (int i = 0; i < NTopRows; i++)
            {
                float y = 576f + i * 26f;

                Text rk = TwinUIFactory.Label(body, "NTopRank_" + i, TwinTheme.FsMicro,
                    TextAnchor.MiddleCenter, TwinTheme.TextMuted);
                TwinUIFactory.Place(rk.rectTransform, 0f, y + 4f, 14f, 14f);
                rk.text = (i + 1).ToString();
                _nTopRank[i] = rk;

                Text nm = TwinUIFactory.Label(body, "NTopName_" + i, TwinTheme.FsCaption,
                    TextAnchor.MiddleLeft, TwinTheme.TextSecondary);
                TwinUIFactory.Place(nm.rectTransform, 16f, y, 96f, 22f);
                _nTopName[i] = nm;

                TwinRoundedRect track = TwinUIFactory.Rounded(body, "NTopTrack_" + i, 3f,
                    TwinTheme.ChartGrid, TwinTheme.ChartGrid, Color.clear, 0f, false);
                TwinUIFactory.Place(track.rectTransform, 116f, y + 8f, 168f, 6f);
                _nTopTrack[i] = track;

                Image fill = TwinUIFactory.Block(track.rectTransform, "Fill", TwinTheme.ChartSeq2, false);
                TwinUIFactory.Place(fill.rectTransform, 0f, 0f, 168f, 6f);
                _nTopBar[i] = fill;

                Text v = TwinUIFactory.Label(body, "NTopVal_" + i, TwinTheme.FsCaption,
                    TextAnchor.MiddleRight, TwinTheme.TextSecondary);
                TwinUIFactory.Place(v.rectTransform, 290f, y, 80f, 22f);
                _nTopVal[i] = v;

                Text pct = TwinUIFactory.Label(body, "NTopPct_" + i, TwinTheme.FsMicro,
                    TextAnchor.MiddleRight, TwinTheme.TextMuted);
                TwinUIFactory.Place(pct.rectTransform, 374f, y + 3f, 60f, 16f);
                _nTopPct[i] = pct;
            }

            // 空状态：盖在视口正中，不进滚动内容（否则滚到中间才看得到"暂无数据"）
            _nEmpty = TwinUIFactory.Label(root, "NEmpty", TwinTheme.FsBody,
                TextAnchor.MiddleCenter, TwinTheme.TextMuted);
            TwinUIFactory.Place(_nEmpty.rectTransform, RightX, NStatsViewY + NStatsViewH * 0.5f - 10f,
                RightW, 20f);
            _nEmpty.text = "暂无数据";
            _nEmpty.gameObject.SetActive(false);

            // 「能耗 Top3」块内空状态：居中在 3 行的位置（y 576..654），
            // 进滚动内容 —— 它不是整页替身，是这一块自己的说明。
            //
            // 【2026-11-03 P8】原来没有这个节点：拉闸后三行全隐藏，只留标题，
            // 主理人截图里就是一块白框。判据见 `RefreshStats` 的 `noRank`。
            _nTopEmpty = TwinUIFactory.Label(body, "NTopEmpty", TwinTheme.FsCaption,
                TextAnchor.MiddleCenter, TwinTheme.TextMuted);
            TwinUIFactory.Place(_nTopEmpty.rectTransform, 0f, 576f + 26f, RightW - 6f, 22f);
            _nTopEmpty.text = "当前无耗电设备";
            _nTopEmpty.gameObject.SetActive(false);

            // ================================================================ 数据源健康度（P12 补齐）
            //
            // 【为什么放在统计页最底下】它是"解释数据从哪来"的一块，不是主 KPI。
            // 放顶部会把四个主 KPI 挤下去，而主理人验收时第一眼看的是那几个数。
            // 放最底下、滚动才看见，符合它的定位：**有人问"这数据真的假的"时才需要看**。
            //
            // 【为什么不新开第六页】五个模块 Tab 是既定结构（规格 §2），
            // 加第六页要重排 Tab 栏 + 改一堆判据。而这块内容 4 行就够，不值得占一页。
            _nSourceHealth = new TwinSourceHealthCard(RightW - 6f);
            _nSourceHealth.Build(body, 0f, NStatsHealthY);

            RefreshStats();
        }

        /// <summary>窄态统计刷新（0.5s 一次，由 `Update` 节流调用）。</summary>
        private void RefreshStats()
        {
            int total = TwinDeviceStats.ActiveDeviceCount;
            bool empty = total <= 0;

            if (_nEmpty != null && _nEmpty.gameObject.activeSelf != empty)
                _nEmpty.gameObject.SetActive(empty);

            int online = TwinDeviceStats.OnlineDeviceCount;
            SetNKpi(0, empty ? "—" : total.ToString(), "");
            SetNKpi(1, empty ? "—" : (online * 100f / total).ToString("0.#"), empty ? "" : "%");
            int nAlarm = TwinDeviceStats.AlarmDeviceCount;
            SetNKpi(2, nAlarm.ToString(), "");
            if (_nKpiValue[2] != null)
                _nKpiValue[2].color = nAlarm > 0 ? TwinTheme.StatusAlarm : TwinTheme.TextPrimary;
            SetNKpi(3, empty ? "—" : (_nPowerSum / 1000f).ToString("0.0"), empty ? "" : "kW");

            // 【P1】负载率（与全屏同一口径：实时 / 装机容量）
            if (_nKpiLabel[3] != null)
            {
                float capacity = TwinPowerEstimate.TotalWatt();
                SetText(_nKpiLabel[3], (!empty && capacity > 0.01f)
                    ? "当前总功率 · 负载 " + (_nPowerSum / capacity * 100f).ToString("0.#") + "%"
                    : "当前总功率");
            }

            SetText(_nSparkNow, empty ? "" : (_nPowerSum / 1000f).ToString("0.0") + " kW");

            // 【P5-1 P0-2】采样不足 2 点：不画线，改显「正在采样…」。
            // 判据用 `_nSparkWarming`（回填结果），不看 `SampleCount` ——
            // 后者 0.2s 变一次、这里 0.5s 刷一次，直接用会闪。
            if (_nSparkWarmingText != null)
            {
                bool warming = !empty && _nSparkWarming;
                if (_nSparkWarmingText.gameObject.activeSelf != warming)
                    _nSparkWarmingText.gameObject.SetActive(warming);
            }

            // ---- 类型 Top3 ----
            Dictionary<string, int> byType = new Dictionary<string, int>(16, StringComparer.Ordinal);
            Dictionary<int, float> byFloor = new Dictionary<int, float>(4);
            _nTopBuf.Clear();

            IReadOnlyList<TwinAnchor> all = TwinDeviceStats.AllDevices;

            if (all != null)
            {
                for (int i = 0; i < all.Count; i++)
                {
                    TwinAnchor a = all[i];
                    if (!TwinDeviceStats.IsActiveDevice(a)) continue;

                    string type = TwinCode.TypeName(a.Category);
                    if (string.IsNullOrEmpty(type)) type = "其他";

                    int n;
                    byType.TryGetValue(type, out n);
                    byType[type] = n + 1;

                    // 【同一趟遍历】楼层聚合与 Top3 都用实时功率，
                    // 和全屏/数据监测页同一个 `LivePowerOf` 口径 —— 三处数字必须相等。
                    float live = TwinPowerEstimate.LivePowerOf(a);

                    TwinRoomInfo room = TwinSiteLayout.Find(a.Zone);
                    int floor = room != null ? room.Floor : 0;
                    float fw;
                    byFloor.TryGetValue(floor, out fw);
                    byFloor[floor] = fw + live;

                    // ---- 榜单口径：与全屏 `RefreshStatsPage` 完全一致 ----
                    //
                    // 【2026-11-02 修·两态不同步】此前这里**无条件** `_nTopBuf.Add`，
                    // 而全屏早在 P5 就改成了「只收实测」（`HasPowerReading`）。
                    // 于是同一时刻两个形态的榜单不一样：
                    //   · 全屏 Top5：各房间的灯 / 空调 / 电脑（真读数）
                    //   · 窄态 Top3：暖气 150 W（`RatedPower × 0.15` 编出来的，无测点）
                    // （门原先也在这份假榜单里，2026-11-02 起 `RatedPower("Door")` 改成 0，
                    //   它连 4.5W 都没了 —— 见 `RatedPower` 里那一整段注释。）
                    // 主理人定过规矩「两态本质是一个面板，数字必须相同」，
                    // 这是硬缺陷，不是"窄态放不下所以精简"。
                    //
                    // 判定复用 `TwinPowerEstimate.HasPowerReading`，不另写一份 ——
                    // 它内部也已经含了"断电不算读数"（见该方法 2026-11-02 的注释）。
                    if (TwinPowerEstimate.HasPowerReading(a))
                        _nTopBuf.Add(new KeyValuePair<TwinAnchor, float>(a, live));
                }
            }

            // ---- 类型分布：donut 分段 + 图例 ----
            List<KeyValuePair<string, int>> list = new List<KeyValuePair<string, int>>(byType);
            list.Sort(delegate (KeyValuePair<string, int> x, KeyValuePair<string, int> y)
            {
                return y.Value.CompareTo(x.Value);
            });

            // 【合并规则必须和全屏一致】前 4 类 + "其他"。
            //
            // 不合并的话：70 台里只画前 5 类的 62 台，剩下 8 台"消失"，
            // 圆环加起来不是 100% —— 两态数字对不上，评委一眼能看出。
            // 这里直接把全屏 `RefreshStatsPage` 的那 8 行抄过来，不另写一套。
            _nTypeBuf.Clear();
            int keep = NTypeMax - 1;
            for (int i = 0; i < keep && i < list.Count; i++) _nTypeBuf.Add(list[i]);

            if (list.Count > keep)
            {
                int rest = 0;
                for (int i = keep; i < list.Count; i++) rest += list[i].Value;
                if (rest > 0) _nTypeBuf.Add(new KeyValuePair<string, int>("其他", rest));
            }

            float cursor = 0f;
            for (int i = 0; i < NTypeMax; i++)
            {
                bool on = i < _nTypeBuf.Count && !empty;
                // 【一个都不能漏】这五类元素必须在这里**同一处** SetActive。
                //
                // ── 为什么改成逐个独立判（2026-11-02）─────────────────────
                // 原写法是「只要 `_nTypeName[i]` 的 activeSelf 变了，就顺手把
                // 另外四个一起切」。这有个隐性 bug：首次刷新时五个件默认都是
                // active=true，若 `on` 也是 true，则 `activeSelf != on` 不成立、
                // 整块跳过 —— 于是**新增的 `_nTypeCount` 永远不会被激活**，
                // 表现为"其他四段都在、就是没有台数"（地图上看起来像漏画一列）。
                // 现在每个件各判各的，与全屏 `RefreshStatsPage` 同一套写法。
                if (_nTypeSeg[i] != null && _nTypeSeg[i].gameObject.activeSelf != on)
                    _nTypeSeg[i].gameObject.SetActive(on);
                if (_nTypeDot[i] != null && _nTypeDot[i].gameObject.activeSelf != on)
                    _nTypeDot[i].gameObject.SetActive(on);
                if (_nTypeName[i] != null && _nTypeName[i].gameObject.activeSelf != on)
                    _nTypeName[i].gameObject.SetActive(on);
                if (_nTypeCount[i] != null && _nTypeCount[i].gameObject.activeSelf != on)
                    _nTypeCount[i].gameObject.SetActive(on);
                if (_nTypeVal[i] != null && _nTypeVal[i].gameObject.activeSelf != on)
                    _nTypeVal[i].gameObject.SetActive(on);
                if (!on) continue;

                float ratio = _nTypeBuf[i].Value / (float)total;

                // 分段色 / 图例色同一个色号，统一走 `TwinTheme.ChartSeqAt`（唯一色板）
                Color c = NDonutColor(i);

                Image seg = _nTypeSeg[i];
                seg.color = c;
                seg.fillAmount = ratio;
                // 逆时针累加起点：第 i 段的扇区从 cursor 处开始，首尾相接不重叠
                seg.rectTransform.localRotation = Quaternion.Euler(0f, 0f, -360f * cursor);
                cursor += ratio;

                if (_nTypeDot[i] != null) _nTypeDot[i].SetFill(c, c);

                SetText(_nTypeName[i], _nTypeBuf[i].Key);
                // 三段式的后两段：台数单独一列、占比单独一列。
                // 占比保留 1 位小数，**和全屏同一个精度** —— 精度也是口径。
                SetText(_nTypeCount[i], _nTypeBuf[i].Value + " 台");
                SetText(_nTypeVal[i], (ratio * 100f).ToString("0.0") + "%");
            }

            if (_nDonutCenter != null)
                _nDonutCenter.text = total > 0 ? total.ToString() : "—";

            // ================================================================ 楼层能耗（按功率降序）
            //
            // 图纸 §2.5 的排序规范：条形图按值降序，读者不用脑内排序。
            // 原来全屏按楼层号排（室外 0 排最前），这一版两态统一改成降序。
            List<KeyValuePair<int, float>> floors = new List<KeyValuePair<int, float>>(byFloor);
            floors.Sort(delegate (KeyValuePair<int, float> x, KeyValuePair<int, float> y)
            {
                return y.Value.CompareTo(x.Value);
            });

            float maxFloor = floors.Count > 0 ? Mathf.Max(1f, floors[0].Value) : 1f;

            for (int i = 0; i < NFloorRows; i++)
            {
                bool on = i < floors.Count && !empty;
                if (_nFloorName[i] != null && _nFloorName[i].gameObject.activeSelf != on)
                {
                    _nFloorName[i].gameObject.SetActive(on);
                    _nFloorVal[i].gameObject.SetActive(on);
                    _nFloorPct[i].gameObject.SetActive(on);
                    _nFloorBar[i].gameObject.SetActive(on);
                    if (_nFloorTrack[i] != null) _nFloorTrack[i].gameObject.SetActive(on);
                }
                if (!on) continue;

                int f = floors[i].Key;
                SetText(_nFloorName[i], f <= 0 ? "室外" : f + "F");

                float ratio = floors[i].Value / maxFloor;
                _nFloorBar[i].rectTransform.sizeDelta = new Vector2(Mathf.Max(3f, 236f * ratio), 6f);

                SetText(_nFloorVal[i], (floors[i].Value / 1000f).ToString("0.0") + " kW");

                float pct = _nPowerSum > 0.01f ? floors[i].Value / _nPowerSum * 100f : 0f;
                SetText(_nFloorPct[i], pct.ToString("0") + "%");
            }

            // ================================================================ Top3 设备（3s 重排节流）
            //
            // 【为什么 3 秒才重排一次】Top 按实时功率排，而功率每帧都在微动。
            // 不节流的话第 2/3 名每秒换十几次位，看起来像界面在抖。
            // 3 秒既能反映设备开关，又不会让人眼跟着跳。
            if (Time.unscaledTime >= _nTopResortAt)
            {
                _nTopResortAt = Time.unscaledTime + 3f;

                _nTopBuf.Sort(delegate (KeyValuePair<TwinAnchor, float> x, KeyValuePair<TwinAnchor, float> y)
                {
                    return y.Value.CompareTo(x.Value);
                });

                _nTopCache.Clear();
                for (int i = 0; i < NTopRows && i < _nTopBuf.Count; i++)
                    _nTopCache.Add(_nTopBuf[i]);
            }

            float maxTop = _nTopCache.Count > 0 ? Mathf.Max(1f, _nTopCache[0].Value) : 1f;

            // 【2026-11-03 P8 修·白框】`empty` 判的是"设备数为 0"，
            // 拉闸时 70 台仍在册 → false；但 `_nTopCache` 被 `HasPowerReading` 清空。
            // 两个条件都对上才该有内容，否则显示块内空状态。
            //
            // ⚠️ 只在 `!empty` 时显示块内空状态：设备数真的是 0 时，
            //    由整页 `_nEmpty` 一句「暂无数据」覆盖，这里再来一句就是重复。
            //    所以 `noRank` 取 `!empty && _nTopCache.Count == 0`。
            //    全屏那边四块是并列的、没有整页盖层，所以它的第 4 块用 `empty || noRank`。
            bool noRank = !empty && _nTopCache.Count == 0;
            if (_nTopEmpty != null && _nTopEmpty.gameObject.activeSelf != noRank)
                _nTopEmpty.gameObject.SetActive(noRank);

            for (int i = 0; i < NTopRows; i++)
            {
                bool on = i < _nTopCache.Count && !empty;
                if (_nTopName[i] != null && _nTopName[i].gameObject.activeSelf != on)
                {
                    _nTopRank[i].gameObject.SetActive(on);
                    _nTopName[i].gameObject.SetActive(on);
                    _nTopVal[i].gameObject.SetActive(on);
                    _nTopPct[i].gameObject.SetActive(on);
                    _nTopBar[i].gameObject.SetActive(on);
                    if (_nTopTrack[i] != null) _nTopTrack[i].gameObject.SetActive(on);
                }
                if (!on) continue;

                KeyValuePair<TwinAnchor, float> kv = _nTopCache[i];
                SetText(_nTopName[i], TwinCode.Short(kv.Key.TwinId));

                float ratio = kv.Value / maxTop;
                _nTopBar[i].rectTransform.sizeDelta = new Vector2(Mathf.Max(3f, 168f * ratio), 6f);

                SetText(_nTopVal[i], (kv.Value / 1000f).ToString("0.0") + " kW");

                float pct = _nPowerSum > 0.01f ? kv.Value / _nPowerSum * 100f : 0f;
                SetText(_nTopPct[i], pct.ToString("0") + "%");
            }

            // ================================================================ 数据源健康度（P12 补齐）
            //
            // 【为什么放在这里、不放 `UpdateNarrowStats`】后者是 5Hz（0.2s），
            // 而这张卡片每次 Tick 要 `FindObjectOfType` 两趟 + 遍历 Sources。
            // 一行"发了几条 / 丢了几条"的数字，0.5s 刷新完全够，
            // 5Hz 刷是纯浪费 —— 而且 FindObjectOfType 是场景全遍历，不便宜。
            if (_nSourceHealth != null) _nSourceHealth.Tick();
        }

        /// <summary>
        /// 窄态 KPI 一格：数值大字 + 单位小字，单位位置按数值实际宽度跟随。
        /// 与全屏 `TwinFullLayer.SetKpi` 同一套逻辑、只换卡宽 —— 两态必须一致。
        /// </summary>
        private void SetNKpi(int i, string value, string unit)
        {
            Text v = _nKpiValue[i];
            if (v == null) return;

            SetText(v, value);

            Text u = _nKpiUnit[i];
            if (u == null) return;

            bool hasUnit = !string.IsNullOrEmpty(unit);
            if (u.gameObject.activeSelf != hasUnit) u.gameObject.SetActive(hasUnit);
            if (!hasUnit) return;

            SetText(u, unit);

            // 数值宽度 + 4px 间隙；再夹到卡内右缘（210 - 12 留白 - 单位自身宽）
            float x = 12f + Mathf.Ceil(v.preferredWidth) + 4f;
            float rightLimit = NKpiW - 12f - Mathf.Ceil(u.preferredWidth);
            x = Mathf.Min(x, Mathf.Max(12f, rightLimit));

            u.rectTransform.anchoredPosition = new Vector2(x, u.rectTransform.anchoredPosition.y);
        }

        /// <summary>
        /// 窄态 donut 配色。**必须和全屏 `TwinFullLayer.Pages.DonutColor` 完全一致**，
        /// 否则同一个"设备类型分布"在两态下颜色不同 —— 那正是主理人反复强调的
        /// "他们本质还是一个面板"。
        ///
        /// ── 2026-11-02：从"两份手写值"改成"单向转发" ─────────────────
        /// 原来这里是一个 5 分支 switch，值抄自全屏的 `DonutPalette`，
        /// 靠注释「必须保持一致」提醒后人。问题在于：**注释拦不住改错**，
        /// 只要有人调了全屏那一份而忘了这里，两态就分家了。
        ///
        /// 现在色板只有一份 —— `TwinTheme` 里的 private 数组 + `ChartSeqAt(i)` 读口，
        /// 全屏的 `DonutColor` 和这里都只是转发。值无处可漂，一致性由编译器保证。
        ///
        /// 【为什么可以放 TwinTheme 了】上次没放是因为担心"UI 结构泄漏到主题层"，
        /// 但序列色板（ChartSeq1~4 已在那儿）本来就是主题的一部分，
        /// 段数 5 是"donut 最多几段"这个图表约定，不是页面布局 —— 放主题层不越界。
        /// </summary>
        private static Color NDonutColor(int i)
        {
            return TwinTheme.ChartSeqAt(i);
        }

        /// <summary>
        /// 滚动条跟随：按 ScrollRect 的归一化位置把指示条往下挪。
        ///
        /// 【为什么手算而不用 Unity 的 Scrollbar】`Scrollbar` 需要
        /// HandleRect / Direction / Value 一整套接线，而这里只要一根
        /// "能看出还有多少没看到"的 4px 提示条 —— 一根 Image 改 anchoredPosition
        /// 更简单，也不会和 ScrollRect 的 value 双向绑定打架。
        /// </summary>
        private void UpdateStatsBar()
        {
            if (_nStatsBar == null || _nStatsContent == null) return;

            float travel = NStatsContentH - NStatsViewH;   // 可滚距离
            if (travel <= 0f)
            {
                // 内容不足一屏（比如改成极矮版式时）：整条长满，不留残头
                if (!Mathf.Approximately(_nStatsBarH, NStatsViewH))
                {
                    _nStatsBarH = NStatsViewH;
                    _nStatsBar.rectTransform.sizeDelta = new Vector2(NStatsBarW, _nStatsBarH);
                }
                _nStatsBar.rectTransform.anchoredPosition = new Vector2(RightW - NStatsBarW, 0f);
                return;
            }

            // 指示条长度 = 视口/内容 比例。这是"还剩多少没看到"的直接视觉编码，
            // 比固定长度更准确（固定长度在 484/672 这种轻微溢出时会让人误以为还有很多）。
            float h = Mathf.Clamp(NStatsViewH * NStatsViewH / NStatsContentH, 24f, NStatsViewH);
            if (!Mathf.Approximately(_nStatsBarH, h))
            {
                _nStatsBarH = h;
                _nStatsBar.rectTransform.sizeDelta = new Vector2(NStatsBarW, _nStatsBarH);
            }

            // content 的 y 位移是负向累计（向下滚 = y 变大）
            float scrolled = Mathf.Clamp(-_nStatsContent.anchoredPosition.y, 0f, travel);
            float t = scrolled / travel;

            _nStatsBar.rectTransform.anchoredPosition =
                new Vector2(RightW - NStatsBarW, -(NStatsViewH - h) * t);
        }

        /// <summary>
        /// 【P5-1 P0-2】进窄态统计页时重置曲线，每次进都重建。
        ///
        /// 与全屏 `ResetStatsSpark()` 是同一件事、同一套理由：
        /// `TwinSparkline` 的 span 按"缓冲里最早点到现在"算，留着上次的旧点
        /// 会让新采的点全挤在右端（P0-2 截图的直接成因）。
        /// 重置后立刻从 `TwinMetricHistory` 那条常驻序列整条回填，
        /// `t0` 才是"历史里最早那个点"，跨度才对。
        /// </summary>
        private void ResetNarrowSpark()
        {
            if (_nSpark != null) _nSpark.Clear();
            _nSparkHistoryLoaded = false;
            _nSparkWarming = false;
            _nSparkLo = -1f;
            _nSparkHi = -1f;
            _nSparkNextAt = 0f;     // 下一帧立刻采一次
            _nPowerSum = 0f;
        }

        /// <summary>
        /// 窄态统计的 5Hz 采样 + 0.5s 刷新。
        ///
        /// 和 L2 大屏同一套口径（实时 `Power` 求和），但**独立缓冲** ——
        /// 两处可能不同时可见，共享缓冲会让"切页面时曲线跳一下"。
        /// 只在统计页签激活时跑。
        /// </summary>
        private void UpdateNarrowStats()
        {
            if (!_built || _selected != 4) return;
            if (_nSpark == null) return;

            float now = Time.unscaledTime;

            if (now >= _nSparkNextAt)
            {
                _nSparkNextAt = now + 0.2f;

                IReadOnlyList<TwinAnchor> all = TwinDeviceStats.AllDevices;
                float sum = 0f;

                if (all != null)
                {
                    for (int i = 0; i < all.Count; i++)
                    {
                        TwinAnchor a = all[i];
                        if (a == null || !TwinDeviceStats.IsActiveDevice(a)) continue;
                        sum += TwinPowerEstimate.LivePowerOf(a);   // 与全屏同一口径
                    }
                }

                _nPowerSum = sum;

                // 【P5-1 P0-2】不再用当前值伪造 60 点历史（旧写法已删）。
                //
                // 病根与全屏完全相同：拿一个当前值往回填 60 个点，画出的是
                // 一段完美平线 —— 那段历史根本不存在。而且 `_nSparkHistoryLoaded`
                // 只做一次，切走再切回时缓冲里只剩几个新点，
                // `TwinSparkline` 的 span 按"最早点到现在"算 → 新点全挤右端。
                //
                // 现在读 `TwinMetricHistory` 里那条常驻采样的全楼总功率序列。
                // 它和界面开不开无关，所以切走 30s 再回来，那 30s 的曲线也在。
                if (!_nSparkHistoryLoaded)
                {
                    _nSparkHistoryLoaded = true;
                    _nSparkWarming = !TwinMetricHistory.FillAggregatePower(_nSpark);
                }

                _nSpark.Push(now, sum);

                // 【2026-11-02 修】warming 必须能自己解除，与全屏同一套规则。
                //
                // 原写法把 `_nSparkWarming = !haveHistory;` 放在 if 外面，每 0.2s 执行；
                // 而 `haveHistory` 只在首次回填那一帧有值，之后恒 false，
                // 于是从第二帧起 warming 被永久置回 true ——
                // 曲线画出来了，「正在采样…」却一直盖在上面（主理人截图实测）。
                if (_nSparkWarming && _nSpark.SampleCount >= 3) _nSparkWarming = false;

                // 点数上限兜底：超过一个满窗就整条重读，别让 Push 里的 O(n) 搬移长跑
                if (_nSpark.SampleCount > 360)
                {
                    TwinMetricHistory.FillAggregatePower(_nSpark);
                    // 不再覆盖 warming：走到这里点数必然 >360，不可能"不足 2 点"
                }

                if (_nSparkLo < 0f || sum < _nSparkLo) _nSparkLo = sum;
                if (_nSparkHi < 0f || sum > _nSparkHi) _nSparkHi = sum;
                _nSparkLo = Mathf.Lerp(_nSparkLo, sum, 0.02f);
                _nSparkHi = Mathf.Lerp(_nSparkHi, sum, 0.02f);

                float span = Mathf.Max(_nSparkHi - _nSparkLo, Mathf.Max(0.5f, _nSparkHi * 0.05f));
                float pad = span * 0.25f;
                _nSpark.SetRange(_nSparkLo - pad, _nSparkHi + pad);
            }

            // 注意：这里**不碰 `_nextRefresh`**。它是主 `Update` 的 0.5s 节流阀，
            // 这里再改会让其他页的刷新节奏乱掉。`RefreshStats` 由主 `Update` 的 switch 调。
        }

        // ---------------------------------------------------------------- 设备管理

        private void BuildDevice(RectTransform root)
        {
            // ---- 搜索筛选条 y=60 高 40：搜索框 240×32 R8 + 楼层 chip ×3（高 28 R14）----
            TwinRoundedRect search = TwinUIFactory.Rounded(root, "SearchBox", TwinTheme.R8,
                TwinTheme.Track, TwinTheme.Track, TwinTheme.Border, TwinTheme.StrokeHair.Width, false);
            TwinUIFactory.Place(search.rectTransform, RightX, DevSearchY, 240f, 32f);

            Text ph = TwinUIFactory.Label(search.rectTransform, "Placeholder", TwinTheme.FsCaption,
                TextAnchor.MiddleLeft, TwinTheme.TextMuted);
            TwinUIFactory.Place(ph.rectTransform, 12f, 0f, 216f, 32f);
            ph.text = "搜索编号 / 名称";
            ph.raycastTarget = false;

            // 楼层 chip ×3：全部 / 1F / 2F，高 28 R14
            string[] chips = { "全部", "1F", "2F" };
            for (int i = 0; i < chips.Length; i++)
            {
                // 【C-30】原来 raycast:false + 无 Button —— 长得像按钮，点了一点反应都没有
                // （主理人 play 实测）。现在补成真按钮：raycast 打开 + 挂 Button。
                TwinRoundedRect chip = TwinUIFactory.Rounded(root, "Chip_" + i, 14f,
                    TwinTheme.Track, TwinTheme.Track,
                    Color.clear, TwinTheme.StrokeHair.Width, true);
                TwinUIFactory.Place(chip.rectTransform, RightX + 248f + i * 52f, DevSearchY + 2f, 48f, 28f);
                _chipBg[i] = chip;

                Text ct = TwinUIFactory.Label(chip.rectTransform, "T", TwinTheme.FsMicro,
                    TextAnchor.MiddleCenter, TwinTheme.TextMuted);
                TwinUIFactory.Stretch(ct.rectTransform);
                ct.text = chips[i];
                ct.raycastTarget = false;
                _chipText[i] = ct;

                int idx = i;
                Button cb = chip.gameObject.AddComponent<Button>();
                cb.transition = Selectable.Transition.None;
                cb.onClick.AddListener(delegate { OnFloorChipClicked(idx); });
            }
            RefreshFloorChips();

            // ---- 计数行 y=108 高 18 ----
            _devCount = TwinUIFactory.Label(root, "DevCount", TwinTheme.FsCaption,
                TextAnchor.MiddleLeft, TwinTheme.TextMuted);
            TwinUIFactory.Place(_devCount.rectTransform, RightX, DevCountY, RightW, 18f);

            // ---- 表头 y=134 高 20：编号 150 / 名称 自适应 / 位置 90 / 状态 56 / 开关 40 ----
            string[] heads = { "编号", "名称", "位置", "状态", "" };
            float[] hx = { 0f, 158f, 268f, 358f, 404f };
            float[] hw = { 150f, 104f, 86f, 44f, 36f };
            for (int i = 0; i < heads.Length; i++)
            {
                Text h = TwinUIFactory.Label(root, "Th_" + i, TwinTheme.FsMicro,
                    TextAnchor.MiddleLeft, TwinTheme.TextMuted);
                TwinUIFactory.Place(h.rectTransform, RightX + hx[i], DevHeadY, hw[i], 20f);
                h.text = heads[i];
            }

            // 表头下的分隔线
            Image line = TwinUIFactory.Block(root, "ThLine", TwinTheme.Divider, false);
            TwinUIFactory.Place(line.rectTransform, RightX, DevHeadY + 20f, RightW, 1f);

            // ---- 【C-31】台账列表：改成可滚动 ----
            //
            // 【为什么必须改】原来固定 7 行、没有 ScrollRect ——
            // 53 台设备只能看到前 7 台，剩下的永远看不见也滑不到
            // （主理人 2026-10-31 二次反馈"窄态也不能滑动"）。
            // 结构和电力页 / 全屏台账完全一致：Scroll → Viewport(RectMask2D) → Content。
            GameObject scrollGo = TwinUIFactory.Node("DevScroll", root);
            RectTransform scrollRt = (RectTransform)scrollGo.transform;
            TwinUIFactory.Place(scrollRt, RightX, DevListY, RightW, DevListViewH);

            // ScrollRect 的滚轮事件沿射线命中的 Graphic 派发；scrollRt 没有 Graphic，
            // 必须铺一层全透明垫片，否则滚轮收不到（和电力树 HitArea 同一个套路）。
            Image scrollHit = TwinUIFactory.Block(scrollRt, "Hit", new Color(0f, 0f, 0f, 0f), true);
            TwinUIFactory.Stretch(scrollHit.rectTransform);

            GameObject viewGo = TwinUIFactory.Node("Viewport", scrollRt);
            RectTransform viewRt = (RectTransform)viewGo.transform;
            TwinUIFactory.Stretch(viewRt);
            viewGo.AddComponent<RectMask2D>();

            GameObject contentGo = TwinUIFactory.Node("Content", viewRt);
            RectTransform contentRt = (RectTransform)contentGo.transform;
            contentRt.anchorMin = new Vector2(0f, 1f);
            contentRt.anchorMax = new Vector2(1f, 1f);
            contentRt.pivot = new Vector2(0.5f, 1f);
            contentRt.anchoredPosition = Vector2.zero;
            contentRt.sizeDelta = new Vector2(0f, 0f);
            _devContent = contentRt;

            ScrollRect dsr = scrollGo.AddComponent<ScrollRect>();
            dsr.viewport = viewRt;
            dsr.content = contentRt;
            dsr.horizontal = false;
            dsr.vertical = true;
            dsr.movementType = ScrollRect.MovementType.Clamped;
            dsr.scrollSensitivity = 20f;

            for (int i = 0; i < DevRows; i++)
            {
                float y = i * DevRowH;

                RectTransform row = TwinUIFactory.Node("DevRow_" + i, contentRt).transform as RectTransform;
                TwinUIFactory.Place(row, 0f, y, RightW, DevRowH);
                _devRow[i] = row;

                // 【C-30】选中高亮层：铺满整行的圆角底，默认全透明。
                // 必须放在状态条/文字**之前**创建 —— UGUI 靠 sibling 顺序决定遮挡，
                // 后建的会盖住先建的，铺在文字上面就把字盖没了。
                TwinRoundedRect rowBg = TwinUIFactory.Rounded(row, "SelBg", TwinTheme.R8,
                    Color.clear, Color.clear, Color.clear, 0f, false);
                TwinUIFactory.Stretch(rowBg.rectTransform);
                _devRowBg[i] = rowBg;

                // 左 3px 状态条
                Image bar = TwinUIFactory.Block(row, "Bar", TwinTheme.StatusUnknown, false);
                TwinUIFactory.Place(bar.rectTransform, 0f, (DevRowH - 24f) * 0.5f,
                    TwinTheme.BarStatusWidth, 24f);
                _devBar[i] = bar;

                _devId[i] = MakeCell(row, "Id", 12f, 150f, TwinTheme.FsBody, TwinTheme.TextPrimary);
                _devName[i] = MakeCell(row, "Name", 170f, 104f, TwinTheme.FsCaption, TwinTheme.TextSecondary);
                _devPos[i] = MakeCell(row, "Pos", 274f, 86f, TwinTheme.FsCaption, TwinTheme.TextMuted);
                _devTag[i] = MakeCell(row, "Tag", 358f, 44f, TwinTheme.FsMicro, TwinTheme.TextMuted);

                // 【C-30】整行可点 → 选中该设备（走 TwinPicker.Select，C-27 转向自然生效）。
                // 行本身是纯 Node（没有 Graphic），射线打不到它 —— 必须铺一层
                // **完全透明但 raycastTarget=true** 的垫片，否则 Button 永远收不到点击。
                // 这和电力页 ScrollRect 的 HitArea 是同一个套路。
                Image hit = TwinUIFactory.Block(row, "Hit", new Color(0f, 0f, 0f, 0f), true);
                TwinUIFactory.Stretch(hit.rectTransform);

                int devIdx = i;
                Button rowBtn = row.gameObject.AddComponent<Button>();
                rowBtn.transition = Selectable.Transition.None;
                rowBtn.onClick.AddListener(delegate { OnDeviceRowClicked(devIdx); });

                row.gameObject.SetActive(false);
            }
        }

        /// <summary>列表单元格：一行一个 Text，垂直居中，超宽截断。</summary>
        private static Text MakeCell(RectTransform row, string name, float x, float w, int size, Color color)
        {
            Text t = TwinUIFactory.Label(row, name, size, TextAnchor.MiddleLeft, color);
            TwinUIFactory.Place(t.rectTransform, x, 0f, w, DevRowH);
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            return t;
        }

        // ---------------------------------------------------------------- 电力管理

        private void BuildPower(RectTransform root)
        {
            // ---- 三级树 y=60 高 360，内部滚动 ----
            // 20 节点 × 平均 30 = 600 > 360，必须滚（规格 §3.3）。结构：Scroll → Viewport(裁) → Content
            GameObject scrollGo = TwinUIFactory.Node("PwrScroll", root);
            RectTransform scrollRt = (RectTransform)scrollGo.transform;
            TwinUIFactory.Place(scrollRt, RightX, PwrTreeY, RightW, PwrTreeH);

            // 【滚轮为什么点了没反应】ScrollRect 实现了 IScrollHandler，但滚轮事件是
            // **沿着射线命中的 Graphic 派发**的。scrollRt 上没有任何 Graphic，
            // 鼠标悬停时射线打不到它 → ScrollRect 收不到 OnScroll → 滑条不动。
            // 这里铺一层完全透明、但 raycastTarget = true 的 Image 当"事件接收垫"。
            Image scrollHit = TwinUIFactory.Block(scrollRt, "HitArea", new Color(0f, 0f, 0f, 0f), true);
            TwinUIFactory.Stretch(scrollHit.rectTransform);

            GameObject viewGo = TwinUIFactory.Node("Viewport", scrollRt);
            RectTransform viewRt = (RectTransform)viewGo.transform;
            TwinUIFactory.Stretch(viewRt);
            viewGo.AddComponent<RectMask2D>();

            GameObject contentGo = TwinUIFactory.Node("Content", viewRt);
            RectTransform contentRt = (RectTransform)contentGo.transform;
            contentRt.anchorMin = new Vector2(0f, 1f);
            contentRt.anchorMax = new Vector2(1f, 1f);
            contentRt.pivot = new Vector2(0.5f, 1f);
            contentRt.anchoredPosition = Vector2.zero;
            contentRt.sizeDelta = new Vector2(0f, 0f);

            ScrollRect sr = scrollGo.AddComponent<ScrollRect>();
            sr.viewport = viewRt;
            sr.content = contentRt;
            sr.horizontal = false;
            sr.vertical = true;
            sr.movementType = ScrollRect.MovementType.Clamped;
            sr.scrollSensitivity = 20f;

            // 滚动条：宽 4、白@0.08（规格 §3.3「滚动条可见」）。
            // 只做视觉提示，不做可拖拽手柄 —— 本轮只验"能看出可滚"，交互细化留 P29-5。
            Image bar = TwinUIFactory.Block(scrollRt, "Scrollbar", TwinTheme.WithAlpha(Color.white, 0.08f), false);
            TwinUIFactory.Place(bar.rectTransform, RightW - 4f, 0f, 4f, PwrTreeH);

            BuildPowerRows(contentRt);

            // 【C-30】记住 content，供合闸后重画（原来 BuildPowerRows 是 static 且只跑一次，
            // 点了合闸状态永远不会更新 —— 这是"点了没反应"的另一半原因）
            _pwrContent = contentRt;

            // ---- 影响面卡 y=428 高 80 ----
            TwinRoundedRect card = TwinUIFactory.Rounded(root, "ImpactCard", TwinTheme.R12,
                TwinTheme.CardTop, TwinTheme.CardBottom, TwinTheme.Border, TwinTheme.StrokeHair.Width, false);
            TwinUIFactory.Place(card.rectTransform, RightX, PwrImpactY, RightW, PwrImpactH);

            Text title = TwinUIFactory.Label(card.rectTransform, "T", TwinTheme.FsCaption,
                TextAnchor.UpperLeft, TwinTheme.TextMuted);
            TwinUIFactory.Place(title.rectTransform, 16f, 10f, 200f, 16f);
            title.text = "影响面";

            Text main = TwinUIFactory.Label(card.rectTransform, "Main", TwinTheme.FsBody,
                TextAnchor.UpperLeft, TwinTheme.TextPrimary);
            TwinUIFactory.Place(main.rectTransform, 16f, 28f, RightW - 32f, 20f);
            main.text = "选中回路后显示受影响的设备数与功率合计";

            Text sub = TwinUIFactory.Label(card.rectTransform, "Sub", TwinTheme.FsMicro,
                TextAnchor.UpperLeft, TwinTheme.TextMuted);
            TwinUIFactory.Place(sub.rectTransform, 16f, 50f, RightW - 32f, 24f);
            sub.horizontalOverflow = HorizontalWrapMode.Wrap;
            sub.text = "点左侧任意回路查看分级停电的影响范围";
        }

        /// <summary>
        /// 电路树行：按 <see cref="TwinCircuitNode.Level"/> 缩进 16/级，
        /// 楼栋 36 / 楼层 32 / 房间 28（规格 §3.3）。数据直接读 <see cref="TwinCircuit.All"/>。
        ///
        /// 【C-30 去掉了 static】原来它是静态方法，而合闸回调必须是实例方法
        /// （要读 `_pwrContent`、调 `RepaintPowerTree`）—— 静态方法里没法挂实例回调。
        /// </summary>
        private void BuildPowerRows(RectTransform content)
        {
            IReadOnlyList<TwinCircuitNode> nodes = TwinCircuit.All;
            float y = 0f;

            // 【C-30】把回路 Id 记到行节点上（用 name 携带），点行时反查是哪条回路。
            // 为什么不用下标：TwinCircuit.All 的顺序将来可能变（增删节点），
            // 下标一旦错位就会"点了 1F 实际断了 2F"——电力操作错位的代价太大，不能赌。
            for (int i = 0; i < nodes.Count; i++)
            {
                TwinCircuitNode n = nodes[i];
                float h = n.Level == 0 ? 36f : (n.Level == 1 ? 32f : 28f);

                RectTransform row = TwinUIFactory.Node("Pwr_" + i, content).transform as RectTransform;
                TwinUIFactory.Place(row, n.Level * 16f, y, 440f - n.Level * 16f, h);

                // 标题 15 / 13 / 11 按层级
                int size = n.Level == 0 ? TwinTheme.FsBodyStrong
                         : (n.Level == 1 ? TwinTheme.FsBody : TwinTheme.FsCaption);
                Color col = n.Level == 0 ? TwinTheme.TextPrimary
                          : (n.Level == 1 ? TwinTheme.TextSecondary : TwinTheme.TextMuted);

                Text t = TwinUIFactory.Label(row, "Title", size, TextAnchor.MiddleLeft, col);
                TwinUIFactory.Place(t.rectTransform, 0f, 0f, 220f, h);
                t.text = n.Title;
                t.raycastTarget = false;

                // 通断胶囊：高 22，**右对齐到绝对 380**。
                // ⚠️ 不能写行内固定 x —— 行本身有 `Level*16` 缩进，胶囊会跟着右移，
                // 三层节点看起来就是"一圈按钮歪着排"（P29-4 P0-2 反馈）。
                float pillX = 440f - n.Level * 16f - 52f - 8f;

                // 【P3-2】三态：自己分闸 / 上级断电 / 正常通电。
                // 原来只读 n.On，1F 断电后子房间仍显示「合闸」—— 面板和场景对不上。
                bool parentOn = TwinCircuit.ParentPowered(n.Id);
                bool selfOn = n.On;

                string label;
                Color state;
                if (!selfOn)
                {
                    label = "分闸";
                    state = TwinTheme.StatusAlarm;
                }
                else if (!parentOn)
                {
                    label = "无电";
                    state = TwinTheme.TextMuted;
                }
                else
                {
                    label = "合闸";
                    state = TwinTheme.StatusNormal;
                }

                // SBO 流程中：整行标警示色（弹窗正在操作这条回路）
                bool armed = TwinSystemState.IsCircuitBusy(n.Id, _sbo);
                if (armed) state = TwinTheme.StatusWarning;

                // 【C-30】原来 raycast:false + 无 Button —— 合闸胶囊点不动。
                TwinRoundedRect pill = TwinUIFactory.Rounded(row, "Pill", 11f,
                    TwinTheme.WithAlpha(state, 0.16f), TwinTheme.WithAlpha(state, 0.16f),
                    armed ? TwinTheme.WithAlpha(state, 0.7f) : Color.clear,
                    armed ? 1f : 0f, true);
                TwinUIFactory.Place(pill.rectTransform, pillX, (h - 22f) * 0.5f, 52f, 22f);

                Text pt = TwinUIFactory.Label(pill.rectTransform, "P", TwinTheme.FsMicro,
                    TextAnchor.MiddleCenter, state);
                TwinUIFactory.Stretch(pt.rectTransform);
                // 文案沿用既有「合闸 / 分闸」——优化单 §5 第 8 条明确列为"做对了不要动"。
                // 【P3】不再有「确认?」这个中间态：两步确认改由 SBO 弹窗承担。
                pt.text = label;
                pt.raycastTarget = false;

                string circuitId = n.Id;
                Button pb = pill.gameObject.AddComponent<Button>();
                pb.transition = Selectable.Transition.None;
                pb.onClick.AddListener(delegate { OnCircuitClicked(circuitId); });

                y += h;
            }

            // Content 高度 = 所有行总高（ScrollRect 靠它算可滚范围）
            content.sizeDelta = new Vector2(0f, y);
        }

        // ---------------------------------------------------------------- 告警中心

        private void BuildAlarm(RectTransform root)
        {
            // ---- 分段切换 y=60 高 32，两段等分 220 ----
            // 【C-30】原来 segBox / segOn 都是 raycast:false，两个页签点不动。
            // segBox 铺满整条、raycast 打开当事件垫；segOn 是"选中态底色"，只是视觉层。
            TwinRoundedRect segBox = TwinUIFactory.Rounded(root, "SegBox", TwinTheme.R8,
                TwinTheme.Track, TwinTheme.Track, TwinTheme.Border, TwinTheme.StrokeHair.Width, true);
            TwinUIFactory.Place(segBox.rectTransform, RightX, AlmSegY, RightW, AlmSegH);
            _almSegBg = segBox;

            // 选中段：AccentDim 底 + 主色字
            TwinRoundedRect segOn = TwinUIFactory.Rounded(segBox.rectTransform, "On", TwinTheme.R8,
                TwinTheme.AccentDim, TwinTheme.AccentDim, Color.clear, 0f, false);
            TwinUIFactory.Place(segOn.rectTransform, 0f, 0f, RightW * 0.5f, AlmSegH);
            _almSegOnBg = segOn;

            _almSegOn = TwinUIFactory.Label(segOn.rectTransform, "T", TwinTheme.FsCaption,
                TextAnchor.MiddleCenter, TwinTheme.Accent);
            TwinUIFactory.Stretch(_almSegOn.rectTransform);
            _almSegOn.text = "进行中";
            _almSegOn.raycastTarget = false;

            _almSegHis = TwinUIFactory.Label(segBox.rectTransform, "His", TwinTheme.FsCaption,
                TextAnchor.MiddleCenter, TwinTheme.TextMuted);
            TwinUIFactory.Place(_almSegHis.rectTransform, RightW * 0.5f, 0f, RightW * 0.5f, AlmSegH);
            _almSegHis.text = "历史";
            _almSegHis.raycastTarget = false;

            // 两个页签的点击热区：左半 / 右半，各自铺一层透明垫
            for (int i = 0; i < 2; i++)
            {
                Image hit = TwinUIFactory.Block(segBox.rectTransform, "SegHit_" + i,
                    new Color(0f, 0f, 0f, 0f), true);
                TwinUIFactory.Place(hit.rectTransform, RightW * 0.5f * i, 0f, RightW * 0.5f, AlmSegH);

                int idx = i;
                Button sb = hit.gameObject.AddComponent<Button>();
                sb.transition = Selectable.Transition.None;
                sb.onClick.AddListener(delegate { OnAlarmTabClicked(idx); });
            }

            RefreshAlarmTabStyles();

            // ---- 告警列表 y=100 高 348，行高 52，视口 6 行 ----
            for (int i = 0; i < AlmRows; i++)
            {
                float y = AlmListY + i * AlmRowH;

                RectTransform row = TwinUIFactory.Node("AlmRow_" + i, root).transform as RectTransform;
                TwinUIFactory.Place(row, RightX, y, RightW, AlmRowH);
                _almRow[i] = row;

                // 【告警可点】选中高亮底：圆角 R8，默认全透明。
                // 必须建在色条/文字**之前** —— UGUI 按 sibling 顺序绘制，后建的盖住先建的。
                TwinRoundedRect rowBg = TwinUIFactory.Rounded(row, "SelBg", TwinTheme.R8,
                    Color.clear, Color.clear, Color.clear, 0f, false);
                TwinUIFactory.Stretch(rowBg.rectTransform);
                _almRowBg[i] = rowBg;

                // 左 4px 严重度条
                Image bar = TwinUIFactory.Block(row, "Bar", TwinTheme.StatusUnknown, false);
                TwinUIFactory.Place(bar.rectTransform, 0f, 8f, 4f, AlmRowH - 16f);
                _almBar[i] = bar;

                // 行内三段竖排（规格 §3.4）：编号 13 @y=2 / 名称 11 @y=20 / 描述 9 @y=36。
                // ⚠️ 高度必须显式给，不能沿用字号 —— `size + 6` 会让 13px 的编号占到 19，
                // 与 y=20 的名称压出 1px 重叠（实测"告警日志文字叠在一起"）。
                _almId[i] = MakeAlmCell(row, "Id", 12f, 240f, 2f, 16f, TwinTheme.FsBody, TwinTheme.TextPrimary);
                _almName[i] = MakeAlmCell(row, "Name", 12f, 200f, 20f, 16f, TwinTheme.FsCaption, TwinTheme.TextSecondary);
                _almDesc[i] = MakeAlmCell(row, "Desc", 12f, 280f, 36f, 14f, TwinTheme.FsMicro, TwinTheme.TextMuted);
                _almDur[i] = MakeAlmCell(row, "Dur", 290f, 70f, 2f, 16f, TwinTheme.FsMicro, TwinTheme.TextMuted);

                // 【告警可点】整行可点 → 选中该设备并让镜头飞过去。
                //
                // ── 为什么必须铺这层垫片 ────────────────────────────────────
                // 行本身是纯 `Node`（没有任何 Graphic），射线打不到它 ——
                // `Button` 挂在这样的对象上永远收不到点击，**而且不报错**。
                // 这和电力页 ScrollRect 的 HitArea、设备台账的 Hit 是同一个坑，
                // 本项目已经踩过三次（见本文件 BuildDevice / BuildPower 的注释）。
                //
                // 垫片必须是**全透明但 raycastTarget=true** 的 Image：
                // 用 `Color.clear` 也算 Graphic，照样能收射线（与 TwinUIFactory.Label
                // 默认 raycastTarget=false 相反）。
                Image hit = TwinUIFactory.Block(row, "Hit", new Color(0f, 0f, 0f, 0f), true);
                TwinUIFactory.Stretch(hit.rectTransform);

                int almIdx = i;
                Button rowBtn = row.gameObject.AddComponent<Button>();
                rowBtn.transition = Selectable.Transition.None;
                // 不设 targetGraphic：本行没有"按钮底"，高亮由 SelBg 自己表达，
                // 交给 Button 过渡反而会和选中色抢同一个 Image。
                rowBtn.onClick.AddListener(delegate { OnAlarmRowClicked(almIdx); });

                row.gameObject.SetActive(false);
            }

            // ---- 追溯占位条 y=456 高 52（规格 §3.4「⏸ 占位」）----
            // 占位判据：无填充底、更淡描边、只用 Muted 字、不出现数字（§4.1）
            TwinRoundedRect trace = TwinUIFactory.Rounded(root, "TraceBox", TwinTheme.R8,
                Color.clear, Color.clear, TwinTheme.WithAlpha(Color.white, 0.006f),
                TwinTheme.StrokeHair.Width, false);
            TwinUIFactory.Place(trace.rectTransform, RightX, AlmTraceY, RightW, AlmTraceH);

            Text tt = TwinUIFactory.Label(trace.rectTransform, "T", TwinTheme.FsCaption,
                TextAnchor.UpperLeft, TwinTheme.TextMuted);
            TwinUIFactory.Place(tt.rectTransform, 12f, 8f, RightW - 24f, 16f);
            tt.text = "全链路追溯待接入";

            Text td = TwinUIFactory.Label(trace.rectTransform, "D", TwinTheme.FsMicro,
                TextAnchor.UpperLeft, TwinTheme.TextMuted);
            TwinUIFactory.Place(td.rectTransform, 12f, 26f, RightW - 24f, 14f);
            td.horizontalOverflow = HorizontalWrapMode.Wrap;
            td.text = "选中一条告警后，这里显示 下发→回执→执行→回读→恢复 的完整时间线。";
        }

        /// <summary>
        /// 告警行单元格。**高度由调用方显式给** —— 不能按字号推（`size + 6`），
        /// 否则 13px 编号会占到 19 高，与下一段 y=20 的名称压出重叠。
        /// </summary>
        private static Text MakeAlmCell(RectTransform row, string name, float x, float w,
                                        float y, float h, int size, Color color)
        {
            Text t = TwinUIFactory.Label(row, name, size, TextAnchor.UpperLeft, color);
            TwinUIFactory.Place(t.rectTransform, x, y, w, h);
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            return t;
        }

        // ---------------------------------------------------------------- 数据监测

        private void BuildMonitor(RectTransform root)
        {
            // KPI 2×2：216×88，x=184 / 408，y=60 / 156（规格 §1.5 权威预算）
            _kpiPower = TwinMetricCard.Create(root, "Kpi_Power", RightX, KpiY1,
                CardW, CardH, TwinTheme.FsTitle, "总功率", "kW");
            // 【P5】总功率已改读实时求和，副标不能再写"按额定功率 × 台数"（会自相矛盾）
            _kpiPower.SetEstimateNote("实时 · 在运设备");
            _kpiEnergy = TwinMetricCard.Create(root, "Kpi_Energy", RightX + CardW + 8f, KpiY1,
                CardW, CardH, TwinTheme.FsTitle, "今日用电", "kWh");
            _kpiEnergy.SetEstimateNote("按额定功率 × 运行时长");   // C-28
            _kpiOnline = TwinMetricCard.Create(root, "Kpi_Online", RightX, KpiY2,
                CardW, CardH, TwinTheme.FsTitle, "在线", "台");
            _kpiAlarm = TwinMetricCard.Create(root, "Kpi_Alarm", RightX + CardW + 8f, KpiY2,
                CardW, CardH, TwinTheme.FsTitle, "告警", "台");

            BuildHealthCard(root);
            BuildTop3(root);

            RefreshMonitor();
            _nextRefresh = Time.unscaledTime + 0.5f;
        }

        /// <summary>
        /// 楼层健康卡：标题 11 + 三段条（h9）+ 图例 9。
        /// 这里只建骨架，数值由 <see cref="RefreshHealthCard"/> 填 ——
        /// 上一版把 `ratio {0.92,0.05,0.03}` 和 `正常 141 / 警告 7 / 告警 5` 写死在构建里，
        /// 同屏和 KPI 的「设备总数 53」对不上（141+7+5=153），是 R10 的第二处来源。
        /// </summary>
        private void BuildHealthCard(RectTransform root)
        {
            TwinRoundedRect card = TwinUIFactory.Rounded(root, "HealthCard", TwinTheme.R12,
                TwinTheme.CardTop, TwinTheme.CardBottom, TwinTheme.Border, TwinTheme.StrokeHair.Width, false);
            TwinUIFactory.Place(card.rectTransform, RightX, HealthY, RightW, CardH);

            Text title = TwinUIFactory.Label(card.rectTransform, "Title", TwinTheme.FsCaption,
                TextAnchor.UpperLeft, TwinTheme.TextMuted);
            TwinUIFactory.Place(title.rectTransform, 16f, 12f, 200f, 16f);
            title.text = "楼层健康";

            _healthPct = TwinUIFactory.Label(card.rectTransform, "Pct", TwinTheme.FsCaption,
                TextAnchor.UpperRight, TwinTheme.TextSecondary);
            TwinUIFactory.Place(_healthPct.rectTransform, RightW - 160f, 12f, 144f, 16f);
            _healthPct.text = "";

            Color[] colors = { TwinTheme.StatusNormal, TwinTheme.StatusWarning, TwinTheme.StatusAlarm };

            for (int i = 0; i < 3; i++)
            {
                // 三段条：h=9，段间 4，圆角 2。宽度与 x 全部由刷新现算。
                TwinRoundedRect seg = TwinUIFactory.Rounded(card.rectTransform, "Seg_" + i, 2f,
                    colors[i], colors[i], Color.clear, 0f, false);
                TwinUIFactory.Place(seg.rectTransform, 16f, 34f, 2f, 9f);
                seg.gameObject.SetActive(false);
                _healthSeg[i] = seg;

                // 图例：色点 6 + 文案 9
                TwinRoundedRect dot = TwinUIFactory.Rounded(card.rectTransform, "LegDot_" + i, 3f,
                    colors[i], colors[i], Color.clear, 0f, false);
                TwinUIFactory.Place(dot.rectTransform, 16f + i * 96f, 56f, 6f, 6f);

                Text leg = TwinUIFactory.Label(card.rectTransform, "Leg_" + i, TwinTheme.FsMicro,
                    TextAnchor.MiddleLeft, TwinTheme.TextMuted);
                TwinUIFactory.Place(leg.rectTransform, 26f + i * 96f, 50f, 84f, 16f);
                leg.text = "";
                _healthLeg[i] = leg;
            }
        }

        /// <summary>
        /// 三段 = 正常 / 警告+告警 / 离线（含 Stale）。**三段之和必须等于设备总数**，
        /// 不等就整卡转无数据态 —— 宁可显示"数据不一致"，也不画一条比例是编的条（规格 R2）。
        /// </summary>
        private void RefreshHealthCard()
        {
            if (_healthSeg[0] == null) return;

            int total = TwinDeviceStats.ActiveDeviceCount;
            int online = TwinDeviceStats.OnlineDeviceCount;
            int alarm = TwinDeviceStats.AlarmDeviceCount;
            int offline = TwinDeviceStats.OfflineDeviceCount;

            int normal = Mathf.Max(0, online - alarm);   // 正常 = 在线 − 告警（告警是在线子集）
            int warnAlarm = alarm;
            int off = offline;

            bool consistent = total > 0
                              && TwinDeviceStats.IsPartitionConsistent
                              && normal + warnAlarm + off == total;

            if (!consistent)
            {
                for (int i = 0; i < 3; i++)
                {
                    if (_healthSeg[i] != null) _healthSeg[i].gameObject.SetActive(false);
                    if (_healthLeg[i] != null) SetText(_healthLeg[i], "");
                }
                if (_healthPct != null)
                    SetText(_healthPct, total > 0 ? "数据不一致" : "数据源未接入");
                return;
            }

            const float gap = 4f;
            float usable = RightW - 32f - gap * 2f;
            int[] values = { normal, warnAlarm, off };

            float x = 16f;
            float used = 0f;

            for (int i = 0; i < 3; i++)
            {
                // 最后一段吃掉取整余数，保证三段首尾正好铺满 usable
                float w = i == 2 ? usable - used : Mathf.Round(usable * values[i] / total);
                if (w < 0f) w = 0f;
                used += w;

                TwinRoundedRect seg = _healthSeg[i];
                if (seg != null)
                {
                    // 0 值段不画：宽度 0 的圆角矩形会退化成一条 1px 竖线，看着像"凭空多一段"
                    bool visible = w >= 1f;
                    if (seg.gameObject.activeSelf != visible) seg.gameObject.SetActive(visible);
                    if (visible)
                    {
                        RectTransform sr = seg.rectTransform;
                        // 变了才写：Place 会触发 layout dirty，0.5s 一次也别白写
                        if (Mathf.Abs(sr.sizeDelta.x - w) > 0.5f || Mathf.Abs(sr.anchoredPosition.x - x) > 0.5f)
                            TwinUIFactory.Place(sr, x, 34f, w, 9f);
                    }
                }

                if (_healthLeg[i] != null) SetText(_healthLeg[i], HealthLabel(i) + " " + values[i]);

                x += w + gap;
            }

            int pct = Mathf.RoundToInt(normal * 100f / total);
            if (_healthPct != null) SetText(_healthPct, "正常 " + pct + "%");
        }

        private static string HealthLabel(int index)
        {
            switch (index)
            {
                case 0: return "正常";
                case 1: return "警告";
                default: return "离线";
            }
        }

        /// <summary>
        /// 异常 Top3：行高 40、gap 6，左 3px 严重度条 + 编号 11 + 指标 9 + 右侧标签 9。
        ///
        /// 只建 3 个空壳，内容由 <see cref="RefreshTop3"/> 填。
        /// 上一版这里写死 `B1-001-配电-001 / 1F-101-插座-004 / 3F-302-空调-002` 三行示例 ——
        /// 告警 KPI 显示 0 的同时右边挂着 3 条"异常"，答辩一问就露。
        /// </summary>
        private void BuildTop3(RectTransform root)
        {
            Text head = TwinUIFactory.Label(root, "AbnTitle", TwinTheme.FsCaption,
                TextAnchor.UpperLeft, TwinTheme.TextMuted);
            TwinUIFactory.Place(head.rectTransform, RightX, AbnTitleY, RightW, 16f);
            head.text = "异常 Top3";

            for (int i = 0; i < 3; i++)
            {
                float y = AbnRowY + i * (AbnRowH + AbnRowGap);

                TwinRoundedRect row = TwinUIFactory.Rounded(root, "Abn_" + i, TwinTheme.R8,
                    Color.clear, Color.clear, TwinTheme.Border, TwinTheme.StrokeHair.Width, false);
                TwinUIFactory.Place(row.rectTransform, RightX, y, RightW, AbnRowH);
                _abnRow[i] = row.rectTransform;

                Image bar = TwinUIFactory.Block(row.rectTransform, "Sev", TwinTheme.StatusUnknown, false);
                TwinUIFactory.Place(bar.rectTransform, 0f, 8f, TwinTheme.BarStatusWidth, 24f);
                _abnBar[i] = bar;

                Text id = TwinUIFactory.Label(row.rectTransform, "Id", TwinTheme.FsCaption,
                    TextAnchor.MiddleLeft, TwinTheme.TextPrimary);
                TwinUIFactory.Place(id.rectTransform, 12f, 0f, 150f, AbnRowH);
                _abnId[i] = id;

                Text metric = TwinUIFactory.Label(row.rectTransform, "Metric", TwinTheme.FsMicro,
                    TextAnchor.MiddleLeft, TwinTheme.TextMuted);
                TwinUIFactory.Place(metric.rectTransform, 168f, 0f, 190f, AbnRowH);
                _abnMetric[i] = metric;

                Text tag = TwinUIFactory.Label(row.rectTransform, "Tag", TwinTheme.FsMicro,
                    TextAnchor.MiddleRight, TwinTheme.StatusUnknown);
                TwinUIFactory.Place(tag.rectTransform, RightW - 84f, 0f, 68f, AbnRowH);
                _abnTag[i] = tag;

                row.gameObject.SetActive(false);
            }

            _abnEmpty = TwinUIFactory.Label(root, "AbnEmpty", TwinTheme.FsCaption,
                TextAnchor.MiddleLeft, TwinTheme.TextMuted);
            TwinUIFactory.Place(_abnEmpty.rectTransform, RightX + 12f, AbnRowY, RightW - 12f, AbnRowH);
            _abnEmpty.text = "暂无异常";
        }

        /// <summary>
        /// 取「仍在持续」的异常，按严重度降序、同严重度按时长降序，填前 3 行。
        /// 数据源是 `TwinAlarmHistory`（事件维度），不遍历 Registry —— 这正是
        /// `grep Registry.All` 能在 UI 层归零的前提。
        /// </summary>
        private void RefreshTop3()
        {
            if (_abnRow[0] == null) return;

            _abnBuf.Clear();
            TwinAlarmHistory history = TwinAlarmHistory.Instance;
            if (history != null) history.GetRecent(_abnBuf, 64);

            _abnTop.Clear();
            for (int i = 0; i < _abnBuf.Count; i++)
            {
                // 已恢复的不该出现在「异常 Top3」里 —— 那里回答的是"现在还在出什么事"
                if (_abnBuf[i].Ongoing) _abnTop.Add(_abnBuf[i]);
                if (_abnTop.Count >= 3) break;
            }

            float now = Time.realtimeSinceStartup;

            // 手写选择排序：最多 3 个元素，避免用捕获 now 的 lambda 每 0.5s 分配一次闭包
            for (int i = 0; i < _abnTop.Count - 1; i++)
            {
                int best = i;
                for (int j = i + 1; j < _abnTop.Count; j++)
                    if (CompareAlarm(_abnTop[j], _abnTop[best], now) < 0) best = j;
                if (best == i) continue;

                TwinAlarmHistory.AlarmRecord tmp = _abnTop[i];
                _abnTop[i] = _abnTop[best];
                _abnTop[best] = tmp;
            }

            for (int i = 0; i < 3; i++)
            {
                bool on = i < _abnTop.Count;
                if (_abnRow[i] != null && _abnRow[i].gameObject.activeSelf != on)
                    _abnRow[i].gameObject.SetActive(on);
                if (!on) continue;

                TwinAlarmHistory.AlarmRecord rec = _abnTop[i];
                Color sev = TwinStatusPalette.Of(rec.PeakStatus);
                string label = TwinStatusPalette.Label(rec.PeakStatus);

                if (_abnBar[i] != null) _abnBar[i].color = sev;
                // 【P0-4】Top3 编号 = `2F-201 空调`（P29-6 §6.1），不再出现带 `/` 的原始 TwinId
                SetText(_abnId[i], TwinCode.WithType(rec.TwinId));
                SetText(_abnMetric[i], label + " · 已持续 " + TwinAlarmHistory.FormatDuration(rec.Duration(now)));
                if (_abnTag[i] != null)
                {
                    SetText(_abnTag[i], label);
                    _abnTag[i].color = sev;
                }
            }

            if (_abnEmpty != null && _abnEmpty.gameObject.activeSelf != (_abnTop.Count == 0))
                _abnEmpty.gameObject.SetActive(_abnTop.Count == 0);
        }

        /// <summary>排序比较：返回负数表示 a 应排在 b 前面（更严重 / 更久）。</summary>
        private static int CompareAlarm(TwinAlarmHistory.AlarmRecord a, TwinAlarmHistory.AlarmRecord b, float now)
        {
            int sa = TwinStatusPalette.Severity(a.PeakStatus);
            int sb = TwinStatusPalette.Severity(b.PeakStatus);
            if (sa != sb) return sb.CompareTo(sa);           // 严重度高的在前
            return b.Duration(now).CompareTo(a.Duration(now)); // 同严重度，久的在前
        }

        // ---------------------------------------------------------------- 设备管理 / 告警中心 刷新

        /// <summary>
        /// 台账刷新：计数行 + 7 行列表。
        /// 遍历走 <see cref="TwinDeviceStats.AllDevices"/>（快照层的统一遍历入口），
        /// 行级过滤用 <see cref="TwinDeviceStats.IsActiveDevice"/> —— UI 层不直接碰 `Registry.All`。
        /// </summary>
        private void RefreshDevice()
        {
            if (_devCount == null) return;

            int total = TwinDeviceStats.ActiveDeviceCount;
            int scope = TwinDeviceStats.ControlScopeCount;
            _devCount.text = "共 " + total + " 台 · 受控 " + scope + " · 缺失 0";

            IReadOnlyList<TwinAnchor> all = TwinDeviceStats.AllDevices;
            _devBuf.Clear();

            // 【C-30】楼层筛选。状态从 TwinSystemState 读（全屏那套写的是同一个字段），
            // 所以窄态选 1F、切到全屏还是 1F。
            int filter = TwinSystemState.FloorFilter;

            if (all != null)
            {
                for (int i = 0; i < all.Count && _devBuf.Count < DevRows; i++)
                {
                    TwinAnchor a = all[i];
                    if (!TwinDeviceStats.IsActiveDevice(a)) continue;

                    if (filter > 0)
                    {
                        TwinRoomInfo info = TwinSiteLayout.Find(a.Zone);
                        int floor = info != null ? info.Floor : 0;
                        // filter==3 → 室外（楼层 0 的分区）；1/2 → 对应楼层
                        int want = filter == 3 ? 0 : filter;
                        if (floor != want) continue;
                    }

                    _devBuf.Add(a);
                }
            }

            for (int i = 0; i < DevRows; i++)
            {
                bool on = i < _devBuf.Count;
                if (_devRow[i] != null && _devRow[i].gameObject.activeSelf != on)
                    _devRow[i].gameObject.SetActive(on);
                if (!on) continue;

                TwinAnchor a = _devBuf[i];
                TwinStatus s = a.StatusOfWorst();
                Color c = TwinStatusPalette.Of(s);

                if (_devBar[i] != null) _devBar[i].color = c;

                // 【C-30】选中行高亮 —— 点了没任何视觉反馈，用户会以为"点不动"。
                // 用行底色表达选中（AccentDim），跟全屏那套保持同一个语义。
                //
                // 【2026-11-03 P10 双态同步】全屏那边补强成三层（底 + 边 + 字），
                // 这里必须同步 —— 两态"本质还是一个面板"。原来两边都只有一层
                // AccentDim（Accent @0.035，Δ18），在深色行底上几乎看不出变化。
                // 现在同样是：底 AccentDim / 边 AccentLine / 「详情›」文字转主色。
                if (_devRowBg[i] != null)
                {
                    bool sel = ReferenceEquals(SchoolTwin.Interaction.TwinPicker.Selected, a);
                    Color bg = sel ? TwinTheme.AccentDim : Color.clear;
                    _devRowBg[i].SetFill(bg, bg);
                    // 未选中行保持"无描边"（构建时就是 Color.clear / 0f），
                    // 只在选中时给 1px 主色边 —— 不能给未选中行补 Border，
                    // 那样整张表会突然浮出 64 条行框，和窄态原来的观感不同。
                    _devRowBg[i].SetBorder(sel ? TwinTheme.AccentLine : Color.clear,
                                           sel ? TwinTheme.StrokeHair.Width : 0f);
                }

                // 【P0-3】编号列固定 150、名称列 x=170（间隙 8 ≥ 规格要求的 8）。
                // 原来这里直接塞 a.TwinId，而 MakeCell 用的是 Overflow ——
                // 文本画出格子边界压到名称列上。两重修：
                //   ① 内容换成短码 `2F-201`（TwinCode 唯一出口，原始 TwinId 不进 UI）
                //   ② 仍然截断兜底：表外分区会退化成 Zone 原名（如 ClassComputer），长度不可控
                SetText(_devId[i], TwinUIFactory.Truncate(
                    TwinCode.Short(a.TwinId), TwinTheme.FsBody, 150f));
                SetText(_devName[i], a.DisplayName);
                SetText(_devPos[i], TwinCode.Room(a.Zone));
                SetText(_devTag[i], TwinStatusPalette.Label(s));
                if (_devTag[i] != null) _devTag[i].color = c;
            }

            // 【C-31】Content 高度 = 实际行数 × 行距。ScrollRect 靠它算可滚范围，
            // 不设的话永远停在 0，滚轮滚不动。
            if (_devContent != null)
            {
                float contentH = _devBuf.Count * DevRowH;
                if (!Mathf.Approximately(_devContent.sizeDelta.y, contentH))
                    _devContent.sizeDelta = new Vector2(0f, contentH);
            }
        }

        /// <summary>TwinId 的第一段当位置短码（`Class2-1/Light/Main_01` → `Class2-1`）。全码放详情卡。</summary>
        private static string ShortZone(string twinId)
        {
            if (string.IsNullOrEmpty(twinId)) return "";
            int slash = twinId.IndexOf('/');
            return slash > 0 ? twinId.Substring(0, slash) : twinId;
        }

        // ================================================================ 设备管理交互（C-30）

        /// <summary>
        /// 点楼层 chip。**只改共享状态 + 重画，不碰其它页**。
        /// 状态写在 TwinSystemState，全屏那套读同一个字段 —— 切视图不会丢筛选。
        /// </summary>
        private void OnFloorChipClicked(int index)
        {
            if (TwinSystemState.FloorFilter == index) return;
            TwinSystemState.FloorFilter = index;
            RefreshFloorChips();
            RefreshDevice();
        }

        /// <summary>把三个 chip 的底色/描边/文字色按当前筛选值刷一遍。</summary>
        private void RefreshFloorChips()
        {
            for (int i = 0; i < _chipBg.Length; i++)
            {
                if (_chipBg[i] == null) continue;
                bool on = TwinSystemState.FloorFilter == i;

                Color fill = on ? TwinTheme.AccentDim : TwinTheme.Track;
                _chipBg[i].SetFill(fill, fill);
                _chipBg[i].SetBorder(on ? TwinTheme.AccentLine : Color.clear,
                                     TwinTheme.StrokeHair.Width);

                if (_chipText[i] != null)
                    _chipText[i].color = on ? TwinTheme.Accent : TwinTheme.TextMuted;
            }
        }

        /// <summary>
        /// 点设备行 → 选中该设备。**用行的下标查 `_devBuf` 而不是存索引** ——
        /// `_devBuf` 每次刷新都会重排（筛选一变行号就换了），存索引会点到别的设备上。
        /// 选中走 `TwinPicker.Select`，C-27 的镜头转向会自动生效。
        /// </summary>
        private void OnDeviceRowClicked(int row)
        {
            if (row < 0 || row >= _devBuf.Count) return;

            TwinAnchor a = _devBuf[row];
            if (a == null) return;

            SchoolTwin.Interaction.TwinPicker.Select(a);
            RefreshDevice();      // 立刻重画，让选中态当场可见

            // 【P3-B】钻取到设备详情页（页栈深一层，返回按钮回本页）
            TwinDrawer.OpenDeviceDetail(a);
        }

        // ================================================================ 电力管理交互（C-30）

        /// <summary>
        /// 点回路胶囊 → 打开 SBO 弹窗（Select-Before-Operate）。
        ///
        /// ── 为什么必须两步 ──────────────────────────────────────────────
        /// 一条楼栋回路合闸 = 整栋楼断电，一条房间回路 = 整间教室断电。
        /// 单次点击就执行，误触代价太大。行业里的电力 SCADA 一律要求先选后操作。
        ///
        /// ── 为什么改用它、不用上一轮手写的简化版 ────────────────────────
        /// 手写版只有一句「再点一次确认」，用户看不到"这次会断哪 12 台"。
        /// `TwinSboDialog` 会把影响设备名单摊开、给出可见倒计时、作废时说清"什么都没发生" ——
        /// 这三条正是答辩时能讲清"为什么这个操作是安全的"的依据。
        /// </summary>
        private void OnCircuitClicked(string circuitId)
        {
            if (string.IsNullOrEmpty(circuitId)) return;

            TwinCircuitNode node = TwinCircuit.Find(circuitId);
            if (node == null) return;

            EnsureSbo();

            bool turnOn = !node.On;
            TwinSboRequest req = new TwinSboRequest();
            req.Title = (turnOn ? "恢复供电 · " : "分级停电 · ") + node.Title;
            req.Subject = node.Title;
            req.CircuitId = circuitId;
            req.PowerOn = turnOn;
            TwinSystemState.CollectTargets(node, req.Targets);

            // 真正执行：只在 SBO 走到 Armed 且用户点了「执行」时被调用一次
            req.Operate = delegate
            {
                int affected = TwinCircuit.SetSwitch(circuitId, turnOn);
                TwinToast.Show(node.Title + (turnOn ? " 已合闸" : " 已分闸") +
                               "（影响 " + affected + " " + TwinCode.UnitDevice + "）");
                RepaintPowerTree();
            };

            _sbo.Show(req);
            RepaintPowerTree();   // 让那一行立刻变成"流程中"的警示色
        }

        /// <summary>
        /// 建 SBO 弹窗（惰性，只在第一次点合闸时建）。
        ///
        /// 挂在页面自己身上、父节点用页面根 —— 弹窗要盖住整页（除标题栏），
        /// 所以不能挂到电力树的 Scroll 里（会被 RectMask2D 裁掉）。
        /// </summary>
        private void EnsureSbo()
        {
            if (_sbo != null) return;

            RectTransform root = transform as RectTransform;
            if (root == null) return;

            _sbo = gameObject.AddComponent<TwinSboDialog>();
            _sbo.Build(root, 1f);

            // 窄态面板：宽 420（或 640），高 720，标题栏 96，边距 16
            float w = root.rect.width > 1f ? root.rect.width : 420f;
            float h = root.rect.height > 1f ? root.rect.height : 720f;
            _sbo.Layout(w, h, 96f, 16f);
        }

        /// <summary>
        /// 重画电力树。**先清空再重建**，而不是找节点改颜色 ——
        /// 因为「SBO 流程中」会改变胶囊的描边宽度和配色，
        /// 逐项改要写好几个分支，重建只要一次 `BuildPowerRows`。
        /// 20 个节点重建约 0.3ms，电力操作是低频动作，不值得为它做增量更新。
        /// </summary>
        private void RepaintPowerTree()
        {
            if (_pwrContent == null) return;

            for (int i = _pwrContent.childCount - 1; i >= 0; i--)
                TwinUIFactory.DestroySafe(_pwrContent.GetChild(i).gameObject);

            BuildPowerRows(_pwrContent);
        }

        /// <summary>
        /// 告警列表刷新：分段文案 + 6 行。
        /// 严重度由左侧色条承载，文字不再重复写「严重/警告」（规格 §3.4 负责人裁决）。
        ///
        /// 【C-30】补上「历史」页签：原来只渲染进行中，历史那个页签点了没反应 ——
        /// 现在按 TwinSystemState.AlarmHistory 分派，进行中 = 未恢复，历史 = 已恢复。
        /// </summary>
        private void RefreshAlarm()
        {
            if (_almRow[0] == null) return;

            int ongoing = TwinDeviceStats.UnackedAlarmEventCount;
            if (_almSegOn != null) _almSegOn.text = "进行中 " + ongoing;

            // 复用 _abnBuf：Top3 与告警中心不会同帧执行（Update 里按当前页分派）
            _abnBuf.Clear();
            TwinAlarmHistory history = TwinAlarmHistory.Instance;
            if (history != null) history.GetRecent(_abnBuf, 64);

            // 历史条数：已恢复的记录（EndTime > 0）
            int closed = 0;
            for (int i = 0; i < _abnBuf.Count; i++)
                if (!_abnBuf[i].Ongoing) closed++;
            if (_almSegHis != null) _almSegHis.text = "历史 " + closed;

            bool wantHistory = TwinSystemState.AlarmHistory;

            // 【告警可点】选中判定只查一次：拿当前选中的 TwinId 跟每行比字符串。
            //
            // 上一版在循环里对每行都 `reg.TryGet(rec.TwinId)` + `ReferenceEquals`，
            // 6 行 × 每 0.5s = 12 次字典查找/秒。虽然量不大，但这里**根本不需要查表**：
            // 选中的锚点自己就有 TwinId，比字符串就够了，还顺带避免
            // "告警记录已过期、锚点已销毁"时 `TryGet` 返回旧引用造成的误判。
            TwinAnchor selNow = SchoolTwin.Interaction.TwinPicker.Selected;
            string selId = selNow != null ? selNow.TwinId : null;

            int n = 0;
            for (int i = 0; i < _abnBuf.Count && n < AlmRows; i++)
            {
                TwinAlarmHistory.AlarmRecord rec = _abnBuf[i];
                if (wantHistory == rec.Ongoing) continue;   // 进行中取 !Ongoing==false，历史取 Ongoing==false

                if (_almRow[n] != null && !_almRow[n].gameObject.activeSelf)
                    _almRow[n].gameObject.SetActive(true);

                Color c = TwinStatusPalette.Of(rec.PeakStatus);

                if (_almBar[n] != null) _almBar[n].color = c;
                SetText(_almId[n], TwinCode.Short(rec.TwinId));
                SetText(_almName[n], rec.DisplayName);
                SetText(_almDesc[n], TwinStatusPalette.Label(rec.PeakStatus) + " · " + rec.Clock);
                SetText(_almDur[n], wantHistory ? "已恢复" : "进行中");
                if (_almDur[n] != null)
                    _almDur[n].color = wantHistory ? TwinTheme.StatusNormal : TwinTheme.StatusAlarm;

                // 【告警可点】记下这一行是谁 —— 点击时按 TwinId 反查锚点，
                // 不存行号（`_abnBuf` 会重排，行号会漂）。
                _almTwinId[n] = rec.TwinId;

                // 选中态高亮：和设备台账同一个语义（AccentDim 底），
                // 否则"点了一下"没有任何视觉反馈，用户会以为点不动。
                if (_almRowBg[n] != null)
                {
                    bool sel = !string.IsNullOrEmpty(selId)
                               && string.Equals(selId, rec.TwinId, StringComparison.Ordinal);
                    Color bg = sel ? TwinTheme.AccentDim : Color.clear;
                    _almRowBg[n].SetFill(bg, bg);
                }

                n++;
            }

            for (int i = n; i < AlmRows; i++)
            {
                if (_almRow[i] != null && _almRow[i].gameObject.activeSelf)
                    _almRow[i].gameObject.SetActive(false);

                // 【告警可点】行被收起来的同时把 TwinId 一起清掉。
                // 不清的后果：切到「历史」只剩 2 条，前 4 行的旧 `_almTwinId` 还留着；
                // 虽然那些行的 GameObject 已经 SetActive(false) 收不到点击，
                // 但只要将来有任何路径让它们重新亮起来，就会点到"上一页签的第 3 条"。
                // 与其依赖"反正点不到"，不如在收起这一行时就让它失去身份。
                _almTwinId[i] = null;
            }
        }

        // ================================================================ 告警页签交互（C-30）

        /// <summary>
        /// 切「进行中 / 历史」。状态存 TwinSystemState，与全屏告警页共用一份。
        /// </summary>
        private void OnAlarmTabClicked(int index)
        {
            bool history = index == 1;
            if (TwinSystemState.AlarmHistory == history) return;

            TwinSystemState.AlarmHistory = history;
            RefreshAlarmTabStyles();
            RefreshAlarm();
        }

        /// <summary>选中段底块挪到左半/右半，文字配色跟着切。</summary>
        private void RefreshAlarmTabStyles()
        {
            bool history = TwinSystemState.AlarmHistory;

            if (_almSegOnBg != null)
            {
                RectTransform rt = _almSegOnBg.rectTransform;
                rt.anchoredPosition = new Vector2(history ? RightW * 0.5f : 0f, rt.anchoredPosition.y);
            }

            if (_almSegOn != null)
                _almSegOn.color = history ? TwinTheme.TextMuted : TwinTheme.Accent;
            if (_almSegHis != null)
                _almSegHis.color = history ? TwinTheme.Accent : TwinTheme.TextMuted;
        }

        /// <summary>
        /// 【告警可点】点一条告警 → 选中该设备 + 镜头飞过去。
        ///
        /// ── 这一条修的是"点了不定位"────────────────────────────────────
        /// 之前窄态告警行**根本没有点击绑定**：行是纯 Node、没 Graphic、
        /// 也没 Button —— 射线打不到，点了什么都没发生，且不报任何错。
        /// 所以问题不是"定位逻辑坏了"，是"入口从来没接上"。
        ///
        /// ── 为什么按 TwinId 反查，而不是存锚点索引 ──────────────────────
        /// `_abnBuf` 每 0.5s 重排（新告警插最前），行号会漂。
        /// 存索引会出现"点第 3 行却飞到别的设备"，比点不动更难解释。
        /// 这里存的是刷新那一刻写进 `_almTwinId[i]` 的 TwinId，和数据同源。
        ///
        /// ── 定位走 TwinPicker.Select，不自己算相机 ─────────────────────
        /// `Select` 内部会调 `FaceAnchor`，那里已经处理了：
        ///   · 只在**观景模式**下转向（第三人称让玩家自己走过去，主理人 2026-10-31 裁决）
        ///   · 全屏 L2 数据态不转向（中间整块不透明，飞过去也看不见）
        /// 自己再写一遍只会和那三条裁决打架。
        /// </summary>
        private void OnAlarmRowClicked(int index)
        {
            if (index < 0 || index >= AlmRows) return;

            string twinId = _almTwinId[index];
            if (string.IsNullOrEmpty(twinId)) return;

            TwinRuntime runtime = TwinRuntime.Instance;
            TwinRegistry registry = runtime != null ? runtime.Registry : null;
            if (registry == null) return;

            TwinAnchor anchor;
            if (!registry.TryGet(twinId, out anchor) || anchor == null) return;

            SchoolTwin.Interaction.TwinPicker.Select(anchor);
            RefreshAlarm();       // 立刻重画，让选中高亮当场可见
        }

        private void RefreshMonitor()
        {
            // 【P29-3.6】在线/告警改读快照，本页不再遍历 Registry。
            int total = TwinDeviceStats.ActiveDeviceCount;
            int online = TwinDeviceStats.OnlineDeviceCount;
            int alarm = TwinDeviceStats.AlarmDeviceCount;
            bool ready = total > 0;

            if (_kpiOnline != null)
            {
                if (ready) _kpiOnline.SetValue(online.ToString());
                _kpiOnline.SetState(ready ? TwinMetricCard.CardState.Normal : TwinMetricCard.CardState.NoData);
            }

            if (_kpiAlarm != null)
            {
                if (ready) _kpiAlarm.SetValue(alarm.ToString());
                _kpiAlarm.SetState(!ready ? TwinMetricCard.CardState.NoData
                                   : alarm > 0 ? TwinMetricCard.CardState.Alarm
                                   : TwinMetricCard.CardState.Normal);
            }

            // 【P5 口径统一】总功率改读**实时求和**，不再是「额定功率 × 台数」的估算。
            //
            // ── 为什么必须改 ──────────────────────────────────────────────
            // 原来这里用 `TotalWatt()`（估算容量）→ 21.86 kW，
            // 而统计页用 `SumLivePower()`（实时消耗）→ 2.8 kW，
            // 两个 KPI 都叫"总功率"却差 8 倍 —— 主理人 2026-11-02 截图直接指出
            // "同一个面板两种数字"。
            //
            // 统一到**实时**的理由：统计页那条曲线就是实时求和画的，
            // KPI 必须和曲线对得上；而且"当前总功率"的语义本来就是"现在用了多少"，
            // 不是"全开能到多少"。容量视角另有用途，但不该占用这个 KPI。
            int active = TwinDeviceStats.ActiveDeviceCount;
            float watt = TwinPowerEstimate.SumLivePower();

            if (_kpiPower != null)
            {
                _kpiPower.SetValue(active > 0 ? (watt / 1000f).ToString("0.0") : "—");
                _kpiPower.SetState(active > 0 ? TwinMetricCard.CardState.Normal
                                              : TwinMetricCard.CardState.NoData);
            }

            if (_kpiEnergy != null)
            {
                _kpiEnergy.SetValue(active > 0 ? "≈ " + TwinPowerEstimate.TodayKwh().ToString("0.00") : "—");
                _kpiEnergy.SetState(active > 0 ? TwinMetricCard.CardState.Estimate
                                               : TwinMetricCard.CardState.NoData);
            }

            RefreshHealthCard();
            RefreshTop3();
        }

        /// <summary>数据监测页 0.5s 刷新一次。页面隐藏时 GameObject 不激活，Update 自然停跑。</summary>
        /// <summary>
        /// 0.5s 周期刷新。**按当前页只刷该页** —— 五页同时重建文本会白烧 CPU，
        /// 而且没显示的页刷了也看不见。角标不分页，每轮都更。
        /// </summary>
        private void Update()
        {
            // 【P5】统计页曲线 5Hz 采样（内部自带节流）。
            // 必须放在 `_nextRefresh` 早退**之前** —— 否则 0.5s 窗口内的帧全被挡掉，
            // 曲线只能拿到 2Hz 的点。
            UpdateNarrowStats();

            if (!_built) return;

            // 【P5】每帧推进窄态曲线的时间轴（同全屏：只靠 Push 的话线是冻的）
            if (_nSpark != null && _selected == 4)
                _nSpark.Tick(Time.unscaledTime);

            // 【P5】滚动条跟随必须在每帧跑，不能等 0.5s 节流 ——
            // 拖/滚的过程中 0.5s 才更新一次的话指示条会一跳一跳地追。
            if (_selected == 4 && _nStatsBar != null) UpdateStatsBar();

            if (Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + 0.5f;

            RefreshTabBadge();

            switch (_selected)
            {
                case 0: RefreshMonitor(); break;
                case 1: RefreshDevice(); break;
                case 3: RefreshAlarm(); break;
                case 4: RefreshStats(); break;      // 【P5】统计页（窄态精简版）
                // 电力管理（2）不周期刷新：电路状态变化少，树是静态构建的
                // 设备详情已独立成页（TwinDeviceDetailPage），它自己带 Update，不在这里刷
            }
        }

        private static void SetText(Text t, string value)
        {
            if (t == null) return;
            if (string.Equals(t.text, value, StringComparison.Ordinal)) return;
            t.text = value;
        }
    }
}

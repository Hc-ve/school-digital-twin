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
        private const int DevRows = 7;          // 158 + 7×44 = 466，视口 348 内 7 行

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

        // ---- 告警中心：6 行列表 ----
        private readonly RectTransform[] _almRow = new RectTransform[AlmRows];
        private readonly Image[] _almBar = new Image[AlmRows];
        private readonly Text[] _almId = new Text[AlmRows];
        private readonly Text[] _almName = new Text[AlmRows];
        private readonly Text[] _almDesc = new Text[AlmRows];
        private readonly Text[] _almDur = new Text[AlmRows];
        private Text _almSegOn;
        private Text _almSegHis;

        private readonly List<TwinAnchor> _devBuf = new List<TwinAnchor>(64);

        private float _nextRefresh;
        private int _selected = -1;
        private bool _built;

        // ---------------------------------------------------------------- 构建

        public override void OnShow()
        {
            if (_built) return;
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
        /// 数据统计（规格 §3.5）：本轮整页空状态，**不出图表**。
        ///
        /// 【为什么不出图表】项目无时序存储，kWh 只能按瞬时功率积分估算。
        /// 画一条「趋势折线」等于在答辩现场展示编造数据 —— 评审一问「这条线哪来的」就崩。
        /// 空状态反而是诚实且专业的处理（规格原文 §3.5）。
        ///
        /// 版式：空状态组 y=228、高 132（规格 §4.2），下方留白 168 到 528，把视线压到说明上。
        /// </summary>
        private void BuildStats(RectTransform root)
        {
            TwinEmptyState.Create(root, RightX, 228f, RightW, 132f,
                "数据统计待接入",
                "无时序存储，用电量只能按功率积分估算。\nP5 接入 1s 均值聚合后显示趋势、占比与排行。",
                2, 3);
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
                bool on = i == 0;
                TwinRoundedRect chip = TwinUIFactory.Rounded(root, "Chip_" + i, 14f,
                    on ? TwinTheme.AccentDim : TwinTheme.Track,
                    on ? TwinTheme.AccentDim : TwinTheme.Track,
                    on ? TwinTheme.AccentLine : Color.clear, TwinTheme.StrokeHair.Width, false);
                TwinUIFactory.Place(chip.rectTransform, RightX + 248f + i * 52f, DevSearchY + 2f, 48f, 28f);

                Text ct = TwinUIFactory.Label(chip.rectTransform, "T", TwinTheme.FsMicro,
                    TextAnchor.MiddleCenter, on ? TwinTheme.Accent : TwinTheme.TextMuted);
                TwinUIFactory.Stretch(ct.rectTransform);
                ct.text = chips[i];
                ct.raycastTarget = false;
            }

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

            // ---- 台账列表 y=158 高 348，行高 44，视口 7 行 ----
            for (int i = 0; i < DevRows; i++)
            {
                float y = DevListY + i * DevRowH;

                RectTransform row = TwinUIFactory.Node("DevRow_" + i, root).transform as RectTransform;
                TwinUIFactory.Place(row, RightX, y, RightW, DevRowH);
                _devRow[i] = row;

                // 左 3px 状态条
                Image bar = TwinUIFactory.Block(row, "Bar", TwinTheme.StatusUnknown, false);
                TwinUIFactory.Place(bar.rectTransform, 0f, (DevRowH - 24f) * 0.5f,
                    TwinTheme.BarStatusWidth, 24f);
                _devBar[i] = bar;

                _devId[i] = MakeCell(row, "Id", 12f, 150f, TwinTheme.FsBody, TwinTheme.TextPrimary);
                _devName[i] = MakeCell(row, "Name", 170f, 104f, TwinTheme.FsCaption, TwinTheme.TextSecondary);
                _devPos[i] = MakeCell(row, "Pos", 274f, 86f, TwinTheme.FsCaption, TwinTheme.TextMuted);
                _devTag[i] = MakeCell(row, "Tag", 358f, 44f, TwinTheme.FsMicro, TwinTheme.TextMuted);

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
        /// </summary>
        private static void BuildPowerRows(RectTransform content)
        {
            IReadOnlyList<TwinCircuitNode> nodes = TwinCircuit.All;
            float y = 0f;

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

                // 通断胶囊：高 22，通电绿 / 断电红（低透明度底 + 同色字）
                Color state = n.On ? TwinTheme.StatusNormal : TwinTheme.StatusAlarm;
                TwinRoundedRect pill = TwinUIFactory.Rounded(row, "Pill", 11f,
                    TwinTheme.WithAlpha(state, 0.16f), TwinTheme.WithAlpha(state, 0.16f),
                    Color.clear, 0f, false);
                TwinUIFactory.Place(pill.rectTransform, 240f, (h - 22f) * 0.5f, 52f, 22f);

                Text pt = TwinUIFactory.Label(pill.rectTransform, "P", TwinTheme.FsMicro,
                    TextAnchor.MiddleCenter, state);
                TwinUIFactory.Stretch(pt.rectTransform);
                pt.text = n.On ? "通电" : "断电";
                pt.raycastTarget = false;

                y += h;
            }

            // Content 高度 = 所有行总高（ScrollRect 靠它算可滚范围）
            content.sizeDelta = new Vector2(0f, y);
        }

        // ---------------------------------------------------------------- 告警中心

        private void BuildAlarm(RectTransform root)
        {
            // ---- 分段切换 y=60 高 32，两段等分 220 ----
            TwinRoundedRect segBox = TwinUIFactory.Rounded(root, "SegBox", TwinTheme.R8,
                TwinTheme.Track, TwinTheme.Track, TwinTheme.Border, TwinTheme.StrokeHair.Width, false);
            TwinUIFactory.Place(segBox.rectTransform, RightX, AlmSegY, RightW, AlmSegH);

            // 选中段：AccentDim 底 + 主色字
            TwinRoundedRect segOn = TwinUIFactory.Rounded(segBox.rectTransform, "On", TwinTheme.R8,
                TwinTheme.AccentDim, TwinTheme.AccentDim, Color.clear, 0f, false);
            TwinUIFactory.Place(segOn.rectTransform, 0f, 0f, RightW * 0.5f, AlmSegH);

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

            // ---- 告警列表 y=100 高 348，行高 52，视口 6 行 ----
            for (int i = 0; i < AlmRows; i++)
            {
                float y = AlmListY + i * AlmRowH;

                RectTransform row = TwinUIFactory.Node("AlmRow_" + i, root).transform as RectTransform;
                TwinUIFactory.Place(row, RightX, y, RightW, AlmRowH);
                _almRow[i] = row;

                // 左 4px 严重度条
                Image bar = TwinUIFactory.Block(row, "Bar", TwinTheme.StatusUnknown, false);
                TwinUIFactory.Place(bar.rectTransform, 0f, 8f, 4f, AlmRowH - 16f);
                _almBar[i] = bar;

                _almId[i] = MakeAlmCell(row, "Id", 12f, 200f, 8f, TwinTheme.FsBody, TwinTheme.TextPrimary);
                _almName[i] = MakeAlmCell(row, "Name", 12f, 260f, 18f, TwinTheme.FsCaption, TwinTheme.TextSecondary);
                _almDesc[i] = MakeAlmCell(row, "Desc", 12f, 300f, 34f, TwinTheme.FsMicro, TwinTheme.TextMuted);
                _almDur[i] = MakeAlmCell(row, "Dur", 290f, 70f, 8f, TwinTheme.FsMicro, TwinTheme.TextMuted);

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

        /// <summary>告警行单元格：可指定相对行顶的 y 偏移（行内是编号/名称/描述三段竖排）。</summary>
        private static Text MakeAlmCell(RectTransform row, string name, float x, float w,
                                        float y, int size, Color color)
        {
            Text t = TwinUIFactory.Label(row, name, size, TextAnchor.UpperLeft, color);
            TwinUIFactory.Place(t.rectTransform, x, y, w, size + 6f);
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            return t;
        }

        // ---------------------------------------------------------------- 数据监测

        private void BuildMonitor(RectTransform root)
        {
            // KPI 2×2：216×88，x=184 / 408，y=60 / 156（规格 §1.5 权威预算）
            _kpiPower = TwinMetricCard.Create(root, "Kpi_Power", RightX, KpiY1,
                CardW, CardH, TwinTheme.FsTitle, "总功率", "kW");
            _kpiEnergy = TwinMetricCard.Create(root, "Kpi_Energy", RightX + CardW + 8f, KpiY1,
                CardW, CardH, TwinTheme.FsTitle, "今日用电", "kWh");
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
                SetText(_abnId[i], rec.TwinId);
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
            if (all != null)
            {
                for (int i = 0; i < all.Count && _devBuf.Count < DevRows; i++)
                {
                    TwinAnchor a = all[i];
                    if (!TwinDeviceStats.IsActiveDevice(a)) continue;
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
                SetText(_devId[i], a.TwinId);
                SetText(_devName[i], a.DisplayName);
                SetText(_devPos[i], ShortZone(a.TwinId));
                SetText(_devTag[i], TwinStatusPalette.Label(s));
                if (_devTag[i] != null) _devTag[i].color = c;
            }
        }

        /// <summary>TwinId 的第一段当位置短码（`Class2-1/Light/Main_01` → `Class2-1`）。全码放详情卡。</summary>
        private static string ShortZone(string twinId)
        {
            if (string.IsNullOrEmpty(twinId)) return "";
            int slash = twinId.IndexOf('/');
            return slash > 0 ? twinId.Substring(0, slash) : twinId;
        }

        /// <summary>
        /// 告警列表刷新：分段文案 + 6 行。
        /// 只取「进行中」，状态标签统一「进行中」—— 严重度由左侧色条承载，
        /// 文字不再重复写「严重/警告」（规格 §3.4 负责人裁决）。
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

            int n = 0;
            for (int i = 0; i < _abnBuf.Count && n < AlmRows; i++)
            {
                if (!_abnBuf[i].Ongoing) continue;

                TwinAlarmHistory.AlarmRecord rec = _abnBuf[i];
                if (_almRow[n] != null && !_almRow[n].gameObject.activeSelf)
                    _almRow[n].gameObject.SetActive(true);

                Color c = TwinStatusPalette.Of(rec.PeakStatus);

                if (_almBar[n] != null) _almBar[n].color = c;
                SetText(_almId[n], rec.TwinId);
                SetText(_almName[n], rec.DisplayName);
                SetText(_almDesc[n], TwinStatusPalette.Label(rec.PeakStatus) + " · " + rec.Clock);
                SetText(_almDur[n], "进行中");
                if (_almDur[n] != null) _almDur[n].color = TwinTheme.StatusAlarm;

                n++;
            }

            for (int i = n; i < AlmRows; i++)
                if (_almRow[i] != null && _almRow[i].gameObject.activeSelf)
                    _almRow[i].gameObject.SetActive(false);
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

            // 总功率 / 今日用电 需要电力聚合 sink（P5）。现在没有统一口径 ——
            // 单设备 Power 的量程有的按 W、有的按 kW（灯 0~120W / 插座 0~3.5kW），
            // 直接求和会得出"1.2 + 42"这种没意义的数，所以恒走无数据态（规格五）。
            if (_kpiPower != null) _kpiPower.SetState(TwinMetricCard.CardState.NoData);
            if (_kpiEnergy != null) _kpiEnergy.SetState(TwinMetricCard.CardState.NoData);

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
            if (!_built) return;
            if (Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + 0.5f;

            RefreshTabBadge();

            switch (_selected)
            {
                case 0: RefreshMonitor(); break;
                case 1: RefreshDevice(); break;
                case 3: RefreshAlarm(); break;
                // 电力管理（2）不周期刷新：电路状态变化少，树是静态构建的
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

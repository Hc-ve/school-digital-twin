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
        private readonly RectTransform[] _pages = new RectTransform[5];

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

                // 图标 20×20 R6
                TwinRoundedRect icon = TwinUIFactory.Rounded(rt, "Icon", 6f,
                    TwinTheme.EntryIconFill, TwinTheme.EntryIconFill, Color.clear, 0f, false);
                TwinUIFactory.Place(icon.rectTransform, 12f, 12f, 20f, 20f);

                Text g = TwinUIFactory.Label(icon.rectTransform, "Glyph", TwinTheme.FsMicro,
                    TextAnchor.MiddleCenter, TwinTheme.Accent);
                TwinUIFactory.Stretch(g.rectTransform);
                g.text = m.Glyph;
                g.raycastTarget = false;

                // 三件套之三：文字（选中 TextPrimary / 未选 TextSecondary）
                Text label = TwinUIFactory.Label(rt, "Label", TwinTheme.FsBody,
                    TextAnchor.MiddleLeft, TwinTheme.TextSecondary);
                TwinUIFactory.Place(label.rectTransform, 40f, 0f, TabW - 44f, TabH);
                label.text = m.Name;
                label.raycastTarget = false;
                _tabLabel[i] = label;
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
            }

            RectTransform page = GetPage(index);
            if (page != null) page.gameObject.SetActive(true);

            Action<string> cb = OnModulePicked;
            if (cb != null) cb(Modules[index].Key);
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

            if (index == 0) BuildMonitor(rt);
            else BuildEmpty(rt, Modules[index]);

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

        private void BuildEmpty(RectTransform root, ModuleDef m)
        {
            // 标题写「什么时候」，说明写「有什么」—— 别在两处重复同一句"将在 Px 接入"
            int step = m.Phase == "P3" ? 1 : (m.Phase == "P4" ? 2 : 3);
            TwinEmptyState.Create(root, RightX, 96f, RightW, 180f,
                "该模块将在 " + m.Phase + " 接入", m.AfterText, step, 4);
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
        private void Update()
        {
            if (!_built || _selected != 0) return;
            if (Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + 0.5f;
            RefreshMonitor();
        }

        private static void SetText(Text t, string value)
        {
            if (t == null) return;
            if (string.Equals(t.text, value, StringComparison.Ordinal)) return;
            t.text = value;
        }
    }
}

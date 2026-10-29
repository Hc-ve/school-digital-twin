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

        // ---- 左轨 ----
        private const float RailX = 8f;
        private const float TabW = 152f;
        private const float TabH = 44f;
        private const float TabGap = 4f;

        // ---- 右区 ----
        private const float RightX = 184f;
        private const float RightW = 440f;
        private const float CardW = 216f;
        private const float CardH = 88f;

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
            TwinUIFactory.Place(title.rectTransform, RightX, 10f, RightW, 22f);
            title.text = m.Name;

            // 说明 10.5 → 11（legacy Text 只吃整数）
            Text desc = TwinUIFactory.Label(root, "ModDesc", TwinTheme.FsCaption,
                TextAnchor.UpperLeft, TwinTheme.TextMuted);
            TwinUIFactory.Place(desc.rectTransform, RightX, 34f, RightW, 16f);
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
            // KPI 2×2：216×88，x=184 / 408，y=52 / 148（页面坐标）
            _kpiPower = TwinMetricCard.Create(root, "Kpi_Power", RightX, 52f,
                CardW, CardH, TwinTheme.FsTitle, "总功率", "kW");
            _kpiEnergy = TwinMetricCard.Create(root, "Kpi_Energy", RightX + CardW + 8f, 52f,
                CardW, CardH, TwinTheme.FsTitle, "今日用电", "kWh");
            _kpiOnline = TwinMetricCard.Create(root, "Kpi_Online", RightX, 148f,
                CardW, CardH, TwinTheme.FsTitle, "在线", "台");
            _kpiAlarm = TwinMetricCard.Create(root, "Kpi_Alarm", RightX + CardW + 8f, 148f,
                CardW, CardH, TwinTheme.FsTitle, "告警", "台");

            BuildHealthCard(root);
            BuildTop3(root);

            RefreshMonitor();
        }

        /// <summary>楼层健康卡：标题 11 + 三段条（h9）+ 图例 9。</summary>
        private void BuildHealthCard(RectTransform root)
        {
            TwinRoundedRect card = TwinUIFactory.Rounded(root, "HealthCard", TwinTheme.R12,
                TwinTheme.CardTop, TwinTheme.CardBottom, TwinTheme.Border, TwinTheme.StrokeHair.Width, false);
            TwinUIFactory.Place(card.rectTransform, RightX, 256f, RightW, CardH);

            Text title = TwinUIFactory.Label(card.rectTransform, "Title", TwinTheme.FsCaption,
                TextAnchor.UpperLeft, TwinTheme.TextMuted);
            TwinUIFactory.Place(title.rectTransform, 16f, 12f, 200f, 16f);
            title.text = "楼层健康";

            Text pct = TwinUIFactory.Label(card.rectTransform, "Pct", TwinTheme.FsCaption,
                TextAnchor.UpperRight, TwinTheme.TextSecondary);
            TwinUIFactory.Place(pct.rectTransform, RightW - 160f, 12f, 144f, 16f);
            pct.text = "正常 96%";

            // 三段条：h=9，段间 4，圆角 2
            float[] ratio = { 0.92f, 0.05f, 0.03f };
            Color[] colors = { TwinTheme.StatusNormal, TwinTheme.StatusWarning, TwinTheme.StatusAlarm };
            string[] names = { "正常 141", "警告 7", "告警 5" };

            float x = 16f;
            float barW = RightW - 32f;
            const float gap = 4f;
            float used = 0f;

            for (int i = 0; i < 3; i++)
            {
                float w = i == 2 ? barW - used : Mathf.Round((barW - gap * 2f) * ratio[i]);
                used += w + gap;

                TwinRoundedRect seg = TwinUIFactory.Rounded(card.rectTransform, "Seg_" + i, 2f,
                    colors[i], colors[i], Color.clear, 0f, false);
                TwinUIFactory.Place(seg.rectTransform, x, 34f, Mathf.Max(2f, w), 9f);

                // 图例：色点 6 + 文案 9
                TwinRoundedRect dot = TwinUIFactory.Rounded(card.rectTransform, "LegDot_" + i, 3f,
                    colors[i], colors[i], Color.clear, 0f, false);
                TwinUIFactory.Place(dot.rectTransform, 16f + i * 96f, 56f, 6f, 6f);

                Text leg = TwinUIFactory.Label(card.rectTransform, "Leg_" + i, TwinTheme.FsMicro,
                    TextAnchor.MiddleLeft, TwinTheme.TextMuted);
                TwinUIFactory.Place(leg.rectTransform, 26f + i * 96f, 50f, 84f, 16f);
                leg.text = names[i];

                x += w + gap;
            }
        }

        /// <summary>异常 Top3：行高 40、gap 6，左 3px 严重度条 + 编号 11 + 指标 9 + 右侧标签 9。</summary>
        private void BuildTop3(RectTransform root)
        {
            Text head = TwinUIFactory.Label(root, "AbnTitle", TwinTheme.FsCaption,
                TextAnchor.UpperLeft, TwinTheme.TextMuted);
            TwinUIFactory.Place(head.rectTransform, RightX, 352f, RightW, 16f);
            head.text = "异常 Top3";

            string[][] rows =
            {
                new[] { "B1-001-配电-001", "回路断电 · 已持续 12 分钟", "严重" },
                new[] { "1F-101-插座-004", "功率 3.2kW / 量程 3.5kW", "警告" },
                new[] { "3F-302-空调-002", "离线 26 分钟", "离线" },
            };

            for (int i = 0; i < rows.Length; i++)
            {
                float y = 376f + i * (40f + 6f);

                TwinRoundedRect row = TwinUIFactory.Rounded(root, "Abn_" + i, TwinTheme.R8,
                    Color.clear, Color.clear, TwinTheme.Border, TwinTheme.StrokeHair.Width, false);
                TwinUIFactory.Place(row.rectTransform, RightX, y, RightW, 40f);

                Color sev = i == 0 ? TwinTheme.StatusAlarm
                          : i == 1 ? TwinTheme.StatusWarning
                          : TwinTheme.StatusOffline;

                Image bar = TwinUIFactory.Block(row.rectTransform, "Sev", sev, false);
                TwinUIFactory.Place(bar.rectTransform, 0f, 8f, TwinTheme.BarStatusWidth, 24f);

                Text id = TwinUIFactory.Label(row.rectTransform, "Id", TwinTheme.FsCaption,
                    TextAnchor.MiddleLeft, TwinTheme.TextPrimary);
                TwinUIFactory.Place(id.rectTransform, 12f, 0f, 150f, 40f);
                id.text = rows[i][0];

                Text metric = TwinUIFactory.Label(row.rectTransform, "Metric", TwinTheme.FsMicro,
                    TextAnchor.MiddleLeft, TwinTheme.TextMuted);
                TwinUIFactory.Place(metric.rectTransform, 168f, 0f, 190f, 40f);
                metric.text = rows[i][1];

                Text tag = TwinUIFactory.Label(row.rectTransform, "Tag", TwinTheme.FsMicro,
                    TextAnchor.MiddleRight, sev);
                TwinUIFactory.Place(tag.rectTransform, RightW - 84f, 0f, 68f, 40f);
                tag.text = rows[i][2];
            }
        }

        private void RefreshMonitor()
        {
            TwinRuntime rt = TwinRuntime.Instance;
            int online = 0, alarm = 0, total = 0;
            if (rt != null && rt.Registry != null)
            {
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
            }

            if (_kpiOnline != null) _kpiOnline.SetValue(total > 0 ? online.ToString() : "—");
            if (_kpiAlarm != null)
            {
                _kpiAlarm.SetValue(total > 0 ? alarm.ToString() : "—");
                _kpiAlarm.SetState(alarm > 0 ? TwinMetricCard.CardState.Alarm : TwinMetricCard.CardState.Normal);
            }

            // 总功率 / 今日用电 需要电力数据源（P3）。现在没有统一聚合口径 ——
            // 单设备 Power 的量程有的按 W、有的按 kW（灯 0~120W / 插座 0~3.5kW），
            // 直接求和会得出"1.2 + 42"这种没意义的数，所以宁可显示「—」。
            if (_kpiPower != null) _kpiPower.SetState(TwinMetricCard.CardState.NoData);
            if (_kpiEnergy != null) _kpiEnergy.SetState(TwinMetricCard.CardState.NoData);
        }
    }
}

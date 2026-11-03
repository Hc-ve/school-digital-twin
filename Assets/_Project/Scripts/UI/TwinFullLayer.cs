using System;
using System.Collections.Generic;
using SchoolTwin.Command;
using SchoolTwin.Core;
using SchoolTwin.Twin;
using UnityEngine;
using UnityEngine.UI;

namespace SchoolTwin.UI
{
    /// <summary>
    /// 全屏态（最大化）的 20/60/20 三栏内容层（P29-6 v2 规格）。
    ///
    /// ── 为什么单独一层，不去改窄态页面 ──────────────────────────────
    /// 窄态（420 / 640）已通过验收，规格 §10 明令「不改窄态」。而全屏是**另一套骨架**：
    /// 头 80 / 底 60、左 200 / 中 600 / 右 200、KPI 从横排 3 卡变竖排 3 卡。
    /// 硬塞进同一份页面代码里，等于给已通过的布局加一堆 `if (full)`，
    /// 迟早把窄态改坏。所以**全屏单独一层，切换时整层显隐**。
    ///
    /// ── 坐标 ────────────────────────────────────────────────────
    /// 全部是 1000×720 逻辑坐标，原点在抽屉左上角（规格 §4.1：不引入第二套坐标系）。
    /// 三画布已统一 1000×720 + Expand，Expand 下画布高恒为 720，所以这些 y 值在任何窗口下成立。
    ///
    /// ── 中区 600 是空的 ──────────────────────────────────────────
    /// 中间不画任何东西 —— 那是 3D 的可见区（规格 §4.2：3D 始终全屏渲染，不裁相机 viewport）。
    /// 左右栏是**浮在上面的实底卡片**，不是给 3D 让位的容器。
    /// </summary>
    public partial class TwinFullLayer : MonoBehaviour
    {
        // ---------------------------------------------------------------- 骨架常量（规格 §3）

        public const float RailW = 200f;        // 左右栏宽
        public const float MidW = 600f;         // 中间 3D 可见区
        public const float HeadH = 80f;         // 全屏顶带
        public const float FootH = 60f;         // 全屏底带
        public const float Pad = 16f;           // 栏内边距
        public const float ColW = RailW - Pad * 2f;   // 168：栏内内容宽

        private const float RightX = RailW + MidW;    // 800：右栏起点
        private const float RightInnerX = RightX + Pad; // 816

        public enum Mode
        {
            None = 0,
            Home = 1,      // L1 总控
            System = 2,    // L2 管理系统
        }

        private Mode _mode = Mode.None;
        private RectTransform _root;
        private CanvasGroup _group;

        // ---- 组容器（切换页面时整组显隐，这是不串页的关键）----
        private RectTransform _homeGroup;      // L1 左栏：KPI / 入口 / 动态
        private RectTransform _railGroup;      // L2 左轨：5 Tab
        private RectTransform _asideGroup;     // L2 辅栏：筛选 / 小结 / 追溯

        // ---- L1 左栏 ----
        private TwinMetricCard _kpiDevice;
        private TwinMetricCard _kpiOnline;
        private TwinMetricCard _kpiAlarm;
        private readonly Text[] _feedMain = new Text[3];
        private readonly Text[] _feedSub = new Text[3];
        private readonly Text[] _feedTime = new Text[3];
        private readonly TwinRoundedRect[] _feedDot = new TwinRoundedRect[3];
        private Text _feedMore;

        // ---- L1 右栏 ----
        private RectTransform _alarmGroup;
        private RectTransform _detailGroup;
        private readonly RectTransform[] _alarmCard = new RectTransform[3];
        private readonly Image[] _alarmBar = new Image[3];
        private readonly Text[] _alarmId = new Text[3];
        private readonly Text[] _alarmDesc = new Text[3];
        private readonly Text[] _alarmTag = new Text[3];
        /// <summary>
        /// 告警条编号的可用宽度（px）。由 <see cref="BuildRightRail"/> 按卡宽算出，
        /// 供 <see cref="RefreshAlarmRail"/> 的 `FitTextKeepTail` 使用 —— 两处不能各写一个数，
        /// 否则改卡宽时只改一处，编号就会重新压到告警等级上。
        /// </summary>
        private float _alarmIdWidth = 106f;
        private Text _alarmTitle;
        /// <summary>3 条之外的余量提示（P1-2：标题写 5、列表只 3 条，必须给"还有 N 条"）。</summary>
        private Text _moreAlarm;
        private readonly Text[] _typeName = new Text[4];
        private readonly Text[] _typeCount = new Text[4];
        private readonly Image[] _typeBar = new Image[4];
        private Text _footStatus;

        // ---- L1 右栏 · 设备详情态（规格 §5.1：全屏不弹 470 浮卡，改右栏切换）----
        private Text _dCode;
        private Text _dName;
        private TwinRoundedRect _dChip;
        private Text _dChipText;
        private readonly Text[] _dKey = new Text[4];
        private readonly Text[] _dVal = new Text[4];

        // ---- L2 左轨 ----
        private readonly RectTransform[] _tabs = new RectTransform[5];
        private readonly TwinRoundedRect[] _tabBg = new TwinRoundedRect[5];
        private readonly Image[] _tabBar = new Image[5];
        private readonly Text[] _tabLabel = new Text[5];
        private readonly Text[] _tabIcon = new Text[5];
        private int _tab = 0;

        // ---- L2 主栏 ----
        private RectTransform _monitorGroup;
        private RectTransform _deviceGroup;
        private RectTransform _powerGroup;
        private RectTransform _alarmCenterGroup;
        private RectTransform _statsGroup;
        private RectTransform _emptyGroup;

        private TwinMetricCard _sysPower;
        private TwinMetricCard _sysEnergy;
        private TwinMetricCard _sysOnline;
        private TwinMetricCard _sysAlarm;
        private Text _healthPct;
        /// <summary>健康卡本体。有告警/离线时外框转红/灰，让"这一块有事"能一眼看到。</summary>
        private TwinRoundedRect _healthCard;
        private readonly Image[] _healthSeg = new Image[3];
        private readonly Text[] _healthLeg = new Text[3];
        private readonly TwinRoundedRect[] _healthDot = new TwinRoundedRect[3];
        private readonly RectTransform[] _abnRow = new RectTransform[3];
        private readonly Image[] _abnBar = new Image[3];
        private readonly Text[] _abnId = new Text[3];
        private readonly Text[] _abnTag = new Text[3];
        private Text _sysTitle;

        // ---- L2 设备管理页（六列台账）----
        private const int DevRows = 64;   // 【C-31】对象池容量（全场景 53 台，留余量）；原来固定 7 行看不到后面的设备
        private const float DevRowH = 40f;
        private readonly RectTransform[] _devRow = new RectTransform[DevRows];
        private readonly TwinRoundedRect[] _devRowBg = new TwinRoundedRect[DevRows];
        /// <summary>【C-31】台账滚动内容容器（刷新时按行数设高度）。</summary>
        private RectTransform _devContent;
        private readonly Image[] _devBar = new Image[DevRows];
        private readonly Text[] _devId = new Text[DevRows];
        private readonly Text[] _devName = new Text[DevRows];
        private readonly Text[] _devPos = new Text[DevRows];
        private readonly Text[] _devType = new Text[DevRows];
        private readonly Text[] _devTag = new Text[DevRows];
        private readonly Text[] _devSwitch = new Text[DevRows];
        private Text _devCount;
        private readonly List<TwinAnchor> _devBuf = new List<TwinAnchor>(DevRows);

        // ---- L2 空状态页 ----
        private Text _emptyTitle;
        private Text _emptyBody;

        // ---- L2 辅栏 ----
        /// <summary>右栏筛选：0=全部楼层 / 1=1F / 2=2F / 3=室外。设备管理与台账共用。</summary>
        private int _chipFilter;
        private readonly TwinRoundedRect[] _chipBg = new TwinRoundedRect[4];
        private readonly Text[] _chipText = new Text[4];

        private Text _sumTotal;
        private Text _sumScope;
        private Text _sumMissing;
        private Text _sumOffline;

        // ---- 缓冲 ----
        private readonly List<TwinAlarmHistory.AlarmRecord> _alarmBuf =
            new List<TwinAlarmHistory.AlarmRecord>(64);
        private float _nextRefresh;

        private static readonly string[] TabNames = { "数据监测", "设备管理", "电力管理", "告警中心", "数据统计" };
        private static readonly string[] TabIcons = { "监", "设", "电", "告", "统" };

        // ================================================================ 构建

        public void Build(RectTransform parent)
        {
            _root = (RectTransform)transform;
            _root.SetParent(parent, false);
            TwinUIFactory.Stretch(_root);

            _group = gameObject.AddComponent<CanvasGroup>();
            _group.alpha = 1f;

            BuildTopDivider();
            BuildColumnDivider();
            BuildLeftRail();
            BuildRightRail();
            BuildSystemRail();
            BuildSystemMain();
            BuildSystemAside();
            BuildFooter();

            _mode = Mode.None;
            gameObject.SetActive(false);
        }

        /// <summary>顶带底部的主色分隔线（规格 §7：全屏独有，主色 @0.25）。</summary>
        private void BuildTopDivider()
        {
            Image line = TwinUIFactory.Block(_root, "TopDivider",
                TwinTheme.WithAlpha(TwinTheme.Accent, 0.25f), false);
            TwinUIFactory.Place(line.rectTransform, 0f, HeadH - 1f, RailW * 2f + MidW, 1f);
        }

        /// <summary>
        /// 主栏与右栏的分界线（见微 P0-2）。
        ///
        /// 没有这条线时，右栏「筛选 / 台账小结 / 全链路追溯」和主栏「KPI / 健康条 / Top3」
        /// 直接并排，读起来像"一栏两列内容"，而不是两个区。
        /// x=800、y 92–652，白 @0.06 —— 比卡片描边（@0.006）明显，比主色线（@0.25）克制。
        /// </summary>
        private void BuildColumnDivider()
        {
            Color c = TwinTheme.WithAlpha(Color.white, 0.06f);

            // x=200：左栏与 3D 的分界（见微第三轮要求"能量取到 x=200"，
            // 3D 是透明区、没有实体边界，不给线就没法判断左栏到底多宽）
            Image left = TwinUIFactory.Block(_root, "ColDivLeft", c, false);
            TwinUIFactory.Place(left.rectTransform, RailW, 92f, 1f, 560f);

            // x=800：3D 与右栏的分界
            Image right = TwinUIFactory.Block(_root, "ColDivRight", c, false);
            TwinUIFactory.Place(right.rectTransform, RightX, 92f, 1f, 560f);
        }

        /// <summary>底带右侧的时间 / 版本（规格 §7：全屏独有）。</summary>
        private void BuildFooter()
        {
            Text v = TwinUIFactory.Label(_root, "FootVer", TwinTheme.FsMicro,
                TextAnchor.MiddleRight, TwinTheme.TextMuted);
            TwinUIFactory.Place(v.rectTransform, RailW * 2f + MidW - 20f - 160f, 720f - FootH, 160f, FootH);

            // 【P5-1 P1-4】这里**只写版本号，不写时间**。
            //
            // 原来写 `"v0.9 · " + HH:mm:ss`，而统计页页脚右段（`FFootR`）也是
            // 「更新 HH:mm:ss」，两个时间戳同屏 —— 主理人明确反感的"信息重复"。
            // 时间由统计页那份单独承担（它 0.5s 刷一次，本来就准）；
            // 底带这只是"版本标识"，不带时间语义。
            v.text = "v0.9";
            v.raycastTarget = false;
        }

        // ---------------------------------------------------------------- L1 左栏

        private void BuildLeftRail()
        {
            _homeGroup = NewGroup("HomeRail");

            // 三张 KPI 竖排 168×88（规格 §3.3：y=96 / 192 / 288）
            _kpiDevice = TwinMetricCard.Create(_homeGroup, "FullKpi_Device", Pad, 96f,
                ColW, 88f, TwinTheme.FsDisplay, "设备总数", TwinCode.UnitDevice);
            _kpiOnline = TwinMetricCard.Create(_homeGroup, "FullKpi_Online", Pad, 192f,
                ColW, 88f, TwinTheme.FsDisplay, "在线", TwinCode.UnitDevice);
            _kpiAlarm = TwinMetricCard.Create(_homeGroup, "FullKpi_Alarm", Pad, 288f,
                ColW, 88f, TwinTheme.FsDisplay, "告警", TwinCode.UnitDevice);

            // 卡片序号 01/02/03（规格 §7：全屏独有）
            CardIndex(_kpiDevice, "01");
            CardIndex(_kpiOnline, "02");
            CardIndex(_kpiAlarm, "03");

            // 功能入口
            SectionTitle(Pad, 392f, "功能入口");
            BuildEntry("管", "校园管理系统", "5 个模块", Pad, 406f, true);
            BuildEntry("天", "天气系统", "预留", Pad + 84f, 406f, false);

            // 实时动态 3 行 ×44
            SectionTitle(Pad, 510f, "实时动态");
            _feedMore = TwinUIFactory.Label(_homeGroup, "FeedMore", TwinTheme.FsMicro,
                TextAnchor.UpperRight, TwinTheme.TextMuted);
            TwinUIFactory.Place(_feedMore.rectTransform, Pad, 510f, ColW, 14f);
            _feedMore.text = "";
            _feedMore.raycastTarget = false;

            for (int i = 0; i < 3; i++)
            {
                float y = 524f + i * 44f;

                TwinRoundedRect dot = TwinUIFactory.Rounded(_homeGroup, "FDot_" + i, 3f,
                    TwinTheme.StatusUnknown, TwinTheme.StatusUnknown, Color.clear, 0f, false);
                TwinUIFactory.Place(dot.rectTransform, Pad, y + 16f, 6f, 6f);
                _feedDot[i] = dot;

                Text main = TwinUIFactory.Label(_homeGroup, "FMain_" + i, TwinTheme.FsCaption,
                    TextAnchor.UpperLeft, TwinTheme.TextSecondary);
                TwinUIFactory.Place(main.rectTransform, Pad + 12f, y + 4f, ColW - 12f - 44f, 16f);
                _feedMain[i] = main;

                Text sub = TwinUIFactory.Label(_homeGroup, "FSub_" + i, TwinTheme.FsMicro,
                    TextAnchor.UpperLeft, TwinTheme.TextMuted);
                TwinUIFactory.Place(sub.rectTransform, Pad + 12f, y + 22f, ColW - 12f - 44f, 12f);
                _feedSub[i] = sub;

                Text time = TwinUIFactory.Label(_homeGroup, "FTime_" + i, TwinTheme.FsMicro,
                    TextAnchor.UpperRight, TwinTheme.TextMuted);
                TwinUIFactory.Place(time.rectTransform, Pad + ColW - 44f, y + 4f, 44f, 12f);
                _feedTime[i] = time;
            }
        }

        private void CardIndex(TwinMetricCard card, string index)
        {
            if (card == null) return;
            Text t = TwinUIFactory.Label(card.Rect, "Idx", TwinTheme.FsMicro,
                TextAnchor.UpperRight, TwinTheme.TextMuted);
            TwinUIFactory.Place(t.rectTransform, ColW - 32f, 10f, 20f, 12f);
            t.text = index;
            t.raycastTarget = false;
        }

        private void BuildEntry(string glyph, string name, string note, float x, float y, bool on)
        {
            TwinRoundedRect card = TwinUIFactory.Rounded(_homeGroup, "Entry_" + name, TwinTheme.R12,
                on ? TwinTheme.AccentDim : TwinTheme.CardTop,
                on ? TwinTheme.AccentDim : TwinTheme.CardBottom,
                on ? TwinTheme.AccentLine : TwinTheme.Border, TwinTheme.StrokeHair.Width, true);
            TwinUIFactory.Place(card.rectTransform, x, y, 76f, 88f);

            TwinRoundedRect icon = TwinUIFactory.Rounded(card.rectTransform, "Icon", TwinTheme.R8,
                TwinTheme.EntryIconFill, TwinTheme.EntryIconFill, Color.clear, 0f, false);
            TwinUIFactory.Place(icon.rectTransform, 22f, 12f, 32f, 32f);

            Text g = TwinUIFactory.Label(icon.rectTransform, "G", TwinTheme.FsSubtitle,
                TextAnchor.MiddleCenter, on ? TwinTheme.Accent : TwinTheme.TextMuted);
            TwinUIFactory.Stretch(g.rectTransform);
            g.text = glyph;
            g.raycastTarget = false;

            Text n = TwinUIFactory.Label(card.rectTransform, "N", TwinTheme.FsMicro,
                TextAnchor.UpperCenter, on ? TwinTheme.TextPrimary : TwinTheme.TextSecondary);
            TwinUIFactory.Place(n.rectTransform, 2f, 52f, 72f, 14f);
            n.text = name;
            n.raycastTarget = false;

            Text s = TwinUIFactory.Label(card.rectTransform, "S", TwinTheme.FsMicro,
                TextAnchor.UpperCenter, TwinTheme.TextMuted);
            TwinUIFactory.Place(s.rectTransform, 2f, 68f, 72f, 12f);
            s.text = note;
            s.raycastTarget = false;
        }

        private void SectionTitle(float x, float y, string text)
        {
            Text t = TwinUIFactory.Label(_homeGroup, "Sec_" + text, TwinTheme.FsMicro,
                TextAnchor.UpperLeft, TwinTheme.TextMuted);
            TwinUIFactory.Place(t.rectTransform, x, y, ColW, 14f);
            t.text = text;
            t.raycastTarget = false;
        }

        // ---------------------------------------------------------------- L1 右栏

        private void BuildRightRail()
        {
            _alarmGroup = NewGroup("AlarmRail");
            _detailGroup = NewGroup("DetailRail");

            _alarmTitle = TwinUIFactory.Label(_alarmGroup, "RAlarmTitle", TwinTheme.FsCaption,
                TextAnchor.UpperLeft, TwinTheme.TextSecondary);
            TwinUIFactory.Place(_alarmTitle.rectTransform, RightInnerX, 96f, ColW, 16f);
            _alarmTitle.text = "告警";

            // 告警条 ×3：y=112 / 180 / 248，高 64
            for (int i = 0; i < 3; i++)
            {
                float y = 112f + i * 68f;

                TwinRoundedRect card = TwinUIFactory.Rounded(_alarmGroup, "RAlarm_" + i, TwinTheme.R8,
                    TwinTheme.CardTop, TwinTheme.CardBottom, TwinTheme.Border,
                    TwinTheme.StrokeHair.Width, false);
                TwinUIFactory.Place(card.rectTransform, RightInnerX, y, ColW, 64f);
                _alarmCard[i] = card.rectTransform;

                Image bar = TwinUIFactory.Block(card.rectTransform, "Bar",
                    TwinTheme.StatusAlarm, false);
                TwinUIFactory.Place(bar.rectTransform, 0f, 10f, 3f, 44f);
                _alarmBar[i] = bar;

                Text id = TwinUIFactory.Label(card.rectTransform, "Id", TwinTheme.FsCaption,
                    TextAnchor.UpperLeft, TwinTheme.TextPrimary);
                TwinUIFactory.Place(id.rectTransform, 12f, 10f, ColW - 60f, 16f);
                _alarmId[i] = id;

                // 【2026-11-03 文案变长后加保底截断】编号从 `1F104 电脑` 变成
                // `1F104 机房电脑001` 之后，告警卡这一行到了**刀锋边缘**：
                //
                //   卡宽 ColW = 168，id 起点 12、宽 108 → 文字框右缘 120
                //   但右边 tag（告警等级）框从 112 起，右对齐、「数据陈旧」4 字 @9 = 36px，
                //   实际占 120..156 —— 所以 id 的**真正可用宽是 108px**。
                //   而 70 台里最长的 `2F201 CO₂传感器001` @11 实测约 107.8px。
                //
                // 差 0.2px 就是 0。Legacy Text 是 Overflow 模式：超出不换行、
                // 不省略，直接画到 tag 上 —— 表现是「告警」两个字被编号压住。
                // 所以这里不靠"刚好放得下"，套 `FitText`（中英文分别计宽）。
                // 106 = 108 让 2px 余量，给字体度量误差留空间。
                //
                // ⚠️ 不要改成 `TwinUIFactory.Truncate`：它按 `fontSize×0.62` 一律估宽，
                //    中文会被少算 38%，等于没截 —— 详见 `TwinUIFactory.FitText` 的注释。
                _alarmIdWidth = ColW - 60f - 2f;

                Text desc = TwinUIFactory.Label(card.rectTransform, "Desc", TwinTheme.FsMicro,
                    TextAnchor.UpperLeft, TwinTheme.TextMuted);
                TwinUIFactory.Place(desc.rectTransform, 12f, 28f, ColW - 24f, 14f);
                _alarmDesc[i] = desc;

                Text tag = TwinUIFactory.Label(card.rectTransform, "Tag", TwinTheme.FsMicro,
                    TextAnchor.UpperRight, TwinTheme.StatusAlarm);
                TwinUIFactory.Place(tag.rectTransform, ColW - 56f, 10f, 44f, 12f);
                _alarmTag[i] = tag;
            }

            // 分隔线
            Image div = TwinUIFactory.Block(_alarmGroup, "RDiv", TwinTheme.Divider, false);
            TwinUIFactory.Place(div.rectTransform, RightInnerX, 320f, ColW, 1f);

            // 【P1-2】3 条之外还有多少。放在第 3 条（底 312）与分隔线（320）之间，
            // 不能省 —— 标题写「告警 5」而列表只 3 条、又不说还有几条，读起来像丢了数据。
            _moreAlarm = TwinUIFactory.Label(_alarmGroup, "RAlarmMore", TwinTheme.FsMicro,
                TextAnchor.UpperRight, TwinTheme.TextMuted);
            TwinUIFactory.Place(_moreAlarm.rectTransform, RightInnerX, 314f, ColW, 12f);
            _moreAlarm.raycastTarget = false;
            // 设备类型
            Text typeTitle = TwinUIFactory.Label(_alarmGroup, "RTypeTitle", TwinTheme.FsCaption,
                TextAnchor.UpperLeft, TwinTheme.TextSecondary);
            TwinUIFactory.Place(typeTitle.rectTransform, RightInnerX, 336f, ColW, 16f);
            typeTitle.text = "设备类型";

            for (int i = 0; i < 4; i++)
            {
                float y = 356f + i * 32f;

                Text n = TwinUIFactory.Label(_alarmGroup, "TName_" + i, TwinTheme.FsMicro,
                    TextAnchor.MiddleLeft, TwinTheme.TextMuted);
                TwinUIFactory.Place(n.rectTransform, RightInnerX, y, 44f, 28f);
                _typeName[i] = n;

                TwinRoundedRect track = TwinUIFactory.Rounded(_alarmGroup, "TTrack_" + i, 2f,
                    TwinTheme.Track, TwinTheme.Track, Color.clear, 0f, false);
                TwinUIFactory.Place(track.rectTransform, RightInnerX + 46f, y + 12f, 78f, 4f);

                Image fill = TwinUIFactory.Block(track.rectTransform, "Fill",
                    TwinTheme.Accent, false);
                TwinUIFactory.Place(fill.rectTransform, 0f, 0f, 78f, 4f);
                _typeBar[i] = fill;

                Text c = TwinUIFactory.Label(_alarmGroup, "TCount_" + i, TwinTheme.FsMicro,
                    TextAnchor.MiddleRight, TwinTheme.TextSecondary);
                TwinUIFactory.Place(c.rectTransform, RightInnerX + ColW - 24f, y, 24f, 28f);
                _typeCount[i] = c;
            }

            _footStatus = TwinUIFactory.Label(_alarmGroup, "RStatus", TwinTheme.FsMicro,
                TextAnchor.MiddleLeft, TwinTheme.TextMuted);
            TwinUIFactory.Place(_footStatus.rectTransform, RightInnerX, 620f, ColW, 20f);
            _footStatus.text = "";
            _footStatus.raycastTarget = false;

            BuildDetailRail();
        }

        /// <summary>
        /// 右栏的「设备详情」态（规格 §5.1）。
        ///
        /// 全屏三栏下 3D 只有 600 宽，再弹一张 470 的浮卡就只剩 130 —— 等于把核心卖点毁掉。
        /// 所以全屏态**不弹浮卡**，改成右栏在「告警列表」和「设备详情」之间切换。
        /// 窄态仍然用 470 浮卡（已验收，不动）。
        ///
        /// 返回不设按钮：取消选中（Esc / 点空白）就自动切回告警列表 ——
        /// 少一个按钮就少一处"点了没反应"的可能。
        /// </summary>
        private void BuildDetailRail()
        {
            Text title = TwinUIFactory.Label(_detailGroup, "DTitle", TwinTheme.FsCaption,
                TextAnchor.UpperLeft, TwinTheme.TextSecondary);
            TwinUIFactory.Place(title.rectTransform, RightInnerX, 96f, ColW, 16f);
            title.text = "设备详情";

            Text back = TwinUIFactory.Label(_detailGroup, "DBack", TwinTheme.FsMicro,
                TextAnchor.UpperRight, TwinTheme.TextMuted);
            TwinUIFactory.Place(back.rectTransform, RightInnerX, 96f, ColW, 16f);
            back.text = "Esc 返回";
            back.raycastTarget = false;

            // 全码：`1号教学楼-2F-201-空调-001`，窄栏放不下就折两行
            _dCode = TwinUIFactory.Label(_detailGroup, "DCode", TwinTheme.FsCaption,
                TextAnchor.UpperLeft, TwinTheme.TextPrimary);
            TwinUIFactory.Place(_dCode.rectTransform, RightInnerX, 120f, ColW, 34f);
            _dCode.horizontalOverflow = HorizontalWrapMode.Wrap;
            _dCode.verticalOverflow = VerticalWrapMode.Truncate;
            _dCode.lineSpacing = 1.25f;
            _dCode.raycastTarget = false;

            _dName = TwinUIFactory.Label(_detailGroup, "DName", TwinTheme.FsMicro,
                TextAnchor.UpperLeft, TwinTheme.TextMuted);
            TwinUIFactory.Place(_dName.rectTransform, RightInnerX, 158f, ColW, 14f);
            _dName.raycastTarget = false;

            _dChip = TwinUIFactory.Rounded(_detailGroup, "DChip", 10f,
                TwinTheme.StatusNormal, TwinTheme.StatusNormal, Color.clear, 0f, false);
            TwinUIFactory.Place(_dChip.rectTransform, RightInnerX, 180f, 64f, 20f);

            _dChipText = TwinUIFactory.Label(_dChip.rectTransform, "T", TwinTheme.FsMicro,
                TextAnchor.MiddleCenter, TwinTheme.TextPrimary);
            TwinUIFactory.Stretch(_dChipText.rectTransform);
            _dChipText.raycastTarget = false;

            string[] keys = { "位置", "类型", "指标数", "数据更新" };
            for (int i = 0; i < 4; i++)
            {
                float y = 216f + i * 34f;

                Text k = TwinUIFactory.Label(_detailGroup, "DK_" + i, TwinTheme.FsMicro,
                    TextAnchor.MiddleLeft, TwinTheme.TextMuted);
                TwinUIFactory.Place(k.rectTransform, RightInnerX, y, 60f, 20f);
                k.text = keys[i];
                _dKey[i] = k;

                Text v = TwinUIFactory.Label(_detailGroup, "DV_" + i, TwinTheme.FsMicro,
                    TextAnchor.MiddleRight, TwinTheme.TextSecondary);
                TwinUIFactory.Place(v.rectTransform, RightInnerX + 60f, y, ColW - 60f, 20f);
                _dVal[i] = v;
            }

            _detailGroup.gameObject.SetActive(false);
        }

        // ---------------------------------------------------------------- L2 左轨

        private void BuildSystemRail()
        {
            _railGroup = NewGroup("SysRail");

            for (int i = 0; i < 5; i++)
            {
                float y = 88f + i * 52f;

                TwinRoundedRect bg = TwinUIFactory.Rounded(_railGroup, "FTab_" + i, TwinTheme.R8,
                    Color.clear, Color.clear, Color.clear, 0f, true);
                TwinUIFactory.Place(bg.rectTransform, 8f, y, RailW - 16f, 48f);
                _tabBg[i] = bg;

                Image bar = TwinUIFactory.Block(bg.rectTransform, "Bar", TwinTheme.Accent, false);
                TwinUIFactory.Place(bar.rectTransform, 0f, 13f, 3f, 22f);
                _tabBar[i] = bar;

                TwinRoundedRect icon = TwinUIFactory.Rounded(bg.rectTransform, "Icon", TwinTheme.R8,
                    TwinTheme.EntryIconFill, TwinTheme.EntryIconFill, Color.clear, 0f, false);
                TwinUIFactory.Place(icon.rectTransform, 14f, 13f, 22f, 22f);

                Text g = TwinUIFactory.Label(icon.rectTransform, "G", TwinTheme.FsMicro,
                    TextAnchor.MiddleCenter, TwinTheme.TextMuted);
                TwinUIFactory.Stretch(g.rectTransform);
                g.text = TabIcons[i];
                g.raycastTarget = false;
                _tabIcon[i] = g;

                Text label = TwinUIFactory.Label(bg.rectTransform, "Label", TwinTheme.FsBody,
                    TextAnchor.MiddleLeft, TwinTheme.TextSecondary);
                TwinUIFactory.Place(label.rectTransform, 46f, 0f, RailW - 70f, 48f);
                label.text = TabNames[i];
                label.raycastTarget = false;
                _tabLabel[i] = label;

                int index = i;
                Button btn = bg.gameObject.AddComponent<Button>();
                btn.transition = Selectable.Transition.None;
                btn.onClick.AddListener(delegate { SelectTab(index); });
            }

            SelectTab(0);
        }

        private void SelectTab(int index)
        {
            _tab = index;
            for (int i = 0; i < 5; i++)
            {
                bool on = i == index;
                _tabBg[i].SetFill(on ? TwinTheme.AccentDim : Color.clear,
                                  on ? TwinTheme.AccentDim : Color.clear);
                _tabBar[i].gameObject.SetActive(on);
                _tabLabel[i].color = on ? TwinTheme.TextPrimary : TwinTheme.TextSecondary;
                _tabIcon[i].color = on ? TwinTheme.Accent : TwinTheme.TextMuted;
            }

            // 主栏五组互斥（BuildSystemRail 先于 BuildSystemMain，第一次进来还是 null，判空跳过）
            if (_monitorGroup != null) _monitorGroup.gameObject.SetActive(index == 0);
            if (_deviceGroup != null) _deviceGroup.gameObject.SetActive(index == 1);
            if (_powerGroup != null) _powerGroup.gameObject.SetActive(index == 2);
            if (_alarmCenterGroup != null) _alarmCenterGroup.gameObject.SetActive(index == 3);
            if (_statsGroup != null) _statsGroup.gameObject.SetActive(index == 4);

            // 【P5-1 P0-2】每次进统计页都把曲线重置并从历史层重新回填。
            //
            // 必须放在 `_statsGroup.SetActive(true)` **之后** —— 曲线是这一组的子物体，
            // 组没激活时 `Clear()` 会触发一次无效重建。
            // 重置的意义见 `ResetStatsSpark()` 注释：span 按"缓冲最早点"算，
            // 留着旧点会让新点挤到右端（P0-2 截图的直接成因）。
            if (index == 4) ResetStatsSpark();

            // 【P5】统计页进**满宽态**：右栏让位，中栏 600 + 右栏 200 合并成 800。
            // 其余四页保持原布局（负责人裁决：只允许统计页动）。
            //
            // 【P10】**这里不再自己切 `_asideGroup`**。
            // 原来写的是 `_asideGroup.SetActive(index != 4)` —— 它只认"是不是统计页"，
            // 不认"设备管理页是否正在看详情"。于是从设备详情切走再切回来，
            // 辅栏被无条件打开，和设备详情组**同时显示**、两层内容叠在 200px 右栏里。
            // 现在三组的可见性统一由 `ApplySystemAside()` 裁决（唯一出口），
            // 它在 `RefreshSystem()` 开头就会跑，这里只负责把 Tab 状态设好。
            if (_emptyGroup != null) _emptyGroup.gameObject.SetActive(false);

            _nextRefresh = 0f;   // 切页立刻刷一次，不等下一个 0.5s
        }

        // ---------------------------------------------------------------- L2 主栏

        private void BuildSystemMain()
        {
            _monitorGroup = NewGroup("MonitorGroup");
            _deviceGroup = NewGroup("DeviceGroup");
            _powerGroup = NewGroup("PowerGroup");
            _alarmCenterGroup = NewGroup("AlarmCenterGroup");
            _statsGroup = NewGroup("StatsGroup");
            _emptyGroup = NewGroup("EmptyGroup");

            BuildMonitorPage();
            BuildDevicePage();
            BuildPowerPage();
            BuildAlarmCenterPage();
            BuildStatsPage();
            BuildEmptyPage();

            _deviceGroup.gameObject.SetActive(false);
            _powerGroup.gameObject.SetActive(false);
            _alarmCenterGroup.gameObject.SetActive(false);
            _statsGroup.gameObject.SetActive(false);
            _emptyGroup.gameObject.SetActive(false);
        }

        /// <summary>主栏内的分组容器。Stretch 到 _root，本地坐标与 _root 完全一致 —— 所以组内仍用绝对坐标。</summary>
        private RectTransform NewGroup(string name)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            RectTransform rt = (RectTransform)go.transform;
            rt.SetParent(_root, false);
            TwinUIFactory.Stretch(rt);
            return rt;
        }

        /// <summary>数据监测页：模块头 + KPI 2×2 + 健康卡 + Top3（规格 §3.2）。</summary>
        private void BuildMonitorPage()
        {
            const float x = RailW + Pad;     // 216
            const float w = MidW - Pad * 2f; // 568

            _sysTitle = TwinUIFactory.Label(_monitorGroup, "FSysTitle", TwinTheme.FsSubtitle,
                TextAnchor.UpperLeft, TwinTheme.TextPrimary);
            TwinUIFactory.Place(_sysTitle.rectTransform, x, 92f, w, 22f);
            _sysTitle.text = "数据监测";

            Text desc = TwinUIFactory.Label(_monitorGroup, "FSysDesc", TwinTheme.FsMicro,
                TextAnchor.UpperLeft, TwinTheme.TextMuted);
            TwinUIFactory.Place(desc.rectTransform, x, 116f, w, 14f);
            desc.text = "全场景设备实时数据 · 综合概览 + 单设备详情（用电量 / 功率 / 温度…）";

            // KPI 2×2：280×88，x=216 / 504，y=140 / 236
            const float kw = 280f;
            _sysPower = TwinMetricCard.Create(_monitorGroup, "FKpi_Power", x, 140f, kw, 88f,
                TwinTheme.FsTitle, "总功率", "kW");
            _sysEnergy = TwinMetricCard.Create(_monitorGroup, "FKpi_Energy", x + kw + 8f, 140f, kw, 88f,
                TwinTheme.FsTitle, "今日用电", "kWh");
            _sysEnergy.SetEstimateNote("按额定功率 × 运行时长");   // C-28：与功率卡口径不同，各自说清
            _sysOnline = TwinMetricCard.Create(_monitorGroup, "FKpi_Online", x, 236f, kw, 88f,
                TwinTheme.FsTitle, "在线", TwinCode.UnitDevice);
            _sysAlarm = TwinMetricCard.Create(_monitorGroup, "FKpi_Alarm", x + kw + 8f, 236f, kw, 88f,
                TwinTheme.FsTitle, "告警", TwinCode.UnitDevice);

            BuildHealthCard(_monitorGroup, x, 340f, w);
            BuildTop3(_monitorGroup, x, 444f, w);
        }

        /// <summary>
        /// 设备管理页：**六列台账**（规格 §5 ⑥：编号 150 / 名称 150 / 位置 90 / 类型 72 / 状态 56 / 开关 44）。
        /// 主栏内容宽 568，六列 + 5×6 gap 收到 546，留 22 余量。
        /// </summary>
        private void BuildDevicePage()
        {
            const float x = RailW + Pad;
            const float w = MidW - Pad * 2f;

            Text title = TwinUIFactory.Label(_deviceGroup, "FDevTitle", TwinTheme.FsSubtitle,
                TextAnchor.UpperLeft, TwinTheme.TextPrimary);
            TwinUIFactory.Place(title.rectTransform, x, 92f, w, 22f);
            title.text = "设备管理";

            _devCount = TwinUIFactory.Label(_deviceGroup, "FDevCount", TwinTheme.FsMicro,
                TextAnchor.UpperLeft, TwinTheme.TextMuted);
            TwinUIFactory.Place(_devCount.rectTransform, x, 118f, w, 14f);

            // 六列表头
            string[] heads = { "编号", "名称", "位置", "类型", "状态", "开关" };
            float[] hx = { 0f, 146f, 292f, 378f, 448f, 506f };
            float[] hw = { 140f, 140f, 80f, 64f, 52f, 40f };

            for (int i = 0; i < heads.Length; i++)
            {
                Text h = TwinUIFactory.Label(_deviceGroup, "FTh_" + i, TwinTheme.FsMicro,
                    TextAnchor.MiddleLeft, TwinTheme.TextMuted);
                TwinUIFactory.Place(h.rectTransform, x + hx[i], 142f, hw[i], 20f);
                h.text = heads[i];
                h.raycastTarget = false;
            }

            Image line = TwinUIFactory.Block(_deviceGroup, "FDevLine", TwinTheme.Divider, false);
            TwinUIFactory.Place(line.rectTransform, x, 164f, w, 1f);

            // ---- 【C-31】台账列表：改成可滚动 ----
            //
            // 【为什么必须改】原来固定 7 行、没有 ScrollRect ——
            // 全场景 53 台设备只能看到前 7 台，剩下的永远看不见，也没法滑
            // （主理人 play 实测"设备不够，无法滑动"）。
            //
            // 结构：Scroll(收滚轮) → Viewport(RectMask2D 裁剪) → Content(承载所有行)
            // 这是 UGUI 标准三件套，和电力页那套一致。
            GameObject scrollGo = TwinUIFactory.Node("FDevScroll", _deviceGroup);
            RectTransform scrollRt = (RectTransform)scrollGo.transform;
            TwinUIFactory.Place(scrollRt, x, 172f, w, 468f);   // 172 → 640，一屏 11 行

            // ScrollRect 的滚轮事件是**沿射线命中的 Graphic 派发**的，
            // scrollRt 本身没有 Graphic → 必须铺一层全透明垫片，否则滚轮收不到。
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

            ScrollRect sr = scrollGo.AddComponent<ScrollRect>();
            sr.viewport = viewRt;
            sr.content = contentRt;
            sr.horizontal = false;
            sr.vertical = true;
            sr.movementType = ScrollRect.MovementType.Clamped;
            sr.scrollSensitivity = 24f;

            // 行对象池：一次建满 DevRows 个，刷新时只改文字/显隐，不反复 Instantiate
            for (int i = 0; i < DevRows; i++)
            {
                float y = i * DevRowH;

                TwinRoundedRect row = TwinUIFactory.Rounded(contentRt, "FDevRow_" + i, TwinTheme.R8,
                    Color.clear, Color.clear, TwinTheme.Border, TwinTheme.StrokeHair.Width, true);
                TwinUIFactory.Place(row.rectTransform, 0f, y, w, 36f);
                _devRow[i] = row.rectTransform;
                _devRowBg[i] = row;

                // 整行可点 → 选中该设备并让相机飞过去（复用 TwinDetailPanel.FocusOn）。
                // 原来"详情 ›"只是一行静态文字，点不动 —— 主理人 play 实测报的"死按钮"。
                int devIdx = i;
                Button rowBtn = row.gameObject.AddComponent<Button>();
                rowBtn.transition = Selectable.Transition.None;
                rowBtn.onClick.AddListener(delegate { OnDeviceRowClicked(devIdx); });

                Image bar = TwinUIFactory.Block(row.rectTransform, "Bar", TwinTheme.StatusNormal, false);
                TwinUIFactory.Place(bar.rectTransform, 0f, 8f, 3f, 20f);
                _devBar[i] = bar;

                _devId[i] = DevCell(row.rectTransform, "Id", hx[0] + 12f, hw[0], TwinTheme.FsCaption, TwinTheme.TextPrimary);
                _devName[i] = DevCell(row.rectTransform, "Name", hx[1], hw[1], TwinTheme.FsCaption, TwinTheme.TextSecondary);
                _devPos[i] = DevCell(row.rectTransform, "Pos", hx[2], hw[2], TwinTheme.FsMicro, TwinTheme.TextMuted);
                _devType[i] = DevCell(row.rectTransform, "Type", hx[3], hw[3], TwinTheme.FsMicro, TwinTheme.TextMuted);
                _devTag[i] = DevCell(row.rectTransform, "Tag", hx[4], hw[4], TwinTheme.FsMicro, TwinTheme.TextMuted);
                _devSwitch[i] = DevCell(row.rectTransform, "Sw", hx[5], hw[5], TwinTheme.FsMicro, TwinTheme.Accent);
            }
        }

        private static Text DevCell(RectTransform row, string name, float x, float w, int size, Color color)
        {
            Text t = TwinUIFactory.Label(row, name, size, TextAnchor.MiddleLeft, color);
            TwinUIFactory.Place(t.rectTransform, x, 0f, w, 36f);
            t.raycastTarget = false;
            return t;
        }

        /// <summary>电力 / 告警 / 统计三页在全屏下暂用空状态（P29-6 §10：本轮不做 P29-5）。</summary>
        private void BuildEmptyPage()
        {
            const float x = RailW + Pad;
            const float w = MidW - Pad * 2f;

            _emptyTitle = TwinUIFactory.Label(_emptyGroup, "FEmptyTitle", TwinTheme.FsSubtitle,
                TextAnchor.UpperLeft, TwinTheme.TextPrimary);
            TwinUIFactory.Place(_emptyTitle.rectTransform, x, 92f, w, 22f);

            _emptyBody = TwinUIFactory.Label(_emptyGroup, "FEmptyBody", TwinTheme.FsCaption,
                TextAnchor.UpperLeft, TwinTheme.TextMuted);
            TwinUIFactory.Place(_emptyBody.rectTransform, x, 132f, w, 60f);
            _emptyBody.lineSpacing = 1.4f;
        }

        private void BuildHealthCard(RectTransform parent, float x, float y, float w)
        {
            TwinRoundedRect card = TwinUIFactory.Rounded(parent, "FHealth", TwinTheme.R12,
                TwinTheme.CardTop, TwinTheme.CardBottom, TwinTheme.Border, TwinTheme.StrokeHair.Width, false);
            TwinUIFactory.Place(card.rectTransform, x, y, w, 88f);
            _healthCard = card;

            Text title = TwinUIFactory.Label(card.rectTransform, "T", TwinTheme.FsCaption,
                TextAnchor.UpperLeft, TwinTheme.TextMuted);
            TwinUIFactory.Place(title.rectTransform, 16f, 12f, 200f, 16f);
            title.text = "楼层健康";

            _healthPct = TwinUIFactory.Label(card.rectTransform, "Pct", TwinTheme.FsCaption,
                TextAnchor.UpperRight, TwinTheme.TextSecondary);
            TwinUIFactory.Place(_healthPct.rectTransform, w - 160f, 12f, 144f, 16f);
            _healthPct.text = "正常 100%";

            float barW = w - 32f;
            for (int i = 0; i < 3; i++)
            {
                Image seg = TwinUIFactory.Block(card.rectTransform, "Seg_" + i,
                    TwinTheme.StatusNormal, false);
                TwinUIFactory.Place(seg.rectTransform, 16f, 34f, barW / 3f, 9f);
                _healthSeg[i] = seg;

                TwinRoundedRect dot = TwinUIFactory.Rounded(card.rectTransform, "Dot_" + i, 3f,
                    TwinTheme.StatusNormal, TwinTheme.StatusNormal, Color.clear, 0f, false);
                TwinUIFactory.Place(dot.rectTransform, 16f + i * 130f, 56f, 6f, 6f);
                _healthDot[i] = dot;

                Text leg = TwinUIFactory.Label(card.rectTransform, "Leg_" + i, TwinTheme.FsMicro,
                    TextAnchor.MiddleLeft, TwinTheme.TextMuted);
                TwinUIFactory.Place(leg.rectTransform, 26f + i * 130f, 50f, 120f, 16f);
                _healthLeg[i] = leg;
            }
        }

        private void BuildTop3(RectTransform parent, float x, float y, float w)
        {
            Text head = TwinUIFactory.Label(parent, "FAbnTitle", TwinTheme.FsCaption,
                TextAnchor.UpperLeft, TwinTheme.TextMuted);
            TwinUIFactory.Place(head.rectTransform, x, y, w, 16f);
            head.text = "异常 Top3";

            Text hint = TwinUIFactory.Label(parent, "FAbnHint", TwinTheme.FsMicro,
                TextAnchor.UpperRight, TwinTheme.TextMuted);
            TwinUIFactory.Place(hint.rectTransform, x, y, w, 16f);
            hint.text = "按严重度排序 · 点击定位";
            hint.raycastTarget = false;

            for (int i = 0; i < 3; i++)
            {
                float ry = y + 24f + i * 48f;

                TwinRoundedRect row = TwinUIFactory.Rounded(parent, "FAbn_" + i, TwinTheme.R8,
                    Color.clear, Color.clear, TwinTheme.Border, TwinTheme.StrokeHair.Width, false);
                TwinUIFactory.Place(row.rectTransform, x, ry, w, 44f);
                _abnRow[i] = row.rectTransform;

                Image bar = TwinUIFactory.Block(row.rectTransform, "Bar", TwinTheme.StatusAlarm, false);
                TwinUIFactory.Place(bar.rectTransform, 0f, 10f, 3f, 24f);
                _abnBar[i] = bar;

                Text id = TwinUIFactory.Label(row.rectTransform, "Id", TwinTheme.FsBody,
                    TextAnchor.MiddleLeft, TwinTheme.TextPrimary);
                TwinUIFactory.Place(id.rectTransform, 14f, 0f, 300f, 44f);
                _abnId[i] = id;

                Text tag = TwinUIFactory.Label(row.rectTransform, "Tag", TwinTheme.FsMicro,
                    TextAnchor.MiddleRight, TwinTheme.StatusAlarm);
                TwinUIFactory.Place(tag.rectTransform, w - 80f, 0f, 64f, 44f);
                _abnTag[i] = tag;
            }
        }

        // ---------------------------------------------------------------- L2 辅栏

        private void BuildSystemAside()
        {
            _asideGroup = NewGroup("SysAside");

            Text title = TwinUIFactory.Label(_asideGroup, "FAsideTitle", TwinTheme.FsCaption,
                TextAnchor.UpperLeft, TwinTheme.TextMuted);
            TwinUIFactory.Place(title.rectTransform, RightInnerX, 92f, ColW, 16f);
            title.text = "筛选";

            string[] filters = { "全部楼层", "1F", "2F", "室外" };
            for (int i = 0; i < filters.Length; i++)
            {
                bool on = i == 0;
                // 【C-31】这里原来是 `raycast: false` —— 下面虽然加了 Button，但
// GraphicRaycaster 只命中 raycastTarget=true 的 Graphic，所以 Button 永远收不到点击。
// 主理人 play 实测「全屏态灰色按钮无法点击」就是这个原因（按钮在，但射线打不到）。
TwinRoundedRect chip = TwinUIFactory.Rounded(_asideGroup, "FChip_" + i, TwinTheme.R8,
                    on ? TwinTheme.AccentDim : TwinTheme.Track,
                    on ? TwinTheme.AccentDim : TwinTheme.Track,
                    on ? TwinTheme.AccentLine : Color.clear, TwinTheme.StrokeHair.Width, true);
                TwinUIFactory.Place(chip.rectTransform, RightInnerX, 112f + i * 30f, ColW, 26f);
                _chipBg[i] = chip;

                Text t = TwinUIFactory.Label(chip.rectTransform, "T", TwinTheme.FsMicro,
                    TextAnchor.MiddleLeft, on ? TwinTheme.Accent : TwinTheme.TextMuted);
                TwinUIFactory.Place(t.rectTransform, 10f, 0f, ColW - 16f, 26f);
                t.text = filters[i];
                t.raycastTarget = false;
                _chipText[i] = t;

                // 真筛选：0=全部 1=1F 2=2F 3=室外（原来是死的灰块，主理人问"1F/2F 为什么点不动"）
                int chipIdx = i;
                Button chipBtn = chip.gameObject.AddComponent<Button>();
                chipBtn.transition = Selectable.Transition.None;
                chipBtn.onClick.AddListener(delegate { OnFilterClicked(chipIdx); });
            }

            Text sumTitle = TwinUIFactory.Label(_asideGroup, "FSumTitle", TwinTheme.FsCaption,
                TextAnchor.UpperLeft, TwinTheme.TextMuted);
            TwinUIFactory.Place(sumTitle.rectTransform, RightInnerX, 260f, ColW, 16f);
            sumTitle.text = "台账小结";

            _sumTotal = AsideRow(282f, "共");
            _sumScope = AsideRow(306f, "受控");
            _sumMissing = AsideRow(330f, "缺失");
            _sumOffline = AsideRow(354f, "离线");

            Text trace = TwinUIFactory.Label(_asideGroup, "FTrace", TwinTheme.FsCaption,
                TextAnchor.UpperLeft, TwinTheme.TextMuted);
            TwinUIFactory.Place(trace.rectTransform, RightInnerX, 430f, ColW, 16f);
            trace.text = "全链路追溯";

            Text traceHint = TwinUIFactory.Label(_asideGroup, "FTraceHint", TwinTheme.FsMicro,
                TextAnchor.UpperLeft, TwinTheme.TextMuted);
            TwinUIFactory.Place(traceHint.rectTransform, RightInnerX, 450f, ColW, 40f);
            traceHint.text = "选中设备后显示\n下发 → 回执 → 执行 → 回读";
            traceHint.lineSpacing = 1.3f;
        }

        /// <summary>
        /// 点设备行 → 选中该设备，并**进入设备详情页**（全屏满宽态）。
        ///
        /// ── 【2026-11-03 P11 改：不再切右栏】────────────────────────────
        /// 原来点行只做 `TwinPicker.Select()`，右栏从「筛选」切到「设备详情」——
        /// 而那个详情只有 4 行纯文字（位置/类型/指标数/更新），
        /// 窄态点行走的是 `TwinDrawer.OpenDeviceDetail` → 一张 440 宽的完整详情页
        /// （量程条 + 阈值带 + 60s 趋势线 + 告警列表）。
        /// 主理人截图指出：「设备详情全屏和窄屏展示效果还不一样啊」——
        /// 一句话点破：同一个功能、两套东西，信息量差一大截。
        ///
        /// ── 现在：两态走**同一张详情页** ────────────────────────────────
        /// 全屏点行 → `OpenDeviceDetail` → 详情页 Push 到页栈 → 全屏层让位（Hide），
        /// 详情页自己按 `TwinDrawer.IsFullScreen == true` 用满宽 968 排版，
        /// 与窄态 440 同一份代码、同一套信息（量程条 / 阈值带 / 趋势线 / 告警），
        /// 只有宽度和趋势线高度不同。
        ///
        /// 同时**保留** `TwinPicker.Select()`：右栏列表的行高亮、以及从详情页返回后
        /// 「刚才看的是哪一台」都靠它。
        ///
        /// ── 为什么不再调 TwinDetailPanel.FocusOn ────────────────────────
        ///   ① 它内部让 FreeCameraController 飞过去，但相机控制权在第三人称手里，
        ///      每帧被覆盖 —— 飞了也看不见（主理人 play 实测"无反应"）；
        ///   ② 它会把窄态那张 470 浮卡叫出来，而详情页已经是整页了，浮卡会叠在上面。
        /// </summary>
        private void OnDeviceRowClicked(int index)
        {
            if (index < 0 || index >= _devBuf.Count) return;

            TwinAnchor anchor = _devBuf[index];
            if (anchor == null) return;

            SchoolTwin.Interaction.TwinPicker.Select(anchor);

            // 立刻刷一次：从详情页返回时，行高亮不会停在上一台
            _nextRefresh = 0f;
            Refresh(true);

            // 进详情页（页栈 Push）。这一步会让全屏层 Hide —— 见 ApplyFullMode。
            TwinDrawer.OpenDeviceDetail(anchor);
        }

        /// <summary>点筛选 chip → 切换楼层过滤，并立即刷新列表。</summary>
        private void OnFilterClicked(int index)
        {
            if (_chipFilter == index) return;
            _chipFilter = index;
            RefreshChipStyles();
            _nextRefresh = 0f;
            Refresh(true);
        }

        private void RefreshChipStyles()
        {
            for (int i = 0; i < _chipBg.Length; i++)
            {
                if (_chipBg[i] == null) continue;
                bool on = i == _chipFilter;
                _chipBg[i].SetFill(on ? TwinTheme.AccentDim : TwinTheme.Track,
                                   on ? TwinTheme.AccentDim : TwinTheme.Track);
                _chipBg[i].SetBorder(on ? TwinTheme.AccentLine : Color.clear, TwinTheme.StrokeHair.Width);
                if (_chipText[i] != null)
                    _chipText[i].color = on ? TwinTheme.Accent : TwinTheme.TextMuted;
            }
        }

        private Text AsideRow(float y, string label)
        {
            Text l = TwinUIFactory.Label(_asideGroup, "AL_" + label, TwinTheme.FsMicro,
                TextAnchor.MiddleLeft, TwinTheme.TextMuted);
            TwinUIFactory.Place(l.rectTransform, RightInnerX, y, 80f, 20f);
            l.text = label;

            Text v = TwinUIFactory.Label(_asideGroup, "AV_" + label, TwinTheme.FsMicro,
                TextAnchor.MiddleRight, TwinTheme.TextSecondary);
            TwinUIFactory.Place(v.rectTransform, RightInnerX + ColW - 60f, y, 60f, 20f);
            v.text = "0";
            return v;
        }

        // ================================================================ 显示与刷新

        /// <summary>
        /// 【P3】楼层筛选复位到「全部」。
        ///
        /// 由 `TwinDrawer.ToggleMaximize` 在两态切换时调用。
        /// 本轮不合并两套视图的状态，所以靠"切换即复位"保证用户不会看到
        /// "窄态筛了 1F、切全屏变全部、切回来又只剩 1F" 这种前后不一致。
        /// </summary>
        public void ResetFloorFilter()
        {
            if (_chipFilter == 0) return;
            _chipFilter = 0;
            RefreshChipStyles();
            _nextRefresh = 0f;
        }

        public void Show(Mode mode)
        {
            _mode = mode;
            gameObject.SetActive(mode != Mode.None);
            if (mode == Mode.None) return;

            // 淡入：全屏是从「单侧窄栏」跳到「整屏三栏」，形态跨度大，
            // 直接出现会有一下"闪"。0.15s 淡入把这次跳变压下去（宽度没法缓动，那是两套骨架）。
            if (_group != null) _group.alpha = 0f;

            // 【关键】按模式整组切换。**不能只切几个元素** ——
            // 第一版只切了 KPI 与 L2 的 sys 标题，结果 L1 右栏（告警/类型）与
            // L2 主栏（健康条/Top3）同时显示，三套字叠在一起（主理人 play 实测）。
            // 现在每栏都有独立组容器，切组即切整栏。
            bool home = mode == Mode.Home;

            SetGroupActive(_homeGroup, home);       // L1 左栏：KPI / 入口 / 动态
            SetGroupActive(_railGroup, !home);      // L2 左轨：5 Tab

            // ⚠️ 右栏三组（辅栏 / 告警条 / 设备详情）**不在这里切**，
            // 统一交给下面的 `ApplySystemAside()`（System 态）与
            // `RefreshHome()`（Home 态）。原来这两行在这里各写一遍：
            //     SetGroupActive(_asideGroup,  !home && _tab != 4);
            //     SetGroupActive(_alarmGroup,  false);
            //     SetGroupActive(_detailGroup, false);
            // 结果是"同一个判定有两个出口"—— 上面那行管统计页让位，
            // 下面 P10 那段又管详情位，两处打架，点行时详情组被这边关掉。

            // 主栏五组互斥：数据监测 / 设备管理 / 电力管理 / 告警中心 / 数据统计
            SetGroupActive(_monitorGroup, !home && _tab == 0);
            SetGroupActive(_deviceGroup, !home && _tab == 1);
            SetGroupActive(_powerGroup, !home && _tab == 2);
            SetGroupActive(_alarmCenterGroup, !home && _tab == 3);
            SetGroupActive(_statsGroup, !home && _tab == 4);
            SetGroupActive(_emptyGroup, false);

            // 【P5-1 P0-2】从 Home（或窄态回来）直接进到统计页时，曲线同样要重建。
            //
            // `SelectTab` 只覆盖"页内切 Tab"这一条路径；从 L1 进 L2 时
            // `_tab` 保持上次的值（默认 0，但也可能停在上次的 4），
            // 走的入口是 `Show(Mode.System)` —— 不在这里补一句就会漏。
            if (!home && _tab == 4) ResetStatsSpark();

            // ══════════════════════════════════════════════════════════════
            // 【2026-11-03 P10 修·设备管理页点行无反应】
            //
            // ── 症状（主理人 play 实测）─────────────────────────────────
            // 全屏 → 设备管理 → 点任意一行，**什么都看不出来**：
            // 行不点亮、右栏不变、没有任何反馈。主理人判为"死按钮"。
            //
            // ── 两个根因（不是一个）────────────────────────────────────
            // ① 右栏形态判定**有两个出口**：这里无条件 `_detailGroup=false`，
            //    而 `RefreshSystem` 根本不碰这三组 → System 态下详情组永远关着。
            // ② 即使把 ① 修好，判定读的是 `TwinDetailPanel.SelectedAnchor`，
            //    它比 `TwinPicker.Selected` **晚一帧**同步；点行是同帧刷新，
            //    读到的是旧值 → 右栏仍不动。详见 `ApplySystemAside` 的注释。
            //
            // ── 现在怎么判 ──────────────────────────────────────────────
            // 抽成 `ApplySystemAside()` 统一裁决，`Show` 与 `RefreshSystem` 共用；
            // 里面只读 `TwinPicker.Selected`（和行高亮同源同帧）。
            //
            // 【这里这行其实是冗余的】下面 `Refresh(true)` → `RefreshSystem()`
            // 开头就会调同一个方法。之所以留着，是当"进/切页时右栏必须当场是对的"
            // 这条不变量 —— 万一以后有人在 `Refresh` 里加了早退（比如节流提前 return），
            // 右栏不会残留上一页的形态。幂等调用，代价为 0。
            // ══════════════════════════════════════════════════════════════
            if (!home) ApplySystemAside();
            else
            {
                // Home 态：辅栏是 L2 专属（筛选 / 台账小结 / 全链路追溯），必须关掉。
                // 告警条 / 详情组两组交给紧随其后的 `Refresh(true)` → `RefreshHome()` 裁决，
                // 这里不碰 —— 碰了就又变成"两个出口"。
                SetGroupActive(_asideGroup, false);
            }

            Refresh(true);
        }

        private static void SetGroupActive(Component c, bool on)
        {
            if (c != null && c.gameObject.activeSelf != on) c.gameObject.SetActive(on);
        }

        public void Hide()
        {
            _mode = Mode.None;
            gameObject.SetActive(false);
        }

        /// <summary>淡入推进。由 TwinDrawer.Update 驱动 —— 本层不自己挂 Update，省一个每帧回调。</summary>
        public void TickFade()
        {
            if (_group == null || _group.alpha >= 1f) return;
            _group.alpha = Mathf.MoveTowards(_group.alpha, 1f, Time.unscaledDeltaTime / 0.15f);
        }

        /// <summary>
        /// 每帧驱动统计页的功率曲线采样（5Hz，内部自带节流）。
        ///
        /// 为什么不并进 `Refresh`：它是 0.5s 节流，用它采样 60 秒只有 120 点，
        /// 曲线会一段一段地"跳"。5Hz 和 `TwinMetricHistory.SampleInterval` 一致。
        /// 只在统计页激活时跑，切走立刻停。
        /// </summary>
        private void Update()
        {
            UpdateStatsSpark();

            // 【P5】每帧推进曲线时间轴。
            //
            // `UpdateStatsSpark` 是 5Hz 采样，只负责"加新点"；让整条线向左滚是
            // `TwinSparkline.Tick(time)` 的职责。不调它的话，两次采样之间线是冻住的，
            // 看起来像"卡了一下又跳一格"。
            //
            // 之前的 bug 就在这里：`Tick` 从来没被调用过，`_now` 恒为 0，
            // 时间跨度被夹到 1 秒，所有点压到同一列 —— 曲线画不出来。
            if (_statsSpark != null && _mode != Mode.None && _mode != Mode.Home && _tab == 4)
                _statsSpark.Tick(Time.unscaledTime);
        }

        public void Refresh(bool force)
        {
            if (_mode == Mode.None) return;
            if (!force && Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + 0.5f;

            if (_mode == Mode.Home) RefreshHome();
            else RefreshSystem();
        }

        /// <summary>
        /// System 模式下右栏该显示什么 —— **唯一出口**。
        ///
        /// 【为什么要抽出来】`Show(Mode.System)` 与 `RefreshSystem()` 两条路径都要决定
        /// 右栏形态：前者是"进/切页"，后者是"点击行后的 0.5s 刷新"。
        /// 两处各写一遍必然分叉 —— 2026-11-03 的"点行无反应"就是分叉的后果：
        /// `Show` 里硬关详情组，而 `Refresh` 走 `RefreshSystem` 时**根本不碰**这两组，
        /// 于是选中的变化永远表达不出来。
        ///
        /// ── 右栏两态（System 下）──────────────────────────────────────
        /// | 页 | 右栏内容 | 判据 |
        /// |---|---|---|
        /// | 数据统计 `_tab==4` | 让位给画布（满宽） | 恒关 |
        /// | 其余四页 | 辅栏（筛选 / 小结 / 追溯） | 恒关详情组 |
        ///
        /// 告警条（`_alarmGroup`）只在 Home 右栏出现，System 下恒关。
        ///
        /// ── 【2026-11-03 P11 又砍一刀：System 里不再有"右栏设备详情"】────────
        /// P10 修好"点行有反应"之后，设备管理页的右栏会在选中时切到
        /// `_detailGroup` —— 但那只是 4 行纯文字（位置/类型/指标数/数据更新，宽 168），
        /// 跟窄态点行给了张完整详情页（量程条 + 阈值带 + 趋势线 + 告警）差着一大截。
        /// 主理人截图原话：「设备详情全屏和窄屏展示效果还不一样啊」。
        ///
        /// P11 的解法是**让全屏点行也进那一页整页详情**（`TwinDrawer.OpenDeviceDetail`），
        /// 于是 `_detailGroup` 在 System 下**再没有出场机会** —— 它描述的每一个字段，
        /// 详情页里都有，而且更全。留着它只会在"从详情页返回"时冒出来，
        /// 变成一个比详情页简陋十倍的第二形态。
        ///
        /// ⚠️ 注意 `_detailGroup` **没有删**：L1（Home）右栏还在用它 ——
        /// 3D 里点中一台设备时右栏显示摘要，那是另一条链路，与页栈无关。
        /// 这里只是把 System 态的入口关掉。
        ///
        /// ── 【2026-11-03 P10 第二刀·选中来源必须读 TwinPicker】─────────────
        /// 第一版这里读的是 `TwinDetailPanel.Instance.SelectedAnchor`，**这是错的**：
        /// 那个属性背后是 `_selected`，而 `_selected` 是在 `TwinDetailPanel.Update()`
        /// 里从 `TwinPicker.Selected` 抄过来的 —— **晚一帧**。
        ///
        /// 而点行的调用链是**同帧同步**的：
        ///     Button.onClick → OnDeviceRowClicked → TwinPicker.Select() → Refresh(true)
        /// 于是 `Refresh(true)` 里读到的还是点击前的旧值（null），右栏原地不动；
        /// 真正切换要等 `TwinDrawer.Update` 的 0.5s 节流刷新 —— 用户看到的就是
        /// 「点了没反应」（0.5s 后才悄悄变，且行高亮 Δ18 肉眼看不出）。
        ///
        /// ⚠️ 这个坑本文件**已经踩过一次**：`RefreshDevicePage` 的 C-31 注释写的
        /// 就是「选中态直接读 TwinPicker.Selected，不要读 TwinDetailPanel.SelectedAnchor」。
        /// 同一个面板两处判定，一处改了一处没改 —— 所以这里统一成同一口径，
        /// 两个方法以后要么一起改，要么都用 `TwinPicker.Selected`。
        ///
        /// 【P11 后这里不再读选中，但口径仍然保留】行高亮 (`RefreshDevicePage`)
        /// 与 L1 的 `RefreshHome` 都还读 `TwinPicker.Selected`，
        /// 三处必须继续同源 —— 只是右栏形态不再参与选中判定。
        /// </summary>
        private void ApplySystemAside()
        {
            // 统计页满宽 → 右栏整块让位；其余页常驻辅栏。
            //
            // 【为什么不再看 `sel`】见上：System 里详情走整页，右栏没有第三态了。
            // 少一个判定就少一处"两态不同步"——这正是 P10 那次事故的成因。
            bool statsPage = _tab == 4;

            SetGroupActive(_alarmGroup, false);
            SetGroupActive(_detailGroup, false);
            SetGroupActive(_asideGroup, !statsPage);
        }

        /// <summary>
        /// Esc 在 L2 里的一层：**先退"设备详情"这一块，再退页 / 收抽屉**。
        ///
        /// 【为什么必须有】详情块上挂着一行「Esc 返回」（`BuildDetailRail` 里的 `DBack`），
        /// 没有这个入口那句话就是假的 —— 按 Esc 会直接把整个抽屉收掉，
        /// 比"返回辅栏"退得还狠，用户下次还得重新开抽屉、重新点设备管理。
        ///
        /// 由 `TwinDrawer.OnEscape` 在退页栈之前调用，返回 true = 已消费。
        ///
        /// ── 【P11 判据改了：看"那句话在不在屏幕上"，而不是看模式】────────
        /// 原来写的是 `if (_mode != Mode.System) return false;` —— 它假设
        /// "System 态一定有详情块"，而 P11 之后 System 态的详情块**永远不显示了**
        /// （设备详情改成整页，见 `ApplySystemAside`）。
        ///
        /// 继续按模式判的后果：全屏设备管理里点了行、按 Esc，
        /// 内部把选中清掉了、返回 true，但**屏幕上什么都不会变**
        /// —— 正是主理人最反感的那种"按了没反应"。
        ///
        /// 所以判据换成"详情块当前是不是真的可见"。这样它**自我维护**：
        /// 哪天右栏详情被删掉或被别的形态取代，这里自动失效，不需要再改一次。
        /// </summary>
        public bool HandleEscape()
        {
            // 屏幕上没有「Esc 返回」这句话，就不该把 Esc 吃掉
            if (_detailGroup == null || !_detailGroup.gameObject.activeSelf) return false;
            if (SchoolTwin.Interaction.TwinPicker.Selected == null) return false;

            // 清选中 → 右栏回辅栏。清完必须立刻刷，否则要等 0.5s 节流。
            SchoolTwin.Interaction.TwinPicker.Select(null);
            _nextRefresh = 0f;
            Refresh(true);
            return true;
        }

        private void RefreshHome()
        {
            // 【规格 §5.1】右栏两态：有选中设备 → 设备详情；无选中 → 告警列表 + 设备类型。
            // 左栏（KPI / 入口 / 动态）不参与切换，始终显示。
            //
            // 【P10】选中来源统一读 `TwinPicker.Selected`，**不读
            // `TwinDetailPanel.SelectedAnchor`**（它晚一帧）——
            // 和 `ApplySystemAside`、`RefreshDevicePage` 三处同源。
            TwinAnchor sel = SchoolTwin.Interaction.TwinPicker.Selected;
            bool showDetail = sel != null;

            if (_alarmGroup != null && _alarmGroup.gameObject.activeSelf == showDetail)
                _alarmGroup.gameObject.SetActive(!showDetail);
            if (_detailGroup != null && _detailGroup.gameObject.activeSelf != showDetail)
                _detailGroup.gameObject.SetActive(showDetail);

            int total = TwinDeviceStats.ActiveDeviceCount;
            int online = TwinDeviceStats.OnlineDeviceCount;
            int alarm = TwinDeviceStats.AlarmDeviceCount;

            if (_kpiDevice != null) _kpiDevice.SetValue(total.ToString());
            if (_kpiOnline != null) _kpiOnline.SetValue(online.ToString());
            if (_kpiAlarm != null)
            {
                _kpiAlarm.SetValue(alarm.ToString());
                _kpiAlarm.SetState(alarm > 0 ? TwinMetricCard.CardState.Alarm
                                             : TwinMetricCard.CardState.Normal);
            }

            if (showDetail) RefreshDetail(sel);
            else
            {
                RefreshAlarmRail();
                RefreshTypes();
            }

            RefreshFeed();
        }

        /// <summary>右栏设备详情刷新。四项都是已有字段，不额外查数据源。</summary>
        private void RefreshDetail(TwinAnchor a)
        {
            TwinStatus s = a.StatusOfWorst();
            Color c = TwinStatusPalette.Of(s);

            SetText(_dCode, TwinCode.Full(a.TwinId));
            SetText(_dName, a.DisplayName);

            if (_dChip != null) _dChip.SetFill(c, c);
            SetText(_dChipText, TwinStatusPalette.Label(s));

            SetText(_dVal[0], TwinCode.Room(a.Zone));
            SetText(_dVal[1], TwinCode.TypeName(a.Category));
            SetText(_dVal[2], a.MetricCount + " 项");
            SetText(_dVal[3], a.HasData
                ? Mathf.RoundToInt(a.SecondsSinceLastSample) + " 秒前"
                : "无数据");
        }

        /// <summary>右栏告警：取仍在持续的，最多 3 条。</summary>
        private void RefreshAlarmRail()
        {
            _alarmBuf.Clear();
            TwinAlarmHistory h = TwinAlarmHistory.Instance;
            if (h != null) h.GetRecent(_alarmBuf, 64);

            int n = 0;
            float now = Time.realtimeSinceStartup;

            // 先数"进行中"的总数，才能算"还有几条"。不能只数前 3 条 —— 那正是 P1-2 的问题。
            int ongoing = 0;
            for (int i = 0; i < _alarmBuf.Count; i++)
                if (_alarmBuf[i].Ongoing) ongoing++;

            for (int i = 0; i < _alarmBuf.Count && n < 3; i++)
            {
                TwinAlarmHistory.AlarmRecord r = _alarmBuf[i];
                if (!r.Ongoing) continue;

                Color c = TwinStatusPalette.Of(r.PeakStatus);
                if (_alarmBar[n] != null) _alarmBar[n].color = c;
                SetText(_alarmId[n], TwinUIFactory.FitTextKeepTail(TwinCode.WithType(r.TwinId),
                                                                   TwinTheme.FsCaption, _alarmIdWidth, 3));
                SetText(_alarmDesc[n], TwinStatusPalette.Label(r.PeakStatus) + " · 已持续 " +
                                        TwinAlarmHistory.FormatDuration(r.Duration(now)));
                if (_alarmTag[n] != null)
                {
                    SetText(_alarmTag[n], TwinStatusPalette.Label(r.PeakStatus));
                    _alarmTag[n].color = c;
                }

                if (_alarmCard[n] != null && !_alarmCard[n].gameObject.activeSelf)
                    _alarmCard[n].gameObject.SetActive(true);
                n++;
            }

            for (int i = n; i < 3; i++)
                if (_alarmCard[i] != null && _alarmCard[i].gameObject.activeSelf)
                    _alarmCard[i].gameObject.SetActive(false);

            if (_alarmTitle != null) _alarmTitle.text = "告警  " + TwinDeviceStats.AlarmDeviceCount;

            if (_moreAlarm != null)
                _moreAlarm.text = ongoing > 3 ? "还有 " + (ongoing - 3) + " 条 · 点开查看" : "";
        }

        /// <summary>
        /// 左栏动态：**最新 3 条 + "还有 N 条"**（负责人裁决）。
        /// 不能只显示 3 条不给总数 —— 那会让人以为现场只有 3 件事。
        /// </summary>
        private void RefreshFeed()
        {
            List<FeedLine> lines = CollectFeed();

            int shown = Mathf.Min(3, lines.Count);
            for (int i = 0; i < 3; i++)
            {
                bool on = i < shown;
                if (_feedMain[i] != null && _feedMain[i].gameObject.activeSelf != on)
                {
                    _feedMain[i].gameObject.SetActive(on);
                    _feedSub[i].gameObject.SetActive(on);
                    _feedTime[i].gameObject.SetActive(on);
                    _feedDot[i].gameObject.SetActive(on);
                }
                if (!on) continue;

                FeedLine f = lines[i];
                SetText(_feedMain[i], TwinUIFactory.Truncate(f.Main, TwinTheme.FsCaption, ColW - 56f));
                SetText(_feedSub[i], TwinUIFactory.Truncate(f.Sub, TwinTheme.FsMicro, ColW - 56f));
                SetText(_feedTime[i], f.Time);

                Color c = TwinStatusPalette.Of(f.Status);
                _feedDot[i].SetFill(c, c);
            }

            if (_feedMore != null)
                _feedMore.text = lines.Count > 3 ? "还有 " + (lines.Count - 3) + " 条" : "";
        }

        private struct FeedLine
        {
            public string Main;
            public string Sub;
            public string Time;
            public TwinStatus Status;
            public float Age;
        }

        private readonly List<FeedLine> _feedBuf = new List<FeedLine>(64);

        private List<FeedLine> CollectFeed()
        {
            _feedBuf.Clear();

            TwinCommandCenter cc = TwinCommandCenter.Instance;
            if (cc != null && cc.Log != null)
            {
                double now = TwinClock.NowUtcSeconds();
                IReadOnlyList<TwinCommandLog.Entry> recent = cc.Log.Recent(60);
                for (int i = 0; i < recent.Count; i++)
                {
                    TwinCommandLog.Entry e = recent[i];
                    FeedLine f = new FeedLine();
                    f.Age = (float)Math.Max(0d, now - e.AtUtc);
                    f.Status = e.Ok ? TwinStatus.Normal : TwinStatus.Alarm;
                    f.Main = e.Action;
                    f.Sub = TwinCode.Short(e.TwinId);
                    f.Time = e.Time;
                    _feedBuf.Add(f);
                }
            }

            TwinAlarmHistory ah = TwinAlarmHistory.Instance;
            if (ah != null)
            {
                _alarmBuf.Clear();
                ah.GetRecent(_alarmBuf, 30);
                float now = Time.realtimeSinceStartup;
                for (int i = 0; i < _alarmBuf.Count; i++)
                {
                    TwinAlarmHistory.AlarmRecord r = _alarmBuf[i];
                    FeedLine f = new FeedLine();
                    f.Age = Mathf.Max(0f, now - r.StartTime);
                    f.Status = r.PeakStatus;
                    f.Main = string.IsNullOrEmpty(r.DisplayName) ? "告警" : r.DisplayName;
                    f.Sub = TwinCode.Short(r.TwinId);
                    f.Time = r.Clock;
                    _feedBuf.Add(f);
                }
            }

            _feedBuf.Sort(CompareFeed);
            return _feedBuf;
        }

        private static int CompareFeed(FeedLine a, FeedLine b)
        {
            return a.Age.CompareTo(b.Age);
        }

        /// <summary>设备类型分布：按 Category 聚合，取前 4 类。</summary>
        private void RefreshTypes()
        {
            Dictionary<string, int> count = new Dictionary<string, int>(16, StringComparer.Ordinal);
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
                    count.TryGetValue(type, out n);
                    count[type] = n + 1;
                }
            }

            List<KeyValuePair<string, int>> list = new List<KeyValuePair<string, int>>(count);
            list.Sort(delegate (KeyValuePair<string, int> x, KeyValuePair<string, int> y)
            {
                return y.Value.CompareTo(x.Value);
            });

            int max = list.Count > 0 ? Mathf.Max(1, list[0].Value) : 1;

            for (int i = 0; i < 4; i++)
            {
                bool on = i < list.Count;
                if (_typeName[i] != null && _typeName[i].gameObject.activeSelf != on)
                {
                    _typeName[i].gameObject.SetActive(on);
                    _typeCount[i].gameObject.SetActive(on);
                    _typeBar[i].gameObject.SetActive(on);
                }
                if (!on) continue;

                SetText(_typeName[i], list[i].Key);
                SetText(_typeCount[i], list[i].Value.ToString());

                RectTransform rt = _typeBar[i].rectTransform;
                rt.sizeDelta = new Vector2(Mathf.Max(2f, 78f * list[i].Value / max), 4f);
            }
        }

        private void RefreshSystem()
        {
            // 右栏形态先裁决（详情 / 辅栏 / 让位），再刷主栏五页。
            //
            // 【顺序为什么是"先右后主"】`ApplySystemAside` 会让辅栏与详情组
            // 互相让位，而这两组里可能有本帧要写的文本。先摆好可见性，
            // 再刷内容，就不会出现"写进了隐藏的组"（那样下次显示时是旧值）。
            ApplySystemAside();

            // 五页各自刷新，互不牵连。空页（统计数据不足）由各页自己处理空状态。
            switch (_tab)
            {
                case 0: RefreshMonitorPage(); break;
                case 1: RefreshDevicePage(); break;
                case 2: RefreshPowerPage(); break;
                case 3: RefreshAlarmCenterPage(); break;
                case 4: RefreshStatsPage(); break;
            }
        }

        private void RefreshMonitorPage()
        {
            int total = TwinDeviceStats.ActiveDeviceCount;
            int online = TwinDeviceStats.OnlineDeviceCount;
            int alarm = TwinDeviceStats.AlarmDeviceCount;
            int offline = Mathf.Max(0, total - online);

            if (_sysOnline != null) _sysOnline.SetValue(total > 0 ? online.ToString() : "—");
            if (_sysAlarm != null)
            {
                _sysAlarm.SetValue(total > 0 ? alarm.ToString() : "—");
                _sysAlarm.SetState(alarm > 0 ? TwinMetricCard.CardState.Alarm
                                             : TwinMetricCard.CardState.Normal);
            }

            // 【C-28】总功率 / 今日用电：原来是恒 NoData（「—」+「数据源未接入」），
            // 而同一个面板的统计页却在给 ≈ 4.6 kW —— 一处有数一处未接入显得自相矛盾。
            // 现在与统计页**同源同口径**（TwinPowerEstimate），数值一律标 ≈。
            float watt = TwinPowerEstimate.TotalWatt();

            if (_sysPower != null)
            {
                _sysPower.SetValue(total > 0 ? "≈ " + (watt / 1000f).ToString("0.0") : "—");
                _sysPower.SetState(total > 0 ? TwinMetricCard.CardState.Estimate
                                             : TwinMetricCard.CardState.NoData);
            }

            if (_sysEnergy != null)
            {
                _sysEnergy.SetValue(total > 0 ? "≈ " + TwinPowerEstimate.TodayKwh().ToString("0.00") : "—");
                _sysEnergy.SetState(total > 0 ? TwinMetricCard.CardState.Estimate
                                              : TwinMetricCard.CardState.NoData);
            }

            // 健康条：三段真实计数，颜色按段本色；百分比文字按最严重级别取色
            int normal = Mathf.Max(0, total - alarm - offline);
            float sum = Mathf.Max(1, total);
            float w = MidW - Pad * 2f - 32f;

            float rNormal = normal / sum;
            float rAlarm = alarm / sum;
            float rOff = offline / sum;

            SetSeg(0, 16f, w * rNormal, TwinTheme.StatusNormal);
            SetSeg(1, 16f + w * rNormal, w * rAlarm, TwinTheme.StatusAlarm);
            SetSeg(2, 16f + w * (rNormal + rAlarm), w * rOff, TwinTheme.StatusOffline);

            SetText(_healthLeg[0], "正常 " + normal);
            SetText(_healthLeg[1], "告警 " + alarm);
            SetText(_healthLeg[2], "离线 " + offline);

            // 图例点必须跟段色一一对应（见微 P1-2 验收）。
            // 原来只在创建时给了 StatusNormal 初始色，刷新时没更新 —— 三个点永远是绿的。
            Color[] segColors = { TwinTheme.StatusNormal, TwinTheme.StatusAlarm, TwinTheme.StatusOffline };
            for (int i = 0; i < 3; i++)
                if (_healthDot[i] != null) _healthDot[i].SetFill(segColors[i], segColors[i]);

            Color worst = TwinStatusPalette.HealthColor(normal, 0, alarm, offline);
            if (_healthPct != null)
            {
                _healthPct.text = "正常 " + Mathf.RoundToInt(rNormal * 100f) + "%";
                _healthPct.color = worst;
            }

            // 有告警/离线时整卡外框转该级别色（裁决："存在告警时外框或标题加红"）。
            // 全正常回原始 Border —— 不能一直亮着，那会让"有红框"变成常态、失去提示意义。
            if (_healthCard != null)
            {
                bool hasIssue = alarm > 0 || offline > 0;
                _healthCard.SetBorder(
                    hasIssue ? TwinTheme.WithAlpha(worst, 0.55f) : TwinTheme.Border, 1f);
            }

            RefreshTop3();

            SetText(_sumTotal, total.ToString());
            SetText(_sumScope, TwinDeviceStats.ControlScopeCount.ToString());
            SetText(_sumMissing, "0");
            SetText(_sumOffline, offline.ToString());
        }

        /// <summary>设备管理页刷新：七行台账 + 辅栏小结。</summary>
        private void RefreshDevicePage()
        {
            int total = TwinDeviceStats.ActiveDeviceCount;
            int scope = TwinDeviceStats.ControlScopeCount;
            int offline = Mathf.Max(0, total - TwinDeviceStats.OnlineDeviceCount);

            if (_devCount != null)
                _devCount.text = "共 " + total + " " + TwinCode.UnitDevice +
                                 " · 受控 " + scope + " " + TwinCode.UnitDevice + " · 缺失 0";

            _devBuf.Clear();
            IReadOnlyList<TwinAnchor> all = TwinDeviceStats.AllDevices;
            if (all != null)
            {
                for (int i = 0; i < all.Count && _devBuf.Count < DevRows; i++)
                {
                    TwinAnchor a = all[i];
                    if (!TwinDeviceStats.IsActiveDevice(a)) continue;

                    // 楼层筛选：0=全部 / 1=1F / 2=2F / 3=室外（Floor 0）
                    if (_chipFilter > 0)
                    {
                        TwinRoomInfo info = TwinSiteLayout.Find(a.Zone);
                        int floor = info != null ? info.Floor : 0;
                        int want = _chipFilter == 3 ? 0 : _chipFilter;
                        if (floor != want) continue;
                    }

                    _devBuf.Add(a);
                }
            }

            // 【C-31】选中态**直接读 TwinPicker.Selected**，不要读 TwinDetailPanel.SelectedAnchor。
            // 后者是在它自己的 Update 里同步的 —— 点行的这一帧还没同步过来，
            // 于是"立即 Refresh(true)"画出来的高亮是上一台设备，
            // 用户看到的就是"点了没反应"（要等 0.5s 的下一次刷新才对）。
            TwinAnchor selected = SchoolTwin.Interaction.TwinPicker.Selected;

            for (int i = 0; i < DevRows; i++)
            {
                bool on = i < _devBuf.Count;
                if (_devRow[i] != null && _devRow[i].gameObject.activeSelf != on)
                    _devRow[i].gameObject.SetActive(on);
                if (!on) continue;

                TwinAnchor a = _devBuf[i];
                TwinStatus s = a.StatusOfWorst();
                Color c = TwinStatusPalette.Of(s);

                // 选中行高亮：点一行后右栏切详情，列表这边要让人看出选的是哪条
                //
                // 【2026-11-03 P10 补强】原来只有一层 `AccentDim` 填充（Accent @0.035，
                // 亮度差 Δ18）—— 在深色行底上几乎是看不见的，主理人截图里那行
                // 看不出任何变化，等于"高亮没做"。现在加到三层，逐层递进：
                //   ① 底：AccentDim（Δ18，给"面"的轻重）
                //   ② 边：StrokeAccent（1px Accent@0.55）—— 行边界从"无"变成"有"
                //   ③ 字：编号转 TextPrimary、右侧「详情 ›」转 Accent
                // ②③ 是这套 UI 里既有的选中语言（Tab / 筛选 chip 都这么写），
                // 不引入新 token，也不靠调 alpha 硬凑 —— 8bit 表达不出来。
                bool sel = selected != null && ReferenceEquals(selected, a);
                if (_devRowBg[i] != null)
                {
                    _devRowBg[i].SetFill(sel ? TwinTheme.AccentDim : Color.clear,
                                         sel ? TwinTheme.AccentDim : Color.clear);
                    _devRowBg[i].SetBorder(sel ? TwinTheme.AccentLine : TwinTheme.Border,
                                           TwinTheme.StrokeHair.Width);
                }

                if (_devBar[i] != null) _devBar[i].color = c;
                SetText(_devId[i], TwinUIFactory.Truncate(TwinCode.Short(a.TwinId), TwinTheme.FsCaption, 140f));
                SetText(_devName[i], TwinUIFactory.Truncate(a.DisplayName, TwinTheme.FsCaption, 140f));
                SetText(_devPos[i], TwinCode.Room(a.Zone));
                SetText(_devType[i], TwinCode.TypeName(a.Category));
                SetText(_devTag[i], TwinStatusPalette.Label(s));
                if (_devTag[i] != null) _devTag[i].color = c;
                SetText(_devSwitch[i], "详情 ›");
                // 未选中行把「详情 ›」压成次级色：满屏 11 行都是主色链接，视觉上像
                // "一排按钮"，反而看不出哪行是当前选中的。选中才给 Accent。
                if (_devSwitch[i] != null)
                    _devSwitch[i].color = sel ? TwinTheme.Accent : TwinTheme.TextMuted;
            }

            // 【C-31】Content 高度 = 实际行数 × 行距。ScrollRect 靠它算可滚范围，
            // 不设的话永远停在 0，滚轮滚不动（和窄态电力树同一个道理）。
            if (_devContent != null)
            {
                float contentH = _devBuf.Count * DevRowH;
                if (!Mathf.Approximately(_devContent.sizeDelta.y, contentH))
                    _devContent.sizeDelta = new Vector2(0f, contentH);
            }

            SetText(_sumTotal, total.ToString());
            SetText(_sumScope, scope.ToString());
            SetText(_sumMissing, "0");
            SetText(_sumOffline, offline.ToString());
        }

        private void SetSeg(int i, float x, float w, Color c)
        {
            if (_healthSeg[i] == null) return;
            _healthSeg[i].color = c;
            _healthSeg[i].rectTransform.sizeDelta = new Vector2(Mathf.Max(0f, w), 9f);
            _healthSeg[i].rectTransform.anchoredPosition =
                new Vector2(x, _healthSeg[i].rectTransform.anchoredPosition.y);
        }

        private void RefreshTop3()
        {
            _alarmBuf.Clear();
            TwinAlarmHistory h = TwinAlarmHistory.Instance;
            if (h != null) h.GetRecent(_alarmBuf, 64);

            int n = 0;
            for (int i = 0; i < _alarmBuf.Count && n < 3; i++)
            {
                TwinAlarmHistory.AlarmRecord r = _alarmBuf[i];
                if (!r.Ongoing) continue;

                Color c = TwinStatusPalette.Of(r.PeakStatus);
                if (_abnBar[n] != null) _abnBar[n].color = c;
                SetText(_abnId[n], TwinCode.WithType(r.TwinId));
                if (_abnTag[n] != null)
                {
                    SetText(_abnTag[n], "进行中");
                    _abnTag[n].color = c;
                }

                if (_abnRow[n] != null && !_abnRow[n].gameObject.activeSelf)
                    _abnRow[n].gameObject.SetActive(true);
                n++;
            }

            for (int i = n; i < 3; i++)
                if (_abnRow[i] != null && _abnRow[i].gameObject.activeSelf)
                    _abnRow[i].gameObject.SetActive(false);
        }

        // ---------------------------------------------------------------- 工具

        private static void SetText(Text t, string s)
        {
            if (t != null && !string.Equals(t.text, s, StringComparison.Ordinal)) t.text = s;
        }

        /// <summary>
        /// KPI 一格：数值大字 + 单位小字，单位位置按数值的实际宽度跟随。
        ///
        /// 【为什么不能写死 x】"133"（3 位）和 "2.8"（3 字符但窄）宽度差很多，
        /// 写死一个偏移量必然在一半情况下错位：要么压在数字上，要么飘出老远。
        /// `Text.preferredWidth` 是 UGUI 按当前字号/字体算出的真实排版宽度 ——
        /// 用它算 x 才和肉眼看到的边界一致。
        ///
        /// 【为什么用 `LayoutUtility` 而不是自己字号估宽】估宽要自己维护一张
        /// 字宽表，字号一改就全错。`preferredWidth` 由 TextGenerator 给出，
        /// 换成别的字库也自动对。
        /// </summary>
        private void SetKpi(int i, string value, string unit)
        {
            Text v = _kpiValue[i];
            if (v == null) return;

            SetText(v, value);

            Text u = _kpiUnit[i];
            if (u == null) return;

            // 单位空串时直接隐藏 —— 否则会留下一格"零宽但仍在排版"的空 Text
            bool hasUnit = !string.IsNullOrEmpty(unit);
            if (u.gameObject.activeSelf != hasUnit) u.gameObject.SetActive(hasUnit);
            if (!hasUnit) return;

            SetText(u, unit);

            // 数值宽度 + 4px 间隙，单位左上角贴到那里。
            // `preferredWidth` 在 text 设完之后才是新的，所以顺序不能反。
            float w = Mathf.Ceil(v.preferredWidth);
            float x = 12f + w + 4f;

            // 兜底：万一单位被挤出卡片右边界（极长的数值 + 单位），
            // 至少保证它还在卡内，不压到下一张卡上
            float rightLimit = SKpiW - 12f - Mathf.Ceil(u.preferredWidth);
            x = Mathf.Min(x, rightLimit);

            u.rectTransform.anchoredPosition = new Vector2(x, u.rectTransform.anchoredPosition.y);
        }
    }
}

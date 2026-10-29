using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using SchoolTwin.Command;   // TwinCommandCenter / TwinClock
using SchoolTwin.Core;      // TwinStatus 在 Core，不在 Twin（踩过：CS0246/CS0103）
using SchoolTwin.Twin;

namespace SchoolTwin.UI
{
    /// <summary>
    /// 【P29-3】L1 总控面板：3 张 KPI + 功能入口 + 实时动态 + 工具行（工具行在抽屉里）。
    ///
    /// ── 这一层的职责（§4.1 分区 B/C/D）──────────────────────────────
    /// ```
    ///  y=104  [设备总数 153][在线 151][告警 2]      分区 B 概览 88 高
    ///  y=214  功能入口 ─────────────────────────
    ///  y=232  [校园管理系统][天气 预留]              分区 C 主内容 104 高
    ///  y=372  实时动态            近 1 小时 N 条
    ///  y=392  ● 命令/告警 4 行 × 52                 分区 D 明细流
    /// ```
    ///
    /// ── 三条设计规矩（写在这里，改的时候别再犯）──────────────────────
    /// 1. **数字不许重复**：在线/告警只在 KPI 卡里出现一次。上一版头部三颗胶囊 + 三张概览卡
    ///    说的是同一组数，首屏一半面积在自我复读。
    /// 2. **一个面板一个发光焦点**：有未确认告警 → 告警卡呼吸、主入口只留描边；
    ///    没告警 → 主入口发光、告警卡中性。两者互斥，见 `ApplyFocus()`。
    /// 3. **数值永不染色**，除告警卡（`TwinMetricCard` 里已锁死）。
    ///
    /// ── 坐标怎么来的 ───────────────────────────────────────────────
    /// 本页挂在 `TwinDrawer` 的 Content 里（Content 顶边 = 抽屉 y=96），
    /// 所以页面内坐标 = 文档 §4.1 的抽屉坐标 − 96。代码里的常量全是页面坐标，
    /// 注释里标了对应的抽屉坐标，对表时别再减一遍。
    /// </summary>
    public sealed class TwinHomePage : TwinPageBase
    {
        public override string Title { get { return "总控面板"; } }
        public override string Subtitle { get { return "1号教学楼 · 室内 + 室外路灯"; } }

        /// <summary>点「校园管理系统」时回调（抽屉负责 Push 二级页）。</summary>
        public Action OnOpenSystem;

        // ---- 布局常量（页面坐标；括号内是抽屉坐标）----
        // 【P29-3 补丁 A3】小节标题统一「上 28 / 下 12」，时间线首行再 +6 —— 三处间距全部是 4 的倍数。
        private const float PadX = 16f;
        private const float KpiY = 8f;          // 104
        private const float KpiW = 121f;
        private const float KpiH = 88f;
        private const float KpiStep = 133f;     // 16 / 149 / 282
        private const float SectionGapUp = 28f;     // 小节标题上方
        private const float SectionGapDown = 12f;   // 小节标题下方
        private const float SectionH = 16f;         // 标题行高（11 号字）
        private const float SectionEntryY = 124f;   // 220 = 192 + 28
        private const float EntryY = 152f;          // 248 = 220 + 16 + 12
        private const float EntryW = 88f;
        private const float EntryH = 104f;
        private const float EntryGap = 16f;
        private const float SectionFeedY = 284f;    // 380 = 352 + 28
        private const float FeedTopY = 312f;        // 408 = 380 + 16 + 12（竖线起点）
        private const float FeedFirstRow = 6f;      // 首行再往下 6
        private const float FeedRowH = 52f;
        private const float FeedLineH = 214f;       // 408→622
        private const int FeedRows = 4;

        private TwinMetricCard _cardDevice;
        private TwinMetricCard _cardOnline;
        private TwinMetricCard _cardAlarm;

        private EntryTile _mainEntry;
        private EntryTile _subEntry;

        private Text _feedCaption;                  // 「近 1 小时 N 条」
        private Text _feedEmpty;                    // 「暂无动态」
        private readonly Text[] _feedMain = new Text[FeedRows];
        private readonly Text[] _feedSub = new Text[FeedRows];
        private readonly Text[] _feedTime = new Text[FeedRows];
        private readonly TwinRoundedRect[] _feedDot = new TwinRoundedRect[FeedRows];

        private readonly List<FeedItem> _feedBuf = new List<FeedItem>(8);
        private readonly List<TwinAlarmHistory.AlarmRecord> _alarmBuf =
            new List<TwinAlarmHistory.AlarmRecord>(4);

        private float _nextStatRefresh;
        private float _nextFeedRefresh;
        private bool _built;

        // ---------------------------------------------------------------- 构建

        /// <summary>
        /// 在 `OnShow` 里构建，**不要**在 Push 之前构建：那一刻页面还是 RectTransform 默认尺寸，
        /// 任何"读父节点宽度算居中"的逻辑都会算错（历史上图标整组跑到抽屉外面过）。
        /// </summary>
        public override void OnShow()
        {
            if (!_built)
            {
                _built = true;
                BuildContent();
            }
            RefreshStats();
            RefreshFeed();
            _nextStatRefresh = Time.unscaledTime + 0.5f;
            _nextFeedRefresh = Time.unscaledTime + 2f;
        }

        private void BuildContent()
        {
            RectTransform root = transform as RectTransform;
            if (root == null) return;

            BuildKpiRow(root);
            BuildEntries(root);
            BuildFeed(root);
        }

        // ---- 分区 B：3 张 KPI ----

        private void BuildKpiRow(RectTransform root)
        {
            _cardDevice = TwinMetricCard.Create(root, "Kpi_Device", PadX, KpiY,
                KpiW, KpiH, TwinTheme.FsDisplay, "设备总数", "台");
            _cardOnline = TwinMetricCard.Create(root, "Kpi_Online", PadX + KpiStep, KpiY,
                KpiW, KpiH, TwinTheme.FsDisplay, "在线", "台");
            _cardAlarm = TwinMetricCard.Create(root, "Kpi_Alarm", PadX + KpiStep * 2f, KpiY,
                KpiW, KpiH, TwinTheme.FsDisplay, "告警", "台");
        }

        // ---- 分区 C：功能入口 ----

        private void BuildEntries(RectTransform root)
        {
            SectionLabel(root, "功能入口", SectionEntryY);

            // 整组居中：88×2 + 16 = 192，420−192 = 228，左右各 114
            float total = EntryW * 2f + EntryGap;
            float x0 = Mathf.Round((420f - total) * 0.5f);

            _mainEntry = BuildEntry(root, "Entry_System", x0, true, "管", "校园管理系统", "5 个模块",
                () => { Action a = OnOpenSystem; if (a != null) a(); });

            _subEntry = BuildEntry(root, "Entry_Weather", x0 + EntryW + EntryGap, false, "天", "天气系统", "预留",
                () => TwinToast.Show("天气系统要和「时间系统」一起做，本轮只留入口"));
        }

        private EntryTile BuildEntry(RectTransform parent, string name, float x, bool primary,
                                     string glyph, string title, string sub, Action onClick)
        {
            GameObject go = TwinUIFactory.Node(name, parent);
            RectTransform rt = (RectTransform)go.transform;
            TwinUIFactory.Place(rt, x, EntryY, EntryW, EntryH);

            // 底：主入口用 EntryFill（Δ14.8），次入口用 Hover（Δ11.5）—— 都是标定过的 token
            Color fill = primary ? TwinTheme.EntryFill : TwinTheme.Hover;
            TwinRoundedRect bg = TwinUIFactory.Rounded(rt, "Bg", TwinTheme.R12,
                fill, fill, TwinTheme.Border, TwinTheme.StrokeHair.Width, true);
            TwinUIFactory.Stretch(bg.rectTransform);

            Button btn = go.AddComponent<Button>();
            btn.targetGraphic = bg;
            btn.transition = Selectable.Transition.None;
            if (onClick != null) btn.onClick.AddListener(() => onClick());

            // 图标底 56 R14 + 汉字 24（§6.1：字形按 size×0.43 走，不算字号字面量）
            TwinRoundedRect iconBase = TwinUIFactory.Rounded(rt, "IconBase", 14f,
                TwinTheme.EntryIconFill, TwinTheme.EntryIconFill, Color.clear, 0f, false);
            TwinUIFactory.Place(iconBase.rectTransform, 16f, 8f, 56f, 56f);

            Text g = TwinUIFactory.Label(iconBase.rectTransform, "Glyph", 24,
                TextAnchor.MiddleCenter, primary ? TwinTheme.Accent : TwinTheme.TextMuted);
            TwinUIFactory.Stretch(g.rectTransform);
            g.text = glyph;
            g.raycastTarget = false;

            Text t = TwinUIFactory.Label(rt, "Name", TwinTheme.FsCaption, TextAnchor.UpperCenter,
                primary ? TwinTheme.TextPrimary : TwinTheme.TextMuted);
            TwinUIFactory.Place(t.rectTransform, 2f, 66f, EntryW - 4f, 14f);
            t.text = title;
            t.raycastTarget = false;

            Text s = TwinUIFactory.Label(rt, "Sub", TwinTheme.FsMicro, TextAnchor.UpperCenter,
                primary ? TwinTheme.Accent : TwinTheme.TextMuted);
            TwinUIFactory.Place(s.rectTransform, 2f, 82f, EntryW - 4f, 12f);
            s.text = sub;
            s.raycastTarget = false;

            EntryTile tile = new EntryTile();
            tile.Root = rt;

            if (primary)
            {
                // 双层描边（外 3px@Δ5.6 + 内 1px@Δ75.8）—— 这是全屏唯一的发光焦点，见 ApplyFocus()
                TwinPaint.DoubleStroke(rt, TwinTheme.R12, TwinTheme.GlowOuter, 3f, TwinTheme.GlowInner);

                // 左上角括号：CornerBrackets 会画四角，这里把另外三面关掉，只留 TL 记号
                TwinPaint.CornerBrackets(rt, 10f, TwinTheme.GlowInner, 6f);
                Transform brackets = rt.Find("_Brackets");
                if (brackets != null)
                {
                    for (int i = 0; i < brackets.childCount; i++)
                    {
                        Transform seg = brackets.GetChild(i);
                        if (!seg.name.StartsWith("TL", StringComparison.Ordinal)) seg.gameObject.SetActive(false);
                    }
                }

                tile.GlowOuter = rt.Find("_GlowOuter") != null ? rt.Find("_GlowOuter").gameObject : null;
                tile.GlowInner = rt.Find("_GlowInner") != null ? rt.Find("_GlowInner").gameObject : null;
            }

            return tile;
        }

        // ---- 分区 D：实时动态 ----

        private void BuildFeed(RectTransform root)
        {
            SectionLabel(root, "实时动态", SectionFeedY);

            _feedCaption = TwinUIFactory.Label(root, "FeedCaption", TwinTheme.FsCaption,
                TextAnchor.UpperRight, TwinTheme.TextMuted);
            TwinUIFactory.Place(_feedCaption.rectTransform, 200f, SectionFeedY, 420f - PadX - 200f, 16f);
            _feedCaption.text = "";

            // 竖线：x=22，从 408 到 622（页面坐标 312→526）。用 TimelineSpine（Δ11.5），不是 Divider。
            Image line = TwinUIFactory.Block(root, "FeedLine", TwinTheme.TimelineSpine, false);
            TwinUIFactory.Place(line.rectTransform, 22f, FeedTopY, 1f, FeedLineH);

            for (int i = 0; i < FeedRows; i++)
            {
                float y = FeedTopY + FeedFirstRow + i * FeedRowH;

                TwinRoundedRect dot = TwinUIFactory.Rounded(root, "Dot_" + i, 4f,
                    TwinTheme.StatusUnknown, TwinTheme.StatusUnknown, Color.clear, 0f, false);
                TwinUIFactory.Place(dot.rectTransform, 18f, y + 22f, 8f, 8f);
                _feedDot[i] = dot;

                Text main = TwinUIFactory.Label(root, "Row_" + i, TwinTheme.FsCaption,
                    TextAnchor.UpperLeft, TwinTheme.TextSecondary);
                TwinUIFactory.Place(main.rectTransform, 34f, y + 8f, 420f - 34f - 78f, 16f);

                Text sub = TwinUIFactory.Label(root, "RowSub_" + i, TwinTheme.FsMicro,
                    TextAnchor.UpperLeft, TwinTheme.TextMuted);
                TwinUIFactory.Place(sub.rectTransform, 34f, y + 26f, 420f - 34f - 78f, 12f);

                Text time = TwinUIFactory.Label(root, "RowTime_" + i, TwinTheme.FsMicro,
                    TextAnchor.UpperRight, TwinTheme.TextMuted);
                TwinUIFactory.Place(time.rectTransform, 420f - PadX - 70f, y + 8f, 70f, 12f);

                _feedMain[i] = main;
                _feedSub[i] = sub;
                _feedTime[i] = time;
                SetRowActive(i, false);
            }

            _feedEmpty = TwinUIFactory.Label(root, "FeedEmpty", TwinTheme.FsCaption,
                TextAnchor.UpperLeft, TwinTheme.TextMuted);
            TwinUIFactory.Place(_feedEmpty.rectTransform, 34f, FeedTopY + FeedFirstRow, 300f, 16f);
            _feedEmpty.text = "暂无动态";
            _feedEmpty.gameObject.SetActive(false);
        }

        private void SetRowActive(int i, bool on)
        {
            if (_feedDot[i] != null) _feedDot[i].gameObject.SetActive(on);
            if (_feedMain[i] != null) _feedMain[i].gameObject.SetActive(on);
            if (_feedSub[i] != null) _feedSub[i].gameObject.SetActive(on);
            if (_feedTime[i] != null) _feedTime[i].gameObject.SetActive(on);
        }

        /// <summary>小节标题 + 右侧一条 hairline（从文字右侧拉到 width−16）。</summary>
        private static void SectionLabel(RectTransform parent, string text, float y)
        {
            Text t = TwinUIFactory.Label(parent, "Sec_" + text, TwinTheme.FsCaption,
                TextAnchor.UpperLeft, TwinTheme.TextMuted);
            TwinUIFactory.Place(t.rectTransform, PadX, y, 200f, 16f);
            t.text = text;

            float w = t.preferredWidth;
            float lineX = PadX + w + 8f;
            Image line = TwinUIFactory.Block(parent, "SecLine_" + text, TwinTheme.Divider, false);
            TwinUIFactory.Place(line.rectTransform, lineX, y + 7f, Mathf.Max(0f, 420f - PadX - lineX), 1f);
        }

        // ---------------------------------------------------------------- 刷新

        private void Update()
        {
            if (!_built) return;

            if (Time.unscaledTime >= _nextStatRefresh)
            {
                _nextStatRefresh = Time.unscaledTime + 0.5f;
                RefreshStats();
            }
            if (Time.unscaledTime >= _nextFeedRefresh)
            {
                _nextFeedRefresh = Time.unscaledTime + 2f;
                RefreshFeed();
            }
        }

        private void RefreshStats()
        {
            TwinRuntime rt = TwinRuntime.Instance;
            if (rt == null || rt.Registry == null) return;

            int total = 0, online = 0, alarm = 0;
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

            _cardDevice.SetValue(total.ToString());
            _cardOnline.SetValue(online.ToString());
            _cardAlarm.SetValue(alarm.ToString());

            _cardDevice.SetState(TwinMetricCard.CardState.Normal);
            _cardOnline.SetState(TwinMetricCard.CardState.Normal);
            _cardAlarm.SetState(alarm > 0 ? TwinMetricCard.CardState.Alarm : TwinMetricCard.CardState.Normal);

            ApplyFocus(alarm > 0);
        }

        /// <summary>
        /// 一个面板只允许一个发光焦点（§2 推论）。有未确认告警时告警卡呼吸、主入口只留描边；
        /// 没告警时反过来。**两者互斥** —— 同时亮等于没有焦点。
        /// </summary>
        private void ApplyFocus(bool alarmFocus)
        {
            if (_cardAlarm != null) _cardAlarm.SetBreathing(alarmFocus);
            if (_mainEntry == null) return;
            _mainEntry.SetGlow(!alarmFocus);
        }

        private void RefreshFeed()
        {
            _feedBuf.Clear();

            // 命令日志：越新 Age 越小。多取一些只为统计「近 1 小时 N 条」，
            // 只取 4 条的话标题永远显示 ≤4，那是个假数。
            TwinCommandCenter cc = TwinCommandCenter.Instance;
            if (cc != null && cc.Log != null)
            {
                double now = TwinClock.NowUtcSeconds();
                IReadOnlyList<TwinCommandLog.Entry> recent = cc.Log.Recent(60);
                for (int i = 0; i < recent.Count; i++)
                {
                    TwinCommandLog.Entry e = recent[i];
                    FeedItem f = new FeedItem();
                    f.Age = (float)Math.Max(0d, now - e.AtUtc);
                    f.Status = e.Ok ? TwinStatus.Normal : TwinStatus.Alarm;
                    f.Main = e.Action;
                    f.Sub = e.TwinId;
                    f.Time = e.Time;
                    _feedBuf.Add(f);
                }
            }

            // 告警历史：StartTime 是 realtimeSinceStartup，所以 Age 用同一个时钟算
            TwinAlarmHistory ah = TwinAlarmHistory.Instance;
            if (ah != null)
            {
                ah.GetRecent(_alarmBuf, 30);
                float now = Time.realtimeSinceStartup;
                for (int i = 0; i < _alarmBuf.Count; i++)
                {
                    TwinAlarmHistory.AlarmRecord r = _alarmBuf[i];
                    FeedItem f = new FeedItem();
                    f.Age = Mathf.Max(0f, now - r.StartTime);
                    f.Status = r.PeakStatus;
                    f.Main = string.IsNullOrEmpty(r.DisplayName) ? "告警" : r.DisplayName;
                    f.Sub = string.IsNullOrEmpty(r.Zone) ? r.TwinId : r.Zone;
                    f.Time = r.Clock;
                    _feedBuf.Add(f);
                }
            }

            int hourCount = 0;
            for (int i = 0; i < _feedBuf.Count; i++)
                if (_feedBuf[i].Age <= 3600f) hourCount++;

            _feedBuf.Sort(CompareByAge);

            int shown = Mathf.Min(FeedRows, _feedBuf.Count);
            for (int i = 0; i < FeedRows; i++)
            {
                if (i < shown)
                {
                    FeedItem f = _feedBuf[i];
                    SetRowActive(i, true);
                    SetText(_feedMain[i], f.Main);
                    SetText(_feedSub[i], f.Sub);
                    SetText(_feedTime[i], f.Time);
                    Color c = StatusColor(f.Status);
                    _feedDot[i].SetFill(c, c);
                }
                else
                {
                    SetRowActive(i, false);
                }
            }

            if (_feedEmpty != null) _feedEmpty.gameObject.SetActive(shown == 0);
            if (_feedCaption != null) SetText(_feedCaption, "近 1 小时 " + hourCount + " 条");
        }

        private static int CompareByAge(FeedItem a, FeedItem b)
        {
            return a.Age.CompareTo(b.Age);
        }

        private static Color StatusColor(TwinStatus s)
        {
            switch (s)
            {
                case TwinStatus.Alarm: return TwinTheme.StatusAlarm;
                case TwinStatus.Warning: return TwinTheme.StatusWarning;
                case TwinStatus.Offline: return TwinTheme.StatusOffline;
                case TwinStatus.Stale: return TwinTheme.StatusStale;
                case TwinStatus.Normal: return TwinTheme.StatusNormal;
                default: return TwinTheme.StatusUnknown;
            }
        }

        private static void SetText(Text t, string v)
        {
            if (t == null) return;
            if (!string.Equals(t.text, v, StringComparison.Ordinal)) t.text = v;
        }

        // ---------------------------------------------------------------- 内部类型

        private struct FeedItem
        {
            public float Age;          // 秒（越小越新）
            public TwinStatus Status;
            public string Main;
            public string Sub;
            public string Time;
        }

        private sealed class EntryTile
        {
            public RectTransform Root;
            public GameObject GlowOuter;
            public GameObject GlowInner;

            public void SetGlow(bool on)
            {
                if (GlowOuter != null) GlowOuter.SetActive(on);
                if (GlowInner != null) GlowInner.SetActive(on);
            }
        }
    }
}

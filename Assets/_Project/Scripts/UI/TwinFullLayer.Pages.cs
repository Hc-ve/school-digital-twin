using System;
using System.Collections.Generic;
using System.Text;
using SchoolTwin.Command;
using SchoolTwin.Core;
using SchoolTwin.Twin;
using UnityEngine;
using UnityEngine.UI;

namespace SchoolTwin.UI
{
    /// <summary>
    /// 全屏 L2 的「电力管理 / 告警中心 / 数据统计」三页（P29-6 收口接入，P28 §4.3–4.5）。
    ///
    /// 拆成 partial 文件的原因：TwinFullLayer 主文件已 950 行，再塞三页会破 1600 行，
    /// 改一个坐标要滚半天。这三页共用同一套骨架常量（RailW / MidW / Pad）和组容器，
    /// 所以用 partial 而不是独立组件 —— 独立组件要再传一遍父节点与布局参数，反而更绕。
    ///
    /// ── 三页各自的取数 ──────────────────────────────────────────
    /// 电力：TwinCircuit（20 节点三级回路，SetSwitch 返回影响设备数）
    /// 告警：TwinAlarmHistory（进行中 + 历史）、TwinCommandLog（按 TwinId 过滤）
    /// 统计：TwinDeviceStats.AllDevices（按 Category / Zone 聚合）
    /// </summary>
    public partial class TwinFullLayer
    {
        private const float PageX = RailW + Pad;          // 216
        private const float PageW = MidW - Pad * 2f;      // 568

        // ================================================================ 电力管理

        private const int PwrRows = 20;
        private const float PwrRowH = 28f;

        private readonly RectTransform[] _pwrRow = new RectTransform[PwrRows];
        private readonly TwinRoundedRect[] _pwrRowBg = new TwinRoundedRect[PwrRows];
        private readonly Text[] _pwrTitle = new Text[PwrRows];
        private readonly TwinRoundedRect[] _pwrPill = new TwinRoundedRect[PwrRows];
        private readonly Text[] _pwrPillText = new Text[PwrRows];
        private readonly Text[] _pwrCount = new Text[PwrRows];
        private readonly List<TwinCircuitNode> _pwrNodes = new List<TwinCircuitNode>(PwrRows);

        private Text _pwrImpact;
        private Text _pwrImpactSub;
        private Text _pwrSboHint;
        private string _pwrSelected = "";

        /// <summary>待确认的回路 id。空 = 没有待确认。</summary>
        private string _sboId = "";
        private float _sboUntil;

        /// <summary>SBO 第二次确认的窗口（规格 §4.3：Armed 10s）。</summary>
        private const float SboWindow = 10f;

        private void BuildPowerPage()
        {
            Text title = TwinUIFactory.Label(_powerGroup, "FPwrTitle", TwinTheme.FsSubtitle,
                TextAnchor.UpperLeft, TwinTheme.TextPrimary);
            TwinUIFactory.Place(title.rectTransform, PageX, 92f, PageW, 22f);
            title.text = "电力管理";

            Text desc = TwinUIFactory.Label(_powerGroup, "FPwrDesc", TwinTheme.FsMicro,
                TextAnchor.UpperLeft, TwinTheme.TextMuted);
            TwinUIFactory.Place(desc.rectTransform, PageX, 116f, PageW, 14f);
            desc.text = "楼栋 → 楼层 → 房间 三级回路 · 点一次进入待确认，再点一次执行（10 秒内有效）";

            BuildPowerTree();

            // 影响面卡：y=508，高 100（页面底 660，留 52）
            TwinRoundedRect card = TwinUIFactory.Rounded(_powerGroup, "FPwrImpact", TwinTheme.R12,
                TwinTheme.CardTop, TwinTheme.CardBottom, TwinTheme.Border, TwinTheme.StrokeHair.Width, false);
            TwinUIFactory.Place(card.rectTransform, PageX, 508f, PageW, 100f);

            _pwrImpact = TwinUIFactory.Label(card.rectTransform, "T", TwinTheme.FsCaption,
                TextAnchor.UpperLeft, TwinTheme.TextSecondary);
            TwinUIFactory.Place(_pwrImpact.rectTransform, 16f, 14f, PageW - 32f, 18f);
            _pwrImpact.text = "未选中回路";

            _pwrImpactSub = TwinUIFactory.Label(card.rectTransform, "S", TwinTheme.FsMicro,
                TextAnchor.UpperLeft, TwinTheme.TextMuted);
            TwinUIFactory.Place(_pwrImpactSub.rectTransform, 16f, 38f, PageW - 32f, 16f);
            _pwrImpactSub.text = "展开树节点，点一次进入待确认，再点一次执行";

            _pwrSboHint = TwinUIFactory.Label(card.rectTransform, "H", TwinTheme.FsCaption,
                TextAnchor.UpperLeft, TwinTheme.Accent);
            TwinUIFactory.Place(_pwrSboHint.rectTransform, 16f, 60f, PageW - 32f, 18f);
            _pwrSboHint.text = "";
        }

        /// <summary>
        /// 三级树。20 个节点全展开 + 内部滚动（行高 28，总高约 580 > 视口 360）。
        /// 滚动要接收垫 —— scrollRt 上没有 Graphic 时滚轮事件到不了 ScrollRect（P29-6 已知坑）。
        /// </summary>
        private void BuildPowerTree()
        {
            GameObject viewGo = TwinUIFactory.Node("FPwrView", _powerGroup);
            RectTransform viewRt = (RectTransform)viewGo.transform;
            TwinUIFactory.Place(viewRt, PageX, 140f, PageW, 350f);

            // 接收垫：透明但 raycastTarget=true，否则滚轮打不到
            Image pad = TwinUIFactory.Block(viewRt, "Pad", new Color(0f, 0f, 0f, 0f), true);
            TwinUIFactory.Stretch(pad.rectTransform);

            GameObject contentGo = TwinUIFactory.Node("FPwrContent", viewRt);
            RectTransform contentRt = (RectTransform)contentGo.transform;
            contentRt.anchorMin = new Vector2(0f, 1f);
            contentRt.anchorMax = new Vector2(1f, 1f);
            contentRt.pivot = new Vector2(0.5f, 1f);
            contentRt.anchoredPosition = Vector2.zero;
            contentRt.sizeDelta = new Vector2(0f, PwrRows * PwrRowH + 8f);

            for (int i = 0; i < PwrRows; i++)
            {
                TwinRoundedRect row = TwinUIFactory.Rounded(contentRt, "FRow_" + i, TwinTheme.R8,
                    Color.clear, Color.clear, Color.clear, 0f, true);
                TwinUIFactory.Place(row.rectTransform, 0f, i * PwrRowH, PageW, PwrRowH - 2f);
                _pwrRow[i] = row.rectTransform;
                _pwrRowBg[i] = row;

                Text t = TwinUIFactory.Label(row.rectTransform, "T", TwinTheme.FsCaption,
                    TextAnchor.MiddleLeft, TwinTheme.TextSecondary);
                TwinUIFactory.Place(t.rectTransform, 8f, 0f, PageW - 200f, PwrRowH - 2f);
                t.raycastTarget = false;
                _pwrTitle[i] = t;

                TwinRoundedRect pill = TwinUIFactory.Rounded(row.rectTransform, "P", 9f,
                    TwinTheme.StatusNormal, TwinTheme.StatusNormal, Color.clear, 0f, false);
                TwinUIFactory.Place(pill.rectTransform, PageW - 150f, 4f, 52f, 18f);
                _pwrPill[i] = pill;

                Text pt = TwinUIFactory.Label(pill.rectTransform, "PT", TwinTheme.FsMicro,
                    TextAnchor.MiddleCenter, TwinTheme.TextPrimary);
                TwinUIFactory.Stretch(pt.rectTransform);
                pt.raycastTarget = false;
                _pwrPillText[i] = pt;

                Text c = TwinUIFactory.Label(row.rectTransform, "C", TwinTheme.FsMicro,
                    TextAnchor.MiddleRight, TwinTheme.TextMuted);
                TwinUIFactory.Place(c.rectTransform, PageW - 92f, 0f, 84f, PwrRowH - 2f);
                c.raycastTarget = false;
                _pwrCount[i] = c;

                int idx = i;
                Button btn = row.gameObject.AddComponent<Button>();
                btn.transition = Selectable.Transition.None;
                btn.onClick.AddListener(delegate { OnPowerRowClicked(idx); });
            }

            // ScrollRect 挂在 view 上，content 给它
            ScrollRect scroll = viewGo.AddComponent<ScrollRect>();
            scroll.content = contentRt;
            scroll.viewport = viewRt;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 24f;
            viewGo.AddComponent<RectMask2D>();
        }

        /// <summary>
        /// 点一次 = 进入待确认（10s），再点同一条 = 执行。
        ///
        /// 为什么自己实现而不复用现成弹层：项目里没有 TwinSboGate / TwinSboDialog
        /// （P28 §4.3 写了，但没实现；TwinPowerController 只有心跳与在线判定）。
        /// 这里做成"行内两步"而不是模态弹层 —— 电力页要连着切好几路，弹层会挡视线。
        /// </summary>
        private void OnPowerRowClicked(int index)
        {
            if (index >= _pwrNodes.Count) return;

            TwinCircuitNode node = _pwrNodes[index];
            if (node == null) return;

            _pwrSelected = node.Id;

            float now = Time.unscaledTime;
            bool armed = !string.IsNullOrEmpty(_sboId) && _sboId == node.Id && now < _sboUntil;

            if (!armed)
            {
                // 第一步：进入待确认
                _sboId = node.Id;
                _sboUntil = now + SboWindow;
                RefreshPowerPage();
                return;
            }

            // 第二步：执行
            int affected = TwinCircuit.SetSwitch(node.Id, !node.On);
            _sboId = "";
            _sboUntil = 0f;

            TwinToast.Show(node.Title + (node.On ? " 已分闸" : " 已合闸") +
                           "（影响 " + affected + " " + TwinCode.UnitDevice + "）");
            RefreshPowerPage();
        }

        private void RefreshPowerPage()
        {
            // 节点表：TwinCircuit.All 已按层级顺序，直接取前 20
            _pwrNodes.Clear();
            IReadOnlyList<TwinCircuitNode> all = TwinCircuit.All;
            if (all != null)
            {
                for (int i = 0; i < all.Count && _pwrNodes.Count < PwrRows; i++)
                    if (all[i] != null) _pwrNodes.Add(all[i]);
            }

            // SBO 超时清空
            if (!string.IsNullOrEmpty(_sboId) && Time.unscaledTime > _sboUntil)
            {
                _sboId = "";
                _sboUntil = 0f;
            }

            float now = Time.unscaledTime;
            int shown = 0;

            for (int i = 0; i < PwrRows; i++)
            {
                bool on = i < _pwrNodes.Count;
                if (_pwrRow[i] != null && _pwrRow[i].gameObject.activeSelf != on)
                    _pwrRow[i].gameObject.SetActive(on);
                if (!on) continue;

                TwinCircuitNode n = _pwrNodes[i];
                shown++;

                // 缩进按层级：楼栋 8 / 楼层 24 / 房间 40
                float indent = 8f + n.Level * 16f;
                RectTransform tr = _pwrTitle[i].rectTransform;
                tr.anchoredPosition = new Vector2(indent, tr.anchoredPosition.y);

                int level = Mathf.Clamp(n.Level, 0, 2);
                int size = level == 0 ? TwinTheme.FsBody : (level == 1 ? TwinTheme.FsCaption : TwinTheme.FsMicro);
                _pwrTitle[i].fontSize = size;
                _pwrTitle[i].color = level == 0 ? TwinTheme.TextPrimary
                                    : (level == 1 ? TwinTheme.TextSecondary : TwinTheme.TextMuted);
                SetText(_pwrTitle[i], n.Title);

                Color state = n.On ? TwinTheme.StatusNormal : TwinTheme.StatusOffline;
                _pwrPill[i].SetFill(TwinTheme.WithAlpha(state, 0.18f), TwinTheme.WithAlpha(state, 0.18f));
                SetText(_pwrPillText[i], n.On ? "通电" : "断电");
                _pwrPillText[i].color = state;

                int count = TwinCircuit.CountDevices(n.Id);
                SetText(_pwrCount[i], count > 0 ? count + " " + TwinCode.UnitDevice : "");

                // 选中高亮：待确认中用橙色，普通选中用主色
                bool sel = n.Id == _pwrSelected;
                bool armed = sel && !string.IsNullOrEmpty(_sboId) && _sboId == n.Id && now < _sboUntil;

                Color rowFill = armed ? TwinTheme.WithAlpha(TwinTheme.StatusWarning, 0.16f)
                              : (sel ? TwinTheme.AccentDim : Color.clear);
                if (_pwrRowBg[i] != null) _pwrRowBg[i].SetFill(rowFill, rowFill);
            }

            RefreshPowerImpact(now);
        }

        private void RefreshPowerImpact(float now)
        {
            if (_pwrImpact == null) return;

            TwinCircuitNode sel = string.IsNullOrEmpty(_pwrSelected)
                ? null
                : TwinCircuit.Find(_pwrSelected);

            if (sel == null)
            {
                SetText(_pwrImpact, "未选中回路");
                SetText(_pwrImpactSub, "点列表任意一行：第一次进入待确认，第二次执行");
                SetText(_pwrSboHint, "");
                return;
            }

            int count = TwinCircuit.CountDevices(sel.Id);
            bool armed = !string.IsNullOrEmpty(_sboId) && _sboId == sel.Id && now < _sboUntil;

            SetText(_pwrImpact, sel.Title + " · 当前" + (sel.On ? "通电" : "断电"));
            SetText(_pwrImpactSub,
                (sel.On ? "分闸" : "合闸") + "将影响 " + count + " " + TwinCode.UnitDevice +
                "（此处为影响面预览，不含额定功率估算）");

            if (armed)
            {
                int left = Mathf.CeilToInt(_sboUntil - now);
                SetText(_pwrSboHint, "再点同一行确认 " + (sel.On ? "分闸" : "合闸") +
                                     " · " + left + "s");
                _pwrSboHint.color = TwinTheme.StatusWarning;
            }
            else
            {
                SetText(_pwrSboHint, "点该行进入待确认（10 秒内有效）");
                _pwrSboHint.color = TwinTheme.Accent;
            }
        }

        // ================================================================ 告警中心

        private const int AlmRowsFull = 6;

        private readonly RectTransform[] _acRow = new RectTransform[AlmRowsFull];
        private readonly Image[] _acBar = new Image[AlmRowsFull];
        private readonly Text[] _acId = new Text[AlmRowsFull];
        private readonly Text[] _acDesc = new Text[AlmRowsFull];
        private readonly Text[] _acDur = new Text[AlmRowsFull];
        private readonly List<TwinAlarmHistory.AlarmRecord> _acBuf =
            new List<TwinAlarmHistory.AlarmRecord>(64);

        private readonly List<RectTransform> _tlItems = new List<RectTransform>(12);
        private readonly List<Text> _tlTexts = new List<Text>(12);
        private readonly List<Image> _tlDots = new List<Image>(12);
        private RectTransform _tlRoot;
        private Text _tlTitle;
        private string _acSelected = "";
        private readonly StringBuilder _tlSb = new StringBuilder(128);

        private void BuildAlarmCenterPage()
        {
            Text title = TwinUIFactory.Label(_alarmCenterGroup, "FAlmTitle", TwinTheme.FsSubtitle,
                TextAnchor.UpperLeft, TwinTheme.TextPrimary);
            TwinUIFactory.Place(title.rectTransform, PageX, 92f, PageW, 22f);
            title.text = "告警中心";

            // 进行中 / 历史 切换（任务包要求"进行中 + 历史"，原来只有进行中）
            _almTabOn = MakeAlarmTab("FTabOn", "进行中", PageX + 250f);
            _almTabHis = MakeAlarmTab("FTabHis", "历史", PageX + 350f);

            _tlTitle = TwinUIFactory.Label(_alarmCenterGroup, "FAlmSub", TwinTheme.FsMicro,
                TextAnchor.UpperRight, TwinTheme.TextMuted);
            TwinUIFactory.Place(_tlTitle.rectTransform, PageX, 94f, PageW, 16f);
            _tlTitle.text = "点单条看全链路";

            // 进行中列表：6 行 × 44，y=124 起
            for (int i = 0; i < AlmRowsFull; i++)
            {
                float y = 124f + i * 46f;

                TwinRoundedRect row = TwinUIFactory.Rounded(_alarmCenterGroup, "FAlm_" + i, TwinTheme.R8,
                    Color.clear, Color.clear, TwinTheme.Border, TwinTheme.StrokeHair.Width, true);
                TwinUIFactory.Place(row.rectTransform, PageX, y, PageW, 42f);
                _acRow[i] = row.rectTransform;

                Image bar = TwinUIFactory.Block(row.rectTransform, "Bar", TwinTheme.StatusAlarm, false);
                TwinUIFactory.Place(bar.rectTransform, 0f, 9f, 3f, 24f);
                _acBar[i] = bar;

                Text id = TwinUIFactory.Label(row.rectTransform, "Id", TwinTheme.FsCaption,
                    TextAnchor.MiddleLeft, TwinTheme.TextPrimary);
                TwinUIFactory.Place(id.rectTransform, 14f, 0f, 150f, 42f);
                id.raycastTarget = false;
                _acId[i] = id;

                Text desc = TwinUIFactory.Label(row.rectTransform, "Desc", TwinTheme.FsMicro,
                    TextAnchor.MiddleLeft, TwinTheme.TextMuted);
                TwinUIFactory.Place(desc.rectTransform, 170f, 0f, 300f, 42f);
                desc.raycastTarget = false;
                _acDesc[i] = desc;

                Text dur = TwinUIFactory.Label(row.rectTransform, "Dur", TwinTheme.FsMicro,
                    TextAnchor.MiddleRight, TwinTheme.TextMuted);
                TwinUIFactory.Place(dur.rectTransform, PageW - 96f, 0f, 84f, 42f);
                dur.raycastTarget = false;
                _acDur[i] = dur;

                int idx = i;
                Button btn = row.gameObject.AddComponent<Button>();
                btn.transition = Selectable.Transition.None;
                btn.onClick.AddListener(delegate { OnAlarmRowClicked(idx); });
            }

            // 列表空状态：没有告警时不能只留一片空白，要明确说"没有"
            _acEmpty = TwinUIFactory.Label(_alarmCenterGroup, "FAlmEmpty", TwinTheme.FsCaption,
                TextAnchor.MiddleLeft, TwinTheme.TextMuted);
            TwinUIFactory.Place(_acEmpty.rectTransform, PageX, 240f, PageW, 18f);
            _acEmpty.text = "";
            _acEmpty.raycastTarget = false;

            // 时间线区
            TwinRoundedRect tlCard = TwinUIFactory.Rounded(_alarmCenterGroup, "FTlCard", TwinTheme.R12,
                TwinTheme.CardTop, TwinTheme.CardBottom, TwinTheme.Border, TwinTheme.StrokeHair.Width, false);
            TwinUIFactory.Place(tlCard.rectTransform, PageX, 410f, PageW, 234f);

            Text tlHead = TwinUIFactory.Label(tlCard.rectTransform, "H", TwinTheme.FsCaption,
                TextAnchor.UpperLeft, TwinTheme.TextSecondary);
            TwinUIFactory.Place(tlHead.rectTransform, 16f, 12f, 200f, 16f);
            tlHead.text = "全链路追溯";

            GameObject tlGo = TwinUIFactory.Node("FTlRoot", tlCard.rectTransform);
            _tlRoot = (RectTransform)tlGo.transform;
            TwinUIFactory.Place(_tlRoot, 16f, 34f, PageW - 32f, 186f);

            Text empty = TwinUIFactory.Label(_tlRoot, "Empty", TwinTheme.FsMicro,
                TextAnchor.UpperLeft, TwinTheme.TextMuted);
            TwinUIFactory.Place(empty.rectTransform, 0f, 0f, PageW - 32f, 16f);
            empty.text = "选中上方任意一条告警，这里显示 触发 → 判定 → 通知 → 报文 → 命令 的完整链路";
            _tlEmpty = empty;
        }

        private Text _tlEmpty;
        private Text _almTabOn;
        private Text _almTabHis;
        private Text _acEmpty;
        private bool _acHistory;

        /// <summary>告警页的「进行中 / 历史」切换按钮。两个都是 Text + Button，不引新控件。</summary>
        private Text MakeAlarmTab(string name, string label, float x)
        {
            Text t = TwinUIFactory.Label(_alarmCenterGroup, name, TwinTheme.FsCaption,
                TextAnchor.MiddleLeft, TwinTheme.TextMuted);
            TwinUIFactory.Place(t.rectTransform, x, 94f, 90f, 20f);
            t.text = label;
            t.raycastTarget = true;

            int idx = name.EndsWith("On", StringComparison.Ordinal) ? 0 : 1;
            Button b = t.gameObject.AddComponent<Button>();
            b.transition = Selectable.Transition.None;
            b.onClick.AddListener(delegate { OnAlarmTabClicked(idx); });
            return t;
        }

        private void OnAlarmTabClicked(int idx)
        {
            bool history = idx == 1;
            if (_acHistory == history) return;
            _acHistory = history;
            _acSelected = "";          // 切页清掉选中，避免时间线还挂着上一条
            RefreshAlarmTabStyles();
            _nextRefresh = 0f;
            Refresh(true);
        }

        private void RefreshAlarmTabStyles()
        {
            if (_almTabOn != null)
                _almTabOn.color = _acHistory ? TwinTheme.TextMuted : TwinTheme.Accent;
            if (_almTabHis != null)
                _almTabHis.color = _acHistory ? TwinTheme.Accent : TwinTheme.TextMuted;
        }

        private void OnAlarmRowClicked(int index)
        {
            if (index >= _acBuf.Count) return;
            _acSelected = _acBuf[index].TwinId + "@" + _acBuf[index].StartTime.ToString("0.0");
            RefreshAlarmTimeline();
        }

        private void RefreshAlarmCenterPage()
        {
            _acBuf.Clear();
            TwinAlarmHistory h = TwinAlarmHistory.Instance;
            if (h != null) h.GetRecent(_acBuf, 64);

            int ongoing = 0;
            for (int i = 0; i < _acBuf.Count; i++)
                if (_acBuf[i].Ongoing) ongoing++;

            int closed = _acBuf.Count - ongoing;

            SetText(_almTabOn, "进行中 " + ongoing);
            SetText(_almTabHis, "历史 " + closed);
            RefreshAlarmTabStyles();

            float now = Time.realtimeSinceStartup;
            int n = 0;

            for (int i = 0; i < _acBuf.Count && n < AlmRowsFull; i++)
            {
                TwinAlarmHistory.AlarmRecord r = _acBuf[i];
                // 进行中 = 未恢复；历史 = 已恢复（EndTime > 0）
                if (_acHistory ? r.Ongoing : !r.Ongoing) continue;

                if (_acRow[n] != null && !_acRow[n].gameObject.activeSelf)
                    _acRow[n].gameObject.SetActive(true);

                Color c = TwinStatusPalette.Of(r.PeakStatus);
                if (_acBar[n] != null) _acBar[n].color = c;

                SetText(_acId[n], TwinCode.WithType(r.TwinId));
                SetText(_acDesc[n], r.DisplayName + " · " + TwinStatusPalette.Label(r.PeakStatus));
                SetText(_acDur[n], TwinAlarmHistory.FormatDuration(r.Duration(now)));

                n++;
            }

            for (int i = n; i < AlmRowsFull; i++)
                if (_acRow[i] != null && _acRow[i].gameObject.activeSelf)
                    _acRow[i].gameObject.SetActive(false);

            if (_acEmpty != null)
            {
                _acEmpty.gameObject.SetActive(n == 0);
                if (n == 0)
                    _acEmpty.text = _acHistory
                        ? "暂无已恢复的历史告警 —— 告警恢复后会自动落到这里"
                        : "当前无进行中告警 —— 全部 " + _acBuf.Count + " 条记录可切「历史」查看";
            }

            RefreshAlarmTimeline();
        }

        /// <summary>
        /// 单设备全链路时间线。
        ///
        /// 数据来自两处（都是现成的）：
        ///   · TwinAlarmHistory —— 触发 / 恢复（StartTime / EndTime / PeakStatus）
        ///   · TwinCommandLog   —— 命令下发与回执（按 TwinId 过滤，含耗时与失败原因）
        /// 规格里还列了「判定依据」和「报文」两类，对应的 TwinAlarmStore / TwinMessageLog
        /// 在项目里不存在，所以**只用现有两类**，不编造节点。
        /// </summary>
        private void RefreshAlarmTimeline()
        {
            if (_tlRoot == null) return;

            // AlarmRecord 是 **struct（值类型）**，不能和 null 比较、也不能返回 null。
            // 用 Try 模式：找到就 out 出来并返回 true。
            TwinAlarmHistory.AlarmRecord rec;
            if (!FindSelectedAlarm(out rec))
            {
                if (_tlEmpty != null) _tlEmpty.gameObject.SetActive(true);
                HideTimelineItems(0);
                return;
            }

            if (_tlEmpty != null) _tlEmpty.gameObject.SetActive(false);

            int used = 0;

            // ① 告警触发（时间线第一条，永远有）
            string peak = TwinStatusPalette.Label(rec.PeakStatus);
            used = AddTimelineItem(used, TwinTheme.StatusAlarm,
                "触发 · " + peak, rec.Clock + "  " + rec.DisplayName, true);

            // ② 命令日志（按 TwinId 过滤，取最近 6 条）
            TwinCommandLog log = TwinCommandCenter.Instance != null ? TwinCommandCenter.Instance.Log : null;
            if (log != null)
            {
                IReadOnlyList<TwinCommandLog.Entry> recent = log.Recent(200);
                int added = 0;
                for (int i = 0; i < recent.Count && added < 6 && used < 9; i++)
                {
                    TwinCommandLog.Entry e = recent[i];
                    if (!string.Equals(e.TwinId, rec.TwinId, StringComparison.Ordinal)) continue;

                    _tlSb.Length = 0;
                    _tlSb.Append(e.Action).Append(" · ").Append(e.Ok ? "成功" : "失败");
                    if (e.Elapsed > 0.001) _tlSb.Append(" · ").Append(e.Elapsed.ToString("0.0")).Append("s");
                    if (!string.IsNullOrEmpty(e.Reason)) _tlSb.Append(" · ").Append(e.Reason);

                    used = AddTimelineItem(used, e.Ok ? TwinTheme.Accent : TwinTheme.StatusWarning,
                        "命令 · " + e.Operator, e.Time + "  " + _tlSb, false);
                    added++;
                }
                if (added == 0 && used < 9)
                {
                    used = AddTimelineItem(used, TwinTheme.TextMuted,
                        "命令 · 无记录", "该设备本次告警期间没有下发过控制命令", false);
                }
            }

            // ③ 告警恢复（如果已恢复）
            if (rec.EndTime > 0f && used < 10)
            {
                used = AddTimelineItem(used, TwinTheme.StatusNormal,
                    "恢复 · 已清除", "持续 " + TwinAlarmHistory.FormatDuration(rec.EndTime - rec.StartTime), false);
            }

            HideTimelineItems(used);
        }

        /// <summary>
        /// 按当前选中项在缓冲里找告警记录。**用 Try 模式而不是返回引用** ——
        /// `AlarmRecord` 是 struct，没有 null 可表达"没找到"。
        /// </summary>
        private bool FindSelectedAlarm(out TwinAlarmHistory.AlarmRecord rec)
        {
            rec = default(TwinAlarmHistory.AlarmRecord);
            if (string.IsNullOrEmpty(_acSelected)) return false;

            for (int i = 0; i < _acBuf.Count; i++)
            {
                TwinAlarmHistory.AlarmRecord r = _acBuf[i];
                if (r.TwinId + "@" + r.StartTime.ToString("0.0") == _acSelected)
                {
                    rec = r;
                    return true;
                }
            }
            return false;
        }

        /// <summary>画一条时间线节点（圆点 + 竖线 + 两行文案）。返回下一条的索引。</summary>
        private int AddTimelineItem(int index, Color dotColor, string head, string body, bool first)
        {
            RectTransform item = EnsureTimelineItem(index);
            if (item == null) return index;

            if (!item.gameObject.activeSelf) item.gameObject.SetActive(true);

            float y = index * 38f;
            TwinUIFactory.Place(item, 0f, y, PageW - 32f, 36f);

            Image dot = _tlDots[index];
            if (dot != null) dot.color = dotColor;

            // 竖线：最后一条不画（避免下边悬空）
            Image line = item.Find("Line") != null ? item.Find("Line").GetComponent<Image>() : null;
            if (line != null) line.enabled = !first;

            Text t = _tlTexts[index];
            if (t != null) t.text = head;

            Text b = item.Find("Body") != null ? item.Find("Body").GetComponent<Text>() : null;
            if (b != null) b.text = body;

            return index + 1;
        }

        private RectTransform EnsureTimelineItem(int index)
        {
            while (_tlItems.Count <= index)
            {
                int i = _tlItems.Count;

                GameObject go = TwinUIFactory.Node("Tl_" + i, _tlRoot);
                RectTransform rt = (RectTransform)go.transform;
                _tlItems.Add(rt);

                // 竖线（在圆点下方，串起整条链路）
                Image line = TwinUIFactory.Block(rt, "Line", TwinTheme.Divider, false);
                TwinUIFactory.Place(line.rectTransform, 4f, 12f, 1f, 26f);

                Image dot = TwinUIFactory.Block(rt, "Dot", TwinTheme.Accent, false);
                TwinUIFactory.Place(dot.rectTransform, 0f, 4f, 9f, 9f);
                _tlDots.Add(dot);

                Text head = TwinUIFactory.Label(rt, "Head", TwinTheme.FsMicro,
                    TextAnchor.UpperLeft, TwinTheme.TextSecondary);
                TwinUIFactory.Place(head.rectTransform, 18f, 0f, PageW - 60f, 14f);
                _tlTexts.Add(head);

                Text body = TwinUIFactory.Label(rt, "Body", TwinTheme.FsMicro,
                    TextAnchor.UpperLeft, TwinTheme.TextMuted);
                TwinUIFactory.Place(body.rectTransform, 18f, 15f, PageW - 60f, 14f);
            }

            return _tlItems[index];
        }

        private void HideTimelineItems(int from)
        {
            for (int i = from; i < _tlItems.Count; i++)
                if (_tlItems[i] != null && _tlItems[i].gameObject.activeSelf)
                    _tlItems[i].gameObject.SetActive(false);
        }

        // ================================================================ 数据统计

        private const int StatsTypeMax = 5;

        private readonly Image[] _donutSeg = new Image[StatsTypeMax];
        private readonly Text[] _donutName = new Text[StatsTypeMax];
        private readonly Text[] _donutPct = new Text[StatsTypeMax];
        private readonly Image[] _floorBar = new Image[4];
        private readonly TwinRoundedRect[] _floorTrack = new TwinRoundedRect[4];
        private readonly Text[] _floorName = new Text[4];
        private readonly Text[] _floorVal = new Text[4];
        private Text _statsEmpty;
        private Text _donutCenter;

        private void BuildStatsPage()
        {
            Text title = TwinUIFactory.Label(_statsGroup, "FStTitle", TwinTheme.FsSubtitle,
                TextAnchor.UpperLeft, TwinTheme.TextPrimary);
            TwinUIFactory.Place(title.rectTransform, PageX, 92f, PageW, 22f);
            title.text = "数据统计";

            Text desc = TwinUIFactory.Label(_statsGroup, "FStDesc", TwinTheme.FsMicro,
                TextAnchor.UpperLeft, TwinTheme.TextMuted);
            TwinUIFactory.Place(desc.rectTransform, PageX, 116f, PageW, 14f);
            desc.text = "设备类型分布 · 楼层能耗估算（≈ 按类型额定功率 × 台数，非实测）";

            // 甜甜圈：200×200 @ (216, 144)
            GameObject donutGo = TwinUIFactory.Node("FDonut", _statsGroup);
            RectTransform donutRt = (RectTransform)donutGo.transform;
            TwinUIFactory.Place(donutRt, PageX + 8f, 144f, 200f, 200f);

            Sprite ring = TwinPaint.RingSprite(160, 0.62f);
            for (int i = 0; i < StatsTypeMax; i++)
            {
                Image seg = TwinUIFactory.Block(donutRt, "Seg_" + i, TwinTheme.Accent, false);
                TwinUIFactory.Stretch(seg.rectTransform);
                seg.sprite = ring;
                seg.type = Image.Type.Filled;
                seg.fillMethod = Image.FillMethod.Radial360;
                seg.fillOrigin = (int)Image.Origin360.Top;
                seg.fillAmount = 0f;
                _donutSeg[i] = seg;
            }

            _donutCenter = TwinUIFactory.Label(donutRt, "C", TwinTheme.FsSubtitle,
                TextAnchor.MiddleCenter, TwinTheme.TextPrimary);
            TwinUIFactory.Stretch(_donutCenter.rectTransform);
            _donutCenter.text = "0";
            _donutCenter.raycastTarget = false;

            // 图例（右侧）
            for (int i = 0; i < StatsTypeMax; i++)
            {
                float y = 160f + i * 34f;

                TwinRoundedRect dot = TwinUIFactory.Rounded(_statsGroup, "FDot_" + i, 3f,
                    TwinTheme.Accent, TwinTheme.Accent, Color.clear, 0f, false);
                TwinUIFactory.Place(dot.rectTransform, PageX + 232f, y + 4f, 8f, 8f);

                Text nm = TwinUIFactory.Label(_statsGroup, "FName_" + i, TwinTheme.FsMicro,
                    TextAnchor.MiddleLeft, TwinTheme.TextSecondary);
                TwinUIFactory.Place(nm.rectTransform, PageX + 248f, y, 150f, 16f);
                _donutName[i] = nm;

                Text pct = TwinUIFactory.Label(_statsGroup, "FPct_" + i, TwinTheme.FsMicro,
                    TextAnchor.MiddleRight, TwinTheme.TextMuted);
                TwinUIFactory.Place(pct.rectTransform, PageX + 400f, y, 160f, 16f);
                _donutPct[i] = pct;
            }

            // 楼层能耗横条：y=380 起 4 行
            Text fh = TwinUIFactory.Label(_statsGroup, "FFloorHead", TwinTheme.FsCaption,
                TextAnchor.UpperLeft, TwinTheme.TextMuted);
            TwinUIFactory.Place(fh.rectTransform, PageX, 368f, PageW, 16f);
            fh.text = "楼层能耗估算（未接计量表 · 按额定功率 × 台数，仅量表级）";

            for (int i = 0; i < 4; i++)
            {
                float y = 392f + i * 34f;

                Text nm = TwinUIFactory.Label(_statsGroup, "FFloorName_" + i, TwinTheme.FsMicro,
                    TextAnchor.MiddleLeft, TwinTheme.TextMuted);
                TwinUIFactory.Place(nm.rectTransform, PageX, y, 60f, 24f);
                _floorName[i] = nm;

                TwinRoundedRect track = TwinUIFactory.Rounded(_statsGroup, "FTrack_" + i, 3f,
                    TwinTheme.Track, TwinTheme.Track, Color.clear, 0f, false);
                TwinUIFactory.Place(track.rectTransform, PageX + 64f, y + 8f, 400f, 6f);
                _floorTrack[i] = track;

                Image fill = TwinUIFactory.Block(track.rectTransform, "Fill", TwinTheme.Accent, false);
                TwinUIFactory.Place(fill.rectTransform, 0f, 0f, 400f, 6f);
                _floorBar[i] = fill;

                Text v = TwinUIFactory.Label(_statsGroup, "FFloorVal_" + i, TwinTheme.FsMicro,
                    TextAnchor.MiddleRight, TwinTheme.TextSecondary);
                TwinUIFactory.Place(v.rectTransform, PageX + 472f, y, 96f, 24f);
                _floorVal[i] = v;
            }

            _statsEmpty = TwinUIFactory.Label(_statsGroup, "FStEmpty", TwinTheme.FsCaption,
                TextAnchor.UpperLeft, TwinTheme.TextMuted);
            TwinUIFactory.Place(_statsEmpty.rectTransform, PageX, 520f, PageW, 40f);
            _statsEmpty.text = "";
        }

        /// <summary>
        /// 类型分布 + 楼层能耗。
        ///
        /// 能耗**不是实测**：项目没有 `TwinEnergyEstimator` 也没有时序存储，
        /// 所以用「类型额定功率 × 台数」做量级估算，界面上统一标 ≈（裁决要求"数值标 ≈"）。
        /// 这条必须写清 —— 答辩被问"这个 4.6kW 哪来的"要能答上来。
        /// </summary>
        private void RefreshStatsPage()
        {
            Dictionary<string, int> byType = new Dictionary<string, int>(16, StringComparer.Ordinal);
            Dictionary<int, float> byFloor = new Dictionary<int, float>(4);

            IReadOnlyList<TwinAnchor> all = TwinDeviceStats.AllDevices;
            int total = 0;

            if (all != null)
            {
                for (int i = 0; i < all.Count; i++)
                {
                    TwinAnchor a = all[i];
                    if (!TwinDeviceStats.IsActiveDevice(a)) continue;

                    total++;
                    string type = TwinCode.TypeName(a.Category);
                    if (string.IsNullOrEmpty(type)) type = "其他";

                    int n;
                    byType.TryGetValue(type, out n);
                    byType[type] = n + 1;

                    TwinRoomInfo info = TwinSiteLayout.Find(a.Zone);
                    int floor = info != null ? info.Floor : 0;
                    float w;
                    byFloor.TryGetValue(floor, out w);
                    byFloor[floor] = w + RatedPower(a.Category);
                }
            }

            // ---- 甜甜圈 ----
            List<KeyValuePair<string, int>> list = new List<KeyValuePair<string, int>>(byType);
            list.Sort(delegate (KeyValuePair<string, int> x, KeyValuePair<string, int> y)
            {
                return y.Value.CompareTo(x.Value);
            });

            float cursor = 0f;
            for (int i = 0; i < StatsTypeMax; i++)
            {
                bool on = i < list.Count && total > 0;
                if (_donutSeg[i] != null && _donutSeg[i].gameObject.activeSelf != on)
                    _donutSeg[i].gameObject.SetActive(on);
                if (_donutName[i] != null && _donutName[i].gameObject.activeSelf != on)
                    _donutName[i].gameObject.SetActive(on);
                if (_donutPct[i] != null && _donutPct[i].gameObject.activeSelf != on)
                    _donutPct[i].gameObject.SetActive(on);
                if (!on) continue;

                float ratio = list[i].Value / (float)total;
                Image seg = _donutSeg[i];
                seg.color = DonutColor(i);
                seg.fillAmount = ratio;
                seg.rectTransform.localRotation = Quaternion.Euler(0f, 0f, -360f * cursor);
                cursor += ratio;

                SetText(_donutName[i], list[i].Key + " " + list[i].Value);
                SetText(_donutPct[i], (ratio * 100f).ToString("0.0") + "%");
            }

            if (_donutCenter != null)
                _donutCenter.text = total > 0 ? total.ToString() : "—";

            // ---- 楼层能耗 ----
            List<KeyValuePair<int, float>> floors = new List<KeyValuePair<int, float>>(byFloor);
            floors.Sort(delegate (KeyValuePair<int, float> x, KeyValuePair<int, float> y)
            {
                return x.Key.CompareTo(y.Key);   // 0（表外/室外）排最前
            });

            float maxW = 0.001f;
            for (int i = 0; i < floors.Count; i++) maxW = Mathf.Max(maxW, floors[i].Value);

            for (int i = 0; i < 4; i++)
            {
                bool on = i < floors.Count;
                if (_floorName[i] != null && _floorName[i].gameObject.activeSelf != on)
                {
                    _floorName[i].gameObject.SetActive(on);
                    _floorVal[i].gameObject.SetActive(on);
                    _floorBar[i].gameObject.SetActive(on);
                    // 轨道也必须一起开关：只关填充不关轨道的话，
                    // 没有数据的楼层会留一条空灰条（截图实测第 3/4 行就是这样）
                    if (_floorTrack[i] != null) _floorTrack[i].gameObject.SetActive(on);
                }
                if (!on) continue;

                int f = floors[i].Key;
                SetText(_floorName[i], f <= 0 ? "室外" : f + "F");

                float ratio = floors[i].Value / maxW;
                RectTransform br = _floorBar[i].rectTransform;
                br.sizeDelta = new Vector2(Mathf.Max(2f, 400f * ratio), 6f);

                SetText(_floorVal[i], "≈ " + (floors[i].Value / 1000f).ToString("0.0") + " kW");
            }

            if (_statsEmpty != null)
                _statsEmpty.text = total > 0
                    ? ""
                    : "无设备数据 —— 统计需要至少一台在册设备，接入数据源后自动出现。";
        }

        private static Color DonutColor(int i)
        {
            Color[] palette =
            {
                TwinTheme.Accent,
                new Color(0.20f, 0.80f, 0.35f, 1f),
                new Color(1.00f, 0.62f, 0.15f, 1f),
                new Color(0.62f, 0.45f, 0.95f, 1f),
                new Color(0.35f, 0.62f, 0.78f, 1f),
            };
            return palette[Mathf.Clamp(i, 0, palette.Length - 1)];
        }

        /// <summary>
        /// 类型额定功率（W）。只用于"量级估算"，不是实测值。
        /// 数值取常见型号中位数；改这里会直接改统计页的 ≈ 结果。
        /// </summary>
        private static float RatedPower(string category)
        {
            switch (category)
            {
                case "AC":
                case "Aircon": return 1500f;
                case "Light":
                case "Lamp": return 40f;
                case "PC": return 200f;
                case "Projector": return 300f;
                case "Heat":
                case "Radiator": return 1000f;
                case "Door": return 30f;
                case "Socket": return 100f;
                case "Meter": return 5f;
                case "Camera": return 12f;
                default: return 20f;
            }
        }
    }
}

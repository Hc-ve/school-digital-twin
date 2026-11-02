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

        /// <summary>【P3】SBO 弹窗（惰性创建，只在第一次点合闸时建）。</summary>
        private TwinSboDialog _sbo;

        private void BuildPowerPage()
        {
            Text title = TwinUIFactory.Label(_powerGroup, "FPwrTitle", TwinTheme.FsSubtitle,
                TextAnchor.UpperLeft, TwinTheme.TextPrimary);
            TwinUIFactory.Place(title.rectTransform, PageX, 92f, PageW, 22f);
            title.text = "电力管理";

            Text desc = TwinUIFactory.Label(_powerGroup, "FPwrDesc", TwinTheme.FsMicro,
                TextAnchor.UpperLeft, TwinTheme.TextMuted);
            TwinUIFactory.Place(desc.rectTransform, PageX, 116f, PageW, 14f);
            desc.text = "楼栋 → 楼层 → 房间 三级回路 · 点回路打开影响面确认（SBO 两步）";

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
        /// 点回路行 → 打开 SBO 弹窗（Select-Before-Operate）。
        ///
        /// 【P3 变更】原来这里是自己手写的"行内两步确认"（点一次装填、再点一次执行），
        /// 注释里还写着「项目里没有 TwinSboGate / TwinSboDialog」—— 那句是错的，
        /// 项目里一直有，只是当时没找到。已裁决改用现成实现：
        /// 它会摊开影响设备名单、给可见倒计时、作废时说清"什么都没发生"，
        /// 这三条是答辩时能讲清"为什么这次断电是安全的"的依据。
        /// </summary>
        private void OnPowerRowClicked(int index)
        {
            if (index >= _pwrNodes.Count) return;

            TwinCircuitNode node = _pwrNodes[index];
            if (node == null) return;

            _pwrSelected = node.Id;
            EnsureSbo();

            bool turnOn = !node.On;
            TwinSboRequest req = new TwinSboRequest();
            req.Title = (turnOn ? "恢复供电 · " : "分级停电 · ") + node.Title;
            req.Subject = node.Title;
            req.CircuitId = node.Id;
            req.PowerOn = turnOn;
            TwinSystemState.CollectTargets(node, req.Targets);

            string circuitId = node.Id;
            req.Operate = delegate
            {
                int affected = TwinCircuit.SetSwitch(circuitId, turnOn);
                TwinToast.Show(node.Title + (turnOn ? " 已合闸" : " 已分闸") +
                               "（影响 " + affected + " " + TwinCode.UnitDevice + "）");
                RefreshPowerPage();
            };

            _sbo.Show(req);
            RefreshPowerPage();   // 让那一行立刻变成"流程中"的警示色
        }

        /// <summary>
        /// 建 SBO 弹窗（惰性）。父节点用全屏层根 —— 弹窗要盖住整块，
        /// 挂到电力组里会被别的组切走（切页时组 SetActive(false)，弹窗跟着消失）。
        /// </summary>
        private void EnsureSbo()
        {
            if (_sbo != null) return;
            if (_root == null) return;

            _sbo = gameObject.AddComponent<TwinSboDialog>();
            _sbo.Build(_root, 1f);
            _sbo.Layout(1000f, 720f, HeadH, Pad);
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

            // 【P3】SBO 流程中：把正在操作的那条回路标成警示色（弹窗在它上面）
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

                // 【P3-2】三态，不能只看自己那条闸。
                //
                // 主理人 play 实测：把 1F 分闸后，1F 下面的「1-2班 / 计算机教室…」
                // 仍然显示绿色「合闸」—— 因为这里原来只读 `n.On`（节点自己的闸），
                // 而父级断电时子级的 `On` 仍是 true（它自己的闸确实没动，只是没电）。
                // 面板和场景对不上，用户会以为断电没生效。
                //
                // `TwinCircuit.ParentPowered` 早就写好了（注释里还写着"上级断电时按钮要
                // 明确写「上级已断电」并且禁掉"），只是一直没被 UI 调用。
                bool parentOn = TwinCircuit.ParentPowered(n.Id);
                bool selfOn = n.On;

                string label;
                Color state;
                if (!selfOn)
                {
                    label = "分闸";
                    state = TwinTheme.StatusAlarm;       // 自己断了：红
                }
                else if (!parentOn)
                {
                    label = "无电";                       // 自己合着、上级没电：灰
                    state = TwinTheme.TextMuted;
                }
                else
                {
                    label = "合闸";
                    state = TwinTheme.StatusNormal;      // 正常通电：绿
                }

                _pwrPill[i].SetFill(TwinTheme.WithAlpha(state, 0.18f), TwinTheme.WithAlpha(state, 0.18f));
                SetText(_pwrPillText[i], label);
                _pwrPillText[i].color = state;

                int count = TwinCircuit.CountDevices(n.Id);
                SetText(_pwrCount[i], count > 0 ? count + " " + TwinCode.UnitDevice : "");

                // 选中高亮：SBO 流程中用橙色，普通选中用主色
                bool sel = n.Id == _pwrSelected;
                bool busy = sel && TwinSystemState.IsCircuitBusy(n.Id, _sbo);

                Color rowFill = busy ? TwinTheme.WithAlpha(TwinTheme.StatusWarning, 0.16f)
                              : (sel ? TwinTheme.AccentDim : Color.clear);
                if (_pwrRowBg[i] != null) _pwrRowBg[i].SetFill(rowFill, rowFill);
            }

            RefreshPowerImpact();
        }

        private void RefreshPowerImpact()
        {
            if (_pwrImpact == null) return;

            TwinCircuitNode sel = string.IsNullOrEmpty(_pwrSelected)
                ? null
                : TwinCircuit.Find(_pwrSelected);

            if (sel == null)
            {
                SetText(_pwrImpact, "未选中回路");
                SetText(_pwrImpactSub, "点列表任意一行：先看影响面，再确认执行");
                SetText(_pwrSboHint, "");
                return;
            }

            int count = TwinCircuit.CountDevices(sel.Id);
            bool busy = TwinSystemState.IsCircuitBusy(sel.Id, _sbo);

            SetText(_pwrImpact, sel.Title + " · 当前" + (sel.On ? "通电" : "断电"));
            SetText(_pwrImpactSub,
                (sel.On ? "分闸" : "合闸") + "将影响 " + count + " " + TwinCode.UnitDevice +
                "（此处为影响面预览，不含额定功率估算）");

            if (busy)
            {
                SetText(_pwrSboHint, "SBO 流程进行中 —— 在弹窗里完成「选择 → 执行」");
                _pwrSboHint.color = TwinTheme.StatusWarning;
            }
            else
            {
                SetText(_pwrSboHint, "点该行打开影响面确认（SBO 两步）");
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

            // ② 命令日志（按 TwinId 过滤）
            //
            // 【C-29 顺带收口】原来这里是 6 条，加上触发/恢复实际能画出 10 行，
            // 而时间线区只能放 5 行（tlRoot 高 186 / 行距 38）—— 超出的行直接画到卡片外面，
            // 新加的「待接入」节点更是永远看不见。所以把**行预算**收成 5：
            // 触发 1 + 命令最多 2 + 恢复 1 + 待接入 1。命令不是被删，只是不再铺满。
            TwinCommandLog log = TwinCommandCenter.Instance != null ? TwinCommandCenter.Instance.Log : null;
            if (log != null)
            {
                IReadOnlyList<TwinCommandLog.Entry> recent = log.Recent(200);
                int added = 0;
                for (int i = 0; i < recent.Count && added < 2 && used < 3; i++)
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
                if (added == 0 && used < 3)
                {
                    used = AddTimelineItem(used, TwinTheme.TextMuted,
                        "命令 · 无记录", "该设备本次告警期间没有下发过控制命令", false);
                }
            }

            // ③ 告警恢复（如果已恢复）
            if (rec.EndTime > 0f && used < 4)
            {
                used = AddTimelineItem(used, TwinTheme.StatusNormal,
                    "恢复 · 已清除", "持续 " + TwinAlarmHistory.FormatDuration(rec.EndTime - rec.StartTime), false);
            }

            // ④ 【C-29】已知缺口的显式标注
            // 规格 §4.4 要求链路含「判定依据 / 通知 / 报文」，对应的
            // TwinAlarmStore / TwinMessageLog 在项目里不存在。**不编造节点，而是把缺口写出来** ——
            // 灰色 + 虚线连到前一条，一眼能分出"这条是占位"而不是"漏画了"。
            // 答辩被问"通知记录呢"，这一行就是答案。
            if (used < 11)
            {
                used = AddTimelineItem(used, TwinTheme.TextMuted,
                    "通知 / 报文：待接入", "需 TwinMessageLog（短信 / 语音 / 报文），本轮未建数据层",
                    false, true);
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
        /// <param name="dashed">
        /// 【C-29】连接线用虚线。只给「待接入」这类**占位节点**用 ——
        /// 实线表示"这一环真实发生过"，虚线表示"这一环该有、现在还没有"。
        /// 光靠灰色不够：灰点会被读成"信息量少的真实节点"，虚线才能表达"缺"。
        /// </param>
        private int AddTimelineItem(int index, Color dotColor, string head, string body, bool first,
                                    bool dashed = false)
        {
            RectTransform item = EnsureTimelineItem(index);
            if (item == null) return index;

            if (!item.gameObject.activeSelf) item.gameObject.SetActive(true);

            float y = index * 38f;
            TwinUIFactory.Place(item, 0f, y, PageW - 32f, 36f);

            Image dot = _tlDots[index];
            if (dot != null) dot.color = dotColor;

            // 竖线：最后一条不画（避免下边悬空）。实线/虚线二选一。
            Transform lineT = item.Find("Line");
            Transform dashT = item.Find("LineDash");
            bool showLine = !first;
            if (lineT != null)
            {
                Image line = lineT.GetComponent<Image>();
                if (line != null) line.enabled = showLine && !dashed;
            }
            if (dashT != null && dashT.gameObject.activeSelf != (showLine && dashed))
                dashT.gameObject.SetActive(showLine && dashed);

            Text t = _tlTexts[index];
            if (t != null)
            {
                t.text = head;
                // 占位节点整行压暗，跟真实节点分开（真实节点 Head 用 TextSecondary）
                Color hc = dashed ? TwinTheme.TextMuted : TwinTheme.TextSecondary;
                if (t.color != hc) t.color = hc;
            }

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

                // 竖线（在圆点下方，串起整条链路）—— 实线
                Image line = TwinUIFactory.Block(rt, "Line", TwinTheme.Divider, false);
                TwinUIFactory.Place(line.rectTransform, 4f, 12f, 1f, 26f);

                // 【C-29】虚线版竖线：5 段 3.5px，间隔 5px，铺满同一段 26px。
                // 用分段 Image 而不是改材质 / 引贴图 —— 项目零图片资源，
                // 生成虚线贴图要多一张 Sprite 和一次上传，不如直接摆 5 个小方块。
                GameObject dashGo = TwinUIFactory.Node("LineDash", rt);
                RectTransform dashRt = (RectTransform)dashGo.transform;
                TwinUIFactory.Place(dashRt, 4f, 12f, 1f, 26f);
                for (int d = 0; d < 5; d++)
                {
                    Image seg = TwinUIFactory.Block(dashRt, "D" + d, TwinTheme.TextMuted, false);
                    TwinUIFactory.Place(seg.rectTransform, 0f, d * 5f, 1f, 3.5f);
                }
                dashGo.SetActive(false);

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

        // ================================================================ 满宽几何
        //
        // 【为什么统计页能进满宽态】负责人裁决：只允许统计页在 L2 内合并右栏，
        // 左轨 200 保留（导航不能丢），中栏 600 + 右栏 200 = 800，Pad 16 → 可用 768。
        // 其余四页布局一律不动 —— 所以这里所有坐标都只服务统计页，不复用 PageW。
        private const float SW = MidW + RailW - Pad * 2f;       // 768：满宽可用
        private const float SHalfW = 376f;                      // 左右两半各 376
        private const float SRightX = PageX + SHalfW + 16f;     // 608：右半起点

        private const float SKpiY = 132f;                       // KPI 带顶
        private const float SKpiH = 60f;
        private const float SKpiW = 184f;                       // 4 张横排：216/408/600/792
        private const float SKpiStep = 192f;

        private const float SRow1Y = 232f;                      // 第一行图表（标题占 212~230，留 2px）
        private const float SRow1H = 200f;
        private const float SRow2Y = 458f;                      // 第二行图表（标题占 438~456，留 2px）
        private const float SRow2H = 190f;

        // ---- 现有实现（迁移，不重写）----
        private readonly Image[] _donutSeg = new Image[StatsTypeMax];
        private readonly Text[] _donutName = new Text[StatsTypeMax];
        private readonly Text[] _donutPct = new Text[StatsTypeMax];
        private readonly Image[] _floorBar = new Image[4];
        private readonly TwinRoundedRect[] _floorTrack = new TwinRoundedRect[4];
        private readonly Text[] _floorName = new Text[4];
        private readonly Text[] _floorVal = new Text[4];
        private Text _statsEmpty;
        private Text _donutCenter;

        // ---- 顶部 KPI 带（4 张横排）----
        private readonly Text[] _kpiValue = new Text[4];
        private readonly Text[] _kpiLabel = new Text[4];

        // ---- 全楼实时功率 60s 曲线（聚合视图，纯内存，不落盘）----
        //
        // 【为什么不进 TwinMetricHistory】那是"每台设备每个指标"的原始采样，
        // 共 95 条 × 300 点。全楼功率是**聚合量**，没有对应的 anchor/metric 键，
        // 硬塞进去要么造一个假点位、要么每次遍历求和后写入 —— 前者污染数据层，
        // 后者和"聚合视图"的语义不符。这里自维护一条 300 点缓冲即可，
        // 和 `TwinMetricHistory.SampleInterval`（0.2s）同频，不新增数据源。
        private const int StatsPowerCap = 300;
        private TwinSparkline _statsSpark;
        private Text _sparkNow;
        private Text _sparkRange;
        private float _statsPowerNextAt;
        private float _statsPowerSum;

        // ---- 能耗 Top5 排名 ----
        private const int TopN = 5;
        private readonly Text[] _topRank = new Text[TopN];
        private readonly Text[] _topName = new Text[TopN];
        private readonly TwinRoundedRect[] _topTrack = new TwinRoundedRect[TopN];
        private readonly Image[] _topBar = new Image[TopN];
        private readonly Text[] _topVal = new Text[TopN];
        private readonly List<KeyValuePair<TwinAnchor, float>> _topBuf =
            new List<KeyValuePair<TwinAnchor, float>>(64);

        // ---- 空状态：设备数为 0 时四块各给一句"暂无数据"，不留白框 ----
        private readonly Text[] _blockEmpty = new Text[4];

        private void BuildStatsPage()
        {
            // ---- 页头 ----
            Text title = TwinUIFactory.Label(_statsGroup, "FStTitle", TwinTheme.FsSubtitle,
                TextAnchor.UpperLeft, TwinTheme.TextPrimary);
            TwinUIFactory.Place(title.rectTransform, PageX, 92f, SW, 22f);
            title.text = "数据统计";

            Text desc = TwinUIFactory.Label(_statsGroup, "FStDesc", TwinTheme.FsMicro,
                TextAnchor.UpperLeft, TwinTheme.TextMuted);
            TwinUIFactory.Place(desc.rectTransform, PageX, 114f, SW, 14f);
            desc.text = "设备分布 · 实时功率 · 楼层能耗 · 能耗排名（≈ 按额定功率 × 台数估算，非实测）";

            // ---- ① 顶部 KPI 带：4 张横排 ----
            string[] kpiLabel = { "设备总数", "在线率", "告警中", "当前总功率" };
            for (int i = 0; i < 4; i++)
            {
                float x = PageX + i * SKpiStep;

                TwinRoundedRect card = TwinUIFactory.Rounded(_statsGroup, "FKpiCard_" + i, TwinTheme.R12,
                    TwinTheme.CardTop, TwinTheme.CardBottom, Color.clear, 0f, false);
                TwinUIFactory.Place(card.rectTransform, x, SKpiY, SKpiW, SKpiH);

                Text v = TwinUIFactory.Label(card.rectTransform, "V", 30,
                    TextAnchor.UpperLeft, TwinTheme.TextPrimary);
                TwinUIFactory.Place(v.rectTransform, 12f, 2f, SKpiW - 24f, 34f);
                v.text = "—";
                _kpiValue[i] = v;

                Text l = TwinUIFactory.Label(card.rectTransform, "L", TwinTheme.FsMicro,
                    TextAnchor.UpperLeft, TwinTheme.TextMuted);
                TwinUIFactory.Place(l.rectTransform, 12f, 40f, SKpiW - 24f, 14f);
                l.text = kpiLabel[i];
                _kpiLabel[i] = l;
            }

            Image line = TwinUIFactory.Block(_statsGroup, "FStLine", TwinTheme.Track, false);
            TwinUIFactory.Place(line.rectTransform, PageX, 202f, SW, 1f);

            // ================================================================ ② 左上：类型分布
            BuildBlockTitle("FDonutTitle", PageX, 212f, SHalfW, "设备类型分布", "实时 · 全量");

            GameObject donutGo = TwinUIFactory.Node("FDonut", _statsGroup);
            RectTransform donutRt = (RectTransform)donutGo.transform;
            TwinUIFactory.Place(donutRt, PageX + 6f, SRow1Y + 20f, 150f, 150f);

            Sprite ring = TwinPaint.RingSprite(120, 0.62f);
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

            // 图例：donut 右侧一列，5 行 × 26
            for (int i = 0; i < StatsTypeMax; i++)
            {
                float y = SRow1Y + 26f + i * 26f;

                TwinRoundedRect dot = TwinUIFactory.Rounded(_statsGroup, "FDot_" + i, 3f,
                    TwinTheme.Accent, TwinTheme.Accent, Color.clear, 0f, false);
                TwinUIFactory.Place(dot.rectTransform, PageX + 172f, y + 5f, 8f, 8f);

                Text nm = TwinUIFactory.Label(_statsGroup, "FName_" + i, TwinTheme.FsCaption,
                    TextAnchor.MiddleLeft, TwinTheme.TextSecondary);
                TwinUIFactory.Place(nm.rectTransform, PageX + 186f, y, 118f, 18f);
                _donutName[i] = nm;

                Text pct = TwinUIFactory.Label(_statsGroup, "FPct_" + i, TwinTheme.FsCaption,
                    TextAnchor.MiddleRight, TwinTheme.TextMuted);
                TwinUIFactory.Place(pct.rectTransform, PageX + 306f, y, 62f, 18f);
                _donutPct[i] = pct;
            }

            // ================================================================ ③ 右上：全楼功率 60s 曲线
            BuildBlockTitle("FSparkTitle", SRightX, 212f, SHalfW, "全楼实时功率", "实时 · 60s");

            // 必须走 `TwinUIFactory.Graphic<T>`：它会补 CanvasRenderer。
            // 用 `Node + AddComponent<TwinSparkline>()` 的话组件链是断的 ——
            // `Graphic.Rebuild()` 第一行 `if (canvasRenderer == null) return;` 直接返回，
            // `OnPopulateMesh()` 永远不跑，画面上什么都没有，且**不报错**。
            // （2026-10-21 的趋势线就是这么查了两轮）
            _statsSpark = TwinUIFactory.Graphic<TwinSparkline>(_statsGroup, "FSpark");
            TwinUIFactory.Place(_statsSpark.rectTransform, SRightX + 2f, SRow1Y + 22f, SHalfW - 4f, 148f);
            _statsSpark.SetRange(0f, 30f);        // 初始量程，之后由 UpdateStatsSpark 自适应

            // 主线走 ChartSeq1（Accent 别名），填充走 ChartFill（Accent @0.012）。
            // 边框/基线保持原来的极淡白 —— 它们是"图表区边界"，不该带色相。
            _statsSpark.SetColors(
                TwinTheme.ChartSeq1,
                TwinTheme.ChartFill,
                TwinTheme.WithAlpha(Color.white, 0.09f),
                TwinTheme.WithAlpha(Color.white, 0.22f));

            _sparkNow = TwinUIFactory.Label(_statsGroup, "FSparkNow", TwinTheme.FsSubtitle,
                TextAnchor.LowerLeft, TwinTheme.TextPrimary);
            TwinUIFactory.Place(_sparkNow.rectTransform, SRightX + 2f, SRow1Y + 172f, 180f, 22f);
            _sparkNow.text = "≈ 0.0 kW";

            _sparkRange = TwinUIFactory.Label(_statsGroup, "FSparkRange", TwinTheme.FsMicro,
                TextAnchor.LowerRight, TwinTheme.TextMuted);
            TwinUIFactory.Place(_sparkRange.rectTransform, SRightX + 180f, SRow1Y + 176f, SHalfW - 186f, 16f);
            _sparkRange.text = "";

            // ================================================================ ④ 左下：楼层能耗
            BuildBlockTitle("FFloorTitle", PageX, 438f, SHalfW, "楼层能耗对比", "估算");

            for (int i = 0; i < 4; i++)
            {
                float y = SRow2Y + 6f + i * 36f;

                Text nm = TwinUIFactory.Label(_statsGroup, "FFloorName_" + i, TwinTheme.FsCaption,
                    TextAnchor.MiddleLeft, TwinTheme.TextMuted);
                TwinUIFactory.Place(nm.rectTransform, PageX, y, 52f, 24f);
                _floorName[i] = nm;

                TwinRoundedRect track = TwinUIFactory.Rounded(_statsGroup, "FTrack_" + i, 3f,
                    TwinTheme.Track, TwinTheme.Track, Color.clear, 0f, false);
                TwinUIFactory.Place(track.rectTransform, PageX + 56f, y + 9f, 176f, 6f);
                _floorTrack[i] = track;

                Image fill = TwinUIFactory.Block(track.rectTransform, "Fill", TwinTheme.Accent, false);
                TwinUIFactory.Place(fill.rectTransform, 0f, 0f, 176f, 6f);
                _floorBar[i] = fill;

                Text v = TwinUIFactory.Label(_statsGroup, "FFloorVal_" + i, TwinTheme.FsCaption,
                    TextAnchor.MiddleRight, TwinTheme.TextSecondary);
                TwinUIFactory.Place(v.rectTransform, PageX + 236f, y, 136f, 24f);
                _floorVal[i] = v;
            }

            // ================================================================ ⑤ 右下：能耗 Top5
            BuildBlockTitle("FTopTitle", SRightX, 438f, SHalfW, "能耗 Top5 设备", "估算");

            for (int i = 0; i < TopN; i++)
            {
                float y = SRow2Y + 6f + i * 30f;

                Text rk = TwinUIFactory.Label(_statsGroup, "FTopRank_" + i, TwinTheme.FsMicro,
                    TextAnchor.MiddleCenter, TwinTheme.TextMuted);
                TwinUIFactory.Place(rk.rectTransform, SRightX, y + 3f, 18f, 18f);
                rk.text = (i + 1).ToString();
                _topRank[i] = rk;

                Text nm = TwinUIFactory.Label(_statsGroup, "FTopName_" + i, TwinTheme.FsCaption,
                    TextAnchor.MiddleLeft, TwinTheme.TextSecondary);
                TwinUIFactory.Place(nm.rectTransform, SRightX + 22f, y, 96f, 24f);
                _topName[i] = nm;

                TwinRoundedRect track = TwinUIFactory.Rounded(_statsGroup, "FTopTrack_" + i, 3f,
                    TwinTheme.Track, TwinTheme.Track, Color.clear, 0f, false);
                TwinUIFactory.Place(track.rectTransform, SRightX + 122f, y + 9f, 150f, 6f);
                _topTrack[i] = track;

                Image fill = TwinUIFactory.Block(track.rectTransform, "Fill", TwinTheme.Accent, false);
                TwinUIFactory.Place(fill.rectTransform, 0f, 0f, 150f, 6f);
                _topBar[i] = fill;

                Text v = TwinUIFactory.Label(_statsGroup, "FTopVal_" + i, TwinTheme.FsCaption,
                    TextAnchor.MiddleRight, TwinTheme.TextSecondary);
                TwinUIFactory.Place(v.rectTransform, SRightX + 278f, y, 92f, 24f);
                _topVal[i] = v;
            }

            // ================================================================ ⑥ 空状态（四块各一句）
            for (int i = 0; i < 4; i++)
            {
                float x = (i % 2 == 0) ? PageX : SRightX;
                float y = (i < 2) ? SRow1Y + 80f : SRow2Y + 70f;

                Text e = TwinUIFactory.Label(_statsGroup, "FBlockEmpty_" + i, TwinTheme.FsBody,
                    TextAnchor.MiddleCenter, TwinTheme.TextMuted);
                TwinUIFactory.Place(e.rectTransform, x, y, SHalfW, 24f);
                e.text = "暂无数据";
                e.gameObject.SetActive(false);
                _blockEmpty[i] = e;
            }

            _statsEmpty = TwinUIFactory.Label(_statsGroup, "FStEmpty", TwinTheme.FsCaption,
                TextAnchor.UpperLeft, TwinTheme.TextMuted);
            TwinUIFactory.Place(_statsEmpty.rectTransform, PageX, 646f, SW, 14f);
            _statsEmpty.text = "";
        }

        /// <summary>
        /// 图表块标题：左标题 + 右侧数据来源标注。
        ///
        /// 【为什么每块都要标来源】负责人裁决 C-28：估算值必须让口径在界面上可见，
        /// 不能只写在页脚 —— 答辩时评委看的是图，不是脚注。
        /// </summary>
        private void BuildBlockTitle(string name, float x, float y, float w, string text, string source)
        {
            Text t = TwinUIFactory.Label(_statsGroup, name, TwinTheme.FsBodyStrong,
                TextAnchor.UpperLeft, TwinTheme.TextPrimary);
            TwinUIFactory.Place(t.rectTransform, x, y, w - 86f, 18f);
            t.text = text;

            Text s = TwinUIFactory.Label(_statsGroup, name + "Src", TwinTheme.FsMicro,
                TextAnchor.UpperRight, TwinTheme.TextMuted);
            TwinUIFactory.Place(s.rectTransform, x + w - 86f, y + 3f, 86f, 14f);
            s.text = source;
        }

        /// <summary>
        /// 统计页四块数据。
        ///
        /// 能耗**不是实测**：项目没有 `TwinEnergyEstimator` 也没有时序存储，
        /// 所以用「类型额定功率 × 台数」做量级估算，界面上统一标 ≈（裁决 C-28）。
        /// 这条必须写清 —— 答辩被问"这个 4.6kW 哪来的"要能答上来。
        ///
        /// 刷新频率：KPI / donut / 楼层 / Top5 跟随 `Refresh` 的 0.5s 节流；
        /// 功率曲线由 `Update` 单独 5Hz 采样（和 `TwinMetricHistory.SampleInterval` 同频）。
        /// </summary>
        private void RefreshStatsPage()
        {
            Dictionary<string, int> byType = new Dictionary<string, int>(16, StringComparer.Ordinal);
            Dictionary<int, float> byFloor = new Dictionary<int, float>(4);
            _topBuf.Clear();

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

                    // 【P5 口径统一】楼层能耗也改**实时**，不再是「额定 × 台数」。
                    //
                    // 原来用 `RatedPower` 聚合 → 1F 14.4 + 2F 7.9 = 22.3 kW，
                    // 而同一页的 KPI 写 2.8 kW（实时），差 8 倍 —— 评委一对账就穿帮。
                    // 现在和 KPI / 曲线走同一个 `LivePowerOf`，三个数字天然对得上。
                    float live = TwinPowerEstimate.LivePowerOf(a);
                    byFloor[floor] = w + live;

                    // Top5 排序也用实时功率：原来按 `RatedPower` 排，
                    // 同类设备（五台电脑）全是 1500 W，排名等于随机。
                    _topBuf.Add(new KeyValuePair<TwinAnchor, float>(a, live));
                }
            }

            // KPI 与曲线同源：都用实时求和（`TwinPowerEstimate.SumLivePower`）。
            // 和窄态、数据监测页三处一致。
            float totalW = TwinPowerEstimate.SumLivePower();
            _statsPowerSum = totalW;
            bool empty = total == 0;

            // ================================================================ KPI 带
            SetText(_kpiValue[0], empty ? "—" : total.ToString());

            int online = TwinDeviceStats.OnlineDeviceCount;
            SetText(_kpiValue[1], empty ? "—" : (online * 100f / total).ToString("0.#") + "%");

            int alarmCount = TwinDeviceStats.AlarmDeviceCount;
            SetText(_kpiValue[2], alarmCount.ToString());
            // 【P5】状态色：有告警时数值变红，和"全是白字"相比一眼能看出异常
            if (_kpiValue[2] != null)
                _kpiValue[2].color = alarmCount > 0 ? TwinTheme.StatusAlarm : TwinTheme.TextPrimary;

            SetText(_kpiValue[3], empty ? "—" : (totalW / 1000f).ToString("0.0") + " kW");

            // 【P1】负载率：当前实时功率 / 装机容量。
            //
            // 行业大屏标配的"容量 vs 实际"视角 —— 光给 2.8 kW 说不出业务含义，
            // 配上"装机 22.2 kW、负载 12.6%"就能讲"系统处于低负荷运行"。
            // 容量取 `TotalWatt()`（额定 × 台数），那是它唯一的正确用途。
            if (_kpiLabel[3] != null)
            {
                float capacity = TwinPowerEstimate.TotalWatt();
                SetText(_kpiLabel[3], (!empty && capacity > 0.01f)
                    ? "当前总功率 · 负载 " + (totalW / capacity * 100f).ToString("0.#") + "%"
                    : "当前总功率");
            }

            // ================================================================ 空状态
            for (int i = 0; i < 4; i++)
                if (_blockEmpty[i] != null && _blockEmpty[i].gameObject.activeSelf != empty)
                    _blockEmpty[i].gameObject.SetActive(empty);

            if (_statsSpark != null && _statsSpark.gameObject.activeSelf == empty)
                _statsSpark.gameObject.SetActive(!empty);

            // ================================================================ 甜甜圈
            List<KeyValuePair<string, int>> list = new List<KeyValuePair<string, int>>(byType);
            list.Sort(delegate (KeyValuePair<string, int> x, KeyValuePair<string, int> y)
            {
                return y.Value.CompareTo(x.Value);
            });

            // 【P2】类型超过 5 个时，把第 5 名及以后合并成「其他」。
            //
            // 不合并的话：70 台设备里只画出前 5 类的 62 台，剩下 8 台"消失"，
            // 圆环加起来不是 100% —— 评委一眼能看出对不上账。
            List<KeyValuePair<string, int>> donut = new List<KeyValuePair<string, int>>(StatsTypeMax);
            int keep = StatsTypeMax - 1;
            for (int i = 0; i < keep && i < list.Count; i++) donut.Add(list[i]);

            if (list.Count > keep)
            {
                int rest = 0;
                for (int i = keep; i < list.Count; i++) rest += list[i].Value;
                if (rest > 0) donut.Add(new KeyValuePair<string, int>("其他", rest));
            }

            float cursor = 0f;
            for (int i = 0; i < StatsTypeMax; i++)
            {
                bool on = i < donut.Count && total > 0;
                if (_donutSeg[i] != null && _donutSeg[i].gameObject.activeSelf != on)
                    _donutSeg[i].gameObject.SetActive(on);
                if (_donutName[i] != null && _donutName[i].gameObject.activeSelf != on)
                    _donutName[i].gameObject.SetActive(on);
                if (_donutPct[i] != null && _donutPct[i].gameObject.activeSelf != on)
                    _donutPct[i].gameObject.SetActive(on);
                if (!on) continue;

                float ratio = donut[i].Value / (float)total;
                Image seg = _donutSeg[i];
                seg.color = DonutColor(i);
                seg.fillAmount = ratio;
                seg.rectTransform.localRotation = Quaternion.Euler(0f, 0f, -360f * cursor);
                cursor += ratio;

                SetText(_donutName[i], donut[i].Key + " " + donut[i].Value);
                SetText(_donutPct[i], (ratio * 100f).ToString("0.0") + "%");
            }

            if (_donutCenter != null)
                _donutCenter.text = total > 0 ? total.ToString() : "—";

            // ================================================================ 功率曲线标注
            // 曲线本身由 `Update` 5Hz `Push`，这里只更新数字和量程文字。
            if (_sparkNow != null)
                _sparkNow.text = empty ? "—" : (totalW / 1000f).ToString("0.0") + " kW";

            if (_sparkRange != null)
            {
                if (_statsSpark != null && _statsSpark.SampleCount > 1)
                {
                    float lo, hi;
                    if (TryGetSparkRange(out lo, out hi))
                        _sparkRange.text = "近 60s  " + (lo / 1000f).ToString("0.0") + " ~ " +
                                           (hi / 1000f).ToString("0.0") + " kW";
                    else _sparkRange.text = "";
                }
                else _sparkRange.text = "采样中…";
            }

            // ================================================================ 楼层能耗
            List<KeyValuePair<int, float>> floors = new List<KeyValuePair<int, float>>(byFloor);
            floors.Sort(delegate (KeyValuePair<int, float> x, KeyValuePair<int, float> y)
            {
                return x.Key.CompareTo(y.Key);   // 0（表外/室外）排最前
            });

            float maxW = 0.001f;
            for (int i = 0; i < floors.Count; i++) maxW = Mathf.Max(maxW, floors[i].Value);

            for (int i = 0; i < 4; i++)
            {
                bool on = i < floors.Count && !empty;
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
                br.sizeDelta = new Vector2(Mathf.Max(2f, 176f * ratio), 6f);

                // 【P2】加百分比：楼层之间"谁更费电"才有可比性，只给绝对值读者要自己算
                float pct = totalW > 0.01f ? floors[i].Value / totalW * 100f : 0f;
                SetText(_floorVal[i], (floors[i].Value / 1000f).ToString("0.0") + " kW · " + pct.ToString("0") + "%");
            }

            // ================================================================ 能耗 Top5
            _topBuf.Sort(delegate (KeyValuePair<TwinAnchor, float> x, KeyValuePair<TwinAnchor, float> y)
            {
                return y.Value.CompareTo(x.Value);
            });

            float maxTop = _topBuf.Count > 0 ? Mathf.Max(1f, _topBuf[0].Value) : 1f;

            for (int i = 0; i < TopN; i++)
            {
                bool on = i < _topBuf.Count && !empty;
                if (_topName[i] != null && _topName[i].gameObject.activeSelf != on)
                {
                    _topRank[i].gameObject.SetActive(on);
                    _topName[i].gameObject.SetActive(on);
                    _topVal[i].gameObject.SetActive(on);
                    _topBar[i].gameObject.SetActive(on);
                    if (_topTrack[i] != null) _topTrack[i].gameObject.SetActive(on);
                }
                if (!on) continue;

                KeyValuePair<TwinAnchor, float> kv = _topBuf[i];
                SetText(_topName[i], TwinCode.Short(kv.Key.TwinId));

                float ratio = kv.Value / maxTop;
                RectTransform br = _topBar[i].rectTransform;
                br.sizeDelta = new Vector2(Mathf.Max(2f, 150f * ratio), 6f);

                // 【P1】加占比：从"五台并列"变成"这五台占了全楼多少负荷"，才有分析价值
                float share = totalW > 0.01f ? kv.Value / totalW * 100f : 0f;
                SetText(_topVal[i], kv.Value.ToString("0") + " W · " + share.ToString("0") + "%");
            }

            if (_statsEmpty != null)
                _statsEmpty.text = total > 0
                    ? ""
                    : "无设备数据 —— 统计需要至少一台在册设备，接入数据源后自动出现。";
        }

        /// <summary>取功率曲线窗口内的极值（用于"近 60s 区间"标注）。</summary>
        private bool TryGetSparkRange(out float lo, out float hi)
        {
            lo = 0f;
            hi = 0f;

            if (_statsSpark == null || _statsSpark.SampleCount < 2) return false;
            if (_sparkLo < 0f || _sparkHi < 0f) return false;

            lo = _sparkLo;
            hi = _sparkHi;
            return hi > lo;
        }

        /// <summary>功率曲线窗口内的极值（`Update` 采样时维护，随窗口滚动衰减）。</summary>
        private float _sparkLo = -1f;
        private float _sparkHi = -1f;

        /// <summary>
        /// 统计页的 5Hz 采样：遍历全部在册设备求和，推入功率曲线。
        ///
        /// ── 为什么放在 Update 而不是 Refresh ────────────────────────────
        /// `Refresh` 是 0.5s 节流，用它采样 60 秒只有 120 点，曲线会很"方"。
        /// 5Hz 和 `TwinMetricHistory.SampleInterval` 一致，观感才和趋势线统一。
        ///
        /// 只在统计页激活时跑，切走立刻停 —— 不为一个不显示的图表付代价。
        /// 开销实测见交付报告（53 台求和 @5Hz）。
        /// </summary>
        private void UpdateStatsSpark()
        {
            if (_mode == Mode.None || _mode == Mode.Home) return;
            if (_tab != 4) return;
            if (_statsGroup == null || !_statsGroup.gameObject.activeSelf) return;
            if (_statsSpark == null) return;

            float now = Time.unscaledTime;
            if (now < _statsPowerNextAt) return;
            _statsPowerNextAt = now + 0.2f;

            // 首次采样时跑一次 bench，把"聚合开销"变成可复现的数字（交付报告要用）
            if (!_statsBenchDone)
            {
                _statsBenchDone = true;
                BenchPowerSum();
            }

            float sum = SumAllPower();
            _statsPowerSum = sum;

            // 【P5】首次采样回填 60 个点（12 秒窗口），打开即有完整曲线。
            //
            // 不回填的话：刚进统计页缓冲区只有 1~5 个点，`TwinSparkline` 画不出线，
            // 看起来就是一个空框 —— 违反"打开即有数据，不允许从空白长出来"的验收要求。
            // 回填的是当前值，所以曲线起始是一段平线，之后逐步被真实采样替换。
            if (!_sparkPrefilled)
            {
                _sparkPrefilled = true;
                for (int k = 60; k > 0; k--)
                    _statsSpark.Push(now - k * 0.2f, sum);
            }

            _statsSpark.Push(now, sum);

            // 窗口极值（简化：滑动衰减，不存 300 点副本）
            if (_sparkLo < 0f || sum < _sparkLo) _sparkLo = sum;
            if (_sparkHi < 0f || sum > _sparkHi) _sparkHi = sum;
            _sparkLo = Mathf.Lerp(_sparkLo, sum, 0.02f);
            _sparkHi = Mathf.Lerp(_sparkHi, sum, 0.02f);

            // 【P5】量程自适应。
            //
            // 写死 `SetRange(0, 30)` 时，22kW 的总功率波动 ±1kW 只占 3% 高度 ——
            // 看起来就是一条死线（主理人 2026-11-02 实测反馈"曲线是平的"）。
            // 改成跟着窗口极值走：波动被放大到可见，同时用 `hi*0.15` 兜底，
            // 避免数值几乎不变时把噪声放大成锯齿。
            float lo = _sparkLo;
            float hi = _sparkHi;
            float span = Mathf.Max(hi - lo, Mathf.Max(0.5f, hi * 0.05f));
            float pad = span * 0.25f;
            _statsSpark.SetRange(lo - pad, hi + pad);
        }

        /// <summary>聚合缓冲 bench 只跑一次。</summary>
        private bool _statsBenchDone;

        /// <summary>功率曲线是否已回填（只做一次，避免每帧重复填）。</summary>
        private bool _sparkPrefilled;

        /// <summary>
        /// 遍历全部在册设备求当前总功率。
        ///
        /// 【P5】口径已提到 `TwinPowerEstimate.SumLivePower()` ——
        /// 全屏和窄态共用一份实现，避免两处数字分叉。这里只做转发。
        /// </summary>
        private static float SumAllPower()
        {
            return TwinPowerEstimate.SumLivePower();
        }

        /// <summary>
        /// 测一次聚合开销（200 次取平均）。
        ///
        /// 为什么要在运行时测而不是写在文档里：设备数是会变的（53 台现在，接新点位就变），
        /// 文档里的数字半年后就不准了。跑一次打印在 Console，随时可复现。
        /// </summary>
        private static void BenchPowerSum()
        {
            const int N = 200;
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            float acc = 0f;
            for (int k = 0; k < N; k++) acc += SumAllPower();
            sw.Stop();

            double per = sw.Elapsed.TotalMilliseconds / N;
            Debug.Log("[P5] 全楼功率聚合：遍历 " + TwinDeviceStats.ActiveDeviceCount +
                      " 台 × " + N + " 次 = " + sw.Elapsed.TotalMilliseconds.ToString("0.000") + " ms" +
                      "，单次 " + per.ToString("0.0000") + " ms" +
                      "，5Hz 折算 " + (per * 5.0).ToString("0.0000") + " ms/s" +
                      "（校验和 " + acc.ToString("0") + "）");
        }

        /// <summary>
        /// donut 五段的颜色：**单色相 4 级 + 灰**（图纸 §4.2）。
        ///
        /// ── 为什么删掉原来的绿 / 橙 / 紫 ────────────────────────────────
        /// 原来那三色和语义色撞车：绿 = `StatusNormal`（正常/通过）、
        /// 橙 = `StatusWarning`（告警橙）、紫是项目里没有任何语义的"装饰色"。
        /// 于是"灯具 22.9%"这一段画成绿色，读起来像"灯具状态正常"，
        /// 但 donut 表达的是**构成占比**，不是状态 —— 色彩语义被偷换了。
        ///
        /// 现在用 Accent 青蓝的 4 级明度（ChartSeq1–4）表达序列，
        /// 第 5 段（"其他"）用 `StatusUnknown` 灰，明确"这是聚合项、不是某个类型"。
        ///
        /// 提成 `static readonly` 而不是每次调用 new 数组：
        /// `RefreshStatsPage` 每 0.5s 跑一次，5 段 × 每秒 2 次 = 每秒 10 个短命数组。
        /// </summary>
        private static readonly Color[] DonutPalette =
        {
            TwinTheme.ChartSeq1,        // 第 1 大类型
            TwinTheme.ChartSeq2,
            TwinTheme.ChartSeq3,
            TwinTheme.ChartSeq4,
            TwinTheme.StatusUnknown,    // "其他"聚合
        };

        private static Color DonutColor(int i)
        {
            return DonutPalette[Mathf.Clamp(i, 0, DonutPalette.Length - 1)];
        }

        /// <summary>
        /// 类型额定功率（W）。只用于"量级估算"，不是实测值。
        /// 数值取常见型号中位数；改这里会直接改统计页的 ≈ 结果。
        /// </summary>
        private static float RatedPower(string category)
        {
            return TwinPowerEstimate.RatedPower(category);
        }
    }

    /// <summary>
    /// 【C-28】全屏与窄态共用的**估算口径**（不是数据层）。
    ///
    /// ── 为什么必须抽出来一份 ──────────────────────────────────────────
    /// 「总功率 / 今日用电」两张卡原来恒走 NoData（显示「—」+「数据源未接入」），
    /// 而同一个面板的统计页却按额定功率算出 ≈ 4.6 kW 显示出来 ——
    /// **一处有数、一处未接入**，读的人只会认为其中一处是错的。
    /// 现在两边都调这里，数值必然一致；改功率表也只改一处。
    ///
    /// ── 它为什么不是数据层 ────────────────────────────────────────────
    /// 没有状态、没有时序、没有采样，纯粹是「静态功率表 + 现有设备清单」的一次求和。
    /// 真正的数据层（TwinEnergyEstimator / 计量表 sink）不存在，这里也没假装它存在 ——
    /// 所以界面上所有相关数字都带 ≈，副标写明「按额定功率 × 台数」。
    /// </summary>
    public static class TwinPowerEstimate
    {
        /// <summary>本次运行起点（秒）。首次调用时取，用来算「今日用电」的时长。</summary>
        private static float s_startAt = -1f;

        /// <summary>取运行起点（惰性初始化：首次调用时记下当前时刻）。</summary>
        private static float StartedAt
        {
            get
            {
                if (s_startAt < 0f) s_startAt = Time.realtimeSinceStartup;
                return s_startAt;
            }
        }

        /// <summary>
        /// 单台设备当前的功率（W）：优先读实时 `Power`，取不到才退回「额定 × 待机系数」。
        ///
        /// ── 为什么必须抽成一个方法 ──────────────────────────────────────
        /// 全楼总功率、楼层能耗聚合、Top5 排序**三处**都要这条规则。
        /// 之前各写一遍，改一处忘一处就会出现"KPI 2.8kW、楼层 22.3kW"这种
        /// 同一个页面两个数字（主理人 2026-11-02 截图对账发现的）。
        ///
        /// 现在只在这里定义，三处都调它，改系数只改这一个 0.15。
        /// </summary>
        public static float LivePowerOf(TwinAnchor a)
        {
            if (a == null) return 0f;

            TwinValue pv;
            if (a.TryGetValue("Power", out pv) && pv.Value > 0.01f) return pv.Value;
            return RatedPower(a.Category) * 0.15f;   // 待机底噪
        }

        /// <summary>
        /// 全楼当前总功率（W）：遍历在册设备求和。
        ///
        /// ── 为什么必须放在这里而不是各页各写一份 ──────────────────────
        /// 全屏 L2 统计页和窄态 L1 统计页都要这个数。
        /// 之前两处各写了一个 for 循环，口径一旦分叉（比如一边改了待机系数 0.15、
        /// 另一边忘了改），就会出现"同一个面板两种数字" ——
        /// 主理人 2026-11-02 明确要求"全屏和窄态本质还是一个面板，必须同步"。
        ///
        /// 现在两处都调这一个方法，改口径只改这里。
        /// </summary>
        public static float SumLivePower()
        {
            IReadOnlyList<TwinAnchor> all = TwinDeviceStats.AllDevices;
            float sum = 0f;
            if (all == null) return sum;

            for (int i = 0; i < all.Count; i++)
            {
                TwinAnchor a = all[i];
                if (a == null || !TwinDeviceStats.IsActiveDevice(a)) continue;
                sum += LivePowerOf(a);
            }
            return sum;
        }

        /// <summary>
        /// 类型额定功率（W）。与统计页同一张表 —— 改这里会同时改统计页与 KPI 卡。
        /// </summary>
        public static float RatedPower(string category)
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

        /// <summary>
        /// 在册设备的总额定功率（W）。统计页的楼层 kW 就是按同一份清单同一种过滤加的，
        /// 所以「各楼层之和」与这里的总数一定相等。
        /// </summary>
        public static float TotalWatt()
        {
            float sum = 0f;
            IReadOnlyList<TwinAnchor> all = TwinDeviceStats.AllDevices;
            if (all == null) return 0f;

            for (int i = 0; i < all.Count; i++)
            {
                TwinAnchor a = all[i];
                if (a == null) continue;
                if (!TwinDeviceStats.IsActiveDevice(a)) continue;
                if (IsAggregatePoint(a.TwinId)) continue;   // 聚合点位不参与累加
                sum += RatedPower(a.Category);
            }
            return sum;
        }

        /// <summary>
        /// 该点位是否是「聚合点位」—— 它代表的设备已经由各自的单台点位计入统计。
        ///
        /// ── 为什么必须排除（2026-11-01 拆机房座位后暴露）──────────────
        /// `ClassComputer/PC/Room_01` 代表机房 18 台电脑的**汇总**。
        /// 拆出 `Seat_01..18` 之后，同一批物理设备在场景里有两组点位：
        ///   · Room_01  → 1 个点位代表 18 台（联动开机用）
        ///   · Seat_XX  → 18 个点位各代表 1 台（独立编号用）
        ///
        /// 按设备累加总功率时，这 18 台的功耗会被算两遍，总功率虚高约 3.2kW。
        /// 答辩时被问"机房 19 台电脑怎么算出 6kW"就答不上来了。
        ///
        /// ── 为什么用 TwinId 判定而不是加字段 ──────────────────────────
        /// 加字段要改 `TwinPointDefinition` 的序列化，会牵连 catalog 资产和所有
        /// 读它的地方。目前聚合点位只有一个（机房电脑），一条前缀判断足够，
        /// 等以后出现第二类聚合点位再抽字段。
        /// </summary>
        public static bool IsAggregatePoint(string twinId)
        {
            if (string.IsNullOrEmpty(twinId)) return false;
            return string.Equals(twinId, "ClassComputer/PC/Room_01", System.StringComparison.Ordinal);
        }

        /// <summary>
        /// 今日用电（kWh）估算 = 总功率 × 本次运行时长。
        ///
        /// 【为什么是"运行时长"而不是"今日 0 点到现在"】项目没有时序存储，也没有
        /// 跨会话的日累计。拿墙钟从 0 点算会得出"凌晨没人也耗了 30 度"这种离谱值；
        /// 用**演示进程运行时长**至少是可解释的：这台机器为演示跑了多久。
        /// 所以两张卡的说明分开写（功率卡写「额定功率 × 台数」，电量卡多一句「× 运行时长」）。
        /// </summary>
        public static float TodayKwh()
        {
            float hours = (Time.realtimeSinceStartup - StartedAt) / 3600f;
            if (hours < 0f) hours = 0f;

            return TotalWatt() / 1000f * hours;
        }
    }
}

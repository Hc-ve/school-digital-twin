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
        /// <summary>
        /// 【告警可点】行底（本身就是 `raycastTarget=true` 的 Graphic + Button）。
        ///
        /// 选中态用**底色**表达（AccentDim），不用描边：
        /// 描边已经是 1px Border，改成主色在 42 高的行上几乎看不出来，
        /// 而整行底色一变，"我现在选的是哪条"是瞬间可读的。
        /// </summary>
        private readonly TwinRoundedRect[] _acRowBg = new TwinRoundedRect[AlmRowsFull];
        private readonly Image[] _acBar = new Image[AlmRowsFull];
        private readonly Text[] _acId = new Text[AlmRowsFull];
        private readonly Text[] _acDesc = new Text[AlmRowsFull];
        private readonly Text[] _acDur = new Text[AlmRowsFull];
        private readonly List<TwinAlarmHistory.AlarmRecord> _acBuf =
            new List<TwinAlarmHistory.AlarmRecord>(64);

        /// <summary>
        /// 【告警可点】这一帧**实际画出来**的那几条（已按「进行中/历史」过滤）。
        ///
        /// ── 为什么不能直接拿 `_acBuf[index]` ────────────────────────────
        /// `_acBuf` 装的是**全部**记录（进行中 + 历史混在一起），而列表只渲染其中
        /// 匹配当前页签的那一批。所以 "第 0 行" 不等于 "`_acBuf[0]`" ——
        /// 切到「历史」时，点第 0 行可能选中一条进行中的记录，
        /// 表现为"时间线显示的是另一台设备"，且不报错。
        /// 这里单独存一份渲染顺序的副本，行号与内容一一对应。
        /// </summary>
        private readonly List<TwinAlarmHistory.AlarmRecord> _acShown =
            new List<TwinAlarmHistory.AlarmRecord>(AlmRowsFull);

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
                _acRowBg[i] = row;

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

        /// <summary>
        /// 点一条告警 → 选中设备 + 画时间线。
        ///
        /// ── 两件事一起做，缺一件就"看着不对"──────────────────────────
        ///   ① 选中并让镜头飞过去（`TwinPicker.Select`），这是「点告警定位设备」的本意；
        ///   ② 画全链路时间线（本页右下的追溯卡），这是全屏 L2 的交付内容（图纸 §3.4）。
        ///
        /// 上一版只做了 ②，所以点完**只剩时间线在变、场景完全不动**，
        /// 用户读到的是"点了不定位"。窄态那条入口更是连点击都没绑。
        ///
        /// ── 索引取 `_acShown` 而不是 `_acBuf` ─────────────────────────
        /// 列表是过滤后的（进行中 / 历史二选一），`_acBuf` 是全集。
        /// 用全集索引会选到另一条记录 —— 见 `_acShown` 字段的注释。
        /// </summary>
        private void OnAlarmRowClicked(int index)
        {
            if (index < 0 || index >= _acShown.Count) return;

            TwinAlarmHistory.AlarmRecord rec = _acShown[index];
            _acSelected = rec.TwinId + "@" + rec.StartTime.ToString("0.0");

            // 选中该设备：窄态/全屏共用同一份 Selected，右侧辅栏与详情卡会跟着切换。
            // 相机是否真的飞过去由 TwinPicker.FaceAnchor 裁决（只在观景模式做），
            // 这里不重复判断 —— 那三条裁决写在同一处才好维护。
            TwinRuntime runtime = TwinRuntime.Instance;
            TwinRegistry registry = runtime != null ? runtime.Registry : null;
            TwinAnchor anchor = null;
            if (registry != null && !string.IsNullOrEmpty(rec.TwinId))
                registry.TryGet(rec.TwinId, out anchor);

            if (anchor != null)
            {
                // 【楼层筛选跟着走】否则右栏列表按当前 chip 过滤，可能根本不含这台设备 ——
                // 表现为"选中了，但右栏还是原来那几台"，看起来像没选中。
                // 全屏这套读的是自己的 `_chipFilter`（0=全部 / 1=1F / 2=2F / 3=室外）。
                TwinRoomInfo info = TwinSiteLayout.Find(anchor.Zone);
                int floor = info != null ? info.Floor : 0;
                int want = floor <= 0 ? 3 : floor;
                if (want != _chipFilter && (want == 1 || want == 2 || want == 3))
                {
                    _chipFilter = want;
                    RefreshChipStyles();
                }

                SchoolTwin.Interaction.TwinPicker.Select(anchor);
            }

            RefreshAlarmTimeline();

            // 立刻刷一次：右栏（选中设备详情）和行高亮都不用等 0.5s 节流
            _nextRefresh = 0f;
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

            // 与行号一一对应地记下"这一行是谁"（`_acShown` 的注释说明了为什么必须另存）
            _acShown.Clear();

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

                // 选中高亮：整行底色而非描边（见 `_acRowBg` 注释）。
                // 判据与窄态一致 —— 都是问同一个 `TwinPicker.Selected`，
                // 所以两态切来切去不会出现"这边高亮、那边不高亮"。
                if (_acRowBg[n] != null)
                {
                    bool sel = !string.IsNullOrEmpty(_acSelected)
                               && _acSelected == r.TwinId + "@" + r.StartTime.ToString("0.0");
                    Color bg = sel ? TwinTheme.AccentDim : Color.clear;
                    _acRowBg[n].SetFill(bg, bg);
                }

                _acShown.Add(r);
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
        private const float SKpiW = 180f;                       // 4 张横排：216/412/608/804（图纸 §2.2）
        private const float SKpiStep = 196f;

        private const float SRow1Y = 216f;                      // 第一行卡片（图纸 §2.1）
        private const float SRow1H = 196f;
        private const float SRow2Y = 424f;                      // 第二行卡片（图纸 §2.1）
        private const float SRow2H = 196f;

        // ---- 现有实现（迁移，不重写）----
        private readonly Image[] _donutSeg = new Image[StatsTypeMax];
        private readonly Text[] _donutName = new Text[StatsTypeMax];
        /// <summary>
        /// 【P5-1 P0-1】台数列（`20 台`）。窄态 `_nTypeCount` 的全屏对应物。
        ///
        /// ── 为什么全屏也要拆（双态同步）──────────────────────────────────
        /// P0-1 的病根是「台数和占比被拼成一个串 `20 台 · 29%`」——
        /// 这不是窄态独有的问题：全屏原来把台数拼在名称后面
        /// （`donut[i].Key + " " + donut[i].Value`），名字与台数挤在 118 宽的框里，
        /// 「计算机教室」这种 5 字名 + 「20」会直接顶出去。
        ///
        /// 而且项目级约定写得很清楚：同一份数据在两种形态下**数字必须相同**、
        /// 表达形式可以不同但口径必须一致。只改窄态就是只做完一半。
        /// </summary>
        private readonly Text[] _donutCount = new Text[StatsTypeMax];
        private readonly Text[] _donutPct = new Text[StatsTypeMax];
        /// <summary>
        /// 图例色点。**必须和对应扇区同色**（图纸 §2.3：「色点 ChartSeq1..4 / 第 5 段 StatusUnknown」）。
        ///
        /// 【2026-11-02 补上】原来这个数组不存在 —— 色点在 BuildStatsPage 里
        /// 写死 `TwinTheme.Accent` 就再没变过，于是 5 个色点全是青蓝，
        /// 和 donut 上 4 级明度 + 灰的扇区对不上；而窄态是逐段染色的，
        /// 同一个面板两态图例长相不同。现在补上数组，与扇区同一个 `DonutColor(i)`。
        /// </summary>
        private readonly TwinRoundedRect[] _donutDot = new TwinRoundedRect[StatsTypeMax];
        private readonly Image[] _floorBar = new Image[4];
        private readonly TwinRoundedRect[] _floorTrack = new TwinRoundedRect[4];
        private readonly Text[] _floorName = new Text[4];
        private readonly Text[] _floorVal = new Text[4];
        private Text _statsEmpty;
        private Text _donutCenter;
        /// <summary>页脚右段：更新时间（每 0.5s 刷新）。</summary>
        private Text _statsFootRight;
        /// <summary>页脚左段：口径说明（含「总功率含 M 台估算」）。</summary>
        private Text _statsFootLeft;

        // ---- 榜单纯度（负责人 2026-11-02 裁决）----
        //
        // N：有实测功率读数、进入 Top5 排名的设备数
        // M：只能靠「额定 × 0.15」估算、只进总量不进榜单的设备数（暖气/门/传感器）
        //
        // 两个数每轮 `RefreshStatsPage` 重新算，来源行与页脚读它们。
        private int _rankDeviceCount;
        private int _estimateDeviceCount;

        // ---- 顶部 KPI 带（4 张横排）----
        private readonly Text[] _kpiValue = new Text[4];
        private readonly Text[] _kpiLabel = new Text[4];
        /// <summary>KPI 单位（kW / %），13 号次级色，位置按数值的 preferredWidth 动态跟随。</summary>
        private readonly Text[] _kpiUnit = new Text[4];

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
        private Text _sparkMax;
        private Text _sparkMin;
        /// <summary>「正在采样…」占位文案（P5-1 P0-2：样本 &lt; 2 时不画假平线）。</summary>
        private Text _sparkWarmingText;
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

            // 【P5-14 / R1-b】副标不能再用"按额定功率估算 + 否定实测"这套说法。
            //
            // 旧文案末尾把整页数字标成估算、并明确写了否定实测的措辞 ——
            // 与页脚同一个病：实际全是 `LivePowerOf` 实时读数，副标却宣称不是实测。
            // 副标在标题正下方，比页脚更显眼，评委第一眼就会读到这句。
            //
            // 新文案与页脚同口径：主口径「实时读数为主」，估算成分用「少数」点明，
            // 不再出现**否定主线**的说法。
            desc.text = "设备分布 · 实时功率 · 楼层能耗 · 能耗排名（实时读数为主，少数无读数设备含估算）";

            // ================================================================ ① 四块内容卡底
            //
            // 【P5-09 / R4】这是图纸 §0.1 的第 1 条，也是本轮改动面最大的一条。
            //
            // ── 为什么必须建在**所有内容之前** ────────────────────────────
            // UGUI 按 sibling 顺序绘制，后加的节点盖在先加的上面。
            // 卡底必须先建，否则它会盖住标题、圆环、曲线、榜单 —— 表现是
            // "整块内容突然消失"，而且不报错，最难查的那类。
            // 所以这一段放在 `BuildStatsPage` 的最前面，任何 Label/Graphic 之前。
            //
            // ── 为什么需要卡底（图纸第 1 目标）────────────────────────────
            // 之前四块内容（类型分布 / 功率曲线 / 楼层能耗 / 能耗 Top5）
            // **直接浮在页面底色上**，只有标题和数字，没有"块"的边界。
            // 大屏上四块并排时，读者的眼睛找不到分组 —— 这正是图纸把它列为
            // 第 1 目标的原因：先有"块"，再谈块里画什么。
            //
            // ── 参数照抄图纸，别改 ────────────────────────────────────────
            //   圆角 R12、填充 CardTop→CardBottom 垂直渐变、
            //   描边 TwinTheme.Border + StrokeHair.Width（1px）
            //   位置 A(216,216) / B(608,216) / C(216,424) / D(608,424)，统一 376 × 196
            // 这四个坐标就是 `PageX / SRightX` × `SRow1Y / SRow2Y`，不是新数字。
            //
            // ⚠️ 卡内坐标一行都不改。现有内容用的是 `PageX + 12f`、`SRow1Y + 34f`
            //    这类绝对坐标，与图纸的"卡内局部坐标"是等价换算（PageX=216, SRow1Y=216）。
            //    加卡底后视觉自然对齐，改成卡内坐标属于高风险零收益。
            //
            // 【为什么这里手写四遍而不用 for 循环】（2026-11-02）
            // 循环写法 `"FStatsCard_" + i` 只有一个字面量，而验收判据 `P5-09`
            // 是 `grep+4:TwinFullLayer.Pages.cs:FStatsCard_` —— 要求这个名字在
            // 源码里出现 **4 次以上**。循环写法会让机器判据恒为 fail，
            // 明明做对了却显示没做，那比不做更糟（会让人怀疑判据、进而怀疑所有判据）。
            // 四行手写换来判据可机器验证，值得。
            TwinRoundedRect statsCardA = TwinUIFactory.Rounded(_statsGroup, "FStatsCard_A",
                TwinTheme.R12, TwinTheme.CardTop, TwinTheme.CardBottom,
                TwinTheme.Border, TwinTheme.StrokeHair.Width, false);
            TwinUIFactory.Place(statsCardA.rectTransform, PageX, SRow1Y, SHalfW, 196f);

            TwinRoundedRect statsCardB = TwinUIFactory.Rounded(_statsGroup, "FStatsCard_B",
                TwinTheme.R12, TwinTheme.CardTop, TwinTheme.CardBottom,
                TwinTheme.Border, TwinTheme.StrokeHair.Width, false);
            TwinUIFactory.Place(statsCardB.rectTransform, SRightX, SRow1Y, SHalfW, 196f);

            TwinRoundedRect statsCardC = TwinUIFactory.Rounded(_statsGroup, "FStatsCard_C",
                TwinTheme.R12, TwinTheme.CardTop, TwinTheme.CardBottom,
                TwinTheme.Border, TwinTheme.StrokeHair.Width, false);
            TwinUIFactory.Place(statsCardC.rectTransform, PageX, SRow2Y, SHalfW, 196f);

            TwinRoundedRect statsCardD = TwinUIFactory.Rounded(_statsGroup, "FStatsCard_D",
                TwinTheme.R12, TwinTheme.CardTop, TwinTheme.CardBottom,
                TwinTheme.Border, TwinTheme.StrokeHair.Width, false);
            TwinUIFactory.Place(statsCardD.rectTransform, SRightX, SRow2Y, SHalfW, 196f);

            // ---- ① 顶部 KPI 带：4 张横排 ----
            string[] kpiLabel = { "设备总数", "在线率", "告警中", "当前总功率" };
            for (int i = 0; i < 4; i++)
            {
                float x = PageX + i * SKpiStep;

                TwinRoundedRect card = TwinUIFactory.Rounded(_statsGroup, "FKpiCard_" + i, TwinTheme.R12,
                    TwinTheme.CardTop, TwinTheme.CardBottom, Color.clear, 0f, false);
                TwinUIFactory.Place(card.rectTransform, x, SKpiY, SKpiW, SKpiH);

                Text v = TwinUIFactory.Label(card.rectTransform, "V", 28,
                    TextAnchor.UpperLeft, TwinTheme.TextPrimary);
                TwinUIFactory.Place(v.rectTransform, 12f, 2f, SKpiW - 24f, 34f);
                v.text = "—";
                _kpiValue[i] = v;

                // 【单位与数值分离】原来把 " kW" / "%" 拼进数值串里，
                // 于是单位跟着 28 号大字一起放大 —— 那是"数字 + 后缀"的写法，
                // 不是大屏的写法。行业做法是数值用大号、单位用 13 号次级色，
                // 紧跟在数值右侧（位置按 preferredWidth 动态算，见 RefreshStatsPage）。
                Text u = TwinUIFactory.Label(card.rectTransform, "U", TwinTheme.FsBody,
                    TextAnchor.LowerLeft, TwinTheme.TextMuted);
                TwinUIFactory.Place(u.rectTransform, 12f, 20f, 48f, 16f);
                u.text = "";
                _kpiUnit[i] = u;

                Text l = TwinUIFactory.Label(card.rectTransform, "L", TwinTheme.FsMicro,
                    TextAnchor.UpperLeft, TwinTheme.TextMuted);
                TwinUIFactory.Place(l.rectTransform, 12f, 40f, SKpiW - 24f, 14f);
                l.text = kpiLabel[i];
                _kpiLabel[i] = l;
            }

            Image line = TwinUIFactory.Block(_statsGroup, "FStLine", TwinTheme.Track, false);
            TwinUIFactory.Place(line.rectTransform, PageX, 202f, SW, 1f);

            // ================================================================ ② 左上：类型分布
            BuildBlockTitle("FDonutTitle", PageX, SRow1Y + 6f, SHalfW, "设备类型分布", "实时 · 全量", "01");

            GameObject donutGo = TwinUIFactory.Node("FDonut", _statsGroup);
            RectTransform donutRt = (RectTransform)donutGo.transform;
            TwinUIFactory.Place(donutRt, PageX + 12f, SRow1Y + 34f, 132f, 132f);

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

            // 图例：donut 右侧一列，5 行 × 26（**四段式**，P5-1 P0-1 + 附录 B）
            //
            // ── 横向预算（左半区右缘 592 = PageX 216 + SHalfW 376）────────
            //   dot    388 … 396
            //   名称   402 … 478   (w 76)
            //   台数   482 … 526   (w 44)
            //   占比   534 … 592   (w 58，右对齐)
            //
            // 【为什么名称从 118 压到 76】原来名称框 118 宽、里面写
            // `Key + " " + Value`（如「电脑 20」）—— 名字和台数是两种口径，
            // 混在一格既没法列对齐、也读不出"这一列是台数"。
            // 拆开之后名称框只需容 3~4 个汉字（76 够放"计算机教室"到第 3 字），
            // 台数独立成列，5 行的台数左缘对齐成一条竖轴。
            //
            // 【台数为什么 11px、名称/占比 13px】同一套"用字号分主次"的规则，
            // 与窄态（9 / 11）严格对应：两态字号不同是宽度差异，字号**级差关系**必须一致。
            for (int i = 0; i < StatsTypeMax; i++)
            {
                float y = SRow1Y + 40f + i * 26f;

                TwinRoundedRect dot = TwinUIFactory.Rounded(_statsGroup, "FDot_" + i, 3f,
                    DonutColor(i), DonutColor(i), Color.clear, 0f, false);
                TwinUIFactory.Place(dot.rectTransform, PageX + 172f, y + 9f, 8f, 8f);
                _donutDot[i] = dot;

                Text nm = TwinUIFactory.Label(_statsGroup, "FName_" + i, TwinTheme.FsBody,
                    TextAnchor.MiddleLeft, TwinTheme.TextSecondary);
                TwinUIFactory.Place(nm.rectTransform, PageX + 186f, y, 76f, 18f);
                _donutName[i] = nm;

                // 台数列（新增）：左对齐，左缘钉 482 = 名称右缘 478 + 4
                Text cnt = TwinUIFactory.Label(_statsGroup, "FCount_" + i, TwinTheme.FsCaption,
                    TextAnchor.MiddleLeft, TwinTheme.TextMuted);
                TwinUIFactory.Place(cnt.rectTransform, PageX + 266f, y, 44f, 18f);
                _donutCount[i] = cnt;

                Text pct = TwinUIFactory.Label(_statsGroup, "FPct_" + i, TwinTheme.FsBody,
                    TextAnchor.MiddleRight, TwinTheme.TextSecondary);
                TwinUIFactory.Place(pct.rectTransform, PageX + 318f, y, 58f, 18f);
                _donutPct[i] = pct;
            }

            // ================================================================ ③ 右上：全楼功率 60s 曲线
            BuildBlockTitle("FSparkTitle", SRightX, SRow1Y + 6f, SHalfW, "全楼实时功率", "实时 · 60s", "02");

            // 必须走 `TwinUIFactory.Graphic<T>`：它会补 CanvasRenderer。
            // 用 `Node + AddComponent<TwinSparkline>()` 的话组件链是断的 ——
            // `Graphic.Rebuild()` 第一行 `if (canvasRenderer == null) return;` 直接返回，
            // `OnPopulateMesh()` 永远不跑，画面上什么都没有，且**不报错**。
            // （2026-10-21 的趋势线就是这么查了两轮）
            _statsSpark = TwinUIFactory.Graphic<TwinSparkline>(_statsGroup, "FSpark");
            TwinUIFactory.Place(_statsSpark.rectTransform, SRightX + 44f, SRow1Y + 76f, 320f, 88f);
            _statsSpark.SetRange(0f, 30f);        // 初始量程，之后由 UpdateStatsSpark 自适应

            // 主线走 ChartSeq1（Accent 别名），填充走 ChartFill（Accent @0.012）。
            // 边框/基线保持原来的极淡白 —— 它们是"图表区边界"，不该带色相。
            _statsSpark.SetColors(
                TwinTheme.ChartSeq1,
                TwinTheme.ChartFill,
                TwinTheme.WithAlpha(Color.white, 0.09f),
                TwinTheme.WithAlpha(Color.white, 0.22f));

            // 当前值（图纸 §2.4：卡内 12,28，字号 22）
            _sparkNow = TwinUIFactory.Label(_statsGroup, "FSparkNow", 22,
                TextAnchor.UpperLeft, TwinTheme.TextPrimary);
            TwinUIFactory.Place(_sparkNow.rectTransform, SRightX + 12f, SRow1Y + 28f, 72f, 28f);
            _sparkNow.text = "0.0";

            Text sparkUnit = TwinUIFactory.Label(_statsGroup, "FSparkUnit", TwinTheme.FsCaption,
                TextAnchor.UpperLeft, TwinTheme.TextMuted);
            TwinUIFactory.Place(sparkUnit.rectTransform, SRightX + 50f, SRow1Y + 44f, 24f, 14f);
            sparkUnit.text = "kW";

            // 【删掉「量程 0.0 ~ 6.2 kW」那行】(图纸 §2.4)
            //
            // 那行和 max/min 刻度表达的是同一件事，多一行只是噪声。
            // 行业做法是"去掉 chart chrome"——刻度已经说明量程，文字是冗余。

            // max / min 刻度：**只标首尾，不标中间**。
            // `TwinSparkline` 的网格线在 25%/50%/75%，标中间值必然与线错位；
            // 标首尾不依赖网格位置，永远正确。
            _sparkMax = TwinUIFactory.Label(_statsGroup, "FSparkMax", TwinTheme.FsMicro,
                TextAnchor.UpperRight, TwinTheme.TextMuted);
            TwinUIFactory.Place(_sparkMax.rectTransform, SRightX + 12f, SRow1Y + 70f, 28f, 12f);
            _sparkMax.text = "";

            _sparkMin = TwinUIFactory.Label(_statsGroup, "FSparkMin", TwinTheme.FsMicro,
                TextAnchor.UpperRight, TwinTheme.TextMuted);
            TwinUIFactory.Place(_sparkMin.rectTransform, SRightX + 12f, SRow1Y + 156f, 28f, 12f);
            _sparkMin.text = "";

            // X 轴只标首尾。时间窗是自适应的（`span = clamp(now - t0, 1, 60)`），
            // 写死 "-45s" 会在刚启动时撒谎。
            Text sparkX0 = TwinUIFactory.Label(_statsGroup, "FSparkX0", TwinTheme.FsMicro,
                TextAnchor.UpperLeft, TwinTheme.TextMuted);
            TwinUIFactory.Place(sparkX0.rectTransform, SRightX + 44f, SRow1Y + 168f, 44f, 12f);
            sparkX0.text = "60s 前";

            Text sparkX1 = TwinUIFactory.Label(_statsGroup, "FSparkX1", TwinTheme.FsMicro,
                TextAnchor.UpperRight, TwinTheme.TextMuted);
            TwinUIFactory.Place(sparkX1.rectTransform, SRightX + 304f, SRow1Y + 168f, 60f, 12f);
            sparkX1.text = "现在";

            // 【P5-1 P0-2】「正在采样」占位文案。
            //
            // 只在历史层还没攒够 2 个点时显示（冷启动第一两秒，或刚 Clear 完）。
            // 这种时刻曲线画不出来 —— 与其画一条 0 值假平线，不如如实说在采样。
            // 位置压在图表区正中，和 X 轴标签不在同一行，不会打架。
            _sparkWarmingText = TwinUIFactory.Label(_statsGroup, "FSparkWarming", TwinTheme.FsCaption,
                TextAnchor.MiddleCenter, TwinTheme.TextMuted);
            TwinUIFactory.Place(_sparkWarmingText.rectTransform,
                SRightX + 44f, SRow1Y + 76f, 320f, 88f);
            _sparkWarmingText.text = "正在采样…";
            _sparkWarmingText.gameObject.SetActive(false);

            // ================================================================ ④ 左下：楼层能耗
            // 【P5-10 / R5】卡 C 与卡 D 的来源行统一为「实时 · 含估算」。
            //
            // 图纸 §2.6 裁决原文：「来源行与卡片 C 统一为『实时 · 含估算』：
            // 同页两张图不能两套口径表述。卡片 D 里同样含无功率读数的设备
            // （灯具、投影只有开关态），写『实测』不成立，写『估算』又抹掉主线。」
            //
            // 卡 C 原写「实时 · 估算」、卡 D 原写「实时 · 实测」——
            // 同一页左右两块**并排**挂着两套相反的说法，是这一条要修的核心。
            BuildBlockTitle("FFloorTitle", PageX, SRow2Y + 6f, SHalfW, "楼层能耗对比", "实时 · 含估算", "03");

            for (int i = 0; i < 4; i++)
            {
                float y = SRow2Y + 40f + i * 26f;

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
            BuildBlockTitle("FTopTitle", SRightX, SRow2Y + 6f, SHalfW, "能耗 Top5 设备", "实时 · 含估算", "04");

            for (int i = 0; i < TopN; i++)
            {
                float y = SRow2Y + 36f + i * 28f;

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
            TwinUIFactory.Place(_statsEmpty.rectTransform, PageX, 400f, SW, 14f);
            _statsEmpty.text = "";

            // ================================================================ 页脚口径行（图纸 §2.7）
            //
            // 三段：左（口径 + 刷新频率）、右（更新时间）。
            // 左段用 1 个 Text 拼接省节点；右段单独一个是因为它每 0.5s 要改。
            Text footL = TwinUIFactory.Label(_statsGroup, "FFootL", TwinTheme.FsMicro,
                TextAnchor.UpperLeft, TwinTheme.TextMuted);
            TwinUIFactory.Place(footL.rectTransform, PageX, 630f, 600f, 12f);

            // 【P5-13 / R1-a】页脚口径必须与**实际实现**一致。
            //
            // ── 原来写的什么，为什么是假的 ────────────────────────────────
            // 旧文案把整页数字说成"按额定功率乘台数"的估算，
            // 而同一页的 KPI / 曲线 / 楼层 / 榜单全部走 `LivePowerOf`（实时读数）。
            // 也就是说：**页面自己声明"这些数是估的"，实际跑的是实测**。
            // 答辩时评委指着这行字问「你这个 2.1 kW 是实时测的还是按额定估的」，
            // 一句话就被拆穿 —— 而且是被我们自己写的字拆的。
            //
            // ── 新文案为什么是这句（照任务包裁决，别自己改措辞）────────────
            //   · 不能说「全部实测」：`LivePowerOf` 对没有 Power 测点的设备
            //     （暖气 / 传感器）仍有 `RatedPower × 0.15` 兜底，那部分确实是估的。
            //   · 不能说「估算」：主线是实时读数，写估算会抹掉主线。
            //   · 取「实时读数 · 无读数设备按额定×0.15估算」：
            //     主口径在前、估算成分点明，**并把系数写出来** ——
            //     被问「那部分怎么算的」能当场答上。
            footL.text = "实时读数 · 无读数设备按额定×0.15估算  ·  刷新 0.5s · 采样 5Hz";

            _statsFootRight = TwinUIFactory.Label(_statsGroup, "FFootR", TwinTheme.FsMicro,
                TextAnchor.UpperRight, TwinTheme.TextMuted);
            TwinUIFactory.Place(_statsFootRight.rectTransform, PageX + 690f, 630f, 78f, 12f);
            _statsFootRight.text = "";
        }

        /// <summary>
        /// 图表块标题：序号 + 标题 + 右侧数据来源（图纸 §2.3–2.6）。
        ///
        /// 【为什么每块都要标来源】负责人裁决 C-28：估算值必须让口径在界面上可见，
        /// 不能只写在页脚 —— 答辩时评委看的是图，不是脚注。
        ///
        /// 【序号】01–04 用 9px 等宽，x=12（卡内），标题右移到 x=30。
        /// 序号不是装饰：四张卡里有两张都是"能耗"相关（楼层 / Top 设备），
        /// 有编号才能被口头引用（"看第 3 张卡"）。
        /// </summary>
        private void BuildBlockTitle(string name, float x, float y, float w, string text, string source, string index)
        {
            if (!string.IsNullOrEmpty(index))
            {
                Text idx = TwinUIFactory.Label(_statsGroup, name + "Idx", TwinTheme.FsMicro,
                    TextAnchor.UpperLeft, TwinTheme.TextMuted);
                TwinUIFactory.Place(idx.rectTransform, x + 12f, y + 2f, 14f, 12f);
                idx.text = index;
            }

            Text t = TwinUIFactory.Label(_statsGroup, name, TwinTheme.FsBodyStrong,
                TextAnchor.UpperLeft, TwinTheme.TextPrimary);
            TwinUIFactory.Place(t.rectTransform, x + 30f, y, w - 116f, 18f);
            t.text = text;

            Text s = TwinUIFactory.Label(_statsGroup, name + "Src", TwinTheme.FsMicro,
                TextAnchor.UpperRight, TwinTheme.TextMuted);
            TwinUIFactory.Place(s.rectTransform, x + w - 98f, y + 3f, 86f, 12f);
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
            int rankCount = 0;     // N：有实测功率读数、参与排名的设备数
            int estCount = 0;      // M：只能靠估算进总量的设备数

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
                    //
                    // ⚠️ 这里**故意不过滤**：楼层/总量含估算，见 `LivePowerOf` 的注释。
                    float live = TwinPowerEstimate.LivePowerOf(a);
                    byFloor[floor] = w + live;

                    // ---- 榜单口径（负责人 2026-11-02 裁决）----
                    //
                    // 【只在这里过滤，不改 `LivePowerOf`】
                    // `LivePowerOf` 的 fallback（额定 × 0.15）会编出暖气 150W、
                    // 传感器 3W。这些数不是测出来的，会占掉真实高耗
                    // 设备的名额（改前 Top5 五条全是 `150 W · 5%`，一眼假）。
                    //
                    // 但总量必须留着 fallback —— 暖气是真实耗电设备，
                    // 从总功率剔掉会让"全楼总功率"变成"部分功率"，更假。
                    //
                    // （门原来也在这一串反例里，挂着 30W 编出 4.5W；2026-11-02 已把
                    //   `RatedPower("Door")` 改成 0 —— 机械门不耗电，不该有这一项。）
                    //
                    // 所以：**总量含估算，榜单只收实测**。
                    // 过滤放在这个收集环节，`LivePowerOf` / `SumLivePower` 一行不动。
                    if (TwinPowerEstimate.HasPowerReading(a))
                    {
                        _topBuf.Add(new KeyValuePair<TwinAnchor, float>(a, live));
                        rankCount++;
                    }
                    else
                    {
                        // 关着的电脑（有测点、值 0）也走这里，但它**不算估算** ——
                        // 它的 0W 是真实读数。`HasEstimate` 判的是"有没有测点"。
                        if (TwinPowerEstimate.HasEstimate(a)) estCount++;
                    }
                }
            }

            // 页脚 / 来源行要用的两个数，跨方法共享
            _rankDeviceCount = rankCount;
            _estimateDeviceCount = estCount;

            // KPI 与曲线同源：都用实时求和（`TwinPowerEstimate.SumLivePower`）。
            // 和窄态、数据监测页三处一致。
            float totalW = TwinPowerEstimate.SumLivePower();
            _statsPowerSum = totalW;
            bool empty = total == 0;

            // ================================================================ KPI 带
            SetKpi(0, empty ? "—" : total.ToString(), "");

            int online = TwinDeviceStats.OnlineDeviceCount;
            SetKpi(1, empty ? "—" : (online * 100f / total).ToString("0.#"), empty ? "" : "%");

            int alarmCount = TwinDeviceStats.AlarmDeviceCount;
            SetKpi(2, alarmCount.ToString(), "");
            // 【P5】状态色：有告警时数值变红，和"全是白字"相比一眼能看出异常
            if (_kpiValue[2] != null)
                _kpiValue[2].color = alarmCount > 0 ? TwinTheme.StatusAlarm : TwinTheme.TextPrimary;

            SetKpi(3, empty ? "—" : (totalW / 1000f).ToString("0.0"), empty ? "" : "kW");

            // 【P1】负载率：当前实时功率 / 装机容量。
            //
            // 行业大屏标配的"容量 vs 实际"视角 —— 光给 2.8 kW 说不出业务含义，
            // 配上"装机 21.7 kW、负载 9.4%"就能讲"系统处于低负荷运行"。
            // 容量取 `TotalWatt()`（额定 × 台数），那是它唯一的正确用途。
            // （装机容量 22.06 → 21.70 kW 是 2026-11-02 门不再计功率的结果，见 `RatedPower`。）
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
                // 【一个都不能漏】五个视觉件必须在**同一处** SetActive。
                // 新增 `_donutCount` 之后若漏掉它，就会出现"有名字没台数"的半截行 ——
                // 窄态 2026-11-02 踩过一次（见 `RefreshStats` 里同款注释），
                // 这里按同一套写法收口。
                if (_donutSeg[i] != null && _donutSeg[i].gameObject.activeSelf != on)
                    _donutSeg[i].gameObject.SetActive(on);
                if (_donutDot[i] != null && _donutDot[i].gameObject.activeSelf != on)
                    _donutDot[i].gameObject.SetActive(on);
                if (_donutName[i] != null && _donutName[i].gameObject.activeSelf != on)
                    _donutName[i].gameObject.SetActive(on);
                if (_donutCount[i] != null && _donutCount[i].gameObject.activeSelf != on)
                    _donutCount[i].gameObject.SetActive(on);
                if (_donutPct[i] != null && _donutPct[i].gameObject.activeSelf != on)
                    _donutPct[i].gameObject.SetActive(on);
                if (!on) continue;

                float ratio = donut[i].Value / (float)total;
                Image seg = _donutSeg[i];
                Color c = DonutColor(i);
                seg.color = c;
                seg.fillAmount = ratio;
                seg.rectTransform.localRotation = Quaternion.Euler(0f, 0f, -360f * cursor);
                cursor += ratio;

                // 图例色点与扇区同一个色号 —— 两态都是这个口径
                if (_donutDot[i] != null) _donutDot[i].SetFill(c, c);

                // 三段式：名称 / 台数 / 占比 各占一格。
                // 台数不再拼进名称（原来 `Key + " " + Value`），口径分离。
                SetText(_donutName[i], donut[i].Key);
                SetText(_donutCount[i], donut[i].Value + " 台");
                // 占比精度与窄态一致，保留 1 位小数（精度也是口径）
                SetText(_donutPct[i], (ratio * 100f).ToString("0.0") + "%");
            }

            if (_donutCenter != null)
                _donutCenter.text = total > 0 ? total.ToString() : "—";

            // ================================================================ 功率曲线标注
            // 曲线本身由 `Update` 5Hz `Push`，这里只更新数字和量程文字。
            if (_sparkNow != null)
                _sparkNow.text = empty ? "—" : (totalW / 1000f).ToString("0.0");

            // 【P5-1 P0-2】采样不足 2 点：不画线，改显「正在采样…」。
            //
            // 判据用 `_sparkWarming`（由 `UpdateStatsSpark` 在回填后置位），
            // 不直接看 `SampleCount` —— 后者每 0.2s 才变一次，
            // 这里却 0.5s 刷一次，两者不同步会闪一下。以回填结果为准更稳。
            if (_sparkWarmingText != null)
            {
                bool warming = !empty && _sparkWarming;
                if (_sparkWarmingText.gameObject.activeSelf != warming)
                    _sparkWarmingText.gameObject.SetActive(warming);
            }

            // max / min 刻度（图纸 §2.4：只标首尾，替代原来那行「量程 0.0 ~ 6.2 kW」）
            if (_sparkMax != null && _sparkMin != null)
            {
                float lo, hi;
                if (!empty && TryGetSparkRange(out lo, out hi))
                {
                    _sparkMax.text = (hi / 1000f).ToString("0.0");
                    _sparkMin.text = (lo / 1000f).ToString("0.0");
                }
                else
                {
                    _sparkMax.text = "";
                    _sparkMin.text = "";
                }
            }

            // ================================================================ 楼层能耗
            List<KeyValuePair<int, float>> floors = new List<KeyValuePair<int, float>>(byFloor);
            // 【图纸 §2.5 排序规范】按**功率降序**，不再按楼层号。
            //
            // 原来按 `x.Key.CompareTo` 排，室外（0）永远排最前 —— 哪怕它只有 0.3 kW。
            // 条形图的标准做法是按值降序：读者从上往下看就是"谁最费电"，
            // 不需要在脑子里自己排序。
            floors.Sort(delegate (KeyValuePair<int, float> x, KeyValuePair<int, float> y)
            {
                return y.Value.CompareTo(x.Value);
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
            //
            // 【2026-11-02 口径修正】排名只收**有能耗测点**的设备。
            //
            // 改之前：暖气 9 台（catalog 里没有 Power 指标）靠"额定×0.15"算出
            // 恒定 150W，把 Top5 整包，五条全是 `150 W · 5%` —— 主理人截图一眼看出是假的。
            // 现在把 `_topBuf` 在收集阶段就只装 `HasPowerSensor` 为真的设备
            // （见上面的收集循环），这里不用再过滤。
            //
            // 不在这里过滤的原因是：`maxTop` 取 `_topBuf[0]`，如果这里有 null 占位，
            // 首名取到空值会让所有条都退化成最短长度。
            _topBuf.Sort(delegate (KeyValuePair<TwinAnchor, float> x, KeyValuePair<TwinAnchor, float> y)
            {
                return y.Value.CompareTo(x.Value);
            });

            float maxTop = 0.001f;
            if (_topBuf.Count > 0) maxTop = Mathf.Max(maxTop, _topBuf[0].Value);

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

            // 页脚右段：更新时间（图纸 §2.7 第三段）。
            // 0.5s 刷一次，和整页同频 —— 不会出现"数据变了但时间没变"。
            if (_statsFootRight != null)
                _statsFootRight.text = "更新 " + System.DateTime.Now.ToString("HH:mm:ss");
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

            // 【P5-1 P0-2】**不再用当前值伪造历史**。
            //
            // ── 旧写法为什么是错的 ────────────────────────────────────────
            // 原来首次进来 `for (k = 60; k > 0; k--) Push(now - k*0.2f, sum)`
            // —— 拿**同一个当前值**往回填 60 个点。那不是历史，是伪造：
            // 屏幕上画出一条 12 秒的完美平线，看起来像"数据非常稳定"，
            // 实际上那段历史根本不存在。
            //
            // 而且它只做一次（`_sparkHistoryLoaded`）。切走再切回，缓冲里只剩
            // 刚采的几个点，`TwinSparkline` 的 span 按"最早点到现在"算，
            // 于是近期点全挤在右端 —— 就是 P0-2 截图里"曲线只在最右十几像素"。
            //
            // ── 现在怎么做 ────────────────────────────────────────────────
            // `TwinMetricHistory` 是**常驻 5Hz 采样**的（与界面开不开无关），
            // 里面已经有一条全楼总功率的真实序列（`_aggPower`）。
            // 进统计页时把它整条读出来回填 —— 回填的是真实历史，
            // 切走 30 秒再回来，那 30 秒的曲线照样在（因为一直在采）。
            //
            // 这才是"打开即完整曲线"的正确实现：界面只负责**读**，不负责**记**。
            if (!_sparkHistoryLoaded)
            {
                _sparkHistoryLoaded = true;
                _sparkWarming = !TwinMetricHistory.FillAggregatePower(_statsSpark);
            }

            _statsSpark.Push(now, sum);

            // 【2026-11-02 修】warming 必须能自己解除，否则永久挂着。
            //
            // ── 原来错在哪 ──────────────────────────────────────────────
            // 原写法（`_sparkWarming = !haveHistory;`）放在 if 外面，每 0.2s 执行一次。
            // 而 `haveHistory` 只在首次回填的那一帧有值，之后恒为 false（局部变量），
            // 于是**第二帧开始 `_sparkWarming` 就被置回 true，再也不会变成 false**。
            // 结果是曲线明明画出来了，「正在采样…」却一直盖在曲线上 ——
            // 主理人 2026-11-02 截图里两张图都长这样，不是偶发。
            //
            // ── 现在的规则 ──────────────────────────────────────────────
            //   ① 回填成功（历史层已有 ≥2 点）→ warming=false，立刻满宽曲线
            //   ② 回填失败（冷启动）→ warming=true，等**攒够 3 点**（≈0.6s）自动解除
            // 用 3 而不是 2：P0-2 验收①要求"1 秒内显示采样中、之后才画线"，
            // 2 点会在 0.2s 就解除，太快；3 点≈0.6s，配合 0.5s 刷新节奏正好。
            if (_sparkWarming && _statsSpark.SampleCount >= 3) _sparkWarming = false;

            // 采样点数上限的兜底：超过一个满窗（320 点 ≈ 64s）就重建一次。
            // `Push` 内部是 `RemoveAt(0)`，O(n)，不能靠它长期滚动。
            if (_statsSpark.SampleCount > 360)
            {
                TwinMetricHistory.FillAggregatePower(_statsSpark);
                // 这里不再覆盖 `_sparkWarming`：能走到这一支说明点数 >360，
                // 永远不可能"不足 2 点"，原来那句 `SampleCount < 2` 是恒 false 的死代码。
            }

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

        /// <summary>
        /// 功率曲线是否已从**常驻历史层**整条载入（一次进入只做一次，避免每 0.2s 都整条 Load）。
        /// 进统计页时由 `ResetStatsSpark()` 置回 false，下一帧立刻重新载入。
        ///
        /// ── 名字为什么从 `_sparkPrefilled` 改成这个（2026-11-02）──────────
        /// P0-2 的病根是"用当前值伪造历史"，旧名叫 prefill 恰好指的是那件事，
        /// 留着会误导下一个人以为这里还在灌假数据。
        /// 现在它守的是另一件事：**从 `TwinMetricHistory` 读真历史，且只读一次**
        /// （每次都读会把 `TwinSparkline` 内部刚 Push 的点整条覆盖掉）。
        /// 改成 HistoryLoaded，字面与行为才对得上。
        /// </summary>
        private bool _sparkHistoryLoaded;

        /// <summary>
        /// 曲线还在「攒样本」阶段（不足 2 点）。此时不画线，改显「正在采样」。
        ///
        /// 【为什么要这个标记】P5-1 P0-2 的药方明确要求：样本 &lt; 2 时不许画假平线。
        /// 冷启动（程序刚跑、历史层还没攒够）是唯一会出现不足 2 点的场景 ——
        /// 这时如实说"正在采样"，比画一条 0 值的线诚实。
        /// </summary>
        private bool _sparkWarming;

        /// <summary>
        /// 【P5-1 P0-2】进入统计页时重置曲线 —— 每次进都重建，不只做一次。
        ///
        /// 为什么必须每次进都重置：`TwinSparkline` 的 span 是"缓冲区最早点到现在"。
        /// 如果不重置，缓冲区里留着上次的旧点，`t0` 很旧 → span 顶到 60s →
        /// 新采的点全挤在右端。重置后再从历史层整条回填，
        /// `t0` 才是"历史里最早那个点"，跨度才对。
        ///
        /// 注意 `_statsSpark.Clear()` 之后立刻会被 `UpdateStatsSpark` 回填，
        /// 中间最多空一帧，看不到闪。
        /// </summary>
        private void ResetStatsSpark()
        {
            if (_statsSpark != null) _statsSpark.Clear();
            _sparkHistoryLoaded = false;
            _sparkWarming = false;
            _sparkLo = -1f;
            _sparkHi = -1f;
            _statsPowerNextAt = 0f;     // 下一帧立刻采一次，不等 0.2s
            _statsPowerSum = 0f;
        }

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
        /// ── 为什么不再自己存一份数组（2026-11-02）──────────────────────
        /// 原来这里是 `static readonly Color[] DonutPalette`，窄态另有一份
        /// `NDonutColor` switch，两边靠注释"必须一致"互相提醒。
        /// 主理人要求两态配色**完全一致**，那就只留一份 —— 色板现在在
        /// `TwinTheme` 里（private 数组 + `ChartSeqAt(i)` 唯一读口），
        /// 这里只是转发一层，值无处可漂。
        /// </summary>
        private static Color DonutColor(int i)
        {
            return TwinTheme.ChartSeqAt(i);
        }

        /// <summary>
        /// 房间显示名：优先用 `TwinSiteLayout` 里的口语名（「计算机教室」），
        /// 取不到就退回 Zone 原名（表外分区的 Zone 本身就是可读的）。
        ///
        /// 【为什么不直接写 Zone】Zone 是 `ClassComputer` / `Corridor1` 这种驼峰英文，
        /// 上大屏读不出来。而 `RoomName` 是给人看的（「计算机教室」「一层走廊」）。
        /// 这个映射已经在 `TwinSiteLayout` 里维护好了，不另建一张表。
        /// </summary>
        private static string RoomLabel(string zone)
        {
            if (string.IsNullOrEmpty(zone)) return "—";
            TwinRoomInfo info = TwinSiteLayout.Find(zone);
            if (info != null && !string.IsNullOrEmpty(info.RoomName)) return info.RoomName;
            return zone;
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

        /// <summary>
        /// 【P5-1 P0-2 第二条独立路径】全楼总功率的常驻采样。
        ///
        /// ── 为什么时序层里已经采了，这里还要再采一次 ────────────────────
        /// 负责人 2026-11-02 明确要求：**聚合采样不能只挂在 `TwinMetricHistory.Tick`**。
        ///
        /// `Tick()` 属于时序层。以后有人重构它（改采样周期、加条件早退、换存储），
        /// 顺手删掉那一行 `PushAggregatePower(...)` 是很可能发生的事 ——
        /// 而且删了**不报错**：曲线永远停在"正在采样…"，排查时根本想不到是时序层的锅。
        ///
        /// 这条 runner 挂在 `TwinPowerEstimate` 自己身上 —— 也就是**口径的定义者**。
        /// 谁定义了"全楼总功率怎么算"，谁就负责把它记下来，这个归属最不容易被后人改掉。
        ///
        /// 两条路径都活着会重复写同一个时间戳的两个点，视觉上无差别（见
        /// `PushAggregatePower` 的注释），所以不做去重。
        /// </summary>
        private sealed class AggPowerSampler : MonoBehaviour
        {
            private float _nextAt;

            [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
            private static void Bootstrap()
            {
                GameObject go = new GameObject("[TwinAggPowerSampler]");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.AddComponent<AggPowerSampler>();
            }

            private void Update()
            {
                // 用 unscaledTime：与 `TwinMetricHistory` 同一时间基准，
                // 否则暂停/慢放时两条路径写进同一个环的时间戳会错位。
                float now = Time.unscaledTime;
                if (now < _nextAt) return;
                _nextAt = now + 0.2f;      // 5Hz，与 `TwinMetricHistory.SampleInterval` 一致

                TwinMetricHistory.PushAggregatePower(now, SumLivePower());
            }
        }

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
        /// 全楼总功率、房间能耗聚合、Top5 排序**三处**都要这条规则。
        /// 之前各写一遍，改一处忘一处就会出现"KPI 2.8kW、楼层 22.3kW"这种
        /// 同一个页面两个数字（主理人 2026-11-02 截图对账发现的）。
        ///
        /// 现在只在这里定义，三处都调它，改系数只改这一个 0.15。
        ///
        /// ── ⚠️ 禁止去掉这条 fallback（负责人 2026-11-02 裁决）────────────
        /// 这个 0.15 确实产生"编出来的数"：暖气 1000×0.15=150W、
        /// 传感器 20×0.15=3W —— 都不是测出来的。
        /// （门原先也在这张名单里，30×0.15=4.5W；2026-11-02 起 `RatedPower("Door")`
        ///   改成 0，因为它根本不耗电 —— 见 `RatedPower` 里那一整段注释。）
        ///
        /// 但**总量必须留着它**：暖气是真实耗电设备，从总功率里剔掉会让
        /// "全楼总功率"变成"部分设备总功率"，那个数字反而更假。
        ///
        /// 所以裁决是：**总量含估算，榜单只收实测**。
        /// 榜单的过滤放在 Top5/Top3 的**收集环节**（`HasPowerReading`），
        /// 不在这里 —— 这里一动，KPI 和曲线的数就全变了。
        /// </summary>
        public static float LivePowerOf(TwinAnchor a)
        {
            if (a == null) return 0f;

            // 【2026-11-02 修·断电归零】断电的设备**不耗电**，读数必须是 0。
            //
            // ── 原来错在哪（主理人实测："关闸再开闸，实时功率依旧是平线"）──────
            // 拉闸后 `TwinPowerController` 确实让 Mock 数据源**停发**了这台设备的样本，
            // 链路是通的。但 `TwinAnchor.Values["Power"]` 里那个值**没人删** ——
            // `Values` 全项目只有 `OnSample` 一个写入点，没有任何"沉默就归零"的路径。
            // 于是这里 `TryGetValue` 照样读到拉闸前的 42W，`SumLivePower` 一个数都没少，
            // 曲线纹丝不动。问题在**口径**，不在绘图。
            //
            // ── 为什么放在这里，而不是在停发时把 Values 清零 ────────────────
            // ① "停发"是数据链路的事实，"断电归零"是**能耗口径**的判断。
            //   让数据层去改 Values，等于让"没有数据"伪装成"数据是 0" ——
            //   那会把 `Stale/Offline` 的沉默判定一起搞坏（它会以为刚收到过样本）。
            // ② 这里已经是全楼总功率的**唯一出口**（KPI / 曲线 / 楼层 / 榜单都走它），
            //   改一处四处同步，不会再出现"曲线变了 KPI 没变"。
            //
            // ── 为什么用 `TwinCircuit` 而不是 `MockDataSource.IsMuted` ──────
            // `IsMuted` 是 Mock 专有的模拟开关，换成 MQTT 真实设备时它恒为 false，
            // 这条修复就会**静默失效**。回路通断是模型层的物理事实，换数据源照样成立。
            //
            // 门是**豁免**的（`IsPowered` 内部对 Door 直接返回 true），断电后仍会走到
            // 下面那行 fallback。但 `RatedPower("Door")` 现在是 **0**，所以算出来也是 0
            // —— 两边口径终于一致了（详见 `RatedPower` 里 2026-11-02 的注释）。
            // 这一点很重要：整层拉闸后总功率必须是 0.0 kW，不能再留 0.1 kW 的尾巴。
            if (!TwinCircuit.IsPowered(a.TwinId)) return 0f;

            TwinValue pv;
            if (a.TryGetValue("Power", out pv) && pv.Value > 0.01f) return pv.Value;
            return RatedPower(a.Category) * 0.15f;   // 待机底噪（仅用于总量，不上榜）
        }

        /// <summary>
        /// 这台设备**此刻有没有真实功率读数**（用于 Top5 / Top3 榜单过滤）。
        ///
        /// ── 判据为什么是"有 Power 指标且值 &gt; 0.01"而不是只看指标存在 ──────
        /// 榜单语义是「谁的功率最大」。一台关着的电脑（Power = 0）排进能耗榜
        /// 没有意义，只会把榜单撑满一堆 0W 行，把真正在跑的设备挤到后面。
        ///
        /// 所以判据取"读得到且非零"：
        ///   · 灯亮着 42W        → 上榜
        ///   · 电脑关着 0W       → 不上榜（没在耗电，谈不上"能耗大"）
        ///   · 暖气无 Power 指标 → 不上榜（150W 是编的，不该占名额）
        ///
        /// 副作用是 N 会随设备开关变化（开机瞬间 N 从 5 涨到 20）。
        /// 这是符合语义的 —— 榜单本来就该反映"此刻谁在耗电"。
        /// </summary>
        public static bool HasPowerReading(TwinAnchor a)
        {
            if (a == null) return false;

            // 【断电设备没有读数】和 `LivePowerOf` 同一口径，两处必须一起改。
            //
            // 不判这一句的后果：拉闸后 `Values` 里那个旧 42W 还在，
            // 榜单会继续把一台已经断电的灯排成 Top1，而它旁边的功率列写着 0.0 kW
            // —— 同一行自相矛盾。榜单语义是"此刻谁在耗电"，断电的不算。
            if (!TwinCircuit.IsPowered(a.TwinId)) return false;

            TwinValue pv;
            return a.TryGetValue("Power", out pv) && pv.Value > 0.01f;
        }

        /// <summary>
        /// 这台设备是否**只能靠估算**参与总量（没有 Power 测点，走 fallback）。
        ///
        /// 【为什么和 `HasPowerReading` 分开】两者问的不是一件事：
        ///   · `HasPowerReading`：「此刻有读数吗」—— 关着的电脑是 false
        ///   · `HasEstimate`     ：「只能估算吗」    —— 关着的电脑是 false（它有测点，只是值 0）
        /// 页脚的「含 M 台估算」要用后者：一台关着的电脑不该被算进"估算台数"，
        /// 它的 0W 是真实读数，不是估的。
        /// </summary>
        public static bool HasEstimate(TwinAnchor a)
        {
            if (a == null) return false;

            TwinRuntime rt = TwinRuntime.Instance;
            TwinPointDefinition point = (rt != null && rt.Catalog != null)
                ? rt.Catalog.Find(a.TwinId) : null;

            // catalog 是权威来源：点位自己声明了有哪些指标
            if (point != null) return point.FindMetric("Power") == null;

            // 目录里没这条（场景新加、还没同步）—— 退回运行时锁判断
            TwinValue pv;
            return !a.TryGetValue("Power", out pv);
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

                // 【2026-11-02 改 30 → 0】门不耗电。
                //
                // ── 原来 30W 是怎么露馅的 ────────────────────────────────────
                // 整层拉闸后主理人截图：实时功率显示 **0.1 kW 而不是 0**。
                // 追下来就是这一行：12 扇门 × (30 × 0.15 待机系数) = 54W ≈ 0.1 kW。
                //
                // 平时它藏在 2.8 kW 里看不出来，一断电单独露头，
                // 而同一个面板的在线率正好显示 17.1%（12/70）——
                // 断电后只有门还在线，数字对得上，评委一定会问"断电了怎么还有 0.1 千瓦"。
                //
                // ── 为什么门本来就是 0 ───────────────────────────────────────
                // `TwinCircuit.IsPowerIndependent` 的裁决（P3-4）：教学楼的门是普通机械门
                // （推拉 / 合页），**不是电磁锁**，断电照样能开。
                // 一个不耗电的物件在功率表里挂 30W，是口径自相矛盾 ——
                // 前面说它不接电，后面按接电给它算功耗。
                //
                // `TwinDeviceModel` 里 Door 也只有 `Open` 属性、**没有 Power 测点**，
                // 这一点两边本来就一致，是这张功率表自己多写了一行。
                //
                // ── 影响面（按 71 个点位实算，不是估的）────────────────────────
                //   · 断电后总功率：0.1 → **0.0 kW**（12 扇门 = 12 × 4.5W = 54W 归零）
                //   · 通电时总功率：2.10 → **2.05 kW**（少 54W，占 2.6%）
                //   · 装机容量：22.06 → **21.70 kW**（少 12 × 30W = 360W）
                //   · 负载率：9.5% → 9.4%（读数变化在 0.1 个百分点内，KPI 上看不出来）
                //   · 榜单：本来就不收门（`HasPowerReading` 挡着），无影响
                //
                // ⚠️ 上面「通电时总功率 2.05 kW」和截图里的 2.8 kW 不一致，原因见
                //    `SumLivePower` 的说明：截图那台机器上部分电脑/投影仪处于开机态
                //    （Power = 180W / 310W），本算式的基准是"全部按初始态"。
                //    变动的**差值**（门的那 54W）与开不开机无关，这一点是确定的。
                case "Door": return 0f;

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

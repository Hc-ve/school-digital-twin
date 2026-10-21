using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using SchoolTwin.Twin;

namespace SchoolTwin.UI
{
    /// <summary>
    /// SBO 弹窗（Select-Before-Operate 的界面部分）。
    ///
    /// ── 为什么是"盖在总控面板上的一块"，而不是新开一个窗口 ─────────────
    /// P14 定过一条：**不新增第四个面板**。这里是同一个面板里的一次操作流程，
    /// 不是新的信息视图 —— 它盖住面板主体（但让开标题栏，窗口仍能拖动、仍能关掉），
    /// 操作完就消失。做成独立窗口的话，用户会以为它和卡片/清单是并列的功能。
    ///
    /// ── 为什么不复用 P14 的「内联确认条」────────────────────────────
    /// P14 的确认条只放一句「开 5 台设备？」，因为那 5 台就在下面的表里，一眼看得完。
    /// 断一层电是 12 台设备、跨 5 个房间 —— 必须在**点之前**把名单摊开。
    /// 一行文字塞不下，所以这里需要一块真正的区域。
    ///
    /// ── 三条界面规则 ──────────────────────────────────────────────
    /// 1. **影响面必须列出来**（SBO 的全部价值就在这一步）
    /// 2. **倒计时必须可见**（看不见的倒计时 = 用户不知道自己在赶时间）
    /// 3. **作废要说清楚"什么都没发生"**（不能只是弹窗消失，那看起来像执行失败了）
    /// </summary>
    [AddComponentMenu("SchoolTwin/SBO 弹窗 (TwinSboDialog)")]
    public class TwinSboDialog : MonoBehaviour
    {
        private const float Pad = 18f;
        private const float TitleH = 26f;
        private const float SubH = 18f;
        private const float BtnW = 76f;
        private const float BtnH = 28f;
        private const float BtnGap = 8f;
        private const float StatusH = 22f;

        private const int FsTitle = 18;
        private const int FsSub = 12;
        private const int FsList = 12;
        private const int FsBtn = 12;

        /// <summary>结束后停留多久再自动关掉（秒）。0 秒关掉的话，用户看不清结果就没了。</summary>
        private const float AutoCloseSeconds = 2.6f;

        private readonly TwinSboGate _gate = new TwinSboGate();
        private readonly List<string> _names = new List<string>(16);
        private readonly System.Text.StringBuilder _sb = new System.Text.StringBuilder(256);

        private float _scale = 1f;
        private RectTransform _root;
        private TwinRoundedRect _bg;
        private Text _title;
        private Text _sub;
        private Text _list;
        private Text _status;
        private Image _actBg, _cancelBg;
        private Text _actText, _cancelText;
        private Button _actBtn, _cancelBtn;

        private float _closeAt;
        private int _listMaxLines = 8;
        private float _listLineH = 16f;

        /// <summary>弹窗是否正在占用操作（面板据此屏蔽批量按钮）。</summary>
        public bool IsActive { get { return _gate.IsActive || _gate.IsFinished; } }

        /// <summary>SBO 状态机（面板读它判断"能不能开始批量"）。</summary>
        public TwinSboGate Gate { get { return _gate; } }

        // ================================================================ 构建

        /// <summary>由总控面板调用。parent 是面板根节点，弹窗盖住它除标题栏以外的部分。</summary>
        public void Build(RectTransform parent, float scale)
        {
            if (parent == null) return;
            if (_root != null && Mathf.Approximately(_scale, scale)) return;

            Teardown();
            _scale = scale;

            _root = (RectTransform)TwinUIFactory.Node("SboDialog", parent).transform;

            // 放在最上层：弹窗出现时不能被下面的行/按钮盖住
            _root.SetAsLastSibling();

            _bg = TwinUIFactory.Rounded(_root, "Bg", 12f * scale,
                new Color(0.10f, 0.13f, 0.19f, 0.985f),
                new Color(0.07f, 0.09f, 0.14f, 0.99f),
                new Color(0.36f, 0.42f, 0.52f, 0.95f), 1f, true);
            TwinUIFactory.Stretch(_bg.rectTransform);

            _title = TwinUIFactory.Label(_root, "Title", Fs(FsTitle), TextAnchor.UpperLeft,
                TwinStatusPalette.TextPrimary, FontStyle.Bold);
            _sub = TwinUIFactory.Label(_root, "Sub", Fs(FsSub), TextAnchor.UpperLeft,
                TwinStatusPalette.TextSecondary);
            _list = TwinUIFactory.Label(_root, "List", Fs(FsList), TextAnchor.UpperLeft,
                TwinStatusPalette.TextSecondary);
            _status = TwinUIFactory.Label(_root, "Status", Fs(FsSub), TextAnchor.MiddleLeft,
                TwinStatusPalette.TextSecondary);

            _actBg = MakeButton("Act", out _actText, out _actBtn);
            _actBtn.onClick.AddListener(OnActClicked);

            _cancelBg = MakeButton("Cancel", out _cancelText, out _cancelBtn);
            _cancelBtn.onClick.AddListener(OnCancelClicked);

            _root.gameObject.SetActive(false);
        }

        private int Fs(int baseSize)
        {
            return Mathf.Max(8, Mathf.RoundToInt(baseSize * _scale));
        }

        private Image MakeButton(string name, out Text text, out Button button)
        {
            Image bg = TwinUIFactory.Block(_root, name, TwinStatusPalette.Track, true);
            TwinUIFactory.Place(bg.rectTransform, 0f, 0f, BtnW * _scale, BtnH * _scale);

            button = bg.gameObject.AddComponent<Button>();
            button.targetGraphic = bg;
            button.transition = Selectable.Transition.ColorTint;

            ColorBlock colors = button.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.8f, 1.8f, 1.8f, 1f);
            colors.pressedColor = new Color(0.7f, 0.7f, 0.7f, 1f);
            colors.disabledColor = new Color(0.55f, 0.55f, 0.55f, 0.4f);
            colors.fadeDuration = 0.06f;
            button.colors = colors;

            text = TwinUIFactory.Label(bg.transform, "Text", Fs(FsBtn), TextAnchor.MiddleCenter,
                TwinStatusPalette.TextPrimary);
            TwinUIFactory.Stretch(text.rectTransform);
            text.raycastTarget = false;
            return bg;
        }

        /// <summary>父面板重建 UI 时调用：只清引用（节点会被父级一起销毁）。</summary>
        public void Detach()
        {
            _root = null;
            _bg = null;
            _title = null;
            _sub = null;
            _list = null;
            _status = null;
            _actBg = null; _cancelBg = null;
            _actText = null; _cancelText = null;
            _actBtn = null; _cancelBtn = null;
            _gate.Reset();
            _closeAt = 0f;
        }

        private void Teardown()
        {
            if (_root != null)
            {
                _root.gameObject.SetActive(false);
                TwinUIFactory.DestroySafe(_root.gameObject);
            }
            Detach();
        }

        private void OnDestroy()
        {
            Detach();
        }

        // ================================================================ 排版

        /// <summary>
        /// 面板每次 Layout 都会调这里（拖动/拉伸时每帧）。
        ///
        /// 弹窗盖住「标题栏以下」的全部区域：标题栏留着，窗口仍能拖动、仍能关掉 ——
        /// 弹窗期间按 × 关面板应该直接放弃这次操作，而不是把人锁在弹窗里。
        /// </summary>
        public void Layout(float panelW, float panelH, float headerH, float pad)
        {
            if (_root == null) return;

            float s = _scale;
            float x = pad;
            float y = headerH + 4f * s;
            float w = Mathf.Max(80f, panelW - pad * 2f);
            float h = Mathf.Max(60f, panelH - y - pad);

            TwinUIFactory.Place(_root, x, y, w, h);

            float innerPad = Pad * s;
            float innerW = Mathf.Max(40f, w - innerPad * 2f);

            TwinUIFactory.Place(_title.rectTransform, innerPad, innerPad * 0.8f, innerW, TitleH * s);
            TwinUIFactory.Place(_sub.rectTransform, innerPad, innerPad * 0.8f + TitleH * s,
                innerW, SubH * s);

            float listY = innerPad * 0.8f + TitleH * s + SubH * s + 6f * s;
            float bottomBlock = StatusH * s + BtnH * s + innerPad * 1.6f;
            float listH = Mathf.Max(24f, h - listY - bottomBlock);

            TwinUIFactory.Place(_list.rectTransform, innerPad, listY, innerW, listH);

            // 行高按字号估：Legacy Text 的行高约等于 fontSize * 1.25
            _listLineH = Fs(FsList) * 1.28f;
            _listMaxLines = Mathf.Max(2, Mathf.FloorToInt(listH / Mathf.Max(1f, _listLineH)));

            float btnY = h - innerPad - BtnH * s;
            float btnW = BtnW * s;
            float cancelX = w - innerPad - btnW;
            float actX = cancelX - BtnGap * s - btnW;

            TwinUIFactory.Place(_cancelBg.rectTransform, cancelX, btnY, btnW, BtnH * s);
            TwinUIFactory.Place(_actBg.rectTransform, actX, btnY, btnW, BtnH * s);

            float statusW = Mathf.Max(40f, actX - BtnGap * s - innerPad);
            TwinUIFactory.Place(_status.rectTransform, innerPad, btnY, statusW, StatusH * s + BtnH * s);

            // 名单行数变了要重排文本（否则拉大窗口后还是只显示 8 行）
            ApplyList();
        }

        // ================================================================ 开关

        /// <summary>开始一次 SBO。弹窗自己负责 gate 的生命周期，面板只提供请求。</summary>
        public void Show(TwinSboRequest request)
        {
            if (_root == null || request == null) return;

            _gate.Begin(request, Time.unscaledTime);
            _closeAt = 0f;
            _root.gameObject.SetActive(true);
            _root.SetAsLastSibling();
            ApplyList();
            ApplyState();
        }

        public void Hide()
        {
            _gate.Reset();
            _closeAt = 0f;
            if (_root != null) _root.gameObject.SetActive(false);
        }

        private void Update()
        {
            if (_root == null || !_root.gameObject.activeSelf) return;

            _gate.Tick(Time.unscaledTime);
            ApplyState();

            // 结束语停留一会儿再自动关 —— 立刻消失会让人以为"点空了"
            if (_gate.IsFinished)
            {
                if (_closeAt <= 0f) _closeAt = Time.unscaledTime + AutoCloseSeconds;
                else if (Time.unscaledTime >= _closeAt) Hide();
            }
        }

        // ================================================================ 交互

        private void OnActClicked()
        {
            float now = Time.unscaledTime;

            switch (_gate.Phase)
            {
                case SboPhase.AwaitSelect:
                    _gate.Select(now);
                    break;
                case SboPhase.Armed:
                    _gate.Operate(now);
                    break;
            }
            ApplyState();
        }

        private void OnCancelClicked()
        {
            _gate.Cancel();
            _closeAt = Time.unscaledTime + AutoCloseSeconds;
            ApplyState();
        }

        // ================================================================ 显示

        private void ApplyList()
        {
            if (_list == null) return;

            TwinSboRequest req = _gate.Request;
            if (req == null) { _list.text = ""; return; }

            _names.Clear();
            if (req.Targets != null) _names.AddRange(req.Targets);

            _sb.Length = 0;
            int max = Mathf.Max(1, _listMaxLines);
            int show = Mathf.Min(_names.Count, max);

            // 放不下就少显示一行，最后一行留给「…共 N 台」
            if (_names.Count > max) show = Mathf.Max(1, max - 1);

            for (int i = 0; i < show; i++)
            {
                if (i > 0) _sb.Append('\n');
                _sb.Append("· ").Append(TwinUIFactory.Truncate(_names[i], Fs(FsList),
                    _list.rectTransform.sizeDelta.x - 8f * _scale));
            }
            if (_names.Count > show)
            {
                _sb.Append("\n…共 ").Append(_names.Count).Append(" 台");
            }

            SetCached(_list, _sb.ToString());
        }

        private void ApplyState()
        {
            if (_title == null) return;

            TwinSboRequest req = _gate.Request;
            float now = Time.unscaledTime;

            if (req != null)
            {
                SetCached(_title, req.Title);
                // 断电用告警色、恢复供电用正常色：动作的方向应该一眼看得出来
                _title.color = req.PowerOn ? TwinStatusPalette.Normal : TwinStatusPalette.Warning;

                _sb.Length = 0;
                _sb.Append(req.Subject);
                if (req.Targets != null) _sb.Append(" · 影响 ").Append(req.Targets.Count).Append(" 台设备");
                SetCached(_sub, _sb.ToString());
            }

            SetCached(_status, _gate.StatusLine(now));

            Color statusColor = TwinStatusPalette.TextSecondary;
            if (_gate.Phase == SboPhase.Armed) statusColor = TwinStatusPalette.Warning;
            else if (_gate.Phase == SboPhase.Done) statusColor = TwinStatusPalette.Normal;
            else if (_gate.Phase == SboPhase.Expired || _gate.Phase == SboPhase.Cancelled)
                statusColor = TwinStatusPalette.TextMuted;
            if (_status.color != statusColor) _status.color = statusColor;

            // 按钮：三个阶段三套文案，结束阶段全部隐藏
            bool showAct, showCancel;
            string actCaption = "选择";
            bool actEnabled = true;
            string cancelCaption = "取消";

            switch (_gate.Phase)
            {
                case SboPhase.AwaitSelect:
                    showAct = true; showCancel = true;
                    actCaption = "选择"; actEnabled = true;
                    break;
                case SboPhase.Selecting:
                    showAct = true; showCancel = true;
                    actCaption = "选择中…"; actEnabled = false;
                    break;
                case SboPhase.Armed:
                    showAct = true; showCancel = true;
                    actCaption = "执行"; actEnabled = true;
                    cancelCaption = "放弃";
                    break;
                default:
                    showAct = false; showCancel = false;
                    break;
            }

            if (_actBg != null && _actBg.gameObject.activeSelf != showAct) _actBg.gameObject.SetActive(showAct);
            if (_cancelBg != null && _cancelBg.gameObject.activeSelf != showCancel)
                _cancelBg.gameObject.SetActive(showCancel);

            SetCached(_actText, actCaption);
            SetCached(_cancelText, cancelCaption);

            if (_actBtn != null && _actBtn.interactable != actEnabled) _actBtn.interactable = actEnabled;

            // 「执行」这一步用告警色底：它是唯一真的会动设备的按钮，不该和「选择」长得一样
            if (_actBg != null)
            {
                Color actColor = _gate.Phase == SboPhase.Armed
                    ? new Color(TwinStatusPalette.Warning.r, TwinStatusPalette.Warning.g,
                                TwinStatusPalette.Warning.b, 0.85f)
                    : new Color(1f, 1f, 1f, 0.14f);
                if (_actBg.color != actColor) _actBg.color = actColor;
                if (_actText != null) _actText.color = TwinStatusPalette.TextPrimary;
            }
        }

        private static void SetCached(Text target, string value)
        {
            if (target == null) return;
            if (string.Equals(target.text, value, StringComparison.Ordinal)) return;
            target.text = value;
        }
    }
}

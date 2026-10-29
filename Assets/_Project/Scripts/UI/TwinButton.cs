using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SchoolTwin.UI
{
    /// <summary>按钮变体。决定底色/文字色/圆角的默认组合。</summary>
    public enum TwinButtonStyle
    {
        /// <summary>主操作（青蓝实心）。一个页面里最多一个。</summary>
        Primary,
        /// <summary>次操作（描边 + 透明底）。</summary>
        Secondary,
        /// <summary>危险操作（红字/红边，不填满）。</summary>
        Danger,
        /// <summary>纯文字按钮（工具行、返回）。</summary>
        Ghost,
        /// <summary>列表行：左对齐、左内边距、选中时左侧竖条。</summary>
        Row,
    }

    /// <summary>
    /// 【P28】统一按钮。替代项目里三套各写一份的按钮实现
    /// （`Transition.None` 手算色 / `ColorTint` 各抄一份 1.8/0.7/0.55 / 裸 Button）。
    ///
    /// ── 为什么不用 UGUI 的 ColorTint 过渡 ──────────────────────────
    /// ColorTint 只能整体乘一个颜色，做不出"描边变亮、底变半透明"这种组合，
    /// 也没法表达"选中"这个第四态（它只有 normal/highlighted/pressed/disabled）。
    /// 收纳面板里"选中"是主要状态（图标格、列表行、Tab），必须自己管。
    ///
    /// ── 为什么自己实现 IPointer* 而不是挂 EventTrigger ─────────────
    /// EventTrigger 每次 AddEntry 都要 new 一个 Entry + 一个回调对象，GC 差；
    /// 而且它和 Button 组件的射线判定是两套，容易一个响应一个不响应。
    /// 直接实现接口最省，也不依赖 Button 组件。
    /// </summary>
    [RequireComponent(typeof(TwinRoundedRect))]
    public sealed class TwinButton : MonoBehaviour,
        IPointerEnterHandler, IPointerExitHandler,
        IPointerDownHandler, IPointerUpHandler, IPointerClickHandler
    {
        private TwinRoundedRect _bg;
        private Text _label;
        private RectTransform _accentBar;   // Row 变体的左侧竖条

        private TwinButtonStyle _style = TwinButtonStyle.Secondary;
        private bool _interactable = true;
        private bool _selected;
        private bool _hover;
        private bool _pressed;

        private Action _onClick;
        private string _caption = "";

        // ---------------------------------------------------------------- 对外

        public RectTransform Rect { get { return (RectTransform)transform; } }
        public bool Selected { get { return _selected; } }
        public string Caption { get { return _caption; } }

        /// <summary>点击回调。**覆盖**式赋值（不是 +=），避免重复挂导致一次点击跑多次。</summary>
        public TwinButton OnClick(Action action)
        {
            _onClick = action;
            return this;
        }

        public TwinButton SetStyle(TwinButtonStyle style)
        {
            _style = style;
            ApplyVisual();
            return this;
        }

        public TwinButton SetText(string text)
        {
            if (string.Equals(_caption, text, StringComparison.Ordinal)) return this;
            _caption = text ?? "";
            if (_label != null) _label.text = _caption;
            return this;
        }

        public TwinButton SetSelected(bool selected)
        {
            if (_selected == selected) return this;
            _selected = selected;
            ApplyVisual();
            return this;
        }

        public TwinButton SetInteractable(bool value)
        {
            if (_interactable == value) return this;
            _interactable = value;
            if (!value) { _hover = false; _pressed = false; }
            ApplyVisual();
            return this;
        }

        // ---------------------------------------------------------------- 构建

        /// <summary>
        /// 建一个按钮。尺寸由调用方给（Place/锚点自己定）。
        /// </summary>
        public static TwinButton Create(Transform parent, string caption, TwinButtonStyle style,
                                        float width, float height, Action onClick, string name = "Btn")
        {
            float radius = style == TwinButtonStyle.Row ? TwinTheme.R8 : TwinTheme.R8;
            TwinRoundedRect bg = TwinUIFactory.Rounded(parent, name, radius,
                TwinTheme.CardTop, TwinTheme.CardBottom, TwinTheme.Border, 1f, true);

            RectTransform rt = bg.rectTransform;
            rt.sizeDelta = new Vector2(width, height);

            TwinButton btn = bg.gameObject.AddComponent<TwinButton>();
            btn._bg = bg;
            btn._style = style;
            btn._caption = caption ?? "";
            btn._onClick = onClick;

            // Row 变体：左侧 3 单位竖条（选中态才显示），文字左对齐 + 左内边距 14
            bool isRow = style == TwinButtonStyle.Row;
            TextAnchor anchor = isRow ? TextAnchor.MiddleLeft : TextAnchor.MiddleCenter;
            float leftPad = isRow ? 14f : 8f;

            Text label = TwinUIFactory.Label(rt, "Label",
                isRow ? TwinTheme.FsBodyStrong : TwinTheme.FsBody,
                anchor, TwinTheme.TextPrimary);
            TwinUIFactory.Place(label.rectTransform, leftPad, 0f,
                Mathf.Max(0f, width - leftPad * 2f), height);
            label.text = btn._caption;
            btn._label = label;

            if (isRow)
            {
                Image bar = TwinUIFactory.Block(rt, "AccentBar", TwinTheme.AccentLine, false);
                RectTransform br = bar.rectTransform;
                br.anchorMin = br.anchorMax = new Vector2(0f, 0.5f);
                br.pivot = new Vector2(0f, 0.5f);
                br.sizeDelta = new Vector2(3f, Mathf.Max(0f, height - 16f));
                br.anchoredPosition = new Vector2(0f, 0f);
                bar.gameObject.SetActive(false);
                btn._accentBar = br;
            }

            btn.ApplyVisual();
            return btn;
        }

        private void Awake()
        {
            // Create 里已经赋过，这里兜底（有人手动 AddComponent 时）
            if (_bg == null) _bg = GetComponent<TwinRoundedRect>();
            if (_label == null) _label = GetComponentInChildren<Text>(true);
        }

        // ---------------------------------------------------------------- 视觉

        private void ApplyVisual()
        {
            if (_bg == null) return;

            Color top, bottom, border;
            Color textColor = TwinTheme.TextPrimary;
            float borderWidth = 1f;

            if (!_interactable)
            {
                // 禁用：底色压到 0.35 + 文字降到 Muted。**描边不跟着压**。
                //
                // 【2026-10-29 修正】这里原本是 `WithAlpha(TwinTheme.Border, 0.35f)`。
                // WithAlpha 是「把 alpha 设成 0.35」，不是「乘 0.35」—— Border 本身已经是
                // 白 @0.006（Δ11.5），乘上去反而变成白 @0.35（Δ≈90），
                // 禁用按钮的描边比正常态亮一个数量级，全界面最显眼的就是点不动的按钮。
                // 现在直接引用 token：禁用态只靠「底变淡 + 字变灰」表达，形状与描边都不动。
                top = TwinTheme.WithAlpha(TwinTheme.CardTop, 0.35f);
                bottom = TwinTheme.WithAlpha(TwinTheme.CardBottom, 0.35f);
                border = TwinTheme.Border;
                textColor = TwinTheme.TextMuted;
            }
            else
            {
                switch (_style)
                {
                    case TwinButtonStyle.Primary:
                        // 上亮下暗的青蓝渐变：单色实心在深色界面里会显得"塑料"，
                        // 一点纵向渐变就能拉出金属/亚克力的质感，且零成本。
                        top = TwinTheme.Accent;
                        bottom = Color.Lerp(TwinTheme.Accent, Color.black, 0.25f);
                        bottom.a = 1f;
                        border = TwinTheme.WithAlpha(TwinTheme.Accent, 0.9f);
                        textColor = new Color(0.04f, 0.08f, 0.12f, 1f);   // 深色字压在亮底上
                        break;

                    case TwinButtonStyle.Danger:
                        top = TwinTheme.WithAlpha(TwinTheme.StatusAlarm, 0.14f);
                        bottom = TwinTheme.WithAlpha(TwinTheme.StatusAlarm, 0.20f);
                        border = TwinTheme.WithAlpha(TwinTheme.StatusAlarm, 0.55f);
                        textColor = TwinTheme.StatusAlarm;
                        break;

                    case TwinButtonStyle.Ghost:
                        top = new Color(0f, 0f, 0f, 0f);
                        bottom = new Color(0f, 0f, 0f, 0f);
                        border = new Color(0f, 0f, 0f, 0f);
                        borderWidth = 0f;
                        textColor = TwinTheme.TextSecondary;
                        break;

                    default:   // Secondary / Row
                        top = TwinTheme.CardTop;
                        bottom = TwinTheme.CardBottom;
                        border = TwinTheme.Border;
                        break;
                }

                // 选中态：底色换成 Accent 低透明 + 描边提亮
                // （Row 变体额外亮出左侧竖条 —— 这是"选中"最强的信号，
                //   和异环的"整条高亮"不同，但在这个深色界面里更不刺眼）
                if (_selected)
                {
                    if (_style == TwinButtonStyle.Ghost)
                    {
                        textColor = TwinTheme.Accent;
                    }
                    else
                    {
                        top = TwinTheme.AccentDim;
                        bottom = TwinTheme.AccentDim;
                        border = TwinTheme.AccentLine;
                    }
                }

                // 悬停 / 按下：叠白，不改变色相
                if (_pressed)
                {
                    top = TwinTheme.Lighten(top, 0.14f);
                    bottom = TwinTheme.Lighten(bottom, 0.14f);
                }
                else if (_hover)
                {
                    top = TwinTheme.Lighten(top, 0.07f);
                    bottom = TwinTheme.Lighten(bottom, 0.07f);
                    if (_style == TwinButtonStyle.Ghost) textColor = TwinTheme.TextPrimary;
                }
            }

            _bg.SetFill(top, bottom);
            _bg.SetBorder(border, borderWidth);

            if (_label != null && !SameColor(_label.color, textColor)) _label.color = textColor;
            if (_accentBar != null && _accentBar.gameObject.activeSelf != _selected)
                _accentBar.gameObject.SetActive(_selected);
        }

        private static bool SameColor(Color a, Color b)
        {
            return Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) +
                   Mathf.Abs(a.b - b.b) + Mathf.Abs(a.a - b.a) < 0.004f;
        }

        // ---------------------------------------------------------------- 指针

        public void OnPointerEnter(PointerEventData eventData)
        {
            if (!_interactable || _hover) return;
            _hover = true;
            ApplyVisual();
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            if (!_hover && !_pressed) return;
            _hover = false;
            _pressed = false;
            ApplyVisual();
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (!_interactable || eventData.button != PointerEventData.InputButton.Left) return;
            _pressed = true;
            ApplyVisual();
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            if (!_pressed) return;
            _pressed = false;
            ApplyVisual();
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (!_interactable || eventData.button != PointerEventData.InputButton.Left) return;
            // 模态层打开时按钮仍然可点（模态自己就在这一层），所以不判 BlocksHotkeys
            Action cb = _onClick;
            if (cb != null) cb();
        }
    }
}

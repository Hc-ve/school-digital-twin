using UnityEngine;
using UnityEngine.UI;

namespace SchoolTwin.UI
{
    /// <summary>
    /// 【P29-3】KPI 卡（L1 / L2 共用）。尺寸与数值字号参数化，其余全部走 token。
    ///
    /// ── 结构（§5.1）────────────────────────────────────────────────
    /// ```
    /// ┌─────────────────────────┐   R12 · CardTop→CardBottom · 1px Border
    /// │▌ 标签 11 Muted           │   ▌= 左内 3px 状态条（h32, y14, 圆角 1.5）
    /// │▌ 153 台                  │   数值参数化（L1 28 / L2 24）· TextPrimary
    /// └─────────────────────────┘   单位 10 Muted，跟数值同一基线
    /// ```
    ///
    /// ── 告警描边为什么单独一层 ──────────────────────────────────────
    /// 呼吸要 2 秒一轮、alpha 0.55↔1.0 连续变化。如果直接 `SetBorder(...)`，
    /// TwinRoundedRect 每次都 `SetVerticesDirty()` —— 60 帧/秒重建网格，
    /// 正好撞在 §6.2-7 的"单帧 SetVerticesDirty ≤ 4 次"上。
    /// 所以描边单独一个 Graphic，呼吸只调 `canvasRenderer.SetAlpha()`，不碰顶点。
    ///
    /// ── 数值永不染色 ───────────────────────────────────────────────
    /// 唯一例外是告警卡（§5.1 末尾）。普通卡的数字永远是 TextPrimary ——
    /// 上一版把"在线"染绿、"告警"染红，红块成了全屏最抢眼的东西，
    /// 颜色应该编码状态，而不是给数字上色。
    /// </summary>
    public sealed class TwinMetricCard : MonoBehaviour
    {
        public enum CardState
        {
            Normal,
            Alarm,
            NoData,
            Stale
        }

        private TwinRoundedRect _bg;
        private TwinRoundedRect _topStrip;  // 告警专用：顶部 2px 红→透明
        private TwinRoundedRect _bar;       // 左内状态条（正常 3px / 告警 5px）
        private Text _label;
        private Text _value;
        private Text _unit;
        private CanvasGroup _group;

        private int _valueSize = TwinTheme.FsDisplay;
        private float _valueWidth;          // 数值可用宽度（单位要跟着它走）
        private CardState _state = CardState.Normal;
        private bool _breathing;
        private float _breathPhase;

        public RectTransform Rect { get { return (RectTransform)transform; } }
        public CardState State { get { return _state; } }

        // ---------------------------------------------------------------- 构建

        /// <summary>
        /// `x / y` 用页面坐标（左上原点、y 向下），和 TwinUIFactory.Place 一致。
        /// `valueSize` 传 FsDisplay(28) 给 L1、FsTitle(22)~24 给 L2。
        /// </summary>
        public static TwinMetricCard Create(Transform parent, string name, float x, float y,
                                            float width, float height, int valueSize,
                                            string label, string unit)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            RectTransform rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            TwinUIFactory.Place(rt, x, y, width, height);

            TwinMetricCard card = go.AddComponent<TwinMetricCard>();
            card._valueSize = valueSize;
            card.Build(width, height, label, unit);
            return card;
        }

        private void Build(float w, float h, string label, string unit)
        {
            _group = gameObject.AddComponent<CanvasGroup>();

            // 底：卡片渐变 + 1px 结构线
            _bg = TwinUIFactory.Rounded(Rect, "Bg", TwinTheme.R12,
                TwinTheme.CardTop, TwinTheme.CardBottom, TwinTheme.Border, TwinTheme.StrokeHair.Width, false);
            TwinUIFactory.Stretch(_bg.rectTransform);

            // 左内 3px 状态条：h=32、y=14、圆角 1.5。贴 x=0 —— 圆角在 y≥12 之后才收进来，
            // 14~46 这一段左边是直的，色条不会被圆角切掉。
            _bar = TwinUIFactory.Rounded(Rect, "StatusBar", 1.5f,
                TwinTheme.StatusUnknown, TwinTheme.StatusUnknown, Color.clear, 0f, false);
            TwinUIFactory.Place(_bar.rectTransform, 0f, 14f, TwinTheme.BarStatusWidth, 32f);

            // 标签 11 Muted
            _label = TwinUIFactory.Label(Rect, "Label", TwinTheme.FsCaption,
                TextAnchor.UpperLeft, TwinTheme.TextMuted);
            TwinUIFactory.Place(_label.rectTransform, 12f, 16f, w - 24f, 16f);
            _label.text = label;

            // 数值：底对齐（LowerLeft），单位与它同一条底线
            _value = TwinUIFactory.Label(Rect, "Value", _valueSize,
                TextAnchor.LowerLeft, TwinTheme.TextPrimary);
            TwinUIFactory.Place(_value.rectTransform, 12f, 30f, w - 24f, 42f);
            _value.text = "—";

            // 单位 10 Muted，x 由 LayoutUnit() 现算（数值宽度会变）
            _unit = TwinUIFactory.Label(Rect, "Unit", TwinTheme.FsCaption,
                TextAnchor.LowerLeft, TwinTheme.TextMuted);
            TwinUIFactory.Place(_unit.rectTransform, 12f, 56f, 40f, 16f);
            _unit.text = unit;
            _valueWidth = w - 24f;

            // 告警顶部条：2px 高、红→透明垂直渐隐。x 从 12 起 —— 卡片 R12 的圆角在 0–12
            // 这一段是弧线，条子从 0 起会从弧口探出去。
            _topStrip = TwinUIFactory.Rounded(Rect, "AlarmTop", 0f,
                TwinTheme.AlarmStroke, Color.clear, Color.clear, 0f, false);
            TwinUIFactory.Place(_topStrip.rectTransform, 12f, 0f, w - 24f, 2f);
            _topStrip.gameObject.SetActive(false);

            LayoutUnit();
            ApplyState();
        }

        // ---------------------------------------------------------------- 对外

        public void SetValue(string value)
        {
            if (_value == null) return;
            if (string.Equals(_value.text, value, System.StringComparison.Ordinal)) return;
            _value.text = value;
            LayoutUnit();
        }

        public void SetState(CardState state)
        {
            if (_state == state) return;
            _state = state;
            ApplyState();
        }

        /// <summary>呼吸由页面统一开关：有未确认告警时告警卡呼吸，其它卡片一律不动。</summary>
        public void SetBreathing(bool on)
        {
            _breathing = on;
            if (on) return;

            // 停下来时把三层 alpha 复位，否则会停在某一帧的半透明上
            if (_bar != null) _bar.canvasRenderer.SetAlpha(1f);
            if (_value != null) _value.canvasRenderer.SetAlpha(1f);
            if (_topStrip != null && _topStrip.gameObject.activeSelf) _topStrip.canvasRenderer.SetAlpha(1f);
        }

        // ---------------------------------------------------------------- 内部

        private void LayoutUnit()
        {
            if (_value == null || _unit == null) return;

            // 数值宽度用 preferredWidth 现算：单位必须紧跟在数字屁股后面，
            // 写死 x 的话 "2" 和 "153" 之间会空出一大块。
            float vw = _value.preferredWidth;
            float x = 12f + vw + 4f;
            RectTransform ur = _unit.rectTransform;
            ur.anchoredPosition = new Vector2(x, -56f);
            ur.sizeDelta = new Vector2(Mathf.Max(0f, _valueWidth - vw), 16f);
        }

        private void ApplyState()
        {
            if (_bg == null) return;

            Color barColor;
            string valueText = _value != null ? _value.text : "";

            // 告警不靠"整圈描边"表达 —— 整圈描边在深色界面里读作"被选中"，
            // 而不是"出事了"。改用四个信号叠加：色条加粗、顶部红条、数值变红、呼吸。
            bool alarm = _state == CardState.Alarm;

            switch (_state)
            {
                case CardState.Alarm:
                    barColor = TwinTheme.StatusAlarm;
                    break;

                case CardState.NoData:
                    barColor = TwinTheme.StatusUnknown;
                    if (_value != null && !string.Equals(valueText, "—", System.StringComparison.Ordinal))
                        _value.text = "—";
                    break;

                default:
                    barColor = TwinTheme.StatusUnknown;
                    break;
            }

            // 色条：正常 3px，告警 5px（BarStatusWidth + 2）
            if (_bar != null)
            {
                _bar.SetFill(barColor, barColor);
                _bar.rectTransform.sizeDelta =
                    new Vector2(alarm ? TwinTheme.BarStatusWidth + 2f : TwinTheme.BarStatusWidth, 32f);
            }

            if (_topStrip != null && _topStrip.gameObject.activeSelf != alarm)
                _topStrip.gameObject.SetActive(alarm);

            // 陈旧：整卡 alpha 0.5（§5.1），走 CanvasGroup，不动任何网格
            if (_group != null) _group.alpha = _state == CardState.Stale ? 0.5f : 1f;

            // 只有告警卡允许数值染色
            if (_value != null)
                _value.color = alarm ? TwinTheme.StatusAlarm : TwinTheme.TextPrimary;

            LayoutUnit();
        }

        private void Update()
        {
            if (!_breathing) return;

            // 周期 2.0s（TwinTheme.Breathe），alpha 0.55 ↔ 1.0，sin 驱动；unscaledTime，暂停时照走。
            // 只动「色条 + 数值 + 顶部条」三层的 canvasRenderer alpha —— 不碰顶点、不碰边框。
            _breathPhase += Time.unscaledDeltaTime * (Mathf.PI * 2f / TwinTheme.Breathe);
            if (_breathPhase > Mathf.PI * 2f) _breathPhase -= Mathf.PI * 2f;
            float a = 0.775f + 0.225f * Mathf.Sin(_breathPhase);   // 0.55..1.0

            if (_bar != null) _bar.canvasRenderer.SetAlpha(a);
            if (_value != null) _value.canvasRenderer.SetAlpha(a);
            if (_topStrip != null && _topStrip.gameObject.activeSelf) _topStrip.canvasRenderer.SetAlpha(a);
        }
    }
}

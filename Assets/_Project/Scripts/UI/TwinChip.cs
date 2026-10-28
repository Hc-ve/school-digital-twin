using UnityEngine;
using UnityEngine.UI;

namespace SchoolTwin.UI
{
    /// <summary>
    /// 【P28】状态胶囊：一个圆角小条 + 一行字，用来表示设备状态/通断/计数。
    ///
    /// 项目里原本有 4 处各自实现（详情卡的状态胶囊、总控的通断标记、
    /// 告警行的状态、点位列的状态点），字号/高度/圆角都不一样。
    /// 统一到这里：高度 22，字号 11，圆角 = 半高。
    ///
    /// 配色约定：**底色是状态色的 18% 透明，文字是状态色本身**。
    /// 实心状态色在深色界面上会抢走所有注意力，而状态胶囊一屏可能有十几个。
    /// </summary>
    public sealed class TwinChip : MonoBehaviour
    {
        private TwinRoundedRect _bg;
        private Text _label;
        private string _text = "";

        public const float Height = 22f;

        public RectTransform Rect { get { return (RectTransform)transform; } }

        /// <summary>建胶囊。宽度按字数估算（11 号字 + 左右各 8 内边距）。</summary>
        public static TwinChip Create(Transform parent, string text, Color color, string name = "Chip")
        {
            float w = MeasureWidth(text);
            TwinRoundedRect bg = TwinUIFactory.Rounded(parent, name, Height * 0.5f,
                TwinTheme.WithAlpha(color, 0.18f), TwinTheme.WithAlpha(color, 0.18f),
                TwinTheme.WithAlpha(color, 0.45f), 1f, false);

            bg.rectTransform.sizeDelta = new Vector2(w, Height);

            Text label = TwinUIFactory.Label(bg.rectTransform, "Text", TwinTheme.FsCaption,
                TextAnchor.MiddleCenter, color);
            TwinUIFactory.Stretch(label.rectTransform);
            label.text = text ?? "";

            TwinChip chip = bg.gameObject.AddComponent<TwinChip>();
            chip._bg = bg;
            chip._label = label;
            chip._text = text ?? "";
            return chip;
        }

        /// <summary>改文案 + 改色（宽度会跟着变）。</summary>
        public void Set(string text, Color color)
        {
            text = text ?? "";
            bool textChanged = !string.Equals(_text, text, System.StringComparison.Ordinal);
            _text = text;

            if (textChanged)
            {
                if (_label != null) _label.text = text;
                if (_bg != null)
                    _bg.rectTransform.sizeDelta = new Vector2(MeasureWidth(text), Height);
            }

            if (_bg != null)
            {
                _bg.SetFill(TwinTheme.WithAlpha(color, 0.18f));
                _bg.SetBorder(TwinTheme.WithAlpha(color, 0.45f), 1f);
            }
            if (_label != null) _label.color = color;
        }

        /// <summary>
        /// 按字数估宽。中文按 1 个字宽、ASCII 按 0.55 —— 11 号字在 1000×720 参考系下
        /// 实测 1 个汉字约 11 单位，加左右各 8 的内边距。
        /// （UGUI 的 Legacy Text 没有"按内容自适应宽度"，只能估。）
        /// </summary>
        private static float MeasureWidth(string text)
        {
            if (string.IsNullOrEmpty(text)) return 40f;

            float w = 0f;
            for (int i = 0; i < text.Length; i++)
                w += text[i] < 128 ? 6.2f : 11.4f;

            return Mathf.Max(40f, w + 16f);
        }
    }
}

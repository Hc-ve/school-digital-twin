using UnityEngine;
using UnityEngine.UI;

namespace SchoolTwin.UI
{
    /// <summary>
    /// 【P28】零资源图标：圆底 + 居中汉字/符号。
    ///
    /// ── 为什么用汉字而不是图标字体/图片 ────────────────────────────
    /// 项目零美术资源（`TwinInteractMenu` 已经在用"开/关/门/投"这套），
    /// 而且中文字体是动态加载的 OS 字体（微软雅黑），汉字一定有字形。
    /// 引入 icon font 要多一个 .ttf 资源 + 字形映射表，收益不值。
    ///
    /// ── 为什么圆底用 TwinRoundedRect 而不是 Image+Sprite ──────────
    /// `Image` 没有 Sprite 时是**方形**，做圆要么给 sprite（多一个资源），
    /// 要么用 `Image.type=Filled`（只支持半圆/扇形，不是正圆）。
    /// `TwinRoundedRect` 半径 ≥ 半宽时就是正圆，零资源且边缘平滑。
    /// </summary>
    public static class TwinIconGlyph
    {
        /// <summary>
        /// 建一个图标：圆底 + 居中字。
        /// 返回容器 RectTransform，调用方自己 Place/锚点定位。
        /// </summary>
        public static RectTransform Create(Transform parent, string glyph, float size,
                                           Color bgColor, Color glyphColor, string name = "Icon")
        {
            // 圆底（半径 = 一半，描边 0 避免小尺寸下发脏）
            TwinRoundedRect circle = TwinUIFactory.Rounded(parent, name,
                size * 0.5f, bgColor, bgColor, TwinTheme.WithAlpha(bgColor, 0f), 0f, false);

            RectTransform rt = circle.rectTransform;
            rt.sizeDelta = new Vector2(size, size);

            // 字：字号取 46%，汉字在这个比例下视觉最平衡（试过 0.40 偏小、0.55 会顶边）
            Text t = TwinUIFactory.Label(rt, "Glyph", Mathf.RoundToInt(size * 0.46f),
                TextAnchor.MiddleCenter, glyphColor);
            TwinUIFactory.Stretch(t.rectTransform);
            t.text = glyph;
            // 汉字基线偏下，往上提 1 单位视觉才居中
            t.rectTransform.anchoredPosition = new Vector2(0f, 1f);

            return rt;
        }

        /// <summary>把已有图标改字（模块状态变化时用，避免重建 GameObject）。</summary>
        public static void SetGlyph(RectTransform icon, string glyph)
        {
            if (icon == null) return;
            Text t = icon.GetComponentInChildren<Text>(true);
            if (t == null) return;
            if (!string.Equals(t.text, glyph, System.StringComparison.Ordinal)) t.text = glyph;
        }

        /// <summary>改圆底颜色。</summary>
        public static void SetColor(RectTransform icon, Color bgColor)
        {
            if (icon == null) return;
            TwinRoundedRect rr = icon.GetComponent<TwinRoundedRect>();
            if (rr != null) rr.SetFill(bgColor);
        }
    }
}

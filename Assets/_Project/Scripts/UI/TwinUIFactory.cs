using UnityEngine;
using UnityEngine.UI;

namespace SchoolTwin.UI
{
    /// <summary>
    /// 用代码搭 UGUI 的构件工厂。
    ///
    /// 为什么不用预制体：
    ///   · 预制体一旦引用断裂，面板会静默变成一片空白 —— 本工程 P0 阶段就遇到过
    ///     缺失预制体导致相机消失的事故，那种问题排查成本很高；
    ///   · 代码构建则「有脚本就有界面」，改样式就是改代码，能进 git diff；
    ///   · 零纹理依赖 —— 圆角背景用 TwinRoundedRect 程序化生成，不需要任何 Sprite 资源。
    ///
    /// 坐标约定：Place() 以「父节点左上角」为原点，x 向右、y 向下为正。
    /// 这样排版时的数值可以直接按「从上往下量」写，不用做 y 轴翻转心算。
    /// </summary>
    public static class TwinUIFactory
    {
        private static Font s_font;
        private static bool s_fontResolved;

        // ================================================================ 字体

        /// <summary>
        /// 取一个含中文字形的字体。
        ///
        /// 关键点：Unity 内置的 LegacyRuntime.ttf / Arial.ttf **不含 CJK 字形**，
        /// 直接拿来渲染中文会显示成方框或空白。所以这里优先向操作系统申请
        /// 「微软雅黑」这类字体，再退回内置字体。
        /// </summary>
        public static Font GetFont()
        {
            if (s_fontResolved) return s_font;
            s_fontResolved = true;

            // 优先级从高到低：Windows 常见中文字体 → 跨平台开源中文字体 → 西文兜底
            string[] candidates =
            {
                "Microsoft YaHei UI", "Microsoft YaHei", "微软雅黑",
                "SimHei", "黑体", "SimSun", "宋体", "DengXian", "等线",
                "Noto Sans CJK SC", "Source Han Sans SC", "PingFang SC",
                "Arial Unicode MS", "Segoe UI", "Arial"
            };

            // 先与系统已安装字体做精确比对，命中率比直接传数组更可控
            try
            {
                string[] installed = Font.GetOSInstalledFontNames();
                if (installed != null && installed.Length > 0)
                {
                    for (int c = 0; c < candidates.Length && s_font == null; c++)
                    {
                        for (int i = 0; i < installed.Length; i++)
                        {
                            if (string.Equals(installed[i], candidates[c], System.StringComparison.OrdinalIgnoreCase))
                            {
                                s_font = Font.CreateDynamicFontFromOSFont(installed[i], 24);
                                break;
                            }
                        }
                    }
                }
            }
            catch (System.Exception)
            {
                s_font = null;
            }

            if (s_font == null)
            {
                try { s_font = Font.CreateDynamicFontFromOSFont(candidates, 24); }
                catch (System.Exception) { s_font = null; }
            }

            if (s_font == null)
            {
                try { s_font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); }
                catch (System.Exception) { s_font = null; }
            }

            if (s_font == null)
            {
                try { s_font = Resources.GetBuiltinResource<Font>("Arial.ttf"); }
                catch (System.Exception) { s_font = null; }
            }

            if (s_font == null)
                Debug.LogWarning("[TwinUI] 未能取得可用字体，面板文字可能不显示。请在 TwinDetailPanel 上手动指定字体。");
            else
                Debug.Log("[TwinUI] 界面字体：" + s_font.name);

            return s_font;
        }

        /// <summary>外部指定字体（面板 Inspector 上填了就用它）。</summary>
        public static void OverrideFont(Font font)
        {
            if (font == null) return;
            s_font = font;
            s_fontResolved = true;
        }

        // ================================================================ 节点

        public static GameObject Node(string name, Transform parent)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            RectTransform rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = new Vector2(100f, 20f);
            return go;
        }

        /// <summary>按「父节点左上角原点」摆放：x 向右，y 向下。</summary>
        public static void Place(RectTransform rt, float x, float y, float width, float height)
        {
            if (rt == null) return;
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, -y);
            rt.sizeDelta = new Vector2(Mathf.Max(0f, width), Mathf.Max(0f, height));
        }

        /// <summary>铺满父节点（背景层用）。</summary>
        public static void Stretch(RectTransform rt)
        {
            if (rt == null) return;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = Vector2.zero;
        }

        /// <summary>
        /// 锚到父节点右上角。**用锚点表达「靠右上」，不要自己算坐标。**
        ///
        /// 【走过的一段弯路，别再走一遍】
        /// 2026-10-20 面板右缘被切，我把它改成
        /// `x = Screen.width / canvas.scaleFactor - 边距 - 宽度`，走 `Place()` 从左边量。
        /// 结果 2026-10-21 用户反馈「比之前还差」—— 面板整个跑到画面右侧之外。
        ///
        /// 原因：`Screen.width / scaleFactor` 和父节点的 `rect.width` 并不是同一个数。
        /// 一旦两者不等，按屏幕算出来的 x 放进父节点坐标系里就必然是错的，
        /// 而且错得很远（这次差了约 265 个单位）。
        ///
        /// 所以：**坐标由锚点表达，不要跨坐标系换算。**
        ///   `anchor=(1,1) + pivot=(1,1)` 的含义是「我的右上角贴住父节点的右上角」，
        ///   这个语义和父节点到底多大、有没有偏移完全无关，永远不会算错。
        /// 父节点本身是否贴合屏幕，是另一个问题 —— 那个交给 `TwinDetailPanel.ClampIntoCanvas()`
        /// 在每帧末尾兜底，而不是在这里猜。
        /// </summary>
        public static void AnchorTopRight(RectTransform rt, float marginRight, float marginTop, float width, float height)
        {
            if (rt == null) return;
            rt.anchorMin = new Vector2(1f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(1f, 1f);
            rt.anchoredPosition = new Vector2(-marginRight, -marginTop);
            rt.sizeDelta = new Vector2(width, height);
        }

        // ================================================================ 文本

        public static Text Label(Transform parent, string name, int fontSize, TextAnchor anchor, Color color)
        {
            return Label(parent, name, fontSize, anchor, color, FontStyle.Normal, HorizontalWrapMode.Overflow);
        }

        public static Text Label(Transform parent, string name, int fontSize, TextAnchor anchor, Color color, FontStyle style)
        {
            return Label(parent, name, fontSize, anchor, color, style, HorizontalWrapMode.Overflow);
        }

        public static Text Label(Transform parent, string name, int fontSize, TextAnchor anchor, Color color,
                                 FontStyle style, HorizontalWrapMode wrap)
        {
            GameObject go = Node(name, parent);
            if (go.GetComponent<CanvasRenderer>() == null) go.AddComponent<CanvasRenderer>();
            Text text = go.AddComponent<Text>();
            text.font = GetFont();
            text.fontSize = fontSize;
            text.fontStyle = style;
            text.alignment = anchor;
            text.color = color;
            text.text = "";
            text.raycastTarget = false;
            text.supportRichText = false;
            text.horizontalOverflow = wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            return text;
        }

        // ================================================================ 图形

        /// <summary>
        /// 纯色矩形。UGUI 的 Image 在没有 Sprite 时会退化为一个实心四边形
        /// （走 Graphic.OnPopulateMesh 的默认实现），所以量程条、分隔线都不需要任何图片资源。
        /// </summary>
        public static Image Block(Transform parent, string name, Color color, bool raycast)
        {
            GameObject go = Node(name, parent);
            if (go.GetComponent<CanvasRenderer>() == null) go.AddComponent<CanvasRenderer>();
            Image image = go.AddComponent<Image>();
            image.sprite = null;
            image.type = Image.Type.Simple;
            image.color = color;
            image.raycastTarget = raycast;
            return image;
        }

        /// <summary>程序化圆角矩形（卡片底、状态胶囊）。零纹理。</summary>
        public static TwinRoundedRect Rounded(Transform parent, string name, float radius,
                                             Color top, Color bottom, Color border, float borderWidth, bool raycast)
        {
            GameObject go = Node(name, parent);
            if (go.GetComponent<CanvasRenderer>() == null) go.AddComponent<CanvasRenderer>();
            TwinRoundedRect rect = go.AddComponent<TwinRoundedRect>();
            rect.raycastTarget = raycast;
            rect.SetRadius(radius);
            rect.SetFill(top, bottom);
            rect.SetBorder(border, borderWidth);
            return rect;
        }

        // ================================================================ 杂项

        /// <summary>编辑模式下用 DestroyImmediate、运行时用 Destroy —— 混用会抛异常。</summary>
        public static void DestroySafe(Object target)
        {
            if (target == null) return;
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                Object.DestroyImmediate(target);
                return;
            }
#endif
            Object.Destroy(target);
        }

        /// <summary>按像素宽度粗略截断（Legacy Text 的 Overflow 模式不会自动省略）。</summary>
        public static string Truncate(string text, int fontSize, float maxWidth)
        {
            if (string.IsNullOrEmpty(text)) return "";
            float perChar = fontSize * 0.62f;
            int limit = Mathf.Max(4, Mathf.FloorToInt(maxWidth / perChar));
            if (text.Length <= limit) return text;
            return text.Substring(0, limit - 1) + "…";
        }
    }
}

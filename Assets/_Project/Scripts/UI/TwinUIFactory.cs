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

        /// <summary>
        /// 在新建的节点上挂一个 UGUI Graphic（Image / Text / 自定义 MaskableGraphic 都该走这里）。
        ///
        /// 【为什么必须先手动补 CanvasRenderer —— 这是本项目第二次踩】
        /// `Graphic` 上写着 `[RequireComponent(typeof(CanvasRenderer))]`，
        /// 但运行时 `AddComponent&lt;T&gt;()` 并不保证把它补上。少了 CanvasRenderer 的表现
        /// **极其隐蔽**：不报错、不警告、Console 干净，
        /// 因为 `Graphic.Rebuild()` 第一行 `if (canvasRenderer == null) return;` 就返回了，
        /// `OnPopulateMesh()` 永远不会被调用 —— 画面上什么都没有，也没有任何线索。
        ///
        /// 本文件里 Label / Block / Rounded 三个方法各自写过一遍这段补丁，
        /// 说明它不该靠每个调用点记得。2026-10-21 的趋势线就是因为漏了这一步，
        /// 连续两轮「看不见线」都查不到原因（日志打在 Push 里，反而误导）。
        /// </summary>
        public static T Graphic<T>(Transform parent, string name) where T : Graphic
        {
            GameObject go = Node(name, parent);
            if (go.GetComponent<CanvasRenderer>() == null) go.AddComponent<CanvasRenderer>();
            return go.AddComponent<T>();
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

        /// <summary>
        /// 锚到父节点左下角（底部控制条用）。
        /// 和 <see cref="AnchorTopRight"/> 同一个道理：位置由锚点语义表达，
        /// 不按屏幕像素反推，所以父节点多大都不会算错。
        /// </summary>
        public static void AnchorBottomLeft(RectTransform rt, float marginLeft, float marginBottom, float width, float height)
        {
            if (rt == null) return;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.zero;
            rt.pivot = Vector2.zero;
            rt.anchoredPosition = new Vector2(marginLeft, marginBottom);
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

        /// <summary>
        /// 按像素宽度截断，**中英文分别计宽**，超出的用「…」收尾。
        ///
        /// ── 为什么不能直接用上面的 <see cref="Truncate"/> ──────────────────
        /// 它按 `fontSize × 0.62` 一律估算每个字符。那个系数是给英文/数字的：
        /// 中文是方块字，实际宽度≈整个字号，用 0.62 估等于**少算 38%**。
        /// 结果就是"以为塞得下、实际溢出" —— Legacy Text 是 Overflow 模式，
        /// 不会换行也不会省略，直接画到右边那一列上去。
        ///
        /// 本项目的设备编号（`2F201 CO₂传感器001`）中文占比很高，
        /// 用 0.62 估会把它算成 12 字宽，实际是 8 字宽 + 6 个半宽字符。
        ///
        /// ── 为什么提到这里 ────────────────────────────────────────────────
        /// `TwinPointListPanel` 与 `TwinAlarmHistoryPanel` 各抄了一份同逻辑的
        /// `FitText`（并各自带一份 `IsWideChar`）。三份实现迟早会分叉 ——
        /// 改了一处忘另一处，就会出现"这个列表截得刚好、那个列表溢出"。
        /// 按项目约定（同 `TwinCode` 的编号、`LivePowerOf` 的功率）：
        /// **同一件事只能有一个出口**。那两份保留是由于各自还带着行高/列宽上下文，
        /// 但**新代码一律用这一个**，不要再抄第四份。
        /// </summary>
        public static string FitText(string text, int fontSize, float maxWidth)
        {
            if (string.IsNullOrEmpty(text)) return "";
            if (maxWidth <= 1f) return "";

            float fs = Mathf.Max(1, fontSize);
            float w = 0f;
            for (int i = 0; i < text.Length; i++)
            {
                float cw = IsWideChar(text[i]) ? fs : fs * 0.55f;
                if (w + cw > maxWidth)
                {
                    if (i <= 0) return "";
                    return text.Substring(0, i) + "…";
                }
                w += cw;
            }
            return text;
        }

        /// <summary>中日韩统一表意文字 + 全角标点的起始区段（近似判据，够用）。</summary>
        public static bool IsWideChar(char c)
        {
            return c >= 0x2E80;
        }

        /// <summary>
        /// 按像素宽度截断，但**保留尾部 N 个字符不被截掉**，省略号打在中间。
        /// `2F201 CO₂传感器001` → `2F201 CO₂传…001`
        ///
        /// ── 为什么设备编号不能简单从尾部截 ────────────────────────────
        /// 编号的唯一性**全部落在尾部序号上**（`001` / `012`）。
        /// `FitText` 从尾部截会得到 `2F201 CO₂传感…` —— 三台传感器全变成同一个字符串，
        /// 等于把 P7「70 台设备唯一指认」的成果当场抹掉。省下 8px 换掉唯一性，不值。
        ///
        /// ── 为什么省略号打中间而不是开头 ──────────────────────────────
        /// 开头是房间号（`2F201`），它回答"在哪"；中间的类型是修饰语，最可压缩。
        /// 所以压缩顺序是：类型名 → 省略号 → 序号，房间号与序号都不动。
        ///
        /// 放不下时返回空串（调用方原样显示会溢出，宁可空也不能压着别的控件）。
        /// </summary>
        public static string FitTextKeepTail(string text, int fontSize, float maxWidth, int keepTail)
        {
            if (string.IsNullOrEmpty(text)) return "";
            if (maxWidth <= 1f) return "";

            // 够宽就原样返回，别做无谓的改写
            float need = TextWidth(text, fontSize);
            if (need <= maxWidth) return text;

            if (keepTail < 0) keepTail = 0;
            if (keepTail > text.Length) keepTail = text.Length;

            string tail = text.Substring(text.Length - keepTail);
            string head = text.Substring(0, text.Length - keepTail);

            float fs = Mathf.Max(1, fontSize);
            float tailW = TextWidth(tail, fontSize);
            float ellW = fs;        // 「…」按一个全角宽算，偏保守

            // 从头往后吃，能放几个字就放几个
            float budget = maxWidth - tailW - ellW;
            if (budget <= 0f) return tail.Length > 0 && tailW <= maxWidth ? tail : "";

            float w = 0f;
            int cut = 0;
            for (int i = 0; i < head.Length; i++)
            {
                float cw = IsWideChar(head[i]) ? fs : fs * 0.55f;
                if (w + cw > budget) break;
                w += cw;
                cut++;
            }
            if (cut <= 0) return "…" + tail;

            return head.Substring(0, cut) + "…" + tail;
        }

        /// <summary>按 <see cref="FitText"/> 同一套字宽规则估字符串像素宽。</summary>
        public static float TextWidth(string text, int fontSize)
        {
            if (string.IsNullOrEmpty(text)) return 0f;
            float fs = Mathf.Max(1, fontSize);
            float w = 0f;
            for (int i = 0; i < text.Length; i++)
                w += IsWideChar(text[i]) ? fs : fs * 0.55f;
            return w;
        }

        // ================================================================ 画布缩放策略

        /// <summary>
        /// 全项目画布缩放的统一口径：`ScaleWithScreenSize` + 参考 1000×720 + **Expand**。
        ///
        /// ── 为什么必须是 Expand（P29-3.6，修「面板消失 / 尺寸异常」）──────────
        /// 原来是 `MatchWidthOrHeight` + `matchWidthOrHeight = 0`（**只按宽度缩放**）。
        /// 那意味着画布高度完全由窗口宽高比决定：
        ///
        ///     scaleFactor = screenW / 1000
        ///     画布高     = screenH / scaleFactor = screenH × 1000 / screenW
        ///
        /// 1000×720 窗口下正好 720；但主理人用的 **1069×654 只有 611.8** ——
        /// 抽屉是按 720 排的，于是上下各溢 54px：头部 96 被裁掉上半（标题、关闭 × 看不见）、
        /// 工具行 80 被裁掉下半（数据源/录制/帮助/设置 看不见）、抽屉关不掉、触发钮回不来。
        ///
        /// Expand 的语义是 `scaleFactor = min(screenW/1000, screenH/720)`，
        /// 画布**逻辑尺寸只增不减**：
        ///
        /// | 窗口 | scaleFactor | 画布逻辑尺寸 |
        /// |---|---|---|
        /// | 1000×720 | 1.000 | 1000 × 720 |
        /// | 1069×654 | 0.908 | 1177 × **720** |
        ///
        /// 两种窗口高度都是 720，所有按 720 排的布局原样成立 —— **零布局常量改动**。
        /// 代价是窗口更扁时画布变宽（右侧多出空白），但那比裁掉内容好得多，
        /// 也正是 Expand（"扩张而不是裁切"）的设计意图。
        ///
        /// ── 为什么要运行时改而不是改场景 ────────────────────────────────
        /// 任务明确禁止动 `.unity`。运行时改 CanvasScaler 实例，场景里存的旧值不变，
        /// 但每次进 Play 都会被这里纠正 —— 不会出现"编辑器里看着对、进游戏就错"。
        /// 后续若要一次性落库，在编辑器里把它改成同样的值即可，本方法会自动跳过（见下面的早退）。
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        public static void ApplyCanvasPolicy()
        {
            Canvas main = FindMainOverlayCanvas();
            if (main == null)
            {
                Debug.LogWarning("[TwinUI] 没找到 ScreenSpaceOverlay 主画布，跳过缩放统一。" +
                                 "若界面比例异常，检查场景里的 [TwinUI] 是否被禁用或没建出来。");
                return;
            }

            CanvasScaler scaler = main.GetComponent<CanvasScaler>();
            if (scaler == null)
            {
                Debug.LogWarning("[TwinUI] 主画布 " + main.name + " 上没有 CanvasScaler，跳过缩放统一。");
                return;
            }

            if (scaler.uiScaleMode != CanvasScaler.ScaleMode.ScaleWithScreenSize) return;
            if (scaler.screenMatchMode == CanvasScaler.ScreenMatchMode.Expand) return;   // 已符合，不重复写

            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
            scaler.matchWidthOrHeight = 0f;   // Expand 下该值无效，归零避免下次有人误读

            Debug.Log("[TwinUI] 主画布缩放已切到 Expand（" + main.name +
                      "，参考 " + scaler.referenceResolution.x.ToString("0") + "×" +
                      scaler.referenceResolution.y.ToString("0") +
                      "）—— 画布逻辑高度不再随窗口宽高比缩水");
        }

        /// <summary>
        /// 取**面积最大**的 ScreenSpaceOverlay 画布。
        ///
        /// 【为什么不能取第一个】`FindObjectsOfType` 顺序不保证，工程里有若干子 Canvas
        /// （排序层、局部遮罩），取第一个会命中它们。上一轮探针就栽在这，
        /// 日志里报了 `Canvas.rect = 220×68` 这种假数，误导过排查。
        /// </summary>
        private static Canvas FindMainOverlayCanvas()
        {
            Canvas[] all = UnityEngine.Object.FindObjectsOfType<Canvas>(true);

            Canvas best = null;
            float bestArea = -1f;

            for (int i = 0; i < all.Length; i++)
            {
                Canvas c = all[i];
                if (c == null || c.renderMode != RenderMode.ScreenSpaceOverlay) continue;

                RectTransform rt = c.transform as RectTransform;
                if (rt == null) continue;

                float area = rt.rect.width * rt.rect.height;
                if (area > bestArea)
                {
                    bestArea = area;
                    best = c;
                }
            }

            return best;
        }
    }
}

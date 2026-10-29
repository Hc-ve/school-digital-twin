using UnityEngine;
using UnityEngine.UI;

namespace SchoolTwin.UI
{
    /// <summary>
    /// 【P29-4】空状态（§5.5）。给"这个模块还没接"的地方一个**能看懂的**占位，
    /// 而不是一个黑箱 + 一行"待接入"。
    ///
    /// ── 结构 ───────────────────────────────────────────────────────
    /// ```
    ///        ╭───╮         线框圆 40（1px Border + 内部一条 1px 斜线）
    ///        │ ╱ │         标题 13 TextSecondary
    ///        ╰───╯         说明 11 TextMuted（≤2 行，必须写清"什么时候有什么"）
    ///     该模块将在 P3 接入
    ///   接入后这里显示各房间用电排行
    ///      ● ○ ○          三点进度：8px，当前项 Accent，其余 白@0.10
    /// ```
    ///
    /// ── 为什么说明必须写时间 ─────────────────────────────────────────
    /// "暂未开放"这种话等于没说 —— 用户不知道要不要等、要不要换条路。
    /// 文案模板固定为「该模块将在 Px 接入」+「接入后这里显示什么」，
    /// 让空状态本身携带信息，而不是单纯的"空"。
    /// </summary>
    public sealed class TwinEmptyState : MonoBehaviour
    {
        public static TwinEmptyState Create(Transform parent, float x, float y, float w, float h,
                                            string title, string desc, int step, int stepCount)
        {
            GameObject go = new GameObject("EmptyState", typeof(RectTransform));
            RectTransform rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            TwinUIFactory.Place(rt, x, y, w, h);

            TwinEmptyState state = go.AddComponent<TwinEmptyState>();
            state.Build(w, title, desc, step, stepCount);
            return state;
        }

        private void Build(float w, string title, string desc, int step, int stepCount)
        {
            // ---- 线框圆 40：外圈 1px Border，内部一条 1px 斜线（"空盒子"的记号）----
            TwinRoundedRect ring = TwinUIFactory.Rounded(transform, "Ring", 20f,
                Color.clear, Color.clear, TwinTheme.Border, TwinTheme.StrokeHair.Width, false);
            TwinUIFactory.Place(ring.rectTransform, (w - 40f) * 0.5f, 0f, 40f, 40f);

            Image slash = TwinUIFactory.Block(ring.rectTransform, "Slash", TwinTheme.Border, false);
            RectTransform sr = slash.rectTransform;
            sr.anchorMin = new Vector2(0.5f, 0.5f);
            sr.anchorMax = new Vector2(0.5f, 0.5f);
            sr.pivot = new Vector2(0.5f, 0.5f);
            sr.sizeDelta = new Vector2(1f, 24f);
            sr.anchoredPosition = Vector2.zero;
            sr.localRotation = Quaternion.Euler(0f, 0f, -45f);

            // ---- 标题 13 ----
            Text t = TwinUIFactory.Label(transform, "Title", TwinTheme.FsBody,
                TextAnchor.UpperCenter, TwinTheme.TextSecondary);
            TwinUIFactory.Place(t.rectTransform, 0f, 56f, w, 20f);
            t.text = title;

            // ---- 说明 11（≤2 行）----
            Text d = TwinUIFactory.Label(transform, "Desc", TwinTheme.FsCaption,
                TextAnchor.UpperCenter, TwinTheme.TextMuted);
            TwinUIFactory.Place(d.rectTransform, 0f, 78f, w - 24f, 36f);
            d.horizontalOverflow = HorizontalWrapMode.Wrap;
            d.verticalOverflow = VerticalWrapMode.Overflow;
            d.text = desc;

            // ---- 三点进度：8px，当前项 Accent，其余 白@0.10 ----
            int count = Mathf.Max(1, stepCount);
            const float dot = 8f;
            const float gap = 12f;
            float total = count * dot + (count - 1) * gap;
            float x0 = (w - total) * 0.5f;

            for (int i = 0; i < count; i++)
            {
                // 未到的点用 Track（白@0.09，已有 token）—— 不为三个小点再引一个新的半透明值
                Color c = i == step ? TwinTheme.Accent : TwinTheme.Track;
                TwinRoundedRect p = TwinUIFactory.Rounded(transform, "Dot_" + i, dot * 0.5f,
                    c, c, Color.clear, 0f, false);
                TwinUIFactory.Place(p.rectTransform, x0 + i * (dot + gap), 126f, dot, dot);
            }
        }
    }
}

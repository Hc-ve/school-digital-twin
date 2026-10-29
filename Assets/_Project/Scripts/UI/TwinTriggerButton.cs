using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SchoolTwin.UI
{
    /// <summary>
    /// 【P28】总控入口钮 —— 屏幕**右上角**的图标钮，点击呼出收纳面板。
    ///
    /// ── 2026-10-29 改版：从"右缘竖条"改成"右上角图标钮" ──────────────
    /// 第一版做成了贴右缘、竖排三字「总控台」的长条。用户对照《异环》指出不对：
    /// 异环的功能入口是**右上角一排中等尺寸的图标**（纯图标、无文字、手机 App 图标质感），
    /// 点击才弹出收纳面板。这里改成同款：
    ///   · 48×48 圆角方块（radius 12，和图标格里的图标同一套圆角）
    ///   · 只放一个汉字，不带文字标签
    ///   · 右上角留 16 边距，和屏幕边缘保持呼吸
    ///   · 告警角标挂在钮的右上角
    ///
    /// ── 为什么不用图标字体/图片 ────────────────────────────────────
    /// 项目零美术资源，汉字「控」一眼能懂，且和面板里其他图标的做法一致。
    ///
    /// ── 为什么角标是"数量"而不是红点 ───────────────────────────────
    /// 运维需要知道"有几条"，不是"有没有" —— 2 条和 20 条看起来不该一样。
    /// </summary>
    public sealed class TwinTriggerButton : MonoBehaviour,
        IPointerEnterHandler, IPointerExitHandler, IPointerClickHandler
    {
        private const float Size = 48f;
        private const float MarginRight = 16f;
        private const float MarginTop = 16f;

        private RectTransform _root;
        private TwinRoundedRect _bg;
        private Text _glyph;
        private Text _badgeText;
        private RectTransform _badge;

        private bool _hover;
        private bool _open;
        private int _alarmCount = -1;

        private Action _onClick;

        // ---------------------------------------------------------------- 构建

        public static TwinTriggerButton Create(Transform parent, Action onClick)
        {
            GameObject go = new GameObject("TwinTriggerButton", typeof(RectTransform));
            go.transform.SetParent(parent, false);

            TwinTriggerButton btn = go.AddComponent<TwinTriggerButton>();
            btn._onClick = onClick;
            btn.Build();
            return btn;
        }

        private void Build()
        {
            _root = transform as RectTransform;

            // 右上角：anchor 右上 + pivot 右上，向右上各留 16
            _root.anchorMin = new Vector2(1f, 1f);
            _root.anchorMax = new Vector2(1f, 1f);
            _root.pivot = new Vector2(1f, 1f);
            _root.sizeDelta = new Vector2(Size, Size);
            _root.anchoredPosition = new Vector2(-MarginRight, -MarginTop);

            _bg = TwinUIFactory.Rounded(_root, "Bg", TwinTheme.R12,
                TwinTheme.WithAlpha(TwinTheme.Accent, 0.18f),
                TwinTheme.WithAlpha(TwinTheme.Accent, 0.10f),
                TwinTheme.WithAlpha(TwinTheme.Accent, 0.55f), 1f, true);
            TwinUIFactory.Stretch(_bg.rectTransform);

            _glyph = TwinUIFactory.Label(_root, "Glyph", 22,
                TextAnchor.MiddleCenter, TwinTheme.Accent);
            TwinUIFactory.Stretch(_glyph.rectTransform);
            _glyph.text = "控";

            // 告警角标：挂在钮的右上角（一半在钮外，视觉上"挂"在角上）
            TwinRoundedRect badgeBg = TwinUIFactory.Rounded(_root, "Badge", 8f,
                TwinTheme.StatusAlarm, TwinTheme.StatusAlarm,
                TwinTheme.WithAlpha(Color.white, 0.85f), 1f, false);
            _badge = badgeBg.rectTransform;
            _badge.anchorMin = _badge.anchorMax = new Vector2(1f, 1f);
            _badge.pivot = new Vector2(0.5f, 0.5f);
            _badge.sizeDelta = new Vector2(18f, 18f);
            _badge.anchoredPosition = new Vector2(2f, 2f);

            _badgeText = TwinUIFactory.Label(_badge, "N", 10, TextAnchor.MiddleCenter, Color.white);
            TwinUIFactory.Stretch(_badgeText.rectTransform);

            badgeBg.gameObject.SetActive(false);
        }

        // ---------------------------------------------------------------- 对外

        public void SetAlarmCount(int count)
        {
            if (count == _alarmCount) return;
            _alarmCount = count;

            bool show = count > 0;
            if (_badge != null && _badge.gameObject.activeSelf != show)
                _badge.gameObject.SetActive(show);

            if (show && _badgeText != null)
            {
                string t = count > 99 ? "99+" : count.ToString();
                if (!string.Equals(_badgeText.text, t, StringComparison.Ordinal)) _badgeText.text = t;
                if (_badge != null) _badge.sizeDelta = new Vector2(count > 9 ? 24f : 18f, 18f);
            }
        }

        /// <summary>面板打开时钮变成"收起"语义（实心高亮）。</summary>
        public void SetOpen(bool open)
        {
            if (_open == open) return;
            _open = open;
            ApplyVisual();
        }

        // ---------------------------------------------------------------- 视觉

        private void ApplyVisual()
        {
            if (_bg == null) return;

            if (_open)
            {
                // 打开态：实心青蓝，一眼看出"再点一次会收起"
                _bg.SetFill(TwinTheme.Accent, Color.Lerp(TwinTheme.Accent, Color.black, 0.25f));
                _bg.SetBorder(TwinTheme.WithAlpha(Color.white, 0.55f), 1f);
                if (_glyph != null) _glyph.color = new Color(0.04f, 0.08f, 0.12f, 1f);
                return;
            }

            float boost = _hover ? 0.10f : 0f;
            _bg.SetFill(TwinTheme.WithAlpha(TwinTheme.Accent, 0.18f + boost),
                        TwinTheme.WithAlpha(TwinTheme.Accent, 0.10f + boost));
            _bg.SetBorder(TwinTheme.WithAlpha(TwinTheme.Accent, _hover ? 0.85f : 0.55f), 1f);
            if (_glyph != null) _glyph.color = TwinTheme.Accent;
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            if (_hover) return;
            _hover = true;
            ApplyVisual();
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            if (!_hover) return;
            _hover = false;
            ApplyVisual();
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left) return;
            Action cb = _onClick;
            if (cb != null) cb();
        }
    }
}

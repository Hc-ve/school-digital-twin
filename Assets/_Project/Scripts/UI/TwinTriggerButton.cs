using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SchoolTwin.UI
{
    /// <summary>
    /// 【P28】右缘触发钮：抽屉的唯一入口，兼告警角标。
    ///
    /// ── 为什么贴在屏幕右缘、垂直居中 ────────────────────────────────
    /// 参考《异环》的手机式面板是从右侧呼出的，触发钮在右缘符合心理模型。
    /// 垂直居中而不是右上角：右上角要留给告警/小地图，且居中在 16:9 下离手最近。
    ///
    /// ── 为什么悬停要往外伸 8 单位 ──────────────────────────────────
    /// 贴边元素如果完全静止，玩家会觉得"点不动"。伸出一点点是最便宜的"可交互"暗示，
    /// 比改颜色更直观（而且不会和"选中"的青蓝混淆）。
    ///
    /// ── 为什么角标是"数量"而不是红点 ───────────────────────────────
    /// 调研案例 2/5 的经验：运维需要知道"有几条"，不是"有没有"。
    /// 只有 1 个红点的话，2 条告警和 20 条告警看起来一样。
    /// </summary>
    public sealed class TwinTriggerButton : MonoBehaviour,
        IPointerEnterHandler, IPointerExitHandler, IPointerClickHandler
    {
        private const float Width = 44f;
        private const float Height = 132f;
        private const float HoverExtend = 8f;

        private RectTransform _root;
        private TwinRoundedRect _bg;
        private Text _glyph;
        private Text _badgeText;
        private RectTransform _badge;

        private bool _hover;
        private float _extend;          // 当前外伸量（0 ~ HoverExtend）
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

            // 贴右缘：anchor 右上，pivot 右中，x = 0 表示紧贴边
            _root.anchorMin = new Vector2(1f, 0.5f);
            _root.anchorMax = new Vector2(1f, 0.5f);
            _root.pivot = new Vector2(1f, 0.5f);
            _root.sizeDelta = new Vector2(Width, Height);
            _root.anchoredPosition = Vector2.zero;

            // 只有左侧圆角看起来像"从边上长出来的"
            _bg = TwinUIFactory.Rounded(_root, "Bg", TwinTheme.R12,
                TwinTheme.PanelTop, TwinTheme.PanelBottom, TwinTheme.Border, 1f, true);
            TwinUIFactory.Stretch(_bg.rectTransform);

            // 竖排三字："总控台"
            _glyph = TwinUIFactory.Label(_root, "Glyph", TwinTheme.FsBodyStrong,
                TextAnchor.MiddleCenter, TwinTheme.TextSecondary);
            TwinUIFactory.Stretch(_glyph.rectTransform);
            _glyph.text = "总\n控\n台";
            _glyph.lineSpacing = 1.15f;

            // 告警角标：左上角的小圆 + 数字
            TwinRoundedRect badgeBg = TwinUIFactory.Rounded(_root, "Badge", 9f,
                TwinTheme.StatusAlarm, TwinTheme.StatusAlarm,
                TwinTheme.WithAlpha(Color.white, 0.8f), 1f, false);
            _badge = badgeBg.rectTransform;
            _badge.anchorMin = _badge.anchorMax = new Vector2(0f, 1f);
            _badge.pivot = new Vector2(0.5f, 0.5f);
            _badge.sizeDelta = new Vector2(18f, 18f);
            _badge.anchoredPosition = new Vector2(6f, -10f);

            _badgeText = TwinUIFactory.Label(_badge, "N", TwinTheme.FsCaption,
                TextAnchor.MiddleCenter, Color.white);
            TwinUIFactory.Stretch(_badgeText.rectTransform);

            badgeBg.gameObject.SetActive(false);
        }

        // ---------------------------------------------------------------- 对外

        /// <summary>更新告警角标。0 或负数 = 隐藏。</summary>
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
                // 两位数要宽一点，不然挤
                if (_badge != null) _badge.sizeDelta = new Vector2(count > 9 ? 24f : 18f, 18f);
            }
        }

        /// <summary>抽屉打开时按钮变"收起"语义（视觉上高亮）。</summary>
        public void SetOpen(bool open)
        {
            if (_bg == null) return;
            if (open)
            {
                _bg.SetFill(TwinTheme.AccentDim);
                _bg.SetBorder(TwinTheme.AccentLine, 1f);
                if (_glyph != null) _glyph.color = TwinTheme.Accent;
            }
            else
            {
                _bg.SetFill(TwinTheme.PanelTop, TwinTheme.PanelBottom);
                _bg.SetBorder(TwinTheme.Border, 1f);
                if (_glyph != null) _glyph.color = TwinTheme.TextSecondary;
            }
        }

        // ---------------------------------------------------------------- 悬停外伸

        private void Update()
        {
            float target = _hover ? HoverExtend : 0f;
            if (Mathf.Abs(_extend - target) < 0.05f) return;

            // 120ms 内走完，用 unscaledDeltaTime（暂停时也要能点）
            _extend = Mathf.MoveTowards(_extend, target,
                HoverExtend * Time.unscaledDeltaTime / TwinTheme.Fast);

            // 贴右缘：pivot 在右边，x 往左为负 → 外伸 = 往左挪
            if (_root != null) _root.anchoredPosition = new Vector2(-_extend, 0f);
        }

        public void OnPointerEnter(PointerEventData eventData) { _hover = true; }

        public void OnPointerExit(PointerEventData eventData) { _hover = false; }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left) return;
            Action cb = _onClick;
            if (cb != null) cb();
        }
    }
}

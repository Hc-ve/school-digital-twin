using System;
using System.Collections.Generic;
using UnityEngine;

namespace SchoolTwin.UI
{
    /// <summary>
    /// 【P28】抽屉内部的页栈。
    ///
    /// ── 为什么是"栈"而不是"多个页面并存" ──────────────────────────
    /// 现状最大的病就是没有导航：F4/G/T/H 四个窗口能同时开、互相压。
    /// 栈的语义只有一个出口（栈顶出栈），不需要定义"A 和 B 同时开怎么办"。
    ///
    /// ── 动画只做"入场" ────────────────────────────────────────────
    /// 标准的"旧页左滑出 + 新页右滑入"要同时跑两条 tween，状态机复杂一倍，
    /// 而 160ms 内人眼几乎看不出差别。这里旧页直接隐藏、新页滑入 + 淡入，
    /// 观感足够，代码只有一半。
    /// </summary>
    public sealed class TwinPageStack : MonoBehaviour
    {
        private const float SlideDistance = 32f;
        private const float SlideSeconds = 0.20f;

        private RectTransform _content;
        private readonly List<TwinPageBase> _stack = new List<TwinPageBase>(4);

        // 入场动画状态
        private RectTransform _animating;
        private CanvasGroup _animGroup;
        private float _animStart;

        /// <summary>栈变化时触发（抽屉用它更新标题/返回键）。</summary>
        public event Action Changed;

        public bool IsEmpty { get { return _stack.Count == 0; } }

        public TwinPageBase Top
        {
            get { return _stack.Count > 0 ? _stack[_stack.Count - 1] : null; }
        }

        public int Depth { get { return _stack.Count; } }

        public static TwinPageStack Create(Transform parent)
        {
            GameObject go = new GameObject("PageStack", typeof(RectTransform));
            go.transform.SetParent(parent, false);

            TwinPageStack stack = go.AddComponent<TwinPageStack>();
            stack._content = go.transform as RectTransform;
            TwinUIFactory.Stretch(stack._content);
            return stack;
        }

        // ---------------------------------------------------------------- 栈操作

        /// <summary>压入一页。同类型页面不会重复压（避免连点图标压出一堆同名页）。</summary>
        public void Push(TwinPageBase page)
        {
            if (page == null) return;

            TwinPageBase top = Top;
            if (top != null)
            {
                if (top.GetType() == page.GetType())
                {
                    // 已经在看同一页了 —— 不动栈，只把它拉到栈顶刷新一次。
                    // 【P29-4 P0-2】这里必须补一次归 1：如果这一页上次的入场动画
                    // 被打断（alpha 停在 0.3~0.6），早退分支不会重跑动画，
                    // 它就会**永久半透明**，透出走廊和角色。
                    ForceOpaque(top);
                    top.OnShow();
                    return;
                }
                top.OnHide();
                top.gameObject.SetActive(false);
            }

            RectTransform rt = page.transform as RectTransform;
            if (rt != null && rt.parent != _content) rt.SetParent(_content, false);
            if (rt != null) TwinUIFactory.Stretch(rt);

            page.gameObject.SetActive(true);
            _stack.Add(page);
            page.OnShow();

            StartSlideIn(page);
            RaiseChanged();
        }

        /// <summary>弹出栈顶。返回 false 表示已经到底了（调用方应该去关抽屉）。</summary>
        public bool Pop()
        {
            if (_stack.Count == 0) return false;

            TwinPageBase top = _stack[_stack.Count - 1];
            _stack.RemoveAt(_stack.Count - 1);

            if (top != null)
            {
                top.OnHide();
                top.gameObject.SetActive(false);
            }

            TwinPageBase next = Top;
            if (next != null)
            {
                next.gameObject.SetActive(true);
                next.OnShow();
                StartSlideIn(next);
            }

            RaiseChanged();
            return true;
        }

        /// <summary>清空（关抽屉时调）。</summary>
        public void Clear()
        {
            for (int i = _stack.Count - 1; i >= 0; i--)
            {
                TwinPageBase p = _stack[i];
                if (p == null) continue;
                p.OnHide();
                p.gameObject.SetActive(false);
            }
            _stack.Clear();
            _animating = null;
            RaiseChanged();
        }

        private void RaiseChanged()
        {
            Action h = Changed;
            if (h != null) h();
        }

        // ---------------------------------------------------------------- 入场动画

        private void StartSlideIn(TwinPageBase page)
        {
            RectTransform rt = page.transform as RectTransform;
            if (rt == null) return;

            // 【P29-4 P0-2 根因】`_animGroup` 是**单个字段**：新一次入场会覆盖它，
            // 上一位的动画就再也没人收尾，alpha 永久停在半透明。
            // 症状是"面板整体发虚、能透过它看到走廊和角色"，且**换页越频繁越严重**。
            // 修法：每次入场前先把栈内所有页归 1，再给新页从 0 起动画 —— 这样
            // 任何被打断的中间态都会被下一次入场顺手抹平。
            ForceAllOpaque();

            CanvasGroup group = page.GetComponent<CanvasGroup>();
            if (group == null) group = page.gameObject.AddComponent<CanvasGroup>();

            _animating = rt;
            _animGroup = group;
            _animStart = Time.unscaledTime;

            rt.anchoredPosition = new Vector2(SlideDistance, 0f);
            group.alpha = 0f;
        }

        /// <summary>把某一页强制置为不透明（不动它的位置）。</summary>
        private static void ForceOpaque(TwinPageBase page)
        {
            if (page == null) return;
            CanvasGroup g = page.GetComponent<CanvasGroup>();
            if (g != null) g.alpha = 1f;
        }

        /// <summary>栈内所有页归 1。入场动画前调用，抹平上一次被打断的中间态。</summary>
        private void ForceAllOpaque()
        {
            for (int i = 0; i < _stack.Count; i++) ForceOpaque(_stack[i]);
        }

        private void Update()
        {
            if (_animating == null) return;

            float t = (Time.unscaledTime - _animStart) / SlideSeconds;
            if (t >= 1f)
            {
                _animating.anchoredPosition = Vector2.zero;
                if (_animGroup != null) _animGroup.alpha = 1f;
                _animating = null;
                _animGroup = null;
                return;
            }

            float e = TwinTheme.EaseOutCubic(t);
            _animating.anchoredPosition = new Vector2(Mathf.Lerp(SlideDistance, 0f, e), 0f);
            if (_animGroup != null) _animGroup.alpha = e;
        }
    }
}

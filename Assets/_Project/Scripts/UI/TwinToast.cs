using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace SchoolTwin.UI
{
    /// <summary>
    /// 【P28】轻提示：底部浮出一条短消息，2 秒后自动淡出。
    ///
    /// ── 为什么需要 ──────────────────────────────────────────────────
    /// 操作反馈现在只有 Console 日志（`[TwinCmd] ...`），用户看不到。
    /// 典型场景：命令被拒（"设备已断电"）、批量操作完成、复制编号成功。
    ///
    /// ── 为什么是"队列"而不是"覆盖" ──────────────────────────────────
    /// 批量开 20 台设备会瞬间产生 20 条反馈，覆盖式只看到最后一条。
    /// 队列 + 单条 2 秒，最多积压 4 条（超了丢最老），既不会刷屏也不会丢关键信息。
    ///
    /// ── 为什么不用 Unity 的 Debug.Log 上屏 ──────────────────────────
    /// 那要 OnGUI，和 F3/F6 的 IMGUI 叠在一起，且样式完全不可控。
    /// </summary>
    public sealed class TwinToast : MonoBehaviour
    {
        private const float ShowSeconds = 2.0f;
        private const float FadeSeconds = 0.25f;
        private const int MaxQueue = 4;

        private static TwinToast _instance;

        private RectTransform _root;       // 当前这条
        private TwinRoundedRect _bg;
        private Text _label;
        private CanvasGroup _group;

        private readonly Queue<string> _queue = new Queue<string>(8);
        private float _shownAt = -1f;

        public static TwinToast Ensure(Transform parent)
        {
            if (_instance != null) return _instance;

            GameObject go = new GameObject("TwinToast", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            _instance = go.AddComponent<TwinToast>();
            _instance.Build();
            return _instance;
        }

        private void Build()
        {
            _root = transform as RectTransform;
            _root.anchorMin = new Vector2(0.5f, 0f);
            _root.anchorMax = new Vector2(0.5f, 0f);
            _root.pivot = new Vector2(0.5f, 0f);
            _root.anchoredPosition = new Vector2(0f, 96f);
            _root.sizeDelta = new Vector2(360f, 34f);

            _bg = TwinUIFactory.Rounded(_root, "Bg", TwinTheme.Pill(34f),
                TwinTheme.ScrimBase, TwinTheme.ScrimBase,
                TwinTheme.WithAlpha(TwinTheme.Accent, 0.45f), 1f, false);
            TwinUIFactory.Stretch(_bg.rectTransform);

            _label = TwinUIFactory.Label(_root, "Text", TwinTheme.FsBody,
                TextAnchor.MiddleCenter, TwinTheme.TextPrimary);
            TwinUIFactory.Place(_label.rectTransform, 12f, 0f, 336f, 34f);

            _group = gameObject.AddComponent<CanvasGroup>();
            _group.alpha = 0f;
            _group.blocksRaycasts = false;
            _group.interactable = false;
        }

        /// <summary>弹一条提示。同一条文案在 0.4 秒内重复出现会被合并（批量操作常见）。</summary>
        public static void Show(string message)
        {
            if (_instance == null || string.IsNullOrEmpty(message)) return;
            _instance.Enqueue(message);
        }

        private void Enqueue(string message)
        {
            if (_queue.Count >= MaxQueue) _queue.Dequeue();
            _queue.Enqueue(message);

            // 当前没有在展示 → 立刻起一条
            if (_shownAt < 0f) Next();
        }

        private void Next()
        {
            if (_queue.Count == 0) { _shownAt = -1f; return; }

            string msg = _queue.Dequeue();
            if (_label != null)
            {
                _label.text = TwinUIFactory.Truncate(msg, TwinTheme.FsBody, 320f);
            }
            _shownAt = Time.unscaledTime;
        }

        private void Update()
        {
            if (_group == null) return;

            if (_shownAt < 0f)
            {
                if (_group.alpha > 0f)
                    _group.alpha = Mathf.MoveTowards(_group.alpha, 0f, Time.unscaledDeltaTime / FadeSeconds);
                return;
            }

            float age = Time.unscaledTime - _shownAt;

            // 前 0.12s 淡入，最后 0.25s 淡出，中间满不透明
            float target;
            if (age < 0.12f) target = age / 0.12f;
            else if (age > ShowSeconds) target = 0f;
            else target = 1f;

            _group.alpha = Mathf.MoveTowards(_group.alpha, target,
                Time.unscaledDeltaTime / Mathf.Max(0.01f, FadeSeconds));

            if (age > ShowSeconds + FadeSeconds) Next();
        }

        /// <summary>场景切换时清掉单例引用（静态引用会跨场景残留，变成 Unity 的"假 null"）。</summary>
        public static void ResetInstance()
        {
            _instance = null;
        }
    }
}

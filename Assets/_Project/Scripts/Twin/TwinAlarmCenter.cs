using System;
using System.Collections.Generic;
using UnityEngine;
using SchoolTwin.Core;
using SchoolTwin.UI;

namespace SchoolTwin.Twin
{
    /// <summary>
    /// 集中式告警中心：把「几百个锚点各自的状态」汇总成一条告警事件流。
    ///
    /// ── 为什么需要它 ──────────────────────────────────────────────
    /// TwinAnchor 只有 per-anchor 的 StatusChanged。音效、闪烁、日志、上报
    /// 若各自遍历 Registry 判断，会重复计算，而且时序对不齐
    /// （出现「响了但没闪」这种诡异画面）。这里做成唯一的事件源，
    /// 所有「告警输出」都订阅它。
    ///
    /// ── 为什么用轮询而不是订阅 StatusChanged ──────────────────────
    /// 锚点是动态注册的（运行时可增删）。订阅需要在注册时逐个挂钩、
    /// 销毁时逐个摘钩，漏一处就是泄漏或空引用。
    /// 轮询只需每 0.2 秒遍历一次 Registry.All —— 500 点位也只是
    /// 2500 次属性读取/秒，相对 60fps 的渲染开销可以忽略。
    ///
    /// ── 去重策略（防「交响乐」）──────────────────────────────────
    /// · 只在「从不需关注 → 需关注」的跃迁时广播（IsNew = true）
    /// · 持续 Alarm / Offline 时每 _repeatSeconds 再提醒一次（怕人没听见）
    /// · Warning 只在跃迁时提示，不重复 —— 它太常见，重复会让人麻木
    ///
    /// ⚠️ 判定口径必须与 TwinStatusPalette 一致（它是状态语义的唯一来源）。
    ///    自己再写一份 if 判断，早晚会和面板/着色漂移。
    /// </summary>
    [AddComponentMenu("SchoolTwin/告警中心 (TwinAlarmCenter)")]
    [DisallowMultipleComponent]
    public class TwinAlarmCenter : MonoBehaviour
    {
        /// <summary>一次告警事件。</summary>
        public struct AlarmEvent
        {
            public TwinAnchor Anchor;
            /// <summary>新状态（Warning / Alarm / Stale / Offline）</summary>
            public TwinStatus Status;
            /// <summary>跃迁前的状态</summary>
            public TwinStatus Previous;
            /// <summary>true = 刚发生；false = 持续告警的重复提醒</summary>
            public bool IsNew;
        }

        public static TwinAlarmCenter Instance { get; private set; }

        /// <summary>进入或持续「需关注」状态。参数：事件详情。</summary>
        public event Action<AlarmEvent> AlarmRaised;

        /// <summary>从「需关注」恢复为正常。参数：恢复的点位。</summary>
        public event Action<TwinAnchor> AlarmCleared;

        [Header("扫描")]
        [Tooltip("轮询间隔（秒）。0.2 秒足够跟上 2Hz 的数据刷新")]
        [SerializeField] private float _pollInterval = 0.2f;

        [Header("去重")]
        [Tooltip("持续告警时，每隔多少秒再提醒一次（只对 Alarm / Offline）")]
        [SerializeField] private float _repeatSeconds = 30f;

        [Tooltip("Warning 是否也广播事件。关掉则只有 Alarm / Offline / Stale 会触发")]
        [SerializeField] private bool _raiseOnWarning = true;

        private struct Entry
        {
            public TwinStatus Status;
            public float LastRaisedRealtime;
        }

        private readonly Dictionary<TwinAnchor, Entry> _states =
            new Dictionary<TwinAnchor, Entry>(64);
        private readonly List<TwinAnchor> _dead = new List<TwinAnchor>(16);
        private readonly HashSet<TwinAnchor> _alive = new HashSet<TwinAnchor>();

        private float _nextPoll;
        private float _nextPrune;

        /// <summary>正在跟踪的点位数（调试用）。</summary>
        public int TrackedCount { get { return _states.Count; } }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning("[Twin] 场景里有多个 TwinAlarmCenter，保留第一个：" + Instance.name);
                enabled = false;
                return;
            }
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private void Update()
        {
            float now = Time.realtimeSinceStartup;
            if (now < _nextPoll) return;
            _nextPoll = now + Mathf.Max(0.05f, _pollInterval);

            TwinRuntime runtime = TwinRuntime.Instance;
            if (runtime == null || runtime.Registry == null) return;

            IReadOnlyList<TwinAnchor> all = runtime.Registry.All;
            for (int i = 0; i < all.Count; i++)
            {
                TwinAnchor anchor = all[i];
                if (anchor == null) continue;
                Evaluate(anchor, now);
            }

            if (now >= _nextPrune)
            {
                _nextPrune = now + 5f;
                Prune(all);
            }
        }

        private void Evaluate(TwinAnchor anchor, float now)
        {
            TwinStatus status = CurrentStatus(anchor);

            Entry entry;
            bool known = _states.TryGetValue(anchor, out entry);
            TwinStatus prev = known ? entry.Status : TwinStatus.Unknown;

            if (!known || prev != status)
            {
                entry.Status = status;

                // 严重度升高时重置计时，让它立刻响。
                // 否则「Warning → Alarm」这条升级路径会被 isNew 判成"不是新事件"，
                // 又被 30 秒去重挡住 —— 结果最该响的一次偏偏不响。
                if (!known || TwinStatusPalette.Severity(status) > TwinStatusPalette.Severity(prev))
                    entry.LastRaisedRealtime = 0f;

                _states[anchor] = entry;
            }

            bool attention = TwinStatusPalette.NeedsAttention(status);
            bool prevAttention = known && TwinStatusPalette.NeedsAttention(prev);

            if (!attention)
            {
                if (prevAttention) RaiseCleared(anchor);
                return;
            }

            if (status == TwinStatus.Warning && !_raiseOnWarning) return;

            bool isNew = !prevAttention;
            bool repeatable = status == TwinStatus.Alarm || status == TwinStatus.Offline;
            bool due = isNew
                       || (repeatable && now - entry.LastRaisedRealtime >= Mathf.Max(1f, _repeatSeconds));
            if (!due) return;

            entry.LastRaisedRealtime = now;
            _states[anchor] = entry;

            AlarmEvent evt;
            evt.Anchor = anchor;
            evt.Status = status;
            evt.Previous = prev;
            evt.IsNew = isNew;

            Action<AlarmEvent> handler = AlarmRaised;
            if (handler == null) return;

            // 包 try-catch：一个订阅者抛异常不能连累其他订阅者
            // （P2-7 踩过 —— Register 抛异常导致后面的 Subscribe 根本没执行）
            try { handler(evt); }
            catch (Exception e) { Debug.LogError("[Twin] 告警事件处理异常：" + e); }
        }

        private void RaiseCleared(TwinAnchor anchor)
        {
            Action<TwinAnchor> handler = AlarmCleared;
            if (handler == null) return;
            try { handler(anchor); }
            catch (Exception e) { Debug.LogError("[Twin] 告警恢复事件处理异常：" + e); }
        }

        /// <summary>
        /// 清理已销毁锚点的记录。
        /// 不清理会一直持有引用 —— Unity 的「假 null」让 TryGetValue 仍然命中，
        /// 表现为字典越来越大，且每轮都遍历这些死条目。
        /// </summary>
        private void Prune(IReadOnlyList<TwinAnchor> all)
        {
            _alive.Clear();
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i] != null) _alive.Add(all[i]);
            }

            _dead.Clear();
            foreach (KeyValuePair<TwinAnchor, Entry> kv in _states)
            {
                if (!_alive.Contains(kv.Key)) _dead.Add(kv.Key);
            }

            for (int i = 0; i < _dead.Count; i++) _states.Remove(_dead[i]);
        }

        /// <summary>与面板 / 着色同口径：整体状态 + 沉默判定。</summary>
        private static TwinStatus CurrentStatus(TwinAnchor anchor)
        {
            TwinStatus status = anchor.Status;
            if (anchor.IsStale && status != TwinStatus.Offline) status = TwinStatus.Stale;
            return status;
        }

        [ContextMenu("打印当前告警状态")]
        private void PrintStates()
        {
            Debug.Log("[Twin] 告警中心跟踪 " + _states.Count + " 个点位");
            foreach (KeyValuePair<TwinAnchor, Entry> kv in _states)
            {
                if (!TwinStatusPalette.NeedsAttention(kv.Value.Status)) continue;
                string id = kv.Key != null ? kv.Key.TwinId : "(已销毁)";
                Debug.Log("    " + id + "  " + TwinStatusPalette.Label(kv.Value.Status));
            }
        }
    }
}

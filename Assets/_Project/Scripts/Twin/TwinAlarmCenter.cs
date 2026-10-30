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

            /// <summary>
            /// 是否因为「回路断电」被抑制。
            ///
            /// 【为什么必须有这个位，不能只看状态】断电期间我们把这个点位记成 Unknown，
            /// 而 Unknown 和「本来就没数据」在状态上是同一个值。没有这个位，
            /// 供电恢复后就没法区分"要重新判一次"和"一直都在 Unknown"，
            /// 结果是要么恢复后不重新判（灯亮了但面板还是灰的），要么每轮都清一次（白跑）。
            /// </summary>
            public bool Suppressed;
        }

        private readonly Dictionary<TwinAnchor, Entry> _states =
            new Dictionary<TwinAnchor, Entry>(64);
        private readonly List<TwinAnchor> _dead = new List<TwinAnchor>(16);
        private readonly HashSet<TwinAnchor> _alive = new HashSet<TwinAnchor>();

        private float _nextPoll;
        private float _nextPrune;

        /// <summary>当前因断电被抑制的点位数。面板用它显示「断电抑制 N 个」。</summary>
        private int _suppressedCount;

        /// <summary>正在跟踪的点位数（调试用）。</summary>
        public int TrackedCount { get { return _states.Count; } }

        /// <summary>因回路断电被抑制的点位数（这些点位此刻不会报警，是**设计如此**）。</summary>
        public int SuppressedCount { get { return _suppressedCount; } }

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

            // 【P29-3.6】设备指标快照，与告警判定共用这一轮 0.2s 遍历。
            //
            // 为什么挂在这里而不是新开一个 Update：
            //   ① 本方法本来就在遍历 Registry.All，顺手算 4 个计数不增加任何遍历成本；
            //   ② 新开一个 Update 会多一遍 53 次属性读，且与告警判定的时序错开，
            //      会出现「告警卡显示 2、告警中心列表 3 条」这种对不上的画面。
            //
            // ⚠️ Rebuild 内部**不读**本类的 Entry.Status —— 那是断电抑制后的状态，
            //    拿它统计会让「拉一次闸 = 全部设备离线」。详见 TwinDeviceStats 类注释。
            TwinDeviceStats.Rebuild();

            if (now >= _nextPrune)
            {
                _nextPrune = now + 5f;
                Prune(all);
            }
        }

        private void Evaluate(TwinAnchor anchor, float now)
        {
            Entry entry;
            bool known = _states.TryGetValue(anchor, out entry);

            // ---- 断电抑制（P15）----
            //
            // 【为什么必须挡在一切之前】断电后设备沉默 8 秒，状态会自己走到 Offline，
            // 于是整层楼的设备同时"离线告警"—— 一次断电制造 15 条告警 + 15 声提示音，
            // 而真正的事实只有一条：有人把闸拉了。这就是 Spec 里"断电期间不产生新告警"
            // 那条验收项要挡的东西。
            //
            // 已经在报的那条也要收掉（走 Cleared），因为对运维来说"这层断电了"是**已知状态**，
            // 不是一条待处理的异常。留着一屏红色，人反而看不见别的。
            if (!TwinCircuit.IsPowered(anchor.TwinId))
            {
                if (!known)
                {
                    entry.Status = TwinStatus.Unknown;
                    entry.LastRaisedRealtime = 0f;
                    entry.Suppressed = true;
                    _states[anchor] = entry;
                    _suppressedCount++;
                    return;
                }
                if (!entry.Suppressed)
                {
                    bool wasAttention = TwinStatusPalette.NeedsAttention(entry.Status);
                    entry.Suppressed = true;
                    entry.Status = TwinStatus.Unknown;
                    entry.LastRaisedRealtime = 0f;
                    _states[anchor] = entry;
                    _suppressedCount++;
                    if (wasAttention) RaiseCleared(anchor);
                }
                return;
            }

            // ---- 供电恢复 ----
            // 清掉抑制位，让下一段按数据重新判一次。重置成 Unknown 而不是保留旧值，
            // 是为了让"恢复后的第一个真实状态"一定被当成新事件处理（哪怕它还是 Offline）。
            if (known && entry.Suppressed)
            {
                entry.Suppressed = false;
                entry.Status = TwinStatus.Unknown;
                entry.LastRaisedRealtime = 0f;
                _states[anchor] = entry;
                if (_suppressedCount > 0) _suppressedCount--;
            }

            TwinStatus status = CurrentStatus(anchor);
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

            for (int i = 0; i < _dead.Count; i++)
            {
                // 被抑制的点位销毁时也要把计数减回来，否则「断电抑制 N 个」只增不减，
                // 面板上会一直挂着一个不存在的数字。
                Entry e;
                if (_states.TryGetValue(_dead[i], out e) && e.Suppressed && _suppressedCount > 0)
                    _suppressedCount--;
                _states.Remove(_dead[i]);
            }
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

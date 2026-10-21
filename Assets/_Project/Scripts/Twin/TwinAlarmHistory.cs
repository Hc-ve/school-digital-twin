using System;
using System.Collections.Generic;
using UnityEngine;
using SchoolTwin.Core;
using SchoolTwin.UI;

namespace SchoolTwin.Twin
{
    /// <summary>
    /// 告警历史：把 <see cref="TwinAlarmCenter"/> 的事件流记成「一条异常从发生到结束」。
    ///
    /// ── 为什么需要它 ──────────────────────────────────────────────
    /// 告警面板只回答「**现在**谁在报警」。32 个点位一起跑时，运维真正要问的是
    /// 「这个报警**报了多久**了」「刚才那阵过去了吗」「今天一共出过几次」——
    /// 这三个问题都需要时间维度，而 AlarmCenter 是个无状态的事件源（它只负责「响」）。
    ///
    /// ── 与 AlarmCenter 的分工（不要合并）──────────────────────────
    /// · AlarmCenter：**无状态**。每 0.2s 扫一遍，判断「现在该不该响」。
    ///   它的 AlarmRaised 会为了提醒重复发（Alarm/Offline 每 30s 一次）。
    /// · TwinAlarmHistory：**有状态**。把「重复提醒」折叠进同一条记录，
    ///   只关心「这一次异常是什么时候开始的、什么时候结束的、最严重到哪一步」。
    /// 所以同一个 AlarmRaised 里 IsNew=false 的重复提醒，在这里必须去重 ——
    /// 否则一次 10 分钟的告警会在历史里变成 20 条。
    ///
    /// ── 三个容易错的地方 ──────────────────────────────────────────
    /// ① **升级要并进同一条记录**：Warning→Alarm 时 AlarmCenter 不会先发 Cleared，
    ///    而是直接再发一次 Raised。若按「没记录就新建」，一次异常会被切成两条。
    /// ② **持续时间必须用 realtimeSinceStartup，且中途不重置**：它不受 timeScale 影响，
    ///    也不会因为场景暂停而停走。用 Time.time 在暂停时会给出错误的时长。
    /// ③ **抖动抑制**：数据源切换、网络抖一下都会让点位短暂 Stale。
    ///    短于 <c>_minSeconds</c> 的异常不写进历史，也不计入「累计次数」——
    ///    否则切一次数据源就能刷出 32 条「离线」，历史面板全是噪声。
    ///    注意：抑制只作用于**历史**，不影响告警列表和告警音（那两处该响还是响）。
    /// </summary>
    [AddComponentMenu("SchoolTwin/告警历史 (TwinAlarmHistory)")]
    [DisallowMultipleComponent]
    public class TwinAlarmHistory : MonoBehaviour
    {
        /// <summary>一条完整的异常记录（从发生到恢复）。</summary>
        public struct AlarmRecord
        {
            public string TwinId;
            public string DisplayName;
            public string Zone;
            /// <summary>本次异常期间达到的最高严重度（Warning→Alarm 时会被抬上来）。</summary>
            public TwinStatus PeakStatus;
            /// <summary>发生时刻的墙钟时间，HH:mm:ss。历史面板要显示「几点几分」。</summary>
            public string Clock;
            /// <summary>开始时刻（Time.realtimeSinceStartup）。</summary>
            public float StartTime;
            /// <summary>结束时刻；0 表示仍在持续。</summary>
            public float EndTime;

            public bool Ongoing { get { return EndTime <= 0f; } }

            /// <summary>持续时长（秒）。仍在持续的按「到现在为止」算。</summary>
            public float Duration(float now)
            {
                float end = Ongoing ? now : EndTime;
                float d = end - StartTime;
                return d > 0f ? d : 0f;
            }
        }

        public static TwinAlarmHistory Instance { get; private set; }

        [Header("容量")]
        [Tooltip("最多保留多少条已结束的记录。超出后丢最旧的（仍在持续的永远不会被丢）")]
        [SerializeField] private int _capacity = 200;

        [Header("抖动抑制")]
        [Tooltip("持续时长短于这个值（秒）的异常不写进历史，也不计入累计次数")]
        [SerializeField] private float _minSeconds = 2f;

        private TwinAlarmCenter _center;

        /// <summary>仍在持续的异常：锚点 → 记录。用锚点做键是为了 O(1) 查「这个点位报了多久」。</summary>
        private readonly Dictionary<TwinAnchor, AlarmRecord> _ongoing =
            new Dictionary<TwinAnchor, AlarmRecord>(64);

        /// <summary>已结束的记录，按结束先后追加（越靠后越新）。</summary>
        private readonly List<AlarmRecord> _closed = new List<AlarmRecord>(64);

        /// <summary>已结束且被保留的记录数（历史面板的「累计 N 次」）。裁剪不会减少它。</summary>
        private int _keptClosed;

        private readonly List<TwinAnchor> _dead = new List<TwinAnchor>(8);
        private readonly List<AlarmRecord> _sortBuf = new List<AlarmRecord>(256);
        private float _nextPrune;
        /// <summary>下一次尝试挂钩告警中心的时刻（FindObjectOfType 要节流）。</summary>
        private float _nextHookTry;

        /// <summary>累计发生过的异常次数（已结束的有效记录 + 仍在持续的）。</summary>
        public int TotalCount { get { return _keptClosed + _ongoing.Count; } }

        /// <summary>当前仍在持续的点位数。</summary>
        public int OngoingCount { get { return _ongoing.Count; } }

        /// <summary>是否已经成功挂上告警中心的事件（面板可据此提示「历史未启用」）。</summary>
        public bool IsHooked { get { return _center != null; } }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning("[Twin] 场景里有多个 TwinAlarmHistory，保留第一个：" + Instance.name);
                enabled = false;
                return;
            }
            Instance = this;
            TryHook();
        }

        private void Start()
        {
            // Awake 里可能还拿不到 AlarmCenter（它的 Instance 也在 Awake 里赋值，
            // 而两个组件的 Awake 顺序不确定）。这里再试一次，Update 里还有第三次兜底。
            TryHook();
        }

        private void OnDestroy()
        {
            if (_center != null)
            {
                _center.AlarmRaised -= OnRaised;
                _center.AlarmCleared -= OnCleared;
                _center = null;
            }
            if (Instance == this) Instance = null;
        }

        private void TryHook()
        {
            if (_center != null) return;

            TwinAlarmCenter center = TwinAlarmCenter.Instance;
            if (center == null) center = FindObjectOfType<TwinAlarmCenter>();
            if (center == null) return;

            _center = center;
            _center.AlarmRaised += OnRaised;
            _center.AlarmCleared += OnCleared;
        }

        private void Update()
        {
            float now = Time.realtimeSinceStartup;

            // 兜底重试：告警中心可能是运行时才被加进来的，或者它所在的 GameObject 一开始是禁用的。
            // 【为什么要节流】TryHook 里会 FindObjectOfType —— 本工程有 1362 个预制体实例，
            // 场景扫描不是免费的。永远找不到时若每帧扫一次，等于给主线程加了一条常态开销。
            if (_center == null)
            {
                if (now < _nextHookTry) return;
                _nextHookTry = now + 1f;
                TryHook();
                if (_center == null) return;
            }

            if (now < _nextPrune) return;
            _nextPrune = now + 5f;
            Prune(now);
        }

        // ================================================================ 事件

        private void OnRaised(TwinAlarmCenter.AlarmEvent evt)
        {
            TwinAnchor anchor = evt.Anchor;
            if (anchor == null) return;

            AlarmRecord rec;
            if (_ongoing.TryGetValue(anchor, out rec))
            {
                // 重复提醒（IsNew=false）或严重度升级：并进同一条记录，只抬高峰值。
                // 不新建 —— 否则一次持续 10 分钟的 Alarm 会变成 20 条独立记录。
                if (TwinStatusPalette.Severity(evt.Status) > TwinStatusPalette.Severity(rec.PeakStatus))
                {
                    rec.PeakStatus = evt.Status;
                    _ongoing[anchor] = rec;
                }
                return;
            }

            rec.TwinId = anchor.TwinId;
            rec.DisplayName = anchor.DisplayName;
            rec.Zone = anchor.Zone;
            rec.PeakStatus = evt.Status;
            rec.Clock = DateTime.Now.ToString("HH:mm:ss");
            rec.StartTime = Time.realtimeSinceStartup;
            rec.EndTime = 0f;
            _ongoing[anchor] = rec;
        }

        private void OnCleared(TwinAnchor anchor)
        {
            if (anchor == null) return;

            AlarmRecord rec;
            if (!_ongoing.TryGetValue(anchor, out rec)) return;

            _ongoing.Remove(anchor);
            rec.EndTime = Time.realtimeSinceStartup;

            // 抖动抑制：太短的不进历史。判定放在「结束」这一刻 ——
            // 发生的时候还不知道它会持续多久。
            if (rec.EndTime - rec.StartTime < Mathf.Max(0f, _minSeconds)) return;

            _keptClosed++;
            _closed.Add(rec);
            Trim();
        }

        private void Trim()
        {
            int over = _closed.Count - Mathf.Max(1, _capacity);
            if (over > 0) _closed.RemoveRange(0, over);
        }

        /// <summary>
        /// 清掉已销毁锚点的进行中记录。
        /// 不清会永久占着 _ongoing —— Unity 的「假 null」让 TryGetValue 仍然命中，
        /// 表现为「持续时长」一路涨到天上，且永远不结束。
        /// </summary>
        private void Prune(float now)
        {
            if (_ongoing.Count == 0) return;

            _dead.Clear();
            foreach (KeyValuePair<TwinAnchor, AlarmRecord> kv in _ongoing)
            {
                if (kv.Key == null) _dead.Add(kv.Key);
            }
            if (_dead.Count == 0) return;

            for (int i = 0; i < _dead.Count; i++)
            {
                AlarmRecord rec;
                if (!_ongoing.TryGetValue(_dead[i], out rec)) continue;
                _ongoing.Remove(_dead[i]);
                rec.EndTime = Mathf.Max(rec.StartTime + 0.01f, now);
                if (rec.EndTime - rec.StartTime < Mathf.Max(0f, _minSeconds)) continue;
                _keptClosed++;
                _closed.Add(rec);
            }
            Trim();
        }

        // ================================================================ 查询

        /// <summary>
        /// 取某个点位「本次异常已持续多少秒」。
        /// 返回 false 表示它当前没有异常（或异常刚发生还没被记录）。
        /// </summary>
        public bool TryGetOngoingSeconds(TwinAnchor anchor, out float seconds)
        {
            seconds = 0f;
            if (anchor == null) return false;

            AlarmRecord rec;
            if (!_ongoing.TryGetValue(anchor, out rec)) return false;

            seconds = rec.Duration(Time.realtimeSinceStartup);
            return true;
        }

        /// <summary>
        /// 取最近的若干条记录，**按发生时间从新到旧**。
        /// 进行中的和已结束的混在一起排 —— 观众关心的是「时间线」，不是「分类」。
        /// </summary>
        public void GetRecent(List<AlarmRecord> buffer, int max)
        {
            if (buffer == null) return;
            buffer.Clear();
            if (max <= 0) return;

            _sortBuf.Clear();
            for (int i = 0; i < _closed.Count; i++) _sortBuf.Add(_closed[i]);
            foreach (KeyValuePair<TwinAnchor, AlarmRecord> kv in _ongoing) _sortBuf.Add(kv.Value);

            _sortBuf.Sort(CompareByStartDesc);

            int n = Mathf.Min(max, _sortBuf.Count);
            for (int i = 0; i < n; i++) buffer.Add(_sortBuf[i]);
        }

        private static int CompareByStartDesc(AlarmRecord a, AlarmRecord b)
        {
            return b.StartTime.CompareTo(a.StartTime);
        }

        /// <summary>手动清空（演示前复位用）。不影响 AlarmCenter 的当前状态。</summary>
        [ContextMenu("清空告警历史")]
        public void Clear()
        {
            _ongoing.Clear();
            _closed.Clear();
            _keptClosed = 0;
        }

        // ================================================================ 文案

        /// <summary>
        /// 秒 → 中文时长。刻意不用 mm:ss：观众不是运维，`2:30` 会被读成「两点半」。
        ///
        /// 用**向上取整**而不是向下：异常刚发生 0.2 秒时向下取整会显示「0秒」，
        /// 看着像个坏掉的数字；向上取整则永远是「1秒」起。
        /// </summary>
        public static string FormatDuration(float seconds)
        {
            if (seconds < 0f) seconds = 0f;

            int total = Mathf.Max(1, Mathf.CeilToInt(seconds));
            if (total < 60) return total + "秒";

            int m = total / 60;
            int s = total % 60;
            if (m < 60) return s == 0 ? m + "分" : m + "分" + s + "秒";

            int h = m / 60;
            m = m % 60;
            return m == 0 ? h + "小时" : h + "小时" + m + "分";
        }

        [ContextMenu("打印告警历史")]
        private void PrintHistory()
        {
            float now = Time.realtimeSinceStartup;
            Debug.Log("[Twin] 告警历史：累计 " + TotalCount + " 次，进行中 " + OngoingCount + " 个");

            _sortBuf.Clear();
            for (int i = 0; i < _closed.Count; i++) _sortBuf.Add(_closed[i]);
            foreach (KeyValuePair<TwinAnchor, AlarmRecord> kv in _ongoing) _sortBuf.Add(kv.Value);
            _sortBuf.Sort(CompareByStartDesc);

            int n = Mathf.Min(12, _sortBuf.Count);
            for (int i = 0; i < n; i++)
            {
                AlarmRecord r = _sortBuf[i];
                Debug.Log("    " + r.Clock + "  " + r.DisplayName + "  "
                          + TwinStatusPalette.Label(r.PeakStatus) + "  "
                          + (r.Ongoing ? "进行中 " : "") + FormatDuration(r.Duration(now)));
            }
        }
    }
}

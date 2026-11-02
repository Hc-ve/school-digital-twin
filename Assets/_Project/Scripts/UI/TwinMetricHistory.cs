using System.Collections.Generic;
using SchoolTwin.Core;      // TwinValue 在 Core，不在 Twin（踩过：CS0246）
using SchoolTwin.Twin;
using UnityEngine;
// 【别名，不是 using System.Diagnostics】
// 直接 using 那个命名空间会引入 System.Diagnostics.Debug，
// 和 UnityEngine.Debug 撞成 CS0104 二义性 —— 本文件里用的是后者。
using Stopwatch = System.Diagnostics.Stopwatch;

namespace SchoolTwin.UI
{
    /// <summary>
    /// 【P3】时序历史层 —— 现有数据的**短期内存缓存**，不是新数据源。
    ///
    /// ── 为什么需要（行业调研结论）──────────────────────────────────
    /// 五家产品（Grafana / ThingsBoard / WinCC / 紫金桥 / Azure Digital Twins）
    /// 在存储介质上完全不同（外部库 / 本地归档 / 自研实时库 / ADX），
    /// 但架构上完全一致：
    ///
    ///   **时序数据永远独立于界面存在。界面打开只是"读"，不是"开始记"。**
    ///
    /// 我们原来违反的正是这一条：`TwinSparkline` 是界面的一部分，打开时 `Clear()`，
    /// 曲线从空白慢慢长出来 —— 这是"demo 感"的来源，与图表样式无关。
    ///
    /// 详见 `Docs/P3_时序历史层_调研报告_断点.md` 第 2、3 章。
    ///
    /// ── 它是什么 / 不是什么 ──────────────────────────────────────
    /// 是：`TwinAnchor.Values` 的 300 点短期缓存，5Hz 采样，纯内存环形缓冲。
    /// 不是：数据源。**只读 `Values`，从不写它**。不落盘、不接外部存储、不建数据库。
    /// 关掉本类，系统照常运行，只是曲线又变回从空白长。
    ///
    /// ── 参数取值依据（对照调研）──────────────────────────────────
    /// · 300 点 / 60s：与 `TwinSparkline._capacity = 320` 匹配，留 20 点余量，
    ///   避免回填瞬间触发 `List.RemoveAt(0)` 的批量搬移。
    /// · 5Hz：低于 WinCC 的 500ms（数据源本身不是高频），
    ///   高于 Grafana 最低 5s（曲线要平滑）。
    /// · 采集周期与显示周期分离：这是 WinCC 的做法，采集快、窗口长，两个独立参数。
    /// </summary>
    public static class TwinMetricHistory
    {
        /// <summary>采样间隔（秒）。5Hz。</summary>
        public const float SampleInterval = 0.2f;

        /// <summary>每个指标保留的点数。300 点 @5Hz = 60 秒。</summary>
        public const int Capacity = 300;

        /// <summary>
        /// twinId → metric → 环形缓冲。
        ///
        /// 【为什么外层用 TwinAnchor 引用而不是 twinId 字符串】
        /// 采样是 5Hz × 95 指标 = 475 次/秒的查找。用字符串做 key 要么每帧拼接
        /// （持续 GC），要么持有 twinId 的副本。直接用对象引用做 key，
        /// 哈希走 RuntimeHelpers.GetHashCode，零分配。
        /// </summary>
        private static readonly Dictionary<TwinAnchor, Dictionary<string, Ring>> _store =
            new Dictionary<TwinAnchor, Dictionary<string, Ring>>(64);

        /// <summary>回填用的临时缓冲（复用，避免每次打开设备都 new）。</summary>
        private static readonly List<Vector2> _outBuf = new List<Vector2>(Capacity);

        /// <summary>
        /// 扁平化的采样槽：每个 (设备, 指标) 一条，**预先解析好**。
        ///
        /// ── 为什么要这层缓存（实测 1.34ms/s → 优化后见 Console）──────────
        /// 第一版每次采样都遍历 53 台设备、每台调 `IsActiveDevice()`，
        /// 而它内部会读 `go.activeInHierarchy` —— 这是 **native 调用且要遍历父链**，
        /// 深层级下每次 5~10 微秒。53 台 × 5Hz = 每秒 265 次，直接吃掉 1.3ms/s。
        ///
        /// 现在：`IsActiveDevice` 只在**建槽时**判一次；采样阶段只遍历槽列表，
        /// 每槽一次 `TryGetValue`（纯托管字典查找，无 native 调用）+ 一次环形写入。
        ///
        /// ── 为什么采样阶段不再判 IsActiveDevice ──────────────────────────
        /// 设备被 `SetActive(false)` 只是"暂时不显示"，它的数据仍在推送。
        /// 历史缓冲本来就该独立于可见性 —— 这对应 Azure Digital Twins 那条
        /// 「时序独立于孪生模型」的分层原则。设备重新显示时，历史是连续的。
        /// </summary>
        private static readonly List<Slot> _slots = new List<Slot>(128);

        /// <summary>建槽时的设备总数，用来判断要不要重建（设备增删才重建）。</summary>
        private static int _slotSourceCount = -1;

        private sealed class Slot
        {
            public TwinAnchor Anchor;
            public string Metric;
            public Ring Ring;
        }

        private static float _nextSample;

        // ---- 开销实测（验收要求「采样开销 < 0.1ms/s」）----
        private static double _costMsAccum;
        private static int _costCount;
        private static float _nextReport;

        // ================================================================ 采样

        /// <summary>
        /// 主循环统一采样。由常驻 runner 每帧调用，**与任何界面是否打开无关**。
        ///
        /// 内部按 `SampleInterval` 限频，所以每帧调用的代价只是一次 float 比较。
        /// </summary>
        public static void Tick(float now)
        {
            if (now < _nextSample) return;
            _nextSample = now + SampleInterval;

            long t0 = Stopwatch.GetTimestamp();

            EnsureSlots();

            for (int i = 0; i < _slots.Count; i++)
            {
                Slot s = _slots[i];
                TwinValue v;
                if (s.Anchor.TryGetValue(s.Metric, out v))
                    s.Ring.Push(now, v.Value);
            }

            // 开销统计：首次 5 秒后报一次（便于验收当场看到数字），之后每 60 秒一次
            _costMsAccum += (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            _costCount++;

            if (_nextReport <= 0f) _nextReport = now + 5f;

            if (now >= _nextReport)
            {
                _nextReport = now + 60f;
                if (_costCount > 0)
                {
                    double avgMs = _costMsAccum / _costCount;
                    double perSecondMs = avgMs / SampleInterval;
                    Debug.Log("[TwinHistory] 采样 " + _costCount + " 次 / 指标 " + MetricCount() +
                              " 项 / 内存约 " + (MemoryBytes() / 1024f).ToString("0") + " KB" +
                              " / 单次 " + avgMs.ToString("0.0000") + " ms" +
                              " / 折算 " + perSecondMs.ToString("0.0000") + " ms/s");
                }
                _costMsAccum = 0.0;
                _costCount = 0;
            }
        }

        /// <summary>
        /// 建采样槽。**只在设备增删时重建** —— 正常运行零开销。
        ///
        /// 判断依据是设备总数变化：53 台固定场景下，这段代码整个生命周期只跑一次。
        /// 不做定时重建，是因为 `IsActiveDevice` 含 native 调用，
        /// 哪怕 5 秒一次也会在帧率曲线上留一个可测量的尖峰。
        /// </summary>
        private static void EnsureSlots()
        {
            IReadOnlyList<TwinAnchor> all = TwinDeviceStats.AllDevices;
            int count = all != null ? all.Count : 0;

            if (_slots.Count > 0 && count == _slotSourceCount) return;

            _slotSourceCount = count;
            _slots.Clear();
            if (all == null) return;

            for (int i = 0; i < all.Count; i++)
            {
                TwinAnchor a = all[i];
                if (a == null) continue;
                if (!TwinDeviceStats.IsActiveDevice(a)) continue;   // 只在这里判一次

                IReadOnlyDictionary<string, TwinValue> values = a.Values;
                if (values == null || values.Count == 0) continue;

                Dictionary<string, Ring> rings = GetOrCreateRings(a);
                foreach (KeyValuePair<string, TwinValue> kv in values)
                {
                    Ring ring;
                    if (!rings.TryGetValue(kv.Key, out ring))
                    {
                        ring = new Ring();
                        rings[kv.Key] = ring;
                    }

                    Slot s = new Slot();
                    s.Anchor = a;
                    s.Metric = kv.Key;
                    s.Ring = ring;
                    _slots.Add(s);
                }
            }

            Debug.Log("[TwinHistory] 采样槽已建立：" + _slots.Count + " 条（设备 " + count + " 台）");
        }

        private static Dictionary<string, Ring> GetOrCreateRings(TwinAnchor a)
        {
            Dictionary<string, Ring> rings;
            if (!_store.TryGetValue(a, out rings))
            {
                rings = new Dictionary<string, Ring>(8, System.StringComparer.Ordinal);
                _store[a] = rings;
            }
            return rings;
        }

        // ================================================================ 回填

        /// <summary>
        /// 取某台设备某个指标的历史点（按时间升序）。
        ///
        /// 返回 false = 缓冲里还没有这个指标（界面应该退化成"从空白开始"，
        /// 而不是画一条假线）。返回 true 时 `into` 至少 2 个点才有画线的意义。
        /// </summary>
        public static bool TryGet(TwinAnchor a, string metric, List<Vector2> into)
        {
            if (into == null) return false;
            into.Clear();
            if (a == null || string.IsNullOrEmpty(metric)) return false;

            Dictionary<string, Ring> rings;
            if (!_store.TryGetValue(a, out rings)) return false;

            Ring ring;
            if (!rings.TryGetValue(metric, out ring)) return false;

            ring.CopyTo(into);
            return into.Count > 0;
        }

        /// <summary>
        /// 把某台设备某个指标的历史批量回填进趋势线。
        ///
        /// **有历史就回填，没有就 Clear** —— 不画假线。
        /// 这是「打开即完整曲线」的唯一入口，详情页与浮卡都调它。
        /// </summary>
        public static void Fill(TwinAnchor a, string metric, TwinSparkline spark)
        {
            if (spark == null) return;

            if (TryGet(a, metric, _outBuf) && _outBuf.Count >= 2)
                spark.LoadFrom(_outBuf);
            else
                spark.Clear();   // 冷启动（程序刚跑、缓冲还没攒够）时退回原行为
        }

        // ================================================================ 诊断

        /// <summary>已缓存的指标数（用于 Console 诊断）。</summary>
        public static int MetricCount()
        {
            int n = 0;
            foreach (Dictionary<string, Ring> rings in _store.Values)
                n += rings.Count;
            return n;
        }

        /// <summary>
        /// 估算内存占用：环形缓冲本体 + 引用开销。
        /// 每点 8 字节（float T + float V），每指标另有对象头与数组头约 60 字节。
        /// </summary>
        public static long MemoryBytes()
        {
            int metrics = MetricCount();
            return (long)metrics * (Capacity * 8L + 60L);
        }

        /// <summary>清空（场景重载时用；正常运行不需要调）。</summary>
        public static void Clear()
        {
            _store.Clear();
            _slots.Clear();
            _slotSourceCount = -1;
            _nextSample = 0f;
            _costMsAccum = 0.0;
            _costCount = 0;
            _nextReport = 0f;
        }

        // ================================================================ 环形缓冲

        /// <summary>
        /// 定长环形缓冲。写满后覆盖最旧的 —— 对应 WinCC 的「循环归档」。
        ///
        /// 【为什么不用 List】List 满容量后 `RemoveAt(0)` 要搬移整个数组，
        /// 300 点每次搬 2.4KB，5Hz 下就是每秒 12KB 的纯拷贝。环形缓冲写入是 O(1)。
        /// </summary>
        private sealed class Ring
        {
            private readonly Vector2[] _buf = new Vector2[Capacity];
            private int _head;    // 下一个写入位置
            private int _count;

            public void Push(float t, float v)
            {
                _buf[_head].x = t;
                _buf[_head].y = v;
                _head = (_head + 1) % Capacity;
                if (_count < Capacity) _count++;
            }

            public void CopyTo(List<Vector2> dst)
            {
                if (_count == 0) return;

                int start = (_head - _count + Capacity) % Capacity;
                for (int i = 0; i < _count; i++)
                    dst.Add(_buf[(start + i) % Capacity]);
            }
        }
    }

    /// <summary>
    /// 采样器的常驻宿主。
    ///
    /// 【为什么单独一个常驻对象，不塞进 TwinRecorder】
    /// 负责人裁决：「录制可开关，缓冲必须常驻」—— 两者生命周期语义不同，
    /// 混在一起会出现"关掉录制 → 曲线也不采样了"这种耦合故障。
    ///
    /// 自举时机用 AfterSceneLoad：此时 `TwinRuntime` 已初始化，`TwinDeviceStats` 可用。
    /// </summary>
    internal sealed class TwinMetricHistoryRunner : MonoBehaviour
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            GameObject go = new GameObject("[TwinMetricHistory]");
            // 全限定：本文件没有 using System，`Object` 会不会解析到 System.Object
            // 取决于 C# 语言版本，写全最稳。
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<TwinMetricHistoryRunner>();
        }

        private void Update()
        {
            // 用 unscaledTime：暂停/慢放时历史仍应按真实时间推进，
            // 否则恢复播放后曲线的时间轴会出现断层。
            TwinMetricHistory.Tick(Time.unscaledTime);
        }
    }
}

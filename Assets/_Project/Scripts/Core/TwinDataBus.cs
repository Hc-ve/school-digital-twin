using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace SchoolTwin.Core
{
    public struct TwinBusStats
    {
        public int Received;
        public int Dispatched;
        public int Dropped;      // 队列积压触顶被丢弃
        public int Orphan;       // 没有订阅者的数据 —— 点位表写错会立刻体现在这里
        public int PendingInbox;
        public int SubscriberCount;
    }

    /// <summary>
    /// 数据总线：**后台线程写、主线程读**的唯一通道。
    ///
    /// 设计要点（数字孪生掉帧的根因几乎都在这里）：
    /// 1. 生产端（网络/串口线程）只做 Enqueue，永不触碰 Unity API；
    /// 2. 消费端由 TwinRuntime.Update 每帧调用一次 Flush，在主线程分发；
    /// 3. 每帧有分发上限，避免一帧涌入上万条把帧率打死；
    /// 4. 队列设硬上限，超限丢最旧的并计数，宁可丢数据也不 OOM。
    /// </summary>
    public sealed class TwinDataBus : IDataSink
    {
        private readonly ConcurrentQueue<TwinSample> _inbox = new ConcurrentQueue<TwinSample>();
        private readonly List<TwinSample> _drain = new List<TwinSample>(512);
        private readonly Dictionary<string, List<ITwinConsumer>> _byTwinId =
            new Dictionary<string, List<ITwinConsumer>>(StringComparer.Ordinal);
        private readonly List<string> _emptyIds = new List<string>();

        private readonly int _maxPending;
        private int _received;
        private int _dispatched;
        private int _dropped;
        private int _orphan;
        private int _subscriberCount;
        private float _lastDropWarnTime = -999f;

        /// <param name="maxPending">队列硬上限，超过则丢最旧的一条。</param>
        public TwinDataBus(int maxPending = 200000)
        {
            _maxPending = Mathf.Max(1024, maxPending);
        }

        public int PendingCount { get { return _inbox.Count; } }

        // ---------------------------------------------------------------- 生产端

        /// <summary>可由任意线程调用。</summary>
        public void Emit(in TwinSample sample)
        {
            if (!sample.IsValid) return;

            if (_inbox.Count >= _maxPending)
            {
                TwinSample ignored;
                _inbox.TryDequeue(out ignored);
                Interlocked.Increment(ref _dropped);
            }

            _inbox.Enqueue(sample);
            Interlocked.Increment(ref _received);
        }

        /// <summary>批量写入（Mock 生成器一个周期内的一批数据）。</summary>
        public void EmitBatch(IList<TwinSample> batch)
        {
            if (batch == null) return;
            for (int i = 0; i < batch.Count; i++)
            {
                TwinSample s = batch[i];
                Emit(in s);
            }
        }

        // ---------------------------------------------------------------- 订阅管理

        public void Subscribe(string twinId, ITwinConsumer consumer)
        {
            if (string.IsNullOrEmpty(twinId) || consumer == null) return;

            List<ITwinConsumer> list;
            if (!_byTwinId.TryGetValue(twinId, out list))
            {
                list = new List<ITwinConsumer>(2);
                _byTwinId.Add(twinId, list);
            }
            if (!list.Contains(consumer))
            {
                list.Add(consumer);
                Interlocked.Increment(ref _subscriberCount);
            }
        }

        public void Unsubscribe(string twinId, ITwinConsumer consumer)
        {
            if (string.IsNullOrEmpty(twinId) || consumer == null) return;

            List<ITwinConsumer> list;
            if (!_byTwinId.TryGetValue(twinId, out list)) return;
            if (list.Remove(consumer))
            {
                Interlocked.Decrement(ref _subscriberCount);
            }
            if (list.Count == 0) _byTwinId.Remove(twinId);
        }

        /// <summary>按订阅者反查退订，用于 TwinAnchor.OnDisable 兜底。</summary>
        public void UnsubscribeAll(ITwinConsumer consumer)
        {
            if (consumer == null) return;

            _emptyIds.Clear();
            foreach (KeyValuePair<string, List<ITwinConsumer>> kv in _byTwinId)
            {
                if (kv.Value.Remove(consumer))
                {
                    Interlocked.Decrement(ref _subscriberCount);
                }
                if (kv.Value.Count == 0) _emptyIds.Add(kv.Key);
            }
            for (int i = 0; i < _emptyIds.Count; i++) _byTwinId.Remove(_emptyIds[i]);
            _emptyIds.Clear();
        }

        public void Clear()
        {
            TwinSample ignored;
            while (_inbox.TryDequeue(out ignored)) { }
            _byTwinId.Clear();
            _subscriberCount = 0;
        }

        // ---------------------------------------------------------------- 消费端（主线程）

        /// <summary>只在主线程调用，建议每帧一次。</summary>
        public int Flush(int maxDispatch = 4096)
        {
            if (maxDispatch <= 0) return 0;

            _drain.Clear();
            TwinSample s;
            while (_drain.Count < maxDispatch && _inbox.TryDequeue(out s))
            {
                _drain.Add(s);
            }

            int n = _drain.Count;
            for (int i = 0; i < n; i++)
            {
                TwinSample sample = _drain[i];

                List<ITwinConsumer> list;
                if (_byTwinId.TryGetValue(sample.TwinId, out list))
                {
                    // 反向遍历：允许消费方在自己的回调里退订
                    for (int k = list.Count - 1; k >= 0; k--) list[k].OnSample(in sample);
                }
                else
                {
                    Interlocked.Increment(ref _orphan);
                }
                Interlocked.Increment(ref _dispatched);
            }

            // 队列持续积压 = 生产速度超过消费速度，必须让开发者看见
            if (_inbox.Count > maxDispatch * 8)
            {
                float now = Time.realtimeSinceStartup;
                if (now - _lastDropWarnTime > 5f)
                {
                    _lastDropWarnTime = now;
                    Debug.LogWarning("[Twin] 数据总线积压 " + _inbox.Count +
                                     " 条（每帧上限 " + maxDispatch + "）。请检查点位数量或降低数据频率。");
                }
            }

            return n;
        }

        public TwinBusStats GetStats()
        {
            TwinBusStats st;
            st.Received = _received;
            st.Dispatched = _dispatched;
            st.Dropped = _dropped;
            st.Orphan = _orphan;
            st.PendingInbox = _inbox.Count;
            st.SubscriberCount = _subscriberCount;
            return st;
        }

        public void ResetStats()
        {
            Interlocked.Exchange(ref _received, 0);
            Interlocked.Exchange(ref _dispatched, 0);
            Interlocked.Exchange(ref _dropped, 0);
            Interlocked.Exchange(ref _orphan, 0);
        }
    }
}

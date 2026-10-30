using System;
using System.Collections.Generic;
using UnityEngine;
using SchoolTwin.Core;

namespace SchoolTwin.Twin
{
    /// <summary>
    /// 业务 ID ↔ TwinAnchor 的注册表。
    /// 存在的意义：让「数据」和「场景对象」解耦——数据里只有字符串 ID，
    /// 谁来响应由注册表决定，这样换模型、换场景都不用改数据层。
    /// </summary>
    public sealed class TwinRegistry
    {
        private readonly Dictionary<string, TwinAnchor> _byId =
            new Dictionary<string, TwinAnchor>(StringComparer.Ordinal);
        private readonly List<TwinAnchor> _all = new List<TwinAnchor>(64);

        /// <summary>
        /// 被计入 <see cref="ActiveCount"/> 的锚点。用集合而不是靠"注册时再判一次"，
        /// 是因为注销往往发生在 `OnDisable` 里 —— 那一刻 `activeInHierarchy` 可能已经是 false，
        /// 再判一次会得出 false，于是该减的没减，ActiveCount 只增不减。
        /// 这里记「谁被算进去过」，注销时按记录减，账永远是平的。
        /// </summary>
        private readonly HashSet<TwinAnchor> _activeSet = new HashSet<TwinAnchor>();

        private int _activeCount;

        public IReadOnlyList<TwinAnchor> All { get { return _all; } }
        public int Count { get { return _all.Count; } }

        /// <summary>
        /// 设备总数（A1~A4 全满足）。增量维护，读取 O(1)，**不遍历**。
        /// P29-3.6 起它是 UI 唯一的「设备总数」来源，UI 禁止再自己数 `All.Count`。
        /// </summary>
        public int ActiveCount { get { return _activeCount; } }

        /// <summary>注册 / 注销时触发。供快照层立即重算，不必等下一个 0.2s 轮询。</summary>
        public event Action RegistryChanged;

        /// <returns>false 表示 ID 冲突，注册失败（保留先注册的那个）。</returns>
        public bool Register(TwinAnchor anchor)
        {
            if (anchor == null) return false;
            string id = anchor.TwinId;
            if (string.IsNullOrEmpty(id))
            {
                Debug.LogWarning("[Twin] TwinAnchor 未填写 TwinId，已忽略：" + GetPath(anchor));
                return false;
            }

            TwinAnchor exist;
            if (_byId.TryGetValue(id, out exist) && exist != null && exist != anchor)
            {
                Debug.LogError("[Twin] TwinId 重复：'" + id + "'\n  已注册：" + GetPath(exist) +
                               "\n  本次注册：" + GetPath(anchor) + "\n  请修正其中一个，否则数据只会送到先注册的那个。");
                return false;
            }

            _byId[id] = anchor;
            if (!_all.Contains(anchor)) _all.Add(anchor);

            if (TwinDeviceStats.IsActiveDevice(anchor) && _activeSet.Add(anchor)) _activeCount++;

            RaiseChanged();
            return true;
        }

        public void Unregister(TwinAnchor anchor)
        {
            if (anchor == null) return;

            TwinAnchor exist;
            if (_byId.TryGetValue(anchor.TwinId, out exist) && exist == anchor) _byId.Remove(anchor.TwinId);
            _all.Remove(anchor);

            // 按「谁被算进去过」来减，不重新判定 —— 见 _activeSet 的注释
            if (_activeSet.Remove(anchor) && _activeCount > 0) _activeCount--;

            RaiseChanged();
        }

        /// <summary>
        /// 全量重算 ActiveCount。O(n)=53，由快照层每 0.2s 顺手调一次做自愈校正 ——
        /// 增量计数在「组件 enabled 被外部直接改」这类极端时序下可能与实际错开。
        /// </summary>
        public void RecountActive()
        {
            _activeSet.Clear();
            _activeCount = 0;

            for (int i = 0; i < _all.Count; i++)
            {
                TwinAnchor a = _all[i];
                if (!TwinDeviceStats.IsActiveDevice(a)) continue;
                if (_activeSet.Add(a)) _activeCount++;
            }
        }

        private void RaiseChanged()
        {
            Action handler = RegistryChanged;
            if (handler == null) return;

            // 包 try-catch：一个订阅者抛异常不能连累注册流程本身
            try { handler(); }
            catch (Exception e) { Debug.LogError("[Twin] RegistryChanged 处理异常：" + e); }
        }

        public bool TryGet(string twinId, out TwinAnchor anchor)
        {
            return _byId.TryGetValue(twinId, out anchor);
        }

        /// <summary>按分区前缀筛选（如 "Class2-1"）。结果写入传入的 List，不产生新分配。</summary>
        public int GetByZone(string zone, List<TwinAnchor> results)
        {
            if (results == null) return 0;
            results.Clear();
            if (string.IsNullOrEmpty(zone)) return 0;

            for (int i = 0; i < _all.Count; i++)
            {
                TwinAnchor a = _all[i];
                if (a != null && string.Equals(a.Zone, zone, StringComparison.Ordinal)) results.Add(a);
            }
            return results.Count;
        }

        public int CountByStatus(TwinStatus status)
        {
            int n = 0;
            for (int i = 0; i < _all.Count; i++)
            {
                if (_all[i] != null && _all[i].Status == status) n++;
            }
            return n;
        }

        public void Clear()
        {
            _byId.Clear();
            _all.Clear();
            _activeSet.Clear();
            _activeCount = 0;
            RaiseChanged();
        }

        private static string GetPath(TwinAnchor a)
        {
            return a == null ? "(null)" : a.gameObject.name;
        }
    }
}

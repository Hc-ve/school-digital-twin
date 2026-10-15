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

        public IReadOnlyList<TwinAnchor> All { get { return _all; } }
        public int Count { get { return _all.Count; } }

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
            return true;
        }

        public void Unregister(TwinAnchor anchor)
        {
            if (anchor == null) return;
            TwinAnchor exist;
            if (_byId.TryGetValue(anchor.TwinId, out exist) && exist == anchor) _byId.Remove(anchor.TwinId);
            _all.Remove(anchor);
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
        }

        private static string GetPath(TwinAnchor a)
        {
            return a == null ? "(null)" : a.gameObject.name;
        }
    }
}

using System;
using System.Collections.Generic;
using UnityEngine;
using SchoolTwin.Core;

namespace SchoolTwin.Twin
{
    /// <summary>分区组。P3 接入 100 盏室外路灯后，健康条要分「室内 / 室外」两段。</summary>
    public enum ZoneGroup
    {
        Indoor = 0,
        Outdoor = 1,
        All = 2,
    }

    /// <summary>分区统计的取值桶。与 <see cref="ZoneGroup"/> 组合使用。</summary>
    public enum ZoneGroupBucket
    {
        Total = 0,
        Online = 1,
        Offline = 2,
        Alarm = 3,
    }

    /// <summary>
    /// 设备指标的**唯一定量出口**（P29-3.6 §4.1）。
    ///
    /// ── 它解决什么 ──────────────────────────────────────────────────
    /// 重构前同一份「遍历 Registry 数状态」写了 6 遍（HomePage / Drawer / SystemPage /
    /// DetailPanel / DebugHud / PerfReport），于是同一个抽屉里三个地方显示三个数。
    /// 全部收敛到这里之后，UI 只允许读快照，禁止自己遍历 —— `grep Registry.All` 应为 0。
    ///
    /// ── 为什么必须是「快照」而不是每次现算 ──────────────────────────
    /// 同一帧内 KPI 卡、头部摘要、健康条要读同一个数。若各自现算，中间任何一次数据到达
    /// 都会让三个数错开一帧，屏幕上就是「设备总数 53、在线 54」这种不可能的组合。
    /// 快照 + Revision 保证同帧一致。
    ///
    /// ── ⚠️ 必须与断电抑制解耦（本类的核心约束）──────────────────────
    /// `TwinAlarmCenter` 的 `Entry.Status` 是**抑制后**的状态：断电回路会被强改成 Unknown
    /// 并计入 SuppressedCount。若统计读它，**拉一次闸就会让整层设备被判成「离线」**，
    /// 在线数暴跌、健康条全灰 —— 而事实是设备在线、只是没电。
    /// 所以本类一律直接读 `anchor.StatusOfWorst()` + `anchor.IsStale`，
    /// 不碰 TwinAlarmCenter 的 Entry，也不碰 TwinCircuit 的断电判定。
    ///
    /// ── 刷新时机 ───────────────────────────────────────────────────
    /// 只由 `TwinAlarmCenter` 的 0.2s 轮询循环调用 <see cref="Rebuild"/>（它本来就在遍历
    /// Registry.All，顺手算 4 个计数不增加任何遍历成本）。新开一个 Update 会多一遍 53 次
    /// 属性读，还会和告警中心的时序错开，出现「告警卡 2、告警列表 3 条」。
    /// </summary>
    public static class TwinDeviceStats
    {
        /// <summary>历史备份锚点的 TwinId 前缀。A2 判定：它们不是设备。</summary>
        private const string BakPrefix = "BAK";

        // ---- 设备维度（分母口径，同 scope）----
        private static int _activeDeviceCount;
        private static int _onlineDeviceCount;
        private static int _offlineDeviceCount;
        private static int _staleDeviceCount;
        private static int _alarmDeviceCount;

        // ---- 其他维度（不同 scope）----
        private static int _unackedAlarmEventCount;
        private static int _catalogPointCount;

        // ---- 分区计数（P3 前的守恒空实现）----
        private static readonly int[] s_indoor = new int[4];
        private static readonly int[] s_outdoor = new int[4];

        private static uint _revision;
        private static bool _everBuilt;
        private static float _lastBuildTry = -1f;

        /// <summary>设备总数。A1~A4 全满足的锚点数，P3 挂上 100 盏路灯后自动 53 → 153，不改代码。</summary>
        public static int ActiveDeviceCount { get { EnsureBuiltOnce(); return _activeDeviceCount; } }

        /// <summary>在线 = StatusOfWorst ∈ {Normal, Warning, Alarm}（数据在到达且新鲜）。</summary>
        public static int OnlineDeviceCount { get { EnsureBuiltOnce(); return _onlineDeviceCount; } }

        /// <summary>离线 = StatusOfWorst ∈ {Stale, Offline, Unknown}。恒等式：总数 = 在线 + 离线。</summary>
        public static int OfflineDeviceCount { get { EnsureBuiltOnce(); return _offlineDeviceCount; } }

        /// <summary>数据陈旧（沉默 &gt; 3s）。**已含在离线内，不单独参与分区**。</summary>
        public static int StaleDeviceCount { get { EnsureBuiltOnce(); return _staleDeviceCount; } }

        /// <summary>告警（含 Warning）。单位「台」，是「在线」的子集，不参与分区。</summary>
        public static int AlarmDeviceCount { get { EnsureBuiltOnce(); return _alarmDeviceCount; } }

        /// <summary>受控 = 静态白名单条数。常量，永不刷新；路灯只监不控，恒为 38。</summary>
        public static int ControlScopeCount { get { return TwinControlScope.Count; } }

        /// <summary>
        /// 未确认告警【条】。事件维度，一条 = 一个点位的一次告警。
        /// 本轮不做 Ack（裁决 C-B / 方案 A），语义降级为「进行中条数」，文案写「进行中 N 条」。
        /// </summary>
        public static int UnackedAlarmEventCount { get { EnsureBuiltOnce(); return _unackedAlarmEventCount; } }

        /// <summary>点位目录条目数。**仅供对账，不上 UI**。</summary>
        public static int CatalogPointCount { get { EnsureBuiltOnce(); return _catalogPointCount; } }

        /// <summary>快照版本号。UI 每帧比对，变了才刷文字，避免无谓的 Canvas 重建。</summary>
        public static uint Revision { get { EnsureBuiltOnce(); return _revision; } }

        /// <summary>健康条三段之和是否等于设备总数。不等则整卡转无数据态，不许画一条假的条。</summary>
        public static bool IsPartitionConsistent
        {
            get
            {
                EnsureBuiltOnce();
                return _onlineDeviceCount + _offlineDeviceCount == _activeDeviceCount;
            }
        }

        /// <summary>
        /// 全部已注册锚点（只读）。
        ///
        /// 【为什么要有它】告警列表、调试 HUD 需要**逐点**遍历（按状态筛选行），
        /// 这是计数快照给不了的能力；但让它们直接写 `Registry.All` 又会让
        /// 「谁在遍历注册表」这件事重新散开 —— 上一轮就是因为散成 6 处才导致同屏三个数。
        /// 所以把遍历入口也收在这里：统计用上面的计数，遍历用这个，UI 层不再出现 `Registry.All`。
        ///
        /// ⚠️ 拿到它之后**不要自己数总数**（`list.Count` 含 BAK 与未激活锚点），
        /// 行级过滤请用 <see cref="IsActiveDevice"/>。
        /// </summary>
        public static IReadOnlyList<TwinAnchor> AllDevices
        {
            get
            {
                TwinRuntime runtime = TwinRuntime.Instance;
                if (runtime == null || runtime.Registry == null) return null;
                return runtime.Registry.All;
            }
        }

        // ================================================================ 重建

        /// <summary>
        /// 重建快照。**只允许 <see cref="TwinAlarmCenter"/> 每 0.2s 调用一次。**
        /// O(n)=53 次属性读，与告警中心共用同一轮遍历。
        /// </summary>
        public static void Rebuild()
        {
            TwinRuntime runtime = TwinRuntime.Instance;
            if (runtime == null || runtime.Registry == null)
            {
                // 运行时就绪前保持上一次的值，**不清零**：启动首帧显示 0 比显示旧值更难看，
                // 而且 0 会被读成"场景里一台设备都没有"。UI 侧另有"未就绪走无数据态"兜底。
                return;
            }

            TwinRegistry registry = runtime.Registry;

            // 设备总数：注册表增量维护为主，这里做一次 O(n) 校正自愈
            // （enabled / activeInHierarchy 在极端时序下可能与增量计数错开）。
            registry.RecountActive();

            int online = 0, offline = 0, stale = 0, alarm = 0;
            IReadOnlyList<TwinAnchor> all = registry.All;
            for (int i = 0; i < all.Count; i++)
            {
                TwinAnchor anchor = all[i];
                if (!IsActiveDevice(anchor)) continue;

                TwinStatus status = ResolveStatus(anchor);
                if (anchor.IsStale) stale++;

                // 在线 = 数据在到达且新鲜。告警/警告都算在线 —— 设备在线但指标越界，
                // 不是"失联"。告警是「在线」的子集，不参与在线/离线分区。
                if (status == TwinStatus.Normal || status == TwinStatus.Warning || status == TwinStatus.Alarm)
                    online++;
                else
                    offline++;

                if (status == TwinStatus.Alarm || status == TwinStatus.Warning) alarm++;
            }

            TwinAlarmHistory history = TwinAlarmHistory.Instance;
            int ongoingEvents = history != null ? history.OngoingCount : 0;

            TwinCatalog catalog = runtime.Catalog;
            int catalogPoints = catalog != null ? catalog.Count : 0;

            int active = registry.ActiveCount;

            // ---- 分区（守恒空实现）----
            // 当前 53 个点位全部在室内（室外路灯是 P3 才接入的 100 盏）。
            // 之所以不写成"Outdoor 恒 0、Indoor 恒全量"的硬编码，是因为 P3 落地时
            // 只需要把 outdoor 那四个桶填上，UI 一行都不用改 —— 这就是"现在留好接口"的意义。
            s_indoor[(int)ZoneGroupBucket.Total] = active;
            s_indoor[(int)ZoneGroupBucket.Online] = online;
            s_indoor[(int)ZoneGroupBucket.Offline] = offline;
            s_indoor[(int)ZoneGroupBucket.Alarm] = alarm;

            s_outdoor[(int)ZoneGroupBucket.Total] = 0;
            s_outdoor[(int)ZoneGroupBucket.Online] = 0;
            s_outdoor[(int)ZoneGroupBucket.Offline] = 0;
            s_outdoor[(int)ZoneGroupBucket.Alarm] = 0;

            Commit(active, online, offline, stale, alarm, ongoingEvents, catalogPoints);
        }

        /// <summary>四个字段一起比对，任一变化才 +1 Revision —— 没变就不让 UI 重建。</summary>
        private static void Commit(int active, int online, int offline, int stale, int alarm,
                                   int ongoingEvents, int catalogPoints)
        {
            _everBuilt = true;

            bool changed = _activeDeviceCount != active
                           || _onlineDeviceCount != online
                           || _offlineDeviceCount != offline
                           || _staleDeviceCount != stale
                           || _alarmDeviceCount != alarm
                           || _unackedAlarmEventCount != ongoingEvents
                           || _catalogPointCount != catalogPoints;

            _activeDeviceCount = active;
            _onlineDeviceCount = online;
            _offlineDeviceCount = offline;
            _staleDeviceCount = stale;
            _alarmDeviceCount = alarm;
            _unackedAlarmEventCount = ongoingEvents;
            _catalogPointCount = catalogPoints;

            if (changed) _revision++;
        }

        /// <summary>
        /// 懒加载兜底：场景里若没有 <see cref="TwinAlarmCenter"/>（例如单开某个测试场景），
        /// Rebuild 永远不会被调用，UI 会读到全 0。这里在"从未建过"时补一次，
        /// 之后仍然只靠告警中心的 0.2s 循环 —— 不会变成每帧遍历。
        ///
        /// 加 0.2s 节流：`TwinRuntime` 若比 UI 晚就绪，首次 Rebuild 会直接 return 而不置位
        /// `_everBuilt`，没有节流的话每个 getter 都会重试一次（一帧十几次 O(n)）。
        /// </summary>
        private static void EnsureBuiltOnce()
        {
            if (_everBuilt) return;

            float now = Time.realtimeSinceStartup;
            if (now - _lastBuildTry < 0.2f) return;
            _lastBuildTry = now;

            Rebuild();
        }

        // ================================================================ 判定

        /// <summary>
        /// 单个锚点是否算「设备」。四项全部满足才算，逐条可测：
        ///   A1 已注册 —— 由调用方保证（遍历 Registry.All 天然满足）
        ///   A2 TwinId 不以 BAK / BAK2 开头 —— 历史备份不是设备
        ///   A3 物体在激活层级内 —— 允许整层楼临时关掉
        ///   A4 组件 enabled —— 允许单独关掉某个锚点的数据接收而不删物体
        ///
        /// **明确不纳入**：HasData（会让总数随数据到达而跳动）、阈值配置（那是配置缺失）、
        /// 是否在 TwinCatalog 里（目录是"在册"，Active 是"在线可驱动"，P3 会短暂分离）。
        /// </summary>
        public static bool IsActiveDevice(TwinAnchor anchor)
        {
            if (anchor == null) return false;

            string id = anchor.TwinId;
            if (string.IsNullOrEmpty(id)) return false;

            // A2：Ordinal 比较，不用 ToUpper（TwinId 是大小写敏感的 ASCII 路径）
            if (id.StartsWith(BakPrefix, StringComparison.Ordinal)) return false;

            GameObject go = anchor.gameObject;
            if (go == null || !go.activeInHierarchy) return false;   // A3

            return anchor.enabled;                                    // A4
        }

        /// <summary>
        /// 与 <c>TwinAlarmCenter.CurrentStatus</c> 同口径：整体状态 + 沉默判定。
        /// 两者必须一致，否则会出现「告警中心说陈旧、健康条说正常」。
        /// </summary>
        private static TwinStatus ResolveStatus(TwinAnchor anchor)
        {
            TwinStatus status = anchor.StatusOfWorst();
            if (anchor.IsStale && status != TwinStatus.Offline) status = TwinStatus.Stale;
            return status;
        }

        // ================================================================ 分区

        /// <summary>
        /// 按区域分组计数。P3 后健康条要分「室内 / 室外」两段。
        ///
        /// **守恒约束**：`CountByZoneGroup(Indoor, b) + CountByZoneGroup(Outdoor, b)`
        /// 必须恒等于 `CountByZoneGroup(All, b)`。当前 Outdoor 全 0、Indoor 全量，
        /// 等式成立；P3 填入真实分区后仍然成立。
        ///
        /// ⚠️ Outdoor 的判定**不要**写成"Zone 名字里含 Road/Lamp"这种字符串猜法，
        /// 由数据层给显式分区表（`P28` §588 的 `TwinOutdoorZones`），UI 只读结果。
        /// </summary>
        public static int CountByZoneGroup(ZoneGroup group, ZoneGroupBucket bucket)
        {
            EnsureBuiltOnce();

            int index = (int)bucket;
            if (index < 0 || index > 3) return 0;

            switch (group)
            {
                case ZoneGroup.Indoor: return s_indoor[index];
                case ZoneGroup.Outdoor: return s_outdoor[index];
                default: return s_indoor[index] + s_outdoor[index];
            }
        }

        // ================================================================ 调试

        /// <summary>把当前快照打一条日志。调试用；`[ContextMenu]` 不能挂在静态方法上，所以这里是 public。</summary>
        public static void LogSnapshot()
        {
            Debug.Log("[Twin] 设备指标快照 rev=" + _revision +
                      "  总数 " + _activeDeviceCount +
                      "  在线 " + _onlineDeviceCount +
                      "  离线 " + _offlineDeviceCount +
                      "（其中陈旧 " + _staleDeviceCount + "）" +
                      "  告警 " + _alarmDeviceCount +
                      "  受控 " + TwinControlScope.Count +
                      "  进行中告警 " + _unackedAlarmEventCount + " 条" +
                      "  目录 " + _catalogPointCount +
                      "  自洽=" + IsPartitionConsistent);
        }
    }
}

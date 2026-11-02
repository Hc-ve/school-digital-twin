using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace SchoolTwin.Twin
{
    /// <summary>
    /// 设备可用性：**和「数据状态」是两个维度，不许混。**
    ///
    /// ── 为什么必须独立于 TwinStatus ──────────────────────────────
    /// TwinStatus 回答的是「这条数据好不好」（正常 / 警告 / 告警 / 陈旧 / 离线）。
    /// 断电以后设备确实会沉默 8 秒，于是 TwinStatus 自然变成 Offline ——
    /// 但「断电」和「离线」对运维是两件完全不同的事：
    ///   · 离线 = 通信链路出了问题，要查网线 / broker / 网关；
    ///   · 断电 = 有人（或保护装置）把闸拉了，是**已知的人为状态**。
    /// 混成一个，值班的人就会去拔网线找一个根本没通电的设备。
    ///
    /// 所以这里单开一条轴，面板/告警/表现层三处都先问这条轴，再问 TwinStatus。
    /// </summary>
    public enum DeviceAvailability
    {
        /// <summary>还没收到过任何数据（开机第一秒、点位刚注册）。</summary>
        Unknown = 0,

        /// <summary>通电且有数据。</summary>
        Available = 1,

        /// <summary>回路断电。按钮禁用、状态灰显、**告警抑制**。</summary>
        PowerOff = 2,

        /// <summary>通电，但最近一条命令超时没回执。按钮仍可点，但大概率还会失败。</summary>
        Unreachable = 3,
    }

    /// <summary>
    /// 一个电力回路节点。三层：楼栋 → 楼层 → 房间。
    ///
    /// 【为什么要三层，不只做房间】真实配电是「总闸 → 楼层配电箱 → 房间回路」，
    /// 断一层电不是把 5 个房间挨个断一遍，而是断那一层的配电箱 ——
    /// 而且上层断电时下层必须跟着断（`ChainOn` 就是这件事）。
    /// </summary>
    public sealed class TwinCircuitNode
    {
        /// <summary>回路 id。房间级刻意用 "Circuit-" + Zone，与 TwinDeviceModel.PoweredBy 同一个口径。</summary>
        public string Id = "";

        /// <summary>上级回路 id；楼栋为空。</summary>
        public string ParentId = "";

        /// <summary>0 = 楼栋，1 = 楼层，2 = 房间。</summary>
        public int Level;

        /// <summary>房间级才有值。</summary>
        public string Zone = "";

        /// <summary>所在楼层；楼层级是自身楼层，房间级是继承来的，0 = 未登记。</summary>
        public int Floor;

        /// <summary>给人看的名字：「教学楼A」/「1F」/「2-1班」。</summary>
        public string Title = "";

        /// <summary>本节点自己的闸是否合上。上层断电时这里仍是 true —— 层级要分开看。</summary>
        public bool On = true;
    }

    /// <summary>
    /// 校园电力回路：回路 → 设备集合；楼层/房间 → 回路；回路通断 → 设备可用性。
    ///
    /// ── 为什么是静态类而不是 MonoBehaviour ────────────────────────
    /// 它要被四个地方同时查询：告警中心（每 0.2s）、总控面板（每 0.2s）、
    /// 命令中心（每次下发）、表现层（每 0.1s）。做成组件就得四处在场景里找它，
    /// 而且漏挂一次就是「断电了但灯还亮着」这种静默失效。
    /// 和 TwinSiteLayout / TwinDeviceModels / TwinControlScope 同一个做法：**表驱动 + 静态查询**。
    ///
    /// 副作用（停发数据、发心跳）不放在这里 —— 那些是"模拟设备"的事，
    /// 由 <see cref="TwinPowerController"/> 订阅 <see cref="Changed"/> 去做。
    /// 这条边界很重要：回路模型本身不碰数据链路，换数据源时它一行都不用改。
    /// </summary>
    public static class TwinCircuit
    {
        public const string BuildingId = "Circuit-Building";

        /// <summary>楼栋节点的显示名，与 TwinSiteLayout 同源。</summary>
        public static string BuildingTitle { get { return TwinSiteLayout.DefaultBuilding; } }

        public static string FloorId(int floor)
        {
            return "Circuit-F" + floor.ToString();
        }

        /// <summary>
        /// 房间回路 id。
        ///
        /// 【为什么必须是这个格式】`TwinDeviceModels.For()` 早就把
        /// `PoweredBy = "Circuit-" + zone` 写进每个设备模型了（P12 留的位）。
        /// 这里换个格式，那句声明就成了假的 —— 而"模型里写的和系统里用的不是一回事"
        /// 是最难查的一类不一致。
        /// </summary>
        public static string RoomId(string zone)
        {
            return "Circuit-" + (zone ?? "");
        }

        /// <summary>回路通断变化。参数：回路 id、是否通电。</summary>
        public static event Action<string, bool> Changed;

        private static readonly Dictionary<string, TwinCircuitNode> _nodes =
            new Dictionary<string, TwinCircuitNode>(24, StringComparer.Ordinal);
        private static readonly List<TwinCircuitNode> _order = new List<TwinCircuitNode>(24);
        private static bool _built;

        /// <summary>
        /// 「通电但命令超时」的点位 → 失效时刻（unscaledTime）。
        ///
        /// 【为什么放在这里而不是命令中心】可用性只有一条轴，出口只能有一个。
        /// 命令中心知道"刚刚超时了"，但它不该决定"这台设备现在算不算可用" ——
        /// 否则面板、卡片、表现层三处都要各自去问命令中心，迟早有一个忘了问。
        /// </summary>
        private static readonly Dictionary<string, float> _unreachableUntil =
            new Dictionary<string, float>(8, StringComparer.Ordinal);

        /// <summary>
        /// TwinId → 是否通电 的查表缓存。**每次合/拉闸都必须整表清空。**
        ///
        /// ── 为什么需要（2026-11-02 加断电归零后暴露）──────────────────
        /// 一旦 `LivePowerOf` 开始问"这台设备通电吗"，`IsPowered` 就从
        /// "偶尔被面板问"变成"5Hz × 53 台 × 两条采样路径"。
        ///
        /// 而 `IsPowered` 内部的成本并不低：
        ///   · `ZoneOf`  → `Substring`          → 一次字符串分配
        ///   · `RoomId`  → `"Circuit-" + zone`  → 一次字符串分配
        ///   · `IsPowerIndependent` → `Substring` → 又一次
        /// 算下来 530 次/秒 × 3 次分配 ≈ 1600 次小对象分配/秒，纯 GC 压力。
        ///
        /// 断电状态是**事件驱动**的（只有点按钮才会变），天然可缓存：
        /// 缓存表在 `SetSwitch` 里整表清掉，下一次查询重建。
        /// 71 台设备的重建成本可以忽略，而且只在真正拉闸的那一帧发生。
        ///
        /// ⚠️ 新增任何改变 `node.On` 的入口时，必须同时清空这张表 ——
        /// 漏掉的表现是"拉了闸但读数没归零"，且不会报错。
        /// </summary>
        private static readonly Dictionary<string, bool> _poweredCache =
            new Dictionary<string, bool>(96, StringComparer.Ordinal);

        /// <summary>
        /// 每次进入 Play 都从「全部通电」开始。
        ///
        /// 【为什么必须显式重置】静态字段在编辑器里跨 Play 会话是可能存活的
        /// （关闭 Domain Reload 时一定存活）。不重置的话，上一轮断电的房间这一轮开局就是黑的，
        /// 而现象是"灯不亮、按钮点不动、没有任何报错"—— 排查成本极高。
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnLoad()
        {
            _nodes.Clear();
            _order.Clear();
            _built = false;
            _unreachableUntil.Clear();
            _poweredCache.Clear();
            Changed = null;
        }

        /// <summary>手动复位（自检 / 演示前）。</summary>
        public static void ResetAll()
        {
            _nodes.Clear();
            _order.Clear();
            _built = false;
            _unreachableUntil.Clear();
            _poweredCache.Clear();
        }

        // ---------------------------------------------------------------- 建表

        private static void EnsureBuilt()
        {
            if (_built) return;
            _built = true;

            TwinCircuitNode building = new TwinCircuitNode();
            building.Id = BuildingId;
            building.ParentId = "";
            building.Level = 0;
            building.Title = BuildingTitle;
            Add(building);

            int[] floors = TwinSiteLayout.Floors();
            for (int i = 0; i < floors.Length; i++)
            {
                TwinCircuitNode f = new TwinCircuitNode();
                f.Id = FloorId(floors[i]);
                f.ParentId = BuildingId;
                f.Level = 1;
                f.Floor = floors[i];
                f.Title = floors[i] + "F";
                Add(f);
            }

            IReadOnlyList<TwinRoomInfo> rooms = TwinSiteLayout.Rooms;
            for (int i = 0; i < rooms.Count; i++)
            {
                TwinRoomInfo info = rooms[i];
                Add(MakeRoom(info.Zone, info.Floor, info.RoomName));
            }
        }

        private static TwinCircuitNode MakeRoom(string zone, int floor, string title)
        {
            TwinCircuitNode n = new TwinCircuitNode();
            n.Id = RoomId(zone);
            n.ParentId = floor > 0 ? FloorId(floor) : BuildingId;
            n.Level = 2;
            n.Zone = zone ?? "";
            n.Floor = floor;
            n.Title = string.IsNullOrEmpty(title) ? (zone ?? "") : title;
            return n;
        }

        private static void Add(TwinCircuitNode node)
        {
            if (node == null || string.IsNullOrEmpty(node.Id)) return;
            if (_nodes.ContainsKey(node.Id)) return;
            _nodes[node.Id] = node;
            _order.Add(node);
        }

        // ---------------------------------------------------------------- 查询

        /// <summary>全部回路，按「楼栋 → 楼层 → 房间」的建表顺序。</summary>
        public static IReadOnlyList<TwinCircuitNode> All
        {
            get
            {
                EnsureBuilt();
                return _order;
            }
        }

        public static TwinCircuitNode Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            EnsureBuilt();
            TwinCircuitNode n;
            return _nodes.TryGetValue(id, out n) ? n : null;
        }

        /// <summary>
        /// 取某个分区的房间回路。表里没有的分区**现场造一个挂在楼栋下**（Floor=0）。
        ///
        /// 【为什么不返回 null】新加一个房间却因为没登记楼层就"没有回路"，
        /// 结果是这个房间永远断不了电、也永远显示通电 —— 静默失效，没人会发现。
        /// </summary>
        public static TwinCircuitNode RoomOf(string zone)
        {
            EnsureBuilt();
            string id = RoomId(zone);
            TwinCircuitNode n;
            if (_nodes.TryGetValue(id, out n)) return n;

            n = MakeRoom(zone, TwinSiteLayout.UnknownFloor, TwinSiteLayout.Find(zone).RoomName);
            Add(n);
            return n;
        }

        /// <summary>某个分区所在的楼层回路；楼层不明返回 null。</summary>
        public static TwinCircuitNode FloorOfZone(string zone)
        {
            TwinCircuitNode room = RoomOf(zone);
            if (room == null || string.IsNullOrEmpty(room.ParentId)) return null;
            return Find(room.ParentId);
        }

        /// <summary>
        /// 沿「房间 → 楼层 → 楼栋」一路查上去，全链合闸才算通电。
        ///
        /// 【为什么不是只看房间那一个开关】断一层电时，5 个房间的开关并没有被改动 ——
        /// 改动的是楼层配电箱。如果只查房间开关，断完整层电每间房都还显示"通电"。
        /// </summary>
        public static bool IsPoweredZone(string zone)
        {
            TwinCircuitNode node = RoomOf(zone);
            int guard = 0;
            while (node != null && guard++ < 8)
            {
                if (!node.On) return false;
                node = string.IsNullOrEmpty(node.ParentId) ? null : Find(node.ParentId);
            }
            return true;
        }

        public static bool IsPowered(string twinId)
        {
            // 【缓存必须先查，再跑 IsPowerIndependent】
            // 后者内部有 `Substring`（切出类别那一段），放在缓存前面等于每次都白分配一次。
            // 门也是可缓存的：类别由 TwinId 决定，TwinId 不变 → 豁免性永不变。
            bool cached;
            if (_poweredCache.TryGetValue(twinId, out cached)) return cached;

            // 【P3-4】门不受电力回路影响 —— 见 IsPowerIndependent 的说明。
            bool powered = IsPowerIndependent(twinId) || IsPoweredZone(ZoneOf(twinId));
            _poweredCache[twinId] = powered;
            return powered;
        }

        /// <summary>
        /// 该设备是否**不挂在电力回路下**（断不断电都能操作）。
        ///
        /// ── 为什么门要豁免（主理人 2026-10-31 实测反馈）────────────────
        /// 「断电关开关门啥事啊，门也不能开关了」—— 教学楼的门是普通机械门
        /// （推拉 / 合页），不是电磁锁，断电照样能开。把它挂在电力回路下，
        /// 会出现「1F 一断电，整层门都点不动」，这在答辩演示里会被直接当成 bug。
        ///
        /// ── 为什么解析 TwinId 而不查 Registry ──────────────────────────
        /// 这个方法会被 `AvailabilityOf` 每台设备每帧问到（53 台 × 0.5s），
        /// 走字典查 TwinAnchor 再取 Category 太贵。TwinId 本身就是
        /// `Zone/Category/Name_序号`，第二段就是类别，直接切字符串最快。
        ///
        /// ── 以后有电磁门禁怎么办 ──────────────────────────────────────
        /// 那时应该新增一个类别（如 `DoorLock`），而不是把 Door 重新挂回回路 ——
        /// 两种门在同一场景里共存是常态，靠类别区分才是对的。
        /// </summary>
        private static bool IsPowerIndependent(string twinId)
        {
            if (string.IsNullOrEmpty(twinId)) return false;

            int a = twinId.IndexOf('/');
            if (a < 0) return false;
            int b = twinId.IndexOf('/', a + 1);
            if (b < 0) return false;

            string category = twinId.Substring(a + 1, b - a - 1);
            return string.Equals(category, "Door", StringComparison.Ordinal);
        }

        /// <summary>
        /// 上级回路是否通电（不含自己）。
        ///
        /// 【为什么需要单独问这一句】面板上的按钮切换的是**这个节点自己的闸**。
        /// 整栋断电时去点「断 1F 电」，什么都不会变（本来就没电）；再点「恢复供电」，
        /// 1F 还是黑的（总闸没合）—— 用户点了两次，得到零反馈，只会认为按钮坏了。
        /// 所以上级断电时按钮要明确写「上级已断电」并且禁掉。
        /// </summary>
        public static bool ParentPowered(string circuitId)
        {
            TwinCircuitNode node = Find(circuitId);
            if (node == null) return true;

            TwinCircuitNode p = string.IsNullOrEmpty(node.ParentId) ? null : Find(node.ParentId);
            int guard = 0;
            while (p != null && guard++ < 8)
            {
                if (!p.On) return false;
                p = string.IsNullOrEmpty(p.ParentId) ? null : Find(p.ParentId);
            }
            return true;
        }

        /// <summary>
        /// 设备的可用性。**所有显示层都必须先问这一句，再问 TwinStatus。**
        /// </summary>
        public static DeviceAvailability AvailabilityOf(string twinId)
        {
            if (string.IsNullOrEmpty(twinId)) return DeviceAvailability.Unknown;
            if (!IsPowered(twinId)) return DeviceAvailability.PowerOff;
            if (IsUnreachable(twinId)) return DeviceAvailability.Unreachable;

            TwinRuntime runtime = TwinRuntime.Instance;
            TwinAnchor anchor;
            if (runtime != null && runtime.Registry != null &&
                runtime.Registry.TryGet(twinId, out anchor) && anchor != null)
            {
                return anchor.HasData ? DeviceAvailability.Available : DeviceAvailability.Unknown;
            }
            return DeviceAvailability.Unknown;
        }

        // ---------------------------------------------------------------- 无响应

        /// <summary>
        /// 标记「这台设备通电、但最近一条命令超时没回执」。
        ///
        /// 【为什么要给它一个有效期，而不是一个布尔位】无响应是**瞬时事实**，不是状态。
        /// 设成 true 之后必须有人负责改回 false —— 而这个"谁负责"最容易漏
        /// （P2 的教训：从这个分支出去后没人把状态改回来）。给个 20 秒的窗口，
        /// 到期自动消失，就不会出现"设备早就恢复了、面板还挂着无响应"。
        /// </summary>
        public static void MarkUnreachable(string twinId, float seconds)
        {
            if (string.IsNullOrEmpty(twinId)) return;
            _unreachableUntil[twinId] = Time.unscaledTime + Mathf.Max(1f, seconds);
        }

        public static bool IsUnreachable(string twinId)
        {
            if (string.IsNullOrEmpty(twinId) || _unreachableUntil.Count == 0) return false;

            float until;
            if (!_unreachableUntil.TryGetValue(twinId, out until)) return false;
            if (Time.unscaledTime < until) return true;

            _unreachableUntil.Remove(twinId);
            return false;
        }

        /// <summary>
        /// 可用性的人话。面板 / 日志 / 卡片共用这一处 ——
        /// 三处各写一份的话，迟早出现"面板写断电、卡片写离线"的自相矛盾。
        /// </summary>
        public static string AvailabilityLabel(DeviceAvailability a)
        {
            switch (a)
            {
                case DeviceAvailability.Available: return "在线";
                case DeviceAvailability.PowerOff: return "断电";
                case DeviceAvailability.Unreachable: return "无响应";
                default: return "未知";
            }
        }

        /// <summary>
        /// 断电时**不能**用 TwinStatus 的颜色，要用「断电」自己的颜色。
        ///
        /// 用 Offline 的深灰：它表达的正是"这台设备现在没有在运行"，
        /// 而 Warning 的黄会让人以为"它还在工作，只是有点问题"。
        /// </summary>
        public static Color AvailabilityColor(DeviceAvailability a)
        {
            switch (a)
            {
                case DeviceAvailability.PowerOff: return new Color(0.42f, 0.44f, 0.48f, 1f);
                case DeviceAvailability.Unreachable: return new Color(0.95f, 0.62f, 0.22f, 1f);
                default: return new Color(0.62f, 0.69f, 0.79f, 1f);
            }
        }

        // ---------------------------------------------------------------- 通断

        /// <summary>
        /// 合闸 / 拉闸。返回受影响的设备数（-1 = 没有这个回路）。
        ///
        /// 注意：这里**只改状态并发事件**，不碰数据链路。
        /// 停发数据、发心跳由 <see cref="TwinPowerController"/> 负责。
        /// </summary>
        public static int SetSwitch(string circuitId, bool on)
        {
            TwinCircuitNode node = Find(circuitId);
            if (node == null) return -1;

            if (node.On == on) return CountDevices(circuitId);

            node.On = on;

            // 【必须】清空通电缓存 —— 见 `_poweredCache` 的注释。
            // 不清的表现是：拉了闸，但曲线/总量一个数都没变，且 Console 干净。
            _poweredCache.Clear();

            // 先数设备再发事件：事件处理器里会去停发数据，需要知道范围
            int devices = CountDevices(circuitId);

            StringBuilder sb = new StringBuilder(96);
            sb.Append("[TwinPower] ").Append(on ? "合闸" : "拉闸").Append("：")
              .Append(node.Title).Append("（").Append(circuitId).Append("）· 影响 ")
              .Append(devices).Append(" 台设备");
            Debug.Log(sb.ToString());

            Action<string, bool> handler = Changed;
            if (handler != null)
            {
                try { handler(circuitId, on); }
                catch (Exception e) { Debug.LogError("[TwinPower] 回路事件处理异常：" + e); }
            }
            return devices;
        }

        /// <summary>按分区拉/合闸（房间级）。</summary>
        public static int SetSwitchByZone(string zone, bool on)
        {
            return SetSwitch(RoomId(zone), on);
        }

        /// <summary>
        /// 当前「选中范围」对应的回路 id。
        /// 面板用它把「选中的是楼层还是房间」翻译成回路 —— 三种选择各对应一层。
        /// </summary>
        public static string CircuitOfSelection(string zone, int floorFilter)
        {
            if (!string.IsNullOrEmpty(zone)) return RoomId(zone);
            if (floorFilter > 0) return FloorId(floorFilter);
            return BuildingId;
        }

        // ---------------------------------------------------------------- 设备集合

        /// <summary>某个回路下面挂了多少台设备（按当前 Registry 实时数）。</summary>
        public static int CountDevices(string circuitId)
        {
            if (string.IsNullOrEmpty(circuitId)) return 0;

            TwinRuntime runtime = TwinRuntime.Instance;
            if (runtime == null || runtime.Registry == null) return 0;

            int n = 0;
            IReadOnlyList<TwinAnchor> all = runtime.Registry.All;
            for (int i = 0; i < all.Count; i++)
            {
                TwinAnchor a = all[i];
                if (a == null || string.IsNullOrEmpty(a.TwinId)) continue;
                if (IsUnder(a.Zone, circuitId)) n++;
            }
            return n;
        }

        /// <summary>
        /// 把回路下的设备名收进 buffer（SBO 弹窗要「列出将被断电的 N 台设备」）。
        ///
        /// 【为什么给名字而不是给 TwinId】弹窗是给人看的。
        /// `Class2-1/Light/Main_01` 对值班的人没有意义，`2-1班 · 主照明` 才有。
        /// </summary>
        public static void CollectDeviceNames(string circuitId, List<string> buffer)
        {
            if (buffer == null) return;
            buffer.Clear();
            if (string.IsNullOrEmpty(circuitId)) return;

            TwinRuntime runtime = TwinRuntime.Instance;
            if (runtime == null || runtime.Registry == null) return;

            IReadOnlyList<TwinAnchor> all = runtime.Registry.All;
            for (int i = 0; i < all.Count; i++)
            {
                TwinAnchor a = all[i];
                if (a == null || string.IsNullOrEmpty(a.TwinId)) continue;
                if (!IsUnder(a.Zone, circuitId)) continue;
                buffer.Add(DeviceLabel(a));
            }
            buffer.Sort(StringComparer.Ordinal);
        }

        /// <summary>「2-1班 · 主照明」这种给人看的标签。</summary>
        public static string DeviceLabel(TwinAnchor a)
        {
            if (a == null) return "?";

            string room = TwinSiteLayout.Find(a.Zone).RoomName;
            string name = a.DisplayName;

            // DisplayName 默认就是 TwinId；那种情况下自己拼一个更好看的
            if (string.IsNullOrEmpty(name) || string.Equals(name, a.TwinId, StringComparison.Ordinal))
                name = FallbackName(a.TwinId);

            if (string.IsNullOrEmpty(room)) return name;
            return room + " · " + name;
        }

        private static string FallbackName(string twinId)
        {
            if (string.IsNullOrEmpty(twinId)) return "?";
            string[] parts = twinId.Split('/');
            if (parts.Length < 3) return twinId;

            switch (parts[1])
            {
                case "Light": return "主照明";
                case "AC": return "空调机组";
                case "Heat": return "暖气";
                case "Door": return "门";
                case "PC": return "电脑";
                case "Sensor": return "环境传感器";
                case "Alarm": return "烟感";
                default: return parts[1] + "/" + parts[2];
            }
        }

        private static bool IsUnder(string zone, string circuitId)
        {
            if (string.Equals(circuitId, BuildingId, StringComparison.Ordinal)) return true;

            TwinCircuitNode room = RoomOf(zone);
            if (room == null) return false;
            if (string.Equals(room.Id, circuitId, StringComparison.Ordinal)) return true;
            return string.Equals(room.ParentId, circuitId, StringComparison.Ordinal);
        }

        private static string ZoneOf(string twinId)
        {
            if (string.IsNullOrEmpty(twinId)) return "";
            int slash = twinId.IndexOf('/');
            return slash < 0 ? twinId : twinId.Substring(0, slash);
        }

        // ---------------------------------------------------------------- 调试

        [ContextMenu("打印回路状态")]
        public static void PrintAll()
        {
            StringBuilder sb = new StringBuilder(512);
            sb.Append("[TwinPower] 回路共 ").Append(All.Count).Append(" 个\n");
            for (int i = 0; i < _order.Count; i++)
            {
                TwinCircuitNode n = _order[i];
                sb.Append("    ");
                for (int k = 0; k < n.Level; k++) sb.Append("  ");
                sb.Append(n.On ? "[合]" : "[断]").Append(' ').Append(n.Title)
                  .Append("  ").Append(n.Id)
                  .Append("  ").Append(CountDevices(n.Id)).Append(" 台\n");
            }
            Debug.Log(sb.ToString());
        }
    }
}

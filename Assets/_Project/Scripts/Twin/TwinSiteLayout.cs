using System;
using System.Collections.Generic;

namespace SchoolTwin.Twin
{
    /// <summary>
    /// 一个房间在校园里的位置：楼栋 / 楼层 / 中文名。
    /// </summary>
    public sealed class TwinRoomInfo
    {
        /// <summary>与 TwinAnchor.Zone / TwinPointDefinition.Zone 一致的 id，如 Class2-1。</summary>
        public string Zone;

        /// <summary>楼栋。当前场景只有一栋，留着是为了 P15 之后能扩成多栋。</summary>
        public string Building;

        /// <summary>楼层（1 = 一层）。0 表示「表里没有这个分区」。</summary>
        public int Floor;

        /// <summary>给人看的房间名，如「2-1班」。</summary>
        public string RoomName;

        /// <summary>表内顺序。分区列表按它排，不要按 Zone 的字典序（C 会排在 2 前面）。</summary>
        public int Order;

        /// <summary>「2F · 2-1班」。表外分区退化成只显示 Zone。</summary>
        public string DisplayName
        {
            get
            {
                if (Floor <= 0) return string.IsNullOrEmpty(RoomName) ? Zone : RoomName;
                return Floor + "F · " + RoomName;
            }
        }
    }

    /// <summary>
    /// 校园空间层级表：Zone → 楼栋 / 楼层 / 房间名。
    ///
    /// ── 为什么要表驱动，不用坐标推 ──────────────────────────────
    /// 层高只有 3.47m，1F 灯具 3.34、2F 灯具 6.90 —— 用 Y 坐标判楼层必须同时满足
    /// 「Y 窗口」和「本房间中心最近」两条，两条都在 P4-6 踩过坑（Class1-4 与 Class2-2
    /// 的 XZ 完全重合、Class2-1 与 ClassDirector 包围盒重叠 3m）。
    /// 而「哪个房间在几层」是**业务事实**，写进表里既不会错也一眼能看完。
    ///
    /// ── 表里没有的分区怎么办 ────────────────────────────────────
    /// 不丢、不报错，退化成「Floor=0 / RoomName=Zone」。总控面板会把它排在最后，
    /// 并在名字后面加个问号提示「这个分区没登记楼层」。**静默丢弃是最坏的选择** ——
    /// 新加一个房间却不出现在总控面板里，没人会发现。
    /// </summary>
    public static class TwinSiteLayout
    {
        public const string DefaultBuilding = "教学楼A";

        /// <summary>表外分区的楼层标记。</summary>
        public const int UnknownFloor = 0;

        /// <summary>
        /// 顺序沿用点位清单（T）里用了很久的顺序，改动它等于改动两个面板的阅读顺序，
        /// 不是纯技术决定。要按楼层重排的话，在这里改 Order 即可。
        /// </summary>
        private static readonly TwinRoomInfo[] Table =
        {
            R("Class1-2",      1, "1-2班"),
            R("Class1-3",      1, "1-3班"),
            R("Class1-4",      1, "1-4班"),
            R("Class2-1",      2, "2-1班"),
            R("Class2-2",      2, "2-2班"),
            R("ClassComputer", 1, "计算机教室"),
            R("ClassTeachers", 2, "教师办公室"),
            R("ClassDirector", 2, "主任室"),
            R("Corridor1",     1, "一层走廊"),
            R("Corridor2",     2, "二层走廊"),

            // ---- P19 新增：西翼食堂/礼堂、东翼体育馆 ----
            // 顺序接在走廊后面 = 面板左树的阅读顺序：先教室，再公共区域。
            R("Kitchen",       1, "食堂餐厅"),
            R("ActZal",        2, "礼堂（大教室）"),
            R("SportRoom1",    1, "体育器材室"),
            R("SportRoom2",    1, "体育馆（中区）"),
            R("SportRoom3",    1, "体育馆（东区）"),
            // 【P20】体育馆走廊入口那扇带玻璃的木门（`Door02Wood1`），之前完全没接数据
            R("SportGate",     1, "体育馆 入口"),
        };

        private static Dictionary<string, TwinRoomInfo> _index;

        private static TwinRoomInfo R(string zone, int floor, string name)
        {
            TwinRoomInfo r = new TwinRoomInfo();
            r.Zone = zone;
            r.Building = DefaultBuilding;
            r.Floor = floor;
            r.RoomName = name;
            return r;
        }

        private static void BuildIndex()
        {
            _index = new Dictionary<string, TwinRoomInfo>(Table.Length, StringComparer.Ordinal);
            for (int i = 0; i < Table.Length; i++)
            {
                Table[i].Order = i;
                _index[Table[i].Zone] = Table[i];
            }
        }

        /// <summary>全部登记过的房间，按表内顺序。</summary>
        public static IReadOnlyList<TwinRoomInfo> Rooms
        {
            get
            {
                if (_index == null) BuildIndex();
                return Table;
            }
        }

        /// <summary>取房间信息；表里没有就现场造一个 Floor=0 的（不返回 null，调用方不用判空）。</summary>
        public static TwinRoomInfo Find(string zone)
        {
            if (_index == null) BuildIndex();

            if (string.IsNullOrEmpty(zone)) zone = "";
            TwinRoomInfo info;
            if (_index.TryGetValue(zone, out info)) return info;

            TwinRoomInfo fallback = new TwinRoomInfo();
            fallback.Zone = zone;
            fallback.Building = DefaultBuilding;
            fallback.Floor = UnknownFloor;
            fallback.RoomName = zone;
            fallback.Order = Table.Length + 1;
            return fallback;
        }

        public static string DisplayName(string zone)
        {
            return Find(zone).RoomName;
        }

        public static int FloorOf(string zone)
        {
            return Find(zone).Floor;
        }

        public static int OrderOf(string zone)
        {
            return Find(zone).Order;
        }

        /// <summary>表里出现过的楼层，升序。总控面板用它生成筛选按钮。</summary>
        public static int[] Floors()
        {
            List<int> list = new List<int>(4);
            for (int i = 0; i < Table.Length; i++)
            {
                if (!list.Contains(Table[i].Floor)) list.Add(Table[i].Floor);
            }
            list.Sort();
            return list.ToArray();
        }

        /// <summary>
        /// 给 TwinPointListPanel 用的 {zone, 中文名} 表。
        ///
        /// 【为什么要有这个】点位清单原本自己写了一份 10 行的分区表，和这里完全重复。
        /// 两份表一定会漂移（改了一处忘了另一处），所以让它从这里取。
        /// 返回 string[][] 是为了不动它既有的遍历代码。
        /// </summary>
        public static string[][] ZoneTitleTable()
        {
            string[][] table = new string[Table.Length][];
            for (int i = 0; i < Table.Length; i++)
            {
                table[i] = new string[] { Table[i].Zone, Table[i].RoomName };
            }
            return table;
        }
    }
}

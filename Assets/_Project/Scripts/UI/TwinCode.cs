using System;
using System.Collections.Generic;
using System.Text;
using SchoolTwin.Twin;

namespace SchoolTwin.UI
{
    /// <summary>
    /// 设备编号与文案格式化的**唯一出口**（P29-6 §6）。
    ///
    /// ── 为什么要这一层 ─────────────────────────────────────────────
    /// 原始 TwinId 是 `Zone/Category/Name_序号` 形式（如 `ClassComputer/Heat/Radiator_01`），
    /// 它是**内部主键**，给人看毫无意义，而且带 `/` 会让列表列宽失控（P0-3 就是它把名称列压了）。
    /// 已裁决：**原始 TwinId 不得出现在 UI**。所有界面必须走这里的三个格式：
    ///
    ///   短码    `2F-201`                        —— 列表、动态行
    ///   带类型  `2F-201 空调`                    —— 异常 Top3、右栏告警条
    ///   全码    `1号教学楼-2F-201-空调-001`       —— 详情（窄态浮卡 / 全屏右栏）
    ///
    /// ── 房间号怎么来的 ─────────────────────────────────────────────
    /// 不另建表：直接由 `TwinSiteLayout.Rooms`（17 个房间，带 Floor）推导 ——
    /// 房间号 = 楼层 + 该层在表内的序号，即 `Floor + "F-" + Floor + NN`。
    ///   1F 第 1 个 → `1F-101`，2F 第 5 个 → `2F-205`。
    /// 好处：新增房间只要进 TwinSiteLayout 就自动有编号，不会出现"表里加了、编号没有"。
    /// 坏处：房间在表里换顺序，编号会跟着变 —— 所以**表内顺序就是编号顺序，不要随便挪**。
    /// </summary>
    public static class TwinCode
    {
        /// <summary>楼栋中文名（全码第一段）。当前只有一栋，P15 之后按房间表取。</summary>
        private const string BuildingName = "1号教学楼";

        private static Dictionary<string, string> _rooms;

        // ---------------------------------------------------------------- 房间号

        /// <summary>Zone → `2F-201`。表外分区退化成 Zone 本身（不丢、不报错）。</summary>
        public static string Room(string zone)
        {
            if (string.IsNullOrEmpty(zone)) return "—";
            if (_rooms == null) BuildRooms();

            string code;
            if (_rooms.TryGetValue(zone, out code)) return code;
            return zone;
        }

        private static void BuildRooms()
        {
            _rooms = new Dictionary<string, string>(32, StringComparer.Ordinal);

            IReadOnlyList<TwinRoomInfo> rooms = TwinSiteLayout.Rooms;
            Dictionary<int, int> counter = new Dictionary<int, int>(4);

            for (int i = 0; i < rooms.Count; i++)
            {
                TwinRoomInfo r = rooms[i];
                if (r == null || r.Floor <= 0) continue;

                int n;
                counter.TryGetValue(r.Floor, out n);
                n++;
                counter[r.Floor] = n;

                // Floor=2, n=5 → "2F-205"
                _rooms[r.Zone] = r.Floor + "F-" + r.Floor + n.ToString("00");
            }
        }

        // ---------------------------------------------------------------- 三种格式

        /// <summary>`2F-201`。列表与动态行用。</summary>
        public static string Short(string twinId)
        {
            string zone, category, seq;
            Split(twinId, out zone, out category, out seq);
            return Room(zone);
        }

        /// <summary>`2F-201 空调`。异常 Top3、右栏告警条用。</summary>
        public static string WithType(string twinId)
        {
            string zone, category, seq;
            Split(twinId, out zone, out category, out seq);

            string type = TypeName(category);
            if (string.IsNullOrEmpty(type)) return Room(zone);
            return Room(zone) + " " + type;
        }

        /// <summary>`1号教学楼-2F-201-空调-001`。详情（窄态浮卡 / 全屏右栏）用。</summary>
        public static string Full(string twinId)
        {
            string zone, category, seq;
            Split(twinId, out zone, out category, out seq);

            StringBuilder sb = new StringBuilder(48);
            sb.Append(BuildingName).Append('-').Append(Room(zone));

            string type = TypeName(category);
            if (!string.IsNullOrEmpty(type)) sb.Append('-').Append(type);
            if (!string.IsNullOrEmpty(seq)) sb.Append('-').Append(seq);

            return sb.ToString();
        }

        // ---------------------------------------------------------------- 拆分与映射

        /// <summary>
        /// 把 `Zone/Category/Name_01` 拆成三段。
        /// 格式不符时不抛异常：能取到几段就用几段，取不到的留空 ——
        /// 外部数据接进来时一定会有脏 id，这里崩了会连带整个列表空白。
        /// </summary>
        private static void Split(string twinId, out string zone, out string category, out string seq)
        {
            zone = category = seq = "";
            if (string.IsNullOrEmpty(twinId)) return;

            string[] parts = twinId.Split('/');
            if (parts.Length > 0) zone = parts[0];
            if (parts.Length > 1) category = parts[1];

            // 序号取最后一段最后一个下划线之后的数字（`Temp_01` → `001`）
            string tail = parts[parts.Length - 1];
            int cut = tail.LastIndexOf('_');
            if (cut >= 0 && cut < tail.Length - 1)
            {
                string raw = tail.Substring(cut + 1);
                int n;
                if (int.TryParse(raw, out n)) seq = n.ToString("000");
            }
        }

        /// <summary>
        /// Category → 中文名。未登记的类别原样返回（宁可显示 `Projector` 也不显示空白）。
        /// 新增设备类型时**必须在这里补一行**，否则编号里会漏掉类型段。
        /// </summary>
        public static string TypeName(string category)
        {
            if (string.IsNullOrEmpty(category)) return "";

            switch (category)
            {
                case "AC": return "空调";
                case "Aircon": return "空调";
                case "Light": return "灯";
                case "Lamp": return "灯";
                case "PC": return "电脑";
                case "Projector": return "投影";
                case "Sensor": return "传感器";
                case "Heat": return "暖气";
                case "Radiator": return "暖气";
                case "Door": return "门";
                case "Socket": return "插座";
                case "Meter": return "电表";
                case "Switch": return "开关";
                case "Camera": return "摄像头";
                case "Bell": return "铃";
                case "Speaker": return "广播";
                case "Fan": return "风机";
                case "UPS": return "UPS";
                default: return category;
            }
        }

        // ---------------------------------------------------------------- 量词

        /// <summary>
        /// 量词统一「台」（P29-6 §6.2）。设备计数一律用它，不要各处写死。
        /// 「个监测点位」是例外：只在 L1 第一张卡出现一次，所以不放进这里。
        /// </summary>
        public const string UnitDevice = "台";
    }
}

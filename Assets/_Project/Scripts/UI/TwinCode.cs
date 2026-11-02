using System;
using System.Collections.Generic;
using System.Text;
using SchoolTwin.Twin;

namespace SchoolTwin.UI
{
    /// <summary>
    /// 设备编号与文案格式化的**唯一出口**（P29-6 §6 / P3 编号体系）。
    ///
    /// ── 为什么要这一层 ─────────────────────────────────────────────
    /// 原始 TwinId 是 `Zone/Category/Name_序号` 形式（如 `ClassComputer/Heat/Radiator_01`），
    /// 它是**内部主键**，给人看毫无意义，而且带 `/` 会让列表列宽失控（P0-3 就是它把名称列压了）。
    /// 已裁决：**原始 TwinId 不得出现在 UI**。所有界面必须走这里的三个格式：
    ///
    ///   全码    `1号教学楼2F201教室001灯具`   —— 详情卡（窄态浮卡 / 全屏右栏）
    ///   短码    `2F201-001`                  —— 列表台账
    ///   带类型  `2F201 灯具`                  —— 异常 Top3、右栏告警条
    ///
    /// ── 三层分别解决什么问题（主理人原始诉求）────────────────────────
    /// 「对场景所有模型设备进行编号统计，避免遗漏设备，命名规则说清设备所属具体场景及编号」
    ///   · 全码回答「这台设备到底在哪」→ 楼栋 + 房间 + 房间类型 + 序号 + 设备类型
    ///   · 短码回答「这一行是哪台」→ 列宽只有 140px，塞不下全码，用房间号 + 序号
    ///   · 带类型回答「这是什么」→ Top3 一行里要同时看出位置和类型
    ///
    /// ── 房间号怎么来的（不另建表）────────────────────────────────
    /// 直接由 `TwinSiteLayout.Rooms`（17 个房间，带 Floor）推导：
    /// 房间号 = 楼层 + "F" + 楼层 + 同层序号，即 `2F201`。
    ///   1F 第 1 个 → `1F101`，2F 第 5 个 → `2F205`。
    /// 好处：新增房间只要进 TwinSiteLayout 就自动有编号，不会出现"表里加了、编号没有"。
    /// 坏处：房间在表里换顺序，编号会跟着变 —— 所以**表内顺序就是编号顺序，不要随便挪**。
    /// </summary>
    public static class TwinCode
    {
        /// <summary>楼栋中文名（全码第一段）。当前只有一栋，P15 之后按房间表取。</summary>
        private const string BuildingName = "1号教学楼";

        private static Dictionary<string, string> _rooms;

        // ---------------------------------------------------------------- 房间号

        /// <summary>
        /// Zone → `2F201`。表外分区退化成 Zone 本身（不丢、不报错）。
        ///
        /// 【P3 变更】旧格式是 `2F-201`（带横线），已裁决改为 `2F201` ——
        /// 全码里 `1号教学楼2F201教室…` 连着读才像一句话，中间夹横线会断成三段。
        /// </summary>
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

                // Floor=2, n=5 → "2F205"
                _rooms[r.Zone] = r.Floor + "F" + r.Floor + n.ToString("00");
            }
        }

        // ---------------------------------------------------------------- 三种格式

        /// <summary>`2F201-001`。列表台账用（列宽 140px，全码塞不下）。</summary>
        public static string Short(string twinId)
        {
            string zone, category, seq;
            Split(twinId, out zone, out category, out seq);

            string room = Room(zone);
            if (string.IsNullOrEmpty(seq)) return room;
            return room + "-" + seq;
        }

        /// <summary>`2F201 灯具`。异常 Top3、右栏告警条用（要同时看出位置和类型）。</summary>
        public static string WithType(string twinId)
        {
            string zone, category, seq;
            Split(twinId, out zone, out category, out seq);

            string type = TypeName(category);
            if (string.IsNullOrEmpty(type)) return Room(zone);
            return Room(zone) + " " + type;
        }

        /// <summary>
        /// `1号教学楼2F201教室001灯具`。详情卡用 —— 这是「命名规则说清所属场景及编号」的落地形态。
        ///
        /// 五段：楼栋 + 房间号 + 房间类型 + 设备序号 + 设备类型。
        /// 任一段取不到就跳过（外部脏 id 不能崩，也不能出现 `1号教学楼--001` 这种双横线）。
        /// </summary>
        public static string Full(string twinId)
        {
            string zone, category, seq;
            Split(twinId, out zone, out category, out seq);

            StringBuilder sb = new StringBuilder(48);
            sb.Append(BuildingName).Append(Room(zone));

            string roomType = RoomTypeName(zone);
            if (!string.IsNullOrEmpty(roomType)) sb.Append(roomType);

            if (!string.IsNullOrEmpty(seq)) sb.Append(seq);

            string type = TypeName(category);
            if (!string.IsNullOrEmpty(type)) sb.Append(type);

            return sb.ToString();
        }

        /// <summary>
        /// 房间类型词（全码第三段）：`2-1班` → `教室`、`一层走廊` → `走廊`。
        ///
        /// 【为什么不直接用 RoomName】RoomName 是给人看的口语名（「2-1班」「礼堂（大教室）」），
        /// 直接拼进全码会变成 `1号教学楼2F2012-1班001灯` —— 楼层信息重复一遍，还带括号。
        /// 这里把它归到少数几个类型词上，全码才读得通。
        ///
        /// 归不出来的（表外分区）返回空串，全码就退化成 `1号教学楼X001灯`，不编造类型。
        /// </summary>
        private static string RoomTypeName(string zone)
        {
            TwinRoomInfo info = TwinSiteLayout.Find(zone);
            if (info == null || string.IsNullOrEmpty(info.RoomName)) return "";

            string n = info.RoomName;
            if (n.Contains("走廊")) return "走廊";
            if (n.Contains("办公室")) return "办公室";
            if (n.Contains("主任室")) return "主任室";
            if (n.Contains("体育馆")) return "体育馆";
            if (n.Contains("食堂")) return "食堂";
            if (n.Contains("礼堂")) return "礼堂";
            if (n.Contains("器材室")) return "器材室";
            if (n.Contains("计算机")) return "机房";
            if (n.Contains("班")) return "教室";
            return "";
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
                case "Light": return "灯具";
                case "Lamp": return "灯具";
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

        /// <summary>
        /// 指标 key → 中文名。`SupplyTemp` → `供水温度`。
        ///
        /// 【为什么放在这里】浮卡（TwinDetailPanel）和详情页（TwinDeviceDetailPage）
        /// 都要显示同一批指标名 —— 两处各写一份迟早会不一致（改了一处忘了另一处）。
        /// 这里是文案的唯一出口，和 `TypeName` 同一个道理。
        /// </summary>
        public static string MetricName(string metric)
        {
            switch (metric)
            {
                case "Temperature": return "温度";
                case "Humidity": return "湿度";
                case "CO2": return "CO₂ 浓度";
                case "Brightness": return "亮度";
                case "Power": return "功率";
                case "SetTemp": return "设定温度";
                case "Open": return "门状态";
                case "Voltage": return "电压";
                case "Current": return "电流";
                case "Pressure": return "压力";
                case "Flow": return "流量";
                case "Level": return "液位";
                // P4-6 扩点位新增：暖气 / 机房电脑 / 烟感
                case "SupplyTemp": return "供水温度";
                case "ValveOpen": return "阀门开度";
                case "CPULoad": return "CPU 负载";
                case "Smoke": return "烟雾浓度";
                default: return string.IsNullOrEmpty(metric) ? "指标" : metric;
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

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
    ///   短码    `2F201-灯001`                —— 列表台账
    ///   带类型  `2F201 灯具001`               —— 异常 Top3、右栏告警条
    ///
    /// ── 三层分别解决什么问题（主理人原始诉求）────────────────────────
    /// 「对场景所有模型设备进行编号统计，避免遗漏设备，命名规则说清设备所属具体场景及编号」
    ///   · 全码回答「这台设备到底在哪」→ 楼栋 + 房间 + 房间类型 + 序号 + 设备类型
    ///   · 短码回答「这一行是哪台」→ 列宽有限，塞不下全码，用房间号 + 类型字 + 序号
    ///   · 带类型回答「这是什么」→ Top3 一行里要同时看出位置和类型
    ///
    /// ── 【2026-11-03 修 · 短码撞号 51 台】为什么短码必须带类型字 ──────────
    /// 旧短码是 `2F203-001`，只有「房间 + 原始序号」。而场景里每类设备的序号
    /// **都在自己那一类里从 _01 重新起算**（`Light/Main_01`、`AC/Unit_01`、
    /// `Door/Entrance_01`…），于是同一房间的所有类型全落到同一个短码上。
    ///
    /// 按场景 70 台实算（`SchoolSceneDay.unity` 全部 `_twinId`，剔除 BAK 备份点）：
    ///   · 唯一短码只有 **34** 个 —— 70 台里 **51 台**的编号不是唯一的
    ///   · 撞号 **15** 组，最多一组 **7** 台
    ///   · `1F104-001` 同时指 7 台（机房的门/暖气/灯/主脑/1 号座位/投影/传感器）
    ///   · `2F203-001` 同时指 4 台（教师办公室的暖气/灯/机房电脑/投影）
    /// 这直接违背原始诉求 —— 七成设备没法被单独指认，答辩指着两行同号问
    /// 「这是同一台还是两台」就答不上来。
    ///
    /// 修法是**加类型字**而不是「按房间全局重排序号」：
    ///   · 后者会让「机房 11 号机」的编号突然变成 `-012`，与场景里的座位号对不上
    ///   · 前者只动一个方法，列表/详情/告警条全部自动同步，且编号自带语义
    /// 类型字取**中文单字**（灯/电/暖/门/投/传/碳/铃/消/空/主/组），答辩时能念出来。
    /// 修后 70 台 → **70 个唯一短码，撞号 0**（已实测，见 `Docs/P7_设备编号唯一化_断点.md`）。
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

        /// <summary>`2F203-灯001`。列表台账用（列宽有限，全码塞不下）。</summary>
        public static string Short(string twinId)
        {
            string zone, category, name, seq;
            Split(twinId, out zone, out category, out name, out seq);

            return Room(zone) + "-" + TypeGlyph(category, name) + seq;
        }

        /// <summary>`2F203 灯具001`。异常 Top3、右栏告警条用（要同时看出位置和类型）。</summary>
        public static string WithType(string twinId)
        {
            string zone, category, name, seq;
            Split(twinId, out zone, out category, out name, out seq);

            // 【2026-11-03 第二修 · 用短名而不是全名】
            //
            // 这里原来用 `TypeLabel`（全名）。在 300px 的异常 Top3 里没问题，
            // 但右栏告警条只有 108px —— `2F201 温度传感器001` 实测 109.5px，**差 1.5px**。
            // 超了就是 0：Legacy Text 是 Overflow 模式，不换行不省略，
            // 直接画到右边的告警等级（「警告」/「数据陈旧」）上。
            //
            // 当时用 `FitTextKeepTail` 兜底能挡住溢出，但结果是 `2F201 温度传…001`
            // —— 编号里带省略号，读的人第一反应是"这里是不是没显示全/数据缺了"。
            //
            // 根因不是宽度算错，是**三层编号只给了两档长度**：
            //   短码 1 个字（灯）、全码全名（温度传感器），中间没有过渡。
            // 而 `WithType` 恰恰是用在窄栏的那一层 —— 它需要的不是"全名"，
            // 是"能说清类型的最短名"。所以补 `TypeMid`（2~3 字）给这一层专用，
            // `TypeLabel`（全名）留给 `Full()` 的详情卡（那里宽 300+）。
            //
            // ⚠️ 唯一性必须复核：`TypeMid` 仍要区分同房间的两类传感器
            //    （`2F201 温度001` vs `2F201 CO₂001`），以及机房三类电脑。
            //    已按场景 70 台实测：70 个唯一，最长 `1F101 温度001` = 76.5px。
            string type = TypeMid(category, name);
            if (string.IsNullOrEmpty(type)) return Room(zone);
            return Room(zone) + " " + type + seq;
        }

        /// <summary>
        /// `1号教学楼2F201教室001灯具`。详情卡用 —— 这是「命名规则说清所属场景及编号」的落地形态。
        ///
        /// 五段：楼栋 + 房间号 + 房间类型 + 设备序号 + 设备类型。
        /// 任一段取不到就跳过（外部脏 id 不能崩，也不能出现 `1号教学楼--001` 这种双横线）。
        /// </summary>
        public static string Full(string twinId)
        {
            string zone, category, name, seq;
            Split(twinId, out zone, out category, out name, out seq);

            StringBuilder sb = new StringBuilder(48);
            sb.Append(BuildingName).Append(Room(zone));

            string roomType = RoomTypeName(zone);
            if (!string.IsNullOrEmpty(roomType)) sb.Append(roomType);

            // 【2026-11-03 修 · 房间与设备之间必须有分隔】
            //
            // 改之前拼出来是 `1号教学楼2F201001温度传感器`，现在是 `2F201教室001温度传感器`
            // —— 两段数字（房间号尾部的 `201` 和设备序号 `001`）中间如果只隔一个
            // 房间类型词，读起来仍然像 `2F201教室` 后面接一串数字；而房间类型拿不到的
            // 分区（表外 zone）会直接糊成 `2F201001`，读者分不清是
            // 「房间 2F201 的 1 号」还是「房间 2F2010 的 01 号」。
            //
            // 所以**统一**在「房间段」与「设备序号」之间放一个分隔符，不依赖房间类型是否存在：
            //   有房间类型：`1号教学楼2F201教室 · 001温度传感器`
            //   无房间类型：`1号教学楼2F201 · 001温度传感器`
            //
            // 用 `·` 而不是空格：全码在详情卡里是**唯一**出现位置，
            // 一个显式分隔比空格更能表达"这里是两段不同的信息"，
            // 也让将来（若有）按 `·` 切段解析成为可能。
            if (!string.IsNullOrEmpty(seq))
                sb.Append(" · ").Append(seq);

            // 类型段用**细分名**而不是 `TypeName`：
            // 机房三类电脑（主脑 / 整间 / 座位）与两种传感器（温度 / CO₂）
            // 若都写 `电脑`、`传感器`，全码也会撞 —— 和短码是同一件事，
            // 只是撞在更长的字符串上，更难发现。
            string type = TypeLabel(category, name);
            if (!string.IsNullOrEmpty(type)) sb.Append(type);

            return sb.ToString();
        }

        /// <summary>
        /// Category + 设备名 → **中等长度类型名**（`WithType` 用，2~3 字）。
        ///
        /// ── 三层编号为什么需要"中间这一档" ──────────────────────────────
        /// 原来的设计只有两极：
        ///   · 短码：1 个字（`灯`）—— 列表台账用，列宽 96
        ///   · 全码：全名（`温度传感器`）—— 详情卡用，宽度 300+
        /// 而 `WithType` 用在**窄栏**（右栏告警条 108px、窄态异常 150px），
        /// 借用全名就超宽（109.5px > 108px），只能截成 `温度传…001`。
        /// 编号里带省略号，读者会以为数据没显示全 —— 这是拿可读性换 1.5px，不值。
        ///
        /// 所以这一层要的不是"全名"，是「能说清类型的最短名」：
        ///   温度传感器 → 温度    CO₂传感器 → CO₂    机房电脑 → 机房
        /// 唯一性靠**同房间内不重名**保证，不是靠字数堆。
        ///
        /// ⚠️ 改动这里必须复核唯一性（`Docs/P7` §5 有 70 台对照表）：
        ///    `2F201 温度001` / `2F201 CO₂001` —— 同房间两台传感器不撞 ✔
        ///    `1F104 主脑001` / `1F104 电001` / `1F104 机房001` —— 三类电脑不撞 ✔
        /// </summary>
        private static string TypeMid(string category, string name)
        {
            if (string.IsNullOrEmpty(category)) return "";

            switch (category)
            {
                case "PC":
                    if (string.Equals(name, "Master", StringComparison.OrdinalIgnoreCase)) return "主脑";
                    // 整间聚合点：叫「机房」比「机房电脑」短，且在教师办公室那台也不歧义
                    // （它测的就是那间办公室的整组电脑，不是机房）
                    if (string.Equals(name, "Room", StringComparison.OrdinalIgnoreCase)) return "整组";
                    return "电脑";

                case "Sensor":
                    if (!string.IsNullOrEmpty(name) &&
                        name.StartsWith("CO2", StringComparison.OrdinalIgnoreCase)) return "CO₂";
                    return "温度";

                default:
                    return TypeLabel(category, name);
            }
        }

        /// <summary>
        /// Category + 设备名 → **细分的中文类型名**（全码用，与 <see cref="TypeName"/> 的区别：
        /// `PC/Master_01` → `主脑`、`PC/Seat_07` → `电脑`、`Sensor/CO2_01` → `CO₂传感器`）。
        ///
        /// 【为什么不直接复用 TypeName】TypeName 是「类型的中文名」，用于列表的「类型」列；
        /// 那一列要的是归类（三类电脑都显示「电脑」是对的）。而全码要的是**唯一指认**，
        /// 必须细到能区分这三台。两件事语义不同，不能合一个方法。
        /// </summary>
        private static string TypeLabel(string category, string name)
        {
            if (string.IsNullOrEmpty(category)) return "";

            switch (category)
            {
                case "PC":
                    if (string.Equals(name, "Master", StringComparison.OrdinalIgnoreCase)) return "主脑";
                    if (string.Equals(name, "Room", StringComparison.OrdinalIgnoreCase)) return "机房电脑";
                    return "电脑";

                case "Sensor":
                    if (!string.IsNullOrEmpty(name) &&
                        name.StartsWith("CO2", StringComparison.OrdinalIgnoreCase)) return "CO₂传感器";
                    return "温度传感器";

                default:
                    return TypeName(category);
            }
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
        /// 把 `Zone/Category/Name_01` 拆成四段：分区 / 类别 / 设备名 / 序号。
        /// 格式不符时不抛异常：能取到几段就用几段，取不到的留空 ——
        /// 外部数据接进来时一定会有脏 id，这里崩了会连带整个列表空白。
        ///
        /// 【为什么需要设备名段】类型字要区分同类的不同设备：
        /// `PC/Master_01`（主脑）、`PC/Seat_07`（座位）、`PC/Room_01`（整间）
        /// 三者都是 `PC`，只按 Category 出类型字会得出三个「电」。
        /// `Sensor/Temp_01` 与 `Sensor/CO2_01` 同理。
        /// </summary>
        private static void Split(string twinId, out string zone, out string category,
                                  out string name, out string seq)
        {
            zone = category = name = seq = "";
            if (string.IsNullOrEmpty(twinId)) return;

            string[] parts = twinId.Split('/');
            if (parts.Length > 0) zone = parts[0];
            if (parts.Length > 1) category = parts[1];

            // 序号取最后一段最后一个下划线之后的数字（`Temp_01` → `001`）
            string tail = parts[parts.Length - 1];
            name = tail;

            int cut = tail.LastIndexOf('_');
            if (cut >= 0 && cut < tail.Length - 1)
            {
                string raw = tail.Substring(cut + 1);
                int n;
                if (int.TryParse(raw, out n))
                {
                    seq = n.ToString("000");
                    name = tail.Substring(0, cut);
                }
            }
        }

        /// <summary>
        /// Category + 设备名 → **类型字**（短码里那一个汉字）。这是本文件唯一新增的语义层。
        ///
        /// ── 为什么是单字 ────────────────────────────────────────────────
        /// 短码列宽本来就紧（列表台账），全称 `2F203-灯具001` 会把序号挤出去。
        /// 单字既省宽，又能口头念：「二F二〇三 灯〇〇一」。
        ///
        /// ── 为什么 PC / Sensor 要按设备名细分 ───────────────────────────
        /// 直接按 Category 出字会让三类电脑全叫「电」：
        ///   `PC/Master_01` 主脑、`PC/Seat_07` 座位、`PC/Room_01` 整间
        /// 而这三者在场景里是完全不同的东西（主脑是讲台那块大屏，座位是学生机）。
        /// 只按 Category 判，`1F104-电001` 会同时指主脑和 1 号座位 —— 换了个说法又撞回来。
        /// 传感器同理：`Temp_01`（温度）与 `CO2_01`（CO₂）量的是完全不同的物理量。
        ///
        /// ── 未登记的类别为什么取首字而不是返回空 ─────────────────────────
        /// 返回空会让短码退化成 `2F203-001`，也就是**回到修之前的撞号状态**，
        /// 而且新接一类设备时会静默复发。取首字至少保证「不同 Category 不撞」，
        /// 真要好看，在下面 switch 里补一行即可。
        /// </summary>
        private static string TypeGlyph(string category, string name)
        {
            if (string.IsNullOrEmpty(category)) return "";

            switch (category)
            {
                case "Light":
                case "Lamp":      return "灯";

                case "AC":
                case "Aircon":    return "空";

                case "Heat":
                case "Radiator":  return "暖";

                case "Door":      return "门";

                case "Projector": return "投";

                // PC 三类按设备名分：主脑 / 整间聚合 / 普通座位
                case "PC":
                    if (string.Equals(name, "Master", StringComparison.OrdinalIgnoreCase)) return "主";
                    if (string.Equals(name, "Room", StringComparison.OrdinalIgnoreCase)) return "组";
                    return "电";

                // 传感器按被测物理量分：CO₂ 与温度是两回事
                case "Sensor":
                    if (!string.IsNullOrEmpty(name) &&
                        name.StartsWith("CO2", StringComparison.OrdinalIgnoreCase)) return "碳";
                    return "传";

                case "Bell":
                case "Speaker":   return "铃";

                case "Alarm":     return "消";

                case "Socket":    return "插";
                case "Meter":     return "表";
                case "Switch":    return "开";
                case "Camera":    return "像";
                case "Fan":       return "风";
                case "UPS":       return "U";

                // 未登记类别：取 Category 首字，保证至少不与别的类别撞
                default:          return category.Substring(0, 1);
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

                // 【2026-11-03 补】`Alarm` 原来只有短码字（「消」）没有中文名，
                // 于是 `WithType` 掉进 default 返回英文原串 —— 走廊那台烟感
                // 在告警条上显示成 `1F105 Alarm001`，一条中英混排的编号。
                //
                // 根因是 `TypeGlyph`（短码）与这里（全名）是两张独立的表，
                // 加设备时只补了一张。这类"补一半"只能靠**实跑全部 70 条**发现，
                // 看单个 case 是看不出来的。
                //
                // 叫「烟感」而不是「报警器」：场景里这个点位是 `Alarm/Fire_01`
                // （烟感探测器），叫「报警器」会让人以为是声光报警装置。
                case "Alarm": return "烟感";
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

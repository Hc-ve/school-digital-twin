using System.Collections.Generic;
using UnityEngine;
using SchoolTwin.Data;

namespace SchoolTwin.Twin
{
    /// <summary>
    /// 【P47】室内遗漏设备「只接数据」锚点自举：电棒 / 暖气 / 办公电脑 / 饮水机。
    ///
    /// ══════════════════════════════════════════════════════════════════
    /// 【为什么重开一个脚本（P45 的教训，主理人已裁决）】
    ///
    /// P45 的 InteriorDeviceAnchors 把 123 根电棒注册进 ControlScope →
    /// Presenter 为每盏建 glow 点光源，1467 个发光 quad 叠加 P37 底光层 →
    /// 全场景白夜通吃过曝，主理人 2026-11-18 已 git 回退（commit 2cfdfd5）。
    ///
    /// 本脚本与 P46 的 SportHallLampAnchors 同一 doctrine：**只接数据、
    /// 零 glow**——
    ///   · 挂 TwinAnchor + Configure → 管理系统看得到点位、读得到数据；
    ///   · 钉 MockDataSource override → 不跑正弦假波形；
    ///   · **不碰 TwinControlScope** → Presenter 不会为它们建任何 glow。
    /// 室内灯光视觉仍由已验收的 P37/P39 体系负责，本脚本一行灯光都不碰。
    ///
    /// ── 与 SportHallLampAnchors 的分工 ─────────────────────────────
    ///   · SportHall 的 12 盏 LightLum01_03 已由 P46 接管（Main_01 +
    ///     Tube_097~106 + Spare_01），本脚本**跳过已挂锚点的灯**；
    ///   · 本脚本接管剩余的 LightLum01_01 电棒与暖气/电脑/饮水机。
    ///
    /// ══════════════════════════════════════════════════════════════════
    /// 【编号怎么与 Catalog 对齐（关键正确性）】
    ///
    /// Catalog（P45 生成，保留至今）里电棒编号 Tube_001~123 是按
    /// 「分区推导 → (zone,x,z,name) 全局排序」给**当时未挂锚点的 123 盏**
    /// 编的号；P46 又把其中 SportHall 的 10 盏接到了 Tube_097~106。
    /// 所以本脚本必须**复现同一条全局排序**（分区推导 + 排序口径一致），
    /// 让每盏灯落回它的历史序位：已挂的（P46 的 Tube/Spare）占位校验后跳过，
    /// 未挂的补 Configure——**绝不按当前存活列表重新从 001 编号**，
    /// 那会让 TwinId 与 Catalog 条目整体错位（比漏挂更难查的假象）。
    ///
    /// 已挂锚点中只有 TwinId 以 Main_01 结尾的房间主照明灯**不占** Tube 序位
    /// （P45 生成时它们就被排除在编号之外）。
    ///
    /// ── 播种口径（与既有设备一致）─────────────────────────────────
    ///   · 电棒：Power=42W（P46 同口径）/ Brightness=80（室内灯管同款）；
    ///   · 暖气：ValveOpen=45%（P27 HeatSeedIds 同口径）；
    ///   · 电脑：Power/OnCount/CPULoad=0（办公下班关机态，P26 口径）；
    ///   · 饮水机：Power=840W（加热运行）+ Status=1。
    /// </summary>
    public static class InteriorDeviceDataAnchors
    {
        private const float TubePowerSeed = 42f;
        private const float TubeBrightnessSeed = 80f;
        private const float RadiatorValveSeed = 45f;
        private const float DispenserPowerSeed = 840f;
        private const float DispenserStatusSeed = 1f;

        private static bool _done;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() { _done = false; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_done) return;
            if (TwinRuntime.Instance == null) return;   // 只在 SchoolSceneDay 挂
            _done = true;

            // ── 分区推导基准：既有室内锚点（与 P45 目录生成同源同口径）──
            var roomAnchors = new List<TwinAnchor>();
            var allAnchors = Object.FindObjectsOfType<TwinAnchor>(true);
            for (int i = 0; i < allAnchors.Length; i++)
            {
                TwinAnchor a = allAnchors[i];
                if (a != null && !string.IsNullOrEmpty(a.Zone) &&
                    a.Zone != "Exterior" && a.Zone != "Interior")
                    roomAnchors.Add(a);
            }

            // ── 电棒：LightLum01* 全集（含 P46 已挂的，占位保序）──
            var tubes = new List<Transform>();
            var radiators = new List<Transform>();
            var computers = new List<Transform>();
            var dispensers = new List<Transform>();
            Transform[] all = Object.FindObjectsOfType<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                Transform tr = all[i];
                if (tr == null || !tr.gameObject.activeInHierarchy) continue;
                string n = tr.name;
                if (n.StartsWith("LightLum01", System.StringComparison.Ordinal))
                {
                    // 【编号对齐的关键】P45 目录编号时，已挂 Main_01 的主照明灯被
                    // 排除在序列之外——这里必须同样排除，否则它们在排序表中占位，
                    // 后面所有 Tube 序号整体 +1，与 Catalog 全面错位。
                    var ex = tr.GetComponent<TwinAnchor>();
                    if (ex != null && !string.IsNullOrEmpty(ex.TwinId) &&
                        ex.TwinId.EndsWith("Main_01", System.StringComparison.Ordinal))
                        continue;
                    tubes.Add(tr);
                }
                else if (n.StartsWith("Radiator", System.StringComparison.Ordinal) &&
                         tr.GetComponent<TwinAnchor>() == null)
                    radiators.Add(tr);
                else if (n.StartsWith("Computer03", System.StringComparison.Ordinal) &&
                         tr.GetComponent<TwinAnchor>() == null)
                    computers.Add(tr);
                else if (n.StartsWith("WaterCooler", System.StringComparison.Ordinal) &&
                         tr.GetComponent<TwinAnchor>() == null)
                    dispensers.Add(tr);
            }

            // ── 电棒全局排序（P45 目录生成的同款口径）──
            tubes.Sort(delegate (Transform a, Transform b)
            {
                int c = string.CompareOrdinal(ZoneOf(a.position, roomAnchors), ZoneOf(b.position, roomAnchors));
                if (c != 0) return c;
                if (a.position.x != b.position.x) return a.position.x.CompareTo(b.position.x);
                if (a.position.z != b.position.z) return a.position.z.CompareTo(b.position.z);
                return string.CompareOrdinal(a.name, b.name);
            });

            int tubeMounted = 0, tubeSkippedAnchored = 0, tubeWarn = 0;
            var tubeLog = new System.Text.StringBuilder();
            for (int i = 0; i < tubes.Count; i++)
            {
                Transform tr = tubes[i];
                int seq = i + 1;                                  // 全局序位 = Catalog Tube_NNN

                // 03 型（SportHall 篮球场灯）是 P46 SportHallLampAnchors 的领地：
                // 无论它此刻挂没挂锚点，都只占位不配置，避免两个自举脚本抢写。
                if (tr.name.StartsWith("LightLum01_03", System.StringComparison.Ordinal))
                    continue;

                string expectedId = "Light/Tube_" + seq.ToString("000");

                TwinAnchor existing = tr.GetComponent<TwinAnchor>();
                if (existing != null && !string.IsNullOrEmpty(existing.TwinId))
                {
                    // 已挂（P46 的 Tube_097~106 / Spare_01）：占位保序，校验后跳过。
                    // Spare_01 没有对应 Catalog 条目，序位对不上属 P46 已记录的妥协——以已挂 ID 为准。
                    if (!existing.TwinId.EndsWith(expectedId, System.StringComparison.Ordinal))
                    {
                        Debug.LogWarning("[P47] 电棒 " + tr.name + " 已挂 " + existing.TwinId +
                                         "，全局排序序位是 " + expectedId +
                                         " —— 以已挂 ID 为准（P46 Spare/人工核对项），不覆盖");
                        tubeWarn++;
                    }
                    tubeSkippedAnchored++;
                    continue;
                }

                string zone = ZoneOf(tr.position, roomAnchors);
                string twinId = zone + "/" + expectedId;
                TwinAnchor anchor = tr.gameObject.AddComponent<TwinAnchor>();
                anchor.Configure(twinId, zone, "Light", "电棒 " + seq.ToString("000"));
                MockDataSource.SetOverride(twinId, "Power", TubePowerSeed);
                MockDataSource.SetOverride(twinId, "Brightness", TubeBrightnessSeed);
                tubeMounted++;
                if (tubeLog.Length < 1600)
                    tubeLog.AppendLine("  电棒 " + twinId + " @" + tr.position.ToString("F1"));
            }

            // ── 暖气：分区内序号接在既有 Heat 锚点之后 ──
            var heatCount = new Dictionary<string, int>();
            for (int i = 0; i < roomAnchors.Count; i++)
            {
                TwinAnchor a = roomAnchors[i];
                if (a.Category == "Heat")
                {
                    if (!heatCount.ContainsKey(a.Zone)) heatCount[a.Zone] = 0;
                    heatCount[a.Zone]++;
                }
            }
            radiators.Sort(delegate (Transform a, Transform b)
            {
                int c = string.CompareOrdinal(ZoneOf(a.position, roomAnchors), ZoneOf(b.position, roomAnchors));
                if (c != 0) return c;
                if (a.position.x != b.position.x) return a.position.x.CompareTo(b.position.x);
                return a.position.z.CompareTo(b.position.z);
            });
            int heatMounted = 0;
            var heatCountRun = new Dictionary<string, int>(heatCount);
            for (int i = 0; i < radiators.Count; i++)
            {
                Transform tr = radiators[i];
                string zone = ZoneOf(tr.position, roomAnchors);
                int idx;
                heatCountRun.TryGetValue(zone, out idx);
                idx++;
                heatCountRun[zone] = idx;

                string twinId = zone + "/Heat/Radiator_" + idx.ToString("00");
                TwinAnchor anchor = tr.gameObject.AddComponent<TwinAnchor>();
                anchor.Configure(twinId, zone, "Heat", "暖气 " + idx.ToString("00"));
                MockDataSource.SetOverride(twinId, "ValveOpen", RadiatorValveSeed);
                heatMounted++;
            }

            // ── 办公电脑：分区内 Desk 序号 ──
            computers.Sort(delegate (Transform a, Transform b)
            {
                int c = string.CompareOrdinal(ZoneOf(a.position, roomAnchors), ZoneOf(b.position, roomAnchors));
                if (c != 0) return c;
                if (a.position.x != b.position.x) return a.position.x.CompareTo(b.position.x);
                return a.position.z.CompareTo(b.position.z);
            });
            var deskCount = new Dictionary<string, int>();
            int pcMounted = 0;
            for (int i = 0; i < computers.Count; i++)
            {
                Transform tr = computers[i];
                string zone = ZoneOf(tr.position, roomAnchors);
                int ord;
                deskCount.TryGetValue(zone, out ord);
                ord++;
                deskCount[zone] = ord;

                string twinId = zone + "/PC/Desk_" + ord.ToString("00");
                TwinAnchor anchor = tr.gameObject.AddComponent<TwinAnchor>();
                anchor.Configure(twinId, zone, "PC", "办公电脑 " + ord.ToString("00"));
                MockDataSource.SetOverride(twinId, "Power", 0f);
                MockDataSource.SetOverride(twinId, "OnCount", 0f);
                MockDataSource.SetOverride(twinId, "CPULoad", 0f);
                pcMounted++;
            }

            // ── 饮水机：分区内 Dispenser 序号 ──
            dispensers.Sort(delegate (Transform a, Transform b)
            {
                int c = string.CompareOrdinal(ZoneOf(a.position, roomAnchors), ZoneOf(b.position, roomAnchors));
                if (c != 0) return c;
                return string.CompareOrdinal(a.name, b.name);
            });
            var dispCount = new Dictionary<string, int>();
            int dispMounted = 0;
            for (int i = 0; i < dispensers.Count; i++)
            {
                Transform tr = dispensers[i];
                string zone = ZoneOf(tr.position, roomAnchors);
                int ord;
                dispCount.TryGetValue(zone, out ord);
                ord++;
                dispCount[zone] = ord;

                string twinId = zone + "/Water/Dispenser_" + ord.ToString("00");
                TwinAnchor anchor = tr.gameObject.AddComponent<TwinAnchor>();
                anchor.Configure(twinId, zone, "Dispenser", "饮水机 " + ord.ToString("00"));
                MockDataSource.SetOverride(twinId, "Power", DispenserPowerSeed);
                MockDataSource.SetOverride(twinId, "Status", DispenserStatusSeed);
                dispMounted++;
            }

            Debug.Log("[P47] 室内设备只接数据自举完成：电棒补挂 " + tubeMounted +
                      "（占位跳过已挂 " + tubeSkippedAnchored + "，告警 " + tubeWarn +
                      "）/ 暖气 " + heatMounted + " / 办公电脑 " + pcMounted +
                      " / 饮水机 " + dispMounted +
                      "。全部未注册 ControlScope → 零 glow，灯光视觉归 P37/P39 体系" +
                      (tubeLog.Length > 0 ? "\n" + tubeLog.ToString() : ""));
        }

        /// <summary>分区归属：score = dxz + |dy|×2，最近室内锚点；超限 → Interior（与 P45 同口径）。</summary>
        private static string ZoneOf(Vector3 p, List<TwinAnchor> roomAnchors)
        {
            string best = "Interior";
            float bestScore = 26f;
            for (int i = 0; i < roomAnchors.Count; i++)
            {
                TwinAnchor a = roomAnchors[i];
                Vector3 ap = a.transform.position;
                float dxz = Vector2.Distance(new Vector2(ap.x, ap.z), new Vector2(p.x, p.z));
                float score = dxz + Mathf.Abs(ap.y - p.y) * 2f;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = a.Zone;
                }
            }
            return best;
        }
    }
}

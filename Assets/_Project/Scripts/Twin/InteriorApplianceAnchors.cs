using System.Collections.Generic;
using UnityEngine;
using SchoolTwin.Data;

namespace SchoolTwin.Twin
{
    /// <summary>
    /// 【P48】室内小电器「只接数据」锚点自举：挂钟 / 笔记本电脑 / 教室投影仪
    /// （主理人 2026-11-19 委托清单：时钟、电脑、笔记本电脑、音响）。
    ///
    /// ══════════════════════════════════════════════════════════════════
    /// 【普查结论（MCP 实测）】
    ///   · 挂钟 Clock1/1a/1b/2a/2b 变体 ×7，0 锚；
    ///   · 笔记本 Notebook01 ×1，0 锚；
    ///   · 教室投影仪 Projector01 共 12 盏，8 盏已有锚点（P19 两台可控 +
    ///     机房等），4 台未接；
    ///   · 音响：场景/资产里没有独立音响模型——广播/打铃已由
    ///     Corridor1/Bell/Speaker_01 覆盖（P19），本清单无音响可接。
    ///   · Computer06 ×19 是机房主机（Seat/Master 锚点在显示器上），
    ///     Computer03 ×5 已由 P47 接入——均不重复接。
    ///
    /// 【严格遵循 P46 doctrine：只接数据、零 glow、不碰 ControlScope】
    /// P45 的过曝教训（123 电棒 glow 叠加）与主理人回退裁决见
    /// SportHallLampAnchors 类注释。本脚本全部设备：
    ///   · 挂 TwinAnchor + Configure → 管理系统可见、可读；
    ///   · 钉 MockDataSource override → 数据不跑假波形；
    ///   · 不注册 ControlScope → 无按钮、无 glow、无过曝链。
    ///
    /// ══════════════════════════════════════════════════════════════════
    /// 【挂钟的特殊设计：上报"系统时间"】
    ///
    /// 挂钟不是功率型设备，它唯一有孪生价值的数据是**它显示的时间**。
    /// 所以 Metric 只有一个 ClockTime（小时 0~24），由 runner 每秒把
    /// TwinClimate.HourOfDay 写进 override——挂钟与校园管理系统共用
    /// 同一个时间源（真实时钟/表盘拨动都会反映到挂钟读数上），
    /// 答辩可讲"孪生体与场景时钟同源同步"。
    /// </summary>
    public static class InteriorApplianceAnchors
    {
        private const float NotebookPowerSeed = 0f;
        private const float ProjectorPowerSeed = 0f;
        private const float ProjectorLampHoursSeed = 1240f;

        private static bool _done;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() { _done = false; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_done) return;
            if (TwinRuntime.Instance == null) return;   // 只在孪生场景挂
            _done = true;

            // ── 分区推导基准：既有室内锚点（与 P45/P47 同口径）──
            var roomAnchors = new List<TwinAnchor>();
            var allAnchors = Object.FindObjectsOfType<TwinAnchor>(true);
            for (int i = 0; i < allAnchors.Length; i++)
            {
                TwinAnchor a = allAnchors[i];
                if (a != null && !string.IsNullOrEmpty(a.Zone) &&
                    a.Zone != "Exterior" && a.Zone != "Interior")
                    roomAnchors.Add(a);
            }

            // ── 扫描候选（激活、未挂锚点；排除投影幕布 ProjectorScreen）──
            var clocks = new List<Transform>();
            var laptops = new List<Transform>();
            var projectors = new List<Transform>();
            Transform[] all = Object.FindObjectsOfType<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                Transform tr = all[i];
                if (tr == null || !tr.gameObject.activeInHierarchy) continue;
                if (tr.GetComponent<TwinAnchor>() != null) continue;
                string n = tr.name;
                if (n.StartsWith("ProjectorScreen", System.StringComparison.Ordinal)) continue;
                if (n.StartsWith("Clock", System.StringComparison.Ordinal))
                    clocks.Add(tr);
                else if (n.StartsWith("Notebook", System.StringComparison.Ordinal))
                    laptops.Add(tr);
                else if (n.StartsWith("Projector", System.StringComparison.Ordinal))
                    projectors.Add(tr);
            }

            // ── 确定性排序 ──
            clocks.Sort(delegate (Transform a, Transform b)
            {
                int c = string.CompareOrdinal(ZoneOf(a.position, roomAnchors), ZoneOf(b.position, roomAnchors));
                if (c != 0) return c;
                if (a.position.x != b.position.x) return a.position.x.CompareTo(b.position.x);
                return string.CompareOrdinal(a.name, b.name);
            });
            laptops.Sort(delegate (Transform a, Transform b)
            {
                int c = string.CompareOrdinal(ZoneOf(a.position, roomAnchors), ZoneOf(b.position, roomAnchors));
                if (c != 0) return c;
                return string.CompareOrdinal(a.name, b.name);
            });
            projectors.Sort(delegate (Transform a, Transform b)
            {
                int c = string.CompareOrdinal(ZoneOf(a.position, roomAnchors), ZoneOf(b.position, roomAnchors));
                if (c != 0) return c;
                if (a.position.x != b.position.x) return a.position.x.CompareTo(b.position.x);
                return string.CompareOrdinal(a.name, b.name);
            });

            // ── 挂载：挂钟（ClockTime 由 runner 每秒刷新）──
            var clockCount = new Dictionary<string, int>();
            var clockIds = new List<string>();
            int mounted = 0;
            var log = new System.Text.StringBuilder();
            for (int i = 0; i < clocks.Count; i++)
            {
                Transform tr = clocks[i];
                string zone = ZoneOf(tr.position, roomAnchors);
                int ord;
                clockCount.TryGetValue(zone, out ord);
                ord++;
                clockCount[zone] = ord;

                string twinId = zone + "/Clock/Wall_" + ord.ToString("00");
                TwinAnchor anchor = tr.gameObject.AddComponent<TwinAnchor>();
                anchor.Configure(twinId, zone, "Clock", "挂钟 " + ord.ToString("00"));
                clockIds.Add(twinId);
                log.AppendLine("  挂钟 " + twinId + " @" + tr.position.ToString("F1"));
                mounted++;
            }

            // ── 笔记本电脑：PC 类（关机态）──
            for (int i = 0; i < laptops.Count; i++)
            {
                Transform tr = laptops[i];
                string zone = ZoneOf(tr.position, roomAnchors);
                string twinId = zone + "/PC/Laptop_01";
                TwinAnchor anchor = tr.gameObject.AddComponent<TwinAnchor>();
                anchor.Configure(twinId, zone, "PC", "笔记本电脑");
                MockDataSource.SetOverride(twinId, "Power", NotebookPowerSeed);
                MockDataSource.SetOverride(twinId, "OnCount", 0f);
                MockDataSource.SetOverride(twinId, "CPULoad", 0f);
                log.AppendLine("  笔记本 " + twinId + " @" + tr.position.ToString("F1"));
                mounted++;
            }

            // ── 教室投影仪：Projector 类（关机态；灯泡时长保留）──
            var projCount = new Dictionary<string, int>();
            for (int i = 0; i < projectors.Count; i++)
            {
                Transform tr = projectors[i];
                string zone = ZoneOf(tr.position, roomAnchors);
                int ord;
                projCount.TryGetValue(zone, out ord);
                ord++;
                projCount[zone] = ord;

                string twinId = zone + "/Projector/Proj_" + ord.ToString("00");
                TwinAnchor anchor = tr.gameObject.AddComponent<TwinAnchor>();
                anchor.Configure(twinId, zone, "Projector", "投影仪 " + ord.ToString("00"));
                MockDataSource.SetOverride(twinId, "Power", ProjectorPowerSeed);
                MockDataSource.SetOverride(twinId, "LampHours", ProjectorLampHoursSeed);
                log.AppendLine("  投影仪 " + twinId + " @" + tr.position.ToString("F1"));
                mounted++;
            }

            Debug.Log("[P48] 室内小电器只接数据自举完成：挂钟 " + clocks.Count +
                      " / 笔记本 " + laptops.Count + " / 教室投影仪 " + projectors.Count +
                      "，共挂载 " + mounted + "。全部未注册 ControlScope → 零 glow\n" +
                      log.ToString());

            // ── runner：挂钟走时（每秒把系统时间写进 ClockTime override）──
            if (clockIds.Count > 0)
            {
                GameObject go = new GameObject("[InteriorApplianceClocks]");
                var runner = go.AddComponent<ApplianceClockRunner>();
                runner.Init(clockIds);
            }
        }

        /// <summary>分区归属：score = dxz + |dy|×2，最近室内锚点（与 P45/P47 同口径）。</summary>
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

        /// <summary>挂钟走时 runner：每秒把 TwinClimate.HourOfDay 写进各挂钟 ClockTime。</summary>
        private sealed class ApplianceClockRunner : MonoBehaviour
        {
            private List<string> _ids;
            private float _nextWrite;

            public void Init(List<string> ids)
            {
                _ids = ids;
                WriteClocks();
            }

            private void Update()
            {
                if (_ids == null) return;
                if (Time.unscaledTime < _nextWrite) return;
                _nextWrite = Time.unscaledTime + 1f;
                WriteClocks();
            }

            private void WriteClocks()
            {
                if (_ids == null) return;
                double h = TwinClimate.HourOfDay;
                for (int i = 0; i < _ids.Count; i++)
                    MockDataSource.SetOverride(_ids[i], "ClockTime", (float)h);
            }

            private void OnDestroy()
            {
                if (_ids == null) return;
                for (int i = 0; i < _ids.Count; i++)
                    MockDataSource.ClearOverride(_ids[i], "ClockTime");
                _ids = null;
            }
        }
    }
}

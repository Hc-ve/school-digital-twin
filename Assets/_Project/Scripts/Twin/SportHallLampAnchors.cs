using System.Collections.Generic;
using UnityEngine;
using SchoolTwin.Data;

namespace SchoolTwin.Twin
{
    /// <summary>
    /// 【P46】体育馆 12 盏高位灯（LightLum01_03）锚点自举。
    ///
    /// ══════════════════════════════════════════════════════════════════
    /// 【为什么单独写一个脚本，而不是恢复 InteriorDeviceAnchors.cs】
    /// 那个脚本在 P45 造成过全场景过曝（真凶链：RegisterDynamic → ControlScope
    /// → TwinDevicePresenter.ApplyLamp 给每盏建 intensity=2.0 点光源，123 盏叠加
    /// P37 底光层）。主理人 2026-11-18 已裁决回退它。
    ///
    /// 本脚本只做「数据接入」，**刻意不调 TwinControlScope.RegisterDynamic**：
    ///   · 挂 TwinAnchor + Configure → 校园管理系统能看到这 12 个点位、能读数据；
    ///   · 钉 MockDataSource override → Power/LampHours/Status 不跑正弦假波形；
    ///   · 不进 ControlScope → Presenter 不会为它们建任何 glow 点光源 → 零过曝。
    /// 体育馆照明视觉仍由已验收的 P37/P39 补光体系负责，本脚本一行灯光都不碰。
    ///
    /// ══════════════════════════════════════════════════════════════════
    /// 【TwinId 映射】Catalog 里 SportHall 有 11 条：
    ///   SportHall/Light/Main_01（体育馆主照明）
    ///   SportHall/Light/Tube_097~106（篮球场电棒 001~010）
    /// 场景里 12 盏 LightLum01_03：#08 (5.6,7.4,1.8) 已挂 Main_01（场景手工），
    /// 其余按 z→x 排序稳定映射到 Tube_097~106（10 条）。Catalog 比场景少 1 条，
    /// 多出来的那盏挂 Spare_01 并 LogWarning 要求补目录 —— **不造 Tube_107 幽灵 ID**
    /// （那会让面板显示一个数据侧永远不存在的点位，是比漏挂更难查的假象）。
    /// 排序口径与 ExteriorLampAnchors 一致（场景固定 → TwinId 跨 Play 稳定），不用实例 ID 或 GUID。
    /// </summary>
    public static class SportHallLampAnchors
    {
        private const string LampName = "LightLum01_03";
        private const string Zone = "SportHall";
        private const string Category = "Light";
        private const string MainId = "SportHall/Light/Main_01";
        private const int TubeFirst = 97;
        private const int TubeLast = 106;     // Catalog 里真实存在的最后一条（共 10 条）

        private const float PowerOn = 42f;      // 与室内灯管同口径（0~120W，警告线 110）
        private const float LampHours = 1240f;  // 与投影仪/路灯同口径，累计量保留

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() { _done = false; }

        private static bool _done;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_done) return;
            if (TwinRuntime.Instance == null) return;   // 只在 SchoolSceneDay 挂
            _done = true;

            var lamps = new List<Transform>();
            foreach (Transform tr in Object.FindObjectsOfType<Transform>(true))
            {
                if (tr != null && tr.name.StartsWith(LampName, System.StringComparison.Ordinal))
                    lamps.Add(tr);
            }
            lamps.Sort(delegate(Transform a, Transform b)
            {
                int c = a.position.z.CompareTo(b.position.z);
                return c != 0 ? c : a.position.x.CompareTo(b.position.x);
            });

            int mounted = 0, tubeNo = TubeFirst, spare = 0;
            for (int i = 0; i < lamps.Count; i++)
            {
                Transform tr = lamps[i];
                var existing = tr.GetComponent<TwinAnchor>();

                string twinId;
                if (existing != null && !string.IsNullOrEmpty(existing.TwinId) &&
                    existing.TwinId == MainId)
                {
                    // 场景手工已挂主照明：保留，不覆盖
                    twinId = MainId;
                }
                else if (tubeNo <= TubeLast)
                {
                    // 只分配 Catalog 里真实存在的条目，绝不造 Tube_107 这种幽灵 ID
                    twinId = Zone + "/" + Category + "/Tube_" + tubeNo.ToString("000");
                    tubeNo++;
                    if (existing == null) existing = tr.gameObject.AddComponent<TwinAnchor>();
                    existing.Configure(twinId, Zone, Category,
                        "体育馆（篮球场） 电棒 " + (tubeNo - TubeFirst).ToString("000"));
                }
                else
                {
                    // Catalog 条目用尽的灯：给一个明确可查的备用 ID 并告警。
                    // 不静默跳过（那样管理系统里会凭空少一盏灯），也不伪造 Catalog 已存在的 ID。
                    spare++;
                    twinId = Zone + "/" + Category + "/Spare_" + spare.ToString("00");
                    if (existing == null) existing = tr.gameObject.AddComponent<TwinAnchor>();
                    existing.Configure(twinId, Zone, Category,
                        "体育馆 备用高位灯 " + spare.ToString("00") + "（Catalog 待补）");
                    Debug.LogWarning("[P46] 高位灯 " + tr.name + " @" + tr.position.ToString("F1") +
                                     " 没有对应 Catalog 条目，挂为 " + twinId +
                                     " —— 请在 TwinCatalog_Mock.asset 补一条，不要忽略");
                }

                // 钉数据：不进 ControlScope，只写 override（优先级高于 MockSignalGenerator 波形）
                MockDataSource.SetOverride(twinId, "Power", PowerOn);
                MockDataSource.SetOverride(twinId, "LampHours", LampHours);
                MockDataSource.SetOverride(twinId, "Status", 1f);
                mounted++;
            }

            Debug.Log("[P46] 体育馆高位灯锚点接入完成：共 " + mounted + " 盏（" +
                      MainId + " + Tube_" + TubeFirst.ToString("000") + "~" + (tubeNo - 1).ToString("000") +
                      (spare > 0 ? " + 待补目录 " + spare + " 盏" : "") +
                      "），未注册 ControlScope → 零 glow 点光源，不触发过曝");
        }
    }
}

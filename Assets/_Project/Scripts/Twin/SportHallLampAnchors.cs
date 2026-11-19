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
    /// 【TwinId 映射】Catalog 里 SportHall 有 12 条（P47 补齐后）：
    ///   SportHall/Light/Main_01（体育馆主照明）
    ///   SportHall/Light/Tube_097~106（篮球场电棒 001~010）
    ///   SportHall/Light/Tube_124（篮球场电棒 011 —— P47 补录，见下）
    /// 场景里 12 盏 LightLum01_03：#08 (5.6,7.4,1.8) 已挂 Main_01（场景手工），
    /// 其余按 z→x 排序稳定映射到 Tube_097~106（10 条），最后一盏映射 Tube_124。
    ///
    /// 【为什么是 Tube_124 而不是主理人最初提的 Tube_107】
    /// Tube_107 在 Catalog 里已被 SportRoom1 占用（体育器材室 电棒 001，
    /// P45 全局连号 001~123 的既有序位）——再造一个 SportHall/Light/Tube_107
    /// 会 TwinId 撞车。所以补录走全局下一个空位 Tube_124，编号不连号但
    /// 唯一且稳定；P46 原来的 Spare_01 妥协就此消灭。
    /// 排序口径与 ExteriorLampAnchors 一致（场景固定 → TwinId 跨 Play 稳定），不用实例 ID 或 GUID。
    /// </summary>
    public static class SportHallLampAnchors
    {
        private const string LampName = "LightLum01_03";
        private const string Zone = "SportHall";
        private const string Category = "Light";
        private const string MainId = "SportHall/Light/Main_01";
        private const int TubeFirst = 97;
        private const int TubeLast = 106;     // Catalog 里真实存在的连续段（共 10 条）
        private const int TubeExtra = 124;    // 【P47】第 11 盏：Catalog 已补录（跳过被 SportRoom1 占用的 107）

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
                    // 只分配 Catalog 里真实存在的条目
                    twinId = Zone + "/" + Category + "/Tube_" + tubeNo.ToString("000");
                    tubeNo++;
                    if (existing == null) existing = tr.gameObject.AddComponent<TwinAnchor>();
                    existing.Configure(twinId, Zone, Category,
                        "体育馆（篮球场） 电棒 " + (tubeNo - TubeFirst).ToString("000"));
                }
                else if (spare == 0)
                {
                    // 第 11 盏（Catalog 原比场景少 1 条）：P47 已补录 Tube_124，指向它。
                    // 不再造 Spare_01 这种"面板上有、Catalog 里没有"的幽灵 ID。
                    spare++;
                    twinId = Zone + "/" + Category + "/Tube_" + TubeExtra.ToString("000");
                    if (existing == null) existing = tr.gameObject.AddComponent<TwinAnchor>();
                    existing.Configure(twinId, Zone, Category,
                        "体育馆（篮球场） 电棒 " + (TubeExtra - TubeFirst + 1).ToString("000"));
                }
                else
                {
                    // 超出 12 盏的设计数量（场景被美术加灯）才走备用告警。
                    spare++;
                    twinId = Zone + "/" + Category + "/Spare_" + spare.ToString("00");
                    if (existing == null) existing = tr.gameObject.AddComponent<TwinAnchor>();
                    existing.Configure(twinId, Zone, Category,
                        "体育馆 备用高位灯 " + spare.ToString("00") + "（Catalog 待补）");
                    Debug.LogWarning("[P46] 高位灯 " + tr.name + " @" + tr.position.ToString("F1") +
                                     " 超出设计数量（12 盏），挂为 " + twinId +
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
                      (spare > 0 ? " + Tube_" + TubeExtra.ToString("000") : "") +
                      "），未注册 ControlScope → 零 glow 点光源，不触发过曝");
        }
    }
}

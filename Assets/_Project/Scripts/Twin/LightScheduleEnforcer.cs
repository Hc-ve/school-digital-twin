using System.Collections.Generic;
using UnityEngine;
using SchoolTwin.Data;

namespace SchoolTwin.Twin
{
    /// <summary>
    /// 【P48】全灯源默认日程：所有照明类设备 19:00–07:00 默认开灯，其余时段默认关灯
    /// （主理人 2026-11-19 委托，把 P44 只给路灯做的昼夜联动推广到全部灯源）。
    ///
    /// ══════════════════════════════════════════════════════════════════
    /// 【范围与口径】
    ///
    ///   · 范围：场景里所有 Category=Light 的锚点（房间主照明 Main_01、
    ///     电棒 Tube、路灯 RoadLamp、体育馆灯），**跳过 Zone=Exterior**——
    ///     路灯的昼夜联动已由 ExteriorLampAnchors 负责，双写无意义。
    ///   · ON：Power=42W / Brightness=80（与既有播种同口径，P46 先例）；
    ///     OFF：Power=0 / Brightness=0。
    ///   · 窗口与路灯同一张表：19:00–07:00（主理人口径），读
    ///     TwinClimate.HourOfDay，时刻来源（真实时钟/表盘）天然统一。
    ///   · 走 MockDataSource.SetOverride —— 与设备播种/用户点按钮同一数据出口，
    ///     表现层 SyncLamp 自动跟随（室内灯 Power>0.5 → 灯管发光）。
    ///
    /// ══════════════════════════════════════════════════════════════════
    /// 【为什么不逐帧重写——"默认"与"用户操作"的边界】
    ///
    /// 只在**窗口翻转的那一拍**批量写入（开→关 / 关→开），两次翻转之间
    /// 一行都不写。这样用户在两次翻转之间手动开关任何一盏灯，都稳定生效；
    /// 下一次翻转（早 7 点/晚 7 点）恢复"默认"，语义正是主理人要的
    /// "默认开启时间 19:00–07:00，其他时间默认关闭"。
    /// 逐帧重写会跟用户命令打架——P26 已为此踩过一次（"状态被波形改掉"）。
    ///
    /// 【首拍为什么要延迟 2.5 秒】MockDeviceAgent.Start（首帧）会给所有
    /// 受控灯播种"开灯态"（Power=42/Brightness=80）。本类 Init 在
    /// AfterSceneLoad 跑得比它早，若立即按窗口写"关"，会被播种覆盖回去。
    /// 延迟 2.5s 再对账一次，播种/日程两个写入源就收敛到同一状态。
    /// </summary>
    public static class LightScheduleEnforcer
    {
        private const float FirstApplyDelay = 2.5f;
        private const float PollSeconds = 0.5f;
        private const float OnFromHour = 19.0f;
        private const float OnToHour = 7.0f;
        private const float PowerOnWatt = 42f;
        private const float BrightnessOn = 80f;

        private static bool _done;
        private static ScheduleRunner _runner;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() { _done = false; _runner = null; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_done) return;
            if (TwinRuntime.Instance == null) return;   // 只在孪生场景挂
            _done = true;

            GameObject go = new GameObject("[LightSchedule]");
            _runner = go.AddComponent<ScheduleRunner>();
        }

        private sealed class ScheduleRunner : MonoBehaviour
        {
            private float _nextPoll;
            private bool _applied;
            private bool _lastOn;

            private void Update()
            {
                if (!_applied)
                {
                    // 首拍延迟：等 MockDeviceAgent.Start 的播种先落盘，再对账日程
                    if (Time.time < FirstApplyDelay) return;
                    _applied = true;
                    _lastOn = InWindow();
                    ApplyAll(true);
                    _nextPoll = Time.time + PollSeconds;
                    return;
                }

                if (Time.time < _nextPoll) return;
                _nextPoll = Time.time + PollSeconds;

                // 【只在窗口状态变化时写】——两次翻转之间一行都不写，
                // 用户手动开关的 override 不被踩（"默认 vs 用户操作"边界）。
                bool on = InWindow();
                if (on != _lastOn)
                {
                    _lastOn = on;
                    ApplyAll(on);
                }
            }

            /// <summary>开灯窗口判定：19:00–07:00（主理人口径）∧ Exterior 回路之外。</summary>
            private static bool InWindow()
            {
                double h = TwinClimate.HourOfDay % 24.0;
                if (h < 0.0) h += 24.0;
                return h >= OnFromHour || h < OnToHour;
            }

            /// <summary>
            /// 把当前窗口状态写到所有照明类锚点（仅窗口翻转时调用）。
            /// </summary>
            private void ApplyAll(bool on)
            {
                float power = on ? PowerOnWatt : 0f;
                float brightness = on ? BrightnessOn : 0f;

                TwinAnchor[] anchors = Object.FindObjectsOfType<TwinAnchor>(true);
                int written = 0;
                for (int i = 0; i < anchors.Length; i++)
                {
                    TwinAnchor a = anchors[i];
                    if (a == null || a.Category != "Light") continue;
                    if (a.Zone == "Exterior") continue;   // 路灯联动归 ExteriorLampAnchors
                    MockDataSource.SetOverride(a.TwinId, "Power", power);
                    MockDataSource.SetOverride(a.TwinId, "Brightness", brightness);
                    written++;
                }

                Debug.Log("[P48] 灯源日程窗口翻转：" +
                          (on ? "开灯（19:00–07:00 窗口内）" : "关灯（日间默认关）") +
                          "，已写 " + written + " 个照明点位（Power=" + power + "W，Brightness=" + brightness + "）");
            }
        }
    }
}

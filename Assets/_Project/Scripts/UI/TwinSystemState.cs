using System.Collections.Generic;
using SchoolTwin.Twin;
using UnityEngine;

namespace SchoolTwin.UI
{
    /// <summary>
    /// 校园管理系统的**跨视图共享状态**（窄态抽屉 / 全屏最大化 共用一份）。
    ///
    /// ── 为什么要有这个类 ────────────────────────────────────────────
    /// 同一个「管理系统」有两套视图骨架（窄态 420 单栏、全屏 1000 三栏）。
    /// 视图可以有两套（响应式布局本来就该这样），但**状态不能有两份**：
    ///   · 窄态选了「1F」，切全屏又变回「全部」—— 用户会以为操作丢了；
    ///   · 电力两步确认的窗口期若各存一份，窄态点第一次、全屏点第二次就确认不上。
    ///
    /// 行业里的做法叫「状态提升」：状态放到两套视图的共同祖先（这里就是静态类），
    /// 视图只负责「读它来画」和「把点击写成它」。这样交互逻辑只有一份。
    ///
    /// ── 为什么用静态类而不是 MonoBehaviour ──────────────────────────
    /// 无生命周期、无 Inspector 配置、跨场景不需要重建 —— 和 TwinUIState 同一套理由。
    /// </summary>
    public static class TwinSystemState
    {
        // ================================================================ 设备管理

        /// <summary>
        /// 楼层筛选：0 = 全部，1 = 1F，2 = 2F，3 = 室外（楼层字段为 0 的分区）。
        /// 窄态与全屏读同一个值，切换视图时筛选状态连续。
        /// </summary>
        public static int FloorFilter { get; set; }

        /// <summary>
        /// 【P45】设备类型分组筛选："" = 全部；其余为分组键
        /// （Light / Climate / Compute / Water / Access / Env，
        ///  映射见 TwinSystemPage.CategoryInGroup）。
        /// 设备总量上了 350 台（路灯 + 电棒 + 暖气 + 电脑…），只按楼层分已经
        /// 归纳不动了 —— 类型分组是主理人 2026-11-18"重新划分设计归纳"裁决的落地。
        /// </summary>
        public static string DeviceCategoryFilter { get; set; } = "";

        /// <summary>【P45】搜索关键字：匹配 TwinId 或 DisplayName（不分大小写）。空串 = 不过滤。</summary>
        public static string DeviceSearchQuery { get; set; } = "";

        // ================================================================ 告警中心

        /// <summary>false = 进行中，true = 历史。两套视图共用。</summary>
        public static bool AlarmHistory { get; set; }

        // ================================================================ 电力管理
        //
        // 【P3 变更】原来这里存着"两步确认装填窗口"（ArmedCircuit / ArmedUntil / TryArm），
        // 那是上一轮自己手写的简化版。已裁决改用项目现成的 TwinSboGate + TwinSboDialog：
        // 它会把影响设备名单摊开、有可见倒计时、有作废提示，比"再点一次确认"这句话
        // 更能说明"为什么这次操作是安全的"。装填状态改由 TwinSboDialog 自己持有，
        // 所以本类不再需要任何电力字段。

        /// <summary>某条回路当前是否正在 SBO 流程中（弹窗占着操作）。</summary>
        public static bool IsCircuitBusy(string circuitId, TwinSboDialog dialog)
        {
            if (dialog == null || !dialog.IsActive) return false;
            TwinSboRequest req = dialog.Gate != null ? dialog.Gate.Request : null;
            return req != null && string.Equals(req.CircuitId, circuitId, System.StringComparison.Ordinal);
        }

        // ================================================================ 复位

        /// <summary>
        /// 收集某条回路下的设备（SBO 弹窗的「影响名单」）。
        ///
        /// ── 为什么按层级过滤而不是遍历回路的子节点 ────────────────────
        /// `TwinCircuitNode` 只存 Id/ParentId/Level/Zone/Floor，没有"子设备列表"。
        /// 真正要断电的是 `TwinAnchor`，它挂在 TwinDeviceStats 里、按 Zone 归属。
        /// 所以按 Level 反查：楼栋 = 全部，楼层 = 该层所有房间，房间 = 该 Zone。
        ///
        /// ── 为什么输出全码 ────────────────────────────────────────────
        /// 这份名单是给人在断电**之前**看的 —— 「要断的是哪 12 台」必须一眼认得出位置，
        /// 短码 `2F201-001` 在弹窗里排成一列很难对，全码 `1号教学楼2F201教室001灯具` 才对得上。
        /// </summary>
        public static void CollectTargets(TwinCircuitNode node, List<string> into)
        {
            if (into == null) return;
            into.Clear();
            if (node == null) return;

            IReadOnlyList<TwinAnchor> all = TwinDeviceStats.AllDevices;
            if (all == null) return;

            for (int i = 0; i < all.Count; i++)
            {
                TwinAnchor a = all[i];
                if (a == null) continue;
                if (!TwinDeviceStats.IsActiveDevice(a)) continue;

                switch (node.Level)
                {
                    case 0:     // 楼栋 → 全部
                        break;

                    case 1:     // 楼层 → 同层
                        {
                            TwinRoomInfo info = TwinSiteLayout.Find(a.Zone);
                            int floor = info != null ? info.Floor : 0;
                            if (floor != node.Floor) continue;
                            break;
                        }

                    case 2:     // 房间 → 同 Zone
                        if (!string.Equals(a.Zone, node.Zone, System.StringComparison.Ordinal)) continue;
                        break;

                    default:
                        continue;
                }

                into.Add(TwinCode.Full(a.TwinId));
            }
        }

        /// <summary>
        /// 回到初始态。**不要随便调** —— 只在"关闭抽屉 / 退出管理系统"时用，
        /// 切视图（窄态 ↔ 全屏）时绝对不能调，那正是本类存在的意义。
        /// </summary>
        public static void Reset()
        {
            FloorFilter = 0;
            DeviceCategoryFilter = "";
            DeviceSearchQuery = "";
            AlarmHistory = false;
        }
    }
}

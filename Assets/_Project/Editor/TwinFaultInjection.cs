using System.Text;
using UnityEditor;
using UnityEngine;
using SchoolTwin.Command;
using SchoolTwin.Data;
using SchoolTwin.Twin;

namespace SchoolTwin.EditorTools
{
    /// <summary>
    /// P16 一键故障注入：把「演示时要手动点七八下才能造出来的场景」变成一次菜单点击。
    ///
    /// ── 为什么需要它 ──────────────────────────────────────────────
    /// P12~P15 的每个功能都要靠"制造一个特定条件"才看得见：
    ///   · 要看四态？得让设备只回执不执行；
    ///   · 要看告警抑制？得先断电；
    ///   · 要看断线重连？得让数据源静默 8 秒。
    /// 手动做这些要么要改 Inspector（容易忘记改回来），要么要等 45 秒的异常周期。
    /// 演示时最怕的就是"上一个场景没复位干净，下一个场景效果不对" —— 所以这里每一项都配了复位。
    ///
    /// ── 为什么放在 Editor 而不是做成游戏里的面板 ────────────────────
    /// 这些是**调试入口**，不是产品功能。放进游戏 UI 会让"总控面板"多出一排
    /// 观众不该看见的按钮（P14 定的「不新增第四个面板」）。放菜单里，
    /// 演示时按一下就行，也不占画面。
    ///
    /// ⚠️ 所有项都需要 **Play 模式**。编辑模式下点会给出明确提示，不会静默无效。
    /// </summary>
    public static class TwinFaultInjection
    {
        private const string Root = "P16 演示/";
        private const string RootFault = Root + "① 故障注入/";
        private const string RootReset = Root + "② 复位/";

        // ================================================================ 故障注入

        [MenuItem(RootFault + "断 1F 电", false, 1)]
        private static void CutFloor1() { CutFloor(1); }

        [MenuItem(RootFault + "断 2F 电", false, 2)]
        private static void CutFloor2() { CutFloor(2); }

        [MenuItem(RootFault + "断整栋电", false, 3)]
        private static void CutBuilding()
        {
            if (!RequirePlay()) return;
            int n = TwinCircuit.SetSwitch(TwinCircuit.BuildingId, false);
            Report("断整栋电", n);
        }

        [MenuItem(RootFault + "让设备只回执不执行（命令会停在确认中）", false, 20)]
        private static void ToggleApplyState()
        {
            if (!RequirePlay()) return;

            MockDeviceAgent agent = MockDeviceAgent.Instance;
            if (agent == null)
            {
                Debug.LogWarning("[P16] MockDeviceAgent 还没创建 —— 先打开一次总控面板或点一次控制按钮，" +
                                 "命令中心起来时才会把它挂上。");
                return;
            }

            agent.ApplyStateChanges = !agent.ApplyStateChanges;
            Debug.Log("[P16] 设备执行状态：" + (agent.ApplyStateChanges ? "正常" : "只回执不执行") +
                      (agent.ApplyStateChanges
                          ? ""
                          : " —— 现在点任意开关，命令应停在「已执行，等回读…」并在 5 秒后报「执行了但状态没变」"));
        }

        [MenuItem(RootFault + "数据源静默 8 秒（看离线）", false, 21)]
        private static void Silence()
        {
            if (!RequirePlay()) return;

            MockDataSource src = Object.FindObjectOfType<MockDataSource>();
            if (src == null)
            {
                Debug.LogWarning("[P16] 场景里没有正在运行的 MockDataSource（当前可能是 MQTT / 回放模式）。");
                return;
            }

            src.SimulateSilence(8f);
            Debug.Log("[P16] 数据源静默 8 秒 —— 3 秒后应全部变「数据陈旧」，8 秒后变「离线」。" +
                      "注意：这**不是**断电，告警应该正常响（和 P15 的断电抑制对比着看）");
        }

        [MenuItem(RootFault + "把一个点位推到告警", false, 22)]
        private static void PushAlarm()
        {
            if (!RequirePlay()) return;

            TwinRuntime runtime = TwinRuntime.Instance;
            TwinCatalog catalog = runtime != null ? runtime.Catalog : null;
            if (catalog == null || catalog.Points == null || catalog.Points.Count == 0)
            {
                Debug.LogWarning("[P16] 点位目录没挂上，无法注入越界值。");
                return;
            }

            // 找一个「有阈值、能真的越界」的点位。挑第一个会让告警可能落在
            // 一个已经断开的点位上（P15 之后断电点位是抑制状态，注入了也看不见）。
            for (int i = 0; i < catalog.Points.Count; i++)
            {
                TwinPointDefinition p = catalog.Points[i];
                if (p == null || p.Metrics == null || p.Metrics.Count == 0) continue;
                if (!TwinCircuit.IsPowered(p.TwinId)) continue;

                TwinMetricDefinition m = p.Metrics[0];
                if (m == null) continue;

                float value = m.AlarmHigh + Mathf.Max(0.1f, (m.Max - m.Min) * 0.1f);
                MockDataSource.SetOverride(p.TwinId, m.Metric, value);
                Debug.Log("[P16] 已把 " + p.TwinId + "." + m.Metric + " 推到 " + value.ToString("0.##") +
                          "（告警线 " + m.AlarmHigh.ToString("0.##") + "）—— 3 秒内应出现告警音 + 闪烁 + 告警历史一条");
                return;
            }

            Debug.LogWarning("[P16] 没找到可注入的点位（全部断电？先复位）。");
        }

        private static void CutFloor(int floor)
        {
            if (!RequirePlay()) return;
            int n = TwinCircuit.SetSwitch(TwinCircuit.FloorId(floor), false);
            Report("断 " + floor + "F 电", n);
        }

        // ================================================================ 复位

        [MenuItem(RootReset + "恢复全部供电", false, 1)]
        private static void RestoreAll()
        {
            if (!RequirePlay()) return;

            int fixedCount = 0;
            System.Collections.Generic.IReadOnlyList<TwinCircuitNode> all = TwinCircuit.All;
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i] != null && !all[i].On) { TwinCircuit.SetSwitch(all[i].Id, true); fixedCount++; }
            }
            Debug.Log("[P16] 已合闸 " + fixedCount + " 条回路。设备应在 10 秒内重新上报，" +
                      "状态回到正常（灯会回到断电前的开关状态）。");
        }

        [MenuItem(RootReset + "清空注入值 / 停发标记", false, 2)]
        private static void ClearInjections()
        {
            MockDataSource.ClearAllOverrides();
            MockDataSource.ClearAllMuted();
            Debug.Log("[P16] 已清空手动注入值与停发标记（注意：回路开关不会因此合闸，那是上面那一项）。");
        }

        [MenuItem(RootReset + "一键全部复位（推荐演示前跑）", false, 20)]
        private static void ResetAll()
        {
            if (!RequirePlay()) return;

            RestoreAllSilent();
            MockDataSource.ClearAllOverrides();
            MockDataSource.ClearAllMuted();

            MockDeviceAgent agent = MockDeviceAgent.Instance;
            if (agent != null) agent.ApplyStateChanges = true;

            TwinAlarmHistory history = TwinAlarmHistory.Instance;
            if (history == null) history = Object.FindObjectOfType<TwinAlarmHistory>();
            if (history != null) history.Clear();

            Debug.Log("[P16] ===== 全部复位 =====\n" +
                      "  回路：全部合闸\n" +
                      "  注入值 / 停发标记：已清空\n" +
                      "  设备执行：正常\n" +
                      "  告警历史：已清空\n" +
                      "现在可以从头演示了。");
        }

        private static void RestoreAllSilent()
        {
            System.Collections.Generic.IReadOnlyList<TwinCircuitNode> all = TwinCircuit.All;
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i] != null && !all[i].On) TwinCircuit.SetSwitch(all[i].Id, true);
            }
        }

        // ================================================================ 状态打印

        [MenuItem(Root + "打印当前状态（回路 / 心跳 / 抑制）", false, 100)]
        private static void PrintState()
        {
            if (!RequirePlay()) return;

            StringBuilder sb = new StringBuilder(512);
            sb.Append("[P16] ===== 当前状态 =====\n");

            System.Collections.Generic.IReadOnlyList<TwinCircuitNode> all = TwinCircuit.All;
            int off = 0;
            for (int i = 0; i < all.Count; i++)
            {
                TwinCircuitNode n = all[i];
                if (n == null) continue;
                if (!n.On) off++;
                if (n.Level == 0 || !n.On) sb.Append("  ").Append(n.On ? "[合] " : "[断] ").Append(n.Title).Append('\n');
            }

            TwinAlarmCenter center = TwinAlarmCenter.Instance;
            TwinRuntime runtime = TwinRuntime.Instance;
            int total = runtime != null && runtime.Registry != null ? runtime.Registry.Count : 0;

            sb.Append("  回路：").Append(all.Count).Append(" 条，其中断开 ").Append(off).Append(" 条\n");
            sb.Append("  点位：").Append(total).Append(" 个\n");
            sb.Append("  断电抑制：").Append(center != null ? center.SuppressedCount : -1).Append(" 个\n");
            sb.Append("  停发样本：").Append(MockDataSource.MutedCount).Append(" 个\n");

            MockDeviceAgent agent = MockDeviceAgent.Instance;
            sb.Append("  设备执行：").Append(agent == null ? "未创建" : (agent.ApplyStateChanges ? "正常" : "只回执不执行")).Append('\n');

            Debug.Log(sb.ToString());
        }

        // ================================================================ 工具

        /// <summary>
        /// 这些入口都依赖运行时的静态状态，编辑模式下点只会让人以为"功能没做"。
        /// 所以统一拦一句，把原因说清楚 —— 静默无效是最糟的反馈。
        /// </summary>
        private static bool RequirePlay()
        {
            if (Application.isPlaying) return true;
            Debug.LogWarning("[P16] 请先按 Play。故障注入改的是运行时状态（回路、停发标记、设备执行开关），" +
                             "编辑模式下改了也不会保留到 Play，等于什么都没做。");
            return false;
        }

        private static void Report(string action, int devices)
        {
            if (devices < 0)
            {
                Debug.LogWarning("[P16] " + action + " 失败：找不到对应的回路节点。");
                return;
            }
            Debug.Log("[P16] " + action + " —— 影响 " + devices + " 台设备。\n" +
                      "  应看到：这些设备立刻灰显、状态写「断电」、按钮禁用、三维灯灭；\n" +
                      "  不应看到：任何新告警（断电期间告警被抑制，这是 P15 的验收项）。");
        }
    }
}

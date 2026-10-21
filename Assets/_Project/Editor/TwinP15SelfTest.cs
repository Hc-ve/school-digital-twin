using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using SchoolTwin.Twin;
using SchoolTwin.UI;

namespace SchoolTwin.EditorTools
{
    /// <summary>
    /// P15 自检：**不需要 Play，也不需要场景**，批处理跑一遍就把回路与 SBO 的逻辑验完。
    ///
    /// ── 为什么值得写这个文件 ────────────────────────────────────────
    /// 回路和 SBO 的问题都是"不报错的错"：
    ///   · 断一层电，另一层也黑了 —— 界面照样正常，只是行为不对；
    ///   · 10 秒倒计时没走完就执行了 —— 看起来"能断电"，SBO 其实没生效。
    /// 这两类问题靠"点一遍看看"很容易漏（尤其第二类，人眼看不出来它本来该作废）。
    /// 而它们**全是纯逻辑**，不需要渲染、不需要数据源，所以可以在批处理里精确断言。
    ///
    /// ── 用法（Unity 关闭时）──────────────────────────────────────
    ///   Unity.exe -batchmode -quit -nographics -projectPath "&lt;工程&gt;" ^
    ///     -executeMethod SchoolTwin.EditorTools.TwinP15SelfTest.Run -logFile "&lt;日志&gt;"
    /// 通过数 / 失败数会打在日志里，失败时退出码为 1。
    /// </summary>
    public static class TwinP15SelfTest
    {
        private static int _pass;
        private static readonly List<string> _fails = new List<string>(8);

        public static void Run()
        {
            _pass = 0;
            _fails.Clear();

            try
            {
                TwinCircuit.ResetAll();
                TestTopology();
                TestFloorCut();
                TestBuildingCut();
                TestRoomCut();
                TestUnknownZone();
                TestSelectionMapping();
                TestUnreachable();
                TestSboHappyPath();
                TestSboExpiry();
                TestSboCancel();
                TestSboEmpty();
                TestControlScopeOnKnownFloor();
            }
            catch (Exception e)
            {
                _fails.Add("自检抛异常：" + e);
            }

            Debug.Log("[P15Test] ===== 结果：" + _pass + " 通过 / " + _fails.Count + " 失败 =====");
            for (int i = 0; i < _fails.Count; i++) Debug.LogError("[P15Test] 失败：" + _fails[i]);

            if (Application.isBatchMode) EditorApplication.Exit(_fails.Count > 0 ? 1 : 0);
        }

        // ---------------------------------------------------------------- 断言

        private static void Check(bool ok, string what)
        {
            if (ok) { _pass++; return; }
            _fails.Add(what);
        }

        private static void CheckEq(string expected, string actual, string what)
        {
            Check(string.Equals(expected, actual, StringComparison.Ordinal),
                what + "（期望 " + expected + "，实际 " + actual + "）");
        }

        // ---------------------------------------------------------------- 用例

        private static void TestTopology()
        {
            IReadOnlyList<TwinCircuitNode> all = TwinCircuit.All;
            int[] floors = TwinSiteLayout.Floors();

            // 1 栋 + N 层 + 10 个房间
            Check(all.Count == 1 + floors.Length + TwinSiteLayout.Rooms.Count,
                "回路节点数应为 1+" + floors.Length + "+" + TwinSiteLayout.Rooms.Count + "，实际 " + all.Count);

            for (int i = 0; i < TwinSiteLayout.Rooms.Count; i++)
            {
                TwinRoomInfo info = TwinSiteLayout.Rooms[i];
                TwinCircuitNode room = TwinCircuit.Find(TwinCircuit.RoomId(info.Zone));
                if (room == null) { _fails.Add("房间回路缺失：" + info.Zone); continue; }
                CheckEq(TwinCircuit.FloorId(info.Floor), room.ParentId, "房间 " + info.Zone + " 的上级应是它所在楼层");
            }

            Debug.Log("[P15Test] 回路拓扑：" + all.Count + " 个节点，楼层 " + floors.Length + " 个");
        }

        private static void TestFloorCut()
        {
            TwinCircuit.ResetAll();

            const string f1Light = "Class1-2/Light/Main_01";
            const string f2Light = "Class2-1/Light/Main_01";

            Check(TwinCircuit.IsPowered(f1Light), "初始应全部通电（1F）");
            Check(TwinCircuit.IsPowered(f2Light), "初始应全部通电（2F）");

            TwinCircuit.SetSwitch(TwinCircuit.FloorId(1), false);

            Check(!TwinCircuit.IsPowered(f1Light), "断 1F 后 1F 设备应断电");
            Check(TwinCircuit.IsPowered(f2Light), "断 1F 后 2F 设备必须不受影响");
            Check(TwinCircuit.AvailabilityOf(f1Light) == DeviceAvailability.PowerOff,
                "1F 设备可用性应为 PowerOff");

            TwinCircuit.SetSwitch(TwinCircuit.FloorId(1), true);
            Check(TwinCircuit.IsPowered(f1Light), "恢复 1F 后应重新通电");

            Debug.Log("[P15Test] 楼层断电：隔离性通过");
        }

        private static void TestBuildingCut()
        {
            TwinCircuit.ResetAll();

            TwinCircuit.SetSwitch(TwinCircuit.BuildingId, false);
            Check(!TwinCircuit.IsPowered("Class1-2/Light/Main_01"), "断总闸后 1F 应断电");
            Check(!TwinCircuit.IsPowered("Class2-1/Light/Main_01"), "断总闸后 2F 应断电");
            Check(!TwinCircuit.IsPoweredZone("ClassComputer"), "断总闸后计算机教室应断电");

            // 关键：房间级的开关**没有被改动**，但通电状态必须是断的。
            // 这正是「只看自己那一个开关」会漏掉的情况。
            TwinCircuitNode room = TwinCircuit.Find(TwinCircuit.RoomId("Class2-1"));
            Check(room != null && room.On, "断总闸不应改动房间级开关（层级要分开看）");

            // 上级断电时，下级按钮必须知道"点了也没用"
            Check(!TwinCircuit.ParentPowered(TwinCircuit.FloorId(1)), "断总闸后 1F 应知道上级已断电");
            Check(!TwinCircuit.ParentPowered(TwinCircuit.RoomId("Class2-1")), "断总闸后房间应知道上级已断电");
            Check(TwinCircuit.ParentPowered(TwinCircuit.BuildingId), "总闸没有上级，应视为通电");

            TwinCircuit.SetSwitch(TwinCircuit.BuildingId, true);
            Check(TwinCircuit.IsPowered("Class2-1/Light/Main_01"), "合总闸后应恢复");
            Check(TwinCircuit.ParentPowered(TwinCircuit.FloorId(1)), "合总闸后上级应恢复");

            Debug.Log("[P15Test] 总闸断电：层级继承通过");
        }

        private static void TestRoomCut()
        {
            TwinCircuit.ResetAll();

            TwinCircuit.SetSwitchByZone("Class2-1", false);
            Check(!TwinCircuit.IsPowered("Class2-1/Light/Main_01"), "断房间后本房间应断电");
            Check(TwinCircuit.IsPowered("Class2-2/Light/Main_01"), "断房间后同层其它房间不受影响");

            TwinCircuit.SetSwitchByZone("Class2-1", true);
            Debug.Log("[P15Test] 房间断电：通过");
        }

        private static void TestUnknownZone()
        {
            TwinCircuit.ResetAll();

            // 表外分区：不能因为"没登记楼层"就没有回路
            TwinCircuitNode n = TwinCircuit.RoomOf("SomeNewRoom");
            Check(n != null, "表外分区应能拿到回路（不能返回 null）");
            Check(TwinCircuit.IsPoweredZone("SomeNewRoom"), "表外分区默认通电");

            TwinCircuit.SetSwitch(TwinCircuit.RoomId("SomeNewRoom"), false);
            Check(!TwinCircuit.IsPoweredZone("SomeNewRoom"), "表外分区也应能断电");

            Debug.Log("[P15Test] 表外分区兜底：通过");
        }

        private static void TestSelectionMapping()
        {
            CheckEq(TwinCircuit.BuildingId, TwinCircuit.CircuitOfSelection(null, 0), "选「全部」应对应总闸");
            CheckEq(TwinCircuit.FloorId(2), TwinCircuit.CircuitOfSelection(null, 2), "选「2F」应对应楼层回路");
            CheckEq(TwinCircuit.RoomId("Class2-1"), TwinCircuit.CircuitOfSelection("Class2-1", 2),
                "选房间时应以房间为准（即使楼层筛选还开着）");
        }

        private static void TestUnreachable()
        {
            TwinCircuit.ResetAll();

            const string id = "Class2-1/AC/Unit_01";
            Check(!TwinCircuit.IsUnreachable(id), "初始不应是无响应");
            TwinCircuit.MarkUnreachable(id, 20f);
            Check(TwinCircuit.IsUnreachable(id), "标记后应是无响应");
            Check(TwinCircuit.IsPowered(id), "无响应不等于断电（两条轴必须分开）");
        }

        private static void TestSboHappyPath()
        {
            TwinSboGate gate = new TwinSboGate();
            int called = 0;

            TwinSboRequest req = MakeRequest(delegate { called++; });
            gate.Begin(req, 0f);
            Check(gate.Phase == SboPhase.AwaitSelect, "开始后应等待选择");

            Check(!gate.Operate(0.1f), "没选择就执行必须被拒绝（SBO 的核心）");
            Check(called == 0, "没选择就执行，回调不许被调用");

            Check(gate.Select(0.2f), "选择应被接受");
            Check(gate.Phase == SboPhase.Selecting, "选择后应处于 Selecting");

            gate.Tick(0.2f + TwinSboGate.SelectRoundTripSeconds + 0.01f);
            Check(gate.Phase == SboPhase.Armed, "收到选择确认后应进入 Armed");

            float armedAt = 0.2f + TwinSboGate.SelectRoundTripSeconds + 0.01f;
            Check(gate.Remaining(armedAt) > 9f, "就绪窗口应接近 10 秒，实际 " + gate.Remaining(armedAt));

            Check(gate.Operate(armedAt + 1f), "就绪期内执行应成功");
            Check(called == 1, "执行回调应被调用一次，实际 " + called);
            Check(gate.Phase == SboPhase.Done, "执行后应为 Done");

            Check(!gate.Operate(armedAt + 2f), "同一次 SBO 不许执行两次");
            Check(called == 1, "重复执行不许再次调用回调");

            Debug.Log("[P15Test] SBO 正常路径：通过");
        }

        private static void TestSboExpiry()
        {
            TwinSboGate gate = new TwinSboGate();
            int called = 0;

            gate.Begin(MakeRequest(delegate { called++; }), 0f);
            gate.Select(0.1f);
            gate.Tick(0.1f + TwinSboGate.SelectRoundTripSeconds + 0.01f);
            Check(gate.Phase == SboPhase.Armed, "应先进入 Armed");

            // 什么都不点，等 11 秒
            gate.Tick(12f);
            Check(gate.Phase == SboPhase.Expired, "10 秒内没确认执行，选择必须作废");
            Check(!gate.Operate(12.1f), "作废后执行必须失败");
            Check(called == 0, "作废后设备一台都不许动（这是 SBO 存在的理由）");

            Debug.Log("[P15Test] SBO 超时作废：通过（设备未动作）");
        }

        private static void TestSboCancel()
        {
            TwinSboGate gate = new TwinSboGate();
            int called = 0;

            gate.Begin(MakeRequest(delegate { called++; }), 0f);
            gate.Select(0.1f);
            gate.Cancel();
            Check(gate.Phase == SboPhase.Cancelled, "取消后应为 Cancelled");
            Check(!gate.Operate(0.2f), "取消后执行必须失败");
            Check(called == 0, "取消后不许有动作");
        }

        private static void TestSboEmpty()
        {
            TwinSboGate gate = new TwinSboGate();
            TwinSboRequest req = MakeRequest(null);
            req.Targets.Clear();

            gate.Begin(req, 0f);
            Check(!gate.Select(0.1f), "影响 0 台设备时选择应被拒绝");
            Check(gate.Phase == SboPhase.Expired, "应直接作废而不是卡在 Selecting");
        }

        /// <summary>
        /// 受控白名单里的每个点位，其所在分区都必须登记了楼层。
        ///
        /// 【为什么这条要断言】新增一个受控点位、却忘了在 TwinSiteLayout 里登记它的房间，
        /// 结果是这个点位挂在楼栋回路下 —— 断楼层电时它不会跟着断。
        /// 现象是"断完 1F 电，这盏灯还亮着"，而没有任何报错。
        /// </summary>
        private static void TestControlScopeOnKnownFloor()
        {
            IReadOnlyList<string> ids = TwinControlScope.All;
            for (int i = 0; i < ids.Count; i++)
            {
                int slash = ids[i].IndexOf('/');
                string zone = slash < 0 ? ids[i] : ids[i].Substring(0, slash);
                int floor = TwinSiteLayout.FloorOf(zone);
                Check(floor != TwinSiteLayout.UnknownFloor,
                    "受控点位 " + ids[i] + " 的分区 " + zone + " 没在 TwinSiteLayout 里登记楼层");
            }
            Debug.Log("[P15Test] 受控点位楼层归属：" + ids.Count + " 个");
        }

        private static TwinSboRequest MakeRequest(Action operate)
        {
            TwinSboRequest req = new TwinSboRequest();
            req.CircuitId = TwinCircuit.FloorId(1);
            req.PowerOn = false;
            req.Title = "断开 1F 的电源";
            req.Subject = "1F";
            req.Targets = new List<string> { "1-2班 · 主照明", "1-3班 · 主照明" };
            req.Operate = operate;
            return req;
        }

        // ---------------------------------------------------------------- 打印

        [MenuItem("P15 电力回路 / 跑一遍自检（编辑器内）")]
        private static void RunFromMenu()
        {
            Run();
            StringBuilder sb = new StringBuilder(256);
            sb.Append("[P15Test] ").Append(_pass).Append(" 通过 / ").Append(_fails.Count).Append(" 失败");
            Debug.Log(sb.ToString());
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using SchoolTwin.Core;
using SchoolTwin.Data;
using SchoolTwin.Twin;

namespace SchoolTwin.Command
{
    /// <summary>
    /// Mock 设备代理：协议另一端的「那台设备」。
    ///
    /// 它做的事和真实网关完全一样：
    ///   订阅 twin/cmd/** → 按 id 去重 → 校验参数 → **改自己的状态** → 回 ack（accepted / actuated）
    /// 区别只有一处：它改的是 MockDataSource 的注入值（下一拍数据就会带着新值上来），
    /// 而不是真的继电器。
    ///
    /// 【为什么它必须存在】没有它，「闭环」就只能靠命令中心自己骗自己 ——
    /// 命令中心说成功了、界面就绿了，没有任何一方在真的改变状态。
    /// 有了它，「面板 → 命令 → 设备 → 回执 → 状态 → 面板」才是闭合的。
    ///
    /// 【为什么改的是 MockDataSource 的注入值】Mock 数据源每秒生成 10 次正弦波，
    /// 设备要「保持」自己的状态，唯一的办法是在数据出口上压住它。
    /// 这也是为什么 `SetOverride` 的优先级高于波形 —— 它是设备的意志。
    ///
    /// ⚠️ 同一时刻只能有一个 MockDeviceAgent。两个都在听 twin/cmd/** 就会各回一条 ack，
    /// 表现为「一条命令两个回执」，极难查（Spec §9.4）。
    /// </summary>
    [AddComponentMenu("SchoolTwin/Mock 设备代理 (MockDeviceAgent)")]
    public sealed class MockDeviceAgent : MonoBehaviour
    {
        public static MockDeviceAgent Instance { get; private set; }

        [Tooltip("关掉它 = 设备只回执不执行（P12 验收项：命令必须停在「确认中」并在 5 秒后报「执行了但状态没变」）")]
        [SerializeField] private bool _applyStateChanges = true;

        [Tooltip("收到命令到回「已接受」的模拟耗时（秒）")]
        [SerializeField] private float _ackDelaySeconds = 0.12f;

        [Tooltip("回「已接受」到真正改完状态的模拟耗时（秒）。空调压缩机、灯管启动都不是瞬间的")]
        [SerializeField] private float _settleDelaySeconds = 0.65f;

        [Tooltip("是否把每条命令打到 Console（排查用）")]
        [SerializeField] private bool _logEveryCommand = true;

        [Tooltip("打铃持续秒数。这是**设备侧**行为：响完自己停，不需要上位机再发一次「停」")]
        [SerializeField] private float _bellRingSeconds = 3f;

        /// <summary>
        /// 「到点自己恢复」的指标。
        ///
        /// 【为什么需要这张表】P19 之前所有设备都只有**稳态**：开就是开、关就是关，
        /// 状态由命令驱动、一直保持。上课铃是第一个「脉冲型」设备 ——
        /// 它响 3 秒然后自己停。如果不在设备侧做这个复位，界面上的铃会一直显示「正在响」，
        /// 而现实里铃早停了 —— 那就是孪生体和实物不一致，比不做更糟。
        ///
        /// 复位也走 `SetState`（即 `MockDataSource` 注入值），所以回读链路看到的是
        /// 「设备自己把状态改回去了」，而不是谁偷偷改的界面。
        /// </summary>
        private struct AutoOff
        {
            public string TwinId;
            public string Metric;
            public float DueTime;
        }

        private readonly List<AutoOff> _autoOff = new List<AutoOff>(4);

        private struct Job
        {
            public string Id;
            public string TwinId;
            public CommandAction Action;
            public float Value;
            public int Stage;          // 0 = 等回 accepted，1 = 等改完状态回 actuated
            public float DueTime;
        }

        /// <summary>已执行过的命令 id（环形 64）。QoS 1 会重复投递，重复执行会让「开灯」变成两次翻转。</summary>
        private const int DedupeCapacity = 64;

        private readonly List<Job> _jobs = new List<Job>(8);
        private readonly HashSet<string> _seenIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly Queue<string> _idOrder = new Queue<string>(DedupeCapacity + 8);
        private readonly Dictionary<string, float> _owned = new Dictionary<string, float>(StringComparer.Ordinal);

        private Action<string, string> _handler;

        public bool ApplyStateChanges
        {
            get { return _applyStateChanges; }
            set { _applyStateChanges = value; }
        }

        // ---------------------------------------------------------------- 生命周期

        public static MockDeviceAgent Ensure()
        {
            if (Instance != null) return Instance;

            TwinRuntime runtime = TwinRuntime.Ensure();
            MockDeviceAgent found = runtime.GetComponent<MockDeviceAgent>();
            if (found == null) found = runtime.gameObject.AddComponent<MockDeviceAgent>();
            return found;
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogError("[TwinCmd] 场景中存在多个 MockDeviceAgent —— 会各自回一条 ack，" +
                               "表现为「一条命令两个回执」。已禁用多余的：" + name);
                enabled = false;
                return;
            }

            Instance = this;
            _handler = OnCommandMessage;
            LocalCommandTransport.Instance.Subscribe("twin/cmd/", _handler);
        }

        private void Start()
        {
            SeedInitialState();
        }

        private void OnDestroy()
        {
            if (_handler != null) LocalCommandTransport.Instance.Unsubscribe(_handler);

            // 把本代理压住的值放开，让波形恢复 —— 否则退出 Play 之前那些指标会一直钉在固定值
            foreach (KeyValuePair<string, float> kv in _owned)
            {
                int bar = kv.Key.IndexOf('|');
                if (bar <= 0) continue;
                MockDataSource.ClearOverride(kv.Key.Substring(0, bar), kv.Key.Substring(bar + 1));
            }
            _owned.Clear();

            if (Instance == this) Instance = null;
        }

        /// <summary>
        /// 开机就把受控设备的初始状态定下来。
        ///
        /// 【为什么必须做】不压住的话，这些「开关量」仍然走正弦波 ——
        /// 面板上写着「已开」，功率却在 0~110W 之间来回摆，自相矛盾。
        /// 受控设备的状态从此由设备代理负责，这才是「设备」该有的语义。
        ///
        /// 【2026-10-22 为什么要改成遍历白名单】原来只写死 Class2-1 的灯和空调，
        /// 另外 9 盏灯、4 台空调、还有那扇门从来没被压住过。门的后果最明显：
        /// `Open` 的量程是 0~1，正弦波把它压成 0.12~0.88 来回摆，
        /// 界面按「≥0.5 算开」判断 —— 于是**门明明是开着的（场景里的模型就是开的），
        /// 卡片上却一会儿「已开」一会儿「已关」**。用户看到的就是"状态写错了"。
        ///
        /// 现在的规矩：**白名单里的每一台设备都必须有一个明确的初始状态**，
        /// 不再挑着写。新增受控设备时忘了给初值，会在这里体现为"状态在乱跳"。
        /// </summary>
        private void SeedInitialState()
        {
            IReadOnlyList<string> ids = TwinControlScope.All;
            int seeded = 0, unresolved = 0;

            for (int i = 0; i < ids.Count; i++)
                SeedOne(ids[i], ref seeded, ref unresolved);

            // 白名单之外、但**同样有明确静止状态**的点位也要压住。
            // 见 SeedOnlyIds 的说明：不压住的话它们会走正弦波，产生假告警。
            for (int i = 0; i < SeedOnlyIds.Length; i++)
                SeedOne(SeedOnlyIds[i], ref seeded, ref unresolved);

            // 【P3】机房座位（Seat_XX）：**不在硬编码白名单里，必须单独补一轮**。
            //
            // 不补的后果（2026-11-01 实测）：Seat 没有初始状态 → 走正弦波 →
            // Power 在 0~300W 之间自由摆动 → 面板按"功率 > 0.5"判断开关，
            // 于是**明明是关着的机器，卡片上写「已开」**，而且数值每帧都在跳。
            // 点开机后命令中心回读对不上，判 Failed（设备状态与期望不一致）。
            SeedSeatAnchors(ref seeded, ref unresolved);

            Debug.Log("[TwinCmd] 已给 " + seeded + " 台受控设备定下初始状态（白名单共 " +
                      ids.Count + " 个点位，另有 " + SeedOnlyIds.Length + " 个只定状态不开放控制）。" +
                      "状态由设备代理负责，模拟波形不再改它。");

            if (unresolved > 0)
                Debug.LogWarning("[TwinCmd] 有 " + unresolved + " 个点位解析不出设备模型，初始状态没定 —— " +
                                 "它们的状态会跟着模拟波形乱跳。多半是点位目录里还没有这一条。");
        }

        /// <summary>
        /// 暂不开放控制、但**有明确静止状态**的点位。
        ///
        /// 【为什么不能只靠白名单】
        /// 白名单的语义是「界面上给不给按钮」，不是「这个设备有没有确定状态」。
        /// 电脑暂时不给按钮（三维里没有可视化部件，见 TwinControlScope 的注释），
        /// 但它的功率**必须**有个确定值 —— 否则 `MockSignalGenerator` 会把它当连续量，
        /// 在 [WarnLow, WarnHigh] 里做正弦，再按周期往量程边界冲。
        ///
        /// 2026-10-22 用户看到的那条「教师办公室 机房电脑 功率 292.66W」就是假的：
        /// 那时候 PC 既没在 SeedInitialState 里、量程又是单台的 0~300W，
        /// 波形自然就在 0~300 之间来回冲线。**没有设备负责的状态，一律是假的。**
        /// </summary>
        private static readonly string[] SeedOnlyIds =
        {
            // 【P19 变更】`ClassComputer/PC/Room_01` 已进白名单（机房显示器能亮了），
            // 留在这里会被 Seed 两次 —— 值一样所以无害，但日志里的数字会骗人。
            // 只留教师办公室那台：它是 `Computer03`，模型没有屏幕，永远不给按钮。
            "ClassTeachers/PC/Room_01",
        };

        /// <summary>
        /// 场景里模型本来就是**关着**的门。
        ///
        /// 12 扇 `Door07a` 都是半开姿态，所以默认播种「开」，礼堂 `DoorDouble02` 也一样 ——
        /// 它的模型是"一片关、一片半开"，播「开」时画面和卡片一致。
        ///
        /// ⚠️ 曾经把它放进这张表（按「关」播种），理由是 `TwinP17Diag` 打出「关门角 = 0°」。
        /// 那个数字只看**根节点**，而 `DoorDouble02` 的根只是个容器，真正摆姿态的是下面两片门叶。
        /// 按「关」播种的表现是：卡片写关闭、画面里门开着（2026-10-22 用户实测）。
        /// </summary>
        private static readonly string[] DoorsClosedByDefault = new string[0];

        private static bool DoorStartsClosed(string twinId)
        {
            for (int i = 0; i < DoorsClosedByDefault.Length; i++)
            {
                if (string.Equals(DoorsClosedByDefault[i], twinId, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>
        /// 给场景里所有机房座位（`ClassComputer/PC/Seat_XX`）压初始状态。
        ///
        /// 这些点位是批量生成的，不在 `TwinControlScope.Ids` 那张硬编码表里，
        /// 所以 `SeedInitialState` 的主循环遍历不到它们。
        /// 不压的话它们会走正弦波 —— 关机状态功率也在 0~300W 之间摆，
        /// 面板判断"开关"就永远不可信，点开机的回读判据也会失败。
        /// </summary>
        /// <summary>机房整间聚合点位（18 个座位的"总闸"）。</summary>
        private const string RoomPcTwinId = "ClassComputer/PC/Room_01";

        /// <summary>座位 TwinId 前缀。</summary>
        private const string SeatPcPrefix = "ClassComputer/PC/Seat_";

        /// <summary>缓存座位 TwinId（首次扫描后固定，场景运行期不会增减座位）。</summary>
        private readonly List<string> _seatIds = new List<string>(32);
        private bool _seatIdsScanned;

        /// <summary>
        /// 整间开机 / 关机 → 逐个下发给 18 个座位点位。
        ///
        /// 用 `SetState` 而不是发命令：`SetState` 直接写 override，
        /// 和"用户亲手点这台"走的是同一条数据出口，表现层不需要区分来源。
        /// 走 `TwinCommandCenter` 反而会多出 18 条命令日志、18 次回读判定，
        /// 而且联动本来就是设备侧的行为（真实机房里是配电箱合闸），
        /// 不该表现成"上位机逐台点名"。
        /// </summary>
        private void ApplyRoomToSeats(bool on)
        {
            if (!_seatIdsScanned)
            {
                _seatIdsScanned = true;

                TwinAnchor[] anchors = FindObjectsOfType<TwinAnchor>(true);
                for (int i = 0; i < anchors.Length; i++)
                {
                    TwinAnchor a = anchors[i];
                    if (a == null || string.IsNullOrEmpty(a.TwinId)) continue;
                    if (!a.TwinId.StartsWith(SeatPcPrefix, StringComparison.Ordinal)) continue;
                    _seatIds.Add(a.TwinId);
                }
            }

            for (int i = 0; i < _seatIds.Count; i++)
            {
                SetState(_seatIds[i], "Power", on ? 180f : 0f);
                SetState(_seatIds[i], "OnCount", on ? 1f : 0f);
            }

            Debug.Log("[TwinCmd] 机房联动：" + (on ? "开机" : "关机") +
                      " → " + _seatIds.Count + " 个座位点位已同步" +
                      (on ? "（每台 180W）" : "（0W）"));
        }

        private void SeedSeatAnchors(ref int seeded, ref int unresolved)
        {
            TwinAnchor[] anchors = FindObjectsOfType<TwinAnchor>(true);
            for (int i = 0; i < anchors.Length; i++)
            {
                TwinAnchor a = anchors[i];
                if (a == null || string.IsNullOrEmpty(a.TwinId)) continue;
                if (!a.TwinId.StartsWith("ClassComputer/PC/Seat_", StringComparison.Ordinal)) continue;
                SeedOne(a.TwinId, ref seeded, ref unresolved);
            }
        }

        /// <summary>
        /// 灯具的播种功率（W）：按**房型**分档，不再一律 42f。（P5-12 / R7）
        ///
        /// ── 为什么是房型，不是随机数 ────────────────────────────────────
        /// Top5 五行全 42W 之所以"一眼假"，不是因为数值相同本身，
        /// 而是它**不可能是真的**：礼堂 12 盏吊灯和走廊灯同功率说不通。
        ///
        /// 用随机数也能让判据通过（五个不同值），但答辩被问
        /// 「为什么这盏是 68W、那盏是 24W」时，随机数答不上来 ——
        /// 而房型能答：「礼堂是 12 盏大功率吊灯，走廊是低位筒灯」。
        ///
        /// ── 数值取值依据 ────────────────────────────────────────────────
        /// 这个点位代表的是**整个房间的照明**（不是单盏灯），所以取的是
        /// 该房间照明总功率量级。全部落在量程 0~120 内、且低于警告线 110，
        /// 不会因为改播种值就自造出告警。
        ///
        ///   · 普通教室 / 计算机教室  36 / 42   6~9 盏 LED 面板灯
        ///   · 礼堂 / 体育馆          68 / 62   12 盏大功率吊灯（y≈7.7）
        ///   · 食堂                   55       大空间、亮灯时间长
        ///   · 体育室 / 器材室        38 / 32
        ///   · 走廊                   24       低位筒灯，数量多但单点代表一片
        ///   · 校长室 / 教师办公室    28       小房间
        ///
        /// 未知房间退回 40（与旧值 42 接近，看不出跳变）。
        /// </summary>
        private static float LightSeedWatt(string twinId)
        {
            string zone = ZoneOf(twinId);
            switch (zone)
            {
                case "ActZal": return 68f;
                case "SportHall": return 62f;
                case "Kitchen": return 55f;
                case "ClassComputer": return 42f;
                case "SportRoom2": return 38f;
                case "SportRoom3": return 36f;
                case "SportRoom1": return 32f;
                case "ClassDirector": return 28f;
                case "ClassTeachers": return 28f;
                case "Corridor1": return 24f;
                case "Corridor2": return 24f;
                default:
                    // Class1-2 / Class1-3 / Class1-4 / Class2-1 / Class2-2 等普通教室
                    return zone != null && zone.StartsWith("Class", StringComparison.Ordinal) ? 36f : 40f;
            }
        }

        /// <summary>取 TwinId 的第一段（分区名）。避免为一次播种引入字符串切分开销之外的依赖。</summary>
        private static string ZoneOf(string twinId)
        {
            if (string.IsNullOrEmpty(twinId)) return "";
            int slash = twinId.IndexOf('/');
            return slash < 0 ? twinId : twinId.Substring(0, slash);
        }

        private void SeedOne(string twinId, ref int seeded, ref int unresolved)
        {
            TwinDeviceModel model = ResolveModel(twinId);
            if (model == null)
            {
                Debug.LogWarning("[TwinCmd] 受控点位「" + twinId + "」解析不出设备模型，" +
                                 "初始状态没定 —— 它的状态会跟着模拟波形乱跳");
                unresolved++;
                return;
            }

            switch (model.Type)
            {
                case "Light":
                    // 【P5-12 / R7】灯具功率按**房间档位**播种，不再一律 42f。
                    //
                    // ── 为什么必须分开（主理人 2026-11-02 截图）───────────────
                    // 播种值全一样 → Top5 五行全是 `42 W · 2%`、条形等长。
                    // 条形长度按 `该设备 / Top1` 算，五个都等于 Top1，于是全满格。
                    // 评委一眼就能看出"这五条是复制粘贴的"。
                    //
                    // ── 为什么按房间分档，而不是释放 override 交给正弦波 ──────
                    // `P5-5` §5.3 曾建议"播种后释放 override，把 Power 交还正弦波"。
                    // **这条我没采用**，因为它会带进两个更严重的回归：
                    //   ① 灯具量程 0~120，正弦会扫过整个区间 —— 而表现层按
                    //      `Power > 0.5` 判开关，灯会周期性自闪；
                    //   ② 用户点"关灯"写进 override 的 0，会被下一拍正弦**覆盖回去**，
                    //      开关控制直接失效（P26 已经为"状态被波形改掉"踩过一次）。
                    // `P5-4` §6.3 给的方案才是安全的：**仍然钉住，只是钉不同的值**。
                    // 这样灯不会闪、开关仍然说了算，而 Top5 立刻出现 4 个以上不同值。
                    //
                    // ── 分档依据 ──────────────────────────────────────────────
                    // 用房型而不是"随机数"：答辩被问「为什么这盏 68W」时，
                    // 能直接答"礼堂 12 盏大灯，单盏功率高"。随机数答不上来。
                    // 数值都落在量程 0~120 内、且低于警告线 110，不会自造告警。
                    SetState(twinId, "Power", LightSeedWatt(twinId));
                    SetState(twinId, "Brightness", 80f);   // 警告带 20~100
                    seeded++;
                    break;

                case "AC":
                    SetState(twinId, "Power", 1.2f);       // 量程 0~3.5kW，警告线 3
                    SetState(twinId, "SetTemp", 26f);      // 警告带 18~26
                    seeded++;
                    break;

                case "Door":
                    // 场景里的门模型是**开着的**（Door07a 实例都摆成半开姿态），
                    // 初始状态就得是「开」，否则卡片和三维对不上。
                    //
                    // 【P19 例外】礼堂那扇双开门 `DoorDouble02` 的模型是**关着的**
                    // （实测关门角 = 0°，见 `TwinP17Diag` 的输出）。
                    // 如果也按 1 播种，按 Play 的一瞬间这扇门会自己往外转 90° ——
                    // 用户会以为"门自己开了"。初始状态必须等于场景里模型现在的姿态，
                    // 这一条比"所有门都一样"重要。
                    SetState(twinId, "Open", DoorStartsClosed(twinId) ? 0f : 1f);
                    seeded++;
                    break;

                case "PC":
                {
                    // 【P26】初始状态 = **关机**。
                    //
                    // 原来播的是"开机"（units × 180W），结果一进 Play 机房 19 台全亮着，
                    // 而交互设计的第一步就是「开机 / 联动开机 / 投影」三选一 ——
                    // 机器本来就开着，这三个按钮就全成了摆设。
                    //
                    // 真实场景也对：上课前机房是关的，学生/老师进来才开。
                    // 关机时 `OnCount` 也要一起归零，否则卡片会写「已关」旁边「开机 1 台」。
                    SetState(twinId, "Power", 0f);
                    SetState(twinId, "OnCount", 0f);
                    seeded++;
                    break;
                }

                case "Projector":
                    // 【P26】初始状态改成**关机**（P19 时是默认开着）。
                    //
                    // P19 的理由是"投影仪和灯一样，上课就开着"。但 P26 的交互设计里
                    // 「投影模式」是三种开机模式之一 —— 投影仪本来就开着的话，
                    // 这个按钮点了也看不出区别，和 PC 那三条一样形同虚设。
                    // 灯泡累计时长 `LampHours` 仍然保留（那是设备寿命，与开关无关）。
                    SetState(twinId, "Power", 0f);
                    SetState(twinId, "LampHours", 1240f);   // 灯泡已用 1240 小时（量程 0~10000）
                    seeded++;
                    break;

                case "Bell":
                    // 【P19】默认「不在响」。打铃是脉冲动作，静止状态永远是 0。
                    // 不压住的话 `Ring` 会被正弦波压成 0.12~0.88，界面按「≥0.5 算响」判断
                    // → 铃会自己一直在响（和门的 `Open` 是同一类问题，99 号清单第 17 条）。
                    SetState(twinId, "Ring", 0f);
                    SetState(twinId, "Volume", 70f);
                    seeded++;
                    break;
            }
        }

        // ---------------------------------------------------------------- 收命令

        private void OnCommandMessage(string topic, string payload)
        {
            if (string.IsNullOrEmpty(payload)) return;

            string twinId = ExtractTwinId(topic);
            if (string.IsNullOrEmpty(twinId))
            {
                Debug.LogWarning("[TwinCmd] 设备收到主题不合规的命令：" + topic);
                return;
            }

            MiniJson.Object o;
            if (!MiniJson.TryParse(Encoding.UTF8.GetBytes(payload), out o))
            {
                Debug.LogWarning("[TwinCmd] 设备收到非法命令载荷，已忽略：" + payload);
                return;
            }

            string id, actionName;
            float value = 0f;
            if (!o.TryGetString("id", out id) || string.IsNullOrEmpty(id))
            {
                Debug.LogWarning("[TwinCmd] 设备收到没有 id 的命令，已忽略：" + payload);
                return;
            }
            if (!o.TryGetString("action", out actionName))
            {
                Debug.LogWarning("[TwinCmd] 设备收到没有 action 的命令，已忽略：" + payload);
                return;
            }
            o.TryGetNumber("value", out value);

            // ---- 幂等：重复 id 只回 ack，不重复执行 ----
            if (_seenIds.Contains(id))
            {
                if (_logEveryCommand)
                    Debug.Log("[TwinCmd] 设备收到重复命令 " + id + "，已去重（不重复执行）");
                PublishAck(twinId, id, true, 0, AckPhase.Accepted, "duplicate");
                return;
            }
            Remember(id);

            // 主题最后一段也是动作名，但以载荷为准 —— 主题只做路由
            CommandAction action;
            if (!CommandText.TryParseAction(actionName, out action))
            {
                PublishAck(twinId, id, false, (int)CommandFailCode.NotSupported, AckPhase.Accepted, "unknown-action");
                return;
            }

            TwinDeviceModel model = ResolveModel(twinId);
            if (model == null)
            {
                PublishAck(twinId, id, false, (int)CommandFailCode.NotSupported, AckPhase.Accepted, "unknown-device");
                return;
            }

            TwinActionDefinition def = model.FindAction(actionName);
            if (def == null)
            {
                PublishAck(twinId, id, false, (int)CommandFailCode.NotSupported, AckPhase.Accepted, "unsupported");
                return;
            }

            if (value < def.Min - 0.001f || value > def.Max + 0.001f)
            {
                PublishAck(twinId, id, false, (int)CommandFailCode.OutOfRange, AckPhase.Accepted, "out-of-range");
                return;
            }

            Job job = new Job();
            job.Id = id;
            job.TwinId = twinId;
            job.Action = action;
            job.Value = value;
            job.Stage = 0;
            job.DueTime = Time.unscaledTime + Mathf.Max(0f, _ackDelaySeconds);
            _jobs.Add(job);

            if (_logEveryCommand)
                Debug.Log("[TwinCmd] 设备收到命令 " + id + "  " + twinId + " " +
                          CommandText.ActionName(action) + "(" + value.ToString("0.###") + ")");
        }

        private void Update()
        {
            // 脉冲型设备的「到点自己停」先跑 —— 它和命令队列无关，
            // 而且**必须在 `_jobs.Count == 0` 时也跑**（否则打铃后没人再发命令，就永远不会复位）。
            TickAutoOff();

            if (_jobs.Count == 0) return;

            float now = Time.unscaledTime;
            for (int i = _jobs.Count - 1; i >= 0; i--)
            {
                Job job = _jobs[i];
                if (now < job.DueTime) continue;

                if (job.Stage == 0)
                {
                    // 「我收到了，我接受这条命令」—— 注意这**不等于**「我执行了」
                    PublishAck(job.TwinId, job.Id, true, 0, AckPhase.Accepted, "");

                    job.Stage = 1;
                    job.DueTime = now + Mathf.Max(0f, _settleDelaySeconds);
                    _jobs[i] = job;
                }
                else
                {
                    if (_applyStateChanges) ApplyState(job.TwinId, job.Action, job.Value);

                    // 「我执行完了」—— 至于状态到底变没变，由命令中心回读判断，不是我说了算
                    PublishAck(job.TwinId, job.Id, true, 0, AckPhase.Actuated, "");
                    _jobs.RemoveAt(i);
                }
            }
        }

        // ---------------------------------------------------------------- 执行

        /// <summary>
        /// 【P26】这个 PC 点位代表几台电脑。
        ///
        /// 判据：**量程 ≤ 600W 视为「单台机器」**，按 `Power.Max / 180` 向下取整推台数。
        ///   · 主脑 / 机房座位（量程 300W）→ **1 台**，开 = 180W
        ///   · 机房整间（量程 3000W+）→ **10 台 / 1800W**（沿用历史值）
        ///
        /// 【为什么不给整间按量程推】3000W / 180 = 16 台 = 2880W，
        /// 而 `PC/Room_01` 的警告线是 **2600W** —— 一进 Play 就会报假警。
        /// 要改成 16 台，得同时把该点位的 WarnHigh / AlarmHigh 一起抬上去，
        /// 那是另一个改动，不混在这里。
        ///
        /// ── 为什么必须读 catalog 而不是 model（2026-11-01 拆座位后暴露）────
        /// 第一版只读 `TwinDeviceModel` 的 `Power.Max`。但 model 是**按 Category 定义**的
        /// （PC 一律 3000W），不看单个点位的实际配置。
        /// 于是拆出来的 `Seat_01..18`（catalog 里明明是 300W 单台）
        /// 全被当成"整间"，详情页显示 **1800W / 开机 10 台** —— 单台电脑不可能有这数据。
        ///
        /// 现在优先读 catalog 里这个点位自己的 `Power.Max`，取不到才退回 model。
        /// 数据生成本来就该以"这个点位的真实配置"为准，model 只是类型默认值。
        /// </summary>
        private static float PcUnitCount(string twinId, TwinDeviceModel model)
        {
            float max = -1f;

            // ① 优先：catalog 里这个点位自己的量程
            TwinRuntime rt = TwinRuntime.Instance;
            TwinPointDefinition point = (rt != null && rt.Catalog != null)
                ? rt.Catalog.Find(twinId) : null;
            if (point != null)
            {
                TwinMetricDefinition pm = point.FindMetric("Power");
                if (pm != null) max = pm.Max;
            }

            // ② 退回：model 的类型默认量程
            if (max <= 1f && model != null)
            {
                TwinPropertyDefinition prop = model.FindProperty("Power");
                if (prop != null) max = prop.Max;
            }

            if (max <= 1f) return 10f;        // 两处都取不到，沿用历史值
            if (max > 600f) return 10f;       // 整间点位
            return Mathf.Max(1f, Mathf.FloorToInt(max / 180f));
        }

        private void ApplyState(string twinId, CommandAction action, float value)
        {
            TwinDeviceModel model = ResolveModel(twinId);
            string type = model != null ? model.Type : "";

            // 【诊断】2026-11-01 拆座位后出现"点开机命令 Failed、Power 停在波形值"，
            // 加这一行用来确认 type 是否被正确识别成 PC（走错分支 watt 就只有 1W）。
            Debug.Log("[Mock] ApplyState " + twinId + " action=" + action +
                      " value=" + value.ToString("0.##") + " type=「" + type + "」" +
                      " model=" + (model != null ? model.Type : "null"));

            switch (action)
            {
                case CommandAction.SetPower:
                {
                    bool on = value >= 0.5f;
                    float watt;
                    switch (type)
                    {
                        // 【P5-12】开灯的功率必须与播种值同源，否则"点一下开灯"会把
                    // 分档过的 68W 打回 42W —— 档位只在开局生效一次，一点就废。
                    // 两处都走 `LightSeedWatt`，改档位只改一个方法。
                    case "Light": watt = on ? LightSeedWatt(twinId) : 0f; break;
                        case "AC": watt = on ? 1.2f : 0f; break;      // 量程 0~3.5kW，警告线 3
                        // 量程 0~3000W（整间 10 台 × 300W）。开 = 10 台 × 180W = 1800W，
                        // 与 SeedInitialState 的初值必须一致，否则「点开」会看起来像跳变。
                        // 【P26】台数按点位自己的功率量程推，见 PcUnitCount
                        case "PC": watt = on ? PcUnitCount(twinId, model) * 180f : 0f; break;
                        // 【P19】投影仪：量程 0~400W，开 = 310W（灯泡 + 风扇 + 信号板）
                        case "Projector": watt = on ? 310f : 0f; break;
                        default: watt = on ? 1f : 0f; break;
                    }
                    SetState(twinId, "Power", watt);

                    // 灯还有亮度这个指标。只改功率不改亮度的话，卡片上会出现
                    // 「已关」旁边写着「亮度 62%」—— 一眼就能看出是假的。
                    // 亮度是灯具自己的属性，跟着开关一起走才对。
                    if (string.Equals(type, "Light", StringComparison.Ordinal))
                        SetState(twinId, "Brightness", on ? 80f : 0f);

                    // 电脑同理：只改总功率不改开机台数，卡片上会写「已关」而「开机 10 台」。
                    if (string.Equals(type, "PC", StringComparison.Ordinal))
                        SetState(twinId, "OnCount", on ? PcUnitCount(twinId, model) : 0f);

                    // 【P3】机房整间（`PC/Room_01`）→ **联动 18 个座位点位**。
                    //
                    // ── 为什么必须在这里做 ────────────────────────────────────
                    // 拆出 `Seat_01..18` 之后，`Room_01` 不再是"那 18 台的代表"，
                    // 它只是一个聚合点位。如果只改 `Room_01` 自己的 Power，
                    // 18 个 Seat 的 Power 纹丝不动 → 表现层按各自 Power 判断亮灭，
                    // 屏幕一台都不亮 —— 就是主理人 2026-11-01 反馈的
                    // 「联动开机 / 全部关机 无法控制子电脑」。
                    //
                    // 放在数据层而不是表现层：这样 18 个座位各自的详情页、告警、
                    // 用电统计会同步变化，不会出现"屏幕亮了但数据没变"。
                    if (string.Equals(twinId, RoomPcTwinId, StringComparison.Ordinal))
                        ApplyRoomToSeats(on);
                    break;
                }

                case CommandAction.SetTemperature:
                    SetState(twinId, "SetTemp", value);
                    break;

                case CommandAction.SetValve:
                    SetState(twinId, "ValveOpen", value);
                    break;

                case CommandAction.SetOpen:
                    SetState(twinId, "Open", value >= 0.5f ? 1f : 0f);
                    break;

                case CommandAction.SetRing:
                {
                    bool ring = value >= 0.5f;
                    SetState(twinId, "Ring", ring ? 1f : 0f);

                    // 响 → 排一个 3 秒后的复位；停 → 撤掉待复位（防止「点停之后 3 秒又被复位一次」，
                    // 那会在日志里多出一条看不懂的状态变化）
                    if (ring) ScheduleAutoOff(twinId, "Ring", _bellRingSeconds);
                    else CancelAutoOff(twinId, "Ring");
                    break;
                }
            }
        }

        // ---------------------------------------------------------------- 脉冲型设备

        /// <summary>
        /// 设备侧的定时打铃 —— **不是上位机下命令**。
        ///
        /// 【为什么定时打铃不能走命令中心】真实校园里打铃器自带时钟，到点自己响；
        /// 上位机（孪生体）只是**读**它的状态。如果定时打铃也发一条 `twin/cmd/...`，
        /// 那「命令」这个词就被用坏了 —— 将来接真实网关时，网关会收到一条
        /// 没人按过的命令，而真实打铃器根本不接受外部定时。
        ///
        /// 所以这里的分界是：
        ///   · 定时 → 调这个方法（扮演设备内部的时钟）
        ///   · 手动 → 走 `TwinCommandCenter.TrySend(SetRing)` → 收命令 → ApplyState
        /// 两条路最后都落到同一个 `SetState`，表现层只认 `Ring` 这个值，不关心是谁触发的。
        /// </summary>
        public void RingNow(string twinId, float seconds)
        {
            if (string.IsNullOrEmpty(twinId)) return;
            float dur = seconds > 0.1f ? seconds : _bellRingSeconds;

            SetState(twinId, "Ring", 1f);
            ScheduleAutoOff(twinId, "Ring", dur);

            Debug.Log("[TwinCmd] 定时打铃：「" + twinId + "」响 " + dur.ToString("0.#") + " 秒" +
                      "（设备侧定时器触发，不是上位机命令）");
        }

        /// <summary>这台设备此刻在不在响。</summary>
        public bool IsRinging(string twinId)
        {
            TwinAnchor anchor = FindAnchor(twinId);
            TwinValue v;
            return anchor != null && anchor.TryGetValue("Ring", out v) && v.Value > 0.5f;
        }

        private static TwinAnchor FindAnchor(string twinId)
        {
            TwinRuntime runtime = TwinRuntime.Instance;
            TwinAnchor a;
            if (runtime != null && runtime.Registry != null && runtime.Registry.TryGet(twinId, out a))
                return a;
            return null;
        }

        private void ScheduleAutoOff(string twinId, string metric, float seconds)
        {
            CancelAutoOff(twinId, metric);

            AutoOff a = new AutoOff();
            a.TwinId = twinId;
            a.Metric = metric;
            a.DueTime = Time.unscaledTime + Mathf.Max(0.1f, seconds);
            _autoOff.Add(a);
        }

        private void CancelAutoOff(string twinId, string metric)
        {
            for (int i = _autoOff.Count - 1; i >= 0; i--)
            {
                if (string.Equals(_autoOff[i].TwinId, twinId, StringComparison.Ordinal) &&
                    string.Equals(_autoOff[i].Metric, metric, StringComparison.Ordinal))
                    _autoOff.RemoveAt(i);
            }
        }

        private void TickAutoOff()
        {
            if (_autoOff.Count == 0) return;

            float now = Time.unscaledTime;
            for (int i = _autoOff.Count - 1; i >= 0; i--)
            {
                AutoOff a = _autoOff[i];
                if (now < a.DueTime) continue;

                SetState(a.TwinId, a.Metric, 0f);
                _autoOff.RemoveAt(i);

                if (_logEveryCommand)
                    Debug.Log("[TwinCmd] 设备自动复位：「" + a.TwinId + "」的 " + a.Metric + " → 0");
            }
        }

        /// <summary>把设备的某个指标钉在指定值。这一行就是「设备状态」的全部真相。</summary>
        private void SetState(string twinId, string metric, float value)
        {
            MockDataSource.SetOverride(twinId, metric, value);
            _owned[twinId + "|" + metric] = value;
        }

        // ---------------------------------------------------------------- 回执

        private void PublishAck(string twinId, string id, bool ok, int code, AckPhase phase, string msg)
        {
            StringBuilder sb = new StringBuilder(128);
            sb.Append("{\"id\":\"").Append(id).Append("\"");
            sb.Append(",\"ok\":").Append(ok ? "true" : "false");
            sb.Append(",\"code\":").Append(code);
            sb.Append(",\"phase\":\"").Append(phase == AckPhase.Actuated ? "actuated" : "accepted").Append("\"");
            sb.Append(",\"msg\":\"").Append(msg == null ? "" : msg).Append("\"");
            sb.Append(",\"ts\":").Append(TwinClock.NowUtcSeconds().ToString("0.000", CultureInfo.InvariantCulture));
            sb.Append('}');

            LocalCommandTransport.Instance.Send("twin/ack/" + twinId, sb.ToString(), null);
        }

        // ---------------------------------------------------------------- 工具

        /// <summary>twin/cmd/{TwinId}/{Action} —— 去掉前缀和最后一段，中间就是 TwinId（TwinId 本身含 /）。</summary>
        private static string ExtractTwinId(string topic)
        {
            if (string.IsNullOrEmpty(topic)) return null;
            const string prefix = "twin/cmd/";
            if (!topic.StartsWith(prefix, StringComparison.Ordinal)) return null;

            string rest = topic.Substring(prefix.Length);
            int slash = rest.LastIndexOf('/');
            if (slash <= 0) return null;
            return rest.Substring(0, slash);
        }

        private static TwinDeviceModel ResolveModel(string twinId)
        {
            return TwinDeviceModels.Resolve(twinId);
        }

        private void Remember(string id)
        {
            _seenIds.Add(id);
            _idOrder.Enqueue(id);
            while (_idOrder.Count > DedupeCapacity)
            {
                string old = _idOrder.Dequeue();
                _seenIds.Remove(old);
            }
        }

        [ContextMenu("演练：设备只回执不执行（P12 验收用）")]
        private void ToggleApplyState()
        {
            _applyStateChanges = !_applyStateChanges;
            Debug.Log("[TwinCmd] 设备执行状态已" + (_applyStateChanges ? "开启" : "关闭（只回执不改状态）") +
                      "。关闭后命令应停在「确认中」并在 5 秒后报「执行了但状态没变」。");
        }
    }
}

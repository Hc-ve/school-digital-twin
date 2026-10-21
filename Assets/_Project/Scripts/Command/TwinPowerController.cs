using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using SchoolTwin.Data;
using SchoolTwin.Twin;

namespace SchoolTwin.Command
{
    /// <summary>
    /// 回路通断 → 设备行为。**回路模型与数据链路之间唯一的接线处。**
    ///
    /// ── 它负责三件事 ──────────────────────────────────────────────
    ///   ① 断电时让 Mock 数据源**停发**该设备的样本（真实设备断电后确实不再上报）
    ///   ② 发 / 收 `twin/state/{TwinId}` 心跳（Spec §9.5②）
    ///   ③ 供电恢复后自动让数据重新流起来
    ///
    /// ── 为什么这些事不放进 TwinCircuit ───────────────────────────
    /// `TwinCircuit` 在 `SchoolTwin.Twin` 命名空间里，而那个命名空间**刻意不引用 Command**
    /// （防循环依赖，也防止三维层直接发命令）。停发数据要碰 `MockDataSource`、
    /// 发心跳要碰 `LocalCommandTransport` —— 两件都是"设备/协议侧"的事，
    /// 所以接线放在 Command 里，`TwinCircuit` 只管"哪条回路通不通"这个纯事实。
    ///
    /// ── 心跳到底解决了什么（这条别省）─────────────────────────────
    /// 只靠"沉默 8 秒判离线"无法区分两件事：
    ///   · 命令**没送到**（网络/路由问题）
    ///   · 设备**根本不在**（掉电、被拆走）
    /// 心跳把答案提前放在手上：收到过 online=true 的设备，ack 超时就只能是"没送到"；
    /// 心跳说 offline 的设备，超时原因是"它不在"。两者给运维的下一步动作完全不同。
    ///
    /// ⚠️ 诚实的边界：进程内传输没有 MQTT Will 消息，所以 `online:false` 是**我们替设备发的**
    /// （模拟设备的"最后一口气"）。真实部署时这一条必须由 broker 的 Will 发出，
    /// 换过去只需替换 <see cref="PublishState"/> 的实现，上层判据不用改。
    /// </summary>
    [AddComponentMenu("SchoolTwin/回路接线 (TwinPowerController)")]
    [DisallowMultipleComponent]
    public sealed class TwinPowerController : MonoBehaviour
    {
        public static TwinPowerController Instance { get; private set; }

        [Header("心跳")]
        [Tooltip("心跳周期（秒）。Sparkplug 的惯例是 10 秒；比沉默判定（8 秒）长一点才不会自己打架")]
        [SerializeField] private float _heartbeatSeconds = 10f;

        [Header("断电模拟")]
        [Tooltip("断电时是否真的让 Mock 数据源停发该设备的样本。关掉 = 只做可用性标记，数据照常流")]
        [SerializeField] private bool _muteMockOnPowerOff = true;

        [Tooltip("同步间隔（秒）。断电/恢复是事件驱动的，这里只是兜底对账")]
        [SerializeField] private float _reconcileInterval = 0.5f;

        /// <summary>心跳表：TwinId → 最近一次收到的 online。</summary>
        private static readonly Dictionary<string, bool> s_online =
            new Dictionary<string, bool>(64, StringComparer.Ordinal);

        private Action<string, bool> _circuitHandler;
        private Action<string, string> _stateHandler;
        private float _nextHeartbeat;
        private float _nextReconcile;
        private int _mutedCount = -1;

        /// <summary>
        /// 心跳说这台设备在线吗？
        ///
        /// 【没收到过心跳时返回 true】不能返回 false —— 开机头 10 秒所有设备都还没发过心跳，
        /// 那时候把失败原因写成"设备不在线"是纯属编造。宁可说"没回执"，也不要凭空断言。
        /// </summary>
        public static bool IsOnline(string twinId)
        {
            if (string.IsNullOrEmpty(twinId)) return true;
            bool online;
            return !s_online.TryGetValue(twinId, out online) || online;
        }

        public static bool HasHeartbeat(string twinId)
        {
            return !string.IsNullOrEmpty(twinId) && s_online.ContainsKey(twinId);
        }

        public static void Ensure()
        {
            if (Instance != null) return;

            TwinRuntime runtime = TwinRuntime.Ensure();
            TwinPowerController found = runtime.GetComponent<TwinPowerController>();
            if (found == null) found = runtime.gameObject.AddComponent<TwinPowerController>();
        }

        /// <summary>
        /// 自己把自己挂上。和 TwinDevicePresenter 同一个理由：接线处不能依赖 Inspector 配置，
        /// 漏挂一次的表现是"断电了灯还亮着"，而且没有任何报错。
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            Ensure();
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }
            Instance = this;
            s_online.Clear();

            _circuitHandler = OnCircuitChanged;
            TwinCircuit.Changed += _circuitHandler;

            _stateHandler = OnStateMessage;
            LocalCommandTransport.Instance.Subscribe("twin/state/", _stateHandler);
        }

        private void OnDestroy()
        {
            if (_circuitHandler != null) TwinCircuit.Changed -= _circuitHandler;
            if (_stateHandler != null) LocalCommandTransport.Instance.Unsubscribe(_stateHandler);
            s_online.Clear();
            if (Instance == this) Instance = null;
        }

        private void Update()
        {
            float now = Time.unscaledTime;

            // 兜底对账：事件驱动已经覆盖了正常路径，这里防的是
            // "锚点比回路晚注册"（Start 顺序不确定）和"数据源中途被切"。
            if (now >= _nextReconcile)
            {
                _nextReconcile = now + Mathf.Max(0.1f, _reconcileInterval);
                Reconcile();
            }

            if (now >= _nextHeartbeat)
            {
                _nextHeartbeat = now + Mathf.Max(1f, _heartbeatSeconds);
                PublishAllOnline();
            }
        }

        // ---------------------------------------------------------------- 断电 → 停发

        private void OnCircuitChanged(string circuitId, bool on)
        {
            Reconcile();

            // 断电 / 恢复都要**立刻**发一次状态，不能等 10 秒的周期心跳 ——
            // 命令中心要靠它区分"设备不在"和"命令没送到"，晚 10 秒就等于没有。
            PublishForCircuit(circuitId, on);
        }

        /// <summary>
        /// 让 Mock 数据源的通断状态与回路一致。
        ///
        /// 【为什么是"对账"而不是"按事件逐台处理"】事件只知道"哪条回路变了"，
        /// 而设备的注册时机、数据源是否在跑、是不是 Mock 模式，都是别处的状态。
        /// 每 0.5 秒把所有点位和回路比一遍（32 个点位，代价可以忽略），
        /// 就不需要保证事件顺序正确 —— 这类"幂等对账"比"精确的事件链"抗错得多。
        /// </summary>
        private void Reconcile()
        {
            if (!_muteMockOnPowerOff) return;

            TwinRuntime runtime = TwinRuntime.Instance;
            if (runtime == null || runtime.Registry == null) return;

            IReadOnlyList<TwinAnchor> all = runtime.Registry.All;
            int muted = 0;

            for (int i = 0; i < all.Count; i++)
            {
                TwinAnchor a = all[i];
                if (a == null || string.IsNullOrEmpty(a.TwinId)) continue;

                bool dead = !TwinCircuit.IsPowered(a.TwinId);
                if (dead) muted++;
                MockDataSource.SetMuted(a.TwinId, dead);
            }

            // 只在数量变化时打一行：断电和恢复各一条，不会刷屏。
            // 【为什么要打这一行】「设备停发数据」是这一阶段最安静的一个动作 ——
            // 画面只是慢慢变灰，没有任何报错。留一行日志，事后才能确认它真的执行过。
            if (muted != _mutedCount)
            {
                _mutedCount = muted;
                Debug.Log("[TwinPower] 停发样本的设备：" + muted + " 台（共 " + all.Count + " 台）");
            }
        }

        // ---------------------------------------------------------------- 心跳

        private void PublishAllOnline()
        {
            TwinRuntime runtime = TwinRuntime.Instance;
            if (runtime == null || runtime.Registry == null) return;

            IReadOnlyList<TwinAnchor> all = runtime.Registry.All;
            for (int i = 0; i < all.Count; i++)
            {
                TwinAnchor a = all[i];
                if (a == null || string.IsNullOrEmpty(a.TwinId)) continue;
                PublishState(a.TwinId, TwinCircuit.IsPowered(a.TwinId), null);
            }
        }

        private void PublishForCircuit(string circuitId, bool on)
        {
            TwinRuntime runtime = TwinRuntime.Instance;
            if (runtime == null || runtime.Registry == null) return;

            IReadOnlyList<TwinAnchor> all = runtime.Registry.All;
            for (int i = 0; i < all.Count; i++)
            {
                TwinAnchor a = all[i];
                if (a == null || string.IsNullOrEmpty(a.TwinId)) continue;

                // 只发这条回路真正管到的设备：拉一层闸不该给整栋楼发心跳。
                // 用 IsPowered 而不是事件里的 on —— 上层回路断电时，下层房间的
                // 状态也跟着变，那台设备现在到底是通是断，只有 IsPowered 知道。
                if (!BelongsTo(a.TwinId, circuitId)) continue;
                PublishState(a.TwinId, TwinCircuit.IsPowered(a.TwinId), on ? null : "power-off");
            }
        }

        private static bool BelongsTo(string twinId, string circuitId)
        {
            if (string.Equals(circuitId, TwinCircuit.BuildingId, StringComparison.Ordinal)) return true;

            int slash = twinId.IndexOf('/');
            string zone = slash < 0 ? twinId : twinId.Substring(0, slash);
            string roomId = TwinCircuit.RoomId(zone);

            if (string.Equals(roomId, circuitId, StringComparison.Ordinal)) return true;
            TwinCircuitNode room = TwinCircuit.RoomOf(zone);
            return room != null && string.Equals(room.ParentId, circuitId, StringComparison.Ordinal);
        }

        /// <summary>
        /// 发一条 `twin/state/{TwinId}`：`{"online":bool,"ts":...,"avail":"..."}`。
        ///
        /// 主题段数：`twin` / `state` / `{TwinId}` —— 但 TwinId 本身含 `/`，
        /// 所以实际是 `twin/state/Class2-1/Light/Main_01`。订阅侧按前缀 `twin/state/` 匹配，
        /// 不能按段数切（P1 的教训：`+` 只匹配一层）。
        /// </summary>
        private void PublishState(string twinId, bool online, string reason)
        {
            StringBuilder sb = new StringBuilder(96);
            sb.Append("{\"online\":").Append(online ? "true" : "false");
            sb.Append(",\"ts\":").Append(TwinClock.NowUtcSeconds().ToString("0.000", CultureInfo.InvariantCulture));
            if (!online)
            {
                sb.Append(",\"avail\":\"PowerOff\"");
                sb.Append(",\"reason\":\"").Append(string.IsNullOrEmpty(reason) ? "power-off" : reason).Append('"');
            }
            else
            {
                sb.Append(",\"avail\":\"Available\"");
            }
            sb.Append('}');

            LocalCommandTransport.Instance.Send("twin/state/" + twinId, sb.ToString(), null);
        }

        /// <summary>
        /// 收到心跳 → 更新在线表。
        ///
        /// 进程内传输会把消息回给订阅者自己（和 broker 的行为一致），所以这里收到的
        /// 就是刚发出去的那条。换成 MQTT 时这条路径不用改。
        /// </summary>
        private void OnStateMessage(string topic, string payload)
        {
            if (string.IsNullOrEmpty(topic) || string.IsNullOrEmpty(payload)) return;

            const string prefix = "twin/state/";
            if (!topic.StartsWith(prefix, StringComparison.Ordinal)) return;

            string twinId = topic.Substring(prefix.Length);
            if (string.IsNullOrEmpty(twinId)) return;

            MiniJson.Object o;
            if (!MiniJson.TryParse(Encoding.UTF8.GetBytes(payload), out o)) return;

            bool online = true;
            o.TryGetBool("online", out online);
            s_online[twinId] = online;
        }

        // ---------------------------------------------------------------- 调试

        [ContextMenu("打印回路与心跳")]
        private void PrintState()
        {
            TwinCircuit.PrintAll();
            Debug.Log("[TwinPower] 心跳表 " + s_online.Count + " 条，停发设备 " +
                      Mathf.Max(0, _mutedCount) + " 台");
        }
    }
}

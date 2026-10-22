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
            int seeded = 0;

            for (int i = 0; i < ids.Count; i++)
            {
                string twinId = ids[i];
                TwinDeviceModel model = ResolveModel(twinId);
                if (model == null)
                {
                    Debug.LogWarning("[TwinCmd] 受控点位「" + twinId + "」解析不出设备模型，" +
                                     "初始状态没定 —— 它的状态会跟着模拟波形乱跳");
                    continue;
                }

                switch (model.Type)
                {
                    case "Light":
                        SetState(twinId, "Power", 42f);        // 量程 0~120W，警告线 110
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
                        SetState(twinId, "Open", 1f);
                        seeded++;
                        break;
                }
            }

            Debug.Log("[TwinCmd] 已给 " + seeded + " 台受控设备定下初始状态（白名单共 " +
                      ids.Count + " 个点位）。状态由设备代理负责，模拟波形不再改它。");
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

        private void ApplyState(string twinId, CommandAction action, float value)
        {
            TwinDeviceModel model = ResolveModel(twinId);
            string type = model != null ? model.Type : "";

            switch (action)
            {
                case CommandAction.SetPower:
                {
                    bool on = value >= 0.5f;
                    float watt;
                    switch (type)
                    {
                        case "Light": watt = on ? 42f : 0f; break;    // 量程 0~120W，警告线 110
                        case "AC": watt = on ? 1.2f : 0f; break;      // 量程 0~3.5kW，警告线 3
                        case "PC": watt = on ? 180f : 0f; break;      // 量程 0~300W，警告线 260
                        default: watt = on ? 1f : 0f; break;
                    }
                    SetState(twinId, "Power", watt);

                    // 灯还有亮度这个指标。只改功率不改亮度的话，卡片上会出现
                    // 「已关」旁边写着「亮度 62%」—— 一眼就能看出是假的。
                    // 亮度是灯具自己的属性，跟着开关一起走才对。
                    if (string.Equals(type, "Light", StringComparison.Ordinal))
                        SetState(twinId, "Brightness", on ? 80f : 0f);
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

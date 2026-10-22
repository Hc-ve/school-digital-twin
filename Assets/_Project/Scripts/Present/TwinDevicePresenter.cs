using System;
using System.Collections.Generic;
using UnityEngine;
using SchoolTwin.Command;
using SchoolTwin.Core;
using SchoolTwin.Twin;

namespace SchoolTwin.Present
{
    /// <summary>
    /// 设备表现层：把「设备状态」翻译成场景里看得见、听得见的东西。
    ///
    /// 【它读什么，不读什么 —— 这条比功能重要】
    /// 只读**数据链路里的回读值**（`TwinAnchor.TryGetValue`），**绝不读命令状态**。
    /// 因为命令只是「意图」，回读才是「事实」。如果灯亮不亮取决于「我点过开」，
    /// 那么 P12 那条最重要的验收（关掉设备执行 → 命令必须失败）就会失效 ——
    /// 界面报失败、灯却亮了，等于表现层在替系统撒谎。
    ///
    /// 所以：命令失败时灯**不会**亮，因为回读值确实没变。这是对的。
    ///
    /// 【为什么单独一个程序集命名空间】
    /// 它要同时依赖 `SchoolTwin.Twin`（锚点、设备模型）和 `SchoolTwin.Command`（命令事件，用于提示音）。
    /// 而 `Twin` 命名空间是**刻意不引用 Command** 的（防循环依赖、也防止三维层直接发命令）。
    /// 把它放进 `Twin` 会把这条约束破掉，所以单独放 `Present`。
    ///
    /// 【P13 第一刀做了什么】
    ///   ① 灯：`_EmissionColor` + 一盏点光源（开 = 暖白发光，关 = 全黑）
    ///   ② 空调：收到命令时「滴」一声（程序生成波形，零素材）
    ///
    /// 【P17 加的两件事】
    ///   ③ 灯的粒度：一个照明点位代表**整个房间的照明**，所以点一下整个房间的灯一起亮
    ///      （原来只驱动体积最大的那一个 Renderer，看着像"只能控制其中一盏"）
    ///   ④ 门：绕门轴转动，开/关都有动画。角度从几何推，不写死（场景里的门是半开的）
    /// </summary>
    [AddComponentMenu("SchoolTwin/设备表现层 (TwinDevicePresenter)")]
    public sealed class TwinDevicePresenter : MonoBehaviour
    {
        // ---------------------------------------------------------------- 调参

        [Header("灯")]
        [Tooltip("开灯时的发光色（Linear 空间下 >1 才会真的发亮）")]
        [SerializeField] private Color _lampEmission = new Color(2.20f, 2.06f, 1.78f, 1f);

        [Tooltip("点光源强度。0 = 只让灯管自己发光，不照亮房间")]
        [SerializeField] private float _lampGlowIntensity = 2.0f;

        [Tooltip("点光源照射半径（米）。0 = 按灯具尺寸自动算")]
        [SerializeField] private float _lampGlowRange = 0f;

        [Header("空调提示音")]
        [Tooltip("收到命令时是否发声（模拟空调「滴滴」两声）")]
        [SerializeField] private bool _beepOnReceive = true;

        [Tooltip("蜂鸣器频率（Hz）。真实空调在 2000~3000 之间，太低会像电子琴长音")]
        [Range(1000f, 4000f)]
        [SerializeField] private float _beepFrequency = 2400f;

        [Tooltip("提示音音量（0~1）")]
        [Range(0f, 1f)]
        [SerializeField] private float _beepVolume = 0.75f;

        [Header("轮询")]
        [Tooltip("多久读一次回读值（秒）。灯是开关量，0.1 秒已经看不出延迟")]
        [SerializeField] private float _pollInterval = 0.1f;

        [Header("门")]
        [Tooltip("从「关门」转到「全开」转多少度。门绕自己的枢轴转，所以只改旋转、不改位置")]
        [Range(10f, 170f)]
        [SerializeField] private float _doorOpenSweepDegrees = 90f;

        [Tooltip("开一次门 / 关一次门要多久（秒）。真实门大约 1 秒")]
        [Range(0.2f, 4f)]
        [SerializeField] private float _doorSwingSeconds = 1.2f;

        // ---------------------------------------------------------------- 内部

        /// <summary>
        /// 一个照明点位的可视化：**一组**灯具，不是一个。
        ///
        /// 【为什么改成一组（2026-10-22）】
        /// 原来只驱动「体积最大的那一个 Renderer」，所以点开灯只有一盏亮 ——
        /// 用户的原话是「2-1教室里面有多盏灯，但控制面板只能控制其中一盏灯」。
        /// 数据层面没问题（一个房间一个照明点位、一个 Power 值），
        /// 问题在表现层：**一个点位的物理含义是「这个房间的照明」**，不是「这一盏灯」。
        ///
        /// 所以现在按「同一房间」把灯具收成一组，一起发光。
        /// 点光源仍然**每个房间只有一盏**（放在灯具组中心）：
        /// 一个房间 10 盏灯各挂一个点光源，10 个房间就是 100 盏，
        /// Built-in 前向渲染的逐像素灯上限会直接吃掉性能，而且看起来也没有区别。
        /// </summary>
        private sealed class LampVisual
        {
            public readonly List<Renderer> Renderers = new List<Renderer>(16);
            public readonly List<Material> Materials = new List<Material>(16);
            public readonly List<bool> HasEmission = new List<bool>(16);
            public Light Glow;
            public bool IsOn;
        }

        private static readonly int EmissionId = Shader.PropertyToID("_EmissionColor");

        /// <summary>
        /// 一扇门的可视化：绕**门轴**转一个角度。
        ///
        /// 【为什么只需要转、不需要移】`Door07a` 预制体的枢轴就在门轴上：
        /// 它自带的 BoxCollider 是 x 从 -0.006 到 0.835、y 从 0 到 1.943，
        /// 也就是说原点落在「门轴那一侧的底角」上。所以绕本地 Y 轴转 = 绕门轴转，
        /// 位置一个字节都不用动。这也是为什么不需要像 P4 那样去实测反推轴心。
        ///
        /// 【角度为什么记在 `Angle` 里，而不是直接读 transform.eulerAngles】
        /// 动画是逐帧推进的，读回来再算会有浮点漂移；而且门的初始姿态是"半开"
        /// （场景里 12 扇门都是 64° 左右），不是 0°，用绝对角度会算错基准。
        /// 这里所有角度都是**相对初始姿态**的偏移，0 = 一动不动。
        /// </summary>
        private sealed class DoorVisual
        {
            public Transform T;
            public Quaternion BaseRotation;   // 场景里原来的姿态，Angle=0 时就是它
            public float ClosedAngle;         // 门扇贴平墙面的角度（相对初始姿态）
            public float OpenAngle;           // 全开的角度
            public float Angle;               // 当前角度
            public bool IsOpen;

            /// <summary>
            /// 是否已经读到过一次状态。
            ///
            /// 【为什么需要】`IsOpen` 默认 false，而 TickDoors 是逐帧跑的、比 0.1 秒的轮询更早。
            /// 没有这个标记的话，按 Play 的第一帧门会朝"关"的方向抽一下（约 5°）——
            /// 因为还没收到数据，它就先按"关"开始动了。
            /// </summary>
            public bool HasState;
        }

        private readonly Dictionary<string, LampVisual> _lamps =
            new Dictionary<string, LampVisual>(4, StringComparer.Ordinal);

        private readonly Dictionary<string, DoorVisual> _doors =
            new Dictionary<string, DoorVisual>(2, StringComparer.Ordinal);

        /// <summary>
        /// 确认过「没有可视部件」的点位（标记牌承载）。
        ///
        /// 【为什么必须记下来】SyncOne 每 0.1 秒把 15 个点位过一遍，失败的点位不会进 _lamps，
        /// 于是每一轮都会重新 BuildLamp、重新打一条日志。实测 3 个点位 = 30 条/秒，
        /// Console 半分钟破 999，真正的错误反而看不见了。
        /// </summary>
        private readonly HashSet<string> _noVisual = new HashSet<string>(StringComparer.Ordinal);

        private TwinRuntime _runtime;
        private AudioSource _audio;
        private AudioClip _beep;
        private float _beepFreqUsed;
        private float _nextPoll;
        private bool _subscribed;
        private Action<TwinCommand> _commandHandler;

        public static TwinDevicePresenter Instance { get; private set; }

        // ---------------------------------------------------------------- 生命周期

        /// <summary>
        /// 自己把自己挂上，不依赖任何其它层来创建。
        ///
        /// 【为什么不从 TwinCommandCenter 里 Ensure】那会造成 `Command → Present` 的反向引用，
        /// 而 Present 本来就依赖 Command（要听命令事件做提示音），两向引用会让依赖图变成环。
        /// 表现层是「订阅者」—— 由它主动订阅别人，别人不需要知道它存在。
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            Ensure();
        }

        /// <summary>拿到实例；场景里没有就挂在 TwinRuntime 上。</summary>
        public static TwinDevicePresenter Ensure()
        {
            if (Instance != null) return Instance;

            TwinRuntime runtime = TwinRuntime.Ensure();
            TwinDevicePresenter found = runtime.GetComponent<TwinDevicePresenter>();
            if (found == null) found = runtime.gameObject.AddComponent<TwinDevicePresenter>();
            return found;
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }

            Instance = this;
            _runtime = TwinRuntime.Instance;

            if (_beepOnReceive) BuildAudio();

            _commandHandler = OnCommandChanged;
            TwinCommandCenter center = TwinCommandCenter.Instance;
            if (center != null)
            {
                center.Changed += _commandHandler;
                _subscribed = true;
            }
        }

        private void OnDestroy()
        {
            TwinCommandCenter center = TwinCommandCenter.Instance;
            if (center != null && _commandHandler != null) center.Changed -= _commandHandler;

            foreach (KeyValuePair<string, LampVisual> kv in _lamps)
            {
                LampVisual v = kv.Value;
                for (int i = 0; i < v.Materials.Count; i++)
                {
                    if (v.Materials[i] != null) Destroy(v.Materials[i]);
                }
                if (v.Glow != null && v.Glow.gameObject != null) Destroy(v.Glow.gameObject);
            }
            _lamps.Clear();
            _doors.Clear();
            _noVisual.Clear();

            if (_beep != null) Destroy(_beep);
            if (Instance == this) Instance = null;
        }

        private void Update()
        {
            // 门的转动是**逐帧动画**，不能挂在 0.1 秒的轮询里 ——
            // 10Hz 推进一次的话，1.2 秒的开门只有 12 个采样点，看着像卡了一下卡了一下。
            TickDoors(Time.deltaTime);

            if (Time.unscaledTime < _nextPoll) return;
            _nextPoll = Time.unscaledTime + Mathf.Max(0.02f, _pollInterval);

            RefreshBeepIfNeeded();

            if (_runtime == null) _runtime = TwinRuntime.Instance;
            if (_runtime == null || _runtime.Registry == null) return;

            // 命令中心可能比本组件晚建（用户第一次选中可空设备时才 Ensure），
            // 所以订阅这件事也在这里补一次。
            if (!_subscribed && _commandHandler != null)
            {
                TwinCommandCenter center = TwinCommandCenter.Instance;
                if (center != null)
                {
                    center.Changed += _commandHandler;
                    _subscribed = true;
                }
            }

            IReadOnlyList<string> ids = TwinControlScope.All;
            for (int i = 0; i < ids.Count; i++) SyncOne(ids[i]);
        }

        // ---------------------------------------------------------------- 同步

        private void SyncOne(string twinId)
        {
            TwinAnchor anchor;
            if (!_runtime.Registry.TryGet(twinId, out anchor) || anchor == null) return;

            TwinDeviceModel model = TwinDeviceModels.Resolve(twinId);
            if (model == null) return;

            if (string.Equals(model.Type, "Light", StringComparison.Ordinal))
                SyncLamp(twinId, anchor);
            else if (string.Equals(model.Type, "Door", StringComparison.Ordinal))
                SyncDoor(twinId, anchor);

            // AC 目前没有可视化部件，只有提示音（见 OnCommandChanged）
        }

        private void SyncLamp(string twinId, TwinAnchor anchor)
        {
            // 已经确认过「这个点位没有可视部件」就别再试了。
            // 不记住的话，每 0.1 秒会重试一次并重新打一条日志 —— 实测 3 个点位
            // 30 条/秒，Console 半分钟就 999+，把真正的信息全冲掉。
            if (_noVisual.Contains(twinId)) return;

            LampVisual v;
            if (!_lamps.TryGetValue(twinId, out v))
            {
                v = BuildLamp(anchor);
                if (v == null) { _noVisual.Add(twinId); return; }
                _lamps[twinId] = v;
            }

            TwinValue power;
            bool on = anchor.TryGetValue("Power", out power) && power.Value > 0.5f;

            // 【P15】断电时灯必须是灭的。
            //
            // 断电后数据链路整台设备都停了，`TryGetValue` 读到的还是**断电前最后一条样本**
            // （对灯来说就是"开着"）。不看可用性的话，闸都拉了、灯还亮着 ——
            // 这是表现层最容易被抓住的一处"假"。
            //
            // 注意这**不是**在读命令状态（那条红线没破）：回路通断是设备侧的物理事实，
            // 和"我点过开"完全是两件事。
            if (!TwinCircuit.IsPowered(twinId)) on = false;

            ApplyLamp(v, on);
        }

        private LampVisual BuildLamp(TwinAnchor anchor)
        {
            // 找「谁该发光」时要走 VisualTarget，不能直接拿 anchor.gameObject。
            //
            // 【为什么】锚点有两种承载方式（见 P4-6 的规则「有实物就染色实物，没实物就挂标记牌」）：
            //   · 有实物 → 锚点就在灯具上，VisualTarget 默认回退成自己，两者一样；
            //   · 没实物 → 锚点在墙上的 TwinPointMarker 上，那是个 WorldSpace Canvas，
            //     没有 MeshRenderer，灯管无处发光。这时 VisualTarget 指向事后补的灯具。
            // TwinStatusColor 早就在用 VisualTarget 了（见它的 CollectRenderers），
            // 表现层跟着用同一个间接层，两个系统的「染色目标」和「发光目标」才不会分叉。
            GameObject target = anchor.VisualTarget;
            Renderer[] renderers = target != null
                ? target.GetComponentsInChildren<Renderer>(true)
                : null;
            if (renderers == null || renderers.Length == 0)
            {
                // 【这是已知边界，不是故障】P4-6 记录过：ClassComputer / ClassDirector /
                // ClassTeachers 三间房的天花板上没有 LightLum01_01 实例（105 个灯具里没有一盏
                // 落在它们头顶），所以那三个照明点位是挂在 TwinPointMarker 标记牌上的。
                //
                // 用 Log 不用 LogWarning：Warning 意味着「有东西需要修」，而这里没有。
                // 用「说一次」不用「每次说」：调用方记住 _noVisual，不再重试。
                Debug.Log("[TwinPresent] 「" + anchor.TwinId + "」没有可视部件（锚点在标记牌上，" +
                          "VisualTarget 也没指向任何有 Renderer 的物体）——" +
                          "这盏灯不会有发光和照明效果，开关结果只能在标记牌/卡片上看数值。此提示只出现一次。");
                return null;
            }

            // 取体积最大的那个：灯具预制体里可能有细小的装饰件，灯管才是主体
            Renderer main = renderers[0];
            float best = -1f;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer r = renderers[i];
                if (r == null) continue;
                float size = r.bounds.size.sqrMagnitude;
                if (size > best) { best = size; main = r; }
            }
            if (main == null) return null;

            // 把「同一个房间的其它灯」也收进来 —— 一个照明点位代表的是**这个房间的照明**
            List<Renderer> group = CollectLampGroup(anchor, main);

            LampVisual v = new LampVisual();
            Bounds groupBounds = main.bounds;

            for (int i = 0; i < group.Count; i++)
            {
                Renderer r = group[i];
                if (r == null) continue;

                // `.material` 会克隆一份，改的是这一盏自己的材质，不影响其它房间的灯
                Material m = r.material;
                bool has = m != null && m.HasProperty(EmissionId);
                if (has) m.EnableKeyword("_EMISSION");

                v.Renderers.Add(r);
                v.Materials.Add(m);
                v.HasEmission.Add(has);
                if (i > 0) groupBounds.Encapsulate(r.bounds);
            }

            v.Glow = BuildGlow(anchor, groupBounds);

            Debug.Log("[TwinPresent] 灯已接管：" + anchor.TwinId +
                      "（" + v.Renderers.Count + " 盏灯 / 发光 " + CountEmission(v) + " 盏" +
                      "，点光源=" + (v.Glow != null ? "有（房间级，1 盏）" : "无") + "）");

            return v;
        }

        private static int CountEmission(LampVisual v)
        {
            int n = 0;
            for (int i = 0; i < v.HasEmission.Count; i++) if (v.HasEmission[i]) n++;
            return n;
        }

        /// <summary>
        /// 把「同一个房间的所有灯具」收成一组。
        ///
        /// ── 判据（两条，缺一不可）──────────────────────────────────────
        ///   ① **同一层**：灯的 y 与代表灯相差不超过 1.6m（1F 灯 y=3.34、2F 灯 y=6.90，
        ///      相差 3.56m）。1F 与 2F 的走廊 XZ 完全重合，只靠平面范围分不开。
        ///   ② **落在本房间的水平范围内**：范围查 `TwinRoomBounds`（表驱动）。
        ///
        /// ── 为什么不能用「离哪个分区中心最近」──────────────────────────
        /// 2026-10-22 实测过：走廊只有 2~3 个锚点、且集中在走廊一头，算出来的"中心"偏在一端，
        /// 结果 10 个分区里有 2 个走廊吃掉了 21 + 39 盏灯 —— 点一下"二层走廊照明"，
        /// 半栋楼的灯都亮了。房间边界这件事**只能实测反推后写成表**，不能现场算。
        /// 详细理由与实测数字见 `TwinRoomBounds` 的类注释。
        ///
        /// ── 为什么用「父节点」而不是「按名字找灯」────────────────────────
        /// `main.transform.parent` 就是那个灯具容器，不需要知道它叫什么名字
        /// （「名字不是身份，TwinId 才是」—— 99 号清单第 7 条）。
        ///
        /// ── 失败时怎么办 ──────────────────────────────────────────────
        /// 这个分区没登记范围、或拿不到容器 → **只返回代表灯自己**，也就是退回到改动之前的行为。
        /// 宁可少亮，也不能因为算错把隔壁房间的灯点亮。
        /// </summary>
        private List<Renderer> CollectLampGroup(TwinAnchor anchor, Renderer main)
        {
            List<Renderer> group = new List<Renderer>(16);
            group.Add(main);

            string zone = anchor.Zone;
            if (string.IsNullOrEmpty(zone)) return group;

            if (!TwinRoomBounds.Has(zone))
            {
                Debug.Log("[TwinPresent] 「" + anchor.TwinId + "」所在分区「" + zone + "」还没在 " +
                          "TwinRoomBounds 里登记房间范围，这次只驱动代表的那一盏灯。" +
                          "要让整个房间一起亮，给这个分区补一行水平范围即可。此提示只出现一次。");
                return group;
            }

            Transform container = main.transform.parent;
            if (container == null || container.childCount <= 1) return group;

            // 同层判据的基准：代表灯自己的高度
            float floorY = main.bounds.center.y;

            for (int i = 0; i < container.childCount; i++)
            {
                Transform child = container.GetChild(i);
                if (child == null) continue;

                Vector3 p = child.position;
                if (Mathf.Abs(p.y - floorY) > 1.6f) continue;
                if (!TwinRoomBounds.Contains(zone, p.x, p.z)) continue;

                Renderer[] rs = child.GetComponentsInChildren<Renderer>(true);
                for (int k = 0; k < rs.Length; k++)
                {
                    Renderer r = rs[k];
                    if (r == null || group.Contains(r)) continue;
                    group.Add(r);
                }
            }

            return group;
        }

        private Light BuildGlow(TwinAnchor anchor, Bounds bounds)
        {
            if (_lampGlowIntensity <= 0.001f) return null;

            GameObject go = new GameObject("TwinLampGlow");
            go.transform.SetParent(anchor.transform, false);
            go.transform.localPosition = anchor.transform.InverseTransformPoint(bounds.center);
            go.transform.localRotation = Quaternion.identity;

            Light light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(1f, 0.95f, 0.86f);
            light.range = _lampGlowRange > 0.01f
                ? _lampGlowRange
                : Mathf.Clamp(bounds.size.magnitude * 0.75f, 3f, 8f);
            light.intensity = 0f;                 // 由 ApplyLamp 决定
            light.shadows = LightShadows.None;    // 一盏灯的阴影不值这个开销
            light.renderMode = LightRenderMode.Auto;
            return light;
        }

        private void ApplyLamp(LampVisual v, bool on)
        {
            if (v.IsOn == on) return;
            v.IsOn = on;

            for (int i = 0; i < v.Materials.Count; i++)
            {
                Material m = v.Materials[i];
                if (m == null || !v.HasEmission[i]) continue;
                m.SetColor(EmissionId, on ? _lampEmission : Color.black);
            }

            if (v.Glow != null) v.Glow.intensity = on ? _lampGlowIntensity : 0f;
        }

        // ---------------------------------------------------------------- 门

        /// <summary>
        /// 读门的回读状态（`Open`），决定它该开还是该关。真正转动在 TickDoors。
        ///
        /// 【为什么不看回路（`TwinCircuit.IsPowered`）】灯断电必须灭，因为灯是电驱动的；
        /// 门是**手动**的，断电它不会自己关上。把门挂到回路上会做出"拉闸之后门啪一下关了"
        /// 这种物理上不存在的行为。这跟 P15 的「断电灯必须灭」不矛盾 —— 那条针对的是耗电设备。
        /// </summary>
        private void SyncDoor(string twinId, TwinAnchor anchor)
        {
            if (_noVisual.Contains(twinId)) return;

            DoorVisual v;
            if (!_doors.TryGetValue(twinId, out v))
            {
                v = BuildDoor(anchor);
                if (v == null) { _noVisual.Add(twinId); return; }
                _doors[twinId] = v;
            }

            TwinValue open;
            // 没有数据时不要动门：宁可按原样摆着，也不要因为"还没收到第一个样本"
            // 就把它关一下再开一下（那是肉眼可见的抽搐）。
            if (!anchor.TryGetValue("Open", out open)) return;

            v.IsOpen = open.Value >= 0.5f;
            v.HasState = true;
        }

        /// <summary>
        /// 每帧推进门的转动。已经到位的门不写 Transform ——
        /// 12 扇门每帧都写一次旋转，会把 Transform 的变更标记一直点亮，得不偿失。
        /// </summary>
        private void TickDoors(float dt)
        {
            if (_doors.Count == 0) return;

            float step = Mathf.Max(0f, dt);

            foreach (KeyValuePair<string, DoorVisual> kv in _doors)
            {
                DoorVisual v = kv.Value;
                if (v == null || v.T == null || !v.HasState) continue;

                float target = v.IsOpen ? v.OpenAngle : v.ClosedAngle;
                float span = Mathf.Abs(v.OpenAngle - v.ClosedAngle);
                // 0.2s 是 [Range] 的下限：序列化里没有这个键时（旧场景资产），
                // Unity 会用字段初始值 1.2；万一拿到 0 也不会变成"瞬间闪一下"。
                float speed = span / Mathf.Max(0.2f, _doorSwingSeconds);

                float next = Mathf.MoveTowards(v.Angle, target, speed * step);
                if (Mathf.Abs(next - v.Angle) < 0.001f) continue;

                v.Angle = next;
                v.T.rotation = Quaternion.AngleAxis(v.Angle, Vector3.up) * v.BaseRotation;
            }
        }

        /// <summary>
        /// 算出这扇门的「关门角」和「全开角」。
        ///
        /// ── 为什么要这么绕 ────────────────────────────────────────────
        /// 场景里的门**不是关着的**：12 扇 Door07a 都摆成半开姿态（约 64°）。
        /// 所以不能简单地"转 90° 就是开"，必须先知道"关"在哪一边。
        ///
        /// 判据是**这扇门贴在哪面墙上**（详见 ResolveWallIsAlongX）：
        ///   ① 门总在墙上，墙总在房间范围的外沿 → 门在哪根轴上跑出了房间范围，那根轴就是墙的法线；
        ///   ② 墙的方向就是门关上时门扇的方向（门扇贴平墙面）；
        ///   ③ 两个方向里取离当前姿态更近的那个 —— 半开的门离"关"一定比离"反方向"近。
        ///
        /// Class2-1 实测（2026-10-22）：门枢轴 (-12.57, 33.49)，房间范围 x∈[-25.5,-12.4]、z∈[34.5,40]
        ///   → X 在范围内、Z 跑出去 1.01m → 南墙、沿 X；
        ///   门扇本地 +X 的世界朝向 yaw = -64.4° → 关门角 -64.4°（转回贴平墙面）、
        ///   开门角 0°（模型原位就是开）。已验证：关到位后门扇方向 = (1, 0)、正好贴平南墙。
        ///
        /// 任何一步算不出来就**不接管这扇门**，并在 Console 说清楚原因：
        /// 卡片上的开/关状态照样是对的，只是三维里不会转。
        /// </summary>
        private DoorVisual BuildDoor(TwinAnchor anchor)
        {
            GameObject target = anchor.VisualTarget;
            if (target == null) target = anchor.gameObject;

            Transform t = target.transform;

            // 门扇方向 = 本地 +X（Door07a 的 BoxCollider 是 x∈[0,0.835]，从原点往 +X 长）
            Vector3 panel = t.right;
            panel.y = 0f;
            if (panel.sqrMagnitude < 0.0001f)
            {
                Debug.LogWarning("[TwinPresent] 「" + anchor.TwinId + "」的朝向是竖直的，" +
                                 "算不出门扇方向，开合动画不启用");
                return null;
            }
            panel.Normalize();

            bool wallAlongX;
            if (!ResolveWallIsAlongX(anchor, out wallAlongX))
            {
                Debug.LogWarning("[TwinPresent] 「" + anchor.TwinId + "」算不出它贴在哪面墙上" +
                                 "（房间范围没登记，同分区里除门以外的锚点也不到 3 个），" +
                                 "门的开合动画不启用。卡片上的状态照样是对的。");
                return null;
            }

            // 静态合批的对象转了也看不见（合批在进 Play 时就做完了）。
            // 绑定时工具会自动取消 BatchingStatic，但换一台机器 / 重新导入后可能又回来，
            // 所以这里再查一次 —— 这类问题界面上完全没有反馈，只能靠日志。
            Renderer r = target.GetComponentInChildren<Renderer>();
            if (r != null && r.isPartOfStaticBatch)
            {
                Debug.LogWarning("[TwinPresent] 「" + anchor.TwinId + "」还在静态合批里，" +
                                 "转动不会显示出来。选中这个门 → Inspector 右上角取消勾选 Static，" +
                                 "或重新用绑定工具过一遍");
            }

            Vector3 wallDir = wallAlongX ? Vector3.right : Vector3.forward;
            Vector3 closedDir = Vector3.Dot(panel, wallDir) >= 0f ? wallDir : -wallDir;

            // 把当前姿态转到「贴平墙面」需要的角度
            float closedAngle = Vector3.SignedAngle(panel, closedDir, Vector3.up);

            // 开门往哪边转：顺着门现在半开的那个方向继续转（半开方向 = 从关到当前的方向）
            float sign = Mathf.Abs(closedAngle) < 1f ? 1f : -Mathf.Sign(closedAngle);
            float sweep = Mathf.Clamp(_doorOpenSweepDegrees, 10f, 170f);

            // 「开」对应哪个角度 —— 分两种情况，不然按 Play 的第一帧门会自己弹一下：
            //   · 门**本来就是半开的**（场景里 12 扇 Door07a 都摆成 64°）：
            //     原位就是"开"。开门 = 回到原位（0°），关门 = 转回贴墙。
            //   · 门**本来就是关着的**：原位是"关"，得真的转出去，用 `_doorOpenSweepDegrees`。
            float openAngle = Mathf.Abs(closedAngle) >= 10f ? 0f : sign * sweep;

            DoorVisual v = new DoorVisual();
            v.T = t;
            v.BaseRotation = t.rotation;
            v.ClosedAngle = closedAngle;
            v.OpenAngle = openAngle;
            v.Angle = 0f;

            Debug.Log("[TwinPresent] 门已接管：" + anchor.TwinId + "（" + t.name + "）" +
                      " 墙向=" + (wallAlongX ? "X" : "Z") +
                      " 关门角=" + closedAngle.ToString("0.0") + "°" +
                      " 开门角=" + openAngle.ToString("0.0") + "°" +
                      "（角度相对模型当前姿态，0° = 原样）");

            return v;
        }

        /// <summary>
        /// 这扇门贴的那面墙是沿 X 走还是沿 Z 走。
        ///
        /// ── 主判据：门在房间范围外的那根轴，就是墙的法线 ────────────────
        /// 门总在墙上，而墙总在房间范围的外沿。所以只要问一句
        /// 「这个门的位置，在 X 方向跑出房间范围了吗？在 Z 方向跑出去了吗？」：
        ///   · 只有 Z 跑出去 → 墙垂直于 Z → **墙沿 X 走**
        ///   · 只有 X 跑出去 → 墙垂直于 X → 墙沿 Z 走
        ///   · 两个都跑出去 / 都没跑出去 → 说不清，走兜底
        ///
        /// Class2-1 实测：门 (-12.57, 33.49)，房间范围 x∈[-25.5,-12.4]、z∈[34.5,40]
        ///   → X 在范围内（离东边界 0.17m），Z 跑出去 1.01m → 墙沿 X。
        /// **注意别用"离哪条边最近"**：那个判据会选东边界（0.17m），把门判成贴东墙，
        /// 结果关门时门会转进走廊，而不是贴平南墙。2026-10-22 实测踩过。
        ///
        /// ── 兜底：同分区非门锚点的水平包围盒 ──────────────────────────
        /// 房间范围没登记（或说不清）时，用同分区其它锚点围出的盒子，
        /// 看门跑出这个盒子的哪一根轴。
        /// **必须排除 BAK 锚点**：它们和正式锚点同 zone、位置又略有偏差，
        /// 混进来会把包围盒撑到门的另一侧（实测 maxX 从 -13.15 变 -11.69，
        /// 于是"到 x 边界 0m"，直接判错成 Z 墙）。
        /// </summary>
        private bool ResolveWallIsAlongX(TwinAnchor door, out bool alongX)
        {
            alongX = false;

            Vector3 hinge = door.transform.position;
            string zone = door.Zone;

            // ---- 主判据 ----
            float bx0, bx1, bz0, bz1;
            if (!string.IsNullOrEmpty(zone) &&
                TwinRoomBounds.TryGetBounds(zone, out bx0, out bx1, out bz0, out bz1))
            {
                bool outX = hinge.x < bx0 || hinge.x > bx1;
                bool outZ = hinge.z < bz0 || hinge.z > bz1;
                if (outX != outZ)
                {
                    alongX = outZ;          // 只有 Z 跑出去 → 墙沿 X
                    return true;
                }
            }

            // ---- 兜底：同分区非门锚点的包围盒 ----
            TwinRuntime runtime = _runtime != null ? _runtime : TwinRuntime.Instance;
            TwinRegistry registry = runtime != null ? runtime.Registry : null;
            if (registry == null) return false;
            if (string.IsNullOrEmpty(zone)) return false;

            float minX = float.MaxValue, maxX = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;
            int n = 0;

            IReadOnlyList<TwinAnchor> all = registry.All;
            for (int i = 0; i < all.Count; i++)
            {
                TwinAnchor a = all[i];
                if (a == null || a == door) continue;
                if (!string.Equals(a.Zone, zone, StringComparison.Ordinal)) continue;
                if (string.Equals(a.Category, "Door", StringComparison.Ordinal)) continue;
                if (a.TwinId != null && a.TwinId.StartsWith("BAK", StringComparison.Ordinal)) continue;

                Vector3 p = a.transform.position;
                if (p.x < minX) minX = p.x;
                if (p.x > maxX) maxX = p.x;
                if (p.z < minZ) minZ = p.z;
                if (p.z > maxZ) maxZ = p.z;
                n++;
            }

            if (n < 3) return false;

            // 门落在包围盒外时，到该方向边界的距离；落在里面算 0
            float dx = Mathf.Max(0f, Mathf.Max(minX - hinge.x, hinge.x - maxX));
            float dz = Mathf.Max(0f, Mathf.Max(minZ - hinge.z, hinge.z - maxZ));

            if (dx <= 0.0001f && dz <= 0.0001f) return false;   // 门在盒子里面，说不清
            if (dx <= 0.0001f) { alongX = true; return true; }  // 只有 Z 跑出去
            if (dz <= 0.0001f) { alongX = false; return true; } // 只有 X 跑出去

            // 两个都跑出去 → 取跑得更远的那个方向（门在外面更"明显"）
            alongX = dz <= dx;
            return true;
        }

        // ---------------------------------------------------------------- 提示音

        private void OnCommandChanged(TwinCommand cmd)
        {
            if (cmd == null || !_beepOnReceive) return;

            // 「收到」= Accepted。用 Accepted 而不是 Confirmed：
            // 真实空调是在**收到信号**时滴一声，那时候还没执行完。
            if (cmd.State != CommandState.Accepted) return;

            TwinDeviceModel model = TwinDeviceModels.Resolve(cmd.TwinId);
            if (model == null || !string.Equals(model.Type, "AC", StringComparison.Ordinal)) return;

            PlayBeep();
        }

        private void BuildAudio()
        {
            _beep = BuildBeepClip();
            _beepFreqUsed = _beepFrequency;
            _audio = gameObject.AddComponent<AudioSource>();
            _audio.playOnAwake = false;
            _audio.loop = false;
            _audio.spatialBlend = 0f;    // 2D：相机在走廊另一头也听得到
            _audio.volume = 1f;          // 波形本身已经压到 0.42，这里不再二次衰减
            _audio.dopplerLevel = 0f;
        }

        /// <summary>
        /// 在 Inspector 里拖「Beep Frequency」时立刻重建波形。
        ///
        /// 【为什么要这个】声音好不好听，代码写不出来也听不出来 —— 只能现场按耳朵调。
        /// 波形只在 Awake 生成一次，不给个重建钩子的话，拖动滑块毫无反应，
        /// 看起来像「这个参数没用」。
        /// </summary>
        private void RefreshBeepIfNeeded()
        {
            if (!_beepOnReceive || _audio == null) return;
            if (Mathf.Abs(_beepFreqUsed - _beepFrequency) < 1f) return;

            if (_beep != null) Destroy(_beep);
            _beep = BuildBeepClip();
            _beepFreqUsed = _beepFrequency;
        }

        private void PlayBeep()
        {
            if (_audio == null || _beep == null) return;
            _audio.PlayOneShot(_beep, Mathf.Clamp01(_beepVolume));
        }

        /// <summary>
        /// 程序生成「滴滴」两声提示音。零音频素材。
        ///
        /// 【为什么上一版听起来很软 —— 三个原因叠加】
        ///   ① 纯正弦：只有基频、没有谐波，听着像电子琴长音。真实空调是压电蜂鸣器，
        ///      本质接近方波，靠 3、5、7 次谐波才「刺」得出来。
        ///   ② 钟形包络 `sin(π·i/n)`：100ms 里慢慢起慢慢落，起音就占了 50ms。
        ///      人耳判断「是不是按钮音」主要看**起音有多陡**，慢起音 = 温柔 = 不像设备。
        ///   ③ 1180→1560Hz 的滑音太低。真实蜂鸣器在 2~3kHz，而且是两声**同音**「滴滴」，
        ///      不是一个往上滑的音。
        ///
        /// 【这一版怎么改】
        ///   · 波形：`tanh(3·sin)` 软方波 —— 有谐波但不像硬方波那样刺耳（硬方波高频混叠很脏）
        ///   · 包络：2ms 硬起（几乎瞬时）+ 指数衰减（τ=22ms）+ 末尾 4ms 强制归零（防爆音）
        ///   · 频率：2400Hz，两声同音，各 62ms，中间静音 55ms
        ///
        /// 【为什么要累加相位】换频率/重起一声时不能直接写 `sin(2πft)`，
        /// 频率突变会造成相位跳变，又是一声爆音。按 `phase += 2πf/rate` 累加才连续。
        /// </summary>
        private AudioClip BuildBeepClip()
        {
            const int rate = 44100;
            const float beepSeconds = 0.062f;   // 单声长度
            const float gapSeconds = 0.055f;    // 两声之间
            const float attackSeconds = 0.002f; // 起音：2ms，越短越「硬」
            const float decayTau = 0.022f;      // 指数衰减时间常数
            const float tailSeconds = 0.004f;   // 末尾强制淡出，防爆音
            const float amplitude = 0.42f;

            int beepN = Mathf.RoundToInt(rate * beepSeconds);
            int gapN = Mathf.RoundToInt(rate * gapSeconds);
            int total = beepN * 2 + gapN;

            float[] data = new float[total];
            double phase = 0d;
            int idx = 0;

            for (int b = 0; b < 2; b++)
            {
                for (int i = 0; i < beepN; i++)
                {
                    phase += 2d * Math.PI * _beepFrequency / rate;

                    // 软方波：有谐波、够刺，但没有硬方波的高频混叠
                    double wave = Math.Tanh(3.0 * Math.Sin(phase));

                    float t = i / (float)rate;
                    float env = t < attackSeconds
                        ? t / attackSeconds                       // 硬起
                        : Mathf.Exp(-(t - attackSeconds) / decayTau);  // 指数衰减

                    // 末尾 4ms 线性收到 0：指数衰减永远到不了 0，直接切断会「啪」一声
                    float remain = beepSeconds - t;
                    if (remain < tailSeconds) env *= Mathf.Clamp01(remain / tailSeconds);

                    data[idx++] = (float)wave * env * amplitude;
                }

                // 两声之间是静音，数组默认就是 0，跳过即可
                idx += gapN;
            }

            AudioClip clip = AudioClip.Create("TwinAckBeep", total, 1, rate, false);
            clip.SetData(data, 0);
            return clip;
        }
    }
}

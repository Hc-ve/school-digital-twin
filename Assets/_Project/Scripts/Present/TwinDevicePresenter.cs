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
        [Tooltip("开灯时的自发光颜色。**数值不要超过 0.6**，否则灯管会糊成一块纯白")]
        [SerializeField] private Color _lampEmission = new Color(0.60f, 0.56f, 0.46f, 1f);
        // 【调参历史】
        //   2026-10-22 第六轮：2.2 → 0.35，用户原话「灯本体的材质还是过曝」
        //   2026-10-26：0.35 → 0.60，用户原话「灯发的光不贴切现实」
        //     —— 0.35 太暗，灯管只比天花板亮一丝，读不出"正在发光"，
        //        看着像一块没通电的灰白面板。0.60 是本文件里记录的经验上限。
        //
        // 【为什么上限是 0.6】Unity Standard shader 的最终颜色是：
        //   最终色 = albedo × 光照 + emission
        // 灯管 albedo 本来就接近白，emission 再高两者相加就整体过 1，整根灯糊成纯白、
        // 丢失"灯管"的形状感。真正的亮度感来自每盏灯那个点光源，不是这里。
        //
        // 【想要"光晕"必须上后处理】本项目是**内置管线**、且没装 Post Processing Stack，
        // 没有 Bloom —— emission 只会把表面提亮，永远不会往外晕开。
        // 要真发光，得二选一：装 PPS 加 Bloom，或给每盏灯加一个朝向相机的加法光晕面片。
        //
        // 【数值依据】灯管 albedo ≈ 1.0，场景是烘焙光照、灯管处烘焙光约 0.7：
        //   emission 0.35 → 总亮度 ≈ 1.05（刚好白，但偏灰、读不出"在发光"）
        //   emission 0.60 → 总亮度 ≈ 1.30（更亮，形状和边缘还在）  ← 当前
        //   emission 2.20 → 总亮度 ≈ 2.90（裁剪成死白，形状全丢）  ← 2026-10-22 那次

        [Tooltip("**每盏灯**的点光源强度。0 = 只让灯管自己发光，不照亮房间")]
        [SerializeField] private float _lampGlowIntensity = 2.0f;

        [Tooltip("点光源照射半径（米）。0 = 按单盏灯具尺寸自动算（约 3 米）")]
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

        [Tooltip("双开门里「可动扇」的铰链在哪一侧。勾上 = 靠门洞中间（靠近另一片），取消 = 靠外侧。")]
        [SerializeField] private bool _hingeTowardCenter = true;

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
            public string TwinId;
            public readonly List<Renderer> Renderers = new List<Renderer>(16);
            public readonly List<Material> Materials = new List<Material>(16);
            public readonly List<bool> HasEmission = new List<bool>(16);
            /// <summary>
            /// **每盏灯各一个**点光源。
            ///
            /// 【为什么不是「一个房间一个」—— 2026-10-22 用户第三次反馈「不够真实」】
            /// 上一版为了「整间教室一起亮」，把 8 盏灯合并成 1 个 10 米半径的大点光源。
            /// 结果天花板被均匀照亮成**一片死白**，没有明暗过渡 —— 用户的原话是
            /// 「之前的效果更好一些，更贴切现实的灯光效果」。
            ///
            /// 真实教室是每盏灯照亮它正下方一片，天花板上有 8 个独立的亮斑、彼此之间是暗的。
            /// 这个"暗"就是真实感的来源。所以恢复成每盏灯一个 3 米的小点光源。
            ///
            /// 【性能】68 盏灯 = 68 个点光源，但半径只有 3 米、不投影，Unity 会按距离裁剪；
            /// 而且 `LightRenderMode.Auto` 会把远处的自动降级成顶点光。实测可接受。
            /// </summary>
            public Light[] Glows;
            public bool IsOn;

            /// <summary>
            /// 是否已经**真的把状态写进材质**过一次。
            ///
            /// 【为什么需要它 —— 2026-10-22 用户实测「已关但灯还亮着」】
            /// 旧写法是 `if (IsOn == on) return;`，而 `IsOn` 默认 `false`。
            /// 于是「一进 Play 灯就是关的」这条最常见的路径，第一次 `ApplyLamp(v, false)`
            /// 直接命中早退 —— **材质一次都没被写过**，灯保持预制体自带的发光状态。
            /// 表现层看起来"接管了"，实际上什么都没做：点开能亮（状态翻转），
            /// 点关却灭不掉（还是同一个早退，只是这次 `IsOn` 已经是 true）。
            ///
            /// 教训：**「值没变」不等于「已经生效」**。默认值恰好等于目标值时，
            /// 任何「只在变化时执行」的逻辑都必须再配一个「是否已应用」的标记。
            /// 这和 `DoorVisual.HasState`（99 号清单第 20 条）是同一类问题的两个方向。
            /// </summary>
            public bool HasApplied;
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
            /// 【P19】双开门的两片门叶。`null` = 单开门（`Door07a` 那种，整个物体就是一个门扇）。
            ///
            /// 为什么需要单独一份：`Door07a` 预制体**只有一个 GameObject**，原点就在门轴上，
            /// 所以转它自己就是转门扇。而礼堂的 `DoorDouble02` 是**两片独立门叶挂在根节点下**
            /// （`DoorDouble0` / `DoorDouble`，各自有自己的门轴、而且镜像建模），
            /// 转根节点会把两片当成一块整板扫过去 —— 看起来像"整扇门被拆下来转了一圈"。
            ///
            /// 逐片转，**每片用自己的关门角**（见 `DoorLeaf.ClosedAngle`）——
            /// 不能像第一版那样两片共用一个角度、方向取反，因为这扇门的出厂姿态
            /// 是「左片关着、右片半开」，两片需要的转角根本不一样。
            /// </summary>
            public DoorLeaf[] Leaves;

            /// <summary>
            /// 是否已经读到过一次状态。
            ///
            /// 【为什么需要】`IsOpen` 默认 false，而 TickDoors 是逐帧跑的、比 0.1 秒的轮询更早。
            /// 没有这个标记的话，按 Play 的第一帧门会朝"关"的方向抽一下（约 5°）——
            /// 因为还没收到数据，它就先按"关"开始动了。
            /// </summary>
            public bool HasState;

            /// <summary>上次"到位"时记录过的目标角，避免每帧重复打日志。</summary>
            public float LoggedTarget = float.NaN;

            /// <summary>探针：还没到位时每秒记一条，用来判断「Angle 到底有没有在推进」。</summary>
            public float NextProbe;
        }

        /// <summary>双开门的一片门叶。</summary>
        private sealed class DoorLeaf
        {
            public Transform T;
            public Quaternion BaseRotation;

            /// <summary>
            /// 模型姿态下的**门扇朝向**（世界水平单位向量，指向自由边）与**门扇长度**。
            ///
            /// 在收集时一次算好，不在每帧算 —— 因为一片门扇可能由**多个网格**组成
            /// （`Door02Wood1` 是木框 `door02_1` + 玻璃 `glass`，两者共用同一个门轴），
            /// 那时候枢轴是个**没有渲染器**的父节点，没法再从它自己身上取包围盒。
            /// </summary>
            public Vector3 Panel;
            public float Length;

            /// <summary>
            /// 这片门叶「关到位」要转多少度（相对它自己的出厂姿态）。
            ///
            /// `DoorDouble02` 的两片门叶**各自有自己的门轴**（相距 1.46m），
            /// 而且出厂姿态**不是两片都关**：左片已经贴平墙面（关着），右片半开着。
            /// 所以每片算自己的：已经贴平的（|角度| &lt; 8°）冻结，只有没贴平的那片才动。
            /// </summary>
            public float ClosedAngle;

            /// <summary>这片门叶「开」的角度。出厂姿态就是开，所以是 0。</summary>
            public float OpenAngle;

            /// <summary>当前角度（逐帧朝目标推进）。</summary>
            public float Angle;

            /// <summary>
            /// `Panel` 在门叶**本地坐标系**里的方向。
            ///
            /// 【为什么需要】`Panel` 是采集那一刻的**世界**方向。门叶一旦被补了铰链枢轴、
            /// 或 `BaseRotation` 被调整过，`AngleAxis(Angle) * Panel` 就不再等于真实指向 ——
            /// 必须用 `T.rotation * PanelLocal` 才是当前真实朝向。
            /// 2026-10-26 就是拿这个错值判断了两轮，记着。
            /// </summary>
            public Vector3 PanelLocal;

            /// <summary>关门时门扇应该指向的方向（世界），探针用来直接比对。</summary>
            public Vector3 ClosedDir;

            /// <summary>冻结标记：已经贴平墙面的那片，进出都不动它。</summary>
            public bool Frozen;

            /// <summary>探针：上一次记录过的目标角，用来"只在第一次到位时打一条"。</summary>
            public float LoggedTarget = float.NaN;

            /// <summary>探针：还没到位时每秒记一条，用来判断「角度有没有在推进、卡在哪」。</summary>
            public float NextProbe;

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
                if (v.Glows != null)
                {
                    for (int j = 0; j < v.Glows.Length; j++)
                    {
                        Light g = v.Glows[j];
                        if (g != null && g.gameObject != null) Destroy(g.gameObject);
                    }
                }
            }
            _lamps.Clear();
            _doors.Clear();
            _noVisual.Clear();

            // 【P19】运行时材质副本必须自己销毁 —— 它们不在任何资产的引用图里，
            // 不 Destroy 就会在每次进出 Play 时泄漏一份（材质 + 遮罩贴图）。
            foreach (KeyValuePair<string, ScreenVisual> kv in _screens)
            {
                if (kv.Value != null && kv.Value.Material != null) Destroy(kv.Value.Material);
            }
            _screens.Clear();

            foreach (KeyValuePair<string, ProjectorVisual> kv in _projectors)
            {
                if (kv.Value != null && kv.Value.CanvasMaterial != null) Destroy(kv.Value.CanvasMaterial);
            }
            _projectors.Clear();
            _bells.Clear();

            if (_screenMask != null) { Destroy(_screenMask); _screenMask = null; }
            if (_bellChime != null) { Destroy(_bellChime); _bellChime = null; }

            if (_beep != null) Destroy(_beep);
            if (Instance == this) Instance = null;
        }

        private void Update()
        {
            // 门的转动是**逐帧动画**，不能挂在 0.1 秒的轮询里 ——
            // 10Hz 推进一次的话，1.2 秒的开门只有 12 个采样点，看着像卡了一下卡了一下。
            TickDoors(Time.deltaTime);

            // 【P26】屏幕视频：必须逐帧合成，不能挂在 0.1 秒的轮询里（会像幻灯片）
            TickScreenVideos();

            // 【P26】逐个亮：同样要逐帧推进，0.15 秒一台在 10Hz 轮询下会漏拍
            TickScreenStagger();

            // 【P26】投影跟随主脑（内部自带 0.15s 节流）
            TickProjectorFollowMaster();

            // 【P26】PPT 模式下补挂贴图目标（投影仪晚于 PPT 就绪时用）
            TickPptAttach();

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
            else if (string.Equals(model.Type, "PC", StringComparison.Ordinal))
                SyncScreen(twinId, anchor);
            else if (string.Equals(model.Type, "Projector", StringComparison.Ordinal))
                SyncProjector(twinId, anchor);
            else if (string.Equals(model.Type, "Bell", StringComparison.Ordinal))
                SyncBell(twinId, anchor);

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
            v.TwinId = anchor.TwinId;

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
            }

            v.Glows = BuildGlows(anchor, group);

            Debug.Log("[TwinPresent] 灯已接管：" + anchor.TwinId +
                      "（" + v.Renderers.Count + " 盏灯 / 发光 " + CountEmission(v) + " 盏" +
                      "，点光源=" + (v.Glows != null && v.Glows.Length > 0
                          ? "每盏一个共 " + v.Glows.Length + " 个，range=" +
                            (v.Glows[0] != null ? v.Glows[0].range.ToString("0.0") + "m" : "?")
                          : "无") + "）");

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

            // 同层判据的基准：**代表灯的挂点高度**，不是 `bounds.center.y`。
            // 用包围盒中心的话，灯的网格如果往一侧偏（吊杆、灯罩），基准会跟着漂，
            // 而 1F / 2F 的走廊 XZ 完全重合，基准一漂就会把上下两层混在一起。
            float floorY = main.transform.position.y;

            for (int i = 0; i < container.childCount; i++)
            {
                Transform child = container.GetChild(i);
                if (child == null) continue;

                Vector3 p = child.position;
                if (Mathf.Abs(p.y - floorY) > 1.6f) continue;
                if (!TwinRoomBounds.Contains(zone, p.x, p.z)) continue;

                // 每盏灯只取**它自己**体积最大的那个 Renderer（灯管），
                // 而不是把它下面所有 Renderer 都收进来。
                //
                // 【为什么】灯具预制体可能带灯架、吊杆、反光罩。把整棵子树都设成
                // 自发光，整根灯会白成一片、没有质感 —— 看起来像"灯炸了"而不是"灯亮了"。
                // 只点亮灯管，才是真实灯具的样子。现在 `LightLum01_01` 只有 1 个
                // Renderer（单网格），这条对它是无操作，但对以后换模型是必要的。
                Renderer body = LargestRenderer(child);
                if (body == null || group.Contains(body)) continue;
                group.Add(body);
            }

            return group;
        }

        /// <summary>取一个节点（含子节点）里体积最大的 Renderer —— 灯具的主体（灯管）。</summary>
        private static Renderer LargestRenderer(Transform t)
        {
            if (t == null) return null;
            Renderer[] rs = t.GetComponentsInChildren<Renderer>(true);
            Renderer best = null;
            float bestSize = -1f;
            for (int i = 0; i < rs.Length; i++)
            {
                Renderer r = rs[i];
                if (r == null) continue;
                float size = r.bounds.size.sqrMagnitude;
                if (size > bestSize) { bestSize = size; best = r; }
            }
            return best;
        }

        /// <summary>
        /// 给**每一盏**灯各建一个点光源。
        ///
        /// ── 为什么不是「一个房间一个」（2026-10-22 第三次返工）─────────────
        /// 上一版把整组灯合并成 1 个点光源、半径按整组包围盒算（13m 的教室约 10.6m）。
        /// 天花板于是被**均匀**照亮 —— 一片死白、没有明暗过渡，看起来像"发光顶棚"而不是"装了灯"。
        /// 用户的原话：「之前的效果更好一些，更贴切现实的灯光效果」。
        ///
        /// 真实教室是每盏灯照亮它正下方一片，天花板上有 N 个独立的亮斑，
        /// **亮斑之间的暗才是真实感的来源**。所以半径必须按**单盏灯**的尺寸算（约 3 米），
        /// 而且每盏灯一个光源。这是 P13 最初的做法，也是用户认可的观感。
        /// </summary>
        private Light[] BuildGlows(TwinAnchor anchor, List<Renderer> group)
        {
            if (_lampGlowIntensity <= 0.001f) return null;

            Light[] lights = new Light[group.Count];
            for (int i = 0; i < group.Count; i++)
            {
                Renderer r = group[i];
                if (r == null) continue;

                Bounds b = r.bounds;

                GameObject go = new GameObject("TwinLampGlow_" + i);
                go.transform.SetParent(anchor.transform, false);
                // 【精确还原 P13】落点就是灯管的包围盒中心，**不做任何偏移**。
                // 中间有一版我把它下移了 0.35m（想让地面照得亮些），结果灯管本身
                // 落在光源上方、接收不到光，看起来比之前暗 —— 而用户认可的正是 P13 的观感。
                go.transform.localPosition = anchor.transform.InverseTransformPoint(b.center);
                go.transform.localRotation = Quaternion.identity;

                Light light = go.AddComponent<Light>();
                light.type = LightType.Point;
                light.color = new Color(1f, 0.95f, 0.86f);

                // 【精确还原 P13】`Clamp(单盏尺寸 × 0.75, 3, 8)`，灯管约 1.6m → 3m。
                light.range = _lampGlowRange > 0.01f
                    ? _lampGlowRange
                    : Mathf.Clamp(b.size.magnitude * 0.75f, 3f, 8f);

                light.intensity = 0f;                 // 由 ApplyLamp 决定
                light.shadows = LightShadows.None;    // 一盏灯的阴影不值这个开销

                // 【为什么是 ForcePixel 而不是 Auto —— 2026-10-22 第五轮】
                // 场景里本来就有 69 盏 15~25 米的大点光源。加上我们的 8 个，
                // 这个区域同时被 70 多个光源影响。Built-in 前向渲染的逐像素灯上限
                // 默认只有 4 盏，超出的会被降级成**顶点光** —— 而顶点光打在天花板
                // 这种只有两个三角面的大网格上，插值出来几乎看不出亮度。
                //
                // 症状：Console 里 `点光源=8 个 × 2.0` 一切正常，画面上灯管雪白，
                // 但**灯管周围的天花板一点都没亮**。参数全对、日志全对、就是不亮。
                // Auto 是按"重要性"排序的，3 米的小光源在 20 米的大光源面前排不上号。
                light.renderMode = LightRenderMode.ForcePixel;
                lights[i] = light;
            }
            return lights;
        }

        private void ApplyLamp(LampVisual v, bool on)
        {
            // 【必须判 HasApplied，不能只判 IsOn】见 `LampVisual.HasApplied` 的注释：
            // 「第一次 Sync 时灯就是关的」会让 IsOn==on 成立 → 早退 → 材质从没被写过。
            if (v.HasApplied && v.IsOn == on) return;

            v.HasApplied = true;
            v.IsOn = on;

            int written = 0;
            for (int i = 0; i < v.Materials.Count; i++)
            {
                Material m = v.Materials[i];
                if (m == null || !v.HasEmission[i]) continue;
                m.SetColor(EmissionId, on ? _lampEmission : Color.black);
                written++;
            }

            if (v.Glows != null)
            {
                for (int i = 0; i < v.Glows.Length; i++)
                {
                    if (v.Glows[i] != null) v.Glows[i].intensity = on ? _lampGlowIntensity : 0f;
                }
            }

            // 只在「状态真的翻转」或「首次接管」时打一条，方便验收时确认表现层确实动手了。
            // 这条一定要无条件打：状态变了却 `written == 0`（材质没有 _EmissionColor）时，
            // 它是唯一能看出「灯为什么不亮」的线索。
            Debug.Log("[TwinPresent] 灯 " + (on ? "开" : "关") + "：" + v.TwinId +
                      "（" + v.Renderers.Count + " 盏，写了 " + written + " 份自发光" +
                      "，点光源=" + (v.Glows != null ? v.Glows.Length + " 个 × " +
                                     (on ? _lampGlowIntensity.ToString("0.0") : "0.0") : "无") + "）");
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

            bool wasOpen = v.HasState && v.IsOpen;
            bool isOpenNow = open.Value >= 0.5f;
            bool changed = !v.HasState || wasOpen != isOpenNow;

            v.IsOpen = isOpenNow;
            v.HasState = true;

#if UNITY_EDITOR
            // 【临时诊断】门的表现层出问题时，靠截图猜方向效率极低。
            // 这里在**每次状态变化**时把每片门叶的角度写进 p19_door_runtime.txt，
            // 一行就能看出是"没转"还是"转错方向"。查完删掉这一段即可。
            if (changed) DoorTrace(twinId, isOpenNow ? "状态→开" : "状态→关", v);
#endif
        }

        /// <summary>
        /// 【临时诊断，2026-10-22】把一扇门的运行时状态写进 `p19_door_runtime.txt`（追加）。
        ///
        /// 【为什么需要它】礼堂双开门「点关门不生效」查了三轮，每轮都在靠截图猜：
        /// 截图分不清「门叶没转」和「转了但目标位置本来就不对」。这个函数把
        /// **此刻每片门叶的真实角度、目标角度、是否冻结、本地位置与缩放**一次写清楚，
        /// 一眼就能分辨是哪一种。查完删掉这一段和 `SyncDoor` 里的调用即可。
        /// </summary>
        private static void DoorTrace(string twinId, string tag, DoorVisual v)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder(512);
            sb.Append(System.DateTime.Now.ToString("HH:mm:ss.fff")).Append(" [").Append(tag).Append("] ")
              .Append(twinId)
              .Append("  IsOpen=").Append(v.IsOpen ? 1 : 0)
              .Append("  HasState=").Append(v.HasState ? 1 : 0)
              .Append("  Leaves=").Append(v.Leaves == null ? "null" : v.Leaves.Length + "片")
              .Append("  根: 关=").Append(v.ClosedAngle.ToString("0.0"))
              .Append("° 开=").Append(v.OpenAngle.ToString("0.0"))
              .Append("° 当前=").Append(v.Angle.ToString("0.0")).Append("°");

            if (v.Leaves != null)
            {
                for (int i = 0; i < v.Leaves.Length; i++)
                {
                    DoorLeaf l = v.Leaves[i];
                    if (l == null) { sb.Append(" | 片").Append(i).Append("=null"); continue; }
                    sb.Append(" | 片").Append(i).Append("「").Append(l.T != null ? l.T.name : "null").Append("」")
                      .Append(" 冻结=").Append(l.Frozen ? 1 : 0)
                      .Append(" 关=").Append(l.ClosedAngle.ToString("0.0"))
                      .Append("° 开=").Append(l.OpenAngle.ToString("0.0"))
                      .Append("° 当前=").Append(l.Angle.ToString("0.0")).Append("°");
                    if (l.T != null)
                    {
                        sb.Append(" 本地pos=").Append(Fmt(l.T.localPosition))
                          .Append(" 本地scale=").Append(Fmt(l.T.localScale))
                          .Append(" 世界pos=").Append(Fmt(l.T.position));
                    }
                }
            }

            string line = sb.ToString();
            Debug.Log(line);
            try { System.IO.File.AppendAllText("p19_door_runtime.txt", line + "\n"); }
            catch (Exception) { /* 诊断写不进去不该影响表现层 */ }
        }

        private static string Fmt(Vector3 v)
        {
            return "(" + v.x.ToString("0.000") + "," + v.y.ToString("0.000") + "," + v.z.ToString("0.000") + ")";
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

                // 【双开门】每片门叶各走各的角度，根节点不动（见 DoorLeaf 的注释）
                if (v.Leaves != null)
                {
                    for (int i = 0; i < v.Leaves.Length; i++)
                    {
                        DoorLeaf leaf = v.Leaves[i];
                        if (leaf == null || leaf.T == null || leaf.Frozen) continue;

                        float leafTarget = v.IsOpen ? leaf.OpenAngle : leaf.ClosedAngle;
                        float leafSpan = Mathf.Abs(leaf.OpenAngle - leaf.ClosedAngle);
                        float leafSpeed = leafSpan / Mathf.Max(0.2f, _doorSwingSeconds);

                        float leafNext = Mathf.MoveTowards(leaf.Angle, leafTarget, leafSpeed * step);
                        if (Mathf.Abs(leafNext - leaf.Angle) >= 0.001f)
                        {
                            leaf.Angle = leafNext;
                            leaf.T.rotation = Quaternion.AngleAxis(leaf.Angle, Vector3.up) * leaf.BaseRotation;
                        }

                        // 探针：卡住时每秒一条，到位那一刻一条。
                        // 「关门没关上」这类问题只有靠"实际角度 + 停住时门扇指向"才能定位 ——
                        // 只看截图分不清是"没转够"还是"转够了但方向不对"。
                        if (Mathf.Abs(leaf.Angle - leafTarget) > 0.5f)
                        {
                            if (Time.unscaledTime >= leaf.NextProbe)
                            {
                                leaf.NextProbe = Time.unscaledTime + 1f;
                                Debug.Log("[TwinDoor] " + kv.Key + " 片" + i + " 未到位 目标=" +
                                          leafTarget.ToString("0.0") + "° 当前=" +
                                          leaf.Angle.ToString("0.0") + "° 速度=" +
                                          leafSpeed.ToString("0.0") + "°/s");
                            }
                        }
                        else if (!Mathf.Approximately(leaf.LoggedTarget, leafTarget))
                        {
                            leaf.LoggedTarget = leafTarget;
                            // 真实朝向 = 当前旋转 × 本地方向。
                            // **不能**用 `AngleAxis(Angle) * Panel` —— Panel 是镜像前的世界方向，
                            // 镜像过 BaseRotation 之后那个算法是错的（2026-10-26 踩过）。
                            Vector3 nowPanel = leaf.T.rotation * leaf.PanelLocal;
                            float err = Vector3.Angle(nowPanel, leaf.ClosedDir);
                            Debug.Log("[TwinDoor] " + kv.Key + " 片" + i + " 到位 角=" +
                                      leafTarget.ToString("0.0") + "° 门扇方向=" + Fmt(nowPanel) +
                                      " 关门目标方向=" + Fmt(leaf.ClosedDir) +
                                      " 与目标夹角=" + err.ToString("0.0") + "°");
                        }
                    }
                    continue;   // 根节点不参与
                }

                float target = v.IsOpen ? v.OpenAngle : v.ClosedAngle;
                float span = Mathf.Abs(v.OpenAngle - v.ClosedAngle);
                // 0.2s 是 [Range] 的下限：序列化里没有这个键时（旧场景资产），
                // Unity 会用字段初始值 1.2；万一拿到 0 也不会变成"瞬间闪一下"。
                float speed = span / Mathf.Max(0.2f, _doorSwingSeconds);

                // 探针：没到位就每秒记一条 —— 这是判断「Angle 有没有在推进」的唯一办法。
                // 一直不动 = TickDoors 没跑到这里 / target 不对；慢慢挪 = 速度算错。
                if (Mathf.Abs(v.Angle - target) > 0.5f && Time.unscaledTime >= v.NextProbe)
                {
                    v.NextProbe = Time.unscaledTime + 1f;
                    DoorTrace(kv.Key, "探针(未到位) 目标=" + target.ToString("0.0") + "° 速度=" +
                              speed.ToString("0.0") + "°/s 步长=" + step.ToString("0.000"), v);
                }

                float next = Mathf.MoveTowards(v.Angle, target, speed * step);
                if (Mathf.Abs(next - v.Angle) < 0.001f) continue;

                v.Angle = next;
                v.T.rotation = Quaternion.AngleAxis(v.Angle, Vector3.up) * v.BaseRotation;

                // 到位了就把「门边落在哪」打出来 —— 这是唯一客观的判据：
                // 和门洞的实际范围对一下就知道方向对不对，不用再看截图猜。
                if (Mathf.Abs(next - target) < 0.01f && !Mathf.Approximately(v.LoggedTarget, target))
                {
                    v.LoggedTarget = target;
                    Vector3 edge = v.T.position + v.T.right * DoorLeafLength;
                    Debug.Log("[TwinDoor] " + kv.Key + " 到位 角=" + target.ToString("0.0") + "°" +
                              "  门轴=" + Fmt(v.T.position) + "  门边=" + Fmt(edge) +
                              "  门扇方向=" + Fmt(v.T.right));
                }
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
            string closeReason;
            Vector3 closedDir = PickClosedDir(anchor, t.position, panel, wallDir, out closeReason);

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
            // 【双开门】根节点**不转**，只有没贴平墙面的那片门叶转（见 CollectDoorLeaves）。
            // 所以根节点自己的两个角度都置 0，避免 TickDoors 里两套逻辑打架。
            v.Leaves = CollectDoorLeaves(t, wallDir, _hingeTowardCenter);
            if (v.Leaves != null)
            {
                v.ClosedAngle = 0f;
                v.OpenAngle = 0f;
            }

#if UNITY_EDITOR
            DoorTrace(anchor.TwinId, "构建完成", v);
#endif

            Debug.Log("[TwinPresent] 门已接管：" + anchor.TwinId + "（" + t.name + "）" +
                      " 墙向=" + (wallAlongX ? "X" : "Z") +
                      (v.Leaves != null
                          ? " 双开门 " + v.Leaves.Length + " 片门叶（根节点不转，逐片转；详见上面的门叶日志）"
                          : " 关门方向=" + (Vector3.Dot(closedDir, wallDir) > 0f ? "+" : "-") + wallDir.ToString() +
                            "（" + closeReason + "）" +
                            " 关门角=" + closedAngle.ToString("0.0") + "°" +
                            " 开门角=" + openAngle.ToString("0.0") + "°" +
                            "（角度相对模型当前姿态，0° = 原样）"));

            return v;
        }

        /// <summary>
        /// 判断这是不是一扇双开门，是的话把两片门叶收集出来，并**逐片**算出关门角。
        ///
        /// 判据：根节点下**带渲染器的直接子物体 ≥ 2 个**。
        /// `Door07a` 预制体只有 1 个 GameObject（门扇就是它自己），所以返回 null；
        /// `DoorDouble02` 根下有 `DoorDouble0` 和 `DoorDouble` 两片，返回 2 片。
        ///
        /// 【2026-10-22 第二版：一片一个角度】
        /// 第一版按「靠前的一片转 +、靠后的一片转 -」共用一个关门角，实测是错的 ——
        /// 因为这扇门的出厂姿态是「左片关着、右片半开」，不是「两片都关」。
        /// 现在每片自己算：谁已经贴平墙面谁冻结，谁没贴平谁动。
        /// </summary>
        private static DoorLeaf[] CollectDoorLeaves(Transform root, Vector3 wallDir,
                                                    bool hingeTowardCenter)
        {
            if (root == null) return null;

            // 根自己带渲染器 = 门扇就是它自己（`Door07a` 那 12 扇），走根节点那条路。
            if (root.GetComponent<Renderer>() != null) return null;

            List<List<Renderer>> groups;
            List<DoorLeaf> parts = CollectLeafParts(root, out groups);
            if (parts == null || parts.Count == 0) return null;

            // ⚠️ 这里**不能**对 parts 排序：groups 是按同一顺序建的，
            //    只排 parts 会让 parts[i] 和 groups[i] 对不上 —— 左扇拿到右扇的包围盒，
            //    于是「本该冻结的那片在转、本该转的那片冻结」。
            //    2026-10-23 实测踩过：礼堂双开门 片0/DoorDouble0 算出 28.9°、片1/DoorDouble 算出 0°。
            //    顺序 = 层级顺序，本来就稳定，不需要再排。
            DoorLeaf[] leaves = new DoorLeaf[parts.Count];
            for (int i = 0; i < parts.Count; i++)
            {
                DoorLeaf leaf = parts[i];
                leaf.BaseRotation = leaf.T.rotation;

                Vector3 panel;
                float length;
                if (!LeafPanelFromRenderers(leaf.T, groups[i], out panel, out length))
                {
                    // 退化情况：拿不到包围盒就用枢轴自己的 right，至少别让整扇门不动
                    panel = leaf.T.right; panel.y = 0f;
                    panel = panel.sqrMagnitude > 0.0001f ? panel.normalized : Vector3.forward;
                    length = DoorLeafLength;
                }
                leaf.Panel = panel;
                leaf.Length = length;
                // 世界方向 → 门叶本地方向。后面镜像 BaseRotation 之后，
                // 只有 T.rotation * PanelLocal 才是真实朝向（见 PanelLocal 的注释）
                leaf.PanelLocal = Quaternion.Inverse(leaf.BaseRotation) * panel;

                // 两个「贴平墙面」的方向，正常情况下取离当前姿态更近的那个 ——
                // 半开的门离「关」一定比离「反方向」近，所以这就是"顺手关上"的那个方向。
                // 注意：这个判据只依赖 |角度|，和门扇朝向的正负无关，所以镜像建模也不会翻。
                //
                // ⚠️ 但「最近」不等于「对」：2026-10-23 体育馆入口那扇门开到 113°，
                //    最近的方向是往门框**左边**的墙贴 —— 结果整扇门转进墙里。
                //    所以只要能找到**门框**，就改成"往门框中心关"，那才是门洞在哪儿的证据。
                float angA = Vector3.SignedAngle(panel, wallDir, Vector3.up);
                float angB = Vector3.SignedAngle(panel, -wallDir, Vector3.up);

                Vector3 frameCenter;
                string rule;
                Vector3 closedDir;
                if (TryFindFrameCenter(leaf.T, wallDir, out frameCenter))
                {
                    closedDir = Vector3.Dot(frameCenter - leaf.T.position, wallDir) >= 0f ? wallDir : -wallDir;
                    rule = "往门框中心关（门框 " + Fmt(frameCenter) + "）";
                }
                else
                {
                    closedDir = Mathf.Abs(angA) <= Mathf.Abs(angB) ? wallDir : -wallDir;
                    rule = "贴墙较近的那一侧（没找到门框）";
                }
                float closedAngle = Vector3.SignedAngle(panel, closedDir, Vector3.up);
                leaf.ClosedDir = closedDir;   // 探针用：关门时门扇应该指向这里

                // 已经贴平墙面（< 8°）→ 这片本来就是关的，冻结它，进出都不动。
                leaf.Frozen = Mathf.Abs(closedAngle) < 8f;
                leaf.ClosedAngle = leaf.Frozen ? 0f : closedAngle;
                leaf.Angle = 0f;

                // 「开」= **模型原姿态**（OpenAngle = 0），不镜像。
                //
                // 【为什么】2026-10-23 用户拿两张图对比：正确的门（体育馆入口）开的时候
                // 门扇在屋里、**把手那一面朝走廊**；而"镜像到走廊侧"的开法看到的是没把手的那面，
                // 看上去就像"门装反了"。门开向哪一侧，直接决定从走廊看到的是哪一面。
                // 所以：门扇往屋里开 → 走廊看到把手面 ✓（这也正是模型出厂姿态）。
                leaf.OpenAngle = 0f;

                Debug.Log("[TwinPresent] 门叶「" + leaf.T.name + "」门扇=" + Fmt(panel) +
                          " 长=" + length.ToString("0.000") + "m（" + groups[i].Count + " 个网格）" +
                          " 关门角=" + closedAngle.ToString("0.0") + "°（" + rule + "）" +
                          (leaf.Frozen ? " → 已贴平墙面，冻结（不参与动画）" : " → 参与动画"));

                leaves[i] = leaf;
            }

            // 防呆：数组里不许有 null。
            // 「漏赋值」这种 bug 的表现是**完全静默**的 —— 日志正常、角度正常、一帧都不转。
            for (int i = 0; i < leaves.Length; i++)
            {
                if (leaves[i] != null) continue;
                Debug.LogError("[TwinPresent] 门「" + root.name + "」第 " + i +
                               " 片门叶是 null —— CollectDoorLeaves 漏了赋值，动画不会生效。");
                return null;
            }

            // ── 第二遍：双开门补「铰链枢轴」──────────────────────────────────
            //
            // 【为什么必须补】`DoorDouble.FBX` 的门叶网格是**以自身原点为中心**建模的
            // （实测：X 跨度 -423.7 ~ +416.1mm，中心 -3.8mm ≈ 0，即原点在门叶正中间）。
            // 所以直接绕 transform 原点转 = **绕门叶中心转**，视觉上就是
            // 「轴心跑到门叶中间/另一侧去了」——2026-10-26 用户实测反馈。
            //
            // 对比：`Door07a`（教室/餐厅门）根节点本身就在铰链上，所以它一直是对的。
            //
            // 门叶没有子物体可以借位（`m_Children: []`，网格直接挂在门叶上），
            // 所以只能在门轴上插一个空枢轴：枢轴放在**铰链边**，门叶挂到枢轴下。
            // `SetParent(pivot, true)` 保持世界姿态 → 网格原地不动，静止姿态一点不变。
            //
            // 铰链在哪一侧：**离门洞中点最近的那条竖边**。
            //
            // 2026-10-26 用户指图确认：把手在外侧 → 外侧是自由边 → 铰链在内侧。
            // 这扇"双开门"实际是「一片固定 + 一片可动」：
            //   `DoorDouble0` 贴平墙面（closedAngle≈0）→ 被判定 Frozen，等于固定扇；
            //   `DoorDouble` 才是真正开关的那片，铰链在靠固定扇的一侧。
            //
            // ⚠️ 不能按「原点偏移 off」判断要不要补枢轴：实测 `DoorDouble` 的原点
            //    也落在某条边上（off > 0.05），但落在**自由边**上 —— 绕它转就是绕自由边转。
            //    所以必须显式比较两条边离门洞中点的远近，再决定枢轴放哪。
            if (leaves.Length >= 2)
            {
                Vector3 mid = Vector3.zero;
                for (int i = 0; i < leaves.Length; i++) mid += leaves[i].T.position;
                mid /= leaves.Length;
                mid.y = 0f;

                // 无条件打一条：这段代码到底有没有跑到，只能靠日志证明
                Debug.Log("[TwinPresent] 双开门「" + root.name + "」铰链检查开始：片数=" +
                          leaves.Length + " 门洞中点=" + Fmt(mid) +
                          " 铰链朝=" + (hingeTowardCenter ? "门洞中间" : "外侧"));

                for (int i = 0; i < leaves.Length; i++)
                {
                    DoorLeaf leaf = leaves[i];
                    if (leaf.T == null) continue;

                    if (leaf.Frozen)
                    {
                        Debug.Log("[TwinPresent] 门叶「" + leaf.T.name + "」已贴平墙面（固定扇），不补枢轴");
                        continue;
                    }

                    // 已经补过就别再来一次（翻倍会把枢轴越推越远）
                    if (leaf.T.parent != null &&
                        leaf.T.parent.name.EndsWith("_HingePivot", StringComparison.Ordinal))
                    {
                        Debug.Log("[TwinPresent] 门叶「" + leaf.T.name + "」已有铰链枢轴，跳过");
                        continue;
                    }

                    // ── 先算出这片门叶的**两条竖边**（世界坐标）─────────────
                    // 门叶原点是落在网格中心还是落在某条边上，两种都要能处理：
                    //   · off ≈ 0（原点在网格中心）→ 两条边各在 ±panel × 半宽
                    //   · off > 0（原点已经在某条边上）→ 一条边就是原点，另一条在 panel × length
                    // 注意 `length` 的定义（见 LeafPanelFromRenderers）：
                    //   length = 2 × |网格中心 − 原点|，所以原点在边上时 length 正好是整片宽度。
                    float off = LeafCenterOffset(leaf.T, groups[i]);
                    Vector3 e0, e1;
                    if (off < 0.05f)
                    {
                        float halfW = Mathf.Max(0.05f, leaf.Length * 0.5f);
                        e0 = leaf.T.position - leaf.Panel * halfW;
                        e1 = leaf.T.position + leaf.Panel * halfW;
                    }
                    else
                    {
                        e0 = leaf.T.position;
                        e1 = leaf.T.position + leaf.Panel * Mathf.Max(0.05f, leaf.Length);
                    }

                    // ── 铰链边 = 离门洞中点**最近**的那条 ──────────────────
                    // 依据：这扇"双开门"是「一片固定 + 一片可动」——
                    //   `DoorDouble0` 贴平墙面被冻结 = 固定扇；
                    //   可动扇的铰链就在靠固定扇的一侧（门洞中间），把手在外侧。
                    // 常规双开门（铰链在相对外侧）把 `_hingeTowardCenter` 取消勾选即可。
                    bool e0Near = (e0 - mid).sqrMagnitude <= (e1 - mid).sqrMagnitude;
                    bool pickE0 = hingeTowardCenter ? e0Near : !e0Near;
                    Vector3 hingePos = pickE0 ? e0 : e1;

                    Debug.Log("[TwinPresent] 门叶「" + leaf.T.name + "」原点偏移=" + off.ToString("0.000") +
                              "m 边e0=" + Fmt(e0) + " 边e1=" + Fmt(e1) +
                              " → 铰链取 " + (pickE0 ? "e0" : "e1") + " = " + Fmt(hingePos));

                    // 原点本来就在铰链边上 → 不用补枢轴（但下面"镜像开门方向"照样要做）
                    Vector3 shift = hingePos - leaf.T.position;
                    shift.y = 0f;
                    bool needPivot = shift.magnitude >= 0.05f;

                    if (needPivot)
                    {
                        GameObject pivotGo = new GameObject(leaf.T.name + "_HingePivot");
                        Transform pivot = pivotGo.transform;
                        pivot.SetPositionAndRotation(hingePos, leaf.T.rotation);
                        pivot.SetParent(leaf.T.parent, true);

                        leaf.T.SetParent(pivot, true);   // worldPositionStays=true → 网格原地不动

                        // BaseRotation 不用改：枢轴的世界旋转 == 门叶原来的世界旋转。
                        // ClosedAngle 是**相对角度**，绕哪个点转不影响它，也不用改。
                        leaf.T = pivot;
                    }

                    // ── 「开」= **模型原始姿态**，一点不动 ──────────────────────
                    // 2026-10-26 用户明确要求：门默认就是模型建好的样子（两片朝走廊开着），
                    // 这就是"开"；关门才是从原样转回贴平。所以这里不做任何额外旋转，
                    // 也不去"重算一个更好看的开角"——那会把模型原样改掉。
                    leaf.OpenAngle = 0f;
                    leaf.Angle = 0f;
                    // 枢轴插进来时世界姿态已经保持住了，这里不用再写 rotation；
                    // TickDoors 对"已在目标值"的片也不写 Transform → 画面就是模型原样。

                    // 【2026-10-26 用户实测，第三次修正】
                    //   · 按 `SignedAngle` 算出的 −57.6° 关门 → 门叶和墙面重合（转进墙里）
                    //   · 翻成正方向后用户确认「方向对了，但还差 60° 左右」
                    // 说明 `PickClosedDir` 判到的**贴平侧是反的** —— 真正的贴平是另一个平行方向，
                    // 两者相差 180°，所以正确的关门角 = 180° − |关门角| = 122.4°。
                    // 交叉验证：122.4 − 57.6 = 64.8°，与用户目测的"60° 以内"吻合。
                    float closeMag = Mathf.Abs(leaf.ClosedAngle);
                    if (closeMag > 0.01f)
                    {
                        leaf.ClosedAngle = 180f - closeMag;      // 正方向（用户已确认）
                        leaf.ClosedDir = -leaf.ClosedDir;        // 探针改比对真正的贴平方向
                    }

                    Debug.Log("[TwinPresent] 门叶「" + leaf.T.name + "」" +
                              (needPivot
                                  ? "已补铰链枢轴 " + Fmt(hingePos) +
                                    "（原点偏移 " + off.ToString("0.000") + "m，移动 " +
                                    shift.magnitude.ToString("0.000") + "m）"
                                  : "原点已在铰链边，不补枢轴") +
                              "；开=模型原样(0°) 关门角=" + leaf.ClosedAngle.ToString("0.0") + "°" +
                              " 铰链=" + (pickE0 ? "e0" : "e1") +
                              "（" + (hingeTowardCenter ? "朝门洞中间" : "朝外侧") + "）");
                }
            }

            return leaves;
        }

        /// <summary>门叶原点到网格包围盒中心的水平距离。≈0 表示原点在门叶正中间（不是铰链边）。</summary>
        private static float LeafCenterOffset(Transform pivot, List<Renderer> renderers)
        {
            if (pivot == null || renderers == null || renderers.Count == 0) return float.MaxValue;

            bool has = false;
            Bounds b = new Bounds(pivot.position, Vector3.zero);
            for (int i = 0; i < renderers.Count; i++)
            {
                Renderer r = renderers[i];
                if (r == null) continue;
                if (!has) { b = r.bounds; has = true; }
                else b.Encapsulate(r.bounds);
            }
            if (!has) return float.MaxValue;

            Vector3 d = b.center - pivot.position;
            d.y = 0f;
            return d.magnitude;
        }

        /// <summary>
        /// 把「容器根节点」下面的东西整理成**一片片门扇**。
        ///
        /// 三种结构都要能处理：
        ///   · `DoorDouble02` → 根下两片**各自带渲染器**的门叶，枢轴各不相同 → 2 片
        ///   · `Door02Wood1`  → 根 → `DoorAnchor`（空壳）→ `glass` + `door02_1`
        ///                      **两个网格共用同一个门轴** → 它们是同一片门扇的零件（玻璃 + 木框）→ 1 片
        ///   · 其它            → 找不到带渲染器的东西就返回空
        ///
        /// 【2026-10-23 踩过】第一版把「两个带渲染器的子物体」一律当成两片门叶，
        /// 于是 `Door02Wood1` 的玻璃和木框各转各的 —— 玻璃直接从门里飞出去，
        /// 用户看到的就是"玻璃和门不是一体的，而且方向反了"。
        /// **位置相同 = 同一片门扇**，枢轴要用它们的共同父节点。
        /// </summary>
        private static List<DoorLeaf> CollectLeafParts(Transform root, out List<List<Renderer>> partRenderers)
        {
            partRenderers = null;

            List<Transform> visual = new List<Transform>(4);
            CollectVisualLeaves(root, visual, 2);
            if (visual.Count == 0) return null;

            List<DoorLeaf> leaves = new List<DoorLeaf>(2);
            List<List<Renderer>> groups = new List<List<Renderer>>(2);

            for (int i = 0; i < visual.Count; i++)
            {
                Transform part = visual[i];
                Renderer[] rs = part.GetComponentsInChildren<Renderer>(true);
                if (rs == null || rs.Length == 0) continue;

                // 找已经建好的、和它**同一个枢轴**的那一片
                DoorLeaf host = null;
                int hostIndex = -1;
                for (int j = 0; j < leaves.Count; j++)
                {
                    if (Vector3.Distance(leaves[j].T.position, part.position) < 0.05f)
                    {
                        host = leaves[j];
                        hostIndex = j;
                        break;
                    }
                }

                if (host == null)
                {
                    host = new DoorLeaf();
                    host.T = part;
                    leaves.Add(host);
                    groups.Add(new List<Renderer>(4));
                    hostIndex = leaves.Count - 1;
                }
                else
                {
                    // 同一枢轴上的第二个零件 → 改用它们的**共同父节点**当枢轴，
                    // 这样才能两个网格一起转（用零件自己当枢轴的话只能转一个）
                    Transform parent = host.T.parent;
                    if (parent != null && parent != root) host.T = parent;
                }

                List<Renderer> bucket = groups[hostIndex];
                for (int r = 0; r < rs.Length; r++)
                    if (rs[r] != null && !bucket.Contains(rs[r])) bucket.Add(rs[r]);
            }

            if (leaves.Count == 0) return null;
            partRenderers = groups;
            return leaves;
        }

        /// <summary>
        /// 往下找「**自己有渲染器**」的子物体。最多下探 <paramref name="depth"/> 层。
        ///
        /// 不能只看直接子物体：`Door02Wood1` 的门扇在**孙子**节点上
        /// （根 → `DoorAnchor` → `door02_1`），只收直接子物体只会拿到那个空壳。
        /// </summary>
        private static void CollectVisualLeaves(Transform parent, List<Transform> into, int depth)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform c = parent.GetChild(i);
                if (c == null) continue;
                if (c.GetComponent<Renderer>() != null) { into.Add(c); continue; }
                if (depth > 0) CollectVisualLeaves(c, into, depth - 1);
            }
        }

        /// <summary>
        /// 找这扇门**门框**的中心（水平面），用来决定「关门往哪边转」。
        ///
        /// 【为什么必须找门框，不能只看"哪边更近"】门扇开到 113° 的时候，
        /// 「最近贴墙方向」是门框**左边**的那段墙 —— 往那边转，整扇门就转进墙里了
        /// （2026-10-23 体育馆入口门实测）。门洞在哪儿，只有门框知道。
        ///
        /// 判据：名字里带 `frame` 或 `Post` 的渲染器，距离 0.05~4m，
        /// 且方向大致**沿着墙**（`|dot(方向, 墙向)| > 0.5`）—— 门框的两个边柱可能在侧面，
        /// 所以取其中最近的那个。
        /// </summary>
        private static bool TryFindFrameCenter(Transform pivot, Vector3 wallDir, out Vector3 center)
        {
            center = pivot.position;
            if (pivot == null) return false;

            Vector3 p0 = pivot.position;
            Renderer[] all = UnityEngine.Object.FindObjectsOfType<Renderer>(true);
            float best = 4.0f;
            bool found = false;

            for (int i = 0; i < all.Length; i++)
            {
                Renderer r = all[i];
                if (r == null || r.gameObject == null) continue;

                string n = r.gameObject.name;
                if (n.IndexOf("frame", StringComparison.OrdinalIgnoreCase) < 0 &&
                    n.IndexOf("Post", StringComparison.OrdinalIgnoreCase) < 0) continue;

                Vector3 c = r.bounds.center;
                c.y = p0.y;
                Vector3 d = c - p0;
                d.y = 0f;
                float dist = d.magnitude;
                if (dist < 0.05f || dist >= best) continue;
                if (Mathf.Abs(Vector3.Dot(d / dist, wallDir)) < 0.5f) continue;

                best = dist;
                center = c;
                found = true;
            }
            return found;
        }

        /// <summary>
        /// 由一组渲染器的**合并包围盒**算出门扇朝向与长度。
        ///
        /// 门叶的枢轴在门轴上、网格从枢轴往自由边长，所以「包围盒中心 − 枢轴」
        /// 就是半个门扇方向，乘 2 就是门扇长度。
        /// 这是模型自己的几何事实：不依赖 BoxCollider（`Door02Wood1` 只有 MeshCollider），
        /// 也不依赖 `transform.right`（镜像建模的叶子会算反）。
        /// </summary>
        private static bool LeafPanelFromRenderers(Transform pivot, List<Renderer> renderers,
                                                  out Vector3 panel, out float length)
        {
            panel = Vector3.zero;
            length = 0f;
            if (pivot == null || renderers == null || renderers.Count == 0) return false;

            bool has = false;
            Bounds b = new Bounds(pivot.position, Vector3.zero);
            for (int i = 0; i < renderers.Count; i++)
            {
                Renderer r = renderers[i];
                if (r == null) continue;
                if (!has) { b = r.bounds; has = true; }
                else b.Encapsulate(r.bounds);
            }
            if (!has) return false;

            Vector3 d = b.center - pivot.position;
            d.y = 0f;
            if (d.sqrMagnitude < 0.0001f) return false;

            panel = d.normalized;
            length = d.magnitude * 2f;
            return true;
        }
        /// <summary>
        /// Door07a 的门扇长度：它自带的 BoxCollider 是 `x∈[-0.006, 0.835]`。
        /// 「门关到位时门边落在哪」要用到它。
        /// </summary>
        private const float DoorLeafLength = 0.835f;

        /// <summary>
        /// 门关上时门扇贴平墙面 —— 但「贴哪一侧」有两个答案，选错了门会转到门洞外面，
        /// 看起来就像**根本没关**（门扇贴在门洞旁边的墙上）。
        ///
        /// 2026-10-22 用户实测就是这个问题：「门关反了，是往教室里面关，才是关门」。
        /// 当时用的是「取与门扇当前方向同侧的那个」，它把 2-1班 的门判到了 +X 侧，
        /// 门边落点 x=-11.73 —— 房间东界是 -12.4，**门扇整个跑到隔壁去了**。
        ///
        /// ── 判据（依次降级）──────────────────────────────────────────
        ///   ① **门边落点落在房间的「沿墙跨度」内** → 取那一侧。
        ///      门洞开在房间自己的那段墙上，所以门扇收回去必然还在这段墙的跨度里；
        ///      转到另一侧，门边就跑到房间外（隔壁 / 墙外）去了。
        ///   ② 两侧都在 / 都不在跨度内 → 取**朝向房间中心**的那一侧。
        ///   ③ 连房间范围都没登记 → 退回「与门扇当前方向同侧」（旧行为）。
        ///
        /// ── 为什么只比「沿墙那根轴」，不比整个矩形 ────────────────────
        /// 门扇关到位时是**躺在墙里**的，垂直方向本来就在房间范围之外
        /// （2-1班：门枢轴 z=33.49，房间 z 从 34.5 起，差 1.01m 就是墙厚）。
        /// 拿整个矩形去比，两侧都判「不在房间内」，①永远不成立 —— 这条规则就白写了。
        ///
        /// 为什么不能只看 ③：它只保证「转得少」，不保证「转到门洞上」。
        /// </summary>
        private Vector3 PickClosedDir(TwinAnchor anchor, Vector3 hinge, Vector3 panel,
                                      Vector3 wallDir, out string reason)
        {
            Vector3 a = wallDir;
            Vector3 b = -wallDir;
            bool alongX = Mathf.Abs(wallDir.x) > Mathf.Abs(wallDir.z);

            // 先给默认值：`&&` 短路时 `TryGetBounds` 根本不会被调用，
            // 它的 out 参数就没被赋值 —— C# 会直接报 CS0165（"使用了未赋值的局部变量"）。
            // 这跟踩坑清单第 15 条是同一类问题。
            float bx0 = 0f, bx1 = 0f, bz0 = 0f, bz1 = 0f;
            bool hasBox = false;
            if (!string.IsNullOrEmpty(anchor.Zone))
            {
                hasBox = TwinRoomBounds.TryGetBounds(anchor.Zone, out bx0, out bx1, out bz0, out bz1);
            }

            // 【跨度判据是主判据】门洞在哪一侧，只有"墙在哪里断开"知道。
            // 2026-10-23 用烘焙的 2F 平面图放大 14 倍量过 Class2-1：
            //   墙在 x -13.39 处断开、到 -12.47 重新接上 → 门洞在门轴（-12.57）的**西侧**
            //   → 关门必须朝 -X 转 115.6°。一度改成"最短路径"（-64.4°）就把门转进东侧的墙里，
            //   门直接"消失"了。
            // 所以：跨度判据优先；它判不出来（两侧都在/都不在跨度内）才退回最短路径。
            if (hasBox)
            {
                // 只看沿墙那根轴：门扇躺回墙里之后，它必须还在这段墙的跨度内
                float ea = alongX ? hinge.x + DoorLeafLength : hinge.z + DoorLeafLength;
                float eb = alongX ? hinge.x - DoorLeafLength : hinge.z - DoorLeafLength;
                bool inA = alongX ? (ea >= bx0 && ea <= bx1) : (ea >= bz0 && ea <= bz1);
                bool inB = alongX ? (eb >= bx0 && eb <= bx1) : (eb >= bz0 && eb <= bz1);

                if (inA != inB)
                {
                    reason = "门边落点只有 " + (inA ? "+" : "-") + (alongX ? "X" : "Z") + " 侧还在这段墙的跨度内";
                    return inA ? a : b;
                }

                Vector3 center = new Vector3((bx0 + bx1) * 0.5f, hinge.y, (bz0 + bz1) * 0.5f);
                Vector3 toCenter = center - hinge;
                toCenter.y = 0f;
                float da = Vector3.Dot(toCenter, a);
                float db = Vector3.Dot(toCenter, b);
                if (Mathf.Abs(da - db) > 0.01f)
                {
                    reason = "两侧都" + (inA ? "在" : "不在") + "跨度内，取朝向房间中心的一侧";
                    return da > db ? a : b;
                }
            }

            // 房间范围用不上时，退回「门扇离哪边更近」（最短路径）。
            // 门模都是 64.4°/34°/32° 半开，这个兜底在多数门上是对的。
            float angA = Vector3.SignedAngle(panel, a, Vector3.up);
            float angB = Vector3.SignedAngle(panel, b, Vector3.up);
            reason = "房间范围用不上，退回最短路径（" + angA.ToString("0.0") + "° vs " +
                     angB.ToString("0.0") + "°）";
            return Mathf.Abs(angA) <= Mathf.Abs(angB) ? a : b;
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

                // 【2026-10-22 P18 补：两根轴「一样」的时候，改用门扇朝向判】
                //
                // 为什么必须补：P18 新绑的 5 扇门里，`Class1-2`（门 x=-0.56，
                // 房间 x∈[-12.4,-0.7]）和 `Class1-3`（门 x=11.44，房间 x∈[-0.7,11.3]）
                // 的门枢轴**同时**在 X 和 Z 上跑出房间范围 —— 门洞就开在房间的东南角上，
                // 两个方向的超出量分别只有 **0.14m 和 0.01m**。
                //
                // 这种量级的差是「房间范围表是照灯反推的、门在它外沿」造成的，不是几何事实：
                // 把 `TwinRoomBounds` 里 1F 的 MinZ 从 33.5 改成 33.4，这 0.01m 就没了，
                // 判据会当场翻成「东西墙」。**判据不能建立在 1cm 的余量上。**
                //
                // 换一个和房间表无关的事实：**门扇现在摆在什么角度**。
                // 场景里 11 扇门都是半开 64.4°，于是
                //   · 南北墙（墙沿 X）上的门 → 门扇 yaw 落在 ±(45°, 90°]
                //   · 东西墙（墙沿 Z）上的门 → 门扇 yaw 落在 ±[0°, 45°]
                // 实测 6 扇门 yaw 全部 = 64.40°（`_scratch` 里算的），全部判南北墙。
                // yaw 先归一到 (-90°, 90°]：一扇朝 +X 半开的门 yaw 可能是 115.6°，
                // 不归一会被判成东西墙。
                if (outX == outZ && TryLeafIsAlongX(door, out alongX))
                    return true;
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

        /// <summary>
        /// 用**门扇当前朝向来**判墙向：门半开时，门扇方向与墙的夹角就是开合角（约 64°），
        /// 所以「门扇 yaw 落在 45°~90°」就说明它挂在南北墙上（墙沿 X）。
        ///
        /// 【为什么这个判据比房间范围表可靠】
        /// 房间范围表是**照灯反推**的，门在它的外沿上，超出量常常只有 1~15cm
        /// （Class1-2 门在 x=-0.56、房间边界 -0.7，差 0.14m；z 方向差 0.01m）。
        /// 用 1cm 的余量去判「门在哪面墙上」，改一次表就翻。
        /// 门扇朝向是模型自己的姿态，和表无关。
        ///
        /// 前提：门是**半开**的（本场景 11 扇全是 64.4°）。如果哪天把门模型改成关着的，
        /// 门扇 yaw 会接近 0°，这个判据就失效 —— 那种情况下房间范围判据通常能接住。
        /// </summary>
        private static bool TryLeafIsAlongX(TwinAnchor door, out bool alongX)
        {
            alongX = false;
            if (door == null) return false;

            Vector3 panel = door.transform.right;
            panel.y = 0f;
            if (panel.sqrMagnitude < 0.0001f) return false;
            panel.Normalize();

            // Unity 里把 +X 绕 Y 转 yaw 得到 (cos yaw, 0, -sin yaw) → yaw = atan2(-z, x)
            float leafYaw = Mathf.Atan2(-panel.z, panel.x) * Mathf.Rad2Deg;

            // 归一到 (-90°, 90°]：朝 +X 半开的门 yaw 可能是 115.6°，
            // 不归一会被当成「贴着东西墙」。
            while (leafYaw > 90f) leafYaw -= 180f;
            while (leafYaw <= -90f) leafYaw += 180f;

            alongX = Mathf.Abs(leafYaw) > 45f;
            return true;
        }

        // ---------------------------------------------------------------- 电脑显示器（P19）
        //
        // 【P18 为什么判过「做不出来」，P19 为什么又能做了】
        // P18 的结论是：`Monitor04` 是**单网格单材质**，贴图图集里没有独立的「屏幕」子物体，
        // 所以「开机 = 屏幕亮」无从下手。这个结论只对了一半 ——
        // 图集里确实没有独立的**网格**，但屏幕在**贴图**里是一块规整的纯黑矩形：
        //   实测 `Monitor04.png`（2048×2048）里，屏幕区 = x∈[90,1476]、y∈[89,944]，
        //   共 1,185,030 像素，与 `sum(rgb)<30` 的像素数 1,186,519 几乎相等 → 是一整块实心矩形。
        // 于是可以生成一张**只在那块矩形里是白色**的 `_EmissionMap`，
        // 开机时把 `_EmissionColor` 点亮 —— 亮的就只有屏幕，边框和支架一点都不亮。
        //
        // 遮罩是**运行时按 UV 生成**的（`BuildScreenMask`），不落资源文件：
        // 一来不用往 `Resources/` 塞东西、不用维护 .meta，
        // 二来 UV 矩形就写在下面几个常量里，将来换了显示器模型改一行就够。

        /// <summary>屏幕在 `Monitor04.png` 里的像素范围（左, 右, 上, 下）。**量出来的，不是估的**。</summary>
        private const float MaskTexWidth = 2048f;
        private const float MaskTexHeight = 2048f;
        private const float ScreenPxLeft = 90f;
        private const float ScreenPxRight = 1476f;
        private const float ScreenPxTop = 89f;
        private const float ScreenPxBottom = 944f;

        [Header("电脑显示器（P19）")]
        [Tooltip("开机时屏幕的自发光颜色。**可以超过 1** —— 屏幕 albedo 是纯黑，" +
                 "最终颜色就等于这个值，不超过 1 的话它比周围被照亮的墙还暗，看起来像「关机」")]
        [SerializeField] private Color _screenEmission = new Color(0.85f, 0.95f, 1.10f, 1f);

        [Tooltip("屏幕遮罩的内缩像素。往内收一点，避免屏幕边框被一起点亮")]
        [SerializeField] private float _screenMaskInset = 6f;

        // ---- 【P26】屏幕视频 ----
        // 有视频素材时，屏幕改成「播视频」而不是「亮一块纯色」。
        // 素材放 `Assets/StreamingAssets/PC/`，路径相对该目录。
        // 找不到文件就安静退回纯色模式 —— 素材是后补的，不该因为缺文件报错。
        [Tooltip("主脑屏幕的开机视频（相对 StreamingAssets/PC/）。留空 = 不启用视频")]
        [SerializeField] private string _masterBootVideo = PcMediaLibrary.Boot;

        [Tooltip("机房其余 18 台的视频（相对 StreamingAssets/PC/）。" +
                 "按设计「19 台同步同画面」，默认和主脑用同一个文件")]
        [SerializeField] private string _roomVideo = PcMediaLibrary.Boot;

        [Tooltip("投影幕布的视频（相对 StreamingAssets/PC/）。留空 = 幕布只用纯色自发光")]
        [SerializeField] private string _projectorVideo = PcMediaLibrary.Boot;

        // `_EmissionColor` 是**乘**在 `_EmissionMap` 上的。
        // `_screenCanvasEmission`(0.55) 适合"一块发白的幕布"，但乘在视频上会把画面压暗一档，
        // 所以播视频时换这个近白的色调（略偏冷，>0.9 还能吃到 Bloom）。
        [Tooltip("幕布播视频时的自发光颜色。纯色模式仍用 _screenCanvasEmission")]
        [SerializeField] private Color _projectorVideoTint = new Color(0.95f, 0.98f, 1.05f, 1f);

        // ---- 【P26】投影质感（2026-10-27 用户反馈"没有模拟现实的投影效果"）----
        // 之前幕布是均匀满亮度，看起来像一块 LED 屏。真实投影最标志性的特征是
        // **暗角**：镜头光强中心强、四角弱。这个参数一开，立刻就有"投影"的味道。
        [Tooltip("投影暗角强度。0 = 均匀（像 LED 屏），0.3 = 明显暗角（像投影）")]
        [Range(0f, 0.8f)]
        [SerializeField] private float _projectorVignette = 0.30f;

        [Tooltip("投影画面最高亮度。白幕反射率有限，到不了纯白；显示器可以设 1.0")]
        [Range(0.3f, 1.5f)]
        [SerializeField] private float _projectorMaxBright = 0.92f;

        [Tooltip("投影灯泡色温偏移。正值偏暖（黄），负值偏冷（蓝）")]
        [Range(-1f, 1f)]
        [SerializeField] private float _projectorWarmth = 0.12f;

        [Tooltip("幕布画面是否跟随主脑开关。开 = 主脑关机后幕布变「无信号」黑屏；" +
                 "关 = 幕布一直显示最后一段视频")]
        [SerializeField] private bool _projectorFollowMaster = true;

        // ---- 【P26】幕布调试开关 ----
        // 现在视频直接画在**幕布模型本体**上（材质数组整体替换），没有独立面片了。
        // 排查"看不见"时打开这个开关：幕布会变成**不透明亮红**，
        // 一眼就能确认"换的到底是哪块模型"。确认完记得关掉。
        [Tooltip("【调试】把幕布模型染成不透明亮红，用来确认改的是哪块模型")]
        [SerializeField] private bool _debugScreenQuad;

        // ⚠️ 素材要求：**视频本身不能带黑边**。
        // 着色器只能做「等比铺满 + 裁掉多余」，救不了编码进去的黑边。
        // 2026-10-27 用户给的 `开机视频.mp4` 是 1920×880，左右各 98px 是编码进去的黑边，
        // 已用 `ffmpeg -vf crop=1724:880:98:0` 裁掉重新编码。
        // 换新素材前先跑一遍：
        //   ffmpeg -i in.mp4 -vf "cropdetect=limit=24:round=2:reset=0" -f null -
        // 看输出是不是 crop=W:H:0:0，不是就先裁。

        [Tooltip("**主脑**的屏幕视频合成分辨率。2048 清晰但吃显存（约 16MB/张）")]
        [SerializeField] private int _masterVideoAtlasSize = 2048;

        // ---- 【P26】机房 18 台的图集尺寸 ----
        // 每台显示器都有自己的 RenderTexture + 每帧一次 `Graphics.Blit`。
        // 19 台 × 2048² = 19 次 2048² 的 blit/帧，光带宽就 300MB/s+，
        // 帧率会明显掉（2026-10-27 用户实测"视频播放很卡顿"）。
        //
        // 但机房那 18 台在画面上只占很小一块（玩家站远处看是一排小屏幕），
        // 2048 完全是浪费。512 足够 —— 屏幕矩形在图集里约 1386×855 像素，
        // 缩到 512 后实际显示约 347×214 像素，对远处的显示器绰绰有余。
        //
        // 19 台 × 512² = 原来的 1/16 带宽。
        [Tooltip("**机房 18 台**的屏幕视频合成分辨率。512 足够（远处看是小屏幕），" +
                 "调高会明显掉帧")]
        [SerializeField] private int _roomVideoAtlasSize = 512;

        // ---- 【P26】逐个亮（开机风暴）----
        // 18 台显示器原来共用一个材质，只能全亮全灭，没有"机房开机"的节奏感。
        // 给每台一份材质副本，按时间逐个点亮 —— 每台间隔 `_screenStagger` 秒。
        // 19 台 × 0.15s ≈ 2.9 秒，正好是"一台台亮起来"的观感。
        [Tooltip("联动开机时每台显示器的间隔（秒）。0 = 同时全亮")]
        [Range(0f, 0.5f)]
        [SerializeField] private float _screenStagger = 0.15f;

        // ---- 【P26】音量 ----
        // 主脑和机房是**两个独立音源**（各在自己的锚点位置）。
        // 开机音效两边都给满音量的话，站在中间会听到两倍响 + 相位差。
        // 真实机房里也是"主脑那台最响，其余十几台是背景嗡声"。
        [Tooltip("主脑屏幕音量")]
        [Range(0f, 1f)]
        [SerializeField] private float _masterVolume = 0.9f;

        // ⚠️ 主脑和机房在**同一间教室**，音源都会被定位到同一对音箱（`SubBuffer02`），
        // 所以两个 AudioSource 是**同位置、同相位**叠加 —— 音量会直接相加。
        // 0.9 + 0.2 = 1.1，刚好略响一点，不会削波。
        // 如果都调高（比如 0.9 + 0.6 = 1.5）会听到明显的失真。
        [Tooltip("机房 18 台的音量。它们和主脑共用同一对音箱，音量会直接相加，别调太高")]
        [Range(0f, 1f)]
        [SerializeField] private float _roomVolume = 0.20f;

        [Tooltip("投影仪（幕布）的音量。同样和主脑共用音箱，注意叠加")]
        [Range(0f, 1f)]
        [SerializeField] private float _projectorVolume = 0.15f;

        // ---- 【P26】PPT 显示 ----
        // PPT 图片是白底为主，用显示器那套偏暗的自发光（0.55/0.60/0.66）会整体发灰。
        // 这里给接近纯白、略偏暖，看起来像投影仪投的白底幻灯片。
        [Tooltip("PPT 图片的自发光颜色。接近白但略暖，模拟投影仪投白底幻灯片")]
        [SerializeField] private Color _slideEmission = new Color(0.95f, 0.95f, 0.92f, 1f);

        [Tooltip("视频画面四周均匀内缩的比例。0 = 铺满整块屏幕（**默认**）。" +
                 "⚠️ 显示器模型本身自带白色边框（Monitor04 的 bezel），再加内缩会变成双层边框")]
        [Range(0f, 0.3f)]
        [SerializeField] private float _screenVideoInset = 0f;

        /// <summary>
        /// 往上走到「父节点叫 Interior」的那一层 —— 那就是**房间节点**。
        ///
        /// 直接取 parent 也行（实测投影仪/显示器就挂在房间节点下），但多走两步不花钱，
        /// 而且换成"设备挂在吊架/桌腿下"这种结构时仍然正确。
        ///
        /// 【为什么要找房间节点】给投影仪找幕布、给屏幕找音箱，都得在**同一间房**里找，
        /// 否则会串到隔壁教室（两个教室都有 ProjectorScreen01 / SubBuffer02）。
        /// </summary>
        private Transform FindRoomNode(Transform from)
        {
            Transform room = from;
            while (room != null && room.parent != null && room.parent.name != "Interior")
                room = room.parent;
            return room;
        }

        /// <summary>
        /// 【P26】找房间里的**音箱**，返回它们的中心点。
        ///
        /// 声音应该从音箱出来，不是从显示器/投影仪出来。
        /// 这个场景的机房墙上有两个 `SubBuffer02`（截图里幕布两侧的黑色箱子），
        /// 把音源放在两者中点，走近时能听出"声音来自那面墙"。
        ///
        /// 用 `Renderer` 而不是 `Transform` 收集：同一个音箱可能有子物体也带同名，
        /// 用 Transform 会重复计数导致中心点偏移。Renderer 每个音箱只有一个。
        ///
        /// 找不到就返回 false，调用方退回"跟着锚点走"。
        /// </summary>
        private bool TryGetRoomAudioPos(Transform room, out Vector3 pos)
        {
            pos = Vector3.zero;
            if (room == null) return false;

            Renderer[] rs = room.GetComponentsInChildren<Renderer>(true);
            Vector3 sum = Vector3.zero;
            int n = 0;

            for (int i = 0; i < rs.Length; i++)
            {
                if (rs[i] == null) continue;
                string nm = rs[i].name;

                // 这个场景叫 SubBuffer02；再兜几个常见命名，换模型时不至于失效
                bool hit = nm.IndexOf("SubBuffer", StringComparison.OrdinalIgnoreCase) >= 0
                        || nm.IndexOf("Speaker", StringComparison.OrdinalIgnoreCase) >= 0
                        || nm.IndexOf("AudioBox", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!hit) continue;

                sum += rs[i].bounds.center;
                n++;
            }

            if (n == 0) return false;

            pos = sum / n;
            Debug.Log("[TwinPresent] 找到 " + n + " 个音箱（" + room.name + "），" +
                      "音源放在 (" + pos.x.ToString("0.00") + ", " +
                      pos.y.ToString("0.00") + ", " + pos.z.ToString("0.00") + ")");
            return true;
        }

        /// <summary>屏幕矩形在**图集 UV** 里的范围（左下 / 右上）。和 `BuildScreenMask` 用同一套常量。</summary>
        private void ScreenUvRect(out Vector2 min, out Vector2 max)
        {
            float inset = Mathf.Max(0f, _screenMaskInset);
            float u0 = (ScreenPxLeft + inset) / MaskTexWidth;
            float u1 = (ScreenPxRight - inset) / MaskTexWidth;
            float v0 = 1f - (ScreenPxBottom - inset) / MaskTexHeight;
            float v1 = 1f - (ScreenPxTop + inset) / MaskTexHeight;
            min = new Vector2(u0, v0);
            max = new Vector2(u1, v1);
        }

        /// <summary>这个 PC 点位该播哪个视频。返回 null/空 = 该点位不启用视频。</summary>
        private string VideoFileFor(string twinId)
        {
            if (string.Equals(twinId, "ClassComputer/PC/Master_01", StringComparison.Ordinal))
                return _masterBootVideo;
            if (string.Equals(twinId, "ClassComputer/PC/Room_01", StringComparison.Ordinal))
                return _roomVideo;
            return null;
        }

        private sealed class ScreenVisual
        {
            /// <summary>这台点位管的所有显示器（机房是 19 台）。</summary>
            public Renderer[] Renderers;
            /// <summary>运行时材质副本，带 `_EmissionMap` 遮罩。19 台共用这一个。</summary>
            public Material Material;
            /// <summary>
            /// 【P26】每台独立的材质副本（只在这台点位管**多块**屏时才建）。
            /// 共用一份材质只能全亮全灭；要"逐个亮"必须一台一份。
            /// </summary>
            public Material[] Units;
            /// <summary>
            /// 【P26】18 台是否已经合并回共享材质。
            ///
            /// 逐个亮期间 18 份独立材质 = **18 个 draw call**（动态合批失效）。
            /// 全部点亮后颜色一致了，就该合并回 `Material` 一份 ——
            /// 重新恢复动态合批，变回 1 个 draw call。
            /// 2026-10-27 用户报"视频播放很卡顿"，这是主因之一。
            /// </summary>
            public bool Merged;
            /// <summary>【P26】逐个亮的起点时间；&lt;0 表示没有正在进行的点亮。</summary>
            public float LitAt = -1f;
            /// <summary>【P26】已经点亮了几台。</summary>
            public int LitCount;
            /// <summary>【P26】视频载体。为 null 或 `!HasVideo` 时退回纯色自发光。</summary>
            public PcScreenPlayer Player;
            /// <summary>见 `LampVisual.HasApplied`：不能只判 IsOn，否则「第一次就是关」会早退（99 号清单第 20 条）。</summary>
            public bool HasApplied;
            public bool IsOn;
        }

        private readonly Dictionary<string, ScreenVisual> _screens =
            new Dictionary<string, ScreenVisual>(2, StringComparer.Ordinal);

        private Texture2D _screenMask;

        /// <summary>
        /// 【P26】上一次看到的主脑开关状态（投影跟随用）。
        /// 初值故意给 `true`：主脑默认是关的，这样**第一次检查一定会触发同步**，
        /// 把"投影仪开着但主脑没开"的初始状态纠正过来。
        /// 如果初值给 `false`，而主脑也是关的，就永远不触发、幕布会停在旧画面。
        /// </summary>
        private bool _lastMasterOn = true;
        /// <summary>【P26】下次检查主脑状态的时间。</summary>
        private float _nextMasterSync;

        private void SyncScreen(string twinId, TwinAnchor anchor)
        {
            if (_noVisual.Contains(twinId)) return;

            ScreenVisual v;
            if (!_screens.TryGetValue(twinId, out v))
            {
                v = BuildScreen(anchor);
                if (v == null) { _noVisual.Add(twinId); return; }
                _screens[twinId] = v;
            }

            TwinValue power;
            bool on = anchor.TryGetValue("Power", out power) && power.Value > 0.5f;

            // 断电时屏幕必须是黑的 —— 和灯同一条理由（见 SyncLamp）
            if (!TwinCircuit.IsPowered(twinId)) on = false;

            ApplyScreen(v, on);
        }

        private ScreenVisual BuildScreen(TwinAnchor anchor)
        {
            // 【为什么按「材质名 + 分区」找，而不是按名字找】
            // `Monitor04 (7)` 这种编号是 Unity 复制实例时加的，删一个再加一个就全变
            // （99 号清单第 7 条：名字不是身份）。而材质是模型自带的，改不了。
            // 分区再收一道，是为了「教师办公室那台 PC 不能点亮机房的显示器」——
            // 两个 PC 点位在同一个楼层、隔着几米，只靠材质名会互相串。
            List<Renderer> hits = new List<Renderer>(24);
            Renderer[] all = FindObjectsOfType<Renderer>(true);
            string zone = anchor.Zone;
            bool hasBounds = !string.IsNullOrEmpty(zone) && TwinRoomBounds.Has(zone);

            // 【P26】主脑排除：`PC/Room_01` 只接管**除主脑之外**的显示器。
            //
            // 为什么要这一步：拆点之前 `Room_01` 代表整间 19 台；拆出
            // `PC/Master_01` 之后，如果两个点位都按「材质名 + 分区」收集，
            // 它们会**抢同一批 19 台** —— 点「普通开机」也会 19 台全亮，
            // 三种开机模式就完全没区别了。
            //
            // 用 **Transform 引用**排除，不用「离锚点最近的那台」：
            // 显示器是等距排列的，按距离判会随锚点坐标微调而漂移，
            // 出现"这次排除对了、下次排错了"这种没法复现的问题。
            Transform masterT = null;
            if (string.Equals(anchor.TwinId, "ClassComputer/PC/Room_01", StringComparison.Ordinal))
            {
                TwinRuntime rt = TwinRuntime.Instance;
                TwinAnchor masterA = null;
                if (rt != null && rt.Registry != null &&
                    rt.Registry.TryGet("ClassComputer/PC/Master_01", out masterA) && masterA != null)
                {
                    masterT = masterA.transform;
                }
                if (masterT == null)
                {
                    Debug.LogWarning("[TwinPresent] 找不到主脑锚点「ClassComputer/PC/Master_01」——" +
                                     "「" + anchor.TwinId + "」这次会接管**包括主脑在内**的全部显示器，" +
                                     "「普通开机」会变成和「联动开机」一样。请跑菜单 P26 ① 给主脑挂锚点。");
                }
            }

            for (int i = 0; i < all.Length; i++)
            {
                Renderer r = all[i];
                if (r == null || r.sharedMaterial == null) continue;
                if (!r.sharedMaterial.name.StartsWith("Monitor04", StringComparison.Ordinal)) continue;

                // 主脑那台归 `PC/Master_01` 管，这里跳过
                if (masterT != null &&
                    (r.transform == masterT || r.transform.IsChildOf(masterT))) continue;

                if (hasBounds)
                {
                    Vector3 p = r.transform.position;
                    if (!TwinRoomBounds.Contains(zone, p.x, p.z)) continue;
                }
                hits.Add(r);
            }

            if (hits.Count == 0)
            {
                Debug.Log("[TwinPresent] 「" + anchor.TwinId + "」找不到任何显示器（材质名 Monitor04*），" +
                          "这个点位不会有画面反应。若换了显示器模型，改 BuildScreen 里的材质名前缀。");
                return null;
            }

            Material variant = new Material(hits[0].sharedMaterial);
            variant.name = "TwinScreen_" + (string.IsNullOrEmpty(zone) ? "any" : zone);
            variant.SetTexture("_EmissionMap", BuildScreenMask());
            variant.EnableKeyword("_EMISSION");
            variant.SetColor(EmissionId, Color.black);

            // 只换带 `_EmissionMap` 的那种（标准着色器）。别的着色器换了会丢贴图。
            if (!variant.HasProperty(EmissionId))
            {
                Debug.Log("[TwinPresent] 「" + anchor.TwinId + "」的显示器材质没有 _EmissionColor，" +
                          "无法做「开机屏幕亮」。");
                Destroy(variant);
                return null;
            }

            for (int i = 0; i < hits.Count; i++) hits[i].sharedMaterial = variant;

            ScreenVisual v = new ScreenVisual();
            v.Renderers = hits.ToArray();
            v.Material = variant;

            // 【P26】屏幕接视频：把 `_EmissionMap` 从静态遮罩换成"视频合成 RT"。
            // RT 里屏幕矩形内是画面、其余纯黑，所以边框不会被点亮。
            // 找不到素材时 `HasVideo = false`，这块屏退回纯色自发光，不影响其它逻辑。
            string videoFile = VideoFileFor(anchor.TwinId);
            if (!string.IsNullOrEmpty(videoFile))
            {
                Vector2 uvMin, uvMax;
                ScreenUvRect(out uvMin, out uvMax);

                // 主脑满音量，机房给低值（两个独立音源，都给满会叠成两倍响）
                bool isMaster = string.Equals(anchor.TwinId, "ClassComputer/PC/Master_01",
                                              StringComparison.Ordinal);
                float vol = isMaster ? _masterVolume : _roomVolume;

                // 主脑用高分辨率，机房 18 台用低分辨率（省 16 倍带宽）
                int atlas = isMaster ? _masterVideoAtlasSize : _roomVideoAtlasSize;

                // 音源放音箱处（找不到就跟着锚点）
                Vector3 audioPos;
                bool hasAudioPos = TryGetRoomAudioPos(FindRoomNode(anchor.transform), out audioPos);

                v.Player = PcScreenPlayer.Create(anchor.TwinId.Replace('/', '_'), videoFile, uvMin, uvMax,
                                                 variant, _screenEmission, anchor.transform,
                                                 atlas, _screenVideoInset, 0f, vol,
                                                 hasAudioPos ? (Vector3?)audioPos : null);
                if (v.Player != null && v.Player.HasVideo && v.Player.ScreenRT != null)
                {
                    variant.SetTexture("_EmissionMap", v.Player.ScreenRT);
                    variant.SetColor(EmissionId, Color.black);   // 先黑着，等 ApplyScreen 开机
                }
            }

            // ---- 【P26】逐个亮：给每台一份独立材质 ----
            //
            // 原来 18 台共用 `variant` 一个材质 → 只能全亮全灭，没有"机房开机"的节奏。
            // 要一台台亮，就必须一台一份材质（`_EmissionColor` 是逐材质的状态）。
            //
            // 代价：破坏静态合批，18 台变 18 个 draw call。对这个规模的场景可以接受，
            // 换来的是"开机风暴"这个最有辨识度的观感。
            //
            // 顺序：按 **X 坐标** 排序，让点亮从房间一侧扫到另一侧。
            // `FindObjectsOfType` 的返回顺序是引擎内部的，不稳定 —— 不排序的话
            // 每次 Play 点亮顺序都不一样，看起来像 bug。
            if (hits.Count > 1 && _screenStagger > 0.001f)
            {
                hits.Sort((a, b) => a.transform.position.x.CompareTo(b.transform.position.x));

                v.Units = new Material[hits.Count];
                for (int i = 0; i < hits.Count; i++)
                {
                    Material unit = new Material(variant);
                    unit.name = variant.name + "_u" + i.ToString("00");
                    unit.SetColor(EmissionId, Color.black);     // 初始全黑
                    hits[i].sharedMaterial = unit;
                    v.Units[i] = unit;
                }
                v.LitCount = 0;
                v.LitAt = -1f;
            }

            Debug.Log("[TwinPresent] 「" + anchor.TwinId + "」接管了 " + hits.Count +
                      " 台显示器（分区 " + (string.IsNullOrEmpty(zone) ? "未指定" : zone) +
                      (v.Player != null && v.Player.HasVideo ? "，屏幕视频=开" : "") +
                      (v.Units != null ? "，逐个亮=" + _screenStagger.ToString("0.00") + "s/台" : "") + "）");
            return v;
        }

        /// <summary>
        /// 生成屏幕发光遮罩：只有屏幕那块 UV 矩形是白的，其余全黑。
        ///
        /// 【UV 怎么算的】Unity 的 v 轴从**下**往上，PNG 的 y 从**上**往下，所以要翻一下：
        ///   u = x / 2048，  v = 1 - y / 2048
        /// 屏幕 x∈[90,1476] y∈[89,944] →
        ///   u ∈ [0.0439, 0.7207]，v ∈ [0.5391, 0.9565]
        /// 不翻的话遮罩会落在图集的**下半部分**（那是机箱背板），屏幕永远不亮。
        /// </summary>
        private Texture2D BuildScreenMask()
        {
            if (_screenMask != null) return _screenMask;

            const int size = 256;
            float inset = Mathf.Max(0f, _screenMaskInset);

            float u0 = (ScreenPxLeft + inset) / MaskTexWidth;
            float u1 = (ScreenPxRight - inset) / MaskTexWidth;
            float v0 = 1f - (ScreenPxBottom - inset) / MaskTexHeight;
            float v1 = 1f - (ScreenPxTop + inset) / MaskTexHeight;

            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.name = "TwinMonitorScreenMask";
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;

            Color[] px = new Color[size * size];
            for (int y = 0; y < size; y++)
            {
                float v = (y + 0.5f) / size;
                bool inV = v >= v0 && v <= v1;
                for (int x = 0; x < size; x++)
                {
                    float u = (x + 0.5f) / size;
                    bool on = inV && u >= u0 && u <= u1;
                    px[y * size + x] = on ? Color.white : Color.black;
                }
            }
            tex.SetPixels(px);
            tex.Apply(false, false);

            _screenMask = tex;
            return tex;
        }

        private void ApplyScreen(ScreenVisual v, bool on)
        {
            if (v.HasApplied && v.IsOn == on) return;
            v.HasApplied = true;
            v.IsOn = on;

            // 【P26】有独立材质的（多块屏）→ 颜色交给 `TickScreenStagger` 按时间推进
            if (v.Units != null && v.Units.Length > 1)
            {
                if (on)
                {
                    // 上次点亮完已经合并回共享材质了，这里重新拆开
                    SplitUnits(v);
                    v.LitAt = Time.time;
                    v.LitCount = 0;
                }
                else
                {
                    v.LitAt = -1f;
                    v.LitCount = 0;
                    // 关机：直接全灭（不做逐个熄灭，下课时没人看屏幕）。
                    // 合并期间发光走的是共享材质，只设 Units 是无效的。
                    if (v.Material != null && v.Material.HasProperty(EmissionId))
                        v.Material.SetColor(EmissionId, Color.black);
                    for (int i = 0; i < v.Units.Length; i++)
                        if (v.Units[i] != null) v.Units[i].SetColor(EmissionId, Color.black);
                }

                // 视频播放仍由播放器管（它作用在模板材质 `Material` 上，不影响 Units 的颜色）
                if (v.Player != null && v.Player.HasVideo) v.Player.SetOn(on);
                return;
            }

            // 【P26】有视频的屏：开关与自发光都交给播放器统一管
            // （它要负责"播/停 + 清黑 + 自发光颜色"三件事，拆开容易漏）
            if (v.Player != null && v.Player.HasVideo)
            {
                v.Player.SetOn(on);
                return;
            }

            if (v.Material == null) return;
            if (!v.Material.HasProperty(EmissionId)) return;

            v.Material.SetColor(EmissionId, on ? _screenEmission : Color.black);
        }

        /// <summary>
        /// 【P26】把 18 份独立材质合并回共享的那一份，恢复动态合批。
        ///
        /// 逐个亮期间必须一台一份材质（`_EmissionColor` 是逐材质的），
        /// 代价是动态合批失效 —— 18 台从 1 个 draw call 变 18 个。
        /// 全部点亮后颜色都一样了，就没必要再分开：
        /// 把 18 台的 `sharedMaterial` 都指向 `v.Material`，Unity 会自动重新合批。
        ///
        /// 原始 `Units` 数组**保留**（不销毁材质），下次开机重新拆开时直接复用，
        /// 省掉 18 次 `new Material`。
        /// </summary>
        private void MergeUnits(ScreenVisual v)
        {
            if (v.Merged || v.Material == null || v.Units == null) return;

            // 共享材质也要是"亮"的，否则合并后屏幕会灭
            if (v.Material.HasProperty(EmissionId))
                v.Material.SetColor(EmissionId, _screenEmission);

            for (int i = 0; i < v.Renderers.Length && i < v.Units.Length; i++)
            {
                if (v.Renderers[i] != null) v.Renderers[i].sharedMaterial = v.Material;
            }
            v.Merged = true;
        }

        /// <summary>
        /// 【P26】把共享材质重新拆成每台一份，准备逐个亮。和 `MergeUnits` 配对。
        /// </summary>
        private void SplitUnits(ScreenVisual v)
        {
            if (!v.Merged || v.Material == null || v.Units == null) return;

            for (int i = 0; i < v.Renderers.Length && i < v.Units.Length; i++)
            {
                if (v.Renderers[i] == null || v.Units[i] == null) continue;
                v.Units[i].SetColor(EmissionId, Color.black);
                v.Renderers[i].sharedMaterial = v.Units[i];
            }
            v.Merged = false;
        }

        /// <summary>
        /// 【P26】逐个亮的推进。每帧检查一次，按 `_screenStagger` 把屏幕一台台点亮。
        ///
        /// 【为什么用时间驱动而不是协程】协程要处理"点亮途中又被关机"的取消逻辑，
        /// 而且 `TwinDevicePresenter` 的 Update 已经在轮询了，这里顺手算一下最省事，
        /// 状态永远由 `LitAt` + 当前时间决定，不存在"卡在中间"的状态。
        /// </summary>
        private void TickScreenStagger()
        {
            if (_screens.Count == 0) return;

            foreach (KeyValuePair<string, ScreenVisual> kv in _screens)
            {
                ScreenVisual v = kv.Value;
                if (v == null || v.Units == null || v.Units.Length < 2) continue;
                if (!v.IsOn || v.LitAt < 0f) continue;

                float elapsed = Time.time - v.LitAt;
                int want = Mathf.Clamp(
                    1 + Mathf.FloorToInt(elapsed / Mathf.Max(0.01f, _screenStagger)),
                    0, v.Units.Length);

                if (want <= v.LitCount) continue;

                for (int i = v.LitCount; i < want; i++)
                    if (v.Units[i] != null) v.Units[i].SetColor(EmissionId, _screenEmission);

                v.LitCount = want;

                if (v.LitCount >= v.Units.Length)
                {
                    v.LitAt = -1f;      // 全亮完了，停止推进
                    MergeUnits(v);
                    Debug.Log("[TwinPresent] 「" + kv.Key + "」" + v.Units.Length +
                              " 台显示器已全部点亮（材质已合并，恢复合批）");
                }
            }
        }

        /// <summary>
        /// 【P26】给某个点位换视频（"打开PPT" / "视频播放" 用）。
        /// 显示器点位和幕布点位都支持；素材不存在时返回 false，画面保持不变。
        /// </summary>
        public bool PlayPcVideo(string twinId, string fileName)
        {
            ScreenVisual sv;
            if (_screens.TryGetValue(twinId, out sv) && sv != null && sv.Player != null)
                return sv.Player.PlayFile(fileName);

            ProjectorVisual pv;
            if (_projectors.TryGetValue(twinId, out pv) && pv != null && pv.Player != null)
                return pv.Player.PlayFile(fileName);

            Debug.Log("[P26] 点位「" + twinId + "」没有视频载体，换片请求忽略。");
            return false;
        }

        // ================================================================
        // 【P26】播放模式（PPT / 视频）
        //
        // 用户要求：点「打开PPT」进 PPT 模式，面板只留「上一页 / 下一页 / 退出」；
        // 点「视频播放」进视频模式，只留「切换 / 暂停 / 退出」。
        // 这样避免误触「全部关机」这类破坏性按钮，也更像真实的设备操作。
        //
        // 两种模式**互斥**：进 PPT 就停视频，进视频就藏 PPT —— 它们往同一批材质上贴图。
        // ================================================================

        /// <summary>当前处于哪种播放模式。</summary>
        public enum PcPlayMode
        {
            /// <summary>没有在演示（面板显示常规按钮）。</summary>
            None = 0,
            /// <summary>PPT 图片轮播。</summary>
            Ppt = 1,
            /// <summary>视频播放。</summary>
            Video = 2
        }

        private PcPlayMode _playMode = PcPlayMode.None;
        private PcSlideShow _slideShow;
        private string[] _videoList;
        private int _videoIndex = -1;

        /// <summary>当前播放模式。</summary>
        public PcPlayMode PlayMode { get { return _playMode; } }

        /// <summary>PPT 总页数。</summary>
        public int SlideCount { get { return _slideShow != null ? _slideShow.Count : 0; } }

        /// <summary>PPT 当前页（1 起，给 UI 显示用）。</summary>
        public int SlideNumber { get { return _slideShow != null ? _slideShow.Index + 1 : 0; } }

        /// <summary>视频总数。</summary>
        public int VideoCount { get { return _videoList != null ? _videoList.Length : 0; } }

        /// <summary>当前视频序号（1 起）。</summary>
        public int VideoNumber { get { return _videoIndex + 1; } }

        /// <summary>视频是否暂停中。</summary>
        public bool VideoPaused
        {
            get
            {
                PcScreenPlayer p = MasterPlayer();
                return p != null && p.IsPaused;
            }
        }

        /// <summary>
        /// 【P26】关机动画路径（相对 `PC/`）。留空 = 关机直接断电，不播动画。
        /// </summary>
        [Tooltip("关机动画（相对 StreamingAssets/PC/）。点关机时先播这段，播完才真正断电")]
        [SerializeField] private string _shutdownVideo = PcMediaLibrary.Shutdown;

        [Tooltip("关机动画最多等多久（秒）。防止视频解码失败时卡住关不掉机")]
        [Range(1f, 15f)]
        [SerializeField] private float _shutdownTimeout = 8f;

        /// <summary>当前是否在关机动画中（面板据此显示"正在关机…"）。</summary>
        public bool IsShuttingDown { get { return _shutdownRoutine != null; } }

        private Coroutine _shutdownRoutine;

        /// <summary>
        /// 【P26】按真实逻辑关机：**先播关机动画，播完再断电**。
        ///
        /// 真实电脑点关机不是瞬间黑屏，而是走一段"正在关机"。
        /// 直接 `SetPower(0)` 会让画面"啪"地消失，很跳。
        ///
        /// 实现要点：
        ///  - `includeRoom` 决定机房 18 台要不要一起播 ——
        ///    「全部关机」要，「只关主脑」不要（机房还开着，跟着播关机动画很怪）
        ///  - 等**所有**参与的播放器都播完（`IsFinished`），再多等 0.4 秒留最后一帧
        ///  - **超时保护**：`_shutdownTimeout` 秒还没播完就强制关 ——
        ///    素材缺失/解码失败时不能把关机卡住，那是最糟的故障
        /// </summary>
        public void BeginShutdown(System.Action onComplete, bool includeRoom)
        {
            if (_shutdownRoutine != null) return;   // 已经在关，忽略重复点击
            _shutdownRoutine = StartCoroutine(ShutdownRoutine(onComplete, includeRoom));
        }

        private System.Collections.IEnumerator ShutdownRoutine(System.Action onComplete, bool includeRoom)
        {
            bool hasVideo = !string.IsNullOrEmpty(_shutdownVideo)
                            && PcMediaLibrary.Exists(_shutdownVideo);

            if (!hasVideo)
            {
                Debug.Log("[P26] 没有关机动画（" + _shutdownVideo + "），直接断电。");
                _shutdownRoutine = null;
                if (onComplete != null) onComplete();
                yield break;
            }

            // 退出播放模式（PPT/视频）—— 关机动画要能盖住画面
            _playMode = PcPlayMode.None;
            if (_slideShow != null) _slideShow.Hide();

            PcScreenPlayer mp = MasterPlayer();
            PcScreenPlayer pp = ProjectorPlayer();
            PcScreenPlayer rp = includeRoom ? RoomPlayer() : null;

            // 【机房必须先合并材质】18 台在"逐个亮"完成后会合并回共享材质；
            // 但如果在点亮途中就点关机，它们还各用一份 `Units` 材质，
            // 而播放器只操作共享的 `Material` —— 那样 18 台不会显示关机动画。
            // 合并之后 18 台都指向 `Material`，一次 `PlayFile` 全部生效。
            if (rp != null)
            {
                ScreenVisual rv;
                if (_screens.TryGetValue("ClassComputer/PC/Room_01", out rv) && rv != null)
                    MergeUnits(rv);
            }

            if (mp != null) mp.PlayFile(_shutdownVideo);
            if (pp != null) pp.PlayFile(_shutdownVideo);
            if (rp != null) rp.PlayFile(_shutdownVideo);

            Debug.Log("[P26] 关机动画开始：" + _shutdownVideo);

            // 等**所有**参与的播放器都播完（或超时）
            //
            // 【为什么不能只等主脑】主脑和机房是各自 `Prepare()` 的，
            // 就绪时间可能差几百毫秒。只等主脑会在机房动画放到一半时断电 ——
            // 表现就是"子电脑的关机动画没播完/根本没播"。
            float t = 0f;
            while (t < _shutdownTimeout)
            {
                bool done = IsNullOrFinished(mp) && IsNullOrFinished(pp) && IsNullOrFinished(rp);
                if (done && t > 0.5f) break;       // 至少等 0.5 秒，避免"刚播就判完"
                t += Time.unscaledDeltaTime;
                yield return null;
            }

            // 最后一帧留一下，不然画面消失得太突然
            yield return new WaitForSecondsRealtime(0.4f);

            Debug.Log("[P26] 关机动画结束（" + t.ToString("0.0") + "s，" +
                      (includeRoom ? "含机房 18 台" : "仅主脑+投影") + "），断电");
            _shutdownRoutine = null;

            // 【视觉兜底】先把参与关机的屏幕**直接收成"关机态"**，再发命令。
            //
            // 为什么：关机动画最后一帧通常是黑的，如果命令因为任何原因没生效
            // （设备无响应 / 数据源是外部 MQTT / 网络抖动），
            // 画面就会永远停在那一帧黑 —— 看起来像卡死。
            // 2026-10-27 用户报"关机后幕布材质还显示黑色"，就是这个。
            //
            // `SetOn(false)` 会清屏并把自发光压黑，着色器随即切到"幕布本色"（白布）。
            // 命令真发成功后 `SyncProjector` 还会再设一次，幂等无害。
            ForceScreensOff(includeRoom);

            if (onComplete != null) onComplete();
        }

        /// <summary>
        /// 把屏幕强制收成"关机态"（只改视觉，不发命令）。
        /// 同时把 `IsOn` 标记同步下来，避免下次开机的早退判断错位。
        /// </summary>
        private void ForceScreensOff(bool includeRoom)
        {
            ScreenVisual sv;
            if (_screens.TryGetValue("ClassComputer/PC/Master_01", out sv) && sv != null)
            {
                if (sv.Player != null) sv.Player.SetOn(false);
                sv.IsOn = false;
                sv.HasApplied = true;
            }

            if (includeRoom && _screens.TryGetValue("ClassComputer/PC/Room_01", out sv) && sv != null)
            {
                if (sv.Player != null) sv.Player.SetOn(false);
                sv.IsOn = false;
                sv.HasApplied = true;
            }

            ProjectorVisual pv;
            if (_projectors.TryGetValue("ClassComputer/Projector/Main_01", out pv) && pv != null)
            {
                if (pv.Player != null) pv.Player.SetOn(false);
                pv.IsOn = false;
                pv.HasApplied = true;
                if (pv.Beam != null) pv.Beam.intensity = 0f;
                if (pv.BeamCone != null && pv.BeamCone.activeSelf) pv.BeamCone.SetActive(false);
            }
        }

        /// <summary>播放器为 null 视为"已完成"（没这个设备就不该等它）。</summary>
        private static bool IsNullOrFinished(PcScreenPlayer p)
        {
            return p == null || p.IsFinished;
        }

        /// <summary>取主脑的播放器（换片/暂停都通过它）。</summary>
        private PcScreenPlayer MasterPlayer()
        {
            ScreenVisual sv;
            if (_screens.TryGetValue("ClassComputer/PC/Master_01", out sv) && sv != null)
                return sv.Player;
            return null;
        }

        /// <summary>取投影仪的播放器。</summary>
        private PcScreenPlayer ProjectorPlayer()
        {
            ProjectorVisual pv;
            if (_projectors.TryGetValue("ClassComputer/Projector/Main_01", out pv) && pv != null)
                return pv.Player;
            return null;
        }

        /// <summary>取机房 18 台的播放器（它们共用一份材质，所以只有一个播放器）。</summary>
        private PcScreenPlayer RoomPlayer()
        {
            ScreenVisual sv;
            if (_screens.TryGetValue("ClassComputer/PC/Room_01", out sv) && sv != null)
                return sv.Player;
            return null;
        }

        /// <summary>
        /// 进入 PPT 模式：停视频，显示第一页（或上次那一页）。
        /// 返回 false 表示没有 PPT 素材。
        /// </summary>
        public bool EnterPptMode()
        {
            // 屏幕矩形在显示器图集里的 UV —— PPT 合成要用同一套，否则遮罩对不上
            Vector2 uvMin, uvMax;
            ScreenUvRect(out uvMin, out uvMax);

            // 【每次都重新收集材质】投影仪/显示器关掉再开时材质会被重新 new，
            // 旧的已销毁。沿用旧列表会往死对象上贴图，屏幕毫无反应。
            List<Material> mats = CollectPptTargets();

            if (_slideShow == null)
            {
                _slideShow = PcSlideShow.Create(PcMediaLibrary.GetSlides(), mats.ToArray(),
                                                _slideEmission, uvMin, uvMax,
                                                _masterVideoAtlasSize);
            }
            else
            {
                _slideShow.SetTargets(mats.ToArray());
            }

            if (!_slideShow.HasSlides)
            {
                Debug.Log("[P26] 「打开PPT」失败：没找到 PPT 图片。" +
                          "放到 " + PcMediaLibrary.FolderPath + "/" +
                          PcMediaLibrary.SlideFolder + "/ 下，文件名用 slide_01.jpg 这种前缀。");
                return false;
            }

            // 停视频，避免两边抢同一个材质
            PcScreenPlayer mp = MasterPlayer();
            if (mp != null) mp.HaltForOverlay();
            PcScreenPlayer pp = ProjectorPlayer();
            if (pp != null) pp.HaltForOverlay();

            _playMode = PcPlayMode.Ppt;

            // 第一次进从第 1 页开始；再次进入保持上次那一页
            if (_slideShow.Index < 0) _slideShow.Show(0);
            else _slideShow.Show(_slideShow.Index);

            Debug.Log("[P26] 进入 PPT 模式：" + _slideShow.Count + " 页，当前第 " +
                      SlideNumber + " 页，贴图目标 " + mats.Count + " 个");
            return true;
        }

        /// <summary>
        /// 收集 PPT 要贴图的材质：主脑屏幕 + 幕布。
        /// 投影仪还没建好（没开机）时只返回主脑那份，之后由 `TickPptAttach` 补上。
        /// </summary>
        private List<Material> CollectPptTargets()
        {
            List<Material> mats = new List<Material>(4);

            ScreenVisual sv;
            if (_screens.TryGetValue("ClassComputer/PC/Master_01", out sv) && sv != null)
            {
                if (sv.Material != null) mats.Add(sv.Material);
                if (sv.Units != null)
                {
                    for (int i = 0; i < sv.Units.Length; i++)
                        if (sv.Units[i] != null) mats.Add(sv.Units[i]);
                }
            }

            ProjectorVisual pv;
            if (_projectors.TryGetValue("ClassComputer/Projector/Main_01", out pv) && pv != null
                && pv.ScreenMaterial != null)
            {
                mats.Add(pv.ScreenMaterial);
            }

            return mats;
        }

        /// <summary>
        /// 【P26】PPT 模式下补挂贴图目标。
        ///
        /// 点「打开PPT」会顺带开投影仪，但 `BuildProjector` 要等命令下发、
        /// 0.1~0.3 秒后才跑完。进 PPT 那一刻幕布材质还不存在，
        /// 不补的话幕布会一直是黑的。
        /// 每 0.2 秒扫一次，材质一出现就补上。
        /// </summary>
        private void TickPptAttach()
        {
            if (_playMode != PcPlayMode.Ppt || _slideShow == null) return;
            if (Time.unscaledTime < _nextPptAttach) return;
            _nextPptAttach = Time.unscaledTime + 0.2f;

            List<Material> mats = CollectPptTargets();
            for (int i = 0; i < mats.Count; i++) _slideShow.AddTarget(mats[i]);
        }

        /// <summary>下次补挂贴图的时间点。</summary>
        private float _nextPptAttach;

        /// <summary>下一页。返回 false 表示没在 PPT 模式或没有素材。</summary>
        public bool NextSlide()
        {
            if (_playMode != PcPlayMode.Ppt || _slideShow == null) return false;
            if (!_slideShow.Next()) return false;
            Debug.Log("[P26] PPT → 第 " + SlideNumber + "/" + SlideCount + " 页");
            return true;
        }

        /// <summary>上一页。</summary>
        public bool PrevSlide()
        {
            if (_playMode != PcPlayMode.Ppt || _slideShow == null) return false;
            if (!_slideShow.Prev()) return false;
            Debug.Log("[P26] PPT → 第 " + SlideNumber + "/" + SlideCount + " 页");
            return true;
        }

        /// <summary>退出 PPT 模式，画面回到桌面待机图。</summary>
        public void ExitPptMode()
        {
            if (_playMode != PcPlayMode.Ppt) return;
            _playMode = PcPlayMode.None;
            if (_slideShow != null) _slideShow.Hide();

            // 【为什么回待机图而不是重播开机】2026-10-27 用户报"退出 PPT 又开始开机"。
            // 真实电脑退出全屏放映是回到桌面，不会重启。
            RestoreIdleScreen();
            Debug.Log("[P26] 退出 PPT 模式");
        }

        /// <summary>
        /// 进入视频模式：从列表里播第 1 个。
        /// 返回 false 表示没有视频素材。
        /// </summary>
        public bool EnterVideoMode()
        {
            if (_videoList == null || _videoList.Length == 0)
                _videoList = PcMediaLibrary.GetVideos();

            if (_videoList == null || _videoList.Length == 0)
            {
                Debug.Log("[P26] 「视频播放」失败：没找到视频。" +
                          "放到 " + PcMediaLibrary.FolderPath + "/" +
                          PcMediaLibrary.VideoFolder + "/ 下，文件名用 01_xxx.mp4 这种前缀。");
                return false;
            }

            // 退出 PPT（两者互斥）
            if (_playMode == PcPlayMode.Ppt && _slideShow != null) _slideShow.Hide();

            _playMode = PcPlayMode.Video;
            _videoIndex = 0;
            PlayCurrentVideo();

            Debug.Log("[P26] 进入视频模式：" + _videoList.Length + " 个视频，正在播第 " +
                      VideoNumber + " 个「" + _videoList[_videoIndex] + "」");
            return true;
        }

        /// <summary>切到下一个视频（循环）。</summary>
        public bool NextVideo()
        {
            if (_playMode != PcPlayMode.Video || _videoList == null || _videoList.Length == 0)
                return false;

            _videoIndex = (_videoIndex + 1) % _videoList.Length;
            PlayCurrentVideo();
            Debug.Log("[P26] 切换视频 → 第 " + VideoNumber + "/" + _videoList.Length +
                      " 个「" + _videoList[_videoIndex] + "」");
            return true;
        }

        /// <summary>暂停 / 继续当前视频。</summary>
        public bool ToggleVideoPause()
        {
            if (_playMode != PcPlayMode.Video) return false;

            PcScreenPlayer mp = MasterPlayer();
            bool nowPaused = mp != null && mp.IsPaused;
            if (mp != null) mp.SetPaused(!nowPaused);

            PcScreenPlayer pp = ProjectorPlayer();
            if (pp != null) pp.SetPaused(!nowPaused);

            return true;
        }

        /// <summary>退出视频模式，画面回到桌面待机图。</summary>
        public void ExitVideoMode()
        {
            if (_playMode != PcPlayMode.Video) return;
            _playMode = PcPlayMode.None;
            RestoreIdleScreen();
            Debug.Log("[P26] 退出视频模式");
        }

        /// <summary>
        /// 退出演示后把屏幕恢复成"待机画面"（桌面静态图）。
        ///
        /// ── 为什么不是重播开机视频 ──────────────────────────────────────
        /// 2026-10-27 用户报的 bug：「打开PPT功能，并退出后，会重新进入开机状态：
        /// 重新播放开机视频」。这确实不对 —— 退出演示不是重启，
        /// 真实电脑退出全屏播放是回到**桌面**，不会重放开机动画。
        ///
        /// 所以这里贴一张 `desktop.jpg`（`boot.mp4` 的末帧，即"开机完成"的画面）。
        /// 视觉上就是"演示结束，停在桌面"。
        ///
        /// ── 为什么必须先 `RestoreTargetTexture()` ──────────────────────
        /// 从 PPT 退出来时材质上贴的还是 PPT 那张合成图。
        /// 先收回贴图（指向 `ScreenRT`），`ShowStill` 才能把桌面图合进去。
        /// </summary>
        private void RestoreIdleScreen()
        {
            PcScreenPlayer mp = MasterPlayer();
            if (mp != null)
            {
                mp.RestoreTargetTexture();
                if (!mp.ShowStill(PcMediaLibrary.Desktop))
                {
                    // 没有待机图：退回开机视频（至少画面不是黑的）
                    if (!string.IsNullOrEmpty(_masterBootVideo)) mp.PlayFile(_masterBootVideo);
                }
            }

            PcScreenPlayer pp = ProjectorPlayer();
            if (pp != null)
            {
                pp.RestoreTargetTexture();
                if (!pp.ShowStill(PcMediaLibrary.Desktop))
                {
                    if (!string.IsNullOrEmpty(_projectorVideo)) pp.PlayFile(_projectorVideo);
                }
            }
        }

        /// <summary>把当前选中的视频同时播到主脑和幕布上。</summary>
        private void PlayCurrentVideo()
        {
            if (_videoList == null || _videoIndex < 0 || _videoIndex >= _videoList.Length) return;

            string file = _videoList[_videoIndex];

            PcScreenPlayer mp = MasterPlayer();
            if (mp != null) mp.PlayFile(file);

            PcScreenPlayer pp = ProjectorPlayer();
            if (pp != null) pp.PlayFile(file);
        }

        /// <summary>
        /// 【P26】投影画面跟随主脑：主脑关机后幕布不该继续显示旧画面。        ///
        /// 真实投影仪没信号会显示"无信号"（黑屏），不会把最后一帧冻在那儿。
        /// 这条同时补一个逻辑漏洞：`SyncProjector` 的早退是按"投影仪自己的开关"判断的，
        /// 主脑开关变化时投影仪状态没变、不会重跑 —— 所以必须单独盯主脑。
        ///
        /// 每 0.15 秒查一次（不是每帧）：主脑开关是低频事件，没必要每帧读 registry。
        /// </summary>
        private void TickProjectorFollowMaster()
        {
            if (!_projectorFollowMaster) return;
            if (_projectors.Count == 0) return;
            if (Time.unscaledTime < _nextMasterSync) return;
            _nextMasterSync = Time.unscaledTime + 0.15f;

            TwinAnchor ma;
            if (_runtime == null || _runtime.Registry == null) return;
            if (!_runtime.Registry.TryGet("ClassComputer/PC/Master_01", out ma) || ma == null) return;

            TwinValue pv;
            bool masterOn = ma.TryGetValue("Power", out pv) && pv.Value > 0.5f;
            if (masterOn == _lastMasterOn) return;
            _lastMasterOn = masterOn;

            foreach (KeyValuePair<string, ProjectorVisual> kv in _projectors)
            {
                ProjectorVisual v = kv.Value;
                if (v == null || v.Player == null || !v.Player.HasVideo) continue;

                // 投影仪本身开着，才跟着主脑的画面走
                v.Player.SetOn(v.IsOn && masterOn);
            }

            Debug.Log("[P26] 投影同步：主脑" +
                      (masterOn ? "开机 → 幕布恢复画面" : "关机 → 幕布无信号"));
        }

        /// <summary>【P26】每帧把视频合成进各屏的 RT。挂在这里是因为表现层已经有一个 Update。</summary>
        private void TickScreenVideos()
        {
            if (_screens.Count > 0)
            {
                foreach (KeyValuePair<string, ScreenVisual> kv in _screens)
                {
                    ScreenVisual v = kv.Value;
                    if (v != null && v.Player != null) v.Player.Tick();
                }
            }

            // 幕布也算一块屏，一起合成
            if (_projectors.Count > 0)
            {
                foreach (KeyValuePair<string, ProjectorVisual> kv in _projectors)
                {
                    ProjectorVisual v = kv.Value;
                    if (v != null && v.Player != null) v.Player.Tick();
                }
            }
        }

        // ---------------------------------------------------------------- 投影仪（P19）

        [Header("投影仪（P19）")]
        [Tooltip("幕布点亮时的自发光颜色。投影出来的画面偏冷白")]
        [SerializeField] private Color _screenCanvasEmission = new Color(0.55f, 0.60f, 0.66f, 1f);

        // ---- 【P26】投影光束 ----
        // 只有幕布亮、投影仪不"打光"的话，看起来像一块自己会亮的白板，
        // 不像"有台机器在往上面投"。补两样：
        //   ① 一盏聚光灯（灯罩朝幕布）—— 让幕布周围真的被照亮
        //   ② 一段锥形光束网格（加法混合）—— 机房那种"空气里有光柱"的感觉
        [Tooltip("投影光束的聚光灯强度。0 = 不打光，只亮幕布")]
        [SerializeField] private float _beamIntensity = 2.4f;

        [Tooltip("光束颜色（偏冷白，和幕布一致）")]
        [SerializeField] private Color _beamColor = new Color(0.80f, 0.88f, 1.00f, 1f);

        [Tooltip("光束网格的透明度。0 = 不画光柱，只留聚光灯")]
        [Range(0f, 1f)]
        [SerializeField] private float _beamConeAlpha = 0.30f;

        private sealed class ProjectorVisual
        {
            public Renderer Canvas;
            public Material CanvasMaterial;
            /// <summary>【P26】幕布视频。为 null 或 `!HasVideo` 时退回纯色自发光。</summary>
            public PcScreenPlayer Player;
            /// <summary>【P26】幕布模型本体上那个"播视频"的材质（换掉整个材质数组后只留这一个）。</summary>
            public Material ScreenMaterial;
            /// <summary>【P26】朝幕布的聚光灯；光束关掉时为 null。</summary>
            public Light Beam;
            /// <summary>【P26】可见光柱的网格物体；不画光柱时为 null。</summary>
            public GameObject BeamCone;
            public bool HasApplied;
            public bool IsOn;
        }

        private readonly Dictionary<string, ProjectorVisual> _projectors =
            new Dictionary<string, ProjectorVisual>(2, StringComparer.Ordinal);

        private void SyncProjector(string twinId, TwinAnchor anchor)
        {
            if (_noVisual.Contains(twinId)) return;

            ProjectorVisual v;
            if (!_projectors.TryGetValue(twinId, out v))
            {
                v = BuildProjector(anchor);
                if (v == null) { _noVisual.Add(twinId); return; }
                _projectors[twinId] = v;
            }

            TwinValue power;
            bool on = anchor.TryGetValue("Power", out power) && power.Value > 0.5f;
            if (!TwinCircuit.IsPowered(twinId)) on = false;

            if (v.HasApplied && v.IsOn == on) return;
            v.HasApplied = true;
            v.IsOn = on;

            // 【诊断】2026-10-27 用户报"点投影幕布没反应"。没有日志就无法区分
            // 是"命令没到"、"状态没变"还是"变了但没画出来"，所以这里每次状态翻转都打一行。
            Debug.Log("[P26] 投影仪 " + twinId + " Power=" + (power.Value.ToString("0")) +
                      " → on=" + on +
                      "（视频=" + (v.Player != null && v.Player.HasVideo ? "有" : "无") +
                      "，聚光灯=" + (v.Beam != null ? "有" : "无") +
                      "，光柱=" + (v.BeamCone != null ? "有" : "无") + "）");

            // 【P26】有视频的幕布：开关与自发光都交给播放器统一管
            // （现在材质在**幕布模型本体**上，不再有独立面片要显隐）
            if (v.Player != null && v.Player.HasVideo)
            {
                v.Player.SetOn(on);
            }
            else if (v.CanvasMaterial != null && v.CanvasMaterial.HasProperty(EmissionId))
            {
                v.CanvasMaterial.SetColor(EmissionId, on ? _screenCanvasEmission : Color.black);
            }

            // 【P26】光束跟着开关走：聚光灯改强度（不改 enabled，避免每帧重建光照），
            // 光柱网格直接显隐（它只是个面片，开关一次没有开销）。
            if (v.Beam != null) v.Beam.intensity = on ? _beamIntensity : 0f;
            if (v.BeamCone != null && v.BeamCone.activeSelf != on) v.BeamCone.SetActive(on);
        }

        /// <summary>
        /// 找这台投影仪对应的**幕布**，给它一份带自发光的材质副本。
        ///
        /// 【为什么是幕布而不是投影仪本体】投影仪本体是个黑色小盒子，吊在天花板上，
        /// 开机时它自己几乎不发光 —— 亮了也看不出来。真正"一眼就知道开了"的是**幕布**。
        ///
        /// ── 为什么按**层级**找，不按分区矩形、也不按材质 ────────────────
        /// 2026-10-22 P19 第一版按「分区矩形 + 名字前缀」找，实测**两块幕布都找不到**：
        ///   · 计算机教室的幕布在 (-11.54, 3.01, 38.01)，而房间矩形 MaxX = -12.4 → x 差 0.86m 落在框外；
        ///   · 教师办公室的幕布在 (2.81, 6.33, 33.67)，而房间矩形 MinZ = 35.0 → z 差 1.33m 落在框外。
        /// 原因很实在：**幕布挂在教室最前面（讲台那侧的墙上）**，而房间矩形是照**灯**反推的，
        /// 灯只铺到座位区，讲台那一条压根不在矩形里。这不是表写错了，是这张表本来就不为它服务。
        ///   · 也不能按材质找：幕布用的是 `PlasticWhite1` / `PlasticBlack1` 这种**通用**材质，
        ///     全场到处都在用，按材质名会把半个学校都收进来。
        /// 层级是唯一零歧义的事实：投影仪和幕布都是 `Interior/<房间>` 的直接子物体。
        /// </summary>
        private ProjectorVisual BuildProjector(TwinAnchor anchor)
        {
            // 往上走到「父节点叫 Interior」的那一层 —— 那就是房间节点。
            Transform room = FindRoomNode(anchor.transform);

            if (room == null)
            {
                Debug.Log("[TwinPresent] 「" + anchor.TwinId + "」往上找不到 Interior 房间节点，" +
                          "投影仪幕布接不上。");
                return null;
            }

            Renderer canvas = null;
            Renderer[] all = room.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < all.Length; i++)
            {
                Renderer r = all[i];
                if (r == null || r.gameObject == null) continue;
                if (!r.gameObject.name.StartsWith("ProjectorScreen", StringComparison.Ordinal)) continue;
                canvas = r;
                break;
            }

            if (canvas == null || canvas.sharedMaterial == null)
            {
                Debug.Log("[TwinPresent] 「" + anchor.TwinId + "」在房间节点 " + room.name +
                          " 下找不到幕布（ProjectorScreen*），开机时不会有画面反应。");
                return null;
            }

            ProjectorVisual v = new ProjectorVisual();
            v.Canvas = canvas;

            // ── ① 视频路径（优先）─────────────────────────────────────────
            //
            // **直接在幕布模型本体上换材质**。为什么之前十几次都失败：
            //   · 幕布是**预制体实例**（场景里搜不到 ProjectorScreen），
            //     运行时 `sharedMesh` 被静态合批换成 `Combined Mesh (root: scene) N`
            //     （几十 submesh、几万顶点）→ 不能做几何推断、也不能复制。
            //   · 模型 UV 是**图集 UV**、不铺满 0..1 → 塞 `_EmissionMap` 只显示一块碎片。
            //   · `sharedMaterial = x` 只改**第 0 个** submesh；合批后材质数组有多个槽位，
            //     真正画屏幕的那个通常不是第 0 个 → 改了永远没反应。
            //
            // 现在的做法：`Renderer.bounds` 是准确的（实测 3.11×2.12×0.10 = 幕布本身），
            // 由它算出屏幕平面，交给 `SchoolTwin/ScreenOnModel` —— 该着色器用**世界坐标**
            // 反算 UV，完全不依赖模型 UV；再把这个 Renderer 的**整个材质数组**换成它。
            bool videoReady = false;
            if (!string.IsNullOrEmpty(_projectorVideo))
            {
                ScreenFrame frame;
                if (TryGetScreenFrame(canvas, anchor.transform.position, out frame))
                {
                    Material modelMat = BuildModelScreenMaterial(canvas, frame, _projectorVideoTint);
                    if (modelMat != null)
                    {
                        v.ScreenMaterial = modelMat;

                        // 音源放音箱处（机房墙上的两个 SubBuffer02 中点）
                        Vector3 audioPos;
                        bool hasAudioPos = TryGetRoomAudioPos(room, out audioPos);

                        v.Player = PcScreenPlayer.Create(anchor.TwinId.Replace('/', '_'), _projectorVideo,
                                                         Vector2.zero, Vector2.one,
                                                         modelMat, _projectorVideoTint, anchor.transform,
                                                         1024, 0f, frame.Aspect, _projectorVolume,
                                                         hasAudioPos ? (Vector3?)audioPos : null);
                        if (v.Player != null && v.Player.HasVideo && v.Player.ScreenRT != null)
                        {
                            modelMat.SetTexture("_MainTex", v.Player.ScreenRT);
                            modelMat.SetColor(EmissionId, Color.black);
                            videoReady = true;
                        }
                        else
                        {
                            Debug.Log("[TwinPresent] 「" + anchor.TwinId +
                                      "」视频未就绪，幕布退回纯色自发光。");
                        }
                    }
                }
            }

            // ── ② 纯色兜底（没有视频 / 视频起不来时）─────────────────────
            if (!videoReady)
            {
                Material variant = new Material(canvas.sharedMaterial);
                variant.name = "TwinScreenCanvas_" + room.name;
                variant.EnableKeyword("_EMISSION");
                if (!variant.HasProperty(EmissionId))
                {
                    Debug.Log("[TwinPresent] 「" + anchor.TwinId + "」的幕布材质没有 _EmissionColor，跳过。");
                    Destroy(variant);
                    return null;
                }
                variant.SetColor(EmissionId, Color.black);
                canvas.sharedMaterial = variant;
                v.CanvasMaterial = variant;
            }

            // 【P26】投影"打光"：一盏朝幕布的聚光灯 + 一段可见光柱
            v.Beam = BuildProjectorBeam(anchor.transform, canvas.bounds);
            v.BeamCone = BuildProjectorBeamCone(anchor.transform, canvas.bounds);
            if (v.BeamCone != null) v.BeamCone.SetActive(false);   // 开机时才显形

            Debug.Log("[TwinPresent] 「" + anchor.TwinId + "」接管幕布：" + canvas.name +
                      "（房间节点 " + room.name + "，聚光灯=" + (v.Beam != null ? "有" : "无") +
                      "，光柱=" + (v.BeamCone != null ? "有" : "无") + "）");
            return v;
        }

        // ---------------------------------------------------------------- 幕布屏幕载体（P26）

        /// <summary>幕布的"屏幕平面"：中心 + 两个单位轴 + 宽高（世界空间）。</summary>
        private struct ScreenFrame
        {
            public Vector3 Center;
            public Vector3 Right;
            public Vector3 Up;
            public Vector3 Normal;
            public float Width;
            public float Height;
            public float Aspect { get { return Width / Mathf.Max(1e-5f, Height); } }
        }

        /// <summary>
        /// 从 `Renderer.bounds` 算出幕布的屏幕平面。
        ///
        /// 【为什么只信 bounds】这个场景里：
        ///   · `sharedMesh` 是静态合批后的 `Combined Mesh (root: scene) N`（整间房，几十 submesh）
        ///   · 模型的 `transform.forward` 实测是 (0,1,0)（3ds Max 导出带旋转，不可信）
        /// 而 `Renderer.bounds` 是**该 Renderer 自己的世界 AABB**，实测 3.11×2.12×0.10 ——
        /// 正好是一块薄幕布，唯一可靠的输入。
        ///
        /// 幕布是薄板 → **最薄的轴就是厚度**，其方向即法线；另两轴构成屏幕面（大的当宽）。
        ///
        /// 返回 false 表示包围盒不像一块屏幕（某轴过厚 / 尺寸离谱），调用方应放弃。
        /// </summary>
        private bool TryGetScreenFrame(Renderer canvas, Vector3 projectorPos, out ScreenFrame frame)
        {
            frame = default(ScreenFrame);
            if (canvas == null) return false;

            Bounds b = canvas.bounds;
            Vector3 size = b.size;

            // 最薄轴
            int axis = 0;
            if (size.y < size.x) axis = 1;
            if (size.z < size[axis]) axis = 2;

            // 合理性检查：厚度要明显小于另两轴，且宽高在 0.5~15m
            float thickness = size[axis];
            int a1 = (axis + 1) % 3;
            int a2 = (axis + 2) % 3;
            float d1 = size[a1], d2 = size[a2];
            float maxDim = Mathf.Max(d1, d2);
            float minDim = Mathf.Min(d1, d2);

            if (thickness > minDim * 0.5f || minDim < 0.5f || maxDim > 15f)
            {
                Debug.Log("[TwinPresent] 幕布包围盒不像一块屏幕（" +
                          size.x.ToString("0.00") + "×" + size.y.ToString("0.00") + "×" +
                          size.z.ToString("0.00") + "m），不接管。");
                return false;
            }

            int wideAxis = d1 >= d2 ? a1 : a2;
            int highAxis = wideAxis == a1 ? a2 : a1;

            Vector3 normal = Vector3.zero; normal[axis] = 1f;
            if (Vector3.Dot(normal, projectorPos - b.center) < 0f) normal = -normal;

            Vector3 up = Vector3.zero; up[highAxis] = 1f;

            // ⚠️ `right` 必须由右手系算出，**不能直接取世界轴**。
            //
            // 2026-10-27 第一次改：直接 `right[wideAxis] = 1f` → 画面**左右镜像**。
            // 2026-10-27 第二次改：`Cross(up, normal)` → **还是镜像**，符号搞反了。
            //
            // 正确推导（Unity 是**左手坐标系**，用相机验证）：
            //   相机的 `right = Cross(up, forward)`，其中 forward 是**视线方向**。
            //   观众的视线 = -normal（从幕布看向自己），
            //   所以 right = Cross(up, -normal) = **-Cross(up, normal)**。
            //
            // 代入本例验证：normal=(1,0,0)（朝 +X，面向观众），up=(0,1,0)
            //   → 视线 = (-1,0,0) → right = Cross((0,1,0),(-1,0,0)) = (0,0,1) = **+Z** ✔
            //   （之前取 -Z，正好左右翻）
            Vector3 right = -Vector3.Cross(up, normal).normalized;

            // 保险：若 up 与 normal 意外平行（同轴退化），换一个正交基
            if (right.sqrMagnitude < 1e-6f)
            {
                up = axis == 1 ? Vector3.forward : Vector3.up;
                right = -Vector3.Cross(up, normal).normalized;
            }

            frame.Center = b.center;
            frame.Right = right;
            frame.Up = up;
            frame.Normal = normal;
            frame.Width = Mathf.Max(0.05f, maxDim);
            frame.Height = Mathf.Max(0.05f, minDim);
            return true;
        }

        /// <summary>
        /// 给幕布模型本体造一个"播视频"的材质，并把该 Renderer 的**整个材质数组**换掉。
        ///
        /// ── 为什么必须换整个数组 ──────────────────────────────────────
        /// `renderer.sharedMaterial = x` 只改**第 0 个** submesh。这个场景里幕布被合批过，
        /// 材质数组有多个槽位，真正画屏幕的那个通常不是第 0 个 —— 所以只改单个永远没反应。
        ///
        /// ── 为什么用世界坐标算 UV ────────────────────────────────────
        /// `SchoolTwin/ScreenOnModel` 不用模型 UV（那是图集 UV，不铺满 0..1），
        /// 而是把顶点世界位置投到屏幕平面上反算 0..1 —— 与模型 UV 完全无关。
        ///
        /// ── 为什么不用 Standard + _EmissionMap ───────────────────────
        /// Standard 的最终色 = `albedo × 光照 + emission`，屏幕会被房间灯光影响，
        /// 而且关了灯屏幕会跟着暗。投影幕布应该只显示画面本身，所以底色压黑、只输出视频。
        /// </summary>
        private Material BuildModelScreenMaterial(Renderer canvas, ScreenFrame frame, Color tint)
        {
            Shader sh = Shader.Find("SchoolTwin/ScreenOnModel");
            if (sh == null)
            {
                Debug.Log("[TwinPresent] 找不到着色器 `SchoolTwin/ScreenOnModel`，幕布视频不启用。" +
                          "（注意：打包时需加入 Always Included Shaders）");
                return null;
            }

            Material mat = new Material(sh);
            mat.name = "TwinModelScreen_" + canvas.name;
            mat.SetVector("_Center", new Vector4(frame.Center.x, frame.Center.y, frame.Center.z, 0f));
            mat.SetVector("_Right", new Vector4(frame.Right.x, frame.Right.y, frame.Right.z, 0f));
            mat.SetVector("_Up", new Vector4(frame.Up.x, frame.Up.y, frame.Up.z, 0f));
            mat.SetVector("_Size", new Vector4(frame.Width, frame.Height, 0f, 0f));
            mat.SetColor(EmissionId, Color.black);      // 关机全黑

            // 关机时显示的"幕布本色"：应该是一块**白布**。
            //
            // ⚠️ 不能直接沿用原材质颜色 —— 2026-10-27 用户报"关机后幕布还是黑色"，
            // 就是因为幕布模型的原材质本身就是深色（美术做"屏幕关闭"的效果），
            // 直接取它的亮度算出来接近黑，看起来像没关机。
            //
            // 真实投影幕布是白色/浅灰的布料，无论模型贴图是什么颜色。
            // 所以：取原材质亮度作为**上限参考**，但设一个下限 0.62，
            // 保证任何模型关掉后都是一块浅灰白的布。
            Color offCol = new Color(0.78f, 0.78f, 0.75f, 1f);
            Material origMat = canvas.sharedMaterial;
            if (origMat != null && origMat.HasProperty("_Color"))
            {
                Color oc = origMat.GetColor("_Color");
                float lum = Mathf.Clamp01(oc.r * 0.299f + oc.g * 0.587f + oc.b * 0.114f);
                lum = Mathf.Clamp(lum, 0.62f, 1f);       // ← 下限 0.62：白布
                offCol = new Color(lum, lum, lum * 0.98f, 1f);
            }
            mat.SetColor("_OffColor", offCol);

            // 投影质感参数
            mat.SetFloat("_Vignette", Mathf.Clamp(_projectorVignette, 0f, 0.8f));
            mat.SetFloat("_MaxBright", Mathf.Clamp(_projectorMaxBright, 0.3f, 1.5f));
            mat.SetFloat("_Warmth", Mathf.Clamp(_projectorWarmth, -1f, 1f));

            mat.SetFloat("_DebugRed", _debugScreenQuad ? 1f : 0f);

            // **整个数组**都换成这一个材质 —— 只改第 0 个会导致"改了没反应"
            Material[] src = canvas.sharedMaterials;
            int oldCount = src != null ? src.Length : 0;
            Material[] repl = new Material[Mathf.Max(1, oldCount)];
            for (int i = 0; i < repl.Length; i++) repl[i] = mat;
            canvas.sharedMaterials = repl;

            Debug.Log("[TwinPresent] 幕布改用**模型本体**作载体：" + canvas.name +
                      "（原材质槽 " + oldCount + " 个 → 全部替换）\n" +
                      "   屏幕平面 " + frame.Width.ToString("0.00") + "×" +
                      frame.Height.ToString("0.00") + "m（比例 " + frame.Aspect.ToString("0.000") + "）\n" +
                      "   中心 (" + frame.Center.x.ToString("0.00") + ", " +
                      frame.Center.y.ToString("0.00") + ", " + frame.Center.z.ToString("0.00") + ")\n" +
                      "   法线 (" + frame.Normal.x.ToString("0.00") + ", " +
                      frame.Normal.y.ToString("0.00") + ", " + frame.Normal.z.ToString("0.00") + ")" +
                      (_debugScreenQuad ? "\n   ⚠ 调试模式：整块涂亮红" : ""));

            return mat;
        }

        /// <summary>
        /// 【P26】投影仪 → 幕布的一盏聚光灯。
        ///
        /// 只有幕布自己发光的话，看起来像"一块自己会亮的白板"，
        /// 不像"有台机器在往上面投"。补一盏聚光灯，幕布周围的地面/墙才会真的被照亮，
        /// 那才是"打光"的观感。
        ///
        /// 张角按幕布尺寸算：`2 × atan(半宽 / 距离)`，再夹到 [10°, 120°]。
        /// 用 `ForcePixel`：内置前向渲染逐像素灯默认上限 4 盏，教室里本来就有好几盏，
        /// Auto 模式下这盏会被降级成顶点光（打在墙面这种大网格上几乎看不出亮度）。
        /// </summary>
        private Light BuildProjectorBeam(Transform lens, Bounds canvasBounds)
        {
            if (_beamIntensity <= 0.001f || lens == null) return null;

            Vector3 dir = canvasBounds.center - lens.position;
            float dist = dir.magnitude;
            if (dist < 0.3f) return null;

            GameObject go = new GameObject("TwinProjectorBeam");
            go.transform.SetParent(lens, false);
            go.transform.position = lens.position;
            go.transform.rotation = Quaternion.LookRotation(dir.normalized, Vector3.up);

            float halfSpan = Mathf.Max(canvasBounds.extents.x, canvasBounds.extents.y);
            float angle = 2f * Mathf.Atan2(halfSpan, dist) * Mathf.Rad2Deg;

            Light l = go.AddComponent<Light>();
            l.type = LightType.Spot;
            l.color = _beamColor;
            l.spotAngle = Mathf.Clamp(angle * 1.15f, 10f, 120f);   // 留一点余量罩住幕布
            l.range = dist * 1.2f;
            l.intensity = 0f;                                       // 由 SyncProjector 决定
            l.shadows = LightShadows.None;                          // 投影仪不投影阴影，也省开销
            l.renderMode = LightRenderMode.ForcePixel;
            return l;
        }

        /// <summary>
        /// 【P26】可见光柱：一个从镜头张到幕布的锥形网格，加法混合、边缘渐隐。
        ///
        /// 内置管线没有体积光，这是最接近"空气里有光柱"的廉价做法：
        /// 顶点色从镜头端 alpha=1 渐变到幕布端 alpha=0，用粒子加法着色器乘上去。
        ///
        /// 拿不到着色器就**安静跳过**（只是没有光柱，聚光灯照常工作），不报错。
        /// </summary>
        private GameObject BuildProjectorBeamCone(Transform lens, Bounds canvasBounds)
        {
            if (_beamConeAlpha <= 0.001f || lens == null) return null;

            Shader sh = Shader.Find("Legacy Shaders/Particles/Additive");
            if (sh == null)
            {
                Debug.Log("[TwinPresent] 找不到 `Legacy Shaders/Particles/Additive`，" +
                          "投影光柱不画了（聚光灯照常工作）。");
                return null;
            }

            Vector3 dir = canvasBounds.center - lens.position;
            float dist = dir.magnitude;
            if (dist < 0.3f) return null;

            float radius = Mathf.Max(canvasBounds.extents.x, canvasBounds.extents.y) * 1.15f;

            const int seg = 16;
            Vector3[] verts = new Vector3[seg + 2];
            Color[] cols = new Color[seg + 2];
            int[] tris = new int[seg * 3];

            verts[0] = Vector3.zero;                       // 顶点（镜头）
            // ⚠️ 渐隐必须写进 **RGB**，不能只写 alpha。
            // `Legacy Shaders/Particles/Additive` 是加法混合（Blend One One），
            // alpha 通道根本不参与输出，只认 RGB。
            // 2026-10-26 第一版只改了 alpha → 光柱是一条实心白锥，`_beamConeAlpha` 形同虚设。
            cols[0] = Color.white;                         // 镜头端最亮
            for (int i = 0; i < seg; i++)
            {
                float a = i * Mathf.PI * 2f / seg;
                verts[i + 1] = new Vector3(Mathf.Cos(a) * radius, Mathf.Sin(a) * radius, dist);
                cols[i + 1] = new Color(0f, 0f, 0f, 0f);   // 幕布端全黑（加法下 = 不可见）
            }
            verts[seg + 1] = verts[0];                     // 补一个顶点，方便画侧面
            cols[seg + 1] = cols[0];

            for (int i = 0; i < seg; i++)
            {
                tris[i * 3] = 0;
                tris[i * 3 + 1] = i + 1;
                tris[i * 3 + 2] = (i + 1) % seg + 1;
            }

            Mesh mesh = new Mesh();
            mesh.name = "TwinBeamCone";
            mesh.vertices = verts;
            mesh.colors = cols;
            mesh.triangles = tris;
            mesh.RecalculateBounds();

            GameObject go = new GameObject("TwinProjectorBeamCone");
            go.transform.SetParent(lens, false);
            go.transform.position = lens.position;
            go.transform.rotation = Quaternion.LookRotation(dir.normalized, Vector3.up);

            MeshFilter mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;

            Material mat = new Material(sh);
            mat.name = "TwinBeamConeMat";
            if (mat.HasProperty("_TintColor"))
            {
                // 加法混合下亮度由 RGB 决定，所以把透明度乘进 RGB 才是真正的"调淡"
                Color tint = _beamColor * _beamConeAlpha;
                tint.a = _beamConeAlpha;
                mat.SetColor("_TintColor", tint);
            }

            MeshRenderer mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;

            return go;
        }

        // ---------------------------------------------------------------- 上课铃（P19）
        //
        // 铃声是**程序生成**的，工程里一个音频文件都没有（`find Assets -name *.wav` 为空）。
        // 和空调提示音、告警音是同一套做法（`TwinAlarmAudio.MakeBeep`）。
        //
        // 三层分工，缺一层都会出问题：
        //   ① 课表      `TwinBellScheduler` —— 决定什么时候响（设备侧时钟）
        //   ② 状态位    `MockDeviceAgent.RingNow` / `SetRing` —— `Ring` 置 1，3 秒后自己置 0
        //   ③ 表现      **本方法** —— 只读 `Ring`，响了就播声音 + 让警报器闪
        // 所以「谁让它响的」在这里是不知道也不需要知道的：定时和手动走的是同一个值。

        [Header("上课铃（P19）")]
        [Tooltip("铃声音量（0~1）。程序合成的三音上行铃（E5→G5→C6），长 1.75 秒")]
        [Range(0f, 1f)]
        [SerializeField] private float _bellVolume = 0.8f;

        [Tooltip("打铃时警报器的闪烁间隔（秒）。0 = 不闪，只发声")]
        [Range(0f, 1f)]
        [SerializeField] private float _bellFlashInterval = 0.25f;

        [Tooltip("闪烁颜色")]
        [SerializeField] private Color _bellFlashColor = new Color(0.55f, 0.16f, 0.10f, 1f);

        private sealed class BellVisual
        {
            public Renderer[] Renderers;
            public MaterialPropertyBlock Mpb;
            public bool WasRinging;
            public float NextFlash;
            public bool FlashOn;
        }

        private readonly Dictionary<string, BellVisual> _bells =
            new Dictionary<string, BellVisual>(2, StringComparer.Ordinal);

        private AudioClip _bellChime;

        private void SyncBell(string twinId, TwinAnchor anchor)
        {
            BellVisual v;
            if (!_bells.TryGetValue(twinId, out v))
            {
                v = BuildBell(anchor);
                _bells[twinId] = v;
            }

            TwinValue ring;
            bool ringing = anchor.TryGetValue("Ring", out ring) && ring.Value > 0.5f;

            // ---- 起响：播一次铃声（边沿触发，不是电平触发）----
            // 用 `WasRinging` 而不是「ringing 就播」：0.1 秒轮询一次，
            // 电平触发会一秒播 10 遍，叠成一片噪音。
            if (ringing && !v.WasRinging)
            {
                EnsureAudio();
                if (_bellChime == null) _bellChime = BuildBellChime();
                if (_audio != null && _bellChime != null)
                {
                    _audio.PlayOneShot(_bellChime, Mathf.Clamp01(_bellVolume));
                    Debug.Log("[TwinPresent] 打铃：「" + twinId + "」播放铃声（" +
                              _bellChime.length.ToString("0.0") + " 秒）");
                }
                v.FlashOn = true;
                v.NextFlash = 0f;   // 立刻亮第一下
            }

            // ---- 闪烁 ----
            if (ringing && v.Renderers != null && v.Renderers.Length > 0 && _bellFlashInterval > 0.001f)
            {
                if (Time.unscaledTime >= v.NextFlash)
                {
                    v.FlashOn = !v.FlashOn;
                    v.NextFlash = Time.unscaledTime + _bellFlashInterval;
                    WriteBellEmission(v, v.FlashOn ? _bellFlashColor : Color.black);
                }
            }
            else if (!ringing && v.WasRinging)
            {
                WriteBellEmission(v, Color.black);
            }

            v.WasRinging = ringing;
        }

        private BellVisual BuildBell(TwinAnchor anchor)
        {
            BellVisual v = new BellVisual();
            v.Mpb = new MaterialPropertyBlock();

            // 打铃器挂在 `Alarm01` 上（走廊墙上的消防声光警报器）。
            // 锚点就在它身上，所以直接用它自己的渲染器 —— 不找别的。
            GameObject target = anchor.VisualTarget;
            if (target == null) target = anchor.gameObject;
            v.Renderers = target.GetComponentsInChildren<Renderer>(true);

            if (v.Renderers == null || v.Renderers.Length == 0)
            {
                Debug.Log("[TwinPresent] 「" + anchor.TwinId + "」没有渲染器，打铃只有声音没有画面。");
                v.Renderers = null;
            }

            Debug.Log("[TwinPresent] 「" + anchor.TwinId + "」打铃器就位（" +
                      (v.Renderers == null ? 0 : v.Renderers.Length) + " 个渲染器）");
            return v;
        }

        private static void WriteBellEmission(BellVisual v, Color c)
        {
            if (v.Renderers == null) return;
            for (int i = 0; i < v.Renderers.Length; i++)
            {
                Renderer r = v.Renderers[i];
                if (r == null || r.sharedMaterial == null) continue;
                if (!r.sharedMaterial.HasProperty(EmissionId)) continue;

                r.GetPropertyBlock(v.Mpb);
                v.Mpb.SetColor(EmissionId, c);
                r.SetPropertyBlock(v.Mpb);
                v.Mpb.Clear();
            }
        }

        /// <summary>
        /// 合成一段三音上行铃（E5 → G5 → C6，每音 0.55 秒，指数衰减）。
        ///
        /// 【为什么是三个音而不是一声「叮」】学校的上课铃是**有旋律的**，
        /// 一声短促的「叮」听上去像门铃或者报错提示。三个音上行是全世界校园铃的通用语汇。
        ///
        /// 【为什么要 4ms 起音】直接从中值开始振动，波形上是阶跃，扬声器会「咔哒」一声。
        /// 【为什么要二次谐波】纯正弦像测试音，加一点谐波才像金属铃。
        /// </summary>
        private AudioClip BuildBellChime()
        {
            const int sampleRate = 44100;
            const float noteSeconds = 0.55f;
            const float gapSeconds = 0.05f;

            float[] notes = { 659.25f, 783.99f, 1046.50f };   // E5 / G5 / C6

            int noteLen = Mathf.RoundToInt(sampleRate * noteSeconds);
            int gapLen = Mathf.RoundToInt(sampleRate * gapSeconds);
            int total = notes.Length * noteLen + (notes.Length - 1) * gapLen;

            float[] data = new float[total];
            for (int n = 0; n < notes.Length; n++)
            {
                int start = n * (noteLen + gapLen);
                for (int i = 0; i < noteLen; i++)
                {
                    int idx = start + i;
                    if (idx >= total) break;

                    float t = i / (float)sampleRate;
                    float progress = i / (float)noteLen;

                    float attack = Mathf.Clamp01(t / 0.004f);
                    float decay = Mathf.Exp(-3.2f * progress);
                    float env = attack * decay;

                    float s = Mathf.Sin(2f * Mathf.PI * notes[n] * t)
                            + 0.30f * Mathf.Sin(2f * Mathf.PI * notes[n] * 2f * t)
                            + 0.12f * Mathf.Sin(2f * Mathf.PI * notes[n] * 3f * t);

                    data[idx] = s * env * 0.30f;
                }
            }

            AudioClip clip = AudioClip.Create("TwinBellChime", total, 1, sampleRate, false);
            clip.SetData(data, 0);
            return clip;
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
        /// 只要有一个 AudioSource 能放声音就行。
        ///
        /// 【为什么单独一个方法】`BuildAudio` 只在 `_beepOnReceive` 打开时跑（Awake 里）。
        /// 但上课铃的声音和那个开关**无关** —— 有人把空调提示音关掉了，
        /// 结果打铃也不响了，这种耦合很难查（"我明明开了自动打铃"）。
        /// 所以铃声走这条独立入口，用到时才建。
        /// </summary>
        private void EnsureAudio()
        {
            if (_audio != null) return;
            _audio = gameObject.AddComponent<AudioSource>();
            _audio.playOnAwake = false;
            _audio.loop = false;
            _audio.spatialBlend = 0f;
            _audio.volume = 1f;
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

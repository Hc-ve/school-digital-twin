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
    /// 门 / 暖气 / 出风口指示灯留到下一刀 —— 空调模型是单一网格，
    /// 没有可以单独染色的指示灯部件，硬染会把整台机器染绿。
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

        // ---------------------------------------------------------------- 内部

        private sealed class LampVisual
        {
            public Renderer Renderer;
            public Material Material;
            public Light Glow;
            public bool HasEmission;
            public bool IsOn;
        }

        private static readonly int EmissionId = Shader.PropertyToID("_EmissionColor");

        private readonly Dictionary<string, LampVisual> _lamps =
            new Dictionary<string, LampVisual>(4, StringComparer.Ordinal);

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
                if (v.Material != null) Destroy(v.Material);
                if (v.Glow != null && v.Glow.gameObject != null) Destroy(v.Glow.gameObject);
            }
            _lamps.Clear();
            _noVisual.Clear();

            if (_beep != null) Destroy(_beep);
            if (Instance == this) Instance = null;
        }

        private void Update()
        {
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

            LampVisual v = new LampVisual();
            v.Renderer = main;

            // `.material` 会克隆一份，改的是这一盏自己的材质，不影响其它房间的灯
            v.Material = main.material;
            v.HasEmission = v.Material != null && v.Material.HasProperty(EmissionId);
            if (v.HasEmission) v.Material.EnableKeyword("_EMISSION");

            v.Glow = BuildGlow(anchor, main);

            Debug.Log("[TwinPresent] 灯已接管：" + anchor.TwinId +
                      "（Renderer=" + main.name +
                      "，发光=" + (v.HasEmission ? "支持" : "材质没有 _EmissionColor") +
                      "，点光源=" + (v.Glow != null ? "有" : "无") + "）");

            return v;
        }

        private Light BuildGlow(TwinAnchor anchor, Renderer main)
        {
            if (_lampGlowIntensity <= 0.001f) return null;

            Bounds bounds = main.bounds;
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

            if (v.HasEmission && v.Material != null)
                v.Material.SetColor(EmissionId, on ? _lampEmission : Color.black);

            if (v.Glow != null) v.Glow.intensity = on ? _lampGlowIntensity : 0f;
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

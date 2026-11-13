// 灯管光晕层 · 运行时自举（只开 Bloom，不改任何资产）
// 归属：打光（光照/氛围）　任务：P36 灯管开关态
//
// 用法：
//   默认全自动：灯管第一次被点亮时由 `TwinDevicePresenter.ApplyLamp` 调 `EnsureReady()`。
//   一键回滚：  TwinLampBloom.SetEnabled(false);   // 立刻把 Bloom 关回原样
//   自检摘要：  Debug.Log(TwinLampBloom.Describe());
//
// ══════════════════════════════════════════════════════════════════════════
// 【为什么需要一个独立的文件，而不是塞进 TwinLightingPreset】
//
// 实测（`Docs/P36_阶段1_失败诊断_打光.md`）：`TwinLightingPreset.Apply()` 在全项目
// **没有任何调用点**（grep 全仓 0 处，只有它自己文件里的注释提到）。
// 也就是说"改 TwinLightingPreset 里的 bloomIntensity"这条路线在运行时根本不执行。
// 真正每帧在写后处理的是 `EnvironmentState`（只写 ColorGrading.postExposure）。
// 所以 Bloom 必须由一个**真的会被调用到**的地方开启 —— 也就是灯管表现层。
//
// ══════════════════════════════════════════════════════════════════════════
// 【这个文件解决的三个致命问题】（每一条都是上一轮失败的实测根因）
//
// ① `Bloom.enabled` 从来没被打开过
//    `PPS_Bloom.asset` 里 `Bloom.enabled = { overrideState: 1, value: 0 }` —— 硬关。
//    而 `PostProcessEffectSettings.IsEnabledAndSupported()` **只返回 `enabled.value`**
//    （PPv2 源码 Effects/PostProcessEffectSettings.cs），`active` 不是开关。
//    旧代码写的是 `bl.active = ...`，改错了字段 → Bloom 一次都没执行过。
//
// ② 写错了 profile 层级
//    `PostProcessVolume.profile` 的 getter 会 **克隆** `m_InternalProfile` 并缓存
//    （PostProcessVolume.cs:120-138），相机混合读的是这个克隆；
//    而 `EnvironmentState.BindPostProcess()` 写的是 `sharedProfile`（资产本体）。
//    两份对象不同 ⇒ 如果这里也去写 `volume.profile`，就会**首次访问时克隆一份
//    带着旧 postExposure 的副本**，此后相机只看副本，`EnvironmentState` 每帧写
//    `sharedProfile` 的夜晚压暗就永久失效（夜晚不暗、天空过曝）。
//    所以这里**只写 `sharedProfile`**，物理上不去碰那个 getter。
//
// ③ `intensity` 是 Exp2 标度，旧值等于几乎没有
//    `Bloom.cs:212` → `intensity = RuntimeUtilities.Exp2(v / 10f) - 1f`；
//    着色器里是 `color += bloom * Settings.y * _Bloom_Color`（加法）。
//    v=0.55 → Exp2(0.055)-1 ≈ **0.039 倍**，肉眼不可见。
//    要出《异环》那种可见柔晕，v 需要显著更大（本文件见 `NightIntensity` / `DuskIntensity`）。
//
// ══════════════════════════════════════════════════════════════════════════
// 【为什么只开 Bloom、不动其它后处理】
// 任务包硬约束：白天档观感零变化（P35 v10 第 4 项已验收）。
// 所以本文件**只碰 Bloom 的 4 个参数**，且默认在白天档完全不动作
// （见 `AllowedInPhase`）。ColorGrading / Vignette 一律不碰。
//
// ══════════════════════════════════════════════════════════════════════════
// 【回滚】
// `SetEnabled(false)` 会把 Bloom 恢复成"进场前读到的原值"（`enabled=false`、
// intensity 0），并停止任何后续写入。退出 Play 时 `TwinLampBloomRunner.OnDestroy`
// 还会再调一次 `Shutdown()` 主动写回原值。
//
// ⚠️ 诚实说明：本层写的是 `sharedProfile`，那是 **ScriptableObject 资产本体**，
// 不是内存副本。运行时改它不会自动写盘，但资产对象在内存里确实被改过；
// 若有人在这之后保存，就可能带着临时值落盘。所以退出时必须主动还原（见 Shutdown）。
// 为什么不改成"克隆一份 profile"：那会打断 EnvironmentState 的夜晚压暗
// （它缓存的正是 sharedProfile 里的 ColorGrading），详见 Shutdown 的注释。
// 本层全程**从不调用 `SaveAssets`**。

using System.Collections.Generic;
using UnityEngine;
using SchoolTwin.Weather;

#if UNITY_POST_PROCESSING_STACK_V2
using UnityEngine.Rendering.PostProcessing;
#endif

/// <summary>
/// P36 灯管光晕层。只负责一件事：按昼夜档把 PPv2 的 Bloom 开到合适强度。
///
/// 【为什么用 `UNITY_POST_PROCESSING_STACK_V2` 条件编译】
/// 与 `EnvironmentState` 用反射的理由相同但更进一步：那个文件怕"包被删了导致全工程
/// 编译失败"，所以用反射；这里直接引用类型更清晰，用宏把它整体包起来 ——
/// 包存在（宏由 PPv2 的资源脚本定义）就编译真实逻辑，包被删则整个文件退化成空壳，
/// 依然不会让 Assembly-CSharp 编译失败。
/// </summary>
public static class TwinLampBloom
{
    // =====================================================================
    // 调参（全部按 P36 调研文档的量化锚点取，见 Docs/P36_灯管开关态调研_打光.md）
    // =====================================================================

    /// <summary>
    /// Bloom 强度（PPv2 原始标度，经 `Exp2(v/10)-1` 换算后才是实际倍率）。
    ///
    /// 【P36 v2 2026-11-13 主理人 play 后回调：6.0 → 3.2】
    /// 第一版取 6.0（0.516 倍）时实测**教室整间炸成纯白（62.5% 像素 >0.95）**、
    /// 走廊天花板与墙面被整体冲白。原因不是"灯管太亮"，而是**叠加太猛**：
    /// 只要一片区域过了阈值，Bloom 就会把整片区域再叠上 0.516 倍自己，
    /// 亮的地方越亮、直到削顶。所以强度必须明显降下来。
    ///
    /// 换算对照：
    ///   2.5 → 0.189 倍     3.2 → 0.248 倍     6.0 → 0.516 倍（第一版，过曝）
    /// 要出《异环》那种柔晕，0.2 倍量级已经足够 —— 参考图的光晕是"贴管处约管体 0.7 倍，
    /// 然后快速衰减"，这个"0.7 倍"是**叠加在本来就接近白的管壁上**，
    /// 换算到全局叠加倍率就是 0.2 左右，不是 0.5。
    /// </summary>
    private const float NightIntensity = 3.2f;

    /// <summary>
    /// 黄昏档强度。天还亮着、环境光更强、墙面本身就更接近阈值，
    /// 这时候再叠强光晕最容易把整面墙推爆 —— 所以取夜晚的 40%。
    /// </summary>
    private const float DuskIntensity = 1.3f;

    /// <summary>
    /// 亮度阈值（**Gamma 空间**，着色器里会自己 `GammaToLinearSpace` 转线性）。
    ///
    /// 【P36 v2 2026-11-13 主理人 play 后回调：0.72 → 0.90 —— 这是本轮最关键的修复】
    ///
    /// ── 第一版为什么炸 ────────────────────────────────────────────────
    /// 0.72 → 线性 **0.4770**。而实测走廊墙面显示亮度 0.765 → 线性 **0.5462**，
    /// **超阈值 14%**。于是整面墙、整片天花板全被算进 Bloom，被再叠一层光晕，
    /// 一路推到削顶 —— 这就是「教室 62.5% 像素纯白」「走廊一片灰白」的真正原因。
    ///
    /// ── 阈值与实测对象的线性对照 ──────────────────────────────────────
    ///   threshold 0.90 → 线性 0.7874   ← 当前
    ///   threshold 0.95 → 线性 0.8900
    ///   走廊墙面（显示 0.765）→ 线性 0.5462   ✓ 明显低于阈值，不发光
    ///   灯管管壁（emission 1.45~1.70）→ 线性 ≈1.5   ✓ 远高于阈值，正常发光
    ///
    /// ── 一条必须记住的源码事实 ────────────────────────────────────────
    /// **Bloom 在 ColorGrading 之前执行**（`PostProcessLayer.cs:1262-1267`），
    /// 也就是说它采样的是**还没被 postExposure 压暗**的 HDR 缓冲。
    /// 所以「夜晚档画面暗」**并不能**保护墙面免于发光 ——
    /// 阈值必须自己把墙面挡在外面，与昼夜档无关。第一版想当然以为夜里会安全，是错的。
    ///
    /// ── 更根本的一条：本工程没有 Tonemapper ──────────────────────────
    /// `PPS_Bloom.asset` 里 `ColorGrading.tonemapper = { overrideState: 0, value: 0 }`
    /// —— 即 **None**，从未参与混合。
    /// 没有 tonemapper 时，任何超过 1.0 的亮度会被**硬截断**（saturate）成同一个纯白，
    /// 高光完全失去层次。这就是「教室 62.5% 像素 p50 恰好等于 1.000」的成因：
    /// Bloom 把一大片像素推过 1.0，而没人把它们压回来。
    /// → 阈值在这里的作用不只是"选谁发光"，而是**守住 1.0 这条线**。
    /// 开启 Tonemapper 是正确解法，但会改变白天档基线且要改资产，
    /// 超出本任务硬约束（见 Docs/P36_阶段2_实现记录_打光.md §4.1、§9 ⑦）。
    ///
    /// ⚠️ 这个值需要 play 微调：
    ///   · 若天花板/墙面还有淡淡泛白 → 继续往上加（每次 +0.02）
    ///   · 若灯管完全没光晕了       → 略微往下减（每次 −0.02）
    /// </summary>
    private const float BloomThreshold = 0.90f;

    /// <summary>
    /// 扩散范围（PPv2 `diffusion`，1~10 的整数才有最佳质量）。
    ///
    /// 它直接参与迭代层数计算：`logs = log2(max(w,h)) + diffusion - 10`。
    /// 1080p 下 max=1920 → log2≈10.9，diffusion=8 → 8.9 → 8 层金字塔，
    /// 对应约 3~5 倍管宽的柔晕带，与参考图判读一致。
    /// 取 8（默认 7 略窄），让光晕更"糊"一点、更接近日光灯的漫射感。
    /// </summary>
    private const float BloomDiffusion = 8.0f;

    /// <summary>
    /// 软膝：0 = 硬阈值（过阈值处会有明显边界），1 = 完全软。
    ///
    /// 【P36 v2 2026-11-13 回调：0.5（PPv2 默认）→ 0.30 —— 这是第二个关键修复】
    ///
    /// ── softKnee 到底改了什么（源码实测）─────────────────────────────
    /// `Bloom.cs:172` → `knee = lthresh * softKnee + 1e-5`，
    /// 然后 `threshold = (lthresh, lthresh - knee, knee*2, 0.25/knee)`。
    /// 着色器 `Colors.hlsl:242-255` 的 `QuadraticThreshold` 里：
    /// `rq = clamp(br - (lthresh - knee), 0, knee*2)` ⇒
    /// **真正开始有贡献的亮度不是 `lthresh`，而是 `lthresh - knee`**。
    /// 也就是说 softKnee 把阈值**向前延伸了 `lthresh × softKnee`**。
    ///
    /// ── 为什么要降 ────────────────────────────────────────────────────
    /// threshold 0.90 → lthresh 0.7874。
    ///   softKnee 0.5 → 有效起点线性 **0.3937** ← 走廊墙面 0.5462 仍在里面！
    ///   softKnee 0.3 → 有效起点线性 **0.5512** ← 墙面 0.5462 终于被挡在外面
    ///
    /// 实测贡献率（脚本 `Docs/QA/evidence/P36_backup/calc_bloom_threshold.py`）：
    ///
    /// | softKnee | 有效起点 | 走廊墙面(0.5462) | 教室墙面(≈0.75) | 灯管(1.70) |
    /// |---|---|---|---|---|
    /// | 0.50 | 0.3937 | 2.70% | 10.75% | 53.68% |
    /// | **0.30** | **0.5512** | **0.00%** | 5.58% | **53.68%** |
    /// | 0.10 | 0.7087 | 0.00% | 0.72% | 53.68% |
    ///
    /// **关键：灯管那一列始终是 53.68%，一分不减。**
    /// 因为灯管（1.70）远高于整个膝区，softKnee 只影响"刚好在阈值附近"的东西。
    /// 所以降低 softKnee 是**纯赚**：墙面彻底退出 Bloom，灯管光晕一点不变。
    ///
    /// 取 0.30 而不是 0.10：留一点软过渡，避免灯管边缘出现"描边"硬边；
    /// 0.30 已经把走廊墙面压到 0%，足够。
    ///
    /// ⚠️ 若教室（灯密集）仍泛白，可继续降到 0.20（教室墙面 3.05%）。
    /// </summary>
    private const float BloomSoftKnee = 0.30f;

    // =====================================================================
    // 状态
    // =====================================================================

    /// <summary>总开关。false = 恢复进场原值并停止写入，画面回到开 Bloom 之前。</summary>
    public static bool Enabled { get; private set; } = true;

    private static bool _ready;          // 已成功绑定到 profile
    private static bool _applied;        // 已经把本轮参数写下去过
    private static TimeOfDay _appliedPhase = (TimeOfDay)(-1);
    private static string _failReason;

    /// <summary>
    /// 绑定失败后的下次重试时间。
    ///
    /// 【为什么必须有 —— 这是个真实的性能陷阱】
    /// `Bind()` 里用 `FindObjectsOfType<PostProcessVolume>()`，全场景扫描。
    /// 如果失败后不留冷却、每一帧都重试，那么「场景里没有 Volume」的情况
    /// （本工程的 SchoolSceneNight 就是这样）会变成**每帧一次全场景扫描**。
    /// 这在 1080p 下看不出来，一旦场景物体多就是掉帧的来源，而且极难归因。
    /// 冷却 1 秒：既保证 Volume 迟一点出现时能自动接上，又把开销压到可忽略。
    /// </summary>
    private static float _retryAt;
    private const float RetryInterval = 1.0f;

    /// <summary>
    /// 测量/探测进行中。为 true 时 `Tick()` 不再写入 Bloom 参数，
    /// 否则每帧刷新会把人为关掉的 Bloom 又打开，污染测量结果。
    /// </summary>
    private static bool _capturing;

    /// <summary>域重载 / 每次进 Play 前把静态状态清干净（与 IndoorFillLight 同口径）。</summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        Enabled = true;
        _ready = false;
        _applied = false;
        _appliedPhase = (TimeOfDay)(-1);
        _failReason = null;
        _retryAt = 0f;
        _capturing = false;
        ResetPostState();
    }

#if UNITY_POST_PROCESSING_STACK_V2
    private static readonly List<PostProcessProfile> _profiles = new List<PostProcessProfile>(4);
    private static Bloom _bloom;

    // 进场原值（回滚用）
    private static bool _origEnabled, _origEnabledOverride;
    private static float _origIntensity, _origThreshold, _origDiffusion, _origSoftKnee;
    private static bool _origIntensityOverride, _origThresholdOverride,
                        _origDiffusionOverride, _origSoftKneeOverride;
    private static bool _origCaptured;

    /// <summary>清掉"已绑定"相关的静态状态。放在宏里，包不存在时整段消失。</summary>
    private static void ResetPostState()
    {
        _profiles.Clear();
        _bloom = null;
        _origCaptured = false;
    }
#else
    private static void ResetPostState() { }
#endif

    // =====================================================================
    // 对外
    // =====================================================================

    /// <summary>
    /// 确保 Bloom 已按当前昼夜档打开。**幂等、可反复调**，内部只在需要时写值。
    /// 由 `TwinDevicePresenter.ApplyLamp` 在灯被点亮时调用。
    /// </summary>
    public static void EnsureReady()
    {
        if (!Enabled) return;
        Apply(false);
    }

    /// <summary>每帧轻量刷新（档位变了才真正写）。由 <see cref="TwinLampBloomRunner"/> 调。</summary>
    internal static void Tick()
    {
        if (!Enabled) return;
        // 【测量/探测期间必须停掉】
        // 探测要人为关掉 Bloom 拍一帧。若每帧的 Tick 还在跑，它会把 Bloom 又打开，
        // 于是"关 Bloom"那一帧其实是开着的 —— 数据全错，而且看上去"开关没区别"，
        // 会把人误导到"Bloom 无害"的错误结论上。这类"测量工具本身污染测量对象"
        // 的坑很隐蔽，必须显式挡住。
        if (_capturing) return;
        Apply(false);
    }

    /// <summary>验收/应急用总开关。false 后随时可再 true，不需要重进 Play。</summary>
    public static void SetEnabled(bool on)
    {
        if (Enabled == on) return;
        Enabled = on;

        if (!on) Restore();
        else Apply(true);

        Debug.Log("[TwinLampBloom] 灯管光晕" + (on ? "已启用" : "已停用（一键回滚）") +
                  "。所有改动只存在于运行时，退出 Play 自动消失。");
    }

    /// <summary>
    /// 【诊断用】临时关掉所有 `TwinLampGlow`（灯管点光源），用来判定天花板过亮到底怪谁。
    ///
    /// 【为什么需要这个实验 —— 2026-11-13 22:00 的数据链】
    /// 实测同一套 Bloom 参数下：
    ///   走廊 Corridor1 天花板 p50=0.795、削顶  0%
    ///   教室 2-1     天花板 p50=0.955、削顶 22.9%
    /// 差 0.16。而两处的差别在日志里能查到：
    ///   `IndoorFill.SuppressZoneGlows` 只接管 ActZal/Corridor1/Corridor2/Kitchen/SportHall，
    ///   **ClassComputer 不在其中**（判据见 IndoorFillLight.cs:353
    ///   `TakeOver = corridor || 面积 > 200m²`，教室 85m² 两条都不满足）。
    ///   所以教室是「8 个 TwinLampGlow（2.0/3m）+ 6 个 IndoorFill 补光（1.6/8m）」**两套叠加**，
    ///   走廊只有 IndoorFill 一套。
    ///
    /// 但这是**从代码推出来的**，必须用实验证伪。这个命令就是那个实验：
    /// 关掉 glow 后若天花板明显降下来 → 结论成立；若没变化 → 我的推论错了，
    /// 病根在别处（补光强度 / 曝光），不要在 glow 上继续折腾。
    ///
    /// 【用法】Play 中 Console 粘：
    ///   TwinLampBloom.SetGlowsEnabled(false);   // 关掉所有灯管点光源
    ///   TwinLampBloom.SetGlowsEnabled(true);    // 恢复
    /// 只改 `Light.enabled`，不改任何数值、不落盘，`SetEnabled(true)` 或重进 Play 即还原。
    /// </summary>
    /// <summary>灯管点光源当前是否处于开启状态（供热键切换用）。</summary>
    public static bool GlowsEnabled { get; private set; } = true;

    /// <summary>
    /// 把所有灯管点光源沿世界 Y **相对**平移（负值 = 下移）。供热键现场调节用。
    ///
    /// 【为什么需要"相对平移"而不是"设绝对高度"】
    /// 灯管位置由场景决定，代码不知道每根管的确切高度。所以只做增量：
    /// 调用方记住自己已经累计移了多少，每次传差值即可。基线在首次调用时拍快照，
    /// 之后一直以它为基准 —— 反复按键不会累积漂移。
    ///
    /// 【为什么要能现场调 —— 本轮最大的流程教训】
    /// 天花板过曝的合适下移量取决于灯管到天花板的实际间隙（本工程 0.22m）、
    /// 房间反照率、以及和 `IndoorFillLight` 补光层的叠加程度。
    /// 这些**无法离线算准**，只能看。若每次都要改代码 → 重进 Play → 再看，
    /// 一轮就是几分钟，而主理人已经因为反复 play 消耗了很多耐心。
    /// 做成热键后**一次 play 就能试完所有候选值**。
    /// </summary>
    public static void ShiftGlows(float meters)
    {
        Light[] glows = FindGlows();
        if (glows.Length == 0)
        {
            Debug.LogWarning("[TwinLampBloom] 场景里没有 TwinLampGlow，无法平移。");
            return;
        }

        // 【关键：要先分清哪些灯是"亮着的"】
        // 2026-11-13 主理人在走廊按 F10，日志显示下移了 5.40m 但画面纹丝不动。
        // 原因是走廊的 TwinLampGlow 已被 `IndoorFillLight.SuppressZoneGlows()`
        // 每帧置 `enabled = false` —— 移动的是一批**已经熄灭的灯**，当然没反应。
        // 所以这里必须统计亮着的数量，并在日志里明确告警，否则会让人以为热键失灵。
        int lit = 0;
        for (int i = 0; i < glows.Length; i++)
        {
            if (glows[i] != null && glows[i].enabled) lit++;
        }

        // 首次调用（或灯数量变了）才拍基线，之后不再重拍 —— 否则增量会累积漂移。
        if (_glowBaseline == null || _glowBaseline.Length != glows.Length)
        {
            _glowBaseline = new Vector3[glows.Length];
            for (int i = 0; i < glows.Length; i++)
                _glowBaseline[i] = glows[i].transform.position;
        }

        _glowShift += meters;
        for (int i = 0; i < glows.Length; i++)
        {
            if (glows[i] == null) continue;
            Vector3 p = _glowBaseline[i];
            p.y += _glowShift;
            glows[i].transform.position = p;
        }

        string msg = "[TwinLampBloom] 灯管点光源累计下移 " + (-_glowShift).ToString("0.00") +
                     " m（共 " + glows.Length + " 盏，其中**当前亮着 " + lit + " 盏**）。";
        if (lit == 0)
        {
            msg += "\n  ⚠ 一盏都没亮 —— 说明这个分区的点光源已被 IndoorFillLight 接管关闭，" +
                   "\n     移动它们不会有任何画面变化。请去**教室/房间**（未被接管的区域）再试，" +
                   "\n     或按 F7 关掉补光层，让点光源重新成为唯一照明。";
        }
        Debug.Log(msg);
    }

    /// <summary>已累计的平移量（米，正数=上移）。</summary>
    public static float GlowShift { get { return _glowShift; } }

    private static Vector3[] _glowBaseline;
    private static float _glowShift;

    public static void SetGlowsEnabled(bool on)
    {
        Light[] glows = FindGlows();
        int changed = 0;
        for (int i = 0; i < glows.Length; i++)
        {
            if (glows[i].enabled != on) { glows[i].enabled = on; changed++; }
        }
        GlowsEnabled = on;

        Debug.Log("[TwinLampBloom] 灯管点光源「" + (on ? "已恢复" : "已关闭") +
                  "」：扫描到 " + glows.Length + " 个 TwinLampGlow，实际改变 " + changed + " 个。" +
                  (on ? "" : "\n  ⇒ 现在看天花板：若明显变暗，说明过亮由点光源造成（教室两套光叠加），与 Bloom 无关；" +
                             "\n  ⇒ 若几乎没变，说明我的推论错了，病根在补光强度或曝光。"));
    }

    /// <summary>
    /// 找出所有灯管点光源。
    ///
    /// 【判据是"名字前缀"，不是 parent 是不是灯】
    /// `BuildGlows` 里给**灯所在的 GameObject** 命名 `"TwinLampGlow_" + i`，
    /// 并把光点 `SetParent(anchor.transform)` —— 中间要跨好几层容器。
    /// 早期版本用 `t.parent != null && t.parent.name.StartsWith(...)` 判定，全部漏掉。
    ///
    /// 【为什么必须放在宏外】这个函数不依赖后处理包，而宏外的 `SetGlowsEnabled`
    /// 也要用它。放宏内会在包缺失时编译失败（本项目实测：脚本校验能过，
    /// 但真编译会报 CS0103，因为 `#if` 一收窄就找不到符号了）。
    /// </summary>
    private static Light[] FindGlows()
    {
        Light[] all = UnityEngine.Object.FindObjectsOfType<Light>();
        System.Collections.Generic.List<Light> hit =
            new System.Collections.Generic.List<Light>(all.Length);
        for (int i = 0; i < all.Length; i++)
        {
            Light l = all[i];
            if (l == null) continue;
            if (!l.name.StartsWith("TwinLampGlow", System.StringComparison.Ordinal)) continue;
            hit.Add(l);
        }
        return hit.ToArray();
    }

    /// <summary>
    /// 退出 Play / 组件销毁时把参数写回进场原值。由 <see cref="TwinLampBloomRunner"/> 调。
    ///
    /// 【为什么"退出时还要手动还原"—— 这是防落盘的最后一道保险】
    /// 本层写的是 `sharedProfile`，那是 **ScriptableObject 资产本体**。
    /// 虽然在 Unity 里运行时改动不会自动写盘，但资产对象在内存里确实被改过；
    /// 只要有人在这之后点了保存（或某些版本退出 Play 时不做重载），
    /// 资产就可能带着我们运行时的临时值落盘 —— 那就直接违反「不落盘」的硬约束。
    ///
    /// 所以退出时**主动写回**原值：资产内存与磁盘内容重新一致，
    /// 之后即使被保存也不会带进我们的临时参数。
    ///
    /// 【为什么不能改成"克隆一份 profile 再赋给 volume"】
    /// 那样确实完全不碰资产，但会**打断天气系统的夜晚压暗**：
    /// `EnvironmentState.BindPostProcess()` 缓存的是 `sharedProfile` 里的
    /// `ColorGrading`，它每帧往那份写 `postExposure`。一旦把 volume 的
    /// `sharedProfile` 换成我们的克隆，相机读克隆、天气仍写原资产，
    /// 夜晚就不再压暗（表现为「夜里不暗、天空过曝」）。
    /// 而 `EnvironmentState.cs` 是本任务明令**禁止触碰**的文件。
    /// 两害相权：写同一份对象 + 退出还原，是唯一能同时满足"夜晚压暗有效"
    /// 与"不落盘"的做法。
    /// </summary>
    internal static void Shutdown()
    {
        // 协程被外部销毁（退出 Play / 场景切换 / 手动 Destroy 承载壳）时，
        // `ProbeRoutine` 的还原段不会再执行。若此时 `_capturing` 停在 true，
        // `Tick()` 会永久早退 —— 表现为"光晕突然再也不出现了，重进 Play 才好"。
        // 这类"协程中途被杀导致静态标记卡死"的坑，必须在这里兜住。
        _capturing = false;
        AbortMeasure();
        Restore();
    }

    /// <summary>自检摘要（验收时往 Console 贴一条就能看清状态与最终参数）。</summary>
    public static string Describe()
    {
        if (!Enabled) return "[TwinLampBloom] 已停用。";
#if UNITY_POST_PROCESSING_STACK_V2
        if (!_ready)
            return "[TwinLampBloom] 未就绪：" + (_failReason ?? "未知原因");
        return "[TwinLampBloom] 就绪。profile=" + _profiles.Count + " 份" +
               "，当前档=" + EnvironmentState.TimeLabel(CurrentPhase()) +
               "，Bloom enabled=" + (_bloom != null && _bloom.enabled.value) +
               "，intensity=" + (_bloom != null ? _bloom.intensity.value.ToString("0.00") : "-") +
               "（实际倍率 " + (_bloom != null ? (Mathf.Pow(2f, _bloom.intensity.value / 10f) - 1f).ToString("0.000") : "-") + "）" +
               "，threshold=" + (_bloom != null ? _bloom.threshold.value.ToString("0.00") : "-") +
               "（有效起点 " + (_bloom != null ? (Mathf.GammaToLinearSpace(_bloom.threshold.value) *
                                                  (1f - _bloom.softKnee.value)).ToString("0.000") : "-") + "）" +
               "，softKnee=" + (_bloom != null ? _bloom.softKnee.value.ToString("0.00") : "-") +
               "，diffusion=" + (_bloom != null ? _bloom.diffusion.value.ToString("0.0") : "-");
#else
        return "[TwinLampBloom] 后处理包不存在（UNITY_POST_PROCESSING_STACK_V2 未定义），本层不生效。";
#endif
    }

    // =====================================================================
    // 核心
    // =====================================================================

    /// <summary>
    /// 当前昼夜档。读 `EnvironmentState`（与 `IndoorFillLight.TargetDayScale` 同口径：
    /// 自读档位、不订阅事件，`Instance` 缺失时按黄昏兜底）。
    /// </summary>
    private static TimeOfDay CurrentPhase()
    {
        EnvironmentState env = EnvironmentState.Instance;
        return env != null ? env.timeOfDay : TimeOfDay.Dusk;
    }

    /// <summary>
    /// 该档位是否允许 Bloom 动作。
    ///
    /// 【白天档必须完全不动】任务包硬约束「白天档观感零变化」，
    /// 而白天基线是 P35 v10 第 4 项已验收的成果。白天档原资产里
    /// `Bloom.enabled.value = 0`，我们保持它不动，就等于零变化。
    /// 黄昏/夜晚才开（黄昏 0.15、夜晚 0.50 是 `TwinLightingPreset` 里既有的
    /// 强度意图，这里沿用同一档位语义，只是把"真正打开"这件事补上）。
    /// </summary>
    private static bool AllowedInPhase(TimeOfDay t)
    {
        return t == TimeOfDay.Night || t == TimeOfDay.Dusk;
    }

    /// <summary>按档位给出目标强度。黄昏比夜晚弱（见 DuskIntensity 的说明）。</summary>
    private static float IntensityFor(TimeOfDay t)
    {
        return t == TimeOfDay.Night ? NightIntensity : DuskIntensity;
    }

    /// <summary>
    /// 诊断：测量「开 Bloom」与「关 Bloom」两帧的屏幕亮度分布，判定过曝到底来自哪一层。
    ///
    /// 【为什么要做这个测量，而不是继续调参数】
    /// 2026-11-13 22:00 主理人反馈教室 2-1 仍整屏白。逐像素实测：
    ///   走廊  p50=0.831、硬削顶 0%、饱和度 p50=0.183（保留木地板/蓝墙裙颜色）
    ///   教室  p50=1.000、硬削顶 80.9%、饱和度 p50=0.000（**纯灰白，毫无色彩**）
    /// Bloom 的合成是 `color += bloom * 白色`，它加进去的是**白光**，
    /// 所以被它铺白的区域饱和度必然归零 —— 这个特征与教室完全吻合。
    /// 但按 intensity 3.2 算，整屏平均只 +0.033，**单靠 Bloom 推不动 80.9% 的削顶**。
    /// 两个结论互相矛盾，说明教室本身已经接近或超过 1.0，Bloom 只是最后一根稻草。
    /// 用这个测量把两层的贡献拆开，避免继续瞎猜着调参数。
    ///
    /// 【用法】Play 中站在教室 2-1，Console 粘：
    ///   TwinLampBloom.Measure("教室2-1")
    /// 它会先关 Bloom 截一帧、再开回来截一帧，各自统计后还原状态。
    /// </summary>
    public static void Measure(string tag)
    {
#if !UNITY_POST_PROCESSING_STACK_V2
        Debug.LogWarning("[TwinLampBloom] 后处理包不存在，无需测量。");
#else
        if (_measureRoutine != null) return;

        // 承载协程的小壳：静态类不能直接 StartCoroutine，必须有一个 MonoBehaviour 实例。
        GameObject go = new GameObject("[TwinLampBloom.Measure]");
        go.hideFlags = HideFlags.HideAndDontSave;
        _measureHost = go.AddComponent<TwinLampBloomMeasureHost>();
        _measureRoutine = _measureHost.StartCoroutine(MeasureRoutine(tag));
#endif
    }

    /// <summary>
    /// 【诊断用 · 一次 play 定案】2×2 因子探测：把「Bloom」与「灯管点光源」的账彻底分开。
    ///
    /// 【为什么需要它 —— 不要再让主理人反复试四轮】
    /// `Measure` 只能回答"Bloom 占多少"，`SetGlowsEnabled` 只能回答"点光源占多少"，
    /// 而且**两个实验分开做、每次都要重新摆机位**，机位一换数据就不可比。
    /// 更麻烦的是这两个因素会**互相放大**：点光源把天花板推到阈值之上，
    /// Bloom 再把它模糊叠回来 —— 单独测任何一个都会低估它们合起来的破坏力。
    ///
    /// 所以这里做一次**完整的 2×2 因子设计**，同一机位连拍四帧：
    ///
    /// |          | 点光源 开 | 点光源 关 |
    /// |----------|-----------|-----------|
    /// | Bloom 开 |    A      |     D     |
    /// | Bloom 关 |    B      |     C     |
    ///
    /// 于是可以精确分解（用削顶率或上半屏 p50 任一项）：
    ///   · 点光源主效应（Bloom 关时）= B − C   ← 纯净的光源贡献
    ///   · 点光源主效应（Bloom 开时）= A − D
    ///   · Bloom 主效应（光源开时）  = A − B
    ///   · Bloom 主效应（光源关时）  = D − C
    ///   · 交互项（互相放大）        = (A − B) − (D − C)
    ///     ↑ 这一项如果显著为正，说明"点光源先把天花板推过阈值、Bloom 再叠回来"，
    ///       两者是**共犯**，只修一个都不够。这正是 22:00 那组数据的形态。
    ///
    /// 【用法】Play 中站好机位，Console 粘：
    ///   TwinLampBloom.Probe("教室1F");
    /// 四帧拍完自动还原（Bloom 与所有点光源都恢复原状），控制台打印对照表。
    /// </summary>
    public static void Probe(string tag)
    {
#if !UNITY_POST_PROCESSING_STACK_V2
        Debug.LogWarning("[TwinLampBloom] 后处理包不存在，无需探测。");
#else
        if (_measureRoutine != null)
        {
            Debug.LogWarning("[TwinLampBloom] 上一次测量还没跑完，请稍等。");
            return;
        }

        GameObject go = new GameObject("[TwinLampBloom.Probe]");
        go.hideFlags = HideFlags.HideAndDontSave;
        _measureHost = go.AddComponent<TwinLampBloomMeasureHost>();
        _measureRoutine = _measureHost.StartCoroutine(ProbeRoutine(tag));
#endif
    }

#if UNITY_POST_PROCESSING_STACK_V2
    private static UnityEngine.Coroutine _measureRoutine;
    private static TwinLampBloomMeasureHost _measureHost;

    private static System.Collections.IEnumerator MeasureRoutine(string tag)
    {
        yield return null;   // 等一帧，确保相机已完成一次完整渲染

        int sw = Screen.width, sh = Screen.height;
        Texture2D tex = new Texture2D(sw, sh, TextureFormat.RGB24, false);

        // ── 第一帧：当前状态（Bloom 开着）──
        yield return new WaitForEndOfFrame();
        tex.ReadPixels(new Rect(0, 0, sw, sh), 0, 0);
        tex.Apply();
        Shot on = Sample(tex);

        // ── 第二帧：强制关掉 Bloom ──
        bool wasEnabled = Enabled;
        _capturing = true;               // 停掉 Tick，防止它把 Bloom 又打开
        Restore();                       // 直接把参数写回进场原值（enabled=false）
        yield return null;
        yield return new WaitForEndOfFrame();
        tex.ReadPixels(new Rect(0, 0, sw, sh), 0, 0);
        tex.Apply();
        Shot off = Sample(tex);

        // ── 还原 ──
        UnityEngine.Object.Destroy(tex);
        _capturing = false;
        if (wasEnabled) Apply(true);

        // 【为什么同时报"削顶比例"和"天花板区亮度"，而不是只报平均值】
        // 2026-11-13 实测教室截图：整体 p50=0.806 看起来不算太糟，
        // 但切块一看 **天花板 p50=0.955、53.9% 像素 >0.95、22.9% 硬削顶**，
        // 而左侧灰墙只有 0.682。平均值把"天花板被推白"这件事完全平均掉了。
        // 所以测量必须**分区域 + 报削顶率**，否则测完还是看不出病灶在哪。
        Debug.Log("[TwinLampBloom] 测量【" + tag + "】\n" +
                  "  ┌─ 开 Bloom ：" + on.Describe() + "\n" +
                  "  ├─ 关 Bloom ：" + off.Describe() + "\n" +
                  "  └─ 差值     ：平均 " + (on.mean - off.mean).ToString("+0.0000;-0.0000") +
                  "，削顶 " + ((on.clip - off.clip) * 100f).ToString("+0.0;-0.0") + " 个百分点\n" +
                  "  ⇒ " + Verdict(on, off));

        _measureRoutine = null;
        if (_measureHost != null) UnityEngine.Object.Destroy(_measureHost.gameObject);
        _measureHost = null;
    }

    /// <summary>2×2 因子探测的主体。四帧连拍，最后无条件还原。</summary>
    private static System.Collections.IEnumerator ProbeRoutine(string tag)
    {
        yield return null;   // 等一帧，确保相机已完成一次完整渲染

        int sw = Screen.width, sh = Screen.height;
        Texture2D tex = new Texture2D(sw, sh, TextureFormat.RGB24, false);

        // ── 记录所有 glow 的原始开关状态 ────────────────────────────────
        //
        // 【为什么必须记原状，而不是最后统一全部打开】
        // 这个工程里灯是**逐盏开关**的（用户在交互里点灯）。探测时可能有 12 盏亮、
        // 6 盏灭。如果结束时无脑全开，就会把用户原本关着的灯全部点亮 ——
        // 而且这是"运行时状态"，用户很可能会以为是 bug，排查起来极难定位。
        // 所以先拍快照，最后按快照逐个还原。
        Light[] glows = FindGlows();
        bool[] glowWas = new bool[glows.Length];
        for (int i = 0; i < glows.Length; i++) glowWas[i] = glows[i].enabled;

        // Bloom 的进场状态也要记：最后还原时不能无脑 `Apply(true)`，
        // 因为用户可能已经用 `SetEnabled(false)` 关过光晕了。
        bool bloomWas = Enabled;

        _capturing = true;   // 停掉 Tick，否则它每帧都会把 Bloom 重新打开

        // 抓一帧的公共动作：`ReadPixels` 必须在帧末做，所以每次切完状态先 yield 两帧。
        // （不能封成方法：静态方法不能 yield，封装后就没法把等待放进去了。）

        // ── A：Bloom 开 · 点光源 全开（最坏情况，等于"全楼灯都点着"那一帧）──
        SetBloomLive(true);
        SetGlows(glows, true);
        yield return null;
        yield return new WaitForEndOfFrame();
        tex.ReadPixels(new Rect(0, 0, sw, sh), 0, 0); tex.Apply();
        Shot a = Sample(tex);

        // ── B：Bloom 关 · 点光源 全开 ──
        SetBloomLive(false);
        SetGlows(glows, true);
        yield return null;
        yield return new WaitForEndOfFrame();
        tex.ReadPixels(new Rect(0, 0, sw, sh), 0, 0); tex.Apply();
        Shot b = Sample(tex);

        // ── C：Bloom 关 · 点光源 全关 ──
        SetBloomLive(false);
        SetGlows(glows, false);
        yield return null;
        yield return new WaitForEndOfFrame();
        tex.ReadPixels(new Rect(0, 0, sw, sh), 0, 0); tex.Apply();
        Shot c = Sample(tex);

        // ── D：Bloom 开 · 点光源 全关 ──
        SetBloomLive(true);
        SetGlows(glows, false);
        yield return null;
        yield return new WaitForEndOfFrame();
        tex.ReadPixels(new Rect(0, 0, sw, sh), 0, 0); tex.Apply();
        Shot d = Sample(tex);

        // ── 无条件还原（Bloom 按进场状态，灯逐盏按快照）──
        _capturing = false;
        SetBloomLive(bloomWas);
        for (int i = 0; i < glows.Length; i++)
        {
            if (glows[i] != null) glows[i].enabled = glowWas[i];
        }
        UnityEngine.Object.Destroy(tex);

        // 【判据用哪一项：优先"上半屏削顶率"，退化才用"上半屏 p50"】
        // 原因见 `Shot` 的注释：天花板爆炸时 p50 会顶到 1.000 饱和，
        // 一旦饱和就再也分不出 0.05 和 0.20 的差别；而削顶率没有上限，仍有分辨力。
        bool useClip = (a.ceilingClip > 0.005f) || (b.ceilingClip > 0.005f) ||
                       (c.ceilingClip > 0.005f) || (d.ceilingClip > 0.005f);

        float scale = useClip ? 100f : 1f;
        string unit = useClip ? " pt" : "";
        float pick(Shot s) { return useClip ? s.ceilingClip : s.ceilingP50; }
        string F(float v) { return (v * scale).ToString("+0.0;-0.0;0.0") + unit; }

        float lampWithBloom = pick(a) - pick(d);   // A − D
        float lampNoBloom = pick(b) - pick(c);     // B − C
        float bloomLampOn = pick(a) - pick(b);     // A − B
        float bloomLampOff = pick(d) - pick(c);    // D − C
        float interaction = bloomLampOn - bloomLampOff;

        // 【结论怎么下 —— 三个来源各自的典型形态】
        //   点光源两个主效应都大  ⇒ 病在光，Bloom 只是陪衬。降 Bloom 没用。
        //   Bloom 两个主效应都大    ⇒ 病在 Bloom 参数。但注意：交互项大时不能这么读。
        //   交互项显著为正          ⇒ 共犯：光把天花板推过阈值，Bloom 再叠回来。
        //                              必须**同时**动两边，或直接砍掉一边。
        string verdict;
        if (lampWithBloom > 0.03f && lampNoBloom > 0.03f)
        {
            verdict = "**点光源是主因**：无论 Bloom 开关，关掉 TwinLampGlow 都让上半屏降 " +
                      F(lampNoBloom) + "（Bloom 关时）。\n" +
                      "      ⇒ 方向：该房间需要「点光源 / 补光」二选一，不是继续调 Bloom 参数。";
        }
        else if (bloomLampOn > 0.03f && bloomLampOff > 0.03f)
        {
            verdict = "**Bloom 是主因**：无论点光源开关，关掉 Bloom 都让上半屏降 " +
                      F(bloomLampOn) + "。\n" +
                      "      ⇒ 方向：继续降 intensity / 抬 threshold（但会一起削掉灯管光晕）。";
        }
        else if (interaction > 0.03f)
        {
            verdict = "**两者是共犯**：Bloom 的贡献在点光源开着时是 " + F(bloomLampOn) +
                      "，关掉点光源后只剩 " + F(bloomLampOff) + "，交互项 " + F(interaction) + "。\n" +
                      "      ⇒ 点光源先把天花板推过阈值，Bloom 再把它模糊叠回来。只修一边都不够。";
        }
        else
        {
            verdict = "**两者贡献都不显著**（点光源 " + F(lampNoBloom) +
                      "，Bloom " + F(bloomLampOn) + "）⇒ 病根在别处：\n" +
                      "      补光强度 / 材质反照率 / 曝光。本层参数怎么调都不会有改善，应停止在此处调参。";
        }

        Debug.Log(
            "[TwinLampBloom] 2×2 因子探测【" + tag + "】  判据=" +
            (useClip ? "上半屏削顶率" : "上半屏 p50（无削顶，已退化）") + "\n" +
            "  ┌ A  Bloom开·光源开  " + a.Describe() + "\n" +
            "  ├ B  Bloom关·光源开  " + b.Describe() + "\n" +
            "  ├ C  Bloom关·光源关  " + c.Describe() + "\n" +
            "  ├ D  Bloom开·光源关  " + d.Describe() + "\n" +
            "  ├─────────────────────────────────────────\n" +
            "  ├ 点光源主效应（Bloom开） A−D = " + F(lampWithBloom) + "\n" +
            "  ├ 点光源主效应（Bloom关） B−C = " + F(lampNoBloom) + "   ← 纯净的光源账\n" +
            "  ├ Bloom主效应（光源开）   A−B = " + F(bloomLampOn) + "   ← 主理人实际承受的\n" +
            "  ├ Bloom主效应（光源关）   D−C = " + F(bloomLampOff) + "\n" +
            "  └ 交互项 (A−B)−(D−C)         = " + F(interaction) + "   ← >0 即互相放大\n" +
            "  ⇒ " + verdict);

        _measureRoutine = null;
        if (_measureHost != null) UnityEngine.Object.Destroy(_measureHost.gameObject);
        _measureHost = null;
    }

    /// <summary>把 Bloom 切到「运行值 / 进场原值」。探测用，不改 `Enabled` 语义。</summary>
    private static void SetBloomLive(bool live)
    {
        if (live)
        {
            Apply(true);
        }
        else
        {
            Restore();
            // 【必须清掉 _applied】`Restore()` 只把参数写回原值，不动这个标记。
            // 若不清理，`_applied == true` 且档位没变时，后续 `Tick()` 的
            // `Apply(false)` 会早退 —— 于是"人为关掉"变成"永久关掉"，
            // 用户会以为光晕坏了。这已经是本项目第二次踩「状态标记与对象不匹配」
            // （另一次是 `LampVisual.HasApplied`）。
            _applied = false;
        }
    }

    /// <summary>把灯管点光源全部开关。与公开的 `SetGlowsEnabled` 同口径，但不打日志。</summary>
    private static void SetGlows(Light[] glows, bool on)
    {
        for (int i = 0; i < glows.Length; i++)
        {
            if (glows[i] != null) glows[i].enabled = on;
        }
    }

    /// <summary>一次测量的统计结果。</summary>
    private struct Shot
    {
        public float mean;        // 全屏平均亮度
        public float p50;         // 中位数
        public float clip;        // 硬削顶比例（RGB 全 ≥254）
        public float ceilingP50;  // 画面上部 1/3 的中位亮度（≈天花板/墙面上半）
        public float ceilingClip; // 画面上部 1/3 的削顶比例

        public string Describe()
        {
            return "平均 " + mean.ToString("0.000") +
                   "，p50 " + p50.ToString("0.000") +
                   "，削顶 " + (clip * 100f).ToString("0.0") + "%" +
                   "，上半屏 p50 " + ceilingP50.ToString("0.000") +
                   "／削顶 " + (ceilingClip * 100f).ToString("0.0") + "%";
        }
    }

    private static string Verdict(Shot on, Shot off)
    {
        if (off.clip > 0.10f)
            return "关掉 Bloom 仍有 " + (off.clip * 100f).ToString("0.0") +
                   "% 削顶 ⇒ **过曝主因不在 Bloom**，是灯/补光本身太亮。调本层参数无效。";
        if (off.mean > 0.75f)
            return "关掉 Bloom 后平均仍 >0.75 ⇒ Bloom 不是主因，光源偏强。";
        if (on.clip - off.clip > 0.05f)
            return "Bloom 让削顶增加 " + ((on.clip - off.clip) * 100f).ToString("0.0") +
                   " 个百分点 ⇒ **Bloom 是主因**，应继续降低 intensity / 抬 threshold。";
        return "Bloom 贡献很小（平均 " + (on.mean - off.mean).ToString("+0.000;-0.000") +
               "，削顶 " + ((on.clip - off.clip) * 100f).ToString("+0.0;-0.0") +
               " pt）⇒ 这个机位下本层基本无害。";
    }

    /// <summary>
    /// 统计一帧。4 像素步长采样（全量逐像素在 1080p 下约 200 万次 GetPixel，太慢）。
    /// 坐标注意：`ReadPixels` 的原点在**左下角**，而我们要判的"上半屏（天花板）"
    /// 在 y 的高值侧 —— 所以判 `y > h * 2/3`。
    /// </summary>
    private static Shot Sample(Texture2D tex)
    {
        int w = tex.width, h = tex.height;
        Shot s = new Shot();

        float sum = 0f;
        int n = 0, clipN = 0;
        int ceilN = 0, ceilClipN = 0;
        int ceilY0 = h * 2 / 3;   // 图像上部 1/3（ReadPixels 原点在左下）

        // 直方图求 p50（256 桶）
        int[] hist = new int[256];
        int[] ceilHist = new int[256];

        for (int y = 0; y < h; y += 4)
        {
            bool isCeil = y >= ceilY0;
            for (int x = 0; x < w; x += 4)
            {
                Color c = tex.GetPixel(x, y);
                float lum = 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
                int bin = Mathf.Clamp((int)(lum * 255f), 0, 255);

                sum += lum;
                hist[bin]++;
                n++;
                bool clipped = c.r >= 0.996f && c.g >= 0.996f && c.b >= 0.996f;
                if (clipped) clipN++;

                if (isCeil)
                {
                    ceilHist[bin]++;
                    ceilN++;
                    if (clipped) ceilClipN++;
                }
            }
        }

        s.mean = n > 0 ? sum / n : 0f;
        s.clip = n > 0 ? (float)clipN / n : 0f;
        s.p50 = Median(hist, n);
        s.ceilingP50 = Median(ceilHist, ceilN);
        s.ceilingClip = ceilN > 0 ? (float)ceilClipN / ceilN : 0f;
        return s;
    }

    private static float Median(int[] hist, int total)
    {
        if (total <= 0) return 0f;
        int half = total / 2, acc = 0;
        for (int i = 0; i < hist.Length; i++)
        {
            acc += hist[i];
            if (acc >= half) return i / 255f;
        }
        return 1f;
    }
#endif

    /// <summary>
    /// 清掉测量协程的静态引用。**宏外必须有一份**，因为 `Shutdown()` 在宏外，
    /// 而 `_measureRoutine` / `_measureHost` 只有宏内才声明。
    /// </summary>
    private static void AbortMeasure()
    {
#if UNITY_POST_PROCESSING_STACK_V2
        _measureRoutine = null;
        if (_measureHost != null)
        {
            UnityEngine.Object.Destroy(_measureHost.gameObject);
            _measureHost = null;
        }
#endif
    }

    private static void Apply(bool force)
    {
#if UNITY_POST_PROCESSING_STACK_V2
        if (!_ready)
        {
            // 失败后 1 秒内不重试：避免「场景里没有 Volume」时每帧一次全场景扫描
            if (Time.unscaledTime < _retryAt) return;
            if (!Bind()) { _retryAt = Time.unscaledTime + RetryInterval; return; }

            // 【必须清掉"已应用"标记】重新绑定成功后，`_applied` 可能还是上一次绑定的 true，
            // 而档位又恰好没变 —— 那样下面的早退会让**新绑定的一份 profile 从来没被写过**，
            // 表现为"绑定日志说成功了、画面却完全没有光晕"。这类"状态标记与对象不匹配"
            // 的坑与本文件 `LampVisual.HasApplied` 记的是同一类错误。
            _applied = false;
        }

        TimeOfDay phase = CurrentPhase();
        bool allowed = AllowedInPhase(phase);

        if (!force && _applied && _appliedPhase == phase) return;   // 档位没变，不重复写

        if (!allowed)
        {
            // 白天档：恢复原样，一点不留
            Restore();
            _appliedPhase = phase;
            _applied = true;
            return;
        }

        float target = IntensityFor(phase);

        // 参数只在**首次**（或 force）时设，档位间只改 intensity ——
        // threshold/diffusion 是"外观口径"，不需要每档变，少写少出错。
        if (!_origCaptured || force)
        {
            CaptureOriginal();
            for (int i = 0; i < _profiles.Count; i++)
            {
                Bloom bl = GetBloom(_profiles[i]);
                if (bl == null) continue;
                bl.enabled.overrideState = true;
                bl.threshold.overrideState = true;
                bl.diffusion.overrideState = true;
                bl.softKnee.overrideState = true;
                bl.threshold.value = BloomThreshold;
                bl.diffusion.value = BloomDiffusion;
                bl.softKnee.value = BloomSoftKnee;
            }
        }

        for (int i = 0; i < _profiles.Count; i++)
        {
            Bloom bl = GetBloom(_profiles[i]);
            if (bl == null) continue;
            bl.intensity.overrideState = true;
            bl.intensity.value = target;
            bl.enabled.value = true;
        }

        _appliedPhase = phase;
        _applied = true;

        Debug.Log("[TwinLampBloom] Bloom 已开启：" + EnvironmentState.TimeLabel(phase) +
                  " 档，intensity=" + target.ToString("0.00") +
                  "（实际倍率 " + (Mathf.Pow(2f, target / 10f) - 1f).ToString("0.000") + "）" +
                  "，threshold=" + BloomThreshold.ToString("0.00") +
                  "，diffusion=" + BloomDiffusion.ToString("0.0") +
                  "，profile=" + _profiles.Count + " 份。");
#else
        _failReason = "后处理包不存在";
#endif
    }

#if UNITY_POST_PROCESSING_STACK_V2
    /// <summary>
    /// 挂上场景里所有 global Volume 的 `sharedProfile`。
    ///
    /// 【为什么是"所有"而不是第一个】实测 Day 场景有 **2 个** global Volume
    /// （L34172 / L56592），都是 `isGlobal:1 / weight:1 / priority:10`，
    /// 且共用同一份 `PPS_Bloom.asset`（guid d19e4711…）。
    /// `PostProcessManager` 会对每个 global Volume 依次 `OverrideSettings`，
    /// 后写的覆盖先写的；只开一个、另一个仍写着 `enabled=0`，结果就是"等于没开"。
    /// 所以必须遍历全部。
    ///
    /// 【绝不访问 `volume.profile`】见文件头 ②：那个 getter 会克隆并缓存，
    /// 一访问就把 EnvironmentState 的夜晚压暗架空。这里只读 `sharedProfile`。
    /// </summary>
    private static bool Bind()
    {
        _ready = false;
        _failReason = null;
        _profiles.Clear();
        _bloom = null;

        PostProcessVolume[] vols = UnityEngine.Object.FindObjectsOfType<PostProcessVolume>();
        if (vols == null || vols.Length == 0)
        {
            _failReason = "场景里没有 PostProcessVolume（检查相机是否挂了 PostProcessLayer、Volume 是否在 volumeLayer 里）";
            return false;
        }

        int withBloom = 0;
        for (int i = 0; i < vols.Length; i++)
        {
            PostProcessVolume vol = vols[i];
            if (vol == null || !vol.isGlobal) continue;

            // ❗只碰 sharedProfile，绝不去读 vol.profile（那个 getter 有副作用）
            PostProcessProfile prof = vol.sharedProfile;
            if (prof == null) continue;

            Bloom bl = GetBloom(prof);
            if (bl == null) continue;

            if (!_profiles.Contains(prof)) _profiles.Add(prof);
            if (_bloom == null) _bloom = bl;
            withBloom++;
        }

        if (_profiles.Count == 0)
        {
            _failReason = "找到 " + vols.Length + " 个 Volume，但没有一个的 sharedProfile 里含 Bloom";
            return false;
        }

        _ready = true;
        // 记下本次绑定的档位基线：`_appliedPhase` 已在上面被清成无效值（见 Apply 里的说明）
        _appliedPhase = (TimeOfDay)(-1);
        Debug.Log("[TwinLampBloom] 已绑定 " + _profiles.Count + " 份 profile" +
                  "（场景内 Volume 共 " + vols.Length + " 个，含 Bloom 的 " + withBloom + " 个）。");
        return true;
    }

    /// <summary>
    /// 从 profile 里取 Bloom 设置块。**只读，绝不 `AddSettings`**。
    ///
    /// 【为什么不能 AddSettings】
    /// `PostProcessProfile.AddSettings` 会把新建的 effect `settings.Add(...)` 进
    /// 传入的那个 profile 对象，并置 `isDirty = true`。
    /// 而这里传进去的是 `sharedProfile` —— **资产本体**。那等于在运行时改资产、
    /// 并把资产标记为已修改（编辑器里会被保存、打包时会被带进构建），
    /// 直接违反任务包「不落盘、不改 `.mat`/资产」的硬约束。
    /// （而且 `AddSettings` 在同类型已存在时会抛 `InvalidOperationException`，
    ///   套在每帧路径里就是一个定时炸弹。）
    ///
    /// 本工程实测 `PPS_Bloom.asset` 里 Bloom 是存在的，所以只读就够了；
    /// 万一将来被删掉，就返回 null、本层安静地不生效（而不是偷偷往资产里加东西）。
    /// </summary>
    private static Bloom GetBloom(PostProcessProfile prof)
    {
        if (prof == null) return null;
        Bloom bl;
        return prof.TryGetSettings(out bl) ? bl : null;
    }

    /// <summary>记下进场原值，供 `Restore()` 回滚。只记一次。</summary>
    private static void CaptureOriginal()
    {
        if (_origCaptured) return;
        if (_profiles.Count == 0) return;

        Bloom bl = GetBloom(_profiles[0]);
        if (bl == null) return;

        _origEnabled = bl.enabled.value;
        _origEnabledOverride = bl.enabled.overrideState;
        _origIntensity = bl.intensity.value;
        _origIntensityOverride = bl.intensity.overrideState;
        _origThreshold = bl.threshold.value;
        _origThresholdOverride = bl.threshold.overrideState;
        _origDiffusion = bl.diffusion.value;
        _origDiffusionOverride = bl.diffusion.overrideState;
        _origSoftKnee = bl.softKnee.value;
        _origSoftKneeOverride = bl.softKnee.overrideState;
        _origCaptured = true;
    }

    /// <summary>恢复到进场原值（白天档 / 一键回滚都走这里）。</summary>
    private static void Restore()
    {
        if (!_origCaptured) return;
        if (_profiles.Count == 0) return;

        for (int i = 0; i < _profiles.Count; i++)
        {
            Bloom bl = GetBloom(_profiles[i]);
            if (bl == null) continue;

            bl.enabled.value = _origEnabled;
            bl.enabled.overrideState = _origEnabledOverride;
            bl.intensity.value = _origIntensity;
            bl.intensity.overrideState = _origIntensityOverride;
            bl.threshold.value = _origThreshold;
            bl.threshold.overrideState = _origThresholdOverride;
            bl.diffusion.value = _origDiffusion;
            bl.diffusion.overrideState = _origDiffusionOverride;
            bl.softKnee.value = _origSoftKnee;
            bl.softKnee.overrideState = _origSoftKneeOverride;
        }
    }
#else
    // 宏未定义时 `SetEnabled` / `Shutdown` 仍会调 `Restore()`。
    // 没有这个空实现，包缺失的工程会直接编译失败 —— 而"缺失时也应该能编译"
    // 正是本文件全部 `#if` 分支存在的意义。
    private static void Restore() { }
#endif
}

// ══════════════════════════════════════════════════════════════════════════
// 测量用的协程宿主
// ══════════════════════════════════════════════════════════════════════════

/// <summary>
/// 只为 `TwinLampBloom.Measure()` 提供 StartCoroutine 的载体。
///
/// 【为什么必须是独立的顶层类】
/// Unity 不允许把 MonoBehaviour 写成嵌套类（`AddComponent` 会失败）。
/// 也不能复用 `TwinLampBloomRunner` —— 那个是常驻的，测量只是临时对象，
/// 用完立刻 Destroy，不污染运行期结构。
///
/// 只在宏存在时才编译：宏未定义时 `TwinLampBloom.Measure` 本身也是空实现，
/// 不会引用到本类。
/// </summary>
#if UNITY_POST_PROCESSING_STACK_V2
[AddComponentMenu("")]
internal sealed class TwinLampBloomMeasureHost : MonoBehaviour { }
#endif

// ══════════════════════════════════════════════════════════════════════════
// 每帧驱动
// ══════════════════════════════════════════════════════════════════════════

/// <summary>
/// 让 Bloom 跟随昼夜档的极轻量驱动器。
///
/// 【为什么需要它】灯管亮/灭是事件驱动（`ApplyLamp`），但**天气档切换不是**：
/// 玩家在白天开了灯、再切到夜晚，`ApplyLamp` 不会再被调用（灯一直是"开"，
/// `HasApplied && IsOn == on` 直接早退），Bloom 就永远留在白天档的关闭状态。
/// 所以补一个每帧检查档位的小壳。
///
/// 【开销】每帧一次 `EnvironmentState.Instance` 取字段 + 一次枚举比较，
/// 档位没变时立刻返回，不碰任何后处理参数。
/// </summary>
[AddComponentMenu("")]
internal sealed class TwinLampBloomRunner : MonoBehaviour
{
    private static TwinLampBloomRunner _instance;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        _instance = null;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
#if UNITY_POST_PROCESSING_STACK_V2
        // ── 挂载条件：只认「当前场景里存在 PostProcessVolume 或 PostProcessLayer」──
        //
        // 【为什么不用 TwinRuntime.Instance 当判据】
        // 本工程所有自举钩子都是 `RuntimeInitializeOnLoadMethod(AfterSceneLoad)`，
        // 它们之间的执行顺序 Unity **不保证**。用它当判据会变成"谁先跑谁赢"，
        // 在别人的自举顺序调整时静默失效 —— 这类 bug 极难排查。
        // 改查后处理组件：它在 Day 场景里客观存在（静态）、与任何脚本的执行顺序无关。
        //
        // 【为什么不干脆无脑挂】Night 场景实测没有任何后处理组件，
        // 在那里挂一个什么都不做的小壳没有意义；而且 `Tick` 里那句
        // `EnvironmentState.Instance` 也白跑。用组件存在性做闸门最省。
        if (_instance != null) return;

        bool hasPP = UnityEngine.Object.FindObjectOfType<UnityEngine.Rendering.PostProcessing.PostProcessVolume>() != null
                  || UnityEngine.Object.FindObjectOfType<UnityEngine.Rendering.PostProcessing.PostProcessLayer>() != null;
        if (!hasPP) return;

        GameObject go = new GameObject("[TwinLampBloom]");
        _instance = go.AddComponent<TwinLampBloomRunner>();
#endif
    }

    private void Update()
    {
        TwinLampBloom.Tick();
        HandleHotkeys();
    }

    /// <summary>
    /// 诊断热键。
    ///
    /// 【为什么必须有 —— 2026-11-13 的教训】
    /// 我之前一直用"Console 里粘 `TwinLampBloom.Probe("教室1F")`"这种方式让主理人跑诊断。
    /// 结果主理人把命令粘进了 Console 的**搜索过滤框**，Unity 显示
    /// `No results for "TwinLampBloom.Probe(..."` —— 命令一次都没执行过。
    /// Console 窗口是日志查看器，**不具备执行 C# 的能力**。
    /// 所以诊断入口不能是"贴代码"，必须是**按键**或**自动运行**。
    ///
    /// 键位选 F9~F12 区间：避开工程里已用的 F1(chassis) / F3(天气) / F4(闭锁开关)。
    /// </summary>
    private void HandleHotkeys()
    {
        // F11：跑 2×2 因子探测（Bloom × 点光源），Console 打印完整对照表
        if (Input.GetKeyDown(KeyCode.F11))
        {
            TwinLampBloom.Probe("当前机位");
        }

        // F12：就地开关灯管点光源，肉眼直接对比天花板变化
        if (Input.GetKeyDown(KeyCode.F12))
        {
            TwinLampBloom.SetGlowsEnabled(!TwinLampBloom.GlowsEnabled);
        }

        // F9：一键开关本层光晕（验收用）
        if (Input.GetKeyDown(KeyCode.F9))
        {
            TwinLampBloom.SetEnabled(!TwinLampBloom.Enabled);
        }

        // F10：灯管点光源**再下移 0.15m**（按住连按可逐级下移，观察天花板变化）
        if (Input.GetKeyDown(KeyCode.F10))
        {
            TwinLampBloom.ShiftGlows(-0.15f);
        }

        // F8：把下移量归零（回到 P13 原观感，用于对比）
        if (Input.GetKeyDown(KeyCode.F8))
        {
            TwinLampBloom.ShiftGlows(-TwinLampBloom.GlowShift);
        }

        // F7：补光层（IndoorFillLight）总开关 —— **这是分清"谁在照天花板"的关键实验**
        //
        // 【为什么必须有这个键】
        // 2026-11-13 主理人在走廊按 F10 毫无反应，因为走廊的 TwinLampGlow
        // 早被 IndoorFillLight 接管关闭了（每帧 `l.enabled = false`）。
        // 也就是说走廊天花板的光**全部来自补光层**，与点光源无关。
        // 但"教室"没被接管，那里是两套叠加。要分清各自的贡献，
        // 必须能把补光层单独关掉 —— 只关点光源（F12）是不够的，因为只剩补光时天花板依然亮，
        // 会让人误以为"点光源不是主因"。
        //
        // 正交组合：
        //   F12 关点光源 + 补光开 → 只剩补光，看它的贡献
        //   F7  关补光   + 点光源开 → 只剩点光源，看它的贡献
        //   两者都关 → 底噪（自发光/环境光）
        if (Input.GetKeyDown(KeyCode.F7))
        {
            _fillOff = !_fillOff;
            IndoorFillLight.SetEnabled(!_fillOff);
            Debug.Log("[TwinLampBloom] 补光层（IndoorFillLight）" + (_fillOff ? "已关闭" : "已恢复") +
                      "。现在只剩" + (_fillOff ? "灯管点光源" : "两套光叠加") + "在照天花板。");
        }
    }

    private bool _fillOff;

    /// <summary>
    /// 诊断面板。**必须缓存 GUIStyle** —— `OnGUI` 每次事件都会调用（一帧可能 2~4 次），
    /// 在里面 `new GUIStyle()` 会持续产生 GC 垃圾。本面板是常驻的，
    /// 这种"每帧几次的小分配"正是最难归因的掉帧来源。
    /// </summary>
    private GUIStyle _hudStyle;

    private void OnGUI()
    {
        // 只在 Play 里显示，且样式极简 —— 这是诊断面板，不是 UI 交付物。
        if (_hudStyle == null)
        {
            _hudStyle = new GUIStyle(GUI.skin.label);
            _hudStyle.fontSize = 12;
            _hudStyle.normal.textColor = Color.white;
        }

        int w = Screen.width;
        GUI.color = new Color(0f, 0f, 0f, 0.6f);
        GUI.DrawTexture(new Rect(w - 300f, 8f, 292f, 148f), Texture2D.whiteTexture);
        GUI.color = Color.white;

        GUI.Label(new Rect(w - 290f, 10f, 282f, 20f), "── 过曝诊断热键 ──", _hudStyle);
        GUI.Label(new Rect(w - 290f, 30f, 282f, 20f),
                  "F7   补光层：" + (_fillOff ? "已关" : "开") + "  ← 分清谁在照天花板", _hudStyle);
        GUI.Label(new Rect(w - 290f, 50f, 282f, 20f),
                  "F12  灯管点光源：" + (TwinLampBloom.GlowsEnabled ? "开" : "已关"), _hudStyle);
        GUI.Label(new Rect(w - 290f, 70f, 282f, 20f),
                  "F10  点光源下移（累计 " + (-TwinLampBloom.GlowShift).ToString("0.00") + "m）", _hudStyle);
        GUI.Label(new Rect(w - 290f, 90f, 282f, 20f), "F8   下移归零", _hudStyle);
        GUI.Label(new Rect(w - 290f, 110f, 282f, 20f),
                  "F11  2×2 定量探测（Console 看结果）", _hudStyle);
        GUI.Label(new Rect(w - 290f, 130f, 282f, 20f),
                  "F9   光晕层：" + (TwinLampBloom.Enabled ? "开" : "已关"), _hudStyle);
    }

    private void OnDestroy()
    {
        // 退出 Play / 换场景时把写进 sharedProfile 的参数还原，
        // 防止资产带着运行时临时值被保存（见 TwinLampBloom.Shutdown 的说明）。
        TwinLampBloom.Shutdown();
        if (_instance == this) _instance = null;
    }
}
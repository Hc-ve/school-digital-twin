// 昼夜光照预设 · 运行时切换入口
// 归属：打光（光照/氛围）　被断点调用接 UI 按钮
//
// 用法：
//   TwinLightingPreset.Apply(TwinLightingPresetId.Night);
//   var cur = TwinLightingPreset.Current;
//
// 只改光照与后处理参数，不动材质、不重烘、不切场景。

using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;

public enum TwinLightingPresetId
{
    Day = 0,
    Dusk = 1,
    Night = 2,
}

public static class TwinLightingPreset
{
    public static TwinLightingPresetId Current { get; private set; } = TwinLightingPresetId.Day;

    sealed class P
    {
        public Color sunColor; public float sunIntensity;
        public AmbientMode ambientMode;
        public Color ambientSky, ambientEquator, ambientGround; public Color ambientFlat;
        public float ambientIntensity;
        public bool fog; public FogMode fogMode; public Color fogColor; public float fogDensity;
        public float fogStart, fogEnd;
        public float postExposure, temperature, tint, saturation, contrast, postExposureLdr;
        public float vignette, bloomIntensity, bloomThreshold;
    }

    static readonly P Day = new P
    {
        sunColor = new Color(1f, 1f, 0.984f), sunIntensity = 1.0f,
        ambientMode = AmbientMode.Skybox,
        ambientSky = new Color(1f, 1f, 1f), ambientEquator = new Color(0.114f, 0.125f, 0.133f),
        ambientGround = new Color(0.047f, 0.043f, 0.035f), ambientFlat = new Color(0.65f, 0.70f, 0.78f),
        ambientIntensity = 1.0f,
        fog = false, fogMode = FogMode.ExponentialSquared, fogColor = new Color(0.5f, 0.5f, 0.5f),
        fogDensity = 0.01f, fogStart = 0f, fogEnd = 300f,
        postExposure = 0f, temperature = 0f, tint = 0f, saturation = 0f, contrast = 0f, postExposureLdr = 0f,
        vignette = 0.15f, bloomIntensity = 0f, bloomThreshold = 1.1f,
    };

    static readonly P Dusk = new P
    {
        sunColor = new Color(1f, 0.698f, 0.478f), sunIntensity = 0.55f,
        ambientMode = AmbientMode.Trilight,
        ambientSky = new Color(1f, 0.796f, 0.620f), ambientEquator = new Color(0.753f, 0.541f, 0.384f),
        ambientGround = new Color(0.353f, 0.251f, 0.188f), ambientFlat = new Color(0.85f, 0.68f, 0.52f),
        ambientIntensity = 0.85f,
        fog = true, fogMode = FogMode.Exponential, fogColor = new Color(0.851f, 0.635f, 0.467f),
        fogDensity = 0.006f, fogStart = 0f, fogEnd = 300f,
        postExposure = -0.30f, temperature = 15f, tint = 5f, saturation = -6f, contrast = 8f, postExposureLdr = 0f,
        vignette = 0.22f, bloomIntensity = 0.15f, bloomThreshold = 1.05f,
    };

    static readonly P Night = new P
    {
        sunColor = new Color(0.353f, 0.471f, 0.690f), sunIntensity = 0.10f,
        ambientMode = AmbientMode.Trilight,
        ambientSky = new Color(0.110f, 0.153f, 0.251f), ambientEquator = new Color(0.063f, 0.098f, 0.169f),
        ambientGround = new Color(0.024f, 0.039f, 0.071f), ambientFlat = new Color(0.14f, 0.19f, 0.30f),
        ambientIntensity = 0.50f,
        fog = true, fogMode = FogMode.ExponentialSquared, fogColor = new Color(0.039f, 0.071f, 0.125f),
        fogDensity = 0.008f, fogStart = 0f, fogEnd = 300f,
        postExposure = -1.05f, temperature = -18f, tint = -4f, saturation = -20f, contrast = 12f, postExposureLdr = 0f,
        vignette = 0.35f, bloomIntensity = 0.50f, bloomThreshold = 0.85f,
    };

    static P Get(TwinLightingPresetId id)
    {
        switch (id)
        {
            case TwinLightingPresetId.Dusk: return Dusk;
            case TwinLightingPresetId.Night: return Night;
            default: return Day;
        }
    }

    /// <summary>切换光照预设。一次调用完成，无每帧开销。</summary>
    public static void Apply(TwinLightingPresetId id)
    {
        var p = Get(id);

        // 1) 平行光（注意：当前 m_Lightmapping = Baked，运行时不产生实时光，
        //    这里仍写入是为了未来若切成 Mixed/Realtime 能直接生效）
        var sun = RenderSettings.sun;
        if (sun != null) { sun.color = p.sunColor; sun.intensity = p.sunIntensity; }

        // 2) 环境光（只作用于未烘焙物体 + 运行时解绑 lightmap 的结构件）
        RenderSettings.ambientMode = p.ambientMode;
        RenderSettings.ambientSkyColor = p.ambientSky;
        RenderSettings.ambientEquatorColor = p.ambientEquator;
        RenderSettings.ambientGroundColor = p.ambientGround;
        RenderSettings.ambientLight = p.ambientFlat;
        RenderSettings.ambientIntensity = p.ambientIntensity;

        // 3) 雾（屏幕空间效果，对烘焙表面同样生效）
        RenderSettings.fog = p.fog;
        RenderSettings.fogMode = p.fogMode;
        RenderSettings.fogColor = p.fogColor;
        RenderSettings.fogDensity = p.fogDensity;
        RenderSettings.fogStartDistance = p.fogStart;
        RenderSettings.fogEndDistance = p.fogEnd;

        // 4) 后处理（唯一能整体改变烘焙画面明暗的手段）
        ApplyPost(p);

        DynamicGI.UpdateEnvironment();
        Current = id;
    }

    static void ApplyPost(P p)
    {
        var vol = Object.FindObjectOfType<PostProcessVolume>();
        if (vol == null) return;

        // ══════════════════════════════════════════════════════════════════
        // 【P36 2026-11-13 修正：这里原来读的是 `vol.profile`，是个陷阱】
        //
        // `PostProcessVolume.profile` 的 getter 会 **克隆** `sharedProfile` 并缓存
        // （PPv2 PostProcessVolume.cs:120-138 → `m_InternalProfile`）。
        // 而 `EnvironmentState.BindPostProcess()` 写的是 `sharedProfile`（资产本体）。
        // 一旦本方法先跑（它访问了 profile），相机混合就改为只看那份克隆，
        // 此后 `EnvironmentState` 每帧写 `sharedProfile` 的夜晚压暗**永久失效** ——
        // 表现为「夜晚不暗、天空过曝」，而且极难定位（两边代码都"对"）。
        //
        // 所以统一改读 `sharedProfile`：与 `EnvironmentState` 写同一份对象，
        // 不存在克隆分叉。顺带也省掉每次调用的一次 profile 深拷贝。
        // ══════════════════════════════════════════════════════════════════
        var prof = vol.sharedProfile;
        if (prof == null) return;

        ColorGrading cg;
        if (!prof.TryGetSettings(out cg)) cg = prof.AddSettings<ColorGrading>();
        if (cg != null)
        {
            cg.gradingMode.value = GradingMode.HighDefinitionRange;   // LDR 模式下 postExposure 不参与计算
            cg.tonemapper.value = Tonemapper.Neutral;
            cg.postExposure.value = p.postExposure;

            // 【P36】这些参数原本**没有**把 `overrideState` 置 true，而 PPv2 的混合只
            // 处理 `overrideState == true` 的参数（`PostProcessLayer.OverrideSettings`）。
            // 不置 true 就等于"写了但没人读"。这里一并补上。
            cg.postExposure.overrideState = true;
            cg.gradingMode.overrideState = true;
            cg.tonemapper.overrideState = true;
            cg.temperature.overrideState = true;
            cg.tint.overrideState = true;
            cg.saturation.overrideState = true;
            cg.contrast.overrideState = true;

            cg.temperature.value = p.temperature;
            cg.tint.value = p.tint;
            cg.saturation.value = p.saturation;
            cg.contrast.value = p.contrast;
        }

        Vignette vg;
        if (!prof.TryGetSettings(out vg)) vg = prof.AddSettings<Vignette>();
        if (vg != null)
        {
            vg.intensity.value = p.vignette;
            vg.intensity.overrideState = true;
            vg.enabled.overrideState = true;
            vg.enabled.value = p.vignette > 0.001f;
        }

        Bloom bl;
        if (!prof.TryGetSettings(out bl)) bl = prof.AddSettings<Bloom>();
        if (bl != null)
        {
            bl.intensity.value = p.bloomIntensity;
            bl.threshold.value = p.bloomThreshold;

            // ══════════════════════════════════════════════════════════════
            // 【P36 关键修正：`active` 不是开关，`enabled.value` 才是】
            //
            // 原来写的是 `bl.active = ...`。但 PPv2 的
            // `PostProcessEffectSettings.IsEnabledAndSupported()` **只返回
            // `enabled.value`**；`active` 只表示"这组参数是否参与混合"，
            // 与"效果开不开"是两件事（源码 Effects/PostProcessEffectSettings.cs:94-97）。
            // 于是 `active = true` 写了等于没写，Bloom 一直是关的 ——
            // 这就是上一轮"调了 bloomIntensity 却完全没变化"的直接原因。
            //
            // 另外 `overrideState` 也必须为 true，否则混合阶段直接跳过这个参数。
            // ══════════════════════════════════════════════════════════════
            bl.intensity.overrideState = true;
            bl.threshold.overrideState = true;
            bl.enabled.overrideState = true;
            bl.enabled.value = p.bloomIntensity > 0.001f;
        }
    }
}

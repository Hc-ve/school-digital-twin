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
        var prof = vol.profile;
        if (prof == null) return;

        ColorGrading cg;
        if (!prof.TryGetSettings(out cg)) cg = prof.AddSettings<ColorGrading>();
        if (cg != null)
        {
            cg.gradingMode.value = GradingMode.HighDefinitionRange;   // LDR 模式下 postExposure 不参与计算
            cg.tonemapper.value = Tonemapper.Neutral;
            cg.postExposure.value = p.postExposure;
            cg.temperature.value = p.temperature;
            cg.tint.value = p.tint;
            cg.saturation.value = p.saturation;
            cg.contrast.value = p.contrast;
        }

        Vignette vg;
        if (!prof.TryGetSettings(out vg)) vg = prof.AddSettings<Vignette>();
        if (vg != null)
            vg.intensity.value = p.vignette;

        Bloom bl;
        if (!prof.TryGetSettings(out bl)) bl = prof.AddSettings<Bloom>();
        if (bl != null)
        {
            bl.intensity.value = p.bloomIntensity;
            bl.threshold.value = p.bloomThreshold;
            bl.active = p.bloomIntensity > 0.001f;
        }
    }
}

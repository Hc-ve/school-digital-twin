using System;
using System.Collections;
using System.Reflection;
using UnityEngine;

// ══════════════════════════════════════════════════════════════════════
// 【别名：修 CS0104 `'Random' is an ambiguous reference`】
//
// 本文件为 P13 的 postExposure 反射软依赖引入了 `using System`（需要 Type / Assembly），
// 于是 `Random` 同时命中两个类型：
//     UnityEngine.Random    —— static class，Range(int,int) / Range(float,float)，无状态
//     System.Random         —— 需要 new 的实例类，Next(...)，不能静态调用
// `Random.Range(...)` 两种写法名字一样，编译器不猜，直接报二义。
//
// 本文件里三处 `Random.Range` 全是"打雷间隔随机"的**视觉抖动**，语义上要的正是
// Unity 的全局随机（和 Time.deltaTime 同族的引擎侧工具），不是可复现的 System.Random 序列。
// 所以给 Unity 那个起别名，而不是删 using System（反射还要用）。
//
// 这是个"同型病"的又一例：给文件加一个 using，等于往名字空间里塞了一批候选名，
// 必须顺带扫一遍有没有与本文件已有标识符撞名的。上次 `Object[]` 撞了，这次 `Random` 撞了。
// ══════════════════════════════════════════════════════════════════════
using Random = UnityEngine.Random;

namespace SchoolTwin.Weather
{
    /// <summary>
    /// 全局环境状态机：一级「白天 / 夜晚」，二级「晴 / 多云 / 阴 / 雨 / 雷雨 / 雾 / 雪」。
    ///
    /// 一次切换会同时驱动：天空盒、平行光、环境光、内置雾、雨雪粒子、云穹顶、环境音。
    /// 所有参数在 transitionDuration 内**平滑插值**，不会"啪"地跳变。
    ///
    /// 三个关键实现决定（都是踩过的坑）：
    ///  1) **天空盒必须实例化一份**。场景自带的天空盒是磁盘上的资产，
    ///     直接改 `RenderSettings.skybox` 的 `_SkyTint` 会把 .mat 改脏、甚至写回磁盘。
    ///     这里 `new Material(原材质)` 得到运行期副本，只在内存里变。
    ///     （旧版代码因为"场景已有天空盒就直接 return"，导致昼夜切换时天空**从来没变过**。）
    ///  2) **雾用 RenderSettings.fog**。URP 14 没有内置体积雾，但支持线性/指数内置雾，
    ///     URP/Lit 与 Terrain 着色器都会采样它，代价几乎为零。
    ///  3) **DynamicGI.UpdateEnvironment() 要节流**。它会重算天空盒到 cubemap 的卷积，
    ///     每帧调会明显掉帧，这里 0.2 秒一次，过渡结束时再补一次。
    /// </summary>
    public class EnvironmentState : MonoBehaviour
    {
        public static EnvironmentState Instance;

        public TimeOfDay timeOfDay = TimeOfDay.Day;
        public Weather weather = Weather.Clear;

        [Tooltip("一次天气切换的过渡时长（秒）")]
        [SerializeField] private float transitionDuration = 2.4f;

        [Tooltip("是否显示云穹顶（关掉可以省一次大球的透明绘制）")]
        [SerializeField] private bool showClouds = true;

        [Tooltip("环境音总音量系数")]
        [SerializeField, Range(0f, 1f)] private float sfxScale = 0.55f;

        public event System.Action<TimeOfDay, Weather> OnChanged;

        // ---- 运行期引用 ----
        private Light sun;
        private Material skyMat;
        private Camera cam;

        // ---- 【P13】后处理曝光 ----
        //
        // 时间天气系统要在夜里把画面压暗。最干净的实现是 ColorGrading 的 postExposure
        // （单位 EV，纯亮度乘算），而不是改天空盒曝光或调每盏灯 ——
        // 那两者都会破坏已经烘焙好的光照关系。
        //
        // ⚠️ 这里用的是**反射式软依赖**，不是 `using UnityEngine.Rendering.PostProcessing`。
        //    理由：如果哪天有人把 com.unity.postprocessing 从 manifest 里删掉，
        //    直接引用会让**整个 Assembly-CSharp 编译失败**，全工程瘫痪；
        //    用反射则只是"夜天真不压暗"，其余功能全部正常。
        //    这个取舍是刻意的：后处理是锦上添花，不该成为编译门槛。
        private object ppVolume;          // PostProcessVolume
        private object ppGrading;         // ColorGrading
        private bool ppReady;
        private float ppSearchTimer;
        private float ppExposure;         // 当前已写入的 EV，变化超阈值才写
        private bool ppExposureInit;
        private WeatherVisuals visuals;
        private AudioSource rainSrc, windSrc, thunderSrc;
        private bool audioReady;

        // ---- 过渡状态 ----
        private EnvSample cur, from, to;
        private float tt = 1f;

        // ---- 闪电 ----
        private float nextBolt = 6f;
        private float flash;
        private Coroutine boltCo;

        // ---- GI 节流 ----
        private float giTimer;
        private bool giDirty;
        private float sunSearchTimer;

        public bool IsTransitioning { get { return tt < 1f; } }

        /// <summary>
        /// 当前天气对**行走手感**的影响系数（1 = 正常，越小越滑）。
        ///
        /// 参考《异环》：雨天轮胎打滑、雪天速度下降且转向迟钝。
        /// 本工程没有载具，改成第一人称的移速与脚步节奏 ——
        /// 这是最便宜也最有效的"天气影响玩法"：玩家不需要看到数值，
        /// 只要走起来感觉"地面有点滑"就够了。
        /// </summary>
        public float MoveFactor { get; private set; } = 1f;

        // =====================================================================
        // 生命周期
        // =====================================================================

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);

            EnsureSky();
            FindSun();
            EnsureVisuals();
            EnsureAudio();

            cur = Build(timeOfDay, weather);
            from = cur;
            to = cur;
            tt = 1f;
            giDirty = true;
            ApplyAll(cur, 0f, true);
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        // =====================================================================
        // 对外 API（保持与旧版一致，调用点不用改）
        // =====================================================================

        public void Set(TimeOfDay t, Weather w)
        {
            bool same = (timeOfDay == t && weather == w);
            timeOfDay = t;
            weather = w;

            from = cur;
            to = Build(t, w);
            tt = same ? 1f : 0f;

            if (transitionDuration <= 0.02f)
            {
                cur = to;
                tt = 1f;
                giDirty = true;
                ApplyAll(cur, 0f, true);
            }

            if (OnChanged != null) OnChanged(t, w);
        }

        public void SetTime(TimeOfDay t) { Set(t, weather); }
        public void SetWeather(Weather w) { Set(timeOfDay, w); }

        /// <summary>当前这一档天气的中文名，UI 直接用。</summary>
        public static string WeatherLabel(Weather w)
        {
            switch (w)
            {
                case Weather.Cloudy: return "多云";
                case Weather.Overcast: return "阴";
                case Weather.Rain: return "雨";
                case Weather.Storm: return "雷雨";
                case Weather.Fog: return "雾";
                case Weather.Snow: return "雪";
                default: return "晴";
            }
        }

        public static string TimeLabel(TimeOfDay t)
        {
            switch (t)
            {
                case TimeOfDay.Night: return "夜晚";
                case TimeOfDay.Dusk: return "黄昏";
                default: return "白天";
            }
        }

        // =====================================================================
        // 每帧
        // =====================================================================

        void Update()
        {
            if (tt < 1f)
            {
                tt += Time.deltaTime / Mathf.Max(0.05f, transitionDuration);
                float e = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(tt));
                cur = EnvSample.Lerp(from, to, e);
                giDirty = true;                       // 过渡期间才需要重算天空环境光
                if (tt >= 1f) cur = to;
            }

            TickLightning(Time.deltaTime);
            ApplyAll(cur, Time.deltaTime, false);
        }

        // =====================================================================
        // 参数合成：天气预设 × 昼夜修正
        // =====================================================================

        private static EnvSample Build(TimeOfDay t, Weather w)
        {
            EnvSample s = EnvSample.Get(w);

            float sunMul = 1f;      // 太阳强度倍率
            float expMul = 1f;      // 天空盒曝光倍率
            float ambMul = 1f;      // 环境光倍率
            float skyMul = 1f;      // 天空 tint 倍率
            float blueBias = 1f;    // 夜间让蓝通道多留一点
            float fogMul = 1f;
            Color sunTint = Color.white;

            switch (t)
            {
                case TimeOfDay.Day:
                    // 【P50 2026-11-19】主理人诉求：「6:00-19:00 阴天/雨天/雪天天空改回原来的」。
                    // 原基准里恶劣天气白天 sun 只有 0.26~0.48、exposure 0.70~0.90、雾极浓，
                    // 加上天气自动轮换跳到雾/雨档，白天画面就灰黑得像夜晚。
                    // 只在**白天档**把这些天气提亮到接近晴天的明亮观感；夜/黄昏完全不改（红线）。
                    // 用倍率而非改 EnvSample 基准，正好隔离夜档——Night/Dusk 各自 case 覆盖，不进这里。
                    if (w == Weather.Overcast || w == Weather.Rain || w == Weather.Storm ||
                        w == Weather.Fog || w == Weather.Snow)
                    {
                        sunMul = 2.0f;    // 0.26~0.48 → 0.52~0.96，方向光回来了
                        expMul = 1.22f;   // 0.70~1.02 → 0.85~1.24，天光整体提亮
                        ambMul = 1.15f;   // 暗部不再死黑
                        fogMul = 0.45f;   // 浓雾削薄，远景不再糊成灰墙
                        sunTint = new Color(1f, 0.98f, 0.94f);
                        // 云帷幔(雾0.9/雪0.94)会把白天天空盖成灰黑，白天降到 0.35~0.45
                        // 让天光透出来；夜里/黄昏不受影响（不进本分支）。
                        s.overcast *= 0.45f;
                        s.cloud *= 0.6f;
                    }
                    break;
                case TimeOfDay.Dusk:
                    sunMul = 0.50f; expMul = 0.78f; ambMul = 0.80f;
                    skyMul = 0.92f; blueBias = 0.90f; fogMul = 0.80f;
                    sunTint = new Color(1f, 0.66f, 0.42f);
                    break;
                case TimeOfDay.Night:
                    // P39e（迭代6·反弹底色）：ambMul 0.30→0.55。用户验收点：走廊墙面出现"水位线"——
                    // fill 锥体边缘以上全黑。根因 = D13 后场景零反弹，锥体边缘以上没有天花板反弹光可填。
                    // D13 清掉烘焙后环境光已"复活"（不再被烘焙贴图顶掉），夜晚档抬 ambMul 相当于
                    // 给全场景垫一层"反弹底色"，把水位线糊成平滑渐变；灯具下的光池保留（保明暗结构）。
                    // 白天档 ambMul=1 不动（零变化红线）；外景随动需复检（若夜景外墙过亮，压 fogMul 补偿）。
                    sunMul = 0.10f; expMul = 0.34f; ambMul = 0.55f;
                    skyMul = 0.20f; blueBias = 1.70f; fogMul = 0.42f;
                    sunTint = new Color(0.58f, 0.68f, 1f);
                    break;
            }

            s.sunIntensity *= sunMul;
            s.sunColor = new Color(s.sunColor.r * sunTint.r,
                                   s.sunColor.g * sunTint.g,
                                   s.sunColor.b * sunTint.b);
            s.exposure *= expMul;
            s.ambientIntensity *= ambMul;
            s.skyTint = new Color(s.skyTint.r * skyMul,
                                  s.skyTint.g * skyMul,
                                  Mathf.Min(1f, s.skyTint.b * skyMul * blueBias));
            s.fogColor = new Color(s.fogColor.r * fogMul,
                                   s.fogColor.g * fogMul,
                                   s.fogColor.b * fogMul);

            // 夜里天空不该挂着一个大太阳圆盘
            if (t == TimeOfDay.Night) s.sunDisk *= 0.12f;
            else if (t == TimeOfDay.Dusk) s.sunDisk *= 0.7f;

            // 夜里云也要暗下去，否则会浮在夜空上像块白布
            if (t == TimeOfDay.Night)
                s.cloudColor = new Color(s.cloudColor.r * 0.22f, s.cloudColor.g * 0.24f, s.cloudColor.b * 0.34f);
            else if (t == TimeOfDay.Dusk)
                s.cloudColor = new Color(s.cloudColor.r * 0.85f, s.cloudColor.g * 0.72f, s.cloudColor.b * 0.66f);

            // 星空只在夜里出现；云厚的时候由 WeatherVisuals 再按 cloud 压下去
            if (t == TimeOfDay.Night) s.star = 1f;
            else if (t == TimeOfDay.Dusk) s.star = 0.25f;
            else s.star = 0f;

            return s;
        }

        // =====================================================================
        // 应用
        // =====================================================================

        private void ApplyAll(EnvSample s, float dt, bool force)
        {
            EnsureSky();

            // 天气对行走手感的影响：雨天略滑、雪天明显更滑。
            // 放在最前面算，下游（第一人称控制器）随时读到都是本帧的值。
            float mf = 1f;
            if (s.rain > 0.05f) mf -= 0.10f * Mathf.Clamp01(s.rain);
            if (s.snow > 0.05f) mf -= 0.20f * Mathf.Clamp01(s.snow);
            MoveFactor = Mathf.Clamp(mf, 0.65f, 1f);

            if (sun == null)
            {
                // 场景有 69 盏灯，FindObjectsOfType 不便宜 —— 没找到平行光时也别每帧找
                sunSearchTimer -= dt;
                if (sunSearchTimer <= 0f) { sunSearchTimer = 1f; FindSun(); }
            }

            // ---- 天空盒 ----
            if (skyMat != null)
            {
                if (skyMat.HasProperty("_SkyTint")) skyMat.SetColor("_SkyTint", s.skyTint);
                if (skyMat.HasProperty("_Exposure")) skyMat.SetFloat("_Exposure", s.exposure + flash * 0.9f);
                if (skyMat.HasProperty("_AtmosphereThickness")) skyMat.SetFloat("_AtmosphereThickness", s.atmosphere);
                if (skyMat.HasProperty("_SunSize")) skyMat.SetFloat("_SunSize", s.sunDisk);
                if (skyMat.HasProperty("_SunSizeConvergence")) skyMat.SetFloat("_SunSizeConvergence", 8f);
                if (skyMat.HasProperty("_GroundColor"))
                    skyMat.SetColor("_GroundColor", Color.Lerp(s.fogColor, s.skyTint, 0.5f) * 0.8f);
                // 兜底分支用的 6 Sided / Cubemap 天空盒：它们的调色参数叫 _Tint，
                // 默认值 0.5（shader 里乘 2），所以这里再乘 0.55 才是"接近原色"。
                if (skyMat.HasProperty("_Tint")) skyMat.SetColor("_Tint", s.skyTint * 0.55f);
            }

            // ---- 主光 ----
            if (sun != null)
            {
                sun.color = s.sunColor;
                sun.intensity = s.sunIntensity * (1f + flash * 5f);
                sun.enabled = true;
            }

            // ---- 环境光 ----
            RenderSettings.ambientIntensity = Mathf.Clamp(s.ambientIntensity * (1f + flash * 1.8f), 0f, 8f);

            // ---- 【P13】后处理曝光（夜里压暗）----
            ApplyPostExposure(s, dt);

            // ---- 雾 ----
            bool wantFog = s.fogDensity > 0.0005f;
            RenderSettings.fog = wantFog;
            if (wantFog)
            {
                RenderSettings.fogMode = FogMode.ExponentialSquared;

                // 雾密度按相机高度衰减。
                // 数字孪生是 200m 左右的高空俯视，同一套密度会把整个校园糊成一片白；
                // 第一人称在地面 2m，才是雾该有的浓淡。这里从 5m 开始线性衰减到 185m 的 30%。
                float h = 2f;
                var c = Cam;
                if (c != null) h = c.transform.position.y;
                float hk = Mathf.Lerp(1f, 0.30f, Mathf.Clamp01((h - 5f) / 180f));

                RenderSettings.fogDensity = s.fogDensity * hk;
                RenderSettings.fogColor = s.fogColor;
            }

            // ---- 粒子 / 云 / 星空 ----
            if (visuals != null)
            {
                float cloud = showClouds ? s.cloud : 0f;
                // 阴天帷幔：showClouds 关掉时也一起关，否则会剩下一层没有云的"雾墙"
                float veil = showClouds ? s.overcast : 0f;
                // 天空色要下发给积水层：没有反射探针时，积水"看到"的就是这个解析天空，
                // 天气一变地面反光的颜色也得跟着变，否则会出现"灰雨天里地面反射蓝天"。
                Color zenith = s.skyTint;
                Color horizon = Color.Lerp(s.fogColor, s.skyTint, 0.35f);
                visuals.Apply(s.rain, s.snow, s.wind, cloud, veil, s.cloudColor, s.star, zenith, horizon, dt);
            }

            // ---- 音频 ----
            if (audioReady)
            {
                if (rainSrc != null) rainSrc.volume = Mathf.Clamp01(s.rain / 1.2f) * sfxScale;
                if (windSrc != null) windSrc.volume = Mathf.Clamp01(s.wind) * sfxScale * 0.7f;
            }

            // ---- 天空环境光重算（节流） ----
            // 只在真正有变化时才算：DynamicGI.UpdateEnvironment 会把天空盒重新渲染进 cubemap，
            // 每帧调会明显掉帧，所以过渡期间 0.2 秒一次，过渡结束后自然停下来。
            giTimer += dt;
            if (force || giTimer >= 0.2f)
            {
                giTimer = 0f;
                if (giDirty)
                {
                    giDirty = false;
                    DynamicGI.UpdateEnvironment();
                }
            }
        }

        // =====================================================================
        // 【P13】后处理曝光驱动
        // =====================================================================

        /// <summary>
        /// 把「白天基准亮度」乘上一个 EV 偏移后写进 ColorGrading.postExposure。
        ///
        /// 【为什么不直接用 s.exposure】
        /// `s.exposure` 是**天空盒**的 _Exposure，只影响天空和 GI，不影响地面建筑物。
        /// 夜里真正要压的是"整个画面"（建筑、路灯、地面），只有后处理能一次压全。
        /// 所以两者是并行的：天空盒曝光管天、postExposure 管屏幕。都压才能得到
        /// "夜里又暗又蓝"而不是"天色变暗了但楼还是白天那么亮"。
        ///
        /// 【EV 取值的依据】
        /// 白天的 EnvSample.exposure 在 0.85~1.25 之间。夜里 Build() 会给它乘 0.34，
        /// 即约 0.29~0.43。要让画面观感上"夜间"（约 −1.2 EV 到 −2 EV），
        /// 这里按昼夜档给固定偏移，再叠一点天气修正：
        ///   Day   → 0 EV（不动，白天必须是原样）
        ///   Dusk  → −0.35 EV（略压，配合橙色天光）
        ///   Night → −1.45 EV（明显暗下去，但仍看得清建筑轮廓）
        /// 阴雨天在夜间再压一点（云厚的时候夜里更黑，符合直觉）。
        ///
        /// ⚠️ 这几个数字必须 play 实测微调。给的是**起点**，不是定稿。
        /// </summary>
        private void ApplyPostExposure(EnvSample s, float dt)
        {
            if (!ppReady)
            {
                // 找不到卷时别每帧 Find：1 秒一次就够，且把计时器放在这里
                // （ApplyAll 每帧都会进本方法，不做节流就是每帧一次 FindObjectsOfType）
                ppSearchTimer -= dt;
                if (ppSearchTimer > 0f) return;
                ppSearchTimer = 1f;
                BindPostProcess();
                if (!ppReady) return;
            }

            float target;
            switch (timeOfDay)
            {
                case TimeOfDay.Night:
                    // 夜里雨/雪比晴更暗：云层把本就没有的月光进一步挡住
                    target = -1.45f;
                    if (s.rain > 0.5f || s.snow > 0.5f) target -= 0.25f;
                    break;
                case TimeOfDay.Dusk:
                    target = -0.35f;
                    break;
                default:
                    target = 0f;
                    break;
            }

            // 变化小于 0.01 EV 不写：postExposure 是 ParameterOverride，
            // 写一次会 mark 整个卷 dirty，每帧写会有无谓的复制开销。
            if (ppExposureInit && Mathf.Abs(target - ppExposure) < 0.01f) return;

            ppExposure = target;
            ppExposureInit = true;
            SetPostExposure(target);
        }

        /// <summary>
        /// 反射找 PostProcessVolume → sharedProfile → ColorGrading → postExposure。
        /// 全部走反射，理由见 ppVolume 字段的注释（不把后处理变成编译门槛）。
        /// </summary>
        private void BindPostProcess()
        {
            ppReady = false;
            ppVolume = null;
            ppGrading = null;

            Type volType = FindTypeByName("UnityEngine.Rendering.PostProcessing.PostProcessVolume");
            Type cgType = FindTypeByName("UnityEngine.Rendering.PostProcessing.ColorGrading");
            if (volType == null || cgType == null) return;   // 包没装，静默退出

            // 【必须写 UnityEngine.Object 全限定】本文件同时 using System 与 UnityEngine，
            // 裸写 `Object` 会 CS0104（二义性）。这种错在别处见过一次，
            // 编译中断在更早的阶段，真正的原因被前面的报错盖住了，很难第一眼看出来。
            UnityEngine.Object[] vols = FindObjectsOfType(volType, true);
            if (vols == null || vols.Length == 0) return;

            object vol = vols[0];
            object profile = GetMember(vol, "sharedProfile");
            if (profile == null) profile = GetMember(vol, "profile");
            if (profile == null) return;

            // profile.GetSetting<T>() 是泛型方法，反射调用要 MakeGenericMethod
            MethodInfo get = profile.GetType().GetMethod("GetSetting");
            if (get == null) return;
            object cg = get.MakeGenericMethod(cgType).Invoke(profile, null);
            if (cg == null) return;

            ppVolume = vol;
            ppGrading = cg;
            ppReady = true;
            ppExposureInit = false;
        }

        /// <summary>写 postExposure 的 value。ParameterOverride&lt;float&gt; 的字段叫 value。</summary>
        private void SetPostExposure(float ev)
        {
            if (ppGrading == null) return;
            object param = GetMember(ppGrading, "postExposure");
            if (param == null) return;
            SetMember(param, "value", ev);
            // overrideState 必须为 true，否则 value 写了也不生效 ——
            // 这是 PPv2 最隐蔽的一处：值是对的，但没"勾上覆盖"。
            SetMember(param, "overrideState", true);
        }

        private static Type FindTypeByName(string fullName)
        {
            Assembly[] asms = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < asms.Length; i++)
            {
                Type t = asms[i].GetType(fullName, false);
                if (t != null) return t;
            }
            return null;
        }

        private static object GetMember(object obj, string name)
        {
            if (obj == null) return null;
            Type t = obj.GetType();
            FieldInfo f = t.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            if (f != null) return f.GetValue(obj);
            PropertyInfo p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p != null) return p.GetValue(obj, null);
            return null;
        }

        private static void SetMember(object obj, string name, object value)
        {
            if (obj == null) return;
            Type t = obj.GetType();
            FieldInfo f = t.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            if (f != null) { f.SetValue(obj, value); return; }
            PropertyInfo p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p != null && p.CanWrite) p.SetValue(obj, value, null);
        }

        // =====================================================================
        // 闪电
        // =====================================================================

        private void TickLightning(float dt)
        {
            if (cur.lightning < 0.05f)
            {
                nextBolt = Random.Range(4f, 9f);
                return;
            }

            nextBolt -= dt;
            if (nextBolt <= 0f && boltCo == null)
            {
                nextBolt = Random.Range(5f, 12f) / Mathf.Max(0.35f, cur.lightning);
                boltCo = StartCoroutine(BoltRoutine());
            }
        }

        // 真实闪电是"两下"：先一记强闪，短暂变暗，再补一记弱的
        private IEnumerator BoltRoutine()
        {
            flash = 1f;
            if (thunderSrc != null) thunderSrc.PlayDelayed(Random.Range(0.3f, 1.2f));

            yield return new WaitForSeconds(0.07f);
            flash = 0.16f;
            yield return new WaitForSeconds(0.09f);
            flash = 0.72f;
            yield return new WaitForSeconds(0.13f);
            flash = 0f;

            boltCo = null;
        }

        // =====================================================================
        // 运行时兜底
        // =====================================================================

        /// <summary>
        /// 保证有一个**运行期私有**的天空盒材质，且它必须能表达 7 档天气。
        ///
        /// 这里刻意**不复制场景自带的天空盒**。实测场景用的是 Skybox/6 Sided
        /// （属性只有 _RightTex / _UpTex / _Exposure / _Rotation / _Tint），
        /// 根本没有 _SkyTint / _AtmosphereThickness / _SunSize / _GroundColor ——
        /// 也就是说旧版代码里那一串 HasProperty 判断全部为 false，天气切换时天空**几乎不变**。
        ///
        /// 换成运行期的 Skybox/Procedural 之后，EnvSample 的每个字段都能对上一个真实参数，
        /// 而且天空色能和雾色、云色保持一致（否则会出现"天是蓝的、雾是灰的"这种割裂）。
        /// 云层由 WeatherVisuals 的云穹顶负责，所以不依赖天空盒贴图里的云。
        /// </summary>
        private void EnsureSky()
        {
            if (skyMat != null && RenderSettings.skybox == skyMat) return;

            var sh = Shader.Find("Skybox/Procedural");
            if (sh != null)
            {
                skyMat = new Material(sh);
                skyMat.name = "RuntimeSkyboxProcedural";
                skyMat.SetFloat("_SunDisk", 2f);              // 2 = 高质量太阳圆盘
                skyMat.SetFloat("_SunSizeConvergence", 8f);
                RenderSettings.skybox = skyMat;
                return;
            }

            // 兜底：找不到程序化天空盒就复制场景的，至少 _Exposure 还能变
            var src = RenderSettings.skybox;
            if (src != null)
            {
                skyMat = new Material(src);
                skyMat.name = "RuntimeSkybox";
                RenderSettings.skybox = skyMat;
                Debug.LogWarning("[EnvironmentState] 找不到 Skybox/Procedural，退回场景天空盒，只有曝光可调。");
                return;
            }

            Debug.LogWarning("[EnvironmentState] 没有任何可用的天空盒，天空相关效果将被跳过。");
        }

        private Camera Cam
        {
            get
            {
                if (cam == null || !cam.isActiveAndEnabled) cam = FindActiveCamera();
                return cam;
            }
        }

        /// <summary>
        /// 找当前真正在用的相机。
        ///
        /// **不能用 `Camera.main`** —— 它只按 "MainCamera" tag 查找，**不检查 enabled**。
        /// 本工程第一人称模式下孪生相机是 `enabled = false` 但 tag 还在，而
        /// FirstPersonController 建的 FPCam 没设 tag，所以 `Camera.main` 会返回那台
        /// 被禁用的孪生相机（200m 高空）。雾密度按 200m 算就会淡掉三倍，
        /// 雨雪也会被跟到高空 —— 两处都靠这个方法取值。
        ///
        /// `Camera.allCameras` 只包含真正启用的相机，是唯一可靠的数据源；
        /// 再排除 `targetTexture != null` 的小地图相机。
        /// </summary>
        public static Camera FindActiveCamera()
        {
            var all = Camera.allCameras;
            if (all == null) return null;

            Camera best = null;
            for (int i = 0; i < all.Length; i++)
            {
                var c = all[i];
                if (c == null) continue;
                if (c.targetTexture != null) continue;
                if (best == null || c.depth > best.depth) best = c;
            }
            return best;
        }

        private void FindSun()
        {
            var lights = FindObjectsOfType<Light>();
            for (int i = 0; i < lights.Length; i++)
            {
                if (lights[i] != null && lights[i].type == LightType.Directional)
                {
                    sun = lights[i];
                    return;
                }
            }
        }

        private void EnsureVisuals()
        {
            if (visuals != null) return;
            var go = new GameObject("WeatherVisuals");
            go.transform.SetParent(transform, false);
            visuals = go.AddComponent<WeatherVisuals>();
            visuals.EnsureBuilt();
        }

        /// <summary>相机换了（数字孪生 ↔ 第一人称）后调用，让雨雪重新跟随新相机。</summary>
        public void RebindCamera()
        {
            if (visuals != null) visuals.RebindCamera();
        }

        private void EnsureAudio()
        {
            if (audioReady) return;
            audioReady = true;

            rainSrc = MakeLoopSource("RainSfx", ProceduralAssets.RainSfx);
            windSrc = MakeLoopSource("WindSfx", ProceduralAssets.WindSfx);

            thunderSrc = gameObject.AddComponent<AudioSource>();
            thunderSrc.name = "ThunderSfx";
            thunderSrc.clip = ProceduralAssets.ThunderSfx;
            thunderSrc.loop = false;
            thunderSrc.playOnAwake = false;
            thunderSrc.spatialBlend = 0f;
            thunderSrc.volume = 0.9f;
        }

        private AudioSource MakeLoopSource(string name, AudioClip clip)
        {
            var src = gameObject.AddComponent<AudioSource>();
            src.name = name;
            src.clip = clip;
            src.loop = true;
            src.playOnAwake = false;
            src.spatialBlend = 0f;
            src.volume = 0f;
            src.Play();
            return src;
        }
    }
}

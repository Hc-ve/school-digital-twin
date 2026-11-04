using UnityEngine;
using UnityEngine.Rendering;

namespace CampusUI.Environment
{
    /// <summary>
    /// 雨天积水层。参考《异环》雨天「地面积水 + 环境倒影」的表现。
    ///
    /// 做法：在相机脚下铺一块 40m 见方的水平平面，用 CampusUI/WetGround 着色器画。
    /// 着色器负责反射与涟漪，这里只负责三件事：
    ///   1) **湿滑度累积** —— 打湿要 8 秒，蒸发要 26 秒，不是「雨一停地面立刻干」；
    ///   2) **贴地跟随** —— 高度由 WeatherVisuals 的向下射线给，室内直接关掉；
    ///   3) **天空色下发** —— 让反射的地平线色跟着当前天气走（雨天反射灰云、夜晚反射深蓝）。
    ///
    /// 为什么不逐块地面单独做湿润：本工程的地面是场景里现成的网格，
    /// 材质是磁盘上的共享资产，改它会把 .mat 改脏（天空盒那次已经踩过一次）。
    /// 叠一层独立的半透明水面，既能做到「只有脚下这一片有积水」，
    /// 又不碰任何原有材质。
    /// </summary>
    public class WetGround : MonoBehaviour
    {
        private const float Size = 40f;             // 积水层边长（米）
        private const float GroundOffset = 0.015f;  // 比地面高一点，避免 z-fighting

        private const float WetInSeconds = 8f;      // 打湿用时
        private const float DryInSeconds = 26f;     // 蒸发用时（比打湿慢，符合直觉）

        private Transform plane;
        private Material mat;
        private bool built;

        private float wetness;      // 0~1，湿滑度累积量
        private float indoorFade = 1f;
        private bool on;

        private static readonly int IdWetness = Shader.PropertyToID("_Wetness");
        private static readonly int IdSky = Shader.PropertyToID("_SkyColor");
        private static readonly int IdHorizon = Shader.PropertyToID("_HorizonColor");
        private static readonly int IdRipple = Shader.PropertyToID("_RippleStrength");
        private static readonly int IdMask = Shader.PropertyToID("_MaskTex");

        public void Build()
        {
            if (built) return;
            built = true;

            var sh = Shader.Find("CampusUI/WetGround");
            if (sh == null)
            {
                Debug.LogWarning("[WetGround] 找不到 CampusUI/WetGround 着色器，雨天积水效果不可用。");
                return;
            }

            var go = GameObject.CreatePrimitive(PrimitiveType.Plane);   // 默认 10x10 单位
            go.name = "WetGround";

            var col = go.GetComponent<Collider>();
            if (col != null) Destroy(col);       // 有碰撞体会挡住玩家脚下的向下射线

            go.transform.SetParent(transform, false);
            go.transform.localScale = new Vector3(Size * 0.1f, 1f, Size * 0.1f);

            mat = new Material(sh);
            mat.name = "ProcWetGround";
            // 注意：不能用 mat.mainTexture —— 它只认 _MainTex，本着色器的贴图叫 _MaskTex。
            mat.SetTexture(IdMask, ProceduralAssets.WetMask);
            mat.SetColor("_WaterColor", new Color(0.045f, 0.060f, 0.075f, 1f));
            mat.renderQueue = 2992;              // 必须在积雪层 2996 之前，积雪才能盖住积水

            var mr = go.GetComponent<MeshRenderer>();
            if (mr != null)
            {
                mr.sharedMaterial = mat;
                mr.shadowCastingMode = ShadowCastingMode.Off;
                mr.receiveShadows = false;
                mr.lightProbeUsage = LightProbeUsage.Off;
                // 必须保留反射探针：shader 里的 unity_SpecCube0 是"最近反射探针"，
                // 关掉它就只剩默认天空反射，场景里放了探针也拿不到建筑倒影。
                mr.reflectionProbeUsage = ReflectionProbeUsage.BlendProbes;
            }

            plane = go.transform;
            go.SetActive(false);
            on = false;
        }

        /// <summary>贴地跟随。groundY 由调用方的向下射线给出。</summary>
        public void Follow(Vector3 camPos, float groundY)
        {
            if (plane == null) return;
            plane.position = new Vector3(camPos.x, groundY + GroundOffset, camPos.z);
        }

        /// <summary>每帧由 WeatherVisuals 调用。</summary>
        public void Apply(float rain, float dt, Color sky, Color horizon, bool indoor)
        {
            if (mat == null || plane == null) return;

            // ---- 湿滑度累积 ----
            // 用 MoveTowards 而不是指数插值：指数插值永远到不了 0，地面会一直是湿的。
            float target = Mathf.Clamp01(rain);
            if (target > wetness)
                wetness = Mathf.MoveTowards(wetness, target, dt / WetInSeconds);
            else
                wetness = Mathf.MoveTowards(wetness, 0f, dt / DryInSeconds);

            // ---- 室内外过渡 ----
            indoorFade = Mathf.MoveTowards(indoorFade, indoor ? 0f : 1f, dt * 3f);

            float a = wetness * indoorFade;
            bool want = a > 0.01f;
            if (want != on)
            {
                plane.gameObject.SetActive(want);
                on = want;
            }
            if (!on) return;

            mat.SetFloat(IdWetness, a);
            mat.SetColor(IdSky, sky);
            mat.SetColor(IdHorizon, horizon);
            // 涟漪只在下雨时有；雨停了水洼还在，但水面应该静下来
            mat.SetFloat(IdRipple, Mathf.Clamp01(rain) * 1.2f);
        }
    }
}

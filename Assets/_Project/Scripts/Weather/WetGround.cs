using UnityEngine;
using UnityEngine.Rendering;

namespace SchoolTwin.Weather
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
        private bool on;

        private static readonly int IdWetness = Shader.PropertyToID("_Wetness");
        private static readonly int IdSky = Shader.PropertyToID("_SkyColor");
        private static readonly int IdHorizon = Shader.PropertyToID("_HorizonColor");
        private static readonly int IdRipple = Shader.PropertyToID("_RippleStrength");
        private static readonly int IdMask = Shader.PropertyToID("_MaskTex");
        private static readonly int IdMaskOrigin = Shader.PropertyToID("_MaskOrigin");
        private static readonly int IdMaskTileSize = Shader.PropertyToID("_MaskTileSize");

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
            // 【P13】把"一次平铺覆盖多大世界范围"告诉 shader。
            // 取 40m：与平面尺寸一致，正好一个平面里铺一整张图，
            // 水洼的疏密程度与队友原版的观感接近，不用重新调噪声频率。
            mat.SetFloat(IdMaskTileSize, Size);
            // 世界原点，**只在这里设一次，之后再也不改**。
            // 这是"水坑在世界空间静止"的前提：原点一旦跟着平面/相机动，
            // UV 就又变回相对平面的了（详见 Follow 的注释 ②）。
            mat.SetVector(IdMaskOrigin, Vector4.zero);
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

        /// <summary>
        /// 贴地跟随。groundY 由调用方的向下射线给出。
        ///
        /// ══════════════════════════════════════════════════════════════════
        /// 【P13 关键：水坑为什么曾经会"跟着角色跑"，以及正确的解法】
        ///
        /// 原实现直接把平面挪到相机脚下：
        ///     plane.position = new Vector3(camPos.x, groundY + GroundOffset, camPos.z);
        /// 而遮罩用的是物体空间 UV → 图案与平面刚性绑定 → 平面动，图案动。
        /// 玩家看到的就是「水坑粘在脚边、走到哪跟到哪」。
        ///
        /// 【解法分两半，缺一不可】
        ///
        /// ① shader 侧：UV 由世界坐标算（见 WetGround.shader 顶部注释）。
        ///    这让图案的锚点从"平面"换成了"世界原点"。
        ///
        /// ② 这里：`_MaskOrigin` 必须是**固定的世界常量**（就用世界原点 0,0,0），
        ///    **绝对不能**跟着平面或相机走。这是最容易写错的一点 ——
        ///    如果 origin 取"平面当前位置"，那么
        ///        uv = (worldPos - planePos) / Size
        ///    平面一动 origin 跟着动，UV 又变成相对平面的了，等于绕一圈回到原 bug。
        ///    换句话说：世界锁定的前提是**原点不动**。
        ///
        /// 那平面还要不要吸附到整格？—— **不要**，这是我在本修复里推翻过的一个错误设计。
        ///
        /// 起初我以为"必须吸附，否则边缘水洼会忽隐忽现"。推演后不成立：
        ///   · 平面边长 40m，边缘淡出从 r=0.55（半径 11m）就开始，到 r=1.0（半径 20m）归零；
        ///   · 吸附到整格后，平面中心相对相机最多偏 20m —— 正好把相机顶到淡出圈最外沿，
        ///     `edge` 在玩家脚下算出来 ≈ 0 → **脚边一滴水都没有**，比忽隐忽现更糟；
        ///   · 而连续跟随不会让图案移动（UV 由世界坐标决定，平面怎样平移都不影响），
        ///     只是让"被覆盖的窗口"始终对准相机 —— 这正是我们想要的：水膜永远在脚边，
        ///     水洼永远钉在原地。两个目标同时满足。
        ///
        /// 结论：跟随就精确跟，什么都不吸附。
        /// ══════════════════════════════════════════════════════════════════
        /// </summary>
        public void Follow(Vector3 camPos, float groundY)
        {
            if (plane == null) return;

            // 精确跟随：边缘淡出圈始终在相机 11~20m 外，看不出来，也不会盖住脚下。
            plane.position = new Vector3(camPos.x, groundY + GroundOffset, camPos.z);

            // `_MaskOrigin` 保持世界原点不动（在 Build 里设一次零向量即可）。
            // 这里不写它 —— 写了就把世界锁定又破坏掉了，见上面 ② 的解释。
            // 一句话记法：**平面可以动，原点不能动。平面动只改变"哪里被覆盖"，
            // 原点是 UV 的锚，它一动，图案就跟着平面滑走了。**
        }

        // ══════════════════════════════════════════════════════════════════
        // 【P13】只读诊断出口。
        //
        // 为什么必须加：诊断工具原来用 `GetComponentInChildren<Renderer>()` 反查
        // 平面，但 WetGround 是挂在 WeatherVisuals 上的组件，
        // 它的 GetComponentInChildren 会先扫到**同一个 GameObject 上的兄弟组件**
        // 建出来的 CloudDome（云穹顶，位置恒等于相机位置）。
        //
        // 于是自检一直打「积水层 worldY = 1.73 ⚠️与相机同高 → 已铺到当前楼层」——
        // 这个数字是云穹顶的，积水层其实好端端贴在室外地面 -0.485。
        // 一条假警报把排查带偏了整整一轮，所以直接把真值暴露出来。
        // ══════════════════════════════════════════════════════════════════
        public bool IsBuilt { get { return plane != null && mat != null; } }
        public bool IsVisibleNow { get { return on; } }
        public float PlaneWorldY { get { return plane != null ? plane.position.y : float.NaN; } }
        public float WetnessNow { get { return wetness; } }
        public float AlphaInMat { get { return mat != null ? mat.GetFloat(IdWetness) : -1f; } }

        /// <summary>每帧由 WeatherVisuals 调用。</summary>
        /// <param name="indoor">
        /// 玩家此刻是否在遮挡下。**只用于水花以外的观感微调，不参与可见性开关** ——
        /// 理由见下面那段长注释。保留这个参数是为了以后要调"室内看到的积水更暗一点"时有入口。
        /// </param>
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

            // ══════════════════════════════════════════════════════════════
            // 【P13 第三次修：可见性不再跟室内状态走】
            //
            // 原实现：`float a = wetness * indoorFade;`（indoorFade 室内 → 0）
            // 于是进屋 → 整片 40m 水面 alpha 归零 → **窗外的水坑也没了**。
            // 主理人本轮报的"下雨天在室内看窗外看不到水坑的变化，出来室内就能看到"
            // 就是这个，和地面雪当年那个 bug 一模一样。
            //
            // ── 关键前提（这才是敢直接去掉 indoorFade 的依据）────────────
            // 平面位置由 WeatherVisuals 给的 **outdoorGroundY**（室外地平面）决定，
            // 不是 groundY（相机脚下那层楼板）。实测：
            //     室外地面 Y = -0.50     室内 1F 地板 groundY = 0.00
            //     → 水面在 -0.485，比室内地板**低 48cm**
            // 所以水面永远在室内地板以下，深度测试会自己把它挡住。
            // 站二楼时更高，更不可能露出来。
            //
            // 换句话说：**室内看不见积水，本来就该由遮挡负责，不该由我们主动关。**
            // 主动关的直接后果就是"从窗户望出去，外面也是干的" —— 那才是穿帮。
            //
            // ── 什么时候仍然需要关 ────────────────────────────────────────
            // 只有"雨停了 + 水面干透"（wetness → 0）。那时 alpha 自然到 0，
            // 由下面那行 `want = a > 0.01f` 收掉，不浪费一次 draw call。
            //
            // ── 历史教训别再犯 ────────────────────────────────────────────
            // 早先确实出现过"木地板全变黑水面"，但根因是**位置**用错了 groundY
            // （站二楼 → 水面被铺到二楼楼板标高），不是"没跟 indoorFade 关"。
            // 位置对了以后，indoorFade 这层保险就成了纯副作用。
            // ══════════════════════════════════════════════════════════════
            float a = wetness;
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

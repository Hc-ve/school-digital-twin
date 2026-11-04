using UnityEngine;
using UnityEngine.Rendering;

namespace SchoolTwin.Weather
{
    /// <summary>
    /// 天气的可视部分：雨、雪两套粒子系统 + 一个跟随相机的云穹顶。
    ///
    /// 设计要点：
    ///  1) **整块跟着相机走**。雨雪只在玩家周围 60×60 米的盒子里发射，
    ///     跟走以后无论漫游到校园哪个角落都一直在下雨，也不用铺满整个校园。
    ///  2) **粒子在 World 空间模拟**。跟着相机移动的发射器如果同时用 Local 空间，
    ///     已经落下的雨滴会被"拖着走"，看起来像雨在追人。
    ///  3) **云穹顶用 Sprites/Default**。它是 URP 下最稳的透明双面无光照着色器，
    ///     不需要配 _Surface/_Blend/_ZWrite 那一堆材质关键字，避免"配错就变实心白球"。
    ///
    /// 已知取舍：雨雪不做世界碰撞（穿屋顶）。开碰撞要跑 World 碰撞查询，
    /// 几千个粒子每帧一次，代价远大于收益；室内看到下雨是这类轻量方案的通病。
    /// </summary>
    public class WeatherVisuals : MonoBehaviour
    {
        private const float AreaSize = 110f;     // 发射盒水平尺寸（运行时按相机高度再放大）
        private const float EmitHeight = 16f;    // 发射高度（相机上方）
        private const float DomeRadius = 380f;   // 云穹顶半径，必须小于相机 farClip(2000)

        private const float RainRateMax = 3200f; // rain = 1 时每秒发射数
        private const float SnowRateMax = 520f;  // snow = 1 时每秒发射数

        // 落地反馈
        private const float SplashArea = 26f;      // 雨滴溅射覆盖范围（相机脚下）
        private const float SplashRateMax = 1300f; // rain = 1 时每秒水花数
        private const float SnowGroundSize = 90f;   // 近场地积雪盘边长（米）：覆盖第一人称脚下近场，高细分、贴地形、带雪堆起伏
        private const float SnowGroundFarSize = 260f;  // 远场白兜底盘边长（米）：半径 130m，覆盖近场盘到雪天雾起(≈120m)之间的环带；远边被雾完全淡出
        // 雪面抬离"射线采到的地面"的高度。
        // 关键修复（"雪看着很虚、只积在砖缝里"）：脚下庭院/广场是 SchoolBuilding.FBX 里的铺装网格，
        // 它高出地形、且没有碰撞体，所以向下射线只采到"地形"而非"可见铺装面"，
        // 雪面停在 地形+0.10，低于可见砖面 → ZTest LEqual 让砖面戳穿雪面，只剩砖缝凹槽露出白。
        // 把抬升量提到能盖过铺装凸起（庭院/砖面通常高出地形 0~0.25m），雪才能整片盖住地面。
        // 若改模型后砖面仍戳穿，把下面两个数调大即可（单位：米）。远场略高，因为远处地形跟随误差更大。
        private const float SnowGroundLiftNear = 0.30f;
        private const float SnowGroundLiftFar = 0.35f;

        private ParticleSystem rainPs, snowPs;
        private ParticleSystem splashPs;
        private Transform splashRoot;
        private Transform snowGround;
        private Material snowGroundMat;

        // 积雪地形跟随：克隆内建平面网格后逐顶点向下射线，让雪盖住抬高的花坛/台阶。
        private Mesh snowMesh;
        private Vector3[] snowBaseVerts;   // 平面原始顶点（只取 x/z 做世界映射，y 恒为 0）
        private Vector3[] snowWorkVerts;   // 工作副本，逐帧改写 y 做地形位移
        private Vector3 lastDrapeCam = new Vector3(float.MaxValue, 0f, 0f);
        private bool snowDrapeDirty = true;
        private float snowAmount;          // 当前积雪量（0~1，已含 altFade/indoorFade），驱动雪堆高度
        private float lastDrapeAmount = -1f;

        // 远场白兜底雪盘（消除地平线方向裸地形缺口）
        private Transform snowGroundFar;
        private Material snowGroundFarMat;
        private Mesh snowMeshFar;
        private Vector3[] snowBaseVertsFar;
        private Vector3[] snowWorkVertsFar;
        private Vector3 lastDrapeCamFar = new Vector3(float.MaxValue, 0f, 0f);
        private bool snowDrapeDirtyFar = true;
        private float lastDrapeAmountFar = -1f;
        private bool snowGroundFarOn;
        private float lastSnowGroundFarA = -1f;
        private WetGround wet;
        private Transform cloudDome;
        private Material cloudMat;
        private Transform starDome;
        private Material starMat;
        private Camera cam;
        private bool built;

        private float lastRain = -1f, lastSnow = -1f, lastCloud = -1f, lastStarA = -1f;
        private float lastArea = -1f;
        private float lastSplashRain = -1f, lastSnowGroundA = -1f;
        private float groundY;
        private float groundProbeTimer;
        private bool domeOn;
        private bool starOn;
        private bool snowGroundOn;

        // 积雪累积量：0~1。天气里的 snow 是"此刻下多大"，这个是"地上积了多少"。
        // 两者必须分开，否则雪一停地面积雪会瞬间消失（《异环》是渐进积雪 + 更慢的化雪）。
        private float snowAccum;

        // 室内外：1 = 户外，0 = 室内。头顶有遮挡时把雨雪压下去、关掉积水与积雪层。
        private float indoorFade = 1f;
        private bool indoor;

        // 相机高度淡出系数：1 = 完全显示雨雪，0 = 高空俯视时关掉
        private float altFade = 1f;

        // =====================================================================
        // 构建
        // =====================================================================

        public void EnsureBuilt()
        {
            if (built) return;
            built = true;

            rainPs = BuildRain();
            snowPs = BuildSnow();
            BuildCloudDome();
            BuildStarDome();
            splashPs = BuildSplash();
            BuildSnowGround();

            // 积水层独立成一个组件：它自带一套湿滑度累积逻辑，和粒子系统没有耦合
            wet = gameObject.AddComponent<WetGround>();
            wet.Build();
        }

        private ParticleSystem BuildRain()
        {
            var go = new GameObject("RainParticles");
            go.transform.SetParent(transform, false);
            go.transform.localPosition = new Vector3(0f, EmitHeight, 0f);

            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = ps.main;
            main.loop = true;
            main.prewarm = true;
            main.playOnAwake = true;
            main.duration = 5f;
            main.startLifetime = new ParticleSystem.MinMaxCurve(1.4f, 1.9f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0f, 0.4f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.035f, 0.06f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, 6.2831853f);
            main.startColor = new Color(0.72f, 0.82f, 0.96f, 0.55f);
            main.gravityModifier = 1.3f;                  // 靠重力加速，比固定速度自然
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 9000;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;

            var em = ps.emission;
            em.enabled = true;
            em.rateOverTime = 0f;                          // 由 Apply() 控制

            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(AreaSize, 0.5f, AreaSize);
            shape.position = Vector3.zero;
            shape.rotation = Vector3.zero;

            // 生命周期两端淡入淡出，避免雨丝突然出现/消失
            var col = ps.colorOverLifetime;
            col.enabled = true;
            col.color = new ParticleSystem.MinMaxGradient(FadeGradient());

            var vel = ps.velocityOverLifetime;
            vel.enabled = true;
            vel.space = ParticleSystemSimulationSpace.World;
            vel.x = new ParticleSystem.MinMaxCurve(0f);
            vel.y = new ParticleSystem.MinMaxCurve(0f);
            vel.z = new ParticleSystem.MinMaxCurve(0f);

            var pr = go.GetComponent<ParticleSystemRenderer>();
            if (pr != null)
            {
                // 注意：枚举值叫 Stretch，不是 StretchedBillboard（后者是 Inspector 里的显示名）
                pr.renderMode = ParticleSystemRenderMode.Stretch;
                pr.velocityScale = 0.055f;                 // 拉成雨丝
                pr.lengthScale = 2.4f;
                pr.minParticleSize = 0f;
                pr.maxParticleSize = 0.6f;
                pr.shadowCastingMode = ShadowCastingMode.Off;
                pr.receiveShadows = false;
                var m = ProceduralAssets.MakeSpriteMaterial(ProceduralAssets.RainStreak, Color.white, "ProcRainMat");
                if (m != null) pr.sharedMaterial = m;
            }

            ps.Play();
            return ps;
        }

        private ParticleSystem BuildSnow()
        {
            var go = new GameObject("SnowParticles");
            go.transform.SetParent(transform, false);
            go.transform.localPosition = new Vector3(0f, EmitHeight, 0f);

            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = ps.main;
            main.loop = true;
            main.prewarm = true;
            main.playOnAwake = true;
            main.duration = 5f;
            main.startLifetime = new ParticleSystem.MinMaxCurve(8f, 12f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.3f, 0.8f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.07f, 0.16f);
            main.startColor = new Color(1f, 1f, 1f, 0.9f);
            main.gravityModifier = 0.035f;                 // 几乎不加速，慢慢飘
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 6000;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;

            var em = ps.emission;
            em.enabled = true;
            em.rateOverTime = 0f;

            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(AreaSize, 0.5f, AreaSize);
            shape.position = Vector3.zero;
            shape.rotation = Vector3.zero;

            // 噪声扰动：雪花左右打旋，是"雪"和"白点雨"最大的观感差别
            var noise = ps.noise;
            noise.enabled = true;
            noise.strength = 0.55f;
            noise.frequency = 0.35f;
            noise.scrollSpeed = 0.35f;
            noise.damping = true;
            noise.octaveCount = 2;

            var col = ps.colorOverLifetime;
            col.enabled = true;
            col.color = new ParticleSystem.MinMaxGradient(FadeGradient());

            var vel = ps.velocityOverLifetime;
            vel.enabled = true;
            vel.space = ParticleSystemSimulationSpace.World;
            vel.x = new ParticleSystem.MinMaxCurve(0f);
            vel.y = new ParticleSystem.MinMaxCurve(0f);
            vel.z = new ParticleSystem.MinMaxCurve(0f);

            var pr = go.GetComponent<ParticleSystemRenderer>();
            if (pr != null)
            {
                pr.renderMode = ParticleSystemRenderMode.Billboard;
                pr.minParticleSize = 0f;
                pr.maxParticleSize = 1f;
                pr.shadowCastingMode = ShadowCastingMode.Off;
                pr.receiveShadows = false;
                var m = ProceduralAssets.MakeSpriteMaterial(ProceduralAssets.Snowflake, Color.white, "ProcSnowMat");
                if (m != null) pr.sharedMaterial = m;
            }

            ps.Play();
            return ps;
        }

        private static Gradient FadeGradient()
        {
            var g = new Gradient();
            g.SetKeys(
                new[]
                {
                    new GradientColorKey(Color.white, 0f),
                    new GradientColorKey(Color.white, 1f)
                },
                new[]
                {
                    new GradientAlphaKey(0f, 0f),
                    new GradientAlphaKey(1f, 0.12f),
                    new GradientAlphaKey(1f, 0.82f),
                    new GradientAlphaKey(0f, 1f)
                });
            return g;
        }

        private void BuildCloudDome()
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "CloudDome";

            // CreatePrimitive 会附带 MeshCollider，纯视觉物件不需要
            var col = go.GetComponent<Collider>();
            if (col != null) Destroy(col);

            go.transform.SetParent(transform, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localScale = Vector3.one * (DomeRadius * 2f);   // 原始球直径 = 1

            cloudMat = ProceduralAssets.MakeCloudMaterial(
                new Color(1f, 1f, 1f, 0f), "ProcCloudDome", 2995);

            var mr = go.GetComponent<MeshRenderer>();
            if (mr != null)
            {
                if (cloudMat != null) mr.sharedMaterial = cloudMat;
                mr.shadowCastingMode = ShadowCastingMode.Off;
                mr.receiveShadows = false;
                mr.lightProbeUsage = LightProbeUsage.Off;
                mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
            }

            cloudDome = go.transform;
            go.SetActive(false);
            domeOn = false;
        }

        /// <summary>
        /// 星空穹顶。比云穹顶略大一点，避免两层球面在远处 z-fighting。
        /// 星星固定在世界方向（穹顶只跟相机位置、不跟旋转），所以抬头看星空是稳的。
        /// </summary>
        private void BuildStarDome()
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "StarDome";

            var col = go.GetComponent<Collider>();
            if (col != null) Destroy(col);

            go.transform.SetParent(transform, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localScale = Vector3.one * (DomeRadius * 2.1f);

            starMat = ProceduralAssets.MakeSkyDomeMaterial(
                ProceduralAssets.StarField, new Color(1f, 1f, 1f, 0f), "ProcStarDome",
                0.55f, 0f, 0.10f, 2990);

            var mr = go.GetComponent<MeshRenderer>();
            if (mr != null)
            {
                if (starMat != null) mr.sharedMaterial = starMat;
                mr.shadowCastingMode = ShadowCastingMode.Off;
                mr.receiveShadows = false;
                mr.lightProbeUsage = LightProbeUsage.Off;
                mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
            }

            starDome = go.transform;
            go.SetActive(false);
            starOn = false;
        }

        /// <summary>
        /// 雨滴落地溅射。用一块贴在相机脚下的水平薄发射盒，粒子朝上抛出再落下。
        ///
        /// 为什么不做逐粒子碰撞：给几千个雨滴开 Collision 模块 + 子发射器，
        /// 每帧要做几千次碰撞查询，代价远大于收益，而且场景里大量网格没有碰撞体。
        /// 标准做法就是只做相机周围十几米，远处交给雾和整体色调 —— 肉眼也分辨不出。
        /// </summary>
        private ParticleSystem BuildSplash()
        {
            var go = new GameObject("RainSplash");
            go.transform.SetParent(transform, false);
            go.transform.localPosition = new Vector3(0f, -1.6f, 0f);

            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = ps.main;
            main.loop = true;
            main.prewarm = true;
            main.playOnAwake = true;
            main.duration = 2f;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.22f, 0.42f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(1.1f, 2.3f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.025f, 0.06f);
            main.startColor = new Color(0.80f, 0.88f, 0.98f, 0.50f);
            main.gravityModifier = 1.15f;                  // 抛起来再落回去
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 1600;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;

            var em = ps.emission;
            em.enabled = true;
            em.rateOverTime = 0f;

            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Box;
            // 注意：shape.rotation = (-90,0,0) 绕 X 轴把"局部 +Z → 世界 +Y"，
            // 所以粒子沿世界 +Y 向上溅起（水花"溅起再落下"）。
            // 旋转后各维映射为：局部 X → 世界 X（水平），局部 Y → 世界 Z（水平），
            // 局部 Z → 世界 Y（竖直）。因此"薄"的那一维必须放在 Z 上，
            // 否则 26m 高的 Z 维会被立起来变成一根 26m 高的竖直发射柱（之前就是这么崩的）。
            // 修正：X、Y 给 SplashArea（落地铺开 26×26），Z 给 0.05（贴地薄薄一层）。
            shape.scale = new Vector3(SplashArea, SplashArea, 0.05f);
            shape.rotation = new Vector3(-90f, 0f, 0f);

            var col = ps.colorOverLifetime;
            col.enabled = true;
            col.color = new ParticleSystem.MinMaxGradient(FadeGradient());

            var pr = go.GetComponent<ParticleSystemRenderer>();
            if (pr != null)
            {
                pr.renderMode = ParticleSystemRenderMode.Billboard;
                pr.minParticleSize = 0f;
                pr.maxParticleSize = 0.4f;
                pr.shadowCastingMode = ShadowCastingMode.Off;
                pr.receiveShadows = false;
                var m = ProceduralAssets.MakeSpriteMaterial(
                    ProceduralAssets.Snowflake, Color.white, "ProcSplashMat", 3002);
                if (m != null) pr.sharedMaterial = m;
            }

            ps.Play();
            splashRoot = go.transform;
            return ps;
        }

        /// <summary>
        /// 地面积雪：一块贴地的平面，贴图是"噪声 × 径向淡出"，所以看不到方形边界。
        /// 高度每 0.15 秒用一条向下射线贴一次地面（UpdateGround → ProbeGround）。
        ///
        /// 与旧版的区别：
        ///  · 材质换成 CampusUI/SnowGround（受主光 + 球谐环境光），不再是 Sprites/Default 的灰绿平涂；
        ///  · 平面网格被克隆出来，逐顶点向下射线做"地形跟随"（DrapeSnow），
        ///    让雪能盖住抬高的花坛/台阶，而不是在抬高处露出硬边、底下苔藓钻出来。
        /// </summary>
        private void BuildSnowGround()
        {
            // 不用 CreatePrimitive(Plane)：内建平面只有 10 段，铺到 90m 时每格 9m，
            // 地形跟随和雪堆起伏都会变成大台阶。这里手动建一块高细分单位平面网格。
            var go = new GameObject("SnowGround");
            var mf = go.AddComponent<MeshFilter>();
            var mr = go.AddComponent<MeshRenderer>();

            snowMesh = BuildSnowPlaneMesh(56);
            mf.sharedMesh = snowMesh;
            snowBaseVerts = (Vector3[])snowMesh.vertices.Clone();
            snowWorkVerts = (Vector3[])snowMesh.vertices.Clone();

            go.transform.SetParent(transform, false);
            go.transform.localPosition = new Vector3(0f, -1.6f, 0f);
            // 单位网格直接缩放 SnowGroundSize 米（x/z 方向）。
            go.transform.localScale = new Vector3(SnowGroundSize, 1f, SnowGroundSize);

            var sh = Shader.Find("CampusUI/SnowGround");
            if (sh == null)
            {
                Debug.LogWarning("[WeatherVisuals] 找不到 CampusUI/SnowGround 着色器，地面积雪效果不可用。");
                snowGroundMat = null;
            }
            else
            {
                snowGroundMat = new Material(sh);
                snowGroundMat.name = "ProcSnowGround";
                // 注意：不能用 mat.mainTexture —— 它只认 _MainTex，本着色器的贴图叫 _SnowTex。
                snowGroundMat.SetTexture(IdSnowTex, ProceduralAssets.SnowPatch);
                snowGroundMat.SetColor(IdSnowColor, new Color(0.92f, 0.95f, 1.0f, 1f));
                snowGroundMat.SetColor(IdSkyColor, new Color(0.74f, 0.82f, 0.96f, 1f));
                snowGroundMat.SetFloat(IdSnowAmount, 0f);
                snowGroundMat.SetFloat(IdEdgeFade, 1.0f);   // 环形边界交给 SnowPatch 贴图的径向淡出，这里不再二次裁剪
                snowGroundMat.renderQueue = 2996;   // 必须在积水层 2992 之后，积雪才能盖住积水
            }

            if (mr != null)
            {
                if (snowGroundMat != null) mr.sharedMaterial = snowGroundMat;
                mr.shadowCastingMode = ShadowCastingMode.Off;
                // 关键：雪面要接收主光阴影（角色/树的影子落在雪上），必须开 receiveShadows。
                mr.receiveShadows = true;
                mr.lightProbeUsage = LightProbeUsage.Off;
                mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
            }

            snowGround = go.transform;
            go.SetActive(false);
            snowGroundOn = false;
            snowDrapeDirty = true;

            BuildSnowFar();   // 远场白兜底：覆盖近场盘到雪天雾起之间的环带，消除地平线裸地形缺口
            EnsureTreeSnow(); // 自动挂载树根积雪组件(《异环》"树根被雪埋"近似)
        }

        /// <summary>
        /// 自动挂载树根积雪组件：在 WeatherVisuals 下生成一个子物体并挂上 SnowOnTrees，免去手动配置。
        /// 若场景里已经存在 SnowOnTrees 子物体(用户手动挂的)则跳过。
        /// </summary>
        private void EnsureTreeSnow()
        {
            if (GetComponentInChildren<SnowOnTrees>() != null) return;
            var tgo = new GameObject("TreeSnowPiles");
            tgo.transform.SetParent(transform, false);
            tgo.AddComponent<SnowOnTrees>();
        }

        /// <summary>
        /// 远场积雪盘（白兜底）：260m 级大圆盘，跟随相机，低分段、同样射线贴地并接收阴影 + 雾。
        /// 近场 SnowGround 只覆盖脚下 90m，雪天雾在 ≈120m 起；两者之间的环带若没有雪盘就会露出裸地形，
        /// 地平线方向看过去有一圈"没下雪"的地。远场盘把这个环带补上，且其远边被雪天雾完全淡出，看不到边界。
        /// 与近场盘共用 CampusUI/SnowGround 着色器（受主光 + 球谐 + 接收阴影 + 融雾），renderQueue 2995 低于近场 2996，
        /// 重叠区由近场盖住，远场只在 90~130m 环带可见。
        /// </summary>
        private void BuildSnowFar()
        {
            var go = new GameObject("SnowGroundFar");
            var mf = go.AddComponent<MeshFilter>();
            var mr = go.AddComponent<MeshRenderer>();

            snowMeshFar = BuildSnowPlaneMesh(40);   // 远场被雪天雾遮挡，分段可低（41×41≈1681 顶点）；比 32 段更细，减少雪面与地形穿插产生的块面
            mf.sharedMesh = snowMeshFar;
            snowBaseVertsFar = (Vector3[])snowMeshFar.vertices.Clone();
            snowWorkVertsFar = (Vector3[])snowMeshFar.vertices.Clone();

            go.transform.SetParent(transform, false);
            go.transform.localPosition = new Vector3(0f, -1.6f, 0f);
            go.transform.localScale = new Vector3(SnowGroundFarSize, 1f, SnowGroundFarSize);

            var sh = Shader.Find("CampusUI/SnowGround");
            if (sh == null)
            {
                Debug.LogWarning("[WeatherVisuals] 找不到 CampusUI/SnowGround 着色器，远场积雪不可用。");
                snowGroundFarMat = null;
            }
            else
            {
                snowGroundFarMat = new Material(sh);
                snowGroundFarMat.name = "ProcSnowGroundFar";
                // 注意：本着色器贴图叫 _SnowTex，不是 _MainTex。
                snowGroundFarMat.SetTexture(IdSnowTex, ProceduralAssets.SnowPatch);
                snowGroundFarMat.SetColor(IdSnowColor, new Color(0.92f, 0.95f, 1.0f, 1f));
                snowGroundFarMat.SetColor(IdSkyColor, new Color(0.74f, 0.82f, 0.96f, 1f));
                snowGroundFarMat.SetFloat(IdSnowAmount, 0f);
                snowGroundFarMat.SetFloat(IdEdgeFade, 1.0f);   // 环形边界交给 SnowPatch 贴图径向淡出
                snowGroundFarMat.renderQueue = 2995;   // 近场 2996 之下：重叠区近场盖远场
            }

            if (mr != null)
            {
                if (snowGroundFarMat != null) mr.sharedMaterial = snowGroundFarMat;
                mr.shadowCastingMode = ShadowCastingMode.Off;
                // 远场同样要接收主光阴影（树/建筑影子落在远场雪上）
                mr.receiveShadows = true;
                mr.lightProbeUsage = LightProbeUsage.Off;
                mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
            }

            snowGroundFar = go.transform;
            go.SetActive(false);
        }

        // =====================================================================
        // 应用
        // =====================================================================

        /// <summary>由 EnvironmentState 每帧调用；参数都是已经插值好的当前值。</summary>
        public void Apply(float rain, float snow, float wind, float cloud, Color cloudColor, float star,
                          Color skyColor, Color horizonColor, float dt)
        {
            EnsureBuilt();

            // ---- 室内外过渡 ----
            indoorFade = Mathf.MoveTowards(indoorFade, indoor ? 0f : 1f, dt * 3f);

            // ---- 积雪累积 ----
            // 这里的 snow 是"此刻下多大"，snowAccum 是"地上积了多少"，两者必须分开。
            // 直接拿 snow 当积雪量的话，雪一停地面立刻变干净（《异环》是渐进积雪 + 更慢的化雪）。
            if (snow > 0.02f)
                snowAccum = Mathf.MoveTowards(snowAccum, Mathf.Clamp01(snow), dt / 12f);   // 积：约 12 秒
            else
                snowAccum = Mathf.MoveTowards(snowAccum, 0f, dt / 20f);                    // 化：约 20 秒

            // 高空俯视时把粒子发射率按 altFade 压掉：从 200m 高空看雨丝物理上就看不见，
            // 硬画出来只会是相机正下方孤零零的一小团。
            // 室内再乘 shelter：雨雪不该穿过天花板，但也不能全关 —— 站在门口时窗外明明还在下。
            float shelter = Mathf.Lerp(0.25f, 1f, indoorFade);
            float effRain = rain * altFade * shelter;
            float effSnow = snow * altFade * shelter;

            if (rainPs != null && !Mathf.Approximately(effRain, lastRain))
            {
                lastRain = effRain;
                var em = rainPs.emission;
                em.rateOverTime = Mathf.Max(0f, effRain) * RainRateMax;

                var vel = rainPs.velocityOverLifetime;
                float lateral = wind * 5.5f;
                vel.x = new ParticleSystem.MinMaxCurve(lateral);
                vel.z = new ParticleSystem.MinMaxCurve(wind * 1.8f);
            }

            if (snowPs != null && !Mathf.Approximately(effSnow, lastSnow))
            {
                lastSnow = effSnow;
                var em = snowPs.emission;
                em.rateOverTime = Mathf.Max(0f, effSnow) * SnowRateMax;

                var vel = snowPs.velocityOverLifetime;
                vel.x = new ParticleSystem.MinMaxCurve(wind * 2.2f);
                vel.z = new ParticleSystem.MinMaxCurve(wind * 0.8f);

                var noise = snowPs.noise;
                noise.strength = 0.35f + wind * 0.7f;
            }

            if (cloudDome != null && !Mathf.Approximately(cloud, lastCloud))
            {
                lastCloud = cloud;
                bool on = cloud > 0.005f;
                if (on != domeOn)
                {
                    cloudDome.gameObject.SetActive(on);
                    domeOn = on;
                }
                SetDome(cloudMat, cloudColor, cloud);
            }

            // 云随风缓慢飘移
            if (domeOn && cloudDome != null)
                cloudDome.Rotate(0f, (2f + wind * 12f) * Time.deltaTime, 0f, Space.Self);

            // ---- 星空 ----
            // 云越厚能看到的星越少：阴天/雨天/雪天自动把星星盖掉，晴天夜里才满天星。
            if (starDome != null)
            {
                float a = Mathf.Clamp01(star) * (1f - Mathf.Clamp01(cloud) * 0.92f);
                bool on = a > 0.01f;
                if (on != starOn)
                {
                    starDome.gameObject.SetActive(on);
                    starOn = on;
                }
                if (!Mathf.Approximately(a, lastStarA))
                {
                    lastStarA = a;
                    SetDome(starMat, Color.white, a);
                }
            }

            // ---- 雨的落地溅射 ----
            // 室内不溅：水花只应该出现在露天地面。这里直接乘 indoorFade 而不是 shelter，
            // 因为室内还要留一点雨声/雨丝的底噪，但不该有"屋里地上冒水花"。
            float splashRate = Mathf.Max(0f, rain) * altFade * indoorFade * SplashRateMax;
            if (splashPs != null && !Mathf.Approximately(splashRate, lastSplashRain))
            {
                lastSplashRain = splashRate;
                var em = splashPs.emission;
                em.rateOverTime = splashRate;
            }

            // ---- 地面积雪（近场精细盘 + 远场白兜底盘，共同覆盖到地平线方向）----
            {
                // 用 snowAccum 而不是 snow：积雪是有惯性的
                float a = snowAccum * altFade * indoorFade;
                snowAmount = a;
                Shader.SetGlobalFloat(IdGlobalSnow, a);   // 全局雪量，留给后续本地化 Lit/TerrainLit 积雪使用
                bool on = a > 0.01f;

                if (snowGround != null)
                {
                    if (on != snowGroundOn)
                    {
                        snowGround.gameObject.SetActive(on);
                        snowGroundOn = on;
                        if (on) snowDrapeDirty = true;   // 刚开雪时强制重铺一遍地形
                    }
                    if (on && !Mathf.Approximately(a, lastSnowGroundA))
                    {
                        lastSnowGroundA = a;
                        if (snowGroundMat != null) snowGroundMat.SetFloat(IdSnowAmount, a);
                    }
                }

                // 远场白兜底盘：与近场共用积雪量，覆盖近场盘到雪天雾起之间的环带
                if (snowGroundFar != null)
                {
                    if (on != snowGroundFarOn)
                    {
                        snowGroundFar.gameObject.SetActive(on);
                        snowGroundFarOn = on;
                        if (on) snowDrapeDirtyFar = true;
                    }
                    if (on && !Mathf.Approximately(a, lastSnowGroundFarA))
                    {
                        lastSnowGroundFarA = a;
                        if (snowGroundFarMat != null) snowGroundFarMat.SetFloat(IdSnowAmount, a);
                    }
                }
            }

            // ---- 雨天积水 ----
            if (wet != null) wet.Apply(rain, dt, skyColor, horizonColor, indoor);
        }

        /// <summary>
        /// 设置穹顶材质的不透明度，同时兼容 SkyDome 着色器与 Sprites/Default 兜底。
        /// SkyDome 把颜色(_Color)和不透明度(_Opacity)分开，避免 alpha 同时参与混合与着色。
        /// </summary>
        // 积雪着色器（CampusUI/SnowGround）的属性 ID
        private static readonly int IdSnowTex = Shader.PropertyToID("_SnowTex");
        private static readonly int IdSnowColor = Shader.PropertyToID("_SnowColor");
        private static readonly int IdSkyColor = Shader.PropertyToID("_SkyColor");
        private static readonly int IdSnowAmount = Shader.PropertyToID("_SnowAmount");
        private static readonly int IdEdgeFade = Shader.PropertyToID("_EdgeFade");
        private static readonly int IdGlobalSnow = Shader.PropertyToID("_GlobalSnow");   // 全局雪量；后续本地化 Lit/TerrainLit 积雪时直接读取

        private static void SetDome(Material m, Color rgb, float opacity)
        {
            if (m == null) return;
            opacity = Mathf.Clamp01(opacity);

            if (m.HasProperty("_Opacity"))
            {
                m.SetColor("_Color", new Color(rgb.r, rgb.g, rgb.b, 1f));
                m.SetFloat("_Opacity", opacity);
            }
            else
            {
                m.color = new Color(rgb.r, rgb.g, rgb.b, opacity);
            }
        }

        // 整块跟着相机走（只跟位置，不跟旋转，否则云会跟着视角转）
        private void LateUpdate()
        {
            if (cam == null || !cam.isActiveAndEnabled)
            {
                cam = EnvironmentState.FindActiveCamera();
                if (cam == null) return;
            }

            transform.position = cam.transform.position;
            UpdateRange(cam.transform.position.y);
            UpdateGround(cam.transform.position);
        }

        /// <summary>
        /// 让溅射层和积雪层贴在地面上。
        /// 射线每 0.15 秒打一次就够 —— 玩家走动时地面高度变化是连续的，
        /// 每帧打射线纯属浪费（场景里有 6000+ 碰撞体）。
        /// </summary>
        private void UpdateGround(Vector3 camPos)
        {
            groundProbeTimer -= Time.deltaTime;
            if (groundProbeTimer <= 0f)
            {
                groundProbeTimer = 0.15f;
                groundY = ProbeGround(camPos);

                // 头顶 40m 内有遮挡 → 室内或屋檐下。
                // 和地面探测共用同一个 0.15s 节流：场景有 6000+ 碰撞体，两条射线每帧打是白给。
                // distance > 0.3 是为了排除"射线从玩家自己的胶囊里起步"这种情况；
                // 另外显式跳过玩家碰撞体，避免向上射线打到自己头顶的胶囊内壁。
                RaycastHit up;
                Transform playerRoot = (cam != null) ? cam.transform.root : null;
                indoor = Physics.Raycast(new Ray(camPos, Vector3.up), out up, 40f)
                         && up.distance > 0.3f
                         && !IsPlayerCollider(up.collider, playerRoot);
            }

            if (splashRoot != null)
                splashRoot.position = new Vector3(camPos.x, groundY + 0.03f, camPos.z);
            if (snowGround != null)
                snowGround.position = new Vector3(camPos.x, groundY + 0.02f, camPos.z);
            if (snowGroundFar != null)
                snowGroundFar.position = new Vector3(camPos.x, groundY + 0.02f, camPos.z);
            if (wet != null) wet.Follow(camPos, groundY);

            // 积雪地形跟随：让雪盖住抬高的花坛/台阶，而不是浮在半空露出硬边。
            // 跟随相机移动，所以只在相机挪动超过阈值时才重打一遍射线，省开销。
            if (snowGround != null) DrapeSnow(camPos);
            if (snowGroundFar != null) DrapeSnowFar(camPos);
        }

        /// <summary>
        /// 积雪地形跟随：把克隆出来的平面网格逐顶点向下射线，按脚下真实地面高度抬高/压低顶点，
        /// 让雪面"贴"住地形起伏（花坛、台阶、缓坡），而不是一整块平板浮在 groundY 上。
        ///
        /// 性能：平面 10 段细分 = 121 个顶点，每顶点一条短距离向下射线。只在相机移动超过
        /// 0.25m（或刚开雪）时重算，静止时直接跳过；射线距离仅 12m、且忽略玩家胶囊与触发器。
        /// </summary>
        private void DrapeSnow(Vector3 camPos)
        {
            if (snowMesh == null || snowBaseVerts == null || snowWorkVerts == null) return;
            // 重算条件：相机移动超过 0.25m、刚开雪、或积雪量变化较大（雪堆"慢慢长出来"）。
            if (!snowDrapeDirty
                && (camPos - lastDrapeCam).sqrMagnitude < 0.0625f
                && Mathf.Abs(snowAmount - lastDrapeAmount) < 0.04f) return;
            lastDrapeCam = camPos;
            snowDrapeDirty = false;

            float px = snowGround.position.x;
            float pz = snowGround.position.z;
            float baseY = groundY + 0.02f;          // 平面自身世界 Y（顶点 y=0 时的高度）
            float sx = SnowGroundSize;               // 单位网格缩放到 SnowGroundSize 米

            bool changed = false;
            for (int i = 0; i < snowBaseVerts.Length; i++)
            {
                Vector3 b = snowBaseVerts[i];
                float wx = px + b.x * sx;
                float wz = pz + b.z * sx;
                float th = SampleGroundHeight(wx, wz, camPos.y);
                // 严格按地形平铺：顶点抬到"射线地面 + 抬升量"上方。
                // 抬升量(SnowGroundLiftNear)必须盖过脚下可见铺装/砖面的凸起，否则砖面会戳穿雪面。
                // 不再叠加任何程序化雪堆 —— 用户反馈"取消堆叠的一团，按地形平铺就行"。
                float d = Mathf.Clamp((th + SnowGroundLiftNear) - baseY, -1.5f, 1.5f);
                if (Mathf.Abs(snowWorkVerts[i].y - d) > 0.01f)
                {
                    snowWorkVerts[i].y = d;
                    changed = true;
                }
            }

            if (changed)
            {
                snowMesh.vertices = snowWorkVerts;
                snowMesh.RecalculateNormals();   // 斜坡上的雪才有正确的受光法线
                snowMesh.RecalculateBounds();
            }
            lastDrapeAmount = snowAmount;
        }

        /// <summary>
        /// 远场雪盘地形跟随：逻辑同 DrapeSnow，作用于远场大圆盘（低分段）。
        /// 远场主要在雪天雾范围内，地形跟随误差被雾掩盖，仍保证地平线方向不露裸地形。
        /// </summary>
        private void DrapeSnowFar(Vector3 camPos)
        {
            if (snowMeshFar == null || snowBaseVertsFar == null || snowWorkVertsFar == null) return;
            if (!snowDrapeDirtyFar
                && (camPos - lastDrapeCamFar).sqrMagnitude < 0.0625f
                && Mathf.Abs(snowAmount - lastDrapeAmountFar) < 0.04f) return;
            lastDrapeCamFar = camPos;
            snowDrapeDirtyFar = false;

            float px = snowGroundFar.position.x;
            float pz = snowGroundFar.position.z;
            float baseY = groundY + 0.02f;
            float sx = SnowGroundFarSize;

            bool changed = false;
            for (int i = 0; i < snowBaseVertsFar.Length; i++)
            {
                Vector3 b = snowBaseVertsFar[i];
                float wx = px + b.x * sx;
                float wz = pz + b.z * sx;
                float th = SampleGroundHeight(wx, wz, camPos.y);
                // 远场格更大（每格 6.5m），抬升量(SnowGroundLiftFar)略高，进一步减少雪面与地形穿插造成的块面。
                float d = Mathf.Clamp((th + SnowGroundLiftFar) - baseY, -1.5f, 1.5f);
                if (Mathf.Abs(snowWorkVertsFar[i].y - d) > 0.01f)
                {
                    snowWorkVertsFar[i].y = d;
                    changed = true;
                }
            }

            if (changed)
            {
                snowMeshFar.vertices = snowWorkVertsFar;
                snowMeshFar.RecalculateNormals();
                snowMeshFar.RecalculateBounds();
            }
            lastDrapeAmountFar = snowAmount;
        }

        /// <summary>
        /// 程序化雪堆/沙丘高度 —— 已按用户要求整体移除。
        /// 原实现用世界 XZ 锁定的低频噪声在地面上方叠起伏（最高约 0.32m），
        /// 但读起来是"一团一团的堆"，与参考图不符；现在雪面严格贴地形平铺。
        /// </summary>

        /// <summary>
        /// 生成一块单位平面网格（XZ 水平、y=0、UV 0..1），seg 段细分。
        /// 用自定义网格替代内建 Plane：内建只有 10 段，铺到 90m 时每格 9m，
        /// 地形跟随 + 雪堆起伏都会变成大块台阶。这里给 56 段（57×57≈3249 顶点），
        /// 既能贴住地形，又能表现平滑雪堆。
        /// </summary>
        private static Mesh BuildSnowPlaneMesh(int seg)
        {
            int v = seg + 1;
            var verts = new Vector3[v * v];
            var uvs = new Vector2[v * v];
            var tris = new int[seg * seg * 6];
            float step = 1f / seg;
            for (int iz = 0; iz < v; iz++)
            {
                for (int ix = 0; ix < v; ix++)
                {
                    int idx = iz * v + ix;
                    verts[idx] = new Vector3(ix * step - 0.5f, 0f, iz * step - 0.5f);
                    uvs[idx] = new Vector2(ix * step, iz * step);
                }
            }
            int t = 0;
            for (int iz = 0; iz < seg; iz++)
            {
                for (int ix = 0; ix < seg; ix++)
                {
                    int a = iz * v + ix;
                    int b = a + 1;
                    int c = a + v;
                    int d = c + 1;
                    tris[t++] = a; tris[t++] = c; tris[t++] = b;
                    tris[t++] = b; tris[t++] = c; tris[t++] = d;
                }
            }
            var m = new Mesh();
            m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            m.vertices = verts;
            m.uv = uvs;
            m.triangles = tris;
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        /// <summary>
        /// 在 (x,z) 处向下射线取脚下地面高度。复用 groundHits 缓冲。
        /// 与 ProbeGround 一致：跳过触发器与玩家胶囊；只接受落在眼睛高度 ±4m 内的命中，
        /// 避免误中远处高楼/树冠。找不到地面时退回 groundY（也就是摊平，等于旧行为）。
        /// </summary>
        private float SampleGroundHeight(float x, float z, float eyeY)
        {
            Transform playerRoot = (cam != null) ? cam.transform.root : null;
            var origin = new Vector3(x, eyeY + 6f, z);
            int n = Physics.RaycastNonAlloc(new Ray(origin, Vector3.down), groundHits, 12f);

            float best = float.NegativeInfinity;
            bool found = false;
            for (int i = 0; i < n; i++)
            {
                RaycastHit h = groundHits[i];
                if (h.collider == null || h.collider.isTrigger) continue;
                if (IsPlayerCollider(h.collider, playerRoot)) continue;
                if (h.point.y > eyeY - 4f && h.point.y < eyeY + 6f)
                {
                    if (!found || h.point.y > best) { best = h.point.y; found = true; }
                }
            }
            return found ? best : groundY;
        }

        /// <summary>
        /// 向下射线取脚下地面高度。
        ///
        /// 关键修复（Bug A）：向下射线从相机出发，而相机（local y=1.62）正好在玩家
        /// CharacterController 胶囊体（高 1.8、底在玩家脚下、顶在 1.8）内部。
        /// 如果直接用 Physics.Raycast 取"第一个命中"，这条从胶囊内部出发的射线会命中
        /// 胶囊自己的内壁/顶盖，把 groundY 误判到眼高附近 —— 于是积水层/积雪层被停到
        /// 视线高度，变成横贯画面、能照出建筑倒影的反光带。
        ///
        /// 这里改用 RaycastNonAlloc 收集所有命中，跳过触发器与玩家自身碰撞体，
        /// 再在"低于相机"的命中里取最高的一层，那才是玩家真正踩着的地面。
        /// 站在楼层/平台上时也只会贴到脚下的那一层，而不是楼下的地面。
        /// </summary>
        private float ProbeGround(Vector3 camPos)
        {
            Transform playerRoot = (cam != null) ? cam.transform.root : null;
            int n = Physics.RaycastNonAlloc(new Ray(camPos, Vector3.down), groundHits, 400f);

            float best = float.NegativeInfinity;
            bool found = false;
            for (int i = 0; i < n; i++)
            {
                RaycastHit h = groundHits[i];
                if (h.collider == null) continue;
                if (h.collider.isTrigger) continue;                    // 触发器不算地面
                if (IsPlayerCollider(h.collider, playerRoot)) continue; // 跳过玩家胶囊
                if (h.point.y < camPos.y - 0.001f)                     // 只取相机下方的地面
                {
                    if (!found || h.point.y > best) { best = h.point.y; found = true; }
                }
            }

            if (found) return best;
            return camPos.y - 1.62f;   // 打不到地面（室内/悬空）就用眼高反推
        }

        /// <summary>判断碰撞体是不是玩家自身（玩家根节点或其子物体）。</summary>
        private bool IsPlayerCollider(Collider c, Transform playerRoot)
        {
            if (c == null || playerRoot == null) return false;
            return c.transform.IsChildOf(playerRoot);
        }

        private readonly RaycastHit[] groundHits = new RaycastHit[16];

        /// <summary>
        /// 按相机高度调整粒子范围与强度。
        ///  · 地面视角（第一人称）→ 雨雪全开，发射盒覆盖平视视野；
        ///  · 高空俯视（数字孪生）→ 逐步关掉粒子，靠雾/云/光照表达天气。
        /// </summary>
        private void UpdateRange(float camHeight)
        {
            // 60m 以下全开，130m 以上全关，中间平滑过渡（避免拉高相机时雨雪突然消失）
            altFade = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(60f, 130f, camHeight));

            // 发射盒水平尺寸：60m 只够盖住脚边，平视时 50m 外就没有雨，很假。
            // 默认给 110m（约等于 60° FOV 下 110m 远处的可见宽度），高处再放大。
            float wantArea = Mathf.Clamp(110f + Mathf.Max(0f, camHeight - 2f) * 3f, 110f, 200f);
            if (Mathf.Abs(wantArea - lastArea) > 1.5f)
            {
                lastArea = wantArea;
                var v = new Vector3(wantArea, 0.5f, wantArea);
                if (rainPs != null) { var s = rainPs.shape; s.scale = v; }
                if (snowPs != null) { var s = snowPs.shape; s.scale = v; }
            }
        }

        /// <summary>相机换了（比如从数字孪生切到第一人称）时重新绑定。</summary>
        public void RebindCamera() { cam = null; }
    }
}

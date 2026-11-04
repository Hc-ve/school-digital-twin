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
        private const float SplashArea = 18f;      // 雨滴溅射覆盖范围（相机脚下）。26→18：水花改成"贴地闪光"后
                                                   // 密度需求更高，范围收小能让近处更实、远处交给整体色调
        private const float SplashRateMax = 2600f;  // rain = 1 时每秒水花数。1300→2600：
                                                   // 生命从 0.22~0.42s 压到 0.10~0.20s 后，同屏存活数只剩原来的 1/2，
                                                   // 发射率必须翻倍才能维持视觉密度
        private const float SnowGroundSize = 90f;   // 近场地积雪盘边长（米）：覆盖第一人称脚下近场，高细分、贴地形、带雪堆起伏
        private const float SnowGroundFarSize = 260f;  // 远场白兜底盘边长（米）：半径 130m，覆盖近场盘到雪天雾起(≈120m)之间的环带；远边被雾完全淡出
        // 雪面抬离"射线采到的地面"的高度。
        // 关键修复（"雪看着很虚、只积在砖缝里"）：脚下庭院/广场是 SchoolBuilding.FBX 里的铺装网格，
        // 它高出地形、且没有碰撞体，所以向下射线只采到"地形"而非"可见铺装面"，
        // 雪面停在 地形+0.10，低于可见砖面 → ZTest LEqual 让砖面戳穿雪面，只剩砖缝凹槽露出白。
        // 把抬升量提到能盖过铺装凸起（庭院/砖面通常高出地形 0~0.25m），雪才能整片盖住地面。
        //
        // ⚠️【P13 概念：这个值是"用来盖住砖面凸起的填充量"，不是真实雪厚】
        // 但它**同时就是雪面的实际高度**，所以角色抬升必须以它为基准：
        //   vertexY = SampleGroundHeight(x,z) + SnowGroundLiftNear
        // 视觉上"雪面顶在哪"是唯一有意义的量，"填充量 vs 真实雪厚"的区分
        // 只对解释雪盘为什么这么高有用，对抬人没用 —— 拿真实雪厚 12cm 去抬，
        // 角色照样埋在小腿里（实测）。见 UpdatePlayerOnSnow 的长注释。
        private const float SnowGroundLiftNear = 0.30f;
        private const float SnowGroundLiftFar = 0.35f;

        // 室内判定阈值（见 UpdateGround 里的长注释）
        //
        // 【P13 收紧】原值 6m / -0.7 是在**不命中背面**的前提下定的。
        // 现在为了能打中单层天花板，射线开启了 queriesHitBackfaces，
        // 于是树冠的**背面**也会被命中 —— 而背面法线在 CG 里是把正面法线取反，
        // 树冠下缘的背面法线指向正下方（n.y 接近 -1），完全能通过 -0.7 的门槛。
        // 不收紧的话，站在树下就会被判成室内，雨雪全关 —— 又回到之前那个硬伤。
        //
        // 收紧成两道：
        //   ① 天花板必须**真的在头顶很近处**：建筑层高 3.3~4.5m，
        //      取 4.6m（层高上限 + 一点余量）。树冠底面通常 2.2m 起、中心在 5m+，
        //      4.6m 内确实可能命中树冠下缘，所以还要靠 ②。
        //   ② 天花板必须是**接近水平的平面**：n.y ≤ -0.92（约 ±23°）。
        //      树冠是球面，即便在下缘最正的那个点，相邻三角面的法线也会散开；
        //      取 -0.92 能滤掉绝大部分球面片，而平整楼板稳定在 -1.00。
        // 代价：拱形/斜面天花板的房间会漏判。本项目教学楼走廊都是平顶，可接受。
        private const float IndoorCeilHeight = 4.6f;
        private const float IndoorCeilNormalY = -0.92f;

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
        // 【P13】雪面几何自检的缓存：只在第一次重铺、以及自检窗口内打印，
        // 避免每帧重铺时刷爆 Console。
        private int lastSteepCount;
        private float lastSteepMaxDy;
        private float lastSteepStepM;
        private int drapeCount;
        private bool snowGeomLogged;
        private int geomLogCount;      // 几何日志总条数上限（见 DrapeSnow 里的节流说明）
        /// 地面积雪量（0~1）= snowAccum × altFade。
        /// **不含 indoorFade** —— 地上的雪是世界的，不随相机进楼而消失。
        /// 见 Update 里那段长注释（"室内外切换时雪地整片消失 / 闪烁"）。
        private float groundSnowAmount;
        private float lastDrapeAmount = -1f;

        /// <summary>
        /// 角色「脚下有没有雪」的系数 = groundSnowAmount × indoorFade。
        ///
        /// 为什么必须和 groundSnowAmount 分开：
        ///   · 地面雪是**世界属性** —— 我进不进楼，外面该有的雪都在（用 groundSnowAmount）。
        ///   · 角色站不站在雪上、踩不踩得出脚印，是**局部属性** —— 室内的地板上没有雪，
        ///     人不能凭空浮起 30cm、也不该在瓷砖上踩出雪印（用这个值）。
        /// 把这两个混成一个系数，就是上一版"进楼整片雪消失"的原因；
        /// 反过来若全用 groundSnowAmount，人会浮在大堂天花板上、室内满地脚印。
        /// </summary>
        private float playerSnowAmount;

        // ══════════════════════════════════════════════════════════════════
        // 【P13】雪盘的世界锚点
        //
        // 雪盘必须锚在两个"世界属性"上，而不是"相机所在的这一层"：
        //   · outdoorGroundY：**室外地平面**的高度。室内时冻结在最后一次室外值。
        //   · snowAnchor    ：雪盘的 XZ 中心。室内时同样冻结。
        //
        // ── 为什么不能用 groundY ─────────────────────────────────────────
        // groundY = ProbeGround(camPos) = 相机正下方第一个面。上到二楼后它就是
        // 2F 地板高度，于是：
        //   ① DrapeSnow 的 baseY 变成 2F 地板 → 整片雪面被抬到二楼窗台高度，
        //      变成一张悬空的白板；
        //   ② SampleGroundHeight 的 refY 也变成 2F → 判据"离 refY 最近的面"
        //      选中的是室内地板而不是室外地面 → 雪面被逐顶点捏成楼板的形状；
        //   ③ 玩家贴窗转头时，第三人称相机绕角色摆动，camPos 每帧都在动，
        //      groundY 在 2F 地板 / 楼梯平台之间跳 → 雪盘跟着上下弹 → **闪烁**。
        // 三个现象其实是同一个错误：把"室外地面的高度"取成了"相机脚下的地面"。
        //
        // ── 为什么 XZ 也要冻结 ───────────────────────────────────────────
        // 雪盘覆盖 90m，中心若跟着相机进屋，一半雪面就铺进楼里了 —— 一楼室内
        // 低头会看到地板上有雪。冻结在进楼前的位置，雪盘正好留在楼外，
        // 从窗户望出去依然是一片白，而室内地板干干净净。
        // 出门时第一帧就恢复跟随，差异只有这一帧的位移，肉眼看不出跳动。
        // ══════════════════════════════════════════════════════════════════
        private float outdoorGroundY;
        private bool outdoorGroundValid;
        private Vector3 snowAnchor;
        private bool snowAnchorValid;

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

        private float lastRain = -1f, lastSnow = -1f, lastCloud = -1f, lastStarA = -1f, lastOvercast = -1f;
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

        // 头顶射线命中详情，只给自检日志用（判断 indoor 是怎么得出的）
        private float ceilDist = -1f;      // 命中距离，-1 = 没命中
        private float ceilNormalY = 0f;    // 命中面的法线 Y
        private string ceilName = "";

        // 相机高度淡出系数：1 = 完全显示雨雪，0 = 高空俯视时关掉
        private float altFade = 1f;

        // ---------------------------------------------------------------
        // 【P13】只读诊断出口。
        // 为什么要把私有字段暴露出来：`indoor` / `altFade` / `snowAccum` 三个值决定了
        // 雨雪到底显不显示 —— effRain = rain × altFade × shelter、积雪 = snowAccum ×
        // altFade × indoorFade。而这四个都是 private，诊断工具只能靠"用同一个公式重算"
        // 去猜，猜不出真实值。
        //
        // 踩过的坑：雪天粒子出来了、但地面一点积雪都没有，最可能就是 indoorFade 被
        // 误判成 0（第三人称相机在玩家身后上方，向上射线可能打到玩家自己的手臂/模型，
        // 而 IsPlayerCollider 靠 cam.transform.root 判，相机若不是玩家子物体就失效）。
        // 没有真实值只能猜，所以补这三个属性。只读，不参与任何逻辑。
        // ---------------------------------------------------------------
        public bool IndoorNow { get { return indoor; } }
        public float IndoorFadeNow { get { return indoorFade; } }
        public float AltFadeNow { get { return altFade; } }
        public float SnowAccumNow { get { return snowAccum; } }
        public float SnowAmountNow { get { return groundSnowAmount; } }

        /// <summary>雪盘物体本身在不在、激活没激活、材质是不是拿到了（诊断"雪有量但不显示"）。</summary>
        public Transform SnowGroundT { get { return snowGround; } }
        public bool SnowGroundHasMat { get { return snowGroundMat != null; } }
        public bool SnowGroundShaderOk
        {
            get { return snowGroundMat != null && snowGroundMat.shader != null
                     && snowGroundMat.shader.name == "CampusUI/SnowGround"; }
        }
        /// <summary>写进材质的真实 _SnowAmount（诊断"值算出来了但没写进材质"）。</summary>
        public float SnowAmountInMat
        {
            get { return snowGroundMat != null ? snowGroundMat.GetFloat(IdSnowAmount) : -1f; }
        }
        /// <summary>雪盘世界坐标（诊断"雪盘被停到天上/地下"）。</summary>
        public Vector3 SnowGroundWorldPos
        {
            get { return snowGround != null ? snowGround.position : Vector3.zero; }
        }
        /// <summary>雪盘当前是否在渲染（含父级激活状态）。</summary>
        public bool SnowGroundVisible
        {
            get { return snowGround != null && snowGround.gameObject.activeInHierarchy; }
        }

        /// <summary>雪盘的 MeshRenderer 是否被本帧相机看见（不看见就不是材质问题，是包围盒/剔除问题）。</summary>
        public bool SnowGroundRendered
        {
            get
            {
                if (snowGround == null) return false;
                var mr = snowGround.GetComponent<MeshRenderer>();
                return mr != null && mr.isVisible;
            }
        }

        /// <summary>雪盘网格包围盒中心/尺寸（诊断"网格塌成 0 大小"）。</summary>
        public string SnowGroundBounds
        {
            get
            {
                if (snowMesh == null) return "null";
                Bounds b = snowMesh.bounds;
                return "c=" + b.center.ToString("F2") + " s=" + b.size.ToString("F2");
            }
        }

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
            // ══════════════════════════════════════════════════════════════
            // 【P13 修的真 bug：雨丝会穿到地面以下，"消失得很突然"被读成回弹】
            //
            // ⚠️ 先纠正一个**必须靠实测才能发现的错误前提**：
            // 发射器挂在相机上（transform.position = cam.position，粒子节点 localPosition
            // = +EmitHeight），所以"离地高度"不等于 EmitHeight，而是
            //        落距 = EmitHeight + (camY - groundY)
            // 第三人称相机离地约 4m → 落距 20m；第一人称 1.6m → 17.6m。
            // 如果按 16m 硬算，得到的 lifetime 会让雨丝在**离地 4~5m 的空中**集体消失，
            // 那就从"扎地"变成了"空中凭空没掉"，同样是玩家能看出来的破绽。
            // 所以 lifetime 由 UpdateRainLifetime() 按实测落距动态设定，这里只给初值。
            //
            // g = 9.81 × gravityModifier(1.3) = 12.75 m/s²，自由落体 t = √(2h/g)：
            //   h=20m → t=1.77s     h=17.6m → t=1.66s
            // 取 lifetime ≈ t（略早一点），配合 colorOverLifetime 在最后 18% 淡出：
            // alpha 归零的那一刻正好贴地，雨丝"落进地面"而不是"断在半空"。
            //
            // 原值 1.4~1.9 的问题（这才是数据证据）：
            //   · 下限 1.4s：h=20m 时落距只走了 12.5m，雨丝在空中就没了 ✗
            //   · 上限 1.9s：比 1.77s 多 0.13s，粒子穿到**地下 1.1m** 才销毁，
            //     且 82% 处(t=1.56)离地仅 4.6m 就淡出完 → "飘到半空忽然散掉" ✗
            //   上下两端各错一个方向，这才是"落地像弹了一下"的观感来源。
            // ══════════════════════════════════════════════════════════════
            main.startLifetime = new ParticleSystem.MinMaxCurve(1.62f, 1.76f);   // 初值，UpdateRainLifetime 按落距重设
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
            // 【P13】与雪同修：Box 的 startSpeed 沿局部 +Z，rotation=0 时是**水平**方向。
            // 雨的 startSpeed 只有 0~0.4 m/s，横飞量不大，但近处仍能看出雨丝"往旁边撇"。
            // 转 -90° 后局部 +Z → 世界 -Y，初速变成向下，与重力同向，雨丝笔直。
            shape.rotation = new Vector3(-90f, 0f, 0f);

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
            // 【P13】雪花寿命与雨丝同理，必须按实测落距算，不能写死 8~12s。
            //
            // 雪是"慢落"的：gravityModifier=0.035 → g=0.343 m/s²，
            // 初速方向是 shape 的 ±Z（水平），所以竖直方向**完全靠重力起速**。
            //   落距 20m → t = √(2×20/0.343) = 10.8s
            //   落距 17.6m（第一人称）→ 10.1s
            // 原值 8~12s 恰好横跨这个值，意味着：
            //   · lifetime = 8s 的雪花只落了 11m，在**相机上方 9m 的空中**就消失了 ✗
            //   · lifetime = 12s 的雪花落到地下 6.7m 才销毁（穿地，虽然看不见，
            //     但白点在贴地那一刻从"正常大小"突然消失，同样不自然）
            // 只有两端都收到真实落距的 ±2% 内，雪花才是在"落到地面那一刻化掉"。
            // 具体值由 UpdateSnowLifetime() 每 0.5s 刷新，这里只是初值。
            main.startLifetime = new ParticleSystem.MinMaxCurve(10.4f, 10.8f);   // 初值，UpdateSnowLifetime 按落距重设
            // 【P13】初速方向必须朝下，不能沿用默认的局部 +Z。
            //
            // Box 形状的 startSpeed 是沿**局部 +Z** 发射的。原实现 shape.rotation = 0，
            // 于是雪花先水平飞 0.3~0.8 m/s，竖直方向只靠 gravityModifier=0.035 慢慢起速
            // —— 观感上是"从头顶一块看不见的天花板上横着吹出来"，
            // 尤其贴近相机时能看到雪片往外扑，不是往下落。
            //
            // 修法与 BuildSplash 同一套：shape.rotation = (-90,0,0) 让局部 +Z → 世界 -Y。
            // 初速同时收到 0.25~0.6：雪片本身下落速度就是 0.3~1 m/s 量级，
            // 太大就成了冰雹，太小又全靠重力慢慢加速（要 10 秒才追上落距）。
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.25f, 0.60f);
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
            // 旋转后：局部 X → 世界 X，局部 Y → 世界 Z，局部 Z → 世界 -Y（向下）。
            // 所以 scale 的 X/Z 是水平铺开、Y 是薄片厚度；startSpeed 沿 -Y 落下。
            shape.scale = new Vector3(AreaSize, 0.5f, AreaSize);
            shape.position = Vector3.zero;
            shape.rotation = new Vector3(-90f, 0f, 0f);

            // 噪声扰动：雪花左右打旋，是"雪"和"白点雨"最大的观感差别。
            // 注意 strength 不能太大：粒子一旦被噪声推得比下落还快，
            // 就会看到雪片"横着飘"而不是"斜着落"。
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

        /// <summary>
        /// 「一闪即灭」曲线，专给落地水花用。
        ///
        /// 与 <see cref="FadeGradient"/> 的区别在于**没有保持段**：
        ///   FadeGradient  0 → 1(12%) → 1(82%) → 0   中间 70% 的时间是常亮的
        ///   FlashGradient 0 → 0.85(6%) → 0(100%)    出现即开始衰减
        ///
        /// 为什么这个差别重要：雨花是"一瞬"的现象，如果粒子在半空保持常亮几十毫秒，
        /// 眼睛就会抓住它的位置和形状，读成"有个白点飞起来了"（就是玩家说的回弹感）。
        /// 让它一出现就衰减，视觉上只剩"地面闪了一下"，正是水花该有的样子。
        /// </summary>
        private static Gradient FlashGradient()
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
                    new GradientAlphaKey(0.85f, 0.06f),
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
        /// 雨滴落地溅射。
        ///
        /// ══════════════════════════════════════════════════════════════════
        /// 【P13 修的真 bug：这个模块就是玩家反馈的「粒子落地会回弹一下」】
        ///
        /// 原实现是「粒子向上抛出、重力落下」，参数是：
        ///     startSpeed   = (1.1, 2.3) m/s   ← 初速朝上
        ///     gravityModifier = 1.15
        ///     startLifetime = (0.22, 0.42) s
        /// 初速 2.3 + 重力 1.15g，在 0.42 秒内粒子会升到约 0.8m 高再落回来 ——
        /// 描出的正是一条"弹起-落下"的抛物线。玩家看到的就是"雨滴落地后弹了一下"。
        ///
        /// 为什么这不是真实的雨花：真实的雨滴溅射是**贴着地面向四周散开**的水沫，
        /// 高程只有几厘米，而且是**水平方向**的速度占主导。
        /// 我们原来给的是一颗颗垂直上抛的小球，那是"弹珠"不是"水花"。
        ///
        /// 【修法】三处一起改，缺一不可：
        ///   ① startSpeed 从 1.1~2.3 压到 0.06~0.30 —— 几乎不上升，只做微小的"溅起"
        ///   ② gravityModifier 从 1.15 提到 2.6 —— 极短的弧线，0.1 秒内就被压回地面，
        ///      不会给出"飞起来"的视觉时间
        ///   ③ startLifetime 从 0.22~0.42 收到 0.10~0.20 —— 只留"一闪"，
        ///      不给眼睛捕捉抛物线的机会
        ///   ④ 发射盒的薄维（Z，旋转后对应世界 Y）从 0.05 收到 0.012，
        ///      且 startSize 收到 0.012~0.030 —— 变成一小片贴地的白沫
        ///   ⑤ 专用的 alpha 曲线（不用 FadeGradient）：瞬间出现、立刻淡出，
        ///      原来的 12%→82% 保持段会让"停留在半空的小白点"被看清
        /// ══════════════════════════════════════════════════════════════════
        ///
        /// 为什么不做逐粒子碰撞：给几千个雨滴开 Collision 模块 + 子发射器，
        /// 每帧要做几千次碰撞查询，代价远大于收益，而且场景里大量网格没有碰撞体。
        /// 标准做法就是只做相机周围十几米，远处交给雾和整体色调 —— 肉眼也分辨不出。
        /// </summary>
        private ParticleSystem BuildSplash()
        {
            var go = new GameObject("RainSplash");
            go.transform.SetParent(transform, false);
            // y 先给 0，随后由 UpdateGround 每帧写成 groundY + 0.03（见 splashRoot）。
            // 原来写 -1.6 是个陷阱：splashRoot 就是这个 transform，世界位置会被覆盖，
            // 所以在"建好但还没跑第一帧 UpdateGround"的那一小段时间里，
            // 水花会埋在地下一米六 —— 切换天气的瞬间能看到一帧异常。
            go.transform.localPosition = Vector3.zero;

            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = ps.main;
            main.loop = true;
            main.prewarm = true;
            main.playOnAwake = true;
            main.duration = 2f;
            // ① 极短生命：只留"一闪"，不给眼睛捕捉抛物线的机会
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.10f, 0.20f);
            // ② 几乎不上升：真实水沫是贴地散开的
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.06f, 0.30f);
            // ③ 尺寸收到厘米级：一小片白沫，不是一颗小珠子
            main.startSize = new ParticleSystem.MinMaxCurve(0.012f, 0.030f);
            main.startColor = new Color(0.80f, 0.88f, 0.98f, 0.42f);
            // ④ 重力提到 2.6：0.1 秒内就被压回地面，弧线短到看不出"飞起来"
            main.gravityModifier = 2.6f;
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
            // 所以粒子沿世界 +Y 向上溅起。
            // 旋转后各维映射为：局部 X → 世界 X（水平），局部 Y → 世界 Z（水平），
            // 局部 Z → 世界 Y（竖直）。因此"薄"的那一维必须放在 Z 上，
            // 否则 26m 高的 Z 维会被立起来变成一根 26m 高的竖直发射柱（之前就是这么崩的）。
            // 修正：X、Y 给 SplashArea（落地铺开 26×26），Z 给 0.012（贴地薄薄一层）。
            shape.scale = new Vector3(SplashArea, SplashArea, 0.012f);
            shape.rotation = new Vector3(-90f, 0f, 0f);

            // ⑤ 专用曲线：瞬间出现、立刻淡出。不用 FadeGradient 的 12%~82% 保持段。
            var col = ps.colorOverLifetime;
            col.enabled = true;
            col.color = new ParticleSystem.MinMaxGradient(FlashGradient());

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
                // 【P13】脏污细节图：必须与 _SnowTex 分开（前者是带径向淡出的遮罩、
                // wrapMode=Clamp，后者是可平铺噪声、wrapMode=Repeat），详见 SnowGround.shader 里的说明。
                snowGroundMat.SetTexture(IdSnowDetail, ProceduralAssets.SnowDetail);
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
                snowGroundFarMat.SetTexture(IdSnowDetail, ProceduralAssets.SnowDetail);
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
        /// <param name="overcast">阴天帷幔 0~1：平坦压住天空底色，与 <paramref name="cloud"/> 分工见 CloudDome.shader。</param>
        public void Apply(float rain, float snow, float wind, float cloud, float overcast, Color cloudColor, float star,
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
            //
            // ══════════════════════════════════════════════════════════════════
            // 【P13 修的真 bug：室内还有雨雪粒子】
            //
            // 原实现： shelter = Mathf.Lerp(0.25f, 1f, indoorFade);
            // 完全室内时 indoorFade = 0，但公式给的是 **0.25 的底噪**，不是 0。
            // 于是室内仍有 25% 的雨雪（rain=1 时约 800 粒/秒）在相机周围飞，
            // 自检里 "indoor = True / indoorFade = 0.000" 却仍看到粒子，就是这个原因。
            //
            // 为什么当初留了 0.25：注释写的是"站在门口时窗外明明还在下"。
            // 但这个想法在这里不成立 —— 发射盒是**跟着相机**的（AreaSize 以相机为中心），
            // 它只会把雨雪生成在你身上，不可能生成到"窗外"。所以留底噪纯粹是错的。
            //
            // 正确做法：直接用 indoorFade 本身（室内 = 0，室外 = 1）。
            // indoorFade 以 dt×3 变化，约 0.33 秒完成过渡，进门时的淡出足够柔和。
            // ══════════════════════════════════════════════════════════════════
            float shelter = indoorFade;
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

            // 雨丝寿命必须按**实测落距**重算，不能写死。
            // 发射器挂在相机上、groundY 每 0.15s 更新一次，两者都会变：
            // 走上下坡、进出室内（groundY 突变）、相机拉远拉近（camY 变），
            // 落距可能从 17m 变到 25m。写死的 lifetime 只对某一个落距成立，
            // 其余情况不是"空中断掉"就是"扎进地下"——两个都是玩家能看出来的。
            UpdateRainLifetime(dt);

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

            // 雪花寿命同理（见 UpdateSnowLifetime 的注释：8~12s 的写死值会让一半雪花在空中蒸发）
            UpdateSnowLifetime(dt);

            // ---- 云穹顶 + 阴天帷幔 ----
            //
            // 【P13】开关判据从"只看 cloud"改成"cloud 或 overcast 任一非零"。
            // 原实现雪天 cloud=0.62 会开、但 Fog 档 cloud=0.40 也开，只是**遮不住蓝天**；
            // 现在加了独立的 overcast（阴天帷幔），必须一起参与开关与重写判断，
            // 否则"云很少但阴得很重"的档位（Fog / Overcast）根本不会更新材质。
            {
                if (cloudDome != null &&
                    (!Mathf.Approximately(cloud, lastCloud) || !Mathf.Approximately(overcast, lastOvercast)))
                {
                    lastCloud = cloud;
                    lastOvercast = overcast;
                    bool on = Mathf.Max(cloud, overcast) > 0.005f;
                    if (on != domeOn)
                    {
                        cloudDome.gameObject.SetActive(on);
                        domeOn = on;
                    }
                    SetDome(cloudMat, cloudColor, cloud, overcast);
                }
            }

            // 云随风缓慢飘移
            if (domeOn && cloudDome != null)
                cloudDome.Rotate(0f, (2f + wind * 12f) * Time.deltaTime, 0f, Space.Self);

            // ---- 星空 ----
            // 云越厚能看到的星越少：阴天/雨天/雪天自动把星星盖掉，晴天夜里才满天星。
            if (starDome != null)
            {
                // 【P13】遮蔽强度要取 max(cloud, overcast) 而不是只看 cloud。
                // 雪夜 cloud=0.62 只遮 92%*0.62 = 57%，会剩 43% 的星星露出来 ——
                // 但雪天 overcast=0.94，天空实际是**全阴**，物理上一颗星都不该看见。
                // 这是"同一件事两个状态源"的又一例：遮天的是"阴"，不是"云多"。
                //
                // ⚠️ 变量名不能叫 shelter —— 本方法第 629 行已有 `float shelter`（室内庇护系数），
                // 同作用域重复声明是 CS0136。这两个概念也确实不同，改叫 skyCover 更准确。
                float skyCover = Mathf.Max(Mathf.Clamp01(cloud), Mathf.Clamp01(overcast));
                float a = Mathf.Clamp01(star) * (1f - skyCover * 0.98f);
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
                // 用 snowAccum 而不是 snow：积雪是有惯性的。
                //
                // ══════════════════════════════════════════════════════════════════
                // 【P13 修的真 bug：室内外切换时雪地整片消失 / 闪烁】
                //
                // 原式是 `a = snowAccum * altFade * indoorFade` —— 把地面当成
                // 与相机位置绑定的局部属性。两个必然的后果：
                //
                //   ① 走进楼里 → indoorFade → 0 → a = 0 → 整片雪地被 SetActive(false)。
                //      可透过一楼大门、二楼窗户看出去时，**室外的雪是真的在下、真的积着**。
                //      用户看到的就是"回室内雪会消失"。
                //   ② 二楼贴窗时，相机在室内、向下射线打到的是地板（室内），
                //      indoorFade 在 0/1 之间反复抖；一旦 hover 在 0.01 的激活阈值
                //      附近，雪地就一帧开一帧关 —— 这正是"雪地闪烁消失"。
                //
                // 为什么不能靠"提高 indoor 判定精度"绕过：
                // 判定的目的是"这一小片粒子是不是在我身上"，而**落在地上的雪是世界的**，
                // 和我在哪没有关系。把这两件事用同一个系数耦合本身就是设计错误。
                //
                // 正确做法：地面只由「积了多少雪」×「相机是不是在高空」决定，
                // 完全不受室内外影响。室内看不见的雪由遮挡自己解决（墙就在那儿），
                // 不需要我们主动关掉它。
                // ══════════════════════════════════════════════════════════════════
                float a = snowAccum * altFade;
                groundSnowAmount = a;
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

            // ---- 【P13】角色站到雪面上（视觉抬升，不动物理）----
            // 用 playerSnowAmount（含 indoorFade）：室内地板没有雪，人不能浮起来。
            playerSnowAmount = groundSnowAmount * indoorFade;
            UpdatePlayerOnSnow(dt, playerSnowAmount);

            // ---- 【P13】雪地脚印 ----
            UpdateFootprints(playerSnowAmount);

            // ---- 【P13】雪天自检：切到雪天后自动打一次现场快照 ----
            // 为什么不做成菜单：雪天"有粒子无积雪"这种故障，必须看**当时的**运行时值。
            // 让用户 Play 中手动点菜单、再把 Console 截图发回来，中间会丢一轮；
            // 直接在雪量第一次冲破阈值时自己打一次，Editor.log 里就有全部答案。
            TickSnowAudit(dt, snow);

            // ---- 雨天积水 ----
            if (wet != null) wet.Apply(rain, dt, skyColor, horizonColor, indoor);
        }

        // =====================================================================
        // 【P13】雪天自检
        // =====================================================================

        private int snowAuditStage;        // 0=待触发 1=已排程 2=已打完
        private float snowAuditTimer;
        private bool indoorAuditDone;      // 室内快照是否已打过
        private float indoorAuditTimer = 1.5f;
        private float rainLifeTimer;       // 雨丝寿命重算的节流计时（见 UpdateRainLifetime）
        private float snowLifeTimer;       // 雪花寿命重算的节流计时（见 UpdateSnowLifetime）

        /// <summary>
        /// 雪天「有粒子、地上没白」的自动取证。
        ///
        /// 触发条件：snow &gt; 0.5（真在雪天）且还没打过。等 4 秒 —— 积雪累积速率是 dt/12，
        /// 4 秒才到 0.33，刚够跳出"几乎看不见"的区间；等太久用户又会以为没反应。
        ///
        /// 打出来的每一条都对应一个具体假设，不用再猜：
        ///   · snowAccum = 0         → 累积没跑（snow 参数没传进来）
        ///   · indoorFade = 0        → indoor 误判（头顶射线打到玩家自己）
        ///   · altFade = 0           → 相机高度判定错（相机被停到高空）
        ///   · _SnowAmount = 0       → 值算对了但没写进材质（SetFloat 分支没进）
        ///   · activeInHier = false  → 开关阈值判断错
        ///   · isVisible = false     → 材质/网格没问题，是相机看不见（包围盒或位置）
        /// </summary>
        private void TickSnowAudit(float dt, float snow)
        {
            // 室内单独打一次快照。
            //
            // 为什么必须单独打：室外那次是在 snow>0.5 后 4 秒触发的，
            // 而"室内还有粒子 / 室内有横线"这类问题**只在室内复现**。
            // 用户在室内遇到问题时，室外那一次早就打完了（snowAuditStage==2），
            // 什么日志都拿不到，只能靠截图猜。这里补一条独立的室内取证。
            if (snow > 0.5f && indoor && !indoorAuditDone)
            {
                indoorAuditTimer -= dt;
                if (indoorAuditTimer <= 0f)
                {
                    indoorAuditDone = true;
                    Debug.Log("[P13][室内自检] ==== 室内（或在楼里）现场快照 ====\n" + BuildIndoorAudit());
                }
            }
            else if (!indoor)
            {
                // 出门后重置，方便下次进楼再取一次
                indoorAuditDone = false;
                indoorAuditTimer = 1.5f;
            }

            if (snowAuditStage == 2) return;

            if (snowAuditStage == 0)
            {
                if (snow <= 0.5f) return;
                snowAuditStage = 1;
                snowAuditTimer = 4f;
                return;
            }

            snowAuditTimer -= dt;
            if (snowAuditTimer > 0f) return;

            snowAuditStage = 2;
            Debug.Log("[P13][雪天自检] ==== 雪天 4 秒现场快照 ====\n" + BuildSnowAudit());
        }

        /// <summary>
        /// 室内现场快照。只回答一个问题：**为什么人已经在楼里，雪/雨/水还看得见**。
        ///
        /// 关键几项：
        ///   · 头顶射线命中详情 —— 没命中 = 天花板是单层网格且背面没被算进来，
        ///     这正是"室内还有粒子"的根因（queriesHitBackfaces 默认 false）。
        ///   · indoorFade —— 它 0.33 秒才走完，刚进门那一瞬间仍是 1，属正常。
        ///   · 雪盘锚点与世界高度 —— 判断那条"室内横线"是不是浮起来的雪面。
        ///   · 雪盘顶点里有多少个被判定为"非地面"（楼板/屋顶）而沉到地下。
        /// </summary>
        private string BuildIndoorAudit()
        {
            var sb = new System.Text.StringBuilder(700);
            sb.AppendLine("    indoor     = " + indoor + "   / indoorFade = " + indoorFade.ToString("F3"));
            sb.AppendLine("    头顶射线   = " + (ceilDist < 0f
                              ? "**什么都没打到** ← 天花板没被命中，这就是室内还有粒子的原因"
                              : ceilName + "  " + ceilDist.ToString("F2") + "m  法线Y=" + ceilNormalY.ToString("F2"))
                          + "   （要求：距离 ≤ " + IndoorCeilHeight.ToString("F1")
                          + " 且 法线Y ≤ " + IndoorCeilNormalY.ToString("F2") + "）");
            sb.AppendLine("    rainPs      = " + (rainPs != null ? rainPs.emission.rateOverTime.constantMax.ToString("F1") + " 粒/秒" : "null"));
            sb.AppendLine("    snowPs      = " + (snowPs != null ? snowPs.emission.rateOverTime.constantMax.ToString("F1") + " 粒/秒" : "null")
                          + "   （室内应接近 0）");

            sb.AppendLine("  雪盘：");
            if (snowGround != null)
            {
                sb.AppendLine("    worldPos     = " + snowGround.position.ToString("F2"));
                sb.AppendLine("    anchor       = (" + snowAnchor.x.ToString("F2") + ", " + snowAnchor.z.ToString("F2") + ")"
                              + (indoorFade > 0.5f ? "   跟随" : "   **已冻结**"));
                sb.AppendLine("    室外地面 Y   = " + outdoorGroundY.ToString("F2")
                              + "   相机脚下 groundY = " + groundY.ToString("F2"));
                sb.AppendLine("    activeInHier = " + SnowGroundVisible);
                sb.AppendLine("    _SnowAmount  = " + SnowAmountInMat.ToString("F3"));
            }
            else sb.AppendLine("    snowGround = null");

            // ── 雪盘网格的真实纵向跨度 ────────────────────────────────────
            // 这是判断"那条横线还在不在"的直接指标，比看 worldPos.y 有用得多。
            //
            // 横线的成因是网格里出现了**大跨度竖直三角形**：某个顶点被拉到很低的
            // 位置（旧版 -10m），与相邻顶点连成的四边形被斜劈 → 半边成了竖直的墙。
            // 这些墙沿建筑轮廓围成一圈，低角度看就收束成一条横贯画面、能穿墙的细线。
            //
            // 所以：
            //   局部 y 跨度 ≤ 约 2.5m  → 正常（雪面就是在室外地平面上下小幅起伏）
            //   局部 y 跨度 ≥ 8m       → 说明仍有顶点被甩到远处，横线必然还在
            if (snowMesh != null)
            {
                Bounds bb = snowMesh.bounds;
                sb.AppendLine("    mesh.bounds Y   c=" + bb.center.y.ToString("F2")
                              + "  size=" + bb.size.y.ToString("F2")
                              + "   局部 y∈[" + (bb.center.y - bb.extents.y).ToString("F2")
                              + ", " + (bb.center.y + bb.extents.y).ToString("F2") + "]"
                              + (bb.size.y >= 8f ? "   ** 跨度异常大 → 仍有顶点被甩远，横线会在 **" : "   正常"));
                sb.AppendLine("    竖直面计数   = " + lastSteepCount + " 处"
                              + (lastSteepCount > 0
                                  ? "   **就是横线**，最大高差 " + lastSteepMaxDy.ToString("F2")
                                    + "m（间距 " + lastSteepStepM.ToString("F2") + "m）"
                                  : "   （应为 0；非 0 即横线来源）"));
            }

            sb.AppendLine("  用户报过的问题对照：");
            sb.AppendLine("    · 室内外白色横线 → 看【雪面几何】那行的'竖直面'计数"
                          + "（>0 就是横线来源；正常应为 0）");
            sb.AppendLine("    · 室内有粒子 → 看头顶射线是否 '什么都没打到'"
                          + "（已开启 queriesHitBackfaces 以命中单层天花板）");
            return sb.ToString();
        }

        /// <summary>
        /// 按**实测落距**维护雨丝寿命。
        ///
        /// 发射器挂在相机上（localPosition.y = +EmitHeight），所以雨丝真正要走的距离是
        ///     落距 = EmitHeight + (camY - groundY)
        /// 而不是常量 EmitHeight。第三人称相机离地 4m → 20m；第一人称 1.6m → 17.6m；
        /// 相机弹簧臂拉远时还会更高。落距变了而 lifetime 不变，雨丝就会在离地
        /// 若干米的空中集体消失 —— 从"扎进地里"变成"半空蒸发"，破绽一样明显。
        ///
        /// 解析解：h = ½gt² ⇒ t = √(2h/g)，g = 9.81 × gravityModifier。
        /// 用 t 直接当 lifetime：粒子刚好落到地面时生命结束，配合最后 18% 的淡出，
        /// 视觉上就是"雨丝落到地上化掉"。
        ///
        /// 节流 0.5s：groundY 本身 0.15s 才更新一次，而落距是缓慢连续量，
        /// 每帧重设 MinMaxCurve 会白白产生 GC（它会装箱）。
        /// </summary>
        private void UpdateRainLifetime(float dt)
        {
            if (rainPs == null) return;

            rainLifeTimer -= dt;
            if (rainLifeTimer > 0f) return;
            rainLifeTimer = 0.5f;

            if (cam == null) return;

            float drop = EmitHeight + Mathf.Max(0f, cam.transform.position.y - groundY);
            // g = 9.81 × 1.3（与 BuildRain 里的 gravityModifier 必须保持一致）
            const float G = 9.81f * 1.3f;
            // shape 已转 -90°，初速朝下（0~0.4 m/s）。带初速的解：h = v0·t + ½g·t²
            //   t = (-v0 + √(v0² + 2gh)) / g
            // 对 h=20m 而言 v0=0.4 只让 t 短 1.8%，但既然已经写了解析式就写全，
            // 免得以后谁把 startSpeed 调大（比如想做"暴雨"）又对不上。
            const float V0 = 0.2f;                    // 取区间中值，误差 < 1%
            float t = (-V0 + Mathf.Sqrt(V0 * V0 + 2f * G * drop)) / G;

            // 收 3% 让淡出终点落在贴地那一刻之前一点点，而不是刚好穿过去。
            // 下限 0.9s 防极端情况（相机贴地时落距只有 16m，t=1.58 仍远大于它）。
            t = Mathf.Max(0.9f, t * 0.97f);

            // 两端差 4%：同一批雨丝不会同时消失，看上去有厚薄层次。
            var main = rainPs.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(t * 0.96f, t);
        }

        /// <summary>
        /// 按实测落距维护雪花寿命。理由与 <see cref="UpdateRainLifetime"/> 相同，
        /// 但雪是"慢落"体系，参数完全不同，所以单列一个方法而不是塞进同一个。
        ///
        /// g = 9.81 × 0.035 = 0.343 m/s²（必须与 BuildSnow 的 gravityModifier 一致），
        /// 竖直初速为 0（shape 朝 +Z，初速是水平的）→ t = √(2h/g)：
        ///     落距 20m   → 10.79s
        ///     落距 17.6m → 10.13s
        ///     落距 28m   → 12.77s（相机拉高）
        /// 若沿用写死的 8~12s：8s 的雪花只落 11m，在相机上方 9m 的空中蒸发；
        /// 12s 的在落距 20m 时穿地 6.7m。两头都不对。
        ///
        /// 两端差 3%：让雪片错落消失，不要一整批同时没掉。
        /// </summary>
        private void UpdateSnowLifetime(float dt)
        {
            if (snowPs == null) return;

            snowLifeTimer -= dt;
            if (snowLifeTimer > 0f) return;
            snowLifeTimer = 0.5f;

            if (cam == null) return;

            float drop = EmitHeight + Mathf.Max(0f, cam.transform.position.y - groundY);
            const float G = 9.81f * 0.035f;       // 与 BuildSnow 的 gravityModifier 保持一致
            // 同雨：shape 已转 -90°，初速朝下（0.25~0.6 m/s，取中值 0.4）。
            const float V0 = 0.4f;
            float t = (-V0 + Mathf.Sqrt(V0 * V0 + 2f * G * drop)) / G;

            // 收 2%：让 alpha 归零发生在贴地前一点点，雪片"落进"地面而不是"消失在半空"。
            t = Mathf.Clamp(t * 0.98f, 4f, 30f);

            var main = snowPs.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(t * 0.97f, t);
        }

        // =====================================================================
        // 【P13】角色站到雪面上
        // =====================================================================

        private Transform _playerModel;      // TwinPlayer/Model，只动它的 localPosition
        private float playerLiftNow;         // 当前已施加的抬升量（米）

        /// <summary>
        /// 让角色**看起来**站在雪上，而不是陷进雪里。
        ///
        /// ── 抬多少：不是猜的，是算出来的 ──────────────────────────────────
        /// 雪盘的顶点高度由 DrapeSnow 决定：
        ///     vertexY = SampleGroundHeight(x,z) + SnowGroundLiftNear
        /// 而角色根节点贴在 groundY（脚底局部 y=0）。所以**脚下雪面比脚底高**
        ///     lift = SampleGroundHeight(px,pz) + SnowGroundLiftNear - root.position.y
        /// 直接照这个式子算，抬升量与现实雪面严格相等，角色正好站在雪面上。
        ///
        /// ── 为什么不能写死一个数 ─────────────────────────────────────────
        /// 上一版写死 7cm（按"12cm 真实雪厚 × 0.6 踩实"推的），实测角色仍埋到小腿 ——
        /// 因为雪面的 0.30m 里确实有相当部分是"盖住砖面凸起"的填充，
        /// 但**从角色视角看，那部分照样是白色的雪面**，脚底不抬到 0.30 就必然埋进去。
        /// "填充量 vs 真实雪厚"这个区分对解释雪盘高度有用，对抬人没用：
        /// 视觉上唯一有意义的量是"雪面顶在哪"。
        /// 何况铺装凸起在不同位置 0~0.25m 不等，写死任何常数都会在某些地方浮空/陷进去。
        ///
        /// ── 为什么不动物理 ────────────────────────────────────────────────
        /// ③ 只抬模型子物体（TwinPlayer/Model）的 localPosition.y → 物理完全不变。
        /// 另外两个方案都更糟：
        ///   ① 抬高 CharacterController.center → 上台阶时先撞头，改动已验收的移动手感 ✗
        ///   ② 抬高玩家根 transform.position → 每帧和 CharacterController.Move 抢位置，
        ///      重力把角色拉回来，形成持续抖动 ✗
        ///
        /// ── 为什么要平滑 ──────────────────────────────────────────────────
        /// snowAmount 渐变（积雪 12 秒、化雪 20 秒），且地面高度采样有 0.15s 节流。
        /// 直接写会让角色一格一格往上"长"。MoveTowards 以 0.8 m/s 追，
        /// 抬 0.3m 约 0.4 秒到位，肉眼看是"雪慢慢埋住脚背、又慢慢退下去"。
        /// </summary>
        private void UpdatePlayerOnSnow(float dt, float snowAmount)
        {
            if (_playerModel == null)
            {
                Transform root = PlayerRoot();
                if (root == null) return;
                // 模型子物体名字被 TwinCharacterSetup 固定成 "Model"
                _playerModel = root.Find("Model");
                if (_playerModel == null)
                {
                    // 兜底：取第一个带 SkinnedMeshRenderer 的子物体
                    var smr = root.GetComponentInChildren<SkinnedMeshRenderer>();
                    _playerModel = smr != null ? smr.transform : null;
                }
                if (_playerModel == null) return;

                // 记下"没雪时的原始 localPosition"，所有抬升都是相对它算的。
                // 必须记原始值 —— 否则每帧在上一帧的基础上再加，角色会一路飘走。
                playerBaseLocalPos = _playerModel.localPosition;
            }

            Transform playerRoot = PlayerRoot();
            if (playerRoot == null) return;

            // 地面高度采样节流：与 UpdateGround 的 0.15s 对齐。
            // 场景里 6000+ 碰撞体，每帧为抬个脚打射线是白给。
            playerSnowProbeTimer -= dt;
            if (playerSnowProbeTimer <= 0f)
            {
                playerSnowProbeTimer = 0.15f;
                Vector3 pp = playerRoot.position;
                // 脚下雪面顶 —— 必须与 DrapeSnow 用**同一套采样**，否则人会浮空/陷脚。
                //
                // DrapeSnow 的顶点高度是：
                //     SampleGroundHeight(x,z, outdoorGroundY+1.6) + SnowGroundLiftNear
                // 而 SampleGroundHeight 的 refY 也是 outdoorGroundY。所以这里照抄这个式子，
                // 而不是取"角色脚下最高的朝上面" —— 那站在花坛/台阶上会比雪盘基准
                // 高出 0.4~1.0m，角色就会悬在半空。雪盘是世界平铺的，人也得按同一基准。
                playerSnowTopY = SampleGroundHeight(pp.x, pp.z, outdoorGroundY + 1.6f)
                                 + SnowGroundLiftNear;
                playerSnowBaseY = pp.y;
            }

            // 抬升量 = 雪面顶 - 脚底，再加 2cm 余量。
            // 为什么要有余量：雪盘顶点是 10 段细分后线性插值的，这里按单点射线算，
            // 两套采样在斜坡上差几厘米；不补会露出半只脚趾被雪面切掉。
            // 为什么只抬正值：雪面低于脚底时不能往下拽人，否则角色陷进地形。
            float want = Mathf.Max(0f, playerSnowTopY + PlayerSnowClearance - playerSnowBaseY);

            // ⚠️ 为什么这里**不能**直接乘 snowAmount ─────────────────────────
            // DrapeSnow 里顶点高度是 `SampleGroundHeight + SnowGroundLiftNear`，
            // 只要 SnowGround 一激活（snowAmount > 0.01），雪面就已经抬满 0.30m 了，
            // 与 snowAmount 是 0.05 还是 1.0 无关。角色若按 snowAmount 打折抬，
            // 雪天刚起时（snowAmount 0.3）就只抬 9cm，人立刻埋进雪面 21cm。
            //
            // 但也不能完全不衰减：雪面刚出现时材质 alpha 只有几个百分点，
            // 几乎看不见白，此时把人抬到 0.30 会"凭空浮起"。所以用一条很快上线的
            // 曲线 —— snowAmount 到 0.3 就已经抬满，对应雪面已经明显可见的时刻。
            want *= Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0f, 0.3f, Mathf.Clamp01(snowAmount)));

            playerLiftNow = Mathf.MoveTowards(playerLiftNow, want, dt * 0.8f);

            Vector3 p = playerBaseLocalPos;
            p.y += playerLiftNow;
            _playerModel.localPosition = p;
        }

        private Vector3 playerBaseLocalPos;
        private float playerSnowProbeTimer;
        private float playerSnowTopY;
        private float playerSnowBaseY;

        /// <summary>
        /// 脚底比雪面再高出的余量（米）。
        /// 雪盘顶点按 9m 网格线性插值，脚底按精确射线高度算，斜坡上两者差几厘米；
        /// 不补这一点，坡地会露出半只脚趾被雪面切掉，看着像"脚插雪里"。
        /// 2cm 是"能盖住插值误差"和"看不出来浮空"之间的平衡点。
        /// </summary>
        private const float PlayerSnowClearance = 0.02f;

        // =====================================================================
        // 【P13】雪地脚印
        // =====================================================================

        // 参数选择说明（这几个数都不是随手拍的）：
        //   · 步幅：大学生常速步行单步约 0.70~0.75m，鞋长约 0.26m。
        //     若按"一步一个印"发射，雪地上会看到间隔 0.72m 的一串孤立圆点，
        //     因为真实的雪地脚印是左右脚交替、前后间距被鞋长吃掉一部分的。
        //     所以按**半步 0.36m** 发射、左右交替 → 视觉上就是连续的足迹带。
        //   · 横向偏移 0.11m：成人两脚中心间距约 0.20~0.24m，半径就是这个值。
        //   · 半径 0.16m：略大于鞋长的一半（0.13m），留出雪被挤开的边缘。
        //   · 寿命 240s：雪一直在下，4 分钟足够让 32 个槽位（≈11.5m 足迹带）
        //     里最老的印子淡到看不见，不会出现"走了半个操场，几十步前那脚还在"。
        private const int PrintMax = 32;
        private const float PrintStride = 0.36f;
        private const float PrintSideOffset = 0.11f;
        private const float PrintRadiusM = 0.16f;
        private const float PrintLifeSec = 240f;

        private readonly Vector4[] prints = new Vector4[PrintMax];   // xy=世界xz, z=落印时刻, w=强度
        private int printWrite;          // 环形缓冲：下一个写入槽
        private int printUsed;           // 已写入槽数（≤ PrintMax）
        private Vector3 printLastPos;    // 上一次落印的世界位置（脚底中心）
        private bool printHasLast;       // false = 刚起步/刚落地，本帧只记录不落印
        private int printFootSide = 1;   // +1 / -1 交替，模拟左右脚
        private bool printCleared = true; // 材质里的脚印是否已清空
        private Material printMatCached;   // 上一次写入脚印的材质；换实例说明鞋印已丢

        private CharacterController _playerCc;
        private bool _playerCcSearched;

        /// <summary>
        /// 在雪面上留下脚印。
        ///
        /// ── 为什么用世界坐标 + 数组，而不是在雪盘上画贴图 ──────────────────
        /// 雪盘是**跟着相机跑**的（每帧 position = 相机 xz），顶点还会逐帧按地形重铺。
        /// 如果在盘的本地 UV 上画，盘一移动脚印就跟着跑 —— 这正是水坑那轮踩过的坑。
        /// 所以脚印必须锚在**世界 xz** 上：shader 里拿 `i.worldPos.xz` 去比对，
        /// 盘怎么动都不影响印子的位置。
        ///
        /// ── 为什么写进材质而不是生成 GameObject ────────────────────────────
        /// 每个脚印一个面片会有 32 个 DrawCall，且要和雪面做 Z 排序（雪面是
        /// Transparent 队列 2996，面片插在中间必然穿插）。写进 shader 常量数组
        /// 只需要一次 SetVectorArray，零额外 DrawCall，也不会和雪面抢深度。
        /// </summary>
        private void UpdateFootprints(float snowAmount)
        {
            if (snowGroundMat == null) return;

            // 材质被重建过（雪盘被销毁重建 / 切场景后重新构建）→ 旧材质里的脚印全丢，
            // 但 C# 侧的 printUsed 还记着 32 个、prints[] 里还存着旧坐标。
            // 不重置就会"自检说有 32 个脚印、画面上一个都没有"，而且下一秒
            // EmitPrint 会把那 32 个旧坐标原样推回材质 —— 幽灵足迹复活。
            if (!ReferenceEquals(printMatCached, snowGroundMat))
            {
                printMatCached = snowGroundMat;
                ResetPrints();
            }

            // 脚印深度跟着积雪量走：刚下雪时地上还没雪，不该有脚印；
            // 积到 75% 才到最深。中间用 SmoothStep 过渡，避免"忽然出现"。
            //
            // 不再额外乘 indoorFade：snowAmount 在 Update 里已经 = accum × altFade × indoorFade，
            // 再乘一次就是平方 —— 室内外过渡时脚印会衰减得比雪面快一倍，出现
            // "雪都快没了脚印还在"的错位。
            float depth = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.15f, 0.75f, snowAmount));

            // 雪化了 / 切回晴天 → 清空历史脚印。
            // 必须清：否则"雪→晴→雪"之后，上一场雪留在缓冲里的 32 个点会
            // 凭空复活在半路上（那地方的雪早化没了），看着像幽灵足迹。
            if (depth <= 0.01f)
            {
                if (!printCleared) ResetPrints();
                return;
            }

            if (printCleared)
            {
                printCleared = false;
                // 半径/寿命是常量，只在重新开雪时写一次
                snowGroundMat.SetFloat(IdPrintRadius, PrintRadiusM);
                snowGroundMat.SetFloat(IdPrintLife, PrintLifeSec);
            }
            snowGroundMat.SetFloat(IdPrintDepth, depth * 0.9f);

            Transform root = PlayerRoot();
            if (root == null) return;

            Vector3 p = root.position;

            // 只有在真正踩着地面时才落印：跳跃中、上台阶的瞬间都不该留印。
            // CharacterController.isGrounded 在 Move() 之后当帧有效，Update 里读是准的。
            if (!IsPlayerGrounded(root))
            {
                printHasLast = false;   // 落地后从新位置重新起算，避免空中位移被连成一条印
                return;
            }

            if (!printHasLast)
            {
                printLastPos = p;
                printHasLast = true;
                return;
            }

            Vector3 d = p - printLastPos;
            d.y = 0f;
            float dist = d.magnitude;
            if (dist < PrintStride) return;

            Vector3 dir = d / dist;

            // 一帧可能跨过好几个步幅（跑步、被传送、卡顿后单帧位移很大）。
            // 按步长补齐，否则跑步时脚印会变成"一帧一个大跳"，间距忽长忽短。
            // guard 上限 8：防止瞬移 100m 时在一条直线上刷满整个缓冲。
            int guard = 0;
            while (dist >= PrintStride && guard++ < 8)
            {
                printLastPos += dir * PrintStride;
                dist -= PrintStride;
                EmitPrint(printLastPos, dir);
            }

            // 剩下的零头累进起点，下一帧接着算（不丢距离，慢走时间距才均匀）
            printLastPos += dir * dist;
        }

        /// <summary>
        /// 室外**地面**（世界 y）。用于给雪盘定位与铺地形提供基准高度。
        ///
        /// ── 为什么不能用 groundY ─────────────────────────────────────────
        /// groundY = 相机正下方第一个面。它有三个不该有的行为：
        ///   ① 二楼室内 → 取到 2F 楼板（雪面被抬到二楼高度）；
        ///   ② 二楼**外廊/阳台**（indoorFade 仍是 1，因为头顶是天空）→ 取到
        ///      阳台地板，同样把整片雪抬到 4m 高的空中；
        ///   ③ 站在花坛/台阶上 → 取到花坛顶，雪面基准每步都跳一下。
        /// 雪盘代表的是"整个校园地面的积雪"，基准必须是**地形基底**，与站在哪无关。
        ///
        /// ── 实现：优先 Terrain，退化到长射线 ─────────────────────────────
        /// Terrain 是校园的连续基底，SampleHeight 不受建筑/花坛干扰，
        /// 也完全不受相机在楼里还是楼外影响。没有 Terrain 时才用一条长射线，
        /// 并显式跳过玩家自己的胶囊。
        /// </summary>
        private float ProbeOutdoorGround(Vector3 at)
        {
            Terrain t = Terrain.activeTerrain;
            if (t != null && t.terrainData != null)
            {
                // SampleHeight 返回的是**地形相对高度**（相对地形原点/最低点），
                // 必须加地形自身的位置才是世界 y。这是 Terrain API 最常见的错用点。
                Vector3 tp = t.GetPosition();
                return tp.y + t.SampleHeight(new Vector3(at.x, 0f, at.z));
            }

            // 退化路径：从高处往下打一条长射线，只认朝上的面
            Vector3 origin = new Vector3(at.x, at.y + 60f, at.z);
            int n = Physics.RaycastNonAlloc(new Ray(origin, Vector3.down), groundHits, 200f);
            float best = float.NegativeInfinity;
            for (int i = 0; i < n; i++)
            {
                RaycastHit h = groundHits[i];
                if (h.collider == null || h.collider.isTrigger) continue;
                if (IsPlayerCollider(h.collider)) continue;
                if (h.normal.y < 0.6f) continue;
                if (h.point.y > best) best = h.point.y;
            }
            return float.IsNegativeInfinity(best) ? at.y : best;
        }

        /// <summary>清空脚印历史（含材质里的 _PrintCount）。</summary>
        private void ResetPrints()
        {
            printCleared = true;
            printWrite = 0;
            printUsed = 0;
            printHasLast = false;
            printFootSide = 1;
            if (printMatCached != null) printMatCached.SetFloat(IdPrintCount, 0f);
        }

        /// <summary>在指定世界位置落一个脚印，并把整个缓冲推给材质。</summary>
        private void EmitPrint(Vector3 feetPos, Vector3 moveDir)
        {
            // 左右脚交替：垂直于前进方向偏移半个脚间距。
            // 这一步是"看起来像脚印"而不是"一串圆点"的关键 ——
            // 单一中线上连续排圆点，眼睛会读成"滴了一条线"，不会读成"人走过"。
            Vector3 side = new Vector3(-moveDir.z, 0f, moveDir.x) * (PrintSideOffset * printFootSide);
            printFootSide = -printFootSide;

            Vector3 wp = feetPos + side;
            // 时间戳必须和 shader 里的 _Time.y 同源。
            // _Time.y = 本关加载至今的秒数（= Time.timeSinceLevelLoad），
            // 而不是 Time.time（= 游戏启动至今）—— 重载场景后两者会差出整整一段，
            // 用 Time.time 写、_Time.y 读，算出的 age 可能是负数，
            // 表现为"脚印刚踩上去就已经淡没了"或"永远不淡"。
            prints[printWrite] = new Vector4(wp.x, wp.z, Time.timeSinceLevelLoad, 1f);

            printWrite = (printWrite + 1) % PrintMax;
            if (printUsed < PrintMax) printUsed++;

            snowGroundMat.SetVectorArray(IdPrints, prints);
            snowGroundMat.SetFloat(IdPrintCount, printUsed);
        }

        /// <summary>
        /// 角色是否踩在地面上。
        /// 优先用 CharacterController.isGrounded；没有控制器（第一人称模式等）时
        /// 保守返回 true —— 宁可多留几个脚印，也不要整片雪地一个印都没有。
        /// </summary>
        private bool IsPlayerGrounded(Transform root)
        {
            if (!_playerCcSearched)
            {
                _playerCcSearched = true;
                _playerCc = root.GetComponent<CharacterController>();
            }
            return _playerCc == null || _playerCc.isGrounded;
        }

        private string BuildSnowAudit()
        {
            var sb = new System.Text.StringBuilder(900);
            sb.AppendLine("    snowAccum  = " + snowAccum.ToString("F3") + "   （0 = 粒子在飞但没有累积量）");
            sb.AppendLine("    altFade    = " + altFade.ToString("F3") + "   （0 = 相机判定为高空，雨雪全关）");
            sb.AppendLine("    indoor     = " + indoor + "   / indoorFade = " + indoorFade.ToString("F3")
                          + "   （只影响粒子和积水；**不再影响地面雪**）");
            sb.AppendLine("    头顶射线   = " + (ceilDist < 0f
                              ? "6m 内什么都没打到（室外）"
                              : ceilName + "  " + ceilDist.ToString("F2") + "m  法线Y=" + ceilNormalY.ToString("F2"))
                          + "   （判室内要求 法线Y ≤ " + IndoorCeilNormalY.ToString("F1") + "）");
            sb.AppendLine("    snowAmount = " + groundSnowAmount.ToString("F3") + "   （地面积雪 = accum × altFade，**不含 indoorFade**）");
            sb.AppendLine("    indoorFade 不影响地面 → 进楼时地面积雪保持不变，只有粒子和积水会消失");

            sb.AppendLine("  二、雪盘实体（决定地上有没有白）");
            sb.AppendLine("    snowGround   = " + (snowGround != null ? snowGround.name : "null（没建出来）"));
            sb.AppendLine("    activeInHier = " + SnowGroundVisible + (SnowGroundVisible ? "" : "   ← 被 SetActive(false) 了"));
            if (snowGround != null)
            {
                sb.AppendLine("    worldPos     = " + snowGround.position.ToString("F2"));
                sb.AppendLine("    anchor       = (" + snowAnchor.x.ToString("F2") + ", " + snowAnchor.z.ToString("F2") + ")"
                              + (indoorFade > 0.5f ? "   跟随相机" : "   **已冻结**（室内，雪盘留在楼外）"));
                sb.AppendLine("    室外地面 Y   = " + outdoorGroundY.ToString("F2")
                              + "   相机脚下 groundY = " + groundY.ToString("F2")
                              + (Mathf.Abs(outdoorGroundY - groundY) > 0.5f ? "   ← 差很多（在多楼层时正常）" : ""));
                sb.AppendLine("    isVisible    = " + SnowGroundRendered + (SnowGroundRendered ? "" : "   ← 相机看不见！查包围盒/位置"));
                sb.AppendLine("    mesh.bounds  = " + SnowGroundBounds);
            }

            sb.AppendLine("  三、材质（值写进去了没有）");
            sb.AppendLine("    hasMat       = " + SnowGroundHasMat);
            sb.AppendLine("    shaderOk     = " + SnowGroundShaderOk + "   （必须是 CampusUI/SnowGround）");
            sb.AppendLine("    _SnowAmount  = " + SnowAmountInMat.ToString("F3") + "   （材质里的真实值）");

            sb.AppendLine("  四、粒子（落地是否干净）");
            sb.AppendLine("    camY-groundY = " + (cam != null ? (cam.transform.position.y - groundY).ToString("F2") : "?")
                          + " m   （落距 = EmitHeight 16 + 这个值）");
            if (rainPs != null)
            {
                var rl = rainPs.main.startLifetime;
                float rt = rl.constantMax;
                sb.AppendLine("    rain.life    = " + rt.ToString("F3") + " s"
                              + "   （落距 20m 时应 ≈1.74；远小于它 = 雨丝半空断掉）");
            }
            if (snowPs != null)
            {
                var sl = snowPs.main.startLifetime;
                sb.AppendLine("    snow.life    = " + sl.constantMax.ToString("F2") + " s"
                              + "   （落距 20m 时应 ≈10.1；8 秒档会让一半雪花在空中蒸发）");
            }

            // 五、顶点采样分布 —— 判断"雪面到底摊平了没有"的决定性数据
            //
            // 为什么看这个而不是只看 mesh.bounds：
            //   bounds 只给最大最小值，一个顶点浮到 +2.4 就能把 y 尺寸拉爆，
            //   但看不出"是全部顶点都浮了"还是"只有一小撮浮了"。
            //   直方图直接告诉你有多少个顶点落在各个高度带里：
            //     · 健康：>95% 的顶点在 [-1, +1]（贴着地面）
            //     · 有病：有一坨在 [+2, +12]，说明还在采树冠/雨棚/屋顶
            AppendVertHistogram(sb);

            // 六、贴图与 UV —— 判断"雪面画出来了吗"
            AppendTexAudit(sb);

            // 七、角色与脚印 —— 判断"站上去了没有 / 留下印子了没有"
            AppendPlayerSnowAudit(sb);
            return sb.ToString();
        }

        /// <summary>
        /// 角色站立高度与脚印状态的现场数据。
        ///
        /// 这两组数放在一起看才有意义：角色"陷进去"和"脚印不出现"经常是同一个根因
        /// （雪面高度算错 → 人埋了，且脚印落在雪面下方被深度测试挡掉）。
        /// </summary>
        private void AppendPlayerSnowAudit(System.Text.StringBuilder sb)
        {
            sb.AppendLine("  七、角色站立与脚印");
            sb.AppendLine("    雪面顶 - 脚底 = " + (playerSnowTopY - playerSnowBaseY).ToString("F3") + " m"
                          + "   （= SampleGroundHeight + " + SnowGroundLiftNear.ToString("F2") + " - rootY）");
            sb.AppendLine("    已抬升       = " + playerLiftNow.ToString("F3") + " m"
                          + (Mathf.Abs(playerLiftNow - Mathf.Max(0f, playerSnowTopY - playerSnowBaseY)) > 0.03f
                              ? "   ← 还在追（平滑中）" : "   ← 到位"));
            sb.AppendLine("    model        = " + (_playerModel != null ? _playerModel.name : "null")
                          + "   localY = " + (_playerModel != null ? _playerModel.localPosition.y.ToString("F3") : "?")
                          + "   基准Y = " + playerBaseLocalPos.y.ToString("F3"));
            sb.AppendLine("    脚印         = " + printUsed + "/" + PrintMax + " 个"
                          + (_playerCc != null ? ("   grounded = " + _playerCc.isGrounded) : "   （无 CharacterController，按接地处理）"));
            sb.AppendLine("    _PrintDepth  = " + (snowGroundMat != null ? snowGroundMat.GetFloat(IdPrintDepth).ToString("F3") : "?")
                          + "   _PrintCount = " + (snowGroundMat != null ? snowGroundMat.GetFloat(IdPrintCount).ToString("F0") : "?")
                          + "   （Count=0 而脚印>0 = 没推给材质）");
            if (printUsed > 0)
            {
                int last = (printWrite - 1 + PrintMax) % PrintMax;
                Vector4 v = prints[last];
                sb.AppendLine("    最后一个脚印 = (" + v.x.ToString("F2") + ", " + v.z.ToString("F2") + ") 世界xz"
                              + "   距今 " + (Time.timeSinceLevelLoad - v.z).ToString("F1") + "s");
            }
        }

        /// <summary>
        /// 把 _SnowTex 的关键像素直接读回来。
        ///
        /// 为什么必须做这一步：顶点高度对了、材质值对了、包围盒也对了，
        /// 但画面上什么都没出现，唯一的出口就在 frag 的前几行 ——
        ///     cov = tex2D(_SnowTex, uv).a;  if (cov <= 0.003) discard;
        /// 如果这张贴图的 alpha 中心不是 1、或者 UV 映射把整片都采到了边缘的 0，
        /// 那么无论雪量多大，整片都会被 discard，砖面纹理一丝不变。
        /// 读像素是唯一能区分"贴图是空的"和"UV 采错了"的办法。
        /// </summary>
        private void AppendTexAudit(System.Text.StringBuilder sb)
        {
            sb.AppendLine("  六、贴图与 UV（决定雪面画得出来吗）");
            if (snowGroundMat == null)
            {
                sb.AppendLine("    snowGroundMat = null");
                return;
            }

            var tex = snowGroundMat.GetTexture(IdSnowTex) as Texture2D;
            if (tex == null)
            {
                sb.AppendLine("    _SnowTex = null   ← 贴图没绑上，frag 里 cov 恒为 0，整片 discard");
                return;
            }

            sb.AppendLine("    _SnowTex     = " + tex.name + "  " + tex.width + "×" + tex.height
                          + "  wrap=" + tex.wrapMode + "  fmt=" + tex.format);

            // 只在贴图可读时读像素（我们是在运行时 new 出来的，天然可读）
            try
            {
                int cx = tex.width / 2, cy = tex.height / 2;
                Color c0 = tex.GetPixel(cx, cy);                       // 中心：应 alpha=1
                Color c1 = tex.GetPixel(2, 2);                         // 左下角：应 alpha≈0（径向淡出）
                Color c2 = tex.GetPixel(cx, tex.height - 3);           // 上缘：应 alpha≈0
                sb.AppendLine("    alpha 中心   = " + c0.a.ToString("F3") + "   （必须接近 1，否则整片被 discard）");
                sb.AppendLine("    alpha 角落   = " + c1.a.ToString("F3") + "   （应接近 0，是径向淡出）");
                sb.AppendLine("    alpha 上缘   = " + c2.a.ToString("F3"));
                sb.AppendLine("    rgb  中心    = " + c0.r.ToString("F2") + "," + c0.g.ToString("F2") + "," + c0.b.ToString("F2")
                              + "   （噪声颗粒，会被当微法线用）");
            }
            catch (System.Exception e)
            {
                sb.AppendLine("    读像素失败：" + e.GetType().Name + "（贴图不可读）");
            }

            // _SnowTex_ST —— TRANSFORM_TEX 用它做缩放偏移。若被写成 0 缩放，UV 全塌到一点。
            Vector4 st = snowGroundMat.GetVector("_SnowTex_ST");
            sb.AppendLine("    _SnowTex_ST  = (" + st.x.ToString("F2") + "," + st.y.ToString("F2")
                          + "," + st.z.ToString("F2") + "," + st.w.ToString("F2") + ")   （应为 1,1,0,0）");
            sb.AppendLine("    _EdgeFade    = " + snowGroundMat.GetFloat(IdEdgeFade).ToString("F2")
                          + "   （C# 设 1.0 → shader 里走 edge=1 分支）");
            sb.AppendLine("    renderQueue  = " + snowGroundMat.renderQueue + "   （Water 2992 → Snow 2996 应在后面）");
        }

        /// <summary>
        /// 把当前雪盘顶点的 y 值分桶统计，写进自检日志。
        /// 分桶以"相对 groundY 的高度"为基准，单位米。
        /// </summary>
        private void AppendVertHistogram(System.Text.StringBuilder sb)
        {
            sb.AppendLine("  五、雪盘顶点相对脚下地面的分布（决定雪面摊平没有）");
            if (snowWorkVerts == null || snowWorkVerts.Length == 0)
            {
                sb.AppendLine("    snowWorkVerts = null（网格还没建）");
                return;
            }

            // 分桶边界（相对 groundY，米），与 clamp 的 [-6, +1] 对齐
            float[] edges = { -999f, -6f, -1f, 1.2f, 3f, 999f };
            string[] names =
            {
                "< -6 被过度下压", "[-6,-1) 下沉/坡谷", "[-1,+1.2] 正常贴地",
                "(+1.2,+3] 可疑（树冠？）", "> +3 严重异常"
            };

            int[] cnt = new int[names.Length];
            int total = snowWorkVerts.Length;

            // 顶点的 y 是**网格局部坐标**，平面自身世界 y = baseY = groundY + 0.02，
            // 所以该顶点的世界高度相对脚下地面就是： rel = vert.y + 0.02
            for (int i = 0; i < total; i++)
            {
                float rel = snowWorkVerts[i].y + 0.02f;
                int k = names.Length - 1;
                for (int b = 0; b < edges.Length - 1; b++)
                {
                    if (rel >= edges[b] && rel < edges[b + 1]) { k = b; break; }
                }
                cnt[k]++;
            }

            for (int k = 0; k < names.Length; k++)
            {
                float pct = total > 0 ? 100f * cnt[k] / total : 0f;
                sb.AppendLine("    " + names[k].PadRight(22) + "=" + cnt[k].ToString().PadLeft(5)
                              + "  (" + pct.ToString("F1") + "%)");
            }
            sb.AppendLine("    total = " + total + " 顶点"
                          + "   ← 未入任何桶 = " + (total - SumInts(cnt)));

            // 最直观的一条：雪面最高点 vs 相机高度。
            // 如果雪面顶比相机还高，说明整片雪被顶到头顶去了 ——
            // 相机在雪面**下方**，自然一点白都看不见，砖面纹理一丝不变。
            float maxRel = float.NegativeInfinity;
            for (int i = 0; i < total; i++)
                if (snowWorkVerts[i].y + 0.02f > maxRel) maxRel = snowWorkVerts[i].y + 0.02f;

            float camRel = cam != null ? cam.transform.position.y - groundY : float.NaN;
            sb.AppendLine("    雪面最高点   = groundY + " + maxRel.ToString("F2") + " m");
            sb.AppendLine("    相机高度     = groundY + " + camRel.ToString("F2") + " m");
            sb.AppendLine("    " + (maxRel > camRel
                ? "★★★ 雪面高于相机 → 整片雪被顶到头顶，脚下当然一点白都没有！"
                : "OK：雪面低于相机，应该在画面里看得到"));
        }

        private static int SumInts(int[] a)
        {
            int s = 0;
            for (int i = 0; i < a.Length; i++) s += a[i];
            return s;
        }

        /// <summary>
        /// 设置穹顶材质的不透明度，同时兼容 SkyDome 着色器与 Sprites/Default 兜底。
        /// SkyDome 把颜色(_Color)和不透明度(_Opacity)分开，避免 alpha 同时参与混合与着色。
        /// </summary>
        // 积雪着色器（CampusUI/SnowGround）的属性 ID
        private static readonly int IdSnowTex = Shader.PropertyToID("_SnowTex");
        private static readonly int IdSnowDetail = Shader.PropertyToID("_DetailTex");
        private static readonly int IdSnowColor = Shader.PropertyToID("_SnowColor");
        private static readonly int IdSkyColor = Shader.PropertyToID("_SkyColor");
        private static readonly int IdSnowAmount = Shader.PropertyToID("_SnowAmount");
        private static readonly int IdEdgeFade = Shader.PropertyToID("_EdgeFade");
        private static readonly int IdGlobalSnow = Shader.PropertyToID("_GlobalSnow");   // 全局雪量；后续本地化 Lit/TerrainLit 积雪时直接读取
        private static readonly int IdPrints = Shader.PropertyToID("_Prints");
        private static readonly int IdPrintCount = Shader.PropertyToID("_PrintCount");
        private static readonly int IdPrintRadius = Shader.PropertyToID("_PrintRadius");
        private static readonly int IdPrintDepth = Shader.PropertyToID("_PrintDepth");
        private static readonly int IdPrintLife = Shader.PropertyToID("_PrintLife");

        private static void SetDome(Material m, Color rgb, float opacity)
        {
            SetDome(m, rgb, opacity, 0f);
        }

        /// <summary>
        /// 同上，额外写阴天帷幔（只在 CloudDome 上有 _Overcast；星空穹顶走三参重载）。
        /// </summary>
        private static void SetDome(Material m, Color rgb, float opacity, float overcast)
        {
            if (m == null) return;
            opacity = Mathf.Clamp01(opacity);
            overcast = Mathf.Clamp01(overcast);

            if (m.HasProperty("_Opacity"))
            {
                m.SetColor("_Color", new Color(rgb.r, rgb.g, rgb.b, 1f));
                m.SetFloat("_Opacity", opacity);
                // 只有 CloudDome 有这个属性；SkyDome / Sprites-Default 都没有，靠 HasProperty 跳过
                if (m.HasProperty("_Overcast")) m.SetFloat("_Overcast", overcast);
            }
            else
            {
                // 兜底路径（Sprites/Default）：没有 _Overcast 可言，只能把两者合成的
                // 等效不透明度写进 alpha。合成式与 shader 里一致：取 max 而不是相加。
                m.color = new Color(rgb.r, rgb.g, rgb.b, Mathf.Max(opacity, overcast));
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
                //
                // ══════════════════════════════════════════════════════════════════
                // 【P13 修的真 bug：站在室外树下，被判成"室内"】
                //
                // 原判据只有一句：头顶 40m 内有任何非玩家碰撞体 → indoor = true。
                // 校园里到处是树，树冠的碰撞体就在头顶 4~6m，这条射线**必然命中**
                // → 只要是走在林荫道上，indoor 恒为 true
                // → snowAmount = accum × altFade × indoorFade = 0
                // → 雪盘被 SetActive(false)，室内还留着 25% 的雨雪粒子。
                // 自检里的 "室内= True、snowAmount = 0.000、activeInHier = False"
                // 就是这么来的 —— 而截图明明是露天路面。
                //
                // 真正的"室内"要有两个特征，缺一不可：
                //   ① **离得近**：天花板就在头顶 1~6m。树冠通常在 4~6m 甚至更高，
                //      而建筑层高 3.3~4.5m，取 6m 作上限能覆盖绝大多数室内空间。
                //      但这还不够 —— 树下正好也在这个范围。
                //   ② **法线朝下**：天花板的下表面法线指向 -Y（n.y <= -0.5）。
                //      树冠是**外表面**：从下方打射线，命中的是树冠球体的下缘，
                //      其外法线指向**下外侧**（n.y 为负但绝对值小，多数在 -0.1~-0.6），
                //      而真正平整的天花板法线是 -1。
                //      取 n.y <= -0.7 可以稳定区分两者。
                //
                // 代价：头顶有大树冠时会偶尔误判为"室外"（漏判室内）。
                // 这个方向的错误是**可以接受的** —— 室内漏判只是看到窗外在下雨，
                // 而室外误判室内是"整片地面没有积雪"，后者是硬伤。
                // ══════════════════════════════════════════════════════════════
                RaycastHit up;
                indoor = false;

                // ══════════════════════════════════════════════════════════
                // 【P13】室内判定：向上射线必须能命中**背面**
                //
                // Physics.queriesHitBackfaces 默认是 false —— 射线不会命中
                // 三角面的背面。而建筑的天花板通常是**单层**mesh：从下方往上看，
                // 看到的是它的背面。于是这条向上射线在二楼走廊里直接穿过去了，
                // 打了 6m 什么都没命中 → indoor = false → 雪花照常在室内飘。
                //
                // 这正是"室内偶尔还有粒子飘落"的根因：不是判定阈值不对，
                // 是射线根本没碰到天花板。
                //
                // 修法：只在这一次查询里临时打开背面命中，查完立刻恢复原值，
                // 不影响项目里其他任何射线（地形采样、拾取都依赖默认行为）。
                // ══════════════════════════════════════════════════════════
                bool prevBackfaces = Physics.queriesHitBackfaces;
                Physics.queriesHitBackfaces = true;
                bool hitUp = Physics.Raycast(new Ray(camPos, Vector3.up), out up, IndoorCeilHeight);
                Physics.queriesHitBackfaces = prevBackfaces;

                if (hitUp)
                {
                    ceilDist = up.distance;
                    ceilNormalY = up.normal.y;
                    ceilName = up.collider != null ? up.collider.name : "?";
                }
                else
                {
                    ceilDist = -1f;
                    ceilNormalY = 0f;
                    ceilName = "";
                }

                if (hitUp
                    && up.distance > 0.3f                       // 排除"射线从自己胶囊里起步"
                    && !IsPlayerCollider(up.collider)           // 跳过玩家自己
                    && up.normal.y <= IndoorCeilNormalY)        // 必须是被"下表面"挡住
                {
                    indoor = true;
                }
            }

            // ══════════════════════════════════════════════════════════════════
            // 【P13】雪盘锚点更新：锚在**角色**身上，且只在室外跟随
            //
            // ── 为什么锚角色而不是相机（这是"雪地随视角移动"的根因）──────
            // 原实现用 camPos。第三人称相机绕角色做 orbital 旋转，镜头一摆
            // camPos 就在半径 4~5m 的圆弧上移动。雪盘钉在 camPos 上，
            // 于是"只转头不动人"也会让整片雪地横着滑移 ——
            // 用户看到的就是"雪地随视角变化移动"。
            // 角色的 XZ 在转头时**完全不变**，锚它就彻底没有这个问题。
            //
            // ── 为什么进楼要冻结 ─────────────────────────────────────────
            // 雪盘覆盖 90m，中心若跟着进屋，一半雪就铺到室内地板上了。
            // 冻结在进楼前的位置，雪正好留在楼外：从窗户望出去仍然一片白，
            // 室内地板干干净净，而室内本来就不该有雪。
            //
            // ── 为什么用 indoorFade 而不是 indoor 布尔量 ─────────────────
            // 穿过门洞那一两帧 indoor 会反复抖，布尔量会让雪盘瞬移两次。
            // indoorFade 是 0.33 秒的平滑过渡，天然带迟滞。
            // ══════════════════════════════════════════════════════════════════
            Vector3 anchorSrc = camPos;
            Transform pr = PlayerRoot();
            if (pr != null) anchorSrc = pr.position;   // 有角色就锚角色，转头不再移动雪盘

            if (indoorFade > 0.5f)
            {
                // 室外：重新锚定。
                snowAnchor = new Vector3(anchorSrc.x, 0f, anchorSrc.z);
                snowAnchorValid = true;
                outdoorGroundY = ProbeOutdoorGround(anchorSrc);
                outdoorGroundValid = true;
            }

            if (!snowAnchorValid)
            {
                // 一开局就在室内（贴着窗户出生等极端情况）→ 用相机脚下的高度兜底，
                // 总比让雪盘停在世界原点好。
                snowAnchor = new Vector3(camPos.x, 0f, camPos.z);
                snowAnchorValid = true;
            }
            if (!outdoorGroundValid) outdoorGroundY = groundY;

            if (splashRoot != null)
                splashRoot.position = new Vector3(camPos.x, groundY + 0.03f, camPos.z);
            // 雪盘用**世界锚点 + 室外地平面**，不用相机位置与 groundY
            if (snowGround != null)
                snowGround.position = new Vector3(snowAnchor.x, outdoorGroundY + 0.02f, snowAnchor.z);
            if (snowGroundFar != null)
                snowGroundFar.position = new Vector3(snowAnchor.x, outdoorGroundY + 0.02f, snowAnchor.z);
            if (wet != null) wet.Follow(camPos, groundY);

            // 积雪地形跟随：让雪盖住抬高的花坛/台阶，而不是浮在半空露出硬边。
            // 只在相机挪动超过阈值时才重打一遍射线，省开销。
            //
            // ⚠️ 注意传的是 snowAnchor 而不是 camPos：铺地形的结果只取决于
            // 「雪盘中心 + 室外地平面」这两个世界量。室内相机绕角色摆动时 camPos
            // 变而锚点不变，若用 camPos 做重算判据，就会每秒重铺 6 次同样的结果，
            // 白白 700+ 条射线，还让雪面在重铺瞬间闪一下。
            if (snowGround != null) DrapeSnow(snowAnchor);
            if (snowGroundFar != null) DrapeSnowFar(snowAnchor);
        }

        /// <summary>
        /// 积雪地形跟随：把克隆出来的平面网格逐顶点向下射线，按脚下真实地面高度抬高/压低顶点，
        /// 让雪面"贴"住地形起伏（花坛、台阶、缓坡），而不是一整块平板浮在 groundY 上。
        ///
        /// 性能：平面 10 段细分 = 121 个顶点，每顶点一条短距离向下射线。只在相机移动超过
        /// 0.25m（或刚开雪）时重算，静止时直接跳过；射线距离仅 12m、且忽略玩家胶囊与触发器。
        /// </summary>
        private void DrapeSnow(Vector3 anchorXZ)
        {
            if (snowMesh == null || snowBaseVerts == null || snowWorkVerts == null) return;
            // 重算条件：相机移动超过 0.25m、刚开雪、或积雪量变化较大（雪堆"慢慢长出来"）。
            if (!snowDrapeDirty
                && (anchorXZ - lastDrapeCam).sqrMagnitude < 0.0625f
                && Mathf.Abs(groundSnowAmount - lastDrapeAmount) < 0.04f) return;
            lastDrapeCam = anchorXZ;
            snowDrapeDirty = false;

            float px = snowGround.position.x;
            float pz = snowGround.position.z;
            // baseY 必须用**室外地平面**，不能用 groundY。
            // 上到二楼后 groundY = 2F 地板高，顶点会全部被算成"比 baseY 低 3.6m"
            // 再被 clamp 压到同一档，整片雪就成了一张悬在二楼的白板。
            float baseY = outdoorGroundY + 0.02f;    // 平面自身世界 Y（顶点 y=0 时的高度）
            float sx = SnowGroundSize;               // 单位网格缩放到 SnowGroundSize 米

            // 射线基准高度也用室外地平面（第二参数是 eyeY）。
            // SampleGroundHeight 内部拿 refY = groundY 做"离哪个面最近"的判据，
            // 而 groundY 在室内是地板高度 → 判据会选中室内楼板 → 雪面被捏成楼板形状，
            // 跟着人走、跟着视角变。用室外地平面做基准，选中的永远是室外地面。
            float probeEyeY = outdoorGroundY + 1.6f;

            bool changed = false;
            for (int i = 0; i < snowBaseVerts.Length; i++)
            {
                Vector3 b = snowBaseVerts[i];
                float wx = px + b.x * sx;
                float wz = pz + b.z * sx;
                float th = SampleGroundHeight(wx, wz, probeEyeY);

                // ══════════════════════════════════════════════════════════
                // 【P13】以地形为锚做**双侧**限幅 —— 这一条才是横线的正解
                //
                // ── 先说横线是怎么来的（两版错误都试过，留此备查）────────────
                //
                // 第一版：`if (高差 > 1.5m) { y = -10f; continue; }`
                //   想把楼板/树冠那一列"沉到看不见的地方"。后果是雪面上出现一圈
                //   高 9.8m 的竖直三角形：被淘汰的顶点沉到 -10m，相邻顶点还在地面，
                //   两者连成的四边形被斜着劈成两半 —— 半边水平、半边是竖直的"悬崖墙"。
                //   这些墙沿**建筑轮廓**围成一圈，从人眼高度平视时，整圈墙在屏幕上
                //   正好收束成一条横贯画面的细线；它属于雪盘自身几何、深度正常，
                //   所以**能穿进建筑内部**。像素级证据：横线不笔直、截面中间厚两端
                //   尖灭、能盖住树干与墙体。这条路彻底走不通。
                //
                // 第二版：只在**上侧**判据 `if (th - terrainY > 1.5f) th = terrainY;`
                //   修正了竖直悬崖，但它是**死代码**：th 来自 SampleGroundHeight，
                //   内部已把值限制在 [refY - MaxDrop, refY + MaxRise] = [refY - 8, refY + 1.5]，
                //   而 terrainY 与 refY 同源，于是恒有 th - terrainY ≤ 1.5，
                //   严格大于 1.5 永不成立 → 一行都没执行过。
                //
                // ── 真正的成因：**下侧**没限住 ──────────────────────────────
                // SampleGroundHeight 往下放到 MaxDrop = 8m（为了兜住下沉广场），
                // 再被下面 clamp 收成 -6m。旁边那一列却是 0 —— 1.6m 的网格间距上
                // 出现 6m 的落差，坡度 75°，**肉眼就是一面竖墙**。
                // 这面墙和第一版是同一类几何，只是矮一些，所以表现为"室内外都有一条
                // 淡淡的线"。横跨整个雪盘、沿建筑与地形台阶的边界绕圈，深度正常，
                // 于是能穿进建筑。
                //
                // ── 为什么用 |th - terrainY| 双侧 ───────────────────────────
                // terrainY 是**地形基底**（Terrain.SampleHeight），与建筑楼板、
                // 树冠、下沉结构全都无关，是这个点"应该在的高度"的稳定参照。
                // 校园实际地面起伏在 ±1.5m 内，超出这个范围的一律不是地面：
                //   · th - terrainY > +1.5 → 采到树冠/雨棚/屋顶
                //   · th - terrainY < -1.5 → 采到楼板下方/结构内部/误命中的深处
                // 两种情况都退回 terrainY，顶点就永远贴着地形，不会长出竖墙。
                // 代价：真的下沉超过 1.5m 的广场，雪面会平铺在广场口而不是沉下去。
                // 本项目没有这种广场（所有雪景截图里地面都是平的），可接受。
                // ══════════════════════════════════════════════════════════

                // 每顶点一次地形采样。Terrain.SampleHeight 是一次双线性插值，
                // 3249 个顶点也远低于一次物理查询的开销；且本函数本身被
                // "移动超过 0.25m 或雪量变化超过 0.04"节流，静止时完全不跑。
                float terrainY = ProbeOutdoorGround(new Vector3(wx, 0f, wz));
                if (Mathf.Abs(th - terrainY) > 1.5f)
                {
                    // 不是地面 —— 拉回地形基底，杜绝竖直楔形面
                    th = terrainY;
                }

                // 严格按地形平铺：顶点抬到"射线地面 + 抬升量"上方。
                // 抬升量(SnowGroundLiftNear)必须盖过脚下可见铺装/砖面的凸起，否则砖面会戳穿雪面。
                // 不再叠加任何程序化雪堆 —— 用户反馈"取消堆叠的一团，按地形平铺就行"。
                //
                // 【P13】clamp 上下限**不能对称**：
                //  · 往上只给 +1.0 —— 校园地面最大的上凸物是台阶/花坛（≈0.4~1.0m），
                //    再高就是树冠/雨棚/屋顶，那些一旦被采到就是"整片雪飞到头顶"。
                //    上一版给了 ±4.0，树冠顶（refY+2.4）被完整放过，直接导致雪面看不见。
                //  · 往下给 -6.0 —— 下沉广场、坡谷、挡墙背后确实可能低这么多，
                //    压得太死会让雪面悬在谷底半空。
                float d = Mathf.Clamp((th + SnowGroundLiftNear) - baseY, -6.0f, 1.0f);
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

                // 【P13】统计"竖直面"—— 横线的直接证据
                //
                // 横线不是贴图问题、不是深度问题，是**网格里有陡峭三角形**。
                // 判据：相邻顶点（同一行/同一列，间距 SnowGroundSize/56 ≈ 1.6m）
                // 的高差一旦超过 1.6m，三角形与水平面的夹角就 > 45°，
                // 从人眼高度平视时这一片会投影成一枚极扁的亮条。
                //
                // 修好后这里应该全是 0。若还有非零，括号里会带出最大高差，
                // 一眼就能看出是哪个方向的地形没被限住。
                int segV = 57;                       // BuildSnowPlaneMesh(56) → 57×57
                float stepM = SnowGroundSize / 56f;  // 相邻顶点水平间距（米）
                const float SteepDy = 1.6f;          // 超过它就算陡
                int steepX = 0, steepZ = 0;
                float maxDy = 0f;
                for (int iz = 0; iz < segV; iz++)
                {
                    for (int ix = 0; ix < segV; ix++)
                    {
                        int idx = iz * segV + ix;
                        if (ix + 1 < segV)
                        {
                            float dy = Mathf.Abs(snowWorkVerts[idx + 1].y - snowWorkVerts[idx].y);
                            if (dy > SteepDy) { steepX++; if (dy > maxDy) maxDy = dy; }
                        }
                        if (iz + 1 < segV)
                        {
                            float dy = Mathf.Abs(snowWorkVerts[idx + segV].y - snowWorkVerts[idx].y);
                            if (dy > SteepDy) { steepZ++; if (dy > maxDy) maxDy = dy; }
                        }
                    }
                }
                lastSteepCount = steepX + steepZ;
                lastSteepMaxDy = maxDy;
                lastSteepStepM = stepM;

                Bounds nb = snowMesh.bounds;
                // 打印节流：整体最多打 5 次。
                // 第一次重铺必打（那时雪刚起来，几何是最"冷"的状态）；
                // 之后只在自检窗口（snowAuditStage==1 / 室内快照刚打完）补打，
                // 且总量封顶 —— 用户一走动就会重铺，不封顶 Console 会被刷满。
                // 诊断用途只需要"几个不同时刻的横截面"，不需要每一帧。
                bool wantLog = geomLogCount < 5
                               && (!snowGeomLogged || snowAuditStage == 1 || indoorAuditDone);
                if (wantLog)
                {
                    snowGeomLogged = true;
                    geomLogCount++;
                    Debug.Log("[P13][雪面几何] 重铺完成 #" + (++drapeCount)
                          + "   顶点=" + snowWorkVerts.Length
                          + "   相邻间距=" + stepM.ToString("F2") + "m\n"
                          + "    竖直面（高差>" + SteepDy.ToString("F1") + "m）= "
                          + lastSteepCount + " 处"
                          + (steepX + steepZ > 0
                              ? "   **这就是横线**：X向 " + steepX + " / Z向 " + steepZ
                                + "，最大高差 " + maxDy.ToString("F2") + "m"
                              : "   横向无陡面，横线应已消失")
                          + "\n    局部 y∈[" + (nb.center.y - nb.extents.y).ToString("F2")
                          + ", " + (nb.center.y + nb.extents.y).ToString("F2")
                          + "]  跨度=" + nb.size.y.ToString("F2") + "m"
                          + (nb.size.y >= 8f ? "   **跨度异常**" : "   正常"));
                }
            }
            lastDrapeAmount = groundSnowAmount;
        }

        /// <summary>
        /// 远场雪盘地形跟随：逻辑同 DrapeSnow，作用于远场大圆盘（低分段）。
        /// 远场主要在雪天雾范围内，地形跟随误差被雾掩盖，仍保证地平线方向不露裸地形。
        /// </summary>
        private void DrapeSnowFar(Vector3 anchorXZ)
        {
            if (snowMeshFar == null || snowBaseVertsFar == null || snowWorkVertsFar == null) return;
            if (!snowDrapeDirtyFar
                && (anchorXZ - lastDrapeCamFar).sqrMagnitude < 0.0625f
                && Mathf.Abs(groundSnowAmount - lastDrapeAmountFar) < 0.04f) return;
            lastDrapeCamFar = anchorXZ;
            snowDrapeDirtyFar = false;

            float px = snowGroundFar.position.x;
            float pz = snowGroundFar.position.z;
            // 同 DrapeSnow：baseY 与射线基准都用室外地平面，不用 groundY。
            float baseY = outdoorGroundY + 0.02f;
            float sx = SnowGroundFarSize;
            float probeEyeY = outdoorGroundY + 1.6f;

            bool changed = false;
            for (int i = 0; i < snowBaseVertsFar.Length; i++)
            {
                Vector3 b = snowBaseVertsFar[i];
                float wx = px + b.x * sx;
                float wz = pz + b.z * sx;
                float th = SampleGroundHeight(wx, wz, probeEyeY);

                // 【P13】与近场同一条限幅：以地形基底为锚做**双侧**限幅。
                // 远场更必须做 —— 它每格 6.5m，同样的落差会在更长的跨度上
                // 形成一个坡度更缓、但**面积大几十倍**的斜面，从地平线方向看
                // 就是横贯画面的大块白带，比近场那条细线更显眼。
                // 详见 DrapeSnow 里对两种错误写法和真实成因的说明。
                float terrainY = ProbeOutdoorGround(new Vector3(wx, 0f, wz));
                if (Mathf.Abs(th - terrainY) > 1.5f)
                {
                    th = terrainY;
                }

                // 远场格更大（每格 6.5m），抬升量(SnowGroundLiftFar)略高，进一步减少雪面与地形穿插造成的块面。
                // 【P13】clamp 同样非对称（+1.0 / -6.0），理由见 DrapeSnow。
                float d = Mathf.Clamp((th + SnowGroundLiftFar) - baseY, -6.0f, 1.0f);
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
            lastDrapeAmountFar = groundSnowAmount;
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
        /// 在 (x,z) 处向下射线取**脚下地面**高度。复用 groundHits 缓冲。
        ///
        /// ══════════════════════════════════════════════════════════════════
        /// 【P13 修的真 bug：雪面浮到半空，变成破碎的白罩子】
        ///
        /// 原实现只做了一件事：在 12m 射线里丢掉"高于 eyeY+6 / 低于 eyeY-4"的命中，
        /// 然后在剩下的里面**取最高点**。问题出在"取最高点"这个策略上：
        ///
        ///   · 校园里到处是树。树干/树冠有碰撞体，射线从 eyeY+6 往下打**必然先命中树冠**；
        ///   · 树冠 y 普遍在 eyeY+2 ~ eyeY+5，正好落在原过滤区间内 → 被当成"地面"；
        ///   · "取最高点"会优先选中它 → 该顶点的雪面被抬到树冠高度；
        ///   · 56×56 个顶点里凡是落在树冠/灌木上方的，全被抬起来 →
        ///     90×90m 的雪盘变成一张起伏 1m+ 的破布，树下全是白色斑块，
        ///     而真正贴着地面的部分反而被抬到头顶看不见。
        ///
        /// 这就是「雪天满地没有雪、却有白色雾团飘在树上」的根因。
        /// 实测证据：mesh.bounds 的 y 尺寸 1.22、中心 0.89 —— 顶点普遍被抬了 ~1m。
        ///
        /// 【修法】加两道硬约束，缺一不可：
        ///   ① **法线必须朝上**（worldNormal.y ≥ 0.6）：地面是水平的，树冠/树干是斜的/竖直的。
        ///      这一条最有效 —— 树的半球形树冠只有顶上一小片法线朝上，其余全被滤掉。
        ///   ② **必须离参考地面近**（±2.5m 内）：花坛/台阶高差不会超过 2.5m，
        ///      树冠必然超过。用 groundY（脚下真实地面，由 ProbeGround 从相机出发算得更准）
        ///      当参照，而不是用 eyeY —— eyeY 是相机高度，徒步时≈1.6m，但对第三人称
        ///      相机来说它比玩家高 2~3m，用它当基准本身就偏。
        ///
        /// 顺带把 groundHits 从 16 扩到 32：密林 + 花坛 + 铺装叠在一起时，
        /// 16 个槽位会被前排树干占满，后面的真地面被 NonAlloc 直接丢弃。
        /// ══════════════════════════════════════════════════════════════════
        /// </summary>
        private float SampleGroundHeight(float x, float z, float eyeY)
        {
            // 参考高度必须用**室外地平面**，不能用 groundY。
            //
            // 【P13】groundY 是"相机正下方第一个面"。相机上到二楼后它就是 2F 地板，
            // 于是下面的判据"离 refY 最近的朝上面"会选中**室内楼板**，
            // 雪面被逐顶点捏成楼板形状 —— 表现为二层贴窗时雪地随视角变形、闪烁。
            // 改用室外地面后，无论相机在哪一层，判据选中的都是该列的室外地面。
            float refY = outdoorGroundValid ? outdoorGroundY : groundY;

            // ── 射线几何：必须以 refY 为基准，不能用 eyeY 硬编码 ──────────────
            // 原实现 origin = eyeY + 6、dist = 12。第三人称相机比玩家高 ~2m，
            // 于是实际只覆盖 [groundY - 4, groundY + 8] —— 下坡方向打不深，
            // 上坡方向又不够高。现在起点取 max(眼高上方一点, 地面 +12)，
            // 射程反推到 refY - 12，上下都留足余量。eyeY 只在相机很低时才起作用。
            float topY = Mathf.Max(eyeY + 2f, refY + 12f);
            var origin = new Vector3(x, topY, z);
            float dist = topY - (refY - 12f);
            int n = Physics.RaycastNonAlloc(new Ray(origin, Vector3.down), groundHits, dist);

            // ══════════════════════════════════════════════════════════════════
            // 【P13 第二次修：把"取最高点"换成"取最接近 refY 的朝上面"】
            //
            // 上一版只加了"法线朝上 + 高差窗口"两道约束，仍然沿用**取最高点**。
            // 实测证伪：mesh.bounds 的 y 尺寸从 1.22 涨到 **2.38**
            // （局部 y 范围 [0.28, 2.66]），说明仍有大量顶点被抬到 refY+2.4 左右。
            //
            // 为什么加了约束反而更糟：
            //   ① 我把窗口从 ±2.5 放宽到 ±12（为了兜住下沉广场），
            //      于是"高出 2.4m 的树冠顶"重新落回窗口内；
            //   ② 半球树冠**顶上那一小片法线是朝上的**，通过了法线约束；
            //   ③ 而"取最高点"这一条，必然优先选中它而不是脚下的地面。
            //   三者叠加 → 雪盘被抬到树冠高度，地面自然一点白都没有。
            //
            // 根子上的错误是**策略**，不是约束不够：
            //   地面在任一列里永远不是"最高的面" —— 树冠、墙顶、雨棚、亭顶都在它上面。
            //   反过来，"最接近 refY 的面"一定是地面：
            //     · 平地：地面在 refY，距离 0，无敌；
            //     · 台阶/花坛：地面在 refY+0.4，距离 0.4，仍小于树冠的 2.4；
            //     · 坡地：该列地面在 refY-3，距离 3，没有别的候选；
            //     · 头顶有雨棚（refY+3）、脚下有车库顶（refY-4）：地面 0 最小。
            //   所以判据从 max(y) 改为 min(|y - refY|)，一次遍历即可，
            //   也不再需要"严格窗口 → 放宽窗口"两轮兜底。
            // ══════════════════════════════════════════════════════════════════
            const float MinUpNormal = 0.6f;   // cos(53°)：低于它的都当墙/树冠斜面
            const float MaxSpan = 12f;        // 相对 refY 的搜索范围（米）
            // 高出 refY 多少以上就认为"这一列没采到地面"。
            // 校园里真实的地面上凸物：花坛 0.4m、台阶群 1.0m、景观挡墙顶 1.2m。
            // 树冠底面通常在 2.2m 以上 —— 1.5m 的阈值正好卡在两者中间。
            const float MaxRise = 1.5f;
            // 往下则可以放宽：下沉广场/坡谷比脚下低 3~6m 都算地面。
            const float MaxDrop = 8f;

            float best = float.NegativeInfinity;
            float bestDelta = float.MaxValue;
            bool found = false;

            for (int i = 0; i < n; i++)
            {
                RaycastHit h = groundHits[i];
                if (h.collider == null || h.collider.isTrigger) continue;
                if (IsPlayerCollider(h.collider)) continue;

                // 只认朝上的面。树冠侧面/树干/栏杆立面在这一步被滤掉。
                if (h.normal.y < MinUpNormal) continue;

                // 高差方向分开限制：往上卡死（防树冠/雨棚/屋顶），往下放宽（容下沉广场）
                float dy = h.point.y - refY;
                if (dy > MaxRise || dy < -MaxDrop) continue;

                float delta = Mathf.Abs(dy);
                if (delta > MaxSpan) continue;

                if (delta < bestDelta) { bestDelta = delta; best = h.point.y; found = true; }
            }

            // 一个都没采到（纯水面 / 无碰撞的装饰面）→ 退回脚下地面高度，
            // 等价于"整片摊平"，观感上仍是一层薄雪，不会露洞。
            return found ? best : refY;
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
            int n = Physics.RaycastNonAlloc(new Ray(camPos, Vector3.down), groundHits, 400f);

            float best = float.NegativeInfinity;
            bool found = false;
            for (int i = 0; i < n; i++)
            {
                RaycastHit h = groundHits[i];
                if (h.collider == null) continue;
                if (h.collider.isTrigger) continue;              // 触发器不算地面
                if (IsPlayerCollider(h.collider)) continue;      // 跳过玩家胶囊
                if (h.point.y < camPos.y - 0.001f)                     // 只取相机下方的地面
                {
                    if (!found || h.point.y > best) { best = h.point.y; found = true; }
                }
            }

            if (found) return best;
            return camPos.y - 1.62f;   // 打不到地面（室内/悬空）就用眼高反推
        }

        /// <summary>
        /// 判断碰撞体是不是玩家自身。
        ///
        /// ══════════════════════════════════════════════════════════════════
        /// 【P13 修的真 bug：第三人称下 indoor 恒定被判 true】
        ///
        /// 原实现只认 `cam.transform.root`：
        ///     Transform playerRoot = cam.transform.root;
        ///     return c.transform.IsChildOf(playerRoot);
        ///
        /// 但这套第三人称相机 **不是玩家的子物体** —— `TwinThirdPersonCamera` 用
        /// `_target` 跟随玩家、每帧自己算弹簧臂位置（`transform.position = ...`），
        /// 相机的 parent 是场景根。于是 `cam.transform.root` 返回**相机自己**，
        /// `IsChildOf` 对玩家身上的任何碰撞体都返回 false。
        ///
        /// 后果链（三个现象一条根）：
        ///   · 向上射线从相机往上打 40m，打到玩家自己的手臂/头 → 没有被识别为玩家
        ///     → `indoor = true`
        ///   · `indoorFade → 0`，积雪量 = snowAccum × altFade × **indoorFade** → **恒为 0**
        ///     ← 这就是"雪天有雪花、地上一点雪都没有"
        ///   · 雨雪粒子乘的是 `shelter = Lerp(0.25, 1, indoorFade)`，室内留 0.25 底噪
        ///     → 看着还有一点雨丝，但明显偏稀 ← 这就是"雨有但很稀"
        ///
        /// 【修法】玩家根节点不能从相机推，要独立认定。三层判据，从准到宽：
        ///   ① 找场景里的 `TwinPlayerController`（第三人称玩家的权威来源），取它的 transform
        ///   ② 退回：找 tag == "Player"
        ///   ③ 再退回：才用 cam.transform.root（第一人称模式下相机可能在玩家子节点下，这条仍然有效）
        ///
        /// 为什么优先 ①：`TwinPlayerController` 是 `[RequireComponent(CharacterController)]`
        /// 的可移动角色本体，它存在就意味着"场景里有个玩家在跑"，语义最稳，不依赖层级摆放。
        /// 不用 `FindObjectOfType` 每帧找 —— 结果缓存，只在 `RebindCamera()` 时失效重找一次。
        /// ══════════════════════════════════════════════════════════════════
        /// </summary>
        private bool IsPlayerCollider(Collider c)
        {
            if (c == null) return false;

            Transform playerRoot = PlayerRoot();
            if (playerRoot == null) return false;

            return c.transform.IsChildOf(playerRoot);
        }

        /// <summary>玩家根节点。结果缓存；相机会换（数字孪生 ↔ 第一人称）时由 RebindCamera 清缓存。</summary>
        private Transform _playerRoot;

        private Transform PlayerRoot()
        {
            if (_playerRoot != null) return _playerRoot;

            // ① 第三人称玩家控制器 —— 最权威。用 FindObjectOfType 全扫一次即缓存。
            var pc = FindObjectOfType<SchoolTwin.Player.TwinPlayerController>();
            if (pc != null) { _playerRoot = pc.transform; return _playerRoot; }

            // ② tag 兜底
            GameObject byTag = null;
            try { byTag = GameObject.FindGameObjectWithTag("Player"); }
            catch (UnityException) { /* 项目没定义 Player tag 时 FindGameObjectWithTag 会抛 */ }
            if (byTag != null) { _playerRoot = byTag.transform; return _playerRoot; }

            // ③ 最后才用相机的根（第一人称模式下相机常挂在玩家下面，这条有效；
            //    第三人称独立相机走到这里说明前两层都没命中，此时宁可不认，也不要误判整棵树）
            if (cam != null) _playerRoot = cam.transform.root;
            return _playerRoot;
        }

        // 【P13】16 → 32：密林 + 花坛 + 铺装叠在一起时，16 个槽位常被前排树干占满，
// 后面的真地面被 RaycastNonAlloc 直接丢弃 → 该点采不到地面、退回 groundY，
// 于是雪面在那儿塌下去一块。扩容成本只有一个 32 元素的栈上数组。
        private readonly RaycastHit[] groundHits = new RaycastHit[32];

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
        public void RebindCamera()
        {
            cam = null;
            // 玩家根也要跟着失效：从数字孪生（无玩家/相机在根）切回第三人称时，
            // 缓存的空值或旧相机根会让 IsPlayerCollider 一直判错，积雪又归零。
            _playerRoot = null;
        }
    }
}

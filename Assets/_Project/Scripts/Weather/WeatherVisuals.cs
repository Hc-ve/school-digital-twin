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
    /// 几千个粒子每帧一次，代价远大于收益。
    /// 室内看不到粒子靠的是**发射端判定**（头顶射线命中天花板就不发射），
    /// 不是靠粒子撞墙 —— 这两条分工别搞混，详见 UpdateGround 里的长注释。
    /// </summary>
    public class WeatherVisuals : MonoBehaviour
    {
        private const float AreaSize = 64f;      // 【P47】发射盒水平尺寸 110 → 64：盒随角色走，
                                                 // 密度全部集中在角色周边可视区（远处黑区里的粒子是浪费），
                                                 // 局部密度 ×3，修"有的地方多有的地方没有"的区域性稀疏。
        // 【P43 追加 11-17 主理人反馈】撒播高度 16 → 60。
        // 【P47 · 2026-11-19】60 → 28：雪以 1.3 m/s 匀速下落，撒得越高、新走到区域
        // 的"空窗期"越长（60m 要落 46 秒——主理人报的"局部区域几乎没雪"正是玩家
        // 走动后新区域等雪的延迟 + 缓冲满载节流的叠加）。28m 仍高于校园全部建筑
        // （2~4 层 ≈ 15m），二楼窗外落雨观感不变；落程 28m → 21.5s，空窗期减半。
        // 16m 时雨雪从楼顶一线（教学楼约 15~16m）凭空出现，观感是"比楼高一点点"，不像天上来的；
        // 60m ≈ 教学楼 3.75 倍，肉眼不再看到"出生线"。注意发射点的真实语义是
        // 「室外地面 + EmitHeight」（见 UpdateGround :3912 emitPos.y = outdoorGroundY + EmitHeight），
        // 不是字面的"相机上方"。寿命不用跟着改：UpdateRainLifetime / UpdateSnowLifetime
        // 是按实测落距解 √(2h/g) 的解析式，落距变大自动重算。
        private const float EmitHeight = 28f;    // 【P47】撒播高度基底 60 → 28（见 AreaSize 处的空窗期分析；仍高于校园全部建筑）

        // 【P50 2026-11-19】发射盒网格吸附步长（米）——修"雪随角色移动而变稀/消失"。
        // 根因：原 emitPos.xz 每帧 = 角色 xz，玩家一动，新位置才开始发射；雪从 45m 高
        // 落下需 ~13s，这 13s 就是"走过去雪还没落下来"的空窗（主理人实测的断层）。
        // 修法：盒中心吸附到 24m 网格——格内走动盒完全静止（发射源不动=雪均匀），
        // 跨格才跳一次且步长 24m << 盒半宽 55m，新旧覆盖区重叠 31m，
        // 前方新区域多已被旧盒覆盖过、雪已在下 → 空窗基本消除。
        // 粒子 simulationSpace=World，旧粒子留在原地继续落，跨格瞬间也无断层。
        // 盒尺寸/发射率/粒子数全不变 → FPS 零影响（实测放大盒到 240m 会掉帧，故不采用）。
        private const float EmitSnapGrid = 24f;
        // 【P43 追加 11-17 二轮】发射高度随相机自适应：自由相机（F3）升到高空俯瞰时，
        // 固定 60m 的发射面就是一顶"雪天花板"—— 主理人航拍截图里看到的正是它。
        // 实际发射 Y = max(地面+60, 相机+40)，封顶 地面+250（封顶是为了给寿命解析式与
        // 粒子缓冲一个可计算的边界，见两处 maxParticles 的推导）。xz 仍锚角色（F19）。
        private const float CamEmitClearance = 40f;   // 发射面至少在相机上方这么多
        private const float MaxEmitAGL = 250f;        // 相对室外地面的发射高度上限
        private const float DomeRadius = 380f;   // 云穹顶半径，必须小于相机 farClip(2000)

        private const float RainRateMax = 3200f; // rain = 1 时每秒发射数
        // 【P48 · 2026-11-19 提速+增密】雪密度 3200 → 12000：下落提速到 3.5 m/s 后
        // 粒子在柱内停留时间缩短（寿命 21.5s→8s），同密度下存活数会掉——密度必须
        // 按新寿命重算：12000×8s ≈ 9.6 万存活（体积密度 ≈0.84 粒/m³，前视切片
        // ~470 粒，图2量级）。maxParticles 150000 容纳 9.6 万富余。
        // 灯下提亮由 PrecipitationLit 的解析距离场负责（单 pass，密度上调零过绘代价）。
        private const float SnowRateMax = 12000f; // snow = 1 时每秒发射数

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

        // 【P13 第十次修】大跨度高厅（礼堂 / 室内篮球馆）的屋顶高度。
        //
        // ── 为什么 4.6m 不够 ────────────────────────────────────────────
        // 主理人 2026-11-05 报：走廊修好了，但"礼堂和室内篮球场可以很清晰地
        // 看到雨雪粒子"。这两个都是**单层大跨度空间**，屋架下弦普遍 7~12m，
        // 远在 4.6m 之上 → 头顶射线在 4.6m 内什么都没打到 → indoor=false
        // → CullIndoorParticles 第一行就 return，一粒都不剔。
        //
        // 注意这**不是**把 4.6 改成 12 就能解决：4.6m 这个值同时承担着
        // "别把树冠当成天花板"的职责（见上方长注释）。直接放宽到 12m，
        // 站在树下就会被判成室内，雨雪全没 —— 那是更严重的回归。
        //
        // ── 分档判定的依据 ──────────────────────────────────────────────
        // 关键不对称：**室外头顶是天空，没有任何碰撞体**。
        // 所以在 4.6m 内没命中时，再往 12m 找一次，能命中的只可能是
        // 真正的高厅屋顶（或树冠 —— 由法线判据继续过滤）。
        // 树冠即便被 12m 档命中，它的球面法线也过不了 -0.92 这一关。
        private const float IndoorCeilHeightTall = 12f;
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

        // 【P43 修复 11-16】雨雪粒子密度倍率（小 0.35 / 中 1 / 大 2），经 SetDensityScale 写入。
        // 【为什么乘在写入源头而不是 rateOverTimeMultiplier】① 雨/雪发射器因 P13-D9 刻意
        // 不挂父节点（世界坐标定位），任何子级遍历都够不到，multiplier 方案只覆盖到水花；
        // ② 三份速率曲线都是 Constant 模式，该模式下 multiplier 的读改行为不可靠、且会被
        // 本类对 rateOverTime 的重写覆盖。乘在源头上与重写机制天然共存。默认 1，
        // 不调密度时行为与历史版本完全一致。
        private float _densityScale = 1f;
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
        private float ceilFromY;      // 射线起点的世界 Y（自检：应等于角色根 + 0.02）
        private bool ceilSelfHit;     // raw 第一个命中是不是玩家自己（自检：这是 D9 的回归警报）
        private float ceilDist = -1f;      // 命中距离，-1 = 没命中
        private float ceilNormalY = 0f;    // 命中面的法线 Y
        private string ceilName = "";
        private Collider ceilCollider;     // 头顶命中的碰撞体（屋顶可见性诊断用）
        private bool ceilTallFallback;     // 是否是靠 12m 远档才找到天花板的（= 高厅）
        private bool ceilByRenderer;       // 是否是靠渲染网格包围盒判出的室内（无碰撞体场馆）

        // 【P13】"可能是屋顶"的渲染器缓存。
        // 场景只有 74 个 MeshRenderer，一次筛完缓存住，之后每次只遍历这几个。
        // 为什么要缓存：UpdateGround 每 0.15s 跑一次，每次 FindObjectsOfType 全场景扫
        // 在手机上会明显掉帧；而屋顶是静态建筑，运行时不会新增。
        private Renderer[] roofCandidates;
        private bool roofCandidatesBuilt;

        // 【P13 Bug2】观景模式探测的短时缓存（详见 IsFreeCamMode）
        private bool _freeCamCached;
        private float _freeCamProbeTime = -1f;

        // 【P13】给"40m 上限探测"用的独立缓冲。不能复用 ceilHits：
        // 那份数据是主判定刚算出的结果，借来借去会把结论搅乱。
        private readonly RaycastHit[] farHits = new RaycastHit[32];

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
            // ══════════════════════════════════════════════════════════════
            // 【P13-D9】⚠️ 不能 SetParent(transform) —— 这是"二楼看不到雨"和
            //           "雨随视角平移"的共同根因。
            //
            // 本组件 LateUpdate 每帧执行 transform.position = cam.position，
            // 目的是让穹顶/雾这类**以相机为球心**的东西跟着相机走。
            // 但雨雪发射盒不该跟相机：它应该锚在**角色 + 室外地平面**上。
            // 挂成子节点后，两者被强行绑在一起，于是
            //   · 二楼时发射盒中心跑到相机上方 16m → 高过天花板 → 被楼板挡住
            //   · 转身时 camPos 在 orbital 圆弧上移动 → 雨丝整体横移
            //
            // 所以这里**不设父节点**，让它做场景根对象，
            // 世界位置由 UpdateGround 每帧写成 (角色xz, 室外地平面y + EmitHeight)。
            // 与 SnowOnTrees 是同一条纪律：**顶点/位置是世界量的物体，不能挂在
            // 每帧跟随相机的父节点下。**
            // ══════════════════════════════════════════════════════════════
            go.transform.position = new Vector3(0f, EmitHeight, 0f);

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
            // 【P43 追加】撒播高度随相机自适应后（最高 地面+250m），落程更长：
            // 250m 时雨寿命 t=√(500/12.75)≈6.1s，活跃粒子 ≈ rate × t：
            // Medium 3200×6.1≈19.5k，High(×2)≈39k。旧 24000 在高空档会被静默截断
            // （缓冲满 = 新粒子不发射 = 下雪变稀，还查不出原因，与 P28-01 同型病）。
            main.maxParticles = 45000;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;

            var em = ps.emission;
            em.enabled = true;
            em.rateOverTime = 0f;                          // 由 Apply() 控制

            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Box;
            // ══════════════════════════════════════════════════════════════
            // 【P13 修的真 bug：发射盒是个"竖直的墙"，不是"水平的板"】
            //
            // 原值 (AreaSize, 0.5, AreaSize)，注释写的是"Y 是薄片厚度"。
            // 这个前提是错的 —— 下面那行 rotation=(-90,0,0) 会把**局部轴**转掉：
            //     局部 X → 世界 X
            //     局部 Y → 世界 Z      ← 不是世界 Y！
            //     局部 Z → 世界 -Y     ← 薄片该走的是这一根
            // 于是 world 尺寸变成 X=110、Y=110、Z=0.5：
            //     一块 **110m 宽 × 110m 高、只有 0.5m 厚** 的竖直墙壁。
            //
            // 后果（主理人报的"雨雪下得不均匀、随视角变"）：
            //   · 侧着看它 → 整面雨幕糊在脸上，密得反常
            //   · 转 90° 顺着墙看 → 只剩一条薄边，几乎没有雨
            //   · 雪更明显：雪是慢落、空中停留 10 秒，墙的形状一眼看得出
            //
            // 对照 BuildSplash 的写法就能确认这是笔误而不是设计：
            // 那边是 (SplashArea, SplashArea, 0.012f) —— 薄轴放在 Z，才是水平的。
            // 这里必须同口径：薄轴 = 局部 Z = 世界 Y。
            // ══════════════════════════════════════════════════════════════
            shape.scale = new Vector3(AreaSize, AreaSize, 0.5f);
            shape.position = Vector3.zero;
            // ══════════════════════════════════════════════════════════════════
            // 【P13 修的第二处：rotation 符号反了，雨雪是**朝上**发射的】
            //
            // 绕 X 轴转 -90° 的真实映射（用旋转矩阵算，不要凭印象）：
            //     局部 X → 世界 X
            //     局部 Y → 世界 -Z
            //     局部 Z → 世界 +Y   ← **向上！**
            //
            // 而 Box 形状的 startSpeed 正是沿**局部 +Z** 发射的。
            // 所以原实现让雨雪向上喷，与注释写的"→ 世界 -Y，初速向下"完全相反。
            //
            // 危害按粒子自重分档：
            //   · 雨：上行最多 0.4 m/s，重力 9.81×1.3=12.75 → 0.03 秒就掰回来，看不出
            //   · 雪：上行 0.25~0.6 m/s，重力只有 9.81×0.035=**0.343**
            //         → 要飞 1.75 秒、升高 0.52m 才开始下落
            //     雪又慢落、空中停 10 秒，这段"先往上飘"一眼看得出，
            //     也是"雪下得不自然"的组成之一。
            //
            // 改成 +90° 后：局部 Z → 世界 -Y（向下），局部 Y → 世界 +Z。
            // 盒子的世界尺寸不变（只有 Z 轴符号翻转），所以 scale 那边的修法不受影响。
            //
            // ⚠️ 这一处与 UpdateSnowLifetime / UpdateRainLifetime 的落距公式强耦合：
            // 那两个公式按"初速向下"推的 t = (-v0 + √(v0²+2gh))/g。
            // 若把这里改回 -90°（向上），公式立刻失配，雨丝会在空中断掉。
            // ══════════════════════════════════════════════════════════════════
            shape.rotation = new Vector3(90f, 0f, 0f);

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
                var m = ProceduralAssets.MakePrecipitationMaterial(ProceduralAssets.RainStreak, Color.white, "ProcRainMat");
                if (m != null) pr.sharedMaterial = m;
            }

            ps.Play();
            return ps;
        }

        private ParticleSystem BuildSnow()
        {
            var go = new GameObject("SnowParticles");
            // 【P13-D9】同 BuildRain：不挂父节点。理由见那边的大段注释 ——
            // 发射盒必须锚在「角色 xz + 室外地平面 y」，不能跟着相机跑，
            // 否则二楼看不到雪、转身时雪幕整体横移。
            go.transform.position = new Vector3(0f, EmitHeight, 0f);

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
            // 修法：shape.rotation = **+90**（不是 -90）让局部 +Z → 世界 -Y，初速朝下。
            // ⚠️ 符号别照抄 BuildSplash：那边要的是**向上溅**（-90 → 世界 +Y），
            // 雨雪要的是**向下落**（+90 → 世界 -Y）。方向相反，值也相反。
            // 初速同时收到 0.25~0.6：雪片本身下落速度就是 0.3~1 m/s 量级，
            // 太大就成了冰雹，太小又全靠重力慢慢加速（要 10 秒才追上落距）。
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.25f, 0.60f);
            // 【P50 2026-11-19】主理人实测"雪粒子像纸片、近景远景都太大" → 先砍到 0.05~0.09；
            // 二轮反馈"抬头满天、平视几乎空"（体积几何 + 尺寸过小致中远距离亚像素）→ 回调到 0.07~0.12：
            // 比纸片小、比 0.05 档平视可见。Billboard 近大远小是透视，缩小同时改善近/远景。
            main.startSize = new ParticleSystem.MinMaxCurve(0.07f, 0.12f);
            main.startColor = new Color(1f, 1f, 1f, 0.9f);
            main.gravityModifier = 0f;                     // 【P47】重力归零：恒定终端速度下落（见 vel.y 注释）
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            // 【P43 追加】撒播高度随相机自适应后（最高 地面+250m），雪寿命最高
            // t=√(500/0.343)≈37.4s（Clamp 上限已同步 30→40）：活跃粒子
            // 【P47】缓冲与 SnowRateMax 3200 配套：匀速柱 3200×46s≈14.7 万全装得下；
            // High(×2)≈29 万被 150k 截顶——暴雪预设的上限，可接受。
            // （原 45000 配 520 粒/秒；密度上调后缓冲必须配套，否则缓冲满会
            //  静默截断发射——P13 写过这条教训，见 BuildRain 同款注释。）
            main.maxParticles = 120000;   // 【P47】150k → 120k：撒播高度 28m + 盒 64m 后稳态存活 ~7 万，留足余量
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;

            var em = ps.emission;
            em.enabled = true;
            em.rateOverTime = 0f;

            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Box;
            // 【P13】薄轴必须放**局部 Z**（旋转后 → 世界 -Y），与 BuildRain 同一笔误同修。
            // 原值 (AreaSize, 0.5f, AreaSize) 在 rotation=(±90,0,0) 下会变成
            // 110m×110m 的**竖直墙**，雪片从一堵墙里喷出来，侧看糊脸、顺看全空。
            // 详见 BuildRain 里那段完整推导。
            shape.scale = new Vector3(AreaSize, AreaSize, 0.5f);
            shape.position = Vector3.zero;
            // +90°（不是 -90°）：局部 Z → 世界 -Y，初速朝下。
            // 符号的推导见 BuildRain，雪这边后果更明显（重力 0.343，会先上飘 1.75 秒）。
            shape.rotation = new Vector3(90f, 0f, 0f);

            // 噪声扰动：雪花左右打旋，是"雪"和"白点雨"最大的观感差别。
            // 注意 strength 不能太大：粒子一旦被噪声推得比下落还快，
            // 就会看到雪片"横着飘"而不是"斜着落"。
            var noise = ps.noise;
            noise.enabled = true;
            noise.strength = 0.35f;   // 【P47】0.55 → 0.35：噪声漂移过强会把粒子推成疏密条带（区域性不均的次要因素）
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
            // 【P48 · 提速】恒定下落速度 -3.5 m/s（叠加初速 0.25~0.6 → 实际 3.75~4.1 m/s）。
            // 主理人二轮反馈"下落太慢，一轮下落完另一轮还在空中"——1.3 m/s 的飘落
            // 28m 要 21.5 秒，节奏像"慢动作"；3.5 m/s 是大雪+风的观感，28m 落程
            // 8 秒一轮，循环感立起来。仍保持**匀速**（P47 的教训：重力加速会让
            // 贴地密度只有高空 1/5，平视看不到雪）。
            vel.y = new ParticleSystem.MinMaxCurve(-3.5f);
            vel.z = new ParticleSystem.MinMaxCurve(0f);

            var pr = go.GetComponent<ParticleSystemRenderer>();
            if (pr != null)
            {
                pr.renderMode = ParticleSystemRenderMode.Billboard;
                pr.minParticleSize = 0f;
                pr.maxParticleSize = 1f;
                pr.shadowCastingMode = ShadowCastingMode.Off;
                pr.receiveShadows = false;
                var m = ProceduralAssets.MakePrecipitationMaterial(ProceduralAssets.Snowflake, Color.white, "ProcSnowMat");
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
                var m = ProceduralAssets.MakePrecipitationMaterial(
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
            // ══════════════════════════════════════════════════════════════
            // 【P13 Bug3 雪壳 v2 已复用 —— 2026-11-17 主理人要求屋顶/建筑顶积雪】
            //
            // 11-05 事故的四路连带（幕布发黑/角色浮空/脚印消失/自己糊成一团），
            // v2 按分册 §八判据逐条堵死，防线明细见 SnowOnRoofs.cs 头注：
            //   ① 判屋顶不再用世界绝对高度 → 「相对本网格自身竖向跨度 ≥60%」+「向上射线
            //      4m 确认无遮挡」双过滤，室内楼板/吊顶/楼梯平台全部出局；
            //   ② 一楼一壳（每来源网格一个小 mesh），不再合成全场景单块；
            //   ③ renderQueue 2996（屏幕是 Geometry 2000；且室内面已被 ① 出局，
            //      与屏幕不再空间重叠）；
            //   ④ 壳**不带 Collider**（RequireComponent 只有 MeshFilter/MeshRenderer）
            //      → SampleGroundHeight 的 Physics 射线天然穿透，角色/脚印免疫；
            //   ⑤ IndoorByRendererRoof 建表加了雪壳着色器/名字双排除（本文件 :2450 附近）。
            // 验收时必须复测的四个老病灶：机房幕布、角色落地、脚印、室内判定。
            // ══════════════════════════════════════════════════════════════
            EnsureRoofSnow();
        }

        /// <summary>
        /// 【P13 Bug3】自动挂载屋顶积雪组件。
        ///
        /// ══════════════════════════════════════════════════════════════════
        /// ⛔【2026-11-05 12:50 已停用 —— 重新启用前必须先读完这段】
        ///
        /// 本方法打出的雪壳会**污染整个场景**，实测四路连带故障：
        ///   ① 机房幕布发黑（renderQueue 2997 盖在所有屏幕之上）
        ///   ② 角色浮空（被 SampleGroundHeight 当成"地面"）
        ///   ③ 脚印消失（同一份错误采样）
        ///   ④ 屋顶反而仍无雪（混入大量非屋顶面，自己糊成一团）
        ///
        /// 真根因：`SnowOnRoofs.minRoofY` 是**世界绝对高度**，不是相对建筑。
        /// 机房在 1F、天花板 3m，而 minRoofY=3.2f 拦不住二层地板、走廊吊顶、
        /// 楼梯平台、看台顶面 —— 全被当成"屋顶"复制了。
        ///
        /// 要重做，必须先解决"如何只取真正的屋顶"：
        ///   · 按建筑根节点分组（需要先有可信的建筑清单）
        ///   · 或按"某点向上最近的面"逐点探测（而不是全场景扫三角形）
        /// 单纯调 minRoofY 会连带漏掉 1~2 层楼的屋顶，不是参数问题。
        /// ══════════════════════════════════════════════════════════════════
        ///
        /// 现象：下雪天地面、树根、草皮全白了，唯独**建筑屋顶还是原色**，
        /// 像被单独挖掉一块雪 —— 非常突兀。
        ///
        /// 为什么现有三块雪盘盖不到屋顶：近场 90m 盘、远场 260m 盘、树根 3m 圆盘
        /// 全部是**水平板**，且定位方式都是"逐顶点向下打射线贴地"
        /// （见 SampleGroundHeight，它把 `dy > MaxRise(1.5m)` 的点直接丢弃）。
        /// 屋顶在 refY+12 ~ refY+20，高出十几倍 → 全部被丢弃。
        /// 即便不丢弃，一块贴地的水平板在拓扑上也不可能同时贴地面又贴屋顶。
        /// 所以这不是参数没调好，是**数据通路根本不支持**。
        ///
        /// ⚠️ 与 EnsureTreeSnow 同一个坑，必须照抄它的做法：
        /// SnowOnRoofs 的顶点全部是**世界坐标**（相邻三角形之间纹理才连续），
        /// 它必须钉在世界原点，而本组件 LateUpdate 每帧把 transform 搬到相机位置。
        /// 若把它挂成本组件的子节点，整块雪壳会跟着相机平移，
        /// 几十片屋顶雪随着相机移动而整体漂移（白线 bug 的同型病）。
        /// 它自己 Build() 里也会 SetParent(null) 脱离，所以判存在必须用
        /// **全场景查找**，不能用 GetComponentInChildren（脱离后就找不到了，
        /// 每次重建都会再生成一个 → 多份雪壳叠在一起，明暗翻倍）。
        /// </summary>
        private void EnsureRoofSnow()
        {
            if (Object.FindObjectOfType<SnowOnRoofs>(true) != null) return;
            var rgo = new GameObject("RoofSnowShell");
            rgo.transform.position = Vector3.zero;
            rgo.AddComponent<SnowOnRoofs>();
        }

        /// <summary>
        /// 自动挂载树根积雪组件：生成一个 TreeSnowPiles 物体并挂上 SnowOnTrees，免去手动配置。
        /// 若场景里已经存在 SnowOnTrees（用户手动挂的）则跳过。
        ///
        /// ⚠️【P13】不能再用 GetComponentInChildren 判存在 —— 原因见下：
        /// SnowOnTrees 的顶点全部是**世界坐标**，它必须钉在世界原点，
        /// 而本组件的 LateUpdate 每帧把 transform 搬到相机位置。
        /// 若把它挂成本组件的子节点，整块树根雪堆会跟着相机平移，
        /// 几十块同高的水平圆盘与相机眼高共面 → 屏幕上收束成一条横贯全屏的白线
        /// （2026-11-04 主理人反复反馈的"诡异白线"，成因即此）。
        ///
        /// 所以 SnowOnTrees.Build() 里会自行 SetParent(null) 摆脱本层级。
        /// 一旦脱离父节点，GetComponentInChildren 就找不到了，
        /// 每次重建都会再生成一个 → 必须改为**全场景**查找。
        /// </summary>
        private void EnsureTreeSnow()
        {
            // 全场景查找（含未激活），避免"已存在但已脱离本层级"导致重复创建
            if (Object.FindObjectOfType<SnowOnTrees>(true) != null) return;
            var tgo = new GameObject("TreeSnowPiles");
            // 不再挂成本组件的子节点：SnowOnTrees 用世界坐标建 mesh，
            // 父节点的任何平移都会让雪堆整体错位。这里直接放到场景根。
            tgo.transform.position = Vector3.zero;
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

        /// <summary>
        /// 【P43】设置雨雪粒子密度倍率（TimeCycleDirector 三档调用：小 0.35 / 中 1 / 大 2）。
        ///
        /// 【为什么复位三个 last 缓存】Apply 的三路速率写入都带「值没变就不写」闸
        /// （lastRain / lastSnow / lastSplashRain），密度变了但天气没变时闸不会自己开 ——
        /// 复位成 -1 哨兵强制下一次 Apply 全量重写，之后恢复「值变才写」的原节流。
        /// </summary>
        public void SetDensityScale(float k)
        {
            k = Mathf.Max(0f, k);
            if (Mathf.Approximately(k, _densityScale)) return;
            _densityScale = k;
            lastRain = -1f;
            lastSnow = -1f;
            lastSplashRain = -1f;
        }

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
            // 【P13 第三次修：室内不再"关掉天气"，改成"只清掉头顶这一块"】
            //
            // 前两轮在这里改了两遍，方向都是错的，记录如下以免再绕回去：
            //
            //   第一版  shelter = Lerp(0.25, 1, indoorFade)   留 25% 底噪 → 室内飘雨
            //   第二版  shelter = indoorFade                  室内 = 0 → 窗外的雨也没了
            //
            // 第二版的问题（主理人本轮报的）：下雨天进屋，"室内看室外看不到水坑，
            // 出来就能看到""二楼看不到雨和水坑""室内窗外看不到雨雪"。
            // 根因不是 indoorFade 算错了，而是**把"世界天气"和"玩家头顶"绑成一个量**。
            // 天气是全世界的，站在屋檐下并不代表校园里不下雨。
            //
            // 正确模型（三层各管各的）：
            //   · 发射率  → 只看 rain/snow 与 altFade。**永远满速**，天气属于世界。
            //   · 室内遮挡 → 由 CullIndoorParticles 逐粒清掉"落在楼里/雨棚下"的那些。
            //                深度测试会自动挡住墙后的粒子，所以只要清掉真正进屋的，
            //                窗外的雨就自然可见 —— 这才是"透过窗户看雨"。
            //   · 水花    → 仍按 indoorFade 关（屋里地上不该冒水花），见下面的 splashRate。
            //
            // 为什么不能靠"提高 indoor 判定精度"把雨棚排除掉：
            // 站在雨棚下确实不该被雨淋，那是**对的**判定。错的只是"由此关掉整个世界"。
            // ══════════════════════════════════════════════════════════════════
            float effRain = rain * altFade;
            float effSnow = snow * altFade;

            if (rainPs != null && !Mathf.Approximately(effRain, lastRain))
            {
                lastRain = effRain;
                var em = rainPs.emission;
                em.rateOverTime = Mathf.Max(0f, effRain) * RainRateMax * _densityScale;

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
                em.rateOverTime = Mathf.Max(0f, effSnow) * SnowRateMax * _densityScale;

                var vel = snowPs.velocityOverLifetime;
                vel.x = new ParticleSystem.MinMaxCurve(wind * 2.2f);
                vel.z = new ParticleSystem.MinMaxCurve(wind * 0.8f);

                var noise = snowPs.noise;
                noise.strength = 0.35f + wind * 0.7f;
            }

            // 雪花寿命同理（见 UpdateSnowLifetime 的注释：8~12s 的写死值会让一半雪花在空中蒸发）
            UpdateSnowLifetime(dt);

            // ══════════════════════════════════════════════════════════════════
            // 【P13 第三次修：不能 Clear 全部，只剔掉"头顶这一块"的粒子】
            //
            // 上一版在这里写了 `rainPs.Clear(true)` / `snowPs.Clear(true)`，
            // 进楼那一帧把**所有**粒子清空。当时是为了解决"进屋十秒还在飘雪"
            // （雪花 lifetime 8~12s，只掐发射源来不及）。
            //
            // 但那个修法把**窗外的雨也一起清掉了** —— 而窗外的雨正是要保留的。
            // 走廊里靠窗站，画面里应该有雨，只是不该有"落在走廊地面上"的雨。
            // 主理人本轮报的"室内窗外看不到下雨下雪的粒子效果"就是这个。
            //
            // 现在的做法：逐粒检查，只把**已经落进室内空间**的粒子 Kill 掉。
            //   · 判据用同一套头顶射线（ceilDist / indoor），不另起逻辑；
            //   · 只在下落的最后一段（贴地 3m 内）才判，避免把"还在楼顶上方"
            //     的正常粒子误杀 —— 那些粒子本来就该继续往楼下落，落在楼外；
            //   · 每帧最多检查一批（Stride 抽样），不做全量，代价可控。
            //
            // 为什么不在发射端就把"室内区域"排除掉：
            // 发射盒是 110m 见方的平面，无法表达"中间挖掉一个房间"。
            // 逐粒剔除是唯一能同时满足「窗外有雨」和「屋里没雨」的做法。
            // ══════════════════════════════════════════════════════════════════
            // 室内时量一次"我所在的房间有多大"，供逐粒剔除用。
            // 只在室内才测：室外不需要房间边界，四条射线纯浪费。
            if (indoor) ProbeRoomRadius(dt);
            else roomValid = false;

            CullIndoorParticles(rainPs, rain, true);
            CullIndoorParticles(snowPs, snow, false);

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
                em.rateOverTime = splashRate * _densityScale;
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

            // ══════════════════════════════════════════════════════════════════
            // 【P28 2026-11-05】脚印与抬升**不再吃 indoorFade**
            //
            // 原式：playerSnowAmount = groundSnowAmount * indoorFade
            //
            // 问题不在于 indoorFade 算错，而在于**它不该出现在这个乘积里**。
            // indoorFade 是"头顶有没有遮挡"的平滑值，而"脚底下有没有雪"
            // 是另一件事。两者被乘在一起之后，任何一次室内判定抖动
            // 都会瞬间把脚印和抬升一起抹掉：
            //
            //   出门 → 判定翻成室内 → indoorFade 0.33 秒内滑到 0
            //        → snowAmount 归零 → 脚印立刻停、角色立刻往下沉
            //   回到楼里 → 判定翻回室外 → 又慢慢长回来 → 脚印"复活"
            //
            // 主理人报的四个现象全部由此而来：
            //   · 走出门口一定范围才有脚印，出去就没了
            //   · 往回走角色下陷、脚完全埋进雪里
            //   · 走远看不见、走近又出现（判定在 AABB 边界上反复翻）
            //
            // 真正的室内外区分应该由**脚下有没有雪面**来回答，而不是靠
            // 头顶射线。室内地板本来就没有 SnowGround 盖着，那些地方
            // 采不到雪面高度，抬升自然为 0 —— 不需要 indoorFade 帮忙。
            // 脚印那边同理：室内地面的世界坐标不在雪盘覆盖范围内，
            // shader 里 `FootprintAt` 比对的是雪面顶点的世界 XZ，
            // 楼内地板上根本不会显示（雪盘被墙挡着，也画不到楼里）。
            //
            // 所以这里直接给 groundSnowAmount（= 积雪量 × 高度淡出），
            // 让"雪有多少"成为唯一决定因素。
            // ══════════════════════════════════════════════════════════════
            playerSnowAmount = groundSnowAmount;
            UpdatePlayerOnSnow(dt, playerSnowAmount);

            // ---- 【P13】雪地脚印 ----
            UpdateFootprints(playerSnowAmount);

            // ---- 【P13】雪天自检：切到雪天后自动打一次现场快照 ----
            // 为什么不做成菜单：雪天"有粒子无积雪"这种故障，必须看**当时的**运行时值。
            // 让用户 Play 中手动点菜单、再把 Console 截图发回来，中间会丢一轮；
            // 直接在雪量第一次冲破阈值时自己打一次，Editor.log 里就有全部答案。
            // rain 也要传：室内粒子问题雨天同样存在，只认 snow 会让雨天无日志。
            TickSnowAudit(dt, snow, rain);

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
        private int auditWeatherKind;      // 0=无天气 1=雨 2=雪，变了就重新取证

        // 【P13】手动强制执行一次室内自检的快捷键。
        //
        // 为什么需要：室内自检每轮只打一次（indoorAuditDone 挡住），
        // 改完代码想看"这次剔干净了没有"时，得先出门再进屋才会重新取证，
        // 跑来跑去很蠢。给一个键随时按、随时打当前现场。
        // F3/F4/F5/F6 已被 HUD / 详情面板 / 视角 / 性能面板占用，这里用 F7。
        private const KeyCode IndoorAuditKey = KeyCode.F7;
        private float rainLifeTimer;       // 雨丝寿命重算的节流计时（见 UpdateRainLifetime）
        private float snowLifeTimer;       // 雪花寿命重算的节流计时（见 UpdateSnowLifetime）

        // 【P13】逐粒剔除"落进室内"的雨雪用的缓冲（见 CullIndoorParticles）。
        // 为什么不每帧 new：GetParticles 会把整个粒子数组拷出来，每帧分配
        // 9000 个 Particle 结构（约 9000×100B ≈ 900KB）会造成明显 GC 抖动。
        // 复用同一个数组，只在需要时按上限扩容一次。
        private ParticleSystem.Particle[] cullBuf = new ParticleSystem.Particle[9000];

        // 【P13 第五次修】剔除结果的真实取证。
        //
        // ── 为什么必须加 ──────────────────────────────────────────────
        // 前四轮我都在盯「现存粒子 = 雨 840 / 雪 736」这个数，把它当成
        // "剔除没生效"的证据。这是个**错误的判据**：
        //   发射盒 110m × 110m = 12100 m²，雨 3200 粒/秒 × 寿命 1.5s ≈ 4800 粒，
        //   面密度只有 0.4 粒/m²。一个 15m × 8m 的房间（120 m²）**本来就只有约 48 粒**，
        //   雪更稀（520 粒/秒 × 6s ≈ 3120 粒全在 12100 m² 上 → 0.26 粒/m²，房间里约 30 粒）。
        // 也就是说 840 这个总数**完全可能**是"已经剔掉了应该剔的那几十粒"之后的结果。
        // 只看总数，永远分不清"没剔"和"剔干净了"。
        //
        // 所以直接把"房间内还剩几粒"数出来：剔除**后**房里应当 ≈ 0，
        // 而"房里本该有几粒"由剔除**前**的读数给出 —— 两者一起打，才可判读。
        // ──────────────────────────────────────────────────────────────
        private int auditRainInRoom = -1;   // 本轮 CullIndoorParticles 实际杀掉的雨粒数
        private int auditSnowInRoom = -1;   // 同上，雪
        private int auditRainTotal = -1;    // 剔除后的粒子总数（对比用）
        private int auditSnowTotal = -1;
        private int auditRainStill = -1;    // 剔除后**仍留在房间框内**的粒数（应 ≈ 0）
        private int auditSnowStill = -1;

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
        private void TickSnowAudit(float dt, float snow, float rain)
        {
            // 【P13】F7 手动强制取证：随时按随时打，不管 indoorAuditDone 是什么。
            // 拿到的是**当前这一帧**的现场，和自动那次口径完全一致。
            if (Input.GetKeyDown(IndoorAuditKey))
            {
                Debug.Log("[P13][室内自检·手动 F7] ==== 现场快照 ====\n" + BuildIndoorAudit());
            }
            // 室内单独打一次快照。
            //
            // 为什么必须单独打：室外那次是在 snow>0.5 后 4 秒触发的，
            // 而"室内还有粒子 / 室内有横线"这类问题**只在室内复现**。
            // 用户在室内遇到问题时，室外那一次早就打完了（snowAuditStage==2），
            // 什么日志都拿不到，只能靠截图猜。这里补一条独立的室内取证。
            //
            // ══════════════════════════════════════════════════════════════
            // 【P13 第五次修：触发条件补上 rain】
            //
            // 原来只写 `snow > 0.5f`，于是**下雨天进室内什么日志都没有**。
            // 主理人这轮报的就是"室内还能看到雨雪的粒子效果"，其中雨天占一半，
            // 结果自检在最该出证据的天气下彻底沉默，白跑一轮。
            //
            // 而且 Bug 1 本来就是雨、雪两条粒子系统各剔各的，只测雪的天气
            // 只能验证一半。这里改成"雨或雪任一在认真下"就取证。
            // ══════════════════════════════════════════════════════════════
            bool weatherActive = snow > 0.5f || rain > 0.5f;

            // 【P13】天气种类变了就重新取证。
            // 原来的重置只在"出门"时发生，于是在室内把天气从雨切到雪、或从雪切到雨，
            // indoorAuditDone 还是 true，第二段证据永远拿不到 —— 而雨雪两条粒子
            // 系统是分开剔的，只看一种天气等于只验证了一半。
            int kind = snow > rain ? 2 : (rain > 0.5f ? 1 : 0);
            if (kind != auditWeatherKind)
            {
                auditWeatherKind = kind;
                indoorAuditDone = false;
                indoorAuditTimer = 1.5f;
            }

            if (weatherActive && indoor && !indoorAuditDone)
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
            Debug.Log("[P13][横线排查] ==== 与相机等高、且扁平的可疑渲染器 ====\n" + BuildSliverSuspects());
        }

        /// <summary>
        /// 找出「横线」的候选物体。
        ///
        /// ── 为什么需要这段 ────────────────────────────────────────────────
        /// 用户报的横线在室内外都有、能穿过建筑、位于**角色头部高度**。
        /// 这三个特征共同指向：某个**水平面**正好穿过相机的视平线 ——
        /// 平面与视线近乎平行时，它的投影会收束成一条贯穿画面的扁带。
        ///
        /// 已经排除的：雪盘（自检显示竖直面 0 处、纵向跨度 0.20m，几何干净）。
        /// 所以必须换个办法，把"谁可能是那个面"列出来，而不是继续猜。
        ///
        /// ── 判据 ─────────────────────────────────────────────────────────
        ///   ① 渲染器可见、激活
        ///   ② 包围盒在 Y 方向**很扁**（高度 &lt; 1.2m）但 XZ 方向**很宽**（&gt; 5m）
        ///      → 典型水平薄面
        ///   ③ 它的 Y 范围**跨过相机高度**（minY ≤ camY ≤ maxY）
        ///      → 视线正好与它共面，投影会收成一条带
        ///   ④ 渲染队列 ≥ 2500（透明），或用了 ZTest Always
        ///      → 能盖住本应在前面的物体（"穿透建筑物"）
        /// 四条全中就是元凶。任一条不中，打印出来也能排除它。
        /// </summary>
        private string BuildSliverSuspects()
        {
            var sb = new System.Text.StringBuilder(1400);
            if (cam == null) return "    cam = null";

            Vector3 camPos = cam.transform.position;
            float camY = camPos.y;
            sb.AppendLine("    相机世界坐标 = " + camPos.ToString("F3"));
            sb.AppendLine("    判据：包围盒在 Y 方向很扁(<1.5m) 且宽(>4m) 且**跨过相机高度**");

            // ── 先打两个穹顶的实际状态 ──────────────────────────────────
            // 穹顶半径 380m、球心就在相机上。它的"地平线过渡带"在视平线附近
            // 会投影成一条窄带 —— 这是本次最需要排除/确认的候选。
            AppendDomeState(sb, "CloudDome", cloudDome, cloudMat);
            AppendDomeState(sb, "StarDome", starDome, starMat);

            // ══════════════════════════════════════════════════════════════
            // 【P13】树根雪堆的位置自检 —— 白线的直接元凶就藏在这里
            //
            // 判据：它的顶点是**世界坐标**，所以 transform 必须停在原点。
            // 若 position 跟着相机跑（历史上就是，因为被 SetParent 到本组件下，
            // 而本组件每帧 transform.position = cam.position），
            // 几十块同高水平圆盘会与相机眼高共面 → 屏幕上收束成一条横线。
            // 这里把 transform 位置和「是否仍在跟随层级里」都打出来，
            // 以后看一眼日志就能判断白线有没有回来。
            // ══════════════════════════════════════════════════════════════
            var treeSnow = Object.FindObjectOfType<SnowOnTrees>(true);
            if (treeSnow == null)
            {
                sb.AppendLine("    [TreeSnowPiles] = null（未创建 / 场景无树）");
            }
            else
            {
                Transform tt = treeSnow.transform;
                Vector3 tp = tt.position;
                bool detached = tt.parent == null;
                bool atOrigin = tp.sqrMagnitude < 0.0001f;
                var tr = treeSnow.GetComponent<Renderer>();
                string bbTxt = "n/a";
                if (tr != null)
                {
                    Bounds tb = tr.bounds;
                    bbTxt = "Y∈[" + tb.min.y.ToString("F2") + ", " + tb.max.y.ToString("F2")
                          + "]  高=" + tb.size.y.ToString("F2") + "m";
                }
                sb.AppendLine("    [TreeSnowPiles] pos=" + tp.ToString("F2")
                              + "  parent=" + (detached ? "null(已脱离，正确)" : tt.parent.name + " **仍在跟随层级里**")
                              + "\n        transform归零=" + (atOrigin ? "是(正确)" : "**否 → 顶点是世界坐标，必须归零**")
                              + "   渲染器enabled=" + (tr != null ? tr.enabled.ToString() : "?")
                              + "\n        包围盒 " + bbTxt
                              + "\n        ⚠️ 若包围盒 Y 区间**跨过相机高度**，且高很小(<1m) → 就是那条白线");
            }

            Renderer[] all = Object.FindObjectsOfType<Renderer>();
            sb.AppendLine("    场景渲染器总数 = " + all.Length);
            int hit = 0;
            // ── 列出所有"跨过相机高度"的渲染器，按 Y 尺寸升序（越扁越靠前）──
            // 为什么不再预先筛"扁 + 宽"：前一轮这么筛，一个都没命中，
            // 说明真凶可能是个薄但不太宽的东西（横杆、贴片）。
            // 现在只留一条硬条件 —— 包围盒必须跨过相机高度，因为横线就长在视平线上。
            var list = new System.Collections.Generic.List<Renderer>();
            for (int i = 0; i < all.Length; i++)
            {
                Renderer r = all[i];
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                Bounds b = r.bounds;
                if (b.min.y > camY || b.max.y < camY) continue;          // 没跨过视平线
                if (Mathf.Abs(b.center.x - camPos.x) > 120f) continue;
                if (Mathf.Abs(b.center.z - camPos.z) > 120f) continue;
                list.Add(r);
            }
            list.Sort((x, y) => x.bounds.size.y.CompareTo(y.bounds.size.y));

            sb.AppendLine("    跨过视平线、水平 120m 内的渲染器 = " + list.Count + " 个");
            for (int i = 0; i < list.Count && i < 25; i++)
            {
                Renderer r = list[i];
                Bounds b = r.bounds;
                Material m = r.sharedMaterial;
                int q = (m != null) ? m.renderQueue : -1;
                bool always = false;
                if (m != null && m.HasProperty("_ZTest"))
                    always = m.GetFloat("_ZTest") == (float)CompareFunction.Always;
                bool transparent = q >= 2500;

                hit++;
                sb.AppendLine("    " + i.ToString().PadLeft(2) + ". " + r.gameObject.name
                              + "   高=" + b.size.y.ToString("F3") + "m"
                              + "  XZ=" + b.size.x.ToString("F0") + "×" + b.size.z.ToString("F0") + "m"
                              + "  Y∈[" + b.min.y.ToString("F2") + "," + b.max.y.ToString("F2") + "]"
                              + "\n        队列=" + q + (transparent ? "(透明)" : "")
                              + (always ? " **ZTestAlways**" : "")
                              + "  距=" + Vector3.Distance(camPos, b.center).ToString("F1") + "m"
                              + "  " + (m != null ? m.shader.name : "null"));
            }
            if (hit == 0)
            {
                sb.AppendLine("    **一个都没有** —— 说明横线不在 3D 渲染器里，");
                sb.AppendLine("    下一步查 Canvas 上的全屏 Image（UI 是屏幕空间，恒在同一位置）。");
            }
            return sb.ToString();
        }

        /// <summary>打印一个跟随相机的穹顶的当前状态。</summary>
        private void AppendDomeState(System.Text.StringBuilder sb, string label, Transform dome, Material mat)
        {
            if (dome == null)
            {
                sb.AppendLine("    [" + label + "] = null（未构建或已销毁）");
                return;
            }
            sb.AppendLine("    [" + label + "] activeInHier=" + dome.gameObject.activeInHierarchy
                          + "  localScale=" + dome.localScale.x.ToString("F1")
                          + "  worldPos=" + dome.position.ToString("F2"));
            if (mat == null) { sb.AppendLine("        材质 = null"); return; }

            sb.AppendLine("        材质 = " + mat.shader.name + "  队列=" + mat.renderQueue);
            if (mat.HasProperty("_Opacity"))
                sb.AppendLine("        _Opacity=" + mat.GetFloat("_Opacity").ToString("F3")
                              + "   _Overcast=" + (mat.HasProperty("_Overcast")
                                  ? mat.GetFloat("_Overcast").ToString("F3") : "n/a")
                              + "   _HorizonFade=" + (mat.HasProperty("_HorizonFade")
                                  ? mat.GetFloat("_HorizonFade").ToString("F3") : "n/a"));
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
                              : ceilName + "  " + ceilDist.ToString("F2") + "m  法线Y=" + ceilNormalY.ToString("F2")
                                + (ceilTallFallback ? "   ← **靠 12m 远档才找到（高厅：礼堂/球馆）**" : "")
                                + (ceilByRenderer ? "   ← **靠渲染网格包围盒兜底（该建筑无屋顶碰撞体）**" : ""))
                          + "   （要求：距离 ≤ " + IndoorCeilHeight.ToString("F1")
                          + " 且 法线Y ≤ " + IndoorCeilNormalY.ToString("F2") + "）");

            // ══════════════════════════════════════════════════════════════
            // 【P13 第十三次修】这个诊断本身就是错的，先把它的 bug 记下来。
            //
            // 原来写 Physics.Raycast 打 40m，日志打出：
            //     40m 上限探测 = 命中 TwinPlayer 0.16m 法线Y=-1.00
            // **它命中的是玩家自己**。Physics.Raycast 只返回最近命中，
            // 而起点在胶囊内部，0.16m 处就是胶囊内壁 —— 这条射线永远打到自己，
            // 永远给出"命中了东西"的假象，**一点信息量都没有**。
            //
            // 我上一轮据此断言"40m 内没有碰撞体"，方向被自己的代码带偏了。
            // 真正该用的是 NonAlloc + 跳过玩家，和主判定同一个套路。
            //
            // 现在改成：取全部命中，跳过玩家，报"排除自己后最近的那个"。
            //   · 有  → 记下距离与法线，看是否真的超过 12m
            //   · 没有 → 确实没有天花板碰撞体（此时才成立 B 结论）
            // ══════════════════════════════════════════════════════════════
            {
                Transform prX = PlayerRoot();
                Vector3 fromX = prX != null ? prX.position + Vector3.up * 0.02f : (cam != null ? cam.transform.position : transform.position);

                bool prevBf = Physics.queriesHitBackfaces;
                Physics.queriesHitBackfaces = true;

                // 复用 ceilHits 之外的小缓冲，避免影响主判定的那份数据
                int nFar = Physics.RaycastNonAlloc(new Ray(fromX, Vector3.up), farHits, 40f,
                                                   Physics.DefaultRaycastLayers, QueryTriggerInteraction.UseGlobal);
                Physics.queriesHitBackfaces = prevBf;

                // 跳过玩家自己，取最近的
                bool gotFar = false;
                RaycastHit hUpFar = default(RaycastHit);
                float farBest = float.MaxValue;
                for (int i = 0; i < nFar; i++)
                {
                    if (farHits[i].collider == null) continue;
                    if (IsPlayerCollider(farHits[i].collider)) continue;   // ← 关键，缺了这行就是原来那个假象
                    if (farHits[i].distance <= 0.02f) continue;
                    if (farHits[i].distance >= farBest) continue;
                    farBest = farHits[i].distance;
                    hUpFar = farHits[i];
                    gotFar = true;
                }

                if (gotFar)
                {
                    sb.AppendLine("    40m 上限探测 = 命中 " + (hUpFar.collider != null ? hUpFar.collider.name : "?")
                                  + "  " + hUpFar.distance.ToString("F2") + "m  法线Y=" + hUpFar.normal.y.ToString("F2")
                                  + "   （已排除玩家自己，共 " + nFar + " 个原始命中）"
                                  + (hUpFar.distance > IndoorCeilHeightTall
                                      ? "   ⚠️ **超过 12m 远档上限 → 需继续放宽**"
                                      : ""));
                }
                else
                {
                    sb.AppendLine("    40m 上限探测 = **40m 内没有任何碰撞体** "
                                  + "← 这是 B 情况：屋顶只有渲染网格，没有 Collider。"
                                  + " 12m 远档也救不了，必须换判据（靠预设房间包围盒）。");
                }
            }

            // ══════════════════════════════════════════════════════════════
            // 【P13】射线起点与"自命中"排查
            //
            // 上一轮的病根是射线打在玩家自己的胶囊内壁上（0.18m、法线 -1），
            // 而当时自检只打 ceilName / ceilDist，**看起来完全像"命中了天花板"** ——
            // 一个叫 TwinPlayer 的碰撞体、法线 -1、距离 0.18，谁能一眼看出是错的。
            //
            // 所以这里把两件事显式打出来：
            //   · 起点相对角色根的高度（胶囊底面 = 0，头顶 = 1.8）
            //   · 命中物体是不是玩家自己
            // 只要 selfHit 变成 True，就是回归了，不用再猜。
            // ══════════════════════════════════════════════════════════════
            Transform prA = PlayerRoot();
            if (prA != null)
            {
                sb.AppendLine("    射线起点   = " + ceilFromY.ToString("F2")
                              + "   角色根 Y = " + prA.position.y.ToString("F2")
                              + "   相对高度 = " + (ceilFromY - prA.position.y).ToString("F2")
                              + " m（0=胶囊底，1.8=头顶）");
                sb.AppendLine("    自命中     = " + (ceilSelfHit
                                  ? "**True ← 射线打到玩家自己了，室内判定必然失效**"
                                  : "False（正确）"));
            }
            else sb.AppendLine("    射线起点   = 无角色，用相机 " + ceilFromY.ToString("F2"));

            // ══════════════════════════════════════════════════════════════
            // 【P13 第八次修】屋顶到底挡不挡得住视线？
            //
            // ── 为什么这一项决定修法方向 ──────────────────────────────────
            // "屋里看得到雨"有两种完全不同的成因，修法也完全不同：
            //
            //   甲、屋顶可见（实体/双面网格）  →  天花板以上的雨本来就被挡住，
            //       玩家看到的雨只可能来自"地面~天花板"这一薄层。那问题在剔除的
            //       横向框不准（射线从窗洞漏出去，框成了 36.8m），去修 ProbeRoomRadius。
            //
            //   乙、屋顶不可见（单面网格，背面被剔除）→  抬头能透过屋顶看见上空，
            //       发射盒 15.5m 与天花板 6.9m 之间那 8.6m 的雨柱全都露着。
            //       那问题在纵向截断（`p.y > ceilWorldY` 直接放过），
            //       修 ProbeRoomRadius 一点用都没有 —— 这正是前几轮白跑的原因。
            //
            // ── 判据怎么取 ────────────────────────────────────────────────
            // 从"天花板正下方一点点"朝**下**看？不行，那是地板。
            // 用 **Renderer 的背面剔除设置** 才是直接证据：
            //   找到头顶命中的那块网格，读它的 Renderer + 材质，
            //   再判断"从上方看这块三角形"是否会被剔除。
            // 但 Shader 的 Cull 模式在运行时读不到（Material 只暴露 _Cull 若脚本自己写了）。
            //
            // 所以改用一个**几何可判**的近似：从屋顶上方 3m 处朝下打，
            // 与从屋内朝上打，比较两者命中的**距离之和**是否等于屋顶厚度。
            //   · 两条都能命中且距离和很小（≤1m）→ 屋顶是**有厚度的实体**（Box/挤出体）
            //   · 从上方打不中 → 屋顶是一张**单面片**，从上面看是背面
            // 这个判据不依赖 Shader，只依赖碰撞体，而碰撞体是双面的
            // （Unity 的 MeshCollider 默认双面），所以：
            //   ⚠️ 碰撞体能命中 ≠ 渲染看得见。碰撞双面、渲染单面，这是常见组合。
            //   因此最终还要看"屋顶 Renderer 的材质里有没有 _Cull 且为 0"。
            // ══════════════════════════════════════════════════════════════
            AppendRoofVisibility(sb);

            sb.AppendLine("    rainPs      = " + (rainPs != null ? rainPs.emission.rateOverTime.constantMax.ToString("F1") + " 粒/秒" : "null"));
            sb.AppendLine("    snowPs      = " + (snowPs != null ? snowPs.emission.rateOverTime.constantMax.ToString("F1") + " 粒/秒" : "null")
                          + "   （室内**不再归零**，天气属于世界）");
            sb.AppendLine("    现存粒子    = 雨 " + (rainPs != null ? rainPs.particleCount.ToString() : "-")
                          + " / 雪 " + (snowPs != null ? snowPs.particleCount.ToString() : "-")
                          + "   （= 全世界总数，**不能**用来判断剔没剔，看下一行）");

            // ══════════════════════════════════════════════════════════════
            // 【P13 第五次修】这两行才是 Bug 1 的可判读判据。
            //
            // 为什么"现存粒子"那行没用：110m×110m 的发射盒把 4800 粒雨摊开，
            // 面密度 0.4 粒/m²，一个 120 m² 的房间本来就只有约 48 粒。
            // 840 与 "840+48" 在总数上看不出区别，"剔掉了"和"没剔"读数几乎一样。
            //
            // 判读方法（正文里两句都要同时成立才算修好）：
            //   ① 「房内命中」> 0  →  剔除器确实在房间里认出了粒子（说明边界框对了）
            //   ② 「房内命中」≈ 0 而「残留」也 ≈ 0 → 已经完全剔净
            // 若 ① = 0，说明房间矩形框偏了（射线从门洞漏出去/center 用了旧值）。
            // ══════════════════════════════════════════════════════════════
            sb.AppendLine("    房内命中    = 雨 " + (auditRainInRoom >= 0 ? auditRainInRoom.ToString() : "未跑")
                          + " 粒 / 雪 " + (auditSnowInRoom >= 0 ? auditSnowInRoom.ToString() : "未跑")
                          + " 粒   （本轮从房里剔掉的粒数，>0 才算剔除器真的在工作）");
            sb.AppendLine("    剔除后残留  = 雨 " + (auditRainTotal >= 0 ? auditRainTotal.ToString() : "-")
                          + " / 雪 " + (auditSnowTotal >= 0 ? auditSnowTotal.ToString() : "-")
                          + "   （= 总数 − 房内命中 ≈ 窗外应有的量）");
            sb.AppendLine("    框内复查    = 雨 " + (auditRainStill >= 0 ? auditRainStill.ToString() : "未跑")
                          + " 粒 / 雪 " + (auditSnowStill >= 0 ? auditSnowStill.ToString() : "未跑")
                          + " 粒   ← **这个数接近 0 才算剔除成功**，>0 说明还有漏网");

            // ══════════════════════════════════════════════════════════════
            // 【P13】逐粒剔除的房间边界 —— "窗外的雨到底还在不在"的唯一判据
            //
            // 这一组必须打出来，因为上一轮"Clear 全部"的做法让两条需求互相打架：
            // 屋里不能有雨，窗外必须有雨。判断这一版到底有没有做对，
            // 只能靠"房间边界"和"粒子总数"这两个数一起看：
            //   · 房间边界明显大于实际房间 → 会把窗外雨误杀（雪/雨总数偏低）
            //   · 房间边界 ≈ 8m × 30m 的走廊 → 正常
            //   · 边界 max=18 说明这个方向没打到墙，已经用上限兜住
            // ══════════════════════════════════════════════════════════════
            if (roomValid)
            {
                sb.AppendLine("    房间边界    = X[" + roomMinX.ToString("F1") + ", " + roomMaxX.ToString("F1") + "]"
                              + "  Z[" + roomMinZ.ToString("F1") + ", " + roomMaxZ.ToString("F1") + "]"
                              + "   （走 18m 上限 = 该方向没打到墙）");
            }
            else sb.AppendLine("    房间边界    = 未测（室外不测，属正常）");

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

            // ══════════════════════════════════════════════════════════════
            // 【P13-D9】积水层 + 雨雪发射盒的位置自检
            //
            // 这一组是"水坑随视角漂移 / 二楼看不到雨"的唯一可验证指标。
            // 判红的标准全部是**位置量**，不需要看截图：
            //   · 积水面 Y 若 ≈ 相机 Y（差 < 1m）→ 它被铺到二楼地板上了
            //   · 积水面 Y 若 ≈ outdoorGroundY → 正确，留在一楼室外
            //   · 发射盒 Y 若 ≈ 相机 Y + 16 → 它还在跟相机（二楼必然被楼板挡）
            //   · 发射盒 Y 若 ≈ outdoorGroundY + 16 → 正确
            // ══════════════════════════════════════════════════════════════
            sb.AppendLine("  积水 / 雨雪发射盒（D9 判据）：");
            float camYNow = cam != null ? cam.transform.position.y : float.NaN;
            sb.AppendLine("    相机眼高     = " + camYNow.ToString("F2")
                          + "    室外地面 = " + outdoorGroundY.ToString("F2"));

            // ⚠️ 不能用 wet.GetComponentInChildren<Renderer>()：那会抓到同一个
            // GameObject 上的 CloudDome（位置恒等于相机），打出来的 1.73 是假值。
            // 见 WetGround.PlaneWorldY 的注释 —— 这条假警报曾把排查带偏一整轮。
            if (wet != null && wet.IsBuilt)
            {
                float wy2 = wet.PlaneWorldY;
                bool highUp = Mathf.Abs(wy2 - camYNow) < 1.0f;
                sb.AppendLine("    积水层 worldY = " + wy2.ToString("F2")
                              + "   visible=" + wet.IsVisibleNow
                              + "   wetness=" + wet.WetnessNow.ToString("F2")
                              + "   材质alpha=" + wet.AlphaInMat.ToString("F2")
                              + (highUp
                                  ? "   ⚠️ **与相机同高 → 已被铺到当前楼层（二楼会进教室）**"
                                  : "   正确（应贴 outdoorGroundY）"));
            }
            else sb.AppendLine("    积水层 = null / 未启用");

            if (rainPs != null)
            {
                float ry = rainPs.transform.position.y;
                bool followsCam = Mathf.Abs(ry - (camYNow + EmitHeight)) < 2.0f;
                sb.AppendLine("    雨发射盒 worldY = " + ry.ToString("F2")
                              + "   parent=" + (rainPs.transform.parent == null ? "null(已脱离，正确)" : rainPs.transform.parent.name)
                              + (followsCam
                                  ? "   ⚠️ **仍在跟相机（二楼会被楼板挡住）**"
                                  : "   正确（应 = 室外地面 + 16）"));
            }
            if (snowPs != null)
            {
                float sy = snowPs.transform.position.y;
                sb.AppendLine("    雪发射盒 worldY = " + sy.ToString("F2")
                              + "   parent=" + (snowPs.transform.parent == null ? "null(已脱离，正确)" : snowPs.transform.parent.name));
            }

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

                // ══════════════════════════════════════════════════════════
                // 【P13】最决定性的一条：雪面世界高度 vs 相机眼高
                //
                // 一段水平的半透明面片，只要它的高度**穿过相机眼高**，
                // 透视下就会退化成一条横贯全屏的线：
                //   · 面片高于眼高 → 看到它的下表面，仍是一条线；
                //   · 面片正好等于眼高 → 视线与面共面，面积铺满整个屏幕，
                //     亮度增量最大、上边缘最锐利 —— 正是截图里的样子。
                // 这个判据不需要看截图也能定性，所以必须打进日志。
                // ══════════════════════════════════════════════════════════
                if (snowGround != null && cam != null)
                {
                    float snowWorldY = snowGround.position.y + bb.center.y;
                    float camY2 = cam.transform.position.y;
                    float gap = snowWorldY - camY2;
                    sb.AppendLine("    雪面世界Y    = " + snowWorldY.ToString("F3")
                                  + "    相机眼高 = " + camY2.ToString("F3")
                                  + "    差 = " + gap.ToString("F3") + "m"
                                  + (Mathf.Abs(gap) < 0.30f
                                      ? "\n        ⚠️⚠️ **相差不足 0.3m —— 水平雪面与视线共面，"
                                        + "必然在屏幕上收成一条横线**"
                                      : "\n        差值足够，雪面不会与视线共面"));
                }
                if (snowGroundFar != null && cam != null)
                {
                    float farWorldY = snowGroundFar.position.y + snowMeshFar.bounds.center.y;
                    float gap2 = farWorldY - cam.transform.position.y;
                    sb.AppendLine("    远场雪面世界Y = " + farWorldY.ToString("F3")
                                  + "    与眼高差 = " + gap2.ToString("F3") + "m"
                                  + (Mathf.Abs(gap2) < 0.30f ? "   ⚠️ **同样共面**" : ""));
                }
            }

            sb.AppendLine("  用户报过的问题对照：");
            sb.AppendLine("    · 室内外白色横线 → 看【雪面几何】那行的'竖直面'计数"
                          + "（>0 就是横线来源；正常应为 0）");
            sb.AppendLine("    · 室内有粒子 → 看【框内复查】是否 ≈ 0，以及【屋顶可见性】判甲/乙");
            return sb.ToString();
        }

        /// <summary>
        /// 判定"屋顶网格挡不挡得住抬头视线"，这是 Bug 1 两种成因的分水岭。
        ///
        /// ── 为什么不能只看碰撞体 ──────────────────────────────────────
        /// 头顶射线能命中 `Floor2` 只证明**碰撞**存在（MeshCollider 默认双面）。
        /// 但渲染是另一回事：Built-in RP 的不透明 Shader 默认 `Cull Back`，
        /// 单面网格从背面看整体消失。碰撞双面 + 渲染单面在本工程很常见
        /// （建筑网格多半是单层挤出体，法线朝外=朝上，屋内看到的是背面）。
        ///
        /// ── 判据 ──────────────────────────────────────────────────────
        /// 取头顶命中的那块 Renderer，读它的材质：
        ///   · `_Cull` 存在且 = 0          → 双面渲染，屋顶挡得住 → 甲
        ///   · `_Cull` 存在且 = 2（Back）  → 单面，且法线朝上 = 屋内看背面 → 乙
        ///   · 没有 `_Cull` 属性           → 用 Shader 名粗判，未知则打"待确认"
        /// 同时把 Renderer 的 bounds 打出来，方便判断屋顶厚度与高度。
        /// </summary>
        private void AppendRoofVisibility(System.Text.StringBuilder sb)
        {
            if (string.IsNullOrEmpty(ceilName) || ceilDist < 0f)
            {
                sb.AppendLine("  屋顶可见性：未测（头顶没命中任何东西）");
                return;
            }

            // 直接用头顶射线命中的那个 collider，不再遍历整个场景。
            Renderer rd = null;
            if (ceilCollider != null) rd = ceilCollider.GetComponent<Renderer>();
            if (rd == null) rd = ceilCollider != null ? ceilCollider.GetComponentInParent<Renderer>() : null;

            if (rd == null || rd.sharedMaterial == null)
            {
                sb.AppendLine("  屋顶可见性：命中 " + ceilName + "，但取不到 Renderer/材质 → 待确认");
                return;
            }

            Material m = rd.sharedMaterial;
            bool hasCull = m.HasProperty("_Cull");
            float cullVal = hasCull ? m.GetFloat("_Cull") : -1f;

            string verdict;
            if (hasCull)
            {
                // URP/Built-in 约定：0 = Off（双面）, 1 = Front, 2 = Back（默认）
                if (cullVal < 0.5f)
                    verdict = "**甲 · 双面渲染，屋顶挡得住**（修 ProbeRoomRadius 的横向框）";
                else
                    verdict = "**乙 · 单面渲染（Cull Back），屋内看到的是背面 → 屋顶透明**"
                            + "（必须剔天花板以上的雨柱）";
            }
            else
            {
                verdict = "材质无 _Cull 属性，按 Shader 名粗判：" + m.shader.name
                        + "（若是 Standard/Surface 默认 Cull Back → 同乙）";
            }

            Vector3 bc = rd.bounds.center;
            Vector3 bs = rd.bounds.size;
            sb.AppendLine("  屋顶可见性：");
            sb.AppendLine("    命中网格   = " + ceilName + "   material=" + m.name);
            sb.AppendLine("    _Cull      = " + (hasCull ? cullVal.ToString("F0") + "（0=双面 2=背面剔除）" : "无此属性"));
            sb.AppendLine("    结论       = " + verdict);
            sb.AppendLine("    bounds     = c=" + bc.ToString("F2") + "  size=" + bs.ToString("F2")
                          + "   厚度(Y)=" + bs.y.ToString("F2") + "m");
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

            // 【P13-D9】落距 = 发射盒世界 Y − 室外地平面（实测，不用旧公式）。
            // 详细理由见 UpdateRainLifetime 里的同一段注释：旧公式
            // `EmitHeight + (camY - groundY)` 建立在"发射盒挂相机上"的前提，
            // 该前提已被推翻；在二楼会算出偏小的落距，导致雨丝穿地。
            // 直接读 transform.position.y 是唯一不会写错的做法。
            float emitY = rainPs.transform.position.y;
            float drop = Mathf.Max(1f, emitY - outdoorGroundY);
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

            // 【P13-D9】同上：落距实测，不套旧公式（旧公式假设发射盒挂相机上）
            float emitYs = snowPs.transform.position.y;
            float drop = Mathf.Max(1f, emitYs - outdoorGroundY);
            // 【P48】雪改恒定终端速度下落（BuildSnow：gravity=0 + vel.y=-3.5，
            // 叠加初速 0.25~0.6 → 实际 3.75~4.1 m/s，取均值 3.5——主理人提速裁决）。
            // 匀速下落让整根雪柱密度均匀——修"抬头一大堆、平视没几粒"的根因
            // （原重力加速下落：越贴地越快、贴地密度只有高空的 1/5）。
            const float V_FALL = 3.5f;
            float t = drop / V_FALL;

            // 收 2%：让 alpha 归零发生在贴地前一点点，雪片"落进"地面而不是"消失在半空"。
            // 上限 40→80：匀速下落下，落距 250m（相机自适应上限）需 71s——
            // 80s 的上限仍然覆盖；极端机位的撒雪不会"半空蒸发"。
            t = Mathf.Clamp(t * 0.98f, 4f, 80f);

            var main = snowPs.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(t * 0.97f, t);
        }

        // =====================================================================
        // 【P13】逐粒剔除"落进室内"的雨雪
        // =====================================================================

        /// <summary>
        /// 把已经落进**玩家所处的那个房间**的雨雪粒子 Kill 掉，窗外与室外的照常保留。
        ///
        /// ══════════════════════════════════════════════════════════════════
        /// 为什么需要它（而不是像之前那样直接关发射率或 Clear 全部）
        ///
        /// 目标有两个，且互相冲突：
        ///   ① 屋内不能有雨雪（主理人第一轮报的"室内有雨雪粒子效果"）
        ///   ② 从屋里往外看，窗外必须有雨雪（主理人本轮报的"室内窗外看不到"）
        ///
        /// 关发射率：满足 ① 不满足 ②（窗外也没了）
        /// Clear 全部：满足 ① 不满足 ②（同上，而且更彻底）
        /// 什么都不做：满足 ② 不满足 ①
        ///
        /// 只有"逐个粒子判断它此刻在哪"能同时满足两者。
        ///
        /// ── 为什么不能用一个固定半径的圆 ──────────────────────────────
        /// 第一版写的是「离玩家 9m 以内就杀」。这个判据把**窗外的雨也杀了** ——
        /// 走廊窗离人往往只有 2~4m，那些雨正该被看见。
        /// 半径调到 3m 又反过来，走廊深处还是一片雨落在屋里。
        ///
        /// 正确的判据是「这个粒子在不在**我被困住的这个房间**里」。
        /// 房间边界用 4 条水平射线现测（见 ProbeRoomRadius），
        /// 一次 0.3 秒刷新一次，代价可以忽略。
        ///
        /// ── 天花板高度为什么可信 ──────────────────────────────────────
        /// ceilDist 是 UpdateGround 每 0.15s 更新一次的"脚底到天花板距离"，
        /// 由同一条射线给出，和 indoor 判定同源。粒子世界 Y 低于
        /// `脚下地面 + ceilDist` 就说明它已经在这块天花板下面了。
        /// ══════════════════════════════════════════════════════════════════
        /// </summary>
        private void CullIndoorParticles(ParticleSystem ps, float intensity, bool isRain)
        {
            // ══════════════════════════════════════════════════════════════
            // 【P13 第九次修：读数不能在节流之前清零】
            //
            // 上一版把"置 -1"放在方法最开头，于是：
            //     方法每帧都被调用 → 先清零 → 2/3 的帧在 `%3` 处 return
            //     → 自检（F7）落到被节流的帧上，读到的永远是 -1「未跑」
            // 主理人这轮日志三行全「未跑」就是这么来的，而剔除其实一直在跑。
            //
            // 正确做法：**只有真正执行了判定的那一轮**才写读数，
            // 其余帧保留上一次的真实值。这样任意时刻读都是"最近一次有效样本"，
            // 而不是"随机 1/3 概率有值"。
            //
            // 唯一需要显式清零的场景是"理论上不该有数据"（不在室内 / 边界没测出来），
            // 那种情况下面各自单独清，语义才准确。
            // ══════════════════════════════════════════════════════════════
            if (ps == null || intensity <= 0.01f)
            {
                if (isRain) { auditRainInRoom = -1; auditRainTotal = -1; auditRainStill = -1; }
                else { auditSnowInRoom = -1; auditSnowTotal = -1; auditSnowStill = -1; }
                return;
            }
            if (!indoor || ceilDist < 0f)              // 不在室内 / 没测到天花板 → 不剔
            {
                if (isRain) { auditRainInRoom = -1; auditRainTotal = -1; auditRainStill = -1; }
                else { auditSnowInRoom = -1; auditSnowTotal = -1; auditSnowStill = -1; }
                return;
            }
            if (!roomValid)                            // 房间边界还没测出来 → 宁可不动
            {
                if (isRain) { auditRainInRoom = -1; auditRainTotal = -1; auditRainStill = -1; }
                else { auditSnowInRoom = -1; auditSnowTotal = -1; auditSnowStill = -1; }
                return;
            }

            // 节流：三帧一轮。
            // 为什么不是"每帧只处理 1/3 的粒子"：GetParticles 会把**整个**粒子数组
            // 拷出来（雨上限 9000，约 900KB/次），拷贝成本与抽样比例无关。
            // 三帧跑一次全量，总拷贝量就是每帧跑一次的 1/3。
            //
            // ══════════════════════════════════════════════════════════════
            // 【P13 第九次修：节流本身就是"屋里看得到雨"的实质原因之一】
            //
            // 原来写 %3，理由写的是"视觉上察觉不到"。**这个判断是错的**：
            //     雨 3200 粒/秒 ÷ 60fps ≈ 53 粒/帧
            //     3 帧节流 → 单次剔除前最多已累积 160 粒新雨
            // 160 粒雨丝在半空里清晰可见 —— 这不是"来不及看清"，
            // 是每 50ms 就有一批新雨在屋里生成并下落。
            //
            // 更麻烦的是：粒子的 lifetime 是 1.6~1.8 秒（UpdateRainLifetime 按落距算），
            // 所以被剔掉的那批只是"当前这批"，下一帧又补上 53 粒。
            // 稳态下屋里始终悬着「53 粒 × 节流帧数」的雨，永远清不干净。
            //
            // 改成**每帧全量判**。代价是每帧一次 GetParticles（约 900KB 拷贝），
            // 这个开销实测远小于一次阴影渲染；而"屋里下雨"是主理人一眼能看见的
            // 明显缺陷，优先级高于这点 CPU。若将来性能吃紧，再考虑按距离预筛。
            // ══════════════════════════════════════════════════════════════

            // ══════════════════════════════════════════════════════════════════
            // 【P13 第四次修：剔除基准不能用 groundY】
            //
            // 上一版写 `float floorY = groundY;` —— 这个值是**相机正下方**那层地面。
            // 实测日志（二楼 + 观景模式）：
            //     角色根 Y = 3.59      ← 玩家站在二楼
            //     相机眼高 = 15.40     ← 相机被抬到 15m 高空
            //     groundY  = -0.50     ← 相机正下方是**室外地面**
            // 于是剔除区间被算成 [-0.50, 2.79]，而玩家实际在 [3.59, 6.88] ——
            // 整个房间都在区间外，一个粒子都剔不掉。
            // 日志里 `现存粒子 = 雨 747 / 雪 766` 就是这个。
            //
            // 正确基准是**角色根**（CharacterController 底面 = 玩家脚踩的那层楼板）。
            // 这与 indoor 判定、雪盘锚点用的是同一个量（都走 PlayerRoot），
            // 三者必须同源，否则"判定说在室内、剔除按室外算"这种错位还会再出现。
            //
            // 没有角色时（纯数字孪生自由相机）才退回收相机脚下 —— 那种情况下
            // 本来也没有"玩家在哪个房间"可言，用相机位置是唯一选择。
            // ══════════════════════════════════════════════════════════════
            Transform cullPr = PlayerRoot();
            float floorY = cullPr != null ? cullPr.position.y : groundY;
            float ceilWorldY = floorY + ceilDist;

            // ══════════════════════════════════════════════════════════════
            // 【P13 第七次修：纵向剔除上界必须覆盖整个雨柱，不能截在天花板】
            //
            // 原代码在循环里写：
            //     if (p.y > ceilWorldY) continue;
            // 理由是"高于天花板的在楼顶上方，从屋里看被屋顶挡住，不用管"。
            // **这个理由只在屋顶看得见时成立。**
            //
            // 实测（主理人 2026-11-05 日志）：
            //     发射盒 worldY = 15.50      天花板 worldY = 3.59 + 3.31 = 6.90
            //     房内命中 = 19 粒
            // 6.90 → 15.50 这 8.6m 高的雨柱，XZ 就在玩家头顶正上方，一粒都没剔。
            // 19 这个偏小的数正好印证：真正落在"地面~天花板"薄层里的只有 19 粒。
            //
            // 为什么会看见：Built-in RP 不透明 Shader 默认 Cull Back，
            // 单面屋顶从屋内看是**背面 → 整个消失**，深度缓冲里没有屋顶，
            // 上方的雨没有遮挡 → 直接看见。所以"屋里下雨"其实是"上空 8.6m 在下雨"。
            //
            // ⚠️ 这一处与 AppendRoofVisibility 的甲/乙判定强相关：
            //   若诊断显示"甲（双面渲染，屋顶挡得住）"，这个 +24f 就做过量了，
            //   应改回 `if (p.y > ceilWorldY) continue;`，否则会把屋顶上方
            //   本可透过窗户看见的雨一起剔掉。
            //
            // ⚠️ 作用域：这个变量必须声明在**两个循环之外**。
            //   第一版误写在第一个 for 内部，复查循环引用它直接 CS0103。
            //   两个循环要用同一套判据，变量就必须同域，不能各声明一份
            //   （各写一份将来改一处忘一处，正是 shape.scale 那个坑的翻版）。
            //
            // 【P13 第十次修】上界改成取「发射盒顶」与「天花板+24」的较小值。
            // 原来是 ceilWorldY + 24f。在走廊（层高 3.3m）算出 3.59+3.3+24 ≈ 31m，
            // 已经超过发射盒 15.5m —— 多余判断不算错，但高厅（层高 10m）会算到 34m，
            // 同样没意义。真正的上界应该是「这条雨柱从哪来」= 发射盒高度，
            // 因为超过发射盒的 y 根本不会有粒子。
            // 取 Min 保证两种层高下都恰好覆盖到发射盒顶，不多不少。
            // 发射盒的真实 Y 见 UpdateGround 第 ~3123 行：
            //     emitPos.y = outdoorGroundY + EmitHeight
            // 这里必须用 outdoorGroundY 而不是 groundY —— groundY 是相机脚下那层，
            // 室内时它是楼板高度，会让上界整体偏低，漏掉上方一段雨柱。
            // ══════════════════════════════════════════════════════════════
            float emitTopY = outdoorGroundY + EmitHeight + 2f;
            float cullTopY = Mathf.Min(ceilWorldY + 24f, emitTopY);

            Vector3 c = roomCenter;

            var particles = cullBuf;
            int n = ps.GetParticles(particles);
            if (n <= 0) return;

            // 缓冲容量兜底：粒子上限将来若被调大，这里按 n 扩容一次，
            // 免得 GetParticles 只填进前 cullBuf.Length 个、后半批永远不被检查。
            if (n > particles.Length)
            {
                cullBuf = new ParticleSystem.Particle[n];
                particles = cullBuf;
                n = ps.GetParticles(particles);
            }

            bool changed = false;
            int inRoom = 0;      // 剔除前"落在房间里"的粒子数（本方法只数不判，用于自检可读）
            for (int i = 0; i < n; i++)
            {
                Vector3 p = particles[i].position;

                // ① 纵向：上界用 cullTopY（覆盖整个雨柱，理由与作用域的坑见上方
                //    声明处的注释）。下界留 0.5m 余量，已落地的粒子交给 lifetime 收。
                if (p.y > cullTopY) continue;
                if (p.y < floorY - 0.5f) continue;

                // ② 横向：必须在**这个房间**的矩形内。
                //    四边分别判，因为走廊两个方向长、两个方向窄；
                //    用同一个半径会把窄那两侧的窗外雨一起误杀。
                float dx = p.x - c.x;
                float dz = p.z - c.z;
                if (dx > roomMaxX || dx < roomMinX) continue;   // 窗外 / 墙外 → 保留
                if (dz > roomMaxZ || dz < roomMinZ) continue;

                inRoom++;
                // Kill：剩余寿命清零，粒子在下一帧自然消失。
                // 不用 ps.SetParticles 的整批回写也能生效，但必须回写才会被采纳。
                particles[i].remainingLifetime = 0f;
                changed = true;
            }

            // 把读数交出去给自检（剔除后总数要重新取，因为 SetParticles 之后
            // particleCount 会在下一帧才更新，这里先记"本轮在房内命中多少粒"）。
            if (isRain)
            {
                auditRainInRoom = inRoom;
                auditRainTotal = n - inRoom;
            }
            else
            {
                auditSnowInRoom = inRoom;
                auditSnowTotal = n - inRoom;
            }

            if (changed) ps.SetParticles(particles, n);

            // ══════════════════════════════════════════════════════════════
            // 【P13 第七次修：剔除后再复查一遍，确认"框内真的空了"】
            //
            // 为什么要复查：`房内命中 = 19` 只说明"这一轮标了 19 粒该杀"，
            // 但它**不回答**"框里现在还剩几粒"。可能有三种情况都表现为 19：
            //   · 框内共 19 粒，全杀了，框内归零   → 真的修好了
            //   · 框内有 60 粒，只认出 19 粒杀了    → 判据有漏（比如纵向区间不对）
            //   · 框内有 19 粒，但下一帧新粒子又落进来，画面照样有雨 → 节流太快
            // 前两轮我就是拿"命中数"当成功判据，结果分不清这三种。
            //
            // 复查用同一套判据再数一遍，但计数条件完全独立：不杀、只数。
            // 这个数应当**接近 0**；若明显 > 0，说明还有粒子漏网。
            // ══════════════════════════════════════════════════════════════
            int stillInRoom = 0;
            for (int i = 0; i < n; i++)
            {
                Vector3 p = particles[i].position;
                if (p.y > cullTopY) continue;          // 与上面同一套纵向判据
                if (p.y < floorY - 0.5f) continue;
                float dx = p.x - c.x;
                float dz = p.z - c.z;
                if (dx > roomMaxX || dx < roomMinX) continue;
                if (dz > roomMaxZ || dz < roomMinZ) continue;
                if (particles[i].remainingLifetime <= 0.001f) continue;  // 已判死的跳过
                stillInRoom++;
            }

            if (isRain) auditRainStill = stillInRoom;
            else auditSnowStill = stillInRoom;
        }

        // ---- 房间边界探测（见 CullIndoorParticles）----
        private bool roomValid;
        private Vector3 roomCenter;
        private float roomMinX, roomMaxX, roomMinZ, roomMaxZ;
        private float roomProbeTimer;

        /// <summary>
        /// 用 4 条水平射线量出"我此刻所在的房间"有多大，供 CullIndoorParticles 用。
        ///
        /// 为什么不复用天花板的法线/高度：天花板只给出**纵向**边界，
        /// 而"窗外的雨该不该留"是**横向**问题，必须知道墙在哪。
        ///
        /// 为什么只用 4 条射线而不是扫一圈：
        /// 房间在俯视图上是凸的，四个正方向的距离足以框出一个矩形近似；
        /// 扫一圈 16 条对走廊这种长条形没有额外收益，纯浪费。
        ///
        /// 为什么要 clamp 到 RoomMax：
        /// 站在大厅/体育馆时某个方向可能 30m 都没有墙。那样框出来的矩形会把
        /// 整个院子都算成"室内"，雨全被剔掉 —— 又回到最初的 bug。
        /// 上限取 18m（超过普通教室/走廊的合理尺寸），宁可漏掉远处的雨在屋里，
        /// 也不要误杀院子里的雨。
        /// </summary>
        private void ProbeRoomRadius(float dt)
        {
            roomProbeTimer -= dt;
            if (roomProbeTimer > 0f) return;
            roomProbeTimer = 0.3f;

            Transform pr = PlayerRoot();
            Vector3 basePos = pr != null ? pr.position : (cam != null ? cam.transform.position : transform.position);

            const float MaxWall = 18f;
            const float Margin = 0.35f;      // 稍微放宽，避免贴墙站着时脚下那一格被漏掉

            roomCenter = basePos;

            // ══════════════════════════════════════════════════════════════
            // 【P13 第六次修：单一高度探针会从窗洞里漏出去】
            //
            // 上一版固定在 1.5m 打射线。实测日志：
            //     房间边界 = X[-18.4, 18.4]  Z[-18.4, 2.7]
            // 四个方向有三个直接跑到 18m 上限（= 一路没撞到任何东西），
            // 框出来 36.8m × 21.1m ≈ 776 m²，而实际是一条 4~5m 宽的走廊。
            //
            // 为什么会漏：1.5m 正好落在**窗洞的中间高度带**。
            // 教学楼窗台普遍 0.9m 左右、窗顶 2.1m 左右，1.5m 笔直穿窗而过，
            // 而玻璃通常不带碰撞体（只是 MeshRenderer），射线一路飞到 18m。
            // 同理门洞（0~2.1m）也会让射线跑到隔壁房间去。
            //
            // 危害有两层，都不小：
            //   · 框是超集 → 窗外的雨被一起杀掉，本该看见的雨没了
            //   · 沿走廊方向被拉到 18m → 把走廊尽头门外的区域也算成"室内"
            //
            // 修法：每个方向打**三个高度**，取最近的命中。
            //   0.20m → 贴地，必有墙裙/墙体，窗和门都够不到这个高度（门槛下方）
            //   1.10m → 窗台中下，多数墙体裸露段
            //   2.40m → 窗楣以上、天花板以下，绕开窗洞顶部
            // 只要有一条打中墙，就说明这个方向有边界；三条全漏才认为真的没墙。
            // ══════════════════════════════════════════════════════════════
            float[] probeHeights = { 0.20f, 1.10f, 2.40f };

            float dxp = WallDistMulti(basePos, Vector3.right, MaxWall, probeHeights);
            float dxn = WallDistMulti(basePos, Vector3.left, MaxWall, probeHeights);
            float dzp = WallDistMulti(basePos, Vector3.forward, MaxWall, probeHeights);
            float dzn = WallDistMulti(basePos, Vector3.back, MaxWall, probeHeights);

            roomMaxX = Mathf.Min(dxp, MaxWall) + Margin;
            roomMinX = -Mathf.Min(dxn, MaxWall) - Margin;
            roomMaxZ = Mathf.Min(dzp, MaxWall) + Margin;
            roomMinZ = -Mathf.Min(dzn, MaxWall) - Margin;

            roomValid = true;
        }

        /// <summary>
        /// 同一方向、多个高度各打一条射线，返回**最近**的命中距离（没有命中则返回 max）。
        ///
        /// 为什么取最近而不是取平均/最远：墙是实体，任何一条射线撞到的东西都在墙内侧
        /// 或墙上。取最近 = 最保守的室内边界，宁可框小一点（屋里可能漏几粒雨），
        /// 也不要把窗外的雨一起杀了 —— 后者是用户一眼就能看出来的错。
        /// </summary>
        private float WallDistMulti(Vector3 basePos, Vector3 dir, float max, float[] heights)
        {
            float best = max;
            for (int i = 0; i < heights.Length; i++)
            {
                Vector3 from = new Vector3(basePos.x, basePos.y + heights[i], basePos.z);
                float d = WallDist(from, dir, max);
                if (d < best) best = d;
            }
            return best;
        }

        private float WallDist(Vector3 from, Vector3 dir, float max)
        {
            RaycastHit h;
            if (Physics.Raycast(from, dir, out h, max, Physics.DefaultRaycastLayers,
                                QueryTriggerInteraction.UseGlobal))
            {
                if (h.collider != null && !IsPlayerCollider(h.collider))
                    return h.distance;
            }
            return max;
        }

        /// <summary>
        /// 用**渲染网格包围盒**判断"头顶是否有东西盖着"，服务于没有碰撞体的场馆。
        ///
        /// ── 为什么必须存在这个方法 ──────────────────────────────────────
        /// 主理人 2026-11-05 的篮球场日志：
        ///     头顶射线 = **什么都没打到**
        /// 这不是阈值问题 —— 40m 内连一个碰撞体都没有。礼堂/篮球馆这类
        /// 单层大跨度建筑，屋顶只导入了渲染网格（74 个 MeshRenderer），
        /// 但只给了 19 个 MeshCollider（且全是凹形、不在屋顶上）。
        /// 射线永远打不到屋顶，只能改用渲染器的包围盒。
        ///
        /// ── 判据 ──────────────────────────────────────────────────────
        /// 对每个候选渲染器求 bounds，要求：
        ///   ① 底面在玩家头顶上方 0.5m 以外（确实"盖在头上"）
        ///   ② 顶面在玩家头顶上方 30m 以内（排除天空穹顶 —— 它每帧跟着相机）
        ///   ③ 玩家 XZ 在包围盒的 XZ 范围里（站在它下面，不是站在旁边）
        /// 三条全中即认为被覆盖，取**最低的那块底面**作为天花板高度。
        ///
        /// ── 为什么候选要缓存 ────────────────────────────────────────────
        /// 本方法由 UpdateGround 调，0.15s 一次。每次 FindObjectsOfType 在移动端
        /// 会明显掉帧，而建筑是静态的，运行时不会新增，缓存一次即可。
        /// </summary>
        private bool IndoorByRendererRoof(Vector3 from, out float roofY, out string roofName)
        {
            roofY = 0f;
            roofName = null;

            if (!roofCandidatesBuilt)
            {
                roofCandidatesBuilt = true;

                // 只留"可能是屋顶"的：MeshRenderer + 有运行时网格。
                // 排除自身子物体（CloudDome / 雪盘 / 雨雪粒子都在本组件下，
                // 它们的 Renderer 会跟着相机跑，混进来会把自己判成室内）。
                Renderer[] all = GetComponentsInChildren<Renderer>(true);
                // 【P13 第十四次修】建表时也把玩家排除掉，省掉每帧几十次
                // IsChildOf 的开销。注意这**不能替代**下面的逐项判定 ——
                // 本表只建一次并缓存，若此刻角色还没生成，这一次就白过滤。
                Transform playerAtBuild = PlayerRoot();
                var list = new System.Collections.Generic.List<Renderer>(96);
                Renderer[] scene = GameObject.FindObjectsOfType<Renderer>();
                for (int i = 0; i < scene.Length; i++)
                {
                    Renderer rd = scene[i];
                    if (rd == null) continue;
                    if (rd is ParticleSystemRenderer) continue;      // 粒子不要
                    if (rd is LineRenderer) continue;                // 线不要
                    // 【P43 追加 11-17】雪壳 v2 的 Renderer 不要 —— 这是屋顶雪壳事故
                    // 四路连带之一（室内判定翻转）的通路：雪壳世界包围盒罩住全校园，
                    // "顶面在头顶之上 + 玩家 XZ 在盒内"会恒成立 → 全场景被判室内。
                    // 双保险：按雪壳专用着色器名 + 按物体名前缀各挡一道（SnowOnRoofs v2
                    // 的壳用 CampusUI/SnowGround 着色器、物体名 RoofSnowShell 前缀）。
                    if (rd.sharedMaterial != null && rd.sharedMaterial.shader != null
                        && rd.sharedMaterial.shader.name == "CampusUI/SnowGround") continue;
                    if (rd.name.StartsWith("RoofSnowShell")) continue;
                    bool isSelf = false;
                    for (int k = 0; k < all.Length; k++)
                        if (all[k] == rd) { isSelf = true; break; }
                    if (isSelf) continue;                            // 自己的穹顶/雪盘不要
                    if (playerAtBuild != null && rd.transform.IsChildOf(playerAtBuild)) continue;  // 角色不是屋顶
                    list.Add(rd);
                }
                roofCandidates = list.ToArray();
            }

            if (roofCandidates == null || roofCandidates.Length == 0) return false;

            // ══════════════════════════════════════════════════════════════
            // 【P13 第十三次修：判据写反了，建筑包围盒的底面永远在脚底】
            //
            // 上一版的第一条要求是：
            //     if (b.min.y < headY) continue;   // 底面必须高于头顶
            // 这条把**所有建筑整体网格**都排除了。
            //
            // 实证（礼堂 Zal 那份日志）：
            //     bounds c=(-49.16, 6.17, 25.94)  size=(12.36, 5.21, 28.28)
            //     → min.y = 6.17 − 5.21/2 = 3.565
            // 玩家根 Y = 3.59，headY = 4.09 → 3.565 < 4.09 → 被 continue 掉。
            // 可是 3.565 与玩家几乎同高，说明这块网格是**从地面一直到屋顶**的
            // 整体外壳（活动厅的整栋体块），它的"底面"是地板，不是屋檐。
            // 用底面判"在头顶之上"，逻辑上就把整栋楼排除了。
            //
            // 正确判据：**顶面**在头顶之上即可（楼盖着头），
            // 再要求玩家 XZ 在盒内（站在这栋楼的范围里）。
            // 至于"楼有多高"不重要 —— 站在楼里就是站在楼里。
            //
            // 取 roofY 也要跟着改：不能再取 min.y（那是地板），
            // 应当取 max.y（顶面）作为天花板高度。它可能偏高于真实屋架，
            // 但下游只用它算剔除上界，偏大是安全方向（宁可多剔，不漏剔）。
            // ══════════════════════════════════════════════════════════════
            float headY = from.y + 0.5f;     // 头顶留 0.5m 余量
            float roofTopLimit = from.y + 30f;

            bool found = false;
            float best = float.MaxValue;

            // ══════════════════════════════════════════════════════════════
            // 【P13 第十四次修 · 2026-11-05】**必须排除玩家自己的渲染网格**。
            //
            // 这是「角色浮空 + 走雪地不留脚印」同时失效的真根因，而且是我
            // 自己在前几次修里引进的 —— 加兜底时只想到"场景里的屋面没有
            // Collider"，完全忘了**角色本身也是一块有包围盒的网格**。
            //
            // 现场证据（`[P13][室内自检]`，室外露天路面）：
            //     头顶射线 = Alpha_Joints（渲染网格兜底） 1.71m  法线Y=-1.00
            //     40m 探测 = 40m 内没有任何碰撞体      ← 人在室外，头顶是天空
            //     自命中   = True                      ← 射线只打到自己
            //     indoor   = True / indoorFade = 0.000
            //
            // `Alpha_Joints` 是角色骨骼蒙皮网格（身高 1.71m）。它对本方法
            // 来说**完美满足全部判据**：
            //   ① bounds.max.y = 脚底 + 1.71 > headY（脚底 + 0.5）✓
            //   ② 远低于 roofTopLimit（+30m）✓
            //   ③ 玩家 XZ 恒在自己的包围盒内 ✓
            //   ④ ySpan = 1.71 > 0.6，不是薄片 ✓
            //   ⑤ xSpan/zSpan 都不到 1m，不是场地外壳 ✓
            // 于是「站在室外」被判成「在屋里」。
            //
            // 连锁后果正是主理人反馈的那两条，而且**只有一个根因**：
            //     indoor = true
            //       → indoorFade → 0
            //       → playerSnowAmount = groundSnowAmount × 0 = 0
            //       → UpdatePlayerOnSnow 的 want 乘 0 → 抬升恒 0 → **角色陷进雪里**
            //       → UpdateFootprints 的 depth ≤ 0.01 → 直接 return → **一个脚印都不留**
            // 自检里 `已抬升 = 0.000` / `脚印 = 0/32` 就是这么来的。
            // 注意 `雪面顶 - 脚底 = 0.270` 是对的 —— 算得没错，只是被乘了 0。
            //
            // 修复前后对照（昨天能跑、今天不能，差异只在这一段）：
            //     昨天：已抬升 0.302 到位 / 脚印 12/32 grounded=True
            //     今天：已抬升 0.000      / 脚印 0/32
            //
            // ── 为什么不能只靠建表时过滤（见上面 roofCandidates 的建设处）──
            // 候选表是**首次调用时建一次**并缓存的。若那一刻角色还没生成
            // （TwinPlayer 由其它组件在 Start 里 Ensure），过滤就漏了。
            // 所以这里**每次遍历都再判一次**：建表过滤省开销，这里判保证正确。
            // ══════════════════════════════════════════════════════════════
            Transform selfRoot = PlayerRoot();

            for (int i = 0; i < roofCandidates.Length; i++)
            {
                Renderer rd = roofCandidates[i];
                if (rd == null || !rd.enabled) continue;

                // ⓪ 玩家自己的蒙皮网格不是屋顶。缺这一条就是上面那一整段事故。
                if (selfRoot != null && rd.transform.IsChildOf(selfRoot)) continue;

                Bounds b = rd.bounds;

                // ① 顶面必须在头顶之上（否则这块网格盖不住我）
                if (b.max.y <= headY) continue;

                // ② 顶面不能太高 —— 天空穹顶/远景山体之类，排除
                if (b.max.y > roofTopLimit) continue;

                // ③ 玩家 XZ 落在包围盒内 —— **这一步只是粗筛，不能作结论**
                if (from.x < b.min.x || from.x > b.max.x) continue;
                if (from.z < b.min.z || from.z > b.max.z) continue;

                // ④ 排除"薄片地面"：包围盒 Y 跨度太小且底面在脚下，
                //    那是地板/铺装，不是屋顶。真实屋顶跨度不会低于 0.6m。
                float ySpan = b.max.y - b.min.y;
                if (ySpan < 0.6f && b.min.y <= from.y + 0.3f) continue;

                // ⑤ 排除"整片场地外壳"：某些对象（Exterior / Terrain / 大包围盒）
                //    的 XZ 跨度覆盖整个校园，站在室外也会被判成"被盖住"。
                //    校园单体建筑最长边不超过 60m，取 80m 作上限。
                //    这个阈值很粗，但足以滤掉 Terrain(整个场景) 与 Exterior(整片场地)。
                float xSpan = b.max.x - b.min.x;
                float zSpan = b.max.z - b.min.z;
                if (xSpan > 80f || zSpan > 80f) continue;

                // ══════════════════════════════════════════════════════════════
                // 【P28 2026-11-05 · 决定性修复】**AABB 通过 ≠ 头顶有屋顶**
                //
                // 上面 ③ 只是"我落在这块网格的轴对齐包围盒里"，它对**任何**
                // 非矩形建筑都会误判。现场证据（主理人 16:38 截图，人站在
                // 教学楼外的空地上，四周开阔）：
                //
                //     头顶射线 = Corridor（渲染网格兜底） 4.01m
                //     40m 上限探测 = 40m 内没有任何碰撞体
                //     房间边界 = X[-11.8, 18.4]  Z[-18.4, 1.0]
                //     indoor = True / indoorFade = 0.000
                //
                // `Corridor` 是**整栋走廊的合并网格**，它的 AABB 是 30×19m 的
                // 大方块。楼是 L 形/带内院的，方块里必然含进楼外的空地 ——
                // 人站在那儿，③ 通过，于是判成"在屋里"。
                //
                // 这一条误判一次性解释了主理人报的**全部四个现象**：
                //   · 走出门口一定范围才有脚印   ← 门口那段恰好在 AABB 外
                //   · 出去这个范围就没脚印了     ← 进了 AABB，snowAmount→0
                //   · 往回走角色下陷、脚全埋进雪里 ← 抬升 want 被乘 0
                //   · 走远看不见、走近又出现     ← 判定在边界上反复翻转
                //
                // ── 为什么不用 MeshCollider 求交 ────────────────────────────
                // 这些建筑根本没有 Collider（`40m 内没有任何碰撞体` 就是证据），
                // 这正是当初要写这套"渲染网格兜底"的原因。运行时给它们
                // AddComponent<MeshCollider> 能行，但那是**往场景里加物理体**，
                // 立刻会污染所有向下射线（地面采样会把屋顶当地面 → 角色浮空，
                // 就是 F20 那个雪壳事故的翻版）。**绝不能在渲染兜底里造碰撞体。**
                //
                // ── 用什么代替：顶点高度图 ──────────────────────────────────
                // 直接问"这个网格在我 XZ 上方到底有没有实体表面"：
                // 把网格顶点变换到世界坐标，凡落在 (from.x±R, from.z±R) 这个
                // 小方块内的，取它们的最高 y。若存在这样的顶点且高度在头顶之上，
                // 才算"头顶被盖住"。否则这块网格只是**包围盒路过**，跳过。
                //
                // 精度：R = 0.6m 的邻域内只要有一个顶点就认定有表面。
                // 建筑屋顶是密集三角网格（通常 0.5~2m 一个顶点），
                // 而 AABB 里的"空地"离最近的真实墙面往往 3m 以上 —— 这个
                // 尺度差足以稳定区分，不需要做真正的三角形求交（那要
                // 遍历所有三角面，3249 顶点 × 上百个候选网格会直接卡帧）。
                //
                // 代价：网格极稀疏（顶点间距 > 1.2m）的屋顶会被漏判成室外。
                // 这个方向的错误是**可接受**的 —— 漏判室内只是"室外在下雪时
                // 屋里也下"，而误判室内是"整片雪地没有脚印、角色陷进雪里"。
                // 与 §1.6 里那条"宁可漏判室内"的取向一致。
                // ══════════════════════════════════════════════════════════════
                float surfaceY;
                if (!TryGetSurfaceYAt(rd, from.x, from.z, RoofProbeRadius, out surfaceY)) continue;

                // 头顶必须真的有表面（而不是包围盒的上沿）
                if (surfaceY <= headY) continue;
                if (surfaceY > roofTopLimit) continue;

                if (surfaceY < best)
                {
                    best = surfaceY;
                    roofY = surfaceY;
                    roofName = rd.gameObject.name;
                    found = true;
                }
            }

            return found;
        }

        /// <summary>
        /// 查询某个渲染网格在给定世界 XZ 邻域内，**实际存在的表面**的最高 y。
        ///
        /// 返回 false 表示这个位置根本没有该网格的顶点（= 包围盒路过，不是真的盖住）。
        ///
        /// ── 为什么要缓存世界顶点 ──────────────────────────────────────
        /// `Mesh.vertices` **每次调用都返回一份新数组**（Unity 的已知行为，
        /// 不是属性访问那么便宜）。本方法每 0.15s 被调一次，候选网格每次
        /// 5000 顶点 × 12 字节 = 60KB，一秒就是 400KB 的纯垃圾 ——
        /// 在答辩现场会变成周期性的 GC 卡顿。
        ///
        /// 缓存 key 用 **Renderer 而不是 Mesh**：建筑是 prefab 实例，
        /// `sharedMesh` 在多个实例间共享，但它们的**世界位置不同**，
        /// 按 Mesh 缓存会把 A 栋的顶点当成 B 栋的。
        /// 场景是静态的（没有可移动的建筑），所以缓存永不失效。
        /// </summary>
        private bool TryGetSurfaceYAt(Renderer rd, float wx, float wz, float radius, out float surfaceY)
        {
            surfaceY = 0f;

            Vector3[] worldVerts = GetWorldVertsCached(rd);
            if (worldVerts == null || worldVerts.Length == 0) return false;

            float r2 = radius * radius;
            bool any = false;
            float bestY = float.MinValue;

            for (int i = 0; i < worldVerts.Length; i++)
            {
                Vector3 w = worldVerts[i];
                float dx = w.x - wx;
                float dz = w.z - wz;
                if (dx * dx + dz * dz > r2) continue;

                if (w.y > bestY) bestY = w.y;
                any = true;
            }

            if (!any) return false;
            surfaceY = bestY;
            return true;
        }

        /// <summary>按 Renderer 缓存世界坐标顶点（见 TryGetSurfaceYAt 的说明）。</summary>
        private Vector3[] GetWorldVertsCached(Renderer rd)
        {
            if (roofVertCache == null)
                roofVertCache = new System.Collections.Generic.Dictionary<Renderer, Vector3[]>(64);

            Vector3[] cached;
            if (roofVertCache.TryGetValue(rd, out cached)) return cached;

            MeshFilter mf = rd.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null)
            {
                roofVertCache[rd] = null;
                return null;
            }

            Mesh mesh = mf.sharedMesh;
            // `isReadable == false` 时 `.vertices` 会抛。本项目的建筑网格都是
            // 导入的可读资源，但这条调用在 Update 链上 —— 任何异常都会让
            // 整个天气系统停摆，所以必须兜住。
            if (!mesh.isReadable)
            {
                roofVertCache[rd] = null;
                return null;
            }

            Vector3[] local;
            try { local = mesh.vertices; }
            catch (System.Exception)
            {
                roofVertCache[rd] = null;
                return null;
            }

            if (local == null || local.Length == 0)
            {
                roofVertCache[rd] = null;
                return null;
            }

            Matrix4x4 m = rd.transform.localToWorldMatrix;
            for (int i = 0; i < local.Length; i++)
                local[i] = m.MultiplyPoint3x4(local[i]);

            roofVertCache[rd] = local;
            return local;
        }

        private System.Collections.Generic.Dictionary<Renderer, Vector3[]> roofVertCache;

        /// <summary>屋顶表面采样的水平邻域半径（米）。见 IndoorByRendererRoof ⑤ 的精度说明。</summary>
        private const float RoofProbeRadius = 0.6f;

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
            // 【P28】根节点换了（PlayerRoot 的兜底缓存过期重查）→ 模型引用必须跟着失效。
            // 否则抬升会作用在旧对象上：表现为"角色不动、别的什么东西在动"，
            // 或者模型一路飘走（因为 playerBaseLocalPos 记的是旧对象的位置）。
            Transform rootNow = PlayerRoot();
            if (_playerModel != null && rootNow != null && !_playerModel.IsChildOf(rootNow))
            {
                _playerModel = null;
                playerLiftNow = 0f;
            }

            if (_playerModel == null)
            {
                Transform root = rootNow;
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
        //
        // ── 【P28 2026-11-05】容量与寿命重新对齐 ──────────────────────────
        // 原组合是 PrintMax=32 + PrintLife=240，两个数自相矛盾：
        //   32 槽 × 0.36m = 11.52m，按 2.2m/s 只要 **5.2 秒**就走满一圈，
        //   最早的印子被环形缓冲覆盖 —— 寿命 240 秒根本轮不到生效。
        // 现场表现就是主理人说的「走远往回看，脚印还会消失，走进又出现了」。
        //
        // 现组合是 128 槽 + 90 秒：
        //   128 × 0.36m = **46.08m** 足迹带，走满需 20.9 秒；
        //   90 秒寿命中前 63 秒深度不衰减，最后 27 秒淡出。
        //   于是"走出一段再回头看"这一整段（答辩的核心动作）全程可见，
        //   而站着不动 90 秒后整片足迹被雪填平，也符合直觉。
        //
        // 为什么不是 256：92m 已超出"回头看"的实用距离，而每多一槽，
        // 每个像素就多一次常量缓冲读取。128 是容量与成本的分界，
        // 配套的整片剔除（shader 的 _PrintBounds）让实际开销几乎不变。
        private const int PrintMax = 128;
        private const float PrintStride = 0.36f;
        private const float PrintSideOffset = 0.11f;
        private const float PrintRadiusM = 0.16f;
        private const float PrintLifeSec = 90f;

        private readonly Vector4[] prints = new Vector4[PrintMax];   // xy=世界xz, z=落印时刻, w=强度
        private int printWrite;          // 环形缓冲：下一个写入槽
        private int printUsed;           // 已写入槽数（≤ PrintMax）
        private Vector3 printLastPos;    // 上一次落印的世界位置（脚底中心）
        private bool printHasLast;       // false = 刚起步/刚落地，本帧只记录不落印
        private int printFootSide = 1;   // +1 / -1 交替，模拟左右脚
        private bool printCleared = true; // 材质里的脚印是否已清空
        private Material printMatCached;   // 上一次写入脚印的材质；换实例说明鞋印已丢

        // 【P28】脚印门槛诊断（只打 3 次，见 UpdateFootprints 里的说明）
        private int printDiagCount;
        private float printDiagAt = -999f;

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
        /// 每个脚印一个面片会有 PrintMax 个 DrawCall，且要和雪面做 Z 排序（雪面是
        /// Transparent 队列 2996，面片插在中间必然穿插）。写进 shader 常量数组
        /// 只需要一次 SetVectorArray，零额外 DrawCall，也不会和雪面抢深度。
        /// </summary>
        private void UpdateFootprints(float snowAmount)
        {
            if (snowGroundMat == null) return;

            // 材质被重建过（雪盘被销毁重建 / 切场景后重新构建）→ 旧材质里的脚印全丢，
            // 但 C# 侧的 printUsed 还记着已写入的槽数、prints[] 里还存着旧坐标。
            // 不重置就会"自检说有 N 个脚印、画面上一个都没有"，而且下一秒
            // EmitPrint 会把那些旧坐标原样推回材质 —— 幽灵足迹复活。
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

            // ══════════════════════════════════════════════════════════════
            // 【P28 2026-11-05】脚印"一个都不出"的门槛诊断（只打 3 次）
            //
            // 上一轮改完仍然 0/32，而 `SnowAmount` 只有 0.42 —— **没到自动自检的
            // 0.5 阈值**，于是整个 Play 里一条脚印相关日志都没有。这就是
            // "改完不知道有没有生效"的根源：成功的路径上一片安静。
            //
            // 这段把从进函数到落印之间的**每一道闸门**都打出来，任何一道没过
            // 都能一眼看出是哪一道。落印成功也打（第 1 次），用来对比。
            // 封顶 3 次：用户一走动就会每帧进这个函数，不封顶必刷屏（P26 的教训）。
            // ══════════════════════════════════════════════════════════════
            Transform dbgRoot = PlayerRoot();
            bool dbgGrounded = dbgRoot != null && IsPlayerGrounded(dbgRoot);

            if (printDiagCount < 3 && (Time.time - printDiagAt > 1.5f))
            {
                printDiagAt = Time.time;
                printDiagCount++;
                Debug.Log("[P28][脚印诊断 #" + printDiagCount + "/3]\n"
                    + "    snowAmount   = " + snowAmount.ToString("F3")
                    + "   （< 0.15 → depth 归零，一整段都不留印）\n"
                    + "    depth        = " + depth.ToString("F3")
                    + (depth <= 0.01f ? "   ← **闸门①未过：雪量太小**" : "   闸门①通过") + "\n"
                    + "    snowGroundMat= " + (snowGroundMat != null ? "ok" : "**null**") + "\n"
                    + "    playerRoot   = " + (dbgRoot != null ? dbgRoot.name : "**null**")
                    + "   pos=" + (dbgRoot != null ? dbgRoot.position.ToString("F2") : "?") + "\n"
                    + "    grounded     = " + dbgGrounded
                    + "   cc=" + (_playerCc != null ? "有" : "**null（root 不是角色）**") + "\n"
                    + "    printUsed    = " + printUsed + "/" + PrintMax
                    + "   hasLast=" + printHasLast + "   已推材质 _PrintCount="
                    + (snowGroundMat != null ? snowGroundMat.GetFloat(IdPrintCount).ToString("F0") : "?") + "\n"
                    + "    足迹带       = " + (PrintMax * PrintStride).ToString("F1") + "m（"
                    + PrintMax + " 槽 × " + PrintStride.ToString("F2") + "m）  寿命 " + PrintLifeSec.ToString("F0") + "s");
            }

            // 雪化了 / 切回晴天 → 清空历史脚印。
            // 必须清：否则"雪→晴→雪"之后，上一场雪留在缓冲里的那些点会
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

            // ⚠️ 缓冲清零还不够：`_Prints[]` 数组里仍躺着上一场的 128 个旧坐标。
            // shader 靠 `p.w < 0.001` 跳过空槽，而 C# 这边**没清过数组本身**，
            // 只是把 _PrintCount 置 0 让 FootprintAt 整体早退。
            // 一旦下一次 EmitPrint 把 _PrintCount 推回 1，那些 w=1 的旧槽
            // 会立刻"复活"在半路上（上一场雪留下的位置）——
            // 这正是注释里说的幽灵足迹。所以数组必须真的清空。
            for (int i = 0; i < prints.Length; i++) prints[i] = Vector4.zero;

            // 包围盒也要一起复位，否则会留着一个覆盖旧足迹的大盒子，
            // 让新一场的剔除失效（退化成大面积 128 次循环）。
            if (printMatCached != null)
                printMatCached.SetVector(IdPrintBounds, new Vector4(1f, 1f, -1f, -1f));
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
            UpdatePrintBounds();
        }

        /// <summary>
        /// 重算所有**有效**脚印的世界 xz 包围盒，推给 shader 做整片剔除。
        ///
        /// ── 为什么必须重算，不能只扩张 ─────────────────────────────────────
        /// 环形缓冲会**覆盖**旧槽位。若包围盒只做 max/min 单调扩张，
        /// 走出一段再折返时，盒子里会包含早已被覆盖掉的旧区域 ——
        /// 于是"剔除"失去意义，越来越多像素进 128 次循环，
        /// 而这正是加包围盒要避免的事。
        ///
        /// ── 成本 ──────────────────────────────────────────────────────────
        /// 只在 EmitPrint（约 6 次/秒）与 ResetPrints 时调用，遍历 128 个
        /// Vector4 是微秒级。跟"每像素 128 次"完全不是一个量级，不需要再优化。
        ///
        /// ── 为什么时间过期的也算有效 ──────────────────────────────────────
        /// 过期脚印在 shader 里 m 归 0，画面上看不见，但它仍占着槽位。
        /// 若按"是否过期"筛选，盒子的收缩就依赖 _Time.y 与 C# 的时钟同步，
        /// 两边一旦漂移（暂停、时间缩放）就会出现"印子还在但盒子把它剔掉了"。
        /// 按槽位的 w 判空（w=0 是空槽）就够了 —— 覆盖面稍大一点无害。
        /// </summary>
        private void UpdatePrintBounds()
        {
            if (snowGroundMat == null) return;

            float minX = float.MaxValue, minZ = float.MaxValue;
            float maxX = float.MinValue, maxZ = float.MinValue;
            bool any = false;

            for (int i = 0; i < prints.Length; i++)
            {
                Vector4 p = prints[i];
                if (p.w < 0.001f) continue;    // 空槽
                if (p.x < minX) minX = p.x;
                if (p.x > maxX) maxX = p.x;
                if (p.y < minZ) minZ = p.y;    // y 分量存的是世界 z
                if (p.y > maxZ) maxZ = p.y;
                any = true;
            }

            // 没有任何脚印时给一个"必然不命中"的盒（min > max），
            // shader 第一句 XZ 比较就会返回 0，不会退化成全屏遍历。
            if (!any)
            {
                snowGroundMat.SetVector(IdPrintBounds, new Vector4(1f, 1f, -1f, -1f));
                return;
            }

            snowGroundMat.SetVector(IdPrintBounds, new Vector4(minX, minZ, maxX, maxZ));
        }

        /// <summary>
        /// 角色是否踩在地面上。
        /// 优先用 CharacterController.isGrounded；没有控制器（第一人称模式等）时
        /// 保守返回 true —— 宁可多留几个脚印，也不要整片雪地一个印都没有。
        ///
        /// ══════════════════════════════════════════════════════════════════
        /// 【P28 2026-11-05】**不要缓存"找不到"这个结果**
        ///
        /// 原实现：
        ///     if (!_playerCcSearched) { _playerCcSearched = true; _playerCc = root.GetComponent<...>(); }
        /// 第一次调用若 `root` 还不是角色（见 PlayerRoot 的 cam.transform.root 兜底），
        /// 拿到 null 后 `_playerCcSearched` 已置 true，**从此永远不再查**。
        ///
        /// 现场证据：自检里同时出现这两行，而它们是互相矛盾的 ——
        ///     [TwinPlayer] 接线检查：CharacterController=True      ← 角色身上明明有
        ///     脚印 = 0/32 个（无 CharacterController，按接地处理）   ← 这里却说没有
        /// 唯一解释就是 root 不是角色，且这个 null 被永久缓存了。
        ///
        /// 现在改成：**只在拿到了 CharacterController 时才停止搜索**。
        /// 每帧多一次 GetComponent 的代价，换掉一个永久性误判，值得。
        /// （GetComponent 在同一个对象上重复调用会被 Unity 的组件缓存加速，
        ///   这里还挂了 0.15s 级的间接节流 —— 只有 root 一直不对时才会持续走到。）
        /// ══════════════════════════════════════════════════════════════════
        /// </summary>
        private bool IsPlayerGrounded(Transform root)
        {
            if (_playerCc == null && !_playerCcSearched)
            {
                _playerCc = root != null ? root.GetComponent<CharacterController>() : null;
                // 只有真的拿到了才封口。拿不到就下一帧接着找。
                _playerCcSearched = _playerCc != null;
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
                          + " m   （落距 = EmitHeight " + EmitHeight.ToString("0") + " + 这个值）");
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

            // 【P28】足迹带的实际长度与包围盒 —— 这是"走远看不见"的唯一口径。
            // 环封之后足迹带 = PrintMax × PrintStride，与"玩家实际走过多少米"无关。
            sb.AppendLine("    足迹带容量   = " + PrintMax + " 槽 × " + PrintStride.ToString("F2") + "m = "
                          + (PrintMax * PrintStride).ToString("F1") + " m"
                          + "   （按 2.2m/s 走满需 " + (PrintMax * PrintStride / 2.2f).ToString("F1") + "s）"
                          + "   寿命 " + PrintLifeSec.ToString("F0") + "s");
            if (snowGroundMat != null)
            {
                Vector4 bb = snowGroundMat.GetVector(IdPrintBounds);
                bool valid = bb.z >= bb.x && bb.w >= bb.y;
                sb.AppendLine("    _PrintBounds = " + (valid
                                  ? ("X[" + bb.x.ToString("F2") + ", " + bb.z.ToString("F2") + "]  Z["
                                     + bb.y.ToString("F2") + ", " + bb.w.ToString("F2") + "]  "
                                     + "  =" + (bb.z - bb.x).ToString("F1") + "×" + (bb.w - bb.y).ToString("F1") + "m")
                                  : "空（min>max，整片剔除生效）"));
            }
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
        private static readonly int IdPrintBounds = Shader.PropertyToID("_PrintBounds");

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
                // 【P13-D9】射线起点从 camPos 改成**角色**位置。
                //
                // 为什么：第三人称相机吊在角色后上方 4~5m，从 camPos 往上打，
                // 命中的可能是**斜后方的屋檐/外挑板/雨棚**，而不是头顶天花板。
                // 站在二楼靠窗时尤其明显：相机在窗外上方，射线穿过玻璃打到外墙，
                // 判定结果在"室内/室外"之间随视角翻转 → 积水层跟着一会开一会关。
                //
                // "我在不在室内"这件事，取决于**人站在哪**，不取决于镜头在哪。
                // 有角色就用角色，没有（数字孪生自由相机）才退回收相机。
                Vector3 ceilFrom = camPos;
                Transform ceilPr = PlayerRoot();
                if (ceilPr != null) ceilFrom = ceilPr.position;
                ceilFrom += Vector3.up * 0.02f;   // 别正好贴在胶囊底面上

                // ══════════════════════════════════════════════════════════════
                // 【P13 修的真 bug：室内还是有雨雪粒子 —— 射线打到了玩家自己】
                //
                // 上一轮把起点从 camPos 改成角色位置（为了避开屋檐误判），
                // 但**忘了相机原本在角色上方 4~5m、不会命中自己**，而角色位置
                // 正好是 CharacterController 胶囊的底面（height 1.8 / center.y 0.9
                // → 胶囊占 local y∈[0, 1.8]，射线起点就贴在 y=0 上）。
                //
                // 再加上这一行上面刚把 queriesHitBackfaces 打开（为了命中单层天花板），
                // 从胶囊**内部**出发的射线会命中自己的**内壁**：
                //
                //     头顶射线 = TwinPlayer  0.18m  法线Y=-1.00
                //     indoor   = False  / indoorFade = 1.000      ← 这才是病根
                //
                // 0.18m 那个距离就是胶囊底部的内表面。于是：
                //   · `!IsPlayerCollider(up.collider)` 不成立 → indoor 恒 false
                //   · shelter = indoorFade = 1 → 室内满速发射（rain=1 时 3200 粒/秒）
                //   · 发射盒在「角色 xz + 室外地平面 + 16m」，也就是**楼顶之上**
                //   · 粒子不参与碰撞 → 直接穿透楼板落进走廊
                // 主理人看到的"室内有雨雪粒子"就是这么来的。
                //
                // ── 为什么改用 RaycastNonAlloc 而不是把起点抬高 ─────────────
                // 抬高起点（+1.6m 眼高）也能避开自己，但会把 IndoorCeilHeight=4.6
                // 的语义从"脚底到天花板"变成"眼高到天花板"，所有阈值都要重算，
                // 而且矮空间（楼梯下、设备夹层）会漏判。
                // 取全部命中再自己挑，语义完全不变，只多一次数组遍历（16 个槽位）。
                // ══════════════════════════════════════════════════════════════
                int nUp = Physics.RaycastNonAlloc(new Ray(ceilFrom, Vector3.up), ceilHits,
                                                  IndoorCeilHeight,
                                                  Physics.DefaultRaycastLayers,
                                                  QueryTriggerInteraction.UseGlobal);

                // 【P13 第十次修】近档（4.6m）没命中时，退到远档（12m）再找一次。
                //
                // 为什么这样不会误判室外：
                //   室外头顶是天空，12m 内没有任何碰撞体 → 依然 nUp=0，不会误判。
                //   站树下时树冠底面 2.2m 起，**近档就能命中**，走不到这里；
                //   即便某些树冠更高、只在远档命中，法线判据 -0.92 也会把它滤掉。
                // 为什么必须先查近档、再做远档：
                //   若直接一次打到 12m，空旷处长廊（其实有顶但顶在 3.3m）
                //   依然取最近命中，结果不变；但如果头顶先有横梁(2m)、再是屋顶(8m)，
                //   一次查询取最近仍得 2m，也没问题。真正的差别在于**避免
                //   把"外面 6m 处的树冠"当成立即命中** —— 分档保留了"近的一定优先"。
                if (nUp == 0)
                {
                    nUp = Physics.RaycastNonAlloc(new Ray(ceilFrom, Vector3.up), ceilHits,
                                                  IndoorCeilHeightTall,
                                                  Physics.DefaultRaycastLayers,
                                                  QueryTriggerInteraction.UseGlobal);
                    ceilTallFallback = nUp > 0;   // 记录"是靠远档才找到的"，供自检判读
                }
                else ceilTallFallback = false;

                Physics.queriesHitBackfaces = prevBackfaces;

                // 在所有命中里挑「最近的、不是玩家自己」的那一个。
                // NonAlloc 的返回顺序不保证按距离排序，必须自己比。
                //
                // layerMask 用 Physics.DefaultRaycastLayers 而不是 ~0：与原来那行
                // `Physics.Raycast(ray, out up, dist)` 的默认行为保持一致
                // （默认就是 DefaultRaycastLayers = 除 IgnoreRaycast 外的全部层）。
                // 写 ~0 会把 Ignore Raycast 层也拉进来，那些辅助碰撞体一旦出现在
                // 角色头顶，就会把它误判成天花板。
                bool hitUp = false;
                up = default(RaycastHit);
                float bestDist = float.MaxValue;      // 非玩家命中的最近距离
                float selfDist = float.MaxValue;      // 玩家自命中的最近距离（只给自检用）
                ceilFromY = ceilFrom.y;
                ceilSelfHit = false;
                for (int i = 0; i < nUp; i++)
                {
                    RaycastHit h = ceilHits[i];
                    if (h.collider == null) continue;

                    // ⚠️ 自命中的距离**不能**写进 bestDist —— 否则 0.18m 的自命中
                    // 会把后面 4m 处的真天花板挤掉（`h.distance >= bestDist` 恒成立），
                    // indoor 还是 false，等于没修。两个距离必须分开记。
                    if (IsPlayerCollider(h.collider))
                    {
                        if (h.distance < selfDist) selfDist = h.distance;
                        continue;   // 跳过玩家自己的胶囊/模型
                    }

                    if (h.distance <= 0.02f) continue;
                    if (h.distance >= bestDist) continue;
                    bestDist = h.distance;
                    up = h;
                    hitUp = true;
                }
                ceilSelfHit = selfDist < float.MaxValue;

                if (hitUp)
                {
                    ceilDist = up.distance;
                    ceilNormalY = up.normal.y;
                    ceilName = up.collider != null ? up.collider.name : "?";
                    // 【P13】把命中的 collider 直接留一份，供屋顶可见性诊断用。
                    // 之前诊断代码靠 `GameObject.FindObjectsOfType` 按名字反查，
                    // 351 个对象每次自检扫一遍，既慢又可能撞上重名。
                    ceilCollider = up.collider;
                }
                else
                {
                    ceilDist = -1f;
                    ceilNormalY = 0f;
                    ceilName = "";
                    ceilCollider = null;
                }

                if (hitUp && up.normal.y <= IndoorCeilNormalY)   // 必须是被"下表面"挡住
                {
                    indoor = true;
                    // ⚠️ 必须在这里也清掉：否则上一次"靠渲染兜底"的值会残留，
                    // 自检会在一栋有碰撞体屋顶的楼里打出"靠渲染网格兜底"的假标记。
                    ceilByRenderer = false;
                }
                else
                {
                    // ══════════════════════════════════════════════════════
                    // 【P13 第十二次修】没有天花板碰撞体的场馆，用渲染网格兜底。
                    //
                    // ── 为什么射线这条路走不通 ──────────────────────────────
                    // 主理人 2026-11-05 实测（篮球场）：
                    //     头顶射线 = **什么都没打到**
                    // 这两个场馆的屋顶只有渲染网格，Collider 一个都没给，
                    // 射线再怎么放远都没用，必须换数据源。
                    //
                    // ⚠️ 注意进到这里的**不只是**"完全没命中"这一种。
                    // 射线命中了但法线不符（比如打到横梁侧面、门框）也会走到这里，
                    // 那种情况同样需要兜底 —— 所以本分支不区分，一律试一次。
                    // ══════════════════════════════════════════════════════
                    if (IndoorByRendererRoof(ceilFrom, out float roofY, out string roofName))
                    {
                        indoor = true;
                        // 用射线口径统一：ceilDist 是从"脚底起点"到屋顶的距离
                        ceilDist = Mathf.Max(0.1f, roofY - ceilFrom.y);
                        ceilNormalY = -1f;          // 视为水平屋顶
                        ceilName = roofName + "（渲染网格兜底）";
                        ceilCollider = null;
                        ceilByRenderer = true;
                    }
                    else ceilByRenderer = false;
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

            // ══════════════════════════════════════════════════════════════
            // 【P13 Bug2 修：积水层的锚点必须单独算，不能沿用雪盘那套"锚角色"
            //
            // ── 主理人报的现象 ────────────────────────────────────────────
            // "观景模式下部分区域有水坑，水坑区域显示不全"
            // "第三人称看到室外有些地方是有水坑的，但观景模式下部分水坑不显示"
            //
            // ── 根因 ──────────────────────────────────────────────────────
            // 积水层是**相机脚下 40m 见方**的一块贴地平面，靠 Follow 每帧挪位置。
            // 但上一版把锚点取成了角色位置（anchorSrc = pr.position），
            // 于是：
            //   · 第三人称：相机吊在角色后上方，两者相距 4~5m，
            //     平面仍大致盖住视野 → 看起来正常
            //   · 观景模式：相机可以飞出去几十米，而平面**留在角色脚下**，
            //     相机看的地方根本没有平面 → 水坑整片消失
            // 截图里那片水坑边界是笔直的矩形，正是这块 40m 方块的边缘。
            //
            // ── 为什么不能简单改成"永远跟相机" ────────────────────────────
            // 角色模式下平面跟相机，会像早先"水坑粘在镜头前滑"那样：
            // 第三人称镜头绕角色转，平面就横着扫。当时已经把锚点从相机
            // 改成角色才治好的（见 Follow 的长注释）。所以必须**分模式**：
            //   · 角色模式 → 锚角色（保持既有正确行为，不动）
            //   · 观景模式 → 锚相机（相机去哪，水就在哪，这才是观景要的）
            //
            // ── 判据为什么用 FreeCameraController.enabled ─────────────────
            // TwinThirdPersonCamera 的 _freeCamMode 是 private 且无公开属性，
            // 但它在 ApplyFreeCamMode 里同步了这个控制器的 enabled：
            //     if (_free != null && _free.enabled != on) _free.enabled = on;
            // 所以 enabled 与"是否观景模式"严格同义，且不需要改另一个类。
            // ══════════════════════════════════════════════════════════════
            bool freeCam = IsFreeCamMode();
            Vector3 wetAnchor = freeCam ? camPos : anchorSrc;

            if (indoorFade > 0.5f)
            {
                // 室外：重新锚定。
                //
                // ══════════════════════════════════════════════════════════
                // 【P28 2026-11-05】⚠️ 这里**不要搬脚印**（一度想这么做，已否）
                //
                // 我一度以为"走远看不见、走近又出现"是雪盘平移把脚印甩在盘外，
                // 于是加了个 ShiftPrints 把 32 个历史脚印按锚点偏移一起搬。
                // 那是**错的**，而且会引入更糟的 bug，理由两条：
                //
                // ① 脚印是**世界坐标**，雪盘顶点也是**世界坐标**。
                //    DrapeSnow 里 `wx = px + b.x * takeSize`，px 就是 snowGround
                //    的当前世界 x —— 盘平移之后，同一片世界区域仍然被新的顶点
                //    覆盖，`i.worldPos.xz` 依旧能算到那里。**根本没有"甩在盘外"**。
                // ② 雪盘跟随本身就带 0.25m 节流（DrapeSnow 的 dirty 判据）。
                //    若脚印也跟着搬，锚点每抖一次脚印就挪一次 —— 结果不是
                //    "钉在雪面上"，而是**脚印黏着角色一起滑**，比现在更假。
                //
                // 真正的原因是**足迹带宽 11.52m**（32 槽 × 0.36m）：
                // 走远 20m 回头，最早的印子早被环形缓冲覆盖掉了。那是容量问题，
                // 靠 PrintMax 32→128 解决（见下方常量区），不是坐标问题。
                // ══════════════════════════════════════════════════════════
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
                splashRoot.position = new Vector3(anchorSrc.x, groundY + 0.03f, anchorSrc.z);
            // 雪盘用**世界锚点 + 室外地平面**，不用相机位置与 groundY
            if (snowGround != null)
                snowGround.position = new Vector3(snowAnchor.x, outdoorGroundY + 0.02f, snowAnchor.z);
            if (snowGroundFar != null)
                snowGroundFar.position = new Vector3(snowAnchor.x, outdoorGroundY + 0.02f, snowAnchor.z);

            // ══════════════════════════════════════════════════════════════════
            // 【P13-D9】积水层跟随基准：相机 → **角色**（这一条是"水坑随视角
            //           漂移、转视角扫进室内地板"的直接根因）
            //
            // ── 原实现错在哪 ────────────────────────────────────────────
            //     wet.Follow(camPos, groundY);
            // 第三人称相机绕角色做 orbital 旋转，camPos 在半径 4~5m 的圆弧上跑。
            // 积水面是 40m 见方**无条件跟随** camPos 的水平面 —— 镜头一摆，
            // 整片水面就横着扫过去，玩家看到的是"水坑黏在镜头前面滑"。
            //
            // ── 更糟的一层：二楼会把水面铺进教室 ─────────────────────────
            // 第二个参数 groundY 来自 ProbeGround(camPos)，即"相机正下方第一个面"。
            // 站在二楼时它就等于**二楼楼板高度**，于是 40m 的积水面被整块抬到
            // 二楼、铺在室内木地板上 → 主理人截图里"木地板全变成黑色水面"。
            // 这不是水坑跑进室内，是**水面本来就被放到了二楼地板的标高上**。
            //
            // ── 为什么改成角色 + 室外地平面（缺一不可）──────────────────
            //   · 位置 xz 用角色：转身不动，只有真正走路才移动。这是"跟脚不跟镜头"。
            //   · 高度用 outdoorGroundY：**室外地平面**，与积雪盘同一套口径。
            //     它由 ProbeOutdoorGround 采到，与相机在第几层楼无关，
            //     所以站在二楼时水面仍留在一楼室外地面，不会飘进教室。
            //
            // ── 为什么不再需要"室内就关掉"来救场 ─────────────────────────
            // Apply() 里本来就有 indoorFade 把 alpha 压到 0（室内不显示）。
            // 但它只能治"看得见"，治不了"位置错"：进场那 0.33 秒的淡出过程中，
            // 水面已经在正确/错误的位置之间滑过一次了。位置对了，淡出才只是淡出。
            // ══════════════════════════════════════════════════════════════════
            if (wet != null)
            {
                // 先切模式（会改平面尺寸与 _EdgeFade），再定位。
                // 顺序不能反：SetFreeCam 内部按"状态有没有变"短路，
                // 若先 Follow 后 SetFreeCam，尺寸变化要下一帧才生效，
                // 观景切换那一瞬间会看到一帧"大水坑只显示中间一小块"。
                wet.SetFreeCam(freeCam);
                wet.Follow(new Vector3(wetAnchor.x, 0f, wetAnchor.z), outdoorGroundY);
            }

            // ══════════════════════════════════════════════════════════════════
            // 【P13-D9】雨雪粒子发射盒：从"跟相机"改成"跟角色 + 室外地平面"
            //
            // ── 为什么二楼看不到雨（主理人实测现象之一）──────────────────
            // 发射器是**本组件的子物体**（localPosition.y = +EmitHeight = 16），
            // 而本组件 LateUpdate 每帧 transform.position = cam.position。
            // 于是在二楼房间里，发射盒中心在**相机正上方 16m** ——
            // 那已经在二楼天花板（层高 3.3~4.5m）**之上**，粒子生成在楼板上方，
            // 被楼板完全挡住，人站在二楼自然一滴雨都看不见。
            // 同理，"站着不动只转眼"时发射盒也会随 camPos 在圆弧上平移，
            // 雨丝整体跟着镜头横移，也是"雨随视角变"的来源之一。
            //
            // ── 为什么改成"角色 xz + 室外地平面 y" ──────────────────────
            //   · xz 锚角色：转身不动，只随真实行走移动 → 雨不再跟镜头平移。
            //   · y 用 outdoorGroundY + EmitHeight：**室外地面**为基，
            //     与相机在第几层楼无关 → 二楼时发射盒仍悬在一楼室外上方，
            //     雨从楼外落下，从窗户望出去能看到正在下雨（这才是真实观感：
            //     二楼窗外的雨就该在窗外，不该在屋里）。
            //
            // ── 为什么室内要 SetActive(false) 而不是只靠 lifetime ────────
            // 发射盒在楼外时，它的水平范围（110m）会覆盖到室内**头顶高度附近**，
            // 部分粒子可能从窗口飘进来。所以沿用原本的 indoor 开关：
            // 室内不发射。室内"看得到雨"这件事靠的是**窗外那部分粒子**，
            // 而窗外的粒子不在室内，不受 indoor 开关影响 —— 这正是要的效果。
            // ══════════════════════════════════════════════════════════════════
            if (rainPs != null || snowPs != null)
            {
                // 【P43 追加 11-17 二轮】发射 Y 随相机自适应：自由相机升空俯瞰时，
                // 固定高度的发射面会被相机压在头顶之下（航拍视角看到"雪天花板"）。
                // 取「地面+60」与「相机+40」的较大者，封顶「地面+250」——
                // 封顶是给寿命解析式（雪 Clamp 上限 40s）与粒子缓冲（45000）一个可算的边界。
                float camY = (cam != null) ? cam.transform.position.y : (outdoorGroundY + EmitHeight);
                float emitY = Mathf.Min(
                    Mathf.Max(outdoorGroundY + EmitHeight, camY + CamEmitClearance),
                    outdoorGroundY + MaxEmitAGL);
                // 【P50】xz 吸附到 24m 网格（见 EmitSnapGrid 注释）：格内走动发射盒静止，
                // 雪原地持续下落不随人移动；跨格才跳，世界空间旧粒子留在原地填补空窗。
                float snapX = Mathf.Round(anchorSrc.x / EmitSnapGrid) * EmitSnapGrid;
                float snapZ = Mathf.Round(anchorSrc.z / EmitSnapGrid) * EmitSnapGrid;
                Vector3 emitPos = new Vector3(snapX, emitY, snapZ);
                if (rainPs != null) rainPs.transform.position = emitPos;
                if (snowPs != null) snowPs.transform.position = emitPos;
            }

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

        /// <summary>
        /// 当前是不是「观景模式」（相机交给 FreeCameraController 自由漫游）。
        ///
        /// ── 为什么不读 TwinThirdPersonCamera._freeCamMode ──────────────────
        /// 它是 private，没有公开属性，也不该为一个天气组件去动玩家摄像机类的封装。
        /// 而 `ApplyFreeCamMode` 里明确同步了控制器的开关：
        ///     if (_free != null && _free.enabled != on) _free.enabled = on;
        /// 所以 `FreeCameraController.enabled` 与"是否观景模式"严格同义。
        ///
        /// ── 为什么用 FindObjectOfType 而不是缓存 ───────────────────────────
        /// 本方法每次 UpdateGround 调一次（0.15s 一次），而 FindObjectOfType 在
        /// 这个场景（351 个对象）开销可接受；缓存在这里反而是隐患 ——
        /// 场景切换或控制器被禁用后缓存会变陈旧，判断结果会卡在旧模式上。
        /// 为了把这 0.15s 一次的开销也压掉，结果用一个短的失效窗口缓存。
        /// </summary>
        private bool IsFreeCamMode()
        {
            // 0.15s 内复用上次结果：UpdateGround 的节流周期与之一致，
            // 不会因为缓存而漏掉模式切换（切换是人手按键，远慢于 0.15s）。
            if (Time.time - _freeCamProbeTime < 0.15f) return _freeCamCached;
            _freeCamProbeTime = Time.time;

            var fc = FindObjectOfType<SchoolTwin.Interaction.FreeCameraController>();
            _freeCamCached = fc != null && fc.enabled;
            return _freeCamCached;
        }

        private Transform PlayerRoot()
        {
            // ══════════════════════════════════════════════════════════════
            // 【P28 2026-11-05】兜底结果**不能永久缓存**
            //
            // 原实现是"拿到什么就缓存什么，只要有值就再也不查"。问题是 ① 号
            // 权威来源（TwinPlayerController）可能在第一次调用时**还没生成**，
            // 于是走到 ③ 的 `cam.transform.root` —— 而第三人称相机是独立对象，
            // 它的 root 根本不是角色。这个错误对象被缓存下来，此后所有
            // 依赖 PlayerRoot 的功能全部错位：
            //   · 头顶射线起点错 → 室内判定错
            //   · 脚印位置错     → 印子落在相机脚下而不是人物脚下
            //   · 接地判定错     → 见 IsPlayerGrounded 里的说明
            //
            // 现在区分两种命中：
            //   · 权威命中（TwinPlayerController）→ 永久缓存，它不会凭空消失
            //   · 兜底命中（tag / 相机根）        → 只缓存 _FallbackRecheck 秒，
            //                                       过期后重新找一次，等角色就位
            // ══════════════════════════════════════════════════════════════
            if (_playerRoot != null)
            {
                bool expired = _playerRootIsFallback
                               && Time.unscaledTime - _playerRootAt > PlayerRootFallbackRecheck;
                if (!expired) return _playerRoot;
                _playerRoot = null;    // 兜底缓存过期，重新查
                _playerCc = null;      // 换根了，控制器缓存跟着失效
                _playerCcSearched = false;
            }

            // ① 第三人称玩家控制器 —— 最权威。
            var pc = FindObjectOfType<SchoolTwin.Player.TwinPlayerController>();
            if (pc != null)
            {
                _playerRoot = pc.transform;
                _playerRootIsFallback = false;
                _playerRootAt = Time.unscaledTime;
                return _playerRoot;
            }

            // ② tag 兜底
            GameObject byTag = null;
            try { byTag = GameObject.FindGameObjectWithTag("Player"); }
            catch (UnityException) { /* 项目没定义 Player tag 时 FindGameObjectWithTag 会抛 */ }
            if (byTag != null)
            {
                _playerRoot = byTag.transform;
                _playerRootIsFallback = true;
                _playerRootAt = Time.unscaledTime;
                return _playerRoot;
            }

            // ③ 最后才用相机的根（第一人称模式下相机常挂在玩家下面，这条有效；
            //    第三人称独立相机走到这里说明前两层都没命中，此时宁可不认，也不要误判整棵树）
            if (cam != null)
            {
                _playerRoot = cam.transform.root;
                _playerRootIsFallback = true;
                _playerRootAt = Time.unscaledTime;
            }
            return _playerRoot;
        }

        /// <summary>兜底命中的重新探测间隔（秒）。角色通常在 1 秒内生成完毕。</summary>
        private const float PlayerRootFallbackRecheck = 0.5f;

        private bool _playerRootIsFallback;   // 当前缓存是兜底来源（可被重新探测替换）
        private float _playerRootAt = -999f;

        // 【P13】16 → 32：密林 + 花坛 + 铺装叠在一起时，16 个槽位常被前排树干占满，
// 后面的真地面被 RaycastNonAlloc 直接丢弃 → 该点采不到地面、退回 groundY，
// 于是雪面在那儿塌下去一块。扩容成本只有一个 32 元素的栈上数组。
        private readonly RaycastHit[] groundHits = new RaycastHit[32];

        // 【P13】头顶射线的命中缓冲。16 个槽位：室内向上 4.6m 内通常只会命中
        // 天花板 + 一两根管线/吊灯，但角色自己的胶囊内外壁就可能吃掉 2~3 个槽位
        // （从内部出发 + queriesHitBackfaces=true → 进出两面都会被记一次），
        // 给足余量，宁可多一个 16 元素的栈上数组，也不要让真天花板被挤掉。
        private readonly RaycastHit[] ceilHits = new RaycastHit[16];

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
                // ══════════════════════════════════════════════════════════
                // 【P13】⚠️ 这里是"改了一处、被另一处覆盖回去"的经典现场。
                //
                // BuildRain / BuildSnow 里刚把粗薄轴从局部 Y 改到局部 Z
                // （(Area, Area, 0.5)），但**本方法每帧都会重设 shape.scale**，
                // 只改 Build 那边等于没改 —— 第一帧就被这里覆盖回
                // (Area, 0.5, Area)，发射盒照旧是那堵 110m×110m 的竖墙。
                //
                // 任何"形状/尺寸"类的常量，只要在 Build 里设一次、又在
                // Update* 里周期刷新，**两处必须同值且同轴向**。
                // 判据：grep 一下 `shape.scale` 有几处赋值。
                // ══════════════════════════════════════════════════════════
                var v = new Vector3(wantArea, wantArea, 0.5f);   // 薄轴 = 局部 Z = 世界 Y
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
            // 【P28】控制器缓存必须一起清 —— 它和 _playerRoot 是一对：
            // 根换了而控制器还指着旧对象，接地判定就会用错的那个。
            _playerCc = null;
            _playerCcSearched = false;
            _playerRootIsFallback = false;
            _playerRootAt = -999f;
        }
    }
}

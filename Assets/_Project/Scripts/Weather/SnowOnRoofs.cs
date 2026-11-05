using UnityEngine;
using UnityEngine.Rendering;

namespace SchoolTwin.Weather
{
    /// <summary>
    /// 屋顶/雨棚积雪：从场景里找出"正面朝上的大面"，在其上方 2cm 生成一层贴合的雪壳。
    ///
    /// ══════════════════════════════════════════════════════════════════════
    /// 【P13 Bug3：为什么房顶没雪，以及为什么不用粒子碰撞】
    ///
    /// ── 现象 ──────────────────────────────────────────────────────────────
    /// 下雪天，地面、树根、草皮全白了，唯独**建筑屋顶是原色**，
    /// 看起来像屋顶被单独挖掉了雪 —— 非常突兀。
    ///
    /// ── 为什么现有的雪盘盖不到屋顶 ────────────────────────────────────────
    /// 现有积雪是三块**水平板**：近场 SnowGround（脚下 90m）、
    /// 远场 SnowGroundFar（260m）、树根 SnowOnTrees（每棵一个 3m 圆盘）。
    /// 三者全部通过"逐顶点向下打射线贴地"定位（见 SampleGroundHeight）。
    /// 而那个采样有两道硬限制：
    ///       float topY = Mathf.Max(eyeY + 2f, refY + 12f);   // 起点只到 refY+12
    ///       const float MaxRise = 1.5f;                       // dy > 1.5 直接丢弃
    /// 屋顶在 refY + 12 到 refY + 20，**高出 MaxRise 十几倍** → 全部被丢弃。
    /// 即便不丢弃，一块**贴地的水平板**在拓扑上也不可能同时贴地面又贴屋顶。
    /// 所以"屋顶没雪"不是参数没调好，是数据通路根本不支持。
    ///
    /// ── 为什么不用粒子碰撞（主理人问过这个方案）──────────────────────────
    /// 三条硬约束，任一条都足够否掉它：
    ///   ① Unity 粒子 Collision 的 World 模式要求 MeshCollider 必须 Convex。
    ///      本工程 19 个 MeshCollider 全是凹形（convex=0），勾上会把走廊、
    ///      屋顶凹角全部糊成一个凸包，碰撞检测直接失真。
    ///   ② 性能不允许：雨 3200 粒/秒 × 每帧物理查询（WeatherVisuals 里早有此结论）。
    ///   ③ **逻辑上不成立**：屋顶积雪的本质是**表面积雪** —— 雪停之后还在。
    ///      而粒子有 lifetime，时间一到就消失。两者根本不是一件事。
    ///
    /// ── 本方案：复制屋顶网格，向上偏移 2cm，复用 SnowGround 材质 ──────────
    /// 直接从建筑自己的网格生成雪壳，所以坡屋顶、女儿墙、雨棚、台阶顶面
    /// 这些形状全都能贴合，不需要知道"哪里是屋顶"。
    ///
    /// 判据（遍历所有 MeshFilter 的三角形）：
    ///   · 法线朝上：worldNormal.y > MinUpNormal（默认 0.55，约 57° 以内）
    ///   · 面积够大：单三角形面积 > MinTriArea（滤掉扶手、栏杆、装饰线脚）
    ///   · 位置够高：世界 Y > MinRoofY（滤掉地面铺装、路缘石）
    /// 三者同时满足的三角形才复制进雪壳。
    ///
    /// 为什么是"逐三角形筛"而不是"整个网格筛"：
    /// 建筑网格是一个整体（一栋楼一个大 mesh），屋顶和墙在同一个 mesh 里。
    /// 只能按三角形判朝向，把朝上的那部分摘出来 —— 这正是本方案的价值所在。
    /// ══════════════════════════════════════════════════════════════════════
    /// </summary>
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class SnowOnRoofs : MonoBehaviour
    {
        [Tooltip("三角形法线的世界 Y 分量下限。0.55 ≈ 与水平面夹角 57° 以内算作屋顶面")]
        public float minUpNormal = 0.55f;

        [Tooltip("单个三角形的最小面积（㎡）。滤掉栏杆、扶手、线脚等细碎朝上面")]
        public float minTriArea = 0.35f;

        [Tooltip("最低高度（世界 Y）。低于此值的朝上面不算屋顶，避免与地面雪盘重复")]
        public float minRoofY = 3.2f;

        [Tooltip("雪壳相对原面的抬升（米），避免与原屋顶 z-fighting")]
        public float lift = 0.02f;

        [Tooltip("整块网格三角形数超过此值时跳过（建筑外壳合并网格可能上十万面，逐面扫代价过高）")]
        public int maxTrisPerMesh = 120000;

        // ══════════════════════════════════════════════════════════════════
        // ⛔【2026-11-05 12:50 默认关闭 —— 本组件实测会污染整个场景】
        //
        // 实测四路连带故障（主理人 play 反馈）：
        //   ① 机房幕布发黑        —— renderQueue 2997 盖在所有屏幕之上
        //   ② 角色浮空            —— 被 SampleGroundHeight 当成"地面"
        //   ③ 脚印消失            —— 同一份错误采样
        //   ④ 屋顶反而仍无雪      —— 混入大量非屋顶面，自己糊成一团
        //
        // 真根因：minRoofY 是**世界绝对高度**，不是相对建筑。
        // 机房在 1F、天花板 3m，minRoofY=3.2f 拦不住二层地板、走廊吊顶、
        // 楼梯平台、看台顶面 —— 全被当成"屋顶"复制进了雪壳。
        // 主理人截图上那块从天花板斜插进机房的黑色几何体就是它。
        //
        // 保持 enabled=false 是**兜底**：即使有人又从别处 AddComponent，
        // 也不会 Build，场景不受影响。要重做请先解决"如何只取真正的屋顶"。
        // ══════════════════════════════════════════════════════════════════
        // 【为什么是 static readonly 而不是 const】———————————————
        // 原来写的是 `private const bool`。值一样，但 `const` 会在编译期
        // 常量折叠，于是 `Start()` 里 `if (true) { return; }` 之后的 `Build();`
        // 被编译器判定为**不可达代码**，冒出 CS0162 警告：
        //     SnowOnRoofs.cs(112,13): warning CS0162: Unreachable code detected
        //
        // 这个警告本身无害（行为正是我们想要的），但它会常驻 Console，
        // 而"Console 里有警告"会让人对真正的新警告脱敏 —— 这正是 P26 那个
        // 255 条重复警告埋掉真错误的老毛病。零成本消掉它，别留噪声。
        // 改成 `static readonly` 后，值在运行时读取，编译器不能折叠，
        // 分支保留，`Build()` 仍是可达的（只是永远不进）。
        // ══════════════════════════════════════════════════════════════════
        private static readonly bool DISABLED_BY_INCIDENT_20261105 = true;

        private Mesh shellMesh;
        private Material shellMat;
        private MeshRenderer shellMr;
        private bool built;

        private static readonly int IdSnowTex = Shader.PropertyToID("_SnowTex");
        private static readonly int IdSnowDetail = Shader.PropertyToID("_DetailTex");
        private static readonly int IdSnowColor = Shader.PropertyToID("_SnowColor");
        private static readonly int IdSkyColor = Shader.PropertyToID("_SkyColor");
        private static readonly int IdSnowAmount = Shader.PropertyToID("_SnowAmount");
        private static readonly int IdEdgeFade = Shader.PropertyToID("_EdgeFade");
        private static readonly int IdGlobalSnow = Shader.PropertyToID("_GlobalSnow");
        private static readonly int IdMaskOff = Shader.PropertyToID("_MaskOff");
        private static readonly int IdCullProp = Shader.PropertyToID("_Cull");

        private void Start()
        {
            // ⛔ 事故保险：见 DISABLED_BY_INCIDENT_20261105 的说明。
            // 即使有人从别处 AddComponent，也不会 Build → 场景不受污染。
            if (DISABLED_BY_INCIDENT_20261105)
            {
                enabled = false;
                return;
            }
            Build();
        }

        private void Build()
        {
            if (built) return;
            built = true;

            // ══════════════════════════════════════════════════════════════
            // 【沿用 SnowOnTrees 的两条铁律】
            //
            // ① 本组件的顶点全部用**世界坐标**写进 mesh。
            // ② 因此 transform 必须钉在世界原点，且**脱离跟随相机的父节点**。
            //    上一轮"室内外诡异白线"就是这两个条件同时被破坏造成的：
            //    顶点用世界坐标（对）+ 父节点每帧跟随相机（对）= 几何被平移两次。
            //    编译器不报错、单元测试不报错，只有人眼看画面能发现。
            //    详见 Docs/P13_踩坑日志_白线.md
            // ══════════════════════════════════════════════════════════════
            transform.SetParent(null, true);
            transform.position = Vector3.zero;
            transform.rotation = Quaternion.identity;
            transform.localScale = Vector3.one;

            var sh = Shader.Find("CampusUI/SnowGround");
            if (sh == null)
            {
                Debug.LogWarning("[SnowOnRoofs] 找不到 CampusUI/SnowGround 着色器，屋顶积雪不可用。");
                enabled = false;
                return;
            }

            // ---- 收集所有朝上的三角形 ----
            var verts = new System.Collections.Generic.List<Vector3>(4096);
            var norms = new System.Collections.Generic.List<Vector3>(4096);
            var uvs = new System.Collections.Generic.List<Vector2>(4096);
            var tris = new System.Collections.Generic.List<int>(6144);

            MeshFilter[] filters = FindObjectsOfType<MeshFilter>();
            int scannedMeshes = 0, scannedTris = 0, keptTris = 0;

            for (int fi = 0; fi < filters.Length; fi++)
            {
                MeshFilter mf = filters[fi];
                if (mf == null || mf.sharedMesh == null) continue;

                // 跳过本组件自己（重入保护）与所有粒子/线渲染器所在对象
                if (mf.gameObject == gameObject) continue;

                // ══════════════════════════════════════════════════════════
                // 【P13 Bug3】跳过所有"已经用了这套积雪着色器"的几何。
                //
                // 这类几何有三份，全都是**水平朝上的大面**，不排除就会全被收进雪壳：
                //   · SnowGround      近场 90m 贴地雪盘
                //   · SnowGroundFar   远场 260m 兜底雪盘
                //   · TreeSnowPiles   树根雪堆（几十块 3m 圆盘）
                // 后果不是"多一层雪"，而是**白度翻倍**：同一块地面被两层
                // 半透明雪叠加，比周围亮一截，边缘还会出现明显的接缝。
                // 更糟的是 Start 时它们多为 inactive（FindObjectsOfType 默认不返回），
                // 但只要有任何一个当时是激活的，就会漏进来 —— 这是"有时过曝、
                // 有时正常"的经典成因。用着色器名来判，比用物体名字可靠得多。
                // ══════════════════════════════════════════════════════════
                var selfRd = mf.GetComponent<Renderer>();
                if (selfRd != null && selfRd.sharedMaterial != null
                    && selfRd.sharedMaterial.shader != null
                    && selfRd.sharedMaterial.shader.name == "CampusUI/SnowGround")
                    continue;

                Mesh m = mf.sharedMesh;
                if (!m.isReadable) continue;   // 不可读网格读不到顶点，只能放弃

                Transform tr = mf.transform;

                // 整块网格的包围盒都低于门槛 → 连扫都不用扫（地形/铺装/草坪就在这一档）
                Bounds wb = WorldBounds(mf);
                if (wb.max.y < minRoofY) continue;

                Vector3[] mv = m.vertices;
                Vector3[] mn = m.normals;
                int[] mt = m.triangles;
                if (mv == null || mt == null || mt.Length < 3) continue;

                // 【P13 Bug3】超大网格（建筑外壳常是几万面合并成一个 mesh）逐面扫描
                // 代价过高，而且这类网格往往整块都在 minRoofY 之上，扫全量没有收益。
                // 放在取完 mt 之后再判，避免 `m.triangles` 被访问两次
                // （每次访问都会**复制一份**数组，几万面就是几百 KB 的无谓分配）。
                if (mt.Length / 3 > maxTrisPerMesh) continue;

                scannedMeshes++;

                // 逐三角形筛。法线用顶点法线平均（比叉积稳：不受三角形绕序影响）。
                for (int t = 0; t + 2 < mt.Length; t += 3)
                {
                    scannedTris++;

                    int i0 = mt[t], i1 = mt[t + 1], i2 = mt[t + 2];
                    if (i0 >= mv.Length || i1 >= mv.Length || i2 >= mv.Length) continue;

                    Vector3 w0 = tr.TransformPoint(mv[i0]);
                    Vector3 w1 = tr.TransformPoint(mv[i1]);
                    Vector3 w2 = tr.TransformPoint(mv[i2]);

                    // ---- 位置门槛：整块面都在门槛以上才算屋顶 ----
                    float minY = Mathf.Min(w0.y, Mathf.Min(w1.y, w2.y));
                    if (minY < minRoofY) continue;

                    // ---- 面积门槛 ----
                    Vector3 cross = Vector3.Cross(w1 - w0, w2 - w0);
                    float area = cross.magnitude * 0.5f;
                    if (area < minTriArea) continue;

                    // ---- 朝向门槛 ----
                    // 法线取"顶点法线平均"而不是叉积，理由要说清楚：
                    //   · 叉积方向由**三角形绕序**决定。这个工程的模型来自第三方资产，
                    //     绕序不一定统一（左手/右手、导入时 Flip Normals 都可能改过），
                    //     拿叉积判朝向会出现"同一栋楼一半屋顶有雪、一半没有"。
                    //   · 顶点法线是美术/导入器算好的，朝向语义可靠，且对绕序免疫。
                    // 网格连法线都没烘焙的情况（几乎不存在于 FBX 导入资产，
                    // 本工程 245 个模型全部带法线）直接整块跳过 ——
                    // 拿叉积猜朝向等于碰运气，宁可这栋楼没雪，也不要半栋楼有雪。
                    if (mn == null || mn.Length != mv.Length) continue;   // 无法可靠判朝向 → 跳过

                    Vector3 wn = (tr.TransformDirection(mn[i0]) +
                                  tr.TransformDirection(mn[i1]) +
                                  tr.TransformDirection(mn[i2])) / 3f;

                    if (wn.sqrMagnitude < 1e-8f) continue;
                    wn.Normalize();
                    if (wn.y < minUpNormal) continue;

                    // ---- 通过：复制这个三角形，整体抬高 lift ----
                    int b = verts.Count;
                    verts.Add(w0 + Vector3.up * lift);
                    verts.Add(w1 + Vector3.up * lift);
                    verts.Add(w2 + Vector3.up * lift);

                    // 法线直接给"正上方"：屋顶积雪是一层薄壳，
                    // 用原面法线会让坡屋顶上的雪偏灰（光照方向不对），
                    // 而薄雪壳的观感应当整体朝上受光。
                    norms.Add(Vector3.up);
                    norms.Add(Vector3.up);
                    norms.Add(Vector3.up);

                    // UV 用世界 XZ 缩放，与 SnowGround 的地面雪盘同一套尺度，
                    // 雪斑图案的疏密才能和地面一致。
                    const float uvScale = 1f / 6f;
                    uvs.Add(new Vector2(w0.x * uvScale, w0.z * uvScale));
                    uvs.Add(new Vector2(w1.x * uvScale, w1.z * uvScale));
                    uvs.Add(new Vector2(w2.x * uvScale, w2.z * uvScale));

                    tris.Add(b);
                    tris.Add(b + 1);
                    tris.Add(b + 2);

                    keptTris++;
                }
            }

            if (keptTris == 0)
            {
                Debug.LogWarning("[SnowOnRoofs] 没有找到符合条件（朝上 / 面积够大 / 高度够）的屋面三角形，"
                               + "屋顶积雪不会显示。已扫描 " + scannedMeshes + " 个网格 / "
                               + scannedTris + " 个三角形。");
                enabled = false;
                return;
            }

            shellMesh = new Mesh();
            shellMesh.name = "RoofSnowShell";
            // 顶点数可能超过 65535，必须用 32 位索引
            shellMesh.indexFormat = IndexFormat.UInt32;
            shellMesh.SetVertices(verts);
            shellMesh.SetNormals(norms);
            shellMesh.SetUVs(0, uvs);
            shellMesh.SetTriangles(tris, 0);
            shellMesh.RecalculateBounds();

            var myMf = GetComponent<MeshFilter>();
            shellMr = GetComponent<MeshRenderer>();
            if (myMf == null) myMf = gameObject.AddComponent<MeshFilter>();
            if (shellMr == null) shellMr = gameObject.AddComponent<MeshRenderer>();
            myMf.sharedMesh = shellMesh;

            shellMat = new Material(sh);
            shellMat.name = "RoofSnowShell";
            shellMat.SetTexture(IdSnowTex, ProceduralAssets.SnowPatch);
            shellMat.SetTexture(IdSnowDetail, ProceduralAssets.SnowDetail);
            shellMat.SetColor(IdSnowColor, new Color(0.92f, 0.95f, 1.0f, 1f));
            shellMat.SetColor(IdSkyColor, new Color(0.74f, 0.82f, 0.96f, 1f));
            shellMat.SetFloat(IdSnowAmount, 0f);
            // 不做边缘淡出：雪壳是按三角形拼出来的，没有"中心到边缘"的概念，
            // 强制 1.0 让整片屋顶均匀铺满（与树根雪盘同理）。
            shellMat.SetFloat(IdEdgeFade, 1.0f);
            // ⚠️【P13 Bug3】必须打开"忽略雪遮罩"模式，否则屋顶雪一个字都画不出来。
            //
            // 地面雪盘把整块平面铺满 [0,1] UV，_SnowTex（SnowPatch）的 alpha
            // 带"半径 93%~100% 淡到 0"的径向淡出，正好用来裁方形边界。
            // 但雪壳的 UV 只能给**世界坐标**（保证相邻三角形之间纹理连续），
            // 会跑到几十的范围 —— 而 _SnowTex 是 wrapMode=Clamp 的遮罩，
            // 几十的 UV 全被钳到边缘那一列像素，那里 alpha ≈ 0
            // → 片元里 `if (cov <= 0.003) discard` 把整片屋顶全部扔掉。
            // 现场表现极具误导性：Console 说"雪壳已生成 1.2 万个三角形"，
            // 画面上一片雪都没有 —— 会让人以为是筛选逻辑错了。
            shellMat.SetFloat(IdMaskOff, 1f);
            // ⚠️ 剔除模式必须跟着原屋顶走（_Cull = 2 = Back）。
            // 雪壳复制的是屋顶三角形，朝向与原屋顶一致，本工程建筑材质是
            // Standard 默认 Cull Back（单面）。若雪壳用 Cull Off，就会顺着
            // "从室内看不见的那个屋顶"露出来，在天花板上糊一片白 ——
            // 治了室外、坏了室内，正是 Bug 1 里"屋顶被单面剔除"的另一面。
            shellMat.SetFloat(IdCullProp, 2f);
            shellMat.renderQueue = 2997;

            shellMr.sharedMaterial = shellMat;
            shellMr.shadowCastingMode = ShadowCastingMode.Off;
            shellMr.receiveShadows = true;
            shellMr.lightProbeUsage = LightProbeUsage.Off;
            shellMr.reflectionProbeUsage = ReflectionProbeUsage.Off;
            shellMr.enabled = false;

            Debug.Log("[P13][屋顶积雪] 雪壳已生成：" + keptTris + " 个三角形 / "
                    + verts.Count + " 个顶点（扫描 " + scannedMeshes + " 个网格，"
                    + scannedTris + " 个三角形）");
        }

        /// <summary>世界空间包围盒（用 renderer.bounds 更省，但没有 Renderer 时退回网格 bounds）。</summary>
        private static Bounds WorldBounds(MeshFilter mf)
        {
            var r = mf.GetComponent<Renderer>();
            if (r != null) return r.bounds;

            Bounds lb = mf.sharedMesh.bounds;
            Transform tr = mf.transform;
            Vector3 c = tr.TransformPoint(lb.center);
            Vector3 e = tr.TransformVector(lb.extents);
            return new Bounds(c, new Vector3(Mathf.Abs(e.x) * 2f, Mathf.Abs(e.y) * 2f, Mathf.Abs(e.z) * 2f));
        }

        private void Update()
        {
            if (!built || shellMat == null || shellMr == null) return;

            // 与地面雪盘、树根雪堆同一套开关口径：读 WeatherVisuals 每帧写的全局雪量。
            float a = Shader.GetGlobalFloat(IdGlobalSnow);
            bool on = a > 0.01f;
            if (on != shellMr.enabled) shellMr.enabled = on;
            if (on) shellMat.SetFloat(IdSnowAmount, a);
        }

        private void OnDestroy()
        {
            if (shellMesh != null) Destroy(shellMesh);
            if (shellMat != null) Destroy(shellMat);
        }
    }
}
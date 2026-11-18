using UnityEngine;
using UnityEngine.Rendering;
using System.Collections.Generic;

namespace SchoolTwin.Weather
{
    /// <summary>
    /// 雪壳 v2：找出场景里「暴露在天空下的朝上面」（楼顶 / 雨棚 / 女儿墙顶 / 灯顶 / 窗台等），
    /// 在其上方 2cm 生成贴合的积雪壳。
    ///
    /// ══════════════════════════════════════════════════════════════════════
    /// 【v2 · 2026-11-17 重做 —— 按事故分册 §八判据逐条落实】
    ///
    /// v1（2026-11-05）四路连带的根因与 v2 的堵法：
    ///   ① 幕布/屏幕被盖      → v2 不收室内面（见下），与屏幕无空间重叠；queue 2996。
    ///   ② 角色浮空/脚印消失  → SampleGroundHeight 走 Physics 射线打 Collider，
    ///                          而雪壳**只有 MeshRenderer 没有 Collider** → 射线天然穿透。
    ///                          v2 铁律：这个类永不 AddComponent 任何 Collider。
    ///   ③ 室内判定翻转       → IndoorByRendererRoof 建候选表时按着色器名/物体名排除雪壳
    ///                          （WeatherVisuals.cs 内两行，2026-11-17 加）。
    ///   ④ 自己糊成一团       → 根因是 v1 用「世界绝对高度 minRoofY」筛选，
    ///                          1F 天花板 3.2m 就达标，室内楼板/吊顶全被收进壳。
    ///
    /// ── v2 的"屋顶判据"（三层，缺一不可）──────────────────────────────
    ///   第 1 层 · 逐三角形粗筛（便宜，先跑）：
    ///       · 世界 Y ≥ minWorldY(3m)      —— 地面铺装/路缘石归地面雪盘管；
    ///       · 顶点法线平均朝上 wn.y ≥ minUpNormal(0.55)；
    ///       · 三角形面积 ≥ minTriArea(0.35㎡) —— 滤掉栏杆/线脚/碎装饰。
    ///   第 2 层 · 【核心】逐列最高朝上面（1m XZ 栅格）：
    ///       把全部粗筛通过的三角形按其 XZ 所在栅格登记"该列最高朝上面"；
    ///       只有「自己就站在这列最高处（±topTolerance）」的三角形才收进雪壳。
    ///       为什么这一层能判"屋顶"：
    ///         · 3F 楼板再高，同一列上方永远有 4F 楼板/屋顶 —— 不是列顶 → 出局；
    ///         · 顶层吊顶与屋顶同列，屋顶比它高 0.2~0.5m → 吊顶出局；
    ///         · 篮球馆屋顶（9m）在共享大网格里也是自己那列的最高面 → 正确入选
    ///           （这正是 v1"世界绝对高度"会漏、§八路线 3"相对高度"单独用也会漏的
    ///            共享网格场景，栅格法天然免疫）；
    ///         · 灯顶、雨棚、女儿墙顶、窗台：各自列的最高面 → 全部入选（真实）。
    ///       依赖 Collider 与否：**不依赖** —— 纯网格拓扑统计，O(N) 一次扫描。
    ///       （v1 曾想用向上射线确认遮挡，但本工程全场景只有 19 个 MeshCollider，
    ///        建筑大多无碰撞体，射线打不到东西，该路线在本项目不可用——记下来防走回头路。）
    ///   第 3 层 · 相对高度兜底（相对本网格自身竖向跨度 ≥ relMinRatio(0.5)）：
    ///       防止栅格误差把"半山腰的大平台"收进来；单层大跨建筑（跨度小）
    ///       自身所有朝上面天然 ≥50%，不受此层误伤。
    ///
    /// ── 结构约束（§八共同前提）────────────────────────────────────────
    ///   · 一楼一壳：按来源 Renderer 分组，每组一个子 GameObject + 独立 Mesh，
    ///     不再合成全场景单块大 mesh；
    ///   · 顶点全部世界坐标 → 容器钉死世界原点、SetParent(null)（白线教训）；
    ///   · 材质复用 CampusUI/SnowGround（_MaskOff=1 世界 UV、_Cull=2 跟原屋顶单面）；
    ///   · renderQueue 2996（同近场雪盘家族；屏幕 Geometry 2000 且无室内面，不再重叠）；
    ///   · 雪量由 WeatherVisuals 每帧写的 _GlobalSnow 全局值驱动，与地面雪盘同开关。
    /// ══════════════════════════════════════════════════════════════════════
    /// </summary>
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class SnowOnRoofs : MonoBehaviour
    {
        [Tooltip("三角形顶点法线世界 Y 下限。0.55 ≈ 与水平面夹角 57° 以内算朝上面")]
        public float minUpNormal = 0.55f;

        [Tooltip("单个三角形最小面积（㎡）。滤掉栏杆、扶手、线脚等细碎朝上面")]
        public float minTriArea = 0.35f;

        [Tooltip("世界 Y 下限。低于此值的朝上面归地面雪盘管（雪盘自身有贴地采样）")]
        public float minWorldY = 3.0f;

        [Tooltip("相对高度下限：面 minY 相对【本网格自身竖向跨度】的占比。防栅格误差收进半空平台")]
        [Range(0.3f, 0.9f)]
        public float relMinRatio = 0.5f;

        [Tooltip("列顶判定容差（米）。面 minY 距该列最高朝上面不超过此值即视为列顶")]
        public float topTolerance = 0.8f;

        [Tooltip("雪壳相对原面的抬升（米），避免与原屋顶 z-fighting")]
        public float lift = 0.02f;

        [Tooltip("单网格三角形数超过此值时跳过（防一次性扫描卡死；2026-11-17 从 120k 放宽到 400k，建筑合并网格实测更大）")]
        public int maxTrisPerMesh = 400000;

        [Tooltip("栅格列宽（米）。1m 对楼顶/雨棚尺度合适；调小更精细但更慢")]
        public float cellSize = 1.0f;

        [Tooltip("源网格名包含这些词就不铺雪：室内楼板/楼梯/顶棚不该有积雪")]
        // 【2026-11-18 遗迹修复】Stairs02Concrete 必须排除：
        // 它是二楼楼梯口的混凝土平台（顶面积约 4.5×1.4m），顶面被"逐列最高"误判成屋顶，
        // 烘焙后是一整块纯白薄片（RoofSnowShell_Stairs02Concrete），现场就是那块"白板"。
        // 上一轮只排了 Floor2，漏了它 —— 所以 Floor2 修好后白板"又回来了"。
        public string[] excludeSourceName = new string[] { "Floor2", "KitchenFloor", "ZalTop", "ZalStairs", "Stairs02Concrete" };

        // ══════════════════════════════════════════════════════════════════
        // ⛔ 事故保险开关（v1 时代引入，v2 重做后置 false 放行）。
        // 保留这个开关而不是删掉：万一 v2 在某个场景又出现连带，把它拨回 true
        // 即可整体停用（即使被别处 AddComponent 也不 Build），与 11-05 处置同口径。
        // v2 放行的依据：§八三条判据已落实 + 四路连带各有独立防线（见类头注）。
        // static readonly 而非 const：防编译期常量折叠产生 CS0162 不可达警告（v1 教训）。
        // ══════════════════════════════════════════════════════════════════
        private static readonly bool DISABLED_BY_INCIDENT_20261105 = false;

        private readonly List<ShellPart> parts = new List<ShellPart>();
        private Material shellMat;
        private Transform shellRoot;
        private bool built;

        // 【雨天湿模式】湿层颜色（11-17 主理人反馈"雨天楼顶没有水坑效果"）。
        // 湿混凝土/沥青屋顶雨中的核心观感 = 整体变深 + 略带冷蓝；雪/湿切换时还原。
        private static readonly Color WetTint = new Color(0.26f, 0.29f, 0.34f, 1f);
        private static readonly Color WetSky = new Color(0.18f, 0.21f, 0.26f, 1f);
        private bool _wetShown;

        private static readonly int IdSnowTex = Shader.PropertyToID("_SnowTex");
        private static readonly int IdSnowDetail = Shader.PropertyToID("_DetailTex");
        private static readonly int IdSnowColor = Shader.PropertyToID("_SnowColor");
        private static readonly int IdSkyColor = Shader.PropertyToID("_SkyColor");
        private static readonly int IdSnowAmount = Shader.PropertyToID("_SnowAmount");
        private static readonly int IdEdgeFade = Shader.PropertyToID("_EdgeFade");
        private static readonly int IdGlobalSnow = Shader.PropertyToID("_GlobalSnow");
        private static readonly int IdMaskOff = Shader.PropertyToID("_MaskOff");
        private static readonly int IdCullProp = Shader.PropertyToID("_Cull");

        /// <summary>
        /// 雪壳共享材质（两条路径共用）。参数的「为什么」见各行注释——
        /// _MaskOff 与 _Cull 是 v1 用两次失败换来的，动之前先读注释。
        /// </summary>
        private Material MakeShellMaterial()
        {
            var m = new Material(Shader.Find("CampusUI/SnowGround"));
            m.name = "RoofSnowShell";
            m.SetTexture(IdSnowTex, ProceduralAssets.SnowPatch);
            m.SetTexture(IdSnowDetail, ProceduralAssets.SnowDetail);
            m.SetColor(IdSnowColor, new Color(0.92f, 0.95f, 1.0f, 1f));
            m.SetColor(IdSkyColor, new Color(0.74f, 0.82f, 0.96f, 1f));
            m.SetFloat(IdSnowAmount, 0f);
            // 雪壳 UV 是世界坐标（几十的量程），_SnowTex 是 Clamp 遮罩——不关遮罩整片 discard（v1 实测教训）。
            m.SetFloat(IdMaskOff, 1f);
            // 剔除跟原屋顶一致（Cull Back 单面）：从室内看天花板不得露白（v1 实测教训）。
            m.SetFloat(IdCullProp, 2f);
            m.renderQueue = 2996;   // 与近场雪盘同族；屏幕 Geometry 2000 且 v2 不收室内面，无重叠
            m.SetFloat(IdEdgeFade, 1.0f);   // 逐三角形拼壳没有"边缘"概念，均匀铺满
            return m;
        }

        /// <summary>一个来源 Renderer 对应的一片壳。</summary>
        private class ShellPart
        {
            public Mesh mesh;
            public MeshRenderer renderer;
            public string sourceName;
            public int tris;
        }

        private void Start()
        {
            if (DISABLED_BY_INCIDENT_20261105)
            {
                enabled = false;
                return;
            }
            Build();
        }

        /// <summary>栅格键：世界 XZ → 1m 列。long 打包避免嵌套 Dictionary。</summary>
        private static long CellKey(int cx, int cz) { return ((long)cx << 32) ^ (uint)cz; }

        private void Build()
        {
            if (built) return;
            built = true;

            // ── 【两条铁律，白线事故】顶点全是世界坐标 → 钉死世界原点、脱离跟随相机层级 ──
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

            int keptParts = 0;

            // ══════════════════════════════════════════════════════════════
            // 【路径 A · 烘焙产物（首选）】编辑器烘焙的雪壳 prefab。
            // 为什么烘焙是首选：场景开了静态批处理，建筑的运行时网格全是
            // "Combined Mesh (root: scene)"（758 个、CPU 不可读）——运行时扫描
            // 只够到 55 个未合批小网格（树/门廊），楼顶永远扫不到（11-17 实测）。
            // 烘焙在编辑模式跑（无批处理、原始网格、编辑器读顶点不受 isReadable 限制），
            // 工具：SchoolTwin/雪壳/① 烘焙屋顶雪壳（RoofSnowBakeTool.cs）。
            // 场景改动后重跑一次烘焙即可刷新。
            // ══════════════════════════════════════════════════════════════
            var baked = Resources.Load<GameObject>("RoofSnowBake");
            if (baked != null)
            {
                var inst = Instantiate(baked, transform);
                inst.name = "RoofSnowBake";
                inst.transform.localPosition = Vector3.zero;
                inst.transform.localRotation = Quaternion.identity;
                inst.transform.localScale = Vector3.one;

                shellMat = MakeShellMaterial();
                var rs = inst.GetComponentsInChildren<MeshRenderer>();
                for (int i = 0; i < rs.Length; i++)
                {
                    // 烘焙产物里也过滤室内表面：RoofSnowShell_Floor2 就是二楼楼板顶面，
                    // 铺在楼梯口 = 一整块纯白平面（现场照片）。名字命中就直接销毁该片。
                    if (IsExcludedShellSource(rs[i].name)) { Destroy(rs[i].gameObject); continue; }
                    rs[i].sharedMaterial = shellMat;
                    rs[i].shadowCastingMode = ShadowCastingMode.Off;
                    rs[i].receiveShadows = true;
                    rs[i].lightProbeUsage = LightProbeUsage.Off;
                    rs[i].reflectionProbeUsage = ReflectionProbeUsage.Off;
                    rs[i].enabled = false;   // Update 按雪量统一开
                    parts.Add(new ShellPart { renderer = rs[i], sourceName = rs[i].name, tris = 0 });
                    keptParts++;
                }
                Debug.Log("[SnowOnRoofs v2] 使用烘焙产物：" + keptParts + " 片（RoofSnowBake.prefab，剔除室内 " + (rs.Length - keptParts) + " 片）");
                return;
            }

            // ── 路径 B · 运行时扫描（兜底）：只有未合批的可读网格（树/门廊等）会命中 ──
            BuildByScan(sh);
        }

        /// <summary>三层筛选 + 一楼一壳的运行时扫描（烘焙产物缺失时的兜底路径）。</summary>
        private void BuildByScan(Shader sh)
        {
            // ---- 共享材质（所有壳共用一份，Update 只写一次）----
            shellMat = MakeShellMaterial();

            // ══════════════════════════════════════════════════════════════
            // 第 1 层 · 粗筛：收集全部候选三角形（法线朝上 / 面积 / 世界高度）
            // ══════════════════════════════════════════════════════════════
            // 候选先入列表再过栅格，是因为"列最高"必须先看完全部候选才成立。
            var cand = new List<Cand>();
            MeshFilter[] filters = FindObjectsOfType<MeshFilter>();
            int scannedMeshes = 0, scannedTris = 0, skippedBig = 0;

            for (int fi = 0; fi < filters.Length; fi++)
            {
                MeshFilter mf = filters[fi];
                if (mf == null || mf.sharedMesh == null) continue;
                if (mf.gameObject == gameObject) continue;   // 重入保护

                // 室内表面不铺雪：二楼楼板（Floor2）顶面在栅格里就是"列顶"，
                // 会被判成屋顶 → 楼梯口出现一整块白片（实测）。这些名字直接出局。
                bool excluded = false;
                for (int k = 0; k < excludeSourceName.Length; k++)
                {
                    string ex = excludeSourceName[k];
                    if (!string.IsNullOrEmpty(ex) && mf.name.Contains(ex)) { excluded = true; break; }
                }
                if (excluded) continue;

                // 已用积雪着色器的几何（地面雪盘/远场盘/树根雪堆/其他雪壳）不收：
                // 收进来 = 同一块面两层半透明雪叠加，白度翻倍（v1 实测教训）。
                var selfRd = mf.GetComponent<Renderer>();
                if (selfRd == null || selfRd.sharedMaterial == null) continue;
                string shName = selfRd.sharedMaterial.shader != null ? selfRd.sharedMaterial.shader.name : "";
                if (shName == "CampusUI/SnowGround") continue;
                // 屏幕类材质 defensively 跳过（Monitor/TwinScreen 家族）——
                // 屏幕本身近乎竖直，法线筛也能挡，这里双保险零成本。
                if (shName.IndexOf("SchoolTwin/", System.StringComparison.Ordinal) == 0) continue;
                // 【11-17 v3】树冠叶卡不要：树模型（独立树 Tree1/2/3(Clone)）的叶片是
                // 2~5m 的大 alpha 卡，"列顶朝上面"就是整张卡 → 雪壳变成巨片白板穿过
                // 树冠（现场证伪，比半球帽更假）。树冠覆雪统一走 SnowOnTrees 的雪斑系统。
                if (shName.StartsWith("Hidden/Nature/", System.StringComparison.Ordinal)) continue;

                Mesh m = mf.sharedMesh;
                if (!m.isReadable) continue;   // 不可读网格读不到顶点，只能放弃

                Vector3[] mv = m.vertices;
                Vector3[] mn = m.normals;
                int[] mt = m.triangles;
                if (mv == null || mt == null || mt.Length < 3) continue;
                if (mn == null || mn.Length != mv.Length) continue;   // 无法可靠判朝向 → 整块跳过（宁可没雪不糊半楼）

                int triCount = mt.Length / 3;
                if (triCount > maxTrisPerMesh) { skippedBig++; continue; }

                Transform tr = mf.transform;
                Bounds wb = selfRd.bounds;
                // 整块网格的包围盒顶都够不到门槛 → 连扫都不用扫
                if (wb.max.y < minWorldY) continue;

                // 本网格自身竖向跨度（第 3 层相对高度的分母）。跨度 <2m 的网格（薄片/雨棚）
                // 相对高度没有意义，直接给 1.0 放行，交给世界高度与列顶判。
                float span = Mathf.Max(2f, wb.max.y - wb.min.y);

                scannedMeshes++;

                for (int t = 0; t + 2 < mt.Length; t += 3)
                {
                    scannedTris++;
                    int i0 = mt[t], i1 = mt[t + 1], i2 = mt[t + 2];
                    if (i0 >= mv.Length || i1 >= mv.Length || i2 >= mv.Length) continue;

                    Vector3 w0 = tr.TransformPoint(mv[i0]);
                    Vector3 w1 = tr.TransformPoint(mv[i1]);
                    Vector3 w2 = tr.TransformPoint(mv[i2]);

                    float minY = Mathf.Min(w0.y, Mathf.Min(w1.y, w2.y));
                    if (minY < minWorldY) continue;
                    if ((minY - wb.min.y) / span < relMinRatio) continue;   // 第 3 层先粗挡（省栅格）

                    Vector3 cross = Vector3.Cross(w1 - w0, w2 - w0);
                    if (cross.magnitude * 0.5f < minTriArea) continue;

                    Vector3 wn = (tr.TransformDirection(mn[i0]) +
                                  tr.TransformDirection(mn[i1]) +
                                  tr.TransformDirection(mn[i2])) / 3f;
                    if (wn.sqrMagnitude < 1e-8f) continue;
                    wn.Normalize();
                    if (wn.y < minUpNormal) continue;

                    var c = new Cand
                    {
                        w0 = w0, w1 = w1, w2 = w2,
                        minY = minY,
                        cx = Mathf.FloorToInt(((w0.x + w1.x + w2.x) / 3f) / cellSize),
                        cz = Mathf.FloorToInt(((w0.z + w1.z + w2.z) / 3f) / cellSize),
                        src = mf,
                    };
                    cand.Add(c);
                }
            }

            // ══════════════════════════════════════════════════════════════
            // 第 2 层 · 逐列最高朝上面：登记每列的最高候选 minY，只留列顶
            // ══════════════════════════════════════════════════════════════
            var colTop = new Dictionary<long, float>();
            for (int i = 0; i < cand.Count; i++)
            {
                long key = CellKey(cand[i].cx, cand[i].cz);
                float cur;
                if (!colTop.TryGetValue(key, out cur) || cand[i].minY > cur)
                    colTop[key] = cand[i].minY;
            }

            int keptTris = 0;
            var bySource = new Dictionary<MeshFilter, List<Cand>>();
            for (int i = 0; i < cand.Count; i++)
            {
                float top;
                if (colTop.TryGetValue(CellKey(cand[i].cx, cand[i].cz), out top)
                    && cand[i].minY >= top - topTolerance)
                {
                    List<Cand> l;
                    if (!bySource.TryGetValue(cand[i].src, out l))
                    {
                        l = new List<Cand>();
                        bySource[cand[i].src] = l;
                    }
                    l.Add(cand[i]);
                }
            }

            // ══════════════════════════════════════════════════════════════
            // 一楼一壳：每个来源 Renderer 一片，子物体钉在世界原点局部零位
            // ══════════════════════════════════════════════════════════════
            foreach (var kv in bySource)
            {
                List<Cand> l = kv.Value;
                if (l.Count == 0) continue;

                // 二次排除：源 Renderer 名（与雪壳命名同源），室内楼板在这里一定能拦住
                bool exSkip = false;
                for (int k = 0; k < excludeSourceName.Length; k++)
                {
                    string ex = excludeSourceName[k];
                    if (!string.IsNullOrEmpty(ex) && kv.Key.name.Contains(ex)) { exSkip = true; break; }
                }
                if (exSkip) continue;

                var verts = new List<Vector3>(l.Count * 3);
                var norms = new List<Vector3>(l.Count * 3);
                var uvs = new List<Vector2>(l.Count * 3);
                var tris = new List<int>(l.Count * 3);

                for (int i = 0; i < l.Count; i++)
                {
                    Vector3 w0 = l[i].w0, w1 = l[i].w1, w2 = l[i].w2;
                    int b = verts.Count;
                    verts.Add(w0 + Vector3.up * lift);
                    verts.Add(w1 + Vector3.up * lift);
                    verts.Add(w2 + Vector3.up * lift);

                    // 法线一律给正上方：薄雪壳整体朝上受光，坡屋顶不会偏灰（v1 结论沿用）
                    norms.Add(Vector3.up);
                    norms.Add(Vector3.up);
                    norms.Add(Vector3.up);

                    // UV 用世界 XZ 缩放（与地面雪盘同一套尺度，纹理连续）
                    const float uvScale = 1f / 6f;
                    uvs.Add(new Vector2(w0.x * uvScale, w0.z * uvScale));
                    uvs.Add(new Vector2(w1.x * uvScale, w1.z * uvScale));
                    uvs.Add(new Vector2(w2.x * uvScale, w2.z * uvScale));

                    tris.Add(b); tris.Add(b + 1); tris.Add(b + 2);
                }

                var mesh = new Mesh();
                mesh.name = "RoofSnowShell_" + kv.Key.name;
                mesh.indexFormat = IndexFormat.UInt32;   // 顶点可超 65535
                mesh.SetVertices(verts);
                mesh.SetNormals(norms);
                mesh.SetUVs(0, uvs);
                mesh.SetTriangles(tris, 0);
                mesh.RecalculateBounds();

                var go = new GameObject("RoofSnowShell_" + kv.Key.name);
                go.transform.SetParent(transform, false);   // 容器在世界原点 → 局部零位 = 世界坐标
                var mf = go.AddComponent<MeshFilter>();
                var mr = go.AddComponent<MeshRenderer>();
                mf.sharedMesh = mesh;
                mr.sharedMaterial = shellMat;
                mr.shadowCastingMode = ShadowCastingMode.Off;
                mr.receiveShadows = true;
                mr.lightProbeUsage = LightProbeUsage.Off;
                mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
                mr.enabled = false;   // 初始不渲染，Update 按雪量统一开

                parts.Add(new ShellPart { mesh = mesh, renderer = mr, sourceName = kv.Key.name, tris = l.Count });
                keptTris += l.Count;
            }

            if (keptTris == 0)
            {
                Debug.LogWarning("[SnowOnRoofs v2] 没有符合条件的暴露朝上面，屋顶积雪不显示。"
                               + "扫描 " + scannedMeshes + " 网格 / " + scannedTris + " 三角形"
                               + (skippedBig > 0 ? "；因超大跳过 " + skippedBig + " 网格" : ""));
                enabled = false;
                return;
            }

            Debug.Log("[SnowOnRoofs v2] 雪壳已生成：" + parts.Count + " 片 / " + keptTris + " 三角形"
                    + "（扫描 " + scannedMeshes + " 网格 " + scannedTris + " 三角形"
                    + (skippedBig > 0 ? "，超大跳过 " + skippedBig : "") + "）\n"
                    + "  明细：" + BuildDetailLine());
        }

        /// <summary>源名命中排除名单（室内楼板/楼梯/顶棚）：两种 Build 路径共用。</summary>
        private bool IsExcludedShellSource(string nm)
        {
            for (int k = 0; k < excludeSourceName.Length; k++)
            {
                string ex = excludeSourceName[k];
                if (!string.IsNullOrEmpty(ex) && nm != null && nm.Contains(ex)) return true;
            }
            return false;
        }

        private string BuildDetailLine()
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < parts.Count && i < 12; i++)
                sb.Append(parts[i].sourceName).Append('=').Append(parts[i].tris).Append("  ");
            if (parts.Count > 12) sb.Append("…共 ").Append(parts.Count);
            return sb.ToString();
        }

        /// <summary>粗筛候选的临时结构（第 2 层栅格过滤的前置）。</summary>
        private class Cand
        {
            public Vector3 w0, w1, w2;
            public float minY;
            public int cx, cz;
            public MeshFilter src;
        }

        private void Update()
        {
            if (!built || shellMat == null || parts.Count == 0) return;

            // ══════════════════════════════════════════════════════════════
            // 【三态：雪 / 雨·湿 / 干燥】（11-17 主理人反馈"雨天楼顶没有水坑效果"）
            //
            // 同一片壳网格复用三种呈现，不新增几何：
            //   · 雪（_GlobalSnow > 0.01）→ 白色积雪，量随全局雪量；
            //   · 雨（天气=Rain/Storm 且不在下雪）→ **湿模式**：同一网格铺一层
            //     深灰蓝（湿混凝土观感），_SnowAmount 保持高值让片元不被 discard，
            //     _DetailTex 噪声自带斑驳 → 近似"低洼处水痕分布不均"；
            //     真实楼顶雨中就是整体变深，全覆盖深色已是八成观感，
            //     带反射的水坑留待后续按需加（避免第一版就上重材质）。
            //   · 干燥 → 隐藏（现状）。
            // 雪与雨互斥由数据侧 Weather 单值保证，不会双开。
            // ══════════════════════════════════════════════════════════════
            float snow = Shader.GetGlobalFloat(IdGlobalSnow);
            bool snowOn = snow > 0.01f;

            bool raining = false;
            if (!snowOn)
            {
                var env = EnvironmentState.Instance;
                var w = env != null ? env.weather : Weather.Clear;
                raining = w == Weather.Rain || w == Weather.Storm;
            }
            bool show = snowOn || raining;

            for (int i = 0; i < parts.Count; i++)
            {
                if (parts[i].renderer == null) continue;
                if (show != parts[i].renderer.enabled) parts[i].renderer.enabled = show;
            }

            if (snowOn)
            {
                if (_wetShown) RestoreSnowTint();   // 雨→雪切回，别把湿色留在雪上
                _wetShown = false;
                shellMat.SetFloat(IdSnowAmount, snow);
            }
            else if (raining)
            {
                if (!_wetShown)
                {
                    shellMat.SetColor(IdSnowColor, WetTint);
                    shellMat.SetColor(IdSkyColor, WetSky);
                    _wetShown = true;
                }
                // 覆盖率保持高值：低 _SnowAmount 会在片元里整片 discard（v1 教训），湿层就没了
                shellMat.SetFloat(IdSnowAmount, 0.9f);
            }
        }

        private void RestoreSnowTint()
        {
            shellMat.SetColor(IdSnowColor, new Color(0.92f, 0.95f, 1.0f, 1f));
            shellMat.SetColor(IdSkyColor, new Color(0.74f, 0.82f, 0.96f, 1f));
        }

        private void OnDestroy()
        {
            for (int i = 0; i < parts.Count; i++)
            {
                if (parts[i].mesh != null) Destroy(parts[i].mesh);
            }
            parts.Clear();
            if (shellMat != null) Destroy(shellMat);
        }
    }
}

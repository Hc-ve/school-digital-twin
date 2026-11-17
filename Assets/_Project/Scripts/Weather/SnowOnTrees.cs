using UnityEngine;
using UnityEngine.Rendering;   // ShadowCastingMode / LightProbeUsage / ReflectionProbeUsage 都在这个命名空间
using System.Collections.Generic;

namespace SchoolTwin.Weather
{
    /// <summary>
    /// 植被(树)积雪：读取地形 TreeInstance，在每棵树根生成一块"世界锁定"的雪堆圆盘，
    /// 随全局雪量(_GlobalSnow，由 WeatherVisuals 每帧写入)出现/消失。
    ///
    /// 【P43 追加 11-17 主理人反馈】树冠覆雪 v3：叶卡表面**撒雪斑**（v2 贴体雪层已证伪）。
    ///
    /// ── 两版失败的教训（都在，防止走回头路）──────────────────────────
    ///   · v2 半球帽：均匀白色大圆盘悬在树冠上——与真实形态（雪挂在枝叶上表面、
    ///     呈不连续簇状、叶簇间露底）完全不符，主理人评"太假"。
    ///   · v2.5 贴体雪层（复制原型叶卡三角形沿法线外扩）：本素材的树叶不是细碎
    ///     叶面，而是**大面积 alpha 叶卡**（单张 2~5m 的整片卡片）——按面积取最大
    ///     的雪层直接变成巨片白色平板穿过树冠，比帽子更假，当场证伪。
    ///   ⇒ 结论：**在叶卡表面撒独立雪斑**，而不是把叶卡本身复制成雪层。
    ///
    /// ── v3 做法 ────────────────────────────────────────────────────────
    ///   ① 取原型树冠区的朝上叶卡（法线朝上 ≥0.3、树冠区 Y ≥ 树高×0.35）；
    ///   ② 每张叶卡按 0.3m 网格细分采样，Perlin 噪声保留约一半采样点
    ///      （覆盖率可调）——雪斑呈簇状分布，暗叶从缝隙露底；
    ///   ③ 每个雪斑 = 一张 0.22~0.45m 的小方片，贴在叶卡表面上方 0.08~0.16m，
    ///      随机翻滚角 + 随机尺寸，UV 用 SnowPatch 的径向渐隐遮罩 → 边缘融化感；
    ///   ④ 每棵树限量（缺省 60 片），按叶卡面积优先 → 雪挂在外层大叶簇上；
    ///   ⑤ 按每棵树的 rotation/widthScale/heightScale 变换 → 雪与树冠对位
    ///      （v2 半球帽没做这两个变换，矮树的帽子浮空——教训）。
    ///   · 材质复用 CampusUI/SnowGround（_MaskOff=0 走 SnowPatch 径向渐隐，
    ///     与地面雪斑同一套观感）；独立几何零材质污染（地形树原型材质是共享资产）。
    ///   · 独立树（场景树）由 SnowOnRoofs v2 的列顶判据覆盖，两路互补。
    ///
    /// 设计要点（雪堆部分，P13 原案）：
    ///  · 不修改任何内置着色器；所有树根雪堆合并进一个 mesh，仅 1 个 draw call。
    ///
    /// 用法：把本组件挂到场景里任意一个激活的 GameObject 上(建议与 WeatherVisuals 同级)即可。
    /// </summary>
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class SnowOnTrees : MonoBehaviour
    {
        [Tooltip("每棵树根雪堆半径(米)")]
        public float pileRadius = 3.0f;
        [Tooltip("单块雪堆圆盘分段(越大越圆)")]
        public int seg = 16;
        [Tooltip("雪堆中心最大隆起高度(米)")]
        public float pileHeight = 0.18f;

        [Header("树冠雪团（v4，有体积的团块）")]
        [Tooltip("叶卡法线 Y 下限。⚠️必须放到负值：侧向叶卡是树冠轮廓，只有它们能提供侧视可见的白边；0.15 会把轮廓卡全部筛掉（实测侧视整棵无雪）")]
        public float foliageMinUpNormal = -0.10f;
        [Tooltip("树冠区 = 树冠竖向跨度的顶部此比例（雪挂冠顶；教训：按'从底部 35% 起全收'会全选冠底大叶卡，冠顶反而没雪——面积最大的叶卡集中在冠底）")]
        // ⚠️ 这个值是"保留树冠顶部往下多少比例"。0.15 = 只留顶部 15% 的叶卡，
        //    结果 50 张卡全在冠顶 → 雪只堆在冠顶，人眼高度侧面看不到（v4 卡了 8 轮的根因）。
        //    冠面 3.6~8m 都要有雪，取 0.55。
        [Range(0.05f, 0.9f)]
        public float foliageCanopyMinK = 0.85f;
        [Tooltip("每原型最多参与的叶卡数（按面积降序取）")]
        [Range(20, 400)]
        public int foliageMaxCardsPerProto = 300;
        [Tooltip("每棵树最多多少个雪团（预算闸）")]
        [Range(10, 200)]
        public int foliageMaxLumpsPerTree = 0;
        [Tooltip("雪团采样网格步长(米)——越小越密")]
        [Range(0.15f, 0.9f)]
        public float lumpStep = 0.55f;
        [Tooltip("雪团保留率（Perlin 阈值），1=铺满 0.4=稀疏簇状")]
        [Range(0.2f, 1f)]
        public float lumpKeepRatio = 0.55f;
        [Tooltip("雪团半径下限(米)")]
        public float lumpSizeMin = 0.08f;
        [Tooltip("雪团半径上限(米)")]
        public float lumpSizeMax = 0.22f;
        [Tooltip("雪团竖向压扁系数（0.35~0.55：雪是堆起来的，不是球）")]
        [Range(0.2f, 1.2f)]
        public float lumpSquash = 0.85f;
        [Tooltip("雪团贴面下压比例：正值=埋进叶面，只露上半（0 = 正好贴面）")]
        [Range(-0.2f, 0.6f)]
        public float lumpSink = 0.45f;
        [Tooltip("是否给树干/主枝也挂雪（单侧白斑）")]
        public bool trunkSnow = true;
        [Tooltip("枝条积雪：参考图的雪主要长在枝条上缘（细长白条），不是叶子上的白片")]
        public bool branchSnow = true;
        [Tooltip("枝梢雪帽：参考图那种一簇一簇的厚雪团（v7 主路线）")]
        public bool snowCaps = true;
        [Tooltip("每棵树的雪帽簇数")]
        public int capClustersPerTree = 30;
        [Tooltip("簇内雪团的水平散布半径(米)")]
        public float capSpread = 0.20f;
        [Tooltip("单块雪片的轮廓半径下限(米)：太小会整块埋进叶缝里看不见")]
        public float capRadiusMin = 0.18f;
        [Tooltip("单块雪片的轮廓半径上限(米)")]
        public float capRadiusMax = 0.34f;
        [Tooltip("树冠雪被：罩在外轮廓上的连续雪层（调研后的正确路线，取代'一树饺子'的几何雪团）")]
        public bool canopyShell = false;
        [Tooltip("雪被向外扩出叶幕的距离(米)")]
        public float canopyOut = 0.06f;
        [Tooltip("雪被向上抬升(米)")]
        public float canopyLift = 0.10f;
        [Tooltip("雪被裁形阈值：越高雪越少。新 mask = 朝上 × 覆盖率 × 噪声斑块，1.0 左右约为三成白")]
        public float canopyAlphaCut = 1.0f;
        [Tooltip("每棵树的枝条雪条条数")]
        public int branchSnowPerTree = 280;
        [Tooltip("枝条雪条半径(米)")]
        public float branchSnowSize = 0.13f;
        [Tooltip("雪条沿枝条方向的拉伸倍数")]
        public float branchSnowStretch = 2.2f;
        [Tooltip("枝条雪条只取朝上枝面的法线 y 阈值")]
        public float branchSnowMinUp = 0.18f;
        [Tooltip("叶面雪壳层：雪的『覆盖面』。v5 修正后开启——抬升改世界 +Y、着色兜底 0.86、Cull Off")]
        public bool shellLayer = false;
        [Tooltip("树干雪斑半径(米)")]
        [Range(0.04f, 0.35f)]
        public float trunkPatchSize = 0.12f;
        [Tooltip("树干雪斑沿高度的间距(米)")]
        [Range(0.2f, 2f)]
        public float trunkPatchStep = 0.75f;
        [Tooltip("树干雪斑朝哪个方向（世界 XZ 分量的风向；雪挂在迎风面）")]
        public Vector2 trunkWindDir = new Vector2(-0.7f, -0.7f);

        private Mesh pileMesh;
        private Material pileMat;
        private MeshRenderer pileMr;
        private Mesh folMesh;
        private Material clumpMat;
        private MeshRenderer folMr;
        private Mesh shellMesh;
        private Material shellMat;
        private MeshRenderer shellMr;
        private Texture leafTex;
        private bool built;

        // 原型叶卡模板缓存：prototypeIndex → 叶卡列表（prefab 根空间，含法线）
        private readonly Dictionary<int, List<FoliageCard>> protoCards =
            new Dictionary<int, List<FoliageCard>>();
        // 原型的树干/主枝竖直面缓存（挂树干雪斑用）
        private readonly Dictionary<int, List<FoliageCard>> protoBark =
            new Dictionary<int, List<FoliageCard>>();

        private static readonly int IdSnowTex = Shader.PropertyToID("_SnowTex");
        private static readonly int IdSnowDetail = Shader.PropertyToID("_DetailTex");
        private static readonly int IdSnowColor = Shader.PropertyToID("_SnowColor");
        private static readonly int IdColorProp = Shader.PropertyToID("_Color");
        // 雪天"树皮提亮"用：参考图的树干是灰褐色，我们的树皮材质本身很暗，环境光再高也拉不起来。
        // 提亮写在 Update 里、按雪量插值，退出 Play 自动恢复（没有改资产）。
        private readonly System.Collections.Generic.List<Material> barkMats = new System.Collections.Generic.List<Material>();
        private static readonly int IdSkyColor = Shader.PropertyToID("_SkyColor");
        private static readonly int IdShadeColor = Shader.PropertyToID("_ShadeColor");
        private static readonly int IdGrow = Shader.PropertyToID("_Grow");
        private static readonly int IdCoverage = Shader.PropertyToID("_Coverage");
        private static readonly int IdSnowAmount = Shader.PropertyToID("_SnowAmount");
        private static readonly int IdEdgeFade = Shader.PropertyToID("_EdgeFade");
        private static readonly int IdGlobalSnow = Shader.PropertyToID("_GlobalSnow");

        private void Start() { Build(); }

        private void Build()
        {
            if (built) return;
            built = true;

            Terrain terrain = Terrain.activeTerrain;
            if (terrain == null || terrain.terrainData == null) { enabled = false; return; }
            var td = terrain.terrainData;
            var instances = td.treeInstances;
            if (instances == null || instances.Length == 0) { enabled = false; return; }

            // 收集树皮材质，供雪天"树干提亮"用（参考图树干是灰褐色，不是剪影）
            for (int i = 0; i < td.treePrototypes.Length; i++)
            {
                var proto = td.treePrototypes[i];
                if (proto == null || proto.prefab == null) continue;
                foreach (var mr in proto.prefab.GetComponentsInChildren<MeshRenderer>(true))
                {
                    if (mr == null) continue;
                    foreach (var mat in mr.sharedMaterials)
                    {
                        if (mat == null || mat.shader == null) continue;
                        if (mat.shader.name.Contains("Bark") || mat.shader.name.Contains("Tree Creator Bark"))
                            if (!barkMats.Contains(mat)) barkMats.Add(mat);
                    }
                }
            }

            // ══════════════════════════════════════════════════════════════════
            // 【P13 修的真 bug：室内外那条横贯全屏的"诡异白线"就是这里来的】
            // 顶点全部用世界坐标 → transform 必须钉死世界原点并脱离跟随相机层级。
            // ══════════════════════════════════════════════════════════════════
            transform.SetParent(null, true);
            transform.position = Vector3.zero;
            transform.rotation = Quaternion.identity;
            transform.localScale = Vector3.one;

            var tpos = terrain.GetPosition();
            float w = td.size.x, d = td.size.z;

            int vertsPer = (seg + 1) * (seg + 1);
            int triPer = seg * seg * 6;
            int totalV = vertsPer * instances.Length;
            int totalT = triPer * instances.Length;
            var verts = new Vector3[totalV];
            var uvs = new Vector2[totalV];
            var norms = new Vector3[totalV];
            var tris = new int[totalT];

            // 雪团预算：每树 ≤ foliageMaxLumpsPerTree 个半球壳（15 顶点 / 21 三角）
            var folVerts = new List<Vector3>(foliageMaxLumpsPerTree * 16 * instances.Length);
            var folUvs = new List<Vector2>(foliageMaxLumpsPerTree * 16 * instances.Length);
            var folNorms = new List<Vector3>(foliageMaxLumpsPerTree * 16 * instances.Length);
            var folTris = new List<int>(foliageMaxLumpsPerTree * 24 * instances.Length);

            // 叶面雪壳：直接复制真实叶卡几何（顶点数 = 叶卡三角 x3），所以另开一组容器
            var shVerts = new List<Vector3>(100000);
            var shUvs = new List<Vector2>(100000);
            var shNorms = new List<Vector3>(100000);
            var shTris = new List<int>(150000);

            int vOff = 0, tOff = 0;
            for (int t = 0; t < instances.Length; t++)
            {
                var inst = instances[t];
                float wx = tpos.x + inst.position.x * w;
                float wz = tpos.z + inst.position.z * d;
                // ⚠️【P13】必须加 terrain.GetPosition().y：SampleHeight 返回相对高度，
                // 漏加会让雪堆整体抬高 0.5m（本场景地形 m_LocalPosition.y=-0.5）。
                float wy = tpos.y + terrain.SampleHeight(new Vector3(wx, 0f, wz)) + 0.02f;

                int idx0 = vOff;
                for (int z = 0; z <= seg; z++)
                {
                    for (int x = 0; x <= seg; x++)
                    {
                        float u = (float)x / seg - 0.5f;
                        float v = (float)z / seg - 0.5f;
                        float lx = u * 2f * pileRadius;
                        float lz = v * 2f * pileRadius;
                        float r = Mathf.Sqrt(u * u + v * v) * 2f;
                        float bump = (1f - r) * pileHeight;
                        bump += (Mathf.PerlinNoise(wx * 0.2f, wz * 0.2f) - 0.5f) * 0.08f;
                        verts[vOff] = new Vector3(wx + lx, wy + bump, wz + lz);
                        uvs[vOff] = new Vector2((float)x / seg, (float)z / seg);
                        norms[vOff] = Vector3.up;
                        vOff++;
                    }
                }
                for (int z = 0; z < seg; z++)
                {
                    for (int x = 0; x < seg; x++)
                    {
                        int a = idx0 + z * (seg + 1) + x;
                        int b = a + 1;
                        int c = a + (seg + 1);
                        int e = c + 1;
                        tris[tOff++] = a; tris[tOff++] = c; tris[tOff++] = b;
                        tris[tOff++] = b; tris[tOff++] = c; tris[tOff++] = e;
                    }
                }

                // ---- 树冠雪团 + 树干雪斑：地形树按 (位置/旋转/两轴缩放) 组矩阵后盖章 ----
                List<FoliageCard> cards = ProtoCards(inst.prototypeIndex, td);

                var toWorld = Matrix4x4.TRS(
                    new Vector3(wx, wy, wz),
                    Quaternion.Euler(0f, inst.rotation * Mathf.Rad2Deg, 0f),
                    new Vector3(inst.widthScale, inst.heightScale, inst.widthScale));

                if (cards != null && cards.Count > 0)
                {
                    EmitCardLumps(cards, toWorld, t * 13.7f, folVerts, folUvs, folNorms, folTris);
                    // 【主路径】35 棵地形树都走这里，雪帽必须挂在这，挂在克隆体路径等于没做
                    // 雪帽定位必须用"全部叶子"：只算朝上叶子会漏掉冠顶那些朝侧/朝下的叶卡，
                    // 雪帽就被压在叶幕下面（隐藏地形树的诊断证实了这一点）
                    if (snowCaps) EmitSnowCaps(ProtoCards(inst.prototypeIndex, td, true), toWorld, t * 13.7f, folVerts, folUvs, folNorms, folTris);
                    if (canopyShell) EmitCanopyShell(cards, toWorld, t * 13.7f, shVerts, shUvs, shNorms, shTris);
                    if (shellLayer) EmitCardShell(cards, toWorld, t * 13.7f, shVerts, shUvs, shNorms, shTris);
                }
                EmitTrunkSnow(inst.prototypeIndex, td, toWorld, t * 13.7f,
                    folVerts, folUvs, folNorms, folTris);
            }

            // ---- 独立树（场景里的 Tree1/2/3(Clone) 等）：同一套雪斑 ----
            // 它们与地形原型同源（同 prefab），模板直接复用；实例变换用克隆体
            // 自身的 localToWorldMatrix（位置/旋转/缩放全在里面），无需 SampleHeight。
            var treeRds = FindObjectsOfType<MeshRenderer>();
            foreach (var trd in treeRds)
            {
                if (trd == null || trd.sharedMaterial == null || trd.sharedMaterial.shader == null) continue;
                if (trd.sharedMaterial.shader.name != "Hidden/Nature/Tree Creator Bark Optimized") continue;

                var cloneRoot = trd.transform.root;

                // 【必须跳过批量阴影代理】场景里 "TreeShadows" 一个根下挂了 35 个树皮 Renderer
                // （给地形树补投影用的代理），它们 root 相同 → 用 root.localToWorldMatrix 会在
                // 世界原点叠 35 份一模一样的雪（实测雪量翻倍、还全挤在原点）。
                // 判据：同一个 root 下有多份树皮渲染器 = 批处理代理，真树已由 terrain 实例覆盖。
                int barkUnderRoot = 0;
                foreach (var r2 in cloneRoot.GetComponentsInChildren<MeshRenderer>(true))
                {
                    if (r2 == null || r2.sharedMaterial == null || r2.sharedMaterial.shader == null) continue;
                    if (r2.sharedMaterial.shader.name == "Hidden/Nature/Tree Creator Bark Optimized") barkUnderRoot++;
                }
                if (barkUnderRoot > 1) continue;

                string baseName = cloneRoot.name.Replace("(Clone)", "").Trim();
                int protoIdx = -1;
                for (int i = 0; i < td.treePrototypes.Length; i++)
                    if (td.treePrototypes[i].prefab.name == baseName) { protoIdx = i; break; }
                if (protoIdx < 0) continue;

                List<FoliageCard> cards = ProtoCards(protoIdx, td);

                if (cards != null && cards.Count > 0)
                {
                    EmitCardLumps(cards, cloneRoot.localToWorldMatrix,
                        cloneRoot.position.x * 3.7f + cloneRoot.position.z * 5.3f,
                        folVerts, folUvs, folNorms, folTris);
                    if (snowCaps)
                        EmitSnowCaps(cards, cloneRoot.localToWorldMatrix,
                            cloneRoot.position.x * 3.7f + cloneRoot.position.z * 5.3f,
                            folVerts, folUvs, folNorms, folTris);
                    if (canopyShell)
                        EmitCanopyShell(cards, cloneRoot.localToWorldMatrix,
                            cloneRoot.position.x * 3.7f + cloneRoot.position.z * 5.3f,
                            shVerts, shUvs, shNorms, shTris);
                }
                EmitTrunkSnow(protoIdx, td, cloneRoot.localToWorldMatrix,
                    cloneRoot.position.x * 3.7f + cloneRoot.position.z * 5.3f,
                    folVerts, folUvs, folNorms, folTris);
                EmitBranchSnow(protoIdx, td, cloneRoot.localToWorldMatrix,
                    cloneRoot.position.x * 4.1f + cloneRoot.position.z * 6.7f,
                    folVerts, folUvs, folNorms, folTris);
            }

            pileMesh = new Mesh();
            pileMesh.name = "TreeSnowPiles";
            pileMesh.indexFormat = IndexFormat.UInt32;
            pileMesh.vertices = verts;
            pileMesh.uv = uvs;
            pileMesh.normals = norms;
            pileMesh.triangles = tris;
            pileMesh.RecalculateBounds();

            var mf = GetComponent<MeshFilter>();
            pileMr = GetComponent<MeshRenderer>();
            if (mf == null) mf = gameObject.AddComponent<MeshFilter>();
            if (pileMr == null) pileMr = gameObject.AddComponent<MeshRenderer>();
            mf.sharedMesh = pileMesh;

            var sh = Shader.Find("CampusUI/SnowGround");
            if (sh == null)
            {
                Debug.LogWarning("[SnowOnTrees] 找不到 CampusUI/SnowGround 着色器，树根积雪不可用。");
                enabled = false; return;
            }
            pileMat = new Material(sh);
            pileMat.name = "TreeSnowPile";
            pileMat.SetTexture(IdSnowTex, ProceduralAssets.SnowPatch);
            pileMat.SetTexture(IdSnowDetail, ProceduralAssets.SnowDetail);
            pileMat.SetColor(IdSnowColor, new Color(0.92f, 0.95f, 1.0f, 1f));
            pileMat.SetColor(IdSkyColor, new Color(0.74f, 0.82f, 0.96f, 1f));
            pileMat.SetFloat(IdSnowAmount, 0f);
            pileMat.SetFloat(IdEdgeFade, 1.0f);
            pileMat.renderQueue = 2997;   // 在近场/远场雪盘之上

            if (pileMr != null)
            {
                pileMr.sharedMaterial = pileMat;
                pileMr.shadowCastingMode = ShadowCastingMode.Off;
                pileMr.receiveShadows = true;
                pileMr.lightProbeUsage = LightProbeUsage.Off;
                pileMr.reflectionProbeUsage = ReflectionProbeUsage.Off;
                pileMr.enabled = false;         // 初始不渲染；由 Update 按雪量开关
            }

            // ---- 雪团 mesh：独立【不透明】材质 ----
            // ⚠️ 不能复用 pileMat（CampusUI/SnowGround 是 Queue=Transparent + Blend SrcAlpha
            //    + 径向渐隐），拿它画团块就会变成 P43 图里那种"冠外白气球"。
            if (folVerts.Count >= 3)
            {
                folMesh = new Mesh();
                folMesh.name = "TreeSnowClump";
                folMesh.indexFormat = IndexFormat.UInt32;
                folMesh.SetVertices(folVerts);
                folMesh.SetUVs(0, folUvs);
                folMesh.SetNormals(folNorms);
                folMesh.SetTriangles(folTris, 0);
                folMesh.RecalculateBounds();

                var clumpShader = Shader.Find("CampusUI/SnowClump");
                if (clumpShader == null)
                {
                    Debug.LogWarning("[SnowOnTrees] 找不到 CampusUI/SnowClump，树冠雪团不可用。");
                }
                else
                {
                    clumpMat = new Material(clumpShader);
                    clumpMat.name = "TreeSnowClumpMat";
                    clumpMat.SetColor(IdSnowColor, new Color(0.87f, 0.91f, 0.96f, 1f));
                    clumpMat.SetColor(IdShadeColor, new Color(0.66f, 0.74f, 0.87f, 1f));
                    clumpMat.SetFloat(IdGrow, 0f);
                    clumpMat.renderQueue = 2000;   // 几何队列：不透明，先于半透叶卡画完

                    var folGo = new GameObject("TreeSnowClumpGO");
                    folGo.transform.SetParent(transform, false);   // 本物体已钉世界原点，子物体局部零位=世界坐标
                    var fmf = folGo.AddComponent<MeshFilter>();
                    folMr = folGo.AddComponent<MeshRenderer>();
                    fmf.sharedMesh = folMesh;
                    folMr.sharedMaterial = clumpMat;
                    folMr.shadowCastingMode = ShadowCastingMode.Off;
                    folMr.receiveShadows = true;
                    folMr.lightProbeUsage = LightProbeUsage.Off;
                    folMr.reflectionProbeUsage = ReflectionProbeUsage.Off;
                    folMr.enabled = false;
                }
            }

            // ---- 叶面雪壳 mesh：跟叶卡同形，材质用叶纹理 alpha 裁形 ----
            if (shVerts.Count >= 3)
            {
                shellMesh = new Mesh();
                shellMesh.name = "TreeSnowShell";
                shellMesh.indexFormat = IndexFormat.UInt32;
                shellMesh.SetVertices(shVerts);
                shellMesh.SetUVs(0, shUvs);
                shellMesh.SetNormals(shNorms);
                shellMesh.SetTriangles(shTris, 0);
                shellMesh.RecalculateBounds();

                // 【P43 · v5 转向】雪被材质：不再是"逐叶卡贴片"，而是罩住树冠外轮廓的一层连续雪被。
                // 调研结论：树上雪 = 覆盖层，按法线朝上程度挂雪。逐叶摆球体只会得到"一树饺子"。
                var shellShader = Shader.Find("SchoolTwin/SnowCanopy");
                if (shellShader == null)
                {
                    Debug.LogWarning("[SnowOnTrees] 找不到 SchoolTwin/SnowCanopy，树冠雪被不可用。");
                }
                else
                {
                    shellMat = new Material(shellShader);
                    shellMat.name = "TreeSnowCanopyMat";
                    shellMat.SetColor(IdSnowColor, new Color(0.88f, 0.92f, 0.97f, 1f));
                    shellMat.SetColor(IdShadeColor, new Color(0.62f, 0.71f, 0.85f, 1f));
                    shellMat.SetFloat(IdCoverage, 0f);
                    shellMat.SetFloat("_AlphaCut", canopyAlphaCut);
                    shellMat.SetFloat("_NoiseScale", 4.0f);
                    shellMat.SetFloat("_EdgeNoise", 0.70f);
                    shellMat.renderQueue = 2000;

                    var shGo = new GameObject("TreeSnowShellGO");
                    shGo.transform.SetParent(transform, false);
                    var smf = shGo.AddComponent<MeshFilter>();
                    shellMr = shGo.AddComponent<MeshRenderer>();
                    smf.sharedMesh = shellMesh;
                    shellMr.sharedMaterial = shellMat;
                    shellMr.shadowCastingMode = ShadowCastingMode.Off;
                    shellMr.receiveShadows = true;
                    shellMr.lightProbeUsage = LightProbeUsage.Off;
                    shellMr.reflectionProbeUsage = ReflectionProbeUsage.Off;
                    shellMr.enabled = false;
                }
            }
        }

        /// <summary>
        /// 找树叶材质上的贴图（雪壳要靠它裁出叶子形状）。
        /// Nature/Tree Creator 树的叶材质用同一张叶图集，取第一张就够。
        /// </summary>
        private Texture FindLeafTexture(TerrainData td)
        {
            for (int i = 0; i < td.treePrototypes.Length; i++)
            {
                var proto = td.treePrototypes[i];
                if (proto == null || proto.prefab == null) continue;
                foreach (var mr in proto.prefab.GetComponentsInChildren<MeshRenderer>(true))
                {
                    if (mr == null) continue;
                    Material[] mats = mr.sharedMaterials;
                    if (mats == null) continue;
                    for (int k = 0; k < mats.Length; k++)
                    {
                        var mat = mats[k];
                        if (mat == null || mat.shader == null) continue;
                        if (mat.shader.name.IndexOf("Leaves", System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                        if (mat.mainTexture != null) return mat.mainTexture;
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// 叶面雪壳：复制叶卡三角，沿法线外抬 offset（6~14cm），UV 原样带过去。
        /// 这样雪的形状=叶子的形状，边缘是硬的（alpha test），不会出现"软边气球"。
        /// 重量比：这是覆盖面，雪团负责厚度，两者叠加才是参考图那种"积雪压着叶簇"。
        /// </summary>
        private void EmitCardShell(List<FoliageCard> cards, Matrix4x4 toWorld, float seed,
            List<Vector3> sv, List<Vector2> su, List<Vector3> sn, List<int> st)
        {
            for (int ci = 0; ci < cards.Count; ci++)
            {
                FoliageCard card = cards[ci];
                Vector3 a = toWorld.MultiplyPoint3x4(card.v0);
                Vector3 b = toWorld.MultiplyPoint3x4(card.v1);
                Vector3 c = toWorld.MultiplyPoint3x4(card.v2);
                Vector3 nW = toWorld.MultiplyVector(card.n).normalized;

                // ★ 抬升方向必须是【世界向上】，不能沿叶卡法线。
                //   叶卡法线是随机朝向的：沿法线抬会把壳推到叶子侧面/背面甚至叶子里面，
                //   实测（v4 红色诊断）壳几乎全被双面叶卡挡住，只在叶缝里透出一点暗红。
                //   雪是垂直落下堆积的，垂直 +Y 抬 3~6cm 才能稳定压在叶片上表面。
                float lift = 0.030f + 0.035f * Mathf.PerlinNoise((a.x + b.z) * 1.7f + seed, (c.x + a.z) * 1.7f - seed);
                Vector3 up = Vector3.up * lift;

                int bi = sv.Count;
                sv.Add(a + up);
                sv.Add(b + up);
                sv.Add(c + up);
                su.Add(card.uv0);
                su.Add(card.uv1);
                su.Add(card.uv2);
                sn.Add(nW); sn.Add(nW); sn.Add(nW);
                st.Add(bi); st.Add(bi + 1); st.Add(bi + 2);
            }
        }

        /// <summary>
        /// 取地形树原型的叶卡模板（prefab 根空间，懒缓存一次）。
        /// 筛选：法线朝上 ≥0.30、树冠区（局部 Y ≥ 树高×0.35）、按面积降序取前 N。
        /// </summary>
        private List<FoliageCard> ProtoCards(int idx, TerrainData td, bool anyNormal = false)
        {
            List<FoliageCard> cached;
            int cacheKey = anyNormal ? idx + 1000 : idx;
            if (protoCards.TryGetValue(cacheKey, out cached)) return cached;

            var proto = td.treePrototypes[idx];
            var result = new List<FoliageCard>();
            if (proto != null && proto.prefab != null)
            {
                var root = proto.prefab.transform;
                float yMax = -999f;

                var probe = new List<FoliageCard>();
                foreach (var pmf in proto.prefab.GetComponentsInChildren<MeshFilter>(true))
                {
                    var sm = pmf.sharedMesh;
                    if (sm == null || !sm.isReadable) continue;
                    var toRoot = root.worldToLocalMatrix * pmf.transform.localToWorldMatrix;
                    var mv = sm.vertices;
                    var mn = sm.normals;
                    var muv = sm.uv;
                    // 【子网格语义】Nature/Tree Creator prefab 是一个 mesh 两个子材质：
                    //   0 = 树皮（Hidden/Nature/Tree Creator Bark Optimized）
                    //   1 = 叶子（Hidden/Nature/Tree Creator Leaves Optimized）
                    // 叶卡只能取子网格 1，否则树皮上朝上的面也会被当成叶卡撒雪。
                    int leafSub = sm.subMeshCount > 1 ? 1 : 0;
                    int[] mt = sm.GetTriangles(leafSub);
                    if (mv == null || mt == null || mt.Length < 3 || mn == null || mn.Length != mv.Length) continue;

                    for (int t = 0; t + 2 < mt.Length; t += 3)
                    {
                        int i0 = mt[t], i1 = mt[t + 1], i2 = mt[t + 2];
                        if (i0 >= mv.Length || i1 >= mv.Length || i2 >= mv.Length) continue;
                        Vector3 w0 = toRoot.MultiplyPoint3x4(mv[i0]);
                        Vector3 w1 = toRoot.MultiplyPoint3x4(mv[i1]);
                        Vector3 w2 = toRoot.MultiplyPoint3x4(mv[i2]);
                        Vector3 n = (toRoot.MultiplyVector(mn[i0]) +
                                     toRoot.MultiplyVector(mn[i1]) +
                                     toRoot.MultiplyVector(mn[i2])) / 3f;
                        if (n.sqrMagnitude < 1e-8f) continue;
                        n.Normalize();
                        if (!anyNormal && n.y < foliageMinUpNormal) continue;
                        float topY = Mathf.Max(w0.y, Mathf.Max(w1.y, w2.y));
                        if (topY > yMax) yMax = topY;
                        float area = Vector3.Cross(w1 - w0, w2 - w0).magnitude * 0.5f;
                        probe.Add(new FoliageCard
                        {
                            v0 = w0, v1 = w1, v2 = w2,
                            uv0 = (muv != null && i0 < muv.Length) ? muv[i0] : Vector2.zero,
                            uv1 = (muv != null && i1 < muv.Length) ? muv[i1] : Vector2.zero,
                            uv2 = (muv != null && i2 < muv.Length) ? muv[i2] : Vector2.zero,
                            n = n, area = area, topY = topY
                        });
                    }
                }

                // 树冠区取【顶部区段】：topY ≥ 树高×(1−K)。树 prefab 根在地面（y=0），
                // ⚠️ 不能写成"从某个低位起全收"——面积最大的叶卡集中在冠底，
                // 那样前 N 名全是低位卡，冠顶反而一片雪都没有（11-17 俯拍实证）。
                float canopyBase = (yMax > 0f ? yMax : 8f) * (1f - foliageCanopyMinK);
                var kept = new List<FoliageCard>();
                for (int i = 0; i < probe.Count; i++)
                    if (probe[i].topY >= canopyBase) kept.Add(probe[i]);

                // 【只留"露天的顶面"】——真实世界雪只落在能看到天的上表面；
                // 树冠内部的朝上叶卡照样朝天，但被上面的叶子盖着，永远不会接到雪。
                // 做法：把 XZ 打 0.6m 网格，记每个格子里的最高点；某张卡的高度低于
                // 本格最高点 0.8m 以上 → 判定在树冠内部，丢掉。
                // 不做这一步的后果（第一版实测）：雪团有一大半长在冠内，外面只看得到几点白。
                if (kept.Count > 0)
                {
                    float minX = float.MaxValue, maxX = float.MinValue;
                    float minZ = float.MaxValue, maxZ = float.MinValue;
                    for (int i = 0; i < kept.Count; i++)
                    {
                        Vector3 m = (kept[i].v0 + kept[i].v1 + kept[i].v2) / 3f;
                        if (m.x < minX) minX = m.x;
                        if (m.x > maxX) maxX = m.x;
                        if (m.z < minZ) minZ = m.z;
                        if (m.z > maxZ) maxZ = m.z;
                    }
                    const float cell = 0.6f;
                    int gx = Mathf.Clamp(Mathf.CeilToInt((maxX - minX) / cell) + 1, 1, 256);
                    int gz = Mathf.Clamp(Mathf.CeilToInt((maxZ - minZ) / cell) + 1, 1, 256);
                    float[] cellTop = new float[gx * gz];
                    for (int i = 0; i < cellTop.Length; i++) cellTop[i] = -999f;

                    for (int i = 0; i < kept.Count; i++)
                    {
                        Vector3 m = (kept[i].v0 + kept[i].v1 + kept[i].v2) / 3f;
                        int cx = Mathf.Clamp((int)((m.x - minX) / cell), 0, gx - 1);
                        int cz = Mathf.Clamp((int)((m.z - minZ) / cell), 0, gz - 1);
                        int cellIdx = cz * gx + cx;
                        if (kept[i].topY > cellTop[cellIdx]) cellTop[cellIdx] = kept[i].topY;
                    }

                    var top = new List<FoliageCard>(kept.Count);
                    for (int i = 0; i < kept.Count; i++)
                    {
                        Vector3 m = (kept[i].v0 + kept[i].v1 + kept[i].v2) / 3f;
                        int cx = Mathf.Clamp((int)((m.x - minX) / cell), 0, gx - 1);
                        int cz = Mathf.Clamp((int)((m.z - minZ) / cell), 0, gz - 1);
                        if (kept[i].topY >= cellTop[cz * gx + cx] - 3.0f) top.Add(kept[i]);
                    }
                    kept = top;
                }

                kept.Sort((a, b) => b.area.CompareTo(a.area));
                if (kept.Count > foliageMaxCardsPerProto)
                    kept.RemoveRange(foliageMaxCardsPerProto, kept.Count - foliageMaxCardsPerProto);

                result = kept;
            }

            protoCards[cacheKey] = result;
            return result;
        }

        /// <summary>叶卡模板一片（prefab 根空间）。</summary>
        private class FoliageCard
        {
            public Vector3 v0, v1, v2;
            public Vector2 uv0, uv1, uv2;
            public Vector3 n;
            public float area;
            public float topY;
        }

        /// <summary>
        /// v4：在叶卡表面生成【有体积的雪团】（低模半球壳），而不是平面小方片。
        ///
        /// 为什么必须是几何体（三次失败的归因）：
        ///   · 平面片与 alpha 叶卡同层 → 侧视被外层叶卡遮死，只剩冠顶白点；
        ///   · 放大后又因为没有厚度差，读成"浮在冠外的白雾团"；
        ///   · 参数在 0.22~0.95m / 悬浮 0.12~0.34m 之间扫了两轮，两头都不成立。
        /// 几何体有厚度：任意角度都能看到圆鼓的顶面 + 团与团之间的暗缝，
        /// 这正是参考图里"雪团挂在叶簇上"的读法。
        ///
        /// 尺寸走幂律（不是均匀）：少量大团撑远景，多数小团填缝隙。
        /// </summary>
        /// <summary>
        /// 锚点周围 0.7m 内的叶卡顶点数量。用来判断这个点是不是"孤立叶卡"。
        /// 孤立叶卡（伸出冠外的稀疏叶）+雪团 = 悬在天空的白球（用户红圈标出的就是这个）。
        /// </summary>
        private int CardNeighborCount(Vector3 p, List<FoliageCard> cs, int enough = 5)
        {
            int n = 0;
            float r2 = 0.49f;
            for (int i = 0; i < cs.Count; i++)
            {
                if ((cs[i].v0 - p).sqrMagnitude < r2) n++;
                if ((cs[i].v1 - p).sqrMagnitude < r2) n++;
                if ((cs[i].v2 - p).sqrMagnitude < r2) n++;
                if (n >= enough) break;
            }
            return n;
        }

        private void EmitCardLumps(List<FoliageCard> cards, Matrix4x4 toWorld, float seed,
            List<Vector3> fv, List<Vector2> fu, List<Vector3> fn, List<int> ft)
        {
            int lumps = 0;
            int sideCount = 0;

            // 卡池是按面积降序排的：直接顺序遍历会让前几张"大卡"吃满预算，
            // 雪全堆在冠顶（实测第一版就是这样）。这里用 Perlin 哈希把顺序打散，
            // 同时每张卡最多出 2 团 → 雪沿整个树冠分布，而不是一坨。
            int nCards = cards.Count;
            int[] order = new int[nCards];
            float[] key = new float[nCards];
            for (int i = 0; i < nCards; i++)
            {
                order[i] = i;
                key[i] = Mathf.PerlinNoise(cards[i].v0.x * 2.1f + seed, cards[i].v0.z * 3.3f - seed);
            }
            System.Array.Sort(key, order);

            // 树冠最大水平半径（prefab 根空间）：用来判断"这张侧卡是不是在最外圈"。
            // 不过滤的话侧卡团块会长在冠内，外面全被叶子挡死——v4 五轮实测侧视一片绿。
            float maxRad = 0.01f;
            for (int i = 0; i < nCards; i++)
            {
                // 必须用"卡上最远的顶点"，不能用质心：叶卡长 1~2m，质心半径会低估外沿近 1m，
                // 按质心算出来的"冠缘"其实还在叶幕里面（实测团块全埋进叶子里）。
                float r0 = Mathf.Sqrt(cards[i].v0.x * cards[i].v0.x + cards[i].v0.z * cards[i].v0.z);
                float r1 = Mathf.Sqrt(cards[i].v1.x * cards[i].v1.x + cards[i].v1.z * cards[i].v1.z);
                float r2 = Mathf.Sqrt(cards[i].v2.x * cards[i].v2.x + cards[i].v2.z * cards[i].v2.z);
                float rad = Mathf.Max(r0, Mathf.Max(r1, r2));
                if (rad > maxRad) maxRad = rad;
            }

            // 【按高度分带的冠缘半径】侧卡只有落在"本高度带的外壳"上才看得见。
            // 只用一个全局 maxRad 判据不够：冠底半径 3m、冠顶 2m，用全局值会把冠顶侧卡全判成"内部"，
            // 反过来会把冠底深处的卡当成冠缘。0.5m 一带，取本带最大半径。
            const float bandH = 0.5f;
            const int bands = 26;
            float[] bandMax = new float[bands];
            for (int b = 0; b < bands; b++) bandMax[b] = 0.01f;
            for (int i = 0; i < nCards; i++)
            {
                Vector3 cm = (cards[i].v0 + cards[i].v1 + cards[i].v2) / 3f;
                int b = Mathf.Clamp((int)(cm.y / bandH), 0, bands - 1);
                float r0 = Mathf.Sqrt(cards[i].v0.x * cards[i].v0.x + cards[i].v0.z * cards[i].v0.z);
                float r1 = Mathf.Sqrt(cards[i].v1.x * cards[i].v1.x + cards[i].v1.z * cards[i].v1.z);
                float r2 = Mathf.Sqrt(cards[i].v2.x * cards[i].v2.x + cards[i].v2.z * cards[i].v2.z);
                float rad = Mathf.Max(r0, Mathf.Max(r1, r2));
                if (rad > bandMax[b]) bandMax[b] = rad;
            }

            for (int oi = 0; oi < nCards && lumps < foliageMaxLumpsPerTree; oi++)
            {
                int cardIdx = order[oi];
                FoliageCard card = cards[cardIdx];
                Vector3 a = toWorld.MultiplyPoint3x4(card.v0);
                Vector3 b = toWorld.MultiplyPoint3x4(card.v1);
                Vector3 c = toWorld.MultiplyPoint3x4(card.v2);
                Vector3 nW = toWorld.MultiplyVector(card.n).normalized;

                int perCard = 0;
                const int maxPerCard = 2;

                // ① 侧卡（法线偏水平/朝外）：沿最高边上缘撒小团 —— 侧视轮廓的"白边"来源
                if (nW.y < 0.50f)
                {
                    // 只认"冠外圈"的卡：这是侧视可见的必要条件。
                    // ⚠️ 不要再用"法线朝外"当判据：Nature 树的叶卡法线方向不保证朝外，
                    //    实测按法线过滤会把外圈卡全部筛掉，侧视一棵树都看不到雪。
                    Vector3 cm = (card.v0 + card.v1 + card.v2) / 3f;
                    float rad = Mathf.Sqrt(cm.x * cm.x + cm.z * cm.z);
                    int band = Mathf.Clamp((int)(cm.y / bandH), 0, bands - 1);
                    if (rad < bandMax[band] * 0.80f) continue;
                    Vector3 outward = new Vector3(cm.x, 0f, cm.z);
                    if (outward.sqrMagnitude < 1e-6f) continue;
                    outward.Normalize();
                    Vector3 vt = a; int vi = 0;
                    if (b.y > vt.y) { vt = b; vi = 1; }
                    if (c.y > vt.y) { vt = c; vi = 2; }
                    Vector3 o1 = (vi == 1) ? a : (vi == 2 ? a : b);
                    Vector3 o2 = (vi == 2) ? b : c;
                    Vector3 mid = (o1 + o2) * 0.5f;
                    float[] ts = { 0.18f, 0.40f, 0.62f, 0.84f };
                    foreach (float tt in ts)
                    {
                        if (lumps >= foliageMaxLumpsPerTree || perCard >= 3) break;
                        if (Mathf.PerlinNoise((vt.x + tt * 3f) * 2.7f + seed, (vt.z + tt * 3f) * 2.7f - seed) < 0.28f) continue;
                        Vector3 p = Vector3.Lerp(vt, mid, tt);
                        // 侧卡是轮廓：团块必须明显探出叶簇外沿，否则从侧面看全被外层叶挡住。
                        // 实测 0.35m 的小团在侧视里完全不可见，这里按"冠缘团"给到 0.55~1.0m。
                        float r = Mathf.Lerp(lumpSizeMax * 0.62f, lumpSizeMax * 0.95f, Mathf.PerlinNoise(p.y * 2.3f + seed, p.x * 1.9f - seed));
                        Vector3 center = p + outward * (r * 0.60f);
                        EmitDome(center, Vector3.up, r, r * lumpSquash, seed + cardIdx * 3.1f + tt, 1f, 1f, fv, fu, fn, ft);
                        lumps++;
                        sideCount++;
                        perCard++;
                    }
                    continue;
                }

                // ② 朝上叶卡：按网格细分采样 + Perlin 保留 → 簇状分布
                float e01 = Vector3.Distance(a, b);
                float e02 = Vector3.Distance(a, c);
                int n1 = Mathf.Clamp(Mathf.CeilToInt(e01 / lumpStep), 1, 12);
                int n2 = Mathf.Clamp(Mathf.CeilToInt(e02 / lumpStep), 1, 12);

                for (int i = 0; i < n1 && lumps < foliageMaxLumpsPerTree && perCard < maxPerCard; i++)
                {
                    for (int j = 0; j < n2 && lumps < foliageMaxLumpsPerTree && perCard < maxPerCard; j++)
                    {
                        float u = (i + 0.5f) / n1;
                        float v = (j + 0.5f) / n2;
                        if (u + v > 1f) continue;

                        Vector3 p = a + (b - a) * u + (c - a) * v;
                        float noise = Mathf.PerlinNoise(p.x * 3.4f + seed, p.z * 3.4f - seed);
                        if (noise < 1f - lumpKeepRatio) continue;

                        // 幂律：noise^2 把尺寸压向小值，少数点拿到大团（远景靠它们读成白）
                        float k = noise * noise;
                        float r = Mathf.Lerp(lumpSizeMin, lumpSizeMax, k);
                        // 冠缘水平抖动，避免团块排在网格上
                        Vector3 jit = new Vector3(
                            (Mathf.PerlinNoise(p.z * 5.1f + seed, p.x * 5.1f) - 0.5f) * 0.22f,
                            0f,
                            (Mathf.PerlinNoise(p.x * 4.7f - seed, p.z * 4.7f) - 0.5f) * 0.22f);
                        // 沿法线抬一点：雪是"盖在叶面上"的一层，不是埋在叶子里；
                        // 抬太多会悬空（v3 的 0.34m 悬浮就是这么变成白气球的），所以按半径比例抬。
                        // 孤立叶卡不挂雪：否则球会飘在树冠外的空中
                        if (CardNeighborCount(p, cards, 4) < 4) continue;
                        Vector3 center = p + jit + nW * (r * 0.10f);

                        EmitDome(center, nW, r, r * lumpSquash, seed + cardIdx * 7.3f + i * 1.7f + j, 1f, 1f, fv, fu, fn, ft);
                        lumps++;
                        perCard++;
                    }
                }
            }
            // 诊断期用过的计数日志已移除；需要再看分布时把下面这行打开
            // Debug.Log("[SnowOnTrees] proto lumps=" + lumps + " side=" + sideCount + " cards=" + nCards);
            // ③ 冠缘外壳采样：按"方位角 × 高度"二维分箱，取每箱内叶卡顶点的**最大水平半径**，
            //    得到真正的外轮廓 R(θ, y)，再在外轮廓外侧放雪团。
            //    为什么必须这么干：叶卡只有 40~60 张、双面、法线随机，从外面看它们是一层帘幕；
            //    "抽一张卡当锚点"或"在卡上撒点"都会把团落进帘幕内侧 → 人眼高度一棵树都看不到雪（实测 5 轮）。
            //    分箱取最远点等价于求树冠的外壳，任何角度都在最外层。
            if (nCards > 0)
            {
                float minY = 1e9f, maxY = -1e9f;
                for (int i = 0; i < nCards; i++)
                {
                    float cy = (cards[i].v0.y + cards[i].v1.y + cards[i].v2.y) / 3f;
                    if (cy < minY) minY = cy;
                    if (cy > maxY) maxY = cy;
                }
                if (maxY - minY < 0.3f) maxY = minY + 0.3f;

                const int AZ = 20;   // 方位 18° 一箱，箱数越多覆盖越密（参考图几乎每个枝梢都有团）
                const int EL = 8;    // 高度 8 层
                float[,] shellR = new float[EL, AZ];
                Vector3[,] shellP = new Vector3[EL, AZ];
                for (int e = 0; e < EL; e++)
                    for (int a = 0; a < AZ; a++) shellR[e, a] = -1f;

                for (int i = 0; i < nCards; i++)
                {
                    for (int v = 0; v < 3; v++)
                    {
                        Vector3 p = (v == 0) ? cards[i].v0 : (v == 1 ? cards[i].v1 : cards[i].v2);
                        float rr = Mathf.Sqrt(p.x * p.x + p.z * p.z);
                        if (rr < 0.20f) continue;
                        int e = Mathf.Clamp((int)((p.y - minY) / (maxY - minY) * EL), 0, EL - 1);
                        float ang = Mathf.Atan2(p.z, p.x);
                        if (ang < 0f) ang += Mathf.PI * 2f;
                        int a = Mathf.Clamp((int)(ang / (Mathf.PI * 2f) * AZ), 0, AZ - 1);
                        if (rr > shellR[e, a]) { shellR[e, a] = rr; shellP[e, a] = p; }
                    }
                }

                for (int e = 0; e < EL && lumps < foliageMaxLumpsPerTree; e++)
                {
                    // 【P43 · v5 修"空中白球"】每层 R 取中位数，明显超出中位数的箱是"伸出去的孤立叶卡"，
                    // 不是树冠真实轮廓。不排掉的话，雪团就会挂在冠外的天空里（实测被用户红圈标出）。
                    float medR = 0f;
                    {
                        var tmp = new System.Collections.Generic.List<float>(AZ);
                        for (int a2 = 0; a2 < AZ; a2++) if (shellR[e, a2] > 0f) tmp.Add(shellR[e, a2]);
                        if (tmp.Count > 0) { tmp.Sort(); medR = tmp[tmp.Count / 2]; }
                    }

                    for (int a = 0; a < AZ && lumps < foliageMaxLumpsPerTree; a++)
                    {
                        if (shellR[e, a] < 0f) continue;
                        if (medR > 0.01f && shellR[e, a] > medR * 1.30f) continue;
                        float hv = Mathf.PerlinNoise(seed * 1.7f + e * 2.3f + a * 0.77f, seed * 0.9f + e * 0.31f - a * 1.3f);
                        if (hv < 0.10f) continue;

                        Vector3 anchor = shellP[e, a];
                        Vector3 outward = new Vector3(anchor.x, 0f, anchor.z);
                        if (outward.sqrMagnitude < 1e-6f) continue;
                        outward.Normalize();

                        float r = Mathf.Lerp(lumpSizeMin, lumpSizeMax,
                            Mathf.PerlinNoise(a * 1.9f + seed, e * 1.1f - seed));
                        // 贴住叶幕：外推收到 0.02r（以前 0.15r 会让球离叶面太远，看着像悬空）
                        Vector3 center = anchor + outward * (r * 0.02f);
                        center.y = anchor.y + r * 0.02f;
                        Vector3 wc2 = toWorld.MultiplyPoint3x4(center);
                        EmitDome(wc2, Vector3.up, r, r * lumpSquash, seed + e * 7.1f + a * 3.3f, 1f, 1f, fv, fu, fn, ft);
                        lumps++;
                        sideCount++;

                        // 同一箱里再补一个更小的团：参考图的雪是"成串"的，不是一格一个
                        if (lumps < foliageMaxLumpsPerTree && hv > 0.55f)
                        {
                            float r2 = r * (0.55f + 0.25f * Mathf.PerlinNoise(a * 3.1f - seed, e * 2.1f + seed));
                            Vector3 c2 = anchor + outward * (r2 * 0.10f);
                            c2.y = anchor.y + r2 * 0.05f + 0.03f;
                            Vector3 wc3 = toWorld.MultiplyPoint3x4(c2);
                            EmitDome(wc3, Vector3.up, r2, r2 * lumpSquash, seed + e * 4.7f + a * 5.9f, 1f, 1f, fv, fu, fn, ft);
                            lumps++;
                            sideCount++;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// 生成一个低模半球壳（15 顶点 / 21 三角）：底环 + 中环 + 顶点。
        /// axis = 雪堆"长出来"的方向（一般是叶卡法线或世界上方）；
        /// 底环半径带 Perlin 抖动 → 边缘不规则，不是正圆。
        /// 底环不封口（贴在被覆盖物上，看不到背面）。
        /// </summary>
        private void EmitDome(Vector3 center, Vector3 axis, float radius, float height, float seed,
            float scaleU, float scaleV,
            List<Vector3> fv, List<Vector2> fu, List<Vector3> fn, List<int> ft,
            float stretch = 1f, Vector3 t1Hint = default(Vector3))
        {
            if (radius < 0.02f) return;
            axis.Normalize();
            if (axis.sqrMagnitude < 1e-6f) axis = Vector3.up;

            // 【P43 · v5 去"饺子感"】每个雪团姿态/高度都随机：
            // 之前所有团都"底平、顶圆、正朝上"，站远了像一批模具扣出来的饺子。
            // 这里按 seed 给随机倾角(±22°)、随机绕轴朝向、随机高矮。
            float yaw = Mathf.PerlinNoise(seed * 0.37f, 1.3f) * Mathf.PI * 2f;
            float tilt = (Mathf.PerlinNoise(seed * 0.71f, 5.1f) - 0.5f) * 0.75f;
            float tiltDir = Mathf.PerlinNoise(seed * 0.29f, 2.9f) * Mathf.PI * 2f;
            Vector3 tiltAxis = new Vector3(Mathf.Cos(tiltDir), 0f, Mathf.Sin(tiltDir));
            Quaternion qTilt = Quaternion.AngleAxis(tilt * Mathf.Rad2Deg, tiltAxis);
            axis = (qTilt * axis).normalized;
            height *= 0.55f + 0.60f * Mathf.PerlinNoise(seed * 1.31f, 7.7f);

            Vector3 t1;
            if (t1Hint.sqrMagnitude > 1e-6f)
            {
                // 【P43 · v5】指定长轴方向：枝条上的雪是"沿枝条的长条"，不是圆饼。
                // 以前只靠 scaleU/scaleV 想把 dome 拉长，其实那只改变顶点分布、形状还是圆的。
                t1 = t1Hint - axis * Vector3.Dot(t1Hint, axis);
                if (t1.sqrMagnitude < 1e-6f) t1 = Vector3.Cross(axis, Vector3.up);
                t1.Normalize();
            }
            else
            {
                t1 = Vector3.Cross(axis, Mathf.Abs(axis.y) > 0.9f ? Vector3.forward : Vector3.up);
                if (t1.sqrMagnitude < 1e-6f) t1 = Vector3.right;
                t1.Normalize();
            }
            Vector3 t2 = Vector3.Cross(axis, t1).normalized;
            // 绕自身轴随机转个角度：相邻两个雪团的"朝向"不再一模一样
            Vector3 t1r = t1 * Mathf.Cos(yaw) + t2 * Mathf.Sin(yaw);
            t2 = -t1 * Mathf.Sin(yaw) + t2 * Mathf.Cos(yaw);
            t1 = t1r;

            // 10 段 / 5 环：比旧 4 环更圆润，断面接近"椭圆积雪块"
            const int N = 10;
            int baseIdx = fv.Count;
            float baseY = -height * lumpSink;
            float[] ringH = { 0f, 0.25f, 0.50f, 0.75f, 0.90f };
            float[] ringR = { 1.00f, 0.98f, 0.86f, 0.62f, 0.32f };
            float[] ringN = { -0.35f, 0.05f, 0.45f, 0.80f, 0.95f };

            for (int r = 0; r < ringH.Length; r++)
            {
                for (int i = 0; i < N; i++)
                {
                    float ang = (i / (float)N) * Mathf.PI * 2f;
                    // 半径抖动加大：参考图的雪块边缘是犬牙交错的，规整的多边形一眼读成"贴片"
                    float jitter = 0.62f + 0.55f * Mathf.PerlinNoise(seed + i * 0.7f + r * 3.1f, seed * 1.3f + i * 0.31f);
                    // 角向轮廓噪声：整条边不再是光滑的椭圆，而是"雪堆的不规则外缘"
                    float radNoise = 0.72f + 0.56f * Mathf.PerlinNoise(seed * 2.3f + Mathf.Cos(ang) * 1.7f,
                                                                     seed * 0.9f + Mathf.Sin(ang) * 1.7f);
                    float rj = radius * ringR[r] * jitter * radNoise;
                    // 真正做椭圆：沿 t1 方向乘 stretch（枝条上的雪条就靠这个）
                    Vector3 lat = t1 * (Mathf.Cos(ang) * rj * stretch) + t2 * (Mathf.Sin(ang) * rj);
                    // 底环向下垂一点：雪会"挂住"支撑物，而不是悬在它上方留一道黑缝
                    float sag = (r == 0) ? -height * 0.35f * (0.4f + 0.6f * Mathf.PerlinNoise(seed + i * 0.9f, 3.3f)) : 0f;
                    Vector3 p = center + lat + axis * (baseY + height * ringH[r] + sag);
                    fv.Add(p);
                    fn.Add((lat.normalized + axis * ringN[r]).normalized);
                    fu.Add(new Vector2((float)i / N, ringH[r]));
                }
            }

            // 顶点
            int topIdx = fv.Count;
            fv.Add(center + axis * (baseY + height));
            fn.Add(axis);
            fu.Add(new Vector2(0.5f, 1f));

            for (int r = 0; r < ringH.Length; r++)
            {
                int ringA = baseIdx + r * N;
                if (r + 1 < ringH.Length)
                {
                    int ringB = baseIdx + (r + 1) * N;
                    for (int i = 0; i < N; i++)
                    {
                        int i1 = (i + 1) % N;
                        ft.Add(ringA + i); ft.Add(ringB + i); ft.Add(ringB + i1);
                        ft.Add(ringA + i); ft.Add(ringB + i1); ft.Add(ringA + i1);
                    }
                }
                else
                {
                    for (int i = 0; i < N; i++)
                    {
                        int i1 = (i + 1) % N;
                        ft.Add(ringA + i); ft.Add(topIdx); ft.Add(ringA + i1);
                    }
                }
            }
        }

        /// <summary>
        /// 树干/主枝挂雪：取竖直面里"迎风"的那些，贴一层薄雪斑。
        /// 真实形态是关键：雪是单侧挂，不是把整棵树包白（参考图里树干背面仍是褐色）。
        /// </summary>
        private void EmitTrunkSnow(int protoIdx, TerrainData td, Matrix4x4 toWorld, float seed,
            List<Vector3> fv, List<Vector2> fu, List<Vector3> fn, List<int> ft)
        {
            if (!trunkSnow) return;
            var bark = ProtoBark(protoIdx, td);
            if (bark == null || bark.Count == 0) return;

            Vector3 wind = new Vector3(trunkWindDir.x, 0f, trunkWindDir.y);
            if (wind.sqrMagnitude < 1e-4f) wind = new Vector3(-0.7f, 0f, -0.7f);
            wind.Normalize();

            int count = 0;
            for (int i = 0; i < bark.Count && count < 24; i++)
            {
                FoliageCard f = bark[i];
                Vector3 a = toWorld.MultiplyPoint3x4(f.v0);
                Vector3 b = toWorld.MultiplyPoint3x4(f.v1);
                Vector3 c = toWorld.MultiplyPoint3x4(f.v2);
                Vector3 nW = toWorld.MultiplyVector(f.n).normalized;
                if (Mathf.Abs(nW.y) > 0.45f) continue;                 // 只要竖直面
                if (Vector3.Dot(nW, wind) < 0.10f) continue;           // 偏迎风面（放宽到半圈，树干才不是半黑半白）
                float y = (a.y + b.y + c.y) / 3f;
                if (y < 0.25f || y > 6.5f) continue;                   // 只要树干/主枝区

                // 同高度不重复撒（按 0.75m 一档，同一档只出一条）
                if (Mathf.PerlinNoise(y * 1.31f + seed, (a.x + c.z) * 0.37f) < 0.32f) continue;

                Vector3 center = (a + b + c) / 3f;
                float r = trunkPatchSize * (0.75f + 0.5f * Mathf.PerlinNoise(center.x * 3f + seed, center.z * 3f - seed));
                // 竖向拉长：树干上的雪是竖条（stretch 沿世界 up 投到树干切面上）
                EmitDome(center, nW, r, r * 0.32f, seed + i * 2.3f, 0.75f, 2.60f, fv, fu, fn, ft,
                    1.6f, Vector3.up);
                count++;
            }
        }

        /// <summary>
        /// 【P43 · v5 主干改】枝条积雪：参考图的雪 70% 长在**枝条上缘**，是沿枝条走向的细长白条，
        /// 而不是叶子上的白片。之前一直在叶卡上做文章，所以才做成"白叶子"。
        /// 这里直接锚在树皮（sub0）面片上：只取朝上的枝面，沿最长边方向拉成雪条。
        /// </summary>
        private void EmitBranchSnow(int protoIdx, TerrainData td, Matrix4x4 toWorld, float seed,
            List<Vector3> fv, List<Vector2> fu, List<Vector3> fn, List<int> ft)
        {
            if (!branchSnow) return;
            var bark = ProtoBark(protoIdx, td);
            if (bark == null || bark.Count == 0) return;

            int count = 0;
            for (int i = 0; i < bark.Count && count < branchSnowPerTree; i++)
            {
                FoliageCard f = bark[i];
                Vector3 a = toWorld.MultiplyPoint3x4(f.v0);
                Vector3 b = toWorld.MultiplyPoint3x4(f.v1);
                Vector3 c = toWorld.MultiplyPoint3x4(f.v2);
                Vector3 nW = toWorld.MultiplyVector(f.n).normalized;
                if (nW.y < branchSnowMinUp) continue;              // 只要朝上的枝面
                float y = (a.y + b.y + c.y) / 3f;
                if (y < 1.5f) continue;                            // 树干下部交给 trunkSnow

                // 枝条走向 = 最长边
                Vector3 e0 = b - a, e1 = c - b, e2 = a - c;
                Vector3 along = e0; float len = e0.magnitude;
                if (e1.magnitude > len) { along = e1; len = e1.magnitude; }
                if (e2.magnitude > len) { along = e2; len = e2.magnitude; }
                if (len < 0.06f) continue;
                along = (along - nW * Vector3.Dot(along, nW));
                if (along.sqrMagnitude < 1e-5f) along = Vector3.forward;
                along.Normalize();

                // 【P43 · v5】以前这里按 Perlin 概率丢弃，结果雪只长在"噪声高的那半边树"。
                // 参考图是整棵匀的，所以改成"朝上枝面就撒"，靠数量均匀铺满。
                Vector3 center = (a + b + c) / 3f + nW * 0.02f;
                float r = branchSnowSize * (0.78f + 0.44f * Mathf.PerlinNoise(center.x * 4f + seed, center.z * 4f - seed));
                // 有厚度的雪条：高度给到 0.58r（半球感），不是贴枝的白纸带
                EmitDome(center, nW, r, r * 0.58f, seed + i * 1.7f, 1f, 1f, fv, fu, fn, ft,
                    branchSnowStretch, along);
                count++;
            }
        }

        /// <summary>
        /// 【P43 · v7】枝梢"雪帽"：参考图的主体不是糖霜，是一簇一簇饱满的厚雪团。
        /// 每簇 3~5 个圆团互相重叠，整体直径 0.4~0.7m，坐在冠外层叶簇上。
        /// 与旧 EmitCardLumps 的区别：半径大 3 倍、高径比 0.6（不是扁碟）、噪声弱（不是碎片）。
        /// </summary>
        private void EmitSnowCaps(List<FoliageCard> cards, Matrix4x4 toWorld, float seed,
            List<Vector3> fv, List<Vector2> fu, List<Vector3> fn, List<int> ft)
        {
            int nCards = cards.Count;
            if (nCards == 0) return;

            float minY = 1e9f, maxY = -1e9f;
            for (int i = 0; i < nCards; i++)
            {
                float cy = (cards[i].v0.y + cards[i].v1.y + cards[i].v2.y) / 3f;
                if (cy < minY) minY = cy;
                if (cy > maxY) maxY = cy;
            }
            if (maxY - minY < 0.3f) maxY = minY + 0.3f;

            const int AZ = 20;
            const int EL = 10;
            float[,] shellR = new float[EL, AZ];
            Vector3[,] shellP = new Vector3[EL, AZ];
            for (int e = 0; e < EL; e++)
                for (int a = 0; a < AZ; a++) shellR[e, a] = -1f;

            for (int i = 0; i < nCards; i++)
            {
                for (int iu = 0; iu <= 4; iu++)
                {
                    for (int iv = 0; iv <= 4 - iu; iv++)
                    {
                        float u = iu / 4f, v = iv / 4f;
                        Vector3 p = cards[i].v0 + (cards[i].v1 - cards[i].v0) * u + (cards[i].v2 - cards[i].v0) * v;
                        float rr = Mathf.Sqrt(p.x * p.x + p.z * p.z);
                        if (rr < 0.20f) continue;
                        int e = Mathf.Clamp((int)((p.y - minY) / (maxY - minY) * EL), 0, EL - 1);
                        float ang = Mathf.Atan2(p.z, p.x);
                        if (ang < 0f) ang += Mathf.PI * 2f;
                        int a = Mathf.Clamp((int)(ang / (Mathf.PI * 2f) * AZ), 0, AZ - 1);
                        // 雪帽锚点取"本格最高点"：雪落在叶簇的最上面。
                        // 之前按"最远半径"选点，雪帽全挂到树冠中下部，变成一排白蘑菇。
                        if (shellR[e, a] < 0f || p.y > shellP[e, a].y) { shellR[e, a] = rr; shellP[e, a] = p; }
                    }
                }
            }

            var cand = new List<Vector3>();
            var pts = new List<Vector3>();
            for (int i = 0; i < nCards; i++)
            {
                for (int iu = 0; iu <= 3; iu++)
                    for (int iv = 0; iv <= 3 - iu; iv++)
                        pts.Add(cards[i].v0 + (cards[i].v1 - cards[i].v0) * (iu / 3f) + (cards[i].v2 - cards[i].v0) * (iv / 3f));
            }
            // 【定位策略重写】按方位角取"最外缘叶尖"。
            // 只按顶面放雪＝从树下看全被叶幕挡死；参考图的雪团是挂在树冠外轮廓的枝梢上。
            float yGate = minY + (maxY - minY) * 0.35f;
            const int SECT = 36;
            float[] bestR = new float[SECT];
            Vector3[] bestP = new Vector3[SECT];
            for (int s = 0; s < SECT; s++) bestR[s] = -1f;
            for (int k = 0; k < pts.Count; k++)
            {
                Vector3 q = pts[k];
                if (q.y < yGate) continue;
                float rr = Mathf.Sqrt(q.x * q.x + q.z * q.z);
                if (rr < 0.30f) continue;
                float ang = Mathf.Atan2(q.z, q.x);
                if (ang < 0f) ang += Mathf.PI * 2f;
                int s = Mathf.Clamp((int)(ang / (Mathf.PI * 2f) * SECT), 0, SECT - 1);
                if (rr > bestR[s]) { bestR[s] = rr; bestP[s] = q; }
            }
            for (int s = 0; s < SECT; s++)
            {
                if (bestR[s] < 0f) continue;
                // 孤立叶尖不挂雪：0.5m 内至少 4 个叶点，否则就是空中一块白片
                int near = 0;
                for (int k = 0; k < pts.Count; k++)
                {
                    if ((pts[k] - bestP[s]).sqrMagnitude < 0.25f) { near++; if (near >= 4) break; }
                }
                if (near < 4) continue;
                cand.Add(bestP[s]);
            }

            // 再补"树冠肩膀"：外圈(半径>55%)的每个 XZ 格最高叶点。
            // 只挂最外圈尖端雪量太少（实拍不到 10% 白），参考图的白是"外缘+肩部"一起撑起来的。
            float maxRad = 0.05f;
            for (int k = 0; k < pts.Count; k++)
            {
                float rr = Mathf.Sqrt(pts[k].x * pts[k].x + pts[k].z * pts[k].z);
                if (rr > maxRad) maxRad = rr;
            }
            var topCell = new Dictionary<int, Vector3>();
            for (int k = 0; k < pts.Count; k++)
            {
                Vector3 q = pts[k];
                if (q.y < yGate) continue;
                float rr = Mathf.Sqrt(q.x * q.x + q.z * q.z);
                if (rr < maxRad * 0.55f) continue;
                int key = ((int)(q.x * 2f) + 512) * 2048 + ((int)(q.z * 2f) + 512);
                Vector3 old;
                if (!topCell.TryGetValue(key, out old) || q.y > old.y) topCell[key] = q;
            }
            foreach (var kv in topCell) cand.Add(kv.Value);
            if (cand.Count == 0) return;

            // 打散顺序后取前 N 簇：避免全堆在冠顶
            for (int i = 0; i < cand.Count; i++)
            {
                int j = Mathf.Abs((int)(Mathf.PerlinNoise(i * 0.37f + seed, seed * 1.7f) * 10000)) % cand.Count;
                Vector3 tmp = cand[i]; cand[i] = cand[j]; cand[j] = tmp;
            }

            int clusters = Mathf.Min(capClustersPerTree, cand.Count);
            Debug.Log("[SnowCaps] cand=" + cand.Count + " clusters=" + clusters + " pts=" + pts.Count);
            for (int ci = 0; ci < clusters; ci++)
            {
                Vector3 anchorW = toWorld.MultiplyPoint3x4(cand[ci]);
                int balls = 3 + (int)(Mathf.PerlinNoise(anchorW.x * 1.3f + seed, anchorW.z * 1.9f) * 3f);
                for (int bi = 0; bi < balls; bi++)
                {
                    float s = seed + ci * 13.7f + bi * 5.1f;
                    float ang = Mathf.PerlinNoise(s, 1.3f) * Mathf.PI * 2f;
                    float dist = Mathf.Lerp(0.02f, capSpread, Mathf.PerlinNoise(s * 0.7f, 4.1f));
                    Vector3 off = new Vector3(Mathf.Cos(ang) * dist, 0f, Mathf.Sin(ang) * dist);
                    float r = Mathf.Lerp(capRadiusMin, capRadiusMax, Mathf.PerlinNoise(s * 1.9f, 7.7f));
                    // 雪帽贴着枝梢向外探出一点：读成"挂在轮廓上的雪团"，而不是闷在叶幕里
                    Vector3 outW = new Vector3(cand[ci].x, 0f, cand[ci].z);
                    if (outW.sqrMagnitude < 1e-5f) outW = Vector3.right;
                    outW = toWorld.MultiplyVector(outW.normalized).normalized;
                    // 不外推：外推 0.45r 会让雪片脱离枝梢，实拍一片"空中白纸"
                    Vector3 center = anchorW + off + outW * (r * 0.02f) + Vector3.up * (0.01f + dist * 0.03f);
                    // 厚 8~14cm，片块朝外倾斜披在枝梢上，探出叶幕才看得见
                    EmitSnowCap(center, r, 0.08f + 0.06f * Mathf.PerlinNoise(s * 1.1f, 2.9f),
                        (Vector3.up + outW * 0.55f).normalized, s, fv, fu, fn, ft);
                }
            }
        }

        /// <summary>
        /// 不规则"片块状"积雪：参考图里的雪是贴在枝梢上的白色片块，不是球。
        /// 轮廓用 9 个强扰动顶点（0.5~1.5 倍半径），沿枝向拉长，中间厚、边缘薄。
        /// </summary>
        private void EmitSnowCap(Vector3 center, float radius, float height, Vector3 axis, float seed,
            List<Vector3> fv, List<Vector2> fu, List<Vector3> fn, List<int> ft)
        {
            const int N = 9;
            // 雪片平面与 axis 垂直；axis 朝外倾，雪就像"披"在枝梢外侧面，不会被叶幕闷住
            Vector3 t1 = Vector3.Cross(axis, Vector3.forward);
            if (t1.sqrMagnitude < 1e-5f) t1 = Vector3.Cross(axis, Vector3.right);
            t1.Normalize();
            Vector3 t2 = Vector3.Cross(axis, t1).normalized;
            float stretch = 1.3f + 0.7f * Mathf.PerlinNoise(seed * 0.7f, 8.3f);

            int cIdx = fv.Count;
            fv.Add(center + axis * height);                 // 中心最厚
            fn.Add(axis); fu.Add(new Vector2(0.5f, 0.5f));

            int rim = fv.Count;
            for (int i = 0; i < N; i++)
            {
                float ang = i / (float)N * Mathf.PI * 2f;
                // 轮廓强扰动：每片雪的外形都不一样，边缘是"犬牙"而不是椭圆
                float rr = radius * (0.45f + 1.05f * Mathf.PerlinNoise(seed + i * 1.73f, seed * 0.31f));
                Vector3 p = center + t1 * (Mathf.Cos(ang) * rr * stretch) + t2 * (Mathf.Sin(ang) * rr);
                p += axis * (height * 0.10f * Mathf.PerlinNoise(seed + i * 0.9f, 5.1f));
                fv.Add(p);
                fn.Add((axis * 0.85f + (p - center).normalized * 0.35f).normalized);
                fu.Add(new Vector2(Mathf.Cos(ang) * 0.5f + 0.5f, Mathf.Sin(ang) * 0.5f + 0.5f));
            }
            for (int i = 0; i < N; i++)
            {
                int i1 = (i + 1) % N;
                ft.Add(cIdx); ft.Add(rim + i); ft.Add(rim + i1);
            }

            // 侧壁：片块有厚度，边缘能看到"一层雪"而不是一张纸
            int side = fv.Count;
            for (int i = 0; i < N; i++)
            {
                Vector3 p = fv[rim + i] - axis * (height * 0.75f + 0.015f);
                fv.Add(p);
                fn.Add((p - center).normalized);
                fu.Add(new Vector2(i / (float)N, 0f));
            }
            for (int i = 0; i < N; i++)
            {
                int i1 = (i + 1) % N;
                ft.Add(rim + i); ft.Add(side + i); ft.Add(side + i1);
                ft.Add(rim + i); ft.Add(side + i1); ft.Add(rim + i1);
            }
        }

        /// <summary>
        /// 【P43 · v5 转向 · 调研后重做】树冠"雪被"：按方位×高度采样树冠外轮廓，
        /// 在外轮廓外侧铺一张连续的雪壳，靠 shader 的"法线朝上 + 噪声"决定哪里挂雪。
        /// 以前的路线是在叶子上摆独立半球（EmitDome），结果是一树"饺子"，这条路已被证伪。
        /// </summary>
        private void EmitCanopyShell(List<FoliageCard> cards, Matrix4x4 toWorld, float seed,
            List<Vector3> fv, List<Vector2> fu, List<Vector3> fn, List<int> ft)
        {
            int nCards = cards.Count;
            if (nCards == 0) return;

            float minY = 1e9f, maxY = -1e9f;
            for (int i = 0; i < nCards; i++)
            {
                float cy = (cards[i].v0.y + cards[i].v1.y + cards[i].v2.y) / 3f;
                if (cy < minY) minY = cy;
                if (cy > maxY) maxY = cy;
            }
            if (maxY - minY < 0.3f) maxY = minY + 0.3f;

            const int AZ = 40;
            const int EL = 14;
            float[,] shellR = new float[EL, AZ];
            Vector3[,] shellP = new Vector3[EL, AZ];
            Vector3[,] shellC = new Vector3[EL, AZ];   // 每格质心：用来把外壳往叶簇里"收"，避免变成凸包帆布
            int[,] shellN = new int[EL, AZ];
            for (int e = 0; e < EL; e++)
                for (int a = 0; a < AZ; a++) shellR[e, a] = -1f;

            for (int i = 0; i < nCards; i++)
            {
                // 【关键】不能只取 3 个顶点：一棵树才 60 张卡 = 180 点，外壳只能是大面片帆布。
                // 把每张叶卡三角再细分采样，外壳才有"叶簇级"的起伏。
                for (int iu = 0; iu <= 6; iu++)
                {
                    for (int iv = 0; iv <= 6 - iu; iv++)
                    {
                        float u = iu / 6f, v = iv / 6f;
                        Vector3 p = cards[i].v0 + (cards[i].v1 - cards[i].v0) * u + (cards[i].v2 - cards[i].v0) * v;
                        float rr = Mathf.Sqrt(p.x * p.x + p.z * p.z);
                        if (rr < 0.20f) continue;
                        int e = Mathf.Clamp((int)((p.y - minY) / (maxY - minY) * EL), 0, EL - 1);
                        float ang = Mathf.Atan2(p.z, p.x);
                        if (ang < 0f) ang += Mathf.PI * 2f;
                        int a = Mathf.Clamp((int)(ang / (Mathf.PI * 2f) * AZ), 0, AZ - 1);
                        shellC[e, a] += p; shellN[e, a]++;
                        if (rr > shellR[e, a]) { shellR[e, a] = rr; shellP[e, a] = p; }
                    }
                }
            }
            for (int e = 0; e < EL; e++)
                for (int a = 0; a < AZ; a++)
                {
                    if (shellN[e, a] <= 0) continue;
                    shellC[e, a] /= shellN[e, a];
                    // 最远点向质心收 35%：壳顺着叶簇起伏，不再把整棵树包成一块布
                    shellP[e, a] = Vector3.Lerp(shellC[e, a], shellP[e, a], 0.65f);
                }

            int baseIdx = fv.Count;
            for (int e = 0; e < EL; e++)
            {
                for (int a = 0; a < AZ; a++)
                {
                    if (shellR[e, a] < 0f)
                    {
                        fv.Add(Vector3.zero); fn.Add(Vector3.up); fu.Add(Vector2.zero);
                        continue;
                    }
                    Vector3 anchor = shellP[e, a];
                    Vector3 outward = new Vector3(anchor.x, 0f, anchor.z);
                    if (outward.sqrMagnitude < 1e-6f) outward = Vector3.right;
                    outward.Normalize();
                    Vector3 wp = toWorld.MultiplyPoint3x4(anchor);
                    Vector3 wo = toWorld.MultiplyVector(outward).normalized;
                    float t = e / (float)(EL - 1);
                    Vector3 pos = wp + wo * canopyOut + Vector3.up * (canopyLift * (0.4f + 0.6f * t));
                    // 外扩带噪声：雪被边缘不能是一个光滑的椭球，否则像塑料罩
                    pos += wo * ((Mathf.PerlinNoise(a * 0.7f + seed, e * 0.9f - seed) - 0.5f) * 0.12f);
                    // 【厚度】雪面本身要"隆起"：按三维噪声起伏 4~16cm，
                    // 否则就是一张零厚度的白纸（近景实拍的"白帆布"感一半来自这里）
                    float puff = Mathf.PerlinNoise(wp.x * 2.6f + seed, wp.z * 2.6f - wp.y * 1.7f);
                    pos += Vector3.up * (0.03f + 0.13f * puff);
                    fv.Add(pos);
                    // 法线：越靠冠顶越朝上（雪挂顶面），越靠下越朝外（侧缘挂不住雪）
                    Vector3 nrm = (wo * (1.0f - 0.75f * t) + Vector3.up * (0.35f + 0.65f * t)).normalized;
                    fn.Add(nrm);
                    fu.Add(new Vector2(a / (float)AZ, t));
                }
            }

            for (int e = 0; e < EL - 1; e++)
            {
                for (int a = 0; a < AZ; a++)
                {
                    int a1 = (a + 1) % AZ;
                    if (shellR[e, a] < 0f || shellR[e, a1] < 0f || shellR[e + 1, a] < 0f || shellR[e + 1, a1] < 0f) continue;
                    int i00 = baseIdx + e * AZ + a;
                    int i01 = baseIdx + e * AZ + a1;
                    int i10 = baseIdx + (e + 1) * AZ + a;
                    int i11 = baseIdx + (e + 1) * AZ + a1;
                    // 【关键】跨距太大的格不要连：两簇叶之间隔着 1~2m 空隙，
                    // 无脑连成四边形就是一张"白帆布"罩在树冠上（实测）。雪只能长在叶簇上。
                    const float maxEdge = 0.5f;
                    if ((fv[i00] - fv[i01]).sqrMagnitude > maxEdge * maxEdge) continue;
                    if ((fv[i00] - fv[i10]).sqrMagnitude > maxEdge * maxEdge) continue;
                    ft.Add(i00); ft.Add(i10); ft.Add(i11);
                    ft.Add(i00); ft.Add(i11); ft.Add(i01);
                }
            }

            // 冠顶封口：最上层收成一个小顶盖，否则俯视是一个空圈
            int topRing = -1;
            for (int e = EL - 1; e >= 0; e--) { bool any = false; for (int a = 0; a < AZ; a++) if (shellR[e, a] > 0f) { any = true; break; } if (any) { topRing = e; break; } }
            if (topRing >= 0)
            {
                Vector3 sum = Vector3.zero; int cnt = 0;
                for (int a = 0; a < AZ; a++) if (shellR[topRing, a] > 0f) { sum += toWorld.MultiplyPoint3x4(shellP[topRing, a]); cnt++; }
                if (cnt > 0)
                {
                    Vector3 capC = sum / cnt + Vector3.up * (canopyLift + 0.05f);
                    int capIdx = fv.Count;
                    fv.Add(capC); fn.Add(Vector3.up); fu.Add(new Vector2(0.5f, 1f));
                    for (int a = 0; a < AZ; a++)
                    {
                        int a1 = (a + 1) % AZ;
                        if (shellR[topRing, a] < 0f || shellR[topRing, a1] < 0f) continue;
                        int ia = baseIdx + topRing * AZ + a, ib = baseIdx + topRing * AZ + a1;
                        if ((fv[ia] - fv[ib]).sqrMagnitude > 0.75f * 0.75f) continue;
                        if ((fv[ia] - fv[capIdx]).sqrMagnitude > 1.2f * 1.2f) continue;
                        ft.Add(ia); ft.Add(capIdx); ft.Add(ib);
                    }
                }
            }
        }

        /// <summary>取地形树原型的"竖直面"模板（树干/主枝），懒缓存。</summary>
        private List<FoliageCard> ProtoBark(int idx, TerrainData td)
        {
            List<FoliageCard> cached;
            if (protoBark.TryGetValue(idx, out cached)) return cached;

            var result = new List<FoliageCard>();
            var proto = td.treePrototypes[idx];
            if (proto != null && proto.prefab != null)
            {
                var root = proto.prefab.transform;
                foreach (var pmf in proto.prefab.GetComponentsInChildren<MeshFilter>(true))
                {
                    var sm = pmf.sharedMesh;
                    if (sm == null || !sm.isReadable) continue;
                    var toRoot = root.worldToLocalMatrix * pmf.transform.localToWorldMatrix;
                    var mv = sm.vertices;
                    var mn = sm.normals;
                    int[] mt = sm.GetTriangles(0);   // 子网格 0 = 树皮；树干雪斑不能长到叶子上
                    if (mv == null || mt == null || mt.Length < 3 || mn == null || mn.Length != mv.Length) continue;

                    for (int t = 0; t + 2 < mt.Length; t += 3)
                    {
                        int i0 = mt[t], i1 = mt[t + 1], i2 = mt[t + 2];
                        if (i0 >= mv.Length || i1 >= mv.Length || i2 >= mv.Length) continue;
                        Vector3 w0 = toRoot.MultiplyPoint3x4(mv[i0]);
                        Vector3 w1 = toRoot.MultiplyPoint3x4(mv[i1]);
                        Vector3 w2 = toRoot.MultiplyPoint3x4(mv[i2]);
                        Vector3 n = (toRoot.MultiplyVector(mn[i0]) + toRoot.MultiplyVector(mn[i1]) + toRoot.MultiplyVector(mn[i2])) / 3f;
                        if (n.sqrMagnitude < 1e-8f) continue;
                        n.Normalize();
                        if (Mathf.Abs(n.y) > 0.45f) continue;              // 竖直面
                        float area = Vector3.Cross(w1 - w0, w2 - w0).magnitude * 0.5f;
                        if (area < 0.02f) continue;                        // 太碎的面不要
                        result.Add(new FoliageCard { v0 = w0, v1 = w1, v2 = w2, n = n, area = area, topY = Mathf.Max(w0.y, Mathf.Max(w1.y, w2.y)) });
                    }
                }
            }

            protoBark[idx] = result;
            return result;
        }

        private void Update()
        {
            if (!built || pileMat == null || pileMr == null) return;
            float a = Shader.GetGlobalFloat(IdGlobalSnow);
            bool on = a > 0.01f;
            // 树根雪堆只在"刚积雪、地面还没铺满"时有用（给树根垫一圈雪坡）。
            // 全局雪量 >0.45 时地面已是整片雪，这层圆盘反而会露出同心圆边 → 一圈圈白环（实测）。
            bool showPile = on && a < 0.45f;
            if (showPile != pileMr.enabled) pileMr.enabled = showPile;
            if (folMr != null) if (on != folMr.enabled) folMr.enabled = on;
            if (shellMr != null) if (on != shellMr.enabled) shellMr.enabled = on;
            if (on) pileMat.SetFloat(IdSnowAmount, a);
            // 雪团走 _Grow 抖动阈值：不是开关，而是"逐渐盖上来"
            if (on && clumpMat != null) clumpMat.SetFloat(IdGrow, Mathf.Clamp01(a));
            // 叶面雪壳：覆盖率跟雪量走，但**封顶 0.68**——参考图的树冠永远露着暗绿叶缝，
            // 铺到 1.0 会变成一整片白，反而假。
            // 树冠雪被：覆盖率跟雪量走（雪被的"哪里挂雪"由 shader 的法线朝上程度决定）
            if (on && shellMat != null) shellMat.SetFloat(IdCoverage, Mathf.Clamp01(a * 0.85f));

            // 树干提亮：连树皮一起往灰褐抬，否则近景树干是纯黑剪影（和参考图差最远的一条）。
            // 2.35 是看着参考图试出来的：再高树皮纹理就糊了，再低还是黑。
            float barkK = 1f + 1.35f * Mathf.Clamp01(a);
            for (int i = 0; i < barkMats.Count; i++)
                if (barkMats[i] != null) barkMats[i].SetColor(IdColorProp, new Color(barkK, barkK, barkK, 1f));
        }

        private void OnDestroy()
        {
            if (pileMesh != null) Destroy(pileMesh);
            if (folMesh != null) Destroy(folMesh);
            if (shellMesh != null) Destroy(shellMesh);
            if (pileMat != null) Destroy(pileMat);
            if (clumpMat != null) Destroy(clumpMat);
            if (shellMat != null) Destroy(shellMat);
        }
    }
}

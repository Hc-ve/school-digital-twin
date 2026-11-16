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

        [Header("树冠雪斑（v3，叶卡表面撒片）")]
        [Tooltip("叶卡世界法线 Y 下限（树叶大多斜朝上，比建筑的 0.55 宽）")]
        public float foliageMinUpNormal = 0.15f;
        [Tooltip("树冠区 = 树冠竖向跨度的顶部此比例（雪挂冠顶；教训：按'从底部 35% 起全收'会全选冠底大叶卡，冠顶反而没雪——面积最大的叶卡集中在冠底）")]
        [Range(0.2f, 0.6f)]
        public float foliageCanopyMinK = 0.35f;
        [Tooltip("每原型最多参与的叶卡数（按面积降序取）")]
        [Range(20, 200)]
        public int foliageMaxCardsPerProto = 90;
        [Tooltip("雪斑采样网格步长(米)——越小越密")]
        [Range(0.2f, 0.6f)]
        public float foliagePatchStep = 0.30f;
        [Tooltip("雪斑保留率（Perlin 阈值的补），1=铺满 0.4=稀疏簇状")]
        [Range(0.2f, 1f)]
        public float foliageKeepRatio = 0.60f;
        [Tooltip("每棵树最多多少片雪斑（预算闸）")]
        [Range(10, 300)]
        public int foliageMaxQuadsPerTree = 220;
        [Tooltip("雪斑尺寸下限(米)")]
        public float foliagePatchMin = 0.30f;
        [Tooltip("雪斑尺寸上限(米)")]
        public float foliagePatchMax = 0.62f;
        [Tooltip("雪斑悬浮在叶卡表面上方的距离(米)")]
        public float foliageHover = 0.12f;

        private Mesh pileMesh;
        private Material pileMat;
        private MeshRenderer pileMr;
        private Mesh folMesh;
        private MeshRenderer folMr;
        private bool built;

        // 原型叶卡模板缓存：prototypeIndex → 叶卡列表（prefab 根空间，含法线）
        private readonly Dictionary<int, List<FoliageCard>> protoCards =
            new Dictionary<int, List<FoliageCard>>();

        private static readonly int IdSnowTex = Shader.PropertyToID("_SnowTex");
        private static readonly int IdSnowDetail = Shader.PropertyToID("_DetailTex");
        private static readonly int IdSnowColor = Shader.PropertyToID("_SnowColor");
        private static readonly int IdSkyColor = Shader.PropertyToID("_SkyColor");
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

            // 雪斑预算：每树 ≤ foliageMaxQuadsPerTree × 4 顶点
            var folVerts = new List<Vector3>(foliageMaxQuadsPerTree * 4 * instances.Length);
            var folUvs = new List<Vector2>(foliageMaxQuadsPerTree * 4 * instances.Length);
            var folNorms = new List<Vector3>(foliageMaxQuadsPerTree * 4 * instances.Length);
            var folTris = new List<int>(foliageMaxQuadsPerTree * 6 * instances.Length);

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

                // ---- 雪斑：地形树按 (位置/旋转/两轴缩放) 组矩阵后盖章 ----
                List<FoliageCard> cards = ProtoCards(inst.prototypeIndex, td);
                if (cards == null || cards.Count == 0) continue;

                var toWorld = Matrix4x4.TRS(
                    new Vector3(wx, wy, wz),
                    Quaternion.Euler(0f, inst.rotation * Mathf.Rad2Deg, 0f),
                    new Vector3(inst.widthScale, inst.heightScale, inst.widthScale));

                EmitCardPatches(cards, toWorld, t * 13.7f, folVerts, folUvs, folNorms, folTris);
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
                string baseName = cloneRoot.name.Replace("(Clone)", "").Trim();
                int protoIdx = -1;
                for (int i = 0; i < td.treePrototypes.Length; i++)
                    if (td.treePrototypes[i].prefab.name == baseName) { protoIdx = i; break; }
                if (protoIdx < 0) continue;

                List<FoliageCard> cards = ProtoCards(protoIdx, td);
                if (cards == null || cards.Count == 0) continue;

                EmitCardPatches(cards, cloneRoot.localToWorldMatrix,
                    cloneRoot.position.x * 3.7f + cloneRoot.position.z * 5.3f,
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

            // ---- 雪斑 mesh（与雪堆共用一份材质实例 → Update 只写一次雪量）----
            if (folVerts.Count >= 3)
            {
                folMesh = new Mesh();
                folMesh.name = "TreeSnowFoliage";
                folMesh.indexFormat = IndexFormat.UInt32;
                folMesh.SetVertices(folVerts);
                folMesh.SetUVs(0, folUvs);
                folMesh.SetNormals(folNorms);
                folMesh.SetTriangles(folTris, 0);
                folMesh.RecalculateBounds();

                var folGo = new GameObject("TreeSnowFoliageGO");
                folGo.transform.SetParent(transform, false);   // 本物体已钉世界原点，子物体局部零位=世界坐标
                var fmf = folGo.AddComponent<MeshFilter>();
                folMr = folGo.AddComponent<MeshRenderer>();
                fmf.sharedMesh = folMesh;
                folMr.sharedMaterial = pileMat;                // 同一材质实例：雪量一处写、两处生效
                folMr.shadowCastingMode = ShadowCastingMode.Off;
                folMr.receiveShadows = true;
                folMr.lightProbeUsage = LightProbeUsage.Off;
                folMr.reflectionProbeUsage = ReflectionProbeUsage.Off;
                folMr.enabled = false;
            }
        }

        /// <summary>
        /// 取地形树原型的叶卡模板（prefab 根空间，懒缓存一次）。
        /// 筛选：法线朝上 ≥0.30、树冠区（局部 Y ≥ 树高×0.35）、按面积降序取前 N。
        /// </summary>
        private List<FoliageCard> ProtoCards(int idx, TerrainData td)
        {
            List<FoliageCard> cached;
            if (protoCards.TryGetValue(idx, out cached)) return cached;

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
                    int[] mt = sm.triangles;
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
                        if (n.y < foliageMinUpNormal) continue;
                        float topY = Mathf.Max(w0.y, Mathf.Max(w1.y, w2.y));
                        if (topY > yMax) yMax = topY;
                        float area = Vector3.Cross(w1 - w0, w2 - w0).magnitude * 0.5f;
                        probe.Add(new FoliageCard { v0 = w0, v1 = w1, v2 = w2, n = n, area = area, topY = topY });
                    }
                }

                // 树冠区取【顶部区段】：topY ≥ 树高×(1−K)。树 prefab 根在地面（y=0），
                // ⚠️ 不能写成"从某个低位起全收"——面积最大的叶卡集中在冠底，
                // 那样前 N 名全是低位卡，冠顶反而一片雪都没有（11-17 俯拍实证）。
                float canopyBase = (yMax > 0f ? yMax : 8f) * (1f - foliageCanopyMinK);
                var kept = new List<FoliageCard>();
                for (int i = 0; i < probe.Count; i++)
                    if (probe[i].topY >= canopyBase) kept.Add(probe[i]);

                kept.Sort((a, b) => b.area.CompareTo(a.area));
                if (kept.Count > foliageMaxCardsPerProto)
                    kept.RemoveRange(foliageMaxCardsPerProto, kept.Count - foliageMaxCardsPerProto);

                result = kept;
            }

            protoCards[idx] = result;
            return result;
        }

        /// <summary>叶卡模板一片（prefab 根空间）。</summary>
        private class FoliageCard
        {
            public Vector3 v0, v1, v2;
            public Vector3 n;
            public float area;
            public float topY;
        }

        /// <summary>
        /// 在叶卡表面撒雪斑：卡片先变换到世界空间，再按 foliagePatchStep 网格细分采样；
        /// Perlin 噪声保留 foliageKeepRatio 比例（其余露暗叶 → 簇状）；
        /// 每个采样点出一张 SnowPatch 径向渐隐小方片（随机尺寸/翻滚角），
        /// 沿卡面法线悬浮 foliageHover——"雪簇挂在树叶上"的最终形态。
        /// seed 用树的世界坐标派生：同一棵树每次构建结果一致，不同树分布不同。
        /// </summary>
        private void EmitCardPatches(List<FoliageCard> cards, Matrix4x4 toWorld, float seed,
            List<Vector3> fv, List<Vector2> fu, List<Vector3> fn, List<int> ft)
        {
            int quads = 0;
            for (int ci = 0; ci < cards.Count && quads < foliageMaxQuadsPerTree; ci++)
            {
                FoliageCard card = cards[ci];
                Vector3 a = toWorld.MultiplyPoint3x4(card.v0);
                Vector3 b = toWorld.MultiplyPoint3x4(card.v1);
                Vector3 c = toWorld.MultiplyPoint3x4(card.v2);
                Vector3 nW = toWorld.MultiplyVector(card.n).normalized;

                float e01 = Vector3.Distance(a, b);
                float e02 = Vector3.Distance(a, c);
                int n1 = Mathf.Clamp(Mathf.CeilToInt(e01 / foliagePatchStep), 1, 14);
                int n2 = Mathf.Clamp(Mathf.CeilToInt(e02 / foliagePatchStep), 1, 14);

                for (int i = 0; i < n1 && quads < foliageMaxQuadsPerTree; i++)
                {
                    for (int j = 0; j < n2 && quads < foliageMaxQuadsPerTree; j++)
                    {
                        float u = (i + 0.5f) / n1;
                        float v = (j + 0.5f) / n2;
                        if (u + v > 1f) continue;

                        Vector3 p = a + (b - a) * u + (c - a) * v;

                        float noise = Mathf.PerlinNoise(p.x * 3.4f + seed, p.z * 3.4f - seed);
                        if (noise < 1f - foliageKeepRatio) continue;

                        Vector3 jittered = p + new Vector3((Mathf.PerlinNoise(p.z * 5.1f + seed, p.x * 5.1f) - 0.5f) * 0.18f, 0f, (Mathf.PerlinNoise(p.x * 4.7f - seed, p.z * 4.7f) - 0.5f) * 0.18f);
                            Vector3 center = jittered + nW * foliageHover;
                        float size = Mathf.Lerp(foliagePatchMin, foliagePatchMax, noise);

                        Vector3 tangent = Vector3.Cross(nW, Vector3.up);
                        if (tangent.sqrMagnitude < 1e-4f) tangent = Vector3.right;
                        tangent.Normalize();
                        Vector3 bitan = Vector3.Cross(nW, tangent).normalized;
                        float roll = Mathf.PerlinNoise(p.x * 3.1f + seed, p.z * 3.1f - seed) * Mathf.PI;
                        Vector3 uDir = tangent * Mathf.Cos(roll) + bitan * Mathf.Sin(roll);
                        Vector3 vDir = Vector3.Cross(nW, uDir).normalized;

                        int bIdx = fv.Count;
                        float hs = size * 0.5f;
                        fv.Add(center - uDir * hs - vDir * hs);
                        fv.Add(center + uDir * hs - vDir * hs);
                        fv.Add(center + uDir * hs + vDir * hs);
                        fv.Add(center - uDir * hs + vDir * hs);
                        fn.Add(nW); fn.Add(nW); fn.Add(nW); fn.Add(nW);
                        fu.Add(new Vector2(0f, 0f));
                        fu.Add(new Vector2(1f, 0f));
                        fu.Add(new Vector2(1f, 1f));
                        fu.Add(new Vector2(0f, 1f));
                        ft.Add(bIdx); ft.Add(bIdx + 2); ft.Add(bIdx + 1);
                        ft.Add(bIdx); ft.Add(bIdx + 3); ft.Add(bIdx + 2);
                        quads++;
                    }
                }
            }
        }

        private void Update()
        {
            if (!built || pileMat == null || pileMr == null) return;
            float a = Shader.GetGlobalFloat(IdGlobalSnow);
            bool on = a > 0.01f;
            if (on != pileMr.enabled) pileMr.enabled = on;
            if (folMr != null && on != folMr.enabled) folMr.enabled = on;
            if (on) pileMat.SetFloat(IdSnowAmount, a);
        }

        private void OnDestroy()
        {
            if (pileMesh != null) Destroy(pileMesh);
            if (folMesh != null) Destroy(folMesh);
            if (pileMat != null) Destroy(pileMat);
        }
    }
}

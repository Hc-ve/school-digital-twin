using UnityEngine;
using UnityEngine.Rendering;   // ShadowCastingMode / LightProbeUsage / ReflectionProbeUsage 都在这个命名空间
using System.Collections.Generic;

namespace SchoolTwin.Weather
{
    /// <summary>
    /// 植被(树)积雪：读取地形 TreeInstance，在每棵树根生成一块"世界锁定"的雪堆圆盘，
    /// 随全局雪量(_GlobalSnow，由 WeatherVisuals 每帧写入)出现/消失，
    /// 近似《异环》"树根被雪埋、顶部留秋色"的效果。
    ///
    /// 【P43 追加 11-17 主理人反馈】树冠雪帽：树冠顶部再扣一顶扁半球雪帽
    /// （地面+树高×0.86 处，半径按树冠包围盒收 0.75），与树根雪堆同材质同雪量。
    /// 动机：航拍视角下"地面全白、树冠漆黑"不像现实——树上是有积雪的。
    /// 为什么不做逐叶染色：地形树的原型 prefab 材质是**共享资产**，运行时改
    /// _Color 会污染整个会话（B1 同型病：改 sharedMaterial = 改资产本体）；
    /// 雪帽是独立几何，零材质改动。雪帽也是 CampusUI/SnowGround 材质 →
    /// SnowOnRoofs v2 的着色器排除与 IndoorByRendererRoof 的排除都会自动跳过它。
    ///
    /// 设计要点：
    ///  · 不修改任何内置着色器 —— 复用 CampusUI/SnowGround(受主光 + 球谐环境光 + 接收阴影 + 融雾)。
    ///  · 所有树根雪堆合并进一个 mesh，仅 1 个 draw call；树冠雪帽同一份材质再一个 mesh。
    ///  · 雪堆中心微隆、边缘柔和，叠加世界锁定的低频噪声，像雪堆堆在树根而非一片平板。
    ///  · 树木本身是竖直 mesh，雪堆是地面上的水平圆盘，树干从雪里穿出 → 视觉上正是"根被雪埋、冠留秋色"。
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
        [Tooltip("树冠雪帽：帽底相对树高的位置系数（1.0=树顶，越小陷进树冠越深）")]
        [Range(0.6f, 0.95f)]
        public float capHeightK = 0.86f;
        [Tooltip("树冠雪帽半径 = 树冠包围盒水平半径 × 此系数")]
        [Range(0.4f, 1.0f)]
        public float capRadiusK = 0.75f;

        private Mesh pileMesh;
        private Material pileMat;
        private MeshRenderer pileMr;
        private Mesh capMesh;
        private MeshRenderer capMr;
        private bool built;

        // 原型尺寸缓存：prototypeIndex → (树高, 树冠水平半径)。懒算一次（prefab 不可靠 bounds，取网格 bounds × 层级缩放）。
        private readonly Dictionary<int, Vector2> protoSize = new Dictionary<int, Vector2>();

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
            //
            // 下面所有顶点都用**世界坐标**写进 mesh（wx/wz/wy 都是世界量），
            // 所以这个物件的 transform 必须停在**世界原点**。但它是被
            // WeatherVisuals.EnsureTreeSnow() 用 SetParent(transform, false)
            // 挂成子节点的，而 WeatherVisuals 的 LateUpdate 每帧执行：
            //       transform.position = cam.transform.position;
            // → 整块树根雪堆被额外平移了"相机那么多"，位置彻底错乱。
            //
            // ── 为什么错乱之后表现为一条横线（而不是一片雪堆乱飞）──────────
            // 每棵树的雪堆都是一块 pileRadius=3m 的**水平圆盘**，所有盘的
            // y 都相同（见下面 wy 的修法，原来还会整体偏高 0.5m）。
            // 一堆共面、同高的水平圆盘，当相机眼高正好穿过这个平面时，
            // 视线与平面**共面** → 透视把这些盘全部压扁、收束成**一条横线**，
            // 横贯整个屏幕、随视角平滑滑动，且能盖住墙体与树木
            // （材质 renderQueue=2997，排在所有雪盘之上）。
            // 这就是主理人 2026-11-04 反复截图反映的那条白线的全部成因。
            //
            // 修法：构建前先把自己从跟随相机的层级里摘出来，钉死在世界原点。
            //       SetParent(null, true) 保持世界位置不变（不产生瞬移），
            //       随后把世界位置归零，顶点里的世界坐标才与 transform 一致。
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

            // ---- 树冠雪帽（第二块 mesh，同材质同雪量）----
            // 顶点数按"每树 (R+1)×(S+1)"预算，R=4 环 × S=10 扇 = 55 顶点/树。
            const int CapRings = 4, CapSectors = 10;
            int capVertsPer = (CapRings + 1) * (CapSectors + 1);
            var capVerts = new List<Vector3>(capVertsPer * instances.Length);
            var capUvs = new List<Vector2>(capVertsPer * instances.Length);
            var capNorms = new List<Vector3>(capVertsPer * instances.Length);
            var capTris = new List<int>(CapRings * CapSectors * 6 * instances.Length);

            int vOff = 0, tOff = 0;
            for (int t = 0; t < instances.Length; t++)
            {
                var inst = instances[t];
                float wx = tpos.x + inst.position.x * w;
                float wz = tpos.z + inst.position.z * d;
                // ⚠️【P13】必须加 terrain.GetPosition().y
                // Terrain.SampleHeight() 返回的是**相对地形的高度**（地形自身局部坐标），
                // 不是世界高度。本场景 Terrain 的 m_LocalPosition.y = -0.5，
                // 所以漏加这一项会让所有雪堆整体**抬高 0.5m**，浮在草皮上方。
                // 抬高 0.5m 之后，雪堆平面正好压在相机眼高（约 -0.48）附近，
                // 与上一条 SetParent 的平移叠加，白线就被推到画面正中，
                // 表现为"一条悬在空中、遮挡一切"的亮带。
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
                        // 轻微隆起：中心略高、边缘低，叠加世界锁定的低频噪声，像雪堆堆在树根
                        float r = Mathf.Sqrt(u * u + v * v) * 2f;   // 0(中心)~1(边缘)
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

                // ---- 树冠雪帽：扁半球扣在树冠顶 ----
                Vector2 ps = ProtoSize(inst.prototypeIndex, td);
                float capY = wy - 0.02f + ps.x * capHeightK;   // wy 含 +0.02 的贴地抬升，回退掉再叠加树高
                float capR = ps.y * capRadiusK;
                if (capR < 0.6f) capR = 0.6f;                  // 树冠极小的原型给下限，别缩成一个点
                float capH = capR * 0.45f;                     // 扁半球：高 = 半径的 45%，"一层雪毯"的厚度感
                int capIdx0 = capVerts.Count;
                for (int ring = 0; ring <= CapRings; ring++)   // ring 0 = 顶极点 → CapRings = 赤道
                {
                    float lat = (float)ring / CapRings * Mathf.PI * 0.5f;
                    float yy = Mathf.Cos(lat) * capH;
                    float rr = Mathf.Sin(lat) * capR;
                    for (int s = 0; s <= CapSectors; s++)
                    {
                        float ang = (float)s / CapSectors * Mathf.PI * 2f;
                        capVerts.Add(new Vector3(wx + Mathf.Sin(ang) * rr, capY + yy, wz + Mathf.Cos(ang) * rr));
                        capUvs.Add(new Vector2((float)s / CapSectors, (float)ring / CapRings));
                        capNorms.Add(Vector3.up);   // 与雪壳同理：薄雪层整体朝上受光
                    }
                }
                for (int ring = 0; ring < CapRings; ring++)
                {
                    for (int s = 0; s < CapSectors; s++)
                    {
                        int a = capIdx0 + ring * (CapSectors + 1) + s;
                        int b = a + 1;
                        int c = a + (CapSectors + 1);
                        int e = c + 1;
                        capTris.Add(a); capTris.Add(c); capTris.Add(b);
                        capTris.Add(b); capTris.Add(c); capTris.Add(e);
                    }
                }
            }

            pileMesh = new Mesh();
            pileMesh.name = "TreeSnowPiles";
            pileMesh.indexFormat = IndexFormat.UInt32;   // 树多时 289 顶/棵会超 65535，按 32 位建免爆
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
            // 复用天气系统程序化生成的雪斑纹理(覆盖率遮罩)
            pileMat.SetTexture(IdSnowTex, ProceduralAssets.SnowPatch);
            // 【P13】脏污细节图，与雪盘保持同一套观感（否则树根雪堆会显得比地面干净）
            pileMat.SetTexture(IdSnowDetail, ProceduralAssets.SnowDetail);
            pileMat.SetColor(IdSnowColor, new Color(0.92f, 0.95f, 1.0f, 1f));
            pileMat.SetColor(IdSkyColor, new Color(0.74f, 0.82f, 0.96f, 1f));
            pileMat.SetFloat(IdSnowAmount, 0f);
            pileMat.SetFloat(IdEdgeFade, 1.0f);
            pileMat.renderQueue = 2997;   // 在近场/远场雪盘之上，树根雪堆优先显示

            if (pileMr != null)
            {
                pileMr.sharedMaterial = pileMat;
                pileMr.shadowCastingMode = ShadowCastingMode.Off;
                pileMr.receiveShadows = true;   // 树/角色影子落在树根雪堆上
                pileMr.lightProbeUsage = LightProbeUsage.Off;
                pileMr.reflectionProbeUsage = ReflectionProbeUsage.Off;
                pileMr.enabled = false;         // 初始不渲染；由 Update 按雪量开关
            }

            // ---- 树冠雪帽 mesh（与雪堆共用一份材质实例 → Update 只写一次雪量）----
            if (capVerts.Count >= 3)
            {
                capMesh = new Mesh();
                capMesh.name = "TreeSnowCaps";
                capMesh.indexFormat = IndexFormat.UInt32;
                capMesh.SetVertices(capVerts);
                capMesh.SetUVs(0, capUvs);
                capMesh.SetNormals(capNorms);
                capMesh.SetTriangles(capTris, 0);
                capMesh.RecalculateBounds();

                // 【为什么单独开子物体】本物体的 MeshFilter/MeshRenderer 已被雪堆占用，
                // 雪帽需要自己的 Renderer（不同 mesh）——按 SnowOnRoofs v2 同款做法独立子物体。
                var capGo = new GameObject("TreeSnowCapsGO");
                capGo.transform.SetParent(transform, false);   // 本物体已钉世界原点，子物体局部零位=世界坐标
                var cmf = capGo.AddComponent<MeshFilter>();
                capMr = capGo.AddComponent<MeshRenderer>();
                cmf.sharedMesh = capMesh;
                capMr.sharedMaterial = pileMat;                // 同一材质实例：雪量一处写、两处生效
                capMr.shadowCastingMode = ShadowCastingMode.Off;
                capMr.receiveShadows = true;
                capMr.lightProbeUsage = LightProbeUsage.Off;
                capMr.reflectionProbeUsage = ReflectionProbeUsage.Off;
                capMr.enabled = false;
            }
        }

        /// <summary>
        /// 取地形树原型的 (树高, 树冠水平半径)。
        /// prefab 不在场景里，Renderer.bounds 不可靠；改用各 MeshFilter 的
        /// sharedMesh.bounds × 自身层级缩放，相对 prefab 根求最大值。
        /// 每个 prototype 只算一次（懒缓存）。
        /// </summary>
        private Vector2 ProtoSize(int idx, TerrainData td)
        {
            Vector2 v;
            if (protoSize.TryGetValue(idx, out v)) return v;

            float h = 8f, r = 2.4f;   // 取不到网格时按典型行道树兜底
            var proto = td.treePrototypes[idx];
            if (proto != null && proto.prefab != null)
            {
                float maxTop = 0f, maxRad = 0f;
                Vector3 rootPos = proto.prefab.transform.position;
                var smfs = proto.prefab.GetComponentsInChildren<MeshFilter>(true);
                for (int i = 0; i < smfs.Length; i++)
                {
                    var sm = smfs[i].sharedMesh;
                    if (sm == null) continue;
                    var rt = smfs[i].transform;
                    float topY = (rt.position.y - rootPos.y) + sm.bounds.max.y * rt.lossyScale.y;
                    if (topY > maxTop) maxTop = topY;
                    float rad = Mathf.Max(sm.bounds.extents.x * rt.lossyScale.x,
                                          sm.bounds.extents.z * rt.lossyScale.z);
                    if (rad > maxRad) maxRad = rad;
                }
                if (maxTop > 1f) h = maxTop;
                if (maxRad > 0.5f) r = maxRad;
            }
            v = new Vector2(h, r);
            protoSize[idx] = v;
            return v;
        }

        private void Update()
        {
            if (!built || pileMat == null || pileMr == null) return;
            float a = Shader.GetGlobalFloat(IdGlobalSnow);
            bool on = a > 0.01f;
            if (on != pileMr.enabled) pileMr.enabled = on;
            if (capMr != null && on != capMr.enabled) capMr.enabled = on;
            if (on) pileMat.SetFloat(IdSnowAmount, a);
        }

        private void OnDestroy()
        {
            if (pileMesh != null) Destroy(pileMesh);
            if (capMesh != null) Destroy(capMesh);
            if (pileMat != null) Destroy(pileMat);
        }
    }
}

using UnityEngine;
using UnityEngine.Rendering;   // ShadowCastingMode / LightProbeUsage / ReflectionProbeUsage 都在这个命名空间

namespace SchoolTwin.Weather
{
    /// <summary>
    /// 植被(树)积雪：读取地形 TreeInstance，在每棵树根生成一块"世界锁定"的雪堆圆盘，
    /// 随全局雪量(_GlobalSnow，由 WeatherVisuals 每帧写入)出现/消失，
    /// 近似《异环》"树根被雪埋、顶部留秋色"的效果。
    ///
    /// 设计要点：
    ///  · 不修改任何内置着色器 —— 复用 CampusUI/SnowGround(受主光 + 球谐环境光 + 接收阴影 + 融雾)。
    ///  · 所有树根雪堆合并进一个 mesh，仅 1 个 draw call。
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

        private Mesh pileMesh;
        private Material pileMat;
        private MeshRenderer pileMr;
        private bool built;

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
            }

            pileMesh = new Mesh();
            pileMesh.name = "TreeSnowPiles";
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
        }

        private void Update()
        {
            if (!built || pileMat == null || pileMr == null) return;
            float a = Shader.GetGlobalFloat(IdGlobalSnow);
            bool on = a > 0.01f;
            if (on != pileMr.enabled) pileMr.enabled = on;
            if (on) pileMat.SetFloat(IdSnowAmount, a);
        }

        private void OnDestroy()
        {
            if (pileMesh != null) Destroy(pileMesh);
            if (pileMat != null) Destroy(pileMat);
        }
    }
}

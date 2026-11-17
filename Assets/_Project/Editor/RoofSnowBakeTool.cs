using UnityEngine;
using UnityEditor;
using UnityEngine.Rendering;
using System.Collections.Generic;

namespace SchoolTwin.Weather
{
    /// <summary>
    /// 【P43 追加 11-17】屋顶雪壳 · 编辑器烘焙工具。
    ///
    /// ── 为什么必须"编辑器烘焙"而不是运行时扫描 ──────────────────────────
    /// 实测（2026-11-17 play）：场景开了**静态批处理**，建筑的运行时网格全部
    /// 变成 "Combined Mesh (root: scene)"——758 个、CPU 不可读（isReadable=false
    /// 是运行时合并网格的固有属性，改不了）。教学楼（top=16.2m）就在其中。
    /// 所以 SnowOnRoofs 的运行时扫描永远只扫得到 55 个未合批的小网格
    /// （树/门廊），楼顶永远扫不到。
    ///
    /// 编辑模式（非 Play）没有静态批处理：场景里还是原始网格 + 真实 Transform，
    /// 且**编辑器读顶点不受 isReadable 限制**（那是 Player 构建期才剥数据的开关）。
    /// 所以把三层筛选（粗筛 / 逐列最高 / 相对高度）放到编辑器跑一遍，
    /// 把结果以**世界坐标烘焙成 prefab** 存进 Resources，运行时 SnowOnRoofs
    /// 直接实例化——零扫描、零读网格、建筑全覆盖。
    ///
    /// ── 使用 ────────────────────────────────────────────────────────────
    /// 菜单：SchoolTwin/雪壳/① 烘焙屋顶雪壳（要求 SchoolSceneDay 已打开）。
    /// 只读场景、只写一个 prefab（Assets/_Project/Weather/RoofSnow/Resources/），
    /// 不改场景、不改素材导入设置（D13-10 同款只读纪律）。
    /// 场景改动后（新建筑/改楼）重跑一次即可刷新。
    /// </summary>
    public static class RoofSnowBakeTool
    {
        private const float MinUpNormal = 0.55f;
        private const float MinTriArea = 0.35f;
        private const float MinWorldY = 3.0f;
        private const float RelMinRatio = 0.5f;
        private const float TopTolerance = 0.8f;
        private const float Lift = 0.02f;
        private const int MaxTrisPerMesh = 400000;
        private const float CellSize = 1.0f;
        private const string OutputPrefabPath = "Assets/_Project/Weather/RoofSnow/Resources/RoofSnowBake.prefab";

        // ⛔ 室内表面不烘焙：二楼楼板顶面会被"逐列最高"误判成屋顶，
        // 烘焙进 prefab 后就在楼梯口铺出一整块纯白平面（现场照片 11-17）。
        // 与 SnowOnRoofs.excludeSourceName 保持同一份名单。
        private static readonly string[] ExcludeSourceName =
            { "Floor2", "KitchenFloor", "ZalTop", "ZalStairs" };

        private class Cand
        {
            public Vector3 w0, w1, w2;
            public float minY;
            public long cell;
            public MeshFilter src;
        }

        private static long CellKey(int cx, int cz) { return ((long)cx << 32) ^ (uint)cz; }

        [MenuItem("SchoolTwin/雪壳/① 烘焙屋顶雪壳（场景级，一次性）")]
        public static void Bake()
        {
            // 【探针】方法体是否真被调到、走到哪一步——写磁盘文件，绕开 Console 时序问题。
            // 排查"Invoke 成功但无日志无产物"用；定位后可整行删除。
            try { System.IO.File.WriteAllText("Library/RoofSnowBakeMarker.txt",
                "enter @" + System.DateTime.Now.ToString("HH:mm:ss.fff")); } catch { }
            if (EditorApplication.isPlaying)
            {
                EditorUtility.DisplayDialog("屋顶雪壳烘焙", "请在非 Play 状态下烘焙（编辑模式才有原始网格）。", "好");
                return;
            }

            // ═══ 第 1 层 · 粗筛 ═══
            var cand = new List<Cand>();
            MeshFilter[] filters = Object.FindObjectsOfType<MeshFilter>();
            int scannedMeshes = 0, scannedTris = 0, unreadable = 0, skippedBig = 0;
            long totalTrisSeen = 0;

            for (int fi = 0; fi < filters.Length; fi++)
            {
                MeshFilter mf = filters[fi];
                if (mf == null || mf.sharedMesh == null) continue;
                var rd = mf.GetComponent<Renderer>();
                if (rd == null || rd.sharedMaterial == null) continue;

                string shName = rd.sharedMaterial.shader != null ? rd.sharedMaterial.shader.name : "";
                if (shName == "CampusUI/SnowGround") continue;                       // 雪类几何（v2 壳/树帽等）不收
                if (shName.StartsWith("SchoolTwin/", System.StringComparison.Ordinal)) continue;  // 屏幕类，双保险
                if (shName.StartsWith("Hidden/Nature/", System.StringComparison.Ordinal)) continue; // 树冠叶卡：巨卡会变巨片白板，树冠走 SnowOnTrees 雪斑（11-17 v3）

                Mesh m = mf.sharedMesh;
                if (!m.isReadable && Application.isPlaying) continue;   // 运行时读不了就放弃（编辑器仍可读，见类注）
                if (!m.isReadable) unreadable++;

                Vector3[] mv = m.vertices;      // 编辑器下 isReadable=false 也能读（数据未剥离）
                Vector3[] mn = m.normals;
                int[] mt = m.triangles;
                if (mv == null || mt == null || mt.Length < 3) continue;
                if (mn == null || mn.Length != mv.Length) continue;

                int triCount = mt.Length / 3;
                totalTrisSeen += triCount;
                if (triCount > MaxTrisPerMesh) { skippedBig++; continue; }

                Transform tr = mf.transform;
                Bounds wb = rd.bounds;
                if (wb.max.y < MinWorldY) continue;   // 纯地面网格，整块跳过

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
                    if (minY < MinWorldY) continue;
                    if ((minY - wb.min.y) / span < RelMinRatio) continue;

                    Vector3 cross = Vector3.Cross(w1 - w0, w2 - w0);
                    if (cross.magnitude * 0.5f < MinTriArea) continue;

                    Vector3 wn = (tr.TransformDirection(mn[i0]) +
                                  tr.TransformDirection(mn[i1]) +
                                  tr.TransformDirection(mn[i2])) / 3f;
                    if (wn.sqrMagnitude < 1e-8f) continue;
                    wn.Normalize();
                    if (wn.y < MinUpNormal) continue;

                    cand.Add(new Cand
                    {
                        w0 = w0, w1 = w1, w2 = w2,
                        minY = minY,
                        cell = CellKey(
                            Mathf.FloorToInt(((w0.x + w1.x + w2.x) / 3f) / CellSize),
                            Mathf.FloorToInt(((w0.z + w1.z + w2.z) / 3f) / CellSize)),
                        src = mf,
                    });
                }
            }

            // ═══ 第 2 层 · 逐列最高朝上面 ═══
            var colTop = new Dictionary<long, float>();
            for (int i = 0; i < cand.Count; i++)
            {
                float cur;
                if (!colTop.TryGetValue(cand[i].cell, out cur) || cand[i].minY > cur)
                    colTop[cand[i].cell] = cand[i].minY;
            }

            var bySource = new Dictionary<MeshFilter, List<Cand>>();
            for (int i = 0; i < cand.Count; i++)
            {
                float top;
                if (colTop.TryGetValue(cand[i].cell, out top) && cand[i].minY >= top - TopTolerance)
                {
                    List<Cand> l;
                    if (!bySource.TryGetValue(cand[i].src, out l)) { l = new List<Cand>(); bySource[cand[i].src] = l; }
                    l.Add(cand[i]);
                }
            }

            if (bySource.Count == 0)
            {
                Debug.LogWarning("[雪壳烘焙] 没有筛出任何暴露朝上面（扫描 " + scannedMeshes + " 网格 / "
                               + scannedTris + " 三角）。确认打开的是 SchoolSceneDay。");
                return;
            }

            // ═══ 烘焙成 prefab：容器 + 每来源一片（世界坐标，与 SnowOnRoofs v2 同构）═══
            var root = new GameObject("RoofSnowBake");
            root.transform.position = Vector3.zero;
            int partCount = 0, keptTris = 0;

            // ── 落盘目录（先建，mesh .asset 要先于 prefab 写入）──
            string dir = System.IO.Path.GetDirectoryName(OutputPrefabPath).Replace('\\', '/');
            if (!AssetDatabase.IsValidFolder(dir))
            {
                string[] seg = dir.Split('/');
                string cur = seg[0];
                for (int i = 1; i < seg.Length; i++)
                {
                    string next = cur + "/" + seg[i];
                    if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(cur, seg[i]);
                    cur = next;
                }
            }
            // 旧产物先删（prefab + 历史分片 .asset 一并清，防残留）
            var oldPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(OutputPrefabPath);
            if (oldPrefab != null) AssetDatabase.DeleteAsset(OutputPrefabPath);

            // ══════════════════════════════════════════════════════════════
            // ⚠️【11-17 两次现场踩坑，顺序不能动】
            //   踩坑一：SaveAsPrefabAsset **不会把运行时 new Mesh() 的数据序列化进
            //     prefab** —— 首跑存出 62 片 0 三角空壳（回读全 0）。
            //   踩坑二：事后再 AssetDatabase.AddObjectToAsset 把 mesh 嵌进 prefab，
            //     但 prefab 里 MeshFilter 的引用在第一次序列化时就已经写成 null
            //     （临时对象无法按引用序列化），嵌进去也没人指它 → 依旧全空。
            //   ✅ 正解：**先**把每片 mesh 用 CreateAsset 落成独立 .asset（持久化），
            //     **再**赋给 MeshFilter（此时引用指向持久资产），**最后**存 prefab
            //     —— 持久引用才能被 prefab 正确序列化。
            // ══════════════════════════════════════════════════════════════
            foreach (var kv in bySource)
            {
                List<Cand> l = kv.Value;
                if (l.Count == 0) continue;

                // 室内表面不烘焙：二楼楼板（Floor2）顶面在"逐列最高"里就是列顶，
                // 会被判成屋顶 → 烘焙进 prefab 后在楼梯口铺出一整块纯白平面（现场照片）。
                bool exSkip = false;
                for (int k = 0; k < ExcludeSourceName.Length; k++)
                {
                    string ex = ExcludeSourceName[k];
                    if (!string.IsNullOrEmpty(ex) && kv.Key.name.Contains(ex)) { exSkip = true; break; }
                }
                if (exSkip) continue;

                var verts = new Vector3[l.Count * 3];
                var norms = new Vector3[l.Count * 3];
                var uvs = new Vector2[l.Count * 3];
                var tris = new int[l.Count * 3];
                const float uvScale = 1f / 6f;

                for (int i = 0; i < l.Count; i++)
                {
                    Vector3 w0 = l[i].w0, w1 = l[i].w1, w2 = l[i].w2;
                    int b = i * 3;
                    verts[b] = w0 + Vector3.up * Lift;
                    verts[b + 1] = w1 + Vector3.up * Lift;
                    verts[b + 2] = w2 + Vector3.up * Lift;
                    norms[b] = Vector3.up; norms[b + 1] = Vector3.up; norms[b + 2] = Vector3.up;
                    uvs[b] = new Vector2(w0.x * uvScale, w0.z * uvScale);
                    uvs[b + 1] = new Vector2(w1.x * uvScale, w1.z * uvScale);
                    uvs[b + 2] = new Vector2(w2.x * uvScale, w2.z * uvScale);
                    tris[b] = b; tris[b + 1] = b + 1; tris[b + 2] = b + 2;
                }

                var mesh = new Mesh();
                mesh.name = "RoofSnow_" + kv.Key.name;
                mesh.indexFormat = IndexFormat.UInt32;
                mesh.SetVertices(new List<Vector3>(verts));
                mesh.SetNormals(new List<Vector3>(norms));
                mesh.SetUVs(0, new List<Vector2>(uvs));
                mesh.SetTriangles(new List<int>(tris), 0);
                mesh.RecalculateBounds();

                // mesh 先落成独立 .asset（来源名可能带非法文件字符，用序号命名）
                string meshPath = dir + "/RoofSnow_part_" + partCount.ToString("000") + ".asset";
                AssetDatabase.CreateAsset(mesh, meshPath);

                var go = new GameObject("RoofSnowShell_" + kv.Key.name);
                go.transform.SetParent(root.transform, false);
                var mf = go.AddComponent<MeshFilter>();
                mf.sharedMesh = mesh;              // 持久资产引用
                go.AddComponent<MeshRenderer>();   // 材质由运行时 SnowOnRoofs 统一创建分配

                partCount++;
                keptTris += l.Count;
            }

            var prefab = PrefabUtility.SaveAsPrefabAsset(root, OutputPrefabPath);
            Object.DestroyImmediate(root);

            // 回读校验：三角形总数必须对得上，否则就是又存空了
            var verify = AssetDatabase.LoadAssetAtPath<GameObject>(OutputPrefabPath);
            int verifyTris = 0;
            var vfs = verify.GetComponentsInChildren<MeshFilter>(true);
            foreach (var vf in vfs)
                if (vf.sharedMesh != null) verifyTris += vf.sharedMesh.triangles.Length / 3;
            try { System.IO.File.WriteAllText("Library/RoofSnowBakeMarker.txt",
                "done @" + System.DateTime.Now.ToString("HH:mm:ss.fff") + " tris=" + verifyTris); } catch { }

            if (verifyTris != keptTris)
                Debug.LogError("[雪壳烘焙] 回读校验失败：存了 " + verifyTris + " 三角，应为 " + keptTris
                             + "——mesh 未正确嵌入资产，检查 AddObjectToAsset 流程。");
            else
                Debug.Log("[雪壳烘焙] 完成：" + partCount + " 片 / " + keptTris + " 三角形（回读校验 ✓）→ " + OutputPrefabPath
                        + "\n  扫描 " + scannedMeshes + " 网格 / " + scannedTris + " 三角（其中 " + unreadable
                        + " 个不可读网格由编辑器直接读取，无需改导入设置）"
                        + (skippedBig > 0 ? "；超大跳过 " + skippedBig : ""));
        }
    }
}

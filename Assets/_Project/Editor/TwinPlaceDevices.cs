using System;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using SchoolTwin.Twin;

namespace SchoolTwin.EditorTools
{
    /// <summary>
    /// 一次性把 2-1 班剩下的 3 个辅助方块换成「真实模型 / 点位标记牌」。
    ///
    /// 【为什么做成批处理，而不是让用户在窗口里点】
    /// 摆放位置要靠场景几何算 —— 而场景里没有任何「墙面」对象可以直接对齐。
    /// 本类里那几组坐标是**从场景实测数据推出来的**，不是估的：
    ///
    ///   2-1 班原点 (-15.37, 4.39, 37.00)；2 层地面 y≈3.56（门轴 y=3.59、课桌 y=3.56）
    ///   北墙：三台暖气片 Radiator01 在 z=39.78（贴墙），墙内表面 z≈39.86
    ///   同楼其他教室的空调实测：Split01b y=2.42 / Split01a y=6.11，全部贴 z≈38.7~39.1
    ///   → 2 层空调的合理高度 = 地面 3.56 + 2.55 = 6.11，与既有那台完全一致
    ///
    /// 所以空调放在 (-16.86, 6.11, 38.75) 朝 -Z（yaw 180°，与同楼 3 台一致），
    /// 两块标记牌放在同墙两端、略低于空调，抬头就能看到。
    /// </summary>
    public static class TwinPlaceDevices
    {
        private const string ScenePath = "Assets/TirGames/SchoolScene/Scenes/SchoolSceneDay.unity";
        private const string AcPrefabPath =
            "Assets/TirGames/SchoolScene/Models/Interior/Technics/Prefabs/Split01b.prefab";

        private const string IdAc = "Class2-1/AC/Unit_01";
        private const string IdTemp = "Class2-1/Sensor/Temp_01";
        private const string IdCo2 = "Class2-1/Sensor/CO2_01";

        // 北墙一排：空调居中，两块标记牌在两端
        private static readonly Vector3 AcPos = new Vector3(-16.86f, 6.11f, 38.75f);
        private static readonly Vector3 TempPos = new Vector3(-20.94f, 5.15f, 38.90f);
        private static readonly Vector3 Co2Pos = new Vector3(-12.89f, 5.15f, 38.90f);
        private const float WallYaw = 180f;

        /// <summary>菜单入口（用户在编辑器里也能点，效果与批处理一致）。</summary>
        [MenuItem("P1 孪生框架/⑩ 一键放置 2-1 班设备", false, 22)]
        public static void ApplyFromMenu()
        {
            Apply();
        }

        /// <summary>批处理入口：Unity.exe -executeMethod SchoolTwin.EditorTools.TwinPlaceDevices.Apply</summary>
        public static void Apply()
        {
            StringBuilder log = new StringBuilder();
            log.AppendLine("[Twin] ===== 开始放置 2-1 班设备 =====");

            Scene scene = SceneManager.GetActiveScene();
            if (scene.path != ScenePath)
            {
                scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
                log.AppendLine("[Twin] 已打开场景：" + scene.path);
            }

            TwinRuntime runtime = UnityEngine.Object.FindObjectOfType<TwinRuntime>();
            if (runtime == null)
            {
                Debug.LogError("[Twin] 场景里找不到 TwinRuntime，中止。");
                return;
            }

            TwinCatalog catalog = runtime.Catalog;
            if (catalog == null)
            {
                Debug.LogError("[Twin] [TwinRuntime] 上没有点位目录，中止。");
                return;
            }
            log.AppendLine("[Twin] 点位目录：" + catalog.name + " / " + catalog.Points.Count + " 个点位");

            int ok = 0;
            ok += DoModel(catalog, IdAc, AcPrefabPath, AcPos, log);
            ok += DoMarker(catalog, IdTemp, TempPos, log);
            ok += DoMarker(catalog, IdCo2, Co2Pos, log);

            EditorSceneManager.MarkSceneDirty(scene);
            bool saved = EditorSceneManager.SaveScene(scene);
            log.AppendLine("[Twin] 保存场景：" + (saved ? "成功" : "失败"));

            DumpAnchors(log);

            log.AppendLine("[Twin] ===== 完成，成功替换 " + ok + " / 3 =====");
            Debug.Log(log.ToString());
        }

        // ================================================================ 两条路

        private static int DoModel(TwinCatalog catalog, string twinId, string prefabPath, Vector3 pos, StringBuilder log)
        {
            TwinPointDefinition point = catalog.Find(twinId);
            TwinAnchor cube = FindScaffold(twinId);
            if (point == null || cube == null)
            {
                log.AppendLine("[Twin] 跳过 " + twinId + "：point=" + (point != null) + " scaffold=" + (cube != null));
                return 0;
            }

            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null)
            {
                log.AppendLine("[Twin] 跳过 " + twinId + "：找不到预制体 " + prefabPath);
                return 0;
            }

            Transform parent = cube.transform.parent;
            GameObject host = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
            if (host == null)
            {
                log.AppendLine("[Twin] 跳过 " + twinId + "：实例化失败");
                return 0;
            }

            host.name = "AC_Class2-1";
            host.transform.position = pos;
            host.transform.rotation = Quaternion.Euler(0f, WallYaw, 0f);
            host.transform.localScale = Vector3.one;   // 不继承方块的 0.6

            TwinAnchor anchor = host.GetComponent<TwinAnchor>();
            if (anchor == null) anchor = host.AddComponent<TwinAnchor>();
            WriteIdentity(anchor, point);

            if (host.GetComponent<TwinStatusColor>() == null) host.AddComponent<TwinStatusColor>();
            ClearBatchingStatic(host);
            EnsureCollider(host);

            RemoveScaffold(cube);

            log.AppendLine(string.Format("[Twin] ✔ {0} → 模型 {1} @ {2}", twinId, prefab.name, pos));
            return 1;
        }

        private static int DoMarker(TwinCatalog catalog, string twinId, Vector3 pos, StringBuilder log)
        {
            TwinPointDefinition point = catalog.Find(twinId);
            TwinAnchor cube = FindScaffold(twinId);
            if (point == null || cube == null)
            {
                log.AppendLine("[Twin] 跳过 " + twinId + "：point=" + (point != null) + " scaffold=" + (cube != null));
                return 0;
            }

            Transform parent = cube.transform.parent;
            GameObject host = new GameObject("TwinMarker_" + twinId.Replace('/', '_'));
            host.transform.SetParent(parent, false);
            host.transform.position = pos;
            host.transform.rotation = Quaternion.Euler(0f, WallYaw, 0f);
            host.transform.localScale = Vector3.one;

            TwinAnchor anchor = host.AddComponent<TwinAnchor>();
            WriteIdentity(anchor, point);

            host.AddComponent<TwinPointMarker>();

            // 标记牌是纯 UI，没有 Renderer → 不补碰撞体就点不中
            BoxCollider box = host.AddComponent<BoxCollider>();
            box.center = new Vector3(0f, 0.45f, 0f);
            box.size = new Vector3(1.10f, 0.34f, 0.06f);

            RemoveScaffold(cube);

            log.AppendLine(string.Format("[Twin] ✔ {0} → 点位标记牌 @ {1}", twinId, pos));
            return 1;
        }

        // ================================================================ 公共步骤

        private static void WriteIdentity(TwinAnchor anchor, TwinPointDefinition point)
        {
            SetString(anchor, "_twinId", point.TwinId);
            SetString(anchor, "_displayName", point.DisplayName);
            SetString(anchor, "_zone", point.Zone);
            SetString(anchor, "_category", point.Category);
        }

        private static TwinAnchor FindScaffold(string twinId)
        {
            TwinAnchor[] all = UnityEngine.Object.FindObjectsOfType<TwinAnchor>(true);
            for (int i = 0; i < all.Length; i++)
            {
                TwinAnchor a = all[i];
                if (a == null) continue;
                if (!string.Equals(a.TwinId, twinId, StringComparison.Ordinal)) continue;
                if (a.gameObject.name.StartsWith("TwinPoint_", StringComparison.Ordinal)) return a;
            }
            return null;
        }

        private static void RemoveScaffold(TwinAnchor cube)
        {
            GameObject go = cube.gameObject;
            TwinStatusColor color = go.GetComponent<TwinStatusColor>();
            if (color != null) UnityEngine.Object.DestroyImmediate(color);
            UnityEngine.Object.DestroyImmediate(cube);
            go.SetActive(false);   // 隐藏而不是删除：想退回原来的样子还能找到
        }

        private static void ClearBatchingStatic(GameObject go)
        {
            Renderer[] rs = go.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < rs.Length; i++)
            {
                if (rs[i] == null) continue;
                GameObject target = rs[i].gameObject;
                StaticEditorFlags f = GameObjectUtility.GetStaticEditorFlags(target);
                if ((f & StaticEditorFlags.BatchingStatic) == 0) continue;
                GameObjectUtility.SetStaticEditorFlags(target, f & ~StaticEditorFlags.BatchingStatic);
            }
        }

        /// <summary>
        /// 没有碰撞体就按渲染包围盒补一个。
        /// 注意不能拿 bounds.size / localScale —— 模型带旋转时那样算是错的，
        /// 必须把 8 个角点变换到局部空间再求 AABB。
        /// </summary>
        private static void EnsureCollider(GameObject go)
        {
            if (go.GetComponentInChildren<Collider>(true) != null) return;

            Renderer[] rs = go.GetComponentsInChildren<Renderer>(true);
            if (rs.Length == 0) return;

            BoxCollider box = go.AddComponent<BoxCollider>();
            Matrix4x4 toLocal = go.transform.worldToLocalMatrix;
            Bounds local = new Bounds(Vector3.zero, Vector3.zero);
            bool any = false;

            for (int i = 0; i < rs.Length; i++)
            {
                if (rs[i] == null) continue;
                Bounds wb = rs[i].bounds;
                for (int k = 0; k < 8; k++)
                {
                    Vector3 corner = wb.center + new Vector3(
                        (k & 1) == 0 ? -wb.extents.x : wb.extents.x,
                        (k & 2) == 0 ? -wb.extents.y : wb.extents.y,
                        (k & 4) == 0 ? -wb.extents.z : wb.extents.z);
                    Vector3 lp = toLocal.MultiplyPoint3x4(corner);
                    if (!any) { local = new Bounds(lp, Vector3.zero); any = true; }
                    else local.Encapsulate(lp);
                }
            }

            if (!any) return;
            box.center = local.center;
            box.size = local.size;
        }

        private static void SetString(UnityEngine.Object target, string fieldName, string value)
        {
            if (target == null) return;
            SerializedObject so = new SerializedObject(target);
            SerializedProperty p = so.FindProperty(fieldName);
            if (p == null)
            {
                Debug.LogWarning("[Twin] 字段不存在：" + target.GetType().Name + "." + fieldName);
                return;
            }
            p.stringValue = value ?? "";
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(target);
        }

        private static void DumpAnchors(StringBuilder log)
        {
            TwinAnchor[] all = UnityEngine.Object.FindObjectsOfType<TwinAnchor>(true);
            log.AppendLine("[Twin] 场景内锚点共 " + all.Length + " 个：");
            for (int i = 0; i < all.Length; i++)
            {
                TwinAnchor a = all[i];
                if (a == null) continue;
                if (a.TwinId != null && a.TwinId.StartsWith("BAK", StringComparison.Ordinal)) continue;
                log.AppendLine(string.Format("    {0,-32} 宿主 {1,-38} @ {2}",
                    a.TwinId, a.gameObject.name, a.transform.position.ToString("F2")));
            }
        }
    }
}

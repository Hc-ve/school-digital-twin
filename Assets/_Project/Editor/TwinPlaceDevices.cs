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

        // 北墙内表面实测 z≈39.85（窗帘 Blinds01c 在 39.82、室外机 Conditioner01 在 40.27），
        // 但窗套比墙面更靠里，牌子放 39.70 会插进窗套 —— 退到 39.50。
        // 窗户中心 x=-21.41 / -17.42 / -13.42（窗帘位置），牌子挂到**两窗正中间**：
        // x=-19.42 / -15.42。
        // 空调位置是用户在 Scene 视图里手调过的，以场景为准 —— 重新跑 Apply 会覆盖，慎用。
        private static readonly Vector3 AcPos = new Vector3(-11.685f, 6.361f, 38.97f);
        private static readonly Vector3 TempPos = new Vector3(-19.42f, 5.50f, 39.50f);
        private static readonly Vector3 Co2Pos = new Vector3(-15.42f, 5.50f, 39.50f);

        private const float AcYaw = 180f;

        /// <summary>
        /// 标记牌的朝向。**必须是 0，不是 180**。
        ///
        /// UGUI Canvas 的**可见面是 -Z 侧** —— 证据就是原来那段 billboard 代码：
        ///   dir = canvasPos - cameraPos;  canvas.rotation = LookRotation(dir)
        /// LookRotation 让 +Z 指向 dir（背离相机），而那样渲染是正确的，
        /// 说明相机看到的是 -Z 面。
        ///
        /// 所以牌子要面向教室（相机在 -Z 方向），物体的 -Z 就得朝 -Z —— 即 yaw = 0。
        /// 写成 180 会让正面朝墙、相机看到背面，表现为**文字左右镜像**。
        /// </summary>
        private const float MarkerYaw = 0f;

        // 标记牌世界尺寸：0.72m 宽 ≈ 一块门牌。1.1m 在 2 米内会把窗户整块挡住。
        private const float MarkerWidth = 0.72f;
        private const float MarkerHeight = 0.22f;
        private const float MarkerLift = 0f;

        /// <summary>菜单入口（用户在编辑器里也能点，效果与批处理一致）。</summary>
        [MenuItem("P1 孪生框架/⑩ 一键放置 2-1 班设备", false, 22)]
        public static void ApplyFromMenu()
        {
            Apply();
        }

        /// <summary>
        /// 射线探墙：从教室内部朝四个方向打，拿到墙面的**真实 z / x**。
        /// 场景里没有叫「Wall」的对象可以对齐，只能靠碰撞体量。
        /// 同时打印空调实例的世界包围盒，用来判断它离墙多远、尺寸对不对。
        /// </summary>
        public static void Probe()
        {
            StringBuilder log = new StringBuilder();
            Scene scene = SceneManager.GetActiveScene();
            if (scene.path != ScenePath)
                scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            log.AppendLine("[Twin] ==== 墙面探测（教室内部朝四个方向）====");

            float[] heights = { 3.95f, 4.60f, 5.40f, 6.10f };
            for (int i = 0; i < heights.Length; i++)
            {
                Vector3 o = new Vector3(-16.90f, heights[i], 36.60f);
                log.AppendLine("-- y=" + heights[i]);
                Shoot(log, o, Vector3.forward, "北 +Z");
                Shoot(log, o, Vector3.back, "南 -Z");
                Shoot(log, o, Vector3.right, "东 +X");
                Shoot(log, o, Vector3.left, "西 -X");
            }

            log.AppendLine("[Twin] ==== 空调实例包围盒 ====");
            Transform ac = FindByName("AC_Class2-1");
            if (ac == null)
            {
                log.AppendLine("  找不到 AC_Class2-1");
            }
            else
            {
                Renderer[] rs = ac.GetComponentsInChildren<Renderer>(true);
                log.AppendLine("  渲染器 " + rs.Length + " 个，position=" + ac.position.ToString("F3"));
                if (rs.Length > 0)
                {
                    Bounds b = rs[0].bounds;
                    for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
                    log.AppendLine("  世界包围盒 center=" + b.center.ToString("F3") + " size=" + b.size.ToString("F3"));
                }
            }

            Debug.Log(log.ToString());
        }

        private static void Shoot(StringBuilder log, Vector3 origin, Vector3 dir, string label)
        {
            RaycastHit hit;
            if (!Physics.Raycast(origin, dir, out hit, 30f))
            {
                log.AppendLine("   " + label + " 未命中");
                return;
            }
            log.AppendLine(string.Format("   {0} 距离 {1,6:F2}m  命中点 {2}  碰撞体 {3}",
                label, hit.distance, hit.point.ToString("F2"), hit.collider.gameObject.name));
        }

        private static Transform FindByName(string name)
        {
            Transform[] all = UnityEngine.Object.FindObjectsOfType<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null && all[i].name == name) return all[i];
            }
            return null;
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
            host.transform.rotation = Quaternion.Euler(0f, AcYaw, 0f);
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
            host.transform.rotation = Quaternion.Euler(0f, MarkerYaw, 0f);
            host.transform.localScale = Vector3.one;

            TwinAnchor anchor = host.AddComponent<TwinAnchor>();
            WriteIdentity(anchor, point);

            TwinPointMarker marker = host.AddComponent<TwinPointMarker>();
            // 尺寸/朝向显式写死，不依赖脚本默认值 —— 否则改了默认值、老场景还是老尺寸。
            SetFloat(marker, "_widthMeters", MarkerWidth);
            SetFloat(marker, "_heightMeters", MarkerHeight);
            SetFloat(marker, "_lift", MarkerLift);
            // 贴墙挂必须关 billboard：否则牌子绕 Y 轴转，一侧直接转进墙里被削掉一半
            SetBool(marker, "_billboard", false);

            // 标记牌是纯 UI，没有 Renderer → 不补碰撞体就点不中
            BoxCollider box = host.AddComponent<BoxCollider>();
            box.center = new Vector3(0f, MarkerLift, 0f);
            box.size = new Vector3(MarkerWidth, MarkerHeight, 0.06f);

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

        private static void SetFloat(UnityEngine.Object target, string fieldName, float value)
        {
            if (target == null) return;
            SerializedObject so = new SerializedObject(target);
            SerializedProperty p = so.FindProperty(fieldName);
            if (p == null)
            {
                Debug.LogWarning("[Twin] 字段不存在：" + target.GetType().Name + "." + fieldName);
                return;
            }
            p.floatValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(target);
        }

        private static void SetBool(UnityEngine.Object target, string fieldName, bool value)
        {
            if (target == null) return;
            SerializedObject so = new SerializedObject(target);
            SerializedProperty p = so.FindProperty(fieldName);
            if (p == null)
            {
                Debug.LogWarning("[Twin] 字段不存在：" + target.GetType().Name + "." + fieldName);
                return;
            }
            p.boolValue = value;
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

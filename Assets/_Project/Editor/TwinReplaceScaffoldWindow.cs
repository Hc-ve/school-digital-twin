using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using SchoolTwin.Twin;

namespace SchoolTwin.EditorTools
{
    /// <summary>
    /// 用「相近模型」或「点位标记牌」替换 P1 阶段生成的辅助方块（TwinPoint_*）。
    ///
    /// 【为什么需要这个工具，而不是手工拖物体】
    /// 手工做的话，要把新物体摆到方块原来的位置 —— 而方块在三维空间里的坐标
    /// 只能靠肉眼对齐，很容易偏几十厘米，贴在墙上就穿模了。
    ///
    /// 工具直接**继承方块的 Transform**：位置、朝向原样复制，尺寸不复制
    /// （模型有自己的尺寸，套用方块的 0.6 倍缩放会把设备缩成玩具）。
    ///
    /// 【两条路】
    ///   · 有相近模型 → 放模型（空调：Split01b；灯：LightLum01_01）
    ///   · 没有模型 → 放标记牌（温湿度 / CO₂ 这类，全校园都没有传感器模型）
    /// 两条路都会自动摘掉方块上的锚点并隐藏方块，不会重复注册。
    /// </summary>
    public class TwinReplaceScaffoldWindow : EditorWindow
    {
        private const string MenuPath = "P1 孪生框架/⑨ 用模型或标记牌替换辅助方块";

        private TwinCatalog _catalog;
        private Vector2 _scroll;
        private string _message = "";
        private MessageType _messageType = MessageType.Info;

        /// <summary>每个点位选好的替换模型（可空 → 用标记牌）。</summary>
        private readonly Dictionary<string, GameObject> _models = new Dictionary<string, GameObject>(StringComparer.Ordinal);

        [MenuItem(MenuPath, false, 21)]
        public static void Open()
        {
            TwinReplaceScaffoldWindow w = GetWindow<TwinReplaceScaffoldWindow>("替换辅助方块");
            w.minSize = new Vector2(640f, 520f);
            w.TryFindCatalog();
            w.Show();
        }

        private void OnEnable()
        {
            TryFindCatalog();
        }

        private void TryFindCatalog()
        {
            if (_catalog != null) return;

            TwinRuntime runtime = FindObjectOfType<TwinRuntime>();
            if (runtime != null && runtime.Catalog != null)
            {
                _catalog = runtime.Catalog;
                return;
            }

            string[] guids = AssetDatabase.FindAssets("t:TwinCatalog");
            if (guids != null && guids.Length > 0)
                _catalog = AssetDatabase.LoadAssetAtPath<TwinCatalog>(AssetDatabase.GUIDToAssetPath(guids[0]));
        }

        // ================================================================ 界面

        private void OnGUI()
        {
            if (_catalog == null)
            {
                EditorGUILayout.HelpBox("找不到 TwinCatalog。请先在 [TwinRuntime] 上指定点位目录。", MessageType.Warning);
                if (GUILayout.Button("重新查找")) TryFindCatalog();
                return;
            }

            EditorGUILayout.HelpBox(
                "左边选「替换模型」→ 在该方块的位置放这个模型；留空 → 放一个点位标记牌。\n" +
                "两种都会自动摘掉方块的锚点并隐藏方块。位置/朝向继承方块，尺寸不继承。",
                MessageType.Info);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            for (int i = 0; i < _catalog.Points.Count; i++)
            {
                TwinPointDefinition point = _catalog.Points[i];
                if (point == null) continue;
                DrawRow(point);
            }

            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space(4f);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("把剩下的辅助方块全部换成标记牌", GUILayout.Height(26f)))
                    ReplaceAllRemainingWithMarker();

                if (GUILayout.Button("刷新", GUILayout.Width(80f), GUILayout.Height(26f))) Repaint();
            }

            if (!string.IsNullOrEmpty(_message))
                EditorGUILayout.HelpBox(_message, _messageType);
        }

        private void DrawRow(TwinPointDefinition point)
        {
            List<TwinAnchor> carriers = FindCarriers(point.TwinId);
            TwinAnchor scaffold = null;
            for (int i = 0; i < carriers.Count; i++)
            {
                if (IsScaffold(carriers[i].gameObject)) { scaffold = carriers[i]; break; }
            }

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(point.TwinId, EditorStyles.boldLabel, GUILayout.Width(250f));
                    EditorGUILayout.LabelField(CarrierSummary(carriers), EditorStyles.miniLabel);
                }

                if (scaffold == null)
                {
                    EditorGUILayout.LabelField("   已不是辅助方块，无需替换。", EditorStyles.miniLabel);
                    return;
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    GameObject current;
                    _models.TryGetValue(point.TwinId, out current);

                    GameObject picked = (GameObject)EditorGUILayout.ObjectField(
                        "替换模型（空 = 标记牌）", current, typeof(GameObject), false, GUILayout.Width(420f));
                    if (picked != current) _models[point.TwinId] = picked;

                    if (GUILayout.Button("替换", GUILayout.Width(70f)))
                        Replace(point, scaffold, _models.ContainsKey(point.TwinId) ? _models[point.TwinId] : null);
                }
            }
        }

        private static string CarrierSummary(List<TwinAnchor> carriers)
        {
            if (carriers.Count == 0) return "承载：无";
            StringBuilder sb = new StringBuilder("承载：");
            for (int i = 0; i < carriers.Count; i++)
            {
                if (i > 0) sb.Append("、");
                sb.Append(carriers[i].gameObject.name);
                if (IsScaffold(carriers[i].gameObject)) sb.Append("（辅助方块）");
            }
            if (carriers.Count > 1) sb.Append("   ⚠ 多个承载者");
            return sb.ToString();
        }

        // ================================================================ 替换

        private void Replace(TwinPointDefinition point, TwinAnchor scaffold, GameObject model)
        {
            GameObject cube = scaffold.gameObject;

            // ---- ① 造新物体 ----
            GameObject host;
            string mode;

            if (model != null)
            {
                host = InstantiateFrom(model, cube);
                mode = "模型「" + model.name + "」";
            }
            else
            {
                host = new GameObject("TwinMarker_" + point.TwinId.Replace('/', '_'));
                Undo.RegisterCreatedObjectUndo(host, "创建点位标记");
                host.transform.SetParent(cube.transform.parent, true);
                host.transform.position = cube.transform.position;
                host.transform.rotation = Quaternion.identity;
                mode = "点位标记牌";
            }

            if (host == null)
            {
                Say("无法实例化「" + (model != null ? model.name : "?") + "」。请确认它是一个预制体或场景物体。",
                    MessageType.Error);
                return;
            }

            // 位置/朝向继承方块；**尺寸不继承** —— 方块是 0.6 倍的 Cube，
            // 把模型也乘上这个缩放会变成玩具大小。
            host.transform.position = cube.transform.position;
            host.transform.rotation = cube.transform.rotation;

            // ---- ② 挂锚点并写身份 ----
            TwinAnchor anchor = host.GetComponent<TwinAnchor>();
            if (anchor == null) anchor = Undo.AddComponent<TwinAnchor>(host);

            Undo.RecordObject(anchor, "写入点位身份");
            SetString(anchor, "_twinId", point.TwinId);
            SetString(anchor, "_displayName", point.DisplayName);
            SetString(anchor, "_zone", point.Zone);
            SetString(anchor, "_category", point.Category);

            // ---- ③ 表现层 ----
            if (model != null)
            {
                // 有真实模型：用状态染色
                if (host.GetComponent<TwinStatusColor>() == null) Undo.AddComponent<TwinStatusColor>(host);
                ClearBatchingStatic(host);
                EnsureCollider(host);
            }
            else
            {
                // 标记牌：自己会建世界空间 UI，并且自带碰撞体
                if (host.GetComponent<TwinPointMarker>() == null) Undo.AddComponent<TwinPointMarker>(host);
                EnsureCollider(host);
            }

            // ---- ④ 摘掉方块上的锚点并隐藏 ----
            TwinStatusColor cubeColor = cube.GetComponent<TwinStatusColor>();
            if (cubeColor != null) Undo.DestroyObjectImmediate(cubeColor);
            Undo.DestroyObjectImmediate(scaffold);

            Undo.RecordObject(cube, "隐藏辅助方块");
            cube.SetActive(false);

            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            Selection.activeGameObject = host;

            Say("「" + point.TwinId + "」已替换为" + mode + "，位置继承自原方块。\n" +
                "记得 Ctrl+S 保存，然后 Play 验证。", MessageType.Info);
        }

        /// <summary>
        /// 从「预制体资产」或「场景里的预制体实例」造一个新实例。
        /// 场景实例必须先取到它的源预制体再实例化 —— 直接 Instantiate 场景物体
        /// 会得到一个和预制体脱钩的普通物体，以后改预制体它不跟着变。
        /// </summary>
        private static GameObject InstantiateFrom(GameObject source, GameObject nearCube)
        {
            GameObject prefab = source;

            if (!EditorUtility.IsPersistent(source))
            {
                GameObject src = PrefabUtility.GetCorrespondingObjectFromSource(source);
                if (src != null) prefab = src;
            }

            if (EditorUtility.IsPersistent(prefab))
            {
                // InstantiatePrefab 自己会登记 Undo，不要再 RegisterCreatedObjectUndo 一次
                GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, nearCube.transform.parent);
                instance.transform.SetParent(nearCube.transform.parent, true);
                return instance;
            }

            // 普通场景物体（不是预制体）：只能复制一份
            GameObject copy = Instantiate(source, nearCube.transform.parent);
            Undo.RegisterCreatedObjectUndo(copy, "复制替换模型");
            return copy;
        }

        private void ReplaceAllRemainingWithMarker()
        {
            if (_catalog == null) return;

            bool ok = EditorUtility.DisplayDialog(
                "确认",
                "把所有还是辅助方块的点位，全部换成点位标记牌？\n\n" +
                "已经绑到真实物体的点位不受影响。",
                "替换", "取消");
            if (!ok) return;

            int count = 0;
            for (int i = 0; i < _catalog.Points.Count; i++)
            {
                TwinPointDefinition point = _catalog.Points[i];
                if (point == null) continue;

                List<TwinAnchor> carriers = FindCarriers(point.TwinId);
                TwinAnchor scaffold = null;
                for (int k = 0; k < carriers.Count; k++)
                {
                    if (IsScaffold(carriers[k].gameObject)) { scaffold = carriers[k]; break; }
                }
                if (scaffold == null) continue;

                Replace(point, scaffold, null);
                count++;
            }

            Say(count == 0 ? "没有需要替换的辅助方块。" : "已替换 " + count + " 个点位为标记牌。",
                count == 0 ? MessageType.Info : MessageType.Info);
        }

        // ================================================================ 工具

        private static List<TwinAnchor> FindCarriers(string twinId)
        {
            List<TwinAnchor> list = new List<TwinAnchor>();
            TwinAnchor[] all = FindObjectsOfType<TwinAnchor>(true);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null && string.Equals(all[i].TwinId, twinId, StringComparison.Ordinal))
                    list.Add(all[i]);
            }
            return list;
        }

        private static bool IsScaffold(GameObject go)
        {
            return go != null && go.name.StartsWith("TwinPoint_", StringComparison.Ordinal);
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
        /// 没有碰撞体就按渲染包围盒补一个。标记牌没有 Renderer，
        /// 那就按它自己的世界尺寸补 —— 否则场景里点不中。
        /// </summary>
        private static void EnsureCollider(GameObject go)
        {
            if (go.GetComponentInChildren<Collider>(true) != null) return;

            Renderer[] rs = go.GetComponentsInChildren<Renderer>(true);
            BoxCollider box = Undo.AddComponent<BoxCollider>(go);

            if (rs.Length == 0)
            {
                // 标记牌：没有网格，用默认尺寸（和 TwinPointMarker 的默认世界尺寸一致）
                box.center = new Vector3(0f, 0.45f, 0f);
                box.size = new Vector3(1.10f, 0.34f, 0.06f);
                return;
            }

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
        }

        private void Say(string text, MessageType type)
        {
            _message = text;
            _messageType = type;
            Repaint();
        }
    }
}

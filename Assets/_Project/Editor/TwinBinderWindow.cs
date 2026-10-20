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
    /// 「把点位绑定到真实物体」小工具。
    ///
    /// 【它解决的问题】
    /// 场景里那 5 个彩色方块（TwinPoint_*）是 P1 阶段的**脚手架**：用来证明
    /// 「数据 → 场景着色」这条链路通了。但演示时它们很抢眼，而且不像真实的孪生 ——
    /// 真实孪生应该让**教室里的空调、门、灯本身**变色。
    ///
    /// 把锚点从方块挪到真实物体，手工做要 6 步（挂 TwinAnchor、填 4 个字段、挂
    /// TwinStatusColor、取消静态合批、补碰撞体、摘掉旧方块的组件），漏一步就有坑：
    ///
    ///   · 漏「摘旧方块」→ 两个锚点抢同一个 TwinId → Console 报重复，其中一个收不到数据
    ///   · 漏「取消 Batching Static」→ 静态合批会忽略 MaterialPropertyBlock → **颜色不生效，且不报错**
    ///   · 漏「补碰撞体」→ TwinPicker 用 Physics.Raycast，没有碰撞体就**点不中**
    ///
    /// 这三个坑的共同点：**都不报错，只是「看起来没反应」**。所以做成工具，一次做全。
    /// </summary>
    public class TwinBinderWindow : EditorWindow
    {
        private const string MenuPath = "P1 孪生框架/⑧ 把点位绑定到真实物体";

        private TwinCatalog _catalog;
        private Vector2 _scroll;
        private string _message = "";
        private MessageType _messageType = MessageType.Info;

        [MenuItem(MenuPath, false, 20)]
        public static void Open()
        {
            TwinBinderWindow w = GetWindow<TwinBinderWindow>("点位绑定");
            w.minSize = new Vector2(600f, 500f);
            w.TryFindCatalog();
            w.Show();
        }

        private void OnEnable()
        {
            TryFindCatalog();
        }

        // ================================================================ 目录

        private void TryFindCatalog()
        {
            if (_catalog != null) return;

            // 优先用场景里 [TwinRuntime] 挂的那个 —— 它才是运行时真正读的目录，
            // 避免「工具改的是一份、运行读的是另一份」。
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
                EditorGUILayout.HelpBox(
                    "找不到 TwinCatalog。\n请先在场景里选中 [TwinRuntime] 并给它指定点位目录，" +
                    "或用菜单「P1 孪生框架 / ④ 打开 / 创建点位目录资产」。",
                    MessageType.Warning);
                if (GUILayout.Button("重新查找")) TryFindCatalog();
                return;
            }

            DrawSelectionPanel();
            EditorGUILayout.Space(6f);
            DrawCatalogList();

            EditorGUILayout.Space(6f);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("刷新", GUILayout.Width(80f))) Repaint();
                if (GUILayout.Button("体检（输出到 Console）", GUILayout.Width(180f))) RunHealthCheck();
            }

            if (!string.IsNullOrEmpty(_message))
                EditorGUILayout.HelpBox(_message, _messageType);
        }

        private void DrawSelectionPanel()
        {
            GameObject go = Selection.activeGameObject;

            EditorGUILayout.LabelField("当前选中的物体", EditorStyles.boldLabel);

            if (go == null)
            {
                EditorGUILayout.HelpBox(
                    "在 Hierarchy 或 Scene 视图里选中教室里的一个真实物体（空调机、门扇、传感器……），" +
                    "再点右边对应的「绑定」。",
                    MessageType.Info);
                return;
            }

            Renderer[] renderers = go.GetComponentsInChildren<Renderer>(true);
            Collider[] colliders = go.GetComponentsInChildren<Collider>(true);
            TwinAnchor anchor = go.GetComponent<TwinAnchor>();

            EditorGUILayout.LabelField(go.name, EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "  子物体 Renderer：" + renderers.Length +
                "    Collider：" + (colliders.Length > 0 ? "有" : "无") +
                "    已有锚点：" + (anchor != null ? anchor.TwinId : "无"));

            if (renderers.Length == 0)
            {
                EditorGUILayout.HelpBox(
                    "这个物体及其子物体没有 Renderer，绑定后**看不到任何颜色变化**。" +
                    "请往上选一层（选那个真正有网格的物体）。",
                    MessageType.Warning);
            }
            else if (renderers.Length > 30)
            {
                EditorGUILayout.HelpBox(
                    "这个物体下面有 " + renderers.Length + " 个 Renderer —— 绑定后它们**会一起变色**。" +
                    "\n如果你选的是整间教室，请改选更小的部件（比如只选空调挂机）。",
                    MessageType.Warning);
            }

            int batched = CountBatchingStatic(go);
            if (batched > 0)
            {
                EditorGUILayout.HelpBox(
                    "有 " + batched + " 个 Renderer 勾了 Batching Static。静态合批会忽略 " +
                    "MaterialPropertyBlock，**颜色不会生效**。点绑定时会自动取消这个勾选。",
                    MessageType.Info);
            }
        }

        private void DrawCatalogList()
        {
            EditorGUILayout.LabelField("点位目录（" + _catalog.Count + " 个）", EditorStyles.boldLabel);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            for (int i = 0; i < _catalog.Points.Count; i++)
            {
                TwinPointDefinition point = _catalog.Points[i];
                if (point == null) continue;

                List<TwinAnchor> carriers = FindCarriers(point.TwinId);

                using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
                {
                    using (new EditorGUILayout.VerticalScope())
                    {
                        EditorGUILayout.LabelField(point.TwinId, EditorStyles.boldLabel);
                        EditorGUILayout.LabelField(CarrierSummary(carriers), EditorStyles.miniLabel);
                    }

                    if (GUILayout.Button("绑定到选中物体", GUILayout.Width(120f), GUILayout.Height(34f)))
                        Bind(point);

                    using (new EditorGUI.DisabledScope(carriers.Count == 0))
                    {
                        if (GUILayout.Button("定位", GUILayout.Width(52f), GUILayout.Height(34f)))
                        {
                            Selection.activeGameObject = carriers[0].gameObject;
                            EditorGUIUtility.PingObject(carriers[0].gameObject);
                        }
                    }
                }
            }

            EditorGUILayout.EndScrollView();
        }

        private static string CarrierSummary(List<TwinAnchor> carriers)
        {
            if (carriers.Count == 0) return "承载：无（这个点位现在收不到数据）";

            StringBuilder sb = new StringBuilder("承载：");
            for (int i = 0; i < carriers.Count; i++)
            {
                if (i > 0) sb.Append("、");
                sb.Append(carriers[i].gameObject.name);
                if (IsScaffold(carriers[i].gameObject)) sb.Append("（辅助方块）");
            }
            if (carriers.Count > 1) sb.Append("   ⚠ 有多个承载者，会重复注册");
            return sb.ToString();
        }

        // ================================================================ 绑定

        private void Bind(TwinPointDefinition point)
        {
            GameObject go = Selection.activeGameObject;
            if (go == null)
            {
                Say("请先选中一个物体。", MessageType.Warning);
                return;
            }

            Renderer[] renderers = go.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
            {
                Say("「" + go.name + "」及其子物体没有 Renderer，绑定后看不到颜色变化。请改选有网格的物体。",
                    MessageType.Error);
                return;
            }

            if (renderers.Length > 30)
            {
                bool ok = EditorUtility.DisplayDialog(
                    "确认范围",
                    "「" + go.name + "」下面有 " + renderers.Length + " 个 Renderer，绑定后它们会一起变色。\n\n" +
                    "如果你选的是整间教室，建议改选更小的部件。\n确定继续吗？",
                    "继续", "取消");
                if (!ok) return;
            }

            TwinAnchor existing = go.GetComponent<TwinAnchor>();
            if (existing != null && !string.Equals(existing.TwinId, point.TwinId, StringComparison.Ordinal))
            {
                bool ok = EditorUtility.DisplayDialog(
                    "替换点位",
                    "「" + go.name + "」上已经有一个锚点：\n    " + existing.TwinId +
                    "\n\n改为 " + point.TwinId + " ？",
                    "改", "取消");
                if (!ok) return;
            }

            // ---- ① 挂锚点并写入身份字段 ----
            TwinAnchor anchor = existing;
            if (anchor == null) anchor = Undo.AddComponent<TwinAnchor>(go);

            Undo.RecordObject(anchor, "绑定点位");
            SetString(anchor, "_twinId", point.TwinId);
            SetString(anchor, "_displayName", point.DisplayName);
            SetString(anchor, "_zone", point.Zone);
            SetString(anchor, "_category", point.Category);

            // ---- ② 挂着色组件 ----
            if (go.GetComponent<TwinStatusColor>() == null) Undo.AddComponent<TwinStatusColor>(go);

            // ---- ③ 取消静态合批 ----
            // 静态合批会把网格烘进合并后的静态批次，MaterialPropertyBlock 被忽略，
            // 于是「数据在变、颜色不变」，而且不报任何错。这是最容易漏的一步。
            int cleared = ClearBatchingStatic(go);

            // ---- ④ 补碰撞体 ----
            string colliderNote;
            bool colliderOk = EnsureCollider(go, out colliderNote);

            // ---- ⑤ 摘掉旧承载者 ----
            int removed = 0;
            int hidden = 0;
            List<TwinAnchor> old = FindCarriers(point.TwinId);
            for (int i = 0; i < old.Count; i++)
            {
                TwinAnchor a = old[i];
                if (a == null || ReferenceEquals(a, anchor)) continue;

                GameObject carrier = a.gameObject;
                TwinStatusColor color = carrier.GetComponent<TwinStatusColor>();
                if (color != null) Undo.DestroyObjectImmediate(color);
                Undo.DestroyObjectImmediate(a);
                removed++;

                // 辅助方块（TwinPoint_*）留着就是一坨悬空的灰方块，直接关掉。
                // 注意：这里按名字判断只是**决定要不要隐藏**，不参与身份识别 ——
                // 就算判错，最坏结果只是方块多显示一个，不会造成数据错乱。
                if (IsScaffold(carrier) && carrier != go)
                {
                    Undo.RecordObject(carrier, "隐藏辅助方块");
                    carrier.SetActive(false);
                    hidden++;
                }
            }

            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            Selection.activeGameObject = go;

            StringBuilder msg = new StringBuilder();
            msg.Append("已把「").Append(point.TwinId).Append("」绑定到「").Append(go.name).Append("」。");
            if (removed > 0) msg.Append("\n· 摘掉旧承载者 ").Append(removed).Append(" 个");
            if (hidden > 0) msg.Append("，其中 ").Append(hidden).Append(" 个辅助方块已隐藏");
            if (cleared > 0) msg.Append("\n· 取消 Batching Static ").Append(cleared).Append(" 处");
            if (!string.IsNullOrEmpty(colliderNote)) msg.Append("\n· ").Append(colliderNote);
            if (!colliderOk) msg.Append("\n· ⚠ 没有碰撞体，场景里点不中它");
            msg.Append("\n\n记得 Ctrl+S 保存场景，然后 Play 验证。");

            Say(msg.ToString(), MessageType.Info);
        }

        // ================================================================ 体检

        private void RunHealthCheck()
        {
            StringBuilder sb = new StringBuilder();
            int problems = 0;

            sb.AppendLine("[Twin] 点位绑定体检");

            for (int i = 0; i < _catalog.Points.Count; i++)
            {
                TwinPointDefinition point = _catalog.Points[i];
                if (point == null) continue;

                List<TwinAnchor> carriers = FindCarriers(point.TwinId);
                if (carriers.Count == 0)
                {
                    sb.AppendLine("  ✗ " + point.TwinId + "：没有承载者，收不到数据");
                    problems++;
                    continue;
                }

                if (carriers.Count > 1)
                {
                    sb.AppendLine("  ✗ " + point.TwinId + "：有 " + carriers.Count + " 个承载者，会重复注册");
                    problems++;
                }

                for (int k = 0; k < carriers.Count; k++)
                {
                    TwinAnchor a = carriers[k];
                    GameObject go = a.gameObject;

                    if (!go.activeInHierarchy)
                    {
                        sb.AppendLine("  · " + point.TwinId + "：承载者「" + go.name + "」未激活（不参与运行，正常）");
                        continue;
                    }

                    string where = "  · " + point.TwinId + " → " + go.name;
                    if (IsScaffold(go)) where += "（仍是辅助方块）";
                    sb.AppendLine(where);

                    if (go.GetComponentInChildren<Collider>(true) == null)
                    {
                        sb.AppendLine("      ⚠ 没有 Collider，场景里点不中");
                        problems++;
                    }

                    int batched = CountBatchingStatic(go);
                    if (batched > 0)
                    {
                        sb.AppendLine("      ⚠ " + batched + " 个 Renderer 勾了 Batching Static，颜色不会生效");
                        problems++;
                    }

                    if (go.GetComponent<TwinStatusColor>() == null)
                    {
                        sb.AppendLine("      ⚠ 没有 TwinStatusColor，不会变色");
                        problems++;
                    }
                }
            }

            sb.AppendLine(problems == 0 ? "结论：全部正常。" : "结论：发现 " + problems + " 个问题。");
            Debug.Log(sb.ToString());

            Say(problems == 0 ? "体检通过，详见 Console。" : "发现 " + problems + " 个问题，详见 Console。",
                problems == 0 ? MessageType.Info : MessageType.Warning);
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

        /// <summary>是不是 P1 阶段生成的辅助脚手架方块。只用于「要不要隐藏」这个外观决定。</summary>
        private static bool IsScaffold(GameObject go)
        {
            return go != null && go.name.StartsWith("TwinPoint_", StringComparison.Ordinal);
        }

        private static int CountBatchingStatic(GameObject go)
        {
            int n = 0;
            Renderer[] rs = go.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < rs.Length; i++)
            {
                if (rs[i] == null) continue;
                StaticEditorFlags f = GameObjectUtility.GetStaticEditorFlags(rs[i].gameObject);
                if ((f & StaticEditorFlags.BatchingStatic) != 0) n++;
            }
            return n;
        }

        private static int ClearBatchingStatic(GameObject go)
        {
            int n = 0;
            Renderer[] rs = go.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < rs.Length; i++)
            {
                if (rs[i] == null) continue;
                GameObject target = rs[i].gameObject;
                StaticEditorFlags f = GameObjectUtility.GetStaticEditorFlags(target);
                if ((f & StaticEditorFlags.BatchingStatic) == 0) continue;

                GameObjectUtility.SetStaticEditorFlags(target, f & ~StaticEditorFlags.BatchingStatic);
                n++;
            }
            return n;
        }

        /// <summary>
        /// TwinPicker 用 Physics.Raycast 找物体，没有碰撞体就点不中。
        /// 已有碰撞体（自己或子物体）就沿用 —— 不要重复加，重叠碰撞体会让射线多命中一次。
        /// </summary>
        private static bool EnsureCollider(GameObject go, out string note)
        {
            note = "";
            if (go.GetComponentInChildren<Collider>(true) != null) return true;

            Renderer[] rs = go.GetComponentsInChildren<Renderer>(true);
            if (rs.Length == 0)
            {
                note = "没有 Renderer，无法按包围盒生成碰撞体。";
                return false;
            }

            // 把每个 Renderer 的世界包围盒的 8 个角点变换到本物体的局部空间，再求 AABB。
            // 不能直接用 bounds.size 除以 lossyScale —— 物体带旋转时那样算是错的。
            Matrix4x4 toLocal = go.transform.worldToLocalMatrix;
            Bounds local = new Bounds(Vector3.zero, Vector3.zero);
            bool any = false;

            for (int i = 0; i < rs.Length; i++)
            {
                if (rs[i] == null) continue;
                Bounds wb = rs[i].bounds;
                Vector3 c = wb.center;
                Vector3 e = wb.extents;

                for (int k = 0; k < 8; k++)
                {
                    Vector3 corner = c + new Vector3(
                        (k & 1) == 0 ? -e.x : e.x,
                        (k & 2) == 0 ? -e.y : e.y,
                        (k & 4) == 0 ? -e.z : e.z);

                    Vector3 lp = toLocal.MultiplyPoint3x4(corner);
                    if (!any) { local = new Bounds(lp, Vector3.zero); any = true; }
                    else local.Encapsulate(lp);
                }
            }

            if (!any)
            {
                note = "算不出包围盒，未添加碰撞体。";
                return false;
            }

            BoxCollider box = Undo.AddComponent<BoxCollider>(go);
            box.center = local.center;
            box.size = local.size;
            note = "已自动添加 BoxCollider（否则场景里点不中它）。";
            return true;
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

using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using SchoolTwin.Data;
using SchoolTwin.Interaction;
using SchoolTwin.Twin;
using SchoolTwin.UI;

namespace SchoolTwin.EditorTools
{
    /// <summary>
    /// P1 骨架的一键搭建 / 校验工具。
    /// 目的：让「数据 → 场景 → 交互」闭环可以在一分钟内跑起来，
    /// 而不是靠手工拖拽十几个组件（手工拖拽也正是最容易漏配的地方）。
    /// </summary>
    public static class P1TwinBootstrap
    {
        private const string CatalogFolder = "Assets/_Project/ScriptableObjects";
        private const string CatalogPath = CatalogFolder + "/TwinCatalog_Mock.asset";
        private const string MockRootName = "[MockPoints]";
        private const string PanelRootName = "[TwinUI]";
        private const float MockCubeScale = 0.6f;

        // ================================================================ ① 生成 Mock 闭环

        [MenuItem("归档/P1 孪生框架/① 生成 Mock 闭环（当前场景）", false, 10)]
        public static void CreateMockLoop()
        {
            if (EditorApplication.isPlaying)
            {
                EditorUtility.DisplayDialog("请先退出播放模式", "生成操作请在编辑模式下执行。", "知道了");
                return;
            }

            TwinCatalog catalog = LoadOrCreateCatalog();
            if (catalog == null) return;

            TwinRuntime runtime = EnsureRuntime(catalog);
            MockDataSource source = EnsureMockSource(runtime, catalog);
            EnsureCameraTools();
            EnsureHud();
            GameObject mockRoot = EnsureMockPoints(catalog);

            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            Selection.activeObject = mockRoot;

            Debug.Log("[P1] Mock 闭环已就绪：\n" +
                      "  · 点位目录 " + CatalogPath + "（" + catalog.Count + " 个点位）\n" +
                      "  · 运行时 " + runtime.name + "，数据源 " + source.name + "\n" +
                      "  · Mock 点位根 " + MockRootName + "（可整体移动或删除，不影响资源包）\n" +
                      "按 Play 即可看到立方体按状态变色；F3 开关调试 HUD；左键点击点位查看数值。");
        }

        private static TwinCatalog LoadOrCreateCatalog()
        {
            TwinCatalog catalog = AssetDatabase.LoadAssetAtPath<TwinCatalog>(CatalogPath);
            if (catalog != null)
            {
                if (catalog.Count == 0) PopulateCatalog(catalog);
                return catalog;
            }

            if (!AssetDatabase.IsValidFolder(CatalogFolder))
            {
                Debug.LogError("[P1] 目录不存在：" + CatalogFolder + "，请先完成 P0 目录骨架。");
                return null;
            }

            catalog = ScriptableObject.CreateInstance<TwinCatalog>();
            PopulateCatalog(catalog);
            AssetDatabase.CreateAsset(catalog, CatalogPath);
            AssetDatabase.SaveAssets();
            Debug.Log("[P1] 已创建点位目录：" + CatalogPath);
            return catalog;
        }

        private static void PopulateCatalog(TwinCatalog catalog)
        {
            if (catalog.Points == null) catalog.Points = new List<TwinPointDefinition>();
            if (catalog.Points.Count > 0) return;

            catalog.Points.Add(MakePoint("Class2-1/Sensor/Temp_01", "2-1班 温湿度传感器", "Class2-1", "Sensor",
                Metric("Temperature", "°C", 10f, 35f, 16f, 28f, 10f, 32f),
                Metric("Humidity", "%RH", 20f, 80f, 30f, 65f, 20f, 75f)));

            catalog.Points.Add(MakePoint("Class2-1/Sensor/CO2_01", "2-1班 CO₂ 传感器", "Class2-1", "Sensor",
                Metric("CO2", "ppm", 400f, 2000f, 400f, 1000f, 400f, 1500f)));

            catalog.Points.Add(MakePoint("Class2-1/Light/Main_01", "2-1班 主照明", "Class2-1", "Light",
                Metric("Brightness", "%", 0f, 100f, 20f, 100f, 0f, 100f),
                Metric("Power", "W", 0f, 120f, 0f, 110f, 0f, 120f)));

            catalog.Points.Add(MakePoint("Class2-1/AC/Unit_01", "2-1班 空调机组", "Class2-1", "AC",
                Metric("SetTemp", "°C", 16f, 30f, 18f, 26f, 16f, 30f),
                Metric("Power", "kW", 0f, 3.5f, 0f, 3f, 0f, 3.5f)));

            catalog.Points.Add(MakePoint("Class2-1/Door/Entrance_01", "2-1班 前门", "Class2-1", "Door",
                Metric("Open", "0/1", 0f, 1f, 0f, 1f, 0f, 1f)));

            catalog.BuildIndex();
            EditorUtility.SetDirty(catalog);
        }

        private static TwinPointDefinition MakePoint(string id, string display, string zone, string category,
                                                     params TwinMetricDefinition[] metrics)
        {
            TwinPointDefinition point = new TwinPointDefinition();
            point.TwinId = id;
            point.DisplayName = display;
            point.Zone = zone;
            point.Category = category;
            point.Metrics = new List<TwinMetricDefinition>(metrics);
            return point;
        }

        private static TwinMetricDefinition Metric(string name, string unit, float min, float max,
                                                   float warnLow, float warnHigh, float alarmLow, float alarmHigh)
        {
            TwinMetricDefinition metric = new TwinMetricDefinition();
            metric.Metric = name;
            metric.Unit = unit;
            metric.Min = min;
            metric.Max = max;
            metric.WarnLow = warnLow;
            metric.WarnHigh = warnHigh;
            metric.AlarmLow = alarmLow;
            metric.AlarmHigh = alarmHigh;
            return metric;
        }

        private static TwinRuntime EnsureRuntime(TwinCatalog catalog)
        {
            TwinRuntime runtime = Object.FindObjectOfType<TwinRuntime>();
            if (runtime == null)
            {
                GameObject go = new GameObject("[TwinRuntime]");
                Undo.RegisterCreatedObjectUndo(go, "创建 TwinRuntime");
                runtime = go.AddComponent<TwinRuntime>();
            }
            SetField(runtime, "_catalog", catalog);
            return runtime;
        }

        private static MockDataSource EnsureMockSource(TwinRuntime runtime, TwinCatalog catalog)
        {
            MockDataSource source = runtime.GetComponent<MockDataSource>();
            if (source == null) source = Undo.AddComponent<MockDataSource>(runtime.gameObject);
            source.Catalog = catalog;
            EditorUtility.SetDirty(source);
            return source;
        }

        private static void EnsureCameraTools()
        {
            Camera cam = Camera.main;
            if (cam == null) cam = Object.FindObjectOfType<Camera>();
            if (cam == null)
            {
                Debug.LogWarning("[P1] 场景里没有相机，跳过漫游与拾取组件的挂载。");
                return;
            }

            if (cam.GetComponent<FreeCameraController>() == null)
                Undo.AddComponent<FreeCameraController>(cam.gameObject);

            if (cam.GetComponent<TwinPicker>() == null)
                Undo.AddComponent<TwinPicker>(cam.gameObject);

            EnsureInitialViewpoint(cam);

            Debug.Log("[P1] 相机已挂载漫游控制：WASD 移动 / 右键旋转 / 滚轮推进 / Shift 加速 / Q E 升降");
        }

        /// <summary>把相机当前位姿存为「初始机位」，这样预设机位系统立刻可用（数字键 1 跳回）。</summary>
        private static void EnsureInitialViewpoint(Camera cam)
        {
            FreeCameraController controller = cam.GetComponent<FreeCameraController>();
            if (controller == null || controller.ViewpointCount > 0) return;

            SerializedObject so = new SerializedObject(controller);
            SerializedProperty list = so.FindProperty("_viewpoints");
            if (list == null) return;

            list.arraySize = 1;
            SerializedProperty item = list.GetArrayElementAtIndex(0);
            item.FindPropertyRelative("Name").stringValue = "初始机位";
            item.FindPropertyRelative("Anchor").objectReferenceValue = null;
            item.FindPropertyRelative("Position").vector3Value = cam.transform.position;
            item.FindPropertyRelative("Euler").vector3Value = cam.transform.eulerAngles;
            so.ApplyModifiedPropertiesWithoutUndo();

            Debug.Log("[P1] 已把当前相机位姿存为「初始机位」（按数字键 1 可跳回）。" +
                      "建议再补两个室内机位用于 P0 遗留的 A9 性能补测。");
        }

        private static void EnsureHud()
        {
            if (Object.FindObjectOfType<TwinDebugHud>() != null) return;

            GameObject hud = new GameObject("[TwinHud]");
            Undo.RegisterCreatedObjectUndo(hud, "创建调试 HUD");
            hud.AddComponent<TwinDebugHud>();
        }

        private static GameObject EnsureMockPoints(TwinCatalog catalog)
        {
            GameObject root = GameObject.Find(MockRootName);
            if (root == null)
            {
                root = new GameObject(MockRootName);
                Undo.RegisterCreatedObjectUndo(root, "创建 Mock 点位根");
            }

            Transform zone = FindTransformByName("Class2-1");
            Vector3 basePosition;
            if (zone != null)
            {
                basePosition = zone.position + Vector3.up * 2f;
            }
            else
            {
                Camera cam = Camera.main != null ? Camera.main : Object.FindObjectOfType<Camera>();
                basePosition = cam != null
                    ? cam.transform.position + cam.transform.forward * 8f
                    : new Vector3(0f, 2f, 0f);
                Debug.LogWarning("[P1] 场景里没找到名为 Class2-1 的物体，Mock 点位放在相机前方 8m 处，可自行移动。");
            }

            int count = catalog.Count;
            for (int i = 0; i < count; i++)
            {
                TwinPointDefinition point = catalog.Points[i];
                if (point == null) continue;

                string objectName = "TwinPoint_" + point.TwinId.Replace('/', '_');

                // 按 TwinId 去重，而不是按 GameObject 名字。
                //
                // 旧实现是 root.transform.Find(objectName) —— 一旦方块被改名
                // （例如为消除冲突加 BAK/ 前缀），就找不到它，于是重复生成一个
                // 抢同一 TwinId 的锚点：Console 报「TwinId 重复」，
                // 且新方块永远收不到数据（它不在点位目录里）。
                //
                // 另外 Light 这类点位在场景里已有真实物体（灯具预制体）承载锚点，
                // 本来就不该再生成辅助方块 —— 查全场景能一并覆盖这种情况。
                if (HasTwinIdInScene(point.TwinId)) continue;

                GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube.name = objectName;
                cube.transform.SetParent(root.transform, false);
                cube.transform.position = basePosition + new Vector3((i - (count - 1) * 0.5f) * 1.4f, 0f, 0f);
                cube.transform.localScale = Vector3.one * MockCubeScale;
                Undo.RegisterCreatedObjectUndo(cube, "创建孪生点位");

                TwinAnchor anchor = cube.AddComponent<TwinAnchor>();
                SetField(anchor, "_twinId", point.TwinId);
                SetField(anchor, "_displayName", point.DisplayName);
                SetField(anchor, "_zone", point.Zone);
                SetField(anchor, "_category", point.Category);
                cube.AddComponent<TwinStatusColor>();

                AddLabel(cube, point.DisplayName);
            }

            return root;
        }

        /// <summary>
        /// 整个场景（含未激活对象）里是否已有该 TwinId 的锚点。
        /// TwinId 才是点位的身份，GameObject 名字不是 —— 名字随时可能被改。
        /// </summary>
        private static bool HasTwinIdInScene(string twinId)
        {
            if (string.IsNullOrEmpty(twinId)) return false;

            TwinAnchor[] all = Object.FindObjectsOfType<TwinAnchor>(true);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null && all[i].TwinId == twinId) return true;
            }
            return false;
        }

        private static void AddLabel(GameObject parent, string text)
        {
            Font font = LoadBuiltinFont();
            if (font == null)
            {
                Debug.LogWarning("[P1] 取不到内置字体，跳过 3D 标签（不影响数据链路）。");
                return;
            }

            GameObject label = new GameObject("Label");
            label.transform.SetParent(parent.transform, false);
            label.transform.localPosition = new Vector3(0f, 0.9f, 0f);
            // 世界空间标签的经典尺寸组合，字号偏大/偏小直接在 Inspector 里调即可
            label.transform.localScale = Vector3.one * 0.15f;

            TextMesh mesh = label.AddComponent<TextMesh>();
            mesh.text = text;
            mesh.font = font;
            mesh.fontSize = 48;
            mesh.characterSize = 0.1f;
            mesh.anchor = TextAnchor.MiddleCenter;
            mesh.alignment = TextAlignment.Center;
            mesh.color = new Color(1f, 1f, 1f, 0.95f);

            MeshRenderer renderer = label.GetComponent<MeshRenderer>();
            if (renderer != null) renderer.sharedMaterial = font.material;

            Undo.RegisterCreatedObjectUndo(label, "创建点位标签");
        }

        private static Font LoadBuiltinFont()
        {
            Font font = null;
            try { font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); }
            catch (System.Exception) { }

            if (font == null)
            {
                try { font = Resources.GetBuiltinResource<Font>("Arial.ttf"); }
                catch (System.Exception) { }
            }
            return font;
        }

        // ================================================================ ② 为选中物体添加锚点

        [MenuItem("归档/P1 孪生框架/② 为选中物体添加孪生锚点", false, 11)]
        public static void AddAnchorToSelection()
        {
            GameObject[] selected = Selection.gameObjects;
            if (selected == null || selected.Length == 0)
            {
                EditorUtility.DisplayDialog("提示", "请先在 Hierarchy 里选中至少一个物体。", "知道了");
                return;
            }

            int added = 0;
            int staticCleared = 0;

            for (int i = 0; i < selected.Length; i++)
            {
                GameObject go = selected[i];
                if (go == null) continue;

                TwinAnchor anchor = go.GetComponent<TwinAnchor>();
                if (anchor == null)
                {
                    anchor = Undo.AddComponent<TwinAnchor>(go);
                    added++;
                }

                if (string.IsNullOrEmpty(GetStringField(anchor, "_twinId")))
                    SetField(anchor, "_twinId", BuildTwinId(go));

                if (go.GetComponent<TwinStatusColor>() == null) Undo.AddComponent<TwinStatusColor>(go);

                // 静态合批会忽略 MaterialPropertyBlock，导致着色不生效
                StaticEditorFlags flags = GameObjectUtility.GetStaticEditorFlags(go);
                if ((flags & StaticEditorFlags.BatchingStatic) != 0)
                {
                    GameObjectUtility.SetStaticEditorFlags(go, flags & ~StaticEditorFlags.BatchingStatic);
                    staticCleared++;
                }
            }

            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());

            string message = "[P1] 已为 " + selected.Length + " 个物体处理锚点（新增 " + added + " 个）";
            if (staticCleared > 0)
                message += "；其中 " + staticCleared + " 个已取消 Batching Static 勾选（否则状态着色不生效）";
            Debug.Log(message + "\n请到 Inspector 里把 TwinId 改成点位映射表里的正式 ID。");
        }

        private static string BuildTwinId(GameObject go)
        {
            Transform parent = go.transform.parent;
            string zone = parent != null ? parent.name : "Scene";
            return Sanitize(zone) + "/Prop/" + Sanitize(go.name);
        }

        private static string Sanitize(string name)
        {
            if (string.IsNullOrEmpty(name)) return "Unnamed";
            return name.Replace(' ', '_');
        }

        // ================================================================ ③ 列出场景内点位

        [MenuItem("归档/P1 孪生框架/③ 列出场景内孪生点位", false, 12)]
        public static void ListSceneAnchors()
        {
            TwinAnchor[] anchors = Object.FindObjectsOfType<TwinAnchor>(true);
            if (anchors == null || anchors.Length == 0)
            {
                Debug.Log("[P1] 当前场景里没有 TwinAnchor。可用菜单 ① 生成 Mock 闭环。");
                return;
            }

            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append("[P1] 场景内共 ").Append(anchors.Length).Append(" 个孪生点位：\n");
            for (int i = 0; i < anchors.Length; i++)
            {
                TwinAnchor a = anchors[i];
                sb.Append("  ").Append(a.TwinId).Append("   (")
                  .Append(a.DisplayName).Append(")   路径=").Append(GetPath(a.transform)).Append('\n');
            }
            Debug.Log(sb.ToString());
        }

        private static string GetPath(Transform t)
        {
            string path = t.name;
            Transform current = t.parent;
            int guard = 0;
            while (current != null && guard++ < 32)
            {
                path = current.name + "/" + path;
                current = current.parent;
            }
            return path;
        }

        // ================================================================ ④ 打开点位目录

        [MenuItem("归档/P1 孪生框架/④ 打开 / 创建点位目录资产", false, 13)]
        public static void OpenCatalog()
        {
            TwinCatalog catalog = LoadOrCreateCatalog();
            if (catalog == null) return;
            Selection.activeObject = catalog;
            EditorGUIUtility.PingObject(catalog);
        }

        // ================================================================ ⑤ 校验点位目录

        [MenuItem("归档/P1 孪生框架/⑤ 校验点位目录与场景一致性", false, 14)]
        public static void ValidateCatalog()
        {
            TwinCatalog catalog = AssetDatabase.LoadAssetAtPath<TwinCatalog>(CatalogPath);
            if (catalog == null)
            {
                Debug.Log("[P1] 还没有点位目录资产：" + CatalogPath);
                return;
            }

            List<string> problems = new List<string>();
            HashSet<string> ids = new HashSet<string>();

            for (int i = 0; i < catalog.Points.Count; i++)
            {
                TwinPointDefinition p = catalog.Points[i];
                if (p == null) { problems.Add("第 " + i + " 项为空"); continue; }
                if (string.IsNullOrEmpty(p.TwinId)) { problems.Add("第 " + i + " 项 TwinId 为空"); continue; }

                if (!ids.Add(p.TwinId)) problems.Add("TwinId 重复：" + p.TwinId);

                // 首尾空白在 Inspector 里几乎看不出来，但会让字符串精确匹配失败
                if (!string.Equals(p.TwinId, p.TwinId.Trim(), System.StringComparison.Ordinal))
                    problems.Add("TwinId 首尾有空白字符：\"" + p.TwinId + "\"（会导致收不到数据）");

                if (p.Metrics == null || p.Metrics.Count == 0)
                {
                    problems.Add("无指标定义：" + p.TwinId);
                    continue;
                }

                for (int k = 0; k < p.Metrics.Count; k++)
                {
                    TwinMetricDefinition m = p.Metrics[k];
                    if (m == null) { problems.Add(p.TwinId + " 第 " + k + " 个指标为空"); continue; }
                    if (string.IsNullOrEmpty(m.Metric)) problems.Add(p.TwinId + " 指标名为空");
                    if (m.Max <= m.Min) problems.Add(p.TwinId + "." + m.Metric + " 量程无效（Max <= Min）");
                    if (m.WarnLow >= m.WarnHigh) problems.Add(p.TwinId + "." + m.Metric + " 警告阈值无效（WarnLow >= WarnHigh）");
                    if (m.AlarmLow > m.WarnLow || m.AlarmHigh < m.WarnHigh)
                        problems.Add(p.TwinId + "." + m.Metric + " 告警阈值应比警告阈值更宽松");
                }
            }

            // 场景锚点 vs 目录
            TwinAnchor[] anchors = Object.FindObjectsOfType<TwinAnchor>(true);
            HashSet<string> sceneIds = new HashSet<string>();
            for (int i = 0; i < anchors.Length; i++)
            {
                sceneIds.Add(anchors[i].TwinId);
                if (!ids.Contains(anchors[i].TwinId))
                    problems.Add("场景锚点不在点位目录中：" + anchors[i].TwinId + "（路径 " + GetPath(anchors[i].transform) + "）");

                if (!string.Equals(anchors[i].TwinId, anchors[i].TwinId.Trim(), System.StringComparison.Ordinal))
                    problems.Add("场景锚点 TwinId 首尾有空白字符：\"" + anchors[i].TwinId + "\"（路径 " +
                                 GetPath(anchors[i].transform) + "），可用菜单 ⑦ 一键修正");
            }
            foreach (string id in ids)
            {
                if (!sceneIds.Contains(id)) problems.Add("目录点位没有对应场景锚点：" + id);
            }

            if (problems.Count == 0)
            {
                Debug.Log("[P1] 校验通过：点位目录 " + catalog.Count + " 个点位，场景锚点 " + anchors.Length + " 个，未发现问题。");
                return;
            }

            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append("[P1] 校验发现 ").Append(problems.Count).Append(" 个问题：\n");
            for (int i = 0; i < problems.Count; i++) sb.Append("  · ").Append(problems[i]).Append('\n');
            Debug.LogWarning(sb.ToString());
        }

        // ================================================================ ⑥ 生成 UGUI 信息面板

        [MenuItem("归档/P1 孪生框架/⑥ 生成 UGUI 信息面板（当前场景）", false, 15)]
        public static void CreateInfoPanel()
        {
            if (EditorApplication.isPlaying)
            {
                EditorUtility.DisplayDialog("请先退出播放模式", "生成操作请在编辑模式下执行。", "知道了");
                return;
            }

            EnsureEventSystem();

            GameObject root = GameObject.Find(PanelRootName);
            if (root == null)
            {
                root = new GameObject(PanelRootName, typeof(RectTransform));
                Undo.RegisterCreatedObjectUndo(root, "创建孪生 UI");
            }

            Canvas canvas = root.GetComponent<Canvas>();
            if (canvas == null) canvas = Undo.AddComponent<Canvas>(root);
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;

            CanvasScaler scaler = root.GetComponent<CanvasScaler>();
            if (scaler == null) scaler = Undo.AddComponent<CanvasScaler>(root);
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            if (root.GetComponent<GraphicRaycaster>() == null) Undo.AddComponent<GraphicRaycaster>(root);
            if (root.GetComponent<TwinDetailPanel>() == null) Undo.AddComponent<TwinDetailPanel>(root);

            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            Selection.activeObject = root;

            Debug.Log("[P1-C] 信息面板已就位：" + PanelRootName + "\n" +
                      "  · 界面元素在进入 Play 时由 TwinDetailPanel 自动构建（不依赖预制体，避免引用断裂）\n" +
                      "  · 面板显示在屏幕右上：选中点位 → 详情卡片；下方为告警列表\n" +
                      "  · F4 开关面板；左键点选点位；点击告警条目 → 选中并飞过去\n" +
                      "  · 需要 EventSystem 才能接收点击，已自动检查/创建");
        }

        private static void EnsureEventSystem()
        {
            if (Object.FindObjectOfType<UnityEngine.EventSystems.EventSystem>() != null) return;

            // 项目用旧版 Input Manager（activeInputHandler = 0），所以用 StandaloneInputModule
            GameObject go = new GameObject("EventSystem",
                typeof(UnityEngine.EventSystems.EventSystem),
                typeof(UnityEngine.EventSystems.StandaloneInputModule));
            Undo.RegisterCreatedObjectUndo(go, "创建 EventSystem");
            Debug.Log("[P1-C] 场景里没有 EventSystem，已创建（否则 UI 点击无效）。");
        }

        // ================================================================ ⑦ 修正 TwinId 首尾空白

        /// <summary>
        /// 修正 TwinId / 显示名 / 分区 / 类别 的首尾空白。
        ///
        /// 为什么需要它：TwinId 是「业务 ID ↔ 场景对象」的对齐键，在 Inspector 里
        /// 手输时很容易多敲一个空格。Unity 会把它原样序列化成 'Class2-1/Light/Main_01 '
        /// （带引号 + 尾空格），肉眼几乎看不出来，但注册表按字符串精确匹配，
        /// 结果就是该点位永远收不到数据、状态永远停在「未知」。
        /// 这正是 P1-B 场景里实际出现过的问题。
        /// </summary>
        [MenuItem("归档/P1 孪生框架/⑦ 修正 TwinId 首尾空白（当前场景）", false, 16)]
        public static void TrimTwinIds()
        {
            TwinAnchor[] anchors = Object.FindObjectsOfType<TwinAnchor>(true);
            string[] fields = { "_twinId", "_displayName", "_zone", "_category" };
            int fixedCount = 0;

            for (int i = 0; i < anchors.Length; i++)
            {
                TwinAnchor anchor = anchors[i];
                if (anchor == null) continue;

                SerializedObject so = new SerializedObject(anchor);
                bool dirty = false;

                for (int k = 0; k < fields.Length; k++)
                {
                    SerializedProperty property = so.FindProperty(fields[k]);
                    if (property == null || property.propertyType != SerializedPropertyType.String) continue;

                    string raw = property.stringValue;
                    string trimmed = raw == null ? "" : raw.Trim();
                    if (string.Equals(raw, trimmed, System.StringComparison.Ordinal)) continue;

                    property.stringValue = trimmed;
                    dirty = true;
                    Debug.Log("[P1-C] 修正 " + GetPath(anchor.transform) + " 的 " + fields[k] +
                              "：\"" + raw + "\"  →  \"" + trimmed + "\"");
                }

                if (dirty)
                {
                    so.ApplyModifiedProperties();
                    fixedCount++;
                }
            }

            if (fixedCount > 0)
            {
                EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
                Debug.Log("[P1-C] 共修正 " + fixedCount + " 个锚点（检查 " + anchors.Length + " 个）。请 Ctrl+S 保存场景，" +
                          "再执行菜单 ⑤ 复校。");
            }
            else
            {
                Debug.Log("[P1-C] 未发现带首尾空白的字段（检查 " + anchors.Length + " 个锚点）。");
            }
        }

        // ================================================================ 工具

        private static Transform FindTransformByName(string name)
        {
            GameObject direct = GameObject.Find(name);
            if (direct != null) return direct.transform;

            Transform[] all = Object.FindObjectsOfType<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].name == name) return all[i];
            }
            return null;
        }

        private static void SetField(Object target, string fieldName, object value)
        {
            if (target == null) return;

            SerializedObject so = new SerializedObject(target);
            SerializedProperty property = so.FindProperty(fieldName);
            if (property == null)
            {
                Debug.LogWarning("[P1] 字段不存在：" + target.GetType().Name + "." + fieldName);
                return;
            }

            switch (property.propertyType)
            {
                case SerializedPropertyType.String:
                    property.stringValue = value as string ?? "";
                    break;
                case SerializedPropertyType.Float:
                    property.floatValue = value is float ? (float)value : 0f;
                    break;
                case SerializedPropertyType.Integer:
                    property.intValue = value is int ? (int)value : 0;
                    break;
                case SerializedPropertyType.Boolean:
                    property.boolValue = value is bool && (bool)value;
                    break;
                case SerializedPropertyType.ObjectReference:
                    property.objectReferenceValue = value as Object;
                    break;
                default:
                    Debug.LogWarning("[P1] 暂不支持的字段类型：" + fieldName + " (" + property.propertyType + ")");
                    return;
            }

            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static string GetStringField(Object target, string fieldName)
        {
            if (target == null) return "";
            SerializedObject so = new SerializedObject(target);
            SerializedProperty property = so.FindProperty(fieldName);
            return property != null ? property.stringValue : "";
        }
    }
}

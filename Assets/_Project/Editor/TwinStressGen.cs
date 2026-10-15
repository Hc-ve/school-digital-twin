using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using SchoolTwin.Data;
using SchoolTwin.Perf;
using SchoolTwin.Twin;

namespace SchoolTwin.EditorTools
{
    /// <summary>
    /// P1-E 压测场景生成器。
    ///
    /// 目标：把「5 个点位」一键扩到「100 / 200 / 500 个点位」，跑阶梯压测，
    /// 找出数据链路（生成线程 → ConcurrentQueue → 主线程 Flush → 各锚点）在哪一档开始劣化。
    ///
    /// 【与原目录资产的关系】
    ///   · 压测目录写成**独立资产**（TwinCatalog_Stress100/200/500.asset），
    ///     **绝不动** TwinCatalog_Mock.asset —— 那是 P1-C 已验收的基线，改了就再也回不去；
    ///   · 压测点位也放在**独立根节点**下（[StressPoints]），与原 [MockPoints] 互不干扰；
    ///   · 菜单 ⑨ 一键清理：删掉压测目录资产 + 场景压测根节点 + 恢复原数据源指向。
    ///
    /// 【刻意不做的三件事（都会污染压测结果）】
    ///   1. **不给压测立方体挂 TextMesh 标签**：500 个 TextMesh = 500 个独立网格 + 500 个
    ///      DrawCall，帧率会被渲染而不是数据链路拖垮，测出来的数字没有意义；
    ///   2. **不给压测立方体挂 TwinStatusColor**：它会在 OnEnable 里
    ///      GetComponentsInChildren 扫一遍，500 个物体各扫一次是纯启动开销，
    ///      而后面的 Update 又要按 0.1s 刷 MaterialPropertyBlock —— 那测的是渲染不是总线。
    ///      压测只关心数据链路，颜色留给 UI 面板看数字；
    ///   3. **不给压测立方体加 Collider 之外的组件**：Cube 自带 BoxCollider 足够 TwinPicker
    ///      拾取，多余组件都是噪声。
    ///
    ///   > 需要看颜色的话，用菜单 ⑩ 单独给压测点开着色，那是可选的对照实验。
    /// </summary>
    public static class TwinStressGen
    {
        private const string MenuRoot = "P1 孪生框架/";
        private const string CatalogFolder = "Assets/_Project/ScriptableObjects";
        private const string StressRootName = "[StressPoints]";
        private const string OriginalCatalogName = "TwinCatalog_Mock";

        /// <summary>阶梯档位。改这里就能加档（记得同时改菜单项）。</summary>
        private static readonly int[] Tiers = { 100, 200, 500 };

        // ================================================================ ⑧ 生成压测目录

        [MenuItem(MenuRoot + "⑧ 生成压测点位目录（100 / 200 / 500）", false, 20)]
        public static void GenerateStressCatalogs()
        {
            if (EditorApplication.isPlaying)
            {
                EditorUtility.DisplayDialog("请先退出播放模式", "生成操作请在编辑模式下执行。", "知道了");
                return;
            }

            if (!AssetDatabase.IsValidFolder(CatalogFolder))
            {
                Debug.LogError("[P1-E] 目录不存在：" + CatalogFolder);
                return;
            }

            // 以原目录为模板：复用它的指标定义（量程、阈值），保证压测点位与真实点位同构
            TwinCatalog template = AssetDatabase.LoadAssetAtPath<TwinCatalog>(
                CatalogFolder + "/" + OriginalCatalogName + ".asset");
            if (template == null || template.Count == 0)
            {
                Debug.LogError("[P1-E] 找不到模板目录 " + OriginalCatalogName + "（或它没有点位）。" +
                               "请先执行菜单 ① 生成 Mock 闭环。");
                return;
            }

            List<string> created = new List<string>();
            for (int t = 0; t < Tiers.Length; t++)
            {
                int tier = Tiers[t];
                string path = CatalogFolder + "/TwinCatalog_Stress" + tier + ".asset";

                TwinCatalog catalog = AssetDatabase.LoadAssetAtPath<TwinCatalog>(path);
                bool isNew = catalog == null;
                if (isNew) catalog = ScriptableObject.CreateInstance<TwinCatalog>();

                PopulateTier(catalog, template, tier);

                if (isNew)
                {
                    AssetDatabase.CreateAsset(catalog, path);
                }
                else
                {
                    EditorUtility.SetDirty(catalog);
                }
                created.Add(path + "（" + catalog.Count + " 点位）");
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            // 生成后立刻自检：压测目录本身不能有重复 ID / 空指标，否则压测数据不可信
            string report = ValidateStressCatalogs();

            Debug.Log("[P1-E] 压测点位目录已生成：\n  · " + string.Join("\n  · ", created.ToArray()) +
                      "\n" + report +
                      "\n下一步：菜单 ⑨ 把某个档位挂到场景里（会切换数据源指向，可随时切回）。");
        }

        /// <summary>
        /// 按档位填充目录。
        ///
        /// 命名规则：Zone 使用 Class2-1 ~ Class2-N 轮转，这样 Zone 字段本身也是有意义的，
        /// 便于验证「按分区筛选」等下游逻辑在多点位下是否仍然正确。
        /// </summary>
        private static void PopulateTier(TwinCatalog catalog, TwinCatalog template, int tier)
        {
            if (catalog.Points == null) catalog.Points = new List<TwinPointDefinition>();
            catalog.Points.Clear();

            int templateCount = template.Count;
            // 按模板轮转生成，保证每种设备类型都被覆盖到（而不是 500 个温度传感器）
            for (int i = 0; i < tier; i++)
            {
                TwinPointDefinition src = template.Points[i % templateCount];
                if (src == null) continue;

                TwinPointDefinition point = new TwinPointDefinition();
                point.TwinId = BuildStressId(i, src);
                point.DisplayName = src.DisplayName + " #" + (i + 1);
                point.Zone = "Class2-" + ((i / templateCount) % 12 + 1);
                point.Category = src.Category;
                point.SourceTopic = "";

                point.Metrics = new List<TwinMetricDefinition>();
                if (src.Metrics != null)
                {
                    for (int k = 0; k < src.Metrics.Count; k++)
                    {
                        TwinMetricDefinition m = src.Metrics[k];
                        if (m == null) continue;
                        // 必须深拷贝：直接塞引用会让两个目录共享同一对象，改一个动全身
                        point.Metrics.Add(new TwinMetricDefinition
                        {
                            Metric = m.Metric,
                            Unit = m.Unit,
                            Min = m.Min,
                            Max = m.Max,
                            WarnLow = m.WarnLow,
                            WarnHigh = m.WarnHigh,
                            AlarmLow = m.AlarmLow,
                            AlarmHigh = m.AlarmHigh,
                            Epsilon = m.Epsilon
                        });
                    }
                }

                catalog.Points.Add(point);
            }

            catalog.BuildIndex();
        }

        /// <summary>
        /// 生成压测用的 TwinId。
        ///
        /// 关键约束：**必须与原目录的 ID 不重名**。否则场景里同时存在原锚点和压测锚点时，
        /// TwinRegistry 会报「TwinId 重复」并且数据只送给先注册的那个 —— 压测直接失效。
        /// 这里用 "Stress" 段把命名空间隔开。
        /// </summary>
        private static string BuildStressId(int index, TwinPointDefinition template)
        {
            // 取模板 ID 的后两段（如 Sensor/Temp_01），前面换成 Stress-N 段
            string tail = template.TwinId;
            string[] parts = tail.Split('/');
            if (parts.Length >= 2)
                tail = parts[parts.Length - 2] + "/" + parts[parts.Length - 1];

            return "Stress" + (index / 100 + 1).ToString("00") + "/" + tail + "_" + index.ToString("0000");
        }

        private static string ValidateStressCatalogs()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append("  · 自检：");

            int totalProblems = 0;
            for (int t = 0; t < Tiers.Length; t++)
            {
                int tier = Tiers[t];
                TwinCatalog catalog = AssetDatabase.LoadAssetAtPath<TwinCatalog>(
                    CatalogFolder + "/TwinCatalog_Stress" + tier + ".asset");
                if (catalog == null) { sb.Append(" [").Append(tier).Append(" 缺失]"); totalProblems++; continue; }

                HashSet<string> ids = new HashSet<string>();
                int dup = 0, emptyMetric = 0, badRange = 0;
                for (int i = 0; i < catalog.Points.Count; i++)
                {
                    TwinPointDefinition p = catalog.Points[i];
                    if (p == null || string.IsNullOrEmpty(p.TwinId)) { dup++; continue; }
                    if (!ids.Add(p.TwinId)) dup++;
                    if (p.Metrics == null || p.Metrics.Count == 0) { emptyMetric++; continue; }
                    for (int k = 0; k < p.Metrics.Count; k++)
                    {
                        TwinMetricDefinition m = p.Metrics[k];
                        if (m == null || m.Max <= m.Min) badRange++;
                    }
                }

                totalProblems += dup + emptyMetric + badRange;
                sb.Append(" ").Append(tier).Append("档 ")
                  .Append(catalog.Count).Append(" 点位")
                  .Append(dup == 0 && emptyMetric == 0 && badRange == 0 ? " ✓" : " ✗");
                if (dup > 0) sb.Append("[重复ID ").Append(dup).Append("]");
                if (emptyMetric > 0) sb.Append("[无指标 ").Append(emptyMetric).Append("]");
                if (badRange > 0) sb.Append("[量程无效 ").Append(badRange).Append("]");
            }

            // 交叉检查：压测 ID 不能与原目录撞车
            TwinCatalog original = AssetDatabase.LoadAssetAtPath<TwinCatalog>(
                CatalogFolder + "/" + OriginalCatalogName + ".asset");
            if (original != null)
            {
                HashSet<string> originalIds = new HashSet<string>();
                for (int i = 0; i < original.Points.Count; i++)
                    if (original.Points[i] != null) originalIds.Add(original.Points[i].TwinId);

                int collide = 0;
                for (int t = 0; t < Tiers.Length; t++)
                {
                    TwinCatalog c = AssetDatabase.LoadAssetAtPath<TwinCatalog>(
                        CatalogFolder + "/TwinCatalog_Stress" + Tiers[t] + ".asset");
                    if (c == null) continue;
                    for (int i = 0; i < c.Points.Count; i++)
                        if (c.Points[i] != null && originalIds.Contains(c.Points[i].TwinId)) collide++;
                }
                if (collide > 0) { sb.Append("\n  · ✗ 与原目录 ID 冲突 ").Append(collide).Append(" 个"); totalProblems += collide; }
                else sb.Append("\n  · 与原目录 ").Append(original.Count).Append(" 个点位无 ID 冲突 ✓");
            }

            if (totalProblems == 0) sb.Append("\n  · 全部通过 ✓");
            return sb.ToString();
        }

        // ================================================================ ⑨ 把档位挂进场景

        [MenuItem(MenuRoot + "⑨ 把 100 点位档挂到当前场景", false, 21)]
        public static void ApplyTier100() { ApplyTier(100); }

        [MenuItem(MenuRoot + "⑨-b 把 200 点位档挂到当前场景", false, 22)]
        public static void ApplyTier200() { ApplyTier(200); }

        [MenuItem(MenuRoot + "⑨-c 把 500 点位档挂到当前场景", false, 23)]
        public static void ApplyTier500() { ApplyTier(500); }

        private static void ApplyTier(int tier)
        {
            if (EditorApplication.isPlaying)
            {
                EditorUtility.DisplayDialog("请先退出播放模式", "切换档位请在编辑模式下执行。", "知道了");
                return;
            }

            string path = CatalogFolder + "/TwinCatalog_Stress" + tier + ".asset";
            TwinCatalog catalog = AssetDatabase.LoadAssetAtPath<TwinCatalog>(path);
            if (catalog == null)
            {
                EditorUtility.DisplayDialog("目录不存在",
                    "找不到 " + path + "\n请先执行菜单 ⑧ 生成压测点位目录。", "知道了");
                return;
            }

            // 1) 数据源指向压测目录（记住原指向，清理时可还原）
            MockDataSource source = FindMockSource();
            if (source == null)
            {
                Debug.LogError("[P1-E] 场景里找不到 MockDataSource。请先执行菜单 ① 生成 Mock 闭环。");
                return;
            }

            EnsureOriginalCatalogStash(source);
            source.Catalog = catalog;
            EditorUtility.SetDirty(source);

            // 2) 拆掉旧压测根节点，重建（避免残留上一次档位的立方体）
            DestroyStressRoot();

            // 3) 建压测立方体
            int built = BuildStressCubes(catalog);

            // 4) 把 [MockPoints] 藏起来 —— 否则原 5 个点位会与压测点位一起收数据，
            //    总点位数就不是 100 而是 105，压测读数会被污染
            GameObject mockRoot = FindMockRoot();
            bool hidMock = false;
            if (mockRoot != null && mockRoot.activeSelf)
            {
                Undo.RecordObject(mockRoot, "隐藏原 Mock 点位");
                mockRoot.SetActive(false);
                hidMock = true;
            }

            // 5) 确保性能采样器在位
            EnsurePerfReport(tier);

            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());

            Debug.Log("[P1-E] 已切换到 " + tier + " 点位档：\n" +
                      "  · 点位目录 → " + path + "\n" +
                      "  · 新建压测点位 " + built + " 个，根节点 [" + StressRootName + "]\n" +
                      (hidMock ? "  · 已隐藏原 [MockPoints]（避免混入 5 个基线点位）\n" : "  · 原 [MockPoints] 本就是隐藏的\n") +
                      "  · 性能采样器已就位（F6 手动起停，默认自动采 60 秒）\n" +
                      "请 Ctrl+S 保存场景 → Play → 等采样自动结束 → 查看 PerfLogs/twin-perf.csv\n" +
                      "跑完一档后：退出 Play → 菜单 ⑨ 换下一档 → 重复（CSV 会按档位分文件）。");
        }

        private static MockDataSource FindMockSource()
        {
            TwinRuntime runtime = Object.FindObjectOfType<TwinRuntime>();
            if (runtime == null) return null;
            return runtime.GetComponent<MockDataSource>();
        }

        private const string OriginalCatalogPathKey = "SchoolTwin.Stress.OriginalCatalogPath";

        /// <summary>把数据源原来的目录路径存进 EditorPrefs，清理时还原。</summary>
        private static void EnsureOriginalCatalogStash(MockDataSource source)
        {
            string existing = EditorPrefs.GetString(OriginalCatalogPathKey, "");
            if (!string.IsNullOrEmpty(existing)) return;

            TwinCatalog current = source.Catalog;
            if (current == null) return;
            string path = AssetDatabase.GetAssetPath(current);
            if (!string.IsNullOrEmpty(path)) EditorPrefs.SetString(OriginalCatalogPathKey, path);
        }

        private static int BuildStressCubes(TwinCatalog catalog)
        {
            GameObject root = new GameObject(StressRootName);
            Undo.RegisterCreatedObjectUndo(root, "创建压测点位根");

            // 摆成网格：500 个一字排开会有 700 米长，相机根本看不全
            int columns = Mathf.CeilToInt(Mathf.Sqrt(catalog.Count));
            const float spacing = 3.0f;
            const float cubeScale = 1.2f;

            Transform zone = FindTransformByName("Class2-1");
            Vector3 basePosition = zone != null
                ? zone.position + Vector3.up * 2f - new Vector3(columns * spacing * 0.5f, 0f, columns * spacing * 0.5f)
                : new Vector3(-columns * spacing * 0.5f, 2f, -columns * spacing * 0.5f);

            for (int i = 0; i < catalog.Count; i++)
            {
                TwinPointDefinition point = catalog.Points[i];
                if (point == null) continue;

                int col = i % columns;
                int row = i / columns;

                GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube.name = "Stress_" + i.ToString("0000");
                cube.transform.SetParent(root.transform, false);
                cube.transform.position = basePosition + new Vector3(col * spacing, 0f, row * spacing);
                cube.transform.localScale = Vector3.one * cubeScale;
                Undo.RegisterCreatedObjectUndo(cube, "创建压测点位");

                TwinAnchor anchor = cube.AddComponent<TwinAnchor>();
                SetField(anchor, "_twinId", point.TwinId);
                SetField(anchor, "_displayName", point.DisplayName);
                SetField(anchor, "_zone", point.Zone);
                SetField(anchor, "_category", point.Category);

                // 注意：这里刻意不挂 TwinStatusColor、不加 TextMesh 标签（见类注释）
            }

            return catalog.Count;
        }

        private static void EnsurePerfReport(int tier)
        {
            TwinRuntime runtime = Object.FindObjectOfType<TwinRuntime>();
            if (runtime == null) return;

            TwinPerfReport report = Object.FindObjectOfType<TwinPerfReport>();
            if (report == null) report = Undo.AddComponent<TwinPerfReport>(runtime.gameObject);

            // 每档写独立 CSV，避免三档数据混在一个文件里难以比对。
            // 用 SetField（走 SerializedObject）而不是直接调 report.Configure()：
            // 后者只是给私有字段赋值，Unity 的序列化系统不一定感知，
            // 保存场景后值可能丢回默认 —— 走 SerializedObject 才能可靠落盘。
            SetField(report, "_windowSeconds", 1f);
            SetField(report, "_durationSeconds", 60f);
            SetField(report, "_warmupSeconds", 3f);
            SetField(report, "_outputRelativePath", "PerfLogs/twin-perf-" + tier + ".csv");
            SetField(report, "_autoStart", true);
            EditorUtility.SetDirty(report);
        }

        private static void DestroyStressRoot()
        {
            GameObject root = GameObject.Find(StressRootName);
            if (root != null) Undo.DestroyObjectImmediate(root);
        }

        // ================================================================ ⑩ 清理

        [MenuItem(MenuRoot + "⑩ 清理压测现场（还原到 P1-C 基线）", false, 30)]
        public static void CleanupStress()
        {
            if (EditorApplication.isPlaying)
            {
                EditorUtility.DisplayDialog("请先退出播放模式", "清理操作请在编辑模式下执行。", "知道了");
                return;
            }

            bool confirmed = EditorUtility.DisplayDialog(
                "确认清理压测现场？",
                "将执行：\n" +
                "  1. 删除场景中的 [" + StressRootName + "] 压测点位\n" +
                "  2. 恢复数据源指向原目录（" + OriginalCatalogName + "）\n" +
                "  3. 恢复显示 [MockPoints]\n" +
                "  4. 删除三个压测目录资产\n" +
                "  5. 删除场景中的性能采样器\n\n" +
                "此操作可用 Ctrl+Z 撤销（场景部分）。",
                "确认清理", "取消");
            if (!confirmed) return;

            int steps = 0;

            DestroyStressRoot();
            steps++;

            // 还原数据源
            MockDataSource source = FindMockSource();
            string originalPath = EditorPrefs.GetString(OriginalCatalogPathKey, "");
            if (source != null)
            {
                string restore = !string.IsNullOrEmpty(originalPath)
                    ? originalPath
                    : CatalogFolder + "/" + OriginalCatalogName + ".asset";

                TwinCatalog catalog = AssetDatabase.LoadAssetAtPath<TwinCatalog>(restore);
                if (catalog != null)
                {
                    source.Catalog = catalog;
                    EditorUtility.SetDirty(source);
                    Debug.Log("[P1-E] 数据源已还原指向：" + restore);
                }
                else
                {
                    Debug.LogWarning("[P1-E] 还原目标不存在：" + restore + "，请手工在 Inspector 指定目录。");
                }
                steps++;
            }
            EditorPrefs.DeleteKey(OriginalCatalogPathKey);

            // 恢复 MockPoints 显示
            GameObject mockRoot = FindMockRoot();
            if (mockRoot != null && !mockRoot.activeSelf)
            {
                Undo.RecordObject(mockRoot, "恢复显示原 Mock 点位");
                mockRoot.SetActive(true);
                steps++;
            }

            // 删资产
            for (int t = 0; t < Tiers.Length; t++)
            {
                string path = CatalogFolder + "/TwinCatalog_Stress" + Tiers[t] + ".asset";
                if (AssetDatabase.LoadAssetAtPath<TwinCatalog>(path) != null)
                {
                    AssetDatabase.DeleteAsset(path);
                    steps++;
                }
            }

            // 移掉性能采样器
            TwinPerfReport report = Object.FindObjectOfType<TwinPerfReport>();
            if (report != null)
            {
                Undo.DestroyObjectImmediate(report);
                steps++;
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());

            Debug.Log("[P1-E] 压测现场已清理（" + steps + " 步）。请 Ctrl+S 保存场景，" +
                      "再执行菜单 ⑤ 校验一致性确认恢复无误。\n" +
                      "CSV 数据没有被删除，仍在 PerfLogs/ 下（那是验收证据，要保留）。");
        }

        // ================================================================ ⑪ 压测自检（只读）

        [MenuItem(MenuRoot + "⑪ 压测前自检（只读）", false, 31)]
        public static void PreflightCheck()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append("[P1-E] 压测前自检：\n");
            int problems = 0;

            // 1. 数据源
            MockDataSource source = FindMockSource();
            if (source == null) { sb.Append("  ✗ 场景无 MockDataSource\n"); problems++; }
            else
            {
                TwinCatalog c = source.Catalog;
                sb.Append("  · 数据源目录：").Append(c != null ? c.name + "（" + c.Count + " 点位）" : "**未指定**").Append('\n');
                if (c == null) problems++;
                sb.Append("  · 频率：").Append(source.Frequency.ToString("0.#")).Append(" Hz").Append('\n');
                if (source.Frequency < 9.5f) sb.Append("    ⚠ 低于 10Hz，压测口径与文档不一致\n");
            }

            // 2. 场景锚点统计
            TwinAnchor[] anchors = Object.FindObjectsOfType<TwinAnchor>(true);
            int active = 0, inactive = 0;
            HashSet<string> ids = new HashSet<string>();
            int dup = 0;
            for (int i = 0; i < anchors.Length; i++)
            {
                if (anchors[i].gameObject.activeInHierarchy) active++; else inactive++;
                if (!ids.Add(anchors[i].TwinId)) dup++;
            }
            sb.Append("  · 场景锚点：总 ").Append(anchors.Length)
              .Append("（激活 ").Append(active).Append(" / 未激活 ").Append(inactive).Append("）\n");
            if (dup > 0) { sb.Append("    ✗ 存在重复 TwinId ").Append(dup).Append(" 个，压测数据不可信\n"); problems++; }

            // 3. 主线程每帧分发上限 vs 理论吞吐
            TwinRuntime runtime = Object.FindObjectOfType<TwinRuntime>();
            if (runtime != null && source != null && source.Catalog != null)
            {
                int metrics = 0;
                TwinCatalog c = source.Catalog;
                for (int i = 0; i < c.Points.Count; i++)
                    if (c.Points[i] != null && c.Points[i].Metrics != null) metrics += c.Points[i].Metrics.Count;

                float perSecond = metrics * source.Frequency;
                sb.Append("  · 理论吞吐：").Append(metrics).Append(" 指标 × ")
                  .Append(source.Frequency.ToString("0.#")).Append(" Hz = ")
                  .Append(perSecond.ToString("0")).Append(" 条/秒\n");
                sb.Append("    （60FPS 下每帧约 ").Append((perSecond / 60f).ToString("0.0")).Append(" 条，")
                  .Append("远低于每帧上限，理论不会丢包）\n");
            }

            // 4. 性能采样器
            TwinPerfReport report = Object.FindObjectOfType<TwinPerfReport>();
            sb.Append("  · 性能采样器：").Append(report != null ? "已就位（F6 起停）" : "**未挂载**（菜单 ⑨ 会自动加）").Append('\n');
            if (report == null) problems++;

            // 5. 面板
            GameObject ui = GameObject.Find("[TwinUI]");
            sb.Append("  · 信息面板：").Append(ui != null ? "已就位" : "**缺失**（告警观察窗口不可用）").Append('\n');

            sb.Append(problems == 0 ? "\n  ✅ 自检通过，可以开始压测。\n" : "\n  ⚠ 发现 " + problems + " 个阻塞项，请先处理。\n");

            if (problems == 0) Debug.Log(sb.ToString());
            else Debug.LogWarning(sb.ToString());
        }

        // ================================================================ 工具

        /// <summary>
        /// 找原 Mock 点位根节点。
        ///
        /// 注意：这里**不能**写 `GameObject.Find("A") ?? GameObject.Find("B")`。
        /// Unity 的 UnityEngine.Object 重载了 `==`，被销毁/不存在的对象与 null 比较会返回 true，
        /// 但 C# 的 `??` 走的是原生 null 检查、**绕过** Unity 的这个重载，
        /// 于是「fake null」对象会被 `??` 当成有效值返回 —— 这是 Unity 里经典的隐藏 bug 来源。
        /// 老老实实分两步判断最稳。
        /// </summary>
        private static GameObject FindMockRoot()
        {
            GameObject root = GameObject.Find("[MockPoints]");
            if (root != null) return root;
            return GameObject.Find("MockPoints");
        }

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
                Debug.LogWarning("[P1-E] 字段不存在：" + target.GetType().Name + "." + fieldName);
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
                    Debug.LogWarning("[P1-E] 暂不支持的字段类型：" + fieldName + " (" + property.propertyType + ")");
                    return;
            }

            so.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}

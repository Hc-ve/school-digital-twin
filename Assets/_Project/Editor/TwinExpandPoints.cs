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
    /// 把点位从 5 个扩到 32 个：**每个房间按「有实物才建点」的规则生成**。
    ///
    /// ── 为什么是表驱动 + 几何选宿主 ────────────────────────────────
    /// 「哪个房间有哪种设备」是**业务决定**（写在 Specs 表里，一眼能看完）；
    /// 「具体挂到哪一个实例」是**几何事实**（房间里有 3 个暖气片，挂最近的那个）。
    /// 两件事分开写：改业务只动表，换场景只动几何选择，不会互相污染。
    ///
    /// ── 为什么不能靠名字猜 ────────────────────────────────────────
    /// 现场勘察（TwinExpandPoints.Scan）的结果：
    ///   · 暖气 Radiator01 / 空调 Split01* / 显示器 Monitor0* 挂在**房间节点下**
    ///   · 天花板灯 LightLum01_01（105 个）挂在 SchoolScene/DayLightsInterior
    ///   · 门 Door07a（12 个）挂在 SchoolScene/Interior/Doors
    /// 也就是说灯具和门是**全局集合**，名字里没有房间信息。
    /// 只能按「实例的世界坐标落不落在房间包围盒里」来归属 —— 这是事实判断。
    ///
    /// 批处理入口：
    ///   Unity.exe -batchmode -quit -nographics -projectPath "E:/Unity Projects/School-pro" \
    ///     -executeMethod SchoolTwin.EditorTools.TwinExpandPoints.Apply -logFile apply.log
    /// </summary>
    public static partial class TwinExpandPoints
    {
        private const string ScenePath = "Assets/TirGames/SchoolScene/Scenes/SchoolSceneDay.unity";
        private const string CatalogPath = "Assets/_Project/ScriptableObjects/TwinCatalog_Mock.asset";

        // ================================================================ 业务表

        /// <summary>
        /// 要建的点位：(房间节点名, 类别)。
        ///
        /// 为什么 Class2-1 一个都不列：它已经有 5 个点位（温湿度 / CO2 / 照明 / 空调 / 门），
        /// 是「一个房间的完整故事」的样板，重复建只会撞 TwinId。
        /// </summary>
        private static readonly string[,] Specs =
        {
            // 一层教室
            { "Class1-2", "Light" }, { "Class1-2", "Heat" }, { "Class1-2", "AC" }, { "Class1-2", "Sensor" },
            { "Class1-3", "Light" }, { "Class1-3", "Heat" }, { "Class1-3", "AC" },
            { "Class1-4", "Light" }, { "Class1-4", "Heat" }, { "Class1-4", "AC" },

            // 二层教室
            { "Class2-2", "Light" }, { "Class2-2", "Heat" },

            // 功能用房
            { "ClassComputer", "Light" }, { "ClassComputer", "Heat" },
            { "ClassComputer", "PC" }, { "ClassComputer", "Sensor" },
            { "ClassTeachers", "Light" }, { "ClassTeachers", "Heat" }, { "ClassTeachers", "PC" },
            { "ClassDirector", "Light" }, { "ClassDirector", "Heat" }, { "ClassDirector", "AC" },

            // 走廊
            { "Corridor1", "Light" }, { "Corridor1", "Heat" }, { "Corridor1", "Alarm" },
            { "Corridor2", "Light" }, { "Corridor2", "Heat" },
        };

        private static readonly Dictionary<string, string> ZoneNames = new Dictionary<string, string>
        {
            { "Class1-2", "1-2班" },
            { "Class1-3", "1-3班" },
            { "Class1-4", "1-4班" },
            { "Class2-1", "2-1班" },
            { "Class2-2", "2-2班" },
            { "ClassComputer", "计算机教室" },
            { "ClassTeachers", "教师办公室" },
            { "ClassDirector", "主任室" },
            { "Corridor1", "一层走廊" },
            { "Corridor2", "二层走廊" },
        };

        /// <summary>类别 → 点位 ID 后缀 / 中文名 / 宿主预制体匹配前缀（null = 用标记牌）。</summary>
        private static readonly Dictionary<string, string[]> Categories = new Dictionary<string, string[]>
        {
            //                              ID 后缀       中文名       预制体前缀      是否精确匹配
            { "Light",  new[] { "Main_01",     "照明",     "LightLum01_01", "exact" } },
            { "Heat",   new[] { "Radiator_01", "暖气",     "Radiator01",    "exact" } },
            { "AC",     new[] { "Unit_01",     "空调机组", "Split01",       "prefix" } },
            { "PC",     new[] { "Room_01",     "机房电脑", "Monitor",       "prefix" } },
            { "Alarm",  new[] { "Fire_01",     "烟感",     "Alarm01",       "exact" } },
            { "Sensor", new[] { "Temp_01",     "环境传感器", null,          "marker" } },
        };

        // ================================================================ 指标模板
        private static TwinMetricDefinition M(string metric, string unit, float min, float max,
            float warnLow, float warnHigh, float alarmLow, float alarmHigh)
        {
            TwinMetricDefinition m = new TwinMetricDefinition();
            m.Metric = metric;
            m.Unit = unit;
            m.Min = min;
            m.Max = max;
            m.WarnLow = warnLow;
            m.WarnHigh = warnHigh;
            m.AlarmLow = alarmLow;
            m.AlarmHigh = alarmHigh;
            m.Epsilon = 0.05f;
            return m;
        }

        /// <summary>房间 + 它的世界包围盒。用于「候选设备到底属于哪个房间」的判定。</summary>
        private sealed class RoomInfo
        {
            public Transform T;
            public Bounds B;
        }

        /// <summary>
        /// 每类的指标定义。
        ///
        /// ⚠️ 阈值不是随手填的：MockSignalGenerator 的基线是**从阈值派生**的
        /// （取 [WarnLow, WarnHigh] 内收 12%），异常事件往「余量更大」的一侧冲。
        /// 所以 WarnLow/WarnHigh 必须落在 Min/Max 之内且留有余量，否则
        /// 基线区间退化、异常事件冲不出阈值，表现为「一直正常」或「一直在报警」。
        /// </summary>
        private static List<TwinMetricDefinition> BuildMetrics(string category)
        {
            List<TwinMetricDefinition> list = new List<TwinMetricDefinition>(3);
            switch (category)
            {
                case "Light":
                    list.Add(M("Brightness", "%", 0f, 100f, 20f, 100f, 0f, 100f));
                    list.Add(M("Power", "W", 0f, 120f, 0f, 110f, 0f, 120f));
                    break;

                case "Heat":
                    // 暖气供水温度：低温热水供暖，正常 45~75℃，低于 40 就是供不上热
                    list.Add(M("SupplyTemp", "°C", 30f, 90f, 45f, 75f, 40f, 80f));
                    list.Add(M("ValveOpen", "%", 0f, 100f, 5f, 95f, 0f, 100f));
                    break;

                case "AC":
                    list.Add(M("SetTemp", "°C", 16f, 30f, 18f, 26f, 16f, 30f));
                    list.Add(M("Power", "kW", 0f, 3.5f, 0f, 3f, 0f, 3.5f));
                    break;

                case "PC":
                    list.Add(M("Power", "W", 0f, 300f, 0f, 260f, 0f, 300f));
                    list.Add(M("CPULoad", "%", 0f, 100f, 0f, 85f, 0f, 98f));
                    break;

                case "Alarm":
                    // 烟雾浓度（减光率 obs/m）：0 正常，>25 预警，>60 火警
                    list.Add(M("Smoke", "obs/m", 0f, 100f, 0f, 25f, 0f, 60f));
                    break;

                case "Sensor":
                    list.Add(M("Temperature", "°C", 10f, 35f, 16f, 28f, 10f, 32f));
                    list.Add(M("Humidity", "%RH", 20f, 80f, 30f, 65f, 20f, 75f));
                    break;
            }
            return list;
        }

        // ================================================================ 主流程

        /// <summary>
        /// 探测：打印每个房间的世界包围盒，以及所有「全局集合」设备的坐标。
        ///
        /// 【为什么必须先跑这个】
        /// Class1-4 与 Class2-2 的 XZ 完全重合（x=20.61, z=37.00），只差 3.56m 层高；
        /// ClassComputer 与 Class2-1 同理。灯具挂在 DayLightsInterior 全局集合里，
        /// 名字里没有房间信息 —— 只按 XZ 归属必然串层。
        /// 所以要先拿到**真实的层高与灯具高度**，再定 Y 窗口，而不是猜一个容差。
        /// </summary>
        public static void ProbeBounds()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (scene.path != ScenePath)
                scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            StringBuilder log = new StringBuilder();
            log.AppendLine("[PROBE] ===== 房间包围盒 =====");

            Dictionary<string, Transform> rooms = FindRooms();
            foreach (KeyValuePair<string, Transform> kv in rooms)
            {
                Bounds b = RoomBounds(kv.Value);
                log.AppendLine(string.Format(
                    "[PROBE] ROOM\t{0}\tmin=({1:F2},{2:F2},{3:F2})\tmax=({4:F2},{5:F2},{6:F2})\tfloorY={7:F2}",
                    kv.Key, b.min.x, b.min.y, b.min.z, b.max.x, b.max.y, b.max.z, b.min.y));
            }

            log.AppendLine("[PROBE] ===== 全局集合设备坐标 =====");
            Transform[] all = UnityEngine.Object.FindObjectsOfType<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                Transform t = all[i];
                if (t == null) continue;
                string pf = PrefabOf(t.gameObject);
                if (string.IsNullOrEmpty(pf)) continue;
                // 灯具族全部列出（LightLum01_01 / LightLum01_03 …），
                // 不能只认一种：ClassTeachers / ClassDirector 用的可能就是另一种。
                bool isLight = pf.StartsWith("Light", StringComparison.OrdinalIgnoreCase)
                               || pf.StartsWith("Lamp", StringComparison.OrdinalIgnoreCase);
                if (!(isLight || pf == "Alarm01")) continue;
                log.AppendLine(string.Format("[PROBE] GLOBAL\t{0}\t{1}\t({2:F2},{3:F2},{4:F2})\t父={5}",
                    pf, t.name, t.position.x, t.position.y, t.position.z,
                    t.parent != null ? t.parent.name : "(根)"));
            }

            Debug.Log(log.ToString());
            try { System.IO.File.WriteAllText("probe_bounds.txt", log.ToString(), Encoding.UTF8); }
            catch (Exception e) { Debug.LogWarning("[PROBE] 写文件失败：" + e.Message); }
        }

        /// <summary>批处理入口：扩充点位目录 + 在场景里绑宿主。</summary>
        public static void Apply()
        {
            StringBuilder log = new StringBuilder();
            log.AppendLine("[EXPAND] ===== 开始扩充点位 =====");

            Scene scene = SceneManager.GetActiveScene();
            if (scene.path != ScenePath)
            {
                scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
                log.AppendLine("[EXPAND] 已打开场景：" + scene.path);
            }

            TwinRuntime runtime = UnityEngine.Object.FindObjectOfType<TwinRuntime>();
            if (runtime == null || runtime.Catalog == null)
            {
                Debug.LogError("[EXPAND] 场景里找不到 TwinRuntime 或点位目录，中止。");
                return;
            }

            TwinCatalog catalog = runtime.Catalog;
            log.AppendLine("[EXPAND] 点位目录：" + catalog.name + "（现有 " + catalog.Points.Count + " 个）");

            // ---------- 1. 房间节点 ----------
            Dictionary<string, Transform> rooms = FindRooms();
            log.AppendLine("[EXPAND] 找到房间节点 " + rooms.Count + " 个");

            // 所有房间的包围盒都要算一遍，供「候选归属判定」用 ——
            // 判定时必须考虑**全部**房间，不只是 Specs 里列的那些：
            // 一个灯如果离隔壁教室中心更近，它就不属于本房间，哪怕坐标落在本房间包围盒内。
            List<RoomInfo> allRooms = new List<RoomInfo>();
            foreach (KeyValuePair<string, Transform> kv in rooms)
            {
                Bounds b = RoomBounds(kv.Value);
                if (b.size.sqrMagnitude < 0.01f) continue;
                RoomInfo ri = new RoomInfo();
                ri.T = kv.Value;
                ri.B = b;
                allRooms.Add(ri);
            }

            // ---------- 2. 已被占用的宿主（避免两个点位挂同一个物体）----------
            HashSet<GameObject> taken = new HashSet<GameObject>();
            TwinAnchor[] existing = UnityEngine.Object.FindObjectsOfType<TwinAnchor>(true);
            for (int i = 0; i < existing.Length; i++)
            {
                if (existing[i] == null) continue;
                taken.Add(existing[i].gameObject);
            }

            // ---------- 3. 逐个 spec ----------
            int addedPoints = 0, bound = 0, skipped = 0;
            int rows = Specs.GetLength(0);
            for (int i = 0; i < rows; i++)
            {
                string zone = Specs[i, 0];
                string category = Specs[i, 1];

                string[] meta;
                if (!Categories.TryGetValue(category, out meta))
                {
                    log.AppendLine("[EXPAND] ✘ 未知类别 " + category);
                    continue;
                }

                string twinId = zone + "/" + category + "/" + meta[0];
                string zoneName;
                if (!ZoneNames.TryGetValue(zone, out zoneName)) zoneName = zone;
                string display = zoneName + " " + meta[1];

                Transform room;
                if (!rooms.TryGetValue(zone, out room) || room == null)
                {
                    log.AppendLine("[EXPAND] ✘ 找不到房间节点 " + zone + "，跳过 " + twinId);
                    skipped++;
                    continue;
                }

                // 3.1 目录里没有就补上（幂等：已存在则不重复添加）
                if (FindPoint(catalog, twinId) == null)
                {
                    TwinPointDefinition def = new TwinPointDefinition();
                    def.TwinId = twinId;
                    def.DisplayName = display;
                    def.Zone = zone;
                    def.Category = category;
                    def.SourceTopic = "";
                    def.Metrics = BuildMetrics(category);
                    catalog.Points.Add(def);
                    addedPoints++;
                }

                // 3.2 场景里已有同 TwinId 的锚点 → 不重复绑
                TwinAnchor dup = FindAnchor(twinId);
                if (dup != null)
                {
                    log.AppendLine("[EXPAND] · 已存在 " + twinId + " → " + dup.gameObject.name + "（跳过绑定）");
                    skipped++;
                    continue;
                }

                // 3.3 绑宿主
                bool ok;
                if (meta[3] == "marker")
                {
                    ok = BindMarker(room, twinId, display, zone, category, log);
                }
                else
                {
                    ok = BindDevice(room, meta[2], meta[3] == "exact", twinId, display, zone, category, taken, allRooms, log);
                    if (!ok)
                    {
                        // 没有实物模型 → 退回标记牌。
                        // 实测场景里 ClassTeachers / ClassDirector / ClassComputer 三间
                        // **没有天花板灯具实例**（LightLum01_01 的 105 个里没有落在它们头顶的），
                        // 但照明回路在业务上是存在的，所以用 BIM 标签表达，而不是干脆不建这个点。
                        log.AppendLine("[EXPAND] · " + twinId + " 无实物模型，改用标记牌");
                        ok = BindMarker(room, twinId, display, zone, category, log);
                    }
                }

                if (ok) bound++;
                else skipped++;
            }

            // ---------- 4. 落盘 ----------
            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();

            EditorSceneManager.MarkSceneDirty(scene);
            bool saved = EditorSceneManager.SaveScene(scene);
            log.AppendLine("[EXPAND] 场景保存：" + (saved ? "成功" : "失败"));

            // ---------- 5. 验收 ----------
            log.AppendLine("[EXPAND] 新增目录条目 " + addedPoints + " 个，新绑锚点 " + bound + " 个，跳过 " + skipped + " 个");
            log.AppendLine("[EXPAND] 目录现在共 " + catalog.Points.Count + " 个点位");

            TwinAnchor[] after = UnityEngine.Object.FindObjectsOfType<TwinAnchor>(true);
            int live = 0;
            Dictionary<string, int> seen = new Dictionary<string, int>(StringComparer.Ordinal);
            log.AppendLine("[EXPAND] ---- 最终锚点清单 ----");
            List<TwinAnchor> sorted = new List<TwinAnchor>(after);
            sorted.Sort((a, b) => string.CompareOrdinal(a == null ? "" : a.TwinId, b == null ? "" : b.TwinId));
            for (int i = 0; i < sorted.Count; i++)
            {
                TwinAnchor a = sorted[i];
                if (a == null) continue;
                if (a.TwinId != null && a.TwinId.StartsWith("BAK", StringComparison.Ordinal)) continue;
                live++;
                if (seen.ContainsKey(a.TwinId)) seen[a.TwinId]++;
                else seen[a.TwinId] = 1;
                log.AppendLine(string.Format("[EXPAND] ANCHOR\t{0}\t{1}\t{2}",
                    a.TwinId, a.gameObject.name,
                    string.Format("({0:F2},{1:F2},{2:F2})", a.transform.position.x, a.transform.position.y, a.transform.position.z)));
            }
            log.AppendLine("[EXPAND] 生效锚点 " + live + " 个");

            foreach (KeyValuePair<string, int> kv in seen)
            {
                if (kv.Value > 1)
                    log.AppendLine("[EXPAND] ⚠ 重复 TwinId：" + kv.Key + " × " + kv.Value);
            }

            log.AppendLine("[EXPAND] ===== 完成 =====");
            Debug.Log(log.ToString());

            try
            {
                System.IO.File.WriteAllText("expand_points.txt", log.ToString(), Encoding.UTF8);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[EXPAND] 写文件失败：" + e.Message);
            }
        }

        // ================================================================ 绑定

        /// <summary>
        /// 把点位挂到房间内的指定设备实例上。选宿主的顺序是**先层级、后几何**：
        ///
        /// ① 先在这个房间节点自己的子树里找（暖气 / 空调 / 显示器都挂在房间下）——
        ///    层级归属是场景作者写死的，不存在歧义，优先用它；
        /// ② 子树里没有，才去全局集合里按坐标找（天花板灯、烟感在 DayLightsInterior /
        ///    Interior 下，名字里没有房间信息）。这一步必须同时比 XZ **和 Y**：
        ///    Class1-4 与 Class2-2 的 XZ 完全重合，只比 XZ 会把 2 楼的灯挂到 1 楼点位上。
        /// </summary>
        private static bool BindDevice(Transform room, string prefabKey, bool exact,
            string twinId, string display, string zone, string category,
            HashSet<GameObject> taken, List<RoomInfo> allRooms, StringBuilder log)
        {
            Transform best = null;
            int candidates = 0;
            string how = "";

            // ---------- ① 房间子树 ----------
            Transform[] inside = room.GetComponentsInChildren<Transform>(true);
            float bestDist = float.MaxValue;
            Vector3 center = room.position;
            for (int i = 0; i < inside.Length; i++)
            {
                Transform t = inside[i];
                if (t == null || t == room) continue;
                if (!PrefabMatches(t.gameObject, prefabKey, exact)) continue;
                if (taken.Contains(t.gameObject)) continue;

                candidates++;
                float d = (t.position - center).sqrMagnitude;
                if (d < bestDist) { bestDist = d; best = t; }
            }
            if (best != null) how = "房间子树";

            // ---------- ② 全局集合（按包围盒归属）----------
            if (best == null)
            {
                Bounds rb = RoomBounds(room);
                float floorY = rb.min.y;
                // Y 窗口 = [地面+0.5, 地面+3.8]。这两个数是**实测**出来的，不是估的：
                //   1 层地面 ≈ -0.03，1 层灯具 y=3.34 → 地面+3.37
                //   2 层地面 = 3.44，2 层灯具 y=6.90 → 地面+3.46
                // 层高 3.47m，所以「地面以上 3.8m 内」恰好只包含本层灯具，
                // 而 Class1-4 与 Class2-2 的 XZ 完全重合（x 12.85~24.52 / 13.43~24.52），
                // 只比 XZ 一定会把 2 层的灯挂到 1 层点位上。
                float yLo = floorY + 0.5f;
                float yHi = floorY + 3.8f;

                bestDist = float.MaxValue;
                Transform[] all = UnityEngine.Object.FindObjectsOfType<Transform>(true);
                for (int i = 0; i < all.Length; i++)
                {
                    Transform t = all[i];
                    if (t == null) continue;
                    if (!PrefabMatches(t.gameObject, prefabKey, exact)) continue;
                    if (taken.Contains(t.gameObject)) continue;

                    Vector3 p = t.position;
                    if (p.x < rb.min.x - 0.6f || p.x > rb.max.x + 0.6f) continue;
                    if (p.z < rb.min.z - 0.6f || p.z > rb.max.z + 0.6f) continue;
                    if (p.y < yLo || p.y > yHi) continue;

                    // 归属判定：只落在包围盒内还不够 —— 包围盒会互相重叠。
                    // 实测 Class2-1（x −22.55~−9.99）与 ClassDirector（x −13.04~0.53）在 x 上重叠，
                    // 只按包围盒判断会把 2-1 班最东边那盏灯挂到主任室点位上（第一次跑就发生了）。
                    // 加一条：**在同样包含该候选的房间中，本房间中心必须最近**。
                    if (!IsNearestRoom(room, rb, p, allRooms)) continue;

                    candidates++;
                    float d = (p - rb.center).sqrMagnitude;
                    if (d < bestDist) { bestDist = d; best = t; }
                }
                if (best != null) how = "全局集合/包围盒";
            }

            if (best == null)
            {
                log.AppendLine("[EXPAND] ✘ " + twinId + "：房间 " + room.name + " 内没有 " + prefabKey + " 实例");
                return false;
            }

            GameObject host = best.gameObject;
            TwinAnchor anchor = host.GetComponent<TwinAnchor>();
            if (anchor == null) anchor = host.AddComponent<TwinAnchor>();
            WriteIdentity(anchor, twinId, display, zone, category);

            if (host.GetComponent<TwinStatusColor>() == null) host.AddComponent<TwinStatusColor>();

            // 静态合批会绕过 MaterialPropertyBlock，状态色完全不生效 —— 这是静默失效，必须主动取消
            ClearBatchingStatic(host);
            EnsureCollider(host);

            taken.Add(host);

            log.AppendLine(string.Format("[EXPAND] ✔ {0} → {1}（{2}，{3}，候选 {4} 个）",
                twinId, host.name, PrefabOf(host), how, candidates));
            return true;
        }

        private static bool PrefabMatches(GameObject go, string prefabKey, bool exact)
        {
            string pf = PrefabOf(go);
            if (string.IsNullOrEmpty(pf)) return false;
            return exact ? string.Equals(pf, prefabKey, StringComparison.Ordinal)
                         : pf.StartsWith(prefabKey, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 候选设备是否「应该算本房间的」：在所有同时包含它的房间里，本房间中心必须最近。
        ///
        /// 为什么要这一步：房间包围盒**会互相重叠**（Class2-1 与 ClassDirector 在 x 上重叠约 3m，
        /// Class1-4 与 Class2-2 的 XZ 几乎完全重合）。只看「落在盒内」会张冠李戴。
        /// Y 窗口先筛掉不同楼层的房间，剩下的再比中心距离 —— 两条件缺一不可。
        /// </summary>
        private static bool IsNearestRoom(Transform room, Bounds rb, Vector3 p, List<RoomInfo> allRooms)
        {
            float myDist = (p - rb.center).sqrMagnitude;
            for (int i = 0; i < allRooms.Count; i++)
            {
                RoomInfo o = allRooms[i];
                if (o == null || o.T == room) continue;

                // 不同楼层不构成竞争
                if (p.y < o.B.min.y + 0.5f || p.y > o.B.min.y + 3.8f) continue;
                if (p.x < o.B.min.x - 0.6f || p.x > o.B.max.x + 0.6f) continue;
                if (p.z < o.B.min.z - 0.6f || p.z > o.B.max.z + 0.6f) continue;

                if ((p - o.B.center).sqrMagnitude < myDist - 0.0001f) return false;
            }
            return true;
        }

        /// <summary>
        /// 没有设备模型的点位（环境传感器）→ 在房间北墙挂一块标记牌。
        ///
        /// 位置从**房间包围盒**推出来，不是估的：
        ///   z = 北墙内表面 - 0.35（牌子厚度留出来，免得插进墙里）
        ///   y = 地面 + 1.95（与 2-1 班既有两块牌子 5.50 = 地面 3.56 + 1.94 一致）
        /// </summary>
        private static bool BindMarker(Transform room, string twinId, string display,
            string zone, string category, StringBuilder log)
        {
            Bounds rb = RoomBounds(room);
            if (rb.size.sqrMagnitude < 0.01f)
            {
                log.AppendLine("[EXPAND] ✘ " + twinId + "：房间 " + room.name + " 包围盒为空，无法定位标记牌");
                return false;
            }

            float z = rb.max.z - 0.35f;
            float y = rb.min.y + 1.95f;

            // 同一房间挂多块牌子时沿墙错开，否则会叠在一起互相遮挡
            int slot = room.GetComponentsInChildren<TwinPointMarker>(true).Length;
            float x = rb.center.x - 2.0f + slot * 1.7f;

            GameObject host = new GameObject("TwinMarker_" + twinId.Replace('/', '_'));
            host.transform.SetParent(room, false);
            host.transform.position = new Vector3(x, y, z);
            // UGUI Canvas 可见面是 -Z 侧 → 面向教室（相机在 -Z）要 yaw = 0，写 180 会看到镜像文字
            host.transform.rotation = Quaternion.Euler(0f, 0f, 0f);
            // 世界缩放显式拉回 1：牌子尺寸是按米写死的，若房间节点哪天被缩放，
            // 直接 localScale=1 会让牌子跟着变大变小（当前 19 个房间节点实测都是 1，但不能依赖这个）。
            Vector3 ps = room.lossyScale;
            host.transform.localScale = new Vector3(
                Mathf.Abs(ps.x) > 0.0001f ? 1f / ps.x : 1f,
                Mathf.Abs(ps.y) > 0.0001f ? 1f / ps.y : 1f,
                Mathf.Abs(ps.z) > 0.0001f ? 1f / ps.z : 1f);

            TwinAnchor anchor = host.AddComponent<TwinAnchor>();
            WriteIdentity(anchor, twinId, display, zone, category);

            TwinPointMarker marker = host.AddComponent<TwinPointMarker>();
            // 显式写死，不依赖脚本默认值 —— 否则改了默认值、老场景还是老尺寸
            SetFloat(marker, "_widthMeters", 0.72f);
            SetFloat(marker, "_heightMeters", 0.22f);
            SetFloat(marker, "_lift", 0f);
            SetBool(marker, "_billboard", false);

            // 标记牌是纯 UI，没有 Renderer → 不补碰撞体就点不中
            BoxCollider box = host.AddComponent<BoxCollider>();
            box.center = Vector3.zero;
            box.size = new Vector3(0.72f, 0.22f, 0.06f);

            log.AppendLine(string.Format("[EXPAND] ✔ {0} → 标记牌 @ ({1:F2},{2:F2},{3:F2})", twinId, x, y, z));
            return true;
        }

        // ================================================================ 工具

        private static Dictionary<string, Transform> FindRooms()
        {
            Dictionary<string, Transform> map = new Dictionary<string, Transform>(StringComparer.Ordinal);
            Transform[] all = UnityEngine.Object.FindObjectsOfType<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                Transform t = all[i];
                if (t == null || t.parent == null) continue;
                // 只认「直接挂在 Interior 下」的节点，避免匹配到同名的子物体
                if (t.parent.name != "Interior") continue;
                if (!map.ContainsKey(t.name)) map[t.name] = t;
            }
            return map;
        }

        /// <summary>这个 GameObject 来自哪个预制体（取文件名）。场景内建的返回「(场景内建)」。</summary>
        private static string PrefabOf(GameObject go)
        {
            if (go == null) return "";
            GameObject src = PrefabUtility.GetCorrespondingObjectFromSource(go);
            if (src == null) return "(场景内建)";
            string path = AssetDatabase.GetAssetPath(src);
            return string.IsNullOrEmpty(path) ? "(未知)" : System.IO.Path.GetFileNameWithoutExtension(path);
        }

        private static Bounds RoomBounds(Transform room)
        {
            Renderer[] rs = room.GetComponentsInChildren<Renderer>(true);
            bool any = false;
            Bounds b = new Bounds(room.position, Vector3.zero);
            for (int i = 0; i < rs.Length; i++)
            {
                if (rs[i] == null) continue;
                if (!any) { b = rs[i].bounds; any = true; }
                else b.Encapsulate(rs[i].bounds);
            }
            return b;
        }

        private static TwinPointDefinition FindPoint(TwinCatalog catalog, string twinId)
        {
            if (catalog.Points == null) return null;
            for (int i = 0; i < catalog.Points.Count; i++)
            {
                TwinPointDefinition p = catalog.Points[i];
                if (p != null && string.Equals(p.TwinId, twinId, StringComparison.Ordinal)) return p;
            }
            return null;
        }

        private static TwinAnchor FindAnchor(string twinId)
        {
            TwinAnchor[] all = UnityEngine.Object.FindObjectsOfType<TwinAnchor>(true);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null && string.Equals(all[i].TwinId, twinId, StringComparison.Ordinal))
                    return all[i];
            }
            return null;
        }

        private static void WriteIdentity(TwinAnchor anchor, string twinId, string display,
            string zone, string category)
        {
            SetString(anchor, "_twinId", twinId);
            SetString(anchor, "_displayName", display);
            SetString(anchor, "_zone", zone);
            SetString(anchor, "_category", category);
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
        /// 不能拿 bounds.size / localScale —— 模型带旋转时那样算是错的，
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
                Debug.LogWarning("[EXPAND] 字段不存在：" + target.GetType().Name + "." + fieldName);
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
                Debug.LogWarning("[EXPAND] 字段不存在：" + target.GetType().Name + "." + fieldName);
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
                Debug.LogWarning("[EXPAND] 字段不存在：" + target.GetType().Name + "." + fieldName);
                return;
            }
            p.boolValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(target);
        }
    }
}

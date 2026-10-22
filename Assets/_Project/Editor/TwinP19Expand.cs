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
    /// P19：把用户反馈里剩下的四类设备接进数据链路。
    ///
    /// ── 这一轮解决什么 ──────────────────────────────────────────────
    /// 用户提了 5 件事，其中 4 件都卡在同一个地方：**那些房间连分区都没有**，
    /// 所以既没有点位、也没有房间范围，点上去什么都不会发生。
    ///   · 大教室（礼堂 `ActZal`）的门   → 没有分区、没有点位
    ///   · 体育器材室（`SportRoom1/2/3`）的门和灯 → 同上
    ///   · 餐厅（`Kitchen` 1F）的灯       → 同上
    ///   · 投影仪 2 台                   → 有模型，但没有点位
    ///   · 上课铃                        → **场景里根本没有铃的模型**
    /// 第 5 件（电脑开关机）不需要建点位，只要表现层能画出屏幕，见 `TwinDevicePresenter.SyncScreen`。
    ///
    /// 分区矩形（`TwinRoomBounds`）和楼层表（`TwinSiteLayout`）已经在代码里加好了，
    /// 本工具只负责**场景里的锚点 + 点位目录**这两件事。
    ///
    /// ── 宿主的找法：四种规则，每种都写清依据 ─────────────────────────
    ///   · `ByPosition`   按世界坐标找（门）。坐标从场景 YAML 解析出来的，不是估的。
    ///   · `LampInZone`   在分区矩形里找灯具（灯）。**不写死坐标** —— 灯具是等距排布的，
    ///                    硬编码一个坐标，场景一挪就失效，而且没人会发现。
    ///   · `UnderRoom`    在 `Interior/<房间>` 节点下找（投影仪）。层级是**零歧义**的归属证据，
    ///                    比坐标可靠：投影仪 (-15.18, 2.92, 38.25) 与幕布 (-11.54, 3.01, 38.01)
    ///                    的 X 差了 3.6m，按坐标会判成两间房，按层级两个都在计算机教室。
    ///   · `FreeInstance` 找**还没有锚点**的同类型实例（上课铃）。走廊墙上 3 台 `Alarm01`，
    ///                    其中一台已经被 `Corridor1/Alarm/Fire_01` 占用；
    ///                    同一个物体上不放两个锚点（一个属性只能有一个主人）。
    ///
    /// ── 三道闸，和 P18 一样 ─────────────────────────────────────────
    ///   ① Play 模式下硬拒绝（`SaveScene()` 会把播放状态写进资产，2026-10-22 丢过两盏灯）
    ///   ② 先算后写：任何一个宿主找不到就整体不写（宁可一个都不补，也不要补一半）
    ///   ③ 按预制体名 + 坐标/层级匹配，不按物体名（名字不是身份，TwinId 才是）
    ///
    /// 菜单：`P19 扩点位/① 预演（只读）` → `P19 扩点位/② 执行（改场景与点位目录，会存盘）`
    /// 批处理：`-executeMethod SchoolTwin.EditorTools.TwinP19Expand.Plan` / `.Apply`
    /// </summary>
    public static class TwinP19Expand
    {
        private const string MenuRoot = "P19 扩点位/";
        internal const string ScenePath = "Assets/TirGames/SchoolScene/Scenes/SchoolSceneDay.unity";
        private const string CatalogPath = "Assets/_Project/ScriptableObjects/TwinCatalog_Mock.asset";

        /// <summary>坐标匹配容差（米）。坐标是从场景文件里读的，理论误差 0；留 0.2 只防浮点尾差。</summary>
        private const float PositionTolerance = 0.2f;

        // ================================================================ 业务表

        private enum HostKind
        {
            ByPosition,
            LampInZone,
            UnderRoom,
            FreeInstance,
        }

        private struct PointSpec
        {
            public string TwinId;
            public string Zone;
            public string Category;
            public string DisplayName;
            public HostKind Kind;
            /// <summary>宿主预制体的文件名（不含扩展名），如 Door07a / LightLum01_01 / Alarm01。</summary>
            public string Prefab;
            public float X, Y, Z;          // ByPosition 用
            public string RoomNode;        // UnderRoom 用
            public bool PreferLow;         // LampInZone：true = 取最低那一层（1F）
            public string Note;
        }

        private static readonly PointSpec[] Points =
        {
            // ---- 照明 2 盏 ----
            // ⚠️ 礼堂（ActZal）**没有**照明点位，这是有依据的、不是漏了：
            //    西翼 20 盏灯全部在 y ≈ 3.23~3.37（1F 吊顶高度），2F 高度（y≈6.9）一盏都没有。
            //    礼堂在 2F（`Seats01` 的 y=3.57 = 2F 地面），它靠高窗采光，模型里没装吊灯。
            //    要给礼堂补照明，得先补灯具 —— 那是另一件事（P17 给三间空房补过 6 盏）。
            new PointSpec
            {
                TwinId = "Kitchen/Light/Main_01", Zone = "Kitchen", Category = "Light",
                DisplayName = "食堂餐厅 照明", Kind = HostKind.LampInZone,
                Prefab = "LightLum01_01", PreferLow = true,
                Note = "1F 食堂，分区里 20 盏灯全部收进来",
            },
            new PointSpec
            {
                TwinId = "SportRoom1/Light/Main_01", Zone = "SportRoom1", Category = "Light",
                DisplayName = "体育器材室 照明", Kind = HostKind.LampInZone,
                Prefab = "LightLum01_01", PreferLow = true,
                Note = "体育馆西区，1F 三盏灯",
            },

            // ---- 门 4 扇 ----
            // 坐标 = 门的枢轴世界坐标（`_scratch/p19_rot.py` 从场景 YAML 解出来的）。
            new PointSpec
            {
                TwinId = "ActZal/Door/Entrance_01", Zone = "ActZal", Category = "Door",
                DisplayName = "礼堂 大门", Kind = HostKind.ByPosition,
                Prefab = "DoorDouble02", X = -43.09f, Y = 3.56f, Z = 30.69f,
                Note = "2F 礼堂双开门，枢轴在 (-43.09, 3.56, 30.69)",
            },
            new PointSpec
            {
                TwinId = "SportRoom1/Door/Entrance_01", Zone = "SportRoom1", Category = "Door",
                DisplayName = "体育器材室 门", Kind = HostKind.ByPosition,
                Prefab = "Door07a", X = 19.39f, Y = 0.04f, Z = 18.62f,
                Note = "东翼 1F 最西那扇",
            },
            new PointSpec
            {
                TwinId = "SportRoom2/Door/Entrance_01", Zone = "SportRoom2", Category = "Door",
                DisplayName = "体育馆中区 门", Kind = HostKind.ByPosition,
                Prefab = "Door07a", X = 19.39f, Y = 0.04f, Z = 22.63f,
                Note = "东翼 1F 中间那扇",
            },
            new PointSpec
            {
                TwinId = "SportRoom3/Door/Entrance_01", Zone = "SportRoom3", Category = "Door",
                DisplayName = "体育馆东区 门", Kind = HostKind.ByPosition,
                Prefab = "Door07a", X = 19.39f, Y = 0.04f, Z = 26.62f,
                Note = "东翼 1F 最东那扇",
            },

            // ---- 投影仪 2 台 ----
            new PointSpec
            {
                TwinId = "ClassComputer/Projector/Main_01", Zone = "ClassComputer", Category = "Projector",
                DisplayName = "计算机教室 投影仪", Kind = HostKind.UnderRoom,
                Prefab = "Projector01", RoomNode = "ClassComputer",
                Note = "1F，吊装；幕布在同一间房里",
            },
            new PointSpec
            {
                TwinId = "ClassTeachers/Projector/Main_01", Zone = "ClassTeachers", Category = "Projector",
                DisplayName = "教师办公室 投影仪", Kind = HostKind.UnderRoom,
                Prefab = "Projector01", RoomNode = "ClassTeachers",
                Note = "2F，吊装",
            },

            // ---- 上课铃 1 个 ----
            new PointSpec
            {
                TwinId = "Corridor1/Bell/Speaker_01", Zone = "Corridor1", Category = "Bell",
                DisplayName = "校园广播打铃器", Kind = HostKind.FreeInstance,
                Prefab = "Alarm01", X = -16.95f, Y = 2.74f, Z = 33.40f,
                Note = "走廊墙上的消防声光警报器（场景里没有专门的铃），挑一台没被占用的",
            },
        };

        // ================================================================ 菜单入口

        [MenuItem(MenuRoot + "① 预演：检查 9 个点位（只读，不改任何东西）", false, 10)]
        public static void PlanMenu()
        {
            string report = Plan();
            EditorUtility.DisplayDialog("P19 预演完成",
                "结果已打印到 Console，并写入工程根目录的 p19_plan.txt。\n\n" +
                "确认无误后再点「② 执行」。", "好");
            Debug.Log(report);
        }

        [MenuItem(MenuRoot + "② 执行：写入场景与点位目录（会存盘）", false, 11)]
        public static void ApplyMenu()
        {
            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorUtility.DisplayDialog("已拒绝",
                    "现在是 Play 模式。Play 模式下 SaveScene() 会把「播放中的状态」写进场景资产，" +
                    "2026-10-22 因此丢过两盏灯。\n\n请先停止 Play，再点这个菜单。", "好");
                return;
            }

            string report = Apply();
            EditorUtility.DisplayDialog("P19 执行完成", "结果已打印到 Console，并写入 p19_apply.txt。", "好");
            Debug.Log(report);
        }

        [MenuItem(MenuRoot + "③ 诊断：表现层到底能接管到什么（只读）", false, 12)]
        public static void DiagMenu()
        {
            string report = Diag();
            EditorUtility.DisplayDialog("P19 诊断完成", "结果已打印到 Console，并写入 p19_diag.txt。", "好");
            Debug.Log(report);
        }

        /// <summary>
        /// 只读诊断：模拟表现层的**找目标**逻辑，把它会接管到多少个渲染器打出来。
        ///
        /// 【为什么必须有这一步】表现层那几段代码（`BuildScreen` / `BuildProjector` / `BuildBell`）
        /// 只在 Play 模式下跑，找不到目标时只打一条日志然后**静默降级**。
        /// 也就是说：绑定工具说"9 个点位全绑上了"，但按 Play 之后屏幕不亮，
        /// 而原因可能是「按材质名找不到 Monitor04」—— 这件事在编辑器里完全看不出来。
        /// 这个诊断把同一套筛选条件在编辑器里跑一遍，答案当场就有。
        ///
        /// ⚠️ 它**复制**了表现层的筛选条件，不是调用它（表现层的方法都是私有、且依赖运行时状态）。
        /// 两边条件不一致时，这个诊断会骗人 —— 所以改动任何一边都要同步改另一边。
        /// </summary>
        public static string Diag()
        {
            Scene scene = EnsureScene();

            StringBuilder log = new StringBuilder();
            log.AppendLine("[P19D] ===== 表现层接管能力诊断（只读）=====");
            log.AppendLine("[P19D] 场景：" + scene.path);

            Renderer[] all = UnityEngine.Object.FindObjectsOfType<Renderer>(true);
            log.AppendLine("[P19D] 场景渲染器总数：" + all.Length);

            // ---- 电脑显示器：材质名 Monitor04* ----
            log.AppendLine();
            log.AppendLine("[P19D] ---- 显示器（材质名 Monitor04*）----");
            string[] screenZones = { "ClassComputer", "ClassTeachers" };
            for (int z = 0; z < screenZones.Length; z++)
            {
                string zone = screenZones[z];
                float minX, maxX, minZ, maxZ;
                bool has = TwinRoomBounds.TryGetBounds(zone, out minX, out maxX, out minZ, out maxZ);
                int n = 0;
                string sample = "";
                for (int i = 0; i < all.Length; i++)
                {
                    Renderer r = all[i];
                    if (r == null || r.sharedMaterial == null) continue;
                    if (!r.sharedMaterial.name.StartsWith("Monitor04", StringComparison.Ordinal)) continue;
                    if (has)
                    {
                        Vector3 p = r.transform.position;
                        if (!TwinRoomBounds.Contains(zone, p.x, p.z)) continue;
                    }
                    n++;
                    if (sample.Length == 0) sample = r.name + "@" + r.sharedMaterial.name;
                }
                log.AppendLine("[P19D]   " + zone + "：接管 " + n + " 台" +
                               (n > 0 ? "（例：" + sample + "）" : "  ⚠ 一台都没有 —— 屏幕不会亮"));
            }

            // ---- 投影仪幕布：按**层级**找（与 TwinDevicePresenter.BuildProjector 一致）----
            log.AppendLine();
            log.AppendLine("[P19D] ---- 投影仪幕布（按房间节点 + 名字 ProjectorScreen*）----");
            string[] projPoints = { "ClassComputer/Projector/Main_01", "ClassTeachers/Projector/Main_01" };
            for (int z = 0; z < projPoints.Length; z++)
            {
                TwinAnchor a = TwinExpandPoints.FindAnchor(projPoints[z]);
                if (a == null)
                {
                    log.AppendLine("[P19D]   ⚠ 找不到锚点：" + projPoints[z]);
                    continue;
                }

                Transform room = a.transform;
                while (room != null && room.parent != null && room.parent.name != "Interior")
                    room = room.parent;

                int n = 0;
                string sample = "";
                if (room != null)
                {
                    Renderer[] rs = room.GetComponentsInChildren<Renderer>(true);
                    for (int i = 0; i < rs.Length; i++)
                    {
                        if (rs[i] == null || rs[i].gameObject == null) continue;
                        if (!rs[i].gameObject.name.StartsWith("ProjectorScreen", StringComparison.Ordinal)) continue;
                        n++;
                        if (sample.Length == 0)
                            sample = rs[i].name + "  mat=" +
                                     (rs[i].sharedMaterial != null ? rs[i].sharedMaterial.name : "(null)") +
                                     "  hasEmission=" +
                                     (rs[i].sharedMaterial != null && rs[i].sharedMaterial.HasProperty("_EmissionColor"));
                    }
                }
                log.AppendLine("[P19D]   " + projPoints[z] + "：房间节点=" +
                               (room != null ? room.name : "(找不到)") +
                               "  幕布 " + n + " 块" +
                               (n > 0 ? "（" + sample + "）" : "  ⚠ 一块都没有 —— 幕布不会亮"));
            }

            // ---- 打铃器 ----
            log.AppendLine();
            log.AppendLine("[P19D] ---- 打铃器（Alarm01）----");
            TwinAnchor bellAnchor = TwinExpandPoints.FindAnchor("Corridor1/Bell/Speaker_01");
            if (bellAnchor == null)
            {
                log.AppendLine("[P19D]   ⚠ 找不到 Corridor1/Bell/Speaker_01 这个锚点");
            }
            else
            {
                GameObject target = bellAnchor.VisualTarget;
                if (target == null) target = bellAnchor.gameObject;
                Renderer[] rs = target.GetComponentsInChildren<Renderer>(true);
                int withEmission = 0;
                for (int i = 0; i < rs.Length; i++)
                {
                    if (rs[i] != null && rs[i].sharedMaterial != null &&
                        rs[i].sharedMaterial.HasProperty("_EmissionColor")) withEmission++;
                }
                log.AppendLine("[P19D]   宿主=" + target.name + "  渲染器=" + rs.Length +
                               "  其中支持 _EmissionColor=" + withEmission +
                               (withEmission > 0 ? "" : "  ⚠ 不会闪，只有声音"));
            }

            // ---- 灯组：每个新分区能收到几盏 ----
            log.AppendLine();
            log.AppendLine("[P19D] ---- 灯组（同层 ±1.6m + 分区矩形）----");
            string[] lampPoints = { "Kitchen/Light/Main_01", "SportRoom1/Light/Main_01" };
            for (int i = 0; i < lampPoints.Length; i++)
            {
                TwinAnchor a = TwinExpandPoints.FindAnchor(lampPoints[i]);
                if (a == null)
                {
                    log.AppendLine("[P19D]   ⚠ 找不到锚点：" + lampPoints[i]);
                    continue;
                }
                GameObject target = a.VisualTarget;
                if (target == null) target = a.gameObject;
                Renderer main = null;
                Renderer[] own = target.GetComponentsInChildren<Renderer>(true);
                for (int k = 0; k < own.Length; k++)
                {
                    if (main == null || own[k].bounds.size.sqrMagnitude > main.bounds.size.sqrMagnitude) main = own[k];
                }

                int group = 0;
                if (main != null)
                {
                    Transform container = main.transform.parent;
                    float floorY = main.transform.position.y;
                    for (int c = 0; c < container.childCount; c++)
                    {
                        Transform child = container.GetChild(c);
                        if (child == null) continue;
                        Vector3 p = child.position;
                        if (Mathf.Abs(p.y - floorY) > 1.6f) continue;
                        if (!TwinRoomBounds.Contains(a.Zone, p.x, p.z)) continue;
                        group++;
                    }
                }
                log.AppendLine("[P19D]   " + lampPoints[i] + "：代表灯=" + target.name +
                               "  整组 " + group + " 盏" +
                               (group <= 1 ? "  ⚠ 只有它自己，房间不会一起亮" : ""));
            }

            // ---- 上课铃课表 ----
            log.AppendLine();
            TwinBellScheduler scheduler = UnityEngine.Object.FindObjectOfType<TwinBellScheduler>(true);
            log.AppendLine("[P19D] ---- 上课铃课表 ----");
            if (scheduler == null)
            {
                log.AppendLine("[P19D]   ⚠ 场景里没有 TwinBellScheduler（运行时会自动补，但课表改不了）");
            }
            else
            {
                log.AppendLine("[P19D]   在 " + scheduler.gameObject.name + " 上，自动打铃=" + scheduler.AutoRing);
                // ⚠️ BellCount 是 Awake() 里解析出来的，编辑器里（不在 Play）Awake 不跑，
                //    所以这里必然是 0 —— 这不是故障，按 Play 之后才会是 14。
                log.AppendLine("[P19D]   课表条目数在这里读不到（Awake 只在 Play 时跑），" +
                               "按 Play 后 Console 会打印「[TwinBell] 课表 14 个时间点」和下一次打铃时间");
            }

            log.AppendLine();
            log.AppendLine("[P19D] ===== 诊断结束 =====");

            string text = log.ToString();
            WriteFile("p19_diag.txt", text);
            return text;
        }

        // ================================================================ 预演

        public static string Plan()
        {
            Scene scene = EnsureScene();

            StringBuilder log = new StringBuilder();
            log.AppendLine("[P19] ===== 预演（只读，不会改任何东西）=====");
            log.AppendLine("[P19] 场景：" + scene.path);

            TwinCatalog catalog = LoadCatalog(log);
            List<HostHit> hits = ResolveHosts(log);

            int ready = 0, done = 0, blocked = 0;
            log.AppendLine("[P19] ---- 逐个点位 ----");
            for (int i = 0; i < Points.Length; i++)
            {
                PointSpec s = Points[i];
                HostHit h = Find(hits, s.TwinId);
                bool inCatalog = catalog != null && FindPoint(catalog, s.TwinId) != null;

                if (h.Go == null)
                {
                    blocked++;
                    log.AppendLine("[P19] ✘ " + s.TwinId + " —— 找不到宿主（" + s.Prefab + "）" +
                                   "：**这个点位不补**。" + h.Reason);
                    continue;
                }

                if (h.ExistingAnchorTwinId != null)
                {
                    done++;
                    log.AppendLine("[P19] · " + s.TwinId + " 已有锚点（" + h.Go.name + "）—— 跳过");
                    continue;
                }

                ready++;
                Vector3 p = h.Go.transform.position;
                log.AppendLine(string.Format(
                    "[P19] ✔ {0} → {1}（{2}）实际 ({3:F2},{4:F2},{5:F2})  点位目录：{6}  {7}",
                    s.TwinId, h.Go.name, s.Note, p.x, p.y, p.z,
                    inCatalog ? "已有" : "要新增", h.Reason));
            }

            log.AppendLine("[P19] ---- 汇总 ----");
            log.AppendLine("[P19] 可补 " + ready + " 个，已存在 " + done + " 个，找不到 " + blocked + " 个");
            log.AppendLine("[P19] ===== 预演结束，什么都没改 =====");

            string text = log.ToString();
            WriteFile("p19_plan.txt", text);
            return text;
        }

        // ================================================================ 执行

        public static string Apply()
        {
            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                const string msg = "[P19] ✘ 现在是 Play 模式，拒绝执行。请先停止 Play —— " +
                                   "Play 下 SaveScene() 会把播放中的状态写进场景资产。";
                Debug.LogError(msg);
                return msg;
            }

            Scene scene = EnsureScene();

            StringBuilder log = new StringBuilder();
            log.AppendLine("[P19] ===== 执行 =====");

            TwinCatalog catalog = LoadCatalog(log);
            if (catalog == null)
            {
                const string msg = "[P19] ✘ 找不到点位目录资产，中止（场景一个字节都没改）";
                Debug.LogError(msg);
                return msg;
            }

            List<HostHit> hits = ResolveHosts(log);

            // ---- 第二道闸：先算后写 ----
            int need = 0;
            for (int i = 0; i < Points.Length; i++)
            {
                PointSpec s = Points[i];
                HostHit h = Find(hits, s.TwinId);
                if (h.Go == null)
                {
                    string msg = "[P19] ✘ " + s.TwinId + " 找不到宿主（" + s.Prefab + "）：" + h.Reason +
                                 "\n**本次一个字节都没写**，请先跑预演看全量清单。";
                    Debug.LogError(msg);
                    return msg;
                }
                if (h.ExistingAnchorTwinId == null) need++;
            }
            log.AppendLine("[P19] 命中校验通过，需要新绑 " + need + " 个点位");

            TwinStatusColor doorTemplate = FindStatusColorTemplate("Class2-1/Door/Entrance_01");
            TwinStatusColor lightTemplate = FindStatusColorTemplate("Class2-1/Light/Main_01");
            if (doorTemplate == null)
                log.AppendLine("[P19] ⚠ 没找到门的 TwinStatusColor 样板，新门用脚本默认值");
            if (lightTemplate == null)
                log.AppendLine("[P19] ⚠ 没找到灯的 TwinStatusColor 样板，新灯用脚本默认值");

            int bound = 0, addedPoints = 0;
            for (int i = 0; i < Points.Length; i++)
            {
                PointSpec s = Points[i];
                HostHit h = Find(hits, s.TwinId);
                if (h.ExistingAnchorTwinId != null) continue;

                GameObject host = h.Go;

                // 1) 锚点
                TwinAnchor anchor = host.GetComponent<TwinAnchor>();
                if (anchor == null) anchor = host.AddComponent<TwinAnchor>();
                TwinExpandPoints.SetString(anchor, "_twinId", s.TwinId);
                TwinExpandPoints.SetString(anchor, "_displayName", s.DisplayName);
                TwinExpandPoints.SetString(anchor, "_zone", s.Zone);
                TwinExpandPoints.SetString(anchor, "_category", s.Category);
                // 视觉目标留空 = 用自己。这几个点位的锚点都**直接挂在实物上**，
                // 没有"补的模型"要指过去（P13 那种标记牌 + VisualTarget 的组合这里用不上）。
                TwinExpandPoints.SetString(anchor, "_visualTargetPath", "");

                // 2) 状态色
                // 【哪些加、哪些不加】
                //   · 门：要加 —— 状态靠门扇染色透出来（P18 就是这么做的）
                //   · 灯：加（和现有 10 盏一致）；表现层会自动把它的自发光部分接管掉
                //   · 电脑 / 投影仪 / 上课铃：**不加** —— 它们的自发光归表现层，
                //     加了反而要多一处「谁在写 _EmissionColor」的排查（见 TwinStatusColor 的注释）
                bool wantStatusColor = s.Category == "Door" || s.Category == "Light";
                if (wantStatusColor && host.GetComponent<TwinStatusColor>() == null)
                {
                    TwinStatusColor template = s.Category == "Door" ? doorTemplate : lightTemplate;
                    if (template != null)
                    {
                        UnityEditorInternal.ComponentUtility.CopyComponent(template);
                        UnityEditorInternal.ComponentUtility.PasteComponentAsNew(host);
                    }
                    else
                    {
                        host.AddComponent<TwinStatusColor>();
                    }
                }
                if (!wantStatusColor)
                {
                    // 万一宿主身上本来就有（比如预制体自带的），关掉它，别让它和表现层抢自发光
                    TwinStatusColor existing = host.GetComponent<TwinStatusColor>();
                    if (existing != null) existing.enabled = false;
                }

                // 3) 门必须取消静态合批 —— 合批的物体转起来看不见（99 号清单第 21 条）
                //    灯/投影仪/铃不动，不需要
                if (s.Category == "Door") TwinExpandPoints.ClearBatchingStatic(host);

                // 4) 碰撞体（TwinPicker 走 Raycast，没有碰撞体就点不中）
                TwinExpandPoints.EnsureCollider(host);

                // 5) 点位目录
                if (FindPoint(catalog, s.TwinId) == null)
                {
                    catalog.Points.Add(BuildPoint(s));
                    addedPoints++;
                }

                bound++;
                log.AppendLine("[P19] ✔ 已绑 " + s.TwinId + " → " + host.name + "（" + h.Reason + "）");
            }

            // 6) 上课铃课表组件 —— 必须真的进场景，否则「到点自动打铃」永远不触发
            //    （99 号清单第 15 条：写了但没挂 = 快捷键永远没反应）
            bool addedScheduler = EnsureBellScheduler(log);

            // ---- 落盘 ----
            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();

            EditorSceneManager.MarkSceneDirty(scene);
            bool saved = EditorSceneManager.SaveScene(scene);
            log.AppendLine("[P19] 场景保存：" + (saved ? "成功" : "失败"));

            // ---- 验收 ----
            log.AppendLine("[P19] ---- 验收 ----");
            log.AppendLine("[P19] 新绑 " + bound + " 个点位，新增目录条目 " + addedPoints +
                           " 条，点位目录共 " + catalog.Points.Count + " 条");
            log.AppendLine("[P19] 上课铃课表组件：" + (addedScheduler ? "已挂进场景" : "场景里本来就有"));

            Dictionary<string, int> seen = new Dictionary<string, int>(StringComparer.Ordinal);
            TwinAnchor[] all = UnityEngine.Object.FindObjectsOfType<TwinAnchor>(true);
            int live = 0;
            for (int i = 0; i < all.Length; i++)
            {
                TwinAnchor a = all[i];
                if (a == null || a.TwinId == null) continue;
                if (a.TwinId.StartsWith("BAK", StringComparison.Ordinal)) continue;
                live++;
                if (seen.ContainsKey(a.TwinId)) seen[a.TwinId]++;
                else seen[a.TwinId] = 1;
            }
            log.AppendLine("[P19] 场景里有效锚点 " + live + " 个");

            foreach (KeyValuePair<string, int> kv in seen)
            {
                if (kv.Value > 1) log.AppendLine("[P19] ⚠ 重复 TwinId：" + kv.Key + " × " + kv.Value);
            }

            log.AppendLine("[P19] ===== 执行结束 =====");
            log.AppendLine("[P19] 下一步：回 Unity 按 Play，然后");
            log.AppendLine("[P19]   · 总控面板（G）左树里应该多出「食堂餐厅 / 礼堂 / 体育器材室 / 体育馆中区 / 体育馆东区」");
            log.AppendLine("[P19]   · 计算机教室的电脑点「开」→ 19 台显示器屏幕亮起来");
            log.AppendLine("[P19]   · 两间房的投影仪点「关」→ 幕布变黑");
            log.AppendLine("[P19]   · 走廊的「校园广播打铃器」点「打铃」→ 听到三音铃声 + 警报器闪 3 秒");

            string text = log.ToString();
            WriteFile("p19_apply.txt", text);
            return text;
        }

        // ================================================================ 宿主解析

        private struct HostHit
        {
            public string TwinId;
            public GameObject Go;
            public string ExistingAnchorTwinId;
            public string Reason;
        }

        private static List<HostHit> ResolveHosts(StringBuilder log)
        {
            List<HostHit> hits = new List<HostHit>(Points.Length);
            for (int i = 0; i < Points.Length; i++)
            {
                HostHit h = new HostHit();
                h.TwinId = Points[i].TwinId;
                h.Go = null;
                h.ExistingAnchorTwinId = null;
                h.Reason = "";
                hits.Add(h);
            }

            Dictionary<string, Transform> rooms = TwinExpandPoints.FindRooms();
            Transform[] all = UnityEngine.Object.FindObjectsOfType<Transform>(true);

            // ---- 先建一份「预制体 → 实例」索引，四类规则都用得上 ----
            List<Transform> prefabInstances = new List<Transform>(2048);
            for (int i = 0; i < all.Length; i++)
            {
                Transform t = all[i];
                if (t == null) continue;
                string pf = TwinExpandPoints.PrefabOf(t.gameObject);
                if (string.IsNullOrEmpty(pf) || pf == "(场景内建)") continue;
                prefabInstances.Add(t);
            }

            for (int i = 0; i < Points.Length; i++)
            {
                PointSpec s = Points[i];
                HostHit h = hits[i];
                h.Reason = "";

                switch (s.Kind)
                {
                    case HostKind.ByPosition:
                        ResolveByPosition(s, prefabInstances, ref h);
                        break;

                    case HostKind.LampInZone:
                        ResolveLampInZone(s, prefabInstances, ref h);
                        break;

                    case HostKind.UnderRoom:
                        ResolveUnderRoom(s, rooms, ref h);
                        break;

                    case HostKind.FreeInstance:
                        ResolveFreeInstance(s, prefabInstances, ref h);
                        break;
                }

                hits[i] = h;
            }

            // ---- 把「同类实例全量清单」打进日志：为什么没绑上，列出来就不用猜 ----
            log.AppendLine("[P19] ---- 宿主解析明细 ----");
            for (int i = 0; i < hits.Count; i++)
            {
                PointSpec s = Points[i];
                HostHit h = hits[i];
                log.AppendLine("[P19] " + (h.Go == null ? "✘" : "✔") + " " + s.TwinId +
                               (h.Go == null ? "" : "  →  " + h.Go.name) +
                               "  " + h.Reason);
            }

            return hits;
        }

        private static void ResolveByPosition(PointSpec s, List<Transform> pool, ref HostHit h)
        {
            Vector3 want = new Vector3(s.X, s.Y, s.Z);
            Transform best = null;
            float bestDist = float.MaxValue;
            int candidates = 0;

            for (int i = 0; i < pool.Count; i++)
            {
                Transform t = pool[i];
                if (!TwinExpandPoints.PrefabMatches(t.gameObject, s.Prefab, true)) continue;
                candidates++;

                float d = Vector3.Distance(t.position, want);
                if (d > PositionTolerance) continue;
                if (d < bestDist) { bestDist = d; best = t; }
            }

            if (best == null)
            {
                h.Reason = "场景里 " + candidates + " 个 " + s.Prefab + "，没有一个是落在 (" +
                           s.X.ToString("F2") + "," + s.Y.ToString("F2") + "," + s.Z.ToString("F2") + ") 的";
                return;
            }

            h.Go = best.gameObject;
            h.ExistingAnchorTwinId = ExistingAnchor(best.gameObject);
            h.Reason = "坐标匹配（偏差 " + bestDist.ToString("F3") + "m，" + candidates + " 个候选）";
        }

        private static void ResolveLampInZone(PointSpec s, List<Transform> pool, ref HostHit h)
        {
            float minX, maxX, minZ, maxZ;
            if (!TwinRoomBounds.TryGetBounds(s.Zone, out minX, out maxX, out minZ, out maxZ))
            {
                h.Reason = "分区「" + s.Zone + "」没在 TwinRoomBounds 里登记 —— 先补房间范围";
                return;
            }

            List<Transform> inZone = new List<Transform>(32);
            for (int i = 0; i < pool.Count; i++)
            {
                Transform t = pool[i];
                if (!TwinExpandPoints.PrefabMatches(t.gameObject, s.Prefab, true)) continue;
                Vector3 p = t.position;
                if (p.x < minX || p.x > maxX || p.z < minZ || p.z > maxZ) continue;
                inZone.Add(t);
            }

            if (inZone.Count == 0)
            {
                h.Reason = "分区「" + s.Zone + "」的矩形 x[" + minX.ToString("F1") + "," + maxX.ToString("F1") +
                           "] z[" + minZ.ToString("F1") + "," + maxZ.ToString("F1") + "] 里一盏 " + s.Prefab + " 都没有";
                return;
            }

            // 先按层取：PreferLow = 取 y 最低的那一层（1F）
            float minY = float.MaxValue, maxY = float.MinValue;
            for (int i = 0; i < inZone.Count; i++)
            {
                float y = inZone[i].position.y;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
            float floorY = s.PreferLow ? minY : maxY;

            // 再在这一层里取离分区中心最近的那一盏（作为「代表灯」）
            float cx = (minX + maxX) * 0.5f;
            float cz = (minZ + maxZ) * 0.5f;

            Transform best = null;
            float bestDist = float.MaxValue;
            int sameFloor = 0;
            for (int i = 0; i < inZone.Count; i++)
            {
                Transform t = inZone[i];
                Vector3 p = t.position;
                if (Mathf.Abs(p.y - floorY) > 0.5f) continue;
                sameFloor++;

                float d = (p.x - cx) * (p.x - cx) + (p.z - cz) * (p.z - cz);
                if (d < bestDist) { bestDist = d; best = t; }
            }

            if (best == null)
            {
                h.Reason = "分区「" + s.Zone + "」里 " + inZone.Count + " 盏灯，但同一层（y≈" +
                           floorY.ToString("F2") + "）的一盏都没挑出来";
                return;
            }

            h.Go = best.gameObject;
            h.ExistingAnchorTwinId = ExistingAnchor(best.gameObject);
            h.Reason = "分区内 " + inZone.Count + " 盏灯，本层 " + sameFloor +
                       " 盏（y≈" + floorY.ToString("F2") + "），取离中心最近的这盏作代表";
        }

        private static void ResolveUnderRoom(PointSpec s, Dictionary<string, Transform> rooms, ref HostHit h)
        {
            Transform room;
            if (!rooms.TryGetValue(s.RoomNode, out room) || room == null)
            {
                h.Reason = "场景里找不到 Interior/" + s.RoomNode + " 这个房间节点";
                return;
            }

            Transform[] kids = room.GetComponentsInChildren<Transform>(true);
            Transform best = null;
            int candidates = 0;
            for (int i = 0; i < kids.Length; i++)
            {
                Transform t = kids[i];
                if (t == room) continue;
                if (!TwinExpandPoints.PrefabMatches(t.gameObject, s.Prefab, true)) continue;
                candidates++;
                if (best == null) best = t;
            }

            if (best == null)
            {
                h.Reason = "Interior/" + s.RoomNode + " 下没有 " + s.Prefab;
                return;
            }

            h.Go = best.gameObject;
            h.ExistingAnchorTwinId = ExistingAnchor(best.gameObject);
            h.Reason = "层级匹配（Interior/" + s.RoomNode + " 下，" + candidates + " 个候选）";
        }

        private static void ResolveFreeInstance(PointSpec s, List<Transform> pool, ref HostHit h)
        {
            Vector3 want = new Vector3(s.X, s.Y, s.Z);
            Transform best = null;
            float bestDist = float.MaxValue;
            int total = 0, taken = 0;

            for (int i = 0; i < pool.Count; i++)
            {
                Transform t = pool[i];
                if (!TwinExpandPoints.PrefabMatches(t.gameObject, s.Prefab, true)) continue;
                total++;

                // 已经有锚点的跳过 —— 同一个物体上不放两个锚点
                if (ExistingAnchor(t.gameObject) != null) { taken++; continue; }

                float d = Vector3.Distance(t.position, want);
                if (d < bestDist) { bestDist = d; best = t; }
            }

            if (best == null)
            {
                h.Reason = "场景里 " + total + " 个 " + s.Prefab + "，其中 " + taken +
                           " 个已被占用，没有空闲的";
                return;
            }

            h.Go = best.gameObject;
            h.ExistingAnchorTwinId = null;
            h.Reason = "空闲实例（共 " + total + " 个，" + taken + " 个已占用），取离参考点最近的那个" +
                       "（" + bestDist.ToString("F2") + "m）";
        }

        private static HostHit Find(List<HostHit> hits, string twinId)
        {
            for (int i = 0; i < hits.Count; i++)
            {
                if (string.Equals(hits[i].TwinId, twinId, StringComparison.Ordinal)) return hits[i];
            }
            return new HostHit();
        }

        private static string ExistingAnchor(GameObject go)
        {
            TwinAnchor a = go.GetComponent<TwinAnchor>();
            if (a == null) return null;
            return string.IsNullOrEmpty(a.TwinId) ? null : a.TwinId;
        }

        // ================================================================ 资产

        private static TwinCatalog LoadCatalog(StringBuilder log)
        {
            TwinCatalog catalog = AssetDatabase.LoadAssetAtPath<TwinCatalog>(CatalogPath);
            if (catalog == null)
            {
                log.AppendLine("[P19] ✘ 读不到点位目录：" + CatalogPath);
                return null;
            }
            log.AppendLine("[P19] 点位目录：" + catalog.name + "（现有 " + catalog.Points.Count + " 条）");
            return catalog;
        }

        private static TwinPointDefinition FindPoint(TwinCatalog catalog, string twinId)
        {
            if (catalog == null || catalog.Points == null) return null;
            for (int i = 0; i < catalog.Points.Count; i++)
            {
                TwinPointDefinition p = catalog.Points[i];
                if (p != null && string.Equals(p.TwinId, twinId, StringComparison.Ordinal)) return p;
            }
            return null;
        }

        private static TwinStatusColor FindStatusColorTemplate(string twinId)
        {
            TwinAnchor a = TwinExpandPoints.FindAnchor(twinId);
            return a != null ? a.GetComponent<TwinStatusColor>() : null;
        }

        private static TwinPointDefinition BuildPoint(PointSpec s)
        {
            TwinPointDefinition def = new TwinPointDefinition();
            def.TwinId = s.TwinId;
            def.DisplayName = s.DisplayName;
            def.Zone = s.Zone;
            def.Category = s.Category;
            def.SourceTopic = "";

            def.Metrics = new List<TwinMetricDefinition>(2);

            switch (s.Category)
            {
                case "Light":
                    // 与现有 10 盏灯完全一致：亮度 0~100（警告带 20~100）、功率 0~120（警告线 110）
                    def.Metrics.Add(Metric("Brightness", "%", 0f, 100f, 20f, 100f, 0f, 100f));
                    def.Metrics.Add(Metric("Power", "W", 0f, 120f, 0f, 110f, 0f, 120f));
                    break;

                case "Door":
                    // 阈值区间必须写成 0~1，不能留空 —— 留空会让 MockSignalGenerator
                    // 退回全量程，表现为「门自己乱开合」（P18 已踩过）
                    def.Metrics.Add(Metric("Open", "0/1", 0f, 1f, 0f, 1f, 0f, 1f));
                    break;

                case "Projector":
                    // 功率 0~400W，警告线 380（开机约 310W）；灯泡时长 0~10000h，警告线 9000
                    def.Metrics.Add(Metric("Power", "W", 0f, 400f, 0f, 380f, 0f, 400f));
                    def.Metrics.Add(Metric("LampHours", "h", 0f, 10000f, 0f, 9000f, 0f, 9800f));
                    break;

                case "Bell":
                    // Ring 是开关量（0~1，下限 0 → IsDiscrete 判为离散，波形不会把它压成 0.12~0.88）
                    def.Metrics.Add(Metric("Ring", "0/1", 0f, 1f, 0f, 1f, 0f, 1f));
                    // 音量是连续量，警告带收在 55~85：MockSignalGenerator 的基线**从阈值派生**，
                    // 收窄之后模拟出来的就是一个"稳定的音量设置"，不会在 0~100 之间乱摆
                    def.Metrics.Add(Metric("Volume", "%", 0f, 100f, 55f, 85f, 0f, 100f));
                    break;

                default:
                    def.Metrics.Add(Metric("Power", "W", 0f, 1f, 0f, 1f, 0f, 1f));
                    break;
            }

            return def;
        }

        private static TwinMetricDefinition Metric(string name, string unit,
                                                   float min, float max,
                                                   float warnLow, float warnHigh,
                                                   float alarmLow, float alarmHigh)
        {
            TwinMetricDefinition m = new TwinMetricDefinition();
            m.Metric = name;
            m.Unit = unit;
            m.Min = min;
            m.Max = max;
            m.WarnLow = warnLow;
            m.WarnHigh = warnHigh;
            m.AlarmLow = alarmLow;
            m.AlarmHigh = alarmHigh;
            m.Epsilon = 0.05f;
            m.Hysteresis = 0f;
            return m;
        }

        /// <summary>
        /// 把上课铃课表组件挂进场景（`[TwinRuntime]` 上）。
        ///
        /// 【为什么必须落进场景，而不是只靠运行时自动补】
        /// 运行时补的那个用的是**代码里的默认课表**，用户在 Inspector 里改不了。
        /// 而作息时间每个学校都不一样 —— 一个改不了的课表等于没有。
        /// 挂进场景之后，选中 `[TwinRuntime]` 就能看到 `TwinBellScheduler`，直接编辑时间数组。
        /// </summary>
        private static bool EnsureBellScheduler(StringBuilder log)
        {
            TwinBellScheduler existing = UnityEngine.Object.FindObjectOfType<TwinBellScheduler>(true);
            if (existing != null)
            {
                log.AppendLine("[P19] 上课铃课表组件已在场景里：" + existing.gameObject.name +
                               "（课表 " + existing.BellCount + " 个时间点）");
                return false;
            }

            GameObject host = GameObject.Find("[TwinRuntime]");
            if (host == null)
            {
                // 兜底：挂在任意一个已有的 TwinRuntime 组件所在物体上
                TwinRuntime rt = UnityEngine.Object.FindObjectOfType<TwinRuntime>(true);
                if (rt != null) host = rt.gameObject;
            }
            if (host == null)
            {
                log.AppendLine("[P19] ⚠ 场景里找不到 [TwinRuntime]，上课铃课表组件没挂上 —— " +
                               "运行时仍会自动补一个，但课表改不了");
                return false;
            }

            host.AddComponent<TwinBellScheduler>();
            log.AppendLine("[P19] 已把 TwinBellScheduler 挂到 " + host.name + "（课表用默认作息，可在 Inspector 改）");
            return true;
        }

        // ================================================================ 工具

        private static Scene EnsureScene()
        {
            Scene scene = SceneManager.GetActiveScene();
            // 目标场景已经打开时**绝不重开** —— 重开会把用户未保存的改动丢掉
            if (string.IsNullOrEmpty(scene.path) || scene.path != ScenePath)
                scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            return scene;
        }

        private static void WriteFile(string name, string text)
        {
            try
            {
                System.IO.File.WriteAllText(name, text, Encoding.UTF8);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[P19] 写文件失败（" + name + "）：" + e.Message);
            }
        }
    }
}

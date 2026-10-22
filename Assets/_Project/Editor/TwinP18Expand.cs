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
    /// P18：把场景里**已经有实物**的门接进数据链路。
    ///
    /// ── 这一轮解决什么问题 ──────────────────────────────────────────
    /// 用户反馈「部分场景的门和灯仍无法控制」。灯是「房间没有点位」（属于扩分区，另一步），
    /// 门则是**只建了 1 个点位、场景里有 11 扇门** —— 剩下 10 扇没有 TwinAnchor，
    /// 不在白名单里，面板左树里根本不出现，自然点不了。这不是控制失效，是没建。
    ///
    /// 这一轮补 5 扇（另外 5 扇在南翼，那几间房连分区都没有，见文件末尾的说明）。
    ///
    /// ── 为什么按坐标找、不按名字找 ──────────────────────────────────
    /// 「名字不是身份，TwinId 才是」（99 号清单第 7 条）。`Door07a (7)` 这种编号是
    /// Unity 复制实例时自动加的，删一个再加一个就全变了。所以匹配用**世界坐标**，
    /// 名字只写进日志给人看。
    ///
    /// 坐标是从 `SchoolSceneDay.unity` 里直接解析出来的（Door07a 的枢轴就在门轴上，
    /// `_scratch/p18_scan.py`），并且已经用 P17 的诊断日志交叉验证过：
    /// `Door07a (7)` 解出 (-12.57, 3.59, 33.49)，与 `TwinP17Diag` 打印的枢轴完全一致。
    ///
    /// ── 为什么分「预演 / 执行」两个入口 ────────────────────────────
    /// 改场景是**不可逆**的（除非回滚 git）。2026-10-22 已经吃过一次亏：
    /// `⑪ 补灯具` 在 Play 模式下点了，主任室两盏灯从资产里消失、界面不报错。
    /// 所以：预演只读、把每一步要做什么打出来；执行前再校验一遍，
    /// **任何一扇门对不上就整体不写**（宁可一扇都不补，也不要补一半）。
    ///
    /// 菜单：`P18 补点位/① 预演（只读）` → `P18 补点位/② 执行（改场景与点位目录，会存盘）`
    /// 批处理：`-executeMethod SchoolTwin.EditorTools.TwinP18Expand.Plan` / `.Apply`
    /// </summary>
    public static class TwinP18Expand
    {
        private const string MenuRoot = "P18 补点位/";
        internal const string ScenePath = "Assets/TirGames/SchoolScene/Scenes/SchoolSceneDay.unity";
        private const string CatalogPath = "Assets/_Project/ScriptableObjects/TwinCatalog_Mock.asset";

        /// <summary>匹配容差（米）。坐标是从场景文件里读出来的，理论上误差为 0；
        /// 留 0.15m 只是防浮点与 Transform 换算的尾差，不是"差不多就行"。</summary>
        private const float PositionTolerance = 0.15f;

        // ================================================================ 业务表

        /// <summary>
        /// 要补的门。坐标 = `Door07a` 实例的枢轴世界坐标（门轴侧底角），
        /// 从场景 YAML 实测反推，不是估的。
        ///
        /// 归属判据与 P17 那扇一致：**门洞开在哪间房的墙上，就算哪间房**。
        /// 这 5 扇都在北侧教室南墙（走廊侧），X 坐标刚好落在各自房间的东端：
        ///   1F 北排四间教室的门在 x = -12.57 / -0.56 / 11.44 / 23.43，
        ///   2F 只有 x = -12.57（2-1班，已绑）和 23.43（2-2班）两扇。
        /// </summary>
        private static readonly DoorSpec[] Doors =
        {
            // 预制体         X        Y       Z       分区             房间名          备注
            new DoorSpec("Door07a", -12.57f, 0.04f, 33.49f, "ClassComputer", "计算机教室", "1F 北排最西，门轴在东端"),
            new DoorSpec("Door07a",  -0.56f, 0.04f, 33.49f, "Class1-2",      "1-2班",     "1F，紧邻 1-3 班的西侧分界"),
            new DoorSpec("Door07a",  11.44f, 0.04f, 33.44f, "Class1-3",      "1-3班",     "1F"),
            new DoorSpec("Door07a",  23.43f, 0.04f, 33.44f, "Class1-4",      "1-4班",     "1F 北排最东"),
            new DoorSpec("Door07a",  23.43f, 3.59f, 33.44f, "Class2-2",      "2-2班",     "2F（与 1-4 班 XZ 重合，靠 Y 区分）"),
        };

        private struct DoorSpec
        {
            public readonly string Prefab;
            public readonly float X, Y, Z;
            public readonly string Zone;
            public readonly string ZoneName;
            public readonly string Note;

            public DoorSpec(string prefab, float x, float y, float z,
                            string zone, string zoneName, string note)
            {
                Prefab = prefab;
                X = x; Y = y; Z = z;
                Zone = zone;
                ZoneName = zoneName;
                Note = note;
            }

            public Vector3 Position { get { return new Vector3(X, Y, Z); } }

            public string TwinId { get { return Zone + "/Door/Entrance_01"; } }

            public string DisplayName { get { return ZoneName + " 门"; } }
        }

        // ================================================================ 菜单入口

        [MenuItem(MenuRoot + "① 预演：补 5 扇门（只读，不改任何东西）", false, 10)]
        public static void PlanMenu()
        {
            string report = Plan();
            EditorUtility.DisplayDialog("P18 预演完成",
                "结果已打印到 Console，并写入工程根目录的 p18_plan.txt。\n\n" +
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
            EditorUtility.DisplayDialog("P18 执行完成", "结果已打印到 Console，并写入 p18_apply.txt。", "好");
            Debug.Log(report);
        }

        // ================================================================ 预演

        /// <summary>
        /// 只读预演：找出每一扇目标门、检查它现在有没有锚点、点位目录里有没有对应条目。
        /// **不写场景、不存盘、不改资产。**
        /// </summary>
        public static string Plan()
        {
            Scene scene = EnsureScene();

            StringBuilder log = new StringBuilder();
            log.AppendLine("[P18] ===== 预演（只读，不会改任何东西）=====");
            log.AppendLine("[P18] 场景：" + scene.path);

            List<DoorHit> hits = ScanDoors(log);

            TwinCatalog catalog = LoadCatalog(log);

            // ---- 逐扇门的结论 ----
            int ready = 0, done = 0, blocked = 0;
            log.AppendLine("[P18] ---- 逐扇门 ----");
            for (int i = 0; i < Doors.Length; i++)
            {
                DoorSpec d = Doors[i];
                DoorHit hit = Find(hits, d);
                bool inCatalog = catalog != null && FindPoint(catalog, d.TwinId) != null;

                if (hit.Go == null)
                {
                    blocked++;
                    log.AppendLine(string.Format("[P18] ✘ {0}  期望坐标 ({1:F2},{2:F2},{3:F2}) —— " +
                        "场景里找不到这个位置上的 {4}，**这一扇不补**", d.TwinId, d.X, d.Y, d.Z, d.Prefab));
                    continue;
                }

                if (hit.ExistingAnchorTwinId != null)
                {
                    done++;
                    log.AppendLine(string.Format("[P18] · {0}  已有锚点（{1}）—— 跳过，不重复绑",
                        d.TwinId, hit.Go.name));
                    continue;
                }

                ready++;
                log.AppendLine(string.Format("[P18] ✔ {0} → {1}（{2}）实际坐标 ({3:F2},{4:F2},{5:F2})  " +
                    "点位目录：{6}",
                    d.TwinId, hit.Go.name, d.Note,
                    hit.Go.transform.position.x, hit.Go.transform.position.y, hit.Go.transform.position.z,
                    inCatalog ? "已有" : "**要新增**"));
            }

            log.AppendLine("[P18] ---- 汇总 ----");
            log.AppendLine("[P18] 可补 " + ready + " 扇，已存在 " + done + " 扇，找不到 " + blocked + " 扇");
            log.AppendLine("[P18] 门锚点总数现在 " + CountAnchors("Door") + " 个，补完后应为 " +
                           (CountAnchors("Door") + ready) + " 个");
            log.AppendLine("[P18] ===== 预演结束，什么都没改 =====");

            string text = log.ToString();
            WriteFile("p18_plan.txt", text);
            return text;
        }

        // ================================================================ 执行

        /// <summary>执行：补锚点 + 补点位目录。**任何一扇门对不上就整体不写。**</summary>
        public static string Apply()
        {
            // 【第一道闸】Play 模式下存盘会把播放状态写进资产。
            // 2026-10-22 的 `⑪ 补灯具` 就是这么丢掉主任室两盏灯的：界面不报错，只是"点开灯不亮"。
            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                const string msg = "[P18] ✘ 现在是 Play 模式，拒绝执行。请先停止 Play —— " +
                                   "Play 下 SaveScene() 会把播放中的状态写进场景资产。";
                Debug.LogError(msg);
                return msg;
            }

            Scene scene = EnsureScene();

            StringBuilder log = new StringBuilder();
            log.AppendLine("[P18] ===== 执行 =====");

            List<DoorHit> hits = ScanDoors(log);

            TwinCatalog catalog = LoadCatalog(log);
            if (catalog == null)
            {
                const string msg = "[P18] ✘ 找不到点位目录资产，中止（场景一个字节都没改）";
                Debug.LogError(msg);
                return msg;
            }

            // ---- 【第二道闸】先算后写：全部命中检查通过才动手 ----
            int need = 0;
            for (int i = 0; i < Doors.Length; i++)
            {
                DoorSpec d = Doors[i];
                DoorHit hit = Find(hits, d);
                if (hit.Go == null)
                {
                    // 找不到 = 场景被改过（门被删了或挪了）。这种情况**不猜、不凑**，
                    // 整体放弃，让人去看一眼。
                    string msg = "[P18] ✘ " + d.TwinId + " 期望位置 (" + d.X.ToString("F2") + "," +
                                 d.Y.ToString("F2") + "," + d.Z.ToString("F2") +
                                 ") 上找不到 " + d.Prefab + "。\n" +
                                 "可能门被删掉或挪动了。**本次一个字节都没写**，请先跑预演看全量清单。";
                    Debug.LogError(msg);
                    return msg;
                }
                if (hit.ExistingAnchorTwinId == null) need++;
            }
            log.AppendLine("[P18] 命中校验通过，需要新绑 " + need + " 扇门");

            // ---- 找一个「样板」门的 TwinStatusColor，用来复制参数 ----
            // 为什么不 AddComponent 就完事：`2-1班 前门` 那上的 TwinStatusColor 是调过的
            // （_tintOnlyWhenAbnormal=1、_tintStrength=0.3 …），新门用脚本默认值会长得不一样。
            // 复制组件 = 复制它全部序列化字段，最不容易出错。
            TwinStatusColor template = FindDoorStatusColorTemplate();
            if (template == null)
                log.AppendLine("[P18] ⚠ 没找到可参考的 TwinStatusColor（2-1班 门），" +
                               "新门将使用脚本默认值 —— 参数可能和那扇不一致");

            int bound = 0, addedPoints = 0;
            for (int i = 0; i < Doors.Length; i++)
            {
                DoorSpec d = Doors[i];
                DoorHit hit = Find(hits, d);
                if (hit.ExistingAnchorTwinId != null) continue;

                GameObject host = hit.Go;

                // 1) 锚点
                TwinAnchor anchor = host.GetComponent<TwinAnchor>();
                if (anchor == null) anchor = host.AddComponent<TwinAnchor>();
                TwinExpandPoints.SetString(anchor, "_twinId", d.TwinId);
                TwinExpandPoints.SetString(anchor, "_displayName", d.DisplayName);
                TwinExpandPoints.SetString(anchor, "_zone", d.Zone);
                TwinExpandPoints.SetString(anchor, "_category", "Door");
                // 视觉目标留空 = 用自己（门就在这个物体上，没有"补的模型"要指过去）
                TwinExpandPoints.SetString(anchor, "_visualTargetPath", "");

                // 2) 状态色（复制样板；拿不到样板才退化成默认值）
                if (host.GetComponent<TwinStatusColor>() == null)
                {
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

                // 3) 静态合批必须取消 —— 合批的物体转起来看不见（99 号清单第 21 条）
                TwinExpandPoints.ClearBatchingStatic(host);

                // 4) 没有碰撞体就补一个，否则 TwinPicker 的射线点不中（第 15/16 条）
                TwinExpandPoints.EnsureCollider(host);

                // 5) 点位目录
                if (FindPoint(catalog, d.TwinId) == null)
                {
                    catalog.Points.Add(BuildDoorPoint(d));
                    addedPoints++;
                }

                bound++;
                log.AppendLine("[P18] ✔ 已绑 " + d.TwinId + " → " + host.name +
                               "（" + d.Note + "）");
            }

            // ---- 落盘 ----
            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();

            EditorSceneManager.MarkSceneDirty(scene);
            bool saved = EditorSceneManager.SaveScene(scene);
            log.AppendLine("[P18] 场景保存：" + (saved ? "成功" : "失败"));

            // ---- 验收 ----
            log.AppendLine("[P18] ---- 验收 ----");
            log.AppendLine("[P18] 新绑 " + bound + " 扇，新增点位 " + addedPoints + " 条，点位目录共 " +
                           catalog.Points.Count + " 条");

            Dictionary<string, int> seen = new Dictionary<string, int>(StringComparer.Ordinal);
            TwinAnchor[] all = UnityEngine.Object.FindObjectsOfType<TwinAnchor>(true);
            int doorAnchors = 0;
            for (int i = 0; i < all.Length; i++)
            {
                TwinAnchor a = all[i];
                if (a == null || a.TwinId == null) continue;
                if (a.TwinId.StartsWith("BAK", StringComparison.Ordinal)) continue;
                if (!string.Equals(a.Category, "Door", StringComparison.Ordinal)) continue;
                doorAnchors++;
                if (seen.ContainsKey(a.TwinId)) seen[a.TwinId]++;
                else seen[a.TwinId] = 1;
                log.AppendLine(string.Format("[P18] DOOR\t{0}\t{1}\t({2:F2},{3:F2},{4:F2})",
                    a.TwinId, a.gameObject.name,
                    a.transform.position.x, a.transform.position.y, a.transform.position.z));
            }
            log.AppendLine("[P18] 门锚点 " + doorAnchors + " 个");

            foreach (KeyValuePair<string, int> kv in seen)
            {
                if (kv.Value > 1)
                    log.AppendLine("[P18] ⚠ 重复 TwinId：" + kv.Key + " × " + kv.Value);
            }

            log.AppendLine("[P18] ===== 执行结束 =====");
            log.AppendLine("[P18] 下一步：回 Unity 按 Play，面板左树里应该能看到这 5 扇门；" +
                           "点「关」门会绕门轴转回贴墙。");

            string text = log.ToString();
            WriteFile("p18_apply.txt", text);
            return text;
        }

        // ================================================================ 扫描

        private struct DoorHit
        {
            public DoorSpec Spec;
            public GameObject Go;
            public string ExistingAnchorTwinId;
        }

        /// <summary>
        /// 把场景里所有 `Door07a` 实例列出来，并给每扇目标门找宿主。
        /// 顺便把**全量清单**打进日志 —— 「为什么这扇没绑」这种问题，列出来就不用猜。
        /// </summary>
        private static List<DoorHit> ScanDoors(StringBuilder log)
        {
            List<DoorHit> hits = new List<DoorHit>(Doors.Length);
            for (int i = 0; i < Doors.Length; i++)
            {
                DoorHit h = new DoorHit();
                h.Spec = Doors[i];
                h.Go = null;
                h.ExistingAnchorTwinId = null;
                hits.Add(h);
            }

            Transform[] all = UnityEngine.Object.FindObjectsOfType<Transform>(true);

            // ---- 全量清单（证据）----
            log.AppendLine("[P18] ---- 场景里所有 Door07a ----");
            int total = 0;
            List<string> listing = new List<string>();
            for (int i = 0; i < all.Length; i++)
            {
                Transform t = all[i];
                if (t == null) continue;
                if (!TwinExpandPoints.PrefabMatches(t.gameObject, "Door07a", true)) continue;

                total++;
                Vector3 p = t.position;
                TwinAnchor a = t.GetComponent<TwinAnchor>();
                string id = a != null ? a.TwinId : "";
                listing.Add(string.Format("[P18]   {0,-16} ({1,7:F2},{2,6:F2},{3,7:F2})  {4}",
                    t.name, p.x, p.y, p.z,
                    string.IsNullOrEmpty(id) ? "（无锚点）" : "→ " + id));

                // 匹配：只比坐标，不比名字
                for (int k = 0; k < hits.Count; k++)
                {
                    if (hits[k].Go != null) continue;
                    if (Vector3.Distance(p, hits[k].Spec.Position) > PositionTolerance) continue;
                    DoorHit h = hits[k];
                    h.Go = t.gameObject;
                    h.ExistingAnchorTwinId = string.IsNullOrEmpty(id) ? null : id;
                    hits[k] = h;
                    break;
                }
            }
            listing.Sort(StringComparer.Ordinal);
            for (int i = 0; i < listing.Count; i++) log.AppendLine(listing[i]);
            log.AppendLine("[P18] 共 " + total + " 扇 Door07a");

            return hits;
        }

        private static DoorHit Find(List<DoorHit> hits, DoorSpec spec)
        {
            for (int i = 0; i < hits.Count; i++)
                if (string.Equals(hits[i].Spec.TwinId, spec.TwinId, StringComparison.Ordinal))
                    return hits[i];
            return new DoorHit();
        }

        // ================================================================ 资产

        private static TwinCatalog LoadCatalog(StringBuilder log)
        {
            TwinCatalog catalog = AssetDatabase.LoadAssetAtPath<TwinCatalog>(CatalogPath);
            if (catalog == null)
            {
                log.AppendLine("[P18] ✘ 读不到点位目录：" + CatalogPath);
                return null;
            }
            log.AppendLine("[P18] 点位目录：" + catalog.name + "（现有 " + catalog.Points.Count + " 条）");
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

        /// <summary>
        /// 门点位。指标照抄 `Class2-1/Door/Entrance_01`：
        /// 只有 `Open` 一个 0/1 量，阈值区间也写成 0~1（不要留空 ——
        /// 阈值区间无效会让 MockSignalGenerator 退回全量程，表现为「门自己乱开合」）。
        /// </summary>
        private static TwinPointDefinition BuildDoorPoint(DoorSpec d)
        {
            TwinPointDefinition def = new TwinPointDefinition();
            def.TwinId = d.TwinId;
            def.DisplayName = d.DisplayName;
            def.Zone = d.Zone;
            def.Category = "Door";
            def.SourceTopic = "";

            TwinMetricDefinition m = new TwinMetricDefinition();
            m.Metric = "Open";
            m.Unit = "0/1";
            m.Min = 0f;
            m.Max = 1f;
            m.WarnLow = 0f;
            m.WarnHigh = 1f;
            m.AlarmLow = 0f;
            m.AlarmHigh = 1f;
            m.Epsilon = 0.05f;
            def.Metrics = new List<TwinMetricDefinition>(1);
            def.Metrics.Add(m);

            return def;
        }

        /// <summary>拿 `Class2-1/Door/Entrance_01` 那扇门的 TwinStatusColor 当样板。</summary>
        private static TwinStatusColor FindDoorStatusColorTemplate()
        {
            TwinAnchor[] all = UnityEngine.Object.FindObjectsOfType<TwinAnchor>(true);
            for (int i = 0; i < all.Length; i++)
            {
                TwinAnchor a = all[i];
                if (a == null) continue;
                if (!string.Equals(a.TwinId, "Class2-1/Door/Entrance_01", StringComparison.Ordinal)) continue;
                return a.GetComponent<TwinStatusColor>();
            }
            return null;
        }

        // ================================================================ 工具

        private static Scene EnsureScene()
        {
            Scene scene = SceneManager.GetActiveScene();
            // 【只在活动场景不是目标场景时才切】
            // 目标场景已经打开时**绝不重开** —— 重开会把用户未保存的改动丢掉。
            // 活动场景是别的场景且是脏的，OpenScene 会弹「要不要保存」，由用户决定，
            // 这里不替用户做主（2026-10-22 的教训：工具替人做主存盘，丢过东西）。
            if (string.IsNullOrEmpty(scene.path) || scene.path != ScenePath)
                scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            return scene;
        }

        private static int CountAnchors(string category)
        {
            TwinAnchor[] all = UnityEngine.Object.FindObjectsOfType<TwinAnchor>(true);
            int n = 0;
            for (int i = 0; i < all.Length; i++)
            {
                TwinAnchor a = all[i];
                if (a == null || a.TwinId == null) continue;
                if (a.TwinId.StartsWith("BAK", StringComparison.Ordinal)) continue;
                if (!string.Equals(a.Category, category, StringComparison.Ordinal)) continue;
                n++;
            }
            return n;
        }

        private static void WriteFile(string name, string text)
        {
            try
            {
                System.IO.File.WriteAllText(name, text, Encoding.UTF8);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[P18] 写文件失败（" + name + "）：" + e.Message);
            }
        }
    }
}

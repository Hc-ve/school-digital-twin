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
    /// 给「没有灯具模型」的三间房补天花板灯，每间 2 盏，并把照明点位接到新灯上。
    ///
    /// ── 为什么需要这一步 ──────────────────────────────────────────
    /// P4-6 的记录：ClassComputer / ClassDirector / ClassTeachers 三间房的天花板上
    /// 没有 LightLum01_01 实例（105 个灯具里没有一盏落在它们头顶），所以那三个照明点位
    /// 退回了「标记牌」表达。后果是 P13 的表现层无处发光 —— 点「开」只有数字变。
    ///
    /// ── 摆放规则（为什么不是随手放）──────────────────────────────
    /// 1. **高度和朝向抄隔壁**：不按「地面 + 3.4」硬算，而是找同层最近的既有灯具，
    ///    复制它的 y 和 rotation。层高 3.47m，1F 灯具 3.34、2F 6.90，
    ///    差 0.1m 肉眼就能看出「这盏灯装歪了」。
    /// 2. **沿长边分三等份**：两盏灯放在房间长边的 1/3、2/3 处，短边居中。
    ///    这是教室里灯排的排法，比「放在正中间」像样。
    /// 3. **必须离本房间中心最近**：房间包围盒会互相重叠（Class2-1 与 ClassDirector
    ///    在 x 上重叠约 3m），如果算出来的位置离隔壁中心更近，就往本房间中心收，
    ///    最多收 3 次，收不动就报警不硬放。
    ///
    /// ── 为什么把锚点留在标记牌上，只设 _visualTarget ──────────────
    /// 锚点搬到灯上的话，墙上的 TwinPointMarker 就拿不到数据了（它读的是同物体的 TwinAnchor）。
    /// 而 TwinAnchor 本来就有 VisualTarget 这个间接层，TwinStatusColor 早就在用它。
    /// 所以：**锚点留在牌子上管数据，VisualTarget 指向灯管管发光** —— 两件事分开，
    /// 既不用搬组件，也不破坏「牌子显示数值」这个既有行为。
    ///
    /// 批处理入口：
    ///   Unity.exe -batchmode -quit -nographics -projectPath "E:/Unity Projects/School-pro" ^
    ///     -executeMethod SchoolTwin.EditorTools.TwinAddMissingLamps.Apply -logFile add_lamps.log
    /// </summary>
    public static class TwinAddMissingLamps
    {
        private const string LampPrefabPath =
            "Assets/TirGames/SchoolScene/Models/Interior/Lights/Prefabs/LightLum01_01.prefab";

        /// <summary>灯具在场景里的全局集合节点（名字里没有房间信息，只能按坐标归属）。</summary>
        private const string LampParentName = "DayLightsInterior";

        /// <summary>要补灯的房间节点名（与 Interior 下的节点同名）。</summary>
        private static readonly string[] Zones = { "ClassComputer", "ClassDirector", "ClassTeachers" };

        /// <summary>每间房放几盏。</summary>
        private const int LampsPerRoom = 2;

        /// <summary>新灯的名字前缀。重跑时按这个前缀清理上一次的结果，保证幂等。</summary>
        private const string NamePrefix = "TwinLamp_";

        /// <summary>
        /// 【菜单为什么挪进「⚠ 一次性迁移」并且写着「无需再点」】
        /// 它是一次性的场景迁移工具，不是功能。2026-10-22 的实际情况是：
        /// 用户在菜单里看到「⑪ 给三间房补灯具」，以为是还没做的任务，就在 **Play 模式**下点了 ——
        /// 结果报 MissingReferenceException、灯没多出来、**而且场景文件被改坏了**。
        /// 一个做完就不该再点的工具，不能长得像待办事项。
        /// </summary>
        [MenuItem("归档/P1 孪生框架/⚠ 一次性迁移（已执行过，无需再点）/⑪ 给三间房补灯具（每间 2 盏）", false, 23)]
        public static void ApplyFromMenu()
        {
            string message;
            bool ok = Run(out message);
            EditorUtility.DisplayDialog(ok ? "补灯具：完成" : "补灯具：没有执行", message, "好");
        }

        /// <summary>批处理入口（返回值被忽略，失败时由 Run 内部打 error）。</summary>
        public static void Apply()
        {
            string message;
            if (!Run(out message)) Debug.LogError("[LAMP] " + message);
        }

        /// <summary>只读检查：三间房的灯到底在不在、锚点接没接上。不碰场景，随便点。</summary>
        [MenuItem("归档/P1 孪生框架/⚠ 一次性迁移（已执行过，无需再点）/⑫ 检查三间房灯具（只读，安全）", false, 24)]
        public static void InspectFromMenu()
        {
            int missing;
            Inspect(out missing);

            EditorUtility.DisplayDialog("三间房灯具检查",
                missing == 0 ? "三间房都正常，不需要再做任何事。\n\n详情见 Console。"
                : missing < 0 ? "当前打开的不是目标场景，没有检查。\n\n请先打开 SchoolSceneDay.unity。"
                : "有 " + missing + " 间不正常。\n\n详情见 Console。", "好");
        }

        /// <summary>
        /// 只读检查的批处理入口（不弹窗，只打日志）。
        /// 拆出来是为了能在 `-batchmode` 下跑 —— 弹窗在批处理里会把进程卡住。
        /// </summary>
        public static void Inspect()
        {
            int missing;
            Inspect(out missing);
            if (missing > 0) Debug.LogWarning("[LAMP] 有 " + missing + " 间房的灯不正常，见上。");
        }

        private static void Inspect(out int missing)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[LAMP] ===== 三间房灯具检查（只读）=====");

            // 【必须先确保看的是对的那个场景】
            // 批处理（-batchmode -executeMethod）**不会自动加载场景**，活动场景是空的。
            // 不打开的话这里会读到 0 盏灯、0 个锚点，然后报「三间房都没有灯」——
            // 一个看起来很像结论的假结论。（2026-10-22 第一次跑就是这么被骗的。）
            //
            // 但**只在「活动场景没有路径」时才自动打开**（那就是批处理的情形）。
            // 用户开着别的场景时不去动它：`OpenScene(Single)` 会把未保存的修改直接丢掉，
            // 而且不弹任何提示 —— 一个「只读检查」把人家的改动弄没了，是最糟的副作用。
            Scene scene = SceneManager.GetActiveScene();
            if (string.IsNullOrEmpty(scene.path))
            {
                scene = EditorSceneManager.OpenScene(TwinExpandPoints.ScenePath, OpenSceneMode.Single);
            }
            else if (scene.path != TwinExpandPoints.ScenePath)
            {
                Debug.LogWarning("[LAMP] 当前打开的是 " + scene.path + "，不是 " + TwinExpandPoints.ScenePath +
                                 "。请先打开目标场景再检查 —— 这里不会替你切场景（切场景会丢掉未保存的修改）。");
                missing = -1;
                return;
            }
            sb.AppendLine("[LAMP] 检查的场景：" + scene.path);

            missing = 0;
            for (int z = 0; z < Zones.Length; z++)
            {
                string zone = Zones[z];
                string twinId = zone + "/Light/Main_01";

                int lampCount = CountLamps(zone);
                TwinAnchor anchor = TwinExpandPoints.FindAnchor(twinId);
                GameObject vt = anchor != null ? anchor.VisualTarget : null;

                string verdict;
                if (lampCount == 0) { verdict = "✘ 场景里没有灯"; missing++; }
                else if (anchor == null) { verdict = "✘ 找不到锚点"; missing++; }
                else if (vt == null) { verdict = "✘ 锚点没有 VisualTarget（点开灯不会亮）"; missing++; }
                else verdict = "✔ 正常";

                sb.AppendLine(string.Format("  {0}：灯 {1} 盏 / 锚点 {2} / VisualTarget {3} → {4}",
                    zone, lampCount,
                    anchor != null ? anchor.gameObject.name : "(无)",
                    vt != null ? vt.name : "(无)",
                    verdict));
            }

            sb.AppendLine(missing == 0
                ? "  ===== 三间房都正常，这一步不需要再做任何事 ====="
                : "  ===== 有 " + missing + " 间不正常；若灯为 0 盏，退出 Play 模式后再跑一次补灯具 =====");

            Debug.Log(sb.ToString());
        }

        private static int CountLamps(string zone)
        {
            Transform parent = FindByName(LampParentName);
            if (parent == null) return 0;
            int n = 0;
            string prefix = NamePrefix + zone + "_";
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform c = parent.GetChild(i);
                if (c != null && c.name.StartsWith(prefix, StringComparison.Ordinal)) n++;
            }
            return n;
        }

        /// <summary>真正干活。返回 false 表示**什么都没动**（场景未被修改、未被保存）。</summary>
        public static bool Run(out string message)
        {
            message = "完成。详细清单见工程根目录 add_lamps.txt，Console 也有同样的内容。";

            StringBuilder log = new StringBuilder();
            log.AppendLine("[LAMP] ===== 给三间房补天花板灯 =====");

            // ────────────────────────────────────────────────────────────────
            // 【这一道闸必须在最前面，而且必须真的 return】
            // 这个工具会 DestroyImmediate 旧灯、InstantiatePrefab 新灯、最后 SaveScene。
            // 在 Play 模式下做这三件事的后果是连锁的（2026-10-22 实测踩到）：
            //   ① 旧灯被删 → 表现层手里的 Transform 变成「已销毁」→ 同一帧抛
            //      MissingReferenceException（就是用户截图里那条）；
            //   ② 新灯是运行时对象，退出 Play 就没了；
            //   ③ 而 SaveScene 会把**播放中的场景状态**写进磁盘 —— 资产里那间房的灯真的没了。
            // 最终表现：不报错、只是「点开灯不亮」，而且很难想到是「点了个菜单」造成的。
            // ────────────────────────────────────────────────────────────────
            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                message = "不能在 Play 模式下执行。\n\n" +
                          "这个工具会删掉旧灯再放新灯，然后保存场景。播放中执行会把「播放中的场景状态」" +
                          "写进磁盘，导致资产里的灯真的丢失 —— 而且不报错，只表现为「点开灯不亮」。\n\n" +
                          "请先按停止退出 Play 模式再点。\n\n" +
                          "如果你只是想验证「灯亮不亮」：不用跑这个工具，灯早就在场景里了 ——" +
                          "按 Play 后按 G 打开总控面板，选房间点「开」即可。";
                Debug.LogWarning("[LAMP] " + message.Replace("\n", " "));
                return false;
            }

            // 【场景：只在活动场景「没有路径」时自动打开】
            // 批处理下活动场景是空的，必须打开才能干活。
            // 但用户开着别的场景时**不替他切** —— `OpenScene(Single)` 会静默丢掉未保存的修改，
            // 而这个工具本身就会存盘，两件事叠在一起等于把用户没保存的东西也一起抹掉。
            Scene scene = SceneManager.GetActiveScene();
            if (string.IsNullOrEmpty(scene.path))
            {
                scene = EditorSceneManager.OpenScene(TwinExpandPoints.ScenePath, OpenSceneMode.Single);
                log.AppendLine("[LAMP] 已打开场景：" + scene.path);
            }
            else if (scene.path != TwinExpandPoints.ScenePath)
            {
                message = "当前打开的是 " + scene.path + "，不是目标场景。\n\n" +
                          "请先打开 " + TwinExpandPoints.ScenePath + " 再点这个菜单。\n" +
                          "（这里不替你切场景：切场景会丢掉未保存的修改，而本工具紧接着还会存盘。）";
                Fail(log, message);
                return false;
            }

            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(LampPrefabPath);
            if (prefab == null)
            {
                message = "找不到灯具预制体：" + LampPrefabPath;
                Fail(log, message);
                return false;
            }

            Transform lampParent = FindByName(LampParentName);
            if (lampParent == null)
            {
                message = "找不到灯具父节点 " + LampParentName + "（场景结构变了？）";
                Fail(log, message);
                return false;
            }
            log.AppendLine("[LAMP] 灯具父节点：" + lampParent.name);

            Dictionary<string, Transform> rooms = TwinExpandPoints.FindRooms();
            log.AppendLine("[LAMP] 房间节点 " + rooms.Count + " 个");

            // 全部房间的包围盒都要有：判断「这个位置算不算本房间的」时，竞争者是全部房间
            List<Transform> roomList = new List<Transform>();
            List<Bounds> roomBounds = new List<Bounds>();
            foreach (KeyValuePair<string, Transform> kv in rooms)
            {
                Bounds b = TwinExpandPoints.RoomBounds(kv.Value);
                if (b.size.sqrMagnitude < 0.01f) continue;
                roomList.Add(kv.Value);
                roomBounds.Add(b);
            }

            int madeLamps = 0, linked = 0, failed = 0;

            for (int z = 0; z < Zones.Length; z++)
            {
                string zone = Zones[z];
                string twinId = zone + "/Light/Main_01";

                Transform room;
                if (!rooms.TryGetValue(zone, out room) || room == null)
                {
                    log.AppendLine("[LAMP] ✘ 找不到房间节点 " + zone);
                    failed++;
                    continue;
                }

                Bounds rb = TwinExpandPoints.RoomBounds(room);
                if (rb.size.sqrMagnitude < 0.01f)
                {
                    log.AppendLine("[LAMP] ✘ " + zone + " 包围盒为空，跳过");
                    failed++;
                    continue;
                }

                log.AppendLine(string.Format(
                    "[LAMP] ---- {0}：包围盒 x[{1:F2},{2:F2}] z[{3:F2},{4:F2}] 地面 y={5:F2} ----",
                    zone, rb.min.x, rb.max.x, rb.min.z, rb.max.z, rb.min.y));

                // 参考灯：同层（离「地面+3.4」不到 0.8m）且 XZ 最近的既有 LightLum01_01
                Transform reference = FindReferenceLamp(rb);
                if (reference == null)
                {
                    log.AppendLine("[LAMP] ✘ " + zone + " 同层找不到任何既有灯具做参考，跳过（不敢瞎猜高度）");
                    failed++;
                    continue;
                }
                log.AppendLine(string.Format(
                    "[LAMP] 参考灯：{0} @ ({1:F2},{2:F2},{3:F2})",
                    reference.name, reference.position.x, reference.position.y, reference.position.z));

                // 【顺序：先算位置，后删旧灯】
                // 原来是「先删旧灯 → 再算位置」。一旦算不出位置，旧灯已经没了，
                // 场景就退化成「这间房没有灯」—— 而且没有任何报错。2026-10-22 那次事故
                // 就是「删掉了但没补上」的形态。把删除挪到「确定要放」之后，这个洞就没了。
                List<Vector3> spots = ComputeSpots(zone, rb, reference.position.y, roomList, roomBounds, log);
                if (spots.Count == 0)
                {
                    log.AppendLine("[LAMP] ✘ " + zone + " 算不出可用的摆放位置（旧灯保持原样，没有删除）");
                    failed++;
                    continue;
                }

                // 清掉上一次跑出来的同名灯，保证幂等
                int removed = RemoveOldLamps(lampParent, zone);
                if (removed > 0) log.AppendLine("[LAMP] 清理旧灯 " + removed + " 盏");

                GameObject first = null;
                for (int i = 0; i < spots.Count; i++)
                {
                    GameObject lamp = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                    lamp.name = NamePrefix + zone + "_" + (i + 1);
                    lamp.transform.SetParent(lampParent, true);      // true = 保持世界坐标
                    lamp.transform.position = spots[i];
                    lamp.transform.rotation = reference.rotation;   // 朝向抄参考灯
                    lamp.transform.localScale = reference.localScale;

                    // 静态合批会绕过 MaterialPropertyBlock，状态色会静默失效（99 号清单第 7 节）
                    TwinExpandPoints.ClearBatchingStatic(lamp);

                    if (first == null) first = lamp;
                    madeLamps++;
                    log.AppendLine(string.Format("[LAMP] ✔ {0} @ ({1:F2},{2:F2},{3:F2})",
                        lamp.name, spots[i].x, spots[i].y, spots[i].z));
                }

                // 把照明点位的 VisualTarget 指到第一盏新灯上
                TwinAnchor anchor = TwinExpandPoints.FindAnchor(twinId);
                if (anchor == null)
                {
                    log.AppendLine("[LAMP] ✘ 场景里找不到锚点 " + twinId + "（新灯已放，但没人用它）");
                    failed++;
                }
                else
                {
                    SetObject(anchor, "_visualTarget", first);
                    // 路径兜底必须清空，否则 VisualTarget 会优先返回 _visualTarget（正确），
                    // 但一旦引用丢了就会去 GameObject.Find 一个不存在的路径，日志里天天警告
                    TwinExpandPoints.SetString(anchor, "_visualTargetPath", "");
                    log.AppendLine("[LAMP] ✔ " + twinId + " → VisualTarget = " + first.name +
                                   "（锚点仍在 " + anchor.gameObject.name + " 上）");
                    linked++;
                }
            }

            // 【一盏都没造出来就别存盘】
            // 存盘会把「这次跑出来的中间状态」固化下来。一盏都没造就存，等于白白
            // 重写一遍 10 万行场景文件，还把所有「已经正确的东西」暴露在一次全量重序列化里。
            if (madeLamps == 0)
            {
                log.AppendLine("[LAMP] ✘ 这次一盏都没造出来 —— **不保存场景**，磁盘上的内容没变");
                log.AppendLine("[LAMP] ===== 中止 =====");
                Debug.LogError(log.ToString());
                try { System.IO.File.WriteAllText("add_lamps.txt", log.ToString(), Encoding.UTF8); }
                catch (Exception) { }
                message = "一盏灯都没造出来，场景**没有被修改**。原因见 Console（通常是找不到房间节点或参考灯）。";
                return false;
            }

            EditorSceneManager.MarkSceneDirty(scene);
            bool saved = EditorSceneManager.SaveScene(scene);
            log.AppendLine("[LAMP] 场景保存：" + (saved ? "成功" : "失败"));
            log.AppendLine("[LAMP] 新灯 " + madeLamps + " 盏，接入表现层 " + linked + " 个点位，失败 " + failed + " 个");

            // 验收：把三间房最终的灯位和锚点状态列出来
            log.AppendLine("[LAMP] ---- 验收 ----");
            for (int z = 0; z < Zones.Length; z++)
            {
                string twinId = Zones[z] + "/Light/Main_01";
                TwinAnchor a = TwinExpandPoints.FindAnchor(twinId);
                GameObject vt = a != null ? a.VisualTarget : null;
                log.AppendLine(string.Format("[LAMP] {0}\t锚点={1}\tVisualTarget={2}\tRenderer={3}",
                    twinId,
                    a != null ? a.gameObject.name : "(无)",
                    vt != null ? vt.name : "(无)",
                    vt != null ? vt.GetComponentsInChildren<Renderer>(true).Length.ToString() : "0"));
            }

            log.AppendLine("[LAMP] ===== 完成 =====");
            Debug.Log(log.ToString());
            try { System.IO.File.WriteAllText("add_lamps.txt", log.ToString(), Encoding.UTF8); }
            catch (Exception e) { Debug.LogWarning("[LAMP] 写文件失败：" + e.Message); }

            message = saved
                ? "完成：新灯 " + madeLamps + " 盏，接入表现层 " + linked + " 个点位，失败 " + failed + " 个。\n\n" +
                  "**这一步做完就不用再点了。** 想确认效果：按 Play → 按 G 打开总控面板 → 选房间点「开」。"
                : "灯已放好，但场景保存失败（磁盘只读？）。请用 Ctrl+S 手动保存。";
            return saved;
        }

        // ================================================================ 摆放

        /// <summary>
        /// 算两盏灯的位置：沿房间长边分三等份，短边居中。
        ///
        /// 算完还要过一遍「归属检查」—— 包围盒互相重叠时，1/3 处可能已经落到隔壁房间里。
        /// 这时把候选点往本房间中心收，最多收 3 次；收不动就放弃这间房，不硬放。
        /// </summary>
        private static List<Vector3> ComputeSpots(string zone, Bounds rb, float lampY,
            List<Transform> rooms, List<Bounds> bounds, StringBuilder log)
        {
            List<Vector3> list = new List<Vector3>(LampsPerRoom);
            bool alongX = rb.size.x >= rb.size.z;

            for (int i = 0; i < LampsPerRoom; i++)
            {
                float t = (i + 1f) / (LampsPerRoom + 1f);   // 2 盏 → 1/3、2/3
                Vector3 p = alongX
                    ? new Vector3(Mathf.Lerp(rb.min.x, rb.max.x, t), lampY, rb.center.z)
                    : new Vector3(rb.center.x, lampY, Mathf.Lerp(rb.min.z, rb.max.z, t));

                Vector3 center = new Vector3(rb.center.x, lampY, rb.center.z);
                bool ok = false;
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    if (IsNearestRoom(zone, p, rooms, bounds))
                    {
                        ok = true;
                        break;
                    }
                    log.AppendLine(string.Format(
                        "[LAMP] · {0} 候选 ({1:F2},*,{2:F2}) 离隔壁更近，向房间中心收 25%", zone, p.x, p.z));
                    p = Vector3.Lerp(p, center, 0.25f);
                }

                if (!ok)
                {
                    log.AppendLine("[LAMP] ✘ " + zone + " 的第 " + (i + 1) + " 盏收不回本房间，放弃");
                    return new List<Vector3>();
                }
                list.Add(p);
            }
            return list;
        }

        /// <summary>这个位置在所有包含它的房间里，是不是离本房间中心最近。</summary>
        private static bool IsNearestRoom(string zone, Vector3 p, List<Transform> rooms, List<Bounds> bounds)
        {
            // 只比 XZ：楼层已经由「参考灯取同层」保证了，这里再掺进 y 只会让判据变模糊
            Vector3 flat = new Vector3(p.x, 0f, p.z);

            int mine = -1;
            float myDist = float.MaxValue;
            for (int i = 0; i < rooms.Count; i++)
            {
                if (rooms[i] == null || rooms[i].name != zone) continue;
                mine = i;
                myDist = (flat - new Vector3(bounds[i].center.x, 0f, bounds[i].center.z)).sqrMagnitude;
                break;
            }
            if (mine < 0) return true;

            for (int i = 0; i < rooms.Count; i++)
            {
                if (i == mine || rooms[i] == null) continue;
                Bounds b = bounds[i];
                if (p.x < b.min.x - 0.6f || p.x > b.max.x + 0.6f) continue;
                if (p.z < b.min.z - 0.6f || p.z > b.max.z + 0.6f) continue;
                // 不同层不构成竞争：靠地面高度区分
                if (Mathf.Abs(b.min.y - bounds[mine].min.y) > 1.5f) continue;

                float d = (flat - new Vector3(b.center.x, 0f, b.center.z)).sqrMagnitude;
                if (d < myDist - 0.0001f) return false;
            }
            return true;
        }

        /// <summary>
        /// 找同层最近的既有灯具当参考（高度 + 朝向）。
        /// 「同层」用「离 地面+3.4m 不到 0.8m」判定：层高 3.47，另一层的灯差 3.47m，绝不会误选。
        /// </summary>
        private static Transform FindReferenceLamp(Bounds roomBounds)
        {
            float targetY = roomBounds.min.y + 3.4f;
            Vector3 center = roomBounds.center;

            Transform best = null;
            float bestDist = float.MaxValue;
            Transform[] all = UnityEngine.Object.FindObjectsOfType<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                Transform t = all[i];
                if (t == null) continue;
                if (!string.Equals(TwinExpandPoints.PrefabOf(t.gameObject), "LightLum01_01",
                        StringComparison.Ordinal)) continue;
                if (Mathf.Abs(t.position.y - targetY) > 0.8f) continue;

                float d = (t.position - center).sqrMagnitude;
                if (d < bestDist) { bestDist = d; best = t; }
            }
            return best;
        }

        /// <summary>删掉上一次跑出来的同名灯（幂等）。</summary>
        private static int RemoveOldLamps(Transform parent, string zone)
        {
            List<GameObject> kill = new List<GameObject>();
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform c = parent.GetChild(i);
                if (c != null && c.name.StartsWith(NamePrefix + zone + "_", StringComparison.Ordinal))
                    kill.Add(c.gameObject);
            }
            for (int i = 0; i < kill.Count; i++) UnityEngine.Object.DestroyImmediate(kill[i]);
            return kill.Count;
        }

        // ================================================================ 工具

        private static Transform FindByName(string name)
        {
            Transform[] all = UnityEngine.Object.FindObjectsOfType<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null && string.Equals(all[i].name, name, StringComparison.Ordinal))
                    return all[i];
            }
            return null;
        }

        private static void SetObject(UnityEngine.Object target, string fieldName, UnityEngine.Object value)
        {
            if (target == null) return;
            SerializedObject so = new SerializedObject(target);
            SerializedProperty p = so.FindProperty(fieldName);
            if (p == null)
            {
                Debug.LogWarning("[LAMP] 字段不存在：" + target.GetType().Name + "." + fieldName);
                return;
            }
            p.objectReferenceValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(target);
        }

        private static void Fail(StringBuilder log, string msg)
        {
            log.AppendLine("[LAMP] ✘ " + msg);
            Debug.LogError("[LAMP] " + msg);
            try { System.IO.File.WriteAllText("add_lamps.txt", log.ToString(), Encoding.UTF8); }
            catch (Exception) { }
        }
    }
}

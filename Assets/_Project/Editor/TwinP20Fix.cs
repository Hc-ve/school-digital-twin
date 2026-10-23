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
    /// 一次性修正：清理**重复锚点**，并把「体育馆入口」的锚点挪到**容器根节点**上。
    ///
    /// ── 背景（2026-10-23 实测）──────────────────────────────────────
    /// 重跑 `P19 扩点位/② 执行` 之后出了两个问题：
    ///   ① 锚点挂错了物件：日志写着「SportGate/Door/Entrance_01 → **glass**」——
    ///      坐标匹配选中了 `Door02Wood1` 根节点下面那块**玻璃**（它和根节点同坐标）。
    ///      结果是"只有玻璃在转，门框不动"。
    ///   ② 重复锚点：`Corridor1/Bell/Speaker_01 × 2`、`SportRoom1/Light/Main_01 × 2`。
    ///      原因是 ResolveHosts 每次都重新挑"离参考点最近的实例"，第二次挑到了另一台，
    ///      而"是否已绑"只检查被挑中的那个宿主 —— 于是一个点位绑了两个锚点。
    ///
    /// 这个工具只做减法 + 搬家，**不新增点位、不改点位目录**：
    ///   · 同一个 TwinId 有多个锚点时，保留「期望宿主」，其余 DestroyImmediate；
    ///   · 体育馆入口的锚点如果在 `glass` 上，把它挪到 `Door02Wood1` 根节点（保留全部字段）。
    ///
    /// 只读预演 → 确认 → 执行（会存盘）。Play 模式下拒绝执行。
    /// </summary>
    public static class TwinP20Fix
    {
        private const string ScenePath = "Assets/TirGames/SchoolScene/Scenes/SchoolSceneDay.unity";

        /// <summary>期望的宿主名。表里没有的 TwinId 出现重复时，保留第一个、其余删掉。</summary>
        private static readonly Dictionary<string, string> ExpectedHost =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "Corridor1/Bell/Speaker_01",    "Alarm01 (1)" },
                { "SportRoom1/Light/Main_01",     "LightLum01_01 (27)" },
                { "SportGate/Door/Entrance_01",   "Door02Wood1" },
            };

        /// <summary>这些点位的锚点必须挂在某个「容器根」上，不能挂在网格子物体上。</summary>
        private static readonly Dictionary<string, string> RequiredRoot =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "SportGate/Door/Entrance_01", "Door02Wood1" },
            };

        [MenuItem("P20 修正/① 预演：检查重复锚点与挂错的锚点（只读）", false, 10)]
        public static void PlanMenu()
        {
            string text = Plan();
            Debug.Log(text);
            EditorUtility.DisplayDialog("P20 预演完成",
                "结果已打印到 Console，并写入工程根目录 p20_fix_plan.txt。", "好");
        }

        [MenuItem("P20 修正/② 执行：清理重复锚点 + 挪正体育馆入口锚点（会存盘）", false, 11)]
        public static void ApplyMenu()
        {
            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorUtility.DisplayDialog("已拒绝",
                    "现在是 Play 模式。请先按停止（Play 按钮再点一次）再执行。", "好");
                return;
            }
            string text = Apply();
            Debug.Log(text);
            EditorUtility.DisplayDialog("P20 执行完成",
                "结果已打印到 Console，并写入 p20_fix_apply.txt。", "好");
        }

        // ================================================================ 预演

        public static string Plan()
        {
            Scene scene = EnsureScene();
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[P20Fix] ===== 预演（只读）=====");
            sb.AppendLine("[P20Fix] 场景：" + scene.path);

            List<TwinAnchor> all = LoadAnchors();
            sb.AppendLine("[P20Fix] 锚点总数：" + all.Count);

            Dictionary<string, List<TwinAnchor>> byId = Group(all);
            sb.AppendLine();
            sb.AppendLine("[P20Fix] ---- 重复的 TwinId ----");
            int dup = 0;
            foreach (KeyValuePair<string, List<TwinAnchor>> kv in byId)
            {
                if (kv.Value.Count < 2) continue;
                dup++;
                sb.AppendLine("[P20Fix] ⚠ " + kv.Key + " × " + kv.Value.Count + "：");
                for (int i = 0; i < kv.Value.Count; i++)
                    sb.AppendLine("[P20Fix]      " + (i == 0 ? "保留?" : "删除?") + " " +
                                  Path(kv.Value[i]));
                string expect;
                if (ExpectedHost.TryGetValue(kv.Key, out expect))
                    sb.AppendLine("[P20Fix]      期望宿主：" + expect);
                else
                    sb.AppendLine("[P20Fix]      期望宿主：(表里没有 → 保留第一个)");
            }
            if (dup == 0) sb.AppendLine("[P20Fix] （没有重复）");

            sb.AppendLine();
            sb.AppendLine("[P20Fix] ---- 必须挂在容器根上的点位 ----");
            foreach (KeyValuePair<string, string> kv in RequiredRoot)
            {
                List<TwinAnchor> list;
                if (!byId.TryGetValue(kv.Key, out list) || list.Count == 0)
                {
                    sb.AppendLine("[P20Fix] ✘ " + kv.Key + " 没有锚点");
                    continue;
                }
                TwinAnchor a = list[0];
                bool ok = string.Equals(a.gameObject.name, kv.Value, StringComparison.Ordinal);
                sb.AppendLine("[P20Fix] " + (ok ? "✔ " : "⚠ ") + kv.Key + " 当前挂载=" +
                              a.gameObject.name + "  要求=" + kv.Value +
                              (ok ? "" : "  → 要挪到 " + kv.Value));
            }

            sb.AppendLine();
            sb.AppendLine("[P20Fix] ===== 结束 =====");
            Write("p20_fix_plan.txt", sb.ToString());
            return sb.ToString();
        }

        // ================================================================ 执行

        public static string Apply()
        {
            Scene scene = EnsureScene();
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[P20Fix] ===== 执行 =====");

            List<TwinAnchor> all = LoadAnchors();
            Dictionary<string, List<TwinAnchor>> byId = Group(all);

            int removed = 0, moved = 0;

            // ---- ① 重复锚点：留期望的那个 ----
            foreach (KeyValuePair<string, List<TwinAnchor>> kv in byId)
            {
                if (kv.Value.Count < 2) continue;

                string expect;
                ExpectedHost.TryGetValue(kv.Key, out expect);

                TwinAnchor keep = kv.Value[0];
                if (!string.IsNullOrEmpty(expect))
                {
                    for (int i = 0; i < kv.Value.Count; i++)
                    {
                        if (string.Equals(kv.Value[i].gameObject.name, expect, StringComparison.Ordinal))
                        {
                            keep = kv.Value[i];
                            break;
                        }
                    }
                }

                for (int i = 0; i < kv.Value.Count; i++)
                {
                    TwinAnchor a = kv.Value[i];
                    if (a == keep) continue;
                    sb.AppendLine("[P20Fix] 删除重复：" + Path(a) + "（保留 " + Path(keep) + "）");
                    UnityEngine.Object.DestroyImmediate(a, true);
                    removed++;
                }
            }

            // ---- ② 该挂容器根的，挪过去 ----
            foreach (KeyValuePair<string, string> kv in RequiredRoot)
            {
                List<TwinAnchor> list;
                if (!byId.TryGetValue(kv.Key, out list) || list.Count == 0) continue;

                TwinAnchor a = list[0];
                if (a == null) continue;
                if (string.Equals(a.gameObject.name, kv.Value, StringComparison.Ordinal)) continue;

                Transform t = a.transform;
                while (t != null && !string.Equals(t.name, kv.Value, StringComparison.Ordinal)) t = t.parent;
                if (t == null)
                {
                    sb.AppendLine("[P20Fix] ⚠ " + kv.Key + "：往上找不到「" + kv.Value + "」，没动");
                    continue;
                }

                GameObject host = t.gameObject;
                if (host.GetComponent<TwinAnchor>() != null)
                {
                    // 根上已经有一个了 → 删掉挂错的那个
                    sb.AppendLine("[P20Fix] 根上已有锚点，删掉挂错的：" + Path(a));
                    UnityEngine.Object.DestroyImmediate(a, true);
                    removed++;
                    continue;
                }

                // 把整个组件复制到根上（字段原样带过去），再删掉旧的
                UnityEditorInternal.ComponentUtility.CopyComponent(a);
                UnityEditorInternal.ComponentUtility.PasteComponentAsNew(host);
                sb.AppendLine("[P20Fix] 锚点搬家：" + Path(a) + "  →  " + host.name + "  [" + kv.Key + "]");
                UnityEngine.Object.DestroyImmediate(a, true);
                moved++;
            }

            sb.AppendLine();
            sb.AppendLine("[P20Fix] 删除重复 " + removed + " 个，搬家 " + moved + " 个");

            // ---- 验收 + 落盘 ----
            List<TwinAnchor> after = LoadAnchors();
            Dictionary<string, List<TwinAnchor>> afterById = Group(after);
            int stillDup = 0;
            foreach (KeyValuePair<string, List<TwinAnchor>> kv in afterById)
                if (kv.Value.Count > 1) stillDup++;
            sb.AppendLine("[P20Fix] 验收：锚点 " + after.Count + " 个，仍然重复的 TwinId " + stillDup + " 个");
            if (stillDup == 0)
            {
                EditorSceneManager.MarkSceneDirty(scene);
                bool saved = EditorSceneManager.SaveScene(scene);
                sb.AppendLine("[P20Fix] 场景保存：" + (saved ? "成功" : "失败"));
            }
            else
            {
                sb.AppendLine("[P20Fix] ⚠ 还有重复，没存盘 —— 把这份输出发我。");
            }

            sb.AppendLine("[P20Fix] ===== 结束 =====");
            Write("p20_fix_apply.txt", sb.ToString());
            return sb.ToString();
        }

        // ================================================================ 工具

        private static Scene EnsureScene()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (string.IsNullOrEmpty(scene.path) || scene.path != ScenePath)
                scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            return scene;
        }

        private static List<TwinAnchor> LoadAnchors()
        {
            TwinAnchor[] all = UnityEngine.Object.FindObjectsOfType<TwinAnchor>(true);
            List<TwinAnchor> list = new List<TwinAnchor>(all.Length);
            for (int i = 0; i < all.Length; i++)
            {
                TwinAnchor a = all[i];
                if (a == null) continue;
                if (string.IsNullOrEmpty(a.TwinId)) continue;
                list.Add(a);
            }
            return list;
        }

        private static Dictionary<string, List<TwinAnchor>> Group(List<TwinAnchor> list)
        {
            Dictionary<string, List<TwinAnchor>> byId =
                new Dictionary<string, List<TwinAnchor>>(StringComparer.Ordinal);
            for (int i = 0; i < list.Count; i++)
            {
                TwinAnchor a = list[i];
                List<TwinAnchor> bucket;
                if (!byId.TryGetValue(a.TwinId, out bucket))
                {
                    bucket = new List<TwinAnchor>(2);
                    byId[a.TwinId] = bucket;
                }
                bucket.Add(a);
            }
            return byId;
        }

        private static string Path(TwinAnchor a)
        {
            if (a == null) return "(null)";
            return a.gameObject.name + "  [" + a.TwinId + "]";
        }

        private static void Write(string name, string text)
        {
            try { System.IO.File.WriteAllText(name, text, Encoding.UTF8); }
            catch (Exception e) { Debug.LogWarning("[P20Fix] 写文件失败：" + e.Message); }
        }
    }
}

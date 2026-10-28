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
    /// 【P24】给「有灯具模型、但没有灯锚点」的房间补锚点。
    ///
    /// ── 为什么要这个工具 ────────────────────────────────────────────
    /// 2026-10-26 扫了一遍场景里的灯具实例（`LightLum01_01` / `LightLum01_03` / `Light_spot`），
    /// 按房间分组后发现 4 间房有灯但**一个锚点都没有**：
    ///     ActZal（礼堂）      12 盏
    ///     SportHall（篮球场）  12 盏 `LightLum01_03`
    ///     SportRoom2           4 盏
    ///     SportRoom3           4 盏
    /// 没有锚点 = 没有数据链路 = 三维里点上去没有任何反应，面板里也看不到。
    ///
    /// ── 锚点挂在哪 ──────────────────────────────────────────────────
    /// 直接挂在**房间里离中心最近的那盏灯**上（和 12 扇 `Door07a` 一样，锚点贴在真实物体上，
    /// 这样三维里点得到）。不需要 `_visualTarget`：
    /// `TwinDevicePresenter.BuildLamp` 会用它自己的 Renderer 当"代表灯"，
    /// 再由 `CollectLampGroup` 按 `anchor.Zone` + 房间范围把**整间房**的灯都收进来。
    ///
    /// ── 用法 ────────────────────────────────────────────────────────
    /// 菜单 `P24 灯锚点 → ① 给缺失房间补灯锚点`（幂等，已有同 TwinId 锚点的房间会跳过）
    /// 然后**手动**把 4 个 TwinId 加进 `TwinDeviceModel.cs` 的 `TwinControlScope.Ids`，
    /// 最后跑 `P23 交互 → ①` 生成灯开关方块。
    /// </summary>
    public static class TwinP24LampAnchors
    {
        private const string ScenePath = "Assets/TirGames/SchoolScene/Scenes/SchoolSceneDay.unity";

        /// <summary>2026-10-26 扫描确认：有灯具、但没有灯锚点的房间。</summary>
        private static readonly string[] TargetZones =
        {
            "ActZal", "SportHall", "SportRoom2", "SportRoom3",
        };

        private static readonly Dictionary<string, string> DisplayNames =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "ActZal",      "礼堂 主照明" },
                { "SportHall",   "体育馆 主照明" },
                { "SportRoom2",  "体育器材室2 主照明" },
                { "SportRoom3",  "体育器材室3 主照明" },
            };

        [MenuItem("归档/P24 灯锚点/① 给缺失房间补灯锚点", false, 10)]
        public static void RunMenu()
        {
            if (EditorApplication.isPlaying) { Refuse(); return; }
            string report = Run();
            Debug.Log(report);
            EditorUtility.DisplayDialog("P24 灯锚点", report, "好");
        }

        public static string Run()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[P24] ===== 补灯锚点 =====");

            Scene scene = SceneManager.GetActiveScene();
            if (string.IsNullOrEmpty(scene.path))
            {
                scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
                sb.AppendLine("[P24] 当前没有打开场景，已自动打开 " + ScenePath);
            }

            // ---- 已有锚点（幂等依据）----
            HashSet<string> exist = new HashSet<string>(StringComparer.Ordinal);
            TwinAnchor[] all = UnityEngine.Object.FindObjectsOfType<TwinAnchor>(true);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null && !string.IsNullOrEmpty(all[i].TwinId)) exist.Add(all[i].TwinId);
            }
            sb.AppendLine("[P24] 场景里已有锚点 " + exist.Count + " 个");

            // ---- 所有灯具（预制体实例名里带 LightLum / Light_spot）----
            List<Renderer> lamps = new List<Renderer>(128);
            MeshRenderer[] mrs = UnityEngine.Object.FindObjectsOfType<MeshRenderer>(true);
            for (int i = 0; i < mrs.Length; i++)
            {
                MeshRenderer r = mrs[i];
                if (r == null) continue;
                string n = r.gameObject.name;
                if (n.IndexOf("LightLum", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("Light_spot", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    lamps.Add(r);
                }
            }
            sb.AppendLine("[P24] 场景里找到灯具 " + lamps.Count + " 个");
            sb.AppendLine();

            int made = 0, skipped = 0, noLamp = 0, noBounds = 0;

            for (int z = 0; z < TargetZones.Length; z++)
            {
                string zone = TargetZones[z];
                string twinId = zone + "/Light/Main_01";

                if (exist.Contains(twinId))
                {
                    sb.AppendLine("[P24] · " + zone + "：已有 " + twinId + "，跳过");
                    skipped++;
                    continue;
                }

                float x0, x1, z0, z1;
                if (!TwinRoomBounds.TryGetBounds(zone, out x0, out x1, out z0, out z1))
                {
                    sb.AppendLine("[P24] ✘ " + zone + "：房间范围没登记，跳过");
                    noBounds++;
                    continue;
                }

                List<Renderer> inRoom = new List<Renderer>(16);
                for (int i = 0; i < lamps.Count; i++)
                {
                    Vector3 p = lamps[i].transform.position;
                    if (p.x >= x0 && p.x <= x1 && p.z >= z0 && p.z <= z1) inRoom.Add(lamps[i]);
                }

                if (inRoom.Count == 0)
                {
                    sb.AppendLine("[P24] ✘ " + zone + "：房间里找不到灯具，跳过");
                    noLamp++;
                    continue;
                }

                // 取离房间中心最近的那盏当宿主
                Vector3 center = new Vector3((x0 + x1) * 0.5f, inRoom[0].transform.position.y,
                                             (z0 + z1) * 0.5f);
                Renderer best = inRoom[0];
                float bestD = float.MaxValue;
                for (int i = 0; i < inRoom.Count; i++)
                {
                    float d = (inRoom[i].transform.position - center).sqrMagnitude;
                    if (d < bestD) { bestD = d; best = inRoom[i]; }
                }

                GameObject host = best.gameObject;
                TwinAnchor anchor = Undo.AddComponent<TwinAnchor>(host);
                if (anchor == null)
                {
                    sb.AppendLine("[P24] ✘ " + zone + "：加 TwinAnchor 失败");
                    continue;
                }

                string display;
                if (!DisplayNames.TryGetValue(zone, out display) || string.IsNullOrEmpty(display))
                    display = zone + " 主照明";

                SerializedObject so = new SerializedObject(anchor);
                so.FindProperty("_twinId").stringValue = twinId;
                so.FindProperty("_displayName").stringValue = display;
                so.FindProperty("_zone").stringValue = zone;
                so.FindProperty("_category").stringValue = "Light";
                so.FindProperty("_visualTarget").objectReferenceValue = null;   // 用自身 Renderer
                so.FindProperty("_visualTargetPath").stringValue = "";
                so.ApplyModifiedPropertiesWithoutUndo();

                exist.Add(twinId);
                made++;

                sb.AppendLine("[P24] ✔ " + twinId + "  挂在「" + host.name + "」（" + inRoom.Count +
                              " 盏灯）位置 " + Fmt(host.transform.position));
            }

            sb.AppendLine();
            sb.AppendLine("[P24] 新建 " + made + " 个，跳过 " + skipped +
                          "，房间没灯 " + noLamp + "，范围没登记 " + noBounds);
            sb.AppendLine("[P24] ===== 结束 =====");

            if (made > 0)
            {
                EditorSceneManager.MarkSceneDirty(scene);
                bool ok = EditorSceneManager.SaveScene(scene);
                sb.AppendLine("[P24] 场景保存：" + (ok ? "成功" : "失败"));
            }

            sb.AppendLine();
            sb.AppendLine("[P24] 下一步：");
            sb.AppendLine("  1. 把上面新建的 TwinId 加进 TwinDeviceModel.cs 的 TwinControlScope.Ids");
            sb.AppendLine("  2. 跑菜单 P23 交互 → ① 生成灯开关方块");
            sb.AppendLine("  3. Play 验证：靠近开关方块能开关整间房的灯");

            try { System.IO.File.WriteAllText("p24_lamps.txt", sb.ToString(), Encoding.UTF8); }
            catch (Exception) { }

            return sb.ToString();
        }

        private static string Fmt(Vector3 v)
        {
            return "(" + v.x.ToString("0.00") + ", " + v.y.ToString("0.00") + ", " +
                   v.z.ToString("0.00") + ")";
        }

        private static void Refuse()
        {
            EditorUtility.DisplayDialog("已拒绝",
                "现在是 Play 模式。请先按停止 —— Play 下加组件和存场景都会出问题。", "好");
        }
    }
}

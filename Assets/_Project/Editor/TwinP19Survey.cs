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
    /// P19 只读勘察：把场景里的**全部预制体实例**按名字分组列出来，并给出每个房间的实测包围盒。
    ///
    /// ── 为什么需要它 ──────────────────────────────────────────────
    /// 用户这一批提了 5 类新设备（电脑开关机 / 投影仪 / 厨房 / 大教室·体育器材室·餐厅的门灯 / 上课铃）。
    /// 光看截图猜模型名是最容易出错的一步：`LightLum01_01` 不叫 Lamp、`Split01` 是分体空调、
    /// 饮水机藏在嵌套预制体里 —— 名字和业务概念没有对应关系。
    ///
    /// 所以先把「场景里到底有哪些模型、各几个、在哪间房」列清楚，再谈建什么点位。
    /// 这份输出是**事实**，后面所有设计都以它为依据。
    ///
    /// 只读：不改场景、不存盘。
    /// `-executeMethod SchoolTwin.EditorTools.TwinP19Survey.Run`
    /// </summary>
    public static class TwinP19Survey
    {
        private const string ScenePath = "Assets/TirGames/SchoolScene/Scenes/SchoolSceneDay.unity";

        [MenuItem("P19 勘察/导出设备清单（只读）", false, 10)]
        public static void RunMenu()
        {
            string report = Run();
            Debug.Log(report);
            EditorUtility.DisplayDialog("P19 勘察完成", "结果已写入工程根目录 p19_survey.txt，并打印到 Console。", "好");
        }

        public static string Run()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (string.IsNullOrEmpty(scene.path) || scene.path != ScenePath)
                scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            StringBuilder log = new StringBuilder();
            log.AppendLine("[P19] ===== 场景设备勘察（只读）=====");

            // ---------- 1. 房间包围盒 ----------
            Dictionary<string, Transform> rooms = TwinExpandPoints.FindRooms();
            List<RoomInfo> allRooms = new List<RoomInfo>();
            log.AppendLine("[P19] ---- 房间（" + rooms.Count + " 个）----");
            List<string> roomNames = new List<string>(rooms.Keys);
            roomNames.Sort(StringComparer.Ordinal);
            for (int i = 0; i < roomNames.Count; i++)
            {
                Transform t = rooms[roomNames[i]];
                Bounds b = TwinExpandPoints.RoomBounds(t);
                RoomInfo ri = new RoomInfo();
                ri.Zone = roomNames[i];
                ri.T = t;
                ri.B = b;
                allRooms.Add(ri);

                log.AppendLine(string.Format(
                    "[P19] ROOM\t{0,-16} x[{1,7:F2},{2,7:F2}] z[{3,6:F2},{4,6:F2}] 地面y={5,6:F2}",
                    ri.Zone, b.min.x, b.max.x, b.min.z, b.max.z, b.min.y));
            }

            // ---------- 2. 全部预制体实例，按名字分组 ----------
            Transform[] all = UnityEngine.Object.FindObjectsOfType<Transform>(true);
            Dictionary<string, List<Transform>> byPrefab =
                new Dictionary<string, List<Transform>>(StringComparer.Ordinal);

            for (int i = 0; i < all.Length; i++)
            {
                Transform t = all[i];
                if (t == null) continue;
                string pf = TwinExpandPoints.PrefabOf(t.gameObject);
                if (string.IsNullOrEmpty(pf) || pf.StartsWith("(")) continue;
                // 同一预制体的嵌套子物体只算根：根 = 它的父节点不是同一个预制体
                if (t.parent != null)
                {
                    string ppf = TwinExpandPoints.PrefabOf(t.parent.gameObject);
                    if (string.Equals(ppf, pf, StringComparison.Ordinal)) continue;
                }
                if (!byPrefab.ContainsKey(pf)) byPrefab[pf] = new List<Transform>(8);
                byPrefab[pf].Add(t);
            }

            List<string> keys = new List<string>(byPrefab.Keys);
            keys.Sort(StringComparer.Ordinal);

            log.AppendLine("[P19] ---- 预制体实例（" + keys.Count + " 种）----");
            log.AppendLine("[P19] 格式：预制体名 | 数量 | 所在房间分布 | 首个实例坐标");
            for (int i = 0; i < keys.Count; i++)
            {
                List<Transform> list = byPrefab[keys[i]];
                Dictionary<string, int> zones = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int k = 0; k < list.Count; k++)
                {
                    string z = ZoneOf(list[k].position, allRooms);
                    if (z == null) z = "(房外)";
                    if (zones.ContainsKey(z)) zones[z]++;
                    else zones[z] = 1;
                }

                StringBuilder zb = new StringBuilder();
                List<string> zs = new List<string>(zones.Keys);
                zs.Sort(StringComparer.Ordinal);
                for (int k = 0; k < zs.Count; k++)
                {
                    if (k > 0) zb.Append(" / ");
                    zb.Append(zs[k]).Append("×").Append(zones[zs[k]]);
                }

                Vector3 p0 = list[0].position;
                log.AppendLine(string.Format("[P19] PREFAB\t{0,-26}\t{1,4}\t{2}\t({3:F1},{4:F1},{5:F1})",
                    keys[i], list.Count, zb.ToString(), p0.x, p0.y, p0.z));
            }

            // ---------- 3. 每间房的灯具数量（补灯要用）----------
            log.AppendLine("[P19] ---- 每间房的天花板灯具（LightLum*）----");
            for (int i = 0; i < allRooms.Count; i++)
            {
                RoomInfo ri = allRooms[i];
                int n = 0;
                Transform[] inside = ri.T.GetComponentsInChildren<Transform>(true);
                for (int k = 0; k < inside.Length; k++)
                {
                    if (inside[k] == null) continue;
                    string pf = TwinExpandPoints.PrefabOf(inside[k].gameObject);
                    if (pf.StartsWith("LightLum", StringComparison.OrdinalIgnoreCase)) n++;
                }
                log.AppendLine(string.Format("[P19] LIGHTINROOM\t{0,-16}\t{1}", ri.Zone, n));
            }

            // ---------- 4. 全局集合里的灯具按房间归属（表现层实际用的是这条）----------
            log.AppendLine("[P19] ---- 全局集合灯具按坐标归属 ----");
            Dictionary<string, int> globalLight = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < all.Length; i++)
            {
                Transform t = all[i];
                if (t == null) continue;
                string pf = TwinExpandPoints.PrefabOf(t.gameObject);
                if (!pf.StartsWith("LightLum", StringComparison.OrdinalIgnoreCase)) continue;
                string z = ZoneOf(t.position, allRooms);
                if (z == null) z = "(房外)";
                if (globalLight.ContainsKey(z)) globalLight[z]++;
                else globalLight[z] = 1;
            }
            List<string> gl = new List<string>(globalLight.Keys);
            gl.Sort(StringComparer.Ordinal);
            for (int i = 0; i < gl.Count; i++)
                log.AppendLine("[P19] GLOBALLIGHT\t" + gl[i] + "\t" + globalLight[gl[i]]);

            log.AppendLine("[P19] ===== 结束 =====");

            string text = log.ToString();
            Debug.Log(text);
            try { System.IO.File.WriteAllText("p19_survey.txt", text, Encoding.UTF8); }
            catch (Exception e) { Debug.LogWarning("[P19] 写文件失败：" + e.Message); }

            return text;
        }

        private sealed class RoomInfo
        {
            public string Zone;
            public Transform T;
            public Bounds B;
        }

        /// <summary>点落在哪个房间：Y 窗口 + 水平范围 + 中心最近（与 TwinExpandPoints 同口径）。</summary>
        private static string ZoneOf(Vector3 p, List<RoomInfo> rooms)
        {
            string best = null;
            float bestDist = float.MaxValue;
            for (int i = 0; i < rooms.Count; i++)
            {
                RoomInfo r = rooms[i];
                if (r.B.size.sqrMagnitude < 0.01f) continue;
                if (p.y < r.B.min.y + 0.2f || p.y > r.B.min.y + 4.2f) continue;
                if (p.x < r.B.min.x - 0.8f || p.x > r.B.max.x + 0.8f) continue;
                if (p.z < r.B.min.z - 0.8f || p.z > r.B.max.z + 0.8f) continue;
                float d = (p - r.B.center).sqrMagnitude;
                if (d < bestDist) { bestDist = d; best = r.Zone; }
            }
            return best;
        }
    }
}

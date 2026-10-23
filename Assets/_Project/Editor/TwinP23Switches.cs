using System;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using SchoolTwin.Twin;

namespace SchoolTwin.EditorTools
{
    /// <summary>
    /// 生成「灯开关小方块」：贴在教室门旁边的墙上，走近它弹出「开灯/关灯」按钮。
    ///
    /// 【为什么要这个】教室里没有开关的实体模型（灯的锚点在天花板的灯具上，
    /// 玩家走不过去）。用一个小方块占位，先把「走近→弹按钮→点一下→灯亮/灭」
    /// 这条交互链路跑通；以后有真模型了，把小方块替换掉即可（TwinInteractPoint 会留下）。
    ///
    /// 【几何怎么摆】贴着门轴那一侧、沿墙偏移 0.55m，高度离地 1.15m，
    /// 并向房间内部偏 3cm 避免嵌进墙体。门轴取自门扇的 TwinAnchor 位置。
    /// </summary>
    public static class TwinP23Switches
    {
        private const string ScenePath = "Assets/TirGames/SchoolScene/Scenes/SchoolSceneDay.unity";

        /// <summary>开关沿墙方向离门轴的偏移（米）</summary>
        private const float AlongWall = 0.55f;

        /// <summary>开关离地高度（米）。国标是 1.3m，稍微低一点更好按</summary>
        private const float Height = 1.15f;

        /// <summary>向房间内部的偏置（米），免得嵌进墙体</summary>
        private const float IntoRoom = 0.03f;

        [MenuItem("P23 交互/① 生成灯开关方块（会存盘）", false, 10)]
        public static void GenerateMenu()
        {
            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorUtility.DisplayDialog("已拒绝",
                    "现在是 Play 模式。请先按停止再执行。", "好");
                return;
            }
            string report = Generate();
            Debug.Log(report);
            EditorUtility.DisplayDialog("P23 生成灯开关", report, "好");
        }

        public static string Generate()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (string.IsNullOrEmpty(scene.path) || scene.path != ScenePath)
                scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[P23] ===== 生成灯开关方块 =====");

            TwinAnchor[] all = FindObjectsOfType<TwinAnchor>(true);
            int made = 0, skipped = 0;

            for (int i = 0; i < all.Length; i++)
            {
                TwinAnchor light = all[i];
                if (light == null || string.IsNullOrEmpty(light.TwinId)) continue;
                if (!string.Equals(light.Category, "Light", StringComparison.OrdinalIgnoreCase)) continue;
                if (light.Zone == null || !light.Zone.StartsWith("Class", StringComparison.Ordinal)) continue;

                // 同一房间的门
                TwinAnchor door = FindDoor(all, light.Zone);
                if (door == null)
                {
                    sb.AppendLine("[P23] ⚠ " + light.Zone + " 找不到门锚点，跳过");
                    skipped++;
                    continue;
                }

                // 已经生成过就跳过（幂等）
                string goName = "Switch_" + light.Zone;
                if (FindTransform(door.transform.parent, goName) != null)
                {
                    skipped++;
                    continue;
                }

                // 房间中心 → 决定「往房间里贴」的方向
                Vector3 normal = Vector3.zero;
                float x0, x1, z0, z1;
                if (TwinRoomBounds.TryGetBounds(light.Zone, out x0, out x1, out z0, out z1))
                {
                    normal = new Vector3((x0 + x1) * 0.5f - door.transform.position.x,
                                         0f,
                                         (z0 + z1) * 0.5f - door.transform.position.z);
                }
                if (normal.sqrMagnitude < 0.0001f) normal = -door.transform.right;
                normal.y = 0f;
                normal.Normalize();

                Vector3 panel = door.transform.right;
                panel.y = 0f;
                if (panel.sqrMagnitude < 0.0001f) panel = Vector3.right;
                panel.Normalize();

                Vector3 pos = door.transform.position
                              + panel * AlongWall
                              + normal * IntoRoom;
                pos.y = door.transform.position.y - 0.035f + Height;

                // 父节点 = 门的父（房间节点），层级上跟着房间走
                Transform parent = door.transform.parent != null ? door.transform.parent : door.transform;

                GameObject go = new GameObject(goName, typeof(RectTransform));
                Undo.RegisterCreatedObjectUndo(go, "Create Light Switch");
                go.transform.SetParent(parent, false);
                go.transform.position = pos;
                go.transform.rotation = Quaternion.LookRotation(-normal, Vector3.up);

                BoxCollider col = go.AddComponent<BoxCollider>();
                col.size = new Vector3(0.12f, 0.20f, 0.04f);

                SchoolTwin.Interaction.TwinInteractPoint point = go.AddComponent<SchoolTwin.Interaction.TwinInteractPoint>();
                point.TargetTwinId = light.TwinId;
                point.Range = 2.2f;

                sb.AppendLine("[P23] ✔ " + light.Zone + "  开关 " + light.TwinId +
                              "  位置=" + V3(pos) + "  贴在「" + door.name + "」旁边");
                made++;
            }

            sb.AppendLine();
            sb.AppendLine("[P23] 生成 " + made + " 个，跳过（已有/缺门）" + skipped + " 个");

            if (made > 0)
            {
                EditorSceneManager.MarkSceneDirty(scene);
                bool saved = EditorSceneManager.SaveScene(scene);
                sb.AppendLine("[P23] 场景保存：" + (saved ? "成功" : "失败"));
            }

            sb.AppendLine("[P23] ===== 结束 =====");
            try { System.IO.File.WriteAllText("p23_switches.txt", sb.ToString(), Encoding.UTF8); } catch (Exception) { }
            return sb.ToString();
        }

        private static TwinAnchor FindDoor(TwinAnchor[] all, string zone)
        {
            for (int i = 0; i < all.Length; i++)
            {
                TwinAnchor a = all[i];
                if (a == null || string.IsNullOrEmpty(a.TwinId)) continue;
                if (!string.Equals(a.Zone, zone, StringComparison.Ordinal)) continue;
                if (!string.Equals(a.Category, "Door", StringComparison.OrdinalIgnoreCase)) continue;
                return a;
            }
            return null;
        }

        private static Transform FindTransform(Transform parent, string name)
        {
            if (parent == null) return null;
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform c = parent.GetChild(i);
                if (c != null && string.Equals(c.name, name, StringComparison.Ordinal)) return c;
            }
            return null;
        }

        private static string V3(Vector3 v)
        {
            return "(" + v.x.ToString("0.00") + "," + v.y.ToString("0.00") + "," + v.z.ToString("0.00") + ")";
        }
    }
}

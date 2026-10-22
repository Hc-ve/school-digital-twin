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
    /// P19 只读诊断：把每一扇门的**根节点 + 每片门叶**的真实世界变换打出来。
    ///
    /// 为什么要单独一个诊断：`DoorDouble02`（礼堂双开门）和 12 扇 `Door07a` 单开门**不是一回事**。
    ///   · `Door07a`：整个物体就是一片门扇，枢轴在门轴上 → 绕根节点转就对了。
    ///   · `DoorDouble02`：根节点只是容器，真正能动的是下面两片**各自带轴**的门叶，
    ///     而且模型出厂姿态**不是「两片都关」** —— 一片关、一片开（用户 2026-10-22 实测确认：
    ///     左边那扇是关的、右边那扇是开的）。
    /// 于是「根节点转多少度 = 关门」这个假设在这扇门上直接失效：两片一起转，
    /// 结果就是一片转进门洞、另一片转出去，状态还写着"关闭"。
    ///
    /// 这个诊断回答三件事：**哪片是开的**、**每片的门扇朝向**、**转多少度能贴平墙面**。
    ///
    /// 用法有两种，都只读、都不存盘：
    ///   · 菜单：`P19 勘察/门叶诊断（只读，Play 模式下也能点）`
    ///   · 批处理（Unity 关着时）：
    ///     Unity.exe -batchmode -quit -nographics -projectPath &lt;工程&gt; \
    ///       -executeMethod SchoolTwin.EditorTools.TwinP19DoorDiag.Run -logFile &lt;log&gt;
    /// </summary>
    public static class TwinP19DoorDiag
    {
        private const string ScenePath =
            "Assets/TirGames/SchoolScene/Scenes/SchoolSceneDay.unity";

        [MenuItem("P19 勘察/门叶诊断（只读，Play 模式下也能点）", false, 20)]
        public static void RunMenu()
        {
            string text = Dump();
            Debug.Log(text);
            EditorUtility.DisplayDialog("P19 门叶诊断完成",
                "结果已打印到 Console，并写入工程根目录 p19_door.txt。", "好");
        }

        public static void Run()
        {
            Debug.Log(Dump());
        }

        public static string Dump()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (string.IsNullOrEmpty(scene.path) || scene.path != ScenePath)
                scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[P19Door] ===== 门叶变换诊断（只读）=====");
            sb.AppendLine("[P19Door] 场景：" + scene.path +
                          (Application.isPlaying ? "（Play 模式）" : "（编辑模式）"));

            TwinAnchor[] all = UnityEngine.Object.FindObjectsOfType<TwinAnchor>(true);
            List<TwinAnchor> doors = new List<TwinAnchor>(24);
            for (int i = 0; i < all.Length; i++)
            {
                TwinAnchor a = all[i];
                if (a == null) continue;
                if (!string.Equals(a.Category, "Door", StringComparison.Ordinal)) continue;
                doors.Add(a);
            }
            sb.AppendLine("[P19Door] 门锚点 " + doors.Count + " 个");

            int multi = 0;
            for (int i = 0; i < doors.Count; i++)
            {
                TwinAnchor a = doors[i];
                Transform root = a.transform;

                List<Transform> leaves = new List<Transform>(4);
                for (int c = 0; c < root.childCount; c++)
                {
                    Transform t = root.GetChild(c);
                    if (t == null) continue;
                    if (t.GetComponentInChildren<Renderer>(true) == null) continue;
                    leaves.Add(t);
                }

                if (leaves.Count < 2) continue;   // 单门叶不在本次诊断范围
                multi++;

                sb.AppendLine();
                sb.AppendLine("========== " + a.TwinId + "（zone=" + a.Zone + "，根=" + root.name +
                              "，门叶 " + leaves.Count + " 片）==========");
                sb.AppendLine("  根  世界位置=" + V3(root.position) + "  euler=" + V3(root.eulerAngles));
                sb.AppendLine("       right=" + V2(root.right) + "  forward=" + V2(root.forward));

                // 双开门：直接用两根门轴的连线当「门洞方向」—— 这是模型自己的事实，和房间表无关
                for (int k = 0; k < leaves.Count; k++)
                {
                    Transform t = leaves[k];
                    Renderer r = t.GetComponentInChildren<Renderer>(true);
                    Bounds b = r != null ? r.bounds : new Bounds(t.position, Vector3.zero);

                    BoxCollider bc = t.GetComponent<BoxCollider>();
                    if (bc == null) bc = t.GetComponentInChildren<BoxCollider>(true);

                    sb.AppendLine("  ---- 门叶 " + k + "：" + t.name + " ----");
                    sb.AppendLine("      本地位置=" + V3(t.localPosition) + "  世界位置=" + V3(t.position));
                    sb.AppendLine("      euler=" + V3(t.eulerAngles) + "  right=" + V2(t.right) + "  forward=" + V2(t.forward));
                    sb.AppendLine("      渲染器包围盒 center=" + V3(b.center) + "  size=" + V3(b.size));
                    if (bc != null)
                        sb.AppendLine("      BoxCollider size=" + V3(bc.size) + "  center=" + V3(bc.center));
                    else
                        sb.AppendLine("      BoxCollider：无");
                }

                if (leaves.Count == 2)
                {
                    Vector3 h0 = leaves[0].position, h1 = leaves[1].position;
                    Vector3 d = h1 - h0; d.y = 0f;
                    sb.AppendLine("  → 两片门轴的连线=" + V2(d.normalized) +
                                  "  轴距=" + d.magnitude.ToString("0.000") + "m");

                    Vector3[] freeEdge = new Vector3[2];
                    for (int k = 0; k < 2; k++)
                    {
                        Transform t = leaves[k];
                        float len;
                        Vector3 panel = PanelDir(t, out string how, out len);
                        float yaw = Mathf.Atan2(-panel.z, panel.x) * Mathf.Rad2Deg;
                        freeEdge[k] = t.position + panel * len;

                        sb.AppendLine("  → 门叶 " + k + " 门扇朝向=" + V2(panel) +
                                      "（Unity yaw=" + yaw.ToString("0.0") + "°，来源：" + how + "）");
                        sb.AppendLine("      门叶长度=" + len.ToString("0.000") +
                                      "m  自由边落点=" + V3(freeEdge[k]));
                        sb.AppendLine("      贴墙两解的角度：+墙 " +
                                      Vector3.SignedAngle(panel, WallDirAlong(true, h0, h1), Vector3.up).ToString("0.0") +
                                      "°  /  -墙 " +
                                      Vector3.SignedAngle(panel, WallDirAlong(false, h0, h1), Vector3.up).ToString("0.0") +
                                      "°");
                    }

                    // 两片自由边之间的距离：小于门叶厚度就是"关严了"，大于 0.2m 就是"漏一条缝"
                    Vector3 gap = freeEdge[1] - freeEdge[0]; gap.y = 0f;
                    sb.AppendLine("  → 两片自由边之间距离=" + gap.magnitude.ToString("0.000") + "m" +
                                  (gap.magnitude > 0.2f ? "  ⚠ 有缝（关门时会看到一条缝）" : "  ✔ 基本合拢"));

                    // 门轴到「对面那片门叶所在平面」的垂直距离：
                    // 只要这个值 > 0.1m，就说明门轴不在门框上，转到任何角度都关不严（要改预制体）。
                    // 门轴到「另一片门叶所在的那条门扇线」的垂直距离：
                    // 只要这个值 > 0.1m，就说明两片门叶不在同一条线上（门轴偏移），
                    // 转到任何角度都关不严 —— 那要改预制体，不是改动画。
                    Vector3 panel0 = PanelDir(leaves[0], out _, out _);
                    Vector3 normal0 = Vector3.Cross(Vector3.up, panel0).normalized;
                    float off = Mathf.Abs(Vector3.Dot(h1 - h0, normal0));
                    sb.AppendLine("  → 门叶1 门轴到「门叶0 门扇线」的垂距=" + off.ToString("0.000") + "m" +
                                  (off > 0.1f ? "  ⚠ 两片不在同一条线上（模型问题：门轴偏移）" : "  ✔ 同线"));
                }
            }

            if (multi == 0) sb.AppendLine("[P19Door] ⚠ 没有找到任何「多门叶」的门 —— 检查锚点是否还挂在 DoorDouble02 根上");

            sb.AppendLine();
            sb.AppendLine("[P19Door] ===== 结束 =====");

            string text = sb.ToString();
            // ⚠️ 落盘放在 Dump() 里，不放在 RunMenu() 里 ——
            // 第一版只在 Run()（批处理入口）里写文件，菜单那条路径**根本没写**，
            // 但对话框上却写着"已写入 p19_door.txt"。2026-10-22 用户点了菜单、我读不到文件，
            // 就是这个原因（和 TwinP19Survey 犯的是同一个错）。
            try { System.IO.File.WriteAllText("p19_door.txt", text, Encoding.UTF8); }
            catch (Exception e) { Debug.LogWarning("[P19Door] 写文件失败：" + e.Message); }
            return text;
        }

        /// <summary>
        /// 门扇朝向 = 门叶的**长轴**，而且指向**自由边**那一侧。
        ///
        /// 不能直接拿 `transform.right`：`DoorDouble02` 的两片门叶是镜像建模的，
        /// 一片往本地 +X 长、另一片往本地 -X 长（BoxCollider 的 center.x 一个是 +0.42、一个是 -0.42）。
        /// 用 `right` 的话，其中一片会永远算成反方向 —— 转起来就是"一片进、一片出"。
        /// </summary>
        private static Vector3 PanelDir(Transform leaf, out string how, out float length)
        {
            how = "transform.right（没有 BoxCollider，退回旧假设）";
            length = 0.835f;              // Door07a 的门扇长度，兜底用
            Vector3 axis = Vector3.right;
            float sign = 1f;

            BoxCollider bc = leaf.GetComponent<BoxCollider>();
            if (bc == null) bc = leaf.GetComponentInChildren<BoxCollider>(true);

            if (bc != null)
            {
                if (bc.size.x >= bc.size.z)
                {
                    axis = Vector3.right;
                    sign = bc.center.x >= 0f ? 1f : -1f;
                    length = bc.size.x;
                    how = "BoxCollider 长轴=本地X，center.x=" + bc.center.x.ToString("0.000") + " → " +
                          (sign > 0 ? "+X" : "-X");
                }
                else
                {
                    axis = Vector3.forward;
                    sign = bc.center.z >= 0f ? 1f : -1f;
                    length = bc.size.z;
                    how = "BoxCollider 长轴=本地Z，center.z=" + bc.center.z.ToString("0.000") + " → " +
                          (sign > 0 ? "+Z" : "-Z");
                }
            }

            Vector3 dir = leaf.TransformDirection(axis * sign);
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.0001f) return Vector3.right;
            return dir.normalized;
        }

        /// <summary>两根门轴连线方向（= 门洞方向，也就是"墙沿哪根轴走"）。</summary>
        private static Vector3 WallDirAlong(bool positive, Vector3 h0, Vector3 h1)
        {
            Vector3 d = h1 - h0;
            d.y = 0f;
            if (d.sqrMagnitude < 0.0001f) d = Vector3.forward;
            d.Normalize();
            return positive ? d : -d;
        }

        private static string V3(Vector3 v)
        {
            return "(" + v.x.ToString("0.000") + "," + v.y.ToString("0.000") + "," + v.z.ToString("0.000") + ")";
        }

        private static string V2(Vector3 v)
        {
            return "(" + v.x.ToString("0.000") + "," + v.z.ToString("0.000") + ")";
        }
    }
}

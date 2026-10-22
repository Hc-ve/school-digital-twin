using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using SchoolTwin.Twin;
using SchoolTwin.Present;

namespace SchoolTwin.EditorTools
{
    /// <summary>
    /// P17 只读诊断：**不改场景、不存盘**，只把「整房间照明」和「门的墙向」两件事的
    /// 真实运行时数值打出来，用来定位「为什么有的灯不亮」「门往哪边关」。
    ///
    /// 【为什么需要它】这两件事都依赖运行时才有的值：
    ///   · 灯的 `Renderer.bounds.center.y`（网格包围盒，离线读 YAML 拿不到）
    ///   · `anchor.VisualTarget` 解析后的真实对象
    ///   · 场景层级里 `transform.parent.childCount`
    /// 靠静态分析只能算到"应该"，算不到"实际"。
    ///
    /// 用法（Unity 必须关着）：
    ///   Unity.exe -batchmode -quit -nographics -projectPath &lt;工程&gt; \
    ///     -executeMethod SchoolTwin.EditorTools.TwinP17Diag.Run -logFile &lt;log&gt;
    /// </summary>
    public static class TwinP17Diag
    {
        private const string ScenePath =
            "Assets/TirGames/SchoolScene/Scenes/SchoolSceneDay.unity";

        public static void Run()
        {
            if (EditorApplication.isPlaying)
            {
                Debug.LogError("[P17Diag] 请先退出 Play 模式再跑诊断");
                return;
            }

            // 只在活动场景为空时才切，避免丢用户未保存的改动
            if (string.IsNullOrEmpty(EditorSceneManager.GetActiveScene().path))
            {
                EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[P17Diag] ===== 整房间照明 =====");
            DumpLamps(sb);
            sb.AppendLine();
            sb.AppendLine("[P17Diag] ===== 门的墙向 =====");
            DumpDoors(sb);
            sb.AppendLine();
            sb.AppendLine("[P17Diag] ===== 开关量 =====");
            DumpDiscrete(sb);

            Debug.Log(sb.ToString());
        }

        // ------------------------------------------------------------ 灯

        private static void DumpLamps(StringBuilder sb)
        {
            TwinAnchor[] all = UnityEngine.Object.FindObjectsOfType<TwinAnchor>(true);
            int points = 0;

            for (int i = 0; i < all.Length; i++)
            {
                TwinAnchor a = all[i];
                if (a == null) continue;
                if (!string.Equals(a.Category, "Light", StringComparison.Ordinal)) continue;
                if (a.TwinId != null && a.TwinId.StartsWith("BAK", StringComparison.Ordinal)) continue;
                points++;

                GameObject target = a.VisualTarget;
                Renderer[] rs = target != null
                    ? target.GetComponentsInChildren<Renderer>(true) : null;

                sb.AppendLine();
                sb.AppendLine("· " + a.TwinId + "  zone=" + a.Zone +
                              "  VisualTarget=" + (target != null ? target.name : "(null)") +
                              "  renderers=" + (rs != null ? rs.Length : 0));

                if (rs == null || rs.Length == 0) continue;

                Renderer main = rs[0];
                float best = -1f;
                for (int k = 0; k < rs.Length; k++)
                {
                    if (rs[k] == null) continue;
                    float size = rs[k].bounds.size.sqrMagnitude;
                    if (size > best) { best = size; main = rs[k]; }
                }

                Bounds mb = main.bounds;
                sb.AppendLine("  main=" + main.name +
                              "  bounds.center=(" + mb.center.x.ToString("0.00") + "," +
                              mb.center.y.ToString("0.00") + "," + mb.center.z.ToString("0.00") + ")" +
                              "  size.magnitude=" + mb.size.magnitude.ToString("0.00"));

                Transform container = main.transform.parent;
                sb.AppendLine("  container=" + (container != null ? container.name : "(null)") +
                              "  childCount=" + (container != null ? container.childCount : -1) +
                              "  zone登记=" + TwinRoomBounds.Has(a.Zone));

                // 锚点自己这一盏上有没有状态染色 —— 它曾经用 MaterialPropertyBlock 写
                // `_EmissionColor`，和表现层的「开=发光」抢同一个属性（2026-10-22 已修：
                // 灯的自发光归表现层，本组件对灯只染色不碰自发光）。
                TwinStatusColor sc = a.GetComponent<TwinStatusColor>();
                if (sc == null) sc = a.GetComponentInChildren<TwinStatusColor>(true);
                sb.AppendLine("  锚点物体=" + a.gameObject.name +
                              "  静态标记=" + DescribeStatic(a.gameObject) +
                              "  TwinStatusColor=" + (sc != null ? "有（只染色，不再写自发光）" : "无"));
                sb.AppendLine("  代表灯 静态标记=" + DescribeStatic(main.gameObject) +
                              "  material=" + (main.sharedMaterial != null ? main.sharedMaterial.name : "(null)") +
                              "  matID=" + (main.sharedMaterial != null ? main.sharedMaterial.GetInstanceID() : 0));

                if (container == null) continue;

                int hit = 0;
                float floorY = main.transform.position.y;
                for (int c = 0; c < container.childCount; c++)
                {
                    Transform child = container.GetChild(c);
                    if (child == null) continue;
                    Vector3 p = child.position;
                    float dy = Mathf.Abs(p.y - floorY);
                    bool sameFloor = dy <= 1.6f;
                    bool inRoom = TwinRoomBounds.Contains(a.Zone, p.x, p.z);
                    if (!sameFloor || !inRoom) continue;
                    hit++;

                    Renderer[] crs = child.GetComponentsInChildren<Renderer>(true);
                    int em = 0;
                    string mats = "";
                    for (int q = 0; q < crs.Length; q++)
                    {
                        Material m = crs[q] != null ? crs[q].sharedMaterial : null;
                        if (m != null && m.HasProperty("_EmissionColor")) em++;
                        mats += (m != null ? m.name + "#" + m.GetInstanceID() : "(null)") + " ";
                    }
                    sb.AppendLine("    ✔ " + child.name +
                                  "  pos=(" + p.x.ToString("0.00") + "," + p.y.ToString("0.00") + "," +
                                  p.z.ToString("0.00") + ")" +
                                  "  dy=" + dy.ToString("0.00") +
                                  "  renderers=" + crs.Length + " 有emission=" + em +
                                  "  " + mats.Trim() +
                                  "  " + DescribeStatic(child.gameObject));
                }

                sb.AppendLine("  → 命中 " + hit + " 盏");
                if (hit <= 1)
                {
                    sb.AppendLine("  ⚠ 只命中 1 盏：这就是「点开灯只亮一盏」的原因");
                }
            }

            sb.AppendLine();
            sb.AppendLine("照明点位合计 " + points + " 个");
        }

        /// <summary>
        /// 静态标记里最要紧的一位：`BatchingStatic`。
        ///
        /// 【为什么要打这一位】静态合批会把物体烘进一张合并网格，之后**改 Transform 看不见**
        /// （所以门必须清这一位，绑定工具已经做了）。
        ///
        /// 【但它不影响改材质】2026-10-22 实测：2-1班 8 盏灯里 7 盏 `BatchingStatic=开`，
        /// 改 `renderer.material` 的自发光**照样看得见**（合批是按 renderer 的 subMesh 段画的，
        /// 换材质只是让这一盏单独出一个 draw call）。真正被合批吃掉的是 Transform 变更，
        /// 以及 MaterialPropertyBlock 在部分平台上的表现。别把「灯不亮」归到这一位上。
        /// </summary>
        private static string DescribeStatic(GameObject go)
        {
            if (go == null) return "(null)";
            StaticEditorFlags f = GameObjectUtility.GetStaticEditorFlags(go);
            bool batch = (f & StaticEditorFlags.BatchingStatic) != 0;
            return "isStatic=" + go.isStatic + " BatchingStatic=" + (batch ? "开" : "关");
        }

        // ------------------------------------------------------------ 门

        private static void DumpDoors(StringBuilder sb)
        {
            TwinAnchor[] all = UnityEngine.Object.FindObjectsOfType<TwinAnchor>(true);
            for (int i = 0; i < all.Length; i++)
            {
                TwinAnchor a = all[i];
                if (a == null) continue;
                if (!string.Equals(a.Category, "Door", StringComparison.Ordinal)) continue;
                if (a.TwinId != null && a.TwinId.StartsWith("BAK", StringComparison.Ordinal)) continue;

                GameObject target = a.VisualTarget;
                Transform t = target != null ? target.transform : a.transform;

                Vector3 panel = t.right; panel.y = 0f;
                if (panel.sqrMagnitude < 1e-6f) { sb.AppendLine("· " + a.TwinId + " 门扇方向竖直，跳过"); continue; }
                panel.Normalize();

                Vector3 hinge = t.position;
                sb.AppendLine();
                sb.AppendLine("· " + a.TwinId + "  zone=" + a.Zone + "  target=" + t.name);
                sb.AppendLine("  枢轴=(" + hinge.x.ToString("0.00") + "," + hinge.y.ToString("0.00") + "," +
                              hinge.z.ToString("0.00") + ")");
                sb.AppendLine("  门扇方向=(" + panel.x.ToString("0.000") + "," + panel.z.ToString("0.000") + ")" +
                              "  yaw=" + (Mathf.Atan2(panel.z, panel.x) * Mathf.Rad2Deg).ToString("0.0") + "°");

                float bx0, bx1, bz0, bz1;
                if (!TwinRoomBounds.TryGetBounds(a.Zone, out bx0, out bx1, out bz0, out bz1))
                {
                    sb.AppendLine("  房间范围未登记 → 不接管");
                    continue;
                }

                Vector3 center = new Vector3((bx0 + bx1) * 0.5f, hinge.y, (bz0 + bz1) * 0.5f);
                Vector3 toCenter = center - hinge; toCenter.y = 0f;
                sb.AppendLine("  房间范围 x[" + bx0.ToString("0.0") + "," + bx1.ToString("0.0") +
                              "] z[" + bz0.ToString("0.0") + "," + bz1.ToString("0.0") +
                              "]  中心=(" + center.x.ToString("0.00") + "," + center.z.ToString("0.00") + ")");

                // 墙向：门在哪根轴上跑出了房间范围
                bool outX = hinge.x < bx0 || hinge.x > bx1;
                bool outZ = hinge.z < bz0 || hinge.z > bz1;
                bool alongX;
                if (outX != outZ) alongX = outZ;
                else { sb.AppendLine("  墙向说不清（outX=" + outX + " outZ=" + outZ + "）"); continue; }
                sb.AppendLine("  墙向=" + (alongX ? "X" : "Z") +
                              "（outX=" + outX + " outZ=" + outZ + "）");

                Vector3 wallDir = alongX ? Vector3.right : Vector3.forward;

                // 两个候选：朝 +wallDir / 朝 -wallDir
                for (int s = 0; s < 2; s++)
                {
                    Vector3 closedDir = s == 0 ? wallDir : -wallDir;
                    float ang = Vector3.SignedAngle(panel, closedDir, Vector3.up);
                    Vector3 far = hinge + closedDir * 0.835f;

                    // 判据只看**沿墙那根轴**：门扇关到位时躺在墙里，
                    // 垂直方向本来就落在房间范围之外（墙厚），拿它当判据会两边都不成立。
                    bool spanOk = alongX
                        ? (far.x >= bx0 && far.x <= bx1)
                        : (far.z >= bz0 && far.z <= bz1);
                    float dot = Vector3.Dot(toCenter, closedDir);

                    sb.AppendLine("  候选" + (s == 0 ? "+" : "-") + wallDir.ToString() +
                                  "：关门角=" + ang.ToString("0.0") + "°" +
                                  "  门边落点=(" + far.x.ToString("0.00") + "," + far.z.ToString("0.00") + ")" +
                                  "  沿墙跨度内=" + spanOk +
                                  "  整体在房间内=" + (far.x >= bx0 && far.x <= bx1 && far.z >= bz0 && far.z <= bz1) +
                                  "  朝向房间中心=" + (dot > 0f));
                }

                // 旧规则（已废弃）：取与门扇当前方向同侧的那个
                Vector3 old = Vector3.Dot(panel, wallDir) >= 0f ? wallDir : -wallDir;
                sb.AppendLine("  → 旧规则（同侧）选 " + (Vector3.Dot(old, wallDir) > 0f ? "+" : "-") + wallDir.ToString() +
                              "；新规则（沿墙跨度）选 " +
                              ((alongX ? (hinge.x + 0.835f >= bx0 && hinge.x + 0.835f <= bx1)
                                       : (hinge.z + 0.835f >= bz0 && hinge.z + 0.835f <= bz1)) ? "+" : "-") +
                              wallDir.ToString());
            }
        }

        // ------------------------------------------------------------ 开关量

        private static void DumpDiscrete(StringBuilder sb)
        {
            TwinCatalog[] cats = Resources.FindObjectsOfTypeAll<TwinCatalog>();
            if (cats == null || cats.Length == 0) { sb.AppendLine("找不到 TwinCatalog"); return; }

            int total = 0, disc = 0;
            for (int c = 0; c < cats.Length; c++)
            {
                TwinCatalog cat = cats[c];
                if (cat == null || cat.Points == null) continue;
                for (int i = 0; i < cat.Points.Count; i++)
                {
                    TwinPointDefinition p = cat.Points[i];
                    if (p == null || p.Metrics == null) continue;
                    for (int k = 0; k < p.Metrics.Count; k++)
                    {
                        TwinMetricDefinition m = p.Metrics[k];
                        if (m == null) continue;
                        total++;
                        bool d = m.Min > -0.0001f && m.Min < 0.0001f && (m.Max - m.Min) <= 1.0001f;
                        if (!d) continue;
                        disc++;
                        sb.AppendLine("  开关量：" + p.TwinId + " / " + m.Metric +
                                      "  量程 " + m.Min.ToString("0.##") + "~" + m.Max.ToString("0.##") +
                                      "  阈值 " + m.WarnLow.ToString("0.##") + "~" + m.WarnHigh.ToString("0.##"));
                    }
                }
            }
            sb.AppendLine("  指标合计 " + total + "，判为开关量 " + disc);
        }
    }
}

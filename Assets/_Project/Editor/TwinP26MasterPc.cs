using System;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using SchoolTwin.Interaction;
using SchoolTwin.Twin;

namespace SchoolTwin.EditorTools
{
    /// <summary>
    /// 【P26】给「机房主脑电脑」挂锚点 + 交互点。
    ///
    /// ── 为什么要拆点 ────────────────────────────────────────────────
    /// 原来 `ClassComputer/PC/Room_01` 的语义是「**整间机房**的电脑」——19 台一个点位。
    /// 但交互设计要求区分三种开机模式：
    ///   普通开机 = 只开主脑 / 联动开机 = 全机房 / 投影 = 主脑 + 投影仪
    /// 一个点位表达不了「只开主脑」，所以必须拆成：
    ///   `ClassComputer/PC/Master_01`（1 台，交互对象）
    ///   `ClassComputer/PC/Room_01`  （其余 18 台，联动时才开）
    ///
    /// ── 用法 ────────────────────────────────────────────────────────
    ///   1. 在 Hierarchy 里**选中主脑那台显示器**（就是你截图里的 `Monitor04 (10)`）
    ///   2. 菜单 `P26 主脑 / ① 挂锚点与交互点`
    ///   3. 跑 `P24 灯锚点 / ② 同步点位目录`（否则数据链路是断的，今天踩过）
    ///
    /// 幂等：已经挂过就只补缺失的组件，不重复加。
    /// </summary>
    public static class TwinP26MasterPc
    {
        private const string MasterTwinId = "ClassComputer/PC/Master_01";
        private const string MasterZone = "ClassComputer";

        [MenuItem("P26 主脑/① 挂锚点与交互点", false, 10)]
        public static void AttachMenu()
        {
            if (EditorApplication.isPlaying)
            {
                EditorUtility.DisplayDialog("已拒绝",
                    "现在是 Play 模式。请先按停止 —— 加组件和存场景都会出问题。", "好");
                return;
            }

            GameObject sel = Selection.activeGameObject;
            if (sel == null)
            {
                EditorUtility.DisplayDialog("先选中主脑",
                    "请先在 Hierarchy 里选中「主脑那台显示器」，再点这个菜单。\n\n" +
                    "（不要选父节点 —— 锚点要贴在显示器本身，否则排除规则会连带排除掉别的屏）", "好");
                return;
            }

            // 选中的必须真的是一台显示器：材质名 Monitor04*（和 BuildScreen 的判据一致）
            Renderer rend = sel.GetComponent<Renderer>();
            if (rend == null) rend = sel.GetComponentInChildren<Renderer>();
            if (rend == null || rend.sharedMaterial == null ||
                !rend.sharedMaterial.name.StartsWith("Monitor04", StringComparison.Ordinal))
            {
                string matName = (rend != null && rend.sharedMaterial != null) ? rend.sharedMaterial.name : "(无材质)";
                EditorUtility.DisplayDialog("选错了",
                    "选中的物体不是显示器。\n\n" +
                    "当前材质：" + matName + "\n" +
                    "判据：材质名必须以 `Monitor04` 开头（与 BuildScreen 一致）。", "好");
                return;
            }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[P26] ===== 挂主脑锚点 =====");
            sb.AppendLine("[P26] 目标物体：" + sel.name + "（材质 " + rend.sharedMaterial.name + "）");

            Scene scene = SceneManager.GetActiveScene();

            // ---- 幂等检查：全场景不能有第二个 Master_01 ----
            TwinAnchor[] all = UnityEngine.Object.FindObjectsOfType<TwinAnchor>(true);
            TwinAnchor exist = null;
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null && string.Equals(all[i].TwinId, MasterTwinId, StringComparison.Ordinal))
                {
                    exist = all[i];
                    break;
                }
            }
            if (exist != null && exist.gameObject != sel)
            {
                sb.AppendLine("[P26] ⚠ 场景里已经有一个「" + MasterTwinId + "」挂在「" +
                              exist.gameObject.name + "」上，本次不重复创建。");
                sb.AppendLine("       要换主脑，先把那个物体上的 TwinAnchor 删掉再重跑。");
                Debug.Log(sb.ToString());
                EditorUtility.DisplayDialog("已存在", sb.ToString(), "好");
                return;
            }

            // ---- TwinAnchor ----
            TwinAnchor anchor = sel.GetComponent<TwinAnchor>();
            if (anchor == null)
            {
                anchor = Undo.AddComponent<TwinAnchor>(sel);
                sb.AppendLine("[P26] ✔ 加了 TwinAnchor");
            }
            else
            {
                sb.AppendLine("[P26] · 已有 TwinAnchor，只补字段");
            }

            SerializedObject so = new SerializedObject(anchor);
            so.FindProperty("_twinId").stringValue = MasterTwinId;
            so.FindProperty("_displayName").stringValue = "机房主脑";
            so.FindProperty("_zone").stringValue = MasterZone;
            so.FindProperty("_category").stringValue = "PC";
            so.ApplyModifiedPropertiesWithoutUndo();

            // ---- TwinInteractPoint ----
            TwinInteractPoint point = sel.GetComponent<TwinInteractPoint>();
            if (point == null)
            {
                point = Undo.AddComponent<TwinInteractPoint>(sel);
                sb.AppendLine("[P26] ✔ 加了 TwinInteractPoint");
            }
            else
            {
                sb.AppendLine("[P26] · 已有 TwinInteractPoint，只补字段");
            }
            point.TargetTwinId = MasterTwinId;
            point.Range = 2.2f;
            point.Label = "";          // 空 = 用模型默认文案
            EditorUtility.SetDirty(point);

            EditorSceneManager.MarkSceneDirty(scene);
            bool saved = EditorSceneManager.SaveScene(scene);

            sb.AppendLine();
            sb.AppendLine("[P26] TwinId  = " + MasterTwinId);
            sb.AppendLine("[P26] Zone    = " + MasterZone + " / Category = PC");
            sb.AppendLine("[P26] 交互范围 = 2.2m");
            sb.AppendLine("[P26] 场景保存：" + (saved ? "成功" : "失败"));
            sb.AppendLine();
            sb.AppendLine("[P26] 下一步（顺序别错）：");
            sb.AppendLine("   1. 跑 `P24 灯锚点 / ② 同步点位目录（补缺失）`  ← 不做这步数据链路是断的");
            sb.AppendLine("   2. Play，走到主脑前应弹出交互按钮");
            sb.AppendLine("   3. Console 搜 `PC/Master_01` 确认接管了 **1 台**显示器，");
            sb.AppendLine("      搜 `PC/Room_01` 确认接管了 **18 台**（不是 19 台）");
            sb.AppendLine("[P26] ===== 结束 =====");

            try { System.IO.File.WriteAllText("p26_master.txt", sb.ToString(), Encoding.UTF8); }
            catch (Exception) { }

            Debug.Log(sb.ToString());
            EditorUtility.DisplayDialog("P26 主脑", sb.ToString(), "好");
        }

        [MenuItem("P26 主脑/② 检查（只读，不改场景）", false, 11)]
        public static void CheckMenu()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[P26] ===== 主脑检查 =====");

            TwinAnchor[] all = UnityEngine.Object.FindObjectsOfType<TwinAnchor>(true);
            TwinAnchor master = null;
            int pcCount = 0;
            for (int i = 0; i < all.Length; i++)
            {
                TwinAnchor a = all[i];
                if (a == null) continue;
                if (!string.Equals(a.Category, "PC", StringComparison.OrdinalIgnoreCase)) continue;
                pcCount++;
                sb.AppendLine("[P26] PC 点位：" + a.TwinId + "  → " + a.gameObject.name);
                if (string.Equals(a.TwinId, MasterTwinId, StringComparison.Ordinal)) master = a;
            }
            if (pcCount == 0) sb.AppendLine("[P26] 场景里一个 PC 点位都没有");

            sb.AppendLine();
            if (master == null)
                sb.AppendLine("[P26] ✘ 没有 " + MasterTwinId + "，先跑 ①");
            else
                sb.AppendLine("[P26] ✔ 主脑 = 「" + master.gameObject.name + "」");

            sb.AppendLine("[P26] 白名单里有 Master_01：" +
                          (TwinControlScope.IsEnabled(MasterTwinId) ? "是" : "否 ✘ 去 TwinControlScope 加"));

            sb.AppendLine("[P26] ===== 结束 =====");
            Debug.Log(sb.ToString());
            EditorUtility.DisplayDialog("P26 检查", sb.ToString(), "好");
        }
    }
}

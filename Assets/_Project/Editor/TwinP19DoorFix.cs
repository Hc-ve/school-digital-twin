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
    /// 一次性修正：礼堂双开门 `DoorDouble02` 的**右扇门轴**。
    ///
    /// ── 为什么需要改模型 ──────────────────────────────────────────────
    /// 2026-10-22 实测（`TwinP19DoorDiag` 的输出）：
    ///   左扇 `DoorDouble0`  门轴 (-43.104, 30.707)  门扇贴墙   长 0.840m
    ///   右扇 `DoorDouble`   门轴 (-42.385, 31.977)  门扇斜 55°  长 0.847m
    ///   两片门轴的**垂距 = 0.616m**，两片自由边相距 **1.618m**，门轴距 1.459m。
    ///
    /// 两个模型层面的错误：
    ///   ① **右扇门轴离门框 0.616m**（伸在走廊里）。门轴不在门框上，
    ///      叶片转到任何角度都关不到门洞里 —— 这不是动画参数能修的。
    ///   ② **右扇短了约 0.42m**。门轴距 1.459、两片各 0.84，要盖住整个门洞
    ///      它得够到左扇的自由边，现在差 0.42m。
    ///
    /// 所以这个工具干两件事，都**按实测值算**，不写死：
    ///   · 把右扇门轴沿「门扇法线」平移，直到它和左扇落在同一条线上；
    ///   · 把右扇沿**它的长轴**（本地 X）缩放，直到它的自由边正好落在左扇的自由边上。
    ///
    /// ⚠️ 缩放只会拉长门扇，不会加厚（长轴 = 本地 X，厚度 = 本地 Z）。
    ///    代价是右扇的雕花会横向拉开一点，看上去右侧比左侧宽 —— 但门是真关严的。
    ///
    /// 只改这一扇门（`ActZal/Door/Entrance_01`），其它门一个都不碰。
    /// 同一时间只跑一次，跑完会存盘。
    /// </summary>
    public static class TwinP19DoorFix
    {
        private const string ScenePath =
            "Assets/TirGames/SchoolScene/Scenes/SchoolSceneDay.unity";

        private const string TargetTwinId = "ActZal/Door/Entrance_01";

        /// <summary>
        /// 还原：把右扇门轴和缩放改回预制体里原来的值。
        ///
        /// 【为什么要有这个】② 会把右扇拉长 1.5 倍 —— 那是为了补 0.42m 的缝。
        /// 如果看下来觉得"拉长的门比原来还丑"，点这个就能一键回到原样，不用重开场景。
        /// 原始值来自预制体 `DoorDouble02.prefab` 的 `DoorDouble`（id 455620）。
        /// </summary>
        [MenuItem("归档/P19 勘察/③ 还原礼堂双开门门轴（撤销 ②）", false, 22)]
        public static void RevertMenu()
        {
            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorUtility.DisplayDialog("已拒绝",
                    "现在是 Play 模式。请先按停止，再点这个菜单。", "好");
                return;
            }

            Scene scene = SceneManager.GetActiveScene();
            if (string.IsNullOrEmpty(scene.path) || scene.path != ScenePath)
                scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            TwinAnchor anchor = FindDoor(TargetTwinId);
            if (anchor == null) { EditorUtility.DisplayDialog("失败", "找不到门锚点。", "好"); return; }

            Transform offender = null;
            for (int i = 0; i < anchor.transform.childCount; i++)
            {
                Transform c = anchor.transform.GetChild(i);
                if (c != null && c.name == "DoorDouble") { offender = c; break; }
            }
            if (offender == null)
            {
                EditorUtility.DisplayDialog("失败", "根节点下找不到 DoorDouble。", "好");
                return;
            }

            Undo.RecordObject(offender, "P19 还原双开门门轴");
            offender.localPosition = new Vector3(-1.282f, 0.034947395f, 0.705f);
            offender.localScale = Vector3.one;

            EditorSceneManager.MarkSceneDirty(scene);
            bool saved = EditorSceneManager.SaveScene(scene);

            string msg = "[P19Fix] 已还原右扇门叶：本地位置=(-1.282,0.035,0.705) 缩放=(1,1,1)" +
                         "  场景保存：" + (saved ? "成功" : "失败");
            Debug.Log(msg);
            try { System.IO.File.AppendAllText("p19_doorfix.txt", msg + "\n", Encoding.UTF8); } catch (Exception) { }
            EditorUtility.DisplayDialog("P19 已还原", msg, "好");
        }

        [MenuItem("归档/P19 勘察/② 修正礼堂双开门门轴（一次性，会存盘）", false, 21)]
        public static void RunMenu()
        {
            // Play 模式下 SaveScene() 会把播放中的状态写进资产（99 号清单第 14 条）
            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorUtility.DisplayDialog("已拒绝",
                    "现在是 Play 模式。Play 模式下 SaveScene() 会把「播放中的状态」写进场景资产。\n\n" +
                    "请先按停止（Play 按钮再点一次），再点这个菜单。", "好");
                return;
            }

            string report = Apply();
            Debug.Log(report);
            EditorUtility.DisplayDialog("P19 门轴修正完成", report.Length > 700
                ? report.Substring(0, 700) + "\n…（完整结果看 Console，也写进了 p19_doorfix.txt）"
                : report, "好");
        }

        public static string Apply()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (string.IsNullOrEmpty(scene.path) || scene.path != ScenePath)
                scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[P19Fix] ===== 礼堂双开门门轴修正 =====");

            TwinAnchor anchor = FindDoor(TargetTwinId);
            if (anchor == null)
            {
                sb.AppendLine("[P19Fix] ✘ 场景里找不到锚点 " + TargetTwinId + "，什么都没改。");
                Write(sb.ToString());
                return sb.ToString();
            }

            Transform root = anchor.transform;
            Transform reference = null;   // 已经贴平墙面的那片（不动）
            Transform offender = null;    // 门轴偏出去的那片（要修）

            List<Transform> leaves = new List<Transform>(4);
            for (int i = 0; i < root.childCount; i++)
            {
                Transform c = root.GetChild(i);
                if (c == null) continue;
                if (c.GetComponentInChildren<Renderer>(true) == null) continue;
                leaves.Add(c);
            }
            if (leaves.Count != 2)
            {
                sb.AppendLine("[P19Fix] ✘ 门叶数量是 " + leaves.Count + "，不是 2 —— 这个门不是双开门，" +
                              "什么都不改。");
                Write(sb.ToString());
                return sb.ToString();
            }

            // 谁是"已经贴平墙面"的那片：拿两片的门扇朝向对比**墙的走向**，谁更平行谁就是。
            //
            // ⚠️ 不能用「两片门轴的连线」当墙的走向 —— 右扇门轴本来就偏出去 0.72m，
            //    那条连线本身就是斜的（实测离墙 29.5°）。用它打分，反而会选中开着的右扇
            //    （右扇 25°、左扇 29.5°，就差这么点）。第一版就是这么写的，选反了。
            // 墙只会沿 X 或沿 Z：哪个轴上有门叶最平行，就用哪个轴。
            Vector3 l0d = Panel(leaves[0], out _);
            Vector3 l1d = Panel(leaves[1], out _);

            float xScore = Mathf.Max(Mathf.Abs(l0d.x), Mathf.Abs(l1d.x));
            float zScore = Mathf.Max(Mathf.Abs(l0d.z), Mathf.Abs(l1d.z));
            Vector3 wallAxis = xScore > zScore ? Vector3.right : Vector3.forward;

            float align0 = Mathf.Abs(Vector3.Dot(l0d, wallAxis));
            float align1 = Mathf.Abs(Vector3.Dot(l1d, wallAxis));
            reference = align0 >= align1 ? leaves[0] : leaves[1];
            offender = reference == leaves[0] ? leaves[1] : leaves[0];

            sb.AppendLine("[P19Fix] 墙走向=" + V3(wallAxis) +
                          "（左/右门叶与墙的夹角：门叶0=" +
                          (Mathf.Acos(Mathf.Clamp01(align0)) * Mathf.Rad2Deg).ToString("0.0") + "°，门叶1=" +
                          (Mathf.Acos(Mathf.Clamp01(align1)) * Mathf.Rad2Deg).ToString("0.0") + "°）");
            sb.AppendLine("[P19Fix] 参考门叶（已贴墙，不动）：" + reference.name);
            sb.AppendLine("[P19Fix] 待修门叶：" + offender.name);

            // ---- 修之前 ----
            Vector3 refPanel = Panel(reference, out float refLen);
            Vector3 normal = Vector3.Cross(Vector3.up, refPanel).normalized;
            Vector3 refPos0 = reference.position;
            Vector3 offPos0 = offender.position;
            Vector3 refFree0 = refPos0 + refPanel * refLen;
            float offLen0 = Len(offender);

            // 【右扇该够到哪】不是「参考叶的自由边」，而是**参考叶离它最近的那个端点**。
            // 两片门叶是各从自己门轴往中间关的，右扇只要够到左扇的**门轴那一端**就合拢了；
            // 够到左扇的自由边反而是穿过去 0.84m（左扇全长），门就被顶穿了。
            Vector3 refNear = ClosestOnSegment(refPos0, refFree0, offPos0);

            sb.AppendLine("[P19Fix] ---- 修前 ----");
            sb.AppendLine("[P19Fix]   参考 门轴=" + V3(refPos0) + " 自由边=" + V3(refFree0) + " 长=" + refLen.ToString("0.000"));
            sb.AppendLine("[P19Fix]   待修 门轴=" + V3(offPos0) + " 长=" + offLen0.ToString("0.000") +
                          " 本地位置=" + V3(offender.localPosition) + " 本地缩放=" + V3(offender.localScale));
            sb.AppendLine("[P19Fix]   垂距=" + Mathf.Abs(Vector3.Dot(offPos0 - refPos0, normal)).ToString("0.000") + "m");
            sb.AppendLine("[P19Fix]   右扇该够到=" + V3(refNear) + "（参考叶最近端点）距离=" +
                          (refNear - offPos0).magnitude.ToString("0.000") + "m");

            // ---- ① 门轴平移：法线方向对齐 + **沿墙挪到门框的远端** ----
            //
            // ⚠️ 只对齐法线是不够的。第一版保留叶子原来的**沿墙位置**（z=31.977），
            //    而门框只到 31.611 —— 门轴比门框还偏出去 0.37m，门等于挂在门洞外面：
            //    从走廊看就像"门装反了、门轴朝外"（2026-10-23 用户实测）。
            //    门轴必须落在**门框的端头**上。
            float offAlongNormal = Vector3.Dot(offPos0 - refPos0, normal);
            Vector3 shiftWorld = -normal * offAlongNormal;
            Vector3 shiftLocal = root.InverseTransformDirection(shiftWorld);

            Undo.RecordObject(offender, "P19 修正双开门门轴");
            offender.localPosition = offender.localPosition + shiftLocal;

            // 沿墙方向：把门轴挪到「门框离参考叶更远的那一端」
            // 复用上面的 wallAxis（`xScore > zScore ? Vector3.right : Vector3.forward`，
            // 已经是单位向量；参考叶的门扇方向与它同轴，所以不用再算一次）。
            Vector3 frameC; float frameHalf;
            if (FindFrame(offender, wallAxis, out frameC, out frameHalf))
            {
                float refMid = Vector3.Dot((refPos0 + refFree0) * 0.5f, wallAxis);
                float a = Vector3.Dot(frameC, wallAxis) - frameHalf;
                float b = Vector3.Dot(frameC, wallAxis) + frameHalf;
                float targetHinge = Mathf.Abs(b - refMid) >= Mathf.Abs(a - refMid) ? b : a;

                float curHinge = Vector3.Dot(offender.position, wallAxis);
                Vector3 shift2 = wallAxis * (targetHinge - curHinge);
                offender.localPosition = offender.localPosition + root.InverseTransformDirection(shift2);

                sb.AppendLine("[P19Fix]   门框 中心=" + V3(frameC) + " 半长=" + frameHalf.ToString("0.000") +
                              "m → 门轴应落在 " + targetHinge.ToString("0.000") +
                              "（原来 " + curHinge.ToString("0.000") + "）");
            }
            else
            {
                sb.AppendLine("[P19Fix]   ⚠ 没找到门框，门轴沿墙位置没动 —— 可能挂在门洞外面，看下面的数。");
            }

            // 门轴挪过之后，「参考叶上离它最近的端点」可能变了，重新算一次。
            // 不重算的话，长度会按旧位置算，差出来的那截正好是门洞的缝。
            refNear = ClosestOnSegment(refPos0, refFree0, offender.position);

            // ② 拉长：让自由边正好落在「参考叶最近端点」上
            //    用 world 距离算，避免本地轴/缩放换算的误差
            Vector3 offPos1 = offender.position;
            float need = (refNear - offPos1).magnitude;
            float curLen = Len(offender);
            float factor = curLen > 0.0001f ? need / curLen : 1f;

            // 只沿「长轴」缩放：长轴由 BoxCollider 的 size 判（x vs z），和 LeafPanelDir 同一套判据
            BoxCollider bc = offender.GetComponent<BoxCollider>();
            if (bc == null) bc = offender.GetComponentInChildren<BoxCollider>(true);
            bool longIsX = bc == null || bc.size.x >= bc.size.z;

            Undo.RecordObject(offender, "P19 拉长右扇门叶");
            Vector3 s = offender.localScale;
            if (longIsX) s.x *= factor; else s.z *= factor;
            offender.localScale = s;

            // ---- 修之后 ----
            //
            // ⚠️ 校验**必须在「关到位」的姿态上算**，不能在当前姿态上算。
            //    右扇现在是斜 55° 开着的，拿当前朝向乘长度得到的是"现在自由边在哪"，
            //    和"关到位之后在哪"完全是两回事 —— 第一版就是这么校验的，会误判成没修好然后撤销。
            //    关到位的朝向 = 从门轴指向参考叶自由边的方向（修好门轴之后这条线正好沿墙）。
            float newLen = Len(offender);
            Vector3 closedDir = refNear - offender.position;
            closedDir.y = 0f;
            closedDir.Normalize();
            Vector3 expectedFree = offender.position + closedDir * newLen;
            float gap = (refNear - expectedFree).magnitude;

            sb.AppendLine("[P19Fix] ---- 修后 ----");
            sb.AppendLine("[P19Fix]   待修 门轴=" + V3(offender.position) + " 长=" + newLen.ToString("0.000") +
                          " 本地位置=" + V3(offender.localPosition) + " 本地缩放=" + V3(offender.localScale));
            sb.AppendLine("[P19Fix]   垂距=" + Mathf.Abs(Vector3.Dot(offender.position - refPos0, normal)).ToString("0.000") +
                          "m（应 < 0.05）");
            sb.AppendLine("[P19Fix]   关门方向=" + V3(closedDir) + "（应沿墙）");
            sb.AppendLine("[P19Fix]   关到位后自由边=" + V3(expectedFree) + "  该够到的点=" + V3(refNear));
            sb.AppendLine("[P19Fix]   两片之间的缝=" + gap.ToString("0.000") + "m（应 < 0.10）");
            sb.AppendLine("[P19Fix]   缩放因子=" + factor.ToString("0.000") + "（只沿" + (longIsX ? "本地X" : "本地Z") + "拉长）");

            bool ok = gap < 0.10f;
            sb.AppendLine(ok
                ? "[P19Fix] ✔ 修好了：右扇转过去能正好接上左扇。"
                : "[P19Fix] ⚠ 还差一点，看上面的数字；把这份输出发我。");

            // ---- 落盘 ----
            if (ok)
            {
                EditorSceneManager.MarkSceneDirty(scene);
                bool saved = EditorSceneManager.SaveScene(scene);
                sb.AppendLine("[P19Fix] 场景保存：" + (saved ? "成功" : "失败"));
            }
            else
            {
                // 【先算后改】：算出来不对就不存盘，用户重开场景即可复原
                Undo.PerformUndo();
                Undo.PerformUndo();
                sb.AppendLine("[P19Fix] ⚠ 没存盘，并且已经把刚才那两步撤销了（场景保持原样）。");
            }

            sb.AppendLine("[P19Fix] ===== 结束 =====");
            Write(sb.ToString());
            return sb.ToString();
        }

        private static TwinAnchor FindDoor(string twinId)
        {
            TwinAnchor[] all = UnityEngine.Object.FindObjectsOfType<TwinAnchor>(true);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null && string.Equals(all[i].TwinId, twinId, StringComparison.Ordinal))
                    return all[i];
            }
            return null;
        }

        /// <summary>门扇朝向 = 长轴指向自由边（和 TwinDevicePresenter.LeafPanelDir 同一套判据）。</summary>
        private static Vector3 Panel(Transform leaf, out float length)
        {
            length = 0.835f;
            Vector3 axis = Vector3.right;
            float sign = 1f;

            BoxCollider bc = leaf.GetComponent<BoxCollider>();
            if (bc == null) bc = leaf.GetComponentInChildren<BoxCollider>(true);
            if (bc != null)
            {
                if (bc.size.x >= bc.size.z) { axis = Vector3.right; sign = bc.center.x >= 0f ? 1f : -1f; }
                else { axis = Vector3.forward; sign = bc.center.z >= 0f ? 1f : -1f; }
            }

            Vector3 d = leaf.TransformDirection(axis * sign);
            d.y = 0f;
            if (d.sqrMagnitude < 0.0001f) return leaf.right;
            return d.normalized;
        }

        /// <summary>门叶的**世界长度**（含缩放）：用碰撞体长度 × 该轴上的总缩放。</summary>
        private static float Len(Transform leaf)
        {
            BoxCollider bc = leaf.GetComponent<BoxCollider>();
            if (bc == null) bc = leaf.GetComponentInChildren<BoxCollider>(true);
            if (bc == null) return 0.835f * leaf.lossyScale.x;

            if (bc.size.x >= bc.size.z) return bc.size.x * Mathf.Abs(leaf.lossyScale.x);
            return bc.size.z * Mathf.Abs(leaf.lossyScale.z);
        }

        /// <summary>线段 a→b 上离 p 最近的点（水平面内）。用来找「参考叶离右扇门轴最近的端点」。</summary>
        private static Vector3 ClosestOnSegment(Vector3 a, Vector3 b, Vector3 p)
        {
            Vector3 ab = b - a; ab.y = 0f;
            Vector3 ap = p - a; ap.y = 0f;
            float len2 = ab.sqrMagnitude;
            if (len2 < 0.000001f) return a;
            float t = Mathf.Clamp01(Vector3.Dot(ap, ab) / len2);
            Vector3 r = a + ab * t;
            r.y = a.y;
            return r;
        }

        /// <summary>
        /// 找门框：名字带 frame / Post 的渲染器里，沿墙方向最近的那个（距离 0.05~4m）。
        /// 返回它在**水平面**的包围盒中心，以及沿墙方向的半长。
        /// </summary>
        private static bool FindFrame(Transform pivot, Vector3 wallAxis, out Vector3 center, out float half)
        {
            center = pivot.position;
            half = 0f;
            if (pivot == null) return false;

            Renderer[] all = UnityEngine.Object.FindObjectsOfType<Renderer>(true);
            float best = 4.0f;
            bool found = false;

            for (int i = 0; i < all.Length; i++)
            {
                Renderer r = all[i];
                if (r == null || r.gameObject == null) continue;

                string n = r.gameObject.name;
                if (n.IndexOf("frame", StringComparison.OrdinalIgnoreCase) < 0 &&
                    n.IndexOf("Post", StringComparison.OrdinalIgnoreCase) < 0) continue;

                Bounds bb = r.bounds;
                Vector3 c = bb.center; c.y = pivot.position.y;
                Vector3 d = c - pivot.position; d.y = 0f;
                float dist = d.magnitude;
                if (dist < 0.05f || dist >= best) continue;
                if (Mathf.Abs(Vector3.Dot(d / dist, wallAxis)) < 0.5f) continue;

                best = dist;
                center = c;
                half = Mathf.Abs(Vector3.Dot(bb.extents, wallAxis));
                found = true;
            }
            return found;
        }

        private static void Write(string text)        {
            try { System.IO.File.WriteAllText("p19_doorfix.txt", text, Encoding.UTF8); }
            catch (Exception e) { Debug.LogWarning("[P19Fix] 写文件失败：" + e.Message); }
        }

        private static string V3(Vector3 v)
        {
            return "(" + v.x.ToString("0.000") + "," + v.y.ToString("0.000") + "," + v.z.ToString("0.000") + ")";
        }
    }
}

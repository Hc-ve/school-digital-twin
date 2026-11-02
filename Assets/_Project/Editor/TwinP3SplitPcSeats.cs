using System.Collections.Generic;
using System.Reflection;
using System.Text;
using SchoolTwin.Twin;
using UnityEditor;
using UnityEngine;

namespace SchoolTwin.EditorTools
{
    /// <summary>
    /// 【P3】把机房「整间 18 台电脑」拆成 18 个独立点位。
    ///
    /// ── 为什么用 Monitor 做锚点，不用 Table ──────────────────────────
    /// 机房是长条桌，**一张桌子放 2~3 台机器**（主理人截图确认）。
    /// 按 Table 聚类会把两三台电脑并成一组，编号反而更乱。
    /// 显示器是"一台电脑一个"，位置也最稳定，所以用 `Monitor*` 当锚点。
    ///
    /// ── 为什么不按命名编号分组 ────────────────────────────────────
    /// 场景里的实例编号互不对应：`Monitor04 (4)` / `Mouse01 (6)` / `Table03 (7)`，
    /// Monitor 是 4 号、Mouse 是 6 号、Table 是 7 号 —— 没有共同的座位号。
    /// 所以只能**按空间距离聚类**：以每个 Monitor 为中心，就近归拢键鼠。
    ///
    /// ── 分两步，中间人工核对 ──────────────────────────────────────
    /// ① 扫描：只输出清单（名字 / 位置 / 距离），不动场景
    /// ② 生成：按清单建点位（TwinId = `ClassComputer/PC/Seat_01..18`）
    ///
    /// ⚠️ 生成前务必先看 ① 的清单。聚类一旦分错，
    ///    "点 5 号座位亮了 7 号"在演示时很难解释。
    /// </summary>
    public static class TwinP3SplitPcSeats
    {
        /// <summary>机房判定半径（米）。机房约 10×8m，取 12m 留余量。</summary>
        private const float RoomRadius = 12f;

        /// <summary>座位间距下限（米）。两个 Monitor 近于此值说明识别重复。</summary>
        private const float MinSeatGap = 0.6f;

        private const string ZoneId = "ClassComputer";
        private const string NewIdPrefix = "ClassComputer/PC/Seat_";

        // ================================================================ ① 扫描

        [MenuItem("SchoolTwin/P3/拆机房电脑/① 扫描 Monitor", false, 20)]
        public static void Scan()
        {
            List<Transform> monitors = FindMonitors(out Vector3 center, out int rejected);

            StringBuilder sb = new StringBuilder(2048);
            sb.AppendLine("===== 机房电脑座位扫描 =====");
            sb.AppendLine("机房中心（XZ）：(" + center.x.ToString("0.0") + ", " + center.z.ToString("0.0") +
                          ")  半径 " + RoomRadius + "m");
            sb.AppendLine("说明：判定只算水平距离，Y 轴不参与（点位分布在天花板/地面，Y 无意义）");
            sb.AppendLine("Monitor：" + monitors.Count + " 个" +
                          (rejected > 0 ? "（半径外排除 " + rejected + " 个）" : ""));
            sb.AppendLine();

            for (int i = 0; i < monitors.Count; i++)
            {
                Transform t = monitors[i];
                float d = FlatDistance(t.position, center);
                sb.AppendLine("  [" + (i + 1).ToString("00") + "] " + t.name +
                              "  @ " + t.position.ToString("0.0") + "  水平距 " + d.ToString("0.0") + "m");
            }

            sb.AppendLine();
            sb.AppendLine(monitors.Count == 0
                ? "没找到 Monitor —— 检查对象命名是否以 Monitor 开头。"
                : "确认数量与场景一致后，执行「② 生成点位」。");

            Debug.Log(sb.ToString());
        }

        // ================================================================ ② 生成

        [MenuItem("SchoolTwin/P3/拆机房电脑/② 生成点位", false, 21)]
        public static void Generate()
        {
            List<Transform> monitors = FindMonitors(out Vector3 center, out int rejected);
            if (monitors.Count == 0)
            {
                Debug.LogError("[P3拆分] 没找到 Monitor，先跑「① 扫描」看清单。");
                return;
            }

            // 【排除主脑】19 台里有一台是主脑，已有 `ClassComputer/PC/Master_01` 点位。
            // 再给它编一个 Seat_XX 就是重复编号（同一台机器两个 ID），
            // 而且控制范围/表现层会打架。这里按"离 Master_01 最近"识别并剔除。
            Transform master = FindMasterMonitor(monitors, out float masterDist);
            if (master != null)
            {
                monitors.Remove(master);
                Debug.Log("[P3拆分] 识别到主脑：" + master.name + "（距 Master_01 点位 " +
                          masterDist.ToString("0.0") + "m），已从座位列表剔除");
            }
            else
            {
                Debug.LogWarning("[P3拆分] 没找到 Master_01 点位，19 台全部当座位处理 —— 主脑会被重复编号，请检查");
            }

            TwinCatalog catalog = FindCatalog();
            if (catalog == null)
            {
                Debug.LogError("[P3拆分] 场景里找不到 TwinCatalog");
                return;
            }

            bool ok = EditorUtility.DisplayDialog("拆机房电脑",
                "将为 " + monitors.Count + " 台学生机建独立点位：\n\n" +
                "TwinId：ClassComputer/PC/Seat_01 … Seat_" + monitors.Count.ToString("00") + "\n" +
                "量程：单台 0~300W（办公机 150~250W）\n\n" +
                (master != null ? "已排除主脑：" + master.name + "（保留原 Master_01）\n\n" : "") +
                "原有 `ClassComputer/PC/Room_01`（聚合）**保留不动**。\n\n" +
                "⚠️ 注意：Room_01 与 Seat_XX 指向同一批设备，\n" +
                "按设备累加功率的统计口径会重复计算。\n\n" +
                "此操作会修改场景与 TwinCatalog，可 Ctrl+Z 撤销。", "生成", "取消");
            if (!ok) return;

            int created = 0;
            StringBuilder log = new StringBuilder(1024);

            for (int i = 0; i < monitors.Count; i++)
            {
                string twinId = NewIdPrefix + (i + 1).ToString("00");

                // 已存在就跳过，不重复建（可反复执行）
                if (catalog.Find(twinId) != null)
                {
                    log.AppendLine("  跳过（已存在）" + twinId);
                    continue;
                }

                Transform m = monitors[i];

                // ---- 1. 建 catalog 点位 ----
                TwinPointDefinition point = new TwinPointDefinition();
                point.TwinId = twinId;
                point.DisplayName = "机房 " + (i + 1).ToString("00") + " 号机";
                point.Zone = ZoneId;
                point.Category = "PC";
                point.Metrics = new List<TwinMetricDefinition>();
                point.Metrics.Add(Metric("Power", "W", 0f, 300f, 0f, 260f, 0f, 290f));
                point.Metrics.Add(Metric("OnCount", "台", 0f, 1f, 0f, 1f, 0f, 1f));
                point.Metrics.Add(Metric("CPULoad", "%", 0f, 100f, 0f, 85f, 0f, 98f));

                Undo.RecordObject(catalog, "拆机房电脑");
                catalog.Points.Add(point);

                // ---- 2. 建点位：**直接挂在 Monitor 模型上，不建标记牌** ----
                //
                // 【为什么不像其他点位那样建 TwinPointMarker（2026-11-01 主理人反馈）】
                // 标记牌是给"门牌 / 天花板灯"这类**没有可点几何**的设备用的：
                // 一块牌子贴在墙上，既当视觉标签又当点击热区。
                //
                // 但电脑有完整模型，再插一块牌子就是多余 —— 18 块黑牌子立在桌面上，
                // 把机房拍得像工地。而且牌子会挡住显示器正面。
                //
                // 所以这里只在**显示器本体**上挂 TwinAnchor + BoxCollider：
                // 点显示器 = 选中这台电脑，选中后 TwinPicker 会画线框，视觉反馈不缺。
                TwinAnchor anchor = m.gameObject.GetComponent<TwinAnchor>();
                if (anchor == null)
                {
                    Undo.RecordObject(m.gameObject, "拆机房电脑");
                    anchor = m.gameObject.AddComponent<TwinAnchor>();
                }
                SetString(anchor, "_twinId", twinId);
                SetString(anchor, "_displayName", point.DisplayName);
                SetString(anchor, "_zone", ZoneId);
                SetString(anchor, "_category", "PC");

                // 碰撞体：模型本身可能没有（纯渲染对象），没有就补一个包围盒
                BoxCollider box = m.gameObject.GetComponent<BoxCollider>();
                if (box == null)
                {
                    Renderer r = m.GetComponentInChildren<Renderer>(true);
                    Undo.RecordObject(m.gameObject, "拆机房电脑");
                    box = m.gameObject.AddComponent<BoxCollider>();

                    if (r != null)
                    {
                        Bounds b = r.bounds;
                        box.center = m.transform.InverseTransformPoint(b.center);
                        Vector3 size = b.size;
                        Vector3 lossy = m.lossyScale;
                        box.size = new Vector3(
                            size.x / Mathf.Max(0.0001f, lossy.x),
                            size.y / Mathf.Max(0.0001f, lossy.y),
                            size.z / Mathf.Max(0.0001f, lossy.z));
                    }
                    else
                    {
                        box.size = new Vector3(0.55f, 0.35f, 0.1f);
                    }
                }

                created++;
                log.AppendLine("  ✔ " + twinId + "  → " + m.name + " @ " + m.position.ToString("0.0"));
            }

            catalog.BuildIndex();
            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();

            Debug.Log("[P3拆分] 新建 " + created + " 个座位点位（Monitor " + monitors.Count +
                      " 个，半径外排除 " + rejected + " 个）\n" + log);
        }

        // ================================================================ ③ 清理

        /// <summary>
        /// 撤销上一版建的东西：删场景里的标记牌 + 删 catalog 里的 Seat_* 点位。
        ///
        /// 【为什么需要】第一版把点位建成了"标记牌"（`TwinMarker_...` 对象），
        /// 18 块黑牌子插在显示器上，主理人截图反馈"机房像工地"。
        /// 改成挂在显示器本体之后，必须先清掉旧的牌子，否则会同时存在两套。
        /// </summary>
        [MenuItem("SchoolTwin/P3/拆机房电脑/③ 清理已建的牌子", false, 22)]
        public static void CleanMarkers()
        {
            TwinCatalog catalog = FindCatalog();
            if (catalog == null)
            {
                Debug.LogError("[P3清理] 场景里找不到 TwinCatalog");
                return;
            }

            bool ok = EditorUtility.DisplayDialog("清理已建的牌子",
                "将删除：\n" +
                "· 场景里所有 `TwinMarker_ClassComputer_PC_Seat_*` 对象\n" +
                "· TwinCatalog 里所有 `ClassComputer/PC/Seat_*` 点位\n\n" +
                "清理后可以重新执行「② 生成点位」，\n" +
                "新版会把点位挂在显示器本体上，不再建牌子。\n\n" +
                "此操作可 Ctrl+Z 撤销。", "清理", "取消");
            if (!ok) return;

            int removedObj = 0;

            // ---- 1. 删场景里的牌子对象 ----
            Transform[] all = Object.FindObjectsOfType<Transform>(true);
            for (int i = all.Length - 1; i >= 0; i--)
            {
                Transform t = all[i];
                if (t == null) continue;
                if (!t.name.StartsWith("TwinMarker_ClassComputer_PC_Seat_", System.StringComparison.Ordinal))
                    continue;

                Undo.DestroyObjectImmediate(t.gameObject);
                removedObj++;
            }

            // ---- 2. 删 catalog 里的 Seat_* 点位 ----
            int removedPoint = 0;
            for (int i = catalog.Points.Count - 1; i >= 0; i--)
            {
                TwinPointDefinition p = catalog.Points[i];
                if (p == null || string.IsNullOrEmpty(p.TwinId)) continue;
                if (!p.TwinId.StartsWith(NewIdPrefix, System.StringComparison.Ordinal)) continue;

                Undo.RecordObject(catalog, "清理 Seat 点位");
                catalog.Points.RemoveAt(i);
                removedPoint++;
            }

            // ---- 3. 顺手把 Monitor 上残留的 TwinAnchor 摘掉 ----
            int removedAnchor = 0;
            Transform[] all2 = Object.FindObjectsOfType<Transform>(true);
            for (int i = 0; i < all2.Length; i++)
            {
                Transform t = all2[i];
                if (t == null) continue;
                if (!t.name.StartsWith("Monitor", System.StringComparison.OrdinalIgnoreCase)) continue;

                TwinAnchor a = t.GetComponent<TwinAnchor>();
                if (a == null) continue;
                if (string.IsNullOrEmpty(a.TwinId) ||
                    !a.TwinId.StartsWith(NewIdPrefix, System.StringComparison.Ordinal)) continue;

                Undo.DestroyObjectImmediate(a);
                removedAnchor++;
            }

            catalog.BuildIndex();
            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();

            Debug.Log("[P3清理] 删除牌子 " + removedObj + " 个 / 点位 " + removedPoint +
                      " 条 / 显示器上的残留 TwinAnchor " + removedAnchor + " 个。\n" +
                      "现在可以重新执行「② 生成点位」。");
        }

        // ================================================================ 扫描实现

        /// <summary>
        /// 找机房范围内的 Monitor。
        ///
        /// 判定三条：
        ///   ① 对象名以 `Monitor` 开头（大小写不敏感）
        ///   ② **水平距离**（只算 XZ）&lt; RoomRadius
        ///   ③ 带 MeshRenderer（排除空壳）
        ///
        /// ── 为什么用水平距离而不是三维距离（2026-11-01 实测踩到）──────
        /// 第一次跑扫描，机房中心算出来是 `(-16.2, 12.2, 38.0)` —— Y=12.2，
        /// 而机房是 1F、层高 3.47m，设备都在 1~3m。
        /// 中心被异常点位（天花板灯之类）拉高后，地面上的 Monitor（Y≈0.8）
        /// 与中心的**三维距离**直接超过 12m 半径，一台都扫不到。
        ///
        /// "在不在同一间房"本来就是平面问题，Y 轴只会带来噪声。
        ///
        /// 不用"父级链里有 ClassComputer"判，因为场景里这些模型是平铺的，
        /// 没有稳定的分区父节点 —— 平面位置是唯一可靠依据。
        /// </summary>
        private static List<Transform> FindMonitors(out Vector3 center, out int rejected)
        {
            center = RoomCenter();
            rejected = 0;

            List<Transform> result = new List<Transform>(24);

            Transform[] all = Object.FindObjectsOfType<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                Transform t = all[i];
                if (t == null) continue;
                if (!t.name.StartsWith("Monitor", System.StringComparison.OrdinalIgnoreCase)) continue;
                if (t.GetComponent<MeshRenderer>() == null && t.GetComponentInChildren<MeshRenderer>(true) == null)
                    continue;

                if (FlatDistance(t.position, center) > RoomRadius) { rejected++; continue; }

                result.Add(t);
            }

            // 按位置排序：先 Z 后 X，保证 Seat_01..18 的顺序在场景里是**从前往后、从左到右**，
            // 不是按 FindObjectsOfType 的随机顺序 —— 否则编号和实际座位对不上。
            result.Sort(delegate (Transform a, Transform b)
            {
                int z = a.position.z.CompareTo(b.position.z);
                return z != 0 ? z : a.position.x.CompareTo(b.position.x);
            });

            // 去掉过近的重复识别
            List<Transform> dedup = new List<Transform>(result.Count);
            for (int i = 0; i < result.Count; i++)
            {
                bool tooClose = false;
                for (int j = 0; j < dedup.Count; j++)
                {
                    if (Vector3.Distance(result[i].position, dedup[j].position) < MinSeatGap)
                    {
                        tooClose = true;
                        break;
                    }
                }
                if (!tooClose) dedup.Add(result[i]);
            }

            return dedup;
        }

        /// <summary>机房中心 = ClassComputer 分区所有点位的平均位置（**只取 XZ**）。</summary>
        private static Vector3 RoomCenter()
        {
            TwinAnchor[] anchors = Object.FindObjectsOfType<TwinAnchor>(true);
            Vector3 sum = Vector3.zero;
            int n = 0;

            for (int i = 0; i < anchors.Length; i++)
            {
                TwinAnchor a = anchors[i];
                if (a == null || string.IsNullOrEmpty(a.TwinId)) continue;
                if (!a.TwinId.StartsWith(ZoneId + "/", System.StringComparison.Ordinal)) continue;
                sum += a.transform.position;
                n++;
            }

            if (n > 0)
            {
                Vector3 avg = sum / n;
                // Y 强制归零：点位分布在天花板/地面/墙面，Y 的平均值没有物理意义
                // （实测算出过 12.2，而层高只有 3.47）。判定只用 XZ，这里直接抹掉 Y 避免误导。
                avg.y = 0f;
                return avg;
            }

            Debug.LogWarning("[P3拆分] 没找到 ClassComputer 分区点位，机房中心退化为世界原点");
            return Vector3.zero;
        }

        /// <summary>水平距离（只算 XZ）。</summary>
        private static float FlatDistance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>
        /// 从 Monitor 列表里找出主脑那台 —— 离 `ClassComputer/PC/Master_01` 点位最近的一台。
        ///
        /// 【为什么按距离认，不按名字认】19 个 Monitor 的实例编号（4/2/3/10/8…）
        /// 与座位号没有对应关系，名字里看不出哪台是教师机。
        /// 但 `Master_01` 点位本身在场景里是有坐标的，它必然贴着主脑那台显示器 ——
        /// 所以"最近的一台"就是它，这是唯一可靠依据。
        ///
        /// 距离上限 3m：超过说明 `Master_01` 点位没放在显示器附近（配置问题），
        /// 这时返回 null，由调用方给出警告，而不是瞎认一台。
        /// </summary>
        private static Transform FindMasterMonitor(List<Transform> monitors, out float bestDist)
        {
            bestDist = float.MaxValue;

            Vector3 masterPos = Vector3.zero;
            bool found = false;

            TwinAnchor[] anchors = Object.FindObjectsOfType<TwinAnchor>(true);
            for (int i = 0; i < anchors.Length; i++)
            {
                TwinAnchor a = anchors[i];
                if (a == null || string.IsNullOrEmpty(a.TwinId)) continue;
                if (!string.Equals(a.TwinId, "ClassComputer/PC/Master_01", System.StringComparison.Ordinal))
                    continue;
                masterPos = a.transform.position;
                found = true;
                break;
            }

            if (!found) return null;

            Transform best = null;
            for (int i = 0; i < monitors.Count; i++)
            {
                float d = FlatDistance(monitors[i].position, masterPos);
                if (d < bestDist) { bestDist = d; best = monitors[i]; }
            }

            // 超过 3m 认为不是同一台（点位可能挂歪了），不硬认
            if (best != null && bestDist > 3f) return null;

            return best;
        }

        // ================================================================ 辅助

        private static TwinMetricDefinition Metric(string name, string unit, float min, float max,
                                                   float warnLow, float warnHigh,
                                                   float alarmLow, float alarmHigh)
        {
            TwinMetricDefinition m = new TwinMetricDefinition();
            m.Metric = name;
            m.Unit = unit;
            m.Min = min; m.Max = max;
            m.WarnLow = warnLow; m.WarnHigh = warnHigh;
            m.AlarmLow = alarmLow; m.AlarmHigh = alarmHigh;
            return m;
        }

        private static TwinCatalog FindCatalog()
        {
            TwinRuntime runtime = Object.FindObjectOfType<TwinRuntime>();
            if (runtime != null && runtime.Catalog != null) return runtime.Catalog;

            string[] guids = AssetDatabase.FindAssets("t:TwinCatalog");
            if (guids != null && guids.Length > 0)
                return AssetDatabase.LoadAssetAtPath<TwinCatalog>(AssetDatabase.GUIDToAssetPath(guids[0]));
            return null;
        }

        /// <summary>私有序列化字段用反射写 —— 与 TwinPlaceDevices.WriteIdentity 同一套做法。</summary>
        private static void SetString(Object target, string field, string value)
        {
            FieldInfo f = target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic);
            if (f != null) f.SetValue(target, value);
        }

        private static void SetFloat(Object target, string field, float value)
        {
            FieldInfo f = target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic);
            if (f != null) f.SetValue(target, value);
        }

        private static void SetBool(Object target, string field, bool value)
        {
            FieldInfo f = target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic);
            if (f != null) f.SetValue(target, value);
        }
    }
}

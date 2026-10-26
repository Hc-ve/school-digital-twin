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
    /// 【P24】把「场景里有锚点、但点位目录里没有」的点位补进 `TwinCatalog_Mock.asset`。
    ///
    /// ── 为什么需要这个工具 ────────────────────────────────────────────
    /// `MockDataSource.CollectPoints()` **不是实时扫场景**，它读的是
    /// `Assets/_Project/ScriptableObjects/TwinCatalog_Mock.asset` 里序列化的点位表。
    ///
    /// 而 `P1 ① 生成 Mock 闭环` 只会**新建**目录：
    /// ```csharp
    /// if (catalog.Count == 0) PopulateCatalog(catalog);   // 非空直接返回，不重扫
    /// ```
    /// `PopulateCatalog` 里还有 `if (catalog.Points.Count > 0) return;`。
    ///
    /// 于是后补的锚点（2026-10-26 给礼堂 / 体育馆三间补的 4 盏灯）**永远进不了目录**，
    /// 表现是：
    ///   · 三维里点得到、交互面板也弹得出来
    ///   · 点「开灯」→ 命令下发 → Mock 收到 → **但数据源从不给这个点位发样本**
    ///   · 命令中心等回读超时 → `_inFlight` 一直不清 → 之后每次点击都「上一条命令还没结束」
    /// 用户实测：老房间的灯都正常，只有这 4 间不行。
    ///
    /// ── 用法 ────────────────────────────────────────────────────────
    /// 菜单 `P24 灯锚点 → ② 同步点位目录（补缺失）`（幂等，已有的点位不动）
    /// </summary>
    public static class TwinP24CatalogSync
    {
        private const string CatalogPath = "Assets/_Project/ScriptableObjects/TwinCatalog_Mock.asset";

        [MenuItem("P24 灯锚点/② 同步点位目录（补缺失）", false, 20)]
        public static void SyncMenu()
        {
            if (EditorApplication.isPlaying)
            {
                EditorUtility.DisplayDialog("已拒绝",
                    "现在是 Play 模式。请先按停止 —— 目录改动要在编辑模式做。", "好");
                return;
            }
            string report = Sync(false);
            Debug.Log(report);
            EditorUtility.DisplayDialog("P24 同步点位目录", report, "好");
        }

        [MenuItem("P24 灯锚点/③ 只检查（不改文件）", false, 21)]
        public static void CheckMenu()
        {
            string report = Sync(true);
            Debug.Log(report);
            EditorUtility.DisplayDialog("P24 检查点位目录", report, "好");
        }

        private static string Sync(bool dryRun)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[P24] ===== 同步点位目录 =====");

            TwinCatalog catalog = AssetDatabase.LoadAssetAtPath<TwinCatalog>(CatalogPath);
            if (catalog == null)
            {
                sb.AppendLine("[P24] ✘ 找不到目录资产：" + CatalogPath);
                return sb.ToString();
            }
            sb.AppendLine("[P24] 目录现有 " + catalog.Count + " 个点位");

            HashSet<string> inCatalog = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < catalog.Points.Count; i++)
            {
                if (catalog.Points[i] != null && !string.IsNullOrEmpty(catalog.Points[i].TwinId))
                    inCatalog.Add(catalog.Points[i].TwinId);
            }

            TwinAnchor[] all = UnityEngine.Object.FindObjectsOfType<TwinAnchor>(true);
            int added = 0, present = 0, notWhitelisted = 0, unsupported = 0;

            for (int i = 0; i < all.Length; i++)
            {
                TwinAnchor a = all[i];
                if (a == null || string.IsNullOrEmpty(a.TwinId)) continue;
                if (!TwinControlScope.IsEnabled(a.TwinId)) { notWhitelisted++; continue; }
                if (inCatalog.Contains(a.TwinId)) { present++; continue; }

                TwinPointDefinition def = BuildPoint(a);
                if (def == null)
                {
                    sb.AppendLine("[P24] ⚠ " + a.TwinId + " 分类「" + a.Category + "」没有预设指标，跳过");
                    unsupported++;
                    continue;
                }

                sb.AppendLine("[P24] " + (dryRun ? "· 待补 " : "✔ 补入 ") + a.TwinId +
                              "（" + a.Category + "，" + def.Metrics.Count + " 个指标）");
                if (!dryRun)
                {
                    catalog.Points.Add(def);
                    inCatalog.Add(a.TwinId);
                }
                added++;
            }

            sb.AppendLine();
            sb.AppendLine("[P24] 已在目录 " + present + "，缺 " + added +
                          "，非白名单 " + notWhitelisted + "，分类不支持 " + unsupported);

            if (!dryRun && added > 0)
            {
                catalog.BuildIndex();
                EditorUtility.SetDirty(catalog);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                sb.AppendLine("[P24] 目录已保存，现有 " + catalog.Count + " 个点位");
            }
            else if (dryRun)
            {
                sb.AppendLine("[P24] 只检查模式，未写入任何文件");
            }

            sb.AppendLine("[P24] ===== 结束 =====");
            try { System.IO.File.WriteAllText("p24_catalog.txt", sb.ToString(), Encoding.UTF8); }
            catch (Exception) { }
            return sb.ToString();
        }

        /// <summary>按分类给出和现有目录一致的指标定义。只覆盖当前用得到的几类。</summary>
        private static TwinPointDefinition BuildPoint(TwinAnchor a)
        {
            TwinPointDefinition p = new TwinPointDefinition();
            p.TwinId = a.TwinId;
            p.DisplayName = string.IsNullOrEmpty(a.DisplayName) ? a.TwinId : a.DisplayName;
            p.Zone = a.Zone;
            p.Category = a.Category;
            p.Metrics = new List<TwinMetricDefinition>();

            string cat = (a.Category ?? "").Trim();
            if (string.Equals(cat, "Light", StringComparison.OrdinalIgnoreCase))
            {
                // 与 `PopulateCatalog` 里 2-1 班那盏完全一致
                p.Metrics.Add(Metric("Brightness", "%", 0f, 100f, 20f, 100f, 0f, 100f));
                p.Metrics.Add(Metric("Power", "W", 0f, 120f, 0f, 110f, 0f, 120f));
                return p;
            }
            if (string.Equals(cat, "Door", StringComparison.OrdinalIgnoreCase))
            {
                p.Metrics.Add(Metric("Open", "0/1", 0f, 1f, 0f, 1f, 0f, 1f));
                return p;
            }
            if (string.Equals(cat, "AC", StringComparison.OrdinalIgnoreCase))
            {
                p.Metrics.Add(Metric("SetTemp", "°C", 16f, 30f, 18f, 26f, 16f, 30f));
                p.Metrics.Add(Metric("Power", "kW", 0f, 3.5f, 0f, 3f, 0f, 3.5f));
                return p;
            }
            // 【P26】补 PC —— 之前漏了这一类，导致新加的 `ClassComputer/PC/Master_01`
            // 被"分类不支持"跳过，进不了目录 → 数据源从不给它发样本 →
            // 点「开机」命令发得出去、回读永远不变 → `Failed(执行了但状态没变)`。
            // 指标范围与 `ClassComputer/PC/Room_01` 保持一致。
            if (string.Equals(cat, "PC", StringComparison.OrdinalIgnoreCase))
            {
                p.Metrics.Add(Metric("Power", "W", 0f, 3000f, 0f, 2600f, 0f, 2900f));
                p.Metrics.Add(Metric("OnCount", "台", 0f, 30f, 0f, 30f, 0f, 30f));
                p.Metrics.Add(Metric("CPULoad", "%", 0f, 100f, 0f, 85f, 0f, 98f));
                return p;
            }
            if (string.Equals(cat, "Projector", StringComparison.OrdinalIgnoreCase))
            {
                p.Metrics.Add(Metric("Power", "W", 0f, 400f, 0f, 380f, 0f, 400f));
                p.Metrics.Add(Metric("LampHours", "h", 0f, 10000f, 0f, 9000f, 0f, 9800f));
                return p;
            }
            return null;
        }

        private static TwinMetricDefinition Metric(string name, string unit, float min, float max,
                                                   float warnLow, float warnHigh,
                                                   float alarmLow, float alarmHigh)
        {
            TwinMetricDefinition m = new TwinMetricDefinition();
            m.Metric = name;
            m.Unit = unit;
            m.Min = min;
            m.Max = max;
            m.WarnLow = warnLow;
            m.WarnHigh = warnHigh;
            m.AlarmLow = alarmLow;
            m.AlarmHigh = alarmHigh;
            return m;
        }
    }
}

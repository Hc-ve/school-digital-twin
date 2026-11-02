using System.Collections.Generic;
using System.Text;
using SchoolTwin.Twin;
using UnityEditor;
using UnityEngine;

namespace SchoolTwin.EditorTools
{
    /// <summary>
    /// 【P3】设备数据量程审计与修正 —— 按**现实基准**校验所有点位的指标量程。
    ///
    /// ── 为什么需要这个工具 ──────────────────────────────────────────
    /// 主理人 2026-11-01 核对数据时发现两处不现实：
    ///   · 「教师办公室 001 电脑」功率 892W / 2171W —— 单台办公机不可能到这个数；
    ///   · 「CO₂ 浓度 22 ppm」—— 大气本底就是 400+，22 是纯氮环境才有的读数。
    ///
    /// 这类错误不是代码逻辑错，是**配置数据错**（量程/阈值被填错或漏改）。
    /// 53 台设备 95 个指标，靠肉眼逐个核不现实，所以做成工具：
    /// 一次扫全量、按基准表比对、列出偏差、一键修正。
    ///
    /// ── 基准值从哪来 ────────────────────────────────────────────────
    /// 每条都对应现实设备参数，见下方 `Baseline` 表的注释。
    /// 有争议的（如灯功率）取"常见范围"，宁可宽一点也不要卡死。
    ///
    /// ── 用法 ────────────────────────────────────────────────────────
    /// 菜单 `SchoolTwin/P3/数据量程审计` → 看 Console 的问题清单
    /// 菜单 `SchoolTwin/P3/数据量程修正` → 按基准表改（会先弹确认框）
    /// </summary>
    public static class TwinP3DataAudit
    {
        /// <summary>一个指标的现实基准。</summary>
        private sealed class Baseline
        {
            public string Metric;
            public string Unit;
            public float Min, Max, WarnLow, WarnHigh, AlarmLow, AlarmHigh;
            public string Note;   // 依据，写进报告
        }

        /// <summary>
        /// 现实基准表。**每一条都有现实依据**，不是拍脑袋定的。
        /// </summary>
        private static Baseline[] BaselineOf(string category, string zone, string twinId)
        {
            switch (category)
            {
                case "Light":
                    // LED 教室面板灯：常见 20~40W，老式荧光灯管 36~72W。
                    // 量程给 0~120W 覆盖两者；正常区间 20~110W。
                    return new[]
                    {
                        B("Brightness", "%", 0f, 100f, 20f, 100f, 0f, 100f, "亮度百分比"),
                        B("Power", "W", 0f, 120f, 0f, 110f, 0f, 120f, "LED 面板灯 20~40W / 荧光灯管 36~72W"),
                    };

                case "AC":
                    // 教室空调：1.5 匹 ≈ 1.1kW，3 匹 ≈ 2.5kW，5 匹柜机 ≈ 4kW。
                    // 量程 3.5kW 覆盖到 3 匹；正常 0.5~3kW。
                    return new[]
                    {
                        B("SetTemp", "°C", 16f, 30f, 18f, 26f, 16f, 30f, "空调设定温度标准区间"),
                        B("Power", "kW", 0f, 3.5f, 0f, 3f, 0f, 3.5f, "1.5 匹 1.1kW / 3 匹 2.5kW"),
                    };

                case "Heat":
                    // 暖气：供水温度集中供暖 60~80°C（高温水可达 90）。
                    // 阀门开度 0~100%。
                    return new[]
                    {
                        B("ValveOpen", "%", 0f, 100f, 0f, 100f, 0f, 100f, "阀门开度百分比"),
                        B("SupplyTemp", "°C", 30f, 90f, 45f, 75f, 30f, 85f, "集中供暖供水 60~80°C"),
                    };

                case "PC":
                    // 【关键】PC 点位有三种语义，不能只看 Zone：
                    //   · ClassComputer/PC/Master_01  → 主脑 **1 台**（交互对象）
                    //   · ClassComputer/PC/Room_01    → 其余 **18 台**（联动时才开，整间聚合）
                    //   · ClassTeachers/PC/Room_01    → 教师办公室 **1 台**
                    //
                    // 第一版只判 `zone == "ClassComputer"`，结果把 Master_01 也当成整间，
                    // 给了 0~3600W —— 单台主脑不可能到 3.6kW（实测 Console 已暴露）。
                    // 现在加一条：**必须同时是机房分区 + TwinId 里带 `/PC/Room_`** 才算整间。
                    //
                    // 为什么不用 "Room" 字样单独判断：`ClassTeachers/PC/Room_01` 也带 Room，
                    // 但它是一台（模型是 Computer03，只有机箱+键鼠）。必须叠 Zone 条件。
                    bool wholeRoom =
                        string.Equals(zone, "ClassComputer", System.StringComparison.Ordinal) &&
                        twinId != null &&
                        twinId.IndexOf("/PC/Room_", System.StringComparison.Ordinal) >= 0;

                    if (wholeRoom)
                    {
                        return new[]
                        {
                            B("Power", "W", 0f, 3600f, 0f, 3200f, 0f, 3500f,
                              "机房整间 18 台 × 180W ≈ 3240W"),
                            B("OnCount", "台", 0f, 18f, 0f, 18f, 0f, 18f, "机房其余座位数"),
                            B("CPULoad", "%", 0f, 100f, 0f, 85f, 0f, 98f, "CPU 负载"),
                        };
                    }

                    // 单台办公机 / 主脑：待机 60~100W，办公 150~250W，满负荷 300W。
                    return new[]
                    {
                        B("Power", "W", 0f, 300f, 0f, 260f, 0f, 290f,
                          "单台办公机 待机 60~100W / 办公 150~250W / 满负荷 300W"),
                        B("OnCount", "台", 0f, 1f, 0f, 1f, 0f, 1f, "单台"),
                        B("CPULoad", "%", 0f, 100f, 0f, 85f, 0f, 98f, "CPU 负载"),
                    };

                case "Projector":
                    // 教室吊装投影：灯泡机 250~350W，激光机 300~500W。
                    return new[]
                    {
                        B("Power", "W", 0f, 500f, 0f, 450f, 0f, 480f, "灯泡机 300W / 激光机 400W"),
                        B("LampHours", "h", 0f, 10000f, 0f, 9000f, 0f, 9800f, "灯泡寿命 5000~10000h"),
                    };

                case "Door":
                    return new[]
                    {
                        B("Open", "0/1", 0f, 1f, 0f, 1f, 0f, 1f, "开/关二值"),
                    };

                case "Sensor":
                    // 传感器点位可能同时挂温湿度与 CO₂。
                    return new[]
                    {
                        B("Temperature", "°C", 10f, 40f, 16f, 28f, 10f, 32f, "室内温度"),
                        B("Humidity", "%RH", 20f, 80f, 30f, 65f, 20f, 75f, "室内相对湿度"),
                        B("CO2", "ppm", 400f, 2000f, 400f, 1000f, 400f, 1500f,
                          "室外本底 400+ / 室内有人 600~1000 / 通风不良 1500+"),
                    };

                case "Bell":
                    return new[] { B("Ring", "0/1", 0f, 1f, 0f, 1f, 0f, 1f, "响铃二值") };

                case "Alarm":
                    return new[] { B("Smoke", "%", 0f, 100f, 0f, 20f, 0f, 40f, "烟雾浓度") };

                default:
                    return null;
            }
        }

        private static Baseline B(string metric, string unit, float min, float max,
                                  float warnLow, float warnHigh, float alarmLow, float alarmHigh,
                                  string note)
        {
            Baseline b = new Baseline();
            b.Metric = metric;
            b.Unit = unit;
            b.Min = min; b.Max = max;
            b.WarnLow = warnLow; b.WarnHigh = warnHigh;
            b.AlarmLow = alarmLow; b.AlarmHigh = alarmHigh;
            b.Note = note;
            return b;
        }

        // ================================================================ 审计

        [MenuItem("SchoolTwin/P3/数据量程审计", false, 10)]
        public static void Audit()
        {
            TwinCatalog catalog = FindCatalog();
            if (catalog == null)
            {
                Debug.LogError("[P3审计] 场景里找不到 TwinCatalog");
                return;
            }

            StringBuilder sb = new StringBuilder(4096);
            sb.AppendLine("===== P3 数据量程审计 =====");
            sb.AppendLine("点位 " + catalog.Points.Count + " 个");
            sb.AppendLine();

            int problems = 0;

            for (int i = 0; i < catalog.Points.Count; i++)
            {
                TwinPointDefinition p = catalog.Points[i];
                if (p == null || p.Metrics == null) continue;

                Baseline[] baseList = BaselineOf(p.Category, p.Zone, p.TwinId);
                if (baseList == null)
                {
                    sb.AppendLine("[?] " + p.TwinId + "  类型 " + p.Category + " 无基准，跳过");
                    continue;
                }

                for (int m = 0; m < p.Metrics.Count; m++)
                {
                    TwinMetricDefinition d = p.Metrics[m];
                    Baseline b = FindBase(baseList, d.Metric);
                    if (b == null) continue;

                    string diff = DiffOf(d, b);
                    if (diff == null) continue;

                    problems++;
                    sb.AppendLine("[×] " + p.TwinId);
                    sb.AppendLine("    " + d.Metric + "(" + d.Unit + ")  " + diff);
                    sb.AppendLine("    依据：" + b.Note);
                }
            }

            sb.AppendLine();
            sb.AppendLine(problems == 0
                ? "全部符合现实基准。"
                : "共 " + problems + " 处偏差，执行「数据量程修正」可一键改齐。");

            Debug.Log(sb.ToString());
        }

        /// <summary>比对量程，返回差异描述；null = 无偏差。</summary>
        private static string DiffOf(TwinMetricDefinition d, Baseline b)
        {
            if (Mathf.Abs(d.Min - b.Min) < 0.01f &&
                Mathf.Abs(d.Max - b.Max) < 0.01f &&
                Mathf.Abs(d.WarnLow - b.WarnLow) < 0.01f &&
                Mathf.Abs(d.WarnHigh - b.WarnHigh) < 0.01f)
                return null;

            return "当前 [" + d.Min.ToString("0.#") + "~" + d.Max.ToString("0.#") +
                   " / 正常 " + d.WarnLow.ToString("0.#") + "~" + d.WarnHigh.ToString("0.#") + "]" +
                   "  应为 [" + b.Min.ToString("0.#") + "~" + b.Max.ToString("0.#") +
                   " / 正常 " + b.WarnLow.ToString("0.#") + "~" + b.WarnHigh.ToString("0.#") + "]";
        }

        // ================================================================ 修正

        [MenuItem("SchoolTwin/P3/数据量程修正", false, 11)]
        public static void Fix()
        {
            TwinCatalog catalog = FindCatalog();
            if (catalog == null)
            {
                Debug.LogError("[P3修正] 场景里找不到 TwinCatalog");
                return;
            }

            bool ok = EditorUtility.DisplayDialog("P3 数据量程修正",
                "将按现实基准修正所有偏差指标的量程与阈值。\n\n" +
                "重点修正：\n" +
                "· 教师办公室电脑 3000W → 300W（单台）\n" +
                "· CO₂ 量程 → 400~2000 ppm\n\n" +
                "此操作会修改 TwinCatalog 资产，可 Ctrl+Z 撤销。", "执行", "取消");
            if (!ok) return;

            int fixedCount = 0;

            for (int i = 0; i < catalog.Points.Count; i++)
            {
                TwinPointDefinition p = catalog.Points[i];
                if (p == null || p.Metrics == null) continue;

                Baseline[] baseList = BaselineOf(p.Category, p.Zone, p.TwinId);
                if (baseList == null) continue;

                for (int m = 0; m < p.Metrics.Count; m++)
                {
                    TwinMetricDefinition d = p.Metrics[m];
                    Baseline b = FindBase(baseList, d.Metric);
                    if (b == null) continue;
                    if (DiffOf(d, b) == null) continue;

                    Undo.RecordObject(catalog, "P3 数据量程修正");
                    d.Min = b.Min; d.Max = b.Max;
                    d.WarnLow = b.WarnLow; d.WarnHigh = b.WarnHigh;
                    d.AlarmLow = b.AlarmLow; d.AlarmHigh = b.AlarmHigh;
                    fixedCount++;

                    Debug.Log("[P3修正] " + p.TwinId + " " + d.Metric + " → " +
                              b.Min.ToString("0.#") + "~" + b.Max.ToString("0.#") +
                              "（" + b.Note + "）");
                }
            }

            catalog.BuildIndex();
            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();

            Debug.Log("[P3修正] 完成，共修正 " + fixedCount + " 处。请重新 Play 验证数据。");
        }

        // ================================================================ 辅助

        private static Baseline FindBase(Baseline[] list, string metric)
        {
            for (int i = 0; i < list.Length; i++)
                if (string.Equals(list[i].Metric, metric, System.StringComparison.Ordinal))
                    return list[i];
            return null;
        }

        private static TwinCatalog FindCatalog()
        {
            TwinRuntime runtime = Object.FindObjectOfType<TwinRuntime>();
            if (runtime != null && runtime.Catalog != null) return runtime.Catalog;

#if UNITY_2022_2_OR_NEWER
            string[] guids = AssetDatabase.FindAssets("t:TwinCatalog");
            if (guids != null && guids.Length > 0)
                return AssetDatabase.LoadAssetAtPath<TwinCatalog>(AssetDatabase.GUIDToAssetPath(guids[0]));
#endif
            return null;
        }
    }
}

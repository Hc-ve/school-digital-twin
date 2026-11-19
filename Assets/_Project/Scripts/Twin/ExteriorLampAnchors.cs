using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;
using SchoolTwin.Data;

namespace SchoolTwin.Twin
{
    /// <summary>
    /// 【P44 · 阶段 B】室外路灯一体化自举：锚点 + 数据 + 夜间发光。
    ///
    /// 三个职责，一个入口（AfterSceneLoad），零场景手工改动：
    ///
    /// ══════════════════════════════════════════════════════════════════
    /// ① 锚点落地（P44 裁决 2/3/4）
    ///
    /// 把 DayLightsExterior 下 100 盏 RoadLamp 做成测点锚点
    /// （Exterior/Light/RoadLamp_001~100，一灯一锚点）。
    /// 编号靠「名称 + 坐标」与交接表（TableLines，断点文档 §① 逐行转录）
    /// 双向匹配：69 盏 RoadLamp2Off 同名副本只能靠坐标区分。
    /// 匹配失败逐条 LogWarning，绝不静默跳过 —— 静默的锚点缺失到答辩那天
    /// 才会被发现，那是最坏的暴露时机。
    /// TwinAnchor.Configure() 由本类驱动（主理人授权新增的公开配置入口），
    /// 反射写私有字段被项目铁律禁止。
    ///
    /// ══════════════════════════════════════════════════════════════════
    /// ② 数据侧（主理人 2026-11-17 裁决）
    ///
    /// 目录里给每盏灯配了 Power / LampHours / Status 三个指标，但
    /// `MockDeviceAgent.SeedInitialState` 只播 TwinControlScope 白名单 + 机房座位，
    /// **不会覆盖 Exterior 点位** —— 不钉住的话它们会被 MockSignalGenerator
    /// 接管：Power 在 13~97W 之间跑正弦（面板上 100 盏路灯"常亮"），
    /// LampHours 在 1080~8920h 之间乱跳（累计量来回倒退，物理上不成立）。
    /// 这正是 P26/P27 记载过的"没有设备负责的状态一律是假的"。
    ///
    /// 所以挂载的同时钉两类 override（走 `MockDataSource.SetOverride`，
    /// 与设备播种同一数据出口，优先级高于波形）：
    ///   · LampHours = 1240h（与投影仪同口径，累计量保留不归零）；
    ///   · Power 开灯窗口：**19:00–07:00 亮 42W，其余时段灭 0W**（主理人
    ///     原始诉求原话："路灯只在晚上7.00-早上7.00之间亮"）。时刻读
    ///     `TwinClimate.HourOfDay`（真实时钟与表盘覆盖都在这里收口）。
    ///     刻意不借用 `TwinWeatherBridge.ToTimeOfDay` 的 17 点 Dusk 段 ——
    ///     那是给天色渐变用的视觉分段，主理人给路灯的开关时刻就是 19/7，
    ///     两件事不该硬凑一张表。
    ///   · **电力系统可控**：Exterior 回路由 `TwinCircuit.RoomOf` 对未登记分区
    ///     现场建节点（Circuit-Exterior，挂在楼栋下），电力页拉闸 → 全体路灯
    ///     判断电 → 数据 0W 且视觉熄灭，且 TwinPowerController 会按 Registry
    ///     全表 SetMuted（与房间断电同一链路，不是路灯特例）。
    ///
    /// ══════════════════════════════════════════════════════════════════
    /// ③ 视觉侧 · 夜间发光（主理人 2026-11-17 三项裁决）
    ///
    /// 现状：100 盏全是"死几何"（0 个 Light 组件、玻璃不自发光），
    /// 夜里路灯就是一根黑杆 —— 主理人诉求"仿真真实路灯晚上的发光效果"。
    ///
    /// 调研结论（真实路灯夜间特征 → 映射到实现）：
    ///   · 色温：LED 4000K 中性白是现代校园主流 → 保持美术 `GlassWhiteIllum`
    ///     的纯白观感（主理人裁决：不改色温）；
    ///   · 光分布：截光型向下锥形 → Spot 灯口朝下（角度/射程抄美术夜间变体）；
    ///   · 远近层次：远处灯=发光亮点，近处灯=照亮路面光池 → 真实 Light 只开
    ///     离主相机最近 N 盏（主理人裁决 N=14 + Soft 阴影），其余只留灯头
    ///     自发光 —— 这正是真实夜路的观感，也是性能预算的唯一活口。
    ///
    /// 实现（零 prefab 替换、零 .mat 落盘、不碰 P36/P39 室内参数与 Bloom）：
    ///   · 玻璃识别：扫每盏灯的 renderer 材质槽，槽位材质名 GlassWhite /
    ///     GlassLamp1 即灯头玻璃（2Off=独立 LampGlass 节点、1a=独立节点、
    ///     1d=根 renderer 槽 2，三种结构统一按"材质名"找）；
    ///   · 夜档：玻璃槽位运行时换成自发光实例（复制原材质 + _EMISSION 白光，
    ///     与美术夜间材质同参数），灯头加 Light。角度按现实有效亮半径
    ///     （杆高×1.5~1.8，间距=杆高×3~3.5 的行业经验公式）反推：
    ///     2Off→Spot 120° r9 I1.5、1a→Spot 115° r30 I2、1d→Spot 120° r35 I2.8；
    ///     射程/强度沿用美术夜间变体（2Off 由 Point 全向截光化，影子裁决 2026-11-18）。
    ///   · 日档：材质还原、Light 全灭；OnDestroy 全部还原清理。
    /// ⚠️ 灯光效果只在运行时存在，退出 Play 自动消失 —— 与 IndoorFillLight
    ///    同一套"运行时态不落盘"的纪律。
    /// </summary>
    public static class ExteriorLampAnchors
    {
        // ================================================================ 常量

        /// <summary>路灯容器（SchoolScene 根下）。交接 §①：100 盏全是它的直接子节点。</summary>
        private const string ContainerName = "DayLightsExterior";

        /// <summary>TwinId 前缀。Zone=Exterior、Category=Light（主理人裁决 1：Light 有现成设备模型，Lamp 走 default 空壳）。</summary>
        private const string IdPrefix = "Exterior/Light/RoadLamp_";

        /// <summary>路灯所在分区。回路 id = "Circuit-" + 本值，与 TwinDeviceModel.PoweredBy 同口径。</summary>
        private const string ExteriorZone = "Exterior";

        /// <summary>匹配容差（米）。表里坐标保留两位小数，0.05 足以吸收舍入误差、又远小于路灯间距。</summary>
        private const float MatchToleranceSq = 0.05f * 0.05f;

        /// <summary>LampHours 钉值。与投影仪播种同口径（MockDeviceAgent:649 "灯泡已用 1240 小时"）。</summary>
        private const float LampHoursSeed = 1240f;

        /// <summary>开灯功率（W）。交接 §4.3：与室内灯管同口径（量程 0~120、警告线 110）。</summary>
        private const float PowerOnWatt = 42f;

        /// <summary>开灯窗口边界（时）。主理人原话"晚上7.00-早上7.00之间亮" → [19:00, 次日07:00) 亮。</summary>
        private const double OnFromHour = 19.0;
        private const double OnToHour = 7.0;

        /// <summary>昼夜轮询间隔（秒）。0.5s 足够跟上表盘拖动与电力拉闸。</summary>
        private const float LinkPollSeconds = 0.5f;

        // ---- 视觉侧常量（主理人 2026-11-17 三项裁决）----

        /// <summary>真光源预算：只开离主相机最近的 N 盏，其余靠灯头自发光。</summary>
        private const int LightBudget = 14;

        /// <summary>灯头玻璃材质名（资产实测：2Off=GlassWhite，1a/1d=GlassLamp1）。</summary>
        private const string GlassMatName1 = "GlassWhite";
        private const string GlassMatName2 = "GlassLamp1";

        /// <summary>灯头自发光强度。【P50 2026-11-19】主理人 play 实测 6.0 刺眼（糊成大白团），
        /// 回调到 3.0：仍 >1 冲过夜档 Bloom 阈值保留光晕，但观感柔和、贴近参考图"亮白点+柔光晕"。
        /// 改的是共享材质 emission，100 盏零额外光源、零 FPS 成本。Standard shader 支持 HDR >1。</summary>
        private const float GlowEmissionIntensity = 3.0f;

        /// <summary>【P50】灯球色温：暖白 LED（约 3200K）的线性 RGB 比例。
        /// 原 (1,1,1) 是 6500K 冷白，参考图是暖白 —— R 保留 1.0，G 压到 0.88，B 压到 0.72。
        /// 与 GlowEmissionIntensity 相乘得到最终 emission=(3.0, 2.64, 2.16)。</summary>
        private static readonly Color GlowWarmTint = new Color(1.0f, 0.88f, 0.72f, 1f);

        /// <summary>根节点名。带方括号是本工程"系统级常驻节点"命名惯例（TwinWeatherBootstrap 同款）。</summary>
        private const string RootName = "[ExteriorLampAnchors]";
        private const string WatcherName = "[ExteriorLampAnchors.Watcher]";

        // ================================================================ 交接表（P44 断点文档 §①，100 条逐行转录）
        //
        // 格式："编号|场景对象名|x|y|z"。69 盏 RoadLamp2Off 同名，位置是唯一区分键，
        // 所以坐标逐条照抄文档，一个数都不能手抖 —— 抄错一盏，那盏就匹配不上并报警告。

        private static readonly string[] TableLines =
        {
            "001|RoadLamp2Off|1.94|-0.50|12.56",
            "002|RoadLamp2Off|-68.41|-0.50|10.89",
            "003|RoadLamp2Off|-10.69|-0.50|16.90",
            "004|RoadLamp1d (6)|21.50|-0.50|103.32",
            "005|RoadLamp2Off|-58.20|-0.50|5.96",
            "006|RoadLamp2Off|-33.94|-0.50|-17.62",
            "007|RoadLamp2Off|-40.30|-0.50|43.70",
            "008|RoadLamp2Off|31.09|-0.50|19.42",
            "009|RoadLamp2Off|-10.69|-0.50|25.25",
            "010|RoadLamp2Off|1.92|-0.50|21.77",
            "011|RoadLamp1a|-50.61|-0.50|-39.53",
            "012|RoadLamp2Off|-16.01|-0.50|-27.41",
            "013|RoadLamp2Off|37.38|-0.50|1.90",
            "014|RoadLamp2Off|-68.41|-0.50|-4.83",
            "015|RoadLamp1d (10)|-52.90|-0.50|103.32",
            "016|RoadLamp2Off|26.10|-0.50|43.70",
            "017|RoadLamp2Off|-63.36|-0.50|-4.83",
            "018|RoadLamp1a (1)|-72.42|-0.50|-39.53",
            "019|RoadLamp2Off|31.09|-0.50|-16.78",
            "020|RoadLamp2Off|-39.66|-0.50|0.73",
            "021|RoadLamp1a (4)|10.94|-0.50|-39.53",
            "022|RoadLamp2Off|-48.54|-0.50|5.96",
            "023|RoadLamp2Off|-68.41|-0.50|-17.60",
            "024|RoadLamp2Off|16.86|-0.50|-27.41",
            "025|RoadLamp2Off|-68.41|-0.50|37.26",
            "026|RoadLamp1d (8)|-16.00|-0.50|103.32",
            "027|RoadLamp1a (7)|54.99|-0.50|-64.47",
            "028|RoadLamp1d (14)|2.70|-0.50|48.40",
            "029|RoadLamp1a (8)|33.00|-0.50|-64.47",
            "030|RoadLamp2Off|1.94|-0.50|-2.55",
            "031|RoadLamp2Off|-35.44|-0.50|7.32",
            "032|RoadLamp2Off|-29.10|-0.50|-3.98",
            "033|RoadLamp1d (1)|-63.74|-0.50|75.53",
            "034|RoadLamp1a (6)|55.07|-0.50|-39.53",
            "035|RoadLamp2Off|31.09|-0.50|36.54",
            "036|RoadLamp1a (11)|-50.70|-0.50|-64.50",
            "037|RoadLamp1a (5)|32.75|-0.50|-39.53",
            "038|RoadLamp1a (14)|-28.28|-0.50|-64.47",
            "039|RoadLamp1d (11)|-52.90|-0.50|48.40",
            "040|RoadLamp2Off|-34.01|-0.50|-3.98",
            "041|RoadLamp2Off|-58.70|-0.50|43.70",
            "042|RoadLamp2Off|25.74|-0.50|-27.41",
            "043|RoadLamp2Off|-63.39|-0.50|10.89",
            "044|RoadLamp2Off|-48.22|-0.50|-22.06",
            "045|RoadLamp2Off|-20.78|-0.50|25.25",
            "046|RoadLamp2Off|3.20|-0.50|43.70",
            "047|RoadLamp2Off|-48.22|-0.50|-27.41",
            "048|RoadLamp2Off|-29.10|-0.50|12.56",
            "049|RoadLamp1d (12)|-34.60|-0.50|48.40",
            "050|RoadLamp2Off|-57.88|-0.50|-22.06",
            "051|RoadLamp2Off|-20.30|-0.50|43.70",
            "052|RoadLamp1d (5)|32.57|-0.50|57.10",
            "053|RoadLamp2Off|-25.67|-0.50|-22.06",
            "054|RoadLamp2Off|-63.39|-0.50|24.49",
            "055|RoadLamp2Off|-39.66|-0.50|5.96",
            "056|RoadLamp1a (10)|-7.00|-0.50|-64.47",
            "057|RoadLamp2Off|-16.01|-0.50|-22.06",
            "058|RoadLamp2Off|-20.78|-0.50|16.90",
            "059|RoadLamp2Off|-63.36|-0.50|-17.60",
            "060|RoadLamp1d|-63.74|-0.50|57.09",
            "061|RoadLamp2Off|37.38|-0.50|36.54",
            "062|RoadLamp1d (9)|-34.60|-0.50|103.32",
            "063|RoadLamp1d (4)|32.57|-0.50|75.50",
            "064|RoadLamp2Off|-25.99|-0.50|-27.41",
            "065|RoadLamp2Off|37.38|-0.50|19.42",
            "066|RoadLamp1a (2)|-90.64|-0.50|-39.53",
            "067|RoadLamp1a (9)|10.60|-0.50|-64.47",
            "068|RoadLamp2Off|-4.28|-0.50|-17.89",
            "069|RoadLamp2Off|-29.10|-0.50|-17.62",
            "070|RoadLamp2Off|-7.13|-0.50|-22.06",
            "071|RoadLamp2Off|-68.41|-0.50|24.49",
            "072|RoadLamp1a (12)|-72.40|-0.50|-64.50",
            "073|RoadLamp2Off|37.38|-0.50|-16.78",
            "074|RoadLamp2Off|7.20|-0.50|-22.06",
            "075|RoadLamp2Off|-4.28|-0.50|-2.55",
            "076|RoadLamp2Off|-39.34|-0.50|-22.06",
            "077|RoadLamp2Off|-48.54|-0.50|0.73",
            "078|RoadLamp2Off|16.86|-0.50|-22.06",
            "079|RoadLamp1d (3)|32.57|-0.50|94.30",
            "080|RoadLamp2Off|7.20|-0.50|-27.41",
            "081|RoadLamp2Off|25.74|-0.50|-22.06",
            "082|RoadLamp2Off|-27.48|-0.50|25.26",
            "083|RoadLamp1d (15)|21.50|-0.50|48.40",
            "084|RoadLamp1a (13)|-91.24|-0.50|-64.50",
            "085|RoadLamp2Off|34.43|-0.50|-27.41",
            "086|RoadLamp2Off|-7.13|-0.50|-27.41",
            "087|RoadLamp2Off|-39.34|-0.50|-27.41",
            "088|RoadLamp2Off|-2.15|-0.50|25.25",
            "089|RoadLamp2Off|-4.28|-0.50|12.56",
            "090|RoadLamp1d (13)|-16.00|-0.50|48.40",
            "091|RoadLamp1d (7)|2.70|-0.50|103.32",
            "092|RoadLamp2Off|-35.44|-0.50|15.98",
            "093|RoadLamp2Off|-34.99|-0.50|25.26",
            "094|RoadLamp1d (2)|-63.74|-0.50|94.29",
            "095|RoadLamp2Off|1.81|-0.50|-17.89",
            "096|RoadLamp2Off|-58.20|-0.50|0.73",
            "097|RoadLamp2Off|31.09|-0.50|1.90",
            "098|RoadLamp2Off|-63.39|-0.50|37.26",
            "099|RoadLamp2Off|-57.88|-0.50|-27.41",
            "100|RoadLamp1a (3)|-7.28|-0.50|-39.53",
        };

        /// <summary>表条目的运行时形态。Claimed = 已被某盏场景灯认领（防一处坐标匹配两盏灯）。</summary>
        private struct LampSpec
        {
            public int No;
            public string Name;
            public float X, Y, Z;
            public bool Claimed;
        }

        private static List<LampSpec> _table;
        private static ExteriorLampMounter _runner;
        private static SceneWatcher _watcher;

        // ================================================================ 自举

        /// <summary>
        /// 静态状态复位。G10 教训：关掉 Domain Reload 时静态字段跨 Play 存活，
        /// 不清的话上一局的 runner 引用会让本局误判"已挂载"而永远建不出锚点。
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _runner = null;
            _watcher = null;
            _table = null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            TryMount();
        }

        /// <summary>
        /// 挂载入口。幂等：已挂就返回；场景不对（没有 TwinRuntime）就不挂。
        /// 【为什么用 TwinRuntime 当判据】与 TwinWeatherBootstrap / IndoorFillLight 同口径：
        /// 它只在 SchoolSceneDay 里有 —— 菜单 / 废弃场景本来就没有室外路灯，天然挡在外面。
        /// </summary>
        private static bool TryMount()
        {
            if (_runner != null) return true;
            if (TwinRuntime.Instance == null) return false;

            GameObject go = new GameObject(RootName);
            _runner = go.AddComponent<ExteriorLampMounter>();
            MountAll(_runner);   // 真正的挂载在这里发生；runner 只负责后续的昼夜轮询与清理

            // 场景监视器：AfterSceneLoad 只在第一次进场景时跑，之后 SceneManager.LoadScene
            // 不会再触发它。常驻一个只听 sceneLoaded 的空壳（无每帧开销），切场景回来补挂。
            if (_watcher == null)
            {
                GameObject w = new GameObject(WatcherName);
                UnityEngine.Object.DontDestroyOnLoad(w);
                _watcher = w.AddComponent<SceneWatcher>();
            }
            return true;
        }

        internal static void NotifyRunnerDestroyed()
        {
            _runner = null;
        }

        // ================================================================ 挂载主流程

        /// <summary>
        /// 挂载 100 盏（幂等，可重复调用）。失败逐条 LogWarning，绝不静默跳过。
        /// </summary>
        private static void MountAll(ExteriorLampMounter runner)
        {
            Transform root = FindContainer();
            if (root == null)
            {
                Debug.LogWarning("[P44] 未找到 " + ContainerName + "，室外路灯锚点未挂（当前场景没有路灯容器）");
                return;
            }

            List<LampSpec> table = Table();
            for (int i = 0; i < table.Count; i++)
            {
                LampSpec s = table[i];
                s.Claimed = false;
                table[i] = s;
            }

            int mounted = 0;
            int skipped = 0;                       // 已有锚点且 TwinId 一致（重复进场景 / 重复调用）
            List<TwinAnchor> mountedAnchors = new List<TwinAnchor>(table.Count);
            List<string> unmatchedLamps = new List<string>(4);
            StringBuilder claimedSb = new StringBuilder();

            foreach (Transform child in root)
            {
                if (child == null) continue;
                if (!child.name.StartsWith("RoadLamp", StringComparison.Ordinal)) continue;

                int idx = FindSpec(child.name, child.position, table, false);
                if (idx < 0)
                {
                    // 双保险：如果这盏灯已有锚点且 ID 是本表派生的，视为已挂（幂等重入）。
                    TwinAnchor existing = child.GetComponent<TwinAnchor>();
                    if (existing != null && existing.TwinId != null &&
                        existing.TwinId.StartsWith(IdPrefix, StringComparison.Ordinal))
                    {
                        skipped++;
                        mountedAnchors.Add(existing);
                        continue;
                    }

                    unmatchedLamps.Add(child.name + " @" + child.position.ToString("F2"));
                    continue;
                }

                LampSpec spec = table[idx];
                spec.Claimed = true;
                table[idx] = spec;

                string twinId = IdPrefix + spec.No.ToString("000");
                string displayName = "室外路灯 " + spec.No.ToString("000");

                TwinAnchor anchor = child.GetComponent<TwinAnchor>();
                if (anchor == null) anchor = child.gameObject.AddComponent<TwinAnchor>();
                anchor.Configure(twinId, ExteriorZone, "Light", displayName);

                mountedAnchors.Add(anchor);
                mounted++;
            }

            // 反向对账：表里还有哪几条没被任何场景灯认领。
            int unclaimed = 0;
            for (int i = 0; i < table.Count; i++)
            {
                if (table[i].Claimed) continue;
                unclaimed++;
                if (claimedSb.Length > 0) claimedSb.Append("; ");
                claimedSb.Append(table[i].No.ToString("000")).Append("(").Append(table[i].Name).Append(")");
            }

            Debug.Log("[P44] ==== 室外路灯锚点自举 ====\n"
                      + "  容器：" + ContainerName + "\n"
                      + "  本次挂载 " + mounted + " 盏（幂等跳过 " + skipped + "）\n"
                      + "  期望 " + table.Count + " 盏 / 实得锚点 " + mountedAnchors.Count + " 盏\n"
                      + "  TwinId：Exterior/Light/RoadLamp_001~100（Zone=Exterior，Category=Light，主理人裁决 1）\n"
                      + "  LampHours 已钉 1240h；Power 开灯窗口 19:00–07:00（亮 42W / 灭 0W），Exterior 回路可控来电");

            if (unclaimed > 0)
                Debug.LogWarning("[P44] 交接表 " + unclaimed + " 条未匹配到场景路灯：" + claimedSb.ToString());

            if (unmatchedLamps.Count > 0)
                Debug.LogWarning("[P44] 场景 " + unmatchedLamps.Count + " 盏路灯在交接表无匹配（未挂锚点，不许静默跳过）："
                                 + string.Join("; ", unmatchedLamps.ToArray()));

            // 数据侧 + 视觉侧收尾（见 runner.Init）。
            runner.Init(mountedAnchors);
        }

        /// <summary>
        /// 找路灯容器。GameObject.Find 只返回激活对象 —— 容器万一被停用，这里全量兜底
        /// （限定已加载场景内的对象，排除 prefab 资产；并要求确有 RoadLamp 直接子节点，防同名误命中）。
        /// </summary>
        private static Transform FindContainer()
        {
            GameObject go = GameObject.Find(ContainerName);
            if (go != null) return go.transform;

            Transform[] all = Resources.FindObjectsOfTypeAll<Transform>();
            for (int i = 0; i < all.Length; i++)
            {
                Transform t = all[i];
                if (t == null || t.name != ContainerName) continue;
                if (!t.gameObject.scene.IsValid()) continue;
                if (t.childCount == 0) continue;

                foreach (Transform c in t)
                {
                    if (c != null && c.name.StartsWith("RoadLamp", StringComparison.Ordinal)) return t;
                }
            }
            return null;
        }

        /// <summary>
        /// 按 名称 + 位置 找表条目。onlyUnclaimed=false 时忽略认领标记
        /// （首挂和幂等重入都要能找到自己那条）；true 用于防一处坐标匹配两盏灯。
        /// </summary>
        private static int FindSpec(string lampName, Vector3 pos, List<LampSpec> table, bool onlyUnclaimed)
        {
            for (int i = 0; i < table.Count; i++)
            {
                LampSpec s = table[i];
                if (onlyUnclaimed && s.Claimed) continue;
                if (!string.Equals(s.Name, lampName, StringComparison.Ordinal)) continue;

                float dx = s.X - pos.x;
                float dy = s.Y - pos.y;
                float dz = s.Z - pos.z;
                if (dx * dx + dy * dy + dz * dz <= MatchToleranceSq) return i;
            }
            return -1;
        }

        /// <summary>表条目解析（懒加载缓存）。格式见 TableLines 注释。</summary>
        private static List<LampSpec> Table()
        {
            if (_table != null) return _table;

            List<LampSpec> list = new List<LampSpec>(TableLines.Length);
            for (int i = 0; i < TableLines.Length; i++)
            {
                string[] parts = TableLines[i].Split('|');
                if (parts.Length < 5)
                {
                    Debug.LogWarning("[P44] 交接表第 " + (i + 1) + " 行格式非法，已跳过：" + TableLines[i]);
                    continue;
                }

                LampSpec s;
                s.No = int.Parse(parts[0], CultureInfo.InvariantCulture);
                s.Name = parts[1];
                s.X = float.Parse(parts[2], CultureInfo.InvariantCulture);
                s.Y = float.Parse(parts[3], CultureInfo.InvariantCulture);
                s.Z = float.Parse(parts[4], CultureInfo.InvariantCulture);
                s.Claimed = false;
                list.Add(s);
            }
            _table = list;
            return _table;
        }

        // ================================================================ 运行主体

        /// <summary>
        /// 挂载 runner：持有本局挂出的锚点清单，负责数据钉值、昼夜窗口轮询与视觉切换。
        /// 场景卸载随之销毁，OnDestroy 清干净（override / 材质 / 灯光），不留运行时态。
        /// </summary>
        private sealed class ExteriorLampMounter : MonoBehaviour
        {
            private readonly List<LampVisual> _lamps = new List<LampVisual>(128);
            private readonly List<Material> _glowInstances = new List<Material>(4);
            private Vector4[] _glowLamps;
            private bool? _lastOn;
            private float _nextPollRealtime;

            /// <summary>挂载完成后由 MountAll 调用一次：建视觉缓存 + 钉 LampHours + 按当前时刻定状态。</summary>
            public void Init(List<TwinAnchor> anchors)
            {
                for (int i = 0; i < anchors.Count; i++)
                {
                    TwinAnchor a = anchors[i];
                    if (a == null) continue;
                    LampVisual v = new LampVisual();
                    v.TwinId = a.TwinId;
                    v.Root = a.transform;
                    _lamps.Add(v);
                }

                TouchUpCircuitTitle();
                SeedLampHours();
                BuildVisuals();
                ApplyState(true);
            }

            /// <summary>
            /// Circuit-Exterior 是 RoomOf 对未登记分区的现场造节点，Title 会落成裸的 "Exterior"。
            /// 电力系统页直接展示 Title —— 补成中文可读名，运维看到的是「室外路灯回路」。
            /// 只改运行时对象的展示字段，不落盘、不碰 TwinCircuit 代码。
            /// </summary>
            private static void TouchUpCircuitTitle()
            {
                TwinCircuitNode node = TwinCircuit.RoomOf(ExteriorZone);
                if (node != null && node.Title == ExteriorZone) node.Title = "室外路灯回路";
            }

            /// <summary>
            /// LampHours 钉 1240h。累计量必须钉住：不钉会被波形在 1080~8920h 之间来回摆，
            /// "灯泡用了 8000 小时又变回 2000" 在答辩现场是无法解释的画面（P26 同款病）。
            /// </summary>
            private void SeedLampHours()
            {
                for (int i = 0; i < _lamps.Count; i++)
                {
                    MockDataSource.SetOverride(_lamps[i].TwinId, "LampHours", LampHoursSeed);
                }
            }

            /// <summary>
            /// 开灯判定 = **时段窗口 ∧ 电力闸门**（主理人 2026-11-17 裁决，数据与视觉共用这一份）：
            ///   · 19:00–07:00 亮（原话"路灯只在晚上7.00-早上7.00之间亮"）；
            ///   · Exterior 回路拉闸 → 全体路灯灭 —— "电力系统可以控制路灯是否来电"。
            /// </summary>
            private bool LampShouldBeOn()
            {
                double h = TwinClimate.HourOfDay % 24.0;
                if (h < 0.0) h += 24.0;
                if (h < OnToHour || h >= OnFromHour)
                {
                    return TwinCircuit.IsPoweredZone(ExteriorZone);
                }
                return false;
            }

            /// <summary>数据侧：按开灯状态钉 Power override。档位变化才写，不逐帧刷 100 盏字典。</summary>
            private void ApplyData(bool on)
            {
                float watt = on ? PowerOnWatt : 0f;
                for (int i = 0; i < _lamps.Count; i++)
                {
                    MockDataSource.SetOverride(_lamps[i].TwinId, "Power", watt);
                }
            }

            /// <summary>视觉侧：玻璃自发光换/还 + 灯光开/关。档位变化才刷。</summary>
            private void ApplyVisual(bool on)
            {
                for (int i = 0; i < _lamps.Count; i++)
                {
                    LampVisual v = _lamps[i];
                    if (v.GlassRenderer != null)
                    {
                        Material[] mats = v.GlassRenderer.sharedMaterials;
                        if (v.GlassSlot >= 0 && v.GlassSlot < mats.Length)
                        {
                            mats[v.GlassSlot] = on ? v.GlowMaterial : v.OriginalMaterial;
                            v.GlassRenderer.sharedMaterials = mats;
                        }
                    }
                    if (v.HeadLight != null) v.HeadLight.enabled = false;   // 先全灭，预算逻辑立刻点亮近处
                }

                if (on)
                {
                    UpdateLightBudget();
                }
                else
                {
                    // 灯灭 → 粒子距离场清零（白天/拉闸时不该有任何"灯周围雪亮"）
                    Shader.SetGlobalFloat("_P44GlowCount", 0f);
                }
            }

            /// <summary>
            /// 真光源预算：只开离主相机最近 LightBudget 盏。
            ///
            /// 为什么不 100 盏全开：内置管线 forward 下 pixelLightCount 名额有限
            /// （IndoorFillLight 已为室内抬到 32），100+ 实时光源必然大面积降级顶点光。
            /// 而真实夜路的观感本来就是"远处灯 = 发光亮点，近处灯 = 照亮路面" ——
            /// 预算策略恰好同时满足观感与性能，还给了答辩一句现成的解释。
            /// 100 盏的距离计算 + 选择，每 0.5s 一次，成本可忽略。
            /// </summary>
            private void UpdateLightBudget()
            {
                Camera cam = Camera.main;
                if (cam == null) return;

                Vector3 camPos = cam.transform.position;

                // 按到相机的距离排序取最小 N 盏。100 元素 × 每 0.5s 一次全排序，成本可忽略，
                // 换来一段一眼就能看对错的选择逻辑（部分插入排序在满/不满两态下极易写错）。
                List<int> order = new List<int>(_lamps.Count);
                for (int i = 0; i < _lamps.Count; i++) order.Add(i);

                Comparison<int> byDistance = delegate(int a, int b)
                {
                    float da = Vector3.SqrMagnitude(_lamps[a].HeadPos - camPos);
                    float db = Vector3.SqrMagnitude(_lamps[b].HeadPos - camPos);
                    return da.CompareTo(db);
                };
                order.Sort(byDistance);

                HashSet<int> chosen = new HashSet<int>();
                for (int k = 0; k < LightBudget && k < order.Count; k++) chosen.Add(order[k]);

                for (int i = 0; i < _lamps.Count; i++)
                {
                    if (_lamps[i].HeadLight != null) _lamps[i].HeadLight.enabled = chosen.Contains(i);
                }

                // 【P44 光锥显雪】活跃灯头 → 雨雪粒子的解析距离场（见 PrecipitationLit）
                PublishGlowLamps(chosen);
            }

            /// <summary>
            /// 把当前点亮的灯头喂给雨雪粒子的解析距离场（CampusUI/PrecipitationLit
            /// 的 _P44Glow 全局数组：xyz=灯头世界坐标，w=灯强度）。粒子片元按
            /// "到灯头距离"整段提亮，做出实拍里贯穿灯头上下的光柱雪幕——
            /// 零额外实时光源、零层级，灯灭（白天/拉闸）时清零。
            /// </summary>
            private void PublishGlowLamps(HashSet<int> chosen)
            {
                if (_glowLamps == null || _glowLamps.Length != LightBudget)
                    _glowLamps = new Vector4[LightBudget];

                int n = 0;
                foreach (int idx in chosen)
                {
                    LampVisual v = _lamps[idx];
                    float w = v.HeadLight != null ? v.HeadLight.intensity : 1.5f;
                    Vector3 p = v.HeadPos;
                    _glowLamps[n++] = new Vector4(p.x, p.y, p.z, w);
                }
                Shader.SetGlobalVectorArray("_P44Glow", _glowLamps);
                Shader.SetGlobalFloat("_P44GlowCount", n);
            }

            private void Update()
            {
                if (Time.realtimeSinceStartup < _nextPollRealtime) return;
                _nextPollRealtime = Time.realtimeSinceStartup + LinkPollSeconds;
                ApplyState(false);

                // 开灯期间持续刷新真光源预算：玩家走动时"最近 N 盏"要跟着走，
                // 否则近处永远没光池、远处灯白亮（预算只在翻转时算一次的老 bug）。
                if (_lastOn == true) UpdateLightBudget();
            }

            /// <summary>一次轮询：重算开灯判定，数据与视觉只在档位翻转时才真正写。</summary>
            private void ApplyState(bool force)
            {
                bool on = LampShouldBeOn();
                if (!force && _lastOn == on) return;

                bool changed = _lastOn != on;
                _lastOn = on;

                ApplyData(on);
                ApplyVisual(on);

                if (changed || force)
                {
                    Debug.Log("[P44] 路灯" + (on ? "亮灯（19:00–07:00 窗口内且 Exterior 回路通电）" : "熄灯（不在开灯窗口或 Exterior 回路断电）")
                              + "，共 " + _lamps.Count + " 盏（当前时刻 "
                              + TwinClimate.HourOfDay.ToString("F1", CultureInfo.InvariantCulture) + " 时）");
                }
            }

            // ------------------------------------------------------------ 视觉层构建

            /// <summary>
            /// 逐盏识别灯头玻璃与灯口位置，建自发光材质实例与运行时 Light（默认 disabled）。
            /// 失败的灯逐条警告 —— 玻璃找不着的灯夜间只会"哑亮"不出光，不能静默。
            /// </summary>
            private void BuildVisuals()
            {
                Dictionary<string, Material> glowCache = new Dictionary<string, Material>(4);

                for (int i = 0; i < _lamps.Count; i++)
                {
                    LampVisual v = _lamps[i];
                    Transform root = v.Root;
                    if (root == null) continue;

                    // ---- ① 找玻璃槽位（材质名判据，见类注释【视觉侧】）----
                    Renderer[] rends = root.GetComponentsInChildren<Renderer>(true);
                    for (int r = 0; r < rends.Length && v.GlassRenderer == null; r++)
                    {
                        Material[] mats = rends[r].sharedMaterials;
                        for (int s = 0; s < mats.Length; s++)
                        {
                            if (mats[s] == null) continue;
                            string mn = mats[s].name;
                            if (mn == GlassMatName1 || mn == GlassMatName2)
                            {
                                v.GlassRenderer = rends[r];
                                v.GlassSlot = s;
                                v.OriginalMaterial = mats[s];
                                break;
                            }
                        }
                    }

                    if (v.GlassRenderer == null)
                    {
                        Debug.LogWarning("[P44] " + v.TwinId + "（" + root.name + "）找不到灯头玻璃槽位（GlassWhite/GlassLamp1），夜档只出光不发光");
                    }
                    else
                    {
                        // 每种原材质只复制一份自发光实例，100 盏共享 —— 材质实例数量 = 基材质种类数（≤2）。
                        Material glow;
                        if (!glowCache.TryGetValue(v.OriginalMaterial.name, out glow))
                        {
                            glow = new Material(v.OriginalMaterial);
                            if (glow.HasProperty("_EmissionColor"))
                            {
                                glow.EnableKeyword("_EMISSION");
                                // 【P50】纯白(I,I,I) → 暖白 HDR：色温比例 × 强度，冲过 Bloom 阈值出光晕
                                glow.SetColor("_EmissionColor", new Color(
                                    GlowWarmTint.r * GlowEmissionIntensity,
                                    GlowWarmTint.g * GlowEmissionIntensity,
                                    GlowWarmTint.b * GlowEmissionIntensity, 1f));
                            }
                            glowCache[v.OriginalMaterial.name] = glow;
                            _glowInstances.Add(glow);
                        }
                        v.GlowMaterial = glow;
                    }

                    // ---- ② 灯口位置与光源参数（抄美术夜间变体）----
                    // 2Off/1a 的 LampGlass 挂在同名内层模型之下（root.Find 只搜直接子级，搜不到），
                    // 所以这里全树找一次；1d 没有玻璃节点，走下面的包围盒兜底。
                    Transform glassNode = null;
                    Transform[] allT = root.GetComponentsInChildren<Transform>(true);
                    for (int t = 0; t < allT.Length; t++)
                    {
                        if (allT[t].name == "LampGlass") { glassNode = allT[t]; break; }
                    }

                    if (glassNode != null)
                    {
                        v.HeadPos = glassNode.position;
                    }
                    else
                    {
                        // 1d 单节点灯：没有独立玻璃节点，用根 renderer 包围盒顶面中心近似灯口。
                        Renderer rootRend = root.GetComponent<Renderer>();
                        if (rootRend != null)
                        {
                            Bounds b = rootRend.bounds;
                            v.HeadPos = new Vector3(b.center.x, b.max.y - 0.4f, b.center.z);
                        }
                        else
                        {
                            v.HeadPos = root.position + Vector3.up * 5f;
                        }
                    }

                    v.HeadLight = MakeHeadLight(root, glassNode, v.HeadPos);
                }
            }

            /// <summary>灯头 Light：类型与参数逐型抄美术夜间变体（见类注释），阴影按主理人裁决 Soft。</summary>
            private Light MakeHeadLight(Transform root, Transform glassNode, Vector3 headPos)
            {
                GameObject go = new GameObject("RoadLampHeadLight");
                go.transform.SetParent(root, false);
                go.transform.position = headPos;

                Light l = go.AddComponent<Light>();

                if (root.name.StartsWith("RoadLamp1a", StringComparison.Ordinal))
                {
                    // 1a（园区路灯级，头高≈5.6m）：美术 1aNight 原为 Spot 85°——
                    // 光池半径仅 5.6m，低于现实【杆高×1.5~1.8 = 8.4~9.8m】的有效亮半径。
                    // 【主理人 2026-11-18 照射范围裁决：按现实来】
                    // 角度由 tan(半角) = 有效半径/高度 反推：tan(57.5°)=1.57 → 半径 9.6m。
                    // 射程/强度沿用美术变体（r30 / I2）。
                    l.type = LightType.Spot;
                    l.spotAngle = 115f;
                    l.range = 30f;
                    l.intensity = 2.6f;   // 【P50】原2.0→3.2→回调2.6：3.2 太亮刺眼、连带1楼室内过曝
                    go.transform.rotation = Quaternion.Euler(90f, 0f, 0f);   // 灯口朝下（截光型配光）
                }
                else if (root.name.StartsWith("RoadLamp1d", StringComparison.Ordinal))
                {
                    // 1d（市政路灯级，头高≈8.3m 双头）：美术 1dNight 原为 Spot 60°——
                    // 光池半径仅 4.8m，现实应为【杆高×1.5~1.8 = 12.5~14.5m】，严重偏小。
                    // 角度反推：tan(60°)=1.73 → 半径 14.4m，恰好落在现实区间上沿。
                    // 单节点模型分不出两个头，取顶面中心一盏宽角覆盖两头区域。
                    l.type = LightType.Spot;
                    l.spotAngle = 120f;
                    l.range = 35f;
                    l.intensity = 3.2f;   // 【P50】原2.8→4.0→回调3.2：市政双头光池同步柔化
                    go.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                }
                else
                {
                    // 2Off（庭院灯级，头高≈3.3m，69 盏主力灯型）：美术 2On 变体原为 Point 全向。
                    // 【主理人 2026-11-18 影子裁决】全向光把影子洗淡 → 截光化（Spot 向下）；
                    // 【主理人 2026-11-18 照射范围裁决】Spot 90° 光池半径仅 3.3m，偏小——
                    // 现实庭院灯级有效亮半径 = 杆高×1.5~1.8 ≈ 5.0~5.8m。角度反推：
                    // tan(60°)=1.73 → 半径 5.8m；射程提到 9 保证锥边（6.4m）平滑落地不硬切。
                    // 强度仍沿用美术变体 I1.5。
                    l.type = LightType.Spot;
                    l.spotAngle = 120f;
                    l.range = 9f;
                    l.intensity = 2.0f;   // 【P50】原1.5→2.6→回调2.0：69盏庭院灯主力，柔化但保留光池（不退回照不亮）
                    go.transform.rotation = Quaternion.Euler(90f, 0f, 0f);   // 灯口朝下（截光配光）
                }

                l.shadows = LightShadows.Soft;   // 主理人裁决：近 14 盏 + Soft 阴影
                l.enabled = false;               // 预算逻辑点亮
                return l;
            }

            // ------------------------------------------------------------ 清理

            private void OnDestroy()
            {
                // ① 数据：放开本 runner 钉的 override（与 MockDeviceAgent.OnDestroy 同一职责）
                for (int i = 0; i < _lamps.Count; i++)
                {
                    MockDataSource.ClearOverride(_lamps[i].TwinId, "Power");
                    MockDataSource.ClearOverride(_lamps[i].TwinId, "LampHours");
                }

                // ② 视觉：材质还原（多槽位 renderer 必须整组写回，不能只写单槽）
                for (int i = 0; i < _lamps.Count; i++)
                {
                    LampVisual v = _lamps[i];
                    if (v.GlassRenderer != null && v.OriginalMaterial != null)
                    {
                        Material[] mats = v.GlassRenderer.sharedMaterials;
                        if (v.GlassSlot >= 0 && v.GlassSlot < mats.Length)
                        {
                            mats[v.GlassSlot] = v.OriginalMaterial;
                            v.GlassRenderer.sharedMaterials = mats;
                        }
                    }
                    if (v.HeadLight != null) Destroy(v.HeadLight.gameObject);
                }

                // ③ 运行时材质实例（renderer.material 不涉及，全是手动 new 的）
                for (int i = 0; i < _glowInstances.Count; i++)
                {
                    if (_glowInstances[i] != null) Destroy(_glowInstances[i]);
                }
                _glowInstances.Clear();
                _lamps.Clear();

                // ④ 粒子距离场清零（Domain Reload 关闭时全局量会跨 Play 残留）
                Shader.SetGlobalFloat("_P44GlowCount", 0f);

                ExteriorLampAnchors.NotifyRunnerDestroyed();
            }
        }

        /// <summary>每盏灯的视觉缓存。HeadPos 同时用于真光源预算的距离排序。</summary>
        private sealed class LampVisual
        {
            public string TwinId;
            public Transform Root;
            public Renderer GlassRenderer;
            public int GlassSlot = -1;
            public Material OriginalMaterial;
            public Material GlowMaterial;
            public Light HeadLight;
            public Vector3 HeadPos;
        }

        // ================================================================ 场景监视器

        /// <summary>
        /// 只做一件事：场景加载后尝试补挂（幂等，代价一次空引用检查）。
        /// 常驻 DontDestroyOnLoad，无每帧开销。IndoorFillLight 同款。
        /// </summary>
        private sealed class SceneWatcher : MonoBehaviour
        {
            private void OnEnable()
            {
                SceneManager.sceneLoaded += OnSceneLoaded;
            }

            private void OnDisable()
            {
                SceneManager.sceneLoaded -= OnSceneLoaded;
            }

            private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
            {
                TryMount();
            }
        }
    }
}

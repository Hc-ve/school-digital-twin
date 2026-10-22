using System;
using System.Collections.Generic;
using UnityEngine;

namespace SchoolTwin.Twin
{
    /// <summary>设备属性的值类型。只用于「怎么显示 / 怎么校验」，不参与回读比对。</summary>
    public enum TwinPropertyKind
    {
        Bool = 0,
        Int = 1,
        Float = 2,
    }

    /// <summary>
    /// 设备的一个**可读属性**（对齐 W3C WoT Thing Description 的 properties）。
    ///
    /// 【为什么叫属性而不叫指标】指标（TwinMetricDefinition）是「数据链路上的一个数值」，
    /// 属性是「设备对外的能力描述」。两者常常一一对应，但语义不同：
    /// 指标关心阈值和量程，属性关心「它是不是开关量、能不能写」。
    /// 现在两者用 Metric 字段关联起来（属性 → 指标名）。
    /// </summary>
    [Serializable]
    public class TwinPropertyDefinition
    {
        [Tooltip("属性名，如 Switch / SetTemp")]
        public string Name = "Switch";

        [Tooltip("对应的指标名。留空 = 与 Name 相同。数据链路上回读时按这个键取值")]
        public string Metric = "";

        public TwinPropertyKind Kind = TwinPropertyKind.Bool;

        [Tooltip("恒为 false —— 状态只能由设备上报，不许由命令回写。见 ForceReadOnly()")]
        public bool Writable = false;

        public string Unit = "";
        public float Min = 0f;
        public float Max = 1f;

        public string MetricName
        {
            get { return string.IsNullOrEmpty(Metric) ? Name : Metric; }
        }
    }

    /// <summary>
    /// 设备的一个**动作**（对齐 WoT 的 actions）。
    ///
    /// 【Expect* 这几项是干什么的】
    /// 命令发出去之后，怎么判断它真的生效了？唯一可信的答案是「回到数据链路上读状态位」。
    /// 所以每个动作都要声明：读哪个指标（ExpectMetric）、什么范围算「到位」。
    ///   · 开关量（SetPower/SetOpen）：按 On/Off 给两个区间，因为「开」对应的是
    ///     功率从 0 变成几十瓦，不是一个精确值；
    ///   · 连续量（SetTemperature/SetValve）：给一个容差。
    /// **没有 ExpectMetric 的动作不许下发** —— 否则就只能靠 ack 说「成功了」，
    /// 而那正是本项目最想避免的「点了就绿」。
    /// </summary>
    [Serializable]
    public class TwinActionDefinition
    {
        [Tooltip("动作名，与协议里的 Action 一致，如 SetPower")]
        public string Name = "SetPower";

        public float Min = 0f;
        public float Max = 1f;
        public float Step = 1f;

        [Tooltip("true = 开关/枚举量（按 On/Off 区间判），false = 连续量（按容差判）")]
        public bool Discrete = true;

        [Tooltip("回读比对用的指标名。留空 = 该动作无法确认，禁止下发")]
        public string ExpectMetric = "Power";

        [Tooltip("连续量的容差：回读值落在 [value-tol, value+tol] 内算到位")]
        public float ExpectTolerance = 0.1f;

        public float ExpectOnMin = 1f;
        public float ExpectOnMax = 1f;
        public float ExpectOffMin = 0f;
        public float ExpectOffMax = 0f;
    }

    /// <summary>
    /// 设备模型：一个点位「能读什么、能做什么、和谁有关系」。
    ///
    /// 刻意不引入 RDF/OWL —— 我们只需要「这个设备有什么能力」这一个事实，
    /// 一个可序列化的类就够，而且能直接在 Inspector 里看。
    /// </summary>
    [Serializable]
    public class TwinDeviceModel
    {
        [Tooltip("设备类型，与 TwinPointDefinition.Category 一致")]
        public string Type = "";

        [Tooltip("relations.locatedIn —— 它在哪个空间里")]
        public string LocatedIn = "";

        [Tooltip("relations.poweredBy —— 它挂在哪条回路上（P15 断电的前提，现在只写不用）")]
        public string PoweredBy = "";

        public List<TwinPropertyDefinition> Properties = new List<TwinPropertyDefinition>();
        public List<TwinActionDefinition> Actions = new List<TwinActionDefinition>();

        [Tooltip("events —— 设备主动上报的事件名（P12 只声明不使用）")]
        public List<string> Events = new List<string>();

        public bool CanCommand
        {
            get { return Actions != null && Actions.Count > 0; }
        }

        public TwinActionDefinition FindAction(string name)
        {
            if (Actions == null || string.IsNullOrEmpty(name)) return null;
            for (int i = 0; i < Actions.Count; i++)
            {
                TwinActionDefinition a = Actions[i];
                if (a != null && string.Equals(a.Name, name, StringComparison.Ordinal)) return a;
            }
            return null;
        }

        /// <summary>
        /// 主开关动作 —— 这个设备「开 / 关」对应的那一个动作。
        ///
        /// 【为什么不能继续写死 SetPower】
        /// 门只有 SetOpen 一个动作。界面上那颗「开」按钮如果一律发 SetPower，
        /// 门点下去会被命令中心以「不支持」拒掉（`TrySend` 里 `FindAction` 返回 null），
        /// 而界面上只表现为闪一下红字 —— 看不出是动作名不对，看起来像"门的控制没做"。
        ///
        /// 判据用 `Discrete`（开关量），不用动作名：新增一个只有 SetOpen / SetMode 的
        /// 设备类型时，按钮会自动跟着走，不需要再改界面代码。
        /// 暖气（只有 SetValve，连续量）返回 null —— 与改动前「找不到 SetPower 就不给按钮」一致。
        /// </summary>
        public TwinActionDefinition PrimarySwitchAction
        {
            get
            {
                if (Actions == null) return null;
                for (int i = 0; i < Actions.Count; i++)
                {
                    TwinActionDefinition a = Actions[i];
                    if (a != null && a.Discrete) return a;
                }
                return null;
            }
        }

        public TwinPropertyDefinition FindProperty(string metricName)
        {
            if (Properties == null || string.IsNullOrEmpty(metricName)) return null;
            for (int i = 0; i < Properties.Count; i++)
            {
                TwinPropertyDefinition p = Properties[i];
                if (p != null && string.Equals(p.MetricName, metricName, StringComparison.Ordinal)) return p;
            }
            return null;
        }
    }

    /// <summary>
    /// 设备模型工厂 + 回读判据。
    ///
    /// 全部表驱动：新增一个设备类型 = 加一个 case，不改任何业务代码。
    /// 数值区间是照着 TwinCatalog_Mock.asset 里现有指标的量程写的（Power 0~120W / 0~3.5kW 等），
    /// 改量程时这里要一起看。
    /// </summary>
    public static class TwinDeviceModels
    {
        /// <summary>按 Category 派生设备模型。未知类型 = 只读（没有动作）。</summary>
        public static TwinDeviceModel For(string category, string zone, string twinId)
        {
            TwinDeviceModel m = new TwinDeviceModel();
            m.Type = category ?? "";
            m.LocatedIn = zone ?? "";
            m.PoweredBy = string.IsNullOrEmpty(zone) ? "" : "Circuit-" + zone;

            switch (category)
            {
                case "Light":
                    AddProperty(m, "Power", "Power", TwinPropertyKind.Bool, "W", 0f, 120f);
                    // 开灯 → 功率 42W（量程 0~120，警告线 110）；关灯 → 0W（警告线是 0，不误报）
                    AddAction(m, "SetPower", true, "Power", 0f, 1f, 1f, 25f, 70f, 0f, 0.6f);
                    m.Events.Add("LampFail");
                    break;

                case "AC":
                    AddProperty(m, "SetTemp", "SetTemp", TwinPropertyKind.Float, "°C", 16f, 30f);
                    AddProperty(m, "Power", "Power", TwinPropertyKind.Bool, "kW", 0f, 3.5f);
                    // 开空调 → 1.2kW；关 → 0kW
                    AddAction(m, "SetPower", true, "Power", 0f, 1f, 1f, 0.5f, 2.2f, 0f, 0.06f);
                    AddAction(m, "SetTemperature", false, "SetTemp", 16f, 30f, 1f, 0f, 0f, 0f, 0f);
                    m.Actions[1].ExpectTolerance = 0.12f;
                    m.Events.Add("OverTemp");
                    m.Events.Add("FilterDue");
                    break;

                case "Heat":
                    AddProperty(m, "ValveOpen", "ValveOpen", TwinPropertyKind.Float, "%", 0f, 100f);
                    AddAction(m, "SetValve", false, "ValveOpen", 0f, 100f, 10f, 0f, 0f, 0f, 0f);
                    m.Actions[0].ExpectTolerance = 0.6f;
                    break;

                case "PC":
                    // 【2026-10-22 量程修正：0~300W → 0~3000W】
                    // 0~300W 是**单台**电脑的量程，可这个点位的语义是「**整间**机房的电脑」——
                    // 计算机教室有 10 台 Computer06，满负荷 10×300 = 3000W。
                    //
                    // 用单台量程去卡整间总功耗，两种表现都是错的：
                    //   · 按真实总功耗算 → 1800W 直接顶穿 300W 上限，永远报警；
                    //   · 按单台功耗算   → 永远「健康」，量程形同虚设（这就是改动前的状态）。
                    // 而且 MockSignalGenerator 的基线是**从阈值派生**的，
                    // 阈值区间不对，模拟出来的波形也就不对 —— 那条「292W 告警」其实是假告警。
                    AddProperty(m, "Power", "Power", TwinPropertyKind.Bool, "W", 0f, 3000f);

                    // 【为什么加 OnCount】房间级点位最容易回答的问题不是「耗多少瓦」，
                    // 而是「开了几台」。10 台 × 180W 的功耗数字看不出「有 2 台没开」。
                    AddProperty(m, "OnCount", "OnCount", TwinPropertyKind.Int, "台", 0f, 30f);

                    // 开 = 10 台 × 180W = 1800W；关 = 0W。
                    // 回读区间放到 800~2200：机房不一定满编 10 台，
                    // 卡太死会让「确实开了 6 台」被判成「执行了但状态没变」。
                    AddAction(m, "SetPower", true, "Power", 0f, 1f, 1f, 800f, 2200f, 0f, 1f);
                    break;

                case "Door":
                    AddProperty(m, "Open", "Open", TwinPropertyKind.Bool, "0/1", 0f, 1f);
                    AddAction(m, "SetOpen", true, "Open", 0f, 1f, 1f, 0.99f, 1.01f, -0.01f, 0.01f);
                    break;

                default:
                    // Sensor / Alarm：只读。没有动作 = 面板上不会出现按钮（不是灰按钮）
                    if (category == "Sensor")
                    {
                        AddProperty(m, "Temperature", "Temperature", TwinPropertyKind.Float, "°C", 10f, 35f);
                        AddProperty(m, "Humidity", "Humidity", TwinPropertyKind.Float, "%RH", 20f, 80f);
                    }
                    else if (category == "Alarm")
                    {
                        AddProperty(m, "Smoke", "Smoke", TwinPropertyKind.Float, "obs/m", 0f, 100f);
                    }
                    break;
            }

            ForceReadOnly(m);
            return m;
        }

        /// <summary>
        /// 把所有属性强制标成不可写。
        ///
        /// 【为什么要有这一步，而不是只在构造时写 false】
        /// 属性是序列化字段，Inspector 里能改。只要有一个属性的 writable 被打开，
        /// 将来就一定有人写「命令成功后顺手把状态写回去」—— 而那就是假闭环的入口。
        /// 所以在**每次取模型时**强制复位，把这条红线做成结构，而不是靠自觉。
        /// </summary>
        public static void ForceReadOnly(TwinDeviceModel model)
        {
            if (model == null || model.Properties == null) return;
            for (int i = 0; i < model.Properties.Count; i++)
            {
                if (model.Properties[i] != null) model.Properties[i].Writable = false;
            }
        }

        /// <summary>
        /// 取某个点位的设备模型：优先用点位目录里的（可被 Inspector 覆盖），
        /// 目录里没有就按锚点的 Category 现场派生。
        ///
        /// 【为什么统一放这里】命令中心、设备代理、卡片控制区三处都要同一个答案。
        /// 各自实现一遍的话，迟早出现「面板认为可控、设备认为不支持」这种对不上的情况。
        /// </summary>
        public static TwinDeviceModel Resolve(string twinId)
        {
            if (string.IsNullOrEmpty(twinId)) return null;

            TwinRuntime runtime = TwinRuntime.Instance;
            if (runtime == null) return null;

            TwinCatalog catalog = runtime.Catalog;
            TwinPointDefinition point = catalog != null ? catalog.Find(twinId) : null;
            if (point != null) return point.GetDeviceModel();

            TwinAnchor anchor;
            if (runtime.Registry != null && runtime.Registry.TryGet(twinId, out anchor) && anchor != null)
                return For(anchor.Category, anchor.Zone, twinId);

            return null;
        }

        /// <summary>
        /// 启动时盘一次账：32 个点位的模型到底从哪来。
        ///
        /// 【为什么需要这一行日志】
        /// 「资产里的 DeviceModel 填过没有」这个问题，翻 Inspector 是看不出来的：
        /// 反序列化出来的空壳不是 null，Inspector 里显示成一个折叠的、展开全空的条目，
        /// 和「填了一半」长得一模一样。所以只能数内容 —— 而数内容这件事应该在运行时自己说，
        /// 不能靠人去看 YAML。
        ///
        /// 判定口径：
        ///   · 资产内填了 = Actions 或 Properties 非空（Type 单独填了不算，仍会派生）
        ///   · 派生 = 资产里没有内容，按 Category 现场算
        /// 两者之和应当等于点位总数。对不上就说明目录漏了点。
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void LogInventoryOnLoad()
        {
            TwinRuntime runtime = TwinRuntime.Instance;
            TwinCatalog catalog = runtime != null ? runtime.Catalog : null;
            if (catalog == null || catalog.Points == null || catalog.Points.Count == 0)
            {
                // 不是致命错：Resolve 会退到「按锚点 Category 派生」，功能还在。
                // 但阈值会跟着丢（量程/警告线在目录里），所以要说出来。
                Debug.LogWarning("[TwinModel] 点位目录没挂上（TwinRuntime._catalog 为空）——" +
                                 "设备模型只能按锚点 Category 现场派生，卡片上的阈值也会缺");
                return;
            }

            int total = 0, fromAsset = 0, commandable = 0;
            for (int i = 0; i < catalog.Points.Count; i++)
            {
                TwinPointDefinition p = catalog.Points[i];
                if (p == null) continue;
                total++;
                if (p.HasSerializedModel) fromAsset++;

                TwinDeviceModel m = p.GetDeviceModel();
                if (m != null && m.CanCommand) commandable++;
            }

            Debug.Log("[TwinModel] 点位 " + total + " 个：资产内填了模型 " + fromAsset +
                      " 个，运行时派生 " + (total - fromAsset) + " 个；" +
                      "其中有动作（可命令）" + commandable + " 个；" +
                      "控制白名单开放 " + TwinControlScope.Count + " 个");
        }

        /// <summary>
        /// 由「动作 + 参数」推出回读判据。返回 false = 该动作没有判据，不许下发。
        /// </summary>
        public static bool TryBuildExpectation(TwinDeviceModel model, string action, float value,
                                               out string metric, out float min, out float max)
        {
            metric = null;
            min = 0f;
            max = 0f;

            TwinActionDefinition def = model != null ? model.FindAction(action) : null;
            if (def == null || string.IsNullOrEmpty(def.ExpectMetric)) return false;

            metric = def.ExpectMetric;

            if (def.Discrete)
            {
                bool on = value >= 0.5f;
                min = on ? def.ExpectOnMin : def.ExpectOffMin;
                max = on ? def.ExpectOnMax : def.ExpectOffMax;
            }
            else
            {
                min = value - def.ExpectTolerance;
                max = value + def.ExpectTolerance;
            }
            return true;
        }

        private static void AddProperty(TwinDeviceModel m, string name, string metric,
                                        TwinPropertyKind kind, string unit, float min, float max)
        {
            TwinPropertyDefinition p = new TwinPropertyDefinition();
            p.Name = name;
            p.Metric = metric;
            p.Kind = kind;
            p.Unit = unit;
            p.Min = min;
            p.Max = max;
            p.Writable = false;
            m.Properties.Add(p);
        }

        private static void AddAction(TwinDeviceModel m, string name, bool discrete, string expectMetric,
                                      float min, float max, float step,
                                      float onMin, float onMax, float offMin, float offMax)
        {
            TwinActionDefinition a = new TwinActionDefinition();
            a.Name = name;
            a.Discrete = discrete;
            a.ExpectMetric = expectMetric;
            a.Min = min;
            a.Max = max;
            a.Step = step;
            a.ExpectOnMin = onMin;
            a.ExpectOnMax = onMax;
            a.ExpectOffMin = offMin;
            a.ExpectOffMax = offMax;
            m.Actions.Add(a);
        }
    }

    /// <summary>
    /// 控制开放范围。
    ///
    /// 【为什么要有白名单】不是所有「有动作」的设备都该立刻开放。
    /// 判据是**表现层能不能跟上**：点了之后场景里必须有反应，否则用户只会得出
    /// 「这个按钮是假的」——那比不开放更糟。
    ///
    /// 所以白名单只放「表现层已接管」的设备类型：
    ///   · Light —— 灯管发光 + 点光源（P13）
    ///   · AC    —— 收到命令时「滴滴」（P13）
    ///   · Door  —— 绕门轴转动，开/关有动画（P17 / P18）
    ///
    /// 还**没**开放的（可命令点位里剩下的这些）：
    ///   · Heat 9 台 —— 没有可视化部件（只有阀门开度数字），点了看不出反应
    ///   · PC 2 台   —— 【P18 复核结论：不只是「没有可视化部件」】
    ///                  `Computer06` / `Monitor04` 都是**单网格单材质**，
    ///                  贴图图集里没有独立的「屏幕」区域，也没有单独的屏幕子物体 ——
    ///                  所以「开机 = 屏幕亮起来」这件事**做不出来**，除非补一块屏幕面片。
    ///                  既然三维里不会有反应，就不开放，理由和暖气一样。
    ///
    /// 扩展方式：往 Ids 数组里加点位即可，UI、设备代理、表现层三处都从这一处取。
    /// </summary>
    public static class TwinControlScope
    {
        private static readonly string[] Ids =
        {
            // ---- 照明 10 盏（10 个房间各一盏，一个点位代表**整个房间的照明**）----
            "Class1-2/Light/Main_01",
            "Class1-3/Light/Main_01",
            "Class1-4/Light/Main_01",
            "Class2-1/Light/Main_01",
            "Class2-2/Light/Main_01",
            "ClassComputer/Light/Main_01",
            "ClassDirector/Light/Main_01",
            "ClassTeachers/Light/Main_01",
            "Corridor1/Light/Main_01",
            "Corridor2/Light/Main_01",

            // ---- 空调 5 台 ----
            "Class1-2/AC/Unit_01",
            "Class1-3/AC/Unit_01",
            "Class1-4/AC/Unit_01",
            "Class2-1/AC/Unit_01",
            "ClassDirector/AC/Unit_01",

            // ---- 门 6 扇（P17 一扇 + P18 补五扇）----
            // 锚点都挂在 `Door07a` 实例本身（不是标记牌），所以三维里点得到、面板里也点得到。
            // 场景里一共 11 扇 Door07a，剩下 5 扇在南翼（运动教室那一侧）——
            // 那几间房连分区、房间范围、任何点位都没有，门只能看数字不能转，
            // 所以**有意不绑**，留给「扩分区」那一步一起做。
            "Class2-1/Door/Entrance_01",
            "ClassComputer/Door/Entrance_01",
            "Class1-2/Door/Entrance_01",
            "Class1-3/Door/Entrance_01",
            "Class1-4/Door/Entrance_01",
            "Class2-2/Door/Entrance_01",
        };

        public static bool IsEnabled(string twinId)
        {
            if (string.IsNullOrEmpty(twinId)) return false;
            for (int i = 0; i < Ids.Length; i++)
            {
                if (string.Equals(Ids[i], twinId, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>全部受控点位。表现层要逐个遍历它们，所以只读暴露出去。</summary>
        public static IReadOnlyList<string> All { get { return Ids; } }

        public static int Count { get { return Ids.Length; } }
    }
}

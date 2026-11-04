using UnityEditor;
using UnityEngine;
using SchoolTwin.Weather;

// 【别名：必须加，否则 CS0118】
// 本文件在 namespace SchoolTwin.EditorTools 里，外层命名空间是 SchoolTwin；
// 而 SchoolTwin 的成员里有一个**命名空间**叫 SchoolTwin.Weather，里面的枚举也叫 Weather。
// 裸写 `Weather.Rain` 时 C# 解析顺序：
//   ① 当前命名空间 SchoolTwin.EditorTools 的成员 → 没命中
//   ② 外层命名空间 SchoolTwin 的成员 → 命中 `SchoolTwin.Weather` **命名空间**，停
//   ③ using 引入的类型 → 轮不到
// 于是报 `'Weather' is a namespace but is used like a type`。
// 这个坑在 TwinWeatherBridge / TwinTimeWeatherCard / 本文件上已踩过第三次 —— 别再犯。
using WeatherKind = SchoolTwin.Weather.Weather;

namespace SchoolTwin.EditorTools
{
    /// <summary>
    /// 【P13】天气演示开关：Play 中一键切雨 / 切雪，用来答辩演示和快速验证。
    ///
    /// ══════════════════════════════════════════════════════════════════
    /// 【本文件原有一个「③ 在场景里挂上天气系统」菜单，已删除】
    ///
    /// 那个菜单用 `Undo.AddComponent` 把 `EnvironmentState` 写进 `SchoolSceneDay.unity`，
    /// 再让用户按 Ctrl+S 存盘。删除基于两条理由：
    ///
    ///   ① **P13 任务包明令「禁止手动往场景摆天气物件」**。那个方案本身就违规 ——
    ///      为了修一个"组件没挂上"的问题，把 12 个脚本的类别与引用关系写进 10 万行的
    ///      场景文件，风险与收益不成比例，答辩前一天动场景是最不该做的事。
    ///   ② **项目里已有更适合的成形做法**：`TwinPerfBootstrap` 修「按 F6 一直没反应」，
    ///      用的就是 `AfterSceneLoad` 运行时挂载（同样是"组件从未挂进场景"、同样按 guid
    ///      查到引用数 0）。现在 `TwinWeatherBootstrap` 用的是同一个方子 ——
    ///      **用户不用做任何操作，进 Play 就有天气系统**。
    ///
    /// 保留"往场景挂"还会制造第二个创建点：场景里一份 + 自举一份，两个实例在
    /// `Awake` 里因 `Instance != this` 互相 Destroy。这正是本项目反复踩的"同型病"。
    ///
    /// ══════════════════════════════════════════════════════════════════
    /// 【为什么这两个开关只在 Play 模式可用】
    ///
    /// 非 Play 模式下改 `env.weather` 没有任何视觉反应（没有粒子在跑），
    /// 而且退出 Play 会整个回滚 —— 用户会以为"点了没生效"。
    /// 直接拒绝比"接受了但你看不见"诚实。
    /// </summary>
    public static class TwinP13WeatherSetup
    {
        [MenuItem("P13 时间天气/③ 设为雨天（仅 Play 模式，演示用）", false, 30)]
        public static void SetRainMenu()
        {
            ApplyInPlay(WeatherKind.Rain, "雨天");
        }

        [MenuItem("P13 时间天气/④ 设为雪天（仅 Play 模式，演示用）", false, 31)]
        public static void SetSnowMenu()
        {
            ApplyInPlay(WeatherKind.Snow, "雪天");
        }

        [MenuItem("P13 时间天气/⑤ 设为晴天（仅 Play 模式，演示用）", false, 32)]
        public static void SetClearMenu()
        {
            ApplyInPlay(WeatherKind.Clear, "晴天");
        }

        private static void ApplyInPlay(WeatherKind w, string label)
        {
            if (!EditorApplication.isPlaying)
            {
                EditorUtility.DisplayDialog("仅 Play 模式可用",
                    "这个菜单是给答辩演示 / 快速验证用的：Play 起来之后切天气，看粒子有没有出来。\n\n"
                    + "非 Play 模式下改没有意义（没有粒子在跑，退出 Play 还会回滚），"
                    + "所以直接拒绝，免得你以为改了场景。", "好");
                return;
            }

            if (EnvironmentState.Instance == null)
            {
                EditorUtility.DisplayDialog("天气系统未运行",
                    "EnvironmentState.Instance 是 null，说明自举没成功。\n\n"
                    + "可能原因：\n"
                    + "  · 当前场景里没有 TwinRuntime（本工具只对孪生场景生效）\n"
                    + "  · TwinWeatherBootstrap 还没编译进去\n\n"
                    + "先跑「② 诊断：为什么看不到雨雪」，它会打出到底断在哪一环。", "好");
                return;
            }

            // 必须走桥：只改视觉侧会让 TwinClimate 停在旧天气（同型病）
            SchoolTwin.Twin.TwinWeatherBridge.ApplyWeather(w);

            Debug.Log("[P13] 已切到「" + label + "」。\n"
                      + "  env.weather = " + EnvironmentState.Instance.weather + "\n"
                      + "  若 3 秒内看不到，立刻跑「② 诊断：为什么看不到雨雪」——"
                      + "它会打印 altFade / indoor / 粒子 emitted 数，一眼能看出断在哪一环。");
        }
    }
}
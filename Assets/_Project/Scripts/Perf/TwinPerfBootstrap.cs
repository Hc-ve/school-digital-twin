using UnityEngine;
using SchoolTwin.Twin;

namespace SchoolTwin.Perf
{
    /// <summary>
    /// 把 <see cref="TwinPerfReport"/> 自动挂进场景 —— 修的是「按 F6 一直没反应」。
    ///
    /// ── 为什么需要这一步（2026-10-22 实测）────────────────────────────
    /// `TwinPerfReport` 写好了、编译过了、`_toggleKey` 也是 `F6`，但**它从来没被挂进正式场景**：
    /// 按 GUID 查 `SchoolSceneDay.unity`，引用数是 0。它只在压测菜单
    /// （`P1 孪生框架 / ⑨ 把 100/200/500 点位档挂到当前场景`）里被顺带 `AddComponent` 上，
    /// 而那三个入口会顺手把点位目录换成压测档 —— 演示时不可能为了按一下 F6 去换目录。
    ///
    /// 所以结果是：**F6 这个键在整个正式场景里没有任何人监听**。不是按键冲突，是根本没人接。
    ///
    /// ── 为什么用运行时挂载，而不是改场景 ─────────────────────────────
    /// 场景文件 10 万行、1362 个预制体实例，为了加一个调试组件去动它，风险和收益不成比例
    /// （而且改场景必须关掉 Unity 才能批处理）。运行时挂载是项目里已经验证过的做法 ——
    /// `TwinPowerController` 就是这么起来的（`AfterSceneLoad` + `Ensure`）。
    ///
    /// ── 为什么把 AutoStart 关掉 ─────────────────────────────────────
    /// 组件默认 `_autoStart = true`，那是给压测用的（进 Play 就开始采，跑满 60 秒自动落盘）。
    /// 自动挂载如果沿用这个默认值，每次按 Play 都会：屏幕上多一块读数、后台写一个 CSV，
    /// 而用户根本没要求采样。改成「按 F6 才开始」之后，这个组件的存在感等于零 ——
    /// 不按 F6 就只有一个空 `Update` 在读一个键。
    ///
    /// ⚠️ 诚实的边界：`FindObjectOfType` 每帧不跑，只在 `AfterSceneLoad` 跑一次；
    /// 场景里已经手挂了 `TwinPerfReport` 的话这里什么都不做（尊重手挂的配置）。
    /// </summary>
    public static class TwinPerfBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Mount()
        {
            // 已经手挂过就完全不动它 —— 手挂意味着有人配过参数，不该被这里覆盖
            if (UnityEngine.Object.FindObjectOfType<TwinPerfReport>() != null) return;

            TwinRuntime runtime = TwinRuntime.Instance;
            if (runtime == null)
            {
                // 没有 [TwinRuntime] 说明场景根本没起来，这时候挂采样器也没意义，
                // 而且挂到哪都是猜。安静退出比乱挂一个物体强。
                return;
            }

            TwinPerfReport report = runtime.gameObject.AddComponent<TwinPerfReport>();

            // 必须在 Start() 之前赋值。AfterSceneLoad 到第一帧 Start 之间就是这个窗口。
            report.AutoStart = false;
            report.ShowOverlay = true;

            Debug.Log("[TwinPerf] 性能采样器已挂上 —— 按 F6 开始 / 再按 F6 停止，" +
                      "结果写 PerfLogs/twin-perf.csv（每次最多采 60 秒）。");
        }
    }
}

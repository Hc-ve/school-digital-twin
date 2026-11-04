using UnityEngine;
using SchoolTwin.Twin;

namespace SchoolTwin.Weather
{
    /// <summary>
    /// 【P13】天气系统自举 —— 修「play 了没雨没雪」。
    ///
    /// ══════════════════════════════════════════════════════════════════
    /// 【根因：场景里从来没有 EnvironmentState】
    ///
    /// 12 个天气脚本移植过来了、编译也通过了，但**没有任何一处把它挂进场景**。
    /// 逐场景按 guid（`335a7a62e07ea574a9439a11d8ba05b1`）查过，四个场景引用数全是 0：
    ///     SchoolSceneDay        0
    ///     SchoolSceneNight      0
    ///     SchoolSceneAbandoned  0
    ///     SchoolSceneMenu       0
    ///
    /// 于是运行时是这样一条链：
    ///
    ///     场景加载 → 没有任何天气组件 → EnvironmentState.Instance == null
    ///              → 天空 / 光照 / 雾 / 雨雪粒子 / 云穹顶 全都不存在
    ///              → 玩家看到的是场景自带的静态晴天天光
    ///              → 点「雨天」→ 桥 ApplyWeather → env == null → **静默 return**
    ///
    /// 「点了按钮什么都没有」不是按钮坏了，是接收方不存在。而这条 return 不报错，
    /// 所以从 Console 到画面全都"正常" —— 这类静默失败最难查。
    ///
    /// ══════════════════════════════════════════════════════════════════
    /// 【为什么运行时挂，而不是让用户跑菜单往场景里塞】
    ///
    /// 两条理由，第二条是硬约束：
    ///
    ///   ① **任务包明令：「禁止手动往场景摆天气物件」**。我上一版给的是
    ///      「跑菜单 ③ 挂组件 + Ctrl+S 存场景」，那个方案本身就违反禁令 ——
    ///      把 12 个脚本的类别和引用关系写进 10 万行的场景文件，风险与收益不成比例。
    ///   ② 本工程已有的成形做法就是运行时挂载，且有先例可循：
    ///      · `TwinPerfBootstrap` —— 修「按 F6 一直没反应」，同样是因为
    ///        `TwinPerfReport` 从未被挂进场景（按 guid 查引用数 0）
    ///      · `TwinPowerController` / `TwinBellScheduler` / `TwinDeviceModel` /
    ///        `TwinDevicePresenter` —— 全是 `AfterSceneLoad` 自举
    ///      · `TwinDrawer` 自己也是 `AfterSceneLoad` 自举起来的
    ///      本类与 `TwinPerfBootstrap` 是同一个病的同一个方子。
    ///
    /// ══════════════════════════════════════════════════════════════════
    /// 【为什么用 TwinRuntime.Instance 当"是不是孪生场景"的判据】
    ///
    /// 不按场景名硬编码 `SchoolSceneDay`：
    ///   · 场景名一改就失效，而且改的人不会想到还有个自举在依赖它
    ///   · 按 guid 查过，`TwinRuntime` **只在 SchoolSceneDay 里有一份**
    ///     （Night / Abandoned / Menu 全是 0），它与"这是孪生演示场景"等价
    ///   · `TwinPerfBootstrap` 用的正是这个判据，口径统一
    ///
    /// 所以菜单场景、废弃场景不会被塞进一套天气系统 —— 那里没有 TwinRuntime。
    ///
    /// ══════════════════════════════════════════════════════════════════
    /// 【为什么必须新建独立物体，不能挂在 TwinRuntime 身上】
    ///
    /// `EnvironmentState.Awake` 第一件事就是 `DontDestroyOnLoad(gameObject)`。
    /// 如果把它 AddComponent 到 TwinRuntime 那个物体上，**整个 TwinRuntime 会被一起
    /// 搬到 DontDestroyOnLoad 场景** —— Bus、Registry、所有数据源线程全部跨场景残留，
    /// 切场景后旧 runtime 不会被销毁，第二份起来时两个实例互相 Destroy。
    /// 这是"挂错父物体"造成的最坏情况，比没挂更难查。
    ///
    /// 所以这里 new 一个独立的 `[Environment]` 根节点。它被 DontDestroyOnLoad 带走
    /// 是**合理**的：天空盒、平行光、雾、粒子本来就该跟着相机跨场景。
    ///
    /// ══════════════════════════════════════════════════════════════════
    /// 【挂上之后天气是什么】
    ///
    /// `EnvironmentState` 字段默认 `timeOfDay = Day`、`weather = Clear`，
    /// 所以挂上之后**默认仍是晴天**。这是刻意的（不该一进场景就下雨），
    /// 要看到雨雪需要点抽屉「时间天气」→「雨天 / 雪天」。
    ///
    /// `WeatherDirector.auto` 保持 `false`：答辩时天气自己每 90~200 秒跳一次
    /// 会打断讲解节奏，演示要的是"我点它就变"。需要自动轮换时面板里能开
    /// （`TwinTimeWeatherCard.OnPickWeather` 反而会主动关掉它）。
    /// </summary>
    public static class TwinWeatherBootstrap
    {
        /// <summary>根节点名。带方括号是本工程对"系统级常驻节点"的命名惯例（如 `[TwinRuntime]`）。</summary>
        private const string RootName = "[Environment]";

        /// <summary>等 TwinRuntime 出现的上限（秒）。与 TwinDrawer 的重试器同口径。</summary>
        private const float RetrySeconds = 5f;

        // ================================================================ 自举

        /// <summary>
        /// 【为什么 AfterSceneLoad 是安全的时机】
        ///
        /// `AfterSceneLoad` 在所有场景物体的 `Awake` 之后、`Start` 之前。
        /// 三个依赖都在这个窗口里已经就绪：
        ///   · `TwinRuntime.Instance` —— 它的 `Awake` 已跑（`[DefaultExecutionOrder(-100)]`）
        ///   · 场景灯光 —— 已激活，`FindSun()` 能 `FindObjectsOfType<Light>()` 到
        ///   · 主相机 —— 已激活，`Camera.allCameras` 能取到
        /// 而 `Start` 还没跑，所以 `WeatherVisuals` 建出来的粒子在第一帧渲染前就已播上，
        /// 不会出现"进场景先晴一秒再下雨"的跳变。
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Mount()
        {
            if (TryMount()) return;

            // 拿不到 TwinRuntime 不代表"不是孪生场景" —— 可能只是自举顺序还没轮到它。
            // Unity 不保证同一批 RuntimeInitializeOnLoadMethod 的执行顺序（TwinDrawer
            // 就被这个坑过：P29-3 新增两个文件后顺序一变，它的自举排到了画布前面，
            // 整个抽屉没建出来且不报任何异常）。所以这里挂一个重试器，逐帧等到超时。
            GameObject runner = new GameObject("TwinWeatherBootstrap");
            UnityEngine.Object.DontDestroyOnLoad(runner);
            runner.AddComponent<Retry>();
        }

        /// <summary>能挂就挂。返回 false 表示 TwinRuntime 还没出来，调用方该等下一帧。</summary>
        private static bool TryMount()
        {
            if (EnvironmentState.Instance != null) return true;
            if (TwinRuntime.Instance == null) return false;
            Ensure();
            return true;
        }

        /// <summary>
        /// **天气系统唯一的创建入口**。已经在就返回现有的，不重复挂。
        ///
        /// 【为什么必须是唯一入口】本项目反复踩的"同型病"：同一件事有两个创建处，
        /// 就会有两个实例互相 Destroy 的竞态。上一版 `TwinDrawer.ShowTimeWeatherCard()`
        /// 里有一份兜底 `new GameObject("Environment")`，与本类并存时，
        /// 点面板那一刻建的那份和自举建的那份会撞车（Awake 里 `Instance != this` → Destroy）。
        /// 现在抽屉那边改调本方法，创建点只剩这一处。
        /// </summary>
        internal static EnvironmentState Ensure()
        {
            EnvironmentState existing = EnvironmentState.Instance;
            if (existing == null) existing = UnityEngine.Object.FindObjectOfType<EnvironmentState>(true);
            if (existing != null) return existing;

            GameObject go = new GameObject(RootName);

            // 顺序不能反：先加 EnvironmentState，它的 Awake 会 DontDestroyOnLoad 并把
            // WeatherVisuals 子物体建好；再加 WeatherDirector 时就已在 DontDestroyOnLoad 场景里。
            EnvironmentState env = go.AddComponent<EnvironmentState>();

            // 自动轮换默认关（字段默认就是 false，这里显式写一遍表明是刻意选择，不是漏配）
            WeatherDirector dir = go.AddComponent<WeatherDirector>();
            dir.auto = false;

            // ---- 把数据侧对齐视觉侧 ----
            //
            // 【为什么需要这一步】两侧的字段默认值本来就不一致：
            //     EnvironmentState.weather  默认 Clear  （视觉：晴）
            //     TwinClimate._weather      默认 Cloudy （数据：多云）
            // 挂上之后立刻就是"天上大太阳、室温按多云算"—— 两条数据都"正常"，
            // 只有把它们放一起看才发现矛盾。这正是本项目反复踩的"同型病"。
            //
            // 方向取 视觉 → 数据，理由是视觉侧才是用户此刻真在看的那个状态：
            // 进场景看到晴天，数据就该按晴天算。反方向（数据 → 视觉）会在进场景的
            // 第一秒把天色从晴拉到多云，玩家会看到一段没有来源的渐变。
            //
            // 走桥而不是直接写 TwinClimate，是因为桥才是唯一写入出口（任务包第 6 条）。
            // 桥内部 `env.SetWeather(v)` 传的正是 `env.weather` 本身，
            // `EnvironmentState.Set` 里的 same 判断会直接返回，不会重播过渡。
            TwinWeatherBridge.ApplyWeather(env.weather);

            Debug.Log("[P13] ==== 天气系统已自举 ====\n"
                      + "  EnvironmentState → 「" + RootName + "」（DontDestroyOnLoad）\n"
                      + "  WeatherDirector  → 同物体，auto=false（不自动跳天气）\n"
                      + "  初始状态：白天 · 晴（刻意默认值，不会一进场景就下雨）\n"
                      + "  数据侧已对齐视觉侧（TwinClimate 原默认是 Cloudy，不改它会让两边矛盾）\n"
                      + "  要看到雨雪：点抽屉工具行「时间天气」→「雨天 / 雪天」");

            return env;
        }

        /// <summary>等 TwinRuntime 的重试器：逐帧试，最多 5 秒，超时就停（不刷屏）。</summary>
        private sealed class Retry : MonoBehaviour
        {
            private float _deadline;

            private void Awake()
            {
                _deadline = Time.realtimeSinceStartup + RetrySeconds;
            }

            private void Update()
            {
                if (TryMount())
                {
                    Destroy(gameObject);
                    return;
                }

                if (Time.realtimeSinceStartup >= _deadline)
                {
                    // 走到这里说明整个场景没有 TwinRuntime —— 大概率是菜单/废弃场景。
                    // 不报警告：那些场景本来就该没有天气系统，报了反而是噪音。
                    Destroy(gameObject);
                }
            }
        }
    }
}
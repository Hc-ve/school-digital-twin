using System;
using UnityEngine;
using SchoolTwin.Data;    // TwinClimate：起始时刻来源
using SchoolTwin.Twin;
using SchoolTwin.UI;      // 面板冲突调度要探测 TwinTimeWeatherCard 是否已打开

namespace SchoolTwin.Weather
{
    /// <summary>
    /// 【P43】昼夜循环调度器 —— 现实 900 秒 = 游戏 24 小时。
    ///
    /// ══════════════════════════════════════════════════════════════════
    /// 【职责边界：只推时间，不碰光照】
    /// 本类只做两件事：
    ///   ① 用真实时间差算出当前连续小时数；
    ///   ② 经 <see cref="TwinWeatherBridge.ApplyHour"/> 把小时写进数据侧与视觉侧。
    /// 跨 Day/Dusk/Night 的 2.4s 渐变由 Bridge → EnvironmentState.Set 完成，
    /// P36/P39 的 Bloom / postExposure / _lampEmission 一个字都不动。
    /// ══════════════════════════════════════════════════════════════════
    ///
    /// 【为什么用 realtimeSinceStartup 而不是 Time.deltaTime（P43 裁决 3）】
    /// Time.deltaTime 受 timeScale 与 MaximumDeltaTime 影响，失焦/暂停后回来会丢时间；
    /// 本项目 WeatherDirector 仍用 deltaTime（本包不碰），两条轴在失焦时会分叉 ——
    /// 负责人已裁决采纳 A：循环器用真实时间、不改天气侧，视觉上差异不可察觉。
    ///
    /// 【为什么记录"基准小时 + 基准真实时刻"而不是逐帧 hour += delta】
    /// 逐帧累加会积累 double 舍入误差，跑一天后时刻会漂；
    /// 这里用 (now - baseRealtime) * 24 / cycleSeconds 一次性算出偏移，
    /// 误差只取决于 now 的精度，与帧数无关。
    ///
    /// 【面板接管为什么"冻结基准"而不是"记暂停时长"】
    /// 玩家拖表盘时会把 _hour 改成一个任意值。若只记暂停时长，
    /// 恢复时会从"循环器自己的旧时刻"继续，把玩家刚确认的时刻冲掉。
    /// 所以暂停时把当前 hour 写成新基准、恢复时重设 baseRealtime ——
    /// 下一次推进自然从玩家定格的时刻继续（与 TwinTimeWeatherCard.Close 的
    /// "关闭保持时刻"语义一致）。
    /// </summary>
    public sealed class TimeCycleDirector : MonoBehaviour
    {
        // ---- 默认周期（P43 裁决 5：15 分钟维持）----
        private const float DefaultCycleSeconds = 900f;
        private const double HoursPerDay = 24.0;
        private const float MinCycleSeconds = 60f;      // 调试下限 1 分钟
        private const float MaxCycleSeconds = 7200f;    // 调试上限 2 小时

        // ---- 粒子密度三挡的倍率 ----
        // 【P43 修复 11-16】密度经 WeatherVisuals.SetDensityScale 乘进三路速率写入源头。
        // 第一版用 rateOverTimeMultiplier + GetComponentsInChildren 子级遍历，有两个实锤缺陷：
        //   ① 雨/雪发射器因 P13-D9 刻意不挂父节点（世界坐标定位，挂子级会被楼板吃掉发射盒），
        //      子级遍历只够到 RainSplash 一份 —— 验收实测切三档时雨丝 3200/s 纹丝不动；
        //   ② 三份速率曲线都是 Constant 模式，该模式下 multiplier 读改不可靠且会被重写覆盖。
        private const float DensityLow = 0.35f;
        private const float DensityMedium = 1.0f;
        private const float DensityHigh = 2.0f;

        /// <summary>雨雪粒子密度档位。预留给设置按钮调用，本包只提供 API。</summary>
        public enum ParticleDensity { Low, Medium, High }

        /// <summary>运行时实例。与 TwinWeatherBootstrap 一样由自举创建，场景里没有此组件。</summary>
        public static TimeCycleDirector Instance { get; private set; }

        // ---- 时间基准 ----
        private double _baseHour;
        private float _baseRealtime;
        private bool _paused;

        // ---- 当前设置（静态，跨实例/场景保持）----
        private static float _cycleSeconds = DefaultCycleSeconds;
        private static ParticleDensity _density = ParticleDensity.Medium;

        // ---- 面板接管状态 ----
        // 【为什么用 bool 而不是"引用是否为 null"】
        // 面板被 Destroy 后 Unity 的 ==/!= 重载会把它当成 null，
        // 若用 "_panel != null" 判断"是否因面板而暂停"，面板一销毁条件就恒 false，
        // 循环永远解不了冻（P43 阶段 2 实机复现：IsPaused=True 且场景内活动面板=0）。
        // 所以单独记一个"本次暂停的成因是面板"的布尔量，不依赖引用存活。
        private bool _pausedByPanel;

        // ---- 面板探测 / 日志 两套独立节流 ----
        // 面板探测必须比日志快：关面板后要尽快恢复时间推进，
        // 若跟日志共用 30 秒节流，玩家关面板后最多 30 秒时间不动（体验明显是 bug）。
        private float _nextPanelProbe;
        private const float PanelProbeInterval = 0.5f;

        private float _nextLog;
        private const float LogInterval = 30f;

        // ================================================================ 自举

        /// <summary>
        /// 与 TwinWeatherBootstrap 同口径：AfterSceneLoad + 等 EnvironmentState。
        /// 不硬编码场景名，用 EnvironmentState.Instance 判"天气系统已就绪"。
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Mount()
        {
            if (Instance != null) return;
            if (!TryMount())
            {
                GameObject runner = new GameObject("TimeCycleBootstrap");
                UnityEngine.Object.DontDestroyOnLoad(runner);
                runner.AddComponent<Retry>();
            }
        }

        private static bool TryMount()
        {
            if (Instance != null) return true;
            if (EnvironmentState.Instance == null) return false;   // 等天气自举先挂好

            GameObject go = new GameObject("[TimeCycle]");
            UnityEngine.Object.DontDestroyOnLoad(go);
            TimeCycleDirector d = go.AddComponent<TimeCycleDirector>();
            d.Initialize();
            Instance = d;
            return true;
        }

        // ================================================================ 初始化

        private void Initialize()
        {
            // 起点取 TwinClimate.HourOfDay：
            // 若 Play 启动时是真实时钟，就是现在的时刻；
            // 若上一轮留下覆盖值（运行时状态不落盘，重进 Play 自然重置），也以它为准。
            _baseHour = NormalizeHour(TwinClimate.HourOfDay);
            _baseRealtime = Time.realtimeSinceStartup;
            _paused = false;

            ApplyParticleDensity();

            Debug.Log("[P43] ==== 昼夜循环已启动 ====\n"
                      + "  周期 = " + _cycleSeconds.ToString("0") + " 秒 = 游戏 24 小时\n"
                      + "  起始时刻 = " + FormatHour(_baseHour) + "\n"
                      + "  时间基准 = Time.realtimeSinceStartup 差值（不逐帧累加）\n"
                      + "  写入路径 = TwinWeatherBridge.ApplyHour（唯一入口）\n"
                      + "  天气模式 = 由 TwinWeatherBootstrap 设为循环模式默认随机");
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        // ================================================================ 推进

        private void Update()
        {
            // 【关键顺序】面板探测必须在"暂停则 return"之前。
            // 否则一旦因面板暂停，这里直接 return，WatchPanel 再也不执行 → 永久冻结。
            if (Time.realtimeSinceStartup >= _nextPanelProbe)
            {
                _nextPanelProbe = Time.realtimeSinceStartup + PanelProbeInterval;
                WatchPanel();
            }

            if (_paused) return;

            double hour = CurrentHour();

            // P39 铁律：改时刻只走桥。桥内部只在跨档时调 EnvironmentState.Set(2.4s 过渡)，
            // 所以这里每帧调用不会反复重启渐变。
            TwinWeatherBridge.ApplyHour(hour, true);

            if (Time.realtimeSinceStartup >= _nextLog)
            {
                _nextLog = Time.realtimeSinceStartup + LogInterval;
                Debug.Log("[P43] 时刻=" + FormatHour(hour)
                          + " 档=" + TwinWeatherBridge.ToTimeOfDay(hour)
                          + " 周期=" + _cycleSeconds.ToString("0") + "s");
            }
        }

        /// <summary>当前连续小时数 [0,24)。暂停时恒等于定格值。</summary>
        public double CurrentHour()
        {
            if (_paused) return _baseHour;

            double elapsed = Time.realtimeSinceStartup - _baseRealtime;
            double add = elapsed * HoursPerDay / _cycleSeconds;
            if (add < 0.0) add = 0.0;   // 极端时序保护：时钟倒退不把时间往回拨
            return NormalizeHour(_baseHour + add);
        }

        private static double NormalizeHour(double h)
        {
            double r = h % HoursPerDay;
            if (r < 0.0) r += HoursPerDay;
            return r;
        }

        private static string FormatHour(double h)
        {
            int hh = (int)Math.Floor(h);
            int mm = (int)Math.Round((h - hh) * 60.0);
            if (mm >= 60) { mm -= 60; hh = (hh + 1) % 24; }
            return hh.ToString("00") + ":" + mm.ToString("00");
        }

        // ================================================================ 面板冲突调度

        /// <summary>
        /// 低频探测时间天气面板。检测到打开就冻结基准，关闭后自动续跑。
        /// 用 FindObjectOfType 而不是订阅面板事件：本包不许改 TwinTimeWeatherCard.cs（P43 裁决 4）。
        /// 0.5 秒一次，开销可忽略（场景里该类实例数 ≤ 1）。
        /// </summary>
        private void WatchPanel()
        {
            bool open = FindObjectOfType<TwinTimeWeatherCard>() != null;

            if (open && !_paused)
            {
                Pause();
                _pausedByPanel = true;
                Debug.Log("[P43] 时间面板接管 → 循环暂停，定格 " + FormatHour(_baseHour));
            }
            else if (!open && _paused && _pausedByPanel)
            {
                // 只解除"因面板而暂停"的那种暂停；外部主动 Pause() 的不受面板影响。
                Resume();
                _pausedByPanel = false;
                Debug.Log("[P43] 时间面板关闭 → 循环从 " + FormatHour(_baseHour) + " 继续");
            }
        }

        /// <summary>
        /// 冻结当前时刻。
        ///
        /// 【P43 修复 11-16】冻结基准取 TwinClimate（数据侧）而不是 CurrentHour()（墙钟）：
        /// 恢复时 Resume() 从 TwinClimate 重读基准、面板表盘也读 TwinClimate ——
        /// 三处必须同一口钟。旧实现冻结在墙钟值，比数据侧超前最多一帧（掉帧时放大到
        /// 数游戏分钟，实测表盘 08:03 vs 冻结值 08:06），每次开合面板都会静默丢这段差值。
        /// </summary>
        public void Pause()
        {
            if (_paused) return;
            _baseHour = NormalizeHour(TwinClimate.HourOfDay);
            _paused = true;
        }

        /// <summary>
        /// 从数据侧当前时刻继续推进。
        ///
        /// 【为什么恢复时重读 TwinClimate.HourOfDay 而不是用暂停时存下的 _baseHour】
        /// 玩家在面板里可能把时间拖到别处并点确认，桥会把新时刻写进 TwinClimate。
        /// 若仍用开面板那一刻的旧 _baseHour，恢复第一帧就把玩家刚确认的时刻冲回去了。
        /// 所以以"数据侧此刻的真实值"为新基准 —— 这也是 P39 铁律「时刻以桥为准」的延伸。
        /// </summary>
        public void Resume()
        {
            if (!_paused) return;
            _baseHour = NormalizeHour(TwinClimate.HourOfDay);
            _baseRealtime = Time.realtimeSinceStartup;
            _paused = false;
        }

        public bool IsPaused { get { return _paused; } }

        // ================================================================ 设置 API（预留给设置按钮）

        /// <summary>循环周期（秒）。合法区间 [60, 7200]，默认 900。</summary>
        public static float CycleSeconds
        {
            get { return _cycleSeconds; }
            set
            {
                float v = Mathf.Clamp(value, MinCycleSeconds, MaxCycleSeconds);
                if (Mathf.Approximately(v, _cycleSeconds)) return;

                // 改周期时保持当前时刻连续，避免跳变。
                if (Instance != null && !Instance._paused)
                {
                    Instance._baseHour = Instance.CurrentHour();
                    Instance._baseRealtime = Time.realtimeSinceStartup;
                }
                _cycleSeconds = v;
                Debug.Log("[P43] 周期设置为 " + _cycleSeconds.ToString("0") + " 秒/24h");
            }
        }

        /// <summary>雨雪粒子密度档位。默认 Medium。</summary>
        public static ParticleDensity Density
        {
            get { return _density; }
            set
            {
                if (_density == value) return;
                _density = value;
                if (Instance != null) Instance.ApplyParticleDensity();
                Debug.Log("[P43] 粒子密度设置为 " + _density);
            }
        }

        /// <summary>供 UI 显示的设置摘要。设置按钮接入时直接读这个。</summary>
        public static string SettingsSummary()
        {
            return "周期 " + _cycleSeconds.ToString("0") + "s / 粒子 " + _density;
        }

        /// <summary>
        /// 把密度档位经 <see cref="WeatherVisuals.SetDensityScale"/> 写进三路速率写入源头。
        ///
        /// 【为什么不能用 GetComponentsInChildren（第一版的实锤 bug）】雨/雪两份发射器
        /// 因 P13-D9 刻意**不挂任何父节点**（世界坐标定位，挂到 WeatherVisuals 下会被
        /// 楼板吃掉发射盒），子级遍历只够到 RainSplash —— 2026-11-16 验收实测：切三档
        /// 时雨丝 3200/s 纹丝不动，密度控制对主雨雪整体失效。改从 WeatherVisuals 的
        /// 写入源头乘 _densityScale，雨/雪/水花三份一份不漏。
        ///
        /// 【为什么不能用 rateOverTimeMultiplier】三份曲线都是 Constant 模式，该模式下
        /// multiplier 的读改行为不可靠、且会被 Apply 的速率重写覆盖。
        /// 找不到天气视觉时静默返回 —— 面板可能比天气自举更早显示。
        /// </summary>
        private void ApplyParticleDensity()
        {
            WeatherVisuals visuals = EnvironmentState.Instance != null
                ? EnvironmentState.Instance.GetComponent<WeatherVisuals>()
                : null;
            if (visuals == null) visuals = FindObjectOfType<WeatherVisuals>();
            if (visuals == null) return;

            float k = _density == ParticleDensity.Low ? DensityLow
                    : _density == ParticleDensity.High ? DensityHigh
                    : DensityMedium;

            visuals.SetDensityScale(k);
        }

        // ================================================================ 编辑器菜单（设置入口预留）

#if UNITY_EDITOR
        [UnityEditor.MenuItem("SchoolTwin/P43 昼夜循环/设置摘要（Console）")]
        private static void MenuSettings()
        {
            Debug.Log("[P43] " + SettingsSummary()
                      + " | 实例=" + (Instance != null ? "已启动" : "未启动")
                      + " | 当前时刻=" + (Instance != null ? FormatHour(Instance.CurrentHour()) : "n/a"));
        }

        [UnityEditor.MenuItem("SchoolTwin/P43 昼夜循环/周期 15 分钟（默认）")]
        private static void MenuCycleDefault() { CycleSeconds = DefaultCycleSeconds; }

        [UnityEditor.MenuItem("SchoolTwin/P43 昼夜循环/周期 5 分钟（演示加速）")]
        private static void MenuCycleFast() { CycleSeconds = 300f; }

        [UnityEditor.MenuItem("SchoolTwin/P43 昼夜循环/粒子密度/小")]
        private static void MenuDensityLow() { Density = ParticleDensity.Low; }

        [UnityEditor.MenuItem("SchoolTwin/P43 昼夜循环/粒子密度/中")]
        private static void MenuDensityMedium() { Density = ParticleDensity.Medium; }

        [UnityEditor.MenuItem("SchoolTwin/P43 昼夜循环/粒子密度/大")]
        private static void MenuDensityHigh() { Density = ParticleDensity.High; }
#endif

        // ================================================================ 等天气自举的重试器

        private sealed class Retry : MonoBehaviour
        {
            private float _deadline;

            private void Awake()
            {
                _deadline = Time.realtimeSinceStartup + 5f;
            }

            private void Update()
            {
                if (TryMount() || Time.realtimeSinceStartup >= _deadline)
                {
                    Destroy(gameObject);
                }
            }
        }
    }
}

using SchoolTwin.Twin;

namespace SchoolTwin.Data
{
    /// <summary>
    /// Mock 数值生成器：把「正弦波走遍全量程」换成
    /// 「平时在正常区间内缓慢波动 + 偶发短暂越界」。
    ///
    /// ── 为什么必须改 ──────────────────────────────────────────────
    /// 旧实现：value = Min + norm * (Max - Min)，正弦覆盖**整个量程**；
    /// 而阈值（WarnLow/WarnHigh/AlarmLow/AlarmHigh）卡在量程中段，
    /// 于是数值只要往两侧摆，就必然长时间落在警告/告警区。
    ///
    /// 实测（2026-10-20）：5 个点位常态有 3 个异常
    ///   CO2 1990ppm（量程 400~2000，告警线 1500）
    ///   亮度 2.22%（量程 0~100，警告线 20）
    ///   温度 33.11℃（量程 10~35，告警线 32）
    /// 而 P3 验收标准是「非技术人员 3 分钟内**找到**告警房间」——
    /// 一半房间同时在报警，「找到」这个动作就失去了意义。
    ///
    /// ── 新实现分两层 ──────────────────────────────────────────────
    /// ① 基线：在 [WarnLow, WarnHigh] 内收 12% 的区间里做慢速正弦
    ///          → 平时全绿，且永远不会踩到阈值线；
    /// ② 事件：每个指标按各自相位、每 ExcursionPeriodSeconds 秒触发一次
    ///          短促偏移，把数值推向量程边界
    ///          → 偶发一次警告/告警，随即自动恢复。
    ///
    /// ── 设计取舍：为什么不新增「模拟区间」配置项 ──────────────────
    /// 阈值就是「业务上什么算正常」的唯一声明，模拟数据理应服从它。
    /// 若再单独配一个模拟区间，就会出现「阈值改了、模拟数据还在老区间跑」
    /// 的分裂状态。所以这里让模拟区间**由阈值派生**：改阈值即改模拟行为。
    ///
    /// ⚠️ 线程约束：本类只用 System.Math 与自定义比较，**不触碰任何 Unity API**，
    /// 可安全在后台数据线程调用。
    /// （P1-D 曾因在写线程里读 Time.realtimeSinceStartup 导致写线程静默死掉。）
    /// </summary>
    public static class MockSignalGenerator
    {
        /// <summary>生成参数。用对象初始化器构造即可，零 GC。</summary>
        public struct Config
        {
            /// <summary>基线波动周期（秒）。平时数值在这个周期内缓慢起伏。</summary>
            public float PeriodSeconds;

            /// <summary>异常事件周期（秒）。每隔这么久制造一次短暂越界。&lt;= 1 表示永不越界。</summary>
            public float ExcursionPeriodSeconds;

            /// <summary>每次异常事件持续的秒数。</summary>
            public float ExcursionSeconds;

            /// <summary>事件峰值冲向量程边界的深度（0~1）。0.95 ≈ 冲到 Min/Max 附近。</summary>
            public float ExcursionDepth;
        }

        /// <summary>
        /// 生成一个数值。
        /// </summary>
        /// <param name="metric">指标定义，提供 Min/Max 与四个阈值。</param>
        /// <param name="t">数据源启动至今的秒数（不是 Unix 时间戳，避免精度丢失）。</param>
        /// <param name="phase">相位偏移，用于让不同点位/指标的曲线互相错开。</param>
        /// <param name="cfg">生成参数。</param>
        public static float Next(TwinMetricDefinition metric, double t, double phase, in Config cfg)
        {
            if (metric == null) return 0f;

            float lo = metric.Min;
            float hi = metric.Max;
            if (hi <= lo) return lo;

            // ── ① 基线：夹在警告阈值之内，再向内收 12%，平时不踩线 ──
            float warnLo = Clamp(metric.WarnLow, lo, hi);
            float warnHi = Clamp(metric.WarnHigh, lo, hi);
            if (warnHi <= warnLo)
            {
                // 阈值区间无效（例如 WarnLow >= WarnHigh）→ 退回全量程
                warnLo = lo;
                warnHi = hi;
            }

            float inset = (warnHi - warnLo) * 0.12f;
            float nLo = warnLo + inset;
            float nHi = warnHi - inset;
            if (nHi <= nLo)
            {
                nLo = warnLo;
                nHi = warnHi;
            }

            float period = cfg.PeriodSeconds > 5f ? cfg.PeriodSeconds : 5f;
            double baseWave = 0.5 + 0.5 * System.Math.Sin(2.0 * System.Math.PI * t / period + phase);
            float value = (float)(nLo + baseWave * (nHi - nLo));

            // ── ② 事件：按相位错开，每 ExcursionPeriodSeconds 秒来一次短促偏移 ──
            if (cfg.ExcursionPeriodSeconds > 1f && cfg.ExcursionSeconds > 0.1f)
            {
                // 事件时长不得超过半个周期：否则 cycle < dur 恒成立，
                // 退化成「一直越界」，比原来的常态告警还糟。
                float dur = cfg.ExcursionSeconds;
                float half = cfg.ExcursionPeriodSeconds * 0.5f;
                if (dur > half) dur = half;

                double cycle = (t + phase * 3.7) % cfg.ExcursionPeriodSeconds;
                if (cycle < 0.0) cycle += cfg.ExcursionPeriodSeconds;

                if (cycle < dur)
                {
                    // 0 → 1 → 0 的平滑包络，进出都不突兀
                    double k = System.Math.Sin(System.Math.PI * cycle / dur);
                    k *= k;   // 平方 → 上升更快、峰值停留更短

                    // 往「余量更大」的一侧冲，保证一定能越过阈值
                    bool goUp = (hi - nHi) >= (nLo - lo);
                    float target = goUp ? hi : lo;
                    value = (float)(value + (target - value) * k * cfg.ExcursionDepth);
                }
            }

            return Clamp(value, lo, hi);
        }

        /// <summary>自定义 Clamp —— 刻意不用 Mathf，保证本类不依赖 Unity 运行时。</summary>
        private static float Clamp(float v, float lo, float hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }
    }
}

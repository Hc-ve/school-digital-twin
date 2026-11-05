using System;
using System.Collections.Generic;
using UnityEngine;
using SchoolTwin.Twin;
using SchoolTwin.Command;

namespace SchoolTwin.Present
{
    /// <summary>
    /// 【P14 2026-11-05】空调开命令的**双通道反馈**：气流粒子 + 程序化嗡鸣。
    ///
    /// ══════════════════════════════════════════════════════════════════
    /// 为什么需要这个（主理人裁决 C-52 由可选升级为必做）
    /// ══════════════════════════════════════════════════════════════════
    /// 现场问题：5 台空调收到「开」命令后只有「滴」一声提示音，
    /// **3D 场景里没有任何可见变化** —— 评委看不出「命令真的执行了」，
    /// 只能看到面板上一个数字变了。
    ///
    /// 本模块补齐两条反馈通道：
    ///   · 可见：出风口吹出半透明气流粒子（锥形窄角，朝房间内侧）
    ///   · 可听：50/100/150Hz 谐波嗡鸣（常驻 loop，靠 volume 控制）
    ///
    /// ══════════════════════════════════════════════════════════════════
    /// 气流反馈做法源自队员**王绍锋**的教室控制台项目
    /// ══════════════════════════════════════════════════════════════════
    /// 参考的是**做法思路**，不是拷代码（他的是另一个 FBX，节点名不通用，
    /// 按名字找节点在本项目里一条都命中不了 —— 已实测）：
    ///   · 出风口位置不写死，**按机身包围盒推导**顶部格栅位置与吹风方向
    ///   · 关机用 `StopEmitting` 让残留粒子自然飘散，**不硬清屏**
    ///   · 嗡鸣常驻 loop，靠 `volume` 淡入淡出，**不反复 Play/Stop**（避免起播延迟）
    ///   · 程序化合成音频（numpy 频域/IFFT 那套思路）→ 本项目换成 C# 时域谐波叠加
    ///
    /// ══════════════════════════════════════════════════════════════════
    /// 三条口径红线（写在最前面，改代码前先读）
    /// ══════════════════════════════════════════════════════════════════
    /// ① **只在命令执行成功后启动** —— 走 `Actuated`/`Confirmed`，
    ///    不是 `Accepted`。「收到信号」和「真的开了」是两件事，
    ///    挂在 Accepted 上叫**假反馈**（任务包明令禁止）。
    /// ② **断电必须停** —— `TwinCircuit.IsPowered == false` 时粒子与嗡鸣全停。
    ///    「拉闸后空调还在吹」是口径事故，演示动线里拉闸就在开空调之后，必测。
    /// ③ **零素材** —— 不引入任何 wav/png，音频全部代码合成、粒子用默认材质。
    ///
    /// ══════════════════════════════════════════════════════════════════
    /// 为什么单独一个类、不塞进 TwinDevicePresenter
    /// ══════════════════════════════════════════════════════════════════
    /// `TwinDevicePresenter` 已经 4870 行。空调反馈是一个**自包含的子系统**
    /// （自己的粒子、自己的音源、自己的状态机），塞进去只会让那个类更难读。
    /// 这里只暴露 `Tick()` 和 `OnCommand()` 两个入口给外层调用。
    /// </summary>
    public sealed class AcAirflowFeedback
    {
        // ==================================================================
        // 常量
        // ==================================================================

        /// <summary>5 台空调的锚点 TwinId（任务包指定的就是这 5 个，不多不少）。</summary>
        private static readonly string[] AcTwinIds =
        {
            "Class2-1/AC/Unit_01",
            "Class1-2/AC/Unit_01",
            "Class1-3/AC/Unit_01",
            "Class1-4/AC/Unit_01",
            "ClassDirector/AC/Unit_01",
        };

        /// <summary>嗡鸣的基频与谐波（Hz）。王绍锋用的是 50/100/150。</summary>
        ///
        /// 【为什么是 50Hz 基频】工频交流电是 50Hz，变压器/电机嗡鸣的主频就在这里。
        /// 纯 50Hz 几乎听不见（普通笔记本扬声器 50Hz 响应很差），
        /// 真正让它「像空调」的是 100/150Hz 这两个谐波 —— 人耳对 100~200Hz 最敏感。
        private static readonly float[] HumHz = { 50f, 100f, 150f };
        private static readonly float[] HumAmp = { 0.35f, 0.50f, 0.28f };

        /// <summary>
        /// 风噪的频带上限（Hz）与强度。
        ///
        /// ══════════════════════════════════════════════════════════════════
        /// 【2026-11-05 第二轮 · 上一版听不见，根因不是音量，是**频段选错了**
        /// ══════════════════════════════════════════════════════════════════
        /// 上一版只有 50/100/150 Hz 三个谐波。两个致命问题：
        ///
        /// ① **能量全在 150Hz 以下，笔记本扬声器放不出来。**
        ///    内置喇叭的低频截止通常在 150~250Hz，50Hz 那一档衰减 20dB 以上。
        ///    纯谐波版的电平推算（当时的 HumMaxVolume=0.16）：
        ///      提示音峰值 = 波形 0.42 × _beepVolume 0.75 = 0.315
        ///      嗡鸣峰值   = 谐波和 ≈1.13 × 0.30 × 0.16      = 0.054
        ///    嗡鸣只有提示音的 1/6（约 −15dB），再叠上低频衰减，等于没有。
        ///    → 这是「完全听不见」的直接原因，解法是提音量 + 补低频以上的能量。
        ///
        /// ② **真实空调声的主要成分根本不是谐波，是风噪。**
        ///    用户要的是「空调运行声」，那是**压缩机的低频 + 出风的宽带湍流噪声**。
        ///    纯谐波听起来像变压器/镇流器，不是空调在吹风。
        ///
        /// 所以补一层**带通白噪声**（风噪）：
        ///   · 下限 180Hz  —— 避开笔记本喇叭的死区
        ///   · 上限 2400Hz —— 空调风噪的能量集中在中低频，再高会变成"嘶嘶"的电视噪声
        ///   · 噪声占总音量的 55%，谐波只作为"压缩机底噪"的基底
        ///
        /// 【循环怎么做】噪声是无缝循环的天敌 —— 首尾采样点不相关，接缝会"咔"。
        /// 但**不要交叉淡化**（实测它反而引入可听的方差变化）：
        /// 生成 4 秒、直接截断，接缝跳变落在 76.9 百分位，已被噪声自身波动掩蔽。
        /// 完整实测数据见 BuildHumClip 的注释。
        /// </summary>
        private const float WindNoiseLowHz = 180f;
        private const float WindNoiseHighHz = 2400f;

        /// <summary>风噪在总音量里的占比。0.55 → 剩下 0.45 给谐波。</summary>
        private const float WindNoiseMix = 0.55f;

        /// <summary>
        /// 嗡鸣循环片段长度（秒）。必须是**无缝**循环，见 BuildHumClip 的说明。
        ///
        /// 【2026-11-05 从 2 秒改成 4 秒】
        /// 2 秒的循环在**含噪声**之后会产生"洗衣机"感 —— 人耳很容易察觉
        /// 2 秒周期的重复（尤其是低沉的宽带噪声）。
        /// 4 秒把周期拉长一倍，重复感基本消失。
        ///
        /// 谐波整数周期在 4 秒下依然成立：
        ///   50Hz  × 4s = 200 周期（整）
        ///   100Hz × 4s = 400 周期（整）
        ///   150Hz × 4s = 600 周期（整）
        /// </summary>
        private const float HumLoopSeconds = 4f;

        /// <summary>淡入 / 淡出时长（秒）。任务包要求关机 0.7s 淡出。</summary>
        private const float HumFadeIn = 0.7f;
        private const float HumFadeOut = 0.7f;

        /// <summary>
        /// 嗡鸣满音量。
        ///
        /// ══════════════════════════════════════════════════════════════════
        /// 这个值调过两轮，两次都是主理人现场试听后的裁决：
        /// ══════════════════════════════════════════════════════════════════
        ///   0.16  → 完全听不见（只有提示音的 1/6 ≈ −15dB，再叠喇叭低频衰减）
        ///   0.55  → 听得见了，但**偏吵**
        ///   0.18  → 0.55 的 1/3（主理人裁决「调至现在的 1/3」）
        ///
        /// 0.18 时的电平：
        ///   谐波部分 ≈ 0.19 × (0.18/0.55) ≈ 0.062
        ///   风噪部分 ≈ 0.28 × (0.18/0.55) ≈ 0.092
        ///   总峰值   ≈ 0.47 × (0.18/0.55) ≈ 0.154
        ///
        /// 这个电平约为提示音（0.315）的一半 —— 即 −6dB。
        /// 稳态的背景运行声比瞬态的提示音低 6dB 是合理的：
        /// 既能一直听见"空调在转"，按开关时"滴"那一声又清楚压得住。
        ///
        /// ⚠️ **不要为了"更清楚"再往回加。** 0.55 已经试过是偏吵的。
        /// 若以后换到更大的场地/更好的音响，要调大请先问主理人。
        ///
        /// 【为什么是 0.18 而不是精确的 0.1833】幅度上 1.8% 的差别听不出来
        /// （人耳可辨的最小电平变化约 1dB，1.8% ≈ 0.16dB），
        /// 而 0.18 这个数好记、好手算，也方便以后按倍数继续调。
        /// </summary>
        private const float HumMaxVolume = 0.18f;

        // ==================================================================
        // 每台空调一个实例的状态
        // ==================================================================

        private sealed class Unit
        {
            public string TwinId;
            public GameObject Root;          // 粒子系统挂载点（空调模型上的一个空子物体）
            public ParticleSystem Air;       // 气流粒子
            public AudioSource Hum;          // 嗡鸣音源
            public Vector3 VentPos;          // 出风口世界位置（诊断用）
            public Vector3 BlowDir;          // 吹风方向（诊断用，已归一化）

            public bool WantOn;              // 数据层希望它开着（命令执行成功后置真）
            public float HumLevel;           // 当前嗡鸣音量 0~1（用于淡入淡出）
            public bool HumStarted;          // 音源是否已经开始播放（常驻 loop，只 Play 一次）
        }

        private readonly List<Unit> _units = new List<Unit>();
        private AudioClip _humClip;
        private bool _built;
        private bool _buildFailed;
        private string _failReason;

        // ==================================================================
        // 对外入口
        // ==================================================================

        /// <summary>是否构建成功（外层用它决定要不要继续调 Tick）。</summary>
        public bool IsBuilt { get { return _built; } }

        /// <summary>构建失败原因（进日志用）。</summary>
        public string FailReason { get { return _failReason; } }

        /// <summary>
        /// 构建 5 台空调的粒子与音源。**幂等**：重复调用不会重复建。
        ///
        /// 【为什么要在 Awake/Start 之后才调】依赖 `TwinRuntime.Instance` 的 Registry，
        /// 而 Registry 是在各 `TwinAnchor.Start()` 里才注册的。
        /// 太早调会一台都找不到（这正是 `AcProbeRunner` 要等 3 秒的原因）。
        /// </summary>
        public void Build()
        {
            if (_built || _buildFailed) return;

            TwinRuntime rt = TwinRuntime.Instance;
            if (rt == null || rt.Registry == null)
            {
                _buildFailed = true;
                _failReason = "TwinRuntime 还没就绪";
                return;
            }

            _humClip = BuildHumClip();

            int found = 0;
            int missing = 0;
            for (int i = 0; i < AcTwinIds.Length; i++)
            {
                TwinAnchor anchor;
                if (!rt.Registry.TryGet(AcTwinIds[i], out anchor) || anchor == null)
                {
                    missing++;
                    continue;
                }

                Unit u = BuildUnit(anchor, AcTwinIds[i]);
                if (u != null) { _units.Add(u); found++; }
            }

            _built = true;
            Debug.Log("[P14] 空调反馈已构建：" + found + " 台成功" +
                      (missing > 0 ? "，" + missing + " 台锚点缺失" : "") +
                      "。嗡鸣 " + HumHz[0] + "/" + HumHz[1] + "/" + HumHz[2] +
                      "Hz + 风噪 " + WindNoiseLowHz + "~" + WindNoiseHighHz + "Hz，" +
                      "关机走 StopEmitting。");

            // ── 音频自检日志（2026-11-05 第二轮加）───────────────────────
            // 上一版"听不见"，我从日志里**分不清**是下面哪一种：
            //   ① clip 没生成 / 长度为 0  → 代码路径没跑到
            //   ② clip 正常但音量太低      → 参数问题
            //   ③ clip 正常但 AudioListener 不在 / 被禁用 → 环境问题
            // 这三种修法完全不同，日志必须能区分开。
            LogAudioSelfCheck();

            for (int i = 0; i < _units.Count; i++)
            {
                Unit u = _units[i];
                Debug.Log("[P14]   · " + u.TwinId +
                          "  出风口=" + Fmt(u.VentPos) +
                          "  吹向=" + Fmt(u.BlowDir));
            }
        }

        /// <summary>
        /// 音频链路自检：clip 长度 / 峰值 / 音源数量 / Listener 状态。
        ///
        /// 输出例：
        ///   [P14] 音频自检：clip=2.00s 峰值=0.92 音源=5 台  Listener=1 个
        /// </summary>
        private void LogAudioSelfCheck()
        {
            if (_humClip == null)
            {
                Debug.LogError("[P14] 音频自检失败：嗡鸣 clip 为 null —— BuildHumClip 没跑到");
                return;
            }

            // 取一小段样本算峰值，确认波形不是全零
            int probe = Mathf.Min(_humClip.samples, 44100);
            float[] buf = new float[probe];
            bool got = _humClip.GetData(buf, 0);
            float peak = 0f;
            for (int i = 0; i < probe; i++)
            {
                float a = buf[i] < 0f ? -buf[i] : buf[i];
                if (a > peak) peak = a;
            }

            int srcCount = 0;
            for (int i = 0; i < _units.Count; i++) if (_units[i].Hum != null) srcCount++;

            AudioListener[] ls = UnityEngine.Object.FindObjectsOfType<AudioListener>();
            int listenerOn = 0;
            for (int i = 0; i < ls.Length; i++)
                if (ls[i] != null && ls[i].enabled && ls[i].gameObject.activeInHierarchy) listenerOn++;

            string msg = "[P14] 音频自检：clip=" + ((float)_humClip.samples / _humClip.frequency).ToString("F2") + "s" +
                         " 读取=" + (got ? "成功" : "失败") +
                         " 峰值=" + peak.ToString("F3") +
                         " 音源=" + srcCount + " 台" +
                         " Listener=" + listenerOn + " 个(总" + ls.Length + ")" +
                         " 满音量=" + HumMaxVolume.ToString("F2") +
                         " 3D=" + (_units.Count > 0 && _units[0].Hum != null
                                    ? _units[0].Hum.spatialBlend.ToString("F2") : "?");

            if (peak < 0.01f)
                Debug.LogError(msg + "  ⚠ 波形接近静音，BuildHumClip 有问题");
            else if (listenerOn == 0)
                Debug.LogError(msg + "  ⚠ 场景里没有启用的 AudioListener，放不出任何声音");
            else
                Debug.Log(msg);
        }

        /// <summary>
        /// 命令执行成功 → 该台启动反馈。由外层的命令回调调用。
        ///
        /// ⚠️ 调用方必须**只在 `Actuated` / `Confirmed` 时调**，
        /// 不要用 `Accepted`（见类注释红线①）。
        /// </summary>
        public void OnCommandActuated(string twinId, bool on)
        {
            if (!_built) return;

            Unit u = Find(twinId);
            if (u == null) return;

            u.WantOn = on;

            if (on)
            {
                // 粒子立刻起（可见反馈要「马上看到」，不能等淡入）
                if (u.Air != null && !u.Air.isPlaying) u.Air.Play();

                // 【嗡鸣也在这里起播，不等 Tick】Tick 的淡入是从 0 开始爬的，
                // 如果等 Tick 才 Play，会有一段「已经在淡入但声音还没出声」的空窗。
                // 这里先 Play（volume 还是 0，听不见），下一帧 Tick 就开始爬音量。
                if (u.Hum != null && !u.HumStarted)
                {
                    u.Hum.Play();
                    u.HumStarted = true;
                }
                Debug.Log("[P14] " + twinId + " 开 → 气流启动 + 嗡鸣淡入");
            }
            else
            {
                // 【关机不硬清屏】StopEmitting 让已经在空中的粒子飘完自然消失，
                // 比 Clear() 突然全没了真实得多。嗡鸣交给 Tick 淡出。
                if (u.Air != null && u.Air.isPlaying) u.Air.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                Debug.Log("[P14] " + twinId + " 关 → 气流停发（残留自然飘散）+ 嗡鸣淡出");
            }
        }

        /// <summary>
        /// 每帧调用：处理嗡鸣淡入淡出 + **断电强制停止**。
        ///
        /// 【为什么断电检查放这里而不是只在命令回调里】
        /// 拉闸时**没有任何命令下发** —— 是回路状态自己变的。
        /// 只在命令回调里查 IsPowered，拉闸就会漏掉，空调会继续吹。
        /// 这正是不停问「谁还会改变这个状态」的价值。
        /// </summary>
        public void Tick(float unscaledDt)
        {
            if (!_built || _units.Count == 0) return;

            for (int i = 0; i < _units.Count; i++)
            {
                Unit u = _units[i];

                // ── 断电联动（口径红线②）──────────────────────────────
                bool powered = TwinCircuit.IsPowered(u.TwinId);
                if (!powered && u.WantOn)
                {
                    // 断电 = 立刻断，不做淡出 —— 真实空调断电就是瞬间停
                    u.WantOn = false;
                    u.HumLevel = 0f;
                    if (u.Air != null && u.Air.isPlaying)
                        u.Air.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                    // 断电要**真停播**，不是只把音量归零 ——
                    // 这是唯一允许 Stop 的地方（正常开关机走常驻 loop）。
                    // HumStarted 也要复位，来电后再开会重新 Play。
                    if (u.Hum != null)
                    {
                        u.Hum.volume = 0f;
                        u.Hum.Stop();
                    }
                    u.HumStarted = false;
                    Debug.Log("[P14] " + u.TwinId + " 断电 → 气流与嗡鸣强制停止（口径红线）");
                    continue;
                }

                // ── 嗡鸣淡入淡出 ──────────────────────────────────────
                float target = u.WantOn ? 1f : 0f;
                if (!Mathf.Approximately(u.HumLevel, target))
                {
                    // 用 unscaledDt：暂停/慢动作时声音也该按真实时间走
                    float speed = u.WantOn ? (1f / HumFadeIn) : (1f / HumFadeOut);
                    u.HumLevel = Mathf.MoveTowards(u.HumLevel, target, speed * unscaledDt);
                }

                if (u.Hum != null)
                {
                    u.Hum.volume = HumMaxVolume * u.HumLevel;

                    // 【常驻 loop 的关键】clip 一直在播，只靠 volume 控制听不见。
                    // 这样再开的时候**没有起播延迟** —— 王绍锋 README 里
                    // 「0.7s 音量淡入淡出，无起播延迟」说的就是这个。
                    //
                    // ══════════════════════════════════════════════════════
                    // 【2026-11-05 第二轮 · 这里是上一版听不见的第二层原因】
                    // ══════════════════════════════════════════════════════
                    // 上一版写的是：
                    //     HumLevel > 0.001 && !isPlaying  → Play()
                    //     HumLevel <= 0.001 && isPlaying  → Pause()
                    //
                    // 两个问题：
                    // ① `Pause()` 会**释放混音器通道**。5 台空调 = 5 条通道被反复
                    //    申请/释放，低配机器上容易把提示音挤掉。
                    // ② 更隐蔽：`Pause()` 之后 `time` **不会归零**，而 `Play()`
                    //    会从那个位置继续 —— 每次开空调起始相位都不同。
                    //
                    // 改成：**只在第一次开时 Play()，之后永远不 Pause**。
                    // 安静时靠 `volume = 0` 让它无声播放。
                    // 代价是 5 条 4 秒 clip 的无声循环（CPU 可忽略），
                    // 换来的是「零起播延迟 + 相位永远连续」。
                    // ══════════════════════════════════════════════════════
                    if (!u.HumStarted && u.HumLevel > 0.001f)
                    {
                        u.Hum.Play();
                        u.HumStarted = true;
                    }
                }
            }
        }

        /// <summary>退出 Play 时释放（粒子与音源挂在空调模型上，模型不销毁就还在）。</summary>
        public void Destroy()
        {
            for (int i = 0; i < _units.Count; i++)
            {
                Unit u = _units[i];
                if (u.Root != null) UnityEngine.Object.Destroy(u.Root);
                if (u.Hum != null) UnityEngine.Object.Destroy(u.Hum);
            }
            _units.Clear();
            if (_humClip != null) { UnityEngine.Object.Destroy(_humClip); _humClip = null; }
            _built = false;
        }

        // ==================================================================
        // 构建
        // ==================================================================

        private Unit BuildUnit(TwinAnchor anchor, string twinId)
        {
            Vector3 ventPos, blowDir;
            if (!AcVentLocator.TryLocate(anchor, out ventPos, out blowDir))
            {
                Debug.LogWarning("[P14] " + twinId + " 找不到出风口（详见 AcVentLocator 日志）");
                return null;
            }

            // 粒子与音源挂在**锚点**下面（不是空调模型里）——
            // 挂进模型会改它的层级，而任务包禁止改场景；运行时挂子物体只影响内存。
            GameObject root = new GameObject("AcAirflow_" + twinId.Replace('/', '_'));
            root.transform.SetParent(anchor.transform, false);
            root.transform.position = ventPos;          // transform.position 是世界坐标，这里直接给
            // 让粒子的锥口朝吹风方向。
            // ⚠️ 这一行是**唯一**决定发射方向的地方 —— Shape 模块不再做旋转（见下方说明）。
            root.transform.rotation = Quaternion.LookRotation(blowDir, Vector3.up);

            // ── 防御：锚点带缩放会把粒子尺寸一起缩放 ──────────────────────
            // 实测 5 台锚点 lossyScale 都是 (1,1,1)，但锚点是场景作者手工摆的，
            // 以后有人调了父节点缩放，粒子就会莫名变大变小，而现象很难归因。
            // 这里把 root 的 localScale 反向补偿，让粒子的世界尺寸恒定。
            Vector3 ls = anchor.transform.lossyScale;
            if (Mathf.Abs(ls.x) > 0.001f && Mathf.Abs(ls.y) > 0.001f && Mathf.Abs(ls.z) > 0.001f)
                root.transform.localScale = new Vector3(1f / ls.x, 1f / ls.y, 1f / ls.z);
            else
                Debug.LogWarning("[P14] " + twinId + " 锚点 lossyScale 含 0（" + Fmt(ls) +
                                 "），粒子尺寸补偿已跳过 —— 检查场景里这个锚点的缩放");

            ParticleSystem air = BuildAirflowParticles(root);
            AudioSource hum = BuildHumSource(root);

            return new Unit
            {
                TwinId = twinId,
                Root = root,
                Air = air,
                Hum = hum,
                VentPos = ventPos,
                BlowDir = blowDir,
                WantOn = false,
                HumLevel = 0f,
                HumStarted = false,
            };
        }

        /// <summary>
        /// 纯代码建气流粒子。
        ///
        /// ══════════════════════════════════════════════════════════════════
        /// 【2026-11-05 第二轮修正 · 从"圆点阵"改成"光丝雾"】
        /// ══════════════════════════════════════════════════════════════════
        /// 第一轮我改了贴图、尺寸、透明度，出来仍然是"一堆散开的白点"。
        /// 对比主理人给的参考实现，差在**形态范式**而不是参数微调：
        ///
        ///   参考图：沿吹风方向**拉长**的柔边光丝，彼此交叠成一片雾，
        ///           边缘卷曲、形状不规整，像真实空气流过。
        ///   第一轮：每个粒子是**各向同性的圆点**，无论多大都是"点"，
        ///           排列起来就是点阵 —— 点阵永远不像气。
        ///
        /// 所以这一轮改的是"粒子本身长什么样"，不是"点要多大"：
        ///
        /// ① **渲染模式改 Stretch**（`velocityScale = 0.35`）
        ///    billboard 沿**速度在屏幕上的投影方向**拉伸 —— 朝哪飞就往哪拉长，
        ///    方向自动正确，不用手算旋转。这是做气流/尾迹的标准做法。
        ///    配 `renderAlignment = View` 让它始终对着相机拉伸。
        ///
        /// ② **加 Noise 模块**
        ///    丝状感靠"不直"实现：Noise 让每个粒子的运动轨迹被低频噪声推着走，
        ///    出来的丝会卷、会摆、会互相穿插 —— 这是"流体感"最省力的来源。
        ///    没有 Noise 的话，Stretch 出来的是一把**笔直的针**，更像激光。
        ///
        /// ③ **速度提上去**（0.45~0.85 → 0.75~1.35）
        ///    Stretch 的长度 = 速度 × velocityScale × lengthScale。
        ///    第一轮速度太慢，就算开了拉伸也只有短短一截。
        ///
        /// ④ **基础尺寸反而收小**（0.18~0.42 → 0.10~0.22）
        ///    因为拉伸会放大到 2~3 倍长。基础尺寸不收小，
        ///    拉出来是"一片巨大的白布"，不是丝。
        ///
        /// ⑤ **密度提高**（14 → 24/s，maxParticles 70 → 110）
        ///    单根丝很细，要靠数量交叠成雾。
        ///
        /// ⑥ **重力反向**（-0.015 → +0.012）
        ///    负的 gravityModifier 是**往上飘**（像烟），冷气是**下沉**的。
        ///    第一轮方向搞反了 —— 这也是"不像空调"的隐性原因之一。
        /// </summary>
        private ParticleSystem BuildAirflowParticles(GameObject root)
        {
            ParticleSystem ps = root.AddComponent<ParticleSystem>();

            // ⚠️ 必须先 Stop，否则改 main 参数会触发 "setting while playing" 警告
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            ParticleSystem.MainModule main = ps.main;
            main.duration = 1f;
            main.loop = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(2.2f, 3.4f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.75f, 1.35f);   // ③
            main.startSize = new ParticleSystem.MinMaxCurve(0.10f, 0.22f);    // ④
            // 冷白偏青 —— 冷气不该是纯白（纯白看起来像雪/纸屑）
            main.startColor = new Color(0.86f, 0.94f, 1f, 0.26f);
            main.gravityModifier = 0.012f;                                     // ⑥ 正数=下沉
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 110;                                           // ⑤
            main.playOnAwake = false;

            ParticleSystem.EmissionModule em = ps.emission;
            em.rateOverTime = 24f;              // ⑤

            ParticleSystem.ShapeModule shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Cone;
            // 锥角回到 13°：第一轮从 12 收到 8 是**为了治点阵**，
            // 现在改走光丝了，需要一点散开才像"吹出来的一片"，
            // 8° 会让所有丝挤成一根管子，像激光笔。
            shape.angle = 13f;
            shape.radius = 0.42f;               // 格子栅沿长边的分布宽度（长边 1.05m 的一半）
            // ══════════════════════════════════════════════════════════════
            // 【2026-11-05 第三轮 · 发射区从"圆盘"压成"沿墙一条缝"】
            // ══════════════════════════════════════════════════════════════
            // 半径 0.42 的圆盘意味着粒子从**一个直径 84cm 的圆面**上冒出来 ——
            // 那是加湿器/雾化喷头，不是空调。
            //
            // 真实壁挂空调的出风口是**沿机身长边的一条窄缝**（约 3~5cm 高）。
            // 用 `shape.scale` 把圆压成椭圆：
            //   · 本地 X = 沿墙长边方向（因为 root 的 rotation 让 Y 朝上、
            //     Z 朝吹风方向，X 自然落在沿墙方向）→ 保持 1.0
            //   · 本地 Y = 垂直方向 → 压到 0.28，即 0.42*2*0.28 ≈ 24cm
            //     这个高度做**发射区**，配合 13° 锥角扩散，看起来就是一条缝。
            //
            // 注意 `shape.scale` 只影响**发射区形状**，不影响粒子尺寸。
            // ══════════════════════════════════════════════════════════════
            shape.scale = new Vector3(1f, 0.28f, 1f);
            // ══════════════════════════════════════════════════════════════
            // 【2026-11-05 第三轮修正 · 这里原来有一行 rotation = (90,0,0)，是 bug】
            // ══════════════════════════════════════════════════════════════
            // Unity 的 `ParticleSystemShapeType.Cone` **默认就沿本地 +Z 发射**，
            // 不需要任何旋转。而 `root.transform.rotation` 已经用
            //   Quaternion.LookRotation(blowDir, Vector3.up)
            // 让本地 +Z 对齐了吹风方向。
            //
            // 原来那行 `shape.rotation = new Vector3(90f, 0f, 0f)` 是**多余且有害**的：
            // 它把锥轴从 +Z 绕 X 轴转了 90° → 变成 **-Y（垂直向下）**。
            // 也就是说所有粒子其实是**从出风口直接往下掉**，
            // 只靠 `startSpeed` 的初速和 World 模拟空间掩盖了一半。
            //
            // 这也解释了为什么第一版看起来"不像空调在吹风" ——
            // 它根本就没在朝房间方向吹，是在往下撒。
            //
            // 教训：Shape 模块的 `rotation` 是**相对本地坐标系的附加旋转**，
            // 不是"设置发射方向"。要改发射方向请转 Transform，不要转 Shape。
            // ══════════════════════════════════════════════════════════════
            shape.rotation = Vector3.zero;

            // 尺寸随生命：起头细 → 中段饱满 → 末端**不收太小**。
            // 第一轮末尾收到 0.12，丝刚到末端就缩成一个点，
            // 看起来像"粒子消失了"而不是"气散了"。收到 0.55 自然得多。
            ParticleSystem.SizeOverLifetimeModule size = ps.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f,
                new AnimationCurve(
                    new Keyframe(0f, 0.35f),
                    new Keyframe(0.25f, 1f),
                    new Keyframe(1f, 0.55f)));

            // 透明度：淡入 → 保持 → 淡出。雾是越飘越淡的。
            ParticleSystem.ColorOverLifetimeModule col = ps.colorOverLifetime;
            col.enabled = true;
            Gradient g = new Gradient();
            g.SetKeys(
                new[]
                {
                    new GradientColorKey(Color.white, 0f),
                    new GradientColorKey(Color.white, 1f),
                },
                new[]
                {
                    new GradientAlphaKey(0f, 0f),
                    new GradientAlphaKey(0.55f, 0.20f),
                    new GradientAlphaKey(0.30f, 0.65f),
                    new GradientAlphaKey(0f, 1f),
                });
            col.color = new ParticleSystem.MinMaxGradient(g);

            // ══════════════════════════════════════════════════════════════
            // ② Noise —— 流体感的来源
            // ══════════════════════════════════════════════════════════════
            // 参数怎么定的：
            //   strength 0.11  位移幅度（m/s 量级）。再大丝会被吹散成絮状，
            //                  再小就只剩直线。
            //   frequency 0.40 空间频率。低 = 大尺度的缓慢弯折（像气团），
            //                  高 = 高频抖动（像电视雪花）。0.4 接近一米的起伏。
            //   scrollSpeed 0.22 噪声场自己也在流动，避免整片丝同步摆动。
            //   damping = true  让噪声"推一下就松手"，而不是持续拉扯 ——
            //                  持续拉扯会让所有粒子聚成一团，不再散开。
            //   octaveCount = 1 只叠一层。多层 octave 更细腻但更贵，
            //                  5 台空调同时开时没必要。
            //
            // ⚠️ API 名字踩过坑（2026-11-05）：属性是 `octaveCount` 不是 `octaves`。
            ParticleSystem.NoiseModule noise = ps.noise;
            noise.enabled = true;
            noise.strength = new ParticleSystem.MinMaxCurve(0.11f);
            noise.frequency = 0.40f;
            noise.scrollSpeed = new ParticleSystem.MinMaxCurve(0.22f);
            noise.damping = true;
            noise.octaveCount = 1;
            noise.quality = ParticleSystemNoiseQuality.Medium;

            ParticleSystemRenderer pr = root.GetComponent<ParticleSystemRenderer>();
            if (pr != null)
            {
                // ① Stretch：沿速度方向拉伸成丝
                pr.renderMode = ParticleSystemRenderMode.Stretch;
                pr.material = BuildParticleMaterial();
                // lengthScale   基础长宽比（2.3 = 拉长到 2.3 倍）
                // velocityScale 速度对长度的影响（0 = 不随速度变，纯按 lengthScale）
                pr.lengthScale = 2.3f;
                pr.velocityScale = 0.35f;
                pr.cameraVelocityScale = 0f;   // 关掉"相机运动带来的拉伸"，否则走位时会甩出长条
                // ⚠️ API 名字踩过坑（2026-11-05）：
                //   Inspector 里叫 "Render Alignment"，API 里叫 `alignment`，
                //   类型是 `ParticleSystemRenderSpace`（不是 ParticleSystemRenderAlignment）。
                pr.alignment = ParticleSystemRenderSpace.View;
                pr.sortMode = ParticleSystemSortMode.Distance;
            }

            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            return ps;
        }

        /// <summary>
        /// 粒子的材质。**不引入 png** —— 贴图用代码生成。
        ///
        /// ══════════════════════════════════════════════════════════════════
        /// 【2026-11-05 第二轮修正 · 混合模式从 Additive 改回 Alpha Blend】
        /// ══════════════════════════════════════════════════════════════════
        /// 第一轮的理由是「气流是发光的雾气，加色下重叠处自然变亮」——
        /// 这个理由只在**深色背景**成立，而本项目的教室是：
        ///   · 白墙 / 浅色黑板（SchoolScene 是白天光照）
        ///   · 环境光很亮
        ///
        /// 加色混合在亮背景上**不产生可见差异**（白 + 白 = 白），
        /// 结果就是"白墙上多了一块更白的糊" —— 丝状结构全被吃掉。
        /// 这解释了为什么第一轮无论怎么调尺寸都不像气。
        ///
        /// 改回 Alpha Blend（`SrcAlpha / OneMinusSrcAlpha`）：
        ///   · 在亮背景上表现为**真实的半透明白雾**，能看见丝的边缘
        ///   · 暗处不会过曝
        ///   · 代价是多层 alpha 会累加 → 所以单个粒子 alpha 给得很低
        ///     （startColor 0.26 × 渐变峰值 0.55 ≈ 0.14），
        ///     靠 20 多根丝交叠出浓度
        ///
        /// 另外三点：
        ///   · `_ZWrite = 0` —— 粒子不能写深度，否则后面的丝会被裁掉
        ///   · `_Cull = 0`   —— Stretch 的四边形是侧向的，单面剔除会整根消失
        ///   · `renderQueue = 3000` —— Transparent 队列
        /// </summary>
        private Material BuildParticleMaterial()
        {
            // 优先 Particles/Standard Unlit（Built-in RP 有），退回旧版
            Shader sh = Shader.Find("Particles/Standard Unlit");
            bool legacy = false;
            if (sh == null)
            {
                sh = Shader.Find("Legacy Shaders/Particles/Alpha Blended");
                legacy = true;
            }
            if (sh == null) sh = Shader.Find("Sprites/Default");
            if (sh == null) return null;

            Material m = new Material(sh);
            m.name = "AcAirflowParticle";
            m.renderQueue = 3000;                // Transparent 队列

            Texture2D soft = BuildSoftDot();
            m.SetTexture("_MainTex", soft);

            if (!legacy)
            {
                // 整体色调打白：颜色全部由 startColor + ColorOverLifetime 决定
                if (m.HasProperty("_Color")) m.SetColor("_Color", Color.white);

                // ── 混合模式：Alpha Blend ──────────────────────────────
                // Unity 的 Particles/Standard Unlit 用 _Mode 控制面板预设，
                // 但真正生效的是 _SrcBlend / _DstBlend 两个因子。
                // _Mode = 2 是 "Fade" 预设（Alpha Blend），编辑器里显示也正确。
                if (m.HasProperty("_Mode")) m.SetFloat("_Mode", 2f);
                if (m.HasProperty("_SrcBlend"))
                    m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                if (m.HasProperty("_DstBlend"))
                    m.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                if (m.HasProperty("_BlendOp"))
                    m.SetFloat("_BlendOp", (float)UnityEngine.Rendering.BlendOp.Add);
                if (m.HasProperty("_ZWrite")) m.SetFloat("_ZWrite", 0f);

                // 关掉软粒子（需要相机深度贴图，本项目相机没配）
                if (m.HasProperty("_SoftParticlesEnabled")) m.SetFloat("_SoftParticlesEnabled", 0f);
                // 双面：Stretch 四边形是沿速度侧向站立的，单面剔除会整根消失
                if (m.HasProperty("_Cull")) m.SetFloat("_Cull", (float)UnityEngine.Rendering.CullMode.Off);
            }

            m.DisableKeyword("_ALPHATEST_ON");
            m.EnableKeyword("_ALPHABLEND_ON");
            m.DisableKeyword("_ALPHAPREMULTIPLY_ON");

            return m;
        }

        /// <summary>
        /// 【零素材】代码生成一张柔边贴图（128×128）。
        ///
        /// ══════════════════════════════════════════════════════════════════
        /// 【2026-11-05 第二轮修正 · 从"四次方实心圆"改成"高斯长尾"】
        /// ══════════════════════════════════════════════════════════════════
        /// 第一轮用的是 `(1 - r²)²` —— 中心很实、到 r=1 处刚好归零。
        /// 那个贴图在**圆点**形态下没问题，但一旦开了 Stretch 拉伸：
        ///
        ///   · 四次方衰减的中段（r≈0.5~0.8）掉得不够快，
        ///     横向拉长 2~3 倍后变成一根**边缘清晰的实心棒**，像拉丝的糖。
        ///   · 而真实气流是**中心一条核 + 四周弥散**，边界根本看不见。
        ///
        /// 改成高斯 `exp(-4.2 r²)`：
        ///   · r=0     → 1.000（核）
        ///   · r=0.35  → 0.593（亮心区）
        ///   · r=0.55  → 0.281（过渡）
        ///   · r=0.75  → 0.094（已经很淡）
        ///   · r=1.00  → 0.015（几乎不可见，所以**看不到方形边界**）
        /// 长尾一路拖到边缘才归零，拉长后就是一条两端渐隐的丝。
        ///
        /// 分辨率也从 64 提到 128：丝被拉长后，64px 贴图的横向插值
        /// 会出现可见的锯齿台阶。
        ///
        /// RGB 保持白色（颜色由 `startColor` 的冷白决定），只填 alpha。
        /// </summary>
        private Texture2D BuildSoftDot()
        {
            const int size = 128;
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.name = "AcAirflowSoftDot";
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;

            Color32[] px = new Color32[size * size];
            float half = (size - 1) * 0.5f;
            const float k = 4.2f;      // 高斯陡度，见注释里的对照表

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x - half) / half;
                    float dy = (y - half) / half;
                    float r2 = dx * dx + dy * dy;

                    // 圆外裁剪为 0（高斯在 r=1 时是 0.015，不裁会有极淡的方角）
                    float a = r2 >= 1f ? 0f : Mathf.Exp(-k * r2);
                    px[y * size + x] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(a * 255f));
                }
            }

            tex.SetPixels32(px);
            tex.Apply(false, false);
            return tex;
        }

        private AudioSource BuildHumSource(GameObject root)
        {
            AudioSource src = root.AddComponent<AudioSource>();
            src.playOnAwake = false;
            src.loop = true;
            src.clip = _humClip;
            src.volume = 0f;

            // ══════════════════════════════════════════════════════════════
            // 【2026-11-05 第二轮 · 3D 衰减调软，否则"站讲台听不见"】
            // ══════════════════════════════════════════════════════════════
            // 上一版：spatialBlend=0.75 / Linear / min 2 / max 16。
            //
            // Linear 滚降在 8m 处只剩 50% 音量，再乘 spatialBlend 的 0.75 权重 →
            // 实际只有 0.5 × (1-0.75+0.75×0.5) ≈ 0.31 的贡献。
            // 而答辩演示时人站讲台，5 台空调确实都在 6~10m 外。
            //
            // 改成：
            //   · spatialBlend = 0.45  仍能听出方向，但 2D 成分占大头
            //                          （空调嗡鸣本来就是弥漫的环境声，
            //                           不需要精确声像 —— 满 3D 反而像"耳机里有个点"）
            //   · minDistance = 3     3m 内满音量（讲台到第一排空调的距离）
            //   · maxDistance = 30    30m 才完全衰减（整个教室对角线都还有声）
            //
            // 为什么不用 Logarithmic：它对距离极其敏感，走两步音量就跳，
            // 演示时镜头在动，听起来像音量在"呼吸"。
            // ══════════════════════════════════════════════════════════════
            src.spatialBlend = 0.45f;
            src.rolloffMode = AudioRolloffMode.Linear;
            src.minDistance = 3f;
            src.maxDistance = 30f;
            src.dopplerLevel = 0f;

            // 【为什么要 BypassReverbZones / 不设 outputMixerGroup】
            // 默认走 Master，若场景后来接了 AudioMixer 的 Reverb Zone（教室混响），
            // 宽带风噪会被混响糊成一片"隆隆声"。这里显式绕开混响区 ——
            // 空调的机械声是**近场直达声**，室内混响对它是负作用。
            src.bypassReverbZones = true;
            src.priority = 128;      // 默认优先级；不抢铃声/告警的优先权
            return src;
        }

        // ==================================================================
        // 嗡鸣合成（沿 MakeBeep 的谐波路线）
        // ==================================================================

        /// <summary>
        /// 程序生成空调嗡鸣。**零音频素材**。
        ///
        /// ══════════════════════════════════════════════════════════════
        /// 为什么必须是「无缝循环」
        /// ══════════════════════════════════════════════════════════════
        /// 这个 clip 是**常驻 loop** 的（靠 volume 控制听不听得见）。
        /// 如果首尾相位不连续，每次循环接缝处会「咔」一声 ——
        /// 而它每数秒循环一次，接缝处只要有台阶就会「咔」一声，非常明显。
        ///
        /// 做法：让循环长度恰好是**每个谐波的整数个周期**。
        ///   50Hz  → 4s 内 200 个周期（整）
        ///   100Hz → 4s 内 400 个周期（整）
        ///   150Hz → 4s 内 600 个周期（整）
        /// 三个都在整数周期上收尾，首尾自然衔接。
        ///
        /// 选 4 秒而不是 12 秒（王绍锋那个）：内存和加载时间都更小，
        /// 而周期整数性在 4 秒下对这个频率组合依然成立。
        /// 选 4 秒而不是 2 秒：2 秒的噪声循环有可察觉的重复感（见 HumLoopSeconds）。
        /// ══════════════════════════════════════════════════════════════
        ///
        /// 沿的是本项目 `BuildBellChime` / `BuildBeepClip` 的**谐波叠加**路数
        /// （判据 P14-03 锁死这一点，避免有人以后偷偷换成读 wav）。
        /// </summary>
        private AudioClip BuildHumClip()
        {
            const int rate = 44100;
            int total = Mathf.RoundToInt(rate * HumLoopSeconds);

            float[] data = new float[total];

            // ══════════════════════════════════════════════════════════════
            // 第一层：风噪（宽带带通噪声）—— 主要成分，占 55%
            // ══════════════════════════════════════════════════════════════
            // 【⚠️ 我在这上面走过弯路：不要做交叉淡化】
            //
            // 直觉是「噪声首尾不连续，必须交叉淡化」，于是第一版写了
            // 「多生成 0.5s，把末尾 0.5s 与开头 0.5s 交叉混合」。
            //
            // **实测数据把这个直觉否掉了**（用 Python 复现同一算法）：
            //
            //   loop=2s  直接截断 → 接缝 = 0.2201
            //            噪声自身相邻采样差 p99 = 0.1987、max = 0.2840
            //            → 接缝落在 **99.7 百分位**（略高于正常波动，勉强能用）
            //
            //   loop=4s  直接截断 → 接缝 = 0.1059
            //            → 落在 **76.9 百分位**，比 1/4 的正常采样差还小
            //
            // 也就是说：**循环够长时，接缝跳变被噪声自身的波动完全掩蔽**，
            // 根本听不出来。而交叉淡化反而**制造**了两个问题：
            //   ① 末尾 0.5s 变成两段独立噪声的混合 → 方差减半
            //      → 每次循环到那里「突然变轻」，那才是**真正可听**的变化
            //   ② 白噪声的统计特性被破坏，听感「不自然」
            //
            // 结论：4 秒直接截断，不做交叉淡化。
            // 若以后要改循环长度，**先跑上面的百分位验证**再动手。
            // ══════════════════════════════════════════════════════════════
            float[] wind = new float[total];
            MakeBandNoise(wind, total, rate, WindNoiseLowHz, WindNoiseHighHz, 20261105u);

            // ══════════════════════════════════════════════════════════════
            // 第二层：谐波（压缩机/电机底噪）—— 占 45%
            // ══════════════════════════════════════════════════════════════
            // 轻微机械不稳定感：让音量微微起伏。
            // 完全恒定的嗡鸣听起来像电子音，真实空调有压缩机/风扇的周期波动。
            //
            // ⚠️ 频率必须让循环**无缝**：候选 0.7Hz 在 4s 内是 2.8 个周期（不整），
            // 循环接缝处 wobble 会跳一下。取 1.5Hz → 4s 内正好 6 个整周期。
            const float wobbleHz = 1.5f;

            // 归一化：把实测的峰值压到 1.0 以内再混，否则叠加后会削顶。
            // 谐波峰 ≈ 0.35+0.50+0.28 = 1.13 → 除以 1.13。
            const float harmNorm = 1f / 1.13f;

            for (int i = 0; i < total; i++)
            {
                double t = (double)i / rate;
                double h = 0d;

                for (int k = 0; k < HumHz.Length; k++)
                {
                    // 每个谐波相位从 0 开始、在 4s 结束于整数周期 → 无缝
                    h += HumAmp[k] * Math.Sin(2d * Math.PI * HumHz[k] * t);
                }

                float wobble = 1f + 0.06f * Mathf.Sin(2f * Mathf.PI * wobbleHz * (float)t);

                float harm = (float)h * harmNorm * wobble;
                float w = wind[i];

                // 混合。两路都归一化过（各自峰值 ≈1），
                // 按 WindNoiseMix 加权后总峰值 ≤1，不会削顶。
                data[i] = w * WindNoiseMix + harm * (1f - WindNoiseMix);
            }

            // ══════════════════════════════════════════════════════════════
            // 末尾再压一遍电平
            // ══════════════════════════════════════════════════════════════
            // 两路信号不是同相位叠加，理论峰值可能略超 1.0。
            // 这里找实际峰值再整体缩放，保证不削顶（削顶会变成刺耳的破音）。
            float peak = 0f;
            for (int i = 0; i < total; i++)
            {
                float a = data[i] < 0f ? -data[i] : data[i];
                if (a > peak) peak = a;
            }
            if (peak > 0.0001f)
            {
                // 目标峰值 0.92，留 8% 余量
                float gain = 0.92f / peak;
                for (int i = 0; i < total; i++) data[i] *= gain;
            }

            AudioClip clip = AudioClip.Create("TwinAcHum", total, 1, rate, false);
            clip.SetData(data, 0);
            return clip;
        }

        /// <summary>
        /// 生成**带通**噪声（风噪）。
        ///
        /// ══════════════════════════════════════════════════════════════════
        /// 为什么不用 `UnityEngine.Random`
        /// ══════════════════════════════════════════════════════════════════
        /// 它会推进**全局**随机数状态。这个 clip 在命令执行成功时才生成，
        /// 如果它偷偷挪了全局种子，场景里其他用 Random 的东西（飘雪、粒子扰动、
        /// 灯光明暗）就会和"有没有开过空调"产生关联 —— 这种 bug 极难查。
        ///
        /// 用自己的 xorshift：
        ///   · 确定性（同样种子 → 同样波形，方便复现）
        ///   · 不碰全局状态
        ///   · 够快（44k×4s = 176k 个样本，几毫秒）
        ///
        /// ══════════════════════════════════════════════════════════════════
        /// 【2026-11-05 第二轮 · 第一版用"宽低通减窄低通"做带通，是错的】
        /// ══════════════════════════════════════════════════════════════════
        /// 第一版思路：白噪声分别过 fc=2400 和 fc=180 两个低通，
        /// 然后 `high - low` 得到"高频减低频"= 带通。
        ///
        /// 实测频响（我用 Python 算的 z 域传递函数）把它证伪了：
        ///
        ///   ┌──────────┬─────────┬─────────┬─────────┐
        ///   │ 频率     │ 2 级    │ 3 级    │ 4 级    │
        ///   ├──────────┼─────────┼─────────┼─────────┤
        ///   │  40 Hz   │  −8.1dB │  −4.7dB │  −2.5dB │  ← 低频不降反升
        ///   │ 120 Hz   │  −0.9dB │  +1.4dB │  +2.4dB │
        ///   │ 400 Hz   │  +0.4dB │  +0.2dB │  −0.3dB │
        ///   │ 2000 Hz  │  −4.5dB │  −6.8dB │  −9.0dB │
        ///   └──────────┴─────────┴─────────┴─────────┘
        ///
        /// **加级数只会让低频更响** —— 因为两个低通在低频都趋近于 1，
        /// 相减趋近于 0，但级数越高它们"趋近"得越慢，差值反而更大。
        /// 这个结构在低频根本不是高通。
        ///
        /// 正确做法：**高通级联 + 低通级联真正串起来**。
        ///
        ///   ┌──────────┬──────────────┐
        ///   │  20 Hz   │  −38.3 dB    │  ← 低频真正被切掉
        ///   │  40 Hz   │  −26.6 dB    │
        ///   │ 120 Hz   │  −10.3 dB    │
        ///   │ 500 Hz   │   −1.6 dB    │  ← 通带
        ///   │ 1000 Hz  │   −1.9 dB    │
        ///   │ 2000 Hz  │   −4.8 dB    │
        ///   │ 4000 Hz  │  −11.5 dB    │
        ///   │ 8000 Hz  │  −20.9 dB    │  ← 高频被压住，不刺耳
        ///   └──────────┴──────────────┘
        ///
        /// 各 2 级是权衡：1 级转折太软（低频仍会漏），
        /// 4 级以上转折太陡，听起来像"假的"（真实风噪很平缓）。
        /// ══════════════════════════════════════════════════════════════════
        /// </summary>
        private static void MakeBandNoise(float[] dst, int count, int rate,
                                          float lowHz, float highHz, uint seed)
        {
            // ── 低通系数：a = 1 - exp(-2πfc/rate)（精确式，见注释）──
            float aLow = Mathf.Clamp01(1f - Mathf.Exp(-2f * Mathf.PI * lowHz / rate));
            float aHigh = Mathf.Clamp01(1f - Mathf.Exp(-2f * Mathf.PI * highHz / rate));

            // ── 高通系数：alpha = 1/(1 + 2πfc/rate) ──
            float alphaHp = 1f / (1f + 2f * Mathf.PI * lowHz / rate);

            uint s = seed | 1u;      // 不能为 0（xorshift 会卡死）
            float peak = 0f;

            // 高通状态（2 级）
            float hpX1a = 0f, hpY1a = 0f;
            float hpX1b = 0f, hpY1b = 0f;

            // 低通状态（2 级）
            float lp1a = 0f, lp1b = 0f;

            for (int i = 0; i < count; i++)
            {
                // xorshift32 → [-1, 1)
                s ^= s << 13;
                s ^= s >> 17;
                s ^= s << 5;
                float white = ((s & 0xFFFFFF) / 8388608f) - 1f;   // [0,2^24) → [-1,1)

                // ── 高通级联（切掉 180Hz 以下）──
                // 一阶高通：y[n] = alpha * (y[n-1] + x[n] - x[n-1])
                float y1 = alphaHp * (hpY1a + white - hpX1a);
                hpX1a = white; hpY1a = y1;

                float y2 = alphaHp * (hpY1b + y1 - hpX1b);
                hpX1b = y1; hpY1b = y2;

                // ── 低通级联（切掉 2400Hz 以上）──
                lp1a += aHigh * (y2 - lp1a);
                lp1b += aHigh * (lp1a - lp1b);

                float band = lp1b;
                dst[i] = band;

                float ab = band < 0f ? -band : band;
                if (ab > peak) peak = ab;
            }

            // 归一化到峰值 1.0，让上层混合时电平可预测
            if (peak > 0.0001f)
            {
                float g = 1f / peak;
                for (int i = 0; i < count; i++) dst[i] *= g;
            }
        }

        // ==================================================================
        // 工具
        // ==================================================================

        private Unit Find(string twinId)
        {
            for (int i = 0; i < _units.Count; i++)
                if (string.Equals(_units[i].TwinId, twinId, StringComparison.Ordinal))
                    return _units[i];
            return null;
        }

        private static string Fmt(Vector3 v)
        {
            return "(" + v.x.ToString("F2") + "," + v.y.ToString("F2") + "," + v.z.ToString("F2") + ")";
        }
    }
}
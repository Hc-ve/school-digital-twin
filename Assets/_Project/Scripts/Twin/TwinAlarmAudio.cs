using System;
using System.Collections.Generic;
using UnityEngine;
using SchoolTwin.Core;

namespace SchoolTwin.Twin
{
    /// <summary>
    /// 告警音效：订阅 TwinAlarmCenter 的事件，按严重度播不同提示音。
    ///
    /// ── 为什么程序化生成，不用音频素材 ──────────────────────────────
    /// ① 零资源依赖：不用去找 mp3/wav，也不会因为素材丢失或路径变更而静默失效；
    /// ② 参数可调：改一个频率数字就能换音色，不用重新导入音频；
    /// ③ 体积极小：三段音合计约 40KB 内存，不占仓库。
    ///
    /// ── 三种音的设计 ──────────────────────────────────────────────
    /// Offline（440Hz 单响，0.42s）—— 最低沉。「我瞎了」，最该被听见。
    /// Alarm  （880Hz 双响，0.16s×2）—— 急促的「叮叮」，需要处理。
    /// Warning（660Hz 单响，0.11s，音量 0.55）—— 轻提示，默认不播。
    ///
    /// ── 防叠加：三层抑制（2026-10-22 重做）────────────────────────
    /// 原来的实现只有「两条告警之间最少隔 0.6 秒」，在 32 个点位、Mock 异常率 ~9% 的情况下
    /// 等于没拦 —— 用户的原话是「此起彼伏，一直在响」。
    ///
    /// 这其实是工业报警系统里的经典问题，叫 **alarm flood**（告警泛滥）。
    /// ISA 18.2 给出的思路不是「把声音关掉」，而是**限流 + 去重**，所以这里三层：
    ///   ① **同一台设备冷却**：同一 TwinId 在 `_perPointCooldownSeconds` 内不再响
    ///      （设备自己抖一下、报警/恢复反复横跳，不该反复吵）；
    ///   ② **全局最短间隔**：任何两声之间至少 `MinGapFloor` 秒；
    ///   ③ **每分钟预算**：60 秒内最多 `_maxRingsPerMinute` 声，超了静默丢弃。
    ///
    /// 全部是**丢弃不排队**：排队会让延迟越积越长，最后声音和画面完全错位。
    /// 被丢掉的数量记在 `SuppressedCount` 里，可以按 `H` 在告警历史里对照。
    ///
    /// ⚠️ AudioClip.Create + SetData 必须在主线程调用，所以放在 Awake。
    /// </summary>
    [AddComponentMenu("SchoolTwin/告警音效 (TwinAlarmAudio)")]
    [DisallowMultipleComponent]
    public class TwinAlarmAudio : MonoBehaviour
    {
        [Header("音量与开关")]
        [Range(0f, 1f)]
        [SerializeField] private float _volume = 0.35f;

        [Tooltip("全局静音")]
        [SerializeField] private bool _mute = false;

        [Tooltip("Warning 是否也发声。默认关 —— 警告较频繁，响起来会烦")]
        [SerializeField] private bool _soundOnWarning = false;

        [Header("防叠加")]
        [Tooltip("两条告警之间的最短间隔（秒）。多点位同时告警时只播第一声")]
        [SerializeField] private float _minGapSeconds = 0.6f;

        [Header("告警泛滥抑制（ISA 18.2 的 alarm flood 思路）")]
        [Tooltip("同一台设备在这个时间内不重复发声（秒）。防止「报警→恢复→再报警」反复横跳")]
        [SerializeField] private float _perPointCooldownSeconds = 45f;

        [Tooltip("60 秒内最多响几声。0 = 不限制。演示时 6 声已经够提醒了")]
        [SerializeField] private int _maxRingsPerMinute = 6;

        /// <summary>
        /// 全局最短间隔的**硬下限**。
        ///
        /// 【为什么要有它】`_minGapSeconds` 在场景里序列化成 0.6（P7 定的，那时只有 32 个点位、
        /// 异常率低）。序列化值优先于脚本默认值（99 号清单第 1 条），所以把默认值改大**不生效**。
        /// 而 0.6 秒的间隔在现在的数据量下等于没拦 —— 那就别指望 Inspector 里的数字，
        /// 在这里夹一个下限，让「无论 Inspector 写多少都不可能低于 6 秒」成为结构。
        /// </summary>
        private const float MinGapFloor = 6f;

        private AudioSource _source;
        private AudioClip _clipAlarm;
        private AudioClip _clipOffline;
        private AudioClip _clipWarning;

        private TwinAlarmCenter _center;
        private float _lastPlayTime = -999f;
        private int _suppressedCount;
        private bool _warnedNoCenter;

        /// <summary>同一台设备上次发声的时间。</summary>
        private readonly Dictionary<string, float> _lastRingAt =
            new Dictionary<string, float>(32, StringComparer.Ordinal);

        /// <summary>最近 60 秒内的发声时刻（用于「每分钟预算」）。</summary>
        private readonly Queue<float> _ringTimes = new Queue<float>(16);

        /// <summary>被抑制机制丢弃的次数（调试用）。</summary>
        public int SuppressedCount { get { return _suppressedCount; } }

        /// <summary>静音开关。演示时可以用菜单一键静音，不用去 Inspector 里翻。</summary>
        public bool Muted
        {
            get { return _mute; }
            set { _mute = value; }
        }

        private void Awake()
        {
            BuildClips();
            EnsureSource();
        }

        private void Start()
        {
            Subscribe();
        }

        private void OnDisable()
        {
            Unsubscribe();
        }

        private void Update()
        {
            // TwinAlarmCenter 可能比本组件晚创建（或场景重载后重建），
            // 这里做一次兜底重连。检查本身是空引用比较，开销可忽略。
            if (_center != null) return;
            Subscribe();

            // 只警告一次：避免每帧刷屏，也避免"静默失效"（音效不响却毫无提示）
            if (_center == null && !_warnedNoCenter)
            {
                _warnedNoCenter = true;
                Debug.LogWarning("[Twin] TwinAlarmAudio 找不到 TwinAlarmCenter，音效不会触发。" +
                                 "请把 TwinAlarmCenter 挂到 [TwinRuntime] 上。");
            }
        }

        // ---------------------------------------------------------------- 订阅

        private void Subscribe()
        {
            if (_center != null) return;
            _center = TwinAlarmCenter.Instance;
            if (_center == null) return;

            _center.AlarmRaised += OnAlarmRaised;
            _center.AlarmCleared += OnAlarmCleared;
        }

        private void Unsubscribe()
        {
            if (_center == null) return;
            _center.AlarmRaised -= OnAlarmRaised;
            _center.AlarmCleared -= OnAlarmCleared;
            _center = null;
        }

        private void OnAlarmRaised(TwinAlarmCenter.AlarmEvent evt)
        {
            if (_mute || _source == null) return;
            if (evt.Status == TwinStatus.Warning && !_soundOnWarning) return;

            float now = Time.unscaledTime;
            string twinId = evt.Anchor != null ? evt.Anchor.TwinId : null;

            // ① 同一台设备冷却：报警/恢复反复横跳时不该反复吵
            if (!string.IsNullOrEmpty(twinId))
            {
                float lastAt;
                if (_lastRingAt.TryGetValue(twinId, out lastAt) &&
                    now - lastAt < Mathf.Max(0f, _perPointCooldownSeconds))
                {
                    _suppressedCount++;
                    return;
                }
            }

            // ② 全局最短间隔：0.6 秒等于没拦，所以夹一个硬下限
            if (now - _lastPlayTime < Mathf.Max(MinGapFloor, _minGapSeconds))
            {
                _suppressedCount++;
                return;
            }

            // ③ 每分钟预算：告警泛滥时宁可少响，也不能响成一片
            while (_ringTimes.Count > 0 && now - _ringTimes.Peek() > 60f) _ringTimes.Dequeue();
            if (_maxRingsPerMinute > 0 && _ringTimes.Count >= _maxRingsPerMinute)
            {
                _suppressedCount++;
                return;
            }

            _lastPlayTime = now;
            _ringTimes.Enqueue(now);
            if (!string.IsNullOrEmpty(twinId)) _lastRingAt[twinId] = now;

            AudioClip clip = ClipFor(evt.Status);
            if (clip == null) return;

            _source.volume = Mathf.Clamp01(_volume);
            _source.PlayOneShot(clip);
        }

        private void OnAlarmCleared(TwinAnchor anchor)
        {
            // 恢复时不发声 —— 安静本身就是「恢复正常」的信号。
            // 如果以后想要「叮咚」的恢复音，在这里加即可。
        }

        private AudioClip ClipFor(TwinStatus status)
        {
            switch (status)
            {
                case TwinStatus.Offline: return _clipOffline;
                case TwinStatus.Alarm: return _clipAlarm;
                case TwinStatus.Warning: return _clipWarning;
                default: return _clipAlarm;
            }
        }

        // ---------------------------------------------------------------- 音频生成

        private void EnsureSource()
        {
            _source = GetComponent<AudioSource>();
            if (_source == null) _source = gameObject.AddComponent<AudioSource>();

            _source.playOnAwake = false;
            _source.loop = false;
            _source.spatialBlend = 0f;            // 2D：不随相机距离衰减
            _source.volume = Mathf.Clamp01(_volume);
            _source.ignoreListenerPause = true;   // 暂停时仍能提醒
        }

        private void BuildClips()
        {
            _clipOffline = MakeBeep("TwinOffline", 440f, 0.42f, 1, 0f, 1.0f);
            _clipAlarm = MakeBeep("TwinAlarm", 880f, 0.16f, 2, 0.09f, 1.0f);
            _clipWarning = MakeBeep("TwinWarning", 660f, 0.11f, 1, 0f, 0.55f);
        }

        /// <summary>
        /// 生成一段「叮」声：基频 + 二次谐波，4ms 线性起音 + 指数衰减。
        ///
        /// 为什么要有起音段：直接开方波会在波形上产生阶跃，扬声器会发出「咔哒」声。
        /// 为什么要二次谐波：纯正弦听起来像测试音，加一点谐波才像设备提示音。
        /// </summary>
        private static AudioClip MakeBeep(string name, float freqHz, float durationSeconds,
                                          int pulseCount, float gapSeconds, float gain)
        {
            const int sampleRate = 44100;

            if (pulseCount < 1) pulseCount = 1;
            if (durationSeconds <= 0f) durationSeconds = 0.1f;

            float totalLen = pulseCount * durationSeconds + (pulseCount - 1) * gapSeconds;
            int totalSamples = Mathf.Max(1, Mathf.CeilToInt(sampleRate * totalLen));
            float[] data = new float[totalSamples];

            int pulseLen = Mathf.Max(1, Mathf.RoundToInt(sampleRate * durationSeconds));
            int gapLen = Mathf.Max(0, Mathf.RoundToInt(sampleRate * gapSeconds));

            for (int p = 0; p < pulseCount; p++)
            {
                int start = p * (pulseLen + gapLen);
                for (int i = 0; i < pulseLen; i++)
                {
                    int idx = start + i;
                    if (idx >= totalSamples) break;

                    float t = i / (float)sampleRate;
                    float progress = i / (float)pulseLen;

                    float attack = Mathf.Clamp01(t / 0.004f);
                    float decay = Mathf.Exp(-5f * progress);
                    float env = attack * decay;

                    float s = Mathf.Sin(2f * Mathf.PI * freqHz * t)
                            + 0.35f * Mathf.Sin(2f * Mathf.PI * freqHz * 2f * t);

                    data[idx] = s * env * gain * 0.5f;
                }
            }

            AudioClip clip = AudioClip.Create(name, totalSamples, 1, sampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }

        // ---------------------------------------------------------------- 调试

        [ContextMenu("试听：告警音")]
        private void PlayAlarmPreview()
        {
            if (_source == null) EnsureSource();
            if (_clipAlarm == null) BuildClips();
            _source.volume = Mathf.Clamp01(_volume);
            _source.PlayOneShot(_clipAlarm);
        }

        [ContextMenu("试听：离线音")]
        private void PlayOfflinePreview()
        {
            if (_source == null) EnsureSource();
            if (_clipOffline == null) BuildClips();
            _source.volume = Mathf.Clamp01(_volume);
            _source.PlayOneShot(_clipOffline);
        }
    }
}

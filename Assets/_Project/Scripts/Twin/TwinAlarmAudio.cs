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
    /// ── 防叠加 ────────────────────────────────────────────────────
    /// 500 个点位同时告警会响成交响乐。所以 _minGapSeconds 内只播第一声，
    /// 其余静默丢弃（不排队 —— 排队会导致延迟越积越长，最后声音和画面完全错位）。
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

        private AudioSource _source;
        private AudioClip _clipAlarm;
        private AudioClip _clipOffline;
        private AudioClip _clipWarning;

        private TwinAlarmCenter _center;
        private float _lastPlayTime = -999f;
        private int _suppressedCount;
        private bool _warnedNoCenter;

        /// <summary>被防叠加机制丢弃的次数（调试用）。</summary>
        public int SuppressedCount { get { return _suppressedCount; } }

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
            if (now - _lastPlayTime < Mathf.Max(0f, _minGapSeconds))
            {
                _suppressedCount++;
                return;
            }
            _lastPlayTime = now;

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

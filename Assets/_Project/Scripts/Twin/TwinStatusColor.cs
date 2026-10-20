using UnityEngine;
using SchoolTwin.Core;

namespace SchoolTwin.Twin
{
    /// <summary>
    /// 状态着色：把 TwinAnchor 的数据状态映射成物体颜色。
    ///
    /// 用 MaterialPropertyBlock 而不是 renderer.material —— 后者每次访问都会实例化一份材质，
    /// 在几百个点位的场景里会直接把内存和合批打爆（这是 Unity 最常见的性能事故之一）。
    ///
    /// 注意：若物体勾选了 Batching Static / Contribute GI，静态合批会忽略 MaterialPropertyBlock，
    /// 颜色可能不生效。此时需要在编辑器里取消该勾选（本项目的场景物体多为静态，
    /// 建议给孪生点位用自建的动态物体，或按需取消静态标记）。
    /// </summary>
    [AddComponentMenu("SchoolTwin/状态着色 (TwinStatusColor)")]
    public class TwinStatusColor : MonoBehaviour
    {
        [Header("数据来源")]
        [Tooltip("留空则自动取同物体上的 TwinAnchor")]
        [SerializeField] private TwinAnchor _anchor;
        [Tooltip("留空 = 用整体最差状态；填指标名 = 只用该指标着色")]
        [SerializeField] private string _metric = "";

        [Header("目标渲染器（留空 = 自动采集本物体及子物体）")]
        [SerializeField] private Renderer[] _renderers;

        [Header("配色")]
        [SerializeField] private Color _unknown = new Color(0.35f, 0.38f, 0.45f, 1f);
        [SerializeField] private Color _normal = new Color(0.20f, 0.80f, 0.35f, 1f);
        [SerializeField] private Color _warning = new Color(1.00f, 0.72f, 0.15f, 1f);
        [SerializeField] private Color _alarm = new Color(0.90f, 0.20f, 0.20f, 1f);
        [SerializeField] private Color _stale = new Color(0.50f, 0.50f, 0.55f, 1f);
        [SerializeField] private Color _offline = new Color(0.28f, 0.28f, 0.30f, 1f);

        [Header("过渡")]
        [SerializeField] private float _lerpSpeed = 8f;
        [SerializeField] private float _refreshInterval = 0.1f;
        [SerializeField] private bool _applyEmission = true;
        [SerializeField] private float _emissionIntensity = 0.7f;

        [Header("告警闪烁")]
        [Tooltip("Alarm 状态是否闪烁")]
        [SerializeField] private bool _blinkAlarm = true;

        [Tooltip("Offline 状态是否闪烁")]
        [SerializeField] private bool _blinkOffline = true;

        [Tooltip("闪烁周期（秒）")]
        [SerializeField] private float _blinkPeriod = 0.9f;

        [Tooltip("最暗时的亮度系数（1 = 不闪）。0.45 在深色场景里已经很明显")]
        [Range(0f, 1f)]
        [SerializeField] private float _blinkFloor = 0.45f;

        [Tooltip("闪烁时的刷新间隔（秒）。必须比普通刷新快，否则闪成几帧一跳")]
        [SerializeField] private float _blinkRefreshInterval = 0.033f;

        [Tooltip("闪烁时的颜色跟随速度。太慢会把脉冲抹平成常亮")]
        [SerializeField] private float _blinkLerpSpeed = 60f;

        private static readonly int s_colorId = Shader.PropertyToID("_Color");
        private static readonly int s_baseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int s_emissionId = Shader.PropertyToID("_EmissionColor");

        private MaterialPropertyBlock _mpb;
        private Color _current = Color.gray;
        private Color _target = Color.gray;
        private bool _renderersCollected;
        private float _nextRefresh;

        private void OnEnable()
        {
            ResolveAnchor();
            CollectRenderers();
            _target = ColorForCurrentStatus();
            _current = _target;
            Apply(_current);
        }

        private void Update()
        {
            float now = Time.realtimeSinceStartup;

            TwinStatus status = ColorStatus();
            bool blinking = ShouldBlink(status);
            float interval = blinking ? _blinkRefreshInterval : _refreshInterval;

            if (now < _nextRefresh) return;
            _nextRefresh = now + interval;

            Color baseColor = ColorFor(status);
            _target = blinking ? ApplyBlink(baseColor) : baseColor;

            if (Approximately(_current, _target)) return;

            // 闪烁时目标色本身在脉动，用普通的 _lerpSpeed(8) 会把脉冲抹平成常亮，
            // 所以这里换一个更快的跟随速度。
            float speed = blinking ? _blinkLerpSpeed : _lerpSpeed;
            _current = Color.Lerp(_current, _target, 1f - Mathf.Exp(-speed * interval));
            Apply(_current);
        }

        /// <summary>
        /// 只闪 Alarm / Offline。
        /// Warning 不闪 —— 它出现频率高，满屏都闪等于没有闪，人会自动忽略。
        /// </summary>
        private bool ShouldBlink(TwinStatus status)
        {
            if (_blinkAlarm && status == TwinStatus.Alarm) return true;
            if (_blinkOffline && status == TwinStatus.Offline) return true;
            return false;
        }

        /// <summary>
        /// 余弦脉冲。亮色（Alarm 红）用「压暗」闪，暗色（Offline 灰黑）用「提亮」闪。
        ///
        /// 为什么必须区分：Offline 的基色是 (0.28,0.28,0.30)，压到 0.45 倍后是 0.126，
        /// 在深色场景里比背景还黑 —— 看起来像"不闪"，等于没有提醒。
        /// </summary>
        private Color ApplyBlink(Color c)
        {
            float period = Mathf.Max(0.2f, _blinkPeriod);
            float phase = Mathf.Repeat(Time.unscaledTime, period) / period;

            // cos 而非 sin：相位 0 与 1 时都在最亮，状态切换的瞬间不会突然变暗
            float k = 0.5f + 0.5f * Mathf.Cos(phase * 2f * Mathf.PI);

            // 感知亮度（ITU-R BT.601 权重）
            float lum = c.r * 0.299f + c.g * 0.587f + c.b * 0.114f;

            if (lum < 0.35f)
            {
                // 暗色：提亮脉冲，最亮时到 2.6 倍
                float boost = Mathf.Lerp(1f, 2.6f, k);
                return new Color(c.r * boost, c.g * boost, c.b * boost, c.a);
            }

            float factor = Mathf.Lerp(Mathf.Clamp01(_blinkFloor), 1f, k);
            return new Color(c.r * factor, c.g * factor, c.b * factor, c.a);
        }

        private void ResolveAnchor()
        {
            if (_anchor == null) _anchor = GetComponent<TwinAnchor>();
            if (_anchor == null) _anchor = GetComponentInParent<TwinAnchor>();
            if (_anchor == null)
            {
                Debug.LogWarning("[Twin] TwinStatusColor 找不到 TwinAnchor：" + name + "（着色不会生效）");
            }
        }

        private void CollectRenderers()
        {
            if (_renderersCollected && _renderers != null && _renderers.Length > 0) return;

            if (_renderers == null || _renderers.Length == 0)
            {
                GameObject target = _anchor != null ? _anchor.VisualTarget : gameObject;
                _renderers = target.GetComponentsInChildren<Renderer>(true);
            }
            _renderersCollected = true;
        }

        private TwinStatus ColorStatus()
        {
            if (_anchor == null) return TwinStatus.Unknown;

            if (!string.IsNullOrEmpty(_metric))
            {
                TwinValue v;
                if (!_anchor.TryGetValue(_metric, out v)) return TwinStatus.Unknown;
                if (v.Quality == DataQuality.Offline) return TwinStatus.Offline;
                if (v.Quality == DataQuality.Bad) return TwinStatus.Warning;
            }

            TwinStatus s = _anchor.Status;
            if (_anchor.IsStale && s != TwinStatus.Offline) s = TwinStatus.Stale;
            return s;
        }

        private Color ColorForCurrentStatus()
        {
            return ColorFor(ColorStatus());
        }

        private Color ColorFor(TwinStatus status)
        {
            switch (status)
            {
                case TwinStatus.Normal: return _normal;
                case TwinStatus.Warning: return _warning;
                case TwinStatus.Alarm: return _alarm;
                case TwinStatus.Stale: return _stale;
                case TwinStatus.Offline: return _offline;
                default: return _unknown;
            }
        }

        private void Apply(Color color)
        {
            if (_renderers == null) return;
            if (_mpb == null) _mpb = new MaterialPropertyBlock();

            for (int i = 0; i < _renderers.Length; i++)
            {
                Renderer r = _renderers[i];
                if (r == null) continue;

                Material shared = r.sharedMaterial;
                if (shared == null) continue;

                r.GetPropertyBlock(_mpb);
                if (shared.HasProperty(s_colorId)) _mpb.SetColor(s_colorId, color);
                if (shared.HasProperty(s_baseColorId)) _mpb.SetColor(s_baseColorId, color);
                if (_applyEmission && shared.HasProperty(s_emissionId))
                    _mpb.SetColor(s_emissionId, color * _emissionIntensity);
                r.SetPropertyBlock(_mpb);
                _mpb.Clear();
            }
        }

        private static bool Approximately(Color a, Color b)
        {
            return Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b) + Mathf.Abs(a.a - b.a) < 0.004f;
        }

        [ContextMenu("重新采集渲染器")]
        private void RecollectRenderers()
        {
            _renderers = null;
            _renderersCollected = false;
            CollectRenderers();
            Apply(_target);
        }

        [ContextMenu("打印当前状态")]
        private void PrintStatus()
        {
            Debug.Log("[Twin] " + name + " 状态=" + ColorStatus() +
                      (_anchor != null ? "  陈旧=" + _anchor.IsStale : "  (无 TwinAnchor)"));
        }
    }
}

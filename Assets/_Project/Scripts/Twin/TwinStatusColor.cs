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

        [Header("染色强度（0 = 保留原色，1 = 完全替换成状态色）")]
        [Tooltip("Warning / Stale / Unknown 的染色强度。门、墙、柜子这类大件建议 0.2~0.35，否则会整片变成绿/黄")]
        [Range(0f, 1f)]
        [SerializeField] private float _tintStrength = 0.30f;

        [Tooltip("Alarm 的染色强度。必须明显高于 _tintStrength，否则真出事时看不出来")]
        [Range(0f, 1f)]
        [SerializeField] private float _alarmTintStrength = 0.75f;

        [Tooltip("勾上后：正常状态完全不染色，保留物体原本的颜色；只有异常才泛色")]
        [SerializeField] private bool _tintOnlyWhenAbnormal = true;

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

        /// <summary>每个 Renderer 原始材质色。染色时和它混合，而不是整个替换掉。</summary>
        private Color[] _baseColors;
        private float _appliedStrength = -1f;

        private void OnEnable()
        {
            ResolveAnchor();
            CollectRenderers();

            // 【灯：本组件彻底退出】
            // 灯管的外观（亮/灭、自发光、点光源）归 `TwinDevicePresenter` 独占。
            // 这里必须**连 `_Color` 一起放手**，不能只让出自发光 ——
            // 灯管用的是自发光材质，`_Color` 会**乘到** `_EmissionColor` 上：
            // 表现层把自发光写成暖白 (2.2, 2.06, 1.78)，本组件只要把 `_Color`
            // 染成状态绿 (0.20, 0.80, 0.35)，算出来就是 (0.44, 1.65, 0.62) ——
            // 一根**青绿色**的灯管。用户看到的就是「灯变青了，不像灯」。
            //
            // 灯出异常时靠标记牌、详情卡片、告警面板表达，不从灯管颜色里透出来。
            // 这是「一个属性只能有一个主人」（99 号清单第 21 条）在渲染层的同一道理。
            if (PresenterOwnsEmission())
            {
                RestoreBaseColors();   // 清掉可能残留的 MPB 覆盖，回落材质原样
                enabled = false;
                return;
            }

            TwinStatus status = EffectiveStatus();
            _current = ColorFor(status);
            _target = _current;
            _appliedStrength = EffectiveStrength(status);
            Apply(_current, _appliedStrength);
        }

        private void OnDisable()
        {
            // 组件关掉后必须把颜色还回去，否则「门一直是绿的」会留在场景里，
            // 而且下次打开组件时也看不出问题出在哪。
            RestoreBaseColors();
        }

        private void Update()
        {
            float now = Time.realtimeSinceStartup;

            TwinStatus status = EffectiveStatus();
            bool blinking = !IsPowerCut() && ShouldBlink(status);
            float interval = blinking ? _blinkRefreshInterval : _refreshInterval;

            if (now < _nextRefresh) return;
            _nextRefresh = now + interval;

            // 强度也要参与判断：状态从 Normal 变成 Warning 时颜色可能刚好没变多少，
            // 但强度从 0 跳到 0.3，必须重新应用一次。
            float strength = EffectiveStrength(status);
            bool strengthChanged = Mathf.Abs(strength - _appliedStrength) > 0.005f;

            Color baseColor = ColorFor(status);
            _target = blinking ? ApplyBlink(baseColor) : baseColor;

            if (!strengthChanged && Approximately(_current, _target)) return;

            // 闪烁时目标色本身在脉动，用普通的 _lerpSpeed(8) 会把脉冲抹平成常亮，
            // 所以这里换一个更快的跟随速度。
            float speed = blinking ? _blinkLerpSpeed : _lerpSpeed;
            _current = Color.Lerp(_current, _target, 1f - Mathf.Exp(-speed * interval));
            Apply(_current, strength);
            _appliedStrength = strength;
        }

        /// <summary>
        /// 按状态决定「染多深」。
        ///
        /// 【为什么不能一律 1.0】
        /// 原来的实现是把材质的 _Color 整个换成状态色 —— 一扇白门绑上点位后
        /// 直接变成一整块纯绿，很难看，而且「一切正常」时满屋子都是绿色，
        /// 真正出事的那一处反而不显眼。
        ///
        /// 现在：正常不染色（保留原色），异常才泛色，告警比警告更深。
        /// </summary>
        private float StrengthFor(TwinStatus status)
        {
            switch (status)
            {
                case TwinStatus.Normal:
                    return _tintOnlyWhenAbnormal ? 0f : _tintStrength;
                case TwinStatus.Alarm:
                    return _alarmTintStrength;
                case TwinStatus.Offline:
                    return _alarmTintStrength * 0.8f;
                default:
                    return _tintStrength;   // Warning / Stale / Unknown
            }
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
            CaptureBaseColors();
            _renderersCollected = true;
        }

        /// <summary>
        /// 记下每个 Renderer 的原始材质色。
        /// 染色 = 在「原色」和「状态色」之间按强度插值，而不是把原色丢掉 ——
        /// 丢掉原色的结果就是白门变纯绿。
        /// </summary>
        private void CaptureBaseColors()
        {
            if (_renderers == null) { _baseColors = null; return; }

            _baseColors = new Color[_renderers.Length];
            for (int i = 0; i < _renderers.Length; i++)
            {
                Renderer r = _renderers[i];
                Material shared = r != null ? r.sharedMaterial : null;

                if (shared == null) { _baseColors[i] = Color.white; continue; }
                if (shared.HasProperty(s_colorId)) _baseColors[i] = shared.GetColor(s_colorId);
                else if (shared.HasProperty(s_baseColorId)) _baseColors[i] = shared.GetColor(s_baseColorId);
                else _baseColors[i] = Color.white;
            }
        }

        private Color BaseColorAt(int index)
        {
            if (_baseColors == null || index < 0 || index >= _baseColors.Length) return Color.white;
            return _baseColors[index];
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

        /// <summary>
        /// 回路断电了吗。
        ///
        /// 【为什么着色也要管这件事】断电 8 秒后点位自己会变 Offline，而 Offline 是**闪烁**的
        /// （见 ShouldBlink）。断一层电 = 12 台设备同时闪 —— 看起来像 12 个故障，
        /// 实际上只有一个事实。断电要"静下来"：稳定的深灰，不闪。
        /// </summary>
        private bool IsPowerCut()
        {
            return _anchor != null && !string.IsNullOrEmpty(_anchor.TwinId)
                   && !TwinCircuit.IsPowered(_anchor.TwinId);
        }

        /// <summary>着色用的最终状态：断电统一压成 Offline（深灰），不参与闪烁。</summary>
        private TwinStatus EffectiveStatus()
        {
            return IsPowerCut() ? TwinStatus.Offline : ColorStatus();
        }

        private float EffectiveStrength(TwinStatus status)
        {
            // 断电比普通 Offline 更"实"一点（0.6 倍告警强度）：
            // 它是个确定的人为状态，应该一眼能看出"这台设备现在不工作"，
            // 而不是像瞬时丢包那样淡淡泛灰。
            if (IsPowerCut()) return _alarmTintStrength * 0.6f;
            return StrengthFor(status);
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

        /// <summary>
        /// 这台设备的「亮/灭」是不是由 `TwinDevicePresenter` 独占。
        ///
        /// 【2026-10-22 实测踩到的坑】灯的 `_EmissionColor` 只有一个，两套系统同时在写：
        ///   · 表现层：开 = 2.2 的暖白（写在**材质**上）；
        ///   · 本组件：状态色按强度混进自发光（写在 **MaterialPropertyBlock** 上）。
        /// MPB 的优先级**高于**材质属性，而「正常」状态的强度是 0
        /// （`_tintOnlyWhenAbnormal`），于是 MPB 把自发光写成了纯黑 ——
        /// **材质上写得再亮也被盖掉**。
        ///
        /// 症状极其隐蔽：一间教室 8 盏灯，只有锚点挂着的那一盏不亮
        /// （其它 7 盏身上没有本组件），卡片上却写着「已开」。
        /// 用户的原话是「原本这个照明状态正常的灯，现在也被你改的变得不正常了，不亮了」。
        ///
        /// 所以灯的发光归表现层，本组件对灯**只染色、不碰自发光**。
        /// 灯出异常时靠 `_Color` 染色依然看得出来（红/黄/灰），只是不再从灯管里透出来。
        /// </summary>
        private bool PresenterOwnsEmission()
        {
            return _anchor != null && string.Equals(_anchor.Category, "Light", System.StringComparison.Ordinal);
        }

        private void Apply(Color statusColor, float strength)
        {
            if (_renderers == null) return;
            if (_mpb == null) _mpb = new MaterialPropertyBlock();

            bool writeEmission = _applyEmission && !PresenterOwnsEmission();

            for (int i = 0; i < _renderers.Length; i++)
            {
                Renderer r = _renderers[i];
                if (r == null) continue;

                Material shared = r.sharedMaterial;
                if (shared == null) continue;

                // 强度 0 = 完全保留原色。注意这里不是「不写属性」而是「写回原值」：
                // MPB 是叠加在材质上的，之前写过的状态色不会自己消失。
                Color final = strength <= 0.001f
                    ? BaseColorAt(i)
                    : Color.Lerp(BaseColorAt(i), statusColor, strength);

                r.GetPropertyBlock(_mpb);
                if (shared.HasProperty(s_colorId)) _mpb.SetColor(s_colorId, final);
                if (shared.HasProperty(s_baseColorId)) _mpb.SetColor(s_baseColorId, final);
                if (writeEmission && shared.HasProperty(s_emissionId))
                {
                    // 自发光同样按强度混合：低强度时几乎不发光，
                    // 否则「淡淡的泛色」会被自发光放大成整块发亮的绿。
                    _mpb.SetColor(s_emissionId,
                        Color.Lerp(Color.black, statusColor, strength) * _emissionIntensity);
                }
                r.SetPropertyBlock(_mpb);
                _mpb.Clear();
            }
        }

        /// <summary>清掉 MPB 覆盖，让渲染器回落到材质本身的颜色。</summary>
        private void RestoreBaseColors()
        {
            if (_renderers == null) return;
            if (_mpb == null) _mpb = new MaterialPropertyBlock();

            for (int i = 0; i < _renderers.Length; i++)
            {
                Renderer r = _renderers[i];
                if (r == null) continue;
                r.GetPropertyBlock(_mpb);
                _mpb.Clear();
                r.SetPropertyBlock(_mpb);
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
            _appliedStrength = EffectiveStrength(EffectiveStatus());
            Apply(_target, _appliedStrength);
        }

        [ContextMenu("打印当前状态")]
        private void PrintStatus()
        {
            Debug.Log("[Twin] " + name + " 状态=" + ColorStatus() +
                      (_anchor != null ? "  陈旧=" + _anchor.IsStale : "  (无 TwinAnchor)"));
        }
    }
}

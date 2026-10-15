using System;
using System.Collections.Generic;
using UnityEngine;
using SchoolTwin.Core;

namespace SchoolTwin.Twin
{
    /// <summary>
    /// 孪生锚点：场景里「一个可被数据驱动的对象」。
    ///
    /// 职责边界（刻意做窄）：
    ///   · 只负责“持有数据 + 判定状态 + 广播变化”
    ///   · 不负责怎么显示（那是 TwinStatusColor / TwinValueLabel 的事）
    ///   · 不负责数据从哪来（那是 IDataSource 的事）
    /// 这样换可视化方式、换数据源都不用动它。
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("SchoolTwin/孪生锚点 (TwinAnchor)")]
    public class TwinAnchor : MonoBehaviour, ITwinConsumer
    {
        [Header("身份（对应《点位映射表》）")]
        [SerializeField] private string _twinId = "Class2-1/Sensor/Temp_01";
        [SerializeField] private string _displayName = "";
        [Tooltip("留空则自动取 TwinId 的第一段，如 Class2-1/Sensor/Temp_01 → Class2-1")]
        [SerializeField] private string _zone = "";
        [Tooltip("留空则自动取 TwinId 的第二段")]
        [SerializeField] private string _category = "";

        [Header("视觉绑定（留空 = 本物体）")]
        [SerializeField] private GameObject _visualTarget;
        [Tooltip("用于引用资源包 Assets/TirGames 内的只读对象，填场景层级路径，如 SchoolSceneDay/Class2-1/Desk")]
        [SerializeField] private string _visualTargetPath = "";

        [Header("时效与通知")]
        [Tooltip("超过这个秒数没收到数据 → Stale；数据源整体离线 → Offline")]
        [SerializeField] private float _staleAfterSeconds = 3f;
        [Tooltip("两次刷新事件的最小间隔，防止高频抖动导致 UI 反复重绘")]
        [SerializeField] private float _minNotifyInterval = 0.05f;
        [SerializeField] private bool _logEverySample = false;

        private readonly Dictionary<string, TwinValue> _values =
            new Dictionary<string, TwinValue>(StringComparer.Ordinal);

        private TwinCatalog _catalog;
        private GameObject _resolvedTarget;
        private float _lastSampleRealtime = -1f;
        private float _lastNotifyRealtime = -999f;
        private TwinStatus _status = TwinStatus.Unknown;
        private TwinStatus _reportedStatus = TwinStatus.Unknown;

        /// <summary>某个指标发生“有意义的变化”时触发（已按 epsilon 与最小间隔节流）。</summary>
        public event Action<TwinAnchor, string, TwinValue> MetricChanged;
        /// <summary>整体状态变化时触发。</summary>
        public event Action<TwinAnchor, TwinStatus> StatusChanged;

        public string TwinId { get { return _twinId; } }
        public string DisplayName { get { return string.IsNullOrEmpty(_displayName) ? _twinId : _displayName; } }
        public string ConsumerName { get { return "TwinAnchor(" + _twinId + ")"; } }
        public TwinStatus Status { get { return _status; } }
        public int MetricCount { get { return _values.Count; } }
        public IReadOnlyDictionary<string, TwinValue> Values { get { return _values; } }
        public bool HasData { get { return _values.Count > 0; } }

        public string Zone
        {
            get { return string.IsNullOrEmpty(_zone) ? Segment(_twinId, 0) : _zone; }
        }

        public string Category
        {
            get { return string.IsNullOrEmpty(_category) ? Segment(_twinId, 1) : _category; }
        }

        /// <summary>数据是否已陈旧（本机接收时刻判定，不依赖数据源时钟）。</summary>
        public bool IsStale
        {
            get
            {
                if (_lastSampleRealtime < 0f) return false;
                return (Time.realtimeSinceStartup - _lastSampleRealtime) > _staleAfterSeconds;
            }
        }

        /// <summary>距上一次收到数据的秒数；-1 表示至今没收到过任何数据。UI 用来显示「数据新鲜度」。</summary>
        public float SecondsSinceLastSample
        {
            get
            {
                if (_lastSampleRealtime < 0f) return -1f;
                return Time.realtimeSinceStartup - _lastSampleRealtime;
            }
        }

        public GameObject VisualTarget
        {
            get
            {
                if (_visualTarget != null) return _visualTarget;
                if (!string.IsNullOrEmpty(_visualTargetPath))
                {
                    if (_resolvedTarget == null)
                    {
                        _resolvedTarget = GameObject.Find(_visualTargetPath);
                        if (_resolvedTarget == null)
                        {
                            Debug.LogWarning("[Twin] TwinAnchor(" + _twinId + ") 找不到路径：" +
                                             _visualTargetPath + "，回退到本物体");
                        }
                    }
                    if (_resolvedTarget != null) return _resolvedTarget;
                }
                return gameObject;
            }
        }

        // ---------------------------------------------------------------- 生命周期

        private void OnEnable()
        {
            // Ensure 而非依赖场景里已有 TwinRuntime：避免 Awake/OnEnable 顺序陷阱
            TwinRuntime runtime = TwinRuntime.Ensure();
            _catalog = runtime.Catalog;
            runtime.Registry.Register(this);
            runtime.Bus.Subscribe(_twinId, this);
        }

        private void OnDisable()
        {
            TwinRuntime runtime = TwinRuntime.Instance;
            if (runtime == null) return;
            runtime.Bus.Unsubscribe(_twinId, this);
            runtime.Registry.Unregister(this);
        }

        private void Update()
        {
            if (_lastSampleRealtime < 0f) return;
            if (!IsStale) return;
            if (_status == TwinStatus.Stale || _status == TwinStatus.Offline) return;
            SetStatus(TwinStatus.Stale);
            ReportIfNeeded(true);
        }

        private void OnValidate()
        {
            _staleAfterSeconds = Mathf.Max(0.1f, _staleAfterSeconds);
            _minNotifyInterval = Mathf.Max(0f, _minNotifyInterval);
        }

        // ---------------------------------------------------------------- 数据入口

        public void OnSample(in TwinSample sample)
        {
            if (!string.Equals(sample.TwinId, _twinId, StringComparison.Ordinal)) return;

            _lastSampleRealtime = Time.realtimeSinceStartup;

            bool significant;
            TwinValue value;
            TwinValue prev;
            if (_values.TryGetValue(sample.Metric, out prev))
            {
                significant = Mathf.Abs(sample.Value - prev.Value) > GetEpsilon(sample.Metric)
                              || prev.Quality != sample.Quality;
                value.PreviousValue = prev.Value;
            }
            else
            {
                significant = true;
                value.PreviousValue = sample.Value;
            }

            value.Value = sample.Value;
            value.TimestampUtc = sample.TimestampUtc;
            value.Quality = sample.Quality;
            _values[sample.Metric] = value;

            if (_logEverySample) Debug.Log("[Twin] " + sample);

            EvaluateStatus();

            if (significant)
            {
                ReportIfNeeded(false);
            }
        }

        public bool TryGetValue(string metric, out TwinValue value)
        {
            return _values.TryGetValue(metric, out value);
        }

        /// <summary>取“最差”的一个指标状态，用于整机着色。</summary>
        public TwinStatus StatusOfWorst()
        {
            return _status;
        }

        /// <summary>
        /// 取单个指标的状态。
        /// 面板需要「按指标分别着色」（一个点位可能温度正常、CO₂ 告警），
        /// 所以不能只暴露整体最差状态。这里把「数据陈旧」也计入。
        /// </summary>
        public TwinStatus StatusOf(string metric)
        {
            TwinValue value;
            if (!_values.TryGetValue(metric, out value)) return TwinStatus.Unknown;

            TwinStatus status = StatusOf(metric, value);
            if (IsStale && status != TwinStatus.Offline) status = TwinStatus.Stale;
            return status;
        }

        // ---------------------------------------------------------------- 内部

        private void EvaluateStatus()
        {
            TwinStatus worst = TwinStatus.Unknown;
            int worstRank = -1;

            foreach (KeyValuePair<string, TwinValue> kv in _values)
            {
                TwinStatus s = StatusOf(kv.Key, kv.Value);
                int rank = Severity(s);
                if (rank > worstRank)
                {
                    worstRank = rank;
                    worst = s;
                }
                if (rank >= Severity(TwinStatus.Offline)) break;
            }

            SetStatus(worst);
        }

        private TwinStatus StatusOf(string metric, TwinValue value)
        {
            if (value.Quality == DataQuality.Offline) return TwinStatus.Offline;
            if (value.Quality == DataQuality.Bad) return TwinStatus.Warning;
            if (value.Quality == DataQuality.Uncertain) return TwinStatus.Warning;

            TwinMetricDefinition def = FindMetricDefinition(metric);
            if (def != null)
            {
                if (value.Value < def.AlarmLow || value.Value > def.AlarmHigh) return TwinStatus.Alarm;
                if (value.Value < def.WarnLow || value.Value > def.WarnHigh) return TwinStatus.Warning;
            }
            return TwinStatus.Normal;
        }

        private TwinMetricDefinition FindMetricDefinition(string metric)
        {
            if (_catalog == null) return null;
            TwinPointDefinition point = _catalog.Find(_twinId);
            if (point == null) return null;
            return point.FindMetric(metric);
        }

        private float GetEpsilon(string metric)
        {
            TwinMetricDefinition def = FindMetricDefinition(metric);
            return def == null ? 0.01f : Mathf.Max(0f, def.Epsilon);
        }

        private void SetStatus(TwinStatus status)
        {
            if (_status == status) return;
            _status = status;
        }

        private void ReportIfNeeded(bool force)
        {
            bool statusChanged = _status != _reportedStatus;
            if (!force && !statusChanged &&
                (Time.realtimeSinceStartup - _lastNotifyRealtime) < _minNotifyInterval) return;

            _lastNotifyRealtime = Time.realtimeSinceStartup;

            if (MetricChanged != null)
            {
                foreach (KeyValuePair<string, TwinValue> kv in _values)
                {
                    MetricChanged(this, kv.Key, kv.Value);
                }
            }

            if (statusChanged)
            {
                _reportedStatus = _status;
                if (StatusChanged != null) StatusChanged(this, _status);
            }
        }

        private static int Severity(TwinStatus s)
        {
            switch (s)
            {
                case TwinStatus.Offline: return 4;
                case TwinStatus.Alarm: return 3;
                case TwinStatus.Warning: return 2;
                case TwinStatus.Stale: return 1;
                case TwinStatus.Normal: return 0;
                default: return -1;
            }
        }

        private static string Segment(string id, int index)
        {
            if (string.IsNullOrEmpty(id)) return "";
            string[] parts = id.Split('/');
            return index < parts.Length ? parts[index] : "";
        }

        [ContextMenu("打印当前数据")]
        private void PrintValues()
        {
            string text = "[Twin] " + _twinId + "  状态=" + _status + "  陈旧=" + IsStale + "\n";
            foreach (KeyValuePair<string, TwinValue> kv in _values)
            {
                text += "    " + kv.Key + " = " + kv.Value + "\n";
            }
            Debug.Log(text);
        }
    }
}

using System;
using System.Collections.Generic;
using UnityEngine;

namespace SchoolTwin.Twin
{
    /// <summary>单个指标的元数据：量程、阈值、变化阈值。</summary>
    [Serializable]
    public class TwinMetricDefinition
    {
        public string Metric = "Temperature";
        public string Unit = "°C";

        [Header("Mock 生成区间 / 可视化映射区间")]
        public float Min = 0f;
        public float Max = 40f;

        [Header("阈值（越界即告警）")]
        public float WarnLow = 5f;
        public float WarnHigh = 30f;
        public float AlarmLow = 0f;
        public float AlarmHigh = 35f;

        [Tooltip("变化小于该值不触发刷新事件，避免高频抖动导致无意义的重绘")]
        public float Epsilon = 0.05f;
    }

    /// <summary>一个孪生点位 = 一个业务 ID + 它身上的若干指标。这是「点位映射表」的资产化形式。</summary>
    [Serializable]
    public class TwinPointDefinition
    {
        public string TwinId = "Class2-1/Sensor/Temp_01";
        public string DisplayName = "2-1班 温度传感器";
        public string Zone = "Class2-1";
        public string Category = "Sensor";

        [Tooltip("预留：真实数据接入时填 MQTT 主题 / HTTP 路径，Mock 阶段留空")]
        public string SourceTopic = "";

        public List<TwinMetricDefinition> Metrics = new List<TwinMetricDefinition>();

        public TwinMetricDefinition FindMetric(string metric)
        {
            if (string.IsNullOrEmpty(metric) || Metrics == null) return null;
            for (int i = 0; i < Metrics.Count; i++)
            {
                if (Metrics[i] != null && string.Equals(Metrics[i].Metric, metric, StringComparison.Ordinal))
                    return Metrics[i];
            }
            return null;
        }
    }

    /// <summary>
    /// 孪生点位目录（ScriptableObject）。
    /// 它同时是：Mock 数据的生成依据、阈值告警的依据、UI 显示名称的依据。
    /// 单一数据源，避免三处各写一份对不上。
    /// </summary>
    [CreateAssetMenu(fileName = "TwinCatalog", menuName = "SchoolTwin/孪生点位目录")]
    public class TwinCatalog : ScriptableObject
    {
        [Tooltip("点位清单。建议与《点位映射表 v0》CSV 保持一致")]
        public List<TwinPointDefinition> Points = new List<TwinPointDefinition>();

        private Dictionary<string, TwinPointDefinition> _index;

        public int Count
        {
            get { return Points == null ? 0 : Points.Count; }
        }

        public TwinPointDefinition Find(string twinId)
        {
            if (string.IsNullOrEmpty(twinId)) return null;
            if (_index == null) BuildIndex();
            TwinPointDefinition def;
            return _index.TryGetValue(twinId, out def) ? def : null;
        }

        public void BuildIndex()
        {
            _index = new Dictionary<string, TwinPointDefinition>(StringComparer.Ordinal);
            if (Points == null) return;
            for (int i = 0; i < Points.Count; i++)
            {
                TwinPointDefinition p = Points[i];
                if (p == null || string.IsNullOrEmpty(p.TwinId)) continue;
                if (_index.ContainsKey(p.TwinId))
                {
                    Debug.LogWarning("[Twin] 点位目录中存在重复 TwinId：" + p.TwinId);
                    continue;
                }
                _index.Add(p.TwinId, p);
            }
        }

        private void OnValidate()
        {
            _index = null; // 编辑器里改完立刻失效缓存
        }
    }
}

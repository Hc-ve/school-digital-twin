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

        [Tooltip("回差（死区）。0 = 自动取警告带宽的 5%（温度 16~28 → 0.6°C）。" +
                 "值在阈值附近抖动时，没有回差会产生连环告警")]
        public float Hysteresis = 0f;
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

        [Tooltip("设备模型（properties / actions / relations）。留空 = 按 Category 自动派生（推荐）")]
        public TwinDeviceModel DeviceModel;

        public List<TwinMetricDefinition> Metrics = new List<TwinMetricDefinition>();

        /// <summary>
        /// 派生出来的模型缓存。
        ///
        /// 【为什么不用「没有就生成并写回 DeviceModel」】
        /// TwinCatalog 是 ScriptableObject，在 Play 模式下给它赋字段会**把改动留在资产里**
        /// （退出 Play 不还原），结果是每次进 Play 都会给 .asset 生成一大段派生数据，
        /// git diff 全是噪音，而且派生数据和代码会互相漂移。
        /// 所以派生结果只放内存，资产里保持 null。
        /// </summary>
        [System.NonSerialized] private TwinDeviceModel _derivedModel;

        public TwinDeviceModel GetDeviceModel()
        {
            // 【为什么不能只判 null —— 这是本项目踩过的一个静默坑】
            // DeviceModel 是 [Serializable] 的类字段。资产里没有这个键时，
            // Unity 反序列化会**造一个空壳实例**（不是 null），于是 `!= null` 成立，
            // 返回一个「零动作的空壳模型」→ CanCommand == false → 控制区把自己藏起来，
            // 而且一声不响（Console 干净，只是按钮不见了）。
            // 所以判据是「有没有内容」，不是「是不是 null」。
            if (HasContent(DeviceModel))
            {
                TwinDeviceModels.ForceReadOnly(DeviceModel);
                return DeviceModel;
            }
            if (_derivedModel == null) _derivedModel = TwinDeviceModels.For(Category, Zone, TwinId);
            return _derivedModel;
        }

        /// <summary>
        /// 资产里是否真的填过模型（有动作或有属性）。
        ///
        /// 对外暴露成只读属性，是为了让「盘点」能说清一句话：
        /// 这个点位的模型是**从资产里来的**，还是**运行时按 Category 派生的**。
        /// 光看 Inspector 分不出来 —— 空壳也不是 null。
        /// </summary>
        public bool HasSerializedModel
        {
            get { return HasContent(DeviceModel); }
        }

        /// <summary>序列化出来的模型是否真的填过东西（有动作或有属性）。</summary>
        private static bool HasContent(TwinDeviceModel m)
        {
            if (m == null) return false;
            if (m.Actions != null && m.Actions.Count > 0) return true;
            if (m.Properties != null && m.Properties.Count > 0) return true;
            return false;
        }

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

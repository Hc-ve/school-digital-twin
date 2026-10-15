using System;

namespace SchoolTwin.Core
{
    /// <summary>数据质量。区分“值不准”和“没有数据”，是离线判定的基础。</summary>
    public enum DataQuality
    {
        Good = 0,
        Uncertain = 1,
        Bad = 2,
        Offline = 3,
    }

    /// <summary>孪生体运行状态。由“指标值 + 阈值 + 数据时效”共同推导。</summary>
    public enum TwinStatus
    {
        Unknown = 0,
        Normal = 1,
        Warning = 2,
        Alarm = 3,
        Stale = 4,
        Offline = 5,
    }

    /// <summary>数据源状态机。</summary>
    public enum DataSourceState
    {
        Idle = 0,
        Connecting = 1,
        Running = 2,
        Degraded = 3,
        Stopped = 4,
        Faulted = 5,
    }

    /// <summary>
    /// 一个数据点：谁(TwinId) 的 什么量(Metric) 在 何时(TimestampUtc) 是多少(Value)，质量如何。
    /// 用 struct 且不带任何托管分配，是「10Hz × N 点位不产生 GC」的前提。
    /// </summary>
    [Serializable]
    public struct TwinSample
    {
        public string TwinId;
        public string Metric;
        public float Value;
        public double TimestampUtc;
        public DataQuality Quality;

        public TwinSample(string twinId, string metric, float value, double timestampUtc,
                          DataQuality quality = DataQuality.Good)
        {
            TwinId = twinId;
            Metric = metric;
            Value = value;
            TimestampUtc = timestampUtc;
            Quality = quality;
        }

        public bool IsValid
        {
            get { return !string.IsNullOrEmpty(TwinId) && !string.IsNullOrEmpty(Metric); }
        }

        public override string ToString()
        {
            return TwinId + "." + Metric + "=" + Value.ToString("0.###") + "(" + Quality + ")";
        }
    }

    /// <summary>某个孪生体某个指标的当前值。保留 PreviousValue 以便做变化检测与趋势箭头。</summary>
    public struct TwinValue
    {
        public float Value;
        public float PreviousValue;
        public double TimestampUtc;
        public DataQuality Quality;

        public float Delta { get { return Value - PreviousValue; } }

        public bool IsUsable
        {
            get { return Quality == DataQuality.Good || Quality == DataQuality.Uncertain; }
        }

        public override string ToString()
        {
            return Value.ToString("0.###") + "(Δ" + Delta.ToString("+0.###;-0.###;0") + ")";
        }
    }

    /// <summary>数据汇聚终点。**任意线程**都可调用 Emit。</summary>
    public interface IDataSink
    {
        void Emit(in TwinSample sample);
    }

    /// <summary>数据消费方（TwinAnchor 实现它）。**只在主线程**被回调。</summary>
    public interface ITwinConsumer
    {
        string ConsumerName { get; }
        void OnSample(in TwinSample sample);
    }

    /// <summary>数据源。实现方自己负责线程与重连，只通过 IDataSink 往外吐数据。</summary>
    public interface IDataSource
    {
        string SourceName { get; }
        DataSourceState State { get; }
        event Action<DataSourceState> StateChanged;
        void StartSource(IDataSink sink);
        void StopSource();
    }
}

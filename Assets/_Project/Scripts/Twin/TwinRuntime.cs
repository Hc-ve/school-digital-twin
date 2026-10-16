using System.Collections.Generic;
using UnityEngine;
using SchoolTwin.Core;
using SchoolTwin.Data;

namespace SchoolTwin.Twin
{
    /// <summary>
    /// 孪生运行时：场景里唯一的“总调度”。
    ///   Awake   : 建立 Bus 与 Registry
    ///   Update  : 每帧把后台线程写入的数据分发到各 TwinAnchor（唯一的 Unity API 接触点）
    ///   OnDestroy: 停掉所有数据源线程，防止编辑器退出时线程泄漏
    ///
    /// 用 Ensure() 获取实例，任何脚本都不需要关心它是否已在场景里。
    /// </summary>
    [DefaultExecutionOrder(-100)]
    [AddComponentMenu("SchoolTwin/孪生运行时 (TwinRuntime)")]
    public class TwinRuntime : MonoBehaviour
    {
        public static TwinRuntime Instance { get; private set; }

        [Tooltip("点位目录：提供阈值、量程、显示名。留空则所有指标按“正常”处理")]
        [SerializeField] private TwinCatalog _catalog;

        [Tooltip("每帧最多分发多少条数据，防止一帧涌入上万条打死帧率")]
        [SerializeField] private int _maxDispatchPerFrame = 4096;

        [Tooltip("大于 0 时按该间隔打印总线统计（调试用）")]
        [SerializeField] private float _logBusStatsEverySeconds = 0f;

        private readonly List<DataSourceBase> _sources = new List<DataSourceBase>(4);
        private float _nextStatsLog;
        private IDataSink _sinkOverride;

        public TwinDataBus Bus { get; private set; }
        public TwinRegistry Registry { get; private set; }
        public IReadOnlyList<DataSourceBase> Sources { get { return _sources; } }

        /// <summary>
        /// 数据源的出口。默认就是 Bus。
        ///
        /// 【为什么需要这个间接层】
        /// 录制器（TwinRecorder）要「在数据进总线之前顺手抄一份」。做法是把自己套在
        /// 数据源与总线之间：数据源 → 录制器 → 总线。
        ///
        /// 但数据源是在自己的 OnEnable 里直接 `StartSource(runtime.Bus)` 拿出口的
        /// （见 DataSourceBase.OnEnable），等录制器挂上来时数据源早就接好线了。
        /// 所以这里提供一个可替换的出口，录制器开关时把 _sinkOverride 换掉，
        /// 并通知已在运行的数据源重新取一次 —— 这样**不用改任何数据源**。
        /// </summary>
        public IDataSink Sink
        {
            get { return _sinkOverride != null ? _sinkOverride : (IDataSink)Bus; }
        }

        /// <summary>把所有正在运行的数据源的出口改指向新的 sink，并保持它们继续运行。</summary>
        public void RedirectSinkTo(IDataSink sink)
        {
            _sinkOverride = sink;
            RebindRunningSources();
        }

        /// <summary>撤掉出口替换，数据源恢复直连 Bus。</summary>
        public void RestoreSink()
        {
            _sinkOverride = null;
            RebindRunningSources();
        }

        /// <summary>
        /// 让正在运行的数据源重新拿一次出口。做法是「停→起」，因为 Sink 是
        /// StartSource 时赋值的只读属性。停机时间在毫秒级，数据不会丢。
        /// </summary>
        private void RebindRunningSources()
        {
            IDataSink target = Sink;
            for (int i = 0; i < _sources.Count; i++)
            {
                DataSourceBase src = _sources[i];
                if (src == null) continue;
                src.BindSink(target);
            }
        }

        public TwinCatalog Catalog
        {
            get { return _catalog; }
            set { _catalog = value; }
        }

        /// <summary>拿到运行时实例；没有就创建一个隐藏的，保证调用方永远不需要判空。</summary>
        public static TwinRuntime Ensure()
        {
            if (Instance != null) return Instance;

            TwinRuntime found = FindObjectOfType<TwinRuntime>();
            if (found != null) return found;

            GameObject go = new GameObject("[TwinRuntime]");
            return go.AddComponent<TwinRuntime>();
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Debug.LogWarning("[Twin] 场景中存在多个 TwinRuntime，已销毁重复的：" + name);
                Destroy(gameObject);
                return;
            }

            Instance = this;
            Bus = new TwinDataBus();
            Registry = new TwinRegistry();
        }

        private void Update()
        {
            if (Bus == null) return;

            Bus.Flush(_maxDispatchPerFrame);

            if (_logBusStatsEverySeconds > 0f && Time.realtimeSinceStartup >= _nextStatsLog)
            {
                _nextStatsLog = Time.realtimeSinceStartup + _logBusStatsEverySeconds;
                TwinBusStats st = Bus.GetStats();
                Debug.Log("[Twin] 收 " + st.Received + " / 发 " + st.Dispatched + " / 丢 " + st.Dropped +
                          " / 无主 " + st.Orphan + " / 待处理 " + st.PendingInbox +
                          " / 订阅 " + st.SubscriberCount);
            }
        }

        public void RegisterSource(DataSourceBase source)
        {
            if (source == null || _sources.Contains(source)) return;
            _sources.Add(source);
        }

        public void UnregisterSource(DataSourceBase source)
        {
            if (source == null) return;
            _sources.Remove(source);
        }

        public void StopAllSources()
        {
            for (int i = 0; i < _sources.Count; i++)
            {
                if (_sources[i] != null) _sources[i].StopSource();
            }
        }

        private void OnDestroy()
        {
            StopAllSources();
            if (Bus != null) Bus.Clear();
            if (Registry != null) Registry.Clear();
            if (Instance == this) Instance = null;
        }

        private void OnApplicationQuit()
        {
            StopAllSources();
        }
    }
}

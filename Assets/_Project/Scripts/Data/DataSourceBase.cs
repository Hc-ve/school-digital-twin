using System;
using System.Threading;
using UnityEngine;
using SchoolTwin.Core;
using SchoolTwin.Twin;

namespace SchoolTwin.Data
{
    /// <summary>
    /// 数据源基类：把「线程管理 + 状态机 + 退避重连」这些每个数据源都要写一遍的东西收拢在一处。
    ///
    /// 关键约束（违反必然出诡异 bug）：
    ///   1. RunWorker 跑在**后台线程**，禁止调用任何 Unity API（Time / Debug 之外的对象访问都不行）；
    ///   2. 需要时间就用 Stopwatch 或 DateTime.UtcNow，不要用 Time.realtimeSinceStartup；
    ///   3. 出数据只能走 Sink.Emit，由主线程的 TwinRuntime 统一分发。
    /// </summary>
    public abstract class DataSourceBase : MonoBehaviour, IDataSource
    {
        [Header("通用")]
        [Tooltip("OnEnable 时自动开始采集")]
        [SerializeField] protected bool _autoStart = true;

        private Thread _worker;
        private CancellationTokenSource _cts;
        private volatile bool _running;
        private DataSourceState _state = DataSourceState.Idle;

        public abstract string SourceName { get; }
        public DataSourceState State { get { return _state; } }
        public bool IsRunning { get { return _running; } }

        public event Action<DataSourceState> StateChanged;

        /// <summary>数据出口。实现方在 RunWorker 里调用它，可从任意线程调用。</summary>
        protected IDataSink Sink { get; private set; }

        public virtual string StatusText
        {
            get { return SourceName + "[" + _state + "]"; }
        }

        // ---------------------------------------------------------------- 生命周期

        protected virtual void OnEnable()
        {
            TwinRuntime runtime = TwinRuntime.Ensure();
            runtime.RegisterSource(this);
            // 用 runtime.Sink 而不是 runtime.Bus：录制器可能套在中间
            if (_autoStart) StartSource(runtime.Sink);
        }

        protected virtual void OnDisable()
        {
            StopSource();
            TwinRuntime runtime = TwinRuntime.Instance;
            if (runtime != null) runtime.UnregisterSource(this);
        }

        // ---------------------------------------------------------------- 启停

        public void StartSource(IDataSink sink)
        {
            if (_running) return;

            if (sink == null)
            {
                Debug.LogError("[Twin] 数据源 " + SourceName + " 启动失败：sink 为 null");
                SetState(DataSourceState.Faulted);
                return;
            }

            Sink = sink;
            _cts = new CancellationTokenSource();
            _running = true;
            SetState(DataSourceState.Connecting);

            try
            {
                _worker = new Thread(WorkerEntry);
                _worker.IsBackground = true;
                _worker.Name = "TwinSource-" + SourceName;
                _worker.Start();
            }
            catch (Exception e)
            {
                Debug.LogError("[Twin] 数据源 " + SourceName + " 线程启动失败：" + e.Message);
                _running = false;
                SetState(DataSourceState.Faulted);
            }
        }

        /// <summary>不重启线程，只换数据出口（供录制器挂入时使用）。</summary>
        public void BindSink(IDataSink sink)
        {
            if (sink != null) Sink = sink;
        }

        public void StopSource()
        {
            if (!_running)
            {
                if (_state != DataSourceState.Idle && _state != DataSourceState.Stopped)
                    SetState(DataSourceState.Stopped);
                return;
            }

            _running = false;
            try { if (_cts != null) _cts.Cancel(); } catch (Exception) { }

            // 只等 300ms：等太久会卡住主线程，等不到就让它后台自然退出
            try
            {
                if (_worker != null && _worker.IsAlive) _worker.Join(300);
            }
            catch (Exception) { }

            _worker = null;
            if (_cts != null)
            {
                try { _cts.Dispose(); } catch (Exception) { }
                _cts = null;
            }

            OnStopped();
            SetState(DataSourceState.Stopped);
        }

        private void WorkerEntry()
        {
            CancellationToken token = _cts != null ? _cts.Token : CancellationToken.None;
            try
            {
                RunWorker(token);
                SetState(DataSourceState.Stopped);
            }
            catch (OperationCanceledException)
            {
                SetState(DataSourceState.Stopped);
            }
            catch (Exception e)
            {
                Debug.LogError("[Twin] 数据源 " + SourceName + " 异常退出：" + e);
                SetState(DataSourceState.Faulted);
            }
            finally
            {
                _running = false;
            }
        }

        // ---------------------------------------------------------------- 子类实现

        /// <summary>后台线程主循环。token 取消即应尽快返回。</summary>
        protected abstract void RunWorker(CancellationToken token);

        /// <summary>停止后的清理钩子（主线程调用）。</summary>
        protected virtual void OnStopped() { }

        // ---------------------------------------------------------------- 工具

        /// <summary>注意：可能在工作线程被调用，订阅方不要在里面碰 Unity 对象。</summary>
        protected void SetState(DataSourceState state)
        {
            if (_state == state) return;
            _state = state;
            Action<DataSourceState> handler = StateChanged;
            if (handler != null) handler(state);
        }

        /// <summary>指数退避：1s, 2s, 4s, 8s … 上限 30s。用于断线重连。</summary>
        protected static float BackoffSeconds(int attempt, float baseSeconds = 1f, float maxSeconds = 30f)
        {
            if (attempt < 1) attempt = 1;
            float v = baseSeconds * Mathf.Pow(2f, Mathf.Min(attempt - 1, 8));
            return Mathf.Min(v, maxSeconds);
        }

        private static readonly DateTime s_epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>Unix 时间戳（秒）。线程安全，替代无法在工作线程使用的 Time。</summary>
        protected static double NowUtcSeconds()
        {
            return (DateTime.UtcNow - s_epoch).TotalSeconds;
        }
    }
}

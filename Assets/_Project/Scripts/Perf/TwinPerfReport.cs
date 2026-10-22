using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using SchoolTwin.Core;
using SchoolTwin.Twin;
using Debug = UnityEngine.Debug;

namespace SchoolTwin.Perf
{
    /// <summary>
    /// P1-E 性能采样器：把「帧时间 / GC / 总线吞吐」按固定窗口统计后落成 CSV。
    ///
    /// 为什么不用 Profiler 手抄数字：
    ///   · 压测要跑 100/200/500 三档、每档几十秒，手工抄数字既慢又不可复核；
    ///   · Profiler 的 GC Alloc 是「本帧分配」，而压测关心的是「稳定期均值」，
    ///     单帧读数会被偶发尖峰带偏；
    ///   · 落成 CSV 才能进 git、才能事后画曲线、才能写进验收报告当证据。
    ///
    /// 【自身零 GC 约束】这个脚本是「测量工具」，如果它自己每帧分配内存，
    /// 那测出来的 GC 数据就是它自己造成的假象。因此：
    ///   1. Update 里不做任何字符串拼接、不装箱、不 LINQ、不 foreach 字典；
    ///   2. 样本写进预分配的 float[] / int[] 环形缓冲，只累加不分配；
    ///   3. 只有「一个窗口结束时」才拼一次字符串并写盘（默认 1 秒一次，代价可忽略）。
    ///
    /// 用法：挂在场景任意物体上（编辑器菜单会自动挂到 [TwinRuntime]），
    /// 进 Play 后自动开始采样，跑到设定时长自动停止并落盘，也可按 F6 手动起停。
    /// </summary>
    [AddComponentMenu("SchoolTwin/性能采样 (TwinPerfReport)")]
    public class TwinPerfReport : MonoBehaviour
    {
        // ================================================================ 配置

        [Header("采样")]
        [Tooltip("采样窗口长度（秒）。每满一个窗口往 CSV 追加一行")]
        [SerializeField] private float _windowSeconds = 1f;

        [Tooltip("总采样时长（秒）。到点自动停止并写盘；<=0 表示一直采到手动停止")]
        [SerializeField] private float _durationSeconds = 60f;

        [Tooltip("启动后等待多少秒才开始计入统计。用来跳过加载/预热期的抖动")]
        [SerializeField] private float _warmupSeconds = 3f;

        [Tooltip("是否在进入 Play 时自动开始采样")]
        [SerializeField] private bool _autoStart = true;

        [Header("输出")]
        [Tooltip("CSV 输出路径，相对工程根目录")]
        [SerializeField] private string _outputRelativePath = "PerfLogs/twin-perf.csv";

        [Header("交互")]
        [SerializeField] private KeyCode _toggleKey = KeyCode.F6;

        [Tooltip("采样中是否在屏幕上显示实时读数（左上角）")]
        [SerializeField] private bool _showOverlay = true;

        // ================================================================ 运行状态

        private bool _running;
        private float _startRealtime;
        private float _nextWindowEnd;
        private int _windowCount;
        private float _windowFrameTimeSum;
        private int _windowFrameCount;
        private long _windowAllocSum;
        private float _windowWorstFrameMs;
        private int _windowLongFrames;      // 超过 2×窗口均值的帧数（卡顿感）

        // 上个窗口的总线快照，用于算「增量」
        private int _prevReceived;
        private int _prevDispatched;
        private int _prevDropped;
        private int _prevOrphan;

        // 跨窗口累计
        private int _totalLongFrames;
        private float _totalWorstFrameMs = -1f;
        private double _totalAllocBytes;

        private StringBuilder _sb;
        private TwinRuntime _runtime;
        private bool _headerWritten;

        // 实时叠加层用的预分配缓冲（避免 OnGUI 每帧拼字符串）
        private string _overlayText = "";
        private float _overlayRefreshAt;
        private float _lastFps;

        public bool IsRunning { get { return _running; } }
        public string LastOutputPath { get; private set; }

        /// <summary>
        /// 进 Play 是否自动开始采样。
        ///
        /// 【为什么要有这个 setter】本组件现在由 <see cref="TwinPerfBootstrap"/> 在
        /// `AfterSceneLoad` 自动挂上（以前只有压测菜单会挂，正式场景里按 F6 一直没反应）。
        /// 自动挂载时把这里关掉，改成「按 F6 才开始」—— 否则每次 Play 都会自动跑 60 秒、
        /// 屏幕上多一块读数、还顺手写一个 CSV，而用户根本没要求采样。
        ///
        /// 必须在 `Start()` 之前赋值（Awake 之后、第一帧之前），所以用 `AfterSceneLoad` 挂。
        /// </summary>
        public bool AutoStart { get { return _autoStart; } set { _autoStart = value; } }

        /// <summary>采样中是否显示左上角实时读数。</summary>
        public bool ShowOverlay { get { return _showOverlay; } set { _showOverlay = value; } }

        // ================================================================ 生命周期

        private void Awake()
        {
            _sb = new StringBuilder(512);
            _windowSeconds = Mathf.Max(0.1f, _windowSeconds);
            _warmupSeconds = Mathf.Max(0f, _warmupSeconds);
        }

        private void Start()
        {
            if (_autoStart) Begin();
        }

        private void Update()
        {
            if (Input.GetKeyDown(_toggleKey))
            {
                if (_running) End("手动停止");
                else Begin();
            }

            if (!_running) return;

            float now = Time.realtimeSinceStartup;
            float elapsed = now - _startRealtime;

            // 预热期：只推进计时，不计入统计
            if (elapsed < _warmupSeconds)
            {
                _nextWindowEnd = now + _windowSeconds;
                return;
            }

            // 本帧耗时：用 unscaledDeltaTime（真实墙钟），不用 deltaTime
            // —— 压测关心的是「用户实际感受到的帧间隔」，不受 timeScale 影响
            float frameMs = Time.unscaledDeltaTime * 1000f;
            _windowFrameTimeSum += frameMs;
            _windowFrameCount++;
            if (frameMs > _windowWorstFrameMs) _windowWorstFrameMs = frameMs;

            if (now >= _nextWindowEnd)
            {
                _nextWindowEnd = now + _windowSeconds;
                CloseWindow(elapsed);
            }

            // 到点自动收尾
            if (_durationSeconds > 0f && elapsed - _warmupSeconds >= _durationSeconds)
            {
                End("达到设定时长 " + _durationSeconds.ToString("0.#") + "s");
            }
        }

        private void OnDestroy()
        {
            if (_running) End("场景卸载");
        }

        // ================================================================ 启停

        public void Begin()
        {
            if (_running) return;

            _runtime = TwinRuntime.Instance != null ? TwinRuntime.Instance : TwinRuntime.Ensure();
            _running = true;
            _startRealtime = Time.realtimeSinceStartup;
            _nextWindowEnd = _startRealtime + _windowSeconds;
            _windowCount = 0;
            _totalLongFrames = 0;
            _totalWorstFrameMs = -1f;
            _totalAllocBytes = 0d;
            // 重置分配基线：取当前累计值作为零点，之后的差值才是本次采样的分配量
            _lastAllocatedBytes = GC.GetAllocatedBytesForCurrentThread();
            ResetWindowAccumulators();

            SnapshotBus(out _prevReceived, out _prevDispatched, out _prevDropped, out _prevOrphan);
            WriteHeaderIfNeeded();

            Debug.Log("[Perf] 开始采样：窗口 " + _windowSeconds.ToString("0.#") + "s，预热 " +
                      _warmupSeconds.ToString("0.#") + "s，时长 " +
                      (_durationSeconds > 0f ? _durationSeconds.ToString("0.#") + "s" : "直到手动停止") +
                      "，输出 " + OutputPath());
        }

        public void End(string reason)
        {
            if (!_running) return;
            _running = false;

            Debug.Log("[Perf] 采样结束（" + reason + "）：共 " + _windowCount + " 个窗口，累计 GC " +
                      (_totalAllocBytes / 1024d).ToString("0.0") + " KB，最长帧 " +
                      _totalWorstFrameMs.ToString("0.00") + " ms，超长帧 " + _totalLongFrames + " 次。\n" +
                      "  → 数据已写入 " + OutputPath());
        }

        private void ResetWindowAccumulators()
        {
            _windowFrameTimeSum = 0f;
            _windowFrameCount = 0;
            _windowAllocSum = 0;
            _windowWorstFrameMs = -1f;
            _windowLongFrames = 0;
        }

        // ================================================================ 窗口结算

        private void CloseWindow(float elapsed)
        {
            if (_windowFrameCount <= 0)
            {
                // 空窗口（例如编辑器暂停、极低帧率）：只推进分配基线，
                // 否则下一个窗口会把这两段之间的分配量合并计算，造成虚高。
                _lastAllocatedBytes = GC.GetAllocatedBytesForCurrentThread();
                ResetWindowAccumulators();
                return;
            }

            float avgMs = _windowFrameTimeSum / _windowFrameCount;
            float avgFps = avgMs > 0.0001f ? 1000f / avgMs : 0f;
            _lastFps = avgFps;

            // 本窗口 GC 分配量。
            //
            // 【踩过的坑】一开始想用 GC.GetTotalMemory(false) 的差值，但那个 API 返回的是
            // 「托管堆当前占用」而非「累计分配量」：堆被回收后会变小，差值出现负数。
            // 若把负增量当 0 处理，只要窗口内发生过 GC，这一窗口的分配就被整段吞掉，
            // 测出来的 GC 数字会系统性偏小 —— 写进验收报告就是假数据。
            //
            // 正确做法：GC.GetAllocatedBytesForCurrentThread()，返回「当前线程自启动以来
            // 的累计分配字节数」。它是单调递增的，不受回收影响，且开销极低（读一个计数器）。
            // 压测关心的是主线程分配（渲染/脚本都在主线程），所以这个口径正好。
            long currentAlloc = GC.GetAllocatedBytesForCurrentThread();
            long delta = _lastAllocatedBytes < 0 ? 0 : currentAlloc - _lastAllocatedBytes;
            if (delta < 0) delta = 0;   // 理论上不会出现；防御性处理
            _lastAllocatedBytes = currentAlloc;
            _windowAllocSum = delta;
            _totalAllocBytes += delta;

            // 超长帧：以 2 倍窗口均值为阈值，统计「明显卡顿」的帧
            float longThreshold = avgMs * 2f;
            if (_windowWorstFrameMs > longThreshold) _windowLongFrames++;
            _totalLongFrames += _windowLongFrames;
            if (_windowWorstFrameMs > _totalWorstFrameMs) _totalWorstFrameMs = _windowWorstFrameMs;

            // 总线增量
            int recv, disp, drop, orphan, pending, subs;
            SnapshotBus(out recv, out disp, out drop, out orphan);
            pending = PendingCount();
            subs = SubscriberCount();
            int dRecv = recv - _prevReceived;
            int dDisp = disp - _prevDispatched;
            int dDrop = drop - _prevDropped;
            int dOrphan = orphan - _prevOrphan;
            _prevReceived = recv;
            _prevDispatched = disp;
            _prevDropped = drop;
            _prevOrphan = orphan;

            int twinCount = _runtime != null && _runtime.Registry != null ? _runtime.Registry.Count : 0;

            AppendRow(elapsed, avgFps, avgMs, _windowWorstFrameMs, _windowAllocSum,
                      dRecv, dDisp, dDrop, dOrphan, pending, subs, twinCount,
                      CountByStatus(TwinStatus.Normal), CountByStatus(TwinStatus.Warning),
                      CountByStatus(TwinStatus.Alarm), CountByStatus(TwinStatus.Stale),
                      CountByStatus(TwinStatus.Offline));

            _windowCount++;
            ResetWindowAccumulators();

            if (_showOverlay) RefreshOverlay(avgFps, avgMs, pending, dRecv, twinCount);
        }

        /// <summary>
        /// 上次读到的「本线程累计分配字节数」。初值 -1 表示还没读过第一次，
        /// 第一次读只做基线、不产生数据（否则会把进程启动至今的所有分配算进第一个窗口）。
        /// </summary>
        private long _lastAllocatedBytes = -1;

        private void SnapshotBus(out int received, out int dispatched, out int dropped, out int orphan)
        {
            received = 0; dispatched = 0; dropped = 0; orphan = 0;
            if (_runtime == null || _runtime.Bus == null) return;

            TwinBusStats st = _runtime.Bus.GetStats();
            received = st.Received;
            dispatched = st.Dispatched;
            dropped = st.Dropped;
            orphan = st.Orphan;
        }

        private int PendingCount()
        {
            return _runtime != null && _runtime.Bus != null ? _runtime.Bus.PendingCount : 0;
        }

        private int SubscriberCount()
        {
            return _runtime != null && _runtime.Registry != null ? _runtime.Registry.Count : 0;
        }

        /// <summary>按状态统计点位数。TwinRegistry 已提供该接口，避免自己遍历。</summary>
        private int CountByStatus(TwinStatus status)
        {
            return _runtime != null && _runtime.Registry != null ? _runtime.Registry.CountByStatus(status) : 0;
        }

        // ================================================================ 输出

        private string OutputPath()
        {
            string root = Directory.GetParent(Application.dataPath).FullName;
            return Path.Combine(root, _outputRelativePath);
        }

        private void WriteHeaderIfNeeded()
        {
            string path = OutputPath();
            LastOutputPath = path;
            if (_headerWritten) return;
            _headerWritten = true;

            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                bool exists = File.Exists(path);
                using (StreamWriter writer = new StreamWriter(path, true, new UTF8Encoding(false)))
                {
                    if (!exists || new FileInfo(path).Length == 0)
                    {
                        writer.WriteLine(
                            "elapsed_s,avg_fps,avg_frame_ms,worst_frame_ms,gc_alloc_bytes," +
                            "recv,dispatched,dropped,orphan,pending,subscribers,twins," +
                            "normal,warning,alarm,stale,offline");
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[Perf] 无法写入 CSV：" + e.Message);
            }
        }

        private void AppendRow(float elapsed, float fps, float avgMs, float worstMs, long alloc,
                               int recv, int disp, int drop, int orphan, int pending, int subs, int twins,
                               int normal, int warn, int alarm, int stale, int offline)
        {
            // 这一处是唯一允许拼接字符串的地方，且只在窗口结束时发生
            _sb.Length = 0;
            _sb.Append(elapsed.ToString("0.00", CultureInfo.InvariantCulture)).Append(',')
               .Append(fps.ToString("0.0", CultureInfo.InvariantCulture)).Append(',')
               .Append(avgMs.ToString("0.00", CultureInfo.InvariantCulture)).Append(',')
               .Append(worstMs.ToString("0.00", CultureInfo.InvariantCulture)).Append(',')
               .Append(alloc).Append(',')
               .Append(recv).Append(',')
               .Append(disp).Append(',')
               .Append(drop).Append(',')
               .Append(orphan).Append(',')
               .Append(pending).Append(',')
               .Append(subs).Append(',')
               .Append(twins).Append(',')
               .Append(normal).Append(',')
               .Append(warn).Append(',')
               .Append(alarm).Append(',')
               .Append(stale).Append(',')
               .Append(offline);

            try
            {
                using (StreamWriter writer = new StreamWriter(OutputPath(), true, new UTF8Encoding(false)))
                {
                    writer.WriteLine(_sb.ToString());
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[Perf] 写 CSV 失败：" + e.Message);
            }
        }

        // ================================================================ 实时叠加层

        private int _ovFps;
        private float _ovMs;
        private int _ovPending;
        private int _ovRecvRate;
        private int _ovTwins;

        private void RefreshOverlay(float fps, float ms, int pending, int recvDelta, int twins)
        {
            _ovFps = Mathf.RoundToInt(fps);
            _ovMs = ms;
            _ovPending = pending;
            _ovRecvRate = recvDelta;
            _ovTwins = twins;
        }

        private void OnGUI()
        {
            if (!_running || !_showOverlay) return;

            float now = Time.realtimeSinceStartup;
            if (now >= _overlayRefreshAt)
            {
                _overlayRefreshAt = now + 0.25f;
                _sb.Length = 0;
                _sb.Append("性能采样中  [").Append(_toggleKey).Append(" 停止]\n")
                   .Append("FPS ").Append(_ovFps)
                   .Append("   帧 ").Append(_ovMs.ToString("0.00")).Append(" ms\n")
                   .Append("点位 ").Append(_ovTwins)
                   .Append("   收/秒 ").Append(_ovRecvRate)
                   .Append("   待处理 ").Append(_ovPending);
                _overlayText = _sb.ToString();
            }

            GUI.color = Color.white;
            GUI.Box(new Rect(10f, 10f, 260f, 76f), GUIContent.none);
            GUI.Label(new Rect(20f, 16f, 250f, 70f), _overlayText);
        }

        // ================================================================ 供编辑器调用的集合设置

        /// <summary>压测脚本用：一次性配好参数，避免在 Inspector 里手填。</summary>
        public void Configure(float windowSeconds, float durationSeconds, float warmupSeconds, string outputRelativePath)
        {
            _windowSeconds = Mathf.Max(0.1f, windowSeconds);
            _durationSeconds = durationSeconds;
            _warmupSeconds = Mathf.Max(0f, warmupSeconds);
            if (!string.IsNullOrEmpty(outputRelativePath)) _outputRelativePath = outputRelativePath;
        }
    }
}

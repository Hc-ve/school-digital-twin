using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityEngine;
using SchoolTwin.Core;

namespace SchoolTwin.Data
{
    /// <summary>
    /// 录像回放器：读取 <see cref="TwinRecorder"/> 落下的 JSONL 文件，按原始时间间隔重新灌回总线。
    ///
    /// ================================================================
    /// 它为什么是一个「数据源」
    /// ================================================================
    /// 这个类继承 <see cref="DataSourceBase"/>，也就是说在系统看来，
    /// 「一个录像文件」和「一个 MQTT 网关」没有区别 —— 都是往总线里吐数据的东西。
    ///
    /// 这个选择带来一个重要好处：**回放时场景侧一行都不用改**。
    /// 不用给 TwinAnchor 加「现在是回放模式」的开关，不用让面板显示不同的东西，
    /// 阈值判定、告警、颜色、趋势箭头全部原样复用。
    ///
    /// 换句话说：录制时那间教室的空调报了什么警，回放时它就报一模一样的警。
    ///
    /// ================================================================
    /// 回放的时间处理
    /// ================================================================
    /// 文件里每条样本带的是**原始时间戳**（录制时的 UTC 秒）。
    /// 回放不是「按文件顺序快速吐完」，而是**重建原来的节奏**：
    /// 第一条与第二条之间隔了 0.5 秒，回放时也隔 0.5 秒（再除以倍速）。
    ///
    /// 这样才能复现「数据慢慢漂移最后越界」这类问题 —— 如果瞬发，这些现象全都会消失。
    /// </summary>
    [AddComponentMenu("SchoolTwin/录像回放器 (TwinPlayer)")]
    [DisallowMultipleComponent]
    public class TwinPlayer : DataSourceBase
    {
        // ---------------------------------------------------------------- 配置

        [Header("录像文件")]
        [Tooltip("相对工程根目录的路径，如 Recordings/twin-20261016-093012.jsonl。留空则用默认目录下最新的一个")]
        [SerializeField] private string _recordingPath = "";

        [Tooltip("每次 Play 都从目录里挑最新的录像文件")]
        [SerializeField] private bool _useLatestIfEmpty = true;

        [Header("回放行为")]
        [Tooltip("倍速。1 = 原速；2 = 两倍速；0.5 = 慢放")]
        [SerializeField] private float _speed = 1f;

        [Tooltip("播完后从头再来")]
        [SerializeField] private bool _loop = true;

        [Tooltip("开头的空闲时间最多压缩到几秒（避免录像开头有几分钟静默时干等）")]
        [SerializeField] private float _maxInitialIdleSeconds = 2f;

        [Tooltip("单帧最多补发多少条（低帧率下防雪崩）")]
        [SerializeField] private int _maxSamplesPerFrame = 2000;

        [Header("调试")]
        [Tooltip("按文件里的时间戳回放，还是按「读到就发」（后者用于只看最终状态）")]
        [SerializeField] private bool _honorTimestamps = true;

        // ---------------------------------------------------------------- 运行时

        private readonly List<RecordingReader.Sample> _samples = new List<RecordingReader.Sample>(65536);

        private double _playbackStartSampleTs;
        private double _playbackStartWallTs;
        private int _cursor;
        private long _emitted;
        private int _loopCount;
        private volatile bool _loaded;
        private string _resolvedPath;
        private string _loadError;

        // ---------------------------------------------------------------- 暂停 / 跳转（2026-11-04 P12 补齐）

        // 【为什么暂停要用「时钟偏移」而不是「卡住线程」】
        // 回放跑在后台线程，最直觉的做法是暂停时让线程 WaitOne 挂着。但那样有两个坏处：
        //   ① 恢复时要把「暂停了多久」补回来，否则一恢复就狂吐积压的样本（雪崩）；
        //   ② 线程挂着时 SetState 之类的状态变化没法及时反映，UI 看着像卡死。
        //
        // 改用「暂停时一直把 _playbackStartWallTs 往前推」：等待循环里的目标时刻
        // 永远算成"还没到"，线程空转但**不吐数据**，恢复时只要停止推这个偏移，
        // 时间轴就从当前样本继续走。不需要额外补时间，也不会雪崩。
        private volatile bool _paused;

        // 跳转请求：由主线程写一个目标**样本时间戳**，后台线程看到后搬到对应游标。
        //
        // 【为什么传时间戳而不是传下标】UI 侧只有「进度条百分比」，它能对应到的
        // 语义是时间轴（第 30% 的时间点），不是第 30% 的样本条数 ——
        // 样本在时间上不是均匀分布的（有的指标 10Hz，有的 2Hz）。
        // 传时间戳，两边对同一件事只有一个定义。
        private double _seekTargetTs = double.NaN;
        private volatile bool _seekPending;

        private volatile float _progress;   // 0~1，给进度条读

        // 整段录像时长。只在 `LoadFile()` 里写一次（后台线程启动时），
        // 之后全程只读 —— 没有并发写，所以不需要 volatile；
        // 为它加 volatile 反而会让 C# 拒绝（volatile 不支持 double）。
        //
        // 【顺序保证】后台线程先写 `_durationSeconds`，再 `SetState(Running)`；
        // UI 侧先看 `SampleCount > 0` 才读它。`SampleCount` 读的是 `_samples.Count`，
        // 而 `_samples` 的写入发生在写 `_durationSeconds` 之前 ——
        // 靠 `volatile _loaded` 的写屏障把顺序固定住（见 LoadFile 尾部）。
        private double _durationSeconds;

        public override string SourceName { get { return "Playback"; } }

        public override string StatusText
        {
            get
            {
                if (!_loaded) return base.StatusText + " 未加载";
                return base.StatusText + " " + _speed.ToString("0.##") + "x  " +
                       _cursor + "/" + _samples.Count +
                       (_loopCount > 0 ? "  第" + (_loopCount + 1) + "轮" : "");
            }
        }

        public int SampleCount { get { return _samples.Count; } }
        public int Cursor { get { return _cursor; } }
        public long EmittedTotal { get { return Interlocked.Read(ref _emitted); } }
        public string ResolvedPath { get { return _resolvedPath; } }
        public string LoadError { get { return _loadError; } }

        // ---------------------------------------------------------------- 播放控制（对外接口）

        /// <summary>是否处于暂停。暂停时数据源仍是 Running，只是不再吐样本。</summary>
        public bool IsPaused { get { return _paused; } }

        /// <summary>回放进度 0~1。未加载时返回 0。</summary>
        public float Progress { get { return _loaded ? Mathf.Clamp01(_progress) : 0f; } }

        /// <summary>整段录像时长（秒）。未加载时返回 0。</summary>
        public float DurationSeconds { get { return (float)Math.Max(0.0, _durationSeconds); } }

        /// <summary>当前播放到第几秒（相对录像开头）。</summary>
        public float PositionSeconds
        {
            get
            {
                if (!_loaded || _samples.Count == 0) return 0f;
                int i = Mathf.Clamp(_cursor, 0, _samples.Count - 1);
                return (float)Math.Max(0.0, _samples[i].Ts - _samples[0].Ts);
            }
        }

        /// <summary>暂停 / 继续。返回切换后是否处于暂停状态。</summary>
        public bool TogglePause()
        {
            _paused = !_paused;
            return _paused;
        }

        public void Pause() { _paused = true; }
        public void Resume() { _paused = false; }

        /// <summary>
        /// 跳到指定秒（相对录像开头）。下一轮后台循环会搬到对应样本。
        ///
        /// 【为什么只写请求、不当场改 _cursor】`_cursor` 由后台线程推进，
        /// 主线程直接写会和它的 `_cursor++` 打架 —— 表现是"跳过去又被改回来"。
        /// 让后台线程在自己的节奏里执行跳转，只有一个写入者。
        /// </summary>
        public void SeekToSeconds(float seconds)
        {
            if (!_loaded || _samples.Count == 0) return;
            double baseTs = _samples[0].Ts;
            double target = baseTs + Math.Max(0.0, seconds);
            _seekTargetTs = target;
            _seekPending = true;
        }

        /// <summary>跳到进度 0~1。</summary>
        public void SeekToProgress(float p)
        {
            SeekToSeconds(Mathf.Clamp01(p) * DurationSeconds);
        }

        /// <summary>倍速（运行期可改，下一轮等待立即生效）。</summary>
        public float Speed
        {
            get { return _speed; }
            set { _speed = Mathf.Clamp(value, 0.1f, 16f); }
        }

        /// <summary>是否循环。</summary>
        public bool Loop
        {
            get { return _loop; }
            set { _loop = value; }
        }

        /// <summary>重新加载（可换文件后重来）。返回样本数，失败 -1。</summary>
        public int Reload()
        {
            return LoadFile();
        }

        public string RecordingPath
        {
            get { return _recordingPath; }
            set { _recordingPath = value; }
        }

        // ---------------------------------------------------------------- 加载

        /// <summary>
        /// 打开 Play 前（编辑器里）也可以调用，用于「先看看这个文件有多大、多少条」。
        /// 返回读取到的样本数；失败返回 -1，原因见 <see cref="LoadError"/>。
        /// </summary>
        public int LoadFile()
        {
            _loaded = false;
            _loadError = null;
            _samples.Clear();
            _cursor = 0;
            _loopCount = 0;
            Interlocked.Exchange(ref _emitted, 0);

            string path = ResolvePath();
            if (string.IsNullOrEmpty(path))
            {
                _loadError = "找不到录像文件。请把 _recordingPath 填成一个存在的 .jsonl 文件，" +
                             "或把文件放进工程根目录的 Recordings/ 文件夹。";
                return -1;
            }
            _resolvedPath = path;

            // 解析统一交给 RecordingReader —— 编辑器菜单「检验录像文件」用的是同一个类，
            // 两处行为不会漂移。这里不自己再写一遍解析。
            string error;
            List<RecordingReader.Sample> loaded = RecordingReader.Load(path, out error);

            if (loaded == null)
            {
                _loadError = error;
                return -1;
            }

            _samples.AddRange(loaded);

            // 时长与初始进度：一次算好，UI 每帧只读，不重复遍历
            _durationSeconds = _samples.Count > 1
                ? _samples[_samples.Count - 1].Ts - _samples[0].Ts
                : 0.0;
            _progress = 0f;
            _paused = false;
            _seekPending = false;
            _seekTargetTs = double.NaN;

            // ⚠️ `_loaded` 必须**最后**置 true。
            //
            // 它是 volatile，写入带释放屏障 —— 放在这里，能保证上面 `_samples`
            // 与 `_durationSeconds` 的写入对读线程可见。UI 侧 `Progress` 的实现是
            // `_loaded ? clamp(_progress) : 0`，一旦看到 `_loaded == true`，
            // `_progress` 和 `_durationSeconds` 必然已经是算好的值。
            //
            // 反过来的写法（先 _loaded = true 再算时长）会让 UI 在最开始那几帧
            // 读到 duration = 0，进度条算成 0%，时间显示 00:00 / 00:00。
            _loaded = true;

            return _samples.Count;
        }

        private string ResolvePath()
        {
            string path = _recordingPath;
            if (!string.IsNullOrEmpty(path) && !Path.IsPathRooted(path))
                path = Path.Combine(RecordingReader.ProjectRoot(), path);

            if (!string.IsNullOrEmpty(path) && File.Exists(path)) return path;

            if (_useLatestIfEmpty) return FindLatestRecording();

            return null;
        }

        private string FindLatestRecording()
        {
            // 【为什么改成走 RecordingReader.ListRecordings】原来这里自己 GetFiles +
            // 比 LastWriteTime，而 UI 侧（数据源页的录像清单）用的是 ListRecordings。
            // 两套排序规则只要有一处不同（这里是本地时间、那边是 UTC），
            // 就会出现「列表里第一个」和「实际回放的那个」不是同一个文件 ——
            // 而且两边看起来都很正常，极难发现。
            //
            // ListRecordings 已按 ModifiedUtc 新的在前排好，取第一个即可。
            List<RecordingReader.RecordingEntry> list = RecordingReader.ListRecordings();
            if (list == null || list.Count == 0) return null;
            return list[0].Path;
        }

        // ---------------------------------------------------------------- 回放主循环（后台线程）

        protected override void RunWorker(CancellationToken token)
        {
            int count = LoadFile();
            if (count <= 0)
            {
                Debug.LogError("[Twin] 回放启动失败：" + _loadError);
                return;
            }

            SetState(DataSourceState.Running);
            Debug.Log("[Twin] 回放已开始：" + Path.GetFileName(_resolvedPath) +
                      "，共 " + count + " 条，" + _speed.ToString("0.##") + " 倍速" +
                      (_loop ? "，循环" : ""));

            while (!token.IsCancellationRequested)
            {
                // ---- 时间基准 ----
                // 把录像里第一条数据的时间戳对齐到「现在」，之后按原始间隔推进。
                //
                // 开头可能有长时间静默（例如录像开始后 3 分钟才有第一条数据）。
                // 原样等会让使用者以为卡死了，所以「跳过开头那段空白」，
                // 但最多压缩到 _maxInitialIdleSeconds 秒。
                //
                // 【怎么算才对】要明确找「第一条落在容差之内的样本」，
                // 用它作为起点。不能拿 _cursor 去算 —— 循环开始时 _cursor 恒为 0，
                // 那样 skipped 永远是 0，这个功能等于没写。
                double firstTs = _samples[0].Ts;
                double startTs = firstTs;

                if (_maxInitialIdleSeconds > 0f)
                {
                    for (int i = 0; i < _samples.Count; i++)
                    {
                        if (_samples[i].Ts - firstTs <= _maxInitialIdleSeconds)
                        {
                            startTs = _samples[i].Ts;
                            break;
                        }
                    }
                }

                _playbackStartSampleTs = startTs;
                _playbackStartWallTs = NowUtcSeconds();
                _cursor = 0;

                while (_cursor < _samples.Count)
                {
                    if (token.IsCancellationRequested) return;

                    // ---- 暂停（2026-11-04 P12 补齐）----
                    // 不挂住线程，而是把"时间基准"一直往前推，让下面的等待永远等不到目标时刻。
                    // 恢复时不用补时间，时间轴自然从当前样本继续 —— 不会一恢复就雪崩。
                    if (_paused)
                    {
                        _playbackStartWallTs = NowUtcSeconds()
                                               - (_samples[_cursor].Ts - _playbackStartSampleTs)
                                                 / Math.Max(0.01f, _speed);
                        if (token.WaitHandle.WaitOne(30)) return;
                        continue;
                    }

                    // ---- 跳转（2026-11-04 P12 补齐）----
                    // 由主线程写请求，这里执行 —— `_cursor` 只允许后台线程一个写入者。
                    if (_seekPending)
                    {
                        _seekPending = false;
                        double target = _seekTargetTs;
                        _seekTargetTs = double.NaN;

                        int idx = FindIndexAtOrAfter(target);
                        if (idx >= 0 && idx < _samples.Count)
                        {
                            _cursor = idx;

                            // 时间基准要一起搬，否则跳转后第一帧会按"原始时间 - 现在"算出
                            // 一个巨大的负偏移，瞬间吐出剩余全部样本（等于跳转失效）。
                            _playbackStartWallTs = NowUtcSeconds()
                                                   - (_samples[_cursor].Ts - _playbackStartSampleTs)
                                                     / Math.Max(0.01f, _speed);
                            continue;
                        }
                    }

                    RecordingReader.Sample s = _samples[_cursor];

                    // 起点之前的样本（被压缩掉的那段静默）：直接发，不等待
                    if (_honorTimestamps && s.Ts >= _playbackStartSampleTs)
                    {
                        double offset = (s.Ts - _playbackStartSampleTs) / Math.Max(0.01f, _speed);
                        double targetWall = _playbackStartWallTs + offset;

                        // 分片等待：一次性 sleep 很久会让「停止」按钮反应迟钝
                        while (true)
                        {
                            if (token.IsCancellationRequested) return;
                            if (_paused || _seekPending) break;   // 让上层循环优先处理暂停/跳转
                            double remainMs = (targetWall - NowUtcSeconds()) * 1000.0;
                            if (remainMs <= 0.5) break;
                            int slice = (int)Math.Min(100.0, remainMs);
                            if (slice < 1) slice = 1;
                            if (token.WaitHandle.WaitOne(slice)) return;
                        }

                        // 被打断（暂停/跳转）时不要发这一条，回到循环顶部重新判断
                        if (_paused || _seekPending) continue;
                    }

                    TwinSample outSample = new TwinSample(s.TwinId, s.Metric, s.Value, s.Ts, s.Quality);
                    Sink.Emit(in outSample);
                    Interlocked.Increment(ref _emitted);
                    _cursor++;

                    // 进度：只在后台线程写，UI 只读（volatile float 读写是原子的）
                    if (_durationSeconds > 0.0)
                    {
                        int probe = _cursor < _samples.Count ? _cursor : _samples.Count - 1;
                        _progress = (float)Math.Min(1.0,
                            (_samples[probe].Ts - _samples[0].Ts) / _durationSeconds);
                    }

                    // 每补发一批就让出一次 CPU。
                    // 注意先判 _maxSamplesPerFrame > 0 —— 它是 Inspector 字段，
                    // 万一被填成 0，取模会抛 DivideByZeroException（在后台线程里，
                    // 最终表现为数据源 Faulted，很难查）。
                    if (_honorTimestamps && _maxSamplesPerFrame > 0 &&
                        _cursor % _maxSamplesPerFrame == 0)
                    {
                        Thread.Yield();
                    }
                }

                _loopCount++;

                if (!_loop) break;

                Debug.Log("[Twin] 回放完一轮（累计 " + _emitted + " 条），从头再来");
                if (token.WaitHandle.WaitOne(500)) return;
            }
        }

        /// <summary>
        /// 二分查找「第一个时间戳 &gt;= target」的样本下标。
        ///
        /// 【为什么要二分】两万条样本线性扫一遍在主线程会卡一帧；
        /// 而样本按时间戳写入（录制器顺序追加），天然有序，二分是 O(log n)。
        /// 越界时返回最后一个下标，这样"拖到最右"不会跳失败。
        /// </summary>
        private int FindIndexAtOrAfter(double target)
        {
            int lo = 0, hi = _samples.Count - 1, ans = _samples.Count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (_samples[mid].Ts >= target) { ans = mid; hi = mid - 1; }
                else lo = mid + 1;
            }
            return ans;
        }
    }
}

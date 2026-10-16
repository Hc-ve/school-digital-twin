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
            string dir = RecordingReader.RecordingsDirectory();
            if (!Directory.Exists(dir)) return null;

            string best = null;
            DateTime bestTime = DateTime.MinValue;

            try
            {
                string[] files = Directory.GetFiles(dir, "*.jsonl");
                for (int i = 0; i < files.Length; i++)
                {
                    DateTime t = File.GetLastWriteTime(files[i]);
                    if (t > bestTime)
                    {
                        bestTime = t;
                        best = files[i];
                    }
                }
            }
            catch (Exception) { }

            return best;
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
                            double remainMs = (targetWall - NowUtcSeconds()) * 1000.0;
                            if (remainMs <= 0.5) break;
                            int slice = (int)Math.Min(100.0, remainMs);
                            if (slice < 1) slice = 1;
                            if (token.WaitHandle.WaitOne(slice)) return;
                        }
                    }

                    TwinSample outSample = new TwinSample(s.TwinId, s.Metric, s.Value, s.Ts, s.Quality);
                    Sink.Emit(in outSample);
                    Interlocked.Increment(ref _emitted);
                    _cursor++;

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
    }
}

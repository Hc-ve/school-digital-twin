using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using UnityEngine;
using SchoolTwin.Core;

namespace SchoolTwin.Data
{
    /// <summary>
    /// 数据落盘录制器：把经过数据总线的每一条样本写进一个 JSONL 文件，供事后回放。
    ///
    /// ================================================================
    /// 为什么要做这个（P1-D）
    /// ================================================================
    /// 数字孪生上线后最麻烦的事是「现场出了问题，但复现不了」。
    /// 比如某个房间的空调在下午 3 点报了一次告警，等你赶过去已经恢复正常。
    /// 没有落盘，这个 bug 就只能等它下次再犯。
    ///
    /// 落盘之后：把当时的数据文件拖回来，让 <see cref="TwinPlayer"/> 重放一遍，
    /// 告警会**原样再次出现**。调试从「守着设备等复现」变成「拉个文件就能查」。
    ///
    /// ================================================================
    /// 关键设计：装饰器，而不是修改总线
    /// ================================================================
    /// 本类实现 <see cref="IDataSink"/>，用法是把它套在数据总线外面：
    ///
    ///     数据源 ──Emit──▶ [TwinRecorder] ──Emit──▶ TwinDataBus ──▶ TwinAnchor
    ///                            │
    ///                            └── 同时写文件
    ///
    /// 好处：
    ///   1. **不改 TwinDataBus 一行代码**，也不改任何数据源；
    ///   2. 不录制时就完全不挂，零开销；
    ///   3. 换数据源（Mock / MQTT / 回放）自动都录得到，因为它在二者之间。
    ///
    /// ================================================================
    /// 线程安全
    /// ================================================================
    /// 数据源在**后台线程**调用 Emit，而文件写入也是 IO 操作，直接同步写会拖慢数据源线程。
    /// 所以这里用「无锁队列 + 独立写线程」：Emit 只入队（极快，不阻塞数据源），
    /// 写线程在后台慢慢落盘。
    ///
    /// 队列有上限，超限丢弃并计数 —— 宁可丢几条录像，也不能让录制把实时数据链拖垮。
    /// （这是从 P1-E 压测学到的：任何一处无界队列最后都会变成内存炸弹。）
    /// </summary>
    [AddComponentMenu("SchoolTwin/数据录制器 (TwinRecorder)")]
    [DisallowMultipleComponent]
    public class TwinRecorder : MonoBehaviour, IDataSink
    {
        // ---------------------------------------------------------------- 配置

        [Header("开关")]
        [Tooltip("OnEnable 时自动开始录制")]
        [SerializeField] private bool _autoStart = false;

        [Header("落盘位置")]
        [Tooltip("相对工程根目录。留空则用 Recordings/")]
        [SerializeField] private string _outputFolder = "Recordings";

        [Tooltip("文件名前缀，最终形如 twin-20261016-093012.jsonl")]
        [SerializeField] private string _filePrefix = "twin";

        [Tooltip("单个录像文件的最大分钟数，超过则自动换新文件。0 = 不限制")]
        [SerializeField] private float _maxDurationMinutes = 0f;

        [Header("容量保护")]
        [Tooltip("写队列上限。超过则丢弃并计入 DropCount，防止录制拖垮实时链路")]
        [SerializeField] private int _maxQueuedLines = 100000;

        [Tooltip("写盘缓冲区大小（字节）。越大越少 IO 次数，越大也越占内存")]
        [SerializeField] private int _fileBufferBytes = 65536;

        [Tooltip("每写多少条 flush 一次。0 = 只在关闭时 flush（性能最好，但崩溃会丢尾部）")]
        [SerializeField] private int _flushEveryLines = 2000;

        // ---------------------------------------------------------------- 运行时状态

        private readonly Queue<string> _pending = new Queue<string>(4096);
        private readonly object _lock = new object();

        private Thread _writer;
        private volatile bool _running;
        private volatile bool _writerDone;

        private string _currentPath;
        private long _written;
        private long _dropped;
        private long _received;
        private double _startedAtUtc;
        private int _maxQueued;

        /// <summary>被本录制器包裹的下游（正常就是 TwinRuntime.Bus）。</summary>
        private IDataSink _inner;

        public bool IsRecording { get { return _running; } }
        public string CurrentPath { get { return _currentPath; } }
        public long WrittenCount { get { return Interlocked.Read(ref _written); } }
        public long DroppedCount { get { return Interlocked.Read(ref _dropped); } }
        public long ReceivedCount { get { return Interlocked.Read(ref _received); } }
        public int QueueLength { get { lock (_lock) { return _pending.Count; } } }

        public string StatusText
        {
            get
            {
                if (!_running) return "录制器[停止]";
                return "录制器[运行中] 已写 " + WrittenCount + " / 丢 " + DroppedCount +
                       " / 队列 " + QueueLength;
            }
        }

        // ---------------------------------------------------------------- 生命周期

        private void OnEnable()
        {
            if (_autoStart) StartRecording();
        }

        private void OnDisable()
        {
            StopRecording();
        }

        private void OnApplicationQuit()
        {
            StopRecording();
        }

        // ---------------------------------------------------------------- 启停

        /// <summary>
        /// 开始录制。会把本组件**挂到总线的上游**：
        /// 数据源先发到这里，这里转发给总线，同时写文件。
        /// </summary>
        public void StartRecording()
        {
            if (_running) return;

            TwinRuntime runtime = TwinRuntime.Ensure();
            if (runtime.Bus == null)
            {
                Debug.LogError("[Twin] 录制失败：数据总线未就绪");
                return;
            }

            try
            {
                _currentPath = OpenFile();
            }
            catch (Exception e)
            {
                Debug.LogError("[Twin] 录制失败，无法创建文件：" + e.Message);
                return;
            }

            if (string.IsNullOrEmpty(_currentPath)) return;

            _inner = runtime.Bus;
            _startedAtUtc = NowUtcSeconds();
            _written = 0;
            _dropped = 0;
            _received = 0;
            _maxQueued = 0;

            lock (_lock) { _pending.Clear(); }

            _running = true;
            _writerDone = false;

            _writer = new Thread(WriterLoop);
            _writer.IsBackground = true;
            _writer.Name = "TwinRecorder-Writer";
            _writer.Start();

            // 把订阅方（数据源）的出口指向自己 —— 这是「装饰器」的接线动作
            runtime.RedirectSinkTo(this);
        }

        /// <summary>停止录制并把队列里剩余的行全部刷到磁盘。</summary>
        public void StopRecording()
        {
            if (!_running) return;

            _running = false;
            SignalWriterStop();
            DrainWriter();

            if (_writer != null && _writer.IsAlive) _writer.Join(2000);
            _writer = null;

            // 把出口还给总线。
            // 这里用 Instance 而不是 Ensure()：停止时若运行时已经不在了（例如退出播放），
            // 就没有「还回去」的必要，也不该顺手再造一个出来。
            // 但万一 Instance 意外为 null 而出口还挂在录制器上，会变成脏状态，
            // 所以还是兜一层 —— 下一句 if 判断即是兜底。
            TwinRuntime runtime = TwinRuntime.Instance;
            if (runtime != null) runtime.RestoreSink();

            Debug.Log("[Twin] 录制已停止：已写 " + WrittenCount + " 条" +
                      (DroppedCount > 0 ? "，丢弃 " + DroppedCount + " 条（写队列积压）" : "") +
                      "  → " + _currentPath);
            _inner = null;
        }

        private void SignalWriterStop()
        {
            lock (_lock) { Monitor.PulseAll(_lock); }
        }

        private void DrainWriter()
        {
            // 最多等 3 秒让队列排空，避免退出时卡死编辑器
            float deadline = Time.realtimeSinceStartup + 3f;
            while (Time.realtimeSinceStartup < deadline)
            {
                int left;
                lock (_lock) { left = _pending.Count; }
                if (left == 0 || _writerDone) break;
                Thread.Sleep(10);
            }
        }

        // ---------------------------------------------------------------- IDataSink（被数据源调用，可能在任何线程）

        /// <summary>
        /// 数据源调用入口。**可能运行在后台线程**，所以这里只做两件事：
        ///   1. 把样本转成一行文本入队；
        ///   2. 原样转发给总线。
        /// 不做任何 IO、不碰任何 Unity API。
        /// </summary>
        public void Emit(in TwinSample sample)
        {
            if (_running)
            {
                Interlocked.Increment(ref _received);
                string line = FormatLine(in sample);

                bool overflow;
                lock (_lock)
                {
                    overflow = _pending.Count >= _maxQueuedLines;
                    if (!overflow)
                    {
                        _pending.Enqueue(line);
                        if (_pending.Count > _maxQueued) _maxQueued = _pending.Count;
                    }
                }

                if (overflow) Interlocked.Increment(ref _dropped);
            }

            // 无论录不录，数据都必须继续流向总线，否则场景就死了
            IDataSink inner = _inner;
            if (inner != null) inner.Emit(in sample);
        }

        /// <summary>
        /// 把一条样本序列化成一行 JSONL。
        ///
        /// 【为什么不用 JsonUtility / Newtonsoft】
        /// 这里是**热路径**（500 点位 @10Hz = 每秒 5000 次调用），
        /// 反射式序列化会产生大量临时对象，把 GC 拉起来 —— 这正是 P1-C 好不容易压下去的东西。
        /// 字段就这几个，手工拼字符串最快，且不产生中间对象。
        ///
        /// 格式（每行一条，字段名刻意短，5000 行/秒下体积差很多）：
        ///   {"t":1730000000.123,"id":"Class2-1/Sensor/Temp_01","m":"Temperature","v":25.3,"q":0}
        /// </summary>
        private static string FormatLine(in TwinSample sample)
        {
            StringBuilder sb = new StringBuilder(128);
            sb.Append("{\"t\":");
            AppendDouble(sb, sample.TimestampUtc);
            sb.Append(",\"id\":\"");
            AppendEscaped(sb, sample.TwinId);
            sb.Append("\",\"m\":\"");
            AppendEscaped(sb, sample.Metric);
            sb.Append("\",\"v\":");
            AppendFloat(sb, sample.Value);
            sb.Append(",\"q\":");
            sb.Append((int)sample.Quality);
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>
        /// 手写浮点格式化。不用 ToString("R") 是因为它受当前区域设置影响，
        /// 在中文 Windows 上小数点可能变成逗号，导致 JSON 解析失败。
        /// </summary>
        private static void AppendFloat(StringBuilder sb, float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                sb.Append('0');
                return;
            }
            sb.Append(value.ToString("0.######", CultureInfo.InvariantCulture));
        }

        private static void AppendDouble(StringBuilder sb, double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                sb.Append("0");
                return;
            }
            sb.Append(value.ToString("0.###", CultureInfo.InvariantCulture));
        }

        private static void AppendEscaped(StringBuilder sb, string s)
        {
            if (string.IsNullOrEmpty(s)) return;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (c < 0x20) sb.Append(' ');
                else sb.Append(c);
            }
        }

        // ---------------------------------------------------------------- 写线程

        private void WriterLoop()
        {
            StreamWriter writer = null;
            int sinceFlush = 0;
            float fileOpenedRealtime = Time.realtimeSinceStartup;

            try
            {
                writer = new StreamWriter(_currentPath, false, new UTF8Encoding(false), _fileBufferBytes);
                writer.WriteLine("{\"#\":\"twin-recording\",\"v\":1,\"start\":" +
                                 _startedAtUtc.ToString("0.###", CultureInfo.InvariantCulture) + "}");

                while (true)
                {
                    string line = null;
                    bool empty;

                    lock (_lock)
                    {
                        if (_pending.Count == 0)
                        {
                            if (!_running) { empty = true; }
                            else
                            {
                                // 等 100ms 或等到有新数据/停止信号
                                Monitor.Wait(_lock, 100);
                                if (_pending.Count == 0) { empty = !_running ? true : false; }
                                else { empty = false; }
                            }
                        }
                        else empty = false;

                        if (!empty && _pending.Count > 0) line = _pending.Dequeue();
                    }

                    if (line != null)
                    {
                        writer.WriteLine(line);
                        Interlocked.Increment(ref _written);
                        sinceFlush++;

                        if (_flushEveryLines > 0 && sinceFlush >= _flushEveryLines)
                        {
                            writer.Flush();
                            sinceFlush = 0;
                        }

                        // 分卷：超过时长上限就换文件
                        if (_maxDurationMinutes > 0f &&
                            Time.realtimeSinceStartup - fileOpenedRealtime > _maxDurationMinutes * 60f)
                        {
                            writer.Flush();
                            writer.Dispose();
                            _currentPath = OpenFile();
                            writer = new StreamWriter(_currentPath, false, new UTF8Encoding(false), _fileBufferBytes);
                            fileOpenedRealtime = Time.realtimeSinceStartup;
                            sinceFlush = 0;
                        }
                        continue;
                    }

                    // 队列空且已停止 → 收工
                    if (!_running)
                    {
                        // 再抢一次，确保停止瞬间入队的也被写出
                        bool got = false;
                        lock (_lock)
                        {
                            if (_pending.Count > 0) { line = _pending.Dequeue(); got = true; }
                        }
                        if (got && line != null)
                        {
                            writer.WriteLine(line);
                            Interlocked.Increment(ref _written);
                            continue;
                        }
                        break;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[Twin] 录制写线程异常：" + e);
            }
            finally
            {
                if (writer != null)
                {
                    try { writer.Flush(); writer.Dispose(); } catch (Exception) { }
                }
                _writerDone = true;
            }
        }

        // ---------------------------------------------------------------- 文件

        private string OpenFile()
        {
            string dir = _outputFolder;
            if (string.IsNullOrEmpty(dir)) dir = "Recordings";

            if (!Path.IsPathRooted(dir))
                dir = Path.Combine(ProjectRoot(), dir);

            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            string name = _filePrefix + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".jsonl";
            string path = Path.Combine(dir, name);

            // 同秒重开文件时避免覆盖
            int suffix = 1;
            while (File.Exists(path))
            {
                name = _filePrefix + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + suffix + ".jsonl";
                path = Path.Combine(dir, name);
                suffix++;
            }

            return path;
        }

        /// <summary>
        /// 工程根目录。Application.dataPath 是 ".../School/Assets"，
        /// 去掉末尾的 "Assets" 就是根。放工程根而不是 Assets 下，
        /// 是为了避免每次录制都触发 Unity 资源导入刷新。
        /// </summary>
        private static string ProjectRoot()
        {
            string dataPath = Application.dataPath;
            int idx = dataPath.LastIndexOf("/Assets", StringComparison.Ordinal);
            if (idx > 0) return dataPath.Substring(0, idx);
            idx = dataPath.LastIndexOf("\\Assets", StringComparison.Ordinal);
            if (idx > 0) return dataPath.Substring(0, idx);
            return dataPath;
        }

        private static readonly DateTime s_epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private static double NowUtcSeconds()
        {
            return (DateTime.UtcNow - s_epoch).TotalSeconds;
        }
    }
}

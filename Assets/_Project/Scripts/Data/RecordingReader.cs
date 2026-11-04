using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SchoolTwin.Core;

namespace SchoolTwin.Data
{
    /// <summary>
    /// 录像文件读取器（运行时程序集，**public**）。
    ///
    /// ================================================================
    /// 为什么单独抽一个类，而不是把逻辑写在编辑器菜单里
    /// ================================================================
    /// 这个类的直接需求来自编辑器菜单「检验录像文件」，但**解析逻辑必须放在运行时程序集**。
    ///
    /// 原因是 Unity 的预定义程序集划分：
    ///   · `Assets/_Project/Scripts/**` → Assembly-CSharp（运行时）
    ///   · `Assets/_Project/Editor/**`  → Assembly-CSharp-Editor（编辑器）
    ///
    /// 而 JSON 解析器 <see cref="MiniJson"/> 是 `internal` —— **internal 只在本程序集可见**。
    /// 所以编辑器程序集里的代码（`Assets/_Project/Editor/` 下）**看不到 MiniJson**，
    /// 直接调用会报 CS0103「找不到名称 MiniJson」，整个工程编译失败、进不了 Play。
    ///
    /// 把解析逻辑收进这个 public 类之后：
    ///   · 编辑器菜单 → 调用本类（public，跨程序集可见）✓
    ///   · 回放器 → 也调用本类，与菜单用**同一套解析**，行为不会漂移 ✓
    ///   · MiniJson 保持 internal 不变，不必为了编辑器而把内部实现暴露出去 ✓
    ///
    /// 【这个坑的教训】判断可见性不能只看「命名空间一样不一样」，
    /// 要看「文件在不在同一个程序集目录」。`SchoolTwin.Data` 这个命名空间
    /// 在 Scripts/ 和 Editor/ 下都存在时，同名命名空间分属两个程序集。
    /// </summary>
    public static class RecordingReader
    {
        /// <summary>一条录像样本。字段与落盘格式一一对应。</summary>
        public struct Sample
        {
            public double Ts;
            public string TwinId;
            public string Metric;
            public float Value;
            public DataQuality Quality;
        }

        /// <summary>一个录像文件的统计摘要。</summary>
        public sealed class Summary
        {
            public string Path;
            public long FileBytes;
            public long TotalSamples;
            public long UnparsableLines;
            public long MetadataLines;
            public double FirstTimestamp;
            public double LastTimestamp;
            public bool HasTimestamp;
            public List<string> TwinIds = new List<string>();
            public List<string> Metrics = new List<string>();

            public double DurationSeconds
            {
                get { return HasTimestamp ? (LastTimestamp - FirstTimestamp) : 0d; }
            }

            public double AverageRate
            {
                get
                {
                    double d = DurationSeconds;
                    if (d <= 0.0001d) return 0d;
                    return TotalSamples / d;
                }
            }
        }

        // ---------------------------------------------------------------- 逐行解析

        /// <summary>
        /// 解析一行 JSONL。格式：
        ///   {"t":1730000000.123,"id":"Class2-1/Sensor/Temp_01","m":"Temperature","v":25.3,"q":0}
        /// </summary>
        public static bool TryParseLine(string line, out Sample sample)
        {
            sample = default(Sample);
            if (string.IsNullOrEmpty(line)) return false;
            if (line.Length < 3 || line[0] != '{') return false;

            byte[] utf8 = Encoding.UTF8.GetBytes(line);
            MiniJson.Object obj;
            if (!MiniJson.TryParse(utf8, out obj)) return false;

            double ts;
            string id, metric;
            float value;

            if (!obj.TryGetNumber("t", out ts)) return false;
            if (!obj.TryGetString("id", out id)) return false;
            if (!obj.TryGetString("m", out metric)) return false;
            if (!obj.TryGetNumber("v", out value)) return false;
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(metric)) return false;

            sample.Ts = ts;
            sample.TwinId = id;
            sample.Metric = metric;
            sample.Value = value;

            double q;
            if (obj.TryGetNumber("q", out q))
            {
                int qi = (int)q;
                // DataQuality: Good=0 Uncertain=1 Bad=2 Offline=3
                if (qi < 0) qi = 0;
                int max = (int)DataQuality.Offline;
                if (qi > max) qi = max;
                sample.Quality = (DataQuality)qi;
            }
            else sample.Quality = DataQuality.Good;

            return true;
        }

        /// <summary>头部元数据行 {"#":"twin-recording",...} 的识别（不是数据，但要知道它是合法的）。</summary>
        public static bool IsMetadataLine(string line)
        {
            return line != null && line.Length > 3 && line[0] == '{' && line[2] == '#';
        }

        // ---------------------------------------------------------------- 加载

        /// <summary>
        /// 载入整个文件。返回 null 表示失败，原因见 <paramref name="error"/>。
        ///
        /// 结果按时间戳升序排好 —— 录像正常就是有序的，但若中途发生过时钟回拨
        /// （NTP 校时），直接用会让回放时间倒退，所以这里兜一层排序。
        /// </summary>
        public static List<Sample> Load(string path, out string error)
        {
            error = null;
            List<Sample> list = new List<Sample>(65536);

            StreamReader reader = null;
            try
            {
                reader = new StreamReader(path, Encoding.UTF8, true, 65536);

                long len = new FileInfo(path).Length;
                if (len > 0)
                {
                    int guess = (int)Math.Min(len / 64, 4000000);
                    if (guess > list.Capacity) list.Capacity = guess;
                }

                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length == 0) continue;
                    Sample s;
                    if (TryParseLine(line, out s)) list.Add(s);
                }
            }
            catch (Exception e)
            {
                error = "读取失败：" + e.Message;
                return null;
            }
            finally
            {
                if (reader != null) reader.Dispose();
            }

            if (list.Count == 0)
            {
                error = "文件里没有有效样本";
                return null;
            }

            list.Sort(CompareByTimestamp);
            return list;
        }

        public static int CompareByTimestamp(Sample a, Sample b)
        {
            return a.Ts.CompareTo(b.Ts);
        }

        // ---------------------------------------------------------------- 只读统计

        /// <summary>只读扫一遍文件，给出统计摘要（不会把内容留在内存里）。</summary>
        public static Summary Inspect(string path, out string error)
        {
            error = null;
            if (!File.Exists(path))
            {
                error = "文件不存在：" + path;
                return null;
            }

            Summary sum = new Summary();
            sum.Path = path;

            HashSet<string> twins = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> metrics = new HashSet<string>(StringComparer.Ordinal);

            StreamReader reader = null;
            try
            {
                sum.FileBytes = new FileInfo(path).Length;
                reader = new StreamReader(path, Encoding.UTF8, true, 65536);

                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length == 0) continue;
                    if (line[0] != '{') continue;

                    if (IsMetadataLine(line))
                    {
                        sum.MetadataLines++;
                        continue;
                    }

                    Sample s;
                    if (!TryParseLine(line, out s))
                    {
                        sum.UnparsableLines++;
                        continue;
                    }

                    sum.TotalSamples++;
                    twins.Add(s.TwinId);
                    metrics.Add(s.Metric);

                    if (!sum.HasTimestamp)
                    {
                        sum.FirstTimestamp = s.Ts;
                        sum.HasTimestamp = true;
                    }
                    sum.LastTimestamp = s.Ts;
                }
            }
            catch (Exception e)
            {
                error = "读取失败：" + e.Message;
                return null;
            }
            finally
            {
                if (reader != null) reader.Dispose();
            }

            sum.TwinIds.AddRange(twins);
            sum.TwinIds.Sort(StringComparer.Ordinal);
            sum.Metrics.AddRange(metrics);
            sum.Metrics.Sort(StringComparer.Ordinal);

            return sum;
        }

        /// <summary>把摘要渲染成一段人类可读的文本（编辑器对话框与控制台共用）。</summary>
        public static string Describe(Summary sum, int maxListItems = 12)
        {
            if (sum == null) return "（无数据）";

            StringBuilder sb = new StringBuilder(1024);
            sb.Append("文件：").Append(Path.GetFileName(sum.Path)).Append('\n');
            sb.Append("体积：").Append((sum.FileBytes / 1024.0).ToString("0.#")).Append(" KB\n\n");
            sb.Append("样本条数：").Append(sum.TotalSamples).Append('\n');
            sb.Append("无法解析：").Append(sum.UnparsableLines);

            if (sum.TotalSamples > 0 && sum.UnparsableLines * 100 / Math.Max(1L, sum.TotalSamples) < 1)
                sb.Append("（可忽略）");
            sb.Append('\n');

            sb.Append("不同点位：").Append(sum.TwinIds.Count).Append('\n');
            sb.Append("不同指标：").Append(sum.Metrics.Count).Append('\n');

            if (sum.HasTimestamp)
            {
                sb.Append("时间跨度：").Append(sum.DurationSeconds.ToString("0.#")).Append(" 秒\n");
                if (sum.AverageRate > 0)
                    sb.Append("平均频率：").Append(sum.AverageRate.ToString("0.#")).Append(" 条/秒\n");
            }

            if (sum.TwinIds.Count > 0)
            {
                sb.Append("\n点位清单（前 ").Append(maxListItems).Append(" 个）：\n");
                int shown = Math.Min(maxListItems, sum.TwinIds.Count);
                for (int i = 0; i < shown; i++)
                {
                    sb.Append("  · ").Append(sum.TwinIds[i]).Append('\n');
                }
                if (sum.TwinIds.Count > shown)
                    sb.Append("  … 其余 ").Append(sum.TwinIds.Count - shown).Append(" 个");
            }

            return sb.ToString();
        }

        /// <summary>工程根目录（去 Assets 后缀）。编辑器与运行时都要用，放这里共用一份。</summary>
        public static string ProjectRoot()
        {
            string dataPath = UnityEngine.Application.dataPath;
            int idx = dataPath.LastIndexOf("/Assets", StringComparison.Ordinal);
            if (idx > 0) return dataPath.Substring(0, idx);
            idx = dataPath.LastIndexOf("\\Assets", StringComparison.Ordinal);
            if (idx > 0) return dataPath.Substring(0, idx);
            return dataPath;
        }

        /// <summary>默认录像目录。</summary>
        public static string RecordingsDirectory()
        {
            return Path.Combine(ProjectRoot(), "Recordings");
        }

        /// <summary>一条录像文件的清单项（只读文件名/大小/时间，不解析内容）。</summary>
        public struct RecordingEntry
        {
            public string Path;
            public string FileName;
            public long Bytes;
            public DateTime ModifiedUtc;

            /// <summary>给人看的名字：`twin-20261104-091500` 这种，去掉扩展名。</summary>
            public string DisplayName
            {
                get
                {
                    if (string.IsNullOrEmpty(FileName)) return "(空)";
                    int dot = FileName.LastIndexOf('.');
                    return dot > 0 ? FileName.Substring(0, dot) : FileName;
                }
            }

            public string SizeText
            {
                get
                {
                    if (Bytes < 1024) return Bytes + " B";
                    if (Bytes < 1024 * 1024) return (Bytes / 1024f).ToString("0.#") + " KB";
                    return (Bytes / (1024f * 1024f)).ToString("0.#") + " MB";
                }
            }
        }

        /// <summary>
        /// 列出录像目录下所有 .jsonl，按修改时间**新的在前**。
        ///
        /// 【为什么不在 UI 里直接 GetFiles】三处（回放器的"用最新一个"、
        /// 数据源页的录像列表、编辑器菜单）都要这份清单，
        /// 各写一份必然漂移 —— 比如一处按名字排序、一处按时间排序，
        /// 用户看到的"第一个"和实际回放的不是同一个文件。
        ///
        /// 任何 IO 异常都返回空列表，不抛 —— 调用方是 UI，不能因为目录不可读就崩。
        /// </summary>
        public static List<RecordingEntry> ListRecordings()
        {
            List<RecordingEntry> list = new List<RecordingEntry>(8);
            try
            {
                string dir = RecordingsDirectory();
                if (!Directory.Exists(dir)) return list;

                string[] files = Directory.GetFiles(dir, "*.jsonl");
                for (int i = 0; i < files.Length; i++)
                {
                    RecordingEntry e = new RecordingEntry();
                    e.Path = files[i];
                    e.FileName = Path.GetFileName(files[i]);
                    try
                    {
                        FileInfo fi = new FileInfo(files[i]);
                        e.Bytes = fi.Length;
                        e.ModifiedUtc = fi.LastWriteTimeUtc;
                    }
                    catch (Exception) { }
                    list.Add(e);
                }
            }
            catch (Exception)
            {
                return list;
            }

            // 新的在前。用 LastWriteTimeUtc 而不是 LastWriteTime —— 后者是本地时间，
            // 跨时区/夏令时的机器上排序会错位。
            list.Sort(delegate (RecordingEntry a, RecordingEntry b)
            {
                return b.ModifiedUtc.CompareTo(a.ModifiedUtc);
            });
            return list;
        }
    }
}

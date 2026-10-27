using System;
using System.IO;
using UnityEngine;

namespace SchoolTwin.Present
{
    /// <summary>
    /// 【P26】PC / 投影仪用到的视频素材，集中登记。
    ///
    /// ── 为什么要有这个类 ──────────────────────────────────────────────
    /// 之前视频路径散在 `TwinDevicePresenter`（3 个字段）和 `TwinPcMenuPanel`（2 处硬编码），
    /// 换素材要改三四个地方，而且 `Path.Combine` 和 `/` 混用容易出
    /// `StreamingAssets\PC/master/xxx.mp4` 这种半截反斜杠（日志里真出现过）。
    ///
    /// 现在：
    ///   · 路径只在这里定义一次
    ///   · 统一走 `Resolve()`，只拼一次路径、只处理一次分隔符
    ///   · 缺素材时 `Describe()` 会告诉用户"该放到哪个目录"
    ///
    /// ── 换素材怎么操作 ────────────────────────────────────────────────
    /// 把 mp4 丢进 `Assets/StreamingAssets/PC/master/`，文件名对上常量即可，**不用改代码**。
    /// 文件名不符也可以：直接在 Inspector 里改 `_masterBootVideo` 等字段。
    /// </summary>
    public static class PcMediaLibrary
    {
        /// <summary>素材根目录（相对 `StreamingAssets`）。</summary>
        public const string RootFolder = "PC";

        // ---- 文件清单 ----
        /// <summary>开机动画。主脑 / 机房 18 台 / 投影幕布共用。</summary>
        public const string Boot = "master/boot.mp4";

        /// <summary>PPT 图片目录。一页一张，按文件名排序（`slide_01.jpg` …）。</summary>
        public const string SlideFolder = "ppt";

        /// <summary>视频目录。「视频播放」模式在这里取列表，按文件名排序。</summary>
        public const string VideoFolder = "videos";

        /// <summary>
        /// 【P26】「打开PPT」按钮现在改成**图片轮播**，不再播视频。
        /// 保留这个常量只是为了兼容旧配置里的字段值。
        /// </summary>
        public const string Ppt = "master/ppt.mp4";

        /// <summary>旧版「视频播放」的单文件路径（已被 `VideoFolder` 列表取代）。</summary>
        public const string Lesson = "videos/01_数字孪生科普.mp4";

        /// <summary>关机动画（目前没接进流程，预留给"下课时播一段"）。</summary>
        public const string Shutdown = "master/shutdown.mp4";

        /// <summary>
        /// 扫描目录取文件列表（按文件名排序）。
        ///
        /// 【为什么按文件名排序】`Directory.GetFiles` 的返回顺序**不保证**，
        /// 在 NTFS 上通常按目录项顺序，复制/删除几次就乱了。
        /// PPT 页序错了是致命的（第 3 页跑到第 7 页前面），所以必须显式排序。
        /// 约定：文件名用 `slide_01` / `01_xxx` 这种数字前缀。
        /// </summary>
        private static string[] ScanFolder(string subFolder, string pattern)
        {
            string dir = Path.Combine(Application.streamingAssetsPath,
                                      Path.Combine(RootFolder, subFolder).Replace('\\', '/'));
            if (!Directory.Exists(dir)) return new string[0];

            string[] files;
            try
            {
                files = Directory.GetFiles(dir, pattern, SearchOption.TopDirectoryOnly);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[P26] 扫描素材目录失败：" + dir + " — " + e.Message);
                return new string[0];
            }

            if (files == null || files.Length == 0) return new string[0];

            Array.Sort(files, StringComparer.OrdinalIgnoreCase);

            // 转成相对 `PC/` 的路径，和 `Boot` 这类常量保持同一套格式
            string[] rel = new string[files.Length];
            for (int i = 0; i < files.Length; i++)
            {
                string full = files[i].Replace('\\', '/');
                int idx = full.IndexOf("/PC/", StringComparison.OrdinalIgnoreCase);
                rel[i] = idx >= 0 ? full.Substring(idx + 4) : Path.GetFileName(full);
            }
            return rel;
        }

        /// <summary>PPT 图片列表（相对 `PC/` 的路径，按文件名排序）。</summary>
        public static string[] GetSlides()
        {
            string[] jpg = ScanFolder(SlideFolder, "*.jpg");
            string[] png = ScanFolder(SlideFolder, "*.png");
            if (jpg.Length == 0) return png;
            if (png.Length == 0) return jpg;

            // 两种格式混放时合并后再排序
            string[] all = new string[jpg.Length + png.Length];
            Array.Copy(jpg, all, jpg.Length);
            Array.Copy(png, 0, all, jpg.Length, png.Length);
            Array.Sort(all, StringComparer.OrdinalIgnoreCase);
            return all;
        }

        /// <summary>视频列表（相对 `PC/` 的路径，按文件名排序）。</summary>
        public static string[] GetVideos()
        {
            return ScanFolder(VideoFolder, "*.mp4");
        }

        /// <summary>素材目录的绝对路径，报错时展示给用户。</summary>
        public static string FolderPath
        {
            get
            {
                return Path.Combine(Application.streamingAssetsPath,
                                    RootFolder.Replace('\\', '/'));
            }
        }

        /// <summary>
        /// 把相对文件名解析成绝对路径。
        ///
        /// 【为什么要 `Replace('\\','/')`】`Path.Combine` 在 Windows 上用反斜杠拼接，
        /// 而传进来的 `fileName` 里可能已经有 `/`，拼出来就是
        /// `...\StreamingAssets\PC/master/boot.mp4` 这种混合路径。
        /// Unity 的 `VideoPlayer.url` 对这种路径**能读**，但日志里很难看、
        /// 而且 `File.Exists` 在某些平台上会因此失败。统一成 `/` 最稳。
        /// </summary>
        public static string Resolve(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return null;

            string rel = Path.Combine(RootFolder, fileName.Replace('\\', '/'));
            return Path.Combine(Application.streamingAssetsPath, rel).Replace('\\', '/');
        }

        /// <summary>素材是否存在。</summary>
        public static bool Exists(string fileName)
        {
            string full = Resolve(fileName);
            return !string.IsNullOrEmpty(full) && File.Exists(full);
        }

        /// <summary>
        /// 取一个"能用"的视频：首选 `preferred`，不存在就退回 `fallback`。
        /// 两个都没有就返回 null（调用方退回纯色自发光）。
        ///
        /// 【为什么需要 fallback】「打开PPT」「视频播放」的素材是后补的。
        /// 用户点了按钮却发现"没反应"是最差的体验 —— 退回开机视频至少画面在动，
        /// 再配一条日志说明原因。
        /// </summary>
        public static string FirstAvailable(string preferred, string fallback)
        {
            if (Exists(preferred)) return preferred;
            if (Exists(fallback)) return fallback;
            return null;
        }

        /// <summary>把目录里现有的 mp4 列出来，缺素材时贴进日志方便排查。</summary>
        public static string Describe()
        {
            string dir = FolderPath;
            if (!Directory.Exists(dir)) return "目录不存在：" + dir;

            string[] files;
            try
            {
                files = Directory.GetFiles(dir, "*.mp4", SearchOption.AllDirectories);
            }
            catch (Exception e)
            {
                return "读取目录失败：" + e.Message;
            }

            if (files == null || files.Length == 0) return "目录里没有 mp4：" + dir;

            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append("现有 ").Append(files.Length).Append(" 个素材：");
            for (int i = 0; i < files.Length; i++)
            {
                string name = files[i].Replace('\\', '/').Substring(dir.Replace('\\', '/').Length).TrimStart('/');
                sb.Append("\n    ").Append(name);
            }
            return sb.ToString();
        }
    }
}

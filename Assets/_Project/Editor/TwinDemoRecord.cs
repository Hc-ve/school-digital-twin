using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using SchoolTwin.Data;
using SchoolTwin.Twin;

namespace SchoolTwin.EditorTools
{
    /// <summary>
    /// P16 演示用录像入口：一键录 30 秒，到点自己停，并把「录了几条、多大、存哪」打在 Console 里。
    ///
    /// ── 为什么要有它（数据源条上不是已经有「○ 录制」按钮了吗）────────────
    /// 有。但演示的时候那一步是最难做的一步：
    ///   · 录制按钮在**详情卡片的数据源条**上，要先点中一个点位把卡片叫出来，再在一排
    ///     小按钮里找它 —— 一边讲一边找，很容易点成旁边的「Mock / MQTT / 回放」；
    ///   · 录完还要再点一次才停，忘了停就一直写盘，讲到一半磁盘涨了几十兆；
    ///   · 「录了多久」这件事当场是看不出来的（面板上只有「● 录制中」）。
    ///
    /// 所以这里给它一个**不会点错**的入口：菜单点一下，30 秒后自动停，Console 里留下
    /// 一段可以直接念给观众听的结论。产品功能没变，只是把演示路径缩短。
    ///
    /// ── 为什么走 TwinSourceSwitcher 而不是直接调 TwinRecorder ──────────────
    /// 录制器的开关同时影响**面板上的状态**（「○ 录制」/「● 录制中」，以及「回放」按钮
    /// 什么时候从灰变亮）。绕过切换器直接调录制器，文件会正常写，但面板上一直显示
    /// 「○ 录制」——观众看到的就是「按了没反应」。所以只要切换器在，就让它去开。
    ///
    /// ⚠️ 需要 **Play 模式**。编辑模式下点会给明确提示，不会静默无效。
    /// </summary>
    public static class TwinDemoRecord
    {
        private const string Root = "P16 演示/③ 录像/";

        // ---------------------------------------------------------------- 计时状态

        /// <summary>缓存的录制器。每次 Play 都会重新找（域重载后静态字段本来就清空）。</summary>
        private static TwinRecorder s_recorder;

        private static double s_startedAtEditorTime;
        private static double s_stopAtEditorTime;      // 0 = 不限时
        private static float s_plannedSeconds;
        private static bool s_ticking;

        // ================================================================ 菜单

        [MenuItem(Root + "录 30 秒（演示用）", false, 1)]
        private static void Record30Seconds()
        {
            Begin(30f);
        }

        [MenuItem(Root + "开始录制（不限时，手动停）", false, 2)]
        private static void RecordUntilStopped()
        {
            Begin(0f);
        }

        [MenuItem(Root + "停止并保存", false, 20)]
        private static void StopNow()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("[P16] 请先按 Play。");
                return;
            }

            TwinRecorder rec = Resolve();
            if (rec == null)
            {
                Debug.LogWarning("[P16] 场景里找不到录制器（TwinRecorder）。它应该挂在 [TwinRuntime] 下面。");
                return;
            }

            if (!rec.IsRecording)
            {
                Debug.Log("[P16] 当前没有在录制，不需要停止。");
                return;
            }

            Finish("手动停止");
        }

        [MenuItem(Root + "显示录像状态", false, 40)]
        private static void PrintStatus()
        {
            TwinRecorder rec = Resolve();

            StringBuilder sb = new StringBuilder(512);
            sb.Append("[P16] ===== 录像状态 =====\n");

            if (rec == null)
            {
                sb.Append("  录制器：场景里没有（应挂在 [TwinRuntime] 下）\n");
            }
            else if (rec.IsRecording)
            {
                double elapsed = Application.isPlaying
                    ? EditorApplication.timeSinceStartup - s_startedAtEditorTime
                    : 0.0;
                sb.Append("  录制器：运行中，已 ").Append(elapsed.ToString("0.0")).Append(" 秒\n");
                sb.Append("  已写：").Append(rec.WrittenCount).Append(" 条");
                if (rec.DroppedCount > 0) sb.Append("（丢弃 ").Append(rec.DroppedCount).Append(" 条）");
                sb.Append('\n');
                sb.Append("  队列：").Append(rec.QueueLength).Append(" 行待落盘\n");
                sb.Append("  文件：").Append(rec.CurrentPath).Append('\n');
            }
            else
            {
                sb.Append("  录制器：已停止\n");
                sb.Append("  上一次文件：").Append(string.IsNullOrEmpty(rec.CurrentPath) ? "（本次还没录过）" : rec.CurrentPath).Append('\n');
            }

            string latest = FindLatestRecording();
            sb.Append("  目录里最新的录像：").Append(latest ?? "（空）").Append('\n');
            sb.Append("  提示：数据源条上「回放」在路径为空时会自动用最新的那个文件。");

            Debug.Log(sb.ToString());
        }

        [MenuItem(Root + "打开录像目录", false, 41)]
        private static void RevealFolder()
        {
            string dir = null;

            TwinRecorder rec = Resolve();
            if (rec != null && !string.IsNullOrEmpty(rec.CurrentPath))
            {
                try { dir = Path.GetDirectoryName(rec.CurrentPath); } catch (Exception) { }
            }
            if (string.IsNullOrEmpty(dir)) dir = RecordingsFolder();

            if (!Directory.Exists(dir))
            {
                Debug.LogWarning("[P16] 录像目录还不存在：" + dir + "\n  先录一段就会自动创建。");
                return;
            }

            EditorUtility.RevealInFinder(dir);
            Debug.Log("[P16] 已打开：" + dir);
        }

        // ================================================================ 启停

        private static void Begin(float seconds)
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("[P16] 请先按 Play。录制器只在运行时的数据链路上，" +
                                 "编辑模式下按下去只会什么都没发生。");
                return;
            }

            TwinRecorder rec = Resolve();
            if (rec == null)
            {
                Debug.LogWarning("[P16] 场景里找不到录制器（TwinRecorder）。它应该挂在 [TwinRuntime] 下面，" +
                                 "和 MockDataSource / MqttDataSource 同一个物体。");
                return;
            }

            if (rec.IsRecording)
            {
                Debug.LogWarning("[P16] 已经在录了（" + rec.CurrentPath + "）。\n" +
                                 "  想重来请先「停止并保存」——不叠着开第二个文件，是为了避免两个文件里" +
                                 "各有一半数据，回放时对不上。");
                return;
            }

            if (!StartRecording(rec))
            {
                Debug.LogWarning("[P16] 录制没能启动，原因见上面一行日志（通常是录像文件创建失败）。");
                return;
            }

            s_recorder = rec;
            s_startedAtEditorTime = EditorApplication.timeSinceStartup;
            s_plannedSeconds = seconds;
            s_stopAtEditorTime = seconds > 0f ? s_startedAtEditorTime + seconds : 0.0;

            Debug.Log("[P16] ===== 开始录制 =====\n" +
                      (seconds > 0f
                          ? "  时长：" + seconds.ToString("0") + " 秒后自动停止\n"
                          : "  时长：不限时，点「停止并保存」结束\n") +
                      "  文件：" + rec.CurrentPath + "\n" +
                      "  面板上「○ 录制」应变成「● 录制中」。");

            Hook();
        }

        /// <summary>
        /// 开始录制。优先交给切换器（面板状态才跟得上），切换器还没建出来时退回直接开录制器。
        /// </summary>
        private static bool StartRecording(TwinRecorder rec)
        {
            TwinSourceSwitcher sw = FindSwitcher();
            if (sw != null) return sw.ToggleRecord();

            rec.StartRecording();
            return rec.IsRecording;
        }

        private static void StopRecording(TwinRecorder rec)
        {
            TwinSourceSwitcher sw = FindSwitcher();
            if (sw != null && sw.IsRecording)
            {
                sw.ToggleRecord();   // 顺带把「回放」按钮刷成可用
                return;
            }
            rec.StopRecording();
        }

        private static void Finish(string reason)
        {
            Unhook();

            TwinRecorder rec = s_recorder != null ? s_recorder : Resolve();
            if (rec == null)
            {
                Debug.LogWarning("[P16] 录像收尾时找不到录制器了，文件可能还开着。");
                return;
            }

            // 先把统计读出来：StopRecording 里会刷队列、还会打一条自己的日志
            long written = rec.WrittenCount;
            long dropped = rec.DroppedCount;
            string path = rec.CurrentPath;
            double elapsed = EditorApplication.timeSinceStartup - s_startedAtEditorTime;

            if (rec.IsRecording) StopRecording(rec);

            long size = 0;
            try
            {
                FileInfo fi = new FileInfo(path);
                if (fi.Exists) size = fi.Length;
            }
            catch (Exception) { }

            StringBuilder sb = new StringBuilder(512);
            sb.Append("[P16] ===== 录像结束 =====\n");
            sb.Append("  原因：").Append(reason).Append('\n');
            sb.Append("  时长：").Append(elapsed.ToString("0.0")).Append(" 秒\n");
            sb.Append("  样本：").Append(written).Append(" 条");
            if (dropped > 0) sb.Append("（丢弃 ").Append(dropped).Append(" 条，写队列积压）");
            sb.Append('\n');
            sb.Append("  体积：").Append((size / 1024.0).ToString("0.0")).Append(" KB\n");
            sb.Append("  文件：").Append(path).Append('\n');
            sb.Append("  回放：数据源条上点「回放」即可（路径留空时自动用最新的这个文件）。");

            Debug.Log(sb.ToString());

            s_recorder = null;
            s_stopAtEditorTime = 0.0;
        }

        // ================================================================ 计时

        private static void Hook()
        {
            if (s_ticking) return;
            s_ticking = true;
            EditorApplication.update += Tick;
        }

        private static void Unhook()
        {
            if (!s_ticking) return;
            s_ticking = false;
            EditorApplication.update -= Tick;
        }

        /// <summary>
        /// 编辑器每帧回调。只做三件事：发现退出 Play 就收手、发现录制被别处停掉就收手、
        /// 到点了就自动停。
        ///
        /// 【为什么「被别处停掉」也要收手】
        /// 演示时完全可能在面板上手动点了「● 录制中」把它停掉。那时候计时器还在跑，
        /// 30 秒一到就会去 StopRecording 一个已经停掉的录制器 —— 不报错，但会打一条
        /// 「录了 0 条」的日志，看起来像录制坏了。所以这里主动认账。
        /// </summary>
        private static void Tick()
        {
            if (!Application.isPlaying)
            {
                Unhook();
                return;
            }

            TwinRecorder rec = s_recorder != null ? s_recorder : Resolve();
            if (rec == null || !rec.IsRecording)
            {
                Unhook();
                Debug.Log("[P16] 录制已停止（在面板上手动停的？）。");
                return;
            }

            if (s_stopAtEditorTime <= 0.0) return;                    // 不限时
            if (EditorApplication.timeSinceStartup < s_stopAtEditorTime) return;

            Finish("录满 " + s_plannedSeconds.ToString("0") + " 秒，已自动停止");
        }

        // ================================================================ 工具

        private static TwinRecorder Resolve()
        {
            if (s_recorder != null) return s_recorder;
            return UnityEngine.Object.FindObjectOfType<TwinRecorder>();
        }

        private static TwinSourceSwitcher FindSwitcher()
        {
            // 切换器是运行时 AddComponent 挂上的（详情卡片建数据源条时），
            // 所以这里可能返回 null —— 那不是错误，只是面板还没建出来。
            return UnityEngine.Object.FindObjectOfType<TwinSourceSwitcher>();
        }

        private static string RecordingsFolder()
        {
            string dataPath = Application.dataPath;
            string root = dataPath;

            int idx = dataPath.LastIndexOf("/Assets", StringComparison.Ordinal);
            if (idx < 0) idx = dataPath.LastIndexOf("\\Assets", StringComparison.Ordinal);
            if (idx > 0) root = dataPath.Substring(0, idx);

            return Path.Combine(root, "Recordings");
        }

        private static string FindLatestRecording()
        {
            try
            {
                string dir = RecordingsFolder();
                if (!Directory.Exists(dir)) return null;

                string[] files = Directory.GetFiles(dir, "*.jsonl");
                if (files == null || files.Length == 0) return null;

                string best = null;
                DateTime bestTime = DateTime.MinValue;
                for (int i = 0; i < files.Length; i++)
                {
                    DateTime t = File.GetLastWriteTime(files[i]);
                    if (t > bestTime) { bestTime = t; best = files[i]; }
                }
                return best;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}

#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using SchoolTwin.UI;

namespace SchoolTwin.EditorTools
{
    /// <summary>
    /// 【P29-3.5 收尾采集器】请求文件驱动 → 自动进 Play → 开抽屉截 L1 → 进 L2 截 L2 → 写结果 → 退 Play。
    ///
    /// ── 为什么要这么绕 ──────────────────────────────────────────────
    /// 三组数与截图都必须在**真实 Play** 里取：TwinDrawer 是 `RuntimeInitializeOnLoadMethod`
    /// 自举的，编辑器非 Play 状态下压根不存在；而工程同时只能被一个 Unity 实例打开，
    /// 编辑器占着时 batchmode 进不来。所以让正在运行的编辑器自己去跑。
    ///
    /// ── 状态为什么放 SessionState ───────────────────────────────────
    /// 进出 Play 都会触发域重载，普通静态字段会被清空。SessionState 跨域重载保留，
    /// 否则阶段一重载就丢，采集永远卡在第一步。
    ///
    /// ── 用完即可删 ─────────────────────────────────────────────────
    /// 收口期的一次性采集工具，取完证据就可删本文件。
    /// </summary>
    [InitializeOnLoad]
    public static class TwinP29MeasureRunner
    {
        private const string RequestName = "p29_probe.request";
        private const string ResultName = "p29_probe.result.txt";

        private const string KeyStage = "P29Probe.Stage";
        private const string KeyStageAt = "P29Probe.StageAt";
        private const string KeyL1 = "P29Probe.L1";
        private const string KeyL2 = "P29Probe.L2";

        /// <summary>进 Play 后等多久再动（自举 + Registry 注册 + Mock 首批数据）。</summary>
        private const float BootSeconds = 6f;

        /// <summary>开抽屉后等多久截图（滑入 240ms + 布局刷新）。</summary>
        private const float DrawerSettle = 1.2f;

        /// <summary>进 L2 后等多久截图（宽度动画 200ms + 推页 200ms + 首屏构建）。</summary>
        private const float ModuleSettle = 1.6f;

        /// <summary>截完一张后等多久再动（CaptureScreenshot 在帧末落盘）。</summary>
        private const float ShotSettle = 0.5f;

        private enum Stage
        {
            Idle = 0,
            WaitBoot = 1,
            ShotL1 = 2,
            GotoL2 = 3,
            ShotL2 = 4,
            Finish = 5,
        }

        private static double _nextPoll;

        static TwinP29MeasureRunner()
        {
            EditorApplication.update += Tick;
        }

        // ---------------------------------------------------------------- 路径

        /// <summary>
        /// 工程根目录。
        /// 【踩过的坑】不能写 `File.Exists("Temp/xxx")` —— Unity 进程的当前工作目录是
        /// **编辑器安装目录**，不是工程根，相对路径会静默找不到（不报错，就是不触发）。
        /// </summary>
        private static string ProjectRoot
        {
            get { return Directory.GetParent(Application.dataPath).FullName; }
        }

        private static string RequestPath { get { return Path.Combine(ProjectRoot, "Temp", RequestName); } }
        private static string ResultPath { get { return Path.Combine(ProjectRoot, "Temp", ResultName); } }
        private static string EvidenceDir { get { return Path.Combine(ProjectRoot, "Docs", "QA", "evidence"); } }

        // ---------------------------------------------------------------- 状态

        private static Stage Current
        {
            get { return (Stage)SessionState.GetInt(KeyStage, (int)Stage.Idle); }
            set
            {
                SessionState.SetInt(KeyStage, (int)value);
                SessionState.SetFloat(KeyStageAt, (float)EditorApplication.timeSinceStartup);
            }
        }

        private static double StageElapsed
        {
            get
            {
                float at = SessionState.GetFloat(KeyStageAt, 0f);
                if (at <= 0f) return 0.0;
                return EditorApplication.timeSinceStartup - at;
            }
        }

        // ---------------------------------------------------------------- 主循环

        private static void Tick()
        {
            double now = EditorApplication.timeSinceStartup;
            if (now < _nextPoll) return;
            _nextPoll = now + 0.2;

            Stage stage = Current;

            // ---------- 触发 ----------
            if (stage == Stage.Idle)
            {
                if (!File.Exists(RequestPath)) return;
                if (EditorApplication.isPlayingOrWillChangePlaymode) return;

                Debug.Log("[P29-3.5] 收到采集请求，进入 Play…");
                Current = Stage.WaitBoot;
                EditorApplication.EnterPlaymode();
                return;
            }

            if (!EditorApplication.isPlaying) return;

            switch (stage)
            {
                case Stage.WaitBoot:
                    if (StageElapsed < BootSeconds) return;
                    {
                        TwinDrawer drawer = UnityEngine.Object.FindObjectOfType<TwinDrawer>(true);
                        if (drawer == null)
                        {
                            Abort("未找到 TwinDrawer —— 抽屉没自举出来");
                            return;
                        }
                        drawer.Open();
                        Current = Stage.ShotL1;
                    }
                    break;

                case Stage.ShotL1:
                    if (StageElapsed < DrawerSettle) return;
                    SessionState.SetString(KeyL1, TwinLayoutProbe.Capture());
                    Shot("p29_35_L1.png");
                    Current = Stage.GotoL2;
                    break;

                case Stage.GotoL2:
                    if (StageElapsed < ShotSettle) return;
                    {
                        TwinHomePage home = UnityEngine.Object.FindObjectOfType<TwinHomePage>(true);
                        if (home == null || home.OnOpenSystem == null)
                        {
                            Abort("找不到 TwinHomePage.OnOpenSystem —— 进不了 L2");
                            return;
                        }
                        home.OnOpenSystem();
                        Current = Stage.ShotL2;
                    }
                    break;

                case Stage.ShotL2:
                    if (StageElapsed < ModuleSettle) return;
                    SessionState.SetString(KeyL2, TwinLayoutProbe.Capture());
                    Shot("p29_35_L2.png");
                    Current = Stage.Finish;
                    break;

                case Stage.Finish:
                    if (StageElapsed < ShotSettle) return;
                    WriteResult();
                    Current = Stage.Idle;
                    EditorApplication.ExitPlaymode();
                    SafeDelete(RequestPath);

                    // batchmode 没有窗口，跑完必须主动结束进程 —— 否则编辑器会一直挂着不返回。
                    // 交互模式下不能这么做（会把用户的编辑器关掉）。
                    if (Application.isBatchMode)
                    {
                        Debug.Log("[P29-3.5] batchmode 采集完毕，退出编辑器");
                        EditorApplication.Exit(0);
                    }
                    break;
            }
        }

        // ---------------------------------------------------------------- 动作

        private static void Shot(string fileName)
        {
            try
            {
                Directory.CreateDirectory(EvidenceDir);
                string path = Path.Combine(EvidenceDir, fileName);
                ScreenCapture.CaptureScreenshot(path);
                Debug.Log("[P29-3.5] 截图 → " + path);
            }
            catch (Exception e)
            {
                Debug.LogError("[P29-3.5] 截图失败（" + fileName + "）：" + e.Message);
            }
        }

        private static void WriteResult()
        {
            StringBuilder sb = new StringBuilder(4096);
            sb.AppendLine("===== [P29-3.5] 采集结果 =====");
            sb.Append("dll 时间戳：").Append(AssemblyStamp()).AppendLine();
            sb.AppendLine();
            sb.AppendLine("---------- L1（抽屉停在首页）----------");
            sb.AppendLine(SessionState.GetString(KeyL1, "(未采集)"));
            sb.AppendLine("---------- L2（校园管理系统 · 数据监测）----------");
            sb.AppendLine(SessionState.GetString(KeyL2, "(未采集)"));
            sb.AppendLine("截图：Docs/QA/evidence/p29_35_L1.png");
            sb.AppendLine("截图：Docs/QA/evidence/p29_35_L2.png");

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ResultPath));
                File.WriteAllText(ResultPath, sb.ToString());
                Debug.Log("[P29-3.5] 结果已写入 " + ResultPath);
            }
            catch (Exception e)
            {
                Debug.LogError("[P29-3.5] 写结果失败：" + e.Message);
            }
        }

        /// <summary>当前 Assembly-CSharp.dll 的最后写入时间，用于 §6.8 的「已生效」判定。</summary>
        private static string AssemblyStamp()
        {
            try
            {
                string dll = Path.Combine(ProjectRoot, "Library", "ScriptAssemblies", "Assembly-CSharp.dll");
                if (!File.Exists(dll)) return "(找不到 Assembly-CSharp.dll)";
                return File.GetLastWriteTime(dll).ToString("HH:mm:ss");
            }
            catch (Exception e)
            {
                return "(读取失败：" + e.Message + ")";
            }
        }

        private static void Abort(string reason)
        {
            Debug.LogError("[P29-3.5] 采集中止：" + reason);
            WriteResult();
            Current = Stage.Idle;
            EditorApplication.ExitPlaymode();
            SafeDelete(RequestPath);
        }

        private static void SafeDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // 删不掉也无所谓：下次没请求文件就不会触发
            }
        }
    }
}
#endif

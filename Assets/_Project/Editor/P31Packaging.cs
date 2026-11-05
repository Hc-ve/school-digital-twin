using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace SchoolTwin.EditorTools
{
    /// <summary>
    /// 【P31】答辩交付打包：着色器登记 + Windows x64 出包。
    ///
    /// ══════════════════════════════════════════════════════════════════
    /// 【为什么要写这个脚本，而不是让人在 GUI 里点】
    ///
    /// 因为 F22 是一次「设置漏勾 → exe 静默坏掉」的事故，而 GUI 里的勾选
    /// 是**无法被 git 追踪、无法被判据检查、也无法复现**的东西。基线 §1.6 F22
    /// 的原话是"编辑器里全好，exe 里全黑"—— 这类问题的共性是：
    /// **失败不报错、只在最终产物里才暴露**。所以它必须落成代码。
    ///
    /// 落成代码之后有三件事同时成立：
    ///   ① 谁出包都是同一套设置（不会有人忘了勾某项）
    ///   ② `git diff` 看得见设置变了什么
    ///   ③ 判据 P31-01/P31-02 可以 grep `ProjectSettings/GraphicsSettings.asset`
    ///      直接验证，不需要打开 Unity
    ///
    /// ══════════════════════════════════════════════════════════════════
    /// 【为什么选「Always Included Shaders」而不是「移进 Resources」】
    ///
    /// 基线 F22 给了两条修法，本项目选①，理由有两条，第二条是决定性的：
    ///
    ///   1. 改动面最小。改法②要把 2 个 .shader + .meta 挪目录、还要改
    ///      `PcScreenPlayer.cs:223` / `PcSlideShow.cs:113` /
    ///      `TwinDevicePresenter.cs:4434/4547` 四处取值方式。答辩前一天
    ///      动四行业务代码，换来的是同一件事，收益为负。
    ///
    ///   2. **改法②对内置着色器根本无效。** 这次 Player.log 里除了两个项目
    ///      shader，还有一条 `找不到 Legacy Shaders/Particles/Additive，投影光柱不画了`。
    ///      内置着色器住在 Unity 安装目录里，**不在 Assets 下，没法移进 Resources**。
    ///      也就是说改法②只能救一半，剩下的一半还得回来补 Always Included。
    ///      既然无论如何都要开这张表，就一次性全登记，别分两套机制。
    ///
    /// ══════════════════════════════════════════════════════════════════
    /// 【登记清单是怎么来的：不是猜的，是 grep 出来的】
    ///
    /// 依据是 `grep -rn "Shader.Find(" Assets/_Project/Scripts` 的全部命中，
    /// 逐条判定"它在不在 Resources 下"：
    ///   · 天气那 4 个（CampusUI/CloudDome、SkyDome、SnowGround、WetGround）
    ///     已经在 `Scripts/Weather/Resources/` 下 → 天然不剥离，**不登记**。
    ///   · `Universal Render Pipeline/*` 三条是**降级兜底**（本项目是内置管线，
    ///     它们恒为 null）→ **不登记**，登记了反而是无意义的负担。
    ///   · 剩下的全部登记，见下方 <see cref="RequiredShaders"/>。
    /// </summary>
    public static class P31Packaging
    {
        /// <summary>打包输出目录（相对工程根）。.gitignore 已忽略，二进制不进仓库。</summary>
        private const string BuildDir = "Builds";

        /// <summary>
        /// 工程根目录。
        /// **不用 `Directory.GetCurrentDirectory()`** —— 它取决于"Unity 进程是从哪启动的"：
        /// 从 Hub 启动、从命令行启动、从快捷方式启动，cwd 都可能不同。
        /// 一旦 cwd 不是工程根，Builds 会建到别处、StreamingAssets 会比对失败，
        /// 而且**不会报错**，只是"包找不到了"。`Application.dataPath` 恒为 `<工程>/Assets`，
        /// 取它的父目录才是唯一可靠的工程根。
        /// </summary>
        private static string ProjectRoot
        {
            get
            {
                return System.IO.Path.GetDirectoryName(
                    Application.dataPath.TrimEnd('/', '\\'));
            }
        }

        /// <summary>产物名。沿用首包的 `School-pro.exe`，便于和 `Docs/打包/` 里的旧包对照。</summary>
        private const string ExeName = "School-pro.exe";

        /// <summary>
        /// 必须登记进 Always Included Shaders 的着色器。顺序即写入顺序。
        ///
        /// 每条都标注了"谁在用"，因为下一任维护者一定会问"这个能删吗"——
        /// 删掉任意一条的后果都是**不报错、只是 exe 里某块画面没了**。
        ///
        /// ══════════════════════════════════════════════════════════════
        /// 【2026-11-05 23:45 收窄：8 条 → 5 条，两个原因】
        ///
        /// 1. **删掉 `Hidden/Internal-Colored`（这次出包崩溃的元凶）**
        ///    登记后它被序列化成 `{fileID: 69, guid: ...e000..., type: 0}` ——
        ///    `e000` 系列是 **`Library/unity default resources`**，一个带
        ///    `HideFlags.DontSave` 的引擎内部资产，不是 `f000` 系列的
        ///    `unity_builtin_extra` 正规资源包。把它塞进构建后，Unity 写
        ///    `unity_builtin_extra` 时锁计数没归零，直接 Assertion failed 崩掉：
        ///    `Failed to write file .../Resources/unity_builtin_extra`
        ///    `Assertion failed on expression: 'm_LockCount == 0'`
        ///    连崩两次（23:30、23:37），耗时 21.3s / 4.8s，**都不是磁盘或杀软问题**
        ///（磁盘 154GB 空闲、写入实测 2.4GB/s、管家已退出）。
        ///    它是 `Hidden/` 前缀的内部着色器，引擎本来就自带，
        ///    项目**不需要也不应该**去登记它。
        ///
        /// 2. **删掉两个"兜底链"条目（`Legacy Shaders/Particles/Alpha Blended`、
        ///    `Sprites/Default`）**
        ///    它们是 `AcAirflowFeedback.cs` 里"取不到 Unlit 时的退路"。
        ///    本次收窄的原则是**只登日志实测真的缺的东西**：
        ///    旧包 `Player.log` / `Player-prev.log` 两份日志里，实际报缺的只有
        ///    4 个（见下方 ★），这两个从未报过缺 —— 登记它们属于为假想问题付出
        ///    真实风险（每多一条内置 shader 就多一次 builtin_extra 重写）。
        ///
        /// ★ 旧包两份 Player.log 的实测缺失清单（这是本清单的唯一依据）：
        ///    Player.log:125/129  `SchoolTwin/ScreenComposite`  机房视频
        ///    Player.log:116/120  `SchoolTwin/ScreenOnModel`    幕布视频
        ///    Player.log:117/121  `Legacy Shaders/Particles/Additive` 投影光柱
        ///    Player.log:45       `Skybox/Procedural`           昼夜天空
        /// </summary>
        private static readonly string[] RequiredShaders =
        {
            // ── 项目自有（本次 F22 的正面当事人）────────────────────────
            "SchoolTwin/ScreenComposite",   // PcScreenPlayer.cs:223（机房视频）、PcSlideShow.cs:113（PPT 幕布）★
            "SchoolTwin/ScreenOnModel",     // TwinDevicePresenter.cs:4434（投影幕布成像）★

            // ── 引擎内置（同样被 Shader.Find 取、同样会被剥）────────────
            "Legacy Shaders/Particles/Additive",   // TwinDevicePresenter.cs:4547 投影光柱 ★
            "Skybox/Procedural",                   // EnvironmentState.cs:577 昼夜天空 ★
            "Particles/Standard Unlit",            // AcAirflowFeedback.cs:701 空调气流
        };

        /// <summary>
        /// **禁止登记**的着色器。每次执行都会把它们从表里剔除（不是"不添加"，是主动删除）。
        ///
        /// 【为什么需要这个"黑名单"而不是"不在白名单里就别加"】
        /// `ApplyAlwaysIncluded` 是幂等**新增**，它不会撤销上一次已经写进去的东西。
        /// 而 23:30 那次崩溃已经把 `Hidden/Internal-Colored` 写进表里了；
        /// 现在编辑器还开着、GraphicsSettings 在内存里仍是 13 条的旧状态，
        /// 光靠"手改磁盘上的 .asset"未必能让运行中的 Unity 认账
        /// （ProjectSettings 不是 AssetDatabase 的普通资产，`AssetDatabase.Refresh()`
        /// 刷不动它）。重启编辑器能解决，但会丢掉当前的授权会话 —— 不值得。
        /// 所以让脚本自己去删：它读到的是内存里那份，删得掉就是真的删掉了。
        ///
        /// 【为什么 Hidden/Internal-Colored 不能登记】
        /// 它被序列化成 `guid: ...e000...` = `Library/unity default resources`，
        /// 一个 `HideFlags.DontSave` 的引擎内部资产。进构建后写
        /// `unity_builtin_extra` 时锁计数不归零 → `Assertion failed: 'm_LockCount == 0'`
        /// → 出包失败。连崩两次（23:30 / 23:37），删除缓存、退掉管家都无效。
        /// </summary>
        private static readonly string[] ForbiddenShaders =
        {
            "Hidden/Internal-Colored",
        };

        // ================================================================ ① 着色器登记

        /// <summary>
        /// 把 <see cref="RequiredShaders"/> 补进 Project Settings → Graphics → Always Included Shaders。
        ///
        /// **幂等**：已经在表里的不重复加。可以反复执行。
        /// 这点很重要 —— 判据脚本、CI、人手点菜单都可能重复触发，
        /// 不幂等的工具会在表里堆出 8 条重复项，反而把"到底登记了没有"搞模糊。
        /// </summary>
        [MenuItem("P31 打包/① 登记 Always Included Shaders", false, 10)]
        public static void ApplyAlwaysIncluded()
        {
            const string path = "ProjectSettings/GraphicsSettings.asset";
            UnityEngine.Object[] objs = AssetDatabase.LoadAllAssetsAtPath(path);
            if (objs == null || objs.Length == 0)
            {
                Debug.LogError("[P31] 读不到 " + path + "，着色器登记失败。");
                return;
            }

            SerializedObject so = new SerializedObject(objs[0]);
            SerializedProperty list = so.FindProperty("m_AlwaysIncludedShaders");
            if (list == null || !list.isArray)
            {
                Debug.LogError("[P31] GraphicsSettings 里找不到 m_AlwaysIncludedShaders，Unity 版本可能变了。");
                return;
            }

            // 先收集"已经登记过的"，用 Object 引用比对（Unity 会自己规范化成同一个资产）
            var existing = new HashSet<UnityEngine.Object>();
            for (int i = 0; i < list.arraySize; i++)
            {
                UnityEngine.Object o = list.GetArrayElementAtIndex(i).objectReferenceValue;
                if (o != null) existing.Add(o);
            }

            int added = 0;
            var missing = new StringBuilder();

            // ①-a 先剔除黑名单（见 ForbiddenShaders 的说明）。倒序遍历，删除不跳位。
            //
            // 比对用**名字**不用对象引用：`Shader.Find` 回来的实例与表里存的那条
            // 未必是同一个 C# 包装对象（引擎内部各自 new 出来的 wrapper），
            // 引用比对有可能漏判 —— 而"漏判"在这里等于"白删"。
            int removed = 0;
            var forbidNames = new HashSet<string>(ForbiddenShaders);

            for (int i = list.arraySize - 1; i >= 0; i--)
            {
                UnityEngine.Object o = list.GetArrayElementAtIndex(i).objectReferenceValue;
                Shader os = o as Shader;
                if (os == null || !forbidNames.Contains(os.name)) continue;

                // 对象引用数组的删除要两步：先置空、再删。只调一次 DeleteArrayElementAtIndex
                // 在引用型数组上只会"清空引用"留下一个空槽（Unity 的老行为），
                // 结果是表里多一条 {fileID: 0} 的垃圾项，比不删更糟。
                list.GetArrayElementAtIndex(i).objectReferenceValue = null;
                list.DeleteArrayElementAtIndex(i);
                removed++;
                Debug.Log("[P31] 已剔除禁止登记的着色器：" + os.name);
            }

            foreach (string name in RequiredShaders)
            {
                Shader sh = Shader.Find(name);
                if (sh == null)
                {
                    // 找不到就**明确报错**，不静默跳过 —— 静默跳过等价于"以为修了其实没修"，
                    // 正是 F22 的成因。缩进/改名/拼错都会走到这里。
                    Debug.LogError("[P31] 找不到着色器 " + name + "，未登记。请核对 Shader 文件里的 Shader \"...\" 声明。");
                    if (missing.Length > 0) missing.Append(" / ");
                    missing.Append(name);
                    continue;
                }

                if (existing.Contains(sh)) continue;

                int idx = list.arraySize;
                list.InsertArrayElementAtIndex(idx);
                list.GetArrayElementAtIndex(idx).objectReferenceValue = sh;
                existing.Add(sh);
                added++;
                Debug.Log("[P31] 已登记 Always Included Shader：" + name);
            }

            so.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssets();

            Debug.Log("[P31] 着色器登记完成：新增 " + added + " 条、剔除 " + removed
                      + " 条，当前共 " + list.arraySize + " 条。");
            if (missing.Length > 0)
                Debug.LogError("[P31] 以下着色器没登记成功：" + missing);
        }

        // ================================================================ ② 出包

        /// <summary>
        /// 出 Windows x86_64 包到 <c>Builds/School-pro.exe</c>。
        ///
        /// 可被命令行调用（批处理模式）：
        /// <code>
        /// Unity.exe -batchmode -quit -projectPath "E:/Unity Projects/School-pro" \
        ///           -executeMethod SchoolTwin.EditorTools.P31Packaging.BuildWindows \
        ///           -logFile Logs/p31_build.log
        /// </code>
        ///
        /// 【为什么不在这里面设 PlayerSettings】
        /// companyName / productName / 分辨率 / 全屏模式在首包时已验证正确
        /// （`Docs/答辩材料/打包执行清单.md` §0 有实测值），改它们属于
        /// "动了本来对的东西"。本脚本只做**这次要修的两件事**：着色器登记 + 出包。
        /// 唯一显式断言的是 Development Build = 关，因为它是"打包后掉帧"的第一嫌疑人，
        /// 而且它一旦被勾上，产物里会多出一堆让人误判的调试开销。
        /// </summary>
        [MenuItem("P31 打包/② 出包（Windows x64）", false, 11)]
        public static void BuildWindows()
        {
            bool batch = Application.isBatchMode;
            string root = ProjectRoot;

            try
            {
                // ① 先把着色器登记上 —— 顺序不能反。出包在前、登记在后的话，
                //    这次产物依然是旧的剥离状态，等于白打一次（首次打包就这么翻的车）。
                ApplyAlwaysIncluded();

                // ② 断言 Development Build 关闭。BuildOptions.None 本身就是"非开发版"，
                //    这里再断言一次 EditorUserBuildSettings.development，是因为它可能被
                //    上一次 GUI 操作留在 true 上（Build 窗口的勾选状态是持久化的）。
                if (EditorUserBuildSettings.development)
                {
                    EditorUserBuildSettings.development = false;
                    Debug.LogWarning("[P31] Development Build 原本是勾上的，已强制关闭（否则帧率与包体都不是发布值）。");
                }

                // ③ 场景清单：只允许启用项进包，并核对待打包清单就是那一项
                var scenes = new List<string>();
                foreach (EditorBuildSettingsScene s in EditorBuildSettings.scenes)
                {
                    if (s != null && s.enabled) scenes.Add(s.path);
                }

                if (scenes.Count == 0)
                {
                    Fail("Build Settings 里没有启用的场景，无法出包。", batch);
                    return;
                }

                var sb = new StringBuilder();
                for (int i = 0; i < scenes.Count; i++) sb.Append(i == 0 ? "" : ", ").Append(scenes[i]);
                Debug.Log("[P31] 待打包场景（" + scenes.Count + "）：" + sb);
                if (scenes.Count != 1 || !scenes[0].EndsWith("SchoolSceneDay.unity"))
                    Debug.LogWarning("[P31] 场景清单与预期不符（预期仅 SchoolSceneDay.unity），请确认没有误加 Night/Abandoned/Menu。");

                // ④ 输出目录
                string outDir = System.IO.Path.Combine(root, BuildDir);
                System.IO.Directory.CreateDirectory(outDir);
                string exePath = System.IO.Path.Combine(outDir, ExeName);

                var opts = new BuildPlayerOptions
                {
                    scenes = scenes.ToArray(),
                    locationPathName = exePath,
                    target = BuildTarget.StandaloneWindows64,
                    targetGroup = BuildTargetGroup.Standalone,
                    options = BuildOptions.None,      // 非开发版、不连 Profiler、不自动运行
                };

                Debug.Log("[P31] 开始出包 → " + exePath);
                DateTime t0 = DateTime.Now;
                BuildReport report = BuildPipeline.BuildPlayer(opts);
                double secs = (DateTime.Now - t0).TotalSeconds;

                BuildSummary sum = report.summary;
                Debug.Log("[P31] 出包结束：result=" + sum.result
                          + " 耗时=" + secs.ToString("0.0") + "s"
                          + " 大小=" + (sum.totalSize / 1024.0 / 1024.0).ToString("0.0") + "MB"
                          + " 错误=" + sum.totalErrors + " 警告=" + sum.totalWarnings);

                if (sum.result != BuildResult.Succeeded)
                {
                    Fail("出包失败：result=" + sum.result + "，详见 Player.log / build log。", batch);
                    return;
                }

                // ⑤ 产物自检：StreamingAssets 必须完整复制。这一条是 F22 同期发现的：
                //    首包"视频没了"的误判，正是因为没人核对过这个目录到底进没进包。
                CheckStreamingAssets(exePath);

                Debug.Log("[P31] 出包成功：" + exePath);
                if (batch) EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogError("[P31] 出包异常：" + e);
                if (batch) EditorApplication.Exit(1);
            }
        }

        /// <summary>
        /// 核对 exe 旁的 StreamingAssets/PC 是否与源目录一致。
        /// 只比对**文件名集合**，不比对字节 —— 目的是发现"整个目录没拷进去"，
        /// 不是做完整性校验（那件事该由 hash 工具做，不该塞进出包脚本里）。
        /// </summary>
        private static void CheckStreamingAssets(string exePath)
        {
            string dataDir = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(exePath),
                System.IO.Path.GetFileNameWithoutExtension(exePath) + "_Data",
                "StreamingAssets", "PC");

            string srcDir = System.IO.Path.Combine(ProjectRoot, "Assets", "StreamingAssets", "PC");

            if (!System.IO.Directory.Exists(dataDir))
            {
                Debug.LogError("[P31] 产物里没有 StreamingAssets/PC 目录：" + dataDir
                               + " → 机房视频、PPT、桌面图全部会失效。");
                return;
            }

            string[] src = System.IO.Directory.GetFiles(srcDir, "*", System.IO.SearchOption.AllDirectories);
            int missing = 0;
            int srcFiles = 0;
            foreach (string f in src)
            {
                // .meta 不进包，源目录里却有 —— 必须剔除，否则每次都会"报缺 17 个"，
                // 假的红色告警比没有告警更坏（多看两次就没人看了）。
                if (f.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                srcFiles++;

                string rel = f.Substring(srcDir.Length).TrimStart('\\', '/');
                string target = System.IO.Path.Combine(dataDir, rel);
                if (!System.IO.File.Exists(target))
                {
                    Debug.LogError("[P31] StreamingAssets 缺失：" + rel);
                    missing++;
                }
            }

            Debug.Log("[P31] StreamingAssets/PC 核对：源 " + srcFiles + " 个文件（不含 .meta），缺失 " + missing + " 个。");
            if (missing == 0)
                Debug.Log("[P31] StreamingAssets 完整复制（预期 4 mp4 + 12 slide + desktop.jpg = 17 个）。");
        }

        private static void Fail(string msg, bool batch)
        {
            Debug.LogError("[P31] " + msg);
            if (batch) EditorApplication.Exit(1);
        }
    }
}

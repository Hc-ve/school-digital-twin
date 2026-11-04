using System;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;
using UnityEngine.SceneManagement;

namespace SchoolTwin.EditorTools
{
    /// <summary>
    /// 【P13】修 P30 的后处理死路：把「后处理层 + 全局卷」真正挂到场景里。
    ///
    /// ══════════════════════════════════════════════════════════════════
    /// 【P30 的死路到底是什么（先弄清再修）】
    ///
    /// 资产 `Assets/_Project/ScriptableObjects/PPS_Bloom.asset` 一直在，包里也装了
    /// `com.unity.postprocessing@3.2.2`，可是**场景里一个 `PostProcessLayer` 都没有**。
    ///
    /// PPv2 的生效链条是四段，缺任何一段都静默无效（日志全绿、画面没变化）：
    ///   ① 相机上要有 `PostProcessLayer`       ← 场景里**没有**
    ///   ② 层上的 `m_Resources` 要指向 PostProcessResources  ← 没层，谈不上
    ///   ③ 场景里要有 `PostProcessVolume`，`isGlobal=true`    ← 场景里**没有**
    ///   ④ 卷的 `sharedProfile` 要指向 PPS 资产   ← 没卷，谈不上
    ///
    /// 所以 P30 那轮「怎么调都看不到变化」不是参数问题，是**链路没接通**。
    /// 项目里其实已经有 `TwinP25Bloom`（P25 写的）把这四段全实现了，
    /// 但它是个**菜单工具，从来没被执行过** —— 工具写好了没跑，效果等于没写。
    /// 本工具跟它是同一套逻辑，差异只在「P13 要求只开 ColorGrading 的 postExposure」。
    ///
    /// ══════════════════════════════════════════════════════════════════
    /// 【为什么 P13 只开 postExposure，把 Bloom / Vignette 关掉】
    ///
    /// 时间天气系统要在晚上「把画面压暗一点」，这是**一个亮度偏移**，
    /// 最干净的实现就是 ColorGrading 的 postExposure（单位 EV，纯乘法）。
    ///
    /// 而 Bloom 是"把亮部晕开"、Vignette 是"四角压暗"——两者都不是亮度偏移：
    ///   · Bloom 在雪天会失控：整片雪地 albedo≈1，开了 Bloom 雪面会糊成一团白雾，
    ///     恰恰毁掉"地面积雪"这个 P13 要演示的效果
    ///   · Vignette 会在夜间把本就暗的四角压到全黑，看场景时像镜头脏了
    /// 所以本轮明确：Bloom/Vignette 的 `enabled.overrideState` 保持 1（明确关），
    /// 只留 ColorGrading.postExposure 可被天气系统驱动。将来要开 Bloom，
    /// 那是独立一轮视觉评审的事，不该混在时间天气这轮里。
    ///
    /// ══════════════════════════════════════════════════════════════════
    /// 【为什么写成 Editor 工具而不是直接改 .unity 文本】
    ///
    /// `PostProcessLayer.m_Resources` 是**私有 + [SerializeField]**，只能靠 `Init(res)` 赋值；
    /// `PostProcessVolume.sharedProfile` 与 `isGlobal` 虽然公开，但两者在 YAML 里是
    /// 带 fileID 的引用 + 一整套 `m_` 前缀字段。手写 YAML 要凭空造出正确的
    /// fileID 与序列化结构，错一个字段就变成"组件在、但全是默认值"，
    /// **表现和没挂一模一样**，而且不会有任何报错。
    ///
    /// 更关键的是本工程有一条纪律：场景装配一律走 Editor 脚本 + Undo + SaveScene，
    /// 保证可重入、可回退、能在 Console 里留下可核对的日志。
    /// </summary>
    public static class TwinP13PostProcess
    {
        private const string ProfilePath = "Assets/_Project/ScriptableObjects/PPS_Bloom.asset";
        private const string VolumeName = "[PPS] PostProcess";

        [MenuItem("P13 时间天气/① 修后处理死路（挂层 + 全局卷）", false, 10)]
        public static void SetupMenu()
        {
            if (EditorApplication.isPlaying)
            {
                EditorUtility.DisplayDialog("已拒绝",
                    "现在是 Play 模式。请先按停止 —— 加组件和存场景都会出问题。", "好");
                return;
            }
            string report = Setup();
            Debug.Log(report);
            EditorUtility.DisplayDialog("P13 后处理", report, "好");
        }

        private static string Setup()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[P13] ===== 修后处理死路 =====");

            // ---- ① PostProcessResources ----
            PostProcessResources res = FindResources();
            if (res == null)
            {
                sb.AppendLine("[P13] ✘ 找不到 PostProcessResources。");
                sb.AppendLine("       说明 com.unity.postprocessing 没装好 / 没导入完。");
                sb.AppendLine("       等 Console 无红字后重跑本菜单。");
                return sb.ToString();
            }
            sb.AppendLine("[P13] PostProcessResources：" + AssetDatabase.GetAssetPath(res));

            // ---- ② 主相机 ----
            Camera cam = FindMainCamera();
            if (cam == null)
            {
                sb.AppendLine("[P13] ✘ 场景里没有 MainCamera，无法配置。");
                return sb.ToString();
            }
            sb.AppendLine("[P13] 相机：" + cam.name);

            // HDR 必须开：postExposure 是乘在 HDR 值上的，
            // LDR 下 >1 的部分在进后处理前就被裁掉了，压暗没问题但提亮会溢出成白。
            if (!cam.allowHDR)
            {
                cam.allowHDR = true;
                EditorUtility.SetDirty(cam);
                sb.AppendLine("[P13] ✔ 相机已开 HDR");
            }
            else
            {
                sb.AppendLine("[P13] · 相机 HDR 已开");
            }

            // ---- ③ PostProcessLayer ----
            PostProcessLayer layer = cam.GetComponent<PostProcessLayer>();
            if (layer == null)
            {
                layer = Undo.AddComponent<PostProcessLayer>(cam.gameObject);
                sb.AppendLine("[P13] ✔ 相机已加 PostProcessLayer");
            }
            else
            {
                sb.AppendLine("[P13] · 相机已有 PostProcessLayer（复用）");
            }

            // volumeLayer 必须包含卷所在的层。项目没有专门的 PostProcess 层，
            // 卷会建在 Default 层，所以这里用全开（~0）。用具体层反而会因为
            // "卷在 Default、mask 里没 Default"而静默不生效 —— 这是 PPv2 最常见的坑。
            layer.volumeLayer = ~0;
            layer.volumeTrigger = cam.transform;
            layer.antialiasingMode = PostProcessLayer.Antialiasing.None;   // 不动原有抗锯齿策略
            layer.Init(res);
            EditorUtility.SetDirty(layer);

            // ---- ④ 配置资产：只留 ColorGrading.postExposure ----
            PostProcessProfile profile = AssetDatabase.LoadAssetAtPath<PostProcessProfile>(ProfilePath);
            if (profile == null)
            {
                sb.AppendLine("[P13] ✘ 找不到配置资产 " + ProfilePath);
                return sb.ToString();
            }
            sb.AppendLine("[P13] 配置：" + ProfilePath);

            ColorGrading cg = profile.GetSetting<ColorGrading>();
            if (cg == null)
            {
                cg = profile.AddSettings<ColorGrading>();
                sb.AppendLine("[P13] ✔ 配置里补了 ColorGrading");
            }
            cg.enabled.Override(true);
            cg.postExposure.Override(0f);      // 初值 0，运行时由天气系统按时刻驱动
            sb.AppendLine("[P13] ✔ ColorGrading.postExposure 已开启覆盖（初值 0，运行时驱动）");

            Bloom bloom = profile.GetSetting<Bloom>();
            if (bloom != null)
            {
                bloom.enabled.Override(false);
                sb.AppendLine("[P13] ✔ Bloom 已关（雪天会糊成白雾，不是亮度偏移）");
            }
            Vignette vg = profile.GetSetting<Vignette>();
            if (vg != null)
            {
                vg.enabled.Override(false);
                sb.AppendLine("[P13] ✔ Vignette 已关（夜间会把四角压全黑）");
            }

            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();

            // ---- ⑤ 全局卷 ----
            PostProcessVolume volume = null;
            PostProcessVolume[] vols = UnityEngine.Object.FindObjectsOfType<PostProcessVolume>(true);
            for (int i = 0; i < vols.Length; i++)
            {
                if (vols[i] != null && vols[i].name == VolumeName) { volume = vols[i]; break; }
            }
            if (volume == null)
            {
                GameObject go = new GameObject(VolumeName);
                Undo.RegisterCreatedObjectUndo(go, "Create PostProcess Volume");
                volume = go.AddComponent<PostProcessVolume>();
                sb.AppendLine("[P13] ✔ 场景里新建全局卷「" + VolumeName + "」");
            }
            else
            {
                sb.AppendLine("[P13] · 场景里已有卷「" + VolumeName + "」（复用）");
            }

            volume.isGlobal = true;
            volume.priority = 10f;
            volume.weight = 1f;

            // ⚠️ 必须写 sharedProfile，不能写 profile。
            //    `PostProcessVolume.profile` 的 getter 会 Instantiate 一份运行时副本，
            //    写它不会序列化进场景 —— 场景里留下 `sharedProfile: {fileID: 0}`,
            //    后处理静默失效，而每一步日志都会是"✔"。
            volume.sharedProfile = profile;
            EditorUtility.SetDirty(volume);

            if (volume.sharedProfile == null)
                sb.AppendLine("[P13] ✘ Volume.sharedProfile 没设上 —— 后处理不会生效");
            else
                sb.AppendLine("[P13] ✔ Volume.sharedProfile = " +
                              AssetDatabase.GetAssetPath(volume.sharedProfile));

            // ---- ⑥ 存场景 ----
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            bool saved = EditorSceneManager.SaveScene(SceneManager.GetActiveScene());
            sb.AppendLine();
            sb.AppendLine("[P13] 场景保存：" + (saved ? "成功" : "失败"));
            sb.AppendLine("[P13] 生效判据（用户 play 时可核对）：");
            sb.AppendLine("        · 相机 Inspector 上有 Post-process Layer，Resources 非空");
            sb.AppendLine("        · 场景里有 [PPS] PostProcess 物体，Is Global 勾上，Profile = PPS_Bloom");
            sb.AppendLine("        · PPS_Bloom 里只有 Color Grading 是勾上的（Bloom/Vignette 不勾）");
            sb.AppendLine("[P13] ===== 结束 =====");

            try { System.IO.File.WriteAllText("p13_postprocess.txt", sb.ToString(), Encoding.UTF8); }
            catch (Exception) { }
            return sb.ToString();
        }

        /// <summary>找主相机。**不用 Camera.main** —— 它只按 tag 查，不检查 enabled。</summary>
        private static Camera FindMainCamera()
        {
            Camera[] all = UnityEngine.Object.FindObjectsOfType<Camera>(true);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null && all[i].CompareTag("MainCamera")) return all[i];
            }
            // 兜底：拿唯一启用的那台
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null && all[i].isActiveAndEnabled) return all[i];
            }
            return null;
        }

        /// <summary>在项目里找 PostProcessResources（不同包版本路径可能不同，用 FindAssets）。</summary>
        private static PostProcessResources FindResources()
        {
            string[] guids = AssetDatabase.FindAssets("t:PostProcessResources");
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                PostProcessResources r = AssetDatabase.LoadAssetAtPath<PostProcessResources>(path);
                if (r != null) return r;
            }
            return null;
        }
    }
}
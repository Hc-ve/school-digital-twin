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
    /// 【P25】给场景加 Bloom（光晕），让灯管 / 屏幕 / 投影真正"发光"。
    ///
    /// ── 为什么必须上后处理 ────────────────────────────────────────────
    /// 本项目是 **Built-in Render Pipeline**，没有 Bloom。
    /// `_lampEmission` 只会把灯管**表面**提亮，永远不会往外晕开 ——
    /// 真实相机里灯管的观感是"中心过曝 + 边缘柔和溢出"，那个溢出就是 Bloom。
    /// 所以无论 emission 怎么调，都只能得到"一块纯色面板"。
    ///
    /// ── 为什么 emission 0.6 也能触发 Bloom ───────────────────────────
    /// Standard shader 的最终色 = `albedo × 光照 + emission`。
    /// 灯管 albedo ≈ 1.0，烘焙光 ≈ 0.7 → 0.7 + 0.6 = **1.3 > 1.0**。
    /// 只要相机开 HDR，这个 1.3 会保留到后处理栈，Bloom 阈值取 0.9 就能抓到。
    /// 也就是说：**不用把 emission 调回 2.2，加了 Bloom 自然就有光晕。**
    ///
    /// ── 做了什么 ────────────────────────────────────────────────────
    ///   1. 主相机：开 `allowHDR`，加 `PostProcessLayer`（volumeLayer = 全部）
    ///   2. 新建 `Assets/_Project/ScriptableObjects/PPS_Bloom.asset`（Bloom 配置）
    ///   3. 场景里建一个全局 `PostProcessVolume` 引用它
    ///
    /// ── 用法 ────────────────────────────────────────────────────────
    /// 菜单 `P25 光晕 / ① 一键配置 Bloom`（幂等，重复跑不会叠加组件）
    /// ⚠️ 跑之前 `Packages/manifest.json` 里必须有 `com.unity.postprocessing`，
    ///    且 Unity 已经完成包导入（Console 没有编译错误）。
    /// </summary>
    public static class TwinP25Bloom
    {
        private const string ProfilePath = "Assets/_Project/ScriptableObjects/PPS_Bloom.asset";
        private const string VolumeName = "[PPS] Bloom";

        [MenuItem("P25 光晕/① 一键配置 Bloom", false, 10)]
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
            EditorUtility.DisplayDialog("P25 Bloom", report, "好");
        }

        private static string Setup()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[P25] ===== 配置 Bloom =====");

            // ---- ① 资源 ----
            PostProcessResources res = FindResources();
            if (res == null)
            {
                sb.AppendLine("[P25] ✘ 找不到 PostProcessResources。");
                sb.AppendLine("       说明 com.unity.postprocessing 还没装好 / 还没导入完。");
                sb.AppendLine("       请等 Console 里包导入结束（无红字）后重跑本菜单。");
                return sb.ToString();
            }
            sb.AppendLine("[P25] PostProcessResources：" + AssetDatabase.GetAssetPath(res));

            // ---- ② 主相机 ----
            Camera cam = Camera.main;
            if (cam == null)
            {
                Camera[] all = UnityEngine.Object.FindObjectsOfType<Camera>(true);
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i] != null && all[i].CompareTag("MainCamera")) { cam = all[i]; break; }
                }
            }
            if (cam == null)
            {
                sb.AppendLine("[P25] ✘ 场景里没有 MainCamera，无法配置。");
                return sb.ToString();
            }

            if (!cam.allowHDR)
            {
                cam.allowHDR = true;
                EditorUtility.SetDirty(cam);
                sb.AppendLine("[P25] ✔ 相机已开 HDR（不开的话 >1 的亮度会被裁掉，Bloom 抓不到）");
            }
            else
            {
                sb.AppendLine("[P25] · 相机 HDR 已开");
            }

            PostProcessLayer layer = cam.GetComponent<PostProcessLayer>();
            if (layer == null)
            {
                layer = Undo.AddComponent<PostProcessLayer>(cam.gameObject);
                sb.AppendLine("[P25] ✔ 相机已加 PostProcessLayer");
            }
            else
            {
                sb.AppendLine("[P25] · 相机已有 PostProcessLayer");
            }

            // ⚠️ PPS v2 的 `m_Resources` 是**私有且 [SerializeField]** 的，没有公开的 `resources` 字段，
            //    只能通过 `Init(resources)` 赋值 —— 赋值后因为带 [SerializeField]，会被存进场景。
            //    （PPS v1 才有公开的 `layer.resources`，写错会报 CS1061。）
            layer.volumeLayer = ~0;
            layer.volumeTrigger = cam.transform;
            layer.antialiasingMode = PostProcessLayer.Antialiasing.None;   // 不改原有抗锯齿策略
            layer.Init(res);
            EditorUtility.SetDirty(layer);

            // ---- ③ 配置文件 ----
            PostProcessProfile profile = AssetDatabase.LoadAssetAtPath<PostProcessProfile>(ProfilePath);
            if (profile == null)
            {
                profile = ScriptableObject.CreateInstance<PostProcessProfile>();
                AssetDatabase.CreateAsset(profile, ProfilePath);
                sb.AppendLine("[P25] ✔ 新建配置 " + ProfilePath);
            }
            else
            {
                sb.AppendLine("[P25] · 复用配置 " + ProfilePath);
            }

            Bloom bloom = profile.GetSetting<Bloom>();
            if (bloom == null)
            {
                bloom = profile.AddSettings<Bloom>();
                sb.AppendLine("[P25] ✔ 配置里加了 Bloom");
            }
            bloom.enabled.Override(true);
            bloom.intensity.Override(0.85f);   // 光晕强度
            bloom.threshold.Override(0.90f);   // 只抓亮度 >0.9 的像素（灯管 1.3、天花板 0.7 不受影响）
            bloom.softKnee.Override(0.60f);    // 软膝：避免光晕边缘出现硬边
            // PPS v2 里「扩散范围」叫 `diffusion`（Range 1~10，默认 7），不是 v1 的 `scatter`
            bloom.diffusion.Override(7f);
            // 「高质量过滤」在 v2 里是 `fastMode` 的反面
            bloom.fastMode.Override(false);
            bloom.clamp.Override(32f);
            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();

            // ---- ④ 全局 Volume ----
            PostProcessVolume volume = null;
            PostProcessVolume[] vols = UnityEngine.Object.FindObjectsOfType<PostProcessVolume>(true);
            for (int i = 0; i < vols.Length; i++)
            {
                if (vols[i] != null && vols[i].name == VolumeName) { volume = vols[i]; break; }
            }
            if (volume == null)
            {
                GameObject go = new GameObject(VolumeName);
                Undo.RegisterCreatedObjectUndo(go, "Create Bloom Volume");
                volume = go.AddComponent<PostProcessVolume>();
                sb.AppendLine("[P25] ✔ 场景里新建全局 Volume「" + VolumeName + "」");
            }
            else
            {
                sb.AppendLine("[P25] · 场景里已有 Volume「" + VolumeName + "」");
            }
            volume.isGlobal = true;
            volume.priority = 10f;
            volume.weight = 1f;
            // ⚠️ 必须写 `sharedProfile`，不能写 `profile`。
            //    PPS v2 的 `PostProcessVolume.profile` 是**运行时克隆**的只读入口
            //    （getter 会 Instantiate 一份），写它不会序列化进场景 ——
            //    场景里会留下 `sharedProfile: {fileID: 0}`，Bloom 静默失效、日志却全部成功。
            volume.sharedProfile = profile;
            EditorUtility.SetDirty(volume);

            // 自检：`sharedProfile` 是唯一会存进场景的字段。
            // 它为空 = Bloom 完全没生效，但前面每一步日志都会是"✔"，极难发现。
            if (volume.sharedProfile == null)
                sb.AppendLine("[P25] ✘ Volume.sharedProfile 没设上 —— Bloom 不会生效");
            else
                sb.AppendLine("[P25] ✔ Volume.sharedProfile = " +
                              AssetDatabase.GetAssetPath(volume.sharedProfile));

            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            bool saved = EditorSceneManager.SaveScene(SceneManager.GetActiveScene());

            sb.AppendLine();
            sb.AppendLine("[P25] 场景保存：" + (saved ? "成功" : "失败"));
            sb.AppendLine("[P25] 参数：强度 0.85 / 阈值 0.90 / 软膝 0.60 / 扩散 7（高质量过滤开）");
            sb.AppendLine("[P25] 灯管当前 emission=0.60 + 烘焙光 0.7 → 总亮度 1.3，会被 Bloom 抓到。");
            sb.AppendLine("       觉得光晕太强 → 调低 PPS_Bloom 的 Intensity；太弱 → 调高。");
            sb.AppendLine("[P25] ===== 结束 =====");

            try { System.IO.File.WriteAllText("p25_bloom.txt", sb.ToString(), Encoding.UTF8); }
            catch (Exception) { }
            return sb.ToString();
        }

        /// <summary>在项目里找 PostProcessResources（不同包版本路径可能不同，所以用 FindAssets）。</summary>
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

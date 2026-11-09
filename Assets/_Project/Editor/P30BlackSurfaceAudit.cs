#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace SchoolTwin.EditorTools
{
    /// <summary>
    /// 【P30 / D13】场景发黑构件审计 v3 —— **双指标 · 只读 · 不进 Play**。
    ///
    /// ── 为什么整份重写（裁决 C-67 / C-73 / C-75）────────────────────
    /// v2 的 108 条 / 770 条清单已被证伪并作废，根因是三个叠加缺陷：
    ///   · 缺陷 A：`Vector2 uv = uv2[i]` 未应用 <see cref="Renderer.lightmapScaleOffset"/> 的 ST 变换，
    ///             在 scale=1/offset=0 的对象上碰巧正确，其余系统性偏移 →「对一部分、错一部分」。
    ///   · 缺陷 B：图集被降采样到固定 256×256，而实际图集是 1024×1024，且为点采样，
    ///             会漏掉 1/16 的小黑块。
    ///   · 缺陷 C：GuessCategory 无暖气片分支，D13 派工时暖气片全落进「其他」。
    /// 修补无法验证「修没修对」，故重写，并新增一条与 UV 推断完全解耦的**渲染实测**通道。
    ///
    /// ── 两条通道（缺一不可）────────────────────────────────────────
    /// 通道 A · UV 采样：全量扫描，按 UV2 + ST 变换读图集，输出 uvAvg / uvBlackRatio。
    /// 通道 B · 渲染实测：对候选集逐对象 Hide Others 离屏渲染，输出该 Renderer 在屏幕上的
    ///           实际平均亮度。这一路绕开所有 UV / 烘焙 / 采样推断，直接回答「屏幕上黑不黑」。
    ///
    /// ── 四象限判读（任一判黑即标记，但必须区分）──────────────────
    ///   采样黑 + 渲染黑  → 真黑            （两指标一致，可作派修依据）
    ///   采样黑 + 渲染亮  → 指标失效        （ZalTop 型：UV 通道错，禁止派修）
    ///   采样亮 + 渲染黑  → 仅渲染黑        （UV 未捕获，需打光复核）
    ///   采样亮 + 渲染亮  → 正常
    ///
    /// ── 只读纪律（本工具最硬的一条）──────────────────────────────
    /// 通道 B 需要临时切换 Renderer.enabled。**所有切换都在 try/finally 中成对还原**，
    /// 临时对象一律 HideAndDontSave 并销毁；不写场景、不写材质、不写光照设置、
    /// 不改 lightmapIndex、不标脏、不进 Play。工具只往 Docs/QA/evidence 写一份文本报告。
    ///
    /// ── 已知口径边界（诚实标注，避免把近似当真值）────────────────
    /// ① Edit Mode 下读的是 mesh.uv2 原始通道；静态合批的 UV 重映射只在 Player 态发生，不在此通道内。
    /// ② 隔离渲染隐藏了其他 Renderer，因此「遮挡导致的暗」不会被计入；它判定的是该面自身是否发黑。
    /// ③ 亮度为线性空间值（项目是 Linear 色彩空间，见 C5），不是 sRGB 屏幕取色。
    /// ④ 渲染实测只覆盖候选集；未测对象的渲染值记为 N/A，明细里单列，不冒充已复核。
    /// ⑤ Edit Mode 下切换 Renderer.enabled 可能把场景标记为 dirty。工具**不保存**场景，
    ///    值也会在 finally 中还原；但主理人跑完后请勿手按 Ctrl+S，重开场景即可复位。
    ///    （不采用 Unity 的 SceneVisibility 是因为它只影响 SceneView，不影响 Camera.Render。）
    ///
    /// 用完即可删，不进运行时。
    /// </summary>
    public static class P30BlackSurfaceAudit
    {
        // ================================================================ 参数
        // 说明：UvBlackThreshold / UvDimThreshold 沿用 v2 口径，便于与历史数据对照；
        //       它们与采样分辨率无关，不因缺陷 B 的修正而改变。
        private const float UvBlackThreshold = 0.10f;
        private const float UvDimThreshold = 0.25f;
        private const float UvBlackRatioThreshold = 0.50f;

        /// <summary>渲染实测「黑」阈值（线性亮度 0~1）。</summary>
        private const float RenderBlackThreshold = 0.10f;

        /// <summary>
        /// 「局部发黑」判据：最暗帧里低于 RenderBlackThreshold 的像素占比达到此值即标记。
        ///
        /// 为什么必须有这一条：D13 是「翅片间 / 竖梃一小块黑 + 一大片正常」，
        /// 整帧平均值会被正常区域稀释，永远达不到 RenderBlackThreshold。
        /// 取 0.15（15%）是保守起点，可调区间 0.10~0.30；
        /// 具体取值待打光按 play 截图裁决，工具只输出占比，不掩饰也不放大。
        /// </summary>
        private const float LocalDarkRatioThreshold = 0.15f;

        /// <summary>
        /// 渲染实测「偏亮」**参考线**（线性亮度 0~1），只用于报告里标注，不作硬断言。
        ///
        /// ⚠️ 口径修正（2026-11-09，主理人 play 实测后）：
        /// 上一版把 0.25 当 ZalTop 自测的硬门槛，是我在没有实测数据时拍的绝对值。
        /// 现场实测 ZalTop 线性亮度 0.1938（≈ sRGB 0.47，肉眼明显不是黑），
        /// 却因 &lt; 0.25 被判 FAIL —— 这条线与 RenderBlackThreshold=0.10 自相矛盾，
        /// 且「全场最亮的矿棉板」是**相对**其他黑构件而言，不是绝对亮度 0.8。
        /// 因此：硬断言改为「两通道都不判黑 + 渲染值不低于 UV 值」（= 证伪 A 的真正含义），
        /// 0.25 降为报告参考线，具体取值由打光按渲染实测裁决，断点不擅自定死。
        /// </summary>
        private const float RenderBrightReference = 0.25f;

        /// <summary>单个网格最多采多少个 UV2 顶点（v2 是 512，提到 4096 以覆盖小黑块）。</summary>
        private const int MaxUvSamplesPerMesh = 4096;

        /// <summary>
        /// 离屏渲染 RT 边长。⚠️ 这是**屏幕采样**分辨率，与图集采样分辨率无关：
        /// 图集一律按 src.width / src.height 全分辨率读取，不在此处设限。
        /// 64×64 对「求整个对象平均亮度」足够，且让逐对象 ReadPixels 足够快。
        /// 可调区间 32~256；调大不改变判读规则，只增加耗时。
        /// </summary>
        private const int RenderProbeSize = 64;

        /// <summary>
        /// 单帧有效像素低于此数就不采信（可能只拍到几个边缘像素，亮度是噪声）。
        /// 64×64=4096 的 1/8 = 512。可调区间 256~1024。
        /// </summary>
        private const int MinValidPixels = 512;

        /// <summary>
        /// 渲染实测候选上限。候选按 UV 黑度降序取，强制基准对象优先。
        /// 可调区间 100~800；超出部分标 N/A 并在报告里写明截断数量，绝不静默丢弃。
        /// </summary>
        private const int MaxRenderProbeTargets = 400;

        private const string EvidenceDir = "Docs/QA/evidence";

        /// <summary>
        /// 文件头声明。任务包要求逐字输出，防止新旧清单混用。
        /// 注意：108/770 只作为「被替代的旧清单编号」出现在声明里，**不作为任何判据或数据引用**。
        /// </summary>
        private const string LegacyNotice = "本清单替代 10-30 版 108/770 条清单，旧清单作废";

        /// <summary>洋红哨兵背景：用于区分「这里没有几何」与「几何渲染出来是黑的」（沿用打光隔离渲染法）。</summary>
        private static readonly Color SentinelColor = new Color(1f, 0f, 1f, 1f);

        /// <summary>
        /// 6 轴视角。发黑构件朝向不定（吊顶下表面朝下、墙面朝侧向），
        /// 单视角会因背面剔除看不到目标面；逐个方向渲染，取有效像素最多的一帧。
        /// </summary>
        private static readonly Vector3[] ProbeDirections =
        {
            Vector3.up, Vector3.down,
            Vector3.left, Vector3.right,
            Vector3.forward, Vector3.back,
        };

        // ================================================================ 数据结构

        /// <summary>图集亮度图：只存单通道 0~255，48 张 1024² 约 48MB，比缓存 Color[] 省 16 倍。</summary>
        private sealed class AtlasLum
        {
            public byte[] Pixels;
            public int Width;
            public int Height;
            public int OutOfRange;
        }

        private sealed class RenderResult
        {
            public bool Valid;
            public float Avg;
            public int Pixels;
            public string View = "N/A";

            /// <summary>6 轴逐视角诊断串，供打光判读「哪一面亮、哪一面暗」。</summary>
            public string Views = "N/A";

            /// <summary>被洋红哨兵剔除的背景像素数。用于确认 clear color 到底有没有生效。</summary>
            public int BackgroundPixels;

            /// <summary>
            /// 6 轴中**最暗**一帧的亮度与视角。D13 的病灶是「局部某个面黑、其余正常」，
            /// 只看最亮帧会把翅片间/竖梃的黑抹平，故四象限判黑必须看这个值。
            /// </summary>
            public float MinAvg = float.MaxValue;
            public string MinView = "N/A";

            /// <summary>
            /// 最暗帧里「亮度 &lt; RenderBlackThreshold」的像素占比。
            ///
            /// ⚠️ 这是抓 D13 局部发黑的**关键指标**：整帧平均值会把「一小块黑 + 一大片正常」
            /// 稀释成中等亮度（与 UV 通道 v1 漏掉 Floor2 同因，见 §1.6 D23）。
            /// 翅片间 / 窗框竖梃要靠占比才看得见。
            /// </summary>
            public float MinDarkRatio;
        }

        private enum Verdict
        {
            Normal,             // 采样亮 + 渲染亮
            TrueBlack,          // 采样黑 + 渲染黑
            UvMetricInvalid,    // 采样黑 + 渲染亮（ZalTop 型）
            RenderOnlyBlack,    // 采样亮 + 渲染黑
            UvOnly,             // 采样黑 + 渲染通道未覆盖
        }

        private sealed class Item
        {
            public Renderer Renderer;
            public string Path;
            public string Material;
            public string Category;
            public int AtlasIndex;
            public float UvAvg;
            public float UvBlackRatio;
            public int UvSamples;
            public RenderResult Render = new RenderResult();
            public bool IsKnownFixed;
            public Verdict Verdict;
        }

        // ================================================================ 入口

        [MenuItem("归档/P30 发黑检测（只读，不改场景）")]
        public static void Run()
        {
            StringBuilder sb = new StringBuilder(65536);
            sb.AppendLine(LegacyNotice);
            sb.AppendLine("===== P30 发黑审计 v3 · 双指标（只读 / Edit Mode / 不进 Play）=====");
            sb.AppendLine("时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("通道 A：UV2 + lightmapScaleOffset ST 变换，图集按实际尺寸全分辨率 + 双线性采样");
            sb.AppendLine("通道 B：Hide Others 离屏渲染，6 轴全记录，洋红哨兵剔除背景");
            sb.AppendLine("  · 最亮帧 = 判「该对象是否被判死」，用于 ZalTop 基准断言");
            sb.AppendLine("  · 最暗帧 + 暗像素占比 = 判「局部发黑」（翅片间/竖梃），用于四象限分类");
            sb.AppendLine("    （整帧平均值会稀释局部黑，与 UV 通道 v1 漏掉 Floor2 同因，见 §1.6 D23）");
            sb.AppendLine("判读：任一通道判黑即标记；「采样黑+渲染亮」= 指标失效，禁止作为派修依据");
            sb.AppendLine("只读声明：临时切换的 Renderer.enabled 全部在 finally 中还原，不写场景 / 材质 / 光照");
            sb.AppendLine("⚠ 运行后请勿手按 Ctrl+S：Edit Mode 下切换 enabled 可能标记场景 dirty，工具不保存场景，重开即复位");
            sb.AppendLine();

            // ---- 已修复清单：单一真相源，反射读运行时脚本的私有静态字段 ----
            List<string> knownFixed = LoadKnownFixedPaths(sb);
            sb.AppendLine("已修复清单（运行时 lightmapIndex=-1）：" + knownFixed.Count + " 条，来源 P30FixCeilingLightmap.DefaultPaths");
            sb.AppendLine();

            // ---- 图集：按实际尺寸读取（修缺陷 B）----
            Dictionary<int, AtlasLum> atlases = BuildAtlasCache(sb);

            // ---- 通道 A：全量 UV 采样（修缺陷 A）----
            // A4：静态类里必须写 UnityEngine.Object.FindObjectsOfType，不能直接调实例方法。
            Renderer[] all = UnityEngine.Object.FindObjectsOfType<Renderer>(true);
            List<Item> items = new List<Item>(all.Length);
            sb.AppendLine("场景 Renderer 总数：" + all.Length);

            for (int i = 0; i < all.Length; i++)
            {
                Renderer r = all[i];
                if (r == null) continue;
                if (r.lightmapIndex < 0 || !atlases.ContainsKey(r.lightmapIndex)) continue;

                float uvAvg, uvRatio;
                int uvSamples;
                if (!SampleUvLuminance(r, atlases[r.lightmapIndex], out uvAvg, out uvRatio, out uvSamples)) continue;

                string path = BuildPath(r.transform);
                Item it = new Item
                {
                    Renderer = r,
                    Path = path,
                    Material = r.sharedMaterial != null ? r.sharedMaterial.name : "(无材质)",
                    Category = GuessCategory(r),
                    AtlasIndex = r.lightmapIndex,
                    UvAvg = uvAvg,
                    UvBlackRatio = uvRatio,
                    UvSamples = uvSamples,
                    IsKnownFixed = IsInFixedList(path, knownFixed),
                };
                items.Add(it);
            }
            sb.AppendLine("通道 A 有效采样：" + items.Count + " 个 Renderer");

            int totalOutOfRange = 0;
            foreach (KeyValuePair<int, AtlasLum> kv in atlases) totalOutOfRange += kv.Value.OutOfRange;
            sb.AppendLine("ST 变换后落在图集 [0,1] 之外的采样点：" + totalOutOfRange +
                          "（已跳过，不计入均值；若异常偏大说明 ST 口径需复核）");
            sb.AppendLine();

            // ---- 通道 B：候选集（UV 黑/偏暗 + 强制基准 + 已修复清单对象）----
            List<Item> candidates = BuildRenderCandidates(items);
            int truncated = candidates.Count > MaxRenderProbeTargets
                ? candidates.Count - MaxRenderProbeTargets : 0;
            if (truncated > 0) candidates.RemoveRange(MaxRenderProbeTargets, truncated);

            sb.AppendLine("通道 B 渲染实测候选：" + candidates.Count + " 个" +
                          (truncated > 0 ? "（按 UV 黑度截断，另有 " + truncated + " 个未测）" : "（无截断）"));
            sb.AppendLine();

            // Hide Others：一次性快照 + 全关，每个候选只开关自己（O(N+M)，不是 O(N×M)）。
            // 现场还原放在外层 finally —— 即使中途取消、异常、断言失败，也保证全部还原。
            bool[] savedEnabled = new bool[all.Length];
            for (int i = 0; i < all.Length; i++)
                savedEnabled[i] = all[i] != null && all[i].enabled;

            bool cancelled = false;
            try
            {
                for (int i = 0; i < all.Length; i++)
                    if (all[i] != null) all[i].enabled = false;

                for (int i = 0; i < candidates.Count; i++)
                {
                    float progress = (i + 1f) / Mathf.Max(1, candidates.Count);
                    if (EditorUtility.DisplayCancelableProgressBar(
                            "P30 双指标审计", "离屏渲染 " + (i + 1) + "/" + candidates.Count, progress))
                    {
                        cancelled = true;
                        break;
                    }

                    Renderer target = candidates[i].Renderer;
                    if (target == null || !target.gameObject.activeInHierarchy)
                    {
                        candidates[i].Render = new RenderResult { View = "未激活·不测" };
                        continue;
                    }

                    target.enabled = true;
                    try
                    {
                        candidates[i].Render = CaptureRenderedLuminance(target);
                    }
                    finally
                    {
                        target.enabled = false;
                    }
                }
            }
            finally
            {
                for (int i = 0; i < all.Length; i++)
                    if (all[i] != null) all[i].enabled = savedEnabled[i];
                EditorUtility.ClearProgressBar();
            }

            if (cancelled)
            {
                Debug.LogWarning("[P30] 用户取消，本次不出清单（现场已还原）。");
                return;
            }

            // ---- 四象限判读 ----
            for (int i = 0; i < items.Count; i++) items[i].Verdict = Classify(items[i]);

            // ---- 自测断言：必须在出清单之前，失败即报错退出、不落盘 ----
            StringBuilder selfTest = new StringBuilder(2048);
            if (!RunSelfTest(items, selfTest))
            {
                sb.AppendLine("---------- 自测断言：失败 ----------");
                sb.Append(selfTest.ToString());
                sb.AppendLine("本次不输出清单、不写报告文件。请按上面的 FAIL 项排查后重跑。");
                Debug.LogError("[P30] 自测断言失败，本次不出清单。\n" + selfTest.ToString());
                return;
            }

            sb.AppendLine("---------- 自测断言：通过 ----------");
            sb.Append(selfTest.ToString());
            sb.AppendLine();

            // ---- 输出 ----
            AppendSummary(sb, items);
            AppendDetails(sb, items);

            sb.AppendLine("===== 结束 =====");

            string text = sb.ToString();
            Debug.Log(text);
            SaveToFile(text);
        }

        // ================================================================ 已修复清单（反射，单一真相源）

        /// <summary>
        /// 从运行时脚本的私有静态字段读 12 条修复路径，**不另抄一份常量**。
        /// 抄一份会产生第二真相源 —— D14「三块吊顶被写进清单却没验证」就是这种漂移的后果。
        /// 读不到时不静默放行：返回空表并让 Floor2 断言失败，工具主动报错退出。
        /// </summary>
        private static List<string> LoadKnownFixedPaths(StringBuilder sb)
        {
            List<string> result = new List<string>();
            try
            {
                FieldInfo field = typeof(SchoolTwin.Twin.P30FixCeilingLightmap)
                    .GetField("DefaultPaths", BindingFlags.NonPublic | BindingFlags.Static);
                if (field == null)
                {
                    sb.AppendLine("⚠ 反射未找到 DefaultPaths 字段，已修复清单为空（Floor2 断言将失败）");
                    return result;
                }
                string[] paths = field.GetValue(null) as string[];
                if (paths == null) return result;
                for (int i = 0; i < paths.Length; i++)
                    if (!string.IsNullOrEmpty(paths[i])) result.Add(paths[i]);
            }
            catch (Exception e)
            {
                sb.AppendLine("⚠ 读已修复清单异常：" + e.Message);
            }
            return result;
        }

        /// <summary>
        /// 修复表里的路径形如 `SchoolScene/Exterior/SchoolBuilding/Floor2`，
        /// 但 FBX 导入层级往往在场景里多一层模型根节点，全串子串匹配会漏判。
        /// 因此只比「父段/子段」这两段（SchoolBuilding/Floor2），对前缀差异免疫。
        /// </summary>
        private static bool IsInFixedList(string path, List<string> fixedPaths)
        {
            if (string.IsNullOrEmpty(path)) return false;
            for (int i = 0; i < fixedPaths.Count; i++)
            {
                string[] seg = fixedPaths[i].Split('/');
                if (seg.Length < 2) continue;
                string tail = seg[seg.Length - 2] + "/" + seg[seg.Length - 1];
                if (path.IndexOf(tail, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        /// <summary>按完整路径段精确匹配对象名，避免 `ZalTop` 误命中 `ZalTop_02`。</summary>
        private static bool HasSegment(string path, string name)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string[] seg = path.Split('/');
            for (int i = 0; i < seg.Length; i++)
                if (string.Equals(seg[i], name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // ================================================================ 通道 A · 图集（修缺陷 B）

        /// <summary>
        /// 按**图集实际尺寸**全分辨率读取，不再降采样到固定 256。
        /// 只读得到单通道亮度，避免 48 张 Color[] 把编辑器内存吃满。
        /// </summary>
        private static Dictionary<int, AtlasLum> BuildAtlasCache(StringBuilder sb)
        {
            Dictionary<int, AtlasLum> cache = new Dictionary<int, AtlasLum>(64);
            LightmapData[] maps = LightmapSettings.lightmaps;

            sb.AppendLine("---------- 光照图集（按实际尺寸读取）----------");
            sb.AppendLine("lightmaps 数量：" + (maps != null ? maps.Length : 0));
            if (maps == null) return cache;

            for (int i = 0; i < maps.Length; i++)
            {
                LightmapData d = maps[i];
                if (d == null || d.lightmapColor == null) continue;

                AtlasLum atlas = ReadAtlasFullResolution(d.lightmapColor);
                if (atlas == null) continue;
                cache[i] = atlas;

                sb.AppendLine("  图集 " + i.ToString().PadLeft(2) + "：" + d.lightmapColor.name +
                              "  " + atlas.Width + "×" + atlas.Height + "（全分辨率读取）");
            }
            sb.AppendLine();
            return cache;
        }

        private static AtlasLum ReadAtlasFullResolution(Texture2D src)
        {
            RenderTexture rt = null;
            Texture2D readable = null;
            RenderTexture prev = RenderTexture.active;
            try
            {
                int w = src.width;
                int h = src.height;
                if (w <= 0 || h <= 0) return null;

                // isReadable 通常为 false，走 Blit → RT → ReadPixels 绕开限制。
                // 这里的 RT 尺寸 = 图集实际尺寸，是缺陷 B 的核心修正点。
                rt = RenderTexture.GetTemporary(w, h, 0,
                    RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                Graphics.Blit(src, rt);
                RenderTexture.active = rt;

                readable = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
                readable.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                readable.Apply();
                Color32[] px = readable.GetPixels32();

                byte[] lum = new byte[w * h];
                for (int k = 0; k < px.Length; k++)
                {
                    Color32 c = px[k];
                    lum[k] = (byte)Mathf.Clamp(
                        0.299f * c.r + 0.587f * c.g + 0.114f * c.b, 0f, 255f);
                }

                return new AtlasLum { Pixels = lum, Width = w, Height = h, OutOfRange = 0 };
            }
            catch (Exception e)
            {
                Debug.LogWarning("[P30] 读图集失败：" + src.name + " — " + e.Message);
                return null;
            }
            finally
            {
                RenderTexture.active = prev;
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
                if (readable != null) UnityEngine.Object.DestroyImmediate(readable);
            }
        }

        /// <summary>
        /// 双线性采样（v2 是点采样，会漏掉 1/16 的小黑块）。
        /// uv 已归一化到 [0,1]；边界用 clamp，不做 wrap —— 图集 padding 不该被当成有效光。
        /// </summary>
        private static float SampleBilinear(AtlasLum atlas, Vector2 uv)
        {
            float fx = Mathf.Clamp(uv.x, 0f, 1f) * atlas.Width - 0.5f;
            float fy = Mathf.Clamp(uv.y, 0f, 1f) * atlas.Height - 0.5f;
            int x0 = Mathf.FloorToInt(fx);
            int y0 = Mathf.FloorToInt(fy);
            float tx = fx - x0;
            float ty = fy - y0;
            int x1 = x0 + 1;
            int y1 = y0 + 1;

            x0 = Mathf.Clamp(x0, 0, atlas.Width - 1);
            x1 = Mathf.Clamp(x1, 0, atlas.Width - 1);
            y0 = Mathf.Clamp(y0, 0, atlas.Height - 1);
            y1 = Mathf.Clamp(y1, 0, atlas.Height - 1);

            float a = atlas.Pixels[y0 * atlas.Width + x0] / 255f;
            float b = atlas.Pixels[y0 * atlas.Width + x1] / 255f;
            float c = atlas.Pixels[y1 * atlas.Width + x0] / 255f;
            float d = atlas.Pixels[y1 * atlas.Width + x1] / 255f;
            return Mathf.Lerp(Mathf.Lerp(a, b, tx), Mathf.Lerp(c, d, tx), ty);
        }

        /// <summary>
        /// 通道 A：按网格 UV2 在图集上逐点采样，输出平均亮度与黑点占比。
        /// **缺陷 A 修正点**：UV2 必须先过 lightmapScaleOffset 的 ST 变换才能落到图集坐标。
        /// </summary>
        private static bool SampleUvLuminance(Renderer r, AtlasLum atlas,
                                              out float avg, out float ratio, out int samples)
        {
            avg = 0f;
            ratio = 0f;
            samples = 0;

            MeshFilter mf = r.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) return false;

            Vector2[] uv2 = mf.sharedMesh.uv2;
            if (uv2 == null || uv2.Length == 0) return false;

            // ── 缺陷 A 的修正：ST 变换。st.xy = scale，st.zw = offset ──
            // v2 直接 `Vector2 uv = uv2[i]`，在 packer 重排过的对象上系统性采错位置。
            Vector4 st = r.lightmapScaleOffset;

            int stride = Mathf.Max(1, uv2.Length / MaxUvSamplesPerMesh);
            float sum = 0f;
            int black = 0;
            int outOfRange = 0;

            for (int i = 0; i < uv2.Length; i += stride)
            {
                Vector2 uv = new Vector2(
                    uv2[i].x * st.x + st.z,
                    uv2[i].y * st.y + st.w);

                if (uv.x < 0f || uv.x > 1f || uv.y < 0f || uv.y > 1f)
                {
                    outOfRange++;
                    continue;
                }

                float lum = SampleBilinear(atlas, uv);
                sum += lum;
                if (lum < UvBlackThreshold) black++;
                samples++;
            }

            atlas.OutOfRange += outOfRange;
            if (samples == 0) return false;

            avg = sum / samples;
            ratio = black / (float)samples;
            return true;
        }

        // ================================================================ 通道 B · 渲染实测（Hide Others）

        private static List<Item> BuildRenderCandidates(List<Item> items)
        {
            List<Item> forced = new List<Item>();
            List<Item> byUv = new List<Item>();

            for (int i = 0; i < items.Count; i++)
            {
                Item it = items[i];

                // 自测基准 + 已修复清单对象强制进候选，不因 UV 判亮而被漏掉。
                bool isBaseline =
                    HasSegment(it.Path, "ZalTop") ||
                    HasSegment(it.Path, "Floor2") ||
                    it.IsKnownFixed;

                if (isBaseline) forced.Add(it);
                else if (UvIsBlack(it) || it.UvAvg < UvDimThreshold) byUv.Add(it);
            }

            // 黑的排前面，截断时优先保留更可疑的。
            byUv.Sort((a, b) =>
            {
                int c = b.UvBlackRatio.CompareTo(a.UvBlackRatio);
                return c != 0 ? c : a.UvAvg.CompareTo(b.UvAvg);
            });

            List<Item> result = new List<Item>(forced.Count + byUv.Count);
            result.AddRange(forced);
            result.AddRange(byUv);
            return result;
        }

        /// <summary>
        /// 通道 B：把目标 Renderer 单独离屏渲染一次，统计非哨兵像素的平均亮度。
        ///
        /// ⚠️ 这是**相机渲染实测**，与上方 Graphics.Blit 读图集是完全不同的两件事
        /// （判据 D13-08 明确要求二者可区分，故独立成方法，命名 CaptureRenderedLuminance）。
        ///
        /// 只读纪律：Hide Others 的 enabled 快照/还原由调用方 <see cref="Run"/> 的外层 finally 统一负责
        /// （避免每个候选都遍历全场景）；本方法只负责销毁自己创建的临时 GO / RT / Texture2D。
        /// 不写场景、不写材质、不写光照设置、不改 lightmapIndex、不标脏、不进 Play。
        /// </summary>
        private static RenderResult CaptureRenderedLuminance(Renderer target)
        {
            GameObject probeGo = null;
            RenderTexture rt = null;
            Texture2D readable = null;
            RenderTexture prevActive = RenderTexture.active;

            try
            {
                // 调用前已由 Run() 完成 Hide Others：此刻场景中只有 target 处于 enabled。
                Bounds bounds = target.bounds;
                float radius = Mathf.Max(bounds.extents.x, bounds.extents.y, bounds.extents.z);
                if (radius < 0.01f) radius = 0.01f;

                probeGo = new GameObject("__P30_RenderProbe__");
                probeGo.hideFlags = HideFlags.HideAndDontSave;
                Camera cam = probeGo.AddComponent<Camera>();
                // 不让它进编辑器常规渲染循环：只由下面的 cam.Render() 手动驱动。
                // 但 Camera.Render() 在 disabled 相机上行为不可靠，故在同步窗口内临时开启、
                // 渲染完立刻关回 false（同一帧内完成，编辑器渲染循环插不进来）。
                cam.enabled = false;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = SentinelColor;
                cam.orthographic = true;
                cam.orthographicSize = radius * 1.6f;
                cam.nearClipPlane = 0.01f;
                cam.farClipPlane = 2000f;
                cam.cullingMask = ~0;

                rt = RenderTexture.GetTemporary(RenderProbeSize, RenderProbeSize, 16,
                    RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                cam.targetTexture = rt;
                readable = new Texture2D(RenderProbeSize, RenderProbeSize,
                    TextureFormat.RGBA32, false, true);

                // ⚠️ 不能用「有效像素最多」选视角：正交相机下 6 个轴向常常都是满屏
                // （实测 ZalTop 六轴均 4096/4096），该判据会退化成「永远选数组第一个」，
                // 也就是永远从正上方看吊顶的**上表面**，而验收基准是室内可见的**下表面**。
                // 改为：6 轴全渲染、全记录，取「亮度最高」的一帧作为该对象的可见面读数。
                // 全部 6 个数值一并写进报告，由打光判读，工具不替人做单帧裁决。
                // Avg 初值 -1：否则「六轴全黑」时 `avg > 0` 恒 false，View 会停在 N/A。
                RenderResult best = new RenderResult { Avg = -1f };
                StringBuilder views = new StringBuilder(160);
                for (int d = 0; d < ProbeDirections.Length; d++)
                {
                    Vector3 dir = ProbeDirections[d];
                    // 正上/正下方向会让 LookAt 的默认 up 与视线平行而退化，显式换一个 worldUp。
                    Vector3 worldUp = Mathf.Abs(dir.y) > 0.9f ? Vector3.forward : Vector3.up;
                    cam.transform.position = bounds.center + dir * (radius * 3f + 1f);
                    cam.transform.LookAt(bounds.center, worldUp);
                    cam.enabled = true;
                    cam.Render();
                    cam.enabled = false;

                    RenderTexture.active = rt;
                    readable.ReadPixels(new Rect(0, 0, RenderProbeSize, RenderProbeSize), 0, 0);
                    readable.Apply();
                    RenderTexture.active = prevActive;

                    int count, background, dark;
                    float avg = AverageNonSentinel(readable.GetPixels32(),
                                                   out count, out background, out dark);
                    float darkRatio = count > 0 ? dark / (float)count : 0f;
                    views.Append(ViewLabel(dir)).Append('=')
                         .Append(count > 0 ? avg.ToString("0.0000") : "N/A")
                         .Append("/暗").Append((darkRatio * 100f).ToString("0")).Append('%')
                         .Append('(').Append(count).Append("px) ");
                    // 像素太少的帧不采信：可能只是擦到一条边，暗值属噪声。
                    if (count < MinValidPixels) continue;

                    best.Valid = true;
                    best.BackgroundPixels += background;
                    if (avg > best.Avg)
                    {
                        best.Avg = avg;
                        best.Pixels = count;
                        best.View = ViewLabel(dir);
                    }
                    if (avg < best.MinAvg)
                    {
                        best.MinAvg = avg;
                        best.MinView = ViewLabel(dir);
                        best.MinDarkRatio = darkRatio;
                    }
                }
                best.Views = views.ToString().TrimEnd();
                return best;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[P30] 渲染实测失败：" + (target != null ? target.name : "(null)") + " — " + e.Message);
                return new RenderResult();
            }
            finally
            {
                // ---- 只清理本方法自己创建的临时资源；Renderer.enabled 由 Run() 的 finally 还原 ----
                RenderTexture.active = prevActive;
                if (rt != null)
                {
                    Camera cam = probeGo != null ? probeGo.GetComponent<Camera>() : null;
                    if (cam != null) cam.targetTexture = null;
                    RenderTexture.ReleaseTemporary(rt);
                }
                if (readable != null) UnityEngine.Object.DestroyImmediate(readable);
                if (probeGo != null) UnityEngine.Object.DestroyImmediate(probeGo);
            }
        }

        /// <summary>
        /// 剔除洋红哨兵后求平均亮度。线性空间值（项目为 Linear），不转 sRGB。
        /// 返回 -1 表示没有任何有效像素（该对象在此机位没渲染出几何）。
        /// <paramref name="background"/> 回传被剔除的哨兵像素数，用于确认 clear color 是否真的生效。
        /// <paramref name="dark"/> 回传亮度低于 <see cref="RenderBlackThreshold"/> 的像素数，
        /// 用于算「局部发黑占比」—— 平均值会掩盖局部缺陷，这个数才是 D13 的抓手。
        /// </summary>
        private static float AverageNonSentinel(Color32[] px,
                                                out int count, out int background, out int dark)
        {
            count = 0;
            background = 0;
            dark = 0;
            float sum = 0f;
            for (int i = 0; i < px.Length; i++)
            {
                Color32 c = px[i];
                // 哨兵：高红、低绿、高蓝。本项目深蓝冷白体系无洋红材质，不会误伤。
                if (c.r > 200 && c.g < 60 && c.b > 200)
                {
                    background++;
                    continue;
                }
                float lum = (0.299f * c.r + 0.587f * c.g + 0.114f * c.b) / 255f;
                sum += lum;
                if (lum < RenderBlackThreshold) dark++;
                count++;
            }
            return count > 0 ? sum / count : -1f;
        }

        /// <summary>把 6 轴方向压成短标签，报告里可读性比 ToString() 好得多。</summary>
        private static string ViewLabel(Vector3 dir)
        {
            if (dir.y > 0.5f) return "上表面";
            if (dir.y < -0.5f) return "下表面";
            if (dir.x > 0.5f) return "+X";
            if (dir.x < -0.5f) return "-X";
            if (dir.z > 0.5f) return "+Z";
            return "-Z";
        }

        // ================================================================ 四象限判读

        private static bool UvIsBlack(Item it)
        {
            return it.UvAvg < UvBlackThreshold || it.UvBlackRatio >= UvBlackRatioThreshold;
        }

        private static Verdict Classify(Item it)
        {
            bool uvBlack = UvIsBlack(it);

            // 候选集外 / 渲染通道没测到有效几何 → 只能按 UV 单指标标记，标注「未复核」。
            if (!it.Render.Valid) return uvBlack ? Verdict.UvOnly : Verdict.Normal;

            // 判黑看 6 轴里**最暗**的一帧：D13 的病灶是局部发黑（翅片间/竖梃），
            // 只看最亮帧会把它抹平成「正常」。最亮帧只用于 ZalTop 基准断言。
            // 两个条件取或：①整帧都黑 ②局部暗像素占比达标（平均值抓不到的那种黑）。
            bool hasMin = it.Render.MinAvg < float.MaxValue;
            bool renderBlack = hasMin &&
                (it.Render.MinAvg < RenderBlackThreshold ||
                 it.Render.MinDarkRatio >= LocalDarkRatioThreshold);
            if (uvBlack && renderBlack) return Verdict.TrueBlack;
            if (uvBlack && !renderBlack) return Verdict.UvMetricInvalid;
            if (!uvBlack && renderBlack) return Verdict.RenderOnlyBlack;
            return Verdict.Normal;
        }

        private static string VerdictLabel(Verdict v)
        {
            switch (v)
            {
                case Verdict.TrueBlack: return "真黑·双指标一致";
                case Verdict.UvMetricInvalid: return "指标失效·禁派修";
                case Verdict.RenderOnlyBlack: return "仅渲染黑·待复核";
                case Verdict.UvOnly: return "仅UV黑·未实测";
                default: return "正常";
            }
        }

        // ================================================================ 自测断言

        /// <summary>
        /// 两条硬断言，任一失败即返回 false，调用方报错退出、不出清单、不落盘。
        ///
        /// ① ZalTop 必须**不被判黑**：证伪 A 的决定性反例是「旧工具 UV 采样 0.0351
        ///    判近全黑，但屏幕上是全场最亮的矿棉板」。新工具要保证的是
        ///    「两通道都不把它判黑、且渲染实测不低于 UV 采样」—— 即旧指标的系统性
        ///    偏低已被纠正。用绝对亮线 0.25 卡它是错的（见 RenderBrightReference 注释）。
        ///    6 轴逐视角数值一并打印，由打光判读；工具不替人做单帧裁决。
        /// ② Floor2 必须可识别为「已修复」：它在运行时修复清单里，
        ///    不能被当成新缺陷报出去造成重复派工（D4 / D14 的技术债口径）。
        /// </summary>
        private static bool RunSelfTest(List<Item> items, StringBuilder diag)
        {
            bool ok = true;

            Item zal = null;
            Item floor = null;
            for (int i = 0; i < items.Count; i++)
            {
                Item it = items[i];
                if (zal == null && HasSegment(it.Path, "ZalTop")) zal = it;
                if (floor == null && HasSegment(it.Path, "Floor2")) floor = it;
            }

            if (zal == null)
            {
                diag.AppendLine("FAIL [ZalTop] 场景中未找到该基准对象。请确认已打开主场景 SchoolSceneDay 后重跑。");
                ok = false;
            }
            else if (!zal.Render.Valid)
            {
                diag.AppendLine("FAIL [ZalTop] 渲染通道未取得有效像素（Views 全部落空）。" +
                                " Path=" + zal.Path);
                ok = false;
            }
            else if (zal.Render.Avg < RenderBlackThreshold)
            {
                diag.AppendLine("FAIL [ZalTop] 渲染实测确实判黑（低于 RenderBlackThreshold=" +
                                RenderBlackThreshold.ToString("0.00") +
                                "），与「屏幕上最亮的矿棉板」基准矛盾。" +
                                " renderAvg=" + zal.Render.Avg.ToString("0.0000") +
                                " view=" + zal.Render.View + " pixels=" + zal.Render.Pixels +
                                " 背景剔除=" + zal.Render.BackgroundPixels +
                                " | uvAvg=" + zal.UvAvg.ToString("0.0000"));
                diag.AppendLine("       6 轴逐视角：" + zal.Render.Views);
                ok = false;
            }
            else if (zal.Render.Avg < zal.UvAvg)
            {
                diag.AppendLine("FAIL [ZalTop] 渲染实测低于 UV 采样（" +
                                zal.Render.Avg.ToString("0.0000") + " < " + zal.UvAvg.ToString("0.0000") +
                                "），说明新指标仍有系统性偏低，ST 变换口径需复核。" +
                                " view=" + zal.Render.View);
                diag.AppendLine("       6 轴逐视角：" + zal.Render.Views);
                ok = false;
            }
            else
            {
                diag.AppendLine("PASS [ZalTop] 未被判黑：renderAvg=" + zal.Render.Avg.ToString("0.0000") +
                                " ≥ 判黑线 " + RenderBlackThreshold.ToString("0.00") +
                                "，且 ≥ UV " + zal.UvAvg.ToString("0.0000") +
                                "（旧工具 UV=0.0351 判近全黑，ST 变换已纠正）。" +
                                " 判读=" + VerdictLabel(zal.Verdict) + "，最佳视角=" + zal.Render.View);
                diag.AppendLine("       6 轴逐视角：" + zal.Render.Views);
                diag.AppendLine("       最暗帧=" + (zal.Render.MinAvg < float.MaxValue
                                    ? zal.Render.MinAvg.ToString("0.0000") : "N/A") +
                                "（" + zal.Render.MinView + "）暗占比=" +
                                (zal.Render.MinDarkRatio * 100f).ToString("0") +
                                "%；背景剔除像素=" + zal.Render.BackgroundPixels);
                diag.AppendLine("       注：断言用最亮帧判「不判死」；若最暗帧暗占比高，" +
                                "ZalTop 仍会进「仅渲染黑·待复核」族交打光判读，两者不冲突。");
                diag.AppendLine("       偏亮参考线 " + RenderBrightReference.ToString("0.00") +
                                "（仅标注、不作断言）：" +
                                (zal.Render.Avg >= RenderBrightReference ? "已达" : "未达") +
                                "；该线取值待打光按渲染实测裁决。");
            }

            if (floor == null)
            {
                diag.AppendLine("FAIL [Floor2] 场景中未找到该基准对象。请确认已打开主场景 SchoolSceneDay 后重跑。");
                ok = false;
            }
            else if (!floor.IsKnownFixed)
            {
                diag.AppendLine("FAIL [Floor2] 未识别为「已修复」：反射读取的 DefaultPaths 中不含该路径。" +
                                " Path=" + floor.Path);
                ok = false;
            }
            else
            {
                diag.AppendLine("PASS [Floor2] 已识别为「已修复（运行时 lightmapIndex=-1）」" +
                                "，不作新缺陷外报。Path=" + floor.Path);
            }

            return ok;
        }

        // ================================================================ 输出

        private static void AppendSummary(StringBuilder sb, List<Item> items)
        {
            Dictionary<Verdict, int> counts = new Dictionary<Verdict, int>();
            for (int i = 0; i < items.Count; i++)
            {
                Verdict v = items[i].Verdict;
                int n;
                counts.TryGetValue(v, out n);
                counts[v] = n + 1;
            }

            sb.AppendLine("---------- 四象限总账 ----------");
            sb.AppendLine("  真黑·双指标一致      ：" + CountOf(counts, Verdict.TrueBlack));
            sb.AppendLine("  指标失效·禁派修      ：" + CountOf(counts, Verdict.UvMetricInvalid) +
                          "   ← 旧 108 条清单的错就出在这一族");
            sb.AppendLine("  仅渲染黑·待复核      ：" + CountOf(counts, Verdict.RenderOnlyBlack));
            sb.AppendLine("  仅UV黑·未实测        ：" + CountOf(counts, Verdict.UvOnly));
            sb.AppendLine("  正常                 ：" + CountOf(counts, Verdict.Normal));
            sb.AppendLine();

            int measured = 0, fixedCount = 0;
            Dictionary<string, int> byCat = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < items.Count; i++)
            {
                Item it = items[i];
                if (it.Render.Valid) measured++;
                if (it.IsKnownFixed) fixedCount++;
                if (it.Verdict == Verdict.Normal) continue;
                int n;
                byCat.TryGetValue(it.Category, out n);
                byCat[it.Category] = n + 1;
            }
            sb.AppendLine("渲染实测已覆盖：" + measured + " / " + items.Count +
                          "；已修复清单命中：" + fixedCount);
            sb.AppendLine();

            sb.AppendLine("---------- 标记项按类别 ----------");
            foreach (KeyValuePair<string, int> kv in SortedByCount(byCat))
                sb.AppendLine("  " + kv.Key.PadRight(12) + kv.Value + " 处");
            sb.AppendLine();
        }

        private static int CountOf(Dictionary<Verdict, int> counts, Verdict v)
        {
            int n;
            return counts.TryGetValue(v, out n) ? n : 0;
        }

        private static void AppendDetails(StringBuilder sb, List<Item> items)
        {
            List<Item> marked = new List<Item>();
            for (int i = 0; i < items.Count; i++)
                if (items[i].Verdict != Verdict.Normal) marked.Add(items[i]);

            // 真黑在前、指标失效在后；同为黑时按渲染亮度升序（最黑先报）。
            marked.Sort((a, b) =>
            {
                int c = Rank(a.Verdict).CompareTo(Rank(b.Verdict));
                if (c != 0) return c;
                return a.Render.Avg.CompareTo(b.Render.Avg);
            });

            sb.AppendLine("---------- 明细（" + marked.Count + " 条，任一指标判黑即列入）----------");
            sb.AppendLine("渲染均值=6轴最亮帧（判「不黑」用）；最暗帧+暗占比=判「局部发黑」用，两者都要看");
            sb.AppendLine("局部发黑判据：最暗帧亮度 < " + RenderBlackThreshold.ToString("0.00") +
                          " 或 暗占比 ≥ " + (LocalDarkRatioThreshold * 100f).ToString("0") + "%");
            sb.AppendLine("判读 | UV均值 | UV黑占比 | 渲染均值 | 最暗帧 | 暗占比 | 最暗视角 | 有效像素 | 类别 | 图集 | 修复表 | 材质 | 路径");
            for (int i = 0; i < marked.Count; i++)
            {
                Item it = marked[i];
                string renderAvg = it.Render.Valid
                    ? it.Render.Avg.ToString("0.0000") : "N/A";
                string minAvg = it.Render.MinAvg < float.MaxValue
                    ? it.Render.MinAvg.ToString("0.0000") : "N/A";
                sb.AppendLine(
                    VerdictLabel(it.Verdict).PadRight(18) + " | " +
                    it.UvAvg.ToString("0.0000") + " | " +
                    (it.UvBlackRatio * 100f).ToString("0").PadLeft(3) + "% | " +
                    renderAvg.PadRight(8) + " | " +
                    minAvg.PadRight(8) + " | " +
                    (it.Render.MinDarkRatio * 100f).ToString("0").PadLeft(3) + "% | " +
                    it.Render.MinView.PadRight(10) + " | " +
                    it.Render.Pixels.ToString().PadLeft(4) + " | " +
                    it.Category.PadRight(10) + " | " +
                    it.AtlasIndex.ToString().PadLeft(2) + " | " +
                    (it.IsKnownFixed ? "是" : "否").PadRight(6) + " | " +
                    it.Material.PadRight(22) + " | " +
                    it.Path);
            }
            sb.AppendLine();
            sb.AppendLine("注：「指标失效·禁派修」一族 UV 采样判黑但渲染实测判亮，" +
                          "按裁决 C-67 不得作为派修依据；请以渲染实测为准，交打光判读。");
            sb.AppendLine();
        }

        private static int Rank(Verdict v)
        {
            switch (v)
            {
                case Verdict.TrueBlack: return 0;
                case Verdict.RenderOnlyBlack: return 1;
                case Verdict.UvOnly: return 2;
                case Verdict.UvMetricInvalid: return 3;
                default: return 4;
            }
        }

        // ================================================================ 辅助

        /// <summary>
        /// 分类关键词。**缺陷 C 修正**：补 Radiator（暖气片）；
        /// 同时核过 Window / Mullion —— 窗框在资产里是 `Windows*` / `WindowLeft` / `GlassWindows`
        /// 这类命名，无独立 `Mullion` 节点（竖梃疑在合并网格内），故按 Window 族归并并单列说明。
        /// 顺序即优先级：Radiator 必须排在 Wall / Glass 之前，避免被泛化分支吞掉。
        /// </summary>
        private static string GuessCategory(Renderer r)
        {
            string n = r.gameObject.name;

            if (Contains(n, "Radiator")) return "暖气片";
            if (Contains(n, "Mullion")) return "窗框竖梃";
            if (Contains(n, "Window")) return "窗框/窗";
            if (Contains(n, "Top")) return "吊顶";
            if (Contains(n, "Wall")) return "墙";
            if (Contains(n, "Door")) return "门";
            if (Contains(n, "Light")) return "灯";
            if (Contains(n, "Border")) return "边条";
            if (Contains(n, "Glass")) return "玻璃";
            if (Contains(n, "Lamp")) return "路灯";
            if (Contains(n, "Label")) return "UI文字";

            return "其他";
        }

        private static bool Contains(string name, string keyword)
        {
            return name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string BuildPath(Transform t)
        {
            StringBuilder sb = new StringBuilder(96);
            while (t != null)
            {
                if (sb.Length > 0) sb.Insert(0, "/");
                sb.Insert(0, t.name);
                t = t.parent;
            }
            return sb.ToString();
        }

        private static List<KeyValuePair<string, int>> SortedByCount(Dictionary<string, int> src)
        {
            List<KeyValuePair<string, int>> list = new List<KeyValuePair<string, int>>(src);
            list.Sort((a, b) => b.Value.CompareTo(a.Value));
            return list;
        }

        private static void SaveToFile(string text)
        {
            try
            {
                string root = Directory.GetParent(Application.dataPath).FullName;
                string dir = Path.Combine(root, EvidenceDir);
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir,
                    "P30_rescan_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt");
                File.WriteAllText(path, text, Encoding.UTF8);
                Debug.Log("[P30] 完整清单已写入 " + path);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[P30] 清单落盘失败（不影响 Console 输出）：" + e.Message);
            }
        }
    }
}
#endif

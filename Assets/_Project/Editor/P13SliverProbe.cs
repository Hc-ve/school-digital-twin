using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace SchoolTwin.EditorTools
{
    /// <summary>
    /// P13「室内外诡异白线」自动取证工具。
    ///
    /// ══════════════════════════════════════════════════════════════════
    /// 【为什么要用"逐层剥离"而不是继续读代码猜】
    ///
    /// 这条白线已经查了四轮：
    ///   · 第一轮怀疑雪盘竖直悬崖 → 改了两版，日志证明竖直面 = 0 处；
    ///   · 第二轮怀疑 ZTest Always    → 只有主播屏合成用了，和场景无关；
    ///   · 第三轮做了像素测量        → 白带呈透镜形、中间厚两端尖灭；
    ///   · 第四轮算了几何            → 雪盘比相机低 3m，投影最高只到屏幕行 571，
    ///                                而白带在 261，数学上不可能是雪盘。
    ///
    /// 每一轮都能"排除一个"，但没人能"确认一个"。继续这样猜下去是在烧时间。
    ///
    /// 【本工具的做法】
    /// 不用推测，直接用实验判定：
    ///   ① 在 Play 状态下截一帧，测出白带那条线上相对背景的亮度增量（基准值）；
    ///   ② 依次把候选物体 SetActive(false)，每关一个再截一帧、再测同一个位置；
    ///   ③ 谁的增量掉到 0（或大幅下降），白线就是谁画的；
    ///   ④ 测完把全部物体恢复原状，不改场景。
    ///
    /// 这是排障里最可靠的一招 —— 它不依赖对代码的任何假设。
    /// 唯一要求是 Play 状态（让相机/天气都在运行时状态）。
    ///
    /// ── 白带在屏幕上的位置怎么定 ──────────────────────────────────
    /// 不写死行号。做法：沿屏幕中线两侧各取一条竖带，逐行算"与该行上下
    /// 各 6 行的均值之差"，差值最大的那几行就是白带所在。
    /// 这样无论相机怎么摆、窗口多大，都能自动找到它。
    /// ══════════════════════════════════════════════════════════════════
    /// </summary>
    public static class P13SliverProbe
    {
        private const string MenuRoot = "归档/P13 时间天气/";

        [MenuItem(MenuRoot + "① 逐层剥离：白线是谁画的（Play 中运行）", false, 40)]
        public static void ProbeSliver()
        {
            if (!Application.isPlaying)
            {
                EditorUtility.DisplayDialog("需要 Play 状态",
                    "请在 Play 运行中、切到雪天、画面能看到那条白线时再点。\n\n" +
                    "本工具要把候选物体逐个关闭并读像素对比，编辑器模式没有运行时的相机与天气状态。",
                    "知道了");
                return;
            }

            var sb = new StringBuilder(4096);
            sb.AppendLine("========== P13 白线逐层剥离 ==========");

            // ── 第 0 步：找到白带的屏幕行 ──────────────────────────────
            int bandRow;
            float baseline;
            if (!MeasureBand(out bandRow, out baseline))
            {
                Debug.LogWarning("[P13][白线探针] 屏幕读数失败，无法定位白带。");
                return;
            }
            sb.AppendLine("白带定位：屏幕 y = " + bandRow + "   亮度增量 = " + baseline.ToString("F2"));

            if (baseline < 3f)
            {
                sb.AppendLine("增量太小（<3），说明这一帧没有白带。");
                sb.AppendLine("请确认：① 正在 Play ② 已切雪天 ③ 相机正对能看到白线的角度");
                Debug.Log(sb.ToString());
                return;
            }

            // ── 第 1 步：收集候选 ──────────────────────────────────────
            var candidates = CollectCandidates();
            sb.AppendLine("候选物体 = " + candidates.Count + " 个");
            sb.AppendLine();

            // ── 第 2 步：逐个关闭 + 测增量 ────────────────────────────
            var results = new List<KeyValuePair<string, float>>();
            for (int i = 0; i < candidates.Count; i++)
            {
                GameObject go = candidates[i];
                if (go == null) continue;

                bool prev = go.activeSelf;
                go.SetActive(false);

                int row2;
                float after;
                bool ok = MeasureBand(out row2, out after);

                go.SetActive(prev);   // 立刻恢复，绝不改场景

                if (!ok) continue;

                float drop = baseline - after;
                results.Add(new KeyValuePair<string, float>(
                    go.name + "  (计划:" + after.ToString("F2") + ", 降 " + drop.ToString("F2") + ")",
                    drop));

                if (drop > 3f)
                {
                    sb.AppendLine("  ★ " + go.name
                                  + "  关闭后增量 " + baseline.ToString("F2")
                                  + " → " + after.ToString("F2")
                                  + "   降幅 " + drop.ToString("F2") + "  ← **白线来自它**");
                }
            }

            sb.AppendLine();
            sb.AppendLine("── 全部候选的降幅排序（降幅大的在前）──");
            results.Sort((a, b) => b.Value.CompareTo(a.Value));
            for (int i = 0; i < results.Count && i < 20; i++)
                sb.AppendLine("  " + results[i].Value.ToString("F2").PadLeft(7) + "   " + results[i].Key);

            sb.AppendLine();
            sb.AppendLine("提示：降幅接近 0 的就是无关物体；");
            sb.AppendLine("      降幅 ≥ 3 的那个物体名，就是白线的真正来源。");

            Debug.Log("[P13][白线探针]\n" + sb.ToString());
            EditorGUIUtility.systemCopyBuffer = sb.ToString();   // 顺手复制，方便直接粘贴回传
            Debug.Log("[P13][白线探针] 结果已复制到剪贴板。");
        }

        /// <summary>
        /// 找出白带所在屏幕行，并返回该行相对上下邻域的亮度增量。
        /// </summary>
        private static bool MeasureBand(out int bandRow, out float delta)
        {
            bandRow = -1;
            delta = 0f;

            Camera cam = Camera.main;
            if (cam == null)
            {
                // 找不到 MainCamera 时退一步：用任意启用的相机
                Camera[] cams = Object.FindObjectsOfType<Camera>();
                for (int i = 0; i < cams.Length; i++)
                {
                    if (cams[i] != null && cams[i].isActiveAndEnabled) { cam = cams[i]; break; }
                }
            }
            if (cam == null) return false;

            const int W = 320;
            const int H = 200;
            var rt = RenderTexture.GetTemporary(W, H, 24);
            RenderTexture prev = RenderTexture.active;

            var tex = new Texture2D(W, H, TextureFormat.RGB24, false);
            try
            {
                cam.targetTexture = rt;              // 只截这个相机，避免把编辑器 UI 拍进去
                cam.Render();
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
                tex.Apply();
            }
            finally
            {
                cam.targetTexture = null;
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
            }

            Color32[] px = tex.GetPixels32();
            Object.DestroyImmediate(tex);

            // 逐行求平均亮度（只用中间 60% 宽度，避开两侧 UI 面板）
            var rowLum = new float[H];
            int x0 = Mathf.RoundToInt(W * 0.20f);
            int x1 = Mathf.RoundToInt(W * 0.80f);
            for (int y = 0; y < H; y++)
            {
                float s = 0f;
                for (int x = x0; x < x1; x++)
                {
                    Color32 c = px[y * W + x];
                    s += (c.r + c.g + c.b) / 3f;
                }
                rowLum[y] = s / Mathf.Max(1, x1 - x0);
            }

            // 找"比上下各 6 行均值明显更亮"的行 —— 这就是白带
            const int GAP = 6;
            float best = -1f;
            int bestY = -1;
            for (int y = GAP; y < H - GAP; y++)
            {
                float nb = 0f;
                for (int k = 1; k <= GAP; k++)
                {
                    nb += rowLum[y - k] + rowLum[y + k];
                }
                nb /= (GAP * 2f);

                float d = rowLum[y] - nb;
                if (d > best) { best = d; bestY = y; }
            }

            bandRow = bestY;
            delta = best;
            return bestY >= 0;
        }

        /// <summary>
        /// 收集所有"可能画出一条横贯白线"的物体。
        ///
        /// 判据从宽：宁可多测几十个，也不要漏掉真凶（每个只多花一次截屏）。
        ///   · 天气系统的所有子物体（雪盘/云穹顶/星穹顶/积水/粒子/树雪）
        ///   · 所有渲染队列 ≥ 2500 的透明渲染器
        ///   · 所有用了 ZTest Always 的材质所属物体
        ///   · 名字里带 Dome / Sky / Fog / Ground / Canvas 的
        /// </summary>
        private static List<GameObject> CollectCandidates()
        {
            var list = new List<GameObject>();
            var seen = new HashSet<int>();

            System.Action<GameObject> add = go =>
            {
                if (go == null) return;
                int id = go.GetInstanceID();
                if (seen.Contains(id)) return;
                seen.Add(id);
                list.Add(go);
            };

            Renderer[] all = Object.FindObjectsOfType<Renderer>();
            for (int i = 0; i < all.Length; i++)
            {
                Renderer r = all[i];
                if (r == null || !r.gameObject.activeInHierarchy) continue;

                Material m = r.sharedMaterial;
                int q = (m != null) ? m.renderQueue : -1;
                bool always = false;
                if (m != null && m.HasProperty("_ZTest"))
                    // ⚠️【P13 编译错误 CS0103 修法】
                    // CompareFunction 在 UnityEngine.Rendering 命名空间里，
                    // 本文件原来没写 using。这里用**完全限定名**而不是补 using ——
                    // UnityEngine.Rendering 里有 GraphicsSettings / ShaderTagId 等
                    // 与 UnityEditor 侧同名的类型，补 using 有引入新歧义的风险，
                    // 而这里只需要一个枚举值，完全限定最省事也最安全。
                    always = m.GetFloat("_ZTest")
                             == (float)UnityEngine.Rendering.CompareFunction.Always;

                string n = r.gameObject.name.ToLowerInvariant();
                bool nameHit = n.Contains("dome") || n.Contains("sky") || n.Contains("fog")
                            || n.Contains("ground") || n.Contains("canvas") || n.Contains("sliver")
                            || n.Contains("line") || n.Contains("plane") || n.Contains("snow")
                            || n.Contains("wet");

                if (q >= 2500 || always || nameHit)
                    add(r.gameObject);
            }

            // 天气根物体上的粒子系统也一并试（粒子渲染器常有大 bounds）
            ParticleSystem[] ps = Object.FindObjectsOfType<ParticleSystem>();
            for (int i = 0; i < ps.Length; i++)
            {
                if (ps[i] != null && ps[i].gameObject.activeInHierarchy)
                    add(ps[i].gameObject);
            }

            return list;
        }
    }
}
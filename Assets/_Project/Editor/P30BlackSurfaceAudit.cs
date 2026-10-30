#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace SchoolTwin.EditorTools
{
    /// <summary>
    /// 【P30】场景发黑构件检测（**只读**，不改任何东西）。
    ///
    /// ── 判据（v2：加「局部黑」）────────────────────────────────────
    /// 把每张光照图集降采样到 256×256 读进内存，按网格 UV2 逐点采样，得到两个数：
    ///   · `avg`        采样点平均亮度
    ///   · `blackRatio` 亮度 &lt; 0.10 的采样点**占比**
    ///
    /// 判为「真·全黑」的条件：**`avg &lt; 0.10` 或 `blackRatio ≥ 0.50`**
    ///
    /// 【为什么 v1 只看 avg 会漏】一块大网格常常同时含"亮面"和"黑面"：
    ///   · `Floor2` 共享楼板 —— 上表面（三楼地面）有光、下表面（二楼天花板）全黑，平均被拉高；
    ///   · 房间墙体 —— 一面墙的多个房间共用一个 mesh，个别朝向全黑但平均看不出来。
    /// v1 就是这样漏掉了 Floor2。v2 用 blackRatio 抓「局部全黑」。
    ///
    /// ── 为什么能读不可读的图集 ────────────────────────────────────
    /// Lightmap 图集的 `isReadable` 通常是 false，`GetPixels` 会抛异常。
    /// 这里走 `Graphics.Blit` 到临时 RenderTexture 再 `ReadPixels`，绕开这个限制。
    ///
    /// ── 输出 ──────────────────────────────────────────────────────
    /// · Console + 文件 `Docs/QA/evidence/P30_rescan_<时间戳>.txt`
    /// · 三段：总账 / **按房间分组（每房间最黑 8 个）** / 全黑明细
    ///
    /// 用完即可删，不进运行时。
    /// </summary>
    public static class P30BlackSurfaceAudit
    {
        private const float BlackThreshold = 0.10f;
        private const float DimThreshold = 0.25f;

        /// <summary>黑点占比达到这个值也判为全黑（抓"局部整片黑"的大网格）。</summary>
        private const float BlackRatioThreshold = 0.50f;

        private const int SampleSize = 256;
        private const int MaxSamplesPerMesh = 512;
        private const int GroupTopN = 8;

        private const string EvidenceDir = "Docs/QA/evidence";

        // ================================================================ 入口

        [MenuItem("归档/P30 发黑检测（只读，不改场景）")]
        public static void Run()
        {
            StringBuilder sb = new StringBuilder(32768);
            sb.AppendLine("===== P30 发黑构件检测 v2（只读）=====");
            sb.AppendLine("时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("判据：avg < " + BlackThreshold.ToString("0.00") +
                          "  **或** 黑点占比 ≥ " + (BlackRatioThreshold * 100f).ToString("0") + "%");
            sb.AppendLine();

            Dictionary<int, Color[]> atlasPixels = BuildAtlasCache(sb);

            Renderer[] all = UnityEngine.Object.FindObjectsOfType<Renderer>(true);
            sb.AppendLine("场景 Renderer 总数：" + all.Length);
            sb.AppendLine();

            List<Item> hits = new List<Item>(256);   // 命中（avg 黑 或 局部黑）
            List<Item> dim = new List<Item>(512);
            int normal = 0, noLightmap = 0, noUv2 = 0;

            for (int i = 0; i < all.Length; i++)
            {
                Renderer r = all[i];
                if (r == null) continue;

                if (r.lightmapIndex < 0 || !atlasPixels.ContainsKey(r.lightmapIndex))
                {
                    noLightmap++;
                    continue;
                }

                float avg, ratio;
                int samples;
                if (!SampleLuminance(r, atlasPixels, out avg, out ratio, out samples))
                {
                    noUv2++;
                    continue;
                }

                Item item = new Item
                {
                    Renderer = r,
                    Avg = avg,
                    BlackRatio = ratio,
                    Samples = samples,
                    Path = BuildPath(r.transform),
                    Material = r.sharedMaterial != null ? r.sharedMaterial.name : "(无材质)",
                    AtlasIndex = r.lightmapIndex,
                    Category = GuessCategory(r),
                };

                bool isBlack = avg < BlackThreshold || ratio >= BlackRatioThreshold;
                if (isBlack) hits.Add(item);
                else if (avg < DimThreshold) dim.Add(item);
                else normal++;
            }

            // ---- 总账 ----
            sb.AppendLine("---------- 总账 ----------");
            sb.AppendLine("真·全黑（avg < " + BlackThreshold.ToString("0.00") +
                          " 或 黑点占比 ≥ " + (BlackRatioThreshold * 100f).ToString("0") + "%）：" + hits.Count);
            sb.AppendLine("偏暗（" + BlackThreshold.ToString("0.00") + " ~ " + DimThreshold.ToString("0.00") + "）：" + dim.Count);
            sb.AppendLine("正常：" + normal);
            sb.AppendLine("无烘焙 lightmap：" + noLightmap);
            sb.AppendLine("无 UV2：" + noUv2);
            sb.AppendLine();

            // ---- 按类别 ----
            sb.AppendLine("---------- 命中按类别 ----------");
            Dictionary<string, int> byCat = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < hits.Count; i++)
            {
                int n;
                byCat.TryGetValue(hits[i].Category, out n);
                byCat[hits[i].Category] = n + 1;
            }
            foreach (KeyValuePair<string, int> kv in SortedByCount(byCat))
                sb.AppendLine("  " + kv.Key.PadRight(12) + kv.Value + " 处");
            sb.AppendLine();

            // ---- 按房间分组（这一节是给"按房间找黑"用的）----
            sb.AppendLine("---------- 按房间分组（每房间最黑 " + GroupTopN + " 个）----------");
            AppendGroupedByRoom(sb, hits);

            // ---- 全黑明细 ----
            hits.Sort((a, b) => b.BlackRatio.CompareTo(a.BlackRatio));

            sb.AppendLine("---------- 明细（" + hits.Count + " 条，按黑点占比降序）----------");
            sb.AppendLine("序号 | 平均 | 黑点占比 | 采样 | 类别 | 图集 | 材质 | 路径");
            for (int i = 0; i < hits.Count; i++)
            {
                Item it = hits[i];
                sb.AppendLine((i + 1).ToString().PadLeft(3) + " | " +
                              it.Avg.ToString("0.0000") + " | " +
                              (it.BlackRatio * 100f).ToString("0").PadLeft(3) + "% | " +
                              it.Samples.ToString().PadLeft(4) + " | " +
                              it.Category.PadRight(10) + " | " +
                              it.AtlasIndex.ToString().PadLeft(2) + " | " +
                              it.Material.PadRight(24) + " | " +
                              it.Path);
            }

            sb.AppendLine();
            sb.AppendLine("===== 结束 =====");

            string text = sb.ToString();
            Debug.Log(text);
            SaveToFile(text);
        }

        // ================================================================ 房间分组

        /// <summary>
        /// 按「场景名前 3~4 段」分组（如 `SchoolScene/Interior/SportRoom1`），
        /// 每组建出最黑的 N 个。用户描述"篮球场/餐厅/走廊"时可以直接对号入座。
        /// </summary>
        private static void AppendGroupedByRoom(StringBuilder sb, List<Item> hits)
        {
            Dictionary<string, List<Item>> groups =
                new Dictionary<string, List<Item>>(StringComparer.Ordinal);

            for (int i = 0; i < hits.Count; i++)
            {
                string room = RoomOf(hits[i].Path);
                List<Item> list;
                if (!groups.TryGetValue(room, out list))
                {
                    list = new List<Item>(16);
                    groups[room] = list;
                }
                list.Add(hits[i]);
            }

            // 组内有货最多的排前面
            List<KeyValuePair<string, List<Item>>> ordered =
                new List<KeyValuePair<string, List<Item>>>(groups);
            ordered.Sort((a, b) => b.Value.Count.CompareTo(a.Value.Count));

            for (int g = 0; g < ordered.Count; g++)
            {
                string room = ordered[g].Key;
                List<Item> list = ordered[g].Value;

                list.Sort((a, b) => b.BlackRatio.CompareTo(a.BlackRatio));

                sb.AppendLine("[" + room + "]  " + list.Count + " 处");

                int n = Mathf.Min(GroupTopN, list.Count);
                for (int i = 0; i < n; i++)
                {
                    Item it = list[i];
                    sb.AppendLine("   " + (it.BlackRatio * 100f).ToString("0").PadLeft(3) + "%  " +
                                  it.Avg.ToString("0.0000") + "  " +
                                  it.Category.PadRight(8) + "  " +
                                  it.Material.PadRight(22) + "  " + it.Path);
                }
                if (list.Count > n) sb.AppendLine("   … 其余 " + (list.Count - n) + " 处略");
                sb.AppendLine();
            }
        }

        /// <summary>取路径前 3 段做房间名：`SchoolScene/Interior/SportRoom1/xxx/yyy` → `Interior/SportRoom1`。</summary>
        private static string RoomOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return "(未知)";

            string[] p = path.Split('/');
            if (p.Length <= 2) return path;
            if (p.Length == 3) return p[1] + "/" + p[2];
            return p[1] + "/" + p[2];
        }

        // ================================================================ 图集

        private static Dictionary<int, Color[]> BuildAtlasCache(StringBuilder sb)
        {
            Dictionary<int, Color[]> cache = new Dictionary<int, Color[]>(32);
            LightmapData[] maps = LightmapSettings.lightmaps;

            sb.AppendLine("---------- 光照图集 ----------");
            sb.AppendLine("lightmaps 数量：" + (maps != null ? maps.Length : 0));
            if (maps == null) return cache;

            for (int i = 0; i < maps.Length; i++)
            {
                LightmapData d = maps[i];
                if (d == null || d.lightmapColor == null) continue;

                Color[] px = ReadDownsampled(d.lightmapColor);
                if (px == null) continue;

                cache[i] = px;

                float avg = 0f;
                for (int k = 0; k < px.Length; k++)
                    avg += 0.299f * px[k].r + 0.587f * px[k].g + 0.114f * px[k].b;
                avg /= px.Length;

                sb.AppendLine("  图集 " + i.ToString().PadLeft(2) + "：" + d.lightmapColor.name +
                              "  均值 " + avg.ToString("0.0000"));
            }

            sb.AppendLine();
            return cache;
        }

        private static Color[] ReadDownsampled(Texture2D src)
        {
            RenderTexture rt = null;
            RenderTexture prev = RenderTexture.active;

            try
            {
                rt = RenderTexture.GetTemporary(SampleSize, SampleSize, 0,
                    RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                Graphics.Blit(src, rt);
                RenderTexture.active = rt;

                Texture2D readable = new Texture2D(SampleSize, SampleSize, TextureFormat.RGBA32, false);
                readable.ReadPixels(new Rect(0, 0, SampleSize, SampleSize), 0, 0);
                readable.Apply();

                Color[] px = readable.GetPixels();
                UnityEngine.Object.DestroyImmediate(readable);
                return px;
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
            }
        }

        // ================================================================ 采样

        /// <summary>
        /// 按网格 UV2 在图集上逐点采样。
        /// 返回 avg（平均亮度）与 ratio（亮度 &lt; <see cref="BlackThreshold"/> 的点占比）。
        /// </summary>
        private static bool SampleLuminance(Renderer r, Dictionary<int, Color[]> atlas,
                                            out float avg, out float ratio, out int samples)
        {
            avg = 0f;
            ratio = 0f;
            samples = 0;

            MeshFilter mf = r.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) return false;

            Mesh mesh = mf.sharedMesh;
            Vector2[] uv2 = mesh.uv2;
            if (uv2 == null || uv2.Length == 0) return false;

            Color[] px = atlas[r.lightmapIndex];

            int stride = Mathf.Max(1, uv2.Length / MaxSamplesPerMesh);
            float sum = 0f;
            int black = 0;

            for (int i = 0; i < uv2.Length; i += stride)
            {
                Vector2 uv = uv2[i];
                if (uv.x < 0f || uv.x > 1f || uv.y < 0f || uv.y > 1f) continue;

                int x = Mathf.Clamp((int)(uv.x * SampleSize), 0, SampleSize - 1);
                int y = Mathf.Clamp((int)(uv.y * SampleSize), 0, SampleSize - 1);

                Color c = px[y * SampleSize + x];
                float lum = 0.299f * c.r + 0.587f * c.g + 0.114f * c.b;

                sum += lum;
                if (lum < BlackThreshold) black++;
                samples++;
            }

            if (samples == 0) return false;

            avg = sum / samples;
            ratio = black / (float)samples;
            return true;
        }

        // ================================================================ 辅助

        private static string GuessCategory(Renderer r)
        {
            string n = r.gameObject.name;

            if (n.IndexOf("Top", StringComparison.OrdinalIgnoreCase) >= 0) return "吊顶";
            if (n.IndexOf("Wall", StringComparison.OrdinalIgnoreCase) >= 0) return "墙";
            if (n.IndexOf("Door", StringComparison.OrdinalIgnoreCase) >= 0) return "门";
            if (n.IndexOf("Light", StringComparison.OrdinalIgnoreCase) >= 0) return "灯";
            if (n.IndexOf("Border", StringComparison.OrdinalIgnoreCase) >= 0) return "边条";
            if (n.IndexOf("Glass", StringComparison.OrdinalIgnoreCase) >= 0) return "玻璃";
            if (n.IndexOf("Lamp", StringComparison.OrdinalIgnoreCase) >= 0) return "路灯";
            if (n.IndexOf("Label", StringComparison.OrdinalIgnoreCase) >= 0) return "UI文字";

            return "其他";
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
                string path = Path.Combine(dir, "P30_rescan_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt");
                File.WriteAllText(path, text);
                Debug.Log("[P30] 完整清单已写入 " + path);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[P30] 清单落盘失败（不影响 Console 输出）：" + e.Message);
            }
        }

        private struct Item
        {
            public Renderer Renderer;
            public float Avg;
            public float BlackRatio;
            public int Samples;
            public string Path;
            public string Material;
            public string Category;
            public int AtlasIndex;
        }
    }
}
#endif

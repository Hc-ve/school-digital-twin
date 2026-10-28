using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using SchoolTwin.Twin;

namespace SchoolTwin.EditorTools
{
    /// <summary>
    /// 烘焙小地图底图：从正上方拍三张（室外总览 / 一层 / 二层），存成 PNG，
    /// 并把「每张图覆盖的世界矩形」写进 `TwinMapAtlas`。
    ///
    /// ── 为什么是烘焙，不是实时渲染 ────────────────────────────────
    /// 场景有 2000+ 渲染器，挂第二个正交相机等于每帧把整个场景画两遍。地图是静态的，拍一次就够。
    ///
    /// ── 怎么做到「只拍一层」─────────────────────────────────────
    /// 相机在上方朝下看，用 near/far 卡出一个水平薄片：
    ///   near = 相机高度 − 该层天花高度   （天花以上不拍 → 屋顶/上层自然消失）
    ///   far  = 相机高度 − 该层地面高度
    /// 一层 y∈[−0.6, 3.45]；二层 y∈[3.2, 7.0]；室外 y∈[−6, 12]（含屋顶）。
    ///
    /// ── 为什么要「按像素自动裁边」────────────────────────────────
    /// 拍摄范围是靠「房间矩形 + 邻近物件包围盒」估出来的，估大了四周就是一大圈空背景，
    /// 地图主体被压小（2026-10-23 实测：内容只占中间一半）。
    /// 所以渲染完**扫像素找真实内容边界**，三张图用**同一个**裁剪框（否则切楼层会跳），
    /// 再把裁剪后的像素范围换算回世界矩形写进图集。
    ///
    /// 用法：菜单 `P21 地图 → ① 烘焙三张底图`。**不要在 Play 模式下跑。**
    /// </summary>
    public static class TwinMapBaker
    {
        private const string ScenePath = "Assets/TirGames/SchoolScene/Scenes/SchoolSceneDay.unity";
        private const string OutDir = "Assets/_Project/Map";
        private const string AtlasPath = "Assets/_Project/Resources/TwinMapAtlas.asset";

        /// <summary>底图宽度（像素）。高度按世界矩形的长宽比算，不会变形。</summary>
        private const int TexWidth = 2048;

        /// <summary>裁边时四周留的像素，免得内容贴边。</summary>
        private const int CropPadding = 12;

        /// <summary>判断「这是背景色」的容差（0~1，按每通道）。</summary>
        private const float ClearTolerance = 0.02f;

        // 各层的水平切片（世界 y）
        private const float F1Bottom = -0.6f, F1Top = 3.45f;
        private const float F2Bottom = 3.20f, F2Top = 7.00f;
        private const float OutBottom = -6.0f, OutTop = 12.0f;

        private static readonly Color ClearColor = new Color(0.90f, 0.89f, 0.86f, 1f);

        private sealed class Raw
        {
            public string Key;
            public int Floor;
            public Texture2D Tex;
            public float YBottom, YTop;
        }

        [MenuItem("归档/P21 地图/① 烘焙三张底图（室外 / 1F / 2F）", false, 10)]
        public static void BakeMenu()
        {
            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorUtility.DisplayDialog("已拒绝",
                    "现在是 Play 模式。烘焙会改资产与渲染设置，请先按停止。", "好");
                return;
            }

            string report = Bake();
            Debug.Log(report);
            EditorUtility.DisplayDialog("P21 烘焙完成",
                "结果已打印到 Console，并写入 p21_bake.txt。\n\n" +
                "底图在 Assets/_Project/Map/，图集在 Assets/_Project/Resources/TwinMapAtlas.asset。", "好");
        }

        public static string Bake()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (string.IsNullOrEmpty(scene.path) || scene.path != ScenePath)
                scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[P21Bake] ===== 小地图底图烘焙 =====");

            Rect rect;
            if (!ComputeWorldRect(out rect))
            {
                sb.AppendLine("[P21Bake] ✘ 算不出世界范围（房间表为空？），中止。");
                Write(sb.ToString());
                return sb.ToString();
            }
            sb.AppendLine("[P21Bake] 拍摄范围：x[" + rect.xMin.ToString("0.0") + ", " + rect.xMax.ToString("0.0") +
                          "]  z[" + rect.yMin.ToString("0.0") + ", " + rect.yMax.ToString("0.0") +
                          "]  宽 " + rect.width.ToString("0.0") + " × 深 " + rect.height.ToString("0.0") + " m");

            int texH = Mathf.Max(64, Mathf.RoundToInt(TexWidth * rect.height / Mathf.Max(0.001f, rect.width)));
            sb.AppendLine("[P21Bake] 原始画幅：" + TexWidth + " × " + texH + "（按长宽比算，不变形）");

            if (!AssetDatabase.IsValidFolder(OutDir)) AssetDatabase.CreateFolder("Assets/_Project", "Map");

            GameObject go = new GameObject("[MapBaker]");
            Camera cam = go.AddComponent<Camera>();
            GameObject lightGo = null;

            Color oldAmbient = RenderSettings.ambientLight;
            float oldAmbientIntensity = RenderSettings.ambientIntensity;
            UnityEngine.Rendering.AmbientMode oldMode = RenderSettings.ambientMode;
            bool oldFog = RenderSettings.fog;

            TwinMapAtlas atlas = LoadOrCreateAtlas(sb);
            List<Raw> raws = new List<Raw>(3);

            try
            {
                // 场景是烘焙光照，编辑器里临时相机拍出来会偏暗 → 临时加顶光 + 提亮环境光。
                // 只影响这三张图，拍完在 finally 里原样恢复。
                RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
                RenderSettings.ambientLight = new Color(0.55f, 0.55f, 0.55f);
                RenderSettings.ambientIntensity = 1f;
                RenderSettings.fog = false;

                lightGo = new GameObject("[MapBaker] Light");
                Light l = lightGo.AddComponent<Light>();
                l.type = LightType.Directional;
                l.intensity = 0.75f;
                l.color = Color.white;
                l.shadows = LightShadows.None;
                lightGo.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

                cam.orthographic = true;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = ClearColor;
                cam.cullingMask = ~0;
                cam.allowHDR = false;
                cam.allowMSAA = false;
                cam.orthographicSize = rect.height * 0.5f;
                cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                cam.transform.position = new Vector3(rect.center.x, 0f, rect.center.y);

                raws.Add(RenderRaw(cam, texH, TwinMapAtlas.KeyCampus, 0, 40f, OutBottom, OutTop));
                raws.Add(RenderRaw(cam, texH, TwinMapAtlas.KeyFloor1, 1, 12f, F1Bottom, F1Top));
                raws.Add(RenderRaw(cam, texH, TwinMapAtlas.KeyFloor2, 2, 12f, F2Bottom, F2Top));
            }
            catch (Exception e)
            {
                sb.AppendLine("[P21Bake] ✘ 烘焙异常：" + e.Message);
            }
            finally
            {
                if (lightGo != null) UnityEngine.Object.DestroyImmediate(lightGo);
                UnityEngine.Object.DestroyImmediate(go);

                RenderSettings.ambientMode = oldMode;
                RenderSettings.ambientLight = oldAmbient;
                RenderSettings.ambientIntensity = oldAmbientIntensity;
                RenderSettings.fog = oldFog;
            }

            // ---- 每张图**各自**裁剪 ----
            //
            // 【为什么不共用裁剪框】2026-10-23 实测：共用的话室内图也被裁成"整个校园"那么大，
            // 站在 2F 教室角落时圆里一半是楼外空白（用户："二层地图场景会变白"）。
            // 原神的室内图本来就是"只覆盖室内"的一张独立图 —— 每层各自裁到自己的内容边界才对。
            List<TwinMapAtlas.Entry> entries = new List<TwinMapAtlas.Entry>(3);
            for (int i = 0; i < raws.Count; i++)
            {
                Raw raw = raws[i];
                if (raw == null || raw.Tex == null) continue;

                int x0, x1, y0, y1;
                if (!ContentBox(raw.Tex, out x0, out x1, out y0, out y1))
                {
                    sb.AppendLine("[P21Bake] ⚠ " + raw.Key + " 整张都是背景色，跳过（相机朝向/裁剪平面可能不对）");
                    UnityEngine.Object.DestroyImmediate(raw.Tex);
                    continue;
                }

                x0 = Mathf.Max(0, x0 - CropPadding); y0 = Mathf.Max(0, y0 - CropPadding);
                x1 = Mathf.Min(TexWidth - 1, x1 + CropPadding); y1 = Mathf.Min(texH - 1, y1 + CropPadding);

                int cw = x1 - x0 + 1;
                int ch = y1 - y0 + 1;

                // 像素范围换算回世界矩形（纹理 y=0 在下方，对应世界 z 最小）
                Rect world = Rect.MinMaxRect(
                    rect.xMin + rect.width * x0 / TexWidth,
                    rect.yMin + rect.height * y0 / texH,
                    rect.xMin + rect.width * (x1 + 1) / TexWidth,
                    rect.yMin + rect.height * (y1 + 1) / texH);

                TwinMapAtlas.Entry e = SaveCropped(raw, x0, y0, cw, ch, world);
                if (e != null)
                {
                    entries.Add(e);
                    sb.AppendLine("[P21Bake] ✔ " + raw.Key + "  切片 y[" + raw.YBottom.ToString("0.00") + ", " +
                                  raw.YTop.ToString("0.00") + "]  裁剪 " + cw + "×" + ch +
                                  "（原画幅 " + (100f * cw / TexWidth).ToString("0") + "% 宽）" +
                                  "  世界 x[" + world.xMin.ToString("0.0") + ", " + world.xMax.ToString("0.0") +
                                  "] z[" + world.yMin.ToString("0.0") + ", " + world.yMax.ToString("0.0") + "]");
                }
                UnityEngine.Object.DestroyImmediate(raw.Tex);
            }

            if (entries.Count == 3 && atlas != null)
            {
                atlas.Entries = entries.ToArray();
                EditorUtility.SetDirty(atlas);
                AssetDatabase.SaveAssets();
                sb.AppendLine("[P21Bake] 图集已更新：" + AtlasPath);
            }
            else
            {
                sb.AppendLine("[P21Bake] ⚠ 只成功了 " + entries.Count + " 张，图集没有更新。");
            }

            sb.AppendLine();
            sb.AppendLine("[P21Bake] ===== 结束 =====");
            Write(sb.ToString());
            return sb.ToString();
        }

        // ================================================================ 渲染 / 裁剪 / 保存

        private static Raw RenderRaw(Camera cam, int texH, string key, int floor,
                                     float camY, float yBottom, float yTop)
        {
            RenderTexture rt = new RenderTexture(TexWidth, texH, 24, RenderTextureFormat.ARGB32);
            rt.antiAliasing = 1;
            rt.Create();

            Vector3 pos = cam.transform.position;
            pos.y = camY;
            cam.transform.position = pos;

            // near/far 卡出水平薄片：只拍 [yBottom, yTop] 这一层
            cam.nearClipPlane = Mathf.Max(0.05f, camY - yTop);
            cam.farClipPlane = camY - yBottom;

            RenderTexture old = RenderTexture.active;
            cam.targetTexture = rt;
            cam.Render();

            RenderTexture.active = rt;
            Texture2D tex = new Texture2D(TexWidth, texH, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, TexWidth, texH), 0, 0);
            tex.Apply(false, false);
            RenderTexture.active = old;

            cam.targetTexture = null;
            rt.Release();
            UnityEngine.Object.DestroyImmediate(rt);

            Raw raw = new Raw();
            raw.Key = key;
            raw.Floor = floor;
            raw.Tex = tex;
            raw.YBottom = yBottom;
            raw.YTop = yTop;
            return raw;
        }

        /// <summary>扫像素，找出「不是背景色」的包围盒（像素坐标，原点在左下）。</summary>
        private static bool ContentBox(Texture2D tex, out int x0, out int x1, out int y0, out int y1)
        {
            x0 = tex.width; x1 = -1; y0 = tex.height; y1 = -1;
            if (tex == null) return false;

            int w = tex.width, h = tex.height;

            // 【背景色用四角多数票确定】不能直接拿 `ClearColor` 比：
            // 工程是 Linear 色彩空间，相机渲染到 RT 再 ReadPixels 出来的数值可能被做过 sRGB 编码，
            // 和代码里的 Color 不是同一套数值 —— 直接比会「一个像素都不匹配」，裁边等于没做。
            // 四角如果基本一致，就以它们为背景色；不一致（内容顶到角上）才退回 ClearColor。
            Color bg = ClearColor;
            Color c00 = tex.GetPixel(1, 1);
            Color c10 = tex.GetPixel(w - 2, 1);
            Color c01 = tex.GetPixel(1, h - 2);
            Color c11 = tex.GetPixel(w - 2, h - 2);
            if (Near(c00, c10) && Near(c00, c01) && Near(c00, c11)) bg = c00;

            Color[] px = tex.GetPixels();
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    Color c = px[row + x];
                    if (Mathf.Abs(c.r - bg.r) < ClearTolerance &&
                        Mathf.Abs(c.g - bg.g) < ClearTolerance &&
                        Mathf.Abs(c.b - bg.b) < ClearTolerance) continue;

                    if (x < x0) x0 = x;
                    if (x > x1) x1 = x;
                    if (y < y0) y0 = y;
                    if (y > y1) y1 = y;
                }
            }
            return x1 >= x0 && y1 >= y0;
        }

        private static bool Near(Color a, Color b)
        {
            return Mathf.Abs(a.r - b.r) < ClearTolerance * 4f &&
                   Mathf.Abs(a.g - b.g) < ClearTolerance * 4f &&
                   Mathf.Abs(a.b - b.b) < ClearTolerance * 4f;
        }

        /// <summary>图外（没有几何体）的填充色：深灰蓝，读起来像"墙外/未开放"，不像白纸。</summary>
        private static readonly Color VoidColor = new Color(0.16f, 0.17f, 0.19f, 1f);

        private static TwinMapAtlas.Entry SaveCropped(Raw raw, int x0, int y0, int cw, int ch, Rect world)
        {
            Color[] px = raw.Tex.GetPixels(x0, y0, cw, ch);

            // 把「没有几何体」的像素换成深色 —— 原神里地图边界外是压暗的，
            // 白花花的背景看着像"地图没加载完"。
            Color bg = raw.Tex.GetPixel(1, 1);
            for (int i = 0; i < px.Length; i++)
            {
                Color c = px[i];
                if (Mathf.Abs(c.r - bg.r) < ClearTolerance &&
                    Mathf.Abs(c.g - bg.g) < ClearTolerance &&
                    Mathf.Abs(c.b - bg.b) < ClearTolerance)
                {
                    px[i] = VoidColor;
                }
            }

            Texture2D cropped = new Texture2D(cw, ch, TextureFormat.RGB24, false);
            cropped.SetPixels(px);
            cropped.Apply(false, false);

            byte[] png = cropped.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(cropped);

            string assetPath = OutDir + "/TwinMap_" + raw.Key + ".png";
            File.WriteAllBytes(assetPath, png);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);

            TextureImporter imp = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (imp != null)
            {
                imp.textureType = TextureImporterType.Default;
                imp.mipmapEnabled = false;
                imp.wrapMode = TextureWrapMode.Clamp;
                imp.filterMode = FilterMode.Bilinear;
                imp.maxTextureSize = 2048;
                imp.textureCompression = TextureImporterCompression.Uncompressed;
                imp.sRGBTexture = true;
                imp.SaveAndReimport();
            }

            Texture2D saved = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
            if (saved == null) return null;

            TwinMapAtlas.Entry e = new TwinMapAtlas.Entry();
            e.Key = raw.Key;
            e.Floor = raw.Floor;
            e.Texture = saved;
            e.WorldMin = new Vector2(world.xMin, world.yMin);
            e.WorldMax = new Vector2(world.xMax, world.yMax);
            e.BakedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            return e;
        }

        // ================================================================ 工具

        /// <summary>
        /// 拍摄范围 = 所有已登记分区矩形的并集 + 50m 留白（确定性，不扫场景物件）。
        ///
        /// 用分区表而不是「所有渲染器包围盒」：后者会把远景楼（x ≈ −93）也框进来。
        /// 范围给大一点没有代价 —— 每张图后面都会**按像素各自裁边**，多余的部分会被裁掉。
        /// 反而"扫物件动态扩范围"踩过两次雪球（2061 个物件、345×345m），所以彻底不用了。
        /// </summary>
        private static bool ComputeWorldRect(out Rect rect)
        {
            float minX = float.MaxValue, maxX = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;
            int n = 0;

            IReadOnlyList<TwinRoomInfo> rooms = TwinSiteLayout.Rooms;
            for (int i = 0; i < rooms.Count; i++)
            {
                float x0, x1, z0, z1;
                if (!TwinRoomBounds.TryGetBounds(rooms[i].Zone, out x0, out x1, out z0, out z1)) continue;
                minX = Mathf.Min(minX, x0); maxX = Mathf.Max(maxX, x1);
                minZ = Mathf.Min(minZ, z0); maxZ = Mathf.Max(maxZ, z1);
                n++;
            }

            if (n == 0) { rect = new Rect(); return false; }

            // 【只按「房间并集 + 固定留白」算，不再去扫场景物件】
            //
            // 2026-10-23 两次踩坑都出在"扫物件"上：
            //   · 第一版边收边扩 → 雪球滚成 2061 个物件、范围 345×345m；
            //   · 第二版固定参照 + 32m → 校园太密，仍然收进 1960 个，又触发兜底。
            // 而**每张图现在会按像素各自裁边**，拍摄范围大一点完全没有代价（只是分辨率摊薄）。
            // 所以这里保持确定性：房间并集 + 50m，足够盖住校园那圈树。
            const float margin = 50f;
            minX -= margin; maxX += margin;
            minZ -= margin; maxZ += margin;
            rect = Rect.MinMaxRect(minX, minZ, maxX, maxZ);

            Debug.Log("[P21Bake] 拍摄范围 = 房间矩形并集 + " + margin + "m 留白（每张图后面会各自裁边）");
            return true;
        }

        private static TwinMapAtlas LoadOrCreateAtlas(StringBuilder sb)
        {
            TwinMapAtlas atlas = AssetDatabase.LoadAssetAtPath<TwinMapAtlas>(AtlasPath);
            if (atlas != null) return atlas;

            string dir = Path.GetDirectoryName(AtlasPath).Replace('\\', '/');
            if (!AssetDatabase.IsValidFolder(dir))
            {
                sb.AppendLine("[P21Bake] ✘ 目录不存在：" + dir);
                return null;
            }

            atlas = ScriptableObject.CreateInstance<TwinMapAtlas>();
            AssetDatabase.CreateAsset(atlas, AtlasPath);
            AssetDatabase.SaveAssets();
            sb.AppendLine("[P21Bake] 新建图集：" + AtlasPath);
            return atlas;
        }

        private static void Write(string text)
        {
            try { File.WriteAllText("p21_bake.txt", text, Encoding.UTF8); }
            catch (Exception e) { Debug.LogWarning("[P21Bake] 写文件失败：" + e.Message); }
        }
    }
}

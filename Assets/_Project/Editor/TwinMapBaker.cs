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
    /// 场景有 2000+ 渲染器，挂第二个正交相机等于每帧把整个场景画两遍。
    /// 地图是静态的，拍一次就够。
    ///
    /// ── 怎么做到「只拍一层」─────────────────────────────────────
    /// 相机在上方朝下看，用 near/far 卡出一个水平薄片：
    ///   near = 相机高度 − 该层天花高度      （天花以上不拍 → 屋顶/上层自然消失）
    ///   far  = 相机高度 − 该层地面高度      （地面以下不拍）
    /// 一层：y ∈ [−0.6, 3.45]；二层：y ∈ [3.2, 7.0]；室外：y ∈ [−2, 12]（含屋顶）。
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

        /// <summary>四周留白（米）。留一点，免得建筑贴边。</summary>
        private const float Margin = 8f;

        // 各层的水平切片（世界 y）
        private const float F1Bottom = -0.6f, F1Top = 3.45f;
        private const float F2Bottom = 3.20f, F2Top = 7.00f;
        private const float OutBottom = -2.0f, OutTop = 12.0f;

        [MenuItem("P21 地图/① 烘焙三张底图（室外 / 1F / 2F）", false, 10)]
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
                "底图在 Assets/_Project/Map/，图集在 Assets/_Project/Resources/TwinMapAtlas.asset（运行时 Resources.Load 取）。", "好");
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
            sb.AppendLine("[P21Bake] 世界范围：x[" + rect.xMin.ToString("0.0") + ", " + rect.xMax.ToString("0.0") +
                          "]  z[" + rect.yMin.ToString("0.0") + ", " + rect.yMax.ToString("0.0") +
                          "]  宽 " + rect.width.ToString("0.0") + " × 深 " + rect.height.ToString("0.0") + " m");

            int texH = Mathf.Max(64, Mathf.RoundToInt(TexWidth * rect.height / Mathf.Max(0.001f, rect.width)));
            sb.AppendLine("[P21Bake] 底图尺寸：" + TexWidth + " × " + texH + "（按长宽比算，不变形）");

            if (!AssetDatabase.IsValidFolder(OutDir))
            {
                AssetDatabase.CreateFolder("Assets/_Project", "Map");
            }

            // ---- 临时相机 + 临时补光（拍完原样恢复）----
            GameObject go = new GameObject("[MapBaker]");
            Camera cam = go.AddComponent<Camera>();
            GameObject lightGo = null;

            Color oldAmbient = RenderSettings.ambientLight;
            float oldAmbientIntensity = RenderSettings.ambientIntensity;
            UnityEngine.Rendering.AmbientMode oldMode = RenderSettings.ambientMode;
            bool oldFog = RenderSettings.fog;

            TwinMapAtlas atlas = LoadOrCreateAtlas(sb);
            List<TwinMapAtlas.Entry> entries = new List<TwinMapAtlas.Entry>(3);

            try
            {
                // 场景是烘焙光照，编辑器里临时相机拍出来可能偏暗 → 临时加一盏顶光 + 提亮环境光。
                // 只影响这三张图，拍完立刻恢复（finally 里）。
                RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
                RenderSettings.ambientLight = new Color(0.62f, 0.62f, 0.62f);
                RenderSettings.ambientIntensity = 1f;
                RenderSettings.fog = false;

                lightGo = new GameObject("[MapBaker] Light");
                Light l = lightGo.AddComponent<Light>();
                l.type = LightType.Directional;
                l.intensity = 0.85f;
                l.color = Color.white;
                l.shadows = LightShadows.None;
                lightGo.transform.rotation = Quaternion.Euler(90f, 0f, 0f);   // 正上方朝下

                cam.orthographic = true;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.90f, 0.89f, 0.86f, 1f);
                cam.cullingMask = ~0;
                cam.allowHDR = false;
                cam.allowMSAA = false;
                cam.orthographicSize = rect.height * 0.5f;
                cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                cam.transform.position = new Vector3(rect.center.x, 0f, rect.center.y);

                entries.Add(BakeOne(cam, rect, texH, TwinMapAtlas.KeyCampus, 0, 40f, OutBottom, OutTop, sb));
                entries.Add(BakeOne(cam, rect, texH, TwinMapAtlas.KeyFloor1, 1, 12f, F1Bottom, F1Top, sb));
                entries.Add(BakeOne(cam, rect, texH, TwinMapAtlas.KeyFloor2, 2, 12f, F2Bottom, F2Top, sb));
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

            // ---- 写图集 ----
            if (entries.Count == 3 && atlas != null)
            {
                atlas.Entries = entries.ToArray();
                EditorUtility.SetDirty(atlas);
                AssetDatabase.SaveAssets();
                sb.AppendLine("[P21Bake] 图集已更新：" + AtlasPath);
            }

            sb.AppendLine();
            sb.AppendLine("[P21Bake] ===== 结束 =====");
            Write(sb.ToString());
            return sb.ToString();
        }

        // ================================================================ 单张

        private static TwinMapAtlas.Entry BakeOne(Camera cam, Rect rect, int texH,
                                                  string key, int floor, float camY,
                                                  float yBottom, float yTop, StringBuilder sb)
        {
            RenderTexture rt = new RenderTexture(TexWidth, texH, 24, RenderTextureFormat.ARGB32);
            rt.antiAliasing = 1;
            rt.Create();

            Vector3 pos = cam.transform.position;
            pos.y = camY;
            cam.transform.position = pos;

            // 关键：near/far 卡出水平薄片，只拍 [yBottom, yTop] 这一层
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

            byte[] png = tex.EncodeToPNG();
            string fileName = "TwinMap_" + key + ".png";
            string assetPath = OutDir + "/" + fileName;
            File.WriteAllBytes(assetPath, png);
            UnityEngine.Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);

            // 贴图导入设置：不压缩、不生成 mipmap（地图要清晰，且只在 UI 上按原尺寸显示）
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
            if (saved == null)
            {
                sb.AppendLine("[P21Bake] ✘ " + key + "：PNG 写入后读不回来（" + assetPath + "）");
                return null;
            }

            TwinMapAtlas.Entry e = new TwinMapAtlas.Entry();
            e.Key = key;
            e.Floor = floor;
            e.Texture = saved;
            e.WorldMin = new Vector2(rect.xMin, rect.yMin);
            e.WorldMax = new Vector2(rect.xMax, rect.yMax);
            e.BakedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");

            sb.AppendLine("[P21Bake] ✔ " + key + "  切片 y[" + yBottom.ToString("0.00") + ", " + yTop.ToString("0.00") +
                          "]  相机高 " + camY.ToString("0.0") + "  →  " + assetPath);
            return e;
        }

        // ================================================================ 工具

        /// <summary>
        /// 世界范围 = 所有已登记分区矩形的并集 + 留白。
        /// 用分区表而不是「所有渲染器的包围盒」：后者会把远景楼（x ≈ −93）也框进来，地图就全废了。
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

            minX -= Margin; maxX += Margin;
            minZ -= Margin; maxZ += Margin;
            rect = Rect.MinMaxRect(minX, minZ, maxX, maxZ);
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

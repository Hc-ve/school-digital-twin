using System;
using System.IO;
using UnityEngine;

namespace SchoolTwin.Present
{
    /// <summary>
    /// 【P26】PPT 图片轮播。
    ///
    /// ── 为什么不播视频 ────────────────────────────────────────────────
    /// 用户明确要求"PPT 应当展示图片"。视频版 PPT 没法跳页、没法停，
    /// 而且真实课堂上老师是一页页翻的。
    ///
    /// ── 和 `PcScreenPlayer` 的关系 ─────────────────────────────────────
    /// 两者**互斥**：进 PPT 模式就停视频，退出 PPT 才让视频接管。
    /// 都往同一个材质上贴图，同时生效会互相覆盖。
    ///
    /// ── 内存 ─────────────────────────────────────────────────────────
    /// 只保留**当前页**的纹理。1920×1200 RGB24 一张约 6.6MB，
    /// 12 页全缓存就是 80MB —— 完全没必要，切页时销毁上一张即可。
    /// </summary>
    public sealed class PcSlideShow
    {
        /// <summary>`_EmissionColor` 的属性 ID（和 `PcScreenPlayer` 保持一致）。</summary>
        private static readonly int EmissionId = Shader.PropertyToID("_EmissionColor");

        private string[] _files;
        private Material[] _targets;
        private Texture2D _current;
        private Color _onColor;
        private int _index = -1;
        private bool _visible;

        // ---- 显示器的图集合成 ----
        // 显示器的材质是 Standard + `_EmissionMap`，那张图集里**只有屏幕矩形是白的**，
        // 其余全黑 —— 这就是"只有屏幕发光、边框不亮"的实现方式。
        //
        // 如果直接把 PPT 图片塞进 `_EmissionMap`，等于把遮罩换掉了：
        // 整个显示器（机身、底座、支架）都会显示 PPT 画面。
        // 2026-10-27 用户实测："我切换PPT，主脑的材质也在跟着变" —— 就是这个。
        //
        // 正确做法：先把 PPT 画到一张 **2048² 的合成图集**里（屏幕矩形内是画面、
        // 其余黑），再把合成结果当 `_EmissionMap`。用的是和视频完全相同的
        // `SchoolTwin/ScreenComposite` 着色器。
        private RenderTexture _atlasRT;
        private Material _compositeMat;
        private int _atlasSize = 2048;
        private Vector2 _uvMin;
        private Vector2 _uvMax;
        private bool _needComposite;

        /// <summary>一共几页。</summary>
        public int Count { get { return _files != null ? _files.Length : 0; } }

        /// <summary>当前页码（0 起）。-1 表示还没显示过。</summary>
        public int Index { get { return _index; } }

        /// <summary>有没有素材。</summary>
        public bool HasSlides { get { return Count > 0; } }

        /// <summary>是否正在显示。</summary>
        public bool Visible { get { return _visible; } }

        /// <summary>
        /// 建一个轮播器。
        /// </summary>
        /// <param name="files">相对 `PC/` 的图片路径列表（已排序）。</param>
        /// <param name="targets">
        /// 要贴图的材质。显示器的材质有 `_EmissionMap`（Standard），
        /// 幕布的材质有 `_MainTex`（ScreenOnModel）—— 这里自动识别。
        /// </param>
        /// <param name="onColor">自发光颜色（白色 = 正常亮度）。</param>
        /// <param name="uvMin">屏幕矩形在显示器图集里的左下 UV。</param>
        /// <param name="uvMax">屏幕矩形在显示器图集里的右上 UV。</param>
        /// <param name="atlasSize">显示器图集边长（和视频用的一致，2048）。</param>
        public static PcSlideShow Create(string[] files, Material[] targets, Color onColor,
                                         Vector2 uvMin, Vector2 uvMax, int atlasSize)
        {
            PcSlideShow s = new PcSlideShow();
            s._files = files ?? new string[0];
            s._targets = targets ?? new Material[0];
            s._onColor = onColor;
            s._uvMin = uvMin;
            s._uvMax = uvMax;
            s._atlasSize = Mathf.Max(256, atlasSize);

            if (s._files.Length == 0)
            {
                Debug.LogWarning("[P26] PPT 图片列表是空的 —— 把图片放到 " +
                                 PcMediaLibrary.FolderPath + "/" + PcMediaLibrary.SlideFolder +
                                 "/ 下，文件名用 slide_01.jpg 这种数字前缀（决定页序）。");
            }

            // 有任何一个目标材质用 `_EmissionMap`（Standard 显示器）就需要合成
            for (int i = 0; i < s._targets.Length; i++)
            {
                Material m = s._targets[i];
                if (m != null && m.HasProperty("_EmissionMap"))
                {
                    s._needComposite = true;
                    break;
                }
            }

            if (s._needComposite) s.BuildComposite();

            return s;
        }

        /// <summary>建合成用的 RT 和材质。</summary>
        private void BuildComposite()
        {
            Shader sh = Shader.Find("SchoolTwin/ScreenComposite");
            if (sh == null)
            {
                Debug.LogWarning("[P26] 找不到 `SchoolTwin/ScreenComposite`，" +
                                 "PPT 会直接贴到显示器材质上（机身会跟着变色）。");
                _needComposite = false;
                return;
            }

            _atlasRT = new RenderTexture(_atlasSize, _atlasSize, 0, RenderTextureFormat.ARGB32);
            _atlasRT.name = "PcSlideAtlasRT";
            _atlasRT.wrapMode = TextureWrapMode.Clamp;
            _atlasRT.filterMode = FilterMode.Bilinear;
            _atlasRT.Create();

            // 先清黑：屏幕矩形之外必须是黑的，否则整个机身都会亮
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = _atlasRT;
            GL.Clear(true, true, Color.black);
            RenderTexture.active = prev;

            _compositeMat = new Material(sh);
            _compositeMat.name = "PcSlideComposite";
            _compositeMat.SetVector("_ScreenMin", new Vector4(_uvMin.x, _uvMin.y, 0f, 0f));
            _compositeMat.SetVector("_ScreenMax", new Vector4(_uvMax.x, _uvMax.y, 0f, 0f));
            _compositeMat.SetFloat("_Inset", 0f);
            _compositeMat.SetColor("_Tint", Color.white);
        }

        /// <summary>显示指定页。返回 false 表示这张图读不出来。</summary>
        public bool Show(int index)
        {
            if (!HasSlides) return false;

            // 循环：翻过头就绕回去（课堂上"下一页"按到底不该卡住）
            if (index < 0) index = _files.Length - 1;
            if (index >= _files.Length) index = 0;

            string rel = _files[index];
            string full = PcMediaLibrary.Resolve(rel);

            Texture2D tex = LoadTexture(full);
            if (tex == null) return false;

            // 先换再销毁旧的 —— 顺序反了会有一帧黑屏
            Texture2D old = _current;
            _current = tex;
            _index = index;
            _visible = true;

            ApplyToTargets(tex);

            if (old != null) UnityEngine.Object.Destroy(old);
            return true;
        }

        /// <summary>下一页。</summary>
        public bool Next()
        {
            if (!HasSlides) return false;
            return Show(_index < 0 ? 0 : _index + 1);
        }

        /// <summary>上一页。</summary>
        public bool Prev()
        {
            if (!HasSlides) return false;
            return Show(_index < 0 ? 0 : _index - 1);
        }

        /// <summary>
        /// 隐藏（退出 PPT 模式）。把自发光压黑，材质留空。
        /// 纹理**不销毁** —— 用户可能只是想切到视频看看，再切回来还是这一页。
        /// </summary>
        public void Hide()
        {
            _visible = false;
            for (int i = 0; i < _targets.Length; i++)
            {
                Material m = _targets[i];
                if (m == null || !m.HasProperty(EmissionId)) continue;
                m.SetColor(EmissionId, Color.black);
            }
        }

        /// <summary>彻底释放（切换设备/退出播放时调用）。</summary>
        public void Dispose()
        {
            if (_current != null)
            {
                UnityEngine.Object.Destroy(_current);
                _current = null;
            }

            if (_atlasRT != null)
            {
                _atlasRT.Release();
                UnityEngine.Object.Destroy(_atlasRT);
                _atlasRT = null;
            }
            if (_compositeMat != null)
            {
                UnityEngine.Object.Destroy(_compositeMat);
                _compositeMat = null;
            }

            _visible = false;
            _index = -1;
        }

        // ---------------------------------------------------------------- 内部

        private static Texture2D LoadTexture(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath))
            {
                Debug.LogWarning("[P26] PPT 图片不存在：" + fullPath);
                return null;
            }

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(fullPath);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[P26] 读 PPT 图片失败：" + fullPath + " — " + e.Message);
                return null;
            }

            // 先建 2x2 占位，`LoadImage` 会按实际尺寸重建
            Texture2D tex = new Texture2D(2, 2, TextureFormat.RGB24, false);
            tex.name = "PcSlide_" + Path.GetFileNameWithoutExtension(fullPath);

            if (!tex.LoadImage(bytes))
            {
                Debug.LogWarning("[P26] 解码 PPT 图片失败（不是 JPG/PNG？）：" + fullPath);
                UnityEngine.Object.Destroy(tex);
                return null;
            }

            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            return tex;
        }

        private void ApplyToTargets(Texture2D tex)
        {
            float aspect = tex.height > 0 ? (float)tex.width / tex.height : 1.6f;

            // 先合成一张"只有屏幕矩形有画面"的图集（显示器用）
            Texture displayTex = tex;
            if (_needComposite && _atlasRT != null && _compositeMat != null)
            {
                float rectAspect = Mathf.Abs(_uvMax.x - _uvMin.x) /
                                   Mathf.Max(1e-5f, Mathf.Abs(_uvMax.y - _uvMin.y));
                _compositeMat.SetFloat("_Fit", aspect / Mathf.Max(1e-5f, rectAspect));

                Graphics.Blit(tex, _atlasRT, _compositeMat);
                displayTex = _atlasRT;
            }

            for (int i = 0; i < _targets.Length; i++)
            {
                Material m = _targets[i];
                if (m == null) continue;

                // 显示器走 Standard 的 `_EmissionMap`（用合成后的图集，保住遮罩）
                if (m.HasProperty("_EmissionMap")) m.SetTexture("_EmissionMap", displayTex);
                // 幕布走 ScreenOnModel 的 `_MainTex`（直接贴原图，它自己算 UV）
                if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", tex);

                // 幕布的着色器要额外告诉它"图片比例"并切到 contain
                if (m.HasProperty("_ImageAspect")) m.SetFloat("_ImageAspect", aspect);
                if (m.HasProperty("_Contain")) m.SetFloat("_Contain", 1f);

                if (m.HasProperty(EmissionId)) m.SetColor(EmissionId, _onColor);
            }
        }
    }
}

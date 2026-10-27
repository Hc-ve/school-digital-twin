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
        public static PcSlideShow Create(string[] files, Material[] targets, Color onColor)
        {
            PcSlideShow s = new PcSlideShow();
            s._files = files ?? new string[0];
            s._targets = targets ?? new Material[0];
            s._onColor = onColor;

            if (s._files.Length == 0)
            {
                Debug.LogWarning("[P26] PPT 图片列表是空的 —— 把图片放到 " +
                                 PcMediaLibrary.FolderPath + "/" + PcMediaLibrary.SlideFolder +
                                 "/ 下，文件名用 slide_01.jpg 这种数字前缀（决定页序）。");
            }
            return s;
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

            for (int i = 0; i < _targets.Length; i++)
            {
                Material m = _targets[i];
                if (m == null) continue;

                // 显示器走 Standard 的 `_EmissionMap`；幕布走 ScreenOnModel 的 `_MainTex`
                if (m.HasProperty("_EmissionMap")) m.SetTexture("_EmissionMap", tex);
                if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", tex);

                // 幕布的着色器要额外告诉它"图片比例"并切到 contain
                if (m.HasProperty("_ImageAspect")) m.SetFloat("_ImageAspect", aspect);
                if (m.HasProperty("_Contain")) m.SetFloat("_Contain", 1f);

                if (m.HasProperty(EmissionId)) m.SetColor(EmissionId, _onColor);
            }
        }
    }
}

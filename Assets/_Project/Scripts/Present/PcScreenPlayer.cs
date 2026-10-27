using System;
using System.IO;
using UnityEngine;
using UnityEngine.Video;

namespace SchoolTwin.Present
{
    /// <summary>
    /// 【P26】一块屏幕的视频载体。
    ///
    /// ── 它解决什么问题 ────────────────────────────────────────────────
    /// 显示器屏幕的 `_EmissionMap` 覆盖的是**整个显示器图集**（含边框）。
    /// 直接把视频塞进去，边框也会采样到视频 → 整个外壳发光。
    ///
    /// 所以链路是「解码 → 合成 → 贴图」三段：
    /// ```
    /// VideoPlayer ──→ videoRT（原始画面）
    ///                     ↓ Graphics.Blit + TwinScreenComposite 着色器
    ///                 screenRT（与图集同布局：屏幕矩形内是画面，其余纯黑）
    ///                     ↓
    ///                 显示器材质._EmissionMap = screenRT
    /// ```
    ///
    /// ── 为什么 screenRT 是 2048² 而不是屏幕本身那么大 ────────────────
    /// 因为 `_EmissionMap` 的 UV 是**图集 UV**（0..1 覆盖整个显示器）。
    /// 如果 RT 只做屏幕那么大，屏幕矩形以外的 UV 会采样到边界像素（Clamp）→ 边框又亮了。
    /// 只有「与图集同布局」才能让 `_EmissionMap_ST` 保持单位矩阵、边框自然为黑。
    ///
    /// ── 多块屏共用 ────────────────────────────────────────────────────
    /// 一个实例 = 一个解码器 + 一张 videoRT + 一张 screenRT。
    /// 同一分区的 N 块屏共用**同一个实例**（它们的材质本来就共用），
    /// 所以 19 台显示器只解码一次。要"逐个亮"就靠各自材质的 `_EmissionColor` 错峰，
    /// 不是各自解码。
    /// </summary>
    public sealed class PcScreenPlayer
    {
        /// <summary>合成结果，直接给显示器材质的 `_EmissionMap`。</summary>
        public RenderTexture ScreenRT;

        /// <summary>目标材质（该分区的显示器材质副本）。</summary>
        public Material TargetMaterial;

        /// <summary>视频文件是否存在。false 时调用方应退回"纯色自发光"。</summary>
        public bool HasVideo;

        private VideoPlayer _player;
        private RenderTexture _videoRT;
        private GameObject _go;
        private Material _composite;
        private Color _onColor;
        private float _fit = 1f;
        private bool _dimensionKnown;
        private bool _on;

        private const int AtlasSize = 2048;      // 与 MaskTexWidth/Height 一致
        private const int VideoW = 1024;
        private const int VideoH = 576;

        /// <summary>
        /// 创建一块屏的播放器。
        /// </summary>
        /// <param name="name">物体名，方便在 Hierarchy 里认。</param>
        /// <param name="fileName">`StreamingAssets/PC/` 下的相对路径，如 `master/boot.mp4`。</param>
        /// <param name="uvMin">屏幕矩形在**图集 UV** 里的左下角。</param>
        /// <param name="uvMax">右上角。</param>
        /// <param name="target">要改 `_EmissionMap` 的显示器材质。</param>
        /// <param name="onColor">点亮时的 `_EmissionColor`（会被 `_EmissionMap` 相乘）。</param>
        /// <param name="parent">挂在谁下面（只影响 Hierarchy 观感）。</param>
        /// <param name="atlasSize">合成 RT 的边长，必须等于显示器图集边长（默认 2048）。</param>
        /// <param name="inset">四周均匀内缩比例（0~0.3）。模拟真实显示器留一圈黑边。</param>
        public static PcScreenPlayer Create(string name, string fileName,
                                            Vector2 uvMin, Vector2 uvMax,
                                            Material target, Color onColor, Transform parent,
                                            int atlasSize = AtlasSize,
                                            float inset = 0.06f)
        {
            PcScreenPlayer p = new PcScreenPlayer();
            p.TargetMaterial = target;
            p._onColor = onColor;

            string full = Path.Combine(Application.streamingAssetsPath,
                                       Path.Combine("PC", fileName ?? "").Replace('\\', '/'));
            if (string.IsNullOrEmpty(fileName) || !File.Exists(full))
            {
                // 没有素材就安静退回纯色模式，不报错 —— 素材是后补的
                Debug.Log("[P26] 找不到视频「" + full + "」，这块屏继续用纯色自发光。");
                p.HasVideo = false;
                return p;
            }

            p.HasVideo = true;

            // ---- 合成结果：与图集同布局 ----
            int sz = Mathf.Max(256, atlasSize);
            p.ScreenRT = new RenderTexture(sz, sz, 0, RenderTextureFormat.ARGB32);
            p.ScreenRT.name = "PcScreenRT_" + name;
            p.ScreenRT.wrapMode = TextureWrapMode.Clamp;
            p.ScreenRT.filterMode = FilterMode.Bilinear;
            p.ScreenRT.Create();
            p.ClearScreen();

            // ---- 合成材质 ----
            Shader sh = Shader.Find("SchoolTwin/ScreenComposite");
            if (sh == null)
            {
                Debug.LogWarning("[P26] 找不到着色器 `SchoolTwin/ScreenComposite`，视频不启用。");
                p.HasVideo = false;
                return p;
            }
            p._composite = new Material(sh);
            p._composite.name = "PcScreenComposite_" + name;
            p._composite.SetVector("_ScreenMin", new Vector4(uvMin.x, uvMin.y, 0f, 0f));
            p._composite.SetVector("_ScreenMax", new Vector4(uvMax.x, uvMax.y, 0f, 0f));
            p._composite.SetFloat("_Fit", p._fit);
            p._composite.SetFloat("_Inset", Mathf.Clamp(inset, 0f, 0.3f));

            // ---- 解码 ----
            p._videoRT = new RenderTexture(VideoW, VideoH, 0, RenderTextureFormat.ARGB32);
            p._videoRT.name = "PcVideoRT_" + name;
            p._videoRT.Create();

            p._go = new GameObject("PcScreenPlayer_" + name);
            if (parent != null) p._go.transform.SetParent(parent, false);

            // 【声音】走 3D AudioSource，挂在显示器位置上 —— 站在哪台机器前就听哪台的。
            // 之前为了省事设成 `None`，结果开机视频完全没声音（用户实测）。
            AudioSource src = p._go.AddComponent<AudioSource>();
            src.playOnAwake = false;
            src.spatialBlend = 1f;                      // 全 3D
            src.rolloffMode = AudioRolloffMode.Linear;
            src.minDistance = 1.5f;
            src.maxDistance = 14f;
            src.volume = 0.9f;
            src.dopplerLevel = 0f;                      // 机器不动，不需要多普勒

            p._player = p._go.AddComponent<VideoPlayer>();
            p._player.playOnAwake = false;
            p._player.isLooping = false;
            p._player.skipOnDrop = true;
            p._player.renderMode = VideoRenderMode.RenderTexture;
            p._player.targetTexture = p._videoRT;
            p._player.url = full;

            // ⚠️ 音轨配置必须写在 `Prepare()` **之前** ——
            // 准备阶段才会按这里的设置创建音频输出，之后改要重新 Prepare。
            p._player.audioOutputMode = VideoAudioOutputMode.AudioSource;
            p._player.controlledAudioTrackCount = 1;
            p._player.EnableAudioTrack(0, true);
            p._player.SetTargetAudioSource(0, src);

            p._player.prepareCompleted += _ => { p.UpdateFit(); };
            p._player.Prepare();

            Debug.Log("[P26] 屏幕视频就绪：" + name + " ← " + full);
            return p;
        }

        /// <summary>视频尺寸要等 `prepareCompleted` 才知道，拿到后重算 letterbox。</summary>
        private void UpdateFit()
        {
            if (_player == null) return;
            int w = (int)_player.width;
            int h = (int)_player.height;
            if (w <= 0 || h <= 0) return;

            float videoAspect = (float)w / h;

            // 屏幕矩形在图集里的宽高比（图集是正方形，所以直接用 UV 差值）
            Vector4 mn = _composite.GetVector("_ScreenMin");
            Vector4 mx = _composite.GetVector("_ScreenMax");
            float rectAspect = Mathf.Max(1e-5f, (mx.x - mn.x) / Mathf.Max(1e-5f, mx.y - mn.y));

            _fit = videoAspect / rectAspect;
            _composite.SetFloat("_Fit", _fit);
            _dimensionKnown = true;

            Debug.Log("[P26] 视频 " + w + "×" + h + "（比例 " + videoAspect.ToString("0.00") +
                      "），屏幕矩形比例 " + rectAspect.ToString("0.00") +
                      " → _Fit=" + _fit.ToString("0.000") +
                      (_fit >= 1f ? "（裁左右）" : "（裁上下）"));
        }

        /// <summary>开关。开 = 播放；关 = 停止并清黑。</summary>
        public void SetOn(bool on)
        {
            if (!HasVideo) return;
            if (_on == on) return;
            _on = on;

            if (on)
            {
                if (_player != null && !_player.isPlaying) _player.Play();
                if (TargetMaterial != null) TargetMaterial.SetColor("_EmissionColor", _onColor);
            }
            else
            {
                if (_player != null && _player.isPlaying) _player.Stop();
                ClearScreen();
                if (TargetMaterial != null) TargetMaterial.SetColor("_EmissionColor", Color.black);
            }
        }

        /// <summary>
        /// 换一段视频接着播（"打开PPT"/"视频播放"用）。
        /// 文件不存在就返回 false 并打日志 —— 素材是后补的，不该因此崩。
        /// </summary>
        public bool PlayFile(string fileName)
        {
            if (!HasVideo || _player == null) return false;
            if (string.IsNullOrEmpty(fileName)) return false;

            string full = Path.Combine(Application.streamingAssetsPath,
                                       Path.Combine("PC", fileName).Replace('\\', '/'));
            if (!File.Exists(full))
            {
                Debug.Log("[P26] 找不到视频「" + full + "」，画面保持不变。");
                return false;
            }

            _player.url = full;
            _player.Prepare();
            _player.Play();          // Unity 会等 prepare 完成后再真正出画面
            _on = true;
            if (TargetMaterial != null) TargetMaterial.SetColor("_EmissionColor", _onColor);

            Debug.Log("[P26] 切视频 → " + full);
            return true;
        }

        /// <summary>每帧调用：把视频合成进 screenRT。</summary>
        public void Tick()
        {
            if (!HasVideo || !_on) return;
            if (_player == null || _videoRT == null || ScreenRT == null || _composite == null) return;
            if (!_player.isPrepared) return;

            if (!_dimensionKnown) UpdateFit();

            Graphics.Blit(_videoRT, ScreenRT, _composite);
        }

        private void ClearScreen()
        {
            if (ScreenRT == null) return;
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = ScreenRT;
            GL.Clear(true, true, Color.black);
            RenderTexture.active = prev;
        }

        public void Destroy()
        {
            if (_player != null && _player.isPlaying) _player.Stop();

            if (ScreenRT != null) { ScreenRT.Release(); UnityEngine.Object.Destroy(ScreenRT); ScreenRT = null; }
            if (_videoRT != null) { _videoRT.Release(); UnityEngine.Object.Destroy(_videoRT); _videoRT = null; }
            if (_composite != null) { UnityEngine.Object.Destroy(_composite); _composite = null; }
            if (_go != null) { UnityEngine.Object.Destroy(_go); _go = null; }
        }
    }
}

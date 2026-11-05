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
        /// <summary>显示区域的真实宽高比（宽/高）。由调用方显式传入或从 UV 推导。</summary>
        private float _rectAspect = 1f;
        private bool _dimensionKnown;
        private bool _on;

        /// <summary>
        /// 屏幕当前是否点亮。
        ///
        /// 关机动画要用它判断"已经关了的设备别再播一遍" ——
        /// 主理人 2026-11-02 实测：先点「关闭主脑」把主脑关了，
        /// 再点「全部关机」时主脑和投影又被点亮播了一次关机动画，逻辑上说不通。
        /// </summary>
        public bool IsOn { get { return _on; } }
        private int _blitCount;      // 【诊断】累计 Blit 次数
        private float _prepareRetryAt = -1f;   // 未就绪重试的时间点

        /// <summary>
        /// 【P26 2026-11-05】已重试 Prepare 的次数。
        /// 必须有上限，见 `Tick()` 里那段说明 —— 无上限的重试会把 Console 刷满
        /// （实测 255 条），并持续加压解码器。
        /// </summary>
        private int _prepareRetries;
        private const int MaxPrepareRetries = 2;

        /// <summary>
        /// 【P29 2026-11-05】已经发起过一次 `Prepare()`，但还没收到 `prepareCompleted`。
        ///
        /// ── 为什么需要这个字段 ────────────────────────────────────────────
        /// `PlayFile` 要幂等（见那里的长注释），判断"能不能跳过这一次 Prepare"
        /// 不能只看 `isPrepared` —— 那个值在 prepare **进行中**也是 false，
        /// 与"从来没开始过"无法区分：
        ///   · 进行中 → 再 Prepare 会打断上一次（正是要避免的）
        ///   · 没开始过 → 必须 Prepare（否则永远没画面）
        /// `_prepareStarted` 就是把这两种情况分开的那一位。
        ///
        /// 置真：`PlayFile` / `SetOn` 里每次发起 Prepare 之后
        /// 置假：`OnPreparedPlay`（就绪了）、`Destroy`（对象没了）、
        ///       `_gaveUp`（放弃了）、`SetOn(false)`（关机）
        /// </summary>
        private bool _prepareStarted;

        /// <summary>
        /// 【P26 2026-11-05】已经彻底放弃这块屏（解码器起不来）。
        /// 置位后 `Tick()` 直接返回，不再重试、不再刷日志。
        /// 只有重新 `SetOn` 才会清掉它 —— 等价于"断电重来"。
        /// </summary>
        private bool _gaveUp;

        /// <summary>【P26】最近一次 VideoPlayer 报的错（`errorReceived`）。诊断用。</summary>
        private string _lastError;

        /// <summary>
        /// 【P26】这台屏的**默认视频**（创建时传入，通常是开机动画）。
        /// 关机后再开机会回到它 —— 否则「打开PPT → 关机 → 开机」还会显示 PPT。
        /// </summary>
        private string _defaultFile;
        /// <summary>当前正在播的文件。和 `_defaultFile` 不同就说明被换过片。</summary>
        private string _currentFile;
        /// <summary>
        /// 当前显示的是**静态图**（退出演示后的桌面）。
        /// 下次开机要据此判断"该切回默认视频了"——
        /// 因为静态图不是视频，`_currentFile` 没变，不加这个标志会以为还在播默认视频。
        /// </summary>
        private bool _showingStill;

        /// <summary>
        /// 【P29 2026-11-05】当前待机图是哪个文件。
        /// 供 `ShowStill` 做幂等（同一张图不重复读盘解码），
        /// 与 `_showingStill` 配对使用：只有它为真时文件名才有意义。
        /// </summary>
        private string _stillFile;

        /// <summary>调试用名字（日志里区分是哪个点位）。</summary>
        public string Name = "?";

        private const int AtlasSize = 2048;      // 与 MaskTexWidth/Height 一致
        private const int VideoW = 1024;
        private const int VideoH = 576;

        /// <summary>
        /// `_EmissionColor` 的属性 ID。
        /// 【为什么要自己定义】`TwinDevicePresenter` 里那个 `EmissionId` 是 **private**，
        /// 本文件访问不到（2026-10-27 CS0103 踩过）。`Shader.PropertyToID` 的结果是缓存的，
        /// 重复定义一份没有额外开销。
        /// </summary>
        private static readonly int EmissionId = Shader.PropertyToID("_EmissionColor");

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
        /// <param name="rectAspect">
        /// 显示区域的**真实宽高比**（宽 / 高）。
        /// 传 0 表示"从 UV 矩形的宽高比推导" —— 只对**正方形图集**成立（显示器 2048² 是这种情况）。
        /// 幕布面片是 3.11×2.13 这种非正方形，UV 却是 0..1，必须**显式传入真实比例**，
        /// 否则会让 cover 裁掉过多画面（2026-10-27 用户报"投影画面显示不全、比例不符合"）。
        /// </param>
        /// <param name="volume">
        /// 音源音量 0~1。主脑给 0.9，机房给低值 ——
        /// 主脑和机房是**两个独立音源**（各在自己的锚点位置），
        /// 都给 0.9 的话开机音效会叠加成两倍响。
        /// </param>
        /// <param name="audioPos">
        /// 音源的**世界位置**。传 null 就跟着 `parent` 走。
        ///
        /// 【为什么需要这个】声音应该从**音箱**出来，不是从显示器/投影仪出来。
        /// 这个场景里机房墙上有两个 `SubBuffer02` 音箱，把音源放它们中间，
        /// 走近时能听出"声音来自那面墙"而不是"屏幕在响"。
        /// </param>
        public static PcScreenPlayer Create(string name, string fileName,
                                            Vector2 uvMin, Vector2 uvMax,
                                            Material target, Color onColor, Transform parent,
                                            int atlasSize = AtlasSize,
                                            float inset = 0.06f,
                                            float rectAspect = 0f,
                                            float volume = 0.9f,
                                            Vector3? audioPos = null)
        {
            PcScreenPlayer p = new PcScreenPlayer();
            _sessionPlayerCount++;      // 【P30】会话累计：建了几个播放器
#if UNITY_EDITOR
            UnityEditor.EditorPrefs.SetInt(PrefPlayer,
                UnityEditor.EditorPrefs.GetInt(PrefPlayer, 0) + 1);
#endif
            p.TargetMaterial = target;
            p._onColor = onColor;
            p.Name = name;
            p._defaultFile = fileName;      // 关机后再开机回到这个
            p._currentFile = fileName;

            // 显示区域的真实宽高比。0 = 从 UV 矩形推导（只对正方形图集成立）。
            float aspect = rectAspect;
            if (aspect <= 0.0001f)
            {
                float dw = Mathf.Abs(uvMax.x - uvMin.x);
                float dh = Mathf.Abs(uvMax.y - uvMin.y);
                aspect = dh > 1e-5f ? dw / dh : 1f;
            }
            p._rectAspect = Mathf.Max(1e-4f, aspect);

            string full = PcMediaLibrary.Resolve(fileName);
            if (string.IsNullOrEmpty(fileName) || !File.Exists(full))
            {
                // 没有素材就安静退回纯色模式，不报错 —— 素材是后补的
                Debug.Log("[P26] 找不到视频「" + full + "」，这块屏继续用纯色自发光。\n" +
                          "   放素材的位置：" + PcMediaLibrary.FolderPath + "\n" +
                          "   " + PcMediaLibrary.Describe());
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
            // 音源放到音箱位置（不 parent，避免继承缩放；VideoPlayer 不依赖位置）
            if (audioPos.HasValue) p._go.transform.position = audioPos.Value;

            // 【声音】走 3D AudioSource，挂在显示器位置上 —— 站在哪台机器前就听哪台的。
            // 之前为了省事设成 `None`，结果开机视频完全没声音（用户实测）。
            AudioSource src = p._go.AddComponent<AudioSource>();
            src.playOnAwake = false;
            src.spatialBlend = 1f;                      // 全 3D
            src.rolloffMode = AudioRolloffMode.Linear;
            src.minDistance = 1.5f;
            src.maxDistance = 14f;
            src.volume = Mathf.Clamp01(volume);
            src.dopplerLevel = 0f;                      // 机器不动，不需要多普勒

            p._audioSrc = src;                          // 【P30】重建时要精确销毁它

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

            p._player.prepareCompleted += p.OnPreparedPlay;

            // ══════════════════════════════════════════════════════════════
            // 【P26 2026-11-05】必须订阅 `errorReceived`。
            //
            // 上一版只订阅了 `prepareCompleted`，而 VideoPlayer 的失败是**静默的**：
            // 解码器起不来时它既不回调 `prepareCompleted`、也不打任何日志，
            // 外部只能看到 `isPrepared` 一直是 false。
            // 于是"素材坏了 / 解码器耗尽 / 路径不可读"三种完全不同的原因，
            // 在现场表现一模一样 —— 这才是这个 bug 查了这么久的真正原因。
            // `errorReceived` 是唯一能分辨它们的出口，接上它。
            // ══════════════════════════════════════════════════════════════
            p._player.errorReceived += p.OnVideoError;

            // 【P30】把重建所需的原始参数存下来，供 `RebuildPlayer` 使用。
            // 只存"从外面拿到的、重建时拿不到"的东西。
            // ⚠️ 只存真正用到的：`audioPos` / `parent` 在创建时已经落到了
            // `_go.transform` 上，重建时物体本身不动，不需要再存一份。
            // （第一版存了 `_parentTransform`、`_audioPosWorld` 却从不使用 ——
            //   那正是我这次在批评的"看着完整、实则没用"的写法。）
            p._videoFullPath = full;
            p._audioVolume = Mathf.Clamp01(volume);

            try
            {
                p._player.Prepare();
                p._prepareStarted = true;
                p.NotePrepare("Create");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[P26] " + name + " 准备视频失败：" + e.Message +
                                 "（SetOn 时会再试一次）");
            }

            // ══════════════════════════════════════════════════════════════
            // 【P30 2026-11-05】**这行日志原来叫「屏幕视频就绪」，是错的。**
            //
            // 它打印在 `Prepare()` 之后**紧接着**执行 —— 那时候只是"发出了准备请求"，
            // 一帧都还没解出来。真正的就绪只可能在 `OnPreparedPlay`（回调）里。
            //
            // 代价有多大：我按这行日志统计了 16 次 Play，得出「前 9 次都正常」的
            // 错误结论，并据此去修"播放器泄漏"。改完之后症状一模一样 ——
            // 因为那 9 次里 `prepareCompleted` 其实早就开始不触发了，
            // 只是这行假日志把它盖住了。
            //
            // **一行名字错的日志，能把整轮排查带错方向。** 现在改成「已请求准备」，
            // 并且它只在 Create 时打一次，不再参与"到底好没好"的判断。
            // ══════════════════════════════════════════════════════════════
            Debug.Log("[P30] " + name + " 已请求准备（不是就绪）← " + full +
                      "　" + SessionPrepareReport());
            return p;
        }

        // =====================================================================
        // 【P30 2026-11-05】会话级诊断：数「这个编辑器进程一共 Prepare 了多少次」
        // =====================================================================

        /// <summary>
        /// 本编辑器进程内**累计发起过的 Prepare 次数**（跨 Play 累加，域重载不清零）。
        ///
        /// ── 为什么要数这个 ────────────────────────────────────────────────
        /// 日志给出的规律是：累计 Prepare 到 **200~300 次**之后，
        /// `prepareCompleted` 就**再也不会触发**，而且**退出重进 Play 也不恢复**
        /// （域重载都救不回来，说明残留在原生层、不在 C# 静态变量里）。
        ///
        ///   上一次会话：Play#1-2 正常 → Play#3 起全死（当时累计 206 次）
        ///   本次会话：  Play#1-9 正常 → Play#10 起全死（当时累计 289 次）
        ///
        /// 两次临界点不同、但量级一致，且都在"同一份代码、同一台机器"上 ——
        /// 这是**累计资源耗尽**的特征，不是某处逻辑写错。
        ///
        /// 修法有两条，这一条是"看得见"：把这个数打出来。
        /// 另一条是"不发生"：把每次 Play 的 Prepare 次数压到个位数（见 `NeedsFile`
        /// 与 `IsPreparing` 的调用点）。改前 30+ 次/Play，改后 6 次/Play。
        /// </summary>
        private static int _sessionPrepareCount;
        private static int _sessionPlayerCount;
        private static int _sessionDestroyCount;

        // ── 【P30】跨 Play 累计（EditorPrefs）────────────────────────────────
        //
        // ⚠️ 上面那三个 `static` 变量**每次进出 Play 都会被清零**。
        // 证据：本工程 `m_EnterPlayModeOptionsEnabled: 0`，即**域重载开着**，
        // Editor.log 里每次 Play 之前都有一行 `Reloading assemblies for play mode`，
        // 本会话共 17 行，与 17 次 Play 严格一一对应。域一重载，静态字段回初值。
        //
        // 而这次要诊断的恰恰是「**跨 Play 累计**到 200~300 次后失效」。
        // 用会被清零的变量去数，等于什么都数不到 —— 这是我第一版写成
        // `static int` 时的错，和「屏幕视频就绪」那行假日志是同一类错：
        // **测量工具本身先说了一个善意的谎。**
        //
        // 改为存进 `EditorPrefs`：编辑器进程内跨域重载存活、重启也不清
        // （正好能验证"重启编辑器后是否恢复"）。打包后不参与。
        //
        // 想从零复验：Console 里执行
        //   `SchoolTwin.Present.PcScreenPlayer.ResetSessionCounters();`
        private const string PrefPrepare = "SchoolTwin.P30.PrepareCount";
        private const string PrefPlayer = "SchoolTwin.P30.PlayerCount";
        private const string PrefDestroy = "SchoolTwin.P30.DestroyCount";

        /// <summary>本次 Play 内的 Prepare 次数（域重载会清零，与"跨 Play 累计"对照看）。</summary>
        private static int _playPrepareCount;

        /// <summary>【P30】把跨 Play 累计计数清零。复验"从零开始能跑多久"时用。</summary>
        public static void ResetSessionCounters()
        {
#if UNITY_EDITOR
            UnityEditor.EditorPrefs.SetInt(PrefPrepare, 0);
            UnityEditor.EditorPrefs.SetInt(PrefPlayer, 0);
            UnityEditor.EditorPrefs.SetInt(PrefDestroy, 0);
            _sessionPrepareCount = 0;
            _sessionPlayerCount = 0;
            _sessionDestroyCount = 0;
            _playPrepareCount = 0;
            Debug.Log("[P30] 跨 Play 累计计数已清零。");
#endif
        }

        /// <summary>记一次 Prepare。`reason` 只进日志，方便看是谁发起的。</summary>
        private void NotePrepare(string reason)
        {
            _sessionPrepareCount++;
            _playPrepareCount++;
            _lastPrepareReason = reason;
#if UNITY_EDITOR
            UnityEditor.EditorPrefs.SetInt(PrefPrepare,
                UnityEditor.EditorPrefs.GetInt(PrefPrepare, 0) + 1);
#endif
        }

        private string _lastPrepareReason = "-";

        // ── 【P30】重建播放器需要的原始参数（`RebuildPlayer` 用）──────────────
        /// <summary>视频绝对路径。重建时原样复用，不再走 `Resolve`。</summary>
        private string _videoFullPath;
        /// <summary>音源音量（0~1）。重建 AudioSource 用。</summary>
        private float _audioVolume = 0.9f;
        /// <summary>已经重建过几次。防止"重建→失败→重建"变成新的死循环。</summary>
        private int _rebuildCount;
        private const int MaxRebuilds = 2;

        /// <summary>
        /// 【P30】本播放器创建的那个 `AudioSource`。
        /// 重建时要精确销毁它 —— 用 `GetComponents` 全扫会误删别人挂的音源。
        /// </summary>
        private AudioSource _audioSrc;

        /// <summary>一行说清"这个进程已经干了多少次"，用来判断是否已接近耗尽。</summary>
        public static string SessionPrepareReport()
        {
#if UNITY_EDITOR
            int total = UnityEditor.EditorPrefs.GetInt(PrefPrepare, 0);
            int built = UnityEditor.EditorPrefs.GetInt(PrefPlayer, 0);
            int freed = UnityEditor.EditorPrefs.GetInt(PrefDestroy, 0);
            return "【跨Play累计】Prepare=" + total +
                   " 建=" + built + " 销毁=" + freed + " 存活=" + (built - freed) +
                   "　|　本次Play Prepare=" + _playPrepareCount +
                   (total >= 180 ? "　⚠️ 已接近历史失效点(200~300)" : "");
#else
            return "【本次Play】Prepare=" + _playPrepareCount;
#endif
        }

        /// <summary>
        /// 【P30 2026-11-05】把这个播放器的 `VideoPlayer` **整个换一个新的**。
        ///
        /// ══════════════════════════════════════════════════════════════════
        /// 为什么「重新 Prepare」救不回来，必须重建实例
        /// ══════════════════════════════════════════════════════════════════
        /// 现象：累计 Prepare 到 200~300 次之后，`prepareCompleted` **永久不触发**。
        /// 此时 `isPrepared` 一直是 false，且 `errorReceived` **一次都没报**
        /// （日志里 0 条，说明 VideoPlayer 自己认为"没出错，只是还没好"）。
        ///
        /// 在这一层上用 `Prepare()` 重试，等于往一个已经不再工作的解码会话上
        /// 继续发请求 —— 它不会拒绝，也不会回话。看起来就是"永远 Pending"。
        /// 这正是重试 2 次仍然失败的原因：**重试的对象本身就是坏的。**
        ///
        /// 唯一能刷新原生层状态的官方手段，是销毁这个组件、换一个新的。
        /// `VideoPlayer` 是 AddComponent 上去的，Destroy 后原生解码会话随之释放；
        /// 再 AddComponent + 配 url + Prepare，就是一次全新的会话。
        ///
        /// ── 为什么不能顺手也重建 RT ──────────────────────────────────────
        /// `ScreenRT` / `_videoRT` 已经被材质和 18 台座位引用着
        /// （座位屏的 `_EmissionMap` 直接指向 `ScreenRT`）。
        /// 换掉它们，得让所有引用点一起改 —— 那是另一场事故。
        /// 这里的病灶是解码会话，不是 RT，**只换 VideoPlayer，别的不动**。
        /// ══════════════════════════════════════════════════════════════════
        /// </summary>
        /// <returns>重建成功返回 true；连续失败超过 `MaxRebuilds` 或参数缺失返回 false。</returns>
        private bool RebuildPlayer()
        {
            if (_rebuildCount >= MaxRebuilds) return false;
            if (_go == null) return false;                          // 物体没了，没法重建
            if (string.IsNullOrEmpty(_videoFullPath)) return false;  // 没有原始路径
            if (!File.Exists(_videoFullPath)) return false;

            _rebuildCount++;

            // ① 先彻底撒手旧的：解绑 → 停 → 松贴图 → 清 url。
            //    少一步，旧实例的原生会话就可能不死。
            if (_player != null)
            {
                _player.prepareCompleted -= OnPreparedPlay;
                _player.errorReceived -= OnVideoError;
                // 先把音频轨摘掉，避免"组件没了、音轨还挂着"的半残状态
                _player.controlledAudioTrackCount = 0;
                if (_player.isPlaying) _player.Stop();
                _player.targetTexture = null;
                _player.url = string.Empty;
                _player.enabled = false;
            }

            // ② 只销毁**自己建的那个** AudioSource（`_audioSrc`）。
            //    不能用 `GetComponents<AudioSource>()` 全扫 ——
            //    `_go` 上若将来挂了别的音源（提示音等），会被一并删掉。
            if (_audioSrc != null)
            {
                UnityEngine.Object.DestroyImmediate(_audioSrc);
                _audioSrc = null;
            }

            // Destroy 组件是延迟到帧末生效的：先标记销毁、同一帧再加同名组件，
            // 会得到两个组件并存、`GetComponent` 拿到哪个不确定。
            // 所以这里用 `DestroyImmediate` —— 组件级、非资产，在运行时是安全的。
            if (_player != null) UnityEngine.Object.DestroyImmediate(_player);
            _player = null;

            // ③ 重开一套，参数与 `Create` 里完全一致（音轨必须配在 Prepare 之前）。
            AudioSource src = _go.AddComponent<AudioSource>();
            src.playOnAwake = false;
            src.spatialBlend = 1f;
            src.rolloffMode = AudioRolloffMode.Linear;
            src.minDistance = 1.5f;
            src.maxDistance = 14f;
            src.volume = _audioVolume;
            src.dopplerLevel = 0f;

            _player = _go.AddComponent<VideoPlayer>();
            _player.playOnAwake = false;
            _player.isLooping = false;
            _player.skipOnDrop = true;
            _player.renderMode = VideoRenderMode.RenderTexture;
            _player.targetTexture = _videoRT;
            _player.url = _videoFullPath;

            _player.audioOutputMode = VideoAudioOutputMode.AudioSource;
            _player.controlledAudioTrackCount = 1;
            _player.EnableAudioTrack(0, true);
            _player.SetTargetAudioSource(0, src);

            _player.prepareCompleted += OnPreparedPlay;
            _player.errorReceived += OnVideoError;

            _audioSrc = src;            // 【P30】记下来，下次重建要精确销毁

            _prepareStarted = true;
            _prepareRetryAt = -1f;
            NotePrepare("Rebuild");

            try { _player.Prepare(); }
            catch (Exception e)
            {
                Debug.LogWarning("[P30] " + Name + " 重建播放器后 Prepare 抛异常：" + e.Message);
                return false;
            }

            Debug.LogWarning("[P30] " + Name + " 换了一个新的 VideoPlayer 实例（第 " +
                             _rebuildCount + "/" + MaxRebuilds + " 次重建）—— " +
                             "重新 Prepare 救不回已死的解码会话，只能换实例。　" +
                             SessionPrepareReport());
            return true;
        }

        /// <summary>视频尺寸要等 `prepareCompleted` 才知道，拿到后重算 cover 比例。</summary>
        private void UpdateFit()
        {
            if (_player == null) return;
            // ⚠️ `_composite` 为空时直接返回。
            // 2026-10-27：`prepareCompleted` 回调在某些时序下会早于 `_composite` 赋值，
            // 里面抛 NullReference 会让 VideoPlayer 的状态机停在"未就绪"，
            // 表现是 `playing=False prepared=False frame=-1 blit=0` —— 画面永远出不来。
            if (_composite == null) return;

            int w = 0, h = 0;
            try
            {
                w = (int)_player.width;
                h = (int)_player.height;
            }
            catch (Exception)
            {
                // 极少数情况下解码器还没给出尺寸，读 width 会抛。
                // 静默返回，等下一帧/下次回调再算 —— 比例只是观感，不能因为它中断播放。
                return;
            }
            if (w <= 0 || h <= 0) return;

            float videoAspect = (float)w / h;

            // 显示区域的真实宽高比：用调用方传的（幕布面片），或从 UV 推（正方形图集的显示器）
            float rectAspect = Mathf.Max(1e-5f, _rectAspect);

            _fit = videoAspect / rectAspect;
            _composite.SetFloat("_Fit", _fit);
            _dimensionKnown = true;

            Debug.Log("[P26] 视频 " + w + "×" + h + "（比例 " + videoAspect.ToString("0.00") +
                      "），屏幕矩形比例 " + rectAspect.ToString("0.00") +
                      " → _Fit=" + _fit.ToString("0.000") +
                      (_fit >= 1f ? "（裁左右）" : "（裁上下）"));
        }

        /// <summary>
        /// 视频已经播到结尾时把播放位置拉回 0。
        ///
        /// 【为什么必须做】`isLooping = false`，播完停在最后一帧。再调 `Play()`
        /// **不会**从头开始 —— 画面就停在结尾那一帧，从外部看就是"点了没变化"。
        /// 2026-10-27 实测：`SetOn(False)` 和 `SetOn(True)` 两次日志都是 `frame=293`
        /// （视频共 294 帧），说明位置一直没回到开头。
        /// </summary>
        private void RestartIfFinished()
        {
            if (_player == null) return;
            long count = (long)_player.frameCount;
            if (count > 0 && _player.frame >= count - 1)
            {
                _player.frame = 0;
                Debug.Log("[P26] " + Name + " 视频已播完（frame " + (count - 1) + "/" + count +
                          "），回到第 0 帧重播");
            }
        }

        /// <summary>`Prepare()` 完成后的兜底播放（`SetOn(true)` 时还没就绪的情况）。</summary>
        private void OnPreparedPlay(VideoPlayer vp)
        {
            // 【P29】就绪了 → 清掉"正在准备"标志，否则 `PlayFile` 的幂等早退
            // 会一直以为还在准备中，同一文件再调也只 return 不 Play。
            _prepareStarted = false;
            // 顺带把重试计时器复位：已经成功过一次，之前的超时不该继续累积。
            _prepareRetryAt = -1f;

            UpdateFit();
            if (_on && vp != null && !vp.isPlaying)
            {
                RestartIfFinished();
                vp.Play();
                Debug.Log("[P26] " + Name + " 就绪后补播 → frame=" + vp.frame);
            }
        }

        /// <summary>
        /// 【P26 2026-11-05】VideoPlayer 报错出口。
        ///
        /// 接上它之前，解码失败是完全静默的（既不回调 prepareCompleted，
        /// 也不打日志），现场只能看到 `prepared=False` —— 无法区分
        /// 「素材编码不支持」「文件被占用」「解码器耗尽」三种原因。
        ///
        /// 这里**不立即放弃**：真实经验是"偶发的解码器忙"重试一次能过去，
        /// 所以只记录原因，是否放弃交给 `Tick()` 的重试计数决定。
        /// 但要把 `_lastError` 存下来，让 `DebugState()` 和最终的错误日志都能带上它。
        /// </summary>
        private void OnVideoError(VideoPlayer vp, string message)
        {
            _lastError = message;
            Debug.LogWarning("[P26] " + Name + " 视频解码错误：" + message +
                             "（" + DebugState() + "）");
        }

        /// <summary>
        /// 【诊断】把播放器的内部状态摊开，用来区分"没播"、"播了但没合成"、"合成了但没贴上去"。
        /// 用户报"点投影幕布没反应"时，靠这一行就能定位。
        /// </summary>
        public string DebugState()
        {
            if (!HasVideo) return "HasVideo=false";

            string playing = _player != null ? _player.isPlaying.ToString() : "无player";
            string prepared = _player != null ? _player.isPrepared.ToString() : "无player";
            string frame = _player != null ? _player.frame.ToString() : "?";
            string rt = ScreenRT != null
                ? (ScreenRT.IsCreated() ? "已创建 " + ScreenRT.width + "²" : "未创建") : "null";

            string mat;
            if (TargetMaterial == null) mat = "材质null";
            else
            {
                // 显示器用 `_EmissionMap`（Standard），幕布用 `_MainTex`（ScreenOnModel）。
                // 两个都查一遍，否则幕布那行永远显示 "emissionMap=null"，看着像没接上。
                Texture tex = null;
                string texProp = "?";
                if (TargetMaterial.HasProperty("_EmissionMap"))
                {
                    tex = TargetMaterial.GetTexture("_EmissionMap"); texProp = "_EmissionMap";
                }
                else if (TargetMaterial.HasProperty("_MainTex"))
                {
                    tex = TargetMaterial.GetTexture("_MainTex"); texProp = "_MainTex";
                }

                Color ec = TargetMaterial.HasProperty(EmissionId)
                    ? TargetMaterial.GetColor(EmissionId) : Color.clear;
                mat = texProp + "=" + (tex == null ? "null" : tex.name) +
                      " emissionColor=" + ec.r.ToString("0.00") + "," + ec.g.ToString("0.00") +
                      "," + ec.b.ToString("0.00");
            }

            return "on=" + _on + " playing=" + playing + " prepared=" + prepared +
                   " frame=" + frame + " retry=" + _prepareRetries + "/" + MaxPrepareRetries +
                   (_gaveUp ? " GAVEUP" : "") +
                   // 【P29】prep=Pending 表示"已发起 Prepare 还没回来"，
                   // 这是区分"正在准备"与"从未开始"的唯一可见标志。
                   // 复验"机房黑屏"时要看这个：若长期停在 Pending，
                   // 说明有地方在反复打断（见 PlayFile 幂等）。
                   " prep=" + (_prepareStarted ? "Pending" : "Idle") +
                   " still=" + (_showingStill ? (_stillFile ?? "?") : "-") +
                   " rt=" + rt + " blit=" + _blitCount +
                   (string.IsNullOrEmpty(_lastError) ? "" : " err=" + _lastError) + " " + mat;
        }

        /// <summary>开关。开 = 播放；关 = 停止并清黑。</summary>
        public void SetOn(bool on)
        {
            if (!HasVideo) return;

            // ══════════════════════════════════════════════════════════════
            // 【P26 2026-11-05】**顺序改动，这一句必须在 `_on == on` 早退之前**。
            //
            // 原来第一行就是 `if (_on == on) return;`。
            // 当 Tick 里因为重试耗尽把 `_on` 置成 false 之后，
            // 调用方再想开屏是 `SetOn(true)` → `_on(false) != true` → 能进来，没这个问题。
            // 但反过来：如果播放器已经 `_gaveUp` 且 `_on=false`，
            // 用户点"关机"（`SetOn(false)`）会被早退挡掉，`_gaveUp` 清不掉，
            // 下次开屏又被残留的 `_gaveUp` 直接掐死。
            // 所以**任何一次显式 SetOn 都视为一次"重新上电"**，先把失败状态清干净。
            // ══════════════════════════════════════════════════════════════
            if (_on == on)
            {
                // 状态没变，但如果之前放弃过，这次仍然算一次"重新上电"
                if (on && _gaveUp) { _gaveUp = false; _prepareRetries = 0; _prepareRetryAt = -1f; }
                else return;
            }
            else
            {
                _gaveUp = false;
                _prepareRetries = 0;
                _prepareRetryAt = -1f;
            }

            _on = on;

            if (on)
            {
                // 【P26】开机先把贴图收回本播放器。
                // 关机前如果停在 PPT 模式，材质上贴的还是 PPT 那张图 ——
                // 只把 `_on` 置 true 是不会自动换回视频的。
                RestoreTargetTexture();

                // ── 【P26】开机永远从**默认视频**（开机动画）开始 ──────────
                //
                // 2026-10-27 用户实测：「打开PPT → 关机 → 再开机」仍然是 PPT 画面。
                // 原因：`PlayFile` 改了 `_player.url`，关机只是 `Stop()`，
                // 再 `Play()` 播的还是那个被改过的 url。
                //
                // 语义上也不该保留：开机动画是固定流程，跟"上次看到哪"无关。
                if (!string.IsNullOrEmpty(_defaultFile) &&
                    (_showingStill || !string.Equals(_currentFile, _defaultFile, StringComparison.Ordinal)))
                {
                    Debug.Log("[P26] " + Name + " 开机 → 从「" +
                              (_showingStill ? "待机图" : _currentFile) +
                              "」切回默认视频「" + _defaultFile + "」");
                    if (PlayFile(_defaultFile))
                    {
                        // PlayFile 内部已经 Prepare + Play，这里只需补颜色
                        if (TargetMaterial != null) TargetMaterial.SetColor(EmissionId, _onColor);
                        return;
                    }
                }

                // ⚠️ 未准备就绪时 `Play()` 是空操作，状态机会卡在"未播"。
                // 2026-10-27 实测：投影仪 `playing=False prepared=False frame=-1 blit=0`，
                // 而电脑那边同一段代码是 `playing=True`。
                // 原因是幕布播放器的 `Prepare()` 在某个时机没跑成功（`prepareCompleted`
                // 回调里抛异常导致状态机中断）。
                // 兜底：这里判断一次，没就绪就重新 Prepare，并在回调里补一次 Play。
                //
                // ══════════════════════════════════════════════════════════
                // 【P29 2026-11-05】**必须区分"没开始"和"正在准备"**
                //
                // 原来的判据只有 `!isPrepared` —— 而 prepare **进行中**时
                // `isPrepared` 同样是 false。于是每调一次 `SetOn(true)` 就
                // `Prepare()` 一次，把上一次正在跑的 prepare 作废。
                //
                // 现场证据：日志里 `屏幕视频就绪` 与 `尚未就绪，重新 Prepare`
                // 交替出现 —— 刚就绪马上又被这一句打回未就绪。
                // 4 块屏因此同时 3 秒超时、同时放弃（截图那 4 条红字）。
                //
                // 正确判据：只有"从未发起、或上一次已经失败"才重新 Prepare。
                // `_prepareStarted` 为真表示"还在飞"，此时再 Prepare 纯属自伤，
                // 交给 `Tick()` 的 3 秒超时机制去判定失败。
                // ══════════════════════════════════════════════════════════
                if (_player != null && !_player.isPrepared)
                {
                    if (_prepareStarted)
                    {
                        // 已经在准备中 → 不加压，等回调或等 Tick 的超时。
                        // 这里**不能**再 Prepare，否则就是把刚起来的解码器按下去。
                    }
                    else
                    {
                        Debug.Log("[P26] " + Name + " 尚未就绪，重新 Prepare 并在回调里播放");
                        _player.prepareCompleted -= OnPreparedPlay;
                        _player.prepareCompleted += OnPreparedPlay;
                        _player.Prepare();
                        _prepareStarted = true;
                    }
                }
                else if (_player != null && !_player.isPlaying)
                {
                    RestartIfFinished();
                    _player.Play();
                }

                if (TargetMaterial != null) TargetMaterial.SetColor(EmissionId, _onColor);
            }
            else
            {
                if (_player != null && _player.isPlaying) _player.Stop();
                // 【P29】关机 = 一次明确的"停止"，正在飞的 prepare 我们不再关心，
                // 清掉标志，下次开机能干净地重新来一次。
                _prepareStarted = false;
                _prepareRetryAt = -1f;
                ClearScreen();
                if (TargetMaterial != null) TargetMaterial.SetColor(EmissionId, Color.black);
            }

            Debug.Log("[P26] SetOn(" + on + ") → " + DebugState());
        }

        /// <summary>
        /// 暂停 / 继续。
        ///
        /// 【和 `SetOn(false)` 的区别】`SetOn(false)` 是**关机**：停止 + 清黑 + 灭自发光。
        /// 暂停只是冻住画面，屏幕还亮着，回来接着播 —— 课堂上"暂停一下讲两句"就是这个。
        /// </summary>
        public void SetPaused(bool paused)
        {
            if (_player == null || !HasVideo) return;

            if (paused)
            {
                if (_player.isPlaying) _player.Pause();
            }
            else
            {
                if (!_player.isPlaying)
                {
                    RestartIfFinished();
                    _player.Play();
                }
            }
            Debug.Log("[P26] " + Name + (paused ? " 已暂停" : " 继续播放"));
        }

        /// <summary>当前是否暂停中。</summary>
        public bool IsPaused
        {
            get { return _player != null && HasVideo && _player.isPaused; }
        }

        /// <summary>
        /// 是否已经播到结尾。
        ///
        /// 【为什么不用 `!isPlaying`】刚 `Play()` 还没起来时 `isPlaying` 也是 false，
        /// 会误判成"播完了"。必须同时看 `frame` 到了末尾。
        /// 关机动画靠这个判断"什么时候可以真正断电"。
        /// </summary>
        public bool IsFinished
        {
            get
            {
                if (_player == null || !HasVideo) return true;
                if (!_player.isPrepared) return false;
                long count = (long)_player.frameCount;
                if (count <= 0) return false;
                return _player.frame >= count - 1;
            }
        }

        /// <summary>视频总时长（秒）。未就绪时返回 0。</summary>
        public float Duration
        {
            get
            {
                if (_player == null || !HasVideo || !_player.isPrepared) return 0f;
                return (float)_player.length;
            }
        }

        /// <summary>
        /// 【P26】显示一张静态图（退出演示时回"桌面"用）。
        ///
        /// ── 为什么不重播开机视频 ──────────────────────────────────────
        /// 退出 PPT/视频时如果重播 `boot.mp4`，看起来像"又开机了一遍"
        /// （2026-10-27 用户报的 bug）。
        /// 真实电脑开机后停在**桌面**不动，所以这里贴一张 `desktop.jpg`
        /// （由 `boot.mp4` 的末帧提取），视觉上等于"开机完成后的待机状态"。
        ///
        /// ── 为什么复用 ScreenRT ───────────────────────────────────────
        /// 显示器的贴图必须经过"图集合成"才能保住屏幕遮罩（不然整个机身会显示图）。
        /// `ScreenRT` + `_composite` 就是干这个的，直接拿来用，不用另建一套。
        /// 幕布的着色器自己算 UV，直接贴 `_MainTex` 即可。
        /// </summary>
        public bool ShowStill(string fileName)
        {
            if (TargetMaterial == null) return false;

            // 屏幕关着就别点亮它。下面会写 `EmissionId = _onColor`，
            // 不加这一句，退出一瞬间幕布/显示器会被硬点成亮的（看着像闪一下）。
            if (!_on) return false;

            // ══════════════════════════════════════════════════════════════
            // 【P29 2026-11-05】**幂等**：已经在显示同一张待机图就直接返回。
            //
            // 下面这段是 `File.ReadAllBytes` + `Texture2D.LoadImage`，
            // 1920×1200 的 JPG 解码一次是毫秒级。而调用方
            // `DetachSeatScreens` 在 18 个座位开机时会被调 18 次
            // → 18 次读盘 + 18 次解码 + 18 次 `_player.Stop()`。
            //
            // 更要紧的是那句 `Stop()`：它把机房屏正在飞的 `Prepare()`
            // 打断。日志里"刚就绪又未就绪"的一部分就来自这里。
            //
            // 判据：`_showingStill` 为真且文件没换 → 画面已经是它了，不用重来。
            // ══════════════════════════════════════════════════════════════
            if (_showingStill && string.Equals(_stillFile, fileName, StringComparison.Ordinal))
                return true;

            string full = PcMediaLibrary.Resolve(fileName);
            if (string.IsNullOrEmpty(full) || !File.Exists(full))
            {
                // 没有桌面图就安静放着不动 —— 比黑屏好
                Debug.Log("[P26] 找不到待机图「" + full + "」，画面保持不变。");
                return false;
            }

            byte[] bytes;
            try { bytes = File.ReadAllBytes(full); }
            catch (Exception e)
            {
                Debug.LogWarning("[P26] 读待机图失败：" + e.Message);
                return false;
            }

            Texture2D tex = new Texture2D(2, 2, TextureFormat.RGB24, false);
            if (!tex.LoadImage(bytes))
            {
                UnityEngine.Object.Destroy(tex);
                Debug.LogWarning("[P26] 解码待机图失败：" + full);
                return false;
            }

            // 停掉视频，否则还在后台解码浪费性能
            if (_player != null && _player.isPlaying) _player.Stop();

            float aspect = tex.height > 0 ? (float)tex.width / tex.height : 1.6f;

            // 【统一走 ScreenRT】两条路径都先合成进 RT，再改材质指向。
            // 反面教材：幕布分支曾直接 `SetTexture("_MainTex", tex)`，
            // 后面又 `Destroy(tex)` —— 材质会拿着一个已销毁的贴图（粉色/黑屏）。
            // 而且幕布的 RT 是整幅（`uvMin=(0,0) uvMax=(1,1)`），跟视频路径完全一致，
            // 没必要单独走一套。
            if (ScreenRT != null && _composite != null)
            {
                float rectAspect = Mathf.Max(1e-5f, _rectAspect);
                _composite.SetFloat("_Fit", aspect / rectAspect);
                Graphics.Blit(tex, ScreenRT, _composite);

                if (TargetMaterial.HasProperty("_Contain"))
                {
                    // 幕布着色器自己算 UV，贴 RT 即可。`_Contain=0` = cover 铺满，
                    // 和开机视频的观感一致（真实屏幕也不会留边）。
                    TargetMaterial.SetTexture("_MainTex", ScreenRT);
                    TargetMaterial.SetFloat("_ImageAspect", rectAspect);
                    TargetMaterial.SetFloat("_Contain", 0f);
                }
                else if (TargetMaterial.HasProperty("_EmissionMap"))
                {
                    // 显示器：RT 里已经合成好了屏幕遮罩，机身不会亮
                    TargetMaterial.SetTexture("_EmissionMap", ScreenRT);
                }
            }

            TargetMaterial.SetColor(EmissionId, _onColor);
            _showingStill = true;
            _stillFile = fileName;      // 【P29】记下来供幂等判据用

            // 拷进 RT 之后源纹理就没用了，立刻释放（1920×1200 一张 6.6MB）
            UnityEngine.Object.Destroy(tex);

            Debug.Log("[P26] " + Name + " 显示待机图：" + fileName);
            return true;
        }

        /// <summary>
        /// 把材质的贴图**收回本播放器**。
        ///
        /// ── 为什么必须有这一句 ──────────────────────────────────────────
        /// PPT 模式会往同一个材质上贴 PPT 图（显示器贴合成图集、幕布贴原图）。
        /// 退出 PPT 后如果只 `PlayFile` 改 `url`，**贴图还停在 PPT 那张**，
        /// 表现就是"重新开关机，材质也没变回来"（2026-10-27 用户实测）。
        ///
        /// 视频播放靠 `_EmissionMap`（显示器）/ `_MainTex`（幕布）指向自己的
        /// `ScreenRT` —— 必须显式设回去，视频才有地方输出。
        /// </summary>
        public void RestoreTargetTexture()
        {
            if (TargetMaterial == null || ScreenRT == null) return;

            // ⚠️ 同样必须按**着色器种类**分流。
            // `Standard` 的 `_MainTex` 是 Albedo，贴图集上去会把整个模型染黑/染色。
            if (TargetMaterial.HasProperty("_Contain"))
            {
                // 幕布：`_MainTex` 就是画面
                TargetMaterial.SetTexture("_MainTex", ScreenRT);
                TargetMaterial.SetFloat("_Contain", 0f);   // 视频用 cover
            }
            else if (TargetMaterial.HasProperty("_EmissionMap"))
            {
                // 显示器：只动自发光
                TargetMaterial.SetTexture("_EmissionMap", ScreenRT);
            }
        }

        /// <summary>
        /// 【P29 2026-11-05】这块屏**需不需要**换成这个文件。
        ///
        /// 调用方（`TwinDevicePresenter.ApplyScreen` 的座位分支）用它来回答
        /// "现在该不该起播"，代替原来"每次进分支都无条件 PlayFile"。
        ///
        /// ── 为什么不能直接调 `PlayFile` 然后让它自己幂等 ──────────────────
        /// `PlayFile` 现在确实幂等了，但调用方还需要知道"这次调用有没有真的
        /// 起播"，用来置 `_bulkBootStarted`。返回值 `true` 是"调用成功"，
        /// 不是"这次真的起了播"——两者混用会让批量窗口的逻辑失真。
        ///
        /// 判据：文件不同、或当前是待机图、或从没准备过 → 需要换。
        /// 已就绪/正在准备同一个文件 → 不需要。
        /// </summary>
        public bool NeedsFile(string fileName)
        {
            if (!HasVideo) return false;
            if (string.IsNullOrEmpty(fileName)) return false;

            // 待机图状态下必须换回视频（`_currentFile` 可能还是同一个，
            // 但画面停在桌面图上 —— 这是 ShowStill 造成的"同文件不同画面"）
            if (_showingStill) return true;

            if (!string.Equals(_currentFile, fileName, StringComparison.Ordinal)) return true;

            // 同一个文件：只有在"既没就绪、也没在准备"时才需要重新起
            if (_player == null) return true;
            if (_player.isPrepared) return false;
            if (_prepareStarted) return false;      // 正在飞，别打断
            return true;
        }

        /// <summary>
        /// 【P29 2026-11-05】是否正处于"已发起 Prepare、还没收到完成回调"。
        ///
        /// 调用方靠它来决定**要不要去碰这块屏**：
        /// 准备中的屏一旦被 `ShowStill`/`PlayFile` 碰到，`_player.Stop()` 或
        /// 再次 `Prepare()` 都会把它作废 —— 那正是 17:38 那次
        /// 「4 块屏同时 3 秒超时」的直接成因。
        ///
        /// 判据不能写 `!isPrepared`：那个值在"从未开始"时同样是 false。
        /// </summary>
        public bool IsPreparing { get { return _prepareStarted && HasVideo; } }

        /// <summary>
        /// 【P26】把播放器停掉但不改自发光（进 PPT 模式时用）。
        /// `SetOn(false)` 会把 `_on` 置 false 并清黑，退出 PPT 后要重新开机流程，
        /// 这里只停画面，回来直接 `Play()` 就行。
        /// </summary>
        public void HaltForOverlay()
        {
            if (_player != null && _player.isPlaying) _player.Pause();
        }

        /// <summary>换一段视频接着播（"打开PPT"/"视频播放"用）。
        /// 文件不存在就返回 false 并打日志 —— 素材是后补的，不该因此崩。
        ///
        /// ══════════════════════════════════════════════════════════════════
        /// 【P29 2026-11-05】**幂等**：同一个文件已经在放/正在 prepare 就直接返回。
        ///
        /// 这条是整个「机房交互又坏一次」的直接修复点。原来的实现无条件
        /// `url = full; Prepare();` —— 而 `Prepare()` 会**作废上一次还没完成的
        /// prepare**。于是调用方只要每帧调一次，`prepareCompleted` 就永远
        /// 等不到，`prepared` 一直是 False。
        ///
        /// 现场证据（Editor.log，同一块屏相邻两行）：
        ///     屏幕视频就绪：ClassComputer_PC_Master_01
        ///     ClassComputer_PC_Master_01 尚未就绪，重新 Prepare 并在回调里播放
        /// 刚就绪又被打回未就绪 —— 只有"就绪后立刻又 Prepare 一次"能解释。
        ///
        /// 调用方是 `TwinDevicePresenter.ApplyScreen` 的座位分支：
        ///     18 个座位依次开机 → 每个都进这个分支 → 每个都 PlayFile 一次
        ///     → 18 次连续 Prepare，前 17 次全被打断
        /// → 3 秒超时 → 重试 2 次 → 4 块屏同时放弃（截图里那 4 条红字）。
        ///
        /// ── 判据为什么是「已就绪 或 正在准备」两层 ──────────────────────
        /// 只看 `_currentFile == fileName` 不够：那个字段在 `Prepare()` 之前
        /// 就赋值了，此时并**没有**准备好的画面。必须再看 `isPrepared`，
        /// 只有"同一个文件 且 已经就绪"才算真的不用重来。
        /// 「正在准备中」则用 `IsPreparing()` 判 —— 它检查 `isPrepared == false`
        /// 但我们确实已经发起过 prepare（不是从未开始），此时再 Prepare 一次
        /// 仍然是打断，所以要放行过去。
        ///
        /// ⚠️ 不幂等的代价不只是掉帧：每次打断都重新开一次解码会话，
        /// 4 块屏 × 18 次 = 72 次 Prepare，解码器在这种压力下会真的放弃。
        /// ══════════════════════════════════════════════════════════════════
        public bool PlayFile(string fileName)
        {
            if (!HasVideo || _player == null) return false;
            if (string.IsNullOrEmpty(fileName)) return false;

            string full = PcMediaLibrary.Resolve(fileName);
            if (!File.Exists(full))
            {
                Debug.Log("[P26] 找不到视频「" + full + "」，画面保持不变。\n" +
                          "   放素材的位置：" + PcMediaLibrary.FolderPath + "\n" +
                          "   " + PcMediaLibrary.Describe());
                return false;
            }

            // ── 幂等早退 ────────────────────────────────────────────────
            // ① 同一个文件 + 已就绪 + 没被切成待机图 → 完全不需要动
            // ② 同一个文件 + 已在准备中（`_prepareStarted` 为真但还没就绪）
            //    → 再 Prepare 只会打断自己，直接放行
            bool sameFile = string.Equals(_currentFile, fileName, StringComparison.Ordinal);
            if (sameFile && !_showingStill)
            {
                if (_player.isPrepared || _prepareStarted)
                {
                    RestoreTargetTexture();     // 从 PPT 退回来时材质可能还贴着 PPT 图
                    if (_on && _player.isPrepared && !_player.isPlaying)
                    {
                        RestartIfFinished();
                        _player.Play();
                        if (TargetMaterial != null) TargetMaterial.SetColor(EmissionId, _onColor);
                    }
                    return true;                // ← 关键：不再 Prepare
                }
            }

            _currentFile = fileName;
            _showingStill = false;
            _stillFile = null;                  // 【P29】切回视频，待机图记录作废
            _player.url = full;
            _player.Prepare();
            _prepareStarted = true;             // 见幂等早退 ②
            NotePrepare("PlayFile");             // 【P30】计数

            // 【P26】把贴图收回本播放器。
            // 从 PPT 模式退出来时材质上贴的还是 PPT 那张图，
            // 不设回 `ScreenRT` 的话视频画面根本没地方输出。
            RestoreTargetTexture();

            // 【只在屏幕开着时才播】关机状态下换片不该把屏幕点亮。
            // 2026-10-27：原来这里无条件 `Play()` + 设亮色，
            // 若从别的入口（总控面板关屏后再换片）调到，屏幕会自己亮起来。
            // 关机时只换 url，下次 `SetOn(true)` 会按"回默认视频"的规则处理。
            if (_on)
            {
                _player.Play();      // Unity 会等 prepare 完成后再真正出画面
                if (TargetMaterial != null) TargetMaterial.SetColor(EmissionId, _onColor);
            }

            Debug.Log("[P26] 切视频 → " + full + (_on ? "" : "（屏幕未开，仅换片不播）"));
            return true;
        }

        /// <summary>每帧调用：把视频合成进 screenRT。</summary>
        public void Tick()
        {
            if (!HasVideo || !_on) return;

            // 已经放弃的屏不再进任何重试（见字段 _gaveUp 的说明）
            if (_gaveUp) return;

            // 【待机图期间不许动画面】2026-10-27 用户报"退出 PPT 又播了一遍开机"。
            // `Stop()` 之后 `_videoRT` 里还留着最后一帧，这里每帧无条件 blit
            // 会把 `ShowStill` 刚合进去的桌面图立刻盖掉 —— 看起来就像视频又开始了。
            if (_showingStill) return;

            if (_player == null || _videoRT == null || ScreenRT == null || _composite == null) return;

            if (!_player.isPrepared)
            {
                // 尚未就绪：什么都不做，等 `OnPreparedPlay` 回调。
                //
                // ══════════════════════════════════════════════════════════
                // 【P26 2026-11-05】重试必须**有上限**，否则它不是兜底而是刷屏。
                //
                // 原来的写法是"每 3 秒重试一次，永不放弃"，代价是：
                //   · `prepared=False` 一旦成立（解码器起不来），就 3 秒一条
                //     LogWarning 无限打下去 —— 主理人实测 Console 里堆到 255 条，
                //     把真正的错误全埋掉了。**日志的用处是定位，不是复读。**
                //   · 每次 `Prepare()` 都会重新开一次解码会话。失败状态下反复开，
                //     等于持续给已经紧张的解码器加压，把"偶发失败"变成"永久失败"。
                //
                // 现在改成：最多重试 `MaxPrepareRetries` 次，之后**放弃并清屏**，
                // 只留**一条**明确的错误日志（说明"这块屏这次彻底起不来了"），
                // 让画面回落到"关机态/纯色"，而不是永远卡在半黑。
                // 想再试只能重新开关一次屏（`SetOn` 会重置计数）——
                // 这符合真实设备的直觉：屏幕坏了就断电重来，不是无限重连。
                // ══════════════════════════════════════════════════════════
                if (_prepareRetryAt < 0f) _prepareRetryAt = Time.unscaledTime + 3f;
                else if (Time.unscaledTime >= _prepareRetryAt)
                {
                    _prepareRetryAt = -1f;

                    if (_prepareRetries >= MaxPrepareRetries)
                    {
                        // 彻底放弃：只报一次，之后不再进这个分支
                        //
                        // 【P30 2026-11-05】最后一次机会：**换一个播放器实例**。
                        //
                        // 前面 2 次重试都是"再 Prepare 一次"，而失败的那个对象
                        // 本身已经不工作了 —— 在它身上重试多少次都一样。
                        // 这里换实例，等于给这块屏一次真正的"断电重来"。
                        // `_rebuildCount` 上限 2，防止变成"重建→失败→重建"的新死循环。
                        if (RebuildPlayer()) return;

                        // 重建也救不回来才算真死。
                        // 错误文案按**证据**分情况，不再断言单一原因。
                        Debug.LogError("[P30] " + Name + " 视频起不来（已重试 " +
                                       _prepareRetries + " 次、重建 " + _rebuildCount + " 次）。\n" +
                                       "   现场状态：" + DebugState() + "\n" +
                                       "   " + SessionPrepareReport() + "\n" +
                                       "   已排除与已确认（按证据，不是按猜测）：\n" +
                                       "   ① 不是【被反复 Prepare 打断】—— PlayFile 幂等 + " +
                                       "NeedsFile 已生效，本块屏不再被连环 Prepare\n" +
                                       "   ② 不是【播放器未释放】—— Destroy() 已有调用点（OnDestroy）\n" +
                                       "   ③ 是**会话累计 Prepare 次数过多**：历史两次临界点 206 / 289 次，\n" +
                                       "      且退出重进 Play 也不恢复 → 残留在原生解码层，域重载也不清\n" +
                                       "      → **重启 Unity 编辑器**即恢复。这是编辑器侧的限制，不是代码 bug");
                        _gaveUp = true;
                        _on = false;
                        _prepareStarted = false;
                        _prepareRetryAt = -1f;
                        ClearScreen();
                        if (TargetMaterial != null) TargetMaterial.SetColor(EmissionId, Color.black);
                        return;
                    }

                    _prepareRetries++;
                    // 【P30】第一次超时不再"原地再 Prepare"（对已死的会话无效），
                    // 直接换实例。保留计数只为日志可读性。
                    Debug.LogWarning("[P30] " + Name + " 3 秒仍未就绪 —— 不原地重试，" +
                                     "直接换播放器实例（第 " + _prepareRetries + "/" +
                                     MaxPrepareRetries + " 次）。　" + SessionPrepareReport());
                    if (!RebuildPlayer())
                    {
                        // 连重建都不行（物体没了 / 路径没了），只能放弃
                        _gaveUp = true;
                        _on = false;
                        _prepareStarted = false;
                        ClearScreen();
                        if (TargetMaterial != null) TargetMaterial.SetColor(EmissionId, Color.black);
                    }
                }
                return;
            }
            _prepareRetryAt = -1f;
            _prepareStarted = false;   // 【P29】走到这里说明 isPrepared 已经是真，清标志

            // 【逐帧兜底】就绪了但没在播 → 补一次 Play。
            // 2026-10-27：`SetOn(true)` 在 `Prepare()` 完成**之前**被调用时，
            // `Play()` 是空操作，状态机会一直停在"未播"（prepared=True / playing=False）。
            if (!_player.isPlaying && _player.frame <= 0)
                _player.Play();
            if (!_dimensionKnown) UpdateFit();

            Graphics.Blit(_videoRT, ScreenRT, _composite);
            _blitCount++;
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
            // ══════════════════════════════════════════════════════════════
            // 【P26 Bug · 2026-11-05】这里原来漏了两件事，是整个「视频再也
            // 准备不出来」的最终祸根，修的时候三样一起做，缺一样都还在漏：
            //
            //   ① 只 `Stop()` 不 `Destroy(_go)` —— `_go` 上挂着 VideoPlayer
            //      和 AudioSource，`_go` 不销毁，VideoPlayer 就还活着。
            //   ② `_go` 虽然被 `Destroy`，但**没先解绑 `prepareCompleted`**。
            //      `prepareCompleted += p.OnPreparedPlay` 让 VideoPlayer 持有
            //      播放器实例的引用；不解绑的话，即便 GameObject 被销毁，
            //      委托链里的闭包/回调仍可能把旧实例吊住。
            //   ③ `_player` 字段没置 null。这个类不是 MonoBehaviour，
            //      不会因为 `_go` 被销毁就让 `_player` 变成 Unity 的"伪 null"，
            //      后续任何一处 `if (_player != null)` 判断都会**通过**，
            //      于是继续往一个已销毁的 VideoPlayer 上 Prepare → 永远不回调。
            //
            // 表现就是日志里那一串 `prepared=False frame=-1 blit=0` 每 3 秒
            // 重试一次、重试到天荒地老，因为对象已经死了、Prepare 必然石沉大海。
            // ══════════════════════════════════════════════════════════════
            if (_player != null)
            {
                _player.prepareCompleted -= OnPreparedPlay;
                // 【P30】`errorReceived` 也必须解绑 —— 它同样让 VideoPlayer
                // 持有本实例的委托，漏掉它等于旧实例仍被吊着。
                _player.errorReceived -= OnVideoError;
                if (_player.isPlaying) _player.Stop();
                // 先让 VideoPlayer 松开 targetTexture，再销毁 RT，
                // 否则 RT 仍被解码器引用，Release 会失败（RT 泄漏）。
                _player.targetTexture = null;
                _player.url = string.Empty;
                _player = null;      // 见上面 ③：必须显式置 null
            }

            // 【P30】AudioSource 跟着 `_go` 一起没，但字段要清掉：
            // 否则重建路径里的 `_audioSrc != null` 判断会拿到一个已销毁的组件。
            _audioSrc = null;

            _sessionDestroyCount++;      // 【P30】会话累计：销毁了几个
#if UNITY_EDITOR
            UnityEditor.EditorPrefs.SetInt(PrefDestroy,
                UnityEditor.EditorPrefs.GetInt(PrefDestroy, 0) + 1);
#endif

            if (ScreenRT != null) { ScreenRT.Release(); UnityEngine.Object.Destroy(ScreenRT); ScreenRT = null; }
            if (_videoRT != null) { _videoRT.Release(); UnityEngine.Object.Destroy(_videoRT); _videoRT = null; }
            if (_composite != null) { UnityEngine.Object.Destroy(_composite); _composite = null; }
            if (_go != null) { UnityEngine.Object.Destroy(_go); _go = null; }

            // 最后把"可用标志"也拉下来：外部若还持有这个实例（例如
            // `_screens` 尚未清空、或下一帧还会被 Tick 到），
            // `HasVideo=false` 能让所有入口统一早退，不会碰到已经释放的字段。
            HasVideo = false;
            TargetMaterial = null;
            _on = false;
            _prepareStarted = false;   // 【P29】对象已释放，别再让幂等早退以为"还在准备中"
            _prepareRetryAt = -1f;
        }
    }
}

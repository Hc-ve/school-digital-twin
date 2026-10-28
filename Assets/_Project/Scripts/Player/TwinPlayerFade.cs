using System.Collections.Generic;
using UnityEngine;

namespace SchoolTwin.Player
{
    /// <summary>
    /// 【P27】交互面板**压在角色身上**时，把角色模型轻微降显。
    ///
    /// ── 为什么要做"屏幕矩形相交"判断 ────────────────────────────────
    /// 第一版是"面板一出现就把角色淡到 45%"，用户实测反馈"一靠近门就隐身"——
    /// 面板固定在屏幕右侧，角色在画面中间，两者根本没重叠，角色却变成了鬼影。
    ///
    /// 现在的规则：**只有面板的屏幕矩形和角色的屏幕包围盒真的相交时才淡**，
    /// 而且只淡到 62%（能看清人，又能透出按钮）。面板挪到旁边就不淡。
    ///
    /// ── 为什么是改 alpha 而不是 SetActive(false) ───────────────────
    /// `TwinThirdPersonCamera.SetModelVisible()` 已经在用 SetActive(false) 做
    /// "相机贴脸时整体隐藏"。降显的诉求是"还能看见人，只是淡了"，两件事正交：
    /// 本类只动 alpha，不碰 activeSelf。
    ///
    /// ── 为什么必须实例化材质 ───────────────────────────────────────
    /// 角色材质是**资产**。直接改 `sharedMaterial` 会把磁盘上的 .mat 写坏。
    /// 所以走 `renderer.materials`（Unity 自动克隆实例），恢复时还原 + Destroy 实例。
    ///
    /// ⚠️ **顺序陷阱**（2026-10-28 用户报"角色变紫"就是栽在这）：
    /// 一旦访问过 `r.materials`，`r.sharedMaterials` 返回的**就是克隆体**了。
    /// 所以必须"先存 origin = sharedMaterials，再取 materials"，
    /// 恢复时"先 sharedMaterials = origin，再销毁克隆体"。顺序反了就是品红。
    ///
    /// ── 渲染管线 ───────────────────────────────────────────────────
    /// 本项目是**内置管线**（GraphicsSettings.m_CustomRenderPipeline = 0，
    /// 场景材质全是 Legacy Standard，fileID 46）。
    /// 透明化走 Standard 的 `_Mode` + `_ALPHABLEND_ON`，**不是** URP 的 `_Surface`。
    /// </summary>
    public static class TwinPlayerFade
    {
        /// <summary>降显后的 alpha。0.62 = 明显能看穿但不至于像鬼。</summary>
        private const float FadedAlpha = 0.62f;

        /// <summary>面板消失后延迟多久恢复（秒）。防边界抖动反复克隆/销毁材质。</summary>
        private const float RestoreDelay = 0.25f;

        private static bool _faded;
        private static Transform _player;
        private static Renderer[] _renderers;
        private static Camera _cam;

        private static readonly Dictionary<Renderer, Material[]> _orig =
            new Dictionary<Renderer, Material[]>(8);
        private static readonly Dictionary<Renderer, Material[]> _clones =
            new Dictionary<Renderer, Material[]>(8);

        private static float _restoreAt = -1f;

        // 投影 8 个角点用的临时数组（避免每帧 GC）
        private static readonly Vector3[] _corners = new Vector3[8];

        /// <summary>
        /// 每帧由 `TwinInteractMenu.LateUpdate` 调用。
        /// </summary>
        /// <param name="panelVisible">面板是否正在显示</param>
        /// <param name="panelScreenRect">面板在屏幕上的像素矩形（左下原点）</param>
        public static void Update(bool panelVisible, Rect panelScreenRect)
        {
            bool shouldFade = panelVisible && OverlapsPlayer(panelScreenRect);

            if (shouldFade)
            {
                _restoreAt = -1f;                       // 取消待恢复
                if (!_faded || _orig.Count == 0) ApplyFade();
            }
            else if (_faded)
            {
                if (_restoreAt < 0f) _restoreAt = Time.unscaledTime + RestoreDelay;
            }

            // 延迟恢复
            if (_faded && _restoreAt >= 0f && Time.unscaledTime >= _restoreAt)
            {
                _restoreAt = -1f;
                _faded = false;
                Restore();
            }
        }

        // ---------------------------------------------------------------- 相交判断

        /// <summary>
        /// 面板矩形和角色的屏幕包围盒有没有重叠。
        ///
        /// 角色包围盒 = 所有 Renderer 的世界包围盒 8 个角点投影到屏幕后的并集。
        /// 用包围盒而不是骨骼，是因为骨骼点分布不均、算出来会漏掉手臂和头。
        /// </summary>
        private static bool OverlapsPlayer(Rect panel)
        {
            if (!CollectRenderers()) return false;
            if (_cam == null) _cam = Camera.main;
            if (_cam == null) return false;

            bool any = false;
            float minX = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue;

            foreach (Renderer r in _renderers)
            {
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;

                Bounds b = r.bounds;
                Vector3 c = b.center;
                Vector3 e = b.extents;

                for (int i = 0; i < 8; i++)
                {
                    _corners[i] = new Vector3(
                        c.x + ((i & 1) == 0 ? -e.x : e.x),
                        c.y + ((i & 2) == 0 ? -e.y : e.y),
                        c.z + ((i & 4) == 0 ? -e.z : e.z));
                }

                for (int i = 0; i < 8; i++)
                {
                    Vector3 sp = _cam.WorldToScreenPoint(_corners[i]);
                    if (sp.z <= 0f) continue;           // 角点在相机背后，跳过

                    if (sp.x < minX) minX = sp.x;
                    if (sp.x > maxX) maxX = sp.x;
                    if (sp.y < minY) minY = sp.y;
                    if (sp.y > maxY) maxY = sp.y;
                    any = true;
                }
            }

            if (!any) return false;                          // 角色完全在相机背后

            Rect player = Rect.MinMaxRect(minX, minY, maxX, maxY);

            // 蒙皮包围盒通常比"看得见的身体"大一圈（绑定位姿的 AABB，
            // 手臂张开时更大）。直接拿来判相交会**过度触发** —— 面板离人还有
            // 一段距离就开始淡。收缩 15% 更贴近实际轮廓。
            const float shrink = 0.15f;
            player = new Rect(player.x + player.width * shrink,
                              player.y + player.height * shrink,
                              player.width * (1f - shrink * 2f),
                              player.height * (1f - shrink * 2f));

            return panel.Overlaps(player);
        }

        // ---------------------------------------------------------------- 材质

        private static void ApplyFade()
        {
            if (!CollectRenderers()) return;

            _faded = true;

            foreach (Renderer r in _renderers)
            {
                if (r == null) continue;
                if (_orig.ContainsKey(r)) continue;      // 已经克隆过，别重复

                // ⚠️ 顺序：先存原始 sharedMaterials，再取 materials。
                // 反过来的话，origin 拿到的就是克隆体，恢复时会变成品红。
                Material[] origin = r.sharedMaterials;
                Material[] inst = r.materials;           // 克隆体（改它不碰资产）
                if (inst == null || inst.Length == 0) continue;

                _orig[r] = origin;
                _clones[r] = inst;

                foreach (Material m in inst) MakeTransparent(m, FadedAlpha);
            }
        }

        private static void Restore()
        {
            foreach (KeyValuePair<Renderer, Material[]> kv in _orig)
            {
                Renderer r = kv.Key;
                if (r == null) continue;                 // 角色已被销毁

                Material[] origin = kv.Value;
                Material[] inst;
                _clones.TryGetValue(r, out inst);

                // 先装回原始材质，再销毁克隆体（顺序反了就是品红）
                if (origin != null && origin.Length > 0) r.sharedMaterials = origin;

                if (inst != null)
                {
                    for (int i = 0; i < inst.Length; i++)
                        if (inst[i] != null) UnityEngine.Object.Destroy(inst[i]);
                }
            }
            _orig.Clear();
            _clones.Clear();
        }

        /// <summary>
        /// 找角色渲染器（找一次缓存住）。
        /// 取法与 `TwinThirdPersonCamera` 一致：TwinPlayer 下面所有 Renderer。
        /// </summary>
        private static bool CollectRenderers()
        {
            if (_player == null)
            {
                ResetCache();

                // ⚠️ static class 不继承 UnityEngine.Object，必须全限定调静态方法
                SchoolTwin.Player.TwinPlayerController pc =
                    UnityEngine.Object.FindObjectOfType<SchoolTwin.Player.TwinPlayerController>();
                if (pc == null) return false;
                _player = pc.transform;
            }

            if (_renderers == null || _renderers.Length == 0)
            {
                // 带 true：贴脸隐藏时 activeSelf=false 也拿得到
                _renderers = _player.GetComponentsInChildren<Renderer>(true);
                if (_renderers.Length == 0) return false;
            }
            return true;
        }

        private static void ResetCache()
        {
            _orig.Clear();
            _clones.Clear();
            _renderers = null;
            // 不要在这里改 `_faded`：本方法在 ApplyFade 的调用链上，
            // 清掉标志会导致"刚淡上就被判成没淡"，之后永远恢复不了。
        }

        /// <summary>
        /// 把一个 Standard 材质切成 Alpha Blend 并压 alpha。
        ///
        /// Standard 的透明模式由 `_Mode`（float）+ 编译关键字共同决定：
        ///   0=Opaque 1=Cutout 2=Fade 3=Transparent
        /// 用 **2=Fade**：`_SrcAlpha/OneMinusSrcAlpha` 混合，观感最接近"玻璃人"。
        /// </summary>
        private static void MakeTransparent(Material m, float alpha)
        {
            if (m == null || !m.HasProperty("_Mode")) return;   // 非 Standard，别弄坏

            m.SetFloat("_Mode", 2f);
            m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            m.SetInt("_ZWrite", 0);
            m.DisableKeyword("_ALPHATEST_ON");
            m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            m.EnableKeyword("_ALPHABLEND_ON");
            m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;

            if (m.HasProperty("_Color"))
            {
                Color c = m.color;
                c.a = alpha;
                m.color = c;
            }
        }
    }
}

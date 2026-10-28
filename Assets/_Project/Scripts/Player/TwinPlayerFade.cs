using System.Collections.Generic;
using UnityEngine;

namespace SchoolTwin.Player
{
    /// <summary>
    /// 【P27】交互面板显示期间把**角色模型**降显（半透明），面板关了恢复。
    ///
    /// ── 为什么需要 ─────────────────────────────────────────────────
    /// 第三人称下角色就站在屏幕中部，交互面板又固定在屏幕上，
    /// 两者重叠时按钮看起来"浮在角色身上"（2026-10-28 用户报的观感问题）。
    /// 《异环》同款解法：面板一弹，主角变半透明，按钮永远清晰。
    ///
    /// ── 为什么是改 alpha 而不是 SetActive(false) ───────────────────
    /// `TwinThirdPersonCamera.SetModelVisible()` 已经在用 SetActive(false)
    /// 做"贴脸隐藏"，那会把模型**整个消失**。降显的诉求是"还能看见人，
    /// 只是淡了" —— 两件事语义不同，做成正交：这个类只动 alpha，
    /// 不碰 activeSelf，跟 SetModelVisible 互不干扰。
    ///
    /// ── 为什么必须实例化材质 ───────────────────────────────────────
    /// 角色 FBX 的材质是**资产**。直接改 `sharedMaterial` 的颜色/混合模式
    /// 会把磁盘上的 .mat 写坏（下次进场景角色永远是透明的）。
    /// 所以走 `renderer.materials`（Unity 自动克隆实例），恢复时 Destroy 实例。
    ///
    /// ── 渲染管线 ───────────────────────────────────────────────────
    /// 本项目是**内置管线**（GraphicsSettings.m_CustomRenderPipeline = 0，
    /// 场景材质全是 Legacy Standard，fileID 46）。
    /// 透明化走 Standard 的 `_Mode` 属性 + `_ALPHABLEND_ON` 关键字，
    /// **不是** URP 的 `_Surface`。两套写法不通用，换管线要改这里。
    /// </summary>
    public static class TwinPlayerFade
    {
        private const float FadedAlpha = 0.45f;

        private static bool _faded;
        private static Transform _player;
        private static Renderer[] _renderers;

        /// <summary>renderer → **原始**材质数组（来自资产，恢复时赋回去）。</summary>
        private static readonly Dictionary<Renderer, Material[]> _orig =
            new Dictionary<Renderer, Material[]>(8);

        /// <summary>renderer → 运行时克隆出来的材质实例（恢复时销毁）。</summary>
        private static readonly Dictionary<Renderer, Material[]> _clones =
            new Dictionary<Renderer, Material[]>(8);

        /// <summary>恢复的延迟时间点（秒）。防止面板在范围边界反复开关时材质抖动。</summary>
        private static float _restoreAt = -1f;
        private const float RestoreDelay = 0.25f;

        /// <summary>
        /// 面板激活 = true（角色半透明）；面板关闭 = false（延迟恢复）。
        /// 可重入：重复传同一个值是空操作。
        ///
        /// 【为什么要延迟恢复】角色站在交互范围边界上时，面板会逐帧显示/隐藏，
        /// 每帧克隆+销毁材质 → 角色闪烁（2026-10-28 用户报"材质开始闪烁"）。
        /// 关面板后等 0.25s 再恢复，抖动窗口内反复开关就一直是半透明，视觉稳定。
        /// 真正的恢复由 `Tick()` 执行（菜单 LateUpdate 每帧调）。
        /// </summary>
        public static void SetFaded(bool faded)
        {
            if (faded)
            {
                // `_orig.Count > 0` 是必要条件：没克隆成功过（比如第一次调时
                // 角色还没生成）就不能算"已经淡着"，否则会永久早退、再也淡不了。
                if (_faded && _restoreAt < 0f && _orig.Count > 0) return;
                _faded = true;
                _restoreAt = -1f;                        // 取消待恢复
                if (_orig.Count == 0) ApplyFade();       // 还没克隆过才真正动手
            }
            else
            {
                if (!_faded) return;
                if (_restoreAt < 0f) _restoreAt = Time.unscaledTime + RestoreDelay;
            }
        }

        /// <summary>由外部每帧调用，处理"延迟恢复"。</summary>
        public static void Tick()
        {
            if (!_faded || _restoreAt < 0f) return;
            if (Time.unscaledTime < _restoreAt) return;

            _restoreAt = -1f;
            _faded = false;
            Restore();
        }

        // ---------------------------------------------------------------- 实现

        private static void ApplyFade()
        {
            if (!CollectRenderers()) return;

            // CollectRenderers 可能刚 ResetCache 过，这里重新确认状态
            _faded = true;
            _restoreAt = -1f;

            foreach (Renderer r in _renderers)
            {
                if (r == null) continue;
                if (_orig.ContainsKey(r)) continue;      // 已经克隆过，别重复

                // ⚠️ 顺序很重要：**先**存原始 sharedMaterials，**再**取 materials。
                //
                // `r.materials` 会给渲染器装上克隆体，之后就再也拿不回原始引用了
                // （`r.sharedMaterials` 返回的已经是克隆体）。2026-10-28 用户报
                // "角色变紫"就是栽在这 —— 恢复时把克隆体赋回去又 Destroy 掉，
                // 渲染器握着已销毁材质 → 品红。
                Material[] origin = r.sharedMaterials;
                Material[] inst = r.materials;           // 克隆体（改它不碰资产）
                if (inst == null || inst.Length == 0) continue;

                _orig[r] = origin;
                _clones[r] = inst;

                foreach (Material m in inst)
                    MakeTransparent(m, FadedAlpha);
            }
        }

        private static void Restore()
        {
            foreach (KeyValuePair<Renderer, Material[]> kv in _orig)
            {
                Renderer r = kv.Key;
                if (r == null) continue;                 // 角色可能已被销毁

                Material[] origin = kv.Value;
                Material[] inst;
                _clones.TryGetValue(r, out inst);

                // 先把**原始**材质装回去，再销毁克隆体 —— 顺序反了就是品红。
                if (origin != null && origin.Length > 0) r.sharedMaterials = origin;

                // 克隆体一律销毁：`r.materials` 产生的实例和原始资产永远不是同一个对象，
                // 不需要比对，直接销毁即可（否则每次开关都泄漏一批材质）。
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
        /// 模型 = TwinPlayer 下面第一个带渲染器的子树，与
        /// `TwinThirdPersonCamera` L76 的取法一致。
        /// </summary>
        private static bool CollectRenderers()
        {
            if (_player == null)
            {
                // 角色没了（换场景 / 重开 Play）→ 缓存全清，否则字典里留着
                // 上一轮已销毁的 Renderer，会让 `_orig.Count == 0` 判false，
                // 结果永远不再克隆材质（"关了 Domain Reload 时踩到的坑"）。
                ResetCache();

                // ⚠️ 本类是 static class，不能直接调 FindObjectOfType ——
                // 它是 UnityEngine.Object 的静态成员，而 static class 不继承 Object。
                // 走全限定名 Object.FindObjectOfType 明确调用基类静态方法。
                SchoolTwin.Player.TwinPlayerController pc =
                    UnityEngine.Object.FindObjectOfType<SchoolTwin.Player.TwinPlayerController>();
                if (pc == null) return false;
                _player = pc.transform;
            }

            if (_renderers == null || _renderers.Length == 0)
            {
                // SkinnedMeshRenderer（蒙皮模型）+ MeshRenderer（挂件）都要。
                // GetComponentsInChildren 带 true：贴脸隐藏时 activeSelf=false 也拿得到。
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
            // 注意：不要在这里改 `_faded` —— 本方法是从 CollectRenderers 里调的，
            // 而 CollectRenderers 又在 ApplyFade 的调用链上；把标志清掉会导致
            // "刚淡上就被判成没淡"，之后 SetFaded(false) 直接早退、材质永远透明。
        }

        /// <summary>
        /// 把一个 Standard 材质切成 Alpha Blend 透明并压 alpha。
        ///
        /// Standard 的透明模式由 `_Mode`（float）+ 编译关键字共同决定：
        ///   0=Opaque 1=Cutout 2=Fade 3=Transparent
        /// 用 **2=Fade**：它做 `_SrcAlpha/OneMinusSrcAlpha` 混合且保留镜面高光衰减，
        /// 比 3=Transparent（预乘 alpha，淡出来发灰）观感更接近"玻璃人"。
        /// </summary>
        private static void MakeTransparent(Material m, float alpha)
        {
            if (m == null || !m.HasProperty("_Mode")) return;   // 非 Standard，跳过别弄坏

            m.SetFloat("_Mode", 2f);                            // Fade
            m.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            m.SetInt("_ZWrite", 0);                             // 透明物体不写深度，防排序闪烁
            m.DisableKeyword("_ALPHATEST_ON");
            m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            m.EnableKeyword("_ALPHABLEND_ON");
            m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;

            // 颜色属性：Standard 主色在 `_Color`。BaseMap 的 alpha 跟随主色。
            if (m.HasProperty("_Color"))
            {
                Color c = m.color;
                c.a = alpha;
                m.color = c;
            }
        }
    }
}

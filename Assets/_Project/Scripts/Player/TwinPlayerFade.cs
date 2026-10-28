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

        /// <summary>renderer → 淡显前的材质实例数组（恢复时还原并销毁）。</summary>
        private static readonly Dictionary<Renderer, Material[]> _saved =
            new Dictionary<Renderer, Material[]>(8);

        /// <summary>
        /// 面板激活 = true（角色半透明）；面板关闭 = false（恢复）。
        /// 可重入：重复传同一个值是空操作。
        /// </summary>
        public static void SetFaded(bool faded)
        {
            if (_faded == faded) return;
            _faded = faded;

            if (faded) ApplyFade();
            else Restore();
        }

        // ---------------------------------------------------------------- 实现

        private static void ApplyFade()
        {
            if (!CollectRenderers()) return;

            foreach (Renderer r in _renderers)
            {
                if (r == null) continue;

                // `r.materials` 返回的是**实例副本**（Unity 自动克隆），
                // 改它不会碰磁盘资产。原 sharedMaterials 备份不用存 ——
                // 恢复时直接把实例 Destroy 掉，Unity 会自动回落到 shared。
                Material[] inst = r.materials;
                _saved[r] = inst;

                foreach (Material m in inst)
                    MakeTransparent(m, FadedAlpha);
            }
        }

        private static void Restore()
        {
            foreach (KeyValuePair<Renderer, Material[]> kv in _saved)
            {
                Renderer r = kv.Key;
                Material[] inst = kv.Value;

                // 角色可能在这期间被换装/销毁（理论上不会，防御一下）
                if (r == null) continue;

                // 把实例上的 alpha 改回去再销毁？不需要 —— 直接清空实例数组，
                // Unity 找不到实例材质时自动用 sharedMaterials 渲染。
                r.materials = r.sharedMaterials;

                if (inst != null)
                    for (int i = 0; i < inst.Length; i++)
                        if (inst[i] != null)
                            UnityEngine.Object.Destroy(inst[i]);
            }
            _saved.Clear();
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

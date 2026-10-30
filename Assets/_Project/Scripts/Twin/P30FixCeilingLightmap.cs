using UnityEngine;

namespace SchoolTwin.Twin
{
    /// <summary>
    /// 【P30】二楼天花板烘焙绑定修复。
    ///
    /// ── 问题 ──────────────────────────────────────────────────────
    /// `SchoolScene/Exterior/SchoolBuilding/Floor2` 是一块 67.9 × 4.4 × 24.1m 的共享楼板，
    /// 同时充当 Class2-1 与 Class2-2 的天花板。它在烘焙数据里指向一张**全黑图集**，
    /// 于是两个教室的天花板是纯黑、灯管看起来浮在空中。
    ///
    /// ── 为什么只能运行时改 ────────────────────────────────────────
    /// `Renderer.lightmapIndex` 存在 `LightingData.asset` 的二进制里，场景 `.unity` 中
    /// **0 处 `m_LightmapIndex` 文本字段**（已实测），保存场景无法持久化 ——
    /// 只有在 `Awake()` 里赋值才有效。
    ///
    /// ── 为什么是 -1 ───────────────────────────────────────────────
    /// `-1` = 该渲染器**不参与 lightmap**，回落到实时光照 + 环境光，
    /// 呈现自然混凝土质感。视觉基准见 `Docs/QA/evidence/P30_fix_Class2-1_bypass_only.png`。
    ///
    /// ── 三条硬约束（别改）──────────────────────────────────────────
    /// 1. **不许用 `GameObject.Find` 按路径找对象** —— 场景 12 万行、184 个重名对象，
    ///    路径不可靠（P00 §1.6 C3）。必须在 Inspector 里拖引用，序列化进场景。
    /// 2. **只改 `lightmapIndex`** —— 不碰材质、不碰 RenderSettings、不遍历其它 Renderer。
    ///    全场景 1992 个 Renderer 大多是合法烘焙，批量改会毁掉整片画面。
    /// 3. **不加 `[ExecuteAlways]`** —— 那会让编辑器里也执行，弄脏场景。
    ///
    /// ── 回滚 ──────────────────────────────────────────────────────
    /// 禁用或删除本组件即可，不涉及任何资产。
    ///
    /// ── 挂载 ──────────────────────────────────────────────────────
    /// 挂在场景里任意一个**常驻** GameObject 上；`target` 拖那块 Floor2 楼板。
    /// （规格原文写「如 [TwinRuntime]」，但该对象是 `TwinRuntime.Ensure()` 运行时创建的、
    ///   场景里并不存在，实际请挂到 `TwinPlayer` 或 `[TwinUI]` 这类场景常驻对象上。）
    ///
    /// 【与规格原文的差异】原文未给 namespace，这里按项目规范归入 `SchoolTwin.Twin`。
    /// 不影响挂载与 Inspector 显示；若要逐字一致，删掉 namespace 那两行即可，逻辑不变。
    /// </summary>
    [AddComponentMenu("SchoolTwin/P30 天花板烘焙修复 (P30FixCeilingLightmap)")]
    [DisallowMultipleComponent]
    public class P30FixCeilingLightmap : MonoBehaviour
    {
        [Tooltip("拖 SchoolScene/Exterior/SchoolBuilding/Floor2 这块楼板的 Renderer。" +
                 "不要留空 —— 留空则什么都不做（只在 Console 提示一次）。")]
        [SerializeField] private Renderer target;

        private void Awake()
        {
            if (target == null)
            {
                Debug.LogWarning("[P30] P30FixCeilingLightmap 的 target 未拖引用，天花板修复不生效。" +
                                 "请在 Inspector 里把 Floor2 楼板的 Renderer 拖进来。");
                return;
            }

            // 已经是 -1 就不重复写（避免任何不必要的脏标记）
            if (target.lightmapIndex != -1) target.lightmapIndex = -1;
        }
    }
}

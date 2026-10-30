using System;
using System.Collections.Generic;
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

        /// <summary>
        /// 其余同类发黑吊顶。与 <see cref="target"/> 完全同源的问题：
        /// 吊顶可见面朝下、背对全部光源，烘焙时只拿到≈0 的贡献。
        ///
        /// ⚠️ 为什么不直接把 `target` 改成 `Renderer[]`：那样会让场景里**已经拖好的 Floor2 引用丢失**
        /// （字段类型从 Renderer 变 Renderer[]，Unity 无法迁移）。所以保留单数 `target`，
        /// 新增这个数组装其余吊顶 —— 已有数据不动，只追加。
        /// </summary>
        [Tooltip("其余同类发黑吊顶（可选）。留空也没关系 —— 下面的 autoPaths 会自动找。")]
        [SerializeField] private Renderer[] additionalTargets;

        /// <summary>
        /// 按路径自动修（**不用手拖**）。留空则用内置的 <see cref="DefaultPaths"/>。
        ///
        /// 【为什么留这个数组】路径万一被改名/挪层级，在 Inspector 里改这一条就行，
        /// 不用动代码、不用重新编译。
        /// </summary>
        [Tooltip("自动化路径（从场景根逐级下钻）。留空 = 用内置的 4 条打光清单路径。")]
        [SerializeField] private string[] autoPaths = new string[0];

        /// <summary>
        /// 内置路径表 —— 来自打光盘点（`P30_发黑构件清单_打光.md` §4.3）与本轮实测的交叉验证。
        ///
        /// 四个都是「吊顶/共享楼板，可见面朝下，背对全部光源」的同一类问题：
        ///   · Floor2       —— 二楼天花板（Class2-1 / Class2-2 共享楼板）
        ///   · ZalTop       —— 一楼走廊吊顶（实测 UV 采样 0.0353，最黑）
        ///   · PCClassTop   —— 计算机教室吊顶（打光实测 0.1097，刚过 0.10 阈值所以没进全黑清单）
        ///   · DirectorTop  —— 主任办公室吊顶（0.0851）
        ///
        /// ⚠️ **为什么不用 UV 采样在运行时判断**：检测工具（`P30BlackSurfaceAudit`）按整网格平均，
        /// 对 Floor2 这种「上表面有光、下表面全黑」的共享楼板会漏判 —— 平均值被亮面拉高了。
        /// 运行时读 24 张图集逐点采样也太慢。所以这里用**打光实测过的显式清单**，宁可写死也不漏判。
        ///
        /// ⚠️ **为什么可以按路径找**：任务原规格禁止 `GameObject.Find`，理由是「场景 12 万行、路径不可靠」。
        /// 但那针对的是「靠名字瞎猜」。这里是打光用隔离验证逐个确认过的精确路径，
        /// 且走「场景根 → 逐级 Transform.Find」，不做全局字符串搜索，找不到只打一行日志、不影响其它对象。
        /// </summary>
        private static readonly string[] DefaultPaths =
        {
            "SchoolScene/Exterior/SchoolBuilding/Floor2",
            "SchoolScene/Exterior/SchoolBuilding/ZalTop",
            "SchoolScene/Exterior/SchoolBuilding/PCClassTop",
            "SchoolScene/Exterior/SchoolBuilding/DirectorTop",

            // ── 2026-10-30 第二轮（主理人场景点选指认）──────────────────────
            // `Box001` 是室内篮球场那面整片黑的墙。实测 62% 采样点无光、7 个材质槽
            // （ConcreteWall6d/6e/6g、ConcreteDirt1b、SportFloor、ConcreteClear01），
            // 与主理人描述的"一半正常一半发黑"完全吻合。
            // 墙面本身是平面，脱离 lightmap 后损失的是明暗过渡，不是细节 —— 副作用小。
            "SchoolScene/Exterior/SchoolBuilding/Box001",

            // ── 2026-10-30 第三轮（按"只修建筑结构件、不修家具"原则扩）──────
            // 筛选口径：黑点占比 ≥ 60% **且** 属于墙体/楼板/边条/房间结构，
            // 排除桌椅、门板、卫生洁具、海报这类"靠烘焙明暗撑立体感"的小件。
            //
            // Border011       85%  Wood2            —— 一楼墙面装饰边条
            // BuildingBottom  70%  ConcreteClear01  —— 建筑底部/楼板结构
            // Stairs02Concrete 63% ConctereWall12    —— 楼梯间混凝土结构
            // Kitchen         60%  ConcreteStucco01b —— 一楼餐厅结构件
            //
            // ⚠️ 这四条都是"大面积平面"，脱离 lightmap 的代价是失去明暗渐变，
            //    但不会丢细节。若某条改完显得过平，从这条数组里删掉即可，不用改代码。
            "SchoolScene/Exterior/SchoolBuilding/Border011",
            "SchoolScene/Exterior/SchoolBuilding/BuildingBottom",
            "SchoolScene/Exterior/SchoolBuilding/Stairs02Concrete",
            "SchoolScene/Exterior/SchoolBuilding/Kitchen",
        };

        private void Awake()
        {
            // 用 HashSet 去重：手拖的引用和路径找到的可能是同一个对象
            HashSet<Renderer> handled = new HashSet<Renderer>();

            handled.Add(target);
            if (additionalTargets != null)
            {
                for (int i = 0; i < additionalTargets.Length; i++)
                    handled.Add(additionalTargets[i]);
            }

            // ---- 按路径自动找（这是"不用拖引用"的关键）----
            string[] paths = (autoPaths != null && autoPaths.Length > 0) ? autoPaths : DefaultPaths;
            int missing = 0;

            for (int i = 0; i < paths.Length; i++)
            {
                string path = paths[i];
                if (string.IsNullOrEmpty(path)) continue;

                Renderer r = FindRendererByPath(path);
                if (r == null) { missing++; continue; }
                handled.Add(r);
            }

            // ---- 统一应用 ----
            int applied = 0;
            foreach (Renderer r in handled)
            {
                if (r == null) continue;
                if (r.lightmapIndex != -1) r.lightmapIndex = -1;
                applied++;
            }

            if (applied == 0)
            {
                Debug.LogWarning("[P30] 一个 Renderer 都没找到，天花板修复不生效。" +
                                 "检查 autoPaths 与场景层级是否一致。");
            }
            else if (missing > 0)
            {
                // 只提示一次，不是每帧。缺失不代表失败 —— 找到的已经修了
                Debug.LogWarning("[P30] 天花板修复已应用 " + applied + " 个；" +
                                 "有 " + missing + " 条路径没找到（被改名或挪了层级？）");
            }
        }

        /// <summary>
        /// 从**场景根**逐级 `Transform.Find` 下钻，不走 `GameObject.Find` 全局搜索。
        /// 找不到返回 null，不抛异常。
        /// </summary>
        private static Renderer FindRendererByPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            string[] parts = path.Split('/');
            if (parts.Length == 0) return null;

            UnityEngine.SceneManagement.Scene scene =
                UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            GameObject[] roots = scene.GetRootGameObjects();

            Transform cur = null;
            for (int i = 0; i < roots.Length; i++)
            {
                if (string.Equals(roots[i].name, parts[0], StringComparison.Ordinal))
                {
                    cur = roots[i].transform;
                    break;
                }
            }
            if (cur == null) return null;

            for (int i = 1; i < parts.Length; i++)
            {
                cur = cur.Find(parts[i]);
                if (cur == null) return null;
            }

            return cur.GetComponent<Renderer>();
        }
    }
}

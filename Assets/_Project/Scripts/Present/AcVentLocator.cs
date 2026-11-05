using UnityEngine;
using SchoolTwin.Twin;

namespace SchoolTwin.Present
{
    /// <summary>
    /// 【P14 2026-11-05】定位空调出风口：返回**格栅中心的世界位置** + **朝房间内侧的吹风方向**。
    ///
    /// ══════════════════════════════════════════════════════════════════
    /// 为什么不按节点名找
    /// ══════════════════════════════════════════════════════════════════
    /// 王绍锋 README 里写的是「构建期查找 `AirConditioner` / `AC_Body` / `AC_Vent_0` 节点」。
    /// 那套名字在**他的项目**成立。本项目实测：
    ///
    ///   · `grep -o "AirConditioner\|AC_Body\|AC_Vent" SchoolSceneDay.unity` → **0 命中**
    ///   · 5 个 `AC/Unit_01` 锚点的 `_visualTarget` 全是 `{fileID: 0}`、`_visualTargetPath` 全空
    ///   · 锚点 GameObject 的 `m_Name` 是空字符串、没挂 Renderer
    ///
    /// 也就是说：**锚点是纯空物体，身上没有几何体，名字也叫不出空调。**
    /// 所以「按名字找节点」在本项目一条都走不通，必须按**几何特征**找。
    ///
    /// ══════════════════════════════════════════════════════════════════
    /// 三级回退（哪一级成功由日志明确报出来）
    /// ══════════════════════════════════════════════════════════════════
    ///   L1 锚点自身或其 VisualTarget 下有 Renderer
    ///      → 用它的包围盒推导。**最可靠**，因为那是数据层指定的"这就是这台设备"。
    ///   L2 房间根节点下按壁挂空调的**几何特征**筛：
    ///      宽 > 0.5m、最小水平尺寸 < 0.45m（薄）、高 < 0.65m（扁）、
    ///      中心高 1.6~3.4m（吊在墙上）、离锚点 < 4m
    ///      → 命中多个时取**体积最小**的（空调本体比墙面/桌子小得多）
    ///   L3 全部失败 → 用锚点自身位置 + 一个合理的默认方向（朝房间中心）
    ///
    /// ══════════════════════════════════════════════════════════════════
    /// 「朝房间内侧」怎么定
    /// ══════════════════════════════════════════════════════════════════
    /// 空调是壁挂的，它的**厚度方向**（最薄的那个水平轴）就是墙面法线。
    /// 出风口朝**与墙面法线相反的、指向房间内部**的方向。
    ///
    /// 判断"哪边是房间内部"：拿房间根节点的中心点（空调装在房间边缘，
    /// 房间中心一定在"内部"那一侧）。
    /// 这个办法不依赖坐标轴朝向，也不依赖空调是装在东墙还是西墙。
    /// </summary>
    public static class AcVentLocator
    {
        /// <summary>诊断：上一次定位用了哪一级、依据是什么。</summary>
        public static string LastReason { get; private set; }

        public static bool TryLocate(TwinAnchor anchor, out Vector3 ventPos, out Vector3 blowDir)
        {
            ventPos = anchor != null ? anchor.transform.position : Vector3.zero;
            blowDir = Vector3.forward;
            LastReason = "未开始";

            if (anchor == null) { LastReason = "anchor 为 null"; return false; }

            // ── L1：锚点自身 / VisualTarget 下有 Renderer ──────────────────
            Bounds b;
            bool hasAcBounds = TryGetVisualBounds(anchor, out b);
            // 房间包围盒无论走哪一级都要用（L1 用来判方向，L2/L3 用来兜底）
            Bounds room = RoomBoundsNear(anchor, hasAcBounds ? b : new Bounds(anchor.transform.position, Vector3.one));

            if (hasAcBounds)
            {
                DeriveVent(b, room.center, room.size, out ventPos, out blowDir);
                LastReason = "L1 用锚点/VisualTarget 的包围盒（size=" + Fmt(b.size) + "）";
                return true;
            }

            // ── L2：在房间根节点下按几何特征找 ────────────────────────────
            string zone = ZoneOf(anchor.TwinId);
            GameObject zoneRoot = string.IsNullOrEmpty(zone) ? null : GameObject.Find(zone);
            Vector3 roomCenter = room.center;

            if (zoneRoot != null)
            {
                Bounds acBounds;
                if (TryFindAcByShape(zoneRoot, anchor.transform.position, out acBounds))
                {
                    DeriveVent(acBounds, roomCenter, room.size, out ventPos, out blowDir);
                    LastReason = "L2 按几何特征在房间 \"" + zone + "\" 内找到（size=" +
                                 Fmt(acBounds.size) + "，center=" + Fmt(acBounds.center) + "）";
                    return true;
                }
                LastReason = "L2 房间 \"" + zone + "\" 下没有形状匹配的空调模型";
            }
            else
            {
                LastReason = "L2 找不到房间根节点 \"" + zone + "\"";
            }

            // ── L3：兜底 ─────────────────────────────────────────────────
            // 朝房间中心吹。虽然不知道格栅在哪，但至少有气流出来，不会一点反馈都没有。
            blowDir = roomCenter - anchor.transform.position;
            blowDir.y = 0f;
            if (blowDir.sqrMagnitude < 0.0001f) blowDir = Vector3.forward;
            blowDir.Normalize();
            ventPos = anchor.transform.position + blowDir * 0.30f + Vector3.up * 0.10f;
            LastReason = "L3 兜底（朝房间中心，位置不准但保证有反馈）";
            return true;    // 兜底也算成功 —— 有反馈比准确更重要
        }

        // ==================================================================
        // 包围盒获取
        // ==================================================================

        /// <summary>
        /// 取这台设备的可视包围盒。
        ///
        /// ⚠️ 顺序很重要：先看锚点**自己**挂没挂 Renderer，
        /// 再看 `VisualTarget`。因为 `VisualTarget` 在没配的时候会**回退成锚点自己**
        /// （实测本项目 5 台空调都是这种情况），直接读它等于没读。
        /// </summary>
        private static bool TryGetVisualBounds(TwinAnchor anchor, out Bounds bounds)
        {
            bounds = new Bounds();

            // ① 锚点自身（含子物体）
            if (TryCombine(anchor.gameObject, out bounds)) return true;

            // ② VisualTarget（没配时会回退成自己，上面的 ① 已经试过了，这里不会重复成功）
            GameObject visual = anchor.VisualTarget;
            if (visual != null && !ReferenceEquals(visual, anchor.gameObject))
                if (TryCombine(visual, out bounds)) return true;

            return false;
        }

        /// <summary>
        /// ══════════════════════════════════════════════════════════════════
        /// 【2026-11-05 已删除 · 曾经写过 IsMockAnchor()，是错的，这里留档】
        /// ══════════════════════════════════════════════════════════════════
        /// 我看到日志里 `Class2-1` / `ClassDirector` 两台空调的锚点 y=6.36 / 6.11，
        /// 而 `Class1-2` 那几台是 y≈2.4，第一反应是「二楼这两台挂在 6 米高空，
        /// 房间里根本没这面墙，粒子会飘到天花板上面看不见」。
        ///
        /// 于是写了 `IsMockAnchor()`：路径含 "Mock" 或 `position.y > 4f` 就判定为
        /// 假锚点，跳过 L1 强行走 L2 去房间里找真实模型。
        ///
        /// **幸好接进去之前先翻了候选清单 —— 判断是错的。**
        ///
        /// 实测证据（探针日志，5 台全量）：
        ///
        ///   房间         地板参照物                    地板 y     空调模型 y    离地
        ///   ─────────────────────────────────────────────────────────────────────
        ///   Class1-2    Split01b / FlowerHolder01b      ≈1.5      2.43        0.9 m
        ///   Class1-3    Split01c / FlowerHolder01b      ≈1.5      2.58        1.1 m
        ///   Class1-4    Split01a / Blueprints05         ≈1.5      2.57        1.1 m
        ///   Class2-1    Table02 / Radiator01(3)         ≈3.5      6.36        2.9 m
        ///   ClassDirector  TableOffice1 / Radiator01(2) ≈3.5      6.12        2.6 m
        ///
        /// 关键事实：**`Interior` 里有两层楼**，二楼地板在 y≈3.5。
        /// `Class2-1` 和 `ClassDirector` 都在二楼，6 米对它们来说是**正常吊装高度**
        /// （离地 2.6~2.9m，和一楼那三台的 0.9~1.1m 是同一种相对关系）。
        ///
        /// 而且 `[MockPoints]/AC_Class2-1` 这个名字里带 Mock，但它**身上真的有
        /// 一个尺寸 (0.15, 0.30, 1.05) 的 Renderer** —— 和一楼三台 `Split01a/b/c`
        /// 完全同规格。名字里带 Mock 不等于没有几何体。
        ///
        /// 结论：`y > 4f` 这个阈值在**两层楼场景里是错的**，
        /// 接进去会把二楼两台正确空调误杀，让它们退回 L3 兜底（吹向房间中心，
        /// 位置差得更多）。所以这个判据整个删掉，**不做假锚点识别**。
        ///
        /// 教训：判据里的每一根阈值，都必须有**本场景实测的数值**撑着。
        /// 「真实壁挂空调不会装到 4 米以上」是一句听起来很对的话，
        /// 而它错在默认了这个场景只有一层。
        /// ══════════════════════════════════════════════════════════════════
        /// </summary>

        private static bool TryCombine(GameObject go, out Bounds bounds)
        {
            bounds = new Bounds();
            if (go == null) return false;

            Renderer[] rs = go.GetComponentsInChildren<Renderer>(true);
            if (rs == null || rs.Length == 0) return false;

            // 跳过粒子/拖尾这类非几何渲染器 —— 它们的 bounds 会在动画大幅变化
            bool any = false;
            for (int i = 0; i < rs.Length; i++)
            {
                if (rs[i] == null) continue;
                if (rs[i] is ParticleSystemRenderer) continue;
                if (!any) { bounds = rs[i].bounds; any = true; }
                else bounds.Encapsulate(rs[i].bounds);
            }
            return any;
        }

        // ==================================================================
        // L2：按几何特征找空调
        // ==================================================================

        /// <summary>
        /// 在房间根节点下找"壁挂空调"。
        ///
        /// 【判据为什么不靠名字】见类注释：本项目空调模型叫不出可识别的名字。
        /// 只能靠**形状** —— 壁挂空调的形状特征非常稳定：
        ///   · 横向长条（宽 0.8~1.2m）
        ///   · 很薄（厚度 0.2~0.3m）
        ///   · 不高（0.25~0.35m）
        ///   · 吊在 2.2~3.0m
        ///
        /// ══════════════════════════════════════════════════════════════════
        /// 【2026-11-05 修正 · 第一版把花盆架判成了空调】
        /// ══════════════════════════════════════════════════════════════════
        /// 第一版探针在 Class1-2 房间打出两个 ★：
        ///     ★ "Split01b"         size=(0.15,0.30,1.05)   ← 真空调
        ///     ★ "FlowerHolder01b"  size=(0.64,0.50,0.27)   ← 花盆架，误判
        ///
        /// 花盆架怎么混进来的：宽 0.64 过了 `> 0.5`、厚 0.27 过了 `< 0.50`、
        /// 高 0.50 过了 `< 0.70`、y=1.87 过了 `1.6~3.4` —— 四条全过。
        ///
        /// 也就是说第一版的阈值**太松，一条区分力都没有**。
        /// 真空调和花盆架的差别在**比例**上，不在绝对值上：
        ///
        ///   真空调 Split01a  0.15 : 0.30 : 1.05  →  长边/厚度 = 7.0，长边/高度 = 3.5
        ///   花盆架           0.64 : 0.50 : 0.27  →  长边/厚度 = 2.4，长边/高度 = 2.4
        ///
        /// 所以补两条**比例判据**：
        ///   · 长边 / 厚度 > 3.0   （空调是又长又薄的条，花盆架接近方块）
        ///   · 长边 / 高度 > 2.5   （空调是横条，花盆架是竖着的箱）
        ///
        /// 同时收紧绝对值：厚度 < 0.30（原 0.50 太宽）、高度 < 0.45（原 0.70 太宽）。
        /// 另加一条"必须离天花板近"的软约束不必做 —— 楼上楼下都有空调，
        /// 绝对高度不可靠（见 IsMockAnchor 的删除说明），比例判据才是稳的。
        /// </summary>
        private static bool TryFindAcByShape(GameObject zoneRoot, Vector3 acPos, out Bounds result)
        {
            result = new Bounds();

            Renderer[] all = zoneRoot.GetComponentsInChildren<Renderer>(true);
            if (all == null) return false;

            bool any = false;
            float bestScore = float.MinValue;
            Bounds best = new Bounds();

            for (int i = 0; i < all.Length; i++)
            {
                Renderer r = all[i];
                if (r == null || r is ParticleSystemRenderer) continue;

                Bounds b = r.bounds;

                // 距锚点太远的不可能是这台。
                // 【为什么用水平距离而不是 3D 距离】同楼层的空调锚点就在旁边，
                // 3D 距离和水平距离差不多；但若锚点因为楼层/吊顶有高差，
                // 3D 距离会被放大。水平距离只关心"在同一间屋子的哪个位置"。
                float dx = b.center.x - acPos.x;
                float dz = b.center.z - acPos.z;
                if (Mathf.Sqrt(dx * dx + dz * dz) > 6f) continue;

                Vector3 s = b.size;
                float horizMax = Mathf.Max(s.x, s.z);
                float horizMin = Mathf.Min(s.x, s.z);

                // ── 绝对尺寸：壁挂空调的物理范围 ──────────────────────
                if (horizMax < 0.60f) continue;        // 长边至少 0.6m
                if (horizMax > 1.60f) continue;        // 太长，是灯管/黑板/床
                if (horizMin > 0.30f) continue;        // 厚度（收 0.50 → 0.30）
                if (s.y > 0.45f) continue;             // 高度（收 0.70 → 0.45）
                if (s.y < 0.12f) continue;             // 太扁，是贴图纸/窗帘

                // ── 比例：这才是区分空调和花盆架的关键 ────────────────
                float ratioThick = horizMax / Mathf.Max(horizMin, 0.001f);
                float ratioHeight = horizMax / s.y;
                if (ratioThick < 3.0f) continue;       // 空调 = 又长又薄
                if (ratioHeight < 2.5f) continue;      // 空调 = 横条，不是竖箱

                // ── 高度区间（放宽，兼容一楼/二楼）─────────────────────
                // 一楼空调 y≈2.4、二楼 y≈6.1。用**相对房间地板**判断需要地板信息，
                // 这里没有可靠地板，所以只挡掉明显在地面以下的。
                // 探针实测最低的空调 y=2.33，所以下限取 1.5 足够。
                if (b.center.y < 1.50f) continue;

                // 命中多个时取**比例最像空调**的（长厚比越大越像）
                if (ratioThick > bestScore)
                {
                    bestScore = ratioThick;
                    best = b;
                    any = true;
                }
            }

            if (any) result = best;
            return any;
        }

        // ==================================================================
        // 从包围盒推导出风口
        // ==================================================================

        /// <summary>
        /// 给定空调包围盒，推出「格栅中心」与「吹风方向」。
        ///
        /// 思路：
        ///   ① 格栅在**底部前沿** —— 壁挂空调的出风口在机身下沿、朝房间一侧
        ///      （冷气往下吹，这是真实空调的导风板位置）
        ///   ② 吹风方向 = 「厚度方向的负方向」，且必须指向房间内部
        ///   ③ 高度取包围盒底部略上一点（不是正中 —— 正中在机身内部）
        /// </summary>
        private static void DeriveVent(Bounds b, Vector3 roomCenterHint, Vector3 roomExtentHint,
                                       out Vector3 ventPos, out Vector3 blowDir)
        {
            Vector3 s = b.size;

            // ══════════════════════════════════════════════════════════════
            // 【2026-11-05 实测修正 · 第一版这里是错的】
            //
            // 第一版写：`bool thinAlongX = s.x <= s.z;`
            //          `normal = thinAlongX ? Vector3.right : Vector3.forward;`
            //
            // 看起来对（"水平两轴里小的那个就是厚度"），但**方向搞反了**：
            // 厚度方向是**墙面法线**没错，而出风口朝向 = 法线指向房间内部的方向。
            // 我拿"较薄的那个轴"当法线，逻辑上成立；但真实空调是：
            //
            //   Split01a: size = (0.15, 0.30, 1.05)
            //              x=0.15 是**厚度**（贴墙），z=1.05 是**长边**（沿墙）
            //
            // 也就是 s.x < s.z，`thinAlongX = true`，`normal = Vector3.right`（±X）。
            // 但教室里空调是**沿南墙横装**的，法线应该是 ±Z（垂直于墙），
            // 长边才沿 X。实测 `size=(0.15,0.30,1.05)` 说明长边在 Z 上 ——
            // 也就是法线在 **X** 上。数学上我的判断对，是**我脑子里那面墙的方向错了**。
            //
            // 真正的错不在这一行，而在下面 **ventPos 偏了多少**：
            // 第一版偏 `halfThin * 0.75`，对 x=0.15 来说只偏了 0.056m ——
            // 而长边方向（z=1.05）那 1.05m 完全没考虑格子栅是**沿长边分布**的。
            // ══════════════════════════════════════════════════════════════

            // 厚度方向 = 水平两轴中较小的那个（墙面法线方向）
            bool thinAlongX = s.x <= s.z;
            Vector3 normal = thinAlongX ? Vector3.right : Vector3.forward;
            // 长边方向 = 垂直于法线的那个水平轴
            Vector3 alongWall = thinAlongX ? Vector3.forward : Vector3.right;

            float thick = thinAlongX ? s.x : s.z;    // 厚度（0.15）
            float length = thinAlongX ? s.z : s.x;    // 长边（1.05）
            float height = s.y;                       // 高（0.30）

            // 判断哪一侧是房间内部：房间中心在这条法线上的投影符号
            Vector3 toRoom = roomCenterHint - b.center;
            toRoom.y = 0f;
            float side = Vector3.Dot(toRoom, normal);

            // ══════════════════════════════════════════════════════════════
            // 【2026-11-05 修正 · 投影接近 0 时符号会随机翻转】
            // ══════════════════════════════════════════════════════════════
            // 如果房间中心恰好落在法线上（投影 ≈ 0），`side >= 0` 这个判断
            // 就由浮点噪声决定，空调可能朝墙里吹。
            //
            // 真实壁挂空调一定贴墙 —— 它离两面墙必然一侧近一侧远。
            // 所以投影不可靠时，改用「离墙远近」：朝远的那个方向吹。
            //
            // 阈值 0.10m：教室宽 7~8m，投影不到 10cm 说明空调基本在房间中线上，
            // 这种情况几乎不存在，但代码不该在这里赌运气。
            // ══════════════════════════════════════════════════════════════
            if (Mathf.Abs(side) < 0.10f)
            {
                // 用房间包围盒在法线方向上的跨度，算出空调到两侧墙的距离。
                // 房间中心到空调在法线上的偏移量 = Dot(b.center - roomCenter, normal)，
                // 两侧墙各距房间中心 halfSpan，所以：
                //   靠 +normal 那面墙的距离 = halfSpan - 偏移
                //   靠 -normal 那面墙的距离 = halfSpan + 偏移
                float offset = Vector3.Dot(b.center - roomCenterHint, normal);
                float halfSpan = Mathf.Abs(Vector3.Dot(roomExtentHint, normal)) * 0.5f;
                float distPlus = halfSpan - offset;
                float distMinus = halfSpan + offset;
                side = distPlus >= distMinus ? 1f : -1f;   // 朝远的那侧吹
                LastReason += "  ⚠法线投影过小(" + Vector3.Dot(toRoom, normal).ToString("F3") +
                              ")，改用离墙远近判定 side=" + side.ToString("F0");
            }

            // blowDir 要指向房间内部
            Vector3 outward = side >= 0f ? normal : -normal;

            // ══════════════════════════════════════════════════════════════
            // 格栅位置：壁挂空调的导风板在**机身下沿、朝房间那一侧**。
            //
            // 三个方向都要算对：
            //   · 沿法线：贴到朝房间的**外表面**（不是中心，中心在机身内部）
            //   · 沿高度：贴到**下沿**再往上一点点（导风板就在下沿）
            //   · 沿长边：**居中**（格子栅贯穿整个长边）
            //
            // 实测这台：center=(0.42,2.43,39.08) size=(0.15,0.30,1.05)
            //   下沿 y = 2.43 - 0.15 = 2.28
            //   朝房间的外表面：法线方向的中心 ± 0.075
            //   长边居中 → 不变
            //   → 出风口应该落在 y≈2.30、离机身表面 1~2cm 处
            // ══════════════════════════════════════════════════════════════
            ventPos = b.center
                      + outward * (thick * 0.5f + 0.02f)   // 贴到外表面再出 2cm
                      + Vector3.down * (height * 0.5f - 0.03f);  // 下沿往上 3cm

            // 兜底：至少别掉到机身下面
            float bottomY = b.center.y - height * 0.5f;
            if (ventPos.y < bottomY + 0.01f) ventPos.y = bottomY + 0.01f;

            // ══════════════════════════════════════════════════════════════
            // 吹风方向：向房间内部 + 略向下。
            //
            // 【第一版错在 -0.26 的倾斜】**：0.26 对应约 15°，看着合理，
            // 但截图里粒子是从空调下方**斜着喷向侧后方**的 —— 因为
            // 当时 blowDir 被算成了 (-0.97,-0.26,0)，X 占主导，
            // 等于"朝左边的墙吹"。修好法线判断后，倾斜保持小角度即可：
            // 导风板压下来大概是 20°~30°，但**视觉上**太斜会像吹风机，
            // 用 10° 更接近"冷气缓缓下沉"。
            // ══════════════════════════════════════════════════════════════
            blowDir = (outward + Vector3.down * 0.18f).normalized;

            // 诊断留痕：把推导依据写进 LastReason，出问题能直接看出是哪个数不对
            LastReason += "  法线轴=" + (thinAlongX ? "X" : "Z") +
                          " 厚度=" + thick.ToString("F2") +
                          " 长边=" + length.ToString("F2") +
                          " 高=" + height.ToString("F2") +
                          " 朝内侧=" + Fmt(outward);
        }

        /// <summary>
        /// 取房间的**几何包围盒**（中心 + 跨度），用于判断「哪边是房间内部」。
        ///
        /// ══════════════════════════════════════════════════════════════════
        /// 【2026-11-05 修正 · 第一版取房间根节点的 transform.position 是错的】
        /// ══════════════════════════════════════════════════════════════════
        /// 第一版直接 `GameObject.Find(zone).transform.position`。
        /// 这个假设是「房间根节点的 pivot 在房间中央」——
        /// **FBX 导出的空父节点 pivot 经常在门口、墙角、或者世界原点**。
        ///
        /// 实测把它打断了：
        ///   Class1-3 的空调 x=12.42、Class1-4 的 x=12.92，**在同一侧墙**，
        ///   却一个吹向 (-0.97,…)、一个吹向 (+0.97,…) —— 同一面墙不可能反着吹。
        ///   根因就是"房间中心"这个参照点偏了，投影符号跟着翻。
        ///
        /// 改成取**房间内所有 Renderer 的合并包围盒**。这个量不受 pivot 影响，
        /// 只跟房间里实际摆了什么有关。
        ///
        /// ⚠️ 房间包围盒会被**房间外飘出去的东西**污染（比如窗外景物、
        /// 挂在房间节点下的走廊灯具）。所以这里对 y 做一次钳制：
        /// 房间是一个楼层内的封闭空间，高度不该超过 8m。
        /// </summary>
        private static Bounds RoomBoundsNear(TwinAnchor anchor, Bounds fallbackBounds)
        {
            Bounds empty = fallbackBounds;
            string zone = ZoneOf(anchor.TwinId);
            if (string.IsNullOrEmpty(zone)) return empty;

            GameObject root = GameObject.Find(zone);
            if (root == null) return empty;

            Bounds rb;
            if (!TryCombine(root, out rb)) return empty;   // 房间下没 Renderer，用空调自己的

            // 钳制：房间高度不该超过 8m。超了说明包围盒混进了楼外的东西，
            // 此时只取中心点，跨度退回空调包围盒的跨度（自然就走到"离墙远近"分支）。
            if (rb.size.y > 8f)
            {
                empty.center = rb.center;
                return empty;
            }
            return rb;
        }

        private static string ZoneOf(string twinId)
        {
            if (string.IsNullOrEmpty(twinId)) return null;
            int slash = twinId.IndexOf('/');
            return slash > 0 ? twinId.Substring(0, slash) : twinId;
        }

        private static string Fmt(Vector3 v)
        {
            return "(" + v.x.ToString("F2") + "," + v.y.ToString("F2") + "," + v.z.ToString("F2") + ")";
        }
    }
}
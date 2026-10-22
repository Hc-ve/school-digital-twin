using System;
using UnityEngine;

namespace SchoolTwin.Twin
{
    /// <summary>
    /// 房间的水平范围（XZ 矩形）—— 用来回答「哪几盏灯属于这个房间」。
    ///
    /// ── 为什么需要这张表（而不是"按坐标算"）────────────────────────────
    /// 用户的需求是「点一个照明点位，**整个教室**的灯一起亮」。
    /// 而数据层一个房间只有一个 `Light/Main_01` 点位、一个 Power 值，
    /// 所以表现层必须知道「这个点位的物理含义 = 这个房间的照明」，而不是"这一盏灯"。
    ///
    /// 这件事看起来可以现场算（谁离房间近就归谁），实际**算不出来**，2026-10-22 逐个试过：
    ///   · 房间节点？`Interior` 下面确实有 `Class2-1` 这些节点，但里面只挂了 3~12 个
    ///     贴墙的物件（黑板、暖气、标记牌），包围盒是一条贴着北墙的**线**，不是房间；
    ///   · 墙体碰撞？整个楼一个碰撞体都没有（全场景只有 19 个 MeshCollider + 21 个 BoxCollider，
    ///     都是我们自己的标记牌和门）→ 射线穿墙判定做不了；
    ///   · 按灯距聚类？灯是**跨房间等距**的（间距 3.0~4.2m 连续排布，墙两侧没有缝），
    ///     任何聚类阈值都会把隔壁的灯带进来；
    ///   · 按"最近分区中心"？实测更糟：走廊只有 2~3 个锚点、集中在走廊一端，
    ///     算出来的中心偏到一头，结果走廊吃掉了 21 + 39 盏灯（一栋楼的灯都被走廊吸走）。
    ///
    /// 结论：**房间边界只能实测反推，然后写成表**。这与 `TwinSiteLayout`（楼层表）
    /// 是同一个做法 —— 表驱动，不用坐标猜。
    ///
    /// ── 这张表的数字是怎么来的 ───────────────────────────────────────
    /// 从场景实测反推，并且**对全部 111 盏灯逐个验证过归属**：
    ///   · X 边界取**门的位置**：1F 北侧一排门在 x = -12.57 / -0.56 / 11.44 / 23.43，
    ///     每间教室一扇门，门就是房间的分界；2F 只有 x = -12.57 和 23.43 两扇，
    ///     中间三间（主任室 / 教师办公室 / 2-2班）按灯的实际分布再切（-2.5 / 12）。
    ///   · Z 边界取灯的实测 z：北侧教室 z ∈ [34.5, 40]，走廊 z ∈ [29.5, 32.5]。
    ///   · 楼层不写进表里：同一次分组只在**同一层**里做（以代表灯的高度为基准，±1.6m），
    ///     这样 1F / 2F 走廊虽然 XZ 完全重合也不会串。
    ///
    /// ── 验证结果（111 盏灯）──────────────────────────────────────────
    ///   ClassComputer 2 / Class1-2 6 / Class1-3 6 / Class1-4 6
    ///   Class2-1 8 / ClassDirector 2 / ClassTeachers 2 / Class2-2 6
    ///   Corridor1 15 / Corridor2 15   → 合计 68 盏
    /// 其余 43 盏落在没有建点位的房间（西侧实验区、东侧南翼等），**不归任何点位** —— 这是对的。
    ///
    /// ⚠️ 场景挪动房间后这张表会失效。表现层每次接管照明时会把「收到几盏灯」打进日志，
    /// 数字不对就能立刻看出来（见 TwinDevicePresenter.BuildLamp）。
    /// </summary>
    public static class TwinRoomBounds
    {
        private struct Box
        {
            public string Zone;
            public float MinX;
            public float MaxX;
            public float MinZ;
            public float MaxZ;
        }

        private static readonly Box[] Boxes =
        {
            // ---- 1F 北侧一排教室（门在 x = -12.57 / -0.56 / 11.44）----
            new Box { Zone = "ClassComputer", MinX = -25.5f, MaxX = -12.4f, MinZ = 33.5f, MaxZ = 40.0f },
            new Box { Zone = "Class1-2",      MinX = -12.4f, MaxX =  -0.7f, MinZ = 33.5f, MaxZ = 40.0f },
            new Box { Zone = "Class1-3",      MinX =  -0.7f, MaxX =  11.3f, MinZ = 33.5f, MaxZ = 40.0f },
            new Box { Zone = "Class1-4",      MinX =  11.3f, MaxX =  25.0f, MinZ = 33.5f, MaxZ = 40.0f },

            // ---- 2F 北侧一排教室 ----
            new Box { Zone = "Class2-1",      MinX = -25.5f, MaxX = -12.4f, MinZ = 34.5f, MaxZ = 40.0f },
            new Box { Zone = "ClassDirector", MinX = -12.4f, MaxX =  -2.5f, MinZ = 35.0f, MaxZ = 40.0f },
            new Box { Zone = "ClassTeachers", MinX =  -2.5f, MaxX =  12.0f, MinZ = 35.0f, MaxZ = 40.0f },
            new Box { Zone = "Class2-2",      MinX =  12.0f, MaxX =  25.0f, MinZ = 34.5f, MaxZ = 40.0f },

            // ---- 走廊：南北向一条，1F / 2F 各一层（靠「同层」判据区分）----
            new Box { Zone = "Corridor1",     MinX = -45.0f, MaxX =  25.0f, MinZ = 29.5f, MaxZ = 32.5f },
            new Box { Zone = "Corridor2",     MinX = -45.0f, MaxX =  25.0f, MinZ = 29.5f, MaxZ = 32.5f },

            // ---- P19 新增：西翼（食堂 / 礼堂）与东翼（体育馆三间）----
            //
            // 【这两组数字怎么来的】2026-10-22 P19 用 `_scratch/p19_tree.py` 把场景层级打出来，
            // 再从 `SchoolScene/Interior` 下的节点实测反推：
            //   · `Kitchen` 节点的子物体（`KitchenFurniture01/02/03`、`KitchenTable01/02`、
            //     `KitchenRazdacha01_*`（打饭窗口）、`SchoolKitchenSeats` ×21）全部落在
            //     x ∈ [-55.1, -43.1]、z ∈ [12.5, 41.5] → 这是**1F 食堂**（含厨房操作间与就餐区）；
            //   · `ActZal`（актовый зал = 礼堂）节点的子物体（`Seats01` ×25、`Curtain01c/02`）
            //     落在同一片 XZ，但 `Seats01` 的 y = 3.57 → **2F**，即礼堂在食堂正上方。
            //     两层 XZ 完全重合，靠 `CollectLampGroup` 的「同层 ±1.6m」判据区分，与走廊同法。
            //   · `SportRoom1/2/3` 三个节点自己的 Transform 都在 (19.53, 1.14, 20.00)（重合），
            //     但**子物体沿 z 分三段**：1 段 z 16.4~19.3、2 段 z 20.7~23.7、3 段 z 24.6~27.8。
            //     所以按 z 切三段，不按节点位置切。
            //
            // ⚠️ 这三间的 X 起点 12.5 是「不碰北侧教室」的硬约束：
            //    Class1-4 的 MinX 是 11.3，`Find()` 是按 Zone 名精确匹配的，
            //    所以矩形重叠本身无害 —— 但 `Contains(zone, x, z)` 会被用来判归属，
            //    重叠会让一盏灯同时「属于」两个分区。留 1.2m 缝比事后排查便宜。
            new Box { Zone = "Kitchen",    MinX = -55.5f, MaxX = -42.6f, MinZ = 12.0f, MaxZ = 41.5f },
            // ⚠️ ActZal 的 MaxX 是 -43.15，比 Kitchen 的 -42.6 更紧，**这是故意的**：
            // 礼堂的大门 `DoorDouble02` 枢轴在 x = -43.09，它贴的是**东墙**（墙沿 Z 走）。
            // 房间范围判墙向靠「门在哪根轴上跑出了范围」—— 如果 MaxX 给到 -43.0，
            // 门在 x 上就落在范围**内**了（-43.09 < -43.0），两根轴都不出界，判据失效，
            // 会退到「门扇朝向」那条；而双开门的模型姿态不是半开（一片 0°、一片 -125°），
            // 那条判据对它不成立（实测：`DoorDouble02` 的 right 指向 -Z，会被判成"墙沿 X"）。
            // 收紧到 -43.15 之后：x 跑出 0.06m、z 在范围内 → 唯一解 = 东墙，墙沿 Z。
            new Box { Zone = "ActZal",     MinX = -55.5f, MaxX = -43.15f, MinZ = 12.0f, MaxZ = 38.5f },
            // ⚠️ 三间体育房的 MaxX 是 **19.35**，不是「东翼那个大块」的 25.5 —— 这个 0.04m 的余量是故意的。
            // 三扇门（`Door07a`）的枢轴全在 x = 19.39，房间东界 19.35 → **只有 X 跑出范围、Z 在范围内**
            // → 唯一解 = 东墙、墙沿 Z。给到 25.5 的话门在两根轴上都落在范围内，
            // 判据失效、只能退回「门扇朝向」（那三扇门的 yaw 是 27°/32°，和其它门的 64.4° 不一样，
            // 兜底能救回来，但那是**运气**不是判据）。
            // 顺带修掉另一个问题：MaxX=25.5 会把走廊那盏 x=22.63 的灯算进「体育器材室」，
            // 于是点开器材室的灯，走廊也跟着亮一盏。收紧后这一组从 3 盏变回 2 盏（实测）。
            new Box { Zone = "SportRoom1", MinX =  13.0f, MaxX =  19.35f, MinZ = 16.0f, MaxZ = 20.6f },
            new Box { Zone = "SportRoom2", MinX =  13.0f, MaxX =  19.35f, MinZ = 20.6f, MaxZ = 24.6f },
            new Box { Zone = "SportRoom3", MinX =  13.0f, MaxX =  19.35f, MinZ = 24.6f, MaxZ = 28.5f },
        };

        /// <summary>这个分区有没有登记范围。没登记 = 只能驱动代表灯，表现层会说明一次。</summary>
        public static bool Has(string zone)
        {
            return Find(zone) >= 0;
        }

        /// <summary>点 (x, z) 是否落在该分区的房间里。未登记的分区一律返回 false。</summary>
        public static bool Contains(string zone, float x, float z)
        {
            int i = Find(zone);
            if (i < 0) return false;
            Box b = Boxes[i];
            return x >= b.MinX && x <= b.MaxX && z >= b.MinZ && z <= b.MaxZ;
        }

        /// <summary>
        /// 取出某个分区的水平范围。没登记返回 false。
        ///
        /// 目前只有一处用它：判断一扇门贴在哪面墙上（见 `TwinDevicePresenter.ResolveWallIsAlongX`）。
        /// 那件事的判据是「门在房间范围之外，还是之内」——
        /// 门总在墙上，而墙总在房间范围的外沿，所以**门在哪根轴上跑出了房间范围，
        /// 那根轴就是墙的法线**。这比"门离哪条边最近"可靠：
        /// Class2-1 的门在 (-12.57, 33.49)，房间范围 x∈[-25.5,-12.4]、z∈[34.5,40]，
        /// 它离 x 边只有 0.17m（最近），但它在 z 上是**整个跑出去了 1.01m**，
        /// 所以那扇门贴的是南墙（沿 X 走），不是东墙。
        /// </summary>
        public static bool TryGetBounds(string zone, out float minX, out float maxX, out float minZ, out float maxZ)
        {
            minX = maxX = minZ = maxZ = 0f;
            int i = Find(zone);
            if (i < 0) return false;
            Box b = Boxes[i];
            minX = b.MinX; maxX = b.MaxX; minZ = b.MinZ; maxZ = b.MaxZ;
            return true;
        }

        /// <summary>登记过的分区数量（自检用）。</summary>
        public static int Count { get { return Boxes.Length; } }

        private static int Find(string zone)
        {
            if (string.IsNullOrEmpty(zone)) return -1;
            for (int i = 0; i < Boxes.Length; i++)
            {
                if (string.Equals(Boxes[i].Zone, zone, StringComparison.Ordinal)) return i;
            }
            return -1;
        }
    }
}

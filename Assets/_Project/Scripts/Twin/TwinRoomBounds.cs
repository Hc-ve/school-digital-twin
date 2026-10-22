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

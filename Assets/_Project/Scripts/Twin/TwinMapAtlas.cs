using System;
using UnityEngine;

namespace SchoolTwin.Twin
{
    /// <summary>
    /// 小地图底图图集：每张底图一条记录（贴图 + 它覆盖的世界矩形）。
    ///
    /// 【为什么要把「世界矩形」和贴图存在一起】
    /// 运行时把世界 XZ 换算成图上 UV 只需要一次线性映射，不用每帧去反推场景包围盒；
    /// 重新烘焙时矩形跟着一起写进来，换图不会错位。
    ///
    /// 由 `Editor/TwinMapBaker` 生成，**不要手改**。
    /// </summary>
    [CreateAssetMenu(menuName = "SchoolTwin/地图图集 (TwinMapAtlas)", fileName = "TwinMapAtlas")]
    public sealed class TwinMapAtlas : ScriptableObject
    {
        public const string KeyCampus = "Campus";
        public const string KeyFloor1 = "Floor1";
        public const string KeyFloor2 = "Floor2";

        [Serializable]
        public sealed class Entry
        {
            /// <summary>Campus / Floor1 / Floor2。</summary>
            public string Key;

            /// <summary>0 = 室外总览，1 = 一层，2 = 二层。</summary>
            public int Floor;

            public Texture2D Texture;

            /// <summary>底图覆盖的世界范围（X = 世界 x，Y = 世界 z），左下角。</summary>
            public Vector2 WorldMin;

            /// <summary>同上，右上角。</summary>
            public Vector2 WorldMax;

            /// <summary>烘焙时间，用来判断底图是不是过期了。</summary>
            public string BakedAt;

            /// <summary>世界 XZ → 0~1 的图内归一化坐标（左下原点）。</summary>
            public Vector2 WorldToUv(Vector3 world)
            {
                float w = WorldMax.x - WorldMin.x;
                float h = WorldMax.y - WorldMin.y;
                if (w < 0.0001f || h < 0.0001f) return new Vector2(0.5f, 0.5f);
                return new Vector2((world.x - WorldMin.x) / w, (world.z - WorldMin.y) / h);
            }

            /// <summary>世界 XZ → 底图上的像素/UI 坐标（**左上原点**，和 UGUI 一致）。</summary>
            public Vector2 WorldToMap(Vector3 world, Vector2 mapSize)
            {
                Vector2 uv = WorldToUv(world);
                return new Vector2(uv.x * mapSize.x, (1f - uv.y) * mapSize.y);
            }

            /// <summary>图内归一化坐标 → 世界 XZ。</summary>
            public Vector3 UvToWorld(Vector2 uv, float y)
            {
                float w = WorldMax.x - WorldMin.x;
                float h = WorldMax.y - WorldMin.y;
                return new Vector3(WorldMin.x + uv.x * w, y, WorldMin.y + uv.y * h);
            }
        }

        public Entry[] Entries = new Entry[0];

        public Entry Get(string key)
        {
            if (Entries == null) return null;
            for (int i = 0; i < Entries.Length; i++)
            {
                Entry e = Entries[i];
                if (e != null && string.Equals(e.Key, key, StringComparison.Ordinal)) return e;
            }
            return null;
        }

        /// <summary>按楼层取：0 = 室外，1 = 1F，2 = 2F。取不到返回 null。</summary>
        public Entry GetByFloor(int floor)
        {
            if (Entries == null) return null;
            for (int i = 0; i < Entries.Length; i++)
            {
                Entry e = Entries[i];
                if (e != null && e.Floor == floor && e.Texture != null) return e;
            }
            return null;
        }

        public bool IsEmpty { get { return Entries == null || Entries.Length == 0; } }
    }
}

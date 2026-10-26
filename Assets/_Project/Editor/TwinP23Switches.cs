using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using SchoolTwin.Twin;

namespace SchoolTwin.EditorTools
{
    /// <summary>
    /// 生成「灯开关小方块」：贴在教室门旁边的墙上，走近它弹出「开灯/关灯」按钮。
    ///
    /// 【为什么要这个】教室里没有开关的实体模型（灯的锚点在天花板的灯具上，
    /// 玩家走不过去）。用一个小方块占位，先把「走近→弹按钮→点一下→灯亮/灭」
    /// 这条交互链路跑通；以后有真模型了，把小方块替换掉即可（TwinInteractPoint 会留下）。
    ///
    /// ── 只给「白名单里的灯」建开关 ──────────────────────────────
    /// 场景里 `Class2-1` 有 **3 个** Light 锚点：正式那个 + `BAK/`、`BAK2/` 两个历史备份。
    /// 不筛的话按 `FindObjectsOfType` 的返回顺序取第一个 —— 很可能绑到 BAK 上，
    /// 而 BAK **不在控制白名单**里 → 点一下命令被拒 → 灯不亮且看不出原因。
    ///
    /// ── 墙面法线为什么写死 +Z（这是实测结论，不是猜）─────────────
    /// 导出全部 `Door07a` 的 transform 后发现：
    ///   euler = (0, 64.40, 0)，forward = (0.90, 0, 0.43)，right = (0.43, 0, -0.90)
    /// 也就是说**门是半开的**（64.4°），`door.forward` 是打开的门扇法线，
    /// **不是**墙面法线 —— 拿它当墙法线会把开关贴到半空中。
    /// 而 8 间教室的门全在 z≈33.44~33.49 这条走廊墙上，房间都在 +Z 侧，
    /// 所以：墙面法线 = +Z，墙面切线 = X。射线检测会再验一遍，错了就不落。
    ///
    /// ── 位置靠射线，不靠房间表 ──────────────────────────────────
    /// 从房间内 3m 处朝墙反向打射线，第一个交点就是室内墙面 —— 墙多厚都不用管。
    /// 房间矩形（TwinRoomBounds）只用来做一件事：判断沿 X 偏移后**还在不在本房间**。
    /// 因为门几乎都贴着隔墙，+0.75m 很容易跨到隔壁去（实测 Class1-2 就跑到了 Class1-3）。
    /// 注意只能校验 X 这一根轴：矩形是照灯反推的，Z 方向差 1.3m，二维 Contains 会误杀。
    /// </summary>
    public static class TwinP23Switches
    {
        private const string ScenePath = "Assets/TirGames/SchoolScene/Scenes/SchoolSceneDay.unity";
        private const string ContainerName = "P23_Switches";
        private const string MatPath = "Assets/_Project/Materials/TwinSwitchBox.mat";

        /// <summary>开关沿墙方向离门轴的候选偏移（米），从小到大试。门扇净宽约 0.84m，太小会被门洞吃掉</summary>
        private static readonly float[] WallOffsets = { 0.60f, 0.75f, 0.95f, 1.15f };

        /// <summary>开关离地高度（米）。国标是 1.3m，稍微低一点更好按</summary>
        private const float Height = 1.15f;

        /// <summary>方块尺寸（米）：宽 × 高 × 厚</summary>
        private static readonly Vector3 BoxSize = new Vector3(0.12f, 0.20f, 0.04f);

        /// <summary>开关中心离墙面留的缝（米）。方块厚 4cm，留 3cm 差不多齐平</summary>
        private const float WallGap = 0.03f;

        [MenuItem("P23 交互/① 生成灯开关方块（会存盘）", false, 10)]
        public static void GenerateMenu()
        {
            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorUtility.DisplayDialog("已拒绝",
                    "现在是 Play 模式。请先按停止再执行。", "好");
                return;
            }
            string report = Generate();
            Debug.Log(report);
            EditorUtility.DisplayDialog("P23 生成灯开关", report, "好");
        }

        /// <summary>批处理入口（-executeMethod 调用）。无返回值，避免命令行抱怨签名。</summary>
        public static void GenerateBatch()
        {
            Debug.Log(Generate());
        }

        public static string Generate()
        {
            Scene scene = EnsureScene();

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[P23] ===== 生成灯开关方块 =====");

            Transform container = EnsureContainer();
            Material mat = EnsureMaterial();

            // 每次重跑都清空重建：这些是占位方块，留着旧的只会在射线检测里挡住自己
            ClearContainer(container);

            // 场景刚打开时碰撞体可能还没同步进物理世界，射线会全部打空
            Physics.SyncTransforms();

            // 静态类没有继承 MonoBehaviour，FindObjectsOfType 必须走 Object. 前缀
            TwinAnchor[] all = UnityEngine.Object.FindObjectsOfType<TwinAnchor>(true);
            HashSet<string> doneZones = new HashSet<string>(StringComparer.Ordinal);
            int made = 0, skipped = 0, notWhitelisted = 0;

            for (int i = 0; i < all.Length; i++)
            {
                TwinAnchor light = all[i];
                if (light == null || string.IsNullOrEmpty(light.TwinId)) continue;
                if (!string.Equals(light.Category, "Light", StringComparison.OrdinalIgnoreCase)) continue;

                // 【P24】这里原来写死 `light.Zone.StartsWith("Class")` —— 只给教室建开关。
                // 结果 ActZal（礼堂）/ SportHall（篮球场）/ SportRoom2 / SportRoom3 这些
                // 非 Class 开头的房间**永远拿不到开关**，而且因为是在白名单检查之前 continue，
                // 连一行"跳过"日志都不打 —— 用户只会看到"面板有、开关没有"，极难定位。
                // 2026-10-26 用户实测踩到。改成：只要在白名单里就建。
                if (string.IsNullOrEmpty(light.Zone)) continue;

                // 一个房间只建一个开关：先把 BAK 备份和不可控点位筛掉
                if (!TwinControlScope.IsEnabled(light.TwinId))
                {
                    sb.AppendLine("[P23] ○ 跳过非白名单灯 " + light.TwinId + "（Zone=" + light.Zone + "）");
                    notWhitelisted++;
                    continue;
                }
                if (doneZones.Contains(light.Zone))
                {
                    skipped++;
                    continue;
                }
                doneZones.Add(light.Zone);

                string zone = light.Zone;
                float x0, x1, z0, z1;
                if (!TwinRoomBounds.TryGetBounds(zone, out x0, out x1, out z0, out z1))
                {
                    sb.AppendLine("[P23] ⚠ " + zone + " 没有分区矩形，跳过");
                    skipped++;
                    continue;
                }

                // 门只用来「定位」，不需要可控 —— 不能按白名单筛门，
                // 否则 ClassTeachers / ClassDirector（场景里压根没有门锚点）会被误杀。
                TwinAnchor door = FindDoor(all, zone);

                float y;
                float xAnchor;
                float zAnchor;
                string where;

                if (door != null)
                {
                    // 门锚点的 Y 就是地面高度（实测 1F=0.035、2F=3.591）
                    y = door.transform.position.y + Height;
                    xAnchor = door.transform.position.x;
                    zAnchor = door.transform.position.z;
                    where = "门旁「" + door.gameObject.name + "」";
                }
                else
                {
                    // 没门锚点：退一步放走廊墙中段。
                    // 【P24】个别房间的地面不在 0 / 3.556 —— 篮球场（SportHall）是通高大厅，
                    // 地面 y≈−0.05、灯具挂在 7.4m，按楼层公式会推成 2F(3.556) → 开关飘在半空。
                    // 这类房间走显式覆盖表。
                    float floorY;
                    if (!FloorOverrides.TryGetValue(zone, out floorY)) floorY = FloorGuessOf(light.transform.position.y);
                    y = floorY + Height;
                    xAnchor = (x0 + x1) * 0.5f;
                    zAnchor = (z0 + z1) * 0.5f;
                    where = "无门锚点→走廊墙中段";
                }

                Vector3 pos;
                string note;
                Vector3 wallNormal;
                if (!TryPlaceOnRoomWall(x0, x1, z0, z1, xAnchor, zAnchor, y,
                                        out pos, out wallNormal, out note))
                {
                    sb.AppendLine("[P23] ⚠ " + zone + " 定位失败，跳过（" + note + "）");
                    skipped++;
                    continue;
                }

                string goName = "Switch_" + zone;
                GameObject go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = goName;
                Undo.RegisterCreatedObjectUndo(go, "Create Light Switch");
                go.transform.SetParent(container, false);
                go.transform.position = pos;
                // 按墙法线转：方块是 宽X / 高Y / 厚Z，让局部 +Z 对齐法线，厚度就贴在墙面上。
                // 朝 +Z 的墙 LookRotation((0,0,1)) == identity，和原来完全一致；
                // 朝 ±X 的墙（礼堂东墙）以前不转，方块会立歪 —— 2026-10-26 补上。
                go.transform.rotation = Quaternion.LookRotation(wallNormal, Vector3.up);
                go.transform.localScale = BoxSize;

                Renderer r = go.GetComponent<Renderer>();
                if (r != null) r.sharedMaterial = mat;

                BoxCollider col = go.GetComponent<BoxCollider>();
                if (col == null) col = go.AddComponent<BoxCollider>();
                col.size = Vector3.one;   // localScale 已经是真实尺寸
                col.isTrigger = false;

                SchoolTwin.Interaction.TwinInteractPoint point =
                    go.AddComponent<SchoolTwin.Interaction.TwinInteractPoint>();
                point.TargetTwinId = light.TwinId;
                point.Range = 2.2f;

                sb.AppendLine("[P23] ✔ " + zone + "  开关→" + light.TwinId +
                              "  位置=" + V3(pos) + "  " + where + "  " + note);
                made++;
            }

            sb.AppendLine();
            sb.AppendLine("[P23] 生成 " + made + " 个；跳过 " + skipped +
                          " 个；非白名单灯 " + notWhitelisted + " 个");

            if (made > 0)
            {
                EditorSceneManager.MarkSceneDirty(scene);
                bool saved = EditorSceneManager.SaveScene(scene);
                sb.AppendLine("[P23] 场景保存：" + (saved ? "成功" : "失败"));
            }

            sb.AppendLine("[P23] ===== 结束 =====");
            try { System.IO.File.WriteAllText("p23_switches.txt", sb.ToString(), Encoding.UTF8); } catch (Exception) { }
            return sb.ToString();
        }

        // ================================================================ 定位

        /// <summary>
        /// 在房间的走廊那面墙上放开关。
        ///
        /// 法线：先试 +Z（这排教室全在走廊北侧），不行再试 -Z。射线会验证，
        /// 打到的东西必须真的是「朝向房间的竖直墙面」才算数。
        ///
        /// 偏移：沿 X 左右各试几个距离，且必须仍落在房间的 X 跨度内 ——
        /// 门几乎都贴着隔墙，不加这个约束就会跨到隔壁房间的墙上去。
        /// </summary>
        /// <summary>
        /// 在房间墙面上找一个能贴开关的位置。
        ///
        /// 【P24】原来只朝 ±Z 打射线 —— 只适用于「门开在南/北墙」的那排教室。
        /// 礼堂的门在**东墙**（法线沿 X），结果射线一路打到 18m 外的南墙，
        /// 开关落在礼堂最南端（2026-10-26 用户实测）。
        /// 现在四个方向都打，并且把命中的法线带出去，让方块按法线转正。
        /// </summary>
        private static bool TryPlaceOnRoomWall(float x0, float x1, float z0, float z1,
                                               float xAnchor, float zAnchor, float y,
                                               out Vector3 pos, out Vector3 normal, out string note)
        {
            pos = Vector3.zero;
            normal = Vector3.forward;
            note = "";

            const float maxDist = 12f;
            const float margin = 0.15f;    // 距房间边缘留一点余量，免得贴到隔墙侧面
            const float edgeInset = 0.35f; // 起点离房间边界的距离

            // 四面墙按「离门（或锚点）由近到远」排序后再试。
            //
            // 【为什么必须排序】门就开在某面墙上，那面墙才是该贴开关的地方。
            // 2026-10-26 第一版只把 ±X 加在 ±Z 后面，结果礼堂（门在东墙）仍然先打 +Z，
            // 一路命中 18m 外的南墙，开关落在礼堂最南端。
            Vector3[] normals = { Vector3.forward, Vector3.back, Vector3.right, Vector3.left };
            float[] dist =
            {
                Mathf.Abs(zAnchor - z0),   // forward(+Z) 法线 → 墙在 z0（射线往 -Z 打）
                Mathf.Abs(z1 - zAnchor),   // back(-Z)   法线 → 墙在 z1
                Mathf.Abs(xAnchor - x0),   // right(+X)  法线 → 墙在 x0
                Mathf.Abs(x1 - xAnchor),   // left(-X)   法线 → 墙在 x1
            };
            for (int a = 1; a < normals.Length; a++)          // 4 个元素，插入排序足够
            {
                for (int b = a; b > 0 && dist[b] < dist[b - 1]; b--)
                {
                    float td = dist[b]; dist[b] = dist[b - 1]; dist[b - 1] = td;
                    Vector3 tn = normals[b]; normals[b] = normals[b - 1]; normals[b - 1] = tn;
                }
            }

            for (int n = 0; n < normals.Length; n++)
            {
                Vector3 nm = normals[n];
                bool alongX = Mathf.Abs(nm.x) > 0.5f;   // 这面墙的法线沿 X

                // 起点放在**紧贴目标墙的内侧**，短射线打过去：距离短、被家具挡住的概率低。
                float originZ = nm.z > 0f ? z0 + edgeInset : (nm.z < 0f ? z1 - edgeInset : zAnchor);
                float originX = nm.x > 0f ? x0 + edgeInset : (nm.x < 0f ? x1 - edgeInset : xAnchor);

                for (int k = 0; k < WallOffsets.Length; k++)
                {
                    for (int s = 0; s < 2; s++)
                    {
                        float off = WallOffsets[k] * (s == 0 ? 1f : -1f);

                        Vector3 origin;
                        if (alongX)
                        {
                            float pz = zAnchor + off;
                            if (pz < z0 + margin || pz > z1 - margin) continue;
                            origin = new Vector3(originX, y, pz);
                        }
                        else
                        {
                            float px = xAnchor + off;
                            if (px < x0 + margin || px > x1 - margin) continue;
                            origin = new Vector3(px, y, originZ);
                        }

                        RaycastHit hit;
                        if (!Physics.Raycast(origin, -nm, out hit, maxDist, ~0,
                                             QueryTriggerInteraction.Ignore))
                            continue;                                   // 门洞，射线穿过去了
                        if (hit.normal.y > 0.7f) continue;               // 地板 / 天花板
                        if (Vector3.Dot(hit.normal, nm) < 0.7f) continue; // 不是这面墙

                        // 法线指向房间内，往 +normal 让一点才贴在室内这一面
                        pos = hit.point + nm * WallGap;
                        pos.y = y;
                        normal = nm;
                        note = "偏移" + off.ToString("0.00") + "m 法线=" + V3(nm) +
                               " 墙面=" + V3(hit.point);
                        return true;
                    }
                }
            }

            note = "四面墙都找不到合适位置";
            return false;
        }

        /// <summary>没门的时候拿不到地面高度：1F 灯约 3.3m、2F 灯约 6.9m，按 3.556 层高反推。</summary>
        /// <summary>
        /// 地面高度不按「1F=0 / 2F=3.556」走的房间。
        /// 目前只有篮球场：它是通高大厅，地面 y≈−0.05、灯具挂在 7.4m，
        /// 按楼层公式会推成 2F，开关会飘在半空（2026-10-26 加）。
        /// </summary>
        private static readonly Dictionary<string, float> FloorOverrides =
            new Dictionary<string, float>(StringComparer.Ordinal)
            {
                { "SportHall", -0.05f },
            };

        private static float FloorGuessOf(float lightY)
        {
            const float storey = 3.556f;
            if (lightY < storey) return 0f;
            return Mathf.Round((lightY - 3.3f) / storey) * storey;
        }

        // ================================================================ 诊断

        [MenuItem("P23 交互/② 导出教室门朝向（诊断）", false, 20)]
        public static void DumpDoorsMenu()
        {
            string s = DumpDoors();
            Debug.Log(s);
            EditorUtility.DisplayDialog("P23 诊断", "已写到 p23_doors.txt", "好");
        }

        public static void DumpDoorsBatch()
        {
            Debug.Log(DumpDoors());
        }

        /// <summary>把教室门/灯锚点的真实 transform 与房间矩形全导出来，别再靠猜。</summary>
        public static string DumpDoors()
        {
            EnsureScene();

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[P23-DIAG] ===== 门与灯的真实姿态 =====");

            TwinAnchor[] all = UnityEngine.Object.FindObjectsOfType<TwinAnchor>(true);
            for (int i = 0; i < all.Length; i++)
            {
                TwinAnchor a = all[i];
                if (a == null) continue;
                bool isDoor = string.Equals(a.Category, "Door", StringComparison.OrdinalIgnoreCase);
                bool isLight = string.Equals(a.Category, "Light", StringComparison.OrdinalIgnoreCase);
                if (!isDoor && !isLight) continue;
                if (a.Zone == null || !a.Zone.StartsWith("Class", StringComparison.Ordinal)) continue;

                float x0, x1, z0, z1;
                bool hb = TwinRoomBounds.TryGetBounds(a.Zone, out x0, out x1, out z0, out z1);

                sb.AppendLine("[P23-DIAG] " + (isDoor ? "DOOR " : "LIGHT") +
                              " id=" + a.TwinId +
                              " zone=" + a.Zone +
                              " go=" + a.gameObject.name +
                              " pos=" + V3(a.transform.position) +
                              " euler=" + V3(a.transform.eulerAngles) +
                              " fwd=" + V3(a.transform.forward) +
                              " right=" + V3(a.transform.right) +
                              " lossyScale=" + V3(a.transform.lossyScale) +
                              " parent=" + (a.transform.parent != null ? a.transform.parent.name : "-") +
                              (hb
                                  ? " boundsX=(" + x0.ToString("0.00") + "," + x1.ToString("0.00") + ")" +
                                    " boundsZ=(" + z0.ToString("0.00") + "," + z1.ToString("0.00") + ")"
                                  : " bounds=none"));
            }

            sb.AppendLine("[P23-DIAG] ===== 结束 =====");
            try { System.IO.File.WriteAllText("p23_doors.txt", sb.ToString(), Encoding.UTF8); } catch (Exception) { }
            return sb.ToString();
        }

        // ================================================================ 辅助

        /// <summary>批处理（-executeMethod）启动时活动场景是空场景，必须先显式打开。</summary>
        private static Scene EnsureScene()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (string.IsNullOrEmpty(scene.path) || scene.path != ScenePath)
                scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            return scene;
        }

        private static Transform EnsureContainer()
        {
            GameObject found = GameObject.Find(ContainerName);
            if (found != null) return found.transform;

            GameObject go = new GameObject(ContainerName);
            Undo.RegisterCreatedObjectUndo(go, "Create P23_Switches");
            return go.transform;
        }

        /// <summary>清空容器下的旧方块（重跑时避免射线打到上次生成的自己）</summary>
        private static void ClearContainer(Transform container)
        {
            if (container == null) return;
            for (int i = container.childCount - 1; i >= 0; i--)
            {
                GameObject child = container.GetChild(i).gameObject;
                Undo.DestroyObjectImmediate(child);
            }
        }

        private static Material EnsureMaterial()
        {
            Material m = AssetDatabase.LoadAssetAtPath<Material>(MatPath);
            if (m != null) return m;

            Shader sh = Shader.Find("Standard");
            if (sh == null) sh = Shader.Find("Legacy Shaders/Diffuse");
            m = new Material(sh);
            m.name = "TwinSwitchBox";
            m.color = new Color(0.88f, 0.90f, 0.94f, 1f);
            if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0.32f);
            if (m.HasProperty("_EmissionColor"))
            {
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", new Color(0.12f, 0.34f, 0.27f) * 0.7f);
            }

            string dir = System.IO.Path.GetDirectoryName(MatPath).Replace('\\', '/');
            if (!string.IsNullOrEmpty(dir) && !AssetDatabase.IsValidFolder(dir))
            {
                string parent = System.IO.Path.GetDirectoryName(dir).Replace('\\', '/');
                AssetDatabase.CreateFolder(parent, System.IO.Path.GetFileName(dir));
            }
            AssetDatabase.CreateAsset(m, MatPath);
            AssetDatabase.SaveAssets();
            return m;
        }

        private static TwinAnchor FindDoor(TwinAnchor[] all, string zone)
        {
            for (int i = 0; i < all.Length; i++)
            {
                TwinAnchor a = all[i];
                if (a == null || string.IsNullOrEmpty(a.TwinId)) continue;
                if (!string.Equals(a.Zone, zone, StringComparison.Ordinal)) continue;
                if (!string.Equals(a.Category, "Door", StringComparison.OrdinalIgnoreCase)) continue;
                return a;
            }
            return null;
        }

        private static string V3(Vector3 v)
        {
            return "(" + v.x.ToString("0.00") + "," + v.y.ToString("0.00") + "," + v.z.ToString("0.00") + ")";
        }
    }
}

// 【P26】场景显示体检 / 一键还原
//
// 用途：用户报"右侧墙壁材质丢失变黑 + 场景里出现网格"，
// 但磁盘上的场景文件与最后一次提交**字节一致**（git diff 为空），
// 说明要么是编辑器里**未保存**的改动，要么是运行时被脚本改了材质。
// 这个脚本不猜，直接把真实情况打到 Console：
//
//   ① 谁在用 P26 的幕布着色器（是不是误伤到了墙）
//   ② 谁没有材质 / 材质上的 shader 丢了（真正的"材质引用丢失"）
//   ③ 教室附近所有 Renderer 的世界包围盒（看有没有不该出现的巨大物体）
//   ④ 场景根对象的真实世界坐标 + 地面 Y（判断"网格"是不是因为地面被移动）
//
// 菜单：Tools/P26/① 场景显示体检
//      Tools/P26/② 还原幕布材质到预制体默认值
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SchoolTwin.EditorTools
{
    public static class TwinSceneSanityCheck
    {
        /// <summary>P26 接管的那几个点位（和 TwinDevicePresenter 里一致）。</summary>
        static readonly string[] ScreenNames =
        {
            "ProjectorScreen01",
            "ProjectorScreen01Old",
        };

        [MenuItem("Tools/P26/① 场景显示体检", false, 100)]
        public static void RunCheck()
        {
            Scene sc = SceneManager.GetActiveScene();
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("================ P26 场景显示体检 ================");
            sb.AppendLine("场景: " + sc.name + "   路径: " + sc.path);
            sb.AppendLine("场景是否被改过(未保存): " + (sc.isDirty ? "★ 是（有未保存改动）" : "否"));

            GameObject[] roots = sc.GetRootGameObjects();
            sb.AppendLine();
            sb.AppendLine("---- 根对象世界坐标（判断整体位移/缩放）----");
            foreach (GameObject r in roots)
            {
                Transform t = r.transform;
                bool moved = t.position.sqrMagnitude > 1e-6f;
                bool scaled = (t.lossyScale - Vector3.one).sqrMagnitude > 1e-6f;
                bool roted = Quaternion.Angle(t.rotation, Quaternion.identity) > 0.01f;
                sb.AppendLine(string.Format("  {0,-24} pos={1}  scale={2}{3}{4}",
                    r.name, t.position.ToString("F3"), t.lossyScale.ToString("F3"),
                    moved ? "  ◀位置非零" : "", (scaled ? "  ◀缩放非1" : "") + (roted ? "  ◀有旋转" : "")));
            }

            // ---- 遍历所有 Renderer ----
            Renderer[] all = Object.FindObjectsOfType<Renderer>(true);
            sb.AppendLine();
            sb.AppendLine("---- 全场景 Renderer 总数: " + all.Length + " ----");

            List<string> noMat = new List<string>();
            List<string> badShader = new List<string>();
            List<string> screenShader = new List<string>();
            List<string> huge = new List<string>();

            foreach (Renderer rd in all)
            {
                if (rd == null) continue;
                string path = PathOf(rd.transform);

                Material[] mats = rd.sharedMaterials;
                if (mats == null || mats.Length == 0)
                {
                    noMat.Add(path + "（材质数组为空）");
                }
                else
                {
                    for (int i = 0; i < mats.Length; i++)
                    {
                        if (mats[i] == null)
                        {
                            noMat.Add(path + "  槽 " + i + " 为空");
                            continue;
                        }
                        if (mats[i].shader == null)
                        {
                            badShader.Add(path + "  槽 " + i + " shader 丢失");
                            continue;
                        }
                        string sn = mats[i].shader.name;
                        if (sn.IndexOf("ScreenOnModel", System.StringComparison.Ordinal) >= 0 ||
                            sn.IndexOf("ScreenComposite", System.StringComparison.Ordinal) >= 0)
                        {
                            // 幕布着色器只能贴在幕布上。贴到别的地方就会把整块几何涂黑。
                            bool isScreen = false;
                            foreach (string n in ScreenNames)
                                if (path.IndexOf(n, System.StringComparison.Ordinal) >= 0) { isScreen = true; break; }

                            Vector3 sz = rd.bounds.size;
                            string line = string.Format("{0}  [{1}]  bounds={2}",
                                path, sn, sz.ToString("F2"));
                            if (!isScreen || sz.x > 8f || sz.y > 8f || sz.z > 8f)
                                line += "   ★ 可疑：不在幕布上 / 尺寸过大";
                            screenShader.Add(line);
                        }
                    }
                }

                // 巨大物体：很容易是"凭空多出来的网/黑板/黑墙"
                Vector3 s = rd.bounds.size;
                if (s.x > 30f || s.y > 30f || s.z > 30f)
                    huge.Add(path + "  bounds=" + s.ToString("F1"));
            }

            Append(sb, "★ 真正断裂的材质（槽为空）", noMat);
            Append(sb, "★ shader 丢失", badShader);
            Append(sb, "使用 P26 幕布着色器的 Renderer", screenShader);
            Append(sb, "包围盒 > 30m 的巨大物体", huge);

            // ---- 教室附近的地面高度 ----
            sb.AppendLine();
            sb.AppendLine("---- 「网格」相关：地面高度 ----");
            sb.AppendLine("  Unity 场景视图网格固定在 y = 0 平面。");
            sb.AppendLine("  如果地面就在 y = 0，会与网格 z-fighting，网格会透过地面显示出来。");
            sb.AppendLine("  这是**编辑器显示**，不会进入打包版本。");
            GameObject floor = GameObject.Find("Floor");
            if (floor == null) floor = GameObject.Find("Floor01");   // Unity 的"假 null"，不能用 ?? 
            if (floor != null)
                sb.AppendLine("  Floor 世界 y = " + floor.transform.position.y.ToString("F3"));

            sb.AppendLine("==================================================");
            Debug.Log(sb.ToString());
        }

        [MenuItem("Tools/P26/② 还原幕布材质到预制体默认值", false, 101)]
        public static void RevertScreenMaterials()
        {
            // 【为什么不用 PrefabUtility.RevertObjectProperties】
            // 这个 API 在 2022.3.62 里不存在（编译报 CS0117），而且不同版本签名不一致。
            // 换成最稳的做法：直接读预制体里的原始材质，赋回场景里的 Renderer。
            // 赋值本身就是一次"预制体覆盖"，效果等同于还原。
            const string prefabPath =
                "Assets/TirGames/SchoolScene/Models/Interior/Screens/Prefabs/ProjectorScreen01.prefab";

            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null)
            {
                Debug.LogError("[P26] 找不到幕布预制体：" + prefabPath);
                return;
            }

            // 预制体里可能不止一个 Renderer，按名字建索引
            Dictionary<string, Material[]> origin = new Dictionary<string, Material[]>();
            foreach (Renderer r in prefab.GetComponentsInChildren<Renderer>(true))
                origin[r.name] = r.sharedMaterials;

            int n = 0;
            Renderer[] all = Object.FindObjectsOfType<Renderer>(true);
            foreach (Renderer rd in all)
            {
                if (rd == null) continue;
                bool hit = false;
                foreach (string nm in ScreenNames)
                    if (rd.name.IndexOf(nm, System.StringComparison.Ordinal) >= 0) { hit = true; break; }
                if (!hit) continue;

                Material[] src;
                if (!origin.TryGetValue(rd.name, out src) || src == null || src.Length == 0) continue;

                // 已经是原生材质就跳过，避免无意义地把场景标脏
                Material[] cur = rd.sharedMaterials;
                bool same = cur != null && cur.Length == src.Length;
                if (same)
                {
                    for (int i = 0; i < src.Length; i++)
                        if (cur[i] != src[i]) { same = false; break; }
                }
                if (same) continue;

                Undo.RecordObject(rd, "还原幕布材质");
                rd.sharedMaterials = src;
                n++;
                Debug.Log("[P26] 已还原 " + PathOf(rd.transform) +
                          " → " + src.Length + " 个原始材质");
            }

            // 运行时 new 出来的临时材质不是资产，重开 Play 就没了；
            // 这里只处理"被写进场景"的覆盖。
            if (n > 0)
            {
                EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
                Debug.Log("[P26] 幕布材质已还原，共 " + n + " 个 Renderer。记得 Ctrl+S 保存。");
            }
            else
            {
                Debug.Log("[P26] 幕布材质本来就是预制体默认值，没有需要还原的覆盖。\n" +
                          "（运行时被脚本改的材质不会写回场景 —— 退出 Play 就恢复了）");
            }
        }

        static void Append(StringBuilder sb, string title, List<string> items)
        {
            sb.AppendLine();
            sb.AppendLine("---- " + title + "：" + items.Count + " 个 ----");
            for (int i = 0; i < items.Count && i < 40; i++) sb.AppendLine("  " + items[i]);
            if (items.Count > 40) sb.AppendLine("  …… 还有 " + (items.Count - 40) + " 个");
        }

        static string PathOf(Transform t)
        {
            string p = t.name;
            while (t.parent != null) { t = t.parent; p = t.name + "/" + p; }
            return p;
        }
    }
}

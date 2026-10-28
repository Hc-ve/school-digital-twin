using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace School.DigitalTwin.EditorTools
{
    /// <summary>
    /// P0 基线治理工具。
    ///
    /// 设计原则：
    /// 1. 只作用于「当前已打开的场景」，不会扫描或修改其他场景文件；
    /// 2. 扫描为纯只读操作；
    /// 3. 清理操作在执行前弹窗列出全部目标对象，并注册 Undo，可 Ctrl+Z 撤销；
    /// 4. 不触碰 Assets/TirGames 下的任何资源文件。
    ///
    /// 已知目标（SchoolSceneDay）：
    ///   · 缺失预制体实例 2 个，均位于场景根级
    ///       - PrefabInstance fileID 1271640492  (guid c6453f8e1f814744d8b94e5a6d1f9942)
    ///       - PrefabInstance fileID 2101385287  (guid 81154777d5417884981849c5243f6c01)
    ///   · 缺失脚本宿主位于 guid c6453f8e 那个缺失预制体内部（同一根因）
    /// </summary>
    public static class P0BaselineTools
    {
        private const string MenuRoot = "归档/P0 基线治理/";

        [MenuItem(MenuRoot + "① 扫描缺失引用（只读）", false, 10)]
        public static void ScanMissingReferences()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid())
            {
                Debug.LogWarning("[P0] 没有已打开的场景。");
                return;
            }

            List<GameObject> missingPrefabs = new List<GameObject>();
            List<GameObject> missingScripts = new List<GameObject>();

            Collect(scene, missingPrefabs, missingScripts);

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("========== P0 基线治理 · 缺失引用扫描 ==========");
            sb.AppendLine("场景：" + scene.name);
            sb.AppendLine("场景路径：" + scene.path);
            sb.AppendLine();
            sb.AppendLine("【缺失预制体实例】共 " + missingPrefabs.Count + " 个");
            for (int i = 0; i < missingPrefabs.Count; i++)
            {
                GameObject go = missingPrefabs[i];
                sb.AppendLine("  " + (i + 1) + ". " + GetPath(go.transform));
                sb.AppendLine("     判定：" + PrefabUtility.GetPrefabInstanceStatus(go));
            }

            sb.AppendLine();
            sb.AppendLine("【含缺失脚本的对象】共 " + missingScripts.Count + " 个");
            for (int i = 0; i < missingScripts.Count; i++)
            {
                GameObject go = missingScripts[i];
                sb.AppendLine("  " + (i + 1) + ". " + GetPath(go.transform));
            }

            sb.AppendLine();
            sb.AppendLine("===============================================");

            if (missingPrefabs.Count == 0 && missingScripts.Count == 0)
            {
                Debug.Log(sb.ToString() + "\n场景干净，未发现缺失引用。");
            }
            else
            {
                Debug.LogWarning(sb.ToString() + "\n如需清理，请执行菜单：P0 基线治理 / ② 清理缺失预制体实例。");
            }
        }

        [MenuItem(MenuRoot + "② 清理缺失预制体实例（需确认）", false, 11)]
        public static void RemoveMissingPrefabInstances()
        {
            Scene scene = SceneManager.GetActiveScene();
            if (!scene.IsValid())
            {
                Debug.LogWarning("[P0] 没有已打开的场景。");
                return;
            }

            List<GameObject> missingPrefabs = new List<GameObject>();
            List<GameObject> missingScripts = new List<GameObject>();
            Collect(scene, missingPrefabs, missingScripts);

            // 只保留「最上层」的缺失实例：若某实例的祖先也是缺失实例，则跳过，
            // 因为销毁祖先会连带销毁它，重复销毁会产生无效引用。
            List<GameObject> targets = new List<GameObject>();
            for (int i = 0; i < missingPrefabs.Count; i++)
            {
                if (!HasMissingAncestor(missingPrefabs[i]))
                {
                    targets.Add(missingPrefabs[i]);
                }
            }

            if (targets.Count == 0)
            {
                EditorUtility.DisplayDialog(
                    "P0 基线治理",
                    "当前场景未发现可清理的缺失预制体实例。",
                    "好");
                return;
            }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("即将删除以下 " + targets.Count + " 个对象：");
            sb.AppendLine();
            for (int i = 0; i < targets.Count; i++)
            {
                sb.AppendLine("  " + (i + 1) + ". " + GetPath(targets[i].transform));
            }
            sb.AppendLine();
            sb.AppendLine("说明：");
            sb.AppendLine("· 这些对象的源预制体已不在工程中，无法恢复其外观；");
            sb.AppendLine("· 此操作已注册 Undo，可用 Ctrl+Z 撤销；");
            sb.AppendLine("· 不会修改 Assets/TirGames 下的任何文件。");

            if (!EditorUtility.DisplayDialog("P0 基线治理 · 确认清理", sb.ToString(), "删除", "取消"))
            {
                Debug.Log("[P0] 已取消清理。");
                return;
            }

            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("P0 清理缺失预制体实例");

            for (int i = 0; i < targets.Count; i++)
            {
                Undo.DestroyObjectImmediate(targets[i]);
            }

            Undo.CollapseUndoOperations(group);
            EditorSceneManager.MarkSceneDirty(scene);

            Debug.Log("[P0] 已清理 " + targets.Count + " 个缺失预制体实例。请保存场景（Ctrl+S）。");
        }

        private static void Collect(Scene scene, List<GameObject> missingPrefabs, List<GameObject> missingScripts)
        {
            GameObject[] roots = scene.GetRootGameObjects();
            for (int r = 0; r < roots.Length; r++)
            {
                Transform[] all = roots[r].GetComponentsInChildren<Transform>(true);
                for (int i = 0; i < all.Length; i++)
                {
                    GameObject go = all[i].gameObject;

                    if (PrefabUtility.GetPrefabInstanceStatus(go) == PrefabInstanceStatus.MissingAsset
                        && !missingPrefabs.Contains(go))
                    {
                        missingPrefabs.Add(go);
                    }

                    Component[] comps = go.GetComponents<Component>();
                    for (int c = 0; c < comps.Length; c++)
                    {
                        // 脚本丢失时，GetComponents 会返回 null 元素
                        if (comps[c] == null && !missingScripts.Contains(go))
                        {
                            missingScripts.Add(go);
                            break;
                        }
                    }
                }
            }
        }

        private static bool HasMissingAncestor(GameObject go)
        {
            Transform parent = go.transform.parent;
            while (parent != null)
            {
                if (PrefabUtility.GetPrefabInstanceStatus(parent.gameObject) == PrefabInstanceStatus.MissingAsset)
                {
                    return true;
                }
                parent = parent.parent;
            }
            return false;
        }

        private static string GetPath(Transform t)
        {
            List<string> stack = new List<string>();
            Transform cur = t;
            while (cur != null)
            {
                stack.Add(cur.name);
                cur = cur.parent;
            }
            stack.Reverse();
            return string.Join("/", stack.ToArray());
        }
    }
}

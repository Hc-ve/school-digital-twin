using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using SchoolTwin.Player;
using SchoolTwin.Interaction;

namespace SchoolTwin.EditorTools
{
    /// <summary>
    /// 角色接入工具：① 把 FBX 的骨骼类型改成 Humanoid；② 生成角色对象 + 动画控制器。
    ///
    /// ── 为什么必须改成 Humanoid ──────────────────────────────────
    /// Mixamo 导出的 FBX 默认被 Unity 识别成 **Generic**。Generic 的动画片段路径是
    /// 相对**自己那个 FBX 的根节点**的（`Y Bot/Hips` vs `Running/Hips`），
    /// 所以「用 Running.fbx 里的动画去驱动 Y Bot.fbx 的模型」会一条都绑不上 ——
    /// 表现是模型 T-pose 不动，Console 一行错都没有。
    /// Humanoid 靠骨骼映射重定向，路径是什么无所谓，才能跨 FBX 复用动画。
    ///
    /// 用法：菜单 `P22 角色 → ① 设置导入参数` → 等重导入 → `② 生成角色到场景`。
    /// 两步都不要在 Play 模式下点。
    /// </summary>
    public static class TwinCharacterSetup
    {
        private const string Dir = "Assets/_Project/Characters";
        private const string ModelFbx = Dir + "/Y Bot.fbx";
        private const string IdleFbx = Dir + "/Idle.fbx";
        private const string WalkFbx = Dir + "/Standard Walk.fbx";
        private const string RunFbx = Dir + "/Running.fbx";
        private const string ControllerPath = Dir + "/TwinPlayer.controller";

        /// <summary>出生点：2F 走廊（Corridor2 z 28.4~33.4，地面 y≈3.56），面朝 +X。</summary>
        private static readonly Vector3 Spawn = new Vector3(-16f, 3.9f, 31f);
        private const float SpawnYaw = 90f;

        // ================================================================ ① 导入设置

        [MenuItem("P22 角色/① 设置导入参数（Humanoid + 重导入）", false, 10)]
        public static void FixImportsMenu()
        {
            if (EditorApplication.isPlaying) { Refuse(); return; }
            string report = FixImports();
            Debug.Log(report);
            EditorUtility.DisplayDialog("P22 导入设置", report, "好");
        }

        public static string FixImports()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[P22] ===== 设置导入参数 =====");

            string[] files = new string[] { ModelFbx, IdleFbx, WalkFbx, RunFbx };
            int changed = 0, ok = 0;

            for (int i = 0; i < files.Length; i++)
            {
                string path = files[i];
                ModelImporter imp = AssetImporter.GetAtPath(path) as ModelImporter;
                if (imp == null)
                {
                    sb.AppendLine("[P22] ✘ 找不到 " + path + "（文件没放对位置？）");
                    continue;
                }

                bool need = imp.animationType != ModelImporterAnimationType.Human ||
                            imp.avatarSetup != ModelImporterAvatarSetup.CreateFromThisModel ||
                            !imp.importAnimation;

                if (need)
                {
                    imp.animationType = ModelImporterAnimationType.Human;
                    imp.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
                    imp.importAnimation = true;
                    imp.SaveAndReimport();
                    changed++;
                }

                // 重导入后检查 Avatar 有没有生成出来
                Avatar av = FindAvatar(path);
                bool valid = av != null && av.isValid && av.isHuman;
                if (valid) ok++;

                sb.AppendLine("[P22] " + (valid ? "✔ " : "✘ ") + System.IO.Path.GetFileName(path) +
                              "  animationType=" + imp.animationType +
                              "  Avatar=" + (av == null ? "(无)" : (av.isValid ? "有效" : "无效")) +
                              (av != null && av.isHuman ? "  Humanoid✔" : ""));
            }

            // 动画片段必须勾 Loop Time —— Unity 导入 Mixamo 动画默认**不循环**，
            // 播一遍就停在最后一帧：Idle 停住碰巧看着像站着，Walk 停住就是"腿不动"。
            int looped = 0;
            string[] anims = new string[] { IdleFbx, WalkFbx, RunFbx };
            for (int i = 0; i < anims.Length; i++)
            {
                if (EnsureLoop(anims[i])) looped++;
            }

            sb.AppendLine();
            sb.AppendLine("[P22] 改了 " + changed + " 个文件，Avatar 有效 " + ok + " / " + files.Length +
                          "，Loop Time 打开 " + looped + " / " + anims.Length);

            if (ok < files.Length)
            {
                sb.AppendLine("[P22] ⚠ 有文件没生成出有效 Humanoid Avatar。");
                sb.AppendLine("      常见原因：FBX 里的骨骼不是标准人形（缺 T-pose / 缺手指骨骼没关系）。");
                sb.AppendLine("      处理：选中那个 fbx → Inspector → Rig → 点 Configure → 手动映射骨骼 → Done。");
            }

            sb.AppendLine("[P22] ===== 结束 =====");
            try { System.IO.File.WriteAllText("p22_import.txt", sb.ToString(), Encoding.UTF8); } catch (Exception) { }
            return sb.ToString();
        }

        // ================================================================ ② 生成角色

        [MenuItem("P22 角色/② 生成角色到场景（含动画控制器）", false, 11)]
        public static void BuildMenu()
        {
            if (EditorApplication.isPlaying) { Refuse(); return; }
            string report = Build();
            Debug.Log(report);
            EditorUtility.DisplayDialog("P22 生成角色", report, "好");
        }

        public static string Build()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[P22] ===== 生成角色 =====");

            Scene scene = SceneManager.GetActiveScene();
            if (string.IsNullOrEmpty(scene.path)) scene = EditorSceneManager.OpenScene(
                "Assets/TirGames/SchoolScene/Scenes/SchoolSceneDay.unity", OpenSceneMode.Single);

            // 前置：四个 fbx 都得有有效 Avatar
            string[] files = new string[] { ModelFbx, IdleFbx, WalkFbx, RunFbx };
            for (int i = 0; i < files.Length; i++)
            {
                Avatar av = FindAvatar(files[i]);
                if (av == null || !av.isValid || !av.isHuman)
                {
                    sb.AppendLine("[P22] ✘ " + System.IO.Path.GetFileName(files[i]) +
                                  " 还没有有效的 Humanoid Avatar，先跑 ① 设置导入参数。");
                    sb.AppendLine("**本次什么都没改**。");
                    return sb.ToString();
                }
            }

            // 兜底：万一只重跑了 ②，这里也把 Loop Time 补上
            EnsureLoop(IdleFbx);
            EnsureLoop(WalkFbx);
            EnsureLoop(RunFbx);

            AnimationClip idle = FindClip(IdleFbx);
            AnimationClip walk = FindClip(WalkFbx);
            AnimationClip run = FindClip(RunFbx);
            if (idle == null || walk == null || run == null)
            {
                sb.AppendLine("[P22] ✘ 有 fbx 里找不到 AnimationClip（Idle=" + (idle != null) +
                              " Walk=" + (walk != null) + " Run=" + (run != null) + "），什么都没改。");
                return sb.ToString();
            }
            sb.AppendLine("[P22] 动画：Idle=" + idle.name + "  Walk=" + walk.name + "  Run=" + run.name);

            // ---- 动画控制器（1D 混合树：Speed → Idle/Walk/Run）----
            AnimatorController ctrl = BuildController(idle, walk, run, sb);
            if (ctrl == null) return sb.ToString();

            // ---- 角色对象 ----
            GameObject old = GameObject.Find("TwinPlayer");
            if (old != null)
            {
                sb.AppendLine("[P22] 场景里已经有 TwinPlayer，先删掉旧的。");
                Undo.DestroyObjectImmediate(old);
            }

            GameObject go = new GameObject("TwinPlayer");
            Undo.RegisterCreatedObjectUndo(go, "Create TwinPlayer");
            go.transform.position = Spawn;
            go.transform.rotation = Quaternion.Euler(0f, SpawnYaw, 0f);

            CharacterController cc = go.AddComponent<CharacterController>();
            cc.height = 1.8f;
            cc.center = new Vector3(0f, 0.9f, 0f);
            cc.radius = 0.3f;
            cc.stepOffset = 0.35f;      // 能直接上 0.35m 的台阶
            cc.slopeLimit = 45f;
            cc.skinWidth = 0.03f;

            // 【Animator 必须挂在模型自己身上】不要挂在 TwinPlayer 根上：
            // `Y Bot.fbx` 导入时 Unity 会自动给模型根节点挂一个 Animator，
            // 父级再挂一个的话，**父级在播（参数/权重都对）但骨骼被子级那个空 Animator 接管**
            // → 视觉永远停在原地，而 Console 里一切正常（2026-10-23 实测踩过）。
            // 所以：模型自带就用它，没有就加一个；根上一律不留。
            Animator rootAnim = go.GetComponent<Animator>();
            if (rootAnim != null) UnityEngine.Object.DestroyImmediate(rootAnim);


            go.AddComponent<TwinPlayerController>();

            // ---- 模型 ----
            GameObject modelAsset = AssetDatabase.LoadAssetAtPath<GameObject>(ModelFbx);
            GameObject model = (GameObject)PrefabUtility.InstantiatePrefab(modelAsset);
            model.name = "Model";
            model.transform.SetParent(go.transform, false);

            // 脚底对齐 y=0：Mixamo 的轴心不在脚底，不挪的话角色会悬空或陷进地板
            float feet = MeasureFeet(model);
            model.transform.localPosition = new Vector3(0f, -feet, 0f);

            // 动画挂在模型根上
            Animator anim = model.GetComponent<Animator>();
            bool hadOwn = anim != null;
            if (anim == null) anim = model.AddComponent<Animator>();
            anim.runtimeAnimatorController = ctrl;
            anim.applyRootMotion = false;   // 位移交给 TwinPlayerController
            anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;

            // 【必须显式指定 Avatar】只给 runtimeAnimatorController 是不够的：
            // Humanoid 的 Animator 没有 Avatar 就不知道驱动哪套骨骼 → 模型永远 T-pose，
            // 而且 Console 一行错都没有。
            Avatar avatar = FindAvatar(ModelFbx);
            anim.avatar = avatar;

            sb.AppendLine("[P22] 模型自带 Animator=" + hadOwn + "  Avatar=" +
                          (avatar == null ? "(空！动画不会播)" : avatar.name) +
                          "  Controller=" + ctrl.name);
            sb.AppendLine("[P22] 模型脚底对齐：localPosition.y = " + (-feet).ToString("0.000") +
                          "m（" + (feet < 0f ? "整体上移 " : "整体下移 ") + Mathf.Abs(feet).ToString("0.000") + "m）");

            // 角色层：点击射线要排除它，否则第三人称下点不到设备
            int layer = EnsureLayer("TwinPlayer");
            if (layer < 0)
            {
                sb.AppendLine("[P22] ⚠ 没能在 TagManager 里加出 TwinPlayer 层，角色留在 Default 层。" +
                              "（点设备可能被角色挡住 —— 手动加层后重跑）");
            }
            else
            {
                SetLayerRecursive(go, layer);
                sb.AppendLine("[P22] 已把角色放到 TwinPlayer 层（layer " + layer + "）");
            }

            // ---- 相机 ----
            Camera cam = Camera.main;
            if (cam == null)
            {
                sb.AppendLine("[P22] ⚠ 场景里没有 MainCamera，相机脚本没挂。");
            }
            else
            {
                FreeCameraController free = cam.GetComponent<FreeCameraController>();
                if (free != null && free.enabled)
                {
                    free.enabled = false;
                    sb.AppendLine("[P22] 已停用原来的 FreeCameraController（否则两个脚本抢相机）。" +
                                  "想回到自由漫游，把它重新勾上即可。");
                }
                if (cam.GetComponent<TwinThirdPersonCamera>() == null)
                    cam.gameObject.AddComponent<TwinThirdPersonCamera>();
                sb.AppendLine("[P22] 相机已挂 TwinThirdPersonCamera");
            }

            EditorSceneManager.MarkSceneDirty(scene);
            bool saved = EditorSceneManager.SaveScene(scene);
            sb.AppendLine("[P22] 场景保存：" + (saved ? "成功" : "失败"));

            sb.AppendLine();
            sb.AppendLine("[P22] 下一步：按 Play");
            sb.AppendLine("       WASD 走 / 左Shift 跑 / 按住右键转视角 / 滚轮调距离 / F5 切第一人称");
            sb.AppendLine("[P22] ===== 结束 =====");

            try { System.IO.File.WriteAllText("p22_build.txt", sb.ToString(), Encoding.UTF8); } catch (Exception) { }
            return sb.ToString();
        }

        // ================================================================ 工具

        private static AnimatorController BuildController(AnimationClip idle, AnimationClip walk,
                                                          AnimationClip run, StringBuilder sb)
        {
            AnimatorController existing = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            if (existing != null) AssetDatabase.DeleteAsset(ControllerPath);

            AnimatorController ctrl = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
            if (ctrl == null)
            {
                sb.AppendLine("[P22] ✘ 创建动画控制器失败：" + ControllerPath);
                return null;
            }

            ctrl.AddParameter("Speed", AnimatorControllerParameterType.Float);

            AnimatorStateMachine sm = ctrl.layers[0].stateMachine;

            BlendTree tree = new BlendTree();
            tree.name = "Locomotion";
            tree.blendType = BlendTreeType.Simple1D;
            tree.blendParameter = "Speed";
            tree.useAutomaticThresholds = false;
            AssetDatabase.AddObjectToAsset(tree, ctrl);

            // 阈值和 TwinPlayerController 的速度对应：0 站 / 2 走 / 5 跑
            tree.AddChild(idle, 0f);
            tree.AddChild(walk, 2f);
            tree.AddChild(run, 5f);

            AnimatorState state = sm.AddState("Locomotion");
            state.motion = tree;
            state.writeDefaultValues = true;
            sm.defaultState = state;

            EditorUtility.SetDirty(ctrl);
            AssetDatabase.SaveAssets();
            sb.AppendLine("[P22] 动画控制器：" + ControllerPath + "（1D 混合树 Speed 0/2/5）");
            return ctrl;
        }

        private static Avatar FindAvatar(string path)
        {
            UnityEngine.Object[] all = AssetDatabase.LoadAllAssetsAtPath(path);
            for (int i = 0; i < all.Length; i++)
            {
                Avatar a = all[i] as Avatar;
                if (a != null) return a;
            }
            return null;
        }

        /// <summary>
        /// 把某个 FBX 里的动画片段设成循环。返回 true = 本来就要改并且改成功了。
        ///
        /// 【为什么必须做】Unity 导入 Mixamo 动画时 `Loop Time` 默认是**关**的，
        /// 片段播一遍就停在最后一帧。Idle 停住碰巧看着像"站着"，
        /// Walk 停住就是"人滑着走、腿不动" —— 2026-10-23 实测踩过。
        /// </summary>
        private static bool EnsureLoop(string path)
        {
            AnimationClip clip = FindClip(path);
            if (clip == null) return false;

            AnimationClipSettings st = AnimationUtility.GetAnimationClipSettings(clip);
            if (st.loopTime) return false;

            st.loopTime = true;
            AnimationUtility.SetAnimationClipSettings(clip, st);
            EditorUtility.SetDirty(clip);
            AssetDatabase.SaveAssets();
            return true;
        }

        private static AnimationClip FindClip(string path)
        {
            UnityEngine.Object[] all = AssetDatabase.LoadAllAssetsAtPath(path);
            for (int i = 0; i < all.Length; i++)
            {
                AnimationClip c = all[i] as AnimationClip;
                // 跳过 Unity 自动生成的 "preview" 片段
                if (c != null && !c.name.StartsWith("__preview__", StringComparison.Ordinal)) return c;
            }
            return null;
        }

        private static float MeasureFeet(GameObject model)
        {
            Renderer[] rs = model.GetComponentsInChildren<Renderer>();
            if (rs == null || rs.Length == 0) return 0f;

            bool has = false;
            Bounds b = new Bounds(model.transform.position, Vector3.zero);
            for (int i = 0; i < rs.Length; i++)
            {
                if (rs[i] == null) continue;
                if (!has) { b = rs[i].bounds; has = true; }
                else b.Encapsulate(rs[i].bounds);
            }
            if (!has) return 0f;
            return b.min.y - model.transform.position.y;
        }

        /// <summary>
        /// 取层索引，没有就在 TagManager 的空槽里建一个（用户层 8~31）。
        /// 角色必须单独一层：`TwinPicker` 的射线要排除它，否则第三人称下角色会挡住设备点选。
        /// </summary>
        private static int EnsureLayer(string name)
        {
            int exist = LayerMask.NameToLayer(name);
            if (exist >= 0) return exist;

            UnityEngine.Object[] assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset");
            if (assets == null || assets.Length == 0) return -1;

            SerializedObject so = new SerializedObject(assets[0]);
            SerializedProperty layers = so.FindProperty("layers");
            if (layers == null) return -1;

            for (int i = 8; i < layers.arraySize; i++)
            {
                SerializedProperty sp = layers.GetArrayElementAtIndex(i);
                if (!string.IsNullOrEmpty(sp.stringValue)) continue;
                sp.stringValue = name;
                so.ApplyModifiedProperties();
                AssetDatabase.SaveAssets();
                return i;
            }
            return -1;
        }

        private static void SetLayerRecursive(GameObject go, int layer)
        {
            go.layer = layer;
            for (int i = 0; i < go.transform.childCount; i++)
                SetLayerRecursive(go.transform.GetChild(i).gameObject, layer);
        }

        private static void Refuse()
        {
            EditorUtility.DisplayDialog("已拒绝",
                "现在是 Play 模式。请先按停止 —— Play 下改导入设置和存场景都会出问题。", "好");
        }
    }
}

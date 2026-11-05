# 第一人称教室交互漫游项目

> **一句话定位**：把一份导入的静态教室 FBX 模型，改造成可自由漫游、可交互、画面与性能都经过验证的 Unity 第一人称实时场景 Demo。

本项目以 Unity 2022.3 + URP 为基础，围绕「第一人称进入教室 → 与电脑、灯光、座椅、门产生交互」这一条主线，搭建了一套统一的交互框架、一批只在编辑器期运行的安装/诊断/回归工具，以及一套可复现的实测数据。本文面向后续维护者和第一次接手项目的新成员，按「是什么 → 有什么 → 怎么做 → 踩过什么坑 → 怎么跑起来」的顺序组织。

---

## 目录

- [一、项目速览](#一项目速览)
- [二、项目概述](#二项目概述)
- [三、三大亮点功能](#三三大亮点功能)
- [四、功能汇总表](#四功能汇总表)
- [五、技术实现方案](#五技术实现方案)
- [六、效果实拍](#六效果实拍)
- [七、踩坑日志](#七踩坑日志)
- [八、编辑器工具链与验证闭环](#八编辑器工具链与验证闭环)
- [九、关键数据与验证结果](#九关键数据与验证结果)
- [十、代码文件清单与总行数](#十代码文件清单与总行数)
- [十一、外部依赖逐项核查](#十一外部依赖逐项核查)
- [十二、可复用模块清单](#十二可复用模块清单)
- [十三、快速上手](#十三快速上手)
- [十四、维护与扩展](#十四维护与扩展)
- [十五、产物清单](#十五产物清单)

---

## 一、项目速览

| 关键项 | 内容 |
| --- | --- |
| **一句话定位** | 把一份导入的静态教室 FBX 模型，改造成可自由漫游、可交互、画面与性能都经过验证的 Unity 第一人称实时场景 Demo |
| **Unity 版本** | `2022.3.62f3c1`（`ProjectSettings/ProjectVersion.txt`），建议 2022.3.x 同系列 |
| **渲染管线** | URP `14.0.12`，着色器 `Universal Render Pipeline/Lit` |
| **语言与 UI** | C#（Unity Mono）、uGUI `1.0.0`、TextMeshPro `3.0.7`、Timeline `1.7.7` |
| **核心技术** | `Physics.Raycast`、`Rigidbody`、`Terrain` API、`RenderSettings`、`Material.sharedMaterial(s)`、`UnityEditor` 菜单扩展 |
| **主场景** | `Assets/Scenes/SampleScene.unity` |
| **代码规模** | 自写 C# **37 个文件 / 7268 行**（运行时 9 / 1179 行，编辑器 28 / 6089 行）；全工程 **40 个 .cs / 8268 行**，另有文档绘图脚本 606 行 |
| **Asset Store 包** | Package Manager 中**无商店包**，依赖均为 `com.unity.*` 官方包；仅 `Assets/ModularFirstPersonController/` 是以源码形式引入的第三方控制器，工程内未附授权文件 |
| **外部服务** | **无**。Analytics / Ads / IAP 全部关闭，工程内无联网代码，可完全离线开发与运行 |
| **可复用模块** | **5 个运行时脚本 / 738 行**零改动可直接搬到其他工程 |

完整技术栈见 5.1 技术栈，文件级行数明细见第十章，依赖逐项核查见第十一章。

**六项关键信息速查**

| 项 | 要求 | 本文位置 |
| --- | --- | --- |
| ① | 项目一句话定位 | 本章表格首行 |
| ② | 技术栈与 Unity 版本 | 本章表格 + [5.1 技术栈](#51-技术栈) |
| ③ | 3 个最有亮点的功能，每个附运行截图 + 实现原理 | [第三章 三大亮点功能](#三三大亮点功能) |
| ④ | 代码文件清单与总行数 | [第十章 代码文件清单与总行数](#十代码文件清单与总行数) |
| ⑤ | 是否依赖 Asset Store 包 / 外部服务（逐项列） | [第十一章 外部依赖逐项核查](#十一外部依赖逐项核查) |
| ⑥ | 哪些模块自包含可拆出复用 + 文件清单 | [第十二章 可复用模块清单](#十二可复用模块清单) |

---

## 二、项目概述

### 2.1 背景与定位

项目起点是一份从模型库导入的机房/教室 FBX（`机房.FBX`，约 737 MB，占 `Assets` 总体积的九成以上）。这份模型**只有几何与材质，没有任何交互逻辑**：两千多个 `MeshRenderer`、四千多万个三角面里，绝大部分是同一套桌椅、灯具、门被复制出来的重复副本；场景没有可用碰撞、没有第一人称控制、没有交互提示，灯光开关也没有任何视觉反馈。

因此本项目的定位不是「再做一遍建模」，而是**在既有静态资产上补齐实时交互与工程化能力**，具体解决四类问题：

| 问题 | 表现 | 本项目做法 |
| --- | --- | --- |
| 静态资产不可交互 | 模型只是网格，玩家进去什么都点不动 | 抽象出 `Interactable` 基类 + 相机射线判定，批量挂载交互组件 |
| 交互反馈不可见 | 开关灯前后画面几乎没差别 | 灯盘自发光材质 + 环境光倍率 + 后处理联动，做出肉眼可辨的明暗反差 |
| 场景单薄 | 教室外面光秃秃，缺少空间感 | 草地 Terrain 层、树、远山、指数平方雾 |
| 性能隐患无依据 | 两千多份重复网格，凭感觉优化 | 几何签名去重 + GPU Instancing，并用分组 FPS 归因定位真实瓶颈 |

### 2.2 核心业务场景

玩家以第一人称从走廊侧「进入教室」，开场过渡结束后光标锁定、控制器接管，随后可以：

1. 走到任意一台电脑前，准星对准屏幕按 `Q` 开关机；
2. 走到门边墙面开关前，按 `E` 控制全屋灯光；
3. 走到任意一把椅子前，按 `E` 坐下（相机平滑降到坐姿高度、身体锁死、只能转头），再按 `E` 起身；
4. 靠近教室门时门自动打开、离开后延时关闭；
5. 透过侧窗看到室外草地、树和远山，形成完整的空间氛围。

一句话概括：**一个「进得去、看得懂、点得动、跑得动」的可漫游教室 Demo**，同时作为一套 Editor 自动化工具链的实践载体。

### 2.3 系统分层

系统按五层组织，完整架构见下图：

![系统架构总览](docs/images/01-system-architecture.png)

| 层 | 职责 | 主要对象 |
| --- | --- | --- |
| L1 表现与 UI 层 | 开场过渡、交互提示、后处理画面 | `StartupTransition`、`InteractionPromptUI`、`Global Volume` |
| L2 交互逻辑层（Runtime） | 射线判定、交互抽象、状态切换 | `PlayerInteractor`、`Interactable` 及三个子类、`AutoDoorSystem` |
| L3 场景与玩家层 | 第一人称控制、静态几何、地形、灯光相机 | `FirstPersonController`、`机房.FBX`、`Terrain` |
| L4 渲染与引擎层 | 引擎 API、渲染管线、材质批处理 | Unity 2022.3、URP 14、`Physics.Raycast`、`sharedMaterials`、GPU Instancing |
| L5 编辑器工具层（Edit Time） | 安装、诊断、验证、出图 | `Assets/Editor/` 下全部脚本 |

> 设计约束：**运行时脚本只读取已保存的组件引用，不创建资源**。所有物理碰撞挂载、材质生成、灯盘/门扇绑定都由 Editor 工具在编辑器期写入 `SampleScene.unity` 并保存，保证 Play 模式下行为可复现、不依赖编辑器 API。

---

## 三、三大亮点功能

从全部已实现功能里挑出最有代表性的三个：分别对应「玩家状态机」「多子系统联动」「第三方模型层级缺陷修补」，也是踩坑最集中的地方。每个功能附一张 Play 模式运行截图与一段实现原理，完整功能清单见 [第四章 功能汇总表](#四功能汇总表)。

### 亮点一 · 座椅坐下 / 起身

> 49 把椅子全部可坐：相机在 0.55 s 内平滑降到坐姿高度，坐着只能转头、身体锁死；视线移开椅子也照样能按 `E` 起身。

![坐姿视角运行截图：坐下后的第一人称视角](docs/images/effect-sit.png)

**实现原理**：`InteractableSeat` 用静态 `Current` 记录当前座位。坐下前先把 `FirstPersonController` 的 `playerCanMove / enableJump / enableCrouch / enableSprint / enableHeadBob` 全部保存并置 false，只保留 `cameraCanMove = true`；同时把 `Rigidbody.isKinematic = true` 关掉重力，避免玩家被压进地面。相机高度不瞬移，而是在 `Update()` 里用 `Time.deltaTime / sitDuration` 配合 `Mathf.SmoothStep` 插值 `joint.localPosition.y`，实测眼高 `1.166 → 0.954 m`（下沉 `0.212 m`）。难点在于「坐下后准星自然会移开椅子」，所以 `PlayerInteractor.Update()` 在 `Current != null` 时提前短路、锁定该座位，不再走射线判定，提示恒为 `[E] 起身`；起身时逐项还原控制器开关与 `isKinematic`，实测眼高回到 `1.166 m`、误差 `0`。批量安装由 `SeatSetup.cs` 按 `x/z` 容差 `0.3` 把 147 个零件聚成 49 把椅子。

### 亮点二 · 一次按键驱动四路灯光联动

> 按一次 `E`，5 盏射灯、19 块灯盘、全局环境光、开关指示灯同时切换；关灯后室内平均亮度下降 62.8%，从「几乎看不出变化」变成肉眼可辨。

![关灯全景运行截图：射灯熄灭、灯盘变暗、环境光压低](docs/images/effect-light-off.png)

**实现原理**：`InteractableLightSwitch.SetState()` 一次调用并行做四件事——遍历 `lights[]` 切 `Light.enabled`、遍历 `ceilingLamps[]` 调 `CeilingLamp.SetLit()`（整槽替换 `sharedMaterials`，亮态 `Emission=(2,1.7,1.3)`、灭态 `(0,0,0)`）、把 `RenderSettings.ambientIntensity` 设为 `_ambientBase * 0.72`、切换面板 `indicator` 材质。只关射灯不够：平行光与 Skybox 环境光仍在，画面几乎不变，所以必须给玩家一个**可见的发光体**当视觉锚点，再叠加环境光压暗与 Bloom 消失。`Awake()` 会缓存 `_ambientBase`，避免反复开关把 `0.72` 累乘成越来越暗。开灯对照见 `effect-light-on.png`，19 块灯盘的亮灭特写见 [第六章 效果实拍](#六效果实拍)。

### 亮点三 · 自动门与兄弟部件同步

> 玩家靠近门自动开启、离开延时关闭；门扇转动时玻璃、把手、门框等兄弟节点一起绕铰链走，不会「门转了、部件留在原地」。

![门口运行截图：敞开的门扇、门框与墙面开关](docs/images/effect-door.png)

**实现原理**：FBX 导出的门是「pivot 薄板 + 若干兄弟节点」的组合，只旋转 pivot 会留下悬空的玻璃和把手。`AutoDoorInstaller` 先按材质名含「门」的竖直薄板识别门扇，`DoorPartsBinder` 在安装期把跟随部件记入 `Door.extraParts` 并缓存其相对铰链的初始位姿；运行时 `AutoDoorSystem.Apply()` 计算 `worldDelta = d.pivot.rotation * Quaternion.Inverse(d.closedWorldRot)`，再用 `p.position = hinge + worldDelta * (partPos - hinge)` 与 `p.rotation = worldDelta * partRot` 统一换算，保证整组刚体式同步。开合角度由 `DecideAngle()` 依据玩家与门的相对位置决定（默认 90°），`avoidSweepingPlayer` 防止门扇扫到站在门边的玩家。冒烟测试曾抓到门 B「旋转 0°、0/7 部件未动」，修好后门 A 的 8/8 部件最大位移 `1.431 m`，全部跟随。

---

## 四、功能汇总表

下表汇总当前项目已实现的全部功能，严格按 `功能模块 / 功能点 / 交互说明` 三列组织。带「自动」标记的条目无需玩家操作，属于配套能力。

| 功能模块 | 功能点 | 交互说明 |
| --- | --- | --- |
| 电脑交互 | 屏幕开机 / 关机 | 准星对准电脑屏幕（共 49 台）按 `Q`，在「屏幕_开机 / 电脑屏幕_Albedo」材质间切换；再按一次关机 |
| 电脑交互 | 按键与提示同源 | 提示条显示 `[Q] 开机` / `[Q] 关机`，按键由 `InteractablePC.GetKey()` 决定，不写死在全局 |
| 灯光交互 | 全屋灯光开关 | 准星对准门边墙面开关按 `E`，一次同时控制 5 盏 Spot Light 的 `enabled` |
| 灯光交互 | 灯盘亮灭自发光 | 同一操作驱动 19 块 `CeilingLamp` 替换材质槽位，亮态 `Emission=(2,1.7,1.3)`、灭态 `(0,0,0)`，肉眼可见灯盘本身亮/灭 |
| 灯光交互 | 环境光压暗 | 关灯时 `RenderSettings.ambientIntensity` 降为基准值的 `0.72`，配合 Bloom 消失形成明暗反差 |
| 灯光交互 | 开关指示灯 | 面板 `indicator` 随状态切换亮/灭材质，指示灯前表面外推约 0.0017 m 以避免 Z-fighting |
| 灯光交互 | 环境光基准缓存 | `Awake()` 缓存 `_ambientBase`，反复开关不会把环境光越乘越暗 |
| 座椅交互 | 靠近坐下 | 准星对准座椅（共 49 把）按 `E`，相机用 `SmoothStep` 在 0.55 s 内平滑下降到坐姿高度，眼睛下沉约 0.212 m |
| 座椅交互 | 坐姿身体锁死 | 坐下时关闭 `playerCanMove / enableJump / enableCrouch / enableSprint / enableHeadBob`，`Rigidbody.isKinematic = true` 防止重力下陷；保留 `cameraCanMove`，只能转头 |
| 座椅交互 | 视线移开仍可起身 | `InteractableSeat.Current` 静态记录当前座位，坐下期间交互判定短路锁定该座位，准星移开也稳定显示 `[E] 起身` |
| 座椅交互 | 起身复位 | 再按 `E` 还原相机高度与控制器全部开关，`Current` 置空，提示回到 `[E] 坐下` |
| 自动门 | 距离触发开合 | 独立于 `Interactable`，玩家靠近自动开门、离开延时关闭；场景 2 扇门 |
| 自动门 | 门扇跟随部件 | `Door.extraParts` 记录玻璃、把手等兄弟节点，用 `worldDelta` 绕铰链同步旋转，避免门扇转了部件留在原地 |
| 自动门 | 避让玩家 | `avoidSweepingPlayer` 防止门扇扫到站在门边的玩家 |
| 交互框架 | 统一抽象 | `Interactable` 定义 `prompt / range / canInteract / interactKey` 与 `GetPrompt() / GetKey() / CanInteract() / Interact()` |
| 交互框架 | 相机中心射线判定 | `PlayerInteractor` 从相机 forward 发 `Physics.Raycast`，`GetComponentInParent<Interactable>()` 向上找交互物，双重范围校验 |
| 交互框架 | 开场交互门控 | 过渡期间 `StartupTransition.IsTransitioning = true`，交互与提示一律屏蔽，过渡结束才放开控制权与光标 |
| 交互框架 | 交互提示条 | `InteractionPromptUI` 用 `CanvasGroup.alpha` 淡入淡出，文字由命中物体的 `GetPrompt()` + `GetKey()` 拼出 |
| 第一人称 | 移动与转头 | WASD 移动、鼠标转头，光标锁定；由 `ModularFirstPersonController` 提供 |
| 画面与场景 | 室外景观 | 草地 Terrain 层（tileSize 6 m、alphamap 512）、88 棵树、远山、指数平方雾（density 0.012） |
| 画面与场景 | 画质档 | 阴影距离、MSAA、HDR、后处理（Tonemapping / Bloom / Vignette）统一配置 |
| 性能（自动） | Mesh 几何去重 | 按几何签名分组重定向 `MeshFilter.sharedMesh`，2069 个引用被合并，唯一几何组 85 |
| 性能（自动） | GPU Instancing | 对 2 个以上 Renderer 共用的材质开启 `enableInstancing`，35 / 48 个材质生效 |
| 工程化（自动） | 一键安装与验证 | `Assets/Editor/` 下 28 个脚本，覆盖安装、探针、诊断、冒烟测试、截图与报告输出 |

---

## 五、技术实现方案

### 5.1 技术栈

| 类别 | 采用的技术 / 版本 |
| --- | --- |
| 引擎 | Unity `2022.3.62f3c1` |
| 渲染管线 | URP `14.0.12`，着色器 `Universal Render Pipeline/Lit` |
| 语言 | C#（Unity Mono / .NET Standard 2.1） |
| UI | uGUI `1.0.0`、TextMeshPro `3.0.7` |
| 过场 | Timeline `1.7.7`（开场过渡） |
| 第一人称 | `ModularFirstPersonController`（`FirstPersonController.cs`） |
| 物理与射线 | `Physics.Raycast`、`Rigidbody`、`MeshCollider` |
| 地形 | `Terrain` / `TerrainLayer` / alphamap API |
| 渲染控制 | `RenderSettings`（ambient / fog）、`Material.sharedMaterial(s)`、`_EMISSION` 关键字 |
| 后处理 | `Global Volume`（Tonemapping、Bloom、Vignette） |
| 编辑器扩展 | `MenuItem`、`InitializeOnLoad`、`AssetDatabase`、`EditorSceneManager`、`Undo.AddComponent` |

### 5.2 交互判定主流程

`PlayerInteractor.Update()` 每帧跑一遍固定流程，完整流程图如下（图内箭头与判断条件与代码一一对应）：

![交互判定主流程](docs/images/02-interaction-flow.png)

关键逻辑（`Assets/Scripts/Interaction/PlayerInteractor.cs`）：

```csharp
// 1. 开场过渡期间一律不响应
if (StartupTransition.IsTransitioning) { SetCurrent(null); return; }

// 2. 坐着时短路：锁定当前座位，不再看准星
if (InteractableSeat.Current != null)
{
    SetCurrent(InteractableSeat.Current);
    if (Input.GetKeyDown(KeyOf(InteractableSeat.Current)))
        InteractableSeat.Current.Interact(gameObject);
    return;
}

// 3. 相机中心射线 + 向上找交互物 + 双重范围校验
if (Physics.Raycast(viewCamera.transform.position, viewCamera.transform.forward,
        out RaycastHit hit, range, mask, QueryTriggerInteraction.Collide))
{
    Interactable it = hit.collider.GetComponentInParent<Interactable>();
    if (it != null && it.CanInteract() && hit.distance <= it.range) found = it;
}

// 4. 提示与按键都由物体自己决定
InteractionPromptUI.Show(found.GetPrompt(), found.GetKey());
if (Input.GetKeyDown(KeyOf(found))) found.Interact(gameObject);
```

三个设计取舍值得留意：

- **按键不写死在全局**。最初电脑与座椅都用 `E`，后来需求把电脑改成 `Q`。如果只在 `PlayerInteractor` 里改按键，提示条会显示 `[E]` 而实际要按 `Q`。因此改成 `Interactable.GetKey()` 虚方法，每个交互物自己返回按键，提示 UI 与判定共用同一个 `KeyOf()`，从源头消除不一致。
- **`GetComponentInParent` 而非 `GetComponent`**。FBX 导出的层级很深（大量 `Group-*` 嵌套、`scale=0.001` 甚至负缩放），交互组件常挂在父节点，射线命中的是子网格，向上查找才稳。
- **坐下状态优先级最高**。坐下后准星自然会移开椅子，若仍走射线判定会立刻丢失「起身」入口，所以用静态 `Current` 把这条分支提前短路。

### 5.3 核心模块与类关系

![核心模块与类关系](docs/images/03-module-map.png)

`Interactable` 是唯一抽象入口，三个子类分别实现状态切换；`PlayerInteractor` 只依赖抽象类，不关心具体是电脑还是椅子；`AutoDoorSystem` 因为由距离而非准星触发，刻意不继承 `Interactable`；`StartupTransition` 用静态标志做全局门控。

### 5.4 各功能关键实现逻辑

#### 电脑开关机（`InteractablePC.cs`，场景实例 49）

```csharp
public override KeyCode GetKey()
    => powerKey == KeyCode.None ? KeyCode.Q : powerKey;

void SetState(bool on)
{
    _on = on;
    if (screenRenderer == null) return;
    screenRenderer.sharedMaterial = on ? onMaterial : offMaterial;
}
```

开机材质在 `InteractionSetup.cs` 里创建并开启自发光，让黑屏与亮屏有明确区别：

```csharp
onMat.EnableKeyword("_EMISSION");
onMat.SetColor("_EmissionColor", new Color(0.35f, 0.72f, 1f) * 1.9f);
```

> 使用 `sharedMaterial` 而非 `material`：49 台屏幕共享材质，避免在运行时实例化出几十份材质副本。

#### 灯光联动（`InteractableLightSwitch.cs` + `CeilingLamp.cs`，实例 1 + 19）

一次 `E` 触发四路并行切换，时序如下图：

![灯光开关联动时序](docs/images/04-light-sequence.png)

```csharp
void Awake() { _ambientBase = RenderSettings.ambientIntensity; }  // 缓存基准，防浮点漂移

void SetState(bool on)
{
    _on = on;
    for (int i = 0; i < lights.Length; i++)
        if (lights[i] != null) lights[i].enabled = on;             // 5 盏 Spot

    for (int i = 0; i < ceilingLamps.Length; i++)
        if (ceilingLamps[i] != null) ceilingLamps[i].SetLit(on);   // 19 块灯盘

    if (dimAmbient)
        RenderSettings.ambientIntensity = on ? _ambientBase
                                             : _ambientBase * ambientScaleWhenOff; // 0.72
}
```

`CeilingLamp.SetLit()` 通过整槽替换 `sharedMaterials` 实现可见亮灭，仅在确实变化时才写回：

```csharp
var mats = panel.sharedMaterials;
for (int i = 0; i < mats.Length; i++)
    if (mats[i] != want) { mats[i] = want; changed = true; }
if (changed) panel.sharedMaterials = mats;
```

**为什么必须做灯盘**：只关 5 盏 Spot 时，平行光与 Skybox 环境光仍在，画面几乎不变（用户实测反馈「开关灯不明显」）。叠加「灯盘自身可见亮灭 + 环境光压暗 + Bloom 消失」之后，室内全景平均亮度从 `0.347` 降到 `0.129`（下降 62.8%），天花板视角从 `0.311` 降到 `0.130`（下降 58%），才有肉眼可辨的反差。

#### 座椅交互（`InteractableSeat.cs`，实例 49）

```csharp
public static InteractableSeat Current;   // 全局唯一当前座位

// 坐下：保存并关闭第一人称控制器全部身体能力
_fpc.playerCanMove = false;
_fpc.enableJump = false;
_fpc.enableCrouch = false;
_fpc.enableSprint = false;
_fpc.enableHeadBob = false;
_rb.isKinematic = true;                   // 关键：否则重力把玩家压进地面

// 相机平滑下沉，而不是直接赋值
_t += Time.deltaTime / Mathf.Max(0.05f, sitDuration);
float k = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(_t));
jp.y = Mathf.Lerp(_fromJointY, _toJointY, k);
```

坐下前保存所有开关，起身时逐项还原。批量安装由 `SeatSetup.cs` 完成：按 `x/z` 容差 `0.3` 去重，`seatSurfaceY` 取包围盒顶面，`eyeAboveSeat = 0.52`、`sitDuration = 0.55`、`range = 2.2`。实测坐下前眼高 `1.166 m / joint.localY=0.75`，坐下后 `0.954 m / 0.432`，起身后回到 `1.166 m`，误差 `0`。

#### 自动门（`AutoDoorSystem.cs`，实例 1，管理 2 扇门）

门扇是 FBX 里的一个薄板，但玻璃、把手、门框是**兄弟节点**，只转门扇会导致部件留在原地。做法是安装期记录相对位姿，运行时用四元数增量统一带动：

```csharp
Quaternion worldDelta = d.pivot.rotation * Quaternion.Inverse(d.closedWorldRot);
p.position = d.hinge + worldDelta * (d.partPos[i] - d.hinge);
p.rotation = worldDelta * d.partRot[i];
```

同时 `avoidSweepingPlayer` 保证门扇不会扫到站在门边的玩家。开合角度由 `AutoDoorInstaller.DecideAngle()` 依据门与玩家相对位置决定（默认 90°）。

#### 提示 UI 与开场过渡（`InteractionPromptUI.cs` / `StartupTransition.cs`）

- 提示条：`CanvasGroup.alpha` 在 0/1 之间淡入淡出，`sortingOrder = 50`，文字格式为 `"[" + KeyName(key) + "] " + text`；空文本直接淡出。
- 开场过渡：`IsTransitioning` 静态属性做全局交互门控，结束时按配置放开控制器、锁定并隐藏光标：

```csharp
if (enablePlayerAtEnd) playerController.enabled = true;
if (lockCursorAtEnd) { Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; }
IsTransitioning = false;
```

#### 室外景观与性能（`OutdoorScenerySetup.cs` / `MeshDedupApply.cs`）

- 景观：四块区域种树共 88 棵，加草地 Terrain 层，开 `FogMode.ExponentialSquared`、`fogDensity = 0.012`。
- 去重：FBX 导入的网格大多 `isReadable = false`（2152 个），**不能逐顶点比对**，因此改用「几何签名」（顶点数 + 包围盒等）分组，把同组 `MeshFilter.sharedMesh` 指向同一份保留网格；再对 2 个以上 Renderer 共用的材质开启 `enableInstancing`。

### 5.5 核心代码模块清单

运行时脚本（`Assets/Scripts/`，共 9 个）：

| 文件 | 职责 |
| --- | --- |
| `Interaction/Interactable.cs` | 交互抽象基类，定义提示、范围、按键、可交互判定与 `Interact()` |
| `Interaction/InteractablePC.cs` | 电脑开关机，49 台，按键 `Q` |
| `Interaction/InteractableLightSwitch.cs` | 灯光开关，联动 5 盏灯 + 19 灯盘 + 环境光 + 指示灯 |
| `Interaction/InteractableSeat.cs` | 坐下 / 起身，相机平滑下沉、身体锁死、静态 `Current` |
| `Interaction/CeilingLamp.cs` | 灯盘亮灭材质槽位切换 |
| `Interaction/PlayerInteractor.cs` | 相机射线判定、提示分发、按键分发、坐下短路 |
| `Interaction/AutoDoorSystem.cs` | 距离触发自动门 + 跟随部件四元数增量 |
| `UI/InteractionPromptUI.cs` | 提示条淡入淡出与 `[Q]/[E]` 文案拼装 |
| `UI/StartupTransition.cs` | 开场过渡与 `IsTransitioning` 全局门控 |

编辑器脚本（`Assets/Editor/`，共 28 个），按用途分组：

| 分组 | 脚本 |
| --- | --- |
| 安装类 | `InteractionSetup.cs`、`SeatSetup.cs`、`CeilingLampSetup.cs`、`AutoDoorInstaller.cs`、`DoorPartsBinder.cs`、`OutdoorScenerySetup.cs`、`StartupCanvasBuilder.cs`、`StartupCanvasAutoSetup.cs` |
| 生成与优化类 | `MeshDedupAnalysis.cs`、`MeshDedupApply.cs`、`ProjectPolish.cs`、`ProjectOptimizer.cs`、`LightProbe.cs` |
| 探针与诊断类 | `SceneGeomProbe.cs`、`WallProbe.cs`、`OutsideProbe.cs`、`InteractDiag.cs`、`SwitchInspect.cs`、`ProjectHealthCheck.cs` |
| 验证与出图类 | `InteractionVerify.cs`、`SeatVerify.cs`、`DoorVerify.cs`、`KeyMapVerify.cs`、`PlaySmokeTest.cs`、`PerfProbe.cs`、`InteractionShots.cs`、`LampShots.cs`、`SceneryShots.cs` |

---

## 六、效果实拍

以下截图均为项目内 Play 模式实拍，随文档一同提供。

### 灯光开关前后

| 开灯 | 关灯 |
| --- | --- |
| ![开灯全景](docs/images/effect-light-on.png) | ![关灯全景](docs/images/effect-light-off.png) |

![灯光开关面板](docs/images/effect-switch.png)

**灯盘特写对照**（19 块灯盘的自发光材质切换，是关灯可见变化的关键）：

| 开灯灯盘 | 关灯灯盘 |
| --- | --- |
| ![开灯灯盘](docs/images/effect-lamp-on.png) | ![关灯灯盘](docs/images/effect-lamp-off.png) |

### 电脑与座椅

| 电脑提示 | 座椅提示 | 坐姿视角 |
| --- | --- | --- |
| ![电脑提示](docs/images/effect-pc.png) | ![座椅提示](docs/images/effect-seat.png) | ![坐姿视角](docs/images/effect-sit.png) |

### 室外景观

![室外俯瞰](docs/images/effect-outdoor.png)

---

## 七、踩坑日志

以下均为开发过程中实际遇到并解决的问题，括号内为对应的实测报告文件，便于回溯。

#### 坑 1 · 准星对着电脑和开关没反应

- **现象**：进场景后把准星对准电脑屏幕、墙面开关，按键毫无反应，也没有提示。
- **定位**：`InteractDiag.cs` 在过渡结束后逐项打印状态（`交互诊断结果.txt`）：`Cursor=Locked`、`IsTransitioning=False`、`PlayerInteractor` 挂在 `FirstPersonController` 且 `enabled=True`、`mask=-1`、相机=`PlayerCamera`，射线能命中 `Quad (12)` 距离 `1.31 m`、`CanInteract=True`、提示 alpha 正常到 1。
- **原因**：开场过渡期间 `StartupTransition.IsTransitioning` 为 true，且控制器未接管；若交互组件/相机引用没被正确写入场景，射线与提示链路整条断开。
- **解决**：由 `InteractionSetup.cs` 一键完成挂载与引用绑定（`PlayerInteractor.range = 2.8`、`mask = ~0`、绑定相机与提示 UI），并保留过渡期的全局门控，避免过渡中误触。`交互验证结果.txt` 复测：按 `E` 后屏幕材质由 `电脑屏幕_Albedo` 切到 `屏幕_开机`，提示从 `[E] 开机` 变 `[E] 关机`。
- **教训**：交互链路是「过渡门控 → 控制器 → 射线 → 组件 → 提示 → 按键」六段串联，任何一段缺引用都会表现为「完全没反应」，必须逐段打印状态而不是只看最后一段。

#### 坑 2 · 开关面板贴不上墙、还会被门扇扫到

- **现象**：手工摆开关位置，要么陷进墙里，要么正好落在自动门旋转范围内，门一开就把开关带走/挡住。
- **定位**：`WallProbe.cs`（`墙面探测结果.txt`）在 `y=1.30` 沿 x 从门洞向外扫，读到各墙面 z 值（`z=3.324 / 2.624 / 2.475 / 2.394`），门洞中心在不同高度命中不同物体。
- **解决**：`PlaceSwitchBesideDoor()` 采样 7 个点选位；`InteractionVerify.cs` 额外校验两条硬约束——面板背后 `0.5 m` 内有墙（实测距 `0.014 m`，True）、站位到面板无遮挡（True）；并计算「面板距转轴 8.23 m / 1.77 m，门扇半径 0.73 m」，确认都在摆动范围外（✓）。
- **附带技巧**：探测时临时给全部 `MeshRenderer` 加 `MeshCollider`（`2151` 个），探完**全部移除且不保存场景**，避免污染资产。

#### 坑 3 · 开关指示灯看不见（Z-fighting）

- **现象**：指示灯建好了但正面看不到。
- **定位**：`SwitchInspect.cs`（`开关面板体检.txt`）量出面板前表面 `z=3.2974`、指示灯前表面比它只靠前 `0.0017 m`，报告明确写了「>0 才看得见」。
- **解决**：把指示灯沿面板法线外推极小距离，保证恒为正；同时用从正前方 `0.6 m` 打射线复验（命中「灯光开关」，距离 `0.587`）确认可被准星命中。

#### 坑 4 · 开关灯「看不出来」

- **现象**：用户反馈开关灯不明显。
- **原因**：只切 5 盏 Spot 的 `enabled`，平行光 + Skybox 环境光仍在，画面变化很小。
- **解决**：三管齐下——19 块灯盘自发光材质切换、环境光降到 `0.72`、Bloom 随之消失。`灯光对比结果.txt` 量化：室内全景平均亮度 `0.347 → 0.129`（-62.8%），天花板视角 `0.311 → 0.130`（-58%）。
- **教训**：「灯亮不亮」在实时渲染里不等于「光源开没开」，必须给玩家一个**可见的发光体**作为视觉锚点。

#### 坑 5 · 环境光反复开关后越来越暗

- **原因**：早期写成 `RenderSettings.ambientIntensity *= 0.72f`，每次关灯都在上一次结果上继续乘。
- **解决**：`Awake()` 缓存 `_ambientBase`，开灯时赋回基准值、关灯时用 `基准 × 0.72`，绝不累乘。

#### 坑 6 · 自动门 B 扇转了但部件不动 / 干脆没转

- **现象**：`PlaySmokeTest.cs`（`冒烟测试结果.txt`）显示门 A 实际旋转 90°、`8/8` 跟随部件移动、最大位移 `1.431 m`；门 B 实际旋转 **0°**、`0/7` 部件移动、位移 `0`。
- **原因**：门扇与玻璃/把手是兄弟节点，只转 pivot 不会带动它们；B 扇的开合角度/触发条件也需要按玩家相对位置判定。
- **解决**：`DoorPartsBinder.cs` 安装期把跟随部件记入 `Door.extraParts`，`AutoDoorSystem.Apply()` 用 `worldDelta = pivot.rotation * Quaternion.Inverse(closedWorldRot)` 统一换算位置与旋转；`AutoDoorInstaller.DecideAngle()` 决定开门方向，`avoidSweepingPlayer` 避免扫到玩家。
- **教训**：门不是「一个物体」，是「一个 pivot + 一组兄弟部件」，任何旋转逻辑都必须显式带动整组。

#### 坑 7 · 坐下时相机穿地 / 身体还能动

- **现象**：坐下后玩家被重力继续下压，且坐下状态仍能 WASD 移动。
- **解决**：`Rigidbody.isKinematic = true` 关掉重力；坐下前保存并关闭 `playerCanMove / enableJump / enableCrouch / enableSprint / enableHeadBob`，只留 `cameraCanMove = true`；相机高度用 `SmoothStep` 插值而非瞬移。`座椅验证结果.txt` 实测坐下前眼高 `1.166 m`、坐下后 `0.954 m`（下沉 `0.212 m`）、起身后回到 `1.166 m`（差 `0`），且 `cameraCanMove=True`、`isKinematic=True`。
- **教训**：**保存 → 修改 → 还原**是状态类交互的固定套路，起身时必须逐项还原，不能凭记忆补。

#### 坑 8 · 坐下后准星移开，就再也起不来

- **现象**：坐下后视线自然离开椅子，射线判定立刻找不到交互物，「起身」提示消失。
- **解决**：引入 `InteractableSeat.Current` 静态引用，`PlayerInteractor.Update()` 在坐下期间优先短路并锁定该座位，提示恒为 `[E] 起身`。`座椅验证结果.txt` 专门验了「坐着且视线移开椅子后」这一条。

#### 坑 9 · 按 Q 却提示 [E]，按键与提示不一致

- **现象**：需求把电脑开关机从 `E` 改成 `Q`，若只改判定按键，提示条仍显示 `[E]`。
- **解决**：把按键下放到 `Interactable.GetKey()`，判定与提示共用 `KeyOf()`。`KeyMapVerify.cs`（`按键改配结果.txt`）逐项复验：电脑 `Q=49 / 其它=0`、座椅 `E=49`、兜底键 `E`、range `2.8`；三种场景提示分别为 `[[Q] 开机]`、`[[E] 关灯]`、`[[E] 坐下]`，与期望全部一致（✔）。

#### 坑 10 · FBX 网格不可读，无法按顶点去重

- **现象**：`Mesh重复度报告.txt` 显示 `MeshFilter` 共 `2203` 个，其中 `isReadable=false` 的有 `2152` 个，直接读顶点数组会失败。
- **解决**：改用「几何签名」（顶点数、包围盒等可读的元数据）分组，得到唯一几何签名 `82`、重复组 `55`、多余实例 `2121`、估算冗余内存约 `284 MB`；`MeshDedupApply.cs` 把组内 `MeshFilter.sharedMesh` 重定向到保留网格，最终重定向 `2069` 个引用、开启 Instancing 材质 `35/48`（`Mesh去重结果.txt`）。
- **教训**：面对第三方导入资产，先探测「哪些数据可读」再决定算法，不要在不可读数据上设计流程。

#### 坑 11 · 把 FPS 低归咎于树和雾（误判瓶颈）

- **现象**：`冒烟测试结果.txt` 里平均帧率只有 `38.7 FPS`，直觉认为是室外树和雾太重。
- **定位**：`PerfProbe.cs`（`性能归因结果.txt`）先预热 12 秒编译完着色器变体，再分组对比：全开 `120.1`、关树 `120.2`、关雾 `119.8`、都关 `120.9` FPS。
- **结论**：树和雾几乎不耗性能，真正的差距来自**着色器预热/编译**，不是场景内容。
- **教训**：性能对比必须在预热之后做，否则测到的是编译开销；归因要用分组对照，不要凭直觉砍功能。

---

## 八、编辑器工具链与验证闭环

项目里所有批量操作都放在 `Assets/Editor/`，通过 `工具/场景/...` 菜单调用，配合 `[InitializeOnLoad]` + `Temp/*_pending` 标记文件实现「延迟到编辑器空闲再执行」，避免与 Unity 导入流程打架。工具链与闭环如下：

![编辑器工具链与验证闭环](docs/images/05-editor-toolchain.png)

标准闭环是 **安装 → 探针 → 诊断 → 验证 → 出图/报告**：

1. **安装**：`InteractionSetup` / `SeatSetup` / `CeilingLampSetup` / `AutoDoorInstaller` / `DoorPartsBinder` / `OutdoorScenerySetup` 写组件引用并 `EditorSceneManager.SaveScene()`；
2. **探针**：`SceneGeomProbe` / `WallProbe` / `OutsideProbe` 读取场景真实几何，输出位置、包围盒、遮挡关系；
3. **诊断**：`InteractDiag` / `SwitchInspect` / `ProjectHealthCheck` 打印运行时状态与材质参数；
4. **验证**：`InteractionVerify` / `SeatVerify` / `DoorVerify` / `KeyMapVerify` / `PlaySmokeTest` 模拟站位与按键，断言结果；
5. **出图**：`InteractionShots` / `LampShots` / `SceneryShots` 截实拍，`PerfProbe` 出性能数据，结果落到项目根目录的 `*.txt` 报告。

> 这套闭环的价值：每一步都有可复查的文本证据，出现回归时不需要靠「我记得当时是好的」来判断。

---

## 九、关键数据与验证结果

![关键数据与验证结果总览](docs/images/06-data-overview.png)

| 项目 | 结果 | 来源 |
| --- | --- | --- |
| Assets 总体积 / 机房.FBX | 790 MB / 737 MB（94%） | `优化报告.md` |
| 模型网格数 / 顶点 / 三角面 | 2152 个 / 2273 万 / 1341 万面 | `优化报告.md` |
| 重复副本面数占比 | 94.5% | `优化报告.md` |
| Renderer 总数 / 唯一网格 / 唯一材质 | 2203 / 84 / 47 | `项目体检报告.txt` |
| 已烘焙光照贴图 | 0（纯实时光照） | `项目体检报告.txt` |
| 电脑 / 座椅 / 灯盘 / 射灯 | 49 台 / 49 把 / 19 块 / 5 盏 | `交互系统搭建结果.txt`、`灯盘安装结果.txt` |
| 开关灯亮度变化（全景 / 天花板） | 0.347→0.129（-62.8%）/ 0.311→0.130（-58%） | `灯光对比结果.txt` |
| 座椅眼高变化 | 1.166 → 0.954 m（下沉 0.212 m），起身误差 0 | `座椅验证结果.txt` |
| 灯光开关位置 | (-6.336, 1.3, 3.31)，背后 0.5 m 内有墙 | `交互验证结果.txt` |
| 门跟随部件 | 门 A 8/8 移动（最大 1.431 m）；门 B 初次 0/7（已修） | `冒烟测试结果.txt`、`门部件绑定结果.txt` |
| 室外树 / 雾 | 88 棵 / 指数平方 density 0.012 | `室外景观结果.txt` |
| Mesh 去重 | 唯一几何组 85、合并 58 组、重定向 2069、Instancing 35/48 | `Mesh去重结果.txt` |
| 性能归因（预热后） | 全开 120.1 / 关树 120.2 / 关雾 119.8 / 都关 120.9 FPS | `性能归因结果.txt` |
| 冒烟测试帧率（未充分预热） | 38.7 FPS | `冒烟测试结果.txt` |
| 按键配置 | 电脑 Q=49、座椅 E=49、兜底 E、range 2.8 | `按键改配结果.txt` |

---

## 十、代码文件清单与总行数

统计口径：按 `Assets/` 下全部 `.cs` 源文件逐行计数（含空行与注释，UTF-8 / UTF-8 BOM 均按物理行统计），另有文档绘图用 Python 脚本单列。

### 10.1 运行时脚本（自写，9 个 / 1179 行）

| 文件 | 行数 | 职责 |
| --- | --- | --- |
| `Assets/Scripts/Interaction/Interactable.cs` | 35 | 交互抽象基类 |
| `Assets/Scripts/Interaction/InteractablePC.cs` | 70 | 电脑开关机（49 台，Q 键） |
| `Assets/Scripts/Interaction/InteractableLightSwitch.cs` | 87 | 灯光开关四路联动 |
| `Assets/Scripts/Interaction/InteractableSeat.cs` | 190 | 坐下 / 起身状态机 |
| `Assets/Scripts/Interaction/CeilingLamp.cs` | 51 | 灯盘亮灭材质切换 |
| `Assets/Scripts/Interaction/PlayerInteractor.cs` | 94 | 射线判定、提示与按键分发 |
| `Assets/Scripts/Interaction/AutoDoorSystem.cs` | 323 | 自动门 + 跟随部件同步 |
| `Assets/Scripts/UI/InteractionPromptUI.cs` | 64 | 提示条淡入淡出 |
| `Assets/Scripts/UI/StartupTransition.cs` | 265 | 开场过渡与全局门控 |
| **小计** | **1179** | — |

### 10.2 编辑器脚本（自写，28 个 / 6089 行，不进包体）

| 文件 | 行数 | 分组 |
| --- | --- | --- |
| `Assets/Editor/InteractionSetup.cs` | 459 | 安装类 |
| `Assets/Editor/SeatSetup.cs` | 158 | 安装类 |
| `Assets/Editor/CeilingLampSetup.cs` | 180 | 安装类 |
| `Assets/Editor/AutoDoorInstaller.cs` | 208 | 安装类 |
| `Assets/Editor/DoorPartsBinder.cs` | 96 | 安装类 |
| `Assets/Editor/OutdoorScenerySetup.cs` | 518 | 安装类 |
| `Assets/Editor/StartupCanvasBuilder.cs` | 424 | 安装类 |
| `Assets/Editor/StartupCanvasAutoSetup.cs` | 54 | 安装类 |
| `Assets/Editor/MeshDedupAnalysis.cs` | 162 | 生成与优化类 |
| `Assets/Editor/MeshDedupApply.cs` | 148 | 生成与优化类 |
| `Assets/Editor/ProjectPolish.cs` | 196 | 生成与优化类 |
| `Assets/Editor/ProjectOptimizer.cs` | 286 | 生成与优化类 |
| `Assets/Editor/LightProbe.cs` | 143 | 生成与优化类 |
| `Assets/Editor/SceneGeomProbe.cs` | 171 | 探针与诊断类 |
| `Assets/Editor/WallProbe.cs` | 115 | 探针与诊断类 |
| `Assets/Editor/OutsideProbe.cs` | 160 | 探针与诊断类 |
| `Assets/Editor/InteractDiag.cs` | 282 | 探针与诊断类 |
| `Assets/Editor/SwitchInspect.cs` | 125 | 探针与诊断类 |
| `Assets/Editor/ProjectHealthCheck.cs` | 187 | 探针与诊断类 |
| `Assets/Editor/InteractionVerify.cs` | 335 | 验证与出图类 |
| `Assets/Editor/SeatVerify.cs` | 253 | 验证与出图类 |
| `Assets/Editor/DoorVerify.cs` | 96 | 验证与出图类 |
| `Assets/Editor/KeyMapVerify.cs` | 266 | 验证与出图类 |
| `Assets/Editor/PlaySmokeTest.cs` | 190 | 验证与出图类 |
| `Assets/Editor/PerfProbe.cs` | 153 | 验证与出图类 |
| `Assets/Editor/InteractionShots.cs` | 306 | 验证与出图类 |
| `Assets/Editor/LampShots.cs` | 246 | 验证与出图类 |
| `Assets/Editor/SceneryShots.cs` | 172 | 验证与出图类 |
| **小计** | **6089** | — |

### 10.3 第三方与模板脚本（3 个 / 1000 行）

| 文件 | 行数 | 说明 |
| --- | --- | --- |
| `Assets/ModularFirstPersonController/FirstPersonController/FirstPersonController.cs` | 742 | 第三方第一人称控制器，见第十一章 |
| `Assets/TutorialInfo/Scripts/Editor/ReadmeEditor.cs` | 242 | Unity 工程模板自带教程脚本 |
| `Assets/TutorialInfo/Scripts/Readme.cs` | 16 | Unity 工程模板自带教程脚本 |
| **小计** | **1000** | — |

### 10.4 汇总

| 范围 | 文件数 | 行数 |
| --- | --- | --- |
| 运行时脚本（自写） | 9 | 1179 |
| 编辑器脚本（自写） | 28 | 6089 |
| **自写 C# 小计** | **37** | **7268** |
| 第三方控制器 | 1 | 742 |
| Unity 模板脚本 | 2 | 258 |
| **全工程 .cs 合计** | **40** | **8268** |
| 文档绘图脚本 `docs/draw_diagrams.py` | 1 | 606 |
| **含 Python 脚本合计** | **41** | **8874** |

> 行数会随维护变化，重新统计可执行：`python -c "import os,io; print(sum(sum(1 for _ in io.open(os.path.join(d,f),encoding='utf-8-sig',errors='ignore')) for d,_,fs in os.walk('Assets') for f in fs if f.endswith('.cs')))"`。

---

## 十一、外部依赖逐项核查

结论先说：**Package Manager 里没有 Asset Store 包，工程运行时与编辑器期都不依赖任何外部服务，可完全离线开发与运行**。但存在一处需人工核实的第三方代码资产和若干外部美术素材，逐项如下。

### 11.1 Unity Package Manager 包（全部 `com.unity.*`，非 Asset Store 包）

`Packages/manifest.json` 共 41 项依赖：10 个功能包 + 31 个 `com.unity.modules.*` 引擎内置模块，全部来自 Unity 官方 registry 或引擎自带。

| 类别 | 包名与版本 | 用途 | 是否商店包 |
| --- | --- | --- | --- |
| 渲染 | `com.unity.render-pipelines.universal` 14.0.12 | URP 管线、Lit 着色器 | 否，官方包 |
| UI | `com.unity.ugui` 1.0.0、`com.unity.textmeshpro` 3.0.7 | 提示条与文字 | 否，官方包 |
| 过场 | `com.unity.timeline` 1.7.7 | 开场过渡 | 否，官方包 |
| 模板自带（未直接使用） | `com.unity.visualscripting` 1.9.4、`com.unity.test-framework` 1.1.33、`com.unity.collab-proxy` 2.12.4 | 工程模板默认项 | 否，官方包 |
| 编辑器集成 | `com.unity.ide.rider` 3.0.36、`com.unity.ide.visualstudio` 2.0.22、`com.unity.ide.vscode` 1.2.5 | IDE 支持 | 否，官方包 |
| 引擎内置模块 | `com.unity.modules.*`（physics / terrain / ui / xr 等 31 项） | 引擎功能开关 | 否，引擎自带 |

### 11.2 第三方代码资产（唯一一处，需核实授权）

| 项 | 路径 | 内容 | 判定 |
| --- | --- | --- | --- |
| 第一人称控制器 | `Assets/ModularFirstPersonController/` | `FirstPersonController.cs`（742 行）、`.prefab`、`CircleReticle.png`、`How To Use.pdf`、`DemoScene/`（含 Gridbox Prototype Materials 与 Demo 场景） | **以源码形式直接放在 `Assets/` 下的第三方包**，形态符合 Asset Store / 社区导入包，不通过 Package Manager 管理；工程内**未附 LICENSE、`package.json` 或购买凭证**，无法从文件确认来源与授权，**对外分发或商用前需人工核实** |

> 这是全工程唯一的第三方代码依赖。`InteractableSeat.cs` 在编译期直接引用 `FirstPersonController` 类型，替换控制器时需同步改造（见第十二章）。

### 11.3 外部美术与音频素材（本地文件，非服务）

| 项 | 路径 | 说明 |
| --- | --- | --- |
| 主教室模型 | `Assets/机房.FBX` | 737 MB，对象名均为 `Obj3d66-*`，来自外部模型库（3d66），**非 Asset Store 包**；另有 `Assets/Art/Meshes` 等衍生网格 |
| 天空盒 / 贴图 / 音视频 | `Assets/教室贴图 1/`、`Assets/Art/` | `SunsetSkybox_Equirect_1024x512.png`、地毯 Albedo/Normal、`.mp4` 视频、钢琴曲 `.mp3` |
| TMP Essential Resources | `Assets/TextMesh Pro/` | 随官方 `com.unity.textmeshpro` 导入；`LiberationSans - OFL.txt`（SIL OFL）与 `EmojiOne Attribution.txt` 已随包保留 |
| 模板教程资源 | `Assets/TutorialInfo/`、`Assets/Readme.asset` | Unity 工程模板自带，非第三方 |
| URP 模板设置 | `Assets/Settings/` | URP-Balanced / HighFidelity / Performant 三档配置，模板自带 |

### 11.4 外部服务与联网核查

| 检查项 | 结果 |
| --- | --- |
| Unity 在线服务 | `ProjectSettings/UnityConnectSettings.asset` 中 Analytics / Ads / IAP / Cloud 全部 `m_Enabled: 0` |
| 网络请求代码 | 全工程 `.cs` 无 `http(s)` URL、无 `UnityWebRequest` / `Socket` / `HttpClient` / 第三方 SDK |
| 唯一的 `System.Net` | 仅出现在 `FirstPersonController.cs` 的 `#if UNITY_EDITOR` 条件编译 `using` 中，**无实际网络调用** |
| 后端 / 云 / 账号 / 遥测 / 鉴权 | 无，无 API Key、无 Token |
| Editor 工具 | 只读写项目本地 `*.txt` 报告与 `*.png` 截图，不上传任何数据 |
| **结论** | **运行时与编辑器期均可完全离线**，无需联网鉴权 |

### 11.5 仅文档侧的本地依赖（不进 Unity 工程、不进包体）

| 项 | 用途 |
| --- | --- |
| Python 3 + Pillow | 仅用于运行 `docs/draw_diagrams.py` 生成 6 张技术展示图 |
| `C:/Windows/Fonts/msyh.ttc`（微软雅黑） | 仅绘图中文字体，不随工程发布 |

---

## 十二、可复用模块清单

按「搬运成本」分三档。判断依据是对 `Assets/Scripts/` 全部 `using` 与类型引用的静态核查：只依赖 `UnityEngine` / `UnityEngine.UI`、不引用工程内自定义类型的脚本可零改动搬走；引用本项目其他类的需连依赖一起搬；引用第三方控制器或硬编码场景对象名的必须先改造。

### 12.1 A 级 · 零改动可直接搬（5 个文件 / 738 行）

| 文件 | 行数 | 编译期依赖 | 搬过去能做什么 |
| --- | --- | --- | --- |
| `Assets/Scripts/Interaction/Interactable.cs` | 35 | 仅 `UnityEngine` | 交互抽象基类：`prompt / range / GetKey() / CanInteract() / Interact()` |
| `Assets/Scripts/Interaction/CeilingLamp.cs` | 51 | 仅 `UnityEngine` | 任意「发光面板」亮灭材质切换 |
| `Assets/Scripts/Interaction/AutoDoorSystem.cs` | 323 | `UnityEngine` + `System.Collections.Generic` | 距离触发自动门 + 多部件绕铰链同步 |
| `Assets/Scripts/UI/InteractionPromptUI.cs` | 64 | `UnityEngine` + `UnityEngine.UI` | 提示条淡入淡出与 `[Q]/[E]` 文案拼装 |
| `Assets/Scripts/UI/StartupTransition.cs` | 265 | `UnityEngine` + `UnityEngine.UI` | 开场过渡与 `IsTransitioning` 全局交互门控 |
| **合计** | **738** | — | — |

> `AutoDoorSystem` 与 `StartupTransition` 通过 `GetType().Name == "FirstPersonController"` 之类的**字符串 / 反射**方式找玩家，因此目标工程换控制器也能编译通过，只需在 Inspector 里重新指定引用。

### 12.2 B 级 · 带上依赖一起搬（3 个运行时脚本 / 251 行）

| 文件 | 行数 | 需一并搬运 / 改造 |
| --- | --- | --- |
| `Assets/Scripts/Interaction/InteractablePC.cs` | 70 | `Interactable.cs` |
| `Assets/Scripts/Interaction/InteractableLightSwitch.cs` | 87 | `Interactable.cs` + `CeilingLamp.cs` |
| `Assets/Scripts/Interaction/PlayerInteractor.cs` | 94 | `Interactable.cs` + `InteractionPromptUI.cs` + `StartupTransition.cs`；**并需删掉 `InteractableSeat.Current` 短路分支**（或连座椅一起搬） |
| **合计** | **251** | 连同 A 级，整套交互框架共 **8 个文件 / 989 行** |

通用性较高的 Editor 工具（只做遍历统计或性能测量，不含本场景对象名，改资源路径即可复用）：

`Assets/Editor/MeshDedupAnalysis.cs`、`MeshDedupApply.cs`、`PerfProbe.cs`、`ProjectHealthCheck.cs`、`SceneGeomProbe.cs`。

### 12.3 C 级 · 需改造或仅作模板（不建议直接复制）

| 文件 / 组 | 行数 | 原因 |
| --- | --- | --- |
| `Assets/Scripts/Interaction/InteractableSeat.cs` | 190 | 编译期强依赖 `FirstPersonController` 具体类型与 `joint / playerCanMove` 等字段；需先抽象成接口或适配器 |
| `Assets/ModularFirstPersonController/FirstPersonController.cs` | 742 | 第三方代码，授权文件缺失（见 11.2），不建议随项目复制 |
| 场景强绑定 Editor 脚本（11 个） | — | 硬编码 `Obj3d66-*`、`机房`、`电脑屏幕_Albedo`、`座位_*`、`灯光开关` 及墙面采样坐标，只能当模板改 |

场景强绑定的 11 个 Editor 脚本逐项清单：

`CeilingLampSetup.cs`、`InteractDiag.cs`、`InteractionSetup.cs`、`InteractionShots.cs`、`InteractionVerify.cs`、`KeyMapVerify.cs`、`LightProbe.cs`、`SeatSetup.cs`、`StartupCanvasBuilder.cs`、`SwitchInspect.cs`、`WallProbe.cs`。

### 12.4 最小搬运步骤

1. 先复制 A 级 5 个文件，确认目标工程能编译（它们不引用本工程任何类型）；
2. 需要完整交互框架时再复制 B 级 3 个文件，并按上表删除座椅短路分支；
3. 需要坐姿功能时，先把 `InteractableSeat` 里的 `FirstPersonController` 字段替换为目标工程的控制器接口；
4. Editor 脚本一律**先读后改**：把 `Assets/Editor/` 下的场景名、材质名、坐标常量替换成目标工程的值，再跑一遍验证脚本输出 `*.txt` 报告。

---

## 十三、快速上手

### 13.1 环境要求

- Unity **2022.3.62f3c1**（`ProjectSettings/ProjectVersion.txt`），建议 2022.3.x 同系列
- 项目已配置 URP 14.0.12、TextMeshPro 3.0.7、Timeline 1.7.7、uGUI 1.0.0（`Packages/manifest.json`）
- Windows 环境；如需重新生成技术图，另需 Python + Pillow

### 13.2 打开与运行

1. 用 Unity Hub 打开项目根目录（本 `README.md` 所在目录）；
2. 打开主场景 `Assets/Scenes/SampleScene.unity`；
3. 点击 Play，等待开场过渡结束（光标自动锁定、隐藏），此时交互才生效；
4. WASD 移动、鼠标转头，靠近物体看屏幕下方的提示条。

> 不要直接双击别的场景；所有组件引用、门参数、按键配置都保存在 `SampleScene.unity` 里。

### 13.3 按键速查

| 场景 | 按键 | 效果 |
| --- | --- | --- |
| 准星对准电脑屏幕 | `Q` | 开机 / 关机（提示 `[Q] 开机`） |
| 准星对准门边墙面开关 | `E` | 开灯 / 关灯（提示 `[E] 关灯`） |
| 准星对准座椅 | `E` | 坐下（提示 `[E] 坐下`） |
| 已坐下（任意视线） | `E` | 起身（提示 `[E] 起身`） |
| 靠近 / 离开教室门 | 无需按键 | 自动开门 / 延时关闭 |
| 坐姿状态 | 鼠标 | 只能转头，身体不动 |

交互距离参考：全局射线 `range = 2.8`，电脑 `2.6`，开关 `2.2`，座椅 `2.2`。

### 13.4 目录结构

```
项目根目录/
├── README.md                    本文档
├── Assets/
│   ├── Scenes/SampleScene.unity 主场景（全部组件引用在此）
│   ├── Scripts/
│   │   ├── Interaction/        运行时交互（6 个脚本）
│   │   └── UI/                 提示 UI 与开场过渡（2 个脚本）
│   ├── Editor/                 编辑器工具链（28 个脚本，不进包体）
│   ├── ModularFirstPersonController/  第一人称控制器
│   └── 机房.FBX 等模型与材质
├── Packages/manifest.json      依赖清单
├── ProjectSettings/ProjectVersion.txt
├── docs/
│   ├── images/                 技术展示图 + 实拍截图
│   └── draw_diagrams.py        技术展示图生成脚本（可重跑）
├── 优化报告.md
└── *.txt                       各阶段实测报告（诊断/验证/归因结果）
```

### 13.5 常用菜单

安装、探针、验证、出图类脚本都注册在 `工具/场景/...` 菜单下（见 5.5 与第八章清单）。修改批量安装逻辑后，按「安装 → 验证 → 看根目录 `*.txt` 报告」的顺序回归，不要只靠 Play 模式肉眼确认。

---

## 十四、维护与扩展

### 14.1 新增一个交互物的标准步骤

1. 新建类继承 `Interactable`，实现 `Interact(GameObject interactor)`；如需专属按键，覆写 `GetKey()`；如需动态文案，覆写 `GetPrompt()`；
2. 在 `Interact(E)` 里只做状态切换，不要创建材质、不要加碰撞体（交给 Editor 工具）；
3. 写对应的 `Assets/Editor/*Setup.cs`，用 `Undo.AddComponent` 批量挂载并 `EditorSceneManager.SaveScene()`；
4. 写对应的 `*Verify.cs` 模拟站位 + 按键，把断言结果输出到根目录 `*.txt`；
5. 跑一遍 `KeyMapVerify.cs`，确认提示文案与实际按键一致。

### 14.2 必须遵守的约定

- **运行时不创建资源**：材质、碰撞体、灯盘/门扇绑定一律在编辑器期写入场景；运行时只用 `sharedMaterial(s)` 切换。
- **按键只有一个来源**：改按键只改 `Interactable.GetKey()`，绝不改 `PlayerInteractor` 的兜底键。
- **状态类交互用「保存 → 修改 → 还原」**：参考座椅，退出状态必须逐项还原控制器开关与 `Rigidbody`。
- **门是一组物体**：新增门部件记得进 `Door.extraParts`。
- **性能结论要预热后再测**：对比 FPS 前先着色器预热，避免把编译开销当成场景开销。
- **改动后留文本证据**：批量操作后跑一次验证脚本并保留 `*.txt`，便于后续 diff 回归。

### 14.3 重新生成技术展示图

```bash
python -m pip install Pillow
python docs/draw_diagrams.py
```

脚本用 `C:/Windows/Fonts/msyh.ttc`（微软雅黑）绘制中文，2 倍超采样后 LANCZOS 缩放，输出到 `docs/images/01-*.png` ~ `06-*.png`。修改图内容后记得同步更新本文中的引用路径。

---

## 十五、产物清单

**文档**

- `README.md` — 本文档（标准 Markdown）

**技术展示图（PNG，随文档同步输出）**

| 文件 | 内容 |
| --- | --- |
| `docs/images/01-system-architecture.png` | 系统架构总览（五层分层） |
| `docs/images/02-interaction-flow.png` | 交互判定主流程 |
| `docs/images/03-module-map.png` | 核心模块与类关系 |
| `docs/images/04-light-sequence.png` | 灯光开关联动时序 |
| `docs/images/05-editor-toolchain.png` | 编辑器工具链与验证闭环 |
| `docs/images/06-data-overview.png` | 关键数据与验证结果总览 |

**实拍截图（PNG）**

`docs/images/effect-light-on.png`、`effect-light-off.png`、`effect-switch.png`、`effect-pc.png`、`effect-seat.png`、`effect-sit.png`、`effect-outdoor.png`、`effect-door.png`、`effect-lamp-on.png`、`effect-lamp-off.png`

**可复现脚本与数据**

- `docs/draw_diagrams.py` — 技术展示图生成脚本
- 项目根目录 `*.txt` / `优化报告.md` — 各阶段实测报告，是本文所有数字的来源

---

> 文档中全部数值均来自项目内实测报告，未使用估算值；如需复核，按「来源」列打开对应 `*.txt` 即可。

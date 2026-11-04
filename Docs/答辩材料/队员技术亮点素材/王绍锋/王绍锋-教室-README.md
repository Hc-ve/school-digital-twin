# 教室场景控制台（Classroom UI）

> **一句话定位：一个用编辑器脚本一键加装到既有教室场景上的 Unity 交互式控制台，把「空调 / 视角 / 投影仪」三类设备操作与「监控滤镜 / 第一人称漫游 / 粒子气流」等视听效果，收拢到屏幕左侧一块常驻面板里。**

| 项目信息 | 值 |
|---|---|
| 项目路径 | `D:\Uproject\UI` |
| 主场景 | `Assets/Classroom.unity` |
| Unity 版本 | **2022.3.62f3c1 (1623fc0bbb97)** |
| 渲染管线 | Universal Render Pipeline 14.0.12 |
| 主要语言 | C#（运行时 + 编辑器扩展）、Python 3.14（离线资源与校验） |
| 自研代码规模 | **2132 行**（C# 1465 + Python 667） |
| 技术展示图 | `Docs/images/*.png`（5 张，随文档相对路径引用） |
| 运行时联网依赖 | **无** |
| 文档更新日期 | 2026-11-04 |

---

## 目录

1. [项目背景、定位与核心问题](#1-项目背景定位与核心问题)
2. [功能汇总表](#2-功能汇总表)
3. [技术栈、Unity 版本与依赖清单](#3-技术栈unity-版本与依赖清单)
4. [三个最有亮点的功能](#4-三个最有亮点的功能)
5. [技术实现方案](#5-技术实现方案)
6. [代码文件清单与总行数](#6-代码文件清单与总行数)
7. [自包含可复用模块](#7-自包含可复用模块可直接拆给别的工程)
8. [踩坑日志](#8-踩坑日志)
9. [快速上手与维护](#9-快速上手与维护)
10. [技术展示图与配图索引](#10-技术展示图与配图索引)
11. [验证状态与已知限制](#11-验证状态与已知限制)

---

## 1. 项目背景、定位与核心问题

### 1.1 背景与业务场景

工程里已有一个用 FBX 模型搭好的教室场景（`/Assets/教室.fbx`、`class1.fbx`、`PC.fbx`），包含桌椅、电脑、黑板、窗帘、立式空调、吊装投影仪与幕布。场景本身是**静态的**——能看，不能操作。

本项目要把它变成一个**可演示、可交互的教室设备控制台**，典型使用场景包括：

- **课堂演示 / 课程作业答辩**：一块面板直接切换全景、监控、第一人称三种观察方式，讲解空间布局与设备位置。
- **虚拟仿真 / 数字孪生原型**：把「开空调」「开投影」「调监控」这类设备操作抽象成按钮事件，作为后续接入真实设备或后端的前置交互层。
- **Unity 交互开发教学样例**：展示 UGUI、VideoPlayer + RenderTexture、ParticleSystem、状态机、编辑器批处理、程序化资源生成等常见技术点如何组合在一个小工程里。

### 1.2 定位

- 它是**「加装层」而不是「重做场景」**：不修改任何 FBX 模型、不重建材质、不替换原 Main Camera，所有新增对象由一个根节点 `教室控制台` 统一管理。
- 它是**可重复生成的**：菜单点一次或跑一次批处理命令，就把整套控制台重建出来，换一套教室模型也能自动适配摆位。
- 它是**离线自足的**：音频、贴图、中文字体、视频全部本地生成或本地提供，不依赖网络、账号或云服务。

### 1.3 解决的核心问题

| 痛点 | 本项目做法 |
|---|---|
| 静态场景无法交互演示 | 左侧常驻 UGUI 面板，4 个按钮 + 状态栏，配 6 个键盘快捷键 |
| 观察角度单一，讲不清空间关系 | 三视角状态机，统一由 `ApplyView()` 收口，避免状态漂移 |
| 设备开关只有「逻辑」没有「反馈」 | 空调联动声音 + 粒子；投影仪联动画面 + 灯光 + 伴音；视角联动滤镜 + 光标策略 |
| 监控视角「不像监控」 | 五层 CCTV 滤镜（色调 / 暗角 / 扫描线 / 噪点 / 角标）+ 动态 UV、REC 闪烁、实时时间戳 |
| 手搭场景对象容易漏引用、换模型要改一堆坐标 | 编辑器脚本幂等构建，按教室包围盒推导所有摆位，并自动绑定 18+ 个引用字段 |
| 缺素材、版权与体积顾虑 | numpy 合成无缝空调音、程序生成 4 张滤镜贴图，全部可离线复现 |
| 第一人称与 UI 抢鼠标 | 只有「按住右键」才锁光标，左键始终能点面板 |
| 换相机后声音中断 / 多监听器告警 | 全场只保留一个 `AudioListener`，挂在控制台根节点上 |
| 编辑模式下黑屏、批处理拍不到 UI | 幕布默认白色基色；Overlay 画布临时转 World Space 用正交相机拍预览 |

### 1.4 边界（本项目不做什么）

- 不实现存档、多人联机、后端接口、账号系统。
- 不接入真实 IoT 设备，按钮目前只驱动场景内表现。
- 不使用新 Input System、不使用 UI Toolkit、不依赖 Addressables。
- 未做移动端触控适配，输入面向键鼠。

---

## 2. 功能汇总表

### 2.1 交互功能总表

> 三列结构：**功能模块 / 功能点 / 交互说明**，覆盖当前已实现的全部交互。

| 功能模块 | 功能点 | 交互说明 |
|---|---|---|
| **常驻 UI 面板** | 左侧固定面板 | 屏幕左侧垂直居中，`ScreenSpaceOverlay`、sortingOrder=100，锚点贴左，1920×1080 参考分辨率按宽高 0.5 自适应 |
| | 面板视觉 | 深色半透明底 + 蓝色强调条 + 圆角切片 Sprite，标题「教室控制台」 |
| | 4 个功能按钮 | 空调声音 / 摄像头视角 / 第一人称视角 / 投影仪；`Button` + `LayoutElement`，VerticalLayoutGroup 自动排布 |
| | 按钮状态回写 | 激活态蓝色 `activeColor`，未激活深灰 `idleColor`；文字随状态变化（如「空调声音：开」） |
| | 状态栏 | 显示当前视角名 + 上下文操作提示（第一人称显示 WASD 提示，其余显示快捷键提示） |
| | 事件系统 | 自动创建 `EventSystem` + `StandaloneInputModule`；所有滤镜层 `raycastTarget=false`，不挡点击 |
| **空调** | 声音开关 | 点击「空调声音」按钮或按 `Q`；0.7s 音量淡入淡出，无起播延迟 |
| | 无缝循环音频 | `AC_Hum.wav` 常驻 `loop` 播放，靠 `volume` 控制，不反复 `Play/Stop` |
| | 气流粒子联动 | 开空调同时 `acAir.Play()`；关闭时 `StopEmitting` 让残留粒子自然飘散，不硬清屏 |
| | 出风口自动定位 | 构建期查找 `AirConditioner` / `AC_Body` / `AC_Vent_0` 节点，按机身包围盒推导顶部格栅位置与朝房间内侧的吹风方向 |
| **视角系统** | 全景视角 | 默认态；按 `1` 随时返回 |
| | 摄像头监控视角 | 点击按钮或按 `2`；启用监控相机 + 打开滤镜 Canvas；再点一次返回全景 |
| | 第一人称视角 | 点击按钮或按 `3`；启用角色与 FP 相机；再点一次返回全景 |
| | 跨态直达 | 任意视角可直接按 `1/2/3` 跳到目标态，无需先回全景 |
| | 快捷键返回 | `Esc` 从任意非全景态回到全景 |
| | 状态统一收口 | `ApplyView()` 一次性设置相机 enabled、滤镜 SetActive、角色激活、光标、UI 文字 |
| **第一人称漫游** | 移动 | `WASD` / 方向键，CharacterController 驱动，含重力与贴地 |
| | 加速 | 按住 `Left Shift`，速度 ×1.9 |
| | 转视角 | **按住鼠标右键**才锁定光标并转动视角；松开即恢复光标，左键始终可点 UI |
| | 俯仰限制 | ±80° 夹取，避免翻转 |
| **监控滤镜** | 五层叠加 | 色调（灰绿降饱和）、暗角、扫描线、噪点、角标；Canvas sortingOrder=50，低于控制面板 |
| | 噪点动画 | 14 FPS 随机刷新 `uvRect`，模拟胶片颗粒跳动 |
| | 扫描线滚动 | UV 按 0.35/s 向下滚动 |
| | REC 指示灯 | 红点按 1.4Hz `PingPong` 闪烁 |
| | 角标信息 | 左上「REC」、右上「CAM 01 · 1080P」、右下实时时间戳（每秒刷新） |
| | 显示条件 | 滤镜 Canvas 默认 `m_IsActive=0`，仅摄像头视角下激活 |
| **投影仪** | 播放 / 停止 | 点击「投影仪」按钮或按 `E`；`VideoPlayer.Play/Stop`，循环播放 |
| | 画面投射 | 视频渲染到 `ProjectorRT`（1280×720 RenderTexture），开机切到幕布材质 `_BaseMap` |
| | 待机白幕 | 关机切回 1×1 白色贴图，不是黑方块 |
| | 灯光与伴音 | 开机点亮 SpotLight，视频音轨经 `AudioSource` 输出；关机关灯并 mute |
| **编辑器工具** | 一键生成 / 重建 | 菜单「工具 → 教室控制台 → 生成或重建 UI」，幂等，先删旧根节点再生成 |
| | 包围盒自适应 | 遍历场景 Renderer 计算教室尺寸（约 8.1×3.5×9.3 m），推导相机、幕布、角色、出风口位置 |
| | 引用自动绑定 | 构建时直接给 `ClassroomSceneController` 的公开字段赋值，避免手工拖拽漏项 |
| | 批处理支持 | `-batchmode -executeMethod ...BuildAndSave`，无需人工点菜单 |
| | 预览出图 | 自动渲染 6 张视角 / UI / 粒子预览图 |
| **离线工具链** | 程序化音频 | numpy 频域构造噪声 + IFFT + 50/100/150Hz 谐波，生成 12s 无缝 `AC_Hum.wav` |
| | 程序化贴图 | Pillow 生成 grain / scanlines / vignette / particle_soft 四张贴图 |
| | 滤镜效果合成 | 把带 alpha 的滤镜层叠到监控原始画面，输出最终效果图 |
| | 场景校验 | 解码 YAML 中文转义、检查引用绑定、检查对象默认激活状态 |
| **外部集成（开发期）** | Blender MCP | stdio 启动 `mcp-for-blender`，TCP localhost:9878 |
| | Unity MCP | 嵌入式包 + HTTP `http://localhost:8080/mcp` |

### 2.2 快捷键速查

| 按键 | 作用 |
|---|---|
| `1` | 全景视角 |
| `2` | 摄像头监控视角 |
| `3` | 第一人称视角 |
| `Esc` | 从非全景视角返回全景 |
| `Q` | 空调声音 / 气流开关 |
| `E` | 投影仪开关 |
| `W A S D` | 第一人称移动 |
| `Left Shift` | 第一人称加速 |
| 鼠标右键（按住） | 第一人称转视角 |

### 2.3 启动默认状态

全景视角 · 空调关（音量 0）· 气流粒子对象未激活 · 投影仪关（白幕、灯灭）· 监控滤镜 Canvas 未激活 · 第一人称角色未激活。

---

## 3. 技术栈、Unity 版本与依赖清单

### 3.1 技术栈

| 类别 | 选型 | 版本 / 说明 | 用途 |
|---|---|---|---|
| 引擎 | Unity Editor | **2022.3.62f3c1 (1623fc0bbb97)** | 项目主体 |
| 渲染 | Universal Render Pipeline | 14.0.12 | 场景渲染、Unlit 幕布材质 |
| UI | uGUI (`com.unity.ugui`) | 1.0.0 | Canvas / Image / RawImage / Text / Button / LayoutGroup |
| 视频 | `UnityEngine.Video` + VideoPlayer | Unity 内置 Video 模块 | MP4 解码 → RenderTexture |
| 音频 | `AudioSource` / `AudioListener` | Unity 内置 | 空调环境音、视频伴音 |
| 粒子 | Shuriken `ParticleSystem` | Unity 内置 | Stretch 拉伸流线气流 |
| 输入 | 旧版 Input Manager | `Input.GetKey` / `GetAxisRaw` | 键鼠输入，未装新 Input System |
| 物理 | `CharacterController` | Unity 内置 | 第一人称移动与重力 |
| 编辑器扩展 | `UnityEditor` / `EditorSceneManager` | Unity 内置 | 一键构建、批处理、预览渲染 |
| 字体 | SimHei（黑体） | `C:\Windows\Fonts\simhei.ttf` | UGUI 中文显示，构建时复制到 `Assets/Fonts/` |
| 脚本语言 | C# | Unity 2022.3 对应 C# 9 | 运行时 + 编辑器 |
| 离线脚本 | Python | 3.14.4 | 资源生成、校验、绘图 |
| Python 库 | numpy / Pillow / matplotlib | 2.5.3 / 12.3.0 / 3.11.2 | 音频合成、贴图生成、技术展示图 |
| 包管理 | `uv` / `uvx` | 0.12.18 | 运行 Python 工具与 MCP 客户端 |
| 版本资源 | FBX / MP4 / WAV / PNG / TTF | 本地文件 | 模型、视频、音频、贴图、字体 |

> 精确的 Unity 版本来自 `ProjectSettings/ProjectVersion.txt`。

### 3.2 Unity 包依赖（全部来自 Unity 官方 Registry）

`Packages/manifest.json` 中关键项：`com.unity.render-pipelines.universal` 14.0.12、`com.unity.ugui` 1.0.0、`com.unity.textmeshpro` 3.0.7、`com.unity.modules.video` 1.0.0、`com.unity.modules.audio`、`com.unity.modules.particlesystem`、`com.unity.modules.physics`、`com.unity.modules.ui`，其余为 Unity 模板默认模块（ads / analytics / purchasing / collab / IDE 集成 / test-framework 等）。

### 3.3 是否依赖 Asset Store 包 / 外部服务（逐项列）

**结论：运行时零外部依赖；工程里存在第三方资源痕迹但均未被本项目使用。**

| # | 项 | 类型 | 是否运行时依赖 | 说明 |
|---|---|---|---|---|
| 1 | `Assets/ModularFirstPersonController/` | **Asset Store 风格第三方包**（含 `Demo.unity` 与 Gridbox Prototype Materials） | **否** | 工程早期导入的现成第一人称控制器（741 行）。本项目**没有用它**，改用自写的 `FirstPersonWalker.cs`（91 行），以避免与左侧 UI 抢光标、减少依赖。可安全删除 |
| 2 | `Assets/TutorialInfo/` | Unity 3D 模板自带（`Readme.cs` / `ReadmeEditor.cs`） | 否 | 新建工程时的模板文件，与功能无关 |
| 3 | `Assets/MCP/Buildings/`、`Assets/Frefab/` | MCP 生成的材质与预制体资源 | 否 | 非代码，非商店包 |
| 4 | `Packages/manifest.json` 全部条目 | Unity 官方 Registry 包 | 否（引擎内置能力） | **无任何 Asset Store 包**，无 git URL 依赖 |
| 5 | Unity Ads / Analytics / Purchasing / Collab | 官方服务包 | 否 | 仅存在于 manifest，场景中未初始化、未调用、无需账号或联网 |
| 6 | `Packages/com.coplaydev.unity-mcp` | **嵌入式开源包**（GitHub CoplayDev/unity-mcp v10.3.0） | 否（**开发期**） | 以嵌入方式装在 `Packages/`，本地 HTTP `localhost:8080/mcp`，供 AI 在编辑器里操作 Unity。打包后不需要 |
| 7 | `mcp-for-blender` | PyPI 包（开发期） | 否 | Blender 侧 MCP，stdio + TCP `localhost:9878`，与 Blender 插件通信 |
| 8 | `C:\Users\19591\.workbuddy-ai\mcp.json` | 本机 MCP 配置 | 否 | 记录上述两个 MCP 连接的启动方式 |
| 9 | numpy / Pillow / matplotlib | PyPI（开发期） | 否 | 只在 `Tools/` 下离线脚本中使用，运行时不参与 |
| 10 | `simhei.ttf` | Windows 系统字体 | 否 | 构建脚本从系统字体复制一份进 `Assets/Fonts/`，随工程自带 |
| 11 | `Assets/维什戴尔.mp4`、`ZZUT.mp4` | 用户自带视频素材 | **是（本地文件）** | `VideoPlayer.clip` 指向 `Assets/维什戴尔.mp4`；本地资源，非远程流 |
| 12 | `Assets/教室.fbx`、`class1.fbx`、`PC.fbx` | 用户自带模型 | **是（本地文件）** | 静态教室模型 |
| 13 | 后端 / 数据库 / CDN / API Key / 账号登录 | — | **无** | 项目完全没有这类依赖 |

---

## 4. 三个最有亮点的功能

> 每个亮点附一张**真实运行截图**与一段实现原理。截图使用本机绝对路径（沿用本项目既定约定），技术展示图见第 10 章（相对路径）。

### 4.1 亮点一：CCTV 监控视角滤镜系统

![监控画面效果](D:/Uproject/UI/Tools/监控画面效果.png)

**实现原理。** 滤镜不是后处理栈，而是**一张独立的 `ScreenSpaceOverlay` Canvas**（sortingOrder=50，低于控制面板的 100），由 5 层 `Image` / `RawImage` 顺序叠加：灰绿色调层降饱和、暗角贴图压四角、扫描线贴图按 `scanlineTiling=(1,80)` 平铺、噪点贴图按 `(3,1.69)` 平铺，加上 REC / CAM / 时间戳三个 `Text`。动态部分集中在 `SecurityCameraFX.Update()`：噪点以 14 FPS 随机改写 `grainLayer.uvRect` 模拟胶片颗粒，扫描线 UV 按 0.35/s 滚动，REC 点用 `Mathf.PingPong` 控制 alpha，时间戳每秒取 `DateTime.Now`。贴图全部由 `gen_fx_textures.py` 程序生成，噪点 alpha 峰值压到 60/255 并在 UI 层再乘 0.6，避免「雪花屏」过重。因为 Overlay 画布不会被相机 `Render()` 捕获，验收时把滤镜层单独渲染成带 alpha 的 PNG，再由 `composite_security.py` 做 `Image.alpha_composite` 合成，得到上图的最终效果。**这一层完全自包含，可整包搬去别的工程。**

### 4.2 亮点二：空调声音与气流粒子联动

![空调粒子效果](D:/Uproject/UI/Tools/空调粒子效果.png)

**实现原理。** 声音不走「找素材」，而是用 numpy **在频域合成**：对白噪声做 `rfft` 后乘以「低频隆隆 + 中频吹风 + 高频滚降」包络再 `irfft`，叠加 50/100/150/200/250Hz 压缩机谐波与 0.25Hz 呼吸调制，写出 12s / 44.1kHz / 单声道 16bit 的 `AC_Hum.wav`。因为信号长度恰为 N 点且所有调制频率都是 `1/duration` 的整数倍，**首尾天然衔接、循环无爆音**。运行时 `ClassroomSceneController` 让 `AudioSource` 常驻 `loop` 播放、初始音量 0，开关只改 `acTargetVolume`，`Update()` 里用 `Mathf.MoveTowards` 做 0.7s 渐变，规避解码起播延迟。气流粒子由构建器定位空调节点后，在其顶部格栅按指向房间中心的方向发射；关键是 `ParticleSystemRenderer.renderMode = Stretch` 配合 `velocityScale=0.07` / `lengthScale=1.8`，把柔和光斑拉成流线，而不是一团圆泡泡；再加 Noise 模块产生扰动、ColorOverLifetime 做淡入淡出。关机时用 `Stop(StopEmitting)` 只停发射，让空中残留粒子自然飘散。

### 4.3 亮点三：三视角状态机 + 不抢 UI 输入的第一人称漫游

![第一人称视角](D:/Uproject/UI/Tools/第一人称视角.png)

**实现原理。** 三台相机（原有 Main Camera、角落监控相机、角色上的 FP 相机）**不做 Camera.main 替换、不切场景**，统一由 `ClassroomSceneController.ApplyView(ClassroomView)` 单点收口：一次调用同时设置三台相机的 `enabled`、滤镜 Canvas 的 `SetActive`、第一人称角色的 `activeSelf` 与 `SetControl`、以及 `RefreshUI()` 的文字与配色。这样新增视角只改一个方法，不会出现「相机切了但滤镜没关」的状态漂移。快捷键 `1/2/3` 直达任意态，`Esc` 与重复点击按钮回全景。第一人称刻意不依赖 `Rigidbody`、不锁光标：只用 `CharacterController` 做移动与重力，**只有按住鼠标右键时**才 `CursorLockMode.Locked` 并读取 `Mouse X/Y`；松开立即恢复光标，因此左侧面板始终可点。这套「按需锁光标」是共存的关键。

---

## 5. 技术实现方案

### 5.1 系统架构

![系统架构图](Docs/images/architecture.png)

五层结构：交互层只负责收集输入；控制层是唯一状态源；场景对象层是被驱动的相机 / 画布 / 粒子 / 灯光；资源层为本地素材；工具层在编辑器与离线阶段生成并绑定上述全部对象。**运行时不依赖工具层**，打包后可完全移除 `Assets/Editor/` 与 `Tools/`。

### 5.2 交互流程

![交互流程图](Docs/images/interaction-flow.png)

一次操作（按钮 / 快捷键）进入 `ClassroomSceneController` 后，先更新 `CurrentView` / `AcOn` / `ProjectorOn` 三个状态位，再并行驱动空调、视角、投影、UI 四条分支，最终视听反馈与界面状态达成一致。**界面回写与业务逻辑同处一个方法**，是本项目刻意选择的简单做法：状态量只有一个来源，不需要事件总线或 MVC 框架。

### 5.3 视角状态机

![视角状态机](Docs/images/view-state.png)

所有切换走 `ApplyView()`。每个状态明确规定了相机、滤镜、角色、光标与状态栏提示五个维度的取值，避免散落在多个脚本里各改一半。

### 5.4 核心数据链路

![数据链路图](Docs/images/data-flow.png)

三条链路互相独立：视频链路（MP4 → VideoPlayer → RenderTexture → 材质 `_BaseMap`）、音频链路（numpy → WAV → 常驻 AudioSource → 唯一 AudioListener）、滤镜链路（程序生成贴图 → RawImage 分层 → FX 动画 → Overlay Canvas → alpha 合成验证）。

### 5.5 构建与交付流水线

![构建流水线](Docs/images/build-pipeline.png)

构建脚本**幂等**：每次先删除名为 `教室控制台` 的旧根节点再重建，因此反复执行不会产生重复对象；所有摆位由 `ComputeRoomBounds()` 推导，换一套教室模型无需改坐标。预览图与文档同源，重建后同名覆盖，链接不失效。

### 5.6 核心代码模块说明

| 模块 | 文件 | 关键逻辑 |
|---|---|---|
| 总控 | `ClassroomSceneController.cs` | `Awake()` 注册 4 个按钮监听、启动常驻音频、关闭气流与默认视角；`Update()` 做音量渐变 + 快捷键；`ApplyView()` 统一切视角；`RefreshUI()` 回写按钮文字与颜色 |
| 第一人称 | `FirstPersonWalker.cs` | `CharacterController.Move((dir * speed + Vector3.up * verticalSpeed) * dt)`；`Input.GetMouseButton(1)` 控制 `Cursor.lockState`；`OnDisable()` 兜底恢复光标 |
| 投影仪 | `ProjectorScreen.cs` | `Awake()` 创建 1×1 白色 `blank` 贴图并探测材质属性名（`_BaseMap` / `_MainTex` / `_BaseColor`，兼容 URP 与内置管线）；`SetOn()` 切 `VideoPlayer.Play/Stop`、材质贴图、灯光、`videoAudio.mute` |
| 监控滤镜 | `SecurityCameraFX.cs` | `OnEnable()` 复位滚动量；`Update()` 用 `Time.unscaledTime` 驱动噪点定帧刷新、扫描线滚动、REC 闪烁与时间戳 |
| 场景构建器 | `Assets/Editor/ClassroomUIBuilder.cs` | `Build()` 打开场景 → `ComputeRoomBounds()` → 删旧根 → 生成全部对象 → `SaveScene()` → 渲染 6 张预览；`BuildSecurityOverlay()` 建 5 层滤镜；`BuildACAir()` 定位空调并配置 Stretch 粒子；`SaveCanvasPreview()` 把 Overlay 临时转 World Space 用正交相机拍摄 |
| UI 构建 | 同上 `BuildUI()` | `CanvasScaler.ScaleWithScreenSize` 1920×1080；`VerticalLayoutGroup` + `LayoutElement`；`LayoutRebuilder.ForceRebuildLayoutImmediate` 后打印实测尺寸验证不溢出 |
| 资源生成 | `Tools/gen_ac_hum.py`、`gen_fx_textures.py` | 频域 IFFT 合成无缝音频；numpy 生成噪点 / 扫描线 / 暗角 / 柔光粒子四张 RGBA 贴图 |
| 效果合成 | `Tools/composite_security.py` | `Image.alpha_composite` 把滤镜层叠到监控原始画面 |
| 校验 | `Tools/check_*.py` | `codecs.decode('unicode_escape')` 解码 Unity YAML 中文名；检查控制器引用与对象激活状态 |
| 绘图 | `Tools/gen_readme_diagrams.py` | matplotlib + SimHei 生成 5 张技术展示图，文案改动后可一键重出 |

代表性代码（`ApplyView()` 的收口思路）：

```csharp
public void ApplyView(ClassroomView view)
{
    CurrentView = view;
    if (overviewCamera != null)  overviewCamera.enabled  = view == ClassroomView.Overview;
    if (securityCamera != null)  securityCamera.enabled  = view == ClassroomView.SecurityCamera;
    if (securityOverlay != null) securityOverlay.SetActive(view == ClassroomView.SecurityCamera);
    if (firstPersonWalker != null)
    {
        bool fp = view == ClassroomView.FirstPerson;
        if (firstPersonWalker.gameObject.activeSelf != fp)
            firstPersonWalker.gameObject.SetActive(fp);
        firstPersonWalker.SetControl(fp);
    }
    RefreshUI();
}
```

### 5.7 关键设计决策

1. **单一状态源**：三个 bool/enum 状态位 + 一个 `RefreshUI()`，不引入事件总线，代码量小、调试直观。
2. **常驻音频 + 音量渐变**：避免 `Play()` 的解码延迟，也让淡入淡出天然成立。
3. **白幕而非黑幕**：编辑模式 `Awake()` 不执行，若材质默认绑空 RT 会得到黑块；改为白色基色，运行时再切 RT。
4. **按需锁光标**：右键才锁，兼顾漫游与常驻 UI 点击。
5. **全场唯一 AudioListener**：挂在控制台根节点，切相机不断音、无多监听器告警。
6. **纯本地程序化资源**：不引入外部素材，音频 / 贴图可重生成，无版权与联网问题。
7. **构建脚本幂等 + 包围盒自适应**：让整个控制台可「当插件」加装到别的教室场景。

---

## 6. 代码文件清单与总行数

### 6.1 本项目自研代码（合计 **2132 行**）

| 文件 | 行数 | 职责 |
|---|---:|---|
| `Assets/Editor/ClassroomUIBuilder.cs` | 977 | 编辑器一键构建、包围盒计算、UI / 滤镜 / 粒子生成、预览渲染 |
| `Assets/Scripts/ClassroomUI/ClassroomSceneController.cs` | 216 | 总控、状态机、快捷键、UI 回写、空调联动 |
| `Assets/Scripts/ClassroomUI/ProjectorScreen.cs` | 108 | 投影仪播放、材质贴图切换、灯光与伴音 |
| `Assets/Scripts/ClassroomUI/FirstPersonWalker.cs` | 91 | 第一人称移动、按需锁光标 |
| `Assets/Scripts/ClassroomUI/SecurityCameraFX.cs` | 73 | 监控滤镜动画与时间戳 |
| **C# 小计** | **1465** | 5 个文件 |
| `Tools/gen_readme_diagrams.py` | 381 | 生成 5 张技术展示图 |
| `Tools/gen_fx_textures.py` | 80 | 生成 4 张滤镜 / 粒子贴图 |
| `Tools/gen_ac_hum.py` | 61 | 合成 12s 无缝空调音 |
| `Tools/check_scene.py` | 48 | 场景对象名与结构校验 |
| `Tools/check_wiring.py` | 44 | 控制器引用绑定校验 |
| `Tools/composite_security.py` | 32 | 滤镜层 alpha 合成 |
| `Tools/check_active.py` | 21 | 对象默认激活状态校验 |
| **Python 小计** | **667** | 7 个文件 |

### 6.2 工程内随包附带、非本项目自研（999 行）

| 文件 | 行数 | 来源 |
|---|---:|---|
| `Assets/ModularFirstPersonController/FirstPersonController/FirstPersonController.cs` | 741 | 第三方第一人称控制器，**本项目未调用** |
| `Assets/TutorialInfo/Scripts/Editor/ReadmeEditor.cs` | 242 | Unity 3D 模板 |
| `Assets/TutorialInfo/Scripts/Readme.cs` | 16 | Unity 3D 模板 |

### 6.3 统计口径

- 统计命令：`wc -l $(find Assets -name "*.cs") $(find Tools -maxdepth 1 -name "*.py")`
- 工程内全部 `.cs` + `.py` 合计 **3131 行** = 自研 2132 + 随包附带 999。
- 不含 `.unity` 场景 YAML（714 KB）、`.meta`、材质 / 贴图 / 音视频等二进制资源。
- 行数会随 `gen_readme_diagrams.py` 文案调整略有浮动。

---

## 7. 自包含可复用模块（可直接拆给别的工程）

| 可复用模块 | 文件清单 | 外部依赖 | 迁移方式 |
|---|---|---|---|
| **第一人称漫游**（★★★ 完全自包含） | `Assets/Scripts/ClassroomUI/FirstPersonWalker.cs` | 仅 `UnityEngine` + `CharacterController` + 旧 Input Manager | 单文件拷走，挂到带 `CharacterController` 的物体，把子相机拖给 `viewCamera`；调用 `SetControl(true)` 启用 |
| **CCTV 监控滤镜**（★★★ 自包含） | `Assets/Scripts/ClassroomUI/SecurityCameraFX.cs`<br>`Assets/Textures/ClassroomFX/{grain,scanlines,vignette,particle_soft}.png` | uGUI（`RawImage` / `Text`）+ 4 张贴图 | 拷脚本与贴图，建一个 Overlay Canvas 按「色调→暗角→扫描线→噪点→角标」叠层，把 4 个引用拖上；贴图需设 `Wrap Mode = Repeat`、`Alpha Is Transparency` |
| **投影屏幕控制**（★★☆ 轻耦合） | `Assets/Scripts/ClassroomUI/ProjectorScreen.cs` | `UnityEngine.Video`、`RenderTexture`、`Renderer`、可选 `Light` / `AudioSource` | 拷脚本；在目标工程自建 RT 与 Unlit 材质，赋给 `videoPlayer.targetTexture` 与 `screenRenderer`；脚本会自动探测 `_BaseMap` / `_MainTex` |
| **程序化音频**（★★★ 独立） | `Tools/gen_ac_hum.py` | Python + numpy | 改文件末尾 `out` 路径即可；参数 `SR` / `DUR` / 谐波表可直接调 |
| **程序化贴图**（★★★ 独立） | `Tools/gen_fx_textures.py` | Python + numpy + Pillow | 改 `OUT` 常量；随机种子固定，结果可复现 |
| **透明层合成**（★★★ 独立） | `Tools/composite_security.py` | Python + Pillow | 改 3 个路径常量 |
| **技术展示图生成**（★★★ 独立） | `Tools/gen_readme_diagrams.py` | Python + matplotlib + 中文字体 | 改 `OUT_DIR` 与 `FONT_PATH`（Linux/macOS 换成本地中文字体） |
| **场景校验脚本**（★★☆ 需改常量） | `Tools/check_scene.py`、`check_wiring.py`、`check_active.py` | Python 标准库 | 路径与对象名硬编码了本项目场景，需替换常量后使用 |

**不建议直接拆分（强耦合，仅作架构参考）：**

- `Assets/Scripts/ClassroomUI/ClassroomSceneController.cs` —— 字段绑定了本项目 4 个按钮、3 台相机、空调、投影仪。
- `Assets/Editor/ClassroomUIBuilder.cs` —— 硬编码 `Assets/Classroom.unity`、资源路径、FBX 节点名（`AirConditioner`）与字体来源；换工程要重写资源路径与节点查找逻辑，但**「幂等构建 + 包围盒自适应」的设计模式可直接借鉴**。

---

## 8. 踩坑日志

### 8.1 问题速查表

| # | 现象 | 根因 | 解决方案 |
|---|---|---|---|
| 1 | Unity 包管理器装 git URL 包失败 | 本机 `git` 不在系统 PATH，只有 WorkBuddy 自带 PortableGit | 改用**嵌入式包**：clone 后复制到 `Packages/com.coplaydev.unity-mcp` |
| 2 | 批处理报项目被锁 | 用户开着 Unity 编辑器占用同一工程 | 关闭编辑器后再跑 `-batchmode`；不要并发开两个实例 |
| 3 | `grep` 中文对象名结果为 0 | Unity YAML 把中文存成 `\u6295\u5F71\u4EEA` 转义 | 校验脚本用 `codecs.decode('unicode_escape')` 解码后再比对 |
| 4 | 编辑器里幕布是纯黑方块 | 编辑模式 `Awake()` 不执行，RenderTexture 尚无内容 | 材质默认用 Unlit 白色基色、不预贴 RT；运行时由 `SetOn()` 再切 |
| 5 | UI 预览图与全景图 MD5 完全相同 | `ScreenSpaceOverlay` Canvas 在批处理中不会被相机 `Render()` 捕获 | `SaveCanvasPreview()` 临时把 Canvas 转 `WorldSpace`（1920×1080 × 0.01），用正交相机精确拍摄，拍完还原 |
| 6 | 第一版监控滤镜像「雪花屏」 | 噪点 alpha 190 / 扫描线 150 过重 | 贴图 alpha 降到 60，UI 层颜色再乘 0.6，只留淡淡颗粒 |
| 7 | 第一版气流像一团泡泡 | `startSize` 0.26~0.48 太大、Billboard 渲染 | 缩到 0.10~0.20 + `Stretch` 渲染 + `lengthScale=1.8` 拉成流线 |
| 8 | 空调特写相机跑到墙外，画面被墙挡住 | 空调在房间角落，简单沿出风方向偏移会穿墙 | 用 `Vector3.Cross(up, forward)` 取垂直方向，选靠房间内侧的一侧，再 `Mathf.Clamp` 把机位夹在包围盒内 |
| 9 | 气流从机身中部吹出，不像出风口 | 发射点取了 `b.center + up * size.y * 0.18` | 上移到 `* 0.35`（顶部格栅高度） |
| 10 | 切相机后声音中断 / 控制台刷「多个 AudioListener」 | 每个相机自带监听器 | 构建时销毁全部已有 `AudioListener`，只在 `教室控制台` 根节点保留一个 |
| 11 | 第一人称一进入就无法点左侧按钮 | 常规 FPS 会 `Cursor.lockState = Locked` | 改为**仅按住右键**锁定，松开恢复；`OnDisable()` 兜底重置 |
| 12 | 空调开声音有半秒延迟 | 每次开关重新 `Play()` 触发解码 | `AudioSource` 常驻 loop，`volume` 用 `MoveTowards` 做 0.7s 淡入淡出 |
| 13 | 批处理下没有 Game View，UI 布局不一定刷新 | 无游戏窗口时 Overlay / ScreenSpaceCamera 布局不保证 | 同 #5，World Space + `Canvas.ForceUpdateCanvases()` + `LayoutRebuilder.ForceRebuildLayoutImmediate()` |
| 14 | 面板内容可能溢出 | 固定高度 + 动态文字 | 构建后 `ForceRebuildLayoutImmediate` 并打印每个子项实测高度，确认 412px 内容落在 438px 内容区内 |
| 15 | 视频伴音在投影关闭后仍在响 | `VideoPlayer.Stop()` 不会自动静音目标 AudioSource | `ProjectorScreen.SetOn()` 里显式 `videoAudio.mute = !on` |
| 16 | 中文文件名写入后被改名 / 路径引用失效 | 本机文件被人工重命名；相对/绝对路径约定不一致 | 文档与生成脚本统一使用同一套中文名；技术展示图用相对路径、运行截图用绝对路径，并在配图索引中集中登记 |
| 17 | 改了编辑器脚本但批处理仍跑旧逻辑 | Unity 未重新编译程序集 | 打开一次编辑器触发编译，或删除 `Library/ScriptAssemblies` 后重跑 |

### 8.2 重点复盘

**① Overlay Canvas 的两难（#4 #5 #13）。** 这是本项目最花时间的一组问题。Overlay 画布在真机 / Game View 里正常显示，但在 `-batchmode` 下相机 `Render()` 拿不到它，导致「预览图和场景图一模一样」这种极隐蔽的 bug（靠 MD5 比对才发现）。最终没有改用 ScreenSpaceCamera（它在无 Game View 时布局同样不稳），而是**只在出预览图的瞬间**把 Canvas 切成 World Space、按 1920×1080 × 0.01 缩放、用 `orthographicSize=5.4` 的正交相机拍，拍完在 `finally` 里逐项还原 `renderMode` / `worldCamera` / `scale` / `position` / `rotation` / `sizeDelta`。教训：**编辑器批处理里的「所见」不等于运行时的「所得」，验证手段本身也要验证。**

**② 程序化资源的无缝性（#12）。** 直接截取一段白噪声首尾相接必然爆音。做法是在频域构造长度恰为 N 的频谱并 `irfft`，信号天然以 N 为周期；所有调制频率取 `1/duration` 的整数倍（0.25Hz → 12s 内 3 周期，7.5Hz → 90 周期），首尾差值接近 0。这比时域加交叉淡化更干净。

**③ 粒子「像风」还是「像泡泡」（#7）。** 决定观感的不是粒子数量而是**渲染模式**：Billboard 圆形光斑再多也像气泡，`Stretch` 按速度拉伸后才有流向感；配合低 alpha 渐变与 Noise 扰动，才读作气流。

**④ 幂等构建带来的可维护性（#14）。** 早期手工搭对象时，漏绑一个 `acAir` 引用就要在 Inspector 里翻半天。改成构建器直接赋值后，又加了 `check_wiring.py` 从 YAML 层复核 18+ 字段，形成「生成 → 保存 → 静态校验」闭环。代价是构建脚本膨胀到 977 行，但换来了换模型不用改坐标、反复执行不产生重复对象。

**⑤ 状态漂移的预防（#10 #11）。** 相机、滤镜、角色、光标四者若各自监听按钮，很容易出现「相机切了滤镜没关」。统一收口到 `ApplyView()` 后，状态空间从 4 个独立变量压缩成 1 个枚举，新增视角只改一处。

---

## 9. 快速上手与维护

### 9.1 目录结构

```
D:\Uproject\UI
├── Assets/
│   ├── Classroom.unity                 # 主场景
│   ├── Scripts/ClassroomUI/            # 5 个运行时/渲染脚本（含 RenderTexture 资源）
│   ├── Editor/ClassroomUIBuilder.cs    # 一键构建器
│   ├── Audio/AC_Hum.wav                # 程序合成空调音
│   ├── Textures/ClassroomFX/           # 4 张程序化贴图
│   ├── Fonts/SimHei.ttf                # 中文字体
│   ├── Materials/                      # 场景材质 + 幕布 / 粒子材质
│   ├── 教室.fbx / class1.fbx / PC.fbx  # 教室模型
│   └── 维什戴尔.mp4 / ZZUT.mp4         # 视频素材
├── Packages/com.coplaydev.unity-mcp    # 嵌入式 Unity MCP（开发期）
├── Docs/images/                        # 5 张技术展示图（PNG）
├── Tools/                              # Python 工具 + 7 张运行截图
├── README.md                           # 本文档
└── 王绍锋-README.md                    # 早期简版文档（保留备查）
```

### 9.2 运行

1. 用 Unity **2022.3.62f3c1** 打开 `D:\Uproject\UI`。
2. 打开 `Assets/Classroom.unity`，点 Play。
3. 点左侧面板按钮，或用 `1/2/3` 切视角、`Q` 空调、`E` 投影仪、`Esc` 回全景。

> 若刚改过 `Assets/Editor/` 下的脚本，先让 Unity 完成一次编译再运行。

### 9.3 重新生成控制台

菜单 **工具 → 教室控制台 → 生成或重建 UI**，或命令行：

```bash
D:\Unity\2022.3.62f3c1\Editor\Unity.exe -batchmode -nographics \
  -projectPath D:\Uproject\UI \
  -executeMethod ClassroomUI.EditorTools.ClassroomUIBuilder.BuildAndSave \
  -quit
```

执行前关闭正在运行的 Unity 编辑器，否则项目被锁。

### 9.4 重新生成资源与图片

```bash
cd D:\Uproject\UI

# 空调音频（numpy）
python Tools/gen_ac_hum.py

# 4 张滤镜/粒子贴图（numpy + Pillow）
python Tools/gen_fx_textures.py

# 监控滤镜合成效果图（Pillow，需先有构建器产出的两张预览图）
python Tools/composite_security.py

# 5 张技术展示图（matplotlib + SimHei）
python Tools/gen_readme_diagrams.py
```

如本机未预装依赖，用 `uv run --with numpy --with pillow --with matplotlib Tools/xxx.py` 亦可。

### 9.5 静态校验

```bash
python Tools/check_scene.py     # 解码中文转义，确认对象名
python Tools/check_wiring.py    # 控制器 18+ 引用字段非空
python Tools/check_active.py    # 滤镜默认关、面板默认开
```

### 9.6 新成员上手路线

1. 读第 1、2 章，先跑一遍 Play 建立直观认识。
2. 看第 5 章的 5 张图，理解「输入 → 总控 → 四分支」。
3. 从 `ClassroomSceneController.cs`（216 行）开始读代码，它是全项目枢纽。
4. 再读 `ProjectorScreen.cs`、`SecurityCameraFX.cs`、`FirstPersonWalker.cs` 三个独立小模块。
5. 最后看 `ClassroomUIBuilder.cs`（977 行）了解对象是怎么被造出来的；**先读 `Build()` 主流程，再跳读各 `BuildXxx()` 子方法**。
6. 想改视觉参数（配色、粒子大小、滤镜强度），优先改构建器里的常量并重新生成，不要直接改场景实例。

---

## 10. 技术展示图与配图索引

### 10.1 技术展示图（PNG，相对路径引用，可随仓库迁移）

| 相对路径 | 内容 |
|---|---|
| `Docs/images/architecture.png` | 系统架构：交互 / 控制 / 场景对象 / 资源 / 工具五层 |
| `Docs/images/interaction-flow.png` | 交互流程：一次操作分发到四个子系统 |
| `Docs/images/view-state.png` | 三视角状态机与切换条件 |
| `Docs/images/data-flow.png` | 视频 / 音频 / 滤镜三条数据链路 |
| `Docs/images/build-pipeline.png` | 构建、预览、校验、文档流水线 |

均由 `Tools/gen_readme_diagrams.py` 生成，改文案后重跑即可同步更新。

### 10.2 运行效果截图（绝对路径引用，本机固定位置）

| 绝对路径 | 内容 |
|---|---|
| `D:/Uproject/UI/Tools/UI界面.png` | 左侧常驻控制面板 |
| `D:/Uproject/UI/Tools/全景视角.png` | 默认全景相机（含待机白幕） |
| `D:/Uproject/UI/Tools/监控视角原始画面.png` | 监控相机，未叠加滤镜 |
| `D:/Uproject/UI/Tools/监控滤镜层.png` | 独立渲染的滤镜层（带 alpha） |
| `D:/Uproject/UI/Tools/监控画面效果.png` | 滤镜合成后的最终监控画面 |
| `D:/Uproject/UI/Tools/第一人称视角.png` | 第一人称漫游视角 |
| `D:/Uproject/UI/Tools/空调粒子效果.png` | 空调出风气流特写 |

> **路径约定说明**：技术展示图按本文档要求使用相对路径，便于随工程整体迁移；运行效果截图沿用项目此前「不使用相对路径」的约定使用绝对路径。若把整个工程搬到别的机器，只需批量替换第 4 章与本节的 `D:/Uproject/UI/` 前缀。

---

## 11. 验证状态与已知限制

### 11.1 已完成的静态验证

- 控制器 18+ 个公开引用字段全部非空（`check_wiring.py`）。
- `VideoPlayer.m_VideoClip` 指向 `Assets/维什戴尔.mp4` 对应 guid。
- 监控滤镜 Canvas 默认 `m_IsActive=0`、控制面板默认激活（`check_active.py`）。
- 中文对象名与场景层级可正确解码（`check_scene.py`）。
- 面板 412px 内容高度落在 438px 内容区内，无溢出。
- 本文档 5 张技术展示图、7 张运行截图路径全部有效。

### 11.2 尚需人工确认（Play 模式）

> 本项目所有预览图均在 **Unity 编辑器批处理 / 编辑模式**下渲染，**未进入 Play 模式做运行时验收**。请在 Play 中确认：

- 视频能否正常解码并投到幕布（不同机器编解码器支持不同）。
- 空调声音淡入淡出是否平滑、循环点有无爆音。
- 监控滤镜的噪点 / 扫描线 / REC 动画是否按预期频率刷新。
- 气流粒子是否连续发射、关机后残留是否自然飘散。
- 第一人称按住右键转视角时，左键点击 UI 是否始终可用。

### 11.3 已知限制

- 视频硬编码指向 `Assets/维什戴尔.mp4`；若要换成 `ZZUT.mp4`，需改构建器 `VideoPath` 常量并重建。
- 输入基于旧版 Input Manager，未适配新 Input System 与触屏。
- 构建器中字体来源、资源路径、FBX 节点名均为本机 / 本场景硬编码，迁移到别的工程需调整（见第 7 章）。
- 中文字体 `SimHei.ttf` 约 9.7 MB，若需精简可换用子集字体。
- 工程内含未使用的第三方 `ModularFirstPersonController` 与 Unity 模板文件，打包前可删除以减小体积。
- 全部 UI 文本为中文，尚无本地化 / 多语言支持。

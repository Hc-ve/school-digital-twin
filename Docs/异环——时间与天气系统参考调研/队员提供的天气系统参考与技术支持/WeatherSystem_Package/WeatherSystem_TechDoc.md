# 智慧校园 · 天气系统 移植技术文档

> 版本：v1.0（2026-11）
> 来源工程：`D:\unityxiangmu\sx5`（SchoolScene 智慧校园）
> 引擎：团结引擎 / Unity 2022.3.62f3c1 · 渲染管线 URP 14.0.12
> 文档目标：把整套天气系统**零依赖迁移**到另一个 Unity 工程，只需拷贝 12 个文件 + 6 步配置。

---

## 目录

1. [系统能力总览](#1-系统能力总览)
2. [技术栈与前置条件](#2-技术栈与前置条件)
3. [文件清单](#3-文件清单)
4. [整体架构与调用链](#4-整体架构与调用链)
5. [核心数据结构：EnvSample](#5-核心数据结构envsample)
6. [状态机：EnvironmentState](#6-状态机environmentstate)
7. [视觉层：WeatherVisuals](#7-视觉层weathervisuals)
8. [积水层：WetGround](#8-积水层wetground)
9. [树根积雪：SnowOnTrees](#9-树根积雪snowontrees)
10. [自动轮换：WeatherDirector](#10-自动轮换weatherdirector)
11. [UI 面板：WeatherPanel](#11-ui-面板weatherpanel)
12. [四个 Shader](#12-四个-shader)
13. [程序化资源：ProceduralAssets](#13-程序化资源proceduralassets)
14. [移植步骤（照做即可）](#14-移植步骤照做即可)
15. [参数调优速查表](#15-参数调优速查表)
16. [已知坑与规避清单](#16-已知坑与规避清单)
17. [验收清单](#17-验收清单)
18. [附录 A：完整源码](#附录-a完整源码)

---

## 1. 系统能力总览

这套天气系统是**完全程序化**的：没有一张外部贴图、没有一个音频文件、没有任何预制体。所有纹理（雨丝、雪花、云体积、星空、雪斑、水洼遮罩）和音效（雨声、风声、雷声）都在运行时用代码生成。

### 1.1 能力矩阵

| 能力 | 实现载体 | 可调参数 |
|---|---|---|
| 7 档天气切换 | `EnvSample` 预设表 | 晴/多云/阴/雨/雷雨/雾/雪 |
| 3 档时间（昼/黄昏/夜） | `EnvironmentState.Build()` 叠乘 | 光强/曝光/环境光/色调倍率 |
| 2.4 秒平滑过渡 | `EnvSample.Lerp` + `SmoothStep` | `transitionDuration` |
| 程序化天空盒 | 运行期 `Skybox/Procedural` 实例 | 天空色/曝光/大气厚度/太阳盘 |
| 平行光 + 环境光 | `RenderSettings` + `Light` | 颜色/强度 |
| 指数平方雾 | `RenderSettings.fog` | 密度/颜色/高度衰减 |
| 雨（含落地溅射） | 2 套 ParticleSystem | 发射率/速度/风力 |
| 雪（含飘旋噪声） | 1 套 ParticleSystem | 发射率/噪声强度 |
| 地面积雪 | 双层贴地雪盘 + 自定义 Shader | 覆盖半径/抬升量/雪量曲线 |
| 树根积雪 | 地形 `TreeInstance` → 合并网格 | 半径/分段/隆起高度 |
| 雨天积水 | 40m 贴地平面 + 自定义 Shader | 湿滑累积/反射/涟漪 |
| 云穹顶 | 球体 + 3D 噪声体积纹理 | 不透明度/漂移速度 |
| 星空穹顶 | 球体 + 三平面投影 | 亮度/云遮挡 |
| 闪电 + 雷声 | 协程闪白 + 延迟播放 | 间隔/闪光曲线 |
| 天气自动轮换 | 转移权重链 | 停留时长/转移概率 |
| 天气影响移动手感 | `MoveFactor` | 雨 -10%、雪 -20% |
| 天气选择 UI | `WeatherPanel`（尺寸可传参） | 列数/字号 |

### 1.2 设计取舍（为什么这么做）

| 决策 | 原因 |
|---|---|
| 用一个 `struct` 同时当"预设"和"过渡中间态" | 加一个参数只改一处；否则要写两套字段 + 一个逐字段搬运函数 |
| 天空盒运行时 `new Material` 而不是改场景资产 | 改场景 `.mat` 会写回磁盘、污染版本库 |
| 雾用 `RenderSettings.fog` 而不是体积雾 | URP 14 无内置体积雾；内置指数雾被 URP/Lit 与 Terrain 原生采样，代价≈0 |
| 积雪/积水做成"贴地独立平面"而不是改地面材质 | 地面材质是磁盘共享资产，改它会脏；独立半透明层可随时开关、可跟相机移动 |
| 云用 3D 噪声而不是 2D 贴图 | 球面 UV 有极点拉伸、三平面有 90° 平铺格子；3D 体积无极点无接缝 |
| 天气轮换用权重链而不是纯随机 | 纯随机会出现"晴天直接跳雷雨"，一眼看出是随机数 |
| 粒子只做相机周围 110m | 逐粒子碰撞代价远大于收益；远处交给雾和色调 |

### 1.3 演进与关键修正记录

这套系统不是一次写成的，下面这些是实际踩过并修掉的坑，**移植时如果照抄旧思路会重犯**：

| # | 问题现象 | 根因 | 最终方案 |
|---|---|---|---|
| 1 | 昼夜切换时天空**从来没变过** | 场景天空盒是 `Skybox/6 Sided`，没有 `_SkyTint/_AtmosphereThickness/_SunSize`，所有 `HasProperty` 判断全为 false | 运行时新建 `Skybox/Procedural` 材质并赋给 `RenderSettings.skybox` |
| 2 | 抬头看天空有放射状条纹 | 球体 UV 在南北极把经线收敛成一点，贴图被无限拉伸 | 改用三平面投影（`SkyDome.shader`） |
| 3 | 三平面云出现整齐格子 | 投影本质是立方的，每 90° 图案重复 | 改用 3D 噪声体积（`CloudDome.shader`） |
| 4 | 雪是死板灰绿平涂、不受光 | 用了 `Sprites/Default`（完全不参与光照） | 自写 `CampusUI/SnowGround`：主光 Lambert + SH 环境光 + 实时阴影 + 雾 |
| 5 | 雪"发虚、一块一块、草地颜色透出来" | 覆盖率 alpha 被噪声调制在 0.7~1.0 波动 | alpha 改为实心（只有最外 7% 淡出），噪声改存 RGB 只做微法线 |
| 6 | 远看地平线有一圈"没下雪"的地 | 近场雪盘只有 90m，雾在 120m 起 | 增加 260m 远场白兜底盘（renderQueue 更低） |
| 7 | 石砖庭院里雪只在砖缝露白 | 雪面停在"地形+0.10m"，而可见铺装网格高于地形，`ZTest LEqual` 让砖面戳穿雪面 | 抬升量改为近场 0.30m / 远场 0.35m（`SnowGroundLiftNear/Far`） |
| 8 | 高空俯视时雨雪是脚下孤零零一团 | 200m 高空仍按地面发射率发射 | 增加 `altFade`：相机 Y 从 60m 到 130m 线性压到 0 |
| 9 | 室内也在下雪 | 无遮挡判断 | 向上 40m 射线命中天花板 → `indoorFade→0` |
| 10 | 雾把 200m 高空俯视糊成一片白 | 同一套雾密度不适用于两种视高 | 雾密度按相机高度衰减到 30% |
| 11 | 雨雪跟错相机（跟到 200m 高空） | `Camera.main` 只按 tag 查找、**不检查 enabled** | 改用 `Camera.allCameras` 取 depth 最大且无 `targetTexture` 的相机 |
| 12 | 每帧调 `DynamicGI.UpdateEnvironment` 掉帧 | 每次都要重算天空 cubemap 卷积 | 0.2 秒节流 + 过渡结束补一次 |

---

## 2. 技术栈与前置条件

### 2.1 必需

| 项 | 要求 | 说明 |
|---|---|---|
| Unity | 2021.3 LTS 及以上（推荐 2022.3） | 用到 `ParticleSystem.noise`、`Texture3D`、`DynamicGI` |
| 渲染管线 | **URP 12+**（本项目 14.0.12） | 两个 Shader 用 `UniversalForward` + URP ShaderLibrary |
| API 兼容级别 | .NET Standard 2.1 | 无特殊依赖 |
| 程序集 | 无需 asmdef，全部落在 `Assembly-CSharp` | 若要拆 asmdef，需让 UI 层引用 Environment 层 |
| 场景要求 | 至少 1 个 `Directional Light`、1 个启用的相机 | 平行光按 `LightType.Directional` 自动查找 |

### 2.2 内置管线（Built-in）能不能用

**不能直接套用**。`SnowGround.shader` 与 `WetGround.shader` 是 URP 专用（`#include "Packages/com.unity.render-pipelines.universal/..."`、`LightMode=UniversalForward`）。
`CloudDome.shader` 与 `SkyDome.shader` 用的是 CG + `UnityCG.cginc`，**在两条管线下都能跑**。

若目标工程是内置管线，需要把两个 URP Shader 改写为：

- `LightMode = ForwardBase`
- `#include "UnityCG.cginc"` + `#include "AutoLight.cginc"`
- `GetMainLight` → `UNITY_LIGHT_ATTENUATION` / `_LightColor0`
- `SampleSH` → `ShadeSH9(float4(normal,1))`
- `MixFog` → `UNITY_APPLY_FOG`
- `TEXTURE2D/SAMPLER` → `sampler2D`

其余 8 个 C# 文件**不需要任何改动**（它们只用 `Shader.Find("CampusUI/...")`）。

### 2.3 命名空间

```
CampusUI.Core         → EnvironmentState, Models(Weather/TimeOfDay)
CampusUI.Environment  → EnvSample, WeatherVisuals, WetGround, SnowOnTrees,
                        WeatherDirector, ProceduralAssets
CampusUI.UI           → WeatherPanel
```

如果目标工程已有同名命名空间冲突，可以整体替换为你的命名空间，但要**同步改 3 处 using**：
`EnvSample.cs`、`WeatherDirector.cs` 里的 `using CampusUI.Core;`，以及 `WeatherVisuals.cs` 里对 `CampusUI.Core.EnvironmentState.FindActiveCamera()` 的全限定调用。

---

## 3. 文件清单

### 3.1 必须拷贝的 12 个文件

| # | 文件 | 行数 | 类型 | 职责 |
|---|---|---|---|---|
| 1 | `Assets/UI_Systems/Scripts/Core/Models.cs` | 片段 | C# 枚举 | `Weather`(7 档) / `TimeOfDay`(3 档) |
| 2 | `Assets/UI_Systems/Scripts/Core/EnvironmentState.cs` | 513 | C# 状态机 | 总控：参数合成、过渡、下发、闪电、音频 |
| 3 | `Assets/UI_Systems/Scripts/Environment/EnvSample.cs` | 220 | C# 数据 | 16 字段快照 + 7 套预设 + Lerp |
| 4 | `Assets/UI_Systems/Scripts/Environment/WeatherVisuals.cs` | 1000 | C# 视觉 | 粒子/云/星/溅射/雪盘/贴地 |
| 5 | `Assets/UI_Systems/Scripts/Environment/WetGround.cs` | 127 | C# 视觉 | 雨天积水层 |
| 6 | `Assets/UI_Systems/Scripts/Environment/SnowOnTrees.cs` | 164 | C# 视觉 | 树根积雪 |
| 7 | `Assets/UI_Systems/Scripts/Environment/WeatherDirector.cs` | 148 | C# 逻辑 | 自动轮换权重链 |
| 8 | `Assets/UI_Systems/Scripts/Environment/ProceduralAssets.cs` | 700+ | C# 资源 | 7 纹理 + 3 音频，全程序化 |
| 9 | `Assets/UI_Systems/Scripts/UI/WeatherPanel.cs` | 230 | C# UI | 天气选择面板 |
| 10 | `Assets/UI_Systems/Resources/SnowGround.shader` | 156 | HLSL/URP | 受光积雪 |
| 11 | `Assets/UI_Systems/Resources/WetGround.shader` | 192 | HLSL/URP | 积水反射 + 涟漪 |
| 12 | `Assets/UI_Systems/Resources/CloudDome.shader` | 106 | CG | 3D 噪声云穹顶 |
| 13 | `Assets/UI_Systems/Resources/SkyDome.shader` | 105 | CG | 三平面星空穹顶 |

> 第 1 项不是整文件拷贝，只需把 `TimeOfDay` / `Weather` 两个枚举定义搬过去（见附录）。

### 3.2 两个可选接入点（不改也能跑，改了才完整）

| 文件 | 接入点 | 作用 |
|---|---|---|
| `FirstPersonController.cs` | `env.MoveFactor` 乘到移速上 | 雨 -10%、雪 -20% 移速，让天气"有体感" |
| 任意 UI 脚本 | `WeatherPanel.Create(parent, x, y, width, columns, fontSize)` | 复用天气面板 |

### 3.3 依赖关系图

```
                    ┌─────────────────────────────┐
                    │  Models.cs                  │
                    │  enum Weather / TimeOfDay   │
                    └──────────────┬──────────────┘
                                   │
                    ┌──────────────▼──────────────┐
                    │  EnvSample.cs               │
                    │  16 字段 + 7 预设 + Lerp     │
                    └──────────────┬──────────────┘
                                   │
   WeatherDirector ──── SetWeather ─┤
   (自动轮换)                        │
                    ┌──────────────▼──────────────┐
   WeatherPanel ────►│  EnvironmentState.cs        │◄──── 任意 UI 按钮
   (天气面板)         │  过渡 / 昼夜叠乘 / 下发       │
                    └───┬───────┬───────┬─────────┘
                        │       │       │
          ┌─────────────▼──┐ ┌──▼────┐ ┌▼──────────────┐
          │ WeatherVisuals │ │ 音频   │ │ RenderSettings │
          │ 粒子/云/星/雪盘 │ │ 3 个源 │ │ 雾/环境光/天空  │
          └───┬────────┬───┘ └───────┘ └───────────────┘
              │        │
      ┌───────▼──┐ ┌───▼────────┐
      │ WetGround│ │ SnowOnTrees│
      │ 积水      │ │ 树根积雪    │
      └───────┬──┘ └───┬────────┘
              │        │
      ┌───────▼────────▼───────┐
      │ ProceduralAssets       │
      │ 7 纹理 + 3 音频（代码）  │
      └────────────────────────┘
```

---

## 4. 整体架构与调用链

### 4.1 三层结构

| 层 | 职责 | 不该做的事 |
|---|---|---|
| **数据层** `EnvSample` | 只存数值、只做插值 | 不碰任何 `Renderer` / `Material` |
| **控制层** `EnvironmentState` | 决定"当前该是什么样"，把数值下发 | 不自己建粒子、不自己算贴图 |
| **表现层** `WeatherVisuals` / `WetGround` / `SnowOnTrees` | 把数值翻译成 draw call | 不读天气枚举、不知道"现在是什么天气" |

这个分层是**能复用的关键**：表现层只认 `float rain/snow/cloud`，所以可以脱离天气系统单独驱动（比如剧情脚本直接调 `Apply(0.8f, 0f, ...)` 做一场"局部暴雨"）。

### 4.2 每帧调用链

```
EnvironmentState.Update()                         [每帧]
 ├─ if (tt < 1)  tt += dt/transitionDuration
 │              cur = EnvSample.Lerp(from, to, SmoothStep(0,1,tt))
 ├─ TickLightning(dt)                            [雷雨时启动协程闪白]
 └─ ApplyAll(cur, dt, force:false)
     ├─ MoveFactor = 1 - 0.10*rain - 0.20*snow   [夹在 0.65~1]
     ├─ skyMat.SetColor/SetFloat(...)            [天空盒 6 个参数]
     ├─ sun.color / sun.intensity                [主光，含闪电增益]
     ├─ RenderSettings.ambientIntensity          [环境光]
     ├─ RenderSettings.fog / fogDensity / fogColor
     │    └─ 雾密度 × 高度衰减系数 hk
     ├─ visuals.Apply(rain, snow, wind, cloud, cloudColor, star,
     │                zenith, horizon, dt)
     │    ├─ indoorFade → indoor?0:1（3/秒）
     │    ├─ snowAccum  → 积 12s / 化 20s
     │    ├─ 雨粒子 emission.rateOverTime = rain*altFade*shelter*3200
     │    ├─ 雪粒子 emission.rateOverTime = snow*altFade*shelter*520
     │    ├─ 云穹顶 opacity / 旋转
     │    ├─ 星空 opacity = star*(1-cloud*0.92)
     │    ├─ 溅射 rate = rain*altFade*indoorFade*1300
     │    ├─ 雪盘 _SnowAmount = snowAccum*altFade*indoorFade
     │    ├─ Shader.SetGlobalFloat("_GlobalSnow", a)
     │    └─ wet.Apply(rain, dt, sky, horizon, indoor)
     └─ DynamicGI.UpdateEnvironment()            [0.2s 节流]

WeatherVisuals.LateUpdate()                       [每帧，晚于相机移动]
 ├─ transform.position = cam.position            [整块跟相机，不跟旋转]
 ├─ UpdateRange(cam.y) → altFade
 └─ UpdateGround(cam.position)                   [0.15s 节流射线]
     ├─ groundY = ProbeGround(cam)               [向下 400m]
     ├─ indoor  = 向上 40m 是否命中
     ├─ 溅射层 / 近场雪盘 / 远场雪盘 / 积水层 贴到 groundY
     ├─ DrapeSnow(cam)                           [逐顶点射线贴地形]
     └─ DrapeSnowFar(cam)
```

### 4.3 为什么用 `LateUpdate` 而不是 `Update`

雨雪云雪盘必须跟着**本帧最终的相机位置**。第一人称控制器在 `Update` 里移动相机，`LateUpdate` 才能读到移动后的位置；否则快速移动时雨雪会拖后一帧，出现"粒子墙"。

---

## 5. 核心数据结构：EnvSample

`EnvSample` 是一个 `struct`，同时承担两个角色：**天气预设表** + **过渡插值载体**。

### 5.1 16 个字段

| 分组 | 字段 | 类型 | 含义 | 最终去向 |
|---|---|---|---|---|
| 天空盒 | `skyTint` | Color | 天空主色 | `_SkyTint` |
| | `exposure` | float | 曝光 | `_Exposure` |
| | `atmosphere` | float | 大气厚度 | `_AtmosphereThickness` |
| | `sunDisk` | float | 太阳圆盘大小 | `_SunSize` |
| 主光 | `sunColor` | Color | 平行光颜色 | `Light.color` |
| | `sunIntensity` | float | 平行光强度 | `Light.intensity` |
| 环境光 | `ambientIntensity` | float | 环境光强度 | `RenderSettings.ambientIntensity` |
| 雾 | `fogDensity` | float | 雾密度（0=关雾） | `RenderSettings.fogDensity` |
| | `fogColor` | Color | 雾色 | `RenderSettings.fogColor` |
| 粒子 | `rain` | float | 雨强 0~1.6 | 雨粒子发射率 |
| | `snow` | float | 雪强 0~1.2 | 雪粒子发射率 + 积雪累积目标 |
| | `wind` | float | 风 0~1 | 粒子横移 + 云漂移 |
| 云 | `cloud` | float | 云量 0~1 | 云穹顶 `_Opacity` |
| | `cloudColor` | Color | 云色 | 云穹顶 `_Color` |
| 特殊 | `lightning` | float | 雷 0~1 | 闪电触发频率 |
| | `star` | float | 星空 0~1 | 星空穹顶不透明度 |

### 5.2 七套预设（天空 / 光照部分）

| 天气 | skyTint | exposure | atmosphere | sunDisk | sunColor | sunInt | ambient | fogDensity | fogColor |
|---|---|---|---|---|---|---|---|---|---|
| 晴 Clear | 0.45, 0.62, 0.92 | 1.25 | 0.85 | 0.040 | 1.00, 0.96, 0.88 | 1.25 | 0.85 | 0 | 0.70, 0.75, 0.80 |
| 多云 Cloudy | 0.55, 0.66, 0.82 | 1.05 | 1.00 | 0.030 | 1.00, 0.95, 0.88 | 0.85 | 0.80 | 0 | 0.66, 0.70, 0.75 |
| 阴 Overcast | 0.56, 0.58, 0.63 | 0.85 | 1.10 | 0.012 | 0.86, 0.88, 0.92 | 0.42 | 0.70 | 0.0020 | 0.62, 0.65, 0.69 |
| 雨 Rain | 0.42, 0.47, 0.55 | 0.70 | 1.15 | 0.008 | 0.72, 0.78, 0.88 | 0.26 | 0.60 | 0.0055 | 0.55, 0.59, 0.64 |
| 雷雨 Storm | 0.26, 0.29, 0.36 | 0.55 | 1.20 | 0.006 | 0.58, 0.62, 0.74 | 0.16 | 0.50 | 0.0075 | 0.42, 0.45, 0.50 |
| 雾 Fog | 0.64, 0.66, 0.68 | 0.90 | 1.05 | 0.010 | 0.92, 0.92, 0.92 | 0.45 | 0.78 | 0.0140 | 0.74, 0.76, 0.78 |
| 雪 Snow | 0.68, 0.73, 0.82 | 0.95 | 1.10 | 0.014 | 0.95, 0.97, 1.00 | 0.52 | 0.85 | 0.0070 | 0.79, 0.83, 0.88 |

### 5.3 七套预设（粒子 / 云 / 特殊部分）

| 天气 | rain | snow | wind | cloud | cloudColor | lightning | star |
|---|---|---|---|---|---|---|---|
| 晴 | 0 | 0 | 0.08 | 0 | 1.00, 1.00, 1.00 | 0 | 0 |
| 多云 | 0 | 0 | 0.22 | 0.28 | 0.95, 0.96, 0.98 | 0 | 0 |
| 阴 | 0 | 0 | 0.32 | 0.55 | 0.72, 0.74, 0.78 | 0 | 0 |
| 雨 | 1.0 | 0 | 0.50 | 0.72 | 0.55, 0.58, 0.63 | 0 | 0 |
| 雷雨 | 1.6 | 0 | 0.90 | 0.86 | 0.35, 0.38, 0.44 | 1 | 0 |
| 雾 | 0 | 0 | 0.05 | 0.40 | 0.80, 0.82, 0.84 | 0 | 0 |
| 雪 | 0 | 1.0 | 0.20 | 0.62 | 0.82, 0.85, 0.90 | 0 | 0 |

### 5.4 插值

```csharp
public static EnvSample Lerp(EnvSample a, EnvSample b, float t)
```

- `t<=0` 直接返回 `a`，`t>=1` 直接返回 `b`（避免无谓的 16 次 Lerp）；
- 颜色用 `Color.Lerp`，标量用 `Mathf.Lerp`；
- **加字段时只改这一个函数**，这是把"预设"和"运行态"合并成一个结构体的最大收益。

调用方传的 `t` 不是线性的，而是 `SmoothStep(0,1,tt)` —— 起止速度慢、中间快，符合"天气渐变"的直觉，不会出现线性过渡那种"匀速变化"的机械感。

---

## 6. 状态机：EnvironmentState

### 6.1 职责

单例，`DontDestroyOnLoad`。是**唯一**知道"当前是什么天气"的地方。对外只有 3 个入口：

```csharp
public void Set(TimeOfDay t, Weather w);   // 同时设两个维度
public void SetTime(TimeOfDay t);          // 只改时间
public void SetWeather(Weather w);         // 只改天气
public event Action<TimeOfDay, Weather> OnChanged;   // UI 订阅
```

`Set` 里有个细节：`same = (timeOfDay==t && weather==w)`，如果两个都没变，`tt` 直接置 1（不播过渡）。避免用户连点同一个按钮时反复触发 2.4 秒插值。

### 6.2 昼夜叠乘（`Build`）

天气预设是**白天基准**，黄昏/夜晚在它之上做乘法修正：

| 修正项 | 黄昏 Dusk | 夜晚 Night |
|---|---|---|
| `sunMul` 光强 | ×0.50 | ×0.10 |
| `expMul` 曝光 | ×0.78 | ×0.34 |
| `ambMul` 环境光 | ×0.80 | ×0.30 |
| `skyMul` 天空色 | ×0.92 | ×0.20 |
| `blueBias` 蓝通道额外 | ×0.90 | ×1.70 |
| `fogMul` 雾色 | ×0.80 | ×0.42 |
| `sunTint` 光色 | (1.00, 0.66, 0.42) 暖橙 | (0.58, 0.68, 1.00) 冷蓝 |
| `sunDisk` 太阳盘 | ×0.7 | ×0.12 |
| `cloudColor` 云色 | ×(0.85, 0.72, 0.66) | ×(0.22, 0.24, 0.34) |
| `star` 星空 | 0.25 | 1.0 |

三个容易漏的细节：

1. **蓝通道单独处理**：`skyTint.b * skyMul * blueBias`，且 `Mathf.Min(1f, ...)` 夹住。夜里如果只乘 `skyMul=0.2`，天空会变成纯灰黑，失去"夜空偏蓝"的感觉。
2. **夜里必须压暗太阳盘**：否则天空黑着、却挂着一个亮白圆盘，非常出戏。
3. **夜里必须压暗云色**：否则白云会浮在夜空上像块白布。

### 6.3 `ApplyAll` 的 9 个下发目标

| 顺序 | 目标 | 代码 |
|---|---|---|
| 1 | 移动手感 | `MoveFactor = 1 - 0.10*rain - 0.20*snow`，夹在 `0.65~1` |
| 2 | 天空盒 | 6 个 `HasProperty` 保护下的 `SetColor/SetFloat` |
| 3 | 主光 | `sun.color` / `sun.intensity * (1+flash*5)` |
| 4 | 环境光 | `ambientIntensity * (1+flash*1.8)` |
| 5 | 雾 | `fogMode = ExponentialSquared`，密度 × 高度系数 |
| 6 | 视觉层 | `visuals.Apply(...)` 9 个参数 |
| 7 | 音频 | 雨声音量 `rain/1.2*sfxScale`，风声音量 `wind*sfxScale*0.7` |
| 8 | GI | `DynamicGI.UpdateEnvironment()`（0.2s 节流） |
| 9 | 闪电 | 由 `TickLightning` 单独驱动 |

### 6.4 雾的高度衰减

```csharp
float h = cam.transform.position.y;
float hk = Mathf.Lerp(1f, 0.30f, Mathf.Clamp01((h - 5f) / 180f));
RenderSettings.fogDensity = s.fogDensity * hk;
```

- 地面（Y≈2m）→ `hk=1.0`，雾是设计值；
- 200m 俯视 → `hk≈0.30`，否则整个校园糊成一片白。

**这条不能省**：数字孪生/俯瞰模式与第一人称共用一套雾，没有高度补偿就必然有一方是错的。

### 6.5 闪电

真实闪电是"两下"，不是一次闪光：

```
flash = 1.00  → 等 0.07s
flash = 0.16  → 等 0.09s
flash = 0.72  → 等 0.13s
flash = 0.00
```

同时 `thunderSrc.PlayDelayed(Random.Range(0.3f, 1.2f))` —— 雷声比闪电晚 0.3~1.2 秒，这是"先看到再听到"的物理常识，加上之后真实感提升明显。

`flash` 会同时加到天空曝光（`+flash*0.9`）、主光强度（`×1+flash*5`）、环境光（`×1+flash*1.8`）三个通道，所以是"整个场景被照亮"而不是"天空亮了一下"。

### 6.6 相机查找（重要坑）

```csharp
public static Camera FindActiveCamera()
{
    var all = Camera.allCameras;          // 只含真正启用的相机
    Camera best = null;
    for (...) {
        if (c.targetTexture != null) continue;   // 排除小地图相机
        if (best == null || c.depth > best.depth) best = c;
    }
    return best;
}
```

**绝对不要用 `Camera.main`**：它只按 `"MainCamera"` tag 查找、**不检查 `enabled`**。本工程第一人称模式下，孪生相机 `enabled=false` 但 tag 还在，`Camera.main` 会返回那台 200m 高空的相机，导致雾淡掉三倍、雨雪跟到天上。

### 6.7 音频

三段音频全部程序化合成（见第 13 章）：

| 源 | 用途 | 播放方式 |
|---|---|---|
| `RainSfx` | 雨声 | 循环，音量随 `rain` |
| `WindSfx` | 风声 | 循环，音量随 `wind` |
| `ThunderSfx` | 雷声 | 单次，`PlayDelayed(0.3~1.2s)` |

三个 `AudioSource` 都是 `spatialBlend = 0`（2D），因为它们是"环境底噪"而不是场景里的发声物体。

---

## 7. 视觉层：WeatherVisuals

1000 行，是整个系统里最重的一块。它由 `EnvironmentState.EnsureVisuals()` 自动创建（挂在 `WeatherVisuals` 子物体上），**不需要手动摆到场景里**。

### 7.1 常量

| 常量 | 值 | 含义 |
|---|---|---|
| `AreaSize` | 110 m | 粒子发射盒水平尺寸（运行时按相机高度放大） |
| `EmitHeight` | 16 m | 发射盒在相机上方的高度 |
| `DomeRadius` | 380 m | 云穹顶半径（必须 < 相机 farClip） |
| `RainRateMax` | 3200 /s | rain=1 时每秒雨滴数 |
| `SnowRateMax` | 520 /s | snow=1 时每秒雪花数 |
| `SplashArea` | 26 m | 雨滴溅射覆盖范围 |
| `SplashRateMax` | 1300 /s | rain=1 时每秒水花数 |
| `SnowGroundSize` | 90 m | 近场雪盘边长（56 段） |
| `SnowGroundFarSize` | 260 m | 远场雪盘边长（40 段） |
| `SnowGroundLiftNear` | 0.30 m | 近场雪面抬升量 |
| `SnowGroundLiftFar` | 0.35 m | 远场雪面抬升量 |

### 7.2 雨粒子（`BuildRain`）

| 参数 | 值 | 为什么 |
|---|---|---|
| `startLifetime` | 1.4~1.9 s | 从 16m 落到地面约需 1.6s |
| `startSpeed` | 0~0.4 | 初速几乎为 0，靠重力加速 |
| `gravityModifier` | 1.3 | 略大于真实重力，雨丝更有"重量" |
| `startSize` | 0.035~0.06 | 配合 Stretch 拉成细线 |
| `simulationSpace` | World | 相机移动时雨不会跟着平移 |
| `maxParticles` | 9000 | 3200/s × 1.9s ≈ 6100，留余量 |
| `renderMode` | **Stretch** | 注意枚举叫 `Stretch`，不是 Inspector 里显示的 `StretchedBillboard` |
| `velocityScale` | 0.055 | 速度越快拉得越长 |
| `lengthScale` | 2.4 | 雨丝长度倍数 |

生命周期两端用 `FadeGradient()` 淡入淡出（0→1 在 12%，1→0 在 82%），避免雨丝凭空出现/消失。

### 7.3 雪粒子（`BuildSnow`）

和雨的关键差异：

| 参数 | 值 | 作用 |
|---|---|---|
| `startLifetime` | 8~12 s | 雪花飘得慢 |
| `gravityModifier` | 0.035 | 几乎不加速，慢慢落 |
| `startSize` | 0.07~0.16 | 比雨滴大 |
| `noise.enabled` | true | **这是"雪"和"白点雨"最大的观感差别** |
| `noise.strength` | 0.55（运行时 0.35+wind*0.7） | 左右打旋 |
| `noise.frequency` | 0.35 | 旋涡尺度 |
| `noise.octaveCount` | 2 | 两层噪声，够用且便宜 |

### 7.4 雨滴溅射（`BuildSplash`）

不做逐粒子碰撞（几千个雨滴做 Collision 模块会直接卡死），而是在相机脚下放一个 **26×26m 的薄发射盒**，粒子朝上抛出再落回。

这里有一个**极易踩的坑**（代码里留了长注释）：

```csharp
shape.rotation = new Vector3(-90f, 0f, 0f);
shape.scale    = new Vector3(SplashArea, SplashArea, 0.05f);
```

`shape.rotation = (-90,0,0)` 把局部 +Z 映射到世界 +Y。旋转后各维映射为：

- 局部 X → 世界 X（水平）
- 局部 Y → 世界 Z（水平）
- 局部 Z → 世界 Y（竖直）

所以"薄"的那一维必须放在 **Z** 上。如果按直觉写 `scale=(26, 0.05, 26)`，那个 26m 的 Z 维会被立起来，变成一根 **26m 高的竖直发射柱**。

### 7.5 云穹顶（`BuildCloudDome`）

- 球体 `PrimitiveType.Sphere`，`localScale = DomeRadius*2`（原始球直径 1）；
- 用 `CreatePrimitive` 会附带 `MeshCollider`，**必须 Destroy**，否则会挡住射线；
- 材质 queue 2995，`_Opacity` 由 `cloud` 驱动；
- 每帧 `Rotate(0, (2 + wind*12) * dt, 0)` —— 云随风缓慢飘移；
- 只跟相机**位置**不跟**旋转**（在 `LateUpdate` 里 `transform.position = cam.position`），否则云会跟着视角转。

### 7.6 星空穹顶（`BuildStarDome`）

- 半径 `DomeRadius * 2.1`（比云略大，避免两层球面 z-fighting）；
- 用 `SkyDome.shader` 三平面投影（星点稀疏，立方体面的痕迹看不出来）；
- 不透明度 `star * (1 - cloud*0.92)` —— 云越厚星星越少，阴天/雨雪天自动盖掉。

### 7.7 地面积雪：双层雪盘

这是全系统最复杂的部分，也是踩坑最多的地方。

#### 7.7.1 为什么是两层

| 盘 | 边长 | 分段 | renderQueue | 作用 |
|---|---|---|---|---|
| 近场 `SnowGround` | 90 m | 56（57×57=3249 顶点） | 2996 | 脚下精细、逐顶点贴地形 |
| 远场 `SnowGroundFar` | 260 m | 40（41×41=1681 顶点） | 2995 | 补 90~130m 环带，远边被雾淡出 |

雪天雾在 ≈120m 起。如果只有 90m 近场盘，从地平线方向看过去会有一圈"没下雪"的裸地形。远场盘把这个环带补上，且它 130m 外的边缘完全落在雾里，看不到边界。

queue 2995 < 2996，所以重叠区**近场盖远场**，远场只在环带可见。

#### 7.7.2 网格构建（`BuildSnowPlaneMesh`）

**不能用 `CreatePrimitive(Plane)`**：内建平面只有 10 段，铺到 90m 时每格 9m，地形跟随会变成大台阶。这里手写高细分单位平面（x/z ∈ [-0.5, 0.5]，y=0），再靠 `localScale` 缩放到目标米数。

#### 7.7.3 逐顶点贴地（`DrapeSnow` / `DrapeSnowFar`）

```
对每个顶点：
  世界 XZ = 雪盘位置 + 顶点.xz * 盘边长
  向下打 12m 射线 → 取最高命中面高度 th
  顶点世界 Y = th + 抬升量
  顶点本地 Y = Clamp((th + lift) - baseY, -1.5, 1.5)
```

其中 `baseY = groundY + 0.02` 是雪盘自身世界 Y。

**重算触发条件**（三重，避免每帧 3249 条射线）：

1. 相机移动超过 0.5m（`sqrMagnitude >= 0.0625`）
2. 刚开雪（`snowDrapeDirty`）
3. 雪量变化超过 0.04

静止时完全跳过。这是把 3249 条射线从"每帧"降到"走路时每 0.5m 一次"。

#### 7.7.4 抬升量为什么是 0.30 / 0.35

这是第 1.3 节第 7 条的修正。现象是：石砖庭院里"地砖网格清晰、白只在砖缝里"。

原因链：

1. `SampleGroundHeight` 只看得见 **Collider**；庭院铺装是 FBX 里的装饰网格，**地面子网格没有有效碰撞体**；
2. 所以射线只打到地形，雪面停在「地形 + 0.10m」；
3. 可见砖面高于这个位置；
4. Shader 是 `ZTest LEqual` + 透明 + `ZWrite Off`，凸起的砖面直接**戳穿**雪面，只剩凹下去的砖缝露白。

把抬升量提到 0.30m（近场）/ 0.35m（远场）后，雪面高过砖面凸起，整片盖住。

> **移植时的判断方法**：如果你的地面是"一整块带 MeshCollider 的地形/地面网格"，0.10~0.15m 就够；如果有"高于地形的装饰铺装、台阶、花坛"，必须用 0.30m 以上。太小会戳穿，太大会"浮空"。

#### 7.7.5 积雪量累积（`Apply`）

```csharp
if (snow > 0.02f)
    snowAccum = MoveTowards(snowAccum, Clamp01(snow), dt / 12f);   // 积：12 秒
else
    snowAccum = MoveTowards(snowAccum, 0f, dt / 20f);              // 化：20 秒
```

**`snow`（此刻下多大）和 `snowAccum`（地上积了多少）必须分开**。直接拿 `snow` 当积雪量的话，雪一停地面立刻变干净——那是最典型的"假"。

化雪（20s）比积雪（12s）慢，符合直觉。

#### 7.7.6 两个衰减系数

| 系数 | 计算 | 作用 |
|---|---|---|
| `altFade` | 相机 Y 从 60m→130m，`1→0`（SmoothStep） | 200m 高空看雨丝物理上就看不见，硬画出来只是脚下一小团 |
| `indoorFade` | 向上 40m 射线命中天花板 → 3/秒趋近 0 | 室内不该下雪；但留 25% 底噪（`shelter`），站在门口时窗外还在下 |

最终：

```csharp
float a = snowAccum * altFade * indoorFade;
Shader.SetGlobalFloat("_GlobalSnow", a);      // 全局雪量，留给后续本地化积雪
snowGroundMat.SetFloat("_SnowAmount", a);
```

### 7.8 `_GlobalSnow` 全局雪量

`Shader.SetGlobalFloat("_GlobalSnow", a)` 是一个**给未来留的接口**：任何自定义 Shader（比如本地化的 URP Lit / TerrainLit 积雪）都可以直接读这个全局值，不需要再传材质引用。`SnowOnTrees` 就是这么用的。

---

## 8. 积水层：WetGround

### 8.1 为什么独立成组件

它自带一套"湿滑度累积"逻辑，和粒子系统没有耦合。挂在 `WeatherVisuals` 的同一个 GameObject 上，由 `WeatherVisuals.Apply` 每帧调用。

### 8.2 常量

| 常量 | 值 | 含义 |
|---|---|---|
| `Size` | 40 m | 积水层边长 |
| `GroundOffset` | 0.015 m | 比地面高一点，避免 z-fighting |
| `WetInSeconds` | 8 s | 打湿用时 |
| `DryInSeconds` | 26 s | 蒸发用时（比打湿慢，符合直觉） |

### 8.3 湿滑度累积

```csharp
float target = Clamp01(rain);
if (target > wetness)
    wetness = MoveTowards(wetness, target, dt / 8f);    // 打湿
else
    wetness = MoveTowards(wetness, 0f, dt / 26f);       // 蒸发
```

**用 `MoveTowards` 而不是指数插值**：指数插值永远到不了 0，地面会一直是湿的。

### 8.4 开关策略

```csharp
float a = wetness * indoorFade;
bool want = a > 0.01f;
if (want != on) { plane.gameObject.SetActive(want); on = want; }
```

`wetness < 1%` 时整个 GameObject 关掉，不产生任何 draw call。晴天 0 开销。

### 8.5 渲染设置

| 设置 | 值 | 原因 |
|---|---|---|
| `renderQueue` | 2992 | 必须在积雪 2996 之前，雪才能盖住水 |
| `shadowCastingMode` | Off | 水膜不该投影 |
| `receiveShadows` | **false** | 水面靠反射，不靠接收阴影 |
| `lightProbeUsage` | Off | 不用光照探针 |
| `reflectionProbeUsage` | **BlendProbes** | **必须保留**：Shader 里的 `unity_SpecCube0` 是"最近反射探针"，关掉就只剩默认天空反射，场景里放了探针也拿不到建筑倒影 |

### 8.6 天空色下发

```csharp
Color zenith  = s.skyTint;
Color horizon = Color.Lerp(s.fogColor, s.skyTint, 0.35f);
visuals.Apply(..., zenith, horizon, dt);
```

没有反射探针时，积水"看到"的就是这个解析天空。天气一变，地面反光颜色也得跟着变，否则会出现"灰雨天里地面反射蓝天"。

---

## 9. 树根积雪：SnowOnTrees

### 9.1 目标

近似《异环》"树根被雪埋、顶部留秋色"：树干是竖直 mesh，雪堆是地面上的水平圆盘，树干从雪里穿出。

### 9.2 实现

1. 读 `Terrain.activeTerrain.terrainData.treeInstances`；
2. 每棵树根生成一块 `seg × seg` 的圆盘网格（默认 16 段，半径 3m）；
3. **所有树合并进一个 mesh**，只 1 个 draw call；
4. 顶点 Y = `terrain.SampleHeight(...) + 0.02`，再叠加：

```csharp
float r    = sqrt(u*u + v*v) * 2f;              // 0=中心, 1=边缘
float bump = (1f - r) * pileHeight;             // 中心隆起 0.18m
bump += (PerlinNoise(wx*0.2f, wz*0.2f) - 0.5f) * 0.08f;   // 世界锁定低频噪声
```

5. 复用 `CampusUI/SnowGround` 着色器，`renderQueue = 2997`（在雪盘之上）；
6. `Update` 读 `Shader.GetGlobalFloat("_GlobalSnow")`，`>0.01` 才开 `MeshRenderer`。

### 9.3 参数

| 字段 | 默认 | 含义 |
|---|---|---|
| `pileRadius` | 3.0 m | 每棵树根雪堆半径 |
| `seg` | 16 | 单块圆盘分段（越大越圆） |
| `pileHeight` | 0.18 m | 中心最大隆起 |

### 9.4 挂载方式

`WeatherVisuals.EnsureTreeSnow()` 会自动创建一个 `TreeSnowPiles` 子物体并挂上组件，**不需要手动配置**。如果场景里已经有 `SnowOnTrees`（用户手动挂的），则跳过。

> 如果目标工程**不用 Terrain 种树**（比如树是普通 GameObject），这个组件会因 `treeInstances.Length == 0` 自动 `enabled = false`，不会报错。此时需要另写一个"遍历树 Transform 生成雪堆"的版本。

---

## 10. 自动轮换：WeatherDirector

### 10.1 为什么不用纯随机

纯随机会出现"晴天直接跳雷雨"，玩家一眼就看出是随机数。这里用**天气转移权重链**：每种天气只允许转向"气象上说得通"的下一档。

### 10.2 转移表

| 当前 | 可转向（权重） |
|---|---|
| 晴 Clear | 多云 0.60 / 晴 0.25 / 雾 0.15 |
| 多云 Cloudy | 阴 0.45 / 晴 0.35 / 雾 0.10 / 雨 0.10 |
| 阴 Overcast | 雨 0.45 / 多云 0.35 / **雪 0.10** / 雾 0.10 |
| 雨 Rain | 阴 0.50 / 雷雨 0.30 / 多云 0.20 |
| 雷雨 Storm | 雨 0.60 / 阴 0.40 |
| 雾 Fog | 晴 0.50 / 多云 0.50 |
| 雪 Snow | 阴 0.60 / 多云 0.40 |

设计依据：云量是渐变的（晴→多云→阴→雨），雷雨只会从雨里长出来，雾和雪需要特定湿度/温度条件，所以不能直接连到晴天暴晒。

### 10.3 停留时长

`minInterval = 90s`、`maxInterval = 200s`。太短会显得天气在抽搐，太长玩家感受不到变化。

打开自动模式时 `timer = Random.Range(minInterval*0.35f, minInterval*0.75f)` —— 不等满 90 秒，否则玩家会以为按钮没生效。

### 10.4 与手动选择的互斥

`WeatherPanel.PickWeather` 里：

```csharp
var d = WeatherDirector.Instance;
if (d != null && d.auto) d.SetAuto(false);   // 手动选天气 = 接管控制权
```

否则玩家刚选完晴天，40 秒后又被翻成雨天，会以为按钮坏了。

### 10.5 过渡期间不叠加

```csharp
if (env.IsTransitioning) return;   // 正在过渡时不倒计时
```

否则会把 2.4 秒的平滑过渡打断成两段。

---

## 11. UI 面板：WeatherPanel

### 11.1 为什么做成"尺寸全部传参"

数字孪生（左面板 148 宽、3 列、11 号字）和第一人称（设置弹窗 308 宽、4 列、13 号字）都要这个功能，但可用宽度差一倍。所以 `width / columns / fontSize` 全部由调用方传进来，内部按格子均分。

```csharp
var p = WeatherPanel.Create(parentRt, 16f, y, 148f, 3, 11);
y += p.Height;   // 面板实际高度，调用方用它推进 y
```

### 11.2 布局

```
┌──────────────────────────┐
│ 自动轮换 · 关             │  ← 第 0 行（独立，因为它是"模式"不是天气）
├────────────┬─────────────┤
│   白天      │    夜晚      │  ← 第 1 行（2 等分）
├──────┬──────┬──────┬──────┤
│  晴   │ 多云 │  阴  │  雨  │  ← 天气网格
├──────┼──────┼──────┼──────┤
│ 雷雨  │  雾  │  雪  │      │
└──────┴──────┴──────┴──────┘
```

`cellH = fontSize + 13f`，`Gap = 6f`，所有尺寸从这两个值推导，不写死。

### 11.3 状态刷新

- 订阅 `EnvironmentState.OnChanged`；
- `OnEnable` 时补一次 `Refresh()`（面板被重新显示时可能已经变过天气）；
- `Refresh()` 开头有 `if (timeBtns == null && wBtns == null) return;` —— `AddComponent` 会先触发 `OnEnable`，那时 `Build` 还没跑、按钮数组还是 null，直接返回避免提前创建 `EnvironmentState`。

---

## 12. 四个 Shader

### 12.1 `CampusUI/SnowGround`（地面积雪）

| 属性 | 默认 | 含义 |
|---|---|---|
| `_SnowTex` | white | 覆盖率（A）+ 颗粒噪声（RGB） |
| `_SnowColor` | 0.92, 0.95, 1.0 | 雪色 |
| `_SnowAmount` | 0 | 雪量 0~1 |
| `_SkyColor` | 0.74, 0.82, 0.96 | 天空色（远处偏蓝） |
| `_EdgeFade` | 0.55 | 边缘淡出半径（1.0 = 交给贴图） |
| `_MicroNormal` | 0.35 | 微法线扰动强度 |
| `_AmbientBoost` | 0.32 | 环境提亮下限 |

渲染状态：`Queue=Transparent`、`Blend SrcAlpha OneMinusSrcAlpha`、`ZWrite Off`、`ZTest LEqual`、`Cull Off`。

片元着色器的四步：

```hlsl
// 1. 覆盖率
float cov  = sn.a;                                  // 实心
float edge = 1 - smoothstep(_EdgeFade, 1.0, r);     // 方形边界二次收边
if (cov <= 0.003 || edge <= 0.003) discard;

// 2. 微法线（梯度取噪声 .r，不取 .a —— 覆盖率实心，.a 梯度恒为 0）
float3 N = normalize(normalWS + float3(-ddx(sn.r), 0, -ddy(sn.r)) * _MicroNormal * 2.5);

// 3. 受光：主光 Lambert（含实时阴影）+ SH 环境光 + 提亮下限
float3 lit = mainLight.color * saturate(dot(N, L)) * mainLight.shadowAttenuation
           + SampleSH(N) + _AmbientBoost;
float3 col = _SnowColor.rgb * lit * (0.975 + 0.025 * sn.r);
col = lerp(col, _SkyColor.rgb * (0.6 + 0.4*lit), 0.22);   // 远处偏天空色

// 4. 雾 + alpha
col = MixFog(col, ComputeFogFactor(positionCS.z));
float a = saturate(smoothstep(0.0, 0.22, _SnowAmount) * cov * edge);
```

三个必须的编译指令：

```hlsl
#pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE   // 接收实时阴影
#pragma multi_compile_fog                                                  // 参与雾
```

**没有第一个变体，雪上就不会有角色/树的影子**；`GetMainLight(shadowCoord)` 会退化成无阴影版本，雪面看起来"飘"在地上。

`_AmbientBoost = 0.32` 是"弱光下雪仍然是白的"的关键。纯物理的直射+环境光在阴天会算出灰暗的雪，而真实雪是强反照率材质，视觉上应该发亮。

### 12.2 `CampusUI/WetGround`（积水）

| 属性 | 默认 | 含义 |
|---|---|---|
| `_MaskTex` | black | 水洼遮罩（R 通道） |
| `_WaterColor` | 0.05, 0.07, 0.09 | 水色 |
| `_SkyColor` / `_HorizonColor` | 天顶/地平线色 | 解析天空反射 |
| `_Wetness` | 0 | 湿滑度 |
| `_ReflectStrength` | 1 | 反射强度 |
| `_EnvReflect` | 0.80 | 环境探针权重 |
| `_Gloss` | 0.6 | cubemap mip |
| `_RippleStrength` | 1 | 涟漪强度 |
| `_RippleScale` / `_RippleSpeed` | 1.6 / 1 | 涟漪尺度/速度 |
| `_FresnelPower` | 5 | 菲涅尔指数 |

三个关键实现细节：

1. **`R.y = abs(R.y)`** —— 涟漪扰动可能让反射方向朝下，采到立方图下半球会突然变暗一圈。
2. **`unity_SpecCube0` 亮度兜底** —— 没绑探针且默认反射模式异常时它是纯黑，直接采会得到"黑水"。用亮度阈值 `0.002` 判断并退回解析天空色。
3. **程序化涟漪** —— 3 层不同缩放的分格哈希，每格一个随机中心 + 随机相位，`sin(distance - phase)` 得到向外扩散的环。不占贴图，也看不出网格规律。

alpha 计算：

```hlsl
float a = _Wetness * saturate(0.10 + 0.95 * puddle) * edge;
```

水洼处（`puddle≈1`）几乎全不透明，其余地方只留一层薄薄湿光（`0.10`）。

### 12.3 `CampusUI/CloudDome`（云）

| 属性 | 默认 | 含义 |
|---|---|---|
| `_Noise3D` | - | 3D 噪声体积（A=密度） |
| `_Color` / `_Opacity` | - | 云色/不透明度 |
| `_Radius` | 0.34 | 采样球半径 |
| `_Speed` | 0.02 | 漂移速度 |
| `_HorizonFade` | 0.30 | 地平线淡出 |

核心只有三行：

```hlsl
float3 d2 = RotateY(d, _Time.y * _Speed);   // 旋转视线，而不是平移采样点
float3 p  = d2 * _Radius + 0.5;             // 查询球始终在 [0,1]³ 内
float  c  = tex3D(_Noise3D, p).a;
```

**用旋转而不是平移**：平移迟早会把查询球推出 `[0,1]³`，露出贴图边界；旋转不会。

### 12.4 `CampusUI/SkyDome`（星空）

三平面投影，避免球面 UV 的极点拉伸：

```hlsl
float2 uvX = d.zy / max(abs(d.x), 0.08);
float2 uvY = d.xz / max(abs(d.y), 0.08);
float2 uvZ = d.xy / max(abs(d.z), 0.08);
float3 w = a*a; w /= max(1e-4, w.x+w.y+w.z);   // 按轴向权重混合
```

`max(..., 0.08)` 防止轴附近除零；权重 `a*a` 让轴向采样淡出，掩盖三个面的接缝。

地平线淡出：

```hlsl
float horizon = smoothstep(-_HorizonFade, 0.02, d.y);
```

保证穹顶不会"画到地面下面"。

### 12.5 渲染队列总表

| Queue | 物件 | 说明 |
|---|---|---|
| 2990 | StarDome | 最底层星空 |
| 2992 | WetGround | 积水 |
| 2995 | SnowGroundFar / CloudDome | 远场雪 + 云 |
| 2996 | SnowGround | 近场雪（盖远场、盖积水） |
| 2997 | TreeSnowPiles | 树根雪堆（盖雪盘） |
| 3002 | RainSplash | 水花（最上） |

这个顺序是**视觉正确性的一部分**：雪要盖水、树根雪要盖雪盘、水花要在最上。

---

## 13. 程序化资源：ProceduralAssets

全部懒加载（`get` 里判断缓存），首次访问时生成，之后复用。**没有一个外部文件**。

### 13.1 七张纹理

| 属性 | 尺寸 | 格式 | 内容 | 用途 |
|---|---|---|---|---|
| `RainStreak` | - | RGBA32 | 竖直雨丝条 | 雨粒子贴图 |
| `Snowflake` | - | RGBA32 | 六角雪花 | 雪粒子 + 水花 |
| `CloudNoise` | - | RGBA32 | 2D 云噪声 | 兜底云（3D 不可用时） |
| `StarField` | - | RGBA32 | 稀疏星点 | 星空穹顶 |
| `SnowPatch` | 256² | RGBA32 | **A=实心覆盖率，RGB=颗粒噪声** | 积雪遮罩 |
| `WetMask` | 512² | RGBA32 | R=水洼概率 | 积水遮罩 |
| `CloudVolume` | 48³ | RGBA32 | 3D 云密度 | 云穹顶 |

#### `SnowPatch` 的关键设计

```csharp
float edge = 1f - Mathf.SmoothStep(0.93f, 1f, r);   // 只有最外 7% 淡出
float n = 3 层 Perlin;                              // 噪声只进 RGB
px = new Color32(n*255, n*255, n*255, edge*255);    // A 通道不含噪声
```

**alpha 恒为实心**（除最外圈）。旧版是 `alpha = edge * (0.70 + 0.30*n)`，覆盖率在 0.7~1.0 波动，草地的绿褐色会透过 0~30% 透出来，整片雪看起来"发灰、发虚、一块一块"。噪声改存 RGB 后，Shader 取 `.r` 做微法线颗粒——有质感，但地面颜色不会透出来。

#### `WetMask` 的关键设计

- 4 层八度，基础频率 6（每周期约 6.7m），最细一层约 0.4m —— 水洼边界才有"不规则湿地"感；
- **阈值用 34%~64% 分位数**而不是写死常数：分位数拉伸与噪声的绝对量纲无关，换噪声实现也不会突然变成"全是水"或"一滴没有"；
- 再做一次 `smoothstep`（`k*k*(3-2k)`）让边界是软的。

#### `CloudVolume` 的关键设计

- 48³，4 层八度；
- 三组 2D Perlin 交错（`(fx,fy)`、`(fy,fz)`、`(fz,fx)`）近似各向同性 3D 噪声；
- 分位数拉伸 8%~92%；
- `wrapMode = Clamp`（查询球不碰边界，不需要 Repeat）。

### 13.2 三段音频

| 属性 | 采样率 | 生成方式 |
|---|---|---|
| `RainSfx` | 44100 | 白噪声 + 低通滤波 + 幅度调制 |
| `WindSfx` | 44100 | 低频噪声 + 缓慢包络 |
| `ThunderSfx` | 44100 | 低频轰隆 + 快速衰减 |

都用 `CrossfadeLoop` 处理首尾，避免循环接缝处的"咔哒"声。

### 13.3 材质工厂

| 方法 | 用途 | 关键点 |
|---|---|---|
| `MakeCloudMaterial(tint, name, queue)` | 云穹顶 | 优先 `Resources.Load<Shader>("CloudDome")`，失败退 `Shader.Find`，再失败退 2D 三平面 |
| `MakeSkyDomeMaterial(tex, tint, name, tiling, speed, horizonFade, queue)` | 星空 | 同上，退 `Sprites/Default` |
| `MakeSpriteMaterial(tex, tint, name, queue)` | 雨/雪/水花粒子 | `Sprites/Default` |

**为什么 Shader 要放 `Resources` 目录**：`Shader.Find` 在**打包后**只能找到 "Always Included Shaders" 里的着色器。放 `Resources` 里用 `Resources.Load` 取，才不会被剥离。

> **移植时务必保持**：四个 `.shader` 必须放在某个 `Resources` 目录下（本项目是 `Assets/UI_Systems/Resources/`）。否则编辑器里能跑、打包后材质全变粉。

---

## 14. 移植步骤（照做即可）

### Step 1：拷贝文件

按原目录结构拷贝（**保持 `Resources` 目录名不变**）：

```
你的工程/
└── Assets/
    └── UI_Systems/                        ← 目录名可改，但 Resources 不能改
        ├── Resources/
        │   ├── SnowGround.shader
        │   ├── WetGround.shader
        │   ├── CloudDome.shader
        │   └── SkyDome.shader
        └── Scripts/
            ├── Core/
            │   ├── Models.cs              ← 只需两个枚举（见附录）
            │   └── EnvironmentState.cs
            ├── Environment/
            │   ├── EnvSample.cs
            │   ├── WeatherVisuals.cs
            │   ├── WetGround.cs
            │   ├── SnowOnTrees.cs
            │   ├── WeatherDirector.cs
            │   └── ProceduralAssets.cs
            └── UI/
                └── WeatherPanel.cs
```

如果目标工程已有 `Models.cs`，**不要覆盖**，只把两个枚举贴进去。

### Step 2：确认命名空间

三个命名空间必须自洽：

```
CampusUI.Core         ← EnvironmentState, Weather, TimeOfDay
CampusUI.Environment  ← 其余 5 个环境脚本
CampusUI.UI           ← WeatherPanel
```

要改名就全局替换，注意 `WeatherVisuals.cs` 里有一处**全限定调用**：
`CampusUI.Core.EnvironmentState.FindActiveCamera()`。

### Step 3：确认 URP

- 目标工程必须是 URP（`Assets/Settings/` 下有 URP Asset）；
- `SnowGround.shader` / `WetGround.shader` 的 `#include` 路径依赖 `com.unity.render-pipelines.universal` 包，URP 12+ 均可用；
- 若用 HDRP 或内置管线，见 [2.2](#22-内置管线built-in能不能用)。

### Step 4：场景准备（只有 3 件事）

| 需要 | 检查方法 | 缺失后果 |
|---|---|---|
| 1 个 `Directional Light` | Hierarchy 里找 `Light` 且 Type=Directional | 天空有光但场景全黑（`FindSun` 找不到） |
| 1 个启用的相机 | `Camera.allCameras` 非空 | 雨雪不跟随、雾不按高度衰减 |
| 地形（可选） | 有 `Terrain` 且种了树 | `SnowOnTrees` 自动禁用，不影响其他 |

**不需要**手动摆任何天气物件：`EnvironmentState.Awake()` 会自动创建 `WeatherVisuals`、粒子、云穹顶、星空、雪盘、积水层、树根积雪。

### Step 5：挂载入口

在场景里新建一个空 GameObject（比如 `Environment`），挂上 `EnvironmentState`。

> 也可以完全不挂：`WeatherPanel.Env` 的 getter 里会在找不到时自动 `new GameObject("EnvironmentState")`。但**推荐手动挂**，这样能在 Inspector 里调 `transitionDuration` / `showClouds` / `sfxScale`。

### Step 6：接 UI（可选）

```csharp
// 数字孪生左面板
var p = WeatherPanel.Create(leftPanelRt, 16f, y, 148f, 3, 11);
y += p.Height;

// 第一人称设置弹窗
var p2 = WeatherPanel.Create(settingsRt, 16f, y, 308f, 4, 13);
y += p2.Height;
```

`WeatherPanel` 依赖 `UIStyle.Button` / `UITheme.Bg3` / `UIStyle.Lighten` 等 UI 工具类。若目标工程没有，需要提供等价实现（一个返回 `Button` 的工厂 + 一组颜色常量），或把 `Build()` 里的按钮创建换成你自己的 `Instantiate`。

### Step 7（可选）：天气影响移动

```csharp
// 在移动速度计算处
float mf = (env != null && env.MoveFactor > 0.01f) ? env.MoveFactor : 1f;
velocity = input * speed * mf;
```

### Step 8：验证

进 Play，Console 应无红错。打开天气面板点「雪」，应在 2.4 秒内看到：天空变灰白 → 雾起来 → 雪花飘 → 地面渐白（12 秒积满）。

---

## 15. 参数调优速查表

### 15.1 想改"雪看起来怎么样"

| 想要的效果 | 改哪里 | 建议范围 |
|---|---|---|
| 雪更大/更小 | `EnvSample.Snow.snow` | 0.5 ~ 1.2 |
| 雪积得更快 | `WeatherVisuals.Apply` 的 `dt / 12f` | 6 ~ 20 |
| 雪化得更慢 | 同上的 `dt / 20f` | 20 ~ 40 |
| 雪面更白 | `SnowGround.shader` 的 `_AmbientBoost` | 0.25 ~ 0.45 |
| 雪面颗粒更明显 | `_MicroNormal` | 0.2 ~ 0.6 |
| 雪盖住更高的台阶 | `SnowGroundLiftNear/Far` | 0.30 ~ 0.60 |
| 雪盘覆盖更远 | `SnowGroundSize` / `SnowGroundFarSize` | 90/260 → 120/340 |
| 雪面边缘更柔和 | `ProceduralAssets.SnowPatch` 的 `SmoothStep(0.93f, 1f, r)` | 0.85 ~ 0.95 |
| 树根雪堆更大 | `SnowOnTrees.pileRadius` | 1.5 ~ 5 |

### 15.2 想改"雨/积水"

| 想要的效果 | 改哪里 | 建议范围 |
|---|---|---|
| 雨更大 | `EnvSample.Rain.rain` / `Storm.rain` | 1.0 / 1.6 |
| 雨丝更长 | `BuildRain` 的 `lengthScale` | 1.8 ~ 3.5 |
| 雨滴更密 | `RainRateMax` | 2000 ~ 5000 |
| 地面湿得更快 | `WetGround.WetInSeconds` | 4 ~ 12 |
| 地面干得更慢 | `WetGround.DryInSeconds` | 20 ~ 45 |
| 积水更明显 | `WetGround.shader` 的 `_ReflectStrength` | 0.8 ~ 1.6 |
| 水洼更多/更少 | `ProceduralAssets.WetMask` 的 0.34/0.64 分位 | 0.25/0.55 ~ 0.45/0.75 |
| 涟漪更强 | `_RippleStrength` | 0.5 ~ 2.0 |

### 15.3 想改"天空/云/光"

| 想要的效果 | 改哪里 |
|---|---|
| 整体更亮/更暗 | `EnvSample.*.exposure` |
| 天空更蓝 | `EnvSample.*.skyTint` |
| 太阳更大 | `EnvSample.*.sunDisk` |
| 云更厚 | `EnvSample.*.cloud` |
| 云飘得更快 | `WeatherVisuals.Apply` 的 `(2f + wind*12f)` |
| 过渡更快/更慢 | `EnvironmentState.transitionDuration`（默认 2.4s） |
| 雾更浓 | `EnvSample.*.fogDensity` |
| 夜晚更暗 | `EnvironmentState.Build` 的 `Night` 分支 |

### 15.4 想改"自动轮换"

| 想要的效果 | 改哪里 |
|---|---|
| 换得更频繁 | `WeatherDirector.minInterval / maxInterval`（90/200） |
| 雪出现得更频繁 | `Overcast` 分支的 `Weather.Snow` 权重（0.10） |
| 不要雪 | 从 `Overcast` 的 pool 里删掉 `Weather.Snow` |
| 默认开启自动 | `WeatherDirector.auto = true` |

---

## 16. 已知坑与规避清单

按"会不会让移植直接失败"排序。

### 16.1 会让效果完全不出来的坑

| # | 坑 | 现象 | 规避 |
|---|---|---|---|
| 1 | Shader 没放 `Resources` | 编辑器正常，**打包后全变粉** | 四个 `.shader` 必须在 `Resources` 目录 |
| 2 | 场景没有 Directional Light | 天空亮但场景全黑 | 必须有 1 个 |
| 3 | 用了 `Camera.main` 找相机 | 雨雪跟到错误的相机、雾浓度错 | 用 `EnvironmentState.FindActiveCamera()` |
| 4 | 场景天空盒是 6 Sided 且直接改它 | 昼夜切换天空不变 | 必须运行时 `new Material(Skybox/Procedural)` |
| 5 | 雪盘抬升量太小 | 地砖/台阶戳穿雪面，只在缝里露白 | 有装饰铺装时用 0.30m+ |

### 16.2 会让效果"看起来假"的坑

| # | 坑 | 现象 | 规避 |
|---|---|---|---|
| 6 | 用 `snow` 直接当积雪量 | 雪一停地面立刻干净 | 必须用 `snowAccum`（12s 积 / 20s 化） |
| 7 | 雪 alpha 被噪声调制 | 雪发灰发虚、草地颜色透出 | alpha 实心，噪声只进 RGB |
| 8 | 雪 Shader 没加阴影变体 | 雪上没影子，看着"飘" | `#pragma multi_compile _ _MAIN_LIGHT_SHADOWS ...` |
| 9 | 云用球面 UV | 抬头见放射状条纹 | 用 `CloudDome`（3D 噪声）或 `SkyDome`（三平面） |
| 10 | 云用 2D 三平面 | 天空有整齐格子 | 云必须 3D，星空可以三平面 |
| 11 | 溅射发射盒 `scale` 写错维度 | 出现 26m 高的竖直水柱 | `rotation=(-90,0,0)` 时薄的那维放 **Z** |
| 12 | 每帧调 `DynamicGI.UpdateEnvironment` | 明显掉帧 | 0.2s 节流 |
| 13 | 积水关掉反射探针 | 拿不到建筑倒影 | `reflectionProbeUsage = BlendProbes` |
| 14 | 雪/水 Shader 忘了 `Cull Off` | 从下往上看雪盘消失 | 已内置 |

### 16.3 会让交互出错的坑

| # | 坑 | 现象 | 规避 |
|---|---|---|---|
| 15 | `CreatePrimitive` 的 Collider 没删 | 云穹顶/雪盘挡住玩家射线、挡住交互 | 建完立刻 `Destroy(col)` |
| 16 | 雪盘/积水层参与射线 | `SampleGroundHeight` 打到雪盘自己 | 雪盘无 Collider（本项目就是无 Collider） |
| 17 | 向上射线打到自己胶囊 | 站在户外被判成"室内" | `up.distance > 0.3f` + 跳过玩家碰撞体 |
| 18 | 自动轮换与手动选择同时生效 | 刚选完晴天又变雨 | 手动选择时 `SetAuto(false)` |

---

## 17. 验收清单

移植完成后按顺序过一遍，每项都能在 30 秒内确认。

### 17.1 编译与加载

- [ ] Console 无红错
- [ ] 进 Play 后 Hierarchy 里自动出现 `WeatherVisuals`（含 `RainParticles` / `SnowParticles` / `CloudDome` / `StarDome` / `RainSplash` / `SnowGround` / `SnowGroundFar` / `WetGround` / `TreeSnowPiles`）
- [ ] Console 无 `找不到 CampusUI/xxx 着色器` 警告

### 17.2 七档天气逐个过

| 天气 | 应看到 | 不该看到 |
|---|---|---|
| 晴 | 蓝天、强平行光、无雾、无云 | 灰蒙、雾 |
| 多云 | 天空变灰、云穹顶出现、光变弱 | 雨/雪 |
| 阴 | 明显灰暗、云更厚、薄雾 | 雨丝 |
| 雨 | 雨丝下落、地面渐湿、有水花 | 雨丝穿进室内 |
| 雷雨 | 雨更密、场景周期性闪白、听到雷声 | 只有天空亮、场景不亮 |
| 雾 | 远处明显发白、能见度下降 | 雾把近处也糊住 |
| 雪 | 雪花飘旋、地面 12 秒渐白、树根有雪堆 | 雪只在砖缝、雪面发灰透地 |

### 17.3 昼夜

- [ ] 白天→黄昏：光变暖橙、天空变暗、云变暖
- [ ] 黄昏→夜晚：场景明显变暗、天空偏深蓝、出现星空
- [ ] 夜晚有云时：星星被云盖掉
- [ ] 夜晚雷雨：闪白明显（夜晚对比更强）

### 17.4 过渡

- [ ] 切天气是 2.4 秒平滑变化，不是"啪"地跳
- [ ] 连点同一个天气按钮，不重复播过渡
- [ ] 过渡中切到另一个天气，不会卡住

### 17.5 跟随与遮挡

- [ ] 走动时雨雪跟着相机（不会"跑出雨区"）
- [ ] 走进室内：雪停、积水消失、雨声变小
- [ ] 走出室内：雪/积水恢复（有过渡，不是瞬间）
- [ ] 上到高处（>130m）：粒子消失
- [ ] 雪天地面被雪盖住（砖缝不露白）

### 17.6 性能

- [ ] 晴天静止时 `WetGround` / 雪盘 / 云穹顶 / 星空都是 `SetActive(false)`
- [ ] 静止不移动时，雪盘不重算顶点（`DrapeSnow` 被跳过）
- [ ] 帧率与不开天气系统时差异 < 15%

---

## 附录 A：完整源码

> 以下代码与源工程**逐字节一致**，可直接拷贝。
> 文件头标注了原始路径，`Models.cs` 只截取两个枚举。


### A.1  Models.cs（只取两个枚举）

原始路径：`Assets/UI_Systems/Scripts/Core/Models.cs`（仅以下片段需要）

```csharp
namespace CampusUI.Core
{
    // 时间档位（一级）：白天 / 黄昏 / 夜晚
    public enum TimeOfDay { Day, Dusk, Night }

    // 天气档位（二级）：顺序即面板里的排列顺序，先晴后恶劣
    public enum Weather { Clear, Cloudy, Overcast, Rain, Storm, Fog, Snow }
}
```

### A.2  EnvSample.cs

原始路径：`Assets/UI_Systems/Scripts/Environment/EnvSample.cs`

```csharp
using UnityEngine;
using CampusUI.Core;

namespace CampusUI.Environment
{
    /// <summary>
    /// 一整套「环境外观」的数值快照 —— 既当作天气预设表，也当作过渡插值的载体。
    ///
    /// 为什么用同一个结构体做两件事：
    ///   天气切换要做 2~3 秒的平滑过渡，必须有一个"从哪儿到哪儿"的可插值中间态。
    ///   如果预设和运行态分成两个类型，就得写两套字段 + 一个逐字段搬运的转换函数，
    ///   加一个参数要改三处。现在只有一个结构体 + 一个 Lerp，加参数只改这里。
    ///
    /// 注意：这里的数值都是「白天基准」，夜晚/黄昏的差异由 EnvironmentState.Build() 叠乘。
    /// </summary>
    public struct EnvSample
    {
        // ---- 天空盒（Skybox/Procedural）----
        public Color skyTint;
        public float exposure;        // _Exposure
        public float atmosphere;      // _AtmosphereThickness
        public float sunDisk;         // _SunSize，天空里那个太阳圆盘的大小

        // ---- 主光（平行光）----
        public Color sunColor;
        public float sunIntensity;

        // ---- 环境光 ----
        public float ambientIntensity;

        // ---- 雾（RenderSettings，URP 支持内置线性/指数雾）----
        public float fogDensity;      // 0 = 关雾
        public Color fogColor;

        // ---- 粒子 / 云 ----
        public float rain;            // 0~1.6 雨强
        public float snow;            // 0~1.2 雪强
        public float wind;            // 0~1 风（影响粒子横向漂移与风声）
        public float cloud;           // 0~1 云穹顶不透明度
        public Color cloudColor;

        // ---- 特殊 ----
        public float lightning;       // 0 = 无雷，1 = 雷雨
        public float star;            // 0 = 白天（不显示星空），1 = 夜空满天星

        // =====================================================================
        // 预设表
        // =====================================================================

        public static EnvSample Get(Weather w)
        {
            switch (w)
            {
                case Weather.Cloudy: return Cloudy;
                case Weather.Overcast: return Overcast;
                case Weather.Rain: return Rain;
                case Weather.Storm: return Storm;
                case Weather.Fog: return Fog;
                case Weather.Snow: return Snow;
                default: return Clear;
            }
        }

        public static readonly EnvSample Clear = new EnvSample
        {
            skyTint = new Color(0.45f, 0.62f, 0.92f),
            exposure = 1.25f,
            atmosphere = 0.85f,
            sunDisk = 0.040f,
            sunColor = new Color(1f, 0.96f, 0.88f),
            sunIntensity = 1.25f,
            ambientIntensity = 0.85f,
            fogDensity = 0f,
            fogColor = new Color(0.70f, 0.75f, 0.80f),
            rain = 0f, snow = 0f, wind = 0.08f,
            cloud = 0f,
            cloudColor = new Color(1f, 1f, 1f),
            lightning = 0f,
            star = 0f
        };

        public static readonly EnvSample Cloudy = new EnvSample
        {
            skyTint = new Color(0.55f, 0.66f, 0.82f),
            exposure = 1.05f,
            atmosphere = 1.00f,
            sunDisk = 0.030f,
            sunColor = new Color(1f, 0.95f, 0.88f),
            sunIntensity = 0.85f,
            ambientIntensity = 0.80f,
            fogDensity = 0f,
            fogColor = new Color(0.66f, 0.70f, 0.75f),
            rain = 0f, snow = 0f, wind = 0.22f,
            cloud = 0.28f,
            cloudColor = new Color(0.95f, 0.96f, 0.98f),
            lightning = 0f,
            star = 0f
        };

        public static readonly EnvSample Overcast = new EnvSample
        {
            skyTint = new Color(0.56f, 0.58f, 0.63f),
            exposure = 0.85f,
            atmosphere = 1.10f,
            sunDisk = 0.012f,
            sunColor = new Color(0.86f, 0.88f, 0.92f),
            sunIntensity = 0.42f,
            ambientIntensity = 0.70f,
            fogDensity = 0.0020f,
            fogColor = new Color(0.62f, 0.65f, 0.69f),
            rain = 0f, snow = 0f, wind = 0.32f,
            cloud = 0.55f,
            cloudColor = new Color(0.72f, 0.74f, 0.78f),
            lightning = 0f,
            star = 0f
        };

        public static readonly EnvSample Rain = new EnvSample
        {
            skyTint = new Color(0.42f, 0.47f, 0.55f),
            exposure = 0.70f,
            atmosphere = 1.15f,
            sunDisk = 0.008f,
            sunColor = new Color(0.72f, 0.78f, 0.88f),
            sunIntensity = 0.26f,
            ambientIntensity = 0.60f,
            fogDensity = 0.0055f,
            fogColor = new Color(0.55f, 0.59f, 0.64f),
            rain = 1.0f, snow = 0f, wind = 0.50f,
            cloud = 0.72f,
            cloudColor = new Color(0.55f, 0.58f, 0.63f),
            lightning = 0f,
            star = 0f
        };

        public static readonly EnvSample Storm = new EnvSample
        {
            skyTint = new Color(0.26f, 0.29f, 0.36f),
            exposure = 0.55f,
            atmosphere = 1.20f,
            sunDisk = 0.006f,
            sunColor = new Color(0.58f, 0.62f, 0.74f),
            sunIntensity = 0.16f,
            ambientIntensity = 0.50f,
            fogDensity = 0.0075f,
            fogColor = new Color(0.42f, 0.45f, 0.50f),
            rain = 1.6f, snow = 0f, wind = 0.90f,
            cloud = 0.86f,
            cloudColor = new Color(0.35f, 0.38f, 0.44f),
            lightning = 1f,
            star = 0f
        };

        public static readonly EnvSample Fog = new EnvSample
        {
            skyTint = new Color(0.64f, 0.66f, 0.68f),
            exposure = 0.90f,
            atmosphere = 1.05f,
            sunDisk = 0.010f,
            sunColor = new Color(0.92f, 0.92f, 0.92f),
            sunIntensity = 0.45f,
            ambientIntensity = 0.78f,
            fogDensity = 0.0140f,
            fogColor = new Color(0.74f, 0.76f, 0.78f),
            rain = 0f, snow = 0f, wind = 0.05f,
            cloud = 0.40f,
            cloudColor = new Color(0.80f, 0.82f, 0.84f),
            lightning = 0f,
            star = 0f
        };

        public static readonly EnvSample Snow = new EnvSample
        {
            skyTint = new Color(0.68f, 0.73f, 0.82f),
            exposure = 0.95f,
            atmosphere = 1.10f,
            sunDisk = 0.014f,
            sunColor = new Color(0.95f, 0.97f, 1f),
            sunIntensity = 0.52f,
            ambientIntensity = 0.85f,
            fogDensity = 0.0070f,
            fogColor = new Color(0.79f, 0.83f, 0.88f),
            rain = 0f, snow = 1.0f, wind = 0.20f,
            cloud = 0.62f,
            cloudColor = new Color(0.82f, 0.85f, 0.90f),
            lightning = 0f,
            star = 0f
        };

        // =====================================================================
        // 插值
        // =====================================================================

        public static EnvSample Lerp(EnvSample a, EnvSample b, float t)
        {
            if (t <= 0f) return a;
            if (t >= 1f) return b;

            EnvSample s;
            s.skyTint = Color.Lerp(a.skyTint, b.skyTint, t);
            s.exposure = Mathf.Lerp(a.exposure, b.exposure, t);
            s.atmosphere = Mathf.Lerp(a.atmosphere, b.atmosphere, t);
            s.sunDisk = Mathf.Lerp(a.sunDisk, b.sunDisk, t);
            s.sunColor = Color.Lerp(a.sunColor, b.sunColor, t);
            s.sunIntensity = Mathf.Lerp(a.sunIntensity, b.sunIntensity, t);
            s.ambientIntensity = Mathf.Lerp(a.ambientIntensity, b.ambientIntensity, t);
            s.fogDensity = Mathf.Lerp(a.fogDensity, b.fogDensity, t);
            s.fogColor = Color.Lerp(a.fogColor, b.fogColor, t);
            s.rain = Mathf.Lerp(a.rain, b.rain, t);
            s.snow = Mathf.Lerp(a.snow, b.snow, t);
            s.wind = Mathf.Lerp(a.wind, b.wind, t);
            s.cloud = Mathf.Lerp(a.cloud, b.cloud, t);
            s.cloudColor = Color.Lerp(a.cloudColor, b.cloudColor, t);
            s.lightning = Mathf.Lerp(a.lightning, b.lightning, t);
            s.star = Mathf.Lerp(a.star, b.star, t);
            return s;
        }
    }
}
```

### A.3  EnvironmentState.cs

原始路径：`Assets/UI_Systems/Scripts/Core/EnvironmentState.cs`

```csharp
using System.Collections;
using UnityEngine;
using CampusUI.Environment;

namespace CampusUI.Core
{
    /// <summary>
    /// 全局环境状态机：一级「白天 / 夜晚」，二级「晴 / 多云 / 阴 / 雨 / 雷雨 / 雾 / 雪」。
    ///
    /// 一次切换会同时驱动：天空盒、平行光、环境光、内置雾、雨雪粒子、云穹顶、环境音。
    /// 所有参数在 transitionDuration 内**平滑插值**，不会"啪"地跳变。
    ///
    /// 三个关键实现决定（都是踩过的坑）：
    ///  1) **天空盒必须实例化一份**。场景自带的天空盒是磁盘上的资产，
    ///     直接改 `RenderSettings.skybox` 的 `_SkyTint` 会把 .mat 改脏、甚至写回磁盘。
    ///     这里 `new Material(原材质)` 得到运行期副本，只在内存里变。
    ///     （旧版代码因为"场景已有天空盒就直接 return"，导致昼夜切换时天空**从来没变过**。）
    ///  2) **雾用 RenderSettings.fog**。URP 14 没有内置体积雾，但支持线性/指数内置雾，
    ///     URP/Lit 与 Terrain 着色器都会采样它，代价几乎为零。
    ///  3) **DynamicGI.UpdateEnvironment() 要节流**。它会重算天空盒到 cubemap 的卷积，
    ///     每帧调会明显掉帧，这里 0.2 秒一次，过渡结束时再补一次。
    /// </summary>
    public class EnvironmentState : MonoBehaviour
    {
        public static EnvironmentState Instance;

        public TimeOfDay timeOfDay = TimeOfDay.Day;
        public Weather weather = Weather.Clear;

        [Tooltip("一次天气切换的过渡时长（秒）")]
        [SerializeField] private float transitionDuration = 2.4f;

        [Tooltip("是否显示云穹顶（关掉可以省一次大球的透明绘制）")]
        [SerializeField] private bool showClouds = true;

        [Tooltip("环境音总音量系数")]
        [SerializeField, Range(0f, 1f)] private float sfxScale = 0.55f;

        public event System.Action<TimeOfDay, Weather> OnChanged;

        // ---- 运行期引用 ----
        private Light sun;
        private Material skyMat;
        private Camera cam;
        private WeatherVisuals visuals;
        private AudioSource rainSrc, windSrc, thunderSrc;
        private bool audioReady;

        // ---- 过渡状态 ----
        private EnvSample cur, from, to;
        private float tt = 1f;

        // ---- 闪电 ----
        private float nextBolt = 6f;
        private float flash;
        private Coroutine boltCo;

        // ---- GI 节流 ----
        private float giTimer;
        private bool giDirty;
        private float sunSearchTimer;

        public bool IsTransitioning { get { return tt < 1f; } }

        /// <summary>
        /// 当前天气对**行走手感**的影响系数（1 = 正常，越小越滑）。
        ///
        /// 参考《异环》：雨天轮胎打滑、雪天速度下降且转向迟钝。
        /// 本工程没有载具，改成第一人称的移速与脚步节奏 ——
        /// 这是最便宜也最有效的"天气影响玩法"：玩家不需要看到数值，
        /// 只要走起来感觉"地面有点滑"就够了。
        /// </summary>
        public float MoveFactor { get; private set; } = 1f;

        // =====================================================================
        // 生命周期
        // =====================================================================

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);

            EnsureSky();
            FindSun();
            EnsureVisuals();
            EnsureAudio();

            cur = Build(timeOfDay, weather);
            from = cur;
            to = cur;
            tt = 1f;
            giDirty = true;
            ApplyAll(cur, 0f, true);
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        // =====================================================================
        // 对外 API（保持与旧版一致，调用点不用改）
        // =====================================================================

        public void Set(TimeOfDay t, Weather w)
        {
            bool same = (timeOfDay == t && weather == w);
            timeOfDay = t;
            weather = w;

            from = cur;
            to = Build(t, w);
            tt = same ? 1f : 0f;

            if (transitionDuration <= 0.02f)
            {
                cur = to;
                tt = 1f;
                giDirty = true;
                ApplyAll(cur, 0f, true);
            }

            if (OnChanged != null) OnChanged(t, w);
        }

        public void SetTime(TimeOfDay t) { Set(t, weather); }
        public void SetWeather(Weather w) { Set(timeOfDay, w); }

        /// <summary>当前这一档天气的中文名，UI 直接用。</summary>
        public static string WeatherLabel(Weather w)
        {
            switch (w)
            {
                case Weather.Cloudy: return "多云";
                case Weather.Overcast: return "阴";
                case Weather.Rain: return "雨";
                case Weather.Storm: return "雷雨";
                case Weather.Fog: return "雾";
                case Weather.Snow: return "雪";
                default: return "晴";
            }
        }

        public static string TimeLabel(TimeOfDay t)
        {
            switch (t)
            {
                case TimeOfDay.Night: return "夜晚";
                case TimeOfDay.Dusk: return "黄昏";
                default: return "白天";
            }
        }

        // =====================================================================
        // 每帧
        // =====================================================================

        void Update()
        {
            if (tt < 1f)
            {
                tt += Time.deltaTime / Mathf.Max(0.05f, transitionDuration);
                float e = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(tt));
                cur = EnvSample.Lerp(from, to, e);
                giDirty = true;                       // 过渡期间才需要重算天空环境光
                if (tt >= 1f) cur = to;
            }

            TickLightning(Time.deltaTime);
            ApplyAll(cur, Time.deltaTime, false);
        }

        // =====================================================================
        // 参数合成：天气预设 × 昼夜修正
        // =====================================================================

        private static EnvSample Build(TimeOfDay t, Weather w)
        {
            EnvSample s = EnvSample.Get(w);

            float sunMul = 1f;      // 太阳强度倍率
            float expMul = 1f;      // 天空盒曝光倍率
            float ambMul = 1f;      // 环境光倍率
            float skyMul = 1f;      // 天空 tint 倍率
            float blueBias = 1f;    // 夜间让蓝通道多留一点
            float fogMul = 1f;
            Color sunTint = Color.white;

            switch (t)
            {
                case TimeOfDay.Dusk:
                    sunMul = 0.50f; expMul = 0.78f; ambMul = 0.80f;
                    skyMul = 0.92f; blueBias = 0.90f; fogMul = 0.80f;
                    sunTint = new Color(1f, 0.66f, 0.42f);
                    break;
                case TimeOfDay.Night:
                    sunMul = 0.10f; expMul = 0.34f; ambMul = 0.30f;
                    skyMul = 0.20f; blueBias = 1.70f; fogMul = 0.42f;
                    sunTint = new Color(0.58f, 0.68f, 1f);
                    break;
            }

            s.sunIntensity *= sunMul;
            s.sunColor = new Color(s.sunColor.r * sunTint.r,
                                   s.sunColor.g * sunTint.g,
                                   s.sunColor.b * sunTint.b);
            s.exposure *= expMul;
            s.ambientIntensity *= ambMul;
            s.skyTint = new Color(s.skyTint.r * skyMul,
                                  s.skyTint.g * skyMul,
                                  Mathf.Min(1f, s.skyTint.b * skyMul * blueBias));
            s.fogColor = new Color(s.fogColor.r * fogMul,
                                   s.fogColor.g * fogMul,
                                   s.fogColor.b * fogMul);

            // 夜里天空不该挂着一个大太阳圆盘
            if (t == TimeOfDay.Night) s.sunDisk *= 0.12f;
            else if (t == TimeOfDay.Dusk) s.sunDisk *= 0.7f;

            // 夜里云也要暗下去，否则会浮在夜空上像块白布
            if (t == TimeOfDay.Night)
                s.cloudColor = new Color(s.cloudColor.r * 0.22f, s.cloudColor.g * 0.24f, s.cloudColor.b * 0.34f);
            else if (t == TimeOfDay.Dusk)
                s.cloudColor = new Color(s.cloudColor.r * 0.85f, s.cloudColor.g * 0.72f, s.cloudColor.b * 0.66f);

            // 星空只在夜里出现；云厚的时候由 WeatherVisuals 再按 cloud 压下去
            if (t == TimeOfDay.Night) s.star = 1f;
            else if (t == TimeOfDay.Dusk) s.star = 0.25f;
            else s.star = 0f;

            return s;
        }

        // =====================================================================
        // 应用
        // =====================================================================

        private void ApplyAll(EnvSample s, float dt, bool force)
        {
            EnsureSky();

            // 天气对行走手感的影响：雨天略滑、雪天明显更滑。
            // 放在最前面算，下游（第一人称控制器）随时读到都是本帧的值。
            float mf = 1f;
            if (s.rain > 0.05f) mf -= 0.10f * Mathf.Clamp01(s.rain);
            if (s.snow > 0.05f) mf -= 0.20f * Mathf.Clamp01(s.snow);
            MoveFactor = Mathf.Clamp(mf, 0.65f, 1f);

            if (sun == null)
            {
                // 场景有 69 盏灯，FindObjectsOfType 不便宜 —— 没找到平行光时也别每帧找
                sunSearchTimer -= dt;
                if (sunSearchTimer <= 0f) { sunSearchTimer = 1f; FindSun(); }
            }

            // ---- 天空盒 ----
            if (skyMat != null)
            {
                if (skyMat.HasProperty("_SkyTint")) skyMat.SetColor("_SkyTint", s.skyTint);
                if (skyMat.HasProperty("_Exposure")) skyMat.SetFloat("_Exposure", s.exposure + flash * 0.9f);
                if (skyMat.HasProperty("_AtmosphereThickness")) skyMat.SetFloat("_AtmosphereThickness", s.atmosphere);
                if (skyMat.HasProperty("_SunSize")) skyMat.SetFloat("_SunSize", s.sunDisk);
                if (skyMat.HasProperty("_SunSizeConvergence")) skyMat.SetFloat("_SunSizeConvergence", 8f);
                if (skyMat.HasProperty("_GroundColor"))
                    skyMat.SetColor("_GroundColor", Color.Lerp(s.fogColor, s.skyTint, 0.5f) * 0.8f);
                // 兜底分支用的 6 Sided / Cubemap 天空盒：它们的调色参数叫 _Tint，
                // 默认值 0.5（shader 里乘 2），所以这里再乘 0.55 才是"接近原色"。
                if (skyMat.HasProperty("_Tint")) skyMat.SetColor("_Tint", s.skyTint * 0.55f);
            }

            // ---- 主光 ----
            if (sun != null)
            {
                sun.color = s.sunColor;
                sun.intensity = s.sunIntensity * (1f + flash * 5f);
                sun.enabled = true;
            }

            // ---- 环境光 ----
            RenderSettings.ambientIntensity = Mathf.Clamp(s.ambientIntensity * (1f + flash * 1.8f), 0f, 8f);

            // ---- 雾 ----
            bool wantFog = s.fogDensity > 0.0005f;
            RenderSettings.fog = wantFog;
            if (wantFog)
            {
                RenderSettings.fogMode = FogMode.ExponentialSquared;

                // 雾密度按相机高度衰减。
                // 数字孪生是 200m 左右的高空俯视，同一套密度会把整个校园糊成一片白；
                // 第一人称在地面 2m，才是雾该有的浓淡。这里从 5m 开始线性衰减到 185m 的 30%。
                float h = 2f;
                var c = Cam;
                if (c != null) h = c.transform.position.y;
                float hk = Mathf.Lerp(1f, 0.30f, Mathf.Clamp01((h - 5f) / 180f));

                RenderSettings.fogDensity = s.fogDensity * hk;
                RenderSettings.fogColor = s.fogColor;
            }

            // ---- 粒子 / 云 / 星空 ----
            if (visuals != null)
            {
                float cloud = showClouds ? s.cloud : 0f;
                // 天空色要下发给积水层：没有反射探针时，积水"看到"的就是这个解析天空，
                // 天气一变地面反光的颜色也得跟着变，否则会出现"灰雨天里地面反射蓝天"。
                Color zenith = s.skyTint;
                Color horizon = Color.Lerp(s.fogColor, s.skyTint, 0.35f);
                visuals.Apply(s.rain, s.snow, s.wind, cloud, s.cloudColor, s.star, zenith, horizon, dt);
            }

            // ---- 音频 ----
            if (audioReady)
            {
                if (rainSrc != null) rainSrc.volume = Mathf.Clamp01(s.rain / 1.2f) * sfxScale;
                if (windSrc != null) windSrc.volume = Mathf.Clamp01(s.wind) * sfxScale * 0.7f;
            }

            // ---- 天空环境光重算（节流） ----
            // 只在真正有变化时才算：DynamicGI.UpdateEnvironment 会把天空盒重新渲染进 cubemap，
            // 每帧调会明显掉帧，所以过渡期间 0.2 秒一次，过渡结束后自然停下来。
            giTimer += dt;
            if (force || giTimer >= 0.2f)
            {
                giTimer = 0f;
                if (giDirty)
                {
                    giDirty = false;
                    DynamicGI.UpdateEnvironment();
                }
            }
        }

        // =====================================================================
        // 闪电
        // =====================================================================

        private void TickLightning(float dt)
        {
            if (cur.lightning < 0.05f)
            {
                nextBolt = Random.Range(4f, 9f);
                return;
            }

            nextBolt -= dt;
            if (nextBolt <= 0f && boltCo == null)
            {
                nextBolt = Random.Range(5f, 12f) / Mathf.Max(0.35f, cur.lightning);
                boltCo = StartCoroutine(BoltRoutine());
            }
        }

        // 真实闪电是"两下"：先一记强闪，短暂变暗，再补一记弱的
        private IEnumerator BoltRoutine()
        {
            flash = 1f;
            if (thunderSrc != null) thunderSrc.PlayDelayed(Random.Range(0.3f, 1.2f));

            yield return new WaitForSeconds(0.07f);
            flash = 0.16f;
            yield return new WaitForSeconds(0.09f);
            flash = 0.72f;
            yield return new WaitForSeconds(0.13f);
            flash = 0f;

            boltCo = null;
        }

        // =====================================================================
        // 运行时兜底
        // =====================================================================

        /// <summary>
        /// 保证有一个**运行期私有**的天空盒材质，且它必须能表达 7 档天气。
        ///
        /// 这里刻意**不复制场景自带的天空盒**。实测场景用的是 Skybox/6 Sided
        /// （属性只有 _RightTex / _UpTex / _Exposure / _Rotation / _Tint），
        /// 根本没有 _SkyTint / _AtmosphereThickness / _SunSize / _GroundColor ——
        /// 也就是说旧版代码里那一串 HasProperty 判断全部为 false，天气切换时天空**几乎不变**。
        ///
        /// 换成运行期的 Skybox/Procedural 之后，EnvSample 的每个字段都能对上一个真实参数，
        /// 而且天空色能和雾色、云色保持一致（否则会出现"天是蓝的、雾是灰的"这种割裂）。
        /// 云层由 WeatherVisuals 的云穹顶负责，所以不依赖天空盒贴图里的云。
        /// </summary>
        private void EnsureSky()
        {
            if (skyMat != null && RenderSettings.skybox == skyMat) return;

            var sh = Shader.Find("Skybox/Procedural");
            if (sh != null)
            {
                skyMat = new Material(sh);
                skyMat.name = "RuntimeSkyboxProcedural";
                skyMat.SetFloat("_SunDisk", 2f);              // 2 = 高质量太阳圆盘
                skyMat.SetFloat("_SunSizeConvergence", 8f);
                RenderSettings.skybox = skyMat;
                return;
            }

            // 兜底：找不到程序化天空盒就复制场景的，至少 _Exposure 还能变
            var src = RenderSettings.skybox;
            if (src != null)
            {
                skyMat = new Material(src);
                skyMat.name = "RuntimeSkybox";
                RenderSettings.skybox = skyMat;
                Debug.LogWarning("[EnvironmentState] 找不到 Skybox/Procedural，退回场景天空盒，只有曝光可调。");
                return;
            }

            Debug.LogWarning("[EnvironmentState] 没有任何可用的天空盒，天空相关效果将被跳过。");
        }

        private Camera Cam
        {
            get
            {
                if (cam == null || !cam.isActiveAndEnabled) cam = FindActiveCamera();
                return cam;
            }
        }

        /// <summary>
        /// 找当前真正在用的相机。
        ///
        /// **不能用 `Camera.main`** —— 它只按 "MainCamera" tag 查找，**不检查 enabled**。
        /// 本工程第一人称模式下孪生相机是 `enabled = false` 但 tag 还在，而
        /// FirstPersonController 建的 FPCam 没设 tag，所以 `Camera.main` 会返回那台
        /// 被禁用的孪生相机（200m 高空）。雾密度按 200m 算就会淡掉三倍，
        /// 雨雪也会被跟到高空 —— 两处都靠这个方法取值。
        ///
        /// `Camera.allCameras` 只包含真正启用的相机，是唯一可靠的数据源；
        /// 再排除 `targetTexture != null` 的小地图相机。
        /// </summary>
        public static Camera FindActiveCamera()
        {
            var all = Camera.allCameras;
            if (all == null) return null;

            Camera best = null;
            for (int i = 0; i < all.Length; i++)
            {
                var c = all[i];
                if (c == null) continue;
                if (c.targetTexture != null) continue;
                if (best == null || c.depth > best.depth) best = c;
            }
            return best;
        }

        private void FindSun()
        {
            var lights = FindObjectsOfType<Light>();
            for (int i = 0; i < lights.Length; i++)
            {
                if (lights[i] != null && lights[i].type == LightType.Directional)
                {
                    sun = lights[i];
                    return;
                }
            }
        }

        private void EnsureVisuals()
        {
            if (visuals != null) return;
            var go = new GameObject("WeatherVisuals");
            go.transform.SetParent(transform, false);
            visuals = go.AddComponent<WeatherVisuals>();
            visuals.EnsureBuilt();
        }

        /// <summary>相机换了（数字孪生 ↔ 第一人称）后调用，让雨雪重新跟随新相机。</summary>
        public void RebindCamera()
        {
            if (visuals != null) visuals.RebindCamera();
        }

        private void EnsureAudio()
        {
            if (audioReady) return;
            audioReady = true;

            rainSrc = MakeLoopSource("RainSfx", ProceduralAssets.RainSfx);
            windSrc = MakeLoopSource("WindSfx", ProceduralAssets.WindSfx);

            thunderSrc = gameObject.AddComponent<AudioSource>();
            thunderSrc.name = "ThunderSfx";
            thunderSrc.clip = ProceduralAssets.ThunderSfx;
            thunderSrc.loop = false;
            thunderSrc.playOnAwake = false;
            thunderSrc.spatialBlend = 0f;
            thunderSrc.volume = 0.9f;
        }

        private AudioSource MakeLoopSource(string name, AudioClip clip)
        {
            var src = gameObject.AddComponent<AudioSource>();
            src.name = name;
            src.clip = clip;
            src.loop = true;
            src.playOnAwake = false;
            src.spatialBlend = 0f;
            src.volume = 0f;
            src.Play();
            return src;
        }
    }
}
```

### A.4  WeatherVisuals.cs

原始路径：`Assets/UI_Systems/Scripts/Environment/WeatherVisuals.cs`

```csharp
using UnityEngine;
using UnityEngine.Rendering;

namespace CampusUI.Environment
{
    /// <summary>
    /// 天气的可视部分：雨、雪两套粒子系统 + 一个跟随相机的云穹顶。
    ///
    /// 设计要点：
    ///  1) **整块跟着相机走**。雨雪只在玩家周围 60×60 米的盒子里发射，
    ///     跟走以后无论漫游到校园哪个角落都一直在下雨，也不用铺满整个校园。
    ///  2) **粒子在 World 空间模拟**。跟着相机移动的发射器如果同时用 Local 空间，
    ///     已经落下的雨滴会被"拖着走"，看起来像雨在追人。
    ///  3) **云穹顶用 Sprites/Default**。它是 URP 下最稳的透明双面无光照着色器，
    ///     不需要配 _Surface/_Blend/_ZWrite 那一堆材质关键字，避免"配错就变实心白球"。
    ///
    /// 已知取舍：雨雪不做世界碰撞（穿屋顶）。开碰撞要跑 World 碰撞查询，
    /// 几千个粒子每帧一次，代价远大于收益；室内看到下雨是这类轻量方案的通病。
    /// </summary>
    public class WeatherVisuals : MonoBehaviour
    {
        private const float AreaSize = 110f;     // 发射盒水平尺寸（运行时按相机高度再放大）
        private const float EmitHeight = 16f;    // 发射高度（相机上方）
        private const float DomeRadius = 380f;   // 云穹顶半径，必须小于相机 farClip(2000)

        private const float RainRateMax = 3200f; // rain = 1 时每秒发射数
        private const float SnowRateMax = 520f;  // snow = 1 时每秒发射数

        // 落地反馈
        private const float SplashArea = 26f;      // 雨滴溅射覆盖范围（相机脚下）
        private const float SplashRateMax = 1300f; // rain = 1 时每秒水花数
        private const float SnowGroundSize = 90f;   // 近场地积雪盘边长（米）：覆盖第一人称脚下近场，高细分、贴地形、带雪堆起伏
        private const float SnowGroundFarSize = 260f;  // 远场白兜底盘边长（米）：半径 130m，覆盖近场盘到雪天雾起(≈120m)之间的环带；远边被雾完全淡出
        // 雪面抬离"射线采到的地面"的高度。
        // 关键修复（"雪看着很虚、只积在砖缝里"）：脚下庭院/广场是 SchoolBuilding.FBX 里的铺装网格，
        // 它高出地形、且没有碰撞体，所以向下射线只采到"地形"而非"可见铺装面"，
        // 雪面停在 地形+0.10，低于可见砖面 → ZTest LEqual 让砖面戳穿雪面，只剩砖缝凹槽露出白。
        // 把抬升量提到能盖过铺装凸起（庭院/砖面通常高出地形 0~0.25m），雪才能整片盖住地面。
        // 若改模型后砖面仍戳穿，把下面两个数调大即可（单位：米）。远场略高，因为远处地形跟随误差更大。
        private const float SnowGroundLiftNear = 0.30f;
        private const float SnowGroundLiftFar = 0.35f;

        private ParticleSystem rainPs, snowPs;
        private ParticleSystem splashPs;
        private Transform splashRoot;
        private Transform snowGround;
        private Material snowGroundMat;

        // 积雪地形跟随：克隆内建平面网格后逐顶点向下射线，让雪盖住抬高的花坛/台阶。
        private Mesh snowMesh;
        private Vector3[] snowBaseVerts;   // 平面原始顶点（只取 x/z 做世界映射，y 恒为 0）
        private Vector3[] snowWorkVerts;   // 工作副本，逐帧改写 y 做地形位移
        private Vector3 lastDrapeCam = new Vector3(float.MaxValue, 0f, 0f);
        private bool snowDrapeDirty = true;
        private float snowAmount;          // 当前积雪量（0~1，已含 altFade/indoorFade），驱动雪堆高度
        private float lastDrapeAmount = -1f;

        // 远场白兜底雪盘（消除地平线方向裸地形缺口）
        private Transform snowGroundFar;
        private Material snowGroundFarMat;
        private Mesh snowMeshFar;
        private Vector3[] snowBaseVertsFar;
        private Vector3[] snowWorkVertsFar;
        private Vector3 lastDrapeCamFar = new Vector3(float.MaxValue, 0f, 0f);
        private bool snowDrapeDirtyFar = true;
        private float lastDrapeAmountFar = -1f;
        private bool snowGroundFarOn;
        private float lastSnowGroundFarA = -1f;
        private WetGround wet;
        private Transform cloudDome;
        private Material cloudMat;
        private Transform starDome;
        private Material starMat;
        private Camera cam;
        private bool built;

        private float lastRain = -1f, lastSnow = -1f, lastCloud = -1f, lastStarA = -1f;
        private float lastArea = -1f;
        private float lastSplashRain = -1f, lastSnowGroundA = -1f;
        private float groundY;
        private float groundProbeTimer;
        private bool domeOn;
        private bool starOn;
        private bool snowGroundOn;

        // 积雪累积量：0~1。天气里的 snow 是"此刻下多大"，这个是"地上积了多少"。
        // 两者必须分开，否则雪一停地面积雪会瞬间消失（《异环》是渐进积雪 + 更慢的化雪）。
        private float snowAccum;

        // 室内外：1 = 户外，0 = 室内。头顶有遮挡时把雨雪压下去、关掉积水与积雪层。
        private float indoorFade = 1f;
        private bool indoor;

        // 相机高度淡出系数：1 = 完全显示雨雪，0 = 高空俯视时关掉
        private float altFade = 1f;

        // =====================================================================
        // 构建
        // =====================================================================

        public void EnsureBuilt()
        {
            if (built) return;
            built = true;

            rainPs = BuildRain();
            snowPs = BuildSnow();
            BuildCloudDome();
            BuildStarDome();
            splashPs = BuildSplash();
            BuildSnowGround();

            // 积水层独立成一个组件：它自带一套湿滑度累积逻辑，和粒子系统没有耦合
            wet = gameObject.AddComponent<WetGround>();
            wet.Build();
        }

        private ParticleSystem BuildRain()
        {
            var go = new GameObject("RainParticles");
            go.transform.SetParent(transform, false);
            go.transform.localPosition = new Vector3(0f, EmitHeight, 0f);

            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = ps.main;
            main.loop = true;
            main.prewarm = true;
            main.playOnAwake = true;
            main.duration = 5f;
            main.startLifetime = new ParticleSystem.MinMaxCurve(1.4f, 1.9f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0f, 0.4f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.035f, 0.06f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, 6.2831853f);
            main.startColor = new Color(0.72f, 0.82f, 0.96f, 0.55f);
            main.gravityModifier = 1.3f;                  // 靠重力加速，比固定速度自然
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 9000;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;

            var em = ps.emission;
            em.enabled = true;
            em.rateOverTime = 0f;                          // 由 Apply() 控制

            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(AreaSize, 0.5f, AreaSize);
            shape.position = Vector3.zero;
            shape.rotation = Vector3.zero;

            // 生命周期两端淡入淡出，避免雨丝突然出现/消失
            var col = ps.colorOverLifetime;
            col.enabled = true;
            col.color = new ParticleSystem.MinMaxGradient(FadeGradient());

            var vel = ps.velocityOverLifetime;
            vel.enabled = true;
            vel.space = ParticleSystemSimulationSpace.World;
            vel.x = new ParticleSystem.MinMaxCurve(0f);
            vel.y = new ParticleSystem.MinMaxCurve(0f);
            vel.z = new ParticleSystem.MinMaxCurve(0f);

            var pr = go.GetComponent<ParticleSystemRenderer>();
            if (pr != null)
            {
                // 注意：枚举值叫 Stretch，不是 StretchedBillboard（后者是 Inspector 里的显示名）
                pr.renderMode = ParticleSystemRenderMode.Stretch;
                pr.velocityScale = 0.055f;                 // 拉成雨丝
                pr.lengthScale = 2.4f;
                pr.minParticleSize = 0f;
                pr.maxParticleSize = 0.6f;
                pr.shadowCastingMode = ShadowCastingMode.Off;
                pr.receiveShadows = false;
                var m = ProceduralAssets.MakeSpriteMaterial(ProceduralAssets.RainStreak, Color.white, "ProcRainMat");
                if (m != null) pr.sharedMaterial = m;
            }

            ps.Play();
            return ps;
        }

        private ParticleSystem BuildSnow()
        {
            var go = new GameObject("SnowParticles");
            go.transform.SetParent(transform, false);
            go.transform.localPosition = new Vector3(0f, EmitHeight, 0f);

            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = ps.main;
            main.loop = true;
            main.prewarm = true;
            main.playOnAwake = true;
            main.duration = 5f;
            main.startLifetime = new ParticleSystem.MinMaxCurve(8f, 12f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.3f, 0.8f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.07f, 0.16f);
            main.startColor = new Color(1f, 1f, 1f, 0.9f);
            main.gravityModifier = 0.035f;                 // 几乎不加速，慢慢飘
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 6000;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;

            var em = ps.emission;
            em.enabled = true;
            em.rateOverTime = 0f;

            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(AreaSize, 0.5f, AreaSize);
            shape.position = Vector3.zero;
            shape.rotation = Vector3.zero;

            // 噪声扰动：雪花左右打旋，是"雪"和"白点雨"最大的观感差别
            var noise = ps.noise;
            noise.enabled = true;
            noise.strength = 0.55f;
            noise.frequency = 0.35f;
            noise.scrollSpeed = 0.35f;
            noise.damping = true;
            noise.octaveCount = 2;

            var col = ps.colorOverLifetime;
            col.enabled = true;
            col.color = new ParticleSystem.MinMaxGradient(FadeGradient());

            var vel = ps.velocityOverLifetime;
            vel.enabled = true;
            vel.space = ParticleSystemSimulationSpace.World;
            vel.x = new ParticleSystem.MinMaxCurve(0f);
            vel.y = new ParticleSystem.MinMaxCurve(0f);
            vel.z = new ParticleSystem.MinMaxCurve(0f);

            var pr = go.GetComponent<ParticleSystemRenderer>();
            if (pr != null)
            {
                pr.renderMode = ParticleSystemRenderMode.Billboard;
                pr.minParticleSize = 0f;
                pr.maxParticleSize = 1f;
                pr.shadowCastingMode = ShadowCastingMode.Off;
                pr.receiveShadows = false;
                var m = ProceduralAssets.MakeSpriteMaterial(ProceduralAssets.Snowflake, Color.white, "ProcSnowMat");
                if (m != null) pr.sharedMaterial = m;
            }

            ps.Play();
            return ps;
        }

        private static Gradient FadeGradient()
        {
            var g = new Gradient();
            g.SetKeys(
                new[]
                {
                    new GradientColorKey(Color.white, 0f),
                    new GradientColorKey(Color.white, 1f)
                },
                new[]
                {
                    new GradientAlphaKey(0f, 0f),
                    new GradientAlphaKey(1f, 0.12f),
                    new GradientAlphaKey(1f, 0.82f),
                    new GradientAlphaKey(0f, 1f)
                });
            return g;
        }

        private void BuildCloudDome()
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "CloudDome";

            // CreatePrimitive 会附带 MeshCollider，纯视觉物件不需要
            var col = go.GetComponent<Collider>();
            if (col != null) Destroy(col);

            go.transform.SetParent(transform, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localScale = Vector3.one * (DomeRadius * 2f);   // 原始球直径 = 1

            cloudMat = ProceduralAssets.MakeCloudMaterial(
                new Color(1f, 1f, 1f, 0f), "ProcCloudDome", 2995);

            var mr = go.GetComponent<MeshRenderer>();
            if (mr != null)
            {
                if (cloudMat != null) mr.sharedMaterial = cloudMat;
                mr.shadowCastingMode = ShadowCastingMode.Off;
                mr.receiveShadows = false;
                mr.lightProbeUsage = LightProbeUsage.Off;
                mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
            }

            cloudDome = go.transform;
            go.SetActive(false);
            domeOn = false;
        }

        /// <summary>
        /// 星空穹顶。比云穹顶略大一点，避免两层球面在远处 z-fighting。
        /// 星星固定在世界方向（穹顶只跟相机位置、不跟旋转），所以抬头看星空是稳的。
        /// </summary>
        private void BuildStarDome()
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "StarDome";

            var col = go.GetComponent<Collider>();
            if (col != null) Destroy(col);

            go.transform.SetParent(transform, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localScale = Vector3.one * (DomeRadius * 2.1f);

            starMat = ProceduralAssets.MakeSkyDomeMaterial(
                ProceduralAssets.StarField, new Color(1f, 1f, 1f, 0f), "ProcStarDome",
                0.55f, 0f, 0.10f, 2990);

            var mr = go.GetComponent<MeshRenderer>();
            if (mr != null)
            {
                if (starMat != null) mr.sharedMaterial = starMat;
                mr.shadowCastingMode = ShadowCastingMode.Off;
                mr.receiveShadows = false;
                mr.lightProbeUsage = LightProbeUsage.Off;
                mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
            }

            starDome = go.transform;
            go.SetActive(false);
            starOn = false;
        }

        /// <summary>
        /// 雨滴落地溅射。用一块贴在相机脚下的水平薄发射盒，粒子朝上抛出再落下。
        ///
        /// 为什么不做逐粒子碰撞：给几千个雨滴开 Collision 模块 + 子发射器，
        /// 每帧要做几千次碰撞查询，代价远大于收益，而且场景里大量网格没有碰撞体。
        /// 标准做法就是只做相机周围十几米，远处交给雾和整体色调 —— 肉眼也分辨不出。
        /// </summary>
        private ParticleSystem BuildSplash()
        {
            var go = new GameObject("RainSplash");
            go.transform.SetParent(transform, false);
            go.transform.localPosition = new Vector3(0f, -1.6f, 0f);

            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = ps.main;
            main.loop = true;
            main.prewarm = true;
            main.playOnAwake = true;
            main.duration = 2f;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.22f, 0.42f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(1.1f, 2.3f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.025f, 0.06f);
            main.startColor = new Color(0.80f, 0.88f, 0.98f, 0.50f);
            main.gravityModifier = 1.15f;                  // 抛起来再落回去
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 1600;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;

            var em = ps.emission;
            em.enabled = true;
            em.rateOverTime = 0f;

            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Box;
            // 注意：shape.rotation = (-90,0,0) 绕 X 轴把"局部 +Z → 世界 +Y"，
            // 所以粒子沿世界 +Y 向上溅起（水花"溅起再落下"）。
            // 旋转后各维映射为：局部 X → 世界 X（水平），局部 Y → 世界 Z（水平），
            // 局部 Z → 世界 Y（竖直）。因此"薄"的那一维必须放在 Z 上，
            // 否则 26m 高的 Z 维会被立起来变成一根 26m 高的竖直发射柱（之前就是这么崩的）。
            // 修正：X、Y 给 SplashArea（落地铺开 26×26），Z 给 0.05（贴地薄薄一层）。
            shape.scale = new Vector3(SplashArea, SplashArea, 0.05f);
            shape.rotation = new Vector3(-90f, 0f, 0f);

            var col = ps.colorOverLifetime;
            col.enabled = true;
            col.color = new ParticleSystem.MinMaxGradient(FadeGradient());

            var pr = go.GetComponent<ParticleSystemRenderer>();
            if (pr != null)
            {
                pr.renderMode = ParticleSystemRenderMode.Billboard;
                pr.minParticleSize = 0f;
                pr.maxParticleSize = 0.4f;
                pr.shadowCastingMode = ShadowCastingMode.Off;
                pr.receiveShadows = false;
                var m = ProceduralAssets.MakeSpriteMaterial(
                    ProceduralAssets.Snowflake, Color.white, "ProcSplashMat", 3002);
                if (m != null) pr.sharedMaterial = m;
            }

            ps.Play();
            splashRoot = go.transform;
            return ps;
        }

        /// <summary>
        /// 地面积雪：一块贴地的平面，贴图是"噪声 × 径向淡出"，所以看不到方形边界。
        /// 高度每 0.15 秒用一条向下射线贴一次地面（UpdateGround → ProbeGround）。
        ///
        /// 与旧版的区别：
        ///  · 材质换成 CampusUI/SnowGround（受主光 + 球谐环境光），不再是 Sprites/Default 的灰绿平涂；
        ///  · 平面网格被克隆出来，逐顶点向下射线做"地形跟随"（DrapeSnow），
        ///    让雪能盖住抬高的花坛/台阶，而不是在抬高处露出硬边、底下苔藓钻出来。
        /// </summary>
        private void BuildSnowGround()
        {
            // 不用 CreatePrimitive(Plane)：内建平面只有 10 段，铺到 90m 时每格 9m，
            // 地形跟随和雪堆起伏都会变成大台阶。这里手动建一块高细分单位平面网格。
            var go = new GameObject("SnowGround");
            var mf = go.AddComponent<MeshFilter>();
            var mr = go.AddComponent<MeshRenderer>();

            snowMesh = BuildSnowPlaneMesh(56);
            mf.sharedMesh = snowMesh;
            snowBaseVerts = (Vector3[])snowMesh.vertices.Clone();
            snowWorkVerts = (Vector3[])snowMesh.vertices.Clone();

            go.transform.SetParent(transform, false);
            go.transform.localPosition = new Vector3(0f, -1.6f, 0f);
            // 单位网格直接缩放 SnowGroundSize 米（x/z 方向）。
            go.transform.localScale = new Vector3(SnowGroundSize, 1f, SnowGroundSize);

            var sh = Shader.Find("CampusUI/SnowGround");
            if (sh == null)
            {
                Debug.LogWarning("[WeatherVisuals] 找不到 CampusUI/SnowGround 着色器，地面积雪效果不可用。");
                snowGroundMat = null;
            }
            else
            {
                snowGroundMat = new Material(sh);
                snowGroundMat.name = "ProcSnowGround";
                // 注意：不能用 mat.mainTexture —— 它只认 _MainTex，本着色器的贴图叫 _SnowTex。
                snowGroundMat.SetTexture(IdSnowTex, ProceduralAssets.SnowPatch);
                snowGroundMat.SetColor(IdSnowColor, new Color(0.92f, 0.95f, 1.0f, 1f));
                snowGroundMat.SetColor(IdSkyColor, new Color(0.74f, 0.82f, 0.96f, 1f));
                snowGroundMat.SetFloat(IdSnowAmount, 0f);
                snowGroundMat.SetFloat(IdEdgeFade, 1.0f);   // 环形边界交给 SnowPatch 贴图的径向淡出，这里不再二次裁剪
                snowGroundMat.renderQueue = 2996;   // 必须在积水层 2992 之后，积雪才能盖住积水
            }

            if (mr != null)
            {
                if (snowGroundMat != null) mr.sharedMaterial = snowGroundMat;
                mr.shadowCastingMode = ShadowCastingMode.Off;
                // 关键：雪面要接收主光阴影（角色/树的影子落在雪上），必须开 receiveShadows。
                mr.receiveShadows = true;
                mr.lightProbeUsage = LightProbeUsage.Off;
                mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
            }

            snowGround = go.transform;
            go.SetActive(false);
            snowGroundOn = false;
            snowDrapeDirty = true;

            BuildSnowFar();   // 远场白兜底：覆盖近场盘到雪天雾起之间的环带，消除地平线裸地形缺口
            EnsureTreeSnow(); // 自动挂载树根积雪组件(《异环》"树根被雪埋"近似)
        }

        /// <summary>
        /// 自动挂载树根积雪组件：在 WeatherVisuals 下生成一个子物体并挂上 SnowOnTrees，免去手动配置。
        /// 若场景里已经存在 SnowOnTrees 子物体(用户手动挂的)则跳过。
        /// </summary>
        private void EnsureTreeSnow()
        {
            if (GetComponentInChildren<SnowOnTrees>() != null) return;
            var tgo = new GameObject("TreeSnowPiles");
            tgo.transform.SetParent(transform, false);
            tgo.AddComponent<SnowOnTrees>();
        }

        /// <summary>
        /// 远场积雪盘（白兜底）：260m 级大圆盘，跟随相机，低分段、同样射线贴地并接收阴影 + 雾。
        /// 近场 SnowGround 只覆盖脚下 90m，雪天雾在 ≈120m 起；两者之间的环带若没有雪盘就会露出裸地形，
        /// 地平线方向看过去有一圈"没下雪"的地。远场盘把这个环带补上，且其远边被雪天雾完全淡出，看不到边界。
        /// 与近场盘共用 CampusUI/SnowGround 着色器（受主光 + 球谐 + 接收阴影 + 融雾），renderQueue 2995 低于近场 2996，
        /// 重叠区由近场盖住，远场只在 90~130m 环带可见。
        /// </summary>
        private void BuildSnowFar()
        {
            var go = new GameObject("SnowGroundFar");
            var mf = go.AddComponent<MeshFilter>();
            var mr = go.AddComponent<MeshRenderer>();

            snowMeshFar = BuildSnowPlaneMesh(40);   // 远场被雪天雾遮挡，分段可低（41×41≈1681 顶点）；比 32 段更细，减少雪面与地形穿插产生的块面
            mf.sharedMesh = snowMeshFar;
            snowBaseVertsFar = (Vector3[])snowMeshFar.vertices.Clone();
            snowWorkVertsFar = (Vector3[])snowMeshFar.vertices.Clone();

            go.transform.SetParent(transform, false);
            go.transform.localPosition = new Vector3(0f, -1.6f, 0f);
            go.transform.localScale = new Vector3(SnowGroundFarSize, 1f, SnowGroundFarSize);

            var sh = Shader.Find("CampusUI/SnowGround");
            if (sh == null)
            {
                Debug.LogWarning("[WeatherVisuals] 找不到 CampusUI/SnowGround 着色器，远场积雪不可用。");
                snowGroundFarMat = null;
            }
            else
            {
                snowGroundFarMat = new Material(sh);
                snowGroundFarMat.name = "ProcSnowGroundFar";
                // 注意：本着色器贴图叫 _SnowTex，不是 _MainTex。
                snowGroundFarMat.SetTexture(IdSnowTex, ProceduralAssets.SnowPatch);
                snowGroundFarMat.SetColor(IdSnowColor, new Color(0.92f, 0.95f, 1.0f, 1f));
                snowGroundFarMat.SetColor(IdSkyColor, new Color(0.74f, 0.82f, 0.96f, 1f));
                snowGroundFarMat.SetFloat(IdSnowAmount, 0f);
                snowGroundFarMat.SetFloat(IdEdgeFade, 1.0f);   // 环形边界交给 SnowPatch 贴图径向淡出
                snowGroundFarMat.renderQueue = 2995;   // 近场 2996 之下：重叠区近场盖远场
            }

            if (mr != null)
            {
                if (snowGroundFarMat != null) mr.sharedMaterial = snowGroundFarMat;
                mr.shadowCastingMode = ShadowCastingMode.Off;
                // 远场同样要接收主光阴影（树/建筑影子落在远场雪上）
                mr.receiveShadows = true;
                mr.lightProbeUsage = LightProbeUsage.Off;
                mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
            }

            snowGroundFar = go.transform;
            go.SetActive(false);
        }

        // =====================================================================
        // 应用
        // =====================================================================

        /// <summary>由 EnvironmentState 每帧调用；参数都是已经插值好的当前值。</summary>
        public void Apply(float rain, float snow, float wind, float cloud, Color cloudColor, float star,
                          Color skyColor, Color horizonColor, float dt)
        {
            EnsureBuilt();

            // ---- 室内外过渡 ----
            indoorFade = Mathf.MoveTowards(indoorFade, indoor ? 0f : 1f, dt * 3f);

            // ---- 积雪累积 ----
            // 这里的 snow 是"此刻下多大"，snowAccum 是"地上积了多少"，两者必须分开。
            // 直接拿 snow 当积雪量的话，雪一停地面立刻变干净（《异环》是渐进积雪 + 更慢的化雪）。
            if (snow > 0.02f)
                snowAccum = Mathf.MoveTowards(snowAccum, Mathf.Clamp01(snow), dt / 12f);   // 积：约 12 秒
            else
                snowAccum = Mathf.MoveTowards(snowAccum, 0f, dt / 20f);                    // 化：约 20 秒

            // 高空俯视时把粒子发射率按 altFade 压掉：从 200m 高空看雨丝物理上就看不见，
            // 硬画出来只会是相机正下方孤零零的一小团。
            // 室内再乘 shelter：雨雪不该穿过天花板，但也不能全关 —— 站在门口时窗外明明还在下。
            float shelter = Mathf.Lerp(0.25f, 1f, indoorFade);
            float effRain = rain * altFade * shelter;
            float effSnow = snow * altFade * shelter;

            if (rainPs != null && !Mathf.Approximately(effRain, lastRain))
            {
                lastRain = effRain;
                var em = rainPs.emission;
                em.rateOverTime = Mathf.Max(0f, effRain) * RainRateMax;

                var vel = rainPs.velocityOverLifetime;
                float lateral = wind * 5.5f;
                vel.x = new ParticleSystem.MinMaxCurve(lateral);
                vel.z = new ParticleSystem.MinMaxCurve(wind * 1.8f);
            }

            if (snowPs != null && !Mathf.Approximately(effSnow, lastSnow))
            {
                lastSnow = effSnow;
                var em = snowPs.emission;
                em.rateOverTime = Mathf.Max(0f, effSnow) * SnowRateMax;

                var vel = snowPs.velocityOverLifetime;
                vel.x = new ParticleSystem.MinMaxCurve(wind * 2.2f);
                vel.z = new ParticleSystem.MinMaxCurve(wind * 0.8f);

                var noise = snowPs.noise;
                noise.strength = 0.35f + wind * 0.7f;
            }

            if (cloudDome != null && !Mathf.Approximately(cloud, lastCloud))
            {
                lastCloud = cloud;
                bool on = cloud > 0.005f;
                if (on != domeOn)
                {
                    cloudDome.gameObject.SetActive(on);
                    domeOn = on;
                }
                SetDome(cloudMat, cloudColor, cloud);
            }

            // 云随风缓慢飘移
            if (domeOn && cloudDome != null)
                cloudDome.Rotate(0f, (2f + wind * 12f) * Time.deltaTime, 0f, Space.Self);

            // ---- 星空 ----
            // 云越厚能看到的星越少：阴天/雨天/雪天自动把星星盖掉，晴天夜里才满天星。
            if (starDome != null)
            {
                float a = Mathf.Clamp01(star) * (1f - Mathf.Clamp01(cloud) * 0.92f);
                bool on = a > 0.01f;
                if (on != starOn)
                {
                    starDome.gameObject.SetActive(on);
                    starOn = on;
                }
                if (!Mathf.Approximately(a, lastStarA))
                {
                    lastStarA = a;
                    SetDome(starMat, Color.white, a);
                }
            }

            // ---- 雨的落地溅射 ----
            // 室内不溅：水花只应该出现在露天地面。这里直接乘 indoorFade 而不是 shelter，
            // 因为室内还要留一点雨声/雨丝的底噪，但不该有"屋里地上冒水花"。
            float splashRate = Mathf.Max(0f, rain) * altFade * indoorFade * SplashRateMax;
            if (splashPs != null && !Mathf.Approximately(splashRate, lastSplashRain))
            {
                lastSplashRain = splashRate;
                var em = splashPs.emission;
                em.rateOverTime = splashRate;
            }

            // ---- 地面积雪（近场精细盘 + 远场白兜底盘，共同覆盖到地平线方向）----
            {
                // 用 snowAccum 而不是 snow：积雪是有惯性的
                float a = snowAccum * altFade * indoorFade;
                snowAmount = a;
                Shader.SetGlobalFloat(IdGlobalSnow, a);   // 全局雪量，留给后续本地化 Lit/TerrainLit 积雪使用
                bool on = a > 0.01f;

                if (snowGround != null)
                {
                    if (on != snowGroundOn)
                    {
                        snowGround.gameObject.SetActive(on);
                        snowGroundOn = on;
                        if (on) snowDrapeDirty = true;   // 刚开雪时强制重铺一遍地形
                    }
                    if (on && !Mathf.Approximately(a, lastSnowGroundA))
                    {
                        lastSnowGroundA = a;
                        if (snowGroundMat != null) snowGroundMat.SetFloat(IdSnowAmount, a);
                    }
                }

                // 远场白兜底盘：与近场共用积雪量，覆盖近场盘到雪天雾起之间的环带
                if (snowGroundFar != null)
                {
                    if (on != snowGroundFarOn)
                    {
                        snowGroundFar.gameObject.SetActive(on);
                        snowGroundFarOn = on;
                        if (on) snowDrapeDirtyFar = true;
                    }
                    if (on && !Mathf.Approximately(a, lastSnowGroundFarA))
                    {
                        lastSnowGroundFarA = a;
                        if (snowGroundFarMat != null) snowGroundFarMat.SetFloat(IdSnowAmount, a);
                    }
                }
            }

            // ---- 雨天积水 ----
            if (wet != null) wet.Apply(rain, dt, skyColor, horizonColor, indoor);
        }

        /// <summary>
        /// 设置穹顶材质的不透明度，同时兼容 SkyDome 着色器与 Sprites/Default 兜底。
        /// SkyDome 把颜色(_Color)和不透明度(_Opacity)分开，避免 alpha 同时参与混合与着色。
        /// </summary>
        // 积雪着色器（CampusUI/SnowGround）的属性 ID
        private static readonly int IdSnowTex = Shader.PropertyToID("_SnowTex");
        private static readonly int IdSnowColor = Shader.PropertyToID("_SnowColor");
        private static readonly int IdSkyColor = Shader.PropertyToID("_SkyColor");
        private static readonly int IdSnowAmount = Shader.PropertyToID("_SnowAmount");
        private static readonly int IdEdgeFade = Shader.PropertyToID("_EdgeFade");
        private static readonly int IdGlobalSnow = Shader.PropertyToID("_GlobalSnow");   // 全局雪量；后续本地化 Lit/TerrainLit 积雪时直接读取

        private static void SetDome(Material m, Color rgb, float opacity)
        {
            if (m == null) return;
            opacity = Mathf.Clamp01(opacity);

            if (m.HasProperty("_Opacity"))
            {
                m.SetColor("_Color", new Color(rgb.r, rgb.g, rgb.b, 1f));
                m.SetFloat("_Opacity", opacity);
            }
            else
            {
                m.color = new Color(rgb.r, rgb.g, rgb.b, opacity);
            }
        }

        // 整块跟着相机走（只跟位置，不跟旋转，否则云会跟着视角转）
        private void LateUpdate()
        {
            if (cam == null || !cam.isActiveAndEnabled)
            {
                cam = CampusUI.Core.EnvironmentState.FindActiveCamera();
                if (cam == null) return;
            }

            transform.position = cam.transform.position;
            UpdateRange(cam.transform.position.y);
            UpdateGround(cam.transform.position);
        }

        /// <summary>
        /// 让溅射层和积雪层贴在地面上。
        /// 射线每 0.15 秒打一次就够 —— 玩家走动时地面高度变化是连续的，
        /// 每帧打射线纯属浪费（场景里有 6000+ 碰撞体）。
        /// </summary>
        private void UpdateGround(Vector3 camPos)
        {
            groundProbeTimer -= Time.deltaTime;
            if (groundProbeTimer <= 0f)
            {
                groundProbeTimer = 0.15f;
                groundY = ProbeGround(camPos);

                // 头顶 40m 内有遮挡 → 室内或屋檐下。
                // 和地面探测共用同一个 0.15s 节流：场景有 6000+ 碰撞体，两条射线每帧打是白给。
                // distance > 0.3 是为了排除"射线从玩家自己的胶囊里起步"这种情况；
                // 另外显式跳过玩家碰撞体，避免向上射线打到自己头顶的胶囊内壁。
                RaycastHit up;
                Transform playerRoot = (cam != null) ? cam.transform.root : null;
                indoor = Physics.Raycast(new Ray(camPos, Vector3.up), out up, 40f)
                         && up.distance > 0.3f
                         && !IsPlayerCollider(up.collider, playerRoot);
            }

            if (splashRoot != null)
                splashRoot.position = new Vector3(camPos.x, groundY + 0.03f, camPos.z);
            if (snowGround != null)
                snowGround.position = new Vector3(camPos.x, groundY + 0.02f, camPos.z);
            if (snowGroundFar != null)
                snowGroundFar.position = new Vector3(camPos.x, groundY + 0.02f, camPos.z);
            if (wet != null) wet.Follow(camPos, groundY);

            // 积雪地形跟随：让雪盖住抬高的花坛/台阶，而不是浮在半空露出硬边。
            // 跟随相机移动，所以只在相机挪动超过阈值时才重打一遍射线，省开销。
            if (snowGround != null) DrapeSnow(camPos);
            if (snowGroundFar != null) DrapeSnowFar(camPos);
        }

        /// <summary>
        /// 积雪地形跟随：把克隆出来的平面网格逐顶点向下射线，按脚下真实地面高度抬高/压低顶点，
        /// 让雪面"贴"住地形起伏（花坛、台阶、缓坡），而不是一整块平板浮在 groundY 上。
        ///
        /// 性能：平面 10 段细分 = 121 个顶点，每顶点一条短距离向下射线。只在相机移动超过
        /// 0.25m（或刚开雪）时重算，静止时直接跳过；射线距离仅 12m、且忽略玩家胶囊与触发器。
        /// </summary>
        private void DrapeSnow(Vector3 camPos)
        {
            if (snowMesh == null || snowBaseVerts == null || snowWorkVerts == null) return;
            // 重算条件：相机移动超过 0.25m、刚开雪、或积雪量变化较大（雪堆"慢慢长出来"）。
            if (!snowDrapeDirty
                && (camPos - lastDrapeCam).sqrMagnitude < 0.0625f
                && Mathf.Abs(snowAmount - lastDrapeAmount) < 0.04f) return;
            lastDrapeCam = camPos;
            snowDrapeDirty = false;

            float px = snowGround.position.x;
            float pz = snowGround.position.z;
            float baseY = groundY + 0.02f;          // 平面自身世界 Y（顶点 y=0 时的高度）
            float sx = SnowGroundSize;               // 单位网格缩放到 SnowGroundSize 米

            bool changed = false;
            for (int i = 0; i < snowBaseVerts.Length; i++)
            {
                Vector3 b = snowBaseVerts[i];
                float wx = px + b.x * sx;
                float wz = pz + b.z * sx;
                float th = SampleGroundHeight(wx, wz, camPos.y);
                // 严格按地形平铺：顶点抬到"射线地面 + 抬升量"上方。
                // 抬升量(SnowGroundLiftNear)必须盖过脚下可见铺装/砖面的凸起，否则砖面会戳穿雪面。
                // 不再叠加任何程序化雪堆 —— 用户反馈"取消堆叠的一团，按地形平铺就行"。
                float d = Mathf.Clamp((th + SnowGroundLiftNear) - baseY, -1.5f, 1.5f);
                if (Mathf.Abs(snowWorkVerts[i].y - d) > 0.01f)
                {
                    snowWorkVerts[i].y = d;
                    changed = true;
                }
            }

            if (changed)
            {
                snowMesh.vertices = snowWorkVerts;
                snowMesh.RecalculateNormals();   // 斜坡上的雪才有正确的受光法线
                snowMesh.RecalculateBounds();
            }
            lastDrapeAmount = snowAmount;
        }

        /// <summary>
        /// 远场雪盘地形跟随：逻辑同 DrapeSnow，作用于远场大圆盘（低分段）。
        /// 远场主要在雪天雾范围内，地形跟随误差被雾掩盖，仍保证地平线方向不露裸地形。
        /// </summary>
        private void DrapeSnowFar(Vector3 camPos)
        {
            if (snowMeshFar == null || snowBaseVertsFar == null || snowWorkVertsFar == null) return;
            if (!snowDrapeDirtyFar
                && (camPos - lastDrapeCamFar).sqrMagnitude < 0.0625f
                && Mathf.Abs(snowAmount - lastDrapeAmountFar) < 0.04f) return;
            lastDrapeCamFar = camPos;
            snowDrapeDirtyFar = false;

            float px = snowGroundFar.position.x;
            float pz = snowGroundFar.position.z;
            float baseY = groundY + 0.02f;
            float sx = SnowGroundFarSize;

            bool changed = false;
            for (int i = 0; i < snowBaseVertsFar.Length; i++)
            {
                Vector3 b = snowBaseVertsFar[i];
                float wx = px + b.x * sx;
                float wz = pz + b.z * sx;
                float th = SampleGroundHeight(wx, wz, camPos.y);
                // 远场格更大（每格 6.5m），抬升量(SnowGroundLiftFar)略高，进一步减少雪面与地形穿插造成的块面。
                float d = Mathf.Clamp((th + SnowGroundLiftFar) - baseY, -1.5f, 1.5f);
                if (Mathf.Abs(snowWorkVertsFar[i].y - d) > 0.01f)
                {
                    snowWorkVertsFar[i].y = d;
                    changed = true;
                }
            }

            if (changed)
            {
                snowMeshFar.vertices = snowWorkVertsFar;
                snowMeshFar.RecalculateNormals();
                snowMeshFar.RecalculateBounds();
            }
            lastDrapeAmountFar = snowAmount;
        }

        /// <summary>
        /// 程序化雪堆/沙丘高度 —— 已按用户要求整体移除。
        /// 原实现用世界 XZ 锁定的低频噪声在地面上方叠起伏（最高约 0.32m），
        /// 但读起来是"一团一团的堆"，与参考图不符；现在雪面严格贴地形平铺。
        /// </summary>

        /// <summary>
        /// 生成一块单位平面网格（XZ 水平、y=0、UV 0..1），seg 段细分。
        /// 用自定义网格替代内建 Plane：内建只有 10 段，铺到 90m 时每格 9m，
        /// 地形跟随 + 雪堆起伏都会变成大块台阶。这里给 56 段（57×57≈3249 顶点），
        /// 既能贴住地形，又能表现平滑雪堆。
        /// </summary>
        private static Mesh BuildSnowPlaneMesh(int seg)
        {
            int v = seg + 1;
            var verts = new Vector3[v * v];
            var uvs = new Vector2[v * v];
            var tris = new int[seg * seg * 6];
            float step = 1f / seg;
            for (int iz = 0; iz < v; iz++)
            {
                for (int ix = 0; ix < v; ix++)
                {
                    int idx = iz * v + ix;
                    verts[idx] = new Vector3(ix * step - 0.5f, 0f, iz * step - 0.5f);
                    uvs[idx] = new Vector2(ix * step, iz * step);
                }
            }
            int t = 0;
            for (int iz = 0; iz < seg; iz++)
            {
                for (int ix = 0; ix < seg; ix++)
                {
                    int a = iz * v + ix;
                    int b = a + 1;
                    int c = a + v;
                    int d = c + 1;
                    tris[t++] = a; tris[t++] = c; tris[t++] = b;
                    tris[t++] = b; tris[t++] = c; tris[t++] = d;
                }
            }
            var m = new Mesh();
            m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            m.vertices = verts;
            m.uv = uvs;
            m.triangles = tris;
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }

        /// <summary>
        /// 在 (x,z) 处向下射线取脚下地面高度。复用 groundHits 缓冲。
        /// 与 ProbeGround 一致：跳过触发器与玩家胶囊；只接受落在眼睛高度 ±4m 内的命中，
        /// 避免误中远处高楼/树冠。找不到地面时退回 groundY（也就是摊平，等于旧行为）。
        /// </summary>
        private float SampleGroundHeight(float x, float z, float eyeY)
        {
            Transform playerRoot = (cam != null) ? cam.transform.root : null;
            var origin = new Vector3(x, eyeY + 6f, z);
            int n = Physics.RaycastNonAlloc(new Ray(origin, Vector3.down), groundHits, 12f);

            float best = float.NegativeInfinity;
            bool found = false;
            for (int i = 0; i < n; i++)
            {
                RaycastHit h = groundHits[i];
                if (h.collider == null || h.collider.isTrigger) continue;
                if (IsPlayerCollider(h.collider, playerRoot)) continue;
                if (h.point.y > eyeY - 4f && h.point.y < eyeY + 6f)
                {
                    if (!found || h.point.y > best) { best = h.point.y; found = true; }
                }
            }
            return found ? best : groundY;
        }

        /// <summary>
        /// 向下射线取脚下地面高度。
        ///
        /// 关键修复（Bug A）：向下射线从相机出发，而相机（local y=1.62）正好在玩家
        /// CharacterController 胶囊体（高 1.8、底在玩家脚下、顶在 1.8）内部。
        /// 如果直接用 Physics.Raycast 取"第一个命中"，这条从胶囊内部出发的射线会命中
        /// 胶囊自己的内壁/顶盖，把 groundY 误判到眼高附近 —— 于是积水层/积雪层被停到
        /// 视线高度，变成横贯画面、能照出建筑倒影的反光带。
        ///
        /// 这里改用 RaycastNonAlloc 收集所有命中，跳过触发器与玩家自身碰撞体，
        /// 再在"低于相机"的命中里取最高的一层，那才是玩家真正踩着的地面。
        /// 站在楼层/平台上时也只会贴到脚下的那一层，而不是楼下的地面。
        /// </summary>
        private float ProbeGround(Vector3 camPos)
        {
            Transform playerRoot = (cam != null) ? cam.transform.root : null;
            int n = Physics.RaycastNonAlloc(new Ray(camPos, Vector3.down), groundHits, 400f);

            float best = float.NegativeInfinity;
            bool found = false;
            for (int i = 0; i < n; i++)
            {
                RaycastHit h = groundHits[i];
                if (h.collider == null) continue;
                if (h.collider.isTrigger) continue;                    // 触发器不算地面
                if (IsPlayerCollider(h.collider, playerRoot)) continue; // 跳过玩家胶囊
                if (h.point.y < camPos.y - 0.001f)                     // 只取相机下方的地面
                {
                    if (!found || h.point.y > best) { best = h.point.y; found = true; }
                }
            }

            if (found) return best;
            return camPos.y - 1.62f;   // 打不到地面（室内/悬空）就用眼高反推
        }

        /// <summary>判断碰撞体是不是玩家自身（玩家根节点或其子物体）。</summary>
        private bool IsPlayerCollider(Collider c, Transform playerRoot)
        {
            if (c == null || playerRoot == null) return false;
            return c.transform.IsChildOf(playerRoot);
        }

        private readonly RaycastHit[] groundHits = new RaycastHit[16];

        /// <summary>
        /// 按相机高度调整粒子范围与强度。
        ///  · 地面视角（第一人称）→ 雨雪全开，发射盒覆盖平视视野；
        ///  · 高空俯视（数字孪生）→ 逐步关掉粒子，靠雾/云/光照表达天气。
        /// </summary>
        private void UpdateRange(float camHeight)
        {
            // 60m 以下全开，130m 以上全关，中间平滑过渡（避免拉高相机时雨雪突然消失）
            altFade = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(60f, 130f, camHeight));

            // 发射盒水平尺寸：60m 只够盖住脚边，平视时 50m 外就没有雨，很假。
            // 默认给 110m（约等于 60° FOV 下 110m 远处的可见宽度），高处再放大。
            float wantArea = Mathf.Clamp(110f + Mathf.Max(0f, camHeight - 2f) * 3f, 110f, 200f);
            if (Mathf.Abs(wantArea - lastArea) > 1.5f)
            {
                lastArea = wantArea;
                var v = new Vector3(wantArea, 0.5f, wantArea);
                if (rainPs != null) { var s = rainPs.shape; s.scale = v; }
                if (snowPs != null) { var s = snowPs.shape; s.scale = v; }
            }
        }

        /// <summary>相机换了（比如从数字孪生切到第一人称）时重新绑定。</summary>
        public void RebindCamera() { cam = null; }
    }
}
```

### A.5  WetGround.cs

原始路径：`Assets/UI_Systems/Scripts/Environment/WetGround.cs`

```csharp
using UnityEngine;
using UnityEngine.Rendering;

namespace CampusUI.Environment
{
    /// <summary>
    /// 雨天积水层。参考《异环》雨天「地面积水 + 环境倒影」的表现。
    ///
    /// 做法：在相机脚下铺一块 40m 见方的水平平面，用 CampusUI/WetGround 着色器画。
    /// 着色器负责反射与涟漪，这里只负责三件事：
    ///   1) **湿滑度累积** —— 打湿要 8 秒，蒸发要 26 秒，不是「雨一停地面立刻干」；
    ///   2) **贴地跟随** —— 高度由 WeatherVisuals 的向下射线给，室内直接关掉；
    ///   3) **天空色下发** —— 让反射的地平线色跟着当前天气走（雨天反射灰云、夜晚反射深蓝）。
    ///
    /// 为什么不逐块地面单独做湿润：本工程的地面是场景里现成的网格，
    /// 材质是磁盘上的共享资产，改它会把 .mat 改脏（天空盒那次已经踩过一次）。
    /// 叠一层独立的半透明水面，既能做到「只有脚下这一片有积水」，
    /// 又不碰任何原有材质。
    /// </summary>
    public class WetGround : MonoBehaviour
    {
        private const float Size = 40f;             // 积水层边长（米）
        private const float GroundOffset = 0.015f;  // 比地面高一点，避免 z-fighting

        private const float WetInSeconds = 8f;      // 打湿用时
        private const float DryInSeconds = 26f;     // 蒸发用时（比打湿慢，符合直觉）

        private Transform plane;
        private Material mat;
        private bool built;

        private float wetness;      // 0~1，湿滑度累积量
        private float indoorFade = 1f;
        private bool on;

        private static readonly int IdWetness = Shader.PropertyToID("_Wetness");
        private static readonly int IdSky = Shader.PropertyToID("_SkyColor");
        private static readonly int IdHorizon = Shader.PropertyToID("_HorizonColor");
        private static readonly int IdRipple = Shader.PropertyToID("_RippleStrength");
        private static readonly int IdMask = Shader.PropertyToID("_MaskTex");

        public void Build()
        {
            if (built) return;
            built = true;

            var sh = Shader.Find("CampusUI/WetGround");
            if (sh == null)
            {
                Debug.LogWarning("[WetGround] 找不到 CampusUI/WetGround 着色器，雨天积水效果不可用。");
                return;
            }

            var go = GameObject.CreatePrimitive(PrimitiveType.Plane);   // 默认 10x10 单位
            go.name = "WetGround";

            var col = go.GetComponent<Collider>();
            if (col != null) Destroy(col);       // 有碰撞体会挡住玩家脚下的向下射线

            go.transform.SetParent(transform, false);
            go.transform.localScale = new Vector3(Size * 0.1f, 1f, Size * 0.1f);

            mat = new Material(sh);
            mat.name = "ProcWetGround";
            // 注意：不能用 mat.mainTexture —— 它只认 _MainTex，本着色器的贴图叫 _MaskTex。
            mat.SetTexture(IdMask, ProceduralAssets.WetMask);
            mat.SetColor("_WaterColor", new Color(0.045f, 0.060f, 0.075f, 1f));
            mat.renderQueue = 2992;              // 必须在积雪层 2996 之前，积雪才能盖住积水

            var mr = go.GetComponent<MeshRenderer>();
            if (mr != null)
            {
                mr.sharedMaterial = mat;
                mr.shadowCastingMode = ShadowCastingMode.Off;
                mr.receiveShadows = false;
                mr.lightProbeUsage = LightProbeUsage.Off;
                // 必须保留反射探针：shader 里的 unity_SpecCube0 是"最近反射探针"，
                // 关掉它就只剩默认天空反射，场景里放了探针也拿不到建筑倒影。
                mr.reflectionProbeUsage = ReflectionProbeUsage.BlendProbes;
            }

            plane = go.transform;
            go.SetActive(false);
            on = false;
        }

        /// <summary>贴地跟随。groundY 由调用方的向下射线给出。</summary>
        public void Follow(Vector3 camPos, float groundY)
        {
            if (plane == null) return;
            plane.position = new Vector3(camPos.x, groundY + GroundOffset, camPos.z);
        }

        /// <summary>每帧由 WeatherVisuals 调用。</summary>
        public void Apply(float rain, float dt, Color sky, Color horizon, bool indoor)
        {
            if (mat == null || plane == null) return;

            // ---- 湿滑度累积 ----
            // 用 MoveTowards 而不是指数插值：指数插值永远到不了 0，地面会一直是湿的。
            float target = Mathf.Clamp01(rain);
            if (target > wetness)
                wetness = Mathf.MoveTowards(wetness, target, dt / WetInSeconds);
            else
                wetness = Mathf.MoveTowards(wetness, 0f, dt / DryInSeconds);

            // ---- 室内外过渡 ----
            indoorFade = Mathf.MoveTowards(indoorFade, indoor ? 0f : 1f, dt * 3f);

            float a = wetness * indoorFade;
            bool want = a > 0.01f;
            if (want != on)
            {
                plane.gameObject.SetActive(want);
                on = want;
            }
            if (!on) return;

            mat.SetFloat(IdWetness, a);
            mat.SetColor(IdSky, sky);
            mat.SetColor(IdHorizon, horizon);
            // 涟漪只在下雨时有；雨停了水洼还在，但水面应该静下来
            mat.SetFloat(IdRipple, Mathf.Clamp01(rain) * 1.2f);
        }
    }
}
```

### A.6  SnowOnTrees.cs

原始路径：`Assets/UI_Systems/Scripts/Environment/SnowOnTrees.cs`

```csharp
using UnityEngine;
using UnityEngine.Rendering;   // ShadowCastingMode / LightProbeUsage / ReflectionProbeUsage 都在这个命名空间

namespace CampusUI.Environment
{
    /// <summary>
    /// 植被(树)积雪：读取地形 TreeInstance，在每棵树根生成一块"世界锁定"的雪堆圆盘，
    /// 随全局雪量(_GlobalSnow，由 WeatherVisuals 每帧写入)出现/消失，
    /// 近似《异环》"树根被雪埋、顶部留秋色"的效果。
    ///
    /// 设计要点：
    ///  · 不修改任何内置着色器 —— 复用 CampusUI/SnowGround(受主光 + 球谐环境光 + 接收阴影 + 融雾)。
    ///  · 所有树根雪堆合并进一个 mesh，仅 1 个 draw call。
    ///  · 雪堆中心微隆、边缘柔和，叠加世界锁定的低频噪声，像雪堆堆在树根而非一片平板。
    ///  · 树木本身是竖直 mesh，雪堆是地面上的水平圆盘，树干从雪里穿出 → 视觉上正是"根被雪埋、冠留秋色"。
    ///
    /// 用法：把本组件挂到场景里任意一个激活的 GameObject 上(建议与 WeatherVisuals 同级)即可。
    /// </summary>
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class SnowOnTrees : MonoBehaviour
    {
        [Tooltip("每棵树根雪堆半径(米)")]
        public float pileRadius = 3.0f;
        [Tooltip("单块雪堆圆盘分段(越大越圆)")]
        public int seg = 16;
        [Tooltip("雪堆中心最大隆起高度(米)")]
        public float pileHeight = 0.18f;

        private Mesh pileMesh;
        private Material pileMat;
        private MeshRenderer pileMr;
        private bool built;

        private static readonly int IdSnowTex = Shader.PropertyToID("_SnowTex");
        private static readonly int IdSnowColor = Shader.PropertyToID("_SnowColor");
        private static readonly int IdSkyColor = Shader.PropertyToID("_SkyColor");
        private static readonly int IdSnowAmount = Shader.PropertyToID("_SnowAmount");
        private static readonly int IdEdgeFade = Shader.PropertyToID("_EdgeFade");
        private static readonly int IdGlobalSnow = Shader.PropertyToID("_GlobalSnow");

        private void Start() { Build(); }

        private void Build()
        {
            if (built) return;
            built = true;

            Terrain terrain = Terrain.activeTerrain;
            if (terrain == null || terrain.terrainData == null) { enabled = false; return; }
            var td = terrain.terrainData;
            var instances = td.treeInstances;
            if (instances == null || instances.Length == 0) { enabled = false; return; }

            var tpos = terrain.GetPosition();
            float w = td.size.x, d = td.size.z;

            int vertsPer = (seg + 1) * (seg + 1);
            int triPer = seg * seg * 6;
            int totalV = vertsPer * instances.Length;
            int totalT = triPer * instances.Length;
            var verts = new Vector3[totalV];
            var uvs = new Vector2[totalV];
            var norms = new Vector3[totalV];
            var tris = new int[totalT];

            int vOff = 0, tOff = 0;
            for (int t = 0; t < instances.Length; t++)
            {
                var inst = instances[t];
                float wx = tpos.x + inst.position.x * w;
                float wz = tpos.z + inst.position.z * d;
                float wy = terrain.SampleHeight(new Vector3(wx, 0f, wz)) + 0.02f;

                int idx0 = vOff;
                for (int z = 0; z <= seg; z++)
                {
                    for (int x = 0; x <= seg; x++)
                    {
                        float u = (float)x / seg - 0.5f;
                        float v = (float)z / seg - 0.5f;
                        float lx = u * 2f * pileRadius;
                        float lz = v * 2f * pileRadius;
                        // 轻微隆起：中心略高、边缘低，叠加世界锁定的低频噪声，像雪堆堆在树根
                        float r = Mathf.Sqrt(u * u + v * v) * 2f;   // 0(中心)~1(边缘)
                        float bump = (1f - r) * pileHeight;
                        bump += (Mathf.PerlinNoise(wx * 0.2f, wz * 0.2f) - 0.5f) * 0.08f;
                        verts[vOff] = new Vector3(wx + lx, wy + bump, wz + lz);
                        uvs[vOff] = new Vector2((float)x / seg, (float)z / seg);
                        norms[vOff] = Vector3.up;
                        vOff++;
                    }
                }
                for (int z = 0; z < seg; z++)
                {
                    for (int x = 0; x < seg; x++)
                    {
                        int a = idx0 + z * (seg + 1) + x;
                        int b = a + 1;
                        int c = a + (seg + 1);
                        int e = c + 1;
                        tris[tOff++] = a; tris[tOff++] = c; tris[tOff++] = b;
                        tris[tOff++] = b; tris[tOff++] = c; tris[tOff++] = e;
                    }
                }
            }

            pileMesh = new Mesh();
            pileMesh.name = "TreeSnowPiles";
            pileMesh.vertices = verts;
            pileMesh.uv = uvs;
            pileMesh.normals = norms;
            pileMesh.triangles = tris;
            pileMesh.RecalculateBounds();

            var mf = GetComponent<MeshFilter>();
            pileMr = GetComponent<MeshRenderer>();
            if (mf == null) mf = gameObject.AddComponent<MeshFilter>();
            if (pileMr == null) pileMr = gameObject.AddComponent<MeshRenderer>();
            mf.sharedMesh = pileMesh;

            var sh = Shader.Find("CampusUI/SnowGround");
            if (sh == null)
            {
                Debug.LogWarning("[SnowOnTrees] 找不到 CampusUI/SnowGround 着色器，树根积雪不可用。");
                enabled = false; return;
            }
            pileMat = new Material(sh);
            pileMat.name = "TreeSnowPile";
            // 复用天气系统程序化生成的雪斑纹理(覆盖率遮罩)
            pileMat.SetTexture(IdSnowTex, ProceduralAssets.SnowPatch);
            pileMat.SetColor(IdSnowColor, new Color(0.92f, 0.95f, 1.0f, 1f));
            pileMat.SetColor(IdSkyColor, new Color(0.74f, 0.82f, 0.96f, 1f));
            pileMat.SetFloat(IdSnowAmount, 0f);
            pileMat.SetFloat(IdEdgeFade, 1.0f);
            pileMat.renderQueue = 2997;   // 在近场/远场雪盘之上，树根雪堆优先显示

            if (pileMr != null)
            {
                pileMr.sharedMaterial = pileMat;
                pileMr.shadowCastingMode = ShadowCastingMode.Off;
                pileMr.receiveShadows = true;   // 树/角色影子落在树根雪堆上
                pileMr.lightProbeUsage = LightProbeUsage.Off;
                pileMr.reflectionProbeUsage = ReflectionProbeUsage.Off;
                pileMr.enabled = false;         // 初始不渲染；由 Update 按雪量开关
            }
        }

        private void Update()
        {
            if (!built || pileMat == null || pileMr == null) return;
            float a = Shader.GetGlobalFloat(IdGlobalSnow);
            bool on = a > 0.01f;
            if (on != pileMr.enabled) pileMr.enabled = on;
            if (on) pileMat.SetFloat(IdSnowAmount, a);
        }

        private void OnDestroy()
        {
            if (pileMesh != null) Destroy(pileMesh);
            if (pileMat != null) Destroy(pileMat);
        }
    }
}
```

### A.7  WeatherDirector.cs

原始路径：`Assets/UI_Systems/Scripts/Environment/WeatherDirector.cs`

```csharp
using UnityEngine;
using CampusUI.Core;

namespace CampusUI.Environment
{
    /// <summary>
    /// 天气自动轮换，对应《异环》天气系统的「随机模式」。
    ///
    /// 为什么不用纯随机：
    ///   纯随机会出现「晴天直接跳雷雨」这种不符合大气演进的情况，玩家一眼就看出是随机数。
    ///   这里用**天气转移权重链**——每种天气只允许转向"气象上说得通"的下一档，
    ///   于是自然会走成 晴 → 多云 → 阴 → 雨 → 雷雨 → 雨 → 阴 → 多云 → 晴 这样的循环。
    ///
    /// 停留时长在 90~200 秒之间随机。太短会显得天气在抽搐，太长玩家感受不到变化。
    /// </summary>
    public class WeatherDirector : MonoBehaviour
    {
        public static WeatherDirector Instance;

        [Tooltip("是否自动轮换天气（对应《异环》的随机模式）")]
        public bool auto;

        [Tooltip("每次天气停留的最短/最长秒数")]
        [SerializeField] private float minInterval = 90f;
        [SerializeField] private float maxInterval = 200f;

        private float timer;
        private EnvironmentState env;

        public float Remaining { get { return Mathf.Max(0f, timer); } }

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        /// <summary>UI 用：找到或创建一个 WeatherDirector。</summary>
        public static WeatherDirector Ensure()
        {
            if (Instance != null) return Instance;
            var go = new GameObject("WeatherDirector");
            return go.AddComponent<WeatherDirector>();
        }

        public void SetAuto(bool on)
        {
            auto = on;
            // 刚打开时不要等满 90 秒，否则玩家会以为按钮没生效
            if (on) timer = Random.Range(minInterval * 0.35f, minInterval * 0.75f);
        }

        void Update()
        {
            if (!auto) return;

            if (env == null)
            {
                env = EnvironmentState.Instance;
                if (env == null) env = FindObjectOfType<EnvironmentState>();
                if (env == null) return;
            }

            // 正在过渡时不要叠加新目标，否则会把 2.4 秒的平滑过渡打断成两段
            if (env.IsTransitioning) return;

            timer -= Time.deltaTime;
            if (timer > 0f) return;

            timer = Random.Range(minInterval, maxInterval);
            env.SetWeather(PickNext(env.weather));
        }

        // =====================================================================
        // 天气转移权重链
        // =====================================================================

        private static Weather PickNext(Weather cur)
        {
            Weather[] pool;
            float[] weights;
            Table(cur, out pool, out weights);

            float sum = 0f;
            for (int i = 0; i < weights.Length; i++) sum += weights[i];

            float r = Random.value * sum;
            for (int i = 0; i < pool.Length; i++)
            {
                r -= weights[i];
                if (r <= 0f) return pool[i];
            }
            return pool[pool.Length - 1];
        }

        /// <summary>
        /// 每种天气允许转向的下一档及权重。
        /// 设计依据：云量是渐变的（晴→多云→阴→雨），雷雨只会从雨里长出来，
        /// 雾和雪都需要特定的湿度/温度条件，所以不能直接连到晴天暴晒。
        /// </summary>
        private static void Table(Weather w, out Weather[] pool, out float[] weights)
        {
            switch (w)
            {
                case Weather.Clear:
                    pool = new[] { Weather.Cloudy, Weather.Clear, Weather.Fog };
                    weights = new[] { 0.60f, 0.25f, 0.15f };
                    return;

                case Weather.Cloudy:
                    pool = new[] { Weather.Overcast, Weather.Clear, Weather.Fog, Weather.Rain };
                    weights = new[] { 0.45f, 0.35f, 0.10f, 0.10f };
                    return;

                case Weather.Overcast:
                    pool = new[] { Weather.Rain, Weather.Cloudy, Weather.Snow, Weather.Fog };
                    weights = new[] { 0.45f, 0.35f, 0.10f, 0.10f };
                    return;

                case Weather.Rain:
                    pool = new[] { Weather.Overcast, Weather.Storm, Weather.Cloudy };
                    weights = new[] { 0.50f, 0.30f, 0.20f };
                    return;

                case Weather.Storm:
                    pool = new[] { Weather.Rain, Weather.Overcast };
                    weights = new[] { 0.60f, 0.40f };
                    return;

                case Weather.Fog:
                    pool = new[] { Weather.Clear, Weather.Cloudy };
                    weights = new[] { 0.50f, 0.50f };
                    return;

                default: // Weather.Snow
                    pool = new[] { Weather.Overcast, Weather.Cloudy };
                    weights = new[] { 0.60f, 0.40f };
                    return;
            }
        }
    }
}
```

### A.8  ProceduralAssets.cs

原始路径：`Assets/UI_Systems/Scripts/Environment/ProceduralAssets.cs`

```csharp
using UnityEngine;

namespace CampusUI.Environment
{
    /// <summary>
    /// 运行时程序化生成天气系统需要的一切素材：雨丝/雪花/云噪声贴图，以及雨声/风声/雷声音频。
    ///
    /// 为什么不用现成素材：
    ///   · 零依赖 —— 工程里不用塞一堆 png/ogg，也不会有授权问题，换机器照样跑；
    ///   · 可调 —— 想要更密的雨就把贴图宽度调窄，不用回美术；
    ///   · 体积小 —— 全部在 Awake 里生成一次并静态缓存，几十毫秒的事。
    ///
    /// 所有生成物都做了静态缓存，重复调用不会重复分配。
    /// </summary>
    public static class ProceduralAssets
    {
        private const int SR = 44100;      // 采样率

        private static Texture2D _rain, _snow, _cloud, _star, _snowPatch, _wetMask;
        private static Texture3D _cloud3d;
        private static AudioClip _rainSfx, _windSfx, _thunderSfx;

        // =====================================================================
        // 贴图
        // =====================================================================

        /// <summary>雨丝：8×64 的竖向细条，顶端亮、底端淡，左右带软边。</summary>
        public static Texture2D RainStreak
        {
            get
            {
                if (_rain != null) return _rain;

                const int W = 8, H = 64;
                var t = new Texture2D(W, H, TextureFormat.RGBA32, false);
                t.name = "ProcRainStreak";
                t.wrapMode = TextureWrapMode.Clamp;
                t.filterMode = FilterMode.Bilinear;

                var px = new Color32[W * H];
                for (int y = 0; y < H; y++)
                {
                    float v = y / (float)(H - 1);          // 0 = 底，1 = 顶
                    float a = Mathf.Pow(v, 1.7f);          // 顶亮底淡，像运动模糊
                    for (int x = 0; x < W; x++)
                    {
                        float dx = Mathf.Abs(x - (W - 1) * 0.5f) / ((W - 1) * 0.5f);
                        float edge = 1f - dx * dx;          // 中间实、两边虚
                        byte b = (byte)(Mathf.Clamp01(a * edge) * 255f);
                        px[y * W + x] = new Color32(255, 255, 255, b);
                    }
                }
                t.SetPixels32(px);
                t.Apply();
                _rain = t;
                return _rain;
            }
        }

        /// <summary>雪花：32×32 径向软圆，中心不透明、边缘完全淡出。</summary>
        public static Texture2D Snowflake
        {
            get
            {
                if (_snow != null) return _snow;

                const int S = 32;
                var t = new Texture2D(S, S, TextureFormat.RGBA32, false);
                t.name = "ProcSnowflake";
                t.wrapMode = TextureWrapMode.Clamp;
                t.filterMode = FilterMode.Bilinear;

                var px = new Color32[S * S];
                float c = (S - 1) * 0.5f;
                for (int y = 0; y < S; y++)
                {
                    for (int x = 0; x < S; x++)
                    {
                        float dx = (x - c) / c, dy = (y - c) / c;
                        float r = Mathf.Sqrt(dx * dx + dy * dy);
                        float a = Mathf.Clamp01(1f - r);
                        a = a * a * (3f - 2f * a);          // smoothstep，边缘更柔
                        byte b = (byte)(a * 255f);
                        px[y * S + x] = new Color32(255, 255, 255, b);
                    }
                }
                t.SetPixels32(px);
                t.Apply();
                _snow = t;
                return _snow;
            }
        }

        /// <summary>云噪声：256×256 的 4 倍频 Perlin fBm，RGB 全白，A 存云密度。</summary>
        public static Texture2D CloudNoise
        {
            get
            {
                if (_cloud != null) return _cloud;

                const int S = 256;
                var t = new Texture2D(S, S, TextureFormat.RGBA32, false);
                t.name = "ProcCloudNoise";
                t.wrapMode = TextureWrapMode.Repeat;
                t.filterMode = FilterMode.Bilinear;

                var alpha = new float[S * S];

                for (int y = 0; y < S; y++)
                {
                    for (int x = 0; x < S; x++)
                    {
                        float u = x / (float)S, v = y / (float)S;
                        float n = 0f, amp = 0.5f, freq = 3.0f;
                        for (int o = 0; o < 4; o++)
                        {
                            n += Mathf.PerlinNoise(u * freq + o * 17.3f, v * freq + o * 9.1f) * amp;
                            amp *= 0.5f;
                            freq *= 2.07f;
                        }
                        alpha[y * S + x] = n;
                    }
                }

                // 对比度拉伸：用 6%~94% 分位数把噪声铺满 0~1。
                //
                // 不用「固定阈值 + Pow」那套：那组参数依赖 Mathf.PerlinNoise 的绝对幅度，
                // 实测会让 56% 的像素落到接近 0、只有 3% 超过 0.5 —— 云穹顶变成稀稀几缕，
                // 阴天/雷雨根本铺不满天空。分位数拉伸和噪声的绝对量纲无关，换实现也稳。
                var sorted = (float[])alpha.Clone();
                System.Array.Sort(sorted);
                float lo = sorted[Mathf.FloorToInt(sorted.Length * 0.06f)];
                float hi = sorted[Mathf.FloorToInt(sorted.Length * 0.94f)];
                float inv = 1f / Mathf.Max(1e-4f, hi - lo);
                for (int i = 0; i < alpha.Length; i++)
                    alpha[i] = Mathf.Clamp01((alpha[i] - lo) * inv);

                // 水平接缝：球面 UV 的 u 方向首尾相接，必须让 x=0 与 x=S-1 取同一个值，
                // 否则会看到一条明显的竖线。做法是两端各留 fade 列，同时向「本行两端平均值」收敛：
                // 这样首尾两列严格相等（接缝连续），又保留了每行的云形态差异，
                // 不会在球面上压出一条等宽的灰色经线。
                int fade = S / 8;
                for (int y = 0; y < S; y++)
                {
                    int row = y * S;
                    float e = (alpha[row] + alpha[row + S - 1]) * 0.5f;
                    for (int i = 0; i < fade; i++)
                    {
                        float k = i / (float)fade;           // 0 → 1
                        alpha[row + i] = Mathf.Lerp(e, alpha[row + i], k);
                        alpha[row + S - 1 - i] = Mathf.Lerp(alpha[row + S - 1 - i], e, k);
                    }
                    alpha[row] = e;
                    alpha[row + S - 1] = e;
                }

                var px = new Color32[S * S];
                for (int i = 0; i < px.Length; i++)
                    px[i] = new Color32(255, 255, 255, (byte)(alpha[i] * 255f));

                t.SetPixels32(px);
                t.Apply();
                _cloud = t;
                return _cloud;
            }
        }

        /// <summary>
        /// 星空：512×512 全透明底 + 随机星点，亮度呈幂分布（多数很暗、少数很亮）。
        ///
        /// 为什么需要：换成程序化天空盒后，夜晚会变成一片纯深蓝渐变，没有星星会很假。
        /// 星星用 alpha 存亮度、RGB 全白，直接贴到球面穹顶上用普通 Alpha 混合即可。
        /// </summary>
        public static Texture2D StarField
        {
            get
            {
                if (_star != null) return _star;

                const int S = 512;
                var t = new Texture2D(S, S, TextureFormat.RGBA32, false);
                t.name = "ProcStarField";
                t.wrapMode = TextureWrapMode.Repeat;
                t.filterMode = FilterMode.Bilinear;

                var px = new Color32[S * S];
                for (int i = 0; i < px.Length; i++) px[i] = new Color32(255, 255, 255, 0);

                var rnd = new System.Random(90210);
                const int Count = 1500;
                for (int i = 0; i < Count; i++)
                {
                    int x = rnd.Next(S), y = rnd.Next(S);
                    float mag = Mathf.Pow((float)rnd.NextDouble(), 2.5f);   // 幂分布：暗星占多数
                    float bright = 0.30f + 0.70f * mag;
                    int r = mag > 0.70f ? 1 : 0;                            // 亮星画成 3×3 带柔边

                    for (int dy = -r; dy <= r; dy++)
                    {
                        for (int dx = -r; dx <= r; dx++)
                        {
                            float fall = (r == 0) ? 1f
                                      : Mathf.Clamp01(1f - (dx * dx + dy * dy) / 2.6f);
                            int xx = ((x + dx) % S + S) % S;
                            int yy = ((y + dy) % S + S) % S;
                            int idx = yy * S + xx;
                            byte a = (byte)(Mathf.Clamp01(bright * fall) * 255f);
                            if (a > px[idx].a) px[idx] = new Color32(255, 255, 255, a);
                        }
                    }
                }

                t.SetPixels32(px);
                t.Apply();
                _star = t;
                return _star;
            }
        }

        /// <summary>
        /// 地面积雪斑块：噪声密度 × 径向淡出。
        ///
        /// 为什么需要径向淡出：积雪层是一块贴地的方形平面，如果 alpha 铺到边缘，
        /// 就会看到一块"白色正方形"浮在地上。只让 alpha 在最外 10%（半径 90%~100%）淡到 0，
        /// 其余区域都是实白 —— 配合 90m 大圆盘，第一人称看过去是"整片地都白了"，
        /// 只在最外圈柔和淡出，看不出几何边界。
        /// </summary>
        public static Texture2D SnowPatch
        {
            get
            {
                if (_snowPatch != null) return _snowPatch;

                const int S = 256;
                var t = new Texture2D(S, S, TextureFormat.RGBA32, false);
                t.name = "ProcSnowPatch";
                t.wrapMode = TextureWrapMode.Clamp;
                t.filterMode = FilterMode.Bilinear;

                float c = (S - 1) * 0.5f;
                var px = new Color32[S * S];

                for (int y = 0; y < S; y++)
                {
                    for (int x = 0; x < S; x++)
                    {
                        float dx = (x - c) / c, dy = (y - c) / c;
                        float r = Mathf.Sqrt(dx * dx + dy * dy);
                        // 边缘淡出收到最外 7%（r 0.93→1.0）：雪盘内部完全实白，只有最外圈柔和收边。
                        float edge = 1f - Mathf.SmoothStep(0.93f, 1f, r);

                        // 噪声不再参与 alpha。
                        // 之前是 alpha = edge * (0.70 + 0.30 * n)：覆盖率在 0.7~1.0 之间波动，
                        // 草地的绿褐色会透过 0~30% 透出来，整片雪看起来"发灰、发虚、一块一块"。
                        // 现在 alpha 恒为实心（只保留最外圈淡出），噪声改存进 RGB，
                        // 由着色器取 .r 做微法线颗粒 —— 有质感，但不会让地面颜色透出来。
                        float u = x / (float)S, v = y / (float)S;
                        float n = 0f, amp = 0.5f, freq = 34.0f;
                        for (int o = 0; o < 3; o++)
                        {
                            n += Mathf.PerlinNoise(u * freq + o * 13.7f, v * freq + o * 7.3f) * amp;
                            amp *= 0.5f;
                            freq *= 2.1f;
                        }
                        n = Mathf.Clamp01(n);

                        px[y * S + x] = new Color32(
                            (byte)(n * 255f), (byte)(n * 255f), (byte)(n * 255f),
                            (byte)(edge * 255f));
                    }
                }

                t.SetPixels32(px);
                t.Apply();
                _snowPatch = t;
                return _snowPatch;
            }
        }

        /// <summary>
        /// 雨天积水遮罩：R 通道 = 「这里有多像一个水洼」。
        ///
        /// 这张图不直接当颜色用，是给 WetGround.shader 当遮罩的：
        ///   R 高 → 水洼，几乎全不透明、反射强；
        ///   R 低 → 只有一层薄湿膜，透出原来的地面。
        /// 所以它需要的是「斑块感」而不是「云感」—— 频率要比云高得多，
        /// 且必须做阈值化，否则整片地面会均匀地糊上一层灰。
        ///
        /// 阈值用 34%~64% 分位数而不是写死常数：分位数拉伸与噪声的绝对量纲无关，
        /// 换噪声实现也不会突然变成「全是水」或「一滴没有」。
        /// </summary>
        public static Texture2D WetMask
        {
            get
            {
                if (_wetMask != null) return _wetMask;

                const int S = 512;
                var t = new Texture2D(S, S, TextureFormat.RGBA32, false);
                t.name = "ProcWetMask";
                t.wrapMode = TextureWrapMode.Clamp;
                t.filterMode = FilterMode.Bilinear;

                // 这张图最终拉伸铺在 40m 见方的地面上，基础频率 6 → 每周期约 6.7m；
                // 四层八度后最细的一层约 0.4m，水洼边界才有「不规则的湿地」感。
                var n = new float[S * S];
                for (int y = 0; y < S; y++)
                {
                    for (int x = 0; x < S; x++)
                    {
                        float u = x / (float)S, v = y / (float)S;
                        float acc = 0f, amp = 0.5f, freq = 6.0f;
                        for (int o = 0; o < 4; o++)
                        {
                            acc += Mathf.PerlinNoise(u * freq + o * 11.3f, v * freq + o * 5.9f) * amp;
                            amp *= 0.5f;
                            freq *= 2.05f;
                        }
                        n[y * S + x] = acc;
                    }
                }

                var sorted = (float[])n.Clone();
                System.Array.Sort(sorted);
                float lo = sorted[Mathf.FloorToInt(sorted.Length * 0.34f)];
                float hi = sorted[Mathf.FloorToInt(sorted.Length * 0.64f)];
                float inv = 1f / Mathf.Max(1e-4f, hi - lo);

                var px = new Color32[S * S];
                for (int i = 0; i < n.Length; i++)
                {
                    float k = Mathf.Clamp01((n[i] - lo) * inv);
                    float puddle = k * k * (3f - 2f * k);       // smoothstep，让水洼边界是软的
                    px[i] = new Color32((byte)(puddle * 255f), (byte)(puddle * 255f),
                                        (byte)(puddle * 255f), 255);
                }

                t.SetPixels32(px);
                t.Apply();
                _wetMask = t;
                return _wetMask;
            }
        }

        /// <summary>
        /// 云的三维噪声体（Texture3D）。
        ///
        /// 为什么必须用 3D：云要铺满整片天空，可选的采样方式只有三种 ——
        ///   · 球面 UV   → 南北极把经线收敛成一点，贴图被无限拉伸，抬头就是放射条纹；
        ///   · 三平面 2D → 没有极点，但投影本质是立方的：每 90° 图案重复一次，
        ///                 面与面的混合权重又让交界处露出整齐的格子；
        ///   · 3D 噪声   → 视线方向直接索引一个连续体积，无极点、无接缝、无平铺。
        ///
        /// 采样时用 Clamp + 半径 0.34，让查询球始终落在 [0,1]³ 内部，
        /// 所以贴图本身不需要是周期的（做真正的 3D 周期噪声要 6D 采样，不划算）。
        /// </summary>
        public static Texture3D CloudVolume
        {
            get
            {
                if (_cloud3d != null) return _cloud3d;

                const int S = 48;
                int total = S * S * S;
                var raw = new float[total];

                for (int z = 0; z < S; z++)
                {
                    float w = z / (float)S;
                    for (int y = 0; y < S; y++)
                    {
                        float v = y / (float)S;
                        for (int x = 0; x < S; x++)
                        {
                            float u = x / (float)S;
                            float n = 0f, amp = 0.5f, freq = 3.0f;

                            for (int o = 0; o < 4; o++)
                            {
                                float fx = u * freq + o * 17.3f;
                                float fy = v * freq + o * 9.1f;
                                float fz = w * freq + o * 5.7f;
                                // 三组 2D Perlin 交错，近似各向同性的 3D 噪声
                                n += Mathf.PerlinNoise(fx, fy) * amp;
                                n += Mathf.PerlinNoise(fy, fz) * amp * 0.7f;
                                n += Mathf.PerlinNoise(fz, fx) * amp * 0.7f;
                                amp *= 0.5f;
                                freq *= 2.07f;
                            }
                            raw[z * S * S + y * S + x] = n;
                        }
                    }
                }

                // 分位数拉伸：和 2D 云贴图一样，不依赖噪声的绝对幅度
                var sorted = (float[])raw.Clone();
                System.Array.Sort(sorted);
                float lo = sorted[Mathf.FloorToInt(total * 0.08f)];
                float hi = sorted[Mathf.FloorToInt(total * 0.92f)];
                float inv = 1f / Mathf.Max(1e-4f, hi - lo);

                var px = new Color32[total];
                for (int i = 0; i < total; i++)
                {
                    float a = Mathf.Clamp01((raw[i] - lo) * inv);
                    px[i] = new Color32(255, 255, 255, (byte)(a * 255f));
                }

                var t = new Texture3D(S, S, S, TextureFormat.RGBA32, false);
                t.name = "ProcCloudVolume";
                t.wrapMode = TextureWrapMode.Clamp;   // 查询球不碰边界，不需要 Repeat
                t.filterMode = FilterMode.Bilinear;
                t.SetPixels32(px);
                t.Apply();

                _cloud3d = t;
                return _cloud3d;
            }
        }

        /// <summary>云穹顶材质（3D 噪声版）。</summary>
        public static Material MakeCloudMaterial(Color tint, string name, int queue)
        {
            Shader sh = Resources.Load<Shader>("CloudDome");
            if (sh == null) sh = Shader.Find("CampusUI/CloudDome");
            if (sh == null)
            {
                Debug.LogWarning("[ProceduralAssets] 找不到 CampusUI/CloudDome，退回 2D 三平面（天空会有格子）。");
                return MakeSkyDomeMaterial(CloudNoise, tint, name, 1.35f, 0.006f, 0.30f, queue);
            }

            var m = new Material(sh);
            m.name = name;
            m.SetTexture("_Noise3D", CloudVolume);
            m.SetColor("_Color", tint);
            m.SetFloat("_Opacity", tint.a);
            m.SetFloat("_Radius", 0.34f);
            m.SetFloat("_Speed", 0.02f);
            m.SetFloat("_HorizonFade", 0.30f);
            m.renderQueue = queue;
            return m;
        }

        /// <summary>
        /// 天空穹顶材质（2D 三平面，给星空用）。
        ///
        /// 用 `CampusUI/SkyDome` 做**三平面投影**，避开球面 UV 的极点拉伸 ——
        /// 普通球体的 UV 在南北极会把所有经线压到一个点，贴图被无限拉伸，
        /// 抬头看就是一圈放射状条纹。三平面投影按视线方向选平面采样，没有这个奇点。
        /// 星空用它没问题（星点稀疏，立方体面的痕迹看不出来）；云层必须走 3D 版本。
        ///
        /// 放在 Resources 下并用 Resources.Load 取，是为了打包时不会被剥离
        /// （Shader.Find 在构建后只能找到 Always Included 里的着色器）。
        /// </summary>
        public static Material MakeSkyDomeMaterial(Texture2D tex, Color tint, string name,
                                                   float tiling, float speed, float horizonFade, int queue)
        {
            Shader sh = Resources.Load<Shader>("SkyDome");
            if (sh == null) sh = Shader.Find("CampusUI/SkyDome");
            if (sh == null)
            {
                Debug.LogWarning("[ProceduralAssets] 找不到 CampusUI/SkyDome，退回 Sprites/Default（天空会有极点条纹）。");
                return MakeSpriteMaterial(tex, tint, name, queue);
            }

            var m = new Material(sh);
            m.name = name;
            m.mainTexture = tex;                 // -> _MainTex
            m.SetColor("_Color", tint);
            m.SetFloat("_Opacity", tint.a);
            m.SetFloat("_Scale", tiling);
            m.SetFloat("_Speed", speed);
            m.SetFloat("_HorizonFade", horizonFade);
            m.renderQueue = queue;
            return m;
        }

        /// <summary>透明无光照材质（Sprites/Default 在 URP 下正常工作，且双面、不写深度）。</summary>
        /// <param name="queue">
        /// 渲染队列。默认 3000（Transparent）。
        /// 云穹顶和星空穹顶的球心都重合在相机上，按距离排序时距离都是 0，
        /// 队列相同时先后顺序不确定，会出现"星星盖住云"。所以用队列显式分层：
        /// 星空 2990 &lt; 云 2995 &lt; 雨雪 3000。
        /// </param>
        public static Material MakeSpriteMaterial(Texture2D tex, Color tint, string name, int queue = 3000)
        {
            // Sprites/Default 是最省心的选择：双面、Alpha 混合、不受光照影响。
            // 万一工程被裁掉了内置着色器（打包时的剥离），按 URP 的顺序兜底。
            var sh = Shader.Find("Sprites/Default");
            if (sh == null) sh = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            if (sh == null) sh = Shader.Find("Universal Render Pipeline/Unlit");
            if (sh == null)
            {
                Debug.LogWarning("[ProceduralAssets] 找不到可用的透明着色器，粒子材质可能渲染异常。");
                return null;
            }
            var m = new Material(sh);
            m.name = name;
            m.mainTexture = tex;      // URP 侧由 [MainTexture] 特性映射到 _BaseMap
            m.color = tint;           // 同上，映射到 _BaseColor
            m.renderQueue = queue;
            return m;
        }

        // =====================================================================
        // 音频（全部程序化合成，无外部音频素材）
        // =====================================================================

        /// <summary>雨声：白噪声两级低通 + 慢速幅度起伏，听起来像连续的雨幕。</summary>
        public static AudioClip RainSfx
        {
            get
            {
                if (_rainSfx != null) return _rainSfx;

                int len = SR * 6;
                var d = new float[len];
                var rnd = new System.Random(20261027);

                float lp1 = 0f, lp2 = 0f;
                for (int i = 0; i < len; i++)
                {
                    float w = (float)(rnd.NextDouble() * 2.0 - 1.0);
                    lp1 += (w - lp1) * 0.42f;      // 一阶低通
                    lp2 += (lp1 - lp2) * 0.16f;    // 再低通 → 闷一点的雨幕底噪
                    float flutter = 0.86f + 0.14f * Mathf.Sin(i * 6.2831853f / SR * 0.73f);
                    d[i] = lp2 * 1.7f * flutter;     // 两级低通把幅度压到 ~0.09，这里补回来
                }

                d = CrossfadeLoop(d, SR / 2);
                NormalizeTo(d, 0.80f);
                _rainSfx = MakeClip("ProcRain", d);
                return _rainSfx;
            }
        }

        /// <summary>风声：更重的低通 + 0.13Hz 幅度摆动，像风掠过建筑缝隙。</summary>
        public static AudioClip WindSfx
        {
            get
            {
                if (_windSfx != null) return _windSfx;

                int len = SR * 8;
                var d = new float[len];
                var rnd = new System.Random(778899);

                float lp1 = 0f, lp2 = 0f;
                for (int i = 0; i < len; i++)
                {
                    float w = (float)(rnd.NextDouble() * 2.0 - 1.0);
                    lp1 += (w - lp1) * 0.09f;
                    lp2 += (lp1 - lp2) * 0.05f;

                    float t = i / (float)SR;
                    float swell = 0.55f + 0.45f * Mathf.Sin(t * 6.2831853f * 0.13f);
                    swell *= 0.75f + 0.25f * Mathf.Sin(t * 6.2831853f * 0.37f + 1.7f);
                    d[i] = lp2 * 6.0f * swell;       // 0.09/0.05 两级低通非常狠，增益要大得多
                }

                d = CrossfadeLoop(d, SR / 2);
                NormalizeTo(d, 0.80f);
                _windSfx = MakeClip("ProcWind", d);
                return _windSfx;
            }
        }

        /// <summary>雷声：极重低通的长衰减隆隆声 + 开头一记爆裂。</summary>
        public static AudioClip ThunderSfx
        {
            get
            {
                if (_thunderSfx != null) return _thunderSfx;

                int len = SR * 3;
                var d = new float[len];
                var rnd = new System.Random(31415);

                float lp = 0f;
                for (int i = 0; i < len; i++)
                {
                    float w = (float)(rnd.NextDouble() * 2.0 - 1.0);
                    lp += (w - lp) * 0.045f;                 // 很重的低通 → 隆隆声
                    float t = i / (float)len;
                    float env = Mathf.Exp(-t * 4.2f);        // 指数衰减
                    float rumble = 0.6f + 0.4f * Mathf.Sin(t * 26f + 1.3f);
                    d[i] = lp * env * rumble * 2.6f;
                }

                // 开头 90ms 的高频爆裂，模拟雷击瞬间
                int crack = SR * 9 / 100;
                for (int i = 0; i < crack && i < len; i++)
                {
                    float t = i / (float)crack;
                    d[i] += (float)(rnd.NextDouble() * 2.0 - 1.0) * (1f - t) * 0.6f;
                }

                // 尾部 120ms 淡出：不然播到最后一帧会有一个突兀的断点
                int tailFade = SR * 12 / 100;
                for (int i = 0; i < tailFade && i < len; i++)
                {
                    int k = len - 1 - i;
                    d[k] *= i / (float)tailFade;
                }

                NormalizeTo(d, 0.95f);
                _thunderSfx = MakeClip("ProcThunder", d);
                return _thunderSfx;
            }
        }

        // =====================================================================
        // 工具
        // =====================================================================

        private static AudioClip MakeClip(string name, float[] data)
        {
            var clip = AudioClip.Create(name, data.Length, 1, SR, false);
            clip.SetData(data, 0);
            return clip;
        }

        /// <summary>
        /// 把整段音频的峰值压到指定值。
        ///
        /// 为什么必须有：低通滤波 + 固定增益的组合很容易让峰值冲过 1.0（实测风声峰值能到 1.7），
        /// 超出的部分在 SetData 里会被硬削波，听感是"噼啪"的破音。
        /// 归一化只改整体音量、不改波形，所以 flutter / swell 的起伏全部保留。
        /// </summary>
        private static void NormalizeTo(float[] d, float peak)
        {
            float max = 0f;
            for (int i = 0; i < d.Length; i++)
            {
                float a = d[i] < 0f ? -d[i] : d[i];
                if (a > max) max = a;
            }
            if (max < 1e-5f) return;

            float k = peak / max;
            for (int i = 0; i < d.Length; i++) d[i] *= k;
        }

        /// <summary>
        /// 消除循环接缝处的「咔哒」。
        ///
        /// 做法：把尾部 n 个样本与开头 n 个样本交叉混合后**写回开头**，然后把长度缩短 n。
        /// 关键在最后一步 —— 缩短后新的 d[newLen-1] 和 d[0] 在原序列里本来就是相邻样本，
        /// 所以接缝处天然连续。
        ///
        /// 反面教材（旧实现）：只把尾段向 head 混合、却不缩短长度，
        /// 结果尾段末尾趋近 d[n-1] 而循环回到 d[0]，跳变依旧存在 —— 等于白做。
        /// </summary>
        private static float[] CrossfadeLoop(float[] d, int n)
        {
            if (n <= 0 || n * 2 > d.Length) return d;

            int newLen = d.Length - n;
            for (int i = 0; i < n; i++)
            {
                float k = i / (float)n;              // 0 → 1
                d[i] = d[i] * k + d[newLen + i] * (1f - k);
            }
            System.Array.Resize(ref d, newLen);
            return d;
        }
    }
}
```

### A.9  WeatherPanel.cs

原始路径：`Assets/UI_Systems/Scripts/UI/WeatherPanel.cs`

```csharp
using UnityEngine;
using UnityEngine.UI;
using CampusUI.Core;
using CampusUI.Environment;

namespace CampusUI.UI
{
    /// <summary>
    /// 两级天气选择面板：一级「白天 / 夜晚」，二级「晴 / 多云 / 阴 / 雨 / 雷雨 / 雾 / 雪」。
    ///
    /// 做成独立控件的原因：数字孪生（左面板 148 宽）和第一人称（设置弹窗 308 宽）
    /// 都要这个功能，但可用宽度差一倍。所以宽度、列数、字号全部由调用方传进来，
    /// 内部按格子均分，不写死任何尺寸。
    ///
    /// 用法：
    ///   var p = WeatherPanel.Create(parentRt, 16f, y, 148f, 3, 11);
    ///   y += p.Height;
    /// </summary>
    public class WeatherPanel : MonoBehaviour
    {
        /// <summary>面板实际占用的高度，调用方用它推进 y 坐标。</summary>
        public float Height { get; private set; }

        private static readonly TimeOfDay[] Times = { TimeOfDay.Day, TimeOfDay.Night };

        private static readonly Weather[] All =
        {
            Weather.Clear, Weather.Cloudy, Weather.Overcast, Weather.Rain,
            Weather.Storm, Weather.Fog, Weather.Snow
        };

        private EnvironmentState env;
        private bool subscribed;
        private Button[] timeBtns;
        private Button[] wBtns;
        private Button autoBtn;

        // =====================================================================

        public static WeatherPanel Create(Transform parent, float x, float y, float width, int columns, int fontSize)
        {
            var go = new GameObject("WeatherPanel");
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, -y);

            var p = go.AddComponent<WeatherPanel>();
            p.Build(rt, width, Mathf.Clamp(columns, 2, 5), Mathf.Clamp(fontSize, 10, 16));
            return p;
        }

        private void Build(RectTransform rt, float width, int columns, int fontSize)
        {
            const float Gap = 6f;
            float cellH = fontSize + 13f;

            // ---- 第 0 行：自动轮换开关（对应《异环》的「随机模式」）----
            // 放在最上面，因为它是一个"模式"而不是某一档天气，混在天气网格里会让人误以为
            // 「自动」是第八种天气。
            autoBtn = UIStyle.Button(rt, 0f, 0f, width, cellH, AutoLabel(false),
                                     UITheme.Bg3, UITheme.TextDim, fontSize);
            autoBtn.onClick.AddListener(ToggleAuto);

            // ---- 第 1 行：白天 / 夜晚 ----
            float yTime = cellH + Gap;
            float tabW = (width - Gap) * 0.5f;
            timeBtns = new Button[Times.Length];
            for (int i = 0; i < Times.Length; i++)
            {
                int idx = i;
                var b = UIStyle.Button(rt, i * (tabW + Gap), yTime, tabW, cellH,
                                       EnvironmentState.TimeLabel(Times[i]),
                                       UITheme.Bg3, UITheme.TextDim, fontSize);
                b.onClick.AddListener(() => PickTime(idx));
                timeBtns[i] = b;
            }

            // ---- 天气网格 ----
            float cellW = (width - Gap * (columns - 1)) / columns;
            float y0 = yTime + cellH + Gap;
            wBtns = new Button[All.Length];
            for (int i = 0; i < All.Length; i++)
            {
                int idx = i;
                int cx = i % columns, cy = i / columns;
                var b = UIStyle.Button(rt, cx * (cellW + Gap), y0 + cy * (cellH + Gap), cellW, cellH,
                                       EnvironmentState.WeatherLabel(All[i]),
                                       UITheme.Bg3, UITheme.TextDim, fontSize);
                b.onClick.AddListener(() => PickWeather(idx));
                wBtns[i] = b;
            }

            int rows = (All.Length + columns - 1) / columns;
            Height = y0 + rows * cellH + (rows - 1) * Gap;
            rt.sizeDelta = new Vector2(width, Height);

            Subscribe();
            Refresh();
        }

        private static string AutoLabel(bool on)
        {
            return on ? "自动轮换 · 开" : "自动轮换 · 关";
        }

        // =====================================================================

        private EnvironmentState Env
        {
            get
            {
                if (env == null)
                {
                    env = EnvironmentState.Instance;
                    if (env == null) env = Object.FindObjectOfType<EnvironmentState>();
                    if (env == null)
                    {
                        var go = new GameObject("EnvironmentState");
                        env = go.AddComponent<EnvironmentState>();
                    }
                    Subscribe();
                }
                return env;
            }
        }

        private void Subscribe()
        {
            if (subscribed || env == null) return;
            env.OnChanged += OnEnvChanged;
            subscribed = true;
        }

        private void OnDestroy()
        {
            if (subscribed && env != null) env.OnChanged -= OnEnvChanged;
        }

        private void OnEnable()
        {
            Refresh();
        }

        private void OnEnvChanged(TimeOfDay t, Weather w)
        {
            Refresh();
        }

        private void PickTime(int i)
        {
            var e = Env;
            if (e == null) return;
            e.SetTime(Times[i]);
            Refresh();
        }

        private void PickWeather(int i)
        {
            var e = Env;
            if (e == null) return;

            // 手动选天气视为"接管控制权"：自动轮换自动退出。
            // 否则玩家刚选完晴天，40 秒后又被翻成雨天，会以为按钮没生效。
            var d = WeatherDirector.Instance;
            if (d != null && d.auto) d.SetAuto(false);

            e.SetWeather(All[i]);
            Refresh();
        }

        private void ToggleAuto()
        {
            var d = WeatherDirector.Ensure();
            d.SetAuto(!d.auto);
            Refresh();
        }

        // =====================================================================

        /// <summary>按当前环境状态刷新选中高亮。</summary>
        public void Refresh()
        {
            // AddComponent 会先触发 OnEnable，那时 Build 还没跑、按钮数组还是 null。
            // 这里直接返回，避免在 Build 之前就去摸 EnvironmentState（会提前创建它）。
            if (timeBtns == null && wBtns == null) return;

            var e = Env;
            if (e == null) return;

            if (timeBtns != null)
            {
                for (int i = 0; i < timeBtns.Length; i++)
                    Mark(timeBtns[i], e.timeOfDay == Times[i], UITheme.Blue);
            }
            if (wBtns != null)
            {
                for (int i = 0; i < wBtns.Length; i++)
                    Mark(wBtns[i], e.weather == All[i], UITheme.Teal);
            }

            // ---- 自动轮换开关 ----
            var d = WeatherDirector.Instance;
            bool autoOn = d != null && d.auto;
            if (autoBtn != null)
            {
                Mark(autoBtn, autoOn, UITheme.Amber);
                var at = autoBtn.GetComponentInChildren<Text>();
                if (at != null) at.text = AutoLabel(autoOn);
            }
        }

        private static void Mark(Button b, bool on, Color accent)
        {
            if (b == null) return;

            var cb = b.colors;
            cb.normalColor = on ? accent : UITheme.Bg3;
            cb.selectedColor = cb.normalColor;
            cb.highlightedColor = on ? UIStyle.Lighten(accent, 0.12f) : UITheme.Bg2;
            cb.pressedColor = on ? UIStyle.Darken(accent, 0.15f) : UITheme.Bg1;
            b.colors = cb;

            var t = b.GetComponentInChildren<Text>();
            if (t != null) t.color = on ? UITheme.Text : UITheme.TextDim;
        }
    }
}
```

### A.10  SnowGround.shader

原始路径：`Assets/UI_Systems/Resources/SnowGround.shader`

```hlsl
// 地面积雪层。把原来 Sprites/Default 无光照的"灰绿平涂"换成受天光影响的雪。
//
// 针对"雪太假 / 看着发虚、一块一块"的根因：
//   1) 原版用 Sprites/Default（完全不受光），整片雪是死板的灰绿平涂。
//      现在改成：主光 Lambert 直射（含主光实时阴影）+ 球谐(SH)环境光 + 环境提亮下限。
//   2) 原版用"噪声 × 径向淡出"调制覆盖率 alpha，导致雪一块一块、草地颜色透出来发灰显"虚"。
//      现在覆盖率 alpha 改为实心（只有最外圈淡出），噪声改存 RGB，只做极轻微的颜色/法线扰动。
//   3) 雪面严格按地形平铺（几何位移由 C# 端逐顶点向下射线完成），不再叠加任何程序化雪堆。
//   4) 雪量用 smoothstep 快速变实：下雪后很快就是"不透地的白"，同时保留由淡到浓的渐进手感。

Shader "CampusUI/SnowGround"
{
    Properties
    {
        _SnowTex    ("Snow Coverage (A)", 2D) = "white" {}
        _SnowColor  ("Snow Tint", Color) = (0.92, 0.95, 1.0, 1)
        _SnowAmount ("Snow Amount", Range(0, 1)) = 0
        _SkyColor   ("Sky Tint", Color) = (0.74, 0.82, 0.96, 1)
        _EdgeFade   ("Edge Fade", Range(0.2, 1.2)) = 0.55
        _MicroNormal("Micro Normal", Range(0, 1)) = 0.35
        _AmbientBoost("Ambient Boost", Range(0, 1)) = 0.32
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "RenderType" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "SnowGround"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag

            // 接收主光实时阴影（角色/树的影子落在雪上）：自定义 shader 必须显式带这个变体。
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            // 雾：雪天预设会自动开 ExponentialSquared 雾，这里让雪色也参与雾混合，雪盘远边融进雾里。
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _SnowTex_ST;
                float4 _SnowColor;
                float  _SnowAmount;
                float4 _SkyColor;
                float  _EdgeFade;
                float  _MicroNormal;
                float  _AmbientBoost;
            CBUFFER_END

            TEXTURE2D(_SnowTex);
            SAMPLER(sampler_SnowTex);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float3 normalWS   : TEXCOORD2;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                float3 ws = TransformObjectToWorld(v.positionOS.xyz);
                o.positionWS = ws;
                o.positionCS = TransformWorldToHClip(ws);
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.uv = TRANSFORM_TEX(v.uv, _SnowTex);
                return o;
            }

            float4 frag(Varyings i) : SV_Target
            {
                // 采样：a = 覆盖率（实心 + 最外圈淡出），r = 颗粒噪声（只做极轻微的质感扰动）
                float4 sn = SAMPLE_TEXTURE2D(_SnowTex, sampler_SnowTex, i.uv);
                float cov = sn.a;
                if (cov <= 0.003) discard;

                // 边缘淡出（双保险，贴图里已经淡过一次，这里再收一次方形边界）
                float2 c = i.uv - 0.5;
                float r = length(c) * 2.0;
                float edge = 1.0 - smoothstep(_EdgeFade, 1.0, r);
                if (edge <= 0.003) discard;

                // 微法线：用颗粒噪声的空间梯度扰动法线。梯度取噪声(.r)而不是覆盖率(.a) ——
                // 覆盖率现在是实心的，取 .a 的梯度恒为 0。幅度刻意压得很小：
                // 目标是"平铺干净的雪面"，不是雪花噪点。
                float gDX = ddx(sn.r);
                float gDY = ddy(sn.r);
                float3 N = normalize(i.normalWS + float3(-gDX, 0.0, -gDY) * _MicroNormal * 2.5);

                // 受光：主光方向 Lambert + 接收主光实时阴影（角色/树的影子落在雪上）。
                // TransformWorldToShadowCoord 把世界坐标变换到阴影图空间，GetMainLight(shadowCoord)
                // 顺带算出 shadowAttenuation；没有 _MAIN_LIGHT_SHADOWS 变体时 MainLightRealtimeShadow
                // 直接返回 1.0，安全兜底（不会让雪变黑）。
                float4 shadowCoord = TransformWorldToShadowCoord(i.positionWS);
                Light mainLight = GetMainLight(shadowCoord);
                float NdotL = saturate(dot(N, mainLight.direction));
                float3 direct = mainLight.color * NdotL * mainLight.shadowAttenuation;

                // 环境光：球谐(SH) 已含天空/环境颜色，白天亮、阴天灰、夜晚偏蓝。
                float3 ambient = SampleSH(N);

                // 合成：雪色 × (直射 + 环境 + 环境提亮下限)。
                // 雪是强反照率材质，阴天弱光下只按物理直射+环境算会发灰发暗（看着"虚"），
                // 所以给一个提亮下限，保证弱光下雪依然是"白实"的。
                float3 lit = direct + ambient + _AmbientBoost;
                float3 col = _SnowColor.rgb * lit;

                // 极轻微的颜色扰动（幅度 2.5%），避免大面积纯白显得死板。
                col *= (0.975 + 0.025 * sn.r);

                // 远处略偏天空色，强化"雪反射天光"的通透感（雪不是纯白死板色）。
                col = lerp(col, _SkyColor.rgb * (0.6 + 0.4 * lit), 0.22);

                // 雾：让远处雪面和场景雾色融在一起，雪盘远边不再" popping "出白色硬边。
                // 雪天预设会自动开雾（ExponentialSquared），这里把雪色送进 MixFog。
                float fogFactor = ComputeFogFactor(i.positionCS.z);
                col = MixFog(col, fogFactor);

                // 覆盖率已是实心：雪量上来后立刻变成"不透地的白"，草地的颜色不会透出来。
                // smoothstep 让刚开始下的一小段时间里由淡到浓，保留"渐进出现"的手感。
                float a = saturate(smoothstep(0.0, 0.22, _SnowAmount) * cov * edge);
                return float4(col, a);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
```

### A.11  WetGround.shader

原始路径：`Assets/UI_Systems/Resources/WetGround.shader`

```hlsl
// 雨天积水层。参考《异环》雨天"地面积水 + 环境倒影"的表现。
//
// 为什么不用屏幕空间反射（SSPR）：
//   URP 下要么开 Opaque Texture 再做深度重建 + 屏幕步进，要么自写 Renderer Feature，
//   两者都要额外一次全屏拷贝/多个渲染通道，低端机上不稳，而且我们只需要"地面反光"这一个效果。
//   环境立方图（unity_SpecCube0）已经能给出反射方向上的天空与远景，
//   场景里放了反射探针时还会自动包含建筑 —— 视觉上足够，成本几乎为零。
//
// 关键细节（都是容易踩的）：
//   1) R.y 取绝对值。涟漪扰动可能让反射方向朝下，采到立方图下半球会突然变暗一圈。
//   2) unity_SpecCube0 在没绑探针、且默认反射模式异常时是纯黑，直接采会得到"黑水"。
//      用亮度阈值 0.002 判断并退回解析天空色。夜里合法天空的亮度远高于这个阈值。
//   3) 涟漪是程序化算的（3 层不同缩放的分格哈希），不额外占贴图，也看不出网格规律。
//   4) 边缘淡出写在 shader 里，所以遮罩贴图不需要是周期的，平面也看不到方形边界。

Shader "CampusUI/WetGround"
{
    Properties
    {
        _MaskTex ("Puddle Mask (R)", 2D) = "black" {}
        _WaterColor ("Water Tint", Color) = (0.05, 0.07, 0.09, 1)
        _SkyColor ("Sky Zenith", Color) = (0.42, 0.55, 0.78, 1)
        _HorizonColor ("Sky Horizon", Color) = (0.70, 0.75, 0.80, 1)
        _Wetness ("Wetness", Range(0, 1)) = 0
        _ReflectStrength ("Reflect Strength", Range(0, 2)) = 1
        _EnvReflect ("Env Probe Weight", Range(0, 1)) = 0.80
        _Gloss ("Gloss (cube mip)", Range(0, 6)) = 0.6
        _RippleStrength ("Ripple Strength", Range(0, 2)) = 1
        _RippleScale ("Ripple Scale", Float) = 1.6
        _RippleSpeed ("Ripple Speed", Float) = 1
        _FresnelPower ("Fresnel Power", Float) = 5
        _EdgeFade ("Edge Fade", Range(0.2, 1.2)) = 0.55
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "RenderType" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "WetGround"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/EntityLighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _MaskTex_ST;
                float4 _WaterColor;
                float4 _SkyColor;
                float4 _HorizonColor;
                float  _Wetness;
                float  _ReflectStrength;
                float  _EnvReflect;
                float  _Gloss;
                float  _RippleStrength;
                float  _RippleScale;
                float  _RippleSpeed;
                float  _FresnelPower;
                float  _EdgeFade;
            CBUFFER_END

            TEXTURE2D(_MaskTex);
            SAMPLER(sampler_MaskTex);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
            };

            float2 Hash22(float2 p)
            {
                float2 q = float2(dot(p, float2(127.1, 311.7)),
                                  dot(p, float2(269.5, 183.3)));
                return frac(sin(q) * 43758.5453123);
            }

            // 雨滴打在水面上的同心涟漪。做法：把世界 XZ 分格，每格一个随机中心 + 随机相位，
            // 用 sin(distance - phase) 得到向外扩散的环；三层不同缩放叠加，打散网格规律。
            float2 RainRipple(float2 p, float t)
            {
                float2 acc = float2(0.0, 0.0);

                [unroll]
                for (int i = 0; i < 3; i++)
                {
                    float fi = (float)i;
                    float2 q = p * (1.0 + fi * 0.85) + fi * 19.37;
                    float2 cell = floor(q);
                    float2 f = frac(q);

                    float2 h = Hash22(cell + fi * 7.1);
                    float2 ctr = 0.25 + h * 0.5;

                    float2 d2 = f - ctr;
                    float d = length(d2);

                    float ph = frac(t * (0.5 + h.y * 0.7) + h.x);
                    float ring = sin((d - ph * 0.7) * 34.0);
                    float band = saturate(1.0 - abs(d - ph * 0.7) * 5.0);
                    float fade = band * (1.0 - ph) * saturate(1.0 - d * 1.7);

                    acc += normalize(d2 + 1e-4) * ring * fade;
                }

                return acc * 0.45;
            }

            Varyings vert(Attributes v)
            {
                Varyings o;
                float3 ws = TransformObjectToWorld(v.positionOS.xyz);
                o.positionWS = ws;
                o.positionCS = TransformWorldToHClip(ws);
                o.uv = TRANSFORM_TEX(v.uv, _MaskTex);
                return o;
            }

            float4 frag(Varyings i) : SV_Target
            {
                float4 mask = SAMPLE_TEXTURE2D(_MaskTex, sampler_MaskTex, i.uv);
                float puddle = mask.r;

                // 平面 UV 到中心的距离 → 边缘淡出，避免看到一块方形水膜
                float2 c = i.uv - 0.5;
                float r = length(c) * 2.0;
                float edge = 1.0 - smoothstep(_EdgeFade, 1.0, r);
                if (edge <= 0.002) discard;

                // 涟漪法线
                float2 rip = RainRipple(i.positionWS.xz * _RippleScale, _Time.y * _RippleSpeed);
                float3 N = normalize(float3(rip.x * 0.30 * _RippleStrength,
                                            1.0,
                                            rip.y * 0.30 * _RippleStrength));

                float3 V = normalize(_WorldSpaceCameraPos - i.positionWS);
                float fres = pow(saturate(1.0 - saturate(dot(N, V))), _FresnelPower);

                // 反射方向（水面朝下的一半没有意义，取绝对值）
                float3 R = reflect(-V, N);
                R.y = abs(R.y);

                // 解析天空：反射方向越朝上越接近天顶色
                float3 sky = lerp(_HorizonColor.rgb, _SkyColor.rgb, saturate(R.y * 1.5));

                half4 enc = SAMPLE_TEXTURECUBE_LOD(unity_SpecCube0, samplerunity_SpecCube0, R, _Gloss);
                float3 env = DecodeHDREnvironment(enc, unity_SpecCube0_HDR);

                float lum = dot(env, float3(0.299, 0.587, 0.114));
                env = (lum < 0.002) ? sky : min(env, 6.0);   // 探针没绑定时是黑的，退回解析天空

                float3 refl = lerp(sky, env, _EnvReflect);
                float3 col = lerp(_WaterColor.rgb, refl, saturate(fres * 1.6 + 0.12) * _ReflectStrength);

                // 被水覆盖的漫反射会变暗，整体压一点
                col *= lerp(1.0, 0.82, _Wetness);

                // 水洼处几乎全不透明，其余地方只留一层薄薄湿光
                float a = _Wetness * saturate(0.10 + 0.95 * puddle) * edge;
                return float4(col, saturate(a));
            }
            ENDHLSL
        }
    }

    FallBack Off
}
```

### A.12  CloudDome.shader

原始路径：`Assets/UI_Systems/Resources/CloudDome.shader`

```hlsl
// Volumetric-looking cloud dome, sampled from a 3D noise texture.
//
// History of what went wrong before this:
//   1. Spherical UVs  -> all longitudes collapse at the poles, texture is
//      stretched infinitely there, giving radial streaks.
//   2. Triplanar 2D   -> no poles, but the projection is cubic: the pattern
//      repeats once per 90 degrees and the blend weight (a*a) makes the
//      boundaries between faces show up as a visible grid of tiles.
//   3. 3D noise (this) -> the view direction indexes a continuous volume, so
//      there is no pole, no seam and no tiling. The only cost is one tex3D.
//
// The texture is sampled with Clamp wrap and the lookup sphere is kept well
// inside [0,1]^3, so the texture never has to be periodic.

Shader "CampusUI/CloudDome"
{
    Properties
    {
        _Noise3D ("Cloud Volume (alpha = density)", 3D) = "" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _Opacity ("Opacity", Range(0,1)) = 0.5
        _Radius ("Sample Radius", Range(0.05,0.48)) = 0.34
        _Speed ("Drift", Float) = 0.02
        _HorizonFade ("Horizon Fade", Range(0,1)) = 0.30
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "RenderType" = "Transparent"
            "IgnoreProjector" = "True"
        }

        Cull Off
        ZWrite Off
        ZTest LEqual
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #include "UnityCG.cginc"

            sampler3D _Noise3D;
            float4 _Color;
            float _Opacity;
            float _Radius;
            float _Speed;
            float _HorizonFade;

            struct appdata
            {
                float4 vertex : POSITION;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 dir : TEXCOORD0;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                // Dome is centred on the camera, so object space direction == view direction.
                o.dir = v.vertex.xyz;
                return o;
            }

            float3 RotateY(float3 p, float a)
            {
                float s = sin(a), c = cos(a);
                return float3(p.x * c - p.z * s, p.y, p.x * s + p.z * c);
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float3 d = normalize(i.dir);
                float t = _Time.y * _Speed;

                // Drift by slowly rotating the lookup direction instead of
                // translating the sample point: translation would eventually
                // push the lookup sphere outside [0,1]^3 and reveal the border.
                float3 d2 = RotateY(d, t);
                float3 p = d2 * _Radius + 0.5;

                float c = tex3D(_Noise3D, p).a;

                // Fade below the horizon so the dome never shows under the ground.
                float horizon = smoothstep(-_HorizonFade, 0.02, d.y);

                return fixed4(_Color.rgb, saturate(c * _Opacity * horizon));
            }
            ENDCG
        }
    }

    Fallback Off
}
```

### A.13  SkyDome.shader

原始路径：`Assets/UI_Systems/Resources/SkyDome.shader`

```hlsl
// Sky dome shader used by the procedural weather system.
//
// Why this exists: a normal sphere has spherical UVs, and all longitudes collapse
// into a single point at the poles. Any texture mapped that way gets stretched
// infinitely near the poles, which shows up as radial streaks across the sky.
// This shader ignores the mesh UVs entirely and instead projects the view
// direction triplanar, so there is no pole singularity and no u/v seam.
//
// Used for both the cloud layer and the star field (same shader, different texture).

Shader "CampusUI/SkyDome"
{
    Properties
    {
        _MainTex ("Dome Texture (alpha = density)", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _Opacity ("Opacity", Range(0,1)) = 0.5
        _Scale ("Tiling", Float) = 1.4
        _Speed ("Drift Speed", Float) = 0.008
        _HorizonFade ("Horizon Fade", Range(0,1)) = 0.25
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Background"
            "RenderType" = "Background"
            "IgnoreProjector" = "True"
            "PreviewType" = "Skybox"
        }

        Cull Off
        ZWrite Off
        ZTest LEqual
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _Color;
            float _Opacity;
            float _Scale;
            float _Speed;
            float _HorizonFade;

            struct appdata
            {
                float4 vertex : POSITION;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 dir : TEXCOORD0;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                // The dome is a sphere centred on the camera, so an object space
                // vertex direction is also the view direction for that pixel.
                o.dir = v.vertex.xyz;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float3 d = normalize(i.dir);
                float3 a = abs(d);
                float t = _Time.y * _Speed;

                // Triplanar sampling. The max() clamp keeps the division stable
                // near the axes; the axis-aligned weights below fade those
                // samples out anyway.
                float2 uvX = d.zy / max(a.x, 0.08);
                float2 uvY = d.xz / max(a.y, 0.08);
                float2 uvZ = d.xy / max(a.z, 0.08);

                float cX = tex2D(_MainTex, uvX * _Scale + t).a;
                float cY = tex2D(_MainTex, uvY * _Scale + t).a;
                float cZ = tex2D(_MainTex, uvZ * _Scale + t).a;

                float3 w = a * a;
                w /= max(1e-4, w.x + w.y + w.z);
                float c = cX * w.x + cY * w.y + cZ * w.z;

                // Fade out below the horizon so the dome never appears to be
                // painted underneath the ground plane when looking down.
                float horizon = smoothstep(-_HorizonFade, 0.02, d.y);

                return fixed4(_Color.rgb, saturate(c * _Opacity * horizon));
            }
            ENDCG
        }
    }

    Fallback Off
}
```


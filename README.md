# 🎮 校园数字孪生管理系统

> 基于 Unity 2022.3.62f3c1 的可运行数字孪生原型——一栋教学楼、363 个设备点位、五大管理模块、15 分钟昼夜循环，**断网也能完整演示**。

[![Unity](https://img.shields.io/badge/Unity-2022.3.62f3c1-black?logo=unity)](https://unity.com/)
[![Render Pipeline](https://img.shields.io/badge/Pipeline-Built--in-blue)](https://docs.unity3d.com/Manual/RenderTech-BuiltInPipeline.html)
[![Language](https://img.shields.io/badge/Language-C%23-purple)](https://learn.microsoft.com/en-us/dotnet/csharp/)
[![Platform](https://img.shields.io/badge/Platform-Windows-lightgrey)](https://www.microsoft.com/windows)
[![Points](https://img.shields.io/badge/Device%20Points-363-orange)](#-数据规模)
[![Checks](https://img.shields.io/badge/Checks-428-green)](#-质量保障)

---

## 📸 用画面看懂能力

> 以下为本工程的真实运行截图，展示的是原型系统能力，不代表任何真实校园的现场实时数据。

### 昼夜天气仿真

| 晴天白天 | 雨天 | 雪天（夜间） |
|:---:|:---:|:---:|
| ![晴天](Docs/images/scene-clear-day.png) | ![雨天](Docs/images/scene-rain.png) | ![雪天](Docs/images/scene-snow.png) |

15 分钟压缩 24 小时昼夜循环，7 档天气（晴/多云/阴/雨/暴雨/雾/雪）按转移链随机演变，天空与光照 2.4 秒平滑过渡。

### 室内光照与细节

![室内光照](Docs/images/indoor-lighting.png)

室内照明分层（底光层 + 洗顶灯 + 灯管发光），配合过曝修复与材质调校，兼顾明暗层次与可读性。

---

## 🚀 核心亮点

- **五大管理模块全部打通**：数据监测 / 设备管理 / 电力管理 / 告警中心 / 数据统计，三级面板结构（总控 → 管理系统 → 五模块）
- **昼夜天气仿真引擎**：15 分钟 = 24 小时，天气随机转移 + 物理派生数据（温度/湿度/光照随天气与时刻变化）
- **363 个设备点位**：覆盖 11 类设备、18 个分区，走近触发交互、开关控制与状态回读
- **20 节点三级电力回路**：开关操作返回受影响设备清单与倒计时
- **三态漫游**：第一人称 / 第三人称 / 观景俯视
- **离线可用**：Mock 数据源 + MQTT over WebSocket 双通道，断网自动兜底，无需任何外部服务即可运行

---

## 🏗 系统架构

![系统总体架构](Docs/images/architecture-overview.png)

数据从设备点位经事件总线进入表现层与控制中心；时间天气系统同时驱动视觉侧（光照/天空）与数据侧（温湿度等物理量），由桥接层统一收口，避免两侧状态分叉。

### 五模块信息架构

![五模块信息架构](Docs/images/architecture-modules.png)

### 时间天气双通道

![时间天气双通道](Docs/images/architecture-weather.png)

---

## 📊 数据规模

| 指标 | 数量 | 说明 |
|---|---:|---|
| 设备点位 | **363** | 照明 240 / 温控 60 / 电脑 27 / 门 12 / 时钟 7 / 空调 5 / 传感器 4 / 投影仪 4 / 饮水机 2 / 警铃 1 / 告警 1 |
| 设备类型 | 11 | 覆盖照明、温控、算力、门禁、给水、环境等 |
| 分区 | 18 | 楼内房间 + 室外 |
| 业务脚本 | 117 | C#，按 Command/Data/Twin/UI 等职责分目录 |
| 自定义 Shader | 10 | 雨雪距离场提亮、积雪裁形、高光抑制等 |
| 机器可验证判据 | 428 | 代码存在性 + 场景配置 + 数据口径 |

---

## 🛠 技术栈

| 层 | 选型 |
|---|---|
| 引擎 | Unity 2022.3.62f3c1，Built-in 渲染管线，Linear 色彩空间 |
| 语言 | C#（Unity Mono） |
| UI | UGUI + TextMeshPro，自研 TwinTheme token 体系（科技蓝玻璃拟态） |
| 后处理 | Post Processing Stack v2（Bloom / Neutral Tonemapper） |
| 数据 | MQTT over WebSocket + 本地 Mock 双数据源，Newtonsoft.Json |
| 渲染 | 自定义 Shader（雨雪粒子散射、积雪裁形、地面高光抑制） |
| 物理 | ParticleSystem + MeshCollider 射线判定（雨雪/脚印/积水） |

---

## 📦 快速开始

### 环境

- Unity Hub + **Unity 2022.3.62f3c1**
- Windows 10/11（PC 平台）
- 无需后端服务、无需网络（默认 Mock 模式）

### 运行

1. Clone 本仓库
2. Unity Hub → Open → 选择工程根目录
3. 打开场景 `Assets/TirGames/SchoolScene/Scenes/SchoolSceneDay.unity`
4. 点击 Play——默认进入离线 Mock 模式，数据即刻可读

> 场景清单：`SchoolSceneDay`（主场景）、`SchoolSceneNight`、`SchoolSceneMenu`、`SchoolSceneAbandoned`。

### 操作

| 操作 | 方式 |
|---|---|
| 漫游 | WASD 移动，鼠标转向；Q/E 升降，滚轮推进（观景模式） |
| 设备交互 | 走近设备 → 出现交互提示 → 点击菜单开关 / 查看数据 |
| 系统面板 | 总控面板 → 「校园管理系统」→ 五大模块 |
| 时间天气 | 工具行打开时间天气卡，拖动表盘或切换天气档 |

---

## ✅ 质量保障

- **428 条机器可验证判据**（`Tools/status_checks.txt`），覆盖代码存在性、场景配置与数据口径
- **100 条踩坑台账 + 10 份专题复盘**，统一记录「现象 → 根因 → 修法 → 教训」
- 运行 `python Tools/status.py` 刷新看板 `Docs/STATUS.md`

> 判据验证的是「工程结构与配置是否守住」，视觉与交互体验以实际运行为准。

---

## 📁 主要目录结构

```
Assets/
├── TirGames/SchoolScene/     # 场景、建筑模型、材质、贴图
└── _Project/Scripts/
    ├── Command/              # 命令链路与控制中心
    ├── Data/                 # 数据源与物理建模
    ├── Interaction/          # 漫游与交互
    ├── Present/              # 设备表现层
    ├── Render/               # 光照与渲染
    ├── Twin/                 # 测点锚点与目录
    ├── UI/                   # UGUI 界面
    └── Weather/              # 时间天气系统
Docs/images/                  # 本 README 的配图
Docs/                         # 方案、评审、验收证据
Tools/                        # 状态检查脚本
```

---

## 📖 文档索引

| 主题 | 路径 |
|---|---|
| 完整成果汇总 | `Docs/成果汇总_定盘.md` |
| 项目基线与技术坑 | `Docs/P00_项目总览_交接基线.md` |
| 发黑问题经验总纲 | `Docs/发黑问题修复经验总纲.md` |
| 踩坑日志（示例）| `Docs/P42_餐厅梁柱死黑_踩坑日志.md`、`Docs/P43_树冠覆雪与白片_踩坑日志.md` |
| 验收证据 | `Docs/QA/evidence/` |

---

> 本项目为可运行原型系统，代码与文档按职责模块组织，适合作为 Unity 数字孪生 / 仿真系统的工程参考。

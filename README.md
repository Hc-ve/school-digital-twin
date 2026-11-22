# 校园数字孪生管理系统

基于 Unity 2022.3.62f3c1 开发的可运行数字孪生原型系统。以一栋校园教学楼为载体，实现设备监测管控、昼夜天气仿真、第一人称漫游与离线演示，所有数据均可在无网络环境下运行。

![主场景](Docs/QA/evidence/P40_clear_day.png)

## 核心能力

- **管理系统五大模块**：数据监测 / 设备管理 / 电力管理 / 告警中心 / 数据统计，三级面板结构（总控 → 管理系统 → 五模块）
- **昼夜天气仿真**：15 分钟压缩 24 小时昼夜循环，晴/多云/阴/雨/暴雨/雾/雪七档天气按转移链随机演变，2.4 秒平滑过渡
- **设备物联与交互**：363 个设备点位覆盖 11 类设备、18 个分区；走近触发交互菜单，支持开关控制与状态回读
- **电力回路**：20 节点三级回路，开关操作返回受影响设备清单与倒计时
- **三态漫游**：第一人称 / 第三人称 / 观景俯视
- **离线可用**：Mock 数据源 + MQTT over WebSocket 双通道，断网时 Mock 自动兜底

## 数据规模

| 指标 | 数量 |
|---|---:|
| 设备点位 | 363 |
| 设备类型 | 11 |
| 分区 | 18 |
| 业务脚本（C#） | 117 |
| 自定义 Shader | 10 |
| 机器可验证判据 | 428 |

## 技术栈

| 层 | 选型 |
|---|---|
| 引擎 | Unity 2022.3.62f3c1，Built-in 渲染管线，Linear 色彩空间 |
| 语言 | C#（.NET / Unity Mono） |
| UI | UGUI + TextMeshPro，自研 TwinTheme token 体系 |
| 后处理 | Post Processing Stack v2（Bloom / Neutral Tonemapper） |
| 数据 | MQTT over WebSocket + 本地 Mock 双数据源，Newtonsoft.Json |
| 渲染 | 自定义 Shader（雨雪距离场提亮、积雪裁形、高光抑制）|

## 目录结构

```
Assets/
  TirGames/SchoolScene/     # 场景、建筑模型、材质、贴图
  _Project/
    Scripts/                # 全部业务代码
      Command/              # 命令链路与控制中心
      Data/                 # 数据源与物理建模
      Interaction/          # 漫游与交互
      Present/              # 设备表现层
      Render/               # 光照与渲染
      Twin/                 # 测点锚点与目录
      UI/                   # UGUI 界面
      Weather/              # 时间天气系统
    Resources/              # 运行时资源
ProjectSettings/            # 工程设置
Docs/                       # 方案、评审、验收证据
Tools/                      # 状态检查脚本
```

## 运行方式

1. Unity Hub 安装 **Unity 2022.3.62f3c1**
2. Clone 本仓库，用 Unity 打开工程根目录
3. 打开主场景 `Assets/TirGames/SchoolScene/Scenes/SchoolSceneDay.unity`
4. 直接 Play——默认进入 Mock 离线模式，无需配置网络

> 场景清单：`SchoolSceneDay`（主场景）、`SchoolSceneNight`、`SchoolSceneMenu`、`SchoolSceneAbandoned`。

## 操作指引

- **漫游**：WASD 移动，鼠标转向；Q/E 升降，滚轮推进（观景模式）
- **设备交互**：走近设备后出现交互提示，点击菜单执行开关 / 数据查看
- **系统面板**：通过总控面板进入「校园管理系统」，查看五大模块
- **时间天气**：在工具行打开时间天气卡，拖动表盘设定时刻或切换天气档

## 质量保障

- **428 条机器可验证判据**（`Tools/status_checks.txt`），覆盖代码存在性、场景配置与数据口径
- **100 条踩坑台账** + 10 份专题复盘，记录现象 → 根因 → 修法 → 教训
- 运行 `python Tools/status.py` 刷新项目看板 `Docs/STATUS.md`

## 证据截图

| 主题 | 路径 |
|---|---|
| 白天 / 雨天 / 雪天观感 | `Docs/QA/evidence/P40_*.png` |
| 室内光照与过曝修复 | `Docs/QA/evidence/P41_*.png`、`P42_*.png` |
| 树冠覆雪与白片修复 | `Docs/QA/evidence/P43_*.png` |
| 路灯与雪幕 | `Docs/QA/evidence/P50_*.png` |
| 完整成果汇总 | `Docs/成果汇总_定盘.md` |

---

> 本项目为可运行原型，代码与文档按模块组织，适合作为 Unity 数字孪生 / 仿真系统的工程参考。

# _Project —— 自研内容根目录

本目录用于存放**项目自研内容**，与第三方资源包（`Assets/TirGames/`）严格隔离。

> 规则：**永不修改 `Assets/TirGames/` 下的任何文件**。资源包升级或重导时，
> 对资源包的改动会全部丢失。所有定制都应通过本目录下的脚本、预制体变体或
> ScriptableObject 完成。

## 目录约定

| 目录 | 用途 | 说明 |
|------|------|------|
| `Scripts/` | 运行时 C# 脚本 | 按功能分子目录：`Core/`、`Data/`、`Twin/`、`UI/`、`Interaction/` |
| `Editor/` | 编辑器扩展脚本 | 必须放在名为 `Editor` 的目录下，否则打包会报错 |
| `Prefabs/` | 自研预制体 | 设备锚点、数据标签、UI 面板等 |
| `ScriptableObjects/` | 配置资产 | 孪生对象定义、点位映射表、告警规则 |
| `Data/` | 数据文件 | Mock 数据 JSON、点位表 CSV |
| `Scenes/` | 自研场景 | 如数字孪生主场景（基于 SchoolSceneDay 派生） |
| `Materials/` | 自研材质 | 数据驱动着色器、告警材质 |

## 命名约定（数字孪生对象 ID）

场景内孪生对象的命名统一采用：

```
<分区>/<类别>/<序号>
例：Class2-1/Sensor/Temp_01
    SportRoom1/Light/Main_02
    Corridor2/AC/Unit_01
```

分区名直接沿用资源包已有房间名（`Class2-1`、`SportRoom1`、`Corridor2`…），
可最大限度降低人工对齐成本。

## 当前阶段

**P0 基线治理** —— 详见工作区文档：
`E:\workBuddy\2026-10-14-16-03-22\unity-digital-twin\P0-基线治理\P0-基线治理实施计划.md`

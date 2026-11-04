# WeatherSystem_Package · 快速接入说明

本包是从 `D:\unityxiangmu\sx5`（SchoolScene 智慧校园，Unity 2022.3 + URP 14）导出的**完整天气系统**，
可直接拖入另一个 Unity 工程使用。完整原理、参数调优、踩坑记录见包内 `WeatherSystem_TechDoc.md`。

---

## 1. 包内容

```
WeatherSystem_Package/
├── README.md                        ← 本文件
├── WeatherSystem_TechDoc.md         ← 完整技术文档（含全部源码）
└── Assets/
    └── UI_Systems/
        ├── Resources/               ← 目录名必须叫 Resources（打包不被剥离）
        │   ├── SnowGround.shader    地面积雪（URP）
        │   ├── WetGround.shader     雨天积水（URP）
        │   ├── CloudDome.shader     3D 噪声云穹顶
        │   └── SkyDome.shader       三平面星空穹顶
        └── Scripts/
            ├── Core/
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

共 **12 个文件**（8 个 C# + 4 个 Shader），另需 2 个枚举（见第 3 步）。

---

## 2. 三步接入

### 第 1 步：拷贝

把 `Assets/UI_Systems` 整个目录拷到目标工程的 `Assets/` 下。

- 目录名 `UI_Systems` 可以改；
- **`Resources` 这个名字不能改**，否则打包后 Shader 会被剥离，材质全变粉。

### 第 2 步：补两个枚举

如果目标工程**没有** `Weather` / `TimeOfDay`，新建一个 `Models.cs` 放进去：

```csharp
namespace CampusUI.Core
{
    public enum TimeOfDay { Day, Dusk, Night }
    public enum Weather { Clear, Cloudy, Overcast, Rain, Storm, Fog, Snow }
}
```

如果已有，只把这两个枚举合并进你现有的文件即可（**注意命名空间必须是 `CampusUI.Core`**，或全局替换成你的）。

### 第 3 步：场景里挂一个组件

新建空物体 `Environment` → 挂 `EnvironmentState`。完成。

其余全部自动创建：`WeatherVisuals`、雨/雪粒子、云穹顶、星空、溅射、双层雪盘、积水层、树根积雪。

**场景只需满足**：

| 需要 | 说明 |
|---|---|
| 1 个 Directional Light | 按 `LightType.Directional` 自动查找 |
| 1 个启用的相机 | 用 `Camera.allCameras` 找，不用 `Camera.main` |
| 地形（可选） | 有种树才会生成树根雪堆 |

---

## 3. 验证

进 Play，Console 无红错。用 `WeatherPanel` 或代码调用：

```csharp
EnvironmentState.Instance.SetWeather(Weather.Snow);
```

应看到 2.4 秒内：天空变灰白 → 雾起 → 雪花飘 → 地面 12 秒渐白。

---

## 4. 可选接入点

| 位置 | 代码 | 作用 |
|---|---|---|
| 移动速度计算 | `env.MoveFactor`（0.65~1） | 雨 -10%、雪 -20% 移速 |
| 任意 UI 面板 | `WeatherPanel.Create(rt, x, y, width, cols, fontSize)` | 天气选择面板 |
| 自定义 Shader | `Shader.GetGlobalFloat("_GlobalSnow")` | 全局雪量，做本地化积雪 |

---

## 5. 注意事项

1. **必须是 URP**。`SnowGround.shader` / `WetGround.shader` 是 URP 专用；内置管线需改写（文档 §2.2 有对照表）。
2. **`WeatherPanel` 依赖 UI 工具类** `UIStyle` / `UITheme`。目标工程没有的话，把 `Build()` 里的 `UIStyle.Button(...)` 换成你自己的按钮工厂即可，布局逻辑不用动。
3. **命名空间是 `CampusUI.*`**。要改名请全局替换，注意 `WeatherVisuals.cs` 里有一处全限定调用 `CampusUI.Core.EnvironmentState.FindActiveCamera()`。
4. **不要手动往场景里摆天气物件**。它们全部由 `EnvironmentState.Awake()` 运行时创建，手动摆会重复。
5. **雪盘抬升量**（`SnowGroundLiftNear/Far` = 0.30/0.35m）是按"有高于地形的装饰铺装"调的。若目标工程地面是整块带碰撞体的地形，可降到 0.10~0.15m 避免浮空。

---

## 6. 文件与文档章节对照

| 文件 | 文档章节 |
|---|---|
| `EnvSample.cs` | §5 核心数据结构 |
| `EnvironmentState.cs` | §6 状态机 |
| `WeatherVisuals.cs` | §7 视觉层 |
| `WetGround.cs` | §8 积水层 |
| `SnowOnTrees.cs` | §9 树根积雪 |
| `WeatherDirector.cs` | §10 自动轮换 |
| `WeatherPanel.cs` | §11 UI 面板 |
| 4 个 Shader | §12 四个 Shader |
| `ProceduralAssets.cs` | §13 程序化资源 |
| — | §14 移植步骤 / §15 参数调优 / §16 踩坑 / §17 验收 |

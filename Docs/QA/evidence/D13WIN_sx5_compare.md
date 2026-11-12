# D13-WIN：School-pro 与 sx5 逐层对比报告

**日期**：2026-11-11
**目的**：查明「1F 走廊窗框发黑」是否为材质/模型导入参数缺失
**对照项目**：`E:\Unity Projects\sx5`（用户确认其白天校园场景材质完全正常）
**被查项目**：`E:\Unity Projects\School-pro`

---

## 一、一句话结论

**不缺任何导入参数。** 两个项目的材质、贴图引用、模型导入设置几乎逐字节一致。
sx5 之所以正常，不是因为它「多了某个参数」，而是因为 **URP 迁移时把 2017 年那批烘焙
光照贴图整个弃用了** —— 142 张 EXR 还躺在磁盘上，但已经没有任何对象引用它们。

此前十几个版本全在 `.exr` 里改像素，等于在修一份「正常项目根本不用的资产」。

---

## 二、逐层对比数据

### 2.1 资源层（结论：完全一致）

| 检查项 | School-pro | sx5 | 差异 |
|---|---|---|---|
| 材质文件数 | 270 | 270 | 0 |
| 同名材质 | 269 | — | — |
| 材质内贴图 GUID 差异 | — | — | **0**（无缺失贴图） |
| `m_DoubleSidedGI` 差异 | — | — | **0** |
| `m_LightmapFlags` 差异 | — | — | 15 个，均为树木/混凝土材质，与窗框无关 |
| FBX `.meta`（模型导入参数） | 245 | 245 | **0** |
| `Lightmap-*.exr.meta`（光图导入参数） | 47 | 47 | **0** |
| 全部同名 `.meta` | 1912 | — | 仅 66 个不同 |

那 66 个 `.meta` 差异的构成：

| 类型 | 数量 | 性质 |
|---|---|---|
| `.exr` | 42 | 全是 `ReflectionProbe-*.exr`，Unity 版本序列化格式差异（`serializedVersion 4 → 新版`），与窗框无关 |
| `.terrainlayer` | 14 | URP 地形层格式 |
| `.lighting` | 4 | 见 2.3 |
| `.unity` / `.asset` / `.png` / 目录 | 6 | 场景与 URP 配置 |

**没有任何一个 FBX、材质贴图、或光图的导入参数不同。**

### 2.2 材质内容（差异只来自渲染管线）

| 字段 | 差异数 | 说明 |
|---|---|---|
| `m_Shader` | 266 / 269 | `Standard` → `URP/Lit`（URP 官方升级器批量转换，非「缺失」） |
| `m_ValidKeywords` | 95 | 随 shader 变换（`_SPECULAR_SETUP` 等） |
| `m_CustomRenderQueue` / `m_LightmapFlags` | 15 | 树木 Cutout、混凝土等修复，与窗框无关 |

窗框材质 `BuildingWindows01.mat` 的贴图 GUID 两边完全相同：
`_MainTex 77729a58…`、`_MetallicGlossMap 3bb290db…`、`_SpecGlossMap 47f74beb…`。

### 2.3 光照层（★ 决定性差异）

| 检查项 | School-pro | sx5 |
|---|---|---|
| `SchoolSceneDaySettings.lighting` → `m_EnableBakedLightmaps` | **1** | **0** |
| 其他 3 个场景 `.lighting` 的同一项 | 1 | 1（说明 sx5 是**专门**针对 Day 场景关的） |
| `SchoolSceneDay/LightingData.asset` 大小 | **2,928,808 B**（2017 完整烘焙数据） | **20,296 B**（空壳） |
| `LightingData.asset` 内 lightmap 引用 | 有 | 无 |
| 场景 `m_AmbientIntensity` | 1 | 0.75 |
| Global Volume 后处理 | 无 | 有（Tonemapping / Bloom / 对比度 +10） |
| `m_ScaleInLightmap` 字段数 | 141 | 105 |
| `!u!23` MeshRenderer 组件数 | 74 | 37 |
| `m_ReceiveGI` 分布 | 74 × `1`(Lightmaps) | 38 × `1`(Lightmaps) |

两边 `m_ReceiveGI` 都写着 `Lightmaps`，但 sx5 的 LightingData 是空的 ——
**标记还在，数据没了，等于实际走纯实时光/环境光。**

### 2.4 项目层

| 检查项 | School-pro | sx5 |
|---|---|---|
| 渲染管线 | 内置（`m_CustomRenderPipeline: {fileID: 0}`） | URP 14.0.12 |
| 色彩空间 | Linear | Linear（一致） |
| `m_CurrentQuality` | 5 | 2 |
| `m_LightsUseLinearIntensity` | 0 | 1 |
| `m_LightsUseColorTemperature` | 0 | 1 |
| 灯光 | 69 盏（68 Baked + 1 Realtime） | 相同 |
| LightProbeGroup | 4 | 4 |

### 2.5 直接证据

sx5 自带的 `URP_MIGRATION_REPORT.md` 与 `URP_MATERIAL_FIX_REPORT.md` 原文：

> 「**光照数据**：场景使用内置管线烘焙的 Lighting Data Asset —— 该数据在 URP 下
> **无法复用，已清空**。」

> 「**烘焙光照**：原内置管线的光照贴图在 URP 下不可复用，**当前是纯实时光照**。
> 需要间接光和 AO 的话请重新烘焙。」

验证：sx5 场景目录里 `Lightmap-{0..23}_comp_light.exr` 与原项目 2017 原始备份
抽样逐字节一致（lm7 `3b00dbecd16d…`、lm15 `e1899e25d9d7…`）。
**文件都在，内容都没变，只是 sx5 不再引用。**

---

## 三、为什么之前所有尝试都失败

| 已有实验 | 结果 | 现在的解释 |
|---|---|---|
| B：目标图集全填中性灰 0.5 | 黑条消失 | 绕开了坏光图像素 |
| C：`lightmapIndex = -1` | 黑条消失，窗框呈均匀浅米白 | **完全脱离坏光图 = sx5 的实际状态** |
| D：纯白 Unlit 材质 | 黑条消失 | 不采样光图 |
| R1 mask 内 <0.05 染红 | 3/4 黑条变红 | 光图确实参与渲染 |
| R4 bbox 内 mask 外染红 | 4 条全变红 | 采样点落在 UV 岛外 |
| v1~v3 改 EXR 像素 | 无效 | 修错了对象层级 |
| padding 延展 30138 纹素 | 无效 | 同上；且这批数据整体不可复用 |

`D13WIND_C_nolm_win_east.png` 已经给出了答案：断掉光图后，窗框是**均匀的浅米白色**，
与用户提供的「正常窗框材质」参照图一致。当时误判为「只是排除法对照」，没意识到
它就是修复方案本身。

**真正根因**：2017 年用旧版 Enlighten 烘焙的光照贴图，UV 岛缺 padding、
岛内有大面积纯黑（mask 外围 1~6 环 50.7% 为黑），数据本身不可用。
sx5 迁移 URP 时直接弃用，反而绕开了它。

---

## 四、修复方案（按推荐度）

### A. 最小侵入（推荐）

只把 1F 五个窗框 `Windows1~Windows5` 的 `m_ReceiveGI` 从 `Lightmaps`(1)
改为 `LightProbes`(2)，让它们改用场景里已有的 4 组光探针，不再采样坏光图。

- 影响范围：5 个 Renderer / 2103 个
- **2F 完全不动**（用户明确要求）
- 不动任何材质、贴图、EXR、`LightingData.asset`
- 一键可回滚
- 视觉效果由 C 实验截图预先验证过

工具：`Assets/_Project/Editor/D13WinDetachGI.cs`
菜单：`归档 → D13-WIN ⑦ 1F窗框改用光探针` / `⑧ 恢复`

### B. 彻底修复（后续可选）

`Window → Rendering → Lighting → Generate Lighting` 重新烘焙整套 GI，
`m_Padding` 从 2 调到 ≥4。优点：恢复全场景间接光与 AO；
缺点：场景大、68 盏 Baked 灯、耗时长，且需重新调曝光。

### C. 照抄 sx5（不推荐）

整场关闭烘焙光图 + 换 URP + 加后处理 Volume。改动面过大：
原项目有大量自研脚本依赖内置管线，且 sx5 的画面是靠 URP 后处理参数
（曝光 0、对比度 +10、Bloom 0.12）补偿出来的，直接套用会让全场景偏亮/过曝。

---

## 五、当前已做的处置

1. 已把此前所有无效的 EXR 像素改动**全部回滚**，5 张图集逐字节还原为 2017 原始备份
   （`py -3.11 Tools/d13win_pad.py rollback`，已校验 md5 一致）。
2. 保留 `Tools/d13win_pad.py` 与全部取证图，仅作为历史记录，不再使用。
3. 新增方案 A 的 Editor 工具（未执行，需用户在 Unity 里点菜单后验收）。

---

## 六、验收方法

1. Unity 打开 `SchoolSceneDay`，等待脚本编译。
2. `归档 → D13-WIN ⑦ 1F窗框改用光探针`（会自动保存场景，并写回滚记录）。
3. 进 play，走 1F 走廊看那 5 个窗框；同时核对 2F 窗框是否保持原样。
4. 不满意执行 `归档 → D13-WIN ⑧ 恢复 GI 设置` 逐项还原。

# 图片素材包说明（A 类技术图）

> **交付物 ②** ｜ 编制：落笔 ｜ 日期：2026-11-04
> **位置**：`E:\Unity Projects\School-pro\Docs\答辩材料\图片素材\`
> **规模**：8 张技术图，每张 `.mmd`（源码）+ `.png`（导出图）同名共存。

---

## 一、文件清单

| 编号 | 文件名 | 内容 | 主要用在 PPT 第几页 |
|---|---|---|---|
| A01 | `A01_系统总体架构` | 六层单向依赖：UI → 容器组件 → 命令 → 孪生 → 总线运行时 → 数据源 | 04（主图）、02、03 |
| A02 | `A02_数据流向` | 双源注册 → 单挂点 Sink → 总线 Flush → 三个存储 → 三个消费方 | 08 |
| A03 | `A03_命令链路时序` | 四段超时 1s/3s/3s/5s + 五条失败分支 + 日志归集 | 06（主图） |
| A04 | `A04_五模块信息架构` | L0 场景 → L1 420 → L2 640 → 五模块 → 主功能 → L3 详情卡 | 05（主图）、02 |
| A05 | `A05_电力回路三级树` | 20 节点（1 楼栋 + 2 楼层 + 17 房间）+ SBO 3s/10s | 07（主图） |
| A06 | `A06_告警状态机` | Normal/Warning/Alarm/Stale/Offline/断电抑制 六态转移 | 13 备选、06 备选 |
| A07 | `A07_时间天气双通道` | 7 档视觉 → 4 档数据映射 + 数据/视觉双通道 + 夜间三处调光 | 09（主图） |
| A08 | `A08_编号体系` | TwinId 主键 + 三层编号 + 唯一化前后对比（34→70、15 组→0 组） | 10（主图） |

---

## 二、实际出图尺寸与排版建议

全部为 **2 倍图**（`-s 2`），投影时不会糊。但 8 张图的宽高比差异很大，**排版方式不能一刀切**：

| 编号 | 像素 | 宽高比 | 与 16:9（1.78）的关系 | 建议排版 |
|---|---|---|---|---|
| A01 系统总体架构 | 1568 × 1408 | 1.11 | 偏高 | 左侧竖排主图（占宽 ~55%），右侧放 3~4 行文字说明 |
| A02 数据流向 | 1568 × 826 | 1.90 | 接近 | 可独页居中，或左图右文 |
| A03 命令链路时序 | 1568 × 696 | 2.25 | 偏宽 | 上半页横贯，下方留白放结论句 |
| A04 五模块信息架构 | 1568 × 784 | 2.00 | 接近 | 上半页横贯，下方排 5 张 B 类缩略图 |
| A05 电力回路三级树 | 1560 × 1222 | 1.28 | 偏高 | 左侧主图（占宽 ~50%），右侧放 B13/B14 上下对比 |
| A06 告警状态机 | 1568 × 380 | 4.13 | 很宽很扁 | 横贯整页（高度仅占 ~30%），下方必须补文字，否则页面空 |
| A07 时间天气双通道 | 1568 × 986 | 1.59 | 接近 | 上半页主图，下方排 B25/B26 两张场景图 |
| A08 编号体系 | 1496 × 1524 | 0.98 | 正方 | 左图右文（图占宽 ~50%），右侧放 B04 列表缩略图 |

**三条排版提醒**

1. **A06 只有 380px 高**，如果按整页宽度铺满，上下会空出大片深色。要么配文字，要么干脆不单独占页（这也是我把它列为"13 页备选"的原因）。
2. **A01 / A05 / A08 偏高**，不要试图铺满整页宽度——强行拉伸会让文字变形。按上表左右分栏。
3. 所有图都是**深色底**。若 PPT 主色是白色，图片会形成明显色块。两个处理方式：① PPT 整体用深色主题（推荐，与运行截图观感统一）；② 图片加细描边（`#33BFFF` @0.3）过渡，不要加阴影。

---

## 三、配色口径（重要，与其他文档的差异说明）

任务包给的配色是「深色底 `#0A1628`、主色 `#33BFFF`」。这里说明两件事：

**① 主色一致，无冲突。**
`TwinTheme.cs` 第 69 行：`Accent = new Color(0.20f, 0.75f, 1.00f)` = **#33BFFF**。任务包写的是对的（不是 #35D0FF）。

**② 深色底我用的是 `#0A1628`，但这不是代码里的 token 值。**

代码现值（`TwinTheme.cs` L51）：
```
ScrimBase = new Color(0.043f, 0.055f, 0.078f, 0.98f)
```
换算成十六进制约为 `#0B0E14`，比 `#0A1628` 更接近纯黑。

**为什么图里仍用 `#0A1628`**：`#0B0E14` 太接近纯黑，投影仪上会糊成一片、和 PPT 的白底形成死黑块；`#0A1628` 带一点蓝，投影时能看出"深蓝而不是黑"，视觉上更接近运行截图的实际观感。**这是出图的排版选择，不是代码口径**——如果主理人要严格对齐代码，把 8 个 `.mmd` 里的 `#0A1628` 全部替换成 `#0B0E14` 重渲即可（方法见第四节）。

**③ 图内实际用到的颜色**

| 用途 | 值 | 来源 |
|---|---|---|
| 画布底 | `#0A1628` | 排版选择（见上） |
| 高亮框填充 / 描边 | `#1A2534` / `#33BFFF` | 描边 = `TwinTheme.Accent` |
| 普通框填充 / 描边 | `#121B28` / `#39485C` | 由 `PanelBottom`、`Border` 观感取近似值 |
| 正文文字 | `#F0F5FC` | `TextPrimary` (0.94,0.96,0.99) 近似 |
| 次要文字 | `#B8C6D8` | `TextSecondary` (0.62,0.69,0.79) 近似 |
| 告警橙 | `#FF9E26` | `TwinTheme.StatusWarning` (1.00,0.62,0.15)，**只用于告警与危险动作** |
| 告警红 | `#E63333` | `TwinTheme.StatusAlarm` (0.90,0.20,0.20) |
| 正常绿 | `#33CC59` | `TwinTheme.StatusNormal` (0.20,0.80,0.35) |

**橙色出现的位置**（严格受限，符合「Warn 只给告警」的项目纪律）：
- A02 的 `TwinRuntime.Sink` 单挂点框（风险提示）
- A03 的失败分支边框
- A05 的 SBO 两步确认框（危险动作）
- A06 的 Warning 状态

**橙/红以外的位置一律不用橙红**，包括"强调""重点""注意看这里"——这一类靠加粗和 `#33BFFF` 边框表达。

---

## 四、图上每个名词的出处

原则：**图上出现的类名、常量、数值，都能在代码或文档里找到原文**，不是我想出来的。

| 图 | 名词 / 数值 | 出处 |
|---|---|---|
| A01 | `TwinFullLayer`、`TwinDrawer`、`TwinSystemPage`、`TwinDeviceDetailPage`、`TwinTimeWeatherCard` | `Assets/_Project/Scripts/UI/` 实际文件 |
| A01 | `TwinCommandCenter`、`TwinCommandLog`、`TwinPowerController`、`MockDeviceAgent` | `Assets/_Project/Scripts/Command/` 实际 6 个文件 |
| A01 | `TwinAnchor`、`TwinRegistry`、`TwinCatalog`、`TwinCircuit`、`TwinAlarmCenter`、`TwinDeviceStats`、`TwinWeatherBridge` | `Assets/_Project/Scripts/Twin/` 实际 19 个文件 |
| A01 | `TwinDataBus` 实现 `IDataSink`、`DefaultExecutionOrder(-100)`、`Bus.Flush(4096)` | `TwinDataBus.cs`、`TwinRuntime.cs`、P00 §2.3 |
| A01 | `MockDataSource` 10Hz、`MqttDataSource`、`MqttLoopbackPublisher`、`TwinClimate`、`TwinRecorder` | `Assets/_Project/Scripts/Data/` 实际 13 个文件 |
| A02 | `DataSourceBase.RegisterSource`、`m_Enabled = 0` 不进 Sources | `DataSourceBase.cs`、P00 §1.6 相关条目 |
| A02 | `TwinRuntime.Sink` 单挂点、`RedirectSinkTo` | `TwinRuntime.cs` L51-57；`TwinRecorder.cs` L186 |
| A02 | `TwinFanoutSink` **不存在** | 已核实 `find` 无此文件；仅存在于 P28 §7.2 规划表 |
| A02 | `TwinMetricHistory` 300 点环形 | `TwinMetricHistory.cs`、P00 时序历史条目 |
| A03 | 1s / 3s / 3s / 5s | `TwinCommandCenter.cs` L37/39/41/43（`_publishTimeout/_ackTimeout/_actuatedTimeout/_readbackTimeout`） |
| A03 | 拒绝链八道（回放→模型→断电→动作→范围→串行锁→回读判据→白名单） | P00 §2.3 原文 |
| A03 | 500 条环形、无响应保持 3s | `TwinCommandLog`（P00 §2.3）、`_failHoldSeconds = 3f`（L46） |
| A04 | L1 420 / L2 640 / 详情卡 470 / 模态 520×320 / 总宽 906 | P00 §3.6、P28 §2.2 |
| A04 | 内容区 544 = 720 − 96 − 80 | P00 §3.5 硬约束 |
| A04 | 图标格 4 列 cell 88×88、标题 22 副标题 13 | P28 §3.3 组件规范 |
| A04 | 图标汉字「监 / 设 / 电 / 警 / 图」 | P28 §3.4 |
| A05 | 20 节点 = 1 楼栋 + 2 楼层 + 17 房间 | P00 §2.3 原文 |
| A05 | 17 个房间名 | `TwinSiteLayout.cs` L63-95 的 `Table` 数组，逐个核对 |
| A05 | `ChainOn`、`AvailabilityOf` 全链合闸 | `TwinCircuit.cs` L41、L275-282 |
| A05 | SBO 3s / 10s | `TwinSboGate.cs` L81 `SelectBudgetSeconds = 3f`、L87 `ArmedSeconds = 10f` |
| A06 | Stale 3s / Offline 5s | `TwinAnchor.cs` L36 `_staleAfterSeconds = 3f`、L38 `_offlineAfterSeconds = 5f` |
| A06 | 抖动抑制 0.05s、告警轮询 0.2s | `TwinAnchor.cs` L40 `_minNotifyInterval`、`TwinAlarmCenter.cs` L58 `_pollInterval = 0.2f` |
| A06 | 断电抑制 | P00 §2.3 告警条目、`TwinAlarmCenter.cs` L154-176 |
| A07 | 480×360、表盘 200、数字标 r=65、四按钮 104×40、≈68 Graphic、DC≤25 | P13-1 图纸（含 r=58→65 的订正） |
| A07 | `TwinTimeWeatherCard` 551 行、`TwinDialDragArea` 49 行、`TwinWeatherBridge` 214 行、`TwinP13PostProcess` 250 行 | P13 断点回执 |
| A07 | 7 → 4 映射表 | P13 方案 §3.2、裁决 C-44 |
| A07 | `SetWeather:136` / `SetHourOverride:150` 零调用者 | P13 方案 F7 |
| A07 | 68 盏 Area 灯 Baked（`m_Type:3` + `m_Lightmapping:2`） | P13 断点回执（含自我订正 2=Baked 不是 Mixed） |
| A07 | 夜间 −1.45 / 雨雪再 −0.25 / 黄昏 −0.35 | P13 断点回执 |
| A08 | `Full()` L146 / `Short()` L102 / `WithType()` L111 / `TypeMid()` L204 | `TwinCode.cs` 实际行号 |
| A08 | 全码格式 `1号教学楼2F203 · 001灯具` | `TwinCode.cs` L146-175 注释里的示例 |
| A08 | 34 → 70、15 组 → 0 组、51 台不可指认 | P7 断点 §1、§3 |
| A08 | 「设备树路径即 ID」 | P5 调研 I 施耐德 EBO |

---

## 五、怎么改、怎么重渲

### 5.1 环境（本机已装好，可复用）

```
临时目录：C:\Users\86159\AppData\Local\Temp\mmdc-tools
版本：@mermaid-js/mermaid-cli 12.2.0
Puppeteer 配置：pp.json → 指向 C:\Program Files\Google\Chrome\Application\chrome.exe
```

### 5.2 改一张图

1. 用文本编辑器打开对应 `.mmd`
2. 改内容（图上文字、数值、框颜色）
3. 跑渲染命令（在 `mmdc-tools` 目录下）：

```bash
cd /c/Users/86159/AppData/Local/Temp/mmdc-tools
./node_modules/.bin/mmdc \
  -i "E:/Unity Projects/School-pro/Docs/答辩材料/图片素材/A01_系统总体架构.mmd" \
  -o "E:/Unity Projects/School-pro/Docs/答辩材料/图片素材/A01_系统总体架构.png" \
  -p pp.json -b '#0A1628' -s 2
```

参数说明：`-b` 画布底色，`-s 2` 二倍图（投影不糊），`-p` puppeteer 配置。

### 5.3 换底色（如果要严格对齐代码 `#0B0E14`）

```bash
cd "E:/Unity Projects/School-pro/Docs/答辩材料/图片素材"
sed -i 's/#0A1628/#0B0E14/g' *.mmd
# 然后逐张重渲，命令同上
```

⚠️ **注意**：`.mmd` 里 `#0A1628` 出现在两处——画布 `background` 和 `edgeLabelBackground`（时序/状态图的连线标签底）。两处都要换，否则标签后面会出现深蓝小方块。

### 5.4 重新安装渲染工具（换机器时）

```bash
mkdir -p /tmp/mmdc-tools && cd /tmp/mmdc-tools
PUPPETEER_SKIP_DOWNLOAD=1 npm init -y
PUPPETEER_SKIP_DOWNLOAD=1 npm install @mermaid-js/mermaid-cli --no-audit --no-fund
```

`PUPPETEER_SKIP_DOWNLOAD=1` 是关键：跳过下载 Chromium（约 150MB），直接复用本机已装的 Chrome。

---

## 六、出图时踩过的坑（给后续改图的人）

| 坑 | 表现 | 解法 |
|---|---|---|
| **中文宽度被低估** | 节点框比文字窄，右侧字被裁掉（如 `TwinAggregationSink` 显示成 `TwinAggregationSin`） | 节点文字用 HTML label + `min-width:Npx` 显式撑宽，**不要指望 auto 宽度** |
| **`wrappingWidth` 不生效** | 设了 900 还是窄 | 同上，改用 `min-width` |
| **`direction LR` 被根方向覆盖** | 子图设了横向，整体还是竖着排，画出细长条 | 不用子图 + `direction`，改成单列堆叠或显式控制节点顺序 |
| **状态图/时序图标签变白块** | 转移标签渲染成纯白方块，字看不见 | `themeVariables` 里补 `edgeLabelBackground: '#0A1628'` |
| **状态节点白底白字** | `stateDiagram-v2` 的 `classDef` 不生效 | 用 `class X className` 显式绑定，颜色写在 `classDef` 里 |
| **`mmdc -w` 参数不存在** | `error: unknown option '-w'` | 宽度用 `-s`（scale），不要用 `-w` |
| **整体过扁或过窄** | 6 个节点横向排完只剩 1080×260 | 控制单行列数：一列最多 3~5 个并排节点 |

---

## 七、与 PPT 稿的对应关系

`PPT_逐页内容稿.md` 里每页的【配图】字段都引用了 A / B 编号。核对结果：

| PPT 页 | 引用的 A 图 | 是否已交付 |
|---|---|---|
| 02 汇报路径 | A04 | ✅ |
| 03 行业怎么做 | A01 | ✅（另建议配 B02） |
| 04 系统怎么搭 | A01 | ✅ |
| 05 五个模块 | A04 | ✅（另配 B03~B07） |
| 06 一次开关的旅程 | A03 | ✅（另配 B11/B12） |
| 07 一栋楼怎么断电 | A05 | ✅（另配 B13/B14） |
| 08 数据凭什么可信 | A02 | ✅（另配 B07） |
| 09 本周新增：天气 | A07 | ✅（另配 B23~B28，周四后） |
| 10 编号就是身份证 | A08 | ✅（另配 B04） |
| 13 让工程可被检验 | A06（备选） | ✅（主配 B21/B22） |

**A06 目前是备选**：告警状态机更适合放在"技术深度"讲，但 06 页已被命令链路占用。如果主理人想让 13 页更有分量，可以把 A06 放上去替换文字图表；如果 13 页要保持"方法重于功能"的调性，A06 可以不用，或挪到附录页。
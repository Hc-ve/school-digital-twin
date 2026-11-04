# 校园超市数字孪生仿真系统

> **一句话定位**：一个用 Unity 把真实校园超市搬进三维世界、并让「选购、库存、动销、客流、断货预测、补货」在同一份数据上实时联动的教学型数字孪生仿真系统。

| 项目信息 | 值 |
| --- | --- |
| 引擎版本 | Unity **2022.3.62f3c1**（1623fc0bbb97） |
| 渲染管线 | Built-in 内置渲染管线 |
| 开发语言 | C#（.NET Standard 2.1 / Unity Mono） |
| 主场景 | `Assets/Scenes/CampusSupermarket.unity` |
| 业务模块 | 12 个 |
| 代码文件 | 42 个 `.cs`（运行时 29 + 编辑器 13） |
| 代码总行数 | **10,311 行**（运行时 6,051 + 编辑器 4,260） |
| 三维模型 | 超市主模型约 725 万面 + 购物篮道具 |
| 外部服务 | **无**，全本地运行，无联网、无数据库、无账号体系 |

---

## 目录

1. [项目概述](#1-项目概述)
2. [已实现交互功能汇总](#2-已实现交互功能汇总)
3. [三个最有亮点的功能](#3-三个最有亮点的功能)
4. [技术实现方案](#4-技术实现方案)
5. [踩坑日志](#5-踩坑日志)
6. [代码文件清单与总行数](#6-代码文件清单与总行数)
7. [外部依赖核查](#7-外部依赖核查)
8. [可复用模块：自包含可拆分清单](#8-可复用模块自包含可拆分清单)
9. [快速上手](#9-快速上手)
10. [维护说明与后续建议](#10-维护说明与后续建议)

---

## 1 项目概述

### 1.1 一句话定位

**把校园超市的「实体场景 + 经营数据 + 顾客行为」做成一套可交互、可推演、可演示的三维数字孪生系统**——在虚拟场景里的每一次点击、每一笔订单、每一位模拟顾客的购买，都会真实写回同一份库存数据，并驱动三维场景、二维数据面板、预测模型同时变化。

### 1.2 背景与核心业务场景

校园超市是高频、低客单价、SKU 密集的典型零售场景。传统管理方式下，管理者很难一眼看清：哪些货架快断货、哪些商品卖得慢、哪些区域客流集中、今天该补多少货。数据散落在表格和收银系统里，与物理货架是「两张皮」。

本项目以真实校园超市 FBX 模型为底，构建三维镜像场景，再叠加四条业务主线：

| 业务主线 | 具体场景 |
| --- | --- |
| 顾客购物 | 在三维场景中点货架选商品、加减数量、去结算、支付出小票、回看历史订单 |
| 库存运营 | 实时库存、缺货告警清单、点击告警飞到货架、展示台泛红脉冲、按建议一键补货 |
| 经营分析 | 按「今日售出」给货架逐层染色、货架信息卡动销评级、全场数据面板 |
| 仿真推演 | 6 位模拟顾客沿 4 条动线逛店并真实下单、按销售速度预测断货时间、生成补货建议 |

### 1.3 解决的核心问题

1. **数据与场景脱节**：通过单一数据源 + 事件广播，让三维模型、二维面板、预测引擎读同一份库存，消灭「演示动画」式的假联动。
2. **运营状态不可视**：热力图、告警发光、世界空间预测标签，把抽象数字翻译成一眼能看懂的颜色与光效。
3. **补货靠拍脑袋**：用「销量 ÷ 已营业时长」折算销售速度，推出剩余可售时间与建议补货量，并识别「货架已塞满」的容量约束。
4. **成果演示门槛高**：提供 17 分幕、约 160 秒的自动演示动线，无需人工操作即可完整走通全部业务闭环。

### 1.4 技术栈与 Unity 版本

| 层次 | 采用的技术 | 说明 |
| --- | --- | --- |
| 引擎 | Unity 2022.3.62f3c1 | 内置渲染管线，未使用 URP/HDRP |
| 语言 | C# | 运行时脚本 + 编辑器扩展 |
| 界面 | UGUI（`com.unity.ugui` 1.0.0） | 全部界面由代码在运行时生成 |
| 文字 | TextMeshPro 3.0.7（中文 CJK 字体） | 字体图集 2048²，覆盖脚本中出现的汉字 |
| 画面 | Post Processing 3.2.2 | 色彩校正、环境光遮蔽、Bloom |
| 数据 | 单例数据中枢 + `event Action` 事件总线 | 无序列化持久化，运行时内存态 |
| 交互 | `Physics.Raycast` 分层射线、世界空间 Canvas | 色板/标签/按钮分属不同层，互不抢点击 |
| 动画 | 协程 + `Lerp`/`Slerp` + smoothstep | 机位切换、光点运动、呼吸脉冲 |
| 编辑器 | `MenuItem` / `EditorWindow` / `AssetPostprocessor` | 一键搭建、批量修复、资源导入后处理 |
| 装配 | `RuntimeInitializeOnLoadMethod(AfterSceneLoad)` | 运行时自启动，不改场景文件 |

![系统架构图](docs/images/architecture.png)

> 图 1　系统架构：展示交互层 / 业务功能层 / 数据中枢层 / 资源与场景层 / 编辑器工具链

---

## 2 已实现交互功能汇总

### 2.1 功能汇总表

| 功能模块 | 功能点 | 交互说明 |
| --- | --- | --- |
| 场景搭建 | 一键搭建超市场景 | 菜单「Tools/数字孪生/① 一键搭建超市场景」：导入 FBX、计算包围盒自动居中贴地、创建方向光与三色环境光、按模型尺寸算相机裁剪面并挂轨道相机 |
| 场景搭建 | 场景体检 | 菜单②：统计渲染器数、三角面数、材质数、无主贴图材质数、缺失网格数，结果弹窗 + 输出到控制台 |
| 场景搭建 | 放置购物篮 | 菜单③：在场景视图焦点处放置购物篮模型，自动缩放 0.55 并注册撤销 |
| 材质与画面 | 贴图自动匹配 | 「Tools/贴图自动匹配」：三级名称归一化匹配（完全相等 → 贴图名含材质名 → 材质名含贴图名），批量赋主贴图，报告未匹配数 |
| 材质与画面 | 洗护用品材质工具 | 选中物体后循环分配洗护材质，一排货架自动变成不同商品，支持撤销 |
| 材质与画面 | 灯光与后处理修复 | 扫描发光对象、修复发光源灯光、配置后处理 Bloom、修复后处理配置引用 |
| 材质与画面 | 清晰度一键优化 | TMP 字体图集 1024²→2048² 重烘焙、UI 图关闭 mipmap、三维贴图各向异性 8、大图上限放开到 4096 |
| 货架库存 | 库存数据中枢 | `ShelfStore` 单例：商品名/单价/容量/库存/销量，累计售出、累计销售额、全场在售库存；任何变化广播 `Changed` 事件 |
| 货架库存 | 货架点选高亮 | 左键点击货架：整体发光 + 弹出信息卡；再点一次取消；按住 Shift 点击直接补货 |
| 货架库存 | 货架信息卡 | 显示今日售出、实时库存、动销评级；库存变化时自动刷新，低库存时高亮转橙红并呼吸 |
| 货架库存 | 数据面板 | 右下角开合，显示全场在售库存、今日售出、累计销售额、在售商品总数；数值跳变时闪橙提示；可拖拽移动 |
| 购物结算 | 世界空间选购按钮 | 场景中「点击选购」按钮始终朝向相机，带浮动与光晕；点击弹出选购窗 |
| 购物结算 | 选购弹窗 | 每行商品可 +/- 调数量，上限受实时库存约束；库存为 0 显示「缺货」并置灰加号；实时刷新小计、合计、总件数 |
| 购物结算 | 结算明细 | 点「去结账」：只显示已选行，未选行隐藏后紧凑上移；先按最新库存收敛购物车防超卖 |
| 购物结算 | 提交支付 | 扣减库存、累加销量与销售额、生成订单、弹出电子小票，并触发全场数据联动 |
| 订单与小票 | 电子小票 | 支付成功页含订单号、交易时间、商品明细、单价小计、合计金额；点「完成」关闭 |
| 订单与小票 | 订单历史 | 时间倒序列出最多 30 笔订单，卡片高度按条数自适应；点任意一条回看该笔小票 |
| 缺货告警 | 告警清单 | 筛选库存 ≤ 阈值商品，按「缺货优先、库存升序」排序；每条显示商品名、货架号、库存/容量、今日售出、告警徽标；空态与汇总文案自适应 |
| 缺货告警 | 告警项定位 | 点清单任一条：关面板 → 切到货架区机位 → 选中对应货架，形成「告警 → 定位 → 处理」闭环 |
| 缺货告警 | 展示台发光 | 三维缺货展示台按告警条数泛红脉冲，台面标签同步显示告警数 |
| 动销热力图 | 五档染色 | 货架正面覆盖半透明色板，按今日售出分为深绿/绿/黄/红/深红五档（阈值 55/30/15/4 件） |
| 动销热力图 | 悬停浮窗 | 鼠标悬停色板提亮，跟随光标浮窗显示商品名、动销档位、今日售出、库存；靠近屏幕边缘自动翻转 |
| 动销热力图 | 卖出联动 | 每卖一件，对应层颜色实时变化，直观呈现「交易 → 动销」因果 |
| 搜索定位 | 商品搜索 | 输入关键词实时匹配，分级评分（完全匹配 100 / 名称包含 85 / 别名完全 70 / 别名包含 55 / 模型名包含 20），最多 4 条结果 |
| 搜索定位 | 三维定位高亮 | 点结果：摄像机平滑飞到该商品观察机位，目标模型发光高亮；支持按回车直接选第一条 |
| 摄像机漫游 | 自由轨道相机 | 左键拖拽旋转、右键拖拽平移、滚轮缩放/调速、WASD 平移、Q/E 升降、Shift 加速 |
| 摄像机漫游 | 5 个预设机位 | 屏幕按钮或数字键 1~5 切换；位置线性插值、旋转球面插值、视野插值，smoothstep 过渡约 1.1 秒 |
| 摄像机漫游 | 世界按钮叠加相机 | 叠加相机只渲染世界按钮层、只清深度，保证标签永远压在三维场景最上层不被货架遮挡 |
| 客流仿真大屏 | 顾客逛店动线 | 左下角按钮或 F 键铺满全屏；俯视平面图 6 组货架、入口、收银台、地面网格；6 位顾客沿 4 条动线行走并带拖尾光点 |
| 客流仿真大屏 | 停留购买 | 顾客到达货架前停留 1.2 秒，按 72% 概率真实调用 `Sell()` 下单；底部字幕实时播报「顾客 #3 在 A-01 购买…库存 12→11」 |
| 客流仿真大屏 | 断货预测榜 | 右侧按剩余可售时间排序，显示告急项数、建议补货件数、今日预计流失金额，并可按建议一键补货 |
| 断货预测与补货 | 世界空间预测标签 | 货架商品正上方浮空标签：剩余可售时间大字、建议补货量、库存水位条、紧急度徽标；告急时呼吸；点击打开补货建议单 |
| 断货预测与补货 | 智能补货建议单 | R 键或左下角常驻入口打开；每行给出库存/日均销量/剩余可售/建议补货量，行尾可单条补货，底部可一键全补；左下入口告急时变红呼吸 |
| 断货预测与补货 | 满架识别 | 货架余位不足缺口 30% 时不建议补货，标记「满架」，避免给出无法执行的建议 |
| 界面与适配 | 可拖拽面板 | 面板顶部拖拽条自由移动，右下角按钮展开/收起，展开时切换按钮自动隐藏 |
| 界面与适配 | 大屏比例适配 | 画布缩放采用 Expand + 固定 1920×1080 内容层，任意 Game 视图宽高比下内容完整不裁切 |
| 一键演示 | 自动演示动线 | 17 分幕约 160 秒，自动切机位、开面板、操作数据、底部字幕解说；F1 或 G 开关、空格跳过、Esc 退出，进入播放模式后 1.5 秒自动开始 |
| 一键演示 | 演示容错 | 每个动作统一异常保护，单步出错不中断整场；跳过分幕时补执行剩余动作，避免界面停在半路 |

![功能模块地图](docs/images/module-map.png)

> 图 2　12 个业务模块总览

### 2.2 快捷键与入口速查

| 按键 / 入口 | 作用 |
| --- | --- |
| F1 或 G | 开始 / 停止一键演示（Mac 上 F1 常被系统占用为亮度键，G 为备用） |
| 空格 / Esc | 跳过当前分幕 / 退出演示（Esc 也用于关闭各类面板） |
| F | 打开 / 关闭客流仿真大屏 |
| R | 打开 / 关闭智能补货建议单 |
| 数字键 1~5 | 切换 5 个预设机位 |
| 左键拖拽 / 右键拖拽 / 滚轮 | 旋转视角 / 平移 / 缩放 |
| WASD / Q E / Shift | 相机平移 / 升降 / 加速 |
| Shift + 左键点货架 | 直接补货 |
| 左下角三个入口 | 一键演示、补货建议单、客流仿真大屏 |
| 右下角按钮 | 打开 / 收起数据面板 |
| 场景内世界按钮 | 点击选购、点击结账（2 处）、缺货告警标签、断货预测标签（2 处） |

---

## 3 三个最有亮点的功能

### 3.1 购物结算的虚实联动：一笔订单改变整个系统

![支付后库存同步](docs/images/feature-checkout-sync.png)

> 运行截图：支付成功弹窗（订单号、实付金额、库存已同步），右侧运营数据总览同步显示全场在售库存变化

**实现原理**：系统在场景中只放一个 `ShelfStore` 单例作为唯一数据源，库存、销量、销售额全部由它持有。选购弹窗的加号不是简单的数字自增，而是每次点击都通过 `StockOf(index)` 读取实时库存作为上限；支付时 `ShopCheckout.SubmitPay()` 先按最新库存收敛购物车防止超卖，再逐行调用 `Sell()` 扣减库存并累加销量，`Sell()` 末尾触发 `Changed` 事件广播。货架信息卡、数据面板、动销热力图、缺货告警面板、告警展示台、断货预测引擎都在 `OnEnable` 订阅该事件，因此在同一帧完成刷新；紧接着 `OrderBook.Add()` 记一笔订单、`ReceiptView.Show()` 弹出小票。整个链路中没有任何模块直接改别人的界面，新增一个展示视图只需订阅事件即可，这正是数字孪生「一处写、多处读」的核心。

### 3.2 动销热力图：把销售数据染到真实货架上

![动销热力图](docs/images/feature-heatmap.png)

> 运行截图：货架正面按「今日售出」逐层染色，深绿畅销、深红滞销

**实现原理**：`ShelfHeatmap` 在运行时为每个货架的每一层生成一个独立的方形网格体，贴在货架正面外侧，按售出件数套用五档颜色（阈值 55/30/15/4）。关键设计有两个：一是**不改货架本身的材质**，只给覆盖色板上色，否则会把商品包装一起染掉导致认不出货；二是**射线分层**——色板放在「忽略射线」层（Layer 2），货架点选的默认射线会直接穿过色板打到货架身上，而热力图脚本用自定义掩码只扫这一层做悬停拾取，两套射线互不干扰。色板订阅 `ShelfStore.Changed`，顾客或用户每买走一件，对应层立刻变色；鼠标悬停时该层提亮并弹出跟随光标的浮窗，靠近屏幕边缘会自动翻转避免溢出。

### 3.3 缺货告警的三维联动：从数字预警到物理提示

![缺货展示台告警发光](docs/images/feature-alert-glow.png)

> 运行截图：商品售罄后，三维缺货展示台整体泛红脉冲，标签同步显示告警条数

**实现原理**：`StockAlertPanel` 订阅数据中枢事件，遍历所有商品筛出库存 ≤ 阈值的条目，按「缺货优先、库存从少到多」排序，卡片高度随条数自适应；点击任一条会关闭面板、把摄像机切到货架区机位并选中对应货架，把「看到告警」直接接到「走到现场」。三维侧的 `StockAlertGlow` 让缺货展示台按告警状态泛红脉冲，并在台面标签显示条数；它直接查询 `ShelfStore` 而不是等面板刷新，避免「面板没打开过，展示台开局误显示库存正常」。发光实现上踩过一个坑：部分材质虽然存了自发光颜色但没开 `_EMISSION` 关键字，只写颜色会意外点亮，因此脚本记录原状态，归零时显式关闭关键字，并在初始化时先应用一次零值避免首帧闪一下。

---

## 4 技术实现方案

### 4.1 总体架构与业务闭环

系统采用「单一数据源 + 事件驱动 + 代码生成界面」三层思路：数据层只有 `ShelfStore` / `OrderBook` / `StockForecast` 三个单例；业务层所有模块只调用数据层的公开方法，不互相直接改界面；展示层全部由 `UiKit` 在运行时用代码创建，不往场景里塞预制体，避免改坏已有场景文件。

![核心业务流程图](docs/images/business-flow.png)

> 图 3　购物结算 → 库存回写 → 事件广播 → 全场联动

支付环节的完整顺序：

1. 三维世界按钮触发 `ShopPopup.Open()`，加减数量受 `ShelfStore.StockOf()` 约束；
2. `GoCheckout()` 关选购窗、开结算窗，明细只保留已选行并紧凑上移；
3. `SubmitPay()` 先 `Refresh()` 收敛购物车，再逐行 `Sell()` 回写库存；
4. `Sell()` 广播 `Changed`，7 类订阅者同帧刷新；
5. `OrderBook.Add()` 生成订单号（`SO` + 时间戳 + 序号），`ReceiptView.Show()` 弹小票，购物车清零。

### 4.2 数据中枢与两级事件链

![数据联动机制图](docs/images/data-event-bus.png)

> 图 4　写侧四类入口 / 读侧七类订阅者 / 二级联动

`ShelfStore` 暴露 `Sell()`、`Restock()`、`RestockBy()`、`RestockAll()`、`Notify()`，统一在末尾触发 `event Action Changed`。订阅遵循固定写法：`OnEnable` 订阅、`OnDisable`/`OnDestroy` 取消，并在 `Start` 里「先减后加」兜底一次——因为 Unity 的 `Awake` 顺序不确定，数据中枢可能比视图晚初始化，只靠 `OnEnable` 会漏订，只靠 `Start` 可能重复订。

`StockForecast` 是**二级事件源**：它订阅 `ShelfStore.Changed`，每秒重算一次销售速度与剩余可售时间，重算结果用签名比对（等级 / 建议量 / 库存）判断是否有实质变化，有变化才广播自己的 `Changed`，驱动补货建议单与世界预测标签刷新。这样避免每帧无意义地重建整张建议单。

### 4.3 核心模块关键实现逻辑

| 模块 | 关键逻辑 |
| --- | --- |
| 断货预测 | 销售速度 = 今日售出 ÷ 已营业时长（启动时假定已营业 6 小时）；剩余可售 = 库存 ÷ 速度；目标水位 = 日均销量 ×（目标周转 3 天 + 提前期 0.5 天）；建议量 = 目标水位 − 当前库存，并受货架容量约束；余位不足缺口 30% 判「满架」 |
| 紧急度 | 四级：告急（已断货或剩余 ≤ 4 小时）、紧张（≤ 12 小时）、关注（天数 < 目标周转）、充足；同时估算若不补货今日预计流失的销售额 |
| 客流仿真 | 4 条预设路径点数组，6 位顾客按不同延迟出发；每帧按速度沿线段插值移动，历史位置队列生成 5 段拖尾；到达货架边缘停留点暂停 1.2 秒，按 `buyChance=0.72` 决定下单 |
| 商品搜索 | 分级评分 + 插入排序，结果上限 4 条，动态实例化结果行；命中后 `CameraTour.FlyToPose()` 飞到观察位姿，`ProductSpot.Spotlight()` 惰性克隆发光材质做高亮 |
| 机位切换 | 协程驱动，位置 `Vector3.Lerp`、旋转 `Quaternion.Slerp`、视野 `Mathf.Lerp`，插值系数用 `k*k*(3-2*k)` 平滑；过渡期间禁用手动轨道输入，结束后调 `SyncFromTransform()` 同步朝向 |
| 世界预测标签 | 世界空间 Canvas 挂在商品模型正上方，屏幕矩形判定点击（不依赖碰撞体）；静态 `PointerOnTag` 供货架点选避让，避免点标签误选背后的货架 |
| 界面生成 | `UiKit` 提供 `Node/Img/Txt/Btn/Place/Stretch/Dot/ScreenCanvas`；`ResolveFont()` 从现有 TMP 文本里挑含 CJK 的字体，防止中文显示为方块；画布统一 1920×1080 参考分辨率 + `ScreenMatchMode.Expand` |
| 自启动装配 | `ForecastBootstrap`、`CrowdDashboard`、`DemoDirector` 用 `RuntimeInitializeOnLoadMethod(AfterSceneLoad)` 自行创建 GameObject；单例判空避免重复；`ForecastTag` 设 `[DefaultExecutionOrder(-100)]` 提前初始化 |
| 自动演示 | `Beat` 结构含标题、字幕、停留时长、时间轴动作表；协程按幕推进，动作统一 try/catch 包裹；字幕逐字打字机效果，底部进度条显示总进度 |

### 4.4 技术架构总览

![技术架构与关键机制](docs/images/tech-stack.png)

> 图 5　运行时 / 编辑器技术栈与五个关键机制

### 4.5 一键演示时间轴

![一键演示动线时间轴](docs/images/demo-timeline.png)

> 图 6　17 个分幕按业务因果编排，约 160 秒走完全部功能

演示顺序遵循真实业务因果：**巡览 → 动销热力 → 点选货架 → 选购 → 结算出票 → 订单历史 → 客流大屏（顾客真实消耗库存）→ 缺货告警 → 断货预测 → 补货建议 → 一键补货 → 收尾**。所有动作都调用各模块已有的公开方法，不复制业务逻辑、不硬改数据，因此演示过程本身就是一次完整的集成测试。

---

## 5 踩坑日志

以下均为开发过程中真实遇到并已修复的问题，按类别整理，可直接作为后续维护的排错索引。

### 5.1 资源导入与渲染

| # | 现象 | 原因 | 解决方案 | 涉及文件 |
| --- | --- | --- | --- | --- |
| 1 | 热力色板设 `material.color` 完全无效，渲染成纯白 | `Unlit/Transparent` 只有 `_MainTex`，没有 `_Color` 属性 | 换用带 `_Color` 且支持透明混合的着色器，按 `Sprites/Default` → `UI/Default` → `Legacy Transparent/Diffuse` → `Unlit/Color` 顺序兜底 | `ShelfHeatmap.cs` |
| 2 | FBX 导入报 `localRotation assign attempt ... is not valid` | 建模导出的辅助节点 Transform 本身就是 NaN，Unity 拒绝赋值 | 写 `AssetPostprocessor.OnPostprocessModel`，只对 `.fbx` 把 NaN 的旋转/缩放/位移替换为 identity / one / zero | `DTFbxNaNPostprocessor.cs` |
| 3 | 打开场景报 `Failed to load PostProcessProfile.cs` | 后处理 Volume 的 `sharedProfile` 引用写成了脚本的 GUID | 编辑器启动时内存重指向正确的 Profile 资源并保存场景 | `DTFixVolumeProfile.cs` |
| 4 | 工程迁移后 Unity 启动打开空场景、Build Settings 为空 | `Library` 重建导致「上次打开场景」记录丢失 | `[InitializeOnLoad]` + 延迟回调自动打开主场景并插入 Build Settings 首位；只在空场景/SampleScene 时接管，失败不写完成标记以便重试 | `DTRestoreScene.cs` |
| 5 | 热力色板在编辑器里预建后重开工程丢失 | 运行时生成的网格与材质不是资产资源，场景存不下来 | 改为纯运行时生成，编辑器不预建 | `DTShopBuilder.cs` |
| 6 | 大字模型远处被裁、近处深度冲突闪烁 | 相机近远裁剪面用了默认值，不适配超大场景 | 按模型包围盒尺寸自动算裁剪面，近裁剪面 `Clamp(size*0.002, 0.03, 0.5)`，远裁剪面 `size*6+100` | `DigitalTwinSceneBuilder.cs` |
| 7 | 自发光关不掉 / 第一帧闪一下原材质发光 | 材质存了 `_EmissionColor` 但 `_EMISSION` 关键字状态与预期不符 | 记录原始 `emitWasOn`，归零时 `DisableKeyword`；初始化后先 `Apply(0)` 再订阅数据 | `StockAlertGlow.cs` |

### 5.2 界面与适配

| # | 现象 | 原因 | 解决方案 | 涉及文件 |
| --- | --- | --- | --- | --- |
| 8 | 异形/超宽 Game 视图下底部字幕条和按钮被裁掉 | 画布缩放用 `MatchWidthOrHeight` 单边缩放，参考内容超出屏幕 | 统一改 `ScreenMatchMode.Expand`，并在根节点下加固定 1920×1080 居中 Content 层 | `UiKit.cs`、`CrowdDashboard.cs` |
| 9 | 运行时按钮字体发虚、中文出现方块 | TMP 字体图集 1024² 且未烘焙全部汉字；Expand 缩放下字号被压到 7~10px | 图集升 2048² 并扫描脚本中出现的汉字重新烘焙；UI 图关 mipmap、三维贴图各向异性 8；运行时优先挑含 CJK 的字体 | `ClarityOptimizer.cs`、`UiKit.cs` |
| 10 | 弹窗一 `Open()` 就被立刻关掉 | `Awake` 里无条件 `root.SetActive(false)`，而 `root` 可能指向面板自身 | 判断 `root != gameObject` 才在 Awake 隐藏 | `ReceiptView.cs`、`OrderHistoryView.cs` |
| 11 | 结算明细 / 订单列表出现大片空档 | 未选中的行只是隐藏，占位仍在 | 可见行按序重算锚点纵坐标依次上移；卡片高度按实际条数自适应 | `ShopCheckout.cs`、`OrderHistoryView.cs` |
| 12 | 世界按钮文字被背后浅色货架冲淡、两个按钮叠成一列 | 半透明背景 + 摆放位置重叠 | 背景调实；两个结账按钮同高度横向拉开；呼吸幅度调小避免 alpha 过低 | `ShopWorldButton.cs`、`DTShopBuilder.cs` |
| 13 | 补货建议单打开时，客流大屏入口按钮浮在遮罩之上 | 两个高层级画布叠加，模态关系没处理 | 大屏每帧检查建议单 `IsOpen`，模态打开时隐藏自己的入口按钮 | `CrowdDashboard.cs` |

### 5.3 事件、执行顺序与交互

| # | 现象 | 原因 | 解决方案 | 涉及文件 |
| --- | --- | --- | --- | --- |
| 14 | 数据面板/信息卡偶尔不刷新或刷新两次 | `Awake` 顺序不确定，订阅时单例可能还没建好 | `OnEnable` 订阅 + `OnDisable` 取消 + `Start` 里「先减后加」兜底 | `DataPanelBinding.cs`、`ShelfPick.cs` |
| 15 | 商品搜索框输入无反应 | 脚本挂在默认隐藏的结果列表上，`Awake` 根本不执行 | 把 `ProductSearch` 挂到常驻激活的根节点 | `DTShopBuilder.cs` |
| 16 | 点热力色板 / 世界标签时误选中背后货架 | 多个射线系统在同一帧都命中 | 色板放独立层 + 掩码；静态 `PointerOnCell` / `PointerOnTag` 给货架点选做避让；标签用屏幕矩形判定，不加碰撞体 | `ShelfHeatmap.cs`、`ForecastTag.cs`、`ShelfPick.cs` |
| 17 | 机位切换后第一次右键拖拽，视角跳回旧角度 | 轨道相机的 yaw/pitch 缓存没跟上被脚本改过的 Transform | 过渡结束调 `SyncFromTransform()` 同步当前朝向再恢复输入 | `OrbitFlyCamera.cs`、`CameraTour.cs` |
| 18 | 开局展示台显示「库存正常」、「今日售出 0」，与热力图对不上 | 告警数只在面板首次打开时刷新；预置初始销量没累计进全局指标 | 展示台直接查 `ShelfStore`；`ShelfStore.Awake` 遍历初始销量汇总 `SoldTotal/Revenue`；预置模拟「营业半天」的销量与低库存 | `StockAlertGlow.cs`、`ShelfStore.cs`、`DTShopBuilder.cs` |
| 19 | 一键补货时预测列表边遍历边被修改 | `Sell/Restock` 会触发重算并重建 `Skus` 列表 | 先把下标和建议量复制到数组，再按数组补货 | `StockForecast.cs` |
| 20 | 世界空间按钮点不到 | 按钮在专用层且主相机剔除了该层，`OnMouseDown` 不触发 | 改为对该层单独 `Physics.Raycast`，并判断 `IsPointerOverGameObject` 防止透过 2D 面板误触 | `ShopWorldButton.cs` |

### 5.4 平台与演示

| # | 现象 | 原因 | 解决方案 | 涉及文件 |
| --- | --- | --- | --- | --- |
| 21 | Mac 上按 F1 无法启动演示 | F1 被系统占用为亮度调节键，脚本收不到 | 增加备用键 G + 左下角常驻「一键演示」屏幕按钮 | `DemoDirector.cs` |
| 22 | 在播放模式中改脚本后，自启动组件不再创建 | `RuntimeInitializeOnLoadMethod` 只在进入播放时执行一次，热重载不重跑 | 停止并重新进入播放模式；同时提供屏幕按钮手动触发 | `DemoDirector.cs` |
| 23 | 跳过分幕后界面卡在半开状态 | 跳过时剩余时间轴动作被直接丢弃 | 跳幕时补执行该幕剩余 cue，保证状态收尾 | `DemoDirector.cs` |
| 24 | 演示中某个动作抛异常导致整场中断 | 动作直接调用各模块方法，任一空引用会打断协程 | 所有动作统一 `Safe()` try/catch 包裹，单步失败只记日志不中断 | `DemoDirector.cs` |

---

## 6 代码文件清单与总行数

统计口径：Unity 工程 `Assets/Scripts/` 与 `Assets/Editor/` 下的全部 `.cs` 文件，用 `wc -l` 统计（含空行与注释行）。

| 目录 | 文件数 | 总行数 |
| --- | --- | --- |
| `Assets/Scripts/`（运行时） | 29 | 6,051 |
| `Assets/Editor/`（编辑器） | 13 | 4,260 |
| **合计** | **42** | **10,311** |

### 6.1 运行时脚本（Assets/Scripts）

| 文件 | 行数 | 职责 |
| --- | ---: | --- |
| `CrowdDashboard.cs` | 775 | 客流仿真大屏：平面图、顾客动线、购买、预测榜 |
| `DemoDirector.cs` | 592 | 17 分幕自动演示、字幕与进度、动作容错 |
| `ShelfHeatmap.cs` | 477 | 动销热力色板、五档染色、悬停浮窗 |
| `RestockPlanPanel.cs` | 360 | 智能补货建议单、单条/一键补货 |
| `StockForecast.cs` | 354 | 断货预测与建议量计算引擎 |
| `ShelfPick.cs` | 326 | 货架点选高亮、信息卡、Shift 补货 |
| `ShelfStore.cs` | 239 | **库存与销量唯一数据源**、事件广播 |
| `ForecastTag.cs` | 239 | 世界空间断货预测标签 |
| `StockAlertPanel.cs` | 233 | 缺货告警清单、排序、点击定位 |
| `ShopCheckout.cs` | 206 | 结算明细、支付、库存回写、生成订单 |
| `StockAlertGlow.cs` | 203 | 缺货展示台发光与标签联动 |
| `ProductSpot.cs` | 203 | 商品定位机位与高亮 |
| `ProductSearch.cs` | 186 | 商品搜索评分与结果列表 |
| `UiKit.cs` | 182 | 运行时界面生成工具、字体解析、画布创建 |
| `ShopPopup.cs` | 166 | 选购弹窗、数量与库存约束、小计刷新 |
| `DraggablePanel.cs` | 159 | 可拖拽面板开合 |
| `OrderHistoryView.cs` | 149 | 订单历史列表与回看小票 |
| `CameraTour.cs` | 144 | 预设机位平滑切换与飞行定位 |
| `ReceiptView.cs` | 135 | 电子小票展示 |
| `DataPanelBinding.cs` | 111 | 数据面板文本绑定与跳变提示 |
| `ShopWorldButton.cs` | 111 | 世界空间按钮、朝向相机、分层射线 |
| `OrderBook.cs` | 109 | 订单账本（最多 30 笔） |
| `DTLightAutoFix.cs` | 103 | 运行时发光源修复，执行后自毁 |
| `OrbitFlyCamera.cs` | 84 | 轨道飞行相机、朝向同步 |
| `ForecastBootstrap.cs` | 67 | 预测模块自启动装配、注入洗护商品 |
| `DragHandle.cs` | 63 | 面板拖拽条 |
| `CameraFollower.cs` | 32 | 叠加相机跟随主相机、只清深度 |
| `CameraTourButton.cs` | 26 | 机位按钮选中态 |
| `ShopQtyButton.cs` | 17 | 加减按钮回调 |
| **小计** | **6,051** | |

### 6.2 编辑器脚本（Assets/Editor）

| 文件 | 行数 | 职责 |
| --- | ---: | --- |
| `DTShopBuilder.cs` | 2013 | 一键生成全部选购/结算/告警/热力/机位界面与绑定 |
| `DTUIBuilder.cs` | 596 | 生成基础界面、中文字体、事件系统 |
| `DTPostProcessSetup.cs` | 241 | 配置后处理 Bloom（反射调用，避免编译期依赖） |
| `ClarityOptimizer.cs` | 233 | 字体图集与贴图清晰度优化 |
| `WashMaterialTool.cs` | 233 | 洗护材质批量生成与分配 |
| `DigitalTwinSceneBuilder.cs` | 223 | 一键搭建场景、场景体检、放置购物篮 |
| `AutoFixAllOnLoad.cs` | 224 | 贴图自动匹配（类 `AutoAssignTextures`） |
| `DTLightFixer.cs` | 169 | 修复发光源灯光 |
| `DTRestoreScene.cs` | 80 | 迁移后自动恢复主场景与 Build Settings |
| `DTScanLights.cs` | 76 | 扫描发光对象并输出报告 |
| `DTFindLights.cs` | 65 | 查找发光对象编辑器窗口 |
| `DTFixVolumeProfile.cs` | 62 | 修复后处理配置引用 |
| `DTFbxNaNPostprocessor.cs` | 45 | FBX 导入时修正 NaN 变换 |
| **小计** | **4,260** | |

### 6.3 工作区镜像副本说明

当前工作区根目录下另有一份 **43 个 `.cs` / 10,633 行**的平铺副本，是开发过程中的脚本备份与自动化构建脚本（`DT_Run` 系列、`DT_ShopRun`、`DT_UI_AutoRun`、`DT_AutoBuildOnce`、`DT_Check` 等）。这些文件**不属于 Unity 工程编译范围**，不参与运行；正式代码以 `Assets/Scripts` 与 `Assets/Editor` 为准。根目录的 25 张 `NN_功能_状态.png` 是各功能的运行截图存档。

---

## 7 外部依赖核查

### 7.1 Asset Store / 第三方包：**无**

- `Assets/` 下不存在 `Plugins`、`ThirdParty`、`Vendor` 等第三方插件目录。
- `Packages/manifest.json` 中所有依赖均为 `com.unity.*` 官方包，无 Git URL、无本地 `file:` 引用、无付费资源包。
- 项目没有导入任何 Asset Store 付费或免费资源包；三维模型、贴图、材质均为随工程自带的自制/导出资源。

### 7.2 Unity 官方包依赖（逐项）

| 包名 | 版本 | 用途 | 是否必需 |
| --- | --- | --- | --- |
| `com.unity.ugui` | 1.0.0 | 全部界面（Canvas / Image / Button / EventSystem） | **必需** |
| `com.unity.textmeshpro` | 3.0.7 | 全部文字渲染、中文 CJK 字体图集 | **必需** |
| `com.unity.postprocessing` | 3.2.2 | 后处理配置工具生成的 Volume / Bloom | **必需**（若删除 `DTPostProcessSetup.cs` 等工具可一并移除） |
| `com.unity.modules.physics` | 1.0.0 | 货架、色板、世界按钮的分层射线拾取 | **必需** |
| `com.unity.modules.ui` / `uielements` / `imgui` | 1.0.0 | 编辑器扩展窗口绘制 | 编辑器工具需要 |
| `com.unity.modules.imageconversion` | 1.0.0 | 运行时生成圆形精灵贴图 | 需要 |
| `com.unity.modules.director` / `animation` | 1.0.0 | 引擎基础模块，`Timeline` 依赖 | 随引擎 |
| `com.unity.timeline` | 1.7.7 | 工程自带；**项目运行时代码未引用**（课程练习使用） | 可移除 |
| `com.unity.visualscripting` | 1.9.4 | 工程自带，项目未使用 | 可移除 |
| `com.unity.collab-proxy` | 2.12.4 | 版本协作代理，项目未使用 | 可移除 |
| `com.unity.feature.development` | 1.0.1 | 开发特性集合包，项目未直接使用 | 可移除 |
| 其余 `com.unity.modules.*` | 1.0.0 | Unity 内置模块（音频、粒子、地形、VR、网页请求等），随引擎默认启用 | 引擎内置，无需单独安装 |

### 7.3 外部服务与联网：**无**

| 检查项 | 结论 |
| --- | --- |
| 网络请求代码 | 全部脚本未出现 `UnityWebRequest` / `HttpClient` / `WWW` / `Socket` / `http(s)://` |
| 云 SDK / 后端 | 无 Firebase、无数据库、无 BaaS、无账号与鉴权 |
| 第三方 API / 密钥 | 无 API Key、无 Token、无配置文件存放密钥 |
| 数据持久化 | 无文件读写、无 PlayerPrefs、无存档；所有数据为运行时内存态，退出播放即重置 |
| 遥测与统计 | 未调用任何分析/埋点接口 |

### 7.4 非代码资产依赖（随工程自带，非外部服务）

| 资产 | 说明 |
| --- | --- |
| `Assets/SupermarketToUnity.fbx` | 超市主模型，约 725 万面 |
| `Assets/06d5da3e75466bc427697306772aa29e.fbx` | 购物篮道具 |
| `Assets/Textures/`、`Assets/Materials/`、`Assets/caizhi/` | 贴图与材质库 |
| `Assets/TextMesh Pro/` | TMP 字体资源（含中文 CJK 字体图集） |
| `Assets/PostProcessProfiles/` | 后处理配置文件 |
| `Assets/UI/`、`Assets/Models/` | 界面图与模型资源 |
| `Assets/Scenes/CampusSupermarket.unity` | 主场景（约 1.8 MB） |

---

## 8 可复用模块：自包含可拆分清单

**判断标准**：不读取 `ShelfStore` 等本项目业务单例、不硬编码场景物体名或商品名、不依赖本项目专属层级与标签，复制到另一个 Unity 工程后仅靠自身 + Unity 官方包即可编译。

### 8.1 可完全独立拆出（零本项目耦合）

| 复用包 | 文件清单（行数） | 依赖 | 用途 |
| --- | --- | --- | --- |
| 界面代码生成工具 | `Assets/Scripts/UiKit.cs`（182） | UGUI + TMP | 一行代码生成节点/图片/文本/按钮/圆形光点/屏幕画布，自动挑 CJK 字体、Expand 适配 |
| 通用拖拽面板 | `Assets/Scripts/DraggablePanel.cs`（159）、`Assets/Scripts/DragHandle.cs`（63） | UGUI | 任意面板的拖拽移动与展开收起 |
| 摄像机控制套件 | `Assets/Scripts/OrbitFlyCamera.cs`（84）、`CameraTour.cs`（144）、`CameraTourButton.cs`（26）、`CameraFollower.cs`（32） | 引擎基础 | 轨道漫游、预设机位插值切换、叠加相机跟随 |
| 贴图自动匹配 | `Assets/Editor/AutoFixAllOnLoad.cs`（224） | UnityEditor | 按名称批量给材质赋主贴图，适用于任何 FBX 项目 |
| 字体与贴图清晰度优化 | `Assets/Editor/ClarityOptimizer.cs`（233） | UnityEditor + TMP | 扫描脚本字符集重烘焙 TMP 图集、批量优化贴图采样 |
| FBX 异常变换修正 | `Assets/Editor/DTFbxNaNPostprocessor.cs`（45） | UnityEditor | 修任何来源含 NaN 变换的 FBX，消除导入警告 |
| 后处理配置引用修复 | `Assets/Editor/DTFixVolumeProfile.cs`（62） | UnityEditor + PostProcessing | 修复 Volume 的错误 Profile 引用 |
| 材质批量分配工具 | `Assets/Editor/WashMaterialTool.cs`（233） | UnityEditor | 选中物体循环分配材质（改名即可通用） |
| 场景体检与搭建 | `Assets/Editor/DigitalTwinSceneBuilder.cs`（223） | UnityEditor | 模型导入/居中/布光/相机自动配置的通用骨架（改模型路径常量即可） |

### 8.2 带上配套文件即可拆出（弱耦合）

| 复用包 | 文件清单 | 需要一并带上 / 改造点 |
| --- | --- | --- |
| 订单账本 | `Assets/Scripts/OrderBook.cs`（109） | 纯数据类 + 单例，无界面依赖，可原样复制 |
| 库存数据中枢 | `Assets/Scripts/ShelfStore.cs`（239） | 无界面依赖，但商品字段偏零售；换项目时改字段名即可作为通用事件型数据仓库 |
| 搜索与三维定位 | `Assets/Scripts/ProductSearch.cs`（186）、`ProductSpot.cs`（203） | 需带 `CameraTour.cs`（`FlyToPose`）；或把该调用换成自己的相机接口 |
| 断货预测引擎 | `Assets/Scripts/StockForecast.cs`（354） | 需带 `ShelfStore.cs`，或抽象出 `IStockSource` 接口；算法本身（速度/水位/建议量）通用 |
| 加减按钮 | `Assets/Scripts/ShopQtyButton.cs`（17） | 目前直接调 `ShopPopup.Step`，改成 `UnityEvent<int,int>` 即完全通用 |
| 灯光诊断工具 | `Assets/Editor/DTFindLights.cs`（65）、`DTScanLights.cs`（76）、`DTLightFixer.cs`（169） | 按对象编号匹配的规则需按新场景调整 |

### 8.3 强耦合，不建议单独拆出

`CrowdDashboard.cs`、`RestockPlanPanel.cs`、`ForecastTag.cs`、`ForecastBootstrap.cs`、`DemoDirector.cs`、`ShelfHeatmap.cs`、`ShelfPick.cs`、`StockAlertPanel.cs`、`StockAlertGlow.cs`、`DataPanelBinding.cs`、`ShopPopup.cs`、`ShopCheckout.cs`、`ReceiptView.cs`、`OrderHistoryView.cs`、`ShopWorldButton.cs`、`DTShopBuilder.cs`、`DTUIBuilder.cs`、`DTRestoreScene.cs`。

原因：它们或直接依赖 `ShelfStore`/`StockForecast` 的数据结构，或硬编码了本场景的 FBX 节点名、商品名、货架编号、机位下标与专用层（Layer 2 / Layer 30），脱离本工程需要较大改造。

### 8.4 迁移通用模块的最小步骤

1. 复制上表文件到目标工程 `Assets/Scripts/` 或 `Assets/Editor/`（编辑器脚本必须放 `Editor` 目录）。
2. 确认目标工程已装 `com.unity.ugui` 与 `com.unity.textmeshpro`；用 `UiKit` 时必须有至少一个可用的 TMP 字体资源。
3. 若目标工程使用 URP/HDRP，检查 `ShelfHeatmap` 类着色器名兜底逻辑（本清单中的零耦合包不涉及着色器，可忽略）。
4. 摄像机套件中 `CameraFollower` 依赖「专用层 + 叠加相机」，若不复制该层设置，可直接不带此文件。

---

## 9 快速上手

### 9.1 环境要求

- Unity **2022.3.62f3c1**（其他 2022.3.x 通常兼容，跨大版本需重新导入后处理包）
- macOS / Windows 均可；建议独立显卡，主模型约 725 万面
- 无需任何账号、无需联网、无需额外安装 Asset Store 资源

### 9.2 打开与运行

1. 用 Unity Hub 打开工程根目录（含 `Assets/`、`Packages/`、`ProjectSettings/`）。
2. 打开场景 `Assets/Scenes/CampusSupermarket.unity`（迁移后若打开空场景，`DTRestoreScene` 会自动恢复）。
3. 按 **Play**：`ForecastBootstrap`、`CrowdDashboard`、`DemoDirector` 会自动装配；约 1.5 秒后自动开始一键演示。
4. 按 **Esc** 退出演示，即可自由操作：点货架、点「点击选购」、搜索商品、按 F 开客流大屏、按 R 开补货建议单。

### 9.3 编辑器菜单一览

| 菜单 | 作用 |
| --- | --- |
| `Tools/数字孪生/① 一键搭建超市场景` | 从 FBX 重建场景（**会覆盖已有场景，先备份**） |
| `Tools/数字孪生/② 场景体检` | 输出渲染/材质统计 |
| `Tools/数字孪生/③ 放置购物篮` | 在场景视图焦点放置道具 |
| `Tools/数字孪生/④ 生成 UI 界面` | 重建基础界面 |
| `Tools/数字孪生/⑤ 生成选购与结账 UI` | 重建全部业务界面与绑定 |
| `Tools/贴图自动匹配`、`Tools/洗护用品材质工具` | 材质与贴图批处理 |
| `Tools/数字孪生/清晰度一键优化`、`仅优化字体图集` | 字体与贴图清晰度 |
| `DigitalTwin/查找发光对象`、`扫描发光对象`、`修复发光源灯光`、`配置后处理 (Bloom)` | 灯光与后处理诊断修复 |

> 注意：`① 一键搭建` 与 `⑤ 生成选购与结账 UI` 属于重建型工具，会覆盖场景中的同名对象，运行前请确认已保存或备份。

### 9.4 目录结构

```
SuperMarket/
├── Assets/
│   ├── Scenes/CampusSupermarket.unity     主场景
│   ├── Scripts/                           29 个运行时脚本
│   ├── Editor/                            13 个编辑器脚本
│   ├── SupermarketToUnity.fbx             超市主模型（约 725 万面）
│   ├── Textures/ Materials/ caizhi/      贴图与材质
│   ├── TextMesh Pro/                     TMP 字体资源
│   ├── PostProcessProfiles/               后处理配置
│   └── UI/ Models/                       界面图与模型
├── Packages/manifest.json                 仅官方包依赖
├── ProjectSettings/ProjectVersion.txt     Unity 版本
├── README.md                              本文档
└── docs/images/                           技术展示图与功能截图
```

### 9.5 常见改动入口

| 想改什么 | 去哪里 |
| --- | --- |
| 商品名称、单价、初始库存、低库存阈值 | `ShelfStore` 组件 Inspector 的 `items` 数组与 `lowThreshold` |
| 预测参数（营业时长、周转天数、提前期、紧急阈值） | `StockForecast` 组件 Inspector |
| 热力图五档颜色与阈值 | `ShelfHeatmap` 的 `ramp` 与 `thresholds` |
| 顾客数量、行走速度、购买概率 | `CrowdDashboard` 的 `customerCount` / `moveSpeed` / `buyChance` |
| 演示分幕内容、字幕、时长 | `DemoDirector.BuildScript()` 中的 `Beat` 列表 |
| 预设机位与飞行时长 | `CameraTour` 的 `presets` 与 `duration` |
| 新增一个跟随库存变化的视图 | 新建脚本，`OnEnable` 订阅 `ShelfStore.Instance.Changed`，`OnDisable` 取消，在回调里刷新即可 |

---

## 10 维护说明与后续建议

### 10.1 已知边界

- **数据不持久化**：所有库存、订单、预测结果为运行时内存态，退出播放即重置；如需跨会话保留，可在 `ShelfStore` 增加序列化存档。
- **单机单场景**：无多场景切换、无网络同步、无多人协作。
- **模型体量大**：725 万面模型对低端设备压力较大，若需上移动端建议做网格合并与 LOD。
- **专用层约定**：热力色板用 Layer 2、世界按钮用 Layer 30，改动项目 Layer 设置时需同步检查这两个脚本。
- **运行时生成界面**：界面不在场景文件里，编辑器状态下看不到完整 UI，需进入播放模式查看。

### 10.2 建议的后续增强

1. 给 `ShelfStore` 增加 JSON 存档，实现「经营简报 + 断点续玩」。
2. 把 `ShelfStore` 抽象为接口，便于第 8 节列出的预测、热力等模块真正零改造复用到其他零售场景。
3. 为 12 个模块补单元测试，重点覆盖 `Sell()` 边界（库存不足、数量为 0）、预测容量约束、事件订阅生命周期。
4. 把 `DT_Run` 系列自动化脚本整理进 `Assets/Editor/` 并纳入版本管理，避免工作区副本与工程代码分叉。

### 10.3 文档与图片清单

| 文件 | 说明 |
| --- | --- |
| `README.md` | 本文档 |
| `docs/images/architecture.png` | 系统架构图 |
| `docs/images/module-map.png` | 功能模块地图 |
| `docs/images/business-flow.png` | 核心业务流程图 |
| `docs/images/data-event-bus.png` | 数据联动机制图 |
| `docs/images/tech-stack.png` | 技术架构图 |
| `docs/images/demo-timeline.png` | 一键演示时间轴 |
| `docs/images/feature-checkout-sync.png` | 亮点功能截图：支付后库存同步 |
| `docs/images/feature-heatmap.png` | 亮点功能截图：动销热力图 |
| `docs/images/feature-alert-glow.png` | 亮点功能截图：缺货展示台告警发光 |

所有技术展示图均为本工程随文档同步输出的 PNG，Markdown 中使用相对路径引用，移动整个目录即可保持可用。

---

*文档基于工程实际代码与运行截图整理 · 统计口径见第 6 节 · 依赖核查见第 7 节*

# 智慧校园数字孪生 · 第一人称漫游系统

> **一句话定位**：在 TirGames 校园 3D 场景之上，用纯代码构建一套「数字孪生运维大屏 + 第一人称沉浸漫游」双模式交互系统，含程序化动态天气、门／灯／座椅物理交互与自建 HUD，零预制体、零 Asset Store 依赖。

| 项 | 内容 |
|---|---|
| 引擎 | 团结引擎 2022.3.62f3c1（Unity 2022.3 LTS 分支，`m_EditorVersionWithRevision: 1623fc0bbb97`） |
| 渲染管线 | URP 14.0.12（UniversalForward） |
| 主场景 | `Assets/TirGames/SchoolScene/Scenes/SchoolSceneDay.unity` |
| 核心代码 | `Assets/UI_Systems/` —— 36 个 C#（8,867 行）+ 4 个 Shader（556 行） |
| UI 技术 | uGUI（Legacy），**全部运行时用代码构建**，无 Prefab、无场景引用 |
| 运行方式 | 直接 Play 即可 —— `CampusUIBootstrap` 用 `[RuntimeInitializeOnLoadMethod]` 自举 |
| 外部依赖 | **零**。无 Asset Store 包、无网络服务、无第三方 SDK；贴图/音频全部程序化生成 |

---

## 目录

1. [项目背景与定位](#一项目背景与定位)
2. [功能汇总表](#二功能汇总表)
3. [核心功能技术实现](#三核心功能技术实现)
4. [三个最有亮点的功能（截图 + 原理）](#四三个最有亮点的功能)
5. [技术展示图](#五技术展示图)
6. [代码文件清单与总行数](#六代码文件清单与总行数)
7. [依赖清单](#七依赖清单)
8. [可拆出来给别的工程用的模块](#八可拆出来给别的工程用的模块)
9. [快速上手](#九快速上手)
10. [踩坑日志](#十踩坑日志)

---

## 一、项目背景与定位

### 1.1 背景

传统「智慧校园」类项目通常做成两张互不相干的皮：一张是给领导看的数据大屏（纯 2D 图表），一张是给参观者用的 3D 漫游（纯看风景）。两者数据不通、视角割裂 —— 大屏上看到「第三教学楼 3 层有告警」，却没法走过去确认；漫游时走到某栋楼前，也不知道这栋楼的实时状态。

本项目把两者做成**同一套系统的两个视角**：

- **视角 A（宏观 / 运维）**：200m 高空俯瞰整个校园，POI 与设备标记压在实际楼栋上，KPI、告警、用电趋势、图层开关一屏掌握。
- **视角 B（微观 / 体验）**：1.62m 人眼高度落地行走，推门进教室、开灯关灯、在长椅上坐下休息，天气随时间变化。

一键切换，共用同一份数据、同一套环境状态机。这是本项目最核心的业务价值 —— **运维视角与体验视角不再割裂**。

### 1.2 定位

面向 **智慧校园 / 园区数字孪生** 场景的**可复用交互框架 + 演示级完整实现**。不是游戏，也不是纯可视化看板，而是介于两者之间的「可走进去的数字孪生」。

### 1.3 解决的核心问题

| # | 问题 | 本项目怎么解决 |
|---|---|---|
| 1 | 大屏与漫游数据割裂 | `IDataService` 单一数据源，两个视角共用；`CampusModeManager` 一键切换并保持相机/UI 状态一致 |
| 2 | 场景资产是第三方买来的，不能改模型 | **零修改资产**：门/灯/座椅全部运行时按名字与包围盒自动识别并挂组件（`InteractableRegistry`），不碰任何 prefab |
| 3 | 580+ 把椅子、54 个门候选、68 盏灯，手工挂组件不现实 | 运行时扫描 + 尺寸体检 + 父子去重，一次性自动完成（实测：门 34、灯 68、座椅 104） |
| 4 | 换机器就跑不起来（贴图/音频缺失、授权问题） | 雨丝/雪花/云噪声贴图、雨声/风声/雷声**全部代码生成**（`ProceduralAssets`），工程零美术资源也能跑 |
| 5 | 天气只能靠后期滤镜，没法驱动玩法 | `EnvironmentState` 状态机同时驱动天空盒/主光/雾/粒子/云/音频/**移动手感**（雨雪天打滑） |
| 6 | HUD 在任意分辨率下都糊 | Canvas 开 `pixelPerfect`、CanvasScaler 锁 1920×1080、小地图 RT 提到 1024²、编辑器脚本自动切 Game 视图分辨率 |
| 7 | 门打不开 / 走不进去 / 坐下穿模 等一堆"手感"问题 | 逐条实测定位（量门洞净宽算胶囊直径、凸包填洞、优先级仲裁），详见[踩坑日志](#十踩坑日志) |

---

## 二、功能汇总表

### 2.1 模式与导航

| 功能模块 | 功能点 | 交互说明 |
|---|---|---|
| 双模式切换 | 进入第一人称 | 大屏右上「第一人称游览 →」，隐藏孪生 UI、禁用孪生相机、生成玩家与 HUD |
| 双模式切换 | 返回数字孪生 | HUD 右上「← 返回数字孪生」，反向恢复；若正坐着会先起身复位 |
| 自由相机（孪生） | 环绕 / 平移 / 缩放 | 右键拖拽环绕、中键拖拽平移、滚轮缩放（6~800m）、WASD 平移 |
| 自由相机（孪生） | 视角预设 / FlyTo | 1 秒 SmoothStep 平滑飞行到目标点，检索与告警定位复用同一套 |
| 第一人称移动 | 行走 / 奔跑 / 跳跃 | WASD 行走 5m/s，Shift 奔跑 9m/s，空格跳跃 4.5m/s |
| 第一人称视角 | 鼠标转视角 | 灵敏度 2.0 可调、支持 Y 轴反转、俯仰限位 ±80°；鼠标悬停 HUD 时自动暂停转头 |
| 瞬移 | 小地图点击瞬移 | 点击左下小地图任意位置，即时传送到对应世界点并向下射线贴地 |
| 瞬移 | 景点菜单瞬移 | 右侧「景点导览」8 个景点一键传送，到位后自动弹出讲解 |
| 瞬移 | 重置回出生点 | 回到 `SpawnPointFinder` 算出的马路出生位（含朝向） |

### 2.2 交互系统（F / E 键）

| 功能模块 | 功能点 | 交互说明 |
|---|---|---|
| 门 | 开门 / 关门 | 走到门前（半径按门宽 3.0~9.0m 自适应）底部弹「F 开门」，按 F 触发，0.9s ease-out 缓动 |
| 门 | 四种摆法纠正（R 键） | 合页端 × 开向 = 4 种组合，按 R 循环切换，选择写入 `PlayerPrefs` 永久记住 |
| 门 | 推离玩家 | 未手动定过摆法时，开门瞬间按玩家位置挑「门被推到你另一侧」的方向，模拟真实推门 |
| 灯 | 单盏开 / 关 | 走到灯下（半径 5.5m，锚点取地面投影）弹「F 关灯」，0.55s smoothstep 淡入淡出 |
| 灯 | 全部灯开 / 关 | 右下「灯光控制」面板批量控制，68 盏灯各自独立过渡 |
| 灯 | 夜间 / 白天模式 | 一键压暗太阳 + 室内灯全开 / 阳光恢复 |
| 座椅 | 坐下 | 走近长椅 / 椅子（半径 2.2m、需正对 0.6 点积）弹「F 坐下」，落座后视高降到 0.80m |
| 座椅 | 起身 | 按 F、或任意移动键、或空格起身（需先松开移动键，防"坐下立刻弹起"） |
| 座椅 | 坐姿环视 | 坐着时保留鼠标自由环视，移动与脚步禁用 |
| 通用 | 交互仲裁 | 门 0 > 座椅 1 > 灯 2 > 其他 5，同级取最近；10Hz 刷新避免每帧遍历 |

### 2.3 环境 / 天气系统

| 功能模块 | 功能点 | 交互说明 |
|---|---|---|
| 时间 | 白天 / 夜晚（+黄昏枚举） | 两级选择器一级；夜晚太阳压暗到 0.10×、星空出现、云变暗、泛蓝 |
| 天气 | 晴 / 多云 / 阴 / 雨 / 雷雨 / 雾 / 雪（7 档） | 两级选择器二级；2.4s SmoothStep 平滑过渡，不跳变 |
| 天气 | 天气自动轮换 | `WeatherDirector` 按转移权重链（晴→多云→阴→雨→雷雨…）90~200s 随机停留 |
| 天气 | 闪电 | 雷雨档触发，双闪（强 1.0 → 暗 0.16 → 弱 0.72），同步延迟雷声 |
| 天气 | 雨雪粒子 | 100×100m 发射盒跟随相机，雨 3200/s、雪 520/s，粒度为 World 空间模拟 |
| 天气 | 地面积水 | `WetGround` 40m 见方水面，打湿 8s / 蒸发 26s，反射当前天空色 + 程序化涟漪 |
| 天气 | 地面积雪 | `WeatherVisuals` 双层雪盘（近场 90m 高细分贴地 + 远场 260m 兜底），逐顶点射线贴地形 |
| 天气 | 树根积雪 | `SnowOnTrees` 读 Terrain TreeInstance，每棵树根生成雪堆圆盘，全场景合并为 1 个 DrawCall |
| 天气 | 云穹顶 | 半径 380m 的球壳，程序化 3D 噪声云，随天气变不透明度与色调 |
| 天气 | 环境音 | 雨声 / 风声 / 雷声全部程序化合成，音量随天气强度联动 |
| 天气 | 移动手感联动 | 雨天移速 ×0.90、雪天 ×0.80，脚步频率同步变慢 |
| 天气 | 室内外区分 | 进室内雪量淡出、积水关闭、雨雪粒子按碰撞判定淡出 |

### 2.4 数字孪生大屏

| 功能模块 | 功能点 | 交互说明 |
|---|---|---|
| KPI | 顶部 4 项指标 | 校园人数 / 设备在线率 / 待处理告警 / 综合能耗，全部由明细实时统计，非写死 |
| POI | 16 栋建筑世界锚点 | 标记压在实际楼栋上，随相机移动做透视投影，点击弹详情卡 |
| 设备 | 12 类设备标记 | 摄像头/烟感/消防栓/门禁/路灯/电表/充电桩/空调/温湿度/电梯/水泵/道闸，按类型着色 |
| 设备详情 | 右侧详情面板 | 名称、状态、温度、湿度、功率、水压、所属楼栋 |
| 告警 | 告警列表与定位 | 点击告警自动 FlyTo 到对应设备 |
| 图层 | 图层开关 | 建筑 / 设备 / 标注 / 地面分层显隐 |
| 检索 | POI 搜索 | 输入关键字过滤 POI 并飞行定位 |
| 统计 | 用电趋势图 | 24 小时数据用 `Texture2D` 逐像素绘制的折线图 |
| 显示选项 | 标注开关 / 恢复视角 | 标签显隐、快速回到校园全景 |

### 2.5 HUD（第一人称）

| 功能模块 | 功能点 | 交互说明 |
|---|---|---|
| 准星 | 屏幕中心十字 | 2×24 + 24×2 两根细线 |
| 交互键 | 底部胶囊提示 | 「F 开门 / R 换向」，按文字长度实测自适应宽度，淡入 + EaseOutBack 回弹 + 呼吸脉动 |
| 小地图 | 顶视 3D 小地图 | 正交相机 + 1024² RT 渲染真实场景，叠加层 512² 单独绘制玩家朝向扇形/景点/北向 |
| 讲解 | 就近自动讲解 | 走到景点 12m 内自动弹出左上讲解卡（标题 + 正文 + 鎏金分隔条） |
| 提示 | 顶部瞬移提示条 | 「已传送：图书馆」等，2.5s 自动消失 |
| 设置 | 音量 / 天气 / 帮助 | 居中弹窗，总音量 ±10%，昼夜×天气两级选择器，操作说明 |
| 主菜单 | 启动菜单 | 「开始漫游 / 帮助」，打开时禁用控制器防误操作 |
| 视觉规范 | 圆角卡片设计系统 | 深蓝黑底 + 鎏金主强调 + 青碧次强调，`UIStyle` 提供 Card/Button/AccentBar/Rounded 等构件 |

---

## 三、核心功能技术实现

### 3.1 整体技术栈

```
语言        C# 9（Unity 2022.3 默认）
引擎        团结引擎 2022.3.62f3c1
渲染        URP 14.0.12 · UniversalForward · 主光 PerPixel 附加光 · MSAA 4x
物理        CharacterController 胶囊 · MeshCollider 非凸墙 · Physics.OverlapBoxNonAlloc
UI          uGUI (Legacy) · 全部代码构建 · CanvasScaler ScaleWithScreenSize 1920×1080
输入        Legacy Input Manager（Input.GetAxis / GetKeyDown）
音频        AudioSource + 程序化 AudioClip
着色器      HLSL ShaderLab ×4（SnowGround / WetGround / CloudDome / SkyDome）
数据        ScriptableObject（CampusConfig）+ Mock 生成器
编辑器扩展  [InitializeOnLoadMethod] / MenuItem / 反射 UnityEditor.GameView
```

### 3.2 分层架构

```
┌─ 表现层 ── DigitalTwinUI（大屏）· FirstPersonUI（HUD）· WeatherPanel · UIStyle/UITheme
├─ 编排层 ── CampusModeManager（双模式）· CampusUIBootstrap（自举）· GameViewResolution
├─ 交互层 ── InteractableRegistry（仲裁）· InteractableBase · Door/Light/Seat · FirstPersonController
├─ 环境层 ── EnvironmentState（状态机）· EnvSample（预设）· WeatherVisuals · WeatherDirector
├─ 数据层 ── IDataService · MockDataService · CampusConfig · Models · SpawnPointFinder
└─ 引擎层 ── 团结引擎 2022.3 · URP 14.0.12 · CharacterController · uGUI · 4 个自定义 Shader
```

依赖方向严格单向：上层调用下层，下层不反向引用。跨层通信只经由 `CampusUI.Core` 的 `Models` 与两个单例（`EnvironmentState.Instance` / `InteractableRegistry` 静态表）。

### 3.3 双模式切换：`CampusModeManager`

**关键实现逻辑**：切换不是简单 `SetActive`，而是有严格顺序的 6 步，顺序错就会出现黑屏 / 走不动 / 相机跟错。

```csharp
public void EnterFirstPerson()
{
    if (fpMode) return;
    fpMode = true;
    if (fpCtrl == null) BuildFirstPerson();       // 首次才创建

    twinUI.SetUIVisible(false);                   // ① 只显隐画布，不 SetActive 整个 GO
    twinCam.locked = true;                        // ② 自由相机让权
    if (twinCamera != null) twinCamera.enabled = false;   // ③ 孪生相机禁用
    fpCtrl.gameObject.SetActive(true);            // ④ 玩家上线
    fpUI.gameObject.SetActive(true);              // ⑤ HUD 上线
    // ⑥ 不在此启用 fpCtrl —— 交给 FirstPersonUI.ShowMainMenu 控光标
}

private void BuildFirstPerson()
{
    InteractableRegistry.Rescan();                // 清掉上一次 Play 的静态缓存
    Vector3 spawn = SpawnPointFinder.Position;    // 读地形 splatmap 定位到马路
    float yaw = SpawnPointFinder.Yaw;
    // ... 创建 FPPlayer + FirstPersonController + FirstPersonUI
}
```

**为什么第 ① 步不能 `SetActive(false)`**：`DigitalTwinUI` 里有刷新 KPI 的协程，`SetActive(false)` 会把它杀掉，切回来就再也不刷新了。所以 `SetUIVisible` 只操作 Canvas 的 `enabled`。

**为什么第 ⑥ 步要延后**：`FirstPersonUI.ShowMainMenu(true)` 里会 `fpCtrl.enabled = false` 并释放光标。如果在 `EnterFirstPerson` 里先启用了控制器，主菜单打开时玩家会被锁住光标点不到按钮。

### 3.4 交互仲裁：`InteractableRegistry`

**关键实现逻辑**：三层过滤 + 两段排序。

```csharp
public static InteractableBase FindNearest(Vector3 playerPos, Vector3 forward)
{
    Vector3 fwd = new Vector3(forward.x, 0f, forward.z).normalized;

    InteractableBase best = null;
    int bestPrio = int.MaxValue;
    float bestDist = float.MaxValue;

    for (int i = 0; i < s_all.Count; i++)
    {
        var it = s_all[i];
        Vector3 d = it.Anchor - playerPos;
        d.y *= 0.35f;                              // ① 垂直弱化：吊灯也够得着
        float dist = d.magnitude;
        if (dist > it.radius) continue;            // ② 半径过滤

        Vector3 flat = new Vector3(d.x, 0f, d.z);
        if (flat.sqrMagnitude > 0.0001f)
        {
            float dot = Vector3.Dot(fwd, flat.normalized);
            if (dot < it.MinFacingDot) continue;    // ③ 朝向过滤（门 0.2 / 座椅 0.6）
        }

        int prio = it.Priority;                    // ④ 优先级优先，同级比距离
        if (prio < bestPrio || (prio == bestPrio && dist < bestDist))
        { bestPrio = prio; bestDist = dist; best = it; }
    }
    return best;
}
```

**自动挂载的三类资产**（全部按名字 + 包围盒识别，不碰 prefab）：

| 类型 | 识别规则 | 尺寸体检 | 实测命中 |
|---|---|---|---|
| 门 | 名字含 `door`，排除 `post/frame/handle/knob/opened/glass/hinge/lock/mat` | 高 0.6~12m、宽 0.25~14m | 候选 54 → 跳过 8 → **34 个** |
| 灯 | `Light` 组件、非 Directional、启用、强度 > 0.001 | —— | **68 盏** |
| 座椅 | 名字含 `bench/chair/seat/stool/椅/凳`，或 `Benches` 直属子物体 | 高 0.25~1.4m、水平边 0.35~3.2m | **104 个** |

父子去重：候选按层级深度从浅到深排序，`HasDoorAncestor` / `HasSeatAncestor` 检查父级是否已挂组件。这解决了「门扇根 + 子门扇」结构被父子各转一次、门飞到错误位置的 bug。

### 3.5 开门：`DoorInteractable`

三个难点逐个解决：

**① 合页点必须落在门板真实边缘** —— 用 `Renderer.localBounds` 逐级变换，不用世界 AABB 反算。因为场景根节点 `Exterior` 绕 (1,1,1) 转了 −120°，世界 AABB 会被放大成大斜块，反算出的合页点在门外面。

**② 合页在哪一端** —— 三级判定：
1. 轴心偏移（门资产惯例：轴心几乎总在合页端）→ 最可靠；
2. 沿开门弧线密集采样 25°/50°/75°/95°，用 `Physics.OverlapBoxNonAlloc` 数阻挡，取撞得最少的；
3. 都不确定时用默认摆法（max 端 + 负角度）。

**③ 往哪侧推开** —— 静态几何推不出来（走廊两侧常常一样空），改成按键那一刻按玩家位置实时判定：

```csharp
private void PickSwingAwayFromPlayer()
{
    // 两个候选摆法合页端相同、只有角度正负不同，
    // 比较"完全打开时门扇中心"与玩家的距离，取更远的（门被推到了另一侧）
    float dCur = (vCenter[variant] - pp).sqrMagnitude;
    float dAlt = (vCenter[other] - pp).sqrMagnitude;
    if (dAlt > dCur + 0.05f) variant = other;   // 留 0.05 死区，防站在延长线上抖动
}

// 转动：绕「过合页点、平行世界 Y 轴」的直线
private void ApplyPose(float tt)
{
    float e = 1f - (1f - tt) * (1f - tt) * (1f - tt);   // ease-out cubic：起步快、到位减速
    Quaternion d = Quaternion.AngleAxis(openAngle * e, Vector3.up);
    transform.position = hingeWorld + d * (closedPos - hingeWorld);
    transform.rotation = d * closedRot;
}
```

### 3.6 坐下 / 起身：`SeatInteractable` + `FirstPersonController`

**坐面高度按整体高度分类判定**，兼容两类完全不同的资产：

```csharp
// 无靠背长椅 / 凳子（整体 ≤0.62m，如 Bench01_1）→ 顶面就是坐面
// 带靠背椅子（整体 >0.62m，如 Chair01a 约 0.9m）→ 坐面取离地 0.45m 座高
if (h <= 0.62f) seatTop = ab.max.y;
else seatTop = Mathf.Clamp(ab.min.y + 0.45f, ab.min.y + 0.30f, ab.max.y - 0.12f);
```

**落座朝向必须翻转**（修「坐下穿模」）：玩家是面朝椅子走过去的，沿用原朝向坐下后就正对椅背，摄像机贴在靠背上必然穿模。所以落座朝向取「背离椅子中心」：

```csharp
Vector3 fromSeat = transform.position - seat.SitPosition;   // 椅子中心 → 玩家站立处
fromSeat.y = 0f;
if (fromSeat.sqrMagnitude > 0.04f)                          // 距离 > 20cm，方向可信
{
    fromSeat.Normalize();
    yaw = Mathf.Atan2(fromSeat.x, fromSeat.z) * Mathf.Rad2Deg;
}
Vector3 sitPos = seat.SitPosition + fromSeat * 0.12f;       // 再前移 12cm，离开椅背
```

**起身键的边沿判定**（修「坐下立刻弹起」）：玩家通常是「按着 W 走到椅子前按 F」，若直接看按键状态，坐下下一帧就因为 W 还按着而立刻起身：

```csharp
bool moveKey = Mathf.Abs(Input.GetAxisRaw("Horizontal")) > 0.01f
            || Mathf.Abs(Input.GetAxisRaw("Vertical")) > 0.01f;
if (!moveKey) sitKeysReleased = true;                       // 必须先松开
if (sitKeysReleased && (Input.GetKeyDown(KeyCode.Space) || moveKey)) Stand();
```

**已坐座椅优先级降到 −1**，保证任何情况下 F 都解析成「起身」而不是旁边的门 / 灯。同时 `Anchor` 在坐着时返回玩家自身位置 —— 距离≈0，且落座后转身 180° 也不会被朝向过滤掉。

### 3.7 天气状态机：`EnvironmentState` → `EnvSample` → `WeatherVisuals`

**参数合成模型**：天气预设 × 昼夜修正是**叠乘**关系，不是查表。

```csharp
private static EnvSample Build(TimeOfDay t, Weather w)
{
    EnvSample s = EnvSample.Get(w);       // 先取天气基准（白天口径）

    // 再乘昼夜修正
    case TimeOfDay.Night:
        sunMul = 0.10f; expMul = 0.34f; ambMul = 0.30f;
        skyMul = 0.20f; blueBias = 1.70f; fogMul = 0.42f;
        sunTint = new Color(0.58f, 0.68f, 1f);      // 月光偏蓝
        break;

    s.sunIntensity *= sunMul;
    s.skyTint = new Color(s.skyTint.r * skyMul, s.skyTint.g * skyMul,
                          Mathf.Min(1f, s.skyTint.b * skyMul * blueBias));  // 夜天保蓝
    if (t == TimeOfDay.Night) s.sunDisk *= 0.12f;   // 夜里不该挂个大太阳
    if (t == TimeOfDay.Night) s.star = 1f;          // 星空只在夜里
    return s;
}
```

**一次切换驱动 6 个子系统**：天空盒（`_SkyTint`/`_Exposure`/`_AtmosphereThickness`/`_SunSize`）、主光（颜色/强度）、环境光、内置雾（`ExponentialSquared` + 高度衰减）、雨雪云星空粒子、环境音。

**三个关键实现决定**（都是踩过的坑）：

1. **天空盒必须运行期新建**。`new Material(Shader.Find("Skybox/Procedural"))`，改 `RenderSettings.skybox` 的内存引用。若直接改场景自带的 `.mat` 会写脏磁盘资产。
2. **雾用 `RenderSettings.fog`**。URP 14 没有体积雾，但支持内置线性/指数雾，`URP/Lit` 与 Terrain 着色器都会采样，代价几乎为零。
3. **`DynamicGI.UpdateEnvironment()` 必须节流**。它会重算天空盒到 cubemap 的卷积，每帧调会明显掉帧 —— 这里 0.2s 一次，过渡结束后自然停下。

**雾密度按相机高度衰减**（同一个密度在 200m 高空俯视和 2m 地面完全是两种效果）：

```csharp
float h = cam.transform.position.y;
float hk = Mathf.Lerp(1f, 0.30f, Mathf.Clamp01((h - 5f) / 180f));
RenderSettings.fogDensity = s.fogDensity * hk;
```

### 3.8 地面积雪：双层雪盘 + 逐顶点贴地

**问题**：一块平面雪盘铺在地面上，遇到花坛、台阶、铺装凸起就会被戳穿，或者悬空。

**方案**：近场高细分 + 远场低精度兜底，逐顶点向下射线贴地。

```csharp
private const float SnowGroundSize    = 90f;    // 近场：高细分、贴地形、带雪堆起伏
private const float SnowGroundFarSize = 260f;   // 远场：低精度白兜底，覆盖近场到雾边界
private const float SnowGroundLiftNear = 0.30f; // 抬离射线采到的地面高度
private const float SnowGroundLiftFar  = 0.35f;

// 逐顶点贴地
float th = GroundY(worldX, worldZ);             // Physics.RaycastNonAlloc
float d = Mathf.Clamp((th + SnowGroundLiftNear) - baseY, -1.5f, 1.5f);
```

渲染配置：`ZTest LEqual` + `ZWrite Off` + `Blend SrcAlpha OneMinusSrcAlpha`，近场 `renderQueue 2996`、远场 `2995`（保证近场压住远场）。

**为什么抬升 0.30m 而不是贴合地形**：脚下庭院/广场是 `SchoolBuilding.FBX` 里的铺装网格，它高出地形、且**没有碰撞体**，所以向下射线只采到地形而非可见铺装面。雪面若停在 `地形+0.10`，就低于可见砖面，`ZTest LEqual` 会让砖面戳穿雪面，看起来"雪只积在砖缝里、整片发虚"。

### 3.9 HUD 高清化

**根因**：Game 视图不最大化时显示区约 1253×616，HUD 按 1920×1080 设计，`CanvasScaler` 的 `scaleFactor` 被压到 0.65，`fontSize 13` 的字只剩 8 个像素高。

**四处修复**：

| 位置 | 改动 | 效果 |
|---|---|---|
| 三个 Canvas | `canvas.pixelPerfect = true` | UI 顶点落整像素，消除半像素采样 |
| 三个 CanvasScaler | `ScaleWithScreenSize` + `MatchWidthOrHeight 0.5` + `referencePixelsPerUnit 100` | 与 `Sprite.Create(...,100f,...)` 对齐，1:1 不重采样 |
| 小地图 RT | 512→**1024**（主图）、256→**512**（叠加层）+ `Bilinear` | 顶视相机覆盖整个校园，512 时每像素近 1m |
| `GameViewResolution.cs` | 反射切 1920×1080 + 开 `m_MaximizeOnPlay` | Play 时 Game 视图占满窗口，可用像素最多 |

### 3.10 程序化资源：`ProceduralAssets`

零美术依赖的关键。全部在 `Awake` 生成一次并静态缓存：

- **贴图**：雨丝（8×64 竖向细条，顶亮底淡，模拟运动模糊）、雪花、云噪声（含 3D 版）、星空、雪斑、湿痕遮罩
- **音频**：雨声（白噪 + 低通包络）、风声（带通噪声 + 慢速幅度调制）、雷声（低频轰隆 + 衰减尾巴），44.1kHz 单声道

```csharp
// 雨丝贴图：顶端亮、底端淡
float v = y / (float)(H - 1);          // 0 = 底，1 = 顶
float a = Mathf.Pow(v, 1.7f);          // 运动模糊感
float dx = Mathf.Abs(x - (W - 1) * 0.5f) / ((W - 1) * 0.5f);
float edge = 1f - dx * dx;             // 中间实、两边虚
byte b = (byte)(Mathf.Clamp01(a * edge) * 255f);
```

### 3.11 出生点：`SpawnPointFinder`

不硬编码坐标，而是读地形 splatmap 找「马路 / 铺装路面」（splat 权重最高的通道落在硬质路面图层），再推导朝向面向校园中心。这样换场景或改地形也不会掉进草地里或悬空。

### 3.12 第一人称胶囊尺寸（卡门框的根因）

```csharp
// 实测 Door02 门扇 BoxCollider 宽 0.8407m、高 1.9434m，门洞净宽约 0.85m
// 门开到 95° 还会占掉 0.84*cos(95°) ≈ 7cm，实际可通行宽度 ≈ 0.78m
cc.radius = 0.30f;                    // 旧值 0.4 → 直径 0.8m + skin 0.06 = 有效 0.86m > 门洞
cc.height = 1.75f;                    // 门洞净高 1.94m，留 19cm 余量
cc.center = new Vector3(0f, 0.875f, 0f);   // 胶囊底面对齐 transform.position
cc.stepOffset = 0.40f;                // 跨门槛
```

新配置有效直径 0.66m，门洞余量约 12cm。

---

## 四、三个最有亮点的功能

### 亮点 1 · 数字孪生大屏 ⇄ 第一人称漫游 一键双向切换

![数字孪生大屏](docs/images/06-运行截图-数字孪生大屏.png)

*数字孪生模式：200m 高空俯瞰，POI 与设备标记压在实际楼栋上，左侧图层面板 / 右侧设备详情与告警 / 顶部 KPI 一屏掌握。*

![第一人称漫游](docs/images/07-运行截图-第一人称漫游.png)

*第一人称模式：1.62m 人眼高度，左下小地图（顶视真实场景 + 朝向扇形）、右侧景点导览、底部 F 键交互胶囊、左上就近讲解。*

**实现原理**：两个视角不是两套场景，而是**同一条渲染管线上的两台相机 + 两套画布**。`CampusModeManager` 通过 6 步有序切换（画布显隐 → 自由相机锁定 → 孪生相机禁用 → 玩家上线 → HUD 上线 → 光标门控）完成接管，全程不销毁任何对象，因此切换零加载、零重建。

切换时 `EnvironmentState.RebindCamera()` 会把雨雪粒子发射器与雾密度重新绑定到新相机 —— 否则从 200m 高空切到地面，雨雪还挂在天上、雾还是按俯视高度算的。

学生/设备的实时数据由 `IDataService` 单点提供，两个视角读同一份，所以大屏上点「第三教学楼告警」，切到漫游走过去，看到的还是同一个告警。

---

### 亮点 2 · 程序化动态天气（7 档 + 昼夜 × 双层贴地积雪 + 积水 + 打滑手感）

![雪天场景](docs/images/08-运行截图-雪天场景.png)

*雪天：地面积雪整片覆盖铺装面与花坛，天空与雾同调变冷灰，粒子在玩家周围 100m 盒内持续飘落。*

**实现原理**：三层结构 —— `EnvironmentState`（状态机 + 过渡插值）→ `EnvSample`（16 参数预设表）→ `WeatherVisuals`（视觉落地）。

一次 `Set(TimeOfDay, Weather)` 会在 2.4s 内用 SmoothStep 插值出一整套中间态，同时驱动：天空盒 tint/曝光/大气厚度/太阳圆盘、平行光颜色与强度、环境光、指数雾（密度按相机高度衰减）、雨雪粒子、地面积水、地面积雪、树根积雪、云穹顶、星空、雨声风声雷声、**以及第一人称的移动手感**（雨 ×0.90、雪 ×0.80，脚步频率同步变慢 —— 玩家不需要看到数值，走起来"有点滑"就够了）。

积雪是技术难点：单块平面会被花坛、台阶、铺装凸起戳穿。解法是**双层雪盘 + 逐顶点向下射线贴地** —— 近场 90m 高细分（带雪堆起伏噪声），远场 260m 低精度白兜底（消除地平线方向裸地形缺口），并把雪面抬离射线采到的地面 0.30/0.35m，盖过铺装凸起。

所有贴图与音频**零素材依赖**，全部由 `ProceduralAssets` 在运行时用代码生成（雨丝 8×64 贴图、3D 噪声云、44.1kHz 雨/风/雷音频）。

---

### 亮点 3 · 零预制体侵入的运行时交互框架（门 / 灯 / 座椅自动挂载 + 优先级仲裁）

**实现原理**：第三方买来的场景有 34 个门、68 盏灯、104 把椅子，逐个手拖组件不现实，而且 prefab 一改就丢。所以整套交互是**运行时按名字与包围盒自动识别**的：

- **门**：名字含 `door` 且不含 `post/frame/handle/...`，尺寸体检通过 → 挂 `DoorInteractable`，自动算合页端与开向。
- **灯**：`Light` 组件、非平行光、已启用 → 挂 `LightInteractable`，锚点取地面投影（灯在天花板上，否则永远够不着）。
- **座椅**：名字含 `bench/chair/seat/stool/椅/凳`，或 `Benches` 直属子物体 → 挂 `SeatInteractable`，按整体高度判定坐面高度。

识别完不直接给玩家用，而是交给 `InteractableRegistry` 做**优先级仲裁**：门 0 > 座椅 1 > 灯 2 > 其他 5，同级比距离，再叠一层朝向过滤（门 0.2 宽松、座椅 0.6 严格）。这个分层是必须的 —— 门口成排的椅子会抢掉门（进不去室内），室内的灯会抢掉椅子（永远只能关灯），两个坑都实际踩过。

玩家侧统一只有一句 `InteractableRegistry.FindNearest(pos, fwd)`，10Hz 刷新。新增一种可交互物只需要继承 `InteractableBase` 并实现 `ActionLabel` / `Interact()` 两个成员，注册表会自动收集。

---

## 五、技术展示图

### 5.1 系统分层架构

![系统分层架构](docs/images/01-系统分层架构.png)

### 5.2 双模式切换状态机

![双模式状态机](docs/images/02-双模式状态机.png)

### 5.3 交互系统执行流程

![交互系统流程](docs/images/03-交互系统流程.png)

### 5.4 天气系统数据流

![天气系统数据流](docs/images/04-天气系统数据流.png)

### 5.5 代码目录结构

![代码目录结构](docs/images/05-代码目录结构.png)

---

## 六、代码文件清单与总行数

### 6.1 汇总

| 类别 | 文件数 | 行数 |
|---|---:|---:|
| C# 运行时脚本（Scripts/） | 29 | 7,931 |
| C# 编辑器脚本（Editor/） | 6 | 936 |
| **C# 合计** | **35** | **8,867** |
| Shader（Resources/） | 4 | 556 |
| **总计** | **39** | **9,423** |

### 6.2 表现层 `Scripts/UI/`

| 文件 | 行数 | 职责 |
|---|---:|---|
| `DigitalTwinUI.cs` | 803 | 数字孪生大屏：KPI / 图层面板 / 设备标记 / 详情 / 告警 / 趋势图 / POI 弹窗 |
| `FirstPersonUI.cs` | 899 | 第一人称 HUD：准星 / 讲解 / 交互胶囊 / 小地图 / 景点菜单 / 设置 / 主菜单 |
| `UIStyle.cs` | 445 | 视觉构件库：圆角卡片、按钮、强调条、描边、文字测量 |
| `FirstPersonController.cs` | 343 | 第一人称控制器：移动 / 视角 / 坐姿状态机 / 胶囊尺寸 / 凸包修复 |
| `WeatherPanel.cs` | 229 | 两级昼夜 × 天气选择器（宽度/列数/字号由调用方传入） |
| `FreeCameraController.cs` | 113 | 孪生自由相机：环绕 / 平移 / 缩放 / FlyTo |
| `CampusModeManager.cs` | 85 | 双模式切换编排（6 步有序） |
| `UITheme.cs` | 73 | 配色体系：深蓝黑底 + 鎏金 + 青碧 + 三级文字 |
| `LayerController.cs` | 28 | 图层显隐 |
| `UIBuiltinFont.cs` | 28 | 内建字体获取（`Arial.ttf` → `LegacyRuntime.ttf` 兜底） |
| `CampusUIBootstrap.cs` | 25 | `[RuntimeInitializeOnLoadMethod]` 自举，零手工挂载 |

### 6.3 环境层 `Scripts/Environment/`

| 文件 | 行数 | 职责 |
|---|---:|---|
| `WeatherVisuals.cs` | 1,001 | 雨雪粒子 / 云穹顶 / 双层贴地雪盘 / 星空 / 积水跟随 / 室内外淡出 |
| `ProceduralAssets.cs` | 668 | 程序化贴图（雨丝/雪花/云/星/雪斑/湿痕）+ 音频（雨/风/雷） |
| `EnvSample.cs` | 219 | 16 参数结构体 + 7 套天气预设 + `Lerp` 插值 |
| `SnowOnTrees.cs` | 163 | 树根积雪：读 TreeInstance，全场景合并为 1 个 DrawCall |
| `WeatherDirector.cs` | 147 | 天气自动轮换：转移权重链 + 90~200s 随机停留 |
| `WetGround.cs` | 126 | 雨天积水层：打湿 8s / 蒸发 26s / 天空色下发 |

### 6.4 交互层 `Scripts/Interact/`

| 文件 | 行数 | 职责 |
|---|---:|---|
| `DoorInteractable.cs` | 415 | 开门：合页推断 / 弧线阻挡采样 / 四种摆法 / 推离玩家 / PlayerPrefs 持久化 |
| `InteractableRegistry.cs` | 224 | 场景扫描 + 优先级仲裁 + 朝向过滤 |
| `SeatInteractable.cs` | 180 | 坐下：坐面高度判定 / 半径 2.2m / 已坐时优先级 −1 |
| `LightInteractable.cs` | 148 | 开关灯：smoothstep 亮度缓动 / 批量控制 / 地面投影锚点 |
| `InteractableBase.cs` | 58 | 抽象基类：`radius` / `Anchor` / `Priority` / `MinFacingDot` / `Interact()` |

### 6.5 核心与数据层 `Scripts/Core/` + `Scripts/Data/`

| 文件 | 行数 | 职责 |
|---|---:|---|
| `EnvironmentState.cs` | 512 | 环境状态机：天气 × 昼夜叠乘 / 2.4s 过渡 / 闪电 / GI 节流 / 移动手感 |
| `SpawnPointFinder.cs` | 370 | 读地形 splatmap 定位马路出生点 + 朝向推导 |
| `MockDataService.cs` | 324 | 演示数据生成：16 栋建筑 / 96 台设备 / 8 个景点 / 告警 |
| `CampusAligner.cs` | 200 | 数据坐标对齐真实楼栋群质心 |
| `Models.cs` | 75 | 数据模型：`PoiInfo` / `DeviceInfo` / `ScenicSpot` / `AlarmInfo` / `KpiSnapshot` |
| `IDataService.cs` | 15 | 数据访问接口（换真实后端只改实现） |
| `CampusConfig.cs` | 15 | ScriptableObject 数据容器 |

### 6.6 编辑器工具 `Editor/`

| 文件 | 行数 | 职责 |
|---|---:|---|
| `MaterialProbe.cs` | 179 | URP 品红材质体检 + 一键修复（菜单 Tools ▸ 校园UI） |
| `GameViewResolution.cs` | 177 | Play 自动切 1920×1080 + 最大化（反射 `UnityEditor.GameView`） |
| `DoorProbe.cs` | 93 | 门识别诊断：输出每个候选的尺寸、合页端、四种摆法阻挡数 |
| `SpawnPointProbe.cs` | 93 | 出生点诊断 |
| `FixMainCamera.cs` | 91 | 主相机校正 |
| `CleanMissingPrefabs.cs` | 73 | 清理丢失引用的 prefab 实例 |

### 6.7 Shader `Resources/`

| 文件 | 行数 | 职责 |
|---|---:|---|
| `WetGround.shader` | 191 | 积水：解析天空反射 + 程序化涟漪 + 湿滑度遮罩 |
| `SnowGround.shader` | 155 | 积雪：主光 + 球谐环境光 + 接收阴影 + 融雾 + 边缘柔和 |
| `CloudDome.shader` | 105 | 云穹顶：3D 噪声云 |
| `SkyDome.shader` | 105 | 天空穹顶兜底 |

---

## 七、依赖清单

### 7.1 Asset Store 包

| 项 | 是否依赖 | 说明 |
|---|---|---|
| **TirGames – School Scene** | ✅ **是（唯一一个）** | 项目的基础 3D 场景资产。`Assets/TirGames/SchoolScene/`，1.2GB，含校园建筑/教室/体育馆/食堂/树木/长椅等 80+ prefab。v1.4，需 Unity 5.5+。原始 README 见 `Assets/TirGames/ReadMeSchoolScene.txt`。**本项目未修改其任何 prefab 或材质资产**，只在其上叠加运行时系统。 |
| 其他 Asset Store 包 | ❌ 否 | 无 |

> 若换掉这个场景资产，`Assets/UI_Systems/` 仍可独立工作 —— 见第八章。

### 7.2 Unity 官方包（`Packages/manifest.json`）

| 包 | 版本 | 用途 |
|---|---|---|
| `com.unity.render-pipelines.universal` | 14.0.12 | URP 渲染管线（必需） |
| `com.unity.ugui` | 1.0.0 | uGUI（必需，全部 UI 靠它） |
| `com.unity.modules.physics` | 1.0.0 | CharacterController / 射线 / OverlapBox（必需） |
| `com.unity.modules.particlesystem` | 1.0.0 | 雨雪粒子（必需） |
| `com.unity.modules.terrain` + `terrainphysics` | 1.0.0 | 地形 splatmap 出生点（必需） |
| `com.unity.modules.audio` | 1.0.0 | 环境音（必需） |
| `com.unity.textmeshpro` | 3.0.7 | 已安装但**未使用**（全用 Legacy uGUI `Text`） |
| `com.unity.timeline` | 1.7.7 | 已安装但未使用 |
| `com.unity.visualscripting` | 1.9.4 | 已安装但未使用 |
| `com.unity.collab-proxy` / `ide.*` / `test-framework` | — | 工程模板自带，与功能无关 |

### 7.3 外部服务 / 网络 / SDK

| 项 | 是否依赖 | 说明 |
|---|---|---|
| 网络请求 | ❌ 无 | 无任何 `UnityWebRequest` / HTTP 调用 |
| 后端 API | ❌ 无 | 数据全部走 `MockDataService`（本地生成）；接真实后端只需实现 `IDataService` |
| 数据库 | ❌ 无 | 唯一持久化是 `PlayerPrefs`（存门的摆法偏好） |
| 第三方 SDK | ❌ 无 | 无登录 / 支付 / 统计 / 推送 / 广告 SDK |
| 外部贴图 / 音频资源 | ❌ 无 | 天气系统的贴图与音频全部代码生成（`ProceduralAssets`） |
| AI / 云服务 | ❌ 无 | — |

> **结论：除 TirGames 场景资产外，本项目零外部依赖**。丢到任何装了 URP 的空工程里，把 `Assets/UI_Systems/` 拷过去就能跑。

---

## 八、可拆出来给别的工程用的模块

按「自包含程度」从高到低排列。**A / B / C 三组是真正可以原样拷走的**，D 组需要少量适配。

### A. 天气系统（**完全自包含，最推荐复用**）

> 已单独整理为 4,942 行技术文档 + 120KB 代码包：`WeatherSystem_TechDoc.md` / `WeatherSystem_Package.zip`

| 文件 | 行数 |
|---|---:|
| `Scripts/Core/EnvironmentState.cs` | 512 |
| `Scripts/Environment/EnvSample.cs` | 219 |
| `Scripts/Environment/WeatherVisuals.cs` | 1,001 |
| `Scripts/Environment/WeatherDirector.cs` | 147 |
| `Scripts/Environment/WetGround.cs` | 126 |
| `Scripts/Environment/SnowOnTrees.cs` | 163 |
| `Scripts/Environment/ProceduralAssets.cs` | 668 |
| `Scripts/UI/WeatherPanel.cs` | 229 |
| `Resources/SnowGround.shader` | 155 |
| `Resources/WetGround.shader` | 191 |
| `Resources/CloudDome.shader` | 105 |
| `Resources/SkyDome.shader` | 105 |
| **合计** | **3,621** |

**依赖**：仅 `CampusUI.Core.Models` 里的 `Weather` / `TimeOfDay` 两个枚举（可内联进 `EnvSample.cs`）。不依赖场景、不依赖 prefab、不依赖任何美术资源。

**接入方式**：拷入工程 → 场景里挂一个空 GameObject 加 `EnvironmentState` → 调 `EnvironmentState.Instance.SetWeather(Weather.Snow)` 即可。表情/天空盒/雾/雨雪/积雪/积水/环境音/移动手感系数全自动。

### B. 交互框架（自包含，需实现一个 `PlayerPosition` 喂入口）

| 文件 | 行数 |
|---|---:|
| `Scripts/Interact/InteractableBase.cs` | 58 |
| `Scripts/Interact/InteractableRegistry.cs` | 224 |
| `Scripts/Interact/DoorInteractable.cs` | 415 |
| `Scripts/Interact/LightInteractable.cs` | 148 |
| `Scripts/Interact/SeatInteractable.cs` | 180 |
| **合计** | **1,025** |

**依赖**：
- `SeatInteractable` / `DoorInteractable` 里 `FirstPersonController` 的 `Sit/Stand/IsSeated` —— 换控制器时把这三处调用换掉即可；
- `InteractableBase.PlayerPosition` 需要外部每帧写入（本项目在 `FirstPersonUI.Update` 里写）。

**接入方式**：`InteractableRegistry.EnsureScanned()` 一次性扫描挂载 → 每 0.1s `FindNearest(pos, fwd)` → 按 F 调 `Interact()`。三类交互物的识别规则都是通用几何规则，不写死任何场景路径。

### C. UI 视觉规范（自包含）

| 文件 | 行数 |
|---|---:|
| `Scripts/UI/UIStyle.cs` | 445 |
| `Scripts/UI/UITheme.cs` | 73 |
| `Scripts/UI/UIBuiltinFont.cs` | 28 |
| **合计** | **546** |

**依赖**：仅 uGUI。提供 `Card` / `Button` / `AccentBar` / `Rounded` / `RoundedOutline` / `Outline` / `MeasureText` / `MinButtonWidth` 等构件，圆角 Sprite 全部运行时生成，不需要任何美术切图。

### D. 需适配后复用

| 模块 | 文件 | 行数 | 适配点 |
|---|---|---:|---|
| 第一人称控制器 | `Scripts/UI/FirstPersonController.cs` | 343 | 胶囊尺寸按自己的门宽重算；`FixConvexWalls()` 可删 |
| 双模式编排 | `Scripts/UI/CampusModeManager.cs` | 85 | 换成自己的两个视角入口 |
| 出生点定位 | `Scripts/Core/SpawnPointFinder.cs` | 370 | 读 splatmap 的逻辑依赖地形图层命名 |
| 数字孪生大屏 | `Scripts/UI/DigitalTwinUI.cs` + `Core/*` | 1,400+ | 强绑定本项目的 POI/设备数据模型 |
| 程序化资源 | `Scripts/Environment/ProceduralAssets.cs` | 668 | **可单独复用**，任意工程需要雨丝贴图/雨声时直接调 |

### 不建议复用

`SpawnPointProbe` / `DoorProbe` / `MaterialProbe` / `FixMainCamera` / `CleanMissingPrefabs` 都是针对本场景的一次性诊断工具，换工程即失效。

---

## 九、快速上手

### 9.1 环境要求

- 团结引擎 2022.3.62f3c1 或 Unity 2022.3 LTS 任意补丁版本
- URP 14.0.12（`Packages/manifest.json` 已锁）
- 打开场景 `Assets/TirGames/SchoolScene/Scenes/SchoolSceneDay.unity`
- 直接按 Play —— `CampusUIBootstrap` 会自动挂载 `DigitalTwinUI` + `CampusModeManager`，无需手动拖任何东西

### 9.2 首次运行会看到

1. 相机平滑飞到校园中心上空（`FrameCampus`）
2. 顶部 KPI 逐帧刷新，左侧图层面板、右侧设备详情与告警列表就位
3. 点右上「第一人称游览 →」进入漫游，弹出启动主菜单
4. 点「开始漫游」，落在校园马路上，面向建筑群

### 9.3 操作速查

| 操作 | 键位 |
|---|---|
| 移动 / 奔跑 / 跳跃 | WASD / Shift / Space |
| 转视角 | 鼠标移动 |
| 交互（开门 / 开关灯 / 坐下） | F 或 E |
| 切换门的开合方向 | R（对着门时） |
| 起身 | F / 移动键 / Space（需先松开移动键） |
| 小地图瞬移 | 鼠标点击小地图 |
| 缩放 / 环绕 / 平移（孪生） | 滚轮 / 右键拖拽 / 中键拖拽 |

### 9.4 关键目录

```
Assets/UI_Systems/
├── Scripts/
│   ├── Core/           EnvironmentState · SpawnPointFinder · Models · IDataService · MockDataService
│   ├── Data/           CampusConfig（ScriptableObject）
│   ├── Environment/    WeatherVisuals · EnvSample · WeatherDirector · WetGround · SnowOnTrees · ProceduralAssets
│   ├── Interact/       InteractableBase · InteractableRegistry · Door/Light/Seat
│   └── UI/             DigitalTwinUI · FirstPersonUI · FirstPersonController · UIStyle · UITheme ...
├── Editor/             GameViewResolution · MaterialProbe · DoorProbe ...
└── Resources/          SnowGround · WetGround · CloudDome · SkyDome（4 个 Shader）
```

---

## 十、踩坑日志

> 全部为实际调试过程中定位并修复的问题，按「症状 → 根因 → 修复」记录。

### 10.1 URP 迁移与材质

| # | 症状 | 根因 | 修复 |
|---|---|---|---|
| 1 | 整个场景一片荧光粉（品红） | 材质用了内置 `Standard` / `Default-Material`，URP 下无对应 pass；另有两处材质的 `.mat` 资产被删，槽解析成 null | `MaterialProbe` 体检 + 一键替换为 URP 兜底材质；补回丢失资产 |
| 2 | 画面大面积发白、高光溢出 | ① 相机 `m_RenderPostProcessing = 0`，后处理 Volume 完全没生效；② 22 个材质 `_Smoothness` 被官方升级器错写成 1.0 变镜面 | 开相机后处理；按源 shader 类型选槽位（Standard→`_MetallicGlossMap`，Specular→`_SpecGlossMap`）重算，修正 13 个 |
| 3 | 窗帘 / 植物 / 海报变成实心白块 | 6 个 Cutout 材质转换后 `_AlphaClip = 0`、`_ALPHATEST_ON` 丢失 | 恢复 `_AlphaClip=1` + 关键字 + queue 2450 |
| 4 | 树冠渲染成一片实心多边形 | 叶子材质源 `_Cutoff=0.806` 但转换后 `_AlphaClip=0` | 2 个叶子材质 + 5 个预制体内嵌材质恢复裁剪 |
| 5 | 部分物体明显偏亮 | 4 个 Fade 材质 `_SrcBlend` 被设成 `One`（预乘），但颜色没预乘 | `_SrcBlend` 1→5（`SrcAlpha`） |
| 6 | 场景光照平坦、边缘锯齿 | 附加光是 PerVertex（69 盏灯全按顶点插值）、MSAA = 1 | 改 PerPixel（每物体 4 盏）+ 附加光阴影 + MSAA 4x + 主光阴影 4096/4 级联/150m |
| 7 | 后处理 Volume 建好了但毫无效果 | 第一次创建的 `VolumeProfile` 里 4 个组件引用全是 `fileID: 0`（没存为子资产） | 重建 Profile，4 个真实组件挂到场景 `Global Volume (URP)` |
| 8 | 玻璃 / 窗户偏白 | 42 个反射探针 Baked 但 `m_ReflectionProbeData` 为空（源项目遗留） | 玻璃改用固定 alpha 混合；探针问题记录为已知限制 |

### 10.2 天空与天气

| # | 症状 | 根因 | 修复 |
|---|---|---|---|
| 9 | **切昼夜时天空从来没变过** | 旧代码"场景已有天空盒就直接 return"。实测场景用的是 `Skybox/6 Sided`，属性只有 `_RightTex/_UpTex/_Exposure/_Rotation/_Tint` —— 代码里那一串 `HasProperty("_SkyTint")` 判断**全部为 false** | 改为运行期新建 `Skybox/Procedural` 材质，`EnvSample` 每个字段都能对上真实参数 |
| 10 | 切天气后磁盘上的 `.mat` 被改脏 | 直接改 `RenderSettings.skybox` 引用的场景资产 | `new Material(shader)` 得到内存副本，只在运行期变 |
| 11 | 天气切换时明显掉帧 | `DynamicGI.UpdateEnvironment()` 每帧调用，每次都要重算天空盒到 cubemap 的卷积 | 节流到 0.2s 一次，过渡结束补一次，`giDirty` 标记控制 |
| 12 | 数字孪生（200m）视角下整个校园糊成一片白 | 雾密度是按地面 2m 调的，200m 高空同一密度直接过曝 | 雾密度按相机高度线性衰减：5m 全量 → 185m 的 30% |
| 13 | 第一人称下雨雪时粒子还挂在 200m 高空 | `Camera.main` 只按 tag 查找、**不检查 enabled**。第一人称时孪生相机 `enabled=false` 但 tag 还在，`Camera.main` 返回的是那台被禁用的 | 改用 `Camera.allCameras`（只含真正启用的），再排除 `targetTexture != null` 的小地图相机 |
| 14 | **雪发虚，只积在砖缝里** | 脚下铺装网格（`SchoolBuilding.FBX`）高出地形且**无碰撞体**，向下射线只采到地形。雪面停在 `地形+0.10` 低于可见砖面，`ZTest LEqual` 让砖面戳穿雪面 | 抬升量 0.10→**0.30**（近场）/ 0.15→**0.35**（远场），盖过铺装凸起（0~0.25m） |
| 15 | 地平线方向能看到裸地形缺口 | 近场雪盘只有 90m，超出范围没有雪 | 加远场 260m 低精度兜底雪盘（`renderQueue` 2995，低于近场 2996） |
| 16 | 灰雨天里地面反射蓝天 | 积水层的天空色没跟着天气变，用的是固定值 | `EnvironmentState` 每次 `ApplyAll` 把 `zenith`/`horizon` 两个颜色下发给 `WetGround` |
| 17 | 室内看到下雨 / 地面积水 | 轻量方案不做粒子世界碰撞（几千粒子每帧查询代价远大于收益） | 用「室内外判定 + 淡出」近似：进室内雪量淡出、积水关闭 |

### 10.3 交互系统

| # | 症状 | 根因 | 修复 |
|---|---|---|---|
| 18 | **进不去室内（按 F 变成坐下）** | 座椅 `Priority = 0` 与门同级，靠距离比最近。教室/门口旁成排椅子离玩家更近，把 F 键抢走 | 优先级分层：门 0 > 座椅 1 > 灯 2 > 其他 5；已坐座椅降为 −1 |
| 19 | **教室里永远只能关灯，坐不下去** | 灯 `Priority = 1` 排在座椅前，且灯半径 5.5m、室内密布 | 灯改 `Priority = 2` |
| 20 | 侧后方的椅子把正前方的门提示挤掉 | 所有交互物共用 0.2 的朝向点积阈值，座椅间距不到 1m | `InteractableBase.MinFacingDot` 虚属性：门 0.2 宽松、座椅 **0.6** 严格 |
| 21 | **坐下穿模（摄像机嵌进椅背）** | 玩家面朝椅子走过去，`Sit()` 不改朝向，坐下后正对椅背 | 落座朝向取「背离椅子中心」= 面朝走过来的方向；落点再前移 **0.12m** |
| 22 | **坐下瞬间又弹起来** | 玩家按着 W 走到椅子前按 F，下一帧 W 还按着 → 边沿判定缺失 | `sitKeysReleased` 标志：必须先松开所有移动键，之后才允许用移动键/空格起身 |
| 23 | 坐下后转身 180°，F 变成开门 / 关灯 | 座椅锚点在椅子中心，落座后转身导致朝向过滤把座椅筛掉 | `Anchor` 在坐着时返回**玩家自身位置**（距离≈0，且永不被朝向过滤） |
| 24 | **卡在门框里进不去** | 胶囊有效直径 = 0.8 + 0.03×2 = **0.86m**，门洞净宽约 **0.85m**（门开到 95° 还要再占 7cm，实际可通行 0.78m） | `cc.radius` 0.4→**0.30**（有效直径 0.66m，余量 12cm）；`height` 1.8→1.75 |
| 25 | 看得见门洞，走过去撞隐形墙 | 大体积 `MeshCollider` 误勾 `convex`，凸包把门洞整个填平 | `FixConvexWalls()`：运行时把「体积 ≥ 8m³ 且无动态刚体」的凸包改非凸（小长椅木座保持原样） |
| 26 | 有些门飞到错误位置 / 朝向 | 门预制体是「门扇根 + 子门扇」结构，父子名字都含 `door`，两个都挂组件就各转一次 | 候选按层级深度排序（父先子后）+ `HasDoorAncestor` 去重 |
| 27 | 「大部分门没有交互」 | 尺寸体检过严（限制高 1~4.5m、宽 0.4~5.5m、厚度不能超宽 0.9 倍），大门/卷帘门/双开门全被拒 | 放宽到只挡明显不是门的东西（高 <0.6 或 >12m、宽 <0.25 或 >14m） |
| 28 | 合页点算到门外面 | 用世界 AABB 反算局部包围盒 —— 场景根节点 `Exterior` 绕 (1,1,1) 转了 −120°，世界 AABB 被放大成大斜块 | 改用 `Renderer.localBounds` 逐级变换 |
| 29 | 走到门前却不弹按钮 | 测距锚点用 `transform.position`（轴心），而门的轴心做在合页端（`Door07a` 轴心偏到 (20.36,−3.53,−14.85)） | `Anchor` 改用门扇包围盒中心 |

### 10.4 UI 与显示

| # | 症状 | 根因 | 修复 |
|---|---|---|---|
| 30 | **所有 UI 界面都很模糊** | Game 视图 Free Aspect 约 1253×616，HUD 按 1920×1080 设计 → `scaleFactor` 压到 0.65，`fontSize 13` 的字只剩 8px 高 | ① 三 Canvas 开 `pixelPerfect`；② CanvasScaler 锁 1920×1080 + `referencePixelsPerUnit 100`；③ 小地图 RT 512→1024；④ `GameViewResolution.cs` 自动切分辨率 + `MaximizeOnPlay` |
| 31 | 改完 `GameViewResolution.cs` 完全没反应 | **Play 模式中改代码不会重新编译**，脚本从未执行 | 必须 Stop Play → 等编译 → 重新 Play；日志里查 `[CampusUI] Game 视图已设为 1920×1080` 确认 |
| 32 | 小地图渲染成一片白 | 构建顺序错：`BuildMinimap` 在 `ComputeMinimapBounds` / `BuildMinimapCamera` 之前执行，`rawMinimap.texture` 拿到 null | 强制顺序：先算范围 → 建 RT 与叠加纹理 → 再构建 UI |
| 33 | 设置面板和「景点导览」叠在一起 | 设置原本贴在右上角，与右侧景点列表同区域 | 设置改为居中弹窗（340×316） |
| 34 | 鼠标点 HUD 按钮时相机跟着乱转 | `Look()` 没判断是否悬停在 UI 上 | 加 `EventSystem.current.IsPointerOverGameObject()` 提前返回 |
| 35 | 第一人称时 HUD 按钮点不动 | 旧版会 `Cursor.lockState = Locked` | 全程保持 `None` + `visible = true`；视角用 `Input.GetAxis` 读鼠标位移，与锁定状态无关 |
| 36 | 切回数字孪生后 KPI 再也不刷新 | 用 `SetActive(false)` 隐藏 UI 会杀掉刷新协程 | `SetUIVisible` 只操作 Canvas 的 `enabled` |

### 10.5 编译与工程

| # | 症状 | 根因 | 修复 |
|---|---|---|---|
| 37 | `CS0108` 警告：`audio` 隐藏了已废弃的 `Component.audio` | 字段名撞了 Unity 废弃 API | 改名 `stepAudio` |
| 38 | `CS0108`：`FindObjectOfType` 与 `Object.FindObjectOfType` 冲突 | 静态上下文里调实例方法 | 显式写 `Object.FindObjectOfType<T>()` |
| 39 | 出生点落在草地 / 悬空 | 硬编码坐标 | `SpawnPointFinder` 读地形 splatmap 找硬质路面，再向下射线贴地 |
| 40 | 出生时朝向被瞬间重置成 +Z | `yaw` 字段初值为 0，第一帧 `Look()` 用 yaw 覆盖 rotation，出生朝向被抹掉 | `Awake` 里 `yaw = transform.eulerAngles.y` |
| 41 | 主菜单打开时玩家能动 / 关闭后动不了 | `fpCtrl.enabled` 与菜单状态不同步 | 统一由 `ShowMainMenu(bool)` 门控 |

### 10.6 性能

| # | 症状 | 根因 | 修复 |
|---|---|---|---|
| 42 | 走路时周期性卡顿 | 交互物扫描放在 `Update` 里 | 一次性 `EnsureScanned()`（进漫游时完成），之后 10Hz 只做遍历查询 |
| 43 | 几百把椅子各自 `FindObjectOfType` 查控制器 | `SeatInteractable` 每帧每实例查一次 | 静态缓存 `s_ctrl`，全场景只查一次 |
| 44 | 小地图每帧全图重绘 | 叠加层 512² 逐像素画三角/圆点 | 20Hz（0.05s）刷新，肉眼无感 |
| 45 | 每步 `Debug.Log` 刷屏 600+ 条 | 脚步声无音效资源时打日志 | 静默处理 |
| 46 | 树根雪堆几百个 DrawCall | 每棵树一个独立 mesh | 全部合并进一个 mesh，仅 1 个 DrawCall |

---

## 附录：已知限制

1. **雨雪穿屋顶** —— 粒子不做世界碰撞（几千粒子每帧碰撞查询代价远大于收益），室内用淡出近似。
2. **反射探针无烘焙数据** —— 42 个探针为源项目遗留问题，玻璃/窗户只反射天空盒。
3. **数字孪生数据为演示数据** —— `MockDataService` 用固定种子生成，保证每次运行一致；接真实后端实现 `IDataService` 即可。
4. **强依赖 TirGames 场景的地形图层命名** —— `SpawnPointFinder` 的 splatmap 判定按图层顺序，换地形需重调。
5. **仅支持 Legacy Input Manager** —— 未接 Input System 包。

---

*文档基于 `D:\unityxiangmu\sx5` 2026-11-04 快照生成。代码行数为实际统计（含注释与空行）。*
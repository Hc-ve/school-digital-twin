// 室内补光层 · 运行时自举（不碰别人，只补室内）
// 归属：打光（光照/氛围）　交付：P35（老师反馈「室内有点暗，需要调亮室内灯光，但不能过曝」）
//
// 用法（验收/回滚两行就够）：
//   默认全自动：进 Play 即生效，不挂场景、不改场景、不改资产、不动 TwinDevicePresenter。
//   一键回滚：  IndoorFillLight.SetEnabled(false);   // Console 会打一条，画面回到补光前
//   自检摘要：  Debug.Log(IndoorFillLight.Describe());
//
// ── 为什么会有这个文件（根因一句话）──────────────────────────────────
// 2026-11-13 主理人实验 A 实测确诊：夜晚档「室内近黑」的主因是
//   Skybox 环境光链路被时间天气系统连乘压没（_SkyTint×0.20 × _Exposure×0.34
//   × ambientIntensity 0.18 × postExposure −1.45~−1.70EV），
// 而 68/8 盏 Area 面光只支持烘焙、D13 弃用烘焙数据后实时贡献为零，
// 灯具自带的小点光源（TwinDevicePresenter.BuildGlows，range≈3m）只够照亮灯管脚下。
// 修法不是去动天气系统的倍率（那些倍率一动室外跟着亮，撞「室外不能过曝」红线），
// 而是在室内补一层**由灯具语义驱动**的实时点光源：白天几乎为零、夜晚补足。
// 诊断证据链与全部推导见 `Docs/P35_室内补光_方案与标定_打光.md`。
//
// ── 天气与灯具亮度的关系（主理人 2026-11-13 实测确认的语义）──────────
// 「同一时间，天气不一样，环境光强度也不一样，但灯具亮度是一样的」——
// 灯具本体（灯管自发光 / 灯具档强度）确实不随天气变，这是**正确语义**，本层照旧：
// 补光点归属"灯具档"，强度基准只随昼夜档与灯具开关（Power+回路）变化。
//
// ── 【P40 / 2026-11-16 修订】白天档按天气补光 ──────────────────────────
// 主理人实测推翻了上面这条的**白天分支**：「下雨天灯光有点暗，虽然是下雨天，天阴，
// 但室内灯都开着的，不应该这么暗」。活体读数（weather=Rain, timeOfDay=Day）：
//   sun 0.26（晴天 1.25）／ambientIntensity 0.60（晴天 0.85）／exposure 0.70（晴天 1.25）
//   —— 三条链路同时被压，进窗照度只剩约 1/5；
//   而本层 DayScaleDay(0.05) < EnableGate(0.08) ⇒ 补光**整体 disabled**（_activeCount=0）。
// 结果：灯管亮着，但对环境照度零贡献 ⇒ 观感「整片发灰」。
// 修法：白天档不再是一个常数，改为「按天气分级」——
//   晴天 0.05（不变，已验收基线零改动）；多云 0.30；阴/雾 0.55；雨 0.62；雪 0.60；雷雨 0.72。
// **不改** EnvironmentState 的任何倍率（动它室外跟着亮，撞过曝红线），只动本层自己的灯。
//
// ── 版本状态（v11 回退后 = v4 内容 + P37 底光层）─────────────────────
// 本文件当前 = v4 轮结束时的功能集：灯位补光（每根灯管一个光点）+ 接管走廊/
// 大房间旧光点 + 走廊应急底光 + 角色眼睛锚点 + 同层判定 + 淡入淡出。
// P37（2026-11-14）新增：底光层 BaseLights（方案甲+乙）——D13 弃烘焙后夜晚室内
// 零反弹、Spot 锥外纯黑的「均匀垫底」层；只增层不改层，A/B 开关 = F2。
// v5~v10 的增项（地面压暗、F8/F9 机位、F10 锁档、窗玻璃压暗、调试键）已按
// 主理人指令整体撤销，完整实现记录仍保留在 P35 §13 各轮回执中，可按需恢复。

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using SchoolTwin.Core;
using SchoolTwin.Player;
using SchoolTwin.Twin;
using SchoolTwin.Weather;

// ── 为什么是全局命名空间 ─────────────────────────────────────────────
// 跟随同目录 TwinLightingPreset.cs 的现状（它就是全局命名空间）。
// 本文件要同时引用 SchoolTwin.Twin（锚点/回路）、SchoolTwin.Core（TwinValue）、
// SchoolTwin.Weather（EnvironmentState），放哪个命名空间里都不省 using，
// 那就不新增命名空间，保持 Render 目录内两个文件口径一致。
//
// ── 文件结构（为什么拆成两个类）──────────────────────────────────────
// IndoorFillLight    = 对外的静态门面：总开关、挂载、自检摘要（别处只跟它说话）。
// IndoorFillRunner   = 运行主体（MonoBehaviour）：建灯 + 每帧应用，全部调参常量也在这里，
//                      因为常量的所有使用点都在这个类里 —— 第一版把常量放门面类，
//                      编译器报了 33 条 CS0103（跨类私有不可见），才搬到这里。
public static class IndoorFillLight
{
    // =====================================================================
    // 状态与生命周期
    // =====================================================================

    /// <summary>总开关。false = 全部补光点 disabled + 停止应用，画面回到补光前。</summary>
    public static bool Enabled { get; private set; }

    /// <summary>P37 底光层 A/B 开关（F2 切换）。false = 底光全部 disabled，
    /// 补光层与灯管表现完全不受影响——验收「底光有没有用」就靠它与默认态对照。</summary>
    public static bool BaseLightsEnabled { get; private set; }

    /// <summary>P37e 逐像素光源预算（运行时抬升，退出 Play 自动还原）。内置管线每个渲染器
    /// 只有 QualitySettings.pixelLightCount（原值 20）个逐像素名额：走廊的合并大网格上有
    /// 20 补光 + 6 垫底 + 6 反弹 = 32 个候选，超出的全部降级顶点光——顶点插值在稀疏大面上
    /// "插值不出亮度"（P17 同款机制），这就是反弹灯/垫底灯在走廊天花板两轮都看不见的根因。
    /// 抬到 32 后所有灯在所有面上走逐像素路径；代价 = 大面上的多灯逐像素开销，帧率明显
    /// 下降就把 32 调回 20（一行）。</summary>
    public const int PixelLightCountTarget = 32;
    private static int _originalPixelLightCount = -1;

    private static IndoorFillRunner _runner;
    private static SceneWatcher _watcher;

    /// <summary>
    /// 静态状态复位。G10 教训：关掉 Domain Reload 时静态字段跨 Play 存活，
    /// 不清的话上一局的 runner 引用会让本局误判"已挂载"而永远建不出灯。
    /// </summary>
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        _runner = null;
        _watcher = null;
        Enabled = true;
        BaseLightsEnabled = true;
        _originalPixelLightCount = -1;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        TryMount();
    }

    /// <summary>验收/应急用的总开关。false 后随时可再 true，不需要重进 Play。</summary>
    public static void SetEnabled(bool on)
    {
        Enabled = on;
        if (_runner != null)
        {
            if (!on) _runner.RestoreSuppressedGlows();   // 回滚 = 归还表现层的旧光点
            _runner.RefreshNow();
        }
        Debug.Log("[IndoorFill] 补光层" + (on ? "已启用" : "已停用（一键回滚）") +
                  "。改动只存在于运行时，退出 Play 自动消失。");
    }

    /// <summary>
    /// P37 底光层 A/B 开关。与 SetEnabled 同款：随时可切、立即刷一帧、运行时态不落盘。
    /// 只关底光，不动补光层与灯管——同机位开/关各看一眼就是主理人的验收判据。
    /// </summary>
    public static void SetBaseLightsEnabled(bool on)
    {
        BaseLightsEnabled = on;
        if (_runner != null) _runner.RefreshNow();
        Debug.Log("[IndoorFill] 底光层" + (on ? "已启用" : "已停用（A/B 对比）") +
                  "。现在" + (on ? "Spot 锥外由底光垫底" : "只剩 Spot 直射，锥外回到纯黑") +
                  "。改动只存在于运行时，退出 Play 自动消失。");
    }

    /// <summary>
    /// 挂载入口。幂等：已挂就返回；场景不对（没有 TwinRuntime）就不挂。
    /// 【为什么用 TwinRuntime 当判据】与 TwinWeatherBootstrap/TwinPerfBootstrap 同口径：
    /// 按 guid 查过它只在 SchoolSceneDay 里有 —— 夜晚场景主理人已确认"灯光正常"，
    /// 本层本来就不该出现在那里，这个判据天然把我们挡在外面。
    /// </summary>
    internal static bool TryMount()
    {
        if (_runner != null) return true;
        if (TwinRuntime.Instance == null) return false;

        GameObject go = new GameObject("[IndoorFillLight]");
        _runner = go.AddComponent<IndoorFillRunner>();

        // P37e：逐像素名额抬升（幂等）。原值记下来，退出 Play 时原样还回去——运行时态不落盘。
        if (_originalPixelLightCount < 0)
        {
            _originalPixelLightCount = QualitySettings.pixelLightCount;
            QualitySettings.pixelLightCount = PixelLightCountTarget;
            Debug.Log("[IndoorFill] 逐像素光源预算 " + _originalPixelLightCount + " → " + PixelLightCountTarget +
                      "（垫底/反弹灯需要逐像素名额；顶点光在合并大面上不可见）");
        }

        // 场景监视器：RuntimeInitializeOnLoadMethod(AfterSceneLoad) 只在**第一次**进场景时跑，
        // 之后 SceneManager.LoadScene 不会再触发它。常驻一个只听 sceneLoaded 事件的空壳
        //（无每帧开销），万一将来真的切场景，根节点随旧场景销毁后由这里重建。
        if (_watcher == null)
        {
            GameObject w = new GameObject("[IndoorFillLight.Watcher]");
            UnityEngine.Object.DontDestroyOnLoad(w);
            _watcher = w.AddComponent<SceneWatcher>();
        }
        return true;
    }

    internal static void NotifyRunnerDestroyed()
    {
        _runner = null;
        if (_originalPixelLightCount >= 0)
        {
            QualitySettings.pixelLightCount = _originalPixelLightCount;   // 退出 Play 还原预算，不留运行时态
            _originalPixelLightCount = -1;
        }
    }

    /// <summary>自检摘要（验收时往 Console 贴一条就能看清建了什么、启用了什么）。</summary>
    public static string Describe()
    {
        if (_runner == null) return "[IndoorFill] 未挂载（当前场景没有 TwinRuntime）";
        return _runner.Describe();
    }

    // =====================================================================
    // 场景监视器：只做一件事 —— 场景加载后尝试补挂（幂等，代价一次空引用检查）
    // =====================================================================

    private sealed class SceneWatcher : MonoBehaviour
    {
        private void Awake()
        {
            // 与根节点不同：监视器必须跨场景活着，否则它监听的"场景切换"它自己先死了
            UnityEngine.Object.DontDestroyOnLoad(gameObject);
        }

        private void OnEnable() { SceneManager.sceneLoaded += OnSceneLoaded; }
        private void OnDisable() { SceneManager.sceneLoaded -= OnSceneLoaded; }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            TryMount();   // 内部幂等：已挂/场景不对都直接返回
        }
    }
}

// =====================================================================
// 运行主体：建灯 + 每帧应用。**全部调参常量收在这里**（使用点都在本类内）。
// =====================================================================
internal sealed class IndoorFillRunner : MonoBehaviour
{
    // =====================================================================
    // 调参起点（D3 标定表的一部分；**全部是起点不是定稿**，必须 play 实测微调）
    // 每个数字的出处与"调大调小会怎样"写在 Docs/P35 §D3，这里只留一句话注记。
    // =====================================================================

    /// <summary>层高（米）。出处：Corridor 网格包围盒 y 尺寸 3.56（场景实测 52.36×3.56×24.05），
    /// 1F 灯管 y=3.34 / 2F 灯管 y=6.90（TwinDevicePresenter.CollectLampGroup 注释实测）。</summary>
    private const float StoreyHeight = 3.56f;

    /// <summary>补光点离地高度（米）。2.2 = 灯管(3.34)与人眼(1.6)之间偏下：
    /// 太高会贴近灯管、把天花板烧出第二圈白斑；太低会让人眼前出现过近的亮核。</summary>
    private const float FillHeightAboveFloor = 2.2f;

    /// <summary>走廊候选点的间距（米）。走廊灯管实测间距 3~4.2m（TwinRoomBounds 注释），
    /// 补光点取其约 2 倍 —— 它负责"灯管之间的面"，不负责"灯管脚下的点"，不需要一样密。</summary>
    private const float CorridorStep = 7f;

    private const float CorridorRange = 9.5f;      // 兜底网格：覆盖走廊断面的直径（宽 3m 高 3.5m）
    private const float CorridorIntensity = 1.8f;  // 兜底网格单点强度起点（v3 上调）

    private const float RoomMinRange = 7f;         // 教室量级（≈85m²）的覆盖半径下限
    private const float RoomMaxRange = 11f;        // 食堂/礼堂量级（≈360m²）的上限
    private const float RoomIntensity = 1.6f;      // 兜底网格房间单点强度起点（v3 上调）
    private const float HallIntensity = 1.6f;      // 大房间（>200m²）再抬一档，因为单点要喂更大面积

    /// <summary>房间兜底补光点数量 = clamp(面积/55, 1, 3)。55m² ≈ 一间教室一半的照域，
    /// 让 85m² 教室出 2 个点、360m² 食堂封顶 3 个点。</summary>
    private const float RoomAreaPerFill = 55f;

    // —— 灯位补光（首选路径：补光点放在真实灯管正下方，有几根管就有几片光）——
    /// <summary>补光点比灯管低 1.14m：灯管 3.34 → 补光 2.2（与 FillHeightAboveFloor 同一口径）。
    /// 不放在灯管中心的原因：全向点光源离天花板只有 0.16m 会把管顶烧出白斑，
    /// 真实荧光灯往上几乎不发光（灯罩兜着）。</summary>
    private const float TubeFillDrop = 1.14f;

    /// <summary>P39 视角突变根治（三实测证据见 Docs/P39 文档）：巨型合并网格的逐渲染器
    /// 逐像素光分配随相机重排 = 画面亮度跳变；候选光数必须压到 pixelLightCount(48) 以下
    /// 分配才静态化。做法 = 收紧 range（跨分区灯从对方渲染器候选名单里剔除）+ 裁数量。
    /// P39 起点值（play 迭代校准）：
    ///   走廊 9→7（管距 3.5~4.2m 光池仍衔接）、强度 1.7→2.2（治墙面偏暗，实测 0.2→目标 0.3+）；
    ///   房间 8→4.5（教室深 5.5m，中心线补光到墙 2.7m 仍覆盖）、强度 1.7→1.5（治泛黄过曝，
    ///   原始辐照 ≈2.0→1.4，配合 Neutral tonemapper 不再硬截断）；
    ///   礼堂 9→7、1.5→1.6（昏暗端补一点，天花板白斑由反弹灯删除治本）。</summary>
    // ── P41c（2026-11-16）：礼堂 / 餐厅夜档整体再降一档 ────────────────────
    // 【老师原话】「餐厅和礼堂的夜晚开灯后的灯光还是太亮了，能不能再往下降点」
    // 【为什么不能直接改 HallTubeIntensity / BaseIntensityHall / WashIntensityBig】
    //   这三个常量按 `bigRoom = !IsCorridor && 面积 > 200㎡` 被**三个分区共用**：
    //     ActZal 327㎡（礼堂）、Kitchen 364㎡（餐厅）、SportHall 735㎡（体育馆）
    //   改常量 → 体育馆跟着降暗。而体育馆本底仅 0.293，P39f 注释已明确记下
    //   「再往下会压到 0.27 一线，接近过暗」；且体育馆远墙 P0 黑影是硬红线
    //   （BaseRangeHall 注释：range 太小导致远端墙辐照 0.000，占画面近半成「巨大黑影」）。
    //   ⇒ 老师点名的只有礼堂 + 餐厅两个空间，就必须**按分区精确降**，不能动共用常量。
    // 【为什么用独立倍率而不是改三个常量】改动面最小：只乘本分区的强度出口，
    //   布点/盏数/range/同层判定/P39h 洗顶灯自适应全部不动，也不新增任何灯（FPS 红线）。
    // 【取值】P39f 两轮各只降 6%，老师第三次仍说"太亮" ⇒ 本轮一步到位降 20%。
    private static readonly string[] NightDimZones = { "ActZal", "Kitchen" };
    private const float NightDimFactor = 0.80f;        // P41c：−20%（fill 与底光/洗顶同乘）

    private const float CorridorTubeRange = 9.0f;      // P39d：7.5→9（用户循环验收：走廊远场衰减过快、深处黑——
                                                       // 管距 3.5~4.2m 下 range 7.5 的光池只盖 ~5m 前方）
    private const float CorridorTubeIntensity = 2.8f;  // P39e（迭代5·配光再平衡）：3.4→2.8。用户给出豆包真实参照
                                                       // （web7_doubao_target）实测：真实走廊天花板 0.558 是全场
                                                       // 最亮面、上墙 0.377、地板 0.558；我们 floor 0.509 ✓ 但
                                                       // ceiling 0.319 / 上墙 0.215 —— 光的重心倒挂在地板上。
                                                       // fill 降 20% 让出地板，光挪给天花板（Wash 同步升 1.5）。
    private const float RoomTubeRange = 4.5f;          // P39：8→4.5，跨分区候选剔除 + 治教室过曝
    private const float RoomTubeIntensity = 1.5f;      // P39：1.7→1.5
    private const float HallTubeRange = 12.0f;         // P39d：7→12（用户循环验收：体育馆地板 0.10 全场最黑——
                                                       // fill 挂 6.3m 高、range 7 锥体够不到 9m 下的地板，
                                                       // 地板只剩 2 盏 0.9 底光；大厅 fill 射程必须 ≥ 灯到地板距离）
    private const float HallTubeIntensity = 1.98f;     // P39d：1.6→2.2→2.5（迭代3：底光回落 1.3 后由 fill 的
                                                       // 均匀覆盖补地板，消除底光在光泽地板上的热点亮斑）
                                                       // P39f：2.5→2.1→1.98。用户 11-15 两轮反馈：
                                                       // ①「有点过曝，亮度稍微往下调一点」→ 2.1
                                                       // ②「礼堂和餐厅的亮度再稍微调低一点点」→ 1.98（再 −5.7%）
                                                       // 与 BaseIntensityHall 同批联动，取证见其下方注释块。
                                                       // 取证与选值见 BaseIntensityHall 下方的 P39f 注释块。
    /// <summary>P39e：8→4。P39e 把走廊 Base 底光从 Auto 提为 ForcePixel（治墙上部死暗 +
    /// 随灯集合换班的深浅跳变），这会给走廊多 +4 个逐像素 pass。MCP 同机位 cam.Render()
    /// 实测 52.27→57.90 ms（+5.63ms），违反本任务「FPS 不恶化」红线。
    /// 减 4 盏走廊 fill 正好把 ForcePixel 净额归零（77→73，回到修前水平）。
    /// 为什么可以减：8 盏 fill 是 150° 朝下宽锥、range 9、管距 3.5~4.2m，等距取样 4 盏
    /// 后间距 ≈17m 仍被 range 9 的双侧光池接住（等距取样保留首尾覆盖，见 BuildTubeFills）。
    /// 活体 A/B 实测（同机位、同灯集合）：dark 0.2492→0.2495（修复效果不丢）、
    /// ceil 0.3251→0.3252（顶面不变）、full 0.3090→0.3079（全幅 −0.0011）。
    /// 对照组「减 4 盏 Wash」会让天花板 0.3251→0.2305（−29%），故只能减 fill 不能减 wash。</summary>
    private const int MaxTubeFillsCorridor = 4;        // P39：20→8；P39e：8→4（抵消 Base 提级，FPS 净额归零）
                                                       // 候选 4 fill + 4 base + 3 wash + 房间渗入 ≈ 22 < 48；
                                                       // 强度由 CorridorTubeIntensity 2.8 保总量
    private const int MaxTubeFillsRoom = 8;            // 教室 6 管；食堂约 20 管封顶 8（预算保护）

    /// <summary>走廊管位扫描比登记矩形外扩多少米：楼梯间/门厅的灯贴着走廊端头、
    /// 且没有登记分区（TwinRoomBounds 无此行），不扩扫楼梯间永远没光。</summary>
    private const float CorridorScanExpand = 10f;

    /// <summary>接管旧光点（TwinLampGlow：表现层给每根灯管建的 range≈3m 点光源）。
    /// P35 起接管走廊/大房间（45 候选抢 20 逐像素名额 → 抽签式明暗斑块）；P37d 扩大为**全楼**：
    /// 教室保留两套光叠加被主理人判定「与走廊是两种截然不同的灯光效果」——墙上的明暗斑块
    /// 来自 glow 的 3m 范围边界（不是投影，本层不开阴影），「过亮」来自两套光叠乘；
    /// 且 glow 的"天花板亮圈"不符合真实电棒观感。统一后所有房间同一套配方：
    /// 补光 Spot + 垫底灯 + 天花板反弹灯。改 false 即新旧并存；
    /// SetEnabled(false) 会把关掉的 glow 全部恢复。</summary>
    private const bool TakeOverGlows = true;

    /// <summary>走廊应急底光系数：走廊灯断电/关灯时，补光不归零而是保留这一档。
    /// 语义见 P35 §Q3（应急疏散照明 + 窗外环境反射）；改成 0 即"断电走廊全黑"的严格语义。</summary>
    private const float EmergencyFloorFactor = 0.22f;

    /// <summary>昼夜倍率。白天 0.05 **低于启用闸 0.08** ⇒ 白天补光点整体 disabled，
    /// 对已验收的白天画面零改动（这是刻意设计，见 Apply）。
    /// 黄昏 1.0：真实灯具不随时刻变亮度（开灯就是全亮，白天看不见只是被太阳盖住）。</summary>
    private const float DayScaleDay = 0.05f;
    private const float DayScaleDusk = 1.0f;
    private const float DayScaleNight = 1.0f;

    // ── P40（2026-11-16）：白天阴雨天室内补光 ──────────────────────────────
    // 【解决的病】主理人实测：白天切雨天，灯管全亮着但走廊整片发灰。
    // 活体读数：weather=Rain 时 sun=0.26（晴天 1.25，只剩 21%）、
    // RenderSettings.ambientIntensity=0.60（晴天 0.85）、天空盒 exposure=0.70（晴天 1.25）。
    // 三条链路同时被压 ⇒ 室内进窗照度只剩晴天的约 1/5；
    // 而本层白天档 DayScaleDay(0.05) < EnableGate(0.08) ⇒ 补光**整体 disabled**（_activeCount=0），
    // 灯管自发光还在（TwinDevicePresenter 管），却对环境零贡献 ⇒ 「灯开着但发暗」。
    // 【为什么晴天必须保持 0.05】晴天的室内亮度是已验收基线，太阳 1.25 从窗户灌进来足够，
    // 一动就是回归。所以这里只给**非晴天**开口子，晴天仍走 DayScaleDay。
    // 【取值的依据】雨天的室外光约为晴天的 1/5，缺的那 4/5 由补光兜。
    // 补光在全强度(1.0)下的室内观感约等于黄昏档（已验收），故按缺光程度分级：
    //   多云 0.22、阴/雾 0.38、雨 0.42、雪 0.40、雷雨 0.50。
    // 全部高于 EnableGate(0.08) ⇒ 能真正点亮；又都远低于 1.0 ⇒ 不会把白天拍成夜里。
    //
    // 【为什么首版 0.62 被打回】实测 0.62 把天花板洗到 0.506（晴天 0.491，已超标），
    // 但横梁梁底没有光 ⇒ 被亮天花板一衬，变成近乎纯黑的剪影（梁区 std 从 0.082 涨到 0.175）。
    // 照度够了、明暗结构垮了。降到 0.42 让"补光只补缺口"，不跟太阳比亮度。
    private const float DayScaleOvercast = 0.44f;
    private const float DayScaleRain = 0.50f;
    private const float DayScaleStorm = 0.58f;
    private const float DayScaleSnow = 0.46f;
    private const float DayScaleFog = 0.44f;
    /// <summary>多云：太阳 0.85（晴天 68%）、环境光 0.80，室内略欠但基本够看。
    /// 0.22 只做轻微垫底，避免"多云也当阴天补"把晴天基线拖糊。</summary>
    private const float DayScaleCloudy = 0.22f;

    /// <summary>昼夜倍率走完全程的秒数。天气系统过渡 2.4s（EnvironmentState.transitionDuration），
    /// 这里取 1.8s 略快于它，让"灯先到位、天色后到位"，不会出现天已黑灯还没亮的空窗。</summary>
    private const float DayLerpSeconds = 1.8f;

    /// <summary>昼夜倍率低于此值的分区一律 disabled。0.08 卡在 Day(0.05) 与 Dusk(1.0) 之间，
    /// 作用是让白天档的逐像素灯成本也归零（不止是强度归零）。</summary>
    private const float EnableGate = 0.08f;

    /// <summary>灯具开关的渐变速率（每秒）。8/s ⇒ 0↔1 约 0.12s，肉眼等于"啪一下"，
    /// 与 TwinDevicePresenter.ApplyLamp 的瞬时开关观感一致；再快会在轮询竞态下闪。</summary>
    private const float LampSnapPerSecond = 8f;

    /// <summary>状态轮询周期（秒）。灯是开关量，与 TwinDevicePresenter._pollInterval=0.1 同量级，
    /// 取 0.2 是因为本层还要顺带做同层判定，没必要翻倍频率。</summary>
    private const float PollInterval = 0.2f;

    /// <summary>同一 storey 判定的层差容忍（锚点与**分区地面**比，P39b 起不再与灯的安装高度比）。
    /// 1F/2F 教室在 TwinRoomBounds 里 XZ 完全重叠，没有这条会把 1F 的光"透过楼板"漏到 2F（无阴影直射）。
    /// 锚点改成角色眼睛（脚 +1.6m）后，2F 眼高 ≈5.16 与 1F 地面 0 差 5.16，远超本阈值；
    /// 与 2F 地面 3.56 差 1.6，落在同层窗口内（见 EyeAboveFloorMax）——上下层彻底分开。</summary>
    private const float SameStoreyMaxDy = 2f;

    /// <summary>P39b：人眼相对**本层地面**的最大高度（站立 1.6m + 台阶/跳跃余量）。
    /// 同层窗口 = [FloorY - 0.5, FloorY + EyeAboveFloorMax]。旧规则拿"灯的安装高度"比眼高，
    /// 礼堂/体育馆的洗顶灯贴在 7~9m 天花板下，站在厅内眼高 5.2/1.6 与灯差 2.1m > 2 永远判异层
    /// → 大空间顶面永远黑（实测 SportHall/ActZal 双双中招）。改按分区地面后语义恢复：
    /// 人在哪个层的地面附近，哪个层的灯全亮。</summary>
    private const float EyeAboveFloorMax = 2.6f;

    /// <summary>角色眼睛相对脚底的高度（米）。锚点用"眼睛"而不是"脚"，
    /// 否则站在 2F（脚 y=3.56）时与 1F 补光点（y=2.2）只差 1.36，同层判定会漏。</summary>
    private const float EyeHeight = 1.6f;

    /// <summary>启停淡入淡出速率（每秒）。4/s ⇒ 0↔1 约 0.25s：跨楼层等生效变化时灯光软过渡
    /// 而不是瞬跳。再慢会像灯在喘气。</summary>
    private const float EligibilityFadePerSecond = 4f;

    /// <summary>补光颜色。P39：(1, 0.94, 0.82) 暖黄 → (0.94, 0.97, 1.0) 中性冷白。
    /// 依据：教室泛黄吹白（用户 11-14 反馈图4）的黄色调即来自旧 FillColor（饱和叠乘把
    /// 暖色推向全屏黄）；联网实拍（P39_refs/ref7 教室、ref2/ref3 走廊）荧光灯管为
    /// 4000~5000K 冷白。灯管 emission 与本色不再同族由 Neutral tonemapper 统一观感，
    /// 双色割裂由色差收窄（暖黄→冷白只差半档）抵消。</summary>
    private static readonly Color FillColor = new Color(0.94f, 0.97f, 1.0f);

    // ── P37 底光层（方案甲+乙）调参常量 ────────────────────────────────
    // 使命：D13 弃用烘焙后夜晚室内零反弹光，补光 Spot 的锥体外纯黑——在墙上留下
    // 半截黑/斜向硬边，礼堂整体昏暗。底光 = 每分区一层低强度全向光，近似「房间对灯具的反弹」。
    // 【只增层不改层】以下常量只被底光的新方法读取，补光层的常量与逻辑一行未动。
    // 【命名注意】本层叫「底光层（BaseLights）」；文件里 EmergencyFloorFactor 注释中的
    // 「走廊应急底光」是另一回事（灯具关断时补光保留的系数），两者无关。

    /// <summary>底光离地高度。点光在自己高度处对墙面掠射（NdotL→0），这条掠射暗带
    /// 必须压在补光 Spot 的墙面覆盖区之内（150° 锥：走廊墙 0~1.8m / 教室近墙 0~1.5m）
    /// 被其亮度掩住；同时与天花板（距 ≈2.0m）和地面（距 ≈1.5m）保持温和距离——
    /// 抬高烫天花板（P36 红线）、放低烫地板，1.5 是两条红线的中间值。</summary>
    private const float BaseLightHeight = 1.5f;

    /// <summary>底光 range（按分区类型）。只需覆盖本分区断面并与相邻底光衔接：
    /// r 越大远处越亮（atten=1/(1+25d²/r²)），天花板随之整体抬升，红线下要留余量，
    /// 所以不给满房间对角，靠"相邻底光重叠"补覆盖。</summary>
    private const float BaseRangeCorridor = 14f;       // P39e：7→14。7 是「按 fill 同一口径分配静态化」
                                                       // 推出来的，但走廊两侧墙根本不在 fill 锥内（150° 竖直锥、
                                                       // 墙面法线水平、夹角 90°>75°），墙面唯一光源是全向 Base。
                                                       // r=7 时西端墙（x=-42.98，距最近底光 6.7m、到墙顶斜距 >7m）
                                                       // 实测辐照全线 0.000 → 墙面 CV 0.898（一半死黑一半有光）
                                                       // = 用户报的「餐厅门口一大块阴影」，弧线边界是 Wash 锥边切墙的交线。
                                                       // r 扫点（同机位 cam.Render+ReadPixels 同步取样，机位 x=-38 朝 -x）：
                                                       //   r=7 墙上部 0.249 / 墙中部 0.165 / 地面 0.469 / CV 0.898（现状）
                                                       //   r=10  0.350 / 0.268 / 0.473 / 0.428
                                                       //   r=12  0.377 / 0.295 / 0.476 / 0.365
                                                       //   r=14  0.403 / 0.321 / 0.478 / 0.317  ← 取此值
                                                       //   r=18  0.449 / 0.367 / 0.483 / 0.252
                                                       // 关键：地面几乎不动（+1.9%），只抬墙面 —— 正是要的效果。
                                                       // r=18 更平但墙已偏亮（0.449 接近天花板带），且 6 个机位全扫
                                                       // 均零削顶，取 14 留余量。强度不动（1.25）：int→2.0 时墙面
                                                       // 0.58~0.63 已贴削顶。
    private const float BaseRangeRoom = 9f;

    /// <summary>底光强度（夜晚档每盏）。天花板亮度 P39 起由洗顶灯（Wash*）专供，底光只管
    /// 墙面/地面的均匀垫亮。P39 标定：走廊 0.85→1.05（配合 fill 提亮治墙面偏暗）；
    /// 房间 0.8→0.85；礼堂 1.2→0.9 —— P37c 的 1.2 是为治「整体昏暗」，但用户 11-14 反馈
    /// 礼堂天花板已成片过曝白带（同源证据链见 P39 文档），反弹灯删除+洗顶灯接棒后回落。</summary>
    private const float BaseIntensityCorridor = 1.25f;  // P39：0.85→1.25（首轮 play 实测偏暗，二档提亮）
    private const float BaseIntensityRoom = 1.1f;       // P39c：0.85→1.1（用户验收"教室/主任室半边黑"——墙面垫亮
                                                        // 只靠 2 盏底光，0.85 压不住 5m 跨的墙面；洗顶灯管顶、底光管墙）
    private const float BaseIntensityHall = 1.08f;      // P39d：0.9→1.3→1.6→1.3（迭代3：1.6 时底光 Point 在
                                                        // 1.5m 高直打礼堂光泽地板 → 中轴亮斑过曝；回落 1.3，
                                                        // 地板照度缺口由 HallTubeIntensity 2.5 的远场均匀覆盖补）
                                                        // ── P39f（2026-11-15）：1.3 → 1.15 → 1.08 ──────────
                                                        // 【用户两轮原话】
                                                        //   ①「感觉修复的可以，就是有点过曝，亮度稍微往下调一点」→ 1.15
                                                        //   ②「礼堂和餐厅的亮度再稍微调低一点点」→ 1.08（本轮，再 −6.1%）
                                                        // 【先说结论】过曝**不是硬削顶**。活体测：clip95=0.47%、
                                                        // 99.5% 的像素都在 1.0 以下，所以"调亮度"调的其实是
                                                        // **整体偏亮 + 高光段堆积**（0.7~1.0 占 26.4%），不是救截断。
                                                        // 【归因：Bloom 只占 20%，且是全局参数不能动】
                                                        //   礼堂：Bloom 开 0.535 → 关 0.429（Δ0.106，占 20%）
                                                        //   走廊：Bloom 开 0.269 → 关 0.258（Δ0.011，占 4%）
                                                        // 差 10 倍的原因：礼堂天花板/幕布/地板**全在 0.53 一档**，
                                                        // 整片过 0.90 阈值，Bloom 在整幅图上逐行抬升 0.05~0.16
                                                        // （p95 0.31）= 典型的"整体偏亮"；走廊暗面多，Bloom 几乎不启动。
                                                        // ⇒ 病在大房间本地配置，不在全局 Bloom。降 Bloom 会把已验收的
                                                        //   走廊/教室一起改暗（它们本来只有 0.269/0.342），故不动。
                                                        // 【为什么只降 Base+Fill、不动 WashIntensityBig】
                                                        // 活体 HDR 线性归因（关掉后处理看原始值）：
                                                        //   只留 Base → 天花板 0.22；只留 Wash → 天花板 0.06；
                                                        //   Base 强度上调 ×20 只让全幅 +0.014（Wash 近乎空转）
                                                        // 即天花板的主要供光者是 Base 而非 Wash。
                                                        // 【影响面：这三个常量被三个大房间共用，不只礼堂】
                                                        // 按 area > 200㎡ 判定，实为 ActZal(327㎡) + SportHall(735㎡)
                                                        // + Kitchen(1F餐厅 364㎡)。用户第二轮明确点名「礼堂和餐厅」，
                                                        // 正是这条共用关系的两个受害者；体育馆一并受影响，已实测确认。
                                                        // 【实测阶梯（屏幕截图口径，角色与相机同层就位后测得）】
                                                        //   档位(Base/Fill)      礼堂 mean/天花板   餐厅 mean/地板
                                                        //   1.30/2.50 原始       0.330* / —        0.394 / 0.626
                                                        //   1.15/2.10 上一版     0.330 / 0.475     0.368 / 0.601
                                                        //   1.08/1.98 本方案     0.319 / 0.467     0.357 / 0.589  ← 取此
                                                        //   1.00/1.80 激进       0.304 / 0.458     0.339 / 0.563
                                                        // * 礼堂 1.30 档因机位被跟随相机抢占，仅有 1.15 起的可比序列。
                                                        // 取 1.08/1.98 而非 1.00：用户两次都说"稍微"，−6% 符合语义；
                                                        // 再往下走会让体育馆（本底 0.293）压到 0.27 一线，接近过暗。
                                                        // 【取证注意】同层判定的锚点是「相机与角色眼高差 <2m 时用角色，
                                                        // 否则用相机」（MarkEligible 上方 1267 行）。只搬角色或只搬相机
                                                        // 都会判错层——实测踩过：角色搬 1F 而相机留 2F，餐厅 12 盏灯
                                                        // 全部 eligible=0、isPlaying 里看起来"降亮无效"，实为没点灯。
    /// <summary>P39d 保留 13，P39e 曾误收到 7 后**已回退**。
    /// 当时为压椭圆亮斑做过 range 扫点（13→9→7，椭圆 0.244→0.161），但那是在
    /// 10 盏洗顶灯被错误跳过、顶面几乎全黑的前提下测的，收窄后主理人 play 判定「更黑了」。
    /// 众数吊顶高修复落地后重新实测：12 盏洗顶灯正常洗顶时，range 13 → 全幅 0.2507、
    /// 后墙 0.2250、场地 0.1239，全面优于 range 7（0.2403 / 0.2036 / 0.1212），
    /// 且椭圆斑此时已融入均匀顶面、不再是孤立白斑。故恢复 13。</summary>
    private const float BaseRangeHall = 30f;          // P39e：13→30。13 只够到大厅**中段**，远端高墙
                                                      // （z=-17.75、y 4.5~7.1，法线 +z）实测辐照
                                                      // **0.000**——不是"变暗"，是从来没被照亮过。
                                                      // 该墙位于相机正前方且贴得极近，占画面近半，
                                                      // 全黑即成用户报的「巨大黑影」（远墙 y60-180
                                                      // 变异系数 0.923：一半死黑一半有光）。
                                                      // 30 覆盖 26m 纵深 + 两侧墙脚 → 变异系数 0.923→0.346，
                                                      // 墙均值 0.171→0.331，削顶仍 0.0%，单帧耗时反降
                                                      // （9.60→7.57ms，见下行 renderMode 注释）。
                                                      // 强度不动（1.3）：E3 实测 4.0 时墙面 0.65 全削顶。

    /// <summary>【P39 废止】P37d 天花板反弹灯（BaseCeil_*，Point 贴顶 0.35m、强度 1.6）。
    /// 废止依据（三组实测，详见 Docs/P39 文档 §2）：
    /// ① 走廊等合并大网格上被 ForcePixel 补光挤出逐像素名额 → 降级顶点光 → 在稀疏顶点的
    ///    吊顶上插值≈0，P39 实测「关闭全部 40 盏，画面逐字节不变」——「天花板亮度由反弹灯
    ///    供给」的设计意图从未实现；
    /// ② 预算有空余的房间/礼堂上保住像素光 → 贴顶 0.35m 辐照≈1.5 → 无 tonemapper 硬截断
    ///    = 用户反馈的「天花板过曝白带」（礼堂红圈光斑同源）；P37j 爆破试验（强度 30）亦复现白带；
    /// ③ 同一批灯在两种 renderer 上两种命运、随逐相机分配互换 = 「视角一动场景突变」的主凶之一。
    /// 接棒者：Wash* 洗顶灯（朝上 Spot、ForcePixel 确定性像素光、参数见下方 Wash 常量组）。
    /// 常量保留仅供历史核算引用，运行时不再创建反弹灯。</summary>
    private const float BaseBounceHeight = 3.1f;       // ⚠️ P39 废止（历史值）
    private const float BaseBounceIntensity = 1.6f;    // ⚠️ P39 废止（历史值）

    // ── P39 洗顶灯（Wash*）：天花板照明专层 ────────────────────────────
    // 使命：替代废止的贴顶反弹灯，给吊顶一层「平滑灰洗、灯上方略亮、纹理可见」的真实天花板
    // 观感（实拍对照 P39_refs/ref2：天花板均匀受光无白斑无纯黑）。
    // 【为什么 Spot 朝上且 ForcePixel】反弹灯的教训 = 「Auto 让位」在天花板这个目标面上不可用：
    // 让位即顶点光、顶点光在吊顶上不可见。洗顶灯必须确定性像素光（ForcePixel），为此候选总数
    // 已通过 range/数量收紧压到 48 名额之下（见 CorridorTubeRange 注释）。
    // 【位置】贴真实顶板下方 WashDropBelowCeiling 处：射线向上探测（P37i 同款手法），
    // 距顶 0.9m 既避免贴脸烫斑（d=0.35 时辐照 1.5 必爆），又让 150° 锥在顶面铺出 3m 半径
    // 平滑光斑、相邻灯位（3.5m）无缝衔接。
    // 【朝向】Spot 默认朝 +Z，Euler(-90,0,0) 绕 X 轴把 +Z 转成 +Y（与 CreateLight 的
    // Euler(90,0,0)=朝下互为镜像）。

    /// <summary>洗顶灯强度档（夜晚每盏）。P39 首轮 play 迭代：0.6→0.9、range 5.5→7 ——
    /// 首轮实测天花板 0.13~0.16（目标 0.25~0.5 偏暗），且盏数减半（性能回撤）后单盏加重。
    /// 灯位正上方核算：0.9×atten(r7, d0.9)≈0.65 辐照，叠加邻灯与既存漏光（实测原始 ≈0.42）
    /// → 吊顶原始 ≈1.0，× 夜档曝光(−1.45EV) × Neutral tonemapper 滚降 → 显示 ≈0.30，
    /// 落在真实对照带（0.25~0.5）内，纹理可见不削顶。</summary>
    private const float WashIntensity = 1.5f;          // P39e（迭代5·配光再平衡）：0.9→1.5。真实走廊天花板是全场
                                                       // 最亮面（web7_doubao 目标实测 ceiling 0.558 vs 我们 0.319），
                                                       // 洗顶灯 +67% 把顶面拉到主视面；上墙同步受益（0.215→目标 0.38）。
                                                       // 旧标定（0.9→0.30 显示）作废：那是"天花板暗一点也行"的口径。
    private const float WashRange = 7.0f;              // P39 首轮迭代：5.5→7（盏数减半后补覆盖）
    // ── P39h（2026-11-15 夜）：洗顶灯 range 按层高自适应 ──────────────────
    // 【解决的病】1F 餐厅（Kitchen）层高只有 3.34m，洗顶灯挂 y=2.44（离顶 0.9m），
    // 而 range=7 的平方反比曲线在 0.9m 处是 0.708、到 2.5m 处只剩 0.239（对比 2.96:1）。
    // 灯间距 6m、150° 锥在顶面投影半径仅 0.9×tan75°=3.36m ⇒ 顶面只剩一串**孤立圆亮斑**，
    // 斑与斑之间是黑的（用户 11-15 play 截图即此景）。同一 range 在大厅（离顶 0.9m 但
    // 灯高 6~10m、空间开阔）看不出问题，只在矮层高的餐厅暴露。
    // 【为什么不直接改 WashRange 常量】它是**四个分区共用**的（走廊/礼堂/体育馆/餐厅，
    // 实测 83 盏 Wash 全部同源）。活体实测把全局 range 7→12：走廊 ceiling 0.390→0.398
    // 但 std 0.146→0.212（走廊已验收，不该变糊）；体育馆/礼堂同时被抬亮，属未验证回归。
    // 【做法】只对「洗顶灯离顶距离不足」的分区，按倍数放大该区 range，其余分区保持 7 不变。
    // 灯具 range 在 BuildCeilWash 建灯时逐盏传入，不涉及 Apply() 每帧覆写，改常量即持久生效。
    // 【倍数依据】要压住斑块，中心/2.5m 的对比度需从 2.96 降到 ~1.8 一档 ⇒ range 12（1.71×）。
    // 活体实测（餐厅同机位 ScreenCapture）：天花板 mean 0.373→0.578、std 0.236→0.201、
    // 地板白带同时收敛（0.656→0.536），削顶 0.19%→0.30%（仍远低于红线）。
    // 只对 washY-floorY < WashShortCeilSpan 的分区生效：走廊 2.44m、礼堂 3.15m、
    // 体育馆 5.85m 都不触发；餐厅 2.44m 虽与走廊同高，但走廊 IsCorridor 另有配光档
    // （CorridorTubeIntensity），且走廊已验收——用 IsCorridor 排除，只留房间档。
    private const float WashShortCeilSpan = 4.0f;      // 判定「矮层高」的顶-地净高阈值（米）
    private const float WashShortCeilRangeScale = 1.71f; // 矮层高时 range 放大倍数（7→12）
    // 【阈值为什么是 4.0 而不是 2.8】首版用「washY - floorY < 2.8」判层高，翻车：
    // washY 是**世界高度**、floorY 是楼板高度，两者相减在 2F 房间（floorY=3.56）会被
    // 楼板吃掉 3.56m —— ClassDirector/Class2-2/ClassTeachers 净高 6.75m 被算成 2.29m，
    // 误判成「矮」，三间 2F 高房间被连带放大（活体实测 83 盏里 8 个分区命中，其中 3 个是误判）。
    // 改用**净高** ceilY - floorY 判定后：3.34（1F 矮房间）命中、6.75/7.61/6.90（高房间）不命中。
    // 取 4.0 留出余量：现有分区净高只有 3.34 与 6.75+ 两档，4.0 正好落在空档中间，不靠边。
    private const float WashAngle = 150f;              // 与补光 Spot 同口径的宽锥

    // ── P42（2026-11-16）：有梁/柱的大空间，洗顶灯改「全向」补梁柱侧面 ──────
    // 【老师/主理人反馈】餐厅仰视图里，横梁与方柱是**死黑剪影**（红线标注两处）。
    // 【活体确诊，不是猜的】柱面垂直剖面（同机位 cam.Render + ReadPixels）：
    //     y≤1.50 → lum 0.41~0.61（受光正常）；y≥1.70 → lum 0.06~0.09（全关时 0.085 = 零光）
    //   拐点精确落在 BaseLightHeight(1.5)。逐层 A/B 归因（柱面 2.45 / 2.85 / 3.15）：
    //     全关 0.037/0.035/0.037 → 全开 0.075/0.068/0.059，**增量只有 0.038**；
    //     而同一张图的梁底 0.105→0.549、墙面 0.072→0.332、地板 0.186→0.529 都正常。
    //   ⇒ 黑的是**法线水平的面**（梁侧面、柱身），不是梁底。
    // 【几何为什么必然黑】Fill 是 Spot 朝下 forward=(0,-1,0)、半角 75°；灯管在 y=2.21，
    //   柱子上段在 y=2.45~3.34（**在灯之上**）。实测 Fill_Kitchen_04 距柱面点仅 2.0m、
    //   cos(N,L)=0.997，但灯→点方向与锥轴夹角 101.5° > 75° ⇒ 恒在锥外。
    //   Wash 是 Spot 朝上半角 75°，同样够不到竖直面；Base 全向但只有 2 盏、
    //   位于 z=18.5 / 26.2，柱面在 z=28.50 朝 +z（背对两盏 Base）⇒ cos<0，数学上贡献 0。
    //   三层叠加，柱面上段就是 0 光。
    // 【已排除的伪解法】抬高 Base(1.5→3.2)：柱面 0.09→0.05，**更暗**（离得更远）；
    //   把 pixelLightCount 48→128：柱面纹丝不动（0.075/0.068/0.059），**不是名额竞争**；
    //   加 7 盏全向灯：柱面能到 0.19（I=0.86），但那是**加灯**，撞 FPS 红线。
    // 【定稿】把大房间的 Wash 由「朝上 Spot 150°」改为「全向 Point」，强度由
    //   WashIntensityBig(1.8) 降到 WashOmniIntensityBig(...) 补偿能量。
    //   不增一盏灯、不改布点、不动共用常量，只是换光的形状。
    //
    // 🔴【0.75 → 0.9375 的算术修正，2026-11-16 本轮踩的坑】首版直接写 0.75，想着
    //   「扫参里 0.75 得到柱面 0.20，那就填 0.75」。**错了**：扫参是直接写 Light.intensity，
    //   拿到的 0.75 是**生效值**；而常量在 Apply() 里还要再乘 `_nightDimOn ? NightDim : 1`，
    //   餐厅 NightDim=0.80（P41c）⇒ 实际生效只有 0.60，实拍柱面 0.151~0.156（不是预期 0.20）。
    //   修正：常量 = 目标生效值 / NightDimFactor = 0.75 / 0.8 = 0.9375。
    //   验证（同机位、同帧 cam.Render，生效值阶梯）：
    //     生效 0.60 → 柱 0.156/0.153/0.151，顶 0.366，全幅 0.380  ← 首版实际状态
    //     生效 0.75 → 柱 0.199/0.195/0.194，顶 0.398，全幅 0.405  ← 与扫参定稿逐位吻合
    //   ⇒ 「路径上有没有别的倍率」必须在写常量前先查清楚，否则灵敏度白测。
    private const float WashOmniIntensityBig = 0.9375f;
    /// <summary>P42：哪些分区把洗顶灯改全向。只放**有大梁/立柱**的大空间——
    /// 餐厅(Kitchen) 实测柱面 0.06~0.07；礼堂(ActZal) 无梁无柱（目视 + 竖直面采样 0 命中），
    /// 体育馆(SportHall) 远墙 P0 黑影是硬红线、本底 0.234 接近过暗，两者都不许动。
    /// 需要扩范围时往这里加名字即可，行为可按分区灰度。</summary>
    private static readonly string[] WashOmniZones = { "Kitchen" };
    private const float WashDropBelowCeiling = 0.9f;   // 距顶板距离（防贴脸烫斑）
    /// <summary>P39e：顶板高度投票的量化步长（米）。逐点向上射线命中的面会有微小差，
    /// 量化到 0.1m 后取**出现次数最多**的一档作全区吊顶高 = 真实主顶板。
    /// 体育馆实测 10 票 6.75 / 2 票 7.75 → 取 6.75；走廊、礼堂各布点命中一致 → 值不变。
    /// 作用见 BuildCeilWash ② 的详细注释（替换了原先「取最高命中」的翻车逻辑）。</summary>
    private const float WashCeilVoteQuantum = 0.1f;
    /// <summary>P39e：估顶兜底里「吊顶板」的最大厚度（米，bounds.size.y）。超过即视为竖直面
    /// （窗/帘/墙）排除，不当吊顶。礼堂实测：ZalTop 真顶板 0.00、Box002 高顶盒 0.14 均通过；
    /// Glass6 玻璃窗 2.70 被排除 —— 它比顶板低 3.14m，原本会被「取最低 minY」误选成吊顶。
    /// 取 0.6 而不是更小：隔层板/吊顶板带构造厚度，实测最大 0.14；再小没有收益，
    /// 再大（如 3.0）就会把玻璃窗放回来。</summary>
    private const float CeilSlabMaxThickness = 0.6f;
    private const int WashPerCorridor = 3;             // ⚠️ P39b 废止（历史值）：固定三档改为按跨度/灯管数推盏数
    private const int WashPerRoom = 1;                 // ⚠️ P39b 废止（历史值）
    private const int WashPerBigRoom = 2;              // ⚠️ P39b 废止（历史值）

    // ── P39b 洗顶灯覆盖（按真实场景推盏数，替代上面固定三档）────────────
    // 用户口径：照着真实世界调，且各场景按自身灯具数量决定光照范围/强度。固定"走3房1大2"
    // 的直接后果：体育馆 8 管只有 2 盏洗顶、50m 走廊也只有 3 盏——灯间顶面断光、大空间
    // 覆盖不足（11-15 用户截图：走廊顶一截亮一截黑、体育馆/礼堂顶全黑）。
    private const float WashSpanStep = 6f;             // P39c：8→6（用户验收"光照不均匀/区域昏暗"——8m 步距下 150° 锥
                                                       // 在顶面有效亮半径 ~2.7m，相邻光斑之间留黑段；6m 起步衔接）
    private const float WashTubeRatio = 4f;            // 每几根真实灯管配 1 盏洗顶灯（反弹量随灯具数走）
    private const int WashMin = 2;
    private const int WashMax = 14;                    // P39c：8→14（走廊 70m/6m=12 盏全覆盖，大房间 5 行×2=10）
    private const float WashRayMaxDistance = 12f;      // P39b：贴顶射线上探距离（旧 4.5m 够不着 8~10m 的体育馆/礼堂顶）
    private const float WashIntensityBig = 1.8f;       // P39b：大空间（礼堂/体育馆）单盏强度——顶高 8~10m、面积数百㎡，
                                                       // 沿用房间档 0.9 会在 2m 距顶衰减后不可见（实测体育馆顶全黑）；
                                                       // 大空间灯具多（礼堂/体育馆各 8~12 管），反弹量按灯具数走。

    /// <summary>大房间（>200㎡，方案乙）底光盏数：沿长轴 t=0.25/0.75 两点
    ///（与 BuildRoomFills 的沿长轴均布同口径）。不升单盏强度：点光的天花板/地面贡献
    /// 随强度线性上涨，2 盏只在中间互相叠加、峰值不翻倍——覆盖翻倍、红线不动。</summary>
    private const int BaseLightsBigRoom = 2;

    /// <summary>普通房间底光盏数（P37e：1→2，t=0.25/0.75）：接管扩大后 glow 退场，
    /// 单盏居中的垫底光压不住"半间房亮半间房暗"，两点布设让两面墙都够得着；
    /// 反弹灯随垫底灯同步 ×2，教室天花板亮斑覆盖率同步翻倍。</summary>
    private const int BaseLightsRoom = 2;

    /// <summary>走廊底光沿长轴均布的封顶盏数。走廊分区长达数十米，1 盏盖不住，
    /// 按 CorridorStep(7m) 取整。P39：6→4 —— 底光 Point(Auto) 在走廊合并大网格上本就
    /// 降级顶点光（P39 实测其对天花板贡献仅 0.04），4 盏足够供给平滑垫亮，省出的逐像素
    /// 名额让给洗顶灯（天花板照明改为确定性像素光专供）。</summary>
    private const int MaxBaseLightsCorridor = 4;

    // ── P37b 地面高光抑制（联网实拍对照后定向修复：地板镜面反射条）──
    // 病灶：打蜡地板把灯管镜面反射拉成刺眼亮条（五景截图的共同扣分项；v11 撤销 v5 的
    // 抑制后复发）。真实走廊/教室的地面反射是柔和长晕。做法：夜晚/黄昏档把下列大平面的
    // _Glossiness 钳到 0.62（P35 v5 轮实测值），白天档自动还原原值（白天零变化红线）。
    // MPB 方式（MaterialPropertyBlock）：不碰材质资产、不污染共享材质；先 GetPropertyBlock
    // 再写，不覆盖别人的属性块（TwinStatusColor 的染色对象是暖气片，不在名单内，无交集）。
    // 【P39h 修正：钳制必须是「只降不升」】原实现无条件 SetFloat(0.62)，而实测名单 31 个面的
// 原始 _Glossiness 分布是：0.84(KitchenFloor)、0.85(ZalStairs)、0.50(多数 Border*)、
// 0.40(Corridor/Box001/Floor2)、0.13(Box002)、0.00(ZalTop/PCClassTop/DirectorTop 等顶板)。
// 也就是说 0.62 这个值对 **25 个面是在往上抬**（0.50→0.62、0.40→0.62、0.00→0.62），
// 只有 KitchenFloor/ZalStairs 两个面是真正被压低。抬升本身会加重高光（P39h 实测地板白带
// 削顶 1.36%、P95=0.918 即此果），与「抑制高光」的原意相反。
// 改法：取 min(原值, 钳制值)，即只压不抬。这样低反光面完全不动（已验收的走廊/教室/顶板
// 零变化），只有真正高反光的抛光地砖被压下去。
// 【为什么厨房地板要比全场景的 0.62 更狠】KitchenFloor 原值 0.835（全场最高），且 Fill 灯
// 垂直向下正对地板，镜面光路最短。活体 ScreenCapture 实测（同机位、同状态、只改这一个值）：
//   0.62 → floor P95=0.918、削顶 1.36%
//   0.30 → floor P95=0.882、削顶 0.60%  ← 取此
//   0.15 → floor P95=0.873、削顶 0.59%（收益饱和，再压只是丢掉地砖质感）
// 地板均值在 0.492→0.486 之间几乎不动，天花板 0.681 完全不受影响 ⇒ 削掉的只是高光峰值。
private const float GlossClampValue = 0.62f;
    /// <summary>抛光地砖（原 _Glossiness ≥ 此阈值）额外压到 GlossClampStrong。只对真正
    /// 高反光的面生效，普通面仍走 GlossClampValue 的「只降不升」逻辑。阈值取 0.80：
    /// 实测名单里只有 KitchenFloor 0.835 / ZalStairs 0.85 两个面越线，恰好是两处抛光地/楼梯。</summary>
    private const float GlossStrongMin = 0.80f;
    private const float GlossClampStrong = 0.30f;
    private const float GlossMinFloorArea = 20f;   // Floor 关键字兜底的最小水平面积（㎡），防止捞到小物件

    // ── P41（2026-11-16）：夜晚地面按老师口径压到「粗糙、不反光」 ──────────────
    // 【老师原话】「这两个空间地面可以再暗一点，你看地面这块咋调？……我建议它那个地板就
    //   不要反光，就那种粗糙的就可以暗一点，这样子的话，不容易曝光。」
    // 【为什么 P39h 的「只降不升」不够】名单里真正的房间地面反照率并不高：
    //   礼堂 Zal=0.500、餐厅 KitchenFloor=0.835、2F 走廊 Border*=0.500、Corridor=0.400。
    //   旧逻辑 min(原值, 0.62) 对 0.500/0.400 的面**等于什么都没做**（min 出来还是原值），
    //   只有 0.835/0.850 两块抛光砖被压过。所以礼堂/走廊的地面白带一直没消。
    // 【修法】把「地面」和「顶板」分开对待：
    //   地面（名字含 Floor / 且是水平大面）→ 一律压到 GlossFloorTarget(0.18，接近粗糙漫反射)；
    //   其余已在名单里的面 → 维持 min(原值, GlossClampValue) 只降不升。
    // 【为什么是 0.18】Standard shader 非金属的镜面项恒约 4%，_Glossiness 只控制高光**锐度**。
    //   0.62 仍是「亮面」的量级（高光收得很紧、峰值不减）；0.18 会让高光摊成大面积弱晕，
    //   观感就是老师要的「粗糙地面」；再往下（0.05）几乎无高光，地砖会失去质感、像水泥。
    // 【为什么不改 _Metallic】它已是 0，改无可改。
    private const float GlossFloorTarget = 0.18f;

    // ── P41b：桌面 / 台面同样压（老师"不容易曝光"的真正主源）──────────────────
    // 【为什么必须一起做】只压地面后实测餐厅前景桌面 clip 仍高达 **4.46%**、P95=0.990 ——
    // 比地板(1.40%)严重得多。餐桌桌面材质 Wood3 的 _Glossiness 是 **0.850**，比地板还光滑；
    // 不锈钢台面 MetallChrome03 是 0.890。它们被 Fill 灯正对，就是画面里那几团白头。
    // 老师说的是"地面"，但"不容易曝光"的诉求指向画面里所有刺眼白斑，只做地面等于没解决。
    // 【范围】只压真正造成大面积白斑的两种材质：Wood3(餐桌桌面 56 个) 与
    // MetallChrome03(不锈钢打餐台 49 个)。**不含** MetallChrome01（椅腿金属 166 个）——
    // 椅腿是细杆、没有大面积镜面高光，压它属于越界改动且会削弱家具金属质感。
    // 首版把 MetallChrome01 也放进来，名单从 105 涨到 271 个面，属于过度处理，已收回。
    private static readonly string[] GlossDeskMaterials = { "Wood3", "MetallChrome03" };
    private const float GlossDeskTarget = 0.30f;

    /// <summary>命中名单 = P35 v5 轮实测的 31 个大平面（Editor.log 原文），一字未改照抄——
    /// 派工/修复清单来自实测，不来自印象（排查方法论第 23 条）。</summary>
    private static readonly string[] GlossClampNames =
    {
        "ZalTop", "ZalStairs", "Zal", "ReshetkaRight", "ReshetkaLeft", "PCClassTop",
        "KitchenFloor", "Kitchen", "DirectorTop", "Corridor", "BuildingBottomFloor",
        "BuildingBottom", "Box002", "Box001", "Border017", "Border016", "Border015",
        "Border014", "Border013", "Border012", "Border011", "Border010", "Border9",
        "Border8", "Border7", "Border5", "Border4", "Border3", "Border2", "Border1", "Floor2"
    };
    private static readonly int GlossPropertyId = Shader.PropertyToID("_Glossiness");

    // ── 一个分区 = 一组补光点 + 它的灯具锚点 ──────────────────────────
    private sealed class ZoneFill
    {
        public string Zone;
        public bool IsCorridor;
        public Light[] Lights;          // 首选 = 每根灯管一个；兜底 = 按矩形布点
        public bool[] Eligible;         // 本帧是否允许启用（同层判定，Poll 时算好）
        public float[] Fade;            // Eligible 的平滑版（0~1，0.25s 过渡），Apply 用它写强度
        public float BaseIntensity;
        public TwinAnchor LampAnchor;   // 该分区「灯具」测点；可能为 null（没建点位就按常亮处理）
        public float LampScale;         // 0~1 平滑后的灯具开关量
        public float LampTarget;        // 0/1 目标值（Poll 时算）
        public bool TakeOver;           // 本分区的旧光点（TwinLampGlow）是否由本层接管（走廊/大房间）
        public bool LoggedTakeOver;     // 接管日志只打一次
        public float MinX, MaxX, MinZ, MaxZ;
        // ── P37 底光层（只增层不改层：以上补光字段与其逻辑一行未动）──
        public Light[] BaseLights;      // 每分区 1~12 盏全向垫底光（含 P37c 天花板反弹灯；见 Base* 常量注释）
        public bool[] BaseEligible;     // 同层判定（与补光点同一条规则，MarkEligible 一并算）
        public float[] BaseFade;        // 生效平滑量（0~1），Apply 写强度用
        public float[] BaseLightLevels; // 每盏底光的强度档（垫底灯走走廊/房间/礼堂档，反弹灯走 Bounce 档）
        public float FloorY;            // 本分区地面绝对高度（P37i：垂直柱状剖面取证用）
        // ── P41c：本分区的夜档强度倍率（默认 1；仅礼堂/餐厅为 NightDimFactor）──
        // 在 BuildZones 里一次算好，Apply 的三个强度出口（fill / 底光 / 洗顶）统一乘它，
        // 不改共用常量，因而不波及体育馆（同属 bigRoom 档但不在名单里）。
        public float NightDim;
    }

    private readonly List<ZoneFill> _zones = new List<ZoneFill>(16);
    private readonly List<Light> _suppressedGlows = new List<Light>(32);
    // P37b 地面高光抑制的运行时状态（名单收集一次，随昼夜档翻转钳制/还原）
    private readonly Dictionary<Renderer, float> _glossOriginal = new Dictionary<Renderer, float>(32);
    // P41：名单里哪些是「地面」（水平大面 + 名字含 Floor）。地面按老师口径压到 GlossFloorTarget，
    // 其余（顶板/天花之类）只走 min(原值, GlossClampValue) 的老逻辑。
    private readonly HashSet<Renderer> _glossFloors = new HashSet<Renderer>();
    // P41b：桌面/台面（Wood3 / MetallChrome*）—— 与地面分开一档，压到 GlossDeskTarget。
    /// 【为什么 0.30 而不是 0.18】桌面是抛光木/不锈钢，现实里本来就该有柔和高光；
    /// 压到 0.18 会像水泥台面，失去家具质感。0.30 足以把镜面白斑摊成弱晕（与抛光地砖同档）。
    private readonly HashSet<Renderer> _glossDesks = new HashSet<Renderer>();
    private bool _glossClamped;
    private bool _buildFailedFinal;
    // P37f 现场取证（一次性自动运行，只读）：夜晚档就绪 2 秒后打印，主理人零操作，日志回传由执行方自读
    private bool _diagDone;
    private float _diagCountdown = -1f;
    // P37h 自动截图：取证完成后每 2 秒一张、共 3 张，落到 Docs/QA/evidence/P37_auto/（执行方自读，主理人零操作）
    private int _autoShot;
    private float _shotTimer;
    // 【P39 已移除】P37j 贴顶爆破试验的 4 个状态字段——试验已完成历史使命（证明灯→顶板通路
    // 可通且必然白斑，支撑反弹灯废止决策），随反弹灯废止一并清理，避免夜间自动闪烁干扰验收。
    private Camera _cam;
    private float _camSearchTimer;
    private Transform _player;
    private float _playerSearchTimer;
    private float _pollTimer;
    private float _dayScale = 1f;
    // P41c：夜档降档总闸——只有 timeOfDay==Night 时为 true，Dusk/白天一律 false。
    // 与 _dayScale 分开是刻意的：_dayScale 白天也有值（0.05~0.58），拿它判夜会误伤 P40 的阴雨补光。
    private bool _nightDimOn;
    private int _activeCount;

    // ---------------------------------------------------------------- 生命周期

    private void Awake()
    {
        // 建灯**不在 Awake 做**：灯具锚点在它们自己的 Start() 才注册进 Registry
        //（TwinAnchor.cs:168 原注释「注册挪到 Start()：OnEnable 时 TwinRuntime.Awake 可能
        // 还没跑」），AfterSceneLoad 时机 Registry 还是空的 —— 首跑日志打出
        // 「分区 0 / 补光点 0」就是这个原因。真正的建灯在 Update 的就绪检查里。
        // 昼夜倍率从当前档起步，不从 0 动画 —— 中途挂载不该看到一段无来源的渐亮
        _dayScale = TargetDayScale();
    }

    private void OnDestroy()
    {
        // 退出前把接管期间关掉的旧光点还回去（哪怕总开关是开的——本层都要死了，
        // 不能留下一批被关掉的 glow 让表现层背锅）
        RestoreSuppressedGlows();
        // 根节点随场景销毁时把静态引用还回去，否则下一局/下次挂载会误判"已存在"（F18 同族）
        IndoorFillLight.NotifyRunnerDestroyed();
    }

    // ---------------------------------------------------------------- 建灯（幂等）

    private void BuildZones()
    {
        TwinRuntime rt = TwinRuntime.Instance;
        if (rt == null || rt.Registry == null) return;

        // ── 分区清单的来源：Registry 里的「灯具」测点，不是 TwinSiteLayout ──
        // 【为什么】TwinRoomBounds 里有 SportGate 这种"为了判门方向而登记"的矩形，
        // 它不是房间、没有灯具；拿 SiteLayout 全表建灯会给它也塞一个光点。
        // 以真实存在的灯具测点为源头（F1：白名单里有 ≠ 场景里有），再拿
        // TwinRoomBounds 查水平范围 —— 两者都过才建。
        for (int i = 0; i < rt.Registry.All.Count; i++)
        {
            TwinAnchor a = rt.Registry.All[i];
            if (a == null) continue;
            if (!string.Equals(a.Category, "Light", StringComparison.Ordinal)) continue;
            if (string.IsNullOrEmpty(a.Zone)) continue;
            // BAK 测点未激活、也不归任何分区，跳过（与 TwinDeviceStats 的口径一致）
            if (a.TwinId != null && a.TwinId.StartsWith("BAK", StringComparison.Ordinal)) continue;
            if (FindZone(a.Zone) >= 0) continue;   // 同分区只建一次（防一手，正常不会重复）

            float minX, maxX, minZ, maxZ;
            if (!TwinRoomBounds.TryGetBounds(a.Zone, out minX, out maxX, out minZ, out maxZ))
            {
                Debug.Log("[IndoorFill] 分区「" + a.Zone + "」在 TwinRoomBounds 没登记范围，本轮不补光" +
                          "（要补就在那张表加一行，此提示只出现一次）");
                continue;
            }

            TwinRoomInfo info = TwinSiteLayout.Find(a.Zone);
            int floor = info != null ? info.Floor : 1;
            // 楼层 → 地面高度：1F 地面 ≈0、2F ≈3.56。出处见 StoreyHeight 注释。
            float floorY = (Mathf.Clamp(floor, 1, 2) - 1) * StoreyHeight;

            bool corridor = a.Zone.StartsWith("Corridor", StringComparison.Ordinal);
            float area = (maxX - minX) * (maxZ - minZ);

            ZoneFill z = new ZoneFill();
            z.Zone = a.Zone;
            z.IsCorridor = corridor;
            z.LampAnchor = a;
            z.MinX = minX; z.MaxX = maxX; z.MinZ = minZ; z.MaxZ = maxZ;
            z.FloorY = floorY;
            // P41c：夜档降档倍率（老师点名的礼堂 + 餐厅；体育馆不在名单 → 1，零影响）
            z.NightDim = 1f;
            for (int k = 0; k < NightDimZones.Length; k++)
            {
                if (!string.Equals(a.Zone, NightDimZones[k], StringComparison.Ordinal)) continue;
                z.NightDim = NightDimFactor;
                break;
            }
            // 接管范围（P37d 扩大为全楼）：教室与走廊过去是"两套光 vs 一套光"两回事，
            // 主理人判定这是"两种截然不同的灯光效果"。全楼统一接管 → 所有房间同一配方
            // （补光 Spot + 垫底灯 + 天花板反弹灯），明暗斑块与过亮叠加一并消失。
            z.TakeOver = true;

            // 首选：补光点放在**真实灯管正下方**（灯位从场景读，不是按矩形拍点）——
            // 仿真语义：有几根管就有几片光，光池对准灯管，灯不动光就不动。
            if (!BuildTubeFills(z))
            {
                // 兜底：灯位不可得（锚点在标记牌上 / 容器结构意外）→ 退回矩形布点
                if (corridor) BuildCorridorGrid(z, floorY);
                else BuildRoomFills(z, floorY, area);
                // 【P37 小项 B·已批准】兜底路径此前完全静默，「哪个区没走到灯管路径」
                // 只能靠日志计数倒推（P36 回执的做法）；这一行让它变成直读。上面的调度不动。
                Debug.Log("[IndoorFill] 分区「" + z.Zone + "」灯位不可得，改走兜底网格布点" +
                          "（走廊=CorridorGrid / 房间=RoomFills）");
            }

            // P37 底光层：无论补光走首选还是兜底，底光照建——它垫的是「房间反弹」，
            // 只跟分区矩形有关，跟灯位抓没抓到无关。
            BuildBaseLights(z, floorY);

            _zones.Add(z);
        }
    }

    /// <summary>
    /// 等 Registry 就绪后建灯。就绪判定 = Registry.All 里出现了锚点。
    /// 【为什么每帧看一眼】灯具锚点在它们自己的 Start() 才注册（TwinAnchor.cs:168），
    /// 任何"猜时机"的做法都会重蹈首跑「分区 0」的覆辙；一次 Count 读取的成本可忽略。
    /// 【为什么失败只打一次】Registry 有锚点却一个灯具分区都没匹配上 = 这个场景
    /// 真没有可补光的房间，重试没有意义 —— D17：重试必须有上限，超限放弃 + 只留一条。
    /// </summary>
    private void BuildZonesWhenReady()
    {
        TwinRuntime rt = TwinRuntime.Instance;
        if (rt == null || rt.Registry == null || rt.Registry.All.Count == 0) return;

        BuildZones();
        if (_zones.Count == 0)
        {
            _buildFailedFinal = true;
            Debug.Log("[IndoorFill] Registry 已就绪但没有匹配到任何灯具分区，本场景不补光（此条只打一次）");
            return;
        }

        CollectGlossTargets();   // P37b：一次性收集大平面名单（含 _Glossiness 原值），供昼夜档翻转钳制/还原
        Poll();
        Apply(0f);
        Debug.Log("[IndoorFill] 补光层就绪：" + Describe());
    }

    /// <summary>
    /// 走廊兜底：沿 X 铺一圈**候选网格**（灯位不可得时才走）。
    /// 与灯位路径相比少了"光池对准灯管"的对齐感，所以它只是兜底，不是目标形态。
    /// </summary>
    private void BuildCorridorGrid(ZoneFill z, float floorY)
    {
        float zc = (z.MinZ + z.MaxZ) * 0.5f;
        float span = z.MaxX - z.MinX;
        int n = Mathf.Max(1, Mathf.FloorToInt((span - CorridorStep) / CorridorStep) + 1);
        z.Lights = new Light[n];
        z.Eligible = new bool[n];
        z.Fade = new float[n];

        for (int i = 0; i < n; i++)
        {
            float x = Mathf.Clamp(z.MinX + CorridorStep * 0.5f + i * CorridorStep, z.MinX + 1.5f, z.MaxX - 1.5f);
            z.Lights[i] = CreateLight("Fill_" + z.Zone + "_" + i.ToString("00"),
                                      new Vector3(x, floorY + FillHeightAboveFloor, zc),
                                      CorridorRange);
        }
        z.BaseIntensity = CorridorIntensity;
    }

    /// <summary>房间兜底：按面积出 1~3 个点，沿长轴中线均布。教室 85m² → 2 个，食堂/礼堂 360m² → 封顶 3 个。</summary>
    private void BuildRoomFills(ZoneFill z, float floorY, float area)
    {
        int count = Mathf.Clamp(Mathf.RoundToInt(area / RoomAreaPerFill), 1, 3);
        z.Lights = new Light[count];
        z.Eligible = new bool[count];
        z.Fade = new float[count];

        bool alongX = (z.MaxX - z.MinX) >= (z.MaxZ - z.MinZ);
        float range = Mathf.Clamp(RoomMinRange + area / 120f, RoomMinRange, RoomMaxRange);

        for (int i = 0; i < count; i++)
        {
            float t = (i + 0.5f) / count;
            float x = alongX ? Mathf.Lerp(z.MinX, z.MaxX, t) : (z.MinX + z.MaxX) * 0.5f;
            float zz = alongX ? (z.MinZ + z.MaxZ) * 0.5f : Mathf.Lerp(z.MinZ, z.MaxZ, t);
            z.Lights[i] = CreateLight("Fill_" + z.Zone + "_" + i.ToString("00"),
                                      new Vector3(x, floorY + FillHeightAboveFloor, zz),
                                      range);
        }
        z.BaseIntensity = area > 200f ? HallIntensity : RoomIntensity;
    }

    /// <summary>
    /// P37 底光层：给分区垫一层低强度全向光，近似「房间对这些灯具的反弹」。
    /// 【为什么用 Point 不用 Spot】底光的使命是消除 Spot 锥体在墙上的硬边界（半截黑/斜边），
    /// 全向光在墙面上只有距离衰减、没有锥边，天生无边界——再上 Spot 等于把病根复制一层。
    /// 【盏数】走廊沿长轴均布（span/CorridorStep 向上取整、封顶 MaxBaseLightsCorridor）——
    /// 走廊分区长达数十米，1 盏盖不住；房间 1 盏居中；大房间（>200㎡，方案乙）2 盏沿长轴
    /// t=0.25/0.75（与 BuildRoomFills 的沿长轴均布同口径）。
    /// 位置=分区包围盒中心线、离地 BaseLightHeight；range/强度走 Base* 常量。
    /// 【与「应急底光」的区别】EmergencyFloorFactor 是"灯具关断时补光保留的系数"，
    /// 本层是真实存在的灯，两回事。
    /// </summary>
    private void BuildBaseLights(ZoneFill z, float floorY)
    {
        float spanX = z.MaxX - z.MinX, spanZ = z.MaxZ - z.MinZ;
        bool bigRoom = !z.IsCorridor && spanX * spanZ > 200f;
        int count = z.IsCorridor
            ? Mathf.Min(Mathf.CeilToInt(Mathf.Max(spanX, spanZ) / CorridorStep), MaxBaseLightsCorridor)
            : (bigRoom ? BaseLightsBigRoom : BaseLightsRoom);
        float layerIntensity = z.IsCorridor ? BaseIntensityCorridor
                                            : (bigRoom ? BaseIntensityHall : BaseIntensityRoom);

        bool alongX = spanX >= spanZ;
        float range = z.IsCorridor ? BaseRangeCorridor : (bigRoom ? BaseRangeHall : BaseRangeRoom);
        // P39：反弹灯废止（双面失败证据见 BaseBounceIntensity 注释），天花板照明改由
        // 洗顶灯（Wash*，朝上 Spot、ForcePixel）专供。洗顶灯挂进 BaseLights 数组——
        // Apply() 的底光驱动链路（总开关×昼夜闸×同层×灯具开关×淡入淡出）对它原样生效。
        // P39b：洗顶灯盏数 = max(长轴跨度/WashSpanStep, 真实灯管数/WashTubeRatio) 钳到 [WashMin, WashMax]。
        // 大房间按"行×2"（蛇形双列布点，见 BuildCeilWash）：体育馆 28m 长轴 → 4 行×2 列 = 8 盏，
        // 单列布点会让横轴两侧顶面全程无人覆盖（实测左右顶黑、仅中轴一条亮）。
        // 灯管数 = z.Lights.Length（首选路径每根管一个补光点，即真实灯管口径）——灯具越密反弹越多，
        // 与真实场景"灯多顶亮"一致。
        int tubeCount = z.Lights != null ? z.Lights.Length : 0;
        float longSpan = Mathf.Max(spanX, spanZ);
        int rows = Mathf.CeilToInt(longSpan / WashSpanStep);
        int washCount = bigRoom
            ? Mathf.Clamp(rows * 2, WashMin, WashMax)
            : Mathf.Clamp(Mathf.Max(rows, Mathf.CeilToInt(tubeCount / WashTubeRatio)), WashMin, WashMax);
        int total = count + washCount;
        z.BaseLights = new Light[total];
        z.BaseEligible = new bool[total];
        z.BaseFade = new float[total];
        z.BaseLightLevels = new float[total];
        int idx = 0;
        for (int i = 0; i < count; i++)
        {
            // 沿长轴均布：t=(i+0.5)/count。count=1 时 t=0.5 恰是包围盒中心，count=2 时 0.25/0.75，
            // 与 BuildRoomFills 的布点公式同款——一套公式覆盖三种分区。
            // P39 修正潜伏 bug：!alongX 分支的 x 中心原写成了 (MinZ+MaxZ)*0.5（把 Z 中心当 X
            // 中心），ActZal（X 12.3m × Z 26.5m，唯一 !alongX 分区）的底光因此被放到 x=25.25
            // ——完全在房间外、落进体育馆，这正是礼堂「讲台端昏暗」的隐藏成因之一。
            float t = (i + 0.5f) / count;
            float x = alongX ? Mathf.Lerp(z.MinX, z.MaxX, t) : (z.MinX + z.MaxX) * 0.5f;
            float zz = alongX ? (z.MinZ + z.MaxZ) * 0.5f : Mathf.Lerp(z.MinZ, z.MaxZ, t);
            // P39d：大厅底光优先吸附真实灯管正下方——底光 Point 在光泽地板上必然砸出光池，
            // 光池若不在任何可见灯具下 = 「地板不明光斑」（用户验收实测礼堂中轴亮斑）。
            // 大房间拿第 (i+0.5)*tubeCount/count 根管的 XZ；走廊不吸附（走廊 bounds 比管位宽，
            // 西端无管段靠底光垫亮，吸附会把覆盖缩回管区）；房间管位即中心线，等价。
            if (bigRoom && z.Lights != null && z.Lights.Length > 0)
            {
                int ti = Mathf.Clamp(Mathf.RoundToInt((i + 0.5f) * z.Lights.Length / count) - 1, 0, z.Lights.Length - 1);
                Light tube = z.Lights[ti];
                if (tube != null) { x = tube.transform.position.x; zz = tube.transform.position.z; }
            }
            z.BaseLights[idx] = CreateBaseLight("Base_" + z.Zone + "_" + i.ToString("00"),
                                                new Vector3(x, floorY + BaseLightHeight, zz), range);
            // 【P39e】底光必须确定性逐像素。原先只有走廊特判，体育馆漏网，两者是**同一个病**：
            // 走廊 = 70m 合并网格、体育馆内壳 = Box001 一整个合并网格（地板+四面墙+顶板同属
            // 一个 mesh，画面占比实测 69.9%）。这类巨网格顶点极稀，Auto 底光让位降级成顶点光后，
            // 插值不出亮度 ⇒ 面上只吃环境光。
            // 更糟的是名额竞争：plc 实测 48（QualitySettings 在 SetQualityLevel(5) 后被重置回 48，
            // 代码里设的 32 被覆盖），83 盏 Wash + 38 盏 Fill 先抢，Base 排在后面时而被挤掉、
            // 时而又拿回名额 ⇒ 同一块墙在死黑与有光之间跳 = 用户报的「阴影带随视角变深变浅」。
            //
            // 为什么只有全向 Base 能救墙面：Fill/Wash 都是 150° 竖直锥（半角 75°），墙面法线
            // 水平，与竖直锥轴夹角 ≈90° —— 几何上永远在锥外。墙面唯一的光源就是全向 Base。
            //
            // MCP 活体实测（cam.Render + ReadPixels 同步取样）：
            //   走廊：暗区 0.0787→0.2489，亮区 0.4330→0.4459（不降反升）
            //   体育馆：远墙 0.171→0.331、CV 0.923→0.346；整幅 0.195→0.289；削顶仍 0.0%
            // FPS 对冲：两区都改 ForcePixel 后单帧 9.60→7.57ms（Auto 降级路径反而更贵），
            // 无需减灯；走廊侧仍保留 MaxTubeFillsCorridor 8→4 的净额余量。
            z.BaseLights[idx].renderMode = LightRenderMode.ForcePixel;
            z.BaseLightLevels[idx++] = layerIntensity;
        }
        BuildCeilWash(z, floorY, alongX, bigRoom, washCount, ref idx);
    }

    /// <summary>
    /// P39 洗顶灯：每分区 washCount 盏朝上 Spot（ForcePixel），贴真实顶板下方
    /// WashDropBelowCeiling 处，给吊顶一层确定性像素光照明。位置沿分区长轴均布
    /// （不按灯位——走廊灯位密集时按灯位布会浪费名额且过密过亮）。
    /// 高度探测复用 P37i 反弹灯的射线手法：从垫底灯高度向上打射线找第一块面
    /// （有碰撞体时精确贴其下 WashDropBelowCeiling，无碰撞体回退假定高度）。
    /// </summary>
    private void BuildCeilWash(ZoneFill z, float floorY, bool alongX, bool bigRoom, int washCount, ref int idx)
    {
        // P42：本分区是否走「全向补梁柱」模式（名单见 WashOmniZones 注释）。
        // 只对名单内分区生效，其余分区（走廊/教室/礼堂/体育馆）行为逐位不变。
        bool washOmni = false;
        for (int k = 0; k < WashOmniZones.Length; k++)
        {
            if (!string.Equals(z.Zone, WashOmniZones[k], StringComparison.Ordinal)) continue;
            washOmni = true;
            break;
        }

        // ── ① XZ 布点（与高度无关，先算好）─────────────────────────────
        // P39b：大房间蛇形双列（沿长轴推进、横轴在 28%/72% 两列间交替）——体育馆宽 ~19m，
        // 单列中轴布点（旧逻辑）左右两侧顶面全程无人覆盖；走廊窄，单列居中即可。
        float[] xs = new float[washCount];
        float[] zs = new float[washCount];
        for (int i = 0; i < washCount; i++)
        {
            float t = (i + 0.5f) / washCount;
            float lateralT = (i % 2 == 0) ? 0.28f : 0.72f;
            xs[i] = bigRoom ? (alongX ? Mathf.Lerp(z.MinX, z.MaxX, t) : Mathf.Lerp(z.MinX, z.MaxX, lateralT))
                            : (alongX ? Mathf.Lerp(z.MinX, z.MaxX, t) : (z.MinX + z.MaxX) * 0.5f);
            zs[i] = bigRoom ? (alongX ? Mathf.Lerp(z.MinZ, z.MaxZ, lateralT) : Mathf.Lerp(z.MinZ, z.MaxZ, t))
                            : (alongX ? (z.MinZ + z.MaxZ) * 0.5f : Mathf.Lerp(z.MinZ, z.MaxZ, t));
        }

        // ── ② 全区统一吊顶高（P39c）────────────────────────────────────
        // 逐盏各自射线会命中梁/桁架/楼板等**不同面**（实测同区 washY 方差：SportHall
        // 5.85~6.85、ActZal 6.71~7.26、Corridor2 5.65~6.00）——距顶远近不一 → 光斑大小
        // 亮度参差 = 礼堂"不明光斑"、走廊顶一段亮一段暗的直接成因。
        // 真实逻辑：同一房间灯具吊高一致。
        // 【P39e 修正】原实现取全区**最高命中**，在体育馆翻车：12 个布点里 10 个命中
        // 主顶板 DirectorTop=6.75、2 个命中局部高顶 Box001=7.75，取最高 → ceilY=7.75
        // → 洗顶灯全挂 6.85，**比主顶板还高 0.10m，10 盏穿进板里**。穿板灯的 150° 锥
        // 从板缝漏到画面 = 椭圆不明光斑；而真正该被洗亮的主顶面反而没吃到光。
        // 改为取**众数高度**（出现次数最多的顶板），即真实主顶板：
        //   体育馆 → 6.75（10/12 票）→ 灯挂 5.85，12 盏全在主顶板下方且不穿板；
        //   走廊/礼堂 → 各布点命中本就一致，众数 = 原值，行为完全不变（实测 0/12 误伤）。
        // 这样既消掉穿板漏光，又保住全部洗顶灯 → 顶面亮度不塌。
        // 对比过的错误修法（已废弃）：按局部净空**跳过**穿板灯 → 体育馆少 10/12 盏，
        // 天花板中带亮度 0.039（豆包对照 0.279），主理人 play 判定「更黑了」。
        var ceilVotes = new Dictionary<int, int>();
        var ceilValue = new Dictionary<int, float>();
        float maxHit = 0f;
        for (int i = 0; i < washCount; i++)
        {
            Vector3 rayBase = new Vector3(xs[i], floorY + BaseLightHeight, zs[i]);
            RaycastHit ceilHit;
            float hitY;
            if (Physics.Raycast(new Ray(rayBase, Vector3.up), out ceilHit, WashRayMaxDistance))
                hitY = ceilHit.point.y;
            else
                hitY = EstimateCeilYAbove(xs[i], zs[i], floorY);
            if (hitY > maxHit) maxHit = hitY;
            int key = Mathf.RoundToInt(hitY / WashCeilVoteQuantum);
            if (!ceilVotes.ContainsKey(key)) { ceilVotes[key] = 0; ceilValue[key] = hitY; }
            ceilVotes[key]++;
            if (hitY > ceilValue[key]) ceilValue[key] = hitY;   // 同档取偏高值，宁可略低不穿板
        }
        // ⚠️【P39e 已知缺陷，本轮未修】平票裁决的初值 ceilY=0 使 `ceilValue < ceilY` 永假，
        // 平票时实际由字典遍历顺序决定。日志实证：「Class2-1」三票互不相同（各 1 票）时选中
        // ceilY=0.00 → haveCeil=false → 走兜底 washY=floorY+3.7，洗顶灯挂在真实顶板上方。
        // 「ActZal」ceilY=4.61(7/10 票) 低于 floorY(3.56)+1.9 门槛 → 同样落兜底 y=7.26。
        // 不在此轮修的理由：这两个分区都不是本轮报障对象，改投票方向（取低→取高）会直接
        // 改变 ActZal 结果（4.61 折被滤掉后 7.75 折胜出 → 洗顶灯穿板），属未验证回归，
        // 违反「禁止先改后验」。留待下一轮单独立项、逐区 play 验收后再动。
        float ceilY = 0f;
        int bestVotes = 0;
        foreach (var kv in ceilVotes)
        {
            // 平票时取较低档（防高顶补丁把整区灯抬进主顶板里）
            if (kv.Value > bestVotes || (kv.Value == bestVotes && ceilValue[kv.Key] < ceilY))
            { bestVotes = kv.Value; ceilY = ceilValue[kv.Key]; }
        }
        bool haveCeil = ceilY > floorY + BaseLightHeight + 0.4f;

        // ── ③ 建灯（全区同一高度；P39e 众数吊顶高，全部保留）──────────
        // P39h：矮层高分区的 range 自适应放大（见 WashShortCeilSpan 注释块）。
        // 判据是**净高** ceilY - floorY（不是 washY - floorY：后者被楼板高度吃掉，
        // 2F 房间会误判成矮，首版即踩此坑）。净高只为 0 或正数，两档实测 3.34 / 6.75+，
        // 阈值 4.0 落在空档中间。再用 IsCorridor 排除走廊（走廊有独立配光档、且已验收）。
        float zoneWashY = haveCeil ? ceilY - WashDropBelowCeiling
                                   : floorY + BaseLightHeight + 2.2f;   // 全 miss 兜底（旧默认）
        float netClearHeight = haveCeil ? (ceilY - floorY)
                                        : (zoneWashY - floorY + WashDropBelowCeiling);
        bool shortCeil = !z.IsCorridor && netClearHeight < WashShortCeilSpan;
        float zoneWashRange = shortCeil ? WashRange * WashShortCeilRangeScale : WashRange;
        for (int i = 0; i < washCount; i++)
        {
            float washY = zoneWashY;
            if (i == 0)
                Debug.Log("[IndoorFill][P39e] 洗顶灯众数吊顶高：「" + z.Zone + "」ceilY=" + ceilY.ToString("0.00") +
                          "（" + bestVotes + "/" + washCount + " 票" +
                          (maxHit > ceilY + WashCeilVoteQuantum ? "，舍弃局部高顶 " + maxHit.ToString("0.00") : "") +
                          "）→ 洗顶灯 y=" + washY.ToString("0.00") + "，净高 " + netClearHeight.ToString("0.00") +
                          "m，保留 " + washCount + "/" + washCount + " 盏" +
                          "，range=" + zoneWashRange.ToString("0.00") + (shortCeil ? "（P39h 矮层高放大）" : "") +
                          (washOmni ? "，P42 改全向补梁柱 I=" + WashOmniIntensityBig.ToString("0.00") : ""));

            z.BaseLights[idx] = CreateWashLight("Wash_" + z.Zone + "_" + i.ToString("00"),
                                                new Vector3(xs[i], washY, zs[i]), zoneWashRange, washOmni);
            z.BaseLightLevels[idx++] = washOmni ? WashOmniIntensityBig
                                                : (bigRoom ? WashIntensityBig : WashIntensity);
        }
    }

    /// <summary>
    /// P39b：无碰撞体吊顶的估顶兜底——在 (x,zz) 上方、[floorY+1, floorY+14] 高度窗内找
    /// 水平覆盖该点、面积 ≥9㎡ 的**水平薄板**下表面（bounds.min.y），近似可见吊顶高度。
    /// 只在建灯期每分区 miss 时调用一次（FindObjectsOfType 成本可接受）；面积门槛滤掉
    /// 横幅/灯具等装饰小件，避免把吊顶估低。
    /// ⚠️【P39e 修正】原实现只看「面积 ≥9㎡ + 高度在窗内 + 取最低 minY」，漏了「必须是水平面」，
    /// 在礼堂翻车：礼堂吊顶 ZalTop 没有碰撞体（射线全 MISS，实测 10 个布点 10 个 miss），
    /// 只能走本兜底；而比吊顶更低的 `Glass6`（一扇 12.1×18.7m 的**玻璃窗**，bounds.min.y=4.61）
    /// 也满足「面积够大 + 高度在窗内」，且 minY 比 ZalTop(7.75) 低 3.14m → 被当成吊顶。
    /// 后果：ceilY=4.61 低到 floorY(3.56)+1.9 门槛之下 → haveCeil=false → 全部洗顶灯落到
    /// `floorY+BaseLightHeight+2.2 = 7.26` 兜底，离真实顶板 ZalTop(7.75) 只有 0.49m。
    /// 灯越贴顶，150° 锥在顶面上的照亮半径越小（0.49×tan75°=1.83m），而灯间距 5.4m →
    /// 顶面只剩一串**孤立椭圆亮斑**、斑间大片暗区（用户截图即此景，与参考图的均匀受光不符）。
    /// 判据补一条：**竖直面排除**。用 bounds.size.y 的绝对厚度判定（吊顶板必然是薄板）——
    /// 实测礼堂三个候选：ZalTop 真顶板 size.y=0.00、Box002 高顶盒 0.14、Glass6 玻璃窗 2.70。
    /// 取「size.y ≤ 0.6m 才认」可干净排除玻璃窗、保住两类真顶板，见 CeilSlabMaxThickness。
    /// 修正后礼堂 ceilY=7.75 → washY=6.85（正确落距 0.9m），顶面孤立光斑连成片：
    /// 同机位实测 CV 0.263→0.186（仅修高度即 −29%），亮度 0.491→0.559，削顶仍 0.0%。
    /// </summary>
    private float EstimateCeilYAbove(float x, float zz, float floorY)
    {
        float best = float.MaxValue;
        Renderer[] all = FindObjectsOfType<Renderer>();
        for (int i = 0; i < all.Length; i++)
        {
            Bounds b = all[i].bounds;
            if (x < b.min.x || x > b.max.x || zz < b.min.z || zz > b.max.z) continue;
            if (b.min.y < floorY + 1.0f || b.min.y > floorY + 14f) continue;
            if (b.size.x * b.size.z < 9f) continue;
            // P39e：吊顶板必然是**薄板**，竖直面（窗/帘/墙）不是。
            // 用 bounds.size.y 的绝对厚度判定（单位：米，与场景同尺度）：
            //   礼堂实测三个候选 —— Glass6 玻璃窗 size.y=2.70 ✗、Box002 高顶盒 0.14 ✓、ZalTop 真顶板 0.00 ✓。
            // 想过但更差的写法：用 size.y ÷ 水平跨度 的比值 —— Glass6 比值 0.144 恰好卡在 0.15 门槛内侧，
            // 拦不住；比值受面积尺度影响不稳定，绝对厚度更直接（吊顶/隔层板物理厚度都在 0.6m 以内）。
            if (b.size.y > CeilSlabMaxThickness) continue;
            if (b.min.y < best) best = b.min.y;
        }
        return best < float.MaxValue ? best : 0f;
    }

    /// <summary>
    /// P39 洗顶灯建灯口。与补光 Spot（朝下）互为镜像：Euler(-90,0,0) 把 Spot 默认的 +Z
    /// 转成 +Y（朝上）。ForcePixel 是本层的立身之本——反弹灯的教训（Auto 让位→顶点光→
    /// 吊顶上不可见）不允许重演；候选总数已由 range/数量收紧压进逐像素名额（48）。
    /// </summary>
    private Light CreateWashLight(string name, Vector3 pos, float range)
        {
            return CreateWashLight(name, pos, range, false);
        }

    /// <summary>P42：omnidirectional=true 时建成全向 Point（不用 Spot），用于补充
    /// 梁侧面/柱身这类「法线水平、且位于灯管高度之上」的面——竖直锥在几何上永远照不到。
    /// 只换光的形状，盏数/位置/父子关系/驱动链路全部不变，因此不加灯、不抢额外名额。
    /// </summary>
    private Light CreateWashLight(string name, Vector3 pos, float range, bool omnidirectional)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(transform, false);      // 与补光/底光同根：本根死则全死，OnDestroy 一条清干净
        go.transform.localPosition = transform.InverseTransformPoint(pos);
        // 朝上（CreateLight 的 90° 是朝下）。Point 光忽略旋转，但保留以便随时回退成 Spot。
        go.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);
        Light l = go.AddComponent<Light>();
        l.type = omnidirectional ? LightType.Point : LightType.Spot;
        l.color = FillColor;                           // 与直射光同族，避免顶/墙双色割裂
        l.range = range;
        if (!omnidirectional) l.spotAngle = WashAngle;
        l.intensity = 0f;
        l.shadows = LightShadows.None;                 // 与全层同口径：阴影贵，且天花板在锥体内侧无遮挡关系
        l.renderMode = LightRenderMode.ForcePixel;     // 确定性像素光（反弹灯废止的第一条证据）
        l.enabled = false;
        return l;
    }

    /// <summary>
    /// 底光建灯口。**不复用 CreateLight()**：那个方法已按 P36 方案 B 定型为 Spot 朝下 150°
    /// （a00cf88，不许回改），底光要的是全向 Point——为守住「只增层不改层」另开一口。
    /// renderMode 默认 **Auto**，但 BuildBaseLights 建灯后对**全部**分区覆写为 ForcePixel。
    /// ⚠️【P39e 全分区例外】原设计是「走廊/大房间名额有空余，底光保住逐像素、更平滑，Auto 让位
    /// 可省名额」。P39e 实测这个前提不成立，且漏掉体育馆造成复发：
    ///   ① 名额从来不够：plc 实测 48（QualitySettings.SetQualityLevel(5) 把代码设的 32 覆盖回 48），
    ///      而 Wash 83 盏 + Fill 38 盏先抢，Base 排在后面时而被挤掉时而又拿回；
    ///   ② 被挤掉不是「略暗」而是**消失**：走廊/体育馆的大面都是合并巨网格（体育馆内壳 Box001
    ///      地板+墙+顶同属一个 mesh，画面占比 69.9%），顶点极稀，顶点光插值不出亮度；
    ///   ③ 墙面唯一光源就是全向 Base —— Fill/Wash 是 150° 竖直锥（半角 75°），与水平墙面法线
    ///      夹角 ≈90°，几何上永远在锥外。
    /// 所以「让位」在这里等于「把墙关掉」。旧注释「顶点插值的抹平恰好是目标本身」是错的：
    /// 墙面不是低频垫底，而是上/下部唯一的光源分界，被抹平就成了硬阴影带 / 整面死黑。
    /// 全程实测数据见 BuildBaseLights 内的 P39e 注释。
    /// </summary>
    private Light CreateBaseLight(string name, Vector3 pos, float range)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(transform, false);      // 与补光点同根：本根死则全死，OnDestroy 一条清干净
        go.transform.localPosition = transform.InverseTransformPoint(pos);
        Light l = go.AddComponent<Light>();
        l.type = LightType.Point;                      // 全向=无锥边（理由见 BuildBaseLights 注释）
        l.color = FillColor;                           // 底光是「灯的反弹」，色温必须与直射光同族，避免双色割裂
        l.range = range;
        l.intensity = 0f;
        l.shadows = LightShadows.None;                 // 与补光同理由：阴影贵，且阴影本身就是新的硬边
        l.renderMode = LightRenderMode.Auto;           // 让位策略见方法注释
        l.enabled = false;
        return l;
    }

    /// <summary>
    /// 首选路径：把补光点放到**真实灯管的正下方**。
    /// 灯位判据与 TwinDevicePresenter.CollectLampGroup 同口径（那套判据对全部 111 盏灯验证过）：
    ///   ① 与代表灯管同层（y 差 ≤1.6m：1F 灯 3.34 / 2F 灯 6.90，走廊两层 XZ 重合只差高度）；
    ///   ② 灯位落在本分区矩形内（TwinRoomBounds）。
    /// 路径：灯具锚点 → VisualTarget → 体积最大 Renderer（代表灯管）→ 其父节点 = 灯具容器
    /// → 容器内逐个子物体过上面两条判据。
    /// 【为什么补光点比灯管低 1.14m 而不是放在灯管中心】全向点光源放在离天花板只有 0.16m
    /// 的管位上，会把管顶烧出白斑——真实荧光灯往上几乎不发光（灯罩兜着）。下移到半高后：
    /// 地面光池仍然对准灯管（XZ 不变），天花板不吃第二遍光。
    /// </summary>
    private bool BuildTubeFills(ZoneFill z)
    {
        if (z.LampAnchor == null) return false;
        GameObject target = z.LampAnchor.VisualTarget;
        if (target == null) return false;

        Renderer[] rs = target.GetComponentsInChildren<Renderer>(true);
        if (rs == null || rs.Length == 0) return false;
        Renderer main = LargestRendererOf(rs);
        if (main == null) return false;

        Transform container = main.transform.parent;
        if (container == null || container.childCount <= 1) return false;

        float refY = main.transform.position.y;
        int cap = z.IsCorridor ? MaxTubeFillsCorridor : MaxTubeFillsRoom;
        bool bigRoom = (z.MaxX - z.MinX) * (z.MaxZ - z.MinZ) > 200f;
        float range = z.IsCorridor ? CorridorTubeRange : (bigRoom ? HallTubeRange : RoomTubeRange);
        float intensity = z.IsCorridor ? CorridorTubeIntensity : (bigRoom ? HallTubeIntensity : RoomTubeIntensity);

        // 走廊扫描外扩（CorridorScanExpand）：楼梯间/门厅的灯贴着走廊端头且没有登记分区，
        // 不扩扫的话楼梯间永远没光。
        float ex = z.IsCorridor ? CorridorScanExpand : 0f;
        float minX = z.MinX - ex, maxX = z.MaxX + ex, minZ = z.MinZ - ex, maxZ = z.MaxZ + ex;

        List<Vector3> tubes = new List<Vector3>(8);
        for (int i = 0; i < container.childCount; i++)
        {
            Transform c = container.GetChild(i);
            if (c == null) continue;
            if (Mathf.Abs(c.position.y - refY) > 1.6f) continue;   // 判据①：同层
            if (c.position.x < minX || c.position.x > maxX) continue;  // 判据②：在分区内（走廊含端头外扩）
            if (c.position.z < minZ || c.position.z > maxZ) continue;

            Renderer body = LargestRendererOf(c.GetComponentsInChildren<Renderer>(true));
            if (body == null) continue;
            Vector3 p = body.bounds.center;
            tubes.Add(new Vector3(p.x, refY - TubeFillDrop, p.z));
        }

        // P39 等距取样：候选管数超过封顶时，不再按兄弟顺序「取前 N 个」（走廊 20 管封顶 12
        // 会只点亮半条走廊、另半截黑），改为沿候选序列等距跨步取样，选中的管位在全段均匀铺开。
        // rooms 恰好 6~8 管不触发此分支，行为不变。
        if (tubes.Count > cap)
        {
            List<Vector3> picked = new List<Vector3>(cap);
            for (int i = 0; i < cap; i++)
                picked.Add(tubes[(i * tubes.Count) / cap]);
            tubes = picked;
        }

        if (tubes.Count == 0) return false;

        z.Lights = new Light[tubes.Count];
        z.Eligible = new bool[tubes.Count];
        z.Fade = new float[tubes.Count];
        for (int i = 0; i < tubes.Count; i++)
        {
            z.Lights[i] = CreateLight("Fill_" + z.Zone + "_" + i.ToString("00"), tubes[i], range);
        }
        z.BaseIntensity = intensity;
        return true;
    }

    /// <summary>取一组 Renderer 里体积最大的（灯管是灯具预制体的主体）——与 TwinDevicePresenter.LargestRenderer 同法。</summary>
    private static Renderer LargestRendererOf(Renderer[] rs)
    {
        if (rs == null) return null;
        Renderer best = null;
        float bestSize = -1f;
        for (int i = 0; i < rs.Length; i++)
        {
            Renderer r = rs[i];
            if (r == null) continue;
            float size = r.bounds.size.sqrMagnitude;
            if (size > bestSize) { bestSize = size; best = r; }
        }
        return best;
    }

    private Light CreateLight(string name, Vector3 pos, float range)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(transform, false);      // 挂在本根下：本根死则全死，OnDestroy 一条就清干净
        go.transform.localPosition = transform.InverseTransformPoint(pos);
        // 【P36 方案B】Spot 锥体必须显式朝下：Unity 的 Spot 默认朝 +Z（水平），
        // Euler(90,0,0) 绕 X 轴转 90° 恰把 +Z 转成 -Y。与上一行 localPosition 同一坐标口径
        // （本根由 new GameObject 创建、旋转恒为单位四元数，local 即世界）：
        // 天花板在锥体外辐照归零，地面仍在锥体内 NdotL=1，地面照度零变化。
        go.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
        Light l = go.AddComponent<Light>();
        l.type = LightType.Spot;                       // Point→Spot（P36 交接文档 §4.1）：真实荧光灯有灯罩往上几乎不发光（446 行注释同物理事实），
                                                       // 全向 Point 直射天花板 1.39 倍辐照必硬削顶；内置管线仍只有 Point/Spot 有实时光（Area 仅烘焙，D13 后为零）
        l.color = FillColor;
        l.range = range;
        // 【P36 §4.2】150°：初版 140° 经主理人二次验收判「墙面有明暗暗带（像影子）」，
        // 按交接文档预案全局统一上调 140→150（裁决：唯一预案，不加路径分支）。
        // 地面覆盖半径 2.20×tan75°≈8.2m；墙面受光高度 x 米外 = 2.2−x/3.73（1.5m 外墙可照到约 1.8m 高）。
        // 在 Unity Spot 合法域（上限 179°）内不触发钳制；天花板（90° 方向）仍在锥体外，过曝修复不受影响。
        l.spotAngle = 150f;
        l.intensity = 0f;
        l.shadows = LightShadows.None;                 // 阴影贵且走廊灯管已有 glow 定形状；补光层只补"面"
        // 【为什么 ForcePixel】与 TwinDevicePresenter.BuildGlows 同一条血泪教训（P17）：
        // Auto 的小光源会在重要性排序里输给近处大光源，被降级成顶点光，
        // 打在两三角面的大平面上插值不出亮度 —— 症状就是"参数全对、画面不亮"。
        l.renderMode = LightRenderMode.ForcePixel;
        l.enabled = false;
        return l;
    }

    private int FindZone(string zone)
    {
        for (int i = 0; i < _zones.Count; i++)
            if (string.Equals(_zones[i].Zone, zone, StringComparison.Ordinal)) return i;
        return -1;
    }

    // ---------------------------------------------------------------- 每帧

    private void Update()
    {
        // ── 建灯就绪检查（建过或确认失败后不再进，每帧成本 = 一次 Count 读取）──
        if (_zones.Count == 0 && !_buildFailedFinal)
        {
            BuildZonesWhenReady();
            if (_zones.Count == 0) return;   // Registry 还没就绪，下一帧再看
        }

        float dt = Time.deltaTime;
        // 昼夜倍率平滑走 1.8s（DayLerpSeconds），与天气系统的 2.4s 过渡并行不打架：
        // 它只写 RenderSettings/后处理，我们只写灯，两边没有任何共享写入点。
        _dayScale = Mathf.MoveTowards(_dayScale, TargetDayScale(), dt / DayLerpSeconds);

        // P41c：夜档降档总闸。用 timeOfDay 精确判「真夜档」，不用 _dayScale 的数值——
        // P40 把白天按天气拆成多档（0.22~0.58）后，Dusk 是 1.0 与 Night 同值，
        // 拿倍率判会把黄昏一起压暗（黄昏是已验收基线，不许动）。
        SchoolTwin.Weather.EnvironmentState envDim = SchoolTwin.Weather.EnvironmentState.Instance;
        _nightDimOn = envDim != null && envDim.timeOfDay == SchoolTwin.Weather.TimeOfDay.Night;

        _pollTimer -= dt;
        if (_pollTimer <= 0f) { _pollTimer = PollInterval; Poll(); }

        Apply(dt);
    }

    /// <summary>外部开关切换后立即刷一帧，不等下一个 Update。</summary>
    internal void RefreshNow() { Poll(); Apply(0f); }

    /// <summary>
    /// 目标昼夜倍率。**自读 EnvironmentState 档位，不订阅 OnChanged** ——
    /// 理由见 P35 §Q2：OnChanged 只在切换瞬间发一次，订阅方要自己复刻 2.4s 过渡插值；
    /// 自读 + 自平滑三行搞定，且天气系统将来怎么改过渡曲线都不影响这边。
    /// Instance 缺失（理论上 Day 场景自举必挂）时按黄昏档兜底：宁可靠中，不靠两端。
    /// </summary>
    private float TargetDayScale()
    {
        EnvironmentState env = EnvironmentState.Instance;
        if (env == null) return DayScaleDusk;
        switch (env.timeOfDay)
        {
            case TimeOfDay.Night: return DayScaleNight;
            case TimeOfDay.Dusk: return DayScaleDusk;
            // P40：白天不再是**唯一一档**，按天气补 —— 室内灯是开着的，
            // 阴雨天室外照度塌到晴天 1/5，房子里的灯必须把这块缺口填回来，
            // 否则就是主理人截图里"灯亮着但整片发灰"的观感。
            default: return DayScaleForWeather(env.weather);
        }
    }

    /// <summary>
    /// 白天档按天气给室内补光量。晴天 → DayScaleDay(0.05，低于闸门 ⇒ 关灯，已验收基线不动)。
    ///
    /// 【为什么不改太阳/环境光】那三组倍率归 EnvironmentState 独家管理，
    /// 一动室外跟着亮，直接撞「室外不能过曝」红线（P35 的教训）。
    /// 室内补光是本层自己的灯，动它只影响室内，责任边界干净。
    /// </summary>
    private static float DayScaleForWeather(SchoolTwin.Weather.Weather w)
    {
        switch (w)
        {
            case SchoolTwin.Weather.Weather.Cloudy:   return DayScaleCloudy;
            case SchoolTwin.Weather.Weather.Overcast: return DayScaleOvercast;
            case SchoolTwin.Weather.Weather.Rain:     return DayScaleRain;
            case SchoolTwin.Weather.Weather.Storm:    return DayScaleStorm;
            case SchoolTwin.Weather.Weather.Snow:     return DayScaleSnow;
            case SchoolTwin.Weather.Weather.Fog:      return DayScaleFog;
            default: return DayScaleDay;   // Clear 及未知：保持已验收的白天基线
        }
    }

    /// <summary>低频（0.2s）做的事：解析相机/角色、算每分区灯具开关目标、算每个补光点的 Eligible。</summary>
    private void Poll()
    {
        // 相机解析复用 EnvironmentState.FindActiveCamera：它排掉了小地图 RT 相机，
        // 也修过"Camera.main 会拿到 enabled=false 的孪生相机"的坑，不必再造一把尺子。
        if (_cam == null || !_cam.isActiveAndEnabled)
        {
            _camSearchTimer -= PollInterval;
            if (_camSearchTimer <= 0f) { _camSearchTimer = 1f; _cam = EnvironmentState.FindActiveCamera(); }
        }

        // 角色解析：生效锚点以**角色**为主、相机为辅。
        // 第三人称转视角时相机绕角色转圈，拿相机当锚点会让灯的生效圈跟着进进出出。
        if (_player == null)
        {
            _playerSearchTimer -= PollInterval;
            if (_playerSearchTimer <= 0f) { _playerSearchTimer = 1f; _player = FindPlayer(); }
        }

        // ── 旧光点接管（每轮幂等扫一遍；表现层是懒建灯，晚建的这里也能逮到）──
        if (TakeOverGlows) SuppressZoneGlows();

        // P37b：地面高光抑制随昼夜档翻转（与灯同一闸：总开关 × EnableGate；白天档还原原值=零变化红线）。
        // 放在相机/角色早退之前——它不依赖相机与角色。
        UpdateGlossClamp(IndoorFillLight.Enabled && _dayScale > EnableGate);

        // P37f：现场取证倒计时（夜晚档就绪 2 秒后做一次；白天档不计时不取证）
        if (!_diagDone && _zones.Count > 0 && IndoorFillLight.Enabled && _dayScale > EnableGate)
        {
            if (_diagCountdown < 0f) _diagCountdown = 2f;
            _diagCountdown -= PollInterval;
            if (_diagCountdown <= 0f) { _diagDone = true; RunPlacementDiagnostic(); }
        }

        // P37h：自动截图（取证完成后 2s 间隔连拍 3 张游戏视图 → Docs/QA/evidence/P37_auto/，
        // 执行方直接读盘；路径为项目相对路径，编辑器下落在工程根；运行时态不落资产）
        if (_diagDone && _autoShot < 3)
        {
            _shotTimer -= PollInterval;
            if (_shotTimer <= 0f)
            {
                _shotTimer = 2f;
                string dir = "Docs/QA/evidence/P37_auto";
                try { System.IO.Directory.CreateDirectory(dir); } catch { }
                ScreenCapture.CaptureScreenshot(dir + "/auto_" + _autoShot + "_f" + Time.frameCount + ".png");
                _autoShot++;
                Debug.Log("[IndoorFill][诊断] 自动截图 " + _autoShot + "/3（游戏视图）→ " + dir + "/auto_" + (_autoShot - 1) + "_f*.png");
            }
        }

        // 【P39 已移除】P37j 贴顶爆破试验：使命已完成（证明了「灯→可见顶板」通路是通的、
        // 且必然伴随顶部过曝白带——这份证据直接支撑了反弹灯废止决策，见 BaseBounceIntensity
        // 注释与 Docs/P39 文档）。其引用的 BaseCeil_* 反弹灯已废止，试验无对象可炸，整块移除。

        // ── 灯具开关目标 ──
        // 与 TwinDevicePresenter.SyncLamp 完全同口径：读**回读值 Power**，再看回路通断。
        // 读回读值不读命令状态，是表现层铁律（命令是意图、回读是事实）。
        for (int i = 0; i < _zones.Count; i++)
        {
            ZoneFill z = _zones[i];
            if (z.LampAnchor == null) { z.LampTarget = 1f; continue; }
            TwinValue v;
            bool hasPower = z.LampAnchor.TryGetValue("Power", out v) && v.Value > 0.5f;
            z.LampTarget = (hasPower && TwinCircuit.IsPowered(z.LampAnchor.TwinId)) ? 1f : 0f;
        }

        // ── 生效判定：只按「同层」──
        if (_cam == null && _player == null) return;
        // P39b：锚点跨层跟随——观景/自由相机看到另一楼层时（与角色层差 > SameStoreyMaxDy），
        // 生效层以相机为准（用户在看哪层，哪层就亮）；相机与角色同层时仍锚角色，
        // 第三人称绕角色转圈不改变层归属，原语义不变。修"观景看 1F 走廊全黑"。
        Vector3 eyePos;
        if (_cam != null && _player != null &&
            Mathf.Abs(_cam.transform.position.y - (_player.position.y + EyeHeight)) > SameStoreyMaxDy)
        {
            eyePos = _cam.transform.position;
        }
        else
        {
            eyePos = _player != null ? _player.position + Vector3.up * EyeHeight
                                     : (_cam != null ? _cam.transform.position : Vector3.zero);
        }

        for (int i = 0; i < _zones.Count; i++)
        {
            MarkEligible(_zones[i], eyePos);
        }
    }

    /// <summary>角色根节点。TwinPlayerController 由场景提供，找不到时由调用方 1s 重试。</summary>
    private static Transform FindPlayer()
    {
        TwinPlayerController pc = FindObjectOfType<TwinPlayerController>();
        return pc != null ? pc.transform : null;
    }

    /// <summary>
    /// 生效判定：只按同层（P39b 起：锚点与**分区地面**比，窗口 [FloorY-0.5, FloorY+EyeAboveFloorMax]）。
    /// 真实灯具只由"有没有电"决定亮灭，与人在不在附近无关——本层不做任何"靠近才亮"。
    /// 楼层维度保留：1F/2F 的 XZ 完全重叠，不按层分会把楼下的光透到楼上（无阴影直射）；
    /// 跨层时灯切换是真实的（你在下楼）。旧规则拿灯的安装高度比眼高，高大厅房（礼堂/体育馆）
    /// 的洗顶灯贴 7~9m 天花板、与厅内人眼差 2.1m 恒超 2m 阈值，永远判异层——已修。
    /// </summary>
    private void MarkEligible(ZoneFill z, Vector3 eye)
    {
        bool sameFloor = eye.y >= z.FloorY - 0.5f && eye.y <= z.FloorY + EyeAboveFloorMax;
        for (int i = 0; i < z.Lights.Length; i++)
        {
            z.Eligible[i] = sameFloor && z.Lights[i] != null;
        }

        // 底光/洗顶与补光同一条同层规则：1F 的「底光」不许透过楼板照到 2F（无阴影直射，P35 同款教训）。
        if (z.BaseLights != null)
        {
            for (int i = 0; i < z.BaseLights.Length; i++)
            {
                z.BaseEligible[i] = sameFloor && z.BaseLights[i] != null;
            }
        }
    }

    /// <summary>
    /// P37b：一次性收集「地面高光抑制」名单。全场景 Renderer 扫一遍（只读，仅本次建灯期），
    /// 按 P35 v5 轮实测名单精确命中 + 名字含 Floor 且水平面积 ≥20㎡ 兜底（捞教室等不在
    /// 名单里的地板），并记录每个面当前的 _Glossiness 原值——白天档还原靠它兜住零变化红线。
    /// </summary>
    private void CollectGlossTargets()
    {
        Renderer[] all = FindObjectsOfType<Renderer>();
        for (int i = 0; i < all.Length; i++)
        {
            Renderer r = all[i];
            if (r == null || r.sharedMaterial == null) continue;
            if (!r.sharedMaterial.HasProperty(GlossPropertyId)) continue;

            bool hit = false;
            string n = r.name;
            for (int k = 0; k < GlossClampNames.Length; k++)
            {
                if (string.Equals(n, GlossClampNames[k], StringComparison.Ordinal)) { hit = true; break; }
            }
            // Floor + Top 双关键字兜底：教室/机房的天花板（*Top 类命名）镜面噪点亮斑与地板白条同源；
            // 面积阈值把课桌桌面等小物件挡在外面。
            if (!hit && (n.IndexOf("Floor", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         n.IndexOf("Top", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                Bounds b = r.bounds;
                if (b.size.x * b.size.z > GlossMinFloorArea) hit = true;
            }
            if (hit)
            {
                _glossOriginal[r] = r.sharedMaterial.GetFloat(GlossPropertyId);
                // P41：判「是不是地面」。两种形态都要覆盖 ——
                //   ① 薄板地面（Border* / KitchenFloor / ZalStairs）：法线朝上、size.y 近零；
                //   ② **合并网格的房间外壳**（Zal 礼堂 / Floor2 走廊 / Kitchen 餐厅 / Corridor）：
                //      这些是 "Combined Mesh"，一个 renderer 里装了整个房间的**地板+墙+顶**，
                //      包围盒是房间体积（Zal 高 5.21m、Floor2 高 4.36m），几何上永远判不成"薄板"。
                //      ⚠️ 首版只写了几何薄板判据，结果 Zal/Floor2/Kitchen 全被打入非地面、
                //      gloss 一直是 0.500 —— 礼堂地面的白带因此一点没消（老师反馈的核心）。
                //      判据补一条：面积 ≥100㎡ 且高度 ≤6m 的封闭外壳，其**底面就是地板**。
                //      6m 上限把 Box001(8.8m)、BuildingBottom(58m) 这类建筑体量排除在外。
                Bounds gb = r.bounds;
                float area = gb.size.x * gb.size.z;
                bool thinSlab = gb.size.y <= Mathf.Max(0.35f, Mathf.Min(gb.size.x, gb.size.z) * 0.15f)
                                && area >= GlossMinFloorArea;
                bool roomShell = area >= 100f && gb.size.y <= 6.0f;
                if (thinSlab || roomShell) _glossFloors.Add(r);
            }
            else
            {
                // P41b：不在 31 面名单里、但材质是 Wood3/MetallChrome* 的**家具面**（餐桌桌面、
                // 打餐台面）。它们面积小、名字里也没有 Floor/Top，上面两条兜底都捞不到，
                // 却是夜景里最刺眼的白斑源（餐厅前景桌面 clip 4.46%、P95 0.990）。
                // 只按材质名精确命中，不按 gloss 阈值泛扫 —— 全场 gloss≥0.75 的小物件有 500+ 个
                // （椅腿金属 166、椅面皮革 86、暖气片 60），泛扫会把它们一起变哑光，属于越界改动。
                Material sm = r.sharedMaterial;
                // 材质实例名可能带 " (Instance)" 后缀，先截掉再比
                string smName = sm.name;
                int paren = smName.IndexOf(" (Instance)", StringComparison.Ordinal);
                if (paren >= 0) smName = smName.Substring(0, paren);
                for (int k = 0; k < GlossDeskMaterials.Length; k++)
                {
                    if (!string.Equals(smName, GlossDeskMaterials[k], StringComparison.Ordinal)) continue;
                    _glossOriginal[r] = sm.GetFloat(GlossPropertyId);
                    _glossDesks.Add(r);
                    break;
                }
            }
        }
        if (_glossOriginal.Count > 0)
            Debug.Log("[IndoorFill][P41] 高光抑制名单就绪：命中 " + _glossOriginal.Count +
                      " 个面（地面 " + _glossFloors.Count + " 个 → " + GlossFloorTarget +
                      "，桌面台面 " + _glossDesks.Count + " 个 → " + GlossDeskTarget +
                      "，其余顶板类 ≤" + GlossClampValue + "）；白天自动还原原值");
    }

    /// <summary>昼夜档翻转时钳制/还原（Poll 每 0.2s 调用，状态不翻即早退，常驻零开销）。</summary>
    private void UpdateGlossClamp(bool on)
    {
        if (on == _glossClamped) return;
        _glossClamped = on;
        MaterialPropertyBlock mpb = new MaterialPropertyBlock();   // 仅档位翻转时分配，量级可忽略
        foreach (KeyValuePair<Renderer, float> kv in _glossOriginal)
        {
            Renderer r = kv.Key;
            if (r == null) continue;
            // P41：地面单独一档 —— 老师要求"不要反光、粗糙的、暗一点，不容易曝光"。
            // 地面（法线朝上的水平大面）一律压到 GlossFloorTarget(0.18)：高光从"收得很紧的亮条"
            // 摊成"大面积弱晕"，观感即粗糙地面。
            // 非地面（顶板等）仍走 P39h 的「只降不升」：抛光面 ≥0.80 → 0.30，其余 min(原值, 0.62)。
            // 两个分支都只降不升，不会把低反光面往上抬。
            float clamped;
            if (_glossFloors.Contains(r))
            {
                // 仍守「只降不升」：原值已经比目标更低的面（如 Box002=0.132）保持原值不动，
                // 否则就是把本来更粗糙的面往光滑推，与老师的要求背道而驰。
                clamped = Mathf.Min(kv.Value, GlossFloorTarget);
            }
            else if (_glossDesks.Contains(r))
            {
                // 桌面/台面：柔性一档（保住家具质感），同样只降不升
                clamped = Mathf.Min(kv.Value, GlossDeskTarget);
            }
            else
            {
                clamped = kv.Value >= GlossStrongMin ? GlossClampStrong
                                                     : Mathf.Min(kv.Value, GlossClampValue);
            }
            r.GetPropertyBlock(mpb);                              // 先读：保留别人的属性（如状态染色）
            mpb.SetFloat(GlossPropertyId, on ? clamped : kv.Value);
            r.SetPropertyBlock(mpb);
        }
        Debug.Log("[IndoorFill][P41] 高光抑制" + (on ? "已生效：地面→" + GlossFloorTarget +
                                                      "，桌面台面→" + GlossDeskTarget +
                                                      "，顶板类≤" + GlossClampValue + "（均只降不升）"
                                                    : "已还原（白天档，原值恢复）"));
    }

    // ── P37f 现场取证：把"天花板是什么材质、底灯有没有贴墙、反弹灯到底亮没亮"变成日志直读 ──
    // 两轮理论推演（名额→强度→曝光/反照率）到头了，剩余分歧只能现场数据定案。只读、每局一次。

    /// <summary>取证入口：先报全局逐像素名额实值，再对走廊与教室各取一份现场账。</summary>
    private void RunPlacementDiagnostic()
    {
        Debug.Log("[IndoorFill][诊断] pixelLightCount=" + QualitySettings.pixelLightCount +
                  "（目标 " + IndoorFillLight.PixelLightCountTarget + "）");
        RunZoneDiagnostic("Corridor2");
        RunZoneDiagnostic("Class2-1");
    }

    /// <summary>单分区取证：分区矩形 → 每盏底灯的真实位置/开关/强度（查"底灯贴墙"）→
    /// 反弹灯正上方以 0.05m 步长密探 0.05~1.25m 找天花板渲染器（按渲染器聚合命中高度带，
    /// 附着色器/反照率/顶点数）——黑顶是缺失几何、深色材质、还是灯没到位，一条日志定案。
    /// 探针取"中间"那盏反弹灯：走廊长达 70m，端头反弹灯代表性不足。</summary>
    private void RunZoneDiagnostic(string zoneName)
    {
        int zi = FindZone(zoneName);
        if (zi < 0) { Debug.Log("[IndoorFill][诊断] 分区「" + zoneName + "」不存在（TwinRoomBounds 未登记？）"); return; }
        ZoneFill z = _zones[zi];
        int baseCount = z.BaseLights != null ? z.BaseLights.Length : 0;
        Debug.Log("[IndoorFill][诊断] 「" + z.Zone + "」矩形 X[" + z.MinX.ToString("0.0") + "," + z.MaxX.ToString("0.0") +
                  "] Z[" + z.MinZ.ToString("0.0") + "," + z.MaxZ.ToString("0.0") + "] 底灯 " + baseCount + " 盏");
        if (z.BaseLights == null) return;

        List<Vector3> bouncePos = new List<Vector3>();
        for (int j = 0; j < z.BaseLights.Length; j++)
        {
            Light l = z.BaseLights[j];
            if (l == null) continue;
            Debug.Log("[IndoorFill][诊断]   " + l.name +
                      " pos=" + l.transform.position.ToString("0.00") +
                      " enabled=" + l.enabled + " intensity=" + l.intensity.ToString("0.00") +
                      " range=" + l.range.ToString("0.0") + " type=" + l.type);
            if (l.name.StartsWith("BaseCeil", StringComparison.Ordinal)) bouncePos.Add(l.transform.position);
        }
        if (bouncePos.Count == 0) { Debug.Log("[IndoorFill][诊断]   该分区没有反弹灯"); return; }
        Vector3 probeOrigin = bouncePos[bouncePos.Count / 2];

        // 天花板探针 v4（P37i）：改成"垂直柱状剖面"——在反弹灯的 XZ 上，从室内 1.2m 到 4.2m
        // 以 0.05m 步长逐点找包含探针的渲染器，按渲染器聚合绝对 y 带。v3 只从反弹灯向上探，
        // 会漏掉"低于反弹灯的吊顶板"（P37i 射线贴顶就是为它）。穹顶（bounds>100m）仍排除。
        // 这份剖面一次看清：吊顶层在哪、楼板在哪、梁在哪、各自反照率与顶点数。
        Renderer[] all = FindObjectsOfType<Renderer>();
        var hitBands = new Dictionary<string, List<float>>();
        var firstHit = new Dictionary<string, Renderer>();
        bool domeNoted = false;
        float profileFloorY = z.FloorY;
        for (int step = 0; step <= 60; step++)
        {
            Vector3 probe = new Vector3(probeOrigin.x, profileFloorY + 1.2f + step * 0.05f, probeOrigin.z);
            for (int i = 0; i < all.Length; i++)
            {
                Renderer r = all[i];
                if (r == null || r.sharedMaterial == null) continue;
                if (!r.bounds.Contains(probe)) continue;
                Vector3 bs = r.bounds.size;
                if (Mathf.Max(bs.x, Mathf.Max(bs.y, bs.z)) > 100f)
                {
                    if (!domeNoted)
                    {
                        Debug.Log("[IndoorFill][诊断]   （每个探针点都在天空穹顶 StarDome 的包围盒内——正常，穹顶不参与灯光，不计入候选）");
                        domeNoted = true;
                    }
                    continue;
                }
                if (!hitBands.ContainsKey(r.name))
                {
                    hitBands[r.name] = new List<float>();
                    firstHit[r.name] = r;
                }
                hitBands[r.name].Add(probe.y);
                // P37j：不 break——上一版每高度只记第一个命中，大包围盒的 Floor2 会把
                // 与它同柱的其它面（吊顶层/梁/独立顶板）全部"挡报"，正是候选缺失的嫌疑。
            }
        }
        if (hitBands.Count == 0)
        {
            Debug.Log("[IndoorFill][诊断]   ★ 柱状剖面 " + (profileFloorY + 1.2f).ToString("0.0") + "~" +
                      (profileFloorY + 4.2f).ToString("0.0") + "m 没有任何建筑面——该柱上没有可照亮的顶板几何" +
                      "（FBX 静态取证已证实走廊无 CorridorTop 节点），黑的是夜空穹顶：灯无解，补顶板属场景层改动，需负责人裁决");
            return;
        }
        int realLogged = 0;
        foreach (KeyValuePair<string, List<float>> kv in hitBands)
        {
            if (realLogged >= 6) { Debug.Log("[IndoorFill][诊断]   ……另有 " + (hitBands.Count - realLogged) + " 个面未展开"); break; }
            Renderer r = firstHit[kv.Key];
            MeshFilter mf = r.GetComponent<MeshFilter>();
            Mesh mesh = mf != null ? mf.sharedMesh : null;
            int colorId = Shader.PropertyToID("_Color");
            string colorDesc = r.sharedMaterial.HasProperty(colorId)
                ? r.sharedMaterial.GetColor(colorId).ToString("0.00")
                : "（无 _Color）";
            float lo = kv.Value[0], hi = kv.Value[kv.Value.Count - 1];
            Debug.Log("[IndoorFill][诊断]   建筑面" + realLogged + " [" + kv.Key + "] shader=" + r.sharedMaterial.shader.name +
                      " _Color=" + colorDesc + " 顶点数=" + (mesh != null ? mesh.vertexCount.ToString() : "非Mesh") +
                      " y带=" + lo.ToString("0.00") + "~" + hi.ToString("0.00") +
                      "（采样 " + kv.Value.Count + " 点）尺寸=" + r.bounds.size.ToString("0.0"));
            realLogged++;
        }
        Debug.Log("[IndoorFill][诊断]   ★ 柱状剖面完成（" + hitBands.Count + " 个面）——对照反弹灯高度即可定案" +
                  "可见顶板是哪层、反弹灯是否在其下方");
    }

    /// <summary>
    /// 关掉走廊/大房间里表现层建的旧光点（TwinLampGlow，每根灯管一个、range≈3m）。
    /// 【为什么敢动别人的运行时对象】主理人两轮"不符合现实逻辑"反馈授权 + 边界如下：
    ///   ① 只关**走廊与大房间**的：教室 6 glow + 6 补光 = 12 恰好在逐像素名额内，
    ///      天花板光圈是已验收的观感，不动；
    ///   ② 灯管自发光仍由表现层负责（ApplyLamp 写材质），断电灭灯逻辑分毫未动；
    ///   ③ ApplyLamp 只写 intensity、从不写 enabled —— 我关 enabled 与它无写入冲突；
    ///   ④ 完全可回滚：SetEnabled(false)/OnDestroy 会把它们逐个恢复（见 RestoreSuppressedGlows）。
    /// 表现层是懒建灯（第一次同步该锚点时才建），所以每轮 Poll 幂等扫一遍，晚建也能逮到。
    /// </summary>
    private void SuppressZoneGlows()
    {
        for (int i = 0; i < _zones.Count; i++)
        {
            ZoneFill z = _zones[i];
            if (!z.TakeOver || z.LampAnchor == null) continue;

            Transform t = z.LampAnchor.transform;
            for (int c = 0; c < t.childCount; c++)
            {
                Transform child = t.GetChild(c);
                if (child == null || !child.name.StartsWith("TwinLampGlow", StringComparison.Ordinal)) continue;
                Light l = child.GetComponent<Light>();
                if (l == null || !l.enabled) continue;
                l.enabled = false;
                _suppressedGlows.Add(l);
                if (!z.LoggedTakeOver)
                {
                    z.LoggedTakeOver = true;
                    Debug.Log("[IndoorFill] 「" + z.Zone + "」旧光点已接管：照明改由本层每管一个补光点独家负责" +
                              "（SetEnabled(false) 可恢复）");
                }
            }
        }
    }

    /// <summary>归还接管期间关掉的旧光点（总开关关闭 / 本层销毁时调用）。</summary>
    internal void RestoreSuppressedGlows()
    {
        for (int i = 0; i < _suppressedGlows.Count; i++)
            if (_suppressedGlows[i] != null) _suppressedGlows[i].enabled = true;
        _suppressedGlows.Clear();
        for (int i = 0; i < _zones.Count; i++) _zones[i].LoggedTakeOver = false;
    }

    /// <summary>每帧写强度。只有这里是热路径，全部是 float 写与 bool 写，零分配。</summary>
    private void Apply(float dt)
    {
        bool master = IndoorFillLight.Enabled;
        // 启用闸：白天档（0.05 < EnableGate）连 enabled 都不给 —— 已验收的白天画面零改动、零成本
        float ds = master && _dayScale > EnableGate ? _dayScale : 0f;
        bool baseMaster = master && IndoorFillLight.BaseLightsEnabled;   // P37 底光 A/B 开关，与总开关/昼夜闸相与

        _activeCount = 0;
        for (int i = 0; i < _zones.Count; i++)
        {
            ZoneFill z = _zones[i];
            z.LampScale = Mathf.MoveTowards(z.LampScale, z.LampTarget, LampSnapPerSecond * dt);
            // 走廊应急底光：灯具档归零时不归零，压到这一档（语义见 P35 §Q3）
            float factor = Mathf.Max(z.LampScale, z.IsCorridor ? EmergencyFloorFactor : 0f);
            // P41c：夜档降档——只在真夜档（Night=1.0）生效，Dusk(0.6) 与白天档不乘，
            // 保证 P40 已验收的白天阴雨补光与黄昏观感零回退。
            float dim = _nightDimOn ? z.NightDim : 1f;

            for (int j = 0; j < z.Lights.Length; j++)
            {
                Light l = z.Lights[j];
                if (l == null) continue;
                // 生效变化走 0.25s 淡入淡出（跨楼层等生效变化时软过渡，不瞬跳）；
                // 首次应用（dt=0）直接到位：挂载那一刻该亮的灯就是亮的。
                float target = (master && ds > 0f && z.Eligible[j]) ? 1f : 0f;
                if (dt <= 0f && target > 0f && z.Fade[j] <= 0f) z.Fade[j] = 1f;
                z.Fade[j] = Mathf.MoveTowards(z.Fade[j], target, EligibilityFadePerSecond * dt);
                float f = z.Fade[j];
                bool active = f > 0.02f;
                if (l.enabled != active) l.enabled = active;
                l.intensity = active ? z.BaseIntensity * ds * factor * f * dim : 0f;
                if (active) _activeCount++;
            }

            // ── P37 底光层：与补光同一套闸门（总开关 × A/B 开关 × 昼夜档 × 同层 × 灯具开关）──
            // 白天档零变化与补光同机制：DayScaleDay(0.05) < EnableGate(0.08) ⇒ ds=0 ⇒ 全部 disabled。
            // P37i：底光层改乘**二值开关**（LampTarget>0.5 ? 1 : 走廊应急档）而不是 LampScale——
            // 三个不同时点的取证实锤 LampScale 在 0.2~0.5 间摆（Mock 亮度波/回路 0.5s 对账所致），
            // 垫底/反弹跟着乘就永远在可见阈值下闪烁；"反弹"跟随的是开关语义（开=全量反弹），
            // 不是调光语义。走廊关灯保留 EmergencyFloorFactor 应急档（P35 §Q3）。
            if (z.BaseLights != null)
            {
                float baseFactor = z.LampTarget > 0.5f ? 1f : (z.IsCorridor ? EmergencyFloorFactor : 0f);
                for (int j = 0; j < z.BaseLights.Length; j++)
                {
                    Light l = z.BaseLights[j];
                    if (l == null) continue;
                    float target = (baseMaster && ds > 0f && baseFactor > 0f && z.BaseEligible[j]) ? 1f : 0f;
                    if (dt <= 0f && target > 0f && z.BaseFade[j] <= 0f) z.BaseFade[j] = 1f;
                    z.BaseFade[j] = Mathf.MoveTowards(z.BaseFade[j], target, EligibilityFadePerSecond * dt);
                    float f = z.BaseFade[j];
                    bool active = f > 0.02f;
                    if (l.enabled != active) l.enabled = active;
                    l.intensity = active ? z.BaseLightLevels[j] * ds * baseFactor * f * dim : 0f;
                    if (active) _activeCount++;
                }
            }
        }
    }

    internal string Describe()
    {
        int total = 0, baseTotal = 0;
        for (int i = 0; i < _zones.Count; i++)
        {
            total += _zones[i].Lights.Length;
            if (_zones[i].BaseLights != null) baseTotal += _zones[i].BaseLights.Length;
        }
        return "[IndoorFill] 分区 " + _zones.Count + " / 补光点 " + total +
               "（走廊 " + (total - RoomFillCount()) + " + 房间 " + RoomFillCount() + "）" +
               " / 底光 " + baseTotal +
               " / 本帧启用 " + _activeCount +
               " / 昼夜倍率 " + _dayScale.ToString("0.00") +
               " / 总开关 " + (IndoorFillLight.Enabled ? "开" : "关") +
               " / 底光开关 " + (IndoorFillLight.BaseLightsEnabled ? "开" : "关");
    }

    private int RoomFillCount()
    {
        int n = 0;
        for (int i = 0; i < _zones.Count; i++) if (!_zones[i].IsCorridor) n += _zones[i].Lights.Length;
        return n;
    }
}

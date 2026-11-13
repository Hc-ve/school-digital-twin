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
// 这是**正确语义**并已编码进本层：补光点归属"灯具档"，强度只随
// 昼夜档（白天≈0 / 黄昏与夜晚同亮）与灯具开关（Power+回路）变化，
// **不读天气**。随天气变化的部分（雨/雪/阴天的环境光与曝光）继续由
// EnvironmentState 独家管理，两边各管各的，互不写对方的参数。
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

    private const float CorridorTubeRange = 9.0f;      // 走廊灯管间距 3~4.2m，9.0 让光池充分衔接
    private const float CorridorTubeIntensity = 1.7f;  // 夜晚档每管强度起点
    private const float RoomTubeRange = 8.0f;          // 教室 6 管，8.0 全屋无缝衔接
    private const float RoomTubeIntensity = 1.7f;      // P37e：1.9→1.7（单管过热洗墙是"过亮"元凶之一），均匀度由底光×2 补
    private const float HallTubeRange = 9.0f;          // 食堂/礼堂单管要喂的面积更大
    private const float HallTubeIntensity = 1.5f;      // P37b：1.3→1.5，礼堂「整体昏暗」定向提亮；Spot 朝下对天花板零影响
    private const int MaxTubeFillsCorridor = 20;       // 走廊 15 管 + 端头楼梯间宽扫；≤20 = 逐像素名额，可全部像素化
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

    /// <summary>同一 storey 判定：锚点（角色眼睛）与补光点的 y 差超过 2m 视为不同层。
    /// 1F/2F 教室在 TwinRoomBounds 里 XZ 完全重叠，没有这条会把 1F 的光"透过楼板"漏到 2F（无阴影直射）。
    /// 锚点改成角色眼睛（脚 +1.6m）后，2F 眼高 ≈5.16 与 1F 管位 2.2 差 2.96，
    /// 旧阈值 3 会漏进楼下一层；收到 2 后上下层彻底分开，本层（差 0.6）不受影响。</summary>
    private const float SameStoreyMaxDy = 2f;

    /// <summary>角色眼睛相对脚底的高度（米）。锚点用"眼睛"而不是"脚"，
    /// 否则站在 2F（脚 y=3.56）时与 1F 补光点（y=2.2）只差 1.36，同层判定会漏。</summary>
    private const float EyeHeight = 1.6f;

    /// <summary>启停淡入淡出速率（每秒）。4/s ⇒ 0↔1 约 0.25s：跨楼层等生效变化时灯光软过渡
    /// 而不是瞬跳。再慢会像灯在喘气。</summary>
    private const float EligibilityFadePerSecond = 4f;

    /// <summary>补光颜色。取 (1, 0.94, 0.82)：与既有灯管 glow (1, 0.95, 0.86)（TwinDevicePresenter:887）
    /// 同族而略暖半档 —— 补光若偏冷，会和灯管形成"双色光"的割裂感；荧光灯管常见色温 3500~4500K，
    /// 这个 RGB 大致落在 3800K 一带，答辩被问到色温时按这个口径回答。</summary>
    private static readonly Color FillColor = new Color(1.0f, 0.94f, 0.82f);

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
    private const float BaseRangeCorridor = 8f;
    private const float BaseRangeRoom = 9f;
    private const float BaseRangeHall = 11f;

    /// <summary>底光强度（夜晚档每盏）。天花板直接核算（光到天花板 d≈2.0m）：
    /// 走廊 0.85×atten(r8,d2.0)≈0.33、房间 0.8×atten(r9,d2.0)≈0.36、礼堂 1.05×atten(r11,d2.0)≈0.58，
    /// 叠加原有天花板亮度后全部低于 0.8（P36 红线 p50<0.80），纹理保持可见。
    /// P37b 联网实拍对照结论（真实走廊/礼堂夜晚全开灯时墙面均匀受光、整体明快）：
    /// 走廊 0.7→0.85 治上墙偏暗与远端昏暗；礼堂单列 1.05 治「整体昏暗」；
    /// 房间 0.8 维持（实拍对照已接近现实教室）。</summary>
    private const float BaseIntensityCorridor = 0.85f;
    private const float BaseIntensityRoom = 0.8f;
    private const float BaseIntensityHall = 1.2f;      // P37c：1.05→1.2，礼堂二档提亮（屏幕亮度 ≈0.17，红线内余量大）

    /// <summary>P37d 天花板反弹灯：离地 3.1m（距顶 ≈0.4m）、每节垫底灯正上方一盏（全部分区）。
    /// 【为什么需要】走廊/礼堂的 glow 接管关闭后天花板夜里零光源；垫底灯在 1.5m 处距顶 ≈2m、
    /// 衰减后够不着——这就是"走廊天花板近黑"的根因（真实房间的天花板亮度来自反弹光，本工程
    /// 零 GI 只能人工垫）。P37c 首轮只给走廊/大房间且强度 0.9，实拍天花板仍近黑（合并大网格上
    /// 降级顶点光 + 强度不足）；本轮扩到全楼并上调强度。亮度核算：1.2×atten(r, d≈0.4)≈1.1 辐照
    /// × 天花板反照率(≈0.25) ≈ 0.27 屏幕亮度，仍远低于 P36 红线；对地面 d≈3.1 衰减到 ≈0.2 辐照可忽略。</summary>
    private const float BaseBounceHeight = 3.1f;
    private const float BaseBounceIntensity = 1.6f;    // P37i：2.0→1.6，贴真实吊顶层后与 glow 时代可见辐照(≈1.44)同档——贴得越近衰减越小，过高有削顶风险

    /// <summary>大房间（>200㎡，方案乙）底光盏数：沿长轴 t=0.25/0.75 两点
    ///（与 BuildRoomFills 的沿长轴均布同口径）。不升单盏强度：点光的天花板/地面贡献
    /// 随强度线性上涨，2 盏只在中间互相叠加、峰值不翻倍——覆盖翻倍、红线不动。</summary>
    private const int BaseLightsBigRoom = 2;

    /// <summary>普通房间底光盏数（P37e：1→2，t=0.25/0.75）：接管扩大后 glow 退场，
    /// 单盏居中的垫底光压不住"半间房亮半间房暗"，两点布设让两面墙都够得着；
    /// 反弹灯随垫底灯同步 ×2，教室天花板亮斑覆盖率同步翻倍。</summary>
    private const int BaseLightsRoom = 2;

    /// <summary>走廊底光沿长轴均布的封顶盏数。走廊分区长达数十米，1 盏盖不住，
    /// 按 CorridorStep(7m) 取整；封顶 6 的另一层原因：走廊地板/大平顶多为合并大网格，
    /// 顶点光每对象上限 4，超出的会被截断——墙是 Border1~17 分段网格，每段只见 1~2 盏，不受影响。</summary>
    private const int MaxBaseLightsCorridor = 6;

    // ── P37b 地面高光抑制（联网实拍对照后定向修复：地板镜面反射条）──
    // 病灶：打蜡地板把灯管镜面反射拉成刺眼亮条（五景截图的共同扣分项；v11 撤销 v5 的
    // 抑制后复发）。真实走廊/教室的地面反射是柔和长晕。做法：夜晚/黄昏档把下列大平面的
    // _Glossiness 钳到 0.62（P35 v5 轮实测值），白天档自动还原原值（白天零变化红线）。
    // MPB 方式（MaterialPropertyBlock）：不碰材质资产、不污染共享材质；先 GetPropertyBlock
    // 再写，不覆盖别人的属性块（TwinStatusColor 的染色对象是暖气片，不在名单内，无交集）。
    private const float GlossClampValue = 0.62f;
    private const float GlossMinFloorArea = 20f;   // Floor 关键字兜底的最小水平面积（㎡），防止捞到小物件

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
    }

    private readonly List<ZoneFill> _zones = new List<ZoneFill>(16);
    private readonly List<Light> _suppressedGlows = new List<Light>(32);
    // P37b 地面高光抑制的运行时状态（名单收集一次，随昼夜档翻转钳制/还原）
    private readonly Dictionary<Renderer, float> _glossOriginal = new Dictionary<Renderer, float>(32);
    private bool _glossClamped;
    private bool _buildFailedFinal;
    // P37f 现场取证（一次性自动运行，只读）：夜晚档就绪 2 秒后打印，主理人零操作，日志回传由执行方自读
    private bool _diagDone;
    private float _diagCountdown = -1f;
    // P37h 自动截图：取证完成后每 2 秒一张、共 3 张，落到 Docs/QA/evidence/P37_auto/（执行方自读，主理人零操作）
    private int _autoShot;
    private float _shotTimer;
    // P37j 贴顶爆破试验：把走廊中段反弹灯的强度档临时抬到 30、范围 12（强制 Eligible），
    // 0.3s/2s 各拍一张，然后自动还原。亮=灯→可见顶板通路已通（此前纯强度不足，调常量收口）；
    // 仍黑=可见顶板与 Floor2 之间另有不反光的遮挡面，转材质排查。试验期天花板短暂过曝属诊断闪烁。
    private int _burstStage;
    private float _burstTimer;
    private int _burstIndex = -1;
    private float _burstOrigLevel;
    private Camera _cam;
    private float _camSearchTimer;
    private Transform _player;
    private float _playerSearchTimer;
    private float _pollTimer;
    private float _dayScale = 1f;
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
        // P37d：反弹灯扩到全部分区——接管扩大后教室 glow 光圈退场，天花板亮度统一由反弹灯供给。
        bool needCeilBounce = true;
        int total = count + (needCeilBounce ? count : 0);
        z.BaseLights = new Light[total];
        z.BaseEligible = new bool[total];
        z.BaseFade = new float[total];
        z.BaseLightLevels = new float[total];
        int idx = 0;
        for (int i = 0; i < count; i++)
        {
            // 沿长轴均布：t=(i+0.5)/count。count=1 时 t=0.5 恰是包围盒中心，count=2 时 0.25/0.75，
            // 与 BuildRoomFills 的布点公式同款——一套公式覆盖三种分区。
            float t = (i + 0.5f) / count;
            float x = alongX ? Mathf.Lerp(z.MinX, z.MaxX, t) : (z.MinX + z.MaxX) * 0.5f;
            float zz = alongX ? (z.MinZ + z.MaxZ) * 0.5f : Mathf.Lerp(z.MinZ, z.MaxZ, t);
            z.BaseLights[idx] = CreateBaseLight("Base_" + z.Zone + "_" + i.ToString("00"),
                                                new Vector3(x, floorY + BaseLightHeight, zz), range);
            z.BaseLightLevels[idx++] = layerIntensity;
            if (needCeilBounce)
            {
                // P37i：反弹灯贴"看得见的那层顶"。P37f/g 取证实锤：走廊/教室的可见顶板（吊顶层）
                // 低于楼板底面，反弹灯按假定高度（楼板下 0.4m）放置会整个躲进吊顶板上方——
                // 照到的是看不见的背面，可见面照旧黑。从垫底灯向上射线找第一块面（有碰撞体时精确，
                // 命中名/高度进日志），命中即贴其下 0.35m；无碰撞体回退假定高度（探针 v4 的高度带兜底定案）。
                float bounceY = floorY + BaseBounceHeight;
                RaycastHit ceilHit;
                Vector3 rayBase = new Vector3(x, floorY + BaseLightHeight, zz);
                if (Physics.Raycast(new Ray(rayBase, Vector3.up), out ceilHit, 4.5f))
                {
                    float candidate = ceilHit.point.y - 0.35f;
                    if (candidate > floorY + BaseLightHeight + 0.4f) bounceY = candidate;
                    if (i == 0)
                        Debug.Log("[IndoorFill][诊断] 反弹灯贴顶射线：「" + z.Zone + "」命中 " + ceilHit.collider.name +
                                  " y=" + ceilHit.point.y.ToString("0.00") + " → 反弹灯 y=" + bounceY.ToString("0.00"));
                }
                z.BaseLights[idx] = CreateBaseLight("BaseCeil_" + z.Zone + "_" + i.ToString("00"),
                                                    new Vector3(x, bounceY, zz), range);
                z.BaseLightLevels[idx++] = BaseBounceIntensity;
            }
        }
    }

    /// <summary>
    /// 底光建灯口。**不复用 CreateLight()**：那个方法已按 P36 方案 B 定型为 Spot 朝下 150°
    /// （a00cf88，不许回改），底光要的是全向 Point——为守住「只增层不改层」另开一口。
    /// renderMode 用 **Auto**（与补光的 ForcePixel 刻意不同）：走廊合并大面上 20 个补光
    /// Spot（ForcePixel）已占满逐像素名额，底光让位降级成顶点光反而是正确结果——
    /// 垫底要的就是大面上的平滑低频亮度，顶点插值的「抹平」恰好是目标本身（与 P17 里
    /// glow 被降级即隐身相反，那里要形状、这里要均匀）；房间/礼堂名额有空余
    /// （6~8 glow + 6~8 补光 + 1~2 底光 < 20），底光保住逐像素、更平滑。
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
        for (int i = 0; i < container.childCount && tubes.Count < cap; i++)
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
            default: return DayScaleDay;
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

        // P37j：贴顶爆破试验（取证与自动截图完成后自动执行，完毕自动还原）。
        // 把 Corridor2 中段反弹灯的强度档临时抬到 30、范围 12（强制 Eligible），0.3s 与 2s 各拍一张。
        // 亮 = 灯→可见顶板通路已通（此前纯强度不足，调常量即收口）；仍黑 = 可见顶板与 Floor2 之间
        // 另有一层不反光的遮挡面。试验期天花板短暂过曝属诊断闪烁，非验收态。
        if (_diagDone && _autoShot >= 3 && _burstStage < 4)
        {
            _burstTimer -= PollInterval;
            if (_burstStage == 0)
            {
                int zi = FindZone("Corridor2");
                if (zi < 0 || _zones[zi].BaseLights == null)
                {
                    _burstStage = 4;
                    Debug.Log("[IndoorFill][诊断] 爆破试验跳过（Corridor2 无底灯）");
                }
                else
                {
                    ZoneFill z = _zones[zi];
                    int mid = -1;
                    for (int j = 0; j < z.BaseLights.Length; j++)
                        if (z.BaseLights[j] != null && z.BaseLights[j].name.StartsWith("BaseCeil", StringComparison.Ordinal) && mid < 0) mid = j;
                    if (mid < 0) { _burstStage = 4; Debug.Log("[IndoorFill][诊断] 爆破试验跳过（无反弹灯）"); }
                    else
                    {
                        _burstIndex = mid;
                        _burstOrigLevel = z.BaseLightLevels[mid];
                        z.BaseLightLevels[mid] = 30f;
                        z.BaseEligible[mid] = true;
                        z.BaseLights[mid].range = 12f;
                        _burstTimer = 0.3f;
                        _burstStage = 1;
                        Debug.Log("[IndoorFill][诊断] 爆破试验开始：BaseCeil_Corridor2_" + mid.ToString("00") +
                                  " 强度档 30 / 范围 12（2 秒后自动还原）");
                    }
                }
            }
            else if (_burstStage == 1 || _burstStage == 2)
            {
                if (_burstTimer <= 0f)
                {
                    string dir = "Docs/QA/evidence/P37_auto";
                    try { System.IO.Directory.CreateDirectory(dir); } catch { }
                    ScreenCapture.CaptureScreenshot(dir + "/burst_" + (_burstStage - 1) + "_f" + Time.frameCount + ".png");
                    Debug.Log("[IndoorFill][诊断] 爆破截图 " + (_burstStage - 1) + " → " + dir + "/burst_" + (_burstStage - 1) + "_f*.png");
                    _burstTimer = _burstStage == 1 ? 1.7f : 0f;
                    _burstStage = _burstStage == 1 ? 2 : 3;
                }
            }
            else if (_burstStage == 3)
            {
                int zi = FindZone("Corridor2");
                if (zi >= 0 && _zones[zi].BaseLights != null && _burstIndex >= 0 && _burstIndex < _zones[zi].BaseLights.Length)
                {
                    _zones[zi].BaseLightLevels[_burstIndex] = _burstOrigLevel;
                    _zones[zi].BaseLights[_burstIndex].range = BaseRangeCorridor;
                    Debug.Log("[IndoorFill][诊断] 爆破试验还原：强度档 " + _burstOrigLevel.ToString("0.00"));
                }
                _burstStage = 4;
            }
        }

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
        Vector3 eyePos = _player != null ? _player.position + Vector3.up * EyeHeight
                                        : (_cam != null ? _cam.transform.position : Vector3.zero);

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
    /// 生效判定：只按同层（锚点眼睛与补光点 y 差 ≤2m）。
    /// 真实灯具只由"有没有电"决定亮灭，与人在不在附近无关——本层不做任何"靠近才亮"。
    /// 楼层维度保留：1F/2F 的 XZ 完全重叠，不按层分会把楼下的光透到楼上（无阴影直射）；
    /// 跨层时灯切换是真实的（你在下楼）。
    /// </summary>
    private void MarkEligible(ZoneFill z, Vector3 eye)
    {
        for (int i = 0; i < z.Lights.Length; i++)
        {
            Light l = z.Lights[i];
            z.Eligible[i] = l != null && Mathf.Abs(eye.y - l.transform.position.y) <= SameStoreyMaxDy;
        }

        // 底光与补光同一条同层规则：1F 的「反弹」不许透过楼板照到 2F（无阴影直射，P35 同款教训）。
        if (z.BaseLights != null)
        {
            for (int i = 0; i < z.BaseLights.Length; i++)
            {
                Light l = z.BaseLights[i];
                z.BaseEligible[i] = l != null && Mathf.Abs(eye.y - l.transform.position.y) <= SameStoreyMaxDy;
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
            if (hit) _glossOriginal[r] = r.sharedMaterial.GetFloat(GlossPropertyId);
        }
        if (_glossOriginal.Count > 0)
            Debug.Log("[IndoorFill] 地面高光抑制名单就绪：命中 " + _glossOriginal.Count +
                      " 个大面（P35 v5 轮实测 31 面名单 + Floor 关键字兜底），夜晚档 _Glossiness→" +
                      GlossClampValue + "，白天自动还原原值");
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
            r.GetPropertyBlock(mpb);                              // 先读：保留别人的属性（如状态染色）
            mpb.SetFloat(GlossPropertyId, on ? GlossClampValue : kv.Value);
            r.SetPropertyBlock(mpb);
        }
        Debug.Log("[IndoorFill] 地面高光抑制" + (on ? "已生效：地板镜面反射压柔（_Glossiness→" + GlossClampValue + "）"
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
                l.intensity = active ? z.BaseIntensity * ds * factor * f : 0f;
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
                    l.intensity = active ? z.BaseLightLevels[j] * ds * baseFactor * f : 0f;
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

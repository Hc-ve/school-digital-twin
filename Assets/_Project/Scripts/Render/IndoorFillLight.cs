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
// ── 版本状态（v11 回退后 = v4 内容）──────────────────────────────────
// 本文件当前 = v4 轮结束时的功能集：灯位补光（每根灯管一个光点）+ 接管走廊/
// 大房间旧光点 + 走廊应急底光 + 角色眼睛锚点 + 同层判定 + 淡入淡出。
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

    internal static void NotifyRunnerDestroyed() { _runner = null; }

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
    private const float RoomTubeIntensity = 1.6f;      // 教室每管强度起点
    private const float HallTubeRange = 9.0f;          // 食堂/礼堂单管要喂的面积更大
    private const float HallTubeIntensity = 1.3f;
    private const int MaxTubeFillsCorridor = 20;       // 走廊 15 管 + 端头楼梯间宽扫；≤20 = 逐像素名额，可全部像素化
    private const int MaxTubeFillsRoom = 8;            // 教室 6 管；食堂约 20 管封顶 8（预算保护）

    /// <summary>走廊管位扫描比登记矩形外扩多少米：楼梯间/门厅的灯贴着走廊端头、
    /// 且没有登记分区（TwinRoomBounds 无此行），不扩扫楼梯间永远没光。</summary>
    private const float CorridorScanExpand = 10f;

    /// <summary>接管走廊/大房间的旧光点：表现层给每根灯管建了一个 range≈3m 的 TwinLampGlow，
    /// 走廊 30 个 glow + 15 个管位补光 = 45 个候选抢 20 个逐像素名额 → 按重要性"抽签"，
    /// 抽不到的地方一块亮一块暗。关掉走廊与大房间的 glow、由管位补光独家负责后：
    /// 候选 = 15（≤20）→ 全部逐像素、确定性照明。教室 6 glow + 6 补光 = 12 恰好在名额内
    /// 且天花板光圈是已验收观感 —— 保留不动。改 false 即新旧并存；
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
    }

    private readonly List<ZoneFill> _zones = new List<ZoneFill>(16);
    private readonly List<Light> _suppressedGlows = new List<Light>(32);
    private bool _buildFailedFinal;
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
            // 接管范围：走廊（30 个 glow 与管位补光抢名额）与大房间（20+ glow 超名额）。
            // 教室 6 glow + 6 补光 = 12 恰好在名额内，且天花板光圈是已验收的观感 —— 保留。
            z.TakeOver = corridor || (maxX - minX) * (maxZ - minZ) > 200f;

            // 首选：补光点放在**真实灯管正下方**（灯位从场景读，不是按矩形拍点）——
            // 仿真语义：有几根管就有几片光，光池对准灯管，灯不动光就不动。
            if (!BuildTubeFills(z))
            {
                // 兜底：灯位不可得（锚点在标记牌上 / 容器结构意外）→ 退回矩形布点
                if (corridor) BuildCorridorGrid(z, floorY);
                else BuildRoomFills(z, floorY, area);
            }

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
        Light l = go.AddComponent<Light>();
        l.type = LightType.Point;                      // 内置管线只有 Point/Spot 有实时光，Area 仅烘焙（D13 后为零）
        l.color = FillColor;
        l.range = range;
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
        }
    }

    internal string Describe()
    {
        int total = 0;
        for (int i = 0; i < _zones.Count; i++) total += _zones[i].Lights.Length;
        return "[IndoorFill] 分区 " + _zones.Count + " / 补光点 " + total +
               "（走廊 " + (total - RoomFillCount()) + " + 房间 " + RoomFillCount() + "）" +
               " / 本帧启用 " + _activeCount +
               " / 昼夜倍率 " + _dayScale.ToString("0.00") +
               " / 总开关 " + (IndoorFillLight.Enabled ? "开" : "关");
    }

    private int RoomFillCount()
    {
        int n = 0;
        for (int i = 0; i < _zones.Count; i++) if (!_zones[i].IsCorridor) n += _zones[i].Lights.Length;
        return n;
    }
}

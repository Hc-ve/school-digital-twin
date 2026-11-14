// P38 走廊/教室顶板 · 运行时程序化补板层（负责人裁决 ①，四闸门核查表已核准，2026-11-14）
// 归属：外包执行模型（P38）　工程：E:\Unity Projects\School-pro · Unity 2022.3.62f3c1 · 内置管线 · Linear
//
// ── 为什么会有这个文件（一句话，取证链见 Docs/QA/evidence/P37_auto/ 与 Editor.log）──────
// SchoolBuilding.FBX 的顶板节点只有 ZalTop/PCClassTop/DirectorTop/ClassTop 四种
// （grep -a -o 提取实锤），走廊与多数教室的顶板面缺失——夜里抬头看到的是天空穹顶
// （StarDome，Queue=Background+ZWrite Off）。P37j 爆破试验实锤：走廊中段反弹灯
// 30 倍强度直烤可见顶板纹丝不动 = 该处没有任何可照亮的板面。灯光路线到头，
// 本层在运行时给缺顶板的分区程序化补一块白色顶板（与 IndoorFillLight 建灯同口径：
// 运行时生成、零 .unity/.mat 资产落盘、退出 Play 自动消失）。
//
// ── 四闸门（核查表已由负责人逐条核准，实现逐一落实）────────────────────
// 闸1 小地图：TwinMinimap 无 RT 相机（全文仅 :400 读 Camera.main 位置）→ 图标式无遮挡风险；
//     实现后小地图画面目检并入自动截图序列。
// 闸2 F20：SnowOnRoofs 已 DISABLED_BY_INCIDENT_20261105=true 硬停用、SnowOnTrees 无全局扫描；
//     命名 RoomShell_* 避开 Roof/Top/Ceiling 关键词族；bounds 独立不撑任何人的包围盒；
//     新增 MeshCollider 使 WeatherVisuals 头顶室内判定射线可命中（走廊雨雪判定加分项，只记录）。
// 闸3 俯视：玩家眼高高于楼顶（滞回 ±0.5m）自动隐藏整层顶板、回落自动恢复——防止
//     悬停临界高度时每帧闪烁（负责人复核时追加的要求）。
// 闸4 红线：顶板由底光/反弹灯照亮，A/B 自动截图对天花板区域核 p50≤0.8 不惨白；
//     白天档顶板自动隐藏（白天档零变化红线——白天保持已验收观感）。
//
// ── 键位（负责人裁决，纠正了本模型上一轮"F1 被占"的错误结论）────────────
// F1 = 顶板层 A/B 独立开关（grep 实测 F1 全库零占用；不与 F2 底光合并——
// 验收归因需要"只开顶板 / 只开底光"的隔离对比，同开同关按两个键即可）。

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using SchoolTwin.Core;
using SchoolTwin.Player;
using SchoolTwin.Twin;
using SchoolTwin.Weather;

// ── 为什么是独立文件而不是并进 IndoorFillLight ────────────────────────
// P37 底光层（IndoorFillLight.cs/TwinLampBloom.cs）已随 d6fe7a7 验收入库，
// 本文件保持它们字节原样：P38 成果独立成一个新文件，负责人可单独提交、单独回滚，
// 不会把两个任务包混进同一个 diff。

public static class RoomShellLayer
{
    /// <summary>顶板层总开关（F1 A/B）。false = 全部顶板隐藏，底光层/补光层/灯管表现完全不受影响。</summary>
    public static bool Enabled { get; private set; }

    private static RoomShellRunner _runner;
    private static SceneWatcher _watcher;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        // G10 教训：关掉 Domain Reload 时静态字段跨 Play 存活，必须复位
        _runner = null;
        _watcher = null;
        Enabled = false;   // P38 终版：主理人视觉否决补板方案（"糊了一层挡住原本的天花板"），
                           // 默认关闭——F1 可重新启用做对照实验；如需彻底移除可整文件删除
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap() { TryMount(); }

    /// <summary>F1 A/B 开关。随时可切、立即生效；运行时态不落盘，退出 Play 自动消失。</summary>
    public static void SetEnabled(bool on)
    {
        Enabled = on;
        if (_runner != null) _runner.RefreshNow();
        Debug.Log("[RoomShell] 顶板层" + (on ? "已启用" : "已停用（A/B 对比）") +
                  "。改动只存在于运行时，退出 Play 自动消失。");
    }

    public static string Describe()
    {
        if (_runner == null) return "[RoomShell] 未挂载";
        return _runner.Describe();
    }

    internal static bool TryMount()
    {
        if (_runner != null) return true;
        if (TwinRuntime.Instance == null) return false;   // 与 IndoorFillLight 同判据：只挂 SchoolSceneDay

        // P38 终版补丁：Enabled 强制归 false（每次 Play 必经此处）——本工程 Domain Reload 关闭，
        // 静态字段跨 Play 存活（G10 同源），仅靠 ResetStatics 的默认值在"上次 Play 开着顶板"时
        // 不会生效，补板会在下一局照常重建（主理人实测复现）。显式归 false 才是硬闸。
        Enabled = false;

        GameObject go = new GameObject("[RoomShellLayer]");
        _runner = go.AddComponent<RoomShellRunner>();
        if (_watcher == null)
        {
            GameObject w = new GameObject("[RoomShellLayer.Watcher]");
            UnityEngine.Object.DontDestroyOnLoad(w);
            _watcher = w.AddComponent<SceneWatcher>();
        }
        return true;
    }

    internal static void NotifyRunnerDestroyed() { _runner = null; }

    private sealed class SceneWatcher : MonoBehaviour
    {
        private void Awake() { UnityEngine.Object.DontDestroyOnLoad(gameObject); }
        private void OnEnable() { SceneManager.sceneLoaded += OnSceneLoaded; }
        private void OnDisable() { SceneManager.sceneLoaded -= OnSceneLoaded; }
        private void OnSceneLoaded(Scene scene, LoadSceneMode mode) { TryMount(); }
    }
}

// =====================================================================
// 运行主体：补板生成 + 可见性控制。全部调参常量收在这里（使用点都在本类内）。
// =====================================================================
internal sealed class RoomShellRunner : MonoBehaviour
{
    private sealed class ShellGroup
    {
        public string Zone;
        public float FloorY;
        public float MinX, MaxX, MinZ, MaxZ;
        public Renderer Shell;
    }

    /// <summary>顶板离地高度：吊顶线。1F 走廊梁顶 ≈3.4（P37i 射线实测命中 y=3.34 的梁/灯具下方），
    /// 板放 3.5 恰在梁上方 0.16m，把梁之间的开口盖住；2F 同式（3.56+3.5=7.06）。</summary>
    private const float ShellHeightAboveFloor = 3.5f;

    /// <summary>分区矩形外扩（米）：顶板边缘塞进墙厚里，避免四周漏缝看到星点。</summary>
    private const float ShellMargin = 0.3f;

    /// <summary>闸3 滞回：眼高高于 HideAboveY 隐藏、低于 ShowBelowY 恢复（±0.5m 滞回带，
    /// 负责人复核追加——防玩家悬停临界高度时顶板每帧闪烁）。楼顶 ≈7.5~8m，留出俯视余量。</summary>
    private const float HideAboveY = 9.5f;
    private const float ShowBelowY = 8.5f;

    /// <summary>已有 FBX 顶板的分区不再补板（ZalTop/PCClassTop/DirectorTop 覆盖），
    /// 避免双层顶板。清单依据 = FBX 内部节点提取（grep -a 实锤）。</summary>
    private static readonly string[] HasRealTopZones = { "ActZal", "ClassComputer", "ClassDirector" };

    private const float PollInterval = 0.2f;
    private const string ShotDir = "Docs/QA/evidence/P38_auto";

    private readonly List<ShellGroup> _shells = new List<ShellGroup>(16);
    private bool _buildDone;
    private bool _buildFailedFinal;
    private bool _visibleApplied;
    private bool _hiddenByAltitude;
    private Transform _player;
    private float _playerSearchTimer;
    private int _abStage;
    private float _abTimer;

    private void OnDestroy() { RoomShellLayer.NotifyRunnerDestroyed(); }

    private void Update()
    {
        // P38b：逐像素名额下限自持。根因——全楼 47 盏灯（补光 40 内 + 垫底/反弹 40 外）抢
        // IndoorFillLight 设的 32 个名额，Unity 按相机远近排序分配：相机一转名额就换班，
        // 合并大网格（走廊/2F 楼板）上表现为"灯光随视角出现/消失"。抬到 48 ≥ 总灯数 →
        // 零竞争，任何视角全灯全量逐像素。只抬不降（与 IndoorFillLight 的 32 共存，本帧后写者胜）；
        // 帧率明显下降时把 48 调回 32 即回滚（一行）。
        if (QualitySettings.pixelLightCount < 48) QualitySettings.pixelLightCount = 48;

        // F1 先行：不依赖建板状态（P38 终版——顶板默认关闭时 F1 也必须可用）
        if (Input.GetKeyDown(KeyCode.F1))
            RoomShellLayer.SetEnabled(!RoomShellLayer.Enabled);

        if (!_buildDone && !_buildFailedFinal) BuildShellsWhenReady();
        if (!_buildDone) return;   // 未建板：F1 已处理；可见性/A/B/爆破无对象可操作

        PollVisibility();

        // 闸4 自动 A/B：夜晚档才拍（P37j 轮教训——A/B 拍在白天档等于没拍）+ 仅在顶板启用时执行。
        // 建板 2 秒后拍"顶板开"，再自动关板 0.6 秒拍"顶板关"，然后恢复。
        // 同机位一对截图，天花板区域红线（p50≤0.8 不惨白）由执行方读盘核对。
        if (_abStage < 3 && RoomShellLayer.Enabled)
        {
            EnvironmentState abEnv = EnvironmentState.Instance;
            if (abEnv != null && abEnv.timeOfDay == TimeOfDay.Day)
            {
                _abTimer = 1f;          // 白天档挂起，切到夜/黄昏后自动继续
                return;
            }
            _abTimer -= Time.deltaTime;
            if (_abStage == 0) { _abTimer = 2f; _abStage = 1; }
            else if (_abStage == 1 && _abTimer <= 0f)
            {
                try { System.IO.Directory.CreateDirectory(ShotDir); } catch { }
                UnityEngine.ScreenCapture.CaptureScreenshot(ShotDir + "/gate4_shell_on_f" + Time.frameCount + ".png");
                Debug.Log("[RoomShell][闸4] 顶板开态截图 → " + ShotDir + "/gate4_shell_on_f*.png");
                RoomShellLayer.SetEnabled(false);
                _abTimer = 0.6f;
                _abStage = 2;
            }
            else if (_abStage == 2 && _abTimer <= 0f)
            {
                UnityEngine.ScreenCapture.CaptureScreenshot(ShotDir + "/gate4_shell_off_f" + Time.frameCount + ".png");
                Debug.Log("[RoomShell][闸4] 顶板关态截图（对照）→ " + ShotDir + "/gate4_shell_off_f*.png");
                RoomShellLayer.SetEnabled(true);
                _abStage = 3;
            }
        }
    }

    /// <summary>外部开关切换后立即刷一帧可见性，不等下一个 Update。</summary>
    internal void RefreshNow() { ApplyVisibility(true); }

    private void BuildShellsWhenReady()
    {
        if (!RoomShellLayer.Enabled) return;   // P38 终版：开关关闭 = 一块板都不建（每帧重查；F1 开启后本方法自动开建——不设 _buildFailedFinal，否则 F1 会被永久挡死）
        TwinRuntime rt = TwinRuntime.Instance;
        if (rt == null || rt.Registry == null || rt.Registry.All.Count == 0) return;

        // 着色器不 Shader.Find（F22：打包会被剥离）——从场景既有 Standard 渲染器借
        // （Floor2 已被 P37i 探针证实存在且是 Standard、白反照率）。
        Shader stdShader = FindFloorShader();

        int built = 0;
        for (int i = 0; i < rt.Registry.All.Count; i++)
        {
            TwinAnchor a = rt.Registry.All[i];
            if (a == null) continue;
            if (!string.Equals(a.Category, "Light", StringComparison.Ordinal)) continue;
            if (string.IsNullOrEmpty(a.Zone)) continue;
            if (a.TwinId != null && a.TwinId.StartsWith("BAK", StringComparison.Ordinal)) continue;
            if (FindZone(a.Zone) >= 0) continue;                                  // 同分区只建一次
            if (Array.IndexOf(HasRealTopZones, a.Zone) >= 0) continue;            // 已有 FBX 顶板的不补（闸2 名单）

            float minX, maxX, minZ, maxZ;
            if (!TwinRoomBounds.TryGetBounds(a.Zone, out minX, out maxX, out minZ, out maxZ)) continue;

            TwinRoomInfo info = TwinSiteLayout.Find(a.Zone);
            int floor = info != null ? info.Floor : 1;
            float floorY = (Mathf.Clamp(floor, 1, 2) - 1) * 3.56f;   // StoreyHeight 同 IndoorFillLight 口径

            BuildZoneShell(a.Zone, floorY, minX, maxX, minZ, maxZ, stdShader);
            built++;
        }

        if (_shells.Count == 0)
        {
            _buildFailedFinal = true;
            Debug.Log("[RoomShell] 没有匹配到任何需要补板的分区（此条只打一次）");
            return;
        }
        _buildDone = true;
        Debug.Log("[RoomShell] 顶板层就绪：补板 " + _shells.Count + " 块（F1 A/B；白天档自动隐藏；" +
                  "眼高>" + HideAboveY.ToString("0.0") + "m 俯视自动隐藏，<" + ShowBelowY.ToString("0.0") + "m 恢复）");
        RefreshNow();
    }

    /// <summary>顶板着色器：**Unlit/Color**（P38g 定案）。Standard+自发光路线被现场实验否决两次：
    /// ① 自发光乘默认黑图三轮无效（P38f）；② 修正白图后强度 2.0 直接惨白削顶（夜间曝光链 ≈1.0，
    /// 并无强压暗——此前的曝光估计作废）。Unlit 恒定色 = 与灯光/视角/距离/曝光链全部解耦的
    /// 确定性方案：渲染出来恒等于设定色。标定证据：MCP 活体实测 color 1.15 → 走廊顶板惨白削顶，
    /// 故最终取 0.55（屏幕 ≈0.55，与真实参照的夜走廊顶板亮度一致）。</summary>
    private static Shader FindFloorShader()
    {
        Shader unlit = Shader.Find("Unlit/Color");
        if (unlit != null) return unlit;
        Renderer[] all = FindObjectsOfType<Renderer>();
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i] != null && all[i].sharedMaterial != null &&
                string.Equals(all[i].name, "Floor2", StringComparison.Ordinal))
                return all[i].sharedMaterial.shader;
        }
        return null;   // 调用处回退
    }

    private int FindZone(string zone)
    {
        for (int i = 0; i < _shells.Count; i++)
            if (string.Equals(_shells[i].Zone, zone, StringComparison.Ordinal)) return i;
        return -1;
    }

    /// <summary>一块顶板 = 分区矩形（外扩 ShellMargin 塞进墙厚）× 水平面，法线朝下（朝室内）。
    /// 高度 = **贴顶射线命中面下方 0.05m**（P38g 现场实验定案：固定高度 floorY+3.5 在 2F 走廊
    /// 比真实顶板（Floor2 底面 y=6.90）高了 0.16m，整块板躲进顶板上方的夹层被挡住——
    /// 夜里看起来仍是黑的。MCP 活体实验：把板降到 6.85 当场点亮）。无碰撞体回退固定高度。</summary>
    private void BuildZoneShell(string zone, float floorY, float minX, float maxX, float minZ, float maxZ, Shader stdShader)
    {
        float shellY = floorY + ShellHeightAboveFloor;
        RaycastHit ceilHit;
        Vector3 rayOrigin = new Vector3((minX + maxX) * 0.5f, floorY + 1.5f, (minZ + maxZ) * 0.5f);
        if (Physics.Raycast(new Ray(rayOrigin, Vector3.up), out ceilHit, 4.5f))
        {
            float candidate = ceilHit.point.y - 0.05f;
            if (candidate > floorY + 2.6f && candidate < floorY + 4.2f) shellY = candidate;
            Debug.Log("[RoomShell] 「" + zone + "」贴顶射线命中 " + ceilHit.collider.name +
                      " y=" + ceilHit.point.y.ToString("0.00") + " → 顶板 y=" + shellY.ToString("0.00"));
        }
        float y = shellY;
        float x0 = minX - ShellMargin, x1 = maxX + ShellMargin;
        float z0 = minZ - ShellMargin, z1 = maxZ + ShellMargin;

        Mesh mesh = new Mesh();
        Vector3[] verts =
        {
            new Vector3(x0, 0f, z0), new Vector3(x1, 0f, z0),
            new Vector3(x1, 0f, z1), new Vector3(x0, 0f, z1)
        };
        int[] tris = { 0, 1, 2, 0, 2, 3 };
        mesh.vertices = verts;
        mesh.triangles = tris;
        mesh.RecalculateNormals();
        if (mesh.normals[0].y > 0f)
        {
            // 法线必须朝下（朝室内）。不赌手算绕序：实测朝上就翻转三角形顺序，自校验。
            mesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            mesh.RecalculateNormals();
        }
        mesh.RecalculateBounds();
        // P38c：零厚度包围盒的视锥剔除坑——纯平面网格的 AABB 高度为 0，掠射角（平视远端）时
        // 整块板会被误判为视锥外而整体剔除（症状 = "顶板随视角消失/出现"，爆破 30 倍都"照不亮"
        // 的真凶）。给包围盒加厚 1.2m：纯剔除用途，不改变渲染画面，任意视角稳定可见。
        Vector3 bsize = mesh.bounds.size;
        mesh.bounds = new Bounds(mesh.bounds.center, new Vector3(bsize.x, bsize.y + 1.2f, bsize.z));

        GameObject go = new GameObject("RoomShell_" + zone);
        go.transform.SetParent(transform, false);                 // 本根在原点无旋转 → local 即世界
        go.transform.localPosition = new Vector3(0f, y, 0f);
        MeshFilter mf = go.AddComponent<MeshFilter>();
        mf.sharedMesh = mesh;
        MeshRenderer mr = go.AddComponent<MeshRenderer>();

        Material mat = stdShader != null ? new Material(stdShader) : new Material(Shader.Find("Standard"));
        mat.name = "RoomShell_Runtime_" + zone;
        // P38g 终版：Unlit 恒定色 (0.55, 0.53, 0.48) 暖白——夜间曝光链实测 ≈1.0 倍
        // （MCP 标定：color 1.15 → 顶板惨白削顶），故 0.55 落屏 ≈0.55，与真实参照的
        // 夜走廊顶板亮度一致；无灯光响应 → 视角/模式/距离全无关，确定性达标。
        mat.color = new Color(0.55f, 0.53f, 0.48f);
        mr.sharedMaterial = mat;
        mr.enabled = false;                                       // 可见性由 PollVisibility 统一控制
        go.AddComponent<MeshCollider>();                          // 加分项：头顶室内判定射线可命中（只记录）

        _shells.Add(new ShellGroup
        {
            Zone = zone,
            FloorY = floorY,
            MinX = x0, MaxX = x1, MinZ = z0, MaxZ = z1,
            Shell = mr
        });
        Debug.Log("[RoomShell] 补板：「" + zone + "」y=" + y.ToString("0.00") +
                  " 尺寸 " + (x1 - x0).ToString("0.0") + "×" + (z1 - z0).ToString("0.0") +
                  "（外扩 " + ShellMargin.ToString("0.0") + "m）");
    }

    /// <summary>可见性 = 总开关(F1) × 夜/黄昏档（白天隐藏=白天零变化红线） × 俯视滞回（闸3）。变更才写。</summary>
    private void PollVisibility()
    {
        if (_player == null)
        {
            _playerSearchTimer -= Time.deltaTime;
            if (_playerSearchTimer <= 0f)
            {
                _playerSearchTimer = 1f;
                TwinPlayerController pc = FindObjectOfType<TwinPlayerController>();
                _player = pc != null ? pc.transform : null;
            }
        }
        float eyeY = _player != null ? _player.position.y + 1.6f : -1f;
        if (eyeY > HideAboveY) _hiddenByAltitude = true;            // 飞高于楼顶 → 隐藏（闸3）
        else if (eyeY > 0f && eyeY < ShowBelowY) _hiddenByAltitude = false;   // 回落 → 恢复（滞回带内保持）

        bool night = true;
        EnvironmentState env = EnvironmentState.Instance;
        if (env != null) night = env.timeOfDay != TimeOfDay.Day;    // 白天档隐藏 = 白天零变化红线

        ApplyVisibility(night && !_hiddenByAltitude);
    }

    private void ApplyVisibility(bool targetVisible)
    {
        if (targetVisible == _visibleApplied) return;   // 变更才写，常驻零开销
        _visibleApplied = targetVisible;
        for (int i = 0; i < _shells.Count; i++)
            if (_shells[i].Shell != null) _shells[i].Shell.enabled = targetVisible;
        Debug.Log("[RoomShell] 顶板可见性 → " + (targetVisible ? "显示" : "隐藏") +
                  "（F1=" + (RoomShellLayer.Enabled ? "开" : "关") +
                  " 档=" + (EnvironmentState.Instance != null && EnvironmentState.Instance.timeOfDay != TimeOfDay.Day ? "夜/黄昏" : "白天") +
                  " 俯视隐藏=" + (_hiddenByAltitude ? "是" : "否") + "）");
    }

    internal string Describe()
    {
        return "[RoomShell] 补板 " + _shells.Count + " 块 / 总开关 " + (RoomShellLayer.Enabled ? "开" : "关") +
               " / 当前可见 " + (_visibleApplied ? "是" : "否") + "（F1 A/B）";
    }
}

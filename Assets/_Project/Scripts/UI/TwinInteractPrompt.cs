using System;
using System.Collections.Generic;
using UnityEngine;
using SchoolTwin.Command;
using SchoolTwin.Core;
using SchoolTwin.Interaction;
using SchoolTwin.Twin;

namespace SchoolTwin.UI
{
    /// <summary>
    /// 走近物体时弹交互按钮 —— 门/灯/投影/开关方块的**驱动器**。
    ///
    /// 【P27 重构】旧版自己搭单按钮 UI（Rounded + Button + E 键提示），
    /// 现在全部交给统一的 `TwinInteractMenu` 渲染：本类只负责
    /// 「找目标 → 构造一条 Item → menu.Show()」，与主脑面板同一套视觉。
    ///
    /// ── 交互目标从哪来 ────────────────────────────────────────────
    /// 两条来源，都不需要手工配置：
    ///   ① `TwinInteractPoint`（墙上的灯开关方块）→ 指向房间的照明点位
    ///   ② `TwinAnchor` 里 Category = Door / Light 的**实物本身**（门扇就是能走过去的实体）
    ///
    /// ── 判定「可以交互」的三个条件 ────────────────────────────────
    ///   ① 距离 ≤ Range（默认 2.4m，门的交互点取门扇中心）
    ///   ② 在角色**身前**（dot(朝向, 指向目标) > 0.2）—— 背后的东西不该弹按钮
    ///   ③ 该点位在控制白名单里（不在白名单的只能看不能控）
    ///
    /// ── E 键归谁 ──────────────────────────────────────────────────
    /// E 确认统一由 TwinInteractMenu 处理（选中即执行；单条目 = 直接执行）。
    /// 旧版自己的 `Input.GetKeyDown(_key)` 已随 UI 一起删除。
    /// </summary>
    [AddComponentMenu("SchoolTwin/交互驱动器 (TwinInteractPrompt)")]
    public sealed class TwinInteractPrompt : MonoBehaviour
    {
        [Header("判定")]
        [Tooltip("最大交互距离（米）。灯开关方块自己有 Range 字段，会覆盖这个值")]
        [SerializeField] private float _range = 2.4f;

        [Tooltip("要求目标在角色身前：dot(朝向, 指向目标) 要大于这个值")]
        [SerializeField] private float _facingDot = 0.2f;

        /// <summary>沿用旧单按钮的屏幕位置（右侧偏中）。</summary>
        private static readonly Vector2 Anchor = new Vector2(0.62f, 0.5f);

        // ---------------------------------------------------------------- 运行时

        private sealed class Candidate
        {
            public string TwinId;
            public Vector3 WorldPos;
            public float Range;
            public string Label;
            public CommandAction Action;
            public float Value;
            public bool IsDoor;
        }

        private readonly List<Candidate> _candidates = new List<Candidate>(64);

        /// <summary>范围内有效候选（FindBest 填充，可能不止一条）。</summary>
        private readonly List<Candidate> _hits = new List<Candidate>(4);

        /// <summary>复用的条目缓冲（避免每帧 new List 产生 GC）。</summary>
        private readonly List<TwinInteractMenu.Item> _items = new List<TwinInteractMenu.Item>(4);

        private Transform _player;
        private Camera _cam;

        // 扫描缓存：FindObjectsOfType 每帧跑两遍太浪费，改成 1 秒刷一次
        private TwinInteractPoint[] _points;
        private TwinAnchor[] _anchors;
        private float _scanAt;

        // 观景模式下角色控制器没被禁用，面板会和「自由相机」抢 E/滚轮 —— 干脆不弹
        private FreeCameraController _freeCam;
        private bool _freeCamLooked;

        // ================================================================ 生命周期

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (FindObjectOfType<TwinInteractPrompt>(true) != null) return;

            GameObject go = new GameObject("TwinInteractPrompt");
            go.transform.SetParent(TwinInteractMenu.Ensure().transform, false);
            go.AddComponent<TwinInteractPrompt>();
            Debug.Log("[TwinInteract] 门/灯交互驱动器已挂载（统一菜单渲染）");
        }

        private void Update()
        {
            TwinInteractMenu menu = TwinInteractMenu.Ensure();

            if (_player == null)
            {
                TwinPlayerRef();
                if (_player == null) { menu.Hide(); return; }
            }
            if (_cam == null) _cam = Camera.main;
            if (_cam == null) { menu.Hide(); return; }

            // 观景模式下角色原地不动，面板没有意义，还会和 E 键抢
            if (!_freeCamLooked)
            {
                _freeCamLooked = true;
                _freeCam = FindObjectOfType<FreeCameraController>();
            }
            if (_freeCam != null && _freeCam.enabled) { menu.Hide(); return; }

            if (!FindBest()) { menu.Hide(); return; }

            // 候选 → 条目。每条一个 Item，走统一菜单渲染。
            _items.Clear();
            for (int i = 0; i < _hits.Count; i++)
            {
                Candidate c = _hits[i];

                // 目标必须在画面里，否则背后的东西也弹按钮
                Vector3 world = c.WorldPos + Vector3.up * 0.9f;
                Vector3 sp = _cam.WorldToScreenPoint(world);
                if (sp.z <= 0f) continue;                                     // 相机背后
                if (sp.x < 0f || sp.x > _cam.pixelWidth ||
                    sp.y < 0f || sp.y > _cam.pixelHeight) continue;           // 跑出画面

                _items.Add(new TwinInteractMenu.Item(c.Label,
                    c.IsDoor ? TwinInteractMenu.IconKind.Door : TwinInteractMenu.IconKind.Power,
                    () => Use(c)));
            }

            if (_items.Count == 0) { menu.Hide(); return; }
            menu.Show(_items, Anchor);
        }

        private void TwinPlayerRef()
        {
            SchoolTwin.Player.TwinPlayerController pc = FindObjectOfType<SchoolTwin.Player.TwinPlayerController>();
            if (pc != null) _player = pc.transform;
        }

        // ================================================================ 找目标

        /// <summary>
        /// 扫描 + 筛选，结果填进 `_hits`（按距离升序）。
        /// 返回 false = 范围内没有任何有效候选。
        /// </summary>
        private bool FindBest()
        {
            _candidates.Clear();
            _hits.Clear();
            RefreshCache();

            // ① 交互点（墙上的开关方块）
            //    ⚠️ 必须查白名单：场景里 `Class2-1` 有 BAK / BAK2 两个备份灯锚点，
            //    它们的 TwinId 不在 `TwinControlScope` 里 —— 不筛的话按钮照弹，
            //    点下去命令被拒，用户只会看到「点了没反应」。
            for (int i = 0; i < _points.Length; i++)
            {
                TwinInteractPoint p = _points[i];
                if (p == null || string.IsNullOrEmpty(p.TargetTwinId)) continue;
                if (!TwinControlScope.IsEnabled(p.TargetTwinId)) continue;
                AddCandidate(p.TargetTwinId, p.transform.position, p.Range, p.Label);
            }

            // ② 门/灯的实物锚点
            for (int i = 0; i < _anchors.Length; i++)
            {
                TwinAnchor a = _anchors[i];
                if (a == null || string.IsNullOrEmpty(a.TwinId)) continue;
                if (!TwinControlScope.IsEnabled(a.TwinId)) continue;

                TwinDeviceModel model = TwinDeviceModels.Resolve(a.TwinId);
                if (model == null) continue;
                if (!string.Equals(model.Type, "Door", StringComparison.Ordinal) &&
                    !string.Equals(model.Type, "Light", StringComparison.Ordinal)) continue;

                AddCandidate(a.TwinId, a.transform.position, _range, "");
            }

            if (_candidates.Count == 0) return false;

            // 距离最近 + 在身前，按距离升序填 _hits
            Vector3 p0 = _player.position;
            Vector3 fwd = _player.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.0001f) fwd = Vector3.forward;

            for (int i = 0; i < _candidates.Count; i++)
            {
                Candidate c = _candidates[i];
                Vector3 to = c.WorldPos - p0;
                float dist = to.magnitude;
                if (dist > c.Range) continue;

                to.y = 0f;
                if (to.sqrMagnitude > 0.0001f && Vector3.Dot(to.normalized, fwd) < _facingDot) continue;

                c.Range = dist;   // 记下实际距离，排序用
                _hits.Add(c);
            }

            if (_hits.Count == 0) return false;
            _hits.Sort((a, b) => a.Range.CompareTo(b.Range));
            return true;
        }

        /// <summary>1 秒刷新一次候选来源。新生成的开关最多晚 1 秒才认得到，可以接受。</summary>
        private void RefreshCache()
        {
            if (_points != null && _anchors != null && Time.unscaledTime < _scanAt) return;
            _scanAt = Time.unscaledTime + 1f;
            _points = FindObjectsOfType<TwinInteractPoint>();
            _anchors = FindObjectsOfType<TwinAnchor>();
        }

        private void AddCandidate(string twinId, Vector3 pos, float range, string label)
        {
            // 【P26】主脑由 `TwinPcMenuDriver` 接管（三个开机模式 + 开机后按钮组），
            // 这里必须跳过 —— 否则单按钮提示和多按钮面板会同时弹在屏幕上，
            // 用户点了单按钮只能"开/关"，三个模式等于没做。
            if (string.Equals(twinId, "ClassComputer/PC/Master_01", StringComparison.Ordinal)) return;

            // 同一点位只留一个：墙上的开关方块先加，天花板上的灯锚点后加，
            // 不去重的话两处都在范围内时标签会来回跳。
            for (int i = 0; i < _candidates.Count; i++)
                if (string.Equals(_candidates[i].TwinId, twinId, StringComparison.Ordinal)) return;

            TwinDeviceModel model = TwinDeviceModels.Resolve(twinId);
            if (model == null) return;

            Candidate c = new Candidate();
            c.TwinId = twinId;
            c.WorldPos = pos;
            c.Range = range > 0.1f ? range : _range;

            // 动作与取值：门是 SetOpen，灯是 SetPower；读当前回读值决定"开/关"
            // 注意 anchor 必须显式初始化：下面那串 && 是短路的，rt 为 null 时
            // TryGet 根本不会执行，编译器会判「使用了未赋值变量」（CS0165）。
            TwinAnchor anchor = null;
            TwinRuntime rt = TwinRuntime.Instance;
            bool has = rt != null && rt.Registry != null && rt.Registry.TryGet(twinId, out anchor) && anchor != null;

            if (string.Equals(model.Type, "Door", StringComparison.Ordinal))
            {
                c.Action = CommandAction.SetOpen;
                c.IsDoor = true;
                TwinValue v;
                bool open = has && anchor.TryGetValue("Open", out v) && v.Value >= 0.5f;
                c.Value = open ? 0f : 1f;
                if (string.IsNullOrEmpty(label)) c.Label = "开关门";
            }
            else
            {
                c.Action = CommandAction.SetPower;
                c.IsDoor = false;
                TwinValue v;
                bool on = has && anchor.TryGetValue("Power", out v) && v.Value >= 0.5f;
                c.Value = on ? 0f : 1f;

                if (string.IsNullOrEmpty(label))
                {
                    // 【P26】文案按设备类型走。原来一律"开灯/关灯"，
                    // 电脑点位弹出来写"开灯"，一眼就错。
                    string type = model.Type;
                    if (string.Equals(type, "PC", StringComparison.Ordinal))
                        c.Label = on ? "关机" : "开机";
                    else if (string.Equals(type, "Projector", StringComparison.Ordinal))
                        c.Label = on ? "关闭投影" : "打开投影";
                    else
                        c.Label = on ? "关灯" : "开灯";
                }
            }

            if (!string.IsNullOrEmpty(label)) c.Label = label;
            _candidates.Add(c);
        }

        // ================================================================ 执行

        /// <summary>
        /// 发命令。旧版 `Use()` 的逻辑原样保留：冷却 + TrySend + 日志 + 立即翻转值
        /// （回读确认后会被真实状态覆盖）。
        /// </summary>
        private void Use(Candidate c)
        {
            if (c == null) return;

            TwinCommandCenter center = TwinCommandCenter.Ensure();
            if (center == null) return;

            string reject;
            if (!center.TrySend(c.TwinId, c.Action, c.Value, out reject))
            {
                Debug.LogWarning("[TwinInteract] " + c.TwinId + " 被拒绝：" + reject);
                return;
            }

            Debug.Log("[TwinInteract] " + c.Label + " → " + c.TwinId +
                      " " + c.Action + "(" + c.Value + ")");

            // 立刻翻转，下一次按就是反向（回读确认后会被真实状态覆盖）
            c.Value = c.Value >= 0.5f ? 0f : 1f;
        }
    }
}

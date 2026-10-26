using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using SchoolTwin.Command;
using SchoolTwin.Core;
using SchoolTwin.Interaction;
using SchoolTwin.Twin;

namespace SchoolTwin.UI
{
    /// <summary>
    /// 走近物体时在它旁边弹一个交互按钮（和平精英/原神那种），点一下就交互。
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
    /// ── 为什么用投影而不是 World Space Canvas ─────────────────────
    /// 世界空间 Canvas 会被墙挡、被近裁剪面切、缩放还要跟着距离调。
    /// 投影到 Overlay 上只需要一次 WorldToScreenPoint，按钮永远清晰、永远在最上层。
    /// </summary>
    [AddComponentMenu("SchoolTwin/交互按钮 (TwinInteractPrompt)")]
    public sealed class TwinInteractPrompt : MonoBehaviour
    {
        [Header("判定")]
        [Tooltip("最大交互距离（米）。灯开关方块自己有 Range 字段，会覆盖这个值")]
        [SerializeField] private float _range = 2.4f;

        [Tooltip("要求目标在角色身前：dot(朝向, 指向目标) 要大于这个值")]
        [SerializeField] private float _facingDot = 0.2f;

        [Tooltip("交互键（和点击按钮等效）")]
        [SerializeField] private KeyCode _key = KeyCode.E;

        [Tooltip("两次交互之间的最小间隔（秒），防连点")]
        [SerializeField] private float _cooldown = 0.35f;

        [Header("外观")]
        [SerializeField] private Vector2 _size = new Vector2(132f, 38f);

        [Tooltip("按钮在屏幕上的**固定**位置（归一化 0~1，左下角是 0,0）。" +
                 "参考原神/吃鸡：交互提示钉在固定位置，不跟着物体在屏幕上跑")]
        [SerializeField] private Vector2 _screenAnchor = new Vector2(0.62f, 0.5f);

        [Tooltip("在固定锚点上的像素微调，一般留 0")]
        [SerializeField] private Vector2 _screenOffset = Vector2.zero;

        // ---------------------------------------------------------------- 运行时

        private sealed class Candidate
        {
            public string TwinId;
            public Vector3 WorldPos;
            public float Range;
            public string Label;
            public CommandAction Action;
            public float Value;
        }

        private readonly List<Candidate> _candidates = new List<Candidate>(64);
        private Transform _player;
        private Camera _cam;
        private Canvas _canvas;
        private RectTransform _root;
        private TwinRoundedRect _bg;
        private Text _label;
        private Button _button;
        private CanvasGroup _group;
        private Candidate _current;
        private float _nextUse;

        // 扫描缓存：FindObjectsOfType 每帧跑两遍太浪费，改成 1 秒刷一次
        private TwinInteractPoint[] _points;
        private TwinAnchor[] _anchors;
        private float _scanAt;

        // 观景模式下角色控制器没被禁用，E 键会和「自由相机上升」抢 —— 干脆藏掉按钮
        private FreeCameraController _freeCam;
        private bool _freeCamLooked;

        // 淡入淡出（纯视觉，不影响点击判定）
        private float _alpha;
        private float _alphaTarget;

        // ================================================================ 生命周期

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (FindObjectOfType<TwinInteractPrompt>(true) != null) return;

            Canvas canvas = FindOverlayCanvas();
            GameObject go = new GameObject("TwinInteractPrompt", typeof(RectTransform));
            go.transform.SetParent(canvas != null ? canvas.transform : null, false);
            go.AddComponent<TwinInteractPrompt>();
            Debug.Log("[TwinInteract] 场景里没有 TwinInteractPrompt，已自动补一个");
        }

        private static Canvas FindOverlayCanvas()
        {
            Canvas[] all = FindObjectsOfType<Canvas>(true);
            for (int i = 0; i < all.Length; i++)
                if (all[i] != null && all[i].renderMode == RenderMode.ScreenSpaceOverlay) return all[i];

            GameObject go = new GameObject("TwinInteractCanvas", typeof(RectTransform));
            Canvas canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 95;

            CanvasScaler scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            if (go.GetComponent<GraphicRaycaster>() == null) go.AddComponent<GraphicRaycaster>();
            return canvas;
        }

        private void Awake()
        {
            Build();
        }

        private void Build()
        {
            _root = transform as RectTransform;
            if (_root == null) { Debug.LogError("[TwinInteract] 必须挂在有 RectTransform 的物体上"); return; }

            // 按钮钉在屏幕固定位置：锚点 + 像素微调，**不再**每帧跟 WorldToScreenPoint 跑。
            // 跟着物体跑的话，转视角时按钮会在屏幕上乱飘，看着像 bug。
            _root.anchorMin = _root.anchorMax = _screenAnchor;
            _root.pivot = new Vector2(0.5f, 0.5f);
            _root.anchoredPosition = _screenOffset;
            _root.sizeDelta = _size;

            _bg = TwinUIFactory.Rounded(_root, "Bg", _size.y * 0.5f,
                new Color(0.13f, 0.14f, 0.16f, 0.92f), new Color(0.13f, 0.14f, 0.16f, 0.92f),
                new Color(1f, 1f, 1f, 0.22f), 1f, true);
            TwinUIFactory.Stretch(_bg.rectTransform);

            // 左边一个小圆，里面是交互键字母
            Image icon = TwinUIFactory.Block(_root, "Icon", new Color(0.16f, 0.72f, 0.52f, 1f), false);
            RectTransform ir = icon.rectTransform;
            ir.anchorMin = ir.anchorMax = new Vector2(0f, 0.5f);
            ir.pivot = new Vector2(0.5f, 0.5f);
            ir.sizeDelta = new Vector2(24f, 24f);
            ir.anchoredPosition = new Vector2(18f, 0f);

            Text key = TwinUIFactory.Label(ir, "Key", 13, TextAnchor.MiddleCenter, Color.white);
            TwinUIFactory.Stretch(key.rectTransform);
            key.text = _key.ToString();

            _label = TwinUIFactory.Label(_root, "Label", 15, TextAnchor.MiddleLeft, Color.white);
            TwinUIFactory.Place(_label.rectTransform, 38f, 0f, _size.x - 46f, _size.y);

            _button = _root.gameObject.AddComponent<Button>();
            _button.transition = Selectable.Transition.None;
            _button.onClick.AddListener(Use);

            _group = _root.gameObject.AddComponent<CanvasGroup>();
            _group.alpha = 0f;
            _group.blocksRaycasts = false;
            _group.interactable = false;
        }

        private void Update()
        {
            if (_root == null) return;

            if (_player == null)
            {
                TwinPlayerRef();
                if (_player == null) { Hide(); TickFade(); return; }
            }
            if (_cam == null) _cam = Camera.main;
            if (_cam == null) { Hide(); TickFade(); return; }

            // 观景模式下角色原地不动，按钮没有意义，还会和 E 键抢
            if (!_freeCamLooked)
            {
                _freeCamLooked = true;
                _freeCam = FindObjectOfType<FreeCameraController>();
            }
            if (_freeCam != null && _freeCam.enabled) { Hide(); TickFade(); return; }

            Candidate best = FindBest();
            _current = best;

            if (best == null) Hide();
            else Show(best);

            TickFade();
        }

        private void TickFade()
        {
            if (_group == null) return;
            _alpha = Mathf.MoveTowards(_alpha, _alphaTarget, Time.unscaledDeltaTime * 9f);
            _group.alpha = _alpha;

            bool live = _alphaTarget > 0.5f;
            _group.blocksRaycasts = live;
            _group.interactable = live;
        }

        private void TwinPlayerRef()
        {
            SchoolTwin.Player.TwinPlayerController pc = FindObjectOfType<SchoolTwin.Player.TwinPlayerController>();
            if (pc != null) _player = pc.transform;
        }

        // ================================================================ 找目标

        private Candidate FindBest()
        {
            _candidates.Clear();
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

            if (_candidates.Count == 0) return null;

            // 取「距离最近 + 在身前」的那个
            Vector3 p0 = _player.position;
            Vector3 fwd = _player.forward; fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.0001f) fwd = Vector3.forward;

            Candidate best = null;
            float bestScore = float.MaxValue;

            for (int i = 0; i < _candidates.Count; i++)
            {
                Candidate c = _candidates[i];
                Vector3 to = c.WorldPos - p0;
                float dist = to.magnitude;
                if (dist > c.Range) continue;

                to.y = 0f;
                if (to.sqrMagnitude > 0.0001f && Vector3.Dot(to.normalized, fwd) < _facingDot) continue;

                if (dist < bestScore) { bestScore = dist; best = c; }
            }
            return best;
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
                TwinValue v;
                bool open = has && anchor.TryGetValue("Open", out v) && v.Value >= 0.5f;
                c.Value = open ? 0f : 1f;
                if (string.IsNullOrEmpty(label)) c.Label = "开关门";
            }
            else
            {
                c.Action = CommandAction.SetPower;
                TwinValue v;
                bool on = has && anchor.TryGetValue("Power", out v) && v.Value >= 0.5f;
                c.Value = on ? 0f : 1f;
                if (string.IsNullOrEmpty(label)) c.Label = on ? "关灯" : "开灯";
            }

            if (!string.IsNullOrEmpty(label)) c.Label = label;
            _candidates.Add(c);
        }

        // ================================================================ 显示 / 交互

        private void Show(Candidate c)
        {
            // 按钮位置是**固定**的（见 Build），这里只做「目标是否真在画面里」的可见性判断，
            // 免得背后的东西也弹按钮。位置每帧重设的话，转视角时按钮会在屏幕上乱飘。
            Vector3 world = c.WorldPos + Vector3.up * 0.9f;
            Vector3 sp = _cam.WorldToScreenPoint(world);
            if (sp.z <= 0f) { Hide(); return; }                                     // 相机背后
            if (sp.x < 0f || sp.x > _cam.pixelWidth ||
                sp.y < 0f || sp.y > _cam.pixelHeight) { Hide(); return; }           // 跑出画面

            if (!string.Equals(_label.text, c.Label, StringComparison.Ordinal)) _label.text = c.Label;

            _alphaTarget = 1f;

            if (Input.GetKeyDown(_key)) Use();
        }

        private void Hide()
        {
            if (_group == null) return;
            _alphaTarget = 0f;
            _current = null;
        }

        private void Use()
        {
            if (_current == null) return;
            if (Time.unscaledTime < _nextUse) return;
            _nextUse = Time.unscaledTime + _cooldown;

            TwinCommandCenter center = TwinCommandCenter.Ensure();
            if (center == null) return;

            string reject;
            if (!center.TrySend(_current.TwinId, _current.Action, _current.Value, out reject))
            {
                Debug.LogWarning("[TwinInteract] " + _current.TwinId + " 被拒绝：" + reject);
                return;
            }

            Debug.Log("[TwinInteract] " + _current.Label + " → " + _current.TwinId +
                      " " + _current.Action + "(" + _current.Value + ")");

            // 立刻翻转，下一次按就是反向（回读确认后会被真实状态覆盖）
            _current.Value = _current.Value >= 0.5f ? 0f : 1f;
        }
    }
}

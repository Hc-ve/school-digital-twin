using System;
using System.Collections.Generic;
using SchoolTwin.Command;
using SchoolTwin.Player;
using SchoolTwin.Present;
using SchoolTwin.Twin;
using UnityEngine;
using UnityEngine.UI;

namespace SchoolTwin.UI
{
    /// <summary>
    /// 【P26】机房主脑的多按钮交互面板。
    ///
    /// ── 为什么不用 `TwinInteractPrompt` ────────────────────────────────
    /// 那个组件是**单按钮**的：取范围内最近的一个点位，显示一个键位提示，按一次发一条命令。
    /// 主脑需要「开机 / 联动开机 / 投影」三选一，开机后还要换成
    /// 「打开PPT / 视频播放 / 关机」——单按钮表达不了，所以单独做一个面板。
    ///
    /// 主脑的 `TwinInteractPoint` 仍然保留（射线拾取、`TwinInteractPrompt` 那边已加跳过），
    /// 这样"三维里点得到主脑"这件事不受影响。
    ///
    /// ── 两个状态 ──────────────────────────────────────────────────────
    /// ```
    /// [关机]  开机 / 联动开机 / 投影
    /// [已开机] 打开PPT / 视频播放 / 关机
    /// ```
    /// 状态由 `PC/Master_01` 的 `Power` 回读决定 —— 不自己记状态，
    /// 否则用户从总控面板关了机器，这个面板还会显示"已开机"。
    ///
    /// ── 视觉规范（与现有交互按钮一致）────────────────────────────────
    /// 宽 220 / 高 44 / 间距 10 / 圆角 22 / 字号 15，
    /// 背景 `(0.13,0.14,0.16,0.92)`、描边 `(1,1,1,0.22)`，全部走 `TwinUIFactory`。
    /// </summary>
    public sealed class TwinPcMenuPanel : MonoBehaviour
    {
        private const string MasterId = "ClassComputer/PC/Master_01";
        private const string RoomId = "ClassComputer/PC/Room_01";
        private const string ProjectorId = "ClassComputer/Projector/Main_01";

        // ---- 布局 ----
        private const float BtnW = 220f;
        private const float BtnH = 44f;
        private const float BtnGap = 10f;
        private const int FontSize = 15;

        private static readonly Color BgColor = new Color(0.13f, 0.14f, 0.16f, 0.92f);
        private static readonly Color BgColor2 = new Color(0.13f, 0.14f, 0.16f, 0.92f);
        private static readonly Color BorderColor = new Color(1f, 1f, 1f, 0.22f);
        private static readonly Color TextColor = Color.white;

        // ---- 运行期 ----
        private RectTransform _root;
        private CanvasGroup _group;
        private readonly List<GameObject> _buttons = new List<GameObject>(4);

        private Transform _player;
        private Camera _cam;
        private TwinAnchor _master;
        private float _nextScan;
        private float _nextUse;
        private float _alpha;
        private float _alphaTarget;
        private int _shownState = -1;        // -1 隐藏，0 关机态，1 开机态

        /// <summary>联动开机时"机房那 18 台"延迟下发的时间点（秒）；&lt;0 = 没有待发。</summary>
        private float _pendingRoomAt = -1f;

        private const float PanelRange = 2.6f;   // 比交互点的 2.2 略大，走近就能看见
        private const float UseCooldown = 0.45f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (FindObjectOfType<TwinPcMenuPanel>(true) != null) return;

            Canvas canvas = FindOverlayCanvas();
            GameObject go = new GameObject("TwinPcMenuPanel", typeof(RectTransform));
            go.transform.SetParent(canvas != null ? canvas.transform : null, false);
            go.AddComponent<TwinPcMenuPanel>();
            Debug.Log("[P26] 已自动挂载主脑多按钮面板");
        }

        private static Canvas FindOverlayCanvas()
        {
            Canvas[] all = FindObjectsOfType<Canvas>(true);
            for (int i = 0; i < all.Length; i++)
                if (all[i] != null && all[i].renderMode == RenderMode.ScreenSpaceOverlay) return all[i];

            GameObject go = new GameObject("TwinPcCanvas", typeof(RectTransform));
            Canvas canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 96;

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
            _root = transform as RectTransform;
            if (_root == null)
            {
                Debug.LogError("[P26] TwinPcMenuPanel 必须挂在有 RectTransform 的物体上");
                return;
            }

            // 屏幕固定位置：左侧偏中，向下排。不跟物体跑（跟着跑转视角会乱飘）
            _root.anchorMin = _root.anchorMax = new Vector2(0.26f, 0.62f);
            _root.pivot = new Vector2(0f, 1f);
            _root.anchoredPosition = Vector2.zero;
            _root.sizeDelta = new Vector2(BtnW, 200f);

            _group = gameObject.AddComponent<CanvasGroup>();
            _group.alpha = 0f;
            _group.blocksRaycasts = false;
            _group.interactable = false;
        }

        // ---------------------------------------------------------------- 主循环

        private void Update()
        {
            if (_root == null) return;

            if (_player == null) { _player = FindPlayer(); if (_player == null) { Hide(); TickFade(); return; } }
            if (_cam == null) _cam = Camera.main;
            if (_cam == null) { Hide(); TickFade(); return; }

            RefreshMaster();
            if (_master == null) { Hide(); TickFade(); return; }

            // 联动开机的"机房延迟下发"
            if (_pendingRoomAt > 0f && Time.unscaledTime >= _pendingRoomAt)
            {
                _pendingRoomAt = -1f;
                Send(RoomId, 1f, "联动开机（机房）");
            }

            float dist = Vector3.Distance(_player.position, _master.transform.position);
            if (dist > PanelRange) { Hide(); TickFade(); return; }

            // 主脑必须在画面里，否则背后也弹面板
            Vector3 sp = _cam.WorldToScreenPoint(_master.transform.position + Vector3.up * 0.9f);
            if (sp.z <= 0f || sp.x < 0f || sp.x > _cam.pixelWidth || sp.y < 0f || sp.y > _cam.pixelHeight)
            {
                Hide(); TickFade(); return;
            }

            int state = IsMasterOn() ? 1 : 0;
            if (state != _shownState) Rebuild(state);

            _alphaTarget = 1f;
            TickFade();
        }

        private Transform FindPlayer()
        {
            TwinPlayerController pc = FindObjectOfType<TwinPlayerController>();
            return pc != null ? pc.transform : null;
        }

        private void RefreshMaster()
        {
            if (_master != null && Time.unscaledTime < _nextScan) return;
            _nextScan = Time.unscaledTime + 1f;

            _master = null;
            TwinRuntime rt = TwinRuntime.Instance;
            if (rt == null || rt.Registry == null) return;

            TwinAnchor a;
            if (rt.Registry.TryGet(MasterId, out a) && a != null) _master = a;
        }

        private bool IsMasterOn()
        {
            if (_master == null) return false;
            TwinValue v;
            return _master.TryGetValue("Power", out v) && v.Value > 0.5f;
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

        private void Hide()
        {
            _alphaTarget = 0f;
        }

        // ---------------------------------------------------------------- 按钮

        private void ClearButtons()
        {
            for (int i = 0; i < _buttons.Count; i++)
                if (_buttons[i] != null) Destroy(_buttons[i]);
            _buttons.Clear();
        }

        private void Rebuild(int state)
        {
            _shownState = state;
            ClearButtons();

            if (state == 0)
            {
                AddButton("开机", () => Send(MasterId, 1f, "开机"));
                AddButton("联动开机", OnLinkBoot);
                AddButton("投影", OnProject);
            }
            else
            {
                AddButton("打开PPT", () => PlayOnMasterAndProjector("master/ppt.mp4"));
                AddButton("视频播放", () => PlayOnMasterAndProjector("master/lesson.mp4"));
                AddButton("关机", () => Send(MasterId, 0f, "关机"));
            }
        }

        private void AddButton(string caption, Action onClick)
        {
            int index = _buttons.Count;

            TwinRoundedRect bg = TwinUIFactory.Rounded(_root, "Btn_" + caption, BtnH * 0.5f,
                BgColor, BgColor2, BorderColor, 1f, true);

            RectTransform rt = bg.rectTransform;
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = new Vector2(BtnW, BtnH);
            rt.anchoredPosition = new Vector2(0f, -index * (BtnH + BtnGap));

            Text label = TwinUIFactory.Label(rt, "Label", FontSize, TextAnchor.MiddleLeft, TextColor);
            TwinUIFactory.Place(label.rectTransform, 18f, 0f, BtnW - 26f, BtnH);
            label.text = caption;

            Button btn = bg.gameObject.AddComponent<Button>();
            btn.transition = Selectable.Transition.None;
            btn.onClick.AddListener(() => { if (Time.unscaledTime >= _nextUse) { _nextUse = Time.unscaledTime + UseCooldown; onClick(); } });

            _buttons.Add(bg.gameObject);
        }

        // ---------------------------------------------------------------- 动作

        private void OnLinkBoot()
        {
            // 主脑先开，机房 18 台延后 0.35s —— 模拟开机风暴。
            // 同一点位连发两条会被 `_inFlight` 拒（"上一条命令还没结束"），
            // 但 Master 和 Room 是**不同 twinId**，互不影响，所以可以这样错开。
            Send(MasterId, 1f, "开机（主脑）");
            _pendingRoomAt = Time.unscaledTime + 0.35f;
        }

        private void OnProject()
        {
            Send(MasterId, 1f, "开机（主脑）");
            Send(ProjectorId, 1f, "开投影");
        }

        private void PlayOnMasterAndProjector(string fileName)
        {
            TwinDevicePresenter p = TwinDevicePresenter.Instance;
            if (p == null) { Debug.LogWarning("[P26] 找不到表现层，无法换片"); return; }

            bool a = p.PlayPcVideo(MasterId, fileName);
            bool b = p.PlayPcVideo(ProjectorId, fileName);
            if (!a && !b)
                Debug.Log("[P26] 「" + fileName + "」没播起来 —— 素材还没放进来？" +
                          "放到 Assets/StreamingAssets/PC/master/ 下即可，不用改代码。");
        }

        private void Send(string twinId, float value, string what)
        {
            TwinCommandCenter center = TwinCommandCenter.Ensure();
            if (center == null) return;

            string reject;
            if (!center.TrySend(twinId, CommandAction.SetPower, value, out reject))
            {
                Debug.LogWarning("[P26] " + what + " → " + twinId + " 被拒绝：" + reject);
                return;
            }
            Debug.Log("[P26] " + what + " → " + twinId + " SetPower(" + value + ")");
        }
    }
}

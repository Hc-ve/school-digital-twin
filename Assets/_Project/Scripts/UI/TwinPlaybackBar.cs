using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using SchoolTwin.Data;
using SchoolTwin.Twin;

namespace SchoolTwin.UI
{
    /// <summary>
    /// 回放控制条：暂停 / 播放 · 进度拖动 · 倍速 · 已回放时长。
    ///
    /// ================================================================
    /// 它解决什么问题
    /// ================================================================
    /// `TwinPlayer` 本来只能「从头自动播到结束」，没有任何干预手段。演示时这个短板很致命：
    ///   · 想停在某台设备故障的那一秒讲两句 —— 停不住
    ///   · 想反复看同一段 —— 只能等它跑完一整圈
    ///   · 想把 20 分钟录像快速过一遍 —— 只能 1 倍速硬等
    ///
    /// 说白了：**没有控制条的回放，等于一段不能暂停的视频**，
    /// 现场没法用来讲解，只能当"动图"看。
    ///
    /// ================================================================
    /// 为什么单独一条，不塞进已有的「数据源条」
    /// ================================================================
    /// 数据源条已经排满：标记 56 + 3 个按钮 66×3 + 录像按钮 86，
    /// 右侧状态区只剩 118px，塞不下「暂停 + 进度 + 倍速 + 时长」。
    ///
    /// 而且它俩语义不同：数据源条**常驻**（永远显示当前用哪个源），
    /// 这条只在回放时**按需出现**。做成独立节点，非回放状态整条 SetActive(false)，
    /// 不占视觉空间、也不掺进数据源条的刷新节流里。
    ///
    /// ================================================================
    /// 进度条为什么自己算坐标，不用 UGUI Slider
    /// ================================================================
    /// 1. `Slider` 要配 `Handle Slide Area` / `Fill Area` 一整套子层级，且它会把
    ///    `value` 夹在 min~max 里 —— 拖拽时应该**先本地反馈**（手指跟着走），
    ///    松手才 `SeekTo`。用 `Slider` 会出现"拖着拖着被后台进度回调拽回去"的抖动。
    /// 2. 自己算 `(pointer.x 局部化) / 轨道宽`，拖动期间用 `_dragging` 屏蔽后台回写，
    ///    松手再提交。干净。
    ///
    /// 【事件为什么不能挂在本类身上——踩过】
    /// UGUI 的 Pointer 事件沿「射线命中的 Graphic」派发，且要求事件组件与那个
    /// Graphic 在**同一 GameObject** 上。而本类不是 MonoBehaviour，`_root` 也没有
    /// Graphic，所以事件接口单独放在 <see cref="TwinPlaybackDragArea"/> 里，
    /// 挂在轨道命中的那层 Image 上，只负责把屏幕坐标转交给本类。
    /// </summary>
    public class TwinPlaybackBar
    {
        // ---------------------------------------------------------------- 布局基准（未缩放）

        private const float BaseW = 540f;
        private const float BaseH = 34f;

        /// <summary>轨道相对条左缘的 x：让出「暂停按钮 24 + 间隙」。</summary>
        private const float TrackX = 38f;

        /// <summary>轨道右侧让出的宽度：时长 82 + 倍速 38 + 三处间隙 20。</summary>
        private const float TrackRightPad = 140f;

        private const float TrackH = 6f;

        // ---------------------------------------------------------------- 节点

        private RectTransform _root;
        private RectTransform _track;
        private RectTransform _fill;
        private RectTransform _knob;
        private Text _btnText;
        private Text _timeText;
        private Text _speedText;
        private TwinPlaybackDragArea _dragArea;

        // ---------------------------------------------------------------- 状态

        private TwinPlayer _player;
        private float _scale = 1f;

        /// <summary>拖动期间为 true：屏蔽后台进度回写，否则手和进度条会互相拽。</summary>
        private bool _dragging;
        private float _dragProgress;

        private float _nextTick;
        private string _cachedBtn;
        private string _cachedTime;
        private string _cachedSpeed;

        /// <summary>根节点（调用方拿它 SetActive 控制显隐）。</summary>
        public RectTransform Root { get { return _root; } }

        /// <summary>卡片总高（含与外层的间隙），供调用方推算位置。</summary>
        public float Height { get { return (BaseH + 6f) * _scale; } }

        // ================================================================ 构建

        /// <summary>
        /// 在 `parent`（浮卡根节点）左下角建控制条，浮在数据源条**上方**。
        /// `margin` / `scale` 与数据源条用同一套值，两条才会左对齐。
        /// </summary>
        /// <param name="aboveBottom">
        /// 数据源条自身的 bottom margin。本条会再往上叠一个 `BaseH*scale + 6*scale`。
        /// </param>
        public void Build(RectTransform parent, float margin, float scale,
                          float aboveBottom, int fontSize)
        {
            _scale = scale;

            float w = BaseW * scale;
            float h = BaseH * scale;

            _root = (RectTransform)TwinUIFactory.Node("PlaybackBar", parent).transform;

            TwinRoundedRect bg = TwinUIFactory.Rounded(_root, "Bg", 10f * scale,
                TwinStatusPalette.PanelTop, TwinStatusPalette.PanelBottom,
                TwinStatusPalette.PanelBorder, 1f, false);
            TwinUIFactory.Stretch(bg.rectTransform);

            // 锚左下 + pivot 左下，与数据源条同一套锚法。
            TwinUIFactory.AnchorBottomLeft(_root, margin,
                aboveBottom + BaseH * scale + 6f * scale, w, h);

            float pad = 8f * scale;

            // ---- 暂停 / 播放 ----
            float btnSize = 24f * scale;
            Image btnBg = TwinUIFactory.Block(_root, "PlayBtn", new Color(1f, 1f, 1f, 0.12f), true);
            TwinUIFactory.Place(btnBg.rectTransform, pad, (h - btnSize) * 0.5f, btnSize, btnSize);

            Button btn = btnBg.gameObject.AddComponent<Button>();
            btn.targetGraphic = btnBg;
            btn.transition = Selectable.Transition.None;
            btn.onClick.AddListener(OnTogglePause);

            _btnText = TwinUIFactory.Label(btnBg.transform, "T", fontSize,
                TextAnchor.MiddleCenter, TwinStatusPalette.TextPrimary);
            TwinUIFactory.Stretch(_btnText.rectTransform);
            _btnText.text = "||";
            _btnText.raycastTarget = false;

            // ---- 进度轨道 ----
            float trackW = w - TrackX * scale - TrackRightPad * scale;

            Image trackBg = TwinUIFactory.Block(_root, "TrackBg", TwinStatusPalette.Track, false);
            TwinUIFactory.Place(trackBg.rectTransform, TrackX * scale,
                (h - TrackH * scale) * 0.5f, trackW, TrackH * scale);

            Image fill = TwinUIFactory.Block(_root, "TrackFill",
                new Color(0.20f, 0.55f, 0.95f, 0.95f), false);
            TwinUIFactory.Place(fill.rectTransform, TrackX * scale,
                (h - TrackH * scale) * 0.5f, 0f, TrackH * scale);
            _fill = fill.rectTransform;

            // ---- 命中层：整条轨道高度扩到 20px，手指好点 ----
            //
            // 它是**唯一**挂 Pointer 事件的节点，且必须自己带 Graphic（raycast 打开）。
            // 放在 fill/knob 之后创建 → sibling 靠后 → 绘制在上面，
            // 射线优先命中它，拖动不会被 fill 挡住。
            Image hit = TwinUIFactory.Block(_root, "TrackHit",
                new Color(1f, 1f, 1f, 0f), true);
            float hitH = 20f * scale;
            TwinUIFactory.Place(hit.rectTransform, TrackX * scale,
                (h - hitH) * 0.5f, trackW, hitH);
            _track = hit.rectTransform;

            _dragArea = hit.gameObject.AddComponent<TwinPlaybackDragArea>();
            _dragArea.Owner = this;

            // ---- 游标：7×7 点，跟着 fill 右端 ----
            Image knob = TwinUIFactory.Block(_root, "Knob",
                TwinStatusPalette.TextPrimary, false);
            TwinUIFactory.Place(knob.rectTransform, TrackX * scale,
                (h - 7f * scale) * 0.5f, 7f * scale, 7f * scale);
            _knob = knob.rectTransform;
            // 游标虽然建在 hit 之后，但它 raycast:false，不会挡住拖动。

            // ---- 已回放时长 ----
            float timeX = TrackX * scale + trackW + 8f * scale;
            _timeText = TwinUIFactory.Label(_root, "Time", fontSize,
                TextAnchor.MiddleRight, TwinStatusPalette.TextSecondary);
            TwinUIFactory.Place(_timeText.rectTransform, timeX,
                (h - 18f * scale) * 0.5f, 82f * scale, 18f * scale);
            _timeText.text = "--:-- / --:--";

            // ---- 倍速 ----
            float spX = timeX + 82f * scale + 4f * scale;
            Image spBg = TwinUIFactory.Block(_root, "Speed", new Color(1f, 1f, 1f, 0.10f), true);
            TwinUIFactory.Place(spBg.rectTransform, spX, (h - 20f * scale) * 0.5f,
                38f * scale, 20f * scale);

            Button spBtn = spBg.gameObject.AddComponent<Button>();
            spBtn.targetGraphic = spBg;
            spBtn.transition = Selectable.Transition.None;
            spBtn.onClick.AddListener(OnCycleSpeed);

            _speedText = TwinUIFactory.Label(spBg.transform, "T", fontSize,
                TextAnchor.MiddleCenter, TwinStatusPalette.TextSecondary);
            TwinUIFactory.Stretch(_speedText.rectTransform);
            _speedText.text = "1x";
            _speedText.raycastTarget = false;

            Refresh();
        }

        // ================================================================ 交互

        private void OnTogglePause()
        {
            TwinPlayer p = ResolvePlayer();
            if (p == null) return;

            // 播到末尾再点，实际意图是「重看一遍」。
            // 不特判会变成「停在最后一帧、按钮显示播放」，看着像卡死。
            if (p.Progress >= 0.999f) { p.SeekToProgress(0f); p.Resume(); }
            else p.TogglePause();

            Refresh();
        }

        private void OnCycleSpeed()
        {
            TwinPlayer p = ResolvePlayer();
            if (p == null) return;

            // 档位 0.5 → 1 → 2 → 4 → 8 循环。不用滑杆：这儿只有 38px 宽，
            // 滑杆在这么窄的空间里几乎无法精确点选。
            float cur = p.Speed;
            float next;
            if (cur < 0.75f) next = 1f;
            else if (cur < 1.5f) next = 2f;
            else if (cur < 3.5f) next = 4f;
            else if (cur < 7.5f) next = 8f;
            else next = 0.5f;

            p.Speed = next;
            Refresh();
        }

        /// <summary>由命中层回调：按下即开始拖（点哪儿跳哪儿，不用先按住游标）。</summary>
        internal void OnTrackPress(Vector2 screenPos, Camera cam, bool committing)
        {
            TwinPlayer p = ResolvePlayer();
            if (p == null || _track == null) return;

            Vector2 local;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    _track, screenPos, cam, out local))
                return;

            // 【坐标约定】`TwinUIFactory.Place` 设 anchor(0,1) + pivot(0,1)，
            // 也就是 local 原点在**左上角**，向右 local.x 递增。
            // 所以百分比就是 local.x / width，不要取负。
            float w = _track.rect.width;
            float prog = w > 0.01f ? Mathf.Clamp01(local.x / w) : 0f;

            _dragProgress = prog;
            ApplyProgressVisual(prog);

            if (committing)
            {
                _dragging = false;
                p.SeekToProgress(prog);
                Refresh();
            }
            else
            {
                _dragging = true;
            }
        }

        // ================================================================ 刷新

        /// <summary>给 Update 调：每帧跟进度，0.2s 刷文字。</summary>
        public void Tick()
        {
            if (_root == null) return;

            if (!_dragging)
            {
                TwinPlayer p = ResolvePlayer();
                if (p != null) ApplyProgressVisual(p.Progress);
            }

            if (Time.unscaledTime >= _nextTick)
            {
                _nextTick = Time.unscaledTime + 0.2f;
                Refresh();
            }
        }

        private void ApplyProgressVisual(float progress)
        {
            if (_track == null || _fill == null || _knob == null) return;

            float w = _track.rect.width;
            float p = Mathf.Clamp01(progress);
            float fillW = Mathf.Max(0f, w * p);

            Vector2 fs = _fill.sizeDelta;
            _fill.sizeDelta = new Vector2(fillW, fs.y);

            // 游标居中压住 fill 右端：fill 从 TrackX 起，游标左移自身半宽
            _knob.anchoredPosition = new Vector2(
                TrackX * _scale + fillW - 3.5f * _scale,
                _knob.anchoredPosition.y);
        }

        private void Refresh()
        {
            if (_root == null) return;

            TwinPlayer p = ResolvePlayer();
            if (p == null) return;   // 播放器还没建（没点过「回放」），保持原样

            bool ended = p.Progress >= 0.999f;
            SetCached(_btnText, ref _cachedBtn, ended || p.IsPaused ? "▶" : "||");

            SetCached(_timeText, ref _cachedTime,
                FormatTime(p.PositionSeconds) + " / " + FormatTime(p.DurationSeconds));

            SetCached(_speedText, ref _cachedSpeed, p.Speed.ToString("0.#") + "x");
        }

        private static string FormatTime(float sec)
        {
            if (sec < 0f) sec = 0f;
            int total = Mathf.FloorToInt(sec);
            return (total / 60).ToString("00") + ":" + (total % 60).ToString("00");
        }

        private static void SetCached(Text t, ref string cache, string value)
        {
            if (t == null) return;
            if (cache == value) return;
            cache = value;
            t.text = value;
        }

        // ================================================================ 找播放器

        /// <summary>
        /// 播放器是运行时建的（点「回放」才 AddComponent），所以每次都要重新解析；
        /// 一旦建出来就不会消失，找到即缓存。
        /// </summary>
        private TwinPlayer ResolvePlayer()
        {
            if (_player != null) return _player;

            TwinRuntime rt = TwinRuntime.Instance;
            if (rt != null && rt.Sources != null)
            {
                for (int i = 0; i < rt.Sources.Count; i++)
                {
                    TwinPlayer hit = rt.Sources[i] as TwinPlayer;
                    if (hit != null) { _player = hit; break; }
                }
            }
            if (_player == null) _player = Object.FindObjectOfType<TwinPlayer>(true);
            return _player;
        }
    }

    /// <summary>
    /// 进度条命中层的事件代理。
    ///
    /// 【为什么需要这个类】UGUI 的 Pointer 事件沿「射线命中的 Graphic」派发，
    /// 组件必须和那个 Graphic 在**同一个 GameObject** 上才收得到。
    /// 而 <see cref="TwinPlaybackBar"/> 不是 MonoBehaviour，挂在根节点也没有 Graphic。
    /// 所以把事件接口单独放这里，只负责把屏幕坐标转交给外层。
    /// </summary>
    public class TwinPlaybackDragArea : MonoBehaviour,
        IPointerDownHandler, IDragHandler, IEndDragHandler, IPointerClickHandler
    {
        internal TwinPlaybackBar Owner;

        public void OnPointerDown(PointerEventData e)
        {
            if (Owner != null) Owner.OnTrackPress(e.position, e.pressEventCamera, false);
        }

        public void OnDrag(PointerEventData e)
        {
            if (Owner != null) Owner.OnTrackPress(e.position, e.pressEventCamera, false);
        }

        public void OnEndDrag(PointerEventData e)
        {
            if (Owner != null) Owner.OnTrackPress(e.position, e.pressEventCamera, true);
        }

        /// <summary>
        /// 【为什么还要 Click】单击轨道时手指没移动 → 不产生 Drag → 不会触发
        /// `OnEndDrag` → 进度提交不了，看起来"点了没反应"。
        /// 单击走这里提交一次（拖动过的情形 `_dragging` 已被 EndDrag 清掉，这里再提交
        /// 同一个百分比，幂等，不会让进度跳两次）。
        /// </summary>
        public void OnPointerClick(PointerEventData e)
        {
            if (Owner != null) Owner.OnTrackPress(e.position, e.pressEventCamera, true);
        }
    }
}
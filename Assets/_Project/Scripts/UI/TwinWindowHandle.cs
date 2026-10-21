using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SchoolTwin.UI
{
    /// <summary>
    /// 浮窗把手：给任意面板加「拖动 / 拉伸」能力。
    ///
    /// ── 为什么做成独立组件，而不是写进某个面板 ──────────────────────
    /// 需求是「之后面板会越来越多」。所以这里不把拖动逻辑塞进具体面板，
    /// 而是做成「贴在把手上」的通用组件：谁想要这个行为，就在那个 Graphic 上挂一个，
    /// 指定 <see cref="Window"/>（要动谁）和 <see cref="HandleMode"/>（移动还是缩放）。
    /// 面板本身不用改一行 —— 以后新增面板照样能用。
    ///
    /// ── 三个必须处理对的细节 ────────────────────────────────────────
    /// ① **必须在画布坐标系里算增量**。用 `ScreenPointToLocalPointInRectangle` 拿画布局部点，
    ///    它已经把 CanvasScaler 的 scaleFactor 折进去了。直接拿 `eventData.delta`（屏幕像素）
    ///    去加 `anchoredPosition`，在 125% 显示缩放的机器上会跑得比鼠标快 1.25 倍。
    /// ② **一次拖动内用「起点 + 累计位移」，不要每帧累加**。每帧 `+=` 会把浮点误差攒起来，
    ///    更要命的是被 Clamp 夹住之后起点信息就永久丢了 —— 表现为拖到边上再往回拖不动。
    /// ③ **锚点要从「右上」切成「左上」**。窗口原本靠 `anchor=(1,1)` 贴右、pivot 也在右上，
    ///    这时增大 sizeDelta.x 是往**左**长 —— 拖右下角反而变窄，反直觉。
    ///    用户一动手就切成自由定位（左上锚点 + 左上 pivot），之后移动和缩放都符合直觉。
    /// </summary>
    [AddComponentMenu("SchoolTwin/浮窗把手 (TwinWindowHandle)")]
    public class TwinWindowHandle : MonoBehaviour,
        IBeginDragHandler, IDragHandler, IEndDragHandler,
        IPointerEnterHandler, IPointerExitHandler
    {
        public enum Mode
        {
            /// <summary>拖动整个窗口（挂在标题栏上）。</summary>
            Move = 0,
            /// <summary>拖右下角改大小。</summary>
            ResizeBottomRight = 1,
        }

        [Tooltip("要移动/缩放的节点。留空则取自己")]
        public RectTransform Window;

        public Mode HandleMode = Mode.Move;

        [Tooltip("缩放时的最小尺寸（UI 单位）")]
        public Vector2 MinSize = new Vector2(300f, 140f);

        [Tooltip("鼠标悬停时染成什么颜色。留空（HoverTarget 为 null）就不做悬停反馈")]
        public Graphic HoverTarget;

        [Tooltip("悬停色。默认是极淡的白，看起来像「这块可以抓」")]
        public Color HoverColor = new Color(1f, 1f, 1f, 0.07f);

        [Tooltip("拖动/缩放后回调，用来让宿主重新排版")]
        public System.Action OnChanged;

        private RectTransform _canvasRect;
        private Camera _canvasCam;
        private Vector2 _startPointer;
        private Vector2 _startPos;
        private Vector2 _startSize;
        private bool _active;

        private Color _hoverBase;
        private bool _hoverCached;

        private void Awake()
        {
            if (Window == null) Window = transform as RectTransform;
        }

        private void OnDisable()
        {
            // 拖到一半被隐藏（比如按 H 关掉面板）→ 必须清掉，否则下次 BeginDrag 前的
            // 那几帧会用上一次的起点算位移，窗口会瞬移一下
            _active = false;

            // 悬停到一半被隐藏 → 底色会永久停在悬停色上（下次显示就是一块亮条）
            RestoreHover();
        }

        // ================================================================ 悬停

        /// <summary>
        /// 悬停反馈。
        ///
        /// 【为什么需要】标题栏是透明的 —— 用户看不出它能拖。改鼠标指针形状要
        /// `Cursor.SetCursor` + 一张贴图资源，为了这点效果不值得；把底色提亮一点点
        /// 就够传达「这块可以抓」，而且零资源。
        /// </summary>
        public void OnPointerEnter(PointerEventData eventData)
        {
            if (HoverTarget == null) return;
            if (!_hoverCached)
            {
                _hoverBase = HoverTarget.color;
                _hoverCached = true;
            }
            HoverTarget.color = HoverColor;
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            RestoreHover();
        }

        private void RestoreHover()
        {
            if (HoverTarget != null && _hoverCached) HoverTarget.color = _hoverBase;
            _hoverCached = false;
        }

        // ================================================================ 拖拽

        public void OnBeginDrag(PointerEventData eventData)
        {
            if (Window == null) return;
            if (!EnsureCanvas()) return;

            FreeAnchor(Window);

            _startPos = Window.anchoredPosition;
            _startSize = Window.sizeDelta;
            _startPointer = ToCanvasLocal(eventData);
            _active = true;
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (!_active || Window == null) return;

            Vector2 d = ToCanvasLocal(eventData) - _startPointer;

            if (HandleMode == Mode.Move)
            {
                Window.anchoredPosition = _startPos + d;
            }
            else
            {
                // 左上锚点 + 左上 pivot：往右下拖 = 变大，所以高度取负
                float w = Mathf.Max(MinSize.x, _startSize.x + d.x);
                float h = Mathf.Max(MinSize.y, _startSize.y - d.y);

                // 上限 = 父节点大小。不设上限的话窗口可以拉得比画布还大，
                // 而 ClampToParent 只会把位置夹回 0，右半边就永久跑出画面了。
                RectTransform parent = Window.parent as RectTransform;
                if (parent != null)
                {
                    Rect pr = parent.rect;
                    w = Mathf.Min(w, Mathf.Max(MinSize.x, pr.width));
                    h = Mathf.Min(h, Mathf.Max(MinSize.y, pr.height));
                }

                Window.sizeDelta = new Vector2(w, h);
            }

            ClampToParent(Window);

            if (OnChanged != null) OnChanged();
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            if (!_active) return;
            _active = false;
            if (Window == null) return;
            ClampToParent(Window);
            if (OnChanged != null) OnChanged();
        }

        // ================================================================ 坐标

        private bool EnsureCanvas()
        {
            if (_canvasRect != null) return true;

            Canvas canvas = GetComponentInParent<Canvas>();
            if (canvas == null) return false;

            // 用 rootCanvas：ScreenSpaceOverlay 下它的 RectTransform 就代表整个屏幕，
            // 局部坐标单位 == UI 单位（scaleFactor 已经折算过）
            Canvas root = canvas.rootCanvas;
            _canvasRect = root.transform as RectTransform;
            _canvasCam = root.renderMode == RenderMode.ScreenSpaceOverlay ? null : root.worldCamera;
            return _canvasRect != null;
        }

        private Vector2 ToCanvasLocal(PointerEventData eventData)
        {
            Vector2 p = Vector2.zero;
            if (_canvasRect == null) return p;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                _canvasRect, eventData.position, _canvasCam, out p);
            return p;
        }

        // ================================================================ 工具

        /// <summary>
        /// 把节点从「锚右上」切成「自由定位：anchor=(0,1) + pivot=(0,1)」，
        /// 并保持它左上角的世界位置不动（视觉上不跳）。
        ///
        /// 换锚点/轴心时 Unity 不会自动保持位置，必须手动把世界坐标写回去 ——
        /// 否则窗口会「跳」一下，跳多远取决于原来离右上角多远。
        /// </summary>
        public static void FreeAnchor(RectTransform rt)
        {
            if (rt == null) return;

            bool alreadyFree =
                Mathf.Approximately(rt.anchorMin.x, 0f) && Mathf.Approximately(rt.anchorMin.y, 1f) &&
                Mathf.Approximately(rt.anchorMax.x, 0f) && Mathf.Approximately(rt.anchorMax.y, 1f) &&
                Mathf.Approximately(rt.pivot.x, 0f) && Mathf.Approximately(rt.pivot.y, 1f);
            if (alreadyFree) return;

            Vector3[] c = new Vector3[4];
            rt.GetWorldCorners(c);          // 0=左下 1=左上 2=右上 3=右下
            Vector3 topLeft = c[1];

            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);

            // pivot 现在是左上角，所以 position 就代表左上角的位置
            rt.position = topLeft;
        }

        /// <summary>
        /// 把窗口夹在父节点范围内。要求 anchor=(0,1)+pivot=(0,1)（<see cref="FreeAnchor"/> 保证）。
        ///
        /// 用父节点 rect 的**宽高**而不是 xMin/xMax：父节点的 pivot 可能是 (0.5,0.5)
        /// （画布根节点就是这样），rect 的 xMin 是负的，直接拿 xMin 当左边界会算错。
        /// </summary>
        public static void ClampToParent(RectTransform rt)
        {
            if (rt == null) return;
            RectTransform parent = rt.parent as RectTransform;
            if (parent == null) return;

            Rect pr = parent.rect;
            Vector2 size = rt.sizeDelta;
            Vector2 pos = rt.anchoredPosition;

            float maxX = Mathf.Max(0f, pr.width - size.x);
            float maxDown = Mathf.Max(0f, pr.height - size.y);

            // 至少留一条边在画面里，否则用户拖出去就再也抓不回来了。
            // 这里选择「完全不许出去」——比「留一条缝」更省事，也不会丢窗口。
            pos.x = Mathf.Clamp(pos.x, 0f, maxX);
            pos.y = Mathf.Clamp(pos.y, -maxDown, 0f);

            rt.anchoredPosition = pos;
        }
    }
}

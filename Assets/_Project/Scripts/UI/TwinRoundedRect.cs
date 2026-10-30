using UnityEngine;
using UnityEngine.UI;

namespace SchoolTwin.UI
{
    /// <summary>
    /// 程序化圆角矩形（UGUI Graphic）。
    ///
    /// 为什么不用 Sprite 九宫格：
    ///   · 九宫格要把「圆角半径」换算成像素单位（pixelsPerUnitMultiplier / referencePixelsPerUnit），
    ///     在不同 CanvasScaler 缩放比下圆角粗细会漂移，调起来全靠试；
    ///   · 这里直接按 UI 单位生成网格，半径就是半径，与分辨率无关，且零纹理资源、零导入步骤。
    ///
    /// 支持：上下渐变填充 + 1px 描边 + 任意圆角半径（半径 ≥ 半高即自动变成胶囊/圆）。
    /// 性能：顶点数 ≈ 4×(段数+1)×2+1，默认 57 个顶点，静态面板上可忽略。
    /// 刷新走 SetVerticesDirty()，只在值真正变化时调用。
    /// </summary>
    [AddComponentMenu("SchoolTwin/UI/圆角矩形 (TwinRoundedRect)")]
    public class TwinRoundedRect : MaskableGraphic
    {
        [SerializeField] private float _radius = 12f;
        [SerializeField] private Color _fillTop = new Color(0.078f, 0.106f, 0.157f, 0.93f);
        [SerializeField] private Color _fillBottom = new Color(0.055f, 0.075f, 0.114f, 0.95f);
        [SerializeField] private bool _gradient = true;
        /// <summary>
        /// 渐变方向：false = 垂直（默认，顶→底），true = 横向（左→右）。
        /// 横向只服务一处：告警卡顶部 2px 红条「左端 100% → 右端 0%」（P29-3.5 规格四）。
        /// 全项目其余渐变**仍然只能垂直**（P29 §6.3），不要拿它做斜向/径向。
        /// </summary>
        [SerializeField] private bool _horizontal;
        [SerializeField] private Color _borderColor = new Color(0.165f, 0.212f, 0.282f, 0.71f);
        [SerializeField] private float _borderWidth = 1f;
        [Range(1, 12)]
        [SerializeField] private int _cornerSegments = 6;

        private readonly System.Collections.Generic.List<Vector2> _outer =
            new System.Collections.Generic.List<Vector2>(64);
        private readonly System.Collections.Generic.List<Vector2> _inner =
            new System.Collections.Generic.List<Vector2>(64);

        public float Radius { get { return _radius; } }
        public Color FillTop { get { return _fillTop; } }
        public Color FillBottom { get { return _fillBottom; } }

        // ---------------------------------------------------------------- 对外设置

        public void SetRadius(float radius)
        {
            radius = Mathf.Max(0f, radius);
            if (Mathf.Abs(_radius - radius) < 0.01f) return;
            _radius = radius;
            SetVerticesDirty();
        }

        public void SetFill(Color color)
        {
            SetFill(color, color, false, false);
        }

        public void SetFill(Color top, Color bottom)
        {
            SetFill(top, bottom, true, false);
        }

        /// <summary>
        /// 横向渐变：<paramref name="left"/> 在左端、<paramref name="right"/> 在右端。
        /// 唯一用途是告警卡顶部条的「红 → 透明」淡出（规格四要求横向线性，不是垂直）。
        /// 右端请传同色 `WithAlpha(c, 0f)` 而不是 `Color.clear` ——
        /// 后者是 (0,0,0,0)，插值中段会先变暗再透明，看起来像一条脏边。
        /// </summary>
        public void SetFillH(Color left, Color right)
        {
            SetFill(left, right, true, true);
        }

        private void SetFill(Color top, Color bottom, bool gradient, bool horizontal)
        {
            if (Same(_fillTop, top) && Same(_fillBottom, bottom)
                && _gradient == gradient && _horizontal == horizontal) return;
            _fillTop = top;
            _fillBottom = bottom;
            _gradient = gradient;
            _horizontal = horizontal;
            SetVerticesDirty();
        }

        public void SetBorder(Color color, float width)
        {
            width = Mathf.Max(0f, width);
            if (Same(_borderColor, color) && Mathf.Abs(_borderWidth - width) < 0.01f) return;
            _borderColor = color;
            _borderWidth = width;
            SetVerticesDirty();
        }

        /// <summary>整体透明度缩放（悬停高亮、淡入淡出用），不改动已存的两个颜色。</summary>
        public void SetAlpha(float alpha)
        {
            alpha = Mathf.Clamp01(alpha);
            Color t = _fillTop;
            Color b = _fillBottom;
            float ta = t.a * alpha;
            float ba = b.a * alpha;
            if (Mathf.Abs(ta - t.a) < 0.002f && Mathf.Abs(ba - b.a) < 0.002f) return;
            t.a = ta;
            b.a = ba;
            // 必须把 _horizontal 一起传进去：默认参数会把它重置成 false，
            // 结果是一次 SetAlpha 就把横向渐变悄悄改回垂直（告警顶部条会变成"上亮下暗"）。
            SetFill(t, b, _gradient, _horizontal);
        }

        private static bool Same(Color a, Color b)
        {
            return Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b) + Mathf.Abs(a.a - b.a) < 0.002f;
        }

        // ---------------------------------------------------------------- 网格生成

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();

            Rect rect = GetPixelAdjustedRect();
            float w = rect.width;
            float h = rect.height;
            if (w <= 0.01f || h <= 0.01f) return;

            float radius = Mathf.Min(_radius, Mathf.Min(w, h) * 0.5f);
            // 半径为 0 时退化成 4 个顶点的普通矩形，省掉无意义的细分
            int seg = radius < 0.5f ? 0 : Mathf.Clamp(_cornerSegments, 2, 12);

            BuildOutline(rect.xMin, rect.yMin, rect.xMax, rect.yMax, radius, seg, _outer);

            int n = _outer.Count;
            if (n < 3) return;

            // ---------- 填充：中心点 + 外圈扇形三角化，按 y（或 x）做渐变 ----------
            Color32 centerColor = ToColor32(GradientAt(rect, rect.center.x, rect.center.y));
            vh.AddVert(new Vector3(rect.center.x, rect.center.y, 0f), centerColor, Vector2.zero);

            for (int i = 0; i < n; i++)
            {
                Vector2 p = _outer[i];
                vh.AddVert(new Vector3(p.x, p.y, 0f), ToColor32(GradientAt(rect, p.x, p.y)), Vector2.zero);
            }

            for (int i = 0; i < n; i++)
            {
                int a = 0;
                int b = 1 + i;
                int c = 1 + ((i + 1) % n);
                vh.AddTriangle(a, b, c);
            }

            // ---------- 描边：外圈 → 内圈之间连一圈四边形 ----------
            if (_borderWidth <= 0.01f) return;

            float bw = Mathf.Min(_borderWidth, Mathf.Min(w, h) * 0.5f);
            float innerRadius = Mathf.Max(0f, radius - bw);
            BuildOutline(rect.xMin + bw, rect.yMin + bw, rect.xMax - bw, rect.yMax - bw,
                         innerRadius, seg, _inner);

            if (_inner.Count != n) return;

            Color32 borderColor = ToColor32(_borderColor);
            int innerStart = 1 + n;

            for (int i = 0; i < n; i++)
            {
                Vector2 o0 = _outer[i];
                Vector2 o1 = _outer[(i + 1) % n];
                Vector2 i0 = _inner[i];
                Vector2 i1 = _inner[(i + 1) % n];

                vh.AddVert(new Vector3(o0.x, o0.y, 0f), borderColor, Vector2.zero);
                vh.AddVert(new Vector3(o1.x, o1.y, 0f), borderColor, Vector2.zero);
                vh.AddVert(new Vector3(i1.x, i1.y, 0f), borderColor, Vector2.zero);
                vh.AddVert(new Vector3(i0.x, i0.y, 0f), borderColor, Vector2.zero);

                int v0 = innerStart + i * 4;
                vh.AddTriangle(v0, v0 + 1, v0 + 2);
                vh.AddTriangle(v0, v0 + 2, v0 + 3);
            }
        }

        /// <summary>
        /// 取某点的填充色。垂直渐变时 `_fillTop` 是顶端、`_fillBottom` 是底端；
        /// 横向渐变时 `_fillTop` 是**左端**、`_fillBottom` 是**右端**（复用同一对字段，
        /// 由 `_horizontal` 决定方向 —— 不为一个 2px 的告警条再加两个序列化字段）。
        /// </summary>
        private Color GradientAt(Rect rect, float x, float y)
        {
            if (!_gradient) return _fillTop;

            if (_horizontal)
            {
                float tx = rect.width <= 0.01f ? 0f : Mathf.Clamp01((x - rect.xMin) / rect.width);
                return Color.Lerp(_fillTop, _fillBottom, tx);
            }

            float t = rect.height <= 0.01f ? 0f : Mathf.Clamp01((y - rect.yMin) / rect.height);
            return Color.Lerp(_fillBottom, _fillTop, t);
        }

        private static Color32 ToColor32(Color c)
        {
            return c;
        }

        /// <summary>
        /// 生成逆时针闭合轮廓：右→上→左→下 四个角的圆弧依次拼接。
        /// 每个角输出 seg+1 个点，角与角之间自然形成直线边（不需要额外补点）。
        /// seg = 0 时直接输出 4 个角点，得到普通矩形。
        /// </summary>
        private static void BuildOutline(float xMin, float yMin, float xMax, float yMax,
                                        float radius, int seg, System.Collections.Generic.List<Vector2> output)
        {
            output.Clear();

            if (seg <= 0)
            {
                output.Add(new Vector2(xMax, yMax));
                output.Add(new Vector2(xMin, yMax));
                output.Add(new Vector2(xMin, yMin));
                output.Add(new Vector2(xMax, yMin));
                return;
            }

            float r = radius;
            // 四个圆心的顺序：右上 → 左上 → 左下 → 右下（逆时针）
            float cx0 = xMax - r, cy0 = yMax - r;
            float cx1 = xMin + r, cy1 = yMax - r;
            float cx2 = xMin + r, cy2 = yMin + r;
            float cx3 = xMax - r, cy3 = yMin + r;

            AppendArc(output, cx0, cy0, r, 0f, seg);
            AppendArc(output, cx1, cy1, r, 90f, seg);
            AppendArc(output, cx2, cy2, r, 180f, seg);
            AppendArc(output, cx3, cy3, r, 270f, seg);
        }

        private static void AppendArc(System.Collections.Generic.List<Vector2> output,
                                     float cx, float cy, float r, float startDegrees, int seg)
        {
            for (int i = 0; i <= seg; i++)
            {
                float a = (startDegrees + 90f * i / seg) * Mathf.Deg2Rad;
                output.Add(new Vector2(cx + Mathf.Cos(a) * r, cy + Mathf.Sin(a) * r));
            }
        }

        // ---------------------------------------------------------------- 编辑器默认值

        protected override void Awake()
        {
            base.Awake();
            // 顶点色由本类自己写入，Graphic.color 不参与，固定为白色避免误解
            color = Color.white;
        }

#if UNITY_EDITOR
        protected override void OnValidate()
        {
            base.OnValidate();
            _radius = Mathf.Max(0f, _radius);
            _borderWidth = Mathf.Max(0f, _borderWidth);
            SetVerticesDirty();
        }
#endif
    }
}

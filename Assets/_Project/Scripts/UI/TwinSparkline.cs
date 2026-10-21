using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace SchoolTwin.UI
{
    /// <summary>
    /// 指标行里的迷你趋势线（UGUI Graphic，零纹理资源）。
    ///
    /// 【它回答什么问题】详情面板原来只显示「当前值」，看的人只能知道此刻是多少，
    /// 不知道是刚涨上来、还是已经这样飘了半分钟。趋势线补的就是这一句
    /// 「过去这段时间它怎么变的」—— 这也正是数字孪生相对监控大屏的差别所在。
    ///
    /// 【为什么按「量程」归一化，而不是按「这段数据的最大最小值」】
    /// 按数据自适应的话，一条几乎不变的水平线会被放大成剧烈波动，
    /// 看起来像出事了。按量程归一化，纵轴含义和下面的量程条一致，
    /// 线的位置本身就能读出「在正常区的哪一段」。
    ///
    /// 【时间轴是滚动的，但刚打开时先把已有的铺满】
    /// 横轴上限是「最近 WindowSeconds 秒」。但如果一开始就按满窗画，
    /// 刚采到 3 秒数据时，3 个点只占右边缘几个像素 —— 看起来就是「没画」。
    /// 所以跨度取「已有数据的时间跨度」，上限才是 WindowSeconds：
    /// 开头几秒铺满整条宽度，采满一窗之后才开始正常向左滚动。
    ///
    /// 性能：每帧重算一次顶点（默认 120 个采样点 → 约 500 个顶点），
    /// 只在可见时由 TwinDetailPanel 以 10Hz 触发，开销可忽略。
    /// </summary>
    public sealed class TwinSparkline : MaskableGraphic
    {
        [Tooltip("横轴时间窗（秒）。默认 60 秒")]
        [SerializeField] private float _windowSeconds = 60f;

        [Tooltip("采样点上限。超出后丢弃最老的。60 秒 @5Hz ≈ 300 点，留点余量")]
        [SerializeField] private int _capacity = 320;

        [Tooltip("线条粗细（UI 单位）")]
        [SerializeField] private float _lineWidth = 2f;

        [Tooltip("线条颜色")]
        [SerializeField] private Color _lineColor = new Color(0.55f, 0.84f, 1f, 1f);

        [Tooltip("线条下方的填充色（半透明，别调太实，否则会盖住背景）")]
        [SerializeField] private Color _fillColor = new Color(0.45f, 0.78f, 1f, 0.14f);

        [Tooltip("底部基线颜色")]
        [SerializeField] private Color _baseColor = new Color(1f, 1f, 1f, 0.16f);

        private struct Sample
        {
            public float T;   // Time.unscaledTime
            public float V;
        }

        private readonly List<Sample> _samples = new List<Sample>(256);

        private float _min;
        private float _max = 1f;
        private float _now;
        private bool _loggedFirstDraw;

        /// <summary>当前缓存了多少个采样点。用于诊断「线是空的」到底是没数据还是没画。</summary>
        public int SampleCount { get { return _samples.Count; } }

        protected override void Awake()
        {
            base.Awake();
            raycastTarget = false;   // 趋势线不该拦住点选
            color = Color.white;     // Graphic.color 只作乘法因子，实际颜色在 OnPopulateMesh 里给
        }

        /// <summary>设定纵轴的量程（一般直接传指标的 Min/Max）。</summary>
        public void SetRange(float min, float max)
        {
            _min = min;
            _max = max;
            if (_max - _min < 0.0001f) _max = _min + 1f;
            SetVerticesDirty();
        }

        /// <summary>清空历史（切换点位时调用）。</summary>
        public void Clear()
        {
            _samples.Clear();
            SetVerticesDirty();
        }

        /// <summary>压入一个采样点。</summary>
        public void Push(float time, float value)
        {
            if (_samples.Count >= _capacity) _samples.RemoveAt(0);

            Sample s;
            s.T = time;
            s.V = value;
            _samples.Add(s);

            // 第一次攒够两个点、真正能画出线的时候打一行日志。
            // 「趋势线是空的」有两种完全不同的原因（没数据 / 没画出来），
            // 有这一行就能一次分清，不用再靠猜。
            if (!_loggedFirstDraw && _samples.Count >= 2)
            {
                _loggedFirstDraw = true;
                Debug.Log("[TwinUI] 趋势线开始出图（该指标已采到 2 个点）。" +
                          "若此时仍看不到线，问题在绘制而不在数据。");
            }
        }

        /// <summary>
        /// 推进时间并重绘。
        ///
        /// 【为什么要单独一个 Tick】时间轴是滚动的：即使这一刻没有新采样，
        /// 整条线也要往左挪。不推进时间的话，数据停了线就冻住，看不出「已经很久没更新」。
        /// </summary>
        public void Tick(float time)
        {
            _now = time;

            // 【这里故意不丢老点】
            // 早先的写法是「丢掉滑出 60 秒的点」，看着合理，实际有两个坑：
            //   ① 数据一停，老点被一路丢到只剩 1 个 → `_samples.Count < 2` → 整条线消失。
            //      而「数据停了」恰恰是最该看见的时刻，线消失等于把信息藏了。
            //   ② 丢点会让 `_samples[0].T` 跟着往前跳，跨度算法每次重新开始，画面会抖。
            // 内存由 `_capacity`（320 点 ≈ 64 秒）兜住，`Push` 里已经负责丢最老的，
            // 所以这里什么都不用做 —— 只推进时间，让整条线自己往左滚。
            SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();

            Rect r = GetPixelAdjustedRect();
            if (r.width <= 0.5f || r.height <= 0.5f) return;

            // 基线：让「零轴」有参照，也让空数据时不是一片空白
            AddQuad(vh, r.xMin, r.yMin, r.xMax, r.yMin + 1f, _baseColor);

            if (_samples.Count < 2) return;

            // 【为什么时间窗不是固定的 60 秒】固定窗口下，刚开始只有几秒数据时，
            // 所有采样点都挤在右边缘几个像素里 —— 看起来就是「什么都没画」。
            // 改成「跨度 = 已有数据的时间跨度」（上限 60 秒）：
            //   · 刚采到 3 秒数据 → 这 3 秒铺满整条宽度，立刻能看出形状
            //   · 采满 60 秒后 → 开始正常向左滚动
            //   · 数据断了 → _now 继续往前走，跨度被撑大，线自然向左压缩变短，
            //     一眼就能看出「数据停了」，而不是冻住假装没变。
            float t0 = _samples[0].T;
            float span = Mathf.Clamp(_now - t0, 1f, _windowSeconds);
            float left = _now - span;

            float range = Mathf.Max(0.0001f, _max - _min);

            int n = _samples.Count;
            Vector2 prev = Vector2.zero;
            bool hasPrev = false;

            for (int i = 0; i < n; i++)
            {
                Sample s = _samples[i];
                float tx = (s.T - left) / span;
                if (tx < -0.02f) continue;          // 太老，不画
                float vy = Mathf.Clamp01((s.V - _min) / range);

                Vector2 p;
                p.x = r.xMin + Mathf.Clamp01(tx) * r.width;
                p.y = r.yMin + vy * r.height;

                if (hasPrev)
                {
                    AddSegment(vh, prev, p);
                    AddFillQuad(vh, prev, p, r.yMin);
                }

                prev = p;
                hasPrev = true;
            }
        }

        /// <summary>把一段线画成一个四边形（UI 没有线图元，只能用面片拼）。</summary>
        private void AddSegment(VertexHelper vh, Vector2 a, Vector2 b)
        {
            Vector2 d = b - a;
            if (d.sqrMagnitude < 0.0001f) return;

            Vector2 n = new Vector2(-d.y, d.x).normalized * (_lineWidth * 0.5f);

            int i0 = vh.currentVertCount;
            AddVert(vh, a - n, _lineColor);
            AddVert(vh, a + n, _lineColor);
            AddVert(vh, b + n, _lineColor);
            AddVert(vh, b - n, _lineColor);

            vh.AddTriangle(i0, i0 + 1, i0 + 2);
            vh.AddTriangle(i0, i0 + 2, i0 + 3);
        }

        /// <summary>线段下方的半透明填充：让「高低」更容易一眼看出。</summary>
        private void AddFillQuad(VertexHelper vh, Vector2 a, Vector2 b, float bottom)
        {
            int i0 = vh.currentVertCount;
            AddVert(vh, new Vector2(a.x, bottom), _fillColor);
            AddVert(vh, a, _fillColor);
            AddVert(vh, b, _fillColor);
            AddVert(vh, new Vector2(b.x, bottom), _fillColor);

            vh.AddTriangle(i0, i0 + 1, i0 + 2);
            vh.AddTriangle(i0, i0 + 2, i0 + 3);
        }

        private void AddQuad(VertexHelper vh, float x0, float y0, float x1, float y1, Color c)
        {
            int i0 = vh.currentVertCount;
            AddVert(vh, new Vector2(x0, y0), c);
            AddVert(vh, new Vector2(x0, y1), c);
            AddVert(vh, new Vector2(x1, y1), c);
            AddVert(vh, new Vector2(x1, y0), c);

            vh.AddTriangle(i0, i0 + 1, i0 + 2);
            vh.AddTriangle(i0, i0 + 2, i0 + 3);
        }

        private static void AddVert(VertexHelper vh, Vector2 pos, Color c)
        {
            UIVertex v = UIVertex.simpleVert;
            v.position = pos;
            v.color = c;
            v.uv0 = Vector2.zero;
            vh.AddVert(v);
        }
    }
}

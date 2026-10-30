using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace SchoolTwin.UI
{
    /// <summary>
    /// 【P29-1】零资源的「科技风」绘图工具。所有贴图都是代码烘出来的，全项目共用一份缓存。
    ///
    /// ── 它解决什么 ──────────────────────────────────────────────────
    /// §6.2 要求面板有 24px 网格、垂直渐变、双层发光描边、四角括号。这四样都不许用图片资源，
    /// 于是只有两条路：要么每个面板各自 new Texture2D（打开 10 次面板 = 10 张同样的贴图，
    /// 内存和 GC 都不答应），要么把「生成一次、全项目复用」这件事收在一个地方。
    /// 这个类就是那个地方。
    ///
    /// ── 缓存策略 ────────────────────────────────────────────────────
    /// · 网格：按 (cell, major) 做 key，**同一组参数全项目只有一张 Texture2D**；
    /// · 渐变：按「顶色 + 底色」量化后做 key，上限 32 组（超过就整体丢弃重建，
    ///   因为调色板只有 TwinTheme 里那几组，正常不会触发）；
    /// · 括号线段：不生成任何贴图 —— UGUI 的 Image 在 sprite == null 时
    ///   会退化成实心四边形（见 TwinUIFactory.Block 的注释），一条线就是一次 draw，
    ///   8 条线同材质可合批，比多一张白图更省。
    ///
    /// ── 使用顺序（很重要）────────────────────────────────────────────
    /// 面板的构建顺序必须是：背景 → `ApplyGrid` → 内容。
    /// 本类不会去抢 sibling 位置（抢了反而会在"背景也是子节点"的面板里被压在底下），
    /// 所以网格就按调用时机插进层级里，晚于它的内容才画在网格之上。
    ///
    /// ── 一个必须知道的限制 ──────────────────────────────────────────
    /// 运行时 new 出来的贴图不是资源，**不会随场景保存**。所以这些绘制必须在
    /// 面板构建时（Awake / 打开面板）执行一次，不能指望在编辑器里摆好存盘。
    /// 本项目所有 UI 都是代码构建的，天然满足。
    /// </summary>
    public static class TwinPaint
    {
        private const string GridChildName = "_Grid";
        private const string GlowOuterChildName = "_GlowOuter";
        private const string GlowInnerChildName = "_GlowInner";
        private const string BracketChildName = "_Brackets";
        private const string LeftBarChildName = "_LeftBar";

        /// <summary>发光外扩量：外层描边向内收回到基础矩形边缘，正好与内层描边同心。</summary>
        private const float GlowPad = 3f;

        /// <summary>渐变贴图高度。64 行足够 720 高的面板平滑过渡，再多是浪费。</summary>
        private const int GradientRows = 64;

        private static readonly Dictionary<long, Sprite> s_gridCache = new Dictionary<long, Sprite>(4);
        private static readonly Dictionary<long, Sprite> s_gradientCache = new Dictionary<long, Sprite>(8);

        // ================================================================ 网格

        /// <summary>
        /// 网格底纹 Sprite：每 <paramref name="cell"/> 单位一条细线（GridLine），
        /// 每 <paramref name="major"/> 单位一条主线（GridMajor）。**全项目只生成一次**。
        ///
        /// 【P29-3.5 裁决 C-01】**只保留 120 主线，删除 24 细线。**
        /// 传 `cell: 0`（默认值）即"不画细线"，此时贴图尺寸 = major（120×120）。
        /// 理由：投影仪缩放下 24px 细线会产生摩尔纹，并且抢面板的视觉权重 ——
        /// 面板缩略图看过去应该是"纯色深面板"，不是工程图纸。
        ///
        /// 贴图尺寸不是固定 64×64，而是 cell 与 major 的最小公倍数（默认 24/120 → 120×120）。
        /// 原因：Tiled 的 Image 是按「贴图尺寸」平铺的，若贴图边长不是 cell 的整数倍，
        /// 24px 的线会在每次接缝处变成 24/24/16 这样不均匀的间隔 —— 一眼就能看出来。
        /// </summary>
        public static Sprite GridSprite(int cell = 0, int major = 120)
        {
            // cell <= 1 视为「不画细线」。这里不能沿用 Mathf.Max(2, cell) —— 那会把 0 变成 2，
            // 于是"只要主线"的请求反而画出满屏 2px 的密纹，比原来的 24 细线还糟。
            bool hasMinor = cell >= 2;
            major = Mathf.Max(hasMinor ? cell : 2, major);

            long key = ((long)cell << 32) ^ (uint)major;
            Sprite cached;
            if (s_gridCache.TryGetValue(key, out cached) && cached != null) return cached;

            // 没有细线时，贴图边长就等于 major —— 平铺一次正好一个主格，接缝永远落在主线上
            int tile = hasMinor ? TileSize(cell, major) : major;
            Texture2D tex = new Texture2D(tile, tile, TextureFormat.RGBA32, false);
            tex.name = hasMinor ? "TwinGrid_" + cell + "_" + major : "TwinGridMajor_" + major;
            tex.wrapMode = TextureWrapMode.Repeat;   // Tiled 必须 Repeat，否则接缝处采样到边缘像素
            tex.filterMode = FilterMode.Bilinear;    // CanvasScaler 非整数缩放时，Point 会让线「断」掉
            tex.anisoLevel = 0;

            Color32 line = TwinTheme.GridLine;
            Color32 majorColor = TwinTheme.GridMajor;
            Color32 clear = new Color32(255, 255, 255, 0);

            Color32[] pixels = new Color32[tile * tile];
            for (int y = 0; y < tile; y++)
            {
                bool rowMajor = y % major == 0;
                bool rowLine = hasMinor && y % cell == 0;
                for (int x = 0; x < tile; x++)
                {
                    Color32 c = clear;
                    if (rowMajor || x % major == 0) c = majorColor;
                    else if (rowLine || (hasMinor && x % cell == 0)) c = line;
                    pixels[y * tile + x] = c;
                }
            }

            tex.SetPixels32(pixels);
            tex.Apply(false, false);

            Sprite sprite = Sprite.Create(tex, new Rect(0f, 0f, tile, tile),
                                          new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
            sprite.name = tex.name;

            s_gridCache[key] = sprite;
            return sprite;
        }

        /// <summary>平铺尺寸：取 cell 与 major 的最小公倍数，保证两个周期都能对齐接缝。</summary>
        private static int TileSize(int cell, int major)
        {
            int lcm = cell / Gcd(cell, major) * major;
            if (lcm > 0 && lcm <= 512) return lcm;

            // 兜底（极端参数，正常不会走到）：退成 cell 的整数倍且不小于 64，
            // 此时主线周期会跟着贴图走，视觉上仍有主次，只是不再是 120。
            return cell * Mathf.Max(1, Mathf.CeilToInt(64f / cell));
        }

        private static int Gcd(int a, int b)
        {
            while (b != 0)
            {
                int t = a % b;
                a = b;
                b = t;
            }
            return Mathf.Max(1, a);
        }

        /// <summary>
        /// 给面板铺网格：挂一个 Image 子节点（Tiled + GridSprite），不可点击，alpha 可整体调。
        /// 重复调用只会更新 alpha，**不会重建贴图、不会重复建节点**。
        ///
        /// 【C-01】内部固定走 `GridSprite(cell: 0, major: 120)` —— 只有 120 主线。
        /// 调用点不需要改，也不允许传 24 进来（细线已被裁决删除）。
        /// </summary>
        public static Image ApplyGrid(RectTransform rt, float alpha = 1f)
        {
            if (rt == null) return null;

            Image image = FindChild<Image>(rt, GridChildName);
            if (image == null)
            {
                image = TwinUIFactory.Graphic<Image>(rt, GridChildName);
                image.sprite = GridSprite(cell: 0, major: 120);
                image.type = Image.Type.Tiled;
                image.raycastTarget = false;
                TwinUIFactory.Stretch(image.rectTransform);
            }

            image.color = TwinTheme.WithAlpha(Color.white, Mathf.Clamp01(alpha));
            return image;
        }

        // ================================================================ 渐变

        /// <summary>
        /// 垂直渐变（**只允许垂直**，§6.3）。优先走 TwinRoundedRect 的顶点色，
        /// 它本来就是为这件事写的（一次 draw、无贴图）；只有普通 Image 才退化成 1×N 渐变贴图。
        /// </summary>
        public static void VerticalGradient(Graphic g, Color top, Color bottom)
        {
            if (g == null) return;

            TwinRoundedRect rounded = g as TwinRoundedRect;
            if (rounded != null)
            {
                rounded.SetFill(top, bottom);
                return;
            }

            Image image = g as Image;
            if (image != null)
            {
                image.sprite = GradientSprite(top, bottom);
                image.type = Image.Type.Simple;
                image.color = Color.white;   // 颜色已经烘进贴图，再乘一次会变暗
                return;
            }

            // 其它 Graphic 没有顶点色能力，给个近似色，至少不会画成白块
            g.color = top;
        }

        /// <summary>1×64 垂直渐变贴图（按颜色对缓存）。横向拉伸不影响观感，因为是纯 1px 宽。</summary>
        private static Sprite GradientSprite(Color top, Color bottom)
        {
            long key = (ColorKey(top) << 32) | (ColorKey(bottom) & 0xFFFFFFFFL);
            Sprite cached;
            if (s_gradientCache.TryGetValue(key, out cached) && cached != null) return cached;

            if (s_gradientCache.Count >= 32) DropCache(s_gradientCache);

            Texture2D tex = new Texture2D(1, GradientRows, TextureFormat.RGBA32, false);
            tex.name = "TwinGradient";
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            tex.anisoLevel = 0;

            for (int y = 0; y < GradientRows; y++)
            {
                float t = GradientRows <= 1 ? 0f : (float)y / (GradientRows - 1);
                tex.SetPixel(0, y, Color.Lerp(bottom, top, t));   // 贴图 y=0 在底部
            }
            tex.Apply(false, false);

            Sprite sprite = Sprite.Create(tex, new Rect(0f, 0f, 1f, GradientRows),
                                          new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
            sprite.name = tex.name;

            s_gradientCache[key] = sprite;
            return sprite;
        }

        private static long ColorKey(Color c)
        {
            long r = (byte)(Mathf.Clamp01(c.r) * 255f);
            long g = (byte)(Mathf.Clamp01(c.g) * 255f);
            long b = (byte)(Mathf.Clamp01(c.b) * 255f);
            long a = (byte)(Mathf.Clamp01(c.a) * 255f);
            return (r << 24) | (g << 16) | (b << 8) | a;
        }

        // ================================================================ 双层描边（发光）

        /// <summary>
        /// 三层发光公式的②③（§3.4）：
        /// 外层 = 半径 radius+3、描边 outerW、色 outer（Accent@0.10），向外扩 3 让出位置；
        /// 内层 = 半径 radius、描边 1px、色 inner（Accent@0.55），压在基础矩形边缘。
        /// 两层都 raycastTarget=false，不参与点击。
        ///
        /// 外层之所以要外扩：若两层同尺寸，3px 的外层描边会把 1px 的内层描边整条盖住，
        /// 屏幕上只剩一层淡淡的边，「发光」就没了。
        /// </summary>
        public static void DoubleStroke(RectTransform rt, float radius, Color outer, float outerW, Color inner)
        {
            if (rt == null) return;

            float pad = Mathf.Max(1f, outerW);

            TwinRoundedRect glow = GetOrCreateStroke(rt, GlowOuterChildName);
            StretchInset(glow.rectTransform, pad);
            glow.SetFill(Color.clear);
            glow.SetRadius(radius + pad);
            glow.SetBorder(outer, outerW);
            glow.raycastTarget = false;

            TwinRoundedRect edge = GetOrCreateStroke(rt, GlowInnerChildName);
            StretchInset(edge.rectTransform, 0f);
            edge.SetFill(Color.clear);
            edge.SetRadius(radius);
            edge.SetBorder(inner, 1f);
            edge.raycastTarget = false;
        }

        // ================================================================ 四角括号

        /// <summary>
        /// 四角各两条 2px 线段（主入口、触发钮的「取景框」记号）。
        /// 用 8 个 Image 拼：sprite == null 时 Image 本身就是实心四边形，不需要任何贴图。
        /// </summary>
        public static void CornerBrackets(RectTransform rt, float len, Color c, float inset = 6f)
        {
            if (rt == null) return;

            const float thickness = 2f;
            len = Mathf.Max(thickness, len);

            RectTransform root = FindChild<RectTransform>(rt, BracketChildName);
            if (root == null)
            {
                GameObject go = TwinUIFactory.Node(BracketChildName, rt);
                root = (RectTransform)go.transform;
                TwinUIFactory.Stretch(root);

                // 四个角 × 两条边。锚点与 pivot 都贴在同一个角上，
                // 这样父节点改尺寸时括号自动跟着角走，不需要重算坐标。
                string[] corners = { "TL", "TR", "BL", "BR" };
                for (int i = 0; i < corners.Length; i++)
                {
                    Vector2 corner = new Vector2(
                        (corners[i][1] == 'L') ? 0f : 1f,
                        (corners[i][0] == 'T') ? 1f : 0f);

                    Segment(root, corners[i] + "_H", corner, corner, true);
                    Segment(root, corners[i] + "_V", corner, corner, false);
                }
            }

            // 位置 / 尺寸 / 颜色每次同步（同一面板换尺寸或换色时不必重建节点）
            for (int i = 0; i < root.childCount; i++)
            {
                RectTransform seg = (RectTransform)root.GetChild(i);
                Image img = seg.GetComponent<Image>();
                if (img == null) continue;

                bool horizontal = seg.name.EndsWith("_H");
                float sx = seg.pivot.x == 0f ? 1f : -1f;
                float sy = seg.pivot.y == 0f ? 1f : -1f;

                seg.anchoredPosition = new Vector2(sx * inset, sy * inset);
                seg.sizeDelta = horizontal ? new Vector2(len, thickness) : new Vector2(thickness, len);
                img.color = c;
            }
        }

        private static void Segment(RectTransform parent, string name, Vector2 anchor, Vector2 pivot,
                                    bool horizontal)
        {
            Image img = TwinUIFactory.Block(parent, name, Color.white, false);
            RectTransform rt = img.rectTransform;
            rt.anchorMin = anchor;
            rt.anchorMax = anchor;
            rt.pivot = pivot;
            rt.sizeDelta = horizontal ? new Vector2(10f, 2f) : new Vector2(2f, 10f);
        }

        // ================================================================ 边缘色条

        /// <summary>
        /// 左缘竖条（抽屉唯一的边界信号，§4.1：2px、Accent@0.38、通高）。
        /// 竖向拉满父节点，宽度固定 —— 面板高度变化时不需要重算。
        /// </summary>
        public static Image LeftBar(RectTransform rt, float width, Color color)
        {
            if (rt == null) return null;

            Image img = FindChild<Image>(rt, LeftBarChildName);
            if (img == null)
            {
                img = TwinUIFactory.Block(rt, LeftBarChildName, color, false);
                RectTransform bar = img.rectTransform;
                bar.anchorMin = new Vector2(0f, 0f);
                bar.anchorMax = new Vector2(0f, 1f);
                bar.pivot = new Vector2(0f, 0.5f);
                bar.anchoredPosition = Vector2.zero;
            }

            RectTransform r = img.rectTransform;
            r.sizeDelta = new Vector2(Mathf.Max(1f, width), 0f);
            img.color = color;
            img.raycastTarget = false;
            return img;
        }

        // ================================================================ 缓存维护

        /// <summary>
        /// 丢掉所有缓存贴图。正常流程用不到；退出 Play 模式后 Unity 不会回收这些贴图，
        /// 由下面的初始化钩子调用一次，避免「改一次代码跑一次」攒出一堆孤儿 Texture2D。
        /// </summary>
        public static void ResetCache()
        {
            DropCache(s_gridCache);
            DropCache(s_gradientCache);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetCacheOnPlay()
        {
            ResetCache();
        }

        private static void DropCache(Dictionary<long, Sprite> cache)
        {
            foreach (KeyValuePair<long, Sprite> pair in cache)
            {
                Sprite sprite = pair.Value;
                if (sprite == null) continue;
                Texture2D tex = sprite.texture;
                TwinUIFactory.DestroySafe(sprite);
                TwinUIFactory.DestroySafe(tex);
            }
            cache.Clear();
        }

        // ================================================================ 小工具

        private static TwinRoundedRect GetOrCreateStroke(RectTransform rt, string name)
        {
            TwinRoundedRect found = FindChild<TwinRoundedRect>(rt, name);
            if (found != null) return found;

            found = TwinUIFactory.Rounded(rt, name, TwinTheme.R8, Color.clear, Color.clear, Color.clear, 0f, false);
            return found;
        }

        /// <summary>铺满父节点，并在四周再向外扩（正值）/ 内缩（负值）<paramref name="grow"/> 单位。</summary>
        private static void StretchInset(RectTransform rt, float grow)
        {
            if (rt == null) return;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = new Vector2(-grow, -grow);
            rt.offsetMax = new Vector2(grow, grow);
        }

        private static T FindChild<T>(RectTransform parent, string name) where T : Component
        {
            if (parent == null) return null;
            Transform child = parent.Find(name);
            return child == null ? null : child.GetComponent<T>();
        }

        // ================================================================ 圆环（甜甜圈）

        private static readonly Dictionary<int, Sprite> s_ringCache = new Dictionary<int, Sprite>(4);

        /// <summary>
        /// 圆环贴图：白色实心环，内外透明。配合 `Image.Type.Filled + Radial360` 画甜甜圈。
        ///
        /// 为什么代码生成而不是引图片：项目是"零图片资源"约束（所有 UI 都是代码画的）。
        /// 一张 128×128 的环只有 64 KB，生成一次缓存住，不占导入步骤。
        ///
        /// <paramref name="innerRatio"/> 是内径/外径 —— 0.62 大约是一圈"能手写百分比"的厚度，
        /// 太细了看不清，太粗了像圆饼。
        /// </summary>
        public static Sprite RingSprite(int size = 128, float innerRatio = 0.62f)
        {
            size = Mathf.Clamp(size, 32, 512);
            innerRatio = Mathf.Clamp(innerRatio, 0.05f, 0.95f);

            int key = size * 1000 + Mathf.RoundToInt(innerRatio * 100f);
            Sprite cached;
            if (s_ringCache.TryGetValue(key, out cached) && cached != null) return cached;

            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.name = "TwinRing_" + size;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            tex.anisoLevel = 0;

            float c = (size - 1) * 0.5f;
            float outer = c;
            float inner = c * innerRatio;

            Color32 white = new Color32(255, 255, 255, 255);
            Color32 clear = new Color32(255, 255, 255, 0);
            Color32[] px = new Color32[size * size];

            for (int y = 0; y < size; y++)
            {
                float dy = y - c;
                int row = y * size;
                for (int x = 0; x < size; x++)
                {
                    float dx = x - c;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);

                    // 边缘 1px 做线性过渡，避免小尺寸下出现锯齿轮廓
                    float a = Mathf.Clamp01(outer - d) * Mathf.Clamp01(d - inner);
                    px[row + x] = a >= 0.5f ? white : clear;
                }
            }

            tex.SetPixels32(px);
            tex.Apply(false, false);

            Sprite sp = Sprite.Create(tex, new Rect(0f, 0f, size, size),
                new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
            sp.name = tex.name;

            s_ringCache[key] = sp;
            return sp;
        }
    }
}

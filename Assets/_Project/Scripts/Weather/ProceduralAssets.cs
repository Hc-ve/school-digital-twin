using UnityEngine;

namespace SchoolTwin.Weather
{
    /// <summary>
    /// 运行时程序化生成天气系统需要的一切素材：雨丝/雪花/云噪声贴图，以及雨声/风声/雷声音频。
    ///
    /// 为什么不用现成素材：
    ///   · 零依赖 —— 工程里不用塞一堆 png/ogg，也不会有授权问题，换机器照样跑；
    ///   · 可调 —— 想要更密的雨就把贴图宽度调窄，不用回美术；
    ///   · 体积小 —— 全部在 Awake 里生成一次并静态缓存，几十毫秒的事。
    ///
    /// 所有生成物都做了静态缓存，重复调用不会重复分配。
    /// </summary>
    public static class ProceduralAssets
    {
        private const int SR = 44100;      // 采样率

        private static Texture2D _rain, _snow, _cloud, _star, _snowPatch, _wetMask, _snowDetail;
        private static Texture3D _cloud3d;
        private static AudioClip _rainSfx, _windSfx, _thunderSfx;

        // =====================================================================
        // 贴图
        // =====================================================================

        /// <summary>雨丝：8×64 的竖向细条，顶端亮、底端淡，左右带软边。</summary>
        public static Texture2D RainStreak
        {
            get
            {
                if (_rain != null) return _rain;

                const int W = 8, H = 64;
                var t = new Texture2D(W, H, TextureFormat.RGBA32, false);
                t.name = "ProcRainStreak";
                t.wrapMode = TextureWrapMode.Clamp;
                t.filterMode = FilterMode.Bilinear;

                var px = new Color32[W * H];
                for (int y = 0; y < H; y++)
                {
                    float v = y / (float)(H - 1);          // 0 = 底，1 = 顶
                    float a = Mathf.Pow(v, 1.7f);          // 顶亮底淡，像运动模糊
                    for (int x = 0; x < W; x++)
                    {
                        float dx = Mathf.Abs(x - (W - 1) * 0.5f) / ((W - 1) * 0.5f);
                        float edge = 1f - dx * dx;          // 中间实、两边虚
                        byte b = (byte)(Mathf.Clamp01(a * edge) * 255f);
                        px[y * W + x] = new Color32(255, 255, 255, b);
                    }
                }
                t.SetPixels32(px);
                t.Apply();
                _rain = t;
                return _rain;
            }
        }

        /// <summary>雪花：32×32 径向软圆，中心不透明、边缘完全淡出。</summary>
        public static Texture2D Snowflake
        {
            get
            {
                if (_snow != null) return _snow;

                const int S = 32;
                var t = new Texture2D(S, S, TextureFormat.RGBA32, false);
                t.name = "ProcSnowflake";
                t.wrapMode = TextureWrapMode.Clamp;
                t.filterMode = FilterMode.Bilinear;

                var px = new Color32[S * S];
                float c = (S - 1) * 0.5f;
                for (int y = 0; y < S; y++)
                {
                    for (int x = 0; x < S; x++)
                    {
                        float dx = (x - c) / c, dy = (y - c) / c;
                        float r = Mathf.Sqrt(dx * dx + dy * dy);
                        float a = Mathf.Clamp01(1f - r);
                        a = a * a * (3f - 2f * a);          // smoothstep，边缘更柔
                        byte b = (byte)(a * 255f);
                        px[y * S + x] = new Color32(255, 255, 255, b);
                    }
                }
                t.SetPixels32(px);
                t.Apply();
                _snow = t;
                return _snow;
            }
        }

        /// <summary>云噪声：256×256 的 4 倍频 Perlin fBm，RGB 全白，A 存云密度。</summary>
        public static Texture2D CloudNoise
        {
            get
            {
                if (_cloud != null) return _cloud;

                const int S = 256;
                var t = new Texture2D(S, S, TextureFormat.RGBA32, false);
                t.name = "ProcCloudNoise";
                t.wrapMode = TextureWrapMode.Repeat;
                t.filterMode = FilterMode.Bilinear;

                var alpha = new float[S * S];

                for (int y = 0; y < S; y++)
                {
                    for (int x = 0; x < S; x++)
                    {
                        float u = x / (float)S, v = y / (float)S;
                        float n = 0f, amp = 0.5f, freq = 3.0f;
                        for (int o = 0; o < 4; o++)
                        {
                            n += Mathf.PerlinNoise(u * freq + o * 17.3f, v * freq + o * 9.1f) * amp;
                            amp *= 0.5f;
                            freq *= 2.07f;
                        }
                        alpha[y * S + x] = n;
                    }
                }

                // 对比度拉伸：用 6%~94% 分位数把噪声铺满 0~1。
                //
                // 不用「固定阈值 + Pow」那套：那组参数依赖 Mathf.PerlinNoise 的绝对幅度，
                // 实测会让 56% 的像素落到接近 0、只有 3% 超过 0.5 —— 云穹顶变成稀稀几缕，
                // 阴天/雷雨根本铺不满天空。分位数拉伸和噪声的绝对量纲无关，换实现也稳。
                var sorted = (float[])alpha.Clone();
                System.Array.Sort(sorted);
                float lo = sorted[Mathf.FloorToInt(sorted.Length * 0.06f)];
                float hi = sorted[Mathf.FloorToInt(sorted.Length * 0.94f)];
                float inv = 1f / Mathf.Max(1e-4f, hi - lo);
                for (int i = 0; i < alpha.Length; i++)
                    alpha[i] = Mathf.Clamp01((alpha[i] - lo) * inv);

                // 水平接缝：球面 UV 的 u 方向首尾相接，必须让 x=0 与 x=S-1 取同一个值，
                // 否则会看到一条明显的竖线。做法是两端各留 fade 列，同时向「本行两端平均值」收敛：
                // 这样首尾两列严格相等（接缝连续），又保留了每行的云形态差异，
                // 不会在球面上压出一条等宽的灰色经线。
                int fade = S / 8;
                for (int y = 0; y < S; y++)
                {
                    int row = y * S;
                    float e = (alpha[row] + alpha[row + S - 1]) * 0.5f;
                    for (int i = 0; i < fade; i++)
                    {
                        float k = i / (float)fade;           // 0 → 1
                        alpha[row + i] = Mathf.Lerp(e, alpha[row + i], k);
                        alpha[row + S - 1 - i] = Mathf.Lerp(alpha[row + S - 1 - i], e, k);
                    }
                    alpha[row] = e;
                    alpha[row + S - 1] = e;
                }

                var px = new Color32[S * S];
                for (int i = 0; i < px.Length; i++)
                    px[i] = new Color32(255, 255, 255, (byte)(alpha[i] * 255f));

                t.SetPixels32(px);
                t.Apply();
                _cloud = t;
                return _cloud;
            }
        }

        /// <summary>
        /// 星空：512×512 全透明底 + 随机星点，亮度呈幂分布（多数很暗、少数很亮）。
        ///
        /// 为什么需要：换成程序化天空盒后，夜晚会变成一片纯深蓝渐变，没有星星会很假。
        /// 星星用 alpha 存亮度、RGB 全白，直接贴到球面穹顶上用普通 Alpha 混合即可。
        /// </summary>
        public static Texture2D StarField
        {
            get
            {
                if (_star != null) return _star;

                const int S = 512;
                var t = new Texture2D(S, S, TextureFormat.RGBA32, false);
                t.name = "ProcStarField";
                t.wrapMode = TextureWrapMode.Repeat;
                t.filterMode = FilterMode.Bilinear;

                var px = new Color32[S * S];
                for (int i = 0; i < px.Length; i++) px[i] = new Color32(255, 255, 255, 0);

                var rnd = new System.Random(90210);
                const int Count = 1500;
                for (int i = 0; i < Count; i++)
                {
                    int x = rnd.Next(S), y = rnd.Next(S);
                    float mag = Mathf.Pow((float)rnd.NextDouble(), 2.5f);   // 幂分布：暗星占多数
                    float bright = 0.30f + 0.70f * mag;
                    int r = mag > 0.70f ? 1 : 0;                            // 亮星画成 3×3 带柔边

                    for (int dy = -r; dy <= r; dy++)
                    {
                        for (int dx = -r; dx <= r; dx++)
                        {
                            float fall = (r == 0) ? 1f
                                      : Mathf.Clamp01(1f - (dx * dx + dy * dy) / 2.6f);
                            int xx = ((x + dx) % S + S) % S;
                            int yy = ((y + dy) % S + S) % S;
                            int idx = yy * S + xx;
                            byte a = (byte)(Mathf.Clamp01(bright * fall) * 255f);
                            if (a > px[idx].a) px[idx] = new Color32(255, 255, 255, a);
                        }
                    }
                }

                t.SetPixels32(px);
                t.Apply();
                _star = t;
                return _star;
            }
        }

        /// <summary>
        /// 地面积雪斑块：噪声密度 × 径向淡出。
        ///
        /// 为什么需要径向淡出：积雪层是一块贴地的方形平面，如果 alpha 铺到边缘，
        /// 就会看到一块"白色正方形"浮在地上。只让 alpha 在最外 10%（半径 90%~100%）淡到 0，
        /// 其余区域都是实白 —— 配合 90m 大圆盘，第一人称看过去是"整片地都白了"，
        /// 只在最外圈柔和淡出，看不出几何边界。
        /// </summary>
        public static Texture2D SnowPatch
        {
            get
            {
                if (_snowPatch != null) return _snowPatch;

                const int S = 256;
                var t = new Texture2D(S, S, TextureFormat.RGBA32, false);
                t.name = "ProcSnowPatch";
                t.wrapMode = TextureWrapMode.Clamp;
                t.filterMode = FilterMode.Bilinear;

                float c = (S - 1) * 0.5f;
                var px = new Color32[S * S];

                for (int y = 0; y < S; y++)
                {
                    for (int x = 0; x < S; x++)
                    {
                        float dx = (x - c) / c, dy = (y - c) / c;
                        float r = Mathf.Sqrt(dx * dx + dy * dy);

                        // ══════════════════════════════════════════════════════
                        // 【P13 真正的根因：Mathf.SmoothStep 被当成 GLSL 的 smoothstep 用了】
                        //
                        // 原代码： float edge = 1f - Mathf.SmoothStep(0.93f, 1f, r);
                        // 意图是"r 在 0.93~1.0 之间淡出，圆内恒为 1"。
                        //
                        // 但 Unity 的签名是 **Mathf.SmoothStep(float from, float to, float t)**，
                        // 其中 t 是 **0~1 的插值因子**，返回 from 到 to 之间的平滑插值。
                        // 它和 GLSL/HLSL 的 smoothstep(edge0, edge1, x)（x 是坐标）语义完全不同。
                        //
                        // 于是 r=0（圆心）时算出 1 - 0.93 = **0.07** ——
                        // 整片雪盘一直以 6.7% 的不透明度在画，砖面纹理自然一点不受影响。
                        // 实测印证：自检读出 "alpha 中心 = 0.067"。
                        // 这就是"地上完全没有积雪"的真凶，跟地形采样、包围盒都没关系。
                        //
                        // 正确写法：先把 r 从 [0.93, 1] 归一化到 [0, 1]，再交给 SmoothStep。
                        //   r <= 0.93 → InverseLerp 被 Clamp01 到 0 → SmoothStep(0,1,0)=0 → edge=1
                        //   r >= 1.0  → 1 → edge=0
                        // ══════════════════════════════════════════════════════
                        float edge = 1f - Mathf.SmoothStep(0f, 1f,
                                                            Mathf.InverseLerp(0.93f, 1f, r));

                        // 噪声不再参与 alpha。
                        // 之前是 alpha = edge * (0.70 + 0.30 * n)：覆盖率在 0.7~1.0 之间波动，
                        // 草地的绿褐色会透过 0~30% 透出来，整片雪看起来"发灰、发虚、一块一块"。
                        // 现在 alpha 恒为实心（只保留最外圈淡出），噪声改存进 RGB，
                        // 由着色器取 .r 做微法线颗粒 —— 有质感，但不会让地面颜色透出来。
                        float u = x / (float)S, v = y / (float)S;
                        float n = 0f, amp = 0.5f, freq = 34.0f;
                        for (int o = 0; o < 3; o++)
                        {
                            n += Mathf.PerlinNoise(u * freq + o * 13.7f, v * freq + o * 7.3f) * amp;
                            amp *= 0.5f;
                            freq *= 2.1f;
                        }
                        n = Mathf.Clamp01(n);

                        px[y * S + x] = new Color32(
                            (byte)(n * 255f), (byte)(n * 255f), (byte)(n * 255f),
                            (byte)(edge * 255f));
                    }
                }

                t.SetPixels32(px);
                t.Apply();
                _snowPatch = t;
                return _snowPatch;
            }
        }

        /// <summary>
        /// 【P13】可平铺的雪面细节噪声，给 SnowGround.shader 做"脏污"用（_DetailTex）。
        ///
        /// 为什么要单独一张，不能复用 <see cref="SnowPatch"/>：
        ///   SnowPatch 的 alpha 是"这块雪盘有多实"（径向淡出 + Clamp），
        ///   它是**遮罩**不是材质贴图。拿它按世界坐标平铺采样，UV 一超出 [0,1]
        ///   就会被 Clamp 拉伸成色带，边缘那圈 alpha 渐变还会被采进颜色里，出一环环暗圈。
        ///   一张图担两个语义不同的职责，必然有一边不合适 —— 所以拆开。
        ///
        /// 与 SnowPatch 的差异：
        ///   · wrapMode = Repeat（SnowPatch 是 Clamp）
        ///   · 无径向淡出（要平铺，不能有边）
        ///   · 4 个八度（SnowPatch 是 3 个）—— 脏斑需要更细的尺度才有"被踩脏"的碎感
        ///   · 用噪声原始值直接写 RGB，不做阈值拉伸（拉伸交给 shader 里的 (n-0.55)*2.2，
        ///     这样调脏污浓度只改 shader 一个数，不用重生成贴图）
        /// </summary>
        public static Texture2D SnowDetail
        {
            get
            {
                if (_snowDetail != null) return _snowDetail;

                const int S = 256;
                var t = new Texture2D(S, S, TextureFormat.RGBA32, false);
                t.name = "ProcSnowDetail";
                t.wrapMode = TextureWrapMode.Repeat;
                t.filterMode = FilterMode.Bilinear;

                var px = new Color32[S * S];
                for (int y = 0; y < S; y++)
                {
                    for (int x = 0; x < S; x++)
                    {
                        float u = x / (float)S, v = y / (float)S;

                        // 四层八度柏林。
                        //
                        // ⚠️【P13 修正：基准频率必须按"世界尺度"反推，不能拍一个 100 上去】
                        // shader 里采样是 tex2D(_DetailTex, worldPos.xz * 0.06)，
                        // 即一张图铺满 1/0.06 = 16.7m。
                        // 于是第 1 层的细胞尺寸 = 16.7 / freq：
                        //     freq=100 → 0.17m，第 4 层到 0.02m  ← 这是砂纸，不是脏斑
                        //     freq= 11 → 1.52m，末层 0.16m        ← 正是"一块被踩脏的地方"
                        // 原来写 100，画面会变成均匀的高频噪点，脏污完全出不来，
                        // 还会与雪面的微法线颗粒（ddx/ddy）打架产生闪烁。
                        float n = 0f, amp = 0.5f, freq = 11f;
                        for (int o = 0; o < 4; o++)
                        {
                            n += Mathf.PerlinNoise(u * freq + o * 31.7f, v * freq + o * 17.3f) * amp;
                            amp *= 0.5f;
                            freq *= 2.1f;
                        }
                        n = Mathf.Clamp01(n);

                        byte b = (byte)(n * 255f);
                        px[y * S + x] = new Color32(b, b, b, 255);
                    }
                }

                t.SetPixels32(px);
                t.Apply();
                _snowDetail = t;
                return _snowDetail;
            }
        }

        /// <summary>
        /// 雨天积水遮罩：R 通道 = 「这里有多像一个水洼」。
        ///
        /// 这张图不直接当颜色用，是给 WetGround.shader 当遮罩的：
        ///   R 高 → 水洼，几乎全不透明、反射强；
        ///   R 低 → 只有一层薄湿膜，透出原来的地面。
        /// 所以它需要的是「斑块感」而不是「云感」—— 频率要比云高得多，
        /// 且必须做阈值化，否则整片地面会均匀地糊上一层灰。
        ///
        /// 阈值用 34%~64% 分位数而不是写死常数：分位数拉伸与噪声的绝对量纲无关，
        /// 换噪声实现也不会突然变成「全是水」或「一滴没有」。
        /// </summary>
        public static Texture2D WetMask
        {
            get
            {
                if (_wetMask != null) return _wetMask;

                const int S = 512;
                var t = new Texture2D(S, S, TextureFormat.RGBA32, false);
                t.name = "ProcWetMask";
                // 【P13】从 Clamp 改成 Mirror。
                //
                // 原因：WetGround.shader 的 UV 现在由**世界坐标**算（worldPos.xz / tileSize），
                // 校园走两圈 UV 就会到 [3, 7] 这种范围。Clamp 会让整片地面采到边缘那一列像素，
                // 退化成"一条条拉伸的色带"。
                //
                // 为什么用 Mirror 而不是 Repeat：这张图是非周期的柏林噪声，
                // Repeat 会在接缝处出现明显的网格线（左边缘和右边缘的噪声值不相等）。
                // Mirror 在接缝处是镜像对称的，一阶导数连续，肉眼看不出接缝；
                // 只有"局部图案左右对称"这一条副作用，而水洼本来就不该有人眼可辨的方向性。
                t.wrapMode = TextureWrapMode.Mirror;
                t.filterMode = FilterMode.Bilinear;

                // 这张图最终拉伸铺在 40m 见方的地面上，基础频率 6 → 每周期约 6.7m；
                // 四层八度后最细的一层约 0.4m，水洼边界才有「不规则的湿地」感。
                var n = new float[S * S];
                for (int y = 0; y < S; y++)
                {
                    for (int x = 0; x < S; x++)
                    {
                        float u = x / (float)S, v = y / (float)S;
                        float acc = 0f, amp = 0.5f, freq = 6.0f;
                        for (int o = 0; o < 4; o++)
                        {
                            acc += Mathf.PerlinNoise(u * freq + o * 11.3f, v * freq + o * 5.9f) * amp;
                            amp *= 0.5f;
                            freq *= 2.05f;
                        }
                        n[y * S + x] = acc;
                    }
                }

                var sorted = (float[])n.Clone();
                System.Array.Sort(sorted);
                float lo = sorted[Mathf.FloorToInt(sorted.Length * 0.34f)];
                float hi = sorted[Mathf.FloorToInt(sorted.Length * 0.64f)];
                float inv = 1f / Mathf.Max(1e-4f, hi - lo);

                var px = new Color32[S * S];
                for (int i = 0; i < n.Length; i++)
                {
                    float k = Mathf.Clamp01((n[i] - lo) * inv);
                    float puddle = k * k * (3f - 2f * k);       // smoothstep，让水洼边界是软的
                    px[i] = new Color32((byte)(puddle * 255f), (byte)(puddle * 255f),
                                        (byte)(puddle * 255f), 255);
                }

                t.SetPixels32(px);
                t.Apply();
                _wetMask = t;
                return _wetMask;
            }
        }

        /// <summary>
        /// 云的三维噪声体（Texture3D）。
        ///
        /// 为什么必须用 3D：云要铺满整片天空，可选的采样方式只有三种 ——
        ///   · 球面 UV   → 南北极把经线收敛成一点，贴图被无限拉伸，抬头就是放射条纹；
        ///   · 三平面 2D → 没有极点，但投影本质是立方的：每 90° 图案重复一次，
        ///                 面与面的混合权重又让交界处露出整齐的格子；
        ///   · 3D 噪声   → 视线方向直接索引一个连续体积，无极点、无接缝、无平铺。
        ///
        /// 采样时用 Clamp + 半径 0.34，让查询球始终落在 [0,1]³ 内部，
        /// 所以贴图本身不需要是周期的（做真正的 3D 周期噪声要 6D 采样，不划算）。
        /// </summary>
        public static Texture3D CloudVolume
        {
            get
            {
                if (_cloud3d != null) return _cloud3d;

                const int S = 48;
                int total = S * S * S;
                var raw = new float[total];

                for (int z = 0; z < S; z++)
                {
                    float w = z / (float)S;
                    for (int y = 0; y < S; y++)
                    {
                        float v = y / (float)S;
                        for (int x = 0; x < S; x++)
                        {
                            float u = x / (float)S;
                            float n = 0f, amp = 0.5f, freq = 3.0f;

                            for (int o = 0; o < 4; o++)
                            {
                                float fx = u * freq + o * 17.3f;
                                float fy = v * freq + o * 9.1f;
                                float fz = w * freq + o * 5.7f;
                                // 三组 2D Perlin 交错，近似各向同性的 3D 噪声
                                n += Mathf.PerlinNoise(fx, fy) * amp;
                                n += Mathf.PerlinNoise(fy, fz) * amp * 0.7f;
                                n += Mathf.PerlinNoise(fz, fx) * amp * 0.7f;
                                amp *= 0.5f;
                                freq *= 2.07f;
                            }
                            raw[z * S * S + y * S + x] = n;
                        }
                    }
                }

                // 分位数拉伸：和 2D 云贴图一样，不依赖噪声的绝对幅度
                var sorted = (float[])raw.Clone();
                System.Array.Sort(sorted);
                float lo = sorted[Mathf.FloorToInt(total * 0.08f)];
                float hi = sorted[Mathf.FloorToInt(total * 0.92f)];
                float inv = 1f / Mathf.Max(1e-4f, hi - lo);

                var px = new Color32[total];
                for (int i = 0; i < total; i++)
                {
                    float a = Mathf.Clamp01((raw[i] - lo) * inv);
                    px[i] = new Color32(255, 255, 255, (byte)(a * 255f));
                }

                var t = new Texture3D(S, S, S, TextureFormat.RGBA32, false);
                t.name = "ProcCloudVolume";
                t.wrapMode = TextureWrapMode.Clamp;   // 查询球不碰边界，不需要 Repeat
                t.filterMode = FilterMode.Bilinear;
                t.SetPixels32(px);
                t.Apply();

                _cloud3d = t;
                return _cloud3d;
            }
        }

        /// <summary>云穹顶材质（3D 噪声版）。</summary>
        public static Material MakeCloudMaterial(Color tint, string name, int queue)
        {
            Shader sh = Resources.Load<Shader>("CloudDome");
            if (sh == null) sh = Shader.Find("CampusUI/CloudDome");
            if (sh == null)
            {
                Debug.LogWarning("[ProceduralAssets] 找不到 CampusUI/CloudDome，退回 2D 三平面（天空会有格子）。");
                return MakeSkyDomeMaterial(CloudNoise, tint, name, 1.35f, 0.006f, 0.30f, queue);
            }

            var m = new Material(sh);
            m.name = name;
            m.SetTexture("_Noise3D", CloudVolume);
            m.SetColor("_Color", tint);
            m.SetFloat("_Opacity", tint.a);
            // 阴天帷幔初值 0：多云档不会凭空多出一层灰纱，由 SetDome 按天气逐帧写入
            if (m.HasProperty("_Overcast")) m.SetFloat("_Overcast", 0f);
            m.SetFloat("_Radius", 0.34f);
            m.SetFloat("_Speed", 0.02f);
            m.SetFloat("_HorizonFade", 0.30f);
            m.renderQueue = queue;
            return m;
        }

        /// <summary>
        /// 天空穹顶材质（2D 三平面，给星空用）。
        ///
        /// 用 `CampusUI/SkyDome` 做**三平面投影**，避开球面 UV 的极点拉伸 ——
        /// 普通球体的 UV 在南北极会把所有经线压到一个点，贴图被无限拉伸，
        /// 抬头看就是一圈放射状条纹。三平面投影按视线方向选平面采样，没有这个奇点。
        /// 星空用它没问题（星点稀疏，立方体面的痕迹看不出来）；云层必须走 3D 版本。
        ///
        /// 放在 Resources 下并用 Resources.Load 取，是为了打包时不会被剥离
        /// （Shader.Find 在构建后只能找到 Always Included 里的着色器）。
        /// </summary>
        public static Material MakeSkyDomeMaterial(Texture2D tex, Color tint, string name,
                                                   float tiling, float speed, float horizonFade, int queue)
        {
            Shader sh = Resources.Load<Shader>("SkyDome");
            if (sh == null) sh = Shader.Find("CampusUI/SkyDome");
            if (sh == null)
            {
                Debug.LogWarning("[ProceduralAssets] 找不到 CampusUI/SkyDome，退回 Sprites/Default（天空会有极点条纹）。");
                return MakeSpriteMaterial(tex, tint, name, queue);
            }

            var m = new Material(sh);
            m.name = name;
            m.mainTexture = tex;                 // -> _MainTex
            m.SetColor("_Color", tint);
            m.SetFloat("_Opacity", tint.a);
            m.SetFloat("_Scale", tiling);
            m.SetFloat("_Speed", speed);
            m.SetFloat("_HorizonFade", horizonFade);
            m.renderQueue = queue;
            return m;
        }

        /// <summary>透明无光照材质（Sprites/Default 在 URP 下正常工作，且双面、不写深度）。</summary>
        /// <param name="queue">
        /// 渲染队列。默认 3000（Transparent）。
        /// 云穹顶和星空穹顶的球心都重合在相机上，按距离排序时距离都是 0，
        /// 队列相同时先后顺序不确定，会出现"星星盖住云"。所以用队列显式分层：
        /// 星空 2990 &lt; 云 2995 &lt; 雨雪 3000。
        /// </param>
        public static Material MakeSpriteMaterial(Texture2D tex, Color tint, string name, int queue = 3000)
        {
            // Sprites/Default 是最省心的选择：双面、Alpha 混合、不受光照影响。
            // 万一工程被裁掉了内置着色器（打包时的剥离），按 URP 的顺序兜底。
            var sh = Shader.Find("Sprites/Default");
            if (sh == null) sh = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            if (sh == null) sh = Shader.Find("Universal Render Pipeline/Unlit");
            if (sh == null)
            {
                Debug.LogWarning("[ProceduralAssets] 找不到可用的透明着色器，粒子材质可能渲染异常。");
                return null;
            }
            var m = new Material(sh);
            m.name = name;
            m.mainTexture = tex;      // URP 侧由 [MainTexture] 特性映射到 _BaseMap
            m.color = tint;           // 同上，映射到 _BaseColor
            m.renderQueue = queue;
            return m;
        }

        /// <summary>
        /// 雨雪粒子专用：可被实时灯（P44 路灯）照亮的透明材质（P44 · 2026-11-18）。
        ///
        /// 【为什么不用 MakeSpriteMaterial】Sprites/Default 的 SubShader 里写着
        /// `Lighting Off`——纯自发光，结构上收不到任何实时灯：粒子在路灯锥里
        /// 永远不亮。实拍调研（alamy《Snow falling in cones of light under
        /// streetlights》等）：现实里飘落的雪/雨在灯锥里是一大片亮白粒子，
        /// 这是雪夜路灯最有辨识度的画面。
        /// CampusUI/PrecipitationLit 的 ForwardAdd 按"各向同性散射"提亮：
        /// 亮度 = 灯色 × 距离/锥角衰减，无方向项（粒子 billboard 的法线永远
        /// 朝相机，拿它算 NdotL 是纯错误）。base pass 与 Sprites/Default
        /// 完全同观感——白天零回归。
        /// </summary>
        public static Material MakePrecipitationMaterial(Texture2D tex, Color tint, string name, int queue = 3000)
        {
            var sh = Shader.Find("CampusUI/PrecipitationLit");
            if (sh == null)
            {
                // shader 缺席时退回老路径：粒子只是"灯下不亮"，不会消失（可回滚）
                Debug.LogWarning("[ProceduralAssets] 找不到 CampusUI/PrecipitationLit，雨雪粒子退回无光照模式。");
                return MakeSpriteMaterial(tex, tint, name, queue);
            }
            var m = new Material(sh);
            m.name = name;
            m.mainTexture = tex;
            m.color = tint;
            m.renderQueue = queue;
            return m;
        }

        // =====================================================================
        // 音频（全部程序化合成，无外部音频素材）
        // =====================================================================

        /// <summary>雨声：白噪声两级低通 + 慢速幅度起伏，听起来像连续的雨幕。</summary>
        public static AudioClip RainSfx
        {
            get
            {
                if (_rainSfx != null) return _rainSfx;

                int len = SR * 6;
                var d = new float[len];
                var rnd = new System.Random(20261027);

                float lp1 = 0f, lp2 = 0f;
                for (int i = 0; i < len; i++)
                {
                    float w = (float)(rnd.NextDouble() * 2.0 - 1.0);
                    lp1 += (w - lp1) * 0.42f;      // 一阶低通
                    lp2 += (lp1 - lp2) * 0.16f;    // 再低通 → 闷一点的雨幕底噪
                    float flutter = 0.86f + 0.14f * Mathf.Sin(i * 6.2831853f / SR * 0.73f);
                    d[i] = lp2 * 1.7f * flutter;     // 两级低通把幅度压到 ~0.09，这里补回来
                }

                d = CrossfadeLoop(d, SR / 2);
                NormalizeTo(d, 0.80f);
                _rainSfx = MakeClip("ProcRain", d);
                return _rainSfx;
            }
        }

        /// <summary>风声：更重的低通 + 0.13Hz 幅度摆动，像风掠过建筑缝隙。</summary>
        public static AudioClip WindSfx
        {
            get
            {
                if (_windSfx != null) return _windSfx;

                int len = SR * 8;
                var d = new float[len];
                var rnd = new System.Random(778899);

                float lp1 = 0f, lp2 = 0f;
                for (int i = 0; i < len; i++)
                {
                    float w = (float)(rnd.NextDouble() * 2.0 - 1.0);
                    lp1 += (w - lp1) * 0.09f;
                    lp2 += (lp1 - lp2) * 0.05f;

                    float t = i / (float)SR;
                    float swell = 0.55f + 0.45f * Mathf.Sin(t * 6.2831853f * 0.13f);
                    swell *= 0.75f + 0.25f * Mathf.Sin(t * 6.2831853f * 0.37f + 1.7f);
                    d[i] = lp2 * 6.0f * swell;       // 0.09/0.05 两级低通非常狠，增益要大得多
                }

                d = CrossfadeLoop(d, SR / 2);
                NormalizeTo(d, 0.80f);
                _windSfx = MakeClip("ProcWind", d);
                return _windSfx;
            }
        }

        /// <summary>雷声：极重低通的长衰减隆隆声 + 开头一记爆裂。</summary>
        public static AudioClip ThunderSfx
        {
            get
            {
                if (_thunderSfx != null) return _thunderSfx;

                int len = SR * 3;
                var d = new float[len];
                var rnd = new System.Random(31415);

                float lp = 0f;
                for (int i = 0; i < len; i++)
                {
                    float w = (float)(rnd.NextDouble() * 2.0 - 1.0);
                    lp += (w - lp) * 0.045f;                 // 很重的低通 → 隆隆声
                    float t = i / (float)len;
                    float env = Mathf.Exp(-t * 4.2f);        // 指数衰减
                    float rumble = 0.6f + 0.4f * Mathf.Sin(t * 26f + 1.3f);
                    d[i] = lp * env * rumble * 2.6f;
                }

                // 开头 90ms 的高频爆裂，模拟雷击瞬间
                int crack = SR * 9 / 100;
                for (int i = 0; i < crack && i < len; i++)
                {
                    float t = i / (float)crack;
                    d[i] += (float)(rnd.NextDouble() * 2.0 - 1.0) * (1f - t) * 0.6f;
                }

                // 尾部 120ms 淡出：不然播到最后一帧会有一个突兀的断点
                int tailFade = SR * 12 / 100;
                for (int i = 0; i < tailFade && i < len; i++)
                {
                    int k = len - 1 - i;
                    d[k] *= i / (float)tailFade;
                }

                NormalizeTo(d, 0.95f);
                _thunderSfx = MakeClip("ProcThunder", d);
                return _thunderSfx;
            }
        }

        // =====================================================================
        // 工具
        // =====================================================================

        private static AudioClip MakeClip(string name, float[] data)
        {
            var clip = AudioClip.Create(name, data.Length, 1, SR, false);
            clip.SetData(data, 0);
            return clip;
        }

        /// <summary>
        /// 把整段音频的峰值压到指定值。
        ///
        /// 为什么必须有：低通滤波 + 固定增益的组合很容易让峰值冲过 1.0（实测风声峰值能到 1.7），
        /// 超出的部分在 SetData 里会被硬削波，听感是"噼啪"的破音。
        /// 归一化只改整体音量、不改波形，所以 flutter / swell 的起伏全部保留。
        /// </summary>
        private static void NormalizeTo(float[] d, float peak)
        {
            float max = 0f;
            for (int i = 0; i < d.Length; i++)
            {
                float a = d[i] < 0f ? -d[i] : d[i];
                if (a > max) max = a;
            }
            if (max < 1e-5f) return;

            float k = peak / max;
            for (int i = 0; i < d.Length; i++) d[i] *= k;
        }

        /// <summary>
        /// 消除循环接缝处的「咔哒」。
        ///
        /// 做法：把尾部 n 个样本与开头 n 个样本交叉混合后**写回开头**，然后把长度缩短 n。
        /// 关键在最后一步 —— 缩短后新的 d[newLen-1] 和 d[0] 在原序列里本来就是相邻样本，
        /// 所以接缝处天然连续。
        ///
        /// 反面教材（旧实现）：只把尾段向 head 混合、却不缩短长度，
        /// 结果尾段末尾趋近 d[n-1] 而循环回到 d[0]，跳变依旧存在 —— 等于白做。
        /// </summary>
        private static float[] CrossfadeLoop(float[] d, int n)
        {
            if (n <= 0 || n * 2 > d.Length) return d;

            int newLen = d.Length - n;
            for (int i = 0; i < n; i++)
            {
                float k = i / (float)n;              // 0 → 1
                d[i] = d[i] * k + d[newLen + i] * (1f - k);
            }
            System.Array.Resize(ref d, newLen);
            return d;
        }
    }
}

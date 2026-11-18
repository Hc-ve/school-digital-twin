// 地面积雪层（**内置管线 Built-in RP 版**，由 URP 版按队员文档 §2.2 对照表改写）。
//
// 改写对照（§2.2）：
//   LightMode   UniversalForward      → ForwardBase
//   include     URP Core/Lighting     → UnityCG.cginc + AutoLight.cginc
//   GetMainLight+阴影                 → _LightColor0 + UNITY_LIGHT_ATTENUATION
//   SampleSH                          → ShadeSH9(float4(normal,1))
//   MixFog / ComputeFogFactor         → UNITY_APPLY_FOG（宏内部算，不需要自己算 factor）
//   TEXTURE2D / SAMPLER / SAMPLE_*    → sampler2D / tex2D
//   TransformObjectToWorld*           → UnityWorldToClipPos / UnityObjectToWorldNormal
//
// 原来针对"雪太假 / 看着发虚"的三条修正**全部保留**（这是本 Shader 的核心价值）：
//   1) 受主光 Lambert + 球谐环境光 + 环境提亮下限，不再是 Sprites/Default 的灰绿平涂
//   2) 覆盖率 alpha 改实心，只有最外圈淡出；噪声改存 RGB，只做轻微扰动
//   3) 雪面严格按地形平铺（几何位移由 C# 的 DrapeSnow 完成），不叠加程序化雪堆
//
// ⚠️ 内外管线的一处**语义差异**，必须记下来：
//   URP 的 shadowAttenuation 在没开阴影变体时返回 1.0；
//   内置管线这边 UNITY_LIGHT_ATTENUATION 也返回 1.0，但它**要求 SHADOW_COORDS /
//   TRANSFER_SHADOW 成对出现**，漏一个会编译错或阴影全黑。下面按标准 ForwardBase 补齐。
//
// ⚠️ 内置管线的一个硬限制：ForwardBase 只承载**一盏**最亮的平行光。
//   本工程场景里 68 盏 Area 灯全是 Baked，不参与实时直射，所以这条限制不构成影响；
//   但如果以后有人把 Area 灯改成 Realtime，雪面不会受它们照亮 —— 那属于预期行为。
Shader "CampusUI/SnowGround"
{
    Properties
    {
        _SnowTex    ("Snow Coverage (A)", 2D) = "white" {}
        _DetailTex  ("Snow Detail (tileable)", 2D) = "gray" {}
        _SnowColor  ("Snow Tint", Color) = (0.92, 0.95, 1.0, 1)
        _SnowAmount ("Snow Amount", Range(0, 1)) = 0
        _SkyColor   ("Sky Tint", Color) = (0.74, 0.82, 0.96, 1)
        _EdgeFade   ("Edge Fade", Range(0.2, 1.2)) = 0.55
        _MicroNormal("Micro Normal", Range(0, 1)) = 0.35
        _AmbientBoost("Ambient Boost", Range(0, 1)) = 0.32

        // 【P13】脚印：凹痕半径与深度（由 C# 按角色脚掌尺寸写入）
        _PrintRadius("Footprint Radius (m)", Float) = 0.16
        _PrintDepth ("Footprint Depth", Range(0, 1)) = 0.55
        _PrintCount ("Footprint Count", Float) = 0
        // 脚印寿命（秒）：雪一直在下时，旧脚印会被新雪慢慢填平。
        //
        // 【P28】240 → 90。原值 240 是按"能存 4 分钟"设计的，但缓冲只有
        // 32 槽、走 5 秒（11.5m）就被覆盖 —— 寿命参数根本轮不到生效，
        // 两个数是自相矛盾的。改成 128 槽（46m / 21 秒）之后，把寿命
        // 设成 90 秒才自洽：走完一圈（21s）最早的印子还没淡完，
        // 而原地站着不动时，90 秒后整片足迹被新雪填平，也符合直觉。
        // 前 63 秒（70%）深度完全不衰减，最后 27 秒线性淡出。
        _PrintLife  ("Footprint Life (s)", Float) = 90

        // 所有有效脚印的世界 xz 包围盒（xy = min，zw = max）。
        // 由 C# 侧维护，供 FootprintAt 做整片剔除 —— 见 CGPROGRAM 里的说明。
        _PrintBounds("Footprint Bounds", Vector) = (0, 0, 0, 0)

        // ══════════════════════════════════════════════════════════════
        // 【P13 Bug3】屋顶雪壳模式开关
        //
        // 0 = 地面/树根雪盘（默认）：用 _SnowTex 的 alpha 当覆盖率，
        //     并用它的径向淡出裁掉方形边界。
        // 1 = 屋顶雪壳：**忽略 _SnowTex 的 alpha**，覆盖率恒为 1；
        //     厚薄/脏污/微法线全部改从可平铺的 _DetailTex 取。
        //
        // ── 为什么必须有这个开关（不是"顺手加个选项"）──────────────────
        // _SnowTex（SnowPatch）是**遮罩**不是贴图：wrapMode = Clamp，
        // 且 alpha 带"半径 93%~100% 淡到 0"的径向淡出。
        // 地面雪盘把整块平面铺满 [0,1] UV，所以采到的是完整的一张。
        // 但屋顶雪壳按三角形拼出来，UV 只能给**世界坐标**（否则相邻三角形
        // 之间纹理不连续）→ UV 会跑到几十的范围。
        // Clamp 之后，几十的 UV 全部钳到边缘那一列像素，而边缘 alpha ≈ 0
        // → 片元里 `if (cov <= 0.003) discard` 直接把整片屋顶扔掉。
        // 表现就是"代码跑了、日志说生成了 N 个三角形，但画面上屋顶一点雪都没有"。
        // 这和 Bug 2 的 `_EdgeFade = 1.0` 是同一种病：
        // 把"方形平面的边界处理"套用到了"不属于方形平面的几何"上。
        // ══════════════════════════════════════════════════════════════
        _MaskOff    ("Ignore Snow Mask (roof shell)", Range(0, 1)) = 0

        // ══════════════════════════════════════════════════════════════
        // 【P13 Bug3】剔除模式，默认 0 = Off（保持地面雪盘原有行为不变）
        //
        // 屋顶雪壳必须用 2（Back）：雪壳是**复制屋顶三角形**得到的，
        // 保留了原朝向。本工程建筑材质是 Standard 默认 Cull Back（单面），
        // 于是"从室内抬头看屋顶"看到的是背面 → 屋顶本身被剔除、整块消失
        // （这正是 Bug 1 纵向判据必须越过天花板的原因）。
        // 若雪壳也用 Cull Off，就会顺着这个"看不见的屋顶"露出来，
        // 在天花板上糊一片白 —— 治了室外、坏了室内。
        // 所以雪壳单独设 _Cull = 2，与它要贴的那张屋顶保持同一种剔除方式。
        // ══════════════════════════════════════════════════════════════
        _Cull       ("Cull Mode", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "RenderType" = "Transparent"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "SnowGround"
            Tags { "LightMode" = "ForwardBase" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull [_Cull]

            CGPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag

            // 主光实时阴影（角色/树的影子落在雪上）。内置管线用 SHADOWS_SCREEN 关键字，
            // 由 Unity 在方向光投影开启时自动注入，这里不用手写 multi_compile。
            #pragma multi_compile_fwdbase
            #pragma multi_compile_fog

            #include "UnityCG.cginc"
            // 【必须的三个 include，少一个都编译不过】
            //   UnityCG.cginc     —— 坐标变换、TRANSFORM_TEX、fog 宏、ShadeSH9
            //   Lighting.cginc    —— _LightColor0 / _WorldSpaceLightPos0 / _LightMatrix0
            //                        （这两个变量**不在** UnityCG 里。URP 版里它们由
            //                         Light 结构体带进来，改写时最容易漏掉的就是这一句，
            //                         表现为 d3d11 上 "undeclared identifier '_LightColor0'"）
            //   AutoLight.cginc   —— SHADOW_COORDS / TRANSFER_SHADOW / UNITY_LIGHT_ATTENUATION
            //                        它自身 include 了 UnityShadowLibrary，但**不含** _LightColor0
            #include "Lighting.cginc"
            #include "AutoLight.cginc"

            sampler2D _SnowTex;
            float4 _SnowTex_ST;
            sampler2D _DetailTex;    // 可平铺噪声，只给"脏污"采样用（世界坐标 UI）
            fixed4 _SnowColor;
            float  _SnowAmount;
            fixed4 _SkyColor;
            float  _EdgeFade;
            float  _MicroNormal;
            float  _AmbientBoost;
            float  _MaskOff;    // 【P13 Bug3】1 = 屋顶雪壳：不采 _SnowTex 的 alpha，覆盖率恒 1

            // 【P13】脚印数据。
            //
            // ── 为什么用"固定长度的数组"而不是贴图 ────────────────────────
            // 两个方案都能做，选数组的理由：
            //   · 贴图方案要一张 RenderTexture + 一个正交相机逐帧画踩踏点，
            //     等于给天气系统加一个完整的渲染通道，调试时还看不到数据；
            //   · 数组方案的数据流是 C# 一个 float4[]，随时能在自检里打印出来，
            //     而且不需要任何额外 RT、不需要管分辨率与深度。
            //
            // ── 容量定多少（P28 2026-11-05 由 32 改为 128）──────────────
            // 原值 32 的足迹带 = 32 × 0.36m = **11.5 米**，按 2.2m/s 只需 5 秒
            // 就走满一圈，最早的印子被覆盖 —— 表现为"走远往回看没了"。
            // 主理人的答辩动作就是"从教学楼门口走出一段再回头看"，11.5m 不够。
            // 128 槽 = 46.1m（走 21 秒），回头能看到一整段来路，是合适的量级。
            //
            // 为什么不是 256：46m 已超出"回头看"的实用范围，而每多一槽，
            // 每个像素就多一次常量缓冲读取。128 是容量与成本的分界。
            //
            // ⚠️ 成本必须配套优化（见下面的 _PrintBounds 剔除）：
            // 雪盘是 90m × 90m 的整屏级平面，若每个像素无条件遍历 128 次，
            // 帧率会直接掉档。所以**先做一次包围盒判定**，绝大多数像素
            // 一次比较就返回 0，只有足迹所在的那一小片才真的进循环。
            //
            // ── 为什么是 float4 而不是 float3 ─────────────────────────────
            // d3d11 的常量缓冲里数组元素按 16 字节对齐，float3 会被补齐成 float4
            // （而且多个 float3 连排时容易踩到跨寄存器读取的坑）。
            // 直接用 float4 = (worldX, worldZ, 生成时间, 强度)，不需要额外对齐处理。
            #define PRINT_MAX 128
            float4 _Prints[PRINT_MAX];   // xy = 世界 xz，z = 写入时刻，w = 强度
            float  _PrintCount;
            float  _PrintRadius;
            float  _PrintDepth;
            float  _PrintLife;
            // xy = 所有有效脚印的世界 xz **最小值**，zw = **最大值**。
            // 由 C# 侧在 EmitPrint / ResetPrints 时维护，供下面做整片剔除。
            float4 _PrintBounds;

            /// <summary>
            /// 求某世界点在所有脚印里"被踩得最深"的程度（0 = 没被踩，1 = 踩到底）。
            /// 每个脚印是一个柔边圆，取最大值而不是累加 —— 累加会让来回走两次的地方变成黑洞。
            /// </summary>
            float FootprintAt(float2 worldXZ)
            {
                if (_PrintDepth <= 0.001 || _PrintCount < 0.5) return 0.0;

                // ══════════════════════════════════════════════════════════
                // 【P28】整片剔除：不在足迹包围盒里的像素**一次比较就返回**。
                //
                // 128 槽的循环只在足迹带覆盖的那一小片像素上跑。雪盘 90m 见方、
                // 足迹带只有几米宽，命中率不到 2% —— 这一句把 98% 的像素
                // 从 128 次读取压到 4 次比较，是 32→128 能加得起的前提。
                //
                // ── 为什么"空盒"用 min > max 表示 ────────────────────────
                // C# 侧没有任何脚印时推的是 (1, 1, -1, -1)，即 min > max。
                // 这个组合**不可能**由合法足迹产生（min ≤ max 恒成立），
                // 是一枚天然的"无效"标记，不需要额外传一个 bool。
                //
                // ⚠️ 不要额外把 `min == max` 也当空盒：**只有一个脚印时**
                // 包围盒恰好退化成 min == max（一个点，x 与 z 各自相同），
                // 加上那条判据会把刚踩下的第一个印子直接剔掉 ——
                // 表现为"走一步不出现、走两步才出来"。
                // 首帧的 Properties 默认 (0,0,0,0) 不需要单独处理：
                // 那时 `_PrintCount` 是 0，函数开头就已经 return 了。
                //
                // 判据要留 _PrintRadius 的余量：包围盒是按**圆心**算的，
                // 而每个印子有半径，边缘的像素仍要算进去。
                // ══════════════════════════════════════════════════════════
                if (_PrintBounds.x > _PrintBounds.z || _PrintBounds.y > _PrintBounds.w) return 0.0;

                float2 bMin = _PrintBounds.xy - _PrintRadius;
                float2 bMax = _PrintBounds.zw + _PrintRadius;
                if (worldXZ.x < bMin.x || worldXZ.x > bMax.x) return 0.0;
                if (worldXZ.y < bMin.y || worldXZ.y > bMax.y) return 0.0;

                float best = 0.0;
                float r2 = _PrintRadius * _PrintRadius;
                float now = _Time.y;
                // 寿命衰减只在最后 30% 完成：刚踩的脚印要一直是深的，
                // 忽然开始被雪填平才像"雪在下"。
                float fadeStart = _PrintLife * 0.7;
                float fadeSpan = max(_PrintLife * 0.3, 0.01);

                // ⚠️ 循环边界必须是**编译期常量** PRINT_MAX，不能写 (int)_PrintCount。
                // d3d11 在 target 3.5 下要求循环次数可静态展开，用运行时值当上界
                // 会编译报错（或退化成极慢的软件分支）。所以定长遍历、内部跳过空槽。
                //
                // 不加 [unroll]：128 次展开会让指令数翻几十倍（编译期也会很长），
                // 而动态循环在 d3d11 上对常量上界的处理已经足够快。
                // 32→128 之后这里更不该展开 —— 展开的收益是省几条循环指令，
                // 代价是寄存器压力暴涨、可能把整个 frag 挤到 lower 掉。
                for (int i = 0; i < PRINT_MAX; i++)
                {
                    // 未使用的槽位 w = 0（C# 侧没填的初始值），直接跳过。
                    float4 p = _Prints[i];
                    if (p.w < 0.001) continue;

                    float2 d = worldXZ - p.xy;
                    float dist2 = dot(d, d);
                    if (dist2 > r2) continue;

                    // 柔边：中心 1 → 边缘 0。用二次曲线而不是 smoothstep，
                    // 便宜的代价换来更"软"的脚印边缘（雪被压出来的坑不是硬边）。
                    float t = 1.0 - dist2 / r2;
                    float m = t * t;

                    // 时间衰减：越老的脚印越浅（被新雪填平）
                    float age = now - p.z;
                    m *= 1.0 - saturate((age - fadeStart) / fadeSpan);

                    best = max(best, m * p.w);
                }
                return best;
            }

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 uv     : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos      : SV_POSITION;
                float2 uv       : TEXCOORD0;
                float3 worldPos : TEXCOORD1;
                float3 worldN   : TEXCOORD2;
                SHADOW_COORDS(3)
                UNITY_FOG_COORDS(4)
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.pos      = UnityWorldToClipPos(o.worldPos);
                o.worldN   = UnityObjectToWorldNormal(v.normal);
                o.uv       = TRANSFORM_TEX(v.uv, _SnowTex);
                TRANSFER_SHADOW(o);
                UNITY_TRANSFER_FOG(o, o.pos);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                //采样：a = 覆盖率（实心 + 最外圈淡出），r = 颗粒噪声（只做极轻微的质感扰动）
                fixed4 sn = tex2D(_SnowTex, i.uv);

                // ══════════════════════════════════════════════════════════
                // 【P13 Bug3】屋顶雪壳模式：覆盖率不采 _SnowTex 的 alpha
                //
                // 地面雪盘整块平面铺满 [0,1] UV，_SnowTex 的 alpha 径向淡出
                // 正好用来裁方形边界，是对的。
                // 屋顶雪壳的 UV 只能给世界坐标（保证相邻三角形之间纹理连续），
                // 会跑到几十的范围；Clamp 之后全钳到边缘，alpha ≈ 0
                // → 下面那句 discard 会把整片屋顶全部扔掉。
                // 所以屋顶模式下覆盖率恒 1，边缘不做二次裁剪，
                // 厚薄/脏污/颗粒一律改用可平铺的 _DetailTex（按世界坐标高频采样，
                // 天然没有边界问题）。
                // ══════════════════════════════════════════════════════════
                bool roofMode = _MaskOff > 0.5;

                float cov = roofMode ? 1.0 : sn.a;
                if (cov <= 0.003) discard;

                // 边缘淡出（双保险，贴图里已经淡过一次，这里再收一次方形边界）
                //
                // ⚠️【P13 修的真 bug：`_EdgeFade = 1.0` 会让这里算出 NaN】
                // C# 侧 BuildSnowGround 把 _EdgeFade 设成 1.0，理由是"环形边界已交给
                // SnowPatch 贴图的径向淡出，这里不再二次裁剪"。但 smoothstep(a, b, x)
                // 的实现是 (x-a)/(b-a)，a==b==1.0 时 b-a = 0：
                //   · x > 1（四角）  → +inf  → saturate = 1 → edge = 0 → 整片被 discard
                //   · x < 1（盘内）  → -inf  → saturate = 0 → edge = 1 → 正常
                //   · x == 1（正好 r=1 那一圈）→ 0/0 = NaN → saturate(NaN) 结果**取决于驱动**
                // 在 d3d11 上 NaN 经 saturate 常常输出 0，于是 alpha 被算成 0，
                // 表现就是"雪量明明是 1.0、雪盘也 active，但地面一片白都没有"。
                // 这是**未定义行为**，不能靠"恰好没事"过活，必须显式短路。
                float edge;
                if (roofMode)
                {
                    // 屋顶雪壳：按三角形拼出来的几何没有"方形边界"，
                    // 不需要、也不能做二次裁剪（会沿每个三角形的 UV 范围切出花纹）。
                    edge = 1.0;
                }
                else if (_EdgeFade < 0.999)
                {
                    float2 c = i.uv - 0.5;
                    float r = length(c) * 2.0;
                    edge = 1.0 - smoothstep(_EdgeFade, 1.0, r);
                }
                else
                {
                    // 不做二次裁剪：方形边界的柔化完全由 SnowPatch 贴图的径向淡出负责
                    edge = 1.0;
                }
                if (edge <= 0.003) discard;

                // 微法线：用颗粒噪声的空间梯度扰动法线。梯度取噪声(.r)而不是覆盖率(.a) ——
                // 覆盖率现在是实心的，取 .a 的梯度恒为 0。幅度刻意压得很小。
                //
                // 【P13 Bug3】屋顶模式下 sn.r 是 Clamp 后的常数，梯度恒 0，
                // 微法线会完全消失、屋顶雪变成一块死白玻璃。
                // 改从铺满屋顶的世界坐标采样 _DetailTex 求梯度。
                float gDX, gDY;
                if (roofMode)
                {
                    float2 dw0 = i.worldPos.xz * 0.06;
                    float d0 = tex2D(_DetailTex, dw0).r;
                    gDX = ddx(d0);
                    gDY = ddy(d0);
                }
                else
                {
                    gDX = ddx(sn.r);
                    gDY = ddy(sn.r);
                }
                float3 N = normalize(i.worldN + float3(-gDX, 0.0, -gDY) * _MicroNormal * 2.5);

                // 受光：主光 Lambert + 主光实时阴影。
                // 内置管线里主光颜色就是 _LightColor0，衰减由 UNITY_LIGHT_ATTENUATION 一次给出
                // （它同时含阴影与距离衰减；方向光无距离衰减，所以这里等于 shadow 项）。
                float3 L = normalize(_WorldSpaceLightPos0.xyz);
                float NdotL = saturate(dot(N, L));
                UNITY_LIGHT_ATTENUATION(atten, i, i.worldPos);
                float3 direct = _LightColor0.rgb * NdotL * atten;

                // 环境光：球谐已含天空/环境颜色，白天亮、阴天灰、夜晚偏蓝。
                float3 ambient = ShadeSH9(float4(N, 1.0));

                // 合成：雪色 × (直射 + 环境 + 环境提亮下限)。
                // 雪是强反照率材质，阴天弱光下只按物理直射+环境算会发灰发暗，所以给提亮下限。
                float3 lit = direct + ambient + _AmbientBoost;
                float3 col = _SnowColor.rgb * lit;

                // ══════════════════════════════════════════════════════════
                // 【P13】雪面的"脏"与"厚薄"，参照《异环》雪天实拍效果改。
                //
                // 原来只有一句 2.5% 的均匀噪声扰动（col *= 0.975 + 0.025*sn.r），
                // 结果是整片**均匀纯白**，像给地面盖了一张白纸。异环的雪地不是这样：
                //   · 路面中央被车压出**深色车辙**（雪被压实、变薄、透出底层沥青）
                //   · 路缘/树根/栏杆底部**堆得更厚**（雪被扫到边上，也更白）
                //   · 整片雪面有大尺度的明暗起伏（不是平的，会随光照有柔软的阴影）
                //   · 局部有脏污（踩踏、车轮带泥），偏灰黄
                //
                // 用一张贴图做不到这些，但用它的两个通道 + 世界坐标可以近似：
                //   sn.r 是原图的低频柏林噪声（已在 C# 侧算好，3 个八度）→ 拿来做"厚薄"
                //   sn.g 同值 → 拿来做"脏污"，但**频率要放大**，否则脏污走向会和厚薄完全一致
                // 厚度(thick) 直接改亮度：厚 → 更白更亮；薄 → 略暗、透出一点底色感
                // 脏污(dirt) 往灰黄方向偏：这是"雪被压过"的视觉签名
                // ══════════════════════════════════════════════════════════

                // 大尺度厚薄：0~1，越厚越白。
                // 用 sn.r 的一次项 + 二次项组合，让它有"局部堆成一小坨"的团块感，
                // 而不是均匀的噪声底纹（纯一次项看起来还是平）。
                //
                // 【P13 Bug3】屋顶模式下 sn.r 不可用（Clamp 后是边缘那一个常数），
                // 改从 _DetailTex 低频采样取"厚薄"，否则整片屋顶会是均匀的一个灰度，
                // 看不出雪堆起伏。
                float thickSrc = roofMode
                               ? tex2D(_DetailTex, i.worldPos.xz * 0.035).r
                               : sn.r;
                float thick = saturate(thickSrc * 1.15 - 0.10);
                float lump = thick * thick;

                // 小尺度脏污：用世界坐标高频采样**平铺**噪声图，得到一个与厚薄不同相位的图案。
                //
                // ⚠️【P13 关键：不能用 _SnowTex 做这件事】
                // _SnowTex（SnowPatch）的 wrapMode 是 Clamp、且带径向淡出 ——
                // 它的 alpha 是"这块雪盘有多实"，本质是**一张遮罩**，不是可平铺的材质贴图。
                // 拿它按世界坐标采样，一旦 UV 超出 [0,1] 就会被拉伸成色带，
                // 而且边缘那圈 alpha 渐变的像素会被采进颜色里，出现一环环的暗圈。
                //
                // 所以单独给一张可平铺的 _DetailTex：
                //   · wrapMode = Repeat（噪声天然首尾不接，但脏污是随机的，接缝看不出来）
                //   · 只有 RGB，alpha 用不到
                //   · 频率 0.06 → 一张图铺满 16.7m，配合贴图侧基准频率 11，
                //     第 1 层细胞约 1.5m、第 4 层约 0.16m —— 正好是
                //     "一块被踩脏的地方 + 边缘碎屑"的尺度。
                //     （贴图侧原来写的是 freq=100，对应 0.17m 的砂纸级噪点，
                //       脏污完全出不来，还会和下面的微法线颗粒打架闪烁，已一并修正。）
                float2 dw = i.worldPos.xz * 0.06;
                float dirtN = tex2D(_DetailTex, dw).r;
                float dirt = saturate((dirtN - 0.55) * 2.2);   // 只有噪声高处才脏，低处干净

                // 亮度：厚处 +12%，薄处 -14%（原来只有 ±2.5%，画面太平）
                col *= lerp(0.86, 1.12, thick);
                // 团块再加一点起伏，让"软雪堆"有体积感
                col *= (1.0 + lump * 0.10);

                // 脏污：往低饱和的灰黄压。为什么是黄不是灰 ——
                // 现实里被踩脏的雪混的是泥土/融雪盐，色相偏暖；用灰会变成"脏抹布"。
                // 强度上限 0.55：压过头会让雪看起来发黑，答辩投影上更明显。
                float3 dirtyTint = float3(0.52, 0.48, 0.42);
                col = lerp(col, col * dirtyTint * 1.6, dirt * 0.55);

                // 远处略偏天空色，强化"雪反射天光"的通透感。
                col = lerp(col, _SkyColor.rgb * (0.6 + 0.4 * lit), 0.22);

                // ══════════════════════════════════════════════════════════════
                // 【P13】脚印：把踩过的地方压暗 + 让表面变平（凹坑受光更少）
                //
                // 只改颜色是不够的 —— 单纯把雪压暗看起来像"地上有一摊灰"。
                // 真正的脚印有三个视觉特征，这里都做：
                //   ① 整体变暗：坑底接收的天光少（sky occlusion），压到 72%
                //   ② 变脏：鞋底把下面的泥带上来，往灰褐偏（复用上面的 dirtyTint）
                //   ③ 高光变弱：坑是内凹的，法线方向发散，踩着的地方不该有雪的柔亮
                //
                // 强度由 _PrintDepth 统一控制（0 = 完全不显示脚印），
                // 好处是"刚下雪时没有脚印、积够雪才有"可以靠 snowAmount 平滑过渡，
                // 不用在 C# 里做开关判断。
                // ══════════════════════════════════════════════════════════════
                float fp = FootprintAt(i.worldPos.xz);
                if (fp > 0.001)
                {
                    float k = fp * _PrintDepth;

                    // ① 坑底变暗：最多压到 72%
                    col *= lerp(1.0, 0.72, k);

                    // ② 带一点泥色（比周围雪更暖更灰）
                    col = lerp(col, col * float3(0.68, 0.64, 0.58) * 1.3, k * 0.45);

                    // ③ 削弱方向性受光：坑里的 NdotL 变化更平缓。
                    //    这里不改 N（改动法线会让光照跳变），而是把"直射贡献"按 k 压掉一部分，
                    //    视觉等价于"这个位置不再是一块平亮的雪面"。
                    col = lerp(col, col * 0.92 + ambient * 0.08, k);
                }

                // 雾：让远处雪面和场景雾色融在一起，雪盘远边不再" popping "出白色硬边。
                UNITY_APPLY_FOG(i.fogCoord, col);

                // 覆盖率已是实心：雪量上来后立刻变成"不透地的白"。
                // smoothstep 让刚开始下的一小段时间里由淡到浓，保留"渐进出现"的手感。
                float a = saturate(smoothstep(0.0, 0.22, _SnowAmount) * cov * edge);
                return fixed4(col, a);
            }
            ENDCG
        }

        // ══════════════════════════════════════════════════════════════════
        // 【P44 · 2026-11-18】ForwardAdd pass：让实时点光/聚光照亮雪面。
    //
    // ── 为什么必须有这个 pass（主理人报障"雪天夜里路灯一点光照痕迹都没有"）──
    // 上面 ForwardBase 只承载一盏最亮的平行光（月光）。Unity 内置管线的
    // 逐像素点光/聚光全部在 ForwardAdd 阶段渲染——本 pass 缺席时，
    // 100 盏路灯（P44）、室内实时灯全部照不亮雪面，雪地在夜里就是死灰一片。
    //
    // ── 为什么是纯增量、可随时回滚 ──────────────────────────────────
    // 原 ForwardBase pass 一行未动；本 pass 只做"加法"（Blend One One），
    // 删掉整段即回到改前行为。无雪区（cov≈0）在片元里 discard，
    // 不会出现"光透过雪照亮无雪地面"的穿帮。
    //
    // ── 为什么要 multi_compile_fwdadd_fullshadows ────────────────────
    // 普通 fwdadd 变体不含点光/聚光的实时阴影采样；P44 路灯开了 Soft
    // 阴影，树影/灯杆影要落在雪面上就必须带 fullshadows（编译变体多几个，
    // 运行时成本不变）。
    // ══════════════════════════════════════════════════════════════════
    Pass
    {
        Name "SnowGroundAdd"
        Tags { "LightMode" = "ForwardAdd" }

        Blend One One
        ZWrite Off
        ZTest LEqual
        Cull [_Cull]

        CGPROGRAM
        #pragma target 3.5
        #pragma vertex vertAdd
        #pragma fragment fragAdd

        // fwdadd 变体族（POINT / SPOT / DIRECTIONAL / *_COOKIE），
        // fullshadows 让点光立方阴影 / 聚光阴影参与衰减。
        #pragma multi_compile_fwdadd_fullshadows
        #pragma multi_compile_fog

        #include "UnityCG.cginc"
        #include "Lighting.cginc"
        #include "AutoLight.cginc"

        // 与 ForwardBase pass 同一套材质 uniform（两个 CGPROGRAM 是独立编译单元，
        // 各自声明互不冲突；数值由同一份 Material 驱动，天然一致）。
        sampler2D _SnowTex;
        float4 _SnowTex_ST;
        sampler2D _DetailTex;
        fixed4 _SnowColor;
        float  _SnowAmount;
        fixed4 _SkyColor;
        float  _EdgeFade;
        float  _MicroNormal;
        float  _AmbientBoost;
        float  _MaskOff;

        #define PRINT_MAX 128
        float4 _Prints[PRINT_MAX];
        float  _PrintCount;
        float  _PrintRadius;
        float  _PrintDepth;
        float  _PrintLife;
        float4 _PrintBounds;

        // 脚印求深：与 ForwardBase 同一份实现（见上面那份的完整注释）。
        float FootprintAt(float2 worldXZ)
        {
            if (_PrintDepth <= 0.001 || _PrintCount < 0.5) return 0.0;
            if (_PrintBounds.x > _PrintBounds.z || _PrintBounds.y > _PrintBounds.w) return 0.0;

            float2 bMin = _PrintBounds.xy - _PrintRadius;
            float2 bMax = _PrintBounds.zw + _PrintRadius;
            if (worldXZ.x < bMin.x || worldXZ.x > bMax.x) return 0.0;
            if (worldXZ.y < bMin.y || worldXZ.y > bMax.y) return 0.0;

            float best = 0.0;
            float r2 = _PrintRadius * _PrintRadius;
            float now = _Time.y;
            float fadeStart = _PrintLife * 0.7;
            float fadeSpan = max(_PrintLife * 0.3, 0.01);
            for (int i = 0; i < PRINT_MAX; i++)
            {
                float4 p = _Prints[i];
                if (p.w < 0.001) continue;
                float2 d = worldXZ - p.xy;
                float dist2 = dot(d, d);
                if (dist2 > r2) continue;
                float t = 1.0 - dist2 / r2;
                float m = t * t;
                float age = now - p.z;
                m *= 1.0 - saturate((age - fadeStart) / fadeSpan);
                best = max(best, m * p.w);
            }
            return best;
        }

        struct appdata
        {
            float4 vertex : POSITION;
            float3 normal : NORMAL;
            float2 uv     : TEXCOORD0;
        };

        struct v2fAdd
        {
            float4 pos      : SV_POSITION;
            float2 uv       : TEXCOORD0;
            float3 worldPos : TEXCOORD1;
            float3 worldN   : TEXCOORD2;
            SHADOW_COORDS(3)
            UNITY_FOG_COORDS(4)
        };

        v2fAdd vertAdd(appdata v)
        {
            v2fAdd o;
            o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
            o.pos      = UnityWorldToClipPos(o.worldPos);
            o.worldN   = UnityObjectToWorldNormal(v.normal);
            o.uv       = TRANSFORM_TEX(v.uv, _SnowTex);
            TRANSFER_SHADOW(o);
            UNITY_TRANSFER_FOG(o, o.pos);
            return o;
        }

        fixed4 fragAdd(v2fAdd i) : SV_Target
        {
            // 覆盖率与裁剪：与 ForwardBase 完全同口径（无雪区必须 discard，
            // 否则加法光会"透过雪"照亮裸地面）。
            fixed4 sn = tex2D(_SnowTex, i.uv);
            bool roofMode = _MaskOff > 0.5;
            float cov = roofMode ? 1.0 : sn.a;
            if (cov <= 0.003) discard;

            float edge;
            if (roofMode) edge = 1.0;
            else if (_EdgeFade < 0.999)
            {
                float2 c = i.uv - 0.5;
                float r = length(c) * 2.0;
                edge = 1.0 - smoothstep(_EdgeFade, 1.0, r);
            }
            else edge = 1.0;
            if (edge <= 0.003) discard;

            // 微法线：与 ForwardBase 同源（屋顶模式改用 _DetailTex 梯度）。
            float gDX, gDY;
            if (roofMode)
            {
                float2 dw0 = i.worldPos.xz * 0.06;
                float d0 = tex2D(_DetailTex, dw0).r;
                gDX = ddx(d0);
                gDY = ddy(d0);
            }
            else
            {
                gDX = ddx(sn.r);
                gDY = ddy(sn.r);
            }
            float3 N = normalize(i.worldN + float3(-gDX, 0.0, -gDY) * _MicroNormal * 2.5);

            // 【ForwardAdd 的核心区别】这里收到的是"本 pass 负责的那一盏灯"：
            //   · 平行光 → _WorldSpaceLightPos0.xyz 是方向；
            //   · 点/聚光 → 是世界坐标位置，方向要按像素重算（这就是路灯照亮雪面的入口）。
            //   · UNITY_LIGHT_ATTENUATION 同时给出距离衰减与（fullshadows 下的）实时阴影。
            #ifdef USING_DIRECTIONAL_LIGHT
                float3 L = normalize(_WorldSpaceLightPos0.xyz);
            #else
                float3 L = normalize(_WorldSpaceLightPos0.xyz - i.worldPos);
            #endif
            float NdotL = saturate(dot(N, L));
            UNITY_LIGHT_ATTENUATION(atten, i, i.worldPos);
            float3 direct = _LightColor0.rgb * NdotL * atten;

            // 厚薄/脏污/脚印的调色系数与 ForwardBase 同一套，
            // 保证"路灯照亮的雪"和"月光照亮的雪"是同一种雪。
            float thickSrc = roofMode
                           ? tex2D(_DetailTex, i.worldPos.xz * 0.035).r
                           : sn.r;
            float thick = saturate(thickSrc * 1.15 - 0.10);
            float lump = thick * thick;
            float3 col = _SnowColor.rgb * direct;
            col *= lerp(0.86, 1.12, thick);
            col *= (1.0 + lump * 0.10);
            float2 dw = i.worldPos.xz * 0.06;
            float dirtN = tex2D(_DetailTex, dw).r;
            float dirt = saturate((dirtN - 0.55) * 2.2);
            float3 dirtyTint = float3(0.52, 0.48, 0.42);
            col = lerp(col, col * dirtyTint * 1.6, dirt * 0.55);

            // 脚印在加法光下同样要"坑底收光少"，否则路灯会把脚印照没了。
            float fp = FootprintAt(i.worldPos.xz);
            if (fp > 0.001)
            {
                float k = fp * _PrintDepth;
                col *= lerp(1.0, 0.92, k);
            }

            // 加法混合下雾按 Unity 约定压向黑（UNITY_PASS_FORWARDADD 分支），
            // 远处灯照雪不会把雾色越加越亮。
            UNITY_APPLY_FOG(i.fogCoord, col);

            // 覆盖率 alpha 乘进颜色：半透明雪缘不会以全亮度加进画面。
            float a = saturate(smoothstep(0.0, 0.22, _SnowAmount) * cov * edge);
            return fixed4(col * a, 1.0);
        }
        ENDCG
    }
    }

    FallBack Off
}
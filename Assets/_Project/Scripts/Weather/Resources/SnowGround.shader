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
        // 不设成永久 —— 否则 32 个槽位环封一圈后，最老的点还亮着，
        // 会出现"走回半个操场，几十步前那脚还在"的怪现象。
        _PrintLife  ("Footprint Life (s)", Float) = 240
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
            Cull Off

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

            // 【P13】脚印数据。
            //
            // ── 为什么用"固定长度的数组"而不是贴图 ────────────────────────
            // 两个方案都能做，选数组的理由：
            //   · 贴图方案要一张 RenderTexture + 一个正交相机逐帧画踩踏点，
            //     等于给天气系统加一个完整的渲染通道，调试时还看不到数据；
            //   · 数组方案的数据流是 C# 一个 float4[]，随时能在自检里打印出来，
            //     而且不需要任何额外 RT、不需要管分辨率与深度。
            // 32 个点够不够？角色步幅约 0.7m，32 个点 ≈ 22m 的足迹，
            // 而雪盘半径 45m —— 但**只有相机附近的足迹看得见**，
            // 22m 的可见足迹已经远超屏幕范围，够用。超出的按环形缓冲淘汰最旧的。
            //
            // ── 为什么是 float4 而不是 float3 ─────────────────────────────
            // d3d11 的常量缓冲里数组元素按 16 字节对齐，float3 会被补齐成 float4
            // （而且多个 float3 连排时容易踩到跨寄存器读取的坑）。
            // 直接用 float4 = (worldX, worldZ, 生成时间, 强度)，不需要额外对齐处理。
            #define PRINT_MAX 32
            float4 _Prints[PRINT_MAX];   // xy = 世界 xz，z = 写入时刻，w = 强度
            float  _PrintCount;
            float  _PrintRadius;
            float  _PrintDepth;
            float  _PrintLife;

            /// <summary>
            /// 求某世界点在所有脚印里"被踩得最深"的程度（0 = 没被踩，1 = 踩到底）。
            /// 每个脚印是一个柔边圆，取最大值而不是累加 —— 累加会让来回走两次的地方变成黑洞。
            /// </summary>
            float FootprintAt(float2 worldXZ)
            {
                if (_PrintDepth <= 0.001 || _PrintCount < 0.5) return 0.0;

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
                // 不加 [unroll]：32 次展开会让指令数翻几十倍，而动态循环
                // 在 d3d11 上对常量上界的处理已经足够快，这里没必要换寄存器压力。
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
                // 采样：a = 覆盖率（实心 + 最外圈淡出），r = 颗粒噪声（只做极轻微的质感扰动）
                fixed4 sn = tex2D(_SnowTex, i.uv);
                float cov = sn.a;
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
                if (_EdgeFade < 0.999)
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
                float gDX = ddx(sn.r);
                float gDY = ddy(sn.r);
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
                float thick = saturate(sn.r * 1.15 - 0.10);
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
    }

    FallBack Off
}
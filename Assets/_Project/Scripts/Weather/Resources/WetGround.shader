// 雨天积水层（**内置管线 Built-in RP 版**，由 URP 版按队员文档 §2.2 对照表改写）。
//
// ══════════════════════════════════════════════════════════════════════
// 【P13 核心修法：水坑必须在世界空间静止，不能跟着角色跑】
//
// 队友原版的做法是：铺一块 40m 见方的平面，每帧把平面挪到相机脚下（WetGround.Follow），
// 再用**物体 UV** 采样水洼遮罩：
//     o.uv = TRANSFORM_TEX(v.uv, _MaskTex);
// 平面一移动，物体 UV 跟着移动 → 遮罩图案在世界空间里同步平移。
// 玩家看到的就是「水坑粘在脚边、角色走到哪水坑跟到哪」，这是最刺眼的一个穿帮。
//
// 修正：UV 直接由**世界坐标**算，平面怎么挪都不影响图案落在哪。
//     uv = (worldPos.xz - _MaskOrigin) / _MaskTileSize
// 其中 _MaskOrigin 由 C# 传入、取"平面中心对齐到 tile 倍数"后的值。
// 这是个**纯视觉修法**：真正"对"的做法是把水洼画进地形材质，但那要把 .mat 改脏
// （天空盒那次已经踩过一次），而且在 6000+ 碰撞体、多个地面材质的情况下
// 逐个改共享资产风险太高。世界锁定 UV 效果等价，且一个共享材质都不用碰。
// ══════════════════════════════════════════════════════════════════════
//
// 为什么不用屏幕空间反射（SSPR）：
//   内置管线下 SSPR 要 OnRenderImage + 深度重建 + 屏幕步进，全屏拷贝好几次，
//   低端机上不稳；而我们只需要"地面反光"这一个效果。
//   环境立方图（unity_SpecCube0）已经能给出反射方向上的天空与远景，
//   场景里放了反射探针时还会自动包含建筑 —— 视觉上足够，成本几乎为零。
//
// 改写对照（§2.2）：
//   LightMode  UniversalForward            → ForwardBase（本 Shader 不采样主光，只需能被渲染）
//   include    URP Core / EntityLighting   → UnityCG.cginc（DecodeHDREnvironment 内置在 UnityCG 里）
//   TEXTURE2D/SAMPLER/SAMPLE_TEXTURECUBE_LOD → sampler2D / tex2Dlod / texCUBElod
//   CBUFFER_START(UnityPerMaterial)        → 去掉
//
// 关键细节（都是容易踩的，全部原样保留）：
//   1) R.y 取绝对值。涟漪扰动可能让反射方向朝下，采到立方图下半球会突然变暗一圈。
//   2) unity_SpecCube0 在没绑探针、且默认反射模式异常时是纯黑，直接采会得到"黑水"。
//      用亮度阈值 0.002 判断并退回解析天空色。夜里合法天空的亮度远高于这个阈值。
//   3) 涟漪是程序化算的（3 层不同缩放的分格哈希），不额外占贴图，也看不出网格规律。
//   4) 边缘淡出写在 shader 里，所以遮罩贴图不需要是周期的，平面也看不到方形边界。
Shader "CampusUI/WetGround"
{
    Properties
    {
        _MaskTex ("Puddle Mask (R)", 2D) = "black" {}
        _MaskOrigin ("Mask World Origin", Vector) = (0, 0, 0, 0)
        _MaskTileSize ("Mask Tile Size (m)", Float) = 40
        _WaterColor ("Water Tint", Color) = (0.05, 0.07, 0.09, 1)
        _SkyColor ("Sky Zenith", Color) = (0.42, 0.55, 0.78, 1)
        _HorizonColor ("Sky Horizon", Color) = (0.70, 0.75, 0.80, 1)
        _Wetness ("Wetness", Range(0, 1)) = 0
        _ReflectStrength ("Reflect Strength", Range(0, 2)) = 1
        _EnvReflect ("Env Probe Weight", Range(0, 1)) = 0.80
        _Gloss ("Gloss (cube mip)", Range(0, 6)) = 0.6
        _RippleStrength ("Ripple Strength", Range(0, 2)) = 1
        _RippleScale ("Ripple Scale", Float) = 1.6
        _RippleSpeed ("Ripple Speed", Float) = 1
        _FresnelPower ("Fresnel Power", Float) = 5
        _EdgeFade ("Edge Fade", Range(0.2, 1.2)) = 0.55
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
            Name "WetGround"
            Tags { "LightMode" = "ForwardBase" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off

            CGPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag

            #pragma multi_compile_fog

            #include "UnityCG.cginc"

            sampler2D _MaskTex;
            float4 _MaskTex_ST;
            float4 _MaskOrigin;      // 遮罩平铺的世界原点（xz 有效）
            float  _MaskTileSize;    // 一次平铺覆盖的世界尺寸（米）
            fixed4 _WaterColor;
            fixed4 _SkyColor;
            fixed4 _HorizonColor;
            float  _Wetness;
            float  _ReflectStrength;
            float  _EnvReflect;
            float  _Gloss;
            float  _RippleStrength;
            float  _RippleScale;
            float  _RippleSpeed;
            float  _FresnelPower;
            float  _EdgeFade;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv     : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos      : SV_POSITION;
                float2 uv       : TEXCOORD0;   // 世界锁定 UV（采样水洼遮罩）
                float3 worldPos : TEXCOORD1;
                float2 uvLocal  : TEXCOORD3;   // 平面局部 UV 0~1（只用于边缘淡出）
                UNITY_FOG_COORDS(2)
            };

            float2 Hash22(float2 p)
            {
                float2 q = float2(dot(p, float2(127.1, 311.7)),
                                  dot(p, float2(269.5, 183.3)));
                return frac(sin(q) * 43758.5453123);
            }

            // 雨滴打在水面上的同心涟漪。做法：把世界 XZ 分格，每格一个随机中心 + 随机相位，
            // 用 sin(distance - phase) 得到向外扩散的环；三层不同缩放叠加，打散网格规律。
            float2 RainRipple(float2 p, float t)
            {
                float2 acc = float2(0.0, 0.0);

                [unroll]
                for (int i = 0; i < 3; i++)
                {
                    float fi = (float)i;
                    float2 q = p * (1.0 + fi * 0.85) + fi * 19.37;
                    float2 cell = floor(q);
                    float2 f = frac(q);

                    float2 h = Hash22(cell + fi * 7.1);
                    float2 ctr = 0.25 + h * 0.5;

                    float2 d2 = f - ctr;
                    float d = length(d2);

                    float ph = frac(t * (0.5 + h.y * 0.7) + h.x);
                    float ring = sin((d - ph * 0.7) * 34.0);
                    float band = saturate(1.0 - abs(d - ph * 0.7) * 5.0);
                    float fade = band * (1.0 - ph) * saturate(1.0 - d * 1.7);

                    acc += normalize(d2 + 1e-4) * ring * fade;
                }

                return acc * 0.45;
            }

            v2f vert(appdata v)
            {
                v2f o;
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.pos      = UnityWorldToClipPos(o.worldPos);

                // 【P13】世界锁定的 UV —— 水坑图案钉在地面上，不跟平面移动。
                // 不用 TRANSFORM_TEX(v.uv, _MaskTex)：那是物体空间 UV，平面一挪图案就跟着挪。
                //
                // _MaskOrigin 由 C# 传入且**固定为世界原点**（不随平面/相机动），
                // 所以 UV 只取决于该像素在世界里的绝对位置 → 图案在世界空间完全静止。
                // 平面怎么吸附、怎么跳格都不影响这里的结果。
                //
                // _MaskTileSize = 40m：一张 512×512 的噪声图铺满 40m，
                // 缩放与队友原版观感一致，水洼疏密不用重新调。
                o.uv = (o.worldPos.xz - _MaskOrigin.xz) / max(0.001, _MaskTileSize);

                // 边缘淡出必须用**平面局部 UV**，不能用上面那个世界 UV。
                // 世界 UV 可能是 (3.4, 7.1) 这种大范围，拿它算 length(uv-0.5)
                // 会得到恒大于 1 的结果 → 整片被 discard → 水膜完全消失。
                // 这是把 UV 从物体空间切到世界空间时最容易漏的一处。
                o.uvLocal = v.uv;

                UNITY_TRANSFER_FOG(o, o.pos);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 mask = tex2D(_MaskTex, i.uv);
                float puddle = mask.r;

                // 平面 UV 到中心的距离 → 边缘淡出，避免看到一块方形水膜
                //
                // ⚠️【P13】与 SnowGround.shader 同型的退化保护：smoothstep(a,b,x) 内部是
                // (x-a)/(b-a)，当 _EdgeFade 被调到 1.0（Range 上限是 1.2，能调到）时
                // b-a = 0 → 0/0 = NaN，alpha 归零、整片水膜静默消失。
                // 现在 C# 侧用的是默认 0.55 所以没暴露，但这个属性对外可见，必须防。
                //
                // 另注意：这里必须用 i.uvLocal（平面局部 0~1），不是 i.uv（世界 UV）。
                float edge;
                if (_EdgeFade < 0.999)
                {
                    float2 c = i.uvLocal - 0.5;
                    float r = length(c) * 2.0;
                    edge = 1.0 - smoothstep(_EdgeFade, 1.0, r);
                }
                else
                {
                    edge = 1.0;
                }
                if (edge <= 0.002) discard;

                // 涟漪法线
                float2 rip = RainRipple(i.worldPos.xz * _RippleScale, _Time.y * _RippleSpeed);
                float3 N = normalize(float3(rip.x * 0.30 * _RippleStrength,
                                            1.0,
                                            rip.y * 0.30 * _RippleStrength));

                float3 V = normalize(_WorldSpaceCameraPos - i.worldPos);
                float fres = pow(saturate(1.0 - saturate(dot(N, V))), _FresnelPower);

                // 反射方向（水面朝下的一半没有意义，取绝对值）
                float3 R = reflect(-V, N);
                R.y = abs(R.y);

                // 解析天空：反射方向越朝上越接近天顶色
                float3 sky = lerp(_HorizonColor.rgb, _SkyColor.rgb, saturate(R.y * 1.5));

                // 立方图采样：**不能直接写 texCUBElod**。
                //
                // 【根因】d3d11 下 Unity 把反射探针拆成两半：
                //     TextureCube  unity_SpecCube0;
                //     SamplerState unity_SpecCube0_sampler;   ← 独立 SamplerState
                // 而 `texCUBElod(samplerCUBE, float4)` 要求纹理与采样器**合并成一个
                // samplerCUBE 对象**，两半拆开时 d3d11 就会报
                //     "texCUBElod: no matching 2 parameter intrinsic function"
                // （列出的四个候选全是 samplerCUBE 版本，一个都用不上）。
                //
                // 正确写法是走 Unity 的封装宏，它按平台自行决定"合并采样"还是"分离采样"：
                //     UNITY_SAMPLE_TEXCUBE_LOD(tex, coord)   →  带 mip 的立方图采样
                // 底层的 unity_SpecCube0_HDR 由 Unity 自动绑定，不用自己声明。
                half4 enc = UNITY_SAMPLE_TEXCUBE_LOD(unity_SpecCube0, R, _Gloss);
                float3 env = DecodeHDR(enc, unity_SpecCube0_HDR);

                float lum = dot(env, float3(0.299, 0.587, 0.114));
                env = (lum < 0.002) ? sky : min(env, 6.0);   // 探针没绑定时是黑的，退回解析天空

                float3 refl = lerp(sky, env, _EnvReflect);
                float3 col = lerp(_WaterColor.rgb, refl, saturate(fres * 1.6 + 0.12) * _ReflectStrength);

                // 被水覆盖的漫反射会变暗，整体压一点
                col *= lerp(1.0, 0.82, _Wetness);

                UNITY_APPLY_FOG(i.fogCoord, col);

                // 水洼处几乎全不透明，其余地方只留一层薄薄湿光
                float a = _Wetness * saturate(0.10 + 0.95 * puddle) * edge;
                return fixed4(col, saturate(a));
            }
            ENDCG
        }
    }

    FallBack Off
}
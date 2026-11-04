// 雨天积水层（**内置管线 Built-in RP 版**，由 URP 版按队员文档 §2.2 对照表改写）。
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
                float2 uv       : TEXCOORD0;
                float3 worldPos : TEXCOORD1;
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
                o.uv       = TRANSFORM_TEX(v.uv, _MaskTex);
                UNITY_TRANSFER_FOG(o, o.pos);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 mask = tex2D(_MaskTex, i.uv);
                float puddle = mask.r;

                // 平面 UV 到中心的距离 → 边缘淡出，避免看到一块方形水膜
                float2 c = i.uv - 0.5;
                float r = length(c) * 2.0;
                float edge = 1.0 - smoothstep(_EdgeFade, 1.0, r);
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

                // 立方图采样：内置管线用 texCUBElod（URP 的 SAMPLE_TEXTURECUBE_LOD 同价）。
                // DecodeHDREnvironment 来自 UnityCG.cginc，兄弟参数 unity_SpecCube0_HDR 由 Unity 自动绑定。
                half4 enc = texCUBElod(unity_SpecCube0, float4(R, _Gloss));
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
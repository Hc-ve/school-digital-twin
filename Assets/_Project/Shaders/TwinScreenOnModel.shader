// 直接在**幕布模型本体**上显示视频：用世界坐标算 UV，完全不依赖模型自身的 UV0。
//
// 【为什么需要这样】幕布是预制体实例，运行时 `sharedMesh` 是合并网格
// （`Combined Mesh (root: scene) N`，几十个 submesh），
// 模型 UV 是图集 UV、不铺满 0..1 —— 直接塞 `_EmissionMap` 只会显示一块碎片。
// 而 `Renderer.bounds` 是准确的（3.11×2.12×0.10，就是幕布本身），
// 所以用"点在世界里的位置"反算屏幕 UV 最可靠。
//
// 【怎么用】材质上设置：
//   _Center / _Right / _Up / _Size  —— 幕布的屏幕平面（由 bounds 算出）
//   _MainTex                        —— 视频合成后的 RenderTexture（已按 cover 铺满 0..1）
//   _EmissionColor                  —— 开关色调（关 = 黑）
// 顶点位置投到 (right, up) 平面 → 归一化到 0..1 → 采样。屏幕外一律黑。
Shader "SchoolTwin/ScreenOnModel"
{
    Properties
    {
        _MainTex ("Video", 2D) = "black" {}
        _EmissionColor ("Tint", Color) = (1, 1, 1, 1)
        // 关机时幕布的本色。真实幕布是一块白布，不能因为"没画面"就变成纯黑
        _OffColor ("Screen Off Color", Color) = (0.78, 0.78, 0.75, 1)
        _Center ("Screen Center (world)", Vector) = (0, 0, 0, 0)
        _Right ("Screen Right (world)", Vector) = (1, 0, 0, 0)
        _Up ("Screen Up (world)", Vector) = (0, 1, 0, 0)
        _Size ("Screen Size (w, h)", Vector) = (1, 1, 0, 0)
        // ---- 投影质感 ----
        // 真实投影仪镜头的光强分布：中心亮、边缘暗。这是"投影"和"显示器"最直观的区别。
        // 0 = 均匀（像 LED 屏），0.3 = 明显暗角（像投影）
        _Vignette ("Vignette", Range(0, 0.8)) = 0.30
        // 投影画面比显示器暗一档（白幕反射率有限，不可能到纯白 1.0）
        _MaxBright ("Max Brightness", Range(0.3, 1.5)) = 0.92
        // 投影灯泡偏暖：画面整体略微偏黄
        _Warmth ("Warmth", Range(-1, 1)) = 0.12
        // 图片宽高比（宽/高）。`_Contain = 1` 时用来算留边。
        _ImageAspect ("Image Aspect", Float) = 1.6
        // 0 = cover（铺满，裁掉多余，视频用）；1 = contain（完整显示，四周留黑，PPT 用）
        _Contain ("Contain Mode", Float) = 0
        // 调试：1 = 整块面涂亮红，用来确认"改的是不是这块模型"
        _DebugRed ("Debug Red", Float) = 0
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" }

        // 幕布是薄板，模型朝向不可靠 → 双面
        Cull Off
        ZWrite On

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _EmissionColor;
            float4 _OffColor;
            float4 _Center;
            float4 _Right;
            float4 _Up;
            float4 _Size;
            float _Vignette;
            float _MaxBright;
            float _Warmth;
            float _ImageAspect;
            float _Contain;
            float _DebugRed;

            struct appdata
            {
                float4 vertex : POSITION;
            };

            struct v2f
            {
                float4 pos  : SV_POSITION;
                float3 wpos : TEXCOORD0;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.wpos = mul(unity_ObjectToWorld, v.vertex).xyz;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                if (_DebugRed > 0.5)
                    return fixed4(1, 0, 0, 1);

                // 世界坐标 → 幕布平面上的归一化 UV
                float3 rel = i.wpos - _Center.xyz;
                float u = dot(rel, _Right.xyz) / max(1e-5, _Size.x) + 0.5;
                float v = dot(rel, _Up.xyz)    / max(1e-5, _Size.y) + 0.5;

                // 屏幕之外（同一 Renderer 里可能还连着别的几何）
                float inside = step(0.0, u) * step(u, 1.0) * step(0.0, v) * step(v, 1.0);

                // ── contain 模式（PPT 用）─────────────────────────────────
                // PPT 是 16:9/16:10，幕布是 1.47。cover 会裁掉左右各 4%，
                // PPT 的文字贴着边就没了。contain 改成"完整显示、四周留黑"。
                //
                // 推导：屏幕宽高比 S，图片宽高比 I
                //   I > S（图比屏宽）→ 按宽度贴合，上下留黑，v 范围收窄到 S/I
                //   I < S（图比屏高）→ 按高度贴合，左右留黑，u 范围收窄到 I/S
                float2 uv2 = float2(u, v);
                float inImg = 1.0;
                if (_Contain > 0.5)
                {
                    float S = _Size.x / max(1e-5, _Size.y);
                    float I = max(1e-5, _ImageAspect);
                    if (I > S)
                        uv2.y = (v - 0.5) * (S / I) + 0.5;
                    else
                        uv2.x = (u - 0.5) * (I / S) + 0.5;

                    inImg = step(0.0, uv2.x) * step(uv2.x, 1.0) *
                            step(0.0, uv2.y) * step(uv2.y, 1.0);
                }

                // 开关：`_EmissionColor` 被 `PcScreenPlayer.SetOn` 设成黑 / 亮色，用它判断是否在播。
                float on = step(0.001, dot(_EmissionColor.rgb, float3(0.3333, 0.3333, 0.3333)));

                // 关机时显示幕布本色（受环境光影响，关灯会变暗），不是纯黑。
                // 2026-10-27：第一版关机直接输出黑，整块幕布变成一块黑板，很假。
                // 用 `ShadeSH9` 取环境球谐光：比 `UNITY_LIGHTMODEL_AMBIENT` 在新版更可靠，
                // 而且关灯/断電时环境光会变，幕布跟着暗下去才对。
                float3 amb = ShadeSH9(float4(0, 1, 0, 1));
                float lum = saturate(dot(amb, float3(0.3333, 0.3333, 0.3333)) * 3.0 + 0.35);
                float3 offCol = _OffColor.rgb * lum;

                float3 video = tex2D(_MainTex, uv2).rgb;

                // ── 投影质感（2026-10-27 用户反馈"没有模拟现实的投影效果"）──────
                // 之前的幕布是均匀满亮度，看起来就是一块 LED 屏。真实投影有三件事：
                //   ① 镜头光强分布：中心亮、四角暗（暗角）
                //   ② 白幕反射率有限，最亮也到不了纯白
                //   ③ 灯泡偏暖，整体略泛黄
                float2 d = float2(u, v) - 0.5;
                float r2 = saturate(dot(d, d) * 4.0);          // 中心 0，四角 1
                float falloff = 1.0 - _Vignette * r2;

                video *= falloff * _MaxBright;
                video.r *= (1.0 + _Warmth * 0.35);
                video.b *= (1.0 - _Warmth * 0.35);

                // contain 的留边区域 → 纯黑（投影 PPT 时的正常表现）
                video *= inImg;

                float3 c = lerp(offCol, video * _EmissionColor.rgb, on * inside);
                return fixed4(c, 1);
            }
            ENDCG
        }
    }

    Fallback Off
}

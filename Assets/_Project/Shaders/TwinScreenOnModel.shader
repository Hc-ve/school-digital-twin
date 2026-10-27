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

                // 开关：`_EmissionColor` 被 `PcScreenPlayer.SetOn` 设成黑 / 亮色，用它判断是否在播。
                float on = step(0.001, dot(_EmissionColor.rgb, float3(0.3333, 0.3333, 0.3333)));

                // 关机时显示幕布本色（受环境光影响，关灯会变暗），不是纯黑。
                // 2026-10-27：第一版关机直接输出黑，整块幕布变成一块黑板，很假。
                // 用 `ShadeSH9` 取环境球谐光：比 `UNITY_LIGHTMODEL_AMBIENT` 在新版更可靠，
                // 而且关灯/断電时环境光会变，幕布跟着暗下去才对。
                float3 amb = ShadeSH9(float4(0, 1, 0, 1));
                float lum = saturate(dot(amb, float3(0.3333, 0.3333, 0.3333)) * 3.0 + 0.35);
                float3 offCol = _OffColor.rgb * lum;

                float3 video = tex2D(_MainTex, float2(u, v)).rgb * _EmissionColor.rgb;
                float3 c = lerp(offCol, video, on * inside);
                return fixed4(c, 1);
            }
            ENDCG
        }
    }

    Fallback Off
}

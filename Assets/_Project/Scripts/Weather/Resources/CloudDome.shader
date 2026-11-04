// Volumetric-looking cloud dome, sampled from a 3D noise texture.
//
// History of what went wrong before this:
//   1. Spherical UVs  -> all longitudes collapse at the poles, texture is
//      stretched infinitely there, giving radial streaks.
//   2. Triplanar 2D   -> no poles, but the projection is cubic: the pattern
//      repeats once per 90 degrees and the blend weight (a*a) makes the
//      boundaries between faces show up as a visible grid of tiles.
//   3. 3D noise (this) -> the view direction indexes a continuous volume, so
//      there is no pole, no seam and no tiling. The only cost is one tex3D.
//
// The texture is sampled with Clamp wrap and the lookup sphere is kept well
// inside [0,1]^3, so the texture never has to be periodic.

Shader "CampusUI/CloudDome"
{
    Properties
    {
        _Noise3D ("Cloud Volume (alpha = density)", 3D) = "" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _Opacity ("Opacity", Range(0,1)) = 0.5
        _Overcast ("Overcast Veil", Range(0,1)) = 0
        _Radius ("Sample Radius", Range(0.05,0.48)) = 0.34
        _Speed ("Drift", Float) = 0.02
        _HorizonFade ("Horizon Fade", Range(0,1)) = 0.30
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "RenderType" = "Transparent"
            "IgnoreProjector" = "True"
        }

        Cull Off
        ZWrite Off
        ZTest LEqual
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #include "UnityCG.cginc"

            sampler3D _Noise3D;
            float4 _Color;
            float _Opacity;
            float _Overcast;
            float _Radius;
            float _Speed;
            float _HorizonFade;

            struct appdata
            {
                float4 vertex : POSITION;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 dir : TEXCOORD0;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                // Dome is centred on the camera, so object space direction == view direction.
                o.dir = v.vertex.xyz;
                return o;
            }

            float3 RotateY(float3 p, float a)
            {
                float s = sin(a), c = cos(a);
                return float3(p.x * c - p.z * s, p.y, p.x * s + p.z * c);
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float3 d = normalize(i.dir);
                float t = _Time.y * _Speed;

                // Drift by slowly rotating the lookup direction instead of
                // translating the sample point: translation would eventually
                // push the lookup sphere outside [0,1]^3 and reveal the border.
                float3 d2 = RotateY(d, t);
                float3 p = d2 * _Radius + 0.5;

                float c = tex3D(_Noise3D, p).a;

                // Fade below the horizon so the dome never shows under the ground.
                float horizon = smoothstep(-_HorizonFade, 0.02, d.y);

                // 【P13】阴天/雨雪天的"实心云盖"。
                //
                // 问题：云穹顶的 alpha 上限一直是 _Opacity（= 天气预设的 cloud 值），
                // 雪天 cloud = 0.62，也就是**最多只能遮住 62% 的天空**，剩下 38%
                // 仍是 Skybox/Procedural 算出来的蓝天 —— 于是"下着雪、天还是湛蓝"。
                //
                // 为什么不能直接调大 cloud：那是「云的多少」，云多了会变成一朵一朵的白团，
                // 天空反而更亮更花；雪天要的是"整个天被一层灰白压住"，是**均匀的帷幔**，
                // 不是更浓的云。所以拆成两个语义：
                //   _Opacity  云朵本体（噪声塑形，有明暗起伏）
                //   _Overcast 阴天帷幔（平坦、无噪声、只负责压住底色）
                // 最终 alpha 取两者的 max，而不是相加：相加会在云缝处过曝。
                float veil = _Overcast * horizon;

                float a = saturate(max(c * _Opacity, veil) * horizon);
                return fixed4(_Color.rgb, a);
            }
            ENDCG
        }
    }

    Fallback Off
}

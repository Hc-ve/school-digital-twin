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

                return fixed4(_Color.rgb, saturate(c * _Opacity * horizon));
            }
            ENDCG
        }
    }

    Fallback Off
}

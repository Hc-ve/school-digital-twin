// Sky dome shader used by the procedural weather system.
//
// Why this exists: a normal sphere has spherical UVs, and all longitudes collapse
// into a single point at the poles. Any texture mapped that way gets stretched
// infinitely near the poles, which shows up as radial streaks across the sky.
// This shader ignores the mesh UVs entirely and instead projects the view
// direction triplanar, so there is no pole singularity and no u/v seam.
//
// Used for both the cloud layer and the star field (same shader, different texture).

Shader "CampusUI/SkyDome"
{
    Properties
    {
        _MainTex ("Dome Texture (alpha = density)", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _Opacity ("Opacity", Range(0,1)) = 0.5
        _Scale ("Tiling", Float) = 1.4
        _Speed ("Drift Speed", Float) = 0.008
        _HorizonFade ("Horizon Fade", Range(0,1)) = 0.25
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Background"
            "RenderType" = "Background"
            "IgnoreProjector" = "True"
            "PreviewType" = "Skybox"
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
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _Color;
            float _Opacity;
            float _Scale;
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
                // The dome is a sphere centred on the camera, so an object space
                // vertex direction is also the view direction for that pixel.
                o.dir = v.vertex.xyz;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float3 d = normalize(i.dir);
                float3 a = abs(d);
                float t = _Time.y * _Speed;

                // Triplanar sampling. The max() clamp keeps the division stable
                // near the axes; the axis-aligned weights below fade those
                // samples out anyway.
                float2 uvX = d.zy / max(a.x, 0.08);
                float2 uvY = d.xz / max(a.y, 0.08);
                float2 uvZ = d.xy / max(a.z, 0.08);

                float cX = tex2D(_MainTex, uvX * _Scale + t).a;
                float cY = tex2D(_MainTex, uvY * _Scale + t).a;
                float cZ = tex2D(_MainTex, uvZ * _Scale + t).a;

                float3 w = a * a;
                w /= max(1e-4, w.x + w.y + w.z);
                float c = cX * w.x + cY * w.y + cZ * w.z;

                // Fade out below the horizon so the dome never appears to be
                // painted underneath the ground plane when looking down.
                float horizon = smoothstep(-_HorizonFade, 0.02, d.y);

                return fixed4(_Color.rgb, saturate(c * _Opacity * horizon));
            }
            ENDCG
        }
    }

    Fallback Off
}

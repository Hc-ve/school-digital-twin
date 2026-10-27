// 把视频按「等比 + 居中」铺进显示器的屏幕 UV 矩形，矩形外输出纯黑。
//
// 【为什么需要这个着色器】
// 屏幕的 `_EmissionMap` 覆盖的是**整个显示器图集**（含边框），
// 直接把视频塞进去，边框也会采样到视频 → 整个外壳发光。
// 所以每帧把视频 Blit 到一张「与图集同布局」的 RenderTexture，
// 由本着色器负责：① 只在屏幕矩形内输出画面 ② 矩形外输出黑 ③ 视频等比不拉伸。
//
// 【为什么不用现成的遮罩纹理相乘】
// 遮罩是运行时按 UV 生成的，矩形就是几个常量。这里直接按 UV 判断，
// 少一张纹理采样、少一个资源，而且矩形改了只改常量。
Shader "SchoolTwin/ScreenComposite"
{
    Properties
    {
        _MainTex ("Video", 2D) = "black" {}
        _ScreenMin ("Screen Min UV", Vector) = (0, 0, 0, 0)
        _ScreenMax ("Screen Max UV", Vector) = (1, 1, 0, 0)
        // 视频宽高比 ÷ 矩形宽高比。>1 = 视频更宽（上下留黑边），<1 = 更高（左右留黑边）
        _Fit ("Video Aspect / Rect Aspect", Float) = 1
        _Tint ("Tint", Color) = (1, 1, 1, 1)
    }

    SubShader
    {
        Cull Off
        ZWrite Off
        ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _ScreenMin;
            float4 _ScreenMax;
            float _Fit;
            fixed4 _Tint;

            fixed4 frag (v2f_img i) : SV_Target
            {
                float2 uv = i.uv;

                // 屏幕矩形之外 → 纯黑（显示器边框不能被点亮）
                float inside = step(_ScreenMin.x, uv.x) * step(uv.x, _ScreenMax.x)
                             * step(_ScreenMin.y, uv.y) * step(uv.y, _ScreenMax.y);
                if (inside < 0.5)
                    return fixed4(0, 0, 0, 1);

                // 矩形内归一化到 0..1
                float2 size = max(float2(1e-5, 1e-5), _ScreenMax.xy - _ScreenMin.xy);
                float2 local = (uv - _ScreenMin.xy) / size;

                // 【铺满 cover】取视频中间一块、比例等于屏幕矩形的区域，多余部分裁掉。
                //
                // 原来是 contain（整段视频缩进矩形、上下留黑边），用户明确说"视频没有铺满屏幕"，
                // 所以改成 cover：画面填满整个屏幕矩形，代价是两侧各裁掉约 12%。
                //   · 视频更宽（_Fit > 1）→ 裁左右
                //   · 视频更高（_Fit < 1）→ 裁上下
                float2 span = (_Fit >= 1.0)
                    ? float2(1.0 / max(1e-5, _Fit), 1.0)
                    : float2(1.0, _Fit);
                float2 vuv = (local - 0.5) * span + 0.5;

                // 越界保护（理论上 span ≤ 1 时不会越界，留一道保险）
                float inV = step(0.0, vuv.x) * step(vuv.x, 1.0)
                          * step(0.0, vuv.y) * step(vuv.y, 1.0);

                float3 c = tex2D(_MainTex, saturate(vuv)).rgb * inV;
                return fixed4(c * _Tint.rgb, 1);
            }
            ENDCG
        }
    }

    Fallback Off
}

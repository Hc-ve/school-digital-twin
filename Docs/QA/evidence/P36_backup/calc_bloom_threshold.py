# -*- coding: utf-8 -*-
"""P36 Bloom 阈值贡献量精确计算。

依据 PPv2 3.2.2 源码：
  Bloom.cs:171-173
    float lthresh = GammaToLinearSpace(threshold.value);
    float knee = lthresh * softKnee.value + 1e-5f;
    threshold = Vector4(lthresh, lthresh - knee, knee * 2f, 0.25f / knee);

  Colors.hlsl:242-255  QuadraticThreshold(color, threshold, curve)
    half br = Max3(color.rgb);              // 像素线性亮度
    half rq = clamp(br - curve.x, 0, curve.y);
    rq = curve.z * rq * rq;
    color *= max(rq, br - threshold) / br;  // ← 真正进入 bloom 缓冲的比例

其中 curve.x = lthresh - knee, curve.y = knee*2, curve.z = knee*2
注意：curve 参数依次是 (lthresh-knee, knee*2, 0.25/knee)
      shader 里 curve.x 取第一个、curve.y 取第二个、curve.z 取第三个
      => curve.x = lthresh - knee
         curve.y = knee * 2
         curve.z = 0.25 / knee
"""


def g2l(g):
    """Mathf.GammaToLinearSpace 的近似（sRGB）。Unity 用的是精确 sRGB 公式。"""
    if g <= 0.04045:
        return g / 12.92
    return ((g + 0.055) / 1.055) ** 2.4


def exp2_intensity(v):
    return 2 ** (v / 10.0) - 1


def prefilter_ratio(br, threshold_gamma, soft_knee):
    """返回该线性亮度像素被保留进 bloom 缓冲的比例（0~1）。"""
    lthresh = g2l(threshold_gamma)
    knee = lthresh * soft_knee + 1e-5
    curve_x = lthresh - knee
    curve_y = knee * 2.0
    curve_z = 0.25 / knee

    rq = min(max(br - curve_x, 0.0), curve_y)
    rq = curve_z * rq * rq
    return max(rq, br - lthresh) / max(br, 1e-6)


def report(label, threshold_gamma, intensity_v, subjects):
    print('=' * 68)
    print('%s   threshold=%.2f (linear %.4f)   softKnee=0.5   intensity=%.2f (%.4f x)'
          % (label, threshold_gamma, g2l(threshold_gamma), intensity_v,
             exp2_intensity(intensity_v)))
    knee = g2l(threshold_gamma) * 0.5 + 1e-5
    print('   knee = %.4f  =>  实际从线性 %.4f 起就开始有微小贡献'
          % (knee, g2l(threshold_gamma) - knee))
    print('   %-22s %10s %10s %14s' % ('对象', '线性亮度', '预滤比例', '叠加到画面'))
    out = {}
    for name, br in subjects:
        r = prefilter_ratio(br, threshold_gamma, 0.5)
        add = r * exp2_intensity(intensity_v) * br   # bloom 缓冲值 * intensity
        out[name] = (r, add)
        print('   %-22s %10.4f %9.2f%% %13.4f' % (name, br, r * 100, add))
    print()
    return out


wall = 0.5462      # 走廊墙面显示 0.765 -> 线性
lamp = 1.70        # 灯管 emission 上限
ceiling_dark = 0.201 ** 2.2   # 天花板暗区 p50 0.201 (gamma) -> 线性近似

subjects = [
    ('走廊墙面', wall),
    ('天花板暗区', 0.0355),
    ('灯管管壁', lamp),
]

v1 = report('v1（过曝版）', 0.72, 6.0, subjects)
v2n = report('v2 夜晚档（当前）', 0.90, 3.2, subjects)
v2d = report('v2 黄昏档（当前）', 0.90, 1.3, subjects)

print('=' * 68)
print('墙面 / 灯管 的光晕能量比（越小越好，越小=对比越强）')
for label, d in [('v1', v1), ('v2夜晚', v2n), ('v2黄昏', v2d)]:
    ratio = d['走廊墙面'][1] / d['灯管管壁'][1]
    print('  %-8s  %.4f / %.4f = %.3f' % (label, d['走廊墙面'][1], d['灯管管壁'][1], ratio))

print()
print('墙面光晕绝对量下降：')
print('  v1 -> v2夜晚 : %.1f 倍' % (v1['走廊墙面'][1] / v2n['走廊墙面'][1]))
print('  v1 -> v2黄昏 : %.1f 倍' % (v1['走廊墙面'][1] / v2d['走廊墙面'][1]))
print('灯管光晕绝对量下降：')
print('  v1 -> v2夜晚 : %.1f 倍（仍然可见）' % (v1['灯管管壁'][1] / v2n['灯管管壁'][1]))
print('对比度改善：')
print('  v1 -> v2夜晚 : %.1f 倍' % ((v1['走廊墙面'][1] / v1['灯管管壁'][1]) /
                                   (v2n['走廊墙面'][1] / v2n['灯管管壁'][1])))
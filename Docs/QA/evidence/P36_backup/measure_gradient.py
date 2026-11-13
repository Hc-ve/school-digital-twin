# -*- coding: utf-8 -*-
"""沿「距灯距离」采样亮度，区分 Bloom 扩散 vs 光源本身过强。

原理：
  Bloom 是模糊扩散 —— 亮度应随距灯距离单调衰减，且衰减尺度约等于光晕半径。
  光源直照 —— 衰减尺度由灯的光锥/距离决定，在封闭房间里天花板往往整体接近饱和。
"""
from PIL import Image
import math


def profile(path, label, rows):
    im = Image.open(path).convert('RGB')
    W, H = im.size
    print('=' * 74)
    print(label, '  size=%dx%d' % (W, H))
    for tag, y, x0, x1 in rows:
        vals = []
        for x in range(x0, x1, 4):
            r, g, b = im.getpixel((x, y))
            vals.append((0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0)
        if not vals:
            continue
        vals_sorted = sorted(vals)
        n = len(vals_sorted)
        hi = sum(1 for v in vals if v > 0.95) / n * 100
        print('  %-26s 采样%d  min=%.3f p25=%.3f p50=%.3f p75=%.3f max=%.3f  >0.95=%4.1f%%'
              % (tag, n, vals_sorted[0], vals_sorted[n // 4], vals_sorted[n // 2],
                 vals_sorted[n * 3 // 4], vals_sorted[-1], hi))
        # 波动幅度：如果整条线几乎恒定，说明是"整体被推平"，不是局部光晕
        print('      标准差=%.4f   （越小=越平坦，平坦即"被推平"）'
              % (math.sqrt(sum((v - sum(vals) / n) ** 2 for v in vals) / n)))
    print()


# 教室：天花板横切若干行（从灯正下方到远处）
profile(r'C:\Users\86159\.workbuddy-ai\clipboard-images\clipboard-2026-11-13T12-58-45-187Z-76030666.jpg',
        '计算机教室 1F · 天花板横向剖面',
        [
            ('天花板 靠上 y=60', 60, 130, 680),
            ('天花板 中 y=120', 120, 130, 680),
            ('天花板 靠下 y=200', 200, 130, 680),
            ('墙面 右 y=330', 330, 600, 690),
            ('地面 y=500', 500, 150, 600),
        ])

# 走廊：天花板剖面（灯是纵向排列，取横向可看到灯间暗带）
profile(r'C:\Users\86159\.workbuddy-ai\clipboard-images\clipboard-2026-11-13T12-58-45-196Z-f5962595.jpg',
        '二层走廊 2F · 天花板与墙面剖面',
        [
            ('天花板 近 y=90', 90, 300, 700),
            ('天花板 远 y=150', 150, 380, 700),
            ('左墙面 y=250', 250, 30, 200),
            ('地面 近 y=560', 560, 200, 700),
        ])
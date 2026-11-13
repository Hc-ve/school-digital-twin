# -*- coding: utf-8 -*-
"""P36 二次过曝测量：分别统计「整体」与「天花板区域」，定位过曝来源。"""
from PIL import Image


def analyze(path, label, whole, ceiling=None):
    im = Image.open(path).convert('RGB')

    def stat(box, tag):
        sub = im.crop(box)
        px = list(sub.getdata())
        n = len(px)
        lum = [(0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0 for r, g, b in px]
        s = sorted(lum)
        p50 = s[n // 2]
        o95 = sum(1 for v in lum if v > 0.95) / n * 100
        clip = sum(1 for p in px if all(c >= 254 for c in p)) / n * 100
        sat = sorted([(max(p) - min(p)) / max(max(p), 1) for p in px])
        print('  %-12s p50=%.3f  mean=%.3f  >0.95=%5.1f%%  削顶=%5.1f%%  饱和p50=%.3f'
              % (tag, p50, sum(lum) / n, o95, clip, sat[n // 2]))
        # 暖色程度：R-B 差值（判断是否是暖光把画面推黄）
        warm = sum((p[0] - p[2]) / 255.0 for p in px) / n
        print('              暖色偏移 R-B = %+.4f' % warm)

    print('=' * 70)
    print(label)
    stat(whole, '整体')
    if ceiling:
        stat(ceiling, '天花板区')
    print()


analyze(r'C:\Users\86159\.workbuddy-ai\clipboard-images\clipboard-2026-11-13T12-58-45-187Z-76030666.jpg',
        '计算机教室 1F',
        whole=(95, 40, 690, 520),
        ceiling=(120, 40, 690, 300))

analyze(r'C:\Users\86159\.workbuddy-ai\clipboard-images\clipboard-2026-11-13T12-58-45-196Z-f5962595.jpg',
        '二层走廊 2F',
        whole=(60, 30, 700, 560),
        ceiling=(300, 30, 700, 200))
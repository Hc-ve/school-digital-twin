# -*- coding: utf-8 -*-
"""测量过曝截图的像素分布，判断是否真的削顶。
只取游戏视口内的区域，避开编辑器 UI 面板。
"""
from PIL import Image
import sys


def stats(path, label, box):
    im = Image.open(path).convert('RGB')
    im = im.crop(box)
    px = list(im.getdata())
    n = len(px)
    lum = [(0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0 for r, g, b in px]

    over95 = sum(1 for v in lum if v > 0.95) / n * 100
    over99 = sum(1 for v in lum if v > 0.99) / n * 100
    below30 = sum(1 for v in lum if v < 0.30) / n * 100

    s = sorted(lum)
    def pct(p):
        return s[min(int(p / 100.0 * n), n - 1)]

    print('=' * 62)
    print(label)
    print('  区域 %s  像素 %d' % (str(box), n))
    print('  mean=%.3f  p10=%.3f  p50=%.3f  p90=%.3f  p99=%.3f'
          % (sum(lum) / n, pct(10), pct(50), pct(90), pct(99)))
    print('  >0.95: %5.1f%%   >0.99: %5.1f%%   <0.30: %5.1f%%'
          % (over95, over99, below30))

    # 纯白像素里 RGB 是否完全相等（真削顶的特征）
    pure = [p for p in px if all(c >= 254 for c in p)]
    print('  RGB 全 >=254（硬削顶）：%d px = %.1f%%' % (len(pure), len(pure) / n * 100))

    # 通道分离度：若三通道几乎相等，说明是灰色/白色雾；有色彩说明是材质本色
    sat = []
    for r, g, b in px:
        mx, mn = max(r, g, b), min(r, g, b)
        sat.append((mx - mn) / max(mx, 1))
    ss = sorted(sat)
    print('  饱和度 p50=%.3f  p90=%.3f' % (ss[n // 2], ss[min(int(0.9 * n), n - 1)]))
    return lum


# 走廊：视口大致 x 90~910, y 30~510（避开右上 HUD 与小地图）
stats(r'C:\Users\86159\.workbuddy-ai\clipboard-images\clipboard-2026-11-13T12-38-08-110Z-965e6432.jpg',
      '22:00 走廊（取视口主体，避开 HUD）', (90, 150, 560, 500))

# 教室：视口主体
stats(r'C:\Users\86159\.workbuddy-ai\clipboard-images\clipboard-2026-11-13T12-38-08-114Z-868553f0.jpg',
      '22:00 教室 2-1（取视口主体，避开 HUD）', (60, 60, 690, 520))
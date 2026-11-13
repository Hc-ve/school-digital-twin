# -*- coding: utf-8 -*-
"""P36 三次测量：按真实视口坐标（文件 1567x884）分区块统计。

视口边界实测：x 64..1425, y 43..806
HUD「未选中点位」浮层约在 x 998..1396, y 60..430 —— 统计时排除。
"""
from PIL import Image


def region_stat(im, box, tag):
    sub = im.crop(box)
    px = list(sub.getdata())
    n = len(px)
    if n == 0:
        return
    lum = [(0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0 for r, g, b in px]
    s = sorted(lum)
    o95 = sum(1 for v in lum if v > 0.95) / n * 100
    clip = sum(1 for p in px if all(c >= 254 for c in p)) / n * 100
    mean = sum(lum) / n
    std = (sum((v - mean) ** 2 for v in lum) / n) ** 0.5
    sat = sorted([(max(p) - min(p)) / max(max(p), 1) for p in px])[n // 2]
    print('  %-18s p50=%.3f mean=%.3f std=%.3f  >0.95=%5.1f%%  削顶=%5.1f%%  饱和=%.3f'
          % (tag, s[n // 2], mean, std, o95, clip, sat))


def grid(im, box, tag, nx=8, ny=5):
    """把区域切成网格，打印亮度热图，定位最亮块。"""
    x0, y0, x1, y1 = box
    print('\n  【%s】亮度热图（0-9 = 亮度十分位，X = 削顶）' % tag)
    for j in range(ny):
        row = []
        for i in range(nx):
            bx0 = x0 + (x1 - x0) * i // nx
            bx1 = x0 + (x1 - x0) * (j + 1) // ny if False else x0 + (x1 - x0) * (i + 1) // nx
            by0 = y0 + (y1 - y0) * j // ny
            by1 = y0 + (y1 - y0) * (j + 1) // ny
            sub = im.crop((bx0, by0, bx1, by1))
            px = list(sub.getdata())
            if not px:
                row.append(' ')
                continue
            lum = [(0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0 for r, g, b in px]
            m = sum(lum) / len(lum)
            clipr = sum(1 for p in px if all(c >= 254 for c in p)) / len(px)
            row.append('X' if clipr > 0.5 else str(min(9, int(m * 10))))
        print('    ' + ' '.join(row))


def analyze(path, label, vp, blocks, hud=None):
    im = Image.open(path).convert('RGB')
    print('=' * 76)
    print(label)
    region_stat(im, vp, '整个视口')
    for tag, box in blocks:
        region_stat(im, box, tag)
    grid(im, vp, '视口')
    print()


# ── 教室 ──
analyze(
    r'C:\Users\86159\.workbuddy-ai\clipboard-images\clipboard-2026-11-13T12-58-45-187Z-76030666.jpg',
    '计算机教室 1F',
    (64, 43, 1425, 806),
    [
        ('天花板', (150, 50, 950, 260)),
        ('左侧灰墙', (70, 200, 260, 560)),
        ('右侧亮区', (1000, 150, 1400, 520)),
        ('地面/桌面', (200, 600, 900, 800)),
    ])

# ── 走廊 ──
analyze(
    r'C:\Users\86159\.workbuddy-ai\clipboard-images\clipboard-2026-11-13T12-58-45-196Z-f5962595.jpg',
    '二层走廊 2F',
    (60, 30, 700, 780),
    [
        ('天花板', (250, 30, 700, 200)),
        ('左墙', (30, 200, 260, 560)),
        ('地面', (200, 650, 700, 780)),
    ])
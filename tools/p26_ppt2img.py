#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
P26 PPT 转图片

把 PDF（PPT 导出）渲染成一页一张 JPG，规格对齐项目里的屏幕载体：

    画布 1920x1200 (16:10)
    PPT 内容 1920x1080 (16:9) 居中
    上下各 60px 黑边

【为什么加黑边而不是裁切】
PPT 是 16:9，幕布是 1.47、显示器是 1.62。直接 cover 会裁掉左右各 4%，
PPT 的文字/图表贴着边就没了。加黑边后整体是 16:10，
显示器只裁 0.65%、幕布裁 4.16%，而 PPT 内容本身完全在内侧，切不到。

用法：
    python tools/p26_ppt2img.py <输入.pdf> [输出目录]

默认输出到 Assets/StreamingAssets/PC/ppt/
"""
import os
import sys

import pymupdf
from PIL import Image

CANVAS_W, CANVAS_H = 1920, 1200      # 16:10，和视频素材一致
# 95 而不是 92：PPT 是文字/线条为主，压缩率调低后文字边缘明显更干净。
# 代价：12 页从 1.4MB 涨到约 2.5MB —— 完全可以接受（视频动辄几十 MB）。
JPG_QUALITY = 95
BG = (0, 0, 0)                        # 黑边颜色


def main():
    if len(sys.argv) < 2:
        print("用法: python tools/p26_ppt2img.py <输入.pdf> [输出目录]")
        return 1

    pdf_path = sys.argv[1]
    if not os.path.isfile(pdf_path):
        print("✘ 找不到 PDF:", pdf_path)
        return 1

    here = os.path.dirname(os.path.abspath(__file__))
    project = os.path.dirname(here)
    out_dir = sys.argv[2] if len(sys.argv) >= 3 else os.path.join(
        project, "Assets", "StreamingAssets", "PC", "ppt")
    os.makedirs(out_dir, exist_ok=True)

    doc = pymupdf.open(pdf_path)
    n = doc.page_count
    print("源文件: %s" % pdf_path)
    print("页数  : %d" % n)
    print("输出  : %s" % out_dir)
    print("画布  : %dx%d (16:10)，PPT 居中 + 上下黑边")
    print()

    # 按 16:9 的宽度铺满画布，算出内容高度
    content_w = CANVAS_W
    content_h = int(round(content_w * 9.0 / 16.0))
    if content_h > CANVAS_H:                     # 万一源是更扁的比例
        content_h = CANVAS_H
        content_w = int(round(content_h * 16.0 / 9.0))
    off_x = (CANVAS_W - content_w) // 2
    off_y = (CANVAS_H - content_h) // 2

    print("PPT 内容区: %dx%d，偏移 (%d, %d)" % (content_w, content_h, off_x, off_y))
    print()

    total_kb = 0
    for i in range(n):
        page = doc[i]

        # 渲染到 2 倍分辨率再缩小 —— 文字边缘更干净（超采样）
        zoom = (content_w * 2) / page.rect.width
        pix = page.get_pixmap(matrix=pymupdf.Matrix(zoom, zoom), alpha=False)
        slide = Image.frombytes("RGB", (pix.width, pix.height), pix.samples)
        slide = slide.resize((content_w, content_h), Image.LANCZOS)

        canvas = Image.new("RGB", (CANVAS_W, CANVAS_H), BG)
        canvas.paste(slide, (off_x, off_y))

        name = "slide_%02d.jpg" % (i + 1)
        path = os.path.join(out_dir, name)
        canvas.save(path, "JPEG", quality=JPG_QUALITY, optimize=True)

        kb = os.path.getsize(path) / 1024
        total_kb += kb
        text = page.get_text().strip().replace("\n", " ")[:34]
        print("  %-14s %6.0f KB  %s" % (name, kb, text))

    doc.close()
    print()
    print("完成 %d 页，合计 %.1f MB" % (n, total_kb / 1024))
    return 0


if __name__ == "__main__":
    sys.exit(main())

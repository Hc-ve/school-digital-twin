"""把监控滤镜层叠到监控画面上，生成「监控画面效果.png」。

Unity 的 Overlay 画布不会被相机渲染捕获，所以构建器把滤镜单独渲染成一张
带 alpha 的图（「监控滤镜层.png」），这里再用 alpha 合成。
"""
import os
from PIL import Image

TOOLS = r"D:\Uproject\UI\Tools"
scene = os.path.join(TOOLS, "监控视角原始画面.png")
overlay = os.path.join(TOOLS, "监控滤镜层.png")
out = os.path.join(TOOLS, "监控画面效果.png")

if not os.path.exists(scene) or not os.path.exists(overlay):
    print("缺少输入:", scene, overlay)
    raise SystemExit(1)

base = Image.open(scene).convert("RGBA")
over = Image.open(overlay).convert("RGBA")

if base.size != over.size:
    over = over.resize(base.size, Image.LANCZOS)
    print("尺寸不一致，已缩放滤镜层")

composited = Image.alpha_composite(base, over).convert("RGB")
composited.save(out)

# 统计一下滤镜层确实有内容
alpha = over.getchannel("A")
lo, hi = alpha.getextrema()
print(f"滤镜层 alpha 范围 {lo}-{hi}")
print(f"已合成 -> {out}  {composited.size[0]}x{composited.size[1]}")

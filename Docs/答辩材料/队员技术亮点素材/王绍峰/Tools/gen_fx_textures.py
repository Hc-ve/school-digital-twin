"""生成监控滤镜与空调气流所需的贴图。

全部是程序化生成的、可平铺的小图，避免引入外部素材与版权问题。
"""
import os
import numpy as np
from PIL import Image

OUT = r"D:\Uproject\UI\Assets\Textures\ClassroomFX"
os.makedirs(OUT, exist_ok=True)

rng = np.random.default_rng(20261030)


def save(arr, name):
    path = os.path.join(OUT, name)
    Image.fromarray(arr, mode="RGBA").save(path)
    print(f"  {name}  {arr.shape[1]}x{arr.shape[0]}  {os.path.getsize(path)/1024:.1f} KB")


# ---------- 1. 胶片噪点：可平铺的随机灰度噪点 ----------
S = 512
noise = rng.random((S, S))
# 轻微平滑，避免纯白噪声过于刺眼
k = np.array([[1, 2, 1], [2, 4, 2], [1, 2, 1]], dtype=np.float32) / 16.0
sm = np.zeros_like(noise)
for dy in range(-1, 2):
    for dx in range(-1, 2):
        sm += k[dy + 1, dx + 1] * np.roll(np.roll(noise, dy, axis=0), dx, axis=1)
sm = (sm - sm.min()) / (sm.max() - sm.min())

grain = np.zeros((S, S, 4), dtype=np.uint8)
grain[..., 0] = 255
grain[..., 1] = 255
grain[..., 2] = 255
# 峰值压到 60/255 ≈ 0.24，配合 UI 层再乘一次透明度，最终只留很淡的颗粒感
grain[..., 3] = (sm * 60).astype(np.uint8)
save(grain, "grain.png")


# ---------- 2. 扫描线：4px 一组，两条暗两条亮 ----------
H = 8
scan = np.zeros((H, 4, 4), dtype=np.uint8)
for y in range(H):
    # 每 4 行重复：0,1 行为暗线，2,3 行为透明
    if y % 4 in (0, 1):
        scan[y, :, 0:3] = 0
        scan[y, :, 3] = 60
    else:
        scan[y, :, 0:3] = 255
        scan[y, :, 3] = 0
save(scan, "scanlines.png")


# ---------- 3. 暗角：中心透明、四周变黑 ----------
V = 512
yy, xx = np.mgrid[0:V, 0:V].astype(np.float32)
cx = cy = (V - 1) / 2.0
r = np.sqrt(((xx - cx) / cx) ** 2 + ((yy - cy) / cy) ** 2) / np.sqrt(2.0)
# 中心 0.52 以内完全透明，之后平缓变黑
t = np.clip((r - 0.52) / 0.48, 0, 1)
alpha = (t ** 1.8) * 240
vig = np.zeros((V, V, 4), dtype=np.uint8)
vig[..., 0:3] = 0
vig[..., 3] = alpha.astype(np.uint8)
save(vig, "vignette.png")


# ---------- 4. 粒子光斑：柔和圆形，用于空调气流 ----------
P = 128
yy, xx = np.mgrid[0:P, 0:P].astype(np.float32)
c = (P - 1) / 2.0
d = np.sqrt((xx - c) ** 2 + (yy - c) ** 2) / c
falloff = np.clip(1.0 - d, 0, 1) ** 2.2
blob = np.zeros((P, P, 4), dtype=np.uint8)
blob[..., 0:3] = 255
blob[..., 3] = (falloff * 255).astype(np.uint8)
save(blob, "particle_soft.png")

print("完成，输出目录:", OUT)

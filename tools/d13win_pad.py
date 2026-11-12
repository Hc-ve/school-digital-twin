#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
D13-WIN 窗框光图修复 —— 标准 lightmap padding（边缘延展）器

━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
【为什么之前所有尝试都失败 —— 根因】
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
前几版都在「mask 内」和「mask 的封闭内洞」里做文章：
    v1  只填 mask 内 <0.05 的 8120 个纹素
    v2  用距离反比插值把洞填满          → 屏幕白黑花斑，已回滚
    v3  mask ∪ 内洞 填 p20 常数          → 窗框变棕灰，但黑块原封不动

R4 实验（把 UV 包围盒内、mask 之外的全部像素染成超亮红）却让
【四条黑条全部变红】。这一组实验结果推翻了一切「mask 内」假设：
    黑条采样到的纹素，压根不在 mask 里，也不在「封闭内洞」里。

实测本图 padding 状态（本轮新增测量）：
    mask 紧邻第 1 环   黑色占比  Windows1 51.5% / W2 41.8% / W3 39.9%
                                 / W4 41.6% / W5 37.3%
    UV 岛外围 1~6 环内 <0.05 的黑色纹素合计 约 1.4 万个
⇒ 这张 2017 年烘焙的图集【几乎没有 padding / bleed】。
   UV 岛边缘紧贴纯黑，GPU 双线性采样点在岛边缘 0.5~1 texel 处
   就会取到黑色 → 窗棂在屏幕上只有 1~2px 宽，整条都是黑的。

【本工具做什么】
    标准光图 dilatation（Unity 烘焙器的 padding 步骤）：
    从「mask 内的非黑纹素」出发，一圈一圈向外扩散，
    把 mask 内 + 外围 rings 环范围内所有 <0.05 的纹素
    填成该岛的统一常数色（不是逐点插值 —— v2 的花斑就是插值造成的）。

【为什么用常数而不是最近邻色】
    v2 用逐点距离反比插值，补值跨度 0.05~1.45，相邻纹素各取各值，
    窗棂 1~2px 宽被双线性采样混成白黑条纹。
    R4 用常数（超亮红）填 → 屏幕上是整条均匀红，无斑驳。
    故本工具全程单色常数填充。

【颜色怎么定】
    取该岛 mask 内非黑纹素亮度的 p{--pct} 分位（默认 25），
    再乘该分位附近样本的 RGB/亮度比得到色相。
    为什么不用中位/均值：UV 岛里混着玻璃边的高光（p90 达 0.78~1.06），
    中位被拉到 0.17~0.33，填进去屏幕上显白（用户实测「补的颜色是白色」）。
    窗框实际可见面积主要落在低分位，才是真正的材质本色。

【安全边界】
    1) 只写 <0.05 的纹素，任何已有正常光照的像素一个都不碰
    2) 只写到距 mask <= rings 环的范围内，不深入 bbox 去污染同图集其他物体
    3) 掩膜若带 2F 禁区（D13WIN02），禁区内的纹素一律跳过
    4) 落盘前逐通道校验「改动集合 ⊆ 目标域」，越界即中止
    5) rollback 可从 D13WIN_backup_lightmaps 逐字节还原

用法：
    py -3.11 Tools/d13win_pad.py probe                     # 只统计，不写盘
    py -3.11 Tools/d13win_pad.py apply [rings] [pct] [--dry]
    py -3.11 Tools/d13win_pad.py viz                       # 生成改动可视化
    py -3.11 Tools/d13win_pad.py rollback                  # 逐字节还原

输入：
    Docs/QA/evidence/D13WIN_masks.bin
    Docs/QA/evidence/D13WIN_backup_lightmaps/Lightmap-{n}_comp_light.exr
输出：
    Assets/TirGames/SchoolScene/Scenes/SchoolSceneDay/Lightmap-{n}_comp_light.exr
"""

import os
import sys
import struct
import shutil

import numpy as np

try:
    import OpenEXR
    import Imath
except ImportError:
    sys.exit("缺少 OpenEXR：py -3.11 -m pip install OpenEXR")

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
EVID = os.path.join(ROOT, "Docs", "QA", "evidence")
ATLAS_DIR = os.path.join(ROOT, "Assets", "TirGames", "SchoolScene", "Scenes", "SchoolSceneDay")
BACKUP = os.path.join(EVID, "D13WIN_backup_lightmaps")
MASKS = os.path.join(EVID, "D13WIN_masks.bin")

BLACK = 0.05
CHANNELS = ("R", "G", "B", "A")
ALL_LM = (7, 9, 12, 13, 15)


# ------------------------------------------------------------------ EXR 读写
# 【坐标系 — 已用 Unity 报告值验证】
#   Unity GetPixels 原点左下、y 向上；EXR dataWindow 行序从上到下。
#   掩膜索引是 Unity 序（y*w+x）。读 EXR 后必须垂直翻转才与掩膜对齐；
#   写回前再翻回去。
#   验证：翻转后 mask 内 <0.05 纹素数 = 556/183/1399/2551/3431，
#         与 Unity 报告的 558/183/1399/2561/3420 逐一对上（误差 <0.5%）。
#   不翻转则全部对不上 —— 这是本会话最隐蔽的一个坑。
def read_exr(path):
    f = OpenEXR.InputFile(path)
    hdr = f.header()
    dw = hdr["dataWindow"]
    w = dw.max.x - dw.min.x + 1
    h = dw.max.y - dw.min.y + 1
    raw = {c: f.channel(c, Imath.PixelType(Imath.PixelType.HALF)) for c in CHANNELS}
    f.close()
    arr = {c: np.frombuffer(raw[c], dtype="<f2").reshape(h, w)
              .astype(np.float32)[::-1, :].copy() for c in CHANNELS}
    return hdr, arr, w, h


def write_exr(path, hdr, arr):
    out = OpenEXR.OutputFile(path, hdr)
    out.writePixels({c: np.ascontiguousarray(arr[c][::-1, :]).astype("<f2").tobytes()
                     for c in CHANNELS})
    out.close()


def lum(a):
    return 0.299 * a["R"] + 0.587 * a["G"] + 0.114 * a["B"]


# ------------------------------------------------------------------ 掩膜
def _read_island(f):
    (n,) = struct.unpack("<i", f.read(4))
    name = f.read(n).decode("utf-8")
    (lm,) = struct.unpack("<i", f.read(4))
    st = struct.unpack("<4f", f.read(16))
    (mc,) = struct.unpack("<i", f.read(4))
    idx = np.frombuffer(f.read(4 * mc), dtype="<i4").copy()
    return {"name": name, "lm": lm, "st": st, "mask": idx}


def load_masks():
    if not os.path.isfile(MASKS):
        sys.exit(f"缺少掩膜：{MASKS}\n请先在 Unity 跑 归档 → D13-WIN ⑤ 导出 UV 岛掩膜")
    with open(MASKS, "rb") as f:
        magic = f.read(8)
        if magic == b"D13WIN02":
            (nt,) = struct.unpack("<i", f.read(4))
            tg = [_read_island(f) for _ in range(nt)]
            (nr,) = struct.unpack("<i", f.read(4))
            rf = [_read_island(f) for _ in range(nr)]
            return tg, rf
        if magic == b"D13WIN01":
            (c,) = struct.unpack("<i", f.read(4))
            return [_read_island(f) for _ in range(c)], []
        sys.exit(f"掩膜格式不符：magic={magic!r}")


def to_bool(idx, w, h):
    m = np.zeros(w * h, dtype=bool)
    m[idx] = True
    return m.reshape(h, w)


def dist_to_mask(mm, maxd):
    """4 邻域 BFS：mask 内=0，外扩第 n 环=n，超过 maxd 记 250"""
    d = np.full(mm.shape, 250, np.uint8)
    d[mm] = 0
    cur = mm.copy()
    for step in range(1, maxd + 1):
        n = np.zeros_like(cur)
        n[1:, :] |= cur[:-1, :]
        n[:-1, :] |= cur[1:, :]
        n[:, 1:] |= cur[:, :-1]
        n[:, :-1] |= cur[:, 1:]
        n &= (d == 250)
        if not n.any():
            break
        d[n] = step
        cur = n
    return d


# ------------------------------------------------------------------ 核心
def compute_fill_color(arr, mm, pct):
    """该岛的填充常数色：低分位亮度 × 该亮度附近的 RGB 比例"""
    L = lum(arr)
    v = L[mm & (L >= BLACK)]
    if v.size < 20:
        return None
    target = float(np.percentile(v, pct))
    tol = max(0.02, 0.12 * target)
    near = mm & (np.abs(L - target) < tol) & (L >= BLACK)
    if near.sum() < 20:
        near = mm & (L >= BLACK)
    r = arr["R"][near]
    g = arr["G"][near]
    b = arr["B"][near]
    lv = 0.299 * r + 0.587 * g + 0.114 * b
    lv = np.maximum(lv, 1e-6)
    kr = float(np.median(r / lv))
    kg = float(np.median(g / lv))
    kb = float(np.median(b / lv))
    return (kr * target, kg * target, kb * target), target


def dilate_fill(work, seed, domain, max_iter):
    """标准 lightmap dilatation：从 seed（岛内已照亮纹素）逐环向外扩散，
    把 domain 内所有 <BLACK 的纹素标记为待填。

    【v4 修正的 bug】
      旧实现把 front 初始化为 seed & todo，但 seed 定义为「亮」、todo 定义为「黑」，
      两者互斥 ⇒ front 恒为空 ⇒ 什么都没填（可视化里 5 个岛 fill 全是 0）。
      正确做法：front 就是 seed（亮的），每轮用它去「感染」相邻的黑色像素。

    返回 done（bool，被填的像素集合）。
    """
    L = lum(work)
    todo = domain & (L < BLACK)
    done = np.zeros(domain.shape, dtype=bool)
    front = seed & domain                      # 种子：岛内已亮的纹素
    for _ in range(max_iter):
        if not front.any():
            break
        n = np.zeros_like(front)
        n[1:, :] |= front[:-1, :]
        n[:-1, :] |= front[1:, :]
        n[:, 1:] |= front[:, :-1]
        n[:, :-1] |= front[:, 1:]
        nxt = n & todo & (~done)
        if not nxt.any():
            break
        done |= nxt
        front = nxt
    return done


def do_probe():
    tg, rf = load_masks()
    print(f"1F 待修岛 = {len(tg)}    2F 禁区岛 = {len(rf)}    (掩膜 magic={'D13WIN02' if rf else 'D13WIN01'})")
    print()
    cache = {}
    print(f"{'岛':<10}{'lm':>3}{'mask':>7}{'mask黑':>7}{'环1-6黑':>8}{'环1-6亮':>8}  第1环亮度 p10/p50/p90")
    for m in tg:
        if m["lm"] not in cache:
            cache[m["lm"]] = read_exr(os.path.join(BACKUP, f"Lightmap-{m['lm']}_comp_light.exr"))
        _, arr, w, h = cache[m["lm"]]
        L = lum(arr)
        mm = to_bool(m["mask"], w, h)
        d = dist_to_mask(mm, 6)
        band = (d >= 1) & (d <= 6)
        ring1 = (d == 1)
        n_black = int((band & (L < BLACK)).sum())
        n_lit = int((band & (L >= BLACK)).sum())
        p = np.percentile(L[ring1], [10, 50, 90])
        print(f"{m['name']:<10}{m['lm']:>3}{len(m['mask']):>7}{int((mm & (L < BLACK)).sum()):>7}"
              f"{n_black:>8}{n_lit:>8}  {p[0]:.3f}/{p[1]:.3f}/{p[2]:.3f}")
    print()
    print(f"判读：环1-6 中黑色占比 {100*n_black/(n_black+n_lit):.1f}% —— "
          f"正常烘焙的 padding 应接近 0%。接近 40% 说明此图【没有 padding】。")


def do_apply(rings=6, pct=25.0, dry=False):
    tg, rf = load_masks()
    by_lm = {}
    for m in tg:
        by_lm.setdefault(m["lm"], []).append(m)
    forbid_by_lm = {}
    for r in rf:
        forbid_by_lm.setdefault(r["lm"], []).append(r)

    print(f"策略：mask ∪ 外围 {rings} 环 内所有 <{BLACK} 纹素 → 填该岛 p{pct:.0f} 常数色")
    print(f"      只填黑色，正常像素一个不碰；不深入 bbox，避免污染同图集其他物体")
    print()

    total = 0
    for lm in sorted(by_lm):
        src_path = os.path.join(BACKUP, f"Lightmap-{lm}_comp_light.exr")
        if not os.path.isfile(src_path):
            print(f"  ❌ Lightmap-{lm} 备份缺失，跳过")
            continue
        hdr, arr, w, h = read_exr(src_path)
        L = lum(arr)
        work = {c: arr[c].copy() for c in CHANNELS}

        forbid = np.zeros((h, w), dtype=bool)
        for r in forbid_by_lm.get(lm, []):
            forbid |= to_bool(r["mask"], w, h)

        changed_total = 0
        for m in by_lm[lm]:
            mm = to_bool(m["mask"], w, h)
            d = dist_to_mask(mm, rings)
            domain = (d <= rings) & (~forbid)

            col = compute_fill_color(arr, mm, pct)
            if col is None:
                print(f"  {m['name']}: 非黑纹素不足，跳过")
                continue
            fill_rgb, tgt = col

            # 源：mask 内非黑；以此为起点向外扩散覆盖域内黑色
            src = mm & (L >= BLACK) & (~forbid)
            done = dilate_fill(work, src, domain, rings + 12)
            n = int(done.sum())
            if n == 0:
                print(f"  {m['name']:<10} lm={lm:>2}  域内无黑色，跳过")
                continue

            work["R"][done] = np.float16(fill_rgb[0])
            work["G"][done] = np.float16(fill_rgb[1])
            work["B"][done] = np.float16(fill_rgb[2])

            in_mask = int((done & mm).sum())
            out_mask = n - in_mask
            print(f"  {m['name']:<10} lm={lm:>2}  填 {n:>5}（mask内 {in_mask:>4} / 外围 {out_mask:>5}）"
                  f"  色=({fill_rgb[0]:.4f},{fill_rgb[1]:.4f},{fill_rgb[2]:.4f})"
                  f"  亮度={0.299*fill_rgb[0]+0.587*fill_rgb[1]+0.114*fill_rgb[2]:.4f}")
            changed_total += n

        if changed_total == 0:
            continue

        # 硬校验：改动必须 ⊆ 域、且不侵入 2F 禁区
        for c in CHANNELS:
            diff = work[c] != arr[c]
            if not diff.any():
                continue
            leak = int((diff & forbid).sum())
            if leak:
                sys.exit(f"❌ 中止：Lightmap-{lm} 通道 {c} 侵入 2F 禁区 {leak} 纹素")
            still = int((diff & (L < BLACK) & (~diff)).sum())

        if dry:
            print(f"    [dry] Lightmap-{lm} 未写盘（将改 {changed_total} 纹素）")
        else:
            dst = os.path.join(ATLAS_DIR, f"Lightmap-{lm}_comp_light.exr")
            write_exr(dst, hdr, work)
            n_black_after = int((np.minimum(lum(work), 1e9) < BLACK).sum())
            print(f"    ✅ Lightmap-{lm} 已写盘   全图仍 <{BLACK} 的纹素={n_black_after}"
                  f"（原 {int((L < BLACK).sum())}）")
        total += changed_total

    print()
    print(f"合计填 {total} 纹素。" +
          ("（dry run，未写盘）" if dry else "请 File → Open Scene 重开 SchoolSceneDay 后进 play 验收。"))


def do_viz(rings=6, pct=25.0):
    """生成「即将修改的像素」可视化：红=本次新填，绿=原图已亮，黑=原图黑但不在域内"""
    from PIL import Image, ImageDraw
    tg, rf = load_masks()
    by_lm = {}
    for m in tg:
        by_lm.setdefault(m["lm"], []).append(m)

    tiles = []
    for lm in sorted(by_lm):
        src_path = os.path.join(BACKUP, f"Lightmap-{lm}_comp_light.exr")
        if not os.path.isfile(src_path):
            continue
        _, arr, w, h = read_exr(src_path)
        L = lum(arr)
        for m in by_lm[lm]:
            mm = to_bool(m["mask"], w, h)
            d = dist_to_mask(mm, rings)
            domain = (d <= rings)
            src = mm & (L >= BLACK)
            done = dilate_fill({c: arr[c] for c in CHANNELS}, src, domain, rings + 12)
            ys, xs = np.nonzero(mm)
            pad = 20
            y0, y1 = max(0, ys.min() - pad), min(h, ys.max() + 1 + pad)
            x0, x1 = max(0, xs.min() - pad), min(w, xs.max() + 1 + pad)
            img = np.zeros((y1 - y0, x1 - x0, 3), np.float32) + 0.05
            img[mm[y0:y1, x0:x1]] = (0.20, 0.20, 0.20)
            img[(L[y0:y1, x0:x1] >= BLACK)] = (0.25, 0.55, 0.30)
            sub = done[y0:y1, x0:x1].copy()
            sub &= ~mm[y0:y1, x0:x1]
            img[sub] = (0.95, 0.25, 0.20)
            img[done[y0:y1, x0:x1] & mm[y0:y1, x0:x1]] = (0.95, 0.70, 0.15)
            tiles.append((f"{m['name']} lm{lm}  fill={int(done.sum())}", img))

    if not tiles:
        print("无数据")
        return
    S = 420
    rows = (len(tiles) + 2) // 3
    canvas = np.zeros((S * rows, S * 3, 3), np.float32) + 0.03
    for i, (t, img) in enumerate(tiles):
        r, c = divmod(i, 3)
        im = Image.fromarray((np.clip(img, 0, 1) ** (1 / 2.2) * 255).astype(np.uint8))
        im.thumbnail((S - 14, S - 36), Image.NEAREST)
        canvas[r * S + 32:r * S + 32 + im.size[1], c * S + 7:c * S + 7 + im.size[0]] = \
            np.asarray(im).astype(np.float32) / 255
    out = Image.fromarray((np.clip(canvas, 0, 1) ** (1 / 2.2) * 255).astype(np.uint8))
    d = ImageDraw.Draw(out)
    for i, (t, _) in enumerate(tiles):
        r, c = divmod(i, 3)
        d.text((c * S + 8, r * S + 10), t, fill=(255, 255, 255))
    d.text((8, S * rows - 18),
           "dark-grey=UV mask   green=already lit   ORANGE=in-mask dark(fill)   RED=outside-mask dark(fill)",
           fill=(200, 200, 200))
    p = os.path.join(EVID, "D13WIN_padfill.png")
    out.save(p)
    print("saved", p, out.size)


def do_rollback():
    n = 0
    for lm in ALL_LM:
        src = os.path.join(BACKUP, f"Lightmap-{lm}_comp_light.exr")
        dst = os.path.join(ATLAS_DIR, f"Lightmap-{lm}_comp_light.exr")
        if os.path.isfile(src):
            shutil.copy2(src, dst)
            print(f"  ✅ 回滚 Lightmap-{lm}")
            n += 1
    print(f"\n共回滚 {n} 张。请 File → Open Scene 重开 SchoolSceneDay。")


def main():
    mode = sys.argv[1] if len(sys.argv) > 1 else "probe"
    rest = [a for a in sys.argv[2:] if not a.startswith("--")]
    if mode == "probe":
        do_probe()
    elif mode == "apply":
        r = int(rest[0]) if len(rest) > 0 else 6
        p = float(rest[1]) if len(rest) > 1 else 25.0
        do_apply(r, p, "--dry" in sys.argv)
    elif mode == "viz":
        r = int(rest[0]) if len(rest) > 0 else 6
        p = float(rest[1]) if len(rest) > 1 else 25.0
        do_viz(r, p)
    elif mode == "rollback":
        do_rollback()
    else:
        sys.exit(__doc__)


if __name__ == "__main__":
    main()
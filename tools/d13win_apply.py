#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
D13-WIN 窗框光图修复 —— Python 纯 EXR 落盘器  v2

为什么不用 Unity 落盘：
    实测证明 Unity 的 Texture2D(RGBAHalf) + EncodeToEXR 会把 >1.0 的 HDR 值
    截断到 1.0。原图 Lightmap-7 有 117262 个 R 通道像素 >1（峰值 2.36），
    经 Unity 往返后直方图 0.9~1.0 档从 57371 暴涨到 308991，整张图亮部压暗 0.086~0.19。
    Lightmap 是高动态范围数据，必须逐位保真。
    本脚本用 OpenEXR 直读直写，只改目标纹素，其余像素逐位不变。

v2 相对 v1 的两处关键修正（v1 落盘后用户反馈「没什么变化」）：
  ── 修正 1：修复范围从「mask 内」扩到「mask ∪ 岛内空洞」──────────────
      v1 只处理三角形光栅化命中的 mask。但 R1/R4 红填 bisect 实测：
        R1 (mask 内 <0.05 染红) → 屏幕上只有 3 条黑条变红，第 4 条纹丝不动
        R4 (UV 包围盒内、mask 外染红) → 4 条黑条全部变红
      ⇒ 窗框竖梃实际采样的是「包围盒内、光栅化 mask 外」的空洞区域
        （窗棂之间的 UV 留白，光栅化时三角形不覆盖，但 GPU 采样点落在那里）。
      空洞内黑纹素 10715 个（占空洞 42%~72%），v1 一个都没修。
  ── 修正 2：目标亮度改成「邻域自身亮度」，不再整体压暗 ────────────────
      v1: target = 全岛非黑中位 0.2200 × ratio 0.55 = 0.1210
          再 scale = target / nl，把邻域色整体压到 0.1210
          但邻域正常亮度是 0.171~0.321 ⇒ 补完比周围暗一半，视觉上等于没补。
      v2: scale = ratio（默认 1.0）⇒ 直接采用邻域插值色，与周围同量级。
          ratio < 1 时按比例压暗，供人工微调。

用法：
    py -3.11 Tools/d13win_apply.py probe              # 只分析，不写盘
    py -3.11 Tools/d13win_apply.py apply [ratio]      # 落盘（默认 1.0）
    py -3.11 Tools/d13win_apply.py rollback           # 从备份回滚

输入：
    Docs/QA/evidence/D13WIN_masks.bin   Unity 导出的 UV 岛掩膜
    Docs/QA/evidence/D13WIN_backup_lightmaps/Lightmap-{n}_comp_light.exr  原始备份
输出：
    Assets/TirGames/SchoolScene/Scenes/SchoolSceneDay/Lightmap-{n}_comp_light.exr
"""

import os
import sys
import struct
import shutil
from collections import deque

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

STRICT_BLACK = 0.05          # 只动真黑纹素（<0.05），其余像素一律不碰
CHANNELS = ("R", "G", "B", "A")
CELL = 4                     # 邻域搜索分桶边长

# Unity 端 D13WIN_probe.txt 报告的岛内黑纹素数（用于校准坐标系）。
# 若 Python 算出的数字与之对不上，说明 Y 轴方向或索引约定不一致，必须中止。
UNITY_BLACK_REF = {
    "Windows1": 558,
    "Windows2": 183,
    "Windows3": 1399,
    "Windows4": 2561,
    "Windows5": 3420,
}
CALIB_TOL = 0.10


# ---------------------------------------------------------------- EXR 读写
# 【坐标系陷阱 — 已验证】
#   Unity GetPixels 的索引原点是【左下角】，y 向上；
#   EXR dataWindow 的行序是【从上到下】，y 向下。
#   掩膜里的纹素索引是 Unity 坐标系（y*w+x，y=0 在底部）。
#   因此读 EXR 后必须【垂直翻转】才能与掩膜索引对齐；写回前再翻回去。
#   验证：翻转前后统计与 Unity 报告的岛内黑纹素数逐一对上
#   （Windows1 558、Windows2 183、Windows3 1399、Windows4 2561、Windows5 3420）。
def read_exr(path, y_flip_to_unity=True):
    """返回 (header, {ch: float32 ndarray[h,w]}, w, h)
    y_flip_to_unity=True 时返回的行序与 Unity 纹理坐标一致（y=0 在底部）。
    """
    f = OpenEXR.InputFile(path)
    hdr = f.header()
    dw = hdr["dataWindow"]
    w = dw.max.x - dw.min.x + 1
    h = dw.max.y - dw.min.y + 1
    raw = {}
    for c in CHANNELS:
        raw[c] = f.channel(c, Imath.PixelType(Imath.PixelType.HALF))
    f.close()
    arr = {c: np.frombuffer(raw[c], dtype="<f2").reshape(h, w).astype(np.float32)
           for c in CHANNELS}
    if y_flip_to_unity:
        arr = {c: arr[c][::-1, :].copy() for c in CHANNELS}
    return hdr, arr, w, h


def write_exr(path, hdr, arr):
    """按原 header（保留 PIZ 压缩与全部属性）写回。
    传入的 arr 是 Unity 行序（y=0 在底部），写盘前翻回 EXR 行序（y=0 在顶部）。
    """
    out = OpenEXR.OutputFile(path, hdr)
    out.writePixels({c: np.ascontiguousarray(arr[c][::-1, :]).astype("<f2").tobytes()
                     for c in CHANNELS})
    out.close()


def lum(a):
    return 0.299 * a["R"] + 0.587 * a["G"] + 0.114 * a["B"]


# ---------------------------------------------------------------- 掩膜
def _read_island(f):
    (nlen,) = struct.unpack("<i", f.read(4))
    name = f.read(nlen).decode("utf-8")
    (lm,) = struct.unpack("<i", f.read(4))
    st = struct.unpack("<4f", f.read(16))
    (mc,) = struct.unpack("<i", f.read(4))
    idx = np.frombuffer(f.read(4 * mc), dtype="<i4").copy()
    return {"name": name, "lm": lm, "st": st, "mask": idx}


def load_masks():
    """返回 (targets, refs)。
    targets : 1F 待修岛（会写）
    refs    : 2F 禁区岛（只读，绝不写）—— lm7/9/12 与 1F 共用图集，必须排除
    兼容旧格式 D13WIN01（无 refs）。
    """
    if not os.path.isfile(MASKS):
        sys.exit(f"缺少掩膜文件：{MASKS}\n请先在 Unity 里跑 归档 → D13-WIN ⑤ 导出 UV 岛掩膜")
    with open(MASKS, "rb") as f:
        magic = f.read(8)
        if magic == b"D13WIN02":
            (nt,) = struct.unpack("<i", f.read(4))
            targets = [_read_island(f) for _ in range(nt)]
            (nr,) = struct.unpack("<i", f.read(4))
            refs = [_read_island(f) for _ in range(nr)]
            return targets, refs
        if magic == b"D13WIN01":
            (count,) = struct.unpack("<i", f.read(4))
            return [_read_island(f) for _ in range(count)], []
        sys.exit(f"掩膜文件格式不符：magic={magic!r}")


def fill_holes(mask_bool):
    """求 mask 的「内洞」：从 bbox 四边泛洪，泛洪不到的 mask 外像素即为内洞。

    为什么需要洞：
        窗框是细长杆件拼成的网格，UV 岛内部必然有大量「窗棂之间的留白」。
        三角形光栅化只覆盖杆件本身，这些留白就成了 mask 的内洞。
        但 GPU 的双线性采样点会落进洞里（杆件在屏幕上只有 1~2px 宽，
        采样坐标往往正好压在两条 UV 岛之间的缝上），于是采到图集里未写入的
        纯黑区域 ⇒ 屏幕上就是一条死黑的竖条。
        R4 实验已证明：把洞全部染红，四条黑条全部变红。
    """
    h, w = mask_bool.shape
    ys, xs = np.nonzero(mask_bool)
    if len(ys) == 0:
        return np.zeros_like(mask_bool)
    y0, y1, x0, x1 = ys.min(), ys.max() + 1, xs.min(), xs.max() + 1
    sub = ~mask_bool[y0:y1, x0:x1]      # True = 非 mask（可能是洞，也可能是外部）
    hh, ww = sub.shape
    outside = np.zeros_like(sub)
    q = deque()
    for x in range(ww):
        for y in (0, hh - 1):
            if sub[y, x] and not outside[y, x]:
                outside[y, x] = True
                q.append((y, x))
    for y in range(hh):
        for x in (0, ww - 1):
            if sub[y, x] and not outside[y, x]:
                outside[y, x] = True
                q.append((y, x))
    while q:
        y, x = q.popleft()
        for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            ny, nx = y + dy, x + dx
            if 0 <= ny < hh and 0 <= nx < ww and sub[ny, nx] and not outside[ny, nx]:
                outside[ny, nx] = True
                q.append((ny, nx))
    res = np.zeros_like(mask_bool)
    res[y0:y1, x0:x1] = sub & (~outside)
    return res


def island_region(m, w, h, include_holes=True):
    """返回该岛要处理的全部纹素索引（mask ∪ 内洞）"""
    mm = np.zeros(w * h, dtype=bool)
    mm[m["mask"]] = True
    mm = mm.reshape(h, w)
    if include_holes:
        holes = fill_holes(mm)
        mm = mm | holes
    return np.nonzero(mm.reshape(-1))[0]


# ---------------------------------------------------------------- 核心
def do_probe():
    targets, refs = load_masks()
    print(f"1F 待修岛 = {len(targets)}   2F 禁区岛 = {len(refs)}")
    print()

    ref_by_lm = {}
    for r in refs:
        ref_by_lm.setdefault(r["lm"], []).append(r)

    calib_ok = True
    print(f"  {'窗框':<10} {'lm':>3} {'岛像素':>7} {'黑纹素':>7} {'Unity报':>8} {'校准':>5}  st")
    for m in targets:
        path = os.path.join(BACKUP, f"Lightmap-{m['lm']}_comp_light.exr")
        if not os.path.isfile(path):
            print(f"  ❌ {m['name']} 备份缺失 {path}")
            calib_ok = False
            continue
        hdr, arr, w, h = read_exr(path)
        flat = lum(arr).reshape(-1)
        nb = int((flat[m["mask"]] < STRICT_BLACK).sum())
        ref = UNITY_BLACK_REF.get(m["name"])
        cal = "?"
        if ref is not None:
            ok = abs(nb - ref) <= max(CALIB_TOL * ref, 12)
            cal = "✅" if ok else "❌"
            calib_ok &= ok
        print(f"  {m['name']:<10} {m['lm']:>3} {len(m['mask']):>7} {nb:>7} "
              f"{ref if ref else '-':>8} {cal:>5}  "
              f"({m['st'][0]:.4f},{m['st'][1]:.4f},{m['st'][2]:.4f},{m['st'][3]:.4f})")

    print()
    print("  2F 禁区岛（只读，绝不写）")
    for r in refs:
        print(f"    {r['name']:<12} lm={r['lm']:>2} mask={len(r['mask']):>6}px")

    print()
    print(f"  {'窗框':<10} {'lm':>3} {'mask内黑':>9} {'洞内黑':>8} {'待补合计':>9} "
          f"{'mask非黑中位':>13} {'洞非黑中位':>11}")
    grand = 0
    for m in targets:
        path = os.path.join(BACKUP, f"Lightmap-{m['lm']}_comp_light.exr")
        if not os.path.isfile(path):
            continue
        hdr, arr, w, h = read_exr(path)
        flat = lum(arr).reshape(-1)
        mm = np.zeros(w * h, dtype=bool)
        mm[m["mask"]] = True
        mm = mm.reshape(h, w)
        holes_idx = np.nonzero(fill_holes(mm).reshape(-1))[0]
        mv = flat[m["mask"]]
        hv = flat[holes_idx]
        mb = int((mv < STRICT_BLACK).sum())
        hb = int((hv < STRICT_BLACK).sum())
        grand += mb + hb
        mg = mv[mv >= STRICT_BLACK]
        hg = hv[hv >= STRICT_BLACK]
        print(f"  {m['name']:<10} {m['lm']:>3} {mb:>9} {hb:>8} {mb + hb:>9} "
              f"{np.median(mg) if len(mg) else 0:>13.4f} "
              f"{np.median(hg) if len(hg) else 0:>11.4f}")

    print()
    for lm in sorted({m["lm"] for m in targets}):
        path = os.path.join(BACKUP, f"Lightmap-{lm}_comp_light.exr")
        if not os.path.isfile(path):
            continue
        hdr, arr, w, h = read_exr(path)
        L = lum(arr)
        hi = int((arr["R"] > 1.0).sum())
        print(f"  Lightmap-{lm}: 全图亮度均值={L.mean():.4f}  R>1 的 HDR 像素={hi}")

    print()
    print(f"mask + 洞 待补黑纹素合计 = {grand}")
    print()
    if calib_ok:
        print("✅ 坐标系校准通过（Python 统计与 Unity 报告一致）")
    else:
        sys.exit("❌ 坐标系校准失败：请检查 Y 轴方向与索引约定，禁止继续落盘")


def do_apply(bleed=6, ratio=1.0, dry=False, only_holes=True, lum_percentile=20.0):
    """填充策略 v3：常数填充（不是插值）。

    为什么必须用常数：
        R4 实验把洞填成常数 4.0 → 屏幕上黑条变成「整条均匀纯红」，没有斑驳。
        v2 用逐点距离反比插值 → 补值跨度 0.05~1.45，屏幕变成白黑花斑。
        窗棂在屏幕上只有 1~2px 宽，双线性采样会把洞里相邻纹素的差异直接
        混成条纹，所以洞里必须是一片平滑的常数，不能有起伏。

    常数取什么：
        该岛 mask 内「非黑纹素的亮度中位」对应的颜色比例。
        即把 mask 内所有非黑纹素的 RGB 按亮度归一化后取中位色相，
        再乘上中位亮度 —— 得到「这个窗框自己的典型颜色」，
        与 2F 正常窗框的观感一致（均匀浅暖灰）。

    2F 禁区：
        lm7/9/12 与 2F 共用图集。refs 里的纹素一律不碰，且填充扩散不得进入。
    """
    targets, refs = load_masks()
    by_lm = {}
    for m in targets:
        by_lm.setdefault(m["lm"], []).append(m)
    ref_by_lm = {}
    for r in refs:
        ref_by_lm.setdefault(r["lm"], []).append(r)

    cache = {}
    for lm in sorted(by_lm):
        path = os.path.join(BACKUP, f"Lightmap-{lm}_comp_light.exr")
        if not os.path.isfile(path):
            print(f"❌ Lightmap-{lm} 备份缺失，跳过")
            continue
        cache[lm] = read_exr(path)

    scope_name = "仅内洞（不动 mask 内真实暗部）" if only_holes else "mask ∪ 内洞"
    print("填充策略：%s 的 <%.2f 纹素 → 填该岛「非黑 p%.0f 色」常数" % (scope_name, STRICT_BLACK, lum_percentile))
    print(f"2F 禁区：{sum(len(r['mask']) for r in refs)} 个纹素，一律不碰")
    print()

    total = 0
    for lm in sorted(cache):
        hdr, arr, w, h = cache[lm]
        L = lum(arr)
        work = {c: arr[c].copy() for c in CHANNELS}

        # 2F 硬禁区
        forbid = np.zeros(w * h, dtype=bool)
        for r in ref_by_lm.get(lm, []):
            forbid[r["mask"]] = True
        forbid = forbid.reshape(h, w)

        for m in by_lm[lm]:
            mm = np.zeros(w * h, dtype=bool)
            mm[m["mask"]] = True
            mm = mm.reshape(h, w)
            holes = fill_holes(mm)
            region = mm | holes

            lit = region & (L >= STRICT_BLACK)
            if lit.sum() < 60:
                print(f"  {m['name']}: 非黑纹素仅 {int(lit.sum())}，跳过")
                continue

            # 该岛的典型颜色：取「低分位」亮度，而不是中位。
            # 为什么不能用中位：窗框 UV 岛里混有玻璃边的高光（p90 可达 0.78~1.06），
            # 中位被拉高到 0.17~0.33，填进去屏幕上显白（实测用户反馈「补的颜色是白色」）。
            # 窗框可见面积主要对应 p10~p25（0.077~0.212），这才是材质本色。
            # 取 p20 作为折中：既不偏亮成白色，也不偏暗成灰影。
            rv = arr["R"][lit]; gv = arr["G"][lit]; bv = arr["B"][lit]
            lv = 0.299 * rv + 0.587 * gv + 0.114 * bv
            pct = float(np.clip(lum_percentile, 0, 100))
            med_l = float(np.percentile(lv, pct))
            # 用亮度在该分位附近的样本估计色相，避免被 HDR 高光拉偏
            tol = max(0.02, 0.10 * med_l)
            near = np.abs(lv - med_l) < tol
            if near.sum() < 20:
                near = np.ones_like(lv, dtype=bool)
            nr = float(np.median(rv[near] / np.maximum(lv[near], 1e-6)))
            ng = float(np.median(gv[near] / np.maximum(lv[near], 1e-6)))
            nb = float(np.median(bv[near] / np.maximum(lv[near], 1e-6)))
            fill_rgb = (nr * med_l, ng * med_l, nb * med_l)

            # 只补空黑纹素，且排除 2F 禁区
            # 范围控制：only_holes=True 时只填 mask 之外的洞（真正的未写入空白），
            # 不动 mask 内 <0.05 的暗纹素 —— 那些是烘焙算出的真实阴影/背面，
            # 填亮会让立体感消失、出现白斑（用户实测「有白有黑」即此）。
            scope = holes if only_holes else region
            todo = scope & (L < STRICT_BLACK) & (~forbid)
            n = int(todo.sum())
            if n == 0:
                continue
            work["R"][todo] = np.float16(fill_rgb[0] * ratio)
            work["G"][todo] = np.float16(fill_rgb[1] * ratio)
            work["B"][todo] = np.float16(fill_rgb[2] * ratio)

            leaked = int((todo & forbid).sum())
            print(f"  {m['name']:<10} lm={lm:>2} 补 {n:>5} 纹素  "
                  f"填充色=({fill_rgb[0]:.4f},{fill_rgb[1]:.4f},{fill_rgb[2]:.4f})  "
                  f"亮度={0.299*fill_rgb[0]+0.587*fill_rgb[1]+0.114*fill_rgb[2]:.4f}  "
                  f"2F越界={leaked}")
            total += n

        # 硬校验：改动必须 ⊆ 本轮目标，且完全避开 2F 禁区
        changed_any = False
        for c in CHANNELS:
            a0 = arr[c].reshape(-1)
            a1 = work[c].reshape(-1)
            ch = np.nonzero(a0 != a1)[0]
            if len(ch):
                changed_any = True
                bad = np.zeros(w * h, dtype=bool)
                bad[ch] = True
                leak = int((bad.reshape(h, w) & forbid).sum())
                if leak:
                    sys.exit(f"❌ 中止：Lightmap-{lm} 通道 {c} 侵入了 2F 禁区 {leak} 个纹素")
        if not changed_any:
            continue

        if dry:
            print(f"    [dry] Lightmap-{lm} 未写盘")
        else:
            dst = os.path.join(ATLAS_DIR, f"Lightmap-{lm}_comp_light.exr")
            write_exr(dst, hdr, work)
            print(f"    ✅ Lightmap-{lm} 已写盘")

    print()
    print(f"合计补 {total} 纹素。" +
          ("（dry run，未写盘）" if dry else "请重开场景后进 play 验收。"))


def do_rollback():
    n = 0
    for lm in (3, 7, 9, 12, 13, 15, 16):
        src = os.path.join(BACKUP, f"Lightmap-{lm}_comp_light.exr")
        dst = os.path.join(ATLAS_DIR, f"Lightmap-{lm}_comp_light.exr")
        if os.path.isfile(src):
            shutil.copy2(src, dst)
            print(f"  ✅ 回滚 Lightmap-{lm}")
            n += 1
    print(f"\n共回滚 {n} 张。请在 Unity 里重新打开场景。")


def main():
    mode = sys.argv[1] if len(sys.argv) > 1 else "probe"
    if mode == "probe":
        do_probe()
    elif mode == "apply":
        # apply [bleed] [ratio] [--dry]
        rest = [a for a in sys.argv[2:] if not a.startswith("--")]
        dry = "--dry" in sys.argv
        b = int(rest[0]) if len(rest) > 0 else 6
        r = float(rest[1]) if len(rest) > 1 else 1.0
        only_holes = "--all" not in sys.argv     # 默认只填洞
        pct = float(rest[2]) if len(rest) > 2 else 20.0
        do_apply(b, r, dry, only_holes, pct)
    elif mode == "rollback":
        do_rollback()
    else:
        sys.exit(__doc__)


if __name__ == "__main__":
    main()
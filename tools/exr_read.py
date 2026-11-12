"""最小 EXR 读取器（ZIP/ZIPS 压缩 + HALF/FLOAT）。

Unity 导出的 lightmap EXR 用 ZIP 压缩，而 EXR 的 ZIP 在 zlib 之前还有两步
变换，漏掉会导致字节错位、读出 NaN。顺序（参照 OpenEXR ImfZip.cpp 的
Zip::uncompress）：

    解压：zlib inflate → reconstruct(undo predictor) → reorder(undo interleave)

本模块按此实现。PIZ 压缩需要 libOpenEXR，本机装上不可用（离线），
遇到会明确抛 NotImplementedError 而不是静默读错。

用法：
    from exr_read import read_exr, stats, row_means
    w, h, ch = read_exr(path)
    ch["R"]        # list[float]，行优先，index = y*w+x，y=0 是图像【最下面】一行
"""
import struct
import zlib

PIXEL_HALF = 1
PIXEL_FLOAT = 2

_MAGIC = b"\x76\x2f\x31\x01"


def _undo_predictor(buf):
    """逆 predictor：t[i] = t[i-1] + t[i] - 128（uchar 截断）。"""
    b = bytearray(buf)
    for i in range(1, len(b)):
        b[i] = (b[i - 1] + b[i] - 128) & 0xFF
    return b


def _undo_interleave(buf):
    """逆 interleave：数据原是「前半 / 后半」两段，还原为逐像素交错。"""
    n = len(buf)
    half = (n + 1) // 2
    out = bytearray(n)
    i1 = 0
    i2 = half
    for i in range(n):
        if i % 2 == 0:
            out[i1] = buf[i]
            i1 += 1
        else:
            out[i2] = buf[i]
            i2 += 1
    return out


def read_exr(path):
    d = open(path, "rb").read()
    if d[:4] != _MAGIC:
        raise ValueError("not an OpenEXR file: " + path)

    i = 8
    info = {}
    while True:
        j = d.index(b"\x00", i)
        name = d[i:j].decode("ascii")
        i = j + 1
        if not name:
            break
        k = d.index(b"\x00", i)
        typ = d[i:k].decode("ascii")
        i = k + 1
        size = struct.unpack("<i", d[i:i + 4])[0]
        i += 4
        info[name] = (typ, d[i:i + size])
        i += size

    compression = info["compression"][1][0]
    if compression not in (2, 3):
        raise NotImplementedError(
            "只支持 ZIP(3)/ZIPS(2)，当前 compression=%d（4=PIZ 需 libOpenEXR）" % compression)

    x0, y0, x1, y1 = struct.unpack("<4i", info["dataWindow"][1])
    w, h = x1 - x0 + 1, y1 - y0 + 1

    chv = info["channels"][1]
    p = 0
    chans = []
    while chv[p] != 0:
        e = chv.index(b"\x00", p)
        cn = chv[p:e].decode("ascii")
        p = e + 1
        pt = struct.unpack("<i", chv[p:p + 4])[0]
        p += 4
        p += 4          # pLinear(1) + reserved(3)
        p += 8          # xSampling + ySampling
        chans.append((cn, pt))

    per_sample = sum(2 if pt == PIXEL_HALF else 4 for _, pt in chans)

    lines_per_block = 1 if compression == 2 else 16
    nblocks = (h + lines_per_block - 1) // lines_per_block
    offsets = struct.unpack("<%dQ" % nblocks, d[i:i + 8 * nblocks])

    out = {cn: [0.0] * (w * h) for cn, _ in chans}
    for bi in range(nblocks):
        o = offsets[bi]
        ln = struct.unpack("<i", d[o + 4:o + 8])[0]
        raw = zlib.decompress(d[o + 8:o + 8 + ln])

        nl = min(lines_per_block, h - bi * lines_per_block)
        expect = nl * w * per_sample
        if len(raw) == expect:
            pass                        # 无 predictor（少数写入器如此）
        else:
            raw = _undo_interleave(_undo_predictor(raw))
        if len(raw) != expect:
            raise ValueError(
                "block %d 解出 %d 字节，期望 %d（数据布局不符）" % (bi, len(raw), expect))

        y = bi * lines_per_block
        p = 0
        for cn, pt in chans:
            arr = out[cn]
            for ly in range(nl):
                base = (y + ly) * w
                if pt == PIXEL_HALF:
                    vals = struct.unpack_from("<%de" % w, raw, p)
                    p += 2 * w
                else:
                    vals = struct.unpack_from("<%df" % w, raw, p)
                    p += 4 * w
                for x in range(w):
                    arr[base + x] = vals[x]
    return w, h, out


def stats(ch, w, x0, x1, y0, y1):
    """矩形内亮度统计（0.299R+0.587G+0.114B）。"""
    R, G, B = ch["R"], ch["G"], ch["B"]
    vals = []
    for y in range(y0, y1 + 1):
        base = y * w
        for x in range(x0, x1 + 1):
            i = base + x
            vals.append(0.299 * R[i] + 0.587 * G[i] + 0.114 * B[i])
    vals.sort()
    n = len(vals)
    if n == 0:
        raise ValueError("empty rect")
    return {
        "n": n,
        "mean": sum(vals) / n,
        "median": vals[n // 2] if n % 2 else (vals[n // 2 - 1] + vals[n // 2]) / 2,
        "min": vals[0],
        "max": vals[-1],
        "black_pct": 100.0 * sum(1 for v in vals if v < 0.005) / n,
    }


def row_means(ch, w, x0, x1, y0, y1):
    """逐行平均亮度，返回 [(y, mean), ...]。"""
    R, G, B = ch["R"], ch["G"], ch["B"]
    res = []
    for y in range(y0, y1 + 1):
        base = y * w
        s = 0.0
        for x in range(x0, x1 + 1):
            i = base + x
            s += 0.299 * R[i] + 0.587 * G[i] + 0.114 * B[i]
        res.append((y, s / (x1 - x0 + 1)))
    return res


def col_means(ch, w, x0, x1, y0, y1):
    """逐列平均亮度，返回 [(x, mean), ...]。"""
    R, G, B = ch["R"], ch["G"], ch["B"]
    res = []
    for x in range(x0, x1 + 1):
        s = 0.0
        for y in range(y0, y1 + 1):
            i = y * w + x
            s += 0.299 * R[i] + 0.587 * G[i] + 0.114 * B[i]
        res.append((x, s / (y1 - y0 + 1)))
    return res
#!/usr/bin/env bash
# ============================================================================
# P26 素材转码脚本
#
# 把 AI 生成的视频（或任何来源）转成 Unity 项目要的标准参数：
#   16:10 / 1920x1200 / H.264 / yuv420p / 无黑边 / faststart
#
# 用法：
#   ./tools/p26_transcode.sh 输入.mp4 [输出.mp4]
#
# 不带输出名时，输出到 Assets/StreamingAssets/PC/master/ 下同名文件。
#
# 依赖：ffmpeg / ffprobe 在 PATH 里
# ============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$(dirname "$SCRIPT_DIR")"
OUT_DIR="$PROJECT_DIR/Assets/StreamingAssets/PC/master"

W=1920
H=1200          # 16:10，同时喂显示器和幕布时裁切最小
CRF=18
FPS=30

if [ $# -lt 1 ]; then
    echo "用法: $0 输入.mp4 [输出.mp4]"
    echo ""
    echo "标准输出目录: $OUT_DIR"
    echo "建议文件名: boot.mp4 / ppt.mp4 / lesson.mp4"
    exit 1
fi

IN="$1"
if [ ! -f "$IN" ]; then
    echo "✘ 找不到文件: $IN"
    exit 1
fi

if [ $# -ge 2 ]; then
    OUT="$2"
else
    mkdir -p "$OUT_DIR"
    OUT="$OUT_DIR/$(basename "$IN")"
fi

echo "════════════════════════════════════════════════"
echo " 输入: $IN"
echo " 输出: $OUT"
echo "════════════════════════════════════════════════"

# ── 1. 先看原素材规格 ────────────────────────────────────────────────
echo ""
echo "[1/4] 原素材规格"
ffprobe -v error -select_streams v:0 \
    -show_entries stream=width,height,r_frame_rate,codec_name,pix_fmt,duration \
    -of default=noprint_wrappers=1 "$IN" | sed 's/^/      /'

# ── 2. 检测自带黑边 ──────────────────────────────────────────────────
echo ""
echo "[2/4] 黑边检测"
DETECT=$(ffmpeg -hide_banner -i "$IN" -vf "cropdetect=limit=24:round=2:reset=0" \
         -f null - 2>&1 | grep -oE "crop=[0-9]+:[0-9]+:[0-9]+:[0-9]+" | sort -u | tail -1 || true)
echo "      $DETECT"

IN_W=$(ffprobe -v error -select_streams v:0 -show_entries stream=width  -of csv=p=0 "$IN")
IN_H=$(ffprobe -v error -select_streams v:0 -show_entries stream=height -of csv=p=0 "$IN")
CW=$(echo "$DETECT" | cut -d: -f1 | tr -d 'crop=')
CH=$(echo "$DETECT" | cut -d: -f2)
CX=$(echo "$DETECT" | cut -d: -f3)
CY=$(echo "$DETECT" | cut -d: -f4)

PRE=""
if [ -n "$CW" ] && [ "$CW" != "$IN_W" ]; then
    echo "      ⚠ 素材自带黑边（${IN_W}x${IN_H} → 内容 ${CW}x${CH}），先裁掉"
    PRE="crop=${CW}:${CH}:${CX}:${CY},"
else
    echo "      ✔ 无黑边"
fi

# ── 3. 转码 ──────────────────────────────────────────────────────────
echo ""
echo "[3/4] 转码 → ${W}x${H} (16:10) / H.264 / crf $CRF / ${FPS}fps"
# scale=increase + crop = cover（铺满再裁边），和 Unity 里的显示逻辑一致
ffmpeg -hide_banner -loglevel error -y -i "$IN" \
    -vf "${PRE}scale=${W}:${H}:force_original_aspect_ratio=increase,crop=${W}:${H},fps=${FPS}" \
    -c:v libx264 -preset slow -crf "$CRF" -pix_fmt yuv420p \
    -c:a aac -b:a 128k -ac 2 \
    -movflags +faststart \
    "$OUT"

# ── 4. 复检 ──────────────────────────────────────────────────────────
echo ""
echo "[4/4] 复检"
ffprobe -v error -select_streams v:0 \
    -show_entries stream=width,height,r_frame_rate,codec_name,pix_fmt,duration \
    -of default=noprint_wrappers=1 "$OUT" | sed 's/^/      /'

FINAL=$(ffmpeg -hide_banner -i "$OUT" -vf "cropdetect=limit=24:round=2:reset=0" \
        -f null - 2>&1 | grep -oE "crop=[0-9]+:[0-9]+:[0-9]+:[0-9]+" | sort -u | tail -1 || true)
echo "      黑边复检: $FINAL"
if [ "$FINAL" = "crop=${W}:${H}:0:0" ]; then
    echo "      ✔ 通过"
else
    echo "      ⚠ 复检结果不是 crop=${W}:${H}:0:0，请检查"
fi

echo ""
echo "完成: $OUT"
SIZE=$(du -h "$OUT" | cut -f1)
echo "大小: $SIZE （建议 < 50MB）"

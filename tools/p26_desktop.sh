#!/usr/bin/env bash
# ============================================================================
# P26 从 boot.mp4 提取"桌面"待机图
#
# 退出 PPT / 视频时显示这张图，而不是重播开机动画
# （重播看起来像"又开机了一遍"，2026-10-27 用户报的 bug）。
#
# 用法：
#   ./tools/p26_desktop.sh [源视频] [输出]
#
# 默认：Assets/StreamingAssets/PC/master/boot.mp4
#    →  Assets/StreamingAssets/PC/master/desktop.jpg
#
# **换 boot.mp4 之后要重跑这个脚本**，否则桌面图还是旧的那一帧。
# ============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$(dirname "$SCRIPT_DIR")"
MASTER="$PROJECT_DIR/Assets/StreamingAssets/PC/master"

IN="${1:-$MASTER/boot.mp4}"
OUT="${2:-$MASTER/desktop.jpg}"

if [ ! -f "$IN" ]; then
    echo "✘ 找不到源视频: $IN"
    exit 1
fi

echo "源视频: $IN"
echo "输出  : $OUT"

# -sseof -0.15：从末尾前 0.15 秒开始取，避免某些编码的末帧不完整
ffmpeg -hide_banner -loglevel error -y \
    -sseof -0.15 -i "$IN" \
    -frames:v 1 -q:v 3 \
    "$OUT"

if [ ! -f "$OUT" ]; then
    echo "✘ 提取失败"
    exit 1
fi

SIZE=$(du -h "$OUT" | cut -f1)
echo ""
echo "✔ 完成（$SIZE）"
echo ""
echo "这张图就是「开机完成后的桌面」，退出演示时显示。"
echo "⚠ 换 boot.mp4 之后要重跑本脚本，否则桌面图还是旧的。"

#!/usr/bin/env bash
# ============================================================================
# P26 素材导入脚本
#
# 把任意来源的视频转成标准参数并放进 StreamingAssets，自动备份旧文件。
#
# 用法：
#   ./tools/p26_import.sh <源视频> <槽位>
#
# 槽位：
#   boot      开机动画（主脑 + 18 台 + 幕布共用）
#   ppt       「打开PPT」（旧版视频，现在 PPT 用图片了，保留兼容）
#   lesson    「视频播放」
#   shutdown  关机动画（点关机时先播，播完才真正断电）
#
# 例：
#   ./tools/p26_import.sh ~/Downloads/豆包视频.mp4 boot
#   ./tools/p26_import.sh video/教学演示.mp4 lesson
#
# 做了什么：
#   1. 检测源视频规格和黑边
#   2. 自动裁掉黑边（如果有）
#   3. cover 转成 1920×1200 (16:10) / H.264 / yuv420p / 30fps
#   4. 备份旧文件到 _backup/
#   5. 复检并报告
# ============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$(dirname "$SCRIPT_DIR")"
# 素材根目录。注意是 `PC/` 而不是 `PC/master/` ——
# 目标参数里已经带了 `master/` / `videos/` 这些子目录
OUT_DIR="$PROJECT_DIR/Assets/StreamingAssets/PC"
# 备份放在 StreamingAssets **外面** —— 那里的所有文件都会被打进包体
BACKUP_DIR="$PROJECT_DIR/video/_backup"

W=1920
H=1200
CRF=18
FPS=30
usage() {
    echo "用法: $0 <源视频> <目标>"
    echo ""
    echo "目标可以是简写槽位："
    echo "  boot        → master/boot.mp4      开机动画"
    echo "  shutdown    → master/shutdown.mp4  关机动画"
    echo "  ppt         → master/ppt.mp4       （旧版，PPT 已改用图片）"
    echo "  lesson      → videos/01_lesson.mp4"
    echo ""
    echo "也可以直接写相对 PC/ 的路径："
    echo "  ./tools/p26_import.sh 新视频.mp4 videos/02_新内容.mp4"
    echo "  ./tools/p26_import.sh 关机.mp4   master/shutdown.mp4"
    exit 1
}

[ $# -lt 2 ] && usage

IN="$1"
SLOT="$2"

# 简写槽位 → 实际相对路径；带 `/` 的按路径处理
case "$SLOT" in
    boot)     REL="master/boot.mp4" ;;
    shutdown) REL="master/shutdown.mp4" ;;
    ppt)      REL="master/ppt.mp4" ;;
    lesson)   REL="videos/01_lesson.mp4" ;;
    */*)      REL="$SLOT" ;;
    *) echo "✘ 不认识的目标：$SLOT（既不是槽位也不像路径）"; usage ;;
esac

if [ ! -f "$IN" ]; then
    echo "✘ 找不到源文件: $IN"
    exit 1
fi

OUT="$OUT_DIR/$REL"
mkdir -p "$(dirname "$OUT")"

echo "════════════════════════════════════════════════"
echo " 源文件: $IN"
echo " 目标  : $REL"
echo "════════════════════════════════════════════════"

# ── 1. 源规格 ────────────────────────────────────────────────────────
echo ""
echo "[1/5] 源视频规格"
ffprobe -v error -select_streams v:0 \
    -show_entries stream=width,height,r_frame_rate,codec_name,pix_fmt,duration \
    -of default=noprint_wrappers=1 "$IN" | sed 's/^/      /'

HAS_AUDIO=$(ffprobe -v error -select_streams a:0 -show_entries stream=codec_name -of csv=p=0 "$IN" || true)
if [ -n "$HAS_AUDIO" ]; then
    echo "      音轨: 有 ($HAS_AUDIO)"
else
    echo "      音轨: 无（转码时补一条静音轨，避免 Unity 那边 SetTargetAudioSource 报错）"
fi

# ── 2. 黑边检测 ──────────────────────────────────────────────────────
echo ""
echo "[2/5] 黑边检测"
DETECT=$(ffmpeg -hide_banner -i "$IN" -vf "cropdetect=limit=24:round=2:reset=0" \
         -f null - 2>&1 | grep -oE "crop=[0-9]+:[0-9]+:[0-9]+:[0-9]+" | sort -u | tail -1 || true)
echo "      $DETECT"

IN_W=$(ffprobe -v error -select_streams v:0 -show_entries stream=width  -of csv=p=0 "$IN")
IN_H=$(ffprobe -v error -select_streams v:0 -show_entries stream=height -of csv=p=0 "$IN")
CW=$(echo "$DETECT" | sed 's/crop=//' | cut -d: -f1)
CH=$(echo "$DETECT" | cut -d: -f2)
CX=$(echo "$DETECT" | cut -d: -f3)
CY=$(echo "$DETECT" | cut -d: -f4)

PRE=""
# ⚠️ 判断"有没有黑边"必须同时看**宽和高**。
# 2026-10-27 踩过：`数字孪生科普.mp4` 是上下黑边（912x544 → 912x534），
# 宽度完全没变，只判 `CW != IN_W` 会误判成"无黑边"，黑边就原样转进去了。
if [ -n "$CW" ] && { [ "$CW" != "$IN_W" ] || [ "$CH" != "$IN_H" ]; }; then
    # 检测到黑边 → 裁掉，并**额外内缩 1.5%**。
    #
    # 为什么要多裁一点：`cropdetect` 给的边界通常偏松（黑边渐变到内容有几像素过渡），
    # 而且后面 cover 缩放会把残留的几像素黑边**放大扩散**。
    # 实测：源 912x544 检测出上下各 6px 黑边，裁成 912x532 后 cover 到 1230x768，
    # 复检又冒出上下各 10px 黑边（6×1.44 缩放 ≈ 9px，加上插值）。
    # 多裁 1.5% 就干净了。
    IW=$(( CW * 985 / 1000 ))
    IH=$(( CH * 985 / 1000 ))
    IX=$(( CX + (CW - IW) / 2 ))
    IY=$(( CY + (CH - IH) / 2 ))
    # 对齐偶数
    IW=$(( IW - IW % 2 )); IH=$(( IH - IH % 2 ))
    echo "      ⚠ 自带黑边 ${IN_W}x${IN_H} → 内容 ${CW}x${CH}，"
    echo "        再内缩 1.5% 抵消缩放扩散 → ${IW}x${IH}+${IX}+${IY}"
    PRE="crop=${IW}:${IH}:${IX}:${IY},"
else
    echo "      ✔ 无黑边"
fi

# ── 竖屏检查 ─────────────────────────────────────────────────────────
if [ "$IN_H" -gt "$IN_W" ]; then
    echo ""
    echo "      ⚠⚠ 这是**竖屏**视频（${IN_W}x${IN_H}）"
    echo "          我们的载体是横屏 16:10（显示器 1.62 / 幕布 1.47）"
    echo "          竖屏转横屏只能裁掉 70%+ 画面，或者加黑边（黑边会被当画面显示）"
    echo "          建议换一个横屏素材。继续执行会裁掉大量内容 —— 5 秒后继续，Ctrl+C 取消"
    sleep 5
fi

# ── 按时长自动选参数 ─────────────────────────────────────────────────
# 为什么：StreamingAssets 里所有文件都进包体。
# 2 分钟的 1920×1200 crf18 要 200~400MB，完全不能接受。
# 按时长分档，短片子给高画质，长片子降分辨率+提 CRF 控体积。
DUR=$(ffprobe -v error -select_streams v:0 -show_entries stream=duration -of csv=p=0 "$IN")
DUR_INT=$(printf '%.0f' "$DUR" 2>/dev/null || echo 0)

if [ "$DUR_INT" -le 20 ]; then
    W=1920; H=1200; CRF=18
    TIER="短片（≤20s）→ 高画质"
elif [ "$DUR_INT" -le 60 ]; then
    W=1920; H=1200; CRF=21
    TIER="中片（≤60s）→ 标准画质"
else
    W=1600; H=1000; CRF=23
    TIER="长片（>60s）→ 降分辨率控体积"
fi

# ── 不过度放大 ───────────────────────────────────────────────────────
# AI 生成的素材常常只有 720p 甚至更低。硬拉到 1920 宽等于放大 1.5 倍以上，
# 文字和细线会明显糊掉 —— 而画面本来就只有 1386×855 像素，
# 输出比源大太多纯属浪费体积。
# 规则：输出宽度不超过**裁边后**源宽度的 1.35 倍。
EFF_W=$IN_W
if [ -n "$CW" ] && [ "$CW" != "$IN_W" ]; then EFF_W=$CW; fi
EFF_H=$IN_H
if [ -n "$CH" ] && [ "$CH" != "$IN_H" ]; then EFF_H=$CH; fi
MAX_W=$(( EFF_W * 135 / 100 ))
if [ "$W" -gt "$MAX_W" ]; then
    OLD_W=$W
    W=$(( MAX_W - MAX_W % 2 ))
    H=$(( W * 10 / 16 ))
    H=$(( H - H % 2 ))
    TIER="$TIER；源只有 ${EFF_W} 宽，限制放大 → ${W}x${H}（原计划 ${OLD_W}）"
fi

echo ""
echo "      时长 ${DUR_INT}s → $TIER"
echo "      目标 ${W}x${H} / crf $CRF"

# ── 3. 转码 ──────────────────────────────────────────────────────────
echo ""
echo "[3/5] 转码 → ${W}x${H} (16:10) / H.264 / crf $CRF / ${FPS}fps"
TMP="$(mktemp -u).mp4"

if [ -n "$HAS_AUDIO" ]; then
    ffmpeg -hide_banner -loglevel error -y -i "$IN" \
        -vf "${PRE}scale=${W}:${H}:force_original_aspect_ratio=increase,crop=${W}:${H},fps=${FPS}" \
        -c:v libx264 -preset slow -crf "$CRF" -pix_fmt yuv420p \
        -c:a aac -b:a 128k -ac 2 \
        -movflags +faststart "$TMP"
else
    # 无音轨：补一条静音，避免 VideoPlayer 的 SetTargetAudioSource 拿不到轨道
    ffmpeg -hide_banner -loglevel error -y -i "$IN" \
        -f lavfi -i anullsrc=channel_layout=stereo:sample_rate=44100 \
        -vf "${PRE}scale=${W}:${H}:force_original_aspect_ratio=increase,crop=${W}:${H},fps=${FPS}" \
        -c:v libx264 -preset slow -crf "$CRF" -pix_fmt yuv420p \
        -c:a aac -b:a 128k -ac 2 -shortest \
        -movflags +faststart "$TMP"
fi

# ── 4. 备份 + 替换 ───────────────────────────────────────────────────
echo ""
echo "[4/5] 备份并替换"
if [ -f "$OUT" ]; then
    mkdir -p "$BACKUP_DIR"
    STAMP=$(date +%Y%m%d_%H%M%S)
    BASENAME=$(basename "$REL" .mp4)
    cp "$OUT" "$BACKUP_DIR/${BASENAME}_${STAMP}.mp4"
    echo "      旧文件 → _backup/${BASENAME}_${STAMP}.mp4"
else
    echo "      槽位原来是空的，无需备份"
fi
mv "$TMP" "$OUT"
echo "      ✔ 已写入 $OUT"

# ── 5. 复检 ──────────────────────────────────────────────────────────
echo ""
echo "[5/5] 复检"
ffprobe -v error -select_streams v:0 \
    -show_entries stream=width,height,r_frame_rate,codec_name,pix_fmt,duration \
    -of default=noprint_wrappers=1 "$OUT" | sed 's/^/      /'

FINAL=$(ffmpeg -hide_banner -i "$OUT" -vf "cropdetect=limit=24:round=2:reset=0" \
        -f null - 2>&1 | grep -oE "crop=[0-9]+:[0-9]+:[0-9]+:[0-9]+" | sort -u | tail -1 || true)
echo "      黑边复检: $FINAL"
if [ "$FINAL" = "crop=${W}:${H}:0:0" ]; then
    echo "      ✔ 通过"
else
    echo "      ⚠ 不是 crop=${W}:${H}:0:0，请检查"
fi

SIZE=$(du -h "$OUT" | cut -f1)
echo ""
echo "完成: $OUT  ($SIZE)"
echo ""
echo "回 Unity 会自动重新导入，然后 Play 即可。"
echo "如果 Console 报「找不到视频」，检查路径是不是 $REL"

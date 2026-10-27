# P26 素材规范与 AI 生成提示词

> 面向：数字孪生教学场景 · 主脑 PC + 机房 18 台 + 投影幕布
> 更新：2026-10-27

---

## 一、数量限制

### 当前架构：**固定槽位，不是播放列表**

| 槽位 | 文件 | 谁在用 | 可换？ |
|---|---|---|---|
| 开机动画 | `master/boot.mp4` | 主脑 + 18 台 + 幕布 | ✅ 换文件即可 |
| PPT | `master/ppt.mp4` | 「打开PPT」按钮 | ✅ 换文件即可 |
| 教学视频 | `master/lesson.mp4` | 「视频播放」按钮 | ✅ 换文件即可 |
| 关机动画 | `master/shutdown.mp4` | **预留，暂未接入** | ⏳ 需接流程 |

**所以：PPT 1 个、视频 1 个、开机动画 1 个。**

### 如果要多个 PPT / 多个视频

当前面板是**固定按钮**，要支持多素材得改两处：

| 改动 | 内容 | 工作量 |
|---|---|---|
| `PcMediaLibrary` | 从常量改成 `string[]` 列表 + 目录扫描 | 小 |
| `TwinPcMenuPanel` | 按钮从固定 2 个改成"列表 + 上一页/下一页" | 中 |

**建议**：如果只是课堂演示，把多个 PPT 合成**一个视频**（PPT 导出视频本来就是连播），
比改 UI 划算得多。真需要多素材时再说。

---

## 二、参数规范

### 2.1 为什么是 16:10

同一个视频要同时喂给两种载体，两者比例不同：

| 载体 | 尺寸 | 比例 |
|---|---|---|
| 显示器屏幕 | 1386 × 855 px | **1.621** |
| 投影幕布 | 3.11 × 2.12 m | **1.467** |

`cover` 模式的裁切量（每侧）：

| 视频比例 | 显示器 1.62 | 幕布 1.47 |
|---|---|---|
| 1.96:1（现有 boot） | 裁左右 **8.6%** | 裁左右 **12.6%** |
| 16:9（1.778） | 裁左右 4.4% | 裁左右 8.7% |
| **16:10（1.600）** | 裁上下 **0.65%** | 裁左右 **4.16%** |

**结论：用 16:10。** 两边裁切都最小，几乎无损。

### 2.2 完整参数表

| 项 | 要求 | 说明 |
|---|---|---|
| **比例** | **16:10** | 最优，两边裁切都 <5% |
| **分辨率** | **1920×1200** 或 1728×1080 | 显示器屏幕只有 1386×855，1920 宽足够 |
| **编码** | H.264 (libx264) | Unity VideoPlayer 兼容性最好 |
| **像素格式** | yuv420p | 必须，否则部分平台不播 |
| **帧率** | 30 fps | 足够，60 会增大文件 |
| **码率** | 8~12 Mbps | 保证文字锐利 |
| **时长** | 开机 6~10s；PPT/视频 15~60s | 开机动画短，内容可以长 |
| **音频** | AAC 44.1kHz 立体声（可选） | 走 3D 音源，走近更响 |
| **单文件大小** | < 50 MB | StreamingAssets 会进包体 |
| **黑边** | **必须没有** | 有黑边会被当画面内容，裁不掉 |

### 2.3 安全区（重要）

因为 `cover` 会裁掉边缘，**关键内容必须放在中间**：

```
┌─────────────────────────────┐
│  裁掉 4%  │           │ 裁4% │
│           │           │      │
│           │  安全区   │      │
│           │  (中间    │      │
│           │   92%)    │      │
│           │           │      │
└─────────────────────────────┘
```

- 标题、logo、关键图表 → 放中间 85~90% 区域
- 纯背景色、装饰 → 可以延伸到边缘

### 2.4 时长与文件大小

`Assets/StreamingAssets/**` 里**所有文件都会打进包体**，所以时长直接影响安装包大小。

| 槽位 | 建议时长 | 理由 |
|---|---|---|
| `boot.mp4` 开机动画 | **6~15 秒** | 2 分钟的话电脑早开完机了动画还在放，很怪 |
| `ppt.mp4` PPT 演示 | 1~2 分钟 | 合理 |
| `lesson.mp4` 教学视频 | 1~3 分钟 | 合理 |

`p26_import.sh` 会**按时长自动选参数**：

| 时长 | 分辨率 | CRF | 2 分钟约多大 |
|---|---|---|---|
| ≤ 20 秒 | 1920×1200 | 18 | 10~25 MB |
| ≤ 60 秒 | 1920×1200 | 21 | 30~60 MB |
| > 60 秒 | 1600×1000 | 23 | 60~120 MB |

### 2.5 ⚠️ 从抖音/短视频平台下载素材

**必须选横屏视频。**

抖音大部分是竖屏 9:16（1080×1920），而载体是横屏 16:10。竖屏转横屏只有两条路：

| 做法 | 结果 |
|---|---|
| 裁中间一条 | 画面被裁掉 70%+，基本没法看 |
| 加黑边填充 | **黑边会被当成画面内容显示在幕布上**，废掉 |

`p26_import.sh` 检测到竖屏会**警告并暂停 5 秒**让你 Ctrl+C 取消。

其他注意：
- 平台水印会一起进画面（裁边裁不掉中间的水印）
- 二次压缩过的画质偏糊，尽量选高清源
- 版权：课程作业/比赛内部使用一般没问题，公开发布要注意


---

## 三、AI 生成提示词

### 3.1 通用原则

| 原则 | 说明 |
|---|---|
| **不要生成文字** | AI 生成的文字一定是乱码。文字用后期（剪映/PR/PPT）叠加 |
| **主体居中** | 边缘会被裁，见上面安全区 |
| **时长说清楚** | 多数模型默认 4~8 秒，要长片得说 "seamless loop" 或用延长功能 |
| **指定比例** | 明确写 `16:10` 或 `--ar 16:10`（视模型而定） |
| **避免快速运动** | 慢镜头更耐看，也更容易生成稳定 |

### 3.2 `boot.mp4` — 开机动画（6~10 秒）

**中文提示词**（可灵 / 即梦 / 海螺）：

```
科技感开机动画。深蓝到青色的渐变背景，画面中央一个由发光线条构成的校园建筑三维模型缓慢自转，
线条从中心向外扩散生长，逐步勾勒出建筑轮廓与数据流。中心有淡蓝色光晕缓慢呼吸。
镜头极缓慢推近。电影级画质，干净极简，无人物，无文字，深色调，霓虹蓝青光。
比例 16:10，时长 8 秒。
```

**English prompt**（Runway / Pika / Sora）：

```
Futuristic boot-up animation. Deep blue to cyan gradient background. A campus building
3D model made of glowing wireframe lines slowly rotates at the center, lines growing
outward from the center to outline architecture and data streams. Soft blue glow pulsing
at the center. Extremely slow camera push-in. Cinematic, minimal, no people, no text,
dark tone, neon blue and cyan. Aspect ratio 16:10, 8 seconds.
```

**要点**：无文字、深色调、慢运动。这个视频播完会停在最后一帧（桌面），所以最后一帧别太突兀。

### 3.3 `ppt.mp4` — PPT 演示（15~60 秒）

> ⚠️ **强烈建议不要用 AI 视频生成做 PPT。**
> AI 生成的图表数字都是假的、文字是乱码，一眼假。
> 正确做法见 3.5「PPT 素材的正确做法」。

如果确实要用 AI 生成「数据大屏」风格：

**中文**：
```
数字孪生数据可视化大屏界面。深色背景，多个半透明玻璃质感面板悬浮排列，
分别显示校园三维地图、实时数据折线图、设备状态列表、能耗环形图。
数据在持续刷新跳动，图表有缓慢的动画。镜头极缓慢横移。
UI 设计感强，科技蓝配色，清晰锐利，无人物，无文字，比例 16:10，时长 15 秒。
```

**English**：
```
Digital twin data visualization dashboard. Dark background, multiple translucent glass
panels floating in a grid, showing campus 3D map, real-time line charts, device status
lists, energy ring charts. Data refreshing with subtle animation. Extremely slow camera
pan. Strong UI design sense, tech blue palette, sharp and clean, no people, no text.
Aspect ratio 16:10, 15 seconds.
```

### 3.4 `lesson.mp4` — 教学视频（30~60 秒）

**中文**：
```
数字孪生技术教学演示。画面左侧是一栋真实的校园教学楼，右侧同步出现它的半透明三维数字模型，
两者之间用流动的蓝色光效连线连接，表示数据实时同步。镜头缓慢环绕建筑。
明亮的教学风格，蓝白配色，干净背景，科技感但不夸张，无人物，无文字。
比例 16:10，时长 20 秒。
```

**English**：
```
Digital twin technology teaching demo. On the left, a real campus building; on the right,
its translucent 3D digital model appears in sync. Flowing blue light lines connect the two,
representing real-time data synchronization. Slow camera orbit around the building.
Bright educational style, blue and white palette, clean background, tech feel but not
exaggerated, no people, no text. Aspect ratio 16:10, 20 seconds.
```

### 3.5 PPT 素材的正确做法（推荐）

**不要用 AI 视频生成做 PPT。** 用下面两种方式之一：

#### 方案 A：真 PPT 导出视频（最推荐）

1. PowerPoint 做好 5~10 页幻灯片
2. `文件 → 导出 → 创建视频` → 选 **Full HD (1920×1080)**
3. 每页停留 5 秒，勾选"使用录制的计时和旁白"（可选）
4. 导出后用 ffmpeg 转成 16:10（见第四节）

**优点**：文字清晰、数字真实、可控。

#### 方案 B：Canva / Figma 设计稿 + 录屏

1. 做 16:10 画布的设计稿
2. 用 OBS 或系统录屏，逐页切换
3. 同上转码

#### 方案 C：AI 生成背景 + 后期叠字

1. 用 AI 生成**无文字的抽象背景**（数据流、光效、网格）
2. 在剪映/PR 里叠加真实文字和图表
3. 导出

---

## 四、后期处理（ffmpeg）

### 4.1 转成标准参数

```bash
ffmpeg -i 原素材.mp4 \
  -vf "scale=1920:1200:force_original_aspect_ratio=increase,crop=1920:1200" \
  -c:v libx264 -preset slow -crf 18 -pix_fmt yuv420p \
  -c:a aac -b:a 128k -ac 2 \
  -movflags +faststart \
  输出.mp4
```

参数解释：
- `scale=...:increase` + `crop` = **cover**（铺满再裁边），和 Unity 里的逻辑一致
- `crf 18` = 高画质（数字越小越清晰，18 接近视觉无损）
- `-pix_fmt yuv420p` = 兼容性必需
- `-movflags +faststart` = 网页/流式播放优化

### 4.2 检查黑边（**必做**）

```bash
ffmpeg -i 输出.mp4 -vf "cropdetect=limit=24:round=2:reset=0" -f null - 2>&1 \
  | grep -oE "crop=[0-9:]+" | sort -u
```

**输出必须是 `crop=1920:1200:0:0`**（宽高等于分辨率、偏移为 0）。
如果不是，说明素材自带黑边，需要先裁：

```bash
# 例：检测出 crop=1724:880:98:0 → 裁掉左右各 98px
ffmpeg -i 原素材.mp4 -vf "crop=1724:880:98:0" -c:v libx264 -crf 18 -pix_fmt yuv420p 输出.mp4
```

### 4.3 检查规格

```bash
ffprobe -v error -select_streams v:0 \
  -show_entries stream=width,height,r_frame_rate,codec_name,pix_fmt,duration \
  -of default=noprint_wrappers=1 输出.mp4
```

期望：
```
codec_name=h264
width=1920
height=1200
pix_fmt=yuv420p
r_frame_rate=30/1
duration=8.000000
```

---

## 五、放素材

```
Assets/StreamingAssets/PC/master/
├── boot.mp4       ← 开机动画
├── ppt.mp4        ← 「打开PPT」
└── lesson.mp4     ← 「视频播放」
```

**文件名对上就行，不用改任何代码。**

如果要换别的文件名，在 `TwinDevicePresenter` 的 Inspector 里改
`_masterBootVideo` / `_roomVideo` / `_projectorVideo` 即可。

---

## 六、快速验收

Play 后检查：

| 项 | 期望 |
|---|---|
| 主脑屏幕 | 画面铺满、不变形、无黑边 |
| 幕布 | 画面铺满、左右方向正确、有暗角 |
| 关键内容 | 没有被裁掉（标题、logo 都在） |
| 声音 | 走近更响（3D 音源） |
| 换片 | 点「打开PPT」「视频播放」能正确切换 |

**如果画面被裁多了**：把主体往中间挪，或降低视频比例到 1.6 以下。
**如果有黑边**：回第四节重新裁。

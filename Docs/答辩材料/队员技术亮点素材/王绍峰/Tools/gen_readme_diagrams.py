"""生成 README 所需的技术展示图（PNG）。

依赖：matplotlib、SimHei 中文字体。
用法：python Tools/gen_readme_diagrams.py
输出：Docs/images/*.png

所有图都是矢量元素绘制的浅色底图，便于后续修改文案后重新生成。
"""

import os
import matplotlib

matplotlib.use("Agg")

import matplotlib.pyplot as plt
from matplotlib.patches import FancyBboxPatch, FancyArrowPatch
from matplotlib import font_manager

# ---------- 字体与全局样式 ----------
FONT_PATH = r"C:\Windows\Fonts\simhei.ttf"
font_manager.fontManager.addfont(FONT_PATH)
plt.rcParams["font.family"] = font_manager.FontProperties(fname=FONT_PATH).get_name()
plt.rcParams["axes.unicode_minus"] = False

OUT_DIR = r"D:\Uproject\UI\Docs\images"
os.makedirs(OUT_DIR, exist_ok=True)

TEXT = "#1F2933"
SUB = "#5A6875"
BLUE = "#2E6FD6"
TEAL = "#0E9B96"
ORANGE = "#DD7B22"
GREEN = "#2E9E5B"
PURPLE = "#7653C8"
RED = "#D04545"
GREY = "#64748B"

LAYER_FILL = {
    "ui": ("#EEF4FF", "#BDD4F7"),
    "ctrl": ("#F5F0FC", "#DCCCF7"),
    "scene": ("#EDF8F1", "#C6E9D3"),
    "asset": ("#FFF4E8", "#F6D9B8"),
    "tool": ("#F3F5F8", "#D8DEE7"),
}


def canvas(w=16, h=9):
    fig, ax = plt.subplots(figsize=(w, h), dpi=160)
    fig.patch.set_facecolor("white")
    ax.set_facecolor("white")
    ax.set_xlim(0, 100)
    ax.set_ylim(0, 100)
    ax.axis("off")
    return fig, ax


def heading(ax, title, subtitle=None):
    ax.text(50, 96.5, title, ha="center", va="center", fontsize=20,
            fontweight="bold", color=TEXT, zorder=10)
    if subtitle:
        ax.text(50, 91.8, subtitle, ha="center", va="center", fontsize=10.5,
                color=SUB, zorder=10)
    ax.plot([8, 92], [88.6, 88.6], color="#DCE3EC", linewidth=1.2, zorder=2)


def rbox(ax, x, y, w, h, text, edge=BLUE, fill="white", fs=10,
         bold=False, color=TEXT, lw=1.5, rounding=1.1, zorder=4, alpha=1.0):
    p = FancyBboxPatch(
        (x, y), w, h,
        boxstyle=f"round,pad=0.12,rounding_size={rounding}",
        linewidth=lw, edgecolor=edge, facecolor=fill, alpha=alpha, zorder=zorder,
    )
    ax.add_patch(p)
    ax.text(x + w / 2, y + h / 2, text, ha="center", va="center",
            fontsize=fs, color=color, fontweight="bold" if bold else "normal",
            linespacing=1.45, zorder=zorder + 1)
    return p


def arrow(ax, p1, p2, color=BLUE, rad=0.0, lw=1.7, ms=13, style="-|>", ls="-"):
    a = FancyArrowPatch(
        p1, p2, arrowstyle=style, mutation_scale=ms,
        connectionstyle=f"arc3,rad={rad}", color=color,
        linewidth=lw, linestyle=ls, shrinkA=0, shrinkB=0, zorder=8,
    )
    ax.add_patch(a)
    return a


def label(ax, x, y, text, fs=8.6, color=SUB):
    ax.text(x, y, text, ha="center", va="center", fontsize=fs, color=color,
            bbox=dict(boxstyle="round,pad=0.28", fc="white", ec="none", alpha=0.94),
            zorder=10)


def save(fig, name):
    path = os.path.join(OUT_DIR, name)
    fig.savefig(path, dpi=160, bbox_inches="tight", pad_inches=0.22,
                facecolor="white")
    plt.close(fig)
    print("saved:", path)


# ============================================================
# 图 1：系统架构图
# ============================================================
def architecture():
    fig, ax = canvas(16, 10)
    heading(ax, "教室控制台 · 系统架构图",
            "Unity 2022.3 URP  ·  交互层 / 控制层 / 场景对象层 / 资源层 / 工具层")

    layers = [
        # key, y, h, 标签, 框(文字, 边色)
        ("ui", 76, 15, "①\n交互层", BLUE, [
            ("左侧控制面板 Canvas\n4 按钮 + 状态栏 + 强调条", BLUE),
            ("键盘快捷键\n1 / 2 / 3 / Q / E / Esc", BLUE),
            ("鼠标输入\n右键转向 · 左键点 UI", BLUE),
        ]),
        ("ctrl", 57, 15, "②\n控制层", PURPLE, [
            ("ClassroomSceneController\n总控 · 状态机 · UI 回写", PURPLE),
            ("SecurityCameraFX\n滤镜动画驱动", PURPLE),
            ("FirstPersonWalker\n漫游控制", PURPLE),
            ("ProjectorScreen\n投影开关", PURPLE),
        ]),
        ("scene", 37, 16, "③\n场景对象层", GREEN, [
            ("3 台相机\n全景 / 监控 / 第一人称", GREEN),
            ("监控滤镜 Canvas\n5 层叠加", GREEN),
            ("投影幕布\nVideoPlayer + SpotLight", GREEN),
            ("空调气流\nParticleSystem", GREEN),
            ("音频与灯光\nAudioSource / Light", GREEN),
        ]),
        ("asset", 17, 16, "④\n资源层", ORANGE, [
            ("AC_Hum.wav\n12s 无缝", ORANGE),
            ("维什戴尔.mp4\n视频源", ORANGE),
            ("ProjectorRT\n1280×720", ORANGE),
            ("ClassroomFX\n4 张贴图", ORANGE),
            ("SimHei.ttf\nURP 材质", ORANGE),
        ]),
        ("tool", 2, 11, "⑤\n工具层", GREY, [
            ("ClassroomUIBuilder\n一键生成场景", GREY),
            ("gen_ac_hum.py\n音频合成", GREY),
            ("gen_fx_textures.py\n贴图生成", GREY),
            ("composite_security.py\n滤镜合成", GREY),
            ("check_*.py\n场景校验", GREY),
        ]),
    ]

    for key, y, h, lab, edge, items in layers:
        fill, band_edge = LAYER_FILL[key]
        rbox(ax, 14, y, 84, h, "", edge=band_edge, fill=fill, lw=1.2,
             rounding=1.4, zorder=1)
        rbox(ax, 1.5, y, 11, h, lab, edge=edge, fill=edge, fs=11, bold=True,
             color="white", lw=0, rounding=1.2, zorder=3)

        n = len(items)
        gap = 1.4
        w = (81 - gap * (n - 1)) / n
        box_h = h - 4.6
        for i, (txt, col) in enumerate(items):
            x = 15.5 + i * (w + gap)
            fs = 9.4 if n <= 3 else (8.8 if n == 4 else 8.1)
            rbox(ax, x, y + 2.0, w, box_h, txt, edge=col, fs=fs)

    # 层间流向箭头
    for y in (76, 57, 37, 17):
        arrow(ax, (50, y - 0.4), (50, y - 3.2), color="#9AA8BA", lw=1.4, ms=11)

    ax.text(50, 0.2, "运行时自上而下：输入 → 控制 → 场景对象 → 资源读取；工具层在编辑器/离线阶段生成并绑定上述全部对象",
            ha="center", va="bottom", fontsize=8.6, color=SUB)
    save(fig, "architecture.png")


# ============================================================
# 图 2：交互流程图
# ============================================================
def interaction_flow():
    fig, ax = canvas(16, 9.6)
    heading(ax, "教室控制台 · 交互流程图",
            "一次用户操作如何分发到四个子系统，并回写界面状态")

    rbox(ax, 32, 80, 36, 8, "用户操作\n点击左侧面板按钮 / 快捷键 / 鼠标右键",
         edge=BLUE, fill="#EEF4FF", fs=10.5, bold=True)
    rbox(ax, 24, 67, 52, 9,
         "ClassroomSceneController —— 事件分发\nToggleAC · ApplyView · ToggleProjector · HandleShortcuts",
         edge=PURPLE, fill="#F5F0FC", fs=10, bold=True)
    rbox(ax, 30, 55, 40, 8,
         "状态更新\nCurrentView · AcOn · ProjectorOn",
         edge=GREY, fill="#F3F5F8", fs=10, bold=True)

    arrow(ax, (50, 80), (50, 76.2), color=BLUE)
    arrow(ax, (50, 67), (50, 63.2), color=BLUE)

    cols = [
        (3.0, BLUE, "空调子系统",
         "acAudio.volume 淡入淡出\nacAir.Play / Stop\n关机只停发射，残留粒子飘散",
         "听觉 + 气流反馈"),
        (27.0, GREEN, "视角子系统",
         "Camera.enabled 三选一\n滤镜 Canvas.SetActive\n第一人称角色激活 / 光标",
         "画面切换"),
        (51.0, ORANGE, "投影子系统",
         "VideoPlayer.Play / Stop\n幕布材质切 RT / 白底\nSpotLight 与伴音同步",
         "幕布画面 + 灯光"),
        (75.0, PURPLE, "界面回写",
         "RefreshUI 统一刷新\n按钮文字 + 激活配色\n状态栏视角名与操作提示",
         "按钮状态同步"),
    ]

    for x, col, name, body, out in cols:
        w = 22.0
        arrow(ax, (50, 55), (x + w / 2, 46.5), color=col, rad=0.0, lw=1.5)
        rbox(ax, x, 33, w, 13.5, name + "\n\n" + body, edge=col,
             fill="white", fs=9.0, bold=False, lw=1.8)
        arrow(ax, (x + w / 2, 33), (x + w / 2, 27.5), color=col, lw=1.5)
        rbox(ax, x, 19, w, 8.5, out, edge=col,
             fill=LAYER_FILL["tool"][0] if col == PURPLE else "white",
             fs=9.6, bold=True, color=col)
        arrow(ax, (x + w / 2, 19), (50, 10.2), color=col, rad=0.0, lw=1.3, ms=11)

    rbox(ax, 26, 2, 48, 8, "视听反馈 + 界面状态达成一致（单一数据源）",
         edge=TEXT, fill="#F3F5F8", fs=11, bold=True)

    label(ax, 50, 51.0, "四条分支并行执行，互不依赖", fs=8.6)
    save(fig, "interaction-flow.png")


# ============================================================
# 图 3：视角状态机
# ============================================================
def view_state():
    fig, ax = canvas(15, 8.6)
    heading(ax, "教室控制台 · 视角状态机",
            "三态切换统一由 ApplyView(ClassroomView) 收口，避免相机与 UI 状态漂移")

    states = [
        (3.0, BLUE, "Overview 全景视角",
         "启用：Main Camera\n滤镜：关\n第一人称角色：关\n光标：常显\n提示：1/2/3 · Q · E"),
        (36.5, TEAL, "SecurityCamera 监控视角",
         "启用：监控相机\n滤镜：开（5 层）\n第一人称角色：关\n光标：常显\n提示：REC / 时间戳"),
        (70.0, PURPLE, "FirstPerson 第一人称",
         "启用：FP 相机\n滤镜：关\n第一人称角色：开\n光标：右键按下时锁定\n提示：WASD · 右键转视角"),
    ]

    for x, col, name, body in states:
        rbox(ax, x, 38, 27, 32, "", edge=col, fill="white", fs=11.5,
             bold=True, lw=2.0)
        ax.text(x + 13.5, 65.5, name, ha="center", va="center", fontsize=10.2,
                fontweight="bold", color=col, zorder=6)
        rbox(ax, x + 1.5, 40.5, 24, 20, body, edge=col,
             fill="#F8FAFC", fs=8.8, lw=0.9, rounding=0.8)

    # 进入 / 返回箭头（上方为进入，下方为返回）
    arrow(ax, (30, 63), (36.5, 63), color=TEAL, rad=-0.28, lw=1.8)
    label(ax, 33.2, 71.5, "按钮 / 2", fs=8.8, color=TEAL)
    arrow(ax, (36.5, 51), (30, 51), color=TEAL, rad=-0.28, lw=1.6)
    label(ax, 33.2, 45.5, "再点 / 1 / Esc", fs=8.4, color=TEAL)

    arrow(ax, (63.5, 63), (70, 63), color=PURPLE, rad=0.28, lw=1.8)
    label(ax, 66.8, 71.5, "按钮 / 3", fs=8.8, color=PURPLE)
    arrow(ax, (70, 51), (63.5, 51), color=PURPLE, rad=0.28, lw=1.6)
    label(ax, 66.8, 45.5, "再点 / 1 / Esc", fs=8.4, color=PURPLE)

    arrow(ax, (30, 74), (70, 74), color=GREY, rad=-0.22, lw=1.4, ls="--")
    label(ax, 50, 82.0, "全景 → 第一人称：按钮 / 3（可跨态直达）", fs=8.8, color=GREY)
    arrow(ax, (70, 34), (30, 34), color=GREY, rad=0.16, lw=1.4, ls="--")
    label(ax, 50, 26.5, "任意非全景态 → 全景：1 或 Esc", fs=8.8, color=GREY)

    rbox(ax, 12, 6, 76, 12,
         "统一收口：ApplyView() 一次性设置相机 enabled / 滤镜 SetActive / 角色 activeSelf / 光标 / UI 文字\n"
         "好处：新增视角只需改一个方法，不会出现「相机切了但滤镜没关」这类状态漂移",
         edge=TEXT, fill="#F3F5F8", fs=10, bold=False, lw=1.2)
    save(fig, "view-state.png")


# ============================================================
# 图 4：数据链路图
# ============================================================
def data_flow():
    fig, ax = canvas(16, 9.4)
    heading(ax, "教室控制台 · 核心数据链路图",
            "视频 / 音频 / 监控滤镜三条链路：从程序化资源生成到最终画面与声音")

    lanes = [
        (69, BLUE, "#EEF4FF", "① 视频链路", [
            "维什戴尔.mp4\nVideoClip 资源",
            "VideoPlayer\nPlay / Stop / Loop",
            "ProjectorRT\n1280×720 RT",
            "幕布材质\n_BaseMap 切换",
            "屏幕画面\n关:白幕 开:视频",
        ], "伴音经 AudioOutputMode.AudioSource 输出到幕布上的 AudioSource"),
        (40, GREEN, "#EDF8F1", "② 音频链路", [
            "gen_ac_hum.py\nnumpy 频域 IFFT",
            "AC_Hum.wav\n12s 无缝循环",
            "AudioSource\n常驻播放 loop",
            "volume\nMoveTowards 淡入淡出",
            "唯一 AudioListener\n挂在控制台根节点",
        ], "空调开关同时联动 ParticleSystem：Play / StopEmitting"),
        (11, ORANGE, "#FFF4E8", "③ 滤镜链路", [
            "gen_fx_textures.py\n4 张程序贴图",
            "RawImage 分层\n色调/暗角/扫描线/噪点",
            "SecurityCameraFX\nUV / REC / 时间戳",
            "Overlay Canvas\n仅监控视角激活",
            "composite_security.py\n合成监控效果图",
        ], "Overlay 画布不被相机捕获，故单独渲染带 alpha 的滤镜层再合成"),
    ]

    for y, col, fill, name, nodes, note in lanes:
        rbox(ax, 1.5, y - 1.0, 97, 24, "", edge=fill, fill=fill, lw=1.0,
             rounding=1.3, zorder=1)
        rbox(ax, 2.5, y + 17.0, 14, 4.6, name, edge=col, fill=col,
             fs=10.5, bold=True, color="white", lw=0, zorder=5)

        n = len(nodes)
        w = 16.0
        gap = (92 - n * w) / (n - 1)
        for i, txt in enumerate(nodes):
            x = 3.5 + i * (w + gap)
            rbox(ax, x, y + 3.0, w, 10.5, txt, edge=col, fs=9.0, lw=1.5)
            if i < n - 1:
                arrow(ax, (x + w + 0.3, y + 8.2),
                      (x + w + gap - 0.3, y + 8.2), color=col, lw=1.6, ms=11)

        ax.text(50, y - 0.0, note, ha="center", va="center", fontsize=8.6,
                color=SUB, zorder=6)

    save(fig, "data-flow.png")


# ============================================================
# 图 5：构建与交付流水线
# ============================================================
def build_pipeline():
    fig, ax = canvas(16, 8.8)
    heading(ax, "教室控制台 · 构建与交付流水线",
            "离线资源生成 → Unity 批处理构建 → 场景保存 → 预览出图 → 校验与文档")

    steps = [
        (2.0, GREY, "① 离线资源", "gen_ac_hum.py\ngen_fx_textures.py\n生成音频与 4 张贴图"),
        (21.5, BLUE, "② 批处理启动", "Unity -batchmode\n-executeMethod\nBuildAndSave"),
        (41.0, PURPLE, "③ 幂等构建", "计算教室包围盒\n删除旧根节点\n生成对象并绑定引用"),
        (60.5, GREEN, "④ 场景保存", "EditorSceneManager\n.SaveScene\nClassroom.unity"),
        (80.0, ORANGE, "⑤ 预览出图", "6 张视角/UI 预览\nWorld Space 正交相机\n拍 Overlay 画布"),
    ]
    w = 18.0
    for x, col, name, body in steps:
        rbox(ax, x, 46, w, 25, "", edge=col, fill="white", fs=11, bold=True, lw=2.0)
        ax.text(x + w / 2, 66.5, name, ha="center", va="center", fontsize=10.2,
                fontweight="bold", color=col, zorder=6)
        rbox(ax, x + 1.2, 47.5, w - 2.4, 15, body, edge=col, fill="#F8FAFC",
             fs=8.6, lw=0.9, rounding=0.8)

    for i in range(len(steps) - 1):
        x1 = steps[i][0] + w
        x2 = steps[i + 1][0]
        arrow(ax, (x1 + 0.2, 58.5), (x2 - 0.2, 58.5), color="#8A99AC", lw=1.7, ms=12)

    rbox(ax, 2.0, 24, 46, 15,
         "⑥ 校验：check_scene.py / check_wiring.py / check_active.py\n"
         "解码 YAML 中文转义 · 检查 18+ 引用字段 · 确认滤镜默认关闭",
         edge=TEAL, fill="#EDF8F1", fs=9.4)
    rbox(ax, 52.0, 24, 46, 15,
         "⑦ 合成与文档：composite_security.py 生成监控效果图\n"
         "gen_readme_diagrams.py 生成技术展示图 → README.md 引用",
         edge=RED, fill="#FFF1F0", fs=9.4)

    arrow(ax, (25, 46), (25, 39.4), color=TEAL, lw=1.6)
    arrow(ax, (75, 46), (75, 39.4), color=RED, lw=1.6)

    rbox(ax, 6, 5, 88, 13,
         "关键设计：构建脚本幂等（可反复执行不产生重复对象）· 所有摆位由教室包围盒推导，换模型无需改坐标\n"
         "· 预览图与文档同源，重新构建后配图自动同名覆盖，链接不会失效",
         edge=TEXT, fill="#F3F5F8", fs=10, lw=1.2)
    save(fig, "build-pipeline.png")


if __name__ == "__main__":
    architecture()
    interaction_flow()
    view_state()
    data_flow()
    build_pipeline()
    print("all diagrams generated:", OUT_DIR)

#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
项目状态看板生成器（P-STANDUP）

用一条命令生成 Docs/STATUS.md —— 谁在干活、改到哪一步、代码是否已生效、谁在等谁。

设计原则（很重要，别改）：
  1. **只读不写**。本脚本不改任何源码、不动 git 状态，只读文件系统 + 跑几个只读 git 命令。
  2. **零配置**。不维护"当前阶段是什么"这类人工字段 —— 那种字段一定滞后，
     一滞后就变成"看板说 A，实际在做 B"，比没有看板更坏。
     凡是能算出来的，就不要人填。
  3. **生效判定用二进制证据**，不用"他说改完了"：
     最后一个 .cs 的 mtime < Library/ScriptAssemblies/Assembly-CSharp.dll 的 mtime（基线 §6.8）。
  4. **不确定就沉默**。扫不到的东西不猜，宁可空着也不编。

用法：
    python Tools/status.py            # 生成并打印
    python Tools/status.py --quiet    # 只写文件不打印
"""

import os
import re
import subprocess
import sys
from datetime import datetime, timedelta

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "Docs", "STATUS.md")

# Unity 编译产物：生效判定的唯一依据（基线 §6.8）
DLLS = [
    os.path.join(ROOT, "Library", "ScriptAssemblies", "Assembly-CSharp.dll"),
    os.path.join(ROOT, "Library", "ScriptAssemblies", "Assembly-CSharp-Editor.dll"),
]

# 代码根目录（只扫这里，Library/Temp 不算"谁在干活"）
CODE_ROOT = os.path.join(ROOT, "Assets", "_Project", "Scripts")

# 文档根目录
DOC_ROOT = os.path.join(ROOT, "Docs")

# 专家 → 允许写入的资源（基线 §6.8 资源分级表）
# 用路径前缀反推"这块是谁的"，这样不用任何人填表
OWNERS = [
    ("断点", ["Assets/_Project/Scripts/"], "唯一代码写入者"),
    ("见微", ["Docs/P5_", "Docs/P29_", "Docs/UI_Boards/", "Docs/P5-1"], "UI 规范与坐标"),
    ("心跳", ["Docs/P29-3.6", "Docs/P3_", "Docs/P29_3.6"], "数据口径"),
    ("挑刺", ["Docs/QA/"], "测试用例"),
    ("落笔", ["Docs/设计说明书", "Docs/使用手册"], "两份文档"),
]

# 活跃判定窗口：多久内改过算"正在干活"
ACTIVE_WINDOW_MIN = 45


def sh(args):
    """跑一个只读 git 命令，失败返回空串。"""
    try:
        return subprocess.check_output(
            args, cwd=ROOT, stderr=subprocess.DEVNULL, text=True,
            encoding="utf-8", errors="replace").strip()
    except Exception:
        return ""


def unquote_git_path(raw):
    """
    把 git porcelain 路径还原成真实路径。

    两个坑都要处理：
      1. 未跟踪/新增文件带引号且中文被转成八进制（`\\346\\225\\260`）；
         git 默认 core.quotepath=true 会这样输出，纯 ASCII 路径则不加引号。
      2. `-z` 更干净，但我们不用 —— 保持命令可读，转义在这里解。

    解法：带双引号的走 codecs 解码（git 用的是 C 风格八进制转义），
    不带引号的直接用。
    """
    raw = raw.strip()
    if len(raw) >= 2 and raw[0] == '"' and raw[-1] == '"':
        inner = raw[1:-1]
        # git 的八进制转义形如 \346\225\260，每个字节一位八进制
        out = bytearray()
        i = 0
        while i < len(inner):
            c = inner[i]
            if c == "\\" and i + 1 < len(inner):
                nxt = inner[i + 1]
                if nxt in "01234567":
                    oct_digits = inner[i + 1:i + 4]
                    try:
                        out.append(int(oct_digits, 8))
                        i += 4
                        continue
                    except ValueError:
                        pass
                # 常见转义
                esc = {"n": 10, "t": 9, '"': 34, "\\": 92}
                if nxt in esc:
                    out.append(esc[nxt])
                    i += 2
                    continue
            out.extend(c.encode("utf-8"))
            i += 1
        return out.decode("utf-8", errors="replace")
    return raw.strip('"')


def real_rel(path):
    """
    转成相对路径并**还原真实大小写**。

    Windows 文件系统大小写不敏感，但 git 记的是原始大小写；
    有时 porcelain 会给出 `tools/status.py` 而磁盘上是 `Tools/`。
    只要对应真实文件存在，就按真实路径回填，避免看板上出现两个"同一个文件"。
    """
    rel = os.path.relpath(path, ROOT).replace(os.sep, "/")
    if os.path.exists(path):
        # 逐段还原真实大小写
        cur = ROOT
        fixed = []
        for part in rel.split("/"):
            try:
                entries = os.listdir(cur)
            except OSError:
                return rel
            hit = None
            for e in entries:
                if e.lower() == part.lower():
                    hit = e
                    break
            if hit is None:
                return rel
            fixed.append(hit)
            cur = os.path.join(cur, hit)
        return "/".join(fixed)
    return rel


def mtime(p):
    try:
        return os.path.getmtime(p)
    except OSError:
        return None


def fmt_ago(ts, now):
    """ts / now 都是 unix 秒。"""
    if ts is None:
        return "—"
    s = now - ts
    if s < 0:
        return "刚刚"
    if s < 60:
        return "%d 秒前" % int(s)
    if s < 3600:
        return "%d 分钟前" % int(s / 60)
    if s < 86400:
        return "%d 小时前" % int(s / 3600)
    return "%d 天前" % int(s / 86400)


def fmt_ts(ts):
    if ts is None:
        return "—"
    return datetime.fromtimestamp(ts).strftime("%m-%d %H:%M:%S")


def walk_files(root, exts):
    """遍历某目录下指定后缀的文件，返回 [(path, mtime)]，跳过 .meta / 隐藏目录。"""
    out = []
    if not os.path.isdir(root):
        return out
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames[:] = [d for d in dirnames if not d.startswith(".") and d != "obj"]
        for fn in filenames:
            if fn.endswith(".meta"):
                continue
            if any(fn.endswith(e) for e in exts):
                p = os.path.join(dirpath, fn)
                t = mtime(p)
                if t is not None:
                    out.append((p, t))
    return out


def collect_code():
    """代码文件 + 最后修改时间，按新到旧。"""
    files = walk_files(CODE_ROOT, [".cs"])
    files.sort(key=lambda x: -x[1])
    return files


def build_status():
    now = datetime.now().timestamp()
    lines = []
    w = lines.append

    code = collect_code()
    newest_cs = code[0] if code else (None, None)

    dll = None
    dll_path = None
    for d in DLLS:
        t = mtime(d)
        if t is not None and (dll is None or t > dll):
            dll, dll_path = t, d

    status = sh(["git", "status", "--porcelain"])
    dirty = [l for l in status.splitlines() if l.strip()] if status else []
    dirty_code = [l for l in dirty if ".cs" in l]
    dirty_doc = [l for l in dirty if l.endswith(".md") or ".png" in l or ".asset" in l]

    last_commit = sh(["git", "log", "-1", "--format=%h|%ad|%s", "--date=format:%m-%d %H:%M"])

    w("# 项目状态看板")
    w("")
    w("> **自动生成，不要手改。** 刷新命令：`python Tools/status.py`")
    w("> 生成时间：**%s**" % datetime.fromtimestamp(now).strftime("%Y-%m-%d %H:%M:%S"))
    w(">")
    w("> 本看板只认**可验证的证据**（文件时间戳 / git 状态 / 编译产物），不认「他说改完了」。")
    w("> 凡是算不出来的，宁可不写 —— 看板滞后比没有看板更坏。")
    w("")

    # ---------------- 一句话结论 ----------------
    w("## 0. 一句话")
    w("")
    alive = newest_cs[1] is not None and (now - newest_cs[1]) < ACTIVE_WINDOW_MIN * 60
    if alive:
        owner = owner_of(newest_cs[0])
        w("**%s正在施工**：最后改动 `%s`（%s）。未提交代码文件 **%d** 个。"
          % (owner, os.path.basename(newest_cs[0]), fmt_ago(newest_cs[1], now), len(dirty_code)))
    elif dirty_code:
        w("**有未提交代码**（%d 个文件），但最近 %d 分钟内没人动过 —— 可能刚停下、也可能忘了提交。"
          % (len(dirty_code), ACTIVE_WINDOW_MIN))
    else:
        w("**代码区干净**，无未提交改动。最后改动 `%s`（%s）。"
          % (os.path.basename(newest_cs[0]) if newest_cs[0] else "—",
             fmt_ago(newest_cs[1], now)))
    w("")

    # ---------------- 验收判据自检 ----------------
    lines.extend(build_checks_section(now))

    # ---------------- 生效判定 ----------------
    w("## 1. 代码是否已生效（基线 §6.8 唯一判据）")
    w("")
    w("判据：**最后一个 .cs 的 mtime < Assembly-CSharp.dll 的 mtime**。不满足 = 改了但没编译 = 没生效。")
    w("")
    w("| 项 | 时间 |")
    w("|---|---|")
    w("| 最后一个 .cs 改动 | %s（`%s`） |"
      % (fmt_ts(newest_cs[1]), os.path.basename(newest_cs[0]) if newest_cs[0] else "—"))
    w("| dll 编译时间 | %s |" % fmt_ts(dll))
    w("")

    if dll is None:
        w("> ⚠️ **找不到 `Assembly-CSharp.dll`** —— Unity 从未在本机编译过，或 Library 被清过。")
        w("> 此时「已生效」无从判定，一切「改完了」都只是声称。")
    elif newest_cs[1] is not None:
        if newest_cs[1] < dll:
            gap = dll - newest_cs[1]
            w("> ✅ **已生效**，编译晚于最后改动 %.0f 秒。可以 play 验收。" % gap)
        else:
            gap = newest_cs[1] - dll
            w("> ❌ **未生效**：最后一个 .cs 比 dll 新了 **%.0f 秒**。" % gap)
            w("> 说明改完还没触发编译（或无 Auto Refresh）。**现在 play 看到的不是最新代码。**")
    w("")

    # ---------------- 未提交 ----------------
    w("## 2. 未提交改动（= 有丢失风险的工作）")
    w("")
    w("基线 §6.8：多人在同一工作区写同一批文件，冲突不是 merge conflict，是**后写覆盖先写**，且被覆盖方收不到提示。")
    w("")
    if not dirty:
        w("无。工作区干净。")
    else:
        w("共 **%d** 个文件未提交（代码 %d / 文档与其他 %d）。"
          % (len(dirty), len(dirty_code), len(dirty_doc)))
        w("")
        w("| 类型 | 文件 | 最后修改 |")
        w("|---|---|---|")
        for l in dirty:
            # porcelain 前两位是状态码（如 " M" / "??" / "A "），第 3 位是空格。
            # 注意：` M path` 是 "空格+M+空格"，所以不能写 l[3:] 之后又 strip 掉首字符；
            # 正确做法是取 l[2:] 再 strip（状态码固定 2 字符）。
            path = unquote_git_path(l[2:])
            if not path:
                continue
            full = os.path.join(ROOT, path.replace("/", os.sep))
            t = mtime(full)
            # 还原真实大小写，避免同一文件出现两次（Windows 盘上 Tools/ 与 tools/ 是同一个）
            path = real_rel(full) if os.path.exists(full) else path
            if path.endswith(".cs"):
                kind = "代码"
            elif path.endswith(".md"):
                kind = "文档"
            else:
                kind = "其他"
            w("| %s | `%s` | %s |" % (kind, path, fmt_ago(t, now)))
    w("")

    # ---------------- 谁在干活 ----------------
    w("## 3. 谁在动（按文件修改时间反推，不需要任何人填报）")
    w("")
    w("| 专家 | 最近触碰的文件 | 时间 | 活跃度 |")
    w("|---|---|---|---|")
    for name, prefixes, _role in OWNERS:
        hits = []
        for p, t in code:
            rel = os.path.relpath(p, ROOT).replace(os.sep, "/")
            if any(rel.startswith(pre) for pre in prefixes):
                hits.append((p, t))
        for p, t in walk_files(DOC_ROOT, [".md", ".png"]):
            rel = os.path.relpath(p, ROOT).replace(os.sep, "/")
            if any(rel.startswith(pre) for pre in prefixes):
                hits.append((p, t))
        if not hits:
            w("| %s | — | — | 无记录 |" % name)
            continue
        hits.sort(key=lambda x: -x[1])
        newest = hits[0]
        active = (now - newest[1]) < ACTIVE_WINDOW_MIN * 60
        w("| %s | `%s` | %s | %s |"
          % (name, os.path.basename(newest[0]), fmt_ago(newest[1], now),
             "🔥 正在干" if active else "歇着"))
    w("")

    # ---------------- 今日提交 ----------------
    w("## 4. 今日提交")
    w("")
    today = datetime.now().strftime("%Y-%m-%d")
    log = sh(["git", "log", "--since=" + today + " 00:00", "--format=%h|%ad|%s", "--date=format:%H:%M"])
    if not log:
        w("今日无提交。")
    else:
        w("| 时间 | 提交 | 说明 |")
        w("|---|---|---|")
        for line in log.splitlines():
            parts = line.split("|", 2)
            if len(parts) == 3:
                w("| %s | `%s` | %s |" % (parts[1], parts[0], parts[2]))
    w("")

    # ---------------- 文档新鲜度 ----------------
    w("## 5. 文档新鲜度（谁过期了）")
    w("")
    w("超过 **24 小时**没更新的文档标 ⚠️，超过 **72 小时**标 ❌。项目里最贵的错是「照着过期文档施工」。")
    w("")
    docs = walk_files(DOC_ROOT, [".md"])
    docs.sort(key=lambda x: -x[1])
    w("| 文档 | 行数 | 最后修改 | 状态 |")
    w("|---|---|---|---|")
    for p, t in docs[:25]:
        rel = os.path.relpath(p, ROOT).replace(os.sep, "/")
        if "/QA/" in rel:
            continue
        try:
            with open(p, "r", encoding="utf-8", errors="replace") as f:
                n = sum(1 for _ in f)
        except OSError:
            n = 0
        age_h = (now - t) / 3600.0
        flag = "✅ 新鲜" if age_h < 24 else ("⚠️ 超 24h" if age_h < 72 else "❌ 超 72h")
        w("| `%s` | %d | %s | %s |" % (os.path.basename(rel), n, fmt_ago(t, now), flag))
    w("")

    # ---------------- 最近提交 ----------------
    w("## 6. 最近 10 次提交")
    w("")
    recent = sh(["git", "log", "-10", "--format=%h|%ad|%s", "--date=format:%m-%d %H:%M"])
    if recent:
        w("| 时间 | 提交 | 说明 |")
        w("|---|---|---|")
        for line in recent.splitlines():
            parts = line.split("|", 2)
            if len(parts) == 3:
                w("| %s | `%s` | %s |" % (parts[1], parts[0], parts[2]))
    w("")
    w("---")
    w("")
    w("> 本文件由 `Tools/status.py` 生成。要改内容改脚本，不要手改本文件 —— ")
    w("> 手改会在下次生成时被覆盖，而那正是「看板说 A、实际是 B」的开始。")

    return "\n".join(lines) + "\n"


def owner_of(path):
    rel = os.path.relpath(path, ROOT).replace(os.sep, "/")
    for name, prefixes, _r in OWNERS:
        if any(rel.startswith(p) for p in prefixes):
            return name
    return "未知"


# ================================================================ 验收判据自检
#
# 这是本看板唯一"能回答'活做完没有'"的部分。
# 判据来自 Tools/status_checks.txt，格式见该文件头注释。
#
# 为什么值得单独做：
#   项目里最贵的沟通成本不是"干活慢"，是"不知道干完了没"——
#   于是负责人反复问、专家反复答、答案还不一致。
#   `grep -c` 不会美化，0 就是 0。

CHECKS_FILE = os.path.join(ROOT, "Tools", "status_checks.txt")


def run_checks():
    """执行 status_checks.txt 里的全部判据，返回 [(id, 描述, 结果, 详情)]。"""
    results = []
    if not os.path.isfile(CHECKS_FILE):
        return results

    with open(CHECKS_FILE, "r", encoding="utf-8", errors="replace") as f:
        raw_lines = f.readlines()

    for line in raw_lines:
        line = line.rstrip("\n").rstrip("\r")
        if not line.strip() or line.lstrip().startswith("#"):
            continue
        # 文件头用 "> ..." 写说明，不是判据 —— 跳过，否则会被当成一条畸形条目
        if line.lstrip().startswith(">"):
            continue
        parts = line.split("|", 2)
        if len(parts) != 3:
            continue
        cid, desc, spec = [p.strip() for p in parts]
        # ID 必须是 P<数字>-<数字> 这种形态，防止把说明文字当判据
        if not re.match(r"^[A-Za-z]+-?\d+", cid):
            continue

        try:
            ok, detail = eval_check(spec)
        except Exception as e:
            ok, detail = None, "判据解析失败：%s" % e
        results.append((cid, desc, ok, detail))

    return results


def strip_comments(text, lang):
    """去掉注释行，只保留**真正会执行**的代码。

    ── 为什么必须有这个 ────────────────────────────────────────────
    这个坑在一天之内踩了三次：

      P5-06  grep0 "v0.9 · "   → 命中注释「原来写 `"v0.9 · " + HH:mm:ss`」，
                                  代码早改对了，判据一直红
      P5-10  grep0 "实时 · 实测" → 命中注释「卡 D 原写「实时 · 实测」」，
                                  代码已改成「含估算」，判据报"仍有 1 处"
      P5-11  grep0 "实时 · 估算" → 同上，命中「原来是「实时 · 估算」」

    根因不是"某条正则写错了"，而是**判据工具本身不分注释和代码**。
    逐个打补丁只会换个地方再犯 —— 所以在这里一次解决。

    ── 各语言怎么判注释 ──────────────────────────────────────────────
      cs / js / py  : 行注释 `//`（py 是 `#`），块注释 `/* */`
      txt / md      : 不处理（判据清单本身要能写例子）
    """
    if lang in ("txt", "md", ""):
        return text

    line_tokens = ["//"]
    if lang == "py":
        line_tokens = ["#"]

    out = []
    in_block = False
    for line in text.split("\n"):
        if lang in ("cs", "js"):
            s = line.strip()
            if in_block:
                if "*/" in s:
                    in_block = False
                continue
            if s.startswith("/*"):
                if "*/" not in s:
                    in_block = True
                continue
        stripped = line.lstrip()
        if any(stripped.startswith(t) for t in line_tokens):
            continue        # 整行是注释 → 丢掉
        out.append(line)
    return "\n".join(out)


def eval_check(spec):
    """解析并执行一条判据，返回 (True/False/None, 说明)。"""
    kind, _, rest = spec.partition(":")
    kind = kind.strip().lower()

    if kind in ("has", "hasnot"):
        rel = rest.strip()
        full = os.path.join(ROOT, rel.replace("/", os.sep))
        exists = os.path.exists(full)
        ok = exists if kind == "has" else (not exists)
        return ok, ("存在" if exists else "不存在")

    if kind in ("grep", "grep0", "grep+"):
        if kind == "grep0":
            want = 0
            body = rest
        elif kind == "grep":
            n_str, _, body = rest.partition(":")
            want = int(n_str.strip())
        else:  # grep+ ：至少 N 处
            n_str, _, body = rest.partition(":")
            want = int(n_str.strip())
        rel, _, pattern = body.partition(":")
        rel = rel.strip()
        pattern = pattern.strip()
        full = os.path.join(ROOT, rel.replace("/", os.sep))
        if not os.path.exists(full):
            return False, "文件不存在：%s" % rel
        try:
            with open(full, "r", encoding="utf-8", errors="replace") as f:
                text = f.read()
        except OSError as e:
            return None, "读不了：%s" % e

        # 默认剔除注释行 —— 见 strip_comments 的说明（同一天踩三次的坑）
        lang = os.path.splitext(rel)[1].lstrip(".").lower()
        text = strip_comments(text, lang)

        try:
            hits = len(re.findall(pattern, text, re.MULTILINE))
        except re.error as e:
            return None, "正则写错了：%s" % e

        if kind == "grep0":
            return (hits == 0), ("0 处 ✔" if hits == 0 else "仍有 %d 处" % hits)
        if kind == "grep+":
            return (hits >= want), ("%d 处（需 ≥%d）" % (hits, want))
        return (hits == want), ("%d 处（需 =%d）" % (hits, want))

    return None, "未知判据类型：%s" % kind


def build_checks_section(now):
    """生成「验收判据自检」小节。"""
    out = []
    w = out.append
    results = run_checks()

    w("## 0.5 验收判据自检（这活到底做完没有）")
    w("")
    if not results:
        w("尚未配置判据。在 `Tools/status_checks.txt` 里按格式添加，看板会自动跑。")
        w("")
        return out

    total = len(results)
    done = sum(1 for r in results if r[2] is True)
    fail = sum(1 for r in results if r[2] is False)
    unknown = sum(1 for r in results if r[2] is None)

    w("共 **%d** 条判据：**%d 通过** / %d 未过 / %d 无法判定。"
      % (total, done, fail, unknown))
    w("")
    w("> 判据来自 `Tools/status_checks.txt`，全部是 `grep` / 文件存在性这类**机器可验证**的东西。")
    w("> 没有「完成 80%」这种只能人填的数 —— 那种数一定滞后、一定美化。")
    w("")
    w("| 判据 | 内容 | 结果 | 证据 |")
    w("|---|---|---|---|")
    for cid, desc, ok, detail in results:
        mark = "✅ 通过" if ok is True else ("❌ 未过" if ok is False else "⚠️ 未知")
        w("| `%s` | %s | %s | %s |" % (cid, desc, mark, detail))
    w("")

    if fail:
        w("> ❌ 有 **%d** 条未过 —— 说明对应工作**确实还没做完**，不是「快好了」。"
          % fail)
        w("")
    elif done == total and total > 0:
        w("> ✅ **全部判据通过。** 可以进入验收（但不能替代 play 视觉验收）。")
        w("")

    return out


def main():
    text = build_status()
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    if "--quiet" not in sys.argv:
        print(text)
    print("[status] 已写入 %s" % OUT, file=sys.stderr)


if __name__ == "__main__":
    main()
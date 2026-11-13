#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
局部变量声明完整性校验器（找"使用了但没声明"的标识符）。

【为什么必须单独写这一个 —— 2026-11-13 真实事故】
`check_syntax.py`（括号配对）与 `check_macro_symbols.py`（宏边界）**都 PASS**，
但 Unity 报：
    TwinLampBloom.cs(639,22): error CS0103: The name 'bloomWas' does not exist in the current context
原因：重构时把 `bool bloomWas = Enabled;` 连同注释一起替换掉了，
下面的 `SetBloomLive(bloomWas);` 还在。
括号配平、宏配平、符号也确实在文件里出现过 —— 前两个校验器全看不见。
这类"删代码时漏了一处引用"是重构期最高频错误，必须用工具兜住。

【设计取舍：宁可漏报，不要误报】
误报会让人直接忽略这个工具，等于没有。所以识别声明时采用**宽口径**：
  · 多变量声明 `int a = 1, b = 2;`  → 逐个识别
  · 数组类型 `float[] data = ...`   → 识别
  · 泛型 foreach `foreach (KeyValuePair<K,V> kv in ...)` → 识别
  · 任何 `ident =` 的左侧（含赋值）都算"已知标识符" → 大幅降低误报
代价：`x = 5;` 这种"用未声明变量做赋值"会漏报。可接受 ——
真正高频且致命的是**表达式/实参里引用已被删掉的变量**，这类仍能抓到。

【用法】
  python check_locals.py <file.cs> [--strict]
--strict 关闭"赋值左侧也算已知"的宽口径（可能增加误报）
退出码 1 = 发现问题。
"""
import re
import sys

# C# 关键字 + 常见 Unity/BCL 成员名。
WHITELIST = set("""
abstract as base bool break byte case catch char checked class const continue decimal
default delegate do double else enum event explicit extern false finally fixed float for
foreach goto if implicit in int interface internal is lock long namespace new null object
operator out override params private protected public readonly ref return sbyte sealed
short sizeof stackalloc static string struct switch this throw true try typeof uint ulong
unchecked unsafe ushort using virtual void volatile while yield var dynamic nameof await
async partial where value get set add remove
math mathf debug time screen camera render texture2d rect color material shader light
gameobject transform vector3 vector2 quaternion bounds renderer texture format hideflags
coroutine waitforendofframe object destroy findobjectsoftype startcoroutine logwarning
log tostring concat format
gameObject gameobject transform position rotation name tag enabled hideflags
instance deltaTime unscaledTime frameCount fixedDeltaTime timeScale
""".split())

# 白名单比较用大小写无关 —— C# 里 `gameObject`（MonoBehaviour 继承成员）与
# `GameObject`（类型）只差一个字母大小写，逐个列全很容易漏。
WHITELIST_LOWER = {w.lower() for w in WHITELIST}

SIG_RE = re.compile(
    r'^\s*(?:\[[^\]]*\]\s*)*'
    r'(?:public|private|internal|protected)\s+'
    r'(?:static\s+)?'
    r'(?:readonly\s+)?'
    r'[\w\.\<\>\[\],\? ]+?\s+'
    r'(\w+)\s*\(([^)]*)\)\s*$'
)

TYPE_TOK = (r'(?:[A-Z][\w\.]*'                       # 自定义/BCL 类型
            r'|string|int|uint|float|double|long|ulong|short|ushort|byte|sbyte'
            r'|bool|char|decimal|object|var)')

# 声明形态
DECL_RES = [
    # 类型 名字 （可带泛型、数组、可空），后面是 = ; , ) 或 空格+
    re.compile(r'\b' + TYPE_TOK + r'(?:\s*<[^;=]*?>)?\s*(?:\[\s*\])*\s*([a-z_]\w*)\s*(?:=|;|,|\))'),
    # foreach (... ident in ...)
    re.compile(r'\bforeach\s*\([^)]*?\b([a-z_]\w*)\s+in\b'),
    # for (Type ident = ...)
    re.compile(r'\bfor\s*\([^;]*?\b([a-z_]\w*)\s*='),
    # 局部函数 Type name(...) {
    re.compile(r'\b' + TYPE_TOK + r'\s+([a-z_]\w*)\s*\([^)]*\)\s*\{'),
    # out ident / out Type ident
    re.compile(r'\bout\s+(?:' + TYPE_TOK + r'\s+)?([a-z_]\w*)'),
    # is Type ident
    re.compile(r'\bis\s+' + TYPE_TOK + r'\s+([a-z_]\w*)\b'),
    # lambda (a, b) =>  / a =>
    re.compile(r'\(\s*([a-z_]\w*(?:\s*,\s*[a-z_]\w*)*)\s*\)\s*=>'),
    re.compile(r'\b([a-z_]\w*)\s*=>'),
    # 多变量声明续体：`, b = ` / `, c;`
    re.compile(r',\s*([a-z_]\w*)\s*(?:=|;|\))'),
]

# 赋值左侧只在"行首或分号/大括号之后"成立，避免把 `Foo(bar = 1)` 的实参
# 或方法调用的实参误当成已声明标识符（早期版本正是因此**漏报了真错误**）。
ASSIGN_LHS = re.compile(r'(?:^|[;{}])\s*([a-z_]\w*)\s*(?:[-+*/%]?=)(?!=)')


def clean_line(line):
    """去掉注释、字符串、字符字面量、预处理指令。"""
    s = line
    if s.lstrip().startswith('#'):
        return ''
    out = []
    i, n, mode = 0, len(s), None
    while i < n:
        c = s[i]
        if mode == '"':
            if c == '\\':
                i += 2
                continue
            if c == '"':
                mode = None
            i += 1
            continue
        if mode == "'":
            if c == '\\':
                i += 2
                continue
            if c == "'":
                mode = None
            i += 1
            continue
        if c == '"':
            mode = '"'
            i += 1
            continue
        if c == "'":
            mode = "'"
            i += 1
            continue
        if c == '/' and i + 1 < n and s[i + 1] == '/':
            break
        out.append(c)
        i += 1
    return ''.join(out)


def find_methods(lines):
    methods, i = [], 0
    while i < len(lines):
        m = SIG_RE.match(lines[i])
        if m and '{' not in lines[i]:
            j = i + 1
            while j < len(lines) and lines[j].strip() == '':
                j += 1
            if j < len(lines) and lines[j].strip().startswith('{'):
                depth, k = 0, j
                while k < len(lines):
                    depth += lines[k].count('{') - lines[k].count('}')
                    if depth == 0 and k > j:
                        break
                    k += 1
                methods.append((m.group(1), m.group(2), i, k))
                i = k + 1
                continue
        i += 1
    return methods


def collect_decls(lines, strict):
    d = set()
    for l in lines:
        for pat in DECL_RES:
            for m in pat.finditer(l):
                for g in m.groups():
                    if g:
                        for part in g.split(','):
                            part = part.strip()
                            if re.fullmatch(r'[a-z_]\w*', part):
                                d.add(part)
        if not strict:
            for m in ASSIGN_LHS.finditer(l):
                d.add(m.group(1))
    return d


def run(path, strict=False):
    with open(path, encoding='utf-8') as f:
        raw = f.read().split('\n')
    lines = [clean_line(l) for l in raw]

    file_defs = collect_decls(lines, strict)

    problems = []
    for name, params, start, end in find_methods(lines):
        body = lines[start:end]
        declared = set(file_defs)

        for p in params.split(','):
            pm = re.search(r'([a-z_]\w*)\s*(?:=|$)', p.strip())
            if pm:
                declared.add(pm.group(1))

        declared |= collect_decls(body, strict)

        for offset, l in enumerate(body):
            for m in re.finditer(r'\b([a-z_]\w*)\b', l):
                ident = m.group(1)
                if ident in declared or ident.lower() in WHITELIST_LOWER:
                    continue
                before = l[:m.start()].rstrip()
                if before.endswith('.'):
                    continue
                after = l[m.end():]
                if after.lstrip().startswith(':') and not after.lstrip().startswith('::'):
                    continue
                # 本行正在声明它 —— 只看"紧邻之前是类型名"。
                # ⚠️ 不能再用"后面是 ) 或 = "来排除：`Foo(bar)` 的实参后面也是 `)`，
                # 早期版本因此把实参当成声明，**漏报了真错误**。声明判定一律交给
                # DECL_RES 的宽口径正则，这里只补一个"类型名紧邻"的兜底。
                if re.search(r'\b' + TYPE_TOK + r'(?:\s*<[^;=]*?>)?\s*(?:\[\s*\])*\s*$', before):
                    continue
                problems.append((start + offset + 1, name, ident, l.strip()[:95]))

    print('局部变量完整性校验：%s%s' % (path, '  [strict]' if strict else ''))
    if problems:
        seen = set()
        for line, meth, ident, src in problems:
            if (line, ident) in seen:
                continue
            seen.add((line, ident))
            print('  ✗ L%-5d [%s] 未声明: %-16s %s' % (line, meth, ident, src))
        print()
        print('结论：发现 %d 处可疑（请人工确认）' % len(seen))
        return 1

    print('  ✓ 未发现未声明的局部变量')
    return 0


if __name__ == '__main__':
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(2)
    sys.exit(run(sys.argv[1], '--strict' in sys.argv))
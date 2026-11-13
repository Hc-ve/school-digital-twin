#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
双宏态符号解析校验器。

【为什么需要它 —— 2026-11-13 真实事故】
`check_syntax.py` 只做括号与 #if/#endif 配对，**它看不出 #if 收窄后的符号丢失**。
本轮实测踩到两次：
  1. `FindGlows()` 写在 `#if UNITY_POST_PROCESSING_STACK_V2` 内，
     却被宏外的 `SetGlowsEnabled()` 调用。
  2. `Restore()` 同样在宏内，被宏外的 `SetEnabled()` / `Shutdown()` 调用。
两次都是：括号校验 PASS，但真编译会报 CS0103（当前上下文中不存在名称）。
这类错误在 Unity 里表现为"整个脚本编译失败、所有引用它的地方一起红"，
而且因为没有 Unity CLI 无法提前发现，只能靠这种静态模拟兜住。

【做法】
分别模拟「宏已定义」与「宏未定义」两种编译状态：
  · 逐行维护栈式激活状态（支持 #if / #else / #endif / `!` 取反 / 嵌套）
  · 只收集"活跃"分支里的成员定义
  · 再扫活跃分支里的调用点，凡调用名不在定义集内即报警

【用法】
  python check_macro_symbols.py <file.cs> [macro_name]
默认 macro_name = UNITY_POST_PROCESSING_STACK_V2
"""
import re
import sys


def analyze_active(lines, define_macro, macro_name):
    """返回 dict: 行号 -> 该行是否处于激活分支。"""
    active = {}
    stack = []  # 每层: [当前分支是否激活, 父层是否激活]

    for i, line in enumerate(lines, 1):
        s = line.strip()
        parent = stack[-1][0] if stack else True

        if s.startswith('#if'):
            expr = s[3:].strip()
            neg = expr.startswith('!')
            has = macro_name in expr
            # 只认我们关心的这个宏；其余条件视为真（不参与判定）
            cond = define_macro if has else True
            if neg:
                cond = not cond
            stack.append([cond and parent, parent])
            active[i] = parent
            continue

        if s.startswith('#else'):
            if stack:
                top = stack[-1]
                top[0] = (not top[0]) and top[1]
            active[i] = parent
            continue

        if s.startswith('#endif'):
            if stack:
                stack.pop()
            active[i] = stack[-1][0] if stack else True
            continue

        active[i] = stack[-1][0] if stack else True

    return active


# 只检查静态成员方法的调用。刻意收窄，避免把局部变量误报成未定义。
CALL_RE = re.compile(r'\b([A-Z]\w+|\w*[a-z]\w+)\s*\(')
DEF_RE = re.compile(
    r'(?:private|internal|public|protected)\s+'
    r'(?:static\s+)?'
    r'(?:readonly\s+)?'
    r'[\w\[\]<>,.\?]+\s+'
    r'(\w+)\s*[\(=;]'
)


def run(path, macro_name):
    with open(path, encoding='utf-8') as f:
        lines = f.read().split('\n')

    problems = 0
    for define in (True, False):
        active = analyze_active(lines, define, macro_name)

        defs = set()
        for i, line in enumerate(lines, 1):
            if not active.get(i, True):
                continue
            m = DEF_RE.search(line)
            if m:
                defs.add(m.group(1))

        # 收集活跃分支里的"调用形态"
        refs = []
        for i, line in enumerate(lines, 1):
            if not active.get(i, True):
                continue
            s = line.strip()
            if s.startswith('//') or s.startswith('///') or s.startswith('*'):
                continue
            for m in CALL_RE.finditer(line):
                name = m.group(1)
                # 只关心本文件里自己定义过的名字：若它在任一状态下被定义过，
                # 却在当前状态下"未定义却被调用"，就是宏边界错误。
                # （跨文件/引擎 API 不在此校验范围。）
                refs.append((i, name, s[:90]))

        # 建立"全文件曾出现过的定义名"白名单，避免把 Mathf.Clamp 之类当问题
        all_defs = set()
        for i, line in enumerate(lines, 1):
            m = DEF_RE.search(line)
            if m:
                all_defs.add(m.group(1))

        missing = []
        for i, name, src in refs:
            if name in all_defs and name not in defs:
                missing.append((i, name, src))

        tag = '宏已定义' if define else '宏未定义'
        print('  [%s] 活跃定义 %d 个' % (tag, len(defs)))
        if missing:
            for i, name, src in missing:
                print('    ✗ L%-5d %-22s %s' % (i, name, src))
            problems += len(missing)
        else:
            print('    ✓ 无宏边界引用错误')

    print()
    if problems:
        print('结论：发现 %d 处宏边界引用错误（真编译会报 CS0103）' % problems)
        return 1
    print('结论：两种宏态均可编译')
    return 0


if __name__ == '__main__':
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(2)
    macro = sys.argv[2] if len(sys.argv) > 2 else 'UNITY_POST_PROCESSING_STACK_V2'
    print('双宏态符号解析校验：%s' % sys.argv[1])
    sys.exit(run(sys.argv[1], macro))
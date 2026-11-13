# -*- coding: utf-8 -*-
"""P36 语法粗校验：括号配对 + #if/#endif 配对。
不能替代真正的 C# 编译（本机无 Unity/dotnet），但能抓出最常见的
"大括号被误删" 与 "宏没闭合" 两类错误。
"""
import io
import sys


def strip_line(s):
    """去掉行内注释，并保留字符串内容（用于统计括号时必须先删掉字符串）。"""
    out = []
    in_str = False
    in_chr = False
    esc = False
    j = 0
    n = len(s)
    while j < n:
        c = s[j]
        if esc:
            out.append(c)
            esc = False
            j += 1
            continue
        if c == '\\':
            out.append(c)
            esc = True
            j += 1
            continue
        if in_str:
            if c == '"':
                in_str = False
            out.append(c)
            j += 1
            continue
        if in_chr:
            if c == "'":
                in_chr = False
            out.append(c)
            j += 1
            continue
        if c == '"':
            in_str = True
            out.append(c)
            j += 1
            continue
        if c == "'":
            in_chr = True
            out.append(c)
            j += 1
            continue
        if c == '/' and j + 1 < n and s[j + 1] == '/':
            break
        out.append(c)
        j += 1
    return ''.join(out)


def blank_strings(s):
    """把字符串/字符字面量的内容替换成空格，避免 "{" 被算进括号计数。"""
    out = []
    in_str = False
    in_chr = False
    esc = False
    for c in s:
        if esc:
            out.append(' ')
            esc = False
            continue
        if c == '\\':
            out.append(' ')
            esc = True
            continue
        if in_str:
            if c == '"':
                in_str = False
                out.append(' ')
            else:
                out.append(' ')
            continue
        if in_chr:
            if c == "'":
                in_chr = False
                out.append(' ')
            else:
                out.append(' ')
            continue
        if c == '"':
            in_str = True
            out.append(' ')
            continue
        if c == "'":
            in_chr = True
            out.append(' ')
            continue
        out.append(c)
    return ''.join(out)


def check(path):
    src = io.open(path, encoding='utf-8').read()
    lines = src.split('\n')
    depth = 0
    ifdepth = 0
    bad = []
    for i, ln in enumerate(lines, 1):
        code = blank_strings(strip_line(ln))
        st = code.strip()
        if st.startswith('#'):
            if st.startswith('#if'):
                ifdepth += 1
            elif st.startswith('#endif'):
                ifdepth -= 1
                if ifdepth < 0:
                    bad.append((i, '#endif 多余'))
            continue
        depth += code.count('{') - code.count('}')
        if depth < 0:
            bad.append((i, '大括号提前闭合'))
    name = path.replace('/', '\\').split('\\')[-1]
    ok = (depth == 0 and ifdepth == 0 and not bad)
    print(('PASS  ' if ok else 'FAIL  ') + name)
    print('        brace depth = %d (期望 0)' % depth)
    print('        #if  depth  = %d (期望 0)' % ifdepth)
    if bad:
        for b in bad:
            print('        行 %d: %s' % b)
    return ok


if __name__ == '__main__':
    allok = True
    for p in sys.argv[1:]:
        allok = check(p) and allok
    print()
    print('总体：' + ('全部通过' if allok else '存在失败项'))
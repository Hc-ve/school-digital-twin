#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
三个静态校验器一键跑（编译前必跑）。

【背景：2026-11-13 一天内被同一类错误绊了三次】
第一次：`Describe()` 的方法体大括号被误删 —— 括号校验抓到。
第二次：`FindGlows()` / `Restore()` 写在 `#if` 内却被宏外调用 ——
        括号校验 PASS，真编译报 CS0103。补了 check_macro_symbols.py。
第三次：`bool bloomWas = Enabled;` 在重构时被替换掉、引用还在 ——
        括号校验 PASS、宏校验 PASS，真编译报 CS0103。补了 check_locals.py。

三个校验器各自只覆盖一个面，缺一个就会漏。所以做成一个入口。

【用法】
  python check_all.py <file.cs> [file2.cs ...]
任一项失败 → 退出码 1。
"""
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
PY = sys.executable

CHECKS = [
    ('括号/宏配对', 'check_syntax.py'),
    ('宏边界符号', 'check_macro_symbols.py'),
    ('局部变量声明', 'check_locals.py'),
]


def main(files):
    failed = 0
    for f in files:
        print('=' * 68)
        print('文件：%s' % f)
        print('=' * 68)
        for label, script in CHECKS:
            path = os.path.join(HERE, script)
            r = subprocess.run([PY, path, f], capture_output=True, text=True)
            status = 'PASS' if r.returncode == 0 else 'FAIL'
            print('  [%s] %s' % (status, label))
            if r.returncode != 0:
                failed += 1
                for line in (r.stdout or '').splitlines():
                    if line.strip().startswith('✗') or '结论' in line:
                        print('        ' + line.strip())
        print()

    print('=' * 68)
    if failed:
        print('总计：%d 项未通过 —— 不要进 Play，先修。' % failed)
        return 1
    print('总计：全部通过（%d 个文件 × %d 项校验）' % (len(files), len(CHECKS)))
    return 0


if __name__ == '__main__':
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(2)
    sys.exit(main(sys.argv[1:]))
"""Prints the calls and field accesses of one method from a Cpp2IL ISIL dump.

usage: python -I tools/isil_method.py <isil .txt file> <method name substring> [--all]
Without --all only calls (non-runtime) and compares/stores on the object's fields are printed.
Field offsets are decimal in ISIL; interop sources list them as hex (e.g. [rbx+161] = 0xA1).
"""
import re
import sys

path, name = sys.argv[1], sys.argv[2]
show_all = "--all" in sys.argv
text = open(path, encoding="utf-8", errors="replace").read()
for block in text.split("\nMethod: ")[1:]:
    header = block.split("\n", 1)[0]
    if name not in header:
        continue
    print("=== " + header)
    isil = block.split("ISIL:", 1)[1] if "ISIL:" in block else ""
    for line in isil.splitlines():
        s = line.strip()
        if not s:
            continue
        if show_all:
            print("  " + s)
            continue
        if "Call " in s and "il2cpp_" not in s:
            print("  " + s)
        elif re.search(r"(Compare|Move) \[r(bx|cx|di|si)\+\d+\]", s) or re.search(r"Jump", s) and False:
            print("  " + s)

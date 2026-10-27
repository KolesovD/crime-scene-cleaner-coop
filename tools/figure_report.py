"""Report of the figure suite (Bot "figure"): what the host saw of the client.
Usage: python -I tools/figure_report.py <host csv> <client csv> <host log> <client log>  (the csv files are unused)"""
import os
import re
import sys


def main(_hc, _cc, host_log, client_log):
    try:
        h = open(host_log, encoding="utf-8", errors="replace").read()
        c = open(client_log, encoding="utf-8", errors="replace").read()
        h, c = (re.sub(r"(\d),(\d)", r"\1.\2", x) for x in (h, c))   # the game logs numbers in the PC's locale
    except FileNotFoundError as e:
        print(f"FAIL  missing file: {e.filename}\nRESULT: FAIL")
        return 1
    checks = []
    m = re.search(r"FIGURETEST figure: (\d+) vertices, (\d+) bones", h)
    checks.append((m is not None and int(m.group(1)) > 1000 and m.group(2) == "17", f"figure: one skinned mesh ({m.group(0) if m else 'not logged'})"))
    for what in ("mop", "sponge", "axe", "lance"):
        m = re.search(rf"FIGURETEST tool {what}: look '([^']*)' renderers (\d+) length ([\d.]+) m, right hand ([\d.]+) m", h)
        ok = m is not None and int(m.group(2)) > 0 and float(m.group(3)) > 0.1 and float(m.group(4)) < max(1.0, float(m.group(3)))
        checks.append((ok, f"tool {what}: copy in the hand ({m.group(0)[16:] if m else 'not logged'})"))
    m = re.search(r"FIGURETEST use swing: right hand travelled ([\d.]+) m", h)
    checks.append((m is not None and float(m.group(1)) > 0.12, f"use: the hand with the mop goes forward and back ({m.group(1) if m else '?'} m)"))
    checks.append(("FIGURETEST client holds body 0: True" in c, "carry: the client holds body 0"))
    checks.append((re.search(r"COOP body 0 shoulder rig: hips", h) is not None, "carry: humanoid rig of body 0 found on the host"))
    m = re.search(r"FIGURETEST shoulder: shouldered (\w+) gap ([\d.-]+) m, bones ignored (\d+), shoulder frames (\d+)", h)
    ok = m is not None and m.group(1) == "True" and 0 <= float(m.group(2)) < 0.25 and int(m.group(3)) > 0
    checks.append((ok, f"carry: on the host body 0 lies on the client's shoulder, its bone stream ignored ({m.group(0)[12:] if m else 'not logged'})"))
    fin = [re.search(r"FIGURETEST final body 0 root (\S+) (\S+) (\S+) shouldered (\w+)", x) for x in (h, c)]
    if all(fin):
        d = sum((float(fin[0].group(i)) - float(fin[1].group(i))) ** 2 for i in (1, 2, 3)) ** 0.5
        checks.append((d < 0.3 and fin[0].group(4) == "False", f"throw: off the shoulder, back on the stream: roots {d:.2f} m apart"))
    else:
        checks.append((False, "throw: final positions not logged"))
    shots = re.findall(r"FIGURETEST shot written (.+?\.png) \((\d+) bytes\)", h)
    good = [p for p, n in shots if int(n) > 20000 and os.path.exists(p)]
    checks.append((len(good) >= 10, f"pictures: {len(good)} of 10 written"))
    for ok, what in checks:
        print(f"{'PASS' if ok else 'FAIL'}  {what}")
    ok = all(x for x, _ in checks)
    print(f"RESULT: {'PASS' if ok else 'FAIL'} ({sum(1 for x, _ in checks if x)} of {len(checks)} checks)")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main(*sys.argv[1:5]))

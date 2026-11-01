"""Checks the toolcarry suite (tools/run-pair.ps1 -Test toolcarry) from both bots' logs: the client carries the host's
bucket, it rests at the same spot on both, the client can't put it into its inventory, the host can, and then it is
gone on both."""
import math
import re
import sys


def at(log, label):
    m = re.findall(r"TOOLCARRY " + re.escape(label) + r" bucket (gone|at (\S+)) moved ([\d.]+) simulated here (\w+)", log)
    if not m:
        return None
    g, p, moved, mine = m[-1]
    return {"gone": g == "gone", "pos": [float(x) for x in p.split("/")] if p else None, "moved": float(moved), "mine": mine == "True"}


def main(_h, _c, host_log, client_log):
    try:
        h = open(host_log, encoding="utf-8", errors="replace").read()
        c = open(client_log, encoding="utf-8", errors="replace").read()
    except FileNotFoundError as e:
        print(f"FAIL  missing log: {e.filename}\nRESULT: FAIL")
        return 1
    if "TOOLCARRY dump done" not in h or "TOOLCARRY dump done" not in c:
        print("FAIL  dumps missing\nRESULT: FAIL")
        return 1
    hr, cr = at(h, "rest"), at(c, "rest")
    ha, ca = at(h, "after client pick-up"), at(c, "after client pick-up")
    hf, cf = at(h, "final"), at(c, "final")
    checks = [("TOOLCARRY client sees the host's bucket: NO" not in c and "TOOLCARRY client sees" in c, "the client sees the host's bucket")]
    checks.append(("TOOLCARRY client grab: holding" in c, "the client could grab the host's bucket"))
    checks.append((cr and not cr["gone"] and cr["moved"] > 0.5, f"the client carried it away ({cr and cr['moved']:.2f} m)"))
    d = math.dist(hr["pos"], cr["pos"]) if hr and cr and hr["pos"] and cr["pos"] else 99
    checks.append((d < 0.05, f"it rests at the same spot on both ({d:.2f} m apart)"))
    checks.append((cr and hr and cr["mine"] and not hr["mine"], f"the client simulates it now, the host follows (client {cr and cr['mine']}, host {hr and hr['mine']})"))
    checks.append(("pick-up of player 0's" in c and ha and ca and not ha["gone"] and not ca["gone"], "the client can't put the host's bucket into its inventory (still there on both)"))
    checks.append((hf and cf and hf["gone"] and cf["gone"], f"the host picked it up and it is gone on both (host {hf and hf['gone']}, client {cf and cf['gone']})"))
    for ok, what in checks:
        print(f"{'PASS' if ok else 'FAIL'}  {what}")
    ok = all(x for x, _ in checks)
    print(f"RESULT: {'PASS' if ok else 'FAIL'} ({sum(1 for x, _ in checks if x)} of {len(checks)} checks)")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main(*sys.argv[1:5]))

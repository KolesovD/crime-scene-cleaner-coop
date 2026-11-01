"""Checks the bump suite (tools/run-pair.ps1 -Test bump) from both bots' logs: an item the client throws into a resting
item the host simulates knocks it away on both machines, it ends at the same spot, and every item lies at the same spot
on both."""
import math
import re
import sys


def last(log, pattern):
    m = re.findall(pattern, log)
    return m[-1] if m else None


def items(log):
    m = last(log, r"BUMP items (\S*)")
    out = {}
    for part in (m or "").split(";"):
        if "=" in part:
            i, p = part.split("=")
            out[int(i)] = [float(x) for x in p.split("/")]
    return out


def main(_h, _c, host_log, client_log):
    try:
        h = open(host_log, encoding="utf-8", errors="replace").read()
        c = open(client_log, encoding="utf-8", errors="replace").read()
    except FileNotFoundError as e:
        print(f"FAIL  missing log: {e.filename}\nRESULT: FAIL")
        return 1
    if "BUMP dump done" not in h or "BUMP dump done" not in c:
        print("FAIL  dumps missing\nRESULT: FAIL")
        return 1
    pat = r"BUMP final target (-?\d+) at (\S+) moved ([\d.]+) striker at (\S+)"
    hm, cm = last(h, pat), last(c, pat)
    checks = [("BUMP client threw" in c, "the client threw the striker into the target")]
    checks.append((hm and cm and hm[0] == cm[0] and hm[0] != "-1", f"same roles on both (target host {hm and hm[0]}, client {cm and cm[0]})"))
    hmov, cmov = (float(hm[2]) if hm else 0), (float(cm[2]) if cm else 0)
    checks.append((cmov > 0.1, f"the target was knocked away on the client, not a wall ({cmov:.2f} m)"))
    checks.append((hmov > 0.1, f"the target was knocked away on the host too ({hmov:.2f} m)"))
    d = math.dist([float(x) for x in hm[1].split("/")], [float(x) for x in cm[1].split("/")]) if hm and cm else 99
    checks.append((d < 0.05, f"the target ends at the same spot on both ({d:.2f} m apart)"))
    hi, ci = items(h), items(c)
    common = set(hi) & set(ci)
    off = sorted(i for i in common if math.dist(hi[i], ci[i]) > 0.05)
    checks.append((len(common) > 0 and len(set(hi) ^ set(ci)) == 0, f"the same items are active on both ({len(common)} common, {len(set(hi) ^ set(ci))} on one only)"))
    checks.append((len(common) > 0 and not off, f"every item lies at the same spot on both (5 cm; {len(off)} differ{': ' + ','.join(map(str, off[:10])) if off else ''})"))
    for ok, what in checks:
        print(f"{'PASS' if ok else 'FAIL'}  {what}")
    ok = all(x for x, _ in checks)
    print(f"RESULT: {'PASS' if ok else 'FAIL'} ({sum(1 for x, _ in checks if x)} of {len(checks)} checks)")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main(*sys.argv[1:5]))

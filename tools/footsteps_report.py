"""Checks the footsteps suite (tools/run-pair.ps1 -Test footsteps): footprints the client leaves (FootprintSpawner)
appear on the host too, the same decal at the same spot."""
import math
import re
import sys


def stains(log):
    m = re.findall(r"FOOTSTEPS final made (\d+) copied (\d+) new (\d+) (\S*)", log)
    if not m:
        return None, {}
    made, copied, n, rest = m[-1]
    out = {}
    for part in rest.split(";"):
        mm = re.match(r"(\w+)=(.*)@(\S+)/(\S+)/(\S+)~(\S+)", part)
        if mm:
            out[mm.group(1)] = (mm.group(2), [float(mm.group(i)) for i in (3, 4, 5)], float(mm.group(6)))
    return (int(made), int(copied)), out


def main(_h, _c, host_log, client_log):
    try:
        h = open(host_log, encoding="utf-8", errors="replace").read()
        c = open(client_log, encoding="utf-8", errors="replace").read()
    except FileNotFoundError as e:
        print(f"FAIL  missing log: {e.filename}\nRESULT: FAIL")
        return 1
    if "FOOTSTEPS dump done" not in h or "FOOTSTEPS dump done" not in c:
        print("FAIL  dumps missing\nRESULT: FAIL")
        return 1
    (hm, hc), hs = stains(h)
    (cm, cc), cs = stains(c)
    feet = {k: v for k, v in cs.items() if "oot" in v[0]}
    checks = [(len(feet) >= 6, f"the client left footprints ({len(feet)} footprint decals of {len(cs)} new stains: {sorted(set(v[0] for v in cs.values()))})")]
    missing = [k for k in cs if k not in hs]
    checks.append((cs and not missing, f"every new stain of the client is on the host ({len(missing)} missing)"))
    wrong = [k for k in cs if k in hs and hs[k][0] != cs[k][0]]
    checks.append((cs and not wrong, f"the same decal on both ({len(wrong)} differ{': ' + ', '.join(f'{cs[k][0]} -> {hs[k][0]}' for k in wrong[:3]) if wrong else ''})"))
    far = [k for k in cs if k in hs and math.dist(hs[k][1], cs[k][1]) > 0.02]
    checks.append((cs and not far, f"at the same spot (2 cm; {len(far)} off)"))
    dim = [k for k in cs if k in hs and abs(hs[k][2] - cs[k][2]) > 0.05]
    checks.append((cs and not dim, f"as strong on both ({len(dim)} differ)"))
    for ok, what in checks:
        print(f"{'PASS' if ok else 'FAIL'}  {what}")
    ok = all(x for x, _ in checks)
    print(f"RESULT: {'PASS' if ok else 'FAIL'} ({sum(1 for x, _ in checks if x)} of {len(checks)} checks)")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main(*sys.argv[1:5]))

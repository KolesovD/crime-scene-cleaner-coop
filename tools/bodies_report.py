"""Compares the body and van tables the bots dumped (tools/run-pair.ps1 -Test bodies).

Rows: label,kind,index,x,y,z,a,b with kind root/bone (index = body*100 + bone, a = owner, b = held)
or van (a = collected, b = active). Van items G0 (host) and G1 (client) come from the host log.
"""
import csv
import math
import re
import sys


def load(path):
    rows = {}
    with open(path, newline="") as f:
        for r in csv.reader(f):
            rows.setdefault(r[0], {})[(r[1], int(r[2]))] = (float(r[3]), float(r[4]), float(r[5]), int(r[6]), int(r[7]))
    return rows


def d(a, b):
    return math.dist(a[:3], b[:3])


def main(host_csv, client_csv, host_log, client_log=None):
    checks = []
    try:
        h, c = load(host_csv), load(client_csv)
        log = open(host_log, encoding="utf-8", errors="replace").read()
    except FileNotFoundError as e:
        print(f"FAIL  missing file: {e.filename}\nRESULT: FAIL")
        return 1
    if "BODYTEST no bodies" in log:
        print("FAIL  no bodies in this job\nRESULT: FAIL")
        return 1
    m = re.search(r"van items G0 (-?\d+) G1 (-?\d+)", log)
    g0, g1 = (int(m.group(1)), int(m.group(2))) if m else (-1, -1)
    for lab in ("start", "carry", "final"):
        if lab not in h or lab not in c:
            print(f"FAIL  dump {lab} missing\nRESULT: FAIL")
            return 1

    root = ("root", 0)
    cc, hc = c["carry"], h["carry"]
    checks.append((cc[root][4] == 1, "carry: the client holds body 0"))
    checks.append((d(cc[root], c["start"][root]) > 0.3, f"carry: body 0 moved {d(cc[root], c['start'][root]):.2f} m from where it lay"))
    # 0.3.3: while the client carries it, the host shows the body on the client's shoulder (its first-person
    # carry pose is not copied any more), so the two machines differ on purpose until it is let go.
    m = re.search(r"BODYTEST host sees body 0 on the client's shoulder: (\w+) gap ([\d.-]+) m, bones ignored (\d+)", log)
    checks.append((m is not None and m.group(1) == "True" and 0 <= float(m.group(2)) < 0.25 and int(m.group(3)) > 0,
                   f"carry: on the host body 0 lies on the client's shoulder ({m.group(0)[24:] if m else 'not logged'})"))
    checks.append((d(cc[root], hc[root]) < 1.5, f"carry: body 0 root {d(cc[root], hc[root]):.2f} m apart (on the shoulder vs in front of the camera)"))
    checks.append(("BODYTEST host steal of body 0: refused" in log, "steal: the host could not take the body out of the client's hands"))

    clog_all = open(client_log or host_log.replace("host.log", "client.log"), encoding="utf-8", errors="replace").read()
    hf, cf = h["final"], c["final"]
    def counts(text):
        return dict(re.findall(r"BODYTEST objective '(.+?)' bodies (\d+)", text))
    hc_, cc_ = counts(log), counts(clog_all)
    counted = any(int(v) > 0 for v in hc_.values())
    checks.append((counted and hc_ == cc_, f"objectives: each body objective's count the same on both, a van body counted (host {hc_}, client {cc_})"))
    # The hand-over after the throw must happen at rest: handed over while still falling, the body froze in the
    # air on both machines (owner's two-PC test).
    clog = clog_all
    after = clog.split("BODYTEST client throws", 1)[1] if "BODYTEST client throws" in clog else ""
    # The bug was a body frozen in the air after the hand-over: at the end it must lie on something on the host
    # (the watch raycasts down from its root: what is under it and how far).
    hw = re.findall(r"BODYTEST watch y [\d,.-]+ kinematic \w+ owner (\d).*under: '([^']*)' ([\d,.]+) m below", log)
    if hw:
        owner, what, dist = hw[-1]
        dv = float(dist.replace(",", "."))
        checks.append((owner == "0" and dv < 0.4, f"throw: handed back to the host and lying on '{what}' ({dv:.2f} m under the root)"))
    else:
        checks.append((False, "throw: no height watch on the host"))
    checks.append((cf[root][4] == 0 and hf[root][4] == 0, "throw: nobody holds body 0 at the end"))
    # Carried low (root ~0.1-0.4 m above where it lands), so no height threshold: it must have moved on the host
    # (the bug: the host kept it frozen where the client let go) and end up level with or below the carry.
    moved = d(hc[root], hf[root])
    checks.append((moved > 0.1 and hf[root][1] <= hc[root][1] + 0.05, f"throw: body 0 moved {moved:.2f} m on the host after the client threw it (y {hc[root][1]:.2f} -> {hf[root][1]:.2f})"))
    bones = [k for k in hf if k[0] in ("root", "bone")]
    far = [k for k in bones if k in cf and d(hf[k], cf[k]) > 0.1]
    checks.append((bones and not far, f"final: {len(bones) - len(far)} of {len(bones)} bones of all bodies within 10 cm"))
    for k in far[:5]:
        print(f"   {k}: host {hf[k][:3]} client {cf[k][:3]} ({d(hf[k], cf[k]):.2f} m)")
    for name, g, who in (("G0", g0, "host"), ("G1", g1, "client")):
        k = ("van", g)
        ok = g >= 0 and k in hf and k in cf and hf[k][3] == 1 and cf[k][3] == 1
        checks.append((ok, f"van: item {name} ({g}) collected by the {who}, collected on both"))
    vans = [k for k in hf if k[0] == "van" and k[1] not in (g0, g1)]
    diff = [k for k in vans if k not in cf or hf[k][3:] != cf[k][3:]]
    checks.append((not diff, f"van: the other {len(vans)} van items agree"))

    for ok, what in checks:
        print(f"{'PASS' if ok else 'FAIL'}  {what}")
    ok = all(x for x, _ in checks)
    print(f"RESULT: {'PASS' if ok else 'FAIL'} ({sum(1 for x, _ in checks if x)} of {len(checks)} checks)")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main(*sys.argv[1:5]))

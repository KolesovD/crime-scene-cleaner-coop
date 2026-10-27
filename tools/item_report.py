"""Compares the item tables the host and client bots dumped (tools/run-pair.ps1 -Test items).

Rows: label,index,x,y,z,owner,epoch,held. Prints RESULT: PASS only if
 - carry: the item the client holds is within 0.6 m of it on the host,
 - hostcarry: the item the host holds is within 0.6 m of it on the client,
 - final: every item within 5 cm on both sides, same owner and epoch,
 - the host's attempt to take the client's held item was refused (host log).
"""
import csv
import math
import sys


def load(path):
    rows = {}
    with open(path, newline="") as f:
        for r in csv.reader(f):
            rows.setdefault(r[0], {})[int(r[1])] = (float(r[2]), float(r[3]), float(r[4]), int(r[5]), int(r[6]), int(r[7]))
    return rows


def dist(a, b):
    return math.dist(a[:3], b[:3])


def main(host_csv, client_csv, host_log):
    checks = []
    try:
        h, c = load(host_csv), load(client_csv)
    except FileNotFoundError as e:
        print(f"FAIL  missing dump: {e.filename}")
        print("RESULT: FAIL")
        return 1

    def held_check(label, holder, other, who, before="start"):
        if label not in holder or label not in other:
            checks.append((False, f"{label}: dump missing"))
            return
        held = [i for i, r in holder[label].items() if r[5] == 1]
        if not held:
            checks.append((False, f"{label}: {who} holds nothing"))
            return
        i = held[0]
        d = dist(holder[label][i], other[label][i])
        checks.append((d < 0.6, f"{label}: item {i} held by the {who} is {d:.2f} m apart on the two machines"))
        # Without this, an item that never left its spot would pass the check above.
        if before in holder and i in holder[before]:
            m = dist(holder[before][i], holder[label][i])
            checks.append((m > 0.3, f"{label}: the carried item moved {m:.2f} m from where it lay"))
        else:
            checks.append((False, f"{label}: no '{before}' dump to measure the carry"))

    held_check("carry", c, h, "client", "start")
    held_check("hostcarry", h, c, "host", "carry")

    if "final" in h and "final" in c:
        common = sorted(set(h["final"]) & set(c["final"]))
        ds = sorted(((dist(h["final"][i], c["final"][i]), i) for i in common), reverse=True)
        far = [x for x in ds if x[0] > 0.05]
        checks.append((len(common) == len(h["final"]) == len(c["final"]), f"final: same item count ({len(h['final'])} / {len(c['final'])})"))
        checks.append((not far, f"final: {len(common) - len(far)} of {len(common)} items within 5 cm, max {ds[0][0]:.3f} m (item {ds[0][1]})"))
        for d, i in far[:5]:
            print(f"   item {i}: {d:.3f} m  host {h['final'][i][:3]}  client {c['final'][i][:3]}")
        own = [i for i in common if h["final"][i][3:5] != c["final"][i][3:5]]
        checks.append((not own, f"final: owner and epoch agree on {len(common) - len(own)} of {len(common)} items"))
    else:
        checks.append((False, "final: dump missing"))

    try:
        log = open(host_log, encoding="utf-8", errors="replace").read()
        checks.append(("ITEMTEST host steal" in log and "refused" in log.split("ITEMTEST host steal", 1)[1].splitlines()[0],
                       "steal: the host could not take the item out of the client's hands"))
    except FileNotFoundError:
        checks.append((False, "steal: host log missing"))

    for ok, what in checks:
        print(f"{'PASS' if ok else 'FAIL'}  {what}")
    ok = all(x for x, _ in checks)
    print(f"RESULT: {'PASS' if ok else 'FAIL'} ({sum(x for x, _ in checks)} of {len(checks)} checks)")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main(*sys.argv[1:4]))

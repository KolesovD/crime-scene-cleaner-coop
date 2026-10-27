"""Compares the door / key / evidence tables the bots dumped (tools/run-pair.ps1 -Test doors).

Rows: label,kind,index,a,b,c,d. door: a = locked, b/c/d = sums over its moving parts (x+z, y, |rotation|).
key: a = in this machine's KeyHolder, b = key object active. evidence: a = collected, b = active.
S0 (host unlocked + opened), S1 (client opened, later closed) come from the host log.
"""
import csv
import re
import sys


def load(path):
    rows = {}
    with open(path, newline="") as f:
        for r in csv.reader(f):
            rows.setdefault(r[0], {})[(r[1], int(r[2]))] = tuple(float(x) for x in r[3:7])


    return rows


def same_pose(a, b):
    return all(abs(x - y) < 0.02 for x, y in zip(a[1:], b[1:]))


def main(host_csv, client_csv, host_log):
    checks = []
    try:
        h, c = load(host_csv), load(client_csv)
        log = open(host_log, encoding="utf-8", errors="replace").read()
    except FileNotFoundError as e:
        print(f"FAIL  missing file: {e.filename}\nRESULT: FAIL")
        return 1
    m = re.search(r"DOORTEST S0 (-?\d+) S1 (-?\d+)", log)
    if not m or any(l not in h or l not in c for l in ("start", "mid", "final")):
        print("FAIL  roles or dumps missing\nRESULT: FAIL")
        return 1
    s0, s1 = ("door", int(m.group(1))), ("door", int(m.group(2)))
    hs, hm, cm, hf, cf = h["start"], h["mid"], c["mid"], h["final"], c["final"]

    checks.append((hs[s0][0] == 1, "S0: locked at the start"))
    checks.append((hf[s0][0] == 0 and cf[s0][0] == 0, f"S0: unlocked by the host, unlocked on both (host {hf[s0][0]:.0f}, client {cf[s0][0]:.0f})"))
    checks.append((not same_pose(hs[s0], hm[s0]), "S0: the host's door actually moved"))
    checks.append((same_pose(hm[s0], cm[s0]), f"S0: open pose the same on both (host {hm[s0]}, client {cm[s0]})"))
    checks.append((not same_pose(hs[s1], cm[s1]), "S1: the client's door actually moved"))
    checks.append((same_pose(hm[s1], cm[s1]), f"S1: client's open pose the same on both (host {hm[s1]}, client {cm[s1]})"))
    checks.append((same_pose(hf[s1], cf[s1]), f"S1: client's close the same on both (host {hf[s1]}, client {cf[s1]})"))
    others = [k for k in hf if k[0] == "door" and k not in (s0, s1)]
    diff = [k for k in others if k not in cf or not same_pose(hf[k], cf[k]) or hf[k][0] != cf[k][0]]
    checks.append((not diff, f"doors: the other {len(others)} doors agree"))
    k0, e0 = ("key", 0), ("evidence", 0)
    if k0 in hf:
        checks.append((hf[k0][0] == 1 and cf[k0][0] == 1, f"key 0: picked up by the client, in both players' key holders (host {hf[k0][0]:.0f}, client {cf[k0][0]:.0f})"))
    if e0 in hf:
        checks.append((hf[e0][0] == 1 and cf[e0][0] == 1, f"evidence 0: collected by the client, collected on both (host {hf[e0][0]:.0f}, client {cf[e0][0]:.0f})"))
    rest = [k for k in hf if k[0] in ("key", "evidence") and k not in (k0, e0)]
    diff = [k for k in rest if k not in cf or hf[k][0] != cf[k][0]]
    checks.append((not diff, f"keys/evidence: the other {len(rest)} agree"))

    # Client: secret 0, valuable 0, cassette 0. Host: breakable object 0 and the first active breakable item.
    v0, x0, b0 = ("valuable", 0), ("secret", 0), ("broken", 0)
    if v0 in hf:
        checks.append((hf[v0][0] == 1 and cf[v0][0] == 1 and hf[v0][1] == 1 and cf[v0][1] == 1,
                       f"valuable 0: stolen by the client, stolen and on both reports (host {hf[v0][:2]}, client {cf[v0][:2]})"))
    if x0 in hf:
        checks.append((hf[x0][0] == 1 and cf[x0][0] == 1, f"secret 0: found by the client, found on both (host {hf[x0][0]:.0f}, client {cf[x0][0]:.0f})"))
    if b0 in hf:
        checks.append((hf[b0][0] == 1 and cf[b0][0] == 1, f"breakable object 0: destroyed by the host, on both (host {hf[b0][0]:.0f}, client {cf[b0][0]:.0f})"))
    m = re.search(r"DOORTEST host cracked breakable (\d+)", log)
    if m:
        k = ("crack", int(m.group(1)))
        checks.append((hf[k][0] == 1 and cf[k][0] == 1, f"breakable item {k[1]}: cracked by the host, cracked on both (host {hf[k][0]:.0f}, client {cf[k][0]:.0f})"))
    rest = [k for k in hf if k[0] in ("valuable", "secret", "crack", "broken") and k not in (v0, x0, b0) and not (m and k == ("crack", int(m.group(1))))]
    diff = [k for k in rest if k not in cf or hf[k][0] != cf[k][0]]
    checks.append((not diff, f"valuables/secrets/breakables: the other {len(rest)} agree"))
    for k in diff[:5]:
        print(f"   {k}: host {hf[k]} client {cf.get(k)}")

    mq = re.search(r"QUESTTEST host set '([^']+)' entry 1", log)
    if mq:
        try:
            clog = open(sys.argv[4], encoding="utf-8", errors="replace").read()
        except (IndexError, FileNotFoundError):
            clog = ""
        checks.append((f"QUESTTEST '{mq.group(1)}' entry 1 is Success" in clog, f"quest: the host's entry 1 of '{mq.group(1)}' is success on the client too"))
    for ok, what in checks:
        print(f"{'PASS' if ok else 'FAIL'}  {what}")
    ok = all(x for x, _ in checks)
    print(f"RESULT: {'PASS' if ok else 'FAIL'} ({sum(1 for x, _ in checks if x)} of {len(checks)} checks)")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main(*sys.argv[1:4]))

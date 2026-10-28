"""Checks the breakhit suite (tools/run-pair.ps1 -Test breakhit) from both bots' logs: an item thrown by the client
onto a breakable the host owns breaks it once, one machine simulates its shards, and the shards end at the same spots."""
import math
import re
import sys


def last(log, pattern):
    m = re.findall(pattern, log)
    return m[-1] if m else None


def main(_h, _c, host_log, client_log):
    try:
        h = open(host_log, encoding="utf-8", errors="replace").read()
        c = open(client_log, encoding="utf-8", errors="replace").read()
    except FileNotFoundError as e:
        print(f"FAIL  missing log: {e.filename}\nRESULT: FAIL")
        return 1
    if "BREAKHIT dump done" not in h or "BREAKHIT dump done" not in c:
        print("FAIL  dumps missing\nRESULT: FAIL")
        return 1
    pat = r"BREAKHIT final cracked (\w+) shards (\d+) simulatedHere (\d+) at (\S+)"
    hm, cm = last(h, pat), last(c, pat)
    checks = []
    thrown = "BREAKHIT client threw" in c
    checks.append((thrown, "the client threw the striker onto the breakable"))
    checks.append((hm and cm and hm[0] == "True" and cm[0] == "True", f"the breakable is broken on both (host {hm and hm[0]}, client {cm and cm[0]})"))
    checks.append((hm and cm and hm[1] == cm[1] and int(hm[1]) > 0, f"same number of shards on both (host {hm and hm[1]}, client {cm and cm[1]})"))
    owners = (int(hm[2]) if hm else -1, int(cm[2]) if cm else -1)
    lying = sum(1 for x in (hm[3].split(";") if hm else []) if x not in ("gone", "off"))
    checks.append((hm and cm and (owners[0] == 0) != (owners[1] == 0) and sum(owners) == lying,
                   f"the shards still lying around are simulated by one machine only (host {owners[0]}, client {owners[1]}, lying {lying})"))
    worst = -1.0
    if hm and cm and hm[3] != "-" and cm[3] != "-":
        for a, b in zip(hm[3].split(";"), cm[3].split(";")):
            if a in ("gone", "off") or b in ("gone", "off"):
                continue
            pa, pb = [float(x) for x in a.split("/")], [float(x) for x in b.split("/")]
            worst = max(worst, math.dist(pa, pb))
    checks.append((0 <= worst < 0.1, f"the shards lie at the same spots on both (worst {worst:.2f} m)"))
    hs = hm[3].split(";") if hm else []
    cs = cm[3].split(";") if cm else []
    picked = "BREAKHIT host collected shard 2" in h
    checks.append((picked and "other player True" in h and len(hs) > 2 and len(cs) > 2 and hs[2] == "off" and cs[2] == "off",
                   f"a shard the host picked up (the client simulates it) is gone on both (host {hs[2] if len(hs) > 2 else None}, client {cs[2] if len(cs) > 2 else None})"))
    for ok, what in checks:
        print(f"{'PASS' if ok else 'FAIL'}  {what}")
    ok = all(x for x, _ in checks)
    print(f"RESULT: {'PASS' if ok else 'FAIL'} ({sum(1 for x, _ in checks if x)} of {len(checks)} checks)")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main(*sys.argv[1:5]))

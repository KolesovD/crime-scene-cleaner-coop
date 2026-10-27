"""Checks the follow suite (tools/run-pair.ps1 -Test follow): the host moves from its job to another job; the
client, still in the old job, switches by itself and both share the new job."""
import re
import sys


def main(_h, _c, host_log, client_log):
    try:
        h = open(host_log, encoding="utf-8", errors="replace").read()
        c = open(client_log, encoding="utf-8", errors="replace").read()
    except FileNotFoundError as e:
        print(f"FAIL  missing log: {e.filename}\nRESULT: FAIL")
        return 1
    hs = re.search(r"FOLLOWTEST together in '([^']+)'", h)
    cs = re.search(r"FOLLOWTEST together in '([^']+)'", c)
    checks = [
        ("FOLLOWTEST host starts" in h, "the host started another job from inside a job"),
        ("COOP job ticket" in h, "the host sent a job ticket"),
        (re.search(r"COOP in job \d+, the host is in job \d+: switching", c) is not None, "the client, in another job, switched by itself"),
        (hs is not None and cs is not None and hs.group(1) == cs.group(1), f"both together in the new job ({hs and hs.group(1)} / {cs and cs.group(1)})"),
    ]
    for ok, what in checks:
        print(f"{'PASS' if ok else 'FAIL'}  {what}")
    ok = all(x for x, _ in checks)
    print(f"RESULT: {'PASS' if ok else 'FAIL'} ({sum(1 for x, _ in checks if x)} of {len(checks)} checks)")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main(*sys.argv[1:5]))

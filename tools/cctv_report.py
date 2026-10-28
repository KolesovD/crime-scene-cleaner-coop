"""Checks the CCTV suite (tools/run-pair.ps1 -Test cctv): the client hacks the Smart House laptop and deletes a
recording, then all of them; the host's laptop must be hacked too, the recordings gone from its folder and the
objective done."""
import re
import sys


def main(_h, _c, host_log, client_log):
    h = open(host_log, encoding="utf-8", errors="replace").read()
    c = open(client_log, encoding="utf-8", errors="replace").read()
    checks = []
    ok_scene = "CCTVTEST laptop" in h and "CCTVTEST laptop" in c
    checks.append((ok_scene, "both in a job with a CCTV laptop"))
    checks.append(("CCTVTEST client hacked and deleted" in c, "the client hacked the laptop and deleted a recording"))
    hm = re.findall(r"CCTVTEST hacked (\w+) record present (\w+)", h)
    cm = re.findall(r"CCTVTEST hacked (\w+) record present (\w+)", c)
    checks.append((bool(hm) and hm[-1][0] == "True", f"hack: the host's laptop is hacked too ({hm[-1] if hm else None})"))
    # The recordings exist from the start; the client deletes one, then all the others, through the laptop's presenter
    # (the UI's own path).
    real = re.search(r"role record '([^']+)'", c)
    checks.append((bool(real) and real.group(1) != "CCTV_TEST_RECORD", f"the laptop has real recordings ({real.group(1) if real else None})"))
    checks.append((bool(hm) and hm[-1][1] == "False" and bool(cm) and cm[-1][1] == "False",
                   f"delete: the recording is gone on both (host {hm[-1] if hm else None}, client {cm[-1] if cm else None})"))
    hl = re.findall(r"CCTVTEST recordings left (\d+) quest '([^']*)' (\w+)", h)
    cl = re.findall(r"CCTVTEST recordings left (\d+) quest '([^']*)' (\w+)", c)
    checks.append((bool(hl) and bool(cl) and hl[-1][0] == "0" and cl[-1][0] == "0",
                   f"delete all: no recordings left on both (host {hl[-1][0] if hl else None}, client {cl[-1][0] if cl else None})"))
    checks.append((bool(hl) and bool(cl) and hl[-1][2] == "Success" and cl[-1][2] == "Success",
                   f"delete all: the objective is done on both (host {hl[-1][1:] if hl else None}, client {cl[-1][1:] if cl else None})"))
    for ok, what in checks:
        print(f"{'PASS' if ok else 'FAIL'}  {what}")
    ok = all(x for x, _ in checks)
    print(f"RESULT: {'PASS' if ok else 'FAIL'} ({sum(1 for x, _ in checks if x)} of {len(checks)} checks)")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main(*sys.argv[1:5]))

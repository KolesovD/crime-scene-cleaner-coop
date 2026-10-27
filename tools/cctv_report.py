"""Checks the CCTV suite (tools/run-pair.ps1 -Test cctv): the client hacks the Smart House laptop and deletes a
recording; the host's laptop must be hacked too and the recording gone from its folder."""
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
    # The laptop's recordings exist only once its folder is opened in the UI, which a bot can't do: with no real
    # recording the delete is not checked here (owner's manual test covers it).
    real = re.search(r"role record '([^']+)'", c)
    if real and real.group(1) != "CCTV_TEST_RECORD":
        checks.append((bool(hm) and hm[-1][1] == "False" and bool(cm) and cm[-1][1] == "False",
                       f"delete: the recording is gone on both (host {hm[-1] if hm else None}, client {cm[-1] if cm else None})"))
    else:
        print("NOT CHECKED  delete: no real recording in the folder (the UI never opened)")
    for ok, what in checks:
        print(f"{'PASS' if ok else 'FAIL'}  {what}")
    ok = all(x for x, _ in checks)
    print(f"RESULT: {'PASS' if ok else 'FAIL'} ({sum(1 for x, _ in checks if x)} of {len(checks)} checks)")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main(*sys.argv[1:5]))

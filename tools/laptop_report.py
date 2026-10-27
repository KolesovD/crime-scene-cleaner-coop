"""Checks the laptop jobs flow from both bots' logs (tools/run-pair.ps1 -Test laptop).

usage: laptop_report.py <host csv (unused)> <client csv (unused)> <host log> <client log>
"""
import re
import sys


def main(_h, _c, host_log, client_log):
    try:
        h = open(host_log, encoding="utf-8", errors="replace").read()
        c = open(client_log, encoding="utf-8", errors="replace").read()
    except FileNotFoundError as e:
        print(f"FAIL  missing log: {e.filename}\nRESULT: FAIL")
        return 1
    m = re.search(r"LAPTOPTEST host accepts mail \d+ for job (\d+)", h)
    job = m.group(1) if m else None
    lack = re.search(r"LAPTOPTEST host tries the truck with job (-?\d+) \(the client lacks it\)", h)
    checks = [
        ("COOP new game in slot 3" in h and "COOP new game in slot 3" in c and "MENUFLOW client pressed 'Играть у" in c,
         "menu: both started from the co-op screen"),
        ("LAPTOPTEST the story delivers mail" in h and "LAPTOPTEST the story delivers mail" in c, "mail: the next mail reached both laptops"),
        ("LAPTOPTEST in the hub" in h and "LAPTOPTEST in the hub" in c, "hub: both got to the hub"),
        (job is not None, f"laptop: the host accepted a job on its laptop ({job})"),
        (job is not None and f"the host accepted job {job} " in c and "accepting it on my laptop too" in c,
         "laptop: the client's laptop accepted the same job"),
        (job is not None and f"has the host's laptop job {job}: True" in h, "laptop: the host sees the client has it"),
        (bool(lack) and lack.group(1) != "-1" and f"COOP truck start of job {lack.group(1)} refused" in h,
         "truck: the host can't start a job the client lacks"),
        (job is not None and f"LAPTOPTEST host takes the truck to job {job}" in h and f"truck start of job {job} refused" not in h,
         "truck: the host starts the accepted job"),
        (job is not None and f"LAPTOPTEST in job {job} together" in h and f"LAPTOPTEST in job {job} together" in c,
         "together: both in the laptop job"),
    ]
    for ok, what in checks:
        print(f"{'PASS' if ok else 'FAIL'}  {what}")
    ok = all(x for x, _ in checks)
    print(f"RESULT: {'PASS' if ok else 'FAIL'} ({sum(1 for x, _ in checks if x)} of {len(checks)} checks)")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main(*sys.argv[1:5]))

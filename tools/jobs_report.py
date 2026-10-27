"""Checks the jobs flow from both bots' logs (tools/run-pair.ps1 -Test jobs).

usage: jobs_report.py <host csv (unused)> <client csv (unused)> <host log> <client log>
"""
import re
import sys


def ping(log):
    m = re.findall(r"JOBTEST ping (\d+) ms", log)
    return int(m[-1]) if m else 0


def refused_lacking(h):
    m = re.search(r"JOBTEST host tries the truck with job (\d+) \(the client lacks it\)", h)
    return bool(m) and m.group(1) != "-1" and f"COOP truck start of job {m.group(1)} refused" in h


def main(_h, _c, host_log, client_log):
    try:
        h = open(host_log, encoding="utf-8", errors="replace").read()
        c = open(client_log, encoding="utf-8", errors="replace").read()
    except FileNotFoundError as e:
        print(f"FAIL  missing log: {e.filename}\nRESULT: FAIL")
        return 1
    checks = [
        ("JOBTEST in job" in h and "together (#1)" in c, "start: both in the job together"),
        ("JOBTEST host leaves" in h and "another player left the job: leaving too" in c, "leave: the host left, the client left with it"),
        ("JOBTEST report screen open" in h and "JOBTEST report screen open" in c, "report: both got the report screen"),
        ("JOBTEST in the hub" in h and "JOBTEST in the hub" in c, "hub: both back in the hub"),
        ("job ticket 2" in h or "job ticket 1" in h, "ticket: the host's new job sent a ticket"),
        ("following the host into job" in c and "JOBTEST client followed into" in c, "follow: the client started the host's job by itself"),
        ("together again (#2)" in h and "together again (#2)" in c, "again: both in the same job again and sharing it"),
        # From the main menu's Co-op button, the players' way (CSCCOOP_VIA_MENU).
        ("co-op button added to the main menu" in h and "MENUFLOW pressed the main menu's Co-op button: page Root" in h
         and "MENUFLOW pressed the main menu's Co-op button: page Root" in c, "menu: the Co-op button opens the co-op screen on both"),
        ("COOPSCREEN hosting with slot" in h and "COOP new game in slot" in h, "menu: the host hosts with a slot chosen on the screen"),
        ("MENUFLOW client pressed 'Играть у" in c and "COOPSCREEN playing at" in c and "COOP new game in slot" in c and "synced as player 1" in c,
         "menu: the client joins with its slot in one click and connects"),
        # The hub: each in their own apartment, seeing each other, nothing else synced.
        ("hub, players only" in h and "hub, players only" in c, "hub: shared as players only"),
        ("JOBTEST hub: partner visible True" in h and "JOBTEST hub: partner visible True" in c, "hub: both see the other player"),
        (ping(h) > 0 and ping(c) > 0, f"ping: measured on both (host {ping(h)} ms, client {ping(c)} ms)"),
        # Saves stay whole.
        ("JOBTEST client tries the truck with job 0" in c and "COOP truck start of job 0 refused" in c, "truck: the client can't start a job"),
        (refused_lacking(h), "truck: the host can't start a job the client lacks"),
    ]
    for ok, what in checks:
        print(f"{'PASS' if ok else 'FAIL'}  {what}")
    ok = all(x for x, _ in checks)
    print(f"RESULT: {'PASS' if ok else 'FAIL'} ({sum(1 for x, _ in checks if x)} of {len(checks)} checks)")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main(*sys.argv[1:5]))

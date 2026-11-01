"""Checks the lamps suite (tools/run-pair.ps1 -Test lamps) from both bots' logs: switching one player's lamp doesn't
switch the other player's own lamp, a lamp switched by the other player follows on its owner's machine, and with four
UV lamps on the map each machine's own UV lamps light blood."""
import re
import sys


def lamps(log, label):
    return re.findall(r"LAMPS " + label + r" (own|other) (uv|plain) on (\w+) active (\w+) slot (-?\d+) lit (\w+)", log)


def main(_h, _c, host_log, client_log):
    try:
        h = open(host_log, encoding="utf-8", errors="replace").read()
        c = open(client_log, encoding="utf-8", errors="replace").read()
    except FileNotFoundError as e:
        print(f"FAIL  missing log: {e.filename}\nRESULT: FAIL")
        return 1
    if "LAMPS dump done" not in h or "LAMPS dump done" not in c:
        print("FAIL  dumps missing\nRESULT: FAIL")
        return 1
    ph, pc = h.count("LAMPS placed"), c.count("LAMPS placed")
    checks = [(ph == 3 and pc == 3, f"each player put down a lamp and two UV lamps (host {ph}, client {pc})")]
    hon, con = lamps(h, "on"), lamps(c, "on")
    hf, cf = lamps(h, "final"), lamps(c, "final")

    def plain(rows, who):
        return [r[2] for r in rows if r[0] == who and r[1] == "plain"]
    checks.append((plain(con, "own") == ["False"] and plain(hon, "other") == ["False"],
                   f"the client's own lamp stays off on both after the host switched its own on (client {plain(con, 'own')}, host's copy {plain(hon, 'other')})"))
    checks.append((plain(hon, "own") == ["True"] and plain(con, "other") == ["True"],
                   f"the host's lamp is on on both (host {plain(hon, 'own')}, client's copy {plain(con, 'other')})"))
    checks.append(("LAMPS client switched the host's lamp: on False" in c and plain(hf, "own") == ["False"] and plain(cf, "other") == ["False"],
                   f"the client switched the host's lamp off, and it is off on the host too (host {plain(hf, 'own')}, client's copy {plain(cf, 'other')})"))
    # The game lights at most 3 UV lamps at a time (slots 1..3 of CleaningSenseController): each machine's own lamps
    # that are on come first, the other player's get what is left.
    for name, rows in (("host", hon), ("client", con)):
        uv = [r for r in rows if r[1] == "uv"]
        on = [r for r in uv if r[2] == "True"]
        checks.append((len(uv) == 4 and len(on) == 4, f"{name}: four UV lamps on the map, all on ({len(uv)}: {[r[2] for r in uv]})"))
        own = [r for r in on if r[0] == "own"]
        checks.append((len(own) == 2 and all(r[5] == "True" for r in own),
                       f"{name}: its own UV lamps that are on light blood (slot, lit: {[(r[4], r[5]) for r in own]})"))
        lit = [r for r in on if r[5] == "True"]
        checks.append((len(lit) == min(3, len(on)), f"{name}: as many lamps lit as the game can light ({len(lit)} of {len(on)} on; the other's: {[(r[4], r[5]) for r in on if r[0] == 'other']})"))
    for ok, what in checks:
        print(f"{'PASS' if ok else 'FAIL'}  {what}")
    ok = all(x for x, _ in checks)
    print(f"RESULT: {'PASS' if ok else 'FAIL'} ({sum(1 for x, _ in checks if x)} of {len(checks)} checks)")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main(*sys.argv[1:5]))

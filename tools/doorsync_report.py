"""Checks the doorsync suite (tools/run-pair.ps1 -Test doorsync): doors clicked by both players (Toggle) end the same
way on both machines."""
import re
import sys


def main(_h, _c, host_log, client_log):
    try:
        h = open(host_log, encoding="utf-8", errors="replace").read()
        c = open(client_log, encoding="utf-8", errors="replace").read()
    except FileNotFoundError as e:
        print(f"FAIL  missing log: {e.filename}\nRESULT: FAIL")
        return 1
    if "DOORSYNC dump done" not in h or "DOORSYNC dump done" not in c:
        print("FAIL  dumps missing\nRESULT: FAIL")
        return 1
    hr = re.findall(r"DOORSYNC roles (\S*) of", h)
    hs = re.findall(r"DOORSYNC final doors (\S+)", h)
    cs = re.findall(r"DOORSYNC final doors (\S+)", c)
    hs, cs = (hs[-1] if hs else ""), (cs[-1] if cs else "")
    clicks = len(re.findall(r"DOORSYNC \w+ clicked door", h + c))
    changed = len(re.findall(r"clicked door \d+: closed (\w+) -> (?!\1)", h + c))
    checks = [(hr and hr[-1] and len(hr[-1].split(",")) == 3, f"three free doors to click ({hr and hr[-1]})")]
    checks.append((clicks == 6 and changed == 6, f"each of the 6 clicks moved its door ({changed} of {clicks})"))
    roles = [int(x) for x in hr[-1].split(",")] if hr and hr[-1] else []
    diff = [i for i in range(min(len(hs), len(cs))) if hs[i] != cs[i]]
    checks.append((len(hs) == len(cs) and len(hs) > 0 and not diff, f"every door is the same way on both ({len(diff)} differ{': ' + ','.join(map(str, diff[:10])) if diff else ''})"))
    start = re.findall(r"DOORSYNC roles \S* of \d+; doors (\S+)", h)
    start = start[-1] if start else ""
    got = {i: (start[i] if i < len(start) else "?", hs[i] if i < len(hs) else "?", cs[i] if i < len(cs) else "?") for i in roles}
    checks.append((len(roles) == 3 and all(a == b == c for a, b, c in got.values()), f"each clicked door ended as it started, two actions each (start/host/client {got})"))
    for ok, what in checks:
        print(f"{'PASS' if ok else 'FAIL'}  {what}")
    ok = all(x for x, _ in checks)
    print(f"RESULT: {'PASS' if ok else 'FAIL'} ({sum(1 for x, _ in checks if x)} of {len(checks)} checks)")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main(*sys.argv[1:5]))

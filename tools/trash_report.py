"""Checks trash, bags and tools from both bots' logs (tools/run-pair.ps1 -Test trash)."""
import re
import sys


def bag_pos(log, label):
    m = re.search(rf"TRASHTEST {label} bag '[^']*' at (-?[\d,.]+),(-?[\d,.]+),(-?[\d,.]+),(-?[\d,.]+),(-?[\d,.]+),(-?[\d,.]+)", log)
    if not m:
        return None
    g = m.groups()
    # Russian locale prints "6,55" with comma decimals, joined by commas: pairs are x, y, z
    return tuple(float(f"{g[i]}.{g[i + 1]}") for i in (0, 2, 4))


def dist(a, b):
    return sum((x - y) ** 2 for x, y in zip(a, b)) ** 0.5


def main(_h, _c, host_log, client_log):
    try:
        h = open(host_log, encoding="utf-8", errors="replace").read()
        c = open(client_log, encoding="utf-8", errors="replace").read()
    except FileNotFoundError as e:
        print(f"FAIL  missing log: {e.filename}\nRESULT: FAIL")
        return 1
    checks = []
    checks.append(("TRASHTEST client collected trash" in c and re.search(r"TRASHTEST final trash \d+: collected True, active False", h) is not None,
                   "trash: picked up by the client's can, collected and gone on the host"))
    he, ce = bag_pos(h, "early"), bag_pos(c, "early")
    checks.append((he is not None and ce is not None and dist(he, ce) < 0.2, f"bag: the host's new bag shows on the client at the same spot ({he} / {ce})"))
    checks.append(("COOP took over bag" in c and "took over bag" in h, "bag: the client took it over, the host knows"))
    hc, cc = bag_pos(h, "carry"), bag_pos(c, "carry")
    checks.append((hc is not None and cc is not None and dist(hc, cc) < 0.5 and he is not None and dist(cc, he) > 0.5,
                   f"bag: carried by the client, the host's copy follows ({cc} / {hc})"))
    hr, cr = bag_pos(h, "rest"), bag_pos(c, "rest")
    checks.append((hr is not None and cr is not None and dist(hr, cr) < 0.1, f"bag: at rest at the same spot ({cr} / {hr})"))
    checks.append(("client put the bag in the van" in c and re.search(r"host bag (inactive|gone) collected True", h) is not None,
                   "bag: into the van by the client, collected on the host too"))
    if "host placed its tool" in h:
        m = re.search(r"host placed its tool '([^']+)'", h)
        name = m.group(1).replace("(Clone)", "").strip() if m else "?"
        checks.append((f"showing another player's Tool '{name}'" in c, f"tool: the host's placed {name} shows on the client"))
        hw = re.findall(r"TRASHTEST \w+ tool '[^']+' at \S+ water ([\d,.]+) dirt ([\d,.]+)", h)
        cw = re.findall(r"TRASHTEST \w+ tool '[^']+\(other player\)' at \S+ water ([\d,.]+) dirt ([\d,.]+)", c)
        if hw:
            # Same as the host's own bucket, and not the empty default (the game may reset the water it was given).
            checks.append((bool(cw) and cw[-1] == hw[-1] and hw[-1] != ("0,00", "0,00") and hw[-1] != ("0.00", "0.00"),
                           f"bucket: the host's water/dirt shows on the client's copy (host {hw[-1]}, client {cw[-1] if cw else None})"))
    for ok, what in checks:
        print(f"{'PASS' if ok else 'FAIL'}  {what}")
    ok = all(x for x, _ in checks)
    print(f"RESULT: {'PASS' if ok else 'FAIL'} ({sum(1 for x, _ in checks if x)} of {len(checks)} checks)")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main(*sys.argv[1:5]))

"""Compares the stain tables the bots dumped (tools/run-pair.ps1 -Test stains).

Rows: label,index,intensity,active,destroyed,hits,last_hit_strength,last_hit_effectiveness.
Roles (logged by the bot as "STAINTEST roles A a B b C c D d"):
  A client painted 30 strokes (strength 0.77, effectiveness 0.33) and set intensity 0.4
  B host set 0.6 (must reach the client), then the client set 0.9 (must not spread)
  C destroyed by the host, D destroyed by the client
"""
import csv
import re
import sys


def load(path):
    rows = {}
    with open(path, newline="") as f:
        for r in csv.reader(f):
            rows.setdefault(r[0], {})[int(r[1])] = (float(r[2]), int(r[3]), int(r[4]), int(r[5]), float(r[6]), float(r[7]))
    return rows


def main(host_csv, client_csv, host_log):
    checks = []
    try:
        h, c = load(host_csv), load(client_csv)
        log = open(host_log, encoding="utf-8", errors="replace").read()
    except FileNotFoundError as e:
        print(f"FAIL  missing file: {e.filename}\nRESULT: FAIL")
        return 1
    m = re.search(r"STAINTEST roles A (\d+) B (\d+) C (\d+) D (\d+)", log)
    if not m or "final" not in h or "final" not in c or "start" not in h:
        print("FAIL  roles or dumps missing\nRESULT: FAIL")
        return 1
    A, B, C, D = map(int, m.groups())
    hf, cf, hs = h["final"], c["final"], h["start"]

    checks.append((abs(hf[A][0] - 0.4) < 0.002 and abs(cf[A][0] - 0.4) < 0.002, f"A: client's intensity 0.4 on both (host {hf[A][0]:.4f}, client {cf[A][0]:.4f})"))
    added = hf[A][3] - hs[A][3]
    checks.append((added == 30, f"A: host's decal got the client's 30 strokes ({added})"))
    checks.append((abs(hf[A][4] - 0.77) < 1e-3 and abs(hf[A][5] - 0.33) < 1e-3, f"A: stroke arguments arrived unchanged (strength {hf[A][4]}, effectiveness {hf[A][5]})"))
    # The client's later local raise to 0.9 stays local by design: only lower values spread (a raise is
    # re-dirtying, which gets its own event later), so the host must still have 0.6.
    cm = c.get("mid", {})
    checks.append((B in cm and abs(cm[B][0] - 0.6) < 0.002, f"B: host's 0.6 reached the client (client before its raise: {cm.get(B, (None,))[0]})"))
    checks.append((abs(hf[B][0] - 0.6) < 0.002, f"B: host keeps 0.6, the client's raise to 0.9 didn't spread (host {hf[B][0]:.4f}, client {cf[B][0]:.4f})"))
    for name, i, who in (("C", C, "host"), ("D", D, "client")):
        checks.append((hf[i][2] == 1 and cf[i][2] == 1, f"{name}: destroyed by the {who}, gone on both (host {hf[i][2]}, client {cf[i][2]})"))
    others = [i for i in hf if i not in (A, B, C, D)]
    diff = [i for i in others if abs(hf[i][0] - cf[i][0]) > 0.002 or hf[i][2] != cf[i][2]]
    checks.append((len(hf) == len(cf) and not diff, f"others: {len(others) - len(diff)} of {len(others)} stains agree"))
    for i in diff[:5]:
        print(f"   stain {i}: host {hf[i]} client {cf[i]}")

    try:
        clog = open(sys.argv[4], encoding="utf-8", errors="replace").read()
    except (IndexError, FileNotFoundError):
        clog = ""
    # The blood objective's percent must not come from the other machine: a host's percent made the client's
    # game wipe the rest of its blood (owner's test on Dealer's Apartment).
    crossed = re.findall(r"COOP objective '[^']+' \d+/\d+ from another player|COOP objective variable [^=]+= -?\d[\d.]* from another player", log + clog)
    checks.append((not crossed, f"objectives: no count, percent or numeric value crossed between the machines ({len(crossed)} did)"))
    made = re.search(r"STAINTEST client made a stain on '[^']*': \(([-\d.,\s]+)\)", clog)
    def news(text, label):
        return {m.group(1): (m.group(2), float(m.group(3).replace(",", ".")), int(m.group(4)))
                for m in re.finditer(rf"STAINTEST {label} new stain (\w+) at \(([^)]*)\) intensity ([\d,.]+) hits (\d+)", text)}
    hn, cn = news(log, "final"), news(clog, "final")
    checks.append((made is not None, "new stain: the client made one"))
    common = set(hn) & set(cn)
    checks.append((len(common) == 1, f"new stain: the host has the client's stain ({len(hn)} on host, {len(cn)} on client)"))
    if common:
        sid = common.pop()
        checks.append((hn[sid][0] == cn[sid][0], f"new stain: same spot on both ({hn[sid][0]} / {cn[sid][0]})"))
        checks.append((abs(hn[sid][1] - 0.3) < 0.002 and abs(cn[sid][1] - 0.3) < 0.002, f"new stain: the host cleaned it to 0.3, on both ({hn[sid][1]} / {cn[sid][1]})"))
        checks.append((cn[sid][2] >= 20, f"new stain: the host's 20 strokes reached the client's copy ({cn[sid][2]} hits)"))
    for ok, what in checks:
        print(f"{'PASS' if ok else 'FAIL'}  {what}")
    ok = all(x for x, _ in checks)
    print(f"RESULT: {'PASS' if ok else 'FAIL'} ({sum(x for x, _ in checks)} of {len(checks)} checks)")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main(*sys.argv[1:4]))

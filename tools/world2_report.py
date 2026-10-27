"""Checks the world2 suite (tools/run-pair.ps1 -Test world2) from both bots' logs: drawers, board parts,
valuables stolen the real way, a mop hit taking an item over, the lift, furniture back in place, shards."""
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
    roles = re.search(r"WORLD2 roles O (-?\d+) P (-?\d+) V (-?\d+) J (-?\d+) L (-?\d+) H (-?\d+) F (-?\d+) B (-?\d+)", h)
    if not roles or "WORLD2 dump done" not in h or "WORLD2 dump done" not in c:
        print("FAIL  roles or dumps missing\nRESULT: FAIL")
        return 1
    O, P, V, J, L, H, F, B = map(int, roles.groups())
    checks = []

    def both(pattern, expect, what):
        hv, cv = last(h, pattern), last(c, pattern)
        checks.append((hv == expect and cv == expect, f"{what} (host {hv}, client {cv})"))

    if O >= 0: both(rf"WORLD2 openable {O} open (\w+)", "True", f"drawer/cabinet {O}: opened by the client, open on both")
    if P >= 0: both(rf"WORLD2 part {P} cracked (\w+)", "True", f"board part {P}: broken by the client, broken on both")
    if V >= 0:
        both(rf"WORLD2 valuable {V} stolen (\w+)", "True", f"valuable {V}: stolen by the client (OnSteal), stolen on both")
        both(rf"WORLD2 valuable {V} stolen \w+ listed (\w+)", "True", f"valuable {V}: on both reports")
    if J >= 0:
        took = re.search(rf"COOP item {J}: owner 1 epoch", h) is not None and "client hit item" in c
        checks.append((took, f"item {J}: the client's mop hit took it over (host saw owner 1)"))
    if L >= 0:
        both(rf"WORLD2 lift {L} up (\w+)", last(h, rf"WORLD2 lift {L} up (\w+)"), f"lift {L}: same position state")
        hy, cy = last(h, rf"WORLD2 lift {L} up \w+ platform y ([\d,.-]+)"), last(c, rf"WORLD2 lift {L} up \w+ platform y ([\d,.-]+)")
        same = hy is not None and cy is not None and abs(float(hy.replace(",", ".")) - float(cy.replace(",", "."))) < 0.05
        checks.append((same, f"lift {L}: the platform at the same height, within 5 cm (host {hy}, client {cy})"))
    if H >= 0: both(rf"WORLD2 hologram {H} correct (\w+)", "True", f"furniture {F} on spot {H}: put back by the host, counted on both")
    if B >= 0:
        hs = last(h, r"WORLD2 shards (\d+): shard0 at ([\d,./-]+), shard1 active (\w+)")
        cs = last(c, r"WORLD2 shards (\d+): shard0 at ([\d,./-]+), shard1 active (\w+)")
        checks.append((hs is not None and cs is not None and hs[0] == cs[0], f"shards: same number on both ({hs and hs[0]} / {cs and cs[0]})"))
        if hs and cs:
            checks.append((hs[1] == cs[1], f"shards: the host's moved shard 0 at the same spot ({hs[1]} / {cs[1]})"))
            checks.append((hs[2] == "False" and cs[2] == "False", f"shards: shard 1 collected by the host, gone on both ({hs[2]} / {cs[2]})"))

    hx, cx = last(h, r"WORLD2 fixable pose (\S+) before"), last(c, r"WORLD2 fixable pose (\S+) before")
    cxb = last(c, r"WORLD2 fixable pose \S+ before (\S+)")
    if hx or cx:
        checks.append((hx is not None and hx == cx and cx != cxb, f"knocked-over furniture: stood up by the client (OnFix), same pose on both (host {hx}, client {cx}, before {cxb})"))

    if "client used story object" in c:
        both(r"WORLD2 story object used (\w+)", "True", "story object: used by the client (InteractablePanel), used on both")
    both(r"WORLD2 story flag (\w+)", "True", "objective variable: the client's story flag (Lua) set on both")
    crossed = re.findall(r"COOP objective '[^']+' \d+/\d+ from another player|COOP objective variable [^=]+= -?\d[\d.]* from another player", h + c)
    checks.append((not crossed, f"objectives: no count, percent or numeric value crossed between the machines ({len(crossed)} did)"))

    # F9 report (the client's): the same id saved on both, the screenshot and the state file on both, the comment in both.
    rm = re.search(r"COOP report (\d{8}-\d{6}) saved to ", c)
    if rm:
        rid = rm.group(1)
        import pathlib
        root = pathlib.Path(__file__).resolve().parent.parent / "local"
        files = {side: (root / f"game-copy{suf}" / "BepInEx" / "coop-reports" / f"{rid}-{side}") for side, suf in (("host", ""), ("client", "-2"))}
        def has(side, ext): return files[side].with_suffix(ext).exists()
        notes = all(has(sd, ".txt") and "тест: стол стоит криво" in files[sd].with_suffix(".txt").read_text(encoding="utf-8") for sd in files)
        checks.append((f"COOP report {rid} asked by" in h and all(has(sd, ".png") for sd in files) and notes,
                       f"F9 report {rid}: screenshot and state on both, with the client's comment"))
    else:
        checks.append((False, "F9 report: the client saved none"))

    extra = re.search(r"WORLD2 roles .* O2 (-?\d+) D (-?\d+) P2 (-?\d+) I '([^']*)'", h)
    if extra:
        O2, D, P2, I = int(extra.group(1)), int(extra.group(2)), int(extra.group(3)), extra.group(4)
        if O2 >= 0: both(rf"WORLD2 openable2 {O2} open (\w+)", "True", f"drawer {O2}: clicked open by the client (Interact), open on both")
        if D >= 0:
            hp, cp = last(h, rf"WORLD2 door {D} pose ([\d,.]+)"), last(c, rf"WORLD2 door {D} pose ([\d,.]+) before")
            cb = last(c, rf"WORLD2 door {D} pose [\d,.]+ before ([\d,.]+)")
            checks.append((hp is not None and hp == cp and cp != cb, f"door {D}: clicked (Toggle) by the client, moved, same pose on both (host {hp}, client {cp}, before {cb})"))
        if V >= 0:
            hv = last(h, rf"WORLD2 valuable {V} visible (\w+)")
            checks.append((hv == "False", f"valuable {V}: gone from the host's world too (visible {hv})"))
        if P2 >= 0: both(rf"WORLD2 part2 {P2} collected (\w+)", "True", f"broken part {P2}: collected by the host's can, gone on both")
        if I:
            checks.append((re.search(r"COOP action Interactable.Interact on Interactable \d+ from another player", h) is not None,
                           f"interactable '{I}': used by the client, replayed on the host"))

    for ok, what in checks:
        print(f"{'PASS' if ok else 'FAIL'}  {what}")
    ok = all(x for x, _ in checks)
    print(f"RESULT: {'PASS' if ok else 'FAIL'} ({sum(1 for x, _ in checks if x)} of {len(checks)} checks)")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main(*sys.argv[1:5]))

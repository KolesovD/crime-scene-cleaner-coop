"""Checks the products suite (tools/run-pair.ps1 -Test products) from both bots' logs: cleaning products in the host's
bucket are seen by the client, a mop rinsed there takes them, a product the client pours in reaches the host, and both
end with the same bucket."""
import re
import sys


def final(log):
    m = re.findall(r"PRODUCTS final bucket water ([\d.,]+) dirt ([\d.,]+) products (\S*) simulated here (\w+)", log)
    return tuple(x.replace(",", ".") for x in m[-1]) if m else None


def main(_h, _c, host_log, client_log):
    try:
        h = open(host_log, encoding="utf-8", errors="replace").read()
        c = open(client_log, encoding="utf-8", errors="replace").read()
    except FileNotFoundError as e:
        print(f"FAIL  missing log: {e.filename}\nRESULT: FAIL")
        return 1
    if "PRODUCTS dump done" not in h or "PRODUCTS dump done" not in c:
        print("FAIL  dumps missing\nRESULT: FAIL")
        return 1
    green = re.findall(r"green \((.*?)\) added (\w+): (\S*)", h)
    seen = re.findall(r"PRODUCTS client sees the host's bucket: water ([\d.,]+) products (\S*)", c)
    mop = re.findall(r"mop products (\S*), bucket dirt", c)
    red = re.findall(r"poured red \((.*?)\) into the host's bucket: (\w+), now (\S*)", c)
    hf, cf = final(h), final(c)
    checks = [(green and green[-1][1] == "True", f"the host poured the green product into its bucket ({green and green[-1]})")]
    hp = green[-1][2] if green else ""
    checks.append((seen and hp and seen[-1][1] == hp, f"the client sees the same products in the host's bucket ({seen and seen[-1][1]} vs {hp})"))
    checks.append((mop and hp and mop[-1] != "" and all(x in mop[-1].split("+") for x in hp.split("+")), f"the client's mop rinsed there took them ({mop and mop[-1]})"))
    checks.append((red and red[-1][1] == "True", f"the client poured the red product in ({red and red[-1][:2]})"))
    checks.append((hf and cf and hf[2] == cf[2] and red and hf[2] == red[-1][2], f"both end with the same products, the red one too (host {hf and hf[2]}, client {cf and cf[2]})"))
    checks.append((hf and cf and abs(float(hf[0]) - float(cf[0])) < 0.02 and abs(float(hf[1]) - float(cf[1])) < 0.02,
                   f"and the same water and dirt (host {hf and hf[:2]}, client {cf and cf[:2]})"))
    for ok, what in checks:
        print(f"{'PASS' if ok else 'FAIL'}  {what}")
    ok = all(x for x, _ in checks)
    print(f"RESULT: {'PASS' if ok else 'FAIL'} ({sum(1 for x, _ in checks if x)} of {len(checks)} checks)")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main(*sys.argv[1:5]))

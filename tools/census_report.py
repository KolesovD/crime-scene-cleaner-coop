"""Coverage of the census (tools/run-census.ps1): every component type found in the jobs, against what the
mod shares. A type counts as looked at when the mod's source names it (tables, patches, whitelist, helpers)
or it derives from such a type. Interface, effects, audio and the player's own things are left out (personal).
What remains with player-facing methods or saved state is the list of candidates to check by hand.

usage: python -I tools/census_report.py [local/census.csv]
"""
import collections
import pathlib
import re
import sys

root = pathlib.Path(__file__).resolve().parent.parent
csv_path = pathlib.Path(sys.argv[1]) if len(sys.argv) > 1 else root / "local" / "census.csv"
decomp = root / "local" / "decomp" / "MainGame"
src = "\n".join(p.read_text(encoding="utf-8") for p in (root / "src" / "CscCoop").glob("*.cs"))
named = set(re.findall(r"\b([A-Z]\w+)\b", src))

PERSONAL = re.compile(r"(UI|View|HUD|Hud|Menu|Tablet|Setting|Option|Button|Popup|PopUp|Cursor|Codex|Mail|Panel$|Slider|"
                      r"Vfx|VFX|Effect|Sound|Audio|Music|Camera|Animator|Animation|Tutorial|Hint|Notification|Map|Skin|"
                      r"Inventory|Equipper|Inspector|Highlighter|Outline|Photo|SkillTree|Perk|Blink|Punch|Climb|"
                      r"FirstPerson|Player|Brightness|Language|Scan|Dart|Save$|Quest|Event$)")
PERSONAL_BASES = {"UIPanel", "UICustomButton", "DropdownOption", "IncrementEvent", "SkillTreeSegment"}
ACTIVE = re.compile(r"^(Interact|AltInteract|Use|Toggle|Switch\w*|Press\w*|Open\w*|Close\w*|Activate\w*|Try\w+|"
                    r"On(Click|Press|Panel|Interact|Use)\w*|Change\w*State|Power\w*|Turn\w+|Start\w*|Stop\w*|Unlock\w*|"
                    r"Lock\w*|Fix\w*|Collect\w*|Break\w*|Destroy|Execute\w*|Pull\w*|Push\w*|Spawn\w*|Fill\w*|Drain\w*|"
                    r"Move\w*|Rotate\w*|Raise\w*|Lower\w*|Call\w*|Summon\w*|Enter\w*|Exit\w*|Fire\w*|Explode\w*)$")

per_type = collections.defaultdict(dict)
for line in csv_path.read_text(encoding="utf-8").splitlines():
    parts = line.rsplit(",", 2)
    if len(parts) == 3:
        per_type[parts[1]][parts[0]] = int(parts[2])


def info(typ):
    f = decomp / f"{typ}.cs"
    if not f.exists():
        return None, set(), False
    text = f.read_text(encoding="utf-8", errors="replace")
    m = re.search(rf"public class {re.escape(typ)} : (\w+)", text)
    methods = set(re.findall(r"public unsafe (?:virtual |override )?\w+ (\w+)\(", text))
    return (m.group(1) if m else None), methods, ("Save" in methods and "Load" in methods)


def handled(typ, depth=0):
    if typ in named:
        return typ
    base = info(typ)[0]
    if base and depth < 8 and base not in ("MonoBehaviour", "Object", "Component"):
        return handled(base, depth + 1)
    return None


rows = []
for typ, scenes in per_type.items():
    if "." in typ:          # Unity, TMPro, PixelCrushers, Wwise: engine and middleware
        continue
    base, methods, saved = info(typ)
    if base is None:
        continue
    act = sorted(m for m in methods if ACTIVE.match(m))
    rows.append((typ, base, sum(scenes.values()), sorted(scenes), handled(typ), saved, act))

personal = [r for r in rows if not r[4] and (PERSONAL.search(r[0]) or r[1] in PERSONAL_BASES)]
cand = [r for r in rows if not r[4] and r not in personal and (r[6] or r[5])]
done = [r for r in rows if r[4]]
scenes_all = sorted({s for v in per_type.values() for s in v})
print(f"{len(scenes_all)} scenes: {', '.join(scenes_all)}")
print(f"{len(rows)} game types: {len(done)} looked at by the mod, {len(personal)} personal/interface, "
      f"{len(cand)} candidates, {len(rows) - len(done) - len(personal) - len(cand)} passive\n")
print("== candidates (type : base  count, scenes, saved, player-facing methods)")
for typ, base, n, sc, _, saved, act in sorted(cand, key=lambda r: (-len(r[3]), r[0])):
    print(f"{typ} : {base}  {n} in {len(sc)} ({', '.join(s[:14] for s in sc[:4])}{'...' if len(sc) > 4 else ''})"
          f"{'  SAVED' if saved else ''}  {' '.join(act)[:160]}")
print("\n== looked at by the mod")
print(", ".join(r[0] if r[0] == r[4] else f"{r[0]}->{r[4]}" for r in sorted(done)))

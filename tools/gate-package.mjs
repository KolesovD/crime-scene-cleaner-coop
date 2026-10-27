// Gate helper: dist/CscCoop-<version>.zip holds the plugin dlls exactly as built in src/CscCoop/bin, the plugin
// reports that version, and the README is there. Usage: node tools/gate-package.mjs <version>
import { fileURLToPath } from "node:url";
import { createHash } from "node:crypto";
import { readFileSync, existsSync } from "node:fs";
import { execFileSync } from "node:child_process";
import { join } from "node:path";
const [version] = process.argv.slice(2);
const root = fileURLToPath(new URL("..", import.meta.url));
const zip = join(root, `dist/CscCoop-${version}.zip`);
const fail = (m) => { console.log(`PACKAGE ${version} FAIL: ${m}`); process.exit(1); };
if (!version) fail("no version given");
if (!existsSync(zip)) fail(`${zip} missing`);
const sha = (b) => createHash("sha256").update(b).digest("hex");
// Python's zipfile reads the entries (no unzip on stock Windows).
const py = `import sys,zipfile,hashlib\nz=zipfile.ZipFile(sys.argv[1])\nfor n in ("BepInEx/plugins/CscCoop/CscCoop.dll","BepInEx/plugins/CscCoop/CoopCore.dll","README-RU.txt","BepInEx/core/BepInEx.Core.dll"):\n  print(n, hashlib.sha256(z.read(n)).hexdigest() if n in z.namelist() else "MISSING")`;
const out = execFileSync("python", ["-I", "-c", py, zip], { encoding: "utf8" });
const got = Object.fromEntries(out.trim().split(/\r?\n/).map((l) => l.split(" ")));
for (const [n, h] of Object.entries(got)) if (h === "MISSING") fail(`${n} missing from the zip`);
const bin = join(root, "src/CscCoop/bin/Release/net6.0");
for (const dll of ["CscCoop.dll", "CoopCore.dll"])
  if (got[`BepInEx/plugins/CscCoop/${dll}`] !== sha(readFileSync(join(bin, dll)))) fail(`${dll} in the zip differs from the build`);
const plugin = readFileSync(join(root, "src/CscCoop/Plugin.cs"), "utf8");
if (!plugin.includes(`Version = "${version}"`)) fail(`Plugin.cs is not version ${version}`);
console.log(`PACKAGE ${version} OK`);

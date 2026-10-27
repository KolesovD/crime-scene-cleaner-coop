// Gate helper: a bot suite's saved run is fresh (logs newer than the built plugin, written by the expected
// plugin version) and its report passes. Usage: node tools/gate-suite.mjs <test> [version]
import { fileURLToPath } from "node:url";
import { createHash } from "node:crypto";
import { statSync, readFileSync } from "node:fs";
import { execFileSync } from "node:child_process";
import { join } from "node:path";
const [test, version = "0.3.3"] = process.argv.slice(2);
const root = fileURLToPath(new URL("..", import.meta.url));
const built = join(root, "src/CscCoop/bin/Release/net6.0/CscCoop.dll");
const dll = join(root, "local/game-copy/BepInEx/plugins/CscCoop/CscCoop.dll");
const sha = (f) => createHash("sha256").update(readFileSync(f)).digest("hex");
const host = join(root, `local/last-${test}/host.log`), client = join(root, `local/last-${test}/client.log`);
const fail = (m) => { console.log(`SUITE ${test} FAIL: ${m}`); process.exit(1); };
if (sha(built) !== sha(dll)) fail("the test copy runs a different build than src/CscCoop/bin (deploy it and rerun)");
for (const f of [host, client]) {
  if (statSync(f).mtimeMs < statSync(dll).mtimeMs) fail(`${f} is older than the built plugin`);
  if (!readFileSync(f, "utf8").includes(`Co-op ${version} loaded`)) fail(`${f} not written by ${version}`);
}
const report = test === "items" ? "item_report.py" : `${test}_report.py`;
let out;
try {
  out = execFileSync("python", ["-I", join(root, "tools", report), join(root, `local/${test}-host.csv`), join(root, `local/${test}-client.csv`), host, client], { encoding: "utf8" });
} catch (e) { console.log(e.stdout ?? ""); fail("report exited non-zero"); }
console.log(out);
if (!/RESULT: PASS/.test(out)) fail("report did not pass");
console.log(`SUITE ${test} PASS`);

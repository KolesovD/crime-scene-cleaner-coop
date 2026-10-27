// Gate helper: a bot suite run with CSCCOOP_MUTANT=<mutant> (tools/run-pair.ps1 -Test <test> -Mutant <mutant>,
// logs kept in local/last-<test>-<mutant>) is fresh, was really run with the mutant, and its report passes.
// For table-differ: the client logs the cassettes table made different, and a machine logs it as not shared.
// Usage: node tools/gate-mutant.mjs <test> <mutant> <version>
import { fileURLToPath } from "node:url";
import { createHash } from "node:crypto";
import { statSync, readFileSync } from "node:fs";
import { execFileSync } from "node:child_process";
import { join } from "node:path";
const [test, mutant, version] = process.argv.slice(2);
const root = fileURLToPath(new URL("..", import.meta.url));
const built = join(root, "src/CscCoop/bin/Release/net6.0/CscCoop.dll");
const dll = join(root, "local/game-copy/BepInEx/plugins/CscCoop/CscCoop.dll");
const sha = (f) => createHash("sha256").update(readFileSync(f)).digest("hex");
const dir = join(root, `local/last-${test}-${mutant}`);
const host = join(dir, "host.log"), client = join(dir, "client.log");
const fail = (m) => { console.log(`MUTANT ${test} ${mutant} FAIL: ${m}`); process.exit(1); };
if (sha(built) !== sha(dll)) fail("the test copy runs a different build than src/CscCoop/bin");
for (const f of [host, client]) {
  if (statSync(f).mtimeMs < statSync(dll).mtimeMs) fail(`${f} is older than the built plugin`);
  if (!readFileSync(f, "utf8").includes(`Co-op ${version} loaded`)) fail(`${f} not written by ${version}`);
}
const h = readFileSync(host, "utf8"), c = readFileSync(client, "utf8");
if (mutant === "table-differ") {
  if (!c.includes("COOP mutant table-differ: cassettes made different")) fail("the client did not run the mutant");
  if (!/COOP table 'cassettes' differs from player \d+'s: not shared/.test(h + c)) fail("no machine switched the cassettes table off");
}
const report = test === "items" ? "item_report.py" : `${test}_report.py`;
let out;
try {
  out = execFileSync("python", ["-I", join(root, "tools", report), join(root, `local/${test}-host.csv`), join(root, `local/${test}-client.csv`), host, client], { encoding: "utf8" });
} catch (e) { console.log(e.stdout ?? ""); fail("report exited non-zero"); }
console.log(out);
if (!/RESULT: PASS/.test(out)) fail("report did not pass");
console.log(`MUTANT ${test} ${mutant} PASS`);

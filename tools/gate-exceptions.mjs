// Gate helper: no mod exceptions in the saved suite logs. A line counts when it is a COOP/BOT/FIGURE line
// reporting a failure, or a stack frame in our plugin (CscCoop./CoopCore.). Usage: node tools/gate-exceptions.mjs <file|test>...
import { fileURLToPath } from "node:url";
import { readFileSync, existsSync } from "node:fs";
import { join } from "node:path";
const root = fileURLToPath(new URL("..", import.meta.url));
const bad = /(COOP|BOT|FIGURE|WORLD2|BODYTEST)[^\n]*(failed|Exception)|at (CscCoop|CoopCore)\.|^\s*(CscCoop|CoopCore)\.[\w.]+[ (]/;
let n = 0, files = 0;
for (const a of process.argv.slice(2)) {
  const fs = existsSync(a) ? [a] : [join(root, `local/last-${a}/host.log`), join(root, `local/last-${a}/client.log`)];
  for (const f of fs) {
    files++;
    readFileSync(f, "utf8").split(/\r?\n/).forEach((l, i) => { if (bad.test(l)) { n++; if (n <= 20) console.log(`${f}:${i + 1}: ${l.slice(0, 200)}`); } });
  }
}
console.log(n === 0 ? `NO MOD EXCEPTIONS in ${files} logs` : `${n} mod exception lines`);
process.exit(n === 0 ? 0 : 1);

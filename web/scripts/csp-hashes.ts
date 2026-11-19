import { createHash } from "node:crypto";
import { existsSync, readdirSync, readFileSync, statSync, writeFileSync } from "node:fs";
import { join, relative, sep } from "node:path";

// Writes out/csp.json: for every HTML page of the static export the SHA-256 of each inline script, so the Hive BDC
// web host can send "script-src 'self' 'sha256-...'" for exactly that page instead of allowing inline scripts
// (design 2026-11-19, failure case 1).
const out = join(process.cwd(), "out");
if (!existsSync(join(out, "index.html"))) {
  console.error(`No static export in ${out}; run next build first.`);
  process.exit(1);
}

const inline = /<script(?![^>]*\bsrc=)[^>]*>([\s\S]*?)<\/script>/gi;

export function scriptHashes(html: string): string[] {
  const hashes = new Set<string>();
  for (const match of html.matchAll(inline)) {
    if (!match[1]) continue;
    hashes.add(`'sha256-${createHash("sha256").update(match[1], "utf8").digest("base64")}'`);
  }
  return [...hashes].sort();
}

function pages(dir: string): string[] {
  return readdirSync(dir).flatMap((name) => {
    const path = join(dir, name);
    if (statSync(path).isDirectory()) return name === "_next" ? [] : pages(path);
    return name.endsWith(".html") ? [path] : [];
  });
}

const map: Record<string, string[]> = {};
for (const page of pages(out)) map[relative(out, page).split(sep).join("/")] = scriptHashes(readFileSync(page, "utf8"));
writeFileSync(join(out, "csp.json"), JSON.stringify(map, null, 2));
const counts = Object.values(map).map((h) => h.length);
console.log(`csp.json: ${Object.keys(map).length} pages, ${Math.max(...counts)} inline scripts at most on one page`);

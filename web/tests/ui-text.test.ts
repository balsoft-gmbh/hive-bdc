// npm test: every text a user reads on the console is plain and specific (design version 2, change 2 and V3; Balsoft's
// writing rule "no AI tells"). Checks string literals and JSX text in src/; comments are not checked.
import { test } from "node:test";
import assert from "node:assert/strict";
import { readdirSync, readFileSync, statSync } from "node:fs";
import { join } from "node:path";

const src = join(import.meta.dirname, "..", "src");
const files = (dir: string): string[] =>
  readdirSync(dir).flatMap((name) => {
    const path = join(dir, name);
    return statSync(path).isDirectory() ? files(path) : /\.tsx?$/.test(name) ? [path] : [];
  });

// Phrases that read like a chatbot or a template, whatever the page.
const phrases = [
  /\bwelcome to\b/i,
  /\bon its way\b/i,
  /\beverything can be changed later\b/i,
  /\bseamless(ly)?\b/i,
  /\beffortless(ly)?\b/i,
  /\bwith ease\b/i,
  /\blet['’]s\b/i,
  /\bget started\b/i,
  /\bsimply\b/i,
  /\bjust a few\b/i,
  /\bpowerful\b/i,
  /\bleverage\b/i,
  /\bcomprehensive\b/i,
  /\brobust\b/i,
  /\bstreamline\b/i,
  /\bin just\b/i,
  /\bdon['’]t worry\b/i,
  /\bno worries\b/i,
  /\bit['’]s worth noting\b/i,
  /\bplease don['’]t hesitate\b/i,
  /\bI hope\b/,
  /\bgreat question\b/i,
];

/** The user-facing text of a line: quoted strings and text between JSX tags, without comments. */
function texts(line: string): string[] {
  const code = line.replace(/\/\/.*$/, "");
  const found: string[] = [];
  // In a template string only the text counts, not the ${...} code inside it.
  for (const m of code.matchAll(/"([^"\\]*(?:\\.[^"\\]*)*)"|`([^`]*)`/g)) found.push((m[1] ?? m[2] ?? "").replace(/\$\{[^}]*\}/g, "x"));
  for (const m of code.matchAll(/>([^<>{}]+)</g)) found.push(m[1]);
  return found;
}

test("no console text sounds like a chatbot or a template", () => {
  const hits: string[] = [];
  for (const file of files(src)) {
    readFileSync(file, "utf8")
      .split("\n")
      .forEach((line, i) => {
        if (/^\s*(\/\/|\/?\*)/.test(line)) return;
        for (const t of texts(line)) for (const p of phrases) if (p.test(t)) hits.push(`${file}:${i + 1}: "${t.trim().slice(0, 80)}"`);
      });
  }
  assert.deepEqual(hits, []);
});

test("no console text uses a dash as a pause", () => {
  const hits: string[] = [];
  for (const file of files(src)) {
    readFileSync(file, "utf8")
      .split("\n")
      .forEach((line, i) => {
        if (/[—–]/.test(line.replace(/\/\/.*$/, "")) && !/^\s*(\/\/|\/?\*)/.test(line)) hits.push(`${file}:${i + 1}: em or en dash`);
        for (const t of texts(line)) if (/\S - \S/.test(t)) hits.push(`${file}:${i + 1}: "${t.trim().slice(0, 80)}"`);
      });
  }
  assert.deepEqual(hits, []);
});

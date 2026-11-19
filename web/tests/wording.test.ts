// npm test: the console uses the Windows app's words (design part 19 (e) of the connector design, memory "console
// follows the Windows wording"); no page, badge, breadcrumb or help text names the former "Export" page.
import { test } from "node:test";
import assert from "node:assert/strict";
import { readdirSync, readFileSync, statSync } from "node:fs";
import { join } from "node:path";

const src = join(import.meta.dirname, "..", "src");
const files = (dir: string): string[] =>
  readdirSync(dir).flatMap((name) => {
    const path = join(dir, name);
    return statSync(path).isDirectory() ? files(path) : /\.(tsx?|css)$/.test(name) ? [path] : [];
  });
const old = [/>\s*Export\s*</, /"Export"/, /'EXPORT/, /"EXPORT/, /under Export\b/, /Export page/, /["'`]\/export\/["'`?]/, /"Document types"/,
  /"Where documents come from"/, /"Where they go"/, /"This document type"/, /"Made into"/];

test("no console text names the former Export page", () => {
  const found: string[] = [];
  for (const file of files(src)) {
    readFileSync(file, "utf8")
      .split("\n")
      .forEach((line, i) => {
        const code = line.trim();
        if (code.startsWith("//") || code.startsWith("/*") || code.startsWith("*")) return;
        for (const pattern of old) if (pattern.test(line)) found.push(`${file}:${i + 1}: ${code.slice(0, 120)}`);
      });
  }
  assert.deepEqual(found, []);
});

test("the menu says Outbound from your ERP, as the Windows app", () => {
  const shell = readFileSync(join(src, "components", "shell.tsx"), "utf8");
  assert.match(shell, /title: "Outbound", sub: "from your ERP"/);
});

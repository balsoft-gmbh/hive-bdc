// node --test tests/console/*.test.mjs
// Design part 19 (e): the web console uses the Windows app's words; no page, badge, breadcrumb or help text names an
// "Export" page any more.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';

const folder = join(import.meta.dirname, '..', '..', 'Hosting', 'Console');
const files = readdirSync(folder).filter((f) => f.endsWith('.js') || f.endsWith('.html'));
const old = [/text: 'Export'/, /'EXPORT/, /under Export\b/, /Export page/, /#\/export/, /text: 'Document types'/, /\$\{name\} of company/,
  /text: 'Where documents come from'/, /text: 'Where they go'/, /'This document type'/, /'Made into'/];

test('no console text names the former Export page', () => {
  const found = [];
  for (const file of files) {
    readFileSync(join(folder, file), 'utf8').split('\n').forEach((line, i) => {
      const code = line.trim();
      if (code.startsWith('//') || code.startsWith('/*') || code.startsWith('*')) return;
      for (const pattern of old) if (pattern.test(line)) found.push(`${file}:${i + 1}: ${line.trim().slice(0, 120)}`);
    });
  }
  assert.deepEqual(found, []);
});

test('the menu says Outbound from your ERP, as the Windows app', () => {
  const app = readFileSync(join(folder, 'app.js'), 'utf8');
  assert.match(app, /id: 'outbound', label: 'Outbound', sub: 'from your ERP'/);
});

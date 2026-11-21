// npm test: the technical log types, on the sample lines the Windows app is tested with too (tests/fixtures/log-types.json).
import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { LOG_CHOICES, filterLog, logCount, logTypes, noLinesOfType } from "../src/lib/logtypes";

type Fixture = {
  lines: { text: string; type: string }[];
  choices: { id: string; label: string; lines: number[] }[];
};
const fixture: Fixture = JSON.parse(readFileSync(join(import.meta.dirname, "..", "..", "tests", "fixtures", "log-types.json"), "utf8"));
const texts = fixture.lines.map((l) => l.text);

test("every line has the type of its entry, as in the Windows app", () => {
  assert.deepEqual(logTypes(texts), fixture.lines.map((l) => l.type));
});

test("the choices are the ones the Windows app offers, in the same order", () => {
  assert.deepEqual(
    LOG_CHOICES.map((c) => ({ id: c.id, label: c.label })),
    fixture.choices.map((c) => ({ id: c.id, label: c.label })),
  );
});

test("each choice shows its lines and keeps a stack trace with its error", () => {
  for (const choice of fixture.choices) {
    assert.deepEqual(filterLog(texts, choice.id), choice.lines.map((i) => texts[i]), choice.id);
  }
});

test("an info line that names an error stays info", () => {
  const line = "2026-11-21 08:03:00.000 [INF] Error mail sent to the admin";
  assert.deepEqual(filterLog([line], "problems"), []);
  assert.deepEqual(filterLog([line], "info"), [line]);
});

test("an unknown choice shows everything", () => {
  assert.deepEqual(filterLog(texts, "nonsense"), texts);
});

test("the count and the empty message name the lines read", () => {
  assert.equal(logCount(6, 170), "6 of 170 lines");
  assert.equal(logCount(1, 1), "1 of 1 line");
  assert.equal(noLinesOfType(170), "No lines of this type in the last 170 lines.");
  assert.equal(noLinesOfType(1), "No lines of this type in the last line.");
});
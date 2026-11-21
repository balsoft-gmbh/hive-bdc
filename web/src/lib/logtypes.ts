// Technical log types, the same rules as LogParser in the Windows app (tests/fixtures/log-types.json checks both).
// The type belongs to the entry: lines without a timestamp (a stack trace) take the type of the line above them.

export type LogType = "error" | "warning" | "info" | "debug" | "dashboard";

export const LOG_CHOICES = [
  { id: "all", label: "All types" },
  { id: "problems", label: "Errors and warnings" },
  { id: "error", label: "Errors" },
  { id: "warning", label: "Warnings" },
  { id: "info", label: "Info" },
  { id: "debug", label: "Debug" },
  { id: "dashboard", label: "Dashboard actions" },
] as const;

const startsEntry = /^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d/;

function typeOfEntryLine(line: string): LogType {
  const b = line.indexOf("[");
  const level = b >= 0 && line[b + 4] === "]" ? line.slice(b + 1, b + 4).toUpperCase() : "";
  if (level === "ERR" || level === "FTL") return "error";
  if (level === "WRN") return "warning";
  if (line.includes("UI · ")) return "dashboard";
  return level === "DBG" ? "debug" : "info";
}

export function logTypes(lines: string[]): LogType[] {
  let current: LogType = "info";
  return lines.map((line) => (startsEntry.test(line) ? (current = typeOfEntryLine(line)) : current));
}

function matches(choice: string, type: LogType): boolean {
  if (choice === "problems") return type === "error" || type === "warning";
  return LOG_CHOICES.some((c) => c.id === choice && c.id !== "all") ? choice === type : true;
}

export function filterLog(lines: string[], choice: string): string[] {
  const types = logTypes(lines);
  return lines.filter((_, i) => matches(choice, types[i]));
}

const lines = (n: number) => (n === 1 ? "line" : "lines");

export const logCount = (shown: number, total: number) => `${shown} of ${total} ${lines(total)}`;

export const noLinesOfType = (total: number) =>
  `No lines of this type in the last ${total === 1 ? "line" : `${total} lines`}.`;
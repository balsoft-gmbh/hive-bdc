"use client";
import { useEffect, useRef, useState } from "react";
import { Download } from "lucide-react";
import { api } from "@/lib/api";
import { LOG_CHOICES, filterLog, logCount, noLinesOfType } from "@/lib/logtypes";
import { Notice } from "./notice";
import { ICON, Panel } from "./service-ui";

// The Technical log tab: the last 200 lines of today's log (GET /logs?tail=200), filtered by type as in the Windows app
// (design 2026-11-21), and the whole day as a download that the browser builds from a Blob, so no inline script is
// needed (failure case 1).

type Logs = { lines?: string[]; note?: string; error?: string; file?: string };

// Kept while the console is open, as the former page kept it between visits.
let rememberedType = "all";

export function ServiceLog() {
  const [logs, setLogs] = useState<Logs | null>(null);
  const [error, setError] = useState("");
  const [downloadError, setDownloadError] = useState("");
  const [type, setType] = useState(rememberedType);
  const box = useRef<HTMLPreElement>(null);

  useEffect(() => {
    api<Logs>("/logs?tail=200")
      .then((answer) => setLogs(answer ?? {}))
      .catch((e: Error) => setError(e.message));
  }, []);

  const lines = logs?.lines || [];
  const shown = filterLog(lines, type);
  const text = !lines.length
    ? logs?.note || logs?.error || "Nothing in the log yet."
    : shown.length
      ? shown.join("\n")
      : noLinesOfType(lines.length);

  useEffect(() => {
    if (box.current) box.current.scrollTop = box.current.scrollHeight;
  }, [text, logs]);

  const download = async () => {
    setDownloadError("");
    try {
      const all = await api<Logs>("/logs?tail=5000");
      const href = URL.createObjectURL(new Blob([(all?.lines || []).join("\n")], { type: "text/plain" }));
      const link = document.createElement("a");
      link.href = href;
      link.download = all?.file || "hive-bdc.log";
      link.click();
      setTimeout(() => URL.revokeObjectURL(href), 10000);
    } catch (e) {
      setDownloadError((e as Error).message);
    }
  };

  return (
    <Panel
      id="log"
      title="Technical log"
      actions={
        <>
          {lines.length > 0 && <span className="service-log-count">{logCount(shown.length, lines.length)}</span>}
          <label className="service-log-type">
            Type
            <select
              aria-label="Type"
              value={type}
              onChange={(e) => {
                rememberedType = e.target.value;
                setType(e.target.value);
              }}
            >
              {LOG_CHOICES.map((c) => (
                <option key={c.id} value={c.id}>
                  {c.label}
                </option>
              ))}
            </select>
          </label>
          <button type="button" className="button secondary" onClick={() => void download()}>
            <Download {...ICON} />
            Download today&apos;s log
          </button>
        </>
      }
    >
      {error && <Notice kind="error">{error}</Notice>}
      {downloadError && <Notice kind="error">{downloadError}</Notice>}
      {logs && (
        <pre ref={box} className="service-log" tabIndex={0} role="region" aria-label="Lines of the technical log">
          {text}
        </pre>
      )}
      <p className="service-footnote">
        The last 200 lines of today. The download is the whole day, whatever type is chosen. Azure keeps the full log
        under the app&apos;s Log stream.
      </p>
    </Panel>
  );
}
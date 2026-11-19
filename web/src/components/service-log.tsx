"use client";
import { useEffect, useRef, useState } from "react";
import { Download } from "lucide-react";
import { api } from "@/lib/api";
import { Notice } from "./notice";
import { TogglePill } from "./toggle-pill";
import { ICON, Panel } from "./service-ui";

// The Technical log tab: the last 200 lines of today's log (GET /logs?tail=200), only warnings and errors on request,
// and the whole day as a download that the browser builds from a Blob, so no inline script is needed (failure case 1).

const warning = /\b(WRN|ERR|FTL|WARN|ERROR|FATAL|Warning|Error)\b/;
type Logs = { lines?: string[]; note?: string; error?: string; file?: string };

// Kept while the console is open, as the former page kept it between visits.
let rememberedOnlyWarnings = false;

export function ServiceLog() {
  const [logs, setLogs] = useState<Logs | null>(null);
  const [error, setError] = useState("");
  const [downloadError, setDownloadError] = useState("");
  const [onlyWarnings, setOnlyWarnings] = useState(rememberedOnlyWarnings);
  const box = useRef<HTMLPreElement>(null);

  useEffect(() => {
    api<Logs>("/logs?tail=200")
      .then((answer) => setLogs(answer ?? {}))
      .catch((e: Error) => setError(e.message));
  }, []);

  const lines = logs?.lines || [];
  const shown = onlyWarnings ? lines.filter((l) => warning.test(l)) : lines;
  const text = shown.length ? shown.join("\n") : logs?.note || logs?.error || "Nothing in the log yet.";

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
          <TogglePill
            label="Only warnings and errors"
            checked={onlyWarnings}
            onChange={(checked) => {
              rememberedOnlyWarnings = checked;
              setOnlyWarnings(checked);
            }}
          />
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
      <p className="service-footnote">The last 200 lines of today. Azure keeps the full log under the app&apos;s Log stream.</p>
    </Panel>
  );
}
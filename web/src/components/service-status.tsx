"use client";
import { useState } from "react";
import { Copy, FlaskConical, Pause, Play } from "lucide-react";
import { StatusPill } from "@/components/ui/data-grid";
import { useConsole } from "./console-context";
import { Notice } from "./notice";
import { sourceEntries, typeWords } from "@/lib/logic";
import type { ApiError } from "@/lib/api";
import { Facts, SMALL_ICON, postAction } from "./service-ui";
import { TestRunCard, type TestRunAnswer, type TestRunEntry } from "./test-run-card";

// The Status tab (board W13 of the former Service page): whether the instance takes documents, with Pause, Start and
// a test run, and what it is (version, region, time zone, identity). A Viewer or Operator sees it without the actions.

export type Identity = { objectId?: string; tenantId?: string; region?: string; timeZone?: string; inputRoot?: string; message?: string };

type RunResult = { entry: TestRunEntry; answer: TestRunAnswer } | { kind: "info" | "error"; text: string };

export function ServiceStatus({ identity, identityLoading, identityError }: { identity: Identity | null; identityLoading: boolean; identityError: string }) {
  const { me, config, refresh } = useConsole();
  const settings = config ?? {};
  const canChange = Boolean(me?.canChange);
  const paused = Boolean(settings.paused);
  const entries = sourceEntries(settings).filter((e) => e.company.source.kind === "blob");
  const [message, setMessage] = useState("");
  const [flipping, setFlipping] = useState(false);
  const [choice, setChoice] = useState(0);
  const [running, setRunning] = useState(false);
  const [result, setResult] = useState<RunResult | null>(null);

  const flip = async () => {
    setFlipping(true);
    setMessage("");
    try {
      await postAction(paused ? "/setup/start" : "/setup/pause");
      await refresh();
    } catch (error) {
      setMessage((error as Error).message);
    } finally {
      setFlipping(false);
    }
  };

  const runTest = async () => {
    const entry = entries[Math.min(choice, entries.length - 1)];
    if (!entry) return;
    setRunning(true);
    setResult({ kind: "info", text: "The test run is going. This can take a minute." });
    try {
      const answer = await postAction<TestRunAnswer>("/setup/test-run", { documentType: entry.type.id, company: entry.company.dataAreaId });
      setResult({ entry, answer: answer ?? {} });
    } catch (error) {
      const e = error as ApiError;
      setResult({ kind: "error", text: e.status === 409 ? "A test run is already going." : e.message });
    } finally {
      setRunning(false);
    }
  };

  const copyIdentity = () => {
    navigator.clipboard?.writeText(identity?.objectId || "").catch(() => {});
  };
  const pending = identityLoading ? "…" : "";

  return (
    <>
      {message && <Notice kind="error">{message}</Notice>}
      <section className="panel service-panel" aria-labelledby="taking-heading">
        <div className="section-heading">
          <div>
            <h2 id="taking-heading">Taking documents</h2>
            <p>
              Checks every source every {settings.pollSeconds || 30} seconds. Pause stops taking new documents; nothing is lost, they wait in
              their source until you start again.
            </p>
          </div>
          <StatusPill tone={paused ? "amber" : "green"}>
            {paused ? (settings.setupOpen ? "Paused until the set-up is finished" : "Paused") : "Running"}
          </StatusPill>
        </div>
        <div className="service-body">
          {canChange && (
            <div className="service-actions">
              <button type="button" className={`button ${paused ? "primary" : "secondary"}`} disabled={flipping} onClick={() => void flip()}>
                {paused ? <Play {...SMALL_ICON} /> : <Pause {...SMALL_ICON} />}
                {paused ? "Start" : "Pause"}
              </button>
            </div>
          )}
          {canChange && (
            <div className="service-inline">
              <span className="service-label">Test run of the first waiting export of</span>
              <select
                aria-label="Company for the test run"
                value={String(Math.min(choice, Math.max(entries.length - 1, 0)))}
                onChange={(e) => setChoice(Number(e.target.value))}
              >
                {entries.map((e, i) => (
                  <option key={`${e.type.id}-${e.company.dataAreaId}`} value={String(i)}>
                    {`${typeWords(e.type, undefined)} · company ${e.company.dataAreaId}`}
                  </option>
                ))}
              </select>
              <button type="button" className="button secondary" disabled={entries.length === 0 || running} onClick={() => void runTest()}>
                <FlaskConical {...SMALL_ICON} />
                Run the test
              </button>
            </div>
          )}
          {entries.length === 0 && <p className="muted">A test run takes its export from a Blob container; no company reads one yet.</p>}
          {result && ("entry" in result ? <TestRunCard entry={result.entry} answer={result.answer} /> : <Notice kind={result.kind}>{result.text}</Notice>)}
        </div>
      </section>
      <section className="panel service-panel" aria-labelledby="identity-heading">
        <div className="section-heading">
          <div>
            <h2 id="identity-heading">Version and identity</h2>
          </div>
        </div>
        <div className="service-body">
          {identityError && <Notice kind="error">{identityError}</Notice>}
          <Facts
            columns
            items={[
              { label: "Version", value: me?.version },
              { label: "Region", value: identity?.region || pending },
              { label: "Time zone", value: identity?.timeZone ? `${identity.timeZone} (from the installation)` : pending },
              {
                label: "Identity for role commands",
                value: identity?.objectId || pending,
                extra: identity?.objectId ? (
                  <button type="button" className="button secondary" onClick={copyIdentity}>
                    <Copy {...SMALL_ICON} />
                    Copy
                  </button>
                ) : null,
              },
            ]}
          />
        </div>
      </section>
    </>
  );
}
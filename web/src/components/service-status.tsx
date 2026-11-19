"use client";
import { useState } from "react";
import { FlaskConical, LoaderCircle, Pause, Play } from "lucide-react";
import { StatusPill } from "@/components/ui/data-grid";
import { useConsole } from "./console-context";
import { Notice } from "./notice";
import { sourceEntries, typeWords } from "@/lib/logic";

const capitalised = (text: string) => text.charAt(0).toUpperCase() + text.slice(1);
import type { ApiError } from "@/lib/api";
import { CopyButton, Facts, ICON, Panel, postAction } from "./service-ui";
import { TestRunCard, type TestRunAnswer, type TestRunEntry } from "./test-run-card";

// The Status tab (board W13 of the former Service page): whether the instance takes documents, with Pause or Start and
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

  const pending = identityLoading ? "…" : "";
  const state = paused ? (settings.setupOpen ? "Paused until the set-up is finished" : "Paused") : "Running";

  const testRun = canChange ? (
    entries.length ? (
      <span className="service-test-run">
        <select
          aria-label="Company for the test run"
          value={String(Math.min(choice, entries.length - 1))}
          onChange={(e) => setChoice(Number(e.target.value))}
        >
          {entries.map((e, i) => (
            <option key={`${e.type.id}-${e.company.dataAreaId}`} value={String(i)}>
              {`${capitalised(typeWords(e.type, undefined))} · Company ${e.company.dataAreaId}`}
            </option>
          ))}
        </select>
        <button type="button" className="button secondary" disabled={running} onClick={() => void runTest()}>
          {running ? <LoaderCircle className="spin" {...ICON} /> : <FlaskConical {...ICON} />}
          Run the test
        </button>
      </span>
    ) : (
      <span className="service-test-run">
        <span className="muted">A test run takes its export from a Blob container; no company reads one yet.</span>
        <button type="button" className="button secondary" disabled>
          <FlaskConical {...ICON} />
          Run the test
        </button>
      </span>
    )
  ) : null;

  return (
    <>
      {message && <Notice kind="error">{message}</Notice>}
      <Panel
        id="taking"
        title="Taking documents"
        pill={<StatusPill tone={paused ? "amber" : "green"}>{state}</StatusPill>}
        actions={
          canChange && (
            <button
              type="button"
              className="button secondary"
              disabled={flipping}
              title={paused ? "Takes the documents that wait in their source" : "New documents wait in their source until you start again"}
              onClick={() => void flip()}
            >
              {paused ? <Play {...ICON} /> : <Pause {...ICON} />}
              {paused ? "Start" : "Pause"}
            </button>
          )
        }
      >
        <Facts
          items={[
            { label: "Checks every source", value: `Every ${settings.pollSeconds || 30} seconds` },
            ...(testRun ? [{ label: "Test run of the first waiting export", value: testRun, span: 3 as const }] : []),
          ]}
        />
        {result && ("entry" in result ? <TestRunCard entry={result.entry} answer={result.answer} /> : <Notice kind={result.kind}>{result.text}</Notice>)}
      </Panel>
      <Panel id="identity" title="Version and identity">
        {identityError && <Notice kind="error">{identityError}</Notice>}
        <Facts
          items={[
            { label: "Version", value: me?.version },
            { label: "Region", value: identity?.region || pending },
            { label: "Time zone", value: identity?.timeZone || pending },
            {
              label: "Managed identity",
              value: identity?.objectId || pending,
              span: 2,
              extra: identity?.objectId ? <CopyButton text={identity.objectId} label="Copy the managed identity" /> : null,
            },
          ]}
        />
      </Panel>
    </>
  );
}
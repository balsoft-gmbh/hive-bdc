"use client";
/* eslint-disable @typescript-eslint/no-explicit-any */
import { useRouter } from "next/navigation";
import { useState } from "react";
import { Dropdown, Option } from "@fluentui/react-components";
import { LoaderCircle, Play } from "lucide-react";
import { api, type ApiError } from "@/lib/api";
import { sourceEntries } from "@/lib/logic";
import { entryName, sendLine } from "@/app/setup/steps";
import { useConsole } from "./console-context";
import { Notice } from "./notice";
import { Facts, Hint, ICON, Panel, StepActions, StepFrame, type Progress } from "./setup-parts";
import { TestRunCard } from "./test-run-card";

type Entry = { type: any; company: any };

const nameOf = (entry: Entry) => entryName(entry.type, entry.company.dataAreaId);

/**
 * Step 4: a test run for the Blob sources (the service runs one for Blob only, TestRuns.cs); for Service Bus and a web
 * address the step names what to send where (design version 3, decision 3, variant A). Start opens the overview.
 */
export function TestRunStep({ progress, readOnly }: { progress: Progress; readOnly: boolean }) {
  const { config, refresh } = useConsole();
  const router = useRouter();
  const all: Entry[] = sourceEntries(config);
  const entries = all.filter((e) => e.company.source.kind === "blob");
  const others = all.filter((e) => e.company.source.kind !== "blob");
  const [choice, setChoice] = useState("0");
  const [running, setRunning] = useState(false);
  const [result, setResult] = useState<{ entry: Entry; answer: any } | null>(null);
  const [runError, setRunError] = useState("");
  const [ran, setRan] = useState(false);
  const [starting, setStarting] = useState(false);
  const [startError, setStartError] = useState("");

  const run = async () => {
    const entry = entries[Number(choice)];
    if (!entry) return;
    setRunning(true);
    setRunError("");
    setResult(null);
    try {
      const answer = await api("/setup/test-run", {
        method: "POST",
        body: JSON.stringify({ documentType: entry.type.id, company: entry.company.dataAreaId }),
      });
      setResult({ entry, answer });
      setRan(true);
    } catch (error) {
      const e = error as ApiError;
      setRunError(e.status === 409 ? "A test run is already going." : e.message);
    } finally {
      setRunning(false);
    }
  };

  const start = async () => {
    setStarting(true);
    setStartError("");
    try {
      await api("/setup/start", { method: "POST", body: "{}" });
      await refresh();
      router.replace("/");
    } catch (error) {
      setStartError((error as Error).message);
      setStarting(false);
    }
  };

  const selected = entries[Number(choice)];
  const stopped = Boolean(result && !result.answer?.ok);
  const origin = window.location.origin;

  const next = readOnly ? undefined : (
    <button type="button" className="button primary" disabled={starting} onClick={() => void start()}>
      {starting && <LoaderCircle className="spin" {...ICON} />}
      Start Hive BDC
    </button>
  );

  return (
    <StepFrame step={4} progress={progress} readOnly={readOnly}>
      {entries.length > 0 && (
        <Panel id="run-heading" title="Test run" line="Runs the first waiting export through every step, for real. The others wait for Start.">
          <div className="setup-run">
            <div className="field">
              <label id="run-of-label" htmlFor="run-of">
                Document type and company
              </label>
              <Dropdown
                id="run-of"
                className="record-select"
                aria-labelledby="run-of-label"
                disabled={readOnly || running}
                value={selected ? nameOf(selected) : ""}
                selectedOptions={[choice]}
                onOptionSelect={(_, data) => setChoice(data.optionValue ?? "0")}
              >
                {entries.map((entry, i) => (
                  <Option key={`${entry.type.id}-${entry.company.dataAreaId}`} value={String(i)} text={nameOf(entry)}>
                    {nameOf(entry)}
                  </Option>
                ))}
              </Dropdown>
            </div>
            {!readOnly && (
              <button type="button" className="button secondary" disabled={running || !selected} onClick={() => void run()}>
                {running ? <LoaderCircle className="spin" {...ICON} /> : <Play {...ICON} />}
                {ran ? "Run the next export" : "Run the test"}
              </button>
            )}
          </div>
          {running && <Hint>Running the test. This can take a minute.</Hint>}
        </Panel>
      )}
      {runError && <Notice kind="error">{runError}</Notice>}
      {result && <TestRunCard entry={result.entry} answer={result.answer} />}
      {stopped && <p className="setup-note">Nothing was moved. Fix the cause in an earlier step and run the test again.</p>}
      {others.length > 0 && (
        <Panel id="send-heading" title="First document" line="Start Hive BDC, then send one document. It shows in the document history.">
          <Facts
            items={others.map((entry) => {
              const line = sendLine(entry, config, origin);
              return {
                label: nameOf(entry),
                value: line.address ? (
                  <>
                    <code>{line.address}</code>
                    <small className="setup-hint setup-fact-note">{line.text}</small>
                  </>
                ) : (
                  line.text
                ),
                copy: line.address && !readOnly ? { text: line.address, label: `Copy the address for ${nameOf(entry)}` } : undefined,
              };
            })}
          />
        </Panel>
      )}
      {startError && <Notice kind="error">{startError}</Notice>}
      <StepActions back={3} next={next} />
    </StepFrame>
  );
}
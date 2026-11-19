"use client";
/* eslint-disable @typescript-eslint/no-explicit-any */
import { useRouter } from "next/navigation";
import { useState } from "react";
import { Dropdown, Option } from "@fluentui/react-components";
import { LoaderCircle, Play, Rocket } from "lucide-react";
import { api, type ApiError } from "@/lib/api";
import { sourceEntries } from "@/lib/logic";
import { typesWord } from "@/app/setup/steps";
import { StatusPill } from "./ui/data-grid";
import { useConsole } from "./console-context";
import { Notice } from "./notice";
import { ICON, StepActions, StepFrame, type Progress } from "./setup-parts";
import { TestRunCard } from "./test-run-card";

type Entry = { type: any; company: any };

function entryName(entry: Entry) {
  return `${typesWord(entry.type)} · company ${entry.company.dataAreaId}`;
}

export function TestRunStep({ progress, readOnly }: { progress: Progress; readOnly: boolean }) {
  const { config, refresh } = useConsole();
  const router = useRouter();
  const entries: Entry[] = sourceEntries(config);
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

  const wentThrough = Boolean(result?.answer?.ok);
  const selected = entries[Number(choice)];

  const next = readOnly ? undefined : (
    <button type="button" className="button primary" disabled={starting} onClick={() => void start()}>
      {starting ? <LoaderCircle className="spin" {...ICON} /> : <Rocket {...ICON} />}
      Start Hive BDC
    </button>
  );

  return (
    <StepFrame
      step={4}
      progress={progress}
      readOnly={readOnly}
      title={wentThrough ? "One document went all the way through" : "Test run"}
      description={
        wentThrough
          ? "Run the next export to try another, or start Hive BDC. The others wait until you start."
          : "Hive BDC takes the first waiting export and runs it through every step, for real. The others wait until you start."
      }
    >
      <section className="panel">
        <div className="setup-panel-body setup-run">
          <div className="field">
            <label id="run-of-label" htmlFor="run-of">
              Test with the first waiting export of
            </label>
            <Dropdown
              id="run-of"
              className="record-select"
              aria-labelledby="run-of-label"
              disabled={readOnly || running}
              value={selected ? entryName(selected) : ""}
              selectedOptions={[choice]}
              onOptionSelect={(_, data) => setChoice(data.optionValue ?? "0")}
            >
              {entries.map((entry, i) => (
                <Option key={`${entry.type.id}-${entry.company.dataAreaId}`} value={String(i)} text={entryName(entry)}>
                  {entryName(entry)}
                </Option>
              ))}
            </Dropdown>
          </div>
          {!readOnly && (
            <button type="button" className={`button ${wentThrough ? "secondary" : "primary"}`} disabled={running || !selected} onClick={() => void run()}>
              {running ? <LoaderCircle className="spin" {...ICON} /> : <Play {...ICON} />}
              {ran ? "Run the next export" : "Run the test"}
            </button>
          )}
        </div>
      </section>
      {running && <Notice kind="info">The test run is going. This can take a minute.</Notice>}
      {runError && <Notice kind="error">{runError}</Notice>}
      {result && <TestRunCard entry={result.entry} answer={result.answer} />}
      <p className="setup-note">
        When a step fails, the list stops there and names the reason, for example a SharePoint column that does not exist. Nothing is moved;
        fix it on the step before and run again.
      </p>
      {startError && <Notice kind="error">{startError}</Notice>}
      <StepActions back={3} next={next} />
    </StepFrame>
  );
}
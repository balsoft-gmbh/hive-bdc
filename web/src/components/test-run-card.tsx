"use client";
import { StatusPill } from "@/components/ui/data-grid";
import { runLine, typeWords } from "@/lib/logic";
import { Mark } from "./service-ui";

// The result of a test run on the Service page and in the set-up assistant (runCard of the former setup.js, board W4): the document, whether it went all the way
// through, and every step with its time. The history stamps are the instance's own clock (logic.ts runLine).

const timeZone = "Europe/Berlin";

const capitalised = (text: string) => text.charAt(0).toUpperCase() + text.slice(1);

export type TestRunEntry = { type: any; company: any };
export type TestRunAnswer = { ok?: boolean; message?: string; documentId?: string; events?: any[] };

export function TestRunCard({ entry, answer }: { entry: TestRunEntry; answer: TestRunAnswer }) {
  const lines = (answer.events || []).map((e) => runLine(e, timeZone));
  const title = `${answer.documentId ? `${answer.documentId} · ` : ""}${capitalised(typeWords(entry.type, undefined))} · Company ${entry.company.dataAreaId}`;
  return (
    <section className="service-run" aria-label="Steps of the test run">
      <div className="service-run-head">
        <h3>{title}</h3>
        <StatusPill tone={answer.ok ? "green" : "amber"}>{answer.ok ? "Done" : "Stopped"}</StatusPill>
      </div>
      {answer.message && <p className={answer.ok ? "ok-text" : "warn-text"}>{answer.message}</p>}
      {lines.length > 0 && (
        <ol className="service-run-steps" aria-label="Steps of the test run">
          {lines.map((l, i) => (
            <li key={i} className={l.ok ? "ok" : l.waiting ? "waiting" : "bad"}>
              <span className="service-run-time">{l.time}</span>
              <Mark state={l.ok ? "done" : l.waiting ? "waiting" : "problem"} />
              <span className="service-run-text">{l.text}</span>
            </li>
          ))}
        </ol>
      )}
    </section>
  );
}
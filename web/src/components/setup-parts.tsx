"use client";
/* eslint-disable @typescript-eslint/no-explicit-any */
import Link from "next/link";
import { useEffect, useRef, useState, type ReactNode } from "react";
import { ArrowLeft, Check, CircleAlert, CircleCheck, Copy } from "lucide-react";
import { saveConfig } from "@/lib/api";
import { SETUP_LINE, SETUP_TITLE, STEP_NAMES, stepHref, stepLabel } from "@/app/setup/steps";
import { ConflictPanel } from "./conflict-panel";
import { Notice } from "./notice";
import { PageHeading } from "./page-heading";

export const ICON = { size: 16, strokeWidth: 1.75 };

export type Progress = { done: boolean[]; open: number };

/** The steps as the CRM tab bar: number and name, a check once done; every step reached is a link. */
export function StepBar({ step, progress }: { step: number; progress: Progress }) {
  return (
    <nav className="setup-steps-nav" aria-label="Set-up steps">
      <ol className="setup-tabs setup-steps">
        {STEP_NAMES.map((name, i) => {
          const n = i + 1;
          const now = n === step;
          const done = progress.done[i] && !now;
          const state = now ? "active" : done ? "done" : n <= progress.open ? "open" : "later";
          const inner = (
            <>
              <span className="setup-step-mark" aria-hidden="true">
                {done ? <Check size={12} strokeWidth={2.5} /> : n}
              </span>
              <span className="setup-step-name" aria-hidden="true">
                {name}
              </span>
              <span className="sr-only">{stepLabel(i, done)}</span>
            </>
          );
          return (
            <li key={n}>
              {!now && n <= progress.open ? (
                <Link className={`setup-tab ${state}`} href={stepHref(n)}>
                  {inner}
                </Link>
              ) : (
                <span className={`setup-tab ${state}`} aria-current={now ? "step" : undefined}>
                  {inner}
                </span>
              )}
            </li>
          );
        })}
      </ol>
    </nav>
  );
}

/** Title, step bar and, for everyone but an Administrator, the note that the steps are read-only. */
export function StepFrame({ step, progress, readOnly, children }: { step: number; progress: Progress; readOnly: boolean; children: ReactNode }) {
  return (
    <div className="setup-flow">
      <PageHeading title={SETUP_TITLE} description={SETUP_LINE} />
      <StepBar step={step} progress={progress} />
      {readOnly && <Notice kind="info">An administrator is setting Hive BDC up. The other pages open once it is started.</Notice>}
      {children}
    </div>
  );
}

export function StepActions({ back, next }: { back?: number; next?: ReactNode }) {
  return (
    <div className="setup-actions">
      {back ? (
        <Link className="button secondary" href={stepHref(back)}>
          <ArrowLeft {...ICON} />
          Back
        </Link>
      ) : (
        <span />
      )}
      {next}
    </div>
  );
}

/** A panel with the ERP section heading: title, an optional line when the user has to act, a state on the right. */
export function Panel({ id, title, line, state, children }: { id: string; title: ReactNode; line?: ReactNode; state?: ReactNode; children: ReactNode }) {
  return (
    <section className="panel setup-panel" aria-labelledby={id}>
      <div className="section-heading">
        <div>
          <h2 id={id}>{title}</h2>
          {line && <p>{line}</p>}
        </div>
        {state}
      </div>
      <div className="setup-panel-body">{children}</div>
    </section>
  );
}

/** The outcome of a check in one line: a green or amber mark and the service's own words. */
export function Result({ ok, children }: { ok: boolean; children: ReactNode }) {
  return (
    <span className={`setup-result ${ok ? "ok" : "warn"}`}>
      {ok ? <CircleCheck {...ICON} aria-hidden="true" /> : <CircleAlert {...ICON} aria-hidden="true" />}
      <span>{children}</span>
    </span>
  );
}

export function Hint({ children, warn }: { children: ReactNode; warn?: boolean }) {
  return <small className={`setup-hint${warn ? " warn" : ""}`}>{children}</small>;
}

export function TextField({
  id,
  label,
  value,
  onChange,
  onBlur,
  hint,
  type = "text",
  placeholder,
  disabled,
}: {
  id: string;
  label: string;
  value: string;
  onChange: (value: string) => void;
  onBlur?: () => void;
  hint?: ReactNode;
  type?: "text" | "url" | "password";
  placeholder?: string;
  disabled?: boolean;
}) {
  return (
    <div className="field">
      <label htmlFor={id}>{label}</label>
      <input
        id={id}
        type={type}
        value={value}
        placeholder={placeholder}
        disabled={disabled}
        autoComplete="off"
        spellCheck={false}
        onChange={(e) => onChange(e.target.value)}
        onBlur={onBlur}
      />
      {hint && <Hint>{hint}</Hint>}
    </div>
  );
}

export function SelectField({
  id,
  label,
  value,
  options,
  onChange,
  disabled,
}: {
  id: string;
  label: string;
  value: string;
  options: { value: string; label: string }[];
  onChange: (value: string) => void;
  disabled?: boolean;
}) {
  return (
    <div className="field">
      <label htmlFor={id}>{label}</label>
      <select id={id} value={value} disabled={disabled} onChange={(e) => onChange(e.target.value)}>
        {options.map((o) => (
          <option key={o.value} value={o.value}>
            {o.label}
          </option>
        ))}
      </select>
    </div>
  );
}

/** Two or three choices as one bordered row of radios, as Access in the Blob panel. */
export function Segments<T extends string>({
  legend,
  name,
  options,
  value,
  onChange,
  disabled,
}: {
  legend: string;
  name: string;
  options: readonly { value: T; label: string }[];
  value: T;
  onChange: (value: T) => void;
  disabled?: boolean;
}) {
  return (
    <fieldset className="setup-access">
      <legend>{legend}</legend>
      <div className="setup-segments">
        {options.map((o) => (
          <label key={o.value} className="setup-segment">
            <input type="radio" name={name} value={o.value} checked={value === o.value} disabled={disabled} onChange={() => onChange(o.value)} />
            {o.label}
          </label>
        ))}
      </div>
    </fieldset>
  );
}

/** The role command a check answered with, to copy into Azure Cloud Shell. */
export function CommandBox({ command }: { command: string }) {
  return (
    <div className="setup-command">
      <div className="setup-code">
        <code>{command}</code>
        <CopyButton text={command} label="Copy the command" />
      </div>
      <Hint>IT runs this once in Azure Cloud Shell.</Hint>
    </div>
  );
}

/**
 * Keeps step 2 on screen while a save makes it done: without a step in the address the assistant opens the step
 * reached, and the check answer or the new keys would go with the step.
 */
export function holdStep(step: number) {
  if (new URLSearchParams(window.location.search).get("step") !== String(step)) window.history.replaceState(null, "", stepHref(step));
}

/** An icon-only copy button: the label is its accessible name and tooltip; a check shows for a moment once copied. */
export function CopyButton({ text, label }: { text: string; label: string }) {
  const [said, setSaid] = useState("");
  const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  useEffect(() => () => clearTimeout(timer.current), []);
  const copy = async () => {
    try {
      await navigator.clipboard.writeText(text);
      setSaid("Copied");
    } catch {
      setSaid("Select the text and copy it");
    }
    clearTimeout(timer.current);
    timer.current = setTimeout(() => setSaid(""), 2000);
  };
  return (
    <>
      <button type="button" className="icon-button small setup-copy" aria-label={label} title={label} onClick={() => void copy()}>
        {said === "Copied" ? <Check {...ICON} /> : <Copy {...ICON} />}
      </button>
      <span className={said && said !== "Copied" ? "setup-hint" : "sr-only"} aria-live="polite">
        {said}
      </span>
    </>
  );
}

/** Read-only values as the ERP record dialog shows them: small uppercase label above the value. */
export function Facts({ items }: { items: { label: string; value: ReactNode; copy?: { text: string; label: string } }[] }) {
  return (
    <dl className="spotlight-facts record-facts setup-facts">
      {items.map((item) => (
        <div key={item.label}>
          <dt>{item.label}</dt>
          <dd>
            <span className="setup-fact-value">{item.value}</span>
            {item.copy && <CopyButton text={item.copy.text} label={item.copy.label} />}
          </dd>
        </div>
      ))}
    </dl>
  );
}

/**
 * Saves the settings with the version they were read at. A refusal shows who saved in between (ConflictPanel) and
 * "Save both" sends the same body with saveBoth; any other error shows as one line.
 */
export function useConfigSave() {
  const [refusal, setRefusal] = useState<{ conflict?: unknown; error?: string; body?: any; after?: () => Promise<void> } | null>(null);
  const [busy, setBusy] = useState(false);
  const save = async (body: any, after: () => Promise<void>) => {
    setBusy(true);
    setRefusal(null);
    try {
      const result = await saveConfig(body);
      if (result.ok) {
        await after();
        return;
      }
      setRefusal(result.conflict ? { conflict: result.conflict, body, after } : { error: result.message });
    } finally {
      setBusy(false);
    }
  };
  const slot = refusal?.conflict ? (
    <ConflictPanel
      refusal={refusal.conflict}
      onSaveBoth={() => void save({ ...refusal.body, saveBoth: true }, refusal.after!)}
      onReload={() => window.location.reload()}
    />
  ) : refusal?.error ? (
    <Notice kind="error">{refusal.error}</Notice>
  ) : null;
  return { save, slot, busy };
}

export function Loading({ text }: { text: string }) {
  return (
    <p className="setup-loading muted" role="status">
      {text}
    </p>
  );
}
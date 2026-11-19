"use client";
/* eslint-disable @typescript-eslint/no-explicit-any */
import Link from "next/link";
import { useState, type ReactNode } from "react";
import { ArrowLeft, Check, Copy } from "lucide-react";
import { saveConfig } from "@/lib/api";
import { STEP_NAMES, stepHref } from "@/app/setup/steps";
import { ConflictPanel } from "./conflict-panel";
import { Notice } from "./notice";
import { PageHeading } from "./page-heading";

export const ICON = { size: 16, strokeWidth: 1.75 };

export type Progress = { done: boolean[]; open: number };

export function StepBar({ step, progress }: { step: number; progress: Progress }) {
  return (
    <nav className="setup-steps-nav" aria-label="Set-up steps">
      <ol className="setup-steps">
        {STEP_NAMES.map((name, i) => {
          const n = i + 1;
          const now = n === step;
          const done = progress.done[i] && !now;
          const inner = (
            <>
              <span className="setup-step-mark" aria-hidden="true">
                {done ? <Check size={16} strokeWidth={2} /> : n}
              </span>
              <span className="setup-step-text">
                <small>{done ? `STEP ${n} · DONE` : `STEP ${n}`}</small>
                <strong>{name}</strong>
              </span>
            </>
          );
          return (
            <li key={n} className={`setup-step ${now ? "current" : done ? "done" : "next"}`} aria-current={now ? "step" : undefined}>
              {!now && n <= progress.open ? <Link href={stepHref(n)}>{inner}</Link> : <span>{inner}</span>}
            </li>
          );
        })}
      </ol>
    </nav>
  );
}

/** Heading, step bar and, for everyone but an Administrator, the note that the steps are read-only. */
export function StepFrame({
  step,
  progress,
  title,
  description,
  readOnly,
  children,
}: {
  step: number;
  progress: Progress;
  title: ReactNode;
  description?: ReactNode;
  readOnly: boolean;
  children: ReactNode;
}) {
  return (
    <div className="setup-flow">
      <PageHeading eyebrow="HIVE BDC SET-UP" title={title} description={description} />
      <StepBar step={step} progress={progress} />
      {readOnly && (
        <Notice kind="info">
          An administrator is setting Hive BDC up. You can follow the steps here, and the other pages open once it is started.
        </Notice>
      )}
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

/** A result in words with a dot: green when it worked, amber when not. Long messages wrap. */
export function Chip({ ok, children }: { ok: boolean; children: ReactNode }) {
  return (
    <span className={`setup-chip ${ok ? "ok" : "warn"}`}>
      <span className="dot" aria-hidden="true" />
      <span>{children}</span>
    </span>
  );
}

export function Hint({ children, warn }: { children: ReactNode; warn?: boolean }) {
  return <small className={`setup-hint${warn ? " warn-text" : ""}`}>{children}</small>;
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

export function CopyButton({ text, label }: { text: string; label: string }) {
  const [said, setSaid] = useState("");
  const copy = async () => {
    try {
      await navigator.clipboard.writeText(text);
      setSaid("Copied.");
    } catch {
      setSaid("Select the text and copy it.");
    }
  };
  return (
    <span className="setup-copy">
      <button type="button" className="button secondary" aria-label={label} title={label} onClick={() => void copy()}>
        <Copy {...ICON} />
        Copy
      </button>
      <span className="muted" aria-live="polite">
        {said}
      </span>
    </span>
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
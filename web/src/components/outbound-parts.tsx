"use client";
import Link from "next/link";
import { Fragment, useEffect, useRef, useState, type ReactNode } from "react";
import { ChevronRight, Copy } from "lucide-react";
import { api } from "@/lib/api";
import { connectionLabel } from "@/lib/exportlogic";
import { useDraft } from "./save-bar";

// The small parts the Outbound and Connections pages share (the former draft.js and ui.js helpers): the OUTBOUND
// badge, switches, segmented choices, the connection dropdown, inputs that commit on change, the Keep or Remove
// question, result chips, copyable code and the path above a page.

export const ICON = { size: 16, strokeWidth: 1.75 };

/** The address of a document type's page: its tab, the company the tab shows and an open destination. */
export const typePath = (id: string, company?: string, tab?: string, dest?: string) =>
  `/outbound/type/?id=${encodeURIComponent(id)}${company ? `&company=${encodeURIComponent(company)}` : ""}${tab ? `&tab=${tab}` : ""}${dest ? `&dest=${encodeURIComponent(dest)}` : ""}`;

export function OutboundBadge() {
  return <span className="badge-outbound">OUTBOUND</span>;
}

/** A switch as in the Windows app: a checkbox with the switch role, named by its label or aria-label. */
export function Switch({
  label,
  checked,
  onChange,
  disabled,
  id,
}: {
  label?: string;
  checked: boolean;
  onChange: (on: boolean) => void;
  disabled?: boolean;
  id?: string;
}) {
  return (
    <input
      id={id}
      type="checkbox"
      role="switch"
      className="bdc-switch"
      aria-label={label}
      checked={checked}
      disabled={disabled}
      onChange={(e) => onChange(e.target.checked)}
    />
  );
}

/** A switch with its word in front: "Active", "On". */
export function SwitchField({ text, ...props }: { text: string; checked: boolean; onChange: (on: boolean) => void; disabled?: boolean }) {
  return (
    <label className="switch-field">
      <span>{text}</span>
      <Switch {...props} />
    </label>
  );
}

/** One choice of a few, as the CRM's segmented buttons. */
export function Segmented<T extends string>({
  label,
  options,
  value,
  onChange,
  disabled,
}: {
  label: string;
  options: [T, string][];
  value: T;
  onChange: (value: T) => void;
  disabled?: boolean;
}) {
  return (
    <div className="segmented" role="group" aria-label={label}>
      {options.map(([v, text]) => (
        <button key={v} type="button" aria-pressed={v === value} disabled={disabled} onClick={() => v !== value && onChange(v)}>
          {text}
        </button>
      ))}
    </div>
  );
}

/** A labelled field in the CRM's form style; the hint sits under the control. */
export function Field({
  label,
  htmlFor,
  hint,
  children,
  wide,
}: {
  label: string;
  htmlFor: string;
  hint?: ReactNode;
  children: ReactNode;
  wide?: boolean;
}) {
  return (
    <div className={`field${wide ? " full" : ""}`}>
      <label htmlFor={htmlFor}>{label}</label>
      {children}
      {hint ? <span className="field-hint">{hint}</span> : null}
    </div>
  );
}

/**
 * A text box that hands its value over on change (leaving it, or Enter), as the former console did; while typing only
 * onLive hears it. A new value from outside (Discard, another screen) replaces what it shows.
 */
export function CommitInput({
  value,
  onCommit,
  onLive,
  ...rest
}: {
  value: string;
  onCommit: (value: string) => void;
  onLive?: (value: string) => void;
} & Omit<React.InputHTMLAttributes<HTMLInputElement>, "value" | "onChange">) {
  const [text, setText] = useState(value);
  const shown = useRef(value);
  useEffect(() => {
    if (value !== shown.current) {
      shown.current = value;
      setText(value);
    }
  }, [value]);
  const commit = () => {
    const clean = text.trim();
    if (clean !== text) setText(clean);
    if (clean !== value) onCommit(clean);
  };
  return (
    <input
      type="text"
      autoComplete="off"
      spellCheck={false}
      {...rest}
      value={text}
      onChange={(e) => {
        setText(e.target.value);
        onLive?.(e.target.value);
      }}
      onBlur={commit}
      onKeyDown={(e) => {
        if (e.key === "Enter") commit();
      }}
    />
  );
}

/** The connections of one kind as a dropdown; without a value it takes the first one (unless it may stay empty). */
export function ConnectionSelect({
  id,
  kind,
  value,
  onChange,
  optional,
  disabled,
}: {
  id: string;
  kind: string;
  value: string;
  onChange: (value: string) => void;
  optional?: boolean;
  disabled?: boolean;
}) {
  const state = useDraft();
  const choices = (state.body?.connections || []).filter((c: any) => c.kind === kind);
  const empty = optional || choices.length === 0;
  const first = choices[0]?.id ?? "";
  useEffect(() => {
    // onChange is new on every render; the pick depends on the value and the first connection only.
    if (!disabled && !value && first && !optional) onChange(first);
  }, [disabled, value, first, optional]);
  const known = choices.some((c: any) => c.id === value);
  return (
    <select id={id} value={known ? value : empty ? "" : first} disabled={disabled} onChange={(e) => onChange(e.target.value)}>
      {empty && <option value="">{choices.length ? "(none)" : "No connection yet"}</option>}
      {choices.map((c: any) => (
        <option key={c.id} value={c.id}>
          {connectionLabel(c)}
        </option>
      ))}
    </select>
  );
}

/** A result in a word or a sentence, green when it worked. */
export function Chip({ ok, children }: { ok: boolean | null; children: ReactNode }) {
  return (
    <span className={`bdc-chip ${ok === null ? "neutral" : ok ? "ok" : "warn"}`}>
      <span className="dot" aria-hidden="true" />
      <span>{children}</span>
    </span>
  );
}

/**
 * "Remove X? ..." with Keep and Remove inside the page (memory: Remove asks inside the card, Keep or Remove), shown
 * where the list is; Keep takes the focus so Enter or Escape keeps the item.
 */
export function ConfirmStrip({ text, onRemove, onKeep }: { text: string; onRemove: () => void; onKeep: () => void }) {
  const keep = useRef<HTMLButtonElement>(null);
  useEffect(() => keep.current?.focus(), [text]);
  return (
    <div
      className="confirm-strip"
      role="alert"
      onKeyDown={(e) => {
        if (e.key === "Escape") onKeep();
      }}
    >
      <span>{text}</span>
      <span className="row-actions">
        <button ref={keep} type="button" className="button secondary" onClick={onKeep}>
          Keep
        </button>
        <button type="button" className="button danger-solid" onClick={onRemove}>
          Remove
        </button>
      </span>
    </div>
  );
}

/** A value to copy: an address, a key, a role command. */
export function CopyCode({ text, shown, label = "Copy" }: { text: string; shown?: string; label?: string }) {
  const [copied, setCopied] = useState(false);
  return (
    <div className="copy-code">
      <code>{shown ?? text}</code>
      <button
        type="button"
        className="button secondary"
        onClick={() =>
          navigator.clipboard
            .writeText(text)
            .then(() => setCopied(true))
            .catch(() => {})
        }
      >
        <Copy {...ICON} />
        {copied ? "Copied" : label}
      </button>
    </div>
  );
}

/** The path above a page below Outbound: Outbound › Sales invoice › Company 1000 › SharePoint library. */
export function PageTrail({ items }: { items: { text: string; href?: string }[] }) {
  return (
    <nav className="page-trail" aria-label="Path">
      {items.map((item, i) => (
        <Fragment key={`${i}-${item.text}`}>
          {i > 0 && <ChevronRight size={14} strokeWidth={1.75} aria-hidden="true" />}
          {i === items.length - 1 || !item.href ? (
            <span aria-current={i === items.length - 1 ? "location" : undefined}>{item.text}</span>
          ) : (
            <Link href={item.href}>{item.text}</Link>
          )}
        </Fragment>
      ))}
    </nav>
  );
}

/** One GET of the instance for a page: its answer, or the message why it failed. */
export function useAnswer<T = any>(path: string | null) {
  const [answer, setAnswer] = useState<{ path: string; data?: T; error?: string } | null>(null);
  useEffect(() => {
    if (!path) return;
    let current = true;
    api<T>(path)
      .then((data) => current && setAnswer({ path, data }))
      .catch((e: Error) => current && setAnswer({ path, error: e.message }));
    return () => {
      current = false;
    };
  }, [path]);
  const fresh = answer && answer.path === path ? answer : null;
  return { data: fresh?.data, error: fresh?.error ?? "", loading: Boolean(path) && !fresh };
}

/** A POST of a check or test; a refused call becomes { ok: false, message }, as the former console showed it. */
export async function post(path: string, body: unknown): Promise<{ ok: boolean; message: string; [key: string]: any }> {
  try {
    return await api(path, { method: "POST", body: JSON.stringify(body) });
  } catch (error) {
    return { ok: false, message: (error as Error).message };
  }
}
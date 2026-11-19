"use client";
import { useEffect, useRef, useState, type ReactNode } from "react";
import { AlertCircle, Check, CheckCircle2, Copy, Trash2, X as Close } from "lucide-react";
import { api } from "@/lib/api";
import { connectionLabel } from "@/lib/exportlogic";
import { onDialogCancel } from "./ui/confirm";
import { Notice } from "./notice";
import { useDraft } from "./save-bar";

// The small parts the Outbound and Connections pages share (the former draft.js and ui.js helpers): the ERP record
// dialog, read-only facts, segmented choices, the connection dropdown, inputs that commit on change, the Keep or
// Remove question, results, copyable code and the quiet Remove button.

export const ICON = { size: 16, strokeWidth: 1.75 };

/** The address of a document type's page: its tab, the company the tab shows and an open destination. */
export const typePath = (id: string, company?: string, tab?: string, dest?: string) =>
  `/outbound/type/?id=${encodeURIComponent(id)}${company ? `&company=${encodeURIComponent(company)}` : ""}${tab ? `&tab=${tab}` : ""}${dest ? `&dest=${encodeURIComponent(dest)}` : ""}`;

/**
 * The ERP record dialog (hive-erp record-dialog.tsx): title with a quiet meta line, the body, and a footer with a danger
 * action bottom left and the dialog's buttons right. Escape and the close button call onClose.
 */
export function BdcDialog({
  title,
  subtitle,
  onClose,
  children,
  leading,
  footer,
  wide,
}: {
  title: string;
  subtitle?: ReactNode;
  onClose: () => void;
  children: ReactNode;
  leading?: ReactNode;
  footer: ReactNode;
  wide?: boolean;
}) {
  const ref = useRef<HTMLDialogElement>(null);
  useEffect(() => {
    const dialog = ref.current;
    if (dialog && !dialog.open) dialog.showModal();
    return () => dialog?.close();
  }, []);
  return (
    <dialog
      ref={ref}
      className={`record-dialog bdc-dialog${wide ? " wide" : ""}`}
      aria-labelledby="bdc-dialog-title"
      onCancel={onDialogCancel(onClose)}
    >
      <div className="dialog-heading">
        <div>
          <h2 id="bdc-dialog-title">{title}</h2>
          {subtitle ? <p className="dialog-subtitle">{subtitle}</p> : null}
        </div>
        <button type="button" className="icon-button" aria-label="Close dialog" title="Close" onClick={onClose}>
          <Close size={18} strokeWidth={1.75} />
        </button>
      </div>
      <div className="dialog-body">{children}</div>
      <div className="dialog-footer">
        {leading}
        <div className="spacer" />
        {footer}
      </div>
    </dialog>
  );
}

/** Read-only values as the ERP shows them: a small uppercase label over the value. */
export function Facts({ items, className }: { items: [string, ReactNode][]; className?: string }) {
  return (
    <dl className={`bdc-facts${className ? ` ${className}` : ""}`}>
      {items.map(([label, value]) => (
        <div key={label}>
          <dt>{label}</dt>
          <dd>{value}</dd>
        </div>
      ))}
    </dl>
  );
}

/** On or off inside a list row: the CRM's small toggle pill, named for the row it belongs to. */
export function RowToggle({
  label,
  name,
  checked,
  onChange,
  disabled,
  large,
}: {
  label: string;
  name: string;
  checked: boolean;
  onChange: (on: boolean) => void;
  disabled?: boolean;
  large?: boolean;
}) {
  return (
    <label className={large ? "toggle-pill" : "toggle-pill small"}>
      <input type="checkbox" aria-label={name} checked={checked} disabled={disabled} onChange={(e) => onChange(e.target.checked)} />
      {label}
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
      {empty && <option value="">{choices.length ? "None" : "No connection yet"}</option>}
      {choices.map((c: any) => (
        <option key={c.id} value={c.id}>
          {connectionLabel(c)}
        </option>
      ))}
    </select>
  );
}

/** The answer of a check or test in one line: green with a tick when it worked, amber with the reason when not. */
export function Result({ ok, children }: { ok: boolean; children: ReactNode }) {
  return (
    <span className={`bdc-result ${ok ? "ok" : "warn"}`}>
      {ok ? <CheckCircle2 {...ICON} aria-hidden="true" /> : <AlertCircle {...ICON} aria-hidden="true" />}
      <span>{children}</span>
    </span>
  );
}

/**
 * "Remove X? ..." with Keep and Remove inside the page (memory: Remove asks inside the card, Keep or Remove), shown
 * where the item is; Keep takes the focus so Enter or Escape keeps the item.
 */
export function ConfirmStrip({ text, onRemove, onKeep }: { text: string; onRemove: () => void; onKeep: () => void }) {
  const keep = useRef<HTMLButtonElement>(null);
  useEffect(() => keep.current?.focus(), [text]);
  return (
    <div
      className="confirm-strip"
      role="alert"
      onKeyDown={(e) => {
        if (e.key === "Escape") {
          e.stopPropagation();
          onKeep();
        }
      }}
    >
      <span>{text}</span>
      <span className="strip-actions">
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

/** The quiet place for removing a record: a danger text button with a bin, bottom left, as the ERP's Archive. */
export function RemoveButton({ children, onClick, label }: { children: ReactNode; onClick: () => void; label?: string }) {
  return (
    <button type="button" className="button ghost danger" aria-label={label} onClick={onClick}>
      <Trash2 {...ICON} aria-hidden="true" />
      {children}
    </button>
  );
}

/** The quiet end of a record: Remove with a bin, bottom left; it asks Keep or Remove in place. */
export function RemoveLine({ what, text, onRemove }: { what: string; text: string; onRemove: () => void }) {
  const [asking, setAsking] = useState(false);
  const [error, setError] = useState("");
  return (
    <div className="record-end">
      {error && <Notice kind="error">{error}</Notice>}
      {asking ? (
        <ConfirmStrip
          text={text}
          onKeep={() => setAsking(false)}
          onRemove={() => {
            try {
              onRemove();
            } catch (e) {
              setError((e as Error).message);
            }
            setAsking(false);
          }}
        />
      ) : (
        <RemoveButton
          onClick={() => {
            setError("");
            setAsking(true);
          }}
        >
          Remove {what}
        </RemoveButton>
      )}
    </div>
  );
}

/** A value to copy: an address, a key, a role command. The copy button is an icon with its name as tooltip. */
export function CopyCode({ text, shown, label = "Copy" }: { text: string; shown?: string; label?: string }) {
  const [copied, setCopied] = useState(false);
  return (
    <div className="copy-code">
      <code>{shown ?? text}</code>
      <button
        type="button"
        className="icon-button small"
        aria-label={copied ? "Copied" : label}
        title={copied ? "Copied" : label}
        onClick={() =>
          navigator.clipboard
            .writeText(text)
            .then(() => setCopied(true))
            .catch(() => {})
        }
      >
        {copied ? <Check {...ICON} /> : <Copy {...ICON} />}
      </button>
    </div>
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
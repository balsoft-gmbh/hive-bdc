"use client";
import { useState, type ReactNode } from "react";
import Link from "next/link";
import { Check, Copy, X } from "lucide-react";
import { api } from "@/lib/api";
import type { StepRow } from "@/lib/updatelogic";

// The parts the four tabs of the Service page share: the tab bar, the panel, the read-only facts of the Hive ERP
// record dialog, the icon-only copy button, the POST of every action and the step marks of boards P4 to P6.

export const ICON = { size: 16, strokeWidth: 1.75 } as const;

export const SERVICE_TABS = [
  { id: "status", title: "Status" },
  { id: "licence", title: "Licence" },
  { id: "updates", title: "Updates" },
  { id: "log", title: "Technical log" },
] as const;
export type ServiceTab = (typeof SERVICE_TABS)[number]["id"];

/** The tab of ?tab=; anything else opens Status. */
export function tabOf(value: string | null | undefined): ServiceTab {
  return SERVICE_TABS.find((t) => t.id === value)?.id ?? "status";
}

export function tabHref(tab: ServiceTab) {
  return tab === "status" ? "/service/" : `/service/?tab=${tab}`;
}

/** The CRM tab bar of the Service page; Updates carries the count of offered updates, as the menu does. */
export function ServiceTabs({ current, count }: { current: ServiceTab; count: number }) {
  return (
    <nav className="setup-tabs service-tabs" aria-label="Service sections">
      {SERVICE_TABS.map((t) => (
        <Link
          key={t.id}
          href={tabHref(t.id)}
          className={`setup-tab${current === t.id ? " active" : ""}`}
          aria-current={current === t.id ? "page" : undefined}
        >
          {t.title}
          {t.id === "updates" && count ? (
            <span className="service-tab-count">
              {count}
              <span className="sr-only"> update available</span>
            </span>
          ) : null}
        </Link>
      ))}
    </nav>
  );
}

/** A CRM panel: the heading with an optional state pill next to it and the actions on the right, then the body. */
export function Panel({
  id,
  title,
  pill,
  actions,
  children,
  live,
  flush,
}: {
  id: string;
  title: ReactNode;
  pill?: ReactNode;
  actions?: ReactNode;
  children?: ReactNode;
  live?: boolean;
  flush?: boolean;
}) {
  return (
    <section className="panel service-panel" aria-labelledby={`${id}-heading`} aria-live={live ? "polite" : undefined}>
      <div className="section-heading">
        <div className="service-title">
          <h2 id={`${id}-heading`}>{title}</h2>
          {pill}
        </div>
        {actions && <div className="service-heading-actions">{actions}</div>}
      </div>
      {children && <div className={flush ? "service-flush" : "service-body"}>{children}</div>}
    </section>
  );
}

export type Fact = { label: string; value: ReactNode; extra?: ReactNode; span?: 2 | 3 };

/** Read-only facts as the Hive ERP record dialog shows them: small uppercase label, the value below; a long value spans columns. */
export function Facts({ items }: { items: Fact[] }) {
  return (
    <dl className="service-facts">
      {items.map((f) => (
        <div key={f.label} className={f.span ? `span-${f.span}` : undefined}>
          <dt>{f.label}</dt>
          <dd>
            {f.value ? <span className="service-fact-value">{f.value}</span> : <span className="cell-muted">Not set</span>}
            {f.extra}
          </dd>
        </div>
      ))}
    </dl>
  );
}

/** An icon-only copy button with a tooltip; screen readers hear its name and "Copied" once it worked. */
export function CopyButton({ text, label }: { text: string; label: string }) {
  const [copied, setCopied] = useState(false);
  const copy = () => {
    navigator.clipboard
      ?.writeText(text)
      .then(() => {
        setCopied(true);
        setTimeout(() => setCopied(false), 2000);
      })
      .catch(() => {});
  };
  return (
    <>
      <button type="button" className="icon-button service-copy" aria-label={label} title={copied ? "Copied" : label} onClick={copy}>
        {copied ? <Check {...ICON} /> : <Copy {...ICON} />}
      </button>
      <span className="sr-only" aria-live="polite">
        {copied ? "Copied" : ""}
      </span>
    </>
  );
}

/** Every action of the page is a POST with a JSON body, {} when it has none. */
export function postAction<T = any>(path: string, body?: unknown) {
  return api<T>(path, { method: "POST", body: JSON.stringify(body || {}) });
}

export type MarkState = StepRow["state"] | "problem";

/** The round mark in front of a step: a tick when done, a cross when it failed, a turning ring while it runs. */
export function Mark({ state }: { state: MarkState }) {
  if (state === "done")
    return (
      <span className="service-mark done" aria-hidden="true">
        <Check {...ICON} />
      </span>
    );
  if (state === "failed")
    return (
      <span className="service-mark failed" aria-hidden="true">
        <X {...ICON} />
      </span>
    );
  if (state === "problem")
    return (
      <span className="service-mark failed" aria-hidden="true">
        !
      </span>
    );
  return <span className={`service-mark ${state === "running" ? "running" : "waiting"}`} aria-hidden="true" />;
}

/** The steps of an update (boards P4 and P6): mark, title, detail and the time, "now" for the running one. */
export function StepList({ rows, label }: { rows: StepRow[]; label: string }) {
  return (
    <ol className="service-steps" aria-label={label}>
      {rows.map((r) => (
        <li key={r.name} className={`service-step ${r.state}`}>
          <Mark state={r.state} />
          <span className="service-step-text">
            <strong>{r.title}</strong>
            {r.detail && <span>{r.detail}</span>}
          </span>
          <span className={`service-step-time${r.state === "running" ? " now" : ""}`}>{r.state === "running" ? "now" : r.time}</span>
        </li>
      ))}
    </ol>
  );
}
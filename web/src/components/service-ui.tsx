"use client";
import type { ReactNode } from "react";
import Link from "next/link";
import { Check, X } from "lucide-react";
import { api } from "@/lib/api";
import type { StepRow } from "@/lib/updatelogic";

// The parts the four tabs of the Service page share: the tab bar, the POST of every action, the step marks and
// lists of boards P4 to P6, the banners and the fact lists.

export const ICON = { size: 18, strokeWidth: 1.75 } as const;
export const SMALL_ICON = { size: 16, strokeWidth: 1.75 } as const;

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
        <Check {...SMALL_ICON} />
      </span>
    );
  if (state === "failed")
    return (
      <span className="service-mark failed" aria-hidden="true">
        <X {...SMALL_ICON} />
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

/** A banner over the page: gold for an offered update, ok for a finished one, bad for a stop. */
export function ServiceBanner({
  tone,
  icon,
  title,
  titleId,
  role,
  children,
  actions,
}: {
  tone: "gold" | "ok" | "bad";
  icon: ReactNode;
  title: ReactNode;
  titleId?: string;
  role?: "alert" | "status";
  children?: ReactNode;
  actions?: ReactNode;
}) {
  return (
    <section className={`service-banner ${tone}`} role={role} aria-labelledby={titleId}>
      <span className="service-banner-icon">{icon}</span>
      <span className="service-banner-text">
        <strong id={titleId}>{title}</strong>
        {children}
      </span>
      {actions}
    </section>
  );
}

/** Label and value pairs; an empty value reads "unknown", as on the former Service page. */
export function Facts({ items, columns }: { items: { label: string; value: ReactNode; extra?: ReactNode }[]; columns?: boolean }) {
  return (
    <dl className={columns ? "service-facts columns" : "service-facts"}>
      {items.map((f) => (
        <div key={f.label}>
          <dt>{f.label}</dt>
          <dd>
            <span className="service-fact-value">{f.value || "unknown"}</span>
            {f.extra}
          </dd>
        </div>
      ))}
    </dl>
  );
}
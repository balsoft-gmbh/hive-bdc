"use client";
import Link from "next/link";
import { useEffect, useState } from "react";
import { useConsole } from "./console-context";
import { Notice } from "./notice";
import { change, draft, useDraft } from "./save-bar";
import { Chip, CommitInput, ConnectionSelect, CopyCode, Field, Segmented, post, useAnswer } from "./outbound-parts";
import * as X from "@/lib/exportlogic";

// Comes from (board W9, the first half of export.js companyScreen): a Blob container, a Service Bus queue or topic, or
// a web address with its own key. The form shows the draft; every change goes into it at once, and a company opened
// without a source takes the chosen kind right away, as the former console did.

const same = (a: unknown, b: unknown) => String(a ?? "").toLowerCase() === String(b ?? "").toLowerCase();

const TILES: [string, string, string][] = [
  ["blob", "Azure Blob Storage", "F&O writes into a container"],
  ["servicebus", "Azure Service Bus", "F&O sends a message"],
  ["https", "Web address (HTTPS)", "Power Automate, Logic App, ERP"],
];

export function SourceTab({ type, entry, ro }: { type: any; entry: any; ro: boolean }) {
  const identity = useAnswer<{ inputRoot: string }>("/setup/identity");
  if (identity.error) return <Notice kind="error">{identity.error}</Notice>;
  if (!identity.data) return <p className="muted">Reading the instance.</p>;
  return <SourceForm type={type} entry={entry} ro={ro} inputRoot={identity.data.inputRoot} />;
}

type Check = "busy" | { ok: boolean; message: string; command?: string } | null;

function SourceForm({ type, entry, ro, inputRoot }: { type: any; entry: any; ro: boolean; inputRoot: string }) {
  const { config } = useConsole();
  const state = useDraft();
  const [chosen, setChosen] = useState<string | null>(null);
  const [topicChoice, setTopicChoice] = useState<boolean | null>(null);
  const [check, setCheck] = useState<Check>(null);
  const kind = chosen || entry.source?.kind || "blob";
  const source = entry.source && entry.source.kind === kind ? entry.source : null;
  const settings: Record<string, string> = source?.settings || {};
  const httpsId = (entry.source?.kind === "https" && entry.source.connection) || X.httpsConnectionId(type.id, entry.dataAreaId);
  const connection: string = source?.connection || (kind === "https" ? httpsId : "");

  // Every change reads the entry from the draft as it is now, so two changes in one moment do not undo each other.
  const current = () => {
    const t = draft.body?.documentTypes.find((x: any) => same(x.id, type.id));
    return t?.companies.find((c: any) => same(c.dataAreaId, entry.dataAreaId)) ?? null;
  };
  const apply = (patch: { connection?: string; settings?: Record<string, string> }) => {
    const e = current();
    if (!e) return;
    const s = e.source && e.source.kind === kind ? e.source : null;
    const next = {
      kind,
      connection: patch.connection ?? (s?.connection || (kind === "https" ? httpsId : "")),
      settings: { ...(s?.settings || {}), ...(patch.settings || {}) },
    };
    change((d) => X.setSource(d, type.id, e.dataAreaId, next, inputRoot));
  };

  useEffect(() => {
    if (ro) return;
    const e = current();
    if (e && (!e.source || e.source.kind !== kind)) apply({});
    // apply and current read the draft at call time; the kind is what this effect follows.
  }, [kind, ro]);

  const runCheck = async (path: string, body: () => unknown) => {
    setCheck("busy");
    setCheck(await post(path, body()));
  };
  const sourceNow = () => current()?.source?.settings || {};
  const connectionNow = () => current()?.source?.connection || "";
  const hasAny = (k: string) => (state.body?.connections || []).some((c: any) => c.kind === k);
  const noConnection = (what: string) => (
    <p className="warn-text">
      There is no {what} connection yet. Add one under <Link href="/connections/">Connections</Link>, then come back.
    </p>
  );
  const input = (key: string, id: string, placeholder?: string) => (
    <CommitInput id={id} value={settings[key] || ""} placeholder={placeholder} disabled={ro} onCommit={(v) => apply({ settings: { [key]: v } })} />
  );
  const checkLine = (
    <div className="check-line" aria-live="polite">
      {check === "busy" ? (
        <span className="muted">Checking</span>
      ) : check ? (
        <>
          <Chip ok={Boolean(check.ok)}>{check.message}</Chip>
          {!check.ok && check.command && <CopyCode text={check.command} />}
        </>
      ) : null}
    </div>
  );

  let form;
  if (kind === "blob") {
    form = (
      <>
        {!hasAny("azure-storage") && noConnection("Azure Storage")}
        <div className="form-row-3">
          <Field label="Connection" htmlFor="source-connection">
            <ConnectionSelect
              id="source-connection"
              kind="azure-storage"
              value={connection}
              disabled={ro}
              onChange={(v) => apply({ connection: v })}
            />
          </Field>
          <Field label="Container" htmlFor="source-container">
            {input("Container", "source-container", "bdc-export")}
          </Field>
          <Field label="Folder in the container (optional)" htmlFor="source-prefix">
            {input("Prefix", "source-prefix", "empty: the whole container")}
          </Field>
        </div>
        {!ro && (
          <div className="check-row-actions">
            <button
              type="button"
              className="button secondary"
              onClick={() =>
                void runCheck("/setup/check-source", () => ({
                  connection: connectionNow(),
                  container: sourceNow().Container || "",
                  prefix: sourceNow().Prefix || "",
                }))
              }
            >
              Check
            </button>
            {checkLine}
          </div>
        )}
        <p className="note">
          F&amp;O writes each export and its PDF into this container. Hive BDC moves both to processed/ or error/ when it is done.
        </p>
      </>
    );
  } else if (kind === "servicebus") {
    const topicMode = topicChoice ?? Boolean(settings.Topic);
    form = (
      <>
        {!hasAny("servicebus") && noConnection("Service Bus")}
        <div className="form-row-3">
          <Field label="Connection" htmlFor="source-connection">
            <ConnectionSelect id="source-connection" kind="servicebus" value={connection} disabled={ro} onChange={(v) => apply({ connection: v })} />
          </Field>
          <div className="field">
            <span className="field-label">Reads from</span>
            <Segmented
              label="Reads from"
              options={[
                ["queue", "A queue"],
                ["topic", "A topic"],
              ]}
              value={topicMode ? "topic" : "queue"}
              disabled={ro}
              onChange={(v) => {
                setTopicChoice(v === "topic");
                apply({ settings: v === "topic" ? { Queue: "" } : { Topic: "", Subscription: "" } });
              }}
            />
          </div>
          {topicMode ? (
            <div className="form-row-2">
              <Field label="Topic" htmlFor="source-topic">
                {input("Topic", "source-topic")}
              </Field>
              <Field label="Subscription" htmlFor="source-subscription">
                {input("Subscription", "source-subscription")}
              </Field>
            </div>
          ) : (
            <Field label="Queue" htmlFor="source-queue">
              {input("Queue", "source-queue", "bdc-export")}
            </Field>
          )}
        </div>
        <div className="form-row-2">
          <Field label="PDFs are read from" htmlFor="source-pdf-connection">
            <ConnectionSelect
              id="source-pdf-connection"
              kind="azure-storage"
              value={settings.PdfConnection || ""}
              optional
              disabled={ro}
              onChange={(v) => apply({ settings: { PdfConnection: v } })}
            />
          </Field>
          <Field label="Container" htmlFor="source-pdf-container">
            {input("PdfContainer", "source-pdf-container", "bdc-pdf")}
          </Field>
        </div>
        {!ro && (
          <div className="check-row-actions">
            <button
              type="button"
              className="button secondary"
              onClick={() =>
                void runCheck("/setup/check-queue", () => ({
                  connection: connectionNow(),
                  queue: sourceNow().Queue || "",
                  topic: sourceNow().Topic || "",
                  subscription: sourceNow().Subscription || "",
                }))
              }
            >
              Check
            </button>
            {checkLine}
          </div>
        )}
        <p className="note">
          F&amp;O sends each export as a message and names its PDF in the container. A message that cannot be handled goes to the dead-letter part
          with the reason.
        </p>
      </>
    );
  } else {
    const origin = typeof window === "undefined" ? "" : window.location.origin;
    const address = `${origin}/inbound/${encodeURIComponent(type.id)}/${encodeURIComponent(entry.dataAreaId)}`;
    const shown = state.keys.get(httpsId);
    const stored = (config?.connections || []).some((c: any) => c.id === httpsId && c.hasSecret);
    const makeKey = () => {
      const key = X.newKey();
      draft.keys.set(httpsId, key);
      change((d) => X.setHttpsKey(d, type.id, entry.dataAreaId, key, inputRoot, httpsId));
    };
    form = (
      <>
        <div className="field">
          <span className="field-label">Address the sender posts to</span>
          <CopyCode text={address} />
        </div>
        {shown ? (
          <div className="keybox" role="status">
            <strong>New key for {entry.dataAreaId}, shown only now</strong>
            <CopyCode text={shown} shown={`x-api-key: ${shown}`} />
            <span>
              Put it into the sender now. It goes to this instance&apos;s Key Vault when you save and is never shown again. A lost key is replaced,
              not looked up.
            </span>
          </div>
        ) : (
          <p className="muted">
            {stored
              ? "A key is stored for this address. It is never shown again."
              : "No key yet. Create one; the sender sends it in the header x-api-key."}
          </p>
        )}
        {!ro && (
          <div className="check-row-actions">
            <button type="button" className="button secondary" onClick={makeKey}>
              {shown || stored ? "Replace the key" : "Create a key"}
            </button>
            {stored && !shown && <span className="muted small">The old key stops working when you save.</span>}
          </div>
        )}
        <div className="form-row-2 small muted">
          <div className="stack-tight">
            <strong>What the sender posts</strong>
            <span>
              JSON with the ERP export as &quot;document&quot; and the PDF as Base64 in &quot;pdf&quot;. A repeat of the same document number is
              answered &quot;already received&quot;.
            </span>
          </div>
          <div className="stack-tight">
            <strong>Answers</strong>
            <span>202 taken · 200 already received · 401 wrong key · 413 over 50 MB. Nothing is stored on a refusal.</span>
          </div>
        </div>
      </>
    );
  }

  return (
    <section className="panel form-panel" aria-labelledby="from-heading">
      <div className="section-heading">
        <h2 id="from-heading" className="caps">
          Comes from
        </h2>
      </div>
      <div className="form-body">
        <fieldset className="source-tiles" disabled={ro}>
          <legend className="sr-only">Source</legend>
          {TILES.map(([value, title, note]) => (
            <label key={value} className={`source-tile${value === kind ? " selected" : ""}`}>
              <input
                type="radio"
                name={`source-${type.id}-${entry.dataAreaId}`}
                value={value}
                checked={value === kind}
                onChange={() => setChosen(value)}
              />
              <span className="source-tile-text">
                <strong>{title}</strong>
                <span>{note}</span>
              </span>
            </label>
          ))}
        </fieldset>
        {form}
      </div>
    </section>
  );
}
"use client";
/* eslint-disable @typescript-eslint/no-explicit-any */
import Link from "next/link";
import { useState } from "react";
import { LoaderCircle, ShieldCheck } from "lucide-react";
import { api } from "@/lib/api";
import { companyList, postBody, sourceEntries, typeLabel, withServiceBusSource } from "@/lib/logic";
import { busChoiceReady, queueCheckBody, stepHref, type BusChoice } from "@/app/setup/steps";
import { useConsole } from "./console-context";
import { Notice } from "./notice";
import { CommandBox, ICON, Panel, Result, Segments, SelectField, StepActions, TextField, holdStep, useConfigSave } from "./setup-parts";
import type { SourceProps } from "./setup-source";

const ACCESS = [
  { value: "identity", label: "Managed identity" },
  { value: "key", label: "Connection string" },
] as const;

const MODES = [
  { value: "queue", label: "A queue" },
  { value: "topic", label: "A topic" },
] as const;

type Answer = { ok: boolean; message: string; command?: string | null };

/**
 * Step 2 for Service Bus (design version 3, decision 1): the connection, the queue or topic subscription, the PDFs'
 * storage, the type and its companies picked by hand (a peek does not tell types apart). Save and check stores the
 * connection and the sources first, then peeks with the saved connection (S2).
 */
export function BusSource({ progress, readOnly, identity, notes }: SourceProps) {
  const { config, refresh } = useConsole();
  const entries = sourceEntries(config).filter((e) => e.company.source.kind === "servicebus");
  const first = entries[0];
  const settings = first?.company.source.settings ?? {};
  const connections: any[] = config.connections || [];
  const kept = first && connections.find((c) => c.id === first.company.source.connection);
  const pdf = settings.PdfConnection && connections.find((c) => c.id === settings.PdfConnection);
  const types = (config.documentTypes || []).map((t: any) => ({ value: t.id, label: typeLabel(t) }));

  const [form, setForm] = useState<BusChoice>(() => ({
    access: kept?.hasSecret ? "key" : "identity",
    namespace: kept?.settings?.Namespace || "",
    connectionString: "",
    mode: settings.Topic ? "topic" : "queue",
    queue: settings.Queue || "",
    topic: settings.Topic || "",
    subscription: settings.Subscription || "",
    pdfAccount: pdf?.settings?.AccountUrl || "",
    pdfContainer: settings.PdfContainer || "",
    type: first?.type.id || types[0]?.value || "",
    companies: entries.filter((e) => e.type.id === first?.type.id).map((e) => e.company.dataAreaId),
  }));
  const [companies, setCompanies] = useState(form.companies.join(", "));
  const [saved, setSaved] = useState<string>(first?.company.source.connection || "");
  const [dirty, setDirty] = useState(false);
  const [answer, setAnswer] = useState<Answer | "busy" | null>(null);
  const [planError, setPlanError] = useState("");
  const { save, slot, busy } = useConfigSave();

  const keyStored = Boolean(connections.find((c) => c.id === saved)?.hasSecret);
  const ready = busChoiceReady(form, keyStored);
  const checkOnly = Boolean(saved) && !dirty;

  const edit =
    <K extends keyof BusChoice>(key: K) =>
    (value: BusChoice[K]) => {
      setForm((f) => ({ ...f, [key]: value }));
      setDirty(true);
      setAnswer(null);
      setPlanError("");
    };

  const check = async (body: ReturnType<typeof queueCheckBody>) => {
    setAnswer("busy");
    try {
      const a = await api("/setup/check-queue", { method: "POST", body: JSON.stringify(body) });
      setAnswer({ ok: Boolean(a.ok), message: a.message, command: a.command });
    } catch (error) {
      setAnswer({ ok: false, message: (error as Error).message });
    }
  };

  const saveAndCheck = () => {
    if (checkOnly) {
      void check(queueCheckBody(saved, form));
      return;
    }
    let planned: { body: any; connection: string };
    try {
      planned = withServiceBusSource(
        postBody(config),
        { ...form, connection: saved, withSecret: connections.filter((c) => c.hasSecret).map((c) => c.id) },
        identity.inputRoot,
      );
    } catch (error) {
      setPlanError((error as Error).message);
      return;
    }
    const body = queueCheckBody(planned.connection, form);
    setAnswer(null);
    holdStep(2);
    void save(planned.body, async () => {
      setSaved(planned.connection);
      setDirty(false);
      await refresh();
      await check(body);
    });
  };

  let next = null;
  if (!dirty && progress.done[1]) {
    next = (
      <Link className="button primary" href={stepHref(3)}>
        Continue
      </Link>
    );
  } else if (!readOnly) {
    next = (
      <button type="button" className="button primary" disabled>
        Continue
      </button>
    );
  }

  const working = busy || answer === "busy";
  const command = answer && answer !== "busy" && !answer.ok ? answer.command : "";

  return (
    <>
      <Panel id="bus-heading" title="Service Bus">
        <div className="setup-choices">
          <Segments legend="Access" name="bus-access" options={ACCESS} value={form.access} disabled={readOnly} onChange={edit("access")} />
          <Segments legend="Reads from" name="bus-mode" options={MODES} value={form.mode} disabled={readOnly} onChange={edit("mode")} />
        </div>
        <div className="setup-fields">
          {form.access === "identity" ? (
            <TextField
              id="bus-namespace"
              label="Namespace"
              value={form.namespace}
              placeholder="<name>.servicebus.windows.net"
              hint="Signs in as this instance. Needs the Azure Service Bus Data Receiver role."
              disabled={readOnly}
              onChange={edit("namespace")}
            />
          ) : (
            <TextField
              id="bus-key"
              label="Connection string"
              type="password"
              value={form.connectionString}
              placeholder={keyStored ? "Stored. Leave empty to keep it." : "Endpoint=sb://..."}
              hint="From the namespace, Shared access policies. Kept in this instance's Key Vault."
              disabled={readOnly}
              onChange={edit("connectionString")}
            />
          )}
          {form.mode === "queue" ? (
            <TextField id="bus-queue" label="Queue" value={form.queue} placeholder="bdc-export" disabled={readOnly} onChange={edit("queue")} />
          ) : (
            <>
              <TextField id="bus-topic" label="Topic" value={form.topic} placeholder="bdc-export" disabled={readOnly} onChange={edit("topic")} />
              <TextField id="bus-subscription" label="Subscription" value={form.subscription} placeholder="hivebdc" disabled={readOnly} onChange={edit("subscription")} />
            </>
          )}
        </div>
        <div className="setup-fields">
          <SelectField id="bus-type" label="Document type" value={form.type} options={types} disabled={readOnly} onChange={edit("type")} />
          <TextField
            id="bus-companies"
            label="Companies"
            value={companies}
            placeholder="1000, DEMF"
            hint="F&O company IDs, separated by commas."
            disabled={readOnly}
            onChange={(text) => {
              setCompanies(text);
              edit("companies")(companyList(text));
            }}
          />
        </div>
        <div className="setup-fields">
          <TextField
            id="bus-pdf-account"
            label="Storage account of the PDFs (optional)"
            type="url"
            value={form.pdfAccount}
            placeholder="https://<account>.blob.core.windows.net"
            hint="For messages that name their PDF in pdfLink."
            disabled={readOnly}
            onChange={edit("pdfAccount")}
          />
          <TextField
            id="bus-pdf-container"
            label="Container of the PDFs"
            value={form.pdfContainer}
            placeholder="bdc-pdf"
            disabled={readOnly}
            onChange={edit("pdfContainer")}
          />
        </div>
        {!readOnly && (
          <div className="setup-inline">
            <button type="button" className="button secondary" disabled={!ready || working || !identity} onClick={saveAndCheck}>
              {working ? <LoaderCircle className="spin" {...ICON} /> : <ShieldCheck {...ICON} />}
              {checkOnly ? "Check access" : "Save and check"}
            </button>
            <span aria-live="polite" className="setup-check-line">
              {answer === "busy" ? <span className="muted">Checking</span> : answer && <Result ok={answer.ok}>{answer.message}</Result>}
            </span>
          </div>
        )}
        {command && <CommandBox command={command} />}
      </Panel>
      {planError && <Notice kind="error">{planError}</Notice>}
      {notes}
      {slot}
      <StepActions back={1} next={next} />
    </>
  );
}
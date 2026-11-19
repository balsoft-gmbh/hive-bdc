"use client";
/* eslint-disable @typescript-eslint/no-explicit-any */
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useEffect, useState } from "react";
import { ArrowRight, Container, Globe, LoaderCircle, MessagesSquare, ShieldCheck } from "lucide-react";
import { api } from "@/lib/api";
import { andList, cleanPrefix, postBody, roleCommand, setupType, sourceEntries, typeWords, withBlobSource } from "@/lib/logic";
import { companiesWord, stepHref, typesWord } from "@/app/setup/steps";
import { useConsole } from "./console-context";
import { Notice } from "./notice";
import { Chip, CopyButton, ICON, Loading, StepActions, StepFrame, TextField, useConfigSave, type Progress } from "./setup-parts";

const ICON_18 = { size: 18, strokeWidth: 1.75 };

const SOURCES = [
  { value: "blob", title: "Azure Blob Storage", text: "Standard for F&O. F&O writes each export and its PDF into a container.", icon: Container },
  { value: "servicebus", title: "Azure Service Bus", text: "F&O sends each export as a message to a queue or topic.", icon: MessagesSquare },
  { value: "https", title: "Web address (HTTPS)", text: "Power Automate, a Logic App or another ERP posts to Hive BDC.", icon: Globe },
];

function PlanNote({ check, config }: { check: any; config: any }) {
  const plan = setupType(check.byType, config.documentTypes);
  if (!plan.type) {
    return (
      <Notice kind="warn">
        No export of a document type Hive BDC knows is waiting. Send one document from F&O into this container, then check again.
      </Notice>
    );
  }
  const type = config.documentTypes.find((t: any) => t.id === plan.type);
  const later = plan.later.map((l) => {
    const known = config.documentTypes.find((t: any) => t.id.toLowerCase() === String(l.documentType).toLowerCase());
    return l.documentType === "unknown" ? `${l.count} without a document type` : `${l.count} ${typeWords(known || { id: l.documentType }, l.count)}`;
  });
  return (
    <p className="setup-note setup-plan">
      {`Hive BDC sets up ${typesWord(type)} for ${companiesWord(plan.companies)} ${andList(plan.companies)}.`}
      {later.length ? ` Also waiting: ${later.join(", ")}. They stay in the container until you add them under Outbound.` : ""}
    </p>
  );
}

export function SourceStep({ progress, readOnly }: { progress: Progress; readOnly: boolean }) {
  const { config, refresh } = useConsole();
  const router = useRouter();
  const existing = sourceEntries(config).find((e) => e.company.source.kind === "blob");
  const existingConnection = existing && (config.connections || []).find((c: any) => c.id === existing.company.source.connection);
  const sourceSettings = existing?.company.source.settings ?? {};

  const [identity, setIdentity] = useState<any>(null);
  const [identityError, setIdentityError] = useState("");
  const [kind, setKind] = useState("blob");
  const [account, setAccount] = useState<string>(existingConnection?.settings?.AccountUrl || "");
  const [container, setContainer] = useState<string>(sourceSettings.Container || "");
  const [prefix, setPrefix] = useState<string>(sourceSettings.Prefix || "");
  const [access, setAccess] = useState<"identity" | "key">("identity");
  const [key, setKey] = useState("");
  const [check, setCheck] = useState<any>(null);
  const [checkLine, setCheckLine] = useState<{ ok: boolean; text: string } | null>(null);
  const [checking, setChecking] = useState(false);
  const [dirty, setDirty] = useState(!progress.done[1]);
  const { save, slot, busy } = useConfigSave();

  useEffect(() => {
    api("/setup/identity")
      .then(setIdentity)
      .catch((e: Error) => setIdentityError(e.message));
  }, []);

  const edit = (set: (value: any) => void) => (value: any) => {
    set(value);
    setDirty(true);
    setCheck(null);
    setCheckLine(null);
  };

  const runCheck = async () => {
    setChecking(true);
    setCheckLine(null);
    try {
      const answer = await api("/setup/check-source", {
        method: "POST",
        body: JSON.stringify({
          accountUrl: account.trim(),
          container: container.trim(),
          prefix: cleanPrefix(prefix),
          connectionString: access === "key" ? key.trim() : null,
        }),
      });
      setCheck(answer);
      setCheckLine({ ok: Boolean(answer.ok), text: answer.message });
    } catch (error) {
      setCheck(null);
      setCheckLine({ ok: false, text: (error as Error).message });
    } finally {
      setChecking(false);
    }
  };

  const saveSource = () => {
    const planned = withBlobSource(
      postBody(config),
      { accountUrl: account.trim(), container: container.trim(), prefix, connectionString: access === "key" ? key.trim() : "" },
      check.byType,
      identity.inputRoot,
    );
    void save(planned.body, async () => {
      await refresh();
      router.push(stepHref(3));
    });
  };

  const plan = check?.ok ? setupType(check.byType, config.documentTypes) : null;
  const command = check?.command || roleCommand(identity?.objectId, account) || "Enter the storage account address above.";
  const blob = kind === "blob";

  let next = null;
  if (blob && !dirty) {
    next = (
      <Link className="button primary" href={stepHref(3)}>
        Next: where documents go
        <ArrowRight {...ICON} />
      </Link>
    );
  } else if (blob && !readOnly) {
    next = (
      <button type="button" className="button primary" disabled={!plan?.type || busy || !identity} onClick={saveSource}>
        {busy && <LoaderCircle className="spin" {...ICON} />}
        Save and continue
      </button>
    );
  }

  return (
    <StepFrame
      step={2}
      progress={progress}
      readOnly={readOnly}
      title="How does F&O hand over its documents?"
      description="Pick what your F&O already uses. The Balsoft model in F&O writes each export as JSON with its PDF."
    >
      <fieldset className="setup-tiles setup-choose">
        <legend className="sr-only">Comes from</legend>
        {SOURCES.map((s) => (
          <label key={s.value} className="setup-tile">
            <input
              type="radio"
              name="source"
              value={s.value}
              checked={kind === s.value}
              disabled={readOnly}
              onChange={() => setKind(s.value)}
            />
            <span className="setup-tile-icon">
              <s.icon {...ICON_18} />
            </span>
            <span className="setup-tile-body">
              <strong>{s.title}</strong>
              <span>{s.text}</span>
            </span>
          </label>
        ))}
      </fieldset>

      {blob ? (
        <section className="panel" aria-labelledby="blob-heading">
          <div className="section-heading">
            <div>
              <h2 id="blob-heading">The container F&O writes to</h2>
              <p>
                F&O keeps writing to this container as it does today. Hive BDC takes each export with the PDF named in it and moves both to
                processed/ or error/ when it is done.
              </p>
            </div>
          </div>
          <div className="setup-panel-body">
            <div className="setup-fields">
              <TextField
                id="src-account"
                label="Storage account address"
                type="url"
                value={account}
                placeholder="https://youraccount.blob.core.windows.net"
                disabled={readOnly}
                onChange={edit(setAccount)}
              />
              <TextField id="src-container" label="Container" value={container} placeholder="bdc-export" disabled={readOnly} onChange={edit(setContainer)} />
              <TextField
                id="src-prefix"
                label="Folder in the container (optional)"
                value={prefix}
                placeholder="empty: the whole container"
                disabled={readOnly}
                onChange={edit(setPrefix)}
              />
            </div>

            <fieldset className="setup-access">
              <legend>How Hive BDC gets in</legend>
              <label className="setup-option">
                <input
                  type="radio"
                  name="access"
                  value="identity"
                  checked={access === "identity"}
                  disabled={readOnly}
                  onChange={() => edit(setAccess)("identity")}
                />
                <span>
                  <strong>Give Hive BDC access</strong> (recommended, no key to store or renew). Your IT runs this once in Azure Cloud Shell:
                </span>
              </label>
              {access === "identity" && (
                <div className="setup-indent">
                  <div className="setup-code">
                    <code>{command}</code>
                    {!readOnly && <CopyButton text={command} label="Copy the command" />}
                  </div>
                </div>
              )}
              <label className="setup-option">
                <input type="radio" name="access" value="key" checked={access === "key"} disabled={readOnly} onChange={() => edit(setAccess)("key")} />
                <span>
                  <strong>Use a connection string</strong>. It is kept in this instance&apos;s Key Vault and never shown again.
                </span>
              </label>
              {access === "key" && (
                <div className="setup-indent">
                  <TextField
                    id="src-key"
                    label="Connection string"
                    type="password"
                    value={key}
                    placeholder="DefaultEndpointsProtocol=https;AccountName=..."
                    hint="From the storage account, Access keys. It is kept in this instance's Key Vault and never shown again."
                    disabled={readOnly}
                    onChange={edit(setKey)}
                  />
                </div>
              )}
            </fieldset>

            {!readOnly && (
              <div className="setup-inline">
                <button type="button" className="button secondary" disabled={checking} onClick={() => void runCheck()}>
                  {checking ? <LoaderCircle className="spin" {...ICON} /> : <ShieldCheck {...ICON} />}
                  Check access
                </button>
                <span aria-live="polite" className="setup-check-line">
                  {checking ? <span className="muted">Checking</span> : checkLine && <Chip ok={checkLine.ok}>{checkLine.text}</Chip>}
                </span>
              </div>
            )}
            {dirty && check?.ok && <PlanNote check={check} config={config} />}
          </div>
        </section>
      ) : (
        <p className="setup-note">
          The assistant checks and tests a Blob container. For Service Bus or a web address, add the companies on the Outbound page.
        </p>
      )}

      {identityError && <Notice kind="error">{identityError}</Notice>}
      {!identity && !identityError && blob && !readOnly && <Loading text="Reading this instance's identity" />}
      {slot}
      <StepActions back={1} next={next} />
    </StepFrame>
  );
}
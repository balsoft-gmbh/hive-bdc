"use client";
/* eslint-disable @typescript-eslint/no-explicit-any */
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useEffect, useState } from "react";
import { Container, Globe, LoaderCircle, MessagesSquare, ShieldCheck } from "lucide-react";
import { api } from "@/lib/api";
import { andList, cleanPrefix, postBody, roleCommand, setupType, sourceEntries, typeWords, withBlobSource } from "@/lib/logic";
import { companiesWord, stepHref, typesWord } from "@/app/setup/steps";
import { useConsole } from "./console-context";
import { Notice } from "./notice";
import { CopyButton, Hint, ICON, Loading, Panel, Result, StepActions, StepFrame, TextField, useConfigSave, type Progress } from "./setup-parts";

const ICON_18 = { size: 18, strokeWidth: 1.75 };

const SOURCES = [
  { value: "blob", title: "Azure Blob Storage", text: "F&O writes each export with its PDF to a container.", icon: Container },
  { value: "servicebus", title: "Azure Service Bus", text: "F&O sends each export as a message to a queue or topic.", icon: MessagesSquare },
  { value: "https", title: "Web address (HTTPS)", text: "Power Automate, a Logic App or another ERP posts to Hive BDC.", icon: Globe },
];

const ACCESS = [
  { value: "identity", label: "Managed identity" },
  { value: "key", label: "Connection string" },
] as const;

function PlanNote({ check, config }: { check: any; config: any }) {
  const plan = setupType(check.byType, config.documentTypes);
  if (!plan.type) {
    return <Notice kind="warn">No export of a document type Hive BDC knows is waiting. Send one document from F&O into this container, then check again.</Notice>;
  }
  const type = config.documentTypes.find((t: any) => t.id === plan.type);
  const later = plan.later.map((l) => {
    const known = config.documentTypes.find((t: any) => t.id.toLowerCase() === String(l.documentType).toLowerCase());
    return l.documentType === "unknown" ? `${l.count} without a document type` : `${l.count} ${typeWords(known || { id: l.documentType }, l.count)}`;
  });
  return (
    <p className="setup-plan">
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
  const command: string = check?.command || roleCommand(identity?.objectId, account);
  const blob = kind === "blob";

  let next = null;
  if (blob && !dirty) {
    next = (
      <Link className="button primary" href={stepHref(3)}>
        Continue
      </Link>
    );
  } else if (blob && !readOnly) {
    next = (
      <button type="button" className="button primary" disabled={!plan?.type || busy || !identity} onClick={saveSource}>
        {busy && <LoaderCircle className="spin" {...ICON} />}
        Continue
      </button>
    );
  }

  return (
    <StepFrame step={2} progress={progress} readOnly={readOnly}>
      <fieldset className="setup-group">
        <legend>
          <h2 className="setup-group-title">Comes from</h2>
        </legend>
        <div className="setup-tiles setup-choose">
          {SOURCES.map((s) => (
            <label key={s.value} className="setup-tile">
              <input type="radio" name="source" value={s.value} checked={kind === s.value} disabled={readOnly} onChange={() => setKind(s.value)} />
              <span className="setup-tile-icon">
                <s.icon {...ICON_18} />
              </span>
              <span className="setup-tile-body">
                <strong>{s.title}</strong>
                <span>{s.text}</span>
              </span>
            </label>
          ))}
        </div>
      </fieldset>

      {blob ? (
        <Panel id="blob-heading" title="Blob container">
          <div className="setup-fields">
            <TextField
              id="src-account"
              label="Storage account address"
              type="url"
              value={account}
              placeholder="https://<account>.blob.core.windows.net"
              disabled={readOnly}
              onChange={edit(setAccount)}
            />
            <TextField
              id="src-container"
              label="Container"
              value={container}
              placeholder="bdc-export"
              hint="Done exports move to processed/ or error/."
              disabled={readOnly}
              onChange={edit(setContainer)}
            />
            <TextField id="src-prefix" label="Folder in the container (optional)" value={prefix} disabled={readOnly} onChange={edit(setPrefix)} />
          </div>

          <fieldset className="setup-access">
            <legend>Access</legend>
            <div className="setup-segments">
              {ACCESS.map((a) => (
                <label key={a.value} className="setup-segment">
                  <input
                    type="radio"
                    name="access"
                    value={a.value}
                    checked={access === a.value}
                    disabled={readOnly}
                    onChange={() => edit(setAccess)(a.value)}
                  />
                  {a.label}
                </label>
              ))}
            </div>
            {access === "identity" ? (
              <div className="setup-command">
                <div className="setup-code">
                  {command ? <code>{command}</code> : <span className="setup-code-empty">Enter the storage account address above.</span>}
                  {!readOnly && command && <CopyButton text={command} label="Copy the command" />}
                </div>
                <Hint>IT runs this once in Azure Cloud Shell. No key to store or renew.</Hint>
              </div>
            ) : (
              <div className="setup-fields one">
                <TextField
                  id="src-key"
                  label="Connection string"
                  type="password"
                  value={key}
                  placeholder="DefaultEndpointsProtocol=https;AccountName=..."
                  hint="From the storage account, Access keys. Kept in this instance's Key Vault."
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
                {checking ? <span className="muted">Checking</span> : checkLine && <Result ok={checkLine.ok}>{checkLine.text}</Result>}
              </span>
            </div>
          )}
          {dirty && check?.ok && <PlanNote check={check} config={config} />}
        </Panel>
      ) : (
        <p className="setup-note">The assistant checks and tests a Blob container. For Service Bus or HTTPS, add the companies under Outbound.</p>
      )}

      {identityError && <Notice kind="error">{identityError}</Notice>}
      {!identity && !identityError && blob && !readOnly && <Loading text="Reading this instance's identity" />}
      {slot}
      <StepActions back={1} next={next} />
    </StepFrame>
  );
}
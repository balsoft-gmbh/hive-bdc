"use client";
/* eslint-disable @typescript-eslint/no-explicit-any */
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useEffect, useRef, useState, type ComponentType } from "react";
import { Dropdown, Option } from "@fluentui/react-components";
import { Archive, ArrowRight, Container, FolderOpen, LoaderCircle, Mail, PlugZap, Send } from "lucide-react";
import { api } from "@/lib/api";
import { andList, destinationPicks, destinationProblems, hasDestination, postBody, sourceEntries, uniqueId, withDestinations } from "@/lib/logic";
import { companiesWord, stepHref, typesWord } from "@/app/setup/steps";
import { useConsole } from "./console-context";
import { Notice } from "./notice";
import { Chip, Hint, ICON, Loading, StepActions, StepFrame, TextField, useConfigSave, type Progress } from "./setup-parts";

type Setting = { key: string; label: string; required?: boolean; lookup?: string | null; dependsOn?: string | null; hint?: string | null };
type Descriptor = { kind: string; name: string; description: string; connectionKind: string; connected: boolean; settings: Setting[] };
type Pick = { kind: string; companies: string[]; settings: Record<string, string>; connection: string; mixed?: boolean };
type Choices = { hint?: string; warn?: boolean; options?: { value: string; label: string }[] };
type Loaded = { targets: any; kinds: any; identity: any };

const ICON_18 = { size: 18, strokeWidth: 1.75 };
const KIND_ICONS: Record<string, ComponentType<{ size?: number; strokeWidth?: number }>> = { sharepoint: FolderOpen, blob: Container, docuware: Archive, mail: Mail };

const NOTES: Record<string, string> = {
  sharepoint: "The file goes into the library with its name. Columns of the library are filled from the document once you map them under Outbound.",
  mail: "Each document goes to the customer's address from the export.",
};

export function DestinationStep({ progress, readOnly, asked }: { progress: Progress; readOnly: boolean; asked: string }) {
  const { config } = useConsole();
  const [loaded, setLoaded] = useState<Loaded | null>(null);
  const [error, setError] = useState("");

  useEffect(() => {
    Promise.all([api("/targets"), api("/connections/kinds"), api("/setup/identity")])
      .then(([targets, kinds, identity]) => setLoaded({ targets, kinds, identity }))
      .catch((e: Error) => setError(e.message));
  }, []);

  const entries = sourceEntries(config);
  const typeIds = [...new Set(entries.map((e) => e.type.id as string))];
  const typeId =
    typeIds.find((t) => t === asked) ||
    typeIds.find((id) => entries.some((e) => e.type.id === id && !hasDestination(e.company, config))) ||
    typeIds[0];
  const type = config.documentTypes.find((t: any) => t.id === typeId);
  const companies: string[] = entries.filter((e) => e.type.id === typeId).map((e) => e.company.dataAreaId);

  if (!type) {
    return (
      <StepFrame step={3} progress={progress} readOnly={readOnly} title="Where documents go">
        <Notice kind="warn">No document type takes documents from F&O yet. Set up where F&O hands over first.</Notice>
        <StepActions back={2} />
      </StepFrame>
    );
  }

  const intro = [`The exports are ${typesWord(type)} for ${companiesWord(companies)} ${andList(companies)}.`];
  if (type?.handler === "EInvoice") intro.push("Hive BDC makes the e-invoice each export asks for (XRechnung or ZUGFeRD).");
  intro.push("Choose one place or several; other document types follow later under Outbound.");

  return (
    <StepFrame step={3} progress={progress} readOnly={readOnly} title={`Where should ${typesWord(type)} go?`} description={intro.join(" ")}>
      {typeIds.length > 1 && (
        <nav className="setup-tabs" aria-label="Document types of the set-up">
          {typeIds.map((id) => (
            <Link
              key={id}
              href={stepHref(3, id)}
              className={`setup-tab${id === typeId ? " active" : ""}`}
              aria-current={id === typeId ? "page" : undefined}
            >
              {typesWord(config.documentTypes.find((t: any) => t.id === id))}
            </Link>
          ))}
        </nav>
      )}
      {error ? (
        <>
          <Notice kind="error">{error}</Notice>
          <StepActions back={2} />
        </>
      ) : loaded ? (
        <DestinationForm key={typeId} loaded={loaded} type={type} typeId={typeId} companies={companies} readOnly={readOnly} progress={progress} />
      ) : (
        <Loading text="Reading the places this version can deliver to" />
      )}
    </StepFrame>
  );
}

function DestinationForm({
  loaded,
  type,
  typeId,
  companies,
  readOnly,
  progress,
}: {
  loaded: Loaded;
  type: any;
  typeId: string;
  companies: string[];
  readOnly: boolean;
  progress: Progress;
}) {
  const { config, refresh } = useConsole();
  const router = useRouter();
  const mailReady = Boolean(config.mail && config.mail.enabled && config.mail.from);
  const descriptors: Descriptor[] = [
    ...(loaded.targets?.targets || []).map((d: any) => ({ ...d, settings: d.settings || [] })),
    {
      kind: "mail",
      name: "Mail",
      description: mailReady ? `To the customer, from ${config.mail.from}` : "Needs a mailbox under Connections first.",
      connectionKind: "",
      connected: mailReady,
      settings: [],
    },
  ];
  const offered = descriptors.map((d) => d.kind);
  const [picks, setPicks] = useState<Pick[]>(() => destinationPicks(type, offered));
  const [ready, setReady] = useState<Record<string, boolean>>(() => Object.fromEntries(descriptors.map((d) => [d.kind, Boolean(d.connected)])));
  const [drafts, setDrafts] = useState<Record<string, any>>({});
  const { save, slot, busy } = useConfigSave();

  const problems = destinationProblems(picks, companies, descriptors, ready);

  const connectionFor = (d: Descriptor) => {
    const found = (config.connections || []).find((c: any) => c.kind === d.connectionKind);
    return found ? found.id : uniqueId(config.connections, d.connectionKind || d.kind);
  };

  const toggle = (d: Descriptor, on: boolean) => {
    if (on) {
      setPicks((all) => [...all, { kind: d.kind, companies: [...companies], settings: {}, connection: d.connectionKind ? connectionFor(d) : "" }]);
    } else {
      setPicks((all) => all.filter((p) => p.kind !== d.kind));
    }
  };

  const update = (next: Pick) => setPicks((all) => all.map((p) => (p.kind === next.kind ? next : p)));

  const saveDestinations = () => {
    const body: any = withDestinations(postBody(config), typeId, picks, offered);
    for (const [kind, draft] of Object.entries(drafts)) {
      if (!draft || !picks.some((p) => p.kind === kind) || !ready[kind]) continue;
      if (kind === "sharepoint") {
        body.sharePointAuth = { tenantId: draft.tenantId, clientId: draft.clientId, clientSecret: draft.secret };
      } else {
        body.connections = body.connections.filter((c: any) => c.id !== draft.id);
        body.connections.push({ id: draft.id, kind: draft.kind, settings: { ...draft.settings }, secret: draft.secret });
      }
    }
    void save(body, async () => {
      await refresh();
      router.push(stepHref(4));
    });
  };

  let next = null;
  if (!readOnly) {
    next = (
      <button type="button" className="button primary" disabled={problems.length > 0 || busy} onClick={saveDestinations}>
        {busy && <LoaderCircle className="spin" {...ICON} />}
        Save and continue
      </button>
    );
  } else if (progress.done[2]) {
    next = (
      <Link className="button primary" href={stepHref(4)}>
        Next: test run
        <ArrowRight {...ICON} />
      </Link>
    );
  }

  return (
    <>
      <fieldset className="setup-tiles setup-choose setup-places">
        <legend className="sr-only">Goes to</legend>
        {descriptors.map((d) => {
          const off = d.kind === "mail" && !mailReady;
          const Icon = KIND_ICONS[d.kind] ?? Send;
          return (
            <label key={d.kind} className={`setup-tile${off ? " off" : ""}`}>
              <input
                type="checkbox"
                aria-label={d.name}
                aria-describedby={`place-${d.kind}`}
                checked={picks.some((p) => p.kind === d.kind)}
                disabled={readOnly || off}
                onChange={(e) => toggle(d, e.target.checked)}
              />
              <span className="setup-tile-icon">
                <Icon {...ICON_18} />
              </span>
              <span className="setup-tile-body">
                <strong>{d.name}</strong>
                <span id={`place-${d.kind}`}>{d.description}</span>
              </span>
            </label>
          );
        })}
      </fieldset>

      {descriptors
        .filter((d) => picks.some((p) => p.kind === d.kind))
        .map((d) => (
          <DestinationSection
            key={d.kind}
            d={d}
            pick={picks.find((p) => p.kind === d.kind)!}
            companies={companies}
            loaded={loaded}
            ready={Boolean(ready[d.kind])}
            draft={drafts[d.kind]}
            readOnly={readOnly}
            onPick={update}
            onReady={(value) => setReady((all) => ({ ...all, [d.kind]: value }))}
            onDraft={(draft) => setDrafts((all) => ({ ...all, [d.kind]: draft }))}
          />
        ))}

      {!readOnly && problems.length > 0 && (
        <div className="setup-problems" role="status">
          <strong>Before you can save</strong>
          <ul>
            {problems.map((p) => (
              <li key={p}>{p}</li>
            ))}
          </ul>
        </div>
      )}
      {slot}
      <StepActions back={2} next={next} />
    </>
  );
}

function DestinationSection({
  d,
  pick,
  companies,
  loaded,
  ready,
  draft,
  readOnly,
  onPick,
  onReady,
  onDraft,
}: {
  d: Descriptor;
  pick: Pick;
  companies: string[];
  loaded: Loaded;
  ready: boolean;
  draft: any;
  readOnly: boolean;
  onPick: (pick: Pick) => void;
  onReady: (ready: boolean) => void;
  onDraft: (draft: any) => void;
}) {
  const [choices, setChoices] = useState<Record<string, Choices>>({});
  const sequence = useRef<Record<string, number>>({});
  const committed = useRef<Record<string, string>>({ ...pick.settings });
  const lookups = d.settings.filter((s) => s.lookup);
  const headingId = `dest-${d.kind}-heading`;

  const setChoice = (key: string, value: Choices) => setChoices((all) => ({ ...all, [key]: value }));

  const draftFor = (draftNow: any) => {
    if (d.kind === "sharepoint") {
      return draftNow ? { sharePoint: { tenantId: draftNow.tenantId, clientId: draftNow.clientId, secret: draftNow.secret } } : {};
    }
    if (d.kind === "docuware" && draftNow) {
      return { docuWare: { id: draftNow.id, url: draftNow.settings.Url || "", userName: draftNow.settings.UserName || "", secret: draftNow.secret } };
    }
    return {};
  };

  const load = async (setting: Setting, pickNow: Pick, readyNow: boolean, draftNow: any) => {
    if (readOnly) return;
    const seq = (sequence.current[setting.key] ?? 0) + 1;
    sequence.current[setting.key] = seq;
    if (setting.dependsOn && !String(pickNow.settings[setting.dependsOn] || "").trim()) {
      setChoice(setting.key, { hint: "Fill in the field above first." });
      return;
    }
    if (!readyNow && !(d.kind === "sharepoint" && draftNow)) {
      setChoice(setting.key, { hint: "Connect it first." });
      return;
    }
    setChoice(setting.key, { hint: "Loading" });
    try {
      const answer = await api("/targets/choices", {
        method: "POST",
        body: JSON.stringify({ kind: d.kind, lookup: setting.lookup, connection: pickNow.connection, settings: pickNow.settings, ...draftFor(draftNow) }),
      });
      if (sequence.current[setting.key] !== seq) return;
      if (!answer.ok) throw new Error(answer.message);
      setChoice(setting.key, {
        options: (answer.choices || []).map((c: any) => ({ value: c.value, label: c.detail ? `${c.label} (${c.detail})` : c.label })),
      });
      if (d.kind === "sharepoint" && !readyNow) onReady(true);
    } catch (error) {
      if (sequence.current[setting.key] !== seq) return;
      setChoice(setting.key, { hint: (error as Error).message, warn: true });
      if (d.kind === "sharepoint" && draftNow) onReady(false);
    }
  };

  const loadAll = (pickNow: Pick, readyNow: boolean, draftNow: any) => {
    for (const setting of lookups) void load(setting, pickNow, readyNow, draftNow);
  };

  const loadDependents = (key: string, pickNow: Pick) => {
    for (const other of d.settings.filter((s) => s.dependsOn === key)) void load(other, pickNow, ready, draft);
  };

  useEffect(() => {
    // Once: later loads follow the edits that change what a list depends on.
    loadAll(pick, ready, draft);
  }, []);

  const setValue = (key: string, value: string) => {
    const next = { ...pick, settings: { ...pick.settings, [key]: value }, mixed: false };
    onPick(next);
    return next;
  };

  const connected = (draftNow: any, readyNow: boolean) => {
    onDraft(draftNow);
    onReady(readyNow);
    loadAll(pick, readyNow, draftNow);
  };

  return (
    <section className="panel" aria-labelledby={headingId}>
      <div className="section-heading">
        <div>
          <h2 id={headingId}>{d.name}</h2>
        </div>
        {d.kind !== "mail" && <Chip ok={ready}>{ready ? "Connected" : "Not connected yet"}</Chip>}
      </div>
      <div className="setup-panel-body">
        {d.kind !== "mail" && !d.connected && !readOnly && <ConnectForm d={d} pick={pick} loaded={loaded} onConnected={connected} />}
        {pick.mixed && (
          <p className="setup-note">The companies have different values here and keep them. A value you change applies to every ticked company.</p>
        )}
        {d.settings.length > 0 && (
          <div className="setup-fields">
            {d.settings.map((setting) => {
              const id = `dest-${d.kind}-${setting.key}`;
              const current = pick.settings[setting.key] || "";
              if (setting.lookup) {
                const state = choices[setting.key] ?? {};
                const options = state.options ?? [];
                const shown = options.find((o) => o.value === current)?.label || current;
                return (
                  <div className="field" key={setting.key}>
                    <label id={`${id}-label`} htmlFor={id}>
                      {setting.label}
                    </label>
                    <Dropdown
                      id={id}
                      className="record-select"
                      aria-labelledby={`${id}-label`}
                      disabled={readOnly || !state.options}
                      value={shown}
                      selectedOptions={current ? [current] : []}
                      placeholder={setting.required === false ? "(none)" : "Choose"}
                      onOptionSelect={(_, data) => {
                        const next = setValue(setting.key, data.optionValue ?? "");
                        loadDependents(setting.key, next);
                      }}
                    >
                      {[
                        ...(setting.required === false ? [{ value: "", label: "(none)" }] : []),
                        ...options,
                      ].map((o) => (
                        <Option key={o.value || "none"} value={o.value} text={o.label}>
                          {o.label}
                        </Option>
                      ))}
                    </Dropdown>
                    {state.hint && <Hint warn={state.warn}>{state.hint}</Hint>}
                    {!state.hint && setting.hint && <Hint>{setting.hint}</Hint>}
                  </div>
                );
              }
              return (
                <SettingText
                  key={setting.key}
                  id={id}
                  setting={setting}
                  initial={current}
                  disabled={readOnly}
                  onChange={(value) => setValue(setting.key, value)}
                  onCommit={(value) => {
                    if (committed.current[setting.key] === value) return;
                    committed.current[setting.key] = value;
                    loadDependents(setting.key, { ...pick, settings: { ...pick.settings, [setting.key]: value } });
                  }}
                />
              );
            })}
          </div>
        )}
        <fieldset className="setup-companies">
          <legend>For companies</legend>
          {companies.map((company) => (
            <label key={company} className="toggle-pill">
              <input
                type="checkbox"
                checked={pick.companies.includes(company)}
                disabled={readOnly}
                onChange={(e) =>
                  onPick({
                    ...pick,
                    companies: e.target.checked ? [...pick.companies, company] : pick.companies.filter((c) => c !== company),
                  })
                }
              />
              {company}
            </label>
          ))}
        </fieldset>
        {NOTES[d.kind] && <p className="setup-note">{NOTES[d.kind]}</p>}
      </div>
    </section>
  );
}

function SettingText({
  id,
  setting,
  initial,
  disabled,
  onChange,
  onCommit,
}: {
  id: string;
  setting: Setting;
  initial: string;
  disabled: boolean;
  onChange: (value: string) => void;
  onCommit: (value: string) => void;
}) {
  const [text, setText] = useState(initial);
  return (
    <TextField
      id={id}
      label={setting.label}
      value={text}
      hint={setting.hint || undefined}
      disabled={disabled}
      onChange={(value) => {
        setText(value);
        onChange(value.trim());
      }}
      onBlur={() => onCommit(text.trim())}
    />
  );
}

function ConnectForm({
  d,
  pick,
  loaded,
  onConnected,
}: {
  d: Descriptor;
  pick: Pick;
  loaded: Loaded;
  onConnected: (draft: any, ready: boolean) => void;
}) {
  const { config } = useConsole();
  const [result, setResult] = useState<{ ok?: boolean; text: string } | null>(null);
  const [busy, setBusy] = useState(false);
  const auth = config.sharePointAuth || {};
  const [tenant, setTenant] = useState<string>(auth.tenantId || loaded.identity?.tenantId || "");
  const [client, setClient] = useState<string>(auth.clientId || "");
  const kind = d.kind === "sharepoint" ? null : (loaded.kinds?.kinds || []).find((k: any) => k.kind === d.connectionKind);
  const stored = (config.connections || []).find((c: any) => c.id === pick.connection);
  const [values, setValues] = useState<Record<string, string>>(() =>
    Object.fromEntries(((kind && kind.settings) || []).map((s: any) => [s.key, (stored && stored.settings && stored.settings[s.key]) || ""])),
  );
  const [secret, setSecret] = useState("");
  const prefix = `connect-${d.kind}`;

  if (d.kind === "sharepoint") {
    return (
      <div className="setup-connect">
        <p className="muted">The SharePoint app your IT created with the Balsoft script. Its secret is kept in this instance&apos;s Key Vault.</p>
        <div className="setup-fields">
          <TextField id={`${prefix}-tenant`} label="Tenant ID" value={tenant} onChange={setTenant} />
          <TextField
            id={`${prefix}-client`}
            label="Client ID"
            value={client}
            placeholder="00000000-0000-0000-0000-000000000000"
            onChange={setClient}
          />
          <TextField
            id={`${prefix}-secret`}
            label="Client secret"
            type="password"
            value={secret}
            placeholder={auth.secretSet ? "(stored, leave empty to keep)" : ""}
            onChange={setSecret}
          />
        </div>
        <div className="setup-inline">
          <button
            type="button"
            className="button secondary"
            onClick={() => {
              setResult({ text: "Enter the site address below; the libraries load when the app can sign in." });
              onConnected({ tenantId: tenant.trim(), clientId: client.trim(), secret }, false);
            }}
          >
            <PlugZap {...ICON} />
            Use this app
          </button>
          <span aria-live="polite">{result && <Hint>{result.text}</Hint>}</span>
        </div>
      </div>
    );
  }

  if (!kind) return <p className="warn-text">{`This version cannot connect ${d.name} here yet.`}</p>;

  const test = async () => {
    setBusy(true);
    const draft = {
      id: pick.connection,
      kind: kind.kind,
      settings: Object.fromEntries(Object.entries(values).map(([k, v]) => [k, v.trim()])),
      secret,
    };
    try {
      const answer = await api("/connections/test", { method: "POST", body: JSON.stringify(draft) });
      setResult({ ok: Boolean(answer.ok), text: answer.message });
      onConnected(answer.ok ? draft : null, Boolean(answer.ok));
    } catch (error) {
      setResult({ ok: false, text: (error as Error).message });
      onConnected(null, false);
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="setup-connect">
      <p className="muted">{kind.description}</p>
      <div className="setup-fields">
        {(kind.settings || []).map((s: any) => (
          <TextField
            key={s.key}
            id={`${prefix}-${s.key}`}
            label={s.label}
            value={values[s.key] ?? ""}
            hint={s.hint || undefined}
            onChange={(value) => setValues((all) => ({ ...all, [s.key]: value }))}
          />
        ))}
        <TextField
          id={`${prefix}-secret`}
          label={kind.secretLabel || "Secret"}
          type="password"
          value={secret}
          placeholder={stored && stored.hasSecret ? "(stored, leave empty to keep)" : ""}
          hint="Kept in this instance's Key Vault, never shown again."
          onChange={setSecret}
        />
      </div>
      <div className="setup-inline">
        <button type="button" className="button secondary" disabled={busy} onClick={() => void test()}>
          {busy ? <LoaderCircle className="spin" {...ICON} /> : <PlugZap {...ICON} />}
          Test the connection
        </button>
        <span aria-live="polite" className="setup-check-line">
          {result && result.ok !== undefined && <Chip ok={result.ok}>{result.text}</Chip>}
        </span>
      </div>
    </div>
  );
}
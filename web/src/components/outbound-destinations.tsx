"use client";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useEffect, useRef, useState } from "react";
import { Plus, Send } from "lucide-react";
import { DataGrid, type GridColumn } from "./ui/data-grid";
import { Notice } from "./notice";
import { change, draft, useDraft } from "./save-bar";
import { Chip, CommitInput, ConfirmStrip, ConnectionSelect, Field, ICON, Switch, SwitchField, typePath, useAnswer } from "./outbound-parts";
import { api } from "@/lib/api";
import * as X from "@/lib/exportlogic";

// Goes to (board W10, the second half of export.js companyScreen) and one destination's form (board W11, export.js
// destinationScreen): where the file goes, the columns or index fields filled from the document with the value of the
// last document beside each, and Test, which signs in and checks that every mapped field exists.

const same = (a: unknown, b: unknown) => String(a ?? "").toLowerCase() === String(b ?? "").toLowerCase();

type DestRow = { id: string; kind: string; name: string; enabled: boolean; known: boolean; summary: string; mail?: boolean };

export function DestinationsTab({ type, entry, descriptors, ro }: { type: any; entry: any; descriptors: any[]; ro: boolean }) {
  const router = useRouter();
  const state = useDraft();
  const [picking, setPicking] = useState(false);
  const [confirm, setConfirm] = useState<DestRow | null>(null);
  const company = entry.dataAreaId;
  const rows = X.destinationRows(entry, descriptors);
  const mail = state.body.mail;
  const mailReady = Boolean(mail && mail.enabled && mail.from);
  const present = new Set(rows.map((r) => r.kind));
  const offered = descriptors.filter((d) => !present.has(d.kind));
  const gridRows: DestRow[] = [
    ...rows.map((r) => ({ ...r, id: r.kind })),
    { id: "#mail", kind: "mail", name: "Mail", enabled: entry.mail === true, known: true, summary: "", mail: true },
  ];
  const columns: GridColumn<DestRow>[] = [
    {
      key: "destination",
      label: "Destination",
      render: (r) => (
        <span className="cell-stack">
          {r.mail ? (
            <strong>Mail</strong>
          ) : r.known ? (
            <Link className="cell-link" href={typePath(type.id, company, "to", r.kind)}>
              {r.name}
            </Link>
          ) : (
            <strong>{r.name}</strong>
          )}
          <span className="cell-secondary wrap-cell">
            {r.mail
              ? mailReady
                ? `To the recipient the ERP names in the document, from ${mail.from}.`
                : "Document mail is not connected yet. Set it up under Connections."
              : r.summary || "Not set up yet"}
          </span>
        </span>
      ),
    },
    {
      key: "on",
      label: "On",
      width: 80,
      render: (r) =>
        r.mail ? (
          <Switch
            label="Mail on"
            checked={entry.mail === true}
            disabled={ro || (!mailReady && entry.mail !== true)}
            onChange={(on) => change((d) => X.setMail(d, type.id, company, on))}
          />
        ) : r.known ? (
          <Switch
            label={`${r.name} on`}
            checked={r.enabled}
            disabled={ro}
            onChange={(on) => change((d) => X.setDestinationEnabled(d, type.id, company, r.kind, on))}
          />
        ) : null,
    },
  ];
  return (
    <section className="panel" aria-labelledby="to-heading">
      <div className="section-heading">
        <h2 id="to-heading" className="caps">
          Goes to
        </h2>
        {!ro && (
          <button type="button" className="button secondary" aria-expanded={picking} onClick={() => setPicking(!picking)}>
            {picking ? (
              "Close"
            ) : (
              <>
                <Plus {...ICON} />
                Add a destination
              </>
            )}
          </button>
        )}
      </div>
      {picking && !ro && (
        <div className="panel-inset">
          {offered.length ? (
            <div className="setup-tiles">
              {offered.map((d) => (
                <button
                  key={d.kind}
                  type="button"
                  className="setup-tile tile-button"
                  onClick={() => {
                    const connection = state.body.connections.find((c: any) => c.kind === d.connectionKind);
                    change((dr) => X.addDestination(dr, type.id, company, d.kind, connection ? connection.id : ""));
                    setPicking(false);
                    router.push(typePath(type.id, company, "to", d.kind));
                  }}
                >
                  <span className="setup-tile-icon">
                    <Send {...ICON} aria-hidden="true" />
                  </span>
                  <span className="setup-tile-body">
                    <strong>{d.name}</strong>
                    <span>{d.description || ""}</span>
                  </span>
                </button>
              ))}
            </div>
          ) : (
            <p className="muted small">Every destination of this version is set up for this company already.</p>
          )}
        </div>
      )}
      {confirm && (
        <div className="panel-inset">
          <ConfirmStrip
            text={`Remove ${confirm.name}? Documents already delivered stay there.`}
            onKeep={() => setConfirm(null)}
            onRemove={() => {
              change((d) => X.removeDestination(d, type.id, company, confirm.kind));
              setConfirm(null);
            }}
          />
        </div>
      )}
      <DataGrid
        label={`Destinations of Company ${company}`}
        columns={columns}
        rows={gridRows}
        rowClassName={(r) => (confirm?.id === r.id ? "bdc-row-removing" : undefined)}
        actionsWidth="110px"
        actions={
          ro
            ? undefined
            : (r) =>
                r.mail ? null : (
                  <button type="button" className="text-button quiet-link" aria-label={`Remove ${r.name}`} onClick={() => setConfirm(r)}>
                    Remove
                  </button>
                )
        }
      />
    </section>
  );
}

type MapRow = { uid: number; field: string; template: string };
type Choice = { value: string; label: string; detail?: string };
type Hint = { text: string; warn?: boolean };

// The API keeps its name GET /export/sample; tests/wording.test.ts only looks for page links named Export.
const SAMPLE = "/export/sample";

let uids = 0;

export function DestinationForm({ type, entry, kind, descriptors, ro }: { type: any; entry: any; kind: string; descriptors: any[]; ro: boolean }) {
  const router = useRouter();
  const company = entry.dataAreaId;
  const back = typePath(type.id, company, "to");
  const d = descriptors.find((x) => x.kind === kind);
  const present = X.hasDestination(entry, kind);
  const sample = useAnswer<{ fields: Record<string, string>; output: string; message: string }>(
    d && present ? `${SAMPLE}?type=${encodeURIComponent(type.id)}&company=${encodeURIComponent(company)}` : null,
  );

  // After Discard, or from an old link, the destination is not in the draft: opening its form would add it again.
  useEffect(() => {
    if (d && !present) router.replace(back);
  }, [d, present, back, router]);

  if (!d) return <Notice kind="warn">This destination was set up with a newer version of Hive BDC and is kept as it is.</Notice>;
  if (!present) return null;
  if (sample.error) return <Notice kind="error">{sample.error}</Notice>;
  if (!sample.data) return <p className="muted">Reading the last document.</p>;
  return <DestinationEditor type={type} entry={entry} d={d} sample={sample.data} back={back} ro={ro} />;
}

function DestinationEditor({ type, entry, d, sample, back, ro }: { type: any; entry: any; d: any; sample: any; back: string; ro: boolean }) {
  const router = useRouter();
  const company = entry.dataAreaId;
  const kind = d.kind;
  const form = X.destinationForm(entry, kind);
  const fields: Record<string, string> = sample.fields || {};
  const hasFields = Object.keys(fields).length > 0;
  const [rows, setRows] = useState<MapRow[]>(() =>
    Object.entries(form.fields).map(([field, template]) => ({ uid: ++uids, field, template: String(template) })),
  );
  const rowsRef = useRef(rows);
  rowsRef.current = rows;
  const [typing, setTyping] = useState<Record<string, string>>({});
  const [options, setOptions] = useState<Record<string, Choice[]>>({});
  const [hints, setHints] = useState<Record<string, Hint>>({});
  const [fieldChoices, setFieldChoices] = useState<Choice[]>([]);
  const [own, setOwn] = useState<Record<number, boolean>>({});
  const [test, setTest] = useState<"busy" | { ok: boolean; text: string } | null>(null);
  const [removing, setRemoving] = useState(false);

  const current = () => {
    const t = draft.body?.documentTypes.find((x: any) => same(x.id, type.id));
    const e = t?.companies.find((c: any) => same(c.dataAreaId, company));
    return e ? X.destinationForm(e, kind) : null;
  };
  const apply = (patch: { settings?: Record<string, string>; connection?: string; enabled?: boolean }, nextRows: MapRow[] = rowsRef.current) => {
    const f = current();
    if (!f) return;
    if (patch.settings) Object.assign(f.settings, patch.settings);
    if (patch.connection !== undefined) f.connection = patch.connection;
    if (patch.enabled !== undefined) f.enabled = patch.enabled;
    f.fields = Object.fromEntries(nextRows.filter((r) => r.field && r.template).map((r) => [r.field, r.template]));
    change((dr) => X.applyDestination(dr, type.id, company, f));
  };
  const request = (lookup: string) => {
    const f = current() || form;
    return { kind, lookup, connection: f.connection, settings: f.settings };
  };
  const hint = (key: string, value: Hint) => setHints((h) => ({ ...h, [key]: value }));

  const loadChoices = async (s: any) => {
    if (!s.lookup) return;
    const f = current() || form;
    if (s.dependsOn && !String(f.settings[s.dependsOn] || "").trim()) {
      hint(s.key, { text: "Fill in the field before first." });
      return;
    }
    hint(s.key, { text: "Loading" });
    try {
      const answer = await api("/targets/choices", { method: "POST", body: JSON.stringify(request(s.lookup)) });
      if (!answer.ok) throw new Error(answer.message);
      setOptions((o) => ({ ...o, [s.key]: answer.choices || [] }));
      hint(s.key, { text: "" });
    } catch (error) {
      hint(s.key, { text: (error as Error).message, warn: true });
    }
  };

  const loadFields = async (): Promise<{ error: string | null; choices: Choice[] }> => {
    if (!d.fieldsLookup) return { error: null, choices: [] };
    try {
      const answer = await api("/targets/choices", { method: "POST", body: JSON.stringify(request(d.fieldsLookup)) });
      if (!answer.ok) throw new Error(answer.message);
      setFieldChoices(answer.choices || []);
      return { error: null, choices: answer.choices || [] };
    } catch (error) {
      setFieldChoices([]);
      return { error: (error as Error).message, choices: [] };
    }
  };

  const reloadAll = () => {
    for (const s of d.settings.filter((x: any) => x.lookup)) void loadChoices(s);
    if (d.fieldsLookup) void loadFields();
  };
  useEffect(() => {
    reloadAll();
    // Once per opened destination; later loads follow the changes that need them.
  }, []);

  const settingsNow = { ...form.settings, ...typing };
  const folderLine =
    kind === "sharepoint"
      ? hasFields
        ? `With the last document: ${[settingsNow.Library, X.folderPreview(settingsNow.Folder, fields)].filter(Boolean).join("/")}/${
            sample.output || `${fields.documentNumber || fields.DocumentNumber || "document"}.pdf`
          }`
        : sample.message
      : null;

  const connectionField =
    d.connectionKind && d.connectionKind !== "sharepoint" ? (
      <Field label="Connection" htmlFor="dest-connection">
        <ConnectionSelect
          id="dest-connection"
          kind={d.connectionKind}
          value={form.connection}
          disabled={ro}
          onChange={(v) => {
            apply({ connection: v });
            reloadAll();
          }}
        />
      </Field>
    ) : null;

  const settingControl = (s: any) => {
    const id = `dest-${s.key}`;
    const value = form.settings[s.key] || "";
    const shownHint = hints[s.key]?.text || (!s.lookup ? s.hint : "");
    const dependents = () => d.settings.filter((x: any) => x.dependsOn === s.key);
    let control;
    if (s.lookup) {
      const loaded = options[s.key];
      const list: Choice[] = loaded
        ? [
            { value: "", label: s.required === false ? "(none)" : "Choose" },
            ...loaded.map((c) => ({ value: c.value, label: c.detail ? `${c.label} (${c.detail})` : c.label })),
          ]
        : [{ value, label: form.settings[`${s.key}Name`] || value || (s.required === false ? "(none)" : "Choose") }];
      control = (
        <select
          id={id}
          value={list.some((o) => o.value === value) ? value : ""}
          disabled={ro}
          onChange={(e) => {
            const v = e.target.value;
            const label = e.target.selectedOptions[0]?.textContent || "";
            const patch: Record<string, string> = { [s.key]: v };
            if (label && label !== v) patch[`${s.key}Name`] = label;
            apply({ settings: patch });
            for (const other of dependents()) void loadChoices(other);
            if (d.fieldsLookup) void loadFields();
          }}
        >
          {list.map((o) => (
            <option key={o.value} value={o.value}>
              {o.label}
            </option>
          ))}
        </select>
      );
    } else {
      control = (
        <CommitInput
          id={id}
          value={value}
          disabled={ro}
          onLive={(v) => setTyping((t) => ({ ...t, [s.key]: v.trim() }))}
          onCommit={(v) => {
            setTyping((t) => {
              const next = { ...t };
              delete next[s.key];
              return next;
            });
            apply({ settings: { [s.key]: v } });
            for (const other of dependents()) void loadChoices(other);
          }}
        />
      );
    }
    return (
      <div className="field" key={s.key}>
        <label htmlFor={id}>{s.label}</label>
        {control}
        <span className={`field-hint${hints[s.key]?.warn ? " warn-text" : ""}`}>{shownHint}</span>
      </div>
    );
  };

  const choices = X.templateChoices(fields);
  const head = kind === "sharepoint" ? "Column" : "Field";
  const setRow = (uid: number, patch: Partial<MapRow>) => {
    const next = rowsRef.current.map((r) => (r.uid === uid ? { ...r, ...patch } : r));
    setRows(next);
    rowsRef.current = next;
    return next;
  };
  const mapColumns: GridColumn<MapRow & { id: string }>[] = [
    {
      key: "field",
      label: head,
      width: 240,
      render: (row) => {
        const known = fieldChoices.some((c) => c.value === row.field);
        return (
          <select
            aria-label={`${head} ${rows.findIndex((r) => r.uid === row.uid) + 1}`}
            className={known || !row.field || fieldChoices.length === 0 ? undefined : "input-bad"}
            value={row.field}
            disabled={ro}
            onChange={(e) => apply({}, setRow(row.uid, { field: e.target.value }))}
          >
            <option value="">Choose</option>
            {row.field && !known && <option value={row.field}>{row.field}</option>}
            {fieldChoices.map((c) => (
              <option key={c.value} value={c.value}>
                {c.label}
              </option>
            ))}
          </select>
        );
      },
    },
    {
      key: "template",
      label: "Filled from the document",
      width: 300,
      render: (row) => {
        const custom = Boolean(row.template) && !choices.some((c) => c.value === row.template);
        const open = custom || own[row.uid];
        const n = rows.findIndex((r) => r.uid === row.uid) + 1;
        return (
          <span className="template-cell">
            <span className="muted" aria-hidden="true">
              ←
            </span>
            <span className="stack-tight">
              <select
                aria-label={`Filled from the document ${n}`}
                value={open ? "__custom" : row.template}
                disabled={ro}
                onChange={(e) => {
                  if (e.target.value === "__custom") {
                    setOwn((o) => ({ ...o, [row.uid]: true }));
                    return;
                  }
                  setOwn((o) => ({ ...o, [row.uid]: false }));
                  apply({}, setRow(row.uid, { template: e.target.value }));
                }}
              >
                <option value="">Choose</option>
                {choices.map((c) => (
                  <option key={c.value} value={c.value}>
                    {c.label}
                  </option>
                ))}
                <option value="__custom">Own text with {"{fields}"}</option>
              </select>
              {open && (
                <CommitInput
                  aria-label={`Own text ${n}`}
                  value={row.template}
                  placeholder="{documentNumber} {issueDate:yyyy}"
                  disabled={ro}
                  autoFocus={Boolean(own[row.uid]) && !custom}
                  onCommit={(v) => apply({}, setRow(row.uid, { template: v }))}
                />
              )}
            </span>
          </span>
        );
      },
    },
    {
      key: "value",
      label: "Last document",
      render: (row) => {
        const known = fieldChoices.some((c) => c.value === row.field);
        return row.field && !known && fieldChoices.length ? (
          <span className="warn-text small">
            {kind === "sharepoint" ? "The library" : d.name} has no field &quot;{row.field}&quot; yet
          </span>
        ) : (
          <span className="wrap-cell">{hasFields ? X.templatePreview(row.template, fields) || "(empty)" : ""}</span>
        );
      },
    },
  ];

  const runTest = async () => {
    setTest("busy");
    const loaded = await loadFields();
    if (loaded.error) {
      setTest({ ok: false, text: loaded.error });
      return;
    }
    setTest(
      X.fieldTestLine(
        rowsRef.current.map((r) => r.field),
        loaded.choices,
      ),
    );
  };

  return (
    <>
      <section className="panel form-panel" aria-labelledby="dest-heading">
        <div className="section-heading">
          <h2 id="dest-heading">{d.name}</h2>
          <span className="row-actions">
            <Chip ok={Boolean(d.connected)}>{d.connected ? "Connected" : "Not connected yet: set it up under Connections"}</Chip>
            <SwitchField text="On" checked={form.enabled} disabled={ro} onChange={(on) => apply({ enabled: on })} />
          </span>
        </div>
        {(d.settings.length > 0 || connectionField) && (
          <div className="form-body">
            <h3 className="subheading">{kind === "sharepoint" ? "Where the file goes" : "Settings"}</h3>
            <div className="form-row-3">
              {connectionField}
              {d.settings.map(settingControl)}
            </div>
            {folderLine && <span className="muted small">{folderLine}</span>}
          </div>
        )}
      </section>
      {d.fieldsLookup && (
        <section className="panel" aria-labelledby="columns-heading">
          <div className="section-heading">
            <div>
              <h2 id="columns-heading">{kind === "sharepoint" ? "Columns of the library" : `Fields of ${d.name}`}</h2>
              <p>{hasFields ? "Pick a field and what fills it. The right side shows the value from the last document." : sample.message}</p>
            </div>
            {!ro && (
              <button
                type="button"
                className="button secondary"
                onClick={() => {
                  const next = [...rowsRef.current, { uid: ++uids, field: "", template: "" }];
                  setRows(next);
                  rowsRef.current = next;
                }}
              >
                <Plus {...ICON} />
                Add a column
              </button>
            )}
          </div>
          <DataGrid
            label={kind === "sharepoint" ? "Columns of the library" : `Fields of ${d.name}`}
            columns={mapColumns}
            rows={rows.map((r) => ({ ...r, id: String(r.uid) }))}
            actionsWidth="110px"
            actions={
              ro
                ? undefined
                : (row) => (
                    <button
                      type="button"
                      className="text-button quiet-link"
                      aria-label={`Remove ${head.toLowerCase()} ${rows.findIndex((r) => r.uid === row.uid) + 1}`}
                      onClick={() => {
                        const next = rowsRef.current.filter((r) => r.uid !== row.uid);
                        setRows(next);
                        rowsRef.current = next;
                        apply({}, next);
                      }}
                    >
                      Remove
                    </button>
                  )
            }
            empty={<p className="empty-note grid-empty">Nothing mapped yet. Add a column fills one from the document.</p>}
          />
          {!ro && (
            <div className="form-body">
              <div className="check-row-actions">
                <button type="button" className="button secondary" onClick={() => void runTest()}>
                  Test
                </button>
                <span className="muted small">Signs in, reads the fields and checks that every mapped one exists. Nothing is sent.</span>
              </div>
              <div aria-live="polite">
                {test === "busy" ? <span className="muted">Signing in</span> : test ? <Chip ok={test.ok}>{test.text}</Chip> : null}
              </div>
            </div>
          )}
        </section>
      )}
      {!ro && (
        <div className="remove-line">
          {removing ? (
            <ConfirmStrip
              text={`Remove ${d.name} for company ${company}? Documents already delivered stay there.`}
              onKeep={() => setRemoving(false)}
              onRemove={() => {
                change((dr) => X.removeDestination(dr, type.id, company, kind));
                router.push(back);
              }}
            />
          ) : (
            <button type="button" className="text-button quiet-link" onClick={() => setRemoving(true)}>
              Remove this destination
            </button>
          )}
        </div>
      )}
    </>
  );
}
"use client";
import { useRouter } from "next/navigation";
import { useEffect, useRef, useState } from "react";
import Link from "next/link";
import { ArrowLeft, Plus, Send, Trash2 } from "lucide-react";
import { DataGrid, StatusPill, type GridColumn } from "./ui/data-grid";
import { Notice } from "./notice";
import { change, draft, useDraft } from "./save-bar";
import { BdcDialog, CommitInput, ConnectionSelect, Field, ICON, RemoveLine, Result, RowToggle, typePath, useAnswer } from "./outbound-parts";
import { api } from "@/lib/api";
import * as X from "@/lib/exportlogic";

// Goes to (board W10, the second half of export.js companyScreen) and one destination's form (board W11, export.js
// destinationScreen): where the file goes, the columns or index fields filled from the document with the value of the
// last document beside each, and Test, which signs in and checks that every mapped field exists. A destination is
// added from the kind tiles of a dialog and removed at the end of its own form.

const same = (a: unknown, b: unknown) => String(a ?? "").toLowerCase() === String(b ?? "").toLowerCase();

type DestRow = { id: string; kind: string; name: string; enabled: boolean; known: boolean; summary: string; mail?: boolean };

export function DestinationsTab({
  type,
  entry,
  descriptors,
  ro,
  onAdd,
}: {
  type: any;
  entry: any;
  descriptors: any[];
  ro: boolean;
  onAdd: () => void;
}) {
  const state = useDraft();
  const company = entry.dataAreaId;
  const rows = X.destinationRows(entry, descriptors);
  const mail = state.body.mail;
  const mailReady = Boolean(mail && mail.enabled && mail.from);
  const gridRows: DestRow[] = [
    ...rows.map((r) => ({ ...r, id: r.kind })),
    { id: "#mail", kind: "mail", name: "Mail", enabled: entry.mail === true, known: true, summary: "", mail: true },
  ];
  const columns: GridColumn<DestRow>[] = [
    {
      key: "destination",
      label: "Destination",
      width: 240,
      render: (r) =>
        r.mail || !r.known ? (
          <span className="cell-strong">{r.name}</span>
        ) : (
          <Link className="cell-link" href={typePath(type.id, company, "to", r.kind)}>
            {r.name}
          </Link>
        ),
    },
    {
      key: "where",
      label: "Where",
      render: (r) =>
        r.mail ? (
          <div className="mail-where">
            {mailReady ? (
              <span className="wrap-cell">To the recipient in the document, from {mail.from}</span>
            ) : (
              <span className="cell-muted wrap-cell">
                Document mail is not connected yet. <Link href="/connections/">Connections</Link>
              </span>
            )}
            <label className="mail-cc">
              <span className="cell-strong">Cc</span>
              <CommitInput
                aria-label="Cc addresses of this company"
                value={(entry.mailCc || []).join(", ")}
                readOnly={ro}
                onCommit={(text) => change((d) => X.setMailCc(d, type.id, company, text))}
              />
            </label>
            <span className="cell-muted wrap-cell">Gets every mail of this company as a copy, from the Live ERP and from test systems. Comma between addresses.</span>
          </div>
        ) : r.summary ? (
          <span className="wrap-cell">{r.summary}</span>
        ) : (
          <span className="cell-muted">Not set up yet</span>
        ),
    },
    {
      key: "on",
      label: "State",
      width: 110,
      render: (r) =>
        r.mail ? (
          <RowToggle
            label="On"
            name="Mail on"
            checked={entry.mail === true}
            disabled={ro || (!mailReady && entry.mail !== true)}
            onChange={(on) => change((d) => X.setMail(d, type.id, company, on))}
          />
        ) : r.known ? (
          <RowToggle
            label="On"
            name={`${r.name} on`}
            checked={r.enabled}
            disabled={ro}
            onChange={(on) => change((d) => X.setDestinationEnabled(d, type.id, company, r.kind, on))}
          />
        ) : null,
    },
  ];
  return (
    <>
      <section className="panel" aria-labelledby="to-heading">
        <div className="section-heading">
          <h2 id="to-heading">Goes to</h2>
          {!ro && (
            <button type="button" className="button secondary" onClick={onAdd}>
              <Plus {...ICON} aria-hidden="true" />
              Add a destination
            </button>
          )}
        </div>
        <DataGrid label={`Destinations of Company ${company}`} columns={columns} rows={gridRows} />
      </section>
      <BeforeDelivery type={type} entry={entry} ro={ro} />
    </>
  );
}

type StepRow = { id: string };

/** Compress PDF, the step between producing the file and delivering it (same words as the Windows app). */
function BeforeDelivery({ type, entry, ro }: { type: any; entry: any; ro: boolean }) {
  const company = entry.dataAreaId;
  const columns: GridColumn<StepRow>[] = [
    { key: "step", label: "Step", width: 240, render: () => <span className="cell-strong">Compress PDF</span> },
    {
      key: "what",
      label: "What it does",
      render: () => (
        <span className="wrap-cell">
          Smaller file, same look: fonts keep only the letters used, images above 300 ppi go to 200 ppi.
          <span className="cell-muted"> E-invoices: applies to ZUGFeRD PDFs. Signed and PDF/A files stay as they are.</span>
        </span>
      ),
    },
    {
      key: "on",
      label: "State",
      width: 110,
      render: () => (
        <RowToggle
          label="On"
          name="Compress PDF"
          checked={entry.compressPdf === true}
          disabled={ro}
          onChange={(on) => change((d) => X.setCompressPdf(d, type.id, company, on))}
        />
      ),
    },
  ];
  return (
    <section className="panel" aria-labelledby="before-heading">
      <div className="section-heading">
        <h2 id="before-heading">Before delivery</h2>
      </div>
      <DataGrid label={`Before delivery of Company ${company}`} columns={columns} rows={[{ id: "#compress" }]} />
    </section>
  );
}

/** The destination kinds this version offers and the company does not have yet, as tiles; one click adds it. */
export function AddDestinationDialog({ type, entry, descriptors, onClose }: { type: any; entry: any; descriptors: any[]; onClose: () => void }) {
  const router = useRouter();
  const state = useDraft();
  const company = entry.dataAreaId;
  const present = new Set(X.destinationRows(entry, descriptors).map((r) => r.kind));
  const offered = descriptors.filter((d) => !present.has(d.kind));
  return (
    <BdcDialog
      title="Add a destination"
      subtitle={`Company ${company}`}
      onClose={onClose}
      footer={
        <button type="button" className="button secondary" onClick={onClose}>
          Cancel
        </button>
      }
    >
      {offered.length ? (
        <div className="setup-tiles bdc-tiles">
          {offered.map((d) => (
            <button
              key={d.kind}
              type="button"
              className="setup-tile tile-button"
              onClick={() => {
                const connection = state.body.connections.find((c: any) => c.kind === d.connectionKind);
                change((dr) => X.addDestination(dr, type.id, company, d.kind, connection ? connection.id : ""));
                onClose();
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
        <p className="muted">Every destination of this version is set up for this company.</p>
      )}
    </BdcDialog>
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
      const before = d.settings.find((x: any) => x.key === s.dependsOn);
      hint(s.key, { text: `Fill in ${before ? before.label : s.dependsOn} first.` });
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
            <ArrowLeft size={14} strokeWidth={1.75} className="template-arrow" aria-hidden="true" />
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
          <span className="warn-text">
            {kind === "sharepoint" ? "The library" : d.name} has no field &quot;{row.field}&quot; yet
          </span>
        ) : (
          <span className="wrap-cell">{hasFields ? X.templatePreview(row.template, fields) || <span className="cell-muted">Empty</span> : ""}</span>
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

  const removeRow = (uid: number) => {
    const next = rowsRef.current.filter((r) => r.uid !== uid);
    setRows(next);
    rowsRef.current = next;
    apply({}, next);
  };

  return (
    <>
      <section className="panel" aria-labelledby="dest-heading">
        <div className="section-heading">
          <h2 id="dest-heading">{d.name}</h2>
          <span className="heading-tools">
            <StatusPill tone={d.connected ? "green" : "amber"}>{d.connected ? "Connected" : "Not connected"}</StatusPill>
            <RowToggle label="On" name={`${d.name} on`} checked={form.enabled} disabled={ro} large onChange={(on) => apply({ enabled: on })} />
          </span>
        </div>
        <div className="form-body">
          {!d.connected && (
            <Notice kind="warn">
              Set up its connection under <Link href="/connections/">Connections</Link> first.
            </Notice>
          )}
          {(d.settings.length > 0 || connectionField) && (
            <div className="form-row-3">
              {connectionField}
              {d.settings.map(settingControl)}
            </div>
          )}
          {folderLine && <span className="muted small">{folderLine}</span>}
        </div>
      </section>
      {d.fieldsLookup && (
        <section className="panel" aria-labelledby="columns-heading">
          <div className="section-heading">
            <h2 id="columns-heading">{kind === "sharepoint" ? "Columns of the library" : `Fields of ${d.name}`}</h2>
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
                <Plus {...ICON} aria-hidden="true" />
                {kind === "sharepoint" ? "Add a column" : "Add a field"}
              </button>
            )}
          </div>
          {!hasFields && sample.message && <p className="panel-line muted small">{sample.message}</p>}
          <DataGrid
            label={kind === "sharepoint" ? "Columns of the library" : `Fields of ${d.name}`}
            columns={mapColumns}
            rows={rows.map((r) => ({ ...r, id: String(r.uid) }))}
            actionsWidth="56px"
            actions={
              ro
                ? undefined
                : (row) => {
                    const n = rows.findIndex((r) => r.uid === row.uid) + 1;
                    return (
                      <button
                        type="button"
                        className="icon-button small danger"
                        aria-label={`Remove ${head.toLowerCase()} ${n}`}
                        title="Remove"
                        onClick={() => removeRow(row.uid)}
                      >
                        <Trash2 {...ICON} />
                      </button>
                    );
                  }
            }
            empty={<p className="empty-note grid-empty">Nothing mapped yet.</p>}
          />
          {!ro && (
            <div className="panel-foot">
              <button
                type="button"
                className="button secondary"
                title="Signs in, reads the fields and checks every mapped one. Sends nothing."
                onClick={() => void runTest()}
              >
                Test
              </button>
              <span aria-live="polite">
                {test === "busy" ? <span className="muted">Signing in</span> : test ? <Result ok={test.ok}>{test.text}</Result> : null}
              </span>
            </div>
          )}
        </section>
      )}
      {!ro && (
        <RemoveLine
          what={d.name}
          text={`Remove ${d.name} for company ${company}? Documents already delivered stay there.`}
          onRemove={() => {
            change((dr) => X.removeDestination(dr, type.id, company, kind));
            router.push(back);
          }}
        />
      )}
    </>
  );
}

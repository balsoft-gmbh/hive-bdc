"use client";
import Link from "next/link";
import { useRef, useState } from "react";
import { Eye, FileUp, ListPlus, Plus, Trash2 } from "lucide-react";
import { Notice } from "./notice";
import { change } from "./save-bar";
import { BdcDialog, CommitInput, ConnectionSelect, Field, ICON, Result, RowToggle, Segmented, post } from "./outbound-parts";
import { SourceTiles, useChoices, type Choice } from "./inbound-parts";
import * as I from "@/lib/inboundlogic";

// The tabs of one inbound company (boards I4 to I6): where its documents come from and which ones, where the ERP
// reads the CSV, and the CSV's columns with a preview of a ready document. Every value goes into the draft at once;
// the Save bar lists the changes and saves them.

const same = (a: unknown, b: unknown) => String(a ?? "").toLowerCase() === String(b ?? "").toLowerCase();

// ── Comes from (board I4) ───────────────────────────────────────────────────────────

export function SourceTab({
  flow,
  source,
  sources,
  ro,
  onCompany,
}: {
  flow: any;
  source: any;
  sources: any[];
  ro: boolean;
  onCompany: (code: string) => void;
}) {
  const [changing, setChanging] = useState(false);
  const which = (source.settings || []).filter((s: any) => s.step !== "after");
  const after = (source.settings || []).filter((s: any) => s.step === "after");
  return (
    <>
      {!source.connected && (
        <Notice kind="warn">
          {source.name} is not connected yet. Set it up under <Link href="/connections/">Connections</Link>, then its lists fill here.
        </Notice>
      )}
      <div className="bdc-two">
        <section className="panel" aria-labelledby="from-heading">
          <div className="section-heading">
            <h2 id="from-heading">Comes from</h2>
          </div>
          <div className="form-body">
            <div className="source-card">
              <strong>{source.name}</strong>
              {!ro && (
                <button type="button" className="link-button" onClick={() => setChanging(true)}>
                  Change
                </button>
              )}
            </div>
            <Field label="Company in the ERP" htmlFor="inbound-company" hint="Also usable in the CSV as {DataAreaId}.">
              <CommitInput
                id="inbound-company"
                value={flow.DataAreaId}
                disabled={ro}
                onCommit={(v) => {
                  change((d) => I.setCompany(d, flow.Id, v));
                  onCompany(v.toUpperCase());
                }}
              />
            </Field>
            {which.map((s: any) => (
              <SourceSetting key={s.key} flow={flow} setting={s} all={source.settings} ro={ro} />
            ))}
          </div>
        </section>
        <section className="panel" aria-labelledby="after-heading">
          <div className="section-heading">
            <h2 id="after-heading">After import</h2>
          </div>
          <div className="form-body">
            {after.map((s: any) => (
              <SourceSetting key={s.key} flow={flow} setting={s} all={source.settings} ro={ro} />
            ))}
          </div>
        </section>
      </div>
      {changing && (
        <ChangeSourceDialog
          flow={flow}
          sources={sources}
          onClose={() => setChanging(false)}
        />
      )}
    </>
  );
}

function SourceSetting({ flow, setting, all, ro }: { flow: any; setting: any; all: any[]; ro: boolean }) {
  const settings = flow.Source.Settings || {};
  const depends = setting.dependsOn ? all.find((s: any) => s.key === setting.dependsOn) : null;
  const ready = !setting.dependsOn || String(settings[setting.dependsOn] || "").trim().length > 0;
  const { choices, note, loading } = useChoices(flow, setting.lookup, ready);
  const id = `inbound-source-${setting.key}`;
  const value = String(settings[setting.key] || "");
  const set = (v: string, label?: string) => change((d) => I.setSourceSetting(d, flow.Id, setting.key, v, label));
  const hint = !ready && depends ? `Set "${depends.label}" first.` : note || setting.hint;
  // A list from the document system when it has entries; otherwise a text box, as in the Windows app, so a value no
  // document carries yet (the one a workflow sets later) can be typed.
  if (setting.lookup && !note && (loading || !ready || choices.length > 0)) {
    const known = choices.some((c: Choice) => c.value === value);
    return (
      <Field label={setting.label} htmlFor={id} hint={hint}>
        <select
          id={id}
          value={value}
          disabled={ro || !ready || loading}
          onChange={(e) => set(e.target.value, choices.find((c: Choice) => c.value === e.target.value)?.label)}
        >
          <option value="">{loading ? "Reading the list" : ready ? "Choose" : ""}</option>
          {value && !known && <option value={value}>{settings[`${setting.key}Name`] || value}</option>}
          {choices.map((c: Choice) => (
            <option key={c.value} value={c.value}>
              {c.label}
            </option>
          ))}
        </select>
      </Field>
    );
  }
  return (
    <Field label={setting.label} htmlFor={id} hint={hint}>
      <CommitInput id={id} value={value} disabled={ro} onCommit={(v) => set(v)} />
    </Field>
  );
}

function ChangeSourceDialog({ flow, sources, onClose }: { flow: any; sources: any[]; onClose: () => void }) {
  const [kind, setKind] = useState(flow.Source.Kind);
  return (
    <BdcDialog
      title="Where do the documents come from?"
      subtitle={`Company ${flow.DataAreaId}. Its settings start afresh with the new document system.`}
      onClose={onClose}
      wide
      footer={
        <>
          <button type="button" className="button secondary" onClick={onClose}>
            Cancel
          </button>
          <button
            type="button"
            className="button primary"
            disabled={same(kind, flow.Source.Kind)}
            onClick={() => {
              const source = sources.find((s) => s.kind === kind);
              if (source) change((d) => I.changeSource(d, flow.Id, source));
              onClose();
            }}
          >
            Use it
          </button>
        </>
      }
    >
      <SourceTiles sources={sources} value={kind} onChange={setKind} />
    </BdcDialog>
  );
}

// ── Goes to (board I5) ──────────────────────────────────────────────────────────────

export function DestinationTab({ flow, source, destinations, ro }: { flow: any; source: any; destinations: any[]; ro: boolean }) {
  const target = I.destinationOf(flow);
  const d = target ? destinations.find((x) => same(x.kind, target.Kind)) : null;
  const s = (target && target.Settings) || {};
  const saveFile = String(s.SaveFile).toLowerCase() === "true";
  const set = (changes: { connection?: string; settings?: Record<string, any> }) => change((dr) => I.setDestination(dr, flow.Id, changes));
  const overlap = target ? I.filePrefixProblem(target) : "";
  const fields = useChoices(flow, source?.fieldsLookup || "fields", Boolean(source));
  const tables = fields.choices.filter((c) => c.detail === "table");
  const produce = flow.Produce.Settings || {};
  const culture = produce.Culture || "de-DE";
  return (
    <div className="bdc-two">
      <section className="panel" aria-labelledby="to-heading">
        <div className="section-heading">
          <h2 id="to-heading">Where your ERP reads it</h2>
        </div>
        <div className="form-body">
          {target && !d && (
            <Notice kind="warn">
              {target.Kind === "folder"
                ? `This company writes to the folder ${s.Folder || ""}, which only the Windows install has. Choose Blob Storage or Service Bus.`
                : `This company goes to ${target.Kind}, which this instance does not offer. Choose another place.`}
            </Notice>
          )}
          <div className="field">
            <span className="field-label">Goes to</span>
            <Segmented
              label="Goes to"
              options={destinations.map((x) => [x.kind, x.name] as [string, string])}
              value={d ? d.kind : ""}
              disabled={ro}
              onChange={(kind) => change((dr) => I.setDestinationKind(dr, flow.Id, kind))}
            />
            <span className="field-hint">{d ? d.description : "Choose where your ERP reads the CSV."}</span>
          </div>
          {d && (
            <>
              {d.connectionKind && (
                <Field label="Connection" htmlFor="inbound-dest-connection" hint={d.connected ? undefined : "Add one under Connections first."}>
                  <ConnectionSelect
                    id="inbound-dest-connection"
                    kind={d.connectionKind}
                    value={target.Connection}
                    disabled={ro}
                    onChange={(v) => set({ connection: v })}
                  />
                </Field>
              )}
              {d.settings
                .filter((x: any) => x.dependsOn !== "SaveFile")
                .map((x: any) => (
                  <DestinationSetting key={x.key} setting={x} value={s[x.key] || ""} ro={ro} onCommit={(v) => set({ settings: { [x.key]: v } })} />
                ))}
              <RowToggle
                label="Save the document file as well"
                name="Save the document file as well"
                checked={saveFile}
                disabled={ro}
                large
                onChange={(on) => set({ settings: { SaveFile: on } })}
              />
              {saveFile &&
                d.settings
                  .filter((x: any) => x.dependsOn === "SaveFile")
                  .map((x: any) => (
                    <DestinationSetting key={x.key} setting={x} value={s[x.key] || ""} ro={ro} onCommit={(v) => set({ settings: { [x.key]: v } })} />
                  ))}
              {saveFile && !d.settings.some((x: any) => x.dependsOn === "SaveFile") && <span className="field-hint">Its path is {"{FilePath}"} in the CSV.</span>}
              {overlap && <Notice kind="error">{overlap.charAt(0).toUpperCase() + overlap.slice(1)}</Notice>}
            </>
          )}
        </div>
      </section>
      <section className="panel" aria-labelledby="csv-heading">
        <div className="section-heading">
          <h2 id="csv-heading">CSV</h2>
        </div>
        <div className="form-body">
          <Field label="CSV file name" htmlFor="inbound-csv-name" hint="A template, e.g. {ID}.csv.">
            <CommitInput
              id="inbound-csv-name"
              value={produce.FileName || source?.defaultFileName || "{ID}.csv"}
              disabled={ro}
              onCommit={(v) => change((dr) => I.setProduce(dr, flow.Id, "FileName", v))}
            />
          </Field>
          <div className="form-row-split">
            <Field label="Separator" htmlFor="inbound-csv-separator">
              <CommitInput
                id="inbound-csv-separator"
                value={produce.Delimiter || ";"}
                disabled={ro}
                onCommit={(v) => change((dr) => I.setProduce(dr, flow.Id, "Delimiter", v || ";"))}
              />
            </Field>
            <Field label="Numbers and dates" htmlFor="inbound-csv-culture">
              <select
                id="inbound-csv-culture"
                value={culture.toLowerCase().startsWith("de") ? "de-DE" : same(culture, "en-US") ? "en-US" : "invariant"}
                disabled={ro}
                onChange={(e) => change((dr) => I.setProduce(dr, flow.Id, "Culture", e.target.value))}
              >
                {I.NUMBER_FORMATS.map(([value, label]) => (
                  <option key={value} value={value}>
                    {label}
                  </option>
                ))}
              </select>
            </Field>
          </div>
          <Field label="One CSV row per row of the table" htmlFor="inbound-csv-rows" hint={tables.length ? undefined : "The document system lists no table for these documents."}>
            <select
              id="inbound-csv-rows"
              value={produce.RowsFrom || ""}
              disabled={ro || (!tables.length && !produce.RowsFrom)}
              onChange={(e) => change((dr) => I.setProduce(dr, flow.Id, "RowsFrom", e.target.value))}
            >
              <option value="">No table, one row per document</option>
              {produce.RowsFrom && !tables.some((t) => t.value === produce.RowsFrom) && <option value={produce.RowsFrom}>{produce.RowsFrom}</option>}
              {tables.map((t) => (
                <option key={t.value} value={t.value}>
                  {t.label}
                </option>
              ))}
            </select>
          </Field>
        </div>
      </section>
    </div>
  );
}

function DestinationSetting({ setting, value, ro, onCommit }: { setting: any; value: string; ro: boolean; onCommit: (v: string) => void }) {
  const id = `inbound-dest-${setting.key}`;
  const connectionKind = String(setting.lookup || "").startsWith("connections:") ? String(setting.lookup).slice("connections:".length) : "";
  return (
    <Field label={setting.label} htmlFor={id} hint={setting.hint || undefined}>
      {connectionKind ? (
        <ConnectionSelect id={id} kind={connectionKind} value={value} disabled={ro} onChange={onCommit} />
      ) : (
        <CommitInput id={id} value={value} disabled={ro} onCommit={onCommit} />
      )}
    </Field>
  );
}

// ── CSV columns (board I6) ──────────────────────────────────────────────────────────

type Preview = "busy" | { ok: boolean; message: string; lines?: string[]; values?: { name: string; value: string }[] } | null;

export function ColumnsTab({ flow, source, ro }: { flow: any; source: any; ro: boolean }) {
  const columns: { Name: string; Value: string }[] = flow.Produce.Columns || [];
  const fields = useChoices(flow, source?.fieldsLookup || "fields", Boolean(source));
  const [status, setStatus] = useState<{ ok: boolean; message: string } | null>(null);
  const [preview, setPreview] = useState<Preview>(null);
  const file = useRef<HTMLInputElement>(null);
  const setAll = (next: { Name: string; Value: string }[]) => change((d) => I.setColumns(d, flow.Id, next));
  const setOne = (i: number, part: "Name" | "Value", v: string) => setAll(columns.map((c, n) => (n === i ? { ...c, [part]: v } : c)));
  const listId = `inbound-fields-${flow.Id}`;

  const fromFields = () => {
    if (!fields.choices.length) {
      setStatus({ ok: false, message: `${source?.name || "The document system"} listed no fields yet. Fill the settings under Comes from first.` });
      return;
    }
    setAll(I.columnsFromFields(columns, fields.choices, flow.Produce.Settings?.RowsFrom));
    setStatus({ ok: true, message: "Columns added for every field. Rename them to what the ERP expects and remove the ones it does not read." });
  };
  const fromCsv = async (picked: File | undefined) => {
    if (!picked) return;
    const text = await picked.slice(0, 64 * 1024).text();
    const result = I.columnsFromCsv(text, columns, fields.choices);
    change((d) => {
      I.setColumns(d, flow.Id, result.columns);
      return I.setProduce(d, flow.Id, "Delimiter", result.delimiter);
    });
    setStatus({ ok: true, message: `${result.added} column(s) taken from ${picked.name}. Say what fills each one.` });
    if (file.current) file.current.value = "";
  };
  const runPreview = async () => {
    setPreview("busy");
    const answer = await post("/sources/preview", I.sourceRequest(flow));
    setPreview({ ok: answer.ok, message: answer.message, lines: answer.lines, values: answer.values });
  };

  return (
    <section className="panel" aria-labelledby="columns-heading">
      <div className="section-heading">
        <h2 id="columns-heading">CSV columns</h2>
      </div>
      <div className="form-body">
        {!ro && (
          <div className="button-row">
            <button type="button" className="button secondary" onClick={fromFields} disabled={fields.loading}>
              <ListPlus {...ICON} aria-hidden="true" />
              One column per field
            </button>
            <button type="button" className="button secondary" onClick={() => file.current?.click()}>
              <FileUp {...ICON} aria-hidden="true" />
              Columns from a CSV the ERP reads
            </button>
            <input
              ref={file}
              type="file"
              accept=".csv,text/csv,text/plain"
              className="sr-only"
              aria-label="A CSV file the ERP already reads"
              onChange={(e) => void fromCsv(e.target.files?.[0])}
            />
            <button type="button" className="button secondary" onClick={() => void runPreview()} disabled={preview === "busy"}>
              <Eye {...ICON} aria-hidden="true" />
              Preview with a ready document
            </button>
          </div>
        )}
        <datalist id={listId}>
          {fields.choices.map((c) => (
            <option key={c.value} value={`{${c.value}}`}>
              {c.label}
            </option>
          ))}
          <option value="{DataAreaId}">The company</option>
          <option value="{FilePath}">Where the file was saved</option>
        </datalist>
        <div className="grid-scroll">
          <table className="columns-table">
            <thead>
              <tr>
                <th scope="col">Column</th>
                <th scope="col">Value</th>
                {!ro && (
                  <th scope="col">
                    <span className="sr-only">Remove</span>
                  </th>
                )}
              </tr>
            </thead>
            <tbody>
              {columns.map((c, i) => (
                <tr key={i}>
                  <td>
                    <CommitInput className="mono" aria-label={`Column ${i + 1}`} value={c.Name} disabled={ro} onCommit={(v) => setOne(i, "Name", v)} />
                  </td>
                  <td>
                    <CommitInput className="mono" aria-label={`Value of ${c.Name || `column ${i + 1}`}`} list={listId} value={c.Value} disabled={ro} onCommit={(v) => setOne(i, "Value", v)} />
                  </td>
                  {!ro && (
                    <td className="cell-action">
                      <button
                        type="button"
                        className="icon-button small danger"
                        aria-label={`Remove ${c.Name || `column ${i + 1}`}`}
                        title="Remove"
                        onClick={() => setAll(columns.filter((_, n) => n !== i))}
                      >
                        <Trash2 size={15} strokeWidth={1.75} />
                      </button>
                    </td>
                  )}
                </tr>
              ))}
              {columns.length === 0 && (
                <tr>
                  <td colSpan={ro ? 2 : 3} className="cell-muted">
                    No column yet. The buttons above fill them from the document system or from a CSV your ERP reads.
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
        {!ro && (
          <div>
            <button type="button" className="button secondary" onClick={() => setAll([...columns, { Name: "", Value: "" }])}>
              <Plus {...ICON} aria-hidden="true" />
              Add column
            </button>
          </div>
        )}
        <p className="field-hint">
          {"{FIELD}"} takes a field of the document, {"{TABLE.COLUMN}"} a column of the table the rows come from, {"{FIELD:date}"} writes a date in the
          format chosen under Goes to, {"{DataAreaId}"} the company and {"{FilePath}"} where the file was saved.
        </p>
        {status && <Result ok={status.ok}>{status.message}</Result>}
        {preview === "busy" && <p className="muted">Reading one ready {source?.recordNoun || "document"} from {source?.name || "the document system"}.</p>}
        {preview && preview !== "busy" && (
          <>
            <Result ok={preview.ok}>{preview.message}</Result>
            {preview.lines && preview.lines.length > 0 && (
              <pre className="csv-preview" tabIndex={0} aria-label="The CSV this document makes">
                {preview.lines.join("\n")}
              </pre>
            )}
            {preview.values && preview.values.length > 0 && (
              <details className="preview-values">
                <summary>Its values in {source?.name || "the document system"}</summary>
                <dl className="bdc-facts">
                  {preview.values.map((v) => (
                    <div key={v.name}>
                      <dt>{v.name}</dt>
                      <dd>{v.value}</dd>
                    </div>
                  ))}
                </dl>
              </details>
            )}
          </>
        )}
      </div>
    </section>
  );
}

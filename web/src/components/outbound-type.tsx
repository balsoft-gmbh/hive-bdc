"use client";
import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { Fragment, useEffect, useState } from "react";
import { Building2, Plus } from "lucide-react";
import { DataGrid, StatusPill, type GridColumn } from "./ui/data-grid";
import { PageHeading } from "./page-heading";
import { Notice } from "./notice";
import { TogglePill } from "./toggle-pill";
import { useCrumbs, type Crumb } from "./crumbs";
import { useConsole } from "./console-context";
import { SaveBar, change, setTargets, useDraft } from "./save-bar";
import { BdcDialog, CommitInput, Field, ICON, RemoveLine, RowToggle, Segmented, typePath, useAnswer } from "./outbound-parts";
import { SourceTab } from "./outbound-source";
import { AddDestinationDialog, DestinationForm, DestinationsTab } from "./outbound-destinations";
import * as X from "@/lib/exportlogic";
import { typeLabel } from "@/lib/logic";

// One document type (boards W8 to W11, export.js typeScreen, companyScreen and destinationScreen) as an ERP record on
// the CRM tab bar: Companies holds the type's own settings and its companies, Comes from and Goes to the chosen
// company's source and destinations, and a destination opens its form inside Goes to. The shell's breadcrumb shows
// Outbound > type > Company > destination; the address carries everything, so a reload or a shared link opens the
// same place: /outbound/type/?id=SalesInvoice&company=1000&tab=to&dest=sharepoint.

type Tab = "companies" | "from" | "to";
type Dialog = "company" | "destination" | null;

const same = (a: unknown, b: unknown) => String(a ?? "").toLowerCase() === String(b ?? "").toLowerCase();

export function OutboundType() {
  const router = useRouter();
  const params = useSearchParams();
  const id = params.get("id") ?? "";
  const companyParam = params.get("company") ?? "";
  const dest = params.get("dest") ?? "";
  const asked = params.get("tab");
  const tab: Tab = asked === "from" || asked === "to" || asked === "companies" ? asked : dest ? "to" : companyParam ? "from" : "companies";
  const { me } = useConsole();
  const state = useDraft();
  const ro = !me?.canChange;
  const targets = useAnswer<{ targets: any[] }>("/targets");
  const [dialog, setDialog] = useState<Dialog>(null);
  useEffect(() => {
    if (targets.data) setTargets(targets.data.targets || []);
  }, [targets.data]);

  const body = state.body;
  const type = body?.documentTypes.find((t: any) => same(t.id, id)) ?? null;
  const name = type ? typeLabel(type) : id || "Document type";
  const company = type ? companyParam || type.companies[0]?.dataAreaId || "" : "";
  const entry = type?.companies.find((c: any) => same(c.dataAreaId, company)) ?? null;
  const descriptors = targets.data?.targets;
  const destName = dest ? (descriptors || []).find((d: any) => d.kind === dest)?.name || dest : "";

  const crumbs: Crumb[] = [{ label: name, href: type ? typePath(type.id) : undefined }];
  if (type && tab !== "companies" && entry) crumbs.push({ label: `Company ${entry.dataAreaId}`, href: typePath(type.id, entry.dataAreaId, tab) });
  if (type && tab === "to" && dest && entry) crumbs.push({ label: destName });
  useCrumbs(body ? crumbs : []);

  if (!body) return <PageHeading title="Outbound" />;
  if (!type) {
    return (
      <>
        <PageHeading title={name} />
        <Notice kind="error">There is no document type {id}.</Notice>
        <SaveBar />
      </>
    );
  }
  const setType = (changes: any) => change((d) => X.setType(d, type.id, changes));
  const meta = [
    type.id,
    type.handler === "EInvoice" ? "E-invoice" : "PDF as it is",
    type.party === "Vendor" ? "Sent to vendors" : "Sent to customers",
    ...(type.enabled === false ? ["Off, its exports wait in their source"] : []),
  ].join(" · ");

  const tabs: [Tab, string, string][] = [
    ["companies", "Companies", typePath(type.id)],
    ["from", "Comes from", typePath(type.id, company, "from")],
    ["to", "Goes to", typePath(type.id, company, "to")],
  ];

  return (
    <>
      <PageHeading
        title={name}
        description={meta}
        actions={<TogglePill label="Active" checked={type.enabled !== false} disabled={ro} onChange={(on) => setType({ enabled: on })} />}
      />
      <nav className="setup-tabs" aria-label="Document type sections">
        {tabs.map(([key, text, href]) => (
          <Link key={key} href={href} className={`setup-tab${tab === key ? " active" : ""}`} aria-current={tab === key ? "page" : undefined}>
            {text}
          </Link>
        ))}
      </nav>
      <Fragment key={`${state.generation}|${tab}|${company}`}>
        {targets.error && <Notice kind="error">{targets.error}</Notice>}
        {tab === "companies" ? (
          <>
            <TypePanel type={type} ro={ro} />
            <CompaniesPanel type={type} name={name} descriptors={descriptors || []} ro={ro} onAdd={() => setDialog("company")} />
            {!ro && (
              <RemoveLine
                what={name}
                text={`Remove ${name} with its companies and destinations? Nothing changes until you save.`}
                onRemove={() => {
                  change((d) => X.removeType(d, type.id));
                  router.push("/outbound/");
                }}
              />
            )}
          </>
        ) : type.companies.length === 0 ? (
          <section className="panel">
            <div className="empty-state compact">
              <Building2 size={22} strokeWidth={1.75} aria-hidden="true" />
              <h2>No company yet.</h2>
              <p>Add one under Companies first.</p>
              <Link className="button secondary" href={typePath(type.id)}>
                Companies
              </Link>
            </div>
          </section>
        ) : !entry ? (
          <Notice kind="error">
            There is no company {company} for {type.id}.
          </Notice>
        ) : (
          <>
            <CompanyBar type={type} entry={entry} tab={tab} descriptors={descriptors || []} ro={ro} />
            {tab === "from" ? (
              <SourceTab key={`${state.generation}|${entry.dataAreaId}`} type={type} entry={entry} ro={ro} />
            ) : !descriptors ? (
              !targets.error && <p className="muted">Reading the destinations of this version.</p>
            ) : dest ? (
              <DestinationForm
                key={`${state.generation}|${entry.dataAreaId}|${dest}`}
                type={type}
                entry={entry}
                kind={dest}
                descriptors={descriptors}
                ro={ro}
              />
            ) : (
              <DestinationsTab type={type} entry={entry} descriptors={descriptors} ro={ro} onAdd={() => setDialog("destination")} />
            )}
            {!ro && !(tab === "to" && dest) && (
              <RemoveLine
                what={`Company ${entry.dataAreaId}`}
                text={`Remove Company ${entry.dataAreaId} with its destinations? Nothing changes until you save.`}
                onRemove={() => {
                  change((d) => X.removeCompany(d, type.id, entry.dataAreaId));
                  router.push(typePath(type.id));
                }}
              />
            )}
          </>
        )}
      </Fragment>
      <SaveBar hidden={dialog !== null} />
      {dialog === "company" && !ro && (
        <AddCompanyDialog
          typeId={type.id}
          name={name}
          onClose={() => setDialog(null)}
          onAdded={(code) => {
            setDialog(null);
            router.push(typePath(type.id, code, "from"));
          }}
        />
      )}
      {dialog === "destination" && !ro && entry && descriptors && (
        <AddDestinationDialog type={type} entry={entry} descriptors={descriptors} onClose={() => setDialog(null)} />
      )}
    </>
  );
}

/** The type's own settings: name, what Hive BDC makes, who receives it (Active sits in the heading). */
function TypePanel({ type, ro }: { type: any; ro: boolean }) {
  const set = (changes: any) => change((d) => X.setType(d, type.id, changes));
  return (
    <section className="panel" aria-labelledby="type-heading">
      <div className="section-heading">
        <h2 id="type-heading">Document type</h2>
      </div>
      <div className="form-body">
        <div className="form-row-3">
          <Field label="Name" htmlFor="type-name">
            <CommitInput id="type-name" value={type.displayName || ""} disabled={ro} onCommit={(v) => set({ displayName: v })} />
          </Field>
          <div className="field">
            <span className="field-label">Hive BDC makes</span>
            <Segmented
              label="Hive BDC makes"
              options={[
                ["EInvoice", "E-invoice"],
                ["Pdf", "PDF as it is"],
              ]}
              value={type.handler}
              disabled={ro}
              onChange={(v) => set({ handler: v })}
            />
            <span className="field-hint">
              {type.handler === "EInvoice" ? "XRechnung or ZUGFeRD, as each document says. NoEInvoice: the ERP's PDF, unchanged." : "The PDF goes out as F&O made it."}
            </span>
          </div>
          <div className="field">
            <span className="field-label">Sent to</span>
            <Segmented
              label="Sent to"
              options={[
                ["Customer", "Customers"],
                ["Vendor", "Vendors"],
              ]}
              value={type.party}
              disabled={ro}
              onChange={(v) => set({ party: v })}
            />
          </div>
        </div>
      </div>
    </section>
  );
}

type CompanyRow = { id: string; entry: any; fresh: boolean };

/** The companies that post this type, with where their documents come from and go to; a company opens its tabs. */
function CompaniesPanel({ type, name, descriptors, ro, onAdd }: { type: any; name: string; descriptors: any[]; ro: boolean; onAdd: () => void }) {
  const state = useDraft();
  const rows: CompanyRow[] = type.companies.map((c: any) => ({ id: c.dataAreaId, entry: c, fresh: !c.source && !c.input }));
  const columns: GridColumn<CompanyRow>[] = [
    {
      key: "company",
      label: "Company",
      width: 170,
      render: (r) => (
        <Link className="cell-link" href={typePath(type.id, r.id, "from")}>
          Company {r.id}
        </Link>
      ),
    },
    {
      key: "from",
      label: "Comes from",
      width: 280,
      render: (r) => (r.fresh ? <span className="warn-text">Not set up yet</span> : <span className="wrap-cell">{X.sourceWords(r.entry.source)}</span>),
    },
    {
      key: "to",
      label: "Goes to",
      render: (r) => {
        const words = X.destinationWords(r.entry, state.body, descriptors);
        return words === "nowhere yet" ? <span className="cell-muted">Nowhere yet</span> : <span className="wrap-cell">{words}</span>;
      },
    },
    {
      key: "state",
      label: "State",
      width: 110,
      render: (r) => <StatusPill tone={r.entry.enabled !== false ? "green" : "neutral"}>{r.entry.enabled !== false ? "Active" : "Off"}</StatusPill>,
    },
  ];
  return (
    <section className="panel" aria-labelledby="companies-heading">
      <div className="section-heading">
        <h2 id="companies-heading">Companies</h2>
        {!ro && (
          <button type="button" className="button secondary" onClick={onAdd}>
            <Plus {...ICON} aria-hidden="true" />
            Add company
          </button>
        )}
      </div>
      <DataGrid
        label={`Companies of ${name}`}
        columns={columns}
        rows={rows}
        empty={
          <div className="empty-state compact">
            <Building2 size={22} strokeWidth={1.75} aria-hidden="true" />
            <h2>No company yet.</h2>
            <p>{ro ? "An administrator adds the companies that post this document type." : "Add company sets up the first one."}</p>
          </div>
        }
      />
    </section>
  );
}

function AddCompanyDialog({ typeId, name, onClose, onAdded }: { typeId: string; name: string; onClose: () => void; onAdded: (code: string) => void }) {
  const [value, setValue] = useState("");
  const [message, setMessage] = useState("");
  const add = () => {
    try {
      change((d) => X.addCompany(d, typeId, value));
      onAdded(value.trim());
    } catch (e) {
      setMessage((e as Error).message);
    }
  };
  return (
    <BdcDialog
      title="Add company"
      subtitle={name}
      onClose={onClose}
      footer={
        <>
          <button type="button" className="button secondary" onClick={onClose}>
            Cancel
          </button>
          <button type="submit" form="add-company-form" className="button primary">
            Add
          </button>
        </>
      }
    >
      <form
        id="add-company-form"
        className="form-grid-plain one"
        onSubmit={(e) => {
          e.preventDefault();
          add();
        }}
      >
        <Field label="Company (dataAreaId)" htmlFor="add-company">
          <input
            id="add-company"
            type="text"
            autoComplete="off"
            spellCheck={false}
            placeholder="2000"
            value={value}
            onChange={(e) => setValue(e.target.value)}
            autoFocus
          />
        </Field>
      </form>
      {message && <Notice kind="error">{message}</Notice>}
    </BdcDialog>
  );
}

/** The company the Comes from and Goes to tabs show: where it reads from, where it delivers, its switcher and Active. */
function CompanyBar({ type, entry, tab, descriptors, ro }: { type: any; entry: any; tab: Tab; descriptors: any[]; ro: boolean }) {
  const router = useRouter();
  const state = useDraft();
  const goes = X.destinationWords(entry, state.body, descriptors);
  return (
    <section className="panel company-bar" aria-labelledby="company-heading">
      <h2 id="company-heading">Company {entry.dataAreaId}</h2>
      <dl className="bdc-facts inline">
        <div>
          <dt>Comes from</dt>
          <dd>{entry.source ? X.sourceWords(entry.source) : <span className="warn-text">Not set up yet</span>}</dd>
        </div>
        <div>
          <dt>Goes to</dt>
          <dd>{goes === "nowhere yet" ? <span className="cell-muted">Nowhere yet</span> : goes}</dd>
        </div>
      </dl>
      <div className="company-bar-actions">
        {type.companies.length > 1 && (
          <select
            aria-label="Show another company"
            value={entry.dataAreaId}
            onChange={(e) => router.push(typePath(type.id, e.target.value, tab))}
          >
            {type.companies.map((c: any) => (
              <option key={c.dataAreaId} value={c.dataAreaId}>
                Company {c.dataAreaId}
              </option>
            ))}
          </select>
        )}
        <RowToggle
          label="Active"
          name={`Company ${entry.dataAreaId} active`}
          checked={entry.enabled !== false}
          disabled={ro}
          large
          onChange={(on) => change((d) => X.setCompanyActive(d, type.id, entry.dataAreaId, on))}
        />
      </div>
    </section>
  );
}
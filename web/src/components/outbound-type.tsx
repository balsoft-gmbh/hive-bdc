"use client";
import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { Fragment, useEffect, useState } from "react";
import { Building2, Plus } from "lucide-react";
import { DataGrid, type GridColumn } from "./ui/data-grid";
import { PageHeading } from "./page-heading";
import { Notice } from "./notice";
import { useConsole } from "./console-context";
import { SaveBar, change, setTargets, useDraft } from "./save-bar";
import { CommitInput, ConfirmStrip, Field, ICON, OutboundBadge, PageTrail, Segmented, SwitchField, typePath, useAnswer } from "./outbound-parts";
import { SourceTab } from "./outbound-source";
import { DestinationForm, DestinationsTab } from "./outbound-destinations";
import * as X from "@/lib/exportlogic";
import { typeLabel } from "@/lib/logic";

// One document type (boards W8 to W11, export.js typeScreen, companyScreen and destinationScreen) on the CRM tab bar:
// Companies holds the type's own settings and its companies, Comes from and Goes to the chosen company's source and
// destinations, and a destination opens its form inside Goes to. The address carries everything, so a reload or a
// shared link opens the same place: /outbound/type/?id=SalesInvoice&company=1000&tab=to&dest=sharepoint.

type Tab = "companies" | "from" | "to";

const same = (a: unknown, b: unknown) => String(a ?? "").toLowerCase() === String(b ?? "").toLowerCase();

export function OutboundType() {
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
  useEffect(() => {
    if (targets.data) setTargets(targets.data.targets || []);
  }, [targets.data]);

  const body = state.body;
  if (!body) return <PageHeading eyebrow="OUTBOUND" title="Outbound" />;
  const type = body.documentTypes.find((t: any) => same(t.id, id));
  if (!type) {
    return (
      <>
        <PageTrail items={[{ text: "Outbound", href: "/outbound/" }, { text: id || "Document type" }]} />
        <PageHeading eyebrow="OUTBOUND" title="Outbound" />
        <Notice kind="error">There is no document type {id}.</Notice>
        <SaveBar />
      </>
    );
  }
  const name = typeLabel(type);
  const company = companyParam || type.companies[0]?.dataAreaId || "";
  const entry = type.companies.find((c: any) => same(c.dataAreaId, company)) ?? null;
  const descriptors = targets.data?.targets;
  const destName = dest ? (descriptors || []).find((d: any) => d.kind === dest)?.name || dest : "";

  const trail: { text: string; href?: string }[] = [
    { text: "Outbound", href: "/outbound/" },
    { text: name, href: typePath(type.id) },
  ];
  if (tab !== "companies" && entry) trail.push({ text: `Company ${entry.dataAreaId}`, href: typePath(type.id, entry.dataAreaId, tab) });
  if (tab === "to" && dest && entry) trail.push({ text: destName });

  const tabs: [Tab, string, string][] = [
    ["companies", "Companies", typePath(type.id)],
    ["from", "Comes from", typePath(type.id, company, "from")],
    ["to", "Goes to", typePath(type.id, company, "to")],
  ];

  return (
    <>
      <PageTrail items={trail} />
      <PageHeading
        eyebrow="DOCUMENT TYPE"
        title={
          <span className="title-with-badge">
            {name} <OutboundBadge />
          </span>
        }
        description="Its settings, the companies in your ERP that post it, and where their documents come from and go."
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
            <CompaniesPanel type={type} name={name} descriptors={descriptors || []} ro={ro} />
          </>
        ) : type.companies.length === 0 ? (
          <section className="panel">
            <div className="empty-state compact">
              <Building2 size={22} strokeWidth={1.75} aria-hidden="true" />
              <h2>No company yet.</h2>
              <p>Add a company under Companies, then choose where its documents come from and where they go.</p>
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
            <CompanyBar type={type} entry={entry} name={name} tab={tab} ro={ro} />
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
              <DestinationsTab type={type} entry={entry} descriptors={descriptors} ro={ro} />
            )}
          </>
        )}
      </Fragment>
      <SaveBar />
    </>
  );
}

/** The type's own settings: name, what Hive BDC makes, who receives it, Active. */
function TypePanel({ type, ro }: { type: any; ro: boolean }) {
  const set = (changes: any) => change((d) => X.setType(d, type.id, changes));
  return (
    <section className="panel form-panel" aria-labelledby="type-heading">
      <div className="section-heading">
        <h2 id="type-heading" className="caps">
          Document type
        </h2>
      </div>
      <div className="form-body">
        <div className="form-row-4">
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
          <div className="field">
            <span className="field-label" aria-hidden="true">
              &nbsp;
            </span>
            <SwitchField text="Active" checked={type.enabled !== false} disabled={ro} onChange={(on) => set({ enabled: on })} />
          </div>
        </div>
        <p className="muted small">
          {type.handler === "EInvoice"
            ? "Hive BDC makes XRechnung or ZUGFeRD, as each export says. Off: exports of this type wait in their source."
            : "The PDF goes out as F&O made it. Off: exports of this type wait in their source."}
        </p>
      </div>
    </section>
  );
}

type CompanyRow = { id: string; entry: any; fresh: boolean };

/** The companies that post this type, with where their documents come from and go to. */
function CompaniesPanel({ type, name, descriptors, ro }: { type: any; name: string; descriptors: any[]; ro: boolean }) {
  const state = useDraft();
  const [adding, setAdding] = useState(false);
  const [confirm, setConfirm] = useState<string | null>(null);
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
      width: 300,
      render: (r) =>
        r.fresh ? (
          <span className="warn-text wrap-cell">Added just now. Choose where its documents come from and where they go.</span>
        ) : (
          <span className="wrap-cell">{X.sourceWords(r.entry.source)}</span>
        ),
    },
    {
      key: "to",
      label: "Goes to",
      render: (r) =>
        r.fresh ? (
          <Link className="button primary" href={typePath(type.id, r.id, "from")}>
            Set it up
          </Link>
        ) : (
          <span className="wrap-cell">{X.destinationWords(r.entry, state.body, descriptors)}</span>
        ),
    },
    {
      key: "mail",
      label: "Mail",
      width: 100,
      render: (r) => (r.entry.mail ? <span className="ok-text">Mail on</span> : <span className="muted">Mail off</span>),
    },
  ];
  return (
    <section className="panel" aria-labelledby="companies-heading">
      <div className="section-heading">
        <div>
          <h2 id="companies-heading">Companies</h2>
          <p>Each company in your ERP that posts this document type. Open one to set where its documents come from and where they go.</p>
        </div>
        {!ro && !adding && (
          <button type="button" className="button secondary" onClick={() => setAdding(true)}>
            <Plus {...ICON} />
            Add company
          </button>
        )}
      </div>
      {adding && !ro && <AddCompany typeId={type.id} onDone={() => setAdding(false)} />}
      {confirm && (
        <div className="panel-inset">
          <ConfirmStrip
            text={`Remove Company ${confirm} with its destinations? Nothing changes until you save.`}
            onKeep={() => setConfirm(null)}
            onRemove={() => {
              change((d) => X.removeCompany(d, type.id, confirm));
              setConfirm(null);
            }}
          />
        </div>
      )}
      <DataGrid
        label={`Companies of ${name}`}
        columns={columns}
        rows={rows}
        rowClassName={(r) => (confirm === r.id ? "bdc-row-removing" : undefined)}
        actionsWidth="110px"
        actions={
          ro
            ? undefined
            : (r) => (
                <button type="button" className="text-button quiet-link" aria-label={`Remove Company ${r.id}`} onClick={() => setConfirm(r.id)}>
                  Remove
                </button>
              )
        }
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

function AddCompany({ typeId, onDone }: { typeId: string; onDone: () => void }) {
  const [value, setValue] = useState("");
  const [message, setMessage] = useState("");
  const add = () => {
    try {
      change((d) => X.addCompany(d, typeId, value));
      onDone();
    } catch (e) {
      setMessage((e as Error).message);
    }
  };
  return (
    <div className="panel-inset">
      <form
        className="inline-form"
        onSubmit={(e) => {
          e.preventDefault();
          add();
        }}
      >
        <input
          type="text"
          autoComplete="off"
          spellCheck={false}
          aria-label="Company (dataAreaId)"
          placeholder="dataAreaId, for example 2000"
          value={value}
          onChange={(e) => setValue(e.target.value)}
          autoFocus
        />
        <button type="submit" className="button primary">
          Add
        </button>
        <button type="button" className="button secondary" onClick={onDone}>
          Cancel
        </button>
      </form>
      {message && <Notice kind="error">{message}</Notice>}
    </div>
  );
}

/** The company the Comes from and Goes to tabs show: which one, and its Active switch. */
function CompanyBar({ type, entry, name, tab, ro }: { type: any; entry: any; name: string; tab: Tab; ro: boolean }) {
  const router = useRouter();
  return (
    <section className="panel company-bar" aria-labelledby="company-heading">
      <div className="company-bar-text">
        <h2 id="company-heading">Company {entry.dataAreaId}</h2>
        <p className="muted small">Where this company&apos;s documents of the type {name} come from, and where they go.</p>
      </div>
      <div className="company-bar-actions">
        {type.companies.length > 1 && (
          <label className="field inline">
            <span>Company</span>
            <select value={entry.dataAreaId} onChange={(e) => router.push(typePath(type.id, e.target.value, tab))}>
              {type.companies.map((c: any) => (
                <option key={c.dataAreaId} value={c.dataAreaId}>
                  Company {c.dataAreaId}
                </option>
              ))}
            </select>
          </label>
        )}
        <SwitchField
          text="Active"
          checked={entry.enabled !== false}
          disabled={ro}
          onChange={(on) => change((d) => X.setCompanyActive(d, type.id, entry.dataAreaId, on))}
        />
      </div>
    </section>
  );
}
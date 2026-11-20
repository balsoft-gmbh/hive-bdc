"use client";
import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { Fragment, useState } from "react";
import { Building2, Plus } from "lucide-react";
import { DataGrid, StatusPill, type GridColumn } from "./ui/data-grid";
import { PageHeading } from "./page-heading";
import { Notice } from "./notice";
import { TogglePill } from "./toggle-pill";
import { useCrumbs, type Crumb } from "./crumbs";
import { useConsole } from "./console-context";
import { SaveBar, change, useDraft } from "./save-bar";
import { BdcDialog, CommitInput, Field, ICON, RemoveLine, RowToggle } from "./outbound-parts";
import { SourceTiles } from "./inbound-parts";
import { inboundPath, useSources } from "./inbound-list";
import { ColumnsTab, DestinationTab, SourceTab } from "./inbound-company";
import * as I from "@/lib/inboundlogic";

// One inbound document type (boards I2 to I6, the Windows app's inbound type and company pages) on the console's tab
// bar: Companies holds the type's name and its companies, Comes from, Goes to and CSV columns the chosen company's
// flow. The address carries everything: /inbound-flows/type/?type=Vendor%20invoice&company=DEMF&tab=to (not /inbound/,
// which the sign-in leaves open for the machine endpoint POST /inbound/{type}/{company}).

type Tab = "companies" | "from" | "to" | "columns";

const same = (a: unknown, b: unknown) => String(a ?? "").toLowerCase() === String(b ?? "").toLowerCase();

export function InboundType() {
  const router = useRouter();
  const params = useSearchParams();
  const typeParam = params.get("type") ?? "";
  const companyParam = params.get("company") ?? "";
  const asked = params.get("tab");
  const tab: Tab = asked === "from" || asked === "to" || asked === "columns" || asked === "companies" ? asked : companyParam ? "from" : "companies";
  const { me } = useConsole();
  const state = useDraft();
  const ro = !me?.canChange;
  const sources = useSources();
  const [adding, setAdding] = useState(false);
  const body = state.body;
  const type = body ? I.inboundTypes(body.flows).find((t) => same(t.name, typeParam)) ?? null : null;
  const name = type?.name || typeParam || "Document type";
  const flow = type ? type.flows.find((f: any) => same(f.DataAreaId, companyParam)) ?? type.flows[0] : null;
  const crumbs: Crumb[] = [{ label: name, href: type ? inboundPath(type.name) : undefined }];
  if (type && tab !== "companies" && flow) crumbs.push({ label: `Company ${flow.DataAreaId || "new"}` });
  useCrumbs(body ? crumbs : []);
  if (!body) return <PageHeading title="Inbound" />;
  if (!type) {
    return (
      <>
        <PageHeading title={name} />
        <Notice kind="error">There is no inbound document type {typeParam}.</Notice>
        <SaveBar />
      </>
    );
  }
  const kinds = sources.data?.sources || [];
  const destinations = sources.data?.destinations || [];
  const active = type.flows.some((f: any) => f.Enabled !== false);
  const meta = [
    "CSV for your ERP",
    `${type.flows.length} ${type.flows.length === 1 ? "company" : "companies"}`,
    ...(active ? [] : ["Off, its documents wait in their document system"]),
  ].join(" · ");
  const company = flow?.DataAreaId || "";
  const tabs: [Tab, string, string][] = [
    ["companies", "Companies", inboundPath(type.name)],
    ["from", "Comes from", inboundPath(type.name, company, "from")],
    ["to", "Goes to", inboundPath(type.name, company, "to")],
    ["columns", "CSV columns", inboundPath(type.name, company, "columns")],
  ];
  const source = flow ? kinds.find((s: any) => same(s.kind, flow.Source.Kind)) : null;

  return (
    <>
      <PageHeading
        title={name}
        description={meta}
        actions={
          <TogglePill label="Active" checked={active} disabled={ro} onChange={(on) => change((d) => I.setTypeActive(d, type.name, on))} />
        }
      />
      <nav className="setup-tabs" aria-label="Document type sections">
        {tabs.map(([key, text, href]) => (
          <Link key={key} href={href} className={`setup-tab${tab === key ? " active" : ""}`} aria-current={tab === key ? "page" : undefined}>
            {text}
          </Link>
        ))}
      </nav>
      <Fragment key={`${state.generation}|${tab}|${flow?.Id}`}>
        {sources.error && <Notice kind="error">{sources.error}</Notice>}
        {tab === "companies" ? (
          <>
            <TypePanel name={type.name} ro={ro} onRenamed={(next) => router.replace(inboundPath(next))} />
            <CompaniesPanel type={type} kinds={kinds} ro={ro} onAdd={() => setAdding(true)} />
            {!ro && (
              <RemoveLine
                what={name}
                text={`Remove ${name} with its companies? Nothing changes until you save.`}
                onRemove={() => {
                  change((d) => I.removeInboundType(d, type.name));
                  router.push("/inbound-flows/");
                }}
              />
            )}
          </>
        ) : !flow ? null : !sources.data ? (
          !sources.error && <p className="muted">Reading the document systems of this version.</p>
        ) : (
          <>
            <CompanyBar type={type} flow={flow} tab={tab} kinds={kinds} ro={ro} />
            {tab === "from" ? (
              source ? (
                <SourceTab flow={flow} source={source} sources={kinds} ro={ro} onCompany={(code) => router.replace(inboundPath(type.name, code, "from"))} />
              ) : (
                <Notice kind="warn">
                  This company reads from {flow.Source.Kind || "an unknown system"}, which this version of Hive BDC does not know. It is kept as it is.
                </Notice>
              )
            ) : tab === "to" ? (
              <DestinationTab flow={flow} source={source} destinations={destinations} ro={ro} />
            ) : (
              <ColumnsTab flow={flow} source={source} ro={ro} />
            )}
            {!ro && (
              <RemoveLine
                what={`Company ${flow.DataAreaId}`}
                text={`Remove Company ${flow.DataAreaId} from ${name}? Nothing changes until you save.`}
                onRemove={() => {
                  change((d) => I.removeFlow(d, flow.Id));
                  router.push(type.flows.length > 1 ? inboundPath(type.name) : "/inbound-flows/");
                }}
              />
            )}
          </>
        )}
      </Fragment>
      <SaveBar hidden={adding} />
      {adding && !ro && (
        <AddCompanyDialog
          type={type.name}
          sources={kinds}
          destinations={destinations}
          onClose={() => setAdding(false)}
          onAdded={(code) => {
            setAdding(false);
            router.push(inboundPath(type.name, code, "from"));
          }}
        />
      )}
    </>
  );
}

function TypePanel({ name, ro, onRenamed }: { name: string; ro: boolean; onRenamed: (next: string) => void }) {
  const [message, setMessage] = useState("");
  return (
    <section className="panel" aria-labelledby="type-heading">
      <div className="section-heading">
        <h2 id="type-heading">Document type</h2>
      </div>
      <div className="form-body">
        <div className="form-row-3">
          <Field label="Name" htmlFor="inbound-type-name" hint="What the ERP receives, e.g. Purchase order or Vendor invoice. Shown in the document history.">
            <CommitInput
              id="inbound-type-name"
              value={name}
              disabled={ro}
              onCommit={(v) => {
                try {
                  change((d) => I.renameInboundType(d, name, v));
                  setMessage("");
                  onRenamed(v);
                } catch (e) {
                  setMessage((e as Error).message);
                }
              }}
            />
          </Field>
        </div>
        {message && <Notice kind="error">{message}</Notice>}
      </div>
    </section>
  );
}

type CompanyRow = { id: string; flow: any };

function CompaniesPanel({ type, kinds, ro, onAdd }: { type: { name: string; flows: any[] }; kinds: any[]; ro: boolean; onAdd: () => void }) {
  const rows: CompanyRow[] = type.flows.map((f: any) => ({ id: f.Id, flow: f }));
  const columns: GridColumn<CompanyRow>[] = [
    {
      key: "company",
      label: "Company",
      width: 150,
      render: (r) => (
        <Link className="cell-link" href={inboundPath(type.name, r.flow.DataAreaId, "from")}>
          Company {r.flow.DataAreaId || "new"}
        </Link>
      ),
    },
    { key: "from", label: "Comes from", width: 240, render: (r) => <span className="wrap-cell">{I.sourceWords(r.flow, kinds)}</span> },
    {
      key: "which",
      label: "Which documents",
      width: 200,
      render: (r) => {
        const words = I.readyWords(r.flow);
        return words === "Not set yet" ? <span className="warn-text">Not set yet</span> : <span className="wrap-cell">{words}</span>;
      },
    },
    {
      key: "to",
      label: "Goes to",
      render: (r) => {
        const words = I.destinationWords(r.flow);
        return words === "Nowhere yet" ? <span className="cell-muted">Nowhere yet</span> : <span className="wrap-cell">{words}</span>;
      },
    },
    {
      key: "state",
      label: "State",
      width: 110,
      render: (r) => <StatusPill tone={r.flow.Enabled !== false ? "green" : "neutral"}>{r.flow.Enabled !== false ? "Active" : "Off"}</StatusPill>,
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
        label={`Companies of ${type.name}`}
        columns={columns}
        rows={rows}
        empty={
          <div className="empty-state compact">
            <Building2 size={22} strokeWidth={1.75} aria-hidden="true" />
            <h2>No company yet.</h2>
            <p>Add company sets up the first one. A document type without companies is not saved.</p>
          </div>
        }
      />
    </section>
  );
}

function AddCompanyDialog({
  type,
  sources,
  destinations,
  onClose,
  onAdded,
}: {
  type: string;
  sources: any[];
  destinations: any[];
  onClose: () => void;
  onAdded: (code: string) => void;
}) {
  const [value, setValue] = useState("");
  const [kind, setKind] = useState("");
  const [message, setMessage] = useState("");
  const add = () => {
    const source = sources.find((s) => s.kind === kind);
    if (!source) {
      setMessage("Choose where the documents come from.");
      return;
    }
    try {
      change((d) => {
        I.addFlow(d, type, value, source, destinations);
        return d;
      });
      onAdded(value.trim().toUpperCase());
    } catch (e) {
      setMessage((e as Error).message);
    }
  };
  return (
    <BdcDialog
      title="Add company"
      subtitle={`A company in your ERP that receives ${type}.`}
      onClose={onClose}
      wide
      footer={
        <>
          <button type="button" className="button secondary" onClick={onClose}>
            Cancel
          </button>
          <button type="submit" form="add-inbound-company" className="button primary">
            Add company
          </button>
        </>
      }
    >
      <form
        id="add-inbound-company"
        className="form-grid-plain one"
        onSubmit={(e) => {
          e.preventDefault();
          add();
        }}
      >
        <Field label="Company in the ERP" htmlFor="add-inbound-company-code">
          <input
            id="add-inbound-company-code"
            type="text"
            autoComplete="off"
            spellCheck={false}
            placeholder="DEMF"
            value={value}
            onChange={(e) => setValue(e.target.value)}
            autoFocus
          />
        </Field>
      </form>
      <SourceTiles sources={sources} value={kind} onChange={setKind} />
      {message && <Notice kind="error">{message}</Notice>}
    </BdcDialog>
  );
}

/** The company the tabs show: where it reads from, where it delivers, the switcher and Active (board I4 to I6). */
function CompanyBar({ type, flow, tab, kinds, ro }: { type: { name: string; flows: any[] }; flow: any; tab: Tab; kinds: any[]; ro: boolean }) {
  const router = useRouter();
  const goes = I.destinationWords(flow);
  return (
    <section className="panel company-bar" aria-labelledby="company-heading">
      <h2 id="company-heading">
        Company {flow.DataAreaId || "new"} <span className="badge-inbound">INBOUND</span>
      </h2>
      <dl className="bdc-facts inline">
        <div>
          <dt>Comes from</dt>
          <dd>{I.sourceWords(flow, kinds)}</dd>
        </div>
        <div>
          <dt>Goes to</dt>
          <dd>{goes === "Nowhere yet" ? <span className="cell-muted">Nowhere yet</span> : goes}</dd>
        </div>
      </dl>
      <div className="company-bar-actions">
        {type.flows.length > 1 && (
          <select aria-label="Show another company" value={flow.DataAreaId} onChange={(e) => router.push(inboundPath(type.name, e.target.value, tab))}>
            {type.flows.map((f: any) => (
              <option key={f.Id} value={f.DataAreaId}>
                Company {f.DataAreaId || "new"}
              </option>
            ))}
          </select>
        )}
        <RowToggle
          label="Active"
          name={`Company ${flow.DataAreaId} active`}
          checked={flow.Enabled !== false}
          disabled={ro}
          large
          onChange={(on) => change((d) => I.setFlowActive(d, flow.Id, on))}
        />
      </div>
    </section>
  );
}

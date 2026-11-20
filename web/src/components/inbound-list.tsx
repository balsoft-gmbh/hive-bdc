"use client";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useEffect, useState } from "react";
import { FileInput, Plus, Search, X as Clear } from "lucide-react";
import { DataGrid, StatusPill, localQuery, type ColumnFilter, type ColumnFilters, type GridColumn } from "./ui/data-grid";
import { PageHeading } from "./page-heading";
import { Notice } from "./notice";
import { useConsole } from "./console-context";
import { SaveBar, change, setInbound, useDraft } from "./save-bar";
import { BdcDialog, Field, ICON, useAnswer } from "./outbound-parts";
import { SourceTiles } from "./inbound-parts";
import * as I from "@/lib/inboundlogic";

// The Inbound list (board I1, the Windows app's InboundPage list): every inbound document type with its companies,
// where their documents come from and where the ERP reads them; the name opens the type. A new type starts with its
// first company, because a type without companies is not saved (as in the Windows app).

type Row = I.InboundRow & { id: string };

export const inboundPath = (type: string, company?: string, tab?: string) =>
  `/inbound-flows/type/?type=${encodeURIComponent(type)}${company ? `&company=${encodeURIComponent(company)}` : ""}${tab ? `&tab=${tab}` : ""}`;

export type SourcesAnswer = { sources: any[]; destinations: any[] };

/** GET /sources: the document systems and the destinations of this run mode; the draft keeps the destinations for the Save bar. */
export function useSources() {
  const answer = useAnswer<SourcesAnswer>("/sources");
  useEffect(() => {
    if (answer.data) setInbound(answer.data.destinations || []);
  }, [answer.data]);
  return answer;
}

const DESCRIPTION = "Documents waiting in a document system, brought into your ERP.";

export function InboundList() {
  const { generation } = useDraft();
  return <List key={generation} />;
}

function List() {
  const router = useRouter();
  const { me } = useConsole();
  const state = useDraft();
  const ro = !me?.canChange;
  const sources = useSources();
  const [adding, setAdding] = useState(false);
  const [search, setSearch] = useState("");
  const [stateFilter, setStateFilter] = useState("");
  const [sort, setSort] = useState<string>();
  const [direction, setDirection] = useState<string>();
  const [filters, setFilters] = useState<ColumnFilters>({});
  const body = state.body;
  if (!body) return <PageHeading title="Inbound" description={DESCRIPTION} />;

  const kinds = sources.data?.sources || [];
  const all: Row[] = I.inboundRows(body.flows, kinds).map((r) => ({ ...r, id: r.name }));
  const term = search.trim().toLowerCase();
  const matching = all.filter(
    (r) => (!term || r.name.toLowerCase().includes(term)) && (!stateFilter || (stateFilter === "active") === r.enabled),
  );
  const columns: GridColumn<Row>[] = [
    {
      key: "name",
      label: "Document type",
      sortKey: "name",
      filter: "text",
      width: 240,
      value: (r) => r.name,
      render: (r) => (
        <Link href={inboundPath(r.name)} className="cell-link">
          {r.name}
        </Link>
      ),
    },
    {
      key: "companies",
      label: "Companies",
      sortKey: "companies",
      filter: "text",
      width: 180,
      value: (r) => r.companies.join(", "),
      render: (r) => <span className="wrap-cell">{r.companies.join(", ")}</span>,
    },
    { key: "from", label: "Comes from", sortKey: "from", filter: "text", width: 220, value: (r) => r.comesFrom, render: (r) => <span className="wrap-cell">{r.comesFrom}</span> },
    {
      key: "to",
      label: "Goes to",
      sortKey: "to",
      filter: "text",
      value: (r) => r.goesTo,
      render: (r) => (r.goesTo === "Nowhere yet" ? <span className="cell-muted">Nowhere yet</span> : <span className="wrap-cell">{r.goesTo}</span>),
    },
    {
      key: "state",
      label: "State",
      sortKey: "state",
      width: 110,
      value: (r) => (r.enabled ? "Active" : "Off"),
      render: (r) => <StatusPill tone={r.enabled ? "green" : "neutral"}>{r.enabled ? "Active" : "Off"}</StatusPill>,
    },
  ];
  const rows = localQuery(matching, columns, sort, direction, filters);
  const onFilter = (key: string, value: ColumnFilter | null) =>
    setFilters((f) => {
      const next = { ...f };
      if (value === null) delete next[key];
      else next[key] = value;
      return next;
    });
  const filtered = Boolean(term || stateFilter);
  const dirty = I.flowChanges(state.original?.flows, body.flows).length > 0;

  return (
    <>
      <PageHeading
        title="Inbound"
        description={DESCRIPTION}
        actions={
          ro ? undefined : (
            <button type="button" className={`button ${dirty || adding ? "secondary" : "primary"}`} onClick={() => setAdding(true)}>
              <Plus {...ICON} aria-hidden="true" />
              New document type
            </button>
          )
        }
      />
      {sources.error && <Notice kind="error">{sources.error}</Notice>}
      <div className="list-toolbar">
        <div className="field search">
          <label htmlFor="inbound-search">Search</label>
          <div className="search-box">
            <Search {...ICON} aria-hidden="true" />
            <input id="inbound-search" autoComplete="off" value={search} onChange={(e) => setSearch(e.target.value)} placeholder="Name" />
            {search && (
              <button type="button" className="icon-button small" aria-label="Clear search" title="Clear search" onClick={() => setSearch("")}>
                <Clear size={15} strokeWidth={1.75} />
              </button>
            )}
          </div>
        </div>
        <label className="field compact">
          State
          <select aria-label="Filter state" value={stateFilter} onChange={(e) => setStateFilter(e.target.value)}>
            <option value="">All</option>
            <option value="active">Active</option>
            <option value="off">Off</option>
          </select>
        </label>
        <div className="list-toolbar-right">
          <span className="record-count">
            {rows.length} {rows.length === 1 ? "document type" : "document types"}
          </span>
        </div>
      </div>
      <DataGrid
        label="Inbound document types"
        columns={columns}
        rows={rows}
        busy={sources.loading}
        sort={sort}
        direction={direction}
        onSort={(key, dir) => {
          setSort(key);
          setDirection(dir);
        }}
        filters={filters}
        onFilter={onFilter}
        empty={
          <div className="empty-state compact">
            <FileInput size={22} strokeWidth={1.75} aria-hidden="true" />
            <h2>{filtered ? "No document type matches." : "No inbound document type yet."}</h2>
            {!filtered && (
              <p>
                {ro
                  ? "An administrator sets them up."
                  : "New document type sets one up: its companies, where their documents come from and where your ERP reads them."}
              </p>
            )}
          </div>
        }
      />
      <p className="muted small">Every imported document has its steps under Document history, searchable by its id in the document system.</p>
      <SaveBar hidden={adding} />
      {adding && !ro && (
        <NewTypeDialog
          sources={kinds}
          destinations={sources.data?.destinations || []}
          onClose={() => setAdding(false)}
          onAdded={(type, company) => {
            setAdding(false);
            router.push(inboundPath(type, company, "from"));
          }}
        />
      )}
    </>
  );
}

function NewTypeDialog({
  sources,
  destinations,
  onClose,
  onAdded,
}: {
  sources: any[];
  destinations: any[];
  onClose: () => void;
  onAdded: (type: string, company: string) => void;
}) {
  const [name, setName] = useState("");
  const [company, setCompany] = useState("");
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
        I.addFlow(d, name, company, source, destinations);
        return d;
      });
      onAdded(name.trim(), company.trim().toUpperCase());
    } catch (e) {
      setMessage((e as Error).message);
    }
  };
  return (
    <BdcDialog
      title="New document type"
      subtitle="Inbound, into your ERP"
      onClose={onClose}
      wide
      footer={
        <>
          <button type="button" className="button secondary" onClick={onClose}>
            Cancel
          </button>
          <button type="submit" form="new-inbound-form" className="button primary">
            Add
          </button>
        </>
      }
    >
      <form
        id="new-inbound-form"
        className="form-grid-plain"
        onSubmit={(e) => {
          e.preventDefault();
          add();
        }}
      >
        <Field label="Name" htmlFor="new-inbound-name" hint="What the ERP receives, e.g. Purchase order or Vendor invoice. Shown in the document history.">
          <input id="new-inbound-name" type="text" autoComplete="off" placeholder="Vendor invoice" value={name} onChange={(e) => setName(e.target.value)} autoFocus />
        </Field>
        <Field label="Company in the ERP" htmlFor="new-inbound-company" hint="The first company that receives it; more follow under Companies.">
          <input
            id="new-inbound-company"
            type="text"
            autoComplete="off"
            spellCheck={false}
            placeholder="DEMF"
            value={company}
            onChange={(e) => setCompany(e.target.value)}
          />
        </Field>
      </form>
      <SourceTiles sources={sources} value={kind} onChange={setKind} />
      {message && <Notice kind="error">{message}</Notice>}
    </BdcDialog>
  );
}

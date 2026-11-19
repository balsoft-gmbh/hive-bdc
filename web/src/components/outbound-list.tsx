"use client";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useEffect, useState } from "react";
import { FileOutput, Plus, Search, X as Clear } from "lucide-react";
import { DataGrid, StatusPill, localQuery, type ColumnFilter, type ColumnFilters, type GridColumn } from "./ui/data-grid";
import { PageHeading } from "./page-heading";
import { Notice } from "./notice";
import { useConsole } from "./console-context";
import { SaveBar, change, setTargets, useDraft } from "./save-bar";
import { BdcDialog, Field, ICON, typePath, useAnswer } from "./outbound-parts";
import * as X from "@/lib/exportlogic";

// The Outbound list (board W7, export.js listScreen) as an ERP list: search and state in the toolbar, every document
// type with its companies, where they go and its state; the name opens the type. Companies that wait get an offer to
// add them; types that are off and have no company are left out unless "Include unused" is on.

type Row = X.TypeRow;

/** Save and Discard start the list afresh: no open dialog or folded types shown (the former state.ui = {}). */
export function OutboundList() {
  const { generation } = useDraft();
  return <List key={generation} />;
}

const DESCRIPTION = "Documents your ERP posts, and where Hive BDC delivers them.";

function List() {
  const router = useRouter();
  const { me } = useConsole();
  const state = useDraft();
  const ro = !me?.canChange;
  const targets = useAnswer<{ targets: any[] }>("/targets");
  const attention = useAnswer<{ events: any[] }>("/history/attention?limit=200");
  const [adding, setAdding] = useState(false);
  const [showFolded, setShowFolded] = useState(false);
  const [search, setSearch] = useState("");
  const [stateFilter, setStateFilter] = useState("");
  const [sort, setSort] = useState<string>();
  const [direction, setDirection] = useState<string>();
  const [filters, setFilters] = useState<ColumnFilters>({});

  useEffect(() => {
    if (targets.data) setTargets(targets.data.targets || []);
  }, [targets.data]);

  const loadError = targets.error || attention.error;
  const descriptors = targets.data?.targets || [];
  const events = attention.data?.events || [];
  const body = state.body;
  if (!body) return <PageHeading title="Outbound" description={DESCRIPTION} />;
  const dirty = X.draftChanges(state.original, body).length > 0;

  // A type switched off on this page stays in the list until it is saved; only types folded when read fold away.
  const now = X.typeRows(body, events, descriptors);
  const before = new Set(X.typeRows(state.original, events, descriptors).folded.map((r) => r.id));
  const order = body.documentTypes.map((t: any) => t.id);
  const visible = [...now.rows, ...now.folded.filter((r) => !before.has(r.id))].sort((a, b) => order.indexOf(a.id) - order.indexOf(b.id));
  const folded = now.folded.filter((r) => before.has(r.id));
  const listed = showFolded ? [...visible, ...folded] : visible;
  const term = search.trim().toLowerCase();
  const matching = listed.filter(
    (r) =>
      (!term || r.name.toLowerCase().includes(term) || r.id.toLowerCase().includes(term)) &&
      (!stateFilter || (stateFilter === "active") === r.enabled),
  );

  const columns: GridColumn<Row>[] = [
    {
      key: "name",
      label: "Document type",
      sortKey: "name",
      filter: "text",
      width: 260,
      value: (r) => r.name,
      render: (r) => (
        <Link href={typePath(r.id)} className="cell-link" title={r.id}>
          {r.name}
        </Link>
      ),
    },
    {
      key: "companies",
      label: "Companies",
      sortKey: "companies",
      filter: "text",
      width: 200,
      value: (r) => r.companies.join(", "),
      render: (r) => (r.companies.length ? <span className="wrap-cell">{r.companies.join(", ")}</span> : <span className="cell-muted">None</span>),
    },
    {
      key: "goes",
      label: "Goes to",
      sortKey: "goes",
      filter: "text",
      value: (r) => r.goesTo,
      render: (r) =>
        r.goesTo === "nowhere yet" ? <span className="cell-muted">Nowhere yet</span> : <span className="wrap-cell">{r.goesTo}</span>,
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

  const waiting = listed.flatMap((row) => row.waiting.map((w) => ({ row, ...w })));
  const filtered = Boolean(term || stateFilter);

  return (
    <>
      <PageHeading
        title="Outbound"
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
      {loadError && <Notice kind="error">{loadError}</Notice>}
      {waiting.map((w) => (
        <div className="waiting-strip" role="status" key={`${w.row.id}|${w.company}`}>
          <span>
            {w.count} {w.count === 1 ? "document" : "documents"} of company {w.company} {w.count === 1 ? "is" : "are"} waiting for {w.row.name}.
          </span>
          {!ro && (
            <button
              type="button"
              className="button secondary"
              onClick={() => {
                change((d) => X.addCompany(d, w.row.id, w.company));
                router.push(typePath(w.row.id, w.company, "from"));
              }}
            >
              <Plus {...ICON} aria-hidden="true" />
              Add company {w.company}
            </button>
          )}
        </div>
      ))}
      <div className="list-toolbar">
        <div className="field search">
          <label htmlFor="outbound-search">Search</label>
          <div className="search-box">
            <Search {...ICON} aria-hidden="true" />
            <input
              id="outbound-search"
              autoComplete="off"
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              placeholder="Name or ID"
            />
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
          {folded.length > 0 && (
            <label className="toggle-pill" title={`${folded.map((r) => r.name).join(", ")}: off and no company`}>
              <input type="checkbox" checked={showFolded} onChange={(e) => setShowFolded(e.target.checked)} />
              Include unused
            </label>
          )}
          <span className="record-count">
            {rows.length} {rows.length === 1 ? "document type" : "document types"}
          </span>
        </div>
      </div>
      <DataGrid
        label="Outbound document types"
        columns={columns}
        rows={rows}
        busy={targets.loading || attention.loading}
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
            <FileOutput size={22} strokeWidth={1.75} aria-hidden="true" />
            <h2>{filtered ? "No document type matches." : "No document type yet."}</h2>
            {!filtered && <p>{ro ? "An administrator sets them up." : "New document type sets one up."}</p>}
          </div>
        }
      />
      <SaveBar hidden={adding} />
      {adding && !ro && (
        <NewTypeDialog
          onClose={() => setAdding(false)}
          onAdded={(id) => {
            setAdding(false);
            router.push(typePath(id));
          }}
        />
      )}
    </>
  );
}

function NewTypeDialog({ onClose, onAdded }: { onClose: () => void; onAdded: (id: string) => void }) {
  const [id, setId] = useState("");
  const [name, setName] = useState("");
  const [handler, setHandler] = useState("Pdf");
  const [party, setParty] = useState("Customer");
  const [message, setMessage] = useState("");
  const add = () => {
    try {
      change((d) => X.addType(d, id, name.trim(), handler, party));
      onAdded(id.trim());
    } catch (e) {
      setMessage((e as Error).message);
    }
  };
  return (
    <BdcDialog
      title="New document type"
      onClose={onClose}
      footer={
        <>
          <button type="button" className="button secondary" onClick={onClose}>
            Cancel
          </button>
          <button type="submit" form="new-type-form" className="button primary">
            Add
          </button>
        </>
      }
    >
      <form
        id="new-type-form"
        className="form-grid-plain"
        onSubmit={(e) => {
          e.preventDefault();
          add();
        }}
      >
        <Field label="ID the ERP writes as documentType" htmlFor="new-type-id" hint={'Letters, digits, "-" and "_".'}>
          <input
            id="new-type-id"
            type="text"
            autoComplete="off"
            spellCheck={false}
            placeholder="DeliveryNote"
            value={id}
            onChange={(e) => setId(e.target.value)}
            autoFocus
          />
        </Field>
        <Field label="Name" htmlFor="new-type-name">
          <input id="new-type-name" type="text" autoComplete="off" placeholder="Lieferschein" value={name} onChange={(e) => setName(e.target.value)} />
        </Field>
        <Field label="Hive BDC makes" htmlFor="new-type-handler">
          <select id="new-type-handler" value={handler} onChange={(e) => setHandler(e.target.value)}>
            <option value="Pdf">PDF as it is</option>
            <option value="EInvoice">E-invoice</option>
          </select>
        </Field>
        <Field label="Sent to" htmlFor="new-type-party">
          <select id="new-type-party" value={party} onChange={(e) => setParty(e.target.value)}>
            <option value="Customer">Customers</option>
            <option value="Vendor">Vendors</option>
          </select>
        </Field>
      </form>
      {message && <Notice kind="error">{message}</Notice>}
    </BdcDialog>
  );
}
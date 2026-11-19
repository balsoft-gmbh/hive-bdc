"use client";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useEffect, useState } from "react";
import { ArrowRight, FileOutput, Plus, Search } from "lucide-react";
import { DataGrid, localQuery, type ColumnFilter, type ColumnFilters, type GridColumn } from "./ui/data-grid";
import { PageHeading } from "./page-heading";
import { Notice } from "./notice";
import { useConsole } from "./console-context";
import { SaveBar, change, setTargets, useDraft } from "./save-bar";
import { ConfirmStrip, Field, ICON, OutboundBadge, Switch, typePath, useAnswer } from "./outbound-parts";
import * as X from "@/lib/exportlogic";
import { typeWords } from "@/lib/logic";

// The Outbound list (board W7, export.js listScreen): every document type with its companies, where they go and its
// Active switch; companies that wait get an offer to add them, types that are off and have no company fold away.

type Row = X.TypeRow;

/** Save and Discard start the list afresh: no open form, question or folded types shown (the former state.ui = {}). */
export function OutboundList() {
  const { generation } = useDraft();
  return <List key={generation} />;
}

function List() {
  const router = useRouter();
  const { me } = useConsole();
  const state = useDraft();
  const ro = !me?.canChange;
  const targets = useAnswer<{ targets: any[] }>("/targets");
  const attention = useAnswer<{ events: any[] }>("/history/attention?limit=200");
  const [adding, setAdding] = useState(false);
  const [showFolded, setShowFolded] = useState(false);
  const [confirm, setConfirm] = useState<Row | null>(null);
  const [error, setError] = useState("");
  const [search, setSearch] = useState("");
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
  if (!body) return <Heading ro adding={false} onAdd={() => {}} />;

  // A type switched off on this page stays in the list until it is saved; only types folded when read fold away.
  const now = X.typeRows(body, events, descriptors);
  const before = new Set(X.typeRows(state.original, events, descriptors).folded.map((r) => r.id));
  const order = body.documentTypes.map((t: any) => t.id);
  const visible = [...now.rows, ...now.folded.filter((r) => !before.has(r.id))].sort((a, b) => order.indexOf(a.id) - order.indexOf(b.id));
  const folded = now.folded.filter((r) => before.has(r.id));
  const listed = showFolded ? [...visible, ...folded] : visible;
  const term = search.trim().toLowerCase();
  const matching = term ? listed.filter((r) => r.name.toLowerCase().includes(term) || r.id.toLowerCase().includes(term)) : listed;

  const columns: GridColumn<Row>[] = [
    {
      key: "name",
      label: "Document type",
      sortKey: "name",
      filter: "text",
      width: 300,
      value: (r) => r.name,
      render: (r) => (
        <span className="cell-stack">
          <Link href={typePath(r.id)} className="cell-link">
            {r.name}
          </Link>
          <span className="cell-secondary">
            {[r.id, r.handler === "EInvoice" ? "e-invoice" : "PDF", r.party === "Vendor" ? "to vendors" : "to customers"].filter(Boolean).join(" · ")}
          </span>
        </span>
      ),
    },
    {
      key: "companies",
      label: "Companies",
      sortKey: "companies",
      filter: "text",
      width: 200,
      value: (r) => r.companies.join(", "),
      render: (r) => <span className="wrap-cell">{r.companies.length ? r.companies.join(", ") : "none"}</span>,
    },
    {
      key: "goes",
      label: "Goes to",
      sortKey: "goes",
      filter: "text",
      value: (r) => r.goesTo,
      render: (r) => <span className="wrap-cell">{r.goesTo}</span>,
    },
    {
      key: "active",
      label: "Active",
      width: 90,
      render: (r) => (
        <Switch label={`${r.name} active`} checked={r.enabled} disabled={ro} onChange={(on) => change((d) => X.setType(d, r.id, { enabled: on }))} />
      ),
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

  const remove = (row: Row) => {
    try {
      change((d) => X.removeType(d, row.id));
    } catch (e) {
      setError((e as Error).message);
    }
    setConfirm(null);
  };

  const waiting = listed.flatMap((row) => row.waiting.map((w) => ({ row, ...w })));

  return (
    <>
      <Heading ro={ro} adding={adding} onAdd={() => setAdding(true)} />
      <div className="dir-strip" aria-label="Direction">
        <span className="dir-box dark">ERP</span>
        <ArrowRight {...ICON} aria-hidden="true" />
        <span className="dir-box">Hive BDC</span>
        <ArrowRight {...ICON} aria-hidden="true" />
        <span className="dir-box">{X.allDestinations(now.rows)}</span>
        <OutboundBadge />
      </div>
      {loadError && <Notice kind="error">{loadError}</Notice>}
      {error && <Notice kind="error">{error}</Notice>}
      {adding && !ro && (
        <NewTypeForm
          onCancel={() => setAdding(false)}
          onAdded={(id) => {
            setAdding(false);
            router.push(typePath(id));
          }}
        />
      )}
      {waiting.map((w) => (
        <div className="waiting-strip" role="status" key={`${w.row.id}|${w.company}`}>
          <span>
            {w.count} {w.count === 1 ? "document" : "documents"} of company {w.company} {w.count === 1 ? "is" : "are"} waiting. Add the company and
            choose where {typeWords({ id: w.row.id, displayName: w.row.displayName }, undefined)} go, then Hive BDC takes{" "}
            {w.count === 1 ? "it" : "them"}.
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
              <Plus {...ICON} />
              Add company {w.company}
            </button>
          )}
        </div>
      ))}
      {confirm && (
        <ConfirmStrip
          text={`Remove ${confirm.name} with its companies and destinations? Nothing changes until you save.`}
          onKeep={() => setConfirm(null)}
          onRemove={() => remove(confirm)}
        />
      )}
      <div className="list-toolbar">
        <div className="field search">
          <label htmlFor="outbound-search">Search</label>
          <div className="search-box">
            <Search size={17} strokeWidth={1.75} />
            <input
              id="outbound-search"
              type="search"
              autoComplete="off"
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              placeholder="Name or ID of a document type"
            />
          </div>
        </div>
        <div className="list-toolbar-right">
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
        rowClassName={(r) => (confirm?.id === r.id ? "bdc-row-removing" : r.waiting.length ? "bdc-row-warn" : undefined)}
        actionsWidth="110px"
        actions={
          ro
            ? undefined
            : (r) => (
                <button
                  type="button"
                  className="text-button quiet-link"
                  onClick={() => {
                    setError("");
                    setConfirm(r);
                  }}
                  aria-label={`Remove ${r.name}`}
                >
                  Remove
                </button>
              )
        }
        empty={
          <div className="empty-state compact">
            <FileOutput size={22} strokeWidth={1.75} aria-hidden="true" />
            <h2>{term ? `No document type matches "${search.trim()}".` : "No document type yet."}</h2>
            {!term && (
              <p>{ro ? "An administrator adds the document types your ERP posts." : "New document type adds the first one your ERP posts."}</p>
            )}
          </div>
        }
      />
      {folded.length > 0 && (
        <p className="muted small folded-line">
          {folded.map((r) => r.name).join(", ")} {folded.length === 1 ? "is" : "are"} off and {folded.length === 1 ? "has" : "have"} no company.{" "}
          {!showFolded && (
            <button type="button" className="text-button quiet-link" onClick={() => setShowFolded(true)}>
              Show them
            </button>
          )}
        </p>
      )}
      <SaveBar />
    </>
  );
}

function Heading({ ro, adding, onAdd }: { ro: boolean; adding: boolean; onAdd: () => void }) {
  return (
    <PageHeading
      eyebrow="SET UP"
      title="Outbound"
      description="Documents your ERP posts, and where Hive BDC delivers them. Open a document type to set it up."
      actions={
        ro || adding ? undefined : (
          <button type="button" className="button primary" onClick={onAdd}>
            <Plus {...ICON} />
            New document type
          </button>
        )
      }
    />
  );
}

function NewTypeForm({ onCancel, onAdded }: { onCancel: () => void; onAdded: (id: string) => void }) {
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
    <section className="panel form-panel" aria-labelledby="new-type-heading">
      <div className="section-heading">
        <h2 id="new-type-heading">New document type</h2>
      </div>
      <div className="form-body">
        <div className="form-row-4">
          <Field label="ID the ERP writes as documentType" htmlFor="new-type-id">
            <input
              id="new-type-id"
              type="text"
              autoComplete="off"
              spellCheck={false}
              placeholder="DeliveryNote"
              value={id}
              onChange={(e) => setId(e.target.value)}
            />
          </Field>
          <Field label="Name" htmlFor="new-type-name">
            <input
              id="new-type-name"
              type="text"
              autoComplete="off"
              placeholder="Lieferschein"
              value={name}
              onChange={(e) => setName(e.target.value)}
            />
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
        </div>
        {message && <Notice kind="error">{message}</Notice>}
        <div className="row-actions">
          <button type="button" className="button secondary" onClick={onCancel}>
            Cancel
          </button>
          <button type="button" className="button primary" onClick={add}>
            Add
          </button>
        </div>
      </div>
    </section>
  );
}
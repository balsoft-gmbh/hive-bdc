"use client";
import { useRouter, useSearchParams } from "next/navigation";
import { Suspense, useEffect, useState } from "react";
import { Search } from "lucide-react";
import { DataGrid, StatusPill, localQuery, type ColumnFilter, type ColumnFilters, type GridColumn } from "@/components/ui/data-grid";
import { PageHeading } from "@/components/page-heading";
import { api } from "@/lib/api";
import { newestFirst, stepLabel } from "@/lib/logic";

type Event = {
  id: string;
  at: string;
  documentId: string;
  documentNumber?: string;
  dataAreaId?: string;
  step: string;
  state: string;
  target?: string;
  message: string;
};

const columns: GridColumn<Event>[] = [
  { key: "at", label: "When", sortKey: "at", filter: "text", width: 170, value: (e) => e.at, render: (e) => <span className="nowrap">{e.at}</span> },
  {
    key: "document",
    label: "Document",
    sortKey: "document",
    filter: "text",
    width: 170,
    value: (e) => e.documentNumber || e.documentId,
    render: (e) => <strong>{e.documentNumber || e.documentId}</strong>,
  },
  { key: "company", label: "Company", sortKey: "company", filter: "text", width: 110, value: (e) => e.dataAreaId ?? "", render: (e) => e.dataAreaId ?? "" },
  {
    key: "step",
    label: "Step",
    sortKey: "step",
    filter: "text",
    width: 130,
    value: (e) => stepLabel(e.step),
    render: (e) => <StatusPill tone={e.state === "ok" ? "green" : e.state === "waiting" ? "amber" : "rose"}>{stepLabel(e.step)}</StatusPill>,
  },
  { key: "target", label: "Where", sortKey: "target", filter: "text", width: 150, hideBelow: 1300, value: (e) => e.target ?? "", render: (e) => e.target ?? "" },
  { key: "message", label: "What happened", render: (e) => <span className="message-cell">{e.message}</span> },
];

function History() {
  const router = useRouter();
  const params = useSearchParams();
  const search = params.get("search") ?? "";
  const [text, setText] = useState(search);
  const [events, setEvents] = useState<Event[] | null>(null);
  const [error, setError] = useState("");
  const [sort, setSort] = useState<string>();
  const [direction, setDirection] = useState<string>();
  const [filters, setFilters] = useState<ColumnFilters>({});

  useEffect(() => {
    setText(search);
    setEvents(null);
    api(`/history?limit=300${search ? `&search=${encodeURIComponent(search)}` : ""}`)
      .then((answer) => setEvents(newestFirst(answer.events).map((e: any) => ({ ...e, id: String(e.id) }))))
      .catch((e: Error) => setError(e.message));
  }, [search]);

  const rows = events ? localQuery(events, columns, sort, direction, filters) : [];
  const onFilter = (key: string, value: ColumnFilter | null) =>
    setFilters((f) => {
      const next = { ...f };
      if (value === null) delete next[key];
      else next[key] = value;
      return next;
    });

  return (
    <>
      <PageHeading eyebrow="WORK" title="Document history" description="Every step of every document." />
      <form
        className="list-toolbar"
        role="search"
        onSubmit={(event) => {
          event.preventDefault();
          const value = text.trim();
          router.push(value ? `/history/?search=${encodeURIComponent(value)}` : "/history/");
        }}
      >
        <div className="field search">
          <label htmlFor="history-search">Search</label>
          <div className="search-box">
            <Search size={17} />
            <input
              id="history-search"
              type="search"
              autoComplete="off"
              value={text}
              onChange={(e) => setText(e.target.value)}
              placeholder="Document number, company or file name"
            />
          </div>
        </div>
        <div className="list-toolbar-right">{events && <span className="record-count">{rows.length} steps</span>}</div>
      </form>
      {error && (
        <div className="error-banner" role="alert">
          <span>{error}</span>
        </div>
      )}
      <DataGrid
        label="Document history"
        columns={columns}
        rows={rows}
        busy={events === null && !error}
        sort={sort}
        direction={direction}
        onSort={(key, dir) => {
          setSort(key);
          setDirection(dir);
        }}
        filters={filters}
        onFilter={onFilter}
        rowClassName={(e) => (e.state === "ok" ? undefined : "row-warn")}
        empty={search ? `Nothing found for "${search}".` : "No documents yet. They show here once F&O hands over the first one."}
      />
    </>
  );
}

export default function HistoryPage() {
  return (
    <Suspense fallback={null}>
      <History />
    </Suspense>
  );
}

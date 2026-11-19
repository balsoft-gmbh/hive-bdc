"use client";
import Link from "next/link";
import { useEffect, useState } from "react";
import { AlertCircle, ArrowRight, BadgeCheck, CircleCheck } from "lucide-react";
import { DataGrid, StatusPill, type GridColumn } from "@/components/ui/data-grid";
import { PageHeading } from "@/components/page-heading";
import { UpdateNotice } from "@/components/update-notice";
import { useConsole } from "@/components/console-context";
import { api } from "@/lib/api";
import { attentionRows, dayLine, greeting, licenceLine } from "@/lib/logic";

type Row = ReturnType<typeof attentionRows>[number] & { id: string };

export default function OverviewPage() {
  const { me, status, updates } = useConsole();
  const [metrics, setMetrics] = useState<{ today: number; month: number } | null>(null);
  const [rows, setRows] = useState<Row[] | null>(null);
  const [error, setError] = useState("");
  const [results, setResults] = useState<Record<string, { ok: boolean; text: string }>>({});
  const [busy, setBusy] = useState<string>("");
  const [now, setNow] = useState<Date | null>(null);

  useEffect(() => {
    setNow(new Date());
    Promise.all([api("/metrics?recent=0"), api("/history/attention?limit=200")])
      .then(([m, a]) => {
        setMetrics(m);
        setRows(attentionRows(a.events).map((r) => ({ ...r, id: r.documentId })));
      })
      .catch((e: Error) => setError(e.message));
  }, []);

  const retry = async (row: Row) => {
    setBusy(row.id);
    try {
      const answer = await api("/history/retry", { method: "POST", body: JSON.stringify({ documentId: row.documentId }) });
      setResults((r) => ({ ...r, [row.id]: { ok: Boolean(answer.ok), text: answer.message } }));
    } catch (e) {
      setResults((r) => ({ ...r, [row.id]: { ok: false, text: (e as Error).message } }));
    } finally {
      setBusy("");
    }
  };

  const licence = licenceLine(status?.license);
  const count = rows?.length ?? 0;
  const columns: GridColumn<Row>[] = [
    { key: "number", label: "Document", width: 170, render: (r) => <strong>{r.number}</strong> },
    { key: "company", label: "Company", width: 110, render: (r) => r.company },
    {
      key: "message",
      label: "What happened",
      render: (r) => (
        <span className="message-cell">
          {r.target && <StatusPill tone={r.waiting ? "amber" : "rose"}>{r.target}</StatusPill>}
          <span>{r.message}</span>
        </span>
      ),
    },
  ];

  return (
    <>
      <PageHeading
        eyebrow={now ? dayLine(now) : "HIVE BDC"}
        title={now && me ? greeting(now.getHours(), me.name) : "Overview"}
        description={now ? `As of ${now.toLocaleTimeString("de-DE", { hour: "2-digit", minute: "2-digit" })}` : undefined}
      />
      <UpdateNotice view={updates} />
      {!licence.ok && (
        <div className="location-notice page-notice" role="status">
          {status?.license?.message || licence.text}
        </div>
      )}
      {error && (
        <div className="error-banner" role="alert">
          <span>{error}</span>
        </div>
      )}
      <div className="stat-row">
        <div className="queue-tile green">
          <span className="queue-icon">
            <CircleCheck size={16} />
          </span>
          <span className="queue-body">
            <strong>{metrics ? metrics.today : "…"}</strong>
            <span>Delivered today{metrics ? ` · ${metrics.month} this month` : ""}</span>
          </span>
        </div>
        <a className={`queue-tile ${count ? "amber" : "green clear"}`} href="#attention">
          <span className="queue-icon">
            <AlertCircle size={16} />
          </span>
          <span className="queue-body">
            <strong>{rows ? count : "…"}</strong>
            <span>{count ? "Need attention" : "Nothing needs attention"}</span>
          </span>
          <ArrowRight size={14} className="queue-arrow" />
        </a>
        <Link className={`queue-tile ${licence.ok ? "blue" : "rose"}`} href="/service/?tab=licence">
          <span className="queue-icon">
            <BadgeCheck size={16} />
          </span>
          <span className="queue-body">
            <span className="stat-text">{licence.text}</span>
            <span>Licence</span>
          </span>
          <ArrowRight size={14} className="queue-arrow" />
        </Link>
      </div>
      <section className="panel" id="attention" aria-labelledby="attention-heading">
        <div className="section-heading">
          <div>
            <h2 id="attention-heading">Needs attention</h2>
            <p>The newest step of every document that did not go all the way through.</p>
          </div>
          <Link href="/history/" className="inline-link">
            Document history <ArrowRight size={15} />
          </Link>
        </div>
        <DataGrid
          label="Documents that need attention"
          columns={columns}
          rows={rows ?? []}
          busy={rows === null && !error}
          empty="Every document went through."
          actionsWidth="220px"
          actions={(row) => (
            <span className="row-actions">
              <Link href={`/history/?search=${encodeURIComponent(row.documentId)}`}>History</Link>
              {results[row.id] ? (
                <span className={results[row.id].ok ? "ok-text" : "warn-text"}>{results[row.id].text}</span>
              ) : (
                !row.waiting &&
                me?.canRetry && (
                  <button type="button" className="button secondary" disabled={busy === row.id} onClick={() => void retry(row)}>
                    Retry
                  </button>
                )
              )}
            </span>
          )}
        />
      </section>
    </>
  );
}

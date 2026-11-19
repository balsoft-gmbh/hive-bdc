"use client";
import Link from "next/link";
import { useEffect, useState } from "react";
import { AlertCircle, ArrowRight, BadgeCheck, CircleCheck } from "lucide-react";
import { DataGrid, StatusPill, type GridColumn } from "@/components/ui/data-grid";
import { PageHeading } from "@/components/page-heading";
import { StatCard } from "@/components/stat-card";
import { UpdateNotice } from "@/components/update-notice";
import { useConsole } from "@/components/console-context";
import { api } from "@/lib/api";
import { attentionRows, germanDate, licenceWorks } from "@/lib/logic";

type Row = ReturnType<typeof attentionRows>[number] & { id: string };

export default function OverviewPage() {
  const { me, status, updates } = useConsole();
  const [metrics, setMetrics] = useState<{ today: number; month: number } | null>(null);
  const [rows, setRows] = useState<Row[] | null>(null);
  const [error, setError] = useState("");
  const [results, setResults] = useState<Record<string, { ok: boolean; text: string }>>({});
  const [busy, setBusy] = useState<string>("");

  useEffect(() => {
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

  const licence = status?.license;
  const licenceOk = licenceWorks(licence);
  const count = rows?.length ?? 0;
  const columns: GridColumn<Row>[] = [
    {
      key: "number",
      label: "Document",
      width: 170,
      render: (r) => (
        <Link className="cell-link" href={`/history/?search=${encodeURIComponent(r.documentId)}`}>
          {r.number}
        </Link>
      ),
    },
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
      <PageHeading title="Overview" description="What was delivered and what needs a look." />
      <UpdateNotice view={updates} />
      {error && (
        <div className="error-banner" role="alert">
          <span>{error}</span>
        </div>
      )}
      <div className="stats-grid three">
        <StatCard
          title="Delivered today"
          value={metrics ? metrics.today : "…"}
          note={metrics ? `${metrics.month} this month` : undefined}
          icon={CircleCheck}
          accent="green"
        />
        <StatCard
          title="Need attention"
          value={rows ? count : "…"}
          note={rows ? (count ? "Listed below" : "Nothing open") : undefined}
          icon={AlertCircle}
          accent={count ? "gold" : "green"}
        />
        <StatCard
          title="Licence"
          value={licenceOk ? "Valid" : licence?.state === "Missing" || !licence ? "None yet" : String(licence.state)}
          note={licenceOk && licence?.expires ? `Until ${germanDate(licence.expires)}` : licence?.message || undefined}
          icon={BadgeCheck}
          accent={licenceOk ? "blue" : "gold"}
          href="/service/?tab=licence"
        />
      </div>
      <section className="panel" id="attention" aria-labelledby="attention-heading">
        <div className="section-heading">
          <div>
            <h2 id="attention-heading">Needs attention</h2>
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
          actionsWidth="200px"
          actions={(row) =>
            results[row.id] ? (
              <span className={results[row.id].ok ? "ok-text" : "warn-text"}>{results[row.id].text}</span>
            ) : !row.waiting && me?.canRetry ? (
              <button type="button" className="button secondary" disabled={busy === row.id} onClick={() => void retry(row)}>
                Retry
              </button>
            ) : null
          }
        />
      </section>
    </>
  );
}

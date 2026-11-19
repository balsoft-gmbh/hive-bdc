"use client";
import { useEffect, useState, type ReactNode } from "react";
import { CircleAlert, CircleCheck, Copy, Download, History, LoaderCircle, RefreshCw, ShieldCheck, Undo2 } from "lucide-react";
import { DataGrid, StatusPill, type GridColumn } from "@/components/ui/data-grid";
import { useConsole } from "./console-context";
import { Notice } from "./notice";
import { api, type ApiError } from "@/lib/api";
import { germanDate } from "@/lib/logic";
import {
  historyRows,
  megabytes,
  progress,
  screenOf,
  stepRows,
  supportDetails,
  whenText,
  type HistoryRow,
  type UpdateCheck,
  type UpdateHistoryEntry,
  type UpdateView,
} from "@/lib/updatelogic";
import { ICON, Mark, SMALL_ICON, ServiceBanner, StepList, postAction } from "./service-ui";

// The Updates tab (boards P3 to P6 of the former updates.js). While a switch runs the tab asks every 5 seconds; while
// the instance restarts it says so and reconnects by itself, and once another version answers it loads the whole
// console again, so the menu and the top bar show the new version too.

export const POLL_MS = 5000;

type Message = { kind: "info" | "error"; text: string } | null;
type Action = (id: string, text: string, primary: boolean, path: string, body?: unknown, icon?: ReactNode) => ReactNode;

export function ServiceUpdates({ onView }: { onView?: (view: UpdateView) => void }) {
  const { me, refresh } = useConsole();
  const canChange = Boolean(me?.canChange);
  const [data, setData] = useState<UpdateView | null>(null);
  const [loadError, setLoadError] = useState<Message>(null);
  const [message, setMessage] = useState<Message>(null);
  const [busy, setBusy] = useState("");
  const [reconnecting, setReconnecting] = useState(false);
  const [asking, setAsking] = useState(false);

  // One answer of GET /service/updates drawn from scratch, with the message of the action that led to it.
  const show = (view: UpdateView, next: Message) => {
    setData(view);
    setMessage(next);
    setReconnecting(false);
    setAsking(false);
    onView?.(view);
  };

  // Read once per visit of the tab, as the former page did on every visit.
  useEffect(() => {
    let cancelled = false;
    api<UpdateView>("/service/updates")
      .then((view) => {
        if (!cancelled) show(view, null);
      })
      .catch((error: ApiError) => {
        if (!cancelled) setLoadError({ kind: error.status === 404 ? "info" : "error", text: error.message });
      });
    return () => {
      cancelled = true;
    };
  }, []);

  const screen = screenOf(data);

  useEffect(() => {
    if (!data || screen !== "running") return;
    let cancelled = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    let last = data;
    const tick = async () => {
      try {
        const fresh = await api<UpdateView>("/service/updates");
        if (cancelled) return;
        if (fresh.version !== me?.version) {
          window.location.reload();
          return;
        }
        if (screenOf(fresh) !== "running") {
          show(fresh, null);
          void refresh();
          return;
        }
        if (JSON.stringify(fresh.steps) !== JSON.stringify(last.steps)) {
          show(fresh, null);
          return;
        }
        last = fresh;
      } catch {
        if (cancelled) return;
        setReconnecting(true);
      }
      timer = setTimeout(() => void tick(), POLL_MS);
    };
    timer = setTimeout(() => void tick(), POLL_MS);
    return () => {
      cancelled = true;
      if (timer) clearTimeout(timer);
    };
  }, [data, screen, me?.version]);

  const run = async (id: string, path: string, body?: unknown) => {
    if (!data) return;
    const before = data;
    setBusy(id);
    try {
      const answer = await postAction<{ ok?: boolean; message?: string }>(path, body);
      const fresh = await api<UpdateView>("/service/updates");
      show(fresh, answer?.message ? { kind: "info", text: answer.message } : null);
    } catch (error) {
      const text = (error as Error).message;
      // A stop after the download is in the history (board P6); a refusal before it is only this message.
      const fresh = await api<UpdateView>("/service/updates").catch(() => null);
      if (fresh && screenOf(fresh) === "stopped" && screenOf(before) !== "stopped") show(fresh, null);
      else if (fresh) show(fresh, { kind: "error", text });
      else setMessage({ kind: "error", text });
    } finally {
      setBusy("");
      void refresh();
    }
  };

  const action: Action = (id, text, primary, path, body, icon) =>
    canChange ? (
      <button key={id} type="button" className={`button ${primary ? "primary" : "secondary"}`} disabled={busy !== ""} onClick={() => void run(id, path, body)}>
        {busy === id ? <LoaderCircle className="spin" {...SMALL_ICON} /> : icon}
        {text}
      </button>
    ) : null;

  if (loadError) return <Notice kind={loadError.kind}>{loadError.text}</Notice>;
  if (!data)
    return (
      <div className="service-loading" aria-busy="true">
        <LoaderCircle className="spin" {...ICON} />
        <span className="sr-only">Reading the updates</span>
      </div>
    );

  const zone = data.timeZone;
  const last = (data.history || [])[0];
  let banner: ReactNode = null;
  let main: ReactNode;
  if (screen === "running") main = <RunningCard data={data} zone={zone} reconnecting={reconnecting} />;
  else if (screen === "stopped") {
    const runningVersion = data.installed?.version || data.version;
    banner = (
      <ServiceBanner
        tone="bad"
        role="alert"
        icon={<CircleAlert {...ICON} />}
        title={
          last.state === "returned"
            ? `${last.version} did not run properly. Hive BDC is back on ${runningVersion}.`
            : `The update to ${last.version} stopped. Nothing changed.`
        }
      >
        <span>{runningVersion} runs with your settings, and documents flow as before.</span>
      </ServiceBanner>
    );
    main = <StoppedCard data={data} zone={zone} last={last} action={action} />;
  } else if (screen === "offer") main = <OfferCard data={data} zone={zone} action={action} canChange={canChange} />;
  else if (screen === "done") {
    banner = (
      <ServiceBanner
        tone="ok"
        role="status"
        icon={<CircleCheck {...ICON} />}
        title={`Hive BDC runs ${data.installed?.version} since ${whenText(data.installed?.since, zone).slice(11)}`}
      >
        <span>{last.note}</span>
      </ServiceBanner>
    );
    main = <ChecksCard data={data} zone={zone} />;
  } else main = <IdleCard data={data} action={action} canChange={canChange} />;

  const aside = (
    <>
      {screen === "running" && <WhileCard data={data} />}
      {screen === "done" && data.previous && (
        <ReturnCard version={data.previous.version} canChange={canChange} asking={asking} setAsking={setAsking} busy={busy !== ""} run={run} />
      )}
      {screen !== "running" && screen !== "done" && (
        <InstalledCard data={data} zone={zone} action={action} canChange={canChange} withProblem={screen !== "idle"} />
      )}
    </>
  );
  const hasAside = screen !== "done" || Boolean(data.previous);

  return (
    <>
      {message && <Notice kind={message.kind}>{message.text}</Notice>}
      {banner}
      <div className={`service-update-grid${hasAside ? "" : " single"}`}>
        <div className="service-column">{main}</div>
        {hasAside && <div className="service-column">{aside}</div>}
      </div>
      {screen !== "running" && <HistoryPanel data={data} zone={zone} />}
    </>
  );
}

function OfferCard({ data, zone, action, canChange }: { data: UpdateView; zone?: string; action: Action; canChange: boolean }) {
  const offer = data.available!;
  const steps = ["Download from Balsoft", "Check Balsoft's signature", "Back up the settings", `Switch to ${offer.version}`, "Restart, 2 to 4 minutes"];
  const facts = [offer.releaseDate ? `Released ${germanDate(offer.releaseDate)}` : "", offer.size ? megabytes(offer.size) : "", `you have ${data.version}`];
  return (
    <section className="panel service-panel" aria-labelledby="offer-heading">
      <div className="section-heading">
        <div className="service-update-title">
          <StatusPill tone="amber">New version</StatusPill>
          <h2 id="offer-heading" className="service-update-name">
            Hive BDC {offer.version}
          </h2>
          <p>{facts.filter(Boolean).join(" · ")}</p>
        </div>
        <span className="service-update-icon">
          <Download {...ICON} />
        </span>
      </div>
      <div className="service-body">
        {(offer.notes || []).length > 0 && (
          <ul className="service-notes">
            {(offer.notes || []).map((note, i) => (
              <li key={i}>{note}</li>
            ))}
          </ul>
        )}
        <div className="service-happens">
          <span className="service-happens-title">What happens</span>
          <ol>
            {steps.map((step, i) => (
              <li key={step}>
                <span className="service-happens-no" aria-hidden="true">
                  {i + 1}
                </span>
                <span>{step}</span>
              </li>
            ))}
          </ol>
          <p className="muted">
            Documents wait in their source while it restarts; nothing is lost. {data.version} stays ready, so you can return to it with one click.
          </p>
        </div>
        {!data.canUpdate && <Notice kind="info">{data.cannotUpdate}</Notice>}
        {data.schedule && (
          <p className="muted">
            Planned for {whenText(data.schedule.at, zone)} by {data.schedule.by}.
          </p>
        )}
        {data.canUpdate && canChange && (
          <div className="service-actions end">
            {data.schedule
              ? action("cancel", "Cancel the plan", false, "/service/updates/cancel")
              : action("schedule", "Tonight at 22:00", false, "/service/updates/schedule", { at: "22:00" })}
            {action("run", "Update now", true, "/service/updates/run", undefined, <Download {...SMALL_ICON} />)}
          </div>
        )}
      </div>
    </section>
  );
}

function IdleCard({ data, action, canChange }: { data: UpdateView; action: Action; canChange: boolean }) {
  const check = data.lastCheck;
  return (
    <section className="panel service-panel" aria-labelledby="idle-heading">
      <div className="section-heading">
        <div>
          <h2 id="idle-heading" className="service-update-name">
            Hive BDC {data.version}
          </h2>
        </div>
      </div>
      <div className="service-body">
        {data.canUpdate ? (
          <p className="muted">{!check ? "Hive BDC looks for updates every day at 06:00." : check.ok ? `${data.version} is the newest version.` : check.message}</p>
        ) : (
          <Notice kind="info">{data.cannotUpdate}</Notice>
        )}
        {data.canUpdate && canChange && (
          <div className="service-actions">{action("check", "Look now", false, "/service/updates/check", undefined, <RefreshCw {...SMALL_ICON} />)}</div>
        )}
      </div>
    </section>
  );
}

function RunningCard({ data, zone, reconnecting }: { data: UpdateView; zone?: string; reconnecting: boolean }) {
  const rows = stepRows(data, zone).map((row) => {
    if (row.name === "switch" && row.state === "running") return { ...row, detail: "About 2 to 4 minutes; the console is away for about 2 of them" };
    if (row.name === "running" && row.state === "waiting") return { ...row, detail: "The console reconnects by itself and shows the result" };
    return row;
  });
  const percent = progress(rows);
  const p = data.pending!;
  const back = p.kind === "return";
  return (
    <section className="panel service-panel" aria-labelledby="running-heading" aria-live="polite">
      <div className="section-heading">
        <div>
          <h2 id="running-heading" className="service-update-name">
            {back ? `Returning to ${p.to}` : `Updating to ${p.to}`}
          </h2>
          <p>
            Started {whenText(p.startedAt, zone).slice(11)} by {p.by}
          </p>
        </div>
        {!back && <span className="service-percent">{percent} %</span>}
      </div>
      <div className="service-body">
        {!back && <progress className="service-progress" max={100} value={percent} aria-label="Progress" />}
        <StepList rows={rows} label="Steps of the update" />
        {reconnecting && <Notice kind="info">Hive BDC is restarting. This page reconnects by itself.</Notice>}
      </div>
    </section>
  );
}

function Tip({ tone, icon, children }: { tone: "ok" | "neutral" | "gold"; icon: ReactNode; children: ReactNode }) {
  return (
    <div className="service-tip">
      <span className={`service-tip-icon ${tone}`}>{icon}</span>
      <span>{children}</span>
    </div>
  );
}

function WhileCard({ data }: { data: UpdateView }) {
  const p = data.pending!;
  return (
    <section className="panel service-panel" aria-labelledby="while-heading">
      <div className="section-heading">
        <div>
          <h2 id="while-heading">While it runs</h2>
        </div>
      </div>
      <div className="service-body">
        <Tip tone="ok" icon={<ShieldCheck {...SMALL_ICON} />}>
          Exports wait in Blob Storage, Service Bus or at the web address. Nothing is lost; they are taken right after the restart.
        </Tip>
        <Tip tone="neutral" icon={<History {...SMALL_ICON} />}>
          You can close this page. The update finishes on its own, and the history shows the result.
        </Tip>
        <Tip tone="gold" icon={<Download {...SMALL_ICON} />}>
          {p.kind === "return"
            ? `${p.from} is removed once ${p.to} runs; you can update to it again from the feed.`
            : `${p.from} stays in place. If ${p.to} does not suit you, return to it with one click afterwards.`}
        </Tip>
      </div>
    </section>
  );
}

function StoppedCard({ data, zone, last, action }: { data: UpdateView; zone?: string; last: UpdateHistoryEntry; action: Action }) {
  const rows = (data.steps || []).length ? stepRows(data, zone) : [];
  const copy = () => {
    navigator.clipboard?.writeText(supportDetails(data, last, rows)).catch(() => {});
  };
  return (
    <section className="panel service-panel" aria-labelledby="what-heading">
      <div className="section-heading">
        <div>
          <h2 id="what-heading">What happened</h2>
          <p>{[whenText(last.at, zone), last.by].filter(Boolean).join(" · ")}</p>
        </div>
      </div>
      <div className="service-body">
        {rows.length ? <StepList rows={rows} label="Steps of the update" /> : <p>{last.note}</p>}
        <p className="service-note">
          Other stops look the same: a download that breaks off, too little space, or a new version that does not answer within 10 minutes after the
          restart. In the last case Hive BDC returns to the version before by itself and says so here.
        </p>
        <div className="service-actions end">
          <button type="button" className="button secondary" onClick={copy}>
            <Copy {...SMALL_ICON} />
            Copy details for Balsoft support
          </button>
          {data.available && data.canUpdate ? action("run", "Try again", true, "/service/updates/run") : null}
        </div>
      </div>
    </section>
  );
}

type CheckRow = UpdateCheck & { id: string; time: string };

function ChecksCard({ data, zone }: { data: UpdateView; zone?: string }) {
  const rows: CheckRow[] = (data.checks || []).map((c, i) => ({ ...c, id: String(i), time: whenText(c.at, zone).slice(11) }));
  const columns: GridColumn<CheckRow>[] = [
    {
      key: "name",
      label: "Check",
      width: 240,
      render: (c) => (
        <span className={`service-check${c.state === "done" ? "" : " failed"}`}>
          <Mark state={c.state === "done" ? "done" : "failed"} />
          <strong>{c.name}</strong>
          <span className="sr-only">{c.state === "done" ? ", done" : ", failed"}</span>
        </span>
      ),
    },
    { key: "detail", label: "Detail", render: (c) => <span className="message-cell">{c.detail}</span> },
    { key: "time", label: "Time", width: 90, render: (c) => c.time },
  ];
  return (
    <section className="panel service-panel" aria-labelledby="checks-heading">
      <div className="section-heading">
        <div>
          <h2 id="checks-heading">Checked after the restart</h2>
          <p>{data.installed?.version} checks these itself before it calls the update done.</p>
        </div>
      </div>
      <DataGrid label="Checks after the restart" columns={columns} rows={rows} />
    </section>
  );
}

function ReturnCard({
  version,
  canChange,
  asking,
  setAsking,
  busy,
  run,
}: {
  version: string;
  canChange: boolean;
  asking: boolean;
  setAsking: (value: boolean) => void;
  busy: boolean;
  run: (id: string, path: string, body?: unknown) => Promise<void>;
}) {
  return (
    <section className="panel service-panel" aria-labelledby="return-heading">
      <div className="section-heading">
        <div>
          <h2 id="return-heading">
            <span className="service-tip-icon gold">
              <History {...SMALL_ICON} />
            </span>
            Return to {version}
          </h2>
        </div>
      </div>
      <div className="service-body">
        <p>If something does not work as before, go back. It takes 2 to 4 minutes; documents wait in their source meanwhile.</p>
        <p className="muted">Your settings stay as they are. The backup taken before the update stays on the instance.</p>
        {canChange && (
          <div className="service-actions">
            <button type="button" className="button secondary" disabled={busy} onClick={() => setAsking(true)}>
              <Undo2 {...SMALL_ICON} />
              Return to {version}
            </button>
          </div>
        )}
        {canChange && asking && (
          <div className="service-confirm" role="alert">
            <span>Return to {version}? Hive BDC restarts and is away for about 2 minutes.</span>
            <span className="row-actions">
              <button type="button" className="button secondary" onClick={() => setAsking(false)}>
                Keep
              </button>
              <button type="button" className="button primary" disabled={busy} onClick={() => void run("return", "/service/updates/return")}>
                Return to {version}
              </button>
            </span>
          </div>
        )}
      </div>
    </section>
  );
}

function InstalledCard({ data, zone, action, canChange, withProblem }: { data: UpdateView; zone?: string; action: Action; canChange: boolean; withProblem: boolean }) {
  return (
    <section className="panel service-panel" aria-labelledby="installed-heading">
      <div className="section-heading">
        <div>
          <h2 id="installed-heading">Installed</h2>
        </div>
      </div>
      <div className="service-body">
        <dl className="service-pairs">
          <dt>Version</dt>
          <dd>{data.installed?.version || data.version}</dd>
          {data.installed?.since && (
            <>
              <dt>Since</dt>
              <dd>{whenText(data.installed.since, zone)}</dd>
            </>
          )}
          <dt>Looks for updates</dt>
          <dd>every day at 06:00</dd>
          <dt>Last looked</dt>
          <dd>{data.lastCheck ? whenText(data.lastCheck.at, zone) : "not yet"}</dd>
        </dl>
        {withProblem && data.lastCheck && !data.lastCheck.ok && <p className="warn-text">{data.lastCheck.message}</p>}
        {canChange && (
          <div className="service-actions">{action("check", "Look now", false, "/service/updates/check", undefined, <RefreshCw {...SMALL_ICON} />)}</div>
        )}
      </div>
    </section>
  );
}

type HistoryLine = HistoryRow & { id: string };

function HistoryPanel({ data, zone }: { data: UpdateView; zone?: string }) {
  const rows: HistoryLine[] = historyRows(data, zone)
    .slice(0, 8)
    .map((r, i) => ({ ...r, id: String(i) }));
  const columns: GridColumn<HistoryLine>[] = [
    { key: "version", label: "Version", width: 110, render: (r) => <strong>{r.version}</strong> },
    { key: "badge", label: "State", width: 150, render: (r) => <StatusPill tone={r.tone}>{r.badge}</StatusPill> },
    { key: "when", label: "When", width: 150, render: (r) => <span className="nowrap">{r.when}</span> },
    { key: "by", label: "By", width: 170, render: (r) => r.by },
    { key: "note", label: "What happened", render: (r) => <span className="message-cell">{r.note}</span> },
  ];
  return (
    <section className="panel service-panel" aria-labelledby="history-heading">
      <div className="section-heading">
        <div>
          <h2 id="history-heading">Update history</h2>
        </div>
      </div>
      <DataGrid
        label="Version changes"
        columns={columns}
        rows={rows}
        empty={
          <div className="empty-state compact">
            <p>No update yet.</p>
          </div>
        }
      />
    </section>
  );
}
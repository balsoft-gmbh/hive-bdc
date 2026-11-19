"use client";
import { useEffect, useState, type ReactNode } from "react";
import { Copy, Download, LoaderCircle, RefreshCw, RotateCcw, Undo2 } from "lucide-react";
import { DataGrid, StatusPill, type GridColumn } from "@/components/ui/data-grid";
import { confirmAction } from "@/components/ui/confirm";
import { useConsole } from "./console-context";
import { Notice } from "./notice";
import { api, type ApiError } from "@/lib/api";
import { germanDate } from "@/lib/logic";
import {
  answeredByOlderVersion,
  historyRows,
  megabytes,
  progress,
  restartNotice,
  screenOf,
  stepRows,
  stopText,
  supportDetails,
  whenText,
  UPDATES_ADDRESS,
  type HistoryRow,
  type UpdateCheck,
  type UpdateView,
} from "@/lib/updatelogic";
import { Facts, ICON, Mark, Panel, StepList, postAction, type Fact } from "./service-ui";

// The Updates tab (boards P3 to P6 of the former updates.js). While a switch runs the tab asks every 5 seconds; while
// the instance restarts it says so and reconnects by itself, and once another version answers it loads the whole
// console again, so the menu and the top bar show the new version too. Calm panels in the CRM style (design version 2):
// Update now is the one gold button, and only while an update is offered.

export const POLL_MS = 5000;
const NOTICE_TICK_MS = 10_000;

/** The time, read again every few seconds while the running page is open (U5). */
function useNow(everyMs: number) {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), everyMs);
    return () => clearInterval(timer);
  }, [everyMs]);
  return now;
}

type Message = { kind: "info" | "error"; text: string } | null;
type Action = (id: string, text: string, path: string, options?: { primary?: boolean; body?: unknown; icon?: ReactNode }) => ReactNode;

export function ServiceUpdates({ onView }: { onView?: (view: UpdateView) => void }) {
  const { me, refresh } = useConsole();
  const canChange = Boolean(me?.canChange);
  const [data, setData] = useState<UpdateView | null>(null);
  const [loadError, setLoadError] = useState<Message>(null);
  const [message, setMessage] = useState<Message>(null);
  const [busy, setBusy] = useState("");
  const [reconnecting, setReconnecting] = useState(false);

  // One answer of GET /service/updates drawn from scratch, with the message of the action that led to it.
  const show = (view: UpdateView, next: Message) => {
    setData(view);
    setMessage(next);
    setReconnecting(false);
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
      } catch (error) {
        if (cancelled) return;
        // A version before 1.9.0 has no /api/: it answers 404, so it runs and its own update page shows the result.
        if (answeredByOlderVersion(error)) {
          window.location.assign(UPDATES_ADDRESS);
          return;
        }
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

  const action: Action = (id, text, path, options = {}) =>
    canChange ? (
      <button
        key={id}
        type="button"
        className={`button ${options.primary ? "primary" : "secondary"}`}
        disabled={busy !== ""}
        onClick={() => void run(id, path, options.body)}
      >
        {busy === id ? <LoaderCircle className="spin" {...ICON} /> : options.icon}
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
  const lookNow = action("check", "Look now", "/service/updates/check", { icon: <RefreshCw {...ICON} /> });

  return (
    <div className="service-updates">
      {message && <Notice kind={message.kind}>{message.text}</Notice>}
      {screen === "running" && <RunningPanel data={data} zone={zone} reconnecting={reconnecting} />}
      {screen === "stopped" && <StoppedPanel data={data} zone={zone} action={action} />}
      {screen === "offer" && <OfferPanel data={data} zone={zone} action={action} canChange={canChange} />}
      {screen === "done" && (
        <>
          <DonePanel data={data} zone={zone} canChange={canChange} busy={busy !== ""} run={run} lookNow={lookNow} />
          <ChecksPanel data={data} zone={zone} />
        </>
      )}
      {(screen === "idle" || screen === "offer" || screen === "stopped") && <InstalledPanel data={data} zone={zone} idle={screen === "idle"} lookNow={lookNow} />}
      {screen !== "running" && <HistoryPanel data={data} zone={zone} />}
    </div>
  );
}

function OfferPanel({ data, zone, action, canChange }: { data: UpdateView; zone?: string; action: Action; canChange: boolean }) {
  const offer = data.available!;
  const facts: Fact[] = [
    { label: "Released", value: germanDate(offer.releaseDate) },
    { label: "Download", value: offer.size ? megabytes(offer.size) : "" },
    { label: "Restart", value: "2 to 4 minutes" },
  ];
  if (data.schedule) facts.push({ label: "Planned", value: `${whenText(data.schedule.at, zone)} by ${data.schedule.by}`, span: 2 });
  const actions =
    data.canUpdate && canChange ? (
      <>
        {data.schedule
          ? action("cancel", "Cancel the plan", "/service/updates/cancel")
          : action("schedule", "Tonight at 22:00", "/service/updates/schedule", { body: { at: "22:00" } })}
        {action("run", "Update now", "/service/updates/run", { primary: true, icon: <Download {...ICON} /> })}
      </>
    ) : null;
  return (
    <Panel id="offer" title={`Hive BDC ${offer.version}`} pill={<StatusPill tone="amber">New version</StatusPill>} actions={actions}>
      {!data.canUpdate && <Notice kind="info">{data.cannotUpdate}</Notice>}
      <Facts items={facts} />
      {(offer.notes || []).length > 0 && (
        <div className="service-block">
          <span className="service-label">What&apos;s new</span>
          <ul className="service-notes">
            {(offer.notes || []).map((note, i) => (
              <li key={i}>{note}</li>
            ))}
          </ul>
        </div>
      )}
      {data.canUpdate && <p className="service-footnote">Documents wait in their source during the restart, and {data.version} stays ready to return to.</p>}
    </Panel>
  );
}

function InstalledPanel({ data, zone, idle, lookNow }: { data: UpdateView; zone?: string; idle: boolean; lookNow: ReactNode }) {
  const check = data.lastCheck;
  const pill = idle && check ? <StatusPill tone={check.ok ? "green" : "amber"}>{check.ok ? "Newest version" : "Last look failed"}</StatusPill> : null;
  return (
    <Panel id="installed" title="Installed" pill={pill} actions={lookNow}>
      {idle && !data.canUpdate && <Notice kind="info">{data.cannotUpdate}</Notice>}
      <Facts
        items={[
          { label: "Version", value: data.installed?.version || data.version },
          { label: "Since", value: whenText(data.installed?.since, zone) },
          { label: "Looks for updates", value: "Every day at 06:00" },
          { label: "Last looked", value: check ? whenText(check.at, zone) : "Not yet" },
        ]}
      />
      {check && !check.ok && check.message && <p className="warn-text">{check.message}</p>}
    </Panel>
  );
}

function RunningPanel({ data, zone, reconnecting }: { data: UpdateView; zone?: string; reconnecting: boolean }) {
  const rows = stepRows(data, zone).map((row) => {
    if (row.name === "switch" && row.state === "running") return { ...row, detail: "About 2 to 4 minutes; the console is away for about 2 of them" };
    if (row.name === "running" && row.state === "waiting") return { ...row, detail: "The console reconnects by itself and shows the result" };
    return row;
  });
  const percent = progress(rows);
  const p = data.pending!;
  const back = p.kind === "return";
  const now = useNow(NOTICE_TICK_MS);
  const late = restartNotice(p.startedAt, now, p.to ?? "", p.from ?? data.version, back);
  return (
    <Panel
      id="running"
      title={back ? `Returning to ${p.to}` : `Updating to ${p.to}`}
      actions={<span className="service-meta">{`Started ${whenText(p.startedAt, zone).slice(11)} by ${p.by}`}</span>}
      live
    >
      {!back && (
        <div className="service-progress-row">
          <progress className="service-progress" max={100} value={percent} aria-label="Progress" />
          <span className="service-percent">{percent} %</span>
        </div>
      )}
      <StepList rows={rows} label="Steps of the update" />
      {late ? (
        <Notice kind="info">
          <span>{late}</span>
          <button type="button" className="button secondary" onClick={() => window.location.assign(UPDATES_ADDRESS)}>
            <RefreshCw {...ICON} />
            Reload page
          </button>
        </Notice>
      ) : (
        reconnecting && <Notice kind="info">Hive BDC is restarting. This page reconnects by itself.</Notice>
      )}
      <ul className="service-while" aria-label="While it runs">
        <li>Documents wait in their source and are taken right after the restart.</li>
        <li>You can close this page. The update finishes on its own, and the history shows the result.</li>
        <li>
          {back
            ? `${p.from} is removed once ${p.to} runs; you can update to it again from the feed.`
            : `${p.from} stays in place. If ${p.to} does not suit you, return to it with one click afterwards.`}
        </li>
      </ul>
    </Panel>
  );
}

function StoppedPanel({ data, zone, action }: { data: UpdateView; zone?: string; action: Action }) {
  const last = (data.history || [])[0];
  const rows = (data.steps || []).length ? stepRows(data, zone) : [];
  const stop = stopText(data);
  const copy = () => {
    navigator.clipboard?.writeText(supportDetails(data, last, rows)).catch(() => {});
  };
  return (
    <Panel
      id="stopped"
      title={`Update to ${last.version}`}
      pill={<StatusPill tone={stop.tone}>{stop.badge}</StatusPill>}
      actions={
        <>
          <button type="button" className="button secondary" onClick={copy}>
            <Copy {...ICON} />
            Copy details for Balsoft support
          </button>
          {data.available && data.canUpdate ? action("run", "Try again", "/service/updates/run", { icon: <RotateCcw {...ICON} /> }) : null}
        </>
      }
    >
      <p className="service-lead" role="alert">
        {stop.line}
      </p>
      <Facts
        items={[
          { label: "When", value: whenText(last.at, zone) },
          { label: "By", value: last.by },
        ]}
      />
      {rows.length ? <StepList rows={rows} label="Steps of the update" /> : <p>{last.note}</p>}
    </Panel>
  );
}

function DonePanel({
  data,
  zone,
  canChange,
  busy,
  run,
  lookNow,
}: {
  data: UpdateView;
  zone?: string;
  canChange: boolean;
  busy: boolean;
  run: (id: string, path: string, body?: unknown) => Promise<void>;
  lookNow: ReactNode;
}) {
  const last = (data.history || [])[0];
  const previous = data.previous?.version;
  const back = async () => {
    const yes = await confirmAction(
      `Return to ${previous}? Hive BDC restarts and is away for about 2 minutes. Documents wait in their source, and the settings stay as they are.`,
    );
    if (yes) await run("return", "/service/updates/return");
  };
  return (
    <Panel
      id="done"
      title={`Hive BDC ${data.installed?.version}`}
      pill={<StatusPill tone="green">Running</StatusPill>}
      actions={
        <>
          {lookNow}
          {previous && canChange ? (
            <button type="button" className="button secondary" disabled={busy} title="Back to the version before, if something does not work as before" onClick={() => void back()}>
              <Undo2 {...ICON} />
              Return to {previous}
            </button>
          ) : null}
        </>
      }
    >
      <Facts
        items={[
          { label: "Since", value: whenText(data.installed?.since, zone) },
          { label: "By", value: data.installed?.by || last?.by },
          { label: "What happened", value: last?.note },
          ...(previous ? [{ label: "Kept for return", value: previous }] : []),
        ]}
      />
    </Panel>
  );
}

type CheckRow = UpdateCheck & { id: string; time: string };

function ChecksPanel({ data, zone }: { data: UpdateView; zone?: string }) {
  const rows: CheckRow[] = (data.checks || []).map((c, i) => ({ ...c, id: String(i), time: whenText(c.at, zone).slice(11) }));
  const columns: GridColumn<CheckRow>[] = [
    {
      key: "name",
      label: "Check",
      width: 240,
      render: (c) => (
        <span className={`service-check${c.state === "done" ? "" : " failed"}`}>
          <Mark state={c.state === "done" ? "done" : "failed"} />
          <span>{c.name}</span>
          <span className="sr-only">{c.state === "done" ? ", done" : ", failed"}</span>
        </span>
      ),
    },
    { key: "detail", label: "Detail", render: (c) => <span className="message-cell">{c.detail}</span> },
    { key: "time", label: "Time", width: 90, render: (c) => c.time },
  ];
  return (
    <Panel id="checks" title="Checked after the restart" flush>
      <DataGrid label="Checks after the restart" columns={columns} rows={rows} />
    </Panel>
  );
}

type HistoryLine = HistoryRow & { id: string };

function HistoryPanel({ data, zone }: { data: UpdateView; zone?: string }) {
  const rows: HistoryLine[] = historyRows(data, zone)
    .slice(0, 8)
    .map((r, i) => ({ ...r, id: String(i) }));
  const columns: GridColumn<HistoryLine>[] = [
    { key: "version", label: "Version", width: 100, render: (r) => r.version },
    { key: "badge", label: "State", width: 150, render: (r) => <StatusPill tone={r.tone}>{r.badge}</StatusPill> },
    { key: "when", label: "When", width: 150, render: (r) => <span className="nowrap">{r.when}</span> },
    { key: "by", label: "By", width: 160, render: (r) => r.by },
    { key: "note", label: "What happened", render: (r) => <span className="message-cell">{r.note}</span> },
  ];
  return (
    <Panel id="history" title="Update history" flush>
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
    </Panel>
  );
}
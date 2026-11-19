"use client";
import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { useEffect, useState } from "react";
import { Cable, Plus, Search, X as Clear } from "lucide-react";
import { DataGrid, StatusPill, type GridColumn } from "./ui/data-grid";
import { PageHeading } from "./page-heading";
import { Notice } from "./notice";
import { useConsole } from "./console-context";
import { SaveBar, setTargets, useDraft } from "./save-bar";
import { ICON, useAnswer } from "./outbound-parts";
import { ConnectionDialog, type Editing } from "./connections-dialog";
import * as X from "@/lib/exportlogic";

// The Connections page (board W12, connections.js) as an ERP list: every sign-in Hive BDC uses in one grid, the two
// Microsoft 365 apps in a second one. A name opens its record dialog (/connections/?open=id or ?app=sharepoint), where
// Test, Apply and Remove sit; "+ New connection" opens the dialog with the kind choice first. Changes go into the one
// draft of save-bar.tsx; passwords, keys and secrets are sent only with Save, into the instance's Key Vault.

type ConnectionRow = { id: string; c: any };
type AppRow = { id: "sharepoint" | "mail" };
type Pill = { tone: "green" | "amber" | "rose" | "neutral"; text: string; title?: string };

const DESCRIPTION = "Accounts Hive BDC signs in with. Secrets stay in Key Vault.";

/** Save and Discard start the page afresh: no open question (the former state.ui = {}). */
export function ConnectionsPage() {
  const { generation } = useDraft();
  return <Connections key={generation} />;
}

/** "2" with the full list as title text and for screen readers. */
function UsedBy({ uses }: { uses: string[] }) {
  if (!uses.length) return <span className="cell-muted">None</span>;
  return (
    <span title={uses.join(", ")}>
      {uses.length}
      <span className="sr-only">: {uses.join(", ")}</span>
    </span>
  );
}

function Connections() {
  const router = useRouter();
  const params = useSearchParams();
  const { me, config } = useConsole();
  const state = useDraft();
  const ro = !me?.canChange;
  const kinds = useAnswer<{ kinds: any[] }>("/connections/kinds");
  const identity = useAnswer<any>("/setup/identity");
  const targets = useAnswer<{ targets: any[] }>("/targets");
  const [creating, setCreating] = useState(false);
  const [search, setSearch] = useState("");
  useEffect(() => {
    if (targets.data) setTargets(targets.data.targets || []);
  }, [targets.data]);

  const loadError = kinds.error || identity.error || targets.error;
  const body = state.body;
  if (!body) return <PageHeading title="Connections" description={DESCRIPTION} />;
  const kindList = kinds.data?.kinds || [];
  const tests = state.tests;
  const dirty = X.draftChanges(state.original, body).length > 0;

  const openId = params.get("open");
  const app = params.get("app");
  const editing: Editing | null = creating
    ? { what: "new" }
    : openId && body.connections.some((c: any) => c.id === openId)
      ? { what: "connection", id: openId }
      : app === "sharepoint" || app === "mail"
        ? { what: app }
        : null;
  const close = () => {
    setCreating(false);
    if (openId || app) router.replace("/connections/", { scroll: false });
  };

  const testPill = (id: string): Pill => {
    const t = X.testState(tests[id]);
    return { tone: t.tone, text: t.text, title: tests[id] ? `${tests[id].at}: ${tests[id].message}` : undefined };
  };
  const pill = (p: Pill) => (
    <span title={p.title}>
      <StatusPill tone={p.tone}>{p.text}</StatusPill>
    </span>
  );

  const term = search.trim().toLowerCase();
  const connectionRows: ConnectionRow[] = body.connections
    .filter((c: any) => !term || c.id.toLowerCase().includes(term) || X.connectionKindName(c.kind, kindList).toLowerCase().includes(term))
    .map((c: any) => ({ id: c.id, c }));
  const connectionColumns: GridColumn<ConnectionRow>[] = [
    {
      key: "name",
      label: "Name",
      width: 220,
      render: ({ c }) => (
        <Link className="cell-link" href={`/connections/?open=${encodeURIComponent(c.id)}`} replace scroll={false}>
          {c.kind === "https" ? `Key ${c.id}` : c.id}
        </Link>
      ),
    },
    { key: "kind", label: "Kind", width: 170, render: ({ c }) => X.connectionKindName(c.kind, kindList) },
    { key: "signs", label: "Signs in", render: ({ c }) => <span className="wrap-cell">{X.signInWords(c)}</span> },
    { key: "used", label: "Used by", width: 100, render: ({ c }) => <UsedBy uses={X.connectionUses(body, c.id)} /> },
    {
      key: "state",
      label: "State",
      width: 130,
      render: ({ c }) => {
        if (c.kind !== "https") return pill(testPill(c.id));
        const stored = (config?.connections || []).some((x: any) => x.id === c.id && x.hasSecret) || Boolean(c.secret);
        return pill(stored ? { tone: "green", text: "Key stored" } : { tone: "amber", text: "No key yet" });
      },
    },
  ];

  const spApp = body.sharePointAuth || {};
  const appSet = Boolean(spApp.clientId);
  const mail = body.mail || {};
  const mailSet = Boolean(mail.enabled && mail.from);
  const wanting = X.mailCompanies(body);
  const appRows: AppRow[] = [{ id: "sharepoint" }, { id: "mail" }];
  const appColumns: GridColumn<AppRow>[] = [
    {
      key: "name",
      label: "Name",
      width: 220,
      render: (r) => (
        <Link className="cell-link" href={`/connections/?app=${r.id}`} replace scroll={false}>
          {r.id === "sharepoint" ? "SharePoint app" : "Document mail"}
        </Link>
      ),
    },
    { key: "kind", label: "Kind", width: 170, render: (r) => (r.id === "sharepoint" ? "Entra app" : "Entra app and mailbox") },
    {
      key: "signs",
      label: "Signs in",
      render: (r) =>
        r.id === "sharepoint" ? (
          appSet ? (
            <span className="wrap-cell">As client {spApp.clientId}</span>
          ) : (
            <span className="cell-muted">Not set up yet</span>
          )
        ) : mail.from ? (
          <span className="wrap-cell">From {mail.from}</span>
        ) : (
          <span className="cell-muted">Not set up yet</span>
        ),
    },
    { key: "used", label: "Used by", width: 100, render: (r) => <UsedBy uses={r.id === "sharepoint" ? X.sharePointUses(body) : wanting} /> },
    {
      key: "state",
      label: "State",
      width: 130,
      render: (r) =>
        r.id === "sharepoint"
          ? pill(appSet ? testPill("#sharepoint") : { tone: "amber", text: "Not set up" })
          : pill(
              mailSet
                ? testPill("#mail")
                : X.mailboxConfigured(mail)
                  ? { tone: "neutral", text: "Off" }
                  : { tone: wanting.length ? "amber" : "neutral", text: "Not set up" },
            ),
    },
  ];

  return (
    <>
      <PageHeading
        title="Connections"
        description={DESCRIPTION}
        actions={
          ro ? undefined : (
            <button type="button" className={`button ${dirty || editing ? "secondary" : "primary"}`} onClick={() => setCreating(true)}>
              <Plus {...ICON} aria-hidden="true" />
              New connection
            </button>
          )
        }
      />
      {loadError && <Notice kind="error">{loadError}</Notice>}
      <div className="list-toolbar">
        <div className="field search">
          <label htmlFor="connections-search">Search</label>
          <div className="search-box">
            <Search {...ICON} aria-hidden="true" />
            <input id="connections-search" autoComplete="off" value={search} onChange={(e) => setSearch(e.target.value)} placeholder="Name or kind" />
            {search && (
              <button type="button" className="icon-button small" aria-label="Clear search" title="Clear search" onClick={() => setSearch("")}>
                <Clear size={15} strokeWidth={1.75} />
              </button>
            )}
          </div>
        </div>
        <div className="list-toolbar-right">
          <span className="record-count">
            {connectionRows.length} {connectionRows.length === 1 ? "connection" : "connections"}
          </span>
        </div>
      </div>
      <DataGrid
        label="Connections"
        columns={connectionColumns}
        rows={connectionRows}
        busy={kinds.loading}
        empty={
          <div className="empty-state compact">
            <Cable size={22} strokeWidth={1.75} aria-hidden="true" />
            <h2>{term ? "No connection matches." : "No connection yet."}</h2>
            {!term && <p>{ro ? "An administrator adds them." : "New connection sets up the first one."}</p>}
          </div>
        }
      />
      <h2 className="list-heading" id="apps-heading">
        Microsoft 365 apps
      </h2>
      <DataGrid label="Microsoft 365 apps" columns={appColumns} rows={appRows} />
      <SaveBar hidden={editing !== null} />
      {editing && (
        <ConnectionDialog
          key={`${state.generation}|${editing.what}|${"id" in editing ? editing.id : ""}`}
          editing={editing}
          kinds={kindList}
          identity={identity.data}
          ro={ro}
          onClose={close}
        />
      )}
    </>
  );
}
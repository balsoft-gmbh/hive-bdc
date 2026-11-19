"use client";
import { useEffect, useState } from "react";
import { Cable, Plus } from "lucide-react";
import { DataGrid, type GridColumn } from "./ui/data-grid";
import { PageHeading } from "./page-heading";
import { Notice } from "./notice";
import { useConsole } from "./console-context";
import { SaveBar, change, recordTest, setTargets, useDraft } from "./save-bar";
import { Chip, ConfirmStrip, ICON, post, useAnswer } from "./outbound-parts";
import { ConnectionDialog, type Editing } from "./connections-dialog";
import * as X from "@/lib/exportlogic";

// The Connections page (board W12, connections.js): every sign-in Hive BDC uses, the SharePoint app and the document
// mailbox, each with its state, who uses it, Test, Edit and Remove. Changes go into the one draft of save-bar.tsx;
// passwords, keys and secrets are sent only with Save, into the instance's Key Vault, and never read back.

type ConnectionRow = { id: string; c: any };
type AppRow = { id: "#sharepoint" | "#mail" };

/** Save and Discard start the page afresh: no open dialog or question (the former state.ui = {}). */
export function ConnectionsPage() {
  const { generation } = useDraft();
  return <Connections key={generation} />;
}

function Connections() {
  const { me, config } = useConsole();
  const state = useDraft();
  const ro = !me?.canChange;
  const kinds = useAnswer<{ kinds: any[] }>("/connections/kinds");
  const identity = useAnswer<any>("/setup/identity");
  const targets = useAnswer<{ targets: any[] }>("/targets");
  const [editing, setEditing] = useState<Editing | null>(null);
  const [removing, setRemoving] = useState<string | null>(null);
  useEffect(() => {
    if (targets.data) setTargets(targets.data.targets || []);
  }, [targets.data]);

  const loadError = kinds.error || identity.error || targets.error;
  const body = state.body;
  const heading = (
    <PageHeading
      eyebrow="SET UP"
      title="Connections"
      description="The systems Hive BDC signs in to. Each is set up once and used by any document type. Passwords, keys and secrets go to this instance's Key Vault and are never shown again."
    />
  );
  if (!body) return heading;
  const kindList = kinds.data?.kinds || [];
  const tests = state.tests;

  const testChip = (id: string) => {
    const t = tests[id];
    return <Chip ok={t ? t.ok : null}>{X.testWords(t)}</Chip>;
  };
  const testLine = (id: string) => {
    const t = tests[id];
    return t ? <span className={t.ok ? "muted small" : "warn-text small"}>{`${t.at}: ${t.message}`}</span> : null;
  };

  // A storage connection is tested on a container it is used with: an instance often has a role on that container
  // only, so listing the whole account would be refused although the connection works.
  const testConnection = async (c: any) => {
    const containers = c.kind === "azure-storage" ? X.connectionContainers(state.body, c.id) : [];
    const unchanged = JSON.stringify(c) === JSON.stringify((state.original.connections || []).find((x: any) => x.id === c.id));
    if (containers.length && unchanged) {
      const { container, prefix } = containers[0];
      const answer = await post("/setup/check-source", { connection: c.id, container, prefix });
      recordTest(c.id, { ok: answer.ok, message: `Container ${container}: ${answer.message}` });
    } else {
      recordTest(c.id, await post("/connections/test", { id: c.id, kind: c.kind, settings: c.settings, secret: c.secret || "" }));
    }
  };

  const connectionRows: ConnectionRow[] = body.connections.map((c: any) => ({ id: c.id, c }));
  const connectionColumns: GridColumn<ConnectionRow>[] = [
    {
      key: "name",
      label: "Connection",
      width: 190,
      render: ({ c }) => <strong className="wrap-cell">{c.kind === "https" ? `Key ${c.id}` : c.id}</strong>,
    },
    {
      key: "words",
      label: "Signs in",
      render: ({ c }) => {
        const uses = X.connectionUses(state.body, c.id);
        return (
          <span className="cell-stack">
            <span className="wrap-cell">{c.kind === "https" ? `Web address · ${X.connectionWords(c)}` : X.connectionWords(c)}</span>
            <span className="cell-secondary wrap-cell" title={uses.join(", ")}>
              {X.usesWords(uses)}
            </span>
          </span>
        );
      },
    },
    {
      key: "state",
      label: "State",
      width: 220,
      render: ({ c }) => {
        if (c.kind === "https") {
          const stored = (config?.connections || []).some((x: any) => x.id === c.id && x.hasSecret);
          return <Chip ok={stored || Boolean(c.secret)}>{stored || c.secret ? "Key stored" : "No key yet"}</Chip>;
        }
        return (
          <span className="cell-stack">
            {testChip(c.id)}
            {testLine(c.id)}
          </span>
        );
      },
    },
  ];

  const app = body.sharePointAuth || {};
  const appStored = Boolean(config?.sharePointAuth && config.sharePointAuth.secretSet);
  const appSet = Boolean(app.clientId);
  const firstLibrary = body.documentTypes
    .flatMap((t: any) => t.companies)
    .map((c: any) => c.sharePoint)
    .find((sp: any) => sp && sp.enabled && sp.siteUrl && sp.library);
  const appChanged = JSON.stringify(app) !== JSON.stringify(state.original.sharePointAuth || {});
  const blocked = X.sharePointTestBlock(firstLibrary, appChanged);
  const mail = body.mail || {};
  const mailStored = Boolean(config?.mail && config.mail.secretSet);
  const mailSet = Boolean(mail.enabled && mail.from);
  const mailConfigured = X.mailboxConfigured(mail);
  const wanting = body.documentTypes.flatMap((t: any) =>
    t.companies.filter((c: any) => c.mail === true).map((c: any) => `${t.id} › ${c.dataAreaId}`),
  );

  const testSharePoint = async () =>
    recordTest(
      "#sharepoint",
      await post("/config/test-sharepoint", { sharePoint: { siteUrl: firstLibrary.siteUrl, library: firstLibrary.library, folder: "" } }),
    );
  const testMail = async () =>
    recordTest(
      "#mail",
      await post("/config/test-mail", { mail: { tenantId: mail.tenantId, clientId: mail.clientId, from: mail.from, redirectTo: mail.redirectTo } }),
    );

  const appRows: AppRow[] = [{ id: "#sharepoint" }, { id: "#mail" }];
  const appColumns: GridColumn<AppRow>[] = [
    { key: "name", label: "App", width: 190, render: (r) => <strong>{r.id === "#sharepoint" ? "SharePoint app" : "Document mail"}</strong> },
    {
      key: "words",
      label: "Signs in",
      render: (r) => {
        const uses = r.id === "#sharepoint" ? X.sharePointUses(body) : [];
        return (
          <span className="cell-stack">
            <span className="wrap-cell">{r.id === "#sharepoint" ? X.sharePointAppLine(app, appStored) : X.mailboxLine(mail, mailStored)}</span>
            {r.id === "#sharepoint" ? (
              <span className="cell-secondary wrap-cell" title={uses.join(", ")}>
                {X.usesWords(uses)}
              </span>
            ) : (
              <span className={`cell-secondary wrap-cell${wanting.length && !mailSet ? " warn-text" : ""}`}>
                {wanting.length ? `${wanting.length} ${wanting.length === 1 ? "company wants" : "companies want"} to mail` : "No company mails yet"}
              </span>
            )}
          </span>
        );
      },
    },
    {
      key: "state",
      label: "State",
      width: 220,
      render: (r) => (
        <span className="cell-stack">
          {r.id === "#sharepoint" ? (
            appSet ? (
              testChip("#sharepoint")
            ) : (
              <Chip ok={false}>Not set up</Chip>
            )
          ) : mailSet ? (
            testChip("#mail")
          ) : (
            <Chip ok={false}>{mailConfigured ? "Off" : "Not set up"}</Chip>
          )}
          {testLine(r.id)}
        </span>
      ),
    },
  ];

  const removingRow = removing ? body.connections.find((c: any) => c.id === removing) : null;
  const removingUses = removingRow ? X.connectionUses(body, removingRow.id) : [];

  return (
    <>
      {heading}
      {loadError && <Notice kind="error">{loadError}</Notice>}
      {removingRow &&
        (removingUses.length ? (
          <Notice kind="warn">
            <span>Still used by {removingUses.join(", ")}. Change those first.</span>
            <button type="button" className="text-button quiet-link" onClick={() => setRemoving(null)}>
              Close
            </button>
          </Notice>
        ) : (
          <ConfirmStrip
            text={`Remove the connection ${removingRow.id}?`}
            onKeep={() => setRemoving(null)}
            onRemove={() => {
              change((d) => X.removeConnection(d, removingRow.id));
              setRemoving(null);
            }}
          />
        ))}
      <DataGrid
        label="Connections"
        columns={connectionColumns}
        rows={connectionRows}
        busy={kinds.loading}
        rowClassName={(r) => (removing === r.id ? "bdc-row-removing" : tests[r.id] && !tests[r.id].ok ? "bdc-row-warn" : undefined)}
        actionsWidth="220px"
        actions={
          ro
            ? undefined
            : ({ c }) => (
                <span className="row-actions">
                  {c.kind !== "https" && (
                    <button type="button" className="button secondary" aria-label={`Test ${c.id}`} onClick={() => void testConnection(c)}>
                      Test
                    </button>
                  )}
                  {(c.kind === "https" || kindList.some((k) => k.kind === c.kind)) && (
                    <button
                      type="button"
                      className="button secondary"
                      aria-label={c.kind === "https" ? `Replace key ${c.id}` : `Edit ${c.id}`}
                      onClick={() => setEditing(c.kind === "https" ? { what: "key", id: c.id } : { what: "connection", id: c.id })}
                    >
                      {c.kind === "https" ? "Replace key" : "Edit"}
                    </button>
                  )}
                  <button
                    type="button"
                    className="text-button quiet-link"
                    aria-label={`Remove ${c.id}`}
                    onClick={() => setRemoving(removing === c.id ? null : c.id)}
                  >
                    Remove
                  </button>
                </span>
              )
        }
        empty={
          <div className="empty-state compact">
            <Cable size={22} strokeWidth={1.75} aria-hidden="true" />
            <h2>No connection yet.</h2>
            <p>{ro ? "An administrator adds the systems Hive BDC signs in to." : "Choose a kind under Add a connection to set up the first one."}</p>
          </div>
        }
      />
      <section className="panel connections-apps" aria-labelledby="apps-heading">
        <div className="section-heading">
          <div>
            <h2 id="apps-heading">SharePoint and mail</h2>
            <p>The apps your IT makes in your Microsoft 365 tenant.</p>
          </div>
        </div>
        <DataGrid
          label="SharePoint and mail"
          columns={appColumns}
          rows={appRows}
          rowClassName={(r) => (tests[r.id] && !tests[r.id].ok ? "bdc-row-warn" : undefined)}
          actionsWidth="220px"
          actions={
            ro
              ? undefined
              : (r) =>
                  r.id === "#sharepoint" ? (
                    <span className="row-actions">
                      {appSet && (
                        <button
                          type="button"
                          className="button secondary"
                          aria-label="Test the SharePoint app"
                          disabled={Boolean(blocked)}
                          title={blocked || undefined}
                          onClick={() => void testSharePoint()}
                        >
                          Test
                        </button>
                      )}
                      <button
                        type="button"
                        className={appSet ? "button secondary" : "button primary"}
                        onClick={() => setEditing({ what: "sharepoint" })}
                      >
                        {appSet ? "Edit" : "Set up the SharePoint app"}
                      </button>
                      {appSet && blocked && <span className="muted small">{blocked}</span>}
                    </span>
                  ) : (
                    <span className="row-actions">
                      {mailSet && (
                        <button type="button" className="button secondary" aria-label="Test document mail" onClick={() => void testMail()}>
                          Test
                        </button>
                      )}
                      <button
                        type="button"
                        className={mailConfigured ? "button secondary" : "button primary"}
                        onClick={() => setEditing({ what: "mail" })}
                      >
                        {mailConfigured ? "Edit" : "Set up mail"}
                      </button>
                    </span>
                  )
          }
        />
      </section>
      {!ro && (
        <section className="panel connections-add" aria-labelledby="add-heading">
          <div className="section-heading">
            <div>
              <h2 id="add-heading">Add a connection</h2>
              <p>
                Azure Storage and Service Bus can sign in as this instance (identity {identity.data?.objectId || "unknown"}): your IT gives it a role,
                the check on the Outbound page shows the command. A connection still in use cannot be removed.
              </p>
            </div>
          </div>
          <div className="setup-tiles panel-inset">
            {kindList.map((k) => (
              <button key={k.kind} type="button" className="setup-tile tile-button" onClick={() => setEditing({ what: "new", kind: k })}>
                <span className="setup-tile-icon">
                  <Plus {...ICON} aria-hidden="true" />
                </span>
                <span className="setup-tile-body">
                  <strong>{k.name}</strong>
                  <span>{k.description || ""}</span>
                </span>
              </button>
            ))}
          </div>
        </section>
      )}
      {editing && !ro && (
        <ConnectionDialog
          key={`${state.generation}|${editing.what}|${"id" in editing ? editing.id : ""}`}
          editing={editing}
          kinds={kindList}
          identity={identity.data}
          onClose={() => setEditing(null)}
        />
      )}
      <SaveBar />
    </>
  );
}
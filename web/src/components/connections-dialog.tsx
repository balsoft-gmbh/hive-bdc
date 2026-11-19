"use client";
import { useState, type ReactNode } from "react";
import { ArrowLeft, Cable, FileArchive, HardDrive, MessagesSquare } from "lucide-react";
import { TogglePill } from "./toggle-pill";
import { BdcDialog, ConfirmStrip, CopyCode, Facts, Field, ICON, RemoveButton, Result, post } from "./outbound-parts";
import { change, draft, forgetTest, recordTest, useDraft } from "./save-bar";
import { useConsole } from "./console-context";
import { Notice } from "./notice";
import * as X from "@/lib/exportlogic";

// The record dialogs of the Connections page (board W12, the forms of connections.js) in the ERP's layout: title with
// a meta line, facts, the fields, and the footer Remove (bottom left), Test, Cancel and Apply. Apply puts the values
// into the draft; secrets leave the browser only with Save, into the instance's Key Vault. A new connection starts
// with the choice of its kind.

export type Editing = { what: "new" } | { what: "connection"; id: string } | { what: "sharepoint" } | { what: "mail" };

type Answer = { ok: boolean; message: string } | "busy" | null;

const KEPT = "Goes to Key Vault and is never shown again.";
const KIND_ICONS: Record<string, typeof Cable> = { "azure-storage": HardDrive, servicebus: MessagesSquare, docuware: FileArchive };

function SecretInput({ id, stored, value, onChange, disabled }: { id: string; stored: boolean; value: string; onChange: (v: string) => void; disabled?: boolean }) {
  return (
    <input
      id={id}
      type="password"
      autoComplete="off"
      placeholder={stored ? "Stored. Leave empty to keep it." : ""}
      value={value}
      disabled={disabled}
      onChange={(e) => onChange(e.target.value)}
    />
  );
}

function AnswerLine({ answer }: { answer: Answer }) {
  return (
    <div aria-live="polite" className="dialog-result">
      {answer === "busy" ? <span className="muted">Signing in</span> : answer ? <Result ok={answer.ok}>{answer.message}</Result> : null}
    </div>
  );
}

/** The last test of a connection for its facts: "Works at 10:42. Signed in." */
function lastTest(test: { ok: boolean; message: string; at: string } | undefined) {
  if (!test) return <span className="cell-muted">Not tested yet</span>;
  return <Result ok={test.ok}>{`${test.ok ? "Works" : "Refused"} at ${test.at}. ${test.message}`}</Result>;
}

const usedBy = (uses: string[]) => (uses.length ? uses.join(", ") : <span className="cell-muted">Not used yet</span>);

/** Cancel and the primary button of a dialog; a Viewer gets Close only. */
function Buttons({ ro, onClose, primary, test }: { ro: boolean; onClose: () => void; primary?: ReactNode; test?: ReactNode }) {
  if (ro)
    return (
      <button type="button" className="button secondary" onClick={onClose}>
        Close
      </button>
    );
  return (
    <>
      {test}
      <button type="button" className="button secondary" onClick={onClose}>
        Cancel
      </button>
      {primary}
    </>
  );
}

export function ConnectionDialog({
  editing,
  kinds,
  identity,
  ro,
  onClose,
}: {
  editing: Editing;
  kinds: any[];
  identity: any;
  ro: boolean;
  onClose: () => void;
}) {
  switch (editing.what) {
    case "new":
      return <NewConnection kinds={kinds} identity={identity} onClose={onClose} />;
    case "connection":
      return <EditConnection id={editing.id} kinds={kinds} ro={ro} onClose={onClose} />;
    case "sharepoint":
      return <SharePointApp identity={identity} ro={ro} onClose={onClose} />;
    case "mail":
      return <Mailbox identity={identity} ro={ro} onClose={onClose} />;
  }
}

/** The hint under the secret of a kind that can sign in as the instance itself. */
const instanceHint = (identity: any) =>
  `Empty signs in as this instance${identity?.objectId ? ` (identity ${identity.objectId})` : ""}. Your IT gives it a role.`;

function NewConnection({ kinds, identity, onClose }: { kinds: any[]; identity: any; onClose: () => void }) {
  const [kind, setKind] = useState<any>(null);
  if (!kind) {
    return (
      <BdcDialog
        title="New connection"
        subtitle="Choose what Hive BDC signs in to."
        onClose={onClose}
        footer={
          <button type="button" className="button secondary" onClick={onClose}>
            Cancel
          </button>
        }
      >
        <div className="setup-tiles bdc-tiles">
          {kinds.map((k) => {
            const Icon = KIND_ICONS[k.kind] ?? Cable;
            return (
              <button key={k.kind} type="button" className="setup-tile tile-button" onClick={() => setKind(k)}>
                <span className="setup-tile-icon">
                  <Icon {...ICON} aria-hidden="true" />
                </span>
                <span className="setup-tile-body">
                  <strong>{k.name}</strong>
                  <span>{k.description || ""}</span>
                </span>
              </button>
            );
          })}
        </div>
      </BdcDialog>
    );
  }
  return <NewConnectionForm kind={kind} identity={identity} onBack={() => setKind(null)} onClose={onClose} />;
}

function NewConnectionForm({ kind, identity, onBack, onClose }: { kind: any; identity: any; onBack: () => void; onClose: () => void }) {
  const state = useDraft();
  const [name, setName] = useState(() => X.suggestId(state.body.connections, kind.kind));
  const [settings, setSettings] = useState<Record<string, string>>({});
  const [secret, setSecret] = useState("");
  const [answer, setAnswer] = useState<Answer>(null);
  const values = () => ({
    id: name.trim(),
    kind: kind.kind,
    settings: Object.fromEntries(kind.settings.map((s: any) => [s.key, (settings[s.key] || "").trim()])),
    secret,
  });
  const signsInAsInstance = kind.kind === "azure-storage" || kind.kind === "servicebus";
  return (
    <BdcDialog
      title={`New ${kind.name} connection`}
      subtitle={kind.description}
      onClose={onClose}
      leading={
        <button type="button" className="button ghost" onClick={onBack}>
          <ArrowLeft {...ICON} aria-hidden="true" />
          Other kind
        </button>
      }
      footer={
        <Buttons
          ro={false}
          onClose={onClose}
          test={
            <button
              type="button"
              className="button secondary"
              onClick={async () => {
                setAnswer("busy");
                const result = await post("/connections/test", values());
                setAnswer({ ok: Boolean(result.ok), message: result.message });
              }}
            >
              Test connection
            </button>
          }
          primary={
            <button
              type="button"
              className="button primary"
              onClick={() => {
                try {
                  change((d) => X.addConnection(d, values()));
                  onClose();
                } catch (e) {
                  setAnswer({ ok: false, message: (e as Error).message });
                }
              }}
            >
              Add
            </button>
          }
        />
      }
    >
      <div className="form-grid-plain">
        <Field label="Name" htmlFor="new-connection-name">
          <input id="new-connection-name" type="text" autoComplete="off" spellCheck={false} value={name} onChange={(e) => setName(e.target.value)} />
        </Field>
        {kind.settings.map((s: any) => (
          <Field key={s.key} label={s.label} htmlFor={`new-connection-${s.key}`} hint={s.hint}>
            <input
              id={`new-connection-${s.key}`}
              type="text"
              autoComplete="off"
              spellCheck={false}
              value={settings[s.key] || ""}
              onChange={(e) => setSettings((v) => ({ ...v, [s.key]: e.target.value }))}
            />
          </Field>
        ))}
        <Field label={kind.secretLabel || "Secret"} htmlFor="new-connection-secret" hint={signsInAsInstance ? instanceHint(identity) : KEPT}>
          <SecretInput id="new-connection-secret" stored={false} value={secret} onChange={setSecret} />
        </Field>
      </div>
      <AnswerLine answer={answer} />
    </BdcDialog>
  );
}

/**
 * A storage connection is tested on a container it is used with: an instance often has a role on that container only,
 * so listing the whole account would be refused although the connection works. The result is kept for the list.
 */
async function testSaved(c: any): Promise<void> {
  const containers = c.kind === "azure-storage" ? X.connectionContainers(draft.body, c.id) : [];
  const unchanged = JSON.stringify(c) === JSON.stringify((draft.original.connections || []).find((x: any) => x.id === c.id));
  let answer: { ok?: boolean; message?: string };
  if (containers.length && unchanged) {
    const { container, prefix } = containers[0];
    const result = await post("/setup/check-source", { connection: c.id, container, prefix });
    answer = { ok: result.ok, message: `Container ${container}: ${result.message}` };
  } else {
    answer = await post("/connections/test", { id: c.id, kind: c.kind, settings: c.settings, secret: c.secret || "" });
  }
  recordTest(c.id, answer);
}

/** Remove at the bottom left: a connection still in use says who uses it, an unused one asks Keep or Remove. */
function useRemoval(id: string, onClose: () => void) {
  const state = useDraft();
  const [asking, setAsking] = useState(false);
  const uses = X.connectionUses(state.body, id);
  const strip = asking ? (
    uses.length ? (
      <Notice kind="warn">Still used by {uses.join(", ")}. Change those first.</Notice>
    ) : (
      <ConfirmStrip
        text={`Remove the connection ${id}? Nothing changes until you save.`}
        onKeep={() => setAsking(false)}
        onRemove={() => {
          change((d) => X.removeConnection(d, id));
          onClose();
        }}
      />
    )
  ) : null;
  const button = <RemoveButton onClick={() => setAsking(true)}>Remove</RemoveButton>;
  return { strip, button };
}

function EditConnection({ id, kinds, ro, onClose }: { id: string; kinds: any[]; ro: boolean; onClose: () => void }) {
  const { config } = useConsole();
  const state = useDraft();
  const c = state.body.connections.find((x: any) => x.id === id);
  const kind = kinds.find((k) => k.kind === c?.kind);
  const [settings, setSettings] = useState<Record<string, string>>(() =>
    Object.fromEntries((kind?.settings || []).map((s: any) => [s.key, (c?.settings || {})[s.key] || ""])),
  );
  const [secret, setSecret] = useState("");
  const [answer, setAnswer] = useState<Answer>(null);
  const removal = useRemoval(id, onClose);
  if (!c) return null;
  if (c.kind === "https") return <KeyDialog c={c} ro={ro} onClose={onClose} />;
  const stored = (config?.connections || []).some((x: any) => x.id === c.id && x.hasSecret);
  const typed = () => Object.fromEntries(Object.entries(settings).map(([k, v]) => [k, v.trim()]));
  const edited = secret !== "" || Object.entries(typed()).some(([k, v]) => v !== ((c.settings || {})[k] || ""));
  const test = async () => {
    setAnswer("busy");
    if (!edited) {
      // The result is kept for the list and shows under Last test.
      await testSaved(c);
      setAnswer(null);
      return;
    }
    const result = await post("/connections/test", { id: c.id, kind: c.kind, settings: { ...c.settings, ...typed() }, secret: secret || c.secret || "" });
    setAnswer({ ok: Boolean(result.ok), message: result.message });
  };
  return (
    <BdcDialog
      title={c.id}
      subtitle={X.connectionWords(c)}
      onClose={onClose}
      leading={ro ? undefined : removal.button}
      footer={
        <Buttons
          ro={ro}
          onClose={onClose}
          test={
            <button type="button" className="button secondary" onClick={() => void test()}>
              Test connection
            </button>
          }
          primary={
            kind ? (
              <button
                type="button"
                className="button primary"
                onClick={() => {
                  change((d) => X.updateConnection(d, c.id, typed(), secret));
                  forgetTest(c.id);
                  onClose();
                }}
              >
                Apply
              </button>
            ) : null
          }
        />
      }
    >
      <Facts
        items={[
          ["Kind", X.connectionKindName(c.kind, kinds)],
          ["Used by", usedBy(X.connectionUses(state.body, c.id))],
          ["Last test", lastTest(state.tests[c.id])],
        ]}
      />
      {kind ? (
        <div className="form-grid-plain">
          {kind.settings.map((s: any) => (
            <Field key={s.key} label={s.label} htmlFor={`edit-connection-${s.key}`} hint={s.hint}>
              <input
                id={`edit-connection-${s.key}`}
                type="text"
                autoComplete="off"
                spellCheck={false}
                value={settings[s.key] || ""}
                disabled={ro}
                onChange={(e) => setSettings((v) => ({ ...v, [s.key]: e.target.value }))}
              />
            </Field>
          ))}
          <Field label={kind.secretLabel || "Secret"} htmlFor="edit-connection-secret" hint={ro ? undefined : KEPT}>
            <SecretInput id="edit-connection-secret" stored={stored || Boolean(c.secret)} value={secret} disabled={ro} onChange={setSecret} />
          </Field>
        </div>
      ) : (
        <Notice kind="warn">This connection was set up with a newer version of Hive BDC and is kept as it is.</Notice>
      )}
      <AnswerLine answer={answer} />
      {removal.strip}
    </BdcDialog>
  );
}

/** The key a sender posts with to a web address: a new one is shown once; the old one stops working with Save. */
function KeyDialog({ c, ro, onClose }: { c: any; ro: boolean; onClose: () => void }) {
  const { config } = useConsole();
  const state = useDraft();
  const shown = state.keys.get(c.id);
  const stored = (config?.connections || []).some((x: any) => x.id === c.id && x.hasSecret);
  const removal = useRemoval(c.id, onClose);
  return (
    <BdcDialog
      title={`Key ${c.id}`}
      subtitle={X.connectionWords(c)}
      onClose={onClose}
      leading={ro ? undefined : removal.button}
      footer={
        <button type="button" className="button secondary" onClick={onClose}>
          Close
        </button>
      }
    >
      <Facts
        items={[
          ["Kind", "Web address key"],
          ["Key", shown ? "New, goes to Key Vault when you save" : stored || c.secret ? "Stored in Key Vault" : "No key yet"],
          ["Used by", usedBy(X.connectionUses(state.body, c.id))],
        ]}
      />
      {shown && (
        <div className="keybox" role="status">
          <strong>New key, shown only now</strong>
          <CopyCode text={shown} label="Copy the key" />
          <span>Put it into the sender now. The old key stops working when you save.</span>
        </div>
      )}
      {!ro && (
        <div className="check-row-actions">
          <button
            type="button"
            className="button secondary"
            onClick={() => {
              const key = X.newKey();
              draft.keys.set(c.id, key);
              change((d) => X.updateConnection(d, c.id, {}, key));
            }}
          >
            Make a new key
          </button>
        </div>
      )}
      {removal.strip}
    </BdcDialog>
  );
}

function SharePointApp({ identity, ro, onClose }: { identity: any; ro: boolean; onClose: () => void }) {
  const { config } = useConsole();
  const state = useDraft();
  const app = state.body.sharePointAuth || {};
  const stored = Boolean(config?.sharePointAuth && config.sharePointAuth.secretSet);
  const [tenant, setTenant] = useState(app.tenantId || identity?.tenantId || "");
  const [client, setClient] = useState(app.clientId || "");
  const [secret, setSecret] = useState("");
  const [answer, setAnswer] = useState<Answer>(null);
  const firstLibrary = state.body.documentTypes
    .flatMap((t: any) => t.companies)
    .map((c: any) => c.sharePoint)
    .find((sp: any) => sp && sp.enabled && sp.siteUrl && sp.library);
  const appChanged = JSON.stringify(app) !== JSON.stringify(state.original.sharePointAuth || {});
  const blocked = X.sharePointTestBlock(firstLibrary, appChanged);
  const test = async () => {
    setAnswer("busy");
    const result = await post("/config/test-sharepoint", { sharePoint: { siteUrl: firstLibrary.siteUrl, library: firstLibrary.library, folder: "" } });
    recordTest("#sharepoint", result);
    setAnswer(null);
  };
  return (
    <BdcDialog
      title="SharePoint app"
      subtitle={X.sharePointAppLine(app, stored)}
      onClose={onClose}
      footer={
        <Buttons
          ro={ro}
          onClose={onClose}
          test={
            app.clientId ? (
              <button type="button" className="button secondary" disabled={Boolean(blocked)} title={blocked || undefined} onClick={() => void test()}>
                Test connection
              </button>
            ) : null
          }
          primary={
            <button
              type="button"
              className="button primary"
              onClick={() => {
                change((d) => X.setSharePointApp(d, { tenantId: tenant, clientId: client, secret }));
                forgetTest("#sharepoint");
                onClose();
              }}
            >
              Apply
            </button>
          }
        />
      }
    >
      <Facts
        items={[
          ["Used by", usedBy(X.sharePointUses(state.body))],
          ["Last test", app.clientId ? lastTest(state.tests["#sharepoint"]) : <span className="cell-muted">Not set up yet</span>],
        ]}
      />
      <div className="form-grid-plain">
        <Field label="Tenant ID" htmlFor="sharepoint-tenant">
          <input
            id="sharepoint-tenant"
            type="text"
            autoComplete="off"
            spellCheck={false}
            value={tenant}
            disabled={ro}
            onChange={(e) => setTenant(e.target.value)}
          />
        </Field>
        <Field label="Client ID" htmlFor="sharepoint-client">
          <input
            id="sharepoint-client"
            type="text"
            autoComplete="off"
            spellCheck={false}
            placeholder="00000000-0000-0000-0000-000000000000"
            value={client}
            disabled={ro}
            onChange={(e) => setClient(e.target.value)}
          />
        </Field>
        <Field label="Client secret" htmlFor="sharepoint-secret" hint={ro ? undefined : KEPT}>
          <SecretInput id="sharepoint-secret" stored={stored} value={secret} disabled={ro} onChange={setSecret} />
        </Field>
      </div>
      {app.clientId && blocked && !ro && <p className="muted small">Test: {blocked}</p>}
      <AnswerLine answer={answer} />
    </BdcDialog>
  );
}

function Mailbox({ identity, ro, onClose }: { identity: any; ro: boolean; onClose: () => void }) {
  const { config } = useConsole();
  const state = useDraft();
  const mail = state.body.mail || {};
  const stored = Boolean(config?.mail && config.mail.secretSet);
  const [on, setOn] = useState(Boolean(mail.enabled || !mail.from));
  const [tenant, setTenant] = useState(mail.tenantId || "");
  const [client, setClient] = useState(mail.clientId || "");
  const [secret, setSecret] = useState("");
  const [from, setFrom] = useState(mail.from || "");
  const [redirect, setRedirect] = useState((mail.redirectTo || []).join(", "));
  const [sent, setSent] = useState(mail.saveToSentItems !== false);
  const [problems, setProblems] = useState<string[]>([]);
  const [answer, setAnswer] = useState<Answer>(null);
  const mailSet = Boolean(mail.enabled && mail.from);
  const apply = () => {
    const next = { enabled: on, tenantId: tenant, clientId: client, secret, from, redirectTo: redirect.split(/[,;\s]+/), saveToSentItems: sent };
    const found = X.mailboxProblems(
      { ...next, redirectTo: next.redirectTo.filter(Boolean) },
      state.body.sharePointAuth && state.body.sharePointAuth.tenantId,
    );
    if (found.length) {
      setProblems(found);
      return;
    }
    change((d) => X.setMailbox(d, next));
    forgetTest("#mail");
    onClose();
  };
  const test = async () => {
    setAnswer("busy");
    const result = await post("/config/test-mail", { mail: { tenantId: mail.tenantId, clientId: mail.clientId, from: mail.from, redirectTo: mail.redirectTo } });
    recordTest("#mail", result);
    setAnswer(null);
  };
  return (
    <BdcDialog
      title="Document mail"
      subtitle={X.mailboxLine(mail, stored)}
      onClose={onClose}
      footer={
        <Buttons
          ro={ro}
          onClose={onClose}
          test={
            mailSet ? (
              <button type="button" className="button secondary" onClick={() => void test()}>
                Test mail
              </button>
            ) : null
          }
          primary={
            <button type="button" className="button primary" onClick={apply}>
              Apply
            </button>
          }
        />
      }
    >
      <Facts
        items={[
          ["Used by", usedBy(X.mailCompanies(state.body))],
          ["Last test", mailSet ? lastTest(state.tests["#mail"]) : <span className="cell-muted">Not set up yet</span>],
        ]}
      />
      <div className="dialog-toggles">
        <TogglePill label="Send documents by mail" checked={on} disabled={ro} onChange={setOn} />
      </div>
      <div className="form-grid-plain">
        <Field label="Tenant ID (empty: the SharePoint tenant)" htmlFor="mail-tenant">
          <input
            id="mail-tenant"
            type="text"
            autoComplete="off"
            spellCheck={false}
            placeholder={(state.body.sharePointAuth && state.body.sharePointAuth.tenantId) || identity?.tenantId || ""}
            value={tenant}
            disabled={ro}
            onChange={(e) => setTenant(e.target.value)}
          />
        </Field>
        <Field label="Client ID of the mail app" htmlFor="mail-client">
          <input id="mail-client" type="text" autoComplete="off" spellCheck={false} value={client} disabled={ro} onChange={(e) => setClient(e.target.value)} />
        </Field>
        <Field label="Client secret" htmlFor="mail-secret" hint={ro ? undefined : KEPT}>
          <SecretInput id="mail-secret" stored={stored} value={secret} disabled={ro} onChange={setSecret} />
        </Field>
        <Field label="Sent from" htmlFor="mail-from">
          <input
            id="mail-from"
            type="text"
            autoComplete="off"
            spellCheck={false}
            placeholder="rechnung@yourcompany.example"
            value={from}
            disabled={ro}
            onChange={(e) => setFrom(e.target.value)}
          />
        </Field>
        <Field label="Documents of a test ERP go to" htmlFor="mail-redirect" hint="A non-production ERP never mails a customer. Comma between addresses." wide>
          <input
            id="mail-redirect"
            type="text"
            autoComplete="off"
            spellCheck={false}
            placeholder="test@yourcompany.example"
            value={redirect}
            disabled={ro}
            onChange={(e) => setRedirect(e.target.value)}
          />
        </Field>
      </div>
      <label className="check-row dialog-check">
        <input type="checkbox" checked={sent} disabled={ro} onChange={(e) => setSent(e.target.checked)} />
        Keep a copy in Sent Items
      </label>
      {problems.length > 0 && (
        <Notice kind="error">
          <ul className="problems">
            {problems.map((p) => (
              <li key={p}>{p}</li>
            ))}
          </ul>
        </Notice>
      )}
      <AnswerLine answer={answer} />
    </BdcDialog>
  );
}
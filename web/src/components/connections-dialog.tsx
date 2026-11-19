"use client";
import { useEffect, useRef, useState, type ReactNode } from "react";
import { X as Close } from "lucide-react";
import { onDialogCancel } from "./ui/confirm";
import { Chip, CopyCode, Field, Switch, post } from "./outbound-parts";
import { change, draft, forgetTest, useDraft } from "./save-bar";
import { useConsole } from "./console-context";
import { Notice } from "./notice";
import * as X from "@/lib/exportlogic";

// The record dialogs of the Connections page (board W12, the forms of connections.js): a new connection, a
// connection's settings and secret, a web address key, the SharePoint app and the document mailbox. Done puts the
// values into the draft; secrets leave the browser only with Save, into the instance's Key Vault.

export type Editing =
  { what: "new"; kind: any } | { what: "connection"; id: string } | { what: "key"; id: string } | { what: "sharepoint" } | { what: "mail" };

function RecordDialog({
  eyebrow,
  title,
  onClose,
  children,
  footer,
}: {
  eyebrow: string;
  title: string;
  onClose: () => void;
  children: ReactNode;
  footer: ReactNode;
}) {
  const ref = useRef<HTMLDialogElement>(null);
  useEffect(() => {
    const dialog = ref.current;
    if (dialog && !dialog.open) dialog.showModal();
    return () => dialog?.close();
  }, []);
  return (
    <dialog ref={ref} className="record-dialog" aria-labelledby="connection-dialog-title" onCancel={onDialogCancel(onClose)}>
      <div className="dialog-heading">
        <div>
          <div className="eyebrow">{eyebrow}</div>
          <h2 id="connection-dialog-title">{title}</h2>
        </div>
        <button type="button" className="icon-button" aria-label="Close dialog" onClick={onClose}>
          <Close size={18} strokeWidth={1.75} />
        </button>
      </div>
      <div className="form-grid">{children}</div>
      <div className="dialog-footer">{footer}</div>
    </dialog>
  );
}

const KEPT = "Kept in this instance's Key Vault, never shown again.";

function SecretInput({ id, stored, value, onChange }: { id: string; stored: boolean; value: string; onChange: (v: string) => void }) {
  return (
    <input
      id={id}
      type="password"
      autoComplete="off"
      placeholder={stored ? "(stored, leave empty to keep)" : ""}
      value={value}
      onChange={(e) => onChange(e.target.value)}
    />
  );
}

export function ConnectionDialog({ editing, kinds, identity, onClose }: { editing: Editing; kinds: any[]; identity: any; onClose: () => void }) {
  switch (editing.what) {
    case "new":
      return <NewConnection kind={editing.kind} onClose={onClose} />;
    case "connection":
      return <EditConnection id={editing.id} kinds={kinds} onClose={onClose} />;
    case "key":
      return <ReplaceKey id={editing.id} onClose={onClose} />;
    case "sharepoint":
      return <SharePointApp identity={identity} onClose={onClose} />;
    case "mail":
      return <Mailbox identity={identity} onClose={onClose} />;
  }
}

function NewConnection({ kind, onClose }: { kind: any; onClose: () => void }) {
  const state = useDraft();
  const [name, setName] = useState(() => X.suggestId(state.body.connections, kind.kind));
  const [settings, setSettings] = useState<Record<string, string>>({});
  const [secret, setSecret] = useState("");
  const [result, setResult] = useState<{ ok: boolean; message: string } | null>(null);
  const values = () => ({
    id: name.trim(),
    kind: kind.kind,
    settings: Object.fromEntries(kind.settings.map((s: any) => [s.key, (settings[s.key] || "").trim()])),
    secret,
  });
  const signsInAsInstance = kind.kind === "azure-storage" || kind.kind === "servicebus";
  return (
    <RecordDialog
      eyebrow="NEW CONNECTION"
      title={`New ${kind.name} connection`}
      onClose={onClose}
      footer={
        <>
          <button
            type="button"
            className="button secondary"
            onClick={async () => {
              const answer = await post("/connections/test", values());
              setResult({ ok: Boolean(answer.ok), message: answer.message });
            }}
          >
            Test the connection
          </button>
          <div className="spacer" />
          <button type="button" className="button secondary" onClick={onClose}>
            Cancel
          </button>
          <button
            type="button"
            className="button primary"
            onClick={() => {
              try {
                change((d) => X.addConnection(d, values()));
                onClose();
              } catch (e) {
                setResult({ ok: false, message: (e as Error).message });
              }
            }}
          >
            Add
          </button>
        </>
      }
    >
      {kind.description && <p className="muted small dialog-note">{kind.description}</p>}
      <fieldset>
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
        <Field
          label={kind.secretLabel || "Secret"}
          htmlFor="new-connection-secret"
          hint={signsInAsInstance ? "Leave it empty to sign in as this instance; your IT gives it a role." : KEPT}
        >
          <SecretInput id="new-connection-secret" stored={false} value={secret} onChange={setSecret} />
        </Field>
      </fieldset>
      <div aria-live="polite" className="dialog-result">
        {result && <Chip ok={result.ok}>{result.message}</Chip>}
      </div>
    </RecordDialog>
  );
}

function EditConnection({ id, kinds, onClose }: { id: string; kinds: any[]; onClose: () => void }) {
  const { config } = useConsole();
  const state = useDraft();
  const c = state.body.connections.find((x: any) => x.id === id);
  const kind = kinds.find((k) => k.kind === c?.kind);
  const [settings, setSettings] = useState<Record<string, string>>(() =>
    Object.fromEntries((kind?.settings || []).map((s: any) => [s.key, (c?.settings || {})[s.key] || ""])),
  );
  const [secret, setSecret] = useState("");
  const [result, setResult] = useState<{ ok: boolean; message: string } | null>(null);
  if (!c || !kind) return null;
  const stored = (config?.connections || []).some((x: any) => x.id === c.id && x.hasSecret);
  const typed = () => Object.fromEntries(Object.entries(settings).map(([k, v]) => [k, v.trim()]));
  return (
    <RecordDialog
      eyebrow={kind.name.toUpperCase()}
      title={c.id}
      onClose={onClose}
      footer={
        <>
          <button
            type="button"
            className="button secondary"
            onClick={async () => {
              const answer = await post("/connections/test", {
                id: c.id,
                kind: c.kind,
                settings: { ...c.settings, ...typed() },
                secret: secret || c.secret || "",
              });
              setResult({ ok: Boolean(answer.ok), message: answer.message });
            }}
          >
            Test the connection
          </button>
          <div className="spacer" />
          <button type="button" className="button secondary" onClick={onClose}>
            Cancel
          </button>
          <button
            type="button"
            className="button primary"
            onClick={() => {
              change((d) => X.updateConnection(d, c.id, typed(), secret));
              forgetTest(c.id);
              onClose();
            }}
          >
            Done
          </button>
        </>
      }
    >
      <p className="muted small dialog-note">{X.connectionWords(c)}</p>
      <fieldset>
        {kind.settings.map((s: any) => (
          <Field key={s.key} label={s.label} htmlFor={`edit-connection-${s.key}`} hint={s.hint}>
            <input
              id={`edit-connection-${s.key}`}
              type="text"
              autoComplete="off"
              spellCheck={false}
              value={settings[s.key] || ""}
              onChange={(e) => setSettings((v) => ({ ...v, [s.key]: e.target.value }))}
            />
          </Field>
        ))}
        <Field label={kind.secretLabel || "Secret"} htmlFor="edit-connection-secret" hint={KEPT}>
          <SecretInput id="edit-connection-secret" stored={stored || Boolean(c.secret)} value={secret} onChange={setSecret} />
        </Field>
      </fieldset>
      <div aria-live="polite" className="dialog-result">
        {result && <Chip ok={result.ok}>{result.message}</Chip>}
      </div>
    </RecordDialog>
  );
}

function ReplaceKey({ id, onClose }: { id: string; onClose: () => void }) {
  const state = useDraft();
  const shown = state.keys.get(id);
  return (
    <RecordDialog
      eyebrow="WEB ADDRESS"
      title={`Key ${id}`}
      onClose={onClose}
      footer={
        <>
          <button
            type="button"
            className="button secondary"
            onClick={() => {
              const key = X.newKey();
              draft.keys.set(id, key);
              change((d) => X.updateConnection(d, id, {}, key));
            }}
          >
            Make a new key
          </button>
          <div className="spacer" />
          <button type="button" className="button secondary" onClick={onClose}>
            Close
          </button>
        </>
      }
    >
      {shown ? (
        <div className="keybox" role="status">
          <strong>New key, shown only now</strong>
          <CopyCode text={shown} />
          <span>Put it into the sender now. The old key stops working when you save.</span>
        </div>
      ) : (
        <p className="muted small dialog-note">{X.connectionWords(state.body.connections.find((c: any) => c.id === id))}</p>
      )}
    </RecordDialog>
  );
}

function SharePointApp({ identity, onClose }: { identity: any; onClose: () => void }) {
  const { config } = useConsole();
  const state = useDraft();
  const app = state.body.sharePointAuth || {};
  const stored = Boolean(config?.sharePointAuth && config.sharePointAuth.secretSet);
  const [tenant, setTenant] = useState(app.tenantId || identity?.tenantId || "");
  const [client, setClient] = useState(app.clientId || "");
  const [secret, setSecret] = useState("");
  return (
    <RecordDialog
      eyebrow="CONNECTION"
      title="SharePoint app"
      onClose={onClose}
      footer={
        <>
          <div className="spacer" />
          <button type="button" className="button secondary" onClick={onClose}>
            Cancel
          </button>
          <button
            type="button"
            className="button primary"
            onClick={() => {
              change((d) => X.setSharePointApp(d, { tenantId: tenant, clientId: client, secret }));
              forgetTest("#sharepoint");
              onClose();
            }}
          >
            Done
          </button>
        </>
      }
    >
      <p className="muted small dialog-note">{X.sharePointAppLine(app, stored)}</p>
      <fieldset>
        <Field label="Tenant ID" htmlFor="sharepoint-tenant">
          <input
            id="sharepoint-tenant"
            type="text"
            autoComplete="off"
            spellCheck={false}
            value={tenant}
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
            onChange={(e) => setClient(e.target.value)}
          />
        </Field>
        <Field label="Client secret" htmlFor="sharepoint-secret">
          <SecretInput id="sharepoint-secret" stored={stored} value={secret} onChange={setSecret} />
        </Field>
      </fieldset>
    </RecordDialog>
  );
}

function Mailbox({ identity, onClose }: { identity: any; onClose: () => void }) {
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
  const done = () => {
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
  return (
    <RecordDialog
      eyebrow="CONNECTION"
      title="Document mail"
      onClose={onClose}
      footer={
        <>
          <div className="spacer" />
          <button type="button" className="button secondary" onClick={onClose}>
            Cancel
          </button>
          <button type="button" className="button primary" onClick={done}>
            Done
          </button>
        </>
      }
    >
      <p className="muted small dialog-note">{X.mailboxLine(mail, stored)}</p>
      <label className="switch-field dialog-switch">
        <Switch checked={on} onChange={setOn} />
        <span>Send documents by mail</span>
      </label>
      <fieldset>
        <Field label="Tenant ID (empty: the SharePoint tenant)" htmlFor="mail-tenant">
          <input
            id="mail-tenant"
            type="text"
            autoComplete="off"
            spellCheck={false}
            placeholder={(state.body.sharePointAuth && state.body.sharePointAuth.tenantId) || identity?.tenantId || ""}
            value={tenant}
            onChange={(e) => setTenant(e.target.value)}
          />
        </Field>
        <Field label="Client ID of the mail app" htmlFor="mail-client">
          <input id="mail-client" type="text" autoComplete="off" spellCheck={false} value={client} onChange={(e) => setClient(e.target.value)} />
        </Field>
        <Field label="Client secret" htmlFor="mail-secret">
          <SecretInput id="mail-secret" stored={stored} value={secret} onChange={setSecret} />
        </Field>
        <Field label="Sent from" htmlFor="mail-from">
          <input
            id="mail-from"
            type="text"
            autoComplete="off"
            spellCheck={false}
            placeholder="rechnung@yourcompany.example"
            value={from}
            onChange={(e) => setFrom(e.target.value)}
          />
        </Field>
        <Field
          label="Documents of a test ERP go to"
          htmlFor="mail-redirect"
          hint="Comma between addresses. A non-production ERP never mails a customer."
          wide
        >
          <input
            id="mail-redirect"
            type="text"
            autoComplete="off"
            spellCheck={false}
            placeholder="test@yourcompany.example"
            value={redirect}
            onChange={(e) => setRedirect(e.target.value)}
          />
        </Field>
      </fieldset>
      <label className="check-row dialog-check">
        <input type="checkbox" checked={sent} onChange={(e) => setSent(e.target.checked)} />
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
    </RecordDialog>
  );
}
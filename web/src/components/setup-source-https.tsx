"use client";
/* eslint-disable @typescript-eslint/no-explicit-any */
import Link from "next/link";
import { useState } from "react";
import { KeyRound, LoaderCircle } from "lucide-react";
import { companyList, postBody, sourceEntries, typeLabel, withHttpsSource } from "@/lib/logic";
import { newKey } from "@/lib/exportlogic";
import { inboundAddress, stepHref } from "@/app/setup/steps";
import { useConsole } from "./console-context";
import { Notice } from "./notice";
import { CopyButton, Hint, ICON, Panel, SelectField, StepActions, TextField, holdStep, useConfigSave } from "./setup-parts";
import type { SourceProps } from "./setup-source";

const same = (a: unknown, b: unknown) => String(a ?? "").toLowerCase() === String(b ?? "").toLowerCase();

const keyLine = (stored: boolean, readOnly: boolean) => {
  if (stored) return readOnly ? "Key stored in Key Vault." : "Key stored in Key Vault. A save replaces it.";
  return readOnly ? "No key yet." : "Save makes its key.";
};

/**
 * Step 2 for a web address (design version 3, decision 2): the type and its companies; Save makes one key per company
 * with the Outbound page's helpers and shows each once. The keys live in this component's state only: nothing in the
 * browser's storage, gone with a reload (S3).
 */
export function HttpsSource({ progress, readOnly, identity, notes }: SourceProps) {
  const { config, refresh } = useConsole();
  const entries = sourceEntries(config).filter((e) => e.company.source.kind === "https");
  const first = entries[0];
  const types = (config.documentTypes || []).map((t: any) => ({ value: t.id, label: typeLabel(t) }));

  const [type, setType] = useState<string>(first?.type.id || types[0]?.value || "");
  const [companies, setCompanies] = useState(() =>
    entries
      .filter((e) => e.type.id === first?.type.id)
      .map((e) => e.company.dataAreaId)
      .join(", "),
  );
  const [dirty, setDirty] = useState(false);
  const [shown, setShown] = useState<{ type: string; keys: { company: string; key: string }[] } | null>(null);
  const [planError, setPlanError] = useState("");
  const { save, slot, busy } = useConfigSave();

  const list = companyList(companies);
  const origin = window.location.origin;
  const keyOf = (company: string) => (shown && same(shown.type, type) ? (shown.keys.find((k) => same(k.company, company))?.key ?? "") : "");
  const stored = (company: string) => {
    const t = (config.documentTypes || []).find((x: any) => same(x.id, type));
    const entry = t?.companies.find((c: any) => same(c.dataAreaId, company));
    const id = entry?.source?.kind === "https" ? entry.source.connection : "";
    return Boolean(id && (config.connections || []).some((c: any) => c.id === id && c.hasSecret));
  };

  const edit = (set: (value: string) => void) => (value: string) => {
    set(value);
    setDirty(true);
    setPlanError("");
  };

  const saveKeys = () => {
    const keys = list.map((company) => ({ company, key: newKey() }));
    let body: any;
    try {
      body = withHttpsSource(postBody(config), type, keys, identity.inputRoot);
    } catch (error) {
      setPlanError((error as Error).message);
      return;
    }
    holdStep(2);
    void save(body, async () => {
      setShown({ type, keys });
      setDirty(false);
      await refresh();
    });
  };

  let next = null;
  if (!dirty && progress.done[1]) {
    next = (
      <Link className="button primary" href={stepHref(3)}>
        Continue
      </Link>
    );
  } else if (!readOnly) {
    next = (
      <button type="button" className="button primary" disabled>
        Continue
      </button>
    );
  }

  const fresh = list.some((company) => keyOf(company));

  return (
    <>
      <Panel id="https-heading" title="Web address">
        <div className="setup-fields">
          <SelectField id="https-type" label="Document type" value={type} options={types} disabled={readOnly} onChange={edit(setType)} />
          <TextField
            id="https-companies"
            label="Companies"
            value={companies}
            placeholder="1000, DEMF"
            hint="F&O company IDs, separated by commas."
            disabled={readOnly}
            onChange={edit(setCompanies)}
          />
        </div>
        {type && list.length > 0 && (
          <ul className="setup-keys" aria-label="Addresses and keys">
            {list.map((company) => {
              const address = inboundAddress(origin, type, company);
              const key = keyOf(company);
              return (
                <li key={company} className="setup-key">
                  <span className="setup-sublabel">Company {company}</span>
                  <div className="setup-code">
                    <code>{address}</code>
                    {!readOnly && <CopyButton text={address} label={`Copy the address of company ${company}`} />}
                  </div>
                  {key ? (
                    <div className="setup-code">
                      <code>{`x-api-key: ${key}`}</code>
                      <CopyButton text={key} label={`Copy the key of company ${company}`} />
                    </div>
                  ) : (
                    <Hint>{keyLine(stored(company), readOnly)}</Hint>
                  )}
                </li>
              );
            })}
          </ul>
        )}
        <Hint>The sender posts JSON with the export as &quot;document&quot; and the PDF as Base64 in &quot;pdf&quot;.</Hint>
        {fresh && <Notice kind="warn">Copy each key into its sender now. It is not shown again.</Notice>}
        {!readOnly && (
          <div className="setup-inline">
            <button type="button" className="button secondary" disabled={!type || list.length === 0 || busy || !identity} onClick={saveKeys}>
              {busy ? <LoaderCircle className="spin" {...ICON} /> : <KeyRound {...ICON} />}
              Save and create keys
            </button>
          </div>
        )}
      </Panel>
      {planError && <Notice kind="error">{planError}</Notice>}
      {notes}
      {slot}
      <StepActions back={1} next={next} />
    </>
  );
}
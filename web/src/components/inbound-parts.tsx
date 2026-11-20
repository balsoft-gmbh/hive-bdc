"use client";
import { useEffect, useState } from "react";
import { Archive, FolderOpen, Library } from "lucide-react";
import { ICON, post } from "./outbound-parts";
import * as I from "@/lib/inboundlogic";

// Parts the Inbound pages share: the document system tiles (built from GET /sources, so a new connector shows up
// without a console change) and the lists a source fills its dropdowns from (POST /sources/choices).

export type Choice = { value: string; label: string; detail?: string };

const icons: Record<string, typeof Archive> = { docuware: Archive, sharepoint: Library };

/** Where the documents come from: one tile per document system this version reads (the Windows "Where do the documents come from?"). */
export function SourceTiles({ sources, value, onChange, disabled }: { sources: any[]; value: string; onChange: (kind: string) => void; disabled?: boolean }) {
  return (
    <fieldset className="source-tiles" disabled={disabled}>
      <legend className="field-label">Where do the documents come from?</legend>
      {sources.map((s) => {
        const Icon = icons[s.kind] || FolderOpen;
        return (
          <label key={s.kind} className={`source-tile${s.kind === value ? " selected" : ""}`}>
            <input type="radio" name="inbound-source" value={s.kind} checked={s.kind === value} onChange={() => onChange(s.kind)} />
            <span className="setup-tile-icon">
              <Icon {...ICON} aria-hidden="true" />
            </span>
            <span className="source-tile-text">
              <strong>{s.name}</strong>
              <span>
                {s.description}
                {s.connected ? "" : " Not connected yet: set it up under Connections first."}
              </span>
            </span>
          </label>
        );
      })}
      <span className="field-hint">Further document systems appear here when a Hive BDC update brings their connector.</span>
    </fieldset>
  );
}

/**
 * The values of one dropdown of a flow's source, read again when what it depends on changes. Without the setting it
 * depends on, or when the list cannot be read, it answers with a note, and the page lets the user type the value.
 */
export function useChoices(flow: any, lookup: string | null | undefined, ready: boolean) {
  const [answer, setAnswer] = useState<{ key: string; choices: Choice[]; note: string } | null>(null);
  const settings = flow.Source.Settings || {};
  const key = JSON.stringify([flow.Source.Kind, flow.Source.Connection, lookup, settings]);
  useEffect(() => {
    if (!lookup || !ready) return;
    let current = true;
    void post("/sources/choices", I.sourceRequest(flow, lookup)).then((a) => {
      if (current) setAnswer({ key, choices: a.ok ? a.choices || [] : [], note: a.ok ? "" : a.message || "The list could not be read; type the value." });
    });
    return () => {
      current = false;
    };
    // The key holds everything the request is made of.
  }, [key, ready]);
  const fresh = answer && answer.key === key ? answer : null;
  return { choices: fresh?.choices || [], note: fresh?.note || "", loading: Boolean(lookup && ready && !fresh) };
}

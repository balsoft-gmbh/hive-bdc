"use client";
import { useRouter } from "next/navigation";
import { useEffect, useRef, useState, useSyncExternalStore } from "react";
import { saveConfig } from "@/lib/api";
import { postBody } from "@/lib/logic";
import { draftChanges, keptTests, saveProblems } from "@/lib/exportlogic";
import { ConflictPanel } from "./conflict-panel";
import { useConsole } from "./console-context";
import { Notice } from "./notice";
import { confirmAction } from "./ui/confirm";

// The one draft of the settings that the Outbound and Connections pages edit (port of draft.js), and the floating Save
// bar every editing page shows: it lists the draft's changes and saves them with the version the settings were read
// at, so a save by someone else in between shows the conflict panel instead of being overwritten. The draft lives in
// this module, so it spans every Outbound and Connections page; leaving those pages or the browser tab with unsaved
// changes asks first (failure case 8 of the web console design).

export type TestResult = { ok: boolean; message: string; at: string };

export const draft = {
  /** The POST /config body being edited. */
  body: null as any,
  /** The body as it was read, to list the changes against. */
  original: null as any,
  version: null as string | null,
  savedAt: "",
  /** New web address keys, shown only until Save or Discard. */
  keys: new Map<string, string>(),
  /** Test results of connections ("#sharepoint", "#mail" for the apps); they stay through Save and Discard. */
  tests: {} as Record<string, TestResult>,
  /** The destination kinds of GET /targets, for the Save bar's checks. */
  targets: [] as any[],
  /** Grows with every Save and Discard, so the pages start their own state afresh (the former state.ui = {}). */
  generation: 0,
};

let tick = 0;
const listeners = new Set<() => void>();
const emit = () => {
  tick++;
  for (const listener of listeners) listener();
};
const subscribe = (listener: () => void) => {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
};

/** Takes GET /config as the draft unless the draft holds changes; a newer version replaces an unchanged draft. */
export function sync(config: any) {
  if (!config) return;
  const unchanged = !draft.body || draftChanges(draft.original, draft.body).length === 0;
  if (unchanged && (draft.version !== config.version || !draft.body)) {
    const fresh = postBody(config);
    draft.body = fresh;
    draft.original = structuredClone(fresh);
    draft.version = config.version;
  }
}

/** Applies one change to the draft; a change that throws leaves the draft as it was. */
export function change(fn: (body: any) => any) {
  draft.body = fn(draft.body);
  emit();
}

export function setTargets(targets: any[]) {
  draft.targets = targets || [];
  emit();
}

export function recordTest(id: string, answer: { ok?: boolean; message?: string }) {
  draft.tests = { ...draft.tests, [id]: { ok: Boolean(answer.ok), message: answer.message || "", at: clock() } };
  emit();
}

export function forgetTest(id: string) {
  const next = { ...draft.tests };
  delete next[id];
  draft.tests = next;
  emit();
}

export function isDirty() {
  return Boolean(draft.body) && draftChanges(draft.original, draft.body).length > 0;
}

/** Drops the draft (Discard, or leaving with changes); the next render reads the settings again. */
export function drop() {
  draft.body = null;
  draft.keys.clear();
  draft.generation++;
  emit();
}

const clock = () => new Date().toLocaleTimeString("de-DE", { hour: "2-digit", minute: "2-digit" });

/** The draft for a page: kept in step with GET /config, and the page renders again on every change. */
export function useDraft() {
  const { config } = useConsole();
  useSyncExternalStore(
    subscribe,
    () => tick,
    () => tick,
  );
  sync(config);
  return draft;
}

/** Pages that edit the draft: moving between them keeps it, so only links elsewhere ask first. */
export function keepsDraft(pathname: string) {
  return /^\/(outbound|connections)(\/|$)/.test(pathname);
}

const LEAVE = "Your changes are not saved. Leave this page and lose them?";

/**
 * Asks before unsaved changes are lost: the browser's own prompt on reload and close, the console's confirmation on a
 * link to a page outside Outbound and Connections. Port of the CRM's use-unsaved-changes.ts.
 */
function useUnsavedGuard(active: boolean) {
  const router = useRouter();
  useEffect(() => {
    if (!active) return;
    let asking = false;
    const unload = (event: BeforeUnloadEvent) => {
      if (!isDirty()) return;
      event.preventDefault();
      event.returnValue = "";
    };
    const click = (event: MouseEvent) => {
      if (!isDirty() || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
      const link = (event.target as Element | null)?.closest?.("a[href]") as HTMLAnchorElement | null;
      if (!link || link.target === "_blank" || link.hasAttribute("download")) return;
      const url = new URL(link.href, window.location.href);
      if (url.origin === window.location.origin && keepsDraft(url.pathname)) return;
      event.preventDefault();
      event.stopImmediatePropagation();
      if (asking) return;
      asking = true;
      void confirmAction(LEAVE).then((leave) => {
        asking = false;
        if (!leave) return;
        drop();
        if (url.origin === window.location.origin && !url.pathname.startsWith("/.auth/")) router.push(url.pathname + url.search + url.hash);
        else window.location.assign(url.href);
      });
    };
    window.addEventListener("beforeunload", unload);
    document.addEventListener("click", click, true);
    return () => {
      window.removeEventListener("beforeunload", unload);
      document.removeEventListener("click", click, true);
    };
  }, [active, router]);
}

type Result = { conflict: unknown; body: any } | { error: string } | null;

/** The Save bar: the changes of the draft, Discard, and Save once nothing blocks it. Hidden for a Viewer. */
export function SaveBar() {
  const { me, config, refresh } = useConsole();
  const state = useDraft();
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<Result>(null);
  const resultRef = useRef<HTMLDivElement>(null);
  const canChange = Boolean(me?.canChange);
  useUnsavedGuard(canChange);
  useEffect(() => {
    if (result) resultRef.current?.scrollIntoView({ block: "nearest" });
  }, [result]);
  if (!canChange) return null;

  const lines = draftChanges(state.original, state.body);
  const problems = lines.length ? saveProblems(state.body, config, state.targets) : [];

  const saved = async () => {
    const before = draft.original;
    const after = draft.body;
    // The settings are read again before the draft goes, so the page never shows the old values in between.
    await refresh();
    draft.savedAt = clock();
    draft.tests = keptTests(draft.tests, before, after);
    setResult(null);
    drop();
  };

  const send = async (body: any) => {
    setBusy(true);
    setResult(null);
    const answer = await saveConfig(body);
    if (answer.ok) await saved();
    else setResult(answer.conflict ? { conflict: answer.conflict, body } : { error: answer.message });
    setBusy(false);
  };

  const discard = () => {
    setResult(null);
    drop();
    void refresh();
  };

  const reload = () => {
    // The settings are read again from the start; the draft goes first, so the browser does not ask.
    draft.body = null;
    window.location.reload();
  };

  const panel =
    result && "conflict" in result ? (
      <ConflictPanel refusal={result.conflict} onSaveBoth={() => void send({ ...result.body, saveBoth: true })} onReload={reload} />
    ) : result && "error" in result ? (
      <Notice kind="error">{result.error}</Notice>
    ) : null;

  if (lines.length === 0) {
    return (
      <>
        {panel && <div ref={resultRef}>{panel}</div>}
        {state.savedAt && (
          <div className="save-bar save-bar-quiet" aria-live="polite">
            <span className="bdc-chip ok">
              <span className="dot" aria-hidden="true" />
              Saved {state.savedAt}
            </span>
          </div>
        )}
      </>
    );
  }

  return (
    <>
      {panel && <div ref={resultRef}>{panel}</div>}
      <div className="save-bar" role="region" aria-label="Changes not saved" aria-live="polite">
        <div className="save-bar-text">
          <strong>
            {lines.length} {lines.length === 1 ? "change" : "changes"} not saved
          </strong>
          <ul>
            {lines.slice(0, 5).map((line, i) => (
              <li key={`${i}-${line}`}>{line}</li>
            ))}
            {lines.length > 5 && <li>and {lines.length - 5} more</li>}
          </ul>
          {problems.length > 0 && (
            <ul className="warn-text save-bar-problems">
              {problems.map((problem, i) => (
                <li key={`${i}-${problem}`}>{problem}</li>
              ))}
            </ul>
          )}
        </div>
        <span className="row-actions">
          <button type="button" className="button secondary" disabled={busy} onClick={discard}>
            Discard
          </button>
          <button type="button" className="button primary" disabled={busy || problems.length > 0} onClick={() => void send(draft.body)}>
            Save changes
          </button>
        </span>
      </div>
    </>
  );
}
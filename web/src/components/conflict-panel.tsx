"use client";
import { conflictView } from "@/lib/logic";

/**
 * Settings saved by someone else after this page read them (board W6 of the connector design): who and when, both
 * change lists, and "Save both" only when no setting was changed by both.
 */
export function ConflictPanel({ refusal, onSaveBoth, onReload }: { refusal: unknown; onSaveBoth: () => void; onReload: () => void }) {
  const view = conflictView(refusal, "Europe/Berlin");
  const list = (title: string, lines: string[]) => (
    <div className="conflict-side">
      <h3>{title}</h3>
      {lines.length ? (
        <ul>
          {lines.map((line) => (
            <li key={line}>{line}</li>
          ))}
        </ul>
      ) : (
        <p className="muted">Nothing.</p>
      )}
    </div>
  );
  return (
    <section className="panel conflict" role="alert">
      <h2>{view.title}</h2>
      <p>{view.note}</p>
      <div className="conflict-lists">
        {list("Their changes", view.theirs)}
        {list("Your changes", view.yours)}
      </div>
      <div className="row-actions">
        {view.canSaveBoth && (
          <button type="button" className="button" onClick={onSaveBoth}>
            Save both
          </button>
        )}
        <button type="button" className="button secondary" onClick={onReload}>
          Load the settings again
        </button>
      </div>
    </section>
  );
}

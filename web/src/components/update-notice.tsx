"use client";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useState } from "react";
import { ArrowRight, Download } from "lucide-react";
import { useConsole } from "./console-context";
import { Notice } from "./notice";
import { germanDate } from "@/lib/logic";
import { updateBadge, whenText, type UpdateView } from "@/lib/updatelogic";
import { ICON, postAction } from "./service-ui";

/**
 * The line of an offered update on the Overview (board P1); nothing when no update is offered or one runs.
 * Update now and Tonight at 22:00 lead to the Updates tab of the Service page once the instance took them.
 */
export function UpdateNotice({ view }: { view: UpdateView | null | undefined }) {
  const { me } = useConsole();
  const router = useRouter();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  if (!view || !updateBadge(view)) return null;
  const offer = view.available!;

  const go = async (path: string, body?: unknown) => {
    setBusy(true);
    setError("");
    try {
      await postAction(path, body);
      router.push("/service/?tab=updates");
    } catch (e) {
      setError((e as Error).message);
      setBusy(false);
    }
  };

  return (
    <>
      <section className="update-line" aria-labelledby="update-notice-heading">
        <span className="update-line-icon" aria-hidden="true">
          <Download {...ICON} />
        </span>
        <span className="update-line-text">
          <strong id="update-notice-heading">Hive BDC {offer.version} is ready</strong>
          {offer.releaseDate && <span className="muted">Released {germanDate(offer.releaseDate)}</span>}
          <Link className="inline-link" href="/service/?tab=updates">
            What&apos;s new <ArrowRight {...ICON} />
          </Link>
        </span>
        {view.canUpdate && me?.canChange && (
          <span className="update-line-actions">
            {view.schedule ? (
              <span className="muted">Planned for {whenText(view.schedule.at, view.timeZone).slice(11)}</span>
            ) : (
              <button type="button" className="button secondary" disabled={busy} onClick={() => void go("/service/updates/schedule", { at: "22:00" })}>
                Tonight at 22:00
              </button>
            )}
            <button type="button" className="button primary" disabled={busy} onClick={() => void go("/service/updates/run")}>
              Update now
            </button>
          </span>
        )}
      </section>
      {error && <Notice kind="error">{error}</Notice>}
    </>
  );
}
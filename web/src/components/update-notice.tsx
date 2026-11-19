"use client";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useState } from "react";
import { Download } from "lucide-react";
import { useConsole } from "./console-context";
import { Notice } from "./notice";
import { updateBadge, whenText, type UpdateView } from "@/lib/updatelogic";
import { ICON, ServiceBanner, postAction } from "./service-ui";

/**
 * The notice of an offered update on the Overview (board P1); nothing when no update is offered or one runs.
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

  const lead = (offer.notes || [])[0];
  const actions =
    view.canUpdate && me?.canChange ? (
      <span className="row-actions service-banner-actions">
        {view.schedule ? (
          <span className="service-planned">Planned for {whenText(view.schedule.at, view.timeZone).slice(11)}</span>
        ) : (
          <button type="button" className="button secondary" disabled={busy} onClick={() => void go("/service/updates/schedule", { at: "22:00" })}>
            Tonight at 22:00
          </button>
        )}
        <button type="button" className="button primary" disabled={busy} onClick={() => void go("/service/updates/run")}>
          Update now
        </button>
      </span>
    ) : null;

  return (
    <>
      <ServiceBanner tone="gold" icon={<Download {...ICON} />} title={`Hive BDC ${offer.version} is ready`} titleId="update-notice-heading" actions={actions}>
        <span>
          {[lead, `Your settings are kept, and ${view.version} stays ready to return to.`].filter(Boolean).join(" ")}{" "}
          <Link href="/service/?tab=updates">What&apos;s new</Link>
        </span>
      </ServiceBanner>
      {error && <Notice kind="error">{error}</Notice>}
    </>
  );
}
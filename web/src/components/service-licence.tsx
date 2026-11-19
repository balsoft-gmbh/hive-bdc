"use client";
import { StatusPill } from "@/components/ui/data-grid";
import { useConsole } from "./console-context";
import { LicenceUpload } from "./licence-upload";
import { Notice } from "./notice";
import { featureWords, germanDate, licenceNote, licenceWorks } from "@/lib/logic";
import type { Identity } from "./service-status";

// The Licence tab: the licence of GET /status, the tenant it was checked against, its warning, and the upload of a
// renewal or another file (POST /license through LicenceUpload). Viewers and Operators see the licence only.

export function ServiceLicence({ identity }: { identity: Identity | null }) {
  const { me, status, refresh } = useConsole();
  const licence = status?.license ?? {};
  const works = licenceWorks(licence);
  const state = works
    ? `Valid until ${germanDate(licence.expires)}`
    : licence.state === "Missing"
      ? "No licence yet"
      : `Licence ${String(licence.state || "unknown").toLowerCase()}`;
  const line = [
    licence.customer,
    featureWords(licence.features),
    identity?.tenantId ? `checked against tenant ${identity.tenantId.slice(0, 8)}` : "",
  ]
    .filter(Boolean)
    .join(" · ");
  const warning = licenceNote(licence) || (!works && licence.message) || "";
  const canChange = Boolean(me?.canChange);

  return (
    <section className="panel service-panel" aria-labelledby="licence-heading">
      <div className="section-heading">
        <div>
          <h2 id="licence-heading">Licence</h2>
          {line && <p>{line}</p>}
        </div>
        <StatusPill tone={works ? "green" : "amber"}>{state}</StatusPill>
      </div>
      {(warning || canChange) && (
        <div className="service-body">
          {warning && <Notice kind="warn">{warning}</Notice>}
          {canChange && (
            <div className="service-upload">
              <span className="service-label">Renewal or another file:</span>
              <LicenceUpload label="Renewal or another file" onStored={() => void refresh()} />
            </div>
          )}
        </div>
      )}
    </section>
  );
}
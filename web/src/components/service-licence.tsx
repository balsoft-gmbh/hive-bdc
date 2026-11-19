"use client";
import { StatusPill } from "@/components/ui/data-grid";
import { useConsole } from "./console-context";
import { LicenceUpload } from "./licence-upload";
import { Notice } from "./notice";
import { featureWords, germanDate, licenceNote, licenceWorks } from "@/lib/logic";
import { Facts, Panel } from "./service-ui";
import type { Identity } from "./service-status";

// The Licence tab: the licence of GET /status as read-only facts, the tenant it was checked against, its warning, and
// the drop zone for a renewal or another file (POST /license through LicenceUpload). Viewers and Operators see the
// licence only.

export function ServiceLicence({ identity }: { identity: Identity | null }) {
  const { me, status, refresh } = useConsole();
  const licence = status?.license ?? {};
  const works = licenceWorks(licence);
  const missing = !licence.state || licence.state === "Missing";
  const state = licence.state === "Grace" ? "Grace period" : works ? "Valid" : missing ? "No licence yet" : String(licence.state);
  const warning = licenceNote(licence) || (!works && licence.message) || "";
  const canChange = Boolean(me?.canChange);
  const tenant = identity?.tenantId || "";

  return (
    <Panel
      id="licence"
      title="Licence"
      pill={<StatusPill tone={works && licence.state === "Valid" ? "green" : works ? "amber" : "rose"}>{state}</StatusPill>}
    >
      {warning && <Notice kind="warn">{warning}</Notice>}
      <Facts
        items={
          missing
            ? [{ label: "Tenant", value: tenant, span: 2 as const }]
            : [
                { label: "Licensed to", value: licence.customer },
                { label: "Valid until", value: germanDate(licence.expires) },
                { label: "Features", value: featureWords(licence.features) },
                { label: "Tenant", value: tenant, span: 2 as const },
              ]
        }
      />
      {canChange && (
        <div className="service-upload">
          <span className="service-label">
            {missing ? "Licence file" : "New licence file"}
          </span>
          <LicenceUpload label={missing ? "Licence file" : "New licence file"} onStored={() => void refresh()} />
        </div>
      )}
    </Panel>
  );
}
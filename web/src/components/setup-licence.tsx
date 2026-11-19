"use client";
/* eslint-disable @typescript-eslint/no-explicit-any */
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useEffect, useState } from "react";
import { Mail } from "lucide-react";
import { api } from "@/lib/api";
import { featureWords, germanDate, licenceNote, licenceWorks } from "@/lib/logic";
import { ENTRA_PATH, ROLES, licenceRequest, stepHref } from "@/app/setup/steps";
import { StatusPill } from "./ui/data-grid";
import { useConsole } from "./console-context";
import { LicenceUpload } from "./licence-upload";
import { Facts, Hint, ICON, Panel, StepActions, StepFrame, type Progress } from "./setup-parts";

function licenceState(licence: any) {
  const state = String(licence.state || "Missing");
  if (state === "Missing") return <StatusPill tone="amber">No licence yet</StatusPill>;
  if (state === "Valid") return <StatusPill tone="green">Valid</StatusPill>;
  if (licenceWorks(licence)) return <StatusPill tone="amber">{state === "Grace" ? "Grace period" : "Valid"}</StatusPill>;
  return <StatusPill tone="rose">{state.toLowerCase()}</StatusPill>;
}

function Roles() {
  return (
    <Panel id="roles-heading" title="Roles">
      <dl className="setup-roles">
        {ROLES.map((role) => (
          <div key={role.name}>
            <dt>{role.name}</dt>
            <dd>{role.text}</dd>
          </div>
        ))}
      </dl>
      <p className="setup-entra">{`IT gives roles in ${ENTRA_PATH}.`}</p>
    </Panel>
  );
}

export function LicenceStep({ progress, readOnly }: { progress: Progress; readOnly: boolean }) {
  const { status, refresh } = useConsole();
  const router = useRouter();
  const licence = status?.license ?? {};
  const works = licenceWorks(licence);
  const [identity, setIdentity] = useState<any>(null);
  const [address, setAddress] = useState("");

  useEffect(() => {
    setAddress(window.location.origin);
    api("/setup/identity")
      .then(setIdentity)
      .catch((e: Error) => setIdentity({ message: e.message }));
  }, []);

  const tenant = identity?.tenantId ? String(identity.tenantId) : "";
  const stored = async () => {
    await refresh();
    router.push("/setup/");
  };

  const facts = [];
  if (works && licence.customer) facts.push({ label: "Licensed to", value: String(licence.customer) });
  if (works && licence.expires) facts.push({ label: "Valid until", value: germanDate(licence.expires) });
  facts.push({
    label: "Tenant ID",
    value: <code>{identity === null ? "Reading" : tenant || "Not known"}</code>,
    copy: !readOnly && tenant ? { text: tenant, label: "Copy the tenant ID" } : undefined,
  });
  facts.push({
    label: "Instance address",
    value: <code>{address}</code>,
    copy: !readOnly && address ? { text: address, label: "Copy the instance address" } : undefined,
  });
  if (works && featureWords(licence.features)) facts.push({ label: "Features", value: featureWords(licence.features) });

  const problem = works ? licenceNote(licence) : String(licence.state || "Missing") !== "Missing" ? licence.message : "";

  const next = works ? (
    <Link className="button primary" href={stepHref(2)}>
      Continue
    </Link>
  ) : readOnly ? undefined : (
    <button type="button" className="button primary" disabled>
      Continue
    </button>
  );

  return (
    <StepFrame step={1} progress={progress} readOnly={readOnly}>
      <Panel
        id="licence-heading"
        title="Licence"
        line={works || readOnly ? undefined : "Request the licence from Balsoft, then upload the .lic file."}
        state={licenceState(licence)}
      >
        {problem && <p className="setup-problem-line">{problem}</p>}
        <Facts items={facts} />
        {identity?.message && !tenant && <Hint warn>{identity.message}</Hint>}
        {!readOnly && (
          <div className="setup-licence-actions">
            <a className="button secondary" href={licenceRequest(tenant || "not known", address)}>
              <Mail {...ICON} />
              Request your licence
            </a>
            <LicenceUpload onStored={() => void stored()} label={works ? "Another licence file" : "Licence file"} />
          </div>
        )}
      </Panel>
      <Roles />
      <StepActions next={next} />
    </StepFrame>
  );
}
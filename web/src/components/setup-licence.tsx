"use client";
/* eslint-disable @typescript-eslint/no-explicit-any */
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useEffect, useState } from "react";
import { ArrowRight, BadgeCheck, Container, Eye, FileKey2, Mail, RotateCcw, TriangleAlert, UserCog, Users } from "lucide-react";
import { api } from "@/lib/api";
import { featureWords, germanDate, licenceNote, licenceWorks } from "@/lib/logic";
import { ENTRA_PATH, licenceRequest, stepHref } from "@/app/setup/steps";
import { useConsole } from "./console-context";
import { LicenceUpload } from "./licence-upload";
import { CopyButton, ICON, StepActions, StepFrame, type Progress } from "./setup-parts";

const ICON_18 = { size: 18, strokeWidth: 1.75 };

function LicenceState({ licence }: { licence: any }) {
  if (licenceWorks(licence)) {
    const line = [licence.expires ? `Valid until ${germanDate(licence.expires)}` : "", featureWords(licence.features)].filter(Boolean).join(" · ");
    const note = licenceNote(licence);
    return (
      <div className="setup-licence ok" role="status">
        <span className="setup-licence-mark">
          <BadgeCheck {...ICON_18} />
        </span>
        <span className="setup-licence-text">
          <strong>{licence.customer ? `Licensed to ${licence.customer}` : "Licensed"}</strong>
          {line && <span>{line}</span>}
          {note && <span className="warn-text">{note}</span>}
        </span>
      </div>
    );
  }
  if (String(licence.state) === "Missing" || !licence.state) {
    return (
      <div className="setup-licence" role="status">
        <span className="setup-licence-mark">
          <FileKey2 {...ICON_18} />
        </span>
        <span className="setup-licence-text">
          <strong>No licence yet</strong>
          <span className="muted">Choose the .lic file below.</span>
        </span>
      </div>
    );
  }
  return (
    <div className="setup-licence warn" role="status">
      <span className="setup-licence-mark">
        <TriangleAlert {...ICON_18} />
      </span>
      <span className="setup-licence-text">
        <strong>{`The licence is ${String(licence.state).toLowerCase()}`}</strong>
        {licence.message && <span>{licence.message}</span>}
      </span>
    </div>
  );
}

function Welcome() {
  return (
    <section className="panel" aria-labelledby="welcome-heading">
      <div className="section-heading">
        <div>
          <h2 id="welcome-heading">What you need</h2>
          <p>
            This Hive BDC instance runs in your own Azure subscription. It takes the documents F&O hands over and delivers each one to
            the places you choose, as an e-invoice where one is needed.
          </p>
        </div>
      </div>
      <ul className="setup-needs">
        <li>
          <span className="setup-need-icon">
            <FileKey2 {...ICON_18} />
          </span>
          <span>
            <strong>The licence file from Balsoft</strong>
            <span>Balsoft issues it for your tenant. Send them the two values below.</span>
          </span>
        </li>
        <li>
          <span className="setup-need-icon">
            <Container {...ICON_18} />
          </span>
          <span>
            <strong>The container or queue F&O writes to</strong>
            <span>Its address, and someone in your IT who can give this instance access to it.</span>
          </span>
        </li>
        <li>
          <span className="setup-need-icon">
            <Users {...ICON_18} />
          </span>
          <span>
            <strong>Roles for your colleagues</strong>
            <span>Your IT gives them in Microsoft Entra. Who can do what is explained on this page.</span>
          </span>
        </li>
      </ul>
    </section>
  );
}

function Roles() {
  return (
    <section className="panel" aria-labelledby="roles-heading">
      <div className="section-heading">
        <div>
          <h2 id="roles-heading">Who can do what</h2>
          <p>Colleagues sign in with their Microsoft account. Their role decides what they may do.</p>
        </div>
      </div>
      <div className="setup-panel-body">
        <dl className="setup-roles">
          <div>
            <dt>
              <Eye {...ICON} />
              Viewer
            </dt>
            <dd>Sees status, documents and logs.</dd>
          </div>
          <div>
            <dt>
              <RotateCcw {...ICON} />
              Operator
            </dt>
            <dd>Also retries documents that need attention.</dd>
          </div>
          <div>
            <dt>
              <UserCog {...ICON} />
              Administrator
            </dt>
            <dd>Also changes settings, connections and the licence.</dd>
          </div>
        </dl>
        <p className="muted">Your IT gives colleagues a role in Microsoft Entra:</p>
        <p className="setup-path">{ENTRA_PATH}</p>
      </div>
    </section>
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
  const tenantText = identity === null ? "Reading" : tenant || "Not known";

  const stored = async () => {
    await refresh();
    router.push("/setup/");
  };

  const next = works ? (
    <Link className="button primary" href={stepHref(2)}>
      Next: where F&O hands over
      <ArrowRight {...ICON} />
    </Link>
  ) : readOnly ? undefined : (
    <button type="button" className="button primary" disabled>
      Next: where F&O hands over
    </button>
  );

  return (
    <StepFrame
      step={1}
      progress={progress}
      readOnly={readOnly}
      title="Welcome to Hive BDC"
      description="Four steps, then your first document is on its way. Everything can be changed later."
    >
      <Welcome />
      <div className="setup-split">
        <section className="panel" aria-labelledby="licence-heading">
          <div className="section-heading">
            <div>
              <h2 id="licence-heading">Your licence</h2>
              <p>
                Upload the licence file Balsoft sent you (.lic). Hive BDC checks that it was issued for this Azure tenant and keeps it
                with the instance&apos;s settings.
              </p>
            </div>
          </div>
          <div className="setup-panel-body">
            <LicenceState licence={licence} />
            <div className="setup-request">
              <h3>Send Balsoft these two values</h3>
              <dl className="setup-values">
                <div>
                  <dt>Tenant ID</dt>
                  <dd>
                    <code>{tenantText}</code>
                    {!readOnly && tenant && <CopyButton text={tenant} label="Copy the tenant ID" />}
                  </dd>
                </div>
                <div>
                  <dt>Instance address</dt>
                  <dd>
                    <code>{address}</code>
                    {!readOnly && address && <CopyButton text={address} label="Copy the instance address" />}
                  </dd>
                </div>
              </dl>
              {identity?.message && !tenant && <p className="setup-hint">{identity.message}</p>}
              {!readOnly && (
                <a className="button secondary setup-request-link" href={licenceRequest(tenant || "not known", address)}>
                  <Mail {...ICON} />
                  Request your licence
                </a>
              )}
            </div>
            {!readOnly && <LicenceUpload onStored={() => void stored()} label={works ? "Another licence file" : "Licence file"} />}
            <div className="setup-help">
              <strong>If the file is refused</strong>
              <span>
                {
                  '"Issued for another tenant": the licence belongs to a different Microsoft 365 tenant; ask Balsoft for one for this tenant. "Expired": Balsoft sends the renewal each year; upload it here.'
                }
              </span>
            </div>
          </div>
        </section>
        <Roles />
      </div>
      <StepActions next={next} />
    </StepFrame>
  );
}
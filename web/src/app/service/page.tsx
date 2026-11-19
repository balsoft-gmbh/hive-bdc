"use client";
import { useSearchParams } from "next/navigation";
import { Suspense, useEffect, useState } from "react";
import { PageHeading } from "@/components/page-heading";
import { useConsole } from "@/components/console-context";
import { ServiceLicence } from "@/components/service-licence";
import { ServiceLog } from "@/components/service-log";
import { ServiceStatus, type Identity } from "@/components/service-status";
import { ServiceTabs, tabOf } from "@/components/service-ui";
import { ServiceUpdates } from "@/components/service-updates";
import { api } from "@/lib/api";
import { updateBadge, type UpdateView } from "@/lib/updatelogic";

// The Service page (design part 6): one page with the tabs Status, Licence, Updates and Technical log, chosen by
// ?tab= so a reload or a link (the Overview's licence tile) opens the same tab.

function Heading() {
  return (
    <PageHeading
      eyebrow="SERVICE"
      title="This instance"
      description="Whether it takes documents, its licence, its version and what it writes into its log."
    />
  );
}

function ServicePage() {
  const params = useSearchParams();
  const tab = tabOf(params.get("tab"));
  const { updates } = useConsole();
  const [identity, setIdentity] = useState<Identity | null>(null);
  const [identityError, setIdentityError] = useState("");
  const [identityAsked, setIdentityAsked] = useState(false);
  const [updateView, setUpdateView] = useState<UpdateView | null>(null);
  const needsIdentity = tab === "status" || tab === "licence";

  useEffect(() => {
    if (!needsIdentity || identityAsked) return;
    setIdentityAsked(true);
    api<Identity>("/setup/identity")
      .then((answer) => setIdentity(answer ?? {}))
      .catch((e: Error) => setIdentityError(e.message));
  }, [needsIdentity, identityAsked]);

  // On the Updates tab its own, newer answer counts; elsewhere the one the console read with the page.
  const count = updateBadge(tab === "updates" && updateView ? updateView : updates);

  return (
    <>
      <Heading />
      <ServiceTabs current={tab} count={count} />
      {tab === "status" && <ServiceStatus identity={identity} identityLoading={!identity && !identityError} identityError={identityError} />}
      {tab === "licence" && <ServiceLicence identity={identity} />}
      {tab === "updates" && <ServiceUpdates onView={setUpdateView} />}
      {tab === "log" && <ServiceLog />}
    </>
  );
}

export default function Page() {
  return (
    <Suspense fallback={<Heading />}>
      <ServicePage />
    </Suspense>
  );
}
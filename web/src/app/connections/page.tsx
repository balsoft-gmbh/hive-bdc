"use client";
import { Suspense } from "react";
import { ConnectionsPage } from "@/components/connections-page";

export default function Connections() {
  return (
    <div className="bdc-edit">
      <Suspense fallback={null}>
        <ConnectionsPage />
      </Suspense>
    </div>
  );
}
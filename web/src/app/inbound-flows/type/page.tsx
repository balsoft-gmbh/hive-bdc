"use client";
import { Suspense } from "react";
import { InboundType } from "@/components/inbound-type";

export default function InboundTypePage() {
  return (
    <div className="bdc-edit">
      <Suspense fallback={null}>
        <InboundType />
      </Suspense>
    </div>
  );
}

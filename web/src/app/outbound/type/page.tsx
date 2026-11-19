"use client";
import { Suspense } from "react";
import { OutboundType } from "@/components/outbound-type";

export default function OutboundTypePage() {
  return (
    <div className="bdc-edit">
      <Suspense fallback={null}>
        <OutboundType />
      </Suspense>
    </div>
  );
}
"use client";
import { Suspense } from "react";
import { SetupAssistant } from "@/components/setup-assistant";

export default function SetupPage() {
  return (
    <Suspense fallback={null}>
      <SetupAssistant />
    </Suspense>
  );
}
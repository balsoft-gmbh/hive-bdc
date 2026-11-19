"use client";
import { useSearchParams } from "next/navigation";
import { setupProgress } from "@/lib/logic";
import { shownStep } from "@/app/setup/steps";
import { useConsole } from "./console-context";
import { DestinationStep } from "./setup-destinations";
import { LicenceStep } from "./setup-licence";
import { SourceStep } from "./setup-source";
import { TestRunStep } from "./setup-test-run";

/**
 * The first-run assistant (design 2026-11-19-web-console-nextjs.md, decision 7): Licence, Where F&O hands over, Where
 * documents go, Test run, then Start. Only an Administrator acts; everyone else follows the steps read-only.
 */
export function SetupAssistant() {
  const params = useSearchParams();
  const { me, status, config } = useConsole();
  if (!me || !status || !config) return null;
  const progress = setupProgress(status, config);
  const step = shownStep(params.get("step"), progress.open);
  const readOnly = !me.canChange;
  if (step === 1) return <LicenceStep key="1" progress={progress} readOnly={readOnly} />;
  if (step === 2) return <SourceStep key="2" progress={progress} readOnly={readOnly} />;
  if (step === 3) {
    const type = params.get("type") ?? "";
    return <DestinationStep key={`3-${type}`} progress={progress} readOnly={readOnly} asked={type} />;
  }
  return <TestRunStep key="4" progress={progress} readOnly={readOnly} />;
}
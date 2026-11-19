// The set-up assistant's pure parts, apart from its components so tests/setup.test.ts runs them in Node.
/* eslint-disable @typescript-eslint/no-explicit-any */
import { typeWords } from "@/lib/logic";

export const STEP_NAMES = ["Licence", "Where F&O hands over", "Where documents go", "Test run"];

export const SUPPORT_MAIL = "support@balsoft.de";

export const ENTRA_PATH = "Microsoft Entra admin center > Enterprise applications > Hive BDC sign-in > Users and groups";

/**
 * The step on screen: the one the address asks for while it is reached, else the step reached. Progress comes from
 * status and settings, so a reload or a later visit resumes there (failure case 9).
 */
export function shownStep(asked: string | null | undefined, open: number) {
  const step = Number(asked);
  return Number.isInteger(step) && step >= 1 && step <= open ? step : open;
}

export function stepHref(step: number, type?: string) {
  return `/setup/?step=${step}${type ? `&type=${encodeURIComponent(type)}` : ""}`;
}

/** The mail to Balsoft that asks for the licence, with the two values it is issued for. */
export function licenceRequest(tenantId: string, address: string) {
  const body = [
    "Please send us the Hive BDC licence for this instance.",
    "",
    `Tenant ID: ${tenantId}`,
    `Instance address: ${address}`,
  ].join("\r\n");
  return `mailto:${SUPPORT_MAIL}?subject=${encodeURIComponent("Hive BDC licence")}&body=${encodeURIComponent(body)}`;
}

/** A document type in a sentence, always plural: "sales invoices". */
export function typesWord(type: any) {
  return typeWords(type, undefined);
}

/** The word before the company list: "company 1000", "companies 1000 and DEMF". */
export function companiesWord(companies: string[]) {
  return companies.length === 1 ? "company" : "companies";
}
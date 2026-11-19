// The set-up assistant's pure parts, apart from its components so tests/setup.test.ts runs them in Node.
/* eslint-disable @typescript-eslint/no-explicit-any */
import { typeWords } from "@/lib/logic";

export const STEP_NAMES = ["Licence", "Where F&O hands over", "Where documents go", "Test run"];

export const SETUP_TITLE = "Set up Hive BDC";

/** The one line under the title (design version 2, change 5: at most 60 characters). */
export const SETUP_LINE = "Hive BDC stays paused until you start it in step 4.";

export const SUPPORT_MAIL = "support@balsoft.de";

export const ENTRA_PATH = "Microsoft Entra admin center > Enterprise applications > Hive BDC sign-in > Users and groups";

export const ROLES = [
  { name: "Viewer", text: "Sees status, documents and logs." },
  { name: "Operator", text: "Also retries documents that need attention." },
  { name: "Administrator", text: "Also changes settings, connections and the licence." },
];

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

/** The accessible name of the step at this index of the step bar: "Step 1: Licence, done". */
export function stepLabel(index: number, done: boolean) {
  return `Step ${index + 1}: ${STEP_NAMES[index]}${done ? ", done" : ""}`;
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

/** "sales invoices" as a heading or list start: "Sales invoices". */
export function upperFirst(text: string) {
  return text ? text.charAt(0).toUpperCase() + text.slice(1) : text;
}

/** A source entry in the test-run list, in the Windows wording: "Sales invoices · Company 1000". */
export function entryName(type: any, company: string) {
  return `${upperFirst(typesWord(type))} · Company ${company}`;
}

/** Where a sender posts one company's documents of a type (Documents/HttpsInbound.cs). */
export function inboundAddress(origin: string, typeId: string, company: string) {
  return `${origin}/inbound/${encodeURIComponent(typeId)}/${encodeURIComponent(company)}`;
}

export type BusChoice = {
  access: "identity" | "key";
  namespace: string;
  connectionString: string;
  mode: "queue" | "topic";
  queue: string;
  topic: string;
  subscription: string;
  pdfAccount: string;
  pdfContainer: string;
  type: string;
  companies: string[];
};

/**
 * Whether the Service Bus form can be saved: a namespace, or a connection string (a stored one counts), the queue or
 * the topic with its subscription, a type, one company or more, and the PDFs' account and container both or neither.
 */
export function busChoiceReady(choice: BusChoice, keyStored: boolean) {
  const has = (v: string) => v.trim() !== "";
  const access = choice.access === "key" ? has(choice.connectionString) || keyStored : has(choice.namespace);
  const entity = choice.mode === "topic" ? has(choice.topic) && has(choice.subscription) : has(choice.queue);
  return access && entity && has(choice.type) && choice.companies.length > 0 && has(choice.pdfAccount) === has(choice.pdfContainer);
}

/** What the check of a queue or topic subscription is sent, as the Outbound page sends it. */
export function queueCheckBody(connection: string, choice: BusChoice) {
  const topic = choice.mode === "topic";
  return {
    connection,
    queue: topic ? "" : choice.queue.trim(),
    topic: topic ? choice.topic.trim() : "",
    subscription: topic ? choice.subscription.trim() : "",
  };
}

/**
 * What step 4 says to send where for a source without a test run: the queue or topic F&O sends to, or the address a
 * sender posts to.
 */
export function sendLine(entry: { type: any; company: any }, config: any, origin: string) {
  const source = entry.company.source || {};
  const s = source.settings || {};
  if (source.kind === "https") {
    return { text: "With the key in the x-api-key header.", address: inboundAddress(origin, entry.type.id, entry.company.dataAreaId) };
  }
  const connection = ((config && config.connections) || []).find((c: any) => c.id === source.connection);
  const space = connection?.settings?.Namespace ? ` on ${connection.settings.Namespace}` : "";
  const text = s.Queue ? `A message to queue ${s.Queue}${space}.` : `A message to topic ${s.Topic}${space}. Hive BDC reads subscription ${s.Subscription}.`;
  return { text, address: "" };
}
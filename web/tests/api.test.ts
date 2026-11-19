// npm test: the API client (failure case 6 of the console design: an ended session, errors in words).
import { test } from "node:test";
import assert from "node:assert/strict";
import { ApiError, createApi } from "../src/lib/api";

const answer = (status: number, body: string, type = "application/json") =>
  new Response(body, { status, headers: { "content-type": type } });

function client(responses: (() => Response | Promise<Response>)[], start = 1_000_000) {
  const seen: { url: string; init?: RequestInit }[] = [];
  let reloads = 0;
  let stored = 0;
  let clock = start;
  const api = createApi(
    async (url, init) => {
      seen.push({ url, init });
      return responses.shift()!();
    },
    () => reloads++,
    () => clock,
    { get: () => stored, set: (v) => (stored = v) },
  );
  return { api, seen, reloads: () => reloads, advance: (ms: number) => (clock += ms) };
}

test("calls go to /api with JSON and the marker that makes App Service answer 401", async () => {
  const c = client([() => answer(200, '{"running":true}')]);
  assert.deepEqual(await c.api("/status"), { running: true });
  assert.equal(c.seen[0].url, "/api/status");
  const headers = c.seen[0].init?.headers as Record<string, string>;
  assert.equal(headers["X-Requested-With"], "XMLHttpRequest");
  assert.equal(headers.Accept, "application/json");
});

test("a 401 reloads once; a second 401 within a minute shows a message instead of reloading again", async () => {
  const c = client([() => answer(401, ""), () => answer(401, ""), () => answer(401, "")]);
  await assert.rejects(c.api("/status"), (e: ApiError) => e.status === 401 && /Signing in again/.test(e.message));
  assert.equal(c.reloads(), 1);
  const after = client([() => answer(401, "")]);
  // A fresh page (new client) that shares the tab memory of the first one.
  await assert.rejects(c.api("/config"), /Signing in again/);
  assert.equal(c.reloads(), 1);
  void after;
});

test("a new client in the same tab does not reload again within a minute, but does after it", async () => {
  let stored = 0;
  let reloads = 0;
  let clock = 5_000_000;
  const memory = { get: () => stored, set: (v: number) => (stored = v) };
  const make = () => createApi(async () => answer(401, ""), () => reloads++, () => clock, memory);
  await assert.rejects(make()("/status"));
  assert.equal(reloads, 1);
  await assert.rejects(make()("/status"), /Signing in did not work/);
  assert.equal(reloads, 1);
  clock += 61_000;
  await assert.rejects(make()("/status"), /Signing in again/);
  assert.equal(reloads, 2);
});

test("an HTML page instead of JSON counts as an ended session", async () => {
  const c = client([() => answer(200, "<html>sign in</html>", "text/html; charset=utf-8")]);
  await assert.rejects(c.api("/status"), (e: ApiError) => e.status === 401);
  assert.equal(c.reloads(), 1);
});

test("errors carry the service's own words", async () => {
  const c = client([
    () => answer(403, '{"error":"This needs the role Operator."}'),
    () => answer(400, '{"errors":["Container is missing.","Company 1000 has no destination."]}'),
    () => answer(500, ""),
  ]);
  await assert.rejects(c.api("/history/retry"), (e: ApiError) => e.status === 403 && e.message === "This needs the role Operator.");
  await assert.rejects(c.api("/config"), /Container is missing\. Company 1000 has no destination\./);
  await assert.rejects(c.api("/config"), /The service answered 500\./);
});

test("no answer at all is a plain message, not a crash", async () => {
  const c = createApi(async () => {
    throw new TypeError("Failed to fetch");
  }, () => {});
  await assert.rejects(c("/status"), (e: ApiError) => e.status === 0 && /did not answer/.test(e.message));
});


test("App Service's empty 403 for an ended session signs in again; the host's own 403 keeps its words", async () => {
  const platform = client([() => new Response("", { status: 403 })]);
  await assert.rejects(platform.api("/status"), (e: ApiError) => e.status === 401);
  assert.equal(platform.reloads(), 1);
  const role = client([() => answer(403, '{"error":"This needs the role Administrator."}')]);
  await assert.rejects(role.api("/config"), (e: ApiError) => e.status === 403 && e.message === "This needs the role Administrator.");
  assert.equal(role.reloads(), 0);
});


test("an HTML error page while the instance restarts is an answer to show, not a reason to reload", async () => {
  const c = client([() => answer(503, "<html>Service Unavailable</html>", "text/html")]);
  await assert.rejects(c.api("/service/updates"), (e: ApiError) => e.status === 503);
  assert.equal(c.reloads(), 0);
});

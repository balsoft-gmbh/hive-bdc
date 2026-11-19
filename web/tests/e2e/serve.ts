import { createServer, type IncomingMessage, type ServerResponse } from "node:http";
import { existsSync, readFileSync, statSync } from "node:fs";
import { extname, join, normalize, sep } from "node:path";
import { defaults } from "./fixtures";

// Serves the static export as the Hive BDC web host does (Hosting/AzureWebHost.cs): pages with their own content
// security policy from out/csp.json, the API only under /api/ (here from the fixture state), everything else 404.
// The base policy is a copy of ConsoleFiles.PolicyFor; tests/csp.test.ts keeps both the same.
export const BASE_POLICY =
  "default-src 'self'; script-src 'self'{hashes}; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; " +
  "font-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'; object-src 'none'";

const out = join(process.cwd(), "out");
const port = Number(process.env.E2E_PORT ?? 3310);
let routes = defaults();
let calls: { method: string; path: string; body: unknown }[] = [];

const types: Record<string, string> = {
  ".html": "text/html; charset=utf-8",
  ".js": "text/javascript; charset=utf-8",
  ".css": "text/css; charset=utf-8",
  ".json": "application/json",
  ".svg": "image/svg+xml",
  ".png": "image/png",
  ".ttf": "font/ttf",
  ".txt": "text/plain; charset=utf-8",
  ".woff2": "font/woff2",
};

function readBody(req: IncomingMessage): Promise<string> {
  return new Promise((resolve) => {
    let data = "";
    req.on("data", (chunk) => (data += chunk));
    req.on("end", () => resolve(data));
  });
}

function json(res: ServerResponse, status: number, body: unknown) {
  res.writeHead(status, { "Content-Type": "application/json" });
  res.end(JSON.stringify(body));
}

function file(res: ServerResponse, path: string, status = 200) {
  const name = path.slice(out.length + 1).split(sep).join("/");
  const headers: Record<string, string> = { "Content-Type": types[extname(path)] ?? "application/octet-stream" };
  if (name.endsWith(".html")) {
    const csp = JSON.parse(readFileSync(join(out, "csp.json"), "utf8")) as Record<string, string[]>;
    const hashes = (csp[name] ?? []).map((h) => ` ${h}`).join("");
    headers["Content-Security-Policy"] = BASE_POLICY.replace("{hashes}", hashes);
    headers["Cache-Control"] = "no-store";
  }
  res.writeHead(status, headers);
  res.end(res.req.method === "HEAD" ? undefined : readFileSync(path));
}

createServer(async (req, res) => {
  const url = new URL(req.url ?? "/", "http://localhost");
  const method = req.method ?? "GET";
  if (url.pathname === "/__mock/health") return json(res, 200, { ok: true });
  if (url.pathname === "/__mock/reset") {
    routes = defaults();
    calls = [];
    return json(res, 200, { ok: true });
  }
  if (url.pathname === "/__mock/state") {
    Object.assign(routes, JSON.parse((await readBody(req)) || "{}"));
    return json(res, 200, { ok: true });
  }
  if (url.pathname === "/__mock/calls") return json(res, 200, calls);
  if (url.pathname.startsWith("/api/")) {
    const path = url.pathname.slice(4);
    const body = await readBody(req);
    if (method !== "GET") calls.push({ method, path, body: body ? JSON.parse(body) : null });
    const answer = routes[`${method} ${path}${url.search}`] ?? routes[`${method} ${path}`];
    if (answer === undefined) return json(res, 404, { error: `No mock for ${method} ${path}.` });
    const status = (answer as { __status?: number }).__status ?? 200;
    return json(res, status, answer);
  }
  // Next.js prefetches pages with HEAD; the host answers it like GET without the body.
  if (method !== "GET" && method !== "HEAD") return json(res, 405, { error: "Not allowed." });
  // Pages and files of the export only; no path leaves it.
  const relative = normalize(decodeURIComponent(url.pathname)).replace(/^([/\\])+/, "");
  if (relative.split(/[/\\]/).includes("..")) return file(res, join(out, "404.html"), 404);
  let path = join(out, relative);
  if (existsSync(path) && statSync(path).isDirectory()) path = join(path, "index.html");
  else if (!existsSync(path) && existsSync(`${path}.html`)) path = `${path}.html`;
  if (!existsSync(path)) return file(res, join(out, "404.html"), 404);
  return file(res, path);
}).listen(port, "127.0.0.1", () => console.log(`e2e server on http://127.0.0.1:${port}`));

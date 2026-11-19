// The console's only way to the instance: same origin, under /api/ (Hosting/AzureWebHost.cs strips the prefix).
// Requests are marked X-Requested-With, so App Service refuses an ended session instead of redirecting (failure case 6).

export class ApiError extends Error {
  constructor(
    message: string,
    readonly status: number,
    readonly body: unknown,
  ) {
    super(message);
  }
}

type Fetch = (input: string, init?: RequestInit) => Promise<Response>;
type Memory = { get(): number; set(value: number): void };

const RELOAD_KEY = "hivebdc-signin-reload";

/** When the last sign-in reload happened, kept for this browser tab only. */
const tabMemory: Memory = {
  get: () => {
    try {
      return Number(sessionStorage.getItem(RELOAD_KEY) ?? 0);
    } catch {
      return 0;
    }
  },
  set: (value) => {
    try {
      sessionStorage.setItem(RELOAD_KEY, String(value));
    } catch {
      // Storage can be blocked; then a second 401 reloads once more.
    }
  },
};

/**
 * The API client with fetch, reload, clock and memory injectable, so tests/api.test.ts can drive every answer. An
 * ended session reloads the page once, which goes through the App Service sign-in; a 401 again within a minute shows
 * a message instead of reloading for ever.
 */
export function createApi(fetchImpl: Fetch, reload: () => void, now: () => number = () => Date.now(), memory: Memory = tabMemory) {
  let reloading = false;
  const signedOut = () => {
    if (reloading) return new ApiError("Your session ended. Signing in again.", 401, null);
    if (now() - memory.get() < 60_000) {
      return new ApiError("Signing in did not work. Reload the page to sign in again.", 401, null);
    }
    reloading = true;
    memory.set(now());
    reload();
    return new ApiError("Your session ended. Signing in again.", 401, null);
  };
  return async function api<T = any>(path: string, init?: RequestInit): Promise<T> {
    let response: Response;
    try {
      response = await fetchImpl(`/api${path}`, {
        credentials: "same-origin",
        ...init,
        headers: {
          "Content-Type": "application/json",
          Accept: "application/json",
          "X-Requested-With": "XMLHttpRequest",
          ...(init?.headers ?? {}),
        },
      });
    } catch {
      throw new ApiError("Hive BDC did not answer. Check the connection and try again.", 0, null);
    }
    const type = response.headers.get("content-type") ?? "";
    // An ended session: App Service answers an XHR without sign-in with an empty 403 (seen on an instance 2026-11-19;
    // the host's own role refusal is a 403 with a JSON error), others with 401 or a sign-in page.
    const platformRefusal = response.status === 403 && !type.includes("application/json");
    // An HTML page with a success code is the sign-in page; an HTML error page (502 or 503 while the instance restarts
    // after an update) is an answer to show, not a reason to reload.
    if (response.status === 401 || platformRefusal || (response.ok && type.includes("text/html"))) throw signedOut();
    const text = await response.text();
    let body: any = null;
    try {
      body = text ? JSON.parse(text) : null;
    } catch {
      body = { error: text };
    }
    if (!response.ok) {
      const errors = body && Array.isArray(body.errors) && body.errors.length ? body.errors.join(" ") : "";
      throw new ApiError(
        (body && (body.error || body.message)) || errors || `The service answered ${response.status}.`,
        response.status,
        body,
      );
    }
    return body as T;
  };
}

export const api = createApi(
  (input, init) => fetch(input, init),
  () => window.location.reload(),
);

export type SaveResult = { ok: true } | { ok: false; message: string; conflict?: any };

/**
 * Saves the settings with the version they were read at. A 409 carries both change lists (failure cases 54, 61, 70 of
 * the connector design); the caller shows them and may send the body again with saveBoth.
 */
export async function saveConfig(body: unknown): Promise<SaveResult> {
  try {
    await api("/config", { method: "POST", body: JSON.stringify(body) });
    return { ok: true };
  } catch (error) {
    const e = error as ApiError;
    if (e.status === 409 && e.body) return { ok: false, message: e.message, conflict: e.body };
    return { ok: false, message: e.message };
  }
}

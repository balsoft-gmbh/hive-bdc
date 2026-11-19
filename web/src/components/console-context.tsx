"use client";
import { usePathname, useRouter } from "next/navigation";
import { createContext, useCallback, useContext, useEffect, useState } from "react";
import { api } from "@/lib/api";
import { setupNeeded } from "@/lib/logic";

export type Me = {
  name: string;
  roles: string[];
  canRetry: boolean;
  canChange: boolean;
  version: string;
};

type ConsoleState = {
  me: Me | null;
  status: any;
  config: any;
  updates: any;
  loaded: boolean;
  error: string;
  /** Reads status, settings and updates again, as every page change did in the former console. */
  refresh: () => Promise<void>;
};

const Context = createContext<ConsoleState | null>(null);

export function useConsole() {
  const value = useContext(Context);
  if (!value) throw new Error("useConsole needs the ConsoleProvider.");
  return value;
}

/** True while the set-up assistant owns the console: its set-up is open or no source takes documents yet. */
export function isSetupPath(pathname: string) {
  return pathname === "/setup" || pathname.startsWith("/setup/");
}

export function ConsoleProvider({ children }: { children: React.ReactNode }) {
  const pathname = usePathname();
  const router = useRouter();
  const [me, setMe] = useState<Me | null>(null);
  const [status, setStatus] = useState<any>(null);
  const [config, setConfig] = useState<any>(null);
  const [updates, setUpdates] = useState<any>(null);
  const [loaded, setLoaded] = useState(false);
  const [error, setError] = useState("");

  // Status and settings are what a page needs to start; the update view follows on its own, so a slow answer there
  // never holds the page back (measured on a B1 instance: single answers of up to 4 s right after a restart).
  const refresh = useCallback(async () => {
    api("/service/updates")
      .then(setUpdates)
      .catch(() => setUpdates(null));
    try {
      const [s, c] = await Promise.all([api("/status"), api("/config")]);
      setStatus(s);
      setConfig(c);
      setError("");
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setLoaded(true);
    }
  }, []);

  useEffect(() => {
    api<Me>("/me")
      .then(setMe)
      .catch((e: Error) => {
        setError(e.message);
        setLoaded(true);
      });
  }, []);

  // Who is signed in and the state of the instance are read side by side, not one after the other.
  useEffect(() => {
    void refresh();
  }, [pathname, refresh]);

  // A fresh instance opens on the assistant; once set up, the assistant is gone (failure case 9).
  useEffect(() => {
    if (!loaded || !config) return;
    const needed = setupNeeded(config);
    if (needed && !isSetupPath(pathname)) router.replace("/setup/");
    else if (!needed && isSetupPath(pathname)) router.replace("/");
  }, [loaded, config, pathname, router]);

  return (
    <Context.Provider value={{ me, status, config, updates, loaded, error, refresh }}>
      {children}
    </Context.Provider>
  );
}

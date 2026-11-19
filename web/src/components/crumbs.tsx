"use client";
import { createContext, useContext, useEffect, useState } from "react";

// Breadcrumb levels below the page itself, set by the page ("Outbound > Sales invoice > Company DEMF"), so no page
// draws its own path line (design version 2, change 4).

export type Crumb = { label: string; href?: string };

const Context = createContext<{ levels: Crumb[]; set: (levels: Crumb[]) => void }>({ levels: [], set: () => {} });

export function CrumbProvider({ children }: { children: React.ReactNode }) {
  const [levels, set] = useState<Crumb[]>([]);
  return <Context.Provider value={{ levels, set }}>{children}</Context.Provider>;
}

export function useCrumbLevels() {
  return useContext(Context).levels;
}

/** Sets the levels below the page while it is shown; a level without href is the current one. */
export function useCrumbs(levels: Crumb[]) {
  const { set } = useContext(Context);
  const key = JSON.stringify(levels);
  useEffect(() => {
    set(JSON.parse(key));
    return () => set([]);
  }, [key, set]);
}

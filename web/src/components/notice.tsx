import type { ReactNode } from "react";

/** A one-line message on a page: info and warn are announced politely, error as an alert. */
export function Notice({ kind, children }: { kind: "info" | "ok" | "warn" | "error"; children: ReactNode }) {
  return (
    <div className={`notice notice-${kind}`} role={kind === "error" ? "alert" : "status"}>
      {children}
    </div>
  );
}

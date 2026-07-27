import type { ReactNode } from "react";

/** Leer- bzw. Fehlerzeile innerhalb eines Panels — vorher ~sechsmal als Kopie im Code. */
export function EmptyState({ tone = "muted", children }: { tone?: "muted" | "danger"; children: ReactNode }) {
  return (
    <div className={`px-4 py-6 text-center text-[12.5px] ${tone === "danger" ? "text-danger" : "text-ink3"}`}>
      {children}
    </div>
  );
}

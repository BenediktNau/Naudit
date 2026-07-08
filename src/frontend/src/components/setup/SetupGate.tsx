import { useEffect, useState, type ReactNode } from "react";
import { api } from "@/api/client";
import type { SetupStatusDto } from "@/api/types";
import { SetupWizard } from "./SetupWizard";

/** Vor dem AuthGate: braucht die Instanz Setup, kommt der Wizard statt der App.
 *  Status-Fehler ⇒ normale App (das AuthGate hat eigenes Error-Handling). Bypass =
 *  Reparatur-Ausweg fuer Admins (Settings-Seite statt Wizard). */
export function SetupGate({ children }: { children: ReactNode }) {
  const [status, setStatus] = useState<SetupStatusDto | null | "error">(null);
  const [bypass, setBypass] = useState(false);

  useEffect(() => {
    api<SetupStatusDto>("/api/setup/status").then(setStatus, () => setStatus("error"));
  }, []);

  if (status === null) {
    return <div className="grid h-full place-items-center font-mono text-ink3">loading…</div>;
  }
  if (status !== "error" && status.setupRequired && !bypass) {
    return <SetupWizard status={status} onBypass={() => setBypass(true)} />;
  }
  return <>{children}</>;
}

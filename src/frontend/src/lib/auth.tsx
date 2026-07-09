import { createContext, useCallback, useContext, useEffect, useState, type ReactNode } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { api } from "@/api/client";
import type { MeDto } from "@/api/types";
import { LoginPage } from "@/components/pages/LoginPage";
import { PendingPage } from "@/components/pages/PendingPage";

interface AuthState {
  me: MeDto;
  refresh: () => Promise<void>;
  logout: () => Promise<void>;
}

const AuthContext = createContext<AuthState | null>(null);

export function useAuth(): AuthState {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error("useAuth outside <AuthGate>");
  return ctx;
}

/** BFF-AuthGate: klärt /api/me vor dem App-Start. Kein Login ⇒ LoginPage,
 *  pending/rejected ⇒ PendingPage, aktiv ⇒ App. 401 mid-use ⇒ zurück zum Login (via refresh). */
export function AuthGate({ children }: { children: ReactNode }) {
  const [me, setMe] = useState<MeDto | null>(null);
  const [error, setError] = useState(false);
  const queryClient = useQueryClient();

  const refresh = useCallback(async () => {
    // Fehler abfangen: /api/me kann bei Netzwerk-/5xx-Fehlern werfen. refresh() wird u. a.
    // fire-and-forget aus dem 401-Fänger aufgerufen — ohne catch gäbe das eine unhandled rejection.
    try {
      setError(false);
      setMe(await api<MeDto>("/api/me"));
    } catch {
      setError(true);
    }
  }, []);

  // Query-Cache leeren und /api/me neu klären — geteilt von logout UND dem 401-Fänger, damit nach
  // Session-Ablauf keine Daten des vorigen Users im Cache überleben (staleTime würde sie sonst bis
  // zu 30s als „frisch" an den nächsten Login servieren).
  const clearCacheAndRefresh = useCallback(async () => {
    queryClient.clear();
    await refresh();
  }, [refresh, queryClient]);

  const logout = useCallback(async () => {
    await api("/auth/logout", { method: "POST" });
    await clearCacheAndRefresh();
  }, [clearCacheAndRefresh]);

  useEffect(() => {
    // setState nur im Promise-Callback (nie synchron im Effect) — react-hooks/set-state-in-effect.
    api<MeDto>("/api/me").then(setMe, () => setError(true));
  }, []);

  // Globaler 401-Fänger: Session mid-use abgelaufen ⇒ /api/me neu klären ⇒ LoginPage.
  useEffect(() => {
    const original = window.fetch.bind(window);
    window.fetch = async (input, init) => {
      const res = await original(input, init);
      const url = typeof input === "string" ? input : input instanceof URL ? input.href : input.url;
      if (res.status === 401 && url.includes("/api/") && !url.endsWith("/api/me")) void clearCacheAndRefresh();
      return res;
    };
    return () => {
      window.fetch = original;
    };
  }, [clearCacheAndRefresh]);

  if (error) {
    return (
      <div className="grid h-full place-items-center gap-3 text-center font-mono text-sm text-ink3">
        <span className="text-danger">couldn’t reach the server</span>
        <button
          className="cursor-pointer rounded-lg border border-border px-4 py-2 text-ink hover:border-ink3 focus-visible:outline-2 focus-visible:outline-solid focus-visible:outline-offset-2 focus-visible:outline-teal"
          onClick={() => void refresh()}
        >
          retry
        </button>
      </div>
    );
  }
  if (me === null) {
    // Initialer /api/me-Load kann Login ODER App werden → kein App-Skeleton, nur ein dezenter Spinner.
    return (
      <div className="grid h-full place-items-center text-ink3">
        <span
          className="size-6 animate-spin rounded-full border-2 border-current border-t-transparent motion-reduce:animate-none"
          role="status"
          aria-label="loading"
        />
      </div>
    );
  }
  if (!me.isAuthenticated) {
    return <LoginPage providers={me.authProviders} onLoggedIn={refresh} />;
  }
  if (me.status !== "Active") {
    return <PendingPage me={me} onLogout={logout} />;
  }
  return <AuthContext.Provider value={{ me, refresh, logout }}>{children}</AuthContext.Provider>;
}

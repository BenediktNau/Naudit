import { createContext, useCallback, useContext, useEffect, useState, type ReactNode } from "react";
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

  const refresh = useCallback(async () => {
    setMe(await api<MeDto>("/api/me"));
  }, []);

  const logout = useCallback(async () => {
    await api("/auth/logout", { method: "POST" });
    await refresh();
  }, [refresh]);

  useEffect(() => {
    // setState nur im Promise-Callback (nie synchron im Effect) — react-hooks/set-state-in-effect.
    api<MeDto>("/api/me").then(setMe, () => setMe(null));
  }, []);

  // Globaler 401-Fänger: Session mid-use abgelaufen ⇒ /api/me neu klären ⇒ LoginPage.
  useEffect(() => {
    const original = window.fetch.bind(window);
    window.fetch = async (input, init) => {
      const res = await original(input, init);
      const url = typeof input === "string" ? input : input instanceof URL ? input.href : input.url;
      if (res.status === 401 && url.includes("/api/") && !url.endsWith("/api/me")) void refresh();
      return res;
    };
    return () => {
      window.fetch = original;
    };
  }, [refresh]);

  if (me === null) {
    return <div className="grid h-full place-items-center font-mono text-ink3">loading…</div>;
  }
  if (!me.isAuthenticated) {
    return <LoginPage providers={me.authProviders} onLoggedIn={refresh} />;
  }
  if (me.status !== "Active") {
    return <PendingPage me={me} onLogout={logout} />;
  }
  return <AuthContext.Provider value={{ me, refresh, logout }}>{children}</AuthContext.Provider>;
}

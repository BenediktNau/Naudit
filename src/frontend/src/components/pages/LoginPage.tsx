import { useState, type FormEvent } from "react";
import { api, ApiError } from "@/api/client";
import type { AuthProviders } from "@/api/types";
import { Button } from "@/components/ui/Button";
import { Input } from "@/components/ui/Input";
import { Logo } from "@/components/ui/Logo";

// Die Anmeldemaske baut sich gestaffelt auf — jede Gruppe startet etwas später als die davor.
const rise = (delay: number) => ({ animationDelay: `${delay}s` });

export function LoginPage({ providers, onLoggedIn }: { providers: AuthProviders; onLoggedIn: () => Promise<void> }) {
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const external = providers.gitHub || providers.oidc;

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      await api("/auth/login", { method: "POST", body: JSON.stringify({ username, password }) });
      await onLoggedIn();
    } catch (err) {
      setError(
        err instanceof ApiError && err.status === 401 ? "Wrong username or password." : "Sign-in failed — try again.",
      );
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="grid min-h-full animate-pagein place-items-center bg-[radial-gradient(130%_90%_at_50%_0%,rgba(74,222,128,.06),transparent_62%)] px-6 py-12">
      <div className="flex w-[372px] max-w-full flex-col items-center">
        <div className="animate-risein">
          <Logo size={56} />
        </div>
        <div className="mt-4.5 animate-risein text-[24px] font-semibold tracking-[-.02em] text-white" style={rise(0.06)}>
          Naudit
        </div>
        <div className="mt-1.5 animate-risein text-xs text-ink3" style={rise(0.1)}>
          Automated code review · sign in to continue
        </div>

        <form onSubmit={submit} className="mt-7.5 flex w-full animate-risein flex-col gap-2.5" style={rise(0.16)}>
          <Input
            className="w-full px-3.5 py-3 text-[13px]"
            placeholder="Username"
            aria-label="Username"
            value={username}
            onChange={(e) => setUsername(e.target.value)}
          />
          <Input
            className="w-full px-3.5 py-3 text-[13px]"
            type="password"
            placeholder="Password"
            aria-label="Password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
          />
          {error && <div className="font-mono text-xs text-danger">{error}</div>}
          <Button type="submit" disabled={busy || !username || !password} className="mt-0.5 w-full py-3">
            Sign in
          </Button>
        </form>

        {external && (
          <>
            <div className="my-5.5 flex w-full animate-risein items-center gap-3 text-[11px] text-ink3" style={rise(0.2)}>
              <span className="h-px flex-1 bg-hairline" />
              or
              <span className="h-px flex-1 bg-hairline" />
            </div>
            <div className="flex w-full animate-risein flex-col gap-2" style={rise(0.24)}>
              {providers.gitHub && (
                <a
                  href="/auth/login/github"
                  className="w-full rounded-[10px] border border-border py-3 text-center text-[13px] font-medium text-ink transition-colors duration-200 hover:border-ink3 hover:bg-input"
                >
                  Continue with GitHub
                </a>
              )}
              {providers.oidc && (
                <a
                  href="/auth/login/oidc"
                  className="w-full rounded-[10px] border border-border py-3 text-center text-[13px] font-medium text-ink transition-colors duration-200 hover:border-ink3 hover:bg-input"
                >
                  Continue with Keycloak
                </a>
              )}
            </div>
            <p className="mt-5.5 animate-risein text-center text-[11.5px] leading-relaxed text-ink3" style={rise(0.28)}>
              Self-service sign-ups start as{" "}
              <span className="rounded-[5px] bg-warn/12 px-1.5 font-mono text-[10.5px] text-warn">pending</span>.
              <br />
              Admin-created accounts are active immediately.
            </p>
          </>
        )}
        {!external && (
          <div
            className="mt-6 w-full animate-risein rounded-[14px] border border-dashed border-border p-4 text-xs leading-relaxed text-ink3"
            style={rise(0.2)}
          >
            External sign-in is disabled on this instance. Access is provisioned by the administrator.
          </div>
        )}
      </div>
    </div>
  );
}

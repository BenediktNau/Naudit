import { useState, type FormEvent } from "react";
import { api, ApiError } from "@/api/client";
import type { AuthProviders } from "@/api/types";
import { Button } from "@/components/ui/Button";
import { Logo } from "@/components/ui/Logo";

const inputCls =
  "w-full rounded-lg border border-border bg-bg px-4 py-3 font-mono text-[13.5px] text-ink outline-none placeholder:text-ink3 focus:border-acc";

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
      setError(err instanceof ApiError && err.status === 401 ? "Wrong username or password." : "Sign-in failed — try again.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="grid min-h-full place-items-center bg-[radial-gradient(130%_90%_at_50%_0%,rgba(74,222,128,.06),transparent_62%)] p-8">
      <div className="flex w-[380px] max-w-full flex-col items-center">
        <Logo size={64} />
        <div className="mt-5 font-mono text-[28px] font-bold tracking-tight text-white">naudit</div>
        <div className="mt-2 font-mono text-xs text-ink3">code review · access required</div>

        <form onSubmit={submit} className="mt-8 flex w-full flex-col gap-3">
          <input
            className={inputCls}
            placeholder="Username"
            aria-label="Username"
            value={username}
            onChange={(e) => setUsername(e.target.value)}
          />
          <input
            className={inputCls}
            type="password"
            placeholder="Password"
            aria-label="Password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
          />
          {error && <div className="font-mono text-xs text-danger">{error}</div>}
          <Button type="submit" disabled={busy || !username || !password} className="w-full py-3">
            Sign in
          </Button>
        </form>

        {external && (
          <>
            <div className="my-6 flex w-full items-center gap-3 font-mono text-[11px] text-ink3">
              <span className="h-px flex-1 bg-hairline" />
              or
              <span className="h-px flex-1 bg-hairline" />
            </div>
            <div className="flex w-full flex-col gap-2.5">
              {providers.gitHub && (
                <a
                  href="/auth/login/github"
                  className="w-full rounded-lg border border-border py-3 text-center text-sm font-semibold text-ink hover:border-ink3"
                >
                  Continue with GitHub
                </a>
              )}
              {providers.oidc && (
                <a
                  href="/auth/login/oidc"
                  className="w-full rounded-lg border border-border py-3 text-center text-sm font-semibold text-ink hover:border-ink3"
                >
                  Continue with Keycloak
                </a>
              )}
            </div>
            <p className="mt-6 text-center text-xs leading-relaxed text-ink3">
              Self-service sign-ups start as{" "}
              <span className="rounded bg-warn/12 px-1.5 font-mono text-[11px] text-warn">pending</span> —
              <br />
              accounts created by an admin are active immediately.
            </p>
          </>
        )}
        {!external && (
          <div className="mt-7 flex w-full items-start gap-3 rounded-xl border border-dashed border-border p-4 text-xs leading-relaxed text-ink3">
            External sign-in is disabled on this instance. Access is provisioned by the administrator.
          </div>
        )}
      </div>
    </div>
  );
}

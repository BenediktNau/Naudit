import { useState, type FormEvent } from "react";
import { api, ApiError } from "@/api/client";
import { Button } from "@/components/ui/Button";
import { Field, inputCls } from "./shared";

/** Schritt 1: Grafana-Muster — solange kein Admin existiert, wird hier einer angelegt
 *  (Server erzwingt das); existiert einer, ist Login Pflicht. Beide Wege setzen die
 *  Cookie-Session fuer die restlichen Schritte. */
export function StepAdmin({ adminExists, onDone }: { adminExists: boolean; onDone: () => void }) {
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      if (adminExists) {
        await api("/auth/login", { method: "POST", body: JSON.stringify({ username, password }) });
      } else {
        await api("/api/setup/admin", { method: "POST", body: JSON.stringify({ username, password }) });
      }
      onDone();
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) setError("Wrong username or password.");
      else if (err instanceof ApiError && err.status === 409) setError("An admin already exists — sign in instead.");
      else if (err instanceof ApiError && err.status === 400) setError("Username must not be empty; password needs at least 8 characters.");
      else setError("Request failed — try again.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <form onSubmit={submit} className="flex flex-col gap-4">
      <p className="text-[12.5px] text-ink3">
        {adminExists
          ? "An admin account already exists. Sign in to continue the setup."
          : "Create the admin account for this Naudit instance. It manages settings and account approvals."}
      </p>
      <Field label="Username">
        <input className={inputCls} value={username} onChange={(e) => setUsername(e.target.value)} />
      </Field>
      <Field label="Password" hint={adminExists ? undefined : "At least 8 characters."}>
        <input className={inputCls} type="password" value={password} onChange={(e) => setPassword(e.target.value)} />
      </Field>
      {error && <div className="font-mono text-xs text-danger">{error}</div>}
      <Button type="submit" disabled={busy || !username || !password} className="w-full py-3">
        {adminExists ? "Sign in & continue" : "Create admin & continue"}
      </Button>
    </form>
  );
}

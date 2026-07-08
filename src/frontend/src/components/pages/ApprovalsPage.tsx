import { useState, type FormEvent } from "react";
import { ApiError } from "@/api/client";
import type { AccountDto } from "@/api/types";
import { useAccounts, fmtTokens } from "@/hooks/queries";
import {
  useApproveAccount,
  useCreateAccount,
  useRejectAccount,
  useRevokeAccount,
  useSetGitHubLinks,
} from "@/hooks/mutations";
import { Button } from "@/components/ui/Button";
import { Panel } from "@/components/ui/Panel";
import { Pill } from "@/components/ui/Pill";

const inputCls =
  "w-full rounded-lg border border-border bg-bg px-3 py-2 font-mono text-[13px] text-ink outline-none placeholder:text-ink3 focus:border-acc";

function Avatar({ name }: { name: string }) {
  return (
    <span className="grid size-8 shrink-0 place-items-center rounded-full border border-border bg-elev text-xs font-bold text-acc">
      {name.slice(0, 1).toUpperCase()}
    </span>
  );
}

function AccountRow({ account }: { account: AccountDto }) {
  const approve = useApproveAccount();
  const reject = useRejectAccount();
  const revoke = useRevokeAccount();
  const links = useSetGitHubLinks();
  const [err, setErr] = useState<string | null>(null);

  const pending = account.status === "Pending";
  const meta = [
    account.provider === "Oidc" ? "OIDC" : account.provider,
    account.gitHubLogins.length > 0 ? `→ ${account.gitHubLogins.join(", ")}` : null,
    account.projectCount > 0 ? `${account.projectCount} projects` : null,
    account.totalTokens > 0 ? `${fmtTokens(account.totalTokens)} tokens` : null,
  ].filter(Boolean);
  const noLink = account.provider !== "GitHub" && account.gitHubLogins.length === 0;

  // Aktion feuern; Erfolg aktualisiert die Liste via invalidateQueries (im Hook), Fehler landet inline.
  function run<V>(
    m: { mutate: (vars: V, opts?: { onError?: () => void }) => void },
    vars: V,
    label: string,
  ) {
    setErr(null);
    m.mutate(vars, { onError: () => setErr(`${label} failed — try again.`) });
  }

  function editLinks() {
    const value = window.prompt("GitHub owners/orgs (comma-separated):", account.gitHubLogins.join(", "));
    if (value === null) return;
    run(
      links,
      { id: account.id, logins: value.split(",").map((s) => s.trim()).filter(Boolean) },
      "Saving links",
    );
  }

  return (
    <div className="flex items-center gap-3.5 border-b border-hairline px-5 py-4 last:border-b-0">
      <Avatar name={account.username} />
      <div className="min-w-0 flex-1">
        <div className="font-mono text-[13.5px]">{account.username}</div>
        <div className="mt-0.5 text-[11.5px] text-ink3">
          <span className="font-mono">{meta.join(" · ")}</span>
          {noLink && <span className="text-warn"> · no GitHub link</span>}
          {err && <span className="text-danger"> · {err}</span>}
        </div>
      </div>
      {account.isAdmin && <Pill kind="neutral">admin</Pill>}
      {pending ? (
        <>
          <Pill kind="warn">● pending</Pill>
          <Button
            className="px-3 py-1.5 text-xs"
            loading={approve.isPending}
            onClick={() => run(approve, account.id, "Approve")}
          >
            Approve
          </Button>
          <Button
            variant="ghost"
            className="px-3 py-1.5 text-xs"
            loading={reject.isPending}
            onClick={() => run(reject, account.id, "Reject")}
          >
            Reject
          </Button>
        </>
      ) : (
        <>
          <Pill kind="ok">✓ active</Pill>
          <Button
            variant="ghost"
            className="px-3 py-1.5 text-xs"
            loading={links.isPending}
            onClick={editLinks}
          >
            Links
          </Button>
          {!account.isAdmin && (
            <Button
              variant="dangerGhost"
              className="px-3 py-1.5 text-xs"
              loading={revoke.isPending}
              onClick={() => run(revoke, account.id, "Revoke")}
            >
              Revoke
            </Button>
          )}
        </>
      )}
    </div>
  );
}

export function ApprovalsPage() {
  const { data, isLoading } = useAccounts();
  const create = useCreateAccount();
  const [showForm, setShowForm] = useState(false);
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [logins, setLogins] = useState("");
  const [error, setError] = useState<string | null>(null);

  function submit(e: FormEvent) {
    e.preventDefault();
    setError(null);
    create.mutate(
      {
        username,
        password,
        gitHubLogins: logins
          .split(",")
          .map((s) => s.trim())
          .filter(Boolean),
      },
      {
        onSuccess: () => {
          setUsername("");
          setPassword("");
          setLogins("");
          setShowForm(false);
        },
        onError: (err) =>
          setError(
            err instanceof ApiError && err.status === 409
              ? "Username already exists or password too short (min 8 chars)."
              : "Creating the user failed.",
          ),
      },
    );
  }

  if (isLoading || !data) return <div className="p-8 font-mono text-ink3">loading…</div>;

  return (
    <div className="flex flex-col gap-5 px-7 py-6">
      <div className="flex items-center justify-between">
        <h2 className="font-mono text-lg font-bold">
          Approvals <span className="ml-2 text-[13px] font-normal text-ink3">{data.pending.length} open</span>
        </h2>
        <Button className="text-[13px]" onClick={() => setShowForm(!showForm)}>
          + Add user
        </Button>
      </div>

      {showForm && (
        <form onSubmit={submit} className="flex flex-col gap-3 rounded-xl border border-hairline bg-surface p-5 md:flex-row md:items-start">
          <input className={inputCls} placeholder="Username" value={username} onChange={(e) => setUsername(e.target.value)} />
          <input
            className={inputCls}
            type="password"
            placeholder="Password (min 8 chars)"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
          />
          <input
            className={inputCls}
            placeholder="GitHub owners (comma-separated)"
            value={logins}
            onChange={(e) => setLogins(e.target.value)}
          />
          <Button
            type="submit"
            loading={create.isPending}
            disabled={!username || password.length < 8}
            className="shrink-0"
          >
            Create
          </Button>
          {error && <div className="font-mono text-xs text-danger md:self-center">{error}</div>}
        </form>
      )}

      <Panel title="Awaiting approval">
        {data.pending.length === 0 && <div className="px-5 py-5 font-mono text-xs text-ink3">Nothing pending.</div>}
        {data.pending.map((a) => (
          <AccountRow key={a.id} account={a} />
        ))}
      </Panel>

      <Panel title="Approved" extra={`${data.approved.length} accounts`}>
        {data.approved.map((a) => (
          <AccountRow key={a.id} account={a} />
        ))}
      </Panel>
    </div>
  );
}

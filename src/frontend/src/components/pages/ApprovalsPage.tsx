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
import { Input } from "@/components/ui/Input";
import { PageHeader } from "@/components/ui/PageHeader";
import { EmptyState } from "@/components/ui/EmptyState";
import { Collapse } from "@/components/ui/Collapse";
import { Skeleton, SkeletonPanel, SkeletonRows } from "@/components/ui/Skeleton";

function Avatar({ name }: { name: string }) {
  return (
    <span className="grid size-8 shrink-0 place-items-center rounded-full bg-acc/12 text-xs font-bold text-acc">
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
  function run<V>(m: { mutate: (vars: V, opts?: { onError?: () => void }) => void }, vars: V, label: string) {
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
    <div className="flex flex-wrap items-center gap-3 border-b border-seam px-4 py-3.5 transition-colors duration-200 last:border-b-0 hover:bg-elev">
      <Avatar name={account.username} />
      <div className="min-w-[16ch] flex-1">
        <div className="flex items-center gap-2">
          <span className="text-[13px] font-medium text-ink">{account.username}</span>
          {account.isAdmin && (
            <span className="rounded-full border border-teal/40 px-1.5 py-px text-[10px] font-semibold tracking-[.07em] text-teal uppercase">
              admin
            </span>
          )}
        </div>
        <div className="mt-0.5 text-[11px] text-ink3">
          <span className="font-mono">{meta.join(" · ")}</span>
          {noLink && <span className="text-warn"> · no GitHub link</span>}
          {err && <span className="text-danger"> · {err}</span>}
        </div>
      </div>
      {pending ? (
        <>
          <Pill kind="warn" dot>
            pending
          </Pill>
          <div className="flex shrink-0 gap-1.5">
            <Button className="px-3.5 py-1.5 text-xs" loading={approve.isPending} onClick={() => run(approve, account.id, "Approve")}>
              Approve
            </Button>
            <Button
              variant="secondary"
              className="px-3.5 py-1.5 text-xs"
              loading={reject.isPending}
              onClick={() => run(reject, account.id, "Reject")}
            >
              Reject
            </Button>
          </div>
        </>
      ) : (
        <>
          <Pill kind="ok" dot>
            active
          </Pill>
          <div className="flex shrink-0 gap-1.5">
            <Button variant="secondary" className="px-3 py-1.5 text-xs" loading={links.isPending} onClick={editLinks}>
              Links
            </Button>
            {!account.isAdmin && (
              <Button
                variant="secondary"
                className="px-3 py-1.5 text-xs hover:border-danger hover:text-danger"
                loading={revoke.isPending}
                onClick={() => run(revoke, account.id, "Revoke")}
              >
                Revoke
              </Button>
            )}
          </div>
        </>
      )}
    </div>
  );
}

// Skeleton der Approvals-Seite: Kopfzeile + zwei Panels mit Konto-Zeilen (Avatar + Text + Aktionen).
function AccountRowSkeleton() {
  return (
    <>
      <Skeleton className="size-8 shrink-0 rounded-full" />
      <div className="min-w-0 flex-1">
        <Skeleton className="h-3 w-32" />
        <Skeleton className="mt-1.5 h-2.5 w-48" />
      </div>
      <Skeleton className="h-6 w-16 rounded-lg" />
      <Skeleton className="h-6 w-16 rounded-lg" />
    </>
  );
}

function ApprovalsSkeleton() {
  return (
    <div className="flex flex-col gap-3.5">
      <div className="flex items-center justify-between">
        <Skeleton className="h-6 w-40" />
        <Skeleton className="h-8 w-24 rounded-lg" />
      </div>
      <SkeletonPanel>
        <SkeletonRows count={2}>{() => <AccountRowSkeleton />}</SkeletonRows>
      </SkeletonPanel>
      <SkeletonPanel>
        <SkeletonRows count={4}>{() => <AccountRowSkeleton />}</SkeletonRows>
      </SkeletonPanel>
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

  if (isLoading || !data) return <ApprovalsSkeleton />;

  return (
    <>
      <PageHeader
        title="Access"
        subtitle={
          data.pending.length === 0
            ? "No accounts waiting for approval."
            : `${data.pending.length} ${data.pending.length === 1 ? "account" : "accounts"} waiting for approval.`
        }
      >
        <Button onClick={() => setShowForm(!showForm)} aria-expanded={showForm} className="text-[12.5px]">
          + Add user
        </Button>
      </PageHeader>

      <div className="flex flex-col gap-3.5">
        {showForm && (
          <Collapse>
            <form
              onSubmit={submit}
              className="flex flex-wrap items-center gap-2 rounded-[14px] border border-hairline bg-surface p-4"
            >
              <Input
                className="min-w-0 flex-[1_1_180px]"
                placeholder="Username"
                aria-label="Username"
                value={username}
                onChange={(e) => setUsername(e.target.value)}
              />
              <Input
                className="min-w-0 flex-[1_1_180px]"
                type="password"
                placeholder="Password (min 8 chars)"
                aria-label="Password"
                value={password}
                onChange={(e) => setPassword(e.target.value)}
              />
              <Input
                className="min-w-0 flex-[1_1_180px]"
                placeholder="GitHub owners (comma-separated)"
                aria-label="GitHub owners"
                value={logins}
                onChange={(e) => setLogins(e.target.value)}
              />
              <Button type="submit" loading={create.isPending} disabled={!username || password.length < 8} className="shrink-0">
                Create
              </Button>
              {error && <div className="font-mono text-xs text-danger">{error}</div>}
            </form>
          </Collapse>
        )}

        <Panel title="Awaiting approval" extra={`${data.pending.length} open`}>
          {data.pending.length === 0 && <EmptyState>Nothing pending.</EmptyState>}
          {data.pending.map((a) => (
            <AccountRow key={a.id} account={a} />
          ))}
        </Panel>

        <Panel title="Active accounts" extra={`${data.approved.length} accounts`}>
          {data.approved.length === 0 && <EmptyState>No active accounts.</EmptyState>}
          {data.approved.map((a) => (
            <AccountRow key={a.id} account={a} />
          ))}
        </Panel>
      </div>
    </>
  );
}

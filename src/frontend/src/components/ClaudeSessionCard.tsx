import { useState } from "react";
import {
  useClaudeSession,
  useSaveClaudeSession,
  useDeleteClaudeSession,
  useTestClaudeSession,
} from "@/hooks/queries";
import { Panel } from "@/components/ui/Panel";
import { Pill } from "@/components/ui/Pill";

const inputCls =
  "w-full rounded-lg border border-border bg-bg px-3 py-2 font-mono text-[13px] text-ink outline-none placeholder:text-ink3 focus:border-acc";
const btnPrimary =
  "cursor-pointer rounded-lg bg-acc px-3 py-1.5 text-xs font-bold text-accink transition-colors hover:bg-acc2 disabled:cursor-not-allowed disabled:opacity-50";
const btnGhost =
  "cursor-pointer rounded-lg border border-border px-3 py-1.5 text-xs font-bold text-ink transition-colors hover:border-acc disabled:cursor-not-allowed disabled:opacity-50";

/** Profil-Karte: eigener Claude-Code-OAuth-Token fuer Autor-gebundene Reviews (Task 9-API).
 *  Token-Feld ist write-only — ein leerer Wert beim Speichern behaelt den gespeicherten Token (Backend-Semantik). */
export function ClaudeSessionCard() {
  const { data } = useClaudeSession();
  const save = useSaveClaudeSession();
  const remove = useDeleteClaudeSession();
  const test = useTestClaudeSession();
  const [token, setToken] = useState("");
  const [login, setLogin] = useState<string | null>(null);
  if (!data) return null;

  const cooling = data.coolingDownUntil !== null && new Date(data.coolingDownUntil) > new Date();
  const loginValue = login ?? data.gitAuthorLogin ?? "";

  return (
    <Panel title="Claude session" extra={data.configured ? "configured" : "not configured"}>
      <div className="flex flex-col gap-4 px-5 py-4">
        <div className="flex flex-wrap items-center gap-3">
          {data.configured ? <Pill kind="ok">✓ token stored</Pill> : <Pill kind="warn">● no token</Pill>}
          {cooling && (
            <Pill kind="warn">● cooling down until {new Date(data.coolingDownUntil!).toLocaleTimeString()}</Pill>
          )}
          {data.updatedAtUtc && (
            <span className="font-mono text-[11px] text-ink3">
              since {new Date(data.updatedAtUtc).toLocaleDateString()}
            </span>
          )}
        </div>

        <p className="text-[12.5px] leading-relaxed text-ink2">
          Store the OAuth token from <code className="font-mono text-acc">claude setup-token</code> (Claude Pro/Max)
          and reviews of merge requests <b>you</b> authored run on your subscription. Your token is used{" "}
          <b>only for your own MRs</b> — never for other users&apos; work.
        </p>

        <input
          type="password"
          className={inputCls}
          placeholder={data.configured ? "•••••• (stored — leave blank to keep)" : "paste token"}
          value={token}
          onChange={(e) => setToken(e.target.value)}
        />
        <input
          className={inputCls}
          placeholder="git login (your GitLab/GitHub username)"
          value={loginValue}
          onChange={(e) => setLogin(e.target.value)}
        />

        <div className="flex flex-wrap items-center gap-2">
          <button
            className={btnPrimary}
            disabled={save.isPending || (!token && !data.configured)}
            onClick={() =>
              save.mutate(
                { token: token || undefined, gitAuthorLogin: loginValue || undefined },
                { onSuccess: () => setToken("") },
              )
            }
          >
            Save
          </button>
          <button className={btnGhost} disabled={!data.configured || test.isPending} onClick={() => test.mutate()}>
            {test.isPending ? "Testing…" : "Test"}
          </button>
          <button
            className={`${btnGhost} text-warn hover:border-warn`}
            disabled={!data.configured || remove.isPending}
            onClick={() => remove.mutate()}
          >
            Remove
          </button>
          {test.data &&
            (test.data.ok ? <Pill kind="ok">✓ works</Pill> : <Pill kind="warn">● {test.data.error ?? "failed"}</Pill>)}
        </div>
      </div>
    </Panel>
  );
}

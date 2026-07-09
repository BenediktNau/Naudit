import { Panel } from "@/components/ui/Panel";
import { Field } from "@/components/setup/shared";
import { Button } from "@/components/ui/Button";
import { SelectableCard, AuthChip, Toggle } from "../primitives";
import type { SettingsCtx } from "../model";

const inputCls =
  "w-[360px] max-w-full rounded-lg border border-border bg-bg px-3 py-2 font-mono text-[13px] text-ink outline-none placeholder:text-ink3 focus:border-acc disabled:opacity-50";

function Secret({ ctx, k, label, hint }: { ctx: SettingsCtx; k: string; label: string; hint: string }) {
  return (
    <Field label={label} hint={hint}>
      <input type="password" className={inputCls} disabled={ctx.locked(k)}
        placeholder={ctx.secretSet(k) ? "•••••• (set)" : "not set"}
        value={ctx.get(k)} onChange={(e) => ctx.set(k, e.target.value)} />
    </Field>
  );
}

export function GitCategory({ ctx }: { ctx: SettingsCtx }) {
  const platform = ctx.get("Naudit:Git:Platform") || "GitLab";
  const isGitHub = platform === "GitHub";
  const usesApp = ctx.get("Naudit:GitHub:Auth") === "App";

  return (
    <>
      <div className="grid grid-cols-2 gap-3">
        {(["GitLab", "GitHub"] as const).map((p) => (
          <SelectableCard key={p} selected={platform === p} onClick={() => ctx.set("Naudit:Git:Platform", p)}
            disabled={ctx.locked("Naudit:Git:Platform")}>
            <b className="text-[14px]">{p}</b>
            <p className="text-[12.5px] text-ink2">{p === "GitLab" ? "Merge requests on GitLab / self-managed." : "Pull requests on GitHub / GHES."}</p>
          </SelectableCard>
        ))}
      </div>

      {isGitHub ? (
        <Panel title="GitHub connection">
          <div className="flex flex-col gap-4 px-5 py-4">
            <div className="flex gap-2">
              <AuthChip selected={!usesApp} onClick={() => ctx.set("Naudit:GitHub:Auth", "Pat")}
                disabled={ctx.locked("Naudit:GitHub:Auth")}>
                {!usesApp ? "✓ " : ""}Personal access token
              </AuthChip>
              <AuthChip selected={usesApp} onClick={() => ctx.set("Naudit:GitHub:Auth", "App")}
                disabled={ctx.locked("Naudit:GitHub:Auth")}>
                GitHub App (bot)
              </AuthChip>
            </div>

            {usesApp ? (
              <>
                <Field label="App ID" hint="From the GitHub App's settings page.">
                  <input className={inputCls} value={ctx.get("Naudit:GitHub:App:AppId")} disabled={ctx.locked("Naudit:GitHub:App:AppId")}
                    onChange={(e) => ctx.set("Naudit:GitHub:App:AppId", e.target.value)} />
                </Field>
                <Secret ctx={ctx} k="Naudit:GitHub:App:PrivateKey" label="Private key (PEM)" hint="Raw PEM or base64. Stored encrypted." />
                <Field label="Installation ID (optional)" hint="Skips the per-repo lookup when set.">
                  <input className={inputCls} value={ctx.get("Naudit:GitHub:App:InstallationId")} disabled={ctx.locked("Naudit:GitHub:App:InstallationId")}
                    onChange={(e) => ctx.set("Naudit:GitHub:App:InstallationId", e.target.value)} />
                </Field>
              </>
            ) : (
              <Secret ctx={ctx} k="Naudit:GitHub:Token" label="Access token"
                hint="Fine-grained PAT with pull request read/write. Comments appear as the token's owner." />
            )}

            <Secret ctx={ctx} k="Naudit:GitHub:WebhookSecret" label="Webhook secret"
              hint="Must match the secret entered in the repo's webhook settings." />
            <Field label="API base URL (optional)" hint="Only change this for GitHub Enterprise.">
              <input className={inputCls} value={ctx.get("Naudit:GitHub:BaseUrl")} placeholder="https://api.github.com"
                disabled={ctx.locked("Naudit:GitHub:BaseUrl")}
                onChange={(e) => ctx.set("Naudit:GitHub:BaseUrl", e.target.value)} />
            </Field>

            <div className="flex items-center justify-between border-t border-hairline pt-4">
              <div>
                <div className="text-[13px] font-medium text-ink">Post a real review verdict</div>
                <div className="text-[11.5px] text-ink3">APPROVE / REQUEST_CHANGES instead of a plain comment.</div>
              </div>
              <Toggle on={ctx.get("Naudit:GitHub:PostVerdict") === "true"}
                disabled={ctx.locked("Naudit:GitHub:PostVerdict")}
                onChange={(v) => ctx.set("Naudit:GitHub:PostVerdict", v ? "true" : "false")} />
            </div>
          </div>
        </Panel>
      ) : (
        <Panel title="GitLab connection">
          <div className="flex flex-col gap-4 px-5 py-4">
            <Field label="Base URL" hint="e.g. https://gitlab.example.com">
              <input className={inputCls} value={ctx.get("Naudit:GitLab:BaseUrl")} disabled={ctx.locked("Naudit:GitLab:BaseUrl")}
                onChange={(e) => ctx.set("Naudit:GitLab:BaseUrl", e.target.value)} />
            </Field>
            <Secret ctx={ctx} k="Naudit:GitLab:Token" label="Access token" hint="Token with api scope (read diff, post comment)." />
            <Secret ctx={ctx} k="Naudit:GitLab:WebhookSecret" label="Webhook secret" hint="Checked against the X-Gitlab-Token header." />
            <div className="flex items-center justify-between border-t border-hairline pt-4">
              <div>
                <div className="text-[13px] font-medium text-ink">Post a real review verdict</div>
                <div className="text-[11.5px] text-ink3">Calls MR approve / unapprove from the verdict.</div>
              </div>
              <Toggle on={ctx.get("Naudit:GitLab:PostVerdict") === "true"}
                disabled={ctx.locked("Naudit:GitLab:PostVerdict")}
                onChange={(v) => ctx.set("Naudit:GitLab:PostVerdict", v ? "true" : "false")} />
            </div>
          </div>
        </Panel>
      )}

      {isGitHub && !usesApp && (
        <div className="flex items-center justify-between rounded-xl border border-hairline bg-surface px-5 py-4">
          <div>
            <div className="text-[13px] font-semibold text-ink">Run as a bot instead</div>
            <p className="max-w-[56ch] text-[12.5px] text-ink2">
              A GitHub App posts reviews under its own identity and scales past a single user's rate limits.
            </p>
          </div>
          <Button variant="secondary" className="shrink-0 px-3 py-1.5 text-[12.5px]"
            onClick={() => ctx.openWizard({ kind: "github-app", step: 1 })}>
            Set up GitHub App →
          </Button>
        </div>
      )}

      <div className="flex items-center gap-2 text-[12px] text-ink3">
        <span className="inline-block size-2 rounded-full bg-warn" aria-hidden="true" />
        Changes apply after a restart — you'll be prompted when you save.
      </div>
    </>
  );
}

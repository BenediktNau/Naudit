import { Button } from "@/components/ui/Button";
import { CopyRow, Field, inputCls, randomSecret, type WizardDraft } from "./shared";

/** Schritt 3: Plattform-Wahl. PR 2 = manuelle Pfade (GitHub-PAT, GitLab); der
 *  GitHub-App-Manifest-Flow kommt in PR 3. Das Webhook-Secret generiert Naudit —
 *  der Nutzer traegt URL + Secret in der Plattform ein (Anleitung inline). */
export function StepPlatform({ draft, hasGitToken, update, onNext }: {
  draft: WizardDraft;
  hasGitToken: boolean;
  update: (patch: Partial<WizardDraft>) => void;
  onNext: () => void;
}) {
  const base = draft.publicBaseUrl.replace(/\/+$/, "");
  const pick = (platform: "GitHub" | "GitLab") =>
    update({ platform, gitToken: "", webhookSecret: draft.webhookSecret || randomSecret() });

  const tokenOk = draft.gitToken !== "" || (hasGitToken && draft.platform !== "");
  const ready =
    draft.platform === "GitHub"
      ? tokenOk
      : draft.platform === "GitLab"
        ? tokenOk && /^https?:\/\/.+/.test(draft.gitLabBaseUrl)
        : false;

  return (
    <div className="flex flex-col gap-4">
      <div className="grid grid-cols-2 gap-3">
        {(["GitHub", "GitLab"] as const).map((p) => (
          <button
            key={p}
            type="button"
            onClick={() => pick(p)}
            className={`cursor-pointer rounded-xl border px-4 py-3 text-left font-mono text-[13px] ${
              draft.platform === p ? "border-acc text-ink" : "border-border text-ink2 hover:border-ink3"
            }`}
          >
            <div className="font-bold">{p}</div>
            <div className="mt-1 text-[11px] text-ink3">
              {p === "GitHub" ? "Personal access token" : "Self-hosted or gitlab.com"}
            </div>
          </button>
        ))}
      </div>
      <p className="text-[11.5px] text-ink3">
        Using a GitHub App? One-click app creation is coming next — for now, configure it on the Settings page.
      </p>

      {draft.platform === "GitLab" && (
        <Field label="GitLab base URL">
          <input
            className={inputCls}
            placeholder="https://gitlab.example.com"
            value={draft.gitLabBaseUrl}
            onChange={(e) => update({ gitLabBaseUrl: e.target.value })}
          />
        </Field>
      )}
      {draft.platform !== "" && (
        <Field
          label={draft.platform === "GitHub" ? "GitHub personal access token" : "GitLab access token (api scope)"}
          hint={hasGitToken && draft.gitToken === "" ? "A token is already stored — leave empty to keep it." : undefined}
        >
          <input
            className={inputCls}
            type="password"
            placeholder={hasGitToken ? "•••••• (stored)" : ""}
            value={draft.gitToken}
            onChange={(e) => update({ gitToken: e.target.value })}
          />
        </Field>
      )}

      {draft.platform !== "" && (
        <div className="flex flex-col gap-2">
          <div className="text-[12.5px] font-medium text-ink2">
            Add this webhook in {draft.platform === "GitHub" ? "your repository settings (Webhooks → Add webhook, event: pull requests)" : "your project settings (Webhooks, trigger: merge request events)"}:
          </div>
          <CopyRow label="Webhook URL" value={`${base}/webhook/${draft.platform.toLowerCase()}`} />
          <CopyRow label={draft.platform === "GitHub" ? "Webhook secret" : "Secret token"} value={draft.webhookSecret} />
        </div>
      )}

      <Button onClick={onNext} disabled={!ready} className="w-full py-3">
        Continue
      </Button>
    </div>
  );
}

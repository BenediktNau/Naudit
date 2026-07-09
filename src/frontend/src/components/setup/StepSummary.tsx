import { Button } from "@/components/ui/Button";
import { CopyRow, type WizardDraft } from "./shared";

/** Schritt 6: alles auf einen Blick (Secrets maskiert), Webhook-Daten nochmal prominent —
 *  nach dem Neustart sind Secrets write-only und nicht mehr ablesbar. */
export function StepSummary({ draft, hasGitToken, hasAiApiKey, applying, applyError, onApply }: {
  draft: WizardDraft;
  hasGitToken: boolean;
  hasAiApiKey: boolean;
  applying: boolean;
  applyError: string | null;
  onApply: () => void;
}) {
  const base = draft.publicBaseUrl.replace(/\/+$/, "");
  // GitHub-App-Zweig: statt der Token-Zeile den App-Slug zeigen (Auth laeuft ueber die App).
  const gitAuthRow: [string, string] =
    draft.platform === "GitHub" && draft.gitHubAuth === "App"
      ? ["Git auth", `GitHub App (${draft.gitHubAppSlug})`]
      : ["Git token", draft.gitToken !== "" || hasGitToken ? "•••••• (set)" : "—"];
  const rows: [string, string][] = [
    ["Public base URL", draft.publicBaseUrl],
    ["Platform", draft.platform],
    gitAuthRow,
    ...(draft.platform === "GitLab" ? ([["GitLab base URL", draft.gitLabBaseUrl]] as [string, string][]) : []),
    ["AI provider", draft.aiProvider],
    ["AI model", draft.aiModel || "(default)"],
    ["AI endpoint", draft.aiEndpoint || "(default)"],
    ...(draft.aiApiKey !== "" || hasAiApiKey ? ([["AI API key", "•••••• (set)"]] as [string, string][]) : []),
    ["Access mode", draft.accessGateMode],
  ];
  return (
    <div className="flex flex-col gap-4">
      <div className="overflow-hidden rounded-xl border border-hairline">
        {rows.map(([k, v]) => (
          <div key={k} className="flex items-center justify-between border-b border-hairline px-4 py-2.5 last:border-b-0">
            <span className="text-[12.5px] text-ink3">{k}</span>
            <span className="font-mono text-[12.5px] text-ink">{v}</span>
          </div>
        ))}
      </div>

      <div className="flex flex-col gap-2">
        <div className="text-[12.5px] font-medium text-ink2">
          Make sure this webhook is configured — copy it now, the secret is write-only after setup:
        </div>
        <CopyRow label="Webhook URL" value={`${base}/webhook/${draft.platform.toLowerCase()}`} />
        <CopyRow label="Webhook secret" value={draft.webhookSecret} />
      </div>

      {applyError && <div className="font-mono text-xs text-danger">{applyError}</div>}
      <Button onClick={onApply} disabled={applying} className="w-full py-3">
        {applying ? "applying & restarting…" : "Apply & restart"}
      </Button>
    </div>
  );
}

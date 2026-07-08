import { useCallback, useState } from "react";
import { api } from "@/api/client";
import type { SetupDraftResponse, SetupStatusDto } from "@/api/types";
import { Button } from "@/components/ui/Button";
import { Logo } from "@/components/ui/Logo";
import { emptyDraft, type WizardDraft } from "./shared";
import { StepAdmin } from "./StepAdmin";
import { StepInstance } from "./StepInstance";
import { StepPlatform } from "./StepPlatform";
import { StepAi } from "./StepAi";
import { StepAccess } from "./StepAccess";
import { StepSummary } from "./StepSummary";

const TITLES = ["Admin account", "Instance URL", "Git platform", "AI provider", "Access model", "Review & apply"];

/** First-Run-Wizard: linearer Flow, Fortschritt liegt serverseitig als DP-verschluesselter
 *  Draft (ueberlebt Reloads und den Manifest-Redirect in PR 3). Nach "Apply" pollt der
 *  Wizard den Status ueber den In-Process-Neustart hinweg. */
export function SetupWizard({ status, onBypass }: { status: SetupStatusDto; onBypass: () => void }) {
  const [step, setStep] = useState(0);
  const [draft, setDraft] = useState<WizardDraft>({ ...emptyDraft, publicBaseUrl: status.suggestedPublicBaseUrl ?? "" });
  const [hasGitToken, setHasGitToken] = useState(false);
  const [hasAiApiKey, setHasAiApiKey] = useState(false);
  const [applying, setApplying] = useState(false);
  const [applyError, setApplyError] = useState<string | null>(null);
  const [done, setDone] = useState(false);

  const update = useCallback((patch: Partial<WizardDraft>) => setDraft((d) => ({ ...d, ...patch })), []);

  // Nach Schritt 1 (Session steht): gespeicherten Draft laden und lokalen State auffuellen.
  async function loadDraft() {
    const res = await api<SetupDraftResponse>("/api/setup/draft");
    setHasGitToken(res.hasGitToken);
    setHasAiApiKey(res.hasAiApiKey);
    setDraft((d) => ({
      ...d,
      publicBaseUrl: res.draft.publicBaseUrl ?? d.publicBaseUrl,
      platform: (res.draft.platform ?? d.platform) as WizardDraft["platform"],
      gitLabBaseUrl: res.draft.gitLabBaseUrl ?? d.gitLabBaseUrl,
      webhookSecret: res.draft.webhookSecret ?? d.webhookSecret,
      aiProvider: res.draft.aiProvider ?? d.aiProvider,
      aiModel: res.draft.aiModel ?? d.aiModel,
      aiEndpoint: res.draft.aiEndpoint ?? d.aiEndpoint,
      accessGateMode: (res.draft.accessGateMode ?? d.accessGateMode) as WizardDraft["accessGateMode"],
    }));
    setStep(1);
  }

  // Draft bei jedem Weiter speichern — leere Secrets bedeuten serverseitig "behalten".
  async function saveAndNext() {
    await api("/api/setup/draft", { method: "PUT", body: JSON.stringify(draft) });
    if (draft.gitToken !== "") setHasGitToken(true);
    if (draft.aiApiKey !== "") setHasAiApiKey(true);
    setStep((s) => s + 1);
  }

  async function apply() {
    setApplying(true);
    setApplyError(null);
    try {
      await api("/api/setup/draft", { method: "PUT", body: JSON.stringify(draft) });
      await api("/api/setup/apply", { method: "POST" });
    } catch {
      setApplyError("Apply failed — check the values (all required fields set?) and try again.");
      setApplying(false);
      return;
    }
    // Der Host startet in-process neu (~2 s): Status pollen, Fehler = "noch am Neustarten".
    const deadline = Date.now() + 90_000;
    while (Date.now() < deadline) {
      await new Promise((r) => setTimeout(r, 1500));
      try {
        const s = await api<SetupStatusDto>("/api/setup/status");
        if (!s.setupRequired) {
          setDone(true);
          setApplying(false);
          return;
        }
      } catch {
        /* Neustart laeuft noch */
      }
    }
    setApplyError("Restart timed out — reload the page and check the Settings.");
    setApplying(false);
  }

  const base = draft.publicBaseUrl.replace(/\/+$/, "");
  return (
    <div className="grid min-h-full place-items-center bg-[radial-gradient(130%_90%_at_50%_0%,rgba(74,222,128,.06),transparent_62%)] p-8">
      <div className="flex w-[560px] max-w-full flex-col">
        <div className="mb-6 flex items-center gap-3">
          <Logo size={40} />
          <div>
            <div className="font-mono text-lg font-bold text-white">naudit setup</div>
            <div className="font-mono text-[11px] text-ink3">
              {done ? "complete" : `step ${step + 1} of ${TITLES.length} · ${TITLES[step]}`}
            </div>
          </div>
        </div>

        <div className="rounded-2xl border border-hairline bg-surface p-6">
          {done ? (
            <div className="flex flex-col gap-4">
              <div className="font-mono text-[14px] font-bold text-acc">✓ Naudit is configured.</div>
              <p className="text-[12.5px] text-ink3">
                Reviews start as soon as your platform delivers webhooks to{" "}
                <span className="font-mono text-ink2">{`${base}/webhook/${draft.platform.toLowerCase()}`}</span>.
              </p>
              <Button onClick={() => window.location.reload()} className="w-full py-3">
                Open Naudit
              </Button>
            </div>
          ) : (
            <>
              {step === 0 && <StepAdmin adminExists={status.adminExists} onDone={() => void loadDraft()} />}
              {step === 1 && <StepInstance draft={draft} update={update} onNext={() => void saveAndNext()} />}
              {step === 2 && (
                <StepPlatform draft={draft} hasGitToken={hasGitToken} update={update} onNext={() => void saveAndNext()} />
              )}
              {step === 3 && (
                <StepAi draft={draft} hasAiApiKey={hasAiApiKey} update={update} onNext={() => void saveAndNext()} />
              )}
              {step === 4 && <StepAccess draft={draft} update={update} onNext={() => void saveAndNext()} />}
              {step === 5 && (
                <StepSummary
                  draft={draft}
                  hasGitToken={hasGitToken}
                  hasAiApiKey={hasAiApiKey}
                  applying={applying}
                  applyError={applyError}
                  onApply={() => void apply()}
                />
              )}
            </>
          )}
        </div>

        {!done && (
          <div className="mt-4 flex items-center justify-between font-mono text-[11.5px] text-ink3">
            {step > 1 && !applying ? (
              <button type="button" className="cursor-pointer hover:text-ink" onClick={() => setStep((s) => s - 1)}>
                ← back
              </button>
            ) : (
              <span />
            )}
            {status.adminExists && step > 0 && (
              <button type="button" className="cursor-pointer hover:text-ink" onClick={onBypass}>
                open settings instead →
              </button>
            )}
          </div>
        )}
      </div>
    </div>
  );
}

import { useState } from "react";
import { api, ApiError } from "@/api/client";
import type { GitHubManifestResponse } from "@/api/types";
import { Button } from "@/components/ui/Button";
import { CopyRow, Field, inputCls, randomSecret, type WizardDraft } from "./shared";

/** Schritt 3: Plattform-Wahl mit drei Karten — GitHub App (empfohlen, Ein-Klick-Manifest-Flow),
 *  GitHub PAT und GitLab. Die manuellen Pfade (PAT, GitLab-Copy-Paste) bleiben wie in PR 2; der
 *  App-Flow POSTet ein Manifest an GitHub, das per Redirect (?setup=…) zurueckkommt (siehe SetupWizard). */
export function StepPlatform({ draft, hasGitToken, hasGitHubApp, manifestError, update, save, onNext }: {
  draft: WizardDraft;
  hasGitToken: boolean;
  hasGitHubApp: boolean;
  manifestError: string | null;
  update: (patch: Partial<WizardDraft>) => void;
  save: () => Promise<void>;
  onNext: () => void;
}) {
  const base = draft.publicBaseUrl.replace(/\/+$/, "");

  // Aktuelle Wahl aus platform + gitHubAuth ableiten (GitLab hat Vorrang, dann App vs. PAT).
  const selected =
    draft.platform === "GitLab"
      ? "GitLab"
      : draft.gitHubAuth === "App"
        ? "GitHubApp"
        : draft.platform === "GitHub"
          ? "GitHubPat"
          : null;

  const pick = (key: "GitHubApp" | "GitHubPat" | "GitLab") => {
    if (key === "GitHubApp") update({ platform: "GitHub", gitHubAuth: "App" });
    else if (key === "GitHubPat")
      update({ platform: "GitHub", gitHubAuth: "Pat", webhookSecret: draft.webhookSecret || randomSecret() });
    else update({ platform: "GitLab", webhookSecret: draft.webhookSecret || randomSecret() });
  };

  const CHOICES = [
    { key: "GitHubApp", title: "GitHub App", sub: "Recommended — one-click setup" },
    { key: "GitHubPat", title: "GitHub PAT", sub: "Personal access token" },
    { key: "GitLab", title: "GitLab", sub: "Self-hosted or gitlab.com" },
  ] as const;

  const tokenOk = draft.gitToken !== "" || (hasGitToken && draft.platform !== "");
  const ready =
    selected === "GitHubApp"
      ? hasGitHubApp
      : selected === "GitHubPat"
        ? tokenOk
        : selected === "GitLab"
          ? tokenOk && /^https?:\/\/.+/.test(draft.gitLabBaseUrl)
          : false;

  const showManualWebhook = selected === "GitHubPat" || selected === "GitLab";

  return (
    <div className="flex flex-col gap-4">
      <div className="grid grid-cols-3 gap-2">
        {CHOICES.map((c) => (
          <button
            key={c.key}
            type="button"
            onClick={() => pick(c.key)}
            className={`cursor-pointer rounded-xl border px-3 py-3 text-left font-mono text-[13px] ${
              selected === c.key ? "border-acc text-ink" : "border-border text-ink2 hover:border-ink3"
            }`}
          >
            <div className="font-bold">{c.title}</div>
            <div className="mt-1 text-[11px] text-ink3">{c.sub}</div>
          </button>
        ))}
      </div>

      {selected === "GitHubApp" && (
        <GitHubAppPane
          draft={draft}
          hasGitHubApp={hasGitHubApp}
          manifestError={manifestError}
          update={update}
          save={save}
        />
      )}

      {selected === "GitLab" && (
        <Field label="GitLab base URL">
          <input
            className={inputCls}
            placeholder="https://gitlab.example.com"
            value={draft.gitLabBaseUrl}
            onChange={(e) => update({ gitLabBaseUrl: e.target.value })}
          />
        </Field>
      )}

      {(selected === "GitHubPat" || selected === "GitLab") && (
        <Field
          label={selected === "GitHubPat" ? "GitHub personal access token" : "GitLab access token (api scope)"}
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

      {showManualWebhook && (
        <div className="flex flex-col gap-2">
          <div className="text-[12.5px] font-medium text-ink2">
            Add this webhook in{" "}
            {selected === "GitHubPat"
              ? "your repository settings (Webhooks → Add webhook, event: pull requests)"
              : "your project settings (Webhooks, trigger: merge request events)"}
            :
          </div>
          <CopyRow label="Webhook URL" value={`${base}/webhook/${draft.platform.toLowerCase()}`} />
          <CopyRow label={selected === "GitHubPat" ? "Webhook secret" : "Secret token"} value={draft.webhookSecret} />
        </div>
      )}

      <Button onClick={onNext} disabled={!ready} className="w-full py-3">
        Continue
      </Button>
    </div>
  );
}

/** github.com bleibt Default; GHES-Host getrimmt, ohne Slash am Ende (analog GitHubManifest.Normalize). */
function normalizeHost(host: string): string {
  return host.trim() === "" ? "https://github.com" : host.trim().replace(/\/+$/, "");
}

/** App-Pane: entweder Manifest-Formular (App noch nicht erstellt) oder Erfolgs-/Install-Box. */
function GitHubAppPane({ draft, hasGitHubApp, manifestError, update, save }: {
  draft: WizardDraft;
  hasGitHubApp: boolean;
  manifestError: string | null;
  update: (patch: Partial<WizardDraft>) => void;
  save: () => Promise<void>;
}) {
  const [appName, setAppName] = useState("naudit");
  const [org, setOrg] = useState("");
  const [isPublic, setIsPublic] = useState(false);
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);

  // Manifest anfordern (Draft zuvor sichern — der Browser verlaesst gleich die Seite), dann per
  // echtem Form-POST an GitHub schicken. GitHub redirected mit ?setup=github-app-… zurueck.
  async function createApp() {
    setBusy(true);
    setErr(null);
    try {
      await save();
      const res = await api<GitHubManifestResponse>("/api/setup/github/manifest", {
        method: "POST",
        body: JSON.stringify({
          gitHubHost: draft.gitHubHost || null,
          org: org || null,
          appName,
          public: isPublic,
        }),
      });
      const form = document.createElement("form");
      form.method = "post";
      form.action = res.action;
      const input = document.createElement("input");
      input.type = "hidden";
      input.name = "manifest";
      input.value = JSON.stringify(res.manifest);
      form.appendChild(input);
      document.body.appendChild(form);
      form.submit(); // Rueckkehr via /?setup=github-app-…
    } catch (e) {
      setErr(e instanceof ApiError ? e.message : "Could not start the GitHub app flow.");
      setBusy(false);
    }
  }

  if (hasGitHubApp) {
    const installUrl = `${normalizeHost(draft.gitHubHost)}/apps/${draft.gitHubAppSlug}/installations/new`;
    return (
      <div className="flex flex-col gap-3 rounded-xl border border-acc/40 bg-acc/10 px-5 py-4">
        <div className="font-mono text-[13px] font-bold text-acc">
          ✓ GitHub App created{draft.gitHubAppId ? ` (app ID ${draft.gitHubAppId})` : ""}
        </div>
        <p className="text-[12px] leading-relaxed text-ink2">
          Webhook URL and secret were configured automatically by GitHub. Install the app on the accounts or
          organisations whose repositories Naudit should review.
        </p>
        <a
          href={installUrl}
          target="_blank"
          rel="noreferrer"
          className="w-fit shrink-0 cursor-pointer rounded-lg bg-acc px-4 py-2 text-sm font-bold text-accink transition-colors hover:bg-acc2 focus-visible:outline-2 focus-visible:outline-solid focus-visible:outline-offset-2 focus-visible:outline-teal"
        >
          Install the app on GitHub →
        </a>
        <button
          type="button"
          disabled={busy}
          onClick={() => void createApp()}
          className="w-fit cursor-pointer font-mono text-[11.5px] text-ink3 hover:text-ink disabled:cursor-not-allowed disabled:opacity-60"
        >
          {busy ? "starting…" : "Create a different app"}
        </button>
        {err && <div className="font-mono text-xs text-danger">{err}</div>}
      </div>
    );
  }

  return (
    <div className="flex flex-col gap-3">
      <Field label="App name" hint="App names are unique on GitHub — you can adjust it on the GitHub page before creating.">
        <input className={inputCls} value={appName} onChange={(e) => setAppName(e.target.value)} />
      </Field>
      <Field label="Organisation (optional)" hint="Leave empty to create the app on your personal account.">
        <input
          className={inputCls}
          placeholder="my-org"
          value={org}
          onChange={(e) => setOrg(e.target.value)}
        />
      </Field>
      <Field label="GitHub Enterprise host (optional)">
        <input
          className={inputCls}
          placeholder="https://github.com"
          value={draft.gitHubHost}
          onChange={(e) => update({ gitHubHost: e.target.value })}
        />
      </Field>
      <label className="flex cursor-pointer items-center gap-2 text-[12.5px] text-ink2">
        <input type="checkbox" checked={isPublic} onChange={(e) => setIsPublic(e.target.checked)} />
        Public app — others can install it
      </label>
      {(err || manifestError) && <div className="font-mono text-xs text-danger">{err ?? manifestError}</div>}
      <Button
        onClick={() => void createApp()}
        disabled={busy || draft.publicBaseUrl === ""}
        className="w-full py-3"
      >
        {busy ? "starting…" : "Create GitHub App on GitHub →"}
      </Button>
    </div>
  );
}

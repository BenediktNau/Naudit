import { useState, type ReactNode } from "react";

/** Wizard-interner Draft-State: immer Strings ("" = leer). Secrets bleiben leer, wenn der
 *  Server sie bereits hat (has*-Flags) — leer senden heisst "gespeicherten Wert behalten". */
export interface WizardDraft {
  publicBaseUrl: string;
  platform: "" | "GitHub" | "GitLab";
  gitToken: string;
  gitLabBaseUrl: string;
  webhookSecret: string;
  aiProvider: string;
  aiModel: string;
  aiEndpoint: string;
  aiApiKey: string;
  accessGateMode: "Open" | "Registered";
  // GitHub-Auth-Wahl (PAT vs. App). AppId/Slug sind reine Anzeige-Felder aus dem GET;
  // das PUT schickt sie mit, der Server ignoriert sie (serverseitig ueber den Manifest-Callback verwaltet).
  gitHubAuth: "" | "Pat" | "App";
  gitHubHost: string;
  gitHubAppId: string;
  gitHubAppSlug: string;
}

export const emptyDraft: WizardDraft = {
  publicBaseUrl: "",
  platform: "",
  gitToken: "",
  gitLabBaseUrl: "",
  webhookSecret: "",
  aiProvider: "Ollama",
  aiModel: "",
  aiEndpoint: "",
  aiApiKey: "",
  accessGateMode: "Open",
  gitHubAuth: "",
  gitHubHost: "",
  gitHubAppId: "",
  gitHubAppSlug: "",
};

export const inputCls =
  "w-full rounded-lg border border-border bg-bg px-4 py-3 font-mono text-[13.5px] text-ink outline-none placeholder:text-ink3 focus:border-acc";

/** 32 Zufallsbytes als Hex — das Webhook-Secret, das der Nutzer in GitLab/GitHub eintraegt. */
export function randomSecret(): string {
  const bytes = new Uint8Array(32);
  crypto.getRandomValues(bytes);
  return Array.from(bytes, (b) => b.toString(16).padStart(2, "0")).join("");
}

export function Field({ label, hint, children }: { label: string; hint?: string; children: ReactNode }) {
  return (
    <label className="flex flex-col gap-1.5">
      <span className="text-[12.5px] font-medium text-ink2">{label}</span>
      {children}
      {hint && <span className="text-[11.5px] text-ink3">{hint}</span>}
    </label>
  );
}

/** Kopierbarer Wert (Webhook-URL/-Secret) mit Feedback am Button. */
export function CopyRow({ label, value }: { label: string; value: string }) {
  const [copied, setCopied] = useState(false);
  return (
    <div className="flex items-center justify-between gap-3 rounded-lg border border-hairline bg-elev px-3 py-2">
      <div className="min-w-0">
        <div className="text-[11px] text-ink3">{label}</div>
        <div className="truncate font-mono text-[12.5px] text-ink">{value}</div>
      </div>
      <button
        type="button"
        className="shrink-0 cursor-pointer rounded border border-border px-2 py-1 font-mono text-[11px] text-ink2 hover:border-ink3"
        onClick={() => {
          void navigator.clipboard.writeText(value);
          setCopied(true);
          setTimeout(() => setCopied(false), 1500);
        }}
      >
        {copied ? "copied ✓" : "copy"}
      </button>
    </div>
  );
}

import { useState } from "react";
import { api } from "@/api/client";
import { Button } from "@/components/ui/Button";
import { Field, inputCls, type WizardDraft } from "./shared";

const PROVIDERS = ["Ollama", "Anthropic", "OpenAICompatible", "ClaudeCode"] as const;

/** Schritt 4: Provider + bedingte Felder. "Test connection" ist optional — ein Fehlschlag
 *  blockiert nicht (Spec: Ollama ist z. B. oft erst spaeter erreichbar). */
export function StepAi({ draft, hasAiApiKey, update, onNext }: {
  draft: WizardDraft;
  hasAiApiKey: boolean;
  update: (patch: Partial<WizardDraft>) => void;
  onNext: () => void;
}) {
  const [test, setTest] = useState<{ ok: boolean; detail: string } | null>(null);
  const [testing, setTesting] = useState(false);

  const needsKey = draft.aiProvider === "Anthropic" || draft.aiProvider === "OpenAICompatible";
  const showsEndpoint = draft.aiProvider === "Ollama" || draft.aiProvider === "OpenAICompatible";
  const keyOk = !needsKey || draft.aiApiKey !== "" || hasAiApiKey;
  const modelOk = draft.aiProvider === "ClaudeCode" || draft.aiModel !== "";
  const ready = modelOk && keyOk;

  async function runTest() {
    setTesting(true);
    setTest(null);
    try {
      const res = await api<{ ok: boolean; detail: string }>("/api/setup/test-ai", {
        method: "POST",
        body: JSON.stringify({
          provider: draft.aiProvider,
          model: draft.aiModel,
          endpoint: draft.aiEndpoint || null,
          apiKey: draft.aiApiKey || null, // leer ⇒ Server nimmt den gespeicherten Draft-Key
        }),
      });
      setTest(res);
    } catch {
      setTest({ ok: false, detail: "Request failed — is the server reachable?" });
    } finally {
      setTesting(false);
    }
  }

  return (
    <div className="flex flex-col gap-4">
      <Field label="Provider">
        <select
          className={inputCls}
          value={draft.aiProvider}
          onChange={(e) => { update({ aiProvider: e.target.value }); setTest(null); }}
        >
          {PROVIDERS.map((p) => <option key={p} value={p}>{p}</option>)}
        </select>
      </Field>
      <Field
        label="Model"
        hint={draft.aiProvider === "ClaudeCode" ? "Optional — the CLI defaults to \"sonnet\"." : undefined}
      >
        <input
          className={inputCls}
          placeholder={draft.aiProvider === "ClaudeCode" ? "sonnet" : "e.g. claude-sonnet-5, qwen3.5"}
          value={draft.aiModel}
          onChange={(e) => update({ aiModel: e.target.value })}
        />
      </Field>
      {showsEndpoint && (
        <Field label="Endpoint" hint="Optional — defaults to a sensible endpoint for the provider.">
          <input
            className={inputCls}
            placeholder={draft.aiProvider === "Ollama" ? "http://localhost:11434" : "https://api.openai.com/v1"}
            value={draft.aiEndpoint}
            onChange={(e) => update({ aiEndpoint: e.target.value })}
          />
        </Field>
      )}
      {needsKey && (
        <Field
          label="API key"
          hint={hasAiApiKey && draft.aiApiKey === "" ? "A key is already stored — leave empty to keep it." : undefined}
        >
          <input
            className={inputCls}
            type="password"
            placeholder={hasAiApiKey ? "•••••• (stored)" : ""}
            value={draft.aiApiKey}
            onChange={(e) => update({ aiApiKey: e.target.value })}
          />
        </Field>
      )}

      <div className="flex items-center gap-3">
        <Button variant="secondary" onClick={() => void runTest()} disabled={testing || !ready} className="px-3 py-2 text-[12.5px]">
          {testing ? "testing…" : "Test connection"}
        </Button>
        {test && (
          <span className={`font-mono text-[11.5px] ${test.ok ? "text-acc" : "text-warn"}`}>
            {test.ok ? "✓ connection works" : `⚠ ${test.detail}`}
          </span>
        )}
      </div>
      {test && !test.ok && (
        <p className="text-[11.5px] text-ink3">You can continue anyway and fix the AI settings later.</p>
      )}

      <Button onClick={onNext} disabled={!ready} className="w-full py-3">
        Continue
      </Button>
    </div>
  );
}

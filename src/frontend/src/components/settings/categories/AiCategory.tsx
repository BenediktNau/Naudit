import { Panel } from "@/components/ui/Panel";
import { Field } from "@/components/setup/shared";
import { SelectableCard } from "../primitives";
import type { SettingsCtx } from "../model";

const PROVIDERS: { id: string; title: string; tag: string; desc: string }[] = [
  { id: "Ollama", title: "Ollama", tag: "local", desc: "Self-hosted models. No API key, runs on your endpoint." },
  { id: "Anthropic", title: "Anthropic", tag: "API key", desc: "Claude via the Anthropic API." },
  { id: "OpenAICompatible", title: "OpenAI-compatible", tag: "API key + URL", desc: "Any OpenAI-style endpoint (NVIDIA, vLLM, …)." },
  { id: "ClaudeCode", title: "Claude Code", tag: "subscription", desc: "Uses the Claude Code CLI signed in on the server." },
];

const inputCls =
  "w-[280px] rounded-lg border border-border bg-bg px-3 py-2 font-mono text-[13px] text-ink outline-none placeholder:text-ink3 focus:border-acc disabled:opacity-50";

export function AiCategory({ ctx }: { ctx: SettingsCtx }) {
  const provider = ctx.get("Naudit:Ai:Provider") || "Ollama";
  const needsEndpoint = provider === "Ollama" || provider === "OpenAICompatible";
  const needsKey = provider === "Anthropic" || provider === "OpenAICompatible";
  const shown = 1 + (needsEndpoint ? 1 : 0) + (needsKey ? 1 : 0); // Model + Extras
  const meta = PROVIDERS.find((p) => p.id === provider) ?? PROVIDERS[0];

  return (
    <>
      <div className="grid grid-cols-2 gap-3">
        {PROVIDERS.map((p) => {
          const sel = provider === p.id;
          return (
            <SelectableCard key={p.id} selected={sel} onClick={() => ctx.set("Naudit:Ai:Provider", p.id)}
              disabled={ctx.locked("Naudit:Ai:Provider")}>
              <div className="flex items-center justify-between">
                <b className="text-[14px]">{p.title}</b>
                <span className={`font-mono text-[11px] ${sel ? "font-bold text-acc" : "text-ink3"}`}>
                  {sel ? "✓ selected" : p.tag}
                </span>
              </div>
              <p className="text-[12.5px] leading-relaxed text-ink2">{p.desc}</p>
            </SelectableCard>
          );
        })}
      </div>

      <Panel title={`${meta.title} settings`} extra={`${shown} of ${shown} fields shown`}>
        <div className="flex flex-col gap-4 px-5 py-4">
          <Field label="Model" hint={provider === "ClaudeCode" ? "Optional — defaults to sonnet." : "Required."}>
            <input className={inputCls} value={ctx.get("Naudit:Ai:Model")}
              placeholder={provider === "ClaudeCode" ? "sonnet" : ""}
              disabled={ctx.locked("Naudit:Ai:Model")}
              onChange={(e) => ctx.set("Naudit:Ai:Model", e.target.value)} />
          </Field>
          {needsEndpoint && (
            <Field label="Endpoint" hint={provider === "Ollama" ? "Optional — defaults to http://localhost:11434." : "Required."}>
              <input className={inputCls} value={ctx.get("Naudit:Ai:Endpoint")}
                placeholder={provider === "Ollama" ? "http://localhost:11434" : ""}
                disabled={ctx.locked("Naudit:Ai:Endpoint")}
                onChange={(e) => ctx.set("Naudit:Ai:Endpoint", e.target.value)} />
            </Field>
          )}
          {needsKey && (
            <Field label="API key" hint="Required — stored encrypted, never shown again.">
              <input type="password" className={inputCls}
                placeholder={ctx.secretSet("Naudit:Ai:ApiKey") ? "•••••• (set)" : "not set"}
                disabled={ctx.locked("Naudit:Ai:ApiKey")}
                value={ctx.get("Naudit:Ai:ApiKey")}
                onChange={(e) => ctx.set("Naudit:Ai:ApiKey", e.target.value)} />
            </Field>
          )}
          {provider === "ClaudeCode" && (
            <div className="rounded-lg border border-border bg-elev px-4 py-3 text-[12.5px] text-ink2">
              Claude Code signs in with the subscription already configured on the server — there is
              no endpoint or API key to manage here.
            </div>
          )}
        </div>
      </Panel>

      <div className="flex items-center gap-2 text-[12px] text-ink3">
        <span className="inline-block size-2 rounded-full bg-warn" aria-hidden="true" />
        Changes apply after a restart — you'll be prompted when you save.
      </div>
    </>
  );
}

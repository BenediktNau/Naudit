import { Panel } from "@/components/ui/Panel";
import { Field } from "@/components/setup/shared";
import type { SettingsCtx } from "../../model";

export function PromptPanel({ ctx }: { ctx: SettingsCtx }) {
  const prompt = ctx.get("Naudit:Review:SystemPrompt");
  return (
    <Panel title="Review prompt" extra={prompt ? "custom" : "built-in default"}>
      <div className="px-5 py-4">
        <Field label="System prompt" hint="Clearing the field goes back to the built-in prompt.">
          <textarea rows={4} disabled={ctx.locked("Naudit:Review:SystemPrompt")}
            className="min-h-[88px] w-full rounded-lg border border-border bg-bg px-3 py-2 font-mono text-[13px] text-ink outline-none placeholder:text-ink3 focus:border-acc disabled:opacity-50"
            placeholder="Using the built-in review prompt. Write your own here to override it."
            value={prompt} onChange={(e) => ctx.set("Naudit:Review:SystemPrompt", e.target.value)} />
        </Field>
      </div>
    </Panel>
  );
}

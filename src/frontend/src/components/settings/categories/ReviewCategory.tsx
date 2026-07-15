import { Panel } from "@/components/ui/Panel";
import { Field } from "@/components/setup/shared";
import type { SettingsCtx } from "../model";

const SEV = ["Info", "Low", "Medium", "High", "Critical"];
const CONF = ["Low", "Medium", "High"];
const selCls = "w-[220px] rounded-lg border border-border bg-bg px-3 py-2 font-mono text-[13px] text-ink outline-none focus:border-acc disabled:opacity-50";

export function ReviewCategory({ ctx }: { ctx: SettingsCtx }) {
  const sev = ctx.get("Naudit:Review:Gate:MinSeverity") || "High";
  const conf = ctx.get("Naudit:Review:Gate:MinConfidence") || "Medium";
  const prompt = ctx.get("Naudit:Review:SystemPrompt");
  const roundtrips = ctx.get("Naudit:Review:MaxRoundtrips");

  return (
    <>
      <Panel title="Merge gate">
        <div className="flex flex-col gap-4 px-5 py-4">
          <div className="flex flex-wrap gap-4">
            <Field label="Minimum severity" hint="Findings below this never block.">
              <select className={selCls} value={sev} disabled={ctx.locked("Naudit:Review:Gate:MinSeverity")}
                onChange={(e) => ctx.set("Naudit:Review:Gate:MinSeverity", e.target.value)}>
                {SEV.map((s) => <option key={s} value={s}>{s}</option>)}
              </select>
            </Field>
            <Field label="Minimum confidence" hint="How sure the AI must be.">
              <select className={selCls} value={conf} disabled={ctx.locked("Naudit:Review:Gate:MinConfidence")}
                onChange={(e) => ctx.set("Naudit:Review:Gate:MinConfidence", e.target.value)}>
                {CONF.map((c) => <option key={c} value={c}>{c}</option>)}
              </select>
            </Field>
          </div>
          <div className="rounded-lg border border-border bg-elev px-4 py-3 text-[12.5px] text-ink2">
            With these rules, a merge is blocked when a finding is <b className="text-ink">{sev}</b> or
            worse and the AI is at least <b className="text-ink">{conf}</b> confident. Everything else
            becomes a non-blocking comment.
          </div>
        </div>
      </Panel>

      <Panel title="Roundtrip limit">
        <div className="px-5 py-4">
          <Field label="Max automatic reviews per PR"
            hint="Further pushes are skipped after this many reviews. 0 = unlimited. CI-triggered reviews (POST /review) are never limited.">
            <input type="number" min={0} placeholder="3 (default)"
              disabled={ctx.locked("Naudit:Review:MaxRoundtrips")}
              className={selCls} value={roundtrips}
              onChange={(e) => ctx.set("Naudit:Review:MaxRoundtrips", e.target.value)} />
          </Field>
        </div>
      </Panel>

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
    </>
  );
}

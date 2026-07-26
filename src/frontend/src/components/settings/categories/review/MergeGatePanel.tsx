import { Panel } from "@/components/ui/Panel";
import { Field } from "@/components/setup/shared";
import type { SettingsCtx } from "../../model";
import { selCls } from "./shared";

const SEV = ["Info", "Low", "Medium", "High", "Critical"];
const CONF = ["Low", "Medium", "High"];

export function MergeGatePanel({ ctx }: { ctx: SettingsCtx }) {
  const sev = ctx.get("Naudit:Review:Gate:MinSeverity") || "High";
  const conf = ctx.get("Naudit:Review:Gate:MinConfidence") || "Medium";

  return (
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
  );
}

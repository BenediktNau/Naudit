import { Panel } from "@/components/ui/Panel";
import { Field } from "@/components/setup/shared";
import type { SettingsCtx } from "../../model";
import { selCls } from "./shared";

export function RoundtripPanel({ ctx }: { ctx: SettingsCtx }) {
  const roundtrips = ctx.get("Naudit:Review:MaxRoundtrips");
  return (
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
  );
}

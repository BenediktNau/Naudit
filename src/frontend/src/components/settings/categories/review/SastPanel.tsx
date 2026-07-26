import { Panel } from "@/components/ui/Panel";
import { Toggle } from "../../primitives";
import type { SettingsCtx } from "../../model";

const KEY_ENABLED = "Naudit:Sast:Enabled";
const KEY_ANALYZERS = "Naudit:Sast:Analyzers";
/** Fällt der Key weg, registriert die DI genau diese zwei — die UI zeigt das, statt zu luegen. */
const DEFAULTS = ["opengrep", "trivy"];

const csv = (value: string) => value.split(",").map((s) => s.trim()).filter(Boolean);

export function SastPanel({ ctx }: { ctx: SettingsCtx }) {
  const on = ctx.get(KEY_ENABLED) === "true";
  const chosen = csv(ctx.get(KEY_ANALYZERS));
  const isDefault = chosen.length === 0;
  const selected = isDefault ? DEFAULTS : chosen;
  const locked = ctx.locked(KEY_ANALYZERS);

  const toggleAnalyzer = (name: string) => {
    const next = selected.includes(name) ? selected.filter((s) => s !== name) : [...selected, name];
    // Leer heisst "Key entfernen" ⇒ Defaults. "An, aber kein Tool" gibt es nicht.
    ctx.set(KEY_ANALYZERS, next.join(","));
  };

  return (
    <Panel title="Static analysis (SAST)" extra={on ? "on" : "off"}>
      <div className="flex flex-col gap-4 px-5 py-4">
        <div className="flex items-center justify-between gap-4">
          <div>
            <div className="text-[13px] font-medium text-ink">Scan the diff with static analyzers</div>
            <p className="mt-0.5 text-[12.5px] text-ink2">
              Findings are added to the prompt as grounding. They never block a merge on their own.
            </p>
          </div>
          <Toggle on={on} disabled={ctx.locked(KEY_ENABLED)} aria-label="Enable SAST"
            onChange={(v) => ctx.set(KEY_ENABLED, String(v))} />
        </div>

        <div className="flex flex-col gap-2">
          <div className="flex items-center gap-2">
            <span className="text-[13px] font-medium text-ink">Analyzers</span>
            {isDefault && <span className="font-mono text-[11px] text-ink3">default</span>}
          </div>
          <div className="flex flex-wrap gap-2">
            {ctx.options(KEY_ANALYZERS).map((name) => (
              <label key={name}
                className={`flex items-center gap-2 rounded-lg border px-3 py-2 font-mono text-[12.5px] ${
                  selected.includes(name) ? "border-acc bg-acc/6 text-ink" : "border-border text-ink2"
                } ${locked || !on ? "opacity-50" : "cursor-pointer"}`}>
                <input type="checkbox" checked={selected.includes(name)} disabled={locked || !on}
                  onChange={() => toggleAnalyzer(name)} />
                {name}
              </label>
            ))}
          </div>
          <p className="text-[12.5px] text-ink2">
            Unchecking everything falls back to the defaults ({DEFAULTS.join(", ")}). To run no
            analysis at all, switch SAST off.
          </p>
        </div>
      </div>
    </Panel>
  );
}

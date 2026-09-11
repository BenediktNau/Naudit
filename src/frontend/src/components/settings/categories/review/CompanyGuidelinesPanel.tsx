import { Panel } from "@/components/ui/Panel";
import { Field } from "@/components/setup/shared";
import type { SettingsCtx } from "../../model";

const KEY = "Naudit:Review:CompanyGuidelines";

/** Firmenweite Coding-Guidelines als Freitext: landen bei jedem Review als eigene, autoritative
 *  Prompt-Sektion (vor dem Projektprofil). Der Zeichen-Deckel kommt aus dem Katalog — dieselbe
 *  Grenze, gegen die die API beim Speichern prüft. */
export function CompanyGuidelinesPanel({ ctx }: { ctx: SettingsCtx }) {
  const text = ctx.get(KEY);
  const max = ctx.maxLength(KEY);
  const over = max !== null && text.length > max;
  return (
    <Panel title="Company coding guidelines" extra={text.trim() ? "active" : "none"}>
      <div className="px-5 py-4">
        <Field label="Guidelines (Markdown or plain text)"
          hint="Organization-wide rules the reviewer treats as authoritative in every project — naming, logging, error handling, forbidden APIs. Violations are reported as findings; severity follows the actual impact, so style rules do not block a merge.">
          <textarea rows={12} disabled={ctx.locked(KEY)}
            className={`min-h-[200px] w-full rounded-lg border bg-bg px-3 py-2 font-mono text-[13px] text-ink outline-none placeholder:text-ink3 focus:border-acc disabled:opacity-50 ${over ? "border-danger" : "border-border"}`}
            placeholder={"- Public methods need XML doc comments.\n- Never log personal data (names, e-mail addresses).\n- Use the shared HttpClient factory, never new HttpClient()."}
            value={text} onChange={(e) => ctx.set(KEY, e.target.value)} />
        </Field>
        {max !== null && (
          <div className={`mt-1.5 text-right text-[11.5px] tabular-nums ${over ? "text-danger" : "text-ink3"}`}>
            {text.length.toLocaleString()} / {max.toLocaleString()} characters{over ? " — too long, saving will be rejected" : ""}
          </div>
        )}
      </div>
    </Panel>
  );
}

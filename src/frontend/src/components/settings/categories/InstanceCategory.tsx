import { Panel } from "@/components/ui/Panel";
import { Field, CopyRow } from "@/components/setup/shared";
import { SelectableCard } from "../primitives";
import type { SettingsCtx } from "../model";

export function InstanceCategory({ ctx }: { ctx: SettingsCtx }) {
  const base = ctx.get("Naudit:PublicBaseUrl").replace(/\/+$/, "");
  const platform = (ctx.get("Naudit:Git:Platform") || "GitLab").toLowerCase();
  const mode = ctx.get("Naudit:AccessGate:Mode") || "Open";

  return (
    <>
      <Panel title="Address">
        <div className="flex flex-col gap-4 px-5 py-4">
          <Field label="Public base URL" hint="Used to build the webhook and sign-in callback URLs below.">
            <input
              className="w-[360px] max-w-full rounded-lg border border-border bg-bg px-3 py-2 font-mono text-[13px] text-ink outline-none placeholder:text-ink3 focus:border-acc disabled:opacity-50"
              placeholder="https://naudit.example.com" disabled={ctx.locked("Naudit:PublicBaseUrl")}
              value={ctx.get("Naudit:PublicBaseUrl")} onChange={(e) => ctx.set("Naudit:PublicBaseUrl", e.target.value)} />
          </Field>
          {base && (
            <div className="max-w-[520px]">
              <CopyRow label="Webhook URL — paste into your repo's webhook settings" value={`${base}/webhook/${platform}`} />
            </div>
          )}
        </div>
      </Panel>

      <Panel title="Who gets reviews">
        <div className="grid grid-cols-1 gap-3 px-5 py-4 md:grid-cols-2">
          {([
            { id: "Open", title: "Open", desc: "Any repo that knows the webhook secret gets reviews." },
            { id: "Registered", title: "Registered only", desc: "Only repos belonging to approved dashboard accounts. Everyone else is ignored." },
          ] as const).map((o) => (
            <SelectableCard key={o.id} selected={mode === o.id} disabled={ctx.locked("Naudit:AccessGate:Mode")}
              onClick={() => ctx.set("Naudit:AccessGate:Mode", o.id)}>
              <b className="text-[13.5px]">{o.title}</b>
              <p className="text-[12.5px] text-ink2">{o.desc}</p>
            </SelectableCard>
          ))}
        </div>
      </Panel>
    </>
  );
}

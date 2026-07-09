import { useState } from "react";
import type { SettingItem } from "@/api/types";
import { Pill } from "@/components/ui/Pill";
import type { SettingsCtx } from "./model";

/** Enum-Keys als Select statt Freitext (aus der alten SettingsPage uebernommen). */
const ENUMS: Record<string, string[]> = {
  "Naudit:Git:Platform": ["GitLab", "GitHub"],
  "Naudit:GitHub:Auth": ["Pat", "App"],
  "Naudit:Ai:Provider": ["Ollama", "Anthropic", "OpenAICompatible", "ClaudeCode"],
  "Naudit:AccessGate:Mode": ["Open", "Registered"],
  "Naudit:Review:Gate:MinSeverity": ["Info", "Low", "Medium", "High", "Critical"],
  "Naudit:Review:Gate:MinConfidence": ["Low", "Medium", "High"],
  "Naudit:Ui:Auth:GitHub:Enabled": ["false", "true"],
  "Naudit:Ui:Auth:Oidc:Enabled": ["false", "true"],
};

function RawRow({ item, ctx }: { item: SettingItem; ctx: SettingsCtx }) {
  const label = item.key.replace(/^Naudit:/, "");
  const options = ENUMS[item.key];
  return (
    <div className="flex items-center justify-between gap-4 border-b border-hairline px-5 py-3 last:border-b-0">
      <span className="flex items-center gap-2 text-[13px] font-medium text-ink">
        {label}
        {item.source === "env" && <Pill kind="neutral">via environment</Pill>}
        {item.source === "db" && <Pill kind="ok">db</Pill>}
      </span>
      {!item.editable ? (
        <span className="font-mono text-[12.5px] text-ink3">{item.isSecret ? "•••" : item.value ?? "—"}</span>
      ) : options ? (
        <select
          className="w-[300px] rounded border border-hairline bg-transparent px-2 py-1 font-mono text-[12.5px] text-ink2"
          value={ctx.get(item.key)} onChange={(e) => ctx.set(item.key, e.target.value)}
        >
          <option value="">(default)</option>
          {options.map((o) => <option key={o} value={o}>{o}</option>)}
        </select>
      ) : (
        <input
          type={item.isSecret ? "password" : "text"}
          className="w-[300px] rounded border border-hairline bg-transparent px-2 py-1 font-mono text-[12.5px] text-ink2"
          placeholder={item.isSecret ? (item.isSet ? "•••••• (set)" : "not set") : ""}
          value={ctx.get(item.key)} onChange={(e) => ctx.set(item.key, e.target.value)}
        />
      )}
    </div>
  );
}

/** Expertenansicht: flacher Katalog, gefiltert. Editiert denselben drafts-State wie die Kategorien. */
export function RawKeys({ items, ctx }: { items: SettingItem[]; ctx: SettingsCtx }) {
  const [filter, setFilter] = useState("");
  const shown = items.filter((i) => i.key.toLowerCase().includes(filter.toLowerCase()));
  return (
    <div className="flex flex-col gap-4 anim-fadein">
      <div className="flex items-start justify-between gap-4">
        <div>
          <h2 className="font-mono text-[18px] font-bold">Raw keys</h2>
          <p className="mt-1 max-w-[56ch] text-[13px] text-ink2">
            Every DB-managed key, exactly as in the config. Keys set via environment are locked —
            environment always wins.
          </p>
        </div>
        <input
          value={filter} onChange={(e) => setFilter(e.target.value)} placeholder="⌕ filter keys…"
          className="w-[240px] rounded-lg border border-border bg-bg px-3 py-2 font-mono text-[12.5px] text-ink outline-none placeholder:text-ink3 focus:border-acc"
        />
      </div>
      <p className="font-mono text-[11px] text-ink3">Showing all {items.length} DB-managed keys</p>
      <div className="overflow-hidden rounded-xl border border-hairline bg-surface">
        {shown.map((i) => <RawRow key={i.key} item={i} ctx={ctx} />)}
      </div>
    </div>
  );
}

import { Toggle, StatusHint } from "./primitives";
import { CATEGORIES, type CategoryId } from "./model";

export function SettingsSidebar({ active, onSelect, rawMode, onToggleRaw, hints }: {
  active: CategoryId;
  onSelect: (c: CategoryId) => void;
  rawMode: boolean;
  onToggleRaw: (v: boolean) => void;
  hints: Record<CategoryId, { tone: "acc" | "ink3" | "warn"; text: string }>;
}) {
  return (
    <aside className="flex w-[230px] shrink-0 flex-col justify-between border-r border-hairline px-[14px] py-5">
      <div>
        <div className="mb-2 px-3 font-mono text-[11px] font-bold uppercase tracking-[0.12em] text-ink3">Settings</div>
        <nav className="flex flex-col gap-0.5">
          {CATEGORIES.map((c) => {
            const on = active === c.id && !rawMode;
            return (
              <button
                key={c.id} type="button" onClick={() => onSelect(c.id)}
                className={`flex items-center justify-between rounded-lg px-3 py-2.5 text-[13px] transition-colors ${
                  on ? "bg-acc/12 font-semibold text-acc"
                     : `font-medium ${rawMode ? "text-ink3" : "text-ink2 hover:text-ink"}`
                }`}
              >
                <span>{c.label}</span>
                <StatusHint tone={hints[c.id].tone}>{hints[c.id].text}</StatusHint>
              </button>
            );
          })}
        </nav>
      </div>
      <div className="mt-4 border-t border-hairline px-3 pt-4">
        <div className="flex items-center justify-between">
          <span className="text-[13px] font-medium text-ink2">Raw keys</span>
          <Toggle on={rawMode} onChange={onToggleRaw} />
        </div>
        <p className="mt-1 text-[11px] text-ink3">Show every setting as its config key</p>
      </div>
    </aside>
  );
}

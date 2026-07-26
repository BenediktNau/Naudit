import { Toggle, StatusHint } from "./primitives";
import { CATEGORIES, type CategoryId } from "./model";

/** Kategorie-Spalte der Settings. Ohne Trennlinie zum Inhalt — die Einträge stehen frei
 *  neben dem Panel, so wie die Hauptnavigation neben dem Seiteninhalt steht. */
export function SettingsSidebar({
  active,
  onSelect,
  rawMode,
  onToggleRaw,
  hints,
}: {
  active: CategoryId;
  onSelect: (c: CategoryId) => void;
  rawMode: boolean;
  onToggleRaw: (v: boolean) => void;
  hints: Record<CategoryId, { tone: "acc" | "ink3" | "warn"; text: string }>;
}) {
  return (
    <aside className="flex shrink-0 basis-[200px] flex-col gap-0.5 lg:sticky lg:top-0">
      {CATEGORIES.map((c) => {
        const on = active === c.id && !rawMode;
        return (
          <button
            key={c.id}
            type="button"
            onClick={() => {
              onToggleRaw(false);
              onSelect(c.id);
            }}
            aria-current={on ? "true" : undefined}
            className={`flex items-center justify-between gap-2 rounded-[9px] px-3 py-2.5 text-[13px] transition-colors duration-200 ${
              on
                ? "bg-acc/12 font-semibold text-acc"
                : `font-medium ${rawMode ? "text-ink3" : "text-ink2 hover:bg-surface hover:text-ink"}`
            }`}
          >
            <span className="text-left">{c.label}</span>
            <StatusHint tone={hints[c.id].tone}>{hints[c.id].text}</StatusHint>
          </button>
        );
      })}

      <div className="my-2.5 h-px bg-hairline" />
      <div className="flex items-center justify-between gap-2.5 px-3">
        <span className="text-[12.5px] text-ink2">Raw keys</span>
        <Toggle on={rawMode} onChange={onToggleRaw} aria-label="Raw keys mode" />
      </div>
      <p className="mt-1 px-3 text-[11px] leading-snug text-ink3">Show every setting as its config key</p>
    </aside>
  );
}

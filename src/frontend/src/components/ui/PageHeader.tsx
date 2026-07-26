import type { ReactNode } from "react";

/** Kopfzeile jeder Seite: Titel + eine Zeile Erklärung links, Steuerelemente rechts.
 *  Vorher hatte jede Seite ihre eigene, unterschiedlich große Überschrift. */
export function PageHeader({ title, subtitle, children }: { title: string; subtitle?: ReactNode; children?: ReactNode }) {
  return (
    <div className="mb-5 flex flex-wrap items-end justify-between gap-4">
      <div>
        <h1 className="text-[20px] font-semibold tracking-[-.015em] text-ink">{title}</h1>
        {subtitle && <p className="mt-1.5 text-[12.5px] text-ink3">{subtitle}</p>}
      </div>
      {children && <div className="flex flex-wrap items-center gap-2.5">{children}</div>}
    </div>
  );
}

/** Der pulsierende Live-Chip rechts in der Kopfzeile ("Watching 17 repositories"). */
export function LiveChip({ tone = "acc", children }: { tone?: "acc" | "warn"; children: ReactNode }) {
  return (
    <span className="flex items-center gap-2 rounded-full border border-hairline bg-input px-2.5 py-1">
      <span className={`size-1.5 rounded-full animate-livedot ${tone === "acc" ? "bg-acc" : "bg-warn"}`} />
      <span className="text-[11.5px] text-ink2">{children}</span>
    </span>
  );
}

import type { ReactNode } from "react";

export function Panel({
  title,
  extra,
  actions,
  className = "",
  children,
}: {
  title: string;
  extra?: ReactNode;
  actions?: ReactNode;
  className?: string;
  children: ReactNode;
}) {
  return (
    <div className={`overflow-hidden rounded-[14px] border border-hairline bg-surface ${className}`}>
      <div className="flex flex-wrap items-center gap-2.5 border-b border-hairline px-4 py-3">
        <h2 className="text-[13px] font-semibold text-ink">{title}</h2>
        {/* Ohne Aktionen wandert die Meta-Angabe an den rechten Rand (Dashboard-Panels),
            mit Aktionen bleibt sie am Titel und die Buttons übernehmen die rechte Seite. */}
        {extra && <span className={`text-[11px] text-ink3 ${actions ? "" : "ml-auto"}`}>{extra}</span>}
        {actions && <div className="ml-auto flex items-center gap-1.5">{actions}</div>}
      </div>
      {children}
    </div>
  );
}

/** Eine Panel-Zeile mit unterer Haarlinie. Diese Markup-Kopie lag vorher in acht Dateien. */
export function PanelRow({ className = "", children }: { className?: string; children: ReactNode }) {
  return (
    <div className={`flex items-center gap-3 border-b border-seam px-4 py-3.5 last:border-b-0 ${className}`}>
      {children}
    </div>
  );
}

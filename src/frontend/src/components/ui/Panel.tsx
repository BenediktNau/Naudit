import type { ReactNode } from "react";

export function Panel({ title, extra, children }: { title: string; extra?: string; children: ReactNode }) {
  return (
    <div className="overflow-hidden rounded-xl border border-hairline bg-surface">
      <div className="flex items-center justify-between border-b border-hairline px-4 py-3">
        <b className="font-mono text-[12.5px]">{title}</b>
        {extra && <span className="font-mono text-[11px] text-ink3">{extra}</span>}
      </div>
      {children}
    </div>
  );
}

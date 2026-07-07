import type { ReactNode } from "react";
import { Sparkline } from "@/components/ui/Sparkline";

export function StatTile({
  label,
  value,
  sub,
  subAccent = false,
  spark,
}: {
  label: string;
  value: string;
  sub?: ReactNode;
  subAccent?: boolean;
  spark?: number[];
}) {
  return (
    <div className="relative min-h-[132px] overflow-hidden rounded-xl border border-hairline bg-surface px-5 py-4">
      {spark && <Sparkline values={spark} />}
      <div className="relative">
        <div className="font-mono text-[11px] tracking-[.14em] text-ink3 uppercase">{label}</div>
        <div className="mt-2.5 font-mono text-[32px] leading-none font-bold tracking-tight tabular-nums">{value}</div>
        {sub && <div className={`mt-2 text-[12.5px] ${subAccent ? "text-acc" : "text-ink2"}`}>{sub}</div>}
      </div>
    </div>
  );
}

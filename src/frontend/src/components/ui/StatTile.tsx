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
    <div
      className="group relative min-h-[128px] overflow-hidden rounded-[14px] border border-hairline bg-surface px-4.5 py-4
                 transition-[border-color,transform] duration-200 ease-swift hover:-translate-y-0.5 hover:border-[#2b3542]"
    >
      {spark && <Sparkline values={spark} />}
      <div className="relative">
        <div className="text-[11px] font-semibold tracking-[.09em] text-ink3 uppercase">{label}</div>
        <div className="mt-3.5 font-mono text-[30px] leading-none font-bold tracking-[-.02em] tabular-nums">{value}</div>
        {sub && <div className={`mt-2.5 text-[12px] ${subAccent ? "text-acc" : "text-ink2"}`}>{sub}</div>}
      </div>
    </div>
  );
}

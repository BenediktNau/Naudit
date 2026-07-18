import { useState } from "react";
import { useAnalytics, useDashboard } from "@/hooks/queries";
import { StatTile } from "@/components/ui/StatTile";
import { Sparkline } from "@/components/ui/Sparkline";
import { Panel } from "@/components/ui/Panel";
import { Skeleton } from "@/components/ui/Skeleton";

const RANGE_OPTIONS = [7, 30, 90] as const;

// Gleiche Zuordnung wie ReviewDetail.sevColor, hier auf die Kleinschreibung der Analytics-API gemappt.
const sevBarColor: Record<string, string> = {
  critical: "bg-danger",
  high: "bg-danger",
  medium: "bg-warn",
  low: "bg-teal",
  info: "bg-ink3",
};

function pct(n: number): string {
  return `${Math.round(n * 100)}%`;
}

/** Auswertung: Accept-/FP-Rate, Severity-Breakdown, Wochentrend, Gedächtnis-Wirkung. */
export function AnalyticsPage() {
  const { data: dash, isLoading: dashLoading } = useDashboard();
  const [projectId, setProjectId] = useState<number | null>(null);
  const [days, setDays] = useState<(typeof RANGE_OPTIONS)[number]>(30);
  const { data, isLoading, isError } = useAnalytics(projectId, days);

  if (dashLoading) return <div className="p-7"><Skeleton className="h-4 w-64" /></div>;
  if (!dash || dash.projects.length === 0)
    return (
      <div className="p-7 font-mono text-[13px] text-ink3">
        No reviewed projects yet — analytics need at least one review.
      </div>
    );

  return (
    <div className="flex flex-col gap-5 p-7">
      <div className="flex flex-wrap items-center gap-3">
        <h1 className="text-[15px] font-semibold text-ink">Auswertung</h1>
        <select
          className="rounded-lg border border-border bg-elev px-2.5 py-1.5 font-mono text-[12.5px] text-ink"
          value={projectId ?? ""}
          onChange={(e) => setProjectId(e.target.value ? Number(e.target.value) : null)}
        >
          <option value="">All projects</option>
          {dash.projects.map((p) => (
            <option key={p.id} value={p.id}>{p.name}</option>
          ))}
        </select>
        <div className="ml-auto flex gap-1.5">
          {RANGE_OPTIONS.map((d) => (
            <button
              key={d}
              className={`rounded-lg px-2.5 py-1.5 font-mono text-[12px] ${
                days === d ? "bg-acc/12 font-semibold text-acc" : "font-medium text-ink2 hover:text-ink"
              }`}
              onClick={() => setDays(d)}
            >
              {d}d
            </button>
          ))}
        </div>
      </div>

      {isLoading && <Skeleton className="h-4 w-64" />}
      {isError && <div className="font-mono text-[12.5px] text-danger">failed to load analytics</div>}

      {data && (
        <>
          <div className="grid grid-cols-1 gap-4 md:grid-cols-4">
            <StatTile
              label="Findings posted"
              value={`${data.totals.posted}`}
              spark={data.weekly.map((w) => w.posted)}
            />
            <StatTile
              label="Acceptance rate"
              value={pct(data.totals.acceptanceRate)}
              sub={`${data.totals.accepted} accepted`}
              subAccent
            />
            <StatTile
              label="False-positive rate"
              value={pct(data.totals.fpRate)}
              sub={`${data.totals.rejected} rejected`}
            />
            <StatTile
              label="Memory applied"
              value={`${data.memory.timesApplied}`}
              sub={`${data.memory.active}/${data.memory.entries} active entries`}
            />
          </div>

          <Panel title="By severity" extra="accepted / posted">
            {data.bySeverity.length === 0 ? (
              <div className="px-5 py-6 font-mono text-xs text-ink3">No findings in this range.</div>
            ) : (
              <div className="flex flex-col gap-2.5 px-5 py-4">
                {data.bySeverity.map((s) => (
                  <div key={s.severity} className="flex items-center gap-3">
                    <span className="w-16 shrink-0 font-mono text-[11px] text-ink3 uppercase">{s.severity}</span>
                    <div className="h-2 flex-1 overflow-hidden rounded-full bg-elev">
                      <div
                        className={`h-full rounded-full ${sevBarColor[s.severity] ?? "bg-ink3"}`}
                        style={{ width: s.posted === 0 ? "0%" : `${(s.accepted / s.posted) * 100}%` }}
                      />
                    </div>
                    <span className="w-16 shrink-0 text-right font-mono text-[11px] text-ink2 tabular-nums">
                      {s.accepted}/{s.posted}
                    </span>
                  </div>
                ))}
              </div>
            )}
          </Panel>

          <Panel title="Weekly trend" extra="posted findings">
            {data.weekly.length < 2 ? (
              <div className="px-5 py-6 font-mono text-xs text-ink3">Not enough data yet.</div>
            ) : (
              <div className="relative h-24 px-5 py-4">
                <Sparkline values={data.weekly.map((w) => w.posted)} />
              </div>
            )}
          </Panel>
        </>
      )}
    </div>
  );
}

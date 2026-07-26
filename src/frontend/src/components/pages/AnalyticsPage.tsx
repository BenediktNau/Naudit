import { useState } from "react";
import { useAnalytics, useDashboard } from "@/hooks/queries";
import { StatTile } from "@/components/ui/StatTile";
import { Sparkline } from "@/components/ui/Sparkline";
import { Panel } from "@/components/ui/Panel";
import { Select } from "@/components/ui/Input";
import { PageHeader } from "@/components/ui/PageHeader";
import { EmptyState } from "@/components/ui/EmptyState";
import { sevBar } from "@/components/ui/severity";
import { Skeleton, SkeletonStatTile } from "@/components/ui/Skeleton";

const RANGE_OPTIONS = [7, 30, 90] as const;

function pct(n: number): string {
  return `${Math.round(n * 100)}%`;
}

/** Auswertung: Accept-/FP-Rate, Severity-Breakdown, Wochentrend, Gedächtnis-Wirkung. */
export function AnalyticsPage() {
  const { data: dash, isLoading: dashLoading, isError: dashError } = useDashboard();
  const [projectId, setProjectId] = useState<number | null>(null);
  const [days, setDays] = useState<(typeof RANGE_OPTIONS)[number]>(30);
  const { data, isLoading, isError } = useAnalytics(projectId, days);

  if (dashLoading) return <Skeleton className="h-4 w-64" />;
  // Fehler vor Leerzustand: ein gescheiterter Dashboard-Fetch ist keine leere Projektliste.
  if (dashError) return <div className="font-mono text-[13px] text-danger">failed to load projects</div>;
  if (!dash || dash.projects.length === 0)
    return (
      <div className="font-mono text-[13px] text-ink3">
        No reviewed projects yet — analytics need at least one review.
      </div>
    );

  return (
    <>
      <PageHeader title="Auswertung" subtitle="How much of what Naudit posts actually gets accepted.">
        <Select
          aria-label="Project"
          value={projectId ?? ""}
          onChange={(e) => setProjectId(e.target.value ? Number(e.target.value) : null)}
        >
          <option value="">All projects</option>
          {dash.projects.map((p) => (
            <option key={p.id} value={p.id}>
              {p.name}
            </option>
          ))}
        </Select>
        <div className="flex gap-0.5 rounded-[9px] border border-hairline bg-input p-0.5">
          {RANGE_OPTIONS.map((d) => (
            <button
              key={d}
              aria-pressed={days === d}
              className={`rounded-[7px] px-2.5 py-1.5 text-[11px] font-semibold tracking-[.05em] uppercase transition-colors duration-200
                          ${days === d ? "bg-acc/14 text-acc" : "text-ink3 hover:text-ink2"}`}
              onClick={() => setDays(d)}
            >
              {d}d
            </button>
          ))}
        </div>
      </PageHeader>

      {isLoading && (
        <div className="grid grid-cols-1 gap-3.5 md:grid-cols-2 xl:grid-cols-4">
          <SkeletonStatTile />
          <SkeletonStatTile />
          <SkeletonStatTile />
          <SkeletonStatTile />
        </div>
      )}
      {isError && <div className="font-mono text-[12.5px] text-danger">failed to load analytics</div>}

      {data && (
        <div className="flex flex-col gap-3.5">
          <div className="grid grid-cols-1 gap-3.5 md:grid-cols-2 xl:grid-cols-4">
            <StatTile
              label="Findings posted"
              value={`${data.totals.posted}`}
              sub={`across ${dash.projects.length} ${dash.projects.length === 1 ? "repository" : "repositories"}`}
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
              sub={`${data.memory.active}/${data.memory.entries} entries active`}
            />
          </div>

          <div className="grid grid-cols-1 items-start gap-3.5 lg:grid-cols-2">
            <Panel title="By severity" extra="Accepted / posted">
              {data.bySeverity.length === 0 ? (
                <EmptyState>No findings in this range.</EmptyState>
              ) : (
                <div className="flex flex-col gap-3.5 px-4 py-4.5">
                  {data.bySeverity.map((s, i) => (
                    <div key={s.severity} className="flex items-center gap-3">
                      <span className="w-16 shrink-0 text-[11px] font-semibold tracking-[.06em] text-ink3 uppercase">
                        {s.severity}
                      </span>
                      <div className="h-1.5 flex-1 overflow-hidden rounded-full bg-hairline">
                        <div
                          className={`h-full origin-left animate-barfill rounded-full ${sevBar(s.severity)}`}
                          style={{
                            width: s.posted === 0 ? "0%" : `${(s.accepted / s.posted) * 100}%`,
                            animationDelay: `${0.1 + i * 0.08}s`,
                          }}
                        />
                      </div>
                      <span className="w-14 shrink-0 text-right font-mono text-[11.5px] text-ink2 tabular-nums">
                        {s.accepted}/{s.posted}
                      </span>
                    </div>
                  ))}
                </div>
              )}
            </Panel>

            <Panel title="Weekly trend" extra="Posted findings">
              {data.weekly.length < 2 ? (
                <EmptyState>Not enough data yet.</EmptyState>
              ) : (
                <div className="relative m-4 h-32">
                  <Sparkline values={data.weekly.map((w) => w.posted)} band={0.74} />
                </div>
              )}
            </Panel>
          </div>
        </div>
      )}
    </>
  );
}

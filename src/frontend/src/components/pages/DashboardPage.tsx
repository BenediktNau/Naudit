import { useState } from "react";
import type { DashboardFocus } from "@/App";
import { useDashboard, fmtTokens } from "@/hooks/queries";
import { Panel } from "@/components/ui/Panel";
import { StatTile } from "@/components/ui/StatTile";
import { VerdictPill } from "@/components/ui/Pill";
import { PageHeader, LiveChip } from "@/components/ui/PageHeader";
import { EmptyState } from "@/components/ui/EmptyState";
import { Collapse } from "@/components/ui/Collapse";
import { Chevron } from "@/components/ui/icons";
import { Skeleton, SkeletonPanel, SkeletonRows, SkeletonStatTile } from "@/components/ui/Skeleton";
import { ReviewDetail } from "@/components/ReviewDetail";
import { InstallAppBanner } from "@/components/InstallAppBanner";

function timeAgo(iso: string): string {
  const s = (Date.now() - new Date(iso).getTime()) / 1000;
  if (s < 3600) return `${Math.max(1, Math.floor(s / 60))} min ago`;
  if (s < 86400) return `${Math.floor(s / 3600)} h ago`;
  if (s < 7 * 86400) return `${Math.floor(s / 86400)} d ago`;
  return new Date(iso).toLocaleDateString("en", { month: "short", day: "numeric" });
}

const rowCls =
  "flex w-full items-center gap-3 border-b border-seam px-4 py-3 text-left transition-colors duration-200 hover:bg-elev";

// Layout-treues Skeleton: 3 Kacheln + 2 Panels wie im echten Dashboard → kein Sprung beim Laden.
function DashboardSkeleton() {
  return (
    <div className="flex flex-col gap-3.5">
      <Skeleton className="h-5 w-40" />
      <div className="grid grid-cols-1 gap-3.5 md:grid-cols-3">
        <SkeletonStatTile />
        <SkeletonStatTile />
        <SkeletonStatTile />
      </div>
      <div className="grid grid-cols-1 items-start gap-3.5 lg:grid-cols-[5fr_7fr]">
        <SkeletonPanel>
          <SkeletonRows count={4}>
            {() => (
              <>
                <Skeleton className="size-3 shrink-0" />
                <div className="min-w-0 flex-1">
                  <Skeleton className="h-2.5 w-40" />
                  <Skeleton className="mt-1.5 h-2 w-24" />
                </div>
                <Skeleton className="h-2.5 w-10" />
              </>
            )}
          </SkeletonRows>
        </SkeletonPanel>
        <SkeletonPanel>
          <SkeletonRows count={5}>
            {() => (
              <>
                <Skeleton className="size-3 shrink-0" />
                <div className="min-w-0 flex-1">
                  <Skeleton className="h-2.5 w-56" />
                  <Skeleton className="mt-1.5 h-2 w-32" />
                </div>
                <Skeleton className="h-4 w-14 rounded-full" />
                <Skeleton className="h-2.5 w-10" />
              </>
            )}
          </SkeletonRows>
        </SkeletonPanel>
      </div>
    </div>
  );
}

export function DashboardPage({ focus }: { focus?: DashboardFocus | null }) {
  const { data, isLoading, isError } = useDashboard();
  const [openProject, setOpenProject] = useState<number | null>(null);
  const [openReview, setOpenReview] = useState<number | null>(null);

  // Sprung aus der Command-Palette: die getroffene Zeile aufklappen. Zustand beim Rendern
  // nachziehen statt im Effekt — React verwirft den Zwischenstand, ohne ihn zu zeigen.
  // Der nonce sorgt dafür, dass derselbe Treffer nach dem Zuklappen erneut greift.
  const [seenFocus, setSeenFocus] = useState(0);
  if (focus && focus.nonce !== seenFocus) {
    setSeenFocus(focus.nonce);
    if (focus.projectId !== undefined) setOpenProject(focus.projectId);
    if (focus.reviewId !== undefined) setOpenReview(focus.reviewId);
  }

  if (isLoading) return <DashboardSkeleton />;
  if (isError || !data) return <div className="font-mono text-[13px] text-danger">failed to load dashboard</div>;

  const month = new Date().toLocaleDateString("en", { month: "long" });

  return (
    <>
      <PageHeader
        title="Overview"
        subtitle={
          data.reviewsWeek === 0 ? "No reviews in the last seven days." : `${data.reviewsWeek} reviews in the last seven days`
        }
      >
        <LiveChip>
          Watching {data.projectsTotal} {data.projectsTotal === 1 ? "repository" : "repositories"}
        </LiveChip>
      </PageHeader>

      <div className="flex flex-col gap-3.5">
        <InstallAppBanner />

        <div className="grid grid-cols-1 gap-3.5 md:grid-cols-3">
          <StatTile
            label={`Tokens · ${month}`}
            value={fmtTokens(data.tokensMonth)}
            spark={data.tokensPerDay.map((d) => d.tokens)}
          />
          <StatTile
            label="Reviews"
            value={`${data.reviewsTotal}`}
            sub={`${data.reviewsWeek} this week`}
            spark={data.reviewsPerDay.map((d) => d.count)}
          />
          <StatTile
            label="Repositories"
            value={`${data.projectsTotal}`}
            sub={`${data.projectsNewMonth} new this month`}
          />
        </div>

        <div className="grid grid-cols-1 items-start gap-3.5 lg:grid-cols-[5fr_7fr]">
          <Panel title="Projects" extra="Auto-enrolled on first review">
            {data.projects.length === 0 && <EmptyState>No reviews yet.</EmptyState>}
            {data.projects.map((p) => {
              const open = openProject === p.id;
              return (
                <div key={p.id} className="border-b border-seam last:border-b-0">
                  <button className={rowCls} onClick={() => setOpenProject(open ? null : p.id)} aria-expanded={open}>
                    <Chevron open={open} />
                    <span className="min-w-0 flex-1">
                      <span className="block truncate font-mono text-[12.5px] text-ink">{p.name}</span>
                      <span className="mt-0.5 block text-[11px] text-ink3">last · {timeAgo(p.lastReviewedAt)}</span>
                    </span>
                    <span className="font-mono text-[11.5px] text-ink2 tabular-nums">{fmtTokens(p.totalTokens)}</span>
                  </button>
                  {open && (
                    <Collapse>
                      <div className="border-t border-seam bg-sunken">
                        {p.reviews.length === 0 && <EmptyState>No reviews for this project.</EmptyState>}
                        {p.reviews.map((r) => (
                          <div
                            key={r.id}
                            className="flex items-center gap-2.5 border-b border-seam py-2.5 pr-4 pl-9 last:border-b-0"
                          >
                            <span className="shrink-0 font-mono text-[11px] text-ink3">#{r.prNumber}</span>
                            <span className="min-w-0 flex-1 truncate text-[12px] text-ink2">{r.title}</span>
                            <VerdictPill verdict={r.verdict} />
                          </div>
                        ))}
                      </div>
                    </Collapse>
                  )}
                </div>
              );
            })}
          </Panel>

          <Panel title="Recent reviews" extra="All projects">
            {data.recentReviews.length === 0 && <EmptyState>No reviews yet.</EmptyState>}
            {data.recentReviews.map((r) => {
              const open = openReview === r.id;
              return (
                <div key={r.id} className="border-b border-seam last:border-b-0">
                  <button className={rowCls} onClick={() => setOpenReview(open ? null : r.id)} aria-expanded={open}>
                    <Chevron open={open} />
                    <span className="min-w-0 flex-1">
                      <span className="block truncate text-[13px] font-medium text-ink">{r.title}</span>
                      <span className="mt-0.5 block font-mono text-[10.5px] text-ink3">
                        {r.project} #{r.prNumber} · {timeAgo(r.createdAt)}
                      </span>
                    </span>
                    <VerdictPill verdict={r.verdict} />
                    <span className="shrink-0 font-mono text-[11.5px] text-ink2 tabular-nums">
                      {fmtTokens(r.totalTokens)}
                    </span>
                  </button>
                  {open && (
                    <Collapse>
                      <ReviewDetail id={r.id} />
                    </Collapse>
                  )}
                </div>
              );
            })}
          </Panel>
        </div>
      </div>
    </>
  );
}

import { useState } from "react";
import { useDashboard, fmtTokens } from "@/hooks/queries";
import { Panel } from "@/components/ui/Panel";
import { StatTile } from "@/components/ui/StatTile";
import { VerdictPill } from "@/components/ui/Pill";
import { ReviewDetail } from "@/components/ReviewDetail";

function timeAgo(iso: string): string {
  const s = (Date.now() - new Date(iso).getTime()) / 1000;
  if (s < 3600) return `${Math.max(1, Math.floor(s / 60))} min ago`;
  if (s < 86400) return `${Math.floor(s / 3600)} h ago`;
  if (s < 7 * 86400) return `${Math.floor(s / 86400)} d ago`;
  return new Date(iso).toLocaleDateString("en", { month: "short", day: "numeric" });
}

const chevron = (open: boolean) => (
  <svg
    width="13"
    height="13"
    viewBox="0 0 24 24"
    fill="none"
    stroke="currentColor"
    strokeWidth="2.5"
    className={`shrink-0 text-ink3 transition-transform ${open ? "rotate-90" : ""}`}
  >
    <path d="M9 6l6 6-6 6" />
  </svg>
);

export function DashboardPage() {
  const { data, isLoading, isError } = useDashboard();
  const [openProject, setOpenProject] = useState<number | null>(null);
  const [openReview, setOpenReview] = useState<number | null>(null);

  if (isLoading) return <div className="p-8 font-mono text-ink3">loading…</div>;
  if (isError || !data) return <div className="p-8 font-mono text-danger">failed to load dashboard</div>;

  return (
    <div className="flex flex-col gap-5 px-7 py-6">
      <div className="grid grid-cols-1 gap-4 md:grid-cols-3">
        <StatTile
          label={`Tokens · ${new Date().toLocaleDateString("en", { month: "long" })}`}
          value={fmtTokens(data.tokensMonth)}
          spark={data.tokensPerDay.map((d) => d.tokens)}
        />
        <StatTile
          label="Reviews"
          value={`${data.reviewsTotal}`}
          sub={`${data.reviewsWeek} this week`}
          spark={data.reviewsPerDay.map((d) => d.count)}
        />
        <StatTile label="Projects" value={`${data.projectsTotal}`} sub={`${data.projectsNewMonth} new this month`} />
      </div>

      <div className="grid grid-cols-1 items-start gap-4 lg:grid-cols-[5fr_7fr]">
        <Panel title="Projects" extra="auto · on 1st review">
          {data.projects.length === 0 && <div className="px-5 py-6 font-mono text-xs text-ink3">No reviews yet.</div>}
          {data.projects.map((p) => (
            <div key={p.id}>
              <button
                className="flex w-full items-center gap-3 border-b border-hairline px-5 py-3.5 text-left hover:bg-elev"
                onClick={() => setOpenProject(openProject === p.id ? null : p.id)}
              >
                {chevron(openProject === p.id)}
                <span className="min-w-0 flex-1">
                  <span className="block truncate font-mono text-[13px]">{p.name}</span>
                  <span className="mt-0.5 block text-[11.5px] text-ink3">last · {timeAgo(p.lastReviewedAt)}</span>
                </span>
                <span className="font-mono text-xs text-ink2 tabular-nums">{fmtTokens(p.totalTokens)}</span>
              </button>
              {openProject === p.id &&
                p.reviews.map((r) => (
                  <div key={r.id} className="flex items-center gap-2.5 border-b border-hairline bg-bg py-2.5 pr-5 pl-10">
                    <span className="min-w-0 flex-1 truncate font-mono text-[11.5px] text-ink2">
                      #{r.prNumber} · {r.title}
                    </span>
                    <VerdictPill verdict={r.verdict} />
                  </div>
                ))}
            </div>
          ))}
        </Panel>

        <Panel title="Recently reviewed" extra="PRs · all projects">
          {data.recentReviews.length === 0 && <div className="px-5 py-6 font-mono text-xs text-ink3">No reviews yet.</div>}
          {data.recentReviews.map((r) => (
            <div key={r.id}>
              <button
                className="flex w-full items-center gap-3 border-b border-hairline px-5 py-3.5 text-left hover:bg-elev"
                onClick={() => setOpenReview(openReview === r.id ? null : r.id)}
              >
                {chevron(openReview === r.id)}
                <span className="min-w-0 flex-1">
                  <span className="block truncate text-[13.5px] font-semibold">{r.title}</span>
                  <span className="mt-0.5 block font-mono text-[11px] text-ink3">
                    {r.project} #{r.prNumber} · {timeAgo(r.createdAt)}
                  </span>
                </span>
                <VerdictPill verdict={r.verdict} />
                <span className="font-mono text-xs text-ink2 tabular-nums">{fmtTokens(r.totalTokens)}</span>
              </button>
              {openReview === r.id && <ReviewDetail id={r.id} />}
            </div>
          ))}
        </Panel>
      </div>
    </div>
  );
}

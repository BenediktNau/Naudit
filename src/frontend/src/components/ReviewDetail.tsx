import { useReviewDetail, fmtTokens } from "@/hooks/queries";
import { useMarkFalsePositive, useUnmarkFalsePositive } from "@/hooks/mutations";
import { Logo } from "@/components/ui/Logo";
import { Skeleton } from "@/components/ui/Skeleton";

const sevColor: Record<string, string> = {
  Critical: "text-danger bg-danger/12",
  High: "text-danger bg-danger/12",
  Medium: "text-warn bg-warn/12",
  Low: "text-teal bg-teal/12",
  Info: "text-ink3 bg-elev",
};

/** Detailbereich einer aufgeklappten Review-Zeile: Verdict-Meta, Summary, Findings. */
export function ReviewDetail({ id }: { id: number }) {
  const { data, isLoading, isError } = useReviewDetail(id);
  const mark = useMarkFalsePositive(id);
  const unmark = useUnmarkFalsePositive(id);
  if (isLoading)
    return (
      // gleicher Container wie der geladene Zustand → die aufgeklappte Zeile behält ihre Höhe.
      <div className="border-b border-hairline bg-bg py-4 pr-5 pl-10">
        <Skeleton className="h-2.5 w-40" />
        <Skeleton className="mt-3 h-3 w-full max-w-[70ch]" />
        <Skeleton className="mt-1.5 h-3 w-full max-w-[62ch]" />
        <Skeleton className="mt-1.5 h-3 w-1/2" />
      </div>
    );
  if (isError || !data)
    return <div className="border-b border-hairline bg-bg px-10 py-4 font-mono text-xs text-danger">failed to load review</div>;

  return (
    <div className="border-b border-hairline bg-bg py-4 pr-5 pl-10">
      <div className="mb-3 flex flex-wrap items-center gap-2 font-mono text-[11px] text-ink3">
        <span className="inline-flex items-center gap-1.5 text-acc">
          <Logo size={14} /> Naudit verdict
        </span>
        {data.model && <span>· {data.model}</span>}
        {data.inputTokens !== null && (
          <span className="tabular-nums">
            · {fmtTokens(data.inputTokens)} in / {fmtTokens(data.outputTokens ?? 0)} out
          </span>
        )}
      </div>
      <div className="mb-3.5 max-w-[75ch] text-[13px] leading-relaxed whitespace-pre-line text-ink">{data.summary}</div>
      {data.findings.length > 0 && (
        <div className="flex flex-col gap-2">
          {data.findings.map((f) => (
            <div key={f.id} className="flex items-start justify-between gap-2.5">
              <span className={`mt-px shrink-0 rounded px-1.5 py-0.5 font-mono text-[10px] ${sevColor[f.severity] ?? sevColor.Info}`}>
                {f.severity.toLowerCase()}
              </span>
              <div className="text-[12.5px] leading-snug text-ink2">
                {f.file && (
                  <span className="font-mono text-ink3">
                    {f.file}
                    {f.line !== null ? `:${f.line}` : ""} —{" "}
                  </span>
                )}
                {f.text}
              </div>
              <button
                className={`ml-auto shrink-0 self-start rounded px-1.5 py-0.5 font-mono text-[10px] ${
                  f.falsePositive ? "bg-warn/12 text-warn" : "text-ink3 hover:text-warn"
                }`}
                title={
                  f.falsePositive
                    ? "Marked as false positive — click to undo"
                    : "Mark as false positive (feeds the project memory)"
                }
                onClick={() =>
                  f.falsePositive ? unmark.mutate(f.id) : mark.mutate({ findingId: f.id })
                }
              >
                {f.falsePositive ? "FP ✓" : "FP"}
              </button>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

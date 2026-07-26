import { useReviewDetail, fmtTokens } from "@/hooks/queries";
import { useMarkFalsePositive, useUnmarkFalsePositive, useSetResolution } from "@/hooks/mutations";
import { Logo } from "@/components/ui/Logo";
import { Pill } from "@/components/ui/Pill";
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
  const resolution = useSetResolution(id);
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
              <div className="ml-auto flex shrink-0 items-center gap-1.5 self-start">
                {f.resolutionStatus && (
                  <Pill kind={f.resolutionStatus === "Accepted" ? "ok" : "danger"}>
                    {f.resolutionStatus === "Accepted" ? "✓ accepted" : "✗ rejected"}
                  </Pill>
                )}
                <button
                  className={`rounded px-1.5 py-0.5 font-mono text-[10px] disabled:opacity-50 ${
                    f.resolutionStatus === "Accepted" ? "bg-acc/12 text-acc" : "text-ink3 hover:text-acc"
                  }`}
                  title={f.resolutionStatus === "Accepted" ? "Undo accept" : "Accept finding"}
                  disabled={resolution.isPending}
                  onClick={() =>
                    resolution.mutate({
                      findingId: f.id,
                      status: f.resolutionStatus === "Accepted" ? null : "Accepted",
                    })
                  }
                >
                  accept
                </button>
                <button
                  className={`rounded px-1.5 py-0.5 font-mono text-[10px] disabled:opacity-50 ${
                    f.resolutionStatus === "Rejected" ? "bg-danger/12 text-danger" : "text-ink3 hover:text-danger"
                  }`}
                  title={f.resolutionStatus === "Rejected" ? "Undo reject" : "Reject finding"}
                  disabled={resolution.isPending}
                  onClick={() =>
                    resolution.mutate({
                      findingId: f.id,
                      status: f.resolutionStatus === "Rejected" ? null : "Rejected",
                    })
                  }
                >
                  reject
                </button>
                <button
                  className={`rounded px-1.5 py-0.5 font-mono text-[10px] disabled:opacity-50 ${
                    f.falsePositive ? "bg-warn/12 text-warn" : "text-ink3 hover:text-warn"
                  }`}
                  title={
                    f.falsePositive
                      ? "Marked as false positive — click to undo"
                      : "Mark as false positive (feeds the project memory)"
                  }
                  disabled={mark.isPending || unmark.isPending}
                  onClick={() =>
                    f.falsePositive ? unmark.mutate(f.id) : mark.mutate({ findingId: f.id })
                  }
                >
                  {f.falsePositive ? "FP ✓" : "FP"}
                </button>
              </div>
            </div>
          ))}
        </div>
      )}
      {data.transcripts && data.transcripts.length > 0 && (
        <div className="mt-4 border-t border-hairline pt-3">
          <div className="mb-2 font-mono text-[11px] tracking-wide text-ink3 uppercase">
            Prompt &amp; Kommunikation · {data.transcripts.length}
          </div>
          <div className="flex flex-col gap-2">
            {data.transcripts.map((t) => (
              <details key={t.id} className="rounded border border-hairline bg-elev">
                <summary className="cursor-pointer list-none px-3 py-2 font-mono text-[11px] text-ink2">
                  <span className={t.failed ? "text-danger" : "text-acc"}>{t.failed ? "✗ failed" : "▸ call"}</span>
                  {t.model && <span className="text-ink3"> · {t.model}</span>}
                  <span className="text-ink3 tabular-nums">
                    {" "}· {t.latencyMs}ms
                    {t.inputTokens !== null && ` · ${fmtTokens(t.inputTokens)} in / ${fmtTokens(t.outputTokens ?? 0)} out`}
                    {t.toolCount > 0 && ` · ${t.toolCount} tools`}
                  </span>
                </summary>
                <div className="flex flex-col gap-2 px-3 pt-1 pb-3">
                  {t.systemPrompt && <PromptBlock label="System-Prompt" text={t.systemPrompt} />}
                  {t.userPrompt && <PromptBlock label="User-Prompt (Diff + Kontext)" text={t.userPrompt} />}
                  {t.responseText && <PromptBlock label="Rohe LLM-Antwort" text={t.responseText} />}
                </div>
              </details>
            ))}
          </div>
        </div>
      )}
    </div>
  );
}

/** Ein beschriftetes, scrollbares Klartext-Feld (Prompt bzw. Antwort) — Volltext zum Prompt-Optimieren. */
function PromptBlock({ label, text }: { label: string; text: string }) {
  return (
    <div>
      <div className="mb-1 font-mono text-[10px] text-ink3">{label}</div>
      <pre className="max-h-80 overflow-auto rounded bg-bg p-2 font-mono text-[11px] leading-relaxed whitespace-pre-wrap text-ink2">
        {text}
      </pre>
    </div>
  );
}

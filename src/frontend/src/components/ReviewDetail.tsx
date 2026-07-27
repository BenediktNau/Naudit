import type { ReactNode } from "react";
import { useReviewDetail, fmtTokens } from "@/hooks/queries";
import { useMarkFalsePositive, useUnmarkFalsePositive, useSetResolution } from "@/hooks/mutations";
import { Logo } from "@/components/ui/Logo";
import { SevTag } from "@/components/ui/Pill";
import { sevPill } from "@/components/ui/severity";
import { Skeleton } from "@/components/ui/Skeleton";

const shell = "border-t border-seam bg-sunken px-4 py-4 md:pr-5 md:pl-9";

/** Ein Knopf der Aktionsgruppe unter einem Finding. Aktiv = gefüllt, sonst stumm. */
function ActionButton({
  active,
  tone,
  title,
  disabled,
  onClick,
  children,
}: {
  active: boolean;
  tone: "acc" | "danger" | "warn";
  title: string;
  disabled?: boolean;
  onClick: () => void;
  children: ReactNode;
}) {
  const on = {
    acc: "bg-acc/14 text-acc",
    danger: "bg-danger/14 text-danger",
    warn: "bg-warn/14 text-warn",
  }[tone];
  return (
    <button
      title={title}
      aria-pressed={active}
      disabled={disabled}
      onClick={onClick}
      className={`rounded-[7px] px-2.5 py-1 text-[11.5px] font-medium whitespace-nowrap transition-colors duration-200
                  disabled:opacity-50 ${active ? on : "text-ink3 hover:bg-hairline hover:text-ink"}`}
    >
      {children}
    </button>
  );
}

/** Detailbereich einer aufgeklappten Review-Zeile: Verdict-Meta, Summary, Findings, Transkripte. */
export function ReviewDetail({ id }: { id: number }) {
  const { data, isLoading, isError } = useReviewDetail(id);
  const mark = useMarkFalsePositive(id);
  const unmark = useUnmarkFalsePositive(id);
  const resolution = useSetResolution(id);

  if (isLoading)
    return (
      // gleicher Container wie der geladene Zustand → die aufgeklappte Zeile behält ihre Höhe.
      <div className={shell}>
        <Skeleton className="h-2.5 w-40" />
        <Skeleton className="mt-3 h-2.5 w-full max-w-[70ch]" />
        <Skeleton className="mt-1.5 h-2.5 w-full max-w-[62ch]" />
        <Skeleton className="mt-1.5 h-2.5 w-1/2" />
      </div>
    );
  if (isError || !data) return <div className={`${shell} font-mono text-xs text-danger`}>failed to load review</div>;

  const fpBusy = mark.isPending || unmark.isPending;

  return (
    <div className={shell}>
      <div className="flex flex-wrap items-center gap-2 font-mono text-[10.5px] text-ink3">
        <span className="inline-flex items-center gap-1.5 text-acc">
          <Logo size={14} /> Naudit verdict
        </span>
        {data.model && (
          <>
            <span>·</span>
            <span>{data.model}</span>
          </>
        )}
        {data.inputTokens !== null && (
          <>
            <span>·</span>
            <span className="tabular-nums">
              {fmtTokens(data.inputTokens)} in / {fmtTokens(data.outputTokens ?? 0)} out
            </span>
          </>
        )}
      </div>

      <p className="mt-3 mb-4 max-w-[78ch] text-[12.5px] leading-relaxed whitespace-pre-line text-ink">{data.summary}</p>

      {data.findings.length > 0 && (
        <div className="flex flex-col">
          {data.findings.map((f) => (
            <div key={f.id} className="flex flex-wrap items-start gap-2.5 border-t border-seam py-2.5">
              <SevTag className={sevPill(f.severity)}>{f.severity.toLowerCase()}</SevTag>
              <div className="min-w-[20ch] flex-1 text-[12.5px] leading-snug text-ink2">
                {f.file && (
                  <span className="font-mono text-[11px] text-ink3">
                    {f.file}
                    {f.line !== null ? `:${f.line}` : ""} —{" "}
                  </span>
                )}
                {f.text}
              </div>
              <div className="flex shrink-0 gap-px rounded-[9px] border border-hairline bg-surface p-0.5">
                <ActionButton
                  active={f.resolutionStatus === "Accepted"}
                  tone="acc"
                  title={f.resolutionStatus === "Accepted" ? "Undo accept" : "Accept this finding"}
                  disabled={resolution.isPending}
                  onClick={() =>
                    resolution.mutate({
                      findingId: f.id,
                      status: f.resolutionStatus === "Accepted" ? null : "Accepted",
                    })
                  }
                >
                  Accept
                </ActionButton>
                <ActionButton
                  active={f.resolutionStatus === "Rejected"}
                  tone="danger"
                  title={f.resolutionStatus === "Rejected" ? "Undo reject" : "Reject this finding"}
                  disabled={resolution.isPending}
                  onClick={() =>
                    resolution.mutate({
                      findingId: f.id,
                      status: f.resolutionStatus === "Rejected" ? null : "Rejected",
                    })
                  }
                >
                  Reject
                </ActionButton>
                <ActionButton
                  active={f.falsePositive}
                  tone="warn"
                  title={
                    f.falsePositive
                      ? "Marked as false positive — click to undo"
                      : "Mark as false positive (feeds the project memory)"
                  }
                  disabled={fpBusy}
                  onClick={() => (f.falsePositive ? unmark.mutate(f.id) : mark.mutate({ findingId: f.id }))}
                >
                  {f.falsePositive ? "False positive ✓" : "False positive"}
                </ActionButton>
              </div>
            </div>
          ))}
        </div>
      )}

      {data.transcripts && data.transcripts.length > 0 && (
        <div className="mt-4 border-t border-hairline pt-3.5">
          <div className="mb-2.5 text-[11px] font-semibold tracking-[.09em] text-ink3 uppercase">
            Prompt &amp; Kommunikation · {data.transcripts.length}
          </div>
          <div className="flex flex-col gap-2">
            {data.transcripts.map((t) => (
              <details key={t.id} className="rounded-[11px] border border-hairline bg-surface">
                <summary className="cursor-pointer list-none px-3 py-2 font-mono text-[11px] text-ink2">
                  <span className={t.failed ? "text-danger" : "text-acc"}>{t.failed ? "✗ failed" : "▸ call"}</span>
                  {t.model && <span className="text-ink3"> · {t.model}</span>}
                  <span className="text-ink3 tabular-nums">
                    {" "}
                    · {t.latencyMs}ms
                    {t.inputTokens !== null &&
                      ` · ${fmtTokens(t.inputTokens)} in / ${fmtTokens(t.outputTokens ?? 0)} out`}
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
      <pre className="max-h-80 overflow-auto rounded-lg bg-bg p-2.5 font-mono text-[11px] leading-relaxed whitespace-pre-wrap text-ink2">
        {text}
      </pre>
    </div>
  );
}

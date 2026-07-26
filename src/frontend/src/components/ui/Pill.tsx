import type { ReactNode } from "react";

type Kind = "ok" | "warn" | "danger" | "teal" | "neutral";

const styles: Record<Kind, string> = {
  ok: "text-acc bg-acc/12",
  warn: "text-warn bg-warn/12",
  danger: "text-danger bg-danger/12",
  teal: "text-teal bg-teal/12",
  neutral: "text-ink2 bg-hairline",
};

const dots: Record<Kind, string> = {
  ok: "bg-acc",
  warn: "bg-warn",
  danger: "bg-danger",
  teal: "bg-teal",
  neutral: "bg-ink3",
};

/** Status ist nie nur Farbe — der Punkt ist Dekoration, die Bedeutung trägt immer der Text. */
export function Pill({ kind, dot = false, children }: { kind: Kind; dot?: boolean; children: ReactNode }) {
  return (
    <span
      className={`inline-flex shrink-0 items-center gap-1.5 whitespace-nowrap rounded-full px-2.5 py-0.5 font-mono text-[10.5px] ${styles[kind]}`}
    >
      {dot && <span className={`size-1.5 shrink-0 rounded-full ${dots[kind]}`} aria-hidden />}
      {children}
    </span>
  );
}

export function VerdictPill({ verdict }: { verdict: "approve" | "request_changes" }) {
  return verdict === "approve" ? (
    <Pill kind="ok" dot>
      approved
    </Pill>
  ) : (
    <Pill kind="danger" dot>
      changes
    </Pill>
  );
}

/** Severity-Label an einem Finding — eckig statt rund, damit es sich vom Verdict abhebt. */
export function SevTag({ className = "", children }: { className?: string; children: ReactNode }) {
  return (
    <span className={`shrink-0 rounded-md px-2 py-0.5 font-mono text-[10px] tracking-[.04em] ${className}`}>
      {children}
    </span>
  );
}

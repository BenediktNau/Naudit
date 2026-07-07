import type { ReactNode } from "react";

type Kind = "ok" | "warn" | "danger" | "neutral";

const styles: Record<Kind, string> = {
  ok: "text-acc bg-acc/12",
  warn: "text-warn bg-warn/12",
  danger: "text-danger bg-danger/12",
  neutral: "text-ink2 bg-elev",
};

/** Status ist nie nur Farbe: Aufrufer übergeben Glyphe+Text ("✓ approve", "● pending"). */
export function Pill({ kind, children }: { kind: Kind; children: ReactNode }) {
  return (
    <span
      className={`inline-flex items-center gap-1.5 whitespace-nowrap rounded-full px-2.5 py-0.5 font-mono text-[11px] ${styles[kind]}`}
    >
      {children}
    </span>
  );
}

export function VerdictPill({ verdict }: { verdict: "approve" | "request_changes" }) {
  return verdict === "approve" ? <Pill kind="ok">✓ approve</Pill> : <Pill kind="danger">⚠ changes</Pill>;
}

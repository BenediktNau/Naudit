import type { ReactNode } from "react";

/** CSS-only Platzhalter: schimmernder Block, Größe kommt per className.
 *  Die globale reduced-motion-Regel in index.css friert die Animation ein. */
export function Skeleton({ className = "" }: { className?: string }) {
  return <div className={`animate-shimmer rounded bg-elev ${className}`} aria-hidden="true" />;
}

/** Panel-Hülle mit Skeleton-Kopfzeile — spiegelt <Panel> fürs Laden. Zeilen als children. */
export function SkeletonPanel({ children }: { children?: ReactNode }) {
  return (
    <div className="overflow-hidden rounded-[14px] border border-hairline bg-surface">
      <div className="flex items-center justify-between border-b border-hairline px-4 py-3">
        <Skeleton className="h-3 w-24" />
        <Skeleton className="h-3 w-14" />
      </div>
      {children}
    </div>
  );
}

/** N gleich hohe Skeleton-Zeilen mit unterer Trennlinie (für Panel-Listen). */
export function SkeletonRows({ count, children }: { count: number; children: (i: number) => ReactNode }) {
  return (
    <>
      {Array.from({ length: count }, (_, i) => (
        <div key={i} className="flex items-center gap-3 border-b border-seam px-4 py-3.5 last:border-b-0">
          {children(i)}
        </div>
      ))}
    </>
  );
}

/** Kachel-Skeleton in der Geometrie von <StatTile> — verhindert den Sprung beim Laden. */
export function SkeletonStatTile() {
  return (
    <div className="min-h-[128px] rounded-[14px] border border-hairline bg-surface px-4.5 py-4">
      <Skeleton className="h-2.5 w-20" />
      <Skeleton className="mt-3.5 h-6.5 w-28" />
      <Skeleton className="mt-3 h-2.5 w-24" />
    </div>
  );
}

import type { ReactNode } from "react";

/** CSS-only Platzhalter: pulsierender Block, Größe kommt per className.
 *  motion-reduce schaltet die Animation ab (prefers-reduced-motion). */
export function Skeleton({ className = "" }: { className?: string }) {
  return (
    <div
      className={`animate-pulse rounded bg-elev motion-reduce:animate-none ${className}`}
      aria-hidden="true"
    />
  );
}

/** Panel-Hülle mit Skeleton-Kopfzeile — spiegelt <Panel> fürs Laden. Zeilen als children. */
export function SkeletonPanel({ children }: { children?: ReactNode }) {
  return (
    <div className="overflow-hidden rounded-xl border border-hairline bg-surface">
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
        <div key={i} className="flex items-center gap-3.5 border-b border-hairline px-5 py-4 last:border-b-0">
          {children(i)}
        </div>
      ))}
    </>
  );
}

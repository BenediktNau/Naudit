import { useId } from "react";

/** Flächen-Sparkline. Im Kachel-Hintergrund flach (band ≈ .34), als eigenständiges
 *  Trend-Diagramm hoch (band ≈ .74). Die Linie zeichnet sich beim Erscheinen. */
export function Sparkline({ values, band = 0.34 }: { values: number[]; band?: number }) {
  // Eindeutige Gradient-id pro Instanz — sonst kollidieren mehrere Sparklines im selben
  // Dokument (Dashboard rendert zwei). Doppelpunkte aus useId() raus, damit url(#…) gültig bleibt.
  const gid = `spark${useId().replace(/:/g, "")}`;
  if (values.length < 2) return null;

  // Zwischen Minimum und Maximum skalieren statt ab Null: sonst verschwindet der Verlauf
  // bei Reihen, die durchweg hoch liegen (z. B. 94, 96, 98), in einer geraden Linie.
  const max = Math.max(...values);
  const lo = Math.min(...values);
  // `|| 1` fängt die konstante Reihe ab (max === lo); eine Untergrenze auf dem Maximum
  // wäre falsch — sie würde Reihen unterhalb von 1 stauchen.
  const span = max - lo || 1;
  const pts = values.map((v, i) => ({
    x: (i / (values.length - 1)) * 300,
    y: 96 - ((v - lo) / span) * band * 100,
  }));
  const line = pts.map((p, i) => `${i === 0 ? "M" : "L"}${p.x.toFixed(1)},${p.y.toFixed(1)}`).join(" ");

  return (
    <svg
      viewBox="0 0 300 100"
      preserveAspectRatio="none"
      className="pointer-events-none absolute inset-0 size-full"
      aria-hidden
    >
      <defs>
        <linearGradient id={gid} x1="0" y1="0" x2="0" y2="1">
          <stop offset="0" stopColor="var(--color-acc)" stopOpacity=".22" />
          <stop offset="1" stopColor="var(--color-acc)" stopOpacity="0" />
        </linearGradient>
      </defs>
      <path
        d={`${line} L300,100 L0,100 Z`}
        fill={`url(#${gid})`}
        className="animate-veil"
        style={{ animationDelay: ".45s", animationDuration: ".8s" }}
      />
      {/* pathLength/dasharray = 1 macht den Keyframe längenunabhängig: 1 → 0 zeichnet immer genau die ganze Linie. */}
      <path
        d={line}
        fill="none"
        stroke="var(--color-acc)"
        strokeWidth="1.9"
        strokeLinecap="round"
        strokeLinejoin="round"
        // Kein non-scaling-stroke: der Strich würde dann im Bildschirmraum gestrichelt, während
        // pathLength im Nutzerraum normiert — das Zusammenspiel schnitt das letzte Viertel der
        // Linie ab. Die Kachel skaliert ohnehin fast gleichmäßig, der Strich bleibt gleichmäßig.
        pathLength={1}
        strokeDasharray={1}
        className="animate-draw"
        style={{ animationDelay: ".15s" }}
      />
    </svg>
  );
}

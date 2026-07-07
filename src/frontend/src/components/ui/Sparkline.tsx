/** Flächen-Sparkline im Kachel-Hintergrund (Design: Token-/Review-Kacheln). */
export function Sparkline({ values }: { values: number[] }) {
  if (values.length < 2) return null;
  const max = Math.max(...values, 1);
  const pts = values.map((v, i) => ({
    x: (i / (values.length - 1)) * 300,
    y: 95 - (v / max) * 75,
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
        <linearGradient id="sparkfill" x1="0" y1="0" x2="0" y2="1">
          <stop offset="0" stopColor="#4ADE80" stopOpacity=".28" />
          <stop offset="1" stopColor="#4ADE80" stopOpacity="0" />
        </linearGradient>
      </defs>
      <path d={`${line} L300,100 L0,100 Z`} fill="url(#sparkfill)" />
      <path
        d={line}
        fill="none"
        stroke="#4ADE80"
        strokeWidth="2"
        strokeLinecap="round"
        strokeLinejoin="round"
        vectorEffect="non-scaling-stroke"
      />
    </svg>
  );
}

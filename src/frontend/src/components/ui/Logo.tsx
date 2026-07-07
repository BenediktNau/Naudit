/** Das Naudit-Icon (aus dem Logo-Export) als Inline-SVG, skalierbar. */
export function Logo({ size = 24 }: { size?: number }) {
  return (
    <svg width={size} height={size} viewBox="0 0 64 64" style={{ borderRadius: size * 0.22 }}>
      <rect width="64" height="64" fill="#0D1117" />
      <rect x=".5" y=".5" width="63" height="63" rx={size * 0.35} fill="none" stroke="#242D38" />
      <line x1="16" y1="17" x2="16" y2="46" stroke="#FFF" strokeWidth="6.5" strokeLinecap="round" />
      <path
        d="M16 31 C16 23.5 22.5 21 27.5 21 C34.5 21 38 25.5 38 31.5 L38 46"
        fill="none"
        stroke="#FFF"
        strokeWidth="6.5"
        strokeLinecap="round"
      />
      <rect x="45" y="35.5" width="9" height="12" rx="1.5" fill="#4ADE80" />
    </svg>
  );
}

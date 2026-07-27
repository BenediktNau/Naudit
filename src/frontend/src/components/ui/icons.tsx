/** Die Icons des Entwurfs als reine Pfaddaten — eine Icon-Library wäre für neun Glyphen
 *  mehr Abhängigkeit als Nutzen. Alle im 24er-Raster, Strich, keine Füllung. */
export const ICON = {
  dashboard: "M4 5h6v6H4zM14 5h6v4h-6zM14 13h6v6h-6zM4 15h6v4H4z",
  memory: "M12 3l8 4-8 4-8-4 8-4zM4 12l8 4 8-4M4 17l8 4 8-4",
  analytics: "M3 20h18M7 20v-6M12 20V8M17 20v-11",
  approvals: "M16 19a4 4 0 0 0-8 0M12 11a3.5 3.5 0 1 0 0-7 3.5 3.5 0 0 0 0 7M19.5 19a3.5 3.5 0 0 0-2.8-3.4",
  settings: "M4 7h8M16 7h4M4 17h4M12 17h8M16 7a2 2 0 1 0-4 0 2 2 0 0 0 4 0M10 17a2 2 0 1 0-4 0 2 2 0 0 0 4 0",
  profile: "M20 20a8 8 0 0 0-16 0M12 12a4 4 0 1 0 0-8 4 4 0 0 0 0 8",
  pr: "M6 4v12M6 20a2 2 0 1 0 0-4 2 2 0 0 0 0 4M6 4a2 2 0 1 0 0-4M18 8v8M18 20a2 2 0 1 0 0-4 2 2 0 0 0 0 4M18 8a4 4 0 0 0-4-4h-2",
  repo: "M4 5a2 2 0 0 1 2-2h12v18H6a2 2 0 0 1-2-2zM8 3v18",
  signout: "M15 4h3a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2h-3M10 17l-5-5 5-5M5 12h11",
} as const;

export type IconPath = (typeof ICON)[keyof typeof ICON];

export function Icon({ path, size = 15, className = "" }: { path: string; size?: number; className?: string }) {
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.7"
      strokeLinecap="round"
      strokeLinejoin="round"
      className={`shrink-0 ${className}`}
      aria-hidden
    >
      <path d={path} />
    </svg>
  );
}

/** Lupe und Chevron sitzen nicht im Pfad-Schema oben (Kreis bzw. dickerer Strich). */
export function SearchIcon({ size = 13 }: { size?: number }) {
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="2"
      strokeLinecap="round"
      className="shrink-0"
      aria-hidden
    >
      <circle cx="11" cy="11" r="7" />
      <path d="M20 20l-4.2-4.2" />
    </svg>
  );
}

export function Chevron({ open, className = "" }: { open: boolean; className?: string }) {
  return (
    <svg
      width="12"
      height="12"
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="2.6"
      strokeLinecap="round"
      strokeLinejoin="round"
      className={`shrink-0 transition-[transform,color] duration-300 ease-swift ${open ? "rotate-90 text-acc" : "text-ink4"} ${className}`}
      aria-hidden
    >
      <path d="M9 6l6 6-6 6" />
    </svg>
  );
}

export function CloseIcon({ size = 13 }: { size?: number }) {
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="2.2"
      strokeLinecap="round"
      className="shrink-0"
      aria-hidden
    >
      <path d="M6 6l12 12M18 6L6 18" />
    </svg>
  );
}

/** Eine Quelle für Severity-Farben — vorher dreifach gepflegt (ReviewDetail, Analytics, Memory).
 *  Die APIs liefern die Severity mal groß ("High"), mal klein ("high"); daher wird normalisiert. */

type SevKey = "critical" | "high" | "medium" | "low" | "info";

const PILL: Record<SevKey, string> = {
  critical: "text-danger bg-danger/12",
  high: "text-danger bg-danger/12",
  medium: "text-warn bg-warn/12",
  low: "text-teal bg-teal/12",
  info: "text-ink3 bg-hairline",
};

const BAR: Record<SevKey, string> = {
  critical: "bg-danger",
  high: "bg-danger",
  medium: "bg-warn",
  low: "bg-teal",
  info: "bg-ink3",
};

function key(severity: string): SevKey {
  const k = severity.toLowerCase();
  return k in PILL ? (k as SevKey) : "info";
}

/** Klassen für das Severity-Label an einem Finding. */
export function sevPill(severity: string): string {
  return PILL[key(severity)];
}

/** Füllfarbe des Severity-Balkens in der Auswertung. */
export function sevBar(severity: string): string {
  return BAR[key(severity)];
}

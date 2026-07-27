import type { ReactNode } from "react";

/** Aufklapp-Hülle für Detailzeilen. Der Grid-Trick (grid-template-rows 0fr → 1fr) animiert
 *  auf die echte Inhaltshöhe — anders als max-height braucht er keinen geratenen Grenzwert.
 *  Nur mounten, wenn offen: der Keyframe läuft dann bei jedem Aufklappen neu. */
export function Collapse({ children }: { children: ReactNode }) {
  return (
    <div className="grid overflow-hidden animate-expandrow">
      <div className="min-h-0">{children}</div>
    </div>
  );
}

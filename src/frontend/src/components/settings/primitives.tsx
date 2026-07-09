import { useEffect, type ReactNode } from "react";

/** 30×17px Pill-Switch (handoff §Interactions). Aus: bg-elev/Knopf links; An: bg-acc/25/Knopf rechts. */
export function Toggle({ on, onChange, disabled }: {
  on: boolean; onChange: (v: boolean) => void; disabled?: boolean;
}) {
  return (
    <button
      type="button" role="switch" aria-checked={on} disabled={disabled}
      onClick={() => onChange(!on)}
      className={`relative inline-flex h-[17px] w-[30px] shrink-0 cursor-pointer items-center rounded-full border transition-colors duration-150 disabled:cursor-not-allowed disabled:opacity-50 ${
        on ? "border-acc bg-acc/25" : "border-border bg-elev"
      }`}
    >
      <span
        className={`inline-block size-[13px] rounded-full transition-transform duration-150 ${
          on ? "translate-x-[14px] bg-acc" : "translate-x-[2px] bg-ink3"
        }`}
      />
    </button>
  );
}

/** Auswahl-Karte (Provider/Plattform). Ausgewaehlt: border-acc bg-acc/6; sonst Hover border-ink3. */
export function SelectableCard({ selected, onClick, disabled, children }: {
  selected: boolean; onClick: () => void; disabled?: boolean; children: ReactNode;
}) {
  return (
    <button
      type="button" onClick={onClick} disabled={disabled}
      className={`flex flex-col items-stretch gap-1 rounded-[10px] border p-4 text-left transition-colors disabled:cursor-not-allowed disabled:opacity-50 ${
        selected ? "border-acc bg-acc/6" : "border-border hover:border-ink3"
      }`}
    >
      {children}
    </button>
  );
}

/** Auth-Chip (PAT ↔ App). Ausgewaehlt: border-acc bg-acc/10 text-acc. */
export function AuthChip({ selected, onClick, children }: {
  selected: boolean; onClick: () => void; children: ReactNode;
}) {
  return (
    <button
      type="button" onClick={onClick}
      className={`rounded-full border px-3 py-1 font-mono text-[11px] transition-colors ${
        selected ? "border-acc bg-acc/10 text-acc" : "border-border text-ink2 hover:border-ink3"
      }`}
    >
      {children}
    </button>
  );
}

/** Rechtsbuendiger Status-Hinweis in der Sidebar (Space Mono 11px). */
export function StatusHint({ tone, children }: {
  tone: "acc" | "ink3" | "warn"; children: ReactNode;
}) {
  const cls = tone === "acc" ? "text-acc" : tone === "warn" ? "text-warn" : "text-ink3";
  return <span className={`font-mono text-[11px] ${cls}`}>{children}</span>;
}

/** Modal-Huelle: 560px, ueber abgedunkeltem, weichgezeichnetem Backdrop. Esc/Backdrop schliesst. */
export function Modal({ title, step, onClose, footer, children }: {
  title: string; step?: string; onClose: () => void; footer: ReactNode; children: ReactNode;
}) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === "Escape") onClose(); };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);
  return (
    <div
      className="fixed inset-0 z-50 grid place-items-center bg-[rgba(6,9,13,.6)] p-6 backdrop-blur-sm"
      onClick={onClose}
    >
      <div
        className="anim-modalin flex w-[560px] max-w-full flex-col rounded-[14px] border border-border bg-surface shadow-[0_24px_64px_rgba(0,0,0,.5)]"
        onClick={(e) => e.stopPropagation()}
      >
        <div className="flex items-center justify-between border-b border-hairline px-5 py-4">
          <b className="font-mono text-[15px]">{title}</b>
          {step && <span className="font-mono text-[12px] text-ink3">{step}</span>}
        </div>
        <div className="flex flex-col gap-4 px-5 py-5">{children}</div>
        <div className="flex items-center justify-between border-t border-hairline px-5 py-4">{footer}</div>
      </div>
    </div>
  );
}

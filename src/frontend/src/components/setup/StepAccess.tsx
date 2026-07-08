import { Button } from "@/components/ui/Button";
import type { WizardDraft } from "./shared";

const OPTIONS = [
  {
    mode: "Open" as const,
    title: "Open",
    text: "Every project with a valid webhook secret gets reviewed. Typical for a company GitLab or a private instance.",
  },
  {
    mode: "Registered" as const,
    title: "Registered",
    text: "Only projects of approved accounts get reviewed. Recommended if your instance is publicly reachable.",
  },
];

/** Schritt 5: AccessGate-Modus mit plattformabhaengiger Empfehlung. */
export function StepAccess({ draft, update, onNext }: {
  draft: WizardDraft;
  update: (patch: Partial<WizardDraft>) => void;
  onNext: () => void;
}) {
  const recommended = draft.platform === "GitHub" ? "Registered" : "Open";
  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-col gap-3">
        {OPTIONS.map((o) => (
          <button
            key={o.mode}
            type="button"
            onClick={() => update({ accessGateMode: o.mode })}
            className={`cursor-pointer rounded-xl border px-4 py-3 text-left ${
              draft.accessGateMode === o.mode ? "border-acc" : "border-border hover:border-ink3"
            }`}
          >
            <div className="font-mono text-[13px] font-bold text-ink">
              {o.title}
              {recommended === o.mode && <span className="ml-2 text-[11px] font-normal text-acc">recommended</span>}
            </div>
            <div className="mt-1 text-[11.5px] text-ink3">{o.text}</div>
          </button>
        ))}
      </div>
      <Button onClick={onNext} className="w-full py-3">
        Continue
      </Button>
    </div>
  );
}

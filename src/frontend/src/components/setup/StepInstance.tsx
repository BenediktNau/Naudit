import { Button } from "@/components/ui/Button";
import { Field, inputCls, type WizardDraft } from "./shared";

/** Schritt 2: oeffentliche Basis-URL (fuer Webhook-URLs; PR 3 nutzt sie auch fuer den
 *  GitHub-Manifest-Redirect). Aus dem Request vorbefuellt, editierbar. */
export function StepInstance({ draft, update, onNext }: {
  draft: WizardDraft;
  update: (patch: Partial<WizardDraft>) => void;
  onNext: () => void;
}) {
  const valid = /^https?:\/\/.+/.test(draft.publicBaseUrl);
  return (
    <div className="flex flex-col gap-4">
      <Field
        label="Public base URL"
        hint="The URL your git platform can reach Naudit at — used to build the webhook URLs."
      >
        <input
          className={inputCls}
          placeholder="https://naudit.example.com"
          value={draft.publicBaseUrl}
          onChange={(e) => update({ publicBaseUrl: e.target.value })}
        />
      </Field>
      <Button onClick={onNext} disabled={!valid} className="w-full py-3">
        Continue
      </Button>
    </div>
  );
}

import type { SettingsCtx } from "../model";
import { MergeGatePanel } from "./review/MergeGatePanel";
import { RoundtripPanel } from "./review/RoundtripPanel";
import { PromptPanel } from "./review/PromptPanel";
import { CompanyGuidelinesPanel } from "./review/CompanyGuidelinesPanel";
import { SastPanel } from "./review/SastPanel";

/** Kategorie "Review rules": Gate/Roundtrips/Prompt/Firmen-Guidelines plus das Scan-Panel. */
export function ReviewCategory({ ctx }: { ctx: SettingsCtx }) {
  return (
    <>
      <MergeGatePanel ctx={ctx} />
      <RoundtripPanel ctx={ctx} />
      <PromptPanel ctx={ctx} />
      <CompanyGuidelinesPanel ctx={ctx} />
      <SastPanel ctx={ctx} />
    </>
  );
}

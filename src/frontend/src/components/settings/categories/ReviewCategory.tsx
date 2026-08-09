import type { SettingsCtx } from "../model";
import { MergeGatePanel } from "./review/MergeGatePanel";
import { RoundtripPanel } from "./review/RoundtripPanel";
import { PromptPanel } from "./review/PromptPanel";
import { SastPanel } from "./review/SastPanel";

/** Kategorie "Review rules": Gate/Roundtrips/Prompt plus das Scan-Panel. */
export function ReviewCategory({ ctx }: { ctx: SettingsCtx }) {
  return (
    <>
      <MergeGatePanel ctx={ctx} />
      <RoundtripPanel ctx={ctx} />
      <PromptPanel ctx={ctx} />
      <SastPanel ctx={ctx} />
    </>
  );
}

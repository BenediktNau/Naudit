import { useMemo, useState } from "react";
import { useRestartApp, useSaveSettings, useSettings } from "@/hooks/queries";
import { Button } from "@/components/ui/Button";
import { Skeleton, SkeletonPanel } from "@/components/ui/Skeleton";
import type { SettingItem } from "@/api/types";
import { SettingsSidebar } from "@/components/settings/SettingsSidebar";
import { RawKeys } from "@/components/settings/RawKeys";
import { InstanceCategory } from "@/components/settings/categories/InstanceCategory";
import { GitCategory } from "@/components/settings/categories/GitCategory";
import { AiCategory } from "@/components/settings/categories/AiCategory";
import { ReviewCategory } from "@/components/settings/categories/ReviewCategory";
import { SignInCategory } from "@/components/settings/categories/SignInCategory";
import { computeHints } from "@/components/settings/hints";
import { CATEGORIES, type CategoryId, type SettingsCtx, type WizardState } from "@/components/settings/model";

function SettingsSkeleton() {
  return (
    <div className="flex min-h-[70vh]">
      <div className="w-[230px] shrink-0 border-r border-hairline px-[14px] py-5">
        <Skeleton className="mb-4 h-3 w-16" />
        {Array.from({ length: 5 }, (_, i) => <Skeleton key={i} className="mb-2 h-8 w-full" />)}
      </div>
      <div className="flex-1 px-8 py-7">
        <Skeleton className="h-6 w-40" />
        <Skeleton className="mt-2 h-3 w-96" />
        <div className="mt-5"><SkeletonPanel /></div>
      </div>
    </div>
  );
}

/** Editierbar (Admin): schreibt in die DB; env-gesetzte Keys sind gesperrt. Aenderungen gelten
 *  erst nach dem Neustart — Banner + Restart-Button. Secrets sind write-only. */
export function SettingsPage() {
  const { data, isLoading } = useSettings();
  const save = useSaveSettings();
  const restart = useRestartApp();
  const [drafts, setDrafts] = useState<Record<string, string>>({});
  const [active, setActive] = useState<CategoryId>("instance");
  const [rawMode, setRawMode] = useState<boolean>(() => localStorage.getItem("naudit.settings.rawMode") === "1");
  // wizard selbst wird erst ab Task 8 gerendert (Sign-in-Wizard-Modal); Unterstrich vermeidet den unused-var-Lint bis dahin.
  const [_wizard, setWizard] = useState<WizardState>(null);

  const byKey = useMemo(() => {
    const m = new Map<string, SettingItem>();
    for (const s of data?.settings ?? []) m.set(s.key, s);
    return m;
  }, [data]);

  const ctx: SettingsCtx = useMemo(() => ({
    get: (k) => drafts[k] ?? byKey.get(k)?.value ?? "",
    set: (k, v) => setDrafts((d) => ({ ...d, [k]: v })),
    locked: (k) => byKey.get(k)?.editable === false,
    secretSet: (k) => byKey.get(k)?.isSet ?? false,
    openWizard: (w) => setWizard(w),
  }), [drafts, byKey]);

  const dirty = Object.keys(drafts).length > 0;
  const toggleRaw = (v: boolean) => { setRawMode(v); localStorage.setItem("naudit.settings.rawMode", v ? "1" : "0"); };

  if (isLoading || !data) return <SettingsSkeleton />;

  const onSave = () => {
    const changes = Object.entries(drafts).map(([key, value]) => ({ key, value: value === "" ? null : value }));
    save.mutate(changes, { onSuccess: () => setDrafts({}) });
  };

  const hints = computeHints(ctx);
  const activeMeta = CATEGORIES.find((c) => c.id === active)!;

  return (
    <div className="flex min-h-[70vh]">
      <SettingsSidebar active={active} onSelect={setActive} rawMode={rawMode} onToggleRaw={toggleRaw} hints={hints} />
      <div className="flex-1 px-8 py-7">
        <div className="flex items-start justify-between gap-4">
          <div>
            <h2 className="font-mono text-[18px] font-bold">{rawMode ? "Raw keys" : activeMeta.title}</h2>
            {!rawMode && <p className="mt-1 max-w-[56ch] text-[13px] text-ink2">{activeMeta.blurb}</p>}
          </div>
          <Button onClick={onSave} disabled={!dirty || save.isPending} className="shrink-0 px-3 py-1.5 text-[12.5px]">
            {save.isPending ? "saving…" : "Save changes"}
          </Button>
        </div>

        {data.recoveryError && (
          <div className="mt-4 rounded border border-danger/40 bg-danger/10 px-4 py-3 text-[12.5px] text-danger">
            <b>Recovery mode:</b> {data.recoveryError} — reviews are paused until fixed &amp; restarted.
          </div>
        )}
        {data.warnings.map((w) => (
          <div key={w} className="mt-4 rounded border border-warn/40 bg-warn/10 px-4 py-3 text-[12.5px] text-warn">{w}</div>
        ))}
        {data.restartPending && (
          <div className="mt-4 flex items-center justify-between rounded border border-hairline bg-elev px-4 py-3 text-[12.5px] text-ink2">
            <span>Pending changes — restart Naudit to apply.</span>
            <Button variant="secondary" onClick={() => restart.mutate()} disabled={restart.isPending} className="px-3 py-1 text-[12.5px]">
              {restart.isPending ? "restarting…" : "Restart now"}
            </Button>
          </div>
        )}
        {save.isError && (
          <div className="mt-4 rounded border border-danger/40 bg-danger/10 px-4 py-3 text-[12.5px] text-danger">
            Couldn't save settings: {save.error?.message ?? "unknown error"}
          </div>
        )}
        {restart.isError && (
          <div className="mt-4 rounded border border-danger/40 bg-danger/10 px-4 py-3 text-[12.5px] text-danger">
            Restart failed: {restart.error?.message ?? "unknown error"}
          </div>
        )}

        <div className="mt-5">
          {rawMode ? (
            <RawKeys items={data.settings} ctx={ctx} />
          ) : (
            <div key={active} className="anim-fadein flex flex-col gap-5">
              {active === "instance" && <InstanceCategory ctx={ctx} />}
              {active === "git" && <GitCategory ctx={ctx} />}
              {active === "ai" && <AiCategory ctx={ctx} />}
              {active === "review" && <ReviewCategory ctx={ctx} />}
              {active === "signin" && <SignInCategory ctx={ctx} />}
            </div>
          )}
        </div>
      </div>
    </div>
  );
}

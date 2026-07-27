import { useMemo, useState } from "react";
import { useRestartApp, useSaveSettings, useSettings } from "@/hooks/queries";
import { Button } from "@/components/ui/Button";
import { PageHeader } from "@/components/ui/PageHeader";
import { Skeleton, SkeletonPanel } from "@/components/ui/Skeleton";
import type { SettingItem } from "@/api/types";
import { SettingsSidebar } from "@/components/settings/SettingsSidebar";
import { RawKeys } from "@/components/settings/RawKeys";
import { InstanceCategory } from "@/components/settings/categories/InstanceCategory";
import { GitCategory } from "@/components/settings/categories/GitCategory";
import { AiCategory } from "@/components/settings/categories/AiCategory";
import { ReviewCategory } from "@/components/settings/categories/ReviewCategory";
import { SignInCategory } from "@/components/settings/categories/SignInCategory";
import { SignInWizard } from "@/components/settings/wizards/SignInWizard";
import { computeHints } from "@/components/settings/hints";
import { CATEGORIES, type CategoryId, type SettingsCtx, type WizardState } from "@/components/settings/model";

const banner = "rounded-[11px] border px-4 py-3 text-[12.5px]";

function SettingsSkeleton() {
  return (
    <>
      <Skeleton className="h-5 w-28" />
      <Skeleton className="mt-2 h-3 w-80" />
      <div className="mt-5 flex flex-wrap gap-5">
        <div className="basis-[200px] shrink-0">
          {Array.from({ length: 5 }, (_, i) => (
            <Skeleton key={i} className="mb-1.5 h-9 w-full" />
          ))}
        </div>
        <div className="min-w-0 flex-1">
          <SkeletonPanel />
        </div>
      </div>
    </>
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
  const [wizard, setWizard] = useState<WizardState>(null);

  const byKey = useMemo(() => {
    const m = new Map<string, SettingItem>();
    for (const s of data?.settings ?? []) m.set(s.key, s);
    return m;
  }, [data]);

  const ctx: SettingsCtx = useMemo(
    () => ({
      get: (k) => drafts[k] ?? byKey.get(k)?.value ?? "",
      set: (k, v) => setDrafts((d) => ({ ...d, [k]: v })),
      locked: (k) => byKey.get(k)?.editable === false,
      secretSet: (k) => byKey.get(k)?.isSet ?? false,
      options: (k) => byKey.get(k)?.allowedValues ?? [],
      openWizard: (w) => setWizard(w),
    }),
    [drafts, byKey],
  );

  // Nur echte Abweichungen zählen: getippt-und-wieder-zurückgesetzt ist keine Änderung.
  // (Secrets tragen per Kontrakt einen leeren Wert — dort greift dieselbe Regel richtig.)
  const dirtyCount = Object.entries(drafts).filter(([k, v]) => v !== (byKey.get(k)?.value ?? "")).length;
  const toggleRaw = (v: boolean) => {
    setRawMode(v);
    localStorage.setItem("naudit.settings.rawMode", v ? "1" : "0");
  };

  if (isLoading || !data) return <SettingsSkeleton />;

  const onSave = () => {
    // Leerer Draft = Reset auf Default (null). Ausnahme: ein leer gelassenes Secret NICHT senden —
    // sonst löscht "getippt, dann geleert" das gespeicherte Secret (das Feld ist per Kontrakt ohnehin leer).
    const changes = Object.entries(drafts)
      .filter(([key, value]) => !(value === "" && byKey.get(key)?.isSecret))
      .map(([key, value]) => ({ key, value: value === "" ? null : value }));
    save.mutate(changes, { onSuccess: () => setDrafts({}) });
  };

  const hints = computeHints(ctx);
  const activeMeta = CATEGORIES.find((c) => c.id === active)!;
  const base = ctx.get("Naudit:PublicBaseUrl").replace(/\/+$/, "");

  return (
    <>
      <PageHeader title="Settings" subtitle="Changes apply after a restart. Keys set by environment are locked." />

      <div className="flex flex-col gap-3">
        {data.recoveryError && (
          <div className={`${banner} border-danger/40 bg-danger/10 text-danger`}>
            <b>Recovery mode:</b> {data.recoveryError} — reviews are paused until fixed &amp; restarted.
          </div>
        )}
        {data.warnings.map((w) => (
          <div key={w} className={`${banner} border-warn/40 bg-warn/10 text-warn`}>
            {w}
          </div>
        ))}
        {data.restartPending && (
          <div className={`${banner} flex flex-wrap items-center justify-between gap-3 border-hairline bg-surface text-ink2`}>
            <span>Pending changes — restart Naudit to apply.</span>
            <Button
              variant="secondary"
              onClick={() => restart.mutate()}
              disabled={restart.isPending}
              className="px-3 py-1 text-[12.5px]"
            >
              {restart.isPending ? "restarting…" : "Restart now"}
            </Button>
          </div>
        )}
        {save.isError && (
          <div className={`${banner} border-danger/40 bg-danger/10 text-danger`}>
            Couldn't save settings: {save.error?.message ?? "unknown error"}
          </div>
        )}
        {restart.isError && (
          <div className={`${banner} border-danger/40 bg-danger/10 text-danger`}>
            Restart failed: {restart.error?.message ?? "unknown error"}
          </div>
        )}
      </div>

      <div className="mt-4 flex flex-col gap-5 lg:flex-row lg:items-start">
        <SettingsSidebar active={active} onSelect={setActive} rawMode={rawMode} onToggleRaw={toggleRaw} hints={hints} />

        <div className="min-w-0 flex-1">
          <div className="mb-4">
            <h2 className="text-[14px] font-semibold text-ink">{rawMode ? "Raw keys" : activeMeta.title}</h2>
            <p className="mt-1 max-w-[58ch] text-[12px] leading-relaxed text-ink3">
              {rawMode ? "Every setting as its configuration key. For debugging and one-off overrides." : activeMeta.blurb}
            </p>
          </div>
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

      {/* Speicherleiste erscheint erst, wenn es etwas zu speichern gibt — sie klebt am unteren Rand,
          damit sie auch am Ende einer langen Kategorie erreichbar bleibt. */}
      {dirtyCount > 0 && (
        <div className="sticky bottom-0 z-30 -mx-5 mt-5 flex animate-risein flex-wrap items-center gap-3.5 border-t border-border bg-input px-5 py-3 md:-mx-6 md:px-6">
          <span className="size-1.5 animate-livedot rounded-full bg-warn" aria-hidden />
          <span className="text-[12.5px] text-ink2">
            {dirtyCount} unsaved {dirtyCount === 1 ? "change" : "changes"} · applies after restart
          </span>
          <div className="ml-auto flex gap-2">
            <Button variant="secondary" onClick={() => setDrafts({})} disabled={save.isPending} className="px-3.5 py-1.5 text-[12.5px]">
              Discard
            </Button>
            <Button onClick={onSave} loading={save.isPending} className="px-4 py-1.5 text-[12.5px]">
              Save changes
            </Button>
          </div>
        </div>
      )}

      {wizard && <SignInWizard state={wizard} ctx={ctx} base={base} onClose={() => setWizard(null)} />}
    </>
  );
}

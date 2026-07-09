import { useMemo, useState } from "react";
import { useRestartApp, useSaveSettings, useSettings } from "@/hooks/queries";
import { Panel } from "@/components/ui/Panel";
import { Pill } from "@/components/ui/Pill";
import { Button } from "@/components/ui/Button";
import type { SettingItem } from "@/api/types";
import { Skeleton, SkeletonPanel } from "@/components/ui/Skeleton";

/** Gruppierung + Reihenfolge der Panels; Keys wie im Backend-Katalog. */
const GROUPS: { title: string; extra: string; prefixes: string[] }[] = [
  { title: "General", extra: "Naudit", prefixes: ["Naudit:PublicBaseUrl", "Naudit:AccessGate:"] },
  { title: "Git platform", extra: "Naudit:Git*", prefixes: ["Naudit:Git:", "Naudit:GitLab:", "Naudit:GitHub:"] },
  { title: "AI provider", extra: "Naudit:Ai", prefixes: ["Naudit:Ai:"] },
  { title: "Review", extra: "Naudit:Review", prefixes: ["Naudit:Review:"] },
  { title: "Sign-in", extra: "Naudit:Ui:Auth", prefixes: ["Naudit:Ui:Auth:"] },
];

/** Enum-Keys werden als Select gerendert statt als Freitext. */
const ENUMS: Record<string, string[]> = {
  "Naudit:Git:Platform": ["GitLab", "GitHub"],
  "Naudit:GitHub:Auth": ["Pat", "App"],
  "Naudit:Ai:Provider": ["Ollama", "Anthropic", "OpenAICompatible", "ClaudeCode"],
  "Naudit:AccessGate:Mode": ["Open", "Registered"],
  "Naudit:Review:Gate:MinSeverity": ["Info", "Low", "Medium", "High", "Critical"],
  "Naudit:Review:Gate:MinConfidence": ["Low", "Medium", "High"],
  "Naudit:Ui:Auth:GitHub:Enabled": ["false", "true"],
  "Naudit:Ui:Auth:Oidc:Enabled": ["false", "true"],
};

function SettingRow({ item, draft, onChange }: {
  item: SettingItem;
  draft: string | undefined;
  onChange: (v: string) => void;
}) {
  const label = item.key.replace(/^Naudit:/, "");
  const current = draft ?? item.value ?? "";
  const options = ENUMS[item.key];
  return (
    <div className="flex items-center justify-between gap-4 border-b border-hairline px-5 py-3 last:border-b-0">
      <span className="flex items-center gap-2 text-[13px] font-medium text-ink">
        {label}
        {item.source === "env" && <Pill kind="neutral">via environment</Pill>}
        {item.source === "db" && <Pill kind="ok">db</Pill>}
      </span>
      {!item.editable ? (
        <span className="font-mono text-[12.5px] text-ink3">{item.isSecret ? "•••" : (item.value ?? "—")}</span>
      ) : options ? (
        <select
          className="rounded border border-hairline bg-transparent px-2 py-1 font-mono text-[12.5px] text-ink2"
          value={current} onChange={(e) => onChange(e.target.value)}
        >
          <option value="">(default)</option>
          {options.map((o) => <option key={o} value={o}>{o}</option>)}
        </select>
      ) : (
        <input
          type={item.isSecret ? "password" : "text"}
          className="w-64 rounded border border-hairline bg-transparent px-2 py-1 font-mono text-[12.5px] text-ink2"
          placeholder={item.isSecret ? (item.isSet ? "•••••• (set)" : "not set") : ""}
          value={draft ?? (item.isSecret ? "" : (item.value ?? ""))}
          onChange={(e) => onChange(e.target.value)}
        />
      )}
    </div>
  );
}

// Skeleton: Titelblock + drei Config-Panels mit je drei Zeilen (Label ↔ Wert).
function SettingsPanelSkeleton({ rows }: { rows: number }) {
  return (
    <SkeletonPanel>
      {Array.from({ length: rows }, (_, i) => (
        <div key={i} className="flex items-center justify-between border-b border-hairline px-5 py-3.5 last:border-b-0">
          <Skeleton className="h-3 w-28" />
          <Skeleton className="h-3 w-16" />
        </div>
      ))}
    </SkeletonPanel>
  );
}

function SettingsSkeleton() {
  return (
    <div className="flex flex-col gap-5 px-7 py-6">
      <div>
        <Skeleton className="h-6 w-32" />
        <Skeleton className="mt-2 h-3 w-full max-w-[70ch]" />
      </div>
      <div className="grid grid-cols-1 items-start gap-4 md:grid-cols-2">
        <SettingsPanelSkeleton rows={3} />
        <SettingsPanelSkeleton rows={3} />
        <SettingsPanelSkeleton rows={3} />
      </div>
    </div>
  );
}

/** Editierbar (Admin): schreibt in die DB; env-gesetzte Keys sind gesperrt. Änderungen
 *  gelten erst nach dem Neustart — Banner + Restart-Button. Secrets sind write-only. */
export function SettingsPage() {
  const { data, isLoading } = useSettings();
  const save = useSaveSettings();
  const restart = useRestartApp();
  const [drafts, setDrafts] = useState<Record<string, string>>({});

  const dirty = useMemo(() => Object.keys(drafts).length > 0, [drafts]);
  if (isLoading || !data) return <SettingsSkeleton />;

  const onSave = () => {
    const changes = Object.entries(drafts).map(([key, value]) => ({ key, value: value === "" ? null : value }));
    save.mutate(changes, { onSuccess: () => setDrafts({}) });
  };

  return (
    <div className="flex flex-col gap-5 px-7 py-6">
      <div className="flex items-start justify-between">
        <div>
          <h2 className="font-mono text-lg font-bold">Settings</h2>
          <p className="mt-1 max-w-[70ch] text-[12.5px] text-ink3">
            Stored in Naudit's database. Keys set via environment are locked (environment always wins).
            Changes take effect after a restart. Clearing a field resets it to its default.
          </p>
        </div>
        <div className="flex gap-2">
          <Button onClick={onSave} disabled={!dirty || save.isPending} className="px-3 py-1.5 text-[12.5px]">
            {save.isPending ? "saving…" : "Save changes"}
          </Button>
        </div>
      </div>

      {data.recoveryError && (
        <div className="rounded border border-danger/40 bg-danger/10 px-4 py-3 text-[12.5px] text-danger">
          <b>Recovery mode:</b> {data.recoveryError} — reviews are paused until fixed &amp; restarted.
        </div>
      )}
      {data.warnings.map((w) => (
        <div key={w} className="rounded border border-warn/40 bg-warn/10 px-4 py-3 text-[12.5px] text-warn">{w}</div>
      ))}
      {data.restartPending && (
        <div className="flex items-center justify-between rounded border border-hairline bg-elev px-4 py-3 text-[12.5px] text-ink2">
          <span>Pending changes — restart Naudit to apply.</span>
          <Button variant="secondary" onClick={() => restart.mutate()} disabled={restart.isPending} className="px-3 py-1 text-[12.5px]">
            {restart.isPending ? "restarting…" : "Restart now"}
          </Button>
        </div>
      )}
      {save.isError && (
        <div className="rounded border border-danger/40 bg-danger/10 px-4 py-3 text-[12.5px] text-danger">
          Couldn't save settings: {save.error?.message ?? "unknown error"}
        </div>
      )}
      {restart.isError && (
        <div className="rounded border border-danger/40 bg-danger/10 px-4 py-3 text-[12.5px] text-danger">
          Restart failed: {restart.error?.message ?? "unknown error"}
        </div>
      )}

      <div className="grid grid-cols-1 items-start gap-4 md:grid-cols-2">
        {GROUPS.map((g) => (
          <Panel key={g.title} title={g.title} extra={g.extra}>
            {data.settings
              .filter((s) => g.prefixes.some((p) => s.key === p || s.key.startsWith(p)))
              .map((s) => (
                <SettingRow key={s.key} item={s} draft={drafts[s.key]}
                  onChange={(v) => setDrafts((d) => ({ ...d, [s.key]: v }))} />
              ))}
          </Panel>
        ))}
      </div>
    </div>
  );
}

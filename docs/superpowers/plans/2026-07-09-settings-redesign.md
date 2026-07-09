# Settings Page Redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the flat key/value `SettingsPage` with the guided, category-based redesign from the handoff (section 2a) — sidebar nav, conditional fields, Instance/Review/Sign-in categories, a GitHub sign-in wizard, and a Raw-keys expert toggle — frontend-only.

**Architecture:** `SettingsPage.tsx` becomes a thin orchestrator holding `drafts`/`activeCategory`/`rawMode`/`wizard` state and a `byKey` data seam; content lives in `components/settings/` (sidebar, five category panes, raw view, wizard modal, primitives). No backend/API/catalog change — the existing `useSettings`/`useSaveSettings`/`useRestartApp` hooks and `SettingsCatalog` are reused verbatim.

**Tech Stack:** React 19 + TypeScript + Tailwind CSS v4 + TanStack Query (existing). No new dependency. Design tokens from `src/frontend/src/index.css`.

## Global Constraints

- **UI copy is English**, centralized-ready for later i18n (verbatim from spec). Code comments are **German** (repo convention).
- **Style with Tailwind token classes only** (`bg-surface`, `border-hairline`, `text-ink2`, `text-acc`, `bg-acc/12`, …) — **never raw hex**. Exact colors/spacing/type per `docs/design_handoff_settings_redesign/README.md`.
- **No new npm dependency.** Animations are CSS-only and respect `prefers-reduced-motion` (`motion-reduce:animate-none`).
- **Save semantics unchanged:** empty string ⇒ `null` (reset to default). Secrets are write-only (API returns `value: null`, `isSet` flag). Env-locked keys (`editable === false`) are non-editable in every view.
- **Frontend verification = lint + build** (no unit-test harness). Each task ends with `cd src/frontend && npm run lint && npm run build` green, then a commit. The final task also runs `dotnet test Naudit.slnx`.
- Branch `feat/settings-redesign` (already created off `main`; spec + handoff committed).
- Real OAuth callback path is **`/auth/callback/github`** (from `Program.cs`), not the handoff's `/auth/github/callback`.

**Reference for exact visuals:** `docs/design_handoff_settings_redesign/README.md` — sections are cited per task as *(handoff §N)*.

---

### Task 1: CSS keyframes + settings primitives

**Files:**
- Modify: `src/frontend/src/index.css` (append keyframes after the `@theme` block)
- Create: `src/frontend/src/components/settings/primitives.tsx`

**Interfaces:**
- Produces:
  - `Toggle({ on: boolean; onChange: (v: boolean) => void; disabled?: boolean }): JSX` — 30×17px pill switch (handoff §Interactions "Toggles/switches").
  - `Modal({ title: string; step?: string; onClose: () => void; footer: ReactNode; children: ReactNode }): JSX` — 560px centered modal over a dimmed blurred backdrop; `Esc`/backdrop-click calls `onClose`.
  - `SelectableCard({ selected: boolean; onClick: () => void; disabled?: boolean; children: ReactNode }): JSX` — bordered card, selected ⇒ `border-acc bg-acc/6`, unselected hover ⇒ `border-ink3`.
  - `AuthChip({ selected: boolean; onClick: () => void; children: ReactNode }): JSX` — pill chip, selected ⇒ `border-acc bg-acc/10 text-acc`.
  - `StatusHint({ tone: "acc" | "ink3" | "warn"; children: ReactNode }): JSX` — Space Mono 11px right-aligned sidebar hint.

- [ ] **Step 1: Add keyframes to `index.css`**

Append after the `@theme { … }` block:

```css
/* Dezente Transitions fuer den Settings-Redesign — respektieren prefers-reduced-motion. */
@keyframes naudit-fadein {
  from { opacity: 0; transform: translateY(4px); }
  to   { opacity: 1; transform: translateY(0); }
}
@keyframes naudit-modalin {
  from { opacity: 0; transform: scale(.97); }
  to   { opacity: 1; transform: scale(1); }
}
@media (prefers-reduced-motion: no-preference) {
  .anim-fadein  { animation: naudit-fadein .15s ease both; }
  .anim-modalin { animation: naudit-modalin .15s ease both; }
}
```

- [ ] **Step 2: Create `primitives.tsx`**

```tsx
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
```

- [ ] **Step 3: Verify lint + build**

Run: `cd src/frontend && npm run lint && npm run build`
Expected: PASS (no errors; primitives are exported but unused so far — allowed).

- [ ] **Step 4: Commit**

```bash
git add src/frontend/src/index.css src/frontend/src/components/settings/primitives.tsx
git commit -m "feat(settings): CSS-Keyframes + UI-Primitives (Toggle, Modal, Cards, Chips)"
```

---

### Task 2: Orchestrator shell — SettingsPage rewrite, Sidebar, Raw view, category stubs

This task moves today's flat view into `RawKeys.tsx` (nothing lost), builds the two-pane shell + sidebar, and stubs the five category panes so the app builds and is navigable. Categories are fleshed out in Tasks 3–7.

**Files:**
- Rewrite: `src/frontend/src/components/pages/SettingsPage.tsx`
- Create: `src/frontend/src/components/settings/SettingsSidebar.tsx`
- Create: `src/frontend/src/components/settings/RawKeys.tsx`
- Create: `src/frontend/src/components/settings/categories/{Instance,Git,Ai,Review,SignIn}Category.tsx` (stubs)
- Create: `src/frontend/src/components/settings/model.ts` (shared types + helpers)

**Interfaces:**
- Produces (`model.ts`):
  - `type CategoryId = "instance" | "git" | "ai" | "review" | "signin"`
  - `type WizardState = null | { kind: "github-signin" | "github-app" | "oidc"; step: 1 | 2 | 3 }`
  - `interface SettingsCtx { get(key: string): string; set(key: string, value: string): void; locked(key: string): boolean; secretSet(key: string): boolean; openWizard(w: NonNullable<WizardState>): void; }`
  - `const CATEGORIES: { id: CategoryId; label: string; title: string; blurb: string }[]`
- Produces (`RawKeys.tsx`): `RawKeys({ items, ctx }: { items: SettingItem[]; ctx: SettingsCtx }): JSX`
- Produces (`SettingsSidebar.tsx`): `SettingsSidebar({ active, onSelect, rawMode, onToggleRaw, hints }: { active: CategoryId; onSelect: (c: CategoryId) => void; rawMode: boolean; onToggleRaw: (v: boolean) => void; hints: Record<CategoryId, { tone: "acc"|"ink3"|"warn"; text: string }> }): JSX`
- Produces (each category stub): e.g. `AiCategory({ ctx }: { ctx: SettingsCtx }): JSX` — Tasks 3–7 rely on this exact prop shape.
- Consumes: `useSettings`, `useSaveSettings`, `useRestartApp` (`@/hooks/queries`); `SettingItem`, `SettingsDto` (`@/api/types`).

- [ ] **Step 1: Create `model.ts`**

```ts
import type { ReactNode } from "react";

export type CategoryId = "instance" | "git" | "ai" | "review" | "signin";

export type WizardState = null | { kind: "github-signin" | "github-app" | "oidc"; step: 1 | 2 | 3 };

/** Zugriffs-Seam, den jede Kategorie bekommt. get() liest Draft → Wert → "". */
export interface SettingsCtx {
  get(key: string): string;
  set(key: string, value: string): void;
  locked(key: string): boolean;   // env-gesetzt (editable === false)
  secretSet(key: string): boolean;
  openWizard(w: NonNullable<WizardState>): void;
}

export interface CategoryMeta { id: CategoryId; label: string; title: string; blurb: string; }

/** Reihenfolge + Kopf-Texte der Kategorien (handoff §Screens). */
export const CATEGORIES: CategoryMeta[] = [
  { id: "instance", label: "Instance",     title: "Instance",     blurb: "Where Naudit lives and which repos it will review." },
  { id: "git",      label: "Git platform", title: "Git platform", blurb: "Connect the platform Naudit reads diffs from and posts reviews to." },
  { id: "ai",       label: "AI provider",  title: "AI provider",  blurb: "Pick the LLM that reviews your code. Only the fields it needs appear." },
  { id: "review",   label: "Review rules", title: "Review rules", blurb: "When a review blocks a merge, and the prompt that guides it." },
  { id: "signin",   label: "Sign-in",      title: "Sign-in",      blurb: "How admins and users sign in to this dashboard." },
];

export type ReactChildren = ReactNode; // Re-Export-Bequemlichkeit fuer Kategorien
```

- [ ] **Step 2: Create `RawKeys.tsx` (today's SettingRow + filter + dynamic count)**

Move the existing `ENUMS` map and `SettingRow` from the old `SettingsPage.tsx` into this file, driven by `ctx`. Add a filter input and dynamic count.

```tsx
import { useState } from "react";
import type { SettingItem } from "@/api/types";
import { Pill } from "@/components/ui/Pill";
import type { SettingsCtx } from "./model";

/** Enum-Keys als Select statt Freitext (aus der alten SettingsPage uebernommen). */
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

function RawRow({ item, ctx }: { item: SettingItem; ctx: SettingsCtx }) {
  const label = item.key.replace(/^Naudit:/, "");
  const options = ENUMS[item.key];
  return (
    <div className="flex items-center justify-between gap-4 border-b border-hairline px-5 py-3 last:border-b-0">
      <span className="flex items-center gap-2 text-[13px] font-medium text-ink">
        {label}
        {item.source === "env" && <Pill kind="neutral">via environment</Pill>}
        {item.source === "db" && <Pill kind="ok">db</Pill>}
      </span>
      {!item.editable ? (
        <span className="font-mono text-[12.5px] text-ink3">{item.isSecret ? "•••" : item.value ?? "—"}</span>
      ) : options ? (
        <select
          className="w-[300px] rounded border border-hairline bg-transparent px-2 py-1 font-mono text-[12.5px] text-ink2"
          value={ctx.get(item.key)} onChange={(e) => ctx.set(item.key, e.target.value)}
        >
          <option value="">(default)</option>
          {options.map((o) => <option key={o} value={o}>{o}</option>)}
        </select>
      ) : (
        <input
          type={item.isSecret ? "password" : "text"}
          className="w-[300px] rounded border border-hairline bg-transparent px-2 py-1 font-mono text-[12.5px] text-ink2"
          placeholder={item.isSecret ? (item.isSet ? "•••••• (set)" : "not set") : ""}
          value={ctx.get(item.key)} onChange={(e) => ctx.set(item.key, e.target.value)}
        />
      )}
    </div>
  );
}

/** Expertenansicht: flacher Katalog, gefiltert. Editiert denselben drafts-State wie die Kategorien. */
export function RawKeys({ items, ctx }: { items: SettingItem[]; ctx: SettingsCtx }) {
  const [filter, setFilter] = useState("");
  const shown = items.filter((i) => i.key.toLowerCase().includes(filter.toLowerCase()));
  return (
    <div className="flex flex-col gap-4 anim-fadein">
      <div className="flex items-start justify-between gap-4">
        <div>
          <h2 className="font-mono text-[18px] font-bold">Raw keys</h2>
          <p className="mt-1 max-w-[56ch] text-[13px] text-ink2">
            Every DB-managed key, exactly as in the config. Keys set via environment are locked —
            environment always wins.
          </p>
        </div>
        <input
          value={filter} onChange={(e) => setFilter(e.target.value)} placeholder="⌕ filter keys…"
          className="w-[240px] rounded-lg border border-border bg-bg px-3 py-2 font-mono text-[12.5px] text-ink outline-none placeholder:text-ink3 focus:border-acc"
        />
      </div>
      <p className="font-mono text-[11px] text-ink3">Showing all {items.length} DB-managed keys</p>
      <div className="overflow-hidden rounded-xl border border-hairline bg-surface">
        {shown.map((i) => <RawRow key={i.key} item={i} ctx={ctx} />)}
      </div>
    </div>
  );
}
```

- [ ] **Step 3: Create `SettingsSidebar.tsx`**

```tsx
import { Toggle, StatusHint } from "./primitives";
import { CATEGORIES, type CategoryId } from "./model";

export function SettingsSidebar({ active, onSelect, rawMode, onToggleRaw, hints }: {
  active: CategoryId;
  onSelect: (c: CategoryId) => void;
  rawMode: boolean;
  onToggleRaw: (v: boolean) => void;
  hints: Record<CategoryId, { tone: "acc" | "ink3" | "warn"; text: string }>;
}) {
  return (
    <aside className="flex w-[230px] shrink-0 flex-col justify-between border-r border-hairline px-[14px] py-5">
      <div>
        <div className="mb-2 px-3 font-mono text-[11px] font-bold uppercase tracking-[0.12em] text-ink3">Settings</div>
        <nav className="flex flex-col gap-0.5">
          {CATEGORIES.map((c) => {
            const on = active === c.id && !rawMode;
            return (
              <button
                key={c.id} type="button" onClick={() => onSelect(c.id)}
                className={`flex items-center justify-between rounded-lg px-3 py-2.5 text-[13px] transition-colors ${
                  on ? "bg-acc/12 font-semibold text-acc"
                     : `font-medium ${rawMode ? "text-ink3" : "text-ink2 hover:text-ink"}`
                }`}
              >
                <span>{c.label}</span>
                <StatusHint tone={hints[c.id].tone}>{hints[c.id].text}</StatusHint>
              </button>
            );
          })}
        </nav>
      </div>
      <div className="mt-4 border-t border-hairline px-3 pt-4">
        <div className="flex items-center justify-between">
          <span className="text-[13px] font-medium text-ink2">Raw keys</span>
          <Toggle on={rawMode} onChange={onToggleRaw} />
        </div>
        <p className="mt-1 text-[11px] text-ink3">Show every setting as its config key</p>
      </div>
    </aside>
  );
}
```

- [ ] **Step 4: Create the five category stubs**

Each file (`categories/InstanceCategory.tsx`, `GitCategory.tsx`, `AiCategory.tsx`, `ReviewCategory.tsx`, `SignInCategory.tsx`) starts as:

```tsx
import type { SettingsCtx } from "../model";

// Platzhalter — wird in den folgenden Tasks ausgefuellt.
export function AiCategory({ ctx: _ctx }: { ctx: SettingsCtx }) {
  return <div className="text-[13px] text-ink3">…</div>;
}
```

(Adjust the exported name per file: `InstanceCategory`, `GitCategory`, `AiCategory`, `ReviewCategory`, `SignInCategory`. The `_ctx` underscore avoids the unused-var lint until fleshed out.)

- [ ] **Step 5: Rewrite `SettingsPage.tsx` (orchestrator)**

```tsx
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
  const [wizard, setWizard] = useState<WizardState>(null);

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
```

- [ ] **Step 6: Create `hints.ts` (sidebar status derivation)**

```ts
import type { CategoryId, SettingsCtx } from "./model";

/** Status-Hinweise pro Kategorie aus den effektiven Settings (handoff §Interactions). */
export function computeHints(ctx: SettingsCtx): Record<CategoryId, { tone: "acc" | "ink3" | "warn"; text: string }> {
  const platform = ctx.get("Naudit:Git:Platform") || "GitLab";
  const isGitHub = platform === "GitHub";
  const usesApp = ctx.get("Naudit:GitHub:Auth") === "App";
  const gitConnected = isGitHub
    ? (usesApp ? ctx.secretSet("Naudit:GitHub:App:PrivateKey") : ctx.secretSet("Naudit:GitHub:Token")) && ctx.secretSet("Naudit:GitHub:WebhookSecret")
    : ctx.secretSet("Naudit:GitLab:Token") && ctx.secretSet("Naudit:GitLab:WebhookSecret");

  const provider = ctx.get("Naudit:Ai:Provider") || "Ollama";
  const providerLabel = provider === "ClaudeCode" ? "Claude Code"
    : provider === "OpenAICompatible" ? "OpenAI-compat" : provider;

  const sev = ctx.get("Naudit:Review:Gate:MinSeverity");
  const conf = ctx.get("Naudit:Review:Gate:MinConfidence");
  const gateDefault = (!sev || sev === "High") && (!conf || conf === "Medium");

  const gh = ctx.get("Naudit:Ui:Auth:GitHub:Enabled") === "true";
  const oidc = ctx.get("Naudit:Ui:Auth:Oidc:Enabled") === "true";

  return {
    instance: ctx.get("Naudit:PublicBaseUrl")
      ? { tone: "acc", text: "✓" } : { tone: "warn", text: "not set" },
    git: gitConnected ? { tone: "acc", text: `✓ ${platform}` } : { tone: "ink3", text: platform },
    ai: { tone: "ink3", text: providerLabel },
    review: gateDefault ? { tone: "ink3", text: "defaults" } : { tone: "ink3", text: "custom" },
    signin: gh ? { tone: "acc", text: "GitHub" } : oidc ? { tone: "acc", text: "SSO" } : { tone: "warn", text: "local only" },
  };
}
```

- [ ] **Step 7: Verify lint + build**

Run: `cd src/frontend && npm run lint && npm run build`
Expected: PASS. Manual: open Settings — sidebar switches categories (stubs show "…"), Raw-keys toggle flips to today's flat editable catalog with filter + count, Save/restart banners behave as before.

- [ ] **Step 8: Commit**

```bash
git add src/frontend/src/components/pages/SettingsPage.tsx src/frontend/src/components/settings/
git commit -m "feat(settings): Two-Pane-Shell, Sidebar mit Status, Raw-Keys-Modus"
```

---

### Task 3: AI provider category

**Files:**
- Rewrite: `src/frontend/src/components/settings/categories/AiCategory.tsx`

**Interfaces:**
- Consumes: `SettingsCtx` (Task 2); `Field` (`@/components/setup/shared`); `Panel` (`@/components/ui/Panel`); `SelectableCard` (Task 1).
- Produces: `AiCategory({ ctx }): JSX` (prop shape already wired in Task 2 Step 5).

Reference: handoff §1 (provider cards 2×2, requirement tags, conditional matrix, ClaudeCode info strip, amber footer).

- [ ] **Step 1: Implement `AiCategory.tsx`**

```tsx
import { Panel } from "@/components/ui/Panel";
import { Field } from "@/components/setup/shared";
import { SelectableCard } from "../primitives";
import type { SettingsCtx } from "../model";

const PROVIDERS: { id: string; title: string; tag: string; desc: string }[] = [
  { id: "Ollama", title: "Ollama", tag: "local", desc: "Self-hosted models. No API key, runs on your endpoint." },
  { id: "Anthropic", title: "Anthropic", tag: "API key", desc: "Claude via the Anthropic API." },
  { id: "OpenAICompatible", title: "OpenAI-compatible", tag: "API key + URL", desc: "Any OpenAI-style endpoint (NVIDIA, vLLM, …)." },
  { id: "ClaudeCode", title: "Claude Code", tag: "subscription", desc: "Uses the Claude Code CLI signed in on the server." },
];

const inputCls =
  "w-[280px] rounded-lg border border-border bg-bg px-3 py-2 font-mono text-[13px] text-ink outline-none placeholder:text-ink3 focus:border-acc disabled:opacity-50";

export function AiCategory({ ctx }: { ctx: SettingsCtx }) {
  const provider = ctx.get("Naudit:Ai:Provider") || "Ollama";
  const needsEndpoint = provider === "Ollama" || provider === "OpenAICompatible";
  const needsKey = provider === "Anthropic" || provider === "OpenAICompatible";
  const shown = 1 + (needsEndpoint ? 1 : 0) + (needsKey ? 1 : 0); // Model + Extras

  return (
    <>
      <div className="grid grid-cols-2 gap-3">
        {PROVIDERS.map((p) => {
          const sel = provider === p.id;
          return (
            <SelectableCard key={p.id} selected={sel} onClick={() => ctx.set("Naudit:Ai:Provider", p.id)}>
              <div className="flex items-center justify-between">
                <b className="text-[14px]">{p.title}</b>
                <span className={`font-mono text-[11px] ${sel ? "font-bold text-acc" : "text-ink3"}`}>
                  {sel ? "✓ selected" : p.tag}
                </span>
              </div>
              <p className="text-[12.5px] leading-relaxed text-ink2">{p.desc}</p>
            </SelectableCard>
          );
        })}
      </div>

      <Panel title={`${PROVIDERS.find((p) => p.id === provider)!.title} settings`} extra={`${shown} of ${shown} fields shown`}>
        <div className="flex flex-col gap-4 px-5 py-4">
          <Field label="Model" hint={provider === "ClaudeCode" ? "Optional — defaults to sonnet." : "Required."}>
            <input className={inputCls} value={ctx.get("Naudit:Ai:Model")}
              placeholder={provider === "ClaudeCode" ? "sonnet" : ""}
              onChange={(e) => ctx.set("Naudit:Ai:Model", e.target.value)} />
          </Field>
          {needsEndpoint && (
            <Field label="Endpoint" hint={provider === "Ollama" ? "Optional — defaults to http://localhost:11434." : "Required."}>
              <input className={inputCls} value={ctx.get("Naudit:Ai:Endpoint")}
                placeholder={provider === "Ollama" ? "http://localhost:11434" : ""}
                disabled={ctx.locked("Naudit:Ai:Endpoint")}
                onChange={(e) => ctx.set("Naudit:Ai:Endpoint", e.target.value)} />
            </Field>
          )}
          {needsKey && (
            <Field label="API key" hint="Required — stored encrypted, never shown again.">
              <input type="password" className={inputCls}
                placeholder={ctx.secretSet("Naudit:Ai:ApiKey") ? "•••••• (set)" : "not set"}
                disabled={ctx.locked("Naudit:Ai:ApiKey")}
                value={ctx.get("Naudit:Ai:ApiKey")}
                onChange={(e) => ctx.set("Naudit:Ai:ApiKey", e.target.value)} />
            </Field>
          )}
          {provider === "ClaudeCode" && (
            <div className="rounded-lg border border-border bg-elev px-4 py-3 text-[12.5px] text-ink2">
              Claude Code signs in with the subscription already configured on the server — there is
              no endpoint or API key to manage here.
            </div>
          )}
        </div>
      </Panel>

      <div className="flex items-center gap-2 text-[12px] text-ink3">
        <span className="inline-block size-2 rounded-full bg-warn" aria-hidden="true" />
        Changes apply after a restart — you'll be prompted when you save.
      </div>
    </>
  );
}
```

- [ ] **Step 2: Verify lint + build**

Run: `cd src/frontend && npm run lint && npm run build`
Expected: PASS. Manual: AI category — selecting Ollama shows Model+Endpoint; Anthropic shows Model+API key; OpenAI-compatible shows all three; Claude Code shows Model + info strip. Provider change updates the field set immediately (no save) and the sidebar hint.

- [ ] **Step 3: Commit**

```bash
git add src/frontend/src/components/settings/categories/AiCategory.tsx
git commit -m "feat(settings): AI-Kategorie mit Provider-Cards + Conditional Fields"
```

---

### Task 4: Git platform category

**Files:**
- Rewrite: `src/frontend/src/components/settings/categories/GitCategory.tsx`

**Interfaces:**
- Consumes: `SettingsCtx`; `Field` (`@/components/setup/shared`); `Panel`, `Pill`; `SelectableCard`, `AuthChip`, `Toggle` (Task 1).
- Produces: `GitCategory({ ctx }): JSX`. Calls `ctx.openWizard({ kind: "github-app", step: 1 })` from the upsell button (wizard built in Task 8).

Reference: handoff §2 (platform cards, GitHub connection panel, PAT/App auth chips, PostVerdict toggle, "Run as a bot" upsell).

- [ ] **Step 1: Implement `GitCategory.tsx`**

```tsx
import { Panel } from "@/components/ui/Panel";
import { Pill } from "@/components/ui/Pill";
import { Field } from "@/components/setup/shared";
import { Button } from "@/components/ui/Button";
import { SelectableCard, AuthChip, Toggle } from "../primitives";
import type { SettingsCtx } from "../model";

const inputCls =
  "w-[360px] max-w-full rounded-lg border border-border bg-bg px-3 py-2 font-mono text-[13px] text-ink outline-none placeholder:text-ink3 focus:border-acc disabled:opacity-50";

function Secret({ ctx, k, label, hint }: { ctx: SettingsCtx; k: string; label: string; hint: string }) {
  return (
    <Field label={label} hint={hint}>
      <input type="password" className={inputCls} disabled={ctx.locked(k)}
        placeholder={ctx.secretSet(k) ? "•••••• (set)" : "not set"}
        value={ctx.get(k)} onChange={(e) => ctx.set(k, e.target.value)} />
    </Field>
  );
}

export function GitCategory({ ctx }: { ctx: SettingsCtx }) {
  const platform = ctx.get("Naudit:Git:Platform") || "GitLab";
  const isGitHub = platform === "GitHub";
  const usesApp = ctx.get("Naudit:GitHub:Auth") === "App";

  return (
    <>
      <div className="grid grid-cols-2 gap-3">
        {(["GitLab", "GitHub"] as const).map((p) => (
          <SelectableCard key={p} selected={platform === p} onClick={() => ctx.set("Naudit:Git:Platform", p)}>
            <b className="text-[14px]">{p}</b>
            <p className="text-[12.5px] text-ink2">{p === "GitLab" ? "Merge requests on GitLab / self-managed." : "Pull requests on GitHub / GHES."}</p>
          </SelectableCard>
        ))}
      </div>

      {isGitHub ? (
        <Panel title="GitHub connection">
          <div className="flex flex-col gap-4 px-5 py-4">
            <div className="flex gap-2">
              <AuthChip selected={!usesApp} onClick={() => ctx.set("Naudit:GitHub:Auth", "Pat")}>
                {!usesApp ? "✓ " : ""}Personal access token
              </AuthChip>
              <AuthChip selected={usesApp} onClick={() => ctx.set("Naudit:GitHub:Auth", "App")}>
                GitHub App (bot)
              </AuthChip>
            </div>

            {usesApp ? (
              <>
                <Field label="App ID" hint="From the GitHub App's settings page.">
                  <input className={inputCls} value={ctx.get("Naudit:GitHub:App:AppId")} disabled={ctx.locked("Naudit:GitHub:App:AppId")}
                    onChange={(e) => ctx.set("Naudit:GitHub:App:AppId", e.target.value)} />
                </Field>
                <Secret ctx={ctx} k="Naudit:GitHub:App:PrivateKey" label="Private key (PEM)" hint="Raw PEM or base64. Stored encrypted." />
                <Field label="Installation ID (optional)" hint="Skips the per-repo lookup when set.">
                  <input className={inputCls} value={ctx.get("Naudit:GitHub:App:InstallationId")} disabled={ctx.locked("Naudit:GitHub:App:InstallationId")}
                    onChange={(e) => ctx.set("Naudit:GitHub:App:InstallationId", e.target.value)} />
                </Field>
              </>
            ) : (
              <Secret ctx={ctx} k="Naudit:GitHub:Token" label="Access token"
                hint="Fine-grained PAT with pull request read/write. Comments appear as the token's owner." />
            )}

            <Secret ctx={ctx} k="Naudit:GitHub:WebhookSecret" label="Webhook secret"
              hint="Must match the secret entered in the repo's webhook settings." />
            <Field label="API base URL (optional)" hint="Only change this for GitHub Enterprise.">
              <input className={inputCls} value={ctx.get("Naudit:GitHub:BaseUrl")} placeholder="https://api.github.com"
                disabled={ctx.locked("Naudit:GitHub:BaseUrl")}
                onChange={(e) => ctx.set("Naudit:GitHub:BaseUrl", e.target.value)} />
            </Field>

            <div className="flex items-center justify-between border-t border-hairline pt-4">
              <div>
                <div className="text-[13px] font-medium text-ink">Post a real review verdict</div>
                <div className="text-[11.5px] text-ink3">APPROVE / REQUEST_CHANGES instead of a plain comment.</div>
              </div>
              <Toggle on={ctx.get("Naudit:GitHub:PostVerdict") === "true"}
                disabled={ctx.locked("Naudit:GitHub:PostVerdict")}
                onChange={(v) => ctx.set("Naudit:GitHub:PostVerdict", v ? "true" : "false")} />
            </div>
          </div>
        </Panel>
      ) : (
        <Panel title="GitLab connection">
          <div className="flex flex-col gap-4 px-5 py-4">
            <Field label="Base URL" hint="e.g. https://gitlab.example.com">
              <input className={inputCls} value={ctx.get("Naudit:GitLab:BaseUrl")} disabled={ctx.locked("Naudit:GitLab:BaseUrl")}
                onChange={(e) => ctx.set("Naudit:GitLab:BaseUrl", e.target.value)} />
            </Field>
            <Secret ctx={ctx} k="Naudit:GitLab:Token" label="Access token" hint="Token with api scope (read diff, post comment)." />
            <Secret ctx={ctx} k="Naudit:GitLab:WebhookSecret" label="Webhook secret" hint="Checked against the X-Gitlab-Token header." />
            <div className="flex items-center justify-between border-t border-hairline pt-4">
              <div>
                <div className="text-[13px] font-medium text-ink">Post a real review verdict</div>
                <div className="text-[11.5px] text-ink3">Calls MR approve / unapprove from the verdict.</div>
              </div>
              <Toggle on={ctx.get("Naudit:GitLab:PostVerdict") === "true"}
                disabled={ctx.locked("Naudit:GitLab:PostVerdict")}
                onChange={(v) => ctx.set("Naudit:GitLab:PostVerdict", v ? "true" : "false")} />
            </div>
          </div>
        </Panel>
      )}

      {isGitHub && !usesApp && (
        <div className="flex items-center justify-between rounded-xl border border-hairline bg-surface px-5 py-4">
          <div>
            <div className="text-[13px] font-semibold text-ink">Run as a bot instead</div>
            <p className="max-w-[56ch] text-[12.5px] text-ink2">
              A GitHub App posts reviews under its own identity and scales past a single user's rate limits.
            </p>
          </div>
          <Button variant="secondary" className="shrink-0 px-3 py-1.5 text-[12.5px]"
            onClick={() => ctx.openWizard({ kind: "github-app", step: 1 })}>
            Set up GitHub App →
          </Button>
        </div>
      )}

      <div className="flex items-center gap-2 text-[12px] text-ink3">
        <span className="inline-block size-2 rounded-full bg-warn" aria-hidden="true" />
        Changes apply after a restart — you'll be prompted when you save.
      </div>
    </>
  );
}
```

Note: the unused `Pill` import must be dropped if not used — the header "✓ connected" pill from the handoff is optional; omit it to keep lint clean, or render `<Pill kind="ok">✓ connected</Pill>` next to the panel title via a small wrapper. For simplicity this task omits the connected-pill; add it in Task 9 polish if desired. **Remove the `Pill` import** to avoid an unused-import lint error.

- [ ] **Step 2: Verify lint + build**

Run: `cd src/frontend && npm run lint && npm run build`
Expected: PASS. Manual: GitHub selected → PAT fields; switch chip to App → AppId/PrivateKey/InstallationId; back to PAT → upsell card appears; toggle PostVerdict; switch platform to GitLab → GitLab fields. Env-locked fields disabled.

- [ ] **Step 3: Commit**

```bash
git add src/frontend/src/components/settings/categories/GitCategory.tsx
git commit -m "feat(settings): Git-Kategorie mit Plattform-Cards + PAT/App-Feldern"
```

---

### Task 5: Instance category

**Files:**
- Rewrite: `src/frontend/src/components/settings/categories/InstanceCategory.tsx`

**Interfaces:**
- Consumes: `SettingsCtx`; `Field`, `CopyRow` (`@/components/setup/shared`); `Panel`; `SelectableCard`.
- Produces: `InstanceCategory({ ctx }): JSX`.

Reference: handoff §7.

- [ ] **Step 1: Implement `InstanceCategory.tsx`**

```tsx
import { Panel } from "@/components/ui/Panel";
import { Field, CopyRow } from "@/components/setup/shared";
import { SelectableCard } from "../primitives";
import type { SettingsCtx } from "../model";

export function InstanceCategory({ ctx }: { ctx: SettingsCtx }) {
  const base = ctx.get("Naudit:PublicBaseUrl").replace(/\/+$/, "");
  const platform = (ctx.get("Naudit:Git:Platform") || "GitLab").toLowerCase();
  const mode = ctx.get("Naudit:AccessGate:Mode") || "Open";

  return (
    <>
      <Panel title="Address">
        <div className="flex flex-col gap-4 px-5 py-4">
          <Field label="Public base URL" hint="Used to build the webhook and sign-in callback URLs below.">
            <input
              className="w-[360px] max-w-full rounded-lg border border-border bg-bg px-3 py-2 font-mono text-[13px] text-ink outline-none placeholder:text-ink3 focus:border-acc disabled:opacity-50"
              placeholder="https://naudit.example.com" disabled={ctx.locked("Naudit:PublicBaseUrl")}
              value={ctx.get("Naudit:PublicBaseUrl")} onChange={(e) => ctx.set("Naudit:PublicBaseUrl", e.target.value)} />
          </Field>
          {base && (
            <div className="max-w-[520px]">
              <CopyRow label="Webhook URL — paste into your repo's webhook settings" value={`${base}/webhook/${platform}`} />
            </div>
          )}
        </div>
      </Panel>

      <Panel title="Who gets reviews">
        <div className="grid grid-cols-1 gap-3 px-5 py-4 md:grid-cols-2">
          {([
            { id: "Open", title: "Open", desc: "Any repo that knows the webhook secret gets reviews." },
            { id: "Registered", title: "Registered only", desc: "Only repos belonging to approved dashboard accounts. Everyone else is ignored." },
          ] as const).map((o) => (
            <SelectableCard key={o.id} selected={mode === o.id} disabled={ctx.locked("Naudit:AccessGate:Mode")}
              onClick={() => ctx.set("Naudit:AccessGate:Mode", o.id)}>
              <b className="text-[13.5px]">{o.title}</b>
              <p className="text-[12.5px] text-ink2">{o.desc}</p>
            </SelectableCard>
          ))}
        </div>
      </Panel>
    </>
  );
}
```

- [ ] **Step 2: Verify lint + build**

Run: `cd src/frontend && npm run lint && npm run build`
Expected: PASS. Manual: typing a base URL reveals the webhook CopyRow (correct platform suffix); access-gate cards select Open/Registered and update the draft.

- [ ] **Step 3: Commit**

```bash
git add src/frontend/src/components/settings/categories/InstanceCategory.tsx
git commit -m "feat(settings): Instance-Kategorie (Base-URL, Webhook-CopyRow, Access-Gate)"
```

---

### Task 6: Review rules category

**Files:**
- Rewrite: `src/frontend/src/components/settings/categories/ReviewCategory.tsx`

**Interfaces:**
- Consumes: `SettingsCtx`; `Field`; `Panel`, `Pill`.
- Produces: `ReviewCategory({ ctx }): JSX`.

Reference: handoff §8 (merge-gate selects, plain-language preview strip, review-prompt textarea).

- [ ] **Step 1: Implement `ReviewCategory.tsx`**

```tsx
import { Panel } from "@/components/ui/Panel";
import { Pill } from "@/components/ui/Pill";
import { Field } from "@/components/setup/shared";
import type { SettingsCtx } from "../model";

const SEV = ["Info", "Low", "Medium", "High", "Critical"];
const CONF = ["Low", "Medium", "High"];
const selCls = "w-[220px] rounded-lg border border-border bg-bg px-3 py-2 font-mono text-[13px] text-ink outline-none focus:border-acc disabled:opacity-50";

export function ReviewCategory({ ctx }: { ctx: SettingsCtx }) {
  const sev = ctx.get("Naudit:Review:Gate:MinSeverity") || "High";
  const conf = ctx.get("Naudit:Review:Gate:MinConfidence") || "Medium";
  const prompt = ctx.get("Naudit:Review:SystemPrompt");

  return (
    <>
      <Panel title="Merge gate">
        <div className="flex flex-col gap-4 px-5 py-4">
          <div className="flex flex-wrap gap-4">
            <Field label="Minimum severity" hint="Findings below this never block.">
              <select className={selCls} value={sev} disabled={ctx.locked("Naudit:Review:Gate:MinSeverity")}
                onChange={(e) => ctx.set("Naudit:Review:Gate:MinSeverity", e.target.value)}>
                {SEV.map((s) => <option key={s} value={s}>{s}</option>)}
              </select>
            </Field>
            <Field label="Minimum confidence" hint="How sure the AI must be.">
              <select className={selCls} value={conf} disabled={ctx.locked("Naudit:Review:Gate:MinConfidence")}
                onChange={(e) => ctx.set("Naudit:Review:Gate:MinConfidence", e.target.value)}>
                {CONF.map((c) => <option key={c} value={c}>{c}</option>)}
              </select>
            </Field>
          </div>
          <div className="rounded-lg border border-border bg-elev px-4 py-3 text-[12.5px] text-ink2">
            With these rules, a merge is blocked when a finding is <b className="text-ink">{sev}</b> or
            worse and the AI is at least <b className="text-ink">{conf}</b> confident. Everything else
            becomes a non-blocking comment.
          </div>
        </div>
      </Panel>

      <Panel title="Review prompt" extra={prompt ? "custom" : "built-in default"}>
        <div className="px-5 py-4">
          <Field label="System prompt" hint="Clearing the field goes back to the built-in prompt.">
            <textarea rows={4} disabled={ctx.locked("Naudit:Review:SystemPrompt")}
              className="min-h-[88px] w-full rounded-lg border border-border bg-bg px-3 py-2 font-mono text-[13px] text-ink outline-none placeholder:text-ink3 focus:border-acc disabled:opacity-50"
              placeholder="Using the built-in review prompt. Write your own here to override it."
              value={prompt} onChange={(e) => ctx.set("Naudit:Review:SystemPrompt", e.target.value)} />
          </Field>
        </div>
      </Panel>
    </>
  );
}
```

Note: `Pill` is imported for the optional header pill; the handoff shows the "built-in default" state as the panel `extra` text, which this task uses instead. **Remove the `Pill` import** (unused) to keep lint clean, or use `<Pill>` in place of `extra`.

- [ ] **Step 2: Verify lint + build**

Run: `cd src/frontend && npm run lint && npm run build`
Expected: PASS. Manual: changing either select re-renders the preview sentence live; the prompt textarea updates the draft and the panel `extra` flips between "built-in default" / "custom".

- [ ] **Step 3: Commit**

```bash
git add src/frontend/src/components/settings/categories/ReviewCategory.tsx
git commit -m "feat(settings): Review-Kategorie (Merge-Gate + Live-Preview + Prompt)"
```

---

### Task 7: Sign-in category

**Files:**
- Rewrite: `src/frontend/src/components/settings/categories/SignInCategory.tsx`

**Interfaces:**
- Consumes: `SettingsCtx`; `Pill`, `Button`.
- Produces: `SignInCategory({ ctx }): JSX`. Calls `ctx.openWizard({ kind: "github-signin", step: 1 })` and `ctx.openWizard({ kind: "oidc", step: 1 })`.

Reference: handoff §3. The **Change password** button is disabled (no self-service endpoint exists — verified via `grep` in the repo).

- [ ] **Step 1: Implement `SignInCategory.tsx`**

```tsx
import { Pill } from "@/components/ui/Pill";
import { Button } from "@/components/ui/Button";
import type { ReactNode } from "react";
import type { SettingsCtx } from "../model";

function Card({ title, pill, children, action }: { title: string; pill: ReactNode; children: ReactNode; action: ReactNode }) {
  return (
    <div className="flex items-center justify-between gap-4 rounded-xl border border-hairline bg-surface px-5 py-5">
      <div className="min-w-0">
        <div className="flex items-center gap-2">
          <b className="text-[14px]">{title}</b>
          {pill}
        </div>
        <div className="mt-1 max-w-[64ch] text-[12.5px] text-ink2">{children}</div>
      </div>
      <div className="shrink-0">{action}</div>
    </div>
  );
}

export function SignInCategory({ ctx }: { ctx: SettingsCtx }) {
  const gh = ctx.get("Naudit:Ui:Auth:GitHub:Enabled") === "true";
  const oidc = ctx.get("Naudit:Ui:Auth:Oidc:Enabled") === "true";

  return (
    <div className="flex flex-col gap-3">
      <Card title="GitHub sign-in" pill={gh ? <Pill kind="ok">✓ on</Pill> : <Pill kind="neutral">off</Pill>}
        action={<Button variant={gh ? "secondary" : "primary"} className="px-3 py-1.5 text-[12.5px]"
          onClick={() => ctx.openWizard({ kind: "github-signin", step: 1 })}>
          {gh ? "Reconfigure →" : "Set up GitHub sign-in →"}
        </Button>}>
        Let anyone with a GitHub account sign in. New accounts wait on your Approvals page until you
        let them in. <span className="text-ink3">Takes ~2 minutes — we'll walk you through creating the OAuth app.</span>
      </Card>

      <Card title="Single sign-on (OIDC)" pill={oidc ? <Pill kind="ok">✓ on</Pill> : <Pill kind="neutral">off</Pill>}
        action={<Button variant="secondary" className="px-3 py-1.5 text-[12.5px]"
          onClick={() => ctx.openWizard({ kind: "oidc", step: 1 })}>
          {oidc ? "Reconfigure →" : "Set up SSO →"}
        </Button>}>
        Connect your identity provider (Entra ID, Keycloak, Auth0, …) so users sign in with your org's accounts.
      </Card>

      <Card title="Local admin" pill={<Pill kind="ok">✓ active</Pill>}
        action={<Button variant="secondary" disabled className="px-3 py-1.5 text-[12.5px]"
          title="No self-service password change yet — set Naudit:Ui:Admins via environment.">
          Change password
        </Button>}>
        <span className="font-mono text-ink2">password set via environment</span>
      </Card>

      <p className="mt-1 text-[12px] text-ink3">
        Need a key that isn't here? Flip on <b className="text-ink2">Raw keys</b> in the sidebar to edit the full config catalog.
      </p>
    </div>
  );
}
```

- [ ] **Step 2: Verify lint + build**

Run: `cd src/frontend && npm run lint && npm run build`
Expected: PASS. Manual: pills reflect enabled state; "Set up →" buttons open (empty until Task 8, but the handler fires — confirm no crash by temporarily logging, or defer manual open-check to Task 8); Change password is disabled with a tooltip.

- [ ] **Step 3: Commit**

```bash
git add src/frontend/src/components/settings/categories/SignInCategory.tsx
git commit -m "feat(settings): Sign-in-Kategorie (GitHub/OIDC/Local-Cards)"
```

---

### Task 8: Sign-in wizard + App/OIDC credential modals

**Files:**
- Create: `src/frontend/src/components/settings/wizards/SignInWizard.tsx`
- Modify: `src/frontend/src/components/pages/SettingsPage.tsx` (render the wizard when `wizard !== null`)

**Interfaces:**
- Consumes: `Modal`, `Button`, `Field`, `CopyRow`, `SettingsCtx`, `WizardState`, `useSaveSettings`, `useRestartApp`.
- Produces: `SignInWizard({ state, ctx, base, onClose }: { state: NonNullable<WizardState>; ctx: SettingsCtx; base: string; onClose: () => void }): JSX`.

Behavior (spec §Wizards, frontend-only):
- `github-signin` = 3-step GitHub OAuth wizard. **Step 2 saves `Enabled=true` + `ClientId` + `ClientSecret` in one `useSaveSettings` call**; no live verify (honest note). Step 3 offers restart.
- `github-app` / `oidc` = single-step credential form in the same `Modal`, saves the relevant keys.
- The wizard writes directly via its own `useSaveSettings`/`useRestartApp` (independent of the page draft), so saved creds land immediately. It does **not** touch the page `drafts`.

- [ ] **Step 1: Implement `SignInWizard.tsx`**

```tsx
import { useState } from "react";
import { Modal } from "../primitives";
import { Button } from "@/components/ui/Button";
import { Field, CopyRow } from "@/components/setup/shared";
import { useRestartApp, useSaveSettings } from "@/hooks/queries";
import type { SettingsCtx, WizardState } from "../model";

const inputCls =
  "w-full rounded-lg border border-border bg-bg px-3 py-2 font-mono text-[13px] text-ink outline-none placeholder:text-ink3 focus:border-acc";

type Change = { key: string; value: string | null };

export function SignInWizard({ state, ctx, base, onClose }: {
  state: NonNullable<WizardState>; ctx: SettingsCtx; base: string; onClose: () => void;
}) {
  const save = useSaveSettings();
  const restart = useRestartApp();
  const [step, setStep] = useState(state.step);
  const [clientId, setClientId] = useState(ctx.get(
    state.kind === "oidc" ? "Naudit:Ui:Auth:Oidc:ClientId" : "Naudit:Ui:Auth:GitHub:ClientId"));
  const [secret, setSecret] = useState("");
  const [appId, setAppId] = useState(ctx.get("Naudit:GitHub:App:AppId"));
  const [pem, setPem] = useState("");
  const [authority, setAuthority] = useState(ctx.get("Naudit:Ui:Auth:Oidc:Authority"));
  const [saved, setSaved] = useState(false);

  const commit = (changes: Change[], onDone: () => void) =>
    save.mutate(changes.filter((c) => c.value !== ""), { onSuccess: () => { setSaved(true); onDone(); } });

  // --- GitHub App: Ein-Schritt-Formular ---
  if (state.kind === "github-app") {
    return (
      <Modal title="Set up GitHub App" onClose={onClose}
        footer={<>
          <button type="button" className="font-mono text-[12px] text-ink3 hover:text-ink" onClick={onClose}>Cancel</button>
          <Button loading={save.isPending} onClick={() => commit([
            { key: "Naudit:GitHub:Auth", value: "App" },
            { key: "Naudit:GitHub:App:AppId", value: appId },
            { key: "Naudit:GitHub:App:PrivateKey", value: pem === "" ? null : pem },
          ], onClose)}>Save</Button>
        </>}>
        <p className="text-[12.5px] text-ink2">Enter the App's credentials. Reviews will post under the bot identity after a restart.</p>
        <Field label="App ID"><input className={inputCls} value={appId} onChange={(e) => setAppId(e.target.value)} /></Field>
        <Field label="Private key (PEM)" hint="Raw PEM or base64. Stored encrypted.">
          <textarea rows={4} className={`${inputCls} min-h-[88px]`} value={pem}
            placeholder={ctx.secretSet("Naudit:GitHub:App:PrivateKey") ? "•••••• (set — leave blank to keep)" : ""}
            onChange={(e) => setPem(e.target.value)} />
        </Field>
        {save.isError && <div className="text-[12px] text-danger">Couldn't save: {save.error?.message}</div>}
      </Modal>
    );
  }

  // --- OIDC: Ein-Schritt-Formular ---
  if (state.kind === "oidc") {
    return (
      <Modal title="Set up single sign-on (OIDC)" onClose={onClose}
        footer={<>
          <button type="button" className="font-mono text-[12px] text-ink3 hover:text-ink" onClick={onClose}>Cancel</button>
          <Button loading={save.isPending} onClick={() => commit([
            { key: "Naudit:Ui:Auth:Oidc:Enabled", value: "true" },
            { key: "Naudit:Ui:Auth:Oidc:Authority", value: authority },
            { key: "Naudit:Ui:Auth:Oidc:ClientId", value: clientId },
            { key: "Naudit:Ui:Auth:Oidc:ClientSecret", value: secret === "" ? null : secret },
          ], onClose)}>Save</Button>
        </>}>
        <p className="text-[12.5px] text-ink2">Point Naudit at your IdP. Sign-in turns on after a restart.</p>
        <Field label="Authority" hint="e.g. https://login.microsoftonline.com/{tenant}/v2.0">
          <input className={inputCls} value={authority} onChange={(e) => setAuthority(e.target.value)} /></Field>
        <Field label="Client ID"><input className={inputCls} value={clientId} onChange={(e) => setClientId(e.target.value)} /></Field>
        <Field label="Client secret" hint="Stored encrypted.">
          <input type="password" className={inputCls} value={secret}
            placeholder={ctx.secretSet("Naudit:Ui:Auth:Oidc:ClientSecret") ? "•••••• (set — leave blank to keep)" : ""}
            onChange={(e) => setSecret(e.target.value)} /></Field>
        {save.isError && <div className="text-[12px] text-danger">Couldn't save: {save.error?.message}</div>}
      </Modal>
    );
  }

  // --- GitHub sign-in: 3 Schritte ---
  const dots = (
    <div className="flex items-center gap-1.5">
      {[1, 2, 3].map((n) => <span key={n} className={`size-1.5 rounded-full ${n === step ? "bg-acc" : "bg-border"}`} />)}
    </div>
  );

  return (
    <Modal title="Set up GitHub sign-in" step={`step ${step} / 3`} onClose={onClose}
      footer={
        step === 1 ? (
          <>
            <button type="button" className="font-mono text-[12px] text-ink3 hover:text-ink" onClick={onClose}>Cancel</button>
            <div className="flex items-center gap-4">{dots}
              <Button disabled={!clientId || !secret} onClick={() => setStep(2)}>Continue →</Button></div>
          </>
        ) : step === 2 ? (
          <>
            <button type="button" className="font-mono text-[12px] text-ink3 hover:text-ink" onClick={() => setStep(1)}>← Back</button>
            <div className="flex items-center gap-4">{dots}
              <Button loading={save.isPending} disabled={!saved && save.isPending}
                onClick={() => saved ? setStep(3) : commit([
                  { key: "Naudit:Ui:Auth:GitHub:Enabled", value: "true" },
                  { key: "Naudit:Ui:Auth:GitHub:ClientId", value: clientId },
                  { key: "Naudit:Ui:Auth:GitHub:ClientSecret", value: secret },
                ], () => setStep(3))}>Continue →</Button></div>
          </>
        ) : (
          <>
            <button type="button" className="font-mono text-[12px] text-ink3 hover:text-ink" onClick={onClose}>Restart later</button>
            <div className="flex items-center gap-4">{dots}
              <Button loading={restart.isPending} onClick={() => restart.mutate(undefined, { onSuccess: onClose })}>
                Restart now to apply</Button></div>
          </>
        )
      }>
      {step === 1 && (
        <>
          <p className="text-[12.5px] text-ink2">Create an OAuth app on GitHub and paste its credentials here.</p>
          <ol className="flex flex-col gap-3">
            <li className="text-[12.5px] text-ink2">
              1. Open <a className="text-acc hover:underline" target="_blank" rel="noreferrer"
                href="https://github.com/settings/applications/new">GitHub → Settings → Developer settings → New OAuth app</a>.
            </li>
            <li className="text-[12.5px] text-ink2">2. Set the callback URL:
              <div className="mt-1.5"><CopyRow label="Authorization callback URL" value={`${base}/auth/callback/github`} /></div>
            </li>
            <li className="text-[12.5px] text-ink2">3. Paste the Client ID and secret:</li>
          </ol>
          <Field label="Client ID"><input className={inputCls} value={clientId} onChange={(e) => setClientId(e.target.value)} /></Field>
          <Field label="Client secret"><input type="password" className={inputCls} value={secret} onChange={(e) => setSecret(e.target.value)} /></Field>
        </>
      )}
      {step === 2 && (
        <>
          <p className="text-[12.5px] text-ink2">Credentials saved. GitHub sign-in turns on after a restart — we can't test it before then.</p>
          <div className="flex flex-col gap-2 rounded-[10px] border border-hairline bg-bg px-4 py-3 text-[12.5px]">
            <span className="text-acc">✓ Client ID saved</span>
            <span className="text-acc">✓ Callback URL configured</span>
          </div>
          {save.isError && <div className="text-[12px] text-danger">Couldn't save: {save.error?.message}</div>}
        </>
      )}
      {step === 3 && (
        <div className="flex flex-col items-center gap-3 text-center">
          <div className="grid size-[52px] place-items-center rounded-full border border-acc bg-acc/12 text-[22px] text-acc">✓</div>
          <b className="text-[15px]">GitHub sign-in is ready</b>
          <p className="max-w-[46ch] text-[12.5px] text-ink2">
            After the restart, a "Continue with GitHub" button appears on the login page. New sign-ins wait on your Approvals page.
          </p>
        </div>
      )}
    </Modal>
  );
}
```

- [ ] **Step 2: Render the wizard in `SettingsPage.tsx`**

Add the import and render block. Insert the import near the other settings imports:

```tsx
import { SignInWizard } from "@/components/settings/wizards/SignInWizard";
```

Compute `base` next to `hints` (before the `return`):

```tsx
  const base = ctx.get("Naudit:PublicBaseUrl").replace(/\/+$/, "");
```

And render the modal at the end of the outer `<div>` (just before its closing tag):

```tsx
      {wizard && <SignInWizard state={wizard} ctx={ctx} base={base} onClose={() => setWizard(null)} />}
```

- [ ] **Step 3: Verify lint + build**

Run: `cd src/frontend && npm run lint && npm run build`
Expected: PASS. Manual: from Sign-in, "Set up GitHub sign-in →" opens the modal; step 1 requires both creds, step 2 saves them (network call fires), step 3 restart. From Git (PAT mode) the "Set up GitHub App →" upsell opens the App form. OIDC opens its form. Esc/backdrop closes without losing entered values within the open session.

- [ ] **Step 4: Commit**

```bash
git add src/frontend/src/components/settings/wizards/SignInWizard.tsx src/frontend/src/components/pages/SettingsPage.tsx
git commit -m "feat(settings): Sign-in-Wizard (GitHub 3-Step) + App/OIDC-Credential-Modals"
```

---

### Task 9: Smoothness polish + full verification

**Files:**
- Modify (as needed): any category with a layout jump; `SettingsPage.tsx`.

**Interfaces:** none new.

- [ ] **Step 1: Audit transitions & reduced-motion**

Confirm the content pane carries `key={active}` + `anim-fadein` (already in Task 2), the raw view uses `anim-fadein`, the modal uses `anim-modalin`, and toggles animate. Verify no category causes a horizontal scroll or layout jump on switch. If any panel jumps because a conditional block changes height, that is acceptable (spec: swaps are allowed) — do **not** add height animations.

- [ ] **Step 2: Manual reduced-motion check**

In the browser devtools, emulate `prefers-reduced-motion: reduce` and confirm fades/pulses are disabled (the `@media (prefers-reduced-motion: no-preference)` guard means `.anim-*` do nothing under reduce; `Skeleton` already has `motion-reduce:animate-none`).

- [ ] **Step 3: Full frontend verification**

Run: `cd src/frontend && npm run lint && npm run build`
Expected: PASS (tsc `--noEmit` + vite build, no errors/warnings).

- [ ] **Step 4: Backend regression guard (no change expected)**

Run: `cd /home/bnau/workspace/Naudit && dotnet test Naudit.slnx`
Expected: PASS (unchanged — frontend-only branch). Confirms nothing in `wwwroot`/build wiring broke the solution.

- [ ] **Step 5: Manual end-to-end walk-through**

Load the app (`dotnet run --project src/Naudit.Web`, dashboard at the dev server per CLAUDE.md), sign in as admin, open Settings, and confirm each of: initial skeleton → no jump; category switch fades; AI/Git conditional fields swap; Instance webhook CopyRow; Review live preview; Sign-in pills; Raw-keys toggle persists across reload; env-locked field disabled; GitHub sign-in wizard saves + restart prompt.

- [ ] **Step 6: Commit (if any polish changed files)**

```bash
git add -A
git commit -m "polish(settings): Transition-Feinschliff + reduced-motion-Audit"
```

(If Step 1–2 required no changes, skip the commit.)

---

## Self-Review

**Spec coverage:**
- Sidebar nav + status → Task 2 (`SettingsSidebar`, `hints.ts`). ✓
- Conditional AI fields → Task 3. ✓
- Conditional Git fields (PAT/App) → Task 4. ✓
- Instance (base URL, webhook CopyRow, access gate) → Task 5. ✓
- Review rules (gate selects, preview strip, prompt) → Task 6. ✓
- Sign-in cards + Change-password-disabled → Task 7. ✓
- GitHub sign-in wizard (simplified step 2) + App/OIDC modals → Task 8. ✓
- Raw-keys expert mode (filter, dynamic count, dim) → Task 2. ✓
- Smoothness (fade-in, modal-in, toggles, reduced-motion) → Task 1 (keyframes) + Task 9 (audit). ✓
- Callback URL `/auth/callback/github` → Task 8 Step 1. ✓
- Save/restart semantics unchanged, env-locked disabled, secrets write-only → Task 2 `ctx` helpers, used everywhere. ✓

**Placeholder scan:** Category stubs in Task 2 Step 4 are intentional scaffolding replaced in Tasks 3–7 (each ends building green); not plan placeholders. Two "remove the unused `Pill` import" notes (Tasks 4, 6) are explicit instructions, not TODOs.

**Type consistency:** `SettingsCtx` (`get`/`set`/`locked`/`secretSet`/`openWizard`), `CategoryId`, `WizardState`, and the `({ ctx })` category prop shape are defined in Task 2 `model.ts` and used verbatim in Tasks 3–8. `computeHints(ctx)` returns the exact `Record<CategoryId, {tone,text}>` the sidebar consumes. Wizard `commit(changes, onDone)` and the `useSaveSettings` change shape (`{key, value: string|null}`) match the hook.

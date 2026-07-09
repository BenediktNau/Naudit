# Settings page redesign — design

*2026-07-09 · Naudit · branch `feat/settings-redesign` (off `main`)*

Implements the design handoff in `docs/design_handoff_settings_redesign/` (section **2a** only).
Frontend-only. No backend change — reuses the existing settings API, catalog, and save/restart flow.

## Problem

`SettingsPage.tsx` renders the raw `SettingsCatalog` as five flat key/value panels. It is
functional but overwhelming: every DB-managed key is shown at once, secrets and env-locked keys
mixed in, no notion of "which fields does *this* provider/platform actually need". The handoff
(direction 2a) replaces it with a guided, category-based page that shows only the relevant fields,
plus a **Raw keys** expert toggle that preserves today's power-user flow.

## Scope

**In:** the full section-2a guided view (sidebar category nav with derived status, conditional AI
and Git fields, Instance / Review-rules / Sign-in categories, Raw-keys expert mode), and the
**GitHub sign-in wizard** modal (simplified — see below). The GitHub-App and OIDC "Set up →"
buttons open the same modal shell as a plain credential form.

**Out (deliberately):**
- **No verify endpoint / live "test sign-in"** in the sign-in wizard. GitHub OAuth only becomes
  active *after a restart* (config → DB → the OAuth handler is registered at host startup from
  `uiConfig.Auth.GitHub`), so a pre-restart test has nothing to probe. Step 2 is reduced to
  "save-then-instruct" (see Wizard below). Decided with the user.
- **No manifest-based GitHub-App creation from Settings.** The first-run setup wizard does the
  App-manifest dance, but its `/api/setup/*` endpoints are only mapped in setup mode. Wiring the
  manifest flow into the post-setup Settings page is backend work and out of scope; the App
  "Set up →" button collects `App:AppId` / `App:PrivateKey` / `App:InstallationId` directly.
- No router, no responsive/mobile pass, no i18n extraction (copy stays English, centralized-ready),
  no change to the settings API or catalog.

## Architecture

New directory `src/frontend/src/components/settings/`. `SettingsPage.tsx` becomes a thin
orchestrator; content moves into focused files.

```
components/
  pages/SettingsPage.tsx        (rewrite — orchestrator)
  settings/
    SettingsSidebar.tsx         230px nav + status hints + Raw-keys toggle
    RawKeys.tsx                 flat expert view (today's SettingRow) + filter + dynamic count
    primitives.tsx              Toggle, Modal, SelectableCard, AuthChip, StatusHint
    categories/
      InstanceCategory.tsx
      GitCategory.tsx
      AiCategory.tsx
      ReviewCategory.tsx
      SignInCategory.tsx
    wizards/
      SignInWizard.tsx          GitHub sign-in (3 steps) + App/OIDC credential forms (same Modal)
```

Reuse from `components/setup/shared.tsx`: `Field`, `CopyRow`, `randomSecret`. Reuse
`ui/{Panel,Pill,Button,Skeleton}`. Design tokens from `index.css` (`bg-surface`, `border-hairline`,
`text-ink2`, `bg-acc/12`, …) — never raw hex.

### Data access seam

Build `byKey: Map<string, SettingItem>` from `data.settings` once (`useMemo`). Two helpers passed
to every category:

- `get(key) = drafts[key] ?? byKey.get(key)?.value ?? ""`
- `set(key, value)` → `setDrafts(d => ({ ...d, [key]: value }))`
- `locked(key) = byKey.get(key)?.editable === false` (env-set) → field disabled + "via environment" pill
- `isSecretSet(key) = byKey.get(key)?.isSet` → secret placeholder "•••••• (set)"

`drafts: Record<string,string>` is unchanged from today; both the guided view and the raw view
read/write the same record. Save maps `"" → null` (reset to default), exactly like today.

### State (in `SettingsPage`)

- `activeCategory: "instance" | "git" | "ai" | "review" | "signin"` — local, no routing (mirrors
  the `AppPage` local-state pattern).
- `rawMode: boolean` — persisted in `localStorage` (`naudit.settings.rawMode`).
- `drafts: Record<string,string>` — unchanged.
- `wizard: null | { kind: "github-signin" | "github-app" | "oidc"; step: 1|2|3 }`.

### Top-of-pane chrome (kept from today)

The recovery-mode banner, warnings, `restartPending` banner (+ Restart now), and save/restart
error banners stay at the top of the content pane, above the category content. The **Save changes**
button stays top-right of the content pane; `dirty = Object.keys(drafts).length > 0`.

## Categories

Status hints in the sidebar derive from effective settings (right-aligned, Space Mono 11px):

| Category | Hint source |
|---|---|
| Instance | `PublicBaseUrl` set → `✓` (acc), else `not set` (warn) |
| Git platform | platform name + `✓` when token/App-key **and** webhook secret set (`✓ GitHub`) else platform name (ink3) |
| AI provider | provider display name (`Claude Code`, `Ollama`, …) (ink3) |
| Review rules | gate at defaults → `defaults` (ink3), else `custom` (ink3) |
| Sign-in | `GitHub` / `SSO` when enabled, else `local only` (warn) |

### AI provider — conditional field matrix

Provider cards (2×2). Selecting a card writes `Naudit:Ai:Provider` and swaps the fields below:

| Provider | Fields shown |
|---|---|
| `Ollama` | Model (required), Endpoint (optional, default `http://localhost:11434`) |
| `Anthropic` | Model (required), API key (required, secret) |
| `OpenAICompatible` | Model (required), Endpoint (required), API key (required, secret) |
| `ClaudeCode` | Model only (optional, defaults to `sonnet`) + info strip about subscription auth |

Amber-dot footer: "Changes apply after a restart — you'll be prompted when you save."

### Git platform — conditional fields

Platform cards (GitLab / GitHub) write `Naudit:Git:Platform`.
- **GitLab:** BaseUrl, Token (secret), WebhookSecret (secret), PostVerdict (toggle).
- **GitHub:** Auth chips PAT ↔ App write `Naudit:GitHub:Auth` and swap:
  - `Pat` → Token (secret), Webhook secret (secret), API base URL (optional, `https://api.github.com`).
  - `App` → AppId, PrivateKey (secret), InstallationId (optional), Webhook secret (secret), API base URL.
  - Both: "Post a real review verdict" toggle → `Naudit:GitHub:PostVerdict`.
  - Upsell card "Run as a bot instead" with a secondary "Set up GitHub App →" that opens the App
    credential modal.

### Instance

- Public base URL input (`Naudit:PublicBaseUrl`) + derived webhook-URL `CopyRow`
  (`{base}/webhook/{platform-lowercased}`, from the selected platform).
- "Who gets reviews" — two radio cards Open / Registered → `Naudit:AccessGate:Mode`.

### Review rules

- Merge gate: two selects `Naudit:Review:Gate:MinSeverity` (default `High`) and `:MinConfidence`
  (default `Medium`) + a **plain-language preview strip** that re-renders from the current values:
  "…a merge is blocked when a finding is **{severity}** or worse and the AI is at least
  **{confidence}** confident. Everything else becomes a non-blocking comment."
- Review prompt: textarea → `Naudit:Review:SystemPrompt`; empty = built-in default (header pill
  "built-in default").

### Sign-in

Three horizontal cards:
- **GitHub sign-in** — pill off/on from `Ui:Auth:GitHub:Enabled`; "Set up GitHub sign-in →" opens
  the wizard.
- **Single sign-on (OIDC)** — pill from `Ui:Auth:Oidc:Enabled`; "Set up SSO →" opens the OIDC
  credential modal.
- **Local admin** — pill "✓ active"; "Change password" (out of scope for the redesign wiring —
  renders but points at existing behaviour / disabled if none; see Open questions).

## Wizards (frontend-only)

Shared `Modal`: 560px, `bg-surface border-border` radius 14px, over a dimmed blurred backdrop;
header title + "step n / 3"; footer left ghost action + 3 step dots + primary. `Esc`/backdrop
closes; Cancel/Back never lose entered values within the session.

### GitHub sign-in (3 steps)

1. **Create the OAuth app.** Intro + 3 numbered items: link to GitHub → New OAuth app, copyable
   callback URL `CopyRow` value **`{PublicBaseUrl}/auth/callback/github`** (the real
   `CallbackPath` from `Program.cs`; the handoff's `/auth/github/callback` is wrong), Client ID +
   Client secret inputs. Footer: Cancel / Continue →.
2. **Review & save (was "Verify").** Saves all three keys in one `useSaveSettings` call —
   `Ui:Auth:GitHub:Enabled=true`, `:ClientId`, `:ClientSecret` — shows a static checklist
   ("✓ Client ID saved", "✓ Callback URL configured") and an honest note: "GitHub sign-in turns on
   after a restart — we can't test it before then." Continue enabled once the creds are present.
   Footer: ← Back / Continue →.
3. **Done.** ✓ badge, "GitHub sign-in is ready", copy about "Continue with GitHub" appearing on the
   login page after restart + Approvals. Summary rows. Footer: "Restart later" (ghost) / "Restart
   now to apply" (primary → `useRestartApp`).

### GitHub App / OIDC (single-step credential modals, same shell)

- **GitHub App:** AppId, PrivateKey (secret, textarea), InstallationId (optional) → save + restart
  prompt. Sets `Naudit:GitHub:Auth=App` on save.
- **OIDC:** Authority, ClientId, ClientSecret (secret) → save; sets `Ui:Auth:Oidc:Enabled=true`.

## Raw keys (expert mode)

Toggle in the sidebar (persisted). ON: sidebar categories dim; content becomes the flat catalog —
heading "Raw keys" + description, a filter input, and one row per `data.settings` entry (today's
`SettingRow` logic moved into `RawKeys.tsx`): mono key name, input/select, source pills (`db` /
`via environment` + locked value), secrets show "•••••• (set)". Count is **dynamic**:
"Showing all {data.settings.length} DB-managed keys" (not a hardcoded 27). Same drafts state, same
save/restart.

## Smoothness (dezente CSS-Transitions, no library)

- **Category switch:** no skeleton — `useSettings` data is cached (`staleTime`), switching is a
  client-only state change. The content pane gets `key={activeCategory}` and a ~150ms fade-in.
- **Conditional swaps** (provider/platform/auth) and **modal** enter: fade / subtle scale ~150ms.
- **Toggles:** 30×17px pill, `transition-* duration-150`; off `bg-elev`/knob left, on
  `bg-acc/25 border-acc`/knob right.
- Small `@keyframes fadein` (and modal in) added to `index.css`; everything respects
  `prefers-reduced-motion` (`motion-reduce:animate-none` / `@media (prefers-reduced-motion)`).
- Skeleton on the *initial* load stays (the two-pane skeleton), only the per-category switch is
  transition-only.

## Error handling

- Save / restart errors: existing top-of-pane danger banners (unchanged).
- Env-locked keys: non-editable in both views (disabled input + "via environment" pill) — cannot be
  written, matching the API which rejects them.
- Wizard save failure: inline error inside the modal; the modal stays open with values intact.

## Testing / verification

Frontend has no unit-test harness (repo convention = lint + build):

- `npm run lint` and `npm run build` (`tsc --noEmit && vite build`) green.
- `dotnet build`/`dotnet test Naudit.slnx` unaffected (no backend change) — run once to confirm.
- Manual walk-through (throttled network): initial skeleton → no jump; category switch fades, no
  skeleton; AI/Git conditional fields swap on card/chip change; Raw-keys toggle persists across
  reload; env-locked field disabled; sign-in wizard saves creds and the restart prompt fires;
  `prefers-reduced-motion` disables the pulses/fades.

## Open questions / flags

- **"Change password" (Local admin card):** no existing self-service change-password endpoint was
  confirmed. For this PR the button renders but is wired only if such an endpoint exists; otherwise
  it is disabled with a tooltip. Flag to confirm during implementation.
- **GitHub-App manifest automation from Settings:** out of scope (see Scope). Revisit as a separate
  backend+frontend slice if wanted.

## Files (summary)

New: `settings/SettingsSidebar.tsx`, `settings/RawKeys.tsx`, `settings/primitives.tsx`,
`settings/categories/{Instance,Git,Ai,Review,SignIn}Category.tsx`,
`settings/wizards/SignInWizard.tsx`. Rewrite: `pages/SettingsPage.tsx`. Edit: `index.css`
(keyframes). No API, catalog, or type changes.

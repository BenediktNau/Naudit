# Handoff: Naudit Settings Page Redesign

## Overview

Redesign of the admin **Settings** page in the Naudit WebUI (`src/frontend/src/components/pages/SettingsPage.tsx`). The current page renders the raw `SettingsCatalog` key/value list in five panels — functional but overwhelming. The redesign (direction **2a** in the design file) replaces it with:

1. **Sidebar category navigation** with per-category status (Instance, Git platform, AI provider, Review rules, Sign-in).
2. **Conditional fields** — only the fields the current selection actually needs are shown (e.g. `Ai:Provider = ClaudeCode` → only the Model field, no API key/endpoint).
3. **One-click guided setups** — multi-step wizard modals for GitHub sign-in (and, by the same pattern, GitHub App and OIDC).
4. **Raw keys expert mode** — a toggle that shows the full flat key catalog, preserving today's power-user workflow.

## About the Design Files

The file in this bundle (`Settings Redesign.dc.html`) is a **design reference created in HTML** — a static mock showing intended look and behavior, not production code. The task is to **recreate these designs in the existing Naudit frontend**: React 19 + TypeScript + Tailwind CSS v4 + TanStack Query, using the existing components (`Panel`, `Pill`, `Button`, `Skeleton`) and the setup-wizard primitives (`Field`, `CopyRow`, `randomSecret` in `src/frontend/src/components/setup/shared.tsx`).

The design file is a canvas with multiple iterations. **Implement section `2a` only** (the topmost group of screens, badge "2a"). Sections 1a/1b/1c are earlier explorations and the current-page reference.

## Fidelity

**High-fidelity.** Colors, typography, spacing, and copy are final and use the existing Naudit design tokens (from `src/frontend/src/index.css`). Recreate pixel-perfectly with Tailwind token classes (`bg-surface`, `border-hairline`, `text-ink2`, …) rather than raw hex values.

## Screens / Views (section 2a, in order)

All screens share the existing app shell (`TopBar`, max-width 1200 container) and a two-pane settings layout:

- **Sidebar** (230px, `border-r border-hairline`, padding 20px 14px): "SETTINGS" eyebrow (Space Mono 11px bold, uppercase, tracking 0.12em, `text-ink3`), then one nav item per category. Item: flex row space-between, radius 8px, padding 10px 12px, 13px font. Inactive: `font-medium text-ink2`. Active: `font-semibold text-acc bg-acc/12`. Right side of each item is a status hint in Space Mono 11px: `✓` / `✓ GitHub` (`text-acc`), `Claude Code` / `defaults` (`text-ink3`), `local only` (`text-warn`). At the bottom (border-top hairline): **Raw keys** toggle (30×17px pill switch) with caption "Show every setting as its config key" (11px `text-ink3`).
- **Content pane** (flex-1, padding 28px 32px, vertical gap 20px): category `h2` (Space Mono 18px bold) + one-sentence description (13px `text-ink2`, max-width 56ch), a primary **Save changes** button top-right, then the category's panels.

### 1. AI provider
- **Purpose:** pick the review LLM; only relevant fields appear.
- **Provider cards** (2×2 grid, gap 12px): border `border-border`, radius 10px, padding 16px. Title 14px bold + right-aligned requirement tag (Space Mono 11px `text-ink3`): Ollama "local", Anthropic "API key", OpenAI-compatible "API key + URL", Claude Code (selected). Selected card: `border-acc bg-acc/6`, tag "✓ selected" in `text-acc` bold. Description 12.5px `text-ink2` line-height 1.5.
- **Conditional panel** "Claude Code settings" (Panel component, header extra "2 of 2 fields shown"): Model select (280px wide, value `sonnet`, hint "Optional — defaults to sonnet.") + info strip (bg-elev, border-border, radius 8px, 12.5px `text-ink2`): "Claude Code signs in with the subscription already configured on the server — there is no endpoint or API key to manage here."
- **Conditional field matrix** (drives which fields render):
  - `Ollama` → Model (required), Endpoint (optional, default `http://localhost:11434`)
  - `Anthropic` → Model (required), API key (required, secret)
  - `OpenAICompatible` → Model (required), Endpoint (required), API key (required, secret)
  - `ClaudeCode` → Model only (optional, defaults to `sonnet`)
- Footer note with amber dot: "Changes apply after a restart — you'll be prompted when you save."

### 2. Git platform
- **Platform cards** (1×2 grid): GitLab / GitHub (selected). Selecting GitLab swaps the connection panel to GitLab fields (BaseUrl, Token, WebhookSecret, PostVerdict).
- **"GitHub connection" panel** (header pill "✓ connected"):
  - Auth chips: "✓ Personal access token" (selected: `border-acc bg-acc/10 text-acc`) | "GitHub App (bot)". Switching to App swaps the fields to AppId / PrivateKey / InstallationId.
  - Access token (secret, 360px, shows "•••••• (set)"), hint "Fine-grained PAT with pull request read/write. Comments appear as the token's owner."
  - Webhook secret (secret), hint "Must match the secret entered in the repo's webhook settings."
  - API base URL (optional), value `https://api.github.com`, hint "Only change this for GitHub Enterprise."
  - Toggle row (above a top hairline): "Post a real review verdict" + hint "APPROVE / REQUEST_CHANGES instead of a plain comment." → `Naudit:GitHub:PostVerdict`.
- **Upsell card** "Run as a bot instead" with body copy and secondary button "Set up GitHub App →" (launches a wizard analogous to the GitHub sign-in one; content per `docs/github-app.md` manifest flow).

### 3. Sign-in
- Three horizontal cards (Panel-style, padding 20px, flex space-between):
  - **GitHub sign-in** — pill "off" (neutral), copy "Let anyone with a GitHub account sign in. New accounts wait on your Approvals page until you let them in." + footnote "Takes ~2 minutes — we'll walk you through creating the OAuth app." Primary button **"Set up GitHub sign-in →"** opens the wizard.
  - **Single sign-on (OIDC)** — pill "off", copy about IdPs, secondary button "Set up SSO →".
  - **Local admin** — pill "✓ active" (ok), mono summary "user benedikt · password set", secondary button "Change password".
- Footer hint: "Need a key that isn't here? Flip on **Raw keys** in the sidebar to edit the full config catalog."

### 4–6. GitHub sign-in wizard (modal, 3 steps)
Modal: 560px, `bg-surface border-border` radius 14px, shadow `0 24px 64px rgba(0,0,0,.5)`, over a dimmed (`rgba(6,9,13,.6)`) blurred page. Header: title (Space Mono 15px bold) + "step n / 3" (Space Mono 12px `text-ink3`). Footer: left ghost action, right = 3 step dots (6px, active `bg-acc`, inactive `bg-border`) + primary button.

- **Step 1 — Create the OAuth app.** Intro sentence, then 3 numbered steps (22px numbered circles, Space Mono, `text-acc`): (1) link "GitHub → Settings → Developer settings → New OAuth app", (2) copyable callback URL row (`CopyRow`; value `{PublicBaseUrl}/auth/github/callback`), (3) Client ID input + Client secret input (secret). Footer: Cancel / Continue →.
- **Step 2 — Verify.** "Credentials saved. Let's make sure GitHub accepts them before turning sign-in on." Checklist box (bg-bg, border-hairline, radius 10px): "✓ Client ID recognized by GitHub", "✓ Callback URL matches the OAuth app", spinner row "Waiting for a test sign-in…". Primary "Test sign-in with GitHub →" (opens GitHub in a new tab) + caption. Footer: ← Back / Continue (disabled until the test passes; disabled = opacity .5).
- **Step 3 — Done.** Centered 52px ✓ badge (`bg-acc/12 border-acc text-acc`), heading "GitHub sign-in is ready", copy about "Continue with GitHub" + Approvals. Summary box rows: OAuth client `Iv1.…`, Test sign-in "✓ passed" (acc), Applies "after restart" (warn). Footer: "Restart later" (ghost) / **"Restart now to apply"** (primary, calls the existing restart mutation).
- Wizard writes `Naudit:Ui:Auth:GitHub:Enabled=true`, `:ClientId`, `:ClientSecret` via the existing save-settings mutation.

### 7. Instance
- **"Address" panel:** Public base URL input (360px, `https://naudit.tegut.com` as example), hint "Used to build the webhook and sign-in callback URLs below." Below: copyable webhook-URL row (520px, label "Webhook URL — paste into your repo's webhook settings", value `{PublicBaseUrl}/webhook/github`, derived from the selected platform).
- **"Who gets reviews" panel:** two radio cards — **Open** (selected) "Any repo that knows the webhook secret gets reviews." / **Registered only** "Only repos belonging to approved dashboard accounts. Everyone else is ignored." → `Naudit:AccessGate:Mode`.

### 8. Review rules
- **"Merge gate" panel:** two selects side by side (220px): Minimum severity = `High` (hint "Findings below this never block."), Minimum confidence = `Medium` (hint "How sure the AI must be."). Below, a **plain-language preview strip** (bg-elev) that re-renders from the current values: "With these rules, a merge is blocked when a finding is **High** or worse and the AI is at least **Medium** confident. Everything else becomes a non-blocking comment."
- **"Review prompt" panel** (header pill "built-in default"): textarea (min-height 88px, mono 13px) with placeholder "Using the built-in review prompt. Write your own here to override it." + hint "Clearing the field goes back to the built-in prompt."

### 9. Raw keys (expert mode)
- Toggle ON state: track `bg-acc/25 border-acc`, knob right `bg-acc`; caption becomes "Showing all 27 DB-managed keys"; sidebar categories dim to `text-ink3`.
- Content: heading "Raw keys" + description "Every DB-managed key, exactly as in the config. Keys set via environment are locked — environment always wins." Top-right: filter input (240px, "⌕ filter keys…").
- Flat panel of rows (like today's SettingRow): mono 13px key name left, input/select right (300px). Source pills: `db` (ok pill) on DB-set keys; `via environment` (neutral pill) + locked value "•••" on env keys. Secrets show "•••••• (set)".
- Same save/restart behaviour as the guided view; the guided view and raw view edit the same draft state.

## Interactions & Behavior

- **Category nav:** click switches content pane; no routing change needed (mirror the `AppPage` pattern with local state).
- **Conditional rendering:** provider cards / platform cards / auth chips swap the field set below immediately (no save needed). Draft state is keyed by config key, exactly like today's `drafts` record.
- **Save flow (unchanged semantics):** Save writes drafts via `useSaveSettings`; empty string = reset to default; success → restart banner (existing pattern: "Pending changes — restart Naudit to apply." + Restart now). Keep the existing recovery-mode and warning banners at the top of the content pane.
- **Env-locked keys:** non-editable in both views; in the guided view show the field disabled with a "via environment" pill.
- **Wizard:** Continue on step 2 stays disabled until the test callback succeeds. Cancel/Back never lose entered values within the session. Step 3 "Restart now to apply" calls `useRestartApp`.
- **Toggles/switches:** 30×17px pill, 13px knob; off = `bg-elev border-border`, knob `bg-ink3` left; on = `bg-acc/25 border-acc`, knob `bg-acc` right. Transition ~150ms ease.
- **Hover states:** follow existing components (`Button` variants, nav tab hover `hover:text-ink`). Cards: hover `border-ink3` on unselected selectable cards.
- **Status hints in sidebar** derive from effective settings: platform connected (token+secret set), provider name, gate = defaults vs custom, sign-in = "local only" / "GitHub" / "SSO".

## State Management

- `activeCategory: "instance" | "git" | "ai" | "review" | "signin"`
- `rawMode: boolean` (persist in `localStorage`)
- `drafts: Record<string, string>` — unchanged from current implementation; both views read/write it
- `wizard: null | { kind: "github-signin" | "github-app" | "oidc"; step: 1|2|3 }`
- Wizard step 2 needs a verify endpoint (new backend work) or can be reduced to save-then-instruct if out of scope — flag this to the team.
- Data: existing `useSettings`, `useSaveSettings`, `useRestartApp` hooks.

## Design Tokens (existing, from `src/frontend/src/index.css`)

- Colors: bg `#0d1117`, surface `#141a22`, elev `#1a222c`, border `#242d38`, hairline `#1b232d`, ink `#e6edf3`, ink2 `#98a5b3`, ink3 `#5f6b78`, acc `#4ade80`, acc2 `#22c55e`, accink `#06170c`, teal `#53d3d1`, warn `#e3b341`, danger `#f47067`
- Fonts: sans "Space Grotesk", mono "Space Mono"
- Radii: panels/cards 12px, inner cards 10px, inputs/buttons 8px, pills 999px, modal 14px
- Type scale used: 11 / 11.5 / 12 / 12.5 / 13 / 13.5 / 14 / 14.5 / 15 / 17 / 18px
- Spacing: content padding 28px 32px, panel padding 16–20px, row padding 12px 20px, grid gaps 12–20px

## Assets

No new assets. Logo/icon already exist (`src/frontend/src/components/ui/Logo.tsx`, `assets/naudit-icon.svg`). Glyphs (✓, ⌕, ▾, →) are plain text characters, consistent with the codebase's no-icon-library approach.

## Files

- `Settings Redesign.dc.html` — the design canvas. Section **2a** (top) is the spec to implement; **1a/1b** are earlier explorations; **1c** is a reference recreation of the current page.

## Key config mapping (for reference)

Categories → `SettingsCatalog` keys:
- Instance: `Naudit:PublicBaseUrl`, `Naudit:AccessGate:Mode`
- Git platform: `Naudit:Git:Platform`, `Naudit:GitLab:*`, `Naudit:GitHub:*` (incl. `App:*`, `PostVerdict`)
- AI provider: `Naudit:Ai:Provider|Model|Endpoint|ApiKey`
- Review rules: `Naudit:Review:SystemPrompt`, `Naudit:Review:Gate:MinSeverity|MinConfidence`
- Sign-in: `Naudit:Ui:Auth:GitHub:*`, `Naudit:Ui:Auth:Oidc:*`

All UI copy is English and intentionally centralized-ready for later i18n extraction.

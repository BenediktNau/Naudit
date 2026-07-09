# WebUI smoothing — design

*2026-07-08 · Naudit · branch `feat/webui-smoothing` (stacked on `feat/webui-reactivity`)*

## Problem

Every data-backed page swaps a hard `loading…` mono-text for the real content once the
`useQuery` resolves (`DashboardPage`, `ApprovalsPage`, `ProfilePage`, `SettingsPage`,
`ReviewDetail`, and the `AuthGate` initial `/api/me` gate). The result is a jarring
layout jump — the page snaps from a single line of text to a full grid.

Goal: replace the text swaps with **skeleton placeholders that mirror the real layout**, so
loading feels smooth and nothing jumps. Lean scope: skeletons only. `staleTime` already landed
in the reactivity PR; `placeholderData: keepPreviousData` is **deliberately dropped** — the
dashboard uses an accordion (each expanded review is a fresh `<ReviewDetail>` mount, not a single
detail pane whose id changes), so it would be dead code, and `staleTime: 30s` already serves the
cache instantly on re-open.

## Approach (chosen: one primitive + per-page layout skeletons)

A single CSS-only `Skeleton` primitive, composed per page into a layout that matches the real
one (right number of tiles/panels/rows at the right heights). Rejected alternative: a generic
"a few grey blocks" skeleton — less code but still jumps, which defeats the purpose.

## Changes

1. **`src/frontend/src/components/ui/Skeleton.tsx` (new).** `<Skeleton className="…" />` = a
   `div` with `animate-pulse rounded bg-elev motion-reduce:animate-none` (respects
   `prefers-reduced-motion`); caller sets size via `className`. No dependency.

2. **`DashboardPage`, `ApprovalsPage`, `ProfilePage`, `SettingsPage`.** Replace the
   `if (isLoading) return <div>loading…</div>` branch with a layout-faithful skeleton (same
   grid/panel structure, approximate row heights). Error and empty branches stay as-is.

3. **`ReviewDetail`.** Replace the `loading…` line with a few skeleton lines inside the same
   `border-b … px-10 py-4` container so the accordion row keeps its footprint while loading.

4. **`lib/auth.tsx`.** The initial `me === null` gate can resolve to either the login screen or
   the app, so a full app-shell skeleton would be wrong. Replace only the `loading…` **text**
   with the same subtle centered CSS spinner used by the buttons — consistent, minimal.

## Out of scope

`keepPreviousData` / optimistic updates (see above); responsive/mobile layout; a router; any
change to what the pages render once loaded.

## Testing / verification

Frontend has no unit-test harness (repo convention: lint + build):

- `npm run lint` and `npm run build` (`tsc --noEmit && vite build`) green.
- Manual: throttle the network and confirm each page shows a layout-matching skeleton that does
  **not** jump when the data arrives, and that reduced-motion disables the pulse.

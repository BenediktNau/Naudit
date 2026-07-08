# WebUI reactivity — design

*2026-07-08 · Naudit · branch `worktree-feat+webui-reactivity`*

## Problem

The WebUI (`src/frontend`, React 19 + TanStack Query v5) reads data via `useQuery`, but
**write actions bypass the query cache**: the account actions in `api/accounts.ts`
(create/approve/reject/revoke/setGitHubLinks) are plain `api()` calls, and **logout**
(`lib/auth.tsx`) resets `me` without clearing the query cache. Consequences:

- Approve/reject/revoke give **no in-flight feedback** (buttons stay live → double-click),
  **no error surface** (errors are swallowed by `void run(...)`), and only `["accounts"]`
  is invalidated by hand.
- Logout leaves the previous user's cached data in the query cache → stale/flashing data on
  the next login; it also feels like a reload is required.

Goal (agreed scope): make actions feel reactive and snappy — **no manual page reload** — with
a **lean, refetch-after-response** model (no responsive-layout work, no optimistic updates).

## Approach (chosen: A — minimal churn)

Keep the existing auth model (`AuthGate` custom `me` state + global 401 interceptor) as is.
Route the write actions through TanStack `useMutation` with correct cache invalidation, add
in-flight + error UI, and clear the query cache on logout. Rejected alternative B (migrate
`me` to `useQuery`, drop the custom state/interceptor) — more churn/risk in the auth core for
no user-visible gain.

## Changes

1. **`src/frontend/src/hooks/mutations.ts` (new).** One `useMutation` per action:
   `useApproveAccount`, `useRejectAccount`, `useRevokeAccount`, `useCreateAccount`,
   `useSetGitHubLinks`. Each wraps the corresponding `api/accounts.ts` function and, on
   success, invalidates the affected query keys:
   - approve / reject / revoke → invalidate `["accounts"]`.
   - create → invalidate `["accounts"]`.
   - setGitHubLinks → invalidate `["accounts"]` **and** `["dashboard"]` (project ownership
     shown on the dashboard can change).

   Rationale for keys: only the accounts list and the ownership-derived dashboard view depend
   on these actions; historical usage/review data does not.

2. **`src/frontend/src/components/ui/Button.tsx`.** Add an optional `loading?: boolean` prop:
   when set, the button is disabled, shows `aria-busy`, and renders a small inline spinner
   (CSS-only, no dependency). This standardises in-flight feedback.

3. **`src/frontend/src/components/pages/ApprovalsPage.tsx`.** Replace the hand-rolled
   `run`/`refresh` with the mutation hooks:
   - Each action button binds to its mutation; `loading={mutation.isPending}` disables it and
     shows the spinner.
   - On error, show an inline message on the affected row (per-`AccountRow` error state), not
     a swallowed `void`. `createAccount` keeps its existing inline form error.
   - Drop the manual `queryClient.invalidateQueries` — invalidation now lives in the hooks.

4. **`src/frontend/src/lib/auth.tsx`.** In `logout`: `await api("/auth/logout", …)` →
   `queryClient.clear()` → `refresh()` (re-fetch `/api/me` → `LoginPage`). `AuthGate` gets a
   `useQueryClient()` handle. This removes both the stale-cache flash and the reload feel.

5. **`src/frontend/src/main.tsx`.** Set a modest default `staleTime` (30 s) on the
   `QueryClient` so tab switches don't refetch-flicker on every click; freshness after actions
   comes from the explicit invalidations above. (`refetchOnWindowFocus` stays `false`.)

## Out of scope

Responsive/mobile layout; optimistic updates; replacing the `window.prompt` used for editing
GitHub links; introducing a router; a toast system (errors stay inline).

## Testing / verification

The frontend has **no unit-test harness** in this repo (lint + `tsc` build only), so this
follows the repo's frontend convention:

- `npm run lint` and `npm run build` (`tsc --noEmit && vite build`) green.
- Manual end-to-end via the running app (`verify` skill): sign in, approve/reject/revoke a
  pending account and confirm the list updates **without a reload** and shows in-flight +
  error states; sign out and confirm it returns to the login screen with no stale data and no
  reload. Exercised against the .NET host (`dotnet run`, UI+DB enabled) with the Vite dev
  proxy.

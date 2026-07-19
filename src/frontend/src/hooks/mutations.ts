import { useMutation, useQueryClient } from "@tanstack/react-query";
import { approveAccount, createAccount, rejectAccount, revokeAccount, setGitHubLinks } from "@/api/accounts";
import { api } from "@/api/client";

/** Gemeinsame Basis: nach Erfolg die betroffenen Queries invalidieren (Refetch) — so
 *  aktualisiert sich die UI ohne Reload. Die Account-Aktionen betreffen immer die
 *  Accounts-Liste; zusätzliche Keys je Aktion via extraKeys. */
function useAccountMutation<TVars>(
  mutationFn: (vars: TVars) => Promise<unknown>,
  extraKeys: string[][] = [],
) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn,
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: ["accounts"] });
      for (const key of extraKeys) void qc.invalidateQueries({ queryKey: key });
    },
  });
}

export const useApproveAccount = () => useAccountMutation<number>(approveAccount);
export const useRejectAccount = () => useAccountMutation<number>(rejectAccount);
export const useRevokeAccount = () => useAccountMutation<number>(revokeAccount);

// GitHub-Links ändern die Projekt-Zuordnung → zusätzlich das Dashboard invalidieren.
export const useSetGitHubLinks = () =>
  useAccountMutation<{ id: number; logins: string[] }>(
    ({ id, logins }) => setGitHubLinks(id, logins),
    [["dashboard"]],
  );

export const useCreateAccount = () =>
  useAccountMutation<Parameters<typeof createAccount>[0]>(createAccount);

/** FP-Markierung am Finding — invalidiert das Review-Detail (Flag kommt vom Server zurück). */
export function useMarkFalsePositive(reviewId: number) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ findingId, reason }: { findingId: number; reason?: string }) =>
      api<{ id: number; active: boolean }>(`/api/findings/${findingId}/false-positive`, {
        method: "POST",
        body: JSON.stringify({ reason: reason ?? null }),
      }),
    // Promise zurückgeben: isPending deckt so auch den Refetch ab — die Detail-Buttons
    // bleiben disabled, bis der neue Server-Zustand wirklich da ist (kein Klick auf stale Status).
    onSuccess: () =>
      Promise.all([
        qc.invalidateQueries({ queryKey: ["review", reviewId] }),
        qc.invalidateQueries({ queryKey: ["memory"] }), // Prefix-Match: alle Projekt-Gedächtnisse
      ]),
  });
}

export function useUnmarkFalsePositive(reviewId: number) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (findingId: number) =>
      api<void>(`/api/findings/${findingId}/false-positive`, { method: "DELETE" }),
    onSuccess: () =>
      Promise.all([
        qc.invalidateQueries({ queryKey: ["review", reviewId] }),
        qc.invalidateQueries({ queryKey: ["memory"] }), // Prefix-Match: alle Projekt-Gedächtnisse
      ]),
  });
}

/** Neue Konvention am Projekt-Gedächtnis anlegen. */
export function useCreateConvention(projectId: number | null) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (vars: { text: string; file?: string }) =>
      api<{ id: number }>(`/api/projects/${projectId}/memory`, {
        method: "POST",
        body: JSON.stringify({ text: vars.text, file: vars.file ?? null }),
      }),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ["memory", projectId] }),
  });
}

/** Gedächtnis-Eintrag aktivieren/deaktivieren (Soft-Toggle, bleibt fürs Audit erhalten). */
export function useToggleMemoryEntry(projectId: number | null) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (vars: { id: number; active: boolean }) =>
      api<{ id: number; active: boolean }>(`/api/memory/${vars.id}`, {
        method: "PUT",
        body: JSON.stringify({ active: vars.active }),
      }),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ["memory", projectId] }),
  });
}

/** Accept/Reject am Finding — invalidiert das Review-Detail (Status kommt vom Server zurück). */
export function useSetResolution(reviewId: number | null) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (vars: { findingId: number; status: "Accepted" | "Rejected" | null }) =>
      api<{ id: number; resolutionStatus: string | null }>(`/api/findings/${vars.findingId}/resolution`, {
        method: "PUT",
        body: JSON.stringify({ status: vars.status }),
      }),
    // Promise zurückgeben (statt void): Accept/Reject bleiben über isPending disabled,
    // bis das Review-Detail refetcht ist — sonst kurzes Fenster mit stale resolutionStatus.
    onSuccess: () => qc.invalidateQueries({ queryKey: ["review", reviewId] }),
  });
}

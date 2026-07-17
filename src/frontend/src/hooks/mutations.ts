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
    onSuccess: () => void qc.invalidateQueries({ queryKey: ["review", reviewId] }),
  });
}

export function useUnmarkFalsePositive(reviewId: number) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (findingId: number) =>
      api<void>(`/api/findings/${findingId}/false-positive`, { method: "DELETE" }),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ["review", reviewId] }),
  });
}

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
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: ["review", reviewId] });
      void qc.invalidateQueries({ queryKey: ["memory"] }); // Prefix-Match: alle Projekt-Gedächtnisse
    },
  });
}

export function useUnmarkFalsePositive(reviewId: number) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (findingId: number) =>
      api<void>(`/api/findings/${findingId}/false-positive`, { method: "DELETE" }),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: ["review", reviewId] });
      void qc.invalidateQueries({ queryKey: ["memory"] }); // Prefix-Match: alle Projekt-Gedächtnisse
    },
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

/** Architektur-Profil manuell kuratieren (überschreibt, stoppt Auto-Re-Destillation). */
export function useSaveGuidelines(projectId: number | null) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (vars: { markdown: string }) =>
      api<{ manuallyEdited: boolean }>(`/api/projects/${projectId}/guidelines`, {
        method: "PUT",
        body: JSON.stringify({ markdown: vars.markdown }),
      }),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ["guidelines", projectId] }),
  });
}

/** Neu-Destillation anstoßen (auf dem nächsten Review — kein Inline-LLM-Call in der WebUI). */
export function useRedistillGuidelines(projectId: number | null) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: () =>
      api<{ pending: boolean }>(`/api/projects/${projectId}/guidelines/redistill`, { method: "POST" }),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ["guidelines", projectId] }),
  });
}

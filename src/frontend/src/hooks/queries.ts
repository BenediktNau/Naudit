import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api } from "@/api/client";
import type {
  AccountsDto,
  AnalyticsDto,
  ClaudeSessionDto,
  ClaudeSessionTest,
  DashboardDto,
  GitHubAppDto,
  ProjectMemoryDto,
  ReviewDetailDto,
  SettingsDto,
  UsageDto,
} from "@/api/types";

export function useDashboard() {
  return useQuery({ queryKey: ["dashboard"], queryFn: () => api<DashboardDto>("/api/dashboard") });
}

export function useReviewDetail(id: number | null) {
  return useQuery({
    queryKey: ["review", id],
    queryFn: () => api<ReviewDetailDto>(`/api/reviews/${id}`),
    enabled: id !== null,
  });
}

export function useAccounts() {
  return useQuery({ queryKey: ["accounts"], queryFn: () => api<AccountsDto>("/api/accounts") });
}

export function useSettings() {
  return useQuery({ queryKey: ["settings"], queryFn: () => api<SettingsDto>("/api/settings") });
}

export function useSaveSettings() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (changes: { key: string; value: string | null }[]) =>
      api<{ restartPending: boolean }>("/api/settings", {
        method: "PUT",
        body: JSON.stringify({ changes }),
      }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["settings"] }),
  });
}

export function useRestartApp() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: () => api<void>("/api/settings/restart", { method: "POST" }),
    // Host braucht ~2 s zum Neustart; danach Settings neu laden (Session-Cookie überlebt, DP-Keys in DB).
    onSuccess: () =>
      new Promise((resolve) => setTimeout(resolve, 2500)).then(() =>
        qc.invalidateQueries({ queryKey: ["settings"] })),
  });
}

export function useUsage() {
  return useQuery({ queryKey: ["usage"], queryFn: () => api<UsageDto>("/api/me/usage") });
}

/** Installations-Status der GitHub-App. 404 = Feature aus (nicht GitHub+App) ⇒ kein Retry,
 *  data bleibt undefined, das Banner rendert nichts. */
export function useGitHubApp() {
  return useQuery({
    queryKey: ["github-app"],
    queryFn: () => api<GitHubAppDto>("/api/me/github-app"),
    retry: false,
  });
}

export function useClaudeSession() {
  return useQuery({ queryKey: ["claude-session"], queryFn: () => api<ClaudeSessionDto>("/api/me/claude-session") });
}

export function useSaveClaudeSession() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (body: { token?: string; gitAuthorLogin?: string; shareInPool?: boolean }) =>
      api<void>("/api/me/claude-session", { method: "PUT", body: JSON.stringify(body) }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["claude-session"] }),
  });
}

export function useDeleteClaudeSession() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: () => api<void>("/api/me/claude-session", { method: "DELETE" }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["claude-session"] }),
  });
}

export function useTestClaudeSession() {
  return useMutation({
    mutationFn: () => api<ClaudeSessionTest>("/api/me/claude-session/test", { method: "POST" }),
  });
}

/** Gedächtnis-Einträge (FP-Markierungen + Konventionen) eines Projekts. */
export function useProjectMemory(projectId: number | null) {
  return useQuery({
    queryKey: ["memory", projectId],
    queryFn: () => api<ProjectMemoryDto>(`/api/projects/${projectId}/memory`),
    enabled: projectId !== null,
  });
}

/** Auswertungs-Kennzahlen (Totals/Raten, Severity-Breakdown, Wochentrend, Gedächtnis-Wirkung). */
export function useAnalytics(projectId: number | null, days: number) {
  return useQuery({
    queryKey: ["analytics", projectId, days],
    queryFn: () => api<AnalyticsDto>(`/api/analytics?days=${days}${projectId ? `&projectId=${projectId}` : ""}`),
  });
}

/** 1_240_000 → "1.24M", 38_400 → "38k", 950 → "950" */
export function fmtTokens(n: number): string {
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(2).replace(/\.?0+$/, "")}M`;
  if (n >= 1_000) return `${Math.round(n / 1_000)}k`;
  return `${n}`;
}

import { useQuery } from "@tanstack/react-query";
import { api } from "@/api/client";
import type { AccountsDto, DashboardDto, ReviewDetailDto, SettingsDto, UsageDto } from "@/api/types";

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

export function useUsage() {
  return useQuery({ queryKey: ["usage"], queryFn: () => api<UsageDto>("/api/me/usage") });
}

/** 1_240_000 → "1.24M", 38_400 → "38k", 950 → "950" */
export function fmtTokens(n: number): string {
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(2).replace(/\.?0+$/, "")}M`;
  if (n >= 1_000) return `${Math.round(n / 1_000)}k`;
  return `${n}`;
}

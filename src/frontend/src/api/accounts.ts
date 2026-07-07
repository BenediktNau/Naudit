import { api } from "@/api/client";
import type { AccountDto } from "@/api/types";

export function createAccount(body: {
  username: string;
  password: string;
  isAdmin?: boolean;
  gitHubLogins?: string[];
}): Promise<AccountDto> {
  return api<AccountDto>("/api/accounts", { method: "POST", body: JSON.stringify(body) });
}

export const approveAccount = (id: number) => api(`/api/accounts/${id}/approve`, { method: "POST" });
export const rejectAccount = (id: number) => api(`/api/accounts/${id}/reject`, { method: "POST" });
export const revokeAccount = (id: number) => api(`/api/accounts/${id}/revoke`, { method: "POST" });
export const setGitHubLinks = (id: number, logins: string[]) =>
  api(`/api/accounts/${id}/github-links`, { method: "PUT", body: JSON.stringify({ logins }) });

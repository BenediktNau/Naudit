// DTO-Typen — Spiegel der Backend-Kontrakte (Naudit.Web/Endpoints).

export interface AuthProviders {
  local: boolean;
  gitHub: boolean;
  oidc: boolean;
}

export interface MeDto {
  isAuthenticated: boolean;
  username: string | null;
  isAdmin: boolean;
  status: "Pending" | "Active" | "Rejected" | null;
  authProviders: AuthProviders;
}

export interface ReviewListItem {
  id: number;
  prNumber: number;
  title: string;
  verdict: "approve" | "request_changes";
}

export interface ProjectDto {
  id: number;
  name: string;
  lastReviewedAt: string;
  totalTokens: number;
  reviews: ReviewListItem[];
}

export interface RecentReviewDto {
  id: number;
  prNumber: number;
  title: string;
  project: string;
  verdict: "approve" | "request_changes";
  totalTokens: number;
  createdAt: string;
}

export interface DashboardDto {
  tokensMonth: number;
  reviewsTotal: number;
  reviewsWeek: number;
  projectsTotal: number;
  projectsNewMonth: number;
  tokensPerDay: { date: string; tokens: number }[];
  reviewsPerDay: { date: string; count: number }[];
  projects: ProjectDto[];
  recentReviews: RecentReviewDto[];
}

export interface FindingDto {
  severity: string;
  confidence: string;
  file: string | null;
  line: number | null;
  text: string;
}

export interface ReviewDetailDto {
  id: number;
  prNumber: number;
  title: string;
  project: string;
  verdict: "approve" | "request_changes";
  summary: string;
  model: string | null;
  inputTokens: number | null;
  outputTokens: number | null;
  createdAt: string;
  findings: FindingDto[];
}

export interface AccountDto {
  id: number;
  username: string;
  provider: "Local" | "GitHub" | "Oidc";
  status: "Pending" | "Active" | "Rejected";
  isAdmin: boolean;
  createdAt: string;
  gitHubLogins: string[];
  projectCount: number;
  totalTokens: number;
}

export interface AccountsDto {
  pending: AccountDto[];
  approved: AccountDto[];
}

export interface UsageDto {
  monthly: { month: string; tokens: number }[];
  reviewsTotal: number;
  avgTokens: number;
  perProject: { name: string; tokens: number }[];
}

export interface SettingsDto {
  ai: { provider: string; model: string };
  git: { platform: string; auth: string | null; postVerdict: boolean };
  authMethods: AuthProviders;
  systemPrompt: string;
}

export interface GitHubAppAccount {
  login: string;
  installed: boolean | null;
}

export interface GitHubAppDto {
  installUrl: string;
  accounts: GitHubAppAccount[];
}

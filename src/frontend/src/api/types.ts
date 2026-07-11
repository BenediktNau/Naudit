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

export interface SettingItem {
  key: string;
  isSecret: boolean;
  isSet: boolean;
  source: "db" | "env" | "default";
  editable: boolean;
  value: string | null;
}

export interface SettingsDto {
  recoveryError: string | null;
  warnings: string[];
  restartPending: boolean;
  settings: SettingItem[];
}

export interface GitHubAppAccount {
  login: string;
  installed: boolean | null;
}

export interface GitHubAppDto {
  installUrl: string;
  accounts: GitHubAppAccount[];
}

export interface SetupStatusDto {
  setupRequired: boolean;
  adminExists: boolean;
  missing: string[];
  suggestedPublicBaseUrl: string | null;
}

export interface SetupDraftDto {
  publicBaseUrl: string | null;
  platform: "GitHub" | "GitLab" | null;
  gitToken: string | null; // von der API immer maskiert (null) — hasGitToken zeigt "gesetzt"
  gitLabBaseUrl: string | null;
  webhookSecret: string | null;
  aiProvider: string | null;
  aiModel: string | null;
  aiEndpoint: string | null;
  aiApiKey: string | null; // von der API immer maskiert (null) — hasAiApiKey zeigt "gesetzt"
  accessGateMode: "Open" | "Registered" | null;
  gitHubAuth: "Pat" | "App" | null; // Wizard-Wahl (Naudit:GitHub:Auth)
  gitHubHost: string | null; // Web-Host (Default github.com; GHES: eigener Host)
  gitHubAppId: string | null; // aus dem Manifest-Callback (nur bei Auth=App)
  gitHubAppSlug: string | null; // fuer den Install-Link (PEM/state liefert der Server nie)
}

export interface SetupDraftResponse {
  draft: SetupDraftDto;
  hasGitToken: boolean;
  hasAiApiKey: boolean;
  hasGitHubApp: boolean;
}

export interface GitHubManifestResponse {
  action: string; // {host}/settings/apps/new?state=… (Form-POST-Ziel)
  manifest: Record<string, unknown>; // wird als Form-Feld "manifest" mitgeschickt
}

export interface GitLabHookResultDto {
  target: string; // ID oder Pfad des Ziels (Projekt/Gruppe)
  kind: "project" | "group";
  ok: boolean;
  status: number | null; // HTTP-Status der GitLab-API, null bei Netzwerkfehler
  detail: string; // menschenlesbares Ergebnis pro Ziel
}

export interface GitLabHooksResponse {
  results: GitLabHookResultDto[];
}

export interface ClaudeSessionDto {
  configured: boolean;
  updatedAtUtc: string | null;
  coolingDownUntil: string | null;
  gitAuthorLogin: string | null;
}

export interface ClaudeSessionTest {
  ok: boolean;
  error: string | null;
}

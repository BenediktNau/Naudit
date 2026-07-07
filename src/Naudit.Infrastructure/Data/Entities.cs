namespace Naudit.Infrastructure.Data;

public enum AccountStatus { Pending, Active, Rejected }
public enum AccountProvider { Local, GitHub, Oidc }

/// <summary>Ein UI-Account. Lokale Accounts (vom Admin angelegt) sind sofort Active;
/// Self-Service-Anmeldungen (GitHub/OIDC) starten Pending.</summary>
public sealed class AccountEntity
{
    public int Id { get; set; }
    public required string Username { get; set; }
    public string? PasswordHash { get; set; }          // nur bei Provider=Local
    public AccountProvider Provider { get; set; }
    public string? ExternalId { get; set; }            // GitHub-User-Id bzw. OIDC-sub
    public string? DisplayName { get; set; }
    public AccountStatus Status { get; set; }
    public bool IsAdmin { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<GitHubLinkEntity> GitHubLinks { get; set; } = new();
}

/// <summary>GitHub-Owner/Org-Zuordnung eines Accounts — Grundlage der Zugangsschranke.
/// Login wird lowercased gespeichert (case-insensitiver Vergleich per Gleichheit).</summary>
public sealed class GitHubLinkEntity
{
    public int Id { get; set; }
    public int AccountId { get; set; }
    public AccountEntity Account { get; set; } = null!;
    public required string Login { get; set; }
}

/// <summary>Auto-registriert beim ersten Review (Upsert im Audit-Sink).</summary>
public sealed class ProjectEntity
{
    public int Id { get; set; }
    public required string PlatformProjectId { get; set; }   // "owner/repo" (GitHub) bzw. GitLab-ProjectId
    public int? AccountId { get; set; }                       // besitzender Account (über Owner-Link ermittelt)
    public DateTime FirstReviewedAt { get; set; }
    public DateTime LastReviewedAt { get; set; }
    public List<ReviewEntity> Reviews { get; set; } = new();
}

public sealed class ReviewEntity
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public ProjectEntity Project { get; set; } = null!;
    public int PrNumber { get; set; }
    public required string Title { get; set; }
    public required string Verdict { get; set; }              // "approve" | "request_changes"
    public required string Summary { get; set; }
    public long? InputTokens { get; set; }
    public long? OutputTokens { get; set; }
    public string? Model { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<ReviewFindingEntity> Findings { get; set; } = new();
}

public sealed class ReviewFindingEntity
{
    public int Id { get; set; }
    public int ReviewId { get; set; }
    public ReviewEntity Review { get; set; } = null!;
    public required string Severity { get; set; }
    public required string Confidence { get; set; }
    public string? File { get; set; }
    public int? Line { get; set; }
    public required string Text { get; set; }
}

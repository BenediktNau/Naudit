namespace Naudit.Infrastructure.Git;

/// <summary>Welche Git-Plattform aktiv ist (per Config gewählt, eine pro Deployment).</summary>
public enum GitPlatformKind { GitLab, GitHub }

public sealed class GitOptions
{
    public GitPlatformKind Platform { get; set; } = GitPlatformKind.GitLab;
}

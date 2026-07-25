using Naudit.Core.Models;

namespace Naudit.Core.Abstractions;

/// <summary>Materialisiert den Quellcode eines ReviewRequests in ein lokales, wegwerfbares Verzeichnis.</summary>
public interface IWorkspaceProvider
{
    Task<IReviewWorkspace> CheckoutAsync(ReviewRequest request, CancellationToken ct = default);
}

/// <summary>Handle auf den ausgecheckten Quellbaum; DisposeAsync räumt das Temp-Verzeichnis auf.</summary>
public interface IReviewWorkspace : IAsyncDisposable
{
    string RootPath { get; }

    /// <summary>Projekt des Reviews (GitLab-Projekt-Id bzw. "owner/repo"). Analyzer bekommen den
    /// ReviewRequest nicht, brauchen die Kennung aber für projektweise Freigaben (DAST-Allowlist) —
    /// der Checkout kennt sie ohnehin aus dem Request.</summary>
    string ProjectId { get; }
}

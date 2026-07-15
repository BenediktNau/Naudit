using Naudit.Core.Models;

namespace Naudit.Infrastructure.Git;

/// <summary>Liefert den Login des MR-/PR-Autors für das Autor-Session-Routing.
/// Fail-quiet: kein Autor ermittelbar ⇒ null ⇒ Review läuft über den globalen Provider.</summary>
public interface IAuthorLoginResolver
{
    Task<string?> ResolveAsync(ReviewRequest request, CancellationToken ct = default);
}

/// <summary>GitHub: der Login steht schon im Request (Webhook-Mapping) — kein API-Call.</summary>
public sealed class PassthroughAuthorLoginResolver : IAuthorLoginResolver
{
    public Task<string?> ResolveAsync(ReviewRequest request, CancellationToken ct = default)
        => Task.FromResult(request.AuthorLogin);
}

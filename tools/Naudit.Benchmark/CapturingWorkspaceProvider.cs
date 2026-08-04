using Naudit.Core.Abstractions;
using Naudit.Core.Models;

namespace Naudit.Benchmark;

/// <summary>Reicht den Checkout durch und hält den tatsächlich ausgecheckten Commit fest.
///
/// <para>Das ist die Milderung zu einer Abweichung, die in der Arbeit stehen muss: die Vorlage ist
/// nicht eingefroren. Die 41 Vergleichstools reviewten einen Schnappschuss vom Januar 2026, wir
/// lesen den Upstream-Pull-Request heute. RepoCheckoutInfo.HeadRef allein hilft dabei nicht — das
/// ist immer "refs/pull/N/head" und sagt nichts darüber, WELCHER Stand das war. Nach
/// `git checkout FETCH_HEAD` steht in .git/HEAD ein detached HEAD, also die rohe Commit-SHA.
/// Kein zusätzlicher Netzaufruf, kein Unterprozess.</para>
///
/// <para>Nebenbei der belastbarste Nachweis, dass der Checkout wirklich materialisiert ist: ein
/// gescheiterter Klon wird hier als Checkout-Fehlschlag vermerkt. Die Klon-URL wird nirgends
/// festgehalten — sie trägt das Token.</para></summary>
public sealed class CapturingWorkspaceProvider(IWorkspaceProvider inner, ReviewCapture capture) : IWorkspaceProvider
{
    /// <summary>Der umhüllte, echte Provider — für den Verdrahtungstest.</summary>
    public IWorkspaceProvider Inner => inner;

    public async Task<IReviewWorkspace> CheckoutAsync(ReviewRequest request, CancellationToken ct = default)
    {
        IReviewWorkspace workspace;
        try
        {
            workspace = await inner.CheckoutAsync(request, ct);
        }
        catch
        {
            // Kein Workspace ⇒ diff-only, ohne Repo-Kontext und ohne frisches Profil. ReviewService
            // schluckt das still; derselbe Vermerk wie beim gescheiterten GetCheckoutAsync.
            capture.RecordCheckoutFailed();
            throw;
        }

        capture.RecordHeadSha(ReadHeadSha(workspace.RootPath));
        return workspace;
    }

    /// <summary>Liest .git/HEAD. Detached HEAD ⇒ rohe Commit-SHA; alles andere (symbolischer Ref,
    /// unlesbare Datei) ⇒ null. Best-effort: das darf kein Review kippen.</summary>
    internal static string? ReadHeadSha(string rootPath)
    {
        try
        {
            var head = File.ReadAllText(Path.Combine(rootPath, ".git", "HEAD")).Trim();
            return IsCommitSha(head) ? head : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsCommitSha(string s)
        => s.Length is 40 or 64 && s.All(Uri.IsHexDigit);
}

// src/Naudit.Infrastructure/Git/IReviewCommentResponder.cs
namespace Naudit.Infrastructure.Git;

/// <summary>Plattform-Fähigkeit für das FP-Antwort-Kommando: Autor autorisieren + Bestätigung
/// im Thread posten. Bewusst eine INFRASTRUKTUR-Naht (nicht IGitPlatform/Core) — das Kommando
/// ist rein Infrastructure/Web. Eine Implementierung je Plattform, per Config gewählt.</summary>
public interface IReviewCommentResponder
{
    /// <summary>Darf dieser Autor Findings als False Positive markieren? Fail-closed:
    /// unverifizierbar ⇒ false. GitHub prüft author_association (kein I/O), GitLab die Mitgliedschaft.</summary>
    Task<bool> IsAuthorizedAsync(ReviewCommentReply reply, CancellationToken ct = default);

    /// <summary>Postet die Bestätigung als Antwort in denselben Thread/dieselbe Discussion.</summary>
    Task PostReplyAsync(ReviewCommentReply reply, string body, CancellationToken ct = default);
}

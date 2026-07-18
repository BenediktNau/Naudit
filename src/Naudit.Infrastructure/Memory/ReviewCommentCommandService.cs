// src/Naudit.Infrastructure/Memory/ReviewCommentCommandService.cs
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Naudit.Infrastructure.Data;
using Naudit.Infrastructure.Git;

namespace Naudit.Infrastructure.Memory;

/// <summary>Verarbeitet ein "@naudit fp"-Antwort-Kommando: Autor autorisieren → Antwort dem Finding
/// zuordnen (über die in PR 2a erfasste PlatformCommentId, projekt-gescoped) → FP-Eintrag anlegen →
/// im Thread bestätigen. Alles fail-closed/best-effort — der Webhook antwortet immer 200.</summary>
public sealed class ReviewCommentCommandService(
    NauditDbContext db, IReviewCommentResponder responder, ILogger<ReviewCommentCommandService> logger)
{
    public const string ConfirmationText = "Als False Positive gemerkt.";

    public async Task HandleAsync(ReviewCommentReply reply, CancellationToken ct = default)
    {
        if (!await responder.IsAuthorizedAsync(reply, ct))
        {
            logger.LogInformation("FP-Kommando von {Author} auf {Project}!{Iid} ignoriert — nicht autorisiert.",
                reply.AuthorLogin, reply.ProjectId, reply.MergeRequestIid);
            return;
        }

        // Antwort → Finding: die Comment-Id der Antwort == PlatformCommentId des Findings, projekt-gescoped
        // (Ids kollidieren sonst über Projekte). Kante aus PR 2a: zwei Findings auf derselben Datei+Zeile
        // teilten sich EINE GitHub-Comment-Id — dann deterministisch das erste (kleinste Id) nehmen.
        var findings = await db.ReviewFindings
            .Include(f => f.Review)
            .Where(f => f.PlatformCommentId == reply.ReplyToCommentId
                        && f.Review.Project.PlatformProjectId == reply.ProjectId)
            .OrderBy(f => f.Id)
            .ToListAsync(ct);
        if (findings.Count == 0)
        {
            logger.LogInformation("FP-Kommando auf {Project}!{Iid} ohne zugeordnetes Finding (Comment-Id {Id}) — ignoriert.",
                reply.ProjectId, reply.MergeRequestIid, reply.ReplyToCommentId);
            return;
        }
        if (findings.Count > 1)
            logger.LogWarning("Comment-Id {Id} auf {Project} ist mehrdeutig ({Count} Findings) — erstes gewählt.",
                reply.ReplyToCommentId, reply.ProjectId, findings.Count);

        var finding = findings[0];
        await MemoryEntryWriter.MarkFalsePositiveAsync(db, finding, reply.Reason, reply.AuthorLogin, ct);
        logger.LogInformation("Finding {FindingId} auf {Project}!{Iid} von {Author} als False Positive gemerkt.",
            finding.Id, reply.ProjectId, reply.MergeRequestIid, reply.AuthorLogin);

        try
        {
            await responder.PostReplyAsync(reply, ConfirmationText, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Bestätigung ist best-effort — der Eintrag steht bereits; ein fehlgeschlagener Reply
            // darf den Webhook (200) nicht kippen.
            logger.LogWarning(ex, "Bestätigungs-Antwort auf {Project}!{Iid} fehlgeschlagen.",
                reply.ProjectId, reply.MergeRequestIid);
        }
    }
}

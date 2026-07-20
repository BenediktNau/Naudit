// src/Naudit.Infrastructure/Memory/ReviewCommentCommandService.cs
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Naudit.Core.Review;
using Naudit.Infrastructure.Analytics;
using Naudit.Infrastructure.Data;
using Naudit.Infrastructure.Git;

namespace Naudit.Infrastructure.Memory;

/// <summary>Verarbeitet ein "@naudit fp/ok"-Antwort-Kommando: Autor autorisieren → Antwort dem Finding
/// zuordnen (über die in PR 2a erfasste PlatformCommentId, projekt-gescoped) → Kommando anwenden
/// (fp: Gedächtnis-Eintrag + Resolution "Rejected"; ok: Resolution "Accepted") → im Thread bestätigen.
/// Alles fail-closed/best-effort — der Webhook antwortet immer 200.</summary>
public sealed class ReviewCommentCommandService(
    NauditDbContext db, IReviewCommentResponder responder, ILogger<ReviewCommentCommandService> logger,
    ReviewOptions options)
{
    public const string ConfirmationText = "Als False Positive gemerkt.";
    public const string AcceptConfirmationText = "Als angenommen vermerkt.";

    // Quelle für ResolutionWriter.ApplyAsync: explizite Autor-Kommandos (siehe Präzedenz-Regel dort).
    private const string ResolutionSource = ResolutionValues.Sources.Command;

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

        if (reply.Command == ReviewCommandKind.Accept)
        {
            await HandleAcceptAsync(reply, finding, ct);
            return;
        }

        await HandleFalsePositiveAsync(reply, finding, ct);
    }

    private async Task HandleFalsePositiveAsync(ReviewCommentReply reply, ReviewFindingEntity finding, CancellationToken ct)
    {
        // Reihenfolge ist bewusst: die Resolution-Schreibung MUSS vor MarkFalsePositiveAsync laufen.
        // Im Doppel-POST-Race dort ruft der DbUpdateException-Catch-Zweig db.ChangeTracker.Clear() auf,
        // was "finding" detached — liefe die Resolution-Schreibung danach, würde sie ein bereits
        // detachtes Finding mutieren: SaveChangesAsync liefe durch (kein Fehler), aber persistiert
        // nichts (stiller Datenverlust). Resolution-Tracking läuft unabhängig vom Gedächtnis-Eintrag
        // (eigener Schalter, Review-Analytics PR 3).
        if (options.Resolution.Enabled)
            await ResolutionWriter.ApplyAsync(db, finding, ResolutionValues.Rejected, ResolutionSource, reply.AuthorLogin, ct);

        var result = await MemoryEntryWriter.MarkFalsePositiveAsync(db, finding, reply.Reason, reply.AuthorLogin, ct);

        // Redelivery-Schutz: nur bei einem ECHTEN Zustandswechsel (neu angelegt / reaktiviert) bestätigen —
        // sonst würde ein erneut zugestelltes Webhook-Event (oder eine zweite Antwort auf ein bereits
        // gemerktes Finding) eine weitere Bestätigungs-Antwort im Thread posten.
        if (!result.NewlyMarked)
        {
            logger.LogInformation("Finding {FindingId} war bereits als FP markiert — keine erneute Bestätigung.",
                finding.Id);
            return;
        }

        logger.LogInformation("Finding {FindingId} auf {Project}!{Iid} von {Author} als False Positive gemerkt.",
            finding.Id, reply.ProjectId, reply.MergeRequestIid, reply.AuthorLogin);

        await ConfirmAsync(reply, ConfirmationText, ct);
    }

    private async Task HandleAcceptAsync(ReviewCommentReply reply, ReviewFindingEntity finding, CancellationToken ct)
    {
        if (!options.Resolution.Enabled)
        {
            logger.LogInformation("Ok-Kommando auf {Project}!{Iid} ignoriert — Resolution-Tracking deaktiviert.",
                reply.ProjectId, reply.MergeRequestIid);
            return;
        }

        var changed = await ResolutionWriter.ApplyAsync(db, finding, ResolutionValues.Accepted, ResolutionSource, reply.AuthorLogin, ct);

        // Redelivery-Schutz analog zum FP-Zweig: nur bei echtem Zustandswechsel bestätigen.
        if (!changed)
        {
            logger.LogInformation("Finding {FindingId} war bereits als Accepted markiert — keine erneute Bestätigung.",
                finding.Id);
            return;
        }

        logger.LogInformation("Finding {FindingId} auf {Project}!{Iid} von {Author} als angenommen vermerkt.",
            finding.Id, reply.ProjectId, reply.MergeRequestIid, reply.AuthorLogin);

        await ConfirmAsync(reply, AcceptConfirmationText, ct);
    }

    // Bestätigungs-Antwort im Thread posten — best-effort, von beiden Kommando-Zweigen geteilt.
    private async Task ConfirmAsync(ReviewCommentReply reply, string text, CancellationToken ct)
    {
        try
        {
            await responder.PostReplyAsync(reply, text, ct);
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

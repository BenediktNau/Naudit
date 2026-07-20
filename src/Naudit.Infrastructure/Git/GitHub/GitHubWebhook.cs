using System.Security.Cryptography;
using System.Text;
using Naudit.Core.Models;
using Naudit.Infrastructure.Git;

namespace Naudit.Infrastructure.Git.GitHub;

public static class GitHubWebhook
{
    // Reviewbare PR-Actions: neu, wieder geöffnet, neue Commits gepusht.
    private static readonly string[] ReviewableActions = ["opened", "reopened", "synchronize"];

    /// <summary>Mappt ein GitHub-pull_request-Event auf einen ReviewRequest, oder null wenn nichts zu reviewen ist (auch bei null/anderem eventType).</summary>
    public static ReviewRequest? ToReviewRequest(string? eventType, GitHubWebhookPayload payload)
    {
        if (eventType != "pull_request")
            return null;

        if (payload.PullRequest is null || payload.Repository?.FullName is null)
            return null;

        if (payload.Action is null || !ReviewableActions.Contains(payload.Action))
            return null;

        return new ReviewRequest(payload.Repository.FullName, payload.PullRequest.Number,
            payload.PullRequest.Title ?? "", payload.PullRequest.User?.Login);
    }

    /// <summary>Prüft die GitHub-Webhook-Signatur (HMAC-SHA256 über den rohen Body) konstant-zeitlich. Leeres Secret ⇒ false (fail-closed).</summary>
    public static bool IsValidSignature(byte[] body, string secret, string? signatureHeader)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(signatureHeader))
            return false;

        const string prefix = "sha256=";
        if (!signatureHeader.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        byte[] expected;
        try { expected = Convert.FromHexString(signatureHeader[prefix.Length..]); }
        catch (FormatException) { return false; }

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var computed = hmac.ComputeHash(body);

        // Längen-Check zuerst: FixedTimeEquals wirft bei ungleicher Länge; computed ist immer 32 Byte und nicht geheim.
        return expected.Length == computed.Length && CryptographicOperations.FixedTimeEquals(computed, expected);
    }

    /// <summary>Mappt ein pull_request_review_comment-Event auf ein Kommando-Reply, oder null wenn es
    /// keine Antwort auf einen bestehenden Kommentar mit gültigem Kommando-Body ist — erkannt werden
    /// sowohl "@naudit fp" als auch die Annahme-Verben "@naudit ok"/"angenommen"/"accepted".</summary>
    public static ReviewCommentReply? ToCommentReply(string? eventType, GitHubReviewCommentEvent payload)
    {
        if (eventType != "pull_request_review_comment")
            return null;
        if (payload.Action != "created")
            return null;
        // Nur Antworten (in_reply_to_id gesetzt) — sie zeigen auf den Wurzel-Kommentar, unter dem Naudits
        // Finding hängt. Top-Level-Kommentare ohne in_reply_to_id lassen sich keinem Finding zuordnen.
        if (payload.Comment?.InReplyToId is not long replyTo)
            return null;
        if (payload.Repository?.FullName is not string repo)
            return null;
        if (payload.PullRequest is not { } pr)
            return null;

        var cmd = FpReplyCommand.TryParse(payload.Comment.Body);
        if (cmd is null)
            return null;

        // Ohne Autor-Login ist die Antwort nicht zuordenbar (Autorisierung/Protokoll brauchen ihn) —
        // nicht mit einer leeren Identität weiterlaufen.
        if (string.IsNullOrEmpty(payload.Comment.User?.Login))
            return null;

        return new ReviewCommentReply(repo, pr.Number, replyTo.ToString(), cmd.Reason,
            payload.Comment.User.Login, payload.Comment.AuthorAssociation, AuthorId: null, Command: cmd.Kind);
    }
}

using System.Security.Cryptography;
using System.Text;
using Naudit.Core.Models;

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

        return new ReviewRequest(payload.Repository.FullName, payload.PullRequest.Number, payload.PullRequest.Title ?? "");
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

        return expected.Length == computed.Length && CryptographicOperations.FixedTimeEquals(computed, expected);
    }
}

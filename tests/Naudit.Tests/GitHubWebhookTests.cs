using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Naudit.Infrastructure.Git.GitHub;
using Xunit;

namespace Naudit.Tests;

public class GitHubWebhookTests
{
    private const string PullRequestEvent = """
    {
      "action": "opened",
      "repository": { "full_name": "octo/hello-world" },
      "pull_request": { "number": 42, "title": "Add feature X" }
    }
    """;

    [Fact]
    public void ToReviewRequest_mapsPullRequestEvent()
    {
        var payload = JsonSerializer.Deserialize<GitHubWebhookPayload>(PullRequestEvent)!;

        var request = GitHubWebhook.ToReviewRequest("pull_request", payload);

        Assert.NotNull(request);
        Assert.Equal("octo/hello-world", request!.ProjectId);
        Assert.Equal(42, request.MergeRequestIid);
        Assert.Equal("Add feature X", request.Title);
    }

    [Fact]
    public void ToReviewRequest_ignoresNonPullRequestEvents()
    {
        var payload = JsonSerializer.Deserialize<GitHubWebhookPayload>(PullRequestEvent)!;
        Assert.Null(GitHubWebhook.ToReviewRequest("push", payload));
    }

    [Fact]
    public void ToReviewRequest_ignoresNonReviewableActions()
    {
        var json = """{ "action": "closed", "repository": { "full_name": "o/r" }, "pull_request": { "number": 1, "title": "x" } }""";
        var payload = JsonSerializer.Deserialize<GitHubWebhookPayload>(json)!;
        Assert.Null(GitHubWebhook.ToReviewRequest("pull_request", payload));
    }

    [Fact]
    public void ToReviewRequest_returnsNull_whenRepositoryMissing()
    {
        var json = """{ "action": "opened", "pull_request": { "number": 1, "title": "x" } }""";
        var payload = JsonSerializer.Deserialize<GitHubWebhookPayload>(json)!;
        Assert.Null(GitHubWebhook.ToReviewRequest("pull_request", payload));
    }

    [Fact]
    public void ToReviewRequest_returnsNull_whenPullRequestMissing()
    {
        var json = """{ "action": "opened", "repository": { "full_name": "o/r" } }""";
        var payload = JsonSerializer.Deserialize<GitHubWebhookPayload>(json)!;
        Assert.Null(GitHubWebhook.ToReviewRequest("pull_request", payload));
    }

    private static string Sign(string secret, byte[] body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return "sha256=" + Convert.ToHexStringLower(hmac.ComputeHash(body)); // Kleinbuchstaben wie GitHub im X-Hub-Signature-256-Header liefert.
    }

    [Fact]
    public void IsValidSignature_acceptsCorrectSignature()
    {
        var body = Encoding.UTF8.GetBytes("""{"hello":"world"}""");
        var header = Sign("topsecret", body);

        Assert.True(GitHubWebhook.IsValidSignature(body, "topsecret", header));
    }

    [Fact]
    public void IsValidSignature_rejectsWrongSecret()
    {
        var body = Encoding.UTF8.GetBytes("""{"hello":"world"}""");
        var header = Sign("topsecret", body);

        Assert.False(GitHubWebhook.IsValidSignature(body, "other-secret", header));
    }

    [Fact]
    public void IsValidSignature_rejectsNullHeader()
    {
        var body = Encoding.UTF8.GetBytes("x");
        Assert.False(GitHubWebhook.IsValidSignature(body, "topsecret", null));
    }

    [Fact]
    public void IsValidSignature_rejectsMissingPrefix()
    {
        var body = Encoding.UTF8.GetBytes("x");
        Assert.False(GitHubWebhook.IsValidSignature(body, "topsecret", "not-a-signature"));
    }

    [Fact]
    public void IsValidSignature_rejectsMalformedHex()
    {
        var body = Encoding.UTF8.GetBytes("x");
        Assert.False(GitHubWebhook.IsValidSignature(body, "topsecret", "sha256=zzzz"));
    }

    [Fact]
    public void IsValidSignature_rejectsTamperedBody()
    {
        var original = Encoding.UTF8.GetBytes("""{"ref":"main"}""");
        var tampered = Encoding.UTF8.GetBytes("""{"ref":"evil"}""");
        var header = Sign("topsecret", original);

        Assert.False(GitHubWebhook.IsValidSignature(tampered, "topsecret", header));
    }

    [Fact]
    public void IsValidSignature_rejectsEmptySecret_failClosed()
    {
        var body = Encoding.UTF8.GetBytes("x");
        Assert.False(GitHubWebhook.IsValidSignature(body, "", Sign("", body)));
    }
}

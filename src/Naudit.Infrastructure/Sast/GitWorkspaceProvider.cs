using Microsoft.Extensions.Logging;
using Naudit.Core.Abstractions;
using Naudit.Core.Models;
using Naudit.Infrastructure.Process;

namespace Naudit.Infrastructure.Sast;

/// <summary>Klont den MR/PR-Head flach in ein Temp-Verzeichnis (git via IProcessRunner).
/// init → fetch --depth 1 origin &lt;ref&gt; → checkout FETCH_HEAD. Dispose löscht das Verzeichnis.
/// Härtung: das Token steht weder in argv noch in .git/config (nur als http.extraHeader in der
/// Env des Fetch-Prozesses), git erbt nur eine Minimal-Env, und Symlinks aus dem PR werden als
/// reine Textdateien ausgecheckt (core.symlinks=false) — sie könnten sonst aus dem Checkout zeigen.</summary>
public sealed class GitWorkspaceProvider(
    IGitPlatform gitPlatform, IProcessRunner runner, ILogger<GitWorkspaceProvider> logger) : IWorkspaceProvider
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(5);

    // git-spezifisch nur die CA-Datei für selbst gehostete GitLabs mit eigener PKI.
    private static readonly IReadOnlyList<string> GitEnvironment = ProcessEnvironment.MinimalPlus("GIT_SSL_CAINFO");

    public async Task<IReviewWorkspace> CheckoutAsync(ReviewRequest request, CancellationToken ct = default)
    {
        var info = await gitPlatform.GetCheckoutAsync(request, ct);
        var dir = Path.Combine(Path.GetTempPath(), "naudit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            await GitAsync(dir, info, null, ct, "init", "-q");
            await GitAsync(dir, info, null, ct, "remote", "add", "origin", info.CloneUrl);
            await GitAsync(dir, info, AuthEnvironment(info.AuthorizationHeader), ct,
                "fetch", "--depth", "1", "origin", info.HeadRef);
            await GitAsync(dir, info, null, ct, "-c", "core.symlinks=false", "checkout", "-q", "FETCH_HEAD");
            return new GitWorkspace(dir, request.ProjectId);
        }
        catch
        {
            TryDelete(dir);
            throw;
        }
    }

    // Auth über GIT_CONFIG_COUNT/KEY/VALUE: wirkt wie `-c http.extraHeader=…`, steht aber nicht in
    // argv (für jeden Prozess per ps lesbar) und wird nicht in .git/config persistiert.
    private static Dictionary<string, string?> AuthEnvironment(string? authorizationHeader)
    {
        var env = new Dictionary<string, string?> { ["GIT_TERMINAL_PROMPT"] = "0" };
        if (!string.IsNullOrEmpty(authorizationHeader))
        {
            env["GIT_CONFIG_COUNT"] = "1";
            env["GIT_CONFIG_KEY_0"] = "http.extraHeader";
            env["GIT_CONFIG_VALUE_0"] = "Authorization: " + authorizationHeader;
        }
        return env;
    }

    private async Task GitAsync(string dir, RepoCheckoutInfo info, Dictionary<string, string?>? env,
        CancellationToken ct, params string[] args)
    {
        var spec = new ProcessSpec("git", args, StdIn: null,
            Environment: env ?? new Dictionary<string, string?> { ["GIT_TERMINAL_PROMPT"] = "0" },
            WorkingDirectory: dir, Timeout: GitTimeout, InheritedEnvironment: GitEnvironment);
        var result = await runner.RunAsync(spec, ct);
        if (result.ExitCode != 0)
        {
            var op = args.First(a => !a.StartsWith('-') && !a.Contains('='));
            // Verteidigung in der Tiefe: sollte git den Header je in stderr spiegeln, nie ins Log.
            logger.LogWarning("git {Op} schlug fehl (Exit {Code}): {Err}", op, result.ExitCode,
                Scrub(result.StdErr, info.AuthorizationHeader));
            throw new InvalidOperationException($"git {op} schlug fehl (Exit {result.ExitCode}).");
        }
    }

    private static string Scrub(string text, string? authorizationHeader)
    {
        if (string.IsNullOrEmpty(authorizationHeader))
            return text;
        // Ganzen Header-Wert und das reine Credential (ohne Schema-Präfix) maskieren.
        var credential = authorizationHeader.Split(' ', 2) is [_, var c] && c.Length > 0 ? c : authorizationHeader;
        return text.Replace(authorizationHeader, "***", StringComparison.Ordinal)
                   .Replace(credential, "***", StringComparison.Ordinal);
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best effort */ }
    }

    private sealed class GitWorkspace(string root, string projectId) : IReviewWorkspace
    {
        public string RootPath { get; } = root;
        public string ProjectId { get; } = projectId;

        public ValueTask DisposeAsync()
        {
            TryDelete(RootPath);
            return ValueTask.CompletedTask;
        }
    }
}

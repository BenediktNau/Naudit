using Microsoft.Extensions.Logging;
using Naudit.Core.Abstractions;
using Naudit.Core.Models;
using Naudit.Infrastructure.Process;

namespace Naudit.Infrastructure.Sast;

/// <summary>Klont den MR/PR-Head flach in ein Temp-Verzeichnis (git via IProcessRunner).
/// init → fetch --depth 1 origin &lt;ref&gt; → checkout FETCH_HEAD. Dispose löscht das Verzeichnis.</summary>
public sealed class GitWorkspaceProvider(
    IGitPlatform gitPlatform, IProcessRunner runner, ILogger<GitWorkspaceProvider> logger) : IWorkspaceProvider
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(5);

    public async Task<IReviewWorkspace> CheckoutAsync(ReviewRequest request, CancellationToken ct = default)
    {
        var info = await gitPlatform.GetCheckoutAsync(request, ct);
        var dir = Path.Combine(Path.GetTempPath(), "naudit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            await GitAsync(dir, ct, "init", "-q");
            await GitAsync(dir, ct, "remote", "add", "origin", info.CloneUrl);
            await GitAsync(dir, ct, "fetch", "--depth", "1", "origin", info.HeadRef);
            await GitAsync(dir, ct, "checkout", "-q", "FETCH_HEAD");
            return new GitWorkspace(dir, request.ProjectId);
        }
        catch
        {
            TryDelete(dir);
            throw;
        }
    }

    private async Task GitAsync(string dir, CancellationToken ct, params string[] args)
    {
        var spec = new ProcessSpec("git", args, StdIn: null, Environment: null, WorkingDirectory: dir, Timeout: GitTimeout);
        var result = await runner.RunAsync(spec, ct);
        if (result.ExitCode != 0)
        {
            // CloneUrl enthält das Token — nur die Argumente ohne URL loggen.
            logger.LogWarning("git {Op} schlug fehl (Exit {Code}): {Err}", args[0], result.ExitCode, result.StdErr);
            throw new InvalidOperationException($"git {args[0]} schlug fehl (Exit {result.ExitCode}).");
        }
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

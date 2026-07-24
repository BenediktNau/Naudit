using Naudit.Core.Abstractions;

namespace Naudit.Infrastructure.Dast;

/// <summary>Baut die App des PRs aus deren eigenem Dockerfile, startet sie isoliert und liefert eine
/// erreichbare URL — oder null, wenn das für diesen PR nicht geht (nicht freigeschaltet, kein
/// Dockerfile, Build kaputt, kommt nicht hoch, Docker weg). Wirft nie wegen der Sache selbst.</summary>
public interface IAppRunner
{
    Task<RunningApp?> RunAsync(IReviewWorkspace workspace, CancellationToken ct = default);
}

/// <summary>Handle auf die laufende Test-App. DisposeAsync räumt App- und Probe-Container, Netz und
/// Image ab — idempotent, damit ein doppeltes Dispose (finally + using) nicht doppelt abräumt.</summary>
public sealed class RunningApp(string internalUrl, string networkName, string containerName,
    string probeContainerName, Func<ValueTask> teardown) : IAsyncDisposable
{
    /// <summary>URL im Review-Netz (Container-Name als Host) — nur vom Probe-/Playwright-Container
    /// erreichbar; Naudit, Host und Internet sehen sie nie.</summary>
    public string InternalUrl { get; } = internalUrl;

    public string NetworkName { get; } = networkName;
    public string ContainerName { get; } = containerName;

    /// <summary>Probe-Container im selben Netz — PR 1 nutzt ihn für den Healthcheck (exec),
    /// PR 2 spricht über ihn MCP (stdio via exec).</summary>
    public string ProbeContainerName { get; } = probeContainerName;

    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            await teardown();
    }
}

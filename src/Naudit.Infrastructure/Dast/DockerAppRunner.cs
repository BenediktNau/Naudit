using Microsoft.Extensions.Logging;
using Naudit.Core.Abstractions;
using Naudit.Infrastructure.Docker;

namespace Naudit.Infrastructure.Dast;

/// <summary>Führt fremden PR-Code aus — deshalb: eigenes internes Netz je Review (kein Egress,
/// keine veröffentlichten Ports, nirgends), Ressourcen- und Rechte-Grenzen an beiden Containern,
/// kein Volume, keine Naudit-Secrets, hartes Zeitbudget und garantierter Abbau. Naudit betritt das
/// Netz nie selbst: der Healthcheck läuft als docker exec im Probe-Container (dieselbe Primitive
/// wie die CLI-Läufe der Session-Sandbox) — dadurch verhält sich der Runner identisch, ob Naudit
/// im Container oder als nackter Prozess läuft. Jeder Fehlerpfad endet in Teardown + null
/// (fail-open).</summary>
public sealed class DockerAppRunner(
    IDockerClient docker,
    DastOptions options,
    ILogger<DockerAppRunner> logger,
    TimeProvider? time = null) : IAppRunner
{
    public const string NamePrefix = "naudit-dast-";

    public async Task<RunningApp?> RunAsync(IReviewWorkspace workspace, CancellationToken ct = default)
    {
        if (!options.AppliesTo(workspace.ProjectId))
            return null;

        if (!File.Exists(Path.Combine(workspace.RootPath, options.DockerfilePath)))
        {
            logger.LogInformation("DAST: kein {Dockerfile} im Checkout — übersprungen.", options.DockerfilePath);
            return null;
        }

        var key = Guid.NewGuid().ToString("N")[..8];
        var image = $"{NamePrefix}img-{key}";
        var network = $"{NamePrefix}net-{key}";
        var container = $"{NamePrefix}app-{key}";
        var probe = $"{NamePrefix}pw-{key}";

        // Ein Budget über Build + Start + Healthcheck; Teardown läuft danach OHNE Token weiter.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(options.TimeBudget);

        async ValueTask TearDownAsync()
        {
            await SafeAsync(() => docker.RemoveContainerAsync(probe, CancellationToken.None));
            await SafeAsync(() => docker.RemoveContainerAsync(container, CancellationToken.None));
            await SafeAsync(() => docker.RemoveNetworkAsync(network, CancellationToken.None));
            await SafeAsync(() => docker.RemoveImageAsync(image, CancellationToken.None));
            // Das ProbeImage bleibt absichtlich stehen — Cache über Reviews hinweg.
        }

        try
        {
            await using (var context = await WorkspaceTarPacker.PackAsync(workspace.RootPath, options.MaxContextMb, budget.Token))
            {
                if (context is null)
                {
                    logger.LogWarning("DAST: Build-Kontext größer als {Max} MB — übersprungen.", options.MaxContextMb);
                    return null;
                }
                var build = await docker.BuildImageAsync(image, context, options.DockerfilePath, budget.Token);
                if (!build.Success)
                {
                    logger.LogInformation("DAST: Build fehlgeschlagen — übersprungen. {Log}", build.Log);
                    await TearDownAsync();
                    return null;
                }
            }

            await docker.CreateNetworkAsync(network, budget.Token);
            await docker.PullImageAsync(options.ProbeImage, budget.Token); // schneller No-op, wenn schon da

            var limits = new ContainerLimits(options.MemoryLimitMb, options.CpuLimit, options.PidsLimit);
            await docker.RunDetachedAsync(
                new ContainerRunSpec(container, image, VolumeName: null, VolumeTarget: null, Command: [])
                { Network = network, Limits = limits }, budget.Token);
            // Probe-Container: lebt passiv (sleep) im selben Netz und wird nur per exec benutzt —
            // PR 1 für den Healthcheck, PR 2 als Playwright-MCP-Server (stdio via exec).
            await docker.RunDetachedAsync(
                new ContainerRunSpec(probe, options.ProbeImage, VolumeName: null, VolumeTarget: null, Command: [])
                { Network = network, Limits = limits, Entrypoint = ["sleep", "infinity"] }, budget.Token);

            var url = $"http://{container}:{options.AppPort}{options.HealthPath}";
            if (!await WaitForHealthyAsync(probe, url, budget.Token))
            {
                logger.LogInformation("DAST: App wurde nicht erreichbar ({Url}) — übersprungen.", url);
                await TearDownAsync();
                return null;
            }

            logger.LogInformation("DAST: App läuft unter {Url}.", url);
            return new RunningApp(url, network, container, probe, TearDownAsync);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await TearDownAsync();
            throw; // echter Abbruch des Aufrufers wird durchgereicht
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "DAST: App-Runner abgebrochen — Review läuft ohne dynamische Prüfung weiter.");
            await TearDownAsync();
            return null;
        }
    }

    /// <summary>HTTP-Probe als exec-Kommando im Probe-Container: Exit 0 = Webserver antwortet.
    /// Auch eine 404 zählt (beweist einen laufenden Server); 5xx kann eine noch startende App
    /// sein — weiter pollen. Node steckt im Playwright-Image.</summary>
    internal static IReadOnlyList<string> HealthProbeArgv(string url) =>
    [
        "node", "-e",
        "fetch(process.argv[1]).then(r=>process.exit(r.status<500?0:1),()=>process.exit(1))",
        url,
    ];

    private async Task<bool> WaitForHealthyAsync(string probe, string url, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var result = await docker.ExecAsync(probe, HealthProbeArgv(url),
                environment: null, workingDirectory: "/", ct);
            if (result.ExitCode == 0)
                return true;

            try { await Task.Delay(options.HealthPollInterval, time ?? TimeProvider.System, ct); }
            catch (OperationCanceledException) { return false; }
        }
        return false;
    }

    private async Task SafeAsync(Func<Task> operation)
    {
        try { await operation(); }
        catch (Exception ex) { logger.LogWarning(ex, "DAST: Teilschritt des Abbaus fehlgeschlagen (best-effort)."); }
    }
}

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Naudit.Infrastructure.Docker;

namespace Naudit.Infrastructure.Dast;

/// <summary>Räumt beim Start liegengebliebene DAST-Ressourcen ab (naudit-dast-*): nach einem
/// Absturz mitten im Lauf läuft sonst fremder PR-Code weiter. Nur Präfix-Treffer — fremde
/// Container/Netze/Images bleiben unangetastet. Fail-quiet: der Host startet auch ohne Docker.</summary>
public sealed class DastOrphanSweeper(IDockerClient docker, ILogger<DastOrphanSweeper> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        // Jeder Listing-/Entfernen-Schritt einzeln fail-quiet (SafeAsync-Muster wie
        // DockerAppRunner.TearDownAsync): eine fehlgeschlagene Container-Entfernung darf das
        // Aufräumen von Netzen und Images nicht verhindern. OperationCanceledException bricht
        // trotzdem den ganzen Sweep ab — ein echter Shutdown soll nicht weiterlaufen.
        foreach (var container in await SafeListAsync(() => docker.ListContainersAsync(DockerAppRunner.NamePrefix, ct)))
        {
            logger.LogInformation("DAST: entferne verwaisten Container {Name}.", container.Name);
            await SafeAsync(() => docker.RemoveContainerAsync(container.Name, ct));
        }
        foreach (var network in await SafeListAsync(() => docker.ListNetworksAsync(DockerAppRunner.NamePrefix, ct)))
            await SafeAsync(() => docker.RemoveNetworkAsync(network, ct));
        foreach (var image in await SafeListAsync(() => docker.ListImagesAsync(DockerAppRunner.NamePrefix, ct)))
            await SafeAsync(() => docker.RemoveImageAsync(image, ct));
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    private async Task SafeAsync(Func<Task> operation)
    {
        try { await operation(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "DAST: Aufräum-Teilschritt fehlgeschlagen (best-effort).");
        }
    }

    private async Task<IReadOnlyList<T>> SafeListAsync<T>(Func<Task<IReadOnlyList<T>>> operation)
    {
        try { return await operation(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "DAST: Auflisten verwaister Ressourcen fehlgeschlagen.");
            return [];
        }
    }
}

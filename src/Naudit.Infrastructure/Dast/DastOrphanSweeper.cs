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
        try
        {
            foreach (var container in await docker.ListContainersAsync(DockerAppRunner.NamePrefix, ct))
            {
                logger.LogInformation("DAST: entferne verwaisten Container {Name}.", container.Name);
                await docker.RemoveContainerAsync(container.Name, ct);
            }
            foreach (var network in await docker.ListNetworksAsync(DockerAppRunner.NamePrefix, ct))
                await docker.RemoveNetworkAsync(network, ct);
            foreach (var image in await docker.ListImagesAsync(DockerAppRunner.NamePrefix, ct))
                await docker.RemoveImageAsync(image, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "DAST: Aufräumen verwaister Ressourcen fehlgeschlagen.");
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}

using Naudit.Infrastructure.Memory;

namespace Naudit.Web;

/// <summary>Verarbeitet FP-Antwort-Kommandos aus der Queue — je Eintrag ein eigener DI-Scope,
/// mit dem Hosted-Service-Token (nicht dem beendeten Request-Token). Fehler brechen nie den Service.</summary>
public sealed class FpCommandBackgroundService(
    IFpCommandQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<FpCommandBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var reply in queue.DequeueAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<ReviewCommentCommandService>();
                await svc.HandleAsync(reply, stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "FP-Kommando-Verarbeitung fehlgeschlagen ({Project}!{Iid}).",
                    reply.ProjectId, reply.MergeRequestIid);
            }
        }
    }
}

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Naudit.Infrastructure.Setup;

/// <summary>Führt die Kommentar-Event-Prüfung EINMAL nach dem Hochfahren aus. Bewusst
/// BackgroundService statt IHostedService.StartAsync: die Prüfung macht einen HTTP-Aufruf, und ein
/// hängender Aufruf darf den Hoststart nicht blockieren. Ist kein Probe registriert (GitHub im
/// PAT-Modus), passiert nichts.</summary>
public sealed class CommentEventCheckService(
    IServiceScopeFactory scopes, ILogger<CommentEventCheckService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var probe = scope.ServiceProvider.GetService<ICommentEventProbe>();
            if (probe is null) return;

            var status = await probe.CheckAsync(ct);
            if (status.State != CommentEventState.Missing) return;

            foreach (var detail in status.Details)
                logger.LogWarning("{Detail}", detail);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Eine Diagnose darf den Host nie kippen (Audit-Sink-Philosophie).
            logger.LogDebug(ex, "Kommentar-Event-Prüfung fehlgeschlagen.");
        }
    }
}

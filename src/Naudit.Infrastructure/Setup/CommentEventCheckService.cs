using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Naudit.Infrastructure.Setup;

/// <summary>Führt die Kommentar-Event-Prüfung EINMAL nach dem Hochfahren aus. Bewusst
/// BackgroundService statt IHostedService.StartAsync: die Prüfung macht einen HTTP-Aufruf, und ein
/// hängender Aufruf darf den Hoststart nicht blockieren. Ist kein Probe registriert (GitHub im
/// PAT-Modus), passiert nichts. Die Naht ist bewusst plural (GetServices, nicht GetService): ein
/// zweiter Probe (z. B. ein künftiger PAT-Repo-Hook-Check) würde bei GetService kommentarlos vom
/// zuletzt registrierten verdrängt — genau die stille Fehlerklasse, die dieses Feature aufdecken soll.</summary>
public sealed class CommentEventCheckService(
    IServiceScopeFactory scopes, ILogger<CommentEventCheckService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var probes = scope.ServiceProvider.GetServices<ICommentEventProbe>().ToList();
            if (probes.Count == 0) return;

            foreach (var probe in probes)
            {
                var status = await probe.CheckAsync(ct);

                // Genau eine Info-Zeile je Probe: was wurde tatsächlich festgestellt — auch bei
                // Ok/Unknown. Sonst sieht ein Betreiber ohne Debug-Log nie, dass überhaupt geprüft
                // wurde, und verwechselt "nicht ermittelbar" mit "geprüft, alles gut".
                if (!string.IsNullOrEmpty(status.Summary))
                    logger.LogInformation("Kommentar-Event-Prüfung: {Summary}", status.Summary);

                if (status.State != CommentEventState.Missing) continue;
                foreach (var detail in status.Details)
                    logger.LogWarning("{Detail}", detail);
            }
        }
        catch (Exception ex)
        {
            // Eine Diagnose darf den Host nie kippen (Audit-Sink-Philosophie). Unbedingt fangen,
            // aber beim Herunterfahren nur still schlucken statt zu loggen — sonst würde ein
            // Abbruch mitten im Aufruf (EF/SQLite, ObjectDisposedException) den Host als
            // "BackgroundService failed unexpectedly" (Critical) beenden.
            if (!ct.IsCancellationRequested)
                logger.LogDebug(ex, "Kommentar-Event-Prüfung fehlgeschlagen.");
        }
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Naudit.Infrastructure.Data;
using Naudit.Infrastructure.Docker;

namespace Naudit.Infrastructure.Ai.Sandbox;

/// <summary>Hintergrunddienst der Session-Sandbox (nur bei SessionSandbox=Docker registriert):
/// beim Start Ping (Fail-Open-Zustand setzen) + Adoption bestehender Container, danach alle
/// 5 Minuten Re-Ping (Selbstheilung nach Socket-Ausfall), Idle-Sweep und Reconciliation gegen
/// die Kontenliste. Durchweg fail-quiet — Sandbox-Probleme stören nie den Host.</summary>
public sealed class SandboxSweeperService(
    IDockerClient docker,
    SessionContainerManager manager,
    SessionSandboxState state,
    IServiceScopeFactory scopeFactory,
    ILogger<SandboxSweeperService> logger) : BackgroundService
{
    /// <summary>Bewusst kurz gegen das lange IdleTimeout: der Tick ist zugleich die Re-Ping-Sonde.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await AdoptAsync(stoppingToken);
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await TickAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Host-Stopp — regulär beenden.
        }
    }

    /// <summary>Startschritt: Ping + Adoption über das Namens-Präfix (public für direkte Tests).</summary>
    public async Task AdoptAsync(CancellationToken ct)
    {
        if (!await PingAndReportAsync(ct))
            return;
        try
        {
            await manager.AdoptExistingAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Session-Sandbox: Adoption bestehender Container fehlgeschlagen.");
        }
    }

    /// <summary>Ein Sweep-Tick: neu pingen, Idle-Container stoppen, dann gegen die Kontenliste
    /// abgleichen (public für direkte Tests).</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        if (!await PingAndReportAsync(ct))
            return;
        try
        {
            await manager.SweepIdleAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Session-Sandbox: Idle-Sweep fehlgeschlagen.");
        }
        try
        {
            await ReconcileAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Session-Sandbox: Reconciliation fehlgeschlagen.");
        }
    }

    /// <summary>Gleicht die vorhandenen Session-Container gegen die Konten ab und entfernt jeden,
    /// dessen Konto fehlt, nicht aktiv ist oder keinen Token mehr hinterlegt hat — samt Volume.
    /// Der Sofort-Abbau in ClaudeSessionService/AccountService ist best-effort (Docker-Fehler,
    /// laufender Exec, Absturz mitten im Löschen); erst dieser Abgleich macht daraus die Zusage,
    /// dass Credentials den Entzug der Berechtigung nicht überleben (public für direkte Tests).</summary>
    public async Task ReconcileAsync(CancellationToken ct)
    {
        var entries = await docker.ListContainersAsync(SessionContainerManager.NamePrefix, ct);
        if (entries.Count == 0)
            return;

        // Eigener Scope je Lauf: der Sweeper ist Singleton, der DbContext scoped.
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NauditDbContext>();
        foreach (var entry in entries)
        {
            if (!SessionContainerManager.TryParseAccountId(entry.Name, out var accountId))
                continue;
            var account = await db.Accounts.AsNoTracking()
                .SingleOrDefaultAsync(a => a.Id == accountId, ct);
            if (account is { Status: AccountStatus.Active, ClaudeSessionToken: not null })
                continue;
            logger.LogInformation(
                "Session-Sandbox: räume {Name} ab — Konto fehlt, ist nicht aktiv oder hat keinen Token mehr.",
                entry.Name);
            await manager.RemoveAsync(accountId, ct);
        }
    }

    private async Task<bool> PingAndReportAsync(CancellationToken ct)
    {
        var previous = state.SocketReachable;
        var ok = await docker.PingAsync(ct);
        state.ReportPing(ok);
        if (!ok && previous != false)
            logger.LogWarning("Session-Sandbox: docker.sock nicht erreichbar/nutzbar — " +
                "Abo-Sessions laufen in-process weiter (Fallback). Socket-Mount + group_add prüfen, " +
                "siehe docs/session-sandbox.md.");
        else if (ok && previous == false)
            logger.LogInformation("Session-Sandbox: docker.sock wieder erreichbar — Sandbox aktiv.");
        return ok;
    }
}

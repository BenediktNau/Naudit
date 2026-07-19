using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Naudit.Infrastructure.Docker;

namespace Naudit.Infrastructure.Ai.Sandbox;

/// <summary>Hintergrunddienst der Session-Sandbox (nur bei SessionSandbox=Docker registriert):
/// beim Start Ping (Fail-Open-Zustand setzen) + Adoption bestehender Container, danach alle
/// 5 Minuten Re-Ping (Selbstheilung nach Socket-Ausfall) + Idle-Sweep. Durchweg fail-quiet —
/// Sandbox-Probleme stören nie den Host.</summary>
public sealed class SandboxSweeperService(
    IDockerClient docker,
    SessionContainerManager manager,
    SessionSandboxState state,
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

    /// <summary>Ein Sweep-Tick: neu pingen, dann Idle-Container stoppen (public für direkte Tests).</summary>
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

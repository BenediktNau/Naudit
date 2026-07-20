using Microsoft.Extensions.Logging;
using Naudit.Infrastructure.Docker;
using Naudit.Infrastructure.Process;

namespace Naudit.Infrastructure.Ai.Sandbox;

/// <summary>IProcessRunner, der den claude-Lauf per docker exec in den langlebigen Container des
/// Accounts verlegt (warme Session: Prozess-Container + Auth im Volume bleiben zwischen Reviews
/// bestehen). Fail-open: jeder Docker-Plumbing-Fehler (DockerUnavailableException) fällt auf den
/// In-Process-Runner zurück — ein Review scheitert nie an der Sandbox. Timeout/Cancel spiegeln
/// den SystemProcessRunner-Kill-Pfad: Container-Stop (beendet den Exec) + TimeoutException.</summary>
public sealed class DockerSessionRunner(
    int accountId,
    SessionContainerManager manager,
    IDockerClient docker,
    IProcessRunner inProcessFallback,
    SessionSandboxState state,
    ILogger logger) : IProcessRunner
{
    // Fester Pfad, pro Lauf überschrieben: der Account-Lock serialisiert Execs, und /tmp liegt in
    // der Container-Schicht — nicht im persistenten Session-Volume (kein Prompt-Ansammeln dort).
    private const string StdInDirectory = "/tmp";
    private const string StdInFileName = "naudit-stdin";

    public async Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct = default)
    {
        // Letzter Ping negativ ⇒ Docker gar nicht erst versuchen (der Sweeper pingt periodisch neu).
        if (state.SocketReachable == false)
            return await inProcessFallback.RunAsync(spec, ct);

        try
        {
            return await RunInContainerAsync(spec, ct);
        }
        catch (DockerUnavailableException ex)
        {
            logger.LogWarning(ex,
                "Session-Sandbox für Konto {AccountId} nicht verfügbar — Fallback auf In-Process-Lauf.",
                accountId);
            // Der Container hat sich nicht als brauchbar erwiesen: LastUsed zurücknehmen, damit er
            // Sweep/LRU nicht länger blockiert (EnsureRunningAsync hatte ihn vorab als genutzt markiert).
            manager.Invalidate(accountId);
            // Der Fallback bekommt bewusst das VOLLE spec.Timeout erneut (Worst-Case-Wall-Clock ≈
            // Docker-Versuch + In-Process-Timeout) — kein geteiltes Budget, damit der Fallback nie
            // ausgehungert startet.
            return await inProcessFallback.RunAsync(spec, ct);
        }
    }

    private async Task<ProcessResult> RunInContainerAsync(ProcessSpec spec, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(spec.Timeout);
        var name = SessionContainerManager.ContainerName(accountId);
        try
        {
            await manager.EnsureRunningAsync(accountId, timeoutCts.Token);
            using var _ = await manager.AcquireLockAsync(accountId, timeoutCts.Token);

            DockerExecResult result;
            try
            {
                result = await ExecOnceAsync(name, spec, timeoutCts.Token);
            }
            catch (DockerUnavailableException)
            {
                // Container zwischenzeitlich extern gestoppt/entfernt? Einmal neu sicherstellen + Retry;
                // scheitert auch der, greift der Fail-Open-Fallback im Aufrufer.
                await manager.EnsureRunningAsync(accountId, timeoutCts.Token);
                result = await ExecOnceAsync(name, spec, timeoutCts.Token);
            }

            manager.Touch(accountId);
            return new ProcessResult(result.ExitCode, result.StdOut, result.StdErr);
        }
        catch (OperationCanceledException)
        {
            // Kill-Pfad wie SystemProcessRunner: der Container-Stop beendet den Exec mit; die warme
            // Session liegt im Volume und überlebt, EnsureRunning startet beim nächsten Lauf neu.
            try { await docker.StopAsync(name, CancellationToken.None); }
            catch (DockerUnavailableException) { /* best-effort */ }
            if (ct.IsCancellationRequested)
                throw;
            throw new TimeoutException(
                $"'{spec.FileName}' überschritt das Timeout von {spec.Timeout.TotalSeconds:0}s.");
        }
    }

    private async Task<DockerExecResult> ExecOnceAsync(string name, ProcessSpec spec, CancellationToken ct)
    {
        if (spec.StdIn is not null)
            await docker.WriteFileAsync(name, StdInDirectory, StdInFileName, spec.StdIn, ct);

        // Env-Filter: NUR der Session-Token wandert in den Container. CLAUDE_CONFIG_DIR wird bewusst
        // verworfen — das Container-HOME (= persistentes Volume) gewinnt, damit die Session warm bleibt.
        Dictionary<string, string?>? env = null;
        if (spec.Environment is not null
            && spec.Environment.TryGetValue("CLAUDE_CODE_OAUTH_TOKEN", out var token))
            env = new Dictionary<string, string?> { ["CLAUDE_CODE_OAUTH_TOKEN"] = token };

        // Neutrales CWD im Container (kein ambient CLAUDE.md) — Host-WorkingDirectory ist dort bedeutungslos.
        return await docker.ExecAsync(name, BuildArgv(spec), env, workingDirectory: "/tmp", ct);
    }

    // Ohne stdin: argv direkt. Mit stdin: über die Shell umleiten — "$0"/"$@" trägt die
    // Original-Argv unverändert durch (kein Quoting-Problem). Danach wird der Zwischenspeicher
    // gelöscht (er trägt den Diff, der Container lebt tagelang weiter); kein `exec`, damit nach
    // dem Lauf noch aufgeräumt und der Exit-Code der CLI durchgereicht werden kann.
    private static IReadOnlyList<string> BuildArgv(ProcessSpec spec)
    {
        if (spec.StdIn is null)
            return [spec.FileName, .. spec.Arguments];
        var path = $"{StdInDirectory}/{StdInFileName}";
        return ["/bin/sh", "-c", $"\"$0\" \"$@\" < {path}; rc=$?; rm -f {path}; exit $rc",
            spec.FileName, .. spec.Arguments];
    }
}

/// <summary>Factory für SessionSandbox=Docker: pro Account ein an dessen Container gebundener Runner.</summary>
public sealed class DockerSessionRunnerFactory(
    SessionContainerManager manager,
    IDockerClient docker,
    IProcessRunner inProcess,
    SessionSandboxState state,
    ILoggerFactory loggerFactory) : ISessionRunnerFactory
{
    public IProcessRunner ForAccount(int accountId)
        => new DockerSessionRunner(accountId, manager, docker, inProcess, state,
            loggerFactory.CreateLogger<DockerSessionRunner>());
}

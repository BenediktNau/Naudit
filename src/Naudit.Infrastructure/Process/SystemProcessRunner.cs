using System.ComponentModel;
using System.Diagnostics;
using OsProcess = System.Diagnostics.Process;

namespace Naudit.Infrastructure.Process;

/// <summary>Führt einen Subprozess aus: schreibt stdin, liest stdout/stderr, killt bei Timeout/Cancel.</summary>
public sealed class SystemProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = spec.FileName,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            // ArgumentList: kein Shell-Quoting, leeres Argument (--tools "") bleibt erhalten.
            WorkingDirectory = spec.WorkingDirectory ?? Environment.CurrentDirectory,
        };
        foreach (var arg in spec.Arguments)
            psi.ArgumentList.Add(arg);
        if (spec.Environment is not null)
            foreach (var kv in spec.Environment)
                psi.Environment[kv.Key] = kv.Value; // psi.Environment ist bereits mit der Eltern-Env vorbefüllt

        using var process = new OsProcess { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException)
        {
            throw new InvalidOperationException(
                $"Konnte '{spec.FileName}' nicht starten — installiert und auf PATH? ({ex.Message})", ex);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(spec.Timeout);
        try
        {
            // stdin vollständig schreiben + schließen (claude `-p` liest bis EOF, bevor es ausgibt → kein Deadlock).
            // Innerhalb des try, damit ein Cancel/Timeout *während* des Writes den Kill-Pfad erreicht und kein
            // verwaister Kindprozess zurückbleibt (Dispose killt das Kind nicht). Der Write zählt so mit ins Timeout.
            if (spec.StdIn is not null)
                await process.StandardInput.WriteAsync(spec.StdIn.AsMemory(), timeoutCts.Token);
            process.StandardInput.Close();

            // Reads ohne Token: sie laufen bis Prozess-Exit/Pipe-Schluss; bei Timeout/Cancel killt der catch den Prozess,
            // wodurch die Pipes schließen und die Tasks beenden.
            var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

            await process.WaitForExitAsync(timeoutCts.Token);

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return new ProcessResult(process.ExitCode, stdout, stderr);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* schon beendet */ }
            if (ct.IsCancellationRequested)
                throw; // externer Cancel: durchreichen
            throw new TimeoutException(
                $"'{spec.FileName}' überschritt das Timeout von {spec.Timeout.TotalSeconds:0}s.");
        }
    }
}

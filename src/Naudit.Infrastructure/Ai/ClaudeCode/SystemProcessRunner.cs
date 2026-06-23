using System.ComponentModel;
using System.Diagnostics;

namespace Naudit.Infrastructure.Ai.ClaudeCode;

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

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException)
        {
            throw new InvalidOperationException(
                $"Konnte '{spec.FileName}' nicht starten — installiert und auf PATH? ({ex.Message})", ex);
        }

        // stdin vollständig schreiben + schließen (claude `-p` liest bis EOF, bevor es ausgibt → kein Deadlock).
        if (spec.StdIn is not null)
            await process.StandardInput.WriteAsync(spec.StdIn.AsMemory(), ct);
        process.StandardInput.Close();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(spec.Timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* schon beendet */ }
            if (ct.IsCancellationRequested)
                throw; // externer Cancel: durchreichen
            throw new TimeoutException(
                $"'{spec.FileName}' überschritt das Timeout von {spec.Timeout.TotalSeconds:0}s.");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return new ProcessResult(process.ExitCode, stdout, stderr);
    }
}

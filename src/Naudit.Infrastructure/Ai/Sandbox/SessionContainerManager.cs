using Microsoft.Extensions.Logging;
using Naudit.Infrastructure.Docker;

namespace Naudit.Infrastructure.Ai.Sandbox;

/// <summary>Lifecycle der Account-Session-Container: ein langlebiger Container + benanntes Volume
/// pro Account ("naudit-session-&lt;accountId&gt;"), Reviews laufen per docker exec hinein.
/// Prozessweiter Singleton: hält Locks (nie zwei Execs im selben Container — Race auf den
/// CLI-Credential-Cache im Volume) und LastUsed (Idle-Sweep, LRU-Cap) in-memory; nach einem
/// Naudit-Neustart adoptiert er bestehende Container über das Namens-Präfix.</summary>
public sealed class SessionContainerManager(
    IDockerClient docker,
    SessionSandboxOptions options,
    ILogger<SessionContainerManager> logger,
    TimeProvider? time = null)
{
    public const string NamePrefix = "naudit-session-";

    /// <summary>Mount-Ziel des Account-Volumes = HOME des Images — dort hält die claude-CLI ihre
    /// Credentials, das Volume macht die Session über Container-Stop UND -Remove hinweg warm.</summary>
    public const string VolumeTarget = "/home/app";

    private readonly object _gate = new();
    private readonly Dictionary<int, SemaphoreSlim> _locks = [];
    private readonly Dictionary<int, DateTimeOffset> _lastUsed = [];
    private string? _image;

    // Serialisiert Cap-Prüfung + Create über alle Accounts (check-then-act ist sonst racy: zwei
    // gleichzeitige EnsureRunningAsync für verschiedene Accounts könnten beide den Cap passieren,
    // bevor einer der beiden seinen Container angelegt hat). Kurz gehalten — der eigentliche Exec
    // läuft NICHT unter diesem Gate, nur Inspect/Cap/Create/Start.
    private readonly SemaphoreSlim _createGate = new(1, 1);

    public static string ContainerName(int accountId) => $"{NamePrefix}{accountId}";

    /// <summary>Container existiert + läuft; legt fehlende an (Image = Override oder
    /// Selbst-Inspektion), startet gestoppte, erzwingt vorher das LRU-Cap. Liefert den Namen.</summary>
    public async Task<string> EnsureRunningAsync(int accountId, CancellationToken ct = default)
    {
        await _createGate.WaitAsync(ct);
        try
        {
            var name = ContainerName(accountId);
            var info = await docker.InspectContainerAsync(name, ct);
            if (info is null)
            {
                await EnforceCapAsync(accountId, ct);
                var image = await ResolveImageAsync(ct);
                logger.LogInformation("Session-Sandbox: lege Container {Name} an (Image {Image}).", name, image);
                await docker.RunDetachedAsync(
                    new ContainerRunSpec(name, image, VolumeName: name, VolumeTarget, ["sleep", "infinity"]), ct);
            }
            else if (!info.Running)
            {
                await EnforceCapAsync(accountId, ct);
                await docker.StartAsync(name, ct);
            }
            Touch(accountId); // deckt auch die Laufzeit des folgenden Execs ab (Sweep-Schutz)
            return name;
        }
        finally
        {
            _createGate.Release();
        }
    }

    /// <summary>Exklusiv-Lock pro Account — nie zwei Execs gleichzeitig im selben Container.</summary>
    public async Task<IDisposable> AcquireLockAsync(int accountId, CancellationToken ct = default)
    {
        SemaphoreSlim sem;
        lock (_gate)
        {
            if (!_locks.TryGetValue(accountId, out sem!))
                _locks[accountId] = sem = new SemaphoreSlim(1, 1);
        }
        await sem.WaitAsync(ct);
        return new Releaser(sem);
    }

    /// <summary>Begrenzter Lock-Versuch: null = Account hat innerhalb des Zeitfensters einen Exec in
    /// Flight (Sweep/LRU/Remove dürfen dessen Container dann nicht stoppen — docker stop killt den
    /// Exec). Default TimeSpan.Zero = nicht-blockierend.</summary>
    private async Task<IDisposable?> TryAcquireLockAsync(int accountId, TimeSpan? wait = null,
        CancellationToken ct = default)
    {
        SemaphoreSlim sem;
        lock (_gate)
        {
            if (!_locks.TryGetValue(accountId, out sem!))
                _locks[accountId] = sem = new SemaphoreSlim(1, 1);
        }
        return await sem.WaitAsync(wait ?? TimeSpan.Zero, ct) ? new Releaser(sem) : null;
    }

    public void Touch(int accountId)
    {
        lock (_gate) _lastUsed[accountId] = Now();
    }

    /// <summary>Gegenstück zu Touch für den Fail-Open-Pfad: der Lauf ist in-process ausgewichen, der
    /// Container also nicht nachweislich brauchbar. Ohne LastUsed-Eintrag zählt er als ältester und
    /// wird von Sweep/LRU sofort wieder eingesammelt, statt sich bis IdleTimeout selbst zu schützen.</summary>
    public void Invalidate(int accountId)
    {
        lock (_gate) _lastUsed.Remove(accountId);
    }

    public DateTimeOffset? LastUsed(int accountId)
    {
        lock (_gate) return _lastUsed.TryGetValue(accountId, out var t) ? t : null;
    }

    /// <summary>Stoppt laufende Container, deren Account länger als IdleTimeout still ist
    /// (stop, nie rm — die warme Session liegt im Volume).</summary>
    public async Task SweepIdleAsync(CancellationToken ct = default)
    {
        var cutoff = Now() - options.IdleTimeout;
        foreach (var entry in await docker.ListContainersAsync(NamePrefix, ct))
        {
            if (!entry.Running || !TryParseAccountId(entry.Name, out var accountId))
                continue;
            var last = LastUsed(accountId) ?? DateTimeOffset.MinValue;
            if (last > cutoff)
                continue;
            // Nicht-blockierend versuchen: ein laufender Exec (Lock belegt) bedeutet "in Nutzung",
            // nicht idle — der Container wird dann übersprungen statt unter dem Exec weggestoppt zu werden.
            var held = await TryAcquireLockAsync(accountId);
            if (held is null)
            {
                logger.LogInformation("Session-Sandbox: überspringe idle Container {Name} — Exec läuft gerade.",
                    entry.Name);
                continue;
            }
            using (held)
            {
                logger.LogInformation("Session-Sandbox: stoppe idle Container {Name} (zuletzt genutzt {Last:u}).",
                    entry.Name, last);
                await docker.StopAsync(entry.Name, ct);
            }
        }
    }

    /// <summary>Nach Naudit-Neustart: bestehende Präfix-Container als "gerade genutzt" übernehmen
    /// (der Restart-Loop in Program.cs betrifft die Geschwister-Container nicht).</summary>
    public async Task AdoptExistingAsync(CancellationToken ct = default)
    {
        var entries = await docker.ListContainersAsync(NamePrefix, ct);
        var adopted = 0;
        lock (_gate)
        {
            foreach (var entry in entries)
            {
                if (!TryParseAccountId(entry.Name, out var accountId) || _lastUsed.ContainsKey(accountId))
                    continue;
                _lastUsed[accountId] = Now();
                adopted++;
            }
        }
        if (adopted > 0)
            logger.LogInformation("Session-Sandbox: {Count} bestehende Container adoptiert.", adopted);
    }

    /// <summary>Pool-Austritt/Token-Löschung/Suspend: Container UND Volume entfernen — das Volume
    /// enthält die CLI-Credentials des Accounts und darf ihn nicht überleben. Wartet höchstens
    /// RemoveTimeout auf den Account-Lock (der Aufruf hängt am HTTP-Request); false = ein Exec war
    /// in Flight, der Abbau bleibt der Reconciliation im Sweeper überlassen.</summary>
    public async Task<bool> RemoveAsync(int accountId, CancellationToken ct = default)
    {
        var name = ContainerName(accountId);
        using var held = await TryAcquireLockAsync(accountId, options.RemoveTimeout, ct);
        if (held is null)
        {
            logger.LogInformation(
                "Session-Sandbox: Abbau von {Name} verschoben — Exec läuft noch; die Reconciliation räumt nach.", name);
            return false;
        }
        await docker.StopAsync(name, ct);
        await docker.RemoveContainerAsync(name, ct);
        await docker.RemoveVolumeAsync(name, ct);
        lock (_gate) _lastUsed.Remove(accountId);
        return true;
    }

    public async Task<int> CountRunningAsync(CancellationToken ct = default)
        => (await docker.ListContainersAsync(NamePrefix, ct)).Count(e => e.Running);

    // Vor dem Start eines weiteren Containers Platz schaffen: läuft bereits das Cap (ohne den
    // startenden Account selbst), wird der am längsten ungenutzte gestoppt (LRU; unbekannt = ältester).
    private async Task EnforceCapAsync(int startingAccountId, CancellationToken ct)
    {
        var cap = Math.Max(1, options.MaxLiveContainers);
        var running = (await docker.ListContainersAsync(NamePrefix, ct))
            .Where(e => e.Running && TryParseAccountId(e.Name, out var id) && id != startingAccountId)
            .ToList();
        var excess = running.Count - (cap - 1);
        if (excess <= 0)
            return;
        // Kandidaten aufsteigend nach LRU; ein per Exec gesperrter Kandidat wird übersprungen und
        // NICHT auf die Stop-Quote angerechnet — dafür rückt der nächst-älteste nach (stopsNextLru).
        var candidates = running
            .OrderBy(e => TryParseAccountId(e.Name, out var id) ? LastUsed(id) ?? DateTimeOffset.MinValue : DateTimeOffset.MinValue);
        var stopped = 0;
        foreach (var candidate in candidates)
        {
            if (stopped >= excess)
                break;
            if (!TryParseAccountId(candidate.Name, out var candidateAccountId))
                continue;
            // Nicht-blockierend versuchen: ein laufender Exec (Lock belegt) darf nicht per LRU
            // weggestoppt werden — der Container wird dann übersprungen (Cap bleibt transient
            // überschritten, das ist hinnehmbar) statt der Exec killen.
            var held = await TryAcquireLockAsync(candidateAccountId);
            if (held is null)
            {
                logger.LogInformation(
                    "Session-Sandbox: LRU-Kandidat {Name} hat einen laufenden Exec — übersprungen, Cap ({Cap}) bleibt transient überschritten.",
                    candidate.Name, cap);
                continue;
            }
            using (held)
            {
                logger.LogInformation("Session-Sandbox: MaxLiveContainers ({Cap}) erreicht — stoppe LRU-Container {Name}.",
                    cap, candidate.Name);
                await docker.StopAsync(candidate.Name, ct);
            }
            stopped++;
        }
    }

    private async Task<string> ResolveImageAsync(CancellationToken ct)
    {
        if (_image is not null)
            return _image;
        if (!string.IsNullOrWhiteSpace(options.Image))
            return _image = options.Image;
        // Selbst-Inspektion: im Container ist $HOSTNAME die eigene Container-Id ⇒ Image driftet
        // nie von der laufenden Naudit-Version weg. Außerhalb von Docker gibt es kein Image.
        var self = await docker.InspectSelfImageAsync(Environment.MachineName, ct);
        return _image = self ?? throw new DockerUnavailableException(
            "Eigenes Image nicht ermittelbar (läuft Naudit außerhalb von Docker?) — Naudit:Ai:Sandbox:Image setzen.");
    }

    private DateTimeOffset Now() => (time ?? TimeProvider.System).GetUtcNow();

    /// <summary>"naudit-session-&lt;id&gt;" ⇒ Konto-Id; false bei fremden Namen (Sweep/Reconciliation
    /// fassen nur eigene Container an).</summary>
    public static bool TryParseAccountId(string name, out int accountId)
    {
        accountId = 0;
        return name.StartsWith(NamePrefix, StringComparison.Ordinal)
            && int.TryParse(name.AsSpan(NamePrefix.Length), out accountId);
    }

    private sealed class Releaser(SemaphoreSlim sem) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
                sem.Release();
        }
    }
}

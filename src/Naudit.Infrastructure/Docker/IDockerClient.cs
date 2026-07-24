namespace Naudit.Infrastructure.Docker;

/// <summary>Dünne Naht über die Docker-Engine-API — nur die vom Session-Sandbox-Feature benötigten
/// Operationen. Impl: SocketDockerClient (Unix-Socket, kein NuGet); Tests nutzen FakeDockerClient.</summary>
public interface IDockerClient
{
    /// <summary>Socket erreichbar UND nutzbar (GET /_ping)? Liefert false statt zu werfen.</summary>
    Task<bool> PingAsync(CancellationToken ct = default);

    /// <summary>Image-Ref des Containers mit diesem Hostnamen (Selbst-Inspektion im Container:
    /// $HOSTNAME = Container-Id); null, wenn der Engine kein solcher Container bekannt ist.</summary>
    Task<string?> InspectSelfImageAsync(string hostname, CancellationToken ct = default);

    /// <summary>null = Container existiert nicht; sonst der Running-Zustand.</summary>
    Task<ContainerInfo?> InspectContainerAsync(string name, CancellationToken ct = default);

    /// <summary>docker run -d: create (409 = existiert schon, ok) + start, mit benanntem Volume.</summary>
    Task RunDetachedAsync(ContainerRunSpec spec, CancellationToken ct = default);

    Task StartAsync(string name, CancellationToken ct = default);
    Task StopAsync(string name, CancellationToken ct = default);
    Task RemoveContainerAsync(string name, CancellationToken ct = default);
    Task RemoveVolumeAsync(string name, CancellationToken ct = default);

    /// <summary>Schreibt eine Datei in den laufenden Container (PUT /archive, Ein-Eintrag-Tar,
    /// Mode 0644 — der Container-User ist non-root und muss sie lesen können).</summary>
    Task WriteFileAsync(string name, string directory, string fileName, string content, CancellationToken ct = default);

    /// <summary>docker exec (create + start, non-tty, ohne stdin): sammelt stdout/stderr aus dem
    /// multiplexten Stream und liefert den Exit-Code (GET /exec/{id}/json).</summary>
    Task<DockerExecResult> ExecAsync(string name, IReadOnlyList<string> argv,
        IReadOnlyDictionary<string, string?>? environment, string workingDirectory, CancellationToken ct = default);

    /// <summary>Alle Container (auch gestoppte), deren Name mit dem Präfix beginnt — Adoption/Sweep/Status.</summary>
    Task<IReadOnlyList<ContainerListEntry>> ListContainersAsync(string namePrefix, CancellationToken ct = default);

    /// <summary>Legt ein benutzerdefiniertes Netz an — internal (kein Egress: der getestete Code
    /// erreicht weder Internet noch Host). Container betreten das Netz beim Start
    /// (ContainerRunSpec.Network); ein nachträgliches Connect gibt es bewusst nicht — jede
    /// Erreichbarkeit läuft über docker exec. Existiert es schon, ist das kein Fehler.</summary>
    Task CreateNetworkAsync(string name, CancellationToken ct = default);

    /// <summary>Entfernt ein Netz; bereits weg ⇒ kein Fehler (Teardown ist best-effort).</summary>
    Task RemoveNetworkAsync(string name, CancellationToken ct = default);

    /// <summary>Namen aller Netze mit diesem Präfix — für den Orphan-Sweeper.</summary>
    Task<IReadOnlyList<string>> ListNetworksAsync(string namePrefix, CancellationToken ct = default);

    /// <summary>docker build: Kontext als Tar-Strom, Dockerfile-Pfad relativ dazu. Ein
    /// fehlgeschlagener Build ist KEIN Docker-Problem, sondern ein Ergebnis (Success=false, Log) —
    /// nur Transport-/API-Fehler werfen DockerUnavailableException.</summary>
    Task<DockerBuildResult> BuildImageAsync(string tag, Stream tarContext, string dockerfilePath,
        CancellationToken ct = default);

    /// <summary>docker pull: holt ein Referenz-Image (Probe-Container). Schon vorhanden ⇒ schneller
    /// No-op der Engine. Nicht pullbar (Registry weg, Tippfehler) ⇒ DockerUnavailableException —
    /// der Runner behandelt das fail-open.</summary>
    Task PullImageAsync(string reference, CancellationToken ct = default);

    /// <summary>Entfernt ein Image; schon weg/noch in Benutzung ⇒ kein Fehler (best-effort-Teardown).</summary>
    Task RemoveImageAsync(string tag, CancellationToken ct = default);

    /// <summary>Tags aller Images mit diesem Präfix — für den Orphan-Sweeper.</summary>
    Task<IReadOnlyList<string>> ListImagesAsync(string tagPrefix, CancellationToken ct = default);
}

public sealed record ContainerInfo(bool Running);
public sealed record ContainerListEntry(string Name, bool Running);

/// <summary>Volume-Felder sind optional: die Session-Sandbox mountet ihr Credential-Volume, ein
/// DAST-Container bekommt keins. Network/Environment/Limits sind ebenfalls optional — ohne sie
/// verhält sich RunDetachedAsync exakt wie vorher.</summary>
public sealed record ContainerRunSpec(string Name, string Image, string? VolumeName, string? VolumeTarget,
    IReadOnlyList<string> Command)
{
    /// <summary>Benanntes Netz, an dem der Container startet (DAST-Review-Netz).</summary>
    public string? Network { get; init; }

    /// <summary>Env des Containers. Für DAST gilt: nie Naudit-Secrets, nur explizit Gesetztes.</summary>
    public IReadOnlyDictionary<string, string?>? Environment { get; init; }

    /// <summary>Eindämmung für fremden Code; null = keine Grenzen (Session-Sandbox unverändert).</summary>
    public ContainerLimits? Limits { get; init; }

    /// <summary>Überschreibt den ENTRYPOINT des Images (Probe-Container: ["sleep","infinity"] —
    /// er lebt passiv und wird nur per docker exec benutzt). null = ENTRYPOINT des Images gilt.</summary>
    public IReadOnlyList<string>? Entrypoint { get; init; }
}

/// <summary>Grenzen für Container mit fremdem Code: Speicher/CPU/PID-Deckel gegen Fork-Bomb und OOM,
/// dazu alle Capabilities weg und keine Privilege-Eskalation.</summary>
public sealed record ContainerLimits(int MemoryMb, double Cpus, int PidsLimit);

/// <summary>Ergebnis eines Builds: Success=false heißt "dieser PR lässt sich nicht bauen"
/// (kein Fehlerfall der Naht), Log trägt die letzten Zeilen der Engine-Ausgabe.</summary>
public sealed record DockerBuildResult(bool Success, string Log);

public sealed record DockerExecResult(int ExitCode, string StdOut, string StdErr);

/// <summary>Docker-Plumbing-Fehler (Socket/API) — vom Runner fail-open behandelt, nie ein fachlicher claude-Fehler.</summary>
public sealed class DockerUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

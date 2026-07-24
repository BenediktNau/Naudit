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
}

public sealed record ContainerInfo(bool Running);
public sealed record ContainerListEntry(string Name, bool Running);
public sealed record ContainerRunSpec(string Name, string Image, string VolumeName, string VolumeTarget, IReadOnlyList<string> Command);
public sealed record DockerExecResult(int ExitCode, string StdOut, string StdErr);

/// <summary>Docker-Plumbing-Fehler (Socket/API) — vom Runner fail-open behandelt, nie ein fachlicher claude-Fehler.</summary>
public sealed class DockerUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

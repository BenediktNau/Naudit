namespace Naudit.Infrastructure.Ai.Sandbox;

/// <summary>Section Naudit:Ai:Sandbox — Betriebsparameter der containerisierten Abo-Sessions.
/// Bewusst langes IdleTimeout: nicht der Sweeper, sondern MaxLiveContainers (LRU-Stopp) ist im
/// Alltag der Ressourcen-Begrenzer; der Sweeper ist das Netz für "Account tagelang still".</summary>
public sealed class SessionSandboxOptions
{
    /// <summary>Laufende Container, deren Account so lange keinen Review hatte, werden gestoppt
    /// (nicht entfernt — die warme Session liegt im Volume und überlebt den Stopp).</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromDays(2);

    /// <summary>Obergrenze gleichzeitig laufender Session-Container; vor dem Start eines weiteren
    /// wird der am längsten ungenutzte gestoppt (LRU). Untergrenze 1 erzwingt der Manager.</summary>
    public int MaxLiveContainers { get; set; } = 5;

    /// <summary>Pfad zum Docker-Engine-Socket des Hosts.</summary>
    public string DockerSocketPath { get; set; } = "/var/run/docker.sock";

    /// <summary>Optionaler Image-Override; leer ⇒ Selbst-Inspektion (eigenes Image via
    /// `docker inspect $HOSTNAME`) — die claude-CLI ist im Naudit-Image bereits enthalten.</summary>
    public string? Image { get; set; }

    /// <summary>Wie lange RemoveAsync höchstens auf den Account-Lock wartet. Der Abbau hängt am
    /// HTTP-Request (Token löschen/Pool-Austritt/Suspend) und darf nicht auf einen laufenden
    /// Review-Exec warten; wird das Zeitfenster überschritten, übernimmt die Reconciliation
    /// im Sweeper (docs/session-sandbox.md).</summary>
    public TimeSpan RemoveTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Speicher-Obergrenze pro Session-Container in Bytes (HostConfig.Memory);
    /// 0 = unbegrenzt. Default 2 GiB — die claude-CLI (Node) braucht im Review deutlich weniger,
    /// die Grenze fängt Ausreißer/Missbrauch ab, bevor sie den Host treffen.</summary>
    public long MemoryLimitBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    /// <summary>Prozess-/Thread-Obergrenze pro Session-Container (HostConfig.PidsLimit);
    /// 0 = unbegrenzt. Default 512 — Schutz gegen Fork-Bomben aus einem Review heraus.</summary>
    public long PidsLimit { get; set; } = 512;
}

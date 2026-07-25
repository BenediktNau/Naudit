namespace Naudit.Infrastructure.Dast;

/// <summary>Section Naudit:Review:Dast — dynamische Prüfung an der laufenden App. Zwei Schalter
/// hintereinander, weil hier fremder PR-Code gebaut UND ausgeführt wird: der globale Enabled-Schalter
/// und eine Projekt-Allowlist. Leere Allowlist ⇒ kein Projekt (fail-closed).</summary>
public sealed class DastOptions
{
    /// <summary>Globaler Kill-Switch. Bewusst unabhängig von Naudit:Ai:SessionSandbox — andere
    /// Risikoklasse (eigene Abo-Container vs. fremder PR-Code).</summary>
    public bool Enabled { get; set; }

    /// <summary>Freigeschaltete Projekte ("owner/repo" bzw. GitLab-Projekt-Id). Listenförmig ⇒
    /// env/appsettings-only wie ProjectTokens, nicht DB-verwaltet.</summary>
    public List<string> Projects { get; set; } = new();

    /// <summary>Dockerfile relativ zum Checkout-Root; fehlt es, ist DAST für diesen PR nicht anwendbar.</summary>
    public string DockerfilePath { get; set; } = "Dockerfile";

    /// <summary>Port, auf dem die getestete App im Container lauscht.</summary>
    public int AppPort { get; set; } = 8080;

    /// <summary>HTTP-Pfad für den Healthcheck.</summary>
    public string HealthPath { get; set; } = "/";

    /// <summary>Deckel über Build + Start + Healthcheck zusammen.</summary>
    public TimeSpan TimeBudget { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Abstand zwischen zwei Healthcheck-Versuchen.</summary>
    public TimeSpan HealthPollInterval { get; set; } = TimeSpan.FromSeconds(1);

    public int MemoryLimitMb { get; set; } = 1024;
    public double CpuLimit { get; set; } = 1.0;
    public int PidsLimit { get; set; } = 256;

    /// <summary>Obergrenze für den Build-Kontext (der komplett über den Socket in den Daemon wandert).</summary>
    public int MaxContextMb { get; set; } = 200;

    public string DockerSocketPath { get; set; } = "/var/run/docker.sock";

    /// <summary>Image des Probe-Containers im Review-Netz (PR 1: Healthcheck per exec, PR 2:
    /// Playwright-MCP-Server). Wird bei Bedarf gepullt; bewusst nicht naudit-dast-präfixiert,
    /// damit es als Cache über Reviews hinweg stehen bleibt.</summary>
    public string ProbeImage { get; set; } = "mcr.microsoft.com/playwright/mcp:latest";

    /// <summary>Deckel für den agentischen Probing-Loop (Tool-Aufrufe + Modell-Turns zusammen).
    /// Token-frugal: die dynamische Prüfung ist Grounding, kein erschöpfender Scan.</summary>
    public int MaxProbeSteps { get; set; } = 12;

    /// <summary>Kommando, das den Playwright-MCP-Server als stdio-Prozess im Probe-Container startet
    /// (docker exec). Kein --port ⇒ stdio. Als Liste, damit es env-/appsettings-überschreibbar ist.</summary>
    public List<string> ProbeMcpArgv { get; set; } =
        new() { "node", "/app/cli.js", "--headless", "--browser", "chromium", "--no-sandbox" };

    /// <summary>Darf für dieses Projekt gebaut/gestartet werden? Beide Schalter müssen zustimmen.
    /// Config-Binding kann null-Einträge in Projects binden — die werden übersprungen statt eine
    /// NRE zu werfen (AppliesTo läuft in DockerAppRunner.RunAsync vor dem try-Block, ein Fehler
    /// hier würde sonst ungefangen durchschlagen).</summary>
    public bool AppliesTo(string? projectId)
        => Enabled
           && !string.IsNullOrWhiteSpace(projectId)
           && Projects.Any(p => !string.IsNullOrWhiteSpace(p)
                                 && string.Equals(p.Trim(), projectId.Trim(), StringComparison.OrdinalIgnoreCase));
}

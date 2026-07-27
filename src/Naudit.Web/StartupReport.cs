using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Naudit.Core.Review;
using Naudit.Infrastructure.Ai;
using Naudit.Infrastructure.Ai.Logging;
using Naudit.Infrastructure.Dast;
using Naudit.Infrastructure.Data;
using Naudit.Infrastructure.Git;
using Naudit.Infrastructure.Git.GitHub;
using Naudit.Infrastructure.Git.GitLab;
using Naudit.Infrastructure.Mcp;
using Naudit.Infrastructure.Redaction;
using Naudit.Infrastructure.Sast;
using Naudit.Infrastructure.Setup;
using Naudit.Infrastructure.Ui;

// Nur für StartupReport.FormatVersion: reine Formatierungslogik testbar machen, ohne sie public
// zu exponieren (die Klasse hat sonst nur BuildLines/BuildWarnings/Log als bewusste Oberfläche).
[assembly: InternalsVisibleTo("Naudit.Tests")]

namespace Naudit.Web;

/// <summary>Kuratierter Konfigurations-Überblick fürs Start-Log. Bindet die Options bewusst aus
/// IConfiguration statt aus dem DI-Container: AddNauditInfrastructure läuft im Setup- und im
/// Recovery-Modus gar nicht — dort wäre ein Container-basierter Report leer, obwohl man ihn
/// gerade dann braucht. Enthält keine Secrets, nur Enums, Bools, Zahlen und Namen.</summary>
public static class StartupReport
{
    private const string Rule = "════════════════════════════════════════════════";

    public static IReadOnlyList<string> BuildLines(
        IConfiguration config, SetupStatusResult setup, string? recoveryError)
    {
        var git = config.GetSection("Naudit:Git").Get<GitOptions>() ?? new GitOptions();
        var gitHub = config.GetSection("Naudit:GitHub").Get<GitHubOptions>() ?? new GitHubOptions();
        var gitLab = config.GetSection("Naudit:GitLab").Get<GitLabOptions>() ?? new GitLabOptions();
        var ai = config.GetSection("Naudit:Ai").Get<AiOptions>() ?? new AiOptions();
        var aiLogging = config.GetSection("Naudit:Ai:Logging").Get<AiLoggingOptions>() ?? new AiLoggingOptions();
        var mcp = config.GetSection("Naudit:Review:Mcp").Get<McpOptions>() ?? new McpOptions();
        var sast = config.GetSection("Naudit:Sast").Get<SastOptions>() ?? new SastOptions();
        var dast = config.GetSection("Naudit:Review:Dast").Get<DastOptions>() ?? new DastOptions();
        var review = config.GetSection("Naudit:Review").Get<ReviewOptions>() ?? new ReviewOptions();
        var redaction = config.GetSection("Naudit:Redaction").Get<RedactionOptions>() ?? new RedactionOptions();
        var gate = config.GetSection("Naudit:AccessGate").Get<AccessGateOptions>() ?? new AccessGateOptions();
        var db = config.GetSection("Naudit:Db").Get<DatabaseOptions>() ?? new DatabaseOptions();

        var mode = setup.SetupRequired ? "SETUP — Wizard aktiv, Webhooks nicht gemappt"
            : recoveryError is not null ? "RECOVERY — Review-Pipeline nicht geladen"
            : "Review aktiv";

        var lines = new List<string>
        {
            Rule,
            $"  Naudit {Version()}",
            $"  Modus:      {mode}",
            git.Platform == GitPlatformKind.GitHub
                ? $"  Plattform:  GitHub · Auth: {gitHub.Auth} · PostVerdict: {OnOff(gitHub.PostVerdict)}"
                : $"  Plattform:  GitLab · PostVerdict: {OnOff(gitLab.PostVerdict)}",
            $"  AI:         {ai.Provider} · {Model(ai.Model)} · Routing: {ai.SessionRouting}"
                + $" · Sandbox: {ai.SessionSandbox} · MCP: {OnOff(mcp.Enabled)} · Logging: {OnOff(aiLogging.Enabled)}",
            sast.Enabled
                ? $"  SAST:       AN · {string.Join(", ", SastOptions.ResolveAnalyzers(sast.Analyzers))}"
                : "  SAST:       aus",
            dast.Enabled
                ? $"  DAST:       AN · Allowlist: {List(dast.Projects)}"
                : "  DAST:       aus",
            $"  Prompt:     Kontext {OnOff(review.Context.Enabled)} · Memory {OnOff(review.Memory.Enabled)}"
                + $" (max {review.Memory.MaxEntries}) · Guidelines {OnOff(review.Guidelines.Enabled)}"
                + $" · Redaction {OnOff(redaction.Enabled)}",
            $"  Review:     Gate ab {review.Gate.MinSeverity}/{review.Gate.MinConfidence}"
                + $" · MaxRoundtrips {review.MaxRoundtrips} · Resolution {OnOff(review.Resolution.Enabled)}",
            $"  Zugang:     AccessGate {gate.Mode} · DB {db.Provider}",
        };

        if (setup.SetupRequired && setup.MissingKeys.Count > 0)
            lines.Add($"  Fehlt:      {string.Join(", ", setup.MissingKeys)}");
        if (recoveryError is not null)
            lines.Add($"  Fehler:     {recoveryError}");

        lines.Add(Rule);
        return lines;
    }

    private static string OnOff(bool value) => value ? "AN" : "aus";

    private static string Model(string model) =>
        string.IsNullOrWhiteSpace(model) ? "(kein Modell)" : model;

    private static string List(IReadOnlyCollection<string> items) =>
        items.Count == 0 ? "(leer)" : string.Join(", ", items);

    private static string Version() => FormatVersion(
        typeof(StartupReport).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    /// <summary>Reine Formatierungslogik, getrennt von der Assembly-Reflection in <see cref="Version"/> —
    /// so lässt sie sich mit synthetischen Rohwerten testen, ohne dass das Testassembly selbst
    /// gestempelt sein müsste. Zwei Fälle gelten als "ungestempelt" und werden (dev) markiert:
    /// das Attribut fehlt ganz (kein /p:Version gesetzt), oder die Version trägt das
    /// Dockerfile-Default-Suffix "-dev" (ARG VERSION=0.0.0-dev, wenn niemand
    /// --build-arg VERSION=... übergibt) — beides ohne den alten "StartsWith 1.0.0"-Trick, der ein
    /// echtes v1.0.0-Release fälschlich als (dev) markiert hätte. Ein ungestempelter lokaler
    /// `dotnet run` meldet dagegen .NETs eigenen Default "1.0.0" OHNE "-dev"-Suffix — von einem
    /// echten v1.0.0-Release ist das auf dieser Ebene nicht zu unterscheiden, deshalb erscheint hier
    /// schlicht "v1.0.0" ohne Marker (lokale Dev-Läufe sind nicht die Zielgruppe dieses Signals;
    /// Docker/Release sind es, und die sind sauber erkennbar).</summary>
    internal static string FormatVersion(string? raw)
    {
        const string devSuffix = "-dev";
        if (string.IsNullOrWhiteSpace(raw))
            return "v0.0.0 (dev)";
        // SourceLink hängt "+<commit-sha>" an — für die Log-Zeile uninteressant.
        var plus = raw.IndexOf('+');
        var version = plus > 0 ? raw[..plus] : raw;
        return version.EndsWith(devSuffix, StringComparison.OrdinalIgnoreCase)
            ? $"v{version[..^devSuffix.Length]} (dev)"
            : $"v{version}";
    }

    /// <summary>Gültige, aber wirkungslose Konfigurationen — sie erzeugen keinen Fehler und fallen
    /// deshalb sonst erst auf, wenn ein erwartetes Review-Verhalten ausbleibt.</summary>
    public static IReadOnlyList<string> BuildWarnings(IConfiguration config)
    {
        var ai = config.GetSection("Naudit:Ai").Get<AiOptions>() ?? new AiOptions();
        var sast = config.GetSection("Naudit:Sast").Get<SastOptions>() ?? new SastOptions();
        var dast = config.GetSection("Naudit:Review:Dast").Get<DastOptions>() ?? new DastOptions();
        var review = config.GetSection("Naudit:Review").Get<ReviewOptions>() ?? new ReviewOptions();

        var warnings = new List<string>();

        if (dast.Enabled && dast.Projects.Count == 0)
            warnings.Add("DAST ist aktiviert, aber Allowlist Naudit:Review:Dast:Projects ist leer — "
                + "kein Projekt wird dynamisch getestet.");

        if (sast.Enabled && sast.Analyzers.Count == 0)
            warnings.Add("Naudit:Sast:Analyzers ist leer — es greift der Default "
                + $"'{string.Join(", ", SastOptions.DefaultAnalyzers)}'.");

        if (ai.SessionSandbox == SessionSandbox.Docker && ai.SessionRouting == SessionRouting.Single)
            warnings.Add("Naudit:Ai:SessionSandbox=Docker bleibt ohne Wirkung — die Sandbox greift "
                + "nur bei SessionRouting Author/RoundRobin.");

        if (review.MaxRoundtrips <= 0)
            warnings.Add("Naudit:Review:MaxRoundtrips ist deaktiviert — jeder Push löst ein "
                + "weiteres Review aus (Kostenbremse aus).");

        return warnings;
    }

    /// <summary>Block als Information, Warnzeilen als Warning. Vollständig fail-safe: ein Fehler
    /// im Report (z. B. ein un-parsebarer Enum-Wert in der Config) darf den Start nie kippen —
    /// dafür ist im Fehlerfall der Recovery-Modus zuständig, nicht das Log.</summary>
    public static void Log(ILogger logger, IConfiguration config, SetupStatusResult setup, string? recoveryError)
    {
        try
        {
            foreach (var line in BuildLines(config, setup, recoveryError))
                logger.LogInformation("{Line}", line);
            foreach (var warning in BuildWarnings(config))
                logger.LogWarning("{Warning}", warning);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Startup-Report konnte nicht erzeugt werden.");
        }
    }
}

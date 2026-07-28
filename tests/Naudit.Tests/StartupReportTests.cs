using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Naudit.Infrastructure.Settings;
using Naudit.Infrastructure.Setup;
using Naudit.Web;
using Xunit;

namespace Naudit.Tests;

/// <summary>Startup-Report: kuratierter Konfigurationsblock aus reiner IConfiguration —
/// kein Host, kein DI-Container (der Report muss auch im Setup-/Recovery-Modus tragen).</summary>
public class StartupReportTests
{
    private static readonly SetupStatusResult Ready = new(false, []);

    private static IConfiguration Config(params (string Key, string Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    // Die Blockzeilen sind eingerückt — vor dem Präfix-Vergleich trimmen.
    private static string Line(IReadOnlyList<string> lines, string prefix)
        => Assert.Single(lines, l => l.TrimStart().StartsWith(prefix, StringComparison.Ordinal));

    [Fact]
    public void BuildLines_gitHubWithAppAuth_showsPlatformAndAuth()
    {
        var lines = StartupReport.BuildLines(Config(
            ("Naudit:Git:Platform", "GitHub"),
            ("Naudit:GitHub:Auth", "App"),
            ("Naudit:GitHub:PostVerdict", "true")), Ready, null);

        var platform = Line(lines, "Plattform:");
        Assert.Contains("GitHub", platform);
        Assert.Contains("Auth: App", platform);
        Assert.Contains("PostVerdict: AN", platform);
    }

    [Fact]
    public void BuildLines_gitLab_omitsGitHubOnlyFields()
    {
        var lines = StartupReport.BuildLines(Config(("Naudit:Git:Platform", "GitLab")), Ready, null);

        var platform = Line(lines, "Plattform:");
        Assert.Contains("GitLab", platform);
        // Auth ist ein reiner GitHub-Begriff — auf GitLab wäre die Angabe schlicht falsch.
        Assert.DoesNotContain("Auth:", platform);
    }

    [Fact]
    public void BuildLines_sastEnabledWithAnalyzers_listsThemByName()
    {
        var lines = StartupReport.BuildLines(Config(
            ("Naudit:Sast:Enabled", "true"),
            ("Naudit:Sast:Analyzers:0", "trivy"),
            ("Naudit:Sast:Analyzers:1", "osv-scanner")), Ready, null);

        var sast = Line(lines, "SAST:");
        Assert.Contains("AN", sast);
        Assert.Contains("trivy, osv-scanner", sast);
    }

    [Fact]
    public void BuildLines_sastEnabledWithoutAnalyzers_showsTheDefaultThatDiRegisters()
    {
        var lines = StartupReport.BuildLines(Config(("Naudit:Sast:Enabled", "true")), Ready, null);

        // Der Report muss zeigen, was WIRKLICH läuft — DI setzt hier den Default-Paar-Fallback.
        Assert.Contains("opengrep, trivy", Line(lines, "SAST:"));
    }

    [Fact]
    public void BuildLines_sastDisabled_saysOff()
    {
        var lines = StartupReport.BuildLines(Config(), Ready, null);

        Assert.Contains("aus", Line(lines, "SAST:"));
    }

    [Fact]
    public void BuildLines_dastEnabledWithAllowlist_listsProjects()
    {
        var lines = StartupReport.BuildLines(Config(
            ("Naudit:Review:Dast:Enabled", "true"),
            ("Naudit:Review:Dast:Projects:0", "acme/web"),
            ("Naudit:Review:Dast:Projects:1", "acme/api")), Ready, null);

        var dast = Line(lines, "DAST:");
        Assert.Contains("acme/web, acme/api", dast);
    }

    [Fact]
    public void BuildLines_dastEnabledWithEmptyAllowlist_marksItEmpty()
    {
        var lines = StartupReport.BuildLines(Config(("Naudit:Review:Dast:Enabled", "true")), Ready, null);

        Assert.Contains("(leer)", Line(lines, "DAST:"));
    }

    [Fact]
    public void BuildLines_setupMode_showsModeAndMissingKeys()
    {
        var setup = new SetupStatusResult(true, ["Naudit:GitHub:Token", "Naudit:Ai:Model"]);

        var lines = StartupReport.BuildLines(Config(), setup, null);

        Assert.Contains("SETUP", Line(lines, "Modus:"));
        var joined = string.Join("\n", lines);
        Assert.Contains("Naudit:GitHub:Token", joined);
        Assert.Contains("Naudit:Ai:Model", joined);
    }

    [Fact]
    public void BuildLines_recoveryMode_showsModeAndError()
    {
        var lines = StartupReport.BuildLines(
            Config(), Ready, new InvalidOperationException("PrivateKey fehlt: /etc/keys/app.pem"));

        Assert.Contains("RECOVERY", Line(lines, "Modus:"));
        var joined = string.Join("\n", lines);
        Assert.Contains(nameof(InvalidOperationException), joined);
        // Die Ausnahme-MELDUNG darf nicht in den Block: sie zitiert oft den auslösenden
        // Konfigurationswert, und der Block ist als secret-frei dokumentiert.
        Assert.DoesNotContain("PrivateKey fehlt", joined);
    }

    [Fact]
    public void BuildLines_healthyConfig_saysReviewActive()
    {
        var lines = StartupReport.BuildLines(Config(), Ready, null);

        Assert.Contains("Review aktiv", Line(lines, "Modus:"));
    }

    [Fact]
    public void BuildLines_aiLine_reflectsAllConfiguredFields()
    {
        // Nicht-Default-Werte auf jedem Feld der AI-Zeile: eine vertauschte Sektion (z. B. ein
        // Memory- statt Guidelines-Feld) fällt nur so auf — ein Default-Wert wäre auch bei
        // falschem Pfad zufällig korrekt.
        var lines = StartupReport.BuildLines(Config(
            ("Naudit:Ai:Provider", "Anthropic"),
            ("Naudit:Ai:Model", "some-model"),
            ("Naudit:Ai:SessionRouting", "Author"),
            ("Naudit:Ai:SessionSandbox", "Docker"),
            ("Naudit:Review:Mcp:Enabled", "true"),
            ("Naudit:Ai:Logging:Enabled", "true")), Ready, null);

        var ai = Line(lines, "AI:");
        Assert.Contains("Anthropic", ai);
        Assert.Contains("some-model", ai);
        Assert.Contains("Routing: Author", ai);
        Assert.Contains("Sandbox: Docker", ai);
        Assert.Contains("MCP: AN", ai);
        Assert.Contains("Logging: AN", ai);
    }

    [Fact]
    public void BuildLines_aiLine_noModelConfigured_showsFallback()
    {
        var lines = StartupReport.BuildLines(Config(), Ready, null);

        Assert.Contains("(kein Modell)", Line(lines, "AI:"));
    }

    [Fact]
    public void BuildLines_promptLine_reflectsAllConfiguredFields()
    {
        var lines = StartupReport.BuildLines(Config(
            ("Naudit:Review:Context:Enabled", "false"),
            ("Naudit:Review:Memory:Enabled", "false"),
            ("Naudit:Review:Memory:MaxEntries", "7"),
            ("Naudit:Review:Guidelines:Enabled", "false"),
            ("Naudit:Redaction:Enabled", "false")), Ready, null);

        var prompt = Line(lines, "Prompt:");
        Assert.Contains("Kontext aus", prompt);
        Assert.Contains("Memory aus", prompt);
        Assert.Contains("(max 7)", prompt);
        Assert.Contains("Guidelines aus", prompt);
        Assert.Contains("Redaction aus", prompt);
    }

    [Fact]
    public void BuildLines_reviewLine_reflectsAllConfiguredFields()
    {
        var lines = StartupReport.BuildLines(Config(
            ("Naudit:Review:Gate:MinSeverity", "Critical"),
            ("Naudit:Review:Gate:MinConfidence", "High"),
            ("Naudit:Review:MaxRoundtrips", "9"),
            ("Naudit:Review:Resolution:Enabled", "false")), Ready, null);

        var review = Line(lines, "Review:");
        Assert.Contains("Gate ab Critical/High", review);
        Assert.Contains("MaxRoundtrips 9", review);
        Assert.Contains("Resolution aus", review);
    }

    [Fact]
    public void BuildLines_zugangLine_reflectsAllConfiguredFields()
    {
        var lines = StartupReport.BuildLines(Config(
            ("Naudit:AccessGate:Mode", "Registered"),
            ("Naudit:Db:Provider", "Postgres")), Ready, null);

        var zugang = Line(lines, "Zugang:");
        Assert.Contains("AccessGate Registered", zugang);
        Assert.Contains("DB Postgres", zugang);
    }

    // Schlüssel, die geheimwertig sind, aber NICHT im SettingsCatalog als IsSecret geführt werden —
    // der Katalog allein würde das Secret-Leak-Invariant also nur unvollständig prüfen:
    //  - Naudit:Ai:Endpoint ist im Katalog bewusst IsSecret:false (siehe SettingsCatalog), das
    //    Design schließt es aus dem Report aus, weil manche OpenAI-kompatiblen Dienste den
    //    API-Key im URL-Pfad tragen statt in einem eigenen Feld.
    //  - ProjectTokens (GitHub/GitLab) sind env-only (kein Katalogeintrag, siehe IGitTokenProvider),
    //    tragen den Token aber im Wert einer Listen-Entry — Bind-Form ":0:Token".
    //  - Mcp:Servers:*:ApiKey ist ebenfalls kein Katalogeintrag (Server ist eine Liste, kein
    //    einzelner Key) und trägt den Key im ApiKey-Feld — Bind-Form ":0:ApiKey".
    private static readonly string[] ExtraSecretBearingKeys =
    [
        "Naudit:Ai:Endpoint",
        "Naudit:GitHub:ProjectTokens:0:Token",
        "Naudit:GitLab:ProjectTokens:0:Token",
        "Naudit:Review:Mcp:Servers:0:ApiKey",
    ];

    [Fact]
    public void BuildLines_neverLeaksAnySecretValue()
    {
        // Jeden IsSecret-Katalogschlüssel PLUS die geheimwertigen Nicht-Katalog-Schlüssel oben mit
        // einem eindeutigen Sentinel belegen und danach prüfen, dass keiner davon im Block
        // auftaucht — der Report ist ein Log, das in Coolify/Docker landet und potenziell
        // weitergereicht wird.
        var secretKeys = SettingsCatalog.All.Where(d => d.IsSecret).Select(d => d.Key)
            .Concat(ExtraSecretBearingKeys)
            .ToList();
        Assert.NotEmpty(secretKeys);
        var values = secretKeys
            .Select((key, i) => (Key: key, Value: $"SENTINEL-SECRET-{i}"))
            .ToArray();

        var lines = StartupReport.BuildLines(Config(values), Ready, null);

        var joined = string.Join("\n", lines);
        foreach (var (_, value) in values)
            Assert.DoesNotContain(value, joined);
    }

    [Fact]
    public void BuildWarnings_dastEnabledWithoutAllowlist_warns()
    {
        var warnings = StartupReport.BuildWarnings(Config(("Naudit:Review:Dast:Enabled", "true")));

        Assert.Contains(warnings, w => w.Contains("DAST") && w.Contains("Allowlist"));
    }

    [Fact]
    public void BuildWarnings_dastEnabledWithAllowlist_isSilent()
    {
        var warnings = StartupReport.BuildWarnings(Config(
            ("Naudit:Review:Dast:Enabled", "true"),
            ("Naudit:Review:Dast:Projects:0", "acme/web")));

        Assert.DoesNotContain(warnings, w => w.Contains("DAST"));
    }

    [Fact]
    public void BuildWarnings_sastEnabledWithoutAnalyzers_warnsAboutTheDefault()
    {
        var warnings = StartupReport.BuildWarnings(Config(("Naudit:Sast:Enabled", "true")));

        Assert.Contains(warnings, w => w.Contains("Naudit:Sast:Analyzers"));
    }

    [Fact]
    public void BuildWarnings_sandboxDockerWithSingleRouting_warns()
    {
        var warnings = StartupReport.BuildWarnings(Config(
            ("Naudit:Ai:SessionSandbox", "Docker"),
            ("Naudit:Ai:SessionRouting", "Single")));

        Assert.Contains(warnings, w => w.Contains("SessionSandbox"));
    }

    [Fact]
    public void BuildWarnings_sandboxDockerWithAuthorRouting_isSilent()
    {
        var warnings = StartupReport.BuildWarnings(Config(
            ("Naudit:Ai:SessionSandbox", "Docker"),
            ("Naudit:Ai:SessionRouting", "Author")));

        Assert.DoesNotContain(warnings, w => w.Contains("SessionSandbox"));
    }

    [Fact]
    public void BuildWarnings_roundtripLimitOff_warns()
    {
        var warnings = StartupReport.BuildWarnings(Config(("Naudit:Review:MaxRoundtrips", "0")));

        Assert.Contains(warnings, w => w.Contains("MaxRoundtrips"));
    }

    [Fact]
    public void BuildWarnings_defaultConfig_isSilent()
    {
        // Frische Installation ohne Zutun: SAST/DAST aus, Routing Single, MaxRoundtrips 3.
        Assert.Empty(StartupReport.BuildWarnings(Config()));
    }

    [Theory]
    [InlineData(null, "v0.0.0 (dev)")]
    [InlineData("", "v0.0.0 (dev)")]
    [InlineData("   ", "v0.0.0 (dev)")]
    // Dockerfile-Default (kein --build-arg VERSION): unstempelt, (dev) markiert.
    [InlineData("0.0.0-dev", "v0.0.0 (dev)")]
    // SourceLink hängt den Commit-Sha an — muss vor der -dev-Prüfung abgeschnitten werden.
    [InlineData("0.0.0-dev+abc1234", "v0.0.0 (dev)")]
    // Ein echtes v1.0.0-Release darf NIE als (dev) markiert werden (der alte Bug).
    [InlineData("1.0.0", "v1.0.0")]
    [InlineData("1.0.0+abc1234", "v1.0.0")]
    [InlineData("1.2.3", "v1.2.3")]
    [InlineData("2.4.7-dev", "v2.4.7 (dev)")]
    public void FormatVersion_rendersDevMarkerOnlyForMissingOrDevSuffix(string? raw, string expected)
    {
        Assert.Equal(expected, StartupReport.FormatVersion(raw));
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    [Fact]
    public void Log_writesBlockAsInformation_andWarningsAsWarning()
    {
        var logger = new RecordingLogger();

        StartupReport.Log(logger, Config(("Naudit:Review:Dast:Enabled", "true")), Ready, null);

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("Modus:"));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("DAST"));
    }

    [Fact]
    public void Log_whenConfigThrows_doesNotPropagate()
    {
        // Ein Report-Fehler darf den Host NIE am Start hindern (Audit-Sink-Philosophie).
        var logger = new RecordingLogger();
        // Ein un-parsebarer Enum-Wert lässt Get<AiOptions>() werfen.
        var broken = Config(("Naudit:Ai:Provider", "KeinEchterProvider"));

        StartupReport.Log(logger, broken, Ready, null);

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("Startup-Report"));
    }
}

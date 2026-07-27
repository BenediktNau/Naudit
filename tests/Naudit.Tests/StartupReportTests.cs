using Microsoft.Extensions.Configuration;
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
        => Assert.Single(lines.Where(l => l.TrimStart().StartsWith(prefix, StringComparison.Ordinal)));

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
        var lines = StartupReport.BuildLines(Config(), Ready, "PrivateKey fehlt");

        Assert.Contains("RECOVERY", Line(lines, "Modus:"));
        Assert.Contains("PrivateKey fehlt", string.Join("\n", lines));
    }

    [Fact]
    public void BuildLines_healthyConfig_saysReviewActive()
    {
        var lines = StartupReport.BuildLines(Config(), Ready, null);

        Assert.Contains("Review aktiv", Line(lines, "Modus:"));
    }

    [Fact]
    public void BuildLines_neverLeaksAnySecretValue()
    {
        // Jeden IsSecret-Katalogschlüssel mit einem eindeutigen Sentinel belegen und danach
        // prüfen, dass keiner davon im Block auftaucht — der Report ist ein Log, das in
        // Coolify/Docker landet und potenziell weitergereicht wird.
        var secrets = SettingsCatalog.All.Where(d => d.IsSecret).ToList();
        Assert.NotEmpty(secrets);
        var values = secrets
            .Select((d, i) => (d.Key, Value: $"SENTINEL-SECRET-{i}"))
            .ToArray();

        var lines = StartupReport.BuildLines(Config(values), Ready, null);

        var joined = string.Join("\n", lines);
        foreach (var (_, value) in values)
            Assert.DoesNotContain(value, joined);
    }
}

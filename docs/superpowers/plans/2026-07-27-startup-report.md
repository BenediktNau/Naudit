# Startup-Report Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Naudit loggt beim Hochfahren einen kuratierten Block mit der effektiv geladenen Konfiguration (Plattform, AI-Provider, SAST, DAST, Prompt-/Review-Schalter) plus Warnzeilen für wirkungslose Kombinationen.

**Architecture:** Eine statische Klasse `StartupReport` in `Naudit.Web` bindet die `*Options` direkt aus `IConfiguration` (nicht aus dem DI-Container, weil `AddNauditInfrastructure` im Setup-/Recovery-Modus gar nicht läuft) und liefert reine String-Listen. `Program.cs` loggt sie unmittelbar nach `builder.Build()`. Der Aufruf ist fail-safe gekapselt.

**Tech Stack:** .NET 10, ASP.NET Minimal API, `Microsoft.Extensions.Configuration`, xUnit.

## Global Constraints

- Spec: `docs/superpowers/specs/2026-07-27-startup-report-design.md`.
- Solution-Datei ist `Naudit.slnx` — `dotnet test Naudit.sln` schlägt mit MSB1009 fehl.
- Code-Kommentare auf Deutsch (Projektkonvention).
- **Die Core-Regel bleibt unangetastet:** `Naudit.Core` bekommt keine neuen Abhängigkeiten. `StartupReport` lebt in `Naudit.Web` und darf Infrastructure-Options lesen.
- **Keine Secrets im Log** — weder im Klartext noch maskiert. Ausgegeben werden nur Enums, Bools, Zahlen und Analyzer-/Projektnamen. `Naudit:Ai:Endpoint` bleibt draußen.
- TDD: erst der fehlschlagende Test, dann die Implementierung, ein Commit pro Task.

## File Structure

| Datei | Verantwortung |
| --- | --- |
| `src/Naudit.Infrastructure/Sast/SastOptions.cs` (ändern) | Bekommt `ResolveAnalyzers` — der Analyzer-Default als eine geteilte Quelle für DI und Report |
| `src/Naudit.Infrastructure/DependencyInjection.cs` (ändern, Zeile 300-301) | Nutzt `ResolveAnalyzers` statt der Inline-Zuweisung |
| `src/Naudit.Web/StartupReport.cs` (neu) | Baut Blockzeilen + Warnzeilen aus `IConfiguration`; loggt sie fail-safe |
| `src/Naudit.Web/Program.cs` (ändern, nach Zeile 229) | Ruft `StartupReport.Log` auf |
| `tests/Naudit.Tests/SastOptionsTests.cs` (ändern) | Tests für `ResolveAnalyzers` |
| `tests/Naudit.Tests/StartupReportTests.cs` (neu) | Tests für `BuildLines`, `BuildWarnings`, `Log` |
| `Dockerfile` (ändern, Zeile 17) | `ARG VERSION` → `/p:Version` |
| `.github/workflows/release.yml` (ändern) | Reicht die berechnete SemVer als Build-Arg und an die Binary-Publishes |
| `docs/deployment.md` (ändern) | Kurzer Abschnitt, was der Block zeigt |

---

### Task 1: `SastOptions.ResolveAnalyzers` — eine Quelle für den Analyzer-Default

Heute setzt `DependencyInjection.cs:300-301` den Fallback `opengrep, trivy`, wenn `Naudit:Sast:Analyzers` leer ist. Der Report muss denselben Default kennen, sonst meldet er „keine Analyzer", während zwei laufen. Der Fallback wandert deshalb als statische Methode nach `SastOptions` — analog zum bereits vorhandenen `ResolveOpengrepRules`.

**Files:**
- Modify: `src/Naudit.Infrastructure/Sast/SastOptions.cs`
- Modify: `src/Naudit.Infrastructure/DependencyInjection.cs:299-301`
- Test: `tests/Naudit.Tests/SastOptionsTests.cs`

**Interfaces:**
- Consumes: nichts.
- Produces: `public static readonly IReadOnlyList<string> DefaultAnalyzers` und `public static List<string> ResolveAnalyzers(IEnumerable<string> configured)` auf `Naudit.Infrastructure.Sast.SastOptions`. Task 2 ruft `SastOptions.ResolveAnalyzers(sast.Analyzers)` auf.

- [ ] **Step 1: Write the failing tests**

An `tests/Naudit.Tests/SastOptionsTests.cs` anhängen (die Datei existiert bereits und enthält die `ResolveOpengrepRules`-Tests):

```csharp
    [Fact]
    public void ResolveAnalyzers_withEmptyConfig_usesDefaultPair()
    {
        var analyzers = SastOptions.ResolveAnalyzers([]);

        // Ohne Konfiguration greift derselbe Default wie in der DI-Registrierung.
        Assert.Equal(new[] { "opengrep", "trivy" }, analyzers);
    }

    [Fact]
    public void ResolveAnalyzers_withConfiguredList_returnsItUnchanged()
    {
        var analyzers = SastOptions.ResolveAnalyzers(["trivy", "osv-scanner"]);

        // Konfiguriert heißt konfiguriert — der Default ersetzt nichts und ergänzt nichts
        // (anders als ResolveOpengrepRules, wo das Overlay immer mitlaufen MUSS).
        Assert.Equal(new[] { "trivy", "osv-scanner" }, analyzers);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter "FullyQualifiedName~SastOptionsTests"`
Expected: Compile-Fehler `CS0117: 'SastOptions' enthält keine Definition für 'ResolveAnalyzers'`.

- [ ] **Step 3: Implement `ResolveAnalyzers`**

In `src/Naudit.Infrastructure/Sast/SastOptions.cs` direkt unter der `Analyzers`-Property einfügen:

```csharp
    /// <summary>Analyzer-Default, wenn nichts konfiguriert ist. Bewusst hier statt in der
    /// DI-Registrierung: der Startup-Report muss denselben Wert anzeigen, den DI registriert.</summary>
    public static readonly IReadOnlyList<string> DefaultAnalyzers = ["opengrep", "trivy"];

    /// <summary>Effektive Analyzer-Liste: konfigurierte Namen, sonst <see cref="DefaultAnalyzers"/>.
    /// Anders als bei den OpenGrep-Regeln NICHT additiv — wer Analyzer wählt, wählt sie abschließend.</summary>
    public static List<string> ResolveAnalyzers(IEnumerable<string> configured)
    {
        var list = configured.ToList();
        return list.Count > 0 ? list : DefaultAnalyzers.ToList();
    }
```

- [ ] **Step 4: DI auf die neue Methode umstellen**

In `src/Naudit.Infrastructure/DependencyInjection.cs` die beiden Zeilen

```csharp
        if (sastOptions.Analyzers.Count == 0)
            sastOptions.Analyzers = new() { "opengrep", "trivy" };
```

ersetzen durch:

```csharp
        sastOptions.Analyzers = SastOptions.ResolveAnalyzers(sastOptions.Analyzers);
```

- [ ] **Step 5: Run the full suite**

Run: `dotnet test Naudit.slnx`
Expected: PASS — insbesondere `SastOptionsTests` und `SastWiringTests` (letztere prüft die tatsächlich registrierten Analyzer und darf sich nicht ändern).

- [ ] **Step 6: Commit**

```bash
git add src/Naudit.Infrastructure/Sast/SastOptions.cs src/Naudit.Infrastructure/DependencyInjection.cs tests/Naudit.Tests/SastOptionsTests.cs
git commit -m "refactor(sast): Analyzer-Default als SastOptions.ResolveAnalyzers teilen"
```

---

### Task 2: `StartupReport.BuildLines` — der Konfigurationsblock

**Files:**
- Create: `src/Naudit.Web/StartupReport.cs`
- Test: `tests/Naudit.Tests/StartupReportTests.cs` (neu)

**Interfaces:**
- Consumes: `SastOptions.ResolveAnalyzers` (Task 1); `Naudit.Infrastructure.Setup.SetupStatusResult(bool SetupRequired, IReadOnlyList<string> MissingKeys)`.
- Produces: `public static IReadOnlyList<string> StartupReport.BuildLines(IConfiguration config, SetupStatusResult setup, string? recoveryError)` im Namespace `Naudit.Web`. Task 3 ergänzt `BuildWarnings`, Task 4 ergänzt `Log`.

- [ ] **Step 1: Write the failing tests**

Neue Datei `tests/Naudit.Tests/StartupReportTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter "FullyQualifiedName~StartupReportTests"`
Expected: Compile-Fehler — `StartupReport` existiert nicht.

- [ ] **Step 3: Implement `StartupReport.BuildLines`**

Neue Datei `src/Naudit.Web/StartupReport.cs`:

```csharp
using System.Reflection;
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
            $"{Rule}",
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

    /// <summary>Version aus dem Assembly-Stempel (Dockerfile/release.yml reichen /p:Version durch).
    /// Ungestempelt meldet .NET 1.0.0 — das als (dev) kennzeichnen, damit im Log nie eine
    /// erfundene Release-Version steht.</summary>
    private static string Version()
    {
        var raw = typeof(StartupReport).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(raw))
            return "v0.0.0 (dev)";
        // SourceLink hängt "+<commit-sha>" an — für die Log-Zeile uninteressant.
        var plus = raw.IndexOf('+');
        var version = plus > 0 ? raw[..plus] : raw;
        return version.StartsWith("1.0.0", StringComparison.Ordinal) ? $"v{version} (dev)" : $"v{version}";
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter "FullyQualifiedName~StartupReportTests"`
Expected: PASS (alle 11 Tests).

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Web/StartupReport.cs tests/Naudit.Tests/StartupReportTests.cs
git commit -m "feat(web): StartupReport baut den Konfigurationsblock fuers Start-Log"
```

---

### Task 3: `StartupReport.BuildWarnings` — wirkungslose Kombinationen melden

**Files:**
- Modify: `src/Naudit.Web/StartupReport.cs`
- Test: `tests/Naudit.Tests/StartupReportTests.cs`

**Interfaces:**
- Consumes: die privaten Binder aus Task 2 (gleiche Klasse).
- Produces: `public static IReadOnlyList<string> StartupReport.BuildWarnings(IConfiguration config)`. Task 4 loggt das Ergebnis als `LogWarning`.

- [ ] **Step 1: Write the failing tests**

An `tests/Naudit.Tests/StartupReportTests.cs` anhängen:

```csharp
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
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter "FullyQualifiedName~StartupReportTests"`
Expected: Compile-Fehler `CS0117: 'StartupReport' enthält keine Definition für 'BuildWarnings'`.

- [ ] **Step 3: Implement `BuildWarnings`**

In `src/Naudit.Web/StartupReport.cs` direkt unter `BuildLines` einfügen:

```csharp
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
            warnings.Add("DAST ist aktiviert, aber Naudit:Review:Dast:Projects ist leer — "
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
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter "FullyQualifiedName~StartupReportTests"`
Expected: PASS (18 Tests).

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Web/StartupReport.cs tests/Naudit.Tests/StartupReportTests.cs
git commit -m "feat(web): Warnzeilen fuer wirkungslose SAST/DAST/Sandbox-Kombinationen"
```

---

### Task 4: `Log` + Verdrahtung in `Program.cs` + Doku

**Files:**
- Modify: `src/Naudit.Web/StartupReport.cs`
- Modify: `src/Naudit.Web/Program.cs` (direkt nach `var app = builder.Build();`, heute Zeile 229)
- Modify: `docs/deployment.md`
- Test: `tests/Naudit.Tests/StartupReportTests.cs`

**Interfaces:**
- Consumes: `BuildLines` (Task 2), `BuildWarnings` (Task 3).
- Produces: `public static void StartupReport.Log(ILogger logger, IConfiguration config, SetupStatusResult setup, string? recoveryError)`.

- [ ] **Step 1: Write the failing tests**

An `tests/Naudit.Tests/StartupReportTests.cs` anhängen. Der Fake-Logger sammelt die Aufrufe, damit sowohl die Level-Zuordnung als auch das Fail-Safe-Verhalten prüfbar sind:

```csharp
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
```

Ergänze oben in der Datei `using Microsoft.Extensions.Logging;`.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter "FullyQualifiedName~StartupReportTests"`
Expected: Compile-Fehler `CS0117: 'StartupReport' enthält keine Definition für 'Log'`.

- [ ] **Step 3: Implement `Log`**

In `src/Naudit.Web/StartupReport.cs` unter `BuildWarnings` einfügen (und `using Microsoft.Extensions.Logging;` ergänzen, falls die impliziten Usings des Web-SDK es nicht schon abdecken):

```csharp
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
```

- [ ] **Step 4: Wire it into `Program.cs`**

In `src/Naudit.Web/Program.cs` direkt nach `var app = builder.Build();` einfügen:

```csharp
    // Konfigurations-Überblick ins Log — noch vor den Kestrel-Zeilen, und bei JEDEM Durchlauf der
    // Hostschleife: nach einem Settings-Restart zeigt der Block die dann geltenden Werte.
    StartupReport.Log(app.Logger, builder.Configuration, setup, configError?.Message);
```

- [ ] **Step 5: Run the full suite**

Run: `dotnet test Naudit.slnx`
Expected: PASS — insbesondere die vorhandenen `WebApplicationFactory`-Tests, die jetzt durch die neue Zeile laufen.

- [ ] **Step 6: Manuelle Sichtprüfung**

```bash
NAUDIT_TMP=$(mktemp -d)
Naudit__Db__ConnectionString="Data Source=$NAUDIT_TMP/naudit.db" \
Naudit__Sast__Enabled=true \
Naudit__Review__Dast__Enabled=true \
timeout 25 dotnet run --project src/Naudit.Web 2>&1 | head -40
rm -rf "$NAUDIT_TMP"
```

Expected: Der Block erscheint vor den Kestrel-Zeilen; `SAST: AN · opengrep, trivy`; `DAST: AN · Allowlist: (leer)` gefolgt von der DAST-Warnung. Ohne konfigurierte Plattform-Secrets steht `Modus: SETUP …` plus die Liste der fehlenden Schlüssel — das ist korrekt und beweist, dass der Report auch im Setup-Modus trägt.

- [ ] **Step 7: Doku ergänzen**

In `docs/deployment.md` einen Abschnitt anhängen:

````markdown
## Startup-Report

Beim Hochfahren — und nach jedem Settings-Restart erneut — loggt Naudit einen
kompakten Block mit der effektiv geladenen Konfiguration:

```
════════════════════════════════════════════════
  Naudit v0.4.2
  Modus:      Review aktiv
  Plattform:  GitHub · Auth: App · PostVerdict: aus
  AI:         Anthropic · claude-opus-5 · Routing: Single · Sandbox: None · MCP: aus · Logging: aus
  SAST:       AN · opengrep, trivy
  DAST:       aus
  Prompt:     Kontext AN · Memory AN (max 50) · Guidelines AN · Redaction AN
  Review:     Gate ab High/Medium · MaxRoundtrips 3 · Resolution AN
  Zugang:     AccessGate Open · DB Sqlite
════════════════════════════════════════════════
```

Das ist der schnellste Weg zu prüfen, ob eine Settings-Änderung angekommen ist.
Im Setup-Modus steht statt `Review aktiv` ein `SETUP …` samt der noch fehlenden
Schlüssel, im Recovery-Modus `RECOVERY …` samt Fehlermeldung.

Zusätzlich erscheinen Warnzeilen für gültige, aber wirkungslose Kombinationen —
etwa DAST aktiviert bei leerer `Naudit:Review:Dast:Projects`-Allowlist (dann wird
kein Projekt dynamisch getestet).

Der Block enthält **keine Secrets** — nur Schalter, Namen und Zahlen.
````

- [ ] **Step 8: Commit**

```bash
git add src/Naudit.Web/StartupReport.cs src/Naudit.Web/Program.cs tests/Naudit.Tests/StartupReportTests.cs docs/deployment.md
git commit -m "feat(web): Konfigurations-Ueberblick beim Start loggen"
```

---

### Task 5: Versions-Stamping in Build und Release

Ohne diesen Schritt zeigt die Kopfzeile immer `v1.0.0 (dev)` — die berechnete SemVer landet heute nur im Image-Tag und im Git-Tag, nie in der Assembly.

**Files:**
- Modify: `Dockerfile:17`
- Modify: `.github/workflows/release.yml` (Schritte „Determine version", `docker/build-push-action`, „Publish self-contained binaries")

**Interfaces:**
- Consumes: `StartupReport.Version()` liest `AssemblyInformationalVersionAttribute` (Task 2).
- Produces: keine Code-Schnittstelle.

- [ ] **Step 1: `ARG VERSION` im Dockerfile**

In `Dockerfile` die Publish-Zeile der Build-Stage (heute Zeile 17) ersetzen:

```dockerfile
# Restlichen Quellcode kopieren und Release publishen (zieht Infrastructure+Core mit).
# VERSION reicht release.yml durch, damit der Startup-Report die echte Release-Version zeigt;
# lokal bleibt der Default und der Report kennzeichnet den Lauf als (dev).
ARG VERSION=0.0.0
COPY src/ src/
RUN dotnet publish src/Naudit.Web/Naudit.Web.csproj -c Release -o /app/publish --no-restore /p:Version=${VERSION}
```

- [ ] **Step 2: SemVer ohne `v`-Präfix im Workflow bereitstellen**

In `.github/workflows/release.yml`, Schritt „Determine version", nach der `short_sha`-Zeile ergänzen:

```yaml
          # /p:Version verlangt reines SemVer — das Tag-Praefix "v" muss weg.
          echo "semver=${version#v}" >> "$GITHUB_OUTPUT"
```

- [ ] **Step 3: Build-Arg an den Image-Build reichen**

Im `docker/build-push-action`-Schritt unter `file: ./Dockerfile` ergänzen:

```yaml
          build-args: |
            VERSION=${{ steps.version.outputs.semver }}
```

- [ ] **Step 4: Binaries ebenfalls stempeln**

Im Schritt „Publish self-contained binaries" die `dotnet publish`-Zeile erweitern:

```bash
            dotnet publish src/Naudit.Web/Naudit.Web.csproj \
              -c Release -r "$rid" --self-contained true \
              -p:PublishSingleFile=true -p:DebugType=none -p:DebugSymbols=false \
              -p:Version="${version#v}" \
              -o "$out"
```

- [ ] **Step 5: Stamping lokal verifizieren**

```bash
dotnet publish src/Naudit.Web/Naudit.Web.csproj -c Release -o /tmp/naudit-vtest /p:Version=9.9.9 >/dev/null
strings /tmp/naudit-vtest/Naudit.Web.dll | grep -c "9\.9\.9"
rm -rf /tmp/naudit-vtest
```

Expected: Ausgabe ≥ 1 (die Version steckt im Assembly-Attribut).

- [ ] **Step 6: Workflow-Syntax prüfen**

Run: `python3 -c "import yaml,sys; yaml.safe_load(open('.github/workflows/release.yml')); print('ok')"`
Expected: `ok`.

- [ ] **Step 7: Commit**

```bash
git add Dockerfile .github/workflows/release.yml
git commit -m "build: Release-Version in die Assembly stempeln (Startup-Report)"
```

---

## Verifikation zum Schluss

- [ ] `dotnet build Naudit.slnx` — keine Warnungen aus den neuen Dateien
- [ ] `dotnet test Naudit.slnx` — vollständige Suite grün
- [ ] Manuelle Sichtprüfung aus Task 4, Step 6 einmal mit `Naudit__Git__Platform=GitHub` und einmal ohne, um die Plattformzeile in beiden Varianten zu sehen

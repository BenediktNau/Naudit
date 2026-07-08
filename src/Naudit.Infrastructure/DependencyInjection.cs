using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Naudit.Core.Abstractions;
using Naudit.Core.Review;
using Naudit.Infrastructure.Ai;
using Naudit.Infrastructure.Context;
using Naudit.Infrastructure.Data;
using Naudit.Infrastructure.Git;
using Naudit.Infrastructure.Git.GitHub;
using Naudit.Infrastructure.Git.GitLab;
using Naudit.Infrastructure.Process;
using Naudit.Infrastructure.Redaction;
using Naudit.Infrastructure.Sast;
using Naudit.Infrastructure.Ui;

namespace Naudit.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddNauditInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // AI-Provider: aus Config gewählt, hinter IChatClient (austauschbar via appsettings).
        var aiOptions = configuration.GetSection("Naudit:Ai").Get<AiOptions>() ?? new AiOptions();
        services.AddSingleton<IChatClient>(_ => AiClientFactory.Create(aiOptions));
        services.AddSingleton(aiOptions); // für die read-only Settings-Anzeige im WebUI

        // Review-Prompt: leerer Config-Wert -> Default-Prompt.
        var reviewOptions = configuration.GetSection("Naudit:Review").Get<ReviewOptions>() ?? new ReviewOptions();
        if (string.IsNullOrWhiteSpace(reviewOptions.SystemPrompt))
            reviewOptions.SystemPrompt = PromptBuilder.DefaultSystemPrompt;
        services.AddSingleton(reviewOptions);

        // Git-Plattform: eine pro Deployment, per Config gewählt (analog zum AI-Provider).
        var gitOptions = configuration.GetSection("Naudit:Git").Get<GitOptions>() ?? new GitOptions();
        services.AddSingleton(gitOptions);

        switch (gitOptions.Platform)
        {
            case GitPlatformKind.GitHub:
                services.Configure<GitHubOptions>(configuration.GetSection("Naudit:GitHub"));
                // Per-Projekt-Token-Auflösung (Override je Projekt, sonst globaler Token), aus der aktiven Section geseedet.
                var gitHubOptions = configuration.GetSection("Naudit:GitHub").Get<GitHubOptions>() ?? new GitHubOptions();
                if (gitHubOptions.Auth == GitHubAuthKind.App)
                {
                    // Fail-fast beim Start statt kryptischem Fehler beim ersten Review.
                    if (string.IsNullOrWhiteSpace(gitHubOptions.App.AppId) || string.IsNullOrWhiteSpace(gitHubOptions.App.PrivateKey))
                        throw new InvalidOperationException("Naudit:GitHub:Auth=App verlangt Naudit:GitHub:App:AppId und Naudit:GitHub:App:PrivateKey.");

                    // Eigener named Client fürs Token-Minting (JWT-Auth pro Request; gleiche Basis-Header wie die API).
                    // Bewusst Singleton-Provider mit einmal erzeugtem Client: Minting ist selten (~1×/h), Ziel-Host fix.
                    services.AddHttpClient("github-app", http => ConfigureGitHubClient(http, gitHubOptions.BaseUrl));
                    services.AddSingleton<IGitTokenProvider>(sp => new GitHubAppTokenProvider(
                        sp.GetRequiredService<IHttpClientFactory>().CreateClient("github-app"),
                        gitHubOptions.App,
                        sp.GetRequiredService<ILoggerFactory>().CreateLogger<GitHubAppTokenProvider>()));
                }
                else
                {
                    services.AddSingleton<IGitTokenProvider>(new ConfiguredGitTokenProvider(gitHubOptions.Token, gitHubOptions.ProjectTokens));
                }
                services.AddHttpClient<IGitPlatform, GitHubPlatform>((sp, http) =>
                {
                    var opt = sp.GetRequiredService<IOptions<GitHubOptions>>().Value;
                    // Auth wird pro Request in GitHubPlatform gesetzt (Per-Projekt-Token), nicht als Default-Header.
                    ConfigureGitHubClient(http, opt.BaseUrl);
                });
                break;

            default: // GitPlatformKind.GitLab
                services.Configure<GitLabOptions>(configuration.GetSection("Naudit:GitLab"));
                var gitLabOptions = configuration.GetSection("Naudit:GitLab").Get<GitLabOptions>() ?? new GitLabOptions();
                services.AddSingleton<IGitTokenProvider>(new ConfiguredGitTokenProvider(gitLabOptions.Token, gitLabOptions.ProjectTokens));
                services.AddHttpClient<IGitPlatform, GitLabPlatform>((sp, http) =>
                {
                    var opt = sp.GetRequiredService<IOptions<GitLabOptions>>().Value;
                    http.BaseAddress = new Uri(opt.BaseUrl.TrimEnd('/') + "/");
                    // Auth (PRIVATE-TOKEN) wird pro Request in GitLabPlatform gesetzt (Per-Projekt-Token).
                });
                break;
        }

        // SAST/SCA-Grounding: immer die Infrastruktur-Naht registrieren (harmlos wenn ungenutzt),
        // Analyzer nur bei Enabled. Ohne Analyzer verhält sich ReviewService exakt diff-only.
        var sastOptions = configuration.GetSection("Naudit:Sast").Get<SastOptions>() ?? new SastOptions();
        if (sastOptions.Analyzers.Count == 0)
            sastOptions.Analyzers = new() { "opengrep", "trivy" };
        // Voller gepinnter Regelbaum (alle Sprachen) + Overlay laufen IMMER; konfigurierte Pfade
        // kommen additiv dazu. So fällt das Overlay nie versehentlich weg, wenn jemand einen
        // eigenen Regelpfad ergänzt (statt die Defaults still zu ersetzen).
        sastOptions.OpengrepRules = SastOptions.ResolveOpengrepRules(sastOptions.OpengrepRules);
        services.AddSingleton<IProcessRunner, SystemProcessRunner>();
        services.AddSingleton<IFindingReducer>(_ => new DeterministicFindingReducer(sastOptions.MaxFindingsPerGroup));
        services.AddScoped<IWorkspaceProvider, GitWorkspaceProvider>();

        // Kontext-Anreicherung: aus demselben Checkout wie SAST, gesteuert über reviewOptions.Context.
        services.AddScoped<IContextCollector>(_ => new WorkspaceContextCollector(reviewOptions.Context));

        if (sastOptions.Enabled)
        {
            foreach (var name in sastOptions.Analyzers)
            {
                switch (name.ToLowerInvariant())
                {
                    case "opengrep":
                        services.AddScoped<ISastAnalyzer>(sp => new OpengrepAnalyzer(
                            sp.GetRequiredService<IProcessRunner>(),
                            sp.GetRequiredService<ILoggerFactory>().CreateLogger<OpengrepAnalyzer>(),
                            sastOptions.AnalyzerTimeout,
                            sastOptions.OpengrepRules));
                        break;
                    case "betterleaks":
                        services.AddScoped<ISastAnalyzer>(sp => new BetterleaksAnalyzer(
                            sp.GetRequiredService<IProcessRunner>(),
                            sp.GetRequiredService<ILoggerFactory>().CreateLogger<BetterleaksAnalyzer>(),
                            sastOptions.AnalyzerTimeout));
                        break;
                    case "osv-scanner":
                        services.AddScoped<ISastAnalyzer>(sp => new OsvScannerAnalyzer(
                            sp.GetRequiredService<IProcessRunner>(),
                            sp.GetRequiredService<ILoggerFactory>().CreateLogger<OsvScannerAnalyzer>(),
                            sastOptions.AnalyzerTimeout));
                        break;
                    case "trivy":
                        services.AddScoped<ISastAnalyzer>(sp => new TrivyAnalyzer(
                            sp.GetRequiredService<IProcessRunner>(),
                            sp.GetRequiredService<ILoggerFactory>().CreateLogger<TrivyAnalyzer>(),
                            sastOptions.AnalyzerTimeout));
                        break;
                    case "dotnet-sca":
                        services.AddScoped<ISastAnalyzer>(sp => new DotnetScaAnalyzer(
                            sp.GetRequiredService<IProcessRunner>(),
                            sp.GetRequiredService<ILoggerFactory>().CreateLogger<DotnetScaAnalyzer>(),
                            sastOptions.AnalyzerTimeout));
                        break;
                    default:
                        throw new InvalidOperationException($"Unbekannter SAST-Analyzer in Naudit:Sast:Analyzers: '{name}'.");
                }
            }
        }

        // Prompt-Redaction: Secrets/IPs/E-Mails maskieren, bevor der Prompt das LLM erreicht.
        // Default AN (Section fehlt ⇒ Enabled=true); aus ⇒ NullPromptRedactor = heutiges Verhalten.
        var redactionOptions = configuration.GetSection("Naudit:Redaction").Get<RedactionOptions>() ?? new RedactionOptions();
        services.AddSingleton<IPromptRedactor>(redactionOptions.Enabled
            ? new PatternRedactor(redactionOptions)
            : new NullPromptRedactor());

        // Persistenz (Naudit:Db): eigenständiger Belang — DbContext + Zugangsschranke + Audit-Sink
        // nur bei Enabled, sonst No-Ops (= Verhalten ohne DB, keine DB-Datei nötig).
        // Beide Options immer registrieren, damit Program.cs/Endpoints sie lesen können.
        var dbOptions = configuration.GetSection("Naudit:Db").Get<DatabaseOptions>() ?? new DatabaseOptions();
        services.AddSingleton(dbOptions);
        var uiOptions = configuration.GetSection("Naudit:Ui").Get<UiOptions>() ?? new UiOptions();
        services.AddSingleton(uiOptions);

        // UI ⇒ DB: ohne DbContext gäbe es erst beim ersten Request kryptische DI-Fehler —
        // lieber sofort beim Start scheitern (gleiches Muster wie Auth=App ohne AppId).
        if (uiOptions.Enabled && !dbOptions.Enabled)
            throw new InvalidOperationException(
                "Naudit:Ui:Enabled=true verlangt Naudit:Db:Enabled=true (die UI braucht Naudits Datenbank).");

        if (dbOptions.Enabled)
        {
            // Backend per Config; dieselbe (provider-neutrale) Migrationskette läuft auf beiden.
            services.AddDbContext<NauditDbContext>(o =>
            {
                switch (dbOptions.Provider)
                {
                    case DbProvider.Postgres:
                        o.UseNpgsql(dbOptions.ConnectionString);
                        // Der committete Model-Snapshot ist SQLite-geprägt (Migrations werden gegen
                        // SQLite geschrieben); auf Postgres zeigt EFs Pending-Changes-Prüfung deshalb
                        // einen gutartigen, konventionsbedingten Diff (Identity-Strategie). Nur hier
                        // unterdrücken — auf SQLite bleibt die Warnung als „Migration vergessen?"-Netz aktiv.
                        o.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
                        break;
                    default:
                        o.UseSqlite(dbOptions.ConnectionString);
                        break;
                }
            });
            services.AddScoped<IAccessGate, EfAccessGate>();
            services.AddScoped<IReviewAuditSink, EfReviewAuditSink>();
        }
        else
        {
            services.AddSingleton<IAccessGate>(new AllowAllAccessGate());
            services.AddSingleton<IReviewAuditSink>(new NullReviewAuditSink());
        }

        // WebUI (Naudit:Ui): nur Accounts/Dashboard-Belange — braucht die DB (UI ⇒ DB).
        if (uiOptions.Enabled)
        {
            services.AddScoped<AccountService>();
        }

        services.AddScoped<ReviewService>();
        return services;
    }

    /// <summary>Gemeinsame GitHub-Client-Basis (API-Client und App-Token-Minting): BaseAddress +
    /// Pflicht-Header an genau einer Stelle, damit z. B. ein API-Versions-Bump nicht auseinanderläuft.</summary>
    private static void ConfigureGitHubClient(HttpClient http, string baseUrl)
    {
        http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Naudit"); // GitHub verlangt einen User-Agent
    }
}

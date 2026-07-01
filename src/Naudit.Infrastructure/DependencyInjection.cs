using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Naudit.Core.Abstractions;
using Naudit.Core.Review;
using Naudit.Infrastructure.Ai;
using Naudit.Infrastructure.Git;
using Naudit.Infrastructure.Git.GitHub;
using Naudit.Infrastructure.Git.GitLab;
using Naudit.Infrastructure.Process;
using Naudit.Infrastructure.Redaction;
using Naudit.Infrastructure.Sast;

namespace Naudit.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddNauditInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // AI-Provider: aus Config gewählt, hinter IChatClient (austauschbar via appsettings).
        var aiOptions = configuration.GetSection("Naudit:Ai").Get<AiOptions>() ?? new AiOptions();
        services.AddSingleton<IChatClient>(_ => AiClientFactory.Create(aiOptions));

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
                services.AddSingleton<IGitTokenProvider>(new ConfiguredGitTokenProvider(gitHubOptions.Token, gitHubOptions.ProjectTokens));
                services.AddHttpClient<IGitPlatform, GitHubPlatform>((sp, http) =>
                {
                    var opt = sp.GetRequiredService<IOptions<GitHubOptions>>().Value;
                    http.BaseAddress = new Uri(opt.BaseUrl.TrimEnd('/') + "/");
                    // Auth wird pro Request in GitHubPlatform gesetzt (Per-Projekt-Token), nicht als Default-Header.
                    http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
                    http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("Naudit"); // GitHub verlangt einen User-Agent
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

        services.AddScoped<ReviewService>();
        return services;
    }
}

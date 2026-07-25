using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Naudit.Core.Abstractions;
using Naudit.Core.Review;
using Naudit.Infrastructure.Ai;
using Naudit.Infrastructure.Ai.ClaudeCode;
using Naudit.Infrastructure.Ai.Sandbox;
using Naudit.Infrastructure.Context;
using Naudit.Infrastructure.Dast;
using Naudit.Infrastructure.Data;
using Naudit.Infrastructure.Docker;
using Naudit.Infrastructure.Git;
using Naudit.Infrastructure.Git.GitHub;
using Naudit.Infrastructure.Git.GitLab;
using Naudit.Infrastructure.Guidelines;
using Naudit.Infrastructure.Mcp;
using Naudit.Infrastructure.Memory;
using Naudit.Infrastructure.Process;
using Naudit.Infrastructure.Redaction;
using Naudit.Infrastructure.Sast;
using Naudit.Infrastructure.Ui;

namespace Naudit.Infrastructure;

public static class DependencyInjection
{
    /// <summary>DB-Basis (immer an): Options, DbContext, Settings- und Account-Service.
    /// Getrennt von AddNauditInfrastructure, damit der Recovery-Modus (kaputte Review-Config)
    /// die DB/UI-Basis trotzdem bekommt.</summary>
    public static IServiceCollection AddNauditDatabase(this IServiceCollection services, IConfiguration configuration)
    {
        var dbOptions = configuration.GetSection("Naudit:Db").Get<DatabaseOptions>() ?? new DatabaseOptions();
        services.AddSingleton(dbOptions);
        services.AddDbContext<NauditDbContext>(o => DatabaseOptions.ConfigureDbContext(o, dbOptions));
        services.AddScoped<Settings.SettingsService>();
        services.AddScoped<AccountService>();
        services.AddScoped<Ui.ClaudeSessionService>();
        services.AddScoped<Setup.SetupDraftService>();
        return services;
    }

    public static IServiceCollection AddNauditInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // AI-Provider: aus Config gewählt, hinter IChatClient (austauschbar via appsettings).
        var aiOptions = configuration.GetSection("Naudit:Ai").Get<AiOptions>() ?? new AiOptions();

        // MCP-Runtime-Config (Naudit:Review:Mcp). Vor der IChatClient-Registrierung binden, damit der
        // Client-Wrap + der ClaudeCode-CLI-Pfad sie teilen. Singleton für die Review-Pipeline.
        var mcpOptions = configuration.GetSection("Naudit:Review:Mcp").Get<McpOptions>() ?? new McpOptions();
        services.AddSingleton(mcpOptions);
        // Eine Bedingung, zweimal gebraucht (Client-Wrap + Tool-Provider-Registrierung) — als lokale
        // Variable extrahiert, damit die beiden Stellen nie auseinanderlaufen können.
        var mcpForMeaiProvider = mcpOptions.Enabled && aiOptions.Provider != AiProvider.ClaudeCode;

        // Global-Client. Bei aktivem MCP + MEAI-Provider mit Function-Invocation-Loop umhüllen
        // (Cap = MaxIterations). ClaudeCode ist ein eigener IChatClient (CLI-natives MCP) und wird NICHT umhüllt.
        services.AddSingleton<IChatClient>(sp =>
        {
            var client = AiClientFactory.Create(aiOptions, mcpOptions);
            if (mcpForMeaiProvider)
                client = client.AsBuilder()
                    .UseFunctionInvocation(sp.GetService<ILoggerFactory>(),
                        // Untergrenze 1: 0/negativ in der Config würde den Tool-Loop komplett
                        // abschalten bzw. die MEAI-Middleware mit einem ungültigen Wert brechen.
                        c => c.MaximumIterationsPerRequest = Math.Max(1, mcpOptions.MaxIterations))
                    .Build();
            return client;
        });
        services.AddSingleton(aiOptions); // effektive AI-Config für DI (Review-Pipeline; AiClientFactory oben)

        // MCP-Tools: MEAI-Provider + MCP an ⇒ echte MCP-Tools (Function-Invocation nutzt ChatOptions.Tools);
        // sonst No-Op (MCP aus, oder ClaudeCode ⇒ CLI-natives MCP über --mcp-config).
        if (mcpForMeaiProvider)
        {
            services.AddSingleton<IMcpToolConnector>(sp => new McpClientToolConnector(sp.GetRequiredService<ILoggerFactory>()));
            services.AddSingleton<IReviewToolProvider>(sp => new McpReviewToolProvider(
                mcpOptions,
                sp.GetRequiredService<IMcpToolConnector>(),
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<McpReviewToolProvider>()));
        }
        else
        {
            services.AddSingleton<IReviewToolProvider>(new NullReviewToolProvider());
        }

        // Autor-Sessions: Optionen + Cooldown-Registry (Registry auch bei SessionRouting=Single harmlos —
        // die Profil-API zeigt darüber den Cooldown-Status an).
        var authorSessions = configuration.GetSection("Naudit:Ai:AuthorSessions").Get<AuthorSessionsOptions>() ?? new AuthorSessionsOptions();
        services.AddSingleton(authorSessions);
        services.AddSingleton<SessionHealthRegistry>();
        services.AddSingleton<RoundRobinCursor>();

        // DAST (dynamische Prüfung an der laufenden App): eigener Kill-Switch, bewusst unabhängig
        // von der Session-Sandbox — andere Risikoklasse (fremder PR-Code statt eigener Abo-Container).
        var dastOptions = configuration.GetSection("Naudit:Review:Dast").Get<DastOptions>() ?? new DastOptions();
        services.AddSingleton(dastOptions);

        // Session-Sandbox (containerisierte Author/RoundRobin-Sessions): Default None = heutiger
        // In-Process-Runner. Docker ⇒ account-gebundene Runner über den Host-Docker-Socket; jeder
        // Fehlerpfad fällt auf den In-Process-Runner zurück (ein Review scheitert nie an der Sandbox).
        var sandboxOptions = configuration.GetSection("Naudit:Ai:Sandbox").Get<SessionSandboxOptions>()
            ?? new SessionSandboxOptions();
        services.AddSingleton(sandboxOptions);
        services.AddSingleton<SessionSandboxState>();

        // Ein Docker-Client für beide Nutzer (Session-Sandbox und DAST); ist die Sandbox aktiv,
        // gewinnt ihr Socket-Pfad.
        if (aiOptions.SessionSandbox == SessionSandbox.Docker || dastOptions.Enabled)
        {
            var socketPath = aiOptions.SessionSandbox == SessionSandbox.Docker
                ? sandboxOptions.DockerSocketPath
                : dastOptions.DockerSocketPath;
            services.AddSingleton<IDockerClient>(_ => new SocketDockerClient(socketPath));
        }

        if (aiOptions.SessionSandbox == SessionSandbox.Docker)
        {
            services.AddSingleton(sp => new SessionContainerManager(
                sp.GetRequiredService<IDockerClient>(), sandboxOptions,
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<SessionContainerManager>()));
            services.AddSingleton<ISessionRunnerFactory, DockerSessionRunnerFactory>();
            services.AddHostedService<SandboxSweeperService>();
        }
        else
        {
            services.AddSingleton<ISessionRunnerFactory>(sp =>
                new InProcessSessionRunnerFactory(sp.GetRequiredService<IProcessRunner>()));
        }

        if (dastOptions.Enabled)
        {
            // Kein HttpClient: der Healthcheck läuft als docker exec im Probe-Container —
            // Naudit spricht nie selbst HTTP mit der getesteten App.
            services.AddSingleton<IAppRunner>(sp => new DockerAppRunner(
                sp.GetRequiredService<IDockerClient>(),
                dastOptions,
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<DockerAppRunner>()));
            services.AddHostedService<DastOrphanSweeper>();
        }

        services.AddSingleton<SessionSelectionFactory>();

        // Router-Naht: 3 Modi. Single = globaler Client (heutiges Verhalten); Author/RoundRobin
        // sind scoped (brauchen ClaudeSessionService/DbContext).
        switch (aiOptions.SessionRouting)
        {
            case SessionRouting.Author:
                services.AddScoped<IAiClientRouter, AuthorSessionRouter>();
                break;
            case SessionRouting.RoundRobin:
                services.AddScoped<IAiClientRouter, RoundRobinSessionRouter>();
                break;
            default:
                services.AddSingleton<IAiClientRouter>(sp => new SingleClientRouter(sp.GetRequiredService<IChatClient>()));
                break;
        }

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

                    // Installations-Checker fürs WebUI-Onboarding: eigener App-JWT (gleiche Basis wie
                    // der Token-Provider), gleicher named Client. Singleton, weil Slug/Ergebnisse gecached werden.
                    var appJwt = new GitHubAppJwt(gitHubOptions.App.AppId, gitHubOptions.App.PrivateKey);
                    services.AddSingleton(appJwt);
                    services.AddSingleton<IGitHubAppInstallationChecker>(sp => new GitHubAppInstallationChecker(
                        sp.GetRequiredService<IHttpClientFactory>().CreateClient("github-app"),
                        appJwt,
                        sp.GetRequiredService<ILoggerFactory>().CreateLogger<GitHubAppInstallationChecker>()));
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
                // FP-Antwort-Kommando: eigener typed Client (gleiche Basis-Header wie die API),
                // Auth pro Request in der Impl.
                services.AddHttpClient<IReviewCommentResponder, GitHubCommentResponder>((sp, http) =>
                    ConfigureGitHubClient(http, sp.GetRequiredService<IOptions<GitHubOptions>>().Value.BaseUrl));

                // Autor-Session-Routing: der Login steht auf GitHub schon im Request (Webhook-Mapping).
                services.AddSingleton<IAuthorLoginResolver>(new PassthroughAuthorLoginResolver());
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
                // Autor-Auflösung braucht denselben Host wie die GitLab-API (eigener typed Client).
                services.AddHttpClient<IAuthorLoginResolver, GitLabAuthorLoginResolver>((sp, http) =>
                {
                    var opt = sp.GetRequiredService<IOptions<GitLabOptions>>().Value;
                    http.BaseAddress = new Uri(opt.BaseUrl.TrimEnd('/') + "/");
                });

                // FP-Antwort-Kommando: eigener typed Client auf denselben GitLab-Host.
                services.AddHttpClient<IReviewCommentResponder, GitLabCommentResponder>((sp, http) =>
                {
                    var opt = sp.GetRequiredService<IOptions<GitLabOptions>>().Value;
                    http.BaseAddress = new Uri(opt.BaseUrl.TrimEnd('/') + "/");
                });
                break;
        }

        // FP-Antwort-Kommando-Orchestrator (scoped — nutzt DbContext + den plattform-spezifischen Responder).
        services.AddScoped<Naudit.Infrastructure.Memory.ReviewCommentCommandService>();

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

        // Projekt-Gedächtnis: FPs + Konventionen als Prompt-Guidance. Default AN;
        // aus ⇒ NullReviewMemory (leere Liste) = exakt heutiges Verhalten.
        if (reviewOptions.Memory.Enabled)
            services.AddScoped<IReviewMemory, DbReviewMemory>();
        else
            services.AddSingleton<IReviewMemory, NullReviewMemory>();

        // Architektur-Profil: destillierte Guidelines aus der Repo-Doku (Naudit:Review:Guidelines).
        // Aus ⇒ NullReviewGuidelines (immer null) = heutiges Prompt-Verhalten.
        if (reviewOptions.Guidelines.Enabled)
            services.AddScoped<IReviewGuidelines, DistillingReviewGuidelines>();
        else
            services.AddSingleton<IReviewGuidelines, NullReviewGuidelines>();

        var uiOptions = configuration.GetSection("Naudit:Ui").Get<UiOptions>() ?? new UiOptions();
        services.AddSingleton(uiOptions);

        // Zugangsschranke: explizite Betriebsart statt (wie früher) implizit an der DB zu hängen.
        var gateOptions = configuration.GetSection("Naudit:AccessGate").Get<AccessGateOptions>() ?? new AccessGateOptions();
        services.AddSingleton(gateOptions);
        if (gateOptions.Mode == AccessGateMode.Registered)
            services.AddScoped<IAccessGate, EfAccessGate>();
        else
            services.AddSingleton<IAccessGate>(new AllowAllAccessGate());
        services.AddScoped<IReviewAuditSink, EfReviewAuditSink>();
        services.AddScoped<IReviewRoundtripCounter, EfReviewRoundtripCounter>();

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

using System.Net.Http.Headers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Naudit.Core.Abstractions;
using Naudit.Core.Review;
using Naudit.Infrastructure.Ai;
using Naudit.Infrastructure.Git;
using Naudit.Infrastructure.Git.GitHub;
using Naudit.Infrastructure.Git.GitLab;

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
                services.AddHttpClient<IGitPlatform, GitHubPlatform>((sp, http) =>
                {
                    var opt = sp.GetRequiredService<IOptions<GitHubOptions>>().Value;
                    http.BaseAddress = new Uri(opt.BaseUrl.TrimEnd('/') + "/");
                    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", opt.Token);
                    http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
                    http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("Naudit"); // GitHub verlangt einen User-Agent
                });
                break;

            default: // GitPlatformKind.GitLab
                services.Configure<GitLabOptions>(configuration.GetSection("Naudit:GitLab"));
                services.AddHttpClient<IGitPlatform, GitLabPlatform>((sp, http) =>
                {
                    var opt = sp.GetRequiredService<IOptions<GitLabOptions>>().Value;
                    http.BaseAddress = new Uri(opt.BaseUrl.TrimEnd('/') + "/");
                    http.DefaultRequestHeaders.Add("PRIVATE-TOKEN", opt.Token);
                });
                break;
        }

        services.AddScoped<ReviewService>();
        return services;
    }
}

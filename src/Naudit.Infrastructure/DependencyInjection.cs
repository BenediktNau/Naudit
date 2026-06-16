using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Naudit.Core.Abstractions;
using Naudit.Core.Review;
using Naudit.Infrastructure.Ai;
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

        // GitLab: typed HttpClient mit BaseAddress + Token.
        services.Configure<GitLabOptions>(configuration.GetSection("Naudit:GitLab"));
        services.AddHttpClient<IGitPlatform, GitLabPlatform>((sp, http) =>
        {
            var opt = sp.GetRequiredService<IOptions<GitLabOptions>>().Value;
            http.BaseAddress = new Uri(opt.BaseUrl.TrimEnd('/') + "/");
            http.DefaultRequestHeaders.Add("PRIVATE-TOKEN", opt.Token);
        });

        services.AddScoped<ReviewService>();
        return services;
    }
}

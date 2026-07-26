using Mediator;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Naudit.Infrastructure;
using Naudit.Infrastructure.Ai.ClaudeCode;
using Naudit.Infrastructure.Ai.Logging;
using Xunit;

namespace Naudit.Tests;

/// <summary>Verdrahtung des Prompt-Loggings im echten DI-Graph: Der Mediator-Decorator darf nur
/// bei aktivem Naudit:Ai:Logging um den IChatClient liegen, und die Pipeline (Behavior als
/// Singleton, Sink als Scoped) muss die Scope-Validierung überleben — das deckt kein Unit-Test
/// des Behaviors ab.</summary>
public class PromptLoggingWiringTests
{
    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddNauditDatabase(config);
        services.AddNauditInfrastructure(config);
        // ValidateScopes/OnBuild: fängt ein Singleton, das versehentlich einen scoped Dienst
        // (DbContext/Sink) direkt injiziert — genau das Risiko des Singleton-Behaviors.
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true,
        });
    }

    private static Dictionary<string, string?> BaseSettings() => new()
    {
        ["Naudit:Git:Platform"] = "GitLab",
        ["Naudit:GitLab:BaseUrl"] = "https://gitlab.example.com",
    };

    [Fact]
    public void Logging_ausByDefault_clientBleibtUnumhuellt()
    {
        using var provider = Build(BaseSettings());

        Assert.IsNotType<MediatorChatClient>(provider.GetRequiredService<IChatClient>());
    }

    [Fact]
    public void Logging_an_umhuelltDenGlobalenClient()
    {
        var settings = BaseSettings();
        settings["Naudit:Ai:Logging:Enabled"] = "true";
        using var provider = Build(settings);

        Assert.IsType<MediatorChatClient>(provider.GetRequiredService<IChatClient>());
    }

    /// <summary>Die Pipeline muss auflösbar sein: IMediator, das Behavior und der scoped Sink.
    /// Ein falsch registriertes Behavior (scoped im Singleton-Graph) würde hier scheitern.</summary>
    [Fact]
    public void Logging_an_pipelineIstAuflösbar()
    {
        var settings = BaseSettings();
        settings["Naudit:Ai:Logging:Enabled"] = "true";
        using var provider = Build(settings);

        Assert.NotNull(provider.GetRequiredService<IMediator>());
        Assert.NotNull(provider.GetRequiredService<IPipelineBehavior<ChatCompletionRequest, ChatResponse>>());
        using var scope = provider.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IChatTranscriptSink>());
    }

    /// <summary>Session-Routing baut den Session-Client über die SessionSelectionFactory — sie nimmt
    /// AiLoggingOptions + IMediator im Ctor, muss also im echten Graph auflösbar sein (sonst
    /// scheitert Autor-/Pool-Routing erst zur Laufzeit beim ersten Review).</summary>
    [Fact]
    public void SessionRouting_mitLogging_factoryIstAuflösbar()
    {
        var settings = BaseSettings();
        settings["Naudit:Ai:Logging:Enabled"] = "true";
        settings["Naudit:Ai:SessionRouting"] = "Author";
        using var provider = Build(settings);

        using var scope = provider.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<SessionSelectionFactory>());
    }
}

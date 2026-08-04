using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Naudit.Infrastructure;
using Naudit.Infrastructure.Ai;
using Naudit.Infrastructure.Dast;
using Naudit.Infrastructure.Docker;
using Naudit.Infrastructure.Settings;
using Xunit;

namespace Naudit.Tests;

public class DastWiringTests
{
    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddNauditDatabase(config);
        services.AddNauditInfrastructure(config);
        return services.BuildServiceProvider();
    }

    private static Dictionary<string, string?> BaseSettings() => new()
    {
        ["Naudit:Git:Platform"] = "GitLab",
        ["Naudit:GitLab:BaseUrl"] = "https://gitlab.example.com",
    };

    [Fact]
    public void Dast_disabledByDefault_registersNoAppRunner()
    {
        using var provider = Build(BaseSettings());

        Assert.Null(provider.GetService<IAppRunner>());
    }

    [Fact]
    public void Dast_enabled_registersAppRunner_andOrphanSweeper()
    {
        var settings = BaseSettings();
        settings["Naudit:Review:Dast:Enabled"] = "true";
        using var provider = Build(settings);

        Assert.NotNull(provider.GetService<IAppRunner>());
        Assert.Contains(provider.GetServices<IHostedService>(), s => s is DastOrphanSweeper);
    }

    /// <summary>Sind Session-Sandbox UND DAST gleichzeitig aktiv, teilen sie sich einen
    /// IDockerClient — der Sandbox-Socket-Pfad muss gewinnen (andere Risikoklasse, siehe
    /// docs/dast.md#docker-socket-sharing). Ein invertierter Vorrang würde diesen Test kippen.</summary>
    [Fact]
    public void Dast_andSessionSandbox_bothEnabled_sandboxSocketPathWins()
    {
        var settings = BaseSettings();
        settings["Naudit:Ai:SessionSandbox"] = "Docker";
        settings["Naudit:Ai:Sandbox:DockerSocketPath"] = "/tmp/sandbox-test.sock";
        settings["Naudit:Review:Dast:Enabled"] = "true";
        settings["Naudit:Review:Dast:DockerSocketPath"] = "/tmp/dast-test.sock";
        using var provider = Build(settings);

        var client = provider.GetRequiredService<IDockerClient>();

        Assert.Equal("/tmp/sandbox-test.sock", Assert.IsType<SocketDockerClient>(client).SocketPath);
    }

    [Fact]
    public void Dast_enabled_registersDastAnalyzer_amongSastAnalyzers()
    {
        var settings = BaseSettings();
        settings["Naudit:Review:Dast:Enabled"] = "true";
        using var provider = Build(settings);

        Assert.Contains(provider.GetServices<Naudit.Core.Abstractions.ISastAnalyzer>(),
            a => a.Name == "dast");
    }

    [Fact]
    public void Dast_disabled_registersNoDastAnalyzer()
    {
        using var provider = Build(BaseSettings());

        Assert.DoesNotContain(provider.GetServices<Naudit.Core.Abstractions.ISastAnalyzer>(),
            a => a.Name == "dast");
    }

    private static IConfiguration Config(Dictionary<string, string?> settings)
        => new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    /// <summary>Ohne Naudit:Review:Dast:Ai bleibt das Probing exakt am globalen Provider —
    /// die Sektion darf kein Verhalten aendern, solange sie leer ist.</summary>
    [Fact]
    public void DastAi_unset_isTheGlobalProvider()
    {
        var settings = BaseSettings();
        settings["Naudit:Ai:Provider"] = "Ollama";
        settings["Naudit:Ai:Model"] = "qwen3:14b";
        settings["Naudit:Ai:Endpoint"] = "http://ollama:11434";

        var resolved = DastAiOptions.Resolve(Config(settings));

        Assert.Equal(AiProvider.Ollama, resolved.Provider);
        Assert.Equal("qwen3:14b", resolved.Model);
        Assert.Equal("http://ollama:11434", resolved.Endpoint);
    }

    /// <summary>Gleicher Provider ⇒ nur die gesetzten Felder gewinnen, der Rest wird geerbt
    /// (Anwendungsfall: eigener Key oder groesseres Model fuers Probing).</summary>
    [Fact]
    public void DastAi_sameProvider_inheritsUnsetFields()
    {
        var settings = BaseSettings();
        settings["Naudit:Ai:Provider"] = "Anthropic";
        settings["Naudit:Ai:Model"] = "claude-sonnet-5";
        settings["Naudit:Ai:ApiKey"] = "global-key";
        settings["Naudit:Review:Dast:Ai:ApiKey"] = "dast-key";

        var resolved = DastAiOptions.Resolve(Config(settings));

        Assert.Equal(AiProvider.Anthropic, resolved.Provider);
        Assert.Equal("claude-sonnet-5", resolved.Model);
        Assert.Equal("dast-key", resolved.ApiKey);
    }

    /// <summary>Providerwechsel ⇒ nichts erben. Ein vom CLI-Provider geerbtes Model ("sonnet")
    /// an einer Anthropic-API waere nur ein verwirrender Laufzeitfehler.</summary>
    [Fact]
    public void DastAi_providerSwitch_inheritsNothing()
    {
        var settings = BaseSettings();
        settings["Naudit:Ai:Provider"] = "ClaudeCode";
        settings["Naudit:Ai:Model"] = "sonnet";
        settings["Naudit:Ai:ApiKey"] = "global-oauth-token";
        settings["Naudit:Review:Dast:Ai:Provider"] = "Anthropic";

        var resolved = DastAiOptions.Resolve(Config(settings));

        Assert.Equal(AiProvider.Anthropic, resolved.Provider);
        Assert.Equal("", resolved.Model);
        Assert.Null(resolved.ApiKey);
    }

    /// <summary>Die Sektion ist DB-verwaltet (Settings-Seite) — sonst waere sie auf einer
    /// Instanz mit Config-in-DB gar nicht setzbar. Der ApiKey muss als Secret gefuehrt sein,
    /// sonst stuende er im Klartext in der Settings-Tabelle.</summary>
    [Fact]
    public void DastAi_keysAreInSettingsCatalog_apiKeyAsSecret()
    {
        Assert.True(SettingsCatalog.TryGet("Naudit:Review:Dast:Ai:Provider", out var provider));
        Assert.Contains("ClaudeCode", provider!.AllowedValues!);
        Assert.True(SettingsCatalog.TryGet("Naudit:Review:Dast:Ai:Model", out _));
        Assert.True(SettingsCatalog.TryGet("Naudit:Review:Dast:Ai:Endpoint", out _));
        Assert.True(SettingsCatalog.TryGet("Naudit:Review:Dast:Ai:ApiKey", out var key));
        Assert.True(key!.IsSecret);
    }
}

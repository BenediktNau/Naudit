using Microsoft.Extensions.Configuration;
using Naudit.Infrastructure.Setup;
using Xunit;

namespace Naudit.Tests;

/// <summary>Pflichtset-Logik je Plattform/Provider. Leitprinzip: fehlend ⇒ Setup-Modus,
/// un-parsebare Enums ⇒ keine Aussage (das fängt der Recovery-Probe).</summary>
public sealed class SetupStatusTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value))
            .Build();

    [Fact]
    public void LeereConfig_verlangtGitLabDefaults()
    {
        var result = SetupStatus.Check(Config());
        Assert.True(result.SetupRequired);
        Assert.Contains("Naudit:GitLab:BaseUrl", result.MissingKeys);
        Assert.Contains("Naudit:GitLab:Token", result.MissingKeys);
        Assert.Contains("Naudit:GitLab:WebhookSecret", result.MissingKeys);
        Assert.Contains("Naudit:Ai:Model", result.MissingKeys);
    }

    [Fact]
    public void GitLabKomplett_istKeinSetupFall()
    {
        var result = SetupStatus.Check(Config(
            ("Naudit:GitLab:BaseUrl", "https://gitlab.example.com"),
            ("Naudit:GitLab:Token", "t"),
            ("Naudit:GitLab:WebhookSecret", "s"),
            ("Naudit:Ai:Model", "llama3.1")));
        Assert.False(result.SetupRequired);
        Assert.Empty(result.MissingKeys);
    }

    [Fact]
    public void GitHubPat_verlangtTokenUndSecret()
    {
        var result = SetupStatus.Check(Config(
            ("Naudit:Git:Platform", "GitHub"),
            ("Naudit:Ai:Model", "m")));
        Assert.Contains("Naudit:GitHub:Token", result.MissingKeys);
        Assert.Contains("Naudit:GitHub:WebhookSecret", result.MissingKeys);
        Assert.DoesNotContain("Naudit:GitLab:Token", result.MissingKeys);
    }

    [Fact]
    public void GitHubApp_verlangtAppIdUndPrivateKey_stattToken()
    {
        var result = SetupStatus.Check(Config(
            ("Naudit:Git:Platform", "GitHub"),
            ("Naudit:GitHub:Auth", "App"),
            ("Naudit:GitHub:WebhookSecret", "s"),
            ("Naudit:Ai:Model", "m")));
        Assert.Contains("Naudit:GitHub:App:AppId", result.MissingKeys);
        Assert.Contains("Naudit:GitHub:App:PrivateKey", result.MissingKeys);
        Assert.DoesNotContain("Naudit:GitHub:Token", result.MissingKeys);
    }

    [Fact]
    public void GitHubAppKomplett_istKeinSetupFall()
    {
        var result = SetupStatus.Check(Config(
            ("Naudit:Git:Platform", "GitHub"),
            ("Naudit:GitHub:Auth", "App"),
            ("Naudit:GitHub:App:AppId", "123"),
            ("Naudit:GitHub:App:PrivateKey", "PEM"),
            ("Naudit:GitHub:WebhookSecret", "s"),
            ("Naudit:Ai:Model", "m")));
        Assert.False(result.SetupRequired);
    }

    [Fact]
    public void AnthropicOhneApiKey_fehlt_OllamaOhneEndpointNicht()
    {
        var anthropic = SetupStatus.Check(Config(
            ("Naudit:GitLab:BaseUrl", "b"), ("Naudit:GitLab:Token", "t"), ("Naudit:GitLab:WebhookSecret", "s"),
            ("Naudit:Ai:Provider", "Anthropic"), ("Naudit:Ai:Model", "m")));
        Assert.Contains("Naudit:Ai:ApiKey", anthropic.MissingKeys);

        // Ollama: Endpoint hat einen funktionierenden Default (localhost:11434) — nie Pflicht.
        var ollama = SetupStatus.Check(Config(
            ("Naudit:GitLab:BaseUrl", "b"), ("Naudit:GitLab:Token", "t"), ("Naudit:GitLab:WebhookSecret", "s"),
            ("Naudit:Ai:Provider", "Ollama"), ("Naudit:Ai:Model", "m")));
        Assert.False(ollama.SetupRequired);
    }

    [Fact]
    public void ClaudeCode_brauchtKeinModel()
    {
        var result = SetupStatus.Check(Config(
            ("Naudit:GitLab:BaseUrl", "b"), ("Naudit:GitLab:Token", "t"), ("Naudit:GitLab:WebhookSecret", "s"),
            ("Naudit:Ai:Provider", "ClaudeCode")));
        Assert.False(result.SetupRequired);
    }

    [Fact]
    public void UngueltigeEnums_ergebenKeineFehlendenKeys()
    {
        // Kaputte Enum-Werte sind ein Fall fuer den Recovery-Modus (Probe wirft), nicht fuer den Wizard.
        var result = SetupStatus.Check(Config(
            ("Naudit:Git:Platform", "Bogus"),
            ("Naudit:Ai:Provider", "Bogus")));
        Assert.False(result.SetupRequired);
    }
}

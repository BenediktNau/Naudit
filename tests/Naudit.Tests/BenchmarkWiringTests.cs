using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Naudit.Benchmark;
using Naudit.Core.Abstractions;
using Naudit.Core.Models;
using Naudit.Infrastructure;
using Naudit.Infrastructure.Ai.ClaudeCode;
using Naudit.Infrastructure.Git.GitHub;
using Naudit.Infrastructure.Sast;
using Naudit.Tests.Fakes;

namespace Naudit.Tests;

/// <summary>Der einzige Test zwischen dem Lauf und 50 fremden Pull Requests. Er muss zweierlei
/// belegen: dass in den Dekoratoren die echte, konfigurierte Plattform steckt (sonst liest der
/// Benchmark nichts Echtes) und dass ein PostReviewAsync über den fertig komponierten Container
/// keinen einzigen ausgehenden HTTP-Aufruf erzeugt (sonst schreibt er in fremde PRs).</summary>
public class BenchmarkWiringTests
{
    private static IConfiguration Config() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Naudit:Git:Platform"] = "GitHub",
            ["Naudit:GitHub:Token"] = "test-token",
            ["Naudit:GitHub:WebhookSecret"] = "test-secret",
            ["Naudit:Ai:Provider"] = "ClaudeCode",
            ["Naudit:Ai:Model"] = "opus",
            ["Naudit:Db:ConnectionString"] = "Data Source=:memory:",
        })
        .Build();

    /// <summary>Der komponierte Container wie im Runner. Ist ein Handler übergeben, hängt er als
    /// primärer Handler an ALLEN HttpClients — jeder ausgehende Aufruf wird damit sichtbar.</summary>
    private static ServiceProvider Provider(StubHttpMessageHandler? handler = null)
    {
        var services = new ServiceCollection();
        var config = Config();
        services.AddSingleton(config);
        services.AddNauditDatabase(config);
        services.AddNauditInfrastructure(config);
        services.AddBenchmarkCapture();
        if (handler is not null)
            services.ConfigureHttpClientDefaults(b => b.ConfigurePrimaryHttpMessageHandler(() => handler));
        return services.BuildServiceProvider();
    }

    [Fact]
    public void AddBenchmarkCapture_ersetzt_IGitPlatform_durch_den_Dekorator_um_die_echte_Plattform()
    {
        using var provider = Provider();
        using var scope = provider.CreateScope();

        var platform = scope.ServiceProvider.GetRequiredService<IGitPlatform>();

        var decorator = Assert.IsType<CapturingGitPlatform>(platform);
        // Entscheidend: darin steckt die KONFIGURIERTE Plattform. Ein Dekorator um einen
        // Platzhalter läse nichts Echtes und der Benchmark liefe gegen Luft.
        Assert.IsType<GitHubPlatform>(decorator.Inner);
    }

    [Fact]
    public void AddBenchmarkCapture_ersetzt_IChatClient_durch_den_Dekorator_um_den_echten_Client()
    {
        using var provider = Provider();

        var client = provider.GetRequiredService<IChatClient>();

        var decorator = Assert.IsType<CapturingChatClient>(client);
        Assert.IsType<ClaudeCodeChatClient>(decorator.Inner);
    }

    [Fact]
    public void AddBenchmarkCapture_ersetzt_IWorkspaceProvider_durch_den_Dekorator_um_den_echten_Provider()
    {
        // Diese Registrierung läuft über den konkreten Typ, nicht über eine Fabrik — der
        // Dekorations-Helfer muss beide Formen können.
        using var provider = Provider();
        using var scope = provider.CreateScope();

        var workspaces = scope.ServiceProvider.GetRequiredService<IWorkspaceProvider>();

        var decorator = Assert.IsType<CapturingWorkspaceProvider>(workspaces);
        Assert.IsType<GitWorkspaceProvider>(decorator.Inner);
    }

    [Fact]
    public async Task PostReviewAsync_erzeugt_ueber_den_komponierten_Container_keinen_HTTP_Aufruf()
    {
        // Der Handler antwortet mit 200/leer; hier zählt nur, OB er gerufen wird.
        var spy = new StubHttpMessageHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("[]", System.Text.Encoding.UTF8, "application/json"),
        });
        using var provider = Provider(spy);
        using var scope = provider.CreateScope();
        var platform = scope.ServiceProvider.GetRequiredService<IGitPlatform>();
        var request = new ReviewRequest("getsentry/sentry", 93824, "Titel");

        await platform.PostReviewAsync(request, "Zusammenfassung",
            [new InlineComment("a.cs", 12, null, "Fund", FindingSeverity.High, ReviewConfidence.High)],
            ReviewVerdict.RequestChanges);

        Assert.Empty(spy.Calls);
    }

    [Fact]
    public async Task Ein_Lesezugriff_geht_dagegen_wirklich_hinaus()
    {
        // Gegenprobe zum Test darüber: der Spion IST verdrahtet — ein Post erzeugt nur deshalb
        // keinen Aufruf, weil der Dekorator ihn abfängt, nicht weil hier nichts gemessen würde.
        var spy = new StubHttpMessageHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("[]", System.Text.Encoding.UTF8, "application/json"),
        });
        using var provider = Provider(spy);
        using var scope = provider.CreateScope();
        var platform = scope.ServiceProvider.GetRequiredService<IGitPlatform>();

        await platform.GetChangesAsync(new ReviewRequest("getsentry/sentry", 93824, "Titel"));

        var call = Assert.Single(spy.Calls);
        Assert.Equal(HttpMethod.Get, call.Method);
        Assert.Contains("pulls/93824/files", call.Uri!.ToString());
    }

    [Fact]
    public void AddBenchmarkCapture_registriert_ReviewCapture_als_Singleton()
    {
        using var provider = Provider();

        var a = provider.GetRequiredService<ReviewCapture>();
        var b = provider.GetRequiredService<ReviewCapture>();

        Assert.Same(a, b);
    }
}

using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Naudit.Core.Abstractions;
using Naudit.Core.Models;
using Naudit.Infrastructure.Docker;

namespace Naudit.Infrastructure.Dast;

/// <summary>Dynamische Prüfung als weiterer ISastAnalyzer: baut/startet die PR-App (PR-1-Runner),
/// treibt den Playwright-MCP-Server durch einen begrenzten agentischen Loop und mappt die
/// JSON-Beobachtungen des Modells auf ScanFinding(Category=Dast). Reines Grounding, Verdict bleibt am
/// Gate. Fail-open über alles; garantierter Teardown der DAST-Topologie über RunningApp.</summary>
public sealed class DastAnalyzer(
    IAppRunner runner,
    DastOptions options,
    IChatClient chatClient,
    IDockerClient docker,
    ILoggerFactory loggerFactory,
    IReadOnlyList<AITool>? probeToolsOverride = null) : ISastAnalyzer
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<DastAnalyzer>();

    public string Name => "dast";

    public async Task<IReadOnlyList<ScanFinding>> AnalyzeAsync(
        IReviewWorkspace workspace, IReadOnlyList<CodeChange> changes, CancellationToken ct = default)
    {
        if (!options.AppliesTo(workspace.ProjectId))
            return [];

        try
        {
            await using var app = await runner.RunAsync(workspace, ct);
            if (app is null) return [];   // nicht anwendbar / kam nicht hoch — Runner hat schon geloggt

            DastProbeSession? session = null;
            try
            {
                IReadOnlyList<AITool> tools;
                if (probeToolsOverride is not null)
                {
                    tools = probeToolsOverride;   // Testnaht: kein echter MCP-Server
                }
                else
                {
                    session = await DastProbeSession.StartAsync(docker, options, app.ProbeContainerName, loggerFactory, ct);
                    tools = session.Tools;
                }

                var raw = await RunProbeLoopAsync(app.InternalUrl, tools, ct);
                return ParseFindings(raw);
            }
            finally
            {
                if (session is not null) await session.DisposeAsync();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // echter Aufrufer-Abbruch propagiert
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DAST-Probing abgebrochen — Review läuft ohne dynamische Funde weiter.");
            return [];
        }
    }

    private async Task<string> RunProbeLoopAsync(string appUrl, IReadOnlyList<AITool> tools, CancellationToken ct)
    {
        var client = tools.Count > 0
            ? chatClient.AsBuilder().UseFunctionInvocation(loggerFactory,
                c => c.MaximumIterationsPerRequest = Math.Max(1, options.MaxProbeSteps)).Build()
            : chatClient;
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, DastProbePrompt.System(appUrl, options.MaxProbeSteps)),
            new(ChatRole.User, $"Probe the app at {appUrl} now and return the findings JSON."),
        };
        var chatOptions = new ChatOptions { ResponseFormat = ChatResponseFormat.Json };
        if (tools.Count > 0) chatOptions.Tools = [.. tools];
        var response = await client.GetResponseAsync(messages, chatOptions, ct);
        return response.Text;
    }

    /// <summary>Non-JSON / Schema-Fehler ⇒ leere Liste (Grounding-Schritt, nicht fail-closed).</summary>
    private IReadOnlyList<ScanFinding> ParseFindings(string text)
    {
        try
        {
            var doc = JsonSerializer.Deserialize<ProbeResult>(text, JsonOpts);
            if (doc?.Findings is not { Count: > 0 }) return [];
            return doc.Findings
                .Where(f => f is not null)
                .Select(f => new ScanFinding("dast", FindingCategory.Dast, MapSeverity(f!.Severity),
                    $"{f.Summary} ({f.Endpoint})"))
                .ToList();
        }
        catch (JsonException)
        {
            _logger.LogInformation("DAST: Probing-Antwort war kein gültiges JSON — keine dynamischen Funde.");
            return [];
        }
    }

    private static FindingSeverity MapSeverity(string? s) => s?.ToLowerInvariant() switch
    {
        "high" => FindingSeverity.High,
        "medium" => FindingSeverity.Medium,
        _ => FindingSeverity.Low,
    };

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private sealed record ProbeResult(List<ProbeFinding?>? Findings);
    private sealed record ProbeFinding(string? Severity, string? Endpoint, string? Summary);
}

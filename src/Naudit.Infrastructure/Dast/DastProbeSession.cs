using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Naudit.Infrastructure.Docker;

namespace Naudit.Infrastructure.Dast;

/// <summary>Eine MCP-Sitzung je Review: startet den Playwright-MCP-Server als stdio-Prozess im
/// Probe-Container (docker exec, attached duplex), verbindet einen McpClient über die Stream-Naht und
/// listet die Browser-Tools. Kurzlebig — DisposeAsync schließt Client UND exec-Stream. Anders als der
/// prozesslebenslange McpReviewToolProvider (Review-Tool-Loop) gehört diese Sitzung genau einem Lauf.</summary>
public sealed class DastProbeSession : IAsyncDisposable
{
    // Backstop gegen einen hängenden MCP-Handshake: McpClient.CreateAsync liest den stdio-Server-Output
    // per Pipe und behandelt ein sofortiges EOF (Server antwortet nie/Prozess kam nie hoch) NICHT als
    // Fehler, sondern wartet unbegrenzt weiter — ohne diesen Deckel würde ein toter/unerreichbarer
    // Probe-Container die Review für immer blockieren, statt in den Fail-open-Pfad des Analyzers
    // (Task 5/6) zu laufen. Der Probe-Container läuft bereits (sleep infinity, Image bereits gepullt) —
    // es bleibt nur node-Start + Headless-Chromium-Launch, üblicherweise deutlich unter 10s.
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

    private readonly McpClient _client;
    private readonly DockerExecStream _exec;

    public IReadOnlyList<AITool> Tools { get; }

    private DastProbeSession(McpClient client, DockerExecStream exec, IReadOnlyList<AITool> tools)
    {
        _client = client; _exec = exec; Tools = tools;
    }

    public static async Task<DastProbeSession> StartAsync(IDockerClient docker, DastOptions options,
        string probeContainer, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var exec = await docker.ExecStreamAsync(probeContainer, options.ProbeMcpArgv,
            environment: null, workingDirectory: "/", ct);
        McpClient? client = null;
        try
        {
            using var timeoutCts = new CancellationTokenSource(HandshakeTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            var linkedCt = linkedCts.Token;

            // serverInput = was WIR schreiben (Server-stdin), serverOutput = was wir lesen (Server-stdout).
            var transport = new StreamClientTransport(serverInput: exec.Stdin, serverOutput: exec.Stdout, loggerFactory);
            client = await McpClient.CreateAsync(transport, null, loggerFactory, linkedCt);
            var tools = await client.ListToolsAsync((RequestOptions?)null, linkedCt);
            return new DastProbeSession(client, exec, [.. tools]);
        }
        catch
        {
            // Scheitert ListToolsAsync NACH einem erfolgreichen CreateAsync, darf der schon verbundene
            // Client nicht leaken — erst ihn, dann den exec-Stream schließen (gleiche Reihenfolge wie
            // DisposeAsync unten).
            if (client is not null)
                await client.DisposeAsync();
            await exec.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await _client.DisposeAsync(); }
        finally { await _exec.DisposeAsync(); }
    }
}

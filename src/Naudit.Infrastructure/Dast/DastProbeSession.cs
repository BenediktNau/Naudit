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
        // Backstop gegen ein hängendes Docker-Daemon (exec create/start) UND einen hängenden MCP-Handshake:
        // McpClient.CreateAsync liest den stdio-Server-Output per Pipe und behandelt ein sofortiges EOF
        // (Server antwortet nie/Prozess kam nie hoch) NICHT als Fehler, sondern wartet unbegrenzt weiter —
        // und auch ein wedged Socket / langsame Header-Antwort beim exec start würde sonst nur durch die
        // Review-weite ct begrenzt. Ohne diesen Deckel würde ein toter/unerreichbarer Probe-Container oder
        // ein hängender Daemon die Review für immer blockieren, statt in den Fail-open-Pfad des Analyzers
        // (Task 5/6) zu laufen. Der Probe-Container läuft bereits (sleep infinity, Image bereits gepullt) —
        // es bleibt nur node-Start + Headless-Chromium-Launch, üblicherweise deutlich unter 10s. Der Deckel
        // umschließt darum bewusst schon den exec create/start, nicht erst den Handshake.
        using var timeoutCts = new CancellationTokenSource(options.HandshakeTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var linkedCt = linkedCts.Token;

        var exec = await docker.ExecStreamAsync(probeContainer, options.ProbeMcpArgv,
            environment: null, workingDirectory: "/", linkedCt);
        McpClient? client = null;
        try
        {
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

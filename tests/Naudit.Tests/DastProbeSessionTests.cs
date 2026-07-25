using Microsoft.Extensions.Logging.Abstractions;
using Naudit.Infrastructure.Dast;
using Naudit.Infrastructure.Docker;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class DastProbeSessionTests
{
    [Fact]
    public async Task Start_execsProbeArgv_inProbeContainer()
    {
        var docker = new ThrowAfterExecDocker();   // lässt ExecStream zu, MCP-Handshake schlägt dann fehl
        var options = new DastOptions { HandshakeTimeout = TimeSpan.FromMilliseconds(200) };

        await Assert.ThrowsAnyAsync<Exception>(() => DastProbeSession.StartAsync(
            docker, options, "naudit-dast-pw-xyz", NullLoggerFactory.Instance, CancellationToken.None));

        var call = Assert.Single(docker.ExecStreamCalls);
        Assert.Equal("naudit-dast-pw-xyz", call.Container);
        Assert.Equal(options.ProbeMcpArgv, call.Argv);
    }

    /// <summary>ExecStream liefert einen Stream, auf dem der MCP-Handshake nie antwortet ⇒ StartAsync
    /// muss (mit Timeout/Fehler) werfen statt zu hängen; der Analyzer fängt das fail-open.</summary>
    private sealed class ThrowAfterExecDocker : FakeDockerClient
    {
        // NextExecStdout bleibt leer ⇒ McpClient.CreateAsync bekommt EOF/kein Handshake ⇒ Fehler.
    }
}

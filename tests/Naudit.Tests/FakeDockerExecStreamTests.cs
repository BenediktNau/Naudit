using System.Text;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class FakeDockerExecStreamTests
{
    [Fact]
    public async Task ExecStream_fake_echoesScriptedStdout_andRecordsArgv()
    {
        var docker = new FakeDockerClient();
        docker.NextExecStdout = Encoding.UTF8.GetBytes("hello-from-probe");

        await using var exec = await docker.ExecStreamAsync("naudit-dast-pw-1", ["node", "/app/cli.js"],
            environment: null, workingDirectory: "/");
        await exec.Stdin.WriteAsync(Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\"}"));

        var buf = new byte[64];
        var n = await exec.Stdout.ReadAsync(buf);

        Assert.Equal("hello-from-probe", Encoding.UTF8.GetString(buf, 0, n));
        Assert.Contains(docker.ExecStreamCalls, c => c.Container == "naudit-dast-pw-1" && c.Argv[0] == "node");
        Assert.Contains("{\"jsonrpc\":\"2.0\"}", Encoding.UTF8.GetString(docker.LastExecStdin!));
    }
}

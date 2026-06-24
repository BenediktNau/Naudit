using System.Text.Json;
using Microsoft.Extensions.AI;
using Naudit.Infrastructure.Ai;
using Naudit.Infrastructure.Ai.ClaudeCode;
using Naudit.Infrastructure.Process;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class ClaudeCodeChatClientTests
{
    // Baut ein --output-format-json-Envelope wie die CLI es liefert.
    private static string Envelope(string? result, bool isError = false, string subtype = "success")
        => JsonSerializer.Serialize(new { type = "result", subtype, is_error = isError, result });

    private static ChatMessage[] Messages() =>
    [
        new(ChatRole.System, "SYS-PROMPT"),
        new(ChatRole.User, "USER-DIFF"),
    ];

    private static ClaudeCodeChatClient Client(Func<ProcessSpec, ProcessResult> responder, AiOptions? opts = null)
        => new(opts ?? new AiOptions { Provider = AiProvider.ClaudeCode, Model = "sonnet" },
               new StubProcessRunner(responder));

    [Fact]
    public async Task GetResponseAsync_buildsHeadlessArgs_andPipesUserMessageToStdIn()
    {
        var stub = new StubProcessRunner(_ => new ProcessResult(0, Envelope("OK"), ""));
        var client = new ClaudeCodeChatClient(
            new AiOptions { Provider = AiProvider.ClaudeCode, Model = "sonnet" }, stub);

        await client.GetResponseAsync(Messages());

        var args = stub.LastSpec!.Arguments.ToList();
        Assert.Contains("-p", args);
        Assert.Contains("--output-format", args);
        Assert.Contains("json", args);
        Assert.Contains("--max-turns", args);
        Assert.Equal("1", args[args.IndexOf("--max-turns") + 1]);  // genau ein Turn
        Assert.Contains("--tools", args);                 // gefolgt von "" → Tools aus
        Assert.Equal("", args[args.IndexOf("--tools") + 1]);
        Assert.Equal("sonnet", args[args.IndexOf("--model") + 1]);
        Assert.Equal("SYS-PROMPT", args[args.IndexOf("--system-prompt") + 1]);
        Assert.Equal("USER-DIFF", stub.LastSpec.StdIn);   // Diff geht über stdin, nicht als Arg
    }

    [Fact]
    public async Task GetResponseAsync_returnsResultFieldAsText()
    {
        var client = Client(_ => new ProcessResult(0, Envelope("{\"verdict\":\"approve\"}"), ""));

        var response = await client.GetResponseAsync(Messages());

        Assert.Equal("{\"verdict\":\"approve\"}", response.Text);
    }

    [Fact]
    public async Task GetResponseAsync_stripsJsonCodeFences()
    {
        var client = Client(_ => new ProcessResult(0, Envelope("```json\n{\"x\":1}\n```"), ""));

        var response = await client.GetResponseAsync(Messages());

        Assert.Equal("{\"x\":1}", response.Text);
    }

    [Fact]
    public async Task GetResponseAsync_defaultsModelToSonnet_whenUnset()
    {
        var stub = new StubProcessRunner(_ => new ProcessResult(0, Envelope("OK"), ""));
        var client = new ClaudeCodeChatClient(new AiOptions { Provider = AiProvider.ClaudeCode }, stub);

        await client.GetResponseAsync(Messages());

        var args = stub.LastSpec!.Arguments.ToList();
        Assert.Equal("sonnet", args[args.IndexOf("--model") + 1]);
    }

    [Fact]
    public async Task GetResponseAsync_passesApiKeyAsOAuthTokenEnv()
    {
        var stub = new StubProcessRunner(_ => new ProcessResult(0, Envelope("OK"), ""));
        var client = new ClaudeCodeChatClient(
            new AiOptions { Provider = AiProvider.ClaudeCode, Model = "sonnet", ApiKey = "tok-123" }, stub);

        await client.GetResponseAsync(Messages());

        Assert.NotNull(stub.LastSpec!.Environment);
        Assert.Equal("tok-123", stub.LastSpec.Environment!["CLAUDE_CODE_OAUTH_TOKEN"]);
    }

    [Fact]
    public async Task GetResponseAsync_nonZeroExit_throws()
    {
        var client = Client(_ => new ProcessResult(1, "", "boom"));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync(Messages()));
        Assert.Contains("boom", ex.Message);
    }

    [Fact]
    public async Task GetResponseAsync_isErrorEnvelope_throws()
    {
        var client = Client(_ => new ProcessResult(0, Envelope("x", isError: true), ""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync(Messages()));
    }

    [Fact]
    public async Task GetResponseAsync_nonSuccessSubtype_throws()
    {
        var client = Client(_ => new ProcessResult(0, Envelope("x", subtype: "error_max_turns"), ""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync(Messages()));
    }

    [Fact]
    public async Task GetResponseAsync_emptyResult_throws()
    {
        var client = Client(_ => new ProcessResult(0, Envelope(""), ""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync(Messages()));
    }

    [Fact]
    public async Task GetResponseAsync_fencedEmptyResult_throws()
    {
        // Ein Fence-Block ohne Inhalt muss ebenfalls als leer erkannt werden (fail-closed).
        var client = Client(_ => new ProcessResult(0, Envelope("```json\n```"), ""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync(Messages()));
    }
}

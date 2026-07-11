using Naudit.Core.Abstractions;
using Naudit.Core.Models;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class AiClientRouterTests
{
    [Fact]
    public async Task SingleClientRouter_returnsGlobalClient_withoutAttribution()
    {
        var chat = new FakeChatClient("egal");
        var router = new SingleClientRouter(chat);

        var selection = await router.SelectAsync(new ReviewRequest("p", 1, "T"));

        Assert.Same(chat, selection.Client);
        Assert.Null(selection.UsedSessionAccountId());
    }
}

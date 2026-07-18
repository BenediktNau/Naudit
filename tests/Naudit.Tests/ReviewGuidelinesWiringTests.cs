using Microsoft.Extensions.AI;
using Naudit.Core.Abstractions;
using Naudit.Core.Models;
using Naudit.Core.Review;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

/// <summary>Beweist die End-to-End-Verdrahtung des Architektur-Profils: IReviewGuidelines.GetAsync
/// landet — redigiert — als eigene Prompt-Sektion im an das LLM gesendeten Text.</summary>
public class ReviewGuidelinesWiringTests
{
    private static readonly ReviewRequest Request = new("1", 42, "Title");

    private static ReviewService CreateService(IChatClient chat, IGitPlatform git, IReviewGuidelines guidelines) =>
        new(new SingleClientRouter(chat), git, new ReviewOptions { SystemPrompt = "SYS" },
            new FakeWorkspaceProvider(), Array.Empty<ISastAnalyzer>(), new FakeFindingReducer(),
            new NullPromptRedactor(), new FakeContextCollector(), new FakeReviewAuditSink(),
            new NullReviewToolProvider(), new FakeRoundtripCounter(), new FakeReviewMemory(),
            guidelines);

    [Fact]
    public async Task ReviewAsync_rendersGuidelinesSection_inPrompt()
    {
        var chat = new FakeChatClient("""{"summary":"ok","comments":[]}""");
        var git = new FakeGitPlatform([new CodeChange("a.cs", "@@ -0,0 +1,1 @@\n+x")]);
        var guidelines = new FakeReviewGuidelines("- Webhook endpoints must enqueue and return 200 immediately.");
        var service = CreateService(chat, git, guidelines);

        await service.ReviewAsync(Request);

        var user = chat.LastMessages![1].Text;
        Assert.Contains("# Project guidelines", user);
        Assert.Contains("must enqueue and return 200", user);
    }

    [Fact]
    public async Task ReviewAsync_withNullGuidelines_promptHasNoGuidelinesSection()
    {
        var chat = new FakeChatClient("""{"summary":"ok","comments":[]}""");
        var git = new FakeGitPlatform([new CodeChange("a.cs", "@@ -0,0 +1,1 @@\n+x")]);
        var guidelines = new FakeReviewGuidelines(null);
        var service = CreateService(chat, git, guidelines);

        await service.ReviewAsync(Request);

        var user = chat.LastMessages![1].Text;
        Assert.DoesNotContain("# Project guidelines", user);
    }
}

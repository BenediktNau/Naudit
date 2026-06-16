using Microsoft.Extensions.AI;
using Naudit.Core.Abstractions;
using Naudit.Core.Models;

namespace Naudit.Core.Review;

public sealed class ReviewService(IChatClient chatClient, IGitPlatform gitPlatform, ReviewOptions options)
{
    public async Task ReviewAsync(ReviewRequest request, CancellationToken ct = default)
    {
        var changes = await gitPlatform.GetChangesAsync(request, ct);
        if (changes.Count == 0)
            return;

        var messages = PromptBuilder.Build(options.SystemPrompt, request, changes);
        var response = await chatClient.GetResponseAsync(messages, cancellationToken: ct);
        await gitPlatform.PostSummaryAsync(request, response.Text, ct);
    }
}

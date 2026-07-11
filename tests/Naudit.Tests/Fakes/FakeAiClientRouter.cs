using Microsoft.Extensions.AI;
using Naudit.Core.Abstractions;
using Naudit.Core.Models;

namespace Naudit.Tests.Fakes;

// Liefert einen festen Client + feste Attribution — für ReviewService-Tests.
internal sealed class FakeAiClientRouter(IChatClient client, int? sessionAccountId = null) : IAiClientRouter
{
    public ReviewRequest? LastRequest { get; private set; }

    public Task<AiClientSelection> SelectAsync(ReviewRequest request, CancellationToken ct = default)
    {
        LastRequest = request;
        return Task.FromResult(new AiClientSelection(client, () => sessionAccountId));
    }
}

using Microsoft.Extensions.AI;
using Naudit.Core.Abstractions;
using Naudit.Core.Models;

namespace Naudit.Tests.Fakes;

// Liefert eine feste Tool-Liste — für ReviewService-Tests (kein echter MCP-Server nötig).
internal sealed class FakeReviewToolProvider(params AITool[] tools) : IReviewToolProvider
{
    public Task<IReadOnlyList<AITool>> GetToolsAsync(ReviewRequest request, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AITool>>(tools);
}

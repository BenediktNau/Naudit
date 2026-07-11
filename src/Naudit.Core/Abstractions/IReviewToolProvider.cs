using Microsoft.Extensions.AI;
using Naudit.Core.Models;

namespace Naudit.Core.Abstractions;

/// <summary>Liefert die Tools, die dem LLM für diesen Review angeboten werden (leer = keine).
/// MEAI-Abstraktion AITool ist erlaubt; das Bauen aus MCP-Servern passiert in Infrastructure.</summary>
public interface IReviewToolProvider
{
    Task<IReadOnlyList<AITool>> GetToolsAsync(ReviewRequest request, CancellationToken ct = default);
}

/// <summary>Default ohne MCP: keine Tools — identischer Single-Shot wie heute.</summary>
public sealed class NullReviewToolProvider : IReviewToolProvider
{
    public Task<IReadOnlyList<AITool>> GetToolsAsync(ReviewRequest request, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AITool>>([]);
}

using System.Collections.Concurrent;

namespace Naudit.Infrastructure.Ai.ClaudeCode;

/// <summary>In-Memory-Cooldown gescheiterter Autor-Sessions (accountId → coolUntil). Bewusst
/// nicht persistent: nach einem Neustart kostet das schlimmstenfalls einen Fehlversuch mit
/// erneutem Fallback.</summary>
public sealed class SessionHealthRegistry(TimeProvider? time = null)
{
    private readonly ConcurrentDictionary<int, DateTimeOffset> _coolUntil = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public void MarkFailure(int accountId, TimeSpan cooldown)
        => _coolUntil[accountId] = _time.GetUtcNow() + cooldown;

    public bool IsCoolingDown(int accountId) => CoolingDownUntil(accountId) is not null;

    public DateTimeOffset? CoolingDownUntil(int accountId)
        => _coolUntil.TryGetValue(accountId, out var until) && until > _time.GetUtcNow() ? until : null;
}

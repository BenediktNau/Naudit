using Naudit.Core.Abstractions;

namespace Naudit.Infrastructure.Ui;

/// <summary>Gate-No-Op für deaktiviertes UI: jedes Projekt ist erlaubt (heutiges Verhalten).</summary>
public sealed class AllowAllAccessGate : IAccessGate
{
    public Task<bool> IsAllowedAsync(string projectId, CancellationToken ct = default) => Task.FromResult(true);
}

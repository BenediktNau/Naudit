using Naudit.Core.Abstractions;

namespace Naudit.Infrastructure.Ui;

/// <summary>Gate-No-Op für Naudit:AccessGate:Mode=Open (Default): jedes Projekt ist erlaubt
/// (Pre-WebUI-Verhalten).</summary>
public sealed class AllowAllAccessGate : IAccessGate
{
    public Task<bool> IsAllowedAsync(string projectId, CancellationToken ct = default) => Task.FromResult(true);
}

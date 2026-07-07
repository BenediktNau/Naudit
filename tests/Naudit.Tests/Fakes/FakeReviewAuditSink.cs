using Naudit.Core.Abstractions;
using Naudit.Core.Models;

namespace Naudit.Tests.Fakes;

/// <summary>Sammelt Audits in-memory; optional werfend, um die Fehlertoleranz zu testen.</summary>
public sealed class FakeReviewAuditSink : IReviewAuditSink
{
    public List<ReviewAudit> Recorded { get; } = [];
    public bool ThrowOnRecord { get; set; }

    public Task RecordAsync(ReviewAudit audit, CancellationToken ct = default)
    {
        if (ThrowOnRecord) throw new InvalidOperationException("sink kaputt");
        Recorded.Add(audit);
        return Task.CompletedTask;
    }
}

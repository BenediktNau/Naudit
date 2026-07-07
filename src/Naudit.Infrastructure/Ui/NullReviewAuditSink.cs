using Naudit.Core.Abstractions;
using Naudit.Core.Models;

namespace Naudit.Infrastructure.Ui;

/// <summary>Sink-No-Op für deaktiviertes UI: Audits werden verworfen.</summary>
public sealed class NullReviewAuditSink : IReviewAuditSink
{
    public Task RecordAsync(ReviewAudit audit, CancellationToken ct = default) => Task.CompletedTask;
}

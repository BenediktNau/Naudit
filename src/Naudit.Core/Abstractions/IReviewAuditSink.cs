using Naudit.Core.Models;

namespace Naudit.Core.Abstractions;

/// <summary>Nimmt das Audit eines gelaufenen Reviews entgegen (Dashboard/Token-Usage).
/// Der Aufrufer (ReviewService) behandelt Sink-Fehler als Best-Effort — ein Fehler kippt nie den Review.</summary>
public interface IReviewAuditSink
{
    Task RecordAsync(ReviewAudit audit, CancellationToken ct = default);
}

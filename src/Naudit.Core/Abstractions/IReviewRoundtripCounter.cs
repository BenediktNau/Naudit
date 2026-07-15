namespace Naudit.Core.Abstractions;

/// <summary>Zählt bereits gepostete Reviews eines MR/PR — Basis des Roundtrip-Limits
/// (Naudit:Review:MaxRoundtrips). DB-gestützte Implementierung in Infrastructure.</summary>
public interface IReviewRoundtripCounter
{
    Task<int> CountAsync(string projectId, int mergeRequestIid, CancellationToken ct = default);
}

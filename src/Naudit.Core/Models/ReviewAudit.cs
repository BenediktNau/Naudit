namespace Naudit.Core.Models;

/// <summary>Ein Fund fürs Review-Audit (inline oder ohne Position) — speist das Dashboard.</summary>
public sealed record AuditFinding(FindingSeverity Severity, ReviewConfidence Confidence, string? File, int? Line, string Text);

/// <summary>Protokoll eines gelaufenen Reviews: Verdict, Findings und Token-Verbrauch (aus MEAI Usage).</summary>
public sealed record ReviewAudit(
    string ProjectId,
    int MergeRequestIid,
    string Title,
    ReviewVerdict Verdict,
    string Summary,
    IReadOnlyList<AuditFinding> Findings,
    long? InputTokens,
    long? OutputTokens,
    string? Model);

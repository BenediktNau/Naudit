namespace Naudit.Core.Models;

/// <summary>Maschinenlesbares Urteil eines Reviews — Basis fürs CI-Gate.</summary>
public enum ReviewVerdict { Approve, RequestChanges }

/// <summary>Ergebnis eines Reviews: der gepostete Markdown-Text plus das Urteil.
/// Skipped: Review wurde wegen des Roundtrip-Limits übersprungen (nichts gepostet).</summary>
public sealed record ReviewResult(string Markdown, ReviewVerdict Verdict, bool Skipped = false);

namespace Naudit.Core.Models;

/// <summary>Maschinenlesbares Urteil eines Reviews — Basis fürs CI-Gate.</summary>
public enum ReviewVerdict { Approve, RequestChanges }

/// <summary>Ergebnis eines Reviews: der geposteter Markdown-Text plus das Urteil.</summary>
public sealed record ReviewResult(string Markdown, ReviewVerdict Verdict);

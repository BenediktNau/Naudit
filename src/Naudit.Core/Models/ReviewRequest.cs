namespace Naudit.Core.Models;

/// <summary>AuthorLogin: Login des MR-/PR-Autors (GitHub: aus dem Webhook; GitLab: null,
/// wird bei Bedarf per API aufgelöst) — Basis fürs Autor-Session-Routing.</summary>
public sealed record ReviewRequest(string ProjectId, int MergeRequestIid, string Title, string? AuthorLogin = null);

/// <summary>Eine geänderte Datei mit ihrem unified diff.</summary>
public sealed record CodeChange(string FilePath, string Diff);
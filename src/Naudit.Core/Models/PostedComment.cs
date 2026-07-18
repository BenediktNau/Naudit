namespace Naudit.Core.Models;

/// <summary>Plattform-Ids eines geposteten Inline-Kommentars, index-gleich zur Eingabe-Kommentarliste.
/// CommentId: GitHub-Review-Comment-Id bzw. GitLab-Discussion-Id. NoteId: GitLab-Note-Id (GitHub null).
/// Beide null möglich, wenn die Plattform keine Id lieferte oder die Erfassung fehlschlug.</summary>
public sealed record PostedComment(string? CommentId, string? NoteId);

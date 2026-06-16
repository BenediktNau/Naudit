namespace Naudit.Core.Models;

/// <summary>Identifiziert den zu reviewenden Merge Request.</summary>
public sealed record ReviewRequest(string ProjectId, int MergeRequestIid, string Title);

/// <summary>Eine geänderte Datei mit ihrem unified diff.</summary>
public sealed record CodeChange(string FilePath, string Diff);
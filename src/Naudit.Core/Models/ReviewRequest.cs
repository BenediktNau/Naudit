namespace Naudit.Core.Models;

/// <summary>Woher ein Review angestoßen wurde. Webhook-Reviews unterliegen dem Roundtrip-Limit;
/// der synchrone CI-Trigger (POST /review) nie — das Merge-Gate braucht immer ein frisches Verdict.</summary>
public enum ReviewTrigger { Webhook, Ci }

/// <summary>Identifiziert den zu reviewenden Merge Request.
/// AuthorLogin: Login des MR-/PR-Autors (GitHub: aus dem Webhook; GitLab: null, per API aufgelöst) — Autor-Session-Routing.
/// Trigger: Webhook (Default, dem Roundtrip-Limit unterworfen) | Ci (POST /review, nie limitiert).</summary>
public sealed record ReviewRequest(string ProjectId, int MergeRequestIid, string Title,
    string? AuthorLogin = null, ReviewTrigger Trigger = ReviewTrigger.Webhook);

/// <summary>Eine geänderte Datei mit ihrem unified diff.</summary>
public sealed record CodeChange(string FilePath, string Diff);
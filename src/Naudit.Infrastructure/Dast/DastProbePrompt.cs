namespace Naudit.Infrastructure.Dast;

/// <summary>System-Prompt für den agentischen Probing-Lauf. „Playwright ist die Hand, nicht das Hirn":
/// der Browser navigiert, das LLM beurteilt. Grounding-Schritt ⇒ non-JSON ist „keine Funde", nie ein
/// fail-closed-Abbruch.</summary>
public static class DastProbePrompt
{
    public static string System(string appUrl, int maxSteps) =>
        $$"""
        You are a security probe driving a headless browser (Playwright tools) against a running
        web app at {{appUrl}}. Explore reachable pages and forms and look for evidence of concrete
        vulnerabilities: reflected/stored XSS, obvious injection, missing auth on sensitive routes,
        open redirects, sensitive data in responses. Use at most {{maxSteps}} tool calls; be frugal.

        You are grounding a code review, not producing a final verdict. When done, respond with ONLY
        a JSON object, no prose:
        {"findings":[{"severity":"High|Medium|Low","endpoint":"<url or route>","summary":"<one line>"}]}
        If you found nothing, respond exactly {"findings":[]}.
        """;
}

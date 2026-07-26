namespace Naudit.Infrastructure.Ai.Logging;

/// <summary>Ein erfasster LLM-Austausch (ein Aufruf) — das, was das PromptLoggingBehavior an die
/// Persistenz übergibt. Prompt-/Antwort-Felder sind bereits gemäß AiLoggingOptions gefiltert/gekappt.</summary>
public sealed record ChatTranscript(
    Guid CorrelationId,
    string ProjectId,
    int PrNumber,
    string Trigger,
    string? Model,
    string? SystemPrompt,
    string? UserPrompt,
    string? ResponseText,
    long? InputTokens,
    long? OutputTokens,
    long LatencyMs,
    int ToolCount,
    bool Failed);

/// <summary>Persistenz-Naht für Transcripts (IPromptRedactor-Muster: Interface hier in
/// Infrastructure, EF-Impl daneben). Best-effort: ein Fehler beim Schreiben darf ein bereits
/// gelaufenes Review nie kippen.</summary>
public interface IChatTranscriptSink
{
    Task RecordAsync(ChatTranscript transcript, CancellationToken ct = default);
}

using System.Diagnostics;
using Mediator;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Naudit.Infrastructure.Ai.Logging;

/// <summary>Die Prompt-/Kommunikations-Middleware: ein Mediator-Pipeline-Behavior, das sich um
/// JEDEN LLM-Aufruf legt (global wie Autor-/Pool-Session). Davor: strukturiertes Log inkl.
/// System-/User-Prompt; danach: Latenz, Token-Usage, rohe Antwort — plus (opt-in) Persistenz fürs
/// WebUI-Review-Detail. Fail-open: ein Fehler im Logging/Persistieren kippt nie das Review, nur
/// eine echte Aufruf-Exception (LLM/Cancel) wird — nach Erfassung — weitergereicht.</summary>
public sealed class PromptLoggingBehavior(
    AiLoggingOptions options,
    IReviewCorrelationAccessor correlation,
    IServiceScopeFactory scopeFactory,
    ILogger<PromptLoggingBehavior> logger)
    : IPipelineBehavior<ChatCompletionRequest, ChatResponse>
{
    public async ValueTask<ChatResponse> Handle(
        ChatCompletionRequest message,
        MessageHandlerDelegate<ChatCompletionRequest, ChatResponse> next,
        CancellationToken cancellationToken)
    {
        var corr = correlation.Current;
        var systemPrompt = JoinRole(message.Messages, ChatRole.System);
        var userPrompt = JoinRole(message.Messages, ChatRole.User);
        var toolCount = message.Options?.Tools?.Count ?? 0;

        logger.LogInformation(
            "LLM-Aufruf ▸ {Project}#{Pr} corr={Corr} tools={Tools} sys={SysLen}z user={UserLen}z",
            corr?.ProjectId ?? "-", corr?.PrNumber ?? 0, corr?.Id, toolCount,
            systemPrompt?.Length ?? 0, userPrompt?.Length ?? 0);
        if (options.IncludePrompts)
            // Auch das Log wird gekappt: MaxCharsPerField ist eine Obergrenze für den Prompt-Text
            // überhaupt, nicht nur für die DB-Spalte — Logs haben oft weitere Verbreitung als das
            // admin-geschützte Review-Detail.
            logger.LogDebug("LLM-Prompt ▸ corr={Corr}\n--- system ---\n{System}\n--- user ---\n{User}",
                corr?.Id, Cap(systemPrompt), Cap(userPrompt));

        var start = Stopwatch.GetTimestamp();
        ChatResponse response;
        try
        {
            response = await next(message, cancellationToken);
        }
        catch (Exception ex)
        {
            var elapsed = (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            logger.LogWarning(ex, "LLM-Aufruf fehlgeschlagen ▸ {Project}#{Pr} corr={Corr} nach {Ms}ms",
                corr?.ProjectId ?? "-", corr?.PrNumber ?? 0, corr?.Id, elapsed);
            // Fehlversuch als Transcript festhalten (z. B. Autor-Session-Fehler vor dem Fallback),
            // aber nur echte Fehler — Cancel nicht persistieren, nur weiterreichen.
            if (ex is not OperationCanceledException)
                await PersistSafe(new ChatTranscript(
                    corr?.Id ?? Guid.Empty, corr?.ProjectId ?? "", corr?.PrNumber ?? 0, corr?.Trigger ?? "",
                    Model: null, Prompt(systemPrompt), Prompt(userPrompt), ResponseText: null,
                    InputTokens: null, OutputTokens: null, elapsed, toolCount, Failed: true), corr, cancellationToken);
            throw;
        }

        var ms = (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var input = response.Usage?.InputTokenCount;
        var output = response.Usage?.OutputTokenCount;
        logger.LogInformation("LLM-Antwort ◂ {Project}#{Pr} corr={Corr} {Ms}ms {In} in/{Out} out model={Model}",
            corr?.ProjectId ?? "-", corr?.PrNumber ?? 0, corr?.Id, ms, input, output, response.ModelId);
        if (options.IncludeResponse)
            logger.LogDebug("LLM-Antwort-Text ◂ corr={Corr}\n{Text}", corr?.Id, Cap(response.Text));

        await PersistSafe(new ChatTranscript(
            corr?.Id ?? Guid.Empty, corr?.ProjectId ?? "", corr?.PrNumber ?? 0, corr?.Trigger ?? "",
            response.ModelId, Prompt(systemPrompt), Prompt(userPrompt),
            options.IncludeResponse ? Cap(response.Text) : null,
            input, output, ms, toolCount, Failed: false), corr, cancellationToken);

        return response;
    }

    /// <summary>Persistiert best-effort in eigenem Scope (das Behavior ist Singleton, der Sink+DbContext
    /// sind scoped). Nur wenn Persist an UND eine Review-Korrelation vorliegt (globale Nicht-Review-Calls
    /// wie die Guideline-Destillation werden geloggt, aber nicht als Review-Transcript gespeichert).</summary>
    private async Task PersistSafe(ChatTranscript transcript, ReviewCorrelation? corr, CancellationToken ct)
    {
        if (!options.Persist || corr is null) return;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var sink = scope.ServiceProvider.GetRequiredService<IChatTranscriptSink>();
            await sink.RecordAsync(transcript, ct);
        }
        catch (OperationCanceledException)
        {
            // Auch ein Abbruch wird geschluckt: das Nachtragen ist Buchhaltung. Entwiche die
            // OperationCanceledException, ersetzte sie im Erfolgsfall die bereits erhaltene Antwort
            // und im Fehlerfall die ursprüngliche Aufruf-Exception (die direkt danach geworfen wird).
            logger.LogDebug("Transcript-Persistenz abgebrochen ▸ corr={Corr}", corr.Id);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Transcript-Persistenz fehlgeschlagen ▸ corr={Corr}", corr.Id);
        }
    }

    /// <summary>Prompt-Feld: nur wenn IncludePrompts an, sonst null (Metadaten bleiben erhalten).</summary>
    private string? Prompt(string? text) => options.IncludePrompts ? Cap(text) : null;

    /// <summary>Kürzt auf MaxCharsPerField. Die Grenze gilt für das FERTIGE Feld — der Marker zählt
    /// mit, sonst wäre gerade der gekappte Wert länger als konfiguriert. Ist die Grenze kleiner als
    /// der Marker, wird hart abgeschnitten (ein Marker allein spränge sonst schon darüber).</summary>
    private string? Cap(string? text)
    {
        if (text is null) return null;
        var max = options.MaxCharsPerField;
        if (max <= 0 || text.Length <= max) return text;

        var end = max <= TruncationMarker.Length ? max : max - TruncationMarker.Length;
        // Kein Surrogat-Paar (z. B. Emoji) zerschneiden — sonst bleibt ein ungültiges lone surrogate stehen.
        if (end > 0 && char.IsHighSurrogate(text[end - 1]))
            end--;

        return max <= TruncationMarker.Length ? text[..end] : text[..end] + TruncationMarker;
    }

    private const string TruncationMarker = "…[gekürzt]";

    private static string? JoinRole(IEnumerable<ChatMessage> messages, ChatRole role)
    {
        var parts = messages.Where(m => m.Role == role).Select(m => m.Text).Where(t => !string.IsNullOrEmpty(t));
        var joined = string.Join("\n", parts);
        return joined.Length == 0 ? null : joined;
    }
}

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
            logger.LogDebug("LLM-Prompt ▸ corr={Corr}\n--- system ---\n{System}\n--- user ---\n{User}",
                corr?.Id, systemPrompt, userPrompt);

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
            logger.LogDebug("LLM-Antwort-Text ◂ corr={Corr}\n{Text}", corr?.Id, response.Text);

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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Transcript-Persistenz fehlgeschlagen ▸ corr={Corr}", corr.Id);
        }
    }

    /// <summary>Prompt-Feld: nur wenn IncludePrompts an, sonst null (Metadaten bleiben erhalten).</summary>
    private string? Prompt(string? text) => options.IncludePrompts ? Cap(text) : null;

    private string? Cap(string? text)
    {
        if (text is null) return null;
        var max = options.MaxCharsPerField;
        return max > 0 && text.Length > max ? text[..max] + "…[gekürzt]" : text;
    }

    private static string? JoinRole(IEnumerable<ChatMessage> messages, ChatRole role)
    {
        var parts = messages.Where(m => m.Role == role).Select(m => m.Text).Where(t => !string.IsNullOrEmpty(t));
        var joined = string.Join("\n", parts);
        return joined.Length == 0 ? null : joined;
    }
}

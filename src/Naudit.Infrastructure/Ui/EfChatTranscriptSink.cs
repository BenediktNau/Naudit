using Microsoft.Extensions.Logging;
using Naudit.Infrastructure.Ai.Logging;
using Naudit.Infrastructure.Data;

namespace Naudit.Infrastructure.Ui;

/// <summary>Persistiert ein ChatTranscript als Zeile in ChatTranscripts. Scoped (eigener DbContext);
/// das Singleton-Behavior öffnet dafür pro Aufruf einen Scope. Best-effort — der Aufrufer fängt
/// Fehler bereits ab, hier nur die reine Schreiblogik.</summary>
public sealed class EfChatTranscriptSink(NauditDbContext db, ILogger<EfChatTranscriptSink> logger) : IChatTranscriptSink
{
    public async Task RecordAsync(ChatTranscript t, CancellationToken ct = default)
    {
        db.ChatTranscripts.Add(new ChatTranscriptEntity
        {
            CorrelationId = t.CorrelationId,
            ProjectId = t.ProjectId,
            PrNumber = t.PrNumber,
            Trigger = t.Trigger,
            Model = t.Model,
            SystemPrompt = t.SystemPrompt,
            UserPrompt = t.UserPrompt,
            ResponseText = t.ResponseText,
            InputTokens = t.InputTokens,
            OutputTokens = t.OutputTokens,
            LatencyMs = t.LatencyMs,
            ToolCount = t.ToolCount,
            Failed = t.Failed,
            CreatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        logger.LogDebug("Transcript gespeichert ▸ {Project}#{Pr} corr={Corr}", t.ProjectId, t.PrNumber, t.CorrelationId);
    }
}

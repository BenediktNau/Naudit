using Naudit.Core.Review;
using Naudit.Infrastructure.Ai.Logging;

namespace Naudit.Web;

public sealed class ReviewBackgroundService(
    IReviewQueue queue,
    IServiceScopeFactory scopeFactory,
    IReviewCorrelationAccessor correlation,
    AiLoggingOptions logging,
    ILogger<ReviewBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var request in queue.DequeueAllAsync(stoppingToken))
        {
            try
            {
                // Korrelation für die Prompt-Transcripts dieses Reviews setzen (nur wenn Logging an).
                // AsyncLocal ⇒ fließt in ReviewAsync und den Audit-Sink; im finally zurückgesetzt.
                if (logging.Enabled)
                    correlation.Current = new ReviewCorrelation(
                        Guid.NewGuid(), request.ProjectId, request.MergeRequestIid, request.Trigger.ToString());

                using var scope = scopeFactory.CreateScope();
                var reviewService = scope.ServiceProvider.GetRequiredService<ReviewService>();
                var result = await reviewService.ReviewAsync(request, stoppingToken);
                if (result.Skipped)
                    logger.LogInformation("Review für {ProjectId}#{Iid} übersprungen — Roundtrip-Limit erreicht.",
                        request.ProjectId, request.MergeRequestIid);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Review failed for MR {Iid}", request.MergeRequestIid);
            }
            finally
            {
                correlation.Current = null;
            }
        }
    }
}

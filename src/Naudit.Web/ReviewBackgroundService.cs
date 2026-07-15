using Naudit.Core.Review;

namespace Naudit.Web;

public sealed class ReviewBackgroundService(
    IReviewQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<ReviewBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var request in queue.DequeueAllAsync(stoppingToken))
        {
            try
            {
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
        }
    }
}

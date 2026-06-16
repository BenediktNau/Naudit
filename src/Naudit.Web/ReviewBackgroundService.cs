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
                await reviewService.ReviewAsync(request, stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Review failed for MR {Iid}", request.MergeRequestIid);
            }
        }
    }
}

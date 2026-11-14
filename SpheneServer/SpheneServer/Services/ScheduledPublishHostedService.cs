using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Sphene.API.Data.Enum;
using SpheneShared.Data;

namespace SpheneServer.Services;

/// <summary>
/// Materializes due scheduled publications - flips
/// <see cref="ContentPublishStatus.Scheduled"/> rows whose
/// <c>ScheduledForUtc</c> passed into <c>Published</c>. Cheap poll (60 s);
/// reads stay correct even between sweeps because
/// <see cref="PublishingService.IsVisible"/> treats due-scheduled as visible
/// - the sweep only normalizes the stored state.
/// </summary>
public sealed class ScheduledPublishHostedService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ErrorBackoff = TimeSpan.FromMinutes(5);

    private readonly IDbContextFactory<SpheneDbContext> _dbFactory;
    private readonly ILogger<ScheduledPublishHostedService> _logger;

    public ScheduledPublishHostedService(IDbContextFactory<SpheneDbContext> dbFactory,
        ILogger<ScheduledPublishHostedService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var flipped = await MaterializeDueAsync(stoppingToken).ConfigureAwait(false);
                if (flipped > 0)
                {
                    _logger.LogInformation("Scheduled publish: {count} item(s) went live", flipped);
                }
                await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Scheduled publish sweep failed - retrying after backoff");
                await Task.Delay(ErrorBackoff, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<int> MaterializeDueAsync(CancellationToken token)
    {
        await using var ctx = await _dbFactory.CreateDbContextAsync(token).ConfigureAwait(false);
        var now = DateTime.UtcNow;

        var news = await ctx.SyncshellCommunityNews
            .Where(n => n.Status == (int)ContentPublishStatus.Scheduled && n.ScheduledForUtc <= now)
            .ToListAsync(token).ConfigureAwait(false);
        foreach (var n in news)
        {
            n.Status = (int)ContentPublishStatus.Published;
            n.PublishedAt = n.ScheduledForUtc ?? now;
            n.ScheduledForUtc = null;
            n.UpdatedAt = now;
        }

        var blogs = await ctx.UserProfileBlogPosts
            .Where(b => b.Status == (int)ContentPublishStatus.Scheduled && b.ScheduledForUtc <= now)
            .ToListAsync(token).ConfigureAwait(false);
        foreach (var b in blogs)
        {
            b.Status = (int)ContentPublishStatus.Published;
            b.ScheduledForUtc = null;
            b.UpdatedAt = now;
        }

        if (news.Count == 0 && blogs.Count == 0) return 0;
        await ctx.SaveChangesAsync(token).ConfigureAwait(false);
        return news.Count + blogs.Count;
    }
}

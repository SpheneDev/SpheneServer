using SpheneShared.Data;
using SpheneShared.Services;
using SpheneShared.Utils.Configuration;
using Microsoft.EntityFrameworkCore;

namespace SpheneStaticFilesServer.Services;

public sealed class R2CleanupService : BackgroundService
{
    private readonly ILogger<R2CleanupService> _logger;
    private readonly IConfigurationService<StaticFilesServerConfiguration> _configuration;
    private readonly R2StorageService _r2Storage;
    private readonly IDbContextFactory<SpheneDbContext> _dbContextFactory;

    public R2CleanupService(ILogger<R2CleanupService> logger,
        IConfigurationService<StaticFilesServerConfiguration> configuration,
        R2StorageService r2Storage,
        IDbContextFactory<SpheneDbContext> dbContextFactory)
    {
        _logger = logger;
        _configuration = configuration;
        _r2Storage = r2Storage;
        _dbContextFactory = dbContextFactory;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_configuration.GetValueOrDefault(nameof(StaticFilesServerConfiguration.EnableR2Storage), false))
        {
            _logger.LogDebug("R2 cleanup skipped: EnableR2Storage=false");
            return;
        }

        if (!_configuration.GetValueOrDefault(nameof(StaticFilesServerConfiguration.R2CleanupEnabled), false))
        {
            _logger.LogDebug("R2 cleanup skipped: R2CleanupEnabled=false");
            return;
        }

        await base.StartAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("R2 Cleanup Service started");
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var retentionDays = _configuration.GetValueOrDefault(nameof(StaticFilesServerConfiguration.R2ObjectRetentionDays), 90);
            var intervalHours = _configuration.GetValueOrDefault(nameof(StaticFilesServerConfiguration.R2CleanupIntervalHours), 24);

            if (intervalHours <= 0)
            {
                intervalHours = 24;
            }

            try
            {
                await RunCleanupAsync(retentionDays, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during R2 cleanup");
            }

            await Task.Delay(TimeSpan.FromHours(intervalHours), ct).ConfigureAwait(false);
        }
    }

    private async Task RunCleanupAsync(int retentionDays, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow - TimeSpan.FromDays(retentionDays);
        _logger.LogInformation("R2 cleanup started: deleting objects not downloaded since {cutoff:u} ({days} days)", cutoff, retentionDays);

        using var dbContext = await _dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var allUploadedFiles = await dbContext.Files
            .Where(f => f.Uploaded)
            .Select(f => new { f.Hash, f.UploadDate, f.Size })
            .ToListAsync(ct).ConfigureAwait(false);

        if (allUploadedFiles.Count == 0)
        {
            _logger.LogInformation("R2 cleanup: no uploaded files in DB");
            return;
        }

        var lastDownloadByHash = await dbContext.ModDownloadHistory
            .GroupBy(d => d.Hash)
            .Select(g => new { Hash = g.Key, LastDownload = g.Max(d => d.DownloadedAt) })
            .ToDictionaryAsync(d => d.Hash, d => d.LastDownload, StringComparer.OrdinalIgnoreCase, ct).ConfigureAwait(false);

        var filesToDelete = new List<(string Hash, long Size)>(allUploadedFiles.Count);
        foreach (var file in allUploadedFiles)
        {
            if (lastDownloadByHash.TryGetValue(file.Hash, out var lastDownload))
            {
                if (lastDownload < cutoff)
                {
                    filesToDelete.Add((file.Hash, file.Size));
                }
            }
            else if (file.UploadDate < cutoff)
            {
                filesToDelete.Add((file.Hash, file.Size));
            }
        }

        if (filesToDelete.Count == 0)
        {
            _logger.LogInformation("R2 cleanup: no expired files found out of {total} total", allUploadedFiles.Count);
            return;
        }

        var totalSize = filesToDelete.Sum(f => f.Size);
        _logger.LogInformation("R2 cleanup: {expiredCount}/{totalCount} files to delete ({totalSizeMB:F1} MiB)",
            filesToDelete.Count, allUploadedFiles.Count, totalSize / 1024.0 / 1024.0);

        var retainDbEntries = _configuration.GetValueOrDefault(nameof(StaticFilesServerConfiguration.R2RetainDatabaseEntries), true);

        var deleted = 0;
        var failed = 0;
        foreach (var (hash, _) in filesToDelete)
        {
            ct.ThrowIfCancellationRequested();

            var success = await _r2Storage.DeleteObjectByHashAsync(hash, ct).ConfigureAwait(false);
            if (success)
            {
                deleted++;
                if (!retainDbEntries)
                {
                    var fileCache = new SpheneShared.Models.FileCache { Hash = hash };
                    dbContext.Entry(fileCache).State = EntityState.Deleted;
                }
            }
            else
            {
                failed++;
            }

            if ((deleted + failed) % 500 == 0)
            {
                _logger.LogInformation("R2 cleanup progress: deleted={deleted}, failed={failed}, remaining={remaining}",
                    deleted, failed, filesToDelete.Count - deleted - failed);

                if (!retainDbEntries)
                {
                    await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
                }
            }
        }

        if (!retainDbEntries)
        {
            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        _logger.LogInformation("R2 cleanup finished: deleted={deleted}, failed={failed}, totalExpired={totalExpired}",
            deleted, failed, filesToDelete.Count);
    }
}

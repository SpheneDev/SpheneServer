using Microsoft.EntityFrameworkCore;
using SpheneShared.Data;
using SpheneShared.Services;
using SpheneShared.Utils.Configuration;

namespace SpheneStaticFilesServer.Services;

public sealed class R2BackfillHostedService : IHostedService
{
    private readonly ILogger<R2BackfillHostedService> _logger;
    private readonly IConfigurationService<StaticFilesServerConfiguration> _configuration;
    private readonly IDbContextFactory<SpheneDbContext> _dbContextFactory;
    private readonly CachedFileProvider _cachedFileProvider;
    private readonly R2StorageService _r2Storage;
    private CancellationTokenSource? _cts;
    private Task? _task;

    public R2BackfillHostedService(ILogger<R2BackfillHostedService> logger, IConfigurationService<StaticFilesServerConfiguration> configuration,
        IDbContextFactory<SpheneDbContext> dbContextFactory, CachedFileProvider cachedFileProvider, R2StorageService r2Storage)
    {
        _logger = logger;
        _configuration = configuration;
        _dbContextFactory = dbContextFactory;
        _cachedFileProvider = cachedFileProvider;
        _r2Storage = r2Storage;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _task = Task.Run(() => RunAsync(_cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts == null)
        {
            return;
        }

        try
        {
            _cts.Cancel();
        }
        catch
        {
        }

        if (_task != null)
        {
            await _task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        if (!_configuration.GetValueOrDefault(nameof(StaticFilesServerConfiguration.EnableR2Storage), false))
        {
            _logger.LogDebug("R2 backfill skipped: EnableR2Storage=false");
            return;
        }

        if (!_configuration.GetValueOrDefault(nameof(StaticFilesServerConfiguration.EnableR2BackfillOnStartup), false))
        {
            _logger.LogDebug("R2 backfill skipped: EnableR2BackfillOnStartup=false");
            return;
        }

        var maxFiles = _configuration.GetValueOrDefault(nameof(StaticFilesServerConfiguration.R2BackfillMaxFilesPerStartup), 0);
        var parallelism = _configuration.GetValueOrDefault(nameof(StaticFilesServerConfiguration.R2BackfillParallelism), 4);
        if (parallelism <= 0)
        {
            parallelism = 1;
        }

        var useColdStorage = _configuration.GetValueOrDefault(nameof(StaticFilesServerConfiguration.UseColdStorage), false);
        _logger.LogInformation("R2 backfill started. maxFiles={maxFiles}, parallelism={parallelism}, useColdStorage={useColdStorage}", maxFiles, parallelism, useColdStorage);

        await using var db = await _dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var query = db.Files.AsNoTracking()
            .Where(f => f.Size > 0)
            .OrderBy(f => f.Hash)
            .Select(f => f.Hash);

        var semaphore = new SemaphoreSlim(parallelism, parallelism);
        var tasks = new List<Task>(parallelism * 2);
        var processed = 0;
        var uploaded = 0;
        var skipped = 0;

        await foreach (var hash in query.AsAsyncEnumerable().WithCancellation(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();

            if (maxFiles > 0 && processed >= maxFiles)
            {
                break;
            }

            processed++;
            await semaphore.WaitAsync(ct).ConfigureAwait(false);

            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var localFile = await _cachedFileProvider.DownloadAndGetLocalFileInfo(hash).ConfigureAwait(false);
                    if (localFile == null)
                    {
                        Interlocked.Increment(ref skipped);
                        return;
                    }

                    var didUpload = await _r2Storage.UploadIfMissingAsync(hash, localFile.FullName, ct).ConfigureAwait(false);
                    if (didUpload)
                    {
                        Interlocked.Increment(ref uploaded);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "R2 backfill failed for {hash}", hash);
                }
                finally
                {
                    semaphore.Release();
                }
            }, ct));

            if (tasks.Count >= parallelism * 4)
            {
                var completed = await Task.WhenAny(tasks).ConfigureAwait(false);
                tasks.Remove(completed);
            }

            if (processed % 1000 == 0)
            {
                _logger.LogInformation("R2 backfill progress. processed={processed}, uploaded={uploaded}, skippedMissingLocal={skipped}", processed, uploaded, skipped);
            }
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        _logger.LogInformation("R2 backfill finished. processed={processed}, uploaded={uploaded}, skippedMissingLocal={skipped}", processed, uploaded, skipped);
    }
}

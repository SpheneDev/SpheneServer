using SpheneFiles = Sphene.API.Routes.SpheneFiles;
using SpheneStaticFilesServer.Services;
using SpheneStaticFilesServer.Utils;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SpheneShared.Data;
using SpheneShared.Models;

namespace SpheneStaticFilesServer.Controllers;

[Route(SpheneFiles.Cache)]
public class CacheController : ControllerBase
{
    private readonly RequestFileStreamResultFactory _requestFileStreamResultFactory;
    private readonly CachedFileProvider _cachedFileProvider;
    private readonly RequestQueueService _requestQueue;
    private readonly FileStatisticsService _fileStatisticsService;
    private readonly IDbContextFactory<SpheneDbContext> _dbContextFactory;

    public CacheController(ILogger<CacheController> logger, RequestFileStreamResultFactory requestFileStreamResultFactory,
        CachedFileProvider cachedFileProvider, RequestQueueService requestQueue, FileStatisticsService fileStatisticsService,
        IDbContextFactory<SpheneDbContext> dbContextFactory) : base(logger)
    {
        _requestFileStreamResultFactory = requestFileStreamResultFactory;
        _cachedFileProvider = cachedFileProvider;
        _requestQueue = requestQueue;
        _fileStatisticsService = fileStatisticsService;
        _dbContextFactory = dbContextFactory;
    }

    [HttpGet(SpheneFiles.Cache_Get)]
    public async Task<IActionResult> GetFiles(Guid requestId)
    {
        _logger.LogDebug($"GetFile:{SpheneUser}:{requestId}");

        if (!_requestQueue.IsActiveProcessing(requestId, SpheneUser, out var request)) return BadRequest();

        _requestQueue.ActivateRequest(requestId);

        Response.ContentType = "application/octet-stream";

        long requestSize = 0;
        List<BlockFileDataSubstream> substreams = new();
        List<string> downloadedHashes = new();

        foreach (var fileHash in request.FileIds)
        {
            var fs = await _cachedFileProvider.DownloadAndGetLocalFileInfo(fileHash).ConfigureAwait(false);
            if (fs == null) continue;

            substreams.Add(new(fs));

            requestSize += fs.Length;

            if (!string.IsNullOrWhiteSpace(fileHash))
            {
                downloadedHashes.Add(fileHash);
            }
        }

        _fileStatisticsService.LogRequest(requestSize);

        if (downloadedHashes.Count > 0)
        {
            try
            {
                await using var dbContext = await _dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);
                var now = DateTime.UtcNow;
                List<ModDownloadHistory> entries = new(downloadedHashes.Count);
                foreach (var hash in downloadedHashes)
                {
                    entries.Add(new ModDownloadHistory
                    {
                        UserUID = SpheneUser,
                        Hash = hash,
                        DownloadedAt = now
                    });
                }

                await dbContext.ModDownloadHistory.AddRangeAsync(entries).ConfigureAwait(false);
                await dbContext.SaveChangesAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to record download history for request {requestId}", requestId);
            }
        }

        return _requestFileStreamResultFactory.Create(requestId, new BlockFileDataStream(substreams));
    }
}

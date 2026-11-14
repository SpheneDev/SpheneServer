using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SpheneShared.Data;
using SpheneShared.Models;
using SpheneShared.Services;
using SpheneShared.Utils;
using SpheneShared.Utils.Configuration;
using SpheneStaticFilesServer.Services;
using SpheneStaticFilesServer.Utils;

namespace SpheneStaticFilesServer.Controllers;

/// <summary>
/// Community media download route - serves hash-addressed images referenced
/// by community content (gallery, events, banners). The 40-char hash is the
/// capability: unguessable, content-addressed and immutable, so anonymous
/// access is safe while the URL still keeps images off the public web.
/// </summary>
[Route("community/media")]
[AllowAnonymous]
public class CommunityMediaController : ControllerBase
{
    private const long MaxMediaBytes = 4 * 1024 * 1024;
    /// <summary>Upper bound for the awaited durable-storage copy on upload.
    /// Keeps a degraded R2 from stalling the request while still making the
    /// common case synchronous (durable before the change push fans out).</summary>
    private static readonly TimeSpan R2UploadTimeout = TimeSpan.FromSeconds(15);

    private readonly CachedFileProvider _cachedFileProvider;
    private readonly IDbContextFactory<SpheneDbContext> _dbContextFactory;
    private readonly ILogger<CommunityMediaController> _logger;
    private readonly R2StorageService _r2Storage;
    private readonly string _basePath;

    public CommunityMediaController(CachedFileProvider cachedFileProvider,
        IDbContextFactory<SpheneDbContext> dbContextFactory,
        ILogger<CommunityMediaController> logger,
        IConfigurationService<StaticFilesServerConfiguration> configuration,
        R2StorageService r2Storage) : base(logger)
    {
        _cachedFileProvider = cachedFileProvider;
        _dbContextFactory = dbContextFactory;
        _logger = logger;
        _r2Storage = r2Storage;
        _basePath = configuration.GetValueOrDefault(nameof(StaticFilesServerConfiguration.UseColdStorage), false)
            ? configuration.GetValue<string>(nameof(StaticFilesServerConfiguration.ColdStorageDirectory))
            : configuration.GetValue<string>(nameof(StaticFilesServerConfiguration.CacheDirectory));
    }

    /// <summary>Streams the payload for a media hash - 404 when unknown.</summary>
    [HttpGet("{hash}")]
    [ResponseCache(Duration = 31536000, Location = ResponseCacheLocation.Any, NoStore = false)]
    public async Task<IActionResult> Get(string hash, CancellationToken requestAborted)
    {
        hash = hash.ToUpperInvariant();
        if (hash.Length != 40 || !hash.All(Uri.IsHexDigit))
        {
            return BadRequest();
        }

        await using var dbContext = await _dbContextFactory
            .CreateDbContextAsync(requestAborted).ConfigureAwait(false);
        var media = await dbContext.CommunityMedia.AsNoTracking()
            .Where(m => m.Hash == hash)
            .Select(m => new { m.ContentType })
            .SingleOrDefaultAsync(requestAborted).ConfigureAwait(false);
        if (media == null)
        {
            return NotFound();
        }

        var file = await _cachedFileProvider.DownloadAndGetLocalFileInfo(hash)
            .ConfigureAwait(false);
        if (file == null || !file.Exists)
        {
            _logger.LogDebug("community/media miss · {hash}", hash);
            return NotFound();
        }

        return PhysicalFile(file.FullName,
            string.IsNullOrWhiteSpace(media.ContentType) ? "application/octet-stream" : media.ContentType!,
            enableRangeProcessing: false);
    }

    /// <summary>
    /// Community media upload - raw image bytes under the media hash. Unlike
    /// <c>/files/upload/{hash}</c> (LZ4-wrapped, SHA-1 file hash) this route
    /// stores the payload verbatim under the SHA-256 media digest produced by
    /// <c>CommunityMediaService.ComputeMediaHash</c>, so the GET above can
    /// stream it without a decompress step. Auth via the fallback policy;
    /// <see cref="AllowAnonymousAttribute"/> on the class is overridden here.
    /// </summary>
    [HttpPost("{hash}")]
    [Authorize]
    [RequestSizeLimit(MaxMediaBytes)]
    public async Task<IActionResult> Upload(string hash, [FromQuery] string? contentType,
        CancellationToken requestAborted)
    {
        hash = hash.ToUpperInvariant();
        if (hash.Length != 40 || !hash.All(Uri.IsHexDigit))
        {
            return BadRequest();
        }

        using var ms = new MemoryStream();
        await Request.Body.CopyToAsync(ms, requestAborted).ConfigureAwait(false);
        var bytes = ms.ToArray();
        if (bytes.Length == 0 || bytes.Length > MaxMediaBytes)
        {
            return BadRequest();
        }

        // Byte-identical to CommunityMediaService.ComputeMediaHash - the media
        // digest is derived from the RAW payload, not a compressed frame.
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes), 0, 20),
                hash, StringComparison.Ordinal))
        {
            return BadRequest();
        }

        await using var dbContext = await _dbContextFactory
            .CreateDbContextAsync(requestAborted).ConfigureAwait(false);

        var path = FilePathUtil.GetFilePath(_basePath, hash);
        var existing = await dbContext.Files.FindAsync([hash], requestAborted)
            .ConfigureAwait(false);
        if (existing is { Uploaded: true } && existing.Size == bytes.Length
            && System.IO.File.Exists(path)
            && new FileInfo(path).Length == bytes.Length)
        {
            return Ok();
        }

        await System.IO.File.WriteAllBytesAsync(path, bytes, requestAborted)
            .ConfigureAwait(false);

        var uploaderUid = HttpContext.User.Claims
            .FirstOrDefault(f => string.Equals(f.Type, SpheneClaimTypes.Uid, StringComparison.Ordinal))
            ?.Value;
        if (existing == null)
        {
            existing = new FileCache { Hash = hash };
            await dbContext.Files.AddAsync(existing).ConfigureAwait(false);
        }
        existing.UploadDate = DateTime.UtcNow;
        existing.UploaderUID = uploaderUid;
        existing.Size = bytes.Length;
        existing.RawSize = bytes.Length;
        existing.Uploaded = true;

        if (!string.IsNullOrWhiteSpace(contentType))
        {
            var media = await dbContext.CommunityMedia
                .SingleOrDefaultAsync(m => m.Hash == hash, requestAborted).ConfigureAwait(false);
            if (media == null)
            {
                dbContext.CommunityMedia.Add(new CommunityMedia
                {
                    Hash = hash,
                    UploaderUID = uploaderUid,
                    ContentType = contentType,
                    Size = bytes.Length,
                    RefCount = 0,
                    CreatedAt = DateTime.UtcNow
                });
            }
            else if (string.IsNullOrWhiteSpace(media.ContentType))
            {
                media.ContentType = contentType;
            }
        }

        await dbContext.SaveChangesAsync(requestAborted).ConfigureAwait(false);

        // Await the durable copy before acknowledging: the community-changed
        // push reaches viewers within ms, and a GET hitting a node without the
        // local file 404s while a background R2 PUT is still in flight - the
        // client then negative-caches the hash and shows nothing for minutes.
        // Bounded so a degraded R2 can't park the request; a failed copy
        // degrades to local-only, same as deployments without R2.
        if (_r2Storage.IsEnabled())
        {
            try
            {
                using var r2Timeout = new CancellationTokenSource(R2UploadTimeout);
                if (!await _r2Storage.UploadIfMissingAsync(hash, path, r2Timeout.Token).ConfigureAwait(false))
                {
                    _logger.LogWarning("R2 copy for community media {file} did not complete before ack", hash);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("R2 copy for community media {file} timed out; file remains on local cache", hash);
            }
        }
        _logger.LogInformation("{user}|{file}: Community media stored", uploaderUid, hash);
        return Ok();
    }
}

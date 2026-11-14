using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using SpheneShared.Data;
using SpheneShared.Services;
using SpheneShared.Utils;
using SpheneShared.Utils.Configuration;

namespace SpheneServer.Services;

/// <summary>
/// Lazy, resumable base64→file-server media migration. Scans the media-carrying
/// entities for rows with <c>*Base64 != null</c> and <c>*MediaHash == null</c>,
/// hashes the payload, uploads it to the static file server, records the
/// <c>community_media</c> row and stamps the hash - the base64 column stays
/// filled so nothing breaks mid-migration (dual-write safety).
///
/// Batch-bounded and idempotent: a row already carrying a hash is skipped, so
/// restarts resume where the previous run stopped. Disabled unless
/// <see cref="ServerConfiguration.CommunityMediaBackfillEnabled"/> is set.
/// </summary>
public sealed class CommunityMediaBackfillService : BackgroundService
{
    /// <summary>Rows processed per entity type per sweep - keeps DB + CDN load bounded.</summary>
    private const int BatchSize = 25;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ErrorBackoff = TimeSpan.FromMinutes(5);

    private readonly IDbContextFactory<SpheneDbContext> _dbFactory;
    private readonly IConfigurationService<ServerConfiguration> _config;
    private readonly ServerTokenGenerator _tokenGenerator;
    private readonly ILogger<CommunityMediaBackfillService> _logger;
    private readonly HttpClient _httpClient;

    public CommunityMediaBackfillService(IDbContextFactory<SpheneDbContext> dbFactory,
        IConfigurationService<ServerConfiguration> config,
        ServerTokenGenerator tokenGenerator,
        ILogger<CommunityMediaBackfillService> logger,
        HttpMessageHandler? httpHandler = null)
    {
        _dbFactory = dbFactory;
        _config = config;
        _tokenGenerator = tokenGenerator;
        _logger = logger;
        _httpClient = httpHandler == null ? new HttpClient() : new HttpClient(httpHandler);
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.GetValueOrDefault(nameof(ServerConfiguration.CommunityMediaBackfillEnabled), false))
        {
            _logger.LogInformation("Community media backfill disabled - set CommunityMediaBackfillEnabled to opt in");
            return;
        }

        var cdn = _config.GetValue<Uri>(nameof(ServerConfiguration.CdnFullUrl));
        if (cdn == null)
        {
            _logger.LogWarning("Community media backfill enabled but no CdnFullUrl configured - aborting");
            return;
        }

        var uploadBase = new Uri(cdn, "community/media/").ToString();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var migrated = await SweepAsync(uploadBase, stoppingToken).ConfigureAwait(false);
                if (migrated == 0)
                {
                    await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Media backfill sweep failed - retrying after backoff");
                await Task.Delay(ErrorBackoff, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>One bounded sweep across every media-carrying entity type.</summary>
    private async Task<int> SweepAsync(string uploadBase, CancellationToken token)
    {
        await using var ctx = await _dbFactory.CreateDbContextAsync(token).ConfigureAwait(false);
        var migrated = 0;

        var gallery = await ctx.SyncshellCommunityGallery
            .Where(g => g.ImageBase64 != null && g.ImageMediaHash == null)
            .OrderBy(g => g.Id).Take(BatchSize).ToListAsync(token).ConfigureAwait(false);
        foreach (var g in gallery)
        {
            var hash = await MigrateOneAsync(uploadBase, g.ImageBase64!, token).ConfigureAwait(false);
            if (hash == null) continue;
            g.ImageMediaHash = hash;
            await RecordAsync(ctx, hash, g.UploadedByUid, g.ContentType, g.Size, token).ConfigureAwait(false);
            migrated++;
        }

        var events = await ctx.SyncshellCommunityEvents
            .Where(e => e.ImageBase64 != null && e.ImageMediaHash == null)
            .OrderBy(e => e.Id).Take(BatchSize).ToListAsync(token).ConfigureAwait(false);
        foreach (var e in events)
        {
            var hash = await MigrateOneAsync(uploadBase, e.ImageBase64!, token).ConfigureAwait(false);
            if (hash == null) continue;
            e.ImageMediaHash = hash;
            await RecordAsync(ctx, hash, null, null, null, token).ConfigureAwait(false);
            migrated++;
        }

        var welcome = await ctx.SyncshellWelcomePages
            .Where(w => (w.WelcomeImageBase64 != null && w.WelcomeMediaHash == null)
                || (w.AvatarImageBase64 != null && w.AvatarMediaHash == null))
            .OrderBy(w => w.GroupGID).Take(BatchSize).ToListAsync(token).ConfigureAwait(false);
        foreach (var w in welcome)
        {
            if (w.WelcomeImageBase64 != null && w.WelcomeMediaHash == null)
            {
                var hash = await MigrateOneAsync(uploadBase, w.WelcomeImageBase64, token)
                    .ConfigureAwait(false);
                if (hash != null)
                {
                    w.WelcomeMediaHash = hash;
                    await RecordAsync(ctx, hash, null, w.ImageContentType, w.ImageSize, token)
                        .ConfigureAwait(false);
                    migrated++;
                }
            }
            if (w.AvatarImageBase64 != null && w.AvatarMediaHash == null)
            {
                var hash = await MigrateOneAsync(uploadBase, w.AvatarImageBase64, token)
                    .ConfigureAwait(false);
                if (hash != null)
                {
                    w.AvatarMediaHash = hash;
                    await RecordAsync(ctx, hash, null, w.AvatarContentType, w.AvatarSize, token)
                        .ConfigureAwait(false);
                    migrated++;
                }
            }
        }

        if (migrated > 0)
        {
            await ctx.SaveChangesAsync(token).ConfigureAwait(false);
        }
        return migrated;
    }

    /// <summary>Decode → hash → upload one payload; null on upload failure (retry next sweep).</summary>
    private async Task<string?> MigrateOneAsync(string uploadBase, string base64, CancellationToken token)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            return null;
        }

        var hash = CommunityMediaService.ComputeMediaHash(bytes);
        using var request = new HttpRequestMessage(HttpMethod.Post, uploadBase + hash)
        {
            Content = new ByteArrayContent(bytes)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _tokenGenerator.Token);
        using var response = await _httpClient.SendAsync(request, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogDebug("Media backfill upload failed · {hash} · {status}", hash, response.StatusCode);
            return null;
        }
        return hash;
    }

    private static async Task RecordAsync(SpheneDbContext ctx, string hash, string? uploaderUid,
        string? contentType, long? size, CancellationToken token)
    {
        var existing = await ctx.CommunityMedia
            .SingleOrDefaultAsync(m => m.Hash == hash, token).ConfigureAwait(false);
        if (existing != null)
        {
            existing.RefCount++; // another entity now references this blob
            return;
        }
        ctx.CommunityMedia.Add(new SpheneShared.Models.CommunityMedia
        {
            Hash = hash,
            UploaderUID = uploaderUid,
            ContentType = contentType,
            Size = size ?? 0,
            RefCount = 1,
            CreatedAt = DateTime.UtcNow
        });
    }
}

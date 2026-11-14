using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SpheneShared.Services;
using SpheneShared.Utils.Configuration;

namespace SpheneServer.Services;

/// <summary>
/// Result of validating an inbound community image payload before it is
/// persisted anywhere - either a base64 blob (current storage path) or a
/// file-server media reference (target storage path).
/// </summary>
public sealed record CommunityMediaValidation(
    bool IsValid, string? Error, long ByteSize, string? ContentType, string? MediaHash);

/// <summary>
/// Central validation/translation point for community media. Today every
/// community image travels as base64 inside its DTO and is stored in a
/// Postgres column - this service owns the limits and hashing so the
/// Phase-2 switch to file-server storage only touches one place.
///
/// Binary storage itself is NOT reimplemented here: uploads/downloads go
/// through the existing SpheneStaticFilesServer hash-addressed pipeline
/// (<see cref="SpheneShared.Models.FileCache"/>, /files/upload/{hash}).
/// See docs/COMMUNITY_PLATFORM_MEDIA.md for the migration strategy.
/// </summary>
public class CommunityMediaService
{
    /// <summary>Hard cap for a single community image · base64 inflates ~33%.</summary>
    public const long MaxImageBytes = 2 * 1024 * 1024;

    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/webp", "image/gif"
    };

    /// <summary>Content types accepted for community media (upload + write paths).</summary>
    public static IReadOnlyCollection<string> AllowedTypes => AllowedContentTypes;

    private readonly ILogger<CommunityMediaService> _logger;
    private readonly IConfigurationService<ServerConfiguration> _configuration;

    public CommunityMediaService(ILogger<CommunityMediaService> logger,
        IConfigurationService<ServerConfiguration> configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    /// <summary>
    /// Validates a base64 image payload as supplied by community upsert DTOs.
    /// Does not persist - callers keep writing to their existing column
    /// until the media-reference migration lands.
    /// </summary>
    public CommunityMediaValidation ValidateBase64Image(string? base64, string? contentType)
    {
        if (string.IsNullOrEmpty(base64))
        {
            return new(true, null, 0, contentType, null);
        }

        var byteSize = (long)(base64.Length * 3 / 4.0);
        if (byteSize > MaxImageBytes)
        {
            return new(false, $"Image exceeds the {MaxImageBytes / 1024} KiB limit", byteSize, contentType, null);
        }
        if (contentType != null && !AllowedContentTypes.Contains(contentType))
        {
            return new(false, $"Content type '{contentType}' is not allowed for community media", byteSize, contentType, null);
        }

        var hash = ComputeMediaHash(base64);
        return new(true, null, byteSize, contentType, hash);
    }

    /// <summary>
    /// Content hash for deduplication - the file server hashes the binary
    /// payload for <c>FileCache.Hash</c>, so we decode base64 first; the two
    /// schemes must produce identical hashes for the same image.
    /// </summary>
    public static string ComputeMediaHash(string base64)
    {
        try
        {
            var bytes = Convert.FromBase64String(base64);
            return ComputeMediaHash(bytes);
        }
        catch (FormatException)
        {
            return string.Empty;
        }
    }

    /// <summary>SHA-256 over the raw bytes, truncated to the 40-char file-cache width.</summary>
    public static string ComputeMediaHash(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes), 0, 20);

    /// <summary>40-hex media hash format check - anything else is not a media reference.</summary>
    public static bool ValidateMediaHash(string? mediaHash)
        => !string.IsNullOrEmpty(mediaHash) && mediaHash.Length == 40
            && mediaHash.All(Uri.IsHexDigit);

    /// <summary>
    /// Resolves a media hash to the file-server download URL - the dedicated
    /// unguessable <c>/community/media/{hash}</c> route, or null when no CDN
    /// base is configured.
    /// </summary>
    public Uri? ResolveMediaUrl(string mediaHash)
    {
        var cdn = _configuration.GetValue<Uri>(nameof(ServerConfiguration.CdnFullUrl));
        return cdn == null ? null : new Uri(cdn, $"community/media/{mediaHash}");
    }

    /// <summary>
    /// Registers a hash in <c>community_media</c> - the binding record between
    /// a content hash and the entities that reference it. Idempotent per hash:
    /// the first writer wins; <c>RefCount</c> is recomputed by the GC scan,
    /// not incremented here (see docs/COMMUNITY_PLATFORM_MEDIA.md).
    /// </summary>
    public async Task RecordMediaAsync(SpheneShared.Data.SpheneDbContext dbContext, string mediaHash,
        string uploaderUid, string? contentType, long? size)
    {
        if (!ValidateMediaHash(mediaHash)) return;
        var exists = await dbContext.CommunityMedia
            .AnyAsync(m => m.Hash == mediaHash).ConfigureAwait(false);
        if (exists) return;
        dbContext.CommunityMedia.Add(new SpheneShared.Models.CommunityMedia
        {
            Hash = mediaHash,
            UploaderUID = uploaderUid,
            ContentType = contentType,
            Size = size ?? 0,
            RefCount = 1,
            CreatedAt = DateTime.UtcNow
        });
    }

    /// <summary>Upload/fetch templates reported to clients in
    /// <c>GroupGetCommunityMediaUploadInfo</c> - null when unconfigured.</summary>
    public (Uri? UploadTemplate, Uri? FetchTemplate) GetMediaUrlTemplates()
    {
        var cdn = _configuration.GetValue<Uri>(nameof(ServerConfiguration.CdnFullUrl));
        return cdn == null ? (null, null)
            : (new Uri(cdn, "files/upload/"), new Uri(cdn, "community/media/"));
    }
}

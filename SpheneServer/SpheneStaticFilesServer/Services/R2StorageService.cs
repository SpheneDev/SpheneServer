using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using SpheneShared.Services;
using SpheneShared.Utils.Configuration;
using System.Collections.Concurrent;

namespace SpheneStaticFilesServer.Services;

public sealed class R2StorageService
{
    private readonly ILogger<R2StorageService> _logger;
    private readonly IConfigurationService<StaticFilesServerConfiguration> _configuration;
    private readonly ConcurrentDictionary<string, Task> _uploadsInFlight = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _uploadSemaphore = new(4, 4);

    public R2StorageService(ILogger<R2StorageService> logger, IConfigurationService<StaticFilesServerConfiguration> configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    public Uri? GetPublicObjectUrl(string hash)
    {
        if (!IsEnabled())
        {
            return null;
        }

        var baseUrl = _configuration.GetValueOrDefault<Uri>(nameof(StaticFilesServerConfiguration.R2PublicBaseUrl), null);
        if (baseUrl == null)
        {
            return null;
        }

        var key = BuildObjectKey(hash);
        return new Uri(baseUrl, key);
    }

    public void EnqueueUploadIfEnabled(string hash, string localFilePath)
    {
        if (!IsEnabled())
        {
            return;
        }

        hash = hash.ToUpperInvariant();
        if (_uploadsInFlight.TryAdd(hash, Task.Run(() => UploadAsync(hash, localFilePath))))
        {
            _logger.LogInformation("R2 upload queued: {hash}", hash);
        }
    }

    public async Task<bool> UploadIfMissingAsync(string hash, string localFilePath, CancellationToken ct)
    {
        if (!IsEnabled())
        {
            return false;
        }

        hash = hash.ToUpperInvariant();

        var endpoint = _configuration.GetValueOrDefault<Uri>(nameof(StaticFilesServerConfiguration.R2Endpoint), null);
        var bucket = _configuration.GetValueOrDefault(nameof(StaticFilesServerConfiguration.R2BucketName), string.Empty);
        var accessKeyId = _configuration.GetValueOrDefault(nameof(StaticFilesServerConfiguration.R2AccessKeyId), string.Empty);
        var secretAccessKey = _configuration.GetValueOrDefault(nameof(StaticFilesServerConfiguration.R2SecretAccessKey), string.Empty);
        if (endpoint == null || string.IsNullOrWhiteSpace(bucket) || string.IsNullOrWhiteSpace(accessKeyId) || string.IsNullOrWhiteSpace(secretAccessKey))
        {
            _logger.LogWarning("R2 is enabled but missing configuration values; skipping upload");
            return false;
        }

        if (!File.Exists(localFilePath))
        {
            _logger.LogWarning("R2 upload skipped; local file not found: {path}", localFilePath);
            return false;
        }

        await _uploadSemaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var config = new AmazonS3Config
            {
                ServiceURL = endpoint.ToString().TrimEnd('/'),
                ForcePathStyle = true
            };

            var credentials = new BasicAWSCredentials(accessKeyId, secretAccessKey);
            using var client = new AmazonS3Client(credentials, config);

            var key = BuildObjectKey(hash);
            try
            {
                await client.GetObjectMetadataAsync(new GetObjectMetadataRequest
                {
                    BucketName = bucket,
                    Key = key
                }, ct).ConfigureAwait(false);
                _logger.LogDebug("R2 upload skipped (already exists): {hash} => {bucket}/{key}", hash, bucket, key);
                return false;
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
            }

            _logger.LogInformation("R2 upload starting: {hash} => {bucket}/{key}", hash, bucket, key);
            await client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = bucket,
                Key = key,
                FilePath = localFilePath,
                ContentType = "application/octet-stream",
                DisablePayloadSigning = true
            }, ct).ConfigureAwait(false);

            _logger.LogInformation("R2 upload completed: {hash} => {bucket}/{key}", hash, bucket, key);
            return true;
        }
        finally
        {
            _uploadSemaphore.Release();
        }
    }

    private bool IsEnabled()
    {
        return _configuration.GetValueOrDefault(nameof(StaticFilesServerConfiguration.EnableR2Storage), false);
    }

    private string BuildObjectKey(string hash)
    {
        var prefix = _configuration.GetValueOrDefault(nameof(StaticFilesServerConfiguration.R2KeyPrefix), string.Empty) ?? string.Empty;
        if (!string.IsNullOrEmpty(prefix) && !prefix.EndsWith("/", StringComparison.Ordinal))
        {
            prefix += "/";
        }

        return prefix + hash.ToUpperInvariant();
    }

    private async Task UploadAsync(string hash, string localFilePath)
    {
        try
        {
            await UploadIfMissingAsync(hash, localFilePath, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "R2 upload failed for {hash}", hash);
        }
        finally
        {
            _uploadsInFlight.TryRemove(hash, out _);
        }
    }
}

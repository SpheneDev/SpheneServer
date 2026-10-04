using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using SpheneShared.Services;
using SpheneShared.Utils.Configuration;
using System.Collections.Concurrent;
using System.Net;

namespace SpheneStaticFilesServer.Services;

public sealed class R2StorageService
{
    private readonly ILogger<R2StorageService> _logger;
    private readonly IConfigurationService<StaticFilesServerConfiguration> _configuration;
    private readonly ConcurrentDictionary<string, Task> _uploadsInFlight = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _uploadSemaphore = new(4, 4);
    private readonly Lock _clientLock = new();
    private AmazonS3Client? _client;
    private string? _clientKey;
    private string _bucketName = string.Empty;

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

        if (!TryGetClient(out var client, out var bucket))
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
            var key = BuildObjectKey(hash);
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

    public async Task<bool> TryDownloadToLocalAsync(string hash, string destinationFilePath, CancellationToken ct)
    {
        if (!IsEnabled())
        {
            return false;
        }

        hash = hash.ToUpperInvariant();

        if (!TryGetClient(out var client, out var bucket))
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationFilePath) ?? ".");

        var key = BuildObjectKey(hash);
        try
        {
            _logger.LogInformation("R2 download starting: {hash} => {bucket}/{key}", hash, bucket, key);
            using var getResponse = await client.GetObjectAsync(new GetObjectRequest
            {
                BucketName = bucket,
                Key = key
            }, ct).ConfigureAwait(false);

            var tempFileName = destinationFilePath + ".dl";
            await using (var output = new FileStream(tempFileName, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await getResponse.ResponseStream.CopyToAsync(output, ct).ConfigureAwait(false);
            }
            File.Move(tempFileName, destinationFilePath, true);
            _logger.LogInformation("R2 download completed: {hash} => {path}", hash, destinationFilePath);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogDebug("R2 download skipped (not found): {hash} => {bucket}/{key}", hash, bucket, key);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "R2 download failed for {hash} to {path}", hash, destinationFilePath);
            return false;
        }
    }

    private bool IsEnabled()
    {
        return _configuration.GetValueOrDefault(nameof(StaticFilesServerConfiguration.EnableR2Storage), false);
    }

    private bool TryGetClient(out AmazonS3Client client, out string bucket)
    {
        var endpoint = _configuration.GetValueOrDefault<Uri>(nameof(StaticFilesServerConfiguration.R2Endpoint), null);
        var bucketName = _configuration.GetValueOrDefault(nameof(StaticFilesServerConfiguration.R2BucketName), string.Empty);
        var accessKeyId = _configuration.GetValueOrDefault(nameof(StaticFilesServerConfiguration.R2AccessKeyId), string.Empty);
        var secretAccessKey = _configuration.GetValueOrDefault(nameof(StaticFilesServerConfiguration.R2SecretAccessKey), string.Empty);

        if (endpoint == null || string.IsNullOrWhiteSpace(bucketName) || string.IsNullOrWhiteSpace(accessKeyId) || string.IsNullOrWhiteSpace(secretAccessKey))
        {
            client = null!;
            bucket = string.Empty;
            return false;
        }

        var key = endpoint.ToString().TrimEnd('/') + "|" + bucketName + "|" + accessKeyId + "|" + secretAccessKey;
        lock (_clientLock)
        {
            if (_client != null && string.Equals(_clientKey, key, StringComparison.Ordinal))
            {
                client = _client;
                bucket = _bucketName;
                return true;
            }

            _client?.Dispose();
            _clientKey = key;
            _bucketName = bucketName;
            _client = new AmazonS3Client(new BasicAWSCredentials(accessKeyId, secretAccessKey), new AmazonS3Config
            {
                ServiceURL = endpoint.ToString().TrimEnd('/'),
                ForcePathStyle = true
            });

            client = _client;
            bucket = _bucketName;
            return true;
        }
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

    public async Task<List<R2ObjectInfo>> ListObjectsAsync(CancellationToken ct)
    {
        if (!IsEnabled())
        {
            return [];
        }

        if (!TryGetClient(out var client, out var bucket))
        {
            _logger.LogWarning("R2 is enabled but missing configuration values; skipping list");
            return [];
        }

        var prefix = _configuration.GetValueOrDefault(nameof(StaticFilesServerConfiguration.R2KeyPrefix), string.Empty) ?? string.Empty;
        if (!string.IsNullOrEmpty(prefix) && !prefix.EndsWith("/", StringComparison.Ordinal))
        {
            prefix += "/";
        }

        var objects = new List<R2ObjectInfo>();
        string? continuationToken = null;

        do
        {
            var request = new ListObjectsV2Request
            {
                BucketName = bucket,
                Prefix = prefix,
                MaxKeys = 1000
            };

            if (continuationToken != null)
            {
                request.ContinuationToken = continuationToken;
            }

            var response = await client.ListObjectsV2Async(request, ct).ConfigureAwait(false);

            foreach (var s3Object in response.S3Objects)
            {
                objects.Add(new R2ObjectInfo
                {
                    Key = s3Object.Key,
                    Size = s3Object.Size,
                    LastModified = s3Object.LastModified
                });
            }

            continuationToken = response.IsTruncated ? response.NextContinuationToken : null;
        }
        while (continuationToken != null && !ct.IsCancellationRequested);

        _logger.LogInformation("R2 list completed: {count} objects in bucket {bucket}", objects.Count, bucket);
        return objects;
    }

    public async Task<bool> DeleteObjectByHashAsync(string hash, CancellationToken ct)
    {
        if (!IsEnabled())
        {
            return false;
        }

        var key = BuildObjectKey(hash.ToUpperInvariant());
        return await DeleteObjectAsync(key, ct).ConfigureAwait(false);
    }

    public async Task<bool> DeleteObjectAsync(string key, CancellationToken ct)
    {
        if (!IsEnabled())
        {
            return false;
        }

        if (!TryGetClient(out var client, out var bucket))
        {
            return false;
        }

        try
        {
            await client.DeleteObjectAsync(new DeleteObjectRequest
            {
                BucketName = bucket,
                Key = key
            }, ct).ConfigureAwait(false);

            _logger.LogInformation("R2 object deleted: {bucket}/{key}", bucket, key);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "R2 delete failed for {bucket}/{key}", bucket, key);
            return false;
        }
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

public sealed class R2ObjectInfo
{
    public string Key { get; set; } = string.Empty;
    public long Size { get; set; }
    public DateTime LastModified { get; set; }
}

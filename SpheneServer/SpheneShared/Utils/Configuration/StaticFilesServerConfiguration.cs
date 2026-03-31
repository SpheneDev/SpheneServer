using System.Text;

namespace SpheneShared.Utils.Configuration;

public class StaticFilesServerConfiguration : SpheneConfigurationBase
{
    public bool IsDistributionNode { get; set; } = false;
    public Uri MainFileServerAddress { get; set; } = null;
    public Uri DistributionFileServerAddress { get; set; } = null;
    public int ForcedDeletionOfFilesAfterHours { get; set; } = -1;
    public double CacheSizeHardLimitInGiB { get; set; } = -1;
    public int UnusedFileRetentionPeriodInDays { get; set; } = 14;
    public string CacheDirectory { get; set; }
    public int DownloadQueueSize { get; set; } = 50;
    public int DownloadTimeoutSeconds { get; set; } = 5;
    public int DownloadQueueReleaseSeconds { get; set; } = 15;
    public int DownloadQueueClearLimit { get; set; } = 15000;
    public int CleanupCheckInMinutes { get; set; } = 15;
    public bool UseColdStorage { get; set; } = false;
    public string ColdStorageDirectory { get; set; } = null;
    public double ColdStorageSizeHardLimitInGiB { get; set; } = -1;
    public int ColdStorageUnusedFileRetentionPeriodInDays { get; set; } = 30;
    [RemoteConfiguration]
    public double SpeedTestHoursRateLimit { get; set; } = 0.5;
    [RemoteConfiguration]
    public Uri CdnFullUrl { get; set; } = null;
    [RemoteConfiguration]
    public Uri FileServerFallbackAddress { get; set; } = null;

    public bool EnableR2Storage { get; set; } = false;
    public Uri R2Endpoint { get; set; } = null;
    public string R2BucketName { get; set; } = string.Empty;
    public string R2AccessKeyId { get; set; } = string.Empty;
    public string R2SecretAccessKey { get; set; } = string.Empty;
    [RemoteConfiguration]
    public Uri R2PublicBaseUrl { get; set; } = null;
    public string R2KeyPrefix { get; set; } = "files";
    public bool R2RetainDatabaseEntries { get; set; } = true;
    public bool EnableR2BackfillOnStartup { get; set; } = false;
    public int R2BackfillMaxFilesPerStartup { get; set; } = 0;
    public int R2BackfillParallelism { get; set; } = 4;
    public ShardConfiguration? ShardConfiguration { get; set; } = null;
    public override string ToString()
    {
        StringBuilder sb = new();
        sb.AppendLine(base.ToString());
        sb.AppendLine($"{nameof(MainFileServerAddress)} => {MainFileServerAddress}");
        sb.AppendLine($"{nameof(ForcedDeletionOfFilesAfterHours)} => {ForcedDeletionOfFilesAfterHours}");
        sb.AppendLine($"{nameof(CacheSizeHardLimitInGiB)} => {CacheSizeHardLimitInGiB}");
        sb.AppendLine($"{nameof(UseColdStorage)} => {UseColdStorage}");
        sb.AppendLine($"{nameof(ColdStorageDirectory)} => {ColdStorageDirectory}");
        sb.AppendLine($"{nameof(ColdStorageSizeHardLimitInGiB)} => {ColdStorageSizeHardLimitInGiB}");
        sb.AppendLine($"{nameof(ColdStorageUnusedFileRetentionPeriodInDays)} => {ColdStorageUnusedFileRetentionPeriodInDays}");
        sb.AppendLine($"{nameof(UnusedFileRetentionPeriodInDays)} => {UnusedFileRetentionPeriodInDays}");
        sb.AppendLine($"{nameof(CacheDirectory)} => {CacheDirectory}");
        sb.AppendLine($"{nameof(DownloadQueueSize)} => {DownloadQueueSize}");
        sb.AppendLine($"{nameof(DownloadQueueReleaseSeconds)} => {DownloadQueueReleaseSeconds}");
        sb.AppendLine($"{nameof(EnableR2Storage)} => {EnableR2Storage}");
        sb.AppendLine($"{nameof(EnableR2BackfillOnStartup)} => {EnableR2BackfillOnStartup}");
        sb.AppendLine($"{nameof(R2BackfillMaxFilesPerStartup)} => {R2BackfillMaxFilesPerStartup}");
        sb.AppendLine($"{nameof(R2BackfillParallelism)} => {R2BackfillParallelism}");
        return sb.ToString();
    }
}

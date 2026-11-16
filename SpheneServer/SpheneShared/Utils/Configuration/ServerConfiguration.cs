using System.Text;

namespace SpheneShared.Utils.Configuration;

public class ServerConfiguration : SpheneConfigurationBase
{
    [RemoteConfiguration]
    public Uri CdnFullUrl { get; set; } = null;
    /// <summary>Opt-in switch for the lazy base64→CDN media backfill worker.</summary>
    public bool CommunityMediaBackfillEnabled { get; set; } = false;

    [RemoteConfiguration]
    public Uri FileServerFallbackAddress { get; set; } = null;

    [RemoteConfiguration]
    public Version ExpectedClientVersion { get; set; } = new Version(0, 0, 0);

    [RemoteConfiguration]
    public Version MinimumClientVersion { get; set; } = new Version(0, 0, 0);

    [RemoteConfiguration]
    public int MaxExistingGroupsByUser { get; set; } = 3;

    [RemoteConfiguration]
    public int MaxGroupUserCount { get; set; } = 100;

    [RemoteConfiguration]
    public int MaxJoinedGroupsByUser { get; set; } = 6;

    [RemoteConfiguration]
    public bool PurgeUnusedAccounts { get; set; } = false;

    [RemoteConfiguration]
    public int PurgeUnusedAccountsPeriodInDays { get; set; } = 14;

    [RemoteConfiguration]
    public int MaxCharaDataByUser { get; set; } = 10;

    [RemoteConfiguration]
    public int MaxCharaDataByUserVanity { get; set; } = 50;

    [RemoteConfiguration]
    public bool EnableSupporterFeatures { get; set; } = false;

    [RemoteConfiguration]
    public string SupporterIconDirectory { get; set; } = "supporter-icons";
    /// <summary>Full-chat lockdown: when true the plaintext
    /// UserSendChatMessage endpoint rejects every send. Leave off during
    /// rollout - sealed-capable recipients already enforce E2EE per-target.</summary>
    [RemoteConfiguration]
    public bool RequireE2EEChat { get; set; } = false;

    [RemoteConfiguration]
    public int ChatE2EEMaxDevicesPerUser { get; set; } = 8;

    public bool RunPermissionCleanupOnStartup { get; set; } = true;
    public int HubExecutionConcurrencyFilter { get; set; } = 50;

    public override string ToString()
    {
        StringBuilder sb = new();
        sb.AppendLine(base.ToString());
        sb.AppendLine($"{nameof(CdnFullUrl)} => {CdnFullUrl}");
        sb.AppendLine($"{nameof(FileServerFallbackAddress)} => {FileServerFallbackAddress}");
        sb.AppendLine($"{nameof(RedisConnectionString)} => {RedisConnectionString}");
        sb.AppendLine($"{nameof(ExpectedClientVersion)} => {ExpectedClientVersion}");
        sb.AppendLine($"{nameof(MaxExistingGroupsByUser)} => {MaxExistingGroupsByUser}");
        sb.AppendLine($"{nameof(MaxJoinedGroupsByUser)} => {MaxJoinedGroupsByUser}");
        sb.AppendLine($"{nameof(MaxGroupUserCount)} => {MaxGroupUserCount}");
        sb.AppendLine($"{nameof(PurgeUnusedAccounts)} => {PurgeUnusedAccounts}");
        sb.AppendLine($"{nameof(PurgeUnusedAccountsPeriodInDays)} => {PurgeUnusedAccountsPeriodInDays}");
        sb.AppendLine($"{nameof(EnableSupporterFeatures)} => {EnableSupporterFeatures}");
        sb.AppendLine($"{nameof(SupporterIconDirectory)} => {SupporterIconDirectory}");
        sb.AppendLine($"{nameof(RunPermissionCleanupOnStartup)} => {RunPermissionCleanupOnStartup}");
        sb.AppendLine($"{nameof(HubExecutionConcurrencyFilter)} => {HubExecutionConcurrencyFilter}");
        return sb.ToString();
    }
}

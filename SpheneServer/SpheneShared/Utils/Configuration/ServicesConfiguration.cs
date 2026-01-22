using System.Text;

namespace SpheneShared.Utils.Configuration;

public class ServicesConfiguration : SpheneConfigurationBase
{
    public const string DefaultDiscordChangelogUrl = "https://sphene.online/sphene/changelog.json";
    public const string DefaultDiscordPluginMasterUrl = "https://raw.githubusercontent.com/SpheneDev/repo/refs/heads/main/plogonmaster.json";

    public string DiscordBotToken { get; set; } = string.Empty;
    public ulong? DiscordChannelForMessages { get; set; } = null;
    public ulong? DiscordChannelForCommands { get; set; } = null;
    public ulong? DiscordRoleAprilFools2024 { get; set; } = null;
    public ulong? DiscordChannelForBotLog { get; set; } = null!;
    public ulong? DiscordChannelForReleaseChangelogs { get; set; } = null;
    public ulong? DiscordChannelForTestBuildChangelogs { get; set; } = null;
    public ulong? DiscordRoleRegistered { get; set; } = null!;
    public bool KickNonRegisteredUsers { get; set; } = false;
    public string DiscordChangelogUrl { get; set; } = DefaultDiscordChangelogUrl;
    public string DiscordPluginMasterUrl { get; set; } = DefaultDiscordPluginMasterUrl;
    public Uri MainServerAddress { get; set; } = null;
    public Dictionary<ulong, string> VanityRoles { get; set; } = new Dictionary<ulong, string>();

    public override string ToString()
    {
        StringBuilder sb = new();
        sb.AppendLine(base.ToString());
        sb.AppendLine($"{nameof(DiscordBotToken)} => {(string.IsNullOrWhiteSpace(DiscordBotToken) ? "<empty>" : "<redacted>")}");
        sb.AppendLine($"{nameof(MainServerAddress)} => {MainServerAddress}");
        sb.AppendLine($"{nameof(DiscordChannelForMessages)} => {DiscordChannelForMessages}");
        sb.AppendLine($"{nameof(DiscordChannelForCommands)} => {DiscordChannelForCommands}");
        sb.AppendLine($"{nameof(DiscordRoleAprilFools2024)} => {DiscordRoleAprilFools2024}");
        sb.AppendLine($"{nameof(DiscordChannelForReleaseChangelogs)} => {DiscordChannelForReleaseChangelogs}");
        sb.AppendLine($"{nameof(DiscordChannelForTestBuildChangelogs)} => {DiscordChannelForTestBuildChangelogs}");
        sb.AppendLine($"{nameof(DiscordRoleRegistered)} => {DiscordRoleRegistered}");
        sb.AppendLine($"{nameof(KickNonRegisteredUsers)} => {KickNonRegisteredUsers}");
        sb.AppendLine($"{nameof(DiscordChangelogUrl)} => {DiscordChangelogUrl}");
        sb.AppendLine($"{nameof(DiscordPluginMasterUrl)} => {DiscordPluginMasterUrl}");
        foreach (var role in VanityRoles)
        {
            sb.AppendLine($"{nameof(VanityRoles)} => {role.Key} = {role.Value}");
        }
        return sb.ToString();
    }
}

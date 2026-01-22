using Discord;
using Discord.Interactions;
using Discord.Rest;
using Discord.WebSocket;
using SpheneShared.Data;
using SpheneShared.Models;
using SpheneShared.Services;
using SpheneShared.Utils.Configuration;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json.Linq;
using StackExchange.Redis;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SpheneServices.Discord;

internal class DiscordBot : IHostedService
{
    private readonly DiscordBotServices _botServices;
    private readonly IConfigurationService<ServicesConfiguration> _configurationService;
    private readonly IConnectionMultiplexer _connectionMultiplexer;
    private readonly DiscordSocketClient _discordClient;
    private readonly ILogger<DiscordBot> _logger;
    private readonly IDbContextFactory<SpheneDbContext> _dbContextFactory;
    private readonly IServiceProvider _services;
    private InteractionService _interactionModule;
    private readonly CancellationTokenSource? _processReportQueueCts;
    private CancellationTokenSource? _clientConnectedCts;
    private readonly HttpClient _httpClient = new();
    private static readonly TimeSpan ChangelogPollInterval = TimeSpan.FromSeconds(30);
    private static readonly RedisKey LastPostedReleaseKey = new RedisKey("discord:changelog:lastPosted:release");
    private static readonly RedisKey LastPostedTestBuildKey = new RedisKey("discord:changelog:lastPosted:testbuild");
    private static readonly RedisKey LastPostedReleaseHashKey = new RedisKey("discord:changelog:lastPostedHash:release");
    private static readonly RedisKey LastPostedTestBuildHashKey = new RedisKey("discord:changelog:lastPostedHash:testbuild");
    private static readonly RedisKey LastPostedReleaseMessageIdKey = new RedisKey("discord:changelog:lastPostedMessageId:release");
    private static readonly RedisKey LastPostedTestBuildMessageIdKey = new RedisKey("discord:changelog:lastPostedMessageId:testbuild");

    public DiscordBot(DiscordBotServices botServices, IServiceProvider services, IConfigurationService<ServicesConfiguration> configuration,
        IDbContextFactory<SpheneDbContext> dbContextFactory,
        ILogger<DiscordBot> logger, IConnectionMultiplexer connectionMultiplexer)
    {
        _botServices = botServices;
        _services = services;
        _configurationService = configuration;
        _dbContextFactory = dbContextFactory;
        _logger = logger;
        _connectionMultiplexer = connectionMultiplexer;
        _discordClient = new(new DiscordSocketConfig()
        {
            DefaultRetryMode = RetryMode.AlwaysRetry,
            GatewayIntents = GatewayIntents.AllUnprivileged | GatewayIntents.GuildMembers
        });

        _discordClient.Log += Log;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var token = _configurationService.GetValueOrDefault(nameof(ServicesConfiguration.DiscordBotToken), string.Empty);
        token = token.Trim().Trim('"');
        if (token.StartsWith("Bot ", StringComparison.OrdinalIgnoreCase))
        {
            token = token[4..].Trim();
        }
        if (!string.IsNullOrEmpty(token))
        {
            _logger.LogInformation("Starting DiscordBot");
            _logger.LogInformation("Using Configuration: " + _configurationService.ToString());

            _interactionModule?.Dispose();
            _interactionModule = new InteractionService(_discordClient);
            _interactionModule.Log += Log;
            await _interactionModule.AddModuleAsync(typeof(SpheneModule), _services).ConfigureAwait(false);
            await _interactionModule.AddModuleAsync(typeof(SpheneWizardModule), _services).ConfigureAwait(false);

            await _discordClient.LoginAsync(TokenType.Bot, token).ConfigureAwait(false);
            await _discordClient.StartAsync().ConfigureAwait(false);

            _discordClient.Ready += DiscordClient_Ready;
            _discordClient.InteractionCreated += async (x) =>
            {
                var ctx = new SocketInteractionContext(_discordClient, x);
                await _interactionModule.ExecuteCommandAsync(ctx, _services).ConfigureAwait(false);
            };
            _discordClient.UserJoined += OnUserJoined;

            await _botServices.Start().ConfigureAwait(false);
        }
    }

    private async Task OnUserJoined(SocketGuildUser arg)
    {
        try
        {
            using SpheneDbContext dbContext = await _dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);
            var alreadyRegistered = await dbContext.LodeStoneAuth.AnyAsync(u => u.DiscordId == arg.Id).ConfigureAwait(false);
            if (alreadyRegistered)
            {
                await _botServices.AddRegisteredRoleAsync(arg).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to set user role on join");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(_configurationService.GetValueOrDefault(nameof(ServicesConfiguration.DiscordBotToken), string.Empty)))
        {
            await _botServices.Stop().ConfigureAwait(false);
            _processReportQueueCts?.Cancel();
            _clientConnectedCts?.Cancel();

            await _discordClient.LogoutAsync().ConfigureAwait(false);
            await _discordClient.StopAsync().ConfigureAwait(false);
            _interactionModule?.Dispose();
        }

        _httpClient.Dispose();
    }

    private async Task DiscordClient_Ready()
    {
        var guild = (await _discordClient.Rest.GetGuildsAsync().ConfigureAwait(false)).First();
        await _interactionModule.RegisterCommandsToGuildAsync(guild.Id, true).ConfigureAwait(false);
        _clientConnectedCts?.Cancel();
        _clientConnectedCts?.Dispose();
        _clientConnectedCts = new();
        _ = UpdateStatusAsync(_clientConnectedCts.Token);

        await CreateOrUpdateModal(guild).ConfigureAwait(false);
        _botServices.UpdateGuild(guild);
        await _botServices.LogToChannel("Bot startup complete.").ConfigureAwait(false);
        _ = UpdateVanityRoles(guild, _clientConnectedCts.Token);
        _ = RemoveUsersNotInVanityRole(_clientConnectedCts.Token);
        _ = RemoveUnregisteredUsers(_clientConnectedCts.Token);
        _ = MonitorChangelogPostsAsync(_clientConnectedCts.Token);
    }

    private async Task UpdateVanityRoles(RestGuild guild, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation("Updating Vanity Roles");
                Dictionary<ulong, string> vanityRoles = _configurationService.GetValueOrDefault(nameof(ServicesConfiguration.VanityRoles), new Dictionary<ulong, string>());
                if (vanityRoles.Keys.Count != _botServices.VanityRoles.Count)
                {
                    _botServices.VanityRoles.Clear();
                    foreach (var role in vanityRoles)
                    {
                        _logger.LogInformation("Adding Role: {id} => {desc}", role.Key, role.Value);

                        var restrole = guild.GetRole(role.Key);
                        if (restrole != null)
                            _botServices.VanityRoles[restrole] = role.Value;
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during UpdateVanityRoles");
            }
        }
    }

    private async Task CreateOrUpdateModal(RestGuild guild)
    {
        _logger.LogInformation("Creating Wizard: Getting Channel");

        var discordChannelForCommands = _configurationService.GetValue<ulong?>(nameof(ServicesConfiguration.DiscordChannelForCommands));
        if (discordChannelForCommands == null)
        {
            _logger.LogWarning("Creating Wizard: No channel configured");
            return;
        }

        IUserMessage? message = null;
        var socketchannel = await _discordClient.GetChannelAsync(discordChannelForCommands.Value).ConfigureAwait(false) as SocketTextChannel;
        var pinnedMessages = await socketchannel.GetPinnedMessagesAsync().ConfigureAwait(false);
        foreach (var msg in pinnedMessages)
        {
            _logger.LogInformation("Creating Wizard: Checking message id {id}, author is: {author}, hasEmbeds: {embeds}", msg.Id, msg.Author.Id, msg.Embeds.Any());
            if (msg.Author.Id == _discordClient.CurrentUser.Id
                && msg.Embeds.Any())
            {
                message = await socketchannel.GetMessageAsync(msg.Id).ConfigureAwait(false) as IUserMessage;
                break;
            }
        }

        _logger.LogInformation("Creating Wizard: Found message id: {id}", message?.Id ?? 0);

        await GenerateOrUpdateWizardMessage(socketchannel, message).ConfigureAwait(false);
    }

    private async Task GenerateOrUpdateWizardMessage(SocketTextChannel channel, IUserMessage? prevMessage)
    {
        EmbedBuilder eb = new EmbedBuilder();
        eb.WithTitle("Sphene Network Terminal Interface");
        eb.WithDescription("Press \"Start\" to access the terminal!" + Environment.NewLine + Environment.NewLine
            + "Welcome to the Sphene Network. Through this interface, you can manage your connection to the electrope network and synchronize with other souls across the realm. Follow the prompts to proceed.");
        eb.WithThumbnailUrl("https://raw.githubusercontent.com/SpheneDev/repo/main/Sphene/images/icon.png");
        var cb = new ComponentBuilder();
        cb.WithButton("Start", style: ButtonStyle.Primary, customId: "wizard-captcha:true", emote: Emoji.Parse("➡️"));
        if (prevMessage == null)
        {
            var msg = await channel.SendMessageAsync(embed: eb.Build(), components: cb.Build()).ConfigureAwait(false);
            try
            {
                await msg.PinAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // swallow
            }
        }
        else
        {
            await prevMessage.ModifyAsync(p =>
            {
                p.Embed = eb.Build();
                p.Components = cb.Build();
            }).ConfigureAwait(false);
        }
    }

    private Task Log(LogMessage msg)
    {
        switch (msg.Severity)
        {
            case LogSeverity.Critical:
            case LogSeverity.Error:
                _logger.LogError(msg.Exception, msg.Message); break;
            case LogSeverity.Warning:
                _logger.LogWarning(msg.Exception, msg.Message); break;
            default:
                _logger.LogInformation(msg.Message); break;
        }

        return Task.CompletedTask;
    }

    private async Task RemoveUnregisteredUsers(CancellationToken token)
    {
        var guild = (await _discordClient.Rest.GetGuildsAsync().ConfigureAwait(false)).First();
        while (!token.IsCancellationRequested)
        {
            try
            {
                await ProcessUserRoles(guild, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // do nothing
            }
            catch (Exception ex)
            {
                await _botServices.LogToChannel($"Error during user procesing: {ex.Message}").ConfigureAwait(false);
            }

            await Task.Delay(TimeSpan.FromDays(1)).ConfigureAwait(false);
        }
    }

    private async Task ProcessUserRoles(RestGuild guild, CancellationToken token)
    {
        using SpheneDbContext dbContext = await _dbContextFactory.CreateDbContextAsync(token).ConfigureAwait(false);
        var roleId = _configurationService.GetValueOrDefault<ulong?>(nameof(ServicesConfiguration.DiscordRoleRegistered), 0);
        var kickUnregistered = _configurationService.GetValueOrDefault(nameof(ServicesConfiguration.KickNonRegisteredUsers), false);
        if (roleId == null) return;

        var registrationRole = guild.Roles.FirstOrDefault(f => f.Id == roleId.Value);
        var registeredUsers = new HashSet<ulong>(await dbContext.LodeStoneAuth.AsNoTracking().Select(c => c.DiscordId).ToListAsync().ConfigureAwait(false));

        var executionStartTime = DateTimeOffset.UtcNow;

        int processedUsers = 0;
        int addedRoles = 0;
        int kickedUsers = 0;
        int totalRoles = 0;
        int toRemoveUsers = 0;
        int freshUsers = 0;

        await _botServices.LogToChannel($"Starting to process registered users: Adding Role {registrationRole.Name}. Kick Stale Unregistered: {kickUnregistered}.").ConfigureAwait(false);

        await foreach (var userList in guild.GetUsersAsync(new RequestOptions { CancelToken = token }).ConfigureAwait(false))
        {
            _logger.LogInformation("Processing chunk of {count} users, total processed: {proc}, total roles: {total}, roles added: {added}, users kicked: {kicked}, users plan to kick: {planToKick}, fresh user: {fresh}",
                userList.Count, processedUsers, totalRoles + addedRoles, addedRoles, kickedUsers, toRemoveUsers, freshUsers);
            foreach (var user in userList)
            {
                if (user.IsBot) continue;

                if (registeredUsers.Contains(user.Id))
                {
                    bool roleAdded = await _botServices.AddRegisteredRoleAsync(user, registrationRole).ConfigureAwait(false);
                    if (roleAdded) addedRoles++;
                    else totalRoles++;
                }
                else
                {
                    if ((executionStartTime - user.JoinedAt.Value).TotalDays > 7)
                    {
                        if (kickUnregistered)
                        {
                            await _botServices.KickUserAsync(user).ConfigureAwait(false);
                            kickedUsers++;
                        }
                        else
                        {
                            toRemoveUsers++;
                        }
                    }
                    else
                    {
                        freshUsers++;
                    }
                }

                token.ThrowIfCancellationRequested();
                processedUsers++;
            }
        }

        await _botServices.LogToChannel($"Processing registered users finished. Processed {processedUsers} users, added {addedRoles} roles and kicked {kickedUsers} users").ConfigureAwait(false);
    }

    private async Task RemoveUsersNotInVanityRole(CancellationToken token)
    {
        var guild = (await _discordClient.Rest.GetGuildsAsync().ConfigureAwait(false)).First();

        while (!token.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation($"Cleaning up Vanity UIDs");
                await _botServices.LogToChannel("Cleaning up Vanity UIDs").ConfigureAwait(false);
                _logger.LogInformation("Getting rest guild {guildName}", guild.Name);
                var restGuild = await _discordClient.Rest.GetGuildAsync(guild.Id).ConfigureAwait(false);

                Dictionary<ulong, string> allowedRoleIds = _configurationService.GetValueOrDefault(nameof(ServicesConfiguration.VanityRoles), new Dictionary<ulong, string>());
                _logger.LogInformation($"Allowed role ids: {string.Join(", ", allowedRoleIds)}");

                if (allowedRoleIds.Any())
                {
                    using var db = await _dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);

                    var aliasedUsers = await db.LodeStoneAuth.Include("User")
                        .Where(c => c.User != null && !string.IsNullOrEmpty(c.User.Alias)).ToListAsync().ConfigureAwait(false);
                    var aliasedGroups = await db.Groups.Include(u => u.Owner)
                        .Where(c => !string.IsNullOrEmpty(c.Alias)).ToListAsync().ConfigureAwait(false);

                    foreach (var lodestoneAuth in aliasedUsers)
                    {
                        await CheckVanityForUser(restGuild, allowedRoleIds, db, lodestoneAuth, token).ConfigureAwait(false);

                        await Task.Delay(1000, token).ConfigureAwait(false);
                    }

                    foreach (var group in aliasedGroups)
                    {
                        await CheckVanityForGroup(restGuild, allowedRoleIds, db, group, token).ConfigureAwait(false);

                        await Task.Delay(1000, token).ConfigureAwait(false);
                    }
                }
                else
                {
                    _logger.LogInformation("No roles for command defined, no cleanup performed");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Something failed during checking vanity user uids");
            }

            _logger.LogInformation("Vanity UID cleanup complete");
            await Task.Delay(TimeSpan.FromHours(12), token).ConfigureAwait(false);
        }
    }

    private async Task CheckVanityForGroup(RestGuild restGuild, Dictionary<ulong, string> allowedRoleIds, SpheneDbContext db, Group group, CancellationToken token)
    {
        // Skip system-owned public syncshells (protect public city syncshells)
        if (group.OwnerUID == "SYS_PUBSN")
        {
            _logger.LogDebug($"Skipping system-owned public syncshell: {group.GID} [{group.Alias}]");
            return;
        }

        var groupPrimaryUser = group.OwnerUID;
        var groupOwner = await db.Auth.Include(u => u.User).SingleOrDefaultAsync(u => u.UserUID == group.OwnerUID).ConfigureAwait(false);
        if (groupOwner != null && !string.IsNullOrEmpty(groupOwner.PrimaryUserUID))
        {
            groupPrimaryUser = groupOwner.PrimaryUserUID;
        }

        var lodestoneUser = await db.LodeStoneAuth.Include(u => u.User).SingleOrDefaultAsync(f => f.User.UID == groupPrimaryUser).ConfigureAwait(false);
        RestGuildUser discordUser = null;
        if (lodestoneUser != null)
        {
            discordUser = await restGuild.GetUserAsync(lodestoneUser.DiscordId).ConfigureAwait(false);
        }

        _logger.LogInformation($"Checking Group: {group.GID} [{group.Alias}], owned by {group.OwnerUID} ({groupPrimaryUser}), User in Roles: {string.Join(", ", discordUser?.RoleIds ?? new List<ulong>())}");

        if (lodestoneUser == null || discordUser == null || !discordUser.RoleIds.Any(allowedRoleIds.Keys.Contains))
        {
            await _botServices.LogToChannel($"VANITY GID REMOVAL: <@{lodestoneUser?.DiscordId ?? 0}> ({lodestoneUser?.User?.UID}) - GID: {group.GID}, Vanity: {group.Alias}").ConfigureAwait(false);

            _logger.LogInformation($"User {lodestoneUser?.User?.UID ?? "unknown"} not in allowed roles, deleting group alias for {group.GID}");
            group.Alias = null;
            db.Update(group);
            await db.SaveChangesAsync(token).ConfigureAwait(false);
        }
    }

    private async Task CheckVanityForUser(RestGuild restGuild, Dictionary<ulong, string> allowedRoleIds, SpheneDbContext db, LodeStoneAuth lodestoneAuth, CancellationToken token)
    {
        var discordUser = await restGuild.GetUserAsync(lodestoneAuth.DiscordId).ConfigureAwait(false);
        _logger.LogInformation($"Checking User: {lodestoneAuth.DiscordId}, {lodestoneAuth.User.UID} ({lodestoneAuth.User.Alias}), User in Roles: {string.Join(", ", discordUser?.RoleIds ?? new List<ulong>())}");

        if (discordUser == null || !discordUser.RoleIds.Any(u => allowedRoleIds.Keys.Contains(u)))
        {
            _logger.LogInformation($"User {lodestoneAuth.User.UID} not in allowed roles, deleting alias");
            await _botServices.LogToChannel($"VANITY UID REMOVAL: <@{lodestoneAuth.DiscordId}> - UID: {lodestoneAuth.User.UID}, Vanity: {lodestoneAuth.User.Alias}").ConfigureAwait(false);
            lodestoneAuth.User.Alias = null;
            var secondaryUsers = await db.Auth.Include(u => u.User).Where(u => u.PrimaryUserUID == lodestoneAuth.User.UID).ToListAsync().ConfigureAwait(false);
            foreach (var secondaryUser in secondaryUsers)
            {
                _logger.LogInformation($"Secondary User {secondaryUser.User.UID} not in allowed roles, deleting alias");

                secondaryUser.User.Alias = null;
                db.Update(secondaryUser.User);
            }
            db.Update(lodestoneAuth.User);
            await db.SaveChangesAsync(token).ConfigureAwait(false);
        }
    }

    private async Task UpdateStatusAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var endPoint = _connectionMultiplexer.GetEndPoints().First();
            long onlineUsers = 0;
            await foreach (var _ in _connectionMultiplexer.GetServer(endPoint).KeysAsync(pattern: "UID:*").WithCancellation(token))
            {
                onlineUsers++;
            }

            _logger.LogInformation("Users online: " + onlineUsers);
            await _discordClient.SetActivityAsync(new CustomStatusGame("Your Registration Hub")).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }

    private async Task MonitorChangelogPostsAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await CheckAndPostChangelogAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Changelog monitor failed");
            }

            await Task.Delay(ChangelogPollInterval, token).ConfigureAwait(false);
        }
    }

    private async Task CheckAndPostChangelogAsync(CancellationToken token)
    {
        var releaseChannelId = _configurationService.GetValueOrDefault<ulong?>(nameof(ServicesConfiguration.DiscordChannelForReleaseChangelogs), null);
        var testBuildChannelId = _configurationService.GetValueOrDefault<ulong?>(nameof(ServicesConfiguration.DiscordChannelForTestBuildChangelogs), null);
        if (releaseChannelId == null && testBuildChannelId == null)
        {
            return;
        }

        var pluginMaster = await TryFetchPluginMasterAsync(token).ConfigureAwait(false);
        if (pluginMaster == null)
        {
            return;
        }

        var release = pluginMaster.Value.Release;
        var testBuild = pluginMaster.Value.TestBuild;

        using var changelogDoc = await TryFetchChangelogJsonAsync(token).ConfigureAwait(false);
        if (changelogDoc == null)
        {
            return;
        }

        if (releaseChannelId != null && release.Version != null && release.DownloadUrl != null)
        {
            await TryPostSingleChangelogAsync(
                channelId: releaseChannelId.Value,
                changelogDoc: changelogDoc,
                expectedVersion: release.Version,
                expectedIsPrerelease: false,
                expectedDownloadUrl: release.DownloadUrl,
                lastPostedRedisKey: LastPostedReleaseKey,
                lastPostedHashRedisKey: LastPostedReleaseHashKey,
                lastPostedMessageIdRedisKey: LastPostedReleaseMessageIdKey,
                token).ConfigureAwait(false);
        }

        if (testBuildChannelId != null && testBuild.Version != null && testBuild.DownloadUrl != null)
        {
            await TryPostSingleChangelogAsync(
                channelId: testBuildChannelId.Value,
                changelogDoc: changelogDoc,
                expectedVersion: testBuild.Version,
                expectedIsPrerelease: true,
                expectedDownloadUrl: testBuild.DownloadUrl,
                lastPostedRedisKey: LastPostedTestBuildKey,
                lastPostedHashRedisKey: LastPostedTestBuildHashKey,
                lastPostedMessageIdRedisKey: LastPostedTestBuildMessageIdKey,
                token).ConfigureAwait(false);
        }
    }

    private async Task TryPostSingleChangelogAsync(
        ulong channelId,
        JsonDocument changelogDoc,
        string expectedVersion,
        bool expectedIsPrerelease,
        string expectedDownloadUrl,
        RedisKey lastPostedRedisKey,
        RedisKey lastPostedHashRedisKey,
        RedisKey lastPostedMessageIdRedisKey,
        CancellationToken token)
    {
        if (!TryFindChangelogEntry(changelogDoc, expectedVersion, expectedIsPrerelease, out var entry))
        {
            return;
        }

        var buildAvailable = await IsUrlAvailableAsync(expectedDownloadUrl, token).ConfigureAwait(false);
        if (!buildAvailable)
        {
            return;
        }

        var channel = await _discordClient.GetChannelAsync(channelId).ConfigureAwait(false) as IMessageChannel;
        if (channel == null)
        {
            return;
        }

        var db = _connectionMultiplexer.GetDatabase();
        var expectedEmbedTitle = $"{(expectedIsPrerelease ? "Sphene Testbuild" : "Sphene Release")} {expectedVersion}";
        var expectedFooterPrefix = BuildChangelogFooterPrefix(expectedVersion, expectedIsPrerelease);
        var (embed, fingerprint) = BuildChangelogEmbedWithFingerprint(entry, expectedVersion, expectedIsPrerelease);

        var lastPosted = await db.StringGetAsync(lastPostedRedisKey).ConfigureAwait(false);
        var lastPostedHash = await db.StringGetAsync(lastPostedHashRedisKey).ConfigureAwait(false);
        var lastPostedMessageIdRaw = await db.StringGetAsync(lastPostedMessageIdRedisKey).ConfigureAwait(false);

        if (!lastPosted.IsNullOrEmpty && string.Equals(lastPosted.ToString(), expectedVersion, StringComparison.OrdinalIgnoreCase))
        {
            if (!lastPostedMessageIdRaw.IsNullOrEmpty && ulong.TryParse(lastPostedMessageIdRaw.ToString(), out var msgId))
            {
                var message = await channel.GetMessageAsync(msgId).ConfigureAwait(false) as IUserMessage;
                if (message != null)
                {
                    var existingEmbed = message.Embeds.FirstOrDefault(e => FooterMatches(e.Footer?.Text, expectedFooterPrefix))
                                       ?? message.Embeds.FirstOrDefault(e => string.Equals(e.Title, expectedEmbedTitle, StringComparison.OrdinalIgnoreCase));
                    var existingFingerprint = TryExtractChangelogFingerprint(existingEmbed?.Footer?.Text);
                    if (string.Equals(existingFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
                    {
                        await db.StringSetAsync(lastPostedHashRedisKey, fingerprint).ConfigureAwait(false);
                        return;
                    }

                    await message.ModifyAsync(p => p.Embed = embed).ConfigureAwait(false);
                    await db.StringSetAsync(lastPostedHashRedisKey, fingerprint).ConfigureAwait(false);
                    return;
                }
            }

            var found = await TryFindChangelogMessageAsync(channel, expectedFooterPrefix, expectedEmbedTitle, token).ConfigureAwait(false);
            if (found != null)
            {
                var (existingMessage, existingFingerprint) = found.Value;
                if (!string.Equals(existingFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
                {
                    await existingMessage.ModifyAsync(p => p.Embed = embed).ConfigureAwait(false);
                }

                await db.StringSetAsync(lastPostedRedisKey, expectedVersion).ConfigureAwait(false);
                await db.StringSetAsync(lastPostedHashRedisKey, fingerprint).ConfigureAwait(false);
                await db.StringSetAsync(lastPostedMessageIdRedisKey, existingMessage.Id.ToString()).ConfigureAwait(false);
                return;
            }

            var reposted = await channel.SendMessageAsync(embed: embed).ConfigureAwait(false);
            await db.StringSetAsync(lastPostedRedisKey, expectedVersion).ConfigureAwait(false);
            await db.StringSetAsync(lastPostedHashRedisKey, fingerprint).ConfigureAwait(false);
            await db.StringSetAsync(lastPostedMessageIdRedisKey, reposted.Id.ToString()).ConfigureAwait(false);
            return;
        }

        if (!lastPostedHash.IsNullOrEmpty && string.Equals(lastPostedHash.ToString(), fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var posted = await channel.SendMessageAsync(embed: embed).ConfigureAwait(false);
        await db.StringSetAsync(lastPostedRedisKey, expectedVersion).ConfigureAwait(false);
        await db.StringSetAsync(lastPostedHashRedisKey, fingerprint).ConfigureAwait(false);
        await db.StringSetAsync(lastPostedMessageIdRedisKey, posted.Id.ToString()).ConfigureAwait(false);
    }

    private async Task<(IUserMessage Message, string? Fingerprint)?> TryFindChangelogMessageAsync(IMessageChannel channel, string expectedFooterPrefix, string expectedEmbedTitle, CancellationToken token)
    {
        var botUserId = _discordClient.CurrentUser?.Id;
        try
        {
            var messages = await channel.GetMessagesAsync(100).FlattenAsync().ConfigureAwait(false);
            foreach (var message in messages)
            {
                token.ThrowIfCancellationRequested();

                if (botUserId != null && message.Author.Id != botUserId.Value)
                {
                    continue;
                }

                foreach (var embed in message.Embeds)
                {
                    if (FooterMatches(embed.Footer?.Text, expectedFooterPrefix) ||
                        string.Equals(embed.Title, expectedEmbedTitle, StringComparison.OrdinalIgnoreCase))
                    {
                        if (message is IUserMessage userMessage)
                        {
                            return (userMessage, TryExtractChangelogFingerprint(embed.Footer?.Text));
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to check channel history for changelog post");
        }

        return null;
    }

    private (Embed Embed, string Fingerprint) BuildChangelogEmbedWithFingerprint(JsonElement entry, string version, bool isPrerelease)
    {
        var header = isPrerelease ? "Sphene Testbuild" : "Sphene Release";
        var eb = new EmbedBuilder()
            .WithColor(isPrerelease ? Color.Orange : Color.Green)
            .WithTimestamp(DateTimeOffset.UtcNow);

        var title = entry.TryGetProperty("title", out var tProp) && tProp.ValueKind == JsonValueKind.String ? tProp.GetString() : null;
        var description = entry.TryGetProperty("description", out var dProp) && dProp.ValueKind == JsonValueKind.String ? dProp.GetString() : null;

        var descBuilder = new System.Text.StringBuilder();
        descBuilder.AppendLine($"# {header} {version}");

        var cleanedTitle = NormalizeChangelogEntryTitleLine(title);
        var cleanedTitleTrimmed = cleanedTitle?.Trim();
        if (!string.IsNullOrWhiteSpace(cleanedTitleTrimmed) && IsRedundantChangelogTitleLine(cleanedTitleTrimmed, header, version))
        {
            cleanedTitleTrimmed = null;
        }
        if (!string.IsNullOrWhiteSpace(cleanedTitleTrimmed))
        {
            descBuilder.AppendLine();
            descBuilder.AppendLine(cleanedTitleTrimmed);
        }
        if (!string.IsNullOrWhiteSpace(description))
        {
            if (descBuilder.Length > 0) descBuilder.AppendLine();
            descBuilder.AppendLine(description!.Trim());
        }

        var changesText = FormatChangelogChanges(entry, maxChars: 2500);
        if (!string.IsNullOrWhiteSpace(changesText))
        {
            if (descBuilder.Length > 0) descBuilder.AppendLine();
            descBuilder.AppendLine(changesText);
        }

        var finalDesc = descBuilder.ToString().Trim();
        if (finalDesc.Length > 4096)
        {
            finalDesc = finalDesc[..4093] + "...";
        }

        eb.WithDescription(finalDesc);
        var fingerprint = ComputeChangelogFingerprint(header, version, finalDesc);
        var footerPrefix = BuildChangelogFooterPrefix(version, isPrerelease);
        eb.WithFooter($"{footerPrefix} rev {fingerprint}");
        return (eb.Build(), fingerprint);
    }

    private static string BuildChangelogFooterPrefix(string version, bool isPrerelease)
    {
        return $"changelog {(isPrerelease ? "testbuild" : "release")} {version}";
    }

    private static bool FooterMatches(string? footerText, string expectedFooterPrefix)
    {
        if (string.IsNullOrWhiteSpace(footerText))
        {
            return false;
        }

        return footerText.TrimStart().StartsWith(expectedFooterPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRedundantChangelogTitleLine(string titleLine, string header, string version)
    {
        var expected = $"{header} {version}";
        if (titleLine.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (titleLine.StartsWith(expected, StringComparison.OrdinalIgnoreCase))
        {
            var remainder = titleLine[expected.Length..].Trim();
            return string.IsNullOrEmpty(remainder) || remainder is ":" or "-" or "–" or "—";
        }

        return false;
    }

    private static string? TryExtractChangelogFingerprint(string? footerText)
    {
        if (string.IsNullOrWhiteSpace(footerText))
        {
            return null;
        }

        var trimmed = footerText.Trim();
        var revIndex = trimmed.IndexOf("rev ", StringComparison.OrdinalIgnoreCase);
        if (revIndex >= 0)
        {
            var value = trimmed[(revIndex + 4)..].Trim();
            var end = value.IndexOf(' ');
            if (end > 0)
            {
                value = value[..end];
            }
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        return null;
    }

    private static string ComputeChangelogFingerprint(string header, string version, string finalDescription)
    {
        var input = $"{header}|{version}|{finalDescription}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash)[..8].ToLowerInvariant();
    }

    private static string? NormalizeChangelogEntryTitleLine(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var text = title.Trim();
        if (text[0] != '[')
        {
            return text;
        }

        var end = text.IndexOf(']');
        if (end <= 0 || end > 16)
        {
            return text;
        }

        var tag = text[1..end].Trim();
        if (!LooksLikeShortDateTag(tag))
        {
            return text;
        }

        var remainder = text[(end + 1)..].Trim();
        return string.IsNullOrWhiteSpace(remainder) ? null : remainder;
    }

    private static bool LooksLikeShortDateTag(string tag)
    {
        if (tag.Length != 8)
        {
            return false;
        }

        return char.IsDigit(tag[0]) &&
               char.IsDigit(tag[1]) &&
               tag[2] == '-' &&
               char.IsDigit(tag[3]) &&
               char.IsDigit(tag[4]) &&
               tag[5] == '-' &&
               char.IsDigit(tag[6]) &&
               char.IsDigit(tag[7]);
    }

    private string FormatChangelogChanges(JsonElement entry, int maxChars)
    {
        if (!entry.TryGetProperty("changes", out var changesProp) || changesProp.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder();
        foreach (var change in changesProp.EnumerateArray())
        {
            if (builder.Length >= maxChars)
            {
                break;
            }

            if (change.ValueKind == JsonValueKind.String)
            {
                var line = change.GetString();
                if (!string.IsNullOrWhiteSpace(line))
                {
                    AppendLineBounded(builder, $"• {line.Trim()}", maxChars);
                }
            }
            else if (change.ValueKind == JsonValueKind.Object)
            {
                string text = string.Empty;
                if (change.TryGetProperty("description", out var cdProp) && cdProp.ValueKind == JsonValueKind.String)
                {
                    text = cdProp.GetString() ?? string.Empty;
                }

                var hasSub = change.TryGetProperty("sub", out var subProp) && subProp.ValueKind == JsonValueKind.Array;
                var headerText = text.Trim();
                var headerPrinted = false;
                if (!string.IsNullOrWhiteSpace(headerText) && hasSub)
                {
                    if (builder.Length > 0)
                    {
                        AppendNewLineBounded(builder, maxChars);
                    }
                    AppendLineBounded(builder, $"**{headerText}**", maxChars);
                    headerPrinted = true;
                }
                else if (!string.IsNullOrWhiteSpace(headerText))
                {
                    AppendLineBounded(builder, $"• {headerText}", maxChars);
                }

                if (hasSub)
                {
                    foreach (var sub in subProp.EnumerateArray())
                    {
                        if (builder.Length >= maxChars)
                        {
                            break;
                        }

                        string stext = string.Empty;
                        if (sub.ValueKind == JsonValueKind.String)
                        {
                            stext = sub.GetString() ?? string.Empty;
                        }
                        else if (sub.ValueKind == JsonValueKind.Object &&
                                 sub.TryGetProperty("description", out var sdProp) &&
                                 sdProp.ValueKind == JsonValueKind.String)
                        {
                            stext = sdProp.GetString() ?? string.Empty;
                        }

                        if (!string.IsNullOrWhiteSpace(stext))
                        {
                            var prefix = headerPrinted ? "• " : "  • ";
                            AppendLineBounded(builder, $"{prefix}{stext.Trim()}", maxChars);
                        }
                    }
                }
            }
        }

        return builder.ToString().Trim();
    }

    private static void AppendLineBounded(System.Text.StringBuilder builder, string line, int maxChars)
    {
        if (builder.Length >= maxChars || string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        var remaining = maxChars - builder.Length;
        if (remaining <= 0)
        {
            return;
        }

        if (line.Length + Environment.NewLine.Length > remaining)
        {
            var slice = Math.Max(0, remaining - Environment.NewLine.Length - 3);
            if (slice <= 0)
            {
                return;
            }

            builder.Append(line[..slice]);
            builder.Append("...");
            builder.AppendLine();
            return;
        }

        builder.AppendLine(line);
    }

    private static void AppendNewLineBounded(System.Text.StringBuilder builder, int maxChars)
    {
        if (builder.Length + Environment.NewLine.Length > maxChars)
        {
            return;
        }

        builder.AppendLine();
    }

    private async Task<(ChangelogBuild Release, ChangelogBuild TestBuild)?> TryFetchPluginMasterAsync(CancellationToken token)
    {
        try
        {
            var pluginMasterUrl = _configurationService.GetValueOrDefault(
                nameof(ServicesConfiguration.DiscordPluginMasterUrl),
                ServicesConfiguration.DefaultDiscordPluginMasterUrl);

            if (string.IsNullOrWhiteSpace(pluginMasterUrl))
            {
                pluginMasterUrl = ServicesConfiguration.DefaultDiscordPluginMasterUrl;
            }

            using var resp = await _httpClient.GetAsync(pluginMasterUrl, token).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            var entries = JsonSerializer.Deserialize<List<PluginMasterEntry>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (entries == null || entries.Count == 0)
            {
                return null;
            }

            var sphene = entries.FirstOrDefault(e => string.Equals(e.InternalName, "Sphene", StringComparison.OrdinalIgnoreCase));
            if (sphene == null)
            {
                return null;
            }

            var releaseUrl = !string.IsNullOrWhiteSpace(sphene.DownloadLinkUpdate)
                ? sphene.DownloadLinkUpdate
                : sphene.DownloadLinkInstall;

            var release = new ChangelogBuild(sphene.AssemblyVersion, releaseUrl);
            var testBuild = new ChangelogBuild(sphene.TestingAssemblyVersion, sphene.DownloadLinkTesting);
            return (release, testBuild);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to fetch plugin master");
            return null;
        }
    }

    private async Task<JsonDocument?> TryFetchChangelogJsonAsync(CancellationToken token)
    {
        try
        {
            var changelogUrl = _configurationService.GetValueOrDefault(
                nameof(ServicesConfiguration.DiscordChangelogUrl),
                ServicesConfiguration.DefaultDiscordChangelogUrl);

            if (string.IsNullOrWhiteSpace(changelogUrl))
            {
                changelogUrl = ServicesConfiguration.DefaultDiscordChangelogUrl;
            }

            using var resp = await _httpClient.GetAsync(changelogUrl, token).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var stream = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to fetch changelog JSON");
            return null;
        }
    }

    private static bool TryFindChangelogEntry(JsonDocument changelogJson, string expectedVersion, bool expectedIsPrerelease, out JsonElement entry)
    {
        entry = default;

        var root = changelogJson.RootElement;
        if (!root.TryGetProperty("changelogs", out var changelogs) || changelogs.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var item in changelogs.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var version = item.TryGetProperty("version", out var vProp) && vProp.ValueKind == JsonValueKind.String
                ? vProp.GetString() ?? string.Empty
                : string.Empty;

            if (!string.Equals(version, expectedVersion, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var isPrerelease = item.TryGetProperty("isPrerelease", out var prProp) && prProp.ValueKind == JsonValueKind.True;
            if (isPrerelease != expectedIsPrerelease)
            {
                continue;
            }

            entry = item;
            return true;
        }

        return false;
    }

    private async Task<bool> IsUrlAvailableAsync(string url, CancellationToken token)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        try
        {
            using var head = new HttpRequestMessage(HttpMethod.Head, uri);
            using var headResp = await _httpClient.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (headResp.IsSuccessStatusCode)
            {
                return true;
            }

            if (headResp.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented)
            {
                using var get = new HttpRequestMessage(HttpMethod.Get, uri);
                using var getResp = await _httpClient.SendAsync(get, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                return getResp.IsSuccessStatusCode;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to probe URL availability: {url}", url);
            return false;
        }
    }

    private readonly record struct ChangelogBuild(string? Version, string? DownloadUrl);

    private sealed class PluginMasterEntry
    {
        public string InternalName { get; set; } = string.Empty;
        public string AssemblyVersion { get; set; } = string.Empty;
        public string DownloadLinkInstall { get; set; } = string.Empty;
        public string DownloadLinkUpdate { get; set; } = string.Empty;
        public string DownloadLinkTesting { get; set; } = string.Empty;
        public string TestingAssemblyVersion { get; set; } = string.Empty;
    }
}

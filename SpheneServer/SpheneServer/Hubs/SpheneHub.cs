using Sphene.API.Data;
using Sphene.API.Data.Enum;
using Sphene.API.Dto;
using Sphene.API.Dto.User;
using Sphene.API.SignalR;
using SpheneServer.Services;
using SpheneServer.Utils;
using SpheneShared;
using SpheneShared.Data;
using SpheneShared.Metrics;
using SpheneShared.Models;
using SpheneShared.Services;
using SpheneShared.Utils.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis.Extensions.Core.Abstractions;
using System.Collections.Concurrent;

namespace SpheneServer.Hubs;

[Authorize(Policy = "Authenticated")]
public partial class SpheneHub : Hub<ISpheneHub>, ISpheneHub
{
    private static readonly ConcurrentDictionary<string, string> _userConnections = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string> _userClientVersions = new(StringComparer.Ordinal);
    private sealed record LegacyAckSender(string SenderUid, DateTime CreatedAtUtc);
    private static readonly ConcurrentDictionary<string, LegacyAckSender> _acknowledgmentSenders = new(StringComparer.Ordinal);
    private static readonly TimeSpan _legacyAckSenderTtl = TimeSpan.FromMinutes(15);
    private static readonly Timer _legacyAckCleanupTimer = new(_ => PruneLegacyAckSenders(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    // New batch acknowledgment tracker for proper session-based acknowledgments
    private static readonly BatchAcknowledgmentTracker _batchAcknowledgmentTracker = new();
    private static readonly ConcurrentDictionary<string, DateTime> _recentSessionAcknowledgments = new(StringComparer.Ordinal);
    // Track mutual visibility reports per ordered pair key (uidA|uidB)
    private static readonly MutualVisibilityTracker _mutualVisibilityTracker = new();
    private readonly SpheneMetrics _SpheneMetrics;
    private readonly SystemInfoService _systemInfoService;
    private readonly IHttpContextAccessor _contextAccessor;
    private readonly SpheneHubLogger _logger;
    private readonly string _shardName;
    private readonly int _maxExistingGroupsByUser;
    private readonly int _maxJoinedGroupsByUser;
    private readonly int _maxGroupUserCount;
    private readonly IRedisDatabase _redis;
    private readonly OnlineSyncedPairCacheService _onlineSyncedPairCacheService;
    private readonly SpheneCensus _spheneCensus;
    private readonly GPoseLobbyDistributionService _gPoseLobbyDistributionService;
    private readonly Uri _fileServerAddress;
    private readonly Uri? _fileServerFallbackAddress;
    private readonly Version _expectedClientVersion;
    private readonly Version _minimumClientVersion;

    private readonly Lazy<SpheneDbContext> _dbContextLazy;
    private SpheneDbContext DbContext => _dbContextLazy.Value;
    private readonly int _maxCharaDataByUser;
    private readonly int _maxCharaDataByUserVanity;
    private readonly bool _supporterFeaturesEnabled;

    public SpheneHub(SpheneMetrics SpheneMetrics,
        IDbContextFactory<SpheneDbContext> spheneDbContextFactory, ILogger<SpheneHub> logger, SystemInfoService systemInfoService,
        IConfigurationService<ServerConfiguration> configuration, IHttpContextAccessor contextAccessor,
        IRedisDatabase redisDb, OnlineSyncedPairCacheService onlineSyncedPairCacheService, SpheneCensus spheneCensus,
        GPoseLobbyDistributionService gPoseLobbyDistributionService)
    {
        _SpheneMetrics = SpheneMetrics;
        _systemInfoService = systemInfoService;
        _shardName = configuration.GetValue<string>(nameof(ServerConfiguration.ShardName));
        _maxExistingGroupsByUser = configuration.GetValueOrDefault(nameof(ServerConfiguration.MaxExistingGroupsByUser), 3);
        _maxJoinedGroupsByUser = configuration.GetValueOrDefault(nameof(ServerConfiguration.MaxJoinedGroupsByUser), 6);
        _maxGroupUserCount = configuration.GetValueOrDefault(nameof(ServerConfiguration.MaxGroupUserCount), 100);
        _fileServerAddress = configuration.GetValue<Uri>(nameof(ServerConfiguration.CdnFullUrl));
        _fileServerFallbackAddress = configuration.GetValueOrDefault<Uri>(nameof(ServerConfiguration.FileServerFallbackAddress), null);
        _expectedClientVersion = configuration.GetValueOrDefault(nameof(ServerConfiguration.ExpectedClientVersion), new Version(0, 0, 0));
        _minimumClientVersion = configuration.GetValueOrDefault(nameof(ServerConfiguration.MinimumClientVersion), new Version(0, 0, 0));
        _maxCharaDataByUser = configuration.GetValueOrDefault(nameof(ServerConfiguration.MaxCharaDataByUser), 10);
        _maxCharaDataByUserVanity = configuration.GetValueOrDefault(nameof(ServerConfiguration.MaxCharaDataByUserVanity), 50);
        _supporterFeaturesEnabled = configuration.GetValueOrDefault(nameof(ServerConfiguration.EnableSupporterFeatures), false);
        _contextAccessor = contextAccessor;
        _redis = redisDb;
        _onlineSyncedPairCacheService = onlineSyncedPairCacheService;
        _spheneCensus = spheneCensus;
        _gPoseLobbyDistributionService = gPoseLobbyDistributionService;
        _logger = new SpheneHubLogger(this, logger);
        _dbContextLazy = new Lazy<SpheneDbContext>(() => spheneDbContextFactory.CreateDbContext());
    }

    private static void PruneLegacyAckSenders()
    {
        var cutoff = DateTime.UtcNow - _legacyAckSenderTtl;
        foreach (var kvp in _acknowledgmentSenders)
        {
            if (kvp.Value.CreatedAtUtc < cutoff)
            {
                _acknowledgmentSenders.TryRemove(kvp.Key, out _);
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_dbContextLazy.IsValueCreated) DbContext.Dispose();
        }

        base.Dispose(disposing);
    }

    [Authorize(Policy = "Identified")]
    public async Task<ConnectionDto> GetConnectionDto()
    {
        _logger.LogCallInfo();
        _logger.LogCallWarning(SpheneHubLogger.Args("[DEBUG] FileServerAddress value: " + (_fileServerAddress?.ToString() ?? "NULL")));

        _SpheneMetrics.IncCounter(MetricsAPI.CounterInitializedConnections);

        await Clients.Caller.Client_UpdateSystemInfo(_systemInfoService.SystemInfoDto).ConfigureAwait(false);

        var dbUser = await DbContext.Users.SingleAsync(f => f.UID == UserUID).ConfigureAwait(false);
        dbUser.LastLoggedIn = DateTime.UtcNow;

        await Clients.Caller.Client_ReceiveServerMessage(MessageSeverity.Information, "Welcome to Sphene Network.").ConfigureAwait(false);

        var defaultPermissions = await DbContext.UserDefaultPreferredPermissions.SingleOrDefaultAsync(u => u.UserUID == UserUID).ConfigureAwait(false);
        if (defaultPermissions == null)
        {
            defaultPermissions = new UserDefaultPreferredPermission()
            {
                UserUID = UserUID,
            };

            DbContext.UserDefaultPreferredPermissions.Add(defaultPermissions);
        }

        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        return new ConnectionDto(new UserData(dbUser.UID, string.IsNullOrWhiteSpace(dbUser.Alias) ? null : dbUser.Alias))
        {
            CurrentClientVersion = _expectedClientVersion,
            ServerVersion = ISpheneHub.ApiVersion,
            IsAdmin = dbUser.IsAdmin,
            IsModerator = dbUser.IsModerator,
            IsSupporter = dbUser.IsSupporter,
            ServerInfo = new ServerInfo()
            {
                MaxGroupsCreatedByUser = _maxExistingGroupsByUser,
                ShardName = _shardName,
                MaxGroupsJoinedByUser = _maxJoinedGroupsByUser,
                MaxGroupUserCount = _maxGroupUserCount,
                FileServerAddress = _fileServerAddress,
                FileServerFallbackAddress = _fileServerFallbackAddress,
                MaxCharaData = _maxCharaDataByUser,
                MaxCharaDataVanity = _maxCharaDataByUserVanity,
                SupporterFeaturesEnabled = _supporterFeaturesEnabled,
            },
            DefaultPreferredPermissions = new DefaultPermissionsDto()
            {
                DisableGroupAnimations = defaultPermissions.DisableGroupAnimations,
                DisableGroupSounds = defaultPermissions.DisableGroupSounds,
                DisableGroupVFX = defaultPermissions.DisableGroupVFX,
                DisableIndividualAnimations = defaultPermissions.DisableIndividualAnimations,
                DisableIndividualSounds = defaultPermissions.DisableIndividualSounds,
                DisableIndividualVFX = defaultPermissions.DisableIndividualVFX,
                IndividualIsSticky = defaultPermissions.IndividualIsSticky,
            },
        };
    }

    [Authorize(Policy = "Authenticated")]
    public async Task<bool> CheckClientHealth()
    {
        await UpdateUserOnRedis().ConfigureAwait(false);

        return false;
    }

    [Authorize(Policy = "Authenticated")]
    public override async Task OnConnectedAsync()
    {
        // Check client version from User-Agent header before allowing connection
        var userAgent = _contextAccessor.HttpContext?.Request.Headers.UserAgent.ToString() ?? string.Empty;
        
        var clientVersion = ExtractClientVersionFromUserAgent(userAgent);
        
        // Reject connections if client version cannot be extracted (NULL) or is below minimum
        if (clientVersion == null)
        {
            _logger.LogCallWarning(SpheneHubLogger.Args($"Connection rejected: Client version could not be extracted from User-Agent '{userAgent}'. Expected Sphene client."));
            await Clients.Caller.Client_ReceiveServerMessage(MessageSeverity.Error, "Connection rejected: Invalid client. Please update to the latest Sphene version.").ConfigureAwait(false);
            Context.Abort();
            return;
        }
        
        if (clientVersion < _minimumClientVersion)
        {
            _logger.LogCallWarning(SpheneHubLogger.Args($"Client version {clientVersion} is outdated. Minimum required version: {_minimumClientVersion}"));
            
            await Clients.Caller.Client_ReceiveServerMessage(MessageSeverity.Error, 
                $"Your client version ({clientVersion}) is outdated. Minimum required version is {_minimumClientVersion}. Please update your Sphene client.").ConfigureAwait(false);
            
            // Disconnect the client
            Context.Abort();
            return;
        }

        _userClientVersions[UserUID] = clientVersion.ToString();

        if (_userConnections.TryGetValue(UserUID, out var oldId))
        {
            _logger.LogCallWarning(SpheneHubLogger.Args(_contextAccessor.GetIpAddress(), "UpdatingId", oldId, Context.ConnectionId));
            _userConnections[UserUID] = Context.ConnectionId;
        }
        else
        {
            _SpheneMetrics.IncGaugeWithLabels(MetricsAPI.GaugeConnections, labels: Continent);

            try
            {
                _logger.LogCallInfo(SpheneHubLogger.Args(_contextAccessor.GetIpAddress(), Context.ConnectionId, UserCharaIdent));
                await _onlineSyncedPairCacheService.InitPlayer(UserUID).ConfigureAwait(false);
                await UpdateUserOnRedis().ConfigureAwait(false);
                _userConnections[UserUID] = Context.ConnectionId;
                await SendOnlineToAllPairedUsers().ConfigureAwait(false);
            }
            catch
            {
                _userConnections.Remove(UserUID, out _);
            }
        }

        await CheckPendingFileTransfersAsync().ConfigureAwait(false);

        // Resend the current mutual visibility state for all pairs involving this user so a
        // reconnecting client does not get stuck assuming a stale state from before the disconnect.
        await SendCurrentMutualVisibilityToUserAsync(UserUID).ConfigureAwait(false);

        await base.OnConnectedAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Sends the current <see cref="Sphene.API.Dto.Visibility.MutualVisibilityDto"/> for every
    /// pair involving <paramref name="userUid"/> to that user. Used on (re)connect to ensure the
    /// client has an up-to-date view of mutual visibility, even if the state was settled while the
    /// client was offline or transitioning between zones.
    /// </summary>
    private async Task SendCurrentMutualVisibilityToUserAsync(string userUid)
    {
        try
        {
            var dtos = _mutualVisibilityTracker.GetAllCurrentStatesForUser(userUid, DateTime.UtcNow);
            foreach (var dto in dtos)
            {
                var ident = await GetUserIdent(userUid).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(ident))
                {
                    await Clients.User(userUid).Client_UserMutualVisibilityUpdate(dto).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogCallWarning(SpheneHubLogger.Args("Failed to resend mutual visibility on connect", userUid, ex.Message));
        }
    }

    [Authorize(Policy = "Authenticated")]
    public override async Task OnDisconnectedAsync(Exception exception)
    {
        if (_userConnections.TryGetValue(UserUID, out var connectionId)
            && string.Equals(connectionId, Context.ConnectionId, StringComparison.Ordinal))
        {
            _SpheneMetrics.DecGaugeWithLabels(MetricsAPI.GaugeConnections, labels: Continent);

            try
            {
                await GposeLobbyLeave().ConfigureAwait(false);

                await _onlineSyncedPairCacheService.DisposePlayer(UserUID).ConfigureAwait(false);

                _logger.LogCallInfo(SpheneHubLogger.Args(_contextAccessor.GetIpAddress(), Context.ConnectionId, UserCharaIdent));
                if (exception != null)
                    _logger.LogCallWarning(SpheneHubLogger.Args(_contextAccessor.GetIpAddress(), Context.ConnectionId, exception.Message, exception.StackTrace));

                await RemoveUserFromRedis().ConfigureAwait(false);

                _spheneCensus.ClearStatistics(UserUID);

                await SendOfflineToAllPairedUsers().ConfigureAwait(false);

                DbContext.RemoveRange(DbContext.Files.Where(f => !f.Uploaded && f.UploaderUID == UserUID));
                await DbContext.SaveChangesAsync().ConfigureAwait(false);

            }
            catch { }
            finally
            {
                _userConnections.Remove(UserUID, out _);
                _userClientVersions.Remove(UserUID, out _);
                CleanupAcknowledgmentMappingsForUser(UserUID);
                await ResetMutualVisibilityForUserAsync(UserUID).ConfigureAwait(false);
                
                // Clean possible stale online keys for peers referencing this user (defensive)
                try
                {
                    var paired = await GetAllPairedUnpausedUsers(UserUID).ConfigureAwait(false);
                    foreach (var p in paired)
                    {
                        // No-op: rely on peers re-fetching online lists; this block gives us a place to extend if needed
                    }
                }
                catch { }
            }
        }
        else
        {
            _logger.LogCallWarning(SpheneHubLogger.Args(_contextAccessor.GetIpAddress(), "ObsoleteId", UserUID, Context.ConnectionId));
        }

        await base.OnDisconnectedAsync(exception).ConfigureAwait(false);
    }

    // Reset mutual visibility state for all pairs involving the disconnected user
    private async Task ResetMutualVisibilityForUserAsync(string userUid)
    {
        try
        {
            var now = DateTime.UtcNow;
            var results = _mutualVisibilityTracker.ProcessDisconnect(userUid, now);

            foreach (var result in results)
            {
                if (result.Dto is null)
                    continue;

                foreach (var recipientUid in result.RecipientUids)
                {
                    var ident = await GetUserIdent(recipientUid).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(ident))
                    {
                        await Clients.User(recipientUid).Client_UserMutualVisibilityUpdate(result.Dto).ConfigureAwait(false);
                    }
                }

                _logger.LogCallInfo(SpheneHubLogger.Args("Visibility reset due to disconnect", userUid));
            }
        }
        catch (Exception ex)
        {
            _logger.LogCallWarning(SpheneHubLogger.Args("Failed to reset mutual visibility on disconnect", userUid, ex.Message));
        }
    }

    /// <summary>
    /// Extract client version from User-Agent header
    /// </summary>
    /// <param name="userAgent">The User-Agent header value</param>
    /// <returns>The extracted version or null if not found</returns>
    private static Version? ExtractClientVersionFromUserAgent(string userAgent)
    {
        if (string.IsNullOrEmpty(userAgent))
            return null;

        // User-Agent format: "Sphene/1.2.3" or "Sphene/1.2.3.456"
        var match = System.Text.RegularExpressions.Regex.Match(userAgent, @"Sphene/(\d+\.\d+\.\d+(?:\.\d+)?)");
        if (match.Success && Version.TryParse(match.Groups[1].Value, out var version))
        {
            return version;
        }

        return null;
    }

    private static string? GetKnownClientVersion(string uid)
    {
        return _userClientVersions.TryGetValue(uid, out var version) ? version : null;
    }

    // Cleanup acknowledgment mappings for a specific user
    private static void CleanupAcknowledgmentMappingsForUser(string userUid)
    {
        PruneLegacyAckSenders();
        // Clean up legacy acknowledgment mappings for this user
        var keysToRemove = new List<string>();
        
        foreach (var kvp in _acknowledgmentSenders)
        {
            if (kvp.Value.SenderUid == userUid)
            {
                keysToRemove.Add(kvp.Key);
            }
        }
        
        foreach (var key in keysToRemove)
        {
            _acknowledgmentSenders.TryRemove(key, out _);
        }
        
        // Clean up batch acknowledgment sessions for this user
        _batchAcknowledgmentTracker.CleanupSessionsForUser(userUid);

        var recentSessionKeys = _recentSessionAcknowledgments.Keys
            .Where(k => k.StartsWith(userUid + "|", StringComparison.Ordinal))
            .ToList();

        foreach (var key in recentSessionKeys)
        {
            _recentSessionAcknowledgments.TryRemove(key, out _);
        }
    }

    public async Task Client_UserAckYouUpdate(UserPermissionsDto dto)
    {
        await Clients.Caller.Client_UserAckYouUpdate(dto).ConfigureAwait(false);
    }

    // Client_UserAckOtherUpdate method removed - AckOther is controlled by other player's AckYou
}

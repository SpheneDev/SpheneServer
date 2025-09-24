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
    // Map hash keys to sender UIDs for acknowledgment lookup (legacy - will be replaced by batch tracker)
    private static readonly ConcurrentDictionary<string, string> _acknowledgmentSenders = new(StringComparer.Ordinal);
    // New batch acknowledgment tracker for proper session-based acknowledgments
    private static readonly BatchAcknowledgmentTracker _batchAcknowledgmentTracker = new();
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
    private readonly Version _expectedClientVersion;
    private readonly Version _minimumClientVersion;
    private readonly Lazy<SpheneDbContext> _dbContextLazy;
    private SpheneDbContext DbContext => _dbContextLazy.Value;
    private readonly int _maxCharaDataByUser;
    private readonly int _maxCharaDataByUserVanity;

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
        _expectedClientVersion = configuration.GetValueOrDefault(nameof(ServerConfiguration.ExpectedClientVersion), new Version(0, 0, 0));
        _minimumClientVersion = configuration.GetValueOrDefault(nameof(ServerConfiguration.MinimumClientVersion), new Version(0, 0, 0));
        _maxCharaDataByUser = configuration.GetValueOrDefault(nameof(ServerConfiguration.MaxCharaDataByUser), 10);
        _maxCharaDataByUserVanity = configuration.GetValueOrDefault(nameof(ServerConfiguration.MaxCharaDataByUserVanity), 50);
        _contextAccessor = contextAccessor;
        _redis = redisDb;
        _onlineSyncedPairCacheService = onlineSyncedPairCacheService;
        _spheneCensus = spheneCensus;
        _gPoseLobbyDistributionService = gPoseLobbyDistributionService;
        _logger = new SpheneHubLogger(this, logger);
        _dbContextLazy = new Lazy<SpheneDbContext>(() => spheneDbContextFactory.CreateDbContext());
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
            ServerInfo = new ServerInfo()
            {
                MaxGroupsCreatedByUser = _maxExistingGroupsByUser,
                ShardName = _shardName,
                MaxGroupsJoinedByUser = _maxJoinedGroupsByUser,
                MaxGroupUserCount = _maxGroupUserCount,
                FileServerAddress = _fileServerAddress,
                MaxCharaData = _maxCharaDataByUser,
                MaxCharaDataVanity = _maxCharaDataByUserVanity,
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

        await base.OnConnectedAsync().ConfigureAwait(false);
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
                CleanupAcknowledgmentMappingsForUser(UserUID);
            }
        }
        else
        {
            _logger.LogCallWarning(SpheneHubLogger.Args(_contextAccessor.GetIpAddress(), "ObsoleteId", UserUID, Context.ConnectionId));
        }

        await base.OnDisconnectedAsync(exception).ConfigureAwait(false);
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

        // User-Agent format: "Sphene/1.2.3"
        var match = System.Text.RegularExpressions.Regex.Match(userAgent, @"Sphene/(\d+\.\d+\.\d+)");
        if (match.Success && Version.TryParse(match.Groups[1].Value, out var version))
        {
            return version;
        }

        return null;
    }

    // Cleanup acknowledgment mappings for a specific user
    private static void CleanupAcknowledgmentMappingsForUser(string userUid)
    {
        // Clean up legacy acknowledgment mappings for this user
        var keysToRemove = new List<string>();
        
        foreach (var kvp in _acknowledgmentSenders)
        {
            if (kvp.Value == userUid)
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
    }

    public async Task Client_UserAckYouUpdate(UserPermissionsDto dto)
    {
        await Clients.Caller.Client_UserAckYouUpdate(dto).ConfigureAwait(false);
    }

    // Client_UserAckOtherUpdate method removed - AckOther is controlled by other player's AckYou
}

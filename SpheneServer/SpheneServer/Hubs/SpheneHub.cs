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
        _logger.LogCallInfo(SpheneHubLogger.Args("CheckClientHealth", Context.ConnectionId));
        await UpdateUserOnRedis().ConfigureAwait(false);
        _logger.LogDebug("CheckClientHealth completed for connection {0}", Context.ConnectionId);
        return true;
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

    // Deathroll SignalR Methods
    public async Task<bool> DeathrollSendInvitation(DeathrollInvitationDto invitation)
    {
        try
        {
            _logger.LogCallInfo(SpheneHubLogger.Args("DeathrollSendInvitation", invitation.Sender.AliasOrUID, invitation.Recipient.AliasOrUID, invitation.InvitationId));
            
            // If recipient UID is null, try to resolve it from the name
            string recipientUID = invitation.Recipient.UID;
            if (string.IsNullOrEmpty(recipientUID))
            {
                _logger.LogDebug("Attempting to resolve recipient name {0} to UID", invitation.Recipient.AliasOrUID);
                
                var recipientUser = await DbContext.Users.SingleOrDefaultAsync(u => 
                    u.Alias == invitation.Recipient.AliasOrUID).ConfigureAwait(false);
                
                if (recipientUser != null)
                {
                    recipientUID = recipientUser.UID;
                    _logger.LogDebug("Successfully resolved recipient name {0} to UID {1}", 
                        invitation.Recipient.AliasOrUID, recipientUID);
                }
                else
                {
                    _logger.LogCallWarning(SpheneHubLogger.Args("Could not resolve recipient name", invitation.Recipient.AliasOrUID));
                    return false;
                }
            }
            else
            {
                _logger.LogDebug("Recipient UID already provided: {0}", recipientUID);
            }
            
            // Check if recipient is online
            var recipientConnectionId = GetConnectionIdForUser(recipientUID);
            _logger.LogDebug("DEATHROLL DEBUG: Checking connection for recipient UID {0}, ConnectionId: {1}", 
                recipientUID, recipientConnectionId ?? "null");
            
            // Log all current connections for debugging
            _logger.LogDebug("DEATHROLL DEBUG: Current user connections count: {0}", _userConnections.Count);
            foreach (var kvp in _userConnections)
            {
                _logger.LogDebug("DEATHROLL DEBUG: Connection mapping - UID: {0} -> ConnectionId: {1}", kvp.Key, kvp.Value);
            }
            
            if (!string.IsNullOrEmpty(recipientConnectionId))
            {
                _logger.LogDebug("DEATHROLL DEBUG: Sending deathroll invitation to online recipient {0} (UID: {1}) via ConnectionId: {2}", 
                    invitation.Recipient.AliasOrUID, recipientUID, recipientConnectionId);
                
                try
                 {
                     await Clients.Client(recipientConnectionId).Client_DeathrollInvitationReceived(invitation);
                     _logger.LogDebug("DEATHROLL DEBUG: Successfully sent SignalR message to ConnectionId: {0}", recipientConnectionId);
                 }
                 catch (Exception ex)
                 {
                     _logger.LogCallWarning(SpheneHubLogger.Args("DEATHROLL DEBUG: Failed to send SignalR message to ConnectionId", recipientConnectionId, ex.Message));
                     return false;
                 }
                
                _logger.LogCallInfo(SpheneHubLogger.Args("Successfully sent deathroll invitation to", invitation.Recipient.AliasOrUID));
                return true;
            }
            
            _logger.LogCallWarning(SpheneHubLogger.Args("DEATHROLL DEBUG: Recipient not online", invitation.Recipient.AliasOrUID, recipientUID));
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogCallWarning(SpheneHubLogger.Args("DeathrollSendInvitation", ex.Message));
            return false;
        }
    }

    public async Task<bool> DeathrollRespondToInvitation(DeathrollInvitationResponseDto response)
    {
        try
        {
            _logger.LogDebug("Processing deathroll invitation response from {0}: {1}", 
                response.Responder.AliasOrUID, response.Accepted);
            
            // Find the original sender and notify them of the response
            // For now, broadcast to all connected clients (can be optimized later)
            await Clients.All.Client_DeathrollInvitationResponse(response);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogCallWarning(SpheneHubLogger.Args("DeathrollRespondToInvitation", ex.Message));
            return false;
        }
    }

    public async Task<bool> DeathrollUpdateGameState(DeathrollGameStateDto gameState)
    {
        try
        {
            _logger.LogDebug("Updating deathroll game state for game {0}", gameState.GameId);
            
            // Broadcast game state update to all players in the game
            await Clients.All.Client_DeathrollGameStateUpdate(gameState);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogCallWarning(SpheneHubLogger.Args("DeathrollUpdateGameState", ex.Message));
            return false;
        }
    }

    // New lobby system methods
    public async Task<bool> DeathrollUpdateLobbyState(DeathrollGameStateDto lobbyState)
    {
        try
        {
            _logger.LogDebug("Updating deathroll lobby {0} with name '{1}'", lobbyState.GameId, lobbyState.LobbyName);
            
            // Broadcast lobby state update to all connected clients
            await Clients.All.Client_DeathrollGameStateUpdate(lobbyState);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogCallWarning(SpheneHubLogger.Args("DeathrollUpdateLobbyState", ex.Message));
            return false;
        }
    }

    public async Task<bool> DeathrollCreateLobby(DeathrollCreateLobbyDto createLobby)
    {
        try
        {
            _logger.LogDebug("Creating lobby {0} with max players {1}", createLobby.LobbyName, createLobby.MaxPlayers);
            
            // For now, just log the creation - in a real implementation you'd store the lobby
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogCallWarning(SpheneHubLogger.Args("DeathrollCreateLobby", ex.Message));
            return false;
        }
    }

    public async Task<bool> DeathrollJoinLobby(DeathrollJoinLobbyDto joinRequest)
    {
        try
        {
            _logger.LogDebug("Player {0} requesting to join lobby {1}", joinRequest.PlayerName, joinRequest.GameId);
            
            // For now, broadcast the join request to all clients
            // In a real implementation, you'd validate the lobby exists and has space
            await Clients.All.Client_DeathrollLobbyJoinRequest(joinRequest);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogCallWarning(SpheneHubLogger.Args("DeathrollJoinLobby", ex.Message));
            return false;
        }
    }

    public async Task<bool> DeathrollLeaveLobby(DeathrollLeaveLobbyDto leaveInfo)
    {
        try
        {
            _logger.LogDebug("Player {0} leaving lobby {1}", leaveInfo.PlayerName, leaveInfo.GameId);
            // Broadcast leave to all clients so host can update lobby membership
            await Clients.All.Client_DeathrollLobbyLeave(leaveInfo);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogCallWarning(SpheneHubLogger.Args("DeathrollLeaveLobby", ex.Message));
            return false;
        }
    }

    public async Task<bool> DeathrollOpenCloseLobby(string gameId, bool isOpen)
    {
        try
        {
            _logger.LogDebug("{0} lobby {1}", isOpen ? "Opening" : "Closing", gameId);
            
            // Broadcast lobby open/close status to all clients
            await Clients.All.Client_DeathrollLobbyOpenClose(gameId, isOpen);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogCallWarning(SpheneHubLogger.Args("DeathrollOpenCloseLobby", ex.Message));
            return false;
        }
    }

    public async Task<bool> DeathrollStartGameFromLobby(string gameId)
    {
        try
        {
            _logger.LogDebug("Starting game from lobby {0}", gameId);
            
            // Broadcast game start to all clients
            await Clients.All.Client_DeathrollGameStart(gameId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogCallWarning(SpheneHubLogger.Args("DeathrollStartGameFromLobby", ex.Message));
            return false;
        }
    }

    public async Task<bool> DeathrollCancelLobby(string gameId)
    {
        try
        {
            _logger.LogDebug("Canceling lobby {0}", gameId);
            
            // Broadcast lobby cancellation to all clients
            await Clients.All.Client_DeathrollLobbyCanceled(gameId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogCallWarning(SpheneHubLogger.Args("DeathrollCancelLobby", ex.Message));
            return false;
        }
    }

    public async Task<bool> DeathrollSetPlayerReady(string gameId, string playerId, bool isReady)
    {
        try
        {
            _logger.LogDebug("Setting player {0} ready status to {1} in lobby {2}", playerId, isReady, gameId);
            
            // Broadcast player ready status to all clients
            await Clients.All.Client_DeathrollPlayerReady(gameId, playerId, isReady);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogCallWarning(SpheneHubLogger.Args("DeathrollSetPlayerReady", ex.Message));
            return false;
        }
    }

    public async Task<bool> DeathrollAnnounceLobby(DeathrollLobbyAnnouncementDto announcement)
    {
        try
        {
            _logger.LogCallInfo(SpheneHubLogger.Args("Received lobby announcement for game", announcement.GameId, "from host", announcement.Host?.AliasOrUID ?? "Unknown"));
            
            // Broadcast lobby announcement to all clients
            await Clients.All.Client_DeathrollLobbyAnnouncement(announcement);
            
            _logger.LogCallInfo(SpheneHubLogger.Args("Successfully broadcasted lobby announcement for game", announcement.GameId, "to all clients"));
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogCallWarning(SpheneHubLogger.Args("DeathrollAnnounceLobby", ex.Message));
            return false;
        }
    }

    public async Task<bool> DeathrollUpdateTournamentState(DeathrollTournamentStateDto dto)
    {
        try
        {
            _logger.LogDebug("Updating deathroll tournament state for game {0}, tournament {1}", dto.GameId, dto.TournamentId);
            await Clients.All.Client_DeathrollTournamentUpdate(dto);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogCallWarning(SpheneHubLogger.Args("DeathrollUpdateTournamentState", ex.Message));
            return false;
        }
    }

    // Helper method to get connection ID for a user
    private static string? GetConnectionIdForUser(string userUid)
    {
        return _userConnections.TryGetValue(userUid, out var connectionId) ? connectionId : null;
    }

    // Client_UserAckOtherUpdate method removed - AckOther is controlled by other player's AckYou
}

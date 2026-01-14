using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Sphene.API.Data;
using Sphene.API.Data.Enum;
using Sphene.API.Data.Extensions;
using Sphene.API.Dto.User;
using SpheneServer.Utils;
using SpheneShared.Metrics;
using SpheneShared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace SpheneServer.Hubs;

public partial class SpheneHub
{
    private static readonly string[] AllowedExtensionsForGamePaths = { ".mdl", ".tex", ".mtrl", ".tmb", ".pap", ".avfx", ".atex", ".sklb", ".eid", ".phyb", ".pbd", ".scd", ".skp", ".shpk" };
    private static readonly Dictionary<string, bool> PenumbraReceivePreferences = new(StringComparer.Ordinal);

    [Authorize(Policy = "Identified")]
    public async Task UserAddPair(UserDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));

        // don't allow adding nothing
        var uid = dto.User.UID.Trim();
        if (string.Equals(dto.User.UID, UserUID, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(dto.User.UID)) return;

        // grab other user, check if it exists and if a pair already exists
        var otherUser = await DbContext.Users.SingleOrDefaultAsync(u => u.UID == uid || u.Alias == uid).ConfigureAwait(false);
        if (otherUser == null)
        {
            await Clients.Caller.Client_ReceiveServerMessage(MessageSeverity.Warning, $"Cannot pair with {dto.User.UID}, UID does not exist").ConfigureAwait(false);
            return;
        }

        if (string.Equals(otherUser.UID, UserUID, StringComparison.Ordinal))
        {
            await Clients.Caller.Client_ReceiveServerMessage(MessageSeverity.Warning, $"My god you can't pair with yourself why would you do that please stop").ConfigureAwait(false);
            return;
        }

        var existingEntry =
            await DbContext.ClientPairs.AsNoTracking()
                .FirstOrDefaultAsync(p =>
                    p.User.UID == UserUID && p.OtherUserUID == otherUser.UID).ConfigureAwait(false);

        if (existingEntry != null)
        {
            await Clients.Caller.Client_ReceiveServerMessage(MessageSeverity.Warning, $"Cannot pair with {dto.User.UID}, already paired").ConfigureAwait(false);
            return;
        }

        // grab self create new client pair and save
        var user = await DbContext.Users.SingleAsync(u => u.UID == UserUID).ConfigureAwait(false);

        _logger.LogCallInfo(SpheneHubLogger.Args(dto, "Success"));

        ClientPair wl = new ClientPair()
        {
            OtherUser = otherUser,
            User = user,
        };
        await DbContext.ClientPairs.AddAsync(wl).ConfigureAwait(false);

        var existingData = await GetPairInfo(UserUID, otherUser.UID).ConfigureAwait(false);

        var permissions = existingData?.OwnPermissions;
        if (permissions == null || !permissions.Sticky)
        {
            var ownDefaultPermissions = await DbContext.UserDefaultPreferredPermissions.AsNoTracking().SingleOrDefaultAsync(f => f.UserUID == UserUID).ConfigureAwait(false);

            permissions = new UserPermissionSet()
            {
                User = user,
                OtherUser = otherUser,
                DisableAnimations = ownDefaultPermissions.DisableIndividualAnimations,
                DisableSounds = ownDefaultPermissions.DisableIndividualSounds,
                DisableVFX = ownDefaultPermissions.DisableIndividualVFX,
                IsPaused = false,
                Sticky = true
            };

            var existingDbPerms = await DbContext.Permissions.SingleOrDefaultAsync(u => u.UserUID == UserUID && u.OtherUserUID == otherUser.UID).ConfigureAwait(false);
            if (existingDbPerms == null)
            {
                await DbContext.Permissions.AddAsync(permissions).ConfigureAwait(false);
            }
            else
            {
                existingDbPerms.DisableAnimations = permissions.DisableAnimations;
                existingDbPerms.DisableSounds = permissions.DisableSounds;
                existingDbPerms.DisableVFX = permissions.DisableVFX;
                existingDbPerms.IsPaused = false;
                existingDbPerms.Sticky = true;

                DbContext.Permissions.Update(existingDbPerms);
            }
        }

        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        // get the opposite entry of the client pair
        var otherEntry = OppositeEntry(otherUser.UID);
        var otherIdent = await GetUserIdent(otherUser.UID).ConfigureAwait(false);

        var otherPermissions = existingData?.OtherPermissions ?? null;

        var ownPerm = permissions.ToUserPermissions(setSticky: true);
        var otherPerm = otherPermissions.ToUserPermissions();

        var userPairResponse = new UserPairDto(otherUser.ToUserData(),
            otherEntry == null ? IndividualPairStatus.OneSided : IndividualPairStatus.Bidirectional,
            ownPerm, otherPerm);

        await Clients.User(user.UID).Client_UserAddClientPair(userPairResponse).ConfigureAwait(false);

        // check if other user is online
        if (otherIdent == null || otherEntry == null) return;

        // send push with update to other user if other user is online
        await Clients.User(otherUser.UID)
            .Client_UserUpdateOtherPairPermissions(new UserPermissionsDto(user.ToUserData(),
            permissions.ToUserPermissions())).ConfigureAwait(false);

        await Clients.User(otherUser.UID)
            .Client_UpdateUserIndividualPairStatusDto(new(user.ToUserData(), IndividualPairStatus.Bidirectional))
            .ConfigureAwait(false);

        if (!ownPerm.IsPaused() && !otherPerm.IsPaused())
        {
            await Clients.User(UserUID).Client_UserSendOnline(new(otherUser.ToUserData(), otherIdent)).ConfigureAwait(false);
            await Clients.User(otherUser.UID).Client_UserSendOnline(new(user.ToUserData(), UserCharaIdent)).ConfigureAwait(false);
        }
    }

    [Authorize(Policy = "Identified")]
    public async Task UserDelete()
    {
        _logger.LogCallInfo();

        var userEntry = await DbContext.Users.SingleAsync(u => u.UID == UserUID).ConfigureAwait(false);
        var secondaryUsers = await DbContext.Auth.Include(u => u.User).Where(u => u.PrimaryUserUID == UserUID).Select(c => c.User).ToListAsync().ConfigureAwait(false);
        foreach (var user in secondaryUsers)
        {
            await DeleteUser(user).ConfigureAwait(false);
        }

        await DeleteUser(userEntry).ConfigureAwait(false);
    }

    [Authorize(Policy = "Identified")]
    public async Task<List<OnlineUserIdentDto>> UserGetOnlinePairs(CensusDataDto? censusData)
    {
        _logger.LogCallInfo();

        var allPairedUsers = await GetAllPairedUnpausedUsers().ConfigureAwait(false);
        var pairs = await GetOnlineUsers(allPairedUsers).ConfigureAwait(false);

        await SendOnlineToAllPairedUsers().ConfigureAwait(false);

        _spheneCensus.PublishStatistics(UserUID, censusData);

        return pairs.Select(p => new OnlineUserIdentDto(new UserData(p.Key), p.Value)).ToList();
    }

    [Authorize(Policy = "Identified")]
    public async Task UserUpdateGposeState(bool isInGpose)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args("IsInGpose:", isInGpose));

        var usersToSendDataTo = await GetAllPairedUnpausedUsers().ConfigureAwait(false);
        if (usersToSendDataTo.Count == 0) return;

        var dto = new UserGposeStateDto(new(UserUID), isInGpose, DateTime.UtcNow);
        await Clients.Users(usersToSendDataTo).Client_UserGposeStateUpdate(dto).ConfigureAwait(false);
    }

    [Authorize(Policy = "Identified")]
    public async Task<List<UserFullPairDto>> UserGetPairedClients()
    {
        _logger.LogCallInfo();

        var pairs = await GetAllPairInfo(UserUID).ConfigureAwait(false);
        return pairs.Select(p =>
        {
            bool otherAllowsMods = true;
            if (PenumbraReceivePreferences.TryGetValue(p.Key, out var pref))
            {
                otherAllowsMods = pref;
            }
            return new UserFullPairDto(new UserData(p.Key, p.Value.Alias),
                p.Value.ToIndividualPairStatus(),
                p.Value.GIDs.Where(g => !string.Equals(g, Constants.IndividualKeyword, StringComparison.OrdinalIgnoreCase)).ToList(),
                p.Value.OwnPermissions.ToUserPermissions(setSticky: true),
                p.Value.OtherPermissions.ToUserPermissions(),
                otherAllowsMods);
        }).ToList();
    }

    [Authorize(Policy = "Identified")]
    public Task UserUpdatePenumbraReceivePreference(bool allowMods)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args("AllowReceivingPenumbraMods", allowMods));
        PenumbraReceivePreferences[UserUID] = allowMods;
        return NotifyPairedUsersAboutPenumbraPreferenceAsync(allowMods);
    }

    private async Task NotifyPairedUsersAboutPenumbraPreferenceAsync(bool allowMods)
    {
        var pairedUsers = await GetAllPairedUnpausedUsers().ConfigureAwait(false);
        if (pairedUsers.Count == 0)
        {
            return;
        }

        var dto = new UserPenumbraReceivePreferenceDto(new UserData(UserUID), allowMods);
        await Clients.Users(pairedUsers).Client_UserPenumbraReceivePreferenceUpdate(dto).ConfigureAwait(false);
    }

    [Authorize(Policy = "Identified")]
    public async Task<UserProfileDto> UserGetProfile(UserDto user)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(user));

        var allUserPairs = await GetAllPairedUnpausedUsers().ConfigureAwait(false);

        if (!allUserPairs.Contains(user.User.UID, StringComparer.Ordinal) && !string.Equals(user.User.UID, UserUID, StringComparison.Ordinal))
        {
            return new UserProfileDto(user.User, false, null, null, "Due to the pause status you cannot access this users profile.");
        }

        var data = await DbContext.UserProfileData.SingleOrDefaultAsync(u => u.UserUID == user.User.UID).ConfigureAwait(false);
        if (data == null) return new UserProfileDto(user.User, false, null, null, null);

        if (data.FlaggedForReport) return new UserProfileDto(user.User, true, null, null, "This profile is flagged for report and pending evaluation");
        if (data.ProfileDisabled) return new UserProfileDto(user.User, true, null, null, "This profile was permanently disabled");

        return new UserProfileDto(user.User, false, data.IsNSFW, data.Base64ProfileImage, data.UserDescription);
    }

    [Authorize(Policy = "Identified")]
    public async Task UserPushData(UserCharaDataMessageDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto.CharaData.FileReplacements.Count));

        // check for honorific containing . and /
        try
        {
            var honorificJson = Encoding.Default.GetString(Convert.FromBase64String(dto.CharaData.HonorificData));
            var deserialized = JsonSerializer.Deserialize<JsonElement>(honorificJson);
            if (deserialized.TryGetProperty("Title", out var honorificTitle))
            {
                var title = honorificTitle.GetString().Normalize(NormalizationForm.FormKD);
                if (UrlRegex().IsMatch(title))
                {
                    await Clients.Caller.Client_ReceiveServerMessage(MessageSeverity.Error, "Your data was not pushed: The usage of URLs the Honorific titles is prohibited. Remove them to be able to continue to push data.").ConfigureAwait(false);
                    throw new HubException("Invalid data provided, Honorific title invalid: " + title);
                }
            }
        }
        catch (HubException)
        {
            throw;
        }
        catch (Exception)
        {
            // swallow
        }

        bool hadInvalidData = false;
        List<string> invalidGamePaths = new();
        List<string> invalidFileSwapPaths = new();
        foreach (var replacement in dto.CharaData.FileReplacements.SelectMany(p => p.Value))
        {
            var invalidPaths = replacement.GamePaths.Where(p => !GamePathRegex().IsMatch(p)).ToList();
            invalidPaths.AddRange(replacement.GamePaths.Where(p => !AllowedExtensionsForGamePaths.Any(e => p.EndsWith(e, StringComparison.OrdinalIgnoreCase))));
            replacement.GamePaths = replacement.GamePaths.Where(p => !invalidPaths.Contains(p, StringComparer.OrdinalIgnoreCase)).ToArray();
            bool validGamePaths = replacement.GamePaths.Any();
            bool validHash = string.IsNullOrEmpty(replacement.Hash) || HashRegex().IsMatch(replacement.Hash);
            bool validFileSwapPath = string.IsNullOrEmpty(replacement.FileSwapPath) || GamePathRegex().IsMatch(replacement.FileSwapPath);
            if (!validGamePaths || !validHash || !validFileSwapPath)
            {
                _logger.LogCallWarning(SpheneHubLogger.Args("Invalid Data", "GamePaths", validGamePaths, string.Join(",", invalidPaths), "Hash", validHash, replacement.Hash, "FileSwap", validFileSwapPath, replacement.FileSwapPath));
                hadInvalidData = true;
                if (!validFileSwapPath) invalidFileSwapPaths.Add(replacement.FileSwapPath);
                if (!validGamePaths) invalidGamePaths.AddRange(replacement.GamePaths);
                if (!validHash) invalidFileSwapPaths.Add(replacement.Hash);
            }
        }

        if (hadInvalidData)
        {
            await Clients.Caller.Client_ReceiveServerMessage(MessageSeverity.Error, "One or more of your supplied mods were rejected from the server. Consult /xllog for more information.").ConfigureAwait(false);
            throw new HubException("Invalid data provided, contact the appropriate mod creator to resolve those issues"
            + Environment.NewLine
            + string.Join(Environment.NewLine, invalidGamePaths.Select(p => "Invalid Game Path: " + p))
            + Environment.NewLine
            + string.Join(Environment.NewLine, invalidFileSwapPaths.Select(p => "Invalid FileSwap Path: " + p)));
        }

        var recipientUids = dto.Recipients.Select(r => r.UID).ToList();
        bool allCached = await _onlineSyncedPairCacheService.AreAllPlayersCached(UserUID,
            recipientUids, Context.ConnectionAborted).ConfigureAwait(false);

        if (!allCached)
        {
            var allPairedUsers = await GetAllPairedUnpausedUsers().ConfigureAwait(false);

            recipientUids = allPairedUsers.Where(f => recipientUids.Contains(f, StringComparer.Ordinal)).ToList();

            await _onlineSyncedPairCacheService.CachePlayers(UserUID, allPairedUsers, Context.ConnectionAborted).ConfigureAwait(false);
        }

        _logger.LogCallInfo(SpheneHubLogger.Args(recipientUids.Count));

        // Get data hash from character data
        var dataHash = dto.CharaData.DataHash.Value;
        
        // Create batch acknowledgment session for tracking individual recipients
        var sessionId = _batchAcknowledgmentTracker.CreateSession(dataHash, UserUID, recipientUids);
        
        // Keep legacy mapping for backward compatibility (will be removed later)
        _acknowledgmentSenders.AddOrUpdate(dataHash, UserUID, (key, oldValue) => UserUID);
        
        // Log session-based acknowledgment info
        _logger.LogCallInfo(SpheneHubLogger.Args("Hash:", dataHash[..8], "SessionId:", sessionId[..8], "Recipients:", recipientUids.Count));
        
        var characterDataDto = new OnlineUserCharaDataDto(new UserData(UserUID), dto.CharaData)
        {
            DataHash = dataHash,
            RequiresAcknowledgment = true,
            SessionId = sessionId
        };

        await Clients.Users(recipientUids).Client_UserReceiveCharacterData(characterDataDto).ConfigureAwait(false);

        _spheneCensus.PublishStatistics(UserUID, dto.CensusDataDto);

        _SpheneMetrics.IncCounter(MetricsAPI.CounterUserPushData);
        _SpheneMetrics.IncCounter(MetricsAPI.CounterUserPushDataTo, recipientUids.Count);
    }

    [Authorize(Policy = "Identified")]
    public async Task UserSendCharacterDataAcknowledgment(CharacterDataAcknowledgmentDto acknowledgmentDto)
    {
        // Handle new session-based acknowledgments
        if (!string.IsNullOrEmpty(acknowledgmentDto.SessionId))
        {
            if (_batchAcknowledgmentTracker.TryAcknowledge(acknowledgmentDto.SessionId, UserUID, out var session))
            {
                // Validate that the acknowledging user has a pair relationship with the original sender
                var pairExists = await DbContext.ClientPairs.AsNoTracking()
                    .AnyAsync(p => (p.UserUID == UserUID && p.OtherUserUID == session.SenderUid) ||
                                  (p.UserUID == session.SenderUid && p.OtherUserUID == UserUID))
                    .ConfigureAwait(false);
                
                if (!pairExists)
                {
                    _logger.LogCallWarning(SpheneHubLogger.Args("No pair relationship - User:", UserUID, "SessionId:", acknowledgmentDto.SessionId[..8], "Sender:", session.SenderUid));
                    return;
                }
                
                // Create acknowledgment DTO with session information
                var forwardedAcknowledgment = new CharacterDataAcknowledgmentDto(new UserData(UserUID), acknowledgmentDto.DataHash)
                {
                    Success = acknowledgmentDto.Success,
                    ErrorCode = acknowledgmentDto.ErrorCode,
                    ErrorMessage = acknowledgmentDto.ErrorMessage,
                    AcknowledgedAt = acknowledgmentDto.AcknowledgedAt,
                    SessionId = acknowledgmentDto.SessionId
                };
                
                // Send acknowledgment to the original sender
                await Clients.User(session.SenderUid).Client_UserReceiveCharacterDataAcknowledgment(forwardedAcknowledgment).ConfigureAwait(false);
                
                // Log individual acknowledgment
                _logger.LogCallInfo(SpheneHubLogger.Args("SessionAck - User:", UserUID, "SessionId:", acknowledgmentDto.SessionId[..8], "Remaining:", session.PendingRecipients.Count));
                
                // Check if all recipients have acknowledged
                if (_batchAcknowledgmentTracker.IsSessionCompleted(acknowledgmentDto.SessionId))
                {
                    _batchAcknowledgmentTracker.CompleteSession(acknowledgmentDto.SessionId);
                    _logger.LogCallInfo(SpheneHubLogger.Args("SessionComplete - SessionId:", acknowledgmentDto.SessionId[..8]));
                }
            }
            else
            {
                _logger.LogCallWarning(SpheneHubLogger.Args("Invalid session acknowledgment - User:", UserUID, "SessionId:", acknowledgmentDto.SessionId[..8]));
            }
        }
        // Handle legacy hash-based acknowledgments for backward compatibility
        else if (_acknowledgmentSenders.TryGetValue(acknowledgmentDto.DataHash, out var originalSenderUid))
        {
            // Clean up the acknowledgment mapping FIRST to prevent duplicate processing
            var removed = _acknowledgmentSenders.TryRemove(acknowledgmentDto.DataHash, out _);
            
            if (!removed)
            {
                _logger.LogCallWarning(SpheneHubLogger.Args("Acknowledgment already processed - User:", UserUID, "Hash:", acknowledgmentDto.DataHash[..8]));
                return;
            }
            
            // Validate that the acknowledging user has a pair relationship with the original sender
            var pairExists = await DbContext.ClientPairs.AsNoTracking()
                .AnyAsync(p => (p.UserUID == UserUID && p.OtherUserUID == originalSenderUid) ||
                              (p.UserUID == originalSenderUid && p.OtherUserUID == UserUID))
                .ConfigureAwait(false);
            
            if (!pairExists)
            {
                _logger.LogCallWarning(SpheneHubLogger.Args("No pair relationship - User:", UserUID, "Hash:", acknowledgmentDto.DataHash[..8], "Sender:", originalSenderUid));
                return;
            }
            
            // Create a new acknowledgment DTO with the current user (recipient) as the acknowledging user
            var forwardedAcknowledgment = new CharacterDataAcknowledgmentDto(new UserData(UserUID), acknowledgmentDto.DataHash)
            {
                Success = acknowledgmentDto.Success,
                ErrorCode = acknowledgmentDto.ErrorCode,
                ErrorMessage = acknowledgmentDto.ErrorMessage,
                AcknowledgedAt = acknowledgmentDto.AcknowledgedAt
            };
            
            // Send acknowledgment only to the original sender
            await Clients.User(originalSenderUid).Client_UserReceiveCharacterDataAcknowledgment(forwardedAcknowledgment).ConfigureAwait(false);
            
            _logger.LogCallInfo(SpheneHubLogger.Args("LegacyAck - User:", UserUID, "Hash:", acknowledgmentDto.DataHash[..8]));
        }
        
        _SpheneMetrics.IncCounter(MetricsAPI.CounterUserPushData);
    }

    [Authorize(Policy = "Identified")]
    public async Task UserReportVisibility(Sphene.API.Dto.Visibility.UserVisibilityReportDto dto)
    {
        // Validate reporter UID matches connection
        if (!string.Equals(dto.Reporter.UID, UserUID, StringComparison.Ordinal))
        {
            _logger.LogCallWarning(SpheneHubLogger.Args("Invalid reporter UID", dto.Reporter.UID, "Conn", UserUID));
            return;
        }

        // Ensure there is a pair relationship
        var pairExists = await DbContext.ClientPairs.AsNoTracking()
            .AnyAsync(p => (p.UserUID == dto.Reporter.UID && p.OtherUserUID == dto.Target.UID) ||
                           (p.UserUID == dto.Target.UID && p.OtherUserUID == dto.Reporter.UID))
            .ConfigureAwait(false);
        if (!pairExists)
        {
            _logger.LogCallInfo(SpheneHubLogger.Args("Visibility report ignored - no pair", dto.Reporter.UID, dto.Target.UID));
            return;
        }

        // Create ordered key (A|B) to track mutual state regardless of report order
        var (uidA, uidB) = string.Compare(dto.Reporter.UID, dto.Target.UID, StringComparison.Ordinal) <= 0
            ? (dto.Reporter.UID, dto.Target.UID)
            : (dto.Target.UID, dto.Reporter.UID);
        var key = string.Create(uidA.Length + uidB.Length + 1, (uidA, uidB), (span, state) =>
        {
            state.uidA.AsSpan().CopyTo(span);
            span[state.uidA.Length] = '|';
            state.uidB.AsSpan().CopyTo(span.Slice(state.uidA.Length + 1));
        });

        var now = DateTime.UtcNow;
        var state = _mutualVisibilityStates.GetOrAdd(key, _ => new MutualVisibilityState { UidA = uidA, UidB = uidB });

        // Update state based on who reported
        if (string.Equals(dto.Reporter.UID, uidA, StringComparison.Ordinal))
        {
            state.LastSeenA = dto.IsVisible;
            state.LastReportA = now;
        }
        else
        {
            state.LastSeenB = dto.IsVisible;
            state.LastReportB = now;
        }

        // Determine mutual visibility immediately without a time window
        bool newMutual = state.LastSeenA && state.LastSeenB;

        if (newMutual != state.IsMutual)
        {
            state.IsMutual = newMutual;
            var mutualDto = new Sphene.API.Dto.Visibility.MutualVisibilityDto(new(uidA), new(uidB), newMutual, now);

            // Broadcast to both users if they are online
            var identA = await GetUserIdent(uidA).ConfigureAwait(false);
            var identB = await GetUserIdent(uidB).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(identA))
                await Clients.User(uidA).Client_UserMutualVisibilityUpdate(mutualDto).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(identB))
                await Clients.User(uidB).Client_UserMutualVisibilityUpdate(mutualDto).ConfigureAwait(false);

            _logger.LogCallInfo(SpheneHubLogger.Args("Mutual visibility updated", key, newMutual));
        }
        else
        {
            _logger.LogCallInfo(SpheneHubLogger.Args("Visibility report processed", key, "A", state.LastSeenA, "B", state.LastSeenB));
        }
    }

    [Authorize(Policy = "Identified")]
    public async Task UserRemovePair(UserDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));

        if (string.Equals(dto.User.UID, UserUID, StringComparison.Ordinal)) return;

        // check if client pair even exists
        ClientPair callerPair =
            await DbContext.ClientPairs.SingleOrDefaultAsync(w => w.UserUID == UserUID && w.OtherUserUID == dto.User.UID).ConfigureAwait(false);
        if (callerPair == null) return;

        var pairData = await GetPairInfo(UserUID, dto.User.UID).ConfigureAwait(false);

        // delete from database, send update info to users pair list
        DbContext.ClientPairs.Remove(callerPair);
        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        _logger.LogCallInfo(SpheneHubLogger.Args(dto, "Success"));

        await Clients.User(UserUID).Client_UserRemoveClientPair(dto).ConfigureAwait(false);

        // check if opposite entry exists
        if (!pairData.IndividuallyPaired) return;

        // check if other user is online, if no then there is no need to do anything further
        var otherIdent = await GetUserIdent(dto.User.UID).ConfigureAwait(false);
        if (otherIdent == null) return;

        // if the other user had paused the user the state will be offline for either, do nothing
        bool callerHadPaused = pairData.OwnPermissions?.IsPaused ?? false;

        // send updated individual pair status
        await Clients.User(dto.User.UID)
            .Client_UpdateUserIndividualPairStatusDto(new(new(UserUID), IndividualPairStatus.OneSided))
            .ConfigureAwait(false);

        UserPermissionSet? otherPermissions = pairData.OtherPermissions;
        bool otherHadPaused = otherPermissions?.IsPaused ?? true;

        // if the either had paused, do nothing
        if (callerHadPaused && otherHadPaused) return;

        var currentPairData = await GetPairInfo(UserUID, dto.User.UID).ConfigureAwait(false);

        // if neither user had paused each other and either is not in an unpaused group with each other, change state to offline
        if (!currentPairData?.IsSynced ?? true)
        {
            await Clients.User(UserUID).Client_UserSendOffline(dto).ConfigureAwait(false);
            await Clients.User(dto.User.UID).Client_UserSendOffline(new(new(UserUID))).ConfigureAwait(false);
        }
    }

    [Authorize(Policy = "Identified")]
    public async Task UserSetProfile(UserProfileDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));

        if (!string.Equals(dto.User.UID, UserUID, StringComparison.Ordinal)) throw new HubException("Cannot modify profile data for anyone but yourself");

        var existingData = await DbContext.UserProfileData.SingleOrDefaultAsync(u => u.UserUID == dto.User.UID).ConfigureAwait(false);

        if (existingData?.FlaggedForReport ?? false)
        {
            await Clients.Caller.Client_ReceiveServerMessage(MessageSeverity.Error, "Your profile is currently flagged for report and cannot be edited").ConfigureAwait(false);
            return;
        }

        if (existingData?.ProfileDisabled ?? false)
        {
            await Clients.Caller.Client_ReceiveServerMessage(MessageSeverity.Error, "Your profile was permanently disabled and cannot be edited").ConfigureAwait(false);
            return;
        }

        if (!string.IsNullOrEmpty(dto.ProfilePictureBase64))
        {
            byte[] imageData = Convert.FromBase64String(dto.ProfilePictureBase64);
            using MemoryStream ms = new(imageData);
            var format = await Image.DetectFormatAsync(ms).ConfigureAwait(false);
            if (!format.FileExtensions.Contains("png", StringComparer.OrdinalIgnoreCase))
            {
                await Clients.Caller.Client_ReceiveServerMessage(MessageSeverity.Error, "Your provided image file is not in PNG format").ConfigureAwait(false);
                return;
            }
            using var image = Image.Load<Rgba32>(imageData);

            if (image.Width > 256 || image.Height > 256 || (imageData.Length > 250 * 1024))
            {
                await Clients.Caller.Client_ReceiveServerMessage(MessageSeverity.Error, "Your provided image file is larger than 256x256 or more than 250KiB.").ConfigureAwait(false);
                return;
            }
        }

        if (existingData != null)
        {
            if (string.Equals("", dto.ProfilePictureBase64, StringComparison.OrdinalIgnoreCase))
            {
                existingData.Base64ProfileImage = null;
            }
            else if (dto.ProfilePictureBase64 != null)
            {
                existingData.Base64ProfileImage = dto.ProfilePictureBase64;
            }

            if (dto.IsNSFW != null)
            {
                existingData.IsNSFW = dto.IsNSFW.Value;
            }

            if (dto.Description != null)
            {
                existingData.UserDescription = dto.Description;
            }
        }
        else
        {
            UserProfileData userProfileData = new()
            {
                UserUID = dto.User.UID,
                Base64ProfileImage = dto.ProfilePictureBase64 ?? null,
                UserDescription = dto.Description ?? null,
                IsNSFW = dto.IsNSFW ?? false
            };

            await DbContext.UserProfileData.AddAsync(userProfileData).ConfigureAwait(false);
        }

        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        var allPairedUsers = await GetAllPairedUnpausedUsers().ConfigureAwait(false);
        var pairs = await GetOnlineUsers(allPairedUsers).ConfigureAwait(false);

        await Clients.Users(pairs.Select(p => p.Key)).Client_UserUpdateProfile(new(dto.User)).ConfigureAwait(false);
        await Clients.Caller.Client_UserUpdateProfile(new(dto.User)).ConfigureAwait(false);
    }

    [GeneratedRegex(@"^([a-z0-9_ '+&,\.\-\{\}]+\/)+([a-z0-9_ '+&,\.\-\{\}]+\.[a-z]{3,4})$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.ECMAScript)]
    private static partial Regex GamePathRegex();

    [GeneratedRegex(@"^[A-Z0-9]{40}$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.ECMAScript)]
    private static partial Regex HashRegex();

    [GeneratedRegex("^[-a-zA-Z0-9@:%._\\+~#=]{1,256}[\\.,][a-zA-Z0-9()]{1,6}\\b(?:[-a-zA-Z0-9()@:%_\\+.~#?&\\/=]*)$")]
    private static partial Regex UrlRegex();

    private ClientPair OppositeEntry(string otherUID) =>
                                    DbContext.ClientPairs.AsNoTracking().SingleOrDefault(w => w.User.UID == otherUID && w.OtherUser.UID == UserUID);

    public async Task UserUpdateAckYou(bool ackYou)
    {
        // Get user and paired users data sequentially to avoid database concurrency issues
        var user = await DbContext.Users.SingleAsync(u => u.UID == UserUID).ConfigureAwait(false);
        var allPairedUsers = await GetAllPairedUnpausedUsers().ConfigureAwait(false);
        var onlinePairs = await GetOnlineUsers(allPairedUsers).ConfigureAwait(false);

        // Get all relevant permissions in a single query
        var allPermissions = await DbContext.Permissions
            .Where(p => p.UserUID == UserUID || p.OtherUserUID == UserUID)
            .ToListAsync().ConfigureAwait(false);
            
        var ownPermissions = allPermissions.Where(p => string.Equals(p.UserUID, UserUID, StringComparison.Ordinal)).ToList();
        var otherUsersPermissions = allPermissions.Where(p => string.Equals(p.OtherUserUID, UserUID, StringComparison.Ordinal)).ToList();
            
        // Check if any permission actually needs to be updated
        bool statusChanged = ownPermissions.Any(p => p.AckYou != ackYou);
        if (!statusChanged)
        {
            _logger.LogCallInfo(SpheneHubLogger.Args("NO_CHANGE", UserUID, ackYou));
            return; // No change needed, avoid unnecessary database updates and network traffic
        }

        _logger.LogCallInfo(SpheneHubLogger.Args(ackYou));

        // Update permissions in memory - only update own AckYou
        foreach (var permission in ownPermissions)
        {
            permission.AckYou = ackYou;
        }
        
        // No longer update AckOther - partners read AckYou directly

        // Save all changes in a single transaction
        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        // Get all pair information in a single batch operation to avoid database concurrency issues
        var allPairInfo = await GetAllPairInfo(UserUID).ConfigureAwait(false);
        
        // Prepare notification tasks but don't await them individually
        var notificationTasks = new List<Task>();
        
        // Send updates to all online paired users about their updated AckOther value
        foreach (var pair in onlinePairs)
        {
            if (allPairInfo.TryGetValue(pair.Key, out var pairData) && pairData?.OtherPermissions != null)
            {
                notificationTasks.Add(Clients.User(pair.Key).Client_UserAckYouUpdate(
                    new UserPermissionsDto(user.ToUserData(), pairData.OtherPermissions.ToUserPermissions())));
            }
        }

        // Notify caller about their own AckYou update for each visible paired user only
        foreach (var pair in onlinePairs)
        {
            if (allPairInfo.TryGetValue(pair.Key, out var callerPairData) && callerPairData?.OwnPermissions != null)
            {
                notificationTasks.Add(Clients.Caller.Client_UserAckYouUpdate(
                    new UserPermissionsDto(new UserData(pair.Key, string.Empty), callerPairData.OwnPermissions.ToUserPermissions())));
            }
        }
        
        // Execute all notifications in parallel
        if (notificationTasks.Count > 0)
        {
            await Task.WhenAll(notificationTasks).ConfigureAwait(false);
        }
    }

    // UserUpdateAckOther method removed - AckOther is controlled by other player's AckYou
}

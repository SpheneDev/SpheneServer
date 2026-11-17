using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Sphene.API.Data;
using Sphene.API.Data.Enum;
using Sphene.API.Data.Extensions;
using Sphene.API.Dto.User;
using SpheneServer.Services;
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
    private static readonly Dictionary<string, SupporterStyleDto> SupporterStyles = new(StringComparer.Ordinal);

    [Authorize(Policy = "Identified")]
    public async Task UserAddPair(UserDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));

        // don't allow adding nothing
        var uid = dto.User.UID.Trim().TrimStart('@');
        if (string.Equals(uid, UserUID, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(uid)) return;

        // grab other user, check if it exists and if a pair already exists
        var loweredUid = uid.ToLowerInvariant();
        var otherUser = await DbContext.Users.SingleOrDefaultAsync(u => u.UID == uid || u.Alias != null && u.Alias.ToLower() == loweredUid).ConfigureAwait(false);
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

        if (await IsBlockedEitherWay(otherUser.UID).ConfigureAwait(false))
        {
            await Clients.Caller.Client_ReceiveServerMessage(MessageSeverity.Warning, $"Cannot pair with {dto.User.UID}").ConfigureAwait(false);
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

        var reversePermissions = await DbContext.Permissions.SingleOrDefaultAsync(u => u.UserUID == otherUser.UID && u.OtherUserUID == user.UID).ConfigureAwait(false);
        if (reversePermissions == null)
        {
            var otherDefaultPermissions = await DbContext.UserDefaultPreferredPermissions.AsNoTracking().SingleOrDefaultAsync(f => f.UserUID == otherUser.UID).ConfigureAwait(false);
            reversePermissions = new UserPermissionSet()
            {
                User = otherUser,
                OtherUser = user,
                DisableAnimations = otherDefaultPermissions?.DisableIndividualAnimations ?? false,
                DisableSounds = otherDefaultPermissions?.DisableIndividualSounds ?? false,
                DisableVFX = otherDefaultPermissions?.DisableIndividualVFX ?? false,
                IsPaused = false,
                Sticky = false
            };
            await DbContext.Permissions.AddAsync(reversePermissions).ConfigureAwait(false);
        }

        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        // get the opposite entry of the client pair
        var otherEntry = OppositeEntry(otherUser.UID);
        var otherIdent = await GetUserIdent(otherUser.UID).ConfigureAwait(false);

        var otherPermissions = existingData?.OtherPermissions ?? null;

        var ownPerm = permissions.ToUserPermissions(setSticky: true);
        var otherPerm = otherPermissions.ToUserPermissions();
        var reverseOwnPerm = reversePermissions.ToUserPermissions(setSticky: true);
        var reverseOtherPerm = permissions.ToUserPermissions();

        var userPairResponse = new UserPairDto(otherUser.ToUserData(),
            otherEntry == null ? IndividualPairStatus.OneSided : IndividualPairStatus.Bidirectional,
            ownPerm, otherPerm)
        {
            IsOutgoingIndividualPair = true,
            RemoteClientVersion = GetKnownClientVersion(otherUser.UID)
        };

        await Clients.User(user.UID).Client_UserAddClientPair(userPairResponse).ConfigureAwait(false);

        if (otherIdent != null && otherEntry == null)
        {
            var reverseUserPairResponse = new UserPairDto(user.ToUserData(),
                IndividualPairStatus.OneSided,
                reverseOwnPerm, reverseOtherPerm)
            {
                IsOutgoingIndividualPair = false,
                RemoteClientVersion = GetKnownClientVersion(user.UID)
            };

            await Clients.User(otherUser.UID).Client_UserAddClientPair(reverseUserPairResponse).ConfigureAwait(false);
        }

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
            await Clients.User(UserUID).Client_UserSendOnline(await CreateOnlineIdentDtoAsync(otherUser.ToUserData(), otherIdent).ConfigureAwait(false)).ConfigureAwait(false);
            await Clients.User(otherUser.UID).Client_UserSendOnline(await CreateOnlineIdentDtoAsync(user.ToUserData(), UserCharaIdent).ConfigureAwait(false)).ConfigureAwait(false);
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
        var gposedUsers = await GetUsersInGpose(pairs.Keys).ConfigureAwait(false);

        await SendOnlineToAllPairedUsers().ConfigureAwait(false);

        _spheneCensus.PublishStatistics(UserUID, censusData);

        return pairs.Select(p => new OnlineUserIdentDto(new UserData(p.Key), p.Value, GetKnownClientVersion(p.Key), gposedUsers.Contains(p.Key))).ToList();
    }

    [Authorize(Policy = "Identified")]
    public async Task UserUpdateGposeState(bool isInGpose)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args("IsInGpose:", isInGpose));

        if (isInGpose)
            await _redis.AddAsync(GposeStateKey, "1", TimeSpan.FromHours(8)).ConfigureAwait(false);
        else
            await _redis.RemoveAsync(GposeStateKey).ConfigureAwait(false);

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
            return new UserFullPairDto(new UserData(p.Key, p.Value.Alias, p.Value.IsSupporter, p.Value.DisplayName),
                p.Value.ToIndividualPairStatus(),
                p.Value.GIDs.Where(g => !string.Equals(g, Constants.IndividualKeyword, StringComparison.OrdinalIgnoreCase)).ToList(),
                p.Value.OwnPermissions.ToUserPermissions(setSticky: true),
                p.Value.OtherPermissions.ToUserPermissions(),
                otherAllowsMods)
            {
                IsOutgoingIndividualPair = p.Value.IsOutgoingIndividualPair,
                OtherSupporterStyle = _supporterFeaturesEnabled && SupporterStyles.TryGetValue(p.Key, out var supporterStyle) ? supporterStyle : null,
                RemoteClientVersion = GetKnownClientVersion(p.Key)
            };
        }).ToList();
    }

    [Authorize(Policy = "Identified")]
    public Task UserUpdatePenumbraReceivePreference(bool allowMods)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args("AllowReceivingPenumbraMods", allowMods));
        PenumbraReceivePreferences[UserUID] = allowMods;
        return NotifyPairedUsersAboutPenumbraPreferenceAsync(allowMods);
    }

    [Authorize(Policy = "Identified")]
    public async Task UserUpdateSupporterStyle(SupporterStyleDto? style)
    {
        _logger.LogCallInfo();

        if (style == null)
        {
            SupporterStyles.Remove(UserUID);
        }
        else
        {
            SupporterStyles[UserUID] = style;
        }

        await NotifyPairedUsersAboutSupporterStyleAsync().ConfigureAwait(false);
    }

    private async Task NotifyPairedUsersAboutSupporterStyleAsync()
    {
        var pairedUsers = await GetAllPairedUnpausedUsers().ConfigureAwait(false);
        if (pairedUsers.Count == 0)
        {
            return;
        }

        var dto = new UserSupporterStyleDto(new UserData(UserUID),
            SupporterStyles.TryGetValue(UserUID, out var style) ? style : null);
        await Clients.Users(pairedUsers).Client_UserSupporterStyleUpdate(dto).ConfigureAwait(false);
    }

    private sealed record SupporterIconFile(string Path, string Name, bool Animated);
    private const int MaxSupporterIconBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan SupporterIconCatalogTtl = TimeSpan.FromMinutes(1);
    private static readonly HashSet<string> SupporterIconExtensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".gif", ".webp", ".apng" };
    private static readonly HashSet<string> SupporterAnimatedIconExtensions = new(StringComparer.OrdinalIgnoreCase) { ".gif", ".webp", ".apng" };
    private static readonly ConcurrentDictionary<string, byte[]> SupporterIconBytes = new(StringComparer.Ordinal);
    private static readonly System.Threading.Lock SupporterIconCatalogLock = new();
    private static Dictionary<string, SupporterIconFile>? _supporterIconCatalog;
    private static DateTime _supporterIconCatalogBuiltAt = DateTime.MinValue;

    private Dictionary<string, SupporterIconFile> GetSupporterIconCatalog()
    {
        var now = DateTime.UtcNow;
        lock (SupporterIconCatalogLock)
        {
            if (_supporterIconCatalog != null && now - _supporterIconCatalogBuiltAt < SupporterIconCatalogTtl)
            {
                return _supporterIconCatalog;
            }

            var catalog = new Dictionary<string, SupporterIconFile>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!string.IsNullOrWhiteSpace(_supporterIconDirectory) && Directory.Exists(_supporterIconDirectory))
                {
                    foreach (var file in Directory.EnumerateFiles(_supporterIconDirectory))
                    {
                        var fileName = Path.GetFileName(file);
                        var ext = Path.GetExtension(fileName);
                        if (!SupporterIconExtensions.Contains(ext))
                        {
                            continue;
                        }

                        catalog[fileName] = new SupporterIconFile(file,
                            Path.GetFileNameWithoutExtension(fileName),
                            SupporterAnimatedIconExtensions.Contains(ext));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogCallWarning(SpheneHubLogger.Args("Supporter icon catalog scan failed", ex.Message));
            }

            _supporterIconCatalog = catalog;
            _supporterIconCatalogBuiltAt = now;
            return catalog;
        }
    }

    [Authorize(Policy = "Identified")]
    public Task<List<SupporterIconInfoDto>> SupporterGetIcons()
    {
        var icons = new List<SupporterIconInfoDto>();
        if (_supporterFeaturesEnabled)
        {
            foreach (var kvp in GetSupporterIconCatalog())
            {
                icons.Add(new SupporterIconInfoDto { Id = kvp.Key, Name = kvp.Value.Name, Animated = kvp.Value.Animated });
            }
            icons.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        }
        return Task.FromResult(icons);
    }

    [Authorize(Policy = "Identified")]
    public async Task<byte[]?> SupporterGetIconFile(string iconId)
    {
        if (!_supporterFeaturesEnabled || string.IsNullOrWhiteSpace(iconId))
        {
            return null;
        }
        if (!string.Equals(Path.GetFileName(iconId), iconId, StringComparison.Ordinal))
        {
            return null;
        }
        if (!GetSupporterIconCatalog().TryGetValue(iconId, out var file))
        {
            return null;
        }
        if (SupporterIconBytes.TryGetValue(iconId, out var cached))
        {
            return cached;
        }
        try
        {
            var bytes = await File.ReadAllBytesAsync(file.Path).ConfigureAwait(false);
            if (bytes.Length == 0 || bytes.Length > MaxSupporterIconBytes)
            {
                return null;
            }
            SupporterIconBytes[iconId] = bytes;
            return bytes;
        }
        catch (Exception ex)
        {
            _logger.LogCallWarning(SpheneHubLogger.Args("Supporter icon read failed", iconId, ex.Message));
            return null;
        }
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
    public async Task<DateTime?> UserActivateSupporterTrial()
    {
        var user = await DbContext.Users.SingleAsync(u => u.UID == UserUID).ConfigureAwait(false);
        if (user.SupporterTrialStartUtc.HasValue || user.IsSupporter)
        {
            return user.SupporterTrialStartUtc;
        }

        user.SupporterTrialStartUtc = DateTime.UtcNow;
        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        // paired users should see the freshly activated perk without a reconnect
        var pairedUsers = await GetAllPairedUnpausedUsers().ConfigureAwait(false);
        if (pairedUsers.Count > 0)
        {
            var dto = new UserDto(new UserData(user.UID, user.Alias, true, user.DisplayName));
            await Clients.Users(pairedUsers).Client_UserIdentityUpdated(dto).ConfigureAwait(false);
        }
        return user.SupporterTrialStartUtc;
    }

    [Authorize(Policy = "Identified")]
    public async Task<UserProfileDto> UserGetProfile(UserDto user)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(user));

        var requestedUid = await ResolveUidOrAliasAsync(user.User.UID).ConfigureAwait(false);
        if (requestedUid == null)
        {
            return new UserProfileDto(user.User, false, null, null, "This user does not exist.");
        }

        var allUserPairs = await GetAllPairedUnpausedUsers().ConfigureAwait(false);

        if (!allUserPairs.Contains(requestedUid, StringComparer.Ordinal) && !string.Equals(requestedUid, UserUID, StringComparison.Ordinal))
        {
            return new UserProfileDto(user.User, false, null, null, "Due to the pause status you cannot access this users profile.");
        }

        var data = await DbContext.UserProfileData.Include(u => u.User).SingleOrDefaultAsync(u => u.UserUID == requestedUid).ConfigureAwait(false);
        if (data == null) return new UserProfileDto(user.User, false, null, null, null);

        if (data.FlaggedForReport) return new UserProfileDto(user.User, true, null, null, "This profile is flagged for report and pending evaluation");
        if (data.ProfileDisabled) return new UserProfileDto(user.User, true, null, null, "This profile was permanently disabled");

        var isSelf = string.Equals(requestedUid, UserUID, StringComparison.Ordinal);
        var vis = (ProfileVisibility)data.ProfileVisibility;
        var show = (ProfileVisibility flag) => isSelf || !vis.HasFlag(flag);
        return new UserProfileDto(data.User.ToUserData(), false, data.IsNSFW,
            show(ProfileVisibility.HideProfilePicture) ? data.Base64ProfileImage : null,
            show(ProfileVisibility.HideDescription) ? data.UserDescription : null,
            show(ProfileVisibility.HideStatusMessage) ? data.StatusMessage : null,
            show(ProfileVisibility.HideTags) ? data.ProfileTags : null,
            show(ProfileVisibility.HideBanner) ? data.Base64BannerImage : null,
            Utils.Extensions.NormalizeThemeForSupporter(data.ProfileTheme,
                Utils.Extensions.IsSupporterEffective(data.User)),
            data.ProfileVisibility,
            Utils.Extensions.IsSupporterEffective(data.User) ? data.Base64RowImage : null,
            show(ProfileVisibility.HideLinks)
                ? DeserializeJson<List<UserProfileLinkDto>>(data.LinksJson) : null);
    }

    /// <summary>
    /// Lazily fetched profile extras (gallery + blog) - kept off the hot
    /// <see cref="UserGetProfile"/> path so pair-list profile pulls stay light.
    /// </summary>
    [Authorize(Policy = "Identified")]
    public async Task<UserProfileExtrasDto> UserGetProfileExtras(UserDto user)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(user));

        var requestedUid = await ResolveUidOrAliasAsync(user.User.UID).ConfigureAwait(false);
        if (requestedUid == null)
        {
            return new UserProfileExtrasDto(user.User, null, null);
        }

        var allUserPairs = await GetAllPairedUnpausedUsers().ConfigureAwait(false);
        if (!allUserPairs.Contains(requestedUid, StringComparer.Ordinal) && !string.Equals(requestedUid, UserUID, StringComparison.Ordinal))
        {
            return new UserProfileExtrasDto(user.User, null, null);
        }

        var data = await DbContext.UserProfileData.SingleOrDefaultAsync(u => u.UserUID == requestedUid).ConfigureAwait(false);
        if (data == null || data.FlaggedForReport || data.ProfileDisabled)
        {
            return new UserProfileExtrasDto(user.User, null, null);
        }

        var isSelf = string.Equals(requestedUid, UserUID, StringComparison.Ordinal);
        var vis = (ProfileVisibility)data.ProfileVisibility;
        var show = (ProfileVisibility flag) => isSelf || !vis.HasFlag(flag);

        List<UserProfileGalleryImageDto>? gallery = null;
        List<UserProfileGalleryAlbumDto>? albums = null;
        if (show(ProfileVisibility.HideGallery))
        {
            var images = await DbContext.UserProfileGalleryImages
                .Where(g => g.UserUID == requestedUid)
                .OrderBy(g => g.SortOrder).ThenByDescending(g => g.CreatedAt)
                .Select(g => new { g.Id, g.ImageBase64, g.FileName,
                    g.ContentType, g.Size, g.Caption, g.SortOrder, g.CreatedAt, g.AlbumId })
                .ToListAsync().ConfigureAwait(false);
            var likeData = await GetProfileGalleryLikeData(images.Select(g => g.Id).ToList())
                .ConfigureAwait(false);
            gallery = images.Select(g => new UserProfileGalleryImageDto(g.Id, g.ImageBase64,
                g.FileName, g.ContentType, g.Size, g.Caption, g.SortOrder, g.CreatedAt,
                g.AlbumId, likeData.Counts.GetValueOrDefault(g.Id), likeData.Mine.Contains(g.Id)))
                .ToList();

            albums = await DbContext.UserProfileGalleryAlbums
                .Where(a => a.UserUID == requestedUid)
                .OrderBy(a => a.SortOrder).ThenBy(a => a.CreatedAt)
                .Select(a => new UserProfileGalleryAlbumDto(a.Id, a.Name, a.SortOrder,
                    a.Images.Count))
                .ToListAsync().ConfigureAwait(false);
        }

        List<UserProfileBlogPostDto>? blog = null;
        if (show(ProfileVisibility.HideBlog))
        {
            // Status + scope gate: self sees everything; readers get published
            // (or due-scheduled) posts within the visibility scope they qualify
            // for - Public for all, Pairs for paired users only.
            var paired = allUserPairs.Contains(requestedUid, StringComparer.Ordinal);
            var posts = await DbContext.UserProfileBlogPosts
                .Where(b => b.UserUID == requestedUid)
                .OrderByDescending(b => b.Pinned).ThenByDescending(b => b.CreatedAt)
                .Select(b => new { b.Id, b.Title, b.Body, b.Pinned, b.CreatedAt, b.UpdatedAt,
                    b.Status, b.VisibilityScope, b.ScheduledForUtc, b.CoAuthorUidsJson,
                    b.CommentCount, b.CommentsLocked, b.CoverMediaHash, b.ViewCount })
                .ToListAsync().ConfigureAwait(false);
            var nowUtc = DateTime.UtcNow;
            blog = posts.Where(b => isSelf
                    || PublishingService.IsVisible(b.Status, b.ScheduledForUtc, nowUtc)
                        && (b.VisibilityScope == (int)BlogVisibilityScope.Public
                            || (b.VisibilityScope == (int)BlogVisibilityScope.Pairs && paired)))
                .Select(b => new UserProfileBlogPostDto(b.Id, b.Title, b.Body, b.Pinned,
                    b.CreatedAt, b.UpdatedAt, b.Status, b.VisibilityScope, b.ScheduledForUtc,
                    PublishingService.ParseCoAuthors(b.CoAuthorUidsJson),
                    b.CommentCount, b.CommentsLocked, b.CoverMediaHash, b.ViewCount))
                .ToList();
        }

        return new UserProfileExtrasDto(data.User?.ToUserData() ?? user.User, gallery, blog, albums);
    }

    /// <summary>Upserts a profile gallery image - self-owned content only.</summary>
    [Authorize(Policy = "Identified")]
    public async Task<UserProfileGalleryImageDto?> UserUpsertProfileGalleryImage(
        UserProfileGalleryImageUpdateDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));
        if (!string.Equals(dto.User.UID, UserUID, StringComparison.Ordinal)) return null;

        var entity = dto.Id == 0 ? null : await DbContext.UserProfileGalleryImages
            .SingleOrDefaultAsync(g => g.Id == dto.Id && g.UserUID == UserUID).ConfigureAwait(false);
        if (entity == null)
        {
            entity = new UserProfileGalleryImage { UserUID = UserUID, CreatedAt = DateTime.UtcNow };
            await DbContext.UserProfileGalleryImages.AddAsync(entity).ConfigureAwait(false);
        }
        if (!string.IsNullOrEmpty(dto.ImageBase64))
        {
            entity.ImageBase64 = dto.ImageBase64;
            entity.FileName = dto.FileName;
            entity.ContentType = dto.ContentType;
            entity.Size = dto.Size;
        }
        if (dto.AlbumId != entity.AlbumId)
        {
            var albumOk = dto.AlbumId == null || await DbContext.UserProfileGalleryAlbums
                .AnyAsync(a => a.Id == dto.AlbumId && a.UserUID == UserUID).ConfigureAwait(false);
            if (albumOk)
            {
                entity.AlbumId = dto.AlbumId;
            }
        }
        entity.Caption = dto.Caption;
        entity.SortOrder = dto.SortOrder;
        await DbContext.SaveChangesAsync().ConfigureAwait(false);
        var likes = await DbContext.UserProfileGalleryImageLikes
            .CountAsync(l => l.ImageId == entity.Id).ConfigureAwait(false);
        var mine = await DbContext.UserProfileGalleryImageLikes
            .AnyAsync(l => l.ImageId == entity.Id && l.LikerUID == UserUID).ConfigureAwait(false);
        return new UserProfileGalleryImageDto(entity.Id, entity.ImageBase64, entity.FileName,
            entity.ContentType, entity.Size, entity.Caption, entity.SortOrder, entity.CreatedAt,
            entity.AlbumId, likes, mine);
    }

    [Authorize(Policy = "Identified")]
    public async Task<bool> UserDeleteProfileGalleryImage(UserProfileItemDeleteDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));
        if (!string.Equals(dto.User.UID, UserUID, StringComparison.Ordinal)) return false;
        var entity = await DbContext.UserProfileGalleryImages
            .SingleOrDefaultAsync(g => g.Id == dto.Id && g.UserUID == UserUID).ConfigureAwait(false);
        if (entity == null) return false;
        DbContext.UserProfileGalleryImages.Remove(entity);
        await DbContext.SaveChangesAsync().ConfigureAwait(false);
        return true;
    }

    /// <summary>Upserts a profile gallery album - self-owned content only.</summary>
    [Authorize(Policy = "Identified")]
    public async Task<UserProfileGalleryAlbumDto?> UserUpsertProfileGalleryAlbum(
        UserProfileGalleryAlbumUpdateDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));
        if (!string.Equals(dto.User.UID, UserUID, StringComparison.Ordinal)) return null;

        var entity = dto.Id == 0 ? null : await DbContext.UserProfileGalleryAlbums
            .SingleOrDefaultAsync(a => a.Id == dto.Id && a.UserUID == UserUID).ConfigureAwait(false);
        if (entity == null)
        {
            entity = new UserProfileGalleryAlbum { UserUID = UserUID, CreatedAt = DateTime.UtcNow };
            await DbContext.UserProfileGalleryAlbums.AddAsync(entity).ConfigureAwait(false);
        }
        entity.Name = dto.Name;
        entity.SortOrder = dto.SortOrder;
        await DbContext.SaveChangesAsync().ConfigureAwait(false);
        var count = await DbContext.UserProfileGalleryImages
            .CountAsync(g => g.AlbumId == entity.Id).ConfigureAwait(false);
        return new UserProfileGalleryAlbumDto(entity.Id, entity.Name, entity.SortOrder, count);
    }

    /// <summary>Deletes a profile album - photos inside move back to the unsorted pool.</summary>
    [Authorize(Policy = "Identified")]
    public async Task<bool> UserDeleteProfileGalleryAlbum(UserProfileItemDeleteDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));
        if (!string.Equals(dto.User.UID, UserUID, StringComparison.Ordinal)) return false;
        var entity = await DbContext.UserProfileGalleryAlbums
            .SingleOrDefaultAsync(a => a.Id == dto.Id && a.UserUID == UserUID).ConfigureAwait(false);
        if (entity == null) return false;
        DbContext.UserProfileGalleryAlbums.Remove(entity);
        await DbContext.SaveChangesAsync().ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Toggles the caller's like on a profile photo - allowed for the owner
    /// and paired users, mirroring <see cref="UserGetProfileExtras"/> gating.
    /// </summary>
    [Authorize(Policy = "Identified")]
    public async Task<UserProfileGalleryImageLikeResultDto?> UserToggleProfileGalleryImageLike(
        UserProfileGalleryImageLikeDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));
        if (!string.Equals(dto.User.UID, UserUID, StringComparison.Ordinal)) return null;

        var image = await DbContext.UserProfileGalleryImages
            .SingleOrDefaultAsync(g => g.Id == dto.ImageId).ConfigureAwait(false);
        if (image == null) return null;

        var allUserPairs = await GetAllPairedUnpausedUsers().ConfigureAwait(false);
        var isSelf = string.Equals(image.UserUID, UserUID, StringComparison.Ordinal);
        if (!isSelf && !allUserPairs.Contains(image.UserUID, StringComparer.Ordinal))
        {
            return null;
        }

        var like = await DbContext.UserProfileGalleryImageLikes
            .FindAsync(dto.ImageId, UserUID).ConfigureAwait(false);
        var likedNow = like == null;
        if (likedNow)
        {
            await DbContext.UserProfileGalleryImageLikes.AddAsync(
                new UserProfileGalleryImageLike
                {
                    ImageId = dto.ImageId, LikerUID = UserUID, CreatedAt = DateTime.UtcNow
                }).ConfigureAwait(false);
        }
        else
        {
            DbContext.UserProfileGalleryImageLikes.Remove(like);
        }
        await DbContext.SaveChangesAsync().ConfigureAwait(false);
        var total = await DbContext.UserProfileGalleryImageLikes
            .CountAsync(l => l.ImageId == dto.ImageId).ConfigureAwait(false);
        return new UserProfileGalleryImageLikeResultDto(dto.ImageId, likedNow, total);
    }

    /// <summary>Like counts plus the caller's own likes for a set of profile photos.</summary>
    private async Task<(Dictionary<long, int> Counts, HashSet<long> Mine)> GetProfileGalleryLikeData(
        List<long> imageIds)
    {
        var counts = await DbContext.UserProfileGalleryImageLikes
            .Where(l => imageIds.Contains(l.ImageId))
            .GroupBy(l => l.ImageId)
            .Select(x => new { x.Key, Count = x.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count)
            .ConfigureAwait(false);
        var mine = (await DbContext.UserProfileGalleryImageLikes
            .Where(l => l.LikerUID == UserUID && imageIds.Contains(l.ImageId))
            .Select(l => l.ImageId)
            .ToListAsync().ConfigureAwait(false)).ToHashSet();
        return (counts, mine);
    }

    /// <summary>Upserts a profile blog post - self-owned content only.</summary>
    [Authorize(Policy = "Identified")]
    public async Task<UserProfileBlogPostDto?> UserUpsertProfileBlogPost(UserProfileBlogPostUpdateDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));
        if (!string.Equals(dto.User.UID, UserUID, StringComparison.Ordinal)) return null;

        var entity = dto.Id == 0 ? null : await DbContext.UserProfileBlogPosts
            .SingleOrDefaultAsync(b => b.Id == dto.Id).ConfigureAwait(false);
        if (entity == null)
        {
            entity = new UserProfileBlogPost { UserUID = UserUID, CreatedAt = DateTime.UtcNow };
            await DbContext.UserProfileBlogPosts.AddAsync(entity).ConfigureAwait(false);
        }
        // Co-author write right: the owner or a listed co-author may edit -
        // only the owner owns the row (delete stays owner-gated).
        var coAuthors = PublishingService.ParseCoAuthors(entity.CoAuthorUidsJson);
        var isOwner = string.Equals(entity.UserUID, UserUID, StringComparison.Ordinal);
        var isCoAuthor = coAuthors.Contains(UserUID, StringComparer.Ordinal);
        if (!isOwner && !isCoAuthor) return null;
        // Title is optional - but a post needs a title OR a body.
        if (string.IsNullOrWhiteSpace(dto.Title)
            && string.IsNullOrWhiteSpace(dto.Body)) return null;

        var resolution = _publishing.ResolveBlogWrite(isOwner, isCoAuthor,
            dto.StatusIntent.HasValue ? (ContentPublishStatus)dto.StatusIntent.Value
                : (ContentPublishStatus)entity.Status == ContentPublishStatus.Draft
                    ? ContentPublishStatus.Published
                    : (ContentPublishStatus)entity.Status,
            (ContentPublishStatus)entity.Status);
        if (resolution.Verdict == PublishingService.WriteVerdict.Denied) return null;

        entity.Title = dto.Title;
        entity.Body = dto.Body;
        entity.Pinned = dto.Pinned;
        var prev = (ContentPublishStatus)entity.Status;
        entity.Status = (int)resolution.EffectiveStatus;
        entity.ScheduledForUtc = resolution.EffectiveStatus == ContentPublishStatus.Scheduled
            ? dto.ScheduledForUtc ?? entity.ScheduledForUtc
            : null;
        if (dto.VisibilityScope.HasValue)
        {
            entity.VisibilityScope = Math.Clamp(dto.VisibilityScope.Value,
                (int)BlogVisibilityScope.Public, (int)BlogVisibilityScope.Private);
        }
        if (isOwner && dto.CoAuthorUids != null)
        {
            entity.CoAuthorUidsJson = dto.CoAuthorUids.Count == 0
                ? null
                : SerializeJson(dto.CoAuthorUids);
        }
        if (dto.CoverMediaHash != null)
        {
            entity.CoverMediaHash = dto.CoverMediaHash.Length == 0 ? null : dto.CoverMediaHash;
        }
        entity.UpdatedAt = DateTime.UtcNow;
        await DbContext.SaveChangesAsync().ConfigureAwait(false);
        await PushUserProfileChangedAsync(UserUID).ConfigureAwait(false);
        return new UserProfileBlogPostDto(entity.Id, entity.Title, entity.Body,
            entity.Pinned, entity.CreatedAt, entity.UpdatedAt, entity.Status,
            entity.VisibilityScope, entity.ScheduledForUtc,
            PublishingService.ParseCoAuthors(entity.CoAuthorUidsJson),
            entity.CommentCount, entity.CommentsLocked, entity.CoverMediaHash,
            entity.ViewCount);
    }

    /// <summary>Marks one Chronicle as seen by the caller - deduplicated per
    /// (post, viewer) in user_profile_blog_post_views so repeat opens never
    /// inflate the count; the owner's own reads and posts outside the
    /// caller's visibility scope never count.</summary>
    [Authorize(Policy = "Identified")]
    public async Task UserMarkBlogPostViewed(long postId)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(postId));
        var post = await DbContext.UserProfileBlogPosts.AsNoTracking()
            .Where(b => b.Id == postId)
            .Select(b => new { b.UserUID, b.Status, b.ScheduledForUtc, b.VisibilityScope })
            .SingleOrDefaultAsync().ConfigureAwait(false);
        if (post == null) return;
        if (string.Equals(post.UserUID, UserUID, StringComparison.Ordinal)) return;

        // Same read gate as UserGetProfileExtras: published (or due-scheduled)
        // within scope - Public for everyone, Pairs for paired users only.
        if (!PublishingService.IsVisible(post.Status, post.ScheduledForUtc, DateTime.UtcNow))
        {
            return;
        }
        if (post.VisibilityScope != (int)BlogVisibilityScope.Public)
        {
            if (post.VisibilityScope != (int)BlogVisibilityScope.Pairs) return;
            var paired = (await GetAllPairedUnpausedUsers().ConfigureAwait(false))
                .Contains(post.UserUID, StringComparer.Ordinal);
            if (!paired) return;
        }

        // Dedup insert first - ON CONFLICT keeps the (post, viewer) pair
        // unique, so only the row that wins the insert bumps the counter.
        var inserted = await DbContext.Database.ExecuteSqlRawAsync(
            "INSERT INTO user_profile_blog_post_views (post_id, viewer_uid, viewed_at)"
            + " VALUES ({0}, {1}, {2}) ON CONFLICT (post_id, viewer_uid) DO NOTHING",
            postId, UserUID, DateTime.UtcNow).ConfigureAwait(false);
        if (inserted > 0)
        {
            await DbContext.Database.ExecuteSqlRawAsync(
                "UPDATE user_profile_blog_posts SET view_count = view_count + 1 WHERE id = {0}",
                postId).ConfigureAwait(false);
        }
    }

    [Authorize(Policy = "Identified")]
    public async Task<bool> UserDeleteProfileBlogPost(UserProfileItemDeleteDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));
        if (!string.Equals(dto.User.UID, UserUID, StringComparison.Ordinal)) return false;
        var entity = await DbContext.UserProfileBlogPosts
            .SingleOrDefaultAsync(b => b.Id == dto.Id && b.UserUID == UserUID).ConfigureAwait(false);
        if (entity == null) return false;

        // Target rows hang off the post id - comments, their reactions and
        // reports would orphan on a hard delete, so they go in the same txn.
        var commentIds = await DbContext.CommunityComments
            .Where(c => c.TargetKind == (int)CommentTargetKind.Blog && c.TargetId == dto.Id)
            .Select(c => c.Id).ToListAsync().ConfigureAwait(false);
        if (commentIds.Count > 0)
        {
            DbContext.CommunityComments.RemoveRange(DbContext.CommunityComments
                .Where(c => c.TargetKind == (int)CommentTargetKind.Blog && c.TargetId == dto.Id));
            DbContext.CommunityReactions.RemoveRange(DbContext.CommunityReactions
                .Where(r => r.TargetKind == (int)ReactionTargetKind.Comment
                    && commentIds.Contains(r.TargetId)));
            DbContext.CommunityReports.RemoveRange(DbContext.CommunityReports
                .Where(r => r.TargetKind == (int)ReportTargetKind.Comment
                    && commentIds.Contains(r.TargetId)));
        }
        DbContext.CommunityReactions.RemoveRange(DbContext.CommunityReactions
            .Where(r => r.TargetKind == (int)ReactionTargetKind.Blog && r.TargetId == dto.Id));
        DbContext.CommunityReports.RemoveRange(DbContext.CommunityReports
            .Where(r => r.TargetKind == (int)ReportTargetKind.Blog && r.TargetId == dto.Id));
        DbContext.UserProfileBlogPosts.Remove(entity);
        await DbContext.SaveChangesAsync().ConfigureAwait(false);
        await PushUserProfileChangedAsync(UserUID).ConfigureAwait(false);
        return true;
    }

    [Authorize(Policy = "Identified")]
    public async Task UserRequestCharacterDataRefresh(UserDto userDto)
    {
        var targetUid = userDto.User.UID?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(targetUid) || string.Equals(targetUid, UserUID, StringComparison.Ordinal))
        {
            return;
        }

        var pairInfo = await GetPairInfo(UserUID, targetUid).ConfigureAwait(false);
        if (pairInfo == null || !pairInfo.IsSynced)
        {
            _logger.LogCallInfo(SpheneHubLogger.Args("Character data refresh denied - users not synced", UserUID, targetUid));
            return;
        }

        if (pairInfo.OwnPermissions?.IsPaused == true || pairInfo.OtherPermissions?.IsPaused == true)
        {
            _logger.LogCallInfo(SpheneHubLogger.Args("Character data refresh denied - paused pair", UserUID, targetUid));
            return;
        }

        var targetIdent = await GetUserIdent(targetUid).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(targetIdent))
        {
            return;
        }

        await Clients.User(targetUid).Client_UserCharacterDataRefreshRequested(new(UserUID)).ConfigureAwait(false);
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

        // Only send to recipients that are actually connected on this shard right now
        if (recipientUids.Count > 0)
        {
            recipientUids = recipientUids.Where(uid => _userConnections.ContainsKey(uid)).ToList();
        }

        _logger.LogCallInfo(SpheneHubLogger.Args(recipientUids.Count));

        // Get data hash from character data
        var dataHash = dto.CharaData.DataHash.Value;
        await StoreNewHash(UserUID, dataHash).ConfigureAwait(false);
        
        var requiresAck = dto.RequiresAcknowledgment;
        string? sessionId = null;
        if (requiresAck)
        {
            // Create batch acknowledgment session for tracking individual recipients
            sessionId = _batchAcknowledgmentTracker.CreateSession(dataHash, UserUID, recipientUids);
            
            // Keep legacy mapping for backward compatibility (will be removed later)
            _acknowledgmentSenders.AddOrUpdate(dataHash, new LegacyAckSender(UserUID, DateTime.UtcNow), (key, oldValue) => new LegacyAckSender(UserUID, DateTime.UtcNow));
            
            // Log session-based acknowledgment info
            _logger.LogCallInfo(SpheneHubLogger.Args("Hash:", ShortLogToken(dataHash), "SessionId:", ShortLogToken(sessionId), "Recipients:", recipientUids.Count));
        }
        
        var characterDataDto = new OnlineUserCharaDataDto(new UserData(UserUID), dto.CharaData)
        {
            DataHash = dataHash,
            RequiresAcknowledgment = requiresAck,
            SessionId = sessionId
        };

        await Clients.Users(recipientUids).Client_UserReceiveCharacterData(characterDataDto).ConfigureAwait(false);

        _spheneCensus.PublishStatistics(UserUID, dto.CensusDataDto);

        _SpheneMetrics.IncCounter(MetricsAPI.CounterUserPushData);
        _SpheneMetrics.IncCounter(MetricsAPI.CounterUserPushDataTo, recipientUids.Count);
    }

    [Authorize(Policy = "Identified")]
    public Task UserSendCharacterDataAcknowledgmentV2(CharacterDataAcknowledgmentEventDto acknowledgmentEventDto)
    {
        if (acknowledgmentEventDto == null)
        {
            _logger.LogCallWarning(SpheneHubLogger.Args("Invalid acknowledgment payload - User:", UserUID));
            LogAcknowledgmentStat(false, "v2", "invalid_payload", string.Empty, string.Empty, null, null);
            return Task.CompletedTask;
        }

        if (acknowledgmentEventDto.Acknowledgment == null)
        {
            _logger.LogCallWarning(SpheneHubLogger.Args("Invalid acknowledgment payload (missing nested acknowledgment) - User:", UserUID, "Version:", acknowledgmentEventDto.ContractVersion));
            LogAcknowledgmentStat(false, "v2", "invalid_nested_payload", string.Empty, string.Empty, null, acknowledgmentEventDto.ContractVersion.ToString());
            return Task.CompletedTask;
        }

        _logger.LogCallInfo(SpheneHubLogger.Args("AckV2 - User:", UserUID, "Version:", acknowledgmentEventDto.ContractVersion));
        LogAcknowledgmentStat(true, "v2", "accepted", acknowledgmentEventDto.Acknowledgment.DataHash, acknowledgmentEventDto.Acknowledgment.SessionId, acknowledgmentEventDto.Acknowledgment.Success, acknowledgmentEventDto.ContractVersion.ToString());
        return UserSendCharacterDataAcknowledgment(acknowledgmentEventDto.Acknowledgment);
    }

    [Authorize(Policy = "Identified")]
    public async Task UserSendCharacterDataAcknowledgment(CharacterDataAcknowledgmentDto acknowledgmentDto)
    {
        if (acknowledgmentDto == null)
        {
            _logger.LogCallWarning(SpheneHubLogger.Args("Invalid acknowledgment payload - User:", UserUID));
            LogAcknowledgmentStat(false, "unknown", "invalid_payload", string.Empty, string.Empty, null, null);
            return;
        }

        var normalizedHash = acknowledgmentDto.DataHash?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedHash))
        {
            _logger.LogCallWarning(SpheneHubLogger.Args("Invalid acknowledgment hash - User:", UserUID));
            LogAcknowledgmentStat(false, "unknown", "invalid_hash", normalizedHash, normalizedSessionId: string.Empty, acknowledgmentDto.Success, null);
            return;
        }

        var normalizedSessionId = acknowledgmentDto.SessionId?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(acknowledgmentDto.User?.UID)
            && !string.Equals(acknowledgmentDto.User.UID, UserUID, StringComparison.Ordinal))
        {
            _logger.LogCallInfo(SpheneHubLogger.Args("Acknowledgment sender field ignored - Claimed:", acknowledgmentDto.User.UID, "Actual:", UserUID));
            LogAcknowledgmentStat(true, "unknown", "sender_field_ignored", normalizedHash, normalizedSessionId, acknowledgmentDto.Success, acknowledgmentDto.User.UID);
        }

        var acknowledgedAt = acknowledgmentDto.AcknowledgedAt == default ? DateTime.UtcNow : acknowledgmentDto.AcknowledgedAt;

        // Handle new session-based acknowledgments
        if (!string.IsNullOrEmpty(normalizedSessionId))
        {
            if (_batchAcknowledgmentTracker.TryAcknowledge(normalizedSessionId, UserUID, out var session))
            {
                var forwardedHash = session.DataHash;
                if (!string.Equals(normalizedHash, forwardedHash, StringComparison.Ordinal))
                {
                    _logger.LogCallWarning(SpheneHubLogger.Args("Session hash mismatch - User:", UserUID, "SessionId:", ShortLogToken(normalizedSessionId), "Provided:", ShortLogToken(normalizedHash), "Expected:", ShortLogToken(forwardedHash)));
                    LogAcknowledgmentStat(false, "session", "hash_mismatch", normalizedHash, normalizedSessionId, acknowledgmentDto.Success, forwardedHash);
                }
                
                // Create acknowledgment DTO with session information
                var forwardedAcknowledgment = new CharacterDataAcknowledgmentDto(new UserData(UserUID), forwardedHash)
                {
                    Success = acknowledgmentDto.Success,
                    ErrorCode = acknowledgmentDto.ErrorCode,
                    ErrorMessage = acknowledgmentDto.ErrorMessage,
                    AcknowledgedAt = acknowledgedAt,
                    SessionId = normalizedSessionId
                };
                
                var forwardedViaV2 = await SendAcknowledgmentToSenderAsync(session.SenderUid, forwardedAcknowledgment).ConfigureAwait(false);
                LogAcknowledgmentStat(true, forwardedViaV2 ? "session_v2" : "session", "forwarded", forwardedHash, normalizedSessionId, acknowledgmentDto.Success, session.SenderUid);
                
                // Log individual acknowledgment
                _logger.LogCallInfo(SpheneHubLogger.Args("SessionAck - User:", UserUID, "SessionId:", ShortLogToken(normalizedSessionId), "Remaining:", session.PendingRecipients.Count));
                
                // Check if all recipients have acknowledged
                if (_batchAcknowledgmentTracker.IsSessionCompleted(normalizedSessionId))
                {
                    _batchAcknowledgmentTracker.CompleteSession(normalizedSessionId);
                    RememberSessionAcknowledgmentCompletion(session.SenderUid, forwardedHash);
                    _logger.LogCallInfo(SpheneHubLogger.Args("SessionComplete - SessionId:", ShortLogToken(normalizedSessionId)));
                    LogAcknowledgmentStat(true, "session", "completed", forwardedHash, normalizedSessionId, acknowledgmentDto.Success, null);
                }
            }
            else
            {
                _logger.LogCallWarning(SpheneHubLogger.Args("Invalid session acknowledgment - User:", UserUID, "SessionId:", ShortLogToken(normalizedSessionId)));
                LogAcknowledgmentStat(false, "session", "invalid_session", normalizedHash, normalizedSessionId, acknowledgmentDto.Success, null);
            }
        }
        else if (_batchAcknowledgmentTracker.TryAcknowledgeByHash(normalizedHash, UserUID, out var sessionByHash))
        {
            var forwardedHash = sessionByHash.DataHash;

            var forwardedAcknowledgment = new CharacterDataAcknowledgmentDto(new UserData(UserUID), forwardedHash)
            {
                Success = acknowledgmentDto.Success,
                ErrorCode = acknowledgmentDto.ErrorCode,
                ErrorMessage = acknowledgmentDto.ErrorMessage,
                AcknowledgedAt = acknowledgedAt,
                SessionId = sessionByHash.SessionId
            };

            var forwardedViaV2 = await SendAcknowledgmentToSenderAsync(sessionByHash.SenderUid, forwardedAcknowledgment).ConfigureAwait(false);
            LogAcknowledgmentStat(true, forwardedViaV2 ? "session_by_hash_v2" : "session_by_hash", "forwarded", forwardedHash, sessionByHash.SessionId, acknowledgmentDto.Success, sessionByHash.SenderUid);

            if (_batchAcknowledgmentTracker.IsSessionCompleted(sessionByHash.SessionId))
            {
                _batchAcknowledgmentTracker.CompleteSession(sessionByHash.SessionId);
                RememberSessionAcknowledgmentCompletion(sessionByHash.SenderUid, forwardedHash);
                _logger.LogCallInfo(SpheneHubLogger.Args("SessionComplete - SessionId:", ShortLogToken(sessionByHash.SessionId)));
                LogAcknowledgmentStat(true, "session_by_hash", "completed", forwardedHash, sessionByHash.SessionId, acknowledgmentDto.Success, null);
            }
        }
        // Handle legacy hash-based acknowledgments for backward compatibility
        else if (_acknowledgmentSenders.TryGetValue(normalizedHash, out var legacySender))
        {
            var originalSenderUid = legacySender.SenderUid;
            
            // Validate that the acknowledging user has a pair relationship with the original sender BEFORE removing
            var pairExists = await DbContext.ClientPairs.AsNoTracking()
                .AnyAsync(p => (p.UserUID == UserUID && p.OtherUserUID == originalSenderUid) ||
                              (p.UserUID == originalSenderUid && p.OtherUserUID == UserUID))
                .ConfigureAwait(false);
            
            if (!pairExists)
            {
                _logger.LogCallWarning(SpheneHubLogger.Args("No pair relationship - User:", UserUID, "Hash:", ShortLogToken(normalizedHash), "Sender:", originalSenderUid));
                LogAcknowledgmentStat(false, "legacy", "pair_missing", normalizedHash, string.Empty, acknowledgmentDto.Success, originalSenderUid);
                return;
            }
            
            // Clean up the acknowledgment mapping AFTER validation to prevent TOCTOU race
            var removed = _acknowledgmentSenders.TryRemove(normalizedHash, out _);
            
            if (!removed)
            {
                _logger.LogCallWarning(SpheneHubLogger.Args("Acknowledgment already processed - User:", UserUID, "Hash:", ShortLogToken(normalizedHash)));
                LogAcknowledgmentStat(false, "legacy", "already_processed", normalizedHash, string.Empty, acknowledgmentDto.Success, legacySender.SenderUid);
                return;
            }

            if (WasRecentlyCompletedAsSessionAcknowledgment(originalSenderUid, normalizedHash))
            {
                LogAcknowledgmentStat(true, "legacy", "suppressed_duplicate_after_session", normalizedHash, string.Empty, acknowledgmentDto.Success, originalSenderUid);
                return;
            }
            
            // Create a new acknowledgment DTO with the current user (recipient) as the acknowledging user
            var forwardedAcknowledgment = new CharacterDataAcknowledgmentDto(new UserData(UserUID), normalizedHash)
            {
                Success = acknowledgmentDto.Success,
                ErrorCode = acknowledgmentDto.ErrorCode,
                ErrorMessage = acknowledgmentDto.ErrorMessage,
                AcknowledgedAt = acknowledgedAt
            };
            
            var forwardedViaV2 = await SendAcknowledgmentToSenderAsync(originalSenderUid, forwardedAcknowledgment).ConfigureAwait(false);
            LogAcknowledgmentStat(true, forwardedViaV2 ? "legacy_v2" : "legacy", "forwarded", normalizedHash, string.Empty, acknowledgmentDto.Success, originalSenderUid);
            
            _logger.LogCallInfo(SpheneHubLogger.Args("LegacyAck - User:", UserUID, "Hash:", ShortLogToken(normalizedHash)));
        }
        else
        {
            LogAcknowledgmentStat(false, "legacy", "sender_not_found", normalizedHash, string.Empty, acknowledgmentDto.Success, null);
        }
        
        _SpheneMetrics.IncCounter(MetricsAPI.CounterUserPushData);
    }

    private static string ShortLogToken(string value, int length = 8)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "<empty>";
        }

        return value.Length <= length ? value : value[..length];
    }

    private void LogAcknowledgmentStat(bool success, string mode, string outcome, string hash, string normalizedSessionId, bool? ackSuccess, string? detail)
    {
        var args = SpheneHubLogger.Args(
            "AckStat",
            "Success:", success,
            "Mode:", mode,
            "Outcome:", outcome,
            "User:", UserUID,
            "Hash:", ShortLogToken(hash),
            "SessionId:", ShortLogToken(normalizedSessionId),
            "AckSuccess:", ackSuccess,
            "Detail:", detail ?? string.Empty);

        if (success)
        {
            _logger.LogCallInfo(args);
            return;
        }

        _logger.LogCallWarning(args);
    }

    private async Task<bool> SendAcknowledgmentToSenderAsync(string senderUid, CharacterDataAcknowledgmentDto acknowledgment)
    {
        try
        {
            await Clients.User(senderUid).Client_UserReceiveCharacterDataAcknowledgmentV2(new CharacterDataAcknowledgmentEventDto(acknowledgment)).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogCallWarning(SpheneHubLogger.Args("AckV2 forward failed, falling back to V1", "Sender:", senderUid, "Hash:", ShortLogToken(acknowledgment.DataHash), "Error:", ex.Message));
            await Clients.User(senderUid).Client_UserReceiveCharacterDataAcknowledgment(acknowledgment).ConfigureAwait(false);
            return false;
        }
    }

    private static void RememberSessionAcknowledgmentCompletion(string senderUid, string hash)
    {
        PruneOldSessionAcknowledgmentCompletions();
        _recentSessionAcknowledgments[GetSessionCompletionKey(senderUid, hash)] = DateTime.UtcNow;
    }

    private static bool WasRecentlyCompletedAsSessionAcknowledgment(string senderUid, string hash)
    {
        PruneOldSessionAcknowledgmentCompletions();
        return _recentSessionAcknowledgments.ContainsKey(GetSessionCompletionKey(senderUid, hash));
    }

    private static void PruneOldSessionAcknowledgmentCompletions()
    {
        var cutoff = DateTime.UtcNow.AddSeconds(-30);
        foreach (var kvp in _recentSessionAcknowledgments)
        {
            if (kvp.Value < cutoff)
            {
                _recentSessionAcknowledgments.TryRemove(kvp.Key, out _);
            }
        }
    }

    private static string GetSessionCompletionKey(string senderUid, string hash)
    {
        return senderUid + "|" + hash;
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

        // Delegate state management to MutualVisibilityTracker
        var now = DateTime.UtcNow;
        var result = _mutualVisibilityTracker.ProcessReport(dto.Reporter.UID, dto.Target.UID, dto.IsVisible, now);

        if (result.Dto is not null)
        {
            // Broadcast to both users if they are online
            foreach (var recipientUid in result.RecipientUids)
            {
                var ident = await GetUserIdent(recipientUid).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(ident))
                {
                    await Clients.User(recipientUid).Client_UserMutualVisibilityUpdate(result.Dto).ConfigureAwait(false);
                }
            }

            _logger.LogCallInfo(SpheneHubLogger.Args("Mutual visibility updated", dto.Reporter.UID, dto.Target.UID, result.Dto.IsMutuallyVisible));
        }
        else
        {
            _logger.LogCallInfo(SpheneHubLogger.Args("Visibility report processed", dto.Reporter.UID, dto.Target.UID));
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

        // check if other user is online, if no then there is no need to do anything further
        var otherIdent = await GetUserIdent(dto.User.UID).ConfigureAwait(false);
        if (otherIdent == null) return;

        if (!pairData.IndividuallyPaired)
        {
            await Clients.User(dto.User.UID).Client_UserRemoveClientPair(new(new(UserUID))).ConfigureAwait(false);
            return;
        }

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

        if (!string.IsNullOrEmpty(dto.BannerImageBase64))
        {
            byte[] bannerData = Convert.FromBase64String(dto.BannerImageBase64);
            using MemoryStream ms = new(bannerData);
            var format = await Image.DetectFormatAsync(ms).ConfigureAwait(false);
            if (!format.FileExtensions.Contains("png", StringComparer.OrdinalIgnoreCase))
            {
                await Clients.Caller.Client_ReceiveServerMessage(MessageSeverity.Error, "Your provided banner file is not in PNG format").ConfigureAwait(false);
                return;
            }
            using var banner = Image.Load<Rgba32>(bannerData);

            if (banner.Width > 1200 || banner.Height > 400 || (bannerData.Length > 400 * 1024))
            {
                await Clients.Caller.Client_ReceiveServerMessage(MessageSeverity.Error, "Your provided banner image is larger than 1200x400 or more than 400KiB.").ConfigureAwait(false);
                return;
            }
        }

        if (!string.IsNullOrEmpty(dto.Theme) && dto.Theme.Length > 2048)
        {
            await Clients.Caller.Client_ReceiveServerMessage(MessageSeverity.Error, "Your profile theme data is too large.").ConfigureAwait(false);
            return;
        }

        // resolved once - gates the row-image upload and decides whether
        // supporter-only theme fields survive the write below
        var effectiveSupporter = true;
        if (!string.IsNullOrEmpty(dto.RowImageBase64) || dto.Theme != null)
        {
            var supporter = await DbContext.Users.Where(u => u.UID == UserUID)
                .Select(u => new { u.IsSupporter, u.SupporterTrialStartUtc })
                .SingleOrDefaultAsync().ConfigureAwait(false);
            effectiveSupporter = supporter != null
                && Utils.Extensions.IsSupporterEffective(supporter.IsSupporter, supporter.SupporterTrialStartUtc);
        }

        if (!string.IsNullOrEmpty(dto.RowImageBase64))
        {
            if (!effectiveSupporter)
            {
                await Clients.Caller.Client_ReceiveServerMessage(MessageSeverity.Error, "Animated row backgrounds are a supporter feature.").ConfigureAwait(false);
                return;
            }
            byte[] rowData = Convert.FromBase64String(dto.RowImageBase64);
            using MemoryStream ms = new(rowData);
            var format = await Image.DetectFormatAsync(ms).ConfigureAwait(false);
            if (!format.FileExtensions.Contains("png", StringComparer.OrdinalIgnoreCase)
                && !format.FileExtensions.Contains("gif", StringComparer.OrdinalIgnoreCase))
            {
                await Clients.Caller.Client_ReceiveServerMessage(MessageSeverity.Error, "Your row image must be a PNG or GIF file").ConfigureAwait(false);
                return;
            }
            using var rowImage = Image.Load<Rgba32>(rowData);
            if (rowImage.Width > 512 || rowImage.Height > 128 || (rowData.Length > 400 * 1024))
            {
                await Clients.Caller.Client_ReceiveServerMessage(MessageSeverity.Error, "Your row image is larger than 512x128 or more than 400KiB.").ConfigureAwait(false);
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

            if (dto.StatusMessage != null)
            {
                existingData.StatusMessage = dto.StatusMessage;
            }

            if (dto.Tags != null)
            {
                existingData.ProfileTags = dto.Tags;
            }

            if (string.Equals("", dto.BannerImageBase64, StringComparison.OrdinalIgnoreCase))
            {
                existingData.Base64BannerImage = null;
            }
            else if (dto.BannerImageBase64 != null)
            {
                existingData.Base64BannerImage = dto.BannerImageBase64;
            }

            if (dto.Theme != null)
            {
                existingData.ProfileTheme = Utils.Extensions
                    .NormalizeThemeForSupporter(dto.Theme, effectiveSupporter);
            }

            if (dto.Visibility != null)
            {
                existingData.ProfileVisibility = dto.Visibility.Value & 0xFF;
            }

            if (dto.Links != null)
            {
                existingData.LinksJson = SerializeJson(dto.Links);
            }

            if (string.Equals("", dto.RowImageBase64, StringComparison.OrdinalIgnoreCase))
            {
                existingData.Base64RowImage = null;
            }
            else if (dto.RowImageBase64 != null)
            {
                existingData.Base64RowImage = dto.RowImageBase64;
            }
        }
        else
        {
            UserProfileData userProfileData = new()
            {
                UserUID = dto.User.UID,
                Base64ProfileImage = dto.ProfilePictureBase64 ?? null,
                UserDescription = dto.Description ?? null,
                StatusMessage = dto.StatusMessage ?? null,
                ProfileTags = dto.Tags ?? null,
                Base64BannerImage = dto.BannerImageBase64 ?? null,
                Base64RowImage = dto.RowImageBase64 ?? null,
                ProfileTheme = Utils.Extensions
                    .NormalizeThemeForSupporter(dto.Theme, effectiveSupporter),
                ProfileVisibility = (dto.Visibility ?? 0) & 0x1F,
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
            if (allPairInfo.TryGetValue(pair.Key, out var pairData) && pairData?.OwnPermissions != null)
            {
                notificationTasks.Add(Clients.User(pair.Key).Client_UserUpdateOtherPairPermissions(
                    new UserPermissionsDto(user.ToUserData(), pairData.OwnPermissions.ToUserPermissions())));
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

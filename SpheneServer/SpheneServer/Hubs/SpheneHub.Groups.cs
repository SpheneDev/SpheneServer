using Sphene.API.Data.Enum;
using Sphene.API.Data.Extensions;
using Sphene.API.Dto.CharaData;
using Sphene.API.Dto.Group;
using SpheneServer.Utils;
using SpheneShared.Models;
using SpheneShared.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;

namespace SpheneServer.Hubs;

public partial class SpheneHub
{
    [Authorize(Policy = "Identified")]
    public async Task GroupBanUser(GroupPairDto dto, string reason)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto, reason));

        var (userHasRights, group) = await TryValidateGroupModeratorOrOwner(dto.Group.GID).ConfigureAwait(false);
        if (!userHasRights) return;

        var (userExists, groupPair) = await TryValidateUserInGroup(dto.Group.GID, dto.User.UID).ConfigureAwait(false);
        if (!userExists) return;

        if (groupPair.IsModerator || string.Equals(group.OwnerUID, dto.User.UID, StringComparison.Ordinal)) return;

        var alias = string.IsNullOrEmpty(groupPair.GroupUser.Alias) ? "-" : groupPair.GroupUser.Alias;
        var ban = new GroupBan()
        {
            BannedByUID = UserUID,
            BannedReason = $"{reason} (Alias at time of ban: {alias})",
            BannedOn = DateTime.UtcNow,
            BannedUserUID = dto.User.UID,
            GroupGID = dto.Group.GID,
        };

        DbContext.Add(ban);
        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        await GroupRemoveUser(dto).ConfigureAwait(false);

        _logger.LogCallInfo(SpheneHubLogger.Args(dto, "Success"));
    }

    [Authorize(Policy = "Identified")]
    public async Task<bool> GroupSetAreaBoundConsent(AreaBoundJoinConsentRequestDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));

        // Validate that the area-bound syncshell exists
        var binding = await DbContext.AreaBoundSyncshells
            .SingleOrDefaultAsync(a => a.GroupGID == dto.SyncshellGID)
            .ConfigureAwait(false);

        if (binding == null) return false;

        // Find or create consent record
        var consent = await DbContext.AreaBoundSyncshellConsents
            .SingleOrDefaultAsync(c => c.UserUID == UserUID && c.SyncshellGID == dto.SyncshellGID)
            .ConfigureAwait(false);

        if (consent == null)
        {
            consent = new AreaBoundSyncshellConsent
            {
                UserUID = UserUID,
                SyncshellGID = dto.SyncshellGID,
                HasAccepted = dto.AcceptJoin,
                ConsentGivenAt = DateTime.UtcNow,
                AcceptedRulesVersion = dto.AcceptRules ? dto.RulesVersion : 0
            };

            if (dto.AcceptRules)
            {
                consent.LastRulesAcceptedAt = DateTime.UtcNow;
            }

            await DbContext.AreaBoundSyncshellConsents.AddAsync(consent).ConfigureAwait(false);
        }
        else
        {
            consent.HasAccepted = dto.AcceptJoin;
            consent.ConsentGivenAt = DateTime.UtcNow;

            if (dto.AcceptRules)
            {
                consent.AcceptedRulesVersion = dto.RulesVersion;
                consent.LastRulesAcceptedAt = DateTime.UtcNow;
            }
        }

        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        _logger.LogCallInfo(SpheneHubLogger.Args(dto, "Success"));
        return true;
    }

    [Authorize(Policy = "Identified")]
    public async Task GroupChangeGroupPermissionState(GroupPermissionDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));

        var (hasRights, group) = await TryValidateGroupModeratorOrOwner(dto.Group.GID).ConfigureAwait(false);
        if (!hasRights) return;

        group.InvitesEnabled = !dto.Permissions.HasFlag(GroupPermissions.DisableInvites);
        group.PreferDisableSounds = dto.Permissions.HasFlag(GroupPermissions.PreferDisableSounds);
        group.PreferDisableAnimations = dto.Permissions.HasFlag(GroupPermissions.PreferDisableAnimations);
        group.PreferDisableVFX = dto.Permissions.HasFlag(GroupPermissions.PreferDisableVFX);

        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        var groupPairs = DbContext.GroupPairs.Where(p => p.GroupGID == dto.Group.GID).Select(p => p.GroupUserUID).ToList();
        await Clients.Users(groupPairs).Client_GroupChangePermissions(new GroupPermissionDto(dto.Group, dto.Permissions)).ConfigureAwait(false);
    }

    [Authorize(Policy = "Identified")]
    public async Task GroupChangeOwnership(GroupPairDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));

        var (isOwner, group) = await TryValidateOwner(dto.Group.GID).ConfigureAwait(false);
        if (!isOwner) return;

        var (isInGroup, newOwnerPair) = await TryValidateUserInGroup(dto.Group.GID, dto.User.UID).ConfigureAwait(false);
        if (!isInGroup) return;

        var ownedShells = await DbContext.Groups.CountAsync(g => g.OwnerUID == dto.User.UID).ConfigureAwait(false);
        if (ownedShells >= _maxExistingGroupsByUser) return;

        var prevOwner = await DbContext.GroupPairs.SingleOrDefaultAsync(g => g.GroupGID == dto.Group.GID && g.GroupUserUID == UserUID).ConfigureAwait(false);
        prevOwner.IsPinned = false;
        group.Owner = newOwnerPair.GroupUser;
        group.Alias = null;
        newOwnerPair.IsPinned = true;
        newOwnerPair.IsModerator = false;
        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        _logger.LogCallInfo(SpheneHubLogger.Args(dto, "Success"));

        var groupPairs = await DbContext.GroupPairs.Where(p => p.GroupGID == dto.Group.GID).Select(p => p.GroupUserUID).AsNoTracking().ToListAsync().ConfigureAwait(false);

        await Clients.Users(groupPairs).Client_GroupSendInfo(new GroupInfoDto(group.ToGroupData(), newOwnerPair.GroupUser.ToUserData(), group.ToEnum())).ConfigureAwait(false);
    }

    [Authorize(Policy = "Identified")]
    public async Task<bool> GroupChangePassword(GroupPasswordDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));

        var (isOwner, group) = await TryValidateOwner(dto.Group.GID).ConfigureAwait(false);
        if (!isOwner || dto.Password.Length < 10) return false;

        _logger.LogCallInfo(SpheneHubLogger.Args(dto, "Success"));

        group.HashedPassword = StringUtils.Sha256String(dto.Password);
        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        return true;
    }

    [Authorize(Policy = "Identified")]
    public async Task<bool> GroupSetAlias(GroupAliasDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));

        var (isOwner, group) = await TryValidateOwner(dto.Group.GID).ConfigureAwait(false);
        if (!isOwner) return false;

        // Validate alias length and format if provided
        if (!string.IsNullOrWhiteSpace(dto.Alias))
        {
            var trimmedAlias = dto.Alias.Trim();
            if (trimmedAlias.Length < 3 || trimmedAlias.Length > 50) return false;
            
            // Check if alias is already taken by another group
            var existingGroup = await DbContext.Groups.AsNoTracking()
                .SingleOrDefaultAsync(g => g.Alias == trimmedAlias && g.GID != dto.Group.GID).ConfigureAwait(false);
            if (existingGroup != null) return false;
            
            group.Alias = trimmedAlias;
        }
        else
        {
            // Allow clearing the alias by setting it to null
            group.Alias = null;
        }

        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        // Notify all group members about the alias change
        var groupPairs = await DbContext.GroupPairs.Where(p => p.GroupGID == dto.Group.GID)
            .Select(p => p.GroupUserUID).AsNoTracking().ToListAsync().ConfigureAwait(false);
        
        var updatedGroupData = group.ToGroupData();
        await Clients.Users(groupPairs).Client_GroupSendInfo(new GroupInfoDto(updatedGroupData, group.Owner.ToUserData(), group.ToEnum())).ConfigureAwait(false);

        _logger.LogCallInfo(SpheneHubLogger.Args(dto, "Success"));
        return true;
    }

    [Authorize(Policy = "Identified")]
    public async Task GroupClear(GroupDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));

        var (hasRights, group) = await TryValidateGroupModeratorOrOwner(dto.Group.GID).ConfigureAwait(false);
        if (!hasRights) return;

        var groupPairs = await DbContext.GroupPairs.Include(p => p.GroupUser).Where(p => p.GroupGID == dto.Group.GID).ToListAsync().ConfigureAwait(false);
        var notPinned = groupPairs.Where(g => !g.IsPinned && !g.IsModerator).ToList();

        await Clients.Users(notPinned.Select(g => g.GroupUserUID)).Client_GroupDelete(new GroupDto(group.ToGroupData())).ConfigureAwait(false);

        _logger.LogCallInfo(SpheneHubLogger.Args(dto, "Success"));

        DbContext.GroupPairs.RemoveRange(notPinned);

        foreach (var pair in notPinned)
        {
            await Clients.Users(groupPairs.Where(p => p.IsPinned || p.IsModerator).Select(g => g.GroupUserUID))
                .Client_GroupPairLeft(new GroupPairDto(dto.Group, pair.GroupUser.ToUserData())).ConfigureAwait(false);

            var pairIdent = await GetUserIdent(pair.GroupUserUID).ConfigureAwait(false);
            if (string.IsNullOrEmpty(pairIdent)) continue;

            var allUserPairs = await GetAllPairInfo(pair.GroupUserUID).ConfigureAwait(false);

            var sharedData = await DbContext.CharaDataAllowances.Where(u => u.AllowedGroup != null && u.AllowedGroupGID == dto.GID && u.ParentUploaderUID == pair.GroupUserUID).ToListAsync().ConfigureAwait(false);
            DbContext.CharaDataAllowances.RemoveRange(sharedData);

            foreach (var groupUserPair in groupPairs.Where(p => !string.Equals(p.GroupUserUID, pair.GroupUserUID, StringComparison.Ordinal)))
            {
                await UserGroupLeave(pair, pairIdent, allUserPairs, pair.GroupUserUID).ConfigureAwait(false);
            }
        }

        await DbContext.SaveChangesAsync().ConfigureAwait(false);
    }

    [Authorize(Policy = "Identified")]
    public async Task<GroupJoinDto> GroupCreate(GroupCreateDto? dto = null)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));
        var existingGroupsByUser = await DbContext.Groups.CountAsync(u => u.OwnerUID == UserUID).ConfigureAwait(false);
        var existingJoinedGroups = await DbContext.GroupPairs.CountAsync(u => u.GroupUserUID == UserUID).ConfigureAwait(false);
        if (existingGroupsByUser >= _maxExistingGroupsByUser || existingJoinedGroups >= _maxJoinedGroupsByUser)
        {
            throw new System.Exception($"Max groups for user is {_maxExistingGroupsByUser}, max joined groups is {_maxJoinedGroupsByUser}.");
        }

        // Validate alias if provided
        string? alias = null;
        if (dto?.Alias != null)
        {
            var trimmedAlias = dto.Alias.Trim();
            if (trimmedAlias.Length >= 3 && trimmedAlias.Length <= 50)
            {
                // Check if alias is already taken
                var existingGroup = await DbContext.Groups.AsNoTracking()
                    .SingleOrDefaultAsync(g => g.Alias == trimmedAlias).ConfigureAwait(false);
                if (existingGroup == null)
                {
                    alias = trimmedAlias;
                }
            }
        }

        var gid = StringUtils.GenerateRandomString(12);
        while (await DbContext.Groups.AnyAsync(g => g.GID == "MSS-" + gid).ConfigureAwait(false))
        {
            gid = StringUtils.GenerateRandomString(12);
        }
        gid = "MSS-" + gid;

        var passwd = StringUtils.GenerateRandomString(16);
        using var sha = SHA256.Create();
        var hashedPw = StringUtils.Sha256String(passwd);

        UserDefaultPreferredPermission defaultPermissions = await DbContext.UserDefaultPreferredPermissions.SingleAsync(u => u.UserUID == UserUID).ConfigureAwait(false);

        Group newGroup = new()
        {
            GID = gid,
            HashedPassword = hashedPw,
            InvitesEnabled = true,
            OwnerUID = UserUID,
            Alias = alias,
            PreferDisableAnimations = defaultPermissions.DisableGroupAnimations,
            PreferDisableSounds = defaultPermissions.DisableGroupSounds,
            PreferDisableVFX = defaultPermissions.DisableGroupVFX
        };

        GroupPair initialPair = new()
        {
            GroupGID = newGroup.GID,
            GroupUserUID = UserUID,
            IsPinned = true,
        };

        GroupPairPreferredPermission initialPrefPermissions = new()
        {
            UserUID = UserUID,
            GroupGID = newGroup.GID,
            DisableSounds = defaultPermissions.DisableGroupSounds,
            DisableAnimations = defaultPermissions.DisableGroupAnimations,
            DisableVFX = defaultPermissions.DisableGroupAnimations
        };

        await DbContext.Groups.AddAsync(newGroup).ConfigureAwait(false);
        await DbContext.GroupPairs.AddAsync(initialPair).ConfigureAwait(false);
        await DbContext.GroupPairPreferredPermissions.AddAsync(initialPrefPermissions).ConfigureAwait(false);
        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        var self = await DbContext.Users.SingleAsync(u => u.UID == UserUID).ConfigureAwait(false);

        await Clients.User(UserUID).Client_GroupSendFullInfo(new GroupFullInfoDto(newGroup.ToGroupData(), self.ToUserData(),
            newGroup.ToEnum(), initialPrefPermissions.ToEnum(), initialPair.ToEnum(), new(StringComparer.Ordinal)))
            .ConfigureAwait(false);

        _logger.LogCallInfo(SpheneHubLogger.Args(gid));

        return new GroupJoinDto(newGroup.ToGroupData(), passwd, initialPrefPermissions.ToEnum());
    }

    [Authorize(Policy = "Identified")]
    public async Task<List<string>> GroupCreateTempInvite(GroupDto dto, int amount)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto, amount));
        List<string> inviteCodes = new();
        List<GroupTempInvite> tempInvites = new();
        var (hasRights, group) = await TryValidateGroupModeratorOrOwner(dto.Group.GID).ConfigureAwait(false);
        if (!hasRights) return new();

        var existingInvites = await DbContext.GroupTempInvites.Where(g => g.GroupGID == group.GID).ToListAsync().ConfigureAwait(false);

        for (int i = 0; i < amount; i++)
        {
            bool hasValidInvite = false;
            string invite = string.Empty;
            string hashedInvite = string.Empty;
            while (!hasValidInvite)
            {
                invite = StringUtils.GenerateRandomString(10, "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789");
                hashedInvite = StringUtils.Sha256String(invite);
                if (existingInvites.Any(i => string.Equals(i.Invite, hashedInvite, StringComparison.Ordinal))) continue;
                hasValidInvite = true;
                inviteCodes.Add(invite);
            }

            tempInvites.Add(new GroupTempInvite()
            {
                ExpirationDate = DateTime.UtcNow.AddDays(1),
                GroupGID = group.GID,
                Invite = hashedInvite,
            });
        }

        DbContext.GroupTempInvites.AddRange(tempInvites);
        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        return inviteCodes;
    }

    [Authorize(Policy = "Identified")]
    public async Task GroupDelete(GroupDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));

        var (hasRights, group) = await TryValidateOwner(dto.Group.GID).ConfigureAwait(false);

        _logger.LogCallInfo(SpheneHubLogger.Args(dto, "Success"));

        var groupPairs = await DbContext.GroupPairs.Where(p => p.GroupGID == dto.Group.GID).ToListAsync().ConfigureAwait(false);
        DbContext.RemoveRange(groupPairs);
        DbContext.Remove(group);
        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        await Clients.Users(groupPairs.Select(g => g.GroupUserUID)).Client_GroupDelete(new GroupDto(group.ToGroupData())).ConfigureAwait(false);

        await SendGroupDeletedToAll(groupPairs).ConfigureAwait(false);
    }

    [Authorize(Policy = "Identified")]
    public async Task<List<BannedGroupUserDto>> GroupGetBannedUsers(GroupDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));

        var (userHasRights, group) = await TryValidateGroupModeratorOrOwner(dto.GID).ConfigureAwait(false);
        if (!userHasRights) return new List<BannedGroupUserDto>();

        var banEntries = await DbContext.GroupBans.Include(b => b.BannedUser).Where(g => g.GroupGID == dto.Group.GID).AsNoTracking().ToListAsync().ConfigureAwait(false);

        List<BannedGroupUserDto> bannedGroupUsers = banEntries.Select(b =>
            new BannedGroupUserDto(group.ToGroupData(), b.BannedUser.ToUserData(), b.BannedReason, b.BannedOn,
                b.BannedByUID)).ToList();

        _logger.LogCallInfo(SpheneHubLogger.Args(dto, bannedGroupUsers.Count));

        return bannedGroupUsers;
    }

    [Authorize(Policy = "Identified")]
    public async Task<GroupJoinInfoDto> GroupJoin(GroupPasswordDto dto)
    {
        var aliasOrGid = dto.Group.GID.Trim();

        _logger.LogCallInfo(SpheneHubLogger.Args(dto));

        var group = await DbContext.Groups.Include(g => g.Owner).AsNoTracking().SingleOrDefaultAsync(g => g.GID == aliasOrGid || g.Alias == aliasOrGid).ConfigureAwait(false);
        var groupGid = group?.GID ?? string.Empty;
        var existingPair = await DbContext.GroupPairs.AsNoTracking().SingleOrDefaultAsync(g => g.GroupGID == groupGid && g.GroupUserUID == UserUID).ConfigureAwait(false);
        var hashedPw = StringUtils.Sha256String(dto.Password);
        var existingUserCount = await DbContext.GroupPairs.AsNoTracking().CountAsync(g => g.GroupGID == groupGid).ConfigureAwait(false);
        var joinedGroups = await DbContext.GroupPairs.CountAsync(g => g.GroupUserUID == UserUID).ConfigureAwait(false);
        var isBanned = await DbContext.GroupBans.AnyAsync(g => g.GroupGID == groupGid && g.BannedUserUID == UserUID).ConfigureAwait(false);
        var oneTimeInvite = await DbContext.GroupTempInvites.SingleOrDefaultAsync(g => g.GroupGID == groupGid && g.Invite == hashedPw).ConfigureAwait(false);

        if (group == null
            || (!string.Equals(group.HashedPassword, hashedPw, StringComparison.Ordinal) && oneTimeInvite == null)
            || existingPair != null
            || existingUserCount >= _maxGroupUserCount
            || !group.InvitesEnabled
            || joinedGroups >= _maxJoinedGroupsByUser
            || isBanned)
            return new GroupJoinInfoDto(null, null, GroupPermissions.NoneSet, false);

        return new GroupJoinInfoDto(group.ToGroupData(), group.Owner.ToUserData(), group.ToEnum(), true);
    }

    [Authorize(Policy = "Identified")]
    public async Task<bool> GroupJoinFinalize(GroupJoinDto dto)
    {
        var aliasOrGid = dto.Group.GID.Trim();

        _logger.LogCallInfo(SpheneHubLogger.Args(dto));

        var group = await DbContext.Groups.Include(g => g.Owner).AsNoTracking().SingleOrDefaultAsync(g => g.GID == aliasOrGid || g.Alias == aliasOrGid).ConfigureAwait(false);
        var groupGid = group?.GID ?? string.Empty;
        var existingPair = await DbContext.GroupPairs.AsNoTracking().SingleOrDefaultAsync(g => g.GroupGID == groupGid && g.GroupUserUID == UserUID).ConfigureAwait(false);
        var hashedPw = StringUtils.Sha256String(dto.Password);
        var existingUserCount = await DbContext.GroupPairs.AsNoTracking().CountAsync(g => g.GroupGID == groupGid).ConfigureAwait(false);
        var joinedGroups = await DbContext.GroupPairs.CountAsync(g => g.GroupUserUID == UserUID).ConfigureAwait(false);
        var isBanned = await DbContext.GroupBans.AnyAsync(g => g.GroupGID == groupGid && g.BannedUserUID == UserUID).ConfigureAwait(false);
        var oneTimeInvite = await DbContext.GroupTempInvites.SingleOrDefaultAsync(g => g.GroupGID == groupGid && g.Invite == hashedPw).ConfigureAwait(false);

        if (group == null
            || (!string.Equals(group.HashedPassword, hashedPw, StringComparison.Ordinal) && oneTimeInvite == null)
            || existingPair != null
            || existingUserCount >= _maxGroupUserCount
            || !group.InvitesEnabled
            || joinedGroups >= _maxJoinedGroupsByUser
            || isBanned)
            return false;

        // get all pairs before we join
        var allUserPairs = (await GetAllPairInfo(UserUID).ConfigureAwait(false));

        if (oneTimeInvite != null)
        {
            _logger.LogCallInfo(SpheneHubLogger.Args(aliasOrGid, "TempInvite", oneTimeInvite.Invite));
            DbContext.Remove(oneTimeInvite);
        }

        GroupPair newPair = new()
        {
            GroupGID = group.GID,
            GroupUserUID = UserUID,
        };

        var preferredPermissions = await DbContext.GroupPairPreferredPermissions.SingleOrDefaultAsync(u => u.UserUID == UserUID && u.GroupGID == group.GID).ConfigureAwait(false);
        if (preferredPermissions == null)
        {
            GroupPairPreferredPermission newPerms = new()
            {
                GroupGID = group.GID,
                UserUID = UserUID,
                DisableSounds = dto.GroupUserPreferredPermissions.IsDisableSounds(),
                DisableVFX = dto.GroupUserPreferredPermissions.IsDisableVFX(),
                DisableAnimations = dto.GroupUserPreferredPermissions.IsDisableAnimations(),
                IsPaused = false
            };

            DbContext.Add(newPerms);
            preferredPermissions = newPerms;
        }
        else
        {
            preferredPermissions.DisableSounds = dto.GroupUserPreferredPermissions.IsDisableSounds();
            preferredPermissions.DisableVFX = dto.GroupUserPreferredPermissions.IsDisableVFX();
            preferredPermissions.DisableAnimations = dto.GroupUserPreferredPermissions.IsDisableAnimations();
            preferredPermissions.IsPaused = false;
            DbContext.Update(preferredPermissions);
        }

        await DbContext.GroupPairs.AddAsync(newPair).ConfigureAwait(false);

        _logger.LogCallInfo(SpheneHubLogger.Args(aliasOrGid, "Success"));

        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        var groupInfos = await DbContext.GroupPairs.Where(u => u.GroupGID == group.GID && (u.IsPinned || u.IsModerator)).ToListAsync().ConfigureAwait(false);
        await Clients.User(UserUID).Client_GroupSendFullInfo(new GroupFullInfoDto(group.ToGroupData(), group.Owner.ToUserData(),
            group.ToEnum(), preferredPermissions.ToEnum(), newPair.ToEnum(),
            groupInfos.ToDictionary(u => u.GroupUserUID, u => u.ToEnum(), StringComparer.Ordinal))).ConfigureAwait(false);

        var self = DbContext.Users.Single(u => u.UID == UserUID);

        var groupPairs = await DbContext.GroupPairs.Include(p => p.GroupUser)
            .Where(p => p.GroupGID == group.GID && p.GroupUserUID != UserUID).ToListAsync().ConfigureAwait(false);

        var userPairsAfterJoin = await GetAllPairInfo(UserUID).ConfigureAwait(false);

        foreach (var pair in groupPairs)
        {
            var perms = userPairsAfterJoin.TryGetValue(pair.GroupUserUID, out var userinfo);
            // check if we have had prior permissions to that pair, if not add them
            var ownPermissionsToOther = userinfo?.OwnPermissions ?? null;
            if (ownPermissionsToOther == null)
            {
                var existingPermissionsOnDb = await DbContext.Permissions.SingleOrDefaultAsync(p => p.UserUID == UserUID && p.OtherUserUID == pair.GroupUserUID).ConfigureAwait(false);

                if (existingPermissionsOnDb == null)
                {
                    ownPermissionsToOther = new()
                    {
                        UserUID = UserUID,
                        OtherUserUID = pair.GroupUserUID,
                        DisableAnimations = preferredPermissions.DisableAnimations,
                        DisableSounds = preferredPermissions.DisableSounds,
                        DisableVFX = preferredPermissions.DisableVFX,
                        IsPaused = preferredPermissions.IsPaused,
                        Sticky = false
                    };

                    await DbContext.Permissions.AddAsync(ownPermissionsToOther).ConfigureAwait(false);
                }
                else
                {
                    existingPermissionsOnDb.DisableAnimations = preferredPermissions.DisableAnimations;
                    existingPermissionsOnDb.DisableSounds = preferredPermissions.DisableSounds;
                    existingPermissionsOnDb.DisableVFX = preferredPermissions.DisableVFX;
                    existingPermissionsOnDb.IsPaused = false;
                    existingPermissionsOnDb.Sticky = false;

                    DbContext.Update(existingPermissionsOnDb);

                    ownPermissionsToOther = existingPermissionsOnDb;
                }
            }
            else if (!ownPermissionsToOther.Sticky)
            {
                ownPermissionsToOther = await DbContext.Permissions.SingleAsync(u => u.UserUID == UserUID && u.OtherUserUID == pair.GroupUserUID).ConfigureAwait(false);

                // update the existing permission only if it was not set to sticky
                ownPermissionsToOther.DisableAnimations = preferredPermissions.DisableAnimations;
                ownPermissionsToOther.DisableVFX = preferredPermissions.DisableVFX;
                ownPermissionsToOther.DisableSounds = preferredPermissions.DisableSounds;
                ownPermissionsToOther.IsPaused = false;

                DbContext.Update(ownPermissionsToOther);
            }

            // get others permissionset to self and eventually update it
            var otherPermissionToSelf = userinfo?.OtherPermissions ?? null;
            if (otherPermissionToSelf == null)
            {
                var otherExistingPermsOnDb = await DbContext.Permissions.SingleOrDefaultAsync(p => p.UserUID == pair.GroupUserUID && p.OtherUserUID == UserUID).ConfigureAwait(false);

                if (otherExistingPermsOnDb == null)
                {
                    var otherPreferred = await DbContext.GroupPairPreferredPermissions.SingleAsync(u => u.GroupGID == group.GID && u.UserUID == pair.GroupUserUID).ConfigureAwait(false);
                    otherExistingPermsOnDb = new()
                    {
                        UserUID = pair.GroupUserUID,
                        OtherUserUID = UserUID,
                        DisableAnimations = otherPreferred.DisableAnimations,
                        DisableSounds = otherPreferred.DisableSounds,
                        DisableVFX = otherPreferred.DisableVFX,
                        IsPaused = otherPreferred.IsPaused,
                        Sticky = false
                    };

                    await DbContext.AddAsync(otherExistingPermsOnDb).ConfigureAwait(false);
                }
                else if (!otherExistingPermsOnDb.Sticky)
                {
                    var otherPreferred = await DbContext.GroupPairPreferredPermissions.SingleAsync(u => u.GroupGID == group.GID && u.UserUID == pair.GroupUserUID).ConfigureAwait(false);
                    otherExistingPermsOnDb.DisableAnimations = otherPreferred.DisableAnimations;
                    otherExistingPermsOnDb.DisableSounds = otherPreferred.DisableSounds;
                    otherExistingPermsOnDb.DisableVFX = otherPreferred.DisableVFX;
                    otherExistingPermsOnDb.IsPaused = otherPreferred.IsPaused;

                    DbContext.Update(otherExistingPermsOnDb);
                }

                otherPermissionToSelf = otherExistingPermsOnDb;
            }
            else if (!otherPermissionToSelf.Sticky)
            {
                var otherPreferred = await DbContext.GroupPairPreferredPermissions.SingleAsync(u => u.GroupGID == group.GID && u.UserUID == pair.GroupUserUID).ConfigureAwait(false);
                otherPermissionToSelf.DisableAnimations = otherPreferred.DisableAnimations;
                otherPermissionToSelf.DisableSounds = otherPreferred.DisableSounds;
                otherPermissionToSelf.DisableVFX = otherPreferred.DisableVFX;
                otherPermissionToSelf.IsPaused = otherPreferred.IsPaused;

                DbContext.Update(otherPermissionToSelf);
            }

            await Clients.User(UserUID).Client_GroupPairJoined(new GroupPairFullInfoDto(group.ToGroupData(),
                pair.ToUserData(), ownPermissionsToOther.ToUserPermissions(setSticky: ownPermissionsToOther.Sticky),
                otherPermissionToSelf.ToUserPermissions(setSticky: false))).ConfigureAwait(false);
            await Clients.User(pair.GroupUserUID).Client_GroupPairJoined(new GroupPairFullInfoDto(group.ToGroupData(),
                self.ToUserData(), otherPermissionToSelf.ToUserPermissions(setSticky: otherPermissionToSelf.Sticky),
                ownPermissionsToOther.ToUserPermissions(setSticky: false))).ConfigureAwait(false);

            // if not paired prior and neither has the permissions set to paused, send online
            if ((!allUserPairs.ContainsKey(pair.GroupUserUID) || (allUserPairs.TryGetValue(pair.GroupUserUID, out var info) && !info.IsSynced))
                && !otherPermissionToSelf.IsPaused && !ownPermissionsToOther.IsPaused)
            {
                var groupUserIdent = await GetUserIdent(pair.GroupUserUID).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(groupUserIdent))
                {
                    await Clients.User(UserUID).Client_UserSendOnline(new(pair.ToUserData(), groupUserIdent)).ConfigureAwait(false);
                    await Clients.User(pair.GroupUserUID).Client_UserSendOnline(new(self.ToUserData(), UserCharaIdent)).ConfigureAwait(false);
                }
            }
        }

        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        return true;
    }

    [Authorize(Policy = "Identified")]
    public async Task GroupLeave(GroupDto dto)
    {
        await UserLeaveGroup(dto, UserUID).ConfigureAwait(false);
    }

    [Authorize(Policy = "Identified")]
    public async Task<int> GroupPrune(GroupDto dto, int days, bool execute)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto, days, execute));

        var (hasRights, group) = await TryValidateGroupModeratorOrOwner(dto.Group.GID).ConfigureAwait(false);
        if (!hasRights) return -1;

        var allGroupUsers = await DbContext.GroupPairs.Include(p => p.GroupUser).Include(p => p.Group)
            .Where(g => g.GroupGID == dto.Group.GID)
            .ToListAsync().ConfigureAwait(false);
        var usersToPrune = allGroupUsers.Where(p => !p.IsPinned && !p.IsModerator
            && p.GroupUserUID != UserUID
            && p.Group.OwnerUID != p.GroupUserUID
            && p.GroupUser.LastLoggedIn.AddDays(days) < DateTime.UtcNow);

        if (!execute) return usersToPrune.Count();

        DbContext.GroupPairs.RemoveRange(usersToPrune);

        foreach (var pair in usersToPrune)
        {
            await Clients.Users(allGroupUsers.Where(p => !usersToPrune.Contains(p)).Select(g => g.GroupUserUID))
                .Client_GroupPairLeft(new GroupPairDto(dto.Group, pair.GroupUser.ToUserData())).ConfigureAwait(false);
        }

        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        return usersToPrune.Count();
    }

    [Authorize(Policy = "Identified")]
    public async Task GroupRemoveUser(GroupPairDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));

        var (hasRights, group) = await TryValidateGroupModeratorOrOwner(dto.Group.GID).ConfigureAwait(false);
        if (!hasRights) return;

        var (userExists, groupPair) = await TryValidateUserInGroup(dto.Group.GID, dto.User.UID).ConfigureAwait(false);
        if (!userExists) return;

        if (groupPair.IsModerator || string.Equals(group.OwnerUID, dto.User.UID, StringComparison.Ordinal)) return;
        _logger.LogCallInfo(SpheneHubLogger.Args(dto, "Success"));

        DbContext.GroupPairs.Remove(groupPair);

        var groupPairs = DbContext.GroupPairs.Where(p => p.GroupGID == group.GID).AsNoTracking().ToList();
        await Clients.Users(groupPairs.Select(p => p.GroupUserUID)).Client_GroupPairLeft(dto).ConfigureAwait(false);

        var sharedData = await DbContext.CharaDataAllowances.Where(u => u.AllowedGroup != null && u.AllowedGroupGID == dto.GID && u.ParentUploaderUID == dto.UID).ToListAsync().ConfigureAwait(false);
        DbContext.CharaDataAllowances.RemoveRange(sharedData);

        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        var userIdent = await GetUserIdent(dto.User.UID).ConfigureAwait(false);
        if (userIdent == null)
        {
            await DbContext.SaveChangesAsync().ConfigureAwait(false);
            return;
        }

        await Clients.User(dto.User.UID).Client_GroupDelete(new GroupDto(dto.Group)).ConfigureAwait(false);

        var userPairs = await GetAllPairInfo(dto.User.UID).ConfigureAwait(false);
        foreach (var groupUserPair in groupPairs)
        {
            await UserGroupLeave(groupUserPair, userIdent, userPairs, dto.User.UID).ConfigureAwait(false);
        }
    }

    [Authorize(Policy = "Identified")]
    public async Task GroupSetUserInfo(GroupPairUserInfoDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));

        var (userExists, userPair) = await TryValidateUserInGroup(dto.Group.GID, dto.User.UID).ConfigureAwait(false);
        if (!userExists) return;

        var (userIsOwner, _) = await TryValidateOwner(dto.Group.GID).ConfigureAwait(false);
        var (userIsModerator, _) = await TryValidateGroupModeratorOrOwner(dto.Group.GID).ConfigureAwait(false);

        if (dto.GroupUserInfo.HasFlag(GroupPairUserInfo.IsPinned) && userIsModerator && !userPair.IsPinned)
        {
            userPair.IsPinned = true;
        }
        else if (userIsModerator && userPair.IsPinned)
        {
            userPair.IsPinned = false;
        }

        if (dto.GroupUserInfo.HasFlag(GroupPairUserInfo.IsModerator) && userIsOwner && !userPair.IsModerator)
        {
            userPair.IsModerator = true;
        }
        else if (userIsOwner && userPair.IsModerator)
        {
            userPair.IsModerator = false;
        }

        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        var groupPairs = await DbContext.GroupPairs.AsNoTracking().Where(p => p.GroupGID == dto.Group.GID).Select(p => p.GroupUserUID).ToListAsync().ConfigureAwait(false);
        await Clients.Users(groupPairs).Client_GroupPairChangeUserInfo(new GroupPairUserInfoDto(dto.Group, dto.User, userPair.ToEnum())).ConfigureAwait(false);
    }

    [Authorize(Policy = "Identified")]
    public async Task<List<GroupFullInfoDto>> GroupsGetAll()
    {
        _logger.LogCallInfo();

        var groups = await DbContext.GroupPairs.Include(g => g.Group).Include(g => g.Group.Owner).Where(g => g.GroupUserUID == UserUID).AsNoTracking().ToListAsync().ConfigureAwait(false);
        var preferredPermissions = (await DbContext.GroupPairPreferredPermissions.Where(u => u.UserUID == UserUID).ToListAsync().ConfigureAwait(false))
            .Where(u => groups.Exists(k => string.Equals(k.GroupGID, u.GroupGID, StringComparison.Ordinal)))
            .ToDictionary(u => groups.First(f => string.Equals(f.GroupGID, u.GroupGID, StringComparison.Ordinal)), u => u);
        var groupInfos = await DbContext.GroupPairs.Where(u => groups.Select(g => g.GroupGID).Contains(u.GroupGID) && (u.IsPinned || u.IsModerator))
            .ToListAsync().ConfigureAwait(false);

        return preferredPermissions.Select(g => new GroupFullInfoDto(g.Key.Group.ToGroupData(), g.Key.Group.Owner.ToUserData(),
                g.Key.Group.ToEnum(), g.Value.ToEnum(), g.Key.ToEnum(),
                groupInfos.Where(i => string.Equals(i.GroupGID, g.Key.GroupGID, StringComparison.Ordinal))
                .ToDictionary(i => i.GroupUserUID, i => i.ToEnum(), StringComparer.Ordinal))).ToList();
    }

    [Authorize(Policy = "Identified")]
    public async Task GroupUnbanUser(GroupPairDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));

        var (userHasRights, _) = await TryValidateGroupModeratorOrOwner(dto.Group.GID).ConfigureAwait(false);
        if (!userHasRights) return;

        var banEntry = await DbContext.GroupBans.SingleOrDefaultAsync(g => g.GroupGID == dto.Group.GID && g.BannedUserUID == dto.User.UID).ConfigureAwait(false);
        if (banEntry == null) return;

        DbContext.Remove(banEntry);
        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        _logger.LogCallInfo(SpheneHubLogger.Args(dto, "Success"));
    }

    [Authorize(Policy = "Identified")]
    public async Task<bool> GroupSetAreaBinding(AreaBoundSyncshellDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));

        var (isOwner, group) = await TryValidateOwner(dto.Group.GID).ConfigureAwait(false);
        if (!isOwner) return false;

        // Check if area binding already exists for this group
        var existingBinding = await DbContext.AreaBoundSyncshells
            .Include(a => a.Locations)
            .SingleOrDefaultAsync(a => a.GroupGID == dto.Group.GID)
            .ConfigureAwait(false);
        
        if (existingBinding != null)
        {
            // Update existing binding settings
            existingBinding.AutoBroadcastEnabled = dto.Settings.AutoBroadcastEnabled;
            existingBinding.RequireOwnerPresence = dto.Settings.RequireOwnerPresence;
            existingBinding.MaxAutoJoinUsers = dto.Settings.MaxAutoJoinUsers;
            existingBinding.NotifyOnUserEnter = dto.Settings.NotifyOnUserEnter;
            existingBinding.NotifyOnUserLeave = dto.Settings.NotifyOnUserLeave;
            existingBinding.CustomJoinMessage = dto.Settings.CustomJoinMessage;
            existingBinding.JoinRules = dto.Settings.JoinRules;
            existingBinding.RequireRulesAcceptance = dto.Settings.RequireRulesAcceptance;
            
            // Use the rules version from the client (client handles version increment logic)
            existingBinding.RulesVersion = dto.Settings.RulesVersion;
            
            // Clear existing locations and add new ones
            DbContext.AreaBoundLocations.RemoveRange(existingBinding.Locations);
            existingBinding.Locations.Clear();
            
            foreach (var locationDto in dto.BoundAreas)
            {
                var location = new AreaBoundLocation
                {
                    GroupGID = dto.Group.GID,
                    ServerId = (ushort)locationDto.Location.ServerId,
                    MapId = locationDto.Location.MapId,
                    TerritoryId = locationDto.Location.TerritoryId,
                    DivisionId = locationDto.Location.DivisionId,
                    WardId = (ushort)locationDto.Location.WardId,
                    HouseId = (ushort)locationDto.Location.HouseId,
                    RoomId = (byte)locationDto.Location.RoomId,
                    MatchingMode = locationDto.MatchingMode,
                    LocationName = locationDto.LocationName,
                    CreatedAt = DateTime.UtcNow
                };
                existingBinding.Locations.Add(location);
            }
        }
        else
        {
            // Create new binding
            var newBinding = new AreaBoundSyncshell()
            {
                GroupGID = dto.Group.GID,
                AutoBroadcastEnabled = dto.Settings.AutoBroadcastEnabled,
                RequireOwnerPresence = dto.Settings.RequireOwnerPresence,
                MaxAutoJoinUsers = dto.Settings.MaxAutoJoinUsers,
                NotifyOnUserEnter = dto.Settings.NotifyOnUserEnter,
                NotifyOnUserLeave = dto.Settings.NotifyOnUserLeave,
                CustomJoinMessage = dto.Settings.CustomJoinMessage,
                JoinRules = dto.Settings.JoinRules,
                RequireRulesAcceptance = dto.Settings.RequireRulesAcceptance,
                RulesVersion = 1,
                CreatedAt = DateTime.UtcNow,
                Locations = new List<AreaBoundLocation>()
            };
            
            foreach (var locationDto in dto.BoundAreas)
            {
                var location = new AreaBoundLocation
                {
                    GroupGID = dto.Group.GID,
                    ServerId = (ushort)locationDto.Location.ServerId,
                    MapId = locationDto.Location.MapId,
                    TerritoryId = locationDto.Location.TerritoryId,
                    DivisionId = locationDto.Location.DivisionId,
                    WardId = (ushort)locationDto.Location.WardId,
                    HouseId = (ushort)locationDto.Location.HouseId,
                    RoomId = (byte)locationDto.Location.RoomId,
                    MatchingMode = locationDto.MatchingMode,
                    LocationName = locationDto.LocationName,
                    CreatedAt = DateTime.UtcNow
                };
                newBinding.Locations.Add(location);
            }
            
            await DbContext.AreaBoundSyncshells.AddAsync(newBinding).ConfigureAwait(false);
        }

        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        // Broadcast configuration update to all connected clients
        await Clients.All.Client_AreaBoundSyncshellConfigurationUpdate().ConfigureAwait(false);

        _logger.LogCallInfo(SpheneHubLogger.Args(dto, "Success"));
        return true;
    }

    [Authorize(Policy = "Identified")]
    public async Task<bool> GroupRemoveAreaLocation(GroupDto dto, int locationId)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto, locationId));

        var (isOwner, group) = await TryValidateOwner(dto.GID).ConfigureAwait(false);
        if (!isOwner) return false;

        var binding = await DbContext.AreaBoundSyncshells
            .Include(a => a.Locations)
            .SingleOrDefaultAsync(a => a.GroupGID == dto.GID)
            .ConfigureAwait(false);
        
        if (binding == null) return false;

        var locationToRemove = binding.Locations.FirstOrDefault(l => l.Id == locationId);
        if (locationToRemove == null) return false;

        // Remove the specific location first
        DbContext.AreaBoundLocations.Remove(locationToRemove);
        binding.Locations.Remove(locationToRemove);

        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        // Broadcast configuration update to all connected clients
        // This will trigger the client to re-evaluate if users should still be in the syncshell
        await Clients.All.Client_AreaBoundSyncshellConfigurationUpdate().ConfigureAwait(false);

        _logger.LogCallInfo(SpheneHubLogger.Args(dto, locationId, "Success"));
        return true;
    }

    [Authorize(Policy = "Identified")]
    public async Task<bool> GroupDisableAreaBinding(GroupDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));

        var (isOwner, group) = await TryValidateOwner(dto.GID).ConfigureAwait(false);
        if (!isOwner) return false;

        var binding = await DbContext.AreaBoundSyncshells.SingleOrDefaultAsync(a => a.GroupGID == dto.GID).ConfigureAwait(false);
        if (binding == null) return false;

        // Remove the area binding but keep the syncshell (group) intact
        // This converts the area-bound syncshell back to a normal syncshell
        DbContext.AreaBoundSyncshells.Remove(binding);
        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        // Broadcast configuration update to all connected clients
        await Clients.All.Client_AreaBoundSyncshellConfigurationUpdate().ConfigureAwait(false);

        _logger.LogCallInfo(SpheneHubLogger.Args(dto, "Success - Converted to normal syncshell"));
        return true;
    }

    [Authorize(Policy = "Identified")]
    public async Task<bool> GroupRemoveAreaBinding(GroupDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));

        var (isOwner, group) = await TryValidateOwner(dto.GID).ConfigureAwait(false);
        if (!isOwner) return false;

        var binding = await DbContext.AreaBoundSyncshells.SingleOrDefaultAsync(a => a.GroupGID == dto.GID).ConfigureAwait(false);
        if (binding == null) return false;

        // Remove the area binding but keep the syncshell intact
        DbContext.AreaBoundSyncshells.Remove(binding);
        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        // Broadcast configuration update to all connected clients
        await Clients.All.Client_AreaBoundSyncshellConfigurationUpdate().ConfigureAwait(false);

        _logger.LogCallInfo(SpheneHubLogger.Args(dto, "Success"));
        return true;
    }

    [Authorize(Policy = "Identified")]
    public async Task<List<AreaBoundSyncshellDto>> GroupGetAreaBoundSyncshells()
    {
        _logger.LogCallInfo(SpheneHubLogger.Args());

        var areaBoundSyncshells = await DbContext.AreaBoundSyncshells
            .Include(a => a.Group)
            .Include(a => a.Locations)
            .Where(a => a.AutoBroadcastEnabled) // Only return syncshells with auto-broadcast enabled
            .ToListAsync()
            .ConfigureAwait(false);

        var result = new List<AreaBoundSyncshellDto>();
        
        foreach (var binding in areaBoundSyncshells)
        {
            var boundAreas = binding.Locations.Select(location => new AreaBoundLocationDto
            {
                Id = location.Id,
                Location = new LocationInfo
                {
                    ServerId = location.ServerId,
                    MapId = location.MapId,
                    TerritoryId = location.TerritoryId,
                    DivisionId = location.DivisionId,
                    WardId = location.WardId,
                    HouseId = location.HouseId,
                    RoomId = location.RoomId
                },
                MatchingMode = location.MatchingMode,
                LocationName = location.LocationName,
                CreatedAt = location.CreatedAt
            }).ToList();
            
            var settings = new AreaBoundSettings
            {
                AutoBroadcastEnabled = binding.AutoBroadcastEnabled,
                RequireOwnerPresence = binding.RequireOwnerPresence,
                MaxAutoJoinUsers = binding.MaxAutoJoinUsers,
                NotifyOnUserEnter = binding.NotifyOnUserEnter,
                NotifyOnUserLeave = binding.NotifyOnUserLeave,
                CustomJoinMessage = binding.CustomJoinMessage,
                JoinRules = binding.JoinRules,
                RequireRulesAcceptance = binding.RequireRulesAcceptance,
                RulesVersion = binding.RulesVersion
            };
            
            var dto = new AreaBoundSyncshellDto(binding.Group.ToGroupData(), boundAreas)
            {
                Settings = settings
            };
            
            result.Add(dto);
        }

        _logger.LogCallInfo(SpheneHubLogger.Args($"Returning {result.Count} area-bound syncshells"));
        return result;
    }

    [Authorize(Policy = "Identified")]
    public async Task<bool> GroupRequestAreaBoundJoin(AreaBoundJoinRequestDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));

        // Validate that the area-bound syncshell exists and is active
        var binding = await DbContext.AreaBoundSyncshells
            .Include(a => a.Group)
                .ThenInclude(g => g.Owner)
            .SingleOrDefaultAsync(a => a.GroupGID == dto.Group.GID && a.AutoBroadcastEnabled)
            .ConfigureAwait(false);

        if (binding == null) return false;

        // Check if user is already in the group
        var isAlreadyMember = await DbContext.GroupPairs
            .AnyAsync(gp => gp.GroupGID == dto.Group.GID && gp.GroupUserUID == UserUID)
            .ConfigureAwait(false);

        if (isAlreadyMember) return false;

        // Check if user is banned
        var isBanned = await DbContext.GroupBans
            .AnyAsync(gb => gb.GroupGID == dto.Group.GID && gb.BannedUserUID == UserUID)
            .ConfigureAwait(false);

        if (isBanned) return false;

        // Check current group member count
        var currentMemberCount = await DbContext.GroupPairs
            .CountAsync(gp => gp.GroupGID == dto.Group.GID)
            .ConfigureAwait(false);

        if (currentMemberCount >= binding.MaxAutoJoinUsers) return false;

        // Check user consent if rules acceptance is required
        if (binding.RequireRulesAcceptance)
        {
            var consent = await DbContext.AreaBoundSyncshellConsents
                .SingleOrDefaultAsync(c => c.UserUID == UserUID && c.SyncshellGID == dto.Group.GID)
                .ConfigureAwait(false);

            // If no consent exists or rules version is outdated, deny auto-join
            if (consent == null || !consent.HasAccepted || consent.AcceptedRulesVersion < binding.RulesVersion)
            {
                return false;
            }
        }

        // Check if owner is online
        var owner = binding.Group.Owner;
        var ownerIdent = await GetUserIdent(owner.UID).ConfigureAwait(false);
        bool isOwnerOnline = !string.IsNullOrEmpty(ownerIdent);

        if (isOwnerOnline)
        {
            // Send join request to group owner
            await Clients.User(owner.UID).Client_AreaBoundJoinRequest(dto).ConfigureAwait(false);
            _logger.LogDebug("Sent area-bound join request to online owner {OwnerUID} for syncshell {SyncshellGID}", owner.UID, dto.Group.GID);
        }
        else
        {
            // Owner is offline, automatically approve the join request
            _logger.LogDebug("Owner {OwnerUID} is offline, automatically approving area-bound join request for syncshell {SyncshellGID}", owner.UID, dto.Group.GID);
            
            // Create an auto-approved response
            var autoResponse = new AreaBoundJoinResponseDto(
                dto.Group,
                dto.User,
                true,
                "Auto-approved (owner offline)"
            );
            
            // Send the response back to the requesting user
            await Clients.User(dto.User.UID).Client_AreaBoundJoinResponse(autoResponse).ConfigureAwait(false);
            
            // Automatically join the user to the group
            await JoinUserToGroup(dto.User.UID, binding.Group, GroupUserPreferredPermissions.NoneSet).ConfigureAwait(false);
        }

        _logger.LogCallInfo(SpheneHubLogger.Args(dto, "Success"));
        return true;
    }

    [Authorize(Policy = "Identified")]
    public async Task<bool> GroupCheckAreaBoundConsent(string syncshellGID)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(syncshellGID));

        // Validate that the area-bound syncshell exists
        var binding = await DbContext.AreaBoundSyncshells
            .SingleOrDefaultAsync(a => a.GroupGID == syncshellGID)
            .ConfigureAwait(false);

        if (binding == null) return false;

        // Always check if user has valid consent (for auto-rejoin functionality)
        var consent = await DbContext.AreaBoundSyncshellConsents
            .SingleOrDefaultAsync(c => c.UserUID == UserUID && c.SyncshellGID == syncshellGID)
            .ConfigureAwait(false);

        // If no consent exists, user has never joined before
        if (consent == null) return false;

        // User must have accepted to auto-rejoin
        if (!consent.HasAccepted) return false;

        // If rules acceptance is required, check rules version
        if (binding.RequireRulesAcceptance)
        {
            return consent.AcceptedRulesVersion >= binding.RulesVersion;
        }

        // For syncshells without rules, consent is valid if user has accepted before
        return true;
    }

    [Authorize(Policy = "Identified")]
    public async Task<bool> GroupResetAreaBoundConsent(string syncshellGID)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(syncshellGID));

        // Validate that the area-bound syncshell exists
        var binding = await DbContext.AreaBoundSyncshells
            .SingleOrDefaultAsync(a => a.GroupGID == syncshellGID)
            .ConfigureAwait(false);

        if (binding == null) return false;

        // Find existing consent record
        var consent = await DbContext.AreaBoundSyncshellConsents
            .SingleOrDefaultAsync(c => c.UserUID == UserUID && c.SyncshellGID == syncshellGID)
            .ConfigureAwait(false);

        if (consent != null)
        {
            consent.HasAccepted = false;
            consent.ConsentGivenAt = DateTime.UtcNow;
            await DbContext.SaveChangesAsync().ConfigureAwait(false);
        }

        _logger.LogCallInfo(SpheneHubLogger.Args(syncshellGID, "Success"));
        return true;
    }

    [Authorize(Policy = "Identified")]
    public async Task GroupRespondToAreaBoundJoin(AreaBoundJoinResponseDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));

        // Validate that the user is the owner of the group
        var (isOwner, group) = await TryValidateOwner(dto.Group.GID).ConfigureAwait(false);
        if (!isOwner) return;

        // Send response to the requesting user
        await Clients.User(dto.User.UID).Client_AreaBoundJoinResponse(dto).ConfigureAwait(false);

        // If approved, automatically join the requesting user to the group
        if (dto.Accepted)
        {
            await JoinUserToGroup(dto.User.UID, group, GroupUserPreferredPermissions.NoneSet).ConfigureAwait(false);
        }

        _logger.LogCallInfo(SpheneHubLogger.Args(dto, "Success"));
    }

    private async Task JoinUserToGroup(string userUID, Group group, GroupUserPreferredPermissions preferredPermissions)
    {
        // Check if user is already in the group
        var existingPair = await DbContext.GroupPairs.AsNoTracking()
            .SingleOrDefaultAsync(g => g.GroupGID == group.GID && g.GroupUserUID == userUID).ConfigureAwait(false);
        if (existingPair != null) return;

        // Check if user is banned
        var isBanned = await DbContext.GroupBans
            .AnyAsync(g => g.GroupGID == group.GID && g.BannedUserUID == userUID).ConfigureAwait(false);
        if (isBanned) return;

        // Check group capacity
        var existingUserCount = await DbContext.GroupPairs.AsNoTracking()
            .CountAsync(g => g.GroupGID == group.GID).ConfigureAwait(false);
        if (existingUserCount >= _maxGroupUserCount) return;

        // Check user's joined groups limit
        var joinedGroups = await DbContext.GroupPairs.CountAsync(g => g.GroupUserUID == userUID).ConfigureAwait(false);
        if (joinedGroups >= _maxJoinedGroupsByUser) return;

        // Get all pairs before we join
        var allUserPairs = await GetAllPairInfo(userUID).ConfigureAwait(false);

        // Create new group pair
        GroupPair newPair = new()
        {
            GroupGID = group.GID,
            GroupUserUID = userUID,
        };

        // Handle preferred permissions
        var preferredPerms = await DbContext.GroupPairPreferredPermissions
            .SingleOrDefaultAsync(u => u.UserUID == userUID && u.GroupGID == group.GID).ConfigureAwait(false);
        
        if (preferredPerms == null)
        {
            GroupPairPreferredPermission newPerms = new()
            {
                GroupGID = group.GID,
                UserUID = userUID,
                DisableSounds = preferredPermissions.IsDisableSounds(),
                DisableVFX = preferredPermissions.IsDisableVFX(),
                DisableAnimations = preferredPermissions.IsDisableAnimations(),
                IsPaused = false
            };
            DbContext.Add(newPerms);
            preferredPerms = newPerms;
        }
        else
        {
            preferredPerms.DisableSounds = preferredPermissions.IsDisableSounds();
            preferredPerms.DisableVFX = preferredPermissions.IsDisableVFX();
            preferredPerms.DisableAnimations = preferredPermissions.IsDisableAnimations();
            preferredPerms.IsPaused = false;
            DbContext.Update(preferredPerms);
        }

        await DbContext.GroupPairs.AddAsync(newPair).ConfigureAwait(false);
        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        _logger.LogCallInfo(SpheneHubLogger.Args($"User {userUID} joined group {group.GID}", "Success"));

        // Send group info to the new member
        var groupInfos = await DbContext.GroupPairs.Where(u => u.GroupGID == group.GID && (u.IsPinned || u.IsModerator))
            .ToListAsync().ConfigureAwait(false);
        await Clients.User(userUID).Client_GroupSendFullInfo(new GroupFullInfoDto(group.ToGroupData(), group.Owner.ToUserData(),
            group.ToEnum(), preferredPerms.ToEnum(), newPair.ToEnum(),
            groupInfos.ToDictionary(u => u.GroupUserUID, u => u.ToEnum(), StringComparer.Ordinal))).ConfigureAwait(false);

        var newUser = await DbContext.Users.SingleAsync(u => u.UID == userUID).ConfigureAwait(false);
        var groupPairs = await DbContext.GroupPairs.Include(p => p.GroupUser)
            .Where(p => p.GroupGID == group.GID && p.GroupUserUID != userUID).ToListAsync().ConfigureAwait(false);

        var userPairsAfterJoin = await GetAllPairInfo(userUID).ConfigureAwait(false);

        // Handle permissions and notifications for existing group members
        foreach (var pair in groupPairs)
        {
            var perms = userPairsAfterJoin.TryGetValue(pair.GroupUserUID, out var userinfo);
            var ownPermissionsToOther = userinfo?.OwnPermissions ?? null;
            
            if (ownPermissionsToOther == null)
            {
                var existingPermissionsOnDb = await DbContext.Permissions
                    .SingleOrDefaultAsync(p => p.UserUID == userUID && p.OtherUserUID == pair.GroupUserUID).ConfigureAwait(false);

                if (existingPermissionsOnDb == null)
                {
                    ownPermissionsToOther = new()
                    {
                        UserUID = userUID,
                        OtherUserUID = pair.GroupUserUID,
                        DisableAnimations = preferredPerms.DisableAnimations,
                        DisableSounds = preferredPerms.DisableSounds,
                        DisableVFX = preferredPerms.DisableVFX,
                        IsPaused = preferredPerms.IsPaused,
                        Sticky = false
                    };
                    await DbContext.Permissions.AddAsync(ownPermissionsToOther).ConfigureAwait(false);
                }
                else
                {
                    existingPermissionsOnDb.DisableAnimations = preferredPerms.DisableAnimations;
                    existingPermissionsOnDb.DisableSounds = preferredPerms.DisableSounds;
                    existingPermissionsOnDb.DisableVFX = preferredPerms.DisableVFX;
                    existingPermissionsOnDb.IsPaused = false;
                    existingPermissionsOnDb.Sticky = false;
                    DbContext.Update(existingPermissionsOnDb);
                    ownPermissionsToOther = existingPermissionsOnDb;
                }
            }
            else if (!ownPermissionsToOther.Sticky)
            {
                ownPermissionsToOther = await DbContext.Permissions
                    .SingleAsync(u => u.UserUID == userUID && u.OtherUserUID == pair.GroupUserUID).ConfigureAwait(false);
                ownPermissionsToOther.DisableAnimations = preferredPerms.DisableAnimations;
                ownPermissionsToOther.DisableVFX = preferredPerms.DisableVFX;
                ownPermissionsToOther.DisableSounds = preferredPerms.DisableSounds;
                ownPermissionsToOther.IsPaused = false;
                DbContext.Update(ownPermissionsToOther);
            }

            // Handle other user's permissions to the new member
            var otherPermissionToSelf = userinfo?.OtherPermissions ?? null;
            if (otherPermissionToSelf == null)
            {
                var otherExistingPermsOnDb = await DbContext.Permissions
                    .SingleOrDefaultAsync(p => p.UserUID == pair.GroupUserUID && p.OtherUserUID == userUID).ConfigureAwait(false);

                if (otherExistingPermsOnDb == null)
                {
                    var otherPreferred = await DbContext.GroupPairPreferredPermissions
                        .SingleAsync(u => u.GroupGID == group.GID && u.UserUID == pair.GroupUserUID).ConfigureAwait(false);
                    otherExistingPermsOnDb = new()
                    {
                        UserUID = pair.GroupUserUID,
                        OtherUserUID = userUID,
                        DisableAnimations = otherPreferred.DisableAnimations,
                        DisableSounds = otherPreferred.DisableSounds,
                        DisableVFX = otherPreferred.DisableVFX,
                        IsPaused = otherPreferred.IsPaused,
                        Sticky = false
                    };
                    await DbContext.AddAsync(otherExistingPermsOnDb).ConfigureAwait(false);
                }
                else if (!otherExistingPermsOnDb.Sticky)
                {
                    var otherPreferred = await DbContext.GroupPairPreferredPermissions
                        .SingleAsync(u => u.GroupGID == group.GID && u.UserUID == pair.GroupUserUID).ConfigureAwait(false);
                    otherExistingPermsOnDb.DisableAnimations = otherPreferred.DisableAnimations;
                    otherExistingPermsOnDb.DisableSounds = otherPreferred.DisableSounds;
                    otherExistingPermsOnDb.DisableVFX = otherPreferred.DisableVFX;
                    otherExistingPermsOnDb.IsPaused = otherPreferred.IsPaused;
                    DbContext.Update(otherExistingPermsOnDb);
                }
                otherPermissionToSelf = otherExistingPermsOnDb;
            }
            else if (!otherPermissionToSelf.Sticky)
            {
                var otherPreferred = await DbContext.GroupPairPreferredPermissions
                    .SingleAsync(u => u.GroupGID == group.GID && u.UserUID == pair.GroupUserUID).ConfigureAwait(false);
                otherPermissionToSelf.DisableAnimations = otherPreferred.DisableAnimations;
                otherPermissionToSelf.DisableSounds = otherPreferred.DisableSounds;
                otherPermissionToSelf.DisableVFX = otherPreferred.DisableVFX;
                otherPermissionToSelf.IsPaused = otherPreferred.IsPaused;
                DbContext.Update(otherPermissionToSelf);
            }

            // Send join notifications
            await Clients.User(userUID).Client_GroupPairJoined(new GroupPairFullInfoDto(group.ToGroupData(),
                pair.ToUserData(), ownPermissionsToOther.ToUserPermissions(setSticky: ownPermissionsToOther.Sticky),
                otherPermissionToSelf.ToUserPermissions(setSticky: false))).ConfigureAwait(false);
            await Clients.User(pair.GroupUserUID).Client_GroupPairJoined(new GroupPairFullInfoDto(group.ToGroupData(),
                newUser.ToUserData(), otherPermissionToSelf.ToUserPermissions(setSticky: otherPermissionToSelf.Sticky),
                ownPermissionsToOther.ToUserPermissions(setSticky: false))).ConfigureAwait(false);

            // Send online notifications if appropriate
            if ((!allUserPairs.ContainsKey(pair.GroupUserUID) || (allUserPairs.TryGetValue(pair.GroupUserUID, out var info) && !info.IsSynced))
                && !otherPermissionToSelf.IsPaused && !ownPermissionsToOther.IsPaused)
            {
                var groupUserIdent = await GetUserIdent(pair.GroupUserUID).ConfigureAwait(false);
                var newUserIdent = await GetUserIdent(userUID).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(groupUserIdent) && !string.IsNullOrEmpty(newUserIdent))
                {
                    await Clients.User(userUID).Client_UserSendOnline(new(pair.ToUserData(), groupUserIdent)).ConfigureAwait(false);
                    await Clients.User(pair.GroupUserUID).Client_UserSendOnline(new(newUser.ToUserData(), newUserIdent)).ConfigureAwait(false);
                }
            }
        }

        await DbContext.SaveChangesAsync().ConfigureAwait(false);
    }

    [Authorize(Policy = "Identified")]
    public async Task BroadcastAreaBoundSyncshells(LocationInfo userLocation)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(userLocation));
        _logger.LogCallInfo(SpheneHubLogger.Args("BroadcastAreaBoundSyncshells called from user", UserUID, "at location:", userLocation != null ? userLocation.ToString() : "null"));

        // Find all area-bound syncshells that have locations matching the user's location
        var areaBoundSyncshells = await DbContext.AreaBoundSyncshells
            .Include(a => a.Group)
            .Include(a => a.Locations)
            .Where(a => a.AutoBroadcastEnabled)
            .ToListAsync()
            .ConfigureAwait(false);

        _logger.LogDebug("Found {0} area-bound syncshells with auto-broadcast enabled", areaBoundSyncshells.Count);

        var matchingSyncshells = new List<AreaBoundSyncshell>();
        
        foreach (var syncshell in areaBoundSyncshells)
        {
            // Check if any of the syncshell's locations match the user's location
            bool hasMatchingLocation = false;
            
            foreach (var location in syncshell.Locations)
            {
                bool matches = true;

                // Check server match (if specified)
                if (location.ServerId != 0 && location.ServerId != userLocation.ServerId)
                    matches = false;

                // Check territory match (if specified)
                if (matches && location.TerritoryId != 0 && location.TerritoryId != userLocation.TerritoryId)
                    matches = false;

                if (!matches) continue;

                // Apply matching mode logic
                switch (location.MatchingMode)
                {
                    case AreaMatchingMode.ExactMatch:
                        matches = (location.MapId == 0 || location.MapId == userLocation.MapId) &&
                                 (location.DivisionId == 0 || location.DivisionId == userLocation.DivisionId) &&
                                 (location.WardId == 0 || location.WardId == userLocation.WardId) &&
                                 (location.HouseId == 0 || location.HouseId == userLocation.HouseId) &&
                                 (location.RoomId == 0 || location.RoomId == userLocation.RoomId);
                        break;
                    case AreaMatchingMode.TerritoryOnly:
                        // Already checked territory above
                        break;
                    case AreaMatchingMode.ServerAndTerritory:
                        // Already checked server and territory above
                        break;
                    case AreaMatchingMode.HousingWardOnly:
                        matches = location.WardId == 0 || location.WardId == userLocation.WardId;
                        break;
                    case AreaMatchingMode.HousingPlotOnly:
                        matches = (location.WardId == 0 || location.WardId == userLocation.WardId) &&
                                 (location.HouseId == 0 || location.HouseId == userLocation.HouseId);
                        break;
                }

                if (matches)
                {
                    hasMatchingLocation = true;
                    break;
                }
            }
            
            if (hasMatchingLocation)
            {
                matchingSyncshells.Add(syncshell);
            }
        }

        // Send broadcast for each matching syncshell
        foreach (var syncshell in matchingSyncshells)
        {
            // Check if user is already a member
            var isAlreadyMember = await DbContext.GroupPairs
                .AnyAsync(gp => gp.GroupGID == syncshell.GroupGID && gp.GroupUserUID == UserUID)
                .ConfigureAwait(false);

            if (isAlreadyMember) continue;

            // Check if user is banned
            var isBanned = await DbContext.GroupBans
                .AnyAsync(gb => gb.GroupGID == syncshell.GroupGID && gb.BannedUserUID == UserUID)
                .ConfigureAwait(false);

            if (isBanned) continue;

            // Check owner presence requirement
            if (syncshell.RequireOwnerPresence)
            {
                var ownerIdent = await GetUserIdent(syncshell.Group.OwnerUID).ConfigureAwait(false);
                if (string.IsNullOrEmpty(ownerIdent))
                {
                    _logger.LogDebug("Skipping area-bound syncshell {GroupId} broadcast - owner {OwnerId} is offline and RequireOwnerPresence is enabled", 
                        syncshell.GroupGID, syncshell.Group.OwnerUID);
                    continue;
                }
            }

            // Check current group member count
            var currentMemberCount = await DbContext.GroupPairs
                .CountAsync(gp => gp.GroupGID == syncshell.GroupGID)
                .ConfigureAwait(false);

            if (currentMemberCount >= syncshell.MaxAutoJoinUsers) continue;

            // Get users currently in the area (for now, just the current user)
            var currentUser = await DbContext.Users.SingleAsync(u => u.UID == UserUID).ConfigureAwait(false);
            var usersInArea = new List<Sphene.API.Data.UserData> { currentUser.ToUserData() };

            // Create broadcast DTO with the first matching location
            var matchingLocation = syncshell.Locations.First(l => 
            {
                bool matches = true;
                if (l.ServerId != 0 && l.ServerId != userLocation.ServerId) matches = false;
                if (matches && l.TerritoryId != 0 && l.TerritoryId != userLocation.TerritoryId) matches = false;
                // Add more matching logic as needed
                return matches;
            });

            var broadcastDto = new AreaBoundBroadcastDto(
                syncshell.Group.ToGroupData(),
                new LocationInfo
                {
                    ServerId = matchingLocation.ServerId,
                    MapId = matchingLocation.MapId,
                    TerritoryId = matchingLocation.TerritoryId,
                    DivisionId = matchingLocation.DivisionId,
                    WardId = matchingLocation.WardId,
                    HouseId = matchingLocation.HouseId,
                    RoomId = matchingLocation.RoomId
                },
                usersInArea
            );

            // Send broadcast to the user
            await Clients.User(UserUID).Client_AreaBoundSyncshellBroadcast(broadcastDto).ConfigureAwait(false);

            _logger.LogDebug("Broadcasted area-bound syncshell {GroupId} to user {UserId} at location {Location}", 
                syncshell.GroupGID, UserUID, userLocation);
        }

        _logger.LogCallInfo(SpheneHubLogger.Args(userLocation, $"Broadcasted {matchingSyncshells.Count} syncshells"));
    }

    [Authorize(Policy = "Identified")]
    public async Task<SyncshellWelcomePageDto?> GroupGetWelcomePage(GroupDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));

        // Validate user is a member of the group
        var groupPair = await DbContext.GroupPairs
            .SingleOrDefaultAsync(gp => gp.GroupGID == dto.Group.GID && gp.GroupUserUID == UserUID)
            .ConfigureAwait(false);

        if (groupPair == null) return null;

        // Get welcome page data
        var welcomePage = await DbContext.SyncshellWelcomePages
            .SingleOrDefaultAsync(w => w.GroupGID == dto.Group.GID)
            .ConfigureAwait(false);

        if (welcomePage == null || !welcomePage.IsEnabled) return null;

        return new SyncshellWelcomePageDto(
            welcomePage.GroupGID,
            welcomePage.WelcomeText,
            welcomePage.WelcomeImageBase64,
            welcomePage.ImageFileName,
            welcomePage.ImageContentType,
            welcomePage.ImageSize,
            welcomePage.IsEnabled,
            welcomePage.ShowOnJoin,
            welcomePage.ShowOnAreaBoundJoin,
            welcomePage.CreatedAt,
            welcomePage.UpdatedAt
        );
    }

    [Authorize(Policy = "Identified")]
    public async Task<bool> GroupSetWelcomePage(SyncshellWelcomePageUpdateDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));

        // Validate user is the owner or moderator of the group
        var (hasRights, group) = await TryValidateGroupModeratorOrOwner(dto.Group.GID).ConfigureAwait(false);
        if (!hasRights) return false;

        // Find or create welcome page
        var welcomePage = await DbContext.SyncshellWelcomePages
            .SingleOrDefaultAsync(w => w.GroupGID == dto.Group.GID)
            .ConfigureAwait(false);

        if (welcomePage == null)
        {
            welcomePage = new SyncshellWelcomePage
            {
                GroupGID = dto.Group.GID,
                CreatedAt = DateTime.UtcNow
            };
            await DbContext.SyncshellWelcomePages.AddAsync(welcomePage).ConfigureAwait(false);
        }

        // Update welcome page data
        welcomePage.WelcomeText = dto.WelcomeText;
        welcomePage.WelcomeImageBase64 = dto.WelcomeImageBase64;
        welcomePage.ImageFileName = dto.ImageFileName;
        welcomePage.ImageContentType = dto.ImageContentType;
        welcomePage.ImageSize = dto.ImageSize;
        welcomePage.IsEnabled = dto.IsEnabled;
        welcomePage.ShowOnJoin = dto.ShowOnJoin;
        welcomePage.ShowOnAreaBoundJoin = dto.ShowOnAreaBoundJoin;
        welcomePage.UpdatedAt = DateTime.UtcNow;

        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        _logger.LogDebug("Updated welcome page for group {GroupId}", dto.Group.GID);
        return true;
    }

    [Authorize(Policy = "Identified")]
    public async Task<bool> GroupDeleteWelcomePage(GroupDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));

        // Validate user is the owner or moderator of the group
        var (hasRights, group) = await TryValidateGroupModeratorOrOwner(dto.Group.GID).ConfigureAwait(false);
        if (!hasRights) return false;

        // Find and delete welcome page
        var welcomePage = await DbContext.SyncshellWelcomePages
            .SingleOrDefaultAsync(w => w.GroupGID == dto.Group.GID)
            .ConfigureAwait(false);

        if (welcomePage == null) return false;

        DbContext.SyncshellWelcomePages.Remove(welcomePage);
        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        _logger.LogDebug("Deleted welcome page for group {GroupId}", dto.Group.GID);
        return true;
    }
}

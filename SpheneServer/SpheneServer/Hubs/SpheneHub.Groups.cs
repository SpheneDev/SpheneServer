using Sphene.API.Data;
using Sphene.API.Data.Enum;
using Sphene.API.Data.Extensions;
using Sphene.API.Dto.CharaData;
using Sphene.API.Dto.Group;
using SpheneServer.Services;
using SpheneServer.Utils;
using SpheneShared.Models;
using SpheneShared.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Formats.Jpeg;

namespace SpheneServer.Hubs;

public partial class SpheneHub
{
    [Authorize(Policy = "Identified")]
    public async Task GroupBanUser(GroupPairDto dto, string reason)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto, reason));

        if (!await HasCommunityRights(dto.Group.GID, SyncshellStaffPermission.ManageBans).ConfigureAwait(false)) return;

        var group = await DbContext.Groups.AsNoTracking()
            .SingleOrDefaultAsync(g => g.GID == dto.Group.GID).ConfigureAwait(false);
        if (group == null) return;

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

        if (!await HasCommunityRights(dto.Group.GID, SyncshellStaffPermission.ManageSettings).ConfigureAwait(false)) return;

        var group = await DbContext.Groups
            .SingleOrDefaultAsync(g => g.GID == dto.Group.GID).ConfigureAwait(false);
        if (group == null) return;

        var invitesEnabledBefore = group.InvitesEnabled;
        group.InvitesEnabled = !dto.Permissions.HasFlag(GroupPermissions.DisableInvites);
        group.PreferDisableSounds = dto.Permissions.HasFlag(GroupPermissions.PreferDisableSounds);
        group.PreferDisableAnimations = dto.Permissions.HasFlag(GroupPermissions.PreferDisableAnimations);
        group.PreferDisableVFX = dto.Permissions.HasFlag(GroupPermissions.PreferDisableVFX);

        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        var groupPairs = DbContext.GroupPairs.Where(p => p.GroupGID == dto.Group.GID).Select(p => p.GroupUserUID).ToList();
        await Clients.Users(groupPairs).Client_GroupChangePermissions(new GroupPermissionDto(dto.Group, dto.Permissions)).ConfigureAwait(false);

        if (invitesEnabledBefore != group.InvitesEnabled)
        {
            var hasAreaBinding = await DbContext.AreaBoundSyncshells.AsNoTracking()
                .AnyAsync(a => a.GroupGID == dto.Group.GID && a.AutoBroadcastEnabled)
                .ConfigureAwait(false);

            if (hasAreaBinding)
            {
                await Clients.All.Client_AreaBoundSyncshellConfigurationUpdate().ConfigureAwait(false);
            }
        }
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

        // Owner/mod or a staff role carrying ManagePage - the shell name is
        // part of the community identity delegated staff maintain.
        var (hasRights, group) = await TryValidateGroupModeratorOrOwner(dto.Group.GID).ConfigureAwait(false);
        if (!hasRights || group == null)
        {
            var staffBits = await GetStaffPermissionBits(dto.Group.GID, UserUID).ConfigureAwait(false);
            if ((staffBits & (long)SyncshellStaffPermission.ManagePage) == 0) return false;
            group = await DbContext.Groups.Include(g => g.Owner)
                .SingleOrDefaultAsync(g => g.GID == dto.Group.GID).ConfigureAwait(false);
            if (group == null) return false;
        }

        // Validate alias length and format if provided
        if (!string.IsNullOrWhiteSpace(dto.Alias))
        {
            var trimmedAlias = dto.Alias.Trim();
            if (trimmedAlias.Length < 3 || trimmedAlias.Length > 50) return false;

            // AnyAsync - SingleOrDefault would throw when legacy data already
            // holds the same alias on multiple shells.
            var aliasTaken = await DbContext.Groups.AsNoTracking()
                .AnyAsync(g => g.Alias == trimmedAlias && g.GID != dto.Group.GID).ConfigureAwait(false);
            if (aliasTaken) return false;

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
        if (!await HasCommunityRights(dto.Group.GID, SyncshellStaffPermission.ManageInvites).ConfigureAwait(false)) return new();

        var existingInvites = await DbContext.GroupTempInvites.Where(g => g.GroupGID == dto.Group.GID).ToListAsync().ConfigureAwait(false);

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
                GroupGID = dto.Group.GID,
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
        if (!hasRights || group == null)
        {
            _logger.LogCallWarning(SpheneHubLogger.Args(dto, "UnauthorizedOrNotFound"));
            return;
        }

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

        if (!await HasCommunityRights(dto.Group.GID, SyncshellStaffPermission.ManageBans).ConfigureAwait(false))
        {
            return new List<BannedGroupUserDto>();
        }

        var banEntries = await DbContext.GroupBans.Include(b => b.BannedUser).Where(g => g.GroupGID == dto.Group.GID).AsNoTracking().ToListAsync().ConfigureAwait(false);

        var bannerUids = banEntries.Select(b => b.BannedByUID).Distinct().ToList();
        var bannerAliases = await DbContext.Users.AsNoTracking()
            .Where(u => bannerUids.Contains(u.UID))
            .ToDictionaryAsync(u => u.UID, u => u.Alias, StringComparer.Ordinal)
            .ConfigureAwait(false);

        var group = await DbContext.Groups.AsNoTracking()
            .SingleAsync(g => g.GID == dto.Group.GID).ConfigureAwait(false);
        List<BannedGroupUserDto> bannedGroupUsers = banEntries.Select(b =>
            new BannedGroupUserDto(group.ToGroupData(), b.BannedUser.ToUserData(), b.BannedReason, b.BannedOn,
                bannerAliases.TryGetValue(b.BannedByUID, out var bannerAlias) && !string.IsNullOrEmpty(bannerAlias)
                    ? bannerAlias
                    : b.BannedByUID)).ToList();

        _logger.LogCallInfo(SpheneHubLogger.Args(dto, bannedGroupUsers.Count));

        return bannedGroupUsers;
    }

    [Authorize(Policy = "Identified")]
    public async Task<GroupJoinInfoDto> GroupJoin(GroupPasswordDto dto)
    {
        var aliasOrGid = dto.Group.GID.Trim();

        _logger.LogCallInfo(SpheneHubLogger.Args(dto));

        var group = await DbContext.Groups.Include(g => g.Owner).AsNoTracking().SingleOrDefaultAsync(g => g.GID == aliasOrGid || g.Alias == aliasOrGid).ConfigureAwait(false);
        if (group == null)
            return new GroupJoinInfoDto(null, null, GroupPermissions.NoneSet, false);

        var groupGid = group.GID;
        var effectivePolicy = EffectiveJoinPolicy(group);

        // Area-bound shells join exclusively through the area-bound join
        // request (consent + location flow) - the generic GID/password path
        // must not offer membership for them.
        if (await DbContext.AreaBoundSyncshells.AsNoTracking()
                .AnyAsync(a => a.GroupGID == groupGid).ConfigureAwait(false))
            return new GroupJoinInfoDto(null, null, GroupPermissions.NoneSet, false, effectivePolicy);
        var existingPair = await DbContext.GroupPairs.AsNoTracking().SingleOrDefaultAsync(g => g.GroupGID == groupGid && g.GroupUserUID == UserUID).ConfigureAwait(false);
        var hashedPw = StringUtils.Sha256String(dto.Password);
        var existingUserCount = await DbContext.GroupPairs.AsNoTracking().CountAsync(g => g.GroupGID == groupGid).ConfigureAwait(false);
        var joinedGroups = await DbContext.GroupPairs.CountAsync(g => g.GroupUserUID == UserUID).ConfigureAwait(false);
        var isBanned = await DbContext.GroupBans.AnyAsync(g => g.GroupGID == groupGid && g.BannedUserUID == UserUID).ConfigureAwait(false);
        var oneTimeInvite = await DbContext.GroupTempInvites.SingleOrDefaultAsync(g => g.GroupGID == groupGid && g.Invite == hashedPw).ConfigureAwait(false);

        if (existingPair != null
            || existingUserCount >= _maxGroupUserCount
            || joinedGroups >= _maxJoinedGroupsByUser
            || isBanned
            || !JoinCredentialSatisfied(effectivePolicy, group, hashedPw, oneTimeInvite))
            return new GroupJoinInfoDto(null, null, GroupPermissions.NoneSet, false, effectivePolicy);

        return new GroupJoinInfoDto(group.ToGroupData(), group.Owner.ToUserData(), group.ToEnum(), true, effectivePolicy);
    }

    /// <summary>
    /// The gate that decides whether a join credential is sufficient for the
    /// shell's effective join policy - shared by probe and finalize.
    /// </summary>
    private static bool JoinCredentialSatisfied(SyncshellJoinPolicy policy, Group group,
        string hashedPw, GroupTempInvite? oneTimeInvite) => policy switch
    {
        SyncshellJoinPolicy.Public => true,
        SyncshellJoinPolicy.PasswordProtected =>
            string.Equals(group.HashedPassword, hashedPw, StringComparison.Ordinal) || oneTimeInvite != null,
        SyncshellJoinPolicy.InviteOnly => oneTimeInvite != null,
        _ => false, // ApprovalRequired · Closed - never joinable directly
    };

    /// <summary>Legacy lock wins over the configured policy - locked shells stay Closed.</summary>
    private static SyncshellJoinPolicy EffectiveJoinPolicy(Group group)
        => group.InvitesEnabled ? group.JoinPolicy : SyncshellJoinPolicy.Closed;

    [Authorize(Policy = "Identified")]
    public async Task<bool> GroupJoinFinalize(GroupJoinDto dto)
    {
        var aliasOrGid = dto.Group.GID.Trim();

        _logger.LogCallInfo(SpheneHubLogger.Args(dto));

        var group = await DbContext.Groups.Include(g => g.Owner).AsNoTracking().SingleOrDefaultAsync(g => g.GID == aliasOrGid || g.Alias == aliasOrGid).ConfigureAwait(false);
        if (group == null) return false;

        var groupGid = group.GID;
        var effectivePolicy = EffectiveJoinPolicy(group);
        var existingPair = await DbContext.GroupPairs.AsNoTracking().SingleOrDefaultAsync(g => g.GroupGID == groupGid && g.GroupUserUID == UserUID).ConfigureAwait(false);
        var hashedPw = StringUtils.Sha256String(dto.Password);
        var existingUserCount = await DbContext.GroupPairs.AsNoTracking().CountAsync(g => g.GroupGID == groupGid).ConfigureAwait(false);
        var joinedGroups = await DbContext.GroupPairs.CountAsync(g => g.GroupUserUID == UserUID).ConfigureAwait(false);
        var isBanned = await DbContext.GroupBans.AnyAsync(g => g.GroupGID == groupGid && g.BannedUserUID == UserUID).ConfigureAwait(false);
        var oneTimeInvite = await DbContext.GroupTempInvites.SingleOrDefaultAsync(g => g.GroupGID == groupGid && g.Invite == hashedPw).ConfigureAwait(false);

        if (existingPair != null
            || existingUserCount >= _maxGroupUserCount
            || joinedGroups >= _maxJoinedGroupsByUser
            || isBanned
            || !JoinCredentialSatisfied(effectivePolicy, group, hashedPw, oneTimeInvite))
            return false;

        if (oneTimeInvite != null)
        {
            _logger.LogCallInfo(SpheneHubLogger.Args(aliasOrGid, "TempInvite", oneTimeInvite.Invite));
            DbContext.Remove(oneTimeInvite);
        }

        return await CompleteGroupJoinAsync(group, UserUID, UserCharaIdent,
            dto.GroupUserPreferredPermissions, aliasOrGid).ConfigureAwait(false);
    }

    /// <summary>
    /// Shared member-creation path - used by the self-service join finalize
    /// and by membership-application approval. Creates the pair + preferred
    /// permissions, syncs per-member permission rows and pushes the
    /// pair-joined/full-info notifications.
    /// </summary>
    private async Task<bool> CompleteGroupJoinAsync(Group group, string joinerUid, string? joinerCharaIdent,
        GroupUserPreferredPermissions joinPermissions, string aliasOrGid)
    {
        // Area-bound membership is created only by the area-bound join flow
        // (JoinUserToGroup after consent/location) - blocking here also stops
        // owner-approved applications from bypassing the location gate.
        if (await DbContext.AreaBoundSyncshells.AsNoTracking()
                .AnyAsync(a => a.GroupGID == group.GID).ConfigureAwait(false))
            return false;

        // get all pairs before we join
        var allUserPairs = (await GetAllPairInfo(joinerUid).ConfigureAwait(false));

        GroupPair newPair = new()
        {
            GroupGID = group.GID,
            GroupUserUID = joinerUid,
        };

        var preferredPermissions = await DbContext.GroupPairPreferredPermissions.SingleOrDefaultAsync(u => u.UserUID == joinerUid && u.GroupGID == group.GID).ConfigureAwait(false);
        if (preferredPermissions == null)
        {
            GroupPairPreferredPermission newPerms = new()
            {
                GroupGID = group.GID,
                UserUID = joinerUid,
                DisableSounds = joinPermissions.IsDisableSounds(),
                DisableVFX = joinPermissions.IsDisableVFX(),
                DisableAnimations = joinPermissions.IsDisableAnimations(),
                IsPaused = false
            };

            DbContext.Add(newPerms);
            preferredPermissions = newPerms;
        }
        else
        {
            preferredPermissions.DisableSounds = joinPermissions.IsDisableSounds();
            preferredPermissions.DisableVFX = joinPermissions.IsDisableVFX();
            preferredPermissions.DisableAnimations = joinPermissions.IsDisableAnimations();
            preferredPermissions.IsPaused = false;
            DbContext.Update(preferredPermissions);
        }

        await DbContext.GroupPairs.AddAsync(newPair).ConfigureAwait(false);

        _logger.LogCallInfo(SpheneHubLogger.Args(aliasOrGid, "Success"));

        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        var groupInfos = await DbContext.GroupPairs.Where(u => u.GroupGID == group.GID && (u.IsPinned || u.IsModerator)).ToListAsync().ConfigureAwait(false);
        await Clients.User(joinerUid).Client_GroupSendFullInfo(new GroupFullInfoDto(group.ToGroupData(), group.Owner.ToUserData(),
            group.ToEnum(), preferredPermissions.ToEnum(), newPair.ToEnum(),
            groupInfos.ToDictionary(u => u.GroupUserUID, u => u.ToEnum(), StringComparer.Ordinal))).ConfigureAwait(false);

        var self = DbContext.Users.Single(u => u.UID == joinerUid);

        var groupPairs = await DbContext.GroupPairs.Include(p => p.GroupUser)
            .Where(p => p.GroupGID == group.GID && p.GroupUserUID != joinerUid).ToListAsync().ConfigureAwait(false);

        var userPairsAfterJoin = await GetAllPairInfo(joinerUid).ConfigureAwait(false);

        foreach (var pair in groupPairs)
        {
            var perms = userPairsAfterJoin.TryGetValue(pair.GroupUserUID, out var userinfo);
            // check if we have had prior permissions to that pair, if not add them
            var ownPermissionsToOther = userinfo?.OwnPermissions ?? null;
            if (ownPermissionsToOther == null)
            {
                var existingPermissionsOnDb = await DbContext.Permissions.SingleOrDefaultAsync(p => p.UserUID == joinerUid && p.OtherUserUID == pair.GroupUserUID).ConfigureAwait(false);

                if (existingPermissionsOnDb == null)
                {
                    ownPermissionsToOther = new()
                    {
                        UserUID = joinerUid,
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
                ownPermissionsToOther = await DbContext.Permissions.SingleAsync(u => u.UserUID == joinerUid && u.OtherUserUID == pair.GroupUserUID).ConfigureAwait(false);

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
                var otherExistingPermsOnDb = await DbContext.Permissions.SingleOrDefaultAsync(p => p.UserUID == pair.GroupUserUID && p.OtherUserUID == joinerUid).ConfigureAwait(false);

                if (otherExistingPermsOnDb == null)
                {
                    var otherPreferred = await DbContext.GroupPairPreferredPermissions.SingleAsync(u => u.GroupGID == group.GID && u.UserUID == pair.GroupUserUID).ConfigureAwait(false);
                    otherExistingPermsOnDb = new()
                    {
                        UserUID = pair.GroupUserUID,
                        OtherUserUID = joinerUid,
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

            await Clients.User(joinerUid).Client_GroupPairJoined(new GroupPairFullInfoDto(group.ToGroupData(),
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
                    await Clients.User(joinerUid).Client_UserSendOnline(await CreateOnlineIdentDtoAsync(pair.ToUserData(), groupUserIdent).ConfigureAwait(false)).ConfigureAwait(false);
                    await Clients.User(pair.GroupUserUID).Client_UserSendOnline(await CreateOnlineIdentDtoAsync(self.ToUserData(), joinerCharaIdent).ConfigureAwait(false)).ConfigureAwait(false);
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

        // Kicking rides ManageMembers or ManageBans - banning removes too.
        if (!await HasCommunityRights(dto.Group.GID,
                SyncshellStaffPermission.ManageMembers | SyncshellStaffPermission.ManageBans).ConfigureAwait(false))
        {
            return;
        }

        var group = await DbContext.Groups.AsNoTracking()
            .SingleOrDefaultAsync(g => g.GID == dto.Group.GID).ConfigureAwait(false);
        if (group == null) return;

        var (userExists, groupPair) = await TryValidateUserInGroup(dto.Group.GID, dto.User.UID).ConfigureAwait(false);
        if (!userExists) return;

        if (groupPair.IsModerator || string.Equals(group.OwnerUID, dto.User.UID, StringComparison.Ordinal)) return;
        _logger.LogCallInfo(SpheneHubLogger.Args(dto, "Success"));

        DbContext.GroupPairs.Remove(groupPair);

        // A kick strips staff affiliation along with membership - keeping it
        // would leave a pair-less staff entry no one can remove.
        var staffRows = await DbContext.SyncshellCommunityStaffAssignments
            .Where(a => a.GroupGID == group.GID && a.UserUID == dto.User.UID)
            .ToListAsync().ConfigureAwait(false);
        DbContext.SyncshellCommunityStaffAssignments.RemoveRange(staffRows);

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

        if (staffRows.Count > 0)
        {
            await PushCommunityChangedAsync(group.GID,
                SyncshellCommunitySection.Staff, null).ConfigureAwait(false);
            await PushStaffAffiliationChangedAsync(group.GID, dto.User.UID).ConfigureAwait(false);
        }
    }

    [Authorize(Policy = "Identified")]
    public async Task GroupSetUserInfo(GroupPairUserInfoDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));

        var (userExists, userPair) = await TryValidateUserInGroup(dto.Group.GID, dto.User.UID).ConfigureAwait(false);
        if (!userExists) return;

        var (userIsOwner, _) = await TryValidateOwner(dto.Group.GID).ConfigureAwait(false);
        var canManageMembers = await HasCommunityRights(dto.Group.GID, SyncshellStaffPermission.ManageMembers).ConfigureAwait(false);

        if (dto.GroupUserInfo.HasFlag(GroupPairUserInfo.IsPinned) && canManageMembers && !userPair.IsPinned)
        {
            userPair.IsPinned = true;
        }
        else if (canManageMembers && userPair.IsPinned)
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
        var memberGids = groups.Select(g => g.GroupGID).Distinct(StringComparer.Ordinal).ToList();

        // Staff assignments are an administrative affiliation of their own - a
        // staff member keeps shell access even where no live pair row exists
        // (e.g. after leaving a bound area). Those shells join the member set.
        var callerStaffGids = await DbContext.SyncshellCommunityStaffAssignments.AsNoTracking()
            .Where(a => a.UserUID == UserUID).Select(a => a.GroupGID).Distinct()
            .ToListAsync().ConfigureAwait(false);
        var staffOnlyGids = callerStaffGids.Except(memberGids, StringComparer.Ordinal).ToList();
        var allGids = memberGids.Concat(staffOnlyGids).ToList();
        var callerStaffSet = callerStaffGids.ToHashSet(StringComparer.Ordinal);

        var (rosters, memberCounts) = await GetPrivilegedRostersAsync(allGids).ConfigureAwait(false);
        var rosterIdentities = await LoadRosterIdentitiesAsync(rosters).ConfigureAwait(false);

        var preferredPermissions = (await DbContext.GroupPairPreferredPermissions.Where(u => u.UserUID == UserUID).ToListAsync().ConfigureAwait(false))
            .Where(u => groups.Exists(k => string.Equals(k.GroupGID, u.GroupGID, StringComparison.Ordinal)))
            .ToDictionary(u => groups.First(f => string.Equals(f.GroupGID, u.GroupGID, StringComparison.Ordinal)), u => u);

        var result = preferredPermissions.Select(g =>
        {
            var gid = g.Key.GroupGID;
            var selfInfo = g.Key.ToEnum();
            selfInfo.SetStaff(callerStaffSet.Contains(gid));
            return new GroupFullInfoDto(g.Key.Group.ToGroupData(), g.Key.Group.Owner.ToUserData(),
                g.Key.Group.ToEnum(), g.Value.ToEnum(), selfInfo,
                rosters.TryGetValue(gid, out var roster) ? roster : new(StringComparer.Ordinal),
                memberCounts.GetValueOrDefault(gid),
                SliceIdentities(rosters.TryGetValue(gid, out var r) ? r : null, rosterIdentities));
        }).ToList();

        // Shells the caller administers without a live pair join the listing
        // via <see cref="GetStaffOnlyInfosAsync"/>.
        result.AddRange(await GetStaffOnlyInfosAsync(staffOnlyGids, rosters, memberCounts,
            rosterIdentities).ConfigureAwait(false));
        return result;
    }

    /// <summary>
    /// Full-info entries for shells the caller staffs without a live pair.
    /// NoneSet preferred permissions keep them out of every pairing and
    /// synchronization surface - the staff affiliation grants administrative
    /// access only, never area-independent appearance sync.
    /// </summary>
    private async Task<List<GroupFullInfoDto>> GetStaffOnlyInfosAsync(List<string> staffOnlyGids,
        Dictionary<string, Dictionary<string, GroupPairUserInfo>> rosters,
        Dictionary<string, int> memberCounts,
        Dictionary<string, UserData> rosterIdentities)
    {
        var result = new List<GroupFullInfoDto>(staffOnlyGids.Count);
        if (staffOnlyGids.Count == 0) return result;
        var staffOnlyGroups = await DbContext.Groups.Include(g => g.Owner).AsNoTracking()
            .Where(g => staffOnlyGids.Contains(g.GID)).ToListAsync().ConfigureAwait(false);
        foreach (var group in staffOnlyGroups)
        {
            result.Add(new GroupFullInfoDto(group.ToGroupData(), group.Owner.ToUserData(),
                group.ToEnum(), GroupUserPreferredPermissions.NoneSet, GroupPairUserInfo.IsStaff,
                rosters.TryGetValue(group.GID, out var roster) ? roster : new(StringComparer.Ordinal),
                memberCounts.GetValueOrDefault(group.GID),
                SliceIdentities(rosters.TryGetValue(group.GID, out var r) ? r : null,
                    rosterIdentities)));
        }
        return result;
    }

    /// <summary>Loads user identities for every uid present in
    /// <paramref name="rosters"/>.</summary>
    private async Task<Dictionary<string, UserData>> LoadRosterIdentitiesAsync(
        Dictionary<string, Dictionary<string, GroupPairUserInfo>> rosters)
    {
        var uids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var roster in rosters.Values)
        {
            foreach (var uid in roster.Keys) uids.Add(uid);
        }
        return await UserDataByUidAsync(uids).ConfigureAwait(false);
    }

    /// <summary>Slices the batch-loaded identity map down to one shell's
    /// roster; null when the roster is empty so old payloads stay small.</summary>
    private static Dictionary<string, UserData>? SliceIdentities(
        Dictionary<string, GroupPairUserInfo>? roster, Dictionary<string, UserData> all)
    {
        if (roster == null || roster.Count == 0) return null;
        var result = new Dictionary<string, UserData>(roster.Count, StringComparer.Ordinal);
        foreach (var uid in roster.Keys)
        {
            if (all.TryGetValue(uid, out var ud)) result[uid] = ud;
        }
        return result.Count == 0 ? null : result;
    }

    /// <summary>One batched identity lookup covering every uid the community
    /// aggregate references - staff assignments, authors and uploaders - so
    /// surfaces render names and never fall back to raw uids.</summary>
    private async Task<List<UserData>?> LoadMemberIdentitiesAsync(
        List<SyncshellCommunityStaffAssignment> assignments,
        List<SyncshellCommunityNewsPost> news,
        List<SyncshellCommunityEvent> events,
        List<SyncshellCommunityGalleryImage> gallery,
        Dictionary<string, UserData> organizerUsers)
    {
        var uids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in assignments) uids.Add(a.UserUID);
        foreach (var n in news)
        {
            if (!string.IsNullOrEmpty(n.AuthorUid)) uids.Add(n.AuthorUid);
            foreach (var uid in DeserializeJson<List<string>>(n.CoAuthorUidsJson) ?? [])
            {
                uids.Add(uid);
            }
        }
        foreach (var e in events)
        {
            if (!string.IsNullOrEmpty(e.AuthorUid)) uids.Add(e.AuthorUid);
        }
        foreach (var g in gallery)
        {
            if (!string.IsNullOrEmpty(g.UploadedByUid)) uids.Add(g.UploadedByUid);
        }
        if (uids.Count == 0 && organizerUsers.Count == 0) return null;
        var map = await UserDataByUidAsync(uids).ConfigureAwait(false);
        foreach (var kv in organizerUsers) map.TryAdd(kv.Key, kv.Value);
        return map.Values.ToList();
    }

    /// <summary>
    /// Per-shell privileged roster (moderator, pinned and staff-assigned
    /// users) plus authoritative member counts for <paramref name="gids"/>.
    /// The roster is viewer-independent - every caller sees the same staff
    /// set regardless of who currently holds a live pair inside a bound area.
    /// </summary>
    private async Task<(Dictionary<string, Dictionary<string, GroupPairUserInfo>> Rosters,
                        Dictionary<string, int> MemberCounts)> GetPrivilegedRostersAsync(List<string> gids)
    {
        var rosters = new Dictionary<string, Dictionary<string, GroupPairUserInfo>>(StringComparer.Ordinal);
        var memberCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        if (gids.Count == 0)
        {
            return (rosters, memberCounts);
        }

        var privilegedPairs = await DbContext.GroupPairs.AsNoTracking()
            .Where(p => gids.Contains(p.GroupGID) && (p.IsPinned || p.IsModerator))
            .ToListAsync().ConfigureAwait(false);
        var assignments = await DbContext.SyncshellCommunityStaffAssignments.AsNoTracking()
            .Where(a => gids.Contains(a.GroupGID))
            .Select(a => new { a.GroupGID, a.UserUID }).ToListAsync().ConfigureAwait(false);
        var staffUidsByGid = assignments.GroupBy(a => a.GroupGID, StringComparer.Ordinal)
            .ToDictionary(g => g.Key,
                g => g.Select(a => a.UserUID).ToHashSet(StringComparer.Ordinal),
                StringComparer.Ordinal);
        var assignedUids = assignments.Select(a => a.UserUID).ToHashSet(StringComparer.Ordinal);
        List<GroupPair> assignedPairs = assignedUids.Count == 0
            ? []
            : await DbContext.GroupPairs.AsNoTracking()
                .Where(p => gids.Contains(p.GroupGID) && assignedUids.Contains(p.GroupUserUID))
                .ToListAsync().ConfigureAwait(false);
        memberCounts = (await DbContext.GroupPairs.AsNoTracking()
            .Where(p => gids.Contains(p.GroupGID))
            .Select(p => p.GroupGID)
            .ToListAsync().ConfigureAwait(false))
            .GroupBy(g => g, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        foreach (var pair in privilegedPairs.Concat(assignedPairs))
        {
            var roster = rosters.TryGetValue(pair.GroupGID, out var existing)
                ? existing
                : rosters[pair.GroupGID] = new Dictionary<string, GroupPairUserInfo>(StringComparer.Ordinal);
            var flags = pair.ToEnum();
            flags.SetStaff(staffUidsByGid.TryGetValue(pair.GroupGID, out var uids)
                && uids.Contains(pair.GroupUserUID));
            roster[pair.GroupUserUID] = flags;
        }
        // Pair-less staff (e.g. left a bound area) stay in the privileged
        // roster - the assignment is the authoritative staff record, not the
        // pair row.
        foreach (var (gid, uids) in staffUidsByGid)
        {
            var roster = rosters.TryGetValue(gid, out var existing)
                ? existing
                : rosters[gid] = new Dictionary<string, GroupPairUserInfo>(StringComparer.Ordinal);
            foreach (var uid in uids)
            {
                roster.TryAdd(uid, GroupPairUserInfo.IsStaff);
            }
        }
        return (rosters, memberCounts);
    }

    [Authorize(Policy = "Identified")]
    public async Task GroupUnbanUser(GroupPairDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));

        if (!await HasCommunityRights(dto.Group.GID, SyncshellStaffPermission.ManageBans).ConfigureAwait(false)) return;

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
                IsLocked = !binding.Group.InvitesEnabled,
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
        if (!binding.Group.InvitesEnabled) return false;

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
            if (!group.InvitesEnabled)
            {
                _logger.LogCallInfo(SpheneHubLogger.Args("Join blocked: group is locked", dto.Group.GID, dto.User.UID));
                return;
            }
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
                    await Clients.User(userUID).Client_UserSendOnline(await CreateOnlineIdentDtoAsync(pair.ToUserData(), groupUserIdent).ConfigureAwait(false)).ConfigureAwait(false);
                    await Clients.User(pair.GroupUserUID).Client_UserSendOnline(await CreateOnlineIdentDtoAsync(newUser.ToUserData(), newUserIdent).ConfigureAwait(false)).ConfigureAwait(false);
                }
            }
        }

        await DbContext.SaveChangesAsync().ConfigureAwait(false);
    }

    [Authorize(Policy = "Identified")]
    public async Task BroadcastAreaBoundSyncshells(LocationInfo userLocation)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(userLocation));

        // Find all area-bound syncshells that have locations matching the user's location
        var areaBoundSyncshells = await DbContext.AreaBoundSyncshells
            .Include(a => a.Group)
            .Include(a => a.Locations)
            .Where(a => a.AutoBroadcastEnabled)
            .ToListAsync()
            .ConfigureAwait(false);

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
            )
            {
                IsLocked = !syncshell.Group.InvitesEnabled
            };

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
        await PushCommunityChangedAsync(dto.Group.GID, SyncshellCommunitySection.About).ConfigureAwait(false);
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
        await PushCommunityChangedAsync(dto.Group.GID, SyncshellCommunitySection.About).ConfigureAwait(false);
        return true;
    }

    [Authorize(Policy = "Identified")]
    public async Task<SyncshellProfileDto?> GroupGetSyncshellProfile(GroupDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));

        // Validate user is a member of the group
        var groupPair = await DbContext.GroupPairs
            .SingleOrDefaultAsync(gp => gp.GroupGID == dto.Group.GID && gp.GroupUserUID == UserUID)
            .ConfigureAwait(false);

        if (groupPair == null) return null;

        var welcomePage = await DbContext.SyncshellWelcomePages
            .SingleOrDefaultAsync(w => w.GroupGID == dto.Group.GID)
            .ConfigureAwait(false);

        // Unlike GroupGetWelcomePage the profile stays readable while the
        // join popup is disabled - IsEnabled only gates the popup itself.
        if (welcomePage == null) return null;

        return new SyncshellProfileDto(
            welcomePage.GroupGID,
            welcomePage.Tagline,
            welcomePage.WelcomeText,
            welcomePage.WelcomeImageBase64,
            welcomePage.ImageFileName,
            welcomePage.ImageContentType,
            welcomePage.ImageSize,
            welcomePage.AvatarImageBase64,
            welcomePage.AvatarFileName,
            welcomePage.AvatarContentType,
            welcomePage.AvatarSize,
            welcomePage.AccentColor,
            DeserializeSections(welcomePage.SectionsJson),
            welcomePage.IsEnabled,
            welcomePage.ShowOnJoin,
            welcomePage.ShowOnAreaBoundJoin,
            welcomePage.CreatedAt,
            welcomePage.UpdatedAt
        );
    }

    [Authorize(Policy = "Identified")]
    public async Task<bool> GroupSetSyncshellProfile(SyncshellProfileUpdateDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));

        // Owner/mod or a staff role carrying ManagePage
        var hasRights = await HasCommunityRights(dto.Group.GID, SyncshellStaffPermission.ManagePage).ConfigureAwait(false);
        if (!hasRights) return false;

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

        welcomePage.WelcomeText = dto.Description;
        // Hash-based media writes supersede base64 - the payload already lives
        // on the file server; the row records it for later GC.
        if (dto.BannerMediaHash != null)
        {
            welcomePage.WelcomeMediaHash = dto.BannerMediaHash;
            welcomePage.WelcomeImageBase64 = null;
            await _communityMedia.RecordMediaAsync(DbContext, dto.BannerMediaHash,
                UserUID, dto.BannerContentType, dto.BannerSize).ConfigureAwait(false);
        }
        else
        {
            welcomePage.WelcomeImageBase64 = dto.BannerImageBase64;
            // Explicit "no banner" intent or a base64 write both supersede a
            // stored hash - a bare null hash alone proves nothing (older
            // clients simply don't model the field).
            if (dto.ClearBanner || dto.BannerImageBase64 != null)
            {
                welcomePage.WelcomeMediaHash = null;
            }
        }
        welcomePage.ImageFileName = dto.BannerFileName;
        welcomePage.ImageContentType = dto.BannerContentType;
        welcomePage.ImageSize = dto.BannerSize;
        welcomePage.Tagline = dto.Tagline;
        welcomePage.AccentColor = dto.AccentColor;
        if (dto.AvatarMediaHash != null)
        {
            welcomePage.AvatarMediaHash = dto.AvatarMediaHash;
            welcomePage.AvatarImageBase64 = null;
            await _communityMedia.RecordMediaAsync(DbContext, dto.AvatarMediaHash,
                UserUID, dto.AvatarContentType, dto.AvatarSize).ConfigureAwait(false);
        }
        else
        {
            welcomePage.AvatarImageBase64 = dto.AvatarImageBase64;
            if (dto.ClearAvatar || dto.AvatarImageBase64 != null)
            {
                welcomePage.AvatarMediaHash = null;
            }
        }
        welcomePage.AvatarFileName = dto.AvatarFileName;
        welcomePage.AvatarContentType = dto.AvatarContentType;
        welcomePage.AvatarSize = dto.AvatarSize;
        welcomePage.SectionsJson = dto.Sections is { Count: > 0 }
            ? JsonSerializer.Serialize(dto.Sections)
            : null;
        welcomePage.IsEnabled = dto.IsEnabled;
        welcomePage.ShowOnJoin = dto.ShowOnJoin;
        welcomePage.ShowOnAreaBoundJoin = dto.ShowOnAreaBoundJoin;
        welcomePage.UpdatedAt = DateTime.UtcNow;

        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        _logger.LogDebug("Updated syncshell profile for group {GroupId}", dto.Group.GID);
        await PushCommunityChangedAsync(dto.Group.GID, SyncshellCommunitySection.About).ConfigureAwait(false);
        return true;
    }

    [Authorize(Policy = "Identified")]
    public async Task<SyncshellCommunityDto?> GroupGetCommunity(GroupDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var groupPair = await DbContext.GroupPairs
            .AsNoTracking()
            .SingleOrDefaultAsync(gp => gp.GroupGID == dto.Group.GID && gp.GroupUserUID == UserUID)
            .ConfigureAwait(false);

        // the shell's display name rides the DTO for viewers who never see a
        // GroupFullInfoDto - non-members and area-bound previews
        var groupAlias = await DbContext.Groups.AsNoTracking()
            .Where(g => g.GID == dto.Group.GID)
            .Select(g => g.Alias)
            .FirstOrDefaultAsync().ConfigureAwait(false);

        // Authoritative member count - identical for every viewer, regardless
        // of which pair/presence collections their client happens to hold.
        var memberCount = await DbContext.GroupPairs.AsNoTracking()
            .CountAsync(p => p.GroupGID == dto.Group.GID).ConfigureAwait(false);

        // Assigned staff keep member-level read even without a live pair row
        // (e.g. after leaving a bound area) - the assignment is the
        // administrative membership, not the synchronization pair.
        var isStaffAssigned = groupPair == null
            && await DbContext.SyncshellCommunityStaffAssignments.AsNoTracking()
                .AnyAsync(a => a.GroupGID == dto.Group.GID && a.UserUID == UserUID)
                .ConfigureAwait(false);

        // Non-members get a trimmed public preview - profile, links and rules
        // only - and only when the shell is discoverable: area-bound or
        // publicly listed in the directory.
        var callerFavorited = await DbContext.SyncshellFavorites.AsNoTracking()
            .AnyAsync(f => f.GroupGID == dto.Group.GID && f.UserUID == UserUID)
            .ConfigureAwait(false);

        if (groupPair == null && !isStaffAssigned)
        {
            var previewPage = await DbContext.SyncshellWelcomePages.AsNoTracking()
                .SingleOrDefaultAsync(w => w.GroupGID == dto.Group.GID).ConfigureAwait(false);
            var isAreaBound = await DbContext.AreaBoundSyncshells.AsNoTracking()
                .AnyAsync(a => a.GroupGID == dto.Group.GID).ConfigureAwait(false);
            if (!isAreaBound && !(previewPage?.IsListedPublicly ?? false))
            {
                return null;
            }
            var previewProfile = previewPage == null
                ? new SyncshellProfileDto(dto.Group.GID, null, null, null, null, null, null,
                    null, null, null, null, null, null, false, false, false,
                    DateTime.UnixEpoch, DateTime.UnixEpoch)
                : MapProfile(previewPage);
            var previewDestination = previewPage?.DestinationPublished == true
                ? DeserializeDestinationJson(previewPage.DestinationJson)
                : null;
            // Followers get the live in-area roster on area-bound shells -
            // name/avatar only, never appearance data.
            var previewPresent = isAreaBound && callerFavorited
                ? await PresentMemberIdentitiesAsync(dto.Group.GID).ConfigureAwait(false)
                : null;
            // Promotion needs visible events - the preview ships exactly the
            // same slice GroupGetPublicCommunity serves: published + flagged
            // Public. Members-only events never leak to outsiders.
            var previewEvents = await DbContext.SyncshellCommunityEvents.AsNoTracking()
                .Where(e => e.GroupGID == dto.Group.GID
                    && e.PublishStatus == (int)ContentPublishStatus.Published
                    && e.Visibility == (int)EventVisibility.Public)
                .OrderBy(e => e.StartUtc)
                .ToListAsync().ConfigureAwait(false);
            var previewOrganizers = await UserDataByUidAsync(previewEvents
                .SelectMany(e => DeserializeJson<List<string>>(e.OrganizerUidsJson) ?? []))
                .ConfigureAwait(false);
            return new SyncshellCommunityDto(previewProfile, previewPage?.RulesText,
                DeserializeJson<List<SyncshellCommunityLinkDto>>(previewPage?.LinksJson),
                DeserializeJson<List<SyncshellCommunitySection>>(previewPage?.VisibleSectionsJson),
                previewPage?.IsListedPublicly ?? false,
                previewEvents.Select(e => MapEvent(e, users: previewOrganizers)).ToList(),
                null, null, null, null, null, null, groupAlias,
                CommunityDestination: previewDestination,
                CommunityDestinationPublished: previewDestination != null,
                MemberCount: memberCount,
                CallerFavorited: callerFavorited,
                IsAreaBound: isAreaBound,
                PresentMembers: previewPresent,
                FollowerCount: await DbContext.SyncshellFavorites.AsNoTracking()
                    .CountAsync(f => f.GroupGID == dto.Group.GID).ConfigureAwait(false));
        }

        var welcomePage = await DbContext.SyncshellWelcomePages
            .AsNoTracking()
            .SingleOrDefaultAsync(w => w.GroupGID == dto.Group.GID)
            .ConfigureAwait(false);

        // Event visibility: staff and authors see every row; members see
        // published events only (draft/pending stay on the staff surface).
        var canManageEvents = await HasCommunityRights(dto.Group.GID, SyncshellStaffPermission.ManageEvents).ConfigureAwait(false);
        var events = await DbContext.SyncshellCommunityEvents
            .AsNoTracking()
            .Where(e => e.GroupGID == dto.Group.GID && (canManageEvents
                || e.AuthorUid == UserUID
                || e.PublishStatus == (int)ContentPublishStatus.Published))
            .OrderBy(e => e.StartUtc)
            .ToListAsync()
            .ConfigureAwait(false);
        var eventIds = events.Select(e => e.Id).ToList();
        var viewerRsvps = await DbContext.CommunityEventParticipants.AsNoTracking()
            .Where(p => eventIds.Contains(p.EventId) && p.UserUID == UserUID)
            .ToDictionaryAsync(p => p.EventId, p => p.Status)
            .ConfigureAwait(false);

        // Non-staff see visible content only - published or due-scheduled; an
        // author always keeps sight of their own draft/pending posts.
        var canManageNews = await HasCommunityRights(dto.Group.GID, SyncshellStaffPermission.ManageNews).ConfigureAwait(false);
        var nowUtc = DateTime.UtcNow;
        var news = await DbContext.SyncshellCommunityNews
            .AsNoTracking()
            .Where(n => n.GroupGID == dto.Group.GID && (canManageNews || n.AuthorUid == UserUID
                || n.Status == (int)ContentPublishStatus.Published
                || (n.Status == (int)ContentPublishStatus.Scheduled && n.ScheduledForUtc <= nowUtc)))
            .OrderByDescending(n => n.IsPinned)
            .ThenByDescending(n => n.PublishedAt)
            .ToListAsync()
            .ConfigureAwait(false);

        var gallery = await DbContext.SyncshellCommunityGallery
            .AsNoTracking()
            .Where(g => g.GroupGID == dto.Group.GID)
            .OrderBy(g => g.SortOrder)
            .ThenBy(g => g.Id)
            .ToListAsync()
            .ConfigureAwait(false);

        var albums = await DbContext.SyncshellCommunityGalleryAlbums
            .AsNoTracking()
            .Where(a => a.GroupGID == dto.Group.GID)
            .OrderBy(a => a.SortOrder)
            .ThenBy(a => a.Id)
            .ToListAsync()
            .ConfigureAwait(false);

        // Per-viewer filtering: pending photos and restricted audiences stay
        // on the staff side - members see published items addressed to them,
        // uploaders always keep sight of their own submissions.
        var isStaff = await IsCommunityStaff(dto.Group.GID).ConfigureAwait(false);
        gallery = gallery.Where(g =>
                (g.Status == 0 || isStaff
                    || string.Equals(g.UploadedByUid, UserUID, StringComparison.Ordinal))
                && GalleryAudienceVisible(g.Audience, g.AllowedUidsJson, isStaff, g.UploadedByUid))
            .ToList();
        albums = albums.Where(a =>
                GalleryAudienceVisible(a.Audience, a.AllowedUidsJson, isStaff, null))
            .ToList();

        var staffRoles = await DbContext.SyncshellCommunityStaffRoles
            .AsNoTracking()
            .Where(r => r.GroupGID == dto.Group.GID)
            .OrderBy(r => r.SortOrder)
            .ThenBy(r => r.Id)
            .ToListAsync()
            .ConfigureAwait(false);

        var staffAssignments = await DbContext.SyncshellCommunityStaffAssignments
            .AsNoTracking()
            .Where(a => a.GroupGID == dto.Group.GID)
            .ToListAsync()
            .ConfigureAwait(false);

        var likeData = await GetGalleryLikeData(gallery.Select(g => g.Id).ToList())
            .ConfigureAwait(false);

        var profile = welcomePage == null
            ? new SyncshellProfileDto(dto.Group.GID, null, null, null, null, null, null,
                null, null, null, null, null, null, false, false, false,
                DateTime.UnixEpoch, DateTime.UnixEpoch)
            : MapProfile(welcomePage);

        // Community location: members see only the published snapshot; staff
        // with page rights additionally see the unpublished draft so the
        // settings editor can roundtrip it.
        var canManagePage = await HasCommunityRights(dto.Group.GID,
            SyncshellStaffPermission.ManagePage).ConfigureAwait(false);
        var storedDestination = DeserializeDestinationJson(welcomePage?.DestinationJson);
        var destinationPublished = welcomePage?.DestinationPublished == true
            && storedDestination != null;
        var communityDestination = canManagePage || destinationPublished
            ? storedDestination : null;

        // One batched user lookup covers every event's organizer identities -
        // attendees/organizers render as names, never raw UIDs.
        var organizerUsers = await UserDataByUidAsync(events
            .SelectMany(e => DeserializeJson<List<string>>(e.OrganizerUidsJson) ?? []))
            .ConfigureAwait(false);
        var memberIdentities = await LoadMemberIdentitiesAsync(staffAssignments, news, events,
            gallery, organizerUsers).ConfigureAwait(false);

        var isAreaBoundShell = await DbContext.AreaBoundSyncshells.AsNoTracking()
            .AnyAsync(a => a.GroupGID == dto.Group.GID).ConfigureAwait(false);
        var presentMembers = isAreaBoundShell
            ? await PresentMemberIdentitiesAsync(dto.Group.GID).ConfigureAwait(false)
            : null;

        _logger.LogDebug("GroupGetCommunity {GID} · {Ms}ms · events={E} news={N} gallery={G} albums={A} staff={R}/{S}",
            dto.Group.GID, sw.ElapsedMilliseconds, events.Count, news.Count, gallery.Count,
            albums.Count, staffRoles.Count, staffAssignments.Count);
        return new SyncshellCommunityDto(
            profile,
            welcomePage?.RulesText,
            DeserializeJson<List<SyncshellCommunityLinkDto>>(welcomePage?.LinksJson),
            DeserializeJson<List<SyncshellCommunitySection>>(welcomePage?.VisibleSectionsJson),
            welcomePage?.IsListedPublicly ?? false,
            events.Select(e => MapEvent(e,
                viewerRsvps.TryGetValue(e.Id, out var vs) ? vs : (int?)null,
                organizerUsers)).ToList(),
            news.Select(MapNews).ToList(),
            gallery.Select(g => MapGallery(g,
                likeData.Counts.GetValueOrDefault(g.Id), likeData.Mine.Contains(g.Id))).ToList(),
            albums.Select(MapAlbum).ToList(),
            staffRoles.Select(MapStaffRole).ToList(),
            staffAssignments.Select(MapStaffAssignment).ToList(),
            DeserializeJson<List<string>>(welcomePage?.OverviewLayoutJson),
            groupAlias,
            CommunityDestination: communityDestination,
            CommunityDestinationPublished: destinationPublished,
            MemberCount: memberCount,
            MemberIdentities: memberIdentities,
            CallerFavorited: callerFavorited,
            IsAreaBound: isAreaBoundShell,
            PresentMembers: presentMembers,
            FollowerCount: await DbContext.SyncshellFavorites.AsNoTracking()
                .CountAsync(f => f.GroupGID == dto.Group.GID).ConfigureAwait(false)
        );
    }

    /// <summary>
    /// Profile-only community read · identity, accent and media references for
    /// list/card surfaces. Same access gate as <see cref="GroupGetCommunity"/>
    /// (member, area-bound or publicly listed) without the content aggregates.
    /// </summary>
    public async Task<SyncshellProfileDto?> GroupGetCommunityProfile(GroupDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));

        var isMember = await DbContext.GroupPairs.AsNoTracking()
            .AnyAsync(gp => gp.GroupGID == dto.Group.GID && gp.GroupUserUID == UserUID)
            .ConfigureAwait(false);
        var welcomePage = await DbContext.SyncshellWelcomePages.AsNoTracking()
            .SingleOrDefaultAsync(w => w.GroupGID == dto.Group.GID)
            .ConfigureAwait(false);

        if (!isMember)
        {
            var isAreaBound = await DbContext.AreaBoundSyncshells.AsNoTracking()
                .AnyAsync(a => a.GroupGID == dto.Group.GID).ConfigureAwait(false);
            // Assigned staff keep profile access without a live pair - same
            // administrative-membership rule as GroupGetCommunity.
            var isStaffAssigned = await DbContext.SyncshellCommunityStaffAssignments.AsNoTracking()
                .AnyAsync(a => a.GroupGID == dto.Group.GID && a.UserUID == UserUID)
                .ConfigureAwait(false);
            if (!isAreaBound && !isStaffAssigned && !(welcomePage?.IsListedPublicly ?? false))
            {
                return null;
            }
        }

        return welcomePage == null ? null : MapProfile(welcomePage);
    }

    /// <summary>
    /// Publishes, updates or unpublishes the shell's permanent community
    /// location. Kept separate from <see cref="GroupSetCommunity"/> so
    /// profile edits can't accidentally reset it. <paramref name="dto"/>
    /// semantics: Destination null = keep the stored snapshot, Kind.None =
    /// clear it (and unpublish), a valid housing address = replace it.
    /// </summary>
    [Authorize(Policy = "Identified")]
    public async Task<bool> GroupSetCommunityDestination(SyncshellCommunityDestinationUpdateDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));

        var hasRights = await HasCommunityRights(dto.Group.GID,
            SyncshellStaffPermission.ManagePage).ConfigureAwait(false);
        if (!hasRights) return false;

        var welcomePage = await DbContext.SyncshellWelcomePages
            .SingleOrDefaultAsync(w => w.GroupGID == dto.Group.GID).ConfigureAwait(false);
        if (welcomePage == null)
        {
            welcomePage = new SyncshellWelcomePage
            {
                GroupGID = dto.Group.GID,
                CreatedAt = DateTime.UtcNow
            };
            await DbContext.SyncshellWelcomePages.AddAsync(welcomePage).ConfigureAwait(false);
        }

        if (dto.Destination != null)
        {
            if ((EventDestinationKind)dto.Destination.Kind == EventDestinationKind.None)
            {
                welcomePage.DestinationJson = null;
            }
            else
            {
                var sanitized = EventDestinationValidator.Sanitize(dto.Destination);
                // Community locations are housing addresses only - public
                // locations aren't valid here.
                if (sanitized == null
                    || (EventDestinationKind)sanitized.Kind != EventDestinationKind.HousingAddress)
                {
                    return false;
                }
                welcomePage.DestinationJson = SerializeDestinationJson(sanitized);
            }
        }

        var stored = DeserializeDestinationJson(welcomePage.DestinationJson);
        welcomePage.DestinationPublished = dto.Publish && stored != null;
        welcomePage.UpdatedAt = DateTime.UtcNow;
        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        _logger.LogDebug("Updated community destination for group {GroupId} (published={Published})",
            dto.Group.GID, welcomePage.DestinationPublished);
        await PushCommunityChangedAsync(dto.Group.GID).ConfigureAwait(false);
        return true;
    }

    [Authorize(Policy = "Identified")]
    public async Task<bool> GroupSetCommunity(SyncshellCommunityUpdateDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));

        var hasRights = await HasCommunityRights(dto.Group.GID, SyncshellStaffPermission.ManagePage).ConfigureAwait(false);
        if (!hasRights) return false;

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

        welcomePage.WelcomeText = dto.Description;
        // Hash-based media writes supersede base64 - the payload already lives
        // on the file server; the row records it for later GC.
        if (dto.BannerMediaHash != null)
        {
            welcomePage.WelcomeMediaHash = dto.BannerMediaHash;
            welcomePage.WelcomeImageBase64 = null;
            await _communityMedia.RecordMediaAsync(DbContext, dto.BannerMediaHash,
                UserUID, dto.BannerContentType, dto.BannerSize).ConfigureAwait(false);
        }
        else
        {
            welcomePage.WelcomeImageBase64 = dto.BannerImageBase64;
            // Explicit "no banner" intent or a base64 write both supersede a
            // stored hash - a bare null hash alone proves nothing (older
            // clients simply don't model the field).
            if (dto.ClearBanner || dto.BannerImageBase64 != null)
            {
                welcomePage.WelcomeMediaHash = null;
            }
        }
        welcomePage.ImageFileName = dto.BannerFileName;
        welcomePage.ImageContentType = dto.BannerContentType;
        welcomePage.ImageSize = dto.BannerSize;
        welcomePage.Tagline = dto.Tagline;
        welcomePage.AccentColor = dto.AccentColor;
        if (dto.AvatarMediaHash != null)
        {
            welcomePage.AvatarMediaHash = dto.AvatarMediaHash;
            welcomePage.AvatarImageBase64 = null;
            await _communityMedia.RecordMediaAsync(DbContext, dto.AvatarMediaHash,
                UserUID, dto.AvatarContentType, dto.AvatarSize).ConfigureAwait(false);
        }
        else
        {
            welcomePage.AvatarImageBase64 = dto.AvatarImageBase64;
            if (dto.ClearAvatar || dto.AvatarImageBase64 != null)
            {
                welcomePage.AvatarMediaHash = null;
            }
        }
        welcomePage.AvatarFileName = dto.AvatarFileName;
        welcomePage.AvatarContentType = dto.AvatarContentType;
        welcomePage.AvatarSize = dto.AvatarSize;
        welcomePage.SectionsJson = SerializeJson(dto.Sections);
        welcomePage.RulesText = dto.RulesText;
        welcomePage.LinksJson = SerializeJson(dto.Links);
        welcomePage.VisibleSectionsJson = SerializeJson(dto.VisibleSections);
        welcomePage.IsListedPublicly = dto.IsListedPublicly;
        welcomePage.OverviewLayoutJson = dto.OverviewWidgets is { Count: > 0 }
            ? SerializeJson(dto.OverviewWidgets)
            : null;
        welcomePage.IsEnabled = dto.IsEnabled;
        welcomePage.ShowOnJoin = dto.ShowOnJoin;
        welcomePage.ShowOnAreaBoundJoin = dto.ShowOnAreaBoundJoin;
        welcomePage.UpdatedAt = DateTime.UtcNow;

        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        _logger.LogDebug("Updated syncshell community for group {GroupId}", dto.Group.GID);
        await PushCommunityChangedAsync(dto.Group.GID).ConfigureAwait(false);
        return true;
    }

    [Authorize(Policy = "Identified")]
    public async Task<List<SyncshellDirectoryEntryDto>> GroupGetCommunityDirectory()
    {
        _logger.LogCallInfo();

        var listed = await DbContext.SyncshellWelcomePages
            .Where(w => w.IsListedPublicly && w.IsEnabled)
            .OrderByDescending(w => w.UpdatedAt)
            .ToListAsync()
            .ConfigureAwait(false);

        var gids = listed.Select(w => w.GroupGID).ToList();
        var memberCounts = await DbContext.GroupPairs
            .Where(gp => gids.Contains(gp.GroupGID))
            .GroupBy(gp => gp.GroupGID)
            .Select(g => new { Gid = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Gid, g => g.Count)
            .ConfigureAwait(false);
        // Alias + join policy live on the group row - one lookup per listing
        // would N+1, so load the groups once. The effective policy resolves
        // through the same resolver the real join path uses.
        var groups = await DbContext.Groups
            .Where(g => gids.Contains(g.GID))
            .ToDictionaryAsync(g => g.GID)
            .ConfigureAwait(false);
        var memberGids = (await DbContext.GroupPairs.AsNoTracking()
            .Where(gp => gp.GroupUserUID == UserUID)
            .Select(gp => gp.GroupGID).ToListAsync().ConfigureAwait(false))
            .ToHashSet(StringComparer.Ordinal);
        var favoriteGids = (await DbContext.SyncshellFavorites.AsNoTracking()
            .Where(f => f.UserUID == UserUID)
            .Select(f => f.GroupGID).ToListAsync().ConfigureAwait(false))
            .ToHashSet(StringComparer.Ordinal);
        var areaBoundGids = (await DbContext.AreaBoundSyncshells.AsNoTracking()
            .Where(a => gids.Contains(a.GroupGID))
            .Select(a => a.GroupGID).ToListAsync().ConfigureAwait(false))
            .ToHashSet(StringComparer.Ordinal);

        return listed.Select(w =>
        {
            groups.TryGetValue(w.GroupGID, out var group);
            return new SyncshellDirectoryEntryDto(
                w.GroupGID,
                string.IsNullOrWhiteSpace(group?.Alias) ? null : group!.Alias,
                w.Tagline,
                w.AccentColor,
                w.AvatarImageBase64,
                w.WelcomeImageBase64,
                memberCounts.GetValueOrDefault(w.GroupGID),
                w.UpdatedAt,
                group != null ? EffectiveJoinPolicy(group) : SyncshellJoinPolicy.Closed,
                memberGids.Contains(w.GroupGID),
                w.WelcomeMediaHash,
                w.AvatarMediaHash,
                areaBoundGids.Contains(w.GroupGID),
                favoriteGids.Contains(w.GroupGID));
        }).ToList();
    }

    /// <summary>Bookmark/unbookmark a discoverable shell for the caller.
    /// Favorites are private viewer state - nothing pushes and the shell
    /// never learns who follows it. The shell must be viewable without
    /// membership (publicly listed or area-bound), otherwise there would be
    /// nothing to follow.</summary>
    [Authorize(Policy = "Identified")]
    public async Task<bool> GroupSetSyncshellFavorite(GroupDto dto, bool favorite)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));

        var viewable = await DbContext.SyncshellWelcomePages.AsNoTracking()
            .AnyAsync(w => w.GroupGID == dto.Group.GID && w.IsListedPublicly && w.IsEnabled)
            .ConfigureAwait(false)
            || await DbContext.AreaBoundSyncshells.AsNoTracking()
                .AnyAsync(a => a.GroupGID == dto.Group.GID).ConfigureAwait(false);
        if (!viewable) return false;

        var existing = await DbContext.SyncshellFavorites
            .SingleOrDefaultAsync(f => f.GroupGID == dto.Group.GID
                && f.UserUID == UserUID).ConfigureAwait(false);
        if (favorite && existing == null)
        {
            await DbContext.SyncshellFavorites.AddAsync(new SyncshellFavorite
            {
                GroupGID = dto.Group.GID,
                UserUID = UserUID,
                CreatedAtUtc = DateTime.UtcNow
            }).ConfigureAwait(false);
            await DbContext.SaveChangesAsync().ConfigureAwait(false);
        }
        else if (!favorite && existing != null)
        {
            DbContext.SyncshellFavorites.Remove(existing);
            await DbContext.SaveChangesAsync().ConfigureAwait(false);
        }
        return true;
    }

    /// <summary>The caller's bookmarked shells as directory-shaped rows -
    /// powers the Followed section so the pages stay reachable without
    /// membership.</summary>
    [Authorize(Policy = "Identified")]
    public async Task<List<SyncshellDirectoryEntryDto>> GroupGetFavorites()
    {
        _logger.LogCallInfo();

        var favorites = await DbContext.SyncshellFavorites.AsNoTracking()
            .Where(f => f.UserUID == UserUID)
            .OrderByDescending(f => f.CreatedAtUtc)
            .ToListAsync().ConfigureAwait(false);
        if (favorites.Count == 0) return [];

        var gids = favorites.Select(f => f.GroupGID).ToList();
        var pages = await DbContext.SyncshellWelcomePages.AsNoTracking()
            .Where(w => gids.Contains(w.GroupGID))
            .ToDictionaryAsync(w => w.GroupGID).ConfigureAwait(false);
        var groups = await DbContext.Groups.AsNoTracking()
            .Where(g => gids.Contains(g.GID))
            .ToDictionaryAsync(g => g.GID).ConfigureAwait(false);
        var memberCounts = await DbContext.GroupPairs.AsNoTracking()
            .Where(gp => gids.Contains(gp.GroupGID))
            .GroupBy(gp => gp.GroupGID)
            .Select(g => new { Gid = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Gid, g => g.Count).ConfigureAwait(false);
        var areaBoundGids = (await DbContext.AreaBoundSyncshells.AsNoTracking()
            .Where(a => gids.Contains(a.GroupGID))
            .Select(a => a.GroupGID).ToListAsync().ConfigureAwait(false))
            .ToHashSet(StringComparer.Ordinal);
        var memberGids = (await DbContext.GroupPairs.AsNoTracking()
            .Where(gp => gp.GroupUserUID == UserUID && gids.Contains(gp.GroupGID))
            .Select(gp => gp.GroupGID).ToListAsync().ConfigureAwait(false))
            .ToHashSet(StringComparer.Ordinal);

        return favorites.Select(f =>
        {
            pages.TryGetValue(f.GroupGID, out var w);
            groups.TryGetValue(f.GroupGID, out var group);
            return new SyncshellDirectoryEntryDto(
                f.GroupGID,
                string.IsNullOrWhiteSpace(group?.Alias) ? null : group!.Alias,
                w?.Tagline,
                w?.AccentColor,
                w?.AvatarImageBase64,
                w?.WelcomeImageBase64,
                memberCounts.GetValueOrDefault(f.GroupGID),
                w?.UpdatedAt ?? f.CreatedAtUtc,
                group != null ? EffectiveJoinPolicy(group) : SyncshellJoinPolicy.Closed,
                memberGids.Contains(f.GroupGID),
                w?.WelcomeMediaHash,
                w?.AvatarMediaHash,
                areaBoundGids.Contains(f.GroupGID),
                IsFavorite: true);
        }).ToList();
    }

    /// <summary>Identities of everyone currently inside an area-bound shell -
    /// live GroupPair rows. Capped so a crowded venue can't balloon the DTO;
    /// avatar/name only, never appearance data.</summary>
    private async Task<List<UserData>?> PresentMemberIdentitiesAsync(string gid)
    {
        var uids = await DbContext.GroupPairs.AsNoTracking()
            .Where(p => p.GroupGID == gid)
            .OrderBy(p => p.GroupUserUID)
            .Select(p => p.GroupUserUID)
            .Take(64)
            .ToListAsync().ConfigureAwait(false);
        if (uids.Count == 0) return [];
        var users = await UserDataByUidAsync(uids).ConfigureAwait(false);
        return uids.Select(u => users.TryGetValue(u, out var d) ? d : new UserData(u)).ToList();
    }

    /// <summary>
    /// Aggregated activity feed for every syncshell the caller belongs to:
    /// newest published news, upcoming events and recent member-visible
    /// photos, newest first. Member-only content stays member-only  · 
    /// drafts, pending photos and restricted audiences never leak into the
    /// feed. Payloads stay small: photo thumbnails only under ~200 KB.
    /// </summary>
    [Authorize(Policy = "Identified")]
    public async Task<SyncshellCommunityFeedDto> GroupGetCommunityFeed()
    {
        _logger.LogCallInfo();

        var gids = await DbContext.GroupPairs.AsNoTracking()
            .Where(gp => gp.GroupUserUID == UserUID)
            .Select(gp => gp.GroupGID)
            .ToListAsync().ConfigureAwait(false);
        var followed = await FollowedNonMemberGidsAsync(gids).ConfigureAwait(false);
        var allGids = gids.Union(followed, StringComparer.Ordinal).ToList();
        if (allGids.Count == 0)
        {
            return new SyncshellCommunityFeedDto([]);
        }

        var accents = await DbContext.SyncshellWelcomePages.AsNoTracking()
            .Where(w => allGids.Contains(w.GroupGID))
            .Select(w => new { w.GroupGID, w.AccentColor })
            .ToDictionaryAsync(w => w.GroupGID, w => w.AccentColor)
            .ConfigureAwait(false);
        var aliases = await DbContext.Groups.AsNoTracking()
            .Where(g => allGids.Contains(g.GID))
            .Select(g => new { g.GID, g.Alias })
            .ToDictionaryAsync(g => g.GID, g => g.Alias)
            .ConfigureAwait(false);

        var items = await CollectCommunityFeedItems(gids, accents, aliases)
            .ConfigureAwait(false);
        items.AddRange(await CollectFollowedFeedEventsAsync(followed, accents, aliases)
            .ConfigureAwait(false));
        return new SyncshellCommunityFeedDto(items
            .OrderByDescending(i => i.TimestampUtc).Take(30).ToList());
    }

    /// <summary>Shells the caller bookmarked but is not a member of - the
    /// feed slice for them is capped to what the public preview exposes.</summary>
    private async Task<List<string>> FollowedNonMemberGidsAsync(List<string> memberGids)
    {
        var favorites = await DbContext.SyncshellFavorites.AsNoTracking()
            .Where(f => f.UserUID == UserUID)
            .Select(f => f.GroupGID)
            .ToListAsync().ConfigureAwait(false);
        var memberSet = memberGids.ToHashSet(StringComparer.Ordinal);
        return favorites.Where(g => !memberSet.Contains(g)).ToList();
    }

    /// <summary>
    /// Followed non-member shells contribute exactly the event slice the
    /// community preview shows strangers: published + flagged Public.
    /// Member-only events, news, photos, comments and RSVPs never leak to
    /// followers - following is a bookmark, not membership.
    /// </summary>
    private async Task<List<SyncshellCommunityFeedItemDto>> CollectFollowedFeedEventsAsync(
        List<string> followedGids,
        Dictionary<string, string> accents,
        Dictionary<string, string> aliases,
        FeedCursor? cursor = null,
        int? pagedTake = null)
    {
        var items = new List<SyncshellCommunityFeedItemDto>();
        if (followedGids.Count == 0) return items;
        var cutoff = DateTime.UtcNow - TimeSpan.FromDays(1);
        var eventQuery = ApplyFeedKeyset(
            DbContext.SyncshellCommunityEvents.AsNoTracking()
                .Where(e => followedGids.Contains(e.GroupGID) && e.StartUtc >= cutoff
                    && e.PublishStatus == (int)ContentPublishStatus.Published
                    && e.Visibility == (int)EventVisibility.Public),
            cursor, SyncshellFeedItemKind.Event, e => e.StartUtc, e => e.Id);
        // v2 pages the same newest-first order the merged feed applies; v1
        // keeps its soonest-first window - the caps mean different things.
        var ordered = pagedTake.HasValue
            ? eventQuery.OrderByDescending(e => e.StartUtc).ThenByDescending(e => e.Id)
            : eventQuery.OrderBy(e => e.StartUtc);
        var events = await ordered
            .Take(pagedTake ?? 12)
            .Select(e => new { e.GroupGID, e.Id, e.Title, e.Location, e.StartUtc, e.CreatedAt,
                e.EventStatus, e.PreviewMediaHash, e.ImageMediaHash })
            .ToListAsync().ConfigureAwait(false);
        foreach (var e in events)
        {
            var title = e.EventStatus == (int)EventLifecycleStatus.Cancelled
                ? $"Cancelled - {e.Title}" : e.Title;
            items.Add(new SyncshellCommunityFeedItemDto(
                e.GroupGID, aliases.GetValueOrDefault(e.GroupGID),
                accents.GetValueOrDefault(e.GroupGID),
                SyncshellFeedItemKind.Event, e.Id, title, e.Location, e.StartUtc,
                ThumbnailMediaHash: e.PreviewMediaHash ?? e.ImageMediaHash,
                ActivityUtc: e.CreatedAt, FullMediaHash: e.ImageMediaHash));
        }
        return items;
    }

    /// <summary>
    /// Collects the raw feed rows: published news, upcoming events and
    /// member-audience photos, capped per kind. Restricted audiences resolve
    /// per viewer in <c>GroupGetCommunity</c> and never enter the feed.
    /// Feed v2 passes <paramref name="cursor"/> + <paramref name="pagedTake"/>:
    /// the keyset then applies at candidate selection (the v1 cap would hide
    /// every older row) and each kind pages in merged-feed order.
    /// </summary>
    private async Task<List<SyncshellCommunityFeedItemDto>> CollectCommunityFeedItems(
        List<string> gids,
        Dictionary<string, string> accents,
        Dictionary<string, string> aliases,
        FeedCursor? cursor = null,
        int? pagedTake = null)
    {
        var perKind = pagedTake ?? 12;
        const int thumbnailSizeCap = 200_000;
        var items = new List<SyncshellCommunityFeedItemDto>(perKind * 3);

        var newsQuery = ApplyFeedKeyset(
            DbContext.SyncshellCommunityNews.AsNoTracking()
                .Where(n => gids.Contains(n.GroupGID)
                    && (n.Status == (int)ContentPublishStatus.Published
                        || (n.Status == (int)ContentPublishStatus.Scheduled && n.ScheduledForUtc <= DateTime.UtcNow))),
            cursor, SyncshellFeedItemKind.News, n => n.PublishedAt, n => n.Id);
        var news = await newsQuery
            .OrderByDescending(n => n.PublishedAt).ThenByDescending(n => n.Id)
            .Take(perKind)
            .Select(n => new { n.GroupGID, n.Id, n.Title, n.Body, n.PublishedAt })
            .ToListAsync().ConfigureAwait(false);
        foreach (var n in news)
        {
            var snippet = string.IsNullOrWhiteSpace(n.Body) ? null
                : n.Body.Length <= 140 ? n.Body : n.Body[..140].TrimEnd() + "…";
            items.Add(new SyncshellCommunityFeedItemDto(
                n.GroupGID, aliases.GetValueOrDefault(n.GroupGID),
                accents.GetValueOrDefault(n.GroupGID),
                SyncshellFeedItemKind.News, n.Id, n.Title, snippet, n.PublishedAt));
        }

        var cutoff = DateTime.UtcNow - TimeSpan.FromDays(1);
        var eventQuery = ApplyFeedKeyset(
            DbContext.SyncshellCommunityEvents.AsNoTracking()
                .Where(e => gids.Contains(e.GroupGID) && e.StartUtc >= cutoff
                    && e.PublishStatus == (int)ContentPublishStatus.Published),
            cursor, SyncshellFeedItemKind.Event, e => e.StartUtc, e => e.Id);
        // v2 pages the same newest-first order the merged feed applies; v1
        // keeps its soonest-first window - the caps mean different things.
        var orderedEvents = pagedTake.HasValue
            ? eventQuery.OrderByDescending(e => e.StartUtc).ThenByDescending(e => e.Id)
            : eventQuery.OrderBy(e => e.StartUtc);
        var events = await orderedEvents
            .Take(perKind)
            .Select(e => new { e.GroupGID, e.Id, e.Title, e.Location, e.StartUtc, e.CreatedAt,
                e.EventStatus, e.PreviewMediaHash, e.ImageMediaHash })
            .ToListAsync().ConfigureAwait(false);
        foreach (var e in events)
        {
            // Cancelled events stay visible - a cancelled upcoming event is
            // information members need, so the feed marks rather than hides it.
            var title = e.EventStatus == (int)EventLifecycleStatus.Cancelled
                ? $"Cancelled - {e.Title}" : e.Title;
            items.Add(new SyncshellCommunityFeedItemDto(
                e.GroupGID, aliases.GetValueOrDefault(e.GroupGID),
                accents.GetValueOrDefault(e.GroupGID),
                SyncshellFeedItemKind.Event, e.Id, title, e.Location, e.StartUtc,
                ThumbnailMediaHash: e.PreviewMediaHash ?? e.ImageMediaHash,
                ActivityUtc: e.CreatedAt, FullMediaHash: e.ImageMediaHash));
        }

        var photoQuery = ApplyFeedKeyset(
            DbContext.SyncshellCommunityGallery.AsNoTracking()
                .Where(g => gids.Contains(g.GroupGID) && g.Status == 0 && g.Audience == 0),
            cursor, SyncshellFeedItemKind.Photo, g => g.CreatedAt, g => g.Id);
        var photos = await photoQuery
            .OrderByDescending(g => g.CreatedAt).ThenByDescending(g => g.Id)
            .Take(perKind)
            .Select(g => new { g.GroupGID, g.Id, g.Caption, g.FileName, g.Size,
                g.ImageBase64, g.ImageMediaHash, g.CreatedAt })
            .ToListAsync().ConfigureAwait(false);
        foreach (var g in photos)
        {
            // real thumbnails: big originals get downscaled server-side,
            // small ones pass through as-is · feed cards always have art.
            string? thumb = null;
            if (g.ImageBase64 != null)
            {
                try
                {
                    var raw = Convert.FromBase64String(g.ImageBase64);
                    if (raw.Length <= thumbnailSizeCap)
                    {
                        thumb = g.ImageBase64;
                    }
                    else
                    {
                        using var image = Image.Load<Rgba32>(raw);
                        image.Mutate(x => x.Resize(256, 0));
                        using var ms = new MemoryStream();
                        image.SaveAsJpeg(ms, new JpegEncoder { Quality = 70 });
                        var encoded = ms.ToArray();
                        thumb = encoded.Length <= thumbnailSizeCap
                            ? Convert.ToBase64String(encoded) : null;
                    }
                }
                catch (Exception)
                {
                    thumb = null;
                }
            }
            items.Add(new SyncshellCommunityFeedItemDto(
                g.GroupGID, aliases.GetValueOrDefault(g.GroupGID),
                accents.GetValueOrDefault(g.GroupGID),
                SyncshellFeedItemKind.Photo, g.Id,
                string.IsNullOrWhiteSpace(g.Caption) ? "New photo" : g.Caption!,
                g.FileName, g.CreatedAt, thumb, ThumbnailMediaHash: g.ImageMediaHash));
        }

        return items;
    }

    /// <summary>
    /// Public read of a listed community page - no membership required. Falls
    /// back to the member-gated payload when the caller is in the syncshell so
    /// members browsing the directory still see their own unlisted page.
    /// </summary>
    [Authorize(Policy = "Identified")]
    public async Task<SyncshellCommunityDto?> GroupGetPublicCommunity(GroupDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));

        var groupPair = await DbContext.GroupPairs
            .SingleOrDefaultAsync(gp => gp.GroupGID == dto.Group.GID && gp.GroupUserUID == UserUID)
            .ConfigureAwait(false);

        var welcomePage = await DbContext.SyncshellWelcomePages
            .SingleOrDefaultAsync(w => w.GroupGID == dto.Group.GID)
            .ConfigureAwait(false);

        var isPublic = welcomePage is { IsListedPublicly: true, IsEnabled: true };
        if (groupPair == null && !isPublic) return null;
        if (welcomePage == null) return null;

        // Public page: only published events flagged Public - members-only
        // events never leak through the listed preview.
        var events = await DbContext.SyncshellCommunityEvents
            .Where(e => e.GroupGID == dto.Group.GID
                && e.PublishStatus == (int)ContentPublishStatus.Published
                && (groupPair != null || e.Visibility == (int)EventVisibility.Public))
            .OrderBy(e => e.StartUtc)
            .ToListAsync()
            .ConfigureAwait(false);

        // Pending/scheduled/draft items never leave the staff surface - the
        // public read shows visible posts; a member still sees their own.
        var news = await DbContext.SyncshellCommunityNews
            .Where(n => n.GroupGID == dto.Group.GID
                && (n.Status == (int)ContentPublishStatus.Published
                    || (n.Status == (int)ContentPublishStatus.Scheduled && n.ScheduledForUtc <= DateTime.UtcNow)
                    || (groupPair != null && n.AuthorUid == UserUID)))
            .OrderByDescending(n => n.IsPinned)
            .ThenByDescending(n => n.PublishedAt)
            .ToListAsync()
            .ConfigureAwait(false);

        // Public reads hide images still awaiting approval - member or
        // contest submissions are for the member-facing query only.
        var gallery = await DbContext.SyncshellCommunityGallery
            .Where(g => g.GroupGID == dto.Group.GID && g.Status == 0)
            .OrderBy(g => g.SortOrder)
            .ThenBy(g => g.Id)
            .ToListAsync()
            .ConfigureAwait(false);

        var albums = await DbContext.SyncshellCommunityGalleryAlbums
            .Where(a => a.GroupGID == dto.Group.GID)
            .OrderBy(a => a.SortOrder)
            .ThenBy(a => a.Id)
            .ToListAsync()
            .ConfigureAwait(false);

        // Restricted audiences never leave the member surface: anonymous
        // directory visitors get Everyone-only, members the same set the
        // member-facing read would give them.
        if (groupPair == null)
        {
            gallery = gallery.Where(g => g.Audience == 0).ToList();
            albums = albums.Where(a => a.Audience == 0).ToList();
        }
        else
        {
            var isStaff = await IsCommunityStaff(dto.Group.GID).ConfigureAwait(false);
            gallery = gallery.Where(g =>
                    GalleryAudienceVisible(g.Audience, g.AllowedUidsJson, isStaff, g.UploadedByUid))
                .ToList();
            albums = albums.Where(a =>
                    GalleryAudienceVisible(a.Audience, a.AllowedUidsJson, isStaff, null))
                .ToList();
        }

        var staffRoles = await DbContext.SyncshellCommunityStaffRoles
            .Where(r => r.GroupGID == dto.Group.GID)
            .OrderBy(r => r.SortOrder)
            .ThenBy(r => r.Id)
            .ToListAsync()
            .ConfigureAwait(false);

        var staffAssignments = await DbContext.SyncshellCommunityStaffAssignments
            .Where(a => a.GroupGID == dto.Group.GID)
            .ToListAsync()
            .ConfigureAwait(false);

        var likeData = await GetGalleryLikeData(gallery.Select(g => g.Id).ToList())
            .ConfigureAwait(false);

        // The preview's join CTA resolves through the same effective-policy
        // resolver the join path uses - InvitesEnabled=false folds to Closed.
        var group = await DbContext.Groups.AsNoTracking()
            .SingleOrDefaultAsync(g => g.GID == dto.Group.GID).ConfigureAwait(false);

        var organizerUsers = await UserDataByUidAsync(events
            .SelectMany(e => DeserializeJson<List<string>>(e.OrganizerUidsJson) ?? []))
            .ConfigureAwait(false);
        var memberIdentities = await LoadMemberIdentitiesAsync(staffAssignments, news, events,
            gallery, organizerUsers).ConfigureAwait(false);

        return new SyncshellCommunityDto(
            MapProfile(welcomePage),
            welcomePage.RulesText,
            DeserializeJson<List<SyncshellCommunityLinkDto>>(welcomePage.LinksJson),
            DeserializeJson<List<SyncshellCommunitySection>>(welcomePage.VisibleSectionsJson),
            welcomePage.IsListedPublicly,
            events.Select(e => MapEvent(e, users: organizerUsers)).ToList(),
            news.Select(MapNews).ToList(),
            gallery.Select(g => MapGallery(g,
                likeData.Counts.GetValueOrDefault(g.Id), likeData.Mine.Contains(g.Id))).ToList(),
            albums.Select(MapAlbum).ToList(),
            staffRoles.Select(MapStaffRole).ToList(),
            staffAssignments.Select(MapStaffAssignment).ToList(),
            DeserializeJson<List<string>>(welcomePage.OverviewLayoutJson),
            GroupAlias: group?.Alias,
            EffectiveJoinPolicy: group != null ? EffectiveJoinPolicy(group) : null,
            MemberIdentities: memberIdentities,
            CallerFavorited: await DbContext.SyncshellFavorites.AsNoTracking()
                .AnyAsync(f => f.GroupGID == dto.Group.GID && f.UserUID == UserUID)
                .ConfigureAwait(false),
            IsAreaBound: await DbContext.AreaBoundSyncshells.AsNoTracking()
                .AnyAsync(a => a.GroupGID == dto.Group.GID).ConfigureAwait(false),
            FollowerCount: await DbContext.SyncshellFavorites.AsNoTracking()
                .CountAsync(f => f.GroupGID == dto.Group.GID).ConfigureAwait(false))
        ;
    }

    [Authorize(Policy = "Identified")]
    public async Task<SyncshellNewsPostDto?> GroupUpsertCommunityNews(SyncshellNewsPostUpdateDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));

        var hasRights = await HasCommunityRights(dto.Group.GID, SyncshellStaffPermission.ManageNews).ConfigureAwait(false);
        if (!hasRights) return null;

        var entity = dto.Id == 0
            ? null
            : await DbContext.SyncshellCommunityNews
                .SingleOrDefaultAsync(n => n.Id == dto.Id && n.GroupGID == dto.Group.GID)
                .ConfigureAwait(false);

        if (entity == null)
        {
            entity = new SyncshellCommunityNewsPost
            {
                GroupGID = dto.Group.GID,
                AuthorUid = UserUID,
                Status = (int)ContentPublishStatus.Draft,
                CreatedAt = DateTime.UtcNow
            };
            await DbContext.SyncshellCommunityNews.AddAsync(entity).ConfigureAwait(false);
        }

        entity.Title = dto.Title;
        entity.Body = dto.Body;
        entity.IsPinned = dto.IsPinned;

        // Staff writes carry full authority: an explicit StatusIntent goes
        // through the transition matrix; a legacy IsDraft write stays a plain
        // draft/publish switch so old clients behave exactly as before.
        var prevStatus = (ContentPublishStatus)entity.Status;
        var intent = dto.StatusIntent.HasValue
            ? (ContentPublishStatus)dto.StatusIntent.Value
            : dto.IsDraft ? ContentPublishStatus.Draft : ContentPublishStatus.Published;
        var next = PublishingService.ModeratorCanTransition(prevStatus, intent) ? intent : prevStatus;
        entity.Status = (int)next;
        entity.IsDraft = next == ContentPublishStatus.Draft;
        entity.ScheduledForUtc = next == ContentPublishStatus.Scheduled
            ? dto.ScheduledForUtc ?? entity.ScheduledForUtc
            : null;
        if (dto.CoAuthorUids != null)
            entity.CoAuthorUidsJson = SerializeJson(dto.CoAuthorUids);
        if (entity.AuthorUid == null)
            entity.AuthorUid = UserUID;
        entity.PublishedAt = dto.PublishedAt ?? entity.PublishedAt;
        // Publishing a draft stamps the moment it goes live, not when the draft was saved.
        if (entity.PublishedAt == default || (prevStatus == ContentPublishStatus.Draft
            && next == ContentPublishStatus.Published && dto.PublishedAt == null))
            entity.PublishedAt = DateTime.UtcNow;
        entity.UpdatedAt = DateTime.UtcNow;

        await DbContext.SaveChangesAsync().ConfigureAwait(false);
        if (prevStatus != ContentPublishStatus.Published && next == ContentPublishStatus.Published)
        {
            await PushMentionNotificationsAsync(dto.Group.GID, "news", entity.Id,
                entity.Title, entity.Body).ConfigureAwait(false);
        }
        await PushCommunityChangedAsync(dto.Group.GID, SyncshellCommunitySection.News, entity.Id).ConfigureAwait(false);
        return MapNews(entity);
    }

    [Authorize(Policy = "Identified")]
    public async Task<bool> GroupDeleteCommunityNews(SyncshellCommunityItemDeleteDto dto)
        => await DeleteCommunityItem<SyncshellCommunityNewsPost>(dto, SyncshellStaffPermission.ManageNews, SyncshellCommunitySection.News).ConfigureAwait(false);

    [Authorize(Policy = "Identified")]
    public async Task<SyncshellGalleryImageDto?> GroupUpsertCommunityGalleryImage(SyncshellGalleryImageUpdateDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));

        var hasRights = await HasCommunityRights(dto.Group.GID, SyncshellStaffPermission.ManageGallery).ConfigureAwait(false);
        if (!hasRights) return null;

        var entity = dto.Id == 0
            ? null
            : await DbContext.SyncshellCommunityGallery
                .SingleOrDefaultAsync(g => g.Id == dto.Id && g.GroupGID == dto.Group.GID)
                .ConfigureAwait(false);

        if (entity == null)
        {
            entity = new SyncshellCommunityGalleryImage
            {
                GroupGID = dto.Group.GID,
                CreatedAt = DateTime.UtcNow
            };
            await DbContext.SyncshellCommunityGallery.AddAsync(entity).ConfigureAwait(false);
        }

        // Image payload only travels on upload - metadata-only updates keep it.
        // A media hash supersedes base64: the bytes already live on the file server.
        if (dto.ImageMediaHash != null)
        {
            entity.ImageMediaHash = dto.ImageMediaHash;
            entity.ImageBase64 = null;
            entity.FileName = dto.FileName;
            entity.ContentType = dto.ContentType;
            entity.Size = dto.Size;
            await _communityMedia.RecordMediaAsync(DbContext, dto.ImageMediaHash,
                UserUID, dto.ContentType, dto.Size).ConfigureAwait(false);
        }
        else if (dto.ImageBase64 != null)
        {
            entity.ImageBase64 = dto.ImageBase64;
            entity.FileName = dto.FileName;
            entity.ContentType = dto.ContentType;
            entity.Size = dto.Size;
        }
        entity.Caption = dto.Caption;
        entity.SortOrder = dto.SortOrder;
        entity.AlbumId = dto.AlbumId;
        if (dto.Audience.HasValue)
        {
            entity.Audience = dto.Audience.Value;
        }
        if (dto.AllowedUids != null)
        {
            entity.AllowedUidsJson = SerializeJson(dto.AllowedUids);
        }
        if (entity.UploadedByUid == null)
        {
            entity.UploadedByUid = UserUID;
        }

        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        var likeCount = await DbContext.SyncshellCommunityGalleryImageLikes
            .CountAsync(l => l.ImageId == entity.Id).ConfigureAwait(false);
        var liked = await DbContext.SyncshellCommunityGalleryImageLikes
            .AnyAsync(l => l.ImageId == entity.Id && l.UserUID == UserUID).ConfigureAwait(false);
        await PushCommunityChangedAsync(dto.Group.GID, SyncshellCommunitySection.Gallery, entity.Id).ConfigureAwait(false);
        return MapGallery(entity, likeCount, liked);
    }

    /// <summary>
    /// Toggles the caller's like on a gallery image - members only; returns
    /// the resulting liked-state + total so the client can patch its copy.
    /// </summary>
    [Authorize(Policy = "Identified")]
    public async Task<SyncshellGalleryImageLikeDto?> GroupToggleCommunityGalleryImageLike(
        SyncshellGalleryImageLikeDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));

        var image = await DbContext.SyncshellCommunityGallery
            .SingleOrDefaultAsync(g => g.Id == dto.ImageId && g.GroupGID == dto.Group.GID)
            .ConfigureAwait(false);
        if (image == null) return null;

        var (isMember, _) = await TryValidateUserInGroup(dto.Group.GID, UserUID).ConfigureAwait(false);
        if (!isMember) return null;

        // Likes follow visibility - unpublished or audience-restricted
        // photos are not likeable for members they are hidden from.
        var likeIsStaff = await IsCommunityStaff(dto.Group.GID).ConfigureAwait(false);
        if (image.Status != 0
            || !GalleryAudienceVisible(image.Audience, image.AllowedUidsJson, likeIsStaff, image.UploadedByUid))
        {
            return null;
        }

        var existing = await DbContext.SyncshellCommunityGalleryImageLikes
            .FindAsync(new object[] { dto.ImageId, UserUID }).ConfigureAwait(false);
        var liked = existing == null;
        if (existing == null)
        {
            await DbContext.SyncshellCommunityGalleryImageLikes.AddAsync(
                new SyncshellCommunityGalleryImageLike
                {
                    ImageId = dto.ImageId,
                    UserUID = UserUID,
                    GroupGID = dto.Group.GID,
                    CreatedAt = DateTime.UtcNow
                }).ConfigureAwait(false);
        }
        else
        {
            DbContext.SyncshellCommunityGalleryImageLikes.Remove(existing);
        }
        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        var count = await DbContext.SyncshellCommunityGalleryImageLikes
            .CountAsync(l => l.ImageId == dto.ImageId).ConfigureAwait(false);
        return dto with { Liked = liked, Count = count };
    }

    [Authorize(Policy = "Identified")]
    public async Task<bool> GroupDeleteCommunityGalleryImage(SyncshellCommunityItemDeleteDto dto)
    {
        var deleted = await DeleteCommunityItem<SyncshellCommunityGalleryImage>(dto, SyncshellStaffPermission.ManageGallery, SyncshellCommunitySection.Gallery).ConfigureAwait(false);
        if (deleted)
        {
            // Album covers are soft references - clear any pointing at the
            // removed image so the albums fall back to their first photo.
            var covers = await DbContext.SyncshellCommunityGalleryAlbums
                .Where(a => a.GroupGID == dto.Group.GID && a.CoverImageId == dto.Id)
                .ToListAsync().ConfigureAwait(false);
            foreach (var album in covers)
            {
                album.CoverImageId = null;
            }
            if (covers.Count > 0)
            {
                await DbContext.SaveChangesAsync().ConfigureAwait(false);
            }
        }
        return deleted;
    }

    [Authorize(Policy = "Identified")]
    public async Task<SyncshellGalleryAlbumDto?> GroupUpsertCommunityGalleryAlbum(SyncshellGalleryAlbumUpdateDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));

        var hasRights = await HasCommunityRights(dto.Group.GID, SyncshellStaffPermission.ManageGallery).ConfigureAwait(false);
        if (!hasRights) return null;

        var entity = dto.Id == 0
            ? null
            : await DbContext.SyncshellCommunityGalleryAlbums
                .SingleOrDefaultAsync(a => a.Id == dto.Id && a.GroupGID == dto.Group.GID)
                .ConfigureAwait(false);

        if (entity == null)
        {
            entity = new SyncshellCommunityGalleryAlbum
            {
                GroupGID = dto.Group.GID,
                CreatedAt = DateTime.UtcNow
            };
            await DbContext.SyncshellCommunityGalleryAlbums.AddAsync(entity).ConfigureAwait(false);
        }

        entity.Name = dto.Name;
        entity.Description = dto.Description;
        entity.LinkedEventId = dto.LinkedEventId;
        entity.CoverImageId = dto.CoverImageId;
        entity.SortOrder = dto.SortOrder;
        if (dto.Audience.HasValue)
        {
            entity.Audience = dto.Audience.Value;
        }
        if (dto.AllowedUids != null)
        {
            entity.AllowedUidsJson = SerializeJson(dto.AllowedUids);
        }

        await DbContext.SaveChangesAsync().ConfigureAwait(false);
        await PushCommunityChangedAsync(dto.Group.GID, SyncshellCommunitySection.Gallery, entity.Id).ConfigureAwait(false);
        return MapAlbum(entity);
    }

    [Authorize(Policy = "Identified")]
    public async Task<bool> GroupDeleteCommunityGalleryAlbum(SyncshellCommunityItemDeleteDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));

        var hasRights = await HasCommunityRights(dto.Group.GID, SyncshellStaffPermission.ManageGallery).ConfigureAwait(false);
        if (!hasRights) return false;

        var entity = await DbContext.SyncshellCommunityGalleryAlbums
            .SingleOrDefaultAsync(a => a.Id == dto.Id && a.GroupGID == dto.Group.GID)
            .ConfigureAwait(false);
        if (entity == null) return false;

        // Deleting an album frees its photos - it never deletes them.
        var images = await DbContext.SyncshellCommunityGallery
            .Where(g => g.GroupGID == dto.Group.GID && g.AlbumId == dto.Id)
            .ToListAsync().ConfigureAwait(false);
        foreach (var image in images)
        {
            image.AlbumId = null;
        }

        DbContext.SyncshellCommunityGalleryAlbums.Remove(entity);
        await DbContext.SaveChangesAsync().ConfigureAwait(false);
        await PushCommunityChangedAsync(dto.Group.GID, SyncshellCommunitySection.Gallery, dto.Id).ConfigureAwait(false);
        return true;
    }

    [Authorize(Policy = "Identified")]
    public async Task<SyncshellStaffRoleDto?> GroupUpsertCommunityStaffRole(SyncshellStaffRoleUpdateDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));

        if (!await CanManageStaffRoles(dto.Group.GID).ConfigureAwait(false)) return null;

        var entity = dto.Id == 0
            ? null
            : await DbContext.SyncshellCommunityStaffRoles
                .SingleOrDefaultAsync(r => r.Id == dto.Id && r.GroupGID == dto.Group.GID)
                .ConfigureAwait(false);

        if (entity == null)
        {
            entity = new SyncshellCommunityStaffRole
            {
                GroupGID = dto.Group.GID,
                CreatedAt = DateTime.UtcNow
            };
            await DbContext.SyncshellCommunityStaffRoles.AddAsync(entity).ConfigureAwait(false);
        }

        entity.Name = dto.Name;
        entity.ColorArgb = dto.ColorArgb;
        entity.Icon = dto.Icon;
        entity.Permissions = (long)dto.Permissions;
        entity.SortOrder = dto.SortOrder;

        await DbContext.SaveChangesAsync().ConfigureAwait(false);
        await PushCommunityChangedAsync(dto.Group.GID, SyncshellCommunitySection.Staff, entity.Id).ConfigureAwait(false);
        return MapStaffRole(entity);
    }

    [Authorize(Policy = "Identified")]
    public async Task<bool> GroupDeleteCommunityStaffRole(SyncshellCommunityItemDeleteDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));

        if (!await CanManageStaffRoles(dto.Group.GID).ConfigureAwait(false)) return false;

        var entity = await DbContext.SyncshellCommunityStaffRoles
            .SingleOrDefaultAsync(r => r.Id == dto.Id && r.GroupGID == dto.Group.GID)
            .ConfigureAwait(false);
        if (entity == null) return false;

        // Deleting a role frees its holders - it never touches membership.
        var assignments = await DbContext.SyncshellCommunityStaffAssignments
            .Where(a => a.GroupGID == dto.Group.GID && a.RoleId == dto.Id)
            .ToListAsync().ConfigureAwait(false);
        DbContext.SyncshellCommunityStaffAssignments.RemoveRange(assignments);
        DbContext.SyncshellCommunityStaffRoles.Remove(entity);
        await DbContext.SaveChangesAsync().ConfigureAwait(false);
        await PushCommunityChangedAsync(dto.Group.GID, SyncshellCommunitySection.Staff, dto.Id).ConfigureAwait(false);
        // Holders who lost their last assignment lose the staff affiliation -
        // refresh their group list and cached community projection.
        foreach (var uid in assignments.Select(a => a.UserUID).Distinct(StringComparer.Ordinal))
        {
            await PushStaffAffiliationChangedAsync(dto.Group.GID, uid).ConfigureAwait(false);
        }
        return true;
    }

    /// <summary>Self-revocation: a member leaving the shell by choice drops
    /// their staff affiliation along with membership. Area-bound auto-leave
    /// intentionally keeps the assignment (staff retain administrative
    /// access outside bounds) - this endpoint is only for explicit leaves.
    /// Idempotent; no rights check beyond being the assignee.</summary>
    [Authorize(Policy = "Identified")]
    public async Task<bool> GroupRevokeOwnStaffAffiliation(GroupDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));

        var assignments = await DbContext.SyncshellCommunityStaffAssignments
            .Where(a => a.GroupGID == dto.Group.GID && a.UserUID == UserUID)
            .ToListAsync().ConfigureAwait(false);
        if (assignments.Count == 0) return true;

        DbContext.SyncshellCommunityStaffAssignments.RemoveRange(assignments);
        await DbContext.SaveChangesAsync().ConfigureAwait(false);
        await PushCommunityChangedAsync(dto.Group.GID,
            SyncshellCommunitySection.Staff, null).ConfigureAwait(false);
        await PushStaffAffiliationChangedAsync(dto.Group.GID, UserUID).ConfigureAwait(false);
        return true;
    }

    [Authorize(Policy = "Identified")]
    public async Task<bool> GroupSetCommunityStaffAssignment(SyncshellStaffAssignmentUpdateDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));

        if (!await CanManageStaffRoles(dto.Group.GID).ConfigureAwait(false)) return false;

        // Jobs can only be handed to actual members of the syncshell - the
        // owner counts implicitly even when they carry no pair row. Clearing
        // a non-member's rows stays legal: the assignment is an
        // administrative record, not membership, and cleanup must never be
        // blocked - otherwise departed staff become unremovable.
        var (isMember, _) = await TryValidateUserInGroup(dto.Group.GID, dto.UserUID).ConfigureAwait(false);
        if (!isMember)
        {
            var ownerUid = await DbContext.Groups.Where(g => g.GID == dto.Group.GID)
                .Select(g => g.OwnerUID).SingleOrDefaultAsync().ConfigureAwait(false);
            var isClearAll = dto.RoleIds != null
                ? dto.RoleIds.Count == 0
                : dto.RoleId == null;
            if (!isClearAll
                && !string.Equals(ownerUid, dto.UserUID, StringComparison.Ordinal)) return false;
        }

        // New-style callers send the member's complete role set - the list
        // replaces every stored assignment for the user (empty clears all).
        if (dto.RoleIds != null)
        {
            var wanted = dto.RoleIds.Distinct().ToList();
            var validIds = await DbContext.SyncshellCommunityStaffRoles
                .Where(r => r.GroupGID == dto.Group.GID && wanted.Contains(r.Id))
                .Select(r => r.Id).ToListAsync().ConfigureAwait(false);
            if (validIds.Count != wanted.Count) return false;

            var existing = await DbContext.SyncshellCommunityStaffAssignments
                .Where(a => a.GroupGID == dto.Group.GID && a.UserUID == dto.UserUID)
                .ToListAsync().ConfigureAwait(false);
            DbContext.SyncshellCommunityStaffAssignments.RemoveRange(existing);
            foreach (var roleId in wanted)
            {
                await DbContext.SyncshellCommunityStaffAssignments.AddAsync(
                    new SyncshellCommunityStaffAssignment
                    {
                        GroupGID = dto.Group.GID,
                        UserUID = dto.UserUID,
                        RoleId = roleId
                    }).ConfigureAwait(false);
            }

            await DbContext.SaveChangesAsync().ConfigureAwait(false);
            await PushCommunityChangedAsync(dto.Group.GID, SyncshellCommunitySection.Staff, null).ConfigureAwait(false);
            await PushStaffAffiliationChangedAsync(dto.Group.GID, dto.UserUID).ConfigureAwait(false);
            return true;
        }

        var existingLegacy = await DbContext.SyncshellCommunityStaffAssignments
            .Where(a => a.GroupGID == dto.Group.GID && a.UserUID == dto.UserUID)
            .ToListAsync().ConfigureAwait(false);

        if (dto.RoleId == null)
        {
            if (existingLegacy.Count == 0) return true;
            DbContext.SyncshellCommunityStaffAssignments.RemoveRange(existingLegacy);
            await DbContext.SaveChangesAsync().ConfigureAwait(false);
            await PushCommunityChangedAsync(dto.Group.GID, SyncshellCommunitySection.Staff, null).ConfigureAwait(false);
            await PushStaffAffiliationChangedAsync(dto.Group.GID, dto.UserUID).ConfigureAwait(false);
            return true;
        }

        var roleExists = await DbContext.SyncshellCommunityStaffRoles
            .AnyAsync(r => r.Id == dto.RoleId && r.GroupGID == dto.Group.GID)
            .ConfigureAwait(false);
        if (!roleExists) return false;

        // Legacy single-role write - the member ends up holding exactly the
        // requested role, any previous assignment is replaced.
        if (existingLegacy.Count != 1 || existingLegacy[0].RoleId != dto.RoleId.Value)
        {
            DbContext.SyncshellCommunityStaffAssignments.RemoveRange(existingLegacy);
            await DbContext.SyncshellCommunityStaffAssignments.AddAsync(
                new SyncshellCommunityStaffAssignment
                {
                    GroupGID = dto.Group.GID,
                    UserUID = dto.UserUID,
                    RoleId = dto.RoleId.Value
                }).ConfigureAwait(false);
        }

        await DbContext.SaveChangesAsync().ConfigureAwait(false);
        await PushCommunityChangedAsync(dto.Group.GID, SyncshellCommunitySection.Staff, null).ConfigureAwait(false);
        await PushStaffAffiliationChangedAsync(dto.Group.GID, dto.UserUID).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Fans a community-change notice out to all group members so connected
    /// clients can invalidate the affected shell's cached DTO. Member-scoped -
    /// public pages never push to non-members.
    /// </summary>
    /// <summary>
    /// Keeps a user's group list in step with their staff affiliation: a newly
    /// assigned member gets the shell's full info pushed (it appears without
    /// waiting for the next <see cref="GroupsGetAll"/>), a demoted user gets
    /// their community view invalidated - the follow-up fetch then returns
    /// member or preview data as appropriate.
    /// </summary>
    private async Task PushStaffAffiliationChangedAsync(string gid, string uid)
    {
        var group = await DbContext.Groups.Include(g => g.Owner).AsNoTracking()
            .SingleOrDefaultAsync(g => g.GID == gid).ConfigureAwait(false);
        if (group == null) return;

        var selfPair = await DbContext.GroupPairs.AsNoTracking()
            .SingleOrDefaultAsync(p => p.GroupGID == gid && p.GroupUserUID == uid).ConfigureAwait(false);
        var isStaff = await DbContext.SyncshellCommunityStaffAssignments.AsNoTracking()
            .AnyAsync(a => a.GroupGID == gid && a.UserUID == uid).ConfigureAwait(false);
        if (selfPair != null || isStaff)
        {
            var (rosters, memberCounts) = await GetPrivilegedRostersAsync([gid]).ConfigureAwait(false);
            var selfInfo = selfPair?.ToEnum() ?? GroupPairUserInfo.None;
            selfInfo.SetStaff(isStaff);
            var perms = selfPair == null
                ? GroupUserPreferredPermissions.NoneSet
                : (await DbContext.GroupPairPreferredPermissions.AsNoTracking()
                    .SingleOrDefaultAsync(u => u.UserUID == uid && u.GroupGID == gid)
                    .ConfigureAwait(false))?.ToEnum() ?? GroupUserPreferredPermissions.NoneSet;
            await Clients.User(uid).Client_GroupSendFullInfo(new GroupFullInfoDto(
                group.ToGroupData(), group.Owner.ToUserData(), group.ToEnum(), perms, selfInfo,
                rosters.TryGetValue(gid, out var roster) ? roster : new(StringComparer.Ordinal),
                memberCounts.GetValueOrDefault(gid))).ConfigureAwait(false);
        }

        // Always invalidate the affected user's community cache - a demoted
        // user's cached staff projection must be refetched, not linger.
        await Clients.User(uid)
            .Client_CommunityChanged(new CommunityChangedDto(new GroupData(gid),
                SyncshellCommunitySection.Staff, null)).ConfigureAwait(false);
    }

    private async Task PushCommunityChangedAsync(string gid, SyncshellCommunitySection? section = null, long? contentId = null)
    {
        var memberUids = await DbContext.GroupPairs.AsNoTracking()
            .Where(p => p.GroupGID == gid).Select(p => p.GroupUserUID).ToListAsync().ConfigureAwait(false);
        // Assigned staff without a live pair still administer the community -
        // they stay in the notification audience so their cached view tracks
        // changes (demoted staff drop out once the assignment row is gone).
        var staffUids = await DbContext.SyncshellCommunityStaffAssignments.AsNoTracking()
            .Where(a => a.GroupGID == gid).Select(a => a.UserUID).ToListAsync().ConfigureAwait(false);
        var audience = memberUids.Union(staffUids, StringComparer.Ordinal).ToList();
        if (audience.Count == 0) return;
        await Clients.Users(audience)
            .Client_CommunityChanged(new CommunityChangedDto(new GroupData(gid), section, contentId)).ConfigureAwait(false);
    }

    private async Task<bool> DeleteCommunityItem<TEntity>(SyncshellCommunityItemDeleteDto dto,
        SyncshellStaffPermission required, SyncshellCommunitySection section)
        where TEntity : class
    {
        var hasRights = await HasCommunityRights(dto.Group.GID, required).ConfigureAwait(false);
        if (!hasRights) return false;

        var entity = await DbContext.Set<TEntity>().FindAsync(new object[] { dto.Id }).ConfigureAwait(false);
        if (entity == null) return false;

        var gidProp = typeof(TEntity).GetProperty(nameof(SyncshellCommunityEvent.GroupGID));
        if (!string.Equals(gidProp?.GetValue(entity) as string, dto.Group.GID, StringComparison.Ordinal)) return false;

        DbContext.Set<TEntity>().Remove(entity);
        await DbContext.SaveChangesAsync().ConfigureAwait(false);
        await PushCommunityChangedAsync(dto.Group.GID, section, dto.Id).ConfigureAwait(false);
        return true;
    }

    private static SyncshellProfileDto MapProfile(SyncshellWelcomePage w) => new(
        w.GroupGID, w.Tagline, w.WelcomeText, w.WelcomeImageBase64, w.ImageFileName,
        w.ImageContentType, w.ImageSize, w.AvatarImageBase64, w.AvatarFileName,
        w.AvatarContentType, w.AvatarSize, w.AccentColor, DeserializeSections(w.SectionsJson),
        w.IsEnabled, w.ShowOnJoin, w.ShowOnAreaBoundJoin, w.CreatedAt, w.UpdatedAt,
        w.WelcomeMediaHash, w.AvatarMediaHash);

    private static SyncshellNewsPostDto MapNews(SyncshellCommunityNewsPost n) => new(
        n.Id, n.GroupGID, n.Title, n.Body, n.IsPinned, n.PublishedAt, n.CreatedAt, n.UpdatedAt,
        n.IsDraft, n.Status, n.AuthorUid, n.ScheduledForUtc,
        DeserializeJson<List<string>>(n.CoAuthorUidsJson), n.ReviewedByUid, n.ReviewNote,
        n.CommentCount, n.CommentsLocked);

    private static SyncshellGalleryImageDto MapGallery(SyncshellCommunityGalleryImage g,
        int likeCount = 0, bool likedByMe = false) => new(
        g.Id, g.GroupGID, g.ImageBase64, g.FileName, g.ContentType, g.Size, g.Caption,
        g.SortOrder, g.CreatedAt, g.AlbumId, g.UploadedByUid,
        (Sphene.API.Data.Enum.SyncshellGalleryImageStatus)g.Status, likeCount, likedByMe,
        (Sphene.API.Data.Enum.SyncshellGalleryAudience)g.Audience,
        DeserializeJson<List<string>>(g.AllowedUidsJson), g.ImageMediaHash);

    /// <summary>Like counts + the caller's like state for a gallery page.</summary>
    private async Task<(Dictionary<long, int> Counts, HashSet<long> Mine)> GetGalleryLikeData(
        List<long> imageIds)
    {
        var counts = await DbContext.SyncshellCommunityGalleryImageLikes
            .Where(l => imageIds.Contains(l.ImageId))
            .GroupBy(l => l.ImageId)
            .Select(x => new { x.Key, Count = x.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count)
            .ConfigureAwait(false);
        var mine = (await DbContext.SyncshellCommunityGalleryImageLikes
            .Where(l => l.UserUID == UserUID && imageIds.Contains(l.ImageId))
            .Select(l => l.ImageId)
            .ToListAsync().ConfigureAwait(false)).ToHashSet();
        return (counts, mine);
    }

    /// <summary>
    /// Audience check shared by the member and public community reads - staff
    /// and the uploader always see an item; everyone else follows the
    /// audience tier and the SelectedMembers allow-list.
    /// </summary>
    private bool GalleryAudienceVisible(int audience, string? allowedUidsJson,
        bool isStaff, string? uploaderUid)
    {
        if (isStaff
            || string.Equals(uploaderUid, UserUID, StringComparison.Ordinal))
        {
            return true;
        }
        return audience switch
        {
            0 => true,
            2 => DeserializeJson<HashSet<string>>(allowedUidsJson)?.Contains(UserUID) == true,
            _ => false,
        };
    }

    private static SyncshellGalleryAlbumDto MapAlbum(SyncshellCommunityGalleryAlbum a) => new(
        a.Id, a.GroupGID, a.Name, a.Description, a.LinkedEventId, a.CoverImageId,
        a.SortOrder, a.CreatedAt,
        (Sphene.API.Data.Enum.SyncshellGalleryAudience)a.Audience,
        DeserializeJson<List<string>>(a.AllowedUidsJson));

    private static SyncshellStaffRoleDto MapStaffRole(SyncshellCommunityStaffRole r) => new(
        r.Id, r.GroupGID, r.Name, r.ColorArgb, r.Icon,
        (SyncshellStaffPermission)r.Permissions, r.SortOrder);

    private static SyncshellStaffAssignmentDto MapStaffAssignment(SyncshellCommunityStaffAssignment a) => new(
        a.UserUID, a.RoleId, a.Id);

    private static List<SyncshellProfileSectionDto>? DeserializeSections(string? sectionsJson)
        => DeserializeJson<List<SyncshellProfileSectionDto>>(sectionsJson);

    private static string? SerializeJson<T>(List<T>? items)
        => items is { Count: > 0 } ? JsonSerializer.Serialize(items) : null;

    private static T? DeserializeJson<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

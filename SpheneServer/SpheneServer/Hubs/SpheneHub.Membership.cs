using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Sphene.API.Data.Enum;
using Sphene.API.Data.Extensions;
using Sphene.API.Dto.Group;
using SpheneServer.Utils;
using SpheneShared.Models;

namespace SpheneServer.Hubs;

/// <summary>
/// Membership & join-policy domain - application workflow, staff review and
/// the per-shell join policy. Kept out of <c>SpheneHub.Groups.cs</c> per the
/// modularization rule for new hub domains.
/// </summary>
public partial class SpheneHub
{
    private const int ApplicationMessageMaxLength = 500;
    private const int ReviewNoteMaxLength = 300;
    private const int ApplicationListTake = 50;

    /// <summary>Effective join policy for a shell - anyone may probe it (the join UI needs it before joining).</summary>
    [Authorize(Policy = "Identified")]
    public async Task<SyncshellJoinPolicy> GroupGetJoinPolicy(GroupDto dto)
    {
        var group = await DbContext.Groups.AsNoTracking()
            .SingleOrDefaultAsync(g => g.GID == dto.Group.GID).ConfigureAwait(false);
        if (group == null) return SyncshellJoinPolicy.Closed;
        return EffectiveJoinPolicy(group);
    }

    /// <summary>Sets the shell's join policy - moderator-or-owner gated like other shell settings.</summary>
    [Authorize(Policy = "Identified")]
    public async Task<bool> GroupSetJoinPolicy(GroupJoinPolicyDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));

        if (!await HasCommunityRights(dto.Group.GID, SyncshellStaffPermission.ManageSettings).ConfigureAwait(false)) return false;

        var group = await DbContext.Groups
            .SingleOrDefaultAsync(g => g.GID == dto.Group.GID).ConfigureAwait(false);
        if (group == null) return false;
        if (!Enum.IsDefined(dto.Policy)) return false;
        if (group.JoinPolicy == dto.Policy) return true;

        group.JoinPolicy = dto.Policy;
        await DbContext.SaveChangesAsync().ConfigureAwait(false);
        // community-visible setting changed - invalidate member caches.
        await PushCommunityChangedAsync(group.GID).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Submits (or resubmits) a membership application. Idempotent - an
    /// already-pending row just updates the message; a decided row is reset
    /// to Pending so the applicant can re-apply. One row per (group, user).
    /// </summary>
    [Authorize(Policy = "Identified")]
    public async Task<bool> GroupApplyForMembership(GroupApplyMembershipDto dto)
    {
        var gid = dto.Group.GID.Trim();
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));

        var group = await DbContext.Groups.Include(g => g.Owner).AsNoTracking()
            .SingleOrDefaultAsync(g => g.GID == gid || g.Alias == gid).ConfigureAwait(false);
        if (group == null || EffectiveJoinPolicy(group) != SyncshellJoinPolicy.ApprovalRequired) return false;

        // Area-bound shells accept members exclusively while the user stands
        // inside the bounds - applications would approve into nothing anyway.
        if (await DbContext.AreaBoundSyncshells.AsNoTracking()
                .AnyAsync(a => a.GroupGID == group.GID).ConfigureAwait(false))
            return false;

        var isMember = await DbContext.GroupPairs.AsNoTracking()
            .AnyAsync(p => p.GroupGID == group.GID && p.GroupUserUID == UserUID).ConfigureAwait(false);
        var isBanned = await DbContext.GroupBans.AsNoTracking()
            .AnyAsync(b => b.GroupGID == group.GID && b.BannedUserUID == UserUID).ConfigureAwait(false);
        var joinedGroups = await DbContext.GroupPairs.AsNoTracking()
            .CountAsync(p => p.GroupUserUID == UserUID).ConfigureAwait(false);
        if (isMember || isBanned || joinedGroups >= _maxJoinedGroupsByUser) return false;

        var message = string.IsNullOrWhiteSpace(dto.Message) ? null
            : dto.Message!.Length > ApplicationMessageMaxLength ? dto.Message[..ApplicationMessageMaxLength] : dto.Message;

        var application = await DbContext.GroupMembershipApplications
            .SingleOrDefaultAsync(a => a.GroupGID == group.GID && a.ApplicantUID == UserUID).ConfigureAwait(false);
        if (application == null)
        {
            application = new GroupMembershipApplication
            {
                GroupGID = group.GID,
                ApplicantUID = UserUID,
                Status = MembershipApplicationStatus.Pending,
                Message = message,
                CreatedAt = DateTime.UtcNow
            };
            DbContext.GroupMembershipApplications.Add(application);
        }
        else
        {
            if (application.Status == MembershipApplicationStatus.Pending)
            {
                application.Message = message;
            }
            else
            {
                // re-application after a decision - reset the same row
                application.Status = MembershipApplicationStatus.Pending;
                application.Message = message;
                application.ReviewedByUID = null;
                application.ReviewedBy = null;
                application.ReviewNote = null;
                application.ReviewedAt = null;
                application.CreatedAt = DateTime.UtcNow;
            }
        }

        try
        {
            await DbContext.SaveChangesAsync().ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // unique-key race: a parallel request already inserted the row - treat as applied
            return true;
        }

        if (application.Status == MembershipApplicationStatus.Pending)
        {
            var staffUids = await GetMembershipManagerUids(group.GID).ConfigureAwait(false);
            var aliasOrGid = group.Alias ?? group.GID;
            await _notificationWriter.PushManyAsync(staffUids,
                UserNotificationKind.MembershipApplicationReceived,
                $"New join request for {aliasOrGid}",
                $"{UserUID} wants to join your syncshell",
                actorUid: UserUID, groupGid: group.GID,
                contentKind: "application", contentId: application.Id.ToString(),
                dedupeKeyFor: _ => $"memapp:{group.GID}:{UserUID}").ConfigureAwait(false);
        }
        return true;
    }

    /// <summary>Withdraws the caller's own pending application. Idempotent - false when nothing is pending.</summary>
    [Authorize(Policy = "Identified")]
    public async Task<bool> GroupWithdrawMembershipApplication(GroupDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));

        var application = await DbContext.GroupMembershipApplications
            .SingleOrDefaultAsync(a => a.GroupGID == dto.Group.GID && a.ApplicantUID == UserUID).ConfigureAwait(false);
        if (application == null || application.Status != MembershipApplicationStatus.Pending) return false;

        application.Status = MembershipApplicationStatus.Withdrawn;
        application.ReviewedAt = DateTime.UtcNow;
        await DbContext.SaveChangesAsync().ConfigureAwait(false);
        return true;
    }

    /// <summary>The caller's own application for a shell - status tracking.</summary>
    [Authorize(Policy = "Identified")]
    public async Task<MembershipApplicationDto?> GroupGetMyMembershipApplication(GroupDto dto)
    {
        var application = await DbContext.GroupMembershipApplications.AsNoTracking()
            .Include(a => a.Applicant).Include(a => a.ReviewedBy)
            .SingleOrDefaultAsync(a => a.GroupGID == dto.Group.GID && a.ApplicantUID == UserUID).ConfigureAwait(false);
        return application == null ? null : MapApplication(application);
    }

    /// <summary>Pending (then recent decided) applications - requires ManageMembership rights.</summary>
    [Authorize(Policy = "Identified")]
    public async Task<List<MembershipApplicationDto>> GroupGetMembershipApplications(GroupDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));

        if (!await HasCommunityRights(dto.Group.GID, SyncshellStaffPermission.ManageMembership).ConfigureAwait(false))
            return [];

        var rows = await DbContext.GroupMembershipApplications.AsNoTracking()
            .Include(a => a.Applicant).Include(a => a.ReviewedBy)
            .Where(a => a.GroupGID == dto.Group.GID)
            .OrderBy(a => a.Status == MembershipApplicationStatus.Pending ? 0 : 1)
            .ThenByDescending(a => a.CreatedAt)
            .Take(ApplicationListTake)
            .ToListAsync().ConfigureAwait(false);

        var result = new List<MembershipApplicationDto>(rows.Count);
        foreach (var row in rows) result.Add(MapApplication(row));
        return result;
    }

    /// <summary>
    /// Approves or rejects a pending application. Approval runs the shared
    /// member-creation path (<see cref="CompleteGroupJoinAsync"/>) inside a
    /// transaction with a fresh eligibility check - banned users are
    /// auto-rejected, existing members become Approved without a duplicate
    /// pair, and capacity failures leave the row Pending.
    /// </summary>
    [Authorize(Policy = "Identified")]
    public async Task<MembershipApplicationDto?> GroupDecideMembershipApplication(GroupMembershipDecisionDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));

        if (!await HasCommunityRights(dto.Group.GID, SyncshellStaffPermission.ManageMembership).ConfigureAwait(false))
            return null;

        var application = await DbContext.GroupMembershipApplications
            .Include(a => a.Applicant).Include(a => a.ReviewedBy)
            .SingleOrDefaultAsync(a => a.Id == dto.ApplicationId && a.GroupGID == dto.Group.GID).ConfigureAwait(false);
        if (application == null) return null;

        // Idempotent: a decided row is returned as-is - no re-notify, no flip.
        if (application.Status != MembershipApplicationStatus.Pending) return MapApplication(application);

        var note = string.IsNullOrWhiteSpace(dto.Note) ? null
            : dto.Note!.Length > ReviewNoteMaxLength ? dto.Note[..ReviewNoteMaxLength] : dto.Note;
        var group = await DbContext.Groups.Include(g => g.Owner)
            .SingleAsync(g => g.GID == dto.Group.GID).ConfigureAwait(false);

        if (!dto.Approve)
        {
            application.Status = MembershipApplicationStatus.Rejected;
            application.ReviewedByUID = UserUID;
            application.ReviewNote = note;
            application.ReviewedAt = DateTime.UtcNow;
            await DbContext.SaveChangesAsync().ConfigureAwait(false);

            await _notificationWriter.PushAsync(DbContext, application.ApplicantUID,
                UserNotificationKind.MembershipApplicationRejected,
                $"Your request to join {group.Alias ?? group.GID} was declined",
                note, actorUid: UserUID, groupGid: group.GID,
                contentKind: "application", contentId: application.Id.ToString()).ConfigureAwait(false);
            return MapApplication(application);
        }

        // --- approve ---
        var applicantUid = application.ApplicantUID;
        var isMember = await DbContext.GroupPairs.AsNoTracking()
            .AnyAsync(p => p.GroupGID == group.GID && p.GroupUserUID == applicantUid).ConfigureAwait(false);
        var isBanned = await DbContext.GroupBans.AsNoTracking()
            .AnyAsync(b => b.GroupGID == group.GID && b.BannedUserUID == applicantUid).ConfigureAwait(false);

        if (isBanned)
        {
            // permanent ineligibility → auto-reject instead of stuck pending
            application.Status = MembershipApplicationStatus.Rejected;
            application.ReviewedByUID = UserUID;
            application.ReviewNote = note ?? "Applicant is banned from this syncshell";
            application.ReviewedAt = DateTime.UtcNow;
            await DbContext.SaveChangesAsync().ConfigureAwait(false);
            return MapApplication(application);
        }

        if (isMember)
        {
            // joined through another path meanwhile - record as approved, no pair work
            application.Status = MembershipApplicationStatus.Approved;
            application.ReviewedByUID = UserUID;
            application.ReviewNote = note;
            application.ReviewedAt = DateTime.UtcNow;
            await DbContext.SaveChangesAsync().ConfigureAwait(false);
            return MapApplication(application);
        }

        var memberCount = await DbContext.GroupPairs.AsNoTracking()
            .CountAsync(p => p.GroupGID == group.GID).ConfigureAwait(false);
        var joinedGroups = await DbContext.GroupPairs.AsNoTracking()
            .CountAsync(p => p.GroupUserUID == applicantUid).ConfigureAwait(false);
        if (memberCount >= _maxGroupUserCount || joinedGroups >= _maxJoinedGroupsByUser)
            return null; // transient - stays pending, can be retried after capacity frees

        var joinPerms = await ApplicantDefaultJoinPermissions(applicantUid).ConfigureAwait(false);
        var applicantIdent = await GetUserIdent(applicantUid).ConfigureAwait(false);

        // Transactional approval - the pair insert + decision commit together.
        // InMemory (tests) has no transactions; relational stores get one.
        if (DbContext.Database.IsRelational())
        {
            await using var tx = await DbContext.Database.BeginTransactionAsync().ConfigureAwait(false);
            await CompleteGroupJoinAsync(group, applicantUid, applicantIdent, joinPerms, group.Alias ?? group.GID).ConfigureAwait(false);
            application.Status = MembershipApplicationStatus.Approved;
            application.ReviewedByUID = UserUID;
            application.ReviewNote = note;
            application.ReviewedAt = DateTime.UtcNow;
            await DbContext.SaveChangesAsync().ConfigureAwait(false);
            await tx.CommitAsync().ConfigureAwait(false);
        }
        else
        {
            await CompleteGroupJoinAsync(group, applicantUid, applicantIdent, joinPerms, group.Alias ?? group.GID).ConfigureAwait(false);
            application.Status = MembershipApplicationStatus.Approved;
            application.ReviewedByUID = UserUID;
            application.ReviewNote = note;
            application.ReviewedAt = DateTime.UtcNow;
            await DbContext.SaveChangesAsync().ConfigureAwait(false);
        }

        await _notificationWriter.PushAsync(DbContext, applicantUid,
            UserNotificationKind.MembershipApplicationAccepted,
            $"You joined {group.Alias ?? group.GID}",
            "Your membership request was approved",
            actorUid: UserUID, groupGid: group.GID,
            contentKind: "application", contentId: application.Id.ToString()).ConfigureAwait(false);

        // Member list changed - invalidate the community page for all members.
        await PushCommunityChangedAsync(group.GID).ConfigureAwait(false);
        return MapApplication(application);
    }

    /// <summary>
    /// Everyone who may review applications for a shell: owner + moderators +
    /// staff assigned to a role carrying <see cref="SyncshellStaffPermission.ManageMembership"/>.
    /// </summary>
    private async Task<List<string>> GetMembershipManagerUids(string gid)
    {
        var uids = new HashSet<string>(StringComparer.Ordinal);
        var ownerUid = await DbContext.Groups.AsNoTracking()
            .Where(g => g.GID == gid).Select(g => g.OwnerUID).SingleOrDefaultAsync().ConfigureAwait(false);
        if (ownerUid != null) uids.Add(ownerUid);

        var moderatorUids = await DbContext.GroupPairs.AsNoTracking()
            .Where(p => p.GroupGID == gid && p.IsModerator)
            .Select(p => p.GroupUserUID).ToListAsync().ConfigureAwait(false);
        foreach (var uid in moderatorUids) uids.Add(uid);

        var staffBits = await DbContext.SyncshellCommunityStaffAssignments.AsNoTracking()
            .Where(a => a.GroupGID == gid)
            .Join(DbContext.SyncshellCommunityStaffRoles,
                a => a.RoleId, r => r.Id,
                (a, r) => new { a.UserUID, r.Permissions })
            .ToListAsync().ConfigureAwait(false);
        foreach (var s in staffBits)
        {
            if (((SyncshellStaffPermission)s.Permissions & SyncshellStaffPermission.ManageMembership) != 0)
                uids.Add(s.UserUID);
        }
        return [.. uids];
    }

    private async Task<GroupUserPreferredPermissions> ApplicantDefaultJoinPermissions(string applicantUid)
    {
        var defaults = await DbContext.UserDefaultPreferredPermissions.AsNoTracking()
            .SingleOrDefaultAsync(u => u.UserUID == applicantUid).ConfigureAwait(false);
        var perms = GroupUserPreferredPermissions.NoneSet;
        if (defaults == null) return perms;
        perms.SetDisableSounds(defaults.DisableGroupSounds);
        perms.SetDisableAnimations(defaults.DisableGroupAnimations);
        perms.SetDisableVFX(defaults.DisableGroupVFX);
        return perms;
    }

    private static MembershipApplicationDto MapApplication(GroupMembershipApplication a) => new(
        a.Id,
        new Sphene.API.Data.GroupData(a.GroupGID),
        a.Applicant.ToUserData(),
        a.Message,
        a.Status,
        a.CreatedAt,
        a.ReviewedAt,
        a.ReviewedBy?.ToUserData(),
        a.ReviewNote);
}

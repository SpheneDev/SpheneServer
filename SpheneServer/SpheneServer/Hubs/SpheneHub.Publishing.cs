using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Sphene.API.Data;
using Sphene.API.Data.Enum;
using Sphene.API.Dto.Group;
using SpheneServer.Services;
using SpheneServer.Utils;
using SpheneShared.Models;

namespace SpheneServer.Hubs;

/// <summary>
/// Phase-2 publishing pipeline endpoints - member submission paths, the staff
/// review queue, per-shell publishing policy, entity-reference resolution and
/// the community media upload channel. Member writes NEVER carry an
/// authoritative status: <see cref="PublishingService.ResolveWrite"/> decides
/// the effective state from policy + role so crafted DTOs cannot bypass review.
/// </summary>
public partial class SpheneHub
{
    /// <summary>Per-shell policy read - members see the same rules staff apply.</summary>
    [Authorize(Policy = "Identified")]
    public async Task<SyncshellPublishingPolicyDto> GroupGetPublishingPolicy(GroupDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));

        var (isMember, _) = await TryValidateUserInGroup(dto.Group.GID).ConfigureAwait(false);
        var isStaff = isMember || await IsCommunityStaff(dto.Group.GID).ConfigureAwait(false);
        if (!isMember && !isStaff)
        {
            return new SyncshellPublishingPolicyDto(dto.Group);
        }

        var policy = await LoadPolicyAsync(dto.Group.GID).ConfigureAwait(false);
        return new SyncshellPublishingPolicyDto(dto.Group, policy.NewsMode, policy.GalleryMode,
            policy.DirectPublishBits, policy.Version, policy.EventsMode, policy.CommentsMode);
    }

    /// <summary>Policy write - owner/moderator only; pushes a community refresh.</summary>
    [Authorize(Policy = "Identified")]
    public async Task<bool> GroupSetPublishingPolicy(SyncshellPublishingPolicyDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));

        if (!await HasCommunityRights(dto.Group.GID, SyncshellStaffPermission.ManageSettings).ConfigureAwait(false)) return false;

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

        welcomePage.PublishingPolicyJson = PublishingService.SerializePolicy(dto);
        welcomePage.UpdatedAt = DateTime.UtcNow;
        await DbContext.SaveChangesAsync().ConfigureAwait(false);
        await PushCommunityChangedAsync(dto.Group.GID).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Member/author submission path for community news. Unlike
    /// <see cref="GroupUpsertCommunityNews"/> (the staff editor) this always
    /// resolves through the shell policy - a member never chooses the final
    /// status, the server clamps publish/schedule intents into the review
    /// queue under ApprovalRequired and rejects writes under StaffOnly.
    /// </summary>
    [Authorize(Policy = "Identified")]
    public async Task<SyncshellNewsPostDto?> GroupSubmitCommunityNews(SyncshellNewsPostUpdateDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));

        var (isMember, _) = await TryValidateUserInGroup(dto.Group.GID).ConfigureAwait(false);
        if (!isMember) return null;

        var isStaff = await HasCommunityRights(dto.Group.GID, SyncshellStaffPermission.ManageNews)
            .ConfigureAwait(false);
        var staffBits = await GetStaffPermissionBits(dto.Group.GID, UserUID).ConfigureAwait(false);

        var entity = dto.Id == 0
            ? null
            : await DbContext.SyncshellCommunityNews
                .SingleOrDefaultAsync(n => n.Id == dto.Id && n.GroupGID == dto.Group.GID)
                .ConfigureAwait(false);

        // Editing a foreign post is staff work - members touch only their own.
        if (entity != null && !isStaff
            && !PublishingService.IsAuthorOf(UserUID, entity.AuthorUid,
                PublishingService.ParseCoAuthors(entity.CoAuthorUidsJson)))
        {
            return null;
        }

        var role = isStaff ? PublishingService.WriterRole.Moderator
            : entity == null || string.Equals(entity.AuthorUid, UserUID, StringComparison.Ordinal)
                ? PublishingService.WriterRole.Member
                : PublishingService.WriterRole.Author;

        var policy = await LoadPolicyAsync(dto.Group.GID).ConfigureAwait(false);
        var intent = dto.StatusIntent.HasValue
            ? (ContentPublishStatus)dto.StatusIntent.Value
            : ContentPublishStatus.Published;
        var resolution = _publishing.ResolveWrite(policy, "news", role, staffBits,
            intent, entity == null ? null : (ContentPublishStatus)entity.Status);
        if (resolution.Verdict == PublishingService.WriteVerdict.Denied)
        {
            _logger.LogCallWarning(SpheneHubLogger.Args(dto, resolution.DenialReason));
            return null;
        }

        if (entity == null)
        {
            entity = new SyncshellCommunityNewsPost
            {
                GroupGID = dto.Group.GID,
                AuthorUid = UserUID,
                // The entity initializer defaults Status to Published (legacy
                // live-post semantics) - a fresh submission always starts at
                // Draft so the transition gate below detects this publish.
                Status = (int)ContentPublishStatus.Draft,
                CreatedAt = DateTime.UtcNow
            };
            await DbContext.SyncshellCommunityNews.AddAsync(entity).ConfigureAwait(false);
        }

        entity.Title = dto.Title;
        entity.Body = dto.Body;
        if (isStaff)
        {
            entity.IsPinned = dto.IsPinned;
            if (dto.CoAuthorUids != null)
                entity.CoAuthorUidsJson = SerializeJson(dto.CoAuthorUids);
        }
        entity.UpdatedAt = DateTime.UtcNow;
        var prevStatus = (ContentPublishStatus)entity.Status;
        entity.Status = (int)resolution.EffectiveStatus;
        entity.IsDraft = resolution.EffectiveStatus == ContentPublishStatus.Draft;
        entity.ScheduledForUtc = resolution.EffectiveStatus == ContentPublishStatus.Scheduled
            ? dto.ScheduledForUtc ?? entity.ScheduledForUtc
            : null;
        if (prevStatus != ContentPublishStatus.Published
            && resolution.EffectiveStatus == ContentPublishStatus.Published)
        {
            entity.PublishedAt = DateTime.UtcNow;
        }

        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        if (resolution.EffectiveStatus == ContentPublishStatus.PendingApproval
            && prevStatus != ContentPublishStatus.PendingApproval)
        {
            var reviewers = await CollectReviewerUidsAsync(dto.Group.GID,
                SyncshellStaffPermission.ManageNews).ConfigureAwait(false);
            await _notificationWriter.PushManyAsync(reviewers,
                UserNotificationKind.ContentPendingReview,
                $"News pending review · {entity.Title}",
                entity.Body?.Length <= 200 ? entity.Body : entity.Body?[..200],
                UserUID, dto.Group.GID, "news", entity.Id.ToString(),
                uid => $"review:news:{entity.Id}:{uid}").ConfigureAwait(false);
        }

        if (prevStatus != ContentPublishStatus.Published
            && resolution.EffectiveStatus == ContentPublishStatus.Published)
        {
            await PushMentionNotificationsAsync(dto.Group.GID, "news", entity.Id,
                entity.Title, entity.Body).ConfigureAwait(false);
        }

        await PushCommunityChangedAsync(dto.Group.GID, SyncshellCommunitySection.News, entity.Id)
            .ConfigureAwait(false);
        return MapNews(entity);
    }

    /// <summary><c>@uid</c>/<c>@alias</c> mention fan-out - dedupe-keyed per
    /// (content, recipient) so edits never re-notify; fires only on the
    /// transition into <see cref="ContentPublishStatus.Published"/>.</summary>
    private async Task PushMentionNotificationsAsync(string gid, string kind, long id,
        string title, string? body)
    {
        var mentions = EntityReferenceService.ParseMentions(body);
        if (mentions.Count == 0) return;
        var uids = await DbContext.Users.AsNoTracking()
            .Where(u => mentions.Contains(u.UID)
                || (u.Alias != null && mentions.Contains(u.Alias)))
            .Select(u => u.UID).Distinct().ToListAsync().ConfigureAwait(false);
        uids.RemoveAll(u => string.Equals(u, UserUID, StringComparison.Ordinal)); // no self-pings
        if (uids.Count == 0) return;
        await _notificationWriter.PushManyAsync(uids, UserNotificationKind.Mention,
            $"Mentioned in {kind} · {title}", null, UserUID, gid, kind, id.ToString(),
            uid => $"mention:{kind}:{id}:{uid}").ConfigureAwait(false);
    }

    /// <summary>Staff decision on a queued news post - approve publishes, reject
    /// returns it to the author with the note attached.</summary>
    [Authorize(Policy = "Identified")]
    public async Task<bool> GroupDecideCommunityNews(CommunityReviewDecisionDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));

        var hasRights = await HasCommunityRights(dto.Group.GID, SyncshellStaffPermission.ManageNews)
            .ConfigureAwait(false);
        if (!hasRights) return false;

        var entity = await DbContext.SyncshellCommunityNews
            .SingleOrDefaultAsync(n => n.Id == dto.ContentId && n.GroupGID == dto.Group.GID)
            .ConfigureAwait(false);
        if (entity == null || entity.Status != (int)ContentPublishStatus.PendingApproval) return false;

        entity.ReviewedByUid = UserUID;
        entity.ReviewedAt = DateTime.UtcNow;
        entity.ReviewNote = dto.Note;
        entity.UpdatedAt = DateTime.UtcNow;
        if (dto.Approve)
        {
            entity.Status = (int)ContentPublishStatus.Published;
            entity.IsDraft = false;
            entity.PublishedAt = DateTime.UtcNow;
        }
        else
        {
            entity.Status = (int)ContentPublishStatus.Rejected;
        }
        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        if (entity.AuthorUid != null)
        {
            await _notificationWriter.PushAsync(DbContext, entity.AuthorUid,
                dto.Approve ? UserNotificationKind.CommunityNewsPublished : UserNotificationKind.ContentRejected,
                dto.Approve ? $"News published · {entity.Title}" : $"News rejected · {entity.Title}",
                dto.Note, UserUID, dto.Group.GID, "news", entity.Id.ToString(),
                $"decision:news:{entity.Id}:{dto.Approve}").ConfigureAwait(false);
        }

        if (dto.Approve)
        {
            await PushMentionNotificationsAsync(dto.Group.GID, "news", entity.Id,
                entity.Title, entity.Body).ConfigureAwait(false);
        }

        await PushCommunityChangedAsync(dto.Group.GID, SyncshellCommunitySection.News, entity.Id)
            .ConfigureAwait(false);
        return true;
    }

    /// <summary>The staff review queue - pending news + pending gallery images.</summary>
    [Authorize(Policy = "Identified")]
    public async Task<CommunityReviewQueueDto> GroupGetCommunityReviewQueue(GroupDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));

        var canNews = await HasCommunityRights(dto.Group.GID, SyncshellStaffPermission.ManageNews)
            .ConfigureAwait(false);
        var canGallery = await HasCommunityRights(dto.Group.GID, SyncshellStaffPermission.ManageGallery)
            .ConfigureAwait(false);
        var canEvents = await HasCommunityRights(dto.Group.GID, SyncshellStaffPermission.ManageEvents)
            .ConfigureAwait(false);
        var items = new List<CommunityReviewItemDto>();

        if (canNews)
        {
            var pending = await DbContext.SyncshellCommunityNews
                .Where(n => n.GroupGID == dto.Group.GID
                    && n.Status == (int)ContentPublishStatus.PendingApproval)
                .OrderBy(n => n.UpdatedAt)
                .ToListAsync().ConfigureAwait(false);
            items.AddRange(pending.Select(n => new CommunityReviewItemDto(
                "news", n.Id, n.GroupGID, n.Title,
                n.Body?.Length <= 140 ? n.Body : n.Body?[..140],
                n.AuthorUid == null ? null : new UserData(n.AuthorUid), n.UpdatedAt)));
        }

        if (canGallery)
        {
            var pending = await DbContext.SyncshellCommunityGallery
                .Where(g => g.GroupGID == dto.Group.GID
                    && g.Status == (int)Sphene.API.Data.Enum.SyncshellGalleryImageStatus.PendingApproval)
                .OrderBy(g => g.CreatedAt)
                .ToListAsync().ConfigureAwait(false);
            items.AddRange(pending.Select(g => new CommunityReviewItemDto(
                "gallery", g.Id, g.GroupGID,
                string.IsNullOrWhiteSpace(g.Caption) ? "Photo submission" : g.Caption!,
                g.FileName,
                g.UploadedByUid == null ? null : new UserData(g.UploadedByUid), g.CreatedAt)));
        }

        if (canEvents)
        {
            var pending = await DbContext.SyncshellCommunityEvents
                .Where(e => e.GroupGID == dto.Group.GID
                    && e.PublishStatus == (int)ContentPublishStatus.PendingApproval)
                .OrderBy(e => e.UpdatedAt)
                .ToListAsync().ConfigureAwait(false);
            items.AddRange(pending.Select(e => new CommunityReviewItemDto(
                "event", e.Id, e.GroupGID, e.Title,
                e.Location, e.AuthorUid == null ? null : new UserData(e.AuthorUid),
                e.UpdatedAt)));
        }

        return new CommunityReviewQueueDto(dto.Group,
            items.OrderBy(i => i.SubmittedAt).ToList());
    }

    /// <summary>
    /// Member photo submission - always lands in PendingApproval for
    /// non-staff; staff submissions publish straight through.
    /// </summary>
    [Authorize(Policy = "Identified")]
    public async Task<SyncshellGalleryImageDto?> GroupSubmitCommunityGalleryImage(
        SyncshellGalleryImageUpdateDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));

        var (isMember, _) = await TryValidateUserInGroup(dto.Group.GID).ConfigureAwait(false);
        if (!isMember) return null;

        var isStaff = await HasCommunityRights(dto.Group.GID, SyncshellStaffPermission.ManageGallery)
            .ConfigureAwait(false);
        var policy = await LoadPolicyAsync(dto.Group.GID).ConfigureAwait(false);
        var staffBits = await GetStaffPermissionBits(dto.Group.GID, UserUID).ConfigureAwait(false);
        var role = isStaff ? PublishingService.WriterRole.Moderator
            : PublishingService.WriterRole.Member;
        var resolution = _publishing.ResolveWrite(policy, "gallery", role, staffBits,
            ContentPublishStatus.Published, null);
        if (resolution.Verdict == PublishingService.WriteVerdict.Denied)
        {
            _logger.LogCallWarning(SpheneHubLogger.Args(dto, resolution.DenialReason));
            return null;
        }

        var entity = new SyncshellCommunityGalleryImage
        {
            GroupGID = dto.Group.GID,
            UploadedByUid = UserUID,
            CreatedAt = DateTime.UtcNow,
            Status = resolution.EffectiveStatus == ContentPublishStatus.Published
                ? (int)Sphene.API.Data.Enum.SyncshellGalleryImageStatus.Published
                : (int)Sphene.API.Data.Enum.SyncshellGalleryImageStatus.PendingApproval,
            Audience = dto.Audience ?? (int)Sphene.API.Data.Enum.SyncshellGalleryAudience.Everyone,
        };
        if (dto.AllowedUids != null)
        {
            entity.AllowedUidsJson = SerializeJson(dto.AllowedUids);
        }
        if (dto.ImageMediaHash != null)
        {
            entity.ImageMediaHash = dto.ImageMediaHash;
            entity.FileName = dto.FileName;
            entity.ContentType = dto.ContentType;
            entity.Size = dto.Size;
            await _communityMedia.RecordMediaAsync(DbContext, dto.ImageMediaHash,
                UserUID, dto.ContentType, dto.Size).ConfigureAwait(false);
        }
        else
        {
            entity.ImageBase64 = dto.ImageBase64;
            entity.FileName = dto.FileName;
            entity.ContentType = dto.ContentType;
            entity.Size = dto.Size;
        }
        entity.Caption = dto.Caption;
        entity.SortOrder = dto.SortOrder;
        entity.AlbumId = dto.AlbumId;

        await DbContext.SyncshellCommunityGallery.AddAsync(entity).ConfigureAwait(false);
        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        if (entity.Status == (int)Sphene.API.Data.Enum.SyncshellGalleryImageStatus.PendingApproval)
        {
            var reviewers = await CollectReviewerUidsAsync(dto.Group.GID,
                SyncshellStaffPermission.ManageGallery).ConfigureAwait(false);
            await _notificationWriter.PushManyAsync(reviewers,
                UserNotificationKind.ContentPendingReview,
                "Photo pending review",
                entity.Caption, UserUID, dto.Group.GID, "gallery", entity.Id.ToString(),
                uid => $"review:gallery:{entity.Id}:{uid}").ConfigureAwait(false);
        }

        await PushCommunityChangedAsync(dto.Group.GID, SyncshellCommunitySection.Gallery, entity.Id)
            .ConfigureAwait(false);
        return MapGallery(entity);
    }

    /// <summary>Staff decision on a queued photo - approve publishes; a reject
    /// deletes the row (the uploader keeps the bytes client-side).</summary>
    [Authorize(Policy = "Identified")]
    public async Task<bool> GroupDecideCommunityGalleryImage(CommunityReviewDecisionDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));

        var hasRights = await HasCommunityRights(dto.Group.GID, SyncshellStaffPermission.ManageGallery)
            .ConfigureAwait(false);
        if (!hasRights) return false;

        var entity = await DbContext.SyncshellCommunityGallery
            .SingleOrDefaultAsync(g => g.Id == dto.ContentId && g.GroupGID == dto.Group.GID)
            .ConfigureAwait(false);
        if (entity == null
            || entity.Status != (int)Sphene.API.Data.Enum.SyncshellGalleryImageStatus.PendingApproval)
        {
            return false;
        }

        var uploader = entity.UploadedByUid;
        if (dto.Approve)
        {
            entity.Status = (int)Sphene.API.Data.Enum.SyncshellGalleryImageStatus.Published;
            await DbContext.SaveChangesAsync().ConfigureAwait(false);
        }
        else
        {
            DbContext.SyncshellCommunityGallery.Remove(entity);
            await DbContext.SaveChangesAsync().ConfigureAwait(false);
        }

        if (uploader != null)
        {
            await _notificationWriter.PushAsync(DbContext, uploader,
                dto.Approve ? UserNotificationKind.CommunityNewsPublished : UserNotificationKind.ContentRejected,
                dto.Approve ? "Photo published" : "Photo rejected",
                dto.Note ?? entity.Caption, UserUID, dto.Group.GID, "gallery",
                dto.ContentId.ToString(), $"decision:gallery:{dto.ContentId}:{dto.Approve}")
                .ConfigureAwait(false);
        }

        await PushCommunityChangedAsync(dto.Group.GID, SyncshellCommunitySection.Gallery,
            dto.ContentId).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Server-side entity reference resolution - markdown tokens become
    /// visibility-checked cards; invisible targets resolve to kind-only
    /// placeholders so existence/content never leaks to the caller.
    /// </summary>
    [Authorize(Policy = "Identified")]
    public async Task<List<EntityCardDto>> EntityResolveReferences(List<string> tokens)
    {
        _logger.LogCallInfo();
        if (tokens.Count == 0) return [];

        var allPaired = await GetAllPairedUnpausedUsers().ConfigureAwait(false);
        return await _entityRefs.ResolveAsync(UserUID, tokens,
            canViewGroup: async gid =>
            {
                var (member, _) = await TryValidateUserInGroup(gid).ConfigureAwait(false);
                if (member) return true;
                if (await IsCommunityStaff(gid).ConfigureAwait(false)) return true;
                return await DbContext.SyncshellWelcomePages.AsNoTracking()
                    .AnyAsync(w => w.GroupGID == gid && w.IsListedPublicly && w.IsEnabled)
                    .ConfigureAwait(false);
            },
            canViewBlog: uid =>
            {
                if (string.Equals(uid, UserUID, StringComparison.Ordinal)) return Task.FromResult(true);
                return Task.FromResult(allPaired.Contains(uid, StringComparer.Ordinal));
            }).ConfigureAwait(false);
    }

    /// <summary>Community media upload channel - how/where the client ships
    /// the binary before referencing it by hash in a write DTO.</summary>
    [Authorize(Policy = "Identified")]
    public async Task<CommunityMediaUploadInfoDto> GroupGetCommunityMediaUploadInfo(GroupDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));

        var (isMember, _) = await TryValidateUserInGroup(dto.Group.GID).ConfigureAwait(false);
        if (!isMember && !await IsCommunityStaff(dto.Group.GID).ConfigureAwait(false))
        {
            return new CommunityMediaUploadInfoDto(false, null, 0, []);
        }

        var (upload, fetch) = _communityMedia.GetMediaUrlTemplates();
        return new CommunityMediaUploadInfoDto(upload != null,
            upload?.ToString() + "{hash}",
            CommunityMediaService.MaxImageBytes,
            CommunityMediaService.AllowedTypes.ToList(),
            fetch?.ToString() + "{hash}");
    }

    // ------------------------------------------------------------------
    //  helpers
    // ------------------------------------------------------------------

    private async Task<SyncshellPublishingPolicy> LoadPolicyAsync(string gid)
    {
        var json = await DbContext.SyncshellWelcomePages.AsNoTracking()
            .Where(w => w.GroupGID == gid)
            .Select(w => w.PublishingPolicyJson)
            .SingleOrDefaultAsync().ConfigureAwait(false);
        return PublishingService.ParsePolicy(json);
    }

    /// <summary>
    /// Review-queue notification recipients - owner, moderators and any
    /// staff assignee holding <paramref name="permission"/>.
    /// </summary>
    private async Task<List<string>> CollectReviewerUidsAsync(string gid, SyncshellStaffPermission permission)
    {
        var uids = await DbContext.GroupPairs.AsNoTracking()
            .Where(p => p.GroupGID == gid && p.IsModerator)
            .Select(p => p.GroupUserUID)
            .ToListAsync().ConfigureAwait(false);

        var owner = await DbContext.Groups.AsNoTracking()
            .Where(g => g.GID == gid).Select(g => g.OwnerUID)
            .SingleOrDefaultAsync().ConfigureAwait(false);
        if (owner != null) uids.Add(owner);

        var bitHolders = await DbContext.SyncshellCommunityStaffAssignments.AsNoTracking()
            .Where(a => a.GroupGID == gid)
            .Join(DbContext.SyncshellCommunityStaffRoles.Where(r => r.GroupGID == gid),
                a => a.RoleId, r => r.Id, (a, r) => new { a.UserUID, r.Permissions })
            .Where(x => (x.Permissions & (long)permission) != 0)
            .Select(x => x.UserUID)
            .ToListAsync().ConfigureAwait(false);
        uids.AddRange(bitHolders);
        return uids.Distinct(StringComparer.Ordinal).ToList();
    }
}

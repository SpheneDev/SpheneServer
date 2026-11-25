using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Sphene.API.Data;
using Sphene.API.Data.Enum;
using Sphene.API.Dto.Group;
using Sphene.API.Dto.User;
using SpheneServer.Services;
using SpheneServer.Utils;
using SpheneShared.Models;

namespace SpheneServer.Hubs;

/// <summary>
/// Phase 4 - comments, reactions, reports and moderation for community
/// news/events and personal blog posts.
///
/// Authorization flow on every endpoint:
///   1. ResolveTargetAsync - loads the host row and the caller's access
///      (uniform not-found for invisible blog posts; community targets
///      require membership).
///   2. Capability - member writes (comments_mode policy), author edits,
///      or moderation scope (community staff bit / blog owner / platform).
/// Blog paths NEVER consult community staff bits; community paths NEVER
/// consult User.IsModerator for blog rows.
/// </summary>
public partial class SpheneHub
{
    private const int CommentPageSizeMax = 50;

    /// <summary>Resolved access for one comment thread host.</summary>
    private sealed record CommentTargetAccess(
        CommentTargetKind Kind,
        long TargetId,
        string? GroupGid,
        string? OwnerUid,
        string? TargetAuthorUid,
        bool CanView,
        bool CanWrite,
        bool CommentsLocked,
        CommunityCommentService.ModerationScope ModerationScope,
        string? DenyReason = null);

    // ------------------------------------------------------------------
    //  Reads - keyset-paginated roots and replies
    // ------------------------------------------------------------------

    /// <summary>Root comments page - newest first, keyset cursor.</summary>
    [Authorize(Policy = "Identified")]
    public async Task<CommentPageDto> GetComments(CommentPageRequestDto dto)
    {
        var access = await ResolveCommentTargetAsync(dto.TargetKind, dto.TargetId,
            dto.OwnerUid, dto.GroupGID).ConfigureAwait(false);
        var empty = new CommentPageDto(dto.TargetKind, dto.TargetId, [], [], null, 0);
        if (!access.CanView) return empty;

        var pageSize = Math.Clamp(dto.PageSize, 1, CommentPageSizeMax);
        var query = DbContext.CommunityComments.AsNoTracking()
            .Where(c => c.TargetKind == (int)dto.TargetKind && c.TargetId == dto.TargetId
                && c.ParentId == null);
        if (dto.Cursor is > 0) query = query.Where(c => c.Id < dto.Cursor.Value);

        var roots = await query.OrderByDescending(c => c.Id).Take(pageSize + 1)
            .ToListAsync().ConfigureAwait(false);
        var next = roots.Count > pageSize ? roots[^1].Id : (long?)null;
        if (roots.Count > pageSize) roots.RemoveAt(roots.Count - 1);

        var total = await DbContext.CommunityComments
            .CountAsync(c => c.TargetKind == (int)dto.TargetKind && c.TargetId == dto.TargetId
                && c.ParentId == null).ConfigureAwait(false);

        var previews = await LoadReplyPreviewsAsync(
            roots.Select(r => r.Id).ToList()).ConfigureAwait(false);
        var authors = await UserDataByUidAsync(roots
            .Concat(previews.SelectMany(kv => kv.Value))
            .Select(c => c.AuthorUid)).ConfigureAwait(false);
        var staff = access.ModerationScope != CommunityCommentService.ModerationScope.None;
        return new(dto.TargetKind, dto.TargetId,
            roots.Select(c => ToCommentDto(c, staff, authors)).ToList(),
            previews.ToDictionary(kv => kv.Key,
                kv => kv.Value.Select(c => ToCommentDto(c, staff, authors)).ToList()),
            next, total);
    }

    /// <summary>One thread's replies - oldest first under <see cref="CommentPageRequestDto.RootId"/>.</summary>
    [Authorize(Policy = "Identified")]
    public async Task<CommentPageDto> GetCommentReplies(CommentPageRequestDto dto)
    {
        var access = await ResolveCommentTargetAsync(dto.TargetKind, dto.TargetId,
            dto.OwnerUid, dto.GroupGID).ConfigureAwait(false);
        var empty = new CommentPageDto(dto.TargetKind, dto.TargetId, [], [], null, 0);
        if (!access.CanView || dto.RootId is not > 0) return empty;

        var pageSize = Math.Clamp(dto.PageSize, 1, CommentPageSizeMax);
        var query = DbContext.CommunityComments.AsNoTracking()
            .Where(c => c.TargetKind == (int)dto.TargetKind && c.TargetId == dto.TargetId
                && c.RootId == dto.RootId.Value);
        if (dto.Cursor is > 0) query = query.Where(c => c.Id > dto.Cursor.Value);

        var replies = await query.OrderBy(c => c.Id).Take(pageSize + 1)
            .ToListAsync().ConfigureAwait(false);
        var next = replies.Count > pageSize ? replies[^1].Id : (long?)null;
        if (replies.Count > pageSize) replies.RemoveAt(replies.Count - 1);

        var authors = await UserDataByUidAsync(replies.Select(c => c.AuthorUid))
            .ConfigureAwait(false);
        var staff = access.ModerationScope != CommunityCommentService.ModerationScope.None;
        return new(dto.TargetKind, dto.TargetId,
            replies.Select(c => ToCommentDto(c, staff, authors)).ToList(), [], next, replies.Count);
    }

    /// <summary>Latest 3 replies per root (ascending id) for thread previews.</summary>
    private async Task<Dictionary<long, List<CommunityComment>>> LoadReplyPreviewsAsync(List<long> rootIds)
    {
        if (rootIds.Count == 0) return [];
        var rows = await DbContext.CommunityComments.AsNoTracking()
            .Where(c => c.RootId != null && rootIds.Contains(c.RootId.Value))
            .OrderBy(c => c.Id).ToListAsync().ConfigureAwait(false);
        var map = new Dictionary<long, List<CommunityComment>>();
        foreach (var c in rows)
        {
            if (!map.TryGetValue(c.RootId!.Value, out var list)) map[c.RootId.Value] = list = [];
            if (list.Count < 3) list.Add(c);
        }
        return map;
    }

    // ------------------------------------------------------------------
    //  Writes - upsert (create/reply/edit) + author delete
    // ------------------------------------------------------------------

    /// <summary>Create a root comment/reply or edit an existing one.</summary>
    [Authorize(Policy = "Identified")]
    public async Task<CommunityCommentDto?> UpsertComment(CommentUpsertDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));
        if (dto.CommentId is > 0)
        {
            var existing = await DbContext.CommunityComments.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == dto.CommentId.Value).ConfigureAwait(false);
            if (existing == null) return null;
            var editAccess = await ResolveCommentTargetAsync((CommentTargetKind)existing.TargetKind,
                existing.TargetId, dto.OwnerUid, dto.GroupGID).ConfigureAwait(false);
            if (!editAccess.CanView) return null;
            var edited = await CommunityCommentService.EditAsync(DbContext, existing.Id,
                UserUID, dto.Body, DateTime.UtcNow).ConfigureAwait(false);
            if (!edited.Success || edited.Comment == null) return null;
            var editAuthors = await UserDataByUidAsync(new[] { edited.Comment.AuthorUid })
                .ConfigureAwait(false);
            return ToCommentDto(edited.Comment, editAccess.ModerationScope
                != CommunityCommentService.ModerationScope.None, editAuthors);
        }

        var access = await ResolveCommentTargetAsync(dto.TargetKind, dto.TargetId,
            dto.OwnerUid, dto.GroupGID).ConfigureAwait(false);
        if (!access.CanView || !access.CanWrite) return null;

        var outcome = await CommunityCommentService.CreateAsync(DbContext, dto.TargetKind,
            dto.TargetId, access.GroupGid, access.OwnerUid, UserUID, dto.Body,
            dto.ParentId, access.ModerationScope != CommunityCommentService.ModerationScope.None,
            DateTime.UtcNow).ConfigureAwait(false);
        if (!outcome.Success || outcome.Comment == null) return null;

        var comment = outcome.Comment;
        await NotifyCommentAsync(comment, outcome).ConfigureAwait(false);
        if (access.GroupGid != null)
        {
            await PushCommunityChangedAsync(access.GroupGid,
                dto.TargetKind == CommentTargetKind.Event
                    ? SyncshellCommunitySection.Events : SyncshellCommunitySection.News,
                dto.TargetId).ConfigureAwait(false);
        }
        else if (access.OwnerUid != null)
        {
            await PushUserProfileChangedAsync(access.OwnerUid).ConfigureAwait(false);
        }
        var authors = await UserDataByUidAsync(new[] { comment.AuthorUid })
            .ConfigureAwait(false);
        return ToCommentDto(comment,
            access.ModerationScope != CommunityCommentService.ModerationScope.None, authors);
    }

    /// <summary>Author soft-delete - tombstone preserving the thread shape.</summary>
    [Authorize(Policy = "Identified")]
    public async Task<bool> DeleteComment(CommentUpsertDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));
        if (dto.CommentId is not > 0) return false;
        var existing = await DbContext.CommunityComments.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == dto.CommentId.Value).ConfigureAwait(false);
        if (existing == null) return false;
        var access = await ResolveCommentTargetAsync((CommentTargetKind)existing.TargetKind,
            existing.TargetId, dto.OwnerUid, dto.GroupGID).ConfigureAwait(false);
        if (!access.CanView) return false;
        var outcome = await CommunityCommentService.DeleteByAuthorAsync(DbContext,
            existing.Id, UserUID, DateTime.UtcNow).ConfigureAwait(false);
        if (outcome is { Success: true, Changed: true })
        {
            if (access.GroupGid != null)
            {
                await PushCommunityChangedAsync(access.GroupGid,
                    existing.TargetKind == (int)CommentTargetKind.Event
                        ? SyncshellCommunitySection.Events : SyncshellCommunitySection.News,
                    existing.TargetId).ConfigureAwait(false);
            }
            else if (access.OwnerUid != null)
            {
                await PushUserProfileChangedAsync(access.OwnerUid).ConfigureAwait(false);
            }
        }
        return outcome.Success;
    }

    // ------------------------------------------------------------------
    //  Moderation - hide/restore/remove + thread lock
    // ------------------------------------------------------------------

    /// <summary>Moderation transition on one comment or the whole thread.</summary>
    [Authorize(Policy = "Identified")]
    public async Task<CommunityCommentDto?> SetCommentModeration(CommentModerationDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));
        var access = await ResolveCommentTargetAsync(dto.TargetKind, dto.TargetId,
            dto.OwnerUid, dto.Group?.GID).ConfigureAwait(false);
        var scope = access.ModerationScope;
        if (scope == CommunityCommentService.ModerationScope.None) return null;

        var action = (CommunityCommentService.ModAction)dto.Action;
        var outcome = await CommunityCommentService.ModerateAsync(DbContext, dto.TargetKind,
            dto.TargetId, dto.CommentId, action, UserUID, scope, dto.Reason,
            DateTime.UtcNow).ConfigureAwait(false);
        if (!outcome.Success) return null;

        if (outcome.Changed && outcome.Comment != null
            && outcome.Comment.AuthorUid != UserUID
            && action is CommunityCommentService.ModAction.Hide
                or CommunityCommentService.ModAction.Remove
                or CommunityCommentService.ModAction.Restore)
        {
            await _notificationWriter.PushAsync(DbContext, outcome.Comment.AuthorUid,
                UserNotificationKind.CommentModerated,
                $"Your comment was {action.ToString().ToLowerInvariant()}ed",
                dto.Reason, UserUID, access.GroupGid,
                dto.TargetKind.ToString().ToLowerInvariant(), dto.TargetId.ToString(),
                $"mod:{action}:{outcome.Comment.Id}",
                subContentId: outcome.Comment.Id.ToString(),
                targetUid: access.TargetAuthorUid).ConfigureAwait(false);
        }
        if (outcome.Changed)
        {
            if (access.GroupGid != null)
            {
                await PushCommunityChangedAsync(access.GroupGid,
                    dto.TargetKind == CommentTargetKind.Event
                        ? SyncshellCommunitySection.Events : SyncshellCommunitySection.News,
                    dto.TargetId).ConfigureAwait(false);
            }
            else if (access.OwnerUid != null)
            {
                await PushUserProfileChangedAsync(access.OwnerUid).ConfigureAwait(false);
            }
        }
        if (outcome.Comment == null)
        {
            return new CommunityCommentDto(
                dto.CommentId, dto.TargetKind, dto.TargetId, access.GroupGid, null, null,
                CommentState.Visible, null, null, 0, DateTime.UtcNow, null, false, true);
        }
        var modAuthors = await UserDataByUidAsync(new[] { outcome.Comment.AuthorUid })
            .ConfigureAwait(false);
        return ToCommentDto(outcome.Comment, true, modAuthors);
    }

    // ------------------------------------------------------------------
    //  Reactions - state-based writes + aggregate/who reads
    // ------------------------------------------------------------------

    /// <summary>Set=true ensures the reaction exists; Set=false ensures it
    /// doesn't. Idempotent - the PK is the uniqueness authority.</summary>
    [Authorize(Policy = "Identified")]
    public async Task<ReactionSummaryDto> SetReaction(ReactionSetDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));
        var (kind, hostId, access, comment) = await ResolveReactionTargetAsync(dto)
            .ConfigureAwait(false);
        if (access == null || !access.CanView)
        {
            return new(dto.TargetKind, dto.TargetId, [], [], 0);
        }

        var emoji = ResolveReactionEmoji(dto);
        if (emoji.Length == 0)
        {
            return new(dto.TargetKind, dto.TargetId, [], [], 0);
        }
        // Toggle mode decides the direction from the stored row, not the
        // client's guess - feed rows cache viewer state that can lag behind
        // writes made in other surfaces, and a stale guess flipped the write
        // into a no-op or a re-add of something the user meant to remove
        var set = dto.Set;
        if (dto.Toggle)
        {
            var exists = await DbContext.CommunityReactions.AsNoTracking()
                .AnyAsync(r => r.TargetKind == kind && r.TargetId == hostId
                    && r.UserUID == UserUID && r.EmojiSeq == emoji)
                .ConfigureAwait(false);
            set = !exists;
        }
        var outcome = await CommunityReactionService.ApplyAsync(DbContext, kind, hostId,
            UserUID, emoji, set, access.GroupGid, access.OwnerUid,
            DateTime.UtcNow).ConfigureAwait(false);

        // Reactions notify the author once per user/kind/target - the durable
        // dedupe key means remove+re-add never re-notifies. Comment reactions
        // go to the comment's author with the thread host as link container
        // and the comment id as sub target - the deep-link lands inside the
        // thread, not just on the post.
        var notifyUid = comment?.AuthorUid ?? access.TargetAuthorUid;
        if (outcome.Inserted && notifyUid != null
            && !string.Equals(notifyUid, UserUID, StringComparison.Ordinal))
        {
            var linkKind = comment != null
                ? ((CommentTargetKind)comment.TargetKind).ToString().ToLowerInvariant()
                : dto.TargetKind.ToString().ToLowerInvariant();
            await _notificationWriter.PushAsync(DbContext, notifyUid,
                UserNotificationKind.Reaction,
                $"{emoji} on your {dto.TargetKind.ToString().ToLowerInvariant()}",
                null, UserUID, comment?.GroupGID ?? access.GroupGid,
                linkKind, comment?.TargetId.ToString() ?? hostId.ToString(),
                $"reaction:{kind}:{hostId}:{UserUID}:{emoji}",
                subContentId: comment?.Id.ToString(),
                targetUid: access.TargetAuthorUid).ConfigureAwait(false);
        }
        if (outcome.Inserted || outcome.Removed)
        {
            if (access.GroupGid != null)
            {
                // No section: comment-level reactions would carry the comment
                // id, not the host id - clients invalidate their open thread
                // caches for the whole shell page on a section-less push.
                // Marked reactions-only: feeds patch their own toggles
                // locally, so nobody needs a wholesale reload for a count.
                await PushCommunityChangedAsync(access.GroupGid,
                    reactionsOnly: true,
                    reactionTargetKind: comment == null ? kind : null,
                    reactionTargetId: comment == null ? hostId : null).ConfigureAwait(false);
            }
            else if (access.OwnerUid != null)
            {
                // Fine-grained reaction push - the coarse profile invalidation
                // made every viewer's page collapse into a reload for a count
                // change. Clients patch thread summaries and feed rows in
                // place; the host coordinates ride along even for
                // comment-level reactions because the thread is keyed on the
                // host, never the comment.
                var pushKind = comment != null ? comment.TargetKind : kind;
                var pushId = comment != null ? comment.TargetId : hostId;
                await PushUserReactionChangedAsync(access.OwnerUid, pushKind,
                    pushId, comment != null).ConfigureAwait(false);
            }
        }
        return await BuildReactionSummaryAsync(kind, hostId).ConfigureAwait(false);
    }

    /// <summary>Aggregate counts + viewer's own kinds for one target.</summary>
    [Authorize(Policy = "Identified")]
    public async Task<ReactionSummaryDto> GetReactionSummary(ReactionSetDto dto)
    {
        var (kind, hostId, access, _) = await ResolveReactionTargetAsync(dto).ConfigureAwait(false);
        if (access == null || !access.CanView)
        {
            return new(dto.TargetKind, dto.TargetId, [], [], 0);
        }
        return await BuildReactionSummaryAsync(kind, hostId).ConfigureAwait(false);
    }

    /// <summary>Who reacted - newest first, cursor-paginated.</summary>
    [Authorize(Policy = "Identified")]
    public async Task<ReactionUsersDto> GetReactionUsers(ReactionUsersRequestDto dto)
    {
        var probe = new ReactionSetDto(dto.TargetKind, dto.TargetId,
            dto.Kind ?? ReactionKind.Like, true, dto.TargetId, dto.OwnerUid, dto.Group);
        var (kind, hostId, access, _) = await ResolveReactionTargetAsync(probe).ConfigureAwait(false);
        if (access == null || !access.CanView) return new(dto.TargetKind, dto.TargetId, [], null);

        var pageSize = Math.Clamp(dto.PageSize, 1, CommentPageSizeMax);
        var query = DbContext.CommunityReactions.AsNoTracking()
            .Where(r => r.TargetKind == kind && r.TargetId == hostId);
        if (!string.IsNullOrWhiteSpace(dto.Emoji))
        {
            query = query.Where(r => r.EmojiSeq == dto.Emoji);
        }
        else if (dto.Kind != null)
        {
            var legacyEmoji = ReactionEmojiMap.ForKind(dto.Kind.Value);
            query = query.Where(r => r.EmojiSeq == legacyEmoji);
        }
        if (dto.Cursor != null) query = query.Where(r => r.CreatedAt < dto.Cursor.Value);

        var rows = await query.OrderByDescending(r => r.CreatedAt).Take(pageSize + 1)
            .Select(r => new { r.UserUID, r.CreatedAt }).ToListAsync().ConfigureAwait(false);
        var next = rows.Count > pageSize ? rows[^1].CreatedAt : (DateTime?)null;
        if (rows.Count > pageSize) rows.RemoveAt(rows.Count - 1);

        var uids = rows.Select(r => r.UserUID).ToList();
        var users = await DbContext.Users.AsNoTracking()
            .Where(u => uids.Contains(u.UID)).ToListAsync().ConfigureAwait(false);
        var byUid = users.ToDictionary(u => u.UID, u => u.ToUserData());
        return new(dto.TargetKind, dto.TargetId,
            uids.Where(byUid.ContainsKey).Select(u => byUid[u]).ToList(), next);
    }

    /// <summary>Resolves a reaction target: for Comment targets the reaction's
    /// host is the COMMENT row and visibility comes from ITS thread target.
    /// <see cref="CommunityComment"/> rides along so callers can address the
    /// comment's author and thread host without a second fetch.</summary>
    private async Task<(int StoredKind, long StoredId, CommentTargetAccess? Access,
        CommunityComment? Comment)> ResolveReactionTargetAsync(ReactionSetDto dto)
    {
        if (dto.TargetKind == ReactionTargetKind.Comment)
        {
            var host = await DbContext.CommunityComments.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == dto.TargetId).ConfigureAwait(false);
            if (host == null) return (0, 0, null, null);
            var access = await ResolveCommentTargetAsync((CommentTargetKind)host.TargetKind,
                host.TargetId, dto.OwnerUid, dto.Group?.GID).ConfigureAwait(false);
            // Comment reactions store kind=Comment + the comment id; the ACL
            // is the thread's - same visibility as reading the comment.
            return ((int)ReactionTargetKind.Comment, dto.TargetId, access, host);
        }
        var commentKind = dto.TargetKind switch
        {
            ReactionTargetKind.News => CommentTargetKind.News,
            ReactionTargetKind.Event => CommentTargetKind.Event,
            ReactionTargetKind.Blog => CommentTargetKind.Blog,
            _ => (CommentTargetKind)(-1),
        };
        if (commentKind < 0) return (0, 0, null, null);
        var acc = await ResolveCommentTargetAsync(commentKind, dto.TargetId,
            dto.OwnerUid, dto.Group?.GID).ConfigureAwait(false);
        return ((int)dto.TargetKind, dto.TargetId, acc, null);
    }

    private async Task<ReactionSummaryDto> BuildReactionSummaryAsync(int kind, long hostId)
    {
        var (emojiCounts, viewerEmojis, counts, viewerKinds, total) = await CommunityReactionService
            .SummaryAsync(DbContext, kind, hostId, UserUID).ConfigureAwait(false);
        return new((ReactionTargetKind)kind, hostId, counts, viewerKinds, total,
            emojiCounts, viewerEmojis);
    }

    /// <summary>Emoji writes win; legacy kind writes translate through the
    /// canonical map, so both client generations land on the same row.
    /// Bounded length keeps the identity column honest.</summary>
    private static string ResolveReactionEmoji(ReactionSetDto dto)
    {
        var emoji = string.IsNullOrWhiteSpace(dto.Emoji)
            ? ReactionEmojiMap.ForKind(dto.Kind)
            : dto.Emoji.Trim();
        return emoji.Length is > 0 and <= 24 ? emoji : string.Empty;
    }

    // ------------------------------------------------------------------
    //  Reports + moderation queues
    // ------------------------------------------------------------------

    /// <summary>Reporter files a report - deduped by the partial unique index.</summary>
    [Authorize(Policy = "Identified")]
    public async Task<bool> ReportContent(ReportCreateDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));
        var (commentKind, hostId) = dto.TargetKind switch
        {
            ReportTargetKind.Comment => ((CommentTargetKind?)null, 0L),
            ReportTargetKind.News => (CommentTargetKind.News, dto.TargetId),
            ReportTargetKind.Event => (CommentTargetKind.Event, dto.TargetId),
            ReportTargetKind.Blog => (CommentTargetKind.Blog, dto.TargetId),
            _ => ((CommentTargetKind?)null, 0L),
        };

        string? groupGid = null, ownerUid = null;
        if (dto.TargetKind == ReportTargetKind.Comment)
        {
            var comment = await DbContext.CommunityComments.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == dto.TargetId).ConfigureAwait(false);
            if (comment == null) return false;
            var access = await ResolveCommentTargetAsync((CommentTargetKind)comment.TargetKind,
                comment.TargetId, dto.OwnerUid, dto.Group?.GID).ConfigureAwait(false);
            if (!access.CanView) return false; // invisible target → no existence leak
            groupGid = comment.GroupGID;
            ownerUid = comment.OwnerUid;
        }
        else
        {
            var access = await ResolveCommentTargetAsync(commentKind!.Value, hostId,
                dto.OwnerUid, dto.Group?.GID).ConfigureAwait(false);
            if (!access.CanView) return false;
            groupGid = access.GroupGid;
            ownerUid = access.OwnerUid;
        }

        var report = new CommunityReport
        {
            TargetKind = (int)dto.TargetKind,
            TargetId = dto.TargetId,
            GroupGID = groupGid,
            OwnerUid = ownerUid,
            ReporterUid = UserUID,
            Reason = (int)dto.Reason,
            Details = dto.Details?.Trim(),
            State = (int)ReportState.Open,
            CreatedAt = DateTime.UtcNow,
        };
        DbContext.CommunityReports.Add(report);
        CommunityCommentService.Audit(DbContext, UserUID,
            (int)CommunityCommentService.ModerationScope.None, "report_create",
            (int)dto.TargetKind, dto.TargetId, null, groupGid, ownerUid, dto.Reason.ToString(),
            DateTime.UtcNow);
        try
        {
            await DbContext.SaveChangesAsync().ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            return true; // duplicate open report - the unique index already deduped
        }
        return true;
    }

    /// <summary>Community moderation queue - open reports for one shell.</summary>
    [Authorize(Policy = "Identified")]
    public async Task<ModerationQueueDto> GetModerationQueue(GroupDto dto)
    {
        var canModerate = await HasCommunityRights(dto.Group.GID,
            SyncshellStaffPermission.ModerateContent).ConfigureAwait(false);
        if (!canModerate) return new(dto.Group, []);

        var rows = await DbContext.CommunityReports.AsNoTracking()
            .Where(r => r.GroupGID == dto.Group.GID && r.State == (int)ReportState.Open)
            .OrderBy(r => r.Id).Take(100).ToListAsync().ConfigureAwait(false);
        return new(dto.Group, await MapReportsAsync(rows).ConfigureAwait(false));
    }

    /// <summary>Resolve/dismiss a community report - ModerateContent only.</summary>
    [Authorize(Policy = "Identified")]
    public async Task<bool> ResolveReport(ReportDecisionDto dto)
    {
        var report = await DbContext.CommunityReports
            .FirstOrDefaultAsync(r => r.Id == dto.ReportId).ConfigureAwait(false);
        if (report == null || report.GroupGID == null) return false;
        var canModerate = await HasCommunityRights(report.GroupGID,
            SyncshellStaffPermission.ModerateContent).ConfigureAwait(false);
        if (!canModerate || report.State != (int)ReportState.Open) return false;
        return await ApplyReportDecisionAsync(report, dto,
            CommunityCommentService.ModerationScope.CommunityStaff).ConfigureAwait(false);
    }

    /// <summary>Platform queue - blog reports (User.IsModerator only).</summary>
    [Authorize(Policy = "Identified")]
    public async Task<PlatformReportQueueDto> GetPlatformReportQueue()
    {
        if (!await IsPlatformModeratorAsync().ConfigureAwait(false))
            return new([]);
        var rows = await DbContext.CommunityReports.AsNoTracking()
            .Where(r => r.OwnerUid != null && r.GroupGID == null
                && r.State == (int)ReportState.Open)
            .OrderBy(r => r.Id).Take(100).ToListAsync().ConfigureAwait(false);
        return new(await MapReportsAsync(rows).ConfigureAwait(false));
    }

    /// <summary>Resolve/dismiss a platform-scoped (blog) report.</summary>
    [Authorize(Policy = "Identified")]
    public async Task<bool> ResolvePlatformReport(ReportDecisionDto dto)
    {
        if (!await IsPlatformModeratorAsync().ConfigureAwait(false)) return false;
        var report = await DbContext.CommunityReports
            .FirstOrDefaultAsync(r => r.Id == dto.ReportId).ConfigureAwait(false);
        if (report == null || report.OwnerUid == null || report.GroupGID != null
            || report.State != (int)ReportState.Open) return false;
        return await ApplyReportDecisionAsync(report, dto,
            CommunityCommentService.ModerationScope.Platform).ConfigureAwait(false);
    }

    private async Task<bool> ApplyReportDecisionAsync(CommunityReport report,
        ReportDecisionDto dto, CommunityCommentService.ModerationScope scope)
    {
        report.State = (int)(dto.Resolve ? ReportState.Resolved : ReportState.Dismissed);
        report.ResolvedByUid = UserUID;
        report.ResolvedAt = DateTime.UtcNow;
        report.ResolutionNote = dto.Note?.Trim();
        CommunityCommentService.Audit(DbContext, UserUID, (int)scope,
            dto.Resolve ? "report_resolve" : "report_dismiss",
            report.TargetKind, report.TargetId, null, report.GroupGID, report.OwnerUid,
            dto.Note, DateTime.UtcNow, report.Id);
        await DbContext.SaveChangesAsync().ConfigureAwait(false);
        if (!string.Equals(report.ReporterUid, UserUID, StringComparison.Ordinal))
        {
            await _notificationWriter.PushAsync(DbContext, report.ReporterUid,
                UserNotificationKind.ReportOutcome,
                dto.Resolve ? "Your report was resolved" : "Your report was dismissed",
                dto.Note, null, report.GroupGID,
                "report", report.Id.ToString(),
                $"report-outcome:{report.Id}").ConfigureAwait(false);
        }
        return true;
    }

    /// <summary>User-facing notification mute mask - makes the enforced
    /// NotificationDisabledKinds field settable from the client.</summary>
    [Authorize(Policy = "Identified")]
    public async Task<bool> UserSetNotificationMutes(long disabledKindsMask)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(disabledKindsMask));
        var user = await DbContext.Users.SingleOrDefaultAsync(u => u.UID == UserUID)
            .ConfigureAwait(false);
        if (user == null) return false;
        user.NotificationDisabledKinds = disabledKindsMask;
        await DbContext.SaveChangesAsync().ConfigureAwait(false);
        return true;
    }

    /// <summary>Reads the caller's notification mute mask - the read path
    /// the v38 setter lacked; settings UIs render per-kind checkboxes over it.</summary>
    [Authorize(Policy = "Identified")]
    public async Task<long> UserGetNotificationMutes()
    {
        return await DbContext.Users.AsNoTracking()
            .Where(u => u.UID == UserUID)
            .Select(u => u.NotificationDisabledKinds)
            .SingleOrDefaultAsync().ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    //  Target resolution - the single ACL authority
    // ------------------------------------------------------------------

    /// <summary>
    /// Loads the comment host row and resolves the caller's access. Invisible
    /// blog posts return CanView=false with no distinguishing error - reads,
    /// writes, summaries and reports all funnel through this one path so no
    /// endpoint can leak the existence of an inaccessible post.
    /// </summary>
    private async Task<CommentTargetAccess> ResolveCommentTargetAsync(
        CommentTargetKind kind, long targetId, string? ownerUid, string? groupGid)
    {
        switch (kind)
        {
            case CommentTargetKind.News:
            {
                var post = await DbContext.SyncshellCommunityNews.AsNoTracking()
                    .FirstOrDefaultAsync(n => n.Id == targetId).ConfigureAwait(false);
                if (post == null) return Denied(kind, targetId);
                var (isMember, _) = await TryValidateUserInGroup(post.GroupGID).ConfigureAwait(false);
                if (!isMember || !PublishingService.IsVisible(post.Status, post.ScheduledForUtc, DateTime.UtcNow))
                    return Denied(kind, targetId);
                var scope = await CommunityModScopeAsync(post.GroupGID).ConfigureAwait(false);
                var canWrite = await CommentWriteAllowedAsync(post.GroupGID).ConfigureAwait(false);
                return new(kind, targetId, post.GroupGID, null, post.AuthorUid,
                    true, canWrite, post.CommentsLocked, scope);
            }
            case CommentTargetKind.Event:
            {
                var ev = await DbContext.SyncshellCommunityEvents.AsNoTracking()
                    .FirstOrDefaultAsync(e => e.Id == targetId).ConfigureAwait(false);
                if (ev == null) return Denied(kind, targetId);
                var (isMember, _) = await TryValidateUserInGroup(ev.GroupGID).ConfigureAwait(false);
                if (!isMember || !PublishingService.IsVisible(ev.PublishStatus, null, DateTime.UtcNow))
                    return Denied(kind, targetId);
                var scope = await CommunityModScopeAsync(ev.GroupGID).ConfigureAwait(false);
                var canWrite = await CommentWriteAllowedAsync(ev.GroupGID).ConfigureAwait(false);
                return new(kind, targetId, ev.GroupGID, null, ev.AuthorUid,
                    true, canWrite, ev.CommentsLocked, scope);
            }
            case CommentTargetKind.Blog:
            {
                var post = await DbContext.UserProfileBlogPosts.AsNoTracking()
                    .FirstOrDefaultAsync(b => b.Id == targetId).ConfigureAwait(false);
                if (post == null) return Denied(kind, targetId);
                var (canView, canWrite, scope) = await BlogTargetAclAsync(post).ConfigureAwait(false);
                return new(kind, targetId, null, post.UserUID, post.UserUID,
                    canView, canWrite, post.CommentsLocked, scope);
            }
            default:
                return Denied(kind, targetId);
        }
    }

    /// <summary>
    /// THE blog ACL - every blog comment/reaction/report/read/write resolves
    /// through this check (requirement: single authority). Order: self →
    /// profile flags → bidirectional block → publish status → scope.
    /// </summary>
    private async Task<(bool CanView, bool CanWrite, CommunityCommentService.ModerationScope Scope)>
        BlogTargetAclAsync(UserProfileBlogPost post)
    {
        var isSelf = string.Equals(post.UserUID, UserUID, StringComparison.Ordinal);
        var platform = await IsPlatformModeratorAsync().ConfigureAwait(false);
        if (isSelf)
        {
            return (true, !post.CommentsLocked,
                CommunityCommentService.ModerationScope.BlogOwner);
        }

        var profile = await DbContext.UserProfileData.AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserUID == post.UserUID).ConfigureAwait(false);
        if (profile == null || profile.ProfileDisabled || profile.FlaggedForReport)
            return (false, false, platform ? CommunityCommentService.ModerationScope.Platform
                : CommunityCommentService.ModerationScope.None);
        if (((ProfileVisibility)profile.ProfileVisibility).HasFlag(ProfileVisibility.HideBlog))
        {
            // Hidden blog section - platform mods still resolve reports but
            // never view the thread.
            return (false, false, platform ? CommunityCommentService.ModerationScope.Platform
                : CommunityCommentService.ModerationScope.None);
        }

        var blocked = await DbContext.UserBlocks.AsNoTracking().AnyAsync(b =>
            (b.UserUID == UserUID && b.BlockedUserUID == post.UserUID)
            || (b.UserUID == post.UserUID && b.BlockedUserUID == UserUID)).ConfigureAwait(false);
        if (blocked)
        {
            return (false, false, platform ? CommunityCommentService.ModerationScope.Platform
                : CommunityCommentService.ModerationScope.None);
        }

        var visible = PublishingService.IsVisible(post.Status, post.ScheduledForUtc, DateTime.UtcNow)
            && post.VisibilityScope switch
            {
                (int)BlogVisibilityScope.Public => true,
                (int)BlogVisibilityScope.Pairs => await IsPairedWithAsync(post.UserUID).ConfigureAwait(false),
                _ => false,
            };
        var canWrite = visible && !post.CommentsLocked;
        return (visible, canWrite, platform ? CommunityCommentService.ModerationScope.Platform
            : CommunityCommentService.ModerationScope.None);
    }

    /// <summary>Caller is paired with <paramref name="otherUid"/> - mirrors the
    /// pair check used by profile extras.</summary>
    private async Task<bool> IsPairedWithAsync(string otherUid)
    {
        // Mirrors the profile-extras pair gate: mutual ClientPairs or shared
        // group membership, minus paused overrides.
        return (await GetAllPairedUnpausedUsers().ConfigureAwait(false))
            .Contains(otherUid, StringComparer.Ordinal);
    }

    /// <summary>Community moderation scope for the caller - owner/mod implicit,
    /// else the ModerateContent staff bit; blog paths never reach this.</summary>
    private async Task<CommunityCommentService.ModerationScope> CommunityModScopeAsync(string gid)
    {
        var has = await HasCommunityRights(gid, SyncshellStaffPermission.ModerateContent)
            .ConfigureAwait(false);
        return has ? CommunityCommentService.ModerationScope.CommunityStaff
            : CommunityCommentService.ModerationScope.None;
    }

    /// <summary>comments_mode policy gate - Open/MembersOnly let members write,
    /// StaffOnly restricts to staff, Disabled rejects all member writes.</summary>
    private async Task<bool> CommentWriteAllowedAsync(string gid)
    {
        var policy = await LoadPolicyAsync(gid).ConfigureAwait(false);
        return policy.CommentsMode switch
        {
            CommunityCommentsMode.Disabled => false,
            CommunityCommentsMode.StaffOnly => await IsCommunityStaff(gid).ConfigureAwait(false),
            _ => true,
        };
    }

    /// <summary>Platform-moderator check - the existing User.IsModerator flag,
    /// no invented role.</summary>
    private async Task<bool> IsPlatformModeratorAsync()
    {
        return await DbContext.Users.AsNoTracking()
            .AnyAsync(u => u.UID == UserUID && u.IsModerator).ConfigureAwait(false);
    }

    private static CommentTargetAccess Denied(CommentTargetKind kind, long id) =>
        new(kind, id, null, null, null, false, false, false,
            CommunityCommentService.ModerationScope.None);

    // ------------------------------------------------------------------
    //  DTO mapping + notifications
    // ------------------------------------------------------------------

    private CommunityCommentDto ToCommentDto(CommunityComment c, bool viewerModerates,
        IReadOnlyDictionary<string, UserData>? authors = null)
    {
        var mine = string.Equals(c.AuthorUid, UserUID, StringComparison.Ordinal);
        var state = (CommentState)c.State;
        // Body policy: visible → everyone; hidden → moderators + author for
        // appeal context; removed/author-deleted → never served again.
        var showBody = state == CommentState.Visible
            || (state == CommentState.ModeratorHidden && (viewerModerates || mine));
        var showReason = state != CommentState.Visible && (viewerModerates || mine);
        return new(c.Id, (CommentTargetKind)c.TargetKind, c.TargetId, c.GroupGID,
            authors != null && authors.TryGetValue(c.AuthorUid, out var ud)
                ? ud : new UserData(c.AuthorUid),
            showBody ? c.Body : null, state, c.ParentId, c.RootId, c.ReplyCount,
            c.CreatedAt, c.EditedAt, mine, viewerModerates,
            viewerModerates ? c.ModeratedByScope : 0,
            showReason ? c.ModerationReason : null);
    }

    /// <summary>Comment create notifications - target author (roots), parent
    /// author (replies), mentions. Self-notifications suppressed.</summary>
    private async Task NotifyCommentAsync(CommunityComment comment, CommunityCommentService.CommentOutcome outcome)
    {
        var now = DateTime.UtcNow;
        var contentKind = ((CommentTargetKind)comment.TargetKind).ToString().ToLowerInvariant();
        var snippet = Utils.MarkdownText.Strip(comment.Body);
        snippet = snippet.Length > 120 ? snippet[..120].TrimEnd() + "..." : snippet;
        var contentId = comment.TargetId.ToString();

        if (outcome.ParentAuthorUid != null
            && !string.Equals(outcome.ParentAuthorUid, UserUID, StringComparison.Ordinal))
        {
            await _notificationWriter.PushAsync(DbContext, outcome.ParentAuthorUid,
                UserNotificationKind.CommentReply, "New reply to your comment", snippet,
                UserUID, comment.GroupGID, contentKind, contentId,
                $"reply:{comment.Id}", subContentId: comment.Id.ToString(),
                targetUid: outcome.TargetAuthorUid).ConfigureAwait(false);
        }
        else if (outcome.TargetAuthorUid != null
            && !string.Equals(outcome.TargetAuthorUid, UserUID, StringComparison.Ordinal))
        {
            await _notificationWriter.PushAsync(DbContext, outcome.TargetAuthorUid,
                UserNotificationKind.CommentAdded, "New comment", snippet,
                UserUID, comment.GroupGID, contentKind, contentId,
                $"comment:{comment.Id}", subContentId: comment.Id.ToString(),
                targetUid: outcome.TargetAuthorUid).ConfigureAwait(false);
        }

        foreach (var mentioned in EntityReferenceService.ParseMentions(comment.Body))
        {
            if (string.Equals(mentioned, UserUID, StringComparison.Ordinal)) continue;
            await _notificationWriter.PushAsync(DbContext, mentioned,
                UserNotificationKind.Mention, "You were mentioned", snippet,
                UserUID, comment.GroupGID, contentKind, contentId,
                $"mention:{comment.Id}:{mentioned}",
                subContentId: comment.Id.ToString(),
                targetUid: outcome.TargetAuthorUid).ConfigureAwait(false);
        }
    }

    /// <summary>Maps report rows with reporter/target snippets for the queue UI.</summary>
    private async Task<List<CommunityReportDto>> MapReportsAsync(List<CommunityReport> rows)
    {
        var reporterUids = rows.Select(r => r.ReporterUid)
            .Concat(rows.Where(r => r.ResolvedByUid != null).Select(r => r.ResolvedByUid!))
            .Distinct().ToList();
        var users = await DbContext.Users.AsNoTracking()
            .Where(u => reporterUids.Contains(u.UID)).ToListAsync().ConfigureAwait(false);
        var byUid = users.ToDictionary(u => u.UID, u => u.ToUserData());

        var commentIds = rows.Where(r => r.TargetKind == (int)ReportTargetKind.Comment)
            .Select(r => r.TargetId).ToList();
        var comments = await DbContext.CommunityComments.AsNoTracking()
            .Where(c => commentIds.Contains(c.Id)).ToListAsync().ConfigureAwait(false);
        var snippetById = comments.ToDictionary(c => c.Id,
            c => c.State == (int)CommentState.Visible ? c.Body[..Math.Min(120, c.Body.Length)]
                : $"[{(CommentState)c.State}]");

        return rows.Select(r => new CommunityReportDto(
            r.Id, (ReportTargetKind)r.TargetKind, r.TargetId, r.GroupGID,
            byUid.GetValueOrDefault(r.ReporterUid), (ReportReason)r.Reason, r.Details,
            (ReportState)r.State,
            r.ResolvedByUid != null ? byUid.GetValueOrDefault(r.ResolvedByUid) : null,
            r.ResolvedAt, r.ResolutionNote, r.CreatedAt,
            snippetById.GetValueOrDefault(r.TargetId))).ToList();
    }

    /// <summary>Pushes a fine-grained reaction change on profile-owned
    /// content to the owner's online paired viewers and the owner's own
    /// sessions - the same audience <see cref="PushUserProfileChangedAsync"/>
    /// addresses, but the coordinates let clients patch counts in place
    /// instead of dropping every cached profile surface.</summary>
    private async Task PushUserReactionChangedAsync(string uid, int hostKind,
        long hostId, bool commentLevel)
    {
        var paired = await GetAllPairedUnpausedUsers(uid).ConfigureAwait(false);
        var online = await GetOnlineUsers(paired).ConfigureAwait(false);
        var dto = new UserReactionChangedDto(new UserData(uid), hostKind, hostId, commentLevel);
        if (online.Count > 0)
        {
            await Clients.Users(online.Keys.ToList()).Client_UserReactionChanged(dto).ConfigureAwait(false);
        }
        await Clients.User(uid).Client_UserReactionChanged(dto).ConfigureAwait(false);
    }

    /// <summary>Pushes a user-profile content refresh (blog posts, their
    /// comments and reactions) to the owner's online paired viewers and the
    /// owner's own sessions. Reuses the profile-update push so every client
    /// surface drops stale profile state in one place.</summary>
    private async Task PushUserProfileChangedAsync(string uid)
    {
        var paired = await GetAllPairedUnpausedUsers(uid).ConfigureAwait(false);
        var online = await GetOnlineUsers(paired).ConfigureAwait(false);
        var dto = new UserDto(new UserData(uid));
        if (online.Count > 0)
        {
            await Clients.Users(online.Keys.ToList()).Client_UserUpdateProfile(dto).ConfigureAwait(false);
        }
        await Clients.User(uid).Client_UserUpdateProfile(dto).ConfigureAwait(false);
    }
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Sphene.API.Data.Enum;
using Sphene.API.Dto.Group;
using SpheneServer.Services;
using SpheneServer.Utils;
using SpheneShared.Models;
using System.Text;

namespace SpheneServer.Hubs;

/// <summary>
/// Community feed v2 - keyset-paged aggregation across content the caller
/// belongs to (news/events/photos) plus honest activity on the caller's own
/// content (comments, reactions, RSVPs) and blogs by related authors.
///
/// Cursor contract: items order by <c>(TimestampUtc DESC, Kind ASC,
/// ItemId DESC, ActorUid ASC)</c> and the opaque cursor encodes exactly that
/// tuple, so identical timestamps can neither drop nor duplicate rows.
/// Each source is pre-filtered on <c>ts &lt;= cursor.ts</c> and overfetched;
/// the full tuple predicate then applies in memory over the merge.
///
/// Honest-activity rule (Phase-5 review): reaction rows are state-based -
/// a remove+re-add re-timestamps; a removed row leaves no trace. Feed
/// reaction items mean "reacted recently", never a history. RSVP items use
/// <c>CreatedAt</c> (first response) and <c>PromotedAt</c> only - status
/// flips are not reconstructable and are never synthesized.
/// </summary>
public partial class SpheneHub
{
    private const int FeedPageSizeMax = 30;

    /// <summary>Keyset-paged community feed - see class remarks for the
    /// cursor + honesty contract. The v1 <c>GroupGetCommunityFeed</c> stays
    /// for older clients; both share <see cref="CollectCommunityFeedItems"/>.</summary>
    [Authorize(Policy = "Identified")]
    public async Task<SyncshellCommunityFeedPageDto> GroupGetCommunityFeedPage(FeedPageRequestDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto.Take));

        var gids = await DbContext.GroupPairs.AsNoTracking()
            .Where(gp => gp.GroupUserUID == UserUID)
            .Select(gp => gp.GroupGID)
            .ToListAsync().ConfigureAwait(false);
        var followed = await FollowedNonMemberGidsAsync(gids).ConfigureAwait(false);
        var allGids = gids.Union(followed, StringComparer.Ordinal).ToList();

        var take = Math.Clamp(dto.Take, 1, FeedPageSizeMax);
        var cursor = DecodeFeedCursor(dto.Cursor);
        var boundary = cursor?.Ts;
        var perSource = take * 2 + 1;
        var items = new List<SyncshellCommunityFeedItemDto>(take * 4);

        if (allGids.Count > 0)
        {
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

            var baseItems = (await CollectCommunityFeedItems(gids, accents, aliases)
                .ConfigureAwait(false))
                .Concat(await CollectFollowedFeedEventsAsync(followed, accents, aliases)
                    .ConfigureAwait(false))
                .ToList();
            // v1 sources honor the boundary + carry deep-link fields.
            foreach (var it in baseItems)
            {
                if (boundary.HasValue && it.TimestampUtc > boundary.Value) continue;
                var section = it.Kind switch
                {
                    SyncshellFeedItemKind.News => SyncshellCommunitySection.News,
                    SyncshellFeedItemKind.Event => SyncshellCommunitySection.Events,
                    SyncshellFeedItemKind.Photo => SyncshellCommunitySection.Gallery,
                    _ => (SyncshellCommunitySection?)null,
                };
                items.Add(it with { Section = section, ItemId = it.Id });
            }

            await CollectFeedCommentsAsync(gids, accents, aliases, boundary, perSource, items)
                .ConfigureAwait(false);
            await CollectFeedReactionsAsync(gids, accents, aliases, boundary, perSource, items)
                .ConfigureAwait(false);
            await CollectFeedRsvpsAsync(gids, accents, aliases, boundary, perSource, items)
                .ConfigureAwait(false);
        }

        // Blog origin rule (E1): author must be a mutual pair or shell
        // co-member - `GetAllPairedUnpausedUsers` covers both; scope and
        // block checks apply on top.
        await CollectFeedBlogsAsync(gids, boundary, perSource, items).ConfigureAwait(false);

        // Full tuple predicate, deterministic order, page trim - take+1
        // detects "more pages" without a second count pass.
        var candidates = items.Where(i => CursorAdmits(cursor, i))
            .OrderByDescending(i => i.TimestampUtc)
            .ThenBy(i => (int)i.Kind)
            .ThenByDescending(i => i.ItemId ?? i.Id)
            .ThenBy(i => i.ActorUid, StringComparer.Ordinal)
            .Take(take + 1).ToList();
        var saturated = candidates.Count > take;
        var page = candidates.Take(take).ToList();

        var names = await ResolveFeedActorNamesAsync(page).ConfigureAwait(false);
        page = page.Select(i => i.ActorUid != null && names.TryGetValue(i.ActorUid, out var n)
            ? i with { ActorDisplayName = n } : i).ToList();

        var next = saturated && page.Count > 0 ? EncodeFeedCursor(page[^1]) : null;
        return new SyncshellCommunityFeedPageDto(page, next);
    }

    /// <summary>
    /// Profile activity tab - items the *target* authored that the caller
    /// may see: their blogs (post-level ACL), their published news/events in
    /// caller-shared shells, and their visible comments/reactions on hosts
    /// the caller can access. Gate chain: UserBlock either direction →
    /// profile flags (blog rows only - community content isn't profile
    /// content) → per-target host ACL on every row. An inaccessible profile
    /// and empty activity share the same empty page.
    /// </summary>
    [Authorize(Policy = "Identified")]
    public async Task<SyncshellCommunityFeedPageDto> UserGetProfileActivity(
        string targetUid, FeedPageRequestDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto.Take));
        var empty = new SyncshellCommunityFeedPageDto([], null);
        if (string.IsNullOrWhiteSpace(targetUid)) return empty;

        var blockedPair = await DbContext.UserBlocks.AsNoTracking().AnyAsync(b =>
            (b.UserUID == UserUID && b.BlockedUserUID == targetUid)
            || (b.UserUID == targetUid && b.BlockedUserUID == UserUID))
            .ConfigureAwait(false);
        if (blockedPair) return empty;

        var isSelf = string.Equals(targetUid, UserUID, StringComparison.Ordinal);
        var profile = await DbContext.UserProfileData.AsNoTracking()
            .Where(p => p.UserUID == targetUid)
            .Select(p => new { p.ProfileDisabled, p.FlaggedForReport, p.ProfileVisibility })
            .SingleOrDefaultAsync().ConfigureAwait(false);
        // Self bypasses the profile gate - BlogTargetAclAsync returns before
        // the profile check, so own blogs always show on the own profile.
        var profileSealed = !isSelf
            && (profile == null || profile.ProfileDisabled || profile.FlaggedForReport
                || (profile.ProfileVisibility & (int)ProfileVisibility.HideBlog) != 0);

        var gids = await DbContext.GroupPairs.AsNoTracking()
            .Where(gp => gp.GroupUserUID == UserUID)
            .Select(gp => gp.GroupGID).ToListAsync().ConfigureAwait(false);
        var gidSet = gids.ToHashSet(StringComparer.Ordinal);
        var related = new HashSet<string>(await GetAllPairedUnpausedUsers()
            .ConfigureAwait(false), StringComparer.Ordinal);

        var take = Math.Clamp(dto.Take, 1, FeedPageSizeMax);
        var cursor = DecodeFeedCursor(dto.Cursor);
        var boundary = cursor?.Ts;
        var perSource = take * 2 + 1;
        var items = new List<SyncshellCommunityFeedItemDto>(take * 4);
        var now = DateTime.UtcNow;

        await CollectProfileBlogsAsync(targetUid, isSelf, profileSealed, related,
            boundary, perSource, now, items).ConfigureAwait(false);
        await CollectProfilePostsAsync(targetUid, gidSet, boundary, perSource, items)
            .ConfigureAwait(false);
        var callerBlocked = new HashSet<string>(await DbContext.UserBlocks.AsNoTracking()
            .Where(b => b.UserUID == UserUID || b.BlockedUserUID == UserUID)
            .Select(b => b.UserUID == UserUID ? b.BlockedUserUID : b.UserUID)
            .ToListAsync().ConfigureAwait(false), StringComparer.Ordinal);
        await CollectProfileInteractionsAsync(targetUid, gidSet, related,
            callerBlocked, boundary, perSource, now, items).ConfigureAwait(false);

        var candidates = items.Where(i => CursorAdmits(cursor, i))
            .OrderByDescending(i => i.TimestampUtc)
            .ThenBy(i => (int)i.Kind)
            .ThenByDescending(i => i.ItemId ?? i.Id)
            .ThenBy(i => i.ActorUid, StringComparer.Ordinal)
            .Take(take + 1).ToList();
        var saturated = candidates.Count > take;
        var page = candidates.Take(take).ToList();

        var names = await ResolveFeedActorNamesAsync(page).ConfigureAwait(false);
        page = page.Select(i => i.ActorUid != null && names.TryGetValue(i.ActorUid, out var n)
            ? i with { ActorDisplayName = n } : i).ToList();
        var next = saturated && page.Count > 0 ? EncodeFeedCursor(page[^1]) : null;
        return new SyncshellCommunityFeedPageDto(page, next);
    }

    /// <summary>Target's blog posts - post-level ACL (scope vs caller).</summary>
    private async Task CollectProfileBlogsAsync(string targetUid, bool isSelf,
        bool profileSealed, HashSet<string> related, DateTime? boundary,
        int perSource, DateTime now, List<SyncshellCommunityFeedItemDto> sink)
    {
        if (profileSealed) return;
        var q = DbContext.UserProfileBlogPosts.AsNoTracking()
            .Where(b => b.UserUID == targetUid);
        if (!isSelf) q = q.Where(b => b.VisibilityScope != (int)BlogVisibilityScope.Private);
        if (boundary.HasValue) q = q.Where(b => b.CreatedAt <= boundary.Value);
        var rows = await q.OrderByDescending(b => b.CreatedAt).Take(perSource * 2)
            .Select(b => new { b.Id, b.Title, b.Body, b.VisibilityScope,
                b.Status, b.ScheduledForUtc, b.CreatedAt, b.CoverMediaHash })
            .ToListAsync().ConfigureAwait(false);
        var pairsVisible = isSelf || related.Contains(targetUid);
        foreach (var b in rows)
        {
            if (!PublishingService.IsVisible(b.Status, b.ScheduledForUtc, now)) continue;
            if (b.VisibilityScope == (int)BlogVisibilityScope.Pairs && !pairsVisible) continue;
            sink.Add(new SyncshellCommunityFeedItemDto(
                string.Empty, null, null,
                SyncshellFeedItemKind.Blog, b.Id, b.Title, Snip(b.Body), b.CreatedAt,
                Section: null, ItemId: b.Id, ActorUid: targetUid,
                ThumbnailMediaHash: b.CoverMediaHash));
        }
    }

    /// <summary>Target's published news/events in caller-shared shells.</summary>
    private async Task CollectProfilePostsAsync(string targetUid,
        HashSet<string> gids, DateTime? boundary, int perSource,
        List<SyncshellCommunityFeedItemDto> sink)
    {
        var aliases = await DbContext.Groups.AsNoTracking()
            .Where(g => gids.Contains(g.GID))
            .Select(g => new { g.GID, g.Alias })
            .ToDictionaryAsync(g => g.GID, g => g.Alias).ConfigureAwait(false);
        var now = DateTime.UtcNow;
        var newsQ = DbContext.SyncshellCommunityNews.AsNoTracking()
            .Where(n => n.AuthorUid == targetUid && gids.Contains(n.GroupGID)
                && (n.Status == (int)ContentPublishStatus.Published
                    || (n.Status == (int)ContentPublishStatus.Scheduled && n.ScheduledForUtc <= now)));
        if (boundary.HasValue) newsQ = newsQ.Where(n => n.PublishedAt <= boundary.Value);
        var news = await newsQ.OrderByDescending(n => n.PublishedAt).Take(perSource)
            .Select(n => new { n.Id, n.GroupGID, n.Title, n.Body, n.PublishedAt })
            .ToListAsync().ConfigureAwait(false);
        foreach (var n in news)
        {
            sink.Add(new SyncshellCommunityFeedItemDto(
                n.GroupGID, aliases.GetValueOrDefault(n.GroupGID), null,
                SyncshellFeedItemKind.News, n.Id, n.Title, Snip(n.Body), n.PublishedAt,
                Section: SyncshellCommunitySection.News, ItemId: n.Id, ActorUid: targetUid));
        }

        var evQ = DbContext.SyncshellCommunityEvents.AsNoTracking()
            .Where(e => e.AuthorUid == targetUid && gids.Contains(e.GroupGID)
                && e.PublishStatus == (int)ContentPublishStatus.Published);
        if (boundary.HasValue) evQ = evQ.Where(e => e.CreatedAt <= boundary.Value);
        var events = await evQ.OrderByDescending(e => e.CreatedAt).Take(perSource)
            .Select(e => new { e.Id, e.GroupGID, e.Title, e.CreatedAt,
                e.PreviewMediaHash, e.ImageMediaHash })
            .ToListAsync().ConfigureAwait(false);
        foreach (var e in events)
        {
            sink.Add(new SyncshellCommunityFeedItemDto(
                e.GroupGID, aliases.GetValueOrDefault(e.GroupGID), null,
                SyncshellFeedItemKind.Event, e.Id, e.Title, null, e.CreatedAt,
                Section: SyncshellCommunitySection.Events, ItemId: e.Id, ActorUid: targetUid,
                ThumbnailMediaHash: e.PreviewMediaHash ?? e.ImageMediaHash));
        }
    }

    /// <summary>Target's visible comments + reactions on hosts the caller
    /// can access - host ACL resolved per row (news/event: shared gid +
    /// published; blog: post-level gates against the *post's* owner).</summary>
    private async Task CollectProfileInteractionsAsync(string targetUid,
        HashSet<string> gids, HashSet<string> related, HashSet<string> callerBlocked,
        DateTime? boundary, int perSource, DateTime now,
        List<SyncshellCommunityFeedItemDto> sink)
    {
        var visible = (int)CommentState.Visible;
        var cq = DbContext.CommunityComments.AsNoTracking()
            .Where(c => c.AuthorUid == targetUid && c.State == visible);
        if (boundary.HasValue) cq = cq.Where(c => c.CreatedAt <= boundary.Value);
        var comments = await cq.OrderByDescending(c => c.CreatedAt).Take(perSource * 2)
            .Select(c => new { c.Id, c.TargetKind, c.TargetId, c.GroupGID,
                c.OwnerUid, c.Body, c.CreatedAt })
            .ToListAsync().ConfigureAwait(false);
        var rq = DbContext.CommunityReactions.AsNoTracking()
            .Where(r => r.UserUID == targetUid);
        if (boundary.HasValue) rq = rq.Where(r => r.CreatedAt <= boundary.Value);
        var reactions = await rq.OrderByDescending(r => r.CreatedAt).Take(perSource * 2)
            .Select(r => new { r.TargetKind, r.TargetId, r.GroupGID,
                r.OwnerUid, r.Kind, r.CreatedAt })
            .ToListAsync().ConfigureAwait(false);

        var aliases = await DbContext.Groups.AsNoTracking()
            .Where(g => gids.Contains(g.GID))
            .Select(g => new { g.GID, g.Alias })
            .ToDictionaryAsync(g => g.GID, g => g.Alias).ConfigureAwait(false);
        var titles = await ResolveFeedTargetTitlesAsync(
            comments.Select(c => (c.TargetKind, c.TargetId))
                .Concat(reactions.Select(r => (r.TargetKind, r.TargetId))))
            .ConfigureAwait(false);
        var visibleHosts = await VisibleHostsForCallerAsync(
            comments.Select(c => (c.TargetKind, c.TargetId))
                .Concat(reactions.Select(r => (r.TargetKind, r.TargetId))),
            gids, related, callerBlocked, now).ConfigureAwait(false);
        // Comments can only target News/Event/Blog; only reactions may carry
        // a comment target - resolve those to the comment's host post.
        var commentHosts = await ResolveFeedCommentHostsAsync(
            reactions.Where(r => r.TargetKind == (int)ReactionTargetKind.Comment)
                .Select(r => r.TargetId)).ConfigureAwait(false);

        foreach (var c in comments)
        {
            if (!visibleHosts.Contains((c.TargetKind, c.TargetId))) continue;
            titles.TryGetValue((c.TargetKind, c.TargetId), out var title);
            var gid = c.GroupGID ?? string.Empty;
            sink.Add(new SyncshellCommunityFeedItemDto(
                gid, gid.Length > 0 ? aliases.GetValueOrDefault(gid) : null, null,
                SyncshellFeedItemKind.Comment, c.Id,
                $"Comment on {title ?? "a post"}", Snip(c.Body), c.CreatedAt,
                Section: SectionOf(c.TargetKind), ItemId: c.TargetId, ActorUid: targetUid));
        }
        foreach (var r in reactions)
        {
            var host = r.TargetKind == (int)ReactionTargetKind.Comment
                && commentHosts.TryGetValue(r.TargetId, out var rh) ? rh : (Kind: r.TargetKind, Id: r.TargetId);
            if (!visibleHosts.Contains((host.Kind, host.Id))) continue;
            titles.TryGetValue((host.Kind, host.Id), out var title);
            var gid = r.GroupGID ?? string.Empty;
            sink.Add(new SyncshellCommunityFeedItemDto(
                gid, gid.Length > 0 ? aliases.GetValueOrDefault(gid) : null, null,
                SyncshellFeedItemKind.Reaction, r.TargetId,
                $"Reaction on {title ?? "a post"}",
                ((ReactionKind)r.Kind).ToString(), r.CreatedAt,
                Section: SectionOf(host.Kind), ItemId: host.Id, ActorUid: targetUid));
        }
    }

    /// <summary>Hosts visible to the caller - the per-target ACL the review
    /// requires on every activity row: news/event must be published and live
    /// in a caller-shared shell; blogs must pass the post-level gates
    /// (status, scope vs caller, owner block/flags).</summary>
    private async Task<HashSet<(int, long)>> VisibleHostsForCallerAsync(
        IEnumerable<(int Kind, long Id)> targets, HashSet<string> gids,
        HashSet<string> related, HashSet<string> callerBlocked, DateTime now)
    {
        var list = targets.Distinct().ToList();
        var visible = new HashSet<(int, long)>();
        var newsIds = list.Where(t => t.Kind == (int)CommentTargetKind.News).Select(t => t.Id).ToList();
        var eventIds = list.Where(t => t.Kind == (int)CommentTargetKind.Event).Select(t => t.Id).ToList();
        var blogIds = list.Where(t => t.Kind == (int)CommentTargetKind.Blog).Select(t => t.Id).ToList();

        var news = await DbContext.SyncshellCommunityNews.AsNoTracking()
            .Where(n => newsIds.Contains(n.Id) && gids.Contains(n.GroupGID))
            .Select(n => new { n.Id, n.Status, n.ScheduledForUtc })
            .ToListAsync().ConfigureAwait(false);
        foreach (var n in news)
        {
            if (PublishingService.IsVisible(n.Status, n.ScheduledForUtc, now))
                visible.Add(((int)CommentTargetKind.News, n.Id));
        }
        var events = await DbContext.SyncshellCommunityEvents.AsNoTracking()
            .Where(e => eventIds.Contains(e.Id) && gids.Contains(e.GroupGID)
                && e.PublishStatus == (int)ContentPublishStatus.Published)
            .Select(e => e.Id).ToListAsync().ConfigureAwait(false);
        foreach (var id in events) visible.Add(((int)CommentTargetKind.Event, id));

        var blogs = await DbContext.UserProfileBlogPosts.AsNoTracking()
            .Where(b => blogIds.Contains(b.Id))
            .Select(b => new { b.Id, b.UserUID, b.Status, b.ScheduledForUtc, b.VisibilityScope })
            .ToListAsync().ConfigureAwait(false);
        var ownerUids = blogs.Select(b => b.UserUID).Distinct().ToList();
        var validOwners = new HashSet<string>(await DbContext.UserProfileData.AsNoTracking()
            .Where(p => ownerUids.Contains(p.UserUID)
                && !p.ProfileDisabled && !p.FlaggedForReport
                && (p.ProfileVisibility & (int)ProfileVisibility.HideBlog) == 0)
            .Select(p => p.UserUID).ToListAsync().ConfigureAwait(false),
            StringComparer.Ordinal);
        foreach (var b in blogs)
        {
            var isSelf = b.UserUID == UserUID;
            if (!isSelf)
            {
                if (callerBlocked.Contains(b.UserUID) || !validOwners.Contains(b.UserUID)) continue;
                if (b.VisibilityScope == (int)BlogVisibilityScope.Private) continue;
                if (b.VisibilityScope == (int)BlogVisibilityScope.Pairs
                    && !related.Contains(b.UserUID)) continue;
            }
            if (!PublishingService.IsVisible(b.Status, b.ScheduledForUtc, now) && !isSelf) continue;
            visible.Add(((int)CommentTargetKind.Blog, b.Id));
        }
        return visible;
    }

    // ------------------------------------------------------------------
    //  Activity sources - honest timestamps only (see class remarks)
    // ------------------------------------------------------------------

    /// <summary>Comments + replies on content the caller authored.</summary>
    private async Task CollectFeedCommentsAsync(List<string> gids,
        Dictionary<string, string> accents, Dictionary<string, string> aliases,
        DateTime? boundary, int perSource, List<SyncshellCommunityFeedItemDto> sink)
    {
        var visible = (int)CommentState.Visible;
        var q = DbContext.CommunityComments.AsNoTracking()
            .Where(c => c.State == visible && c.AuthorUid != UserUID)
            .Where(c => c.OwnerUid == UserUID // my blog posts
                || (c.TargetKind == (int)CommentTargetKind.News
                    && DbContext.SyncshellCommunityNews.Any(n => n.Id == c.TargetId && n.AuthorUid == UserUID))
                || (c.TargetKind == (int)CommentTargetKind.Event
                    && DbContext.SyncshellCommunityEvents.Any(e => e.Id == c.TargetId && e.AuthorUid == UserUID))
                || (c.ParentId != null
                    && DbContext.CommunityComments.Any(p => p.Id == c.ParentId && p.AuthorUid == UserUID)));
        if (boundary.HasValue) q = q.Where(c => c.CreatedAt <= boundary.Value);
        var rows = await q.OrderByDescending(c => c.CreatedAt).Take(perSource)
            .Select(c => new { c.Id, c.TargetKind, c.TargetId, c.GroupGID, c.AuthorUid, c.Body, c.CreatedAt })
            .ToListAsync().ConfigureAwait(false);
        var titles = await ResolveFeedTargetTitlesAsync(rows.Select(r => (r.TargetKind, r.TargetId)))
            .ConfigureAwait(false);
        foreach (var c in rows)
        {
            var gid = c.GroupGID;
            titles.TryGetValue((c.TargetKind, c.TargetId), out var title);
            sink.Add(new SyncshellCommunityFeedItemDto(
                gid ?? string.Empty, gid != null ? aliases.GetValueOrDefault(gid) : null,
                gid != null ? accents.GetValueOrDefault(gid) : null,
                SyncshellFeedItemKind.Comment, c.Id,
                $"Comment on {title ?? "your post"}",
                Snip(c.Body), c.CreatedAt,
                Section: SectionOf(c.TargetKind),
                ItemId: c.TargetId, ActorUid: c.AuthorUid,
                SubItemId: c.Id));
        }
    }

    /// <summary>Reactions on content the caller authored (posts + comments).</summary>
    private async Task CollectFeedReactionsAsync(List<string> gids,
        Dictionary<string, string> accents, Dictionary<string, string> aliases,
        DateTime? boundary, int perSource, List<SyncshellCommunityFeedItemDto> sink)
    {
        var q = DbContext.CommunityReactions.AsNoTracking()
            .Where(r => r.UserUID != UserUID)
            .Where(r => r.OwnerUid == UserUID // my blog posts
                || (r.TargetKind == (int)ReactionTargetKind.News
                    && DbContext.SyncshellCommunityNews.Any(n => n.Id == r.TargetId && n.AuthorUid == UserUID))
                || (r.TargetKind == (int)ReactionTargetKind.Event
                    && DbContext.SyncshellCommunityEvents.Any(e => e.Id == r.TargetId && e.AuthorUid == UserUID))
                || (r.TargetKind == (int)ReactionTargetKind.Comment
                    && DbContext.CommunityComments.Any(c => c.Id == r.TargetId && c.AuthorUid == UserUID)));
        if (boundary.HasValue) q = q.Where(r => r.CreatedAt <= boundary.Value);
        var rows = await q.OrderByDescending(r => r.CreatedAt).Take(perSource)
            .Select(r => new { r.TargetKind, r.TargetId, r.GroupGID, r.UserUID, r.Kind, r.CreatedAt })
            .ToListAsync().ConfigureAwait(false);
        var titles = await ResolveFeedTargetTitlesAsync(rows.Select(r => (r.TargetKind, r.TargetId)))
            .ConfigureAwait(false);
        // Reaction-on-comment deep-links resolve to the comment's host post.
        var commentHosts = await ResolveFeedCommentHostsAsync(
            rows.Where(r => r.TargetKind == (int)ReactionTargetKind.Comment)
                .Select(r => r.TargetId)).ConfigureAwait(false);
        foreach (var r in rows)
        {
            var gid = r.GroupGID;
            titles.TryGetValue((r.TargetKind, r.TargetId), out var title);
            var host = r.TargetKind == (int)ReactionTargetKind.Comment
                && commentHosts.TryGetValue(r.TargetId, out var h) ? h : (Kind: r.TargetKind, Id: r.TargetId);
            titles.TryGetValue((host.Kind, host.Id), out var hostTitle);
            sink.Add(new SyncshellCommunityFeedItemDto(
                gid ?? string.Empty, gid != null ? aliases.GetValueOrDefault(gid) : null,
                gid != null ? accents.GetValueOrDefault(gid) : null,
                SyncshellFeedItemKind.Reaction, r.TargetId,
                $"Reaction on {title ?? hostTitle ?? "your post"}",
                ((ReactionKind)r.Kind).ToString(), r.CreatedAt,
                Section: SectionOf(host.Kind),
                ItemId: host.Id, ActorUid: r.UserUID,
                // comment-target reactions keep the comment id as inner
                // drill-in - the client lands inside the thread
                SubItemId: r.TargetKind == (int)ReactionTargetKind.Comment
                    ? r.TargetId : null));
        }
    }

    /// <summary>First-RSVP + promotion activity on events the caller authored.</summary>
    private async Task CollectFeedRsvpsAsync(List<string> gids,
        Dictionary<string, string> accents, Dictionary<string, string> aliases,
        DateTime? boundary, int perSource, List<SyncshellCommunityFeedItemDto> sink)
    {
        var q = DbContext.CommunityEventParticipants.AsNoTracking()
            .Where(p => p.UserUID != UserUID)
            .Where(p => DbContext.SyncshellCommunityEvents.Any(e =>
                e.Id == p.EventId && e.AuthorUid == UserUID && gids.Contains(e.GroupGID)))
            .Join(DbContext.SyncshellCommunityEvents, p => p.EventId, e => e.Id,
                (p, e) => new
                {
                    e.GroupGID, EventId = e.Id, e.Title,
                    p.UserUID, p.CreatedAt, p.PromotedAt,
                });
        if (boundary.HasValue) q = q.Where(p => p.CreatedAt <= boundary.Value
            || (p.PromotedAt != null && p.PromotedAt <= boundary.Value));
        var rows = await q.OrderByDescending(p => p.CreatedAt).Take(perSource * 2)
            .ToListAsync().ConfigureAwait(false);
        foreach (var p in rows)
        {
            sink.Add(new SyncshellCommunityFeedItemDto(
                p.GroupGID, aliases.GetValueOrDefault(p.GroupGID),
                accents.GetValueOrDefault(p.GroupGID),
                SyncshellFeedItemKind.Rsvp, p.EventId,
                $"RSVP - {p.Title}", "responded", p.CreatedAt,
                Section: SyncshellCommunitySection.Events,
                ItemId: p.EventId, ActorUid: p.UserUID));
            if (p.PromotedAt.HasValue)
            {
                sink.Add(new SyncshellCommunityFeedItemDto(
                    p.GroupGID, aliases.GetValueOrDefault(p.GroupGID),
                    accents.GetValueOrDefault(p.GroupGID),
                    SyncshellFeedItemKind.Rsvp, p.EventId,
                    $"RSVP - {p.Title}", "promoted to attending", p.PromotedAt.Value,
                    Section: SyncshellCommunitySection.Events,
                    ItemId: p.EventId, ActorUid: p.UserUID));
            }
        }
    }

    /// <summary>Blog posts by mutual pairs or shell co-members - origin rule +
    /// per-scope ACL; arbitrary public blogs never enter (Phase-5 E1).</summary>
    private async Task CollectFeedBlogsAsync(List<string> gids,
        DateTime? boundary, int perSource, List<SyncshellCommunityFeedItemDto> sink)
    {
        var related = new HashSet<string>(await GetAllPairedUnpausedUsers()
            .ConfigureAwait(false), StringComparer.Ordinal);
        if (related.Count == 0) return;

        // Block in either direction seals the author off entirely.
        var blocked = new HashSet<string>(await DbContext.UserBlocks.AsNoTracking()
            .Where(b => b.UserUID == UserUID || b.BlockedUserUID == UserUID)
            .Select(b => b.UserUID == UserUID ? b.BlockedUserUID : b.UserUID)
            .ToListAsync().ConfigureAwait(false), StringComparer.Ordinal);

        // Mirrors BlogTargetAclAsync exactly: authors need a *valid* profile
        // row - missing, disabled, flagged or HideBlog profiles never leak.
        // The ACL's "Pairs" check resolves to `related` itself (mutual pairs
        // ∪ unpaused co-members), so Public and Pairs posts admit the same
        // related audience here.
        var validProfiles = new HashSet<string>(await DbContext.UserProfileData.AsNoTracking()
            .Where(p => related.Contains(p.UserUID)
                && !p.ProfileDisabled && !p.FlaggedForReport
                && (p.ProfileVisibility & (int)ProfileVisibility.HideBlog) == 0)
            .Select(p => p.UserUID).ToListAsync().ConfigureAwait(false),
            StringComparer.Ordinal);

        var now = DateTime.UtcNow;
        var q = DbContext.UserProfileBlogPosts.AsNoTracking()
            .Where(b => related.Contains(b.UserUID)
                && b.VisibilityScope != (int)BlogVisibilityScope.Private);
        if (boundary.HasValue) q = q.Where(b => b.CreatedAt <= boundary.Value);
        var rows = await q.OrderByDescending(b => b.CreatedAt).Take(perSource * 2)
            .Select(b => new { b.Id, b.UserUID, b.Title, b.Body, b.VisibilityScope,
                b.Status, b.ScheduledForUtc, b.CreatedAt, b.CoverMediaHash })
            .ToListAsync().ConfigureAwait(false);

        foreach (var b in rows)
        {
            if (blocked.Contains(b.UserUID) || !validProfiles.Contains(b.UserUID)) continue;
            if (!PublishingService.IsVisible(b.Status, b.ScheduledForUtc, now)) continue;
            sink.Add(new SyncshellCommunityFeedItemDto(
                string.Empty, null, null,
                SyncshellFeedItemKind.Blog, b.Id, b.Title, Snip(b.Body), b.CreatedAt,
                Section: null, ItemId: b.Id, ActorUid: b.UserUID,
                ThumbnailMediaHash: b.CoverMediaHash));
        }
    }

    // ------------------------------------------------------------------
    //  Cursor + helpers
    // ------------------------------------------------------------------

    /// <summary>Target titles for comment/reaction feed rows - one grouped
    /// lookup per host kind instead of N+1 loads.</summary>
    private async Task<Dictionary<(int Kind, long Id), string?>> ResolveFeedTargetTitlesAsync(
        IEnumerable<(int Kind, long Id)> targets)
    {
        var list = targets.Distinct().ToList();
        var titles = new Dictionary<(int, long), string?>();
        var newsIds = list.Where(t => t.Kind == (int)CommentTargetKind.News).Select(t => t.Id).ToList();
        var eventIds = list.Where(t => t.Kind == (int)CommentTargetKind.Event).Select(t => t.Id).ToList();
        var blogIds = list.Where(t => t.Kind == (int)CommentTargetKind.Blog).Select(t => t.Id).ToList();
        var commentIds = list.Where(t => t.Kind == (int)ReactionTargetKind.Comment).Select(t => t.Id).ToList();
        foreach (var n in await DbContext.SyncshellCommunityNews.AsNoTracking()
            .Where(n => newsIds.Contains(n.Id)).Select(n => new { n.Id, n.Title })
            .ToListAsync().ConfigureAwait(false))
            titles[((int)CommentTargetKind.News, n.Id)] = n.Title;
        foreach (var e in await DbContext.SyncshellCommunityEvents.AsNoTracking()
            .Where(e => eventIds.Contains(e.Id)).Select(e => new { e.Id, e.Title })
            .ToListAsync().ConfigureAwait(false))
            titles[((int)CommentTargetKind.Event, e.Id)] = e.Title;
        foreach (var b in await DbContext.UserProfileBlogPosts.AsNoTracking()
            .Where(b => blogIds.Contains(b.Id)).Select(b => new { b.Id, b.Title })
            .ToListAsync().ConfigureAwait(false))
            titles[((int)CommentTargetKind.Blog, b.Id)] = b.Title;
        // Reaction-on-comment rows display the comment's host title.
        var hosts = await ResolveFeedCommentHostsAsync(commentIds).ConfigureAwait(false);
        foreach (var c in hosts)
        {
            if (titles.TryGetValue((c.Value.Kind, c.Value.Id), out var hostTitle))
                titles[((int)ReactionTargetKind.Comment, c.Key)] = hostTitle;
            else
                titles[((int)ReactionTargetKind.Comment, c.Key)] = "your comment";
        }
        return titles;
    }

    /// <summary>Comment id → its host (kind,id) - for deep-linking
    /// reaction-on-comment rows to the post the comment lives on.</summary>
    private async Task<Dictionary<long, (int Kind, long Id)>> ResolveFeedCommentHostsAsync(
        IEnumerable<long> commentIds)
    {
        var ids = commentIds.Distinct().ToList();
        if (ids.Count == 0) return [];
        var rows = await DbContext.CommunityComments.AsNoTracking()
            .Where(c => ids.Contains(c.Id))
            .Select(c => new { c.Id, c.TargetKind, c.TargetId })
            .ToListAsync().ConfigureAwait(false);
        return rows.ToDictionary(c => c.Id, c => (c.TargetKind, c.TargetId));
    }

    private async Task<Dictionary<string, string>> ResolveFeedActorNamesAsync(
        List<SyncshellCommunityFeedItemDto> page)
    {
        var uids = page.Where(i => i.ActorUid != null).Select(i => i.ActorUid!).Distinct().ToList();
        if (uids.Count == 0) return [];
        var rows = await DbContext.Users.AsNoTracking()
            .Where(u => uids.Contains(u.UID)).Select(u => new { u.UID, u.Alias, u.DisplayName })
            .ToListAsync().ConfigureAwait(false);
        // Display name first, alias second - a uid is never a display string.
        return rows.ToDictionary(u => u.UID,
            u => !string.IsNullOrWhiteSpace(u.DisplayName) ? u.DisplayName
                : u.Alias ?? string.Empty, StringComparer.Ordinal);
    }

    /// <summary>Deep-link tab for a comment/reaction target - News/Event map
    /// to their tabs; blog and comment-on-comment items carry no section
    /// (blog nav resolves to the author's profile client-side).</summary>
    private static SyncshellCommunitySection? SectionOf(int targetKind) => targetKind switch
    {
        (int)CommentTargetKind.News => SyncshellCommunitySection.News,
        (int)CommentTargetKind.Event => SyncshellCommunitySection.Events,
        _ => null,
    };

    private static string? Snip(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        // Snippets are single-line previews - markdown must be flattened or
        // tooltips/cards would show raw markup.
        var plain = Utils.MarkdownText.Strip(body);
        if (plain.Length == 0) return null;
        return plain.Length <= 140 ? plain : plain[..140].TrimEnd() + "…";
    }

    /// <summary>Opaque cursor: base64url of "tsTicks:kind:itemId:actorUid".</summary>
    private static string EncodeFeedCursor(SyncshellCommunityFeedItemDto item)
    {
        var raw = $"{item.TimestampUtc.Ticks}:{(int)item.Kind}:{item.ItemId ?? item.Id}:{item.ActorUid}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    /// <summary>Decodes a cursor; malformed input degrades to "first page"
    /// instead of failing the request.</summary>
    private static FeedCursor? DecodeFeedCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return null;
        try
        {
            var padded = cursor.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(padded)).Split(':');
            if (parts.Length < 3) return null;
            return new FeedCursor(new DateTime(long.Parse(parts[0]), DateTimeKind.Utc),
                int.Parse(parts[1]), long.Parse(parts[2]),
                parts.Length > 3 && parts[3].Length > 0 ? parts[3] : null);
        }
        catch { return null; }
    }

    /// <summary>Keyset predicate matching the ORDER BY tuple exactly.</summary>
    private static bool CursorAdmits(FeedCursor? c, SyncshellCommunityFeedItemDto i)
    {
        if (c == null) return true;
        var itemId = i.ItemId ?? i.Id;
        if (i.TimestampUtc != c.Ts) return i.TimestampUtc < c.Ts;
        if ((int)i.Kind != c.Kind) return (int)i.Kind > c.Kind;
        if (itemId != c.ItemId) return itemId < c.ItemId;
        return string.CompareOrdinal(i.ActorUid, c.ActorUid) > 0;
    }

    private sealed record FeedCursor(DateTime Ts, int Kind, long ItemId, string? ActorUid);
}

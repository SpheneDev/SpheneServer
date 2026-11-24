using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Sphene.API.Data.Enum;
using Sphene.API.Dto.Group;
using SpheneServer.Services;
using SpheneServer.Utils;
using SpheneShared.Models;
using System.Globalization;
using System.Linq.Expressions;
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
/// Every source applies the keyset already at candidate selection - base
/// sources (news/events/photos) via <see cref="ApplyFeedKeyset{T}"/>, activity
/// sources via their <c>ts &lt;= cursor.ts</c> overfetch - so a fixed-size
/// preselection can never hide rows the cursor would admit.
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

            // v2 applies the cursor inside the candidate queries - the v1
            // twelve-per-kind preselection would hide every older row beyond
            // it, no matter how far the cursor paged.
            var baseItems = (await CollectCommunityFeedItems(gids, accents, aliases,
                    cursor, perSource).ConfigureAwait(false))
                .Concat(await CollectFollowedFeedEventsAsync(followed, accents, aliases,
                    cursor, perSource).ConfigureAwait(false))
                .ToList();
            // Base sources carry their deep-link fields here - the v1 shape
            // leaves Section/ItemId unset.
            foreach (var it in baseItems)
            {
                var section = it.Kind switch
                {
                    SyncshellFeedItemKind.News => SyncshellCommunitySection.News,
                    SyncshellFeedItemKind.Event => SyncshellCommunitySection.Events,
                    SyncshellFeedItemKind.Photo => SyncshellCommunitySection.Gallery,
                    _ => (SyncshellCommunitySection?)null,
                };
                items.Add(it with { Section = section, ItemId = it.Id });
            }

            await CollectFeedCommentsAsync(gids, accents, aliases, cursor, perSource, items)
                .ConfigureAwait(false);
            await CollectFeedReactionsAsync(gids, accents, aliases, cursor, perSource, items)
                .ConfigureAwait(false);
            await CollectFeedRsvpsAsync(gids, accents, aliases, cursor, perSource, items)
                .ConfigureAwait(false);
        }

        // Blog origin rule (E1): author must be a mutual pair or shell
        // co-member - `GetAllPairedUnpausedUsers` covers both; scope and
        // block checks apply on top.
        await CollectFeedBlogsAsync(gids, cursor, perSource, items).ConfigureAwait(false);

        // Full tuple predicate, deterministic order, page trim - take+1
        // detects "more pages" without a second count pass.
        var candidates = items.Where(i => CursorAdmits(cursor, i))
            .OrderByDescending(i => i.TimestampUtc)
            .ThenBy(i => (int)i.Kind)
            .ThenByDescending(i => i.ItemId ?? i.Id)
            .ThenBy(i => i.ActorUid, StringComparer.Ordinal)
            .ThenBy(i => i.FeedRowKey, StringComparer.Ordinal)
            .Take(take + 1).ToList();
        var saturated = candidates.Count > take;
        var page = candidates.Take(take).ToList();

        var names = await ResolveFeedActorNamesAsync(page).ConfigureAwait(false);
        page = page.Select(i => i.ActorUid != null && names.TryGetValue(i.ActorUid, out var n)
            ? i with { ActorDisplayName = n } : i).ToList();
        page = await FillFeedEngagementAsync(page).ConfigureAwait(false);

        var next = saturated && page.Count > 0 ? EncodeFeedCursor(page[^1]) : null;
        return new SyncshellCommunityFeedPageDto(page, next);
    }

    /// <summary>
    /// The earliest eligible upcoming community event across member and
    /// followed shells - resolved independently of the feed's newest-first
    /// paging so the "Upcoming event" tile can't pick a later event that
    /// happens to sit on page one. Same visibility rules the feed's event
    /// slice uses (member shells: all published; followed shells: public
    /// only) - but strictly future, the tile's previous `&gt; now` filter,
    /// and cancelled events never qualify for a "next" slot.
    /// </summary>
    [Authorize(Policy = "Identified")]
    public async Task<SyncshellCommunityFeedItemDto?> GroupGetNextCommunityEvent()
    {
        _logger.LogCallInfo();
        var gids = await DbContext.GroupPairs.AsNoTracking()
            .Where(gp => gp.GroupUserUID == UserUID)
            .Select(gp => gp.GroupGID)
            .ToListAsync().ConfigureAwait(false);
        var followed = await FollowedNonMemberGidsAsync(gids).ConfigureAwait(false);
        if (gids.Count == 0 && followed.Count == 0) return null;

        var now = DateTime.UtcNow;
        var ev = await DbContext.SyncshellCommunityEvents.AsNoTracking()
            .Where(e => e.StartUtc > now
                && e.PublishStatus == (int)ContentPublishStatus.Published
                && e.EventStatus != (int)EventLifecycleStatus.Cancelled
                && (gids.Contains(e.GroupGID)
                    || (e.Visibility == (int)EventVisibility.Public
                        && followed.Contains(e.GroupGID))))
            .OrderBy(e => e.StartUtc).ThenBy(e => e.Id)
            .Select(e => new
            {
                e.GroupGID, e.Id, e.Title, e.Location, e.StartUtc, e.CreatedAt,
                e.PreviewMediaHash, e.ImageMediaHash,
            })
            .FirstOrDefaultAsync().ConfigureAwait(false);
        if (ev == null) return null;

        var accent = await DbContext.SyncshellWelcomePages.AsNoTracking()
            .Where(w => w.GroupGID == ev.GroupGID)
            .Select(w => w.AccentColor).FirstOrDefaultAsync().ConfigureAwait(false);
        var alias = await DbContext.Groups.AsNoTracking()
            .Where(g => g.GID == ev.GroupGID)
            .Select(g => g.Alias).FirstOrDefaultAsync().ConfigureAwait(false);

        return new SyncshellCommunityFeedItemDto(
            ev.GroupGID, alias, accent,
            SyncshellFeedItemKind.Event, ev.Id, ev.Title, ev.Location, ev.StartUtc,
            Section: SyncshellCommunitySection.Events, ItemId: ev.Id,
            ThumbnailMediaHash: ev.PreviewMediaHash ?? ev.ImageMediaHash,
            ActivityUtc: ev.CreatedAt, FullMediaHash: ev.ImageMediaHash);
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
        var perSource = take * 2 + 1;
        var items = new List<SyncshellCommunityFeedItemDto>(take * 4);
        var now = DateTime.UtcNow;

        await CollectProfileBlogsAsync(targetUid, isSelf, profileSealed, related,
            cursor, perSource, now, items).ConfigureAwait(false);
        await CollectProfilePostsAsync(targetUid, gidSet, cursor, perSource, items)
            .ConfigureAwait(false);
        var callerBlocked = new HashSet<string>(await DbContext.UserBlocks.AsNoTracking()
            .Where(b => b.UserUID == UserUID || b.BlockedUserUID == UserUID)
            .Select(b => b.UserUID == UserUID ? b.BlockedUserUID : b.UserUID)
            .ToListAsync().ConfigureAwait(false), StringComparer.Ordinal);
        await CollectProfileInteractionsAsync(targetUid, gidSet, related,
            callerBlocked, cursor, perSource, now, items).ConfigureAwait(false);

        var candidates = items.Where(i => CursorAdmits(cursor, i))
            .OrderByDescending(i => i.TimestampUtc)
            .ThenBy(i => (int)i.Kind)
            .ThenByDescending(i => i.ItemId ?? i.Id)
            .ThenBy(i => i.ActorUid, StringComparer.Ordinal)
            .ThenBy(i => i.FeedRowKey, StringComparer.Ordinal)
            .Take(take + 1).ToList();
        var saturated = candidates.Count > take;
        var page = candidates.Take(take).ToList();

        var names = await ResolveFeedActorNamesAsync(page).ConfigureAwait(false);
        page = page.Select(i => i.ActorUid != null && names.TryGetValue(i.ActorUid, out var n)
            ? i with { ActorDisplayName = n } : i).ToList();
        page = await FillFeedEngagementAsync(page).ConfigureAwait(false);
        var next = saturated && page.Count > 0 ? EncodeFeedCursor(page[^1]) : null;
        return new SyncshellCommunityFeedPageDto(page, next);
    }

    /// <summary>Target's blog posts - post-level ACL (scope vs caller).
    /// Post id is unique per row, so the id leg alone resolves the keyset.</summary>
    private async Task CollectProfileBlogsAsync(string targetUid, bool isSelf,
        bool profileSealed, HashSet<string> related, FeedCursor? cursor,
        int perSource, DateTime now, List<SyncshellCommunityFeedItemDto> sink)
    {
        if (profileSealed) return;
        var pairsVisible = isSelf || related.Contains(targetUid);
        // Publication + scope go into SQL before the take - a window full of
        // drafts or scope-blocked posts must not starve the eligible rows
        // behind it (PublishingService.IsVisible semantics, inlined).
        var q = DbContext.UserProfileBlogPosts.AsNoTracking()
            .Where(b => b.UserUID == targetUid
                && (b.Status == (int)ContentPublishStatus.Published
                    || (b.Status == (int)ContentPublishStatus.Scheduled
                        && b.ScheduledForUtc <= now)));
        if (!isSelf) q = q.Where(b => b.VisibilityScope != (int)BlogVisibilityScope.Private);
        if (!pairsVisible) q = q.Where(b => b.VisibilityScope != (int)BlogVisibilityScope.Pairs);
        q = ApplyFeedKeyset(q, cursor, SyncshellFeedItemKind.Blog,
            b => b.CreatedAt, b => b.Id);
        var rows = await q.OrderByDescending(b => b.CreatedAt)
            .ThenByDescending(b => b.Id).Take(perSource)
            .Select(b => new { b.Id, b.Title, b.Body, b.CreatedAt, b.CoverMediaHash,
                b.CommentCount, b.ViewCount })
            .ToListAsync().ConfigureAwait(false);
        foreach (var b in rows)
        {
            sink.Add(new SyncshellCommunityFeedItemDto(
                string.Empty, null, null,
                SyncshellFeedItemKind.Blog, b.Id, b.Title, Snip(b.Body), b.CreatedAt,
                Section: null, ItemId: b.Id, ActorUid: targetUid,
                ThumbnailMediaHash: b.CoverMediaHash, TargetOwnerUid: targetUid,
                CommentCount: b.CommentCount, ViewCount: b.ViewCount));
        }
    }

    /// <summary>Target's published news/events in caller-shared shells.</summary>
    private async Task CollectProfilePostsAsync(string targetUid,
        HashSet<string> gids, FeedCursor? cursor, int perSource,
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
        newsQ = ApplyFeedKeyset(newsQ, cursor, SyncshellFeedItemKind.News,
            n => n.PublishedAt, n => n.Id);
        var news = await newsQ.OrderByDescending(n => n.PublishedAt)
            .ThenByDescending(n => n.Id).Take(perSource)
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
        evQ = ApplyFeedKeyset(evQ, cursor, SyncshellFeedItemKind.Event,
            e => e.CreatedAt, e => e.Id);
        var events = await evQ.OrderByDescending(e => e.CreatedAt)
            .ThenByDescending(e => e.Id).Take(perSource)
            .Select(e => new { e.Id, e.GroupGID, e.Title, e.CreatedAt,
                e.PreviewMediaHash, e.ImageMediaHash })
            .ToListAsync().ConfigureAwait(false);
        foreach (var e in events)
        {
            sink.Add(new SyncshellCommunityFeedItemDto(
                e.GroupGID, aliases.GetValueOrDefault(e.GroupGID), null,
                SyncshellFeedItemKind.Event, e.Id, e.Title, null, e.CreatedAt,
                Section: SyncshellCommunitySection.Events, ItemId: e.Id, ActorUid: targetUid,
                ThumbnailMediaHash: e.PreviewMediaHash ?? e.ImageMediaHash,
                ActivityUtc: e.CreatedAt, FullMediaHash: e.ImageMediaHash));
        }
    }

    /// <summary>Target's visible comments + reactions on hosts the caller
    /// can access - host ACL resolved per row (news/event: shared gid +
    /// published; blog: post-level gates against the *post's* owner).
    /// Same keyset contract as the feed collectors: the full tuple
    /// predicate runs before the take, never after it. The host ACL spans
    /// three tables plus per-owner blog gates and stays in memory, so each
    /// source keeps scanning deterministic batches until the page is full
    /// or exhausted - a window of inaccessible hosts can't end paging early.
    /// </summary>
    private async Task CollectProfileInteractionsAsync(string targetUid,
        HashSet<string> gids, HashSet<string> related, HashSet<string> callerBlocked,
        FeedCursor? cursor, int perSource, DateTime now,
        List<SyncshellCommunityFeedItemDto> sink)
    {
        var comments = await ScanProfileCommentsAsync(targetUid, gids, related,
            callerBlocked, cursor, perSource, now).ConfigureAwait(false);
        var reactions = await ScanProfileReactionsAsync(targetUid, gids, related,
            callerBlocked, cursor, perSource, now).ConfigureAwait(false);

        var aliases = await DbContext.Groups.AsNoTracking()
            .Where(g => gids.Contains(g.GID))
            .Select(g => new { g.GID, g.Alias })
            .ToDictionaryAsync(g => g.GID, g => g.Alias).ConfigureAwait(false);
        var titles = await ResolveFeedTargetTitlesAsync(
            comments.Select(c => (c.TargetKind, c.TargetId))
                .Concat(reactions.Select(r => (r.TargetKind, r.TargetId))))
            .ConfigureAwait(false);
        // Comments can only target News/Event/Blog; only reactions may carry
        // a comment target - resolve those to the comment's host post.
        var commentHosts = await ResolveFeedCommentHostsAsync(
            reactions.Where(r => r.TargetKind == (int)ReactionTargetKind.Comment)
                .Select(r => r.TargetId)).ConfigureAwait(false);

        foreach (var c in comments)
        {
            titles.TryGetValue((c.TargetKind, c.TargetId), out var title);
            var gid = c.GroupGID ?? string.Empty;
            sink.Add(new SyncshellCommunityFeedItemDto(
                gid, gid.Length > 0 ? aliases.GetValueOrDefault(gid) : null, null,
                SyncshellFeedItemKind.Comment, c.Id,
                $"Comment on {title ?? "a post"}", Snip(c.Body), c.CreatedAt,
                Section: SectionOf(c.TargetKind), ItemId: c.TargetId, ActorUid: targetUid,
                SubItemId: c.Id, TargetOwnerUid: c.OwnerUid,
                FeedRowKey: c.Id.ToString("D20")));
        }
        foreach (var r in reactions)
        {
            var host = r.TargetKind == (int)ReactionTargetKind.Comment
                && commentHosts.TryGetValue(r.TargetId, out var rh) ? rh : (Kind: r.TargetKind, Id: r.TargetId);
            titles.TryGetValue((host.Kind, host.Id), out var title);
            var gid = r.GroupGID ?? string.Empty;
            sink.Add(new SyncshellCommunityFeedItemDto(
                gid, gid.Length > 0 ? aliases.GetValueOrDefault(gid) : null, null,
                SyncshellFeedItemKind.Reaction, r.TargetId,
                $"Reaction on {title ?? "a post"}",
                r.EmojiSeq, r.CreatedAt,
                Section: SectionOf(host.Kind), ItemId: host.Id, ActorUid: targetUid,
                SubItemId: r.TargetKind == (int)ReactionTargetKind.Comment
                    ? r.TargetId : null,
                TargetOwnerUid: r.OwnerUid,
                FeedRowKey: string.Create(CultureInfo.InvariantCulture,
                    $"{r.TargetKind:D2};{r.TargetId:D20};{r.Kind:D3}")));
        }
    }

    /// <summary>Batched scan of the target's comments - ACL-filters each
    /// batch against its own hosts and advances the scan cursor to the
    /// last row's emitted tuple until <paramref name="perSource"/> eligible
    /// rows collected or the table ran out.</summary>
    private async Task<List<ProfileCommentRow>> ScanProfileCommentsAsync(
        string targetUid, HashSet<string> gids, HashSet<string> related,
        HashSet<string> callerBlocked, FeedCursor? cursor, int perSource, DateTime now)
    {
        var visible = (int)CommentState.Visible;
        var batchTake = perSource * 2;
        var collected = new List<ProfileCommentRow>(perSource);
        var scan = cursor;
        while (collected.Count < perSource)
        {
            var crowId = long.TryParse(scan?.RowKey, out var rid) ? rid : 0L;
            var cq = DbContext.CommunityComments.AsNoTracking()
                .Where(c => c.AuthorUid == targetUid && c.State == visible);
            cq = ApplyFeedKeyset(cq, scan, SyncshellFeedItemKind.Comment,
                c => c.CreatedAt, c => c.TargetId,
                cur => c => string.Compare(c.AuthorUid, cur.ActorUid) > 0
                    || (c.AuthorUid == cur.ActorUid && c.Id > crowId));
            var batch = await cq.OrderByDescending(c => c.CreatedAt)
                .ThenByDescending(c => c.TargetId).ThenBy(c => c.AuthorUid)
                .ThenBy(c => c.Id).Take(batchTake)
                .Select(c => new ProfileCommentRow(c.Id, c.TargetKind, c.TargetId,
                    c.GroupGID, c.OwnerUid, c.Body, c.CreatedAt))
                .ToListAsync().ConfigureAwait(false);
            if (batch.Count == 0) break;
            var hosts = await VisibleHostsForCallerAsync(
                batch.Select(c => (c.TargetKind, c.TargetId)),
                gids, related, callerBlocked, now).ConfigureAwait(false);
            foreach (var c in batch)
            {
                if (hosts.Contains((c.TargetKind, c.TargetId))) collected.Add(c);
            }
            if (batch.Count < batchTake) break; // source exhausted
            var last = batch[^1];
            scan = new FeedCursor(last.CreatedAt, (int)SyncshellFeedItemKind.Comment,
                last.TargetId, targetUid, last.Id.ToString("D20"));
        }
        return collected;
    }

    /// <summary>Batched scan of the target's reactions - resolves each
    /// comment target to its host post BEFORE the ACL check, so a reaction
    /// on an otherwise-unreferenced host still gets judged.</summary>
    private async Task<List<ProfileReactionRow>> ScanProfileReactionsAsync(
        string targetUid, HashSet<string> gids, HashSet<string> related,
        HashSet<string> callerBlocked, FeedCursor? cursor, int perSource, DateTime now)
    {
        var commentKind = (int)ReactionTargetKind.Comment;
        var batchTake = perSource * 2;
        var collected = new List<ProfileReactionRow>(perSource);
        var scan = cursor;
        while (collected.Count < perSource)
        {
            var (rtk, rtid, rk) = ParseRowKeyTail(scan);
            var rq = DbContext.CommunityReactions.AsNoTracking()
                .Where(r => r.UserUID == targetUid)
                .GroupJoin(DbContext.CommunityComments,
                    r => r.TargetId, c => c.Id, (r, hosts) => new { r, hosts })
                .SelectMany(x => x.hosts.DefaultIfEmpty(),
                    (x, h) => new
                    {
                        x.r,
                        EffId = h != null && x.r.TargetKind == commentKind
                            ? h.TargetId : x.r.TargetId,
                    });
            var keyed = ApplyFeedKeyset(rq, scan, SyncshellFeedItemKind.Reaction,
                x => x.r.CreatedAt, x => x.EffId,
                cur => x => string.Compare(x.r.UserUID, cur.ActorUid) > 0
                    || (x.r.UserUID == cur.ActorUid
                        && (x.r.TargetKind > rtk
                            || (x.r.TargetKind == rtk
                                && (x.r.TargetId > rtid
                                    || (x.r.TargetId == rtid && x.r.Kind > rk))))));
            var batch = await keyed
                .OrderByDescending(x => x.r.CreatedAt).ThenByDescending(x => x.EffId)
                .ThenBy(x => x.r.UserUID)
                .ThenBy(x => x.r.TargetKind).ThenBy(x => x.r.TargetId).ThenBy(x => x.r.Kind)
                .Take(batchTake)
                .Select(x => new ProfileReactionRow(x.r.TargetKind, x.r.TargetId,
                    x.r.GroupGID, x.r.OwnerUid, x.r.Kind, x.r.CreatedAt, x.EffId, x.r.EmojiSeq))
                .ToListAsync().ConfigureAwait(false);
            if (batch.Count == 0) break;
            // Resolve comment targets to their host post - the ACL judges
            // the host tuple the row will emit, not the comment id.
            var commentHosts = await ResolveFeedCommentHostsAsync(
                batch.Where(r => r.TargetKind == commentKind)
                    .Select(r => r.TargetId)).ConfigureAwait(false);
            var hosts = await VisibleHostsForCallerAsync(
                batch.Select(r => r.TargetKind == commentKind
                    && commentHosts.TryGetValue(r.TargetId, out var h)
                        ? h : (r.TargetKind, r.TargetId)),
                gids, related, callerBlocked, now).ConfigureAwait(false);
            foreach (var r in batch)
            {
                var host = r.TargetKind == commentKind
                    && commentHosts.TryGetValue(r.TargetId, out var h)
                        ? h : (r.TargetKind, r.TargetId);
                if (hosts.Contains(host)) collected.Add(r);
            }
            if (batch.Count < batchTake) break; // source exhausted
            var last = batch[^1];
            scan = new FeedCursor(last.CreatedAt, (int)SyncshellFeedItemKind.Reaction,
                last.EffId, targetUid,
                string.Create(CultureInfo.InvariantCulture,
                    $"{last.TargetKind:D2};{last.TargetId:D20};{last.Kind:D3}"));
        }
        return collected;
    }

    private sealed record ProfileCommentRow(long Id, int TargetKind, long TargetId,
        string? GroupGID, string? OwnerUid, string? Body, DateTime CreatedAt);

    private sealed record ProfileReactionRow(int TargetKind, long TargetId,
        string? GroupGID, string? OwnerUid, int Kind, DateTime CreatedAt, long EffId,
        string EmojiSeq = "");

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

    /// <summary>Comments + replies on content the caller authored. The
    /// emitted item id is the TARGET post, not the comment - several rows
    /// can share (ts, target, actor) only via distinct comments, so the
    /// keyset binds the actor leg and the row key disambiguates the rest.</summary>
    private async Task CollectFeedCommentsAsync(List<string> gids,
        Dictionary<string, string> accents, Dictionary<string, string> aliases,
        FeedCursor? cursor, int perSource, List<SyncshellCommunityFeedItemDto> sink)
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
        // Exact tail: actor leg, then the row id the FeedRowKey encodes.
        var crowId = long.TryParse(cursor?.RowKey, out var rid) ? rid : 0L;
        q = ApplyFeedKeyset(q, cursor, SyncshellFeedItemKind.Comment,
            c => c.CreatedAt, c => c.TargetId,
            cur => c => string.Compare(c.AuthorUid, cur.ActorUid) > 0
                || (c.AuthorUid == cur.ActorUid && c.Id > crowId));
        // Same order the merge applies: identical-timestamp windows page
        // through deterministically instead of leaking rows past the take.
        var rows = await q.OrderByDescending(c => c.CreatedAt)
            .ThenByDescending(c => c.TargetId)
            .ThenBy(c => c.AuthorUid).ThenBy(c => c.Id)
            .Take(perSource)
            .Select(c => new { c.Id, c.TargetKind, c.TargetId, c.GroupGID, c.OwnerUid, c.AuthorUid, c.Body, c.CreatedAt })
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
                SubItemId: c.Id, TargetOwnerUid: c.OwnerUid,
                FeedRowKey: c.Id.ToString("D20")));
        }
    }

    /// <summary>Reactions on content the caller authored (posts + comments).
    /// The emitted item id is the resolved HOST post - comment-target
    /// reactions page on the comment's post, which the keyset computes
    /// through a left join so selection order matches the emitted tuple.</summary>
    private async Task CollectFeedReactionsAsync(List<string> gids,
        Dictionary<string, string> accents, Dictionary<string, string> aliases,
        FeedCursor? cursor, int perSource, List<SyncshellCommunityFeedItemDto> sink)
    {
        var commentKind = (int)ReactionTargetKind.Comment;
        var q = DbContext.CommunityReactions.AsNoTracking()
            .Where(r => r.UserUID != UserUID)
            .Where(r => r.OwnerUid == UserUID // my blog posts
                || (r.TargetKind == (int)ReactionTargetKind.News
                    && DbContext.SyncshellCommunityNews.Any(n => n.Id == r.TargetId && n.AuthorUid == UserUID))
                || (r.TargetKind == (int)ReactionTargetKind.Event
                    && DbContext.SyncshellCommunityEvents.Any(e => e.Id == r.TargetId && e.AuthorUid == UserUID))
                || (r.TargetKind == commentKind
                    && DbContext.CommunityComments.Any(c => c.Id == r.TargetId && c.AuthorUid == UserUID)))
            // Effective item id: the host post id for comment targets, the
            // target id itself otherwise - the same value the item emits.
            .GroupJoin(DbContext.CommunityComments,
                r => r.TargetId, c => c.Id, (r, hosts) => new { r, hosts })
            .SelectMany(x => x.hosts.DefaultIfEmpty(),
                (x, h) => new
                {
                    x.r,
                    EffId = h != null && x.r.TargetKind == commentKind
                        ? h.TargetId : x.r.TargetId,
                });
        // Exact tail: actor leg, then the composite PK segments the
        // FeedRowKey encodes - (target kind, target id, reaction kind).
        var (rtk, rtid, rk) = ParseRowKeyTail(cursor);
        var keyed = ApplyFeedKeyset(q, cursor, SyncshellFeedItemKind.Reaction,
            x => x.r.CreatedAt, x => x.EffId,
            cur => x => string.Compare(x.r.UserUID, cur.ActorUid) > 0
                || (x.r.UserUID == cur.ActorUid
                    && (x.r.TargetKind > rtk
                        || (x.r.TargetKind == rtk
                            && (x.r.TargetId > rtid
                                || (x.r.TargetId == rtid && x.r.Kind > rk))))));
        // Deterministic order: the row-key tuple closes identical ties the
        // shared fields can't separate (same user, same post, same tick).
        var rows = await keyed
            .OrderByDescending(x => x.r.CreatedAt)
            .ThenByDescending(x => x.EffId)
            .ThenBy(x => x.r.UserUID)
            .ThenBy(x => x.r.TargetKind).ThenBy(x => x.r.TargetId).ThenBy(x => x.r.Kind)
            .Take(perSource)
            .Select(x => new { x.r.TargetKind, x.r.TargetId, x.r.GroupGID, x.r.OwnerUid, x.r.UserUID, x.r.Kind, x.r.CreatedAt, x.r.EmojiSeq })
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
                r.EmojiSeq, r.CreatedAt,
                Section: SectionOf(host.Kind),
                ItemId: host.Id, ActorUid: r.UserUID,
                // comment-target reactions keep the comment id as inner
                // drill-in - the client lands inside the thread
                SubItemId: r.TargetKind == (int)ReactionTargetKind.Comment
                    ? r.TargetId : null,
                TargetOwnerUid: r.OwnerUid,
                // (target kind, target id, reaction kind) plus the actor
                // already in the tuple is unique per reaction row.
                FeedRowKey: string.Create(CultureInfo.InvariantCulture,
                    $"{r.TargetKind:D2};{r.TargetId:D20};{r.Kind:D3}")));
        }
    }

    /// <summary>First-RSVP + promotion activity on events the caller authored.
    /// One participant row can emit two activity rows at distinct timestamps,
    /// so each leg pages through its own keyset window instead of a shared
    /// boundary - a dense same-tick window can't starve either leg.</summary>
    private async Task CollectFeedRsvpsAsync(List<string> gids,
        Dictionary<string, string> accents, Dictionary<string, string> aliases,
        FeedCursor? cursor, int perSource, List<SyncshellCommunityFeedItemDto> sink)
    {
        var baseQ = DbContext.CommunityEventParticipants.AsNoTracking()
            .Where(p => p.UserUID != UserUID)
            .Where(p => DbContext.SyncshellCommunityEvents.Any(e =>
                e.Id == p.EventId && e.AuthorUid == UserUID && gids.Contains(e.GroupGID)))
            .Join(DbContext.SyncshellCommunityEvents, p => p.EventId, e => e.Id,
                (p, e) => new
                {
                    e.GroupGID, EventId = e.Id, e.Title,
                    p.UserUID, p.CreatedAt, p.PromotedAt,
                });
        // Exact tail per leg: actor leg, then the constant leg key itself -
        // a "c" row can never re-enter once a "p" cursor passed it.
        var createdAdmit = string.CompareOrdinal("c", cursor?.RowKey) > 0;
        var promotedAdmit = string.CompareOrdinal("p", cursor?.RowKey) > 0;
        var created = await ApplyFeedKeyset(baseQ, cursor,
                SyncshellFeedItemKind.Rsvp,
                x => x.CreatedAt, x => x.EventId,
                cur => x => string.Compare(x.UserUID, cur.ActorUid) > 0
                    || (x.UserUID == cur.ActorUid && createdAdmit))
            .OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.EventId)
            .ThenBy(x => x.UserUID)
            .Take(perSource).ToListAsync().ConfigureAwait(false);
        var promoted = await ApplyFeedKeyset(
                baseQ.Where(x => x.PromotedAt != null), cursor,
                SyncshellFeedItemKind.Rsvp,
                x => x.PromotedAt!.Value, x => x.EventId,
                cur => x => string.Compare(x.UserUID, cur.ActorUid) > 0
                    || (x.UserUID == cur.ActorUid && promotedAdmit))
            .OrderByDescending(x => x.PromotedAt).ThenByDescending(x => x.EventId)
            .ThenBy(x => x.UserUID)
            .Take(perSource).ToListAsync().ConfigureAwait(false);
        foreach (var p in created)
        {
            sink.Add(new SyncshellCommunityFeedItemDto(
                p.GroupGID, aliases.GetValueOrDefault(p.GroupGID),
                accents.GetValueOrDefault(p.GroupGID),
                SyncshellFeedItemKind.Rsvp, p.EventId,
                $"RSVP - {p.Title}", "responded", p.CreatedAt,
                Section: SyncshellCommunitySection.Events,
                ItemId: p.EventId, ActorUid: p.UserUID, FeedRowKey: "c"));
        }
        foreach (var p in promoted)
        {
            sink.Add(new SyncshellCommunityFeedItemDto(
                p.GroupGID, aliases.GetValueOrDefault(p.GroupGID),
                accents.GetValueOrDefault(p.GroupGID),
                SyncshellFeedItemKind.Rsvp, p.EventId,
                $"RSVP - {p.Title}", "promoted to attending", p.PromotedAt!.Value,
                Section: SyncshellCommunitySection.Events,
                ItemId: p.EventId, ActorUid: p.UserUID, FeedRowKey: "p"));
        }
    }

    /// <summary>Blog posts by mutual pairs or shell co-members - origin rule +
    /// per-scope ACL; arbitrary public blogs never enter (Phase-5 E1).</summary>
    private async Task CollectFeedBlogsAsync(List<string> gids,
        FeedCursor? cursor, int perSource, List<SyncshellCommunityFeedItemDto> sink)
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
        // Author eligibility + publication go into SQL before the take -
        // drafts, blocked or profile-hidden authors can't fill the window
        // and starve the eligible rows behind it.
        var q = ApplyFeedKeyset(DbContext.UserProfileBlogPosts.AsNoTracking()
            .Where(b => related.Contains(b.UserUID)
                && b.VisibilityScope != (int)BlogVisibilityScope.Private
                && !blocked.Contains(b.UserUID)
                && validProfiles.Contains(b.UserUID)
                && (b.Status == (int)ContentPublishStatus.Published
                    || (b.Status == (int)ContentPublishStatus.Scheduled
                        && b.ScheduledForUtc <= now))),
            cursor, SyncshellFeedItemKind.Blog, b => b.CreatedAt, b => b.Id);
        // Post id is unique per row, so a deterministic id tiebreak fully
        // orders an identical-timestamp window.
        var rows = await q.OrderByDescending(b => b.CreatedAt)
            .ThenByDescending(b => b.Id).Take(perSource)
            .Select(b => new { b.Id, b.UserUID, b.Title, b.Body,
                b.CreatedAt, b.CoverMediaHash, b.CommentCount, b.ViewCount })
            .ToListAsync().ConfigureAwait(false);

        foreach (var b in rows)
        {
            sink.Add(new SyncshellCommunityFeedItemDto(
                string.Empty, null, null,
                SyncshellFeedItemKind.Blog, b.Id, b.Title, Snip(b.Body), b.CreatedAt,
                Section: null, ItemId: b.Id, ActorUid: b.UserUID,
                ThumbnailMediaHash: b.CoverMediaHash, TargetOwnerUid: b.UserUID,
                CommentCount: b.CommentCount, ViewCount: b.ViewCount));
        }
    }

    // ------------------------------------------------------------------
    //  Cursor + helpers
    // ------------------------------------------------------------------

    /// <summary>Stamps engagement counters onto the emitted page's content
    /// rows - reactions for News/Event/Blog and comments for News/Event via
    /// one grouped query per kind; blog comment totals come from the
    /// denormalized post column the profile surfaces already show (and the
    /// collectors stamp it). Activity/photo rows keep nulls, so the client
    /// never renders a strip of zeros.</summary>
    private async Task<List<SyncshellCommunityFeedItemDto>> FillFeedEngagementAsync(
        List<SyncshellCommunityFeedItemDto> page)
    {
        // Feed kind → comment/reaction target kind (the two enums share
        // ordinals for News/Event/Blog; Photo carries no thread).
        static int TargetOf(SyncshellFeedItemKind kind) => kind switch
        {
            SyncshellFeedItemKind.News => (int)CommentTargetKind.News,
            SyncshellFeedItemKind.Event => (int)CommentTargetKind.Event,
            SyncshellFeedItemKind.Blog => (int)CommentTargetKind.Blog,
            _ => -1,
        };
        var targetKinds = page.Select(i => TargetOf(i.Kind))
            .Where(k => k >= 0).Distinct().ToList();
        if (targetKinds.Count == 0) return page;

        var reactions = new Dictionary<(int, long), int>();
        var likeCounts = new Dictionary<(int, long), int>();
        var emojiCounts = new Dictionary<(int, long), List<FeedReactionEmojiDto>>();
        var comments = new Dictionary<(int, long), int>();
        var likedByMe = new HashSet<(int, long)>();
        var thumbsUp = ReactionEmojiMap.ThumbsUp;
        foreach (var tk in targetKinds)
        {
            var ids = page.Where(i => TargetOf(i.Kind) == tk)
                .Select(i => i.ItemId ?? i.Id).ToList();
            // Aggregated in memory: thumbs variants (prefix match - the skin
            // tone modifier follows the base glyph) count as likes and are
            // excluded from the per-emoji chips; everything else groups by
            // its exact sequence, most-reacted first.
            var rows = await DbContext.CommunityReactions.AsNoTracking()
                .Where(r => r.TargetKind == tk && ids.Contains(r.TargetId))
                .Select(r => new { r.TargetId, r.EmojiSeq, r.Kind, r.UserUID })
                .ToListAsync().ConfigureAwait(false);
            foreach (var g in rows.GroupBy(r => r.TargetId))
            {
                reactions[(tk, g.Key)] = g.Count();
                var likes = g.Count(r => r.Kind == (int)ReactionKind.Like
                    || r.EmojiSeq.StartsWith(thumbsUp, StringComparison.Ordinal));
                if (likes > 0) likeCounts[(tk, g.Key)] = likes;
                var chips = g.Where(r => r.Kind != (int)ReactionKind.Like
                        && !r.EmojiSeq.StartsWith(thumbsUp, StringComparison.Ordinal))
                    .GroupBy(r => r.EmojiSeq)
                    .Select(e => new FeedReactionEmojiDto(e.Key, e.Count(),
                        e.Any(r => r.UserUID == UserUID)))
                    .OrderByDescending(e => e.Count)
                    .ToList();
                if (chips.Count > 0) emojiCounts[(tk, g.Key)] = chips;
            }
            // The caller's own thumbs-up rows drive the active state +
            // toggle direction in the client's engagement strip.
            foreach (var id in rows.Where(r => r.UserUID == UserUID
                && (r.Kind == (int)ReactionKind.Like
                    || r.EmojiSeq.StartsWith(thumbsUp, StringComparison.Ordinal)))
                .Select(r => r.TargetId).Distinct())
            {
                likedByMe.Add((tk, id));
            }
            if (tk == (int)CommentTargetKind.Blog) continue;
            var visible = (int)CommentState.Visible;
            foreach (var g in await DbContext.CommunityComments.AsNoTracking()
                .Where(c => c.TargetKind == tk && ids.Contains(c.TargetId)
                    && c.State == visible)
                .GroupBy(c => c.TargetId)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToListAsync().ConfigureAwait(false))
            {
                comments[(tk, g.Key)] = g.Count;
            }
        }

        return page.Select(i =>
        {
            var tk = TargetOf(i.Kind);
            if (tk < 0) return i;
            var id = i.ItemId ?? i.Id;
            return i with
            {
                CommentCount = comments.TryGetValue((tk, id), out var cc)
                    ? cc : i.CommentCount,
                ReactionCount = reactions.TryGetValue((tk, id), out var rc)
                    ? rc : i.ReactionCount,
                LikeCount = likeCounts.TryGetValue((tk, id), out var lc) ? lc : 0,
                ReactionEmojis = emojiCounts.TryGetValue((tk, id), out var ec) ? ec : null,
                LikedByMe = likedByMe.Contains((tk, id)),
            };
        }).ToList();
    }


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
        return plain.Length <= 140 ? plain : plain[..140].TrimEnd() + "...";
    }

    /// <summary>Opaque cursor: base64url of
    /// "tsTicks:kind:itemId:actorUid:rowKey" - the row key keeps rows whose
    /// shared tuple fields all match distinct across a page boundary.
    /// <see cref="SyncshellCommunityFeedItemDto.FeedRowKey"/> uses ';'
    /// separators so the ':' split stays unambiguous.</summary>
    private static string EncodeFeedCursor(SyncshellCommunityFeedItemDto item)
    {
        var raw = $"{item.TimestampUtc.Ticks}:{(int)item.Kind}:{item.ItemId ?? item.Id}:{item.ActorUid}:{item.FeedRowKey}";
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
                parts.Length > 3 && parts[3].Length > 0 ? parts[3] : null,
                parts.Length > 4 && parts[4].Length > 0 ? parts[4] : null);
        }
        catch { return null; }
    }

    /// <summary>
    /// Restricts one source query to rows strictly after the cursor in the
    /// shared <c>(ts DESC, kind ASC, id DESC)</c> feed order - applied at
    /// candidate selection so the per-source <c>Take</c> can't starve older
    /// rows behind a fixed preselection. The kind leg collapses into a single
    /// id bound: a source ordered after the cursor's kind admits every row at
    /// the boundary tick, the cursor's own kind admits ids below its anchor,
    /// and earlier kinds admit only older ticks.
    /// </summary>
    private static IQueryable<T> ApplyFeedKeyset<T>(IQueryable<T> query,
        FeedCursor? cursor, SyncshellFeedItemKind kind,
        Expression<Func<T, DateTime>> timestamp, Expression<Func<T, long>> id)
    {
        if (cursor == null) return query;
        var cts = cursor.Ts;
        var idBound = (int)kind > cursor.Kind ? long.MaxValue
            : (int)kind == cursor.Kind ? cursor.ItemId
            : long.MinValue;
        var param = Expression.Parameter(typeof(T), "x");
        var ts = Rebind(timestamp, param);
        var key = Rebind(id, param);
        var sameTick = Expression.Equal(ts, Expression.Constant(cts));
        Expression pred = Expression.OrElse(
            Expression.LessThan(ts, Expression.Constant(cts)),
            Expression.AndAlso(sameTick,
                Expression.LessThan(key, Expression.Constant(idBound))));
        return query.Where(Expression.Lambda<Func<T, bool>>(pred, param));
    }

    /// <summary>Splits a reaction row key back into its composite-PK
    /// segments - emitted as "targetKind;targetId;kind" with fixed-width
    /// zero padding, so plain parses restore the cursor's last leg.</summary>
    private static (int Tk, long Tid, int Rk) ParseRowKeyTail(FeedCursor? cursor)
    {
        var segs = (cursor?.RowKey ?? string.Empty).Split(';');
        return (segs.Length > 0 && int.TryParse(segs[0], out var a) ? a : 0,
            segs.Length > 1 && long.TryParse(segs[1], out var b) ? b : 0L,
            segs.Length > 2 && int.TryParse(segs[2], out var c) ? c : 0);
    }

    /// <summary>Rebinds a selector's parameter onto a shared lambda parameter.</summary>
    private static Expression Rebind(LambdaExpression selector, ParameterExpression target)
        => new KeysetParamSwap(selector.Parameters[0], target).Visit(selector.Body);

    /// <summary>Rebinds a selector's parameter onto the shared lambda parameter.</summary>
    private sealed class KeysetParamSwap(ParameterExpression source, ParameterExpression target)
        : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node)
            => node == source ? target : node;
    }

    /// <summary>
    /// Keyset variant for activity sources whose remaining tuple legs vary
    /// per row. <paramref name="tail"/> builds the exact continuation
    /// predicate applied when <c>id == bound</c> - actor, then the row
    /// discriminator - so the candidate window can never fill up with rows
    /// the cursor already delivered. Semantics must match
    /// <see cref="CursorAdmits"/> leg for leg.
    /// </summary>
    private static IQueryable<T> ApplyFeedKeyset<T>(IQueryable<T> query,
        FeedCursor? cursor, SyncshellFeedItemKind kind,
        Expression<Func<T, DateTime>> timestamp, Expression<Func<T, long>> id,
        Func<FeedCursor, Expression<Func<T, bool>>> tail)
    {
        if (cursor == null) return query;
        var cts = cursor.Ts;
        var idBound = (int)kind > cursor.Kind ? long.MaxValue
            : (int)kind == cursor.Kind ? cursor.ItemId
            : long.MinValue;
        var param = Expression.Parameter(typeof(T), "x");
        var ts = Rebind(timestamp, param);
        var key = Rebind(id, param);
        var sameTick = Expression.Equal(ts, Expression.Constant(cts));
        Expression pred = Expression.OrElse(
            Expression.LessThan(ts, Expression.Constant(cts)),
            Expression.AndAlso(sameTick,
                Expression.OrElse(
                    Expression.LessThan(key, Expression.Constant(idBound)),
                    Expression.AndAlso(
                        Expression.Equal(key, Expression.Constant(idBound)),
                        Rebind(tail(cursor), param)))));
        return query.Where(Expression.Lambda<Func<T, bool>>(pred, param));
    }

    /// <summary>Keyset predicate matching the ORDER BY tuple exactly - the
    /// row-key leg is the last resort for rows whose shared fields all tie.</summary>
    private static bool CursorAdmits(FeedCursor? c, SyncshellCommunityFeedItemDto i)
    {
        if (c == null) return true;
        var itemId = i.ItemId ?? i.Id;
        if (i.TimestampUtc != c.Ts) return i.TimestampUtc < c.Ts;
        if ((int)i.Kind != c.Kind) return (int)i.Kind > c.Kind;
        if (itemId != c.ItemId) return itemId < c.ItemId;
        var actorCmp = string.CompareOrdinal(i.ActorUid, c.ActorUid);
        if (actorCmp != 0) return actorCmp > 0;
        return string.CompareOrdinal(i.FeedRowKey, c.RowKey) > 0;
    }

    private sealed record FeedCursor(DateTime Ts, int Kind, long ItemId,
        string? ActorUid, string? RowKey);
}

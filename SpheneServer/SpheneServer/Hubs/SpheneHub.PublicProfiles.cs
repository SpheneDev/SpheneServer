using Sphene.API.Data;
using Sphene.API.Data.Enum;
using Sphene.API.Dto.Group;
using Sphene.API.Dto.User;
using SpheneServer.Services;
using SpheneServer.Utils;
using SpheneShared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace SpheneServer.Hubs;

/// <summary>
/// Public-profile surface: opt-in profile sharing, bounded search, the slim
/// public detail view, unilateral follow/unfollow and the "Following" feed.
/// One gate - <see cref="LoadPublicTargetAsync"/> - backs every public read so
/// private, blocked, disabled, flagged and unknown targets all degrade to the
/// same "not available" answer without existence disclosure.
/// </summary>
public partial class SpheneHub
{
    private const int PublicProfileSearchTakeMax = 50;

    /// <summary>
    /// The single public-visibility authority: profile row exists, opted in,
    /// not disabled, not flagged, no block in either direction. Returns null
    /// on every failure - callers never distinguish the reason.
    /// </summary>
    private async Task<UserProfileData?> LoadPublicTargetAsync(string uid)
    {
        var profile = await DbContext.UserProfileData.AsNoTracking()
            .Include(p => p.User)
            .SingleOrDefaultAsync(p => p.UserUID == uid).ConfigureAwait(false);
        if (profile == null || !profile.PublicProfileOptIn
            || profile.ProfileDisabled || profile.FlaggedForReport)
        {
            return null;
        }
        if (await IsBlockedEitherDirectionAsync(uid).ConfigureAwait(false))
        {
            return null;
        }
        return profile;
    }

    private Task<bool> IsBlockedEitherDirectionAsync(string otherUid)
        => string.Equals(otherUid, UserUID, StringComparison.Ordinal)
            ? Task.FromResult(false)
            : DbContext.UserBlocks.AsNoTracking().AnyAsync(b =>
                (b.UserUID == UserUID && b.BlockedUserUID == otherUid)
                || (b.UserUID == otherUid && b.BlockedUserUID == UserUID));

    /// <summary>Uids the caller blocks or is blocked by - bulk list filter.</summary>
    private async Task<HashSet<string>> CallerBlockSetAsync()
        => (await DbContext.UserBlocks.AsNoTracking()
            .Where(b => b.UserUID == UserUID || b.BlockedUserUID == UserUID)
            .Select(b => b.UserUID == UserUID ? b.BlockedUserUID : b.UserUID)
            .ToListAsync().ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);

    [Authorize(Policy = "Identified")]
    public async Task<List<PublicProfileSummaryDto>> UserSearchPublicProfiles(
        PublicProfileSearchDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto.Query));
        var take = Math.Clamp(dto.Take, 1, PublicProfileSearchTakeMax);
        var skip = Math.Max(0, dto.Skip);
        var query = dto.Query?.Trim() ?? string.Empty;
        if (query.Length < 2) return [];
        var lowered = query.ToLowerInvariant();
        var blocked = await CallerBlockSetAsync().ConfigureAwait(false);

        // Display-name first, alias second - the same identity preference the
        // client display pipeline uses. Slim projection only: no images, no
        // profile payload per hit.
        var rows = await DbContext.Users.AsNoTracking()
            .Where(u => (u.DisplayName != null && u.DisplayName.ToLower().Contains(lowered))
                || (u.Alias != null && u.Alias.ToLower().Contains(lowered)))
            .Join(DbContext.UserProfileData.AsNoTracking()
                .Where(p => p.PublicProfileOptIn && !p.ProfileDisabled && !p.FlaggedForReport),
                u => u.UID, p => p.UserUID,
                (u, p) => new
                {
                    u.UID, u.Alias, u.DisplayName, p.IsNSFW, p.ProfileImageMediaHash,
                    p.ProfileVisibility,
                })
            .Where(r => r.UID != UserUID && !blocked.Contains(r.UID))
            .OrderBy(r => r.DisplayName ?? r.Alias ?? r.UID)
            .ThenBy(r => r.UID)
            .Skip(skip).Take(take)
            .ToListAsync().ConfigureAwait(false);

        var uids = rows.Select(r => r.UID).ToList();
        var following = (await DbContext.UserFollows.AsNoTracking()
            .Where(f => f.FollowerUID == UserUID && uids.Contains(f.TargetUID))
            .Select(f => f.TargetUID).ToListAsync().ConfigureAwait(false))
            .ToHashSet(StringComparer.Ordinal);

        // HideProfilePicture seals the avatar hash the same way it seals the
        // base64 on the detail read - the mask is per-profile, not per-field.
        return rows.Select(r => new PublicProfileSummaryDto(
            new UserData(r.UID, r.Alias, false, r.DisplayName),
            ((ProfileVisibility)r.ProfileVisibility).HasFlag(ProfileVisibility.HideProfilePicture)
                ? null : r.ProfileImageMediaHash,
            r.IsNSFW, following.Contains(r.UID))).ToList();
    }

    [Authorize(Policy = "Identified")]
    public async Task<PublicProfileDto?> UserGetPublicProfile(UserDto user)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(user));
        var uid = await ResolveUidOrAliasAsync(user.User.UID).ConfigureAwait(false);
        if (uid == null) return null;
        var profile = await LoadPublicTargetAsync(uid).ConfigureAwait(false);
        if (profile == null) return null;

        // The public view shows exactly what a stranger sees - the field mask
        // applies without a self bypass; the owner reads the full record
        // through UserGetProfile instead.
        var vis = (ProfileVisibility)profile.ProfileVisibility;
        var visible = (ProfileVisibility flag) => !vis.HasFlag(flag);

        var now = DateTime.UtcNow;
        List<UserProfileBlogPostDto> chronicles = [];
        if (visible(ProfileVisibility.HideBlog))
        {
            var posts = await DbContext.UserProfileBlogPosts.AsNoTracking()
                .Where(b => b.UserUID == uid
                    && b.VisibilityScope == (int)BlogVisibilityScope.Public
                    && (b.Status == (int)ContentPublishStatus.Published
                        || (b.Status == (int)ContentPublishStatus.Scheduled
                            && b.ScheduledForUtc <= now)))
                .OrderByDescending(b => b.Pinned).ThenByDescending(b => b.CreatedAt)
                .Select(b => new
                {
                    b.Id, b.Title, b.Body, b.Pinned, b.CreatedAt, b.UpdatedAt,
                    b.Status, b.VisibilityScope, b.ScheduledForUtc, b.CoAuthorUidsJson,
                    b.CommentCount, b.CommentsLocked, b.CoverMediaHash, b.ViewCount,
                })
                .ToListAsync().ConfigureAwait(false);
            chronicles = posts.Select(b => new UserProfileBlogPostDto(
                b.Id, b.Title, b.Body, b.Pinned, b.CreatedAt, b.UpdatedAt, b.Status,
                b.VisibilityScope, b.ScheduledForUtc,
                PublishingService.ParseCoAuthors(b.CoAuthorUidsJson),
                b.CommentCount, b.CommentsLocked, b.CoverMediaHash, b.ViewCount)).ToList();
        }

        var following = await DbContext.UserFollows.AsNoTracking()
            .AnyAsync(f => f.FollowerUID == UserUID && f.TargetUID == uid)
            .ConfigureAwait(false);

        return new PublicProfileDto(
            profile.User.ToUserData(), profile.IsNSFW,
            visible(ProfileVisibility.HideProfilePicture) ? profile.Base64ProfileImage : null,
            visible(ProfileVisibility.HideBanner) ? profile.Base64BannerImage : null,
            visible(ProfileVisibility.HideDescription) ? profile.UserDescription : null,
            visible(ProfileVisibility.HideTags) ? profile.ProfileTags : null,
            following, chronicles);
    }

    [Authorize(Policy = "Identified")]
    public async Task<bool> UserSetProfileFollow(UserProfileFollowDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto.User, dto.Follow));
        var uid = await ResolveUidOrAliasAsync(dto.User.UID).ConfigureAwait(false);
        if (uid == null || string.Equals(uid, UserUID, StringComparison.Ordinal))
        {
            return false;
        }

        var existing = await DbContext.UserFollows
            .SingleOrDefaultAsync(f => f.FollowerUID == UserUID && f.TargetUID == uid)
            .ConfigureAwait(false);

        // Unfollow stays available for private/blocked/removed targets - the
        // edge is the caller's own data.
        if (!dto.Follow)
        {
            if (existing == null) return true;
            DbContext.UserFollows.Remove(existing);
            await DbContext.SaveChangesAsync().ConfigureAwait(false);
            return true;
        }

        if (existing != null) return true;

        // A new edge requires the target to be publicly visible at this exact
        // moment - a revoked profile mid-request rejects identically.
        if (await LoadPublicTargetAsync(uid).ConfigureAwait(false) == null)
        {
            return false;
        }

        try
        {
            await DbContext.UserFollows.AddAsync(new UserFollow
            {
                FollowerUID = UserUID,
                TargetUID = uid,
                FollowedAtUtc = DateTime.UtcNow,
            }).ConfigureAwait(false);
            await DbContext.SaveChangesAsync().ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is DbUpdateException
            or ArgumentException or InvalidOperationException)
        {
            // A concurrent duplicate follow may have won the unique key on
            // any store - verify the edge exists before conceding idempotent
            // success; anything else is a real failure and rethrows.
            var won = await DbContext.UserFollows.AsNoTracking().AnyAsync(
                f => f.FollowerUID == UserUID && f.TargetUID == uid)
                .ConfigureAwait(false);
            if (won) return true;
            throw;
        }
    }

    [Authorize(Policy = "Identified")]
    public async Task<SyncshellCommunityFeedPageDto> UserGetFollowingFeed(
        FeedPageRequestDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto.Take));
        var take = Math.Clamp(dto.Take, 1, FeedPageSizeMax);
        var cursor = DecodeFeedCursor(dto.Cursor);
        var now = DateTime.UtcNow;

        // Followed authors whose profile is currently publicly eligible and
        // whose Chronicles section isn't field-hidden. Blocks in either
        // direction cut the author from this page; the edge itself survives.
        var authors = await FollowingAuthorUidsAsync().ConfigureAwait(false);
        if (authors.Count == 0)
        {
            return new SyncshellCommunityFeedPageDto([], null);
        }

        // Visibility filter lands in SQL before the take, same contract as the
        // profile-activity blog collector - a window full of ineligible rows
        // must not starve later eligible ones.
        var q = DbContext.UserProfileBlogPosts.AsNoTracking()
            .Where(b => authors.Contains(b.UserUID)
                && b.VisibilityScope == (int)BlogVisibilityScope.Public
                && (b.Status == (int)ContentPublishStatus.Published
                    || (b.Status == (int)ContentPublishStatus.Scheduled
                        && b.ScheduledForUtc <= now)));
        q = ApplyFeedKeyset(q, cursor, SyncshellFeedItemKind.Blog,
            b => b.CreatedAt, b => b.Id);
        var rows = await q.OrderByDescending(b => b.CreatedAt)
            .ThenByDescending(b => b.Id).Take(take + 1)
            .Select(b => new
            {
                b.Id, b.Title, b.Body, b.CreatedAt, b.CoverMediaHash, b.UserUID,
                b.CommentCount, b.ViewCount,
            })
            .ToListAsync().ConfigureAwait(false);

        // The +1 row only proves a follow-up page exists - never delivered.
        // The cursor must bind the last ACTUALLY emitted row or that proof
        // row would be silently skipped on the next request.
        var delivered = rows.Take(take).ToList();
        var nsfwAuthors = (await DbContext.UserProfileData.AsNoTracking()
            .Where(p => authors.Contains(p.UserUID) && p.IsNSFW)
            .Select(p => p.UserUID).ToListAsync().ConfigureAwait(false))
            .ToHashSet(StringComparer.Ordinal);

        var page = delivered.Select(b => new SyncshellCommunityFeedItemDto(
            string.Empty, null, null,
            SyncshellFeedItemKind.Blog, b.Id, b.Title, Snip(b.Body), b.CreatedAt,
            Section: null, ItemId: b.Id, ActorUid: b.UserUID,
            ThumbnailMediaHash: b.CoverMediaHash, TargetOwnerUid: b.UserUID,
            OwnerIsNsfw: nsfwAuthors.Contains(b.UserUID),
            CommentCount: b.CommentCount, ViewCount: b.ViewCount))
            .ToList();

        var names = await ResolveFeedActorNamesAsync(page).ConfigureAwait(false);
        page = page.Select(i => i.ActorUid != null
                && names.TryGetValue(i.ActorUid, out var n)
            ? i with { ActorDisplayName = n } : i).ToList();
        page = await FillFeedEngagementAsync(page).ConfigureAwait(false);
        var next = rows.Count > take && page.Count > 0
            ? EncodeFeedCursor(page[^1]) : null;
        return new SyncshellCommunityFeedPageDto(page, next);
    }

    /// <summary>
    /// "For You" - a ranked top-N digest across the caller's community
    /// content (member + followed shells) and followed authors' public
    /// Chronicles. Not paged: scoring needs the whole candidate window, so
    /// the response is a single ranked slice and <c>NextCursor</c> stays
    /// null. Score = (1 + engagement) x exp(-ageHours/48) - quiet posts
    /// still rank on recency alone, engagement is honest counters only,
    /// followed authors get a 1.5x boost. Activity rows (comments,
    /// reactions, sign-ups) never enter - they belong to the Activity
    /// surface, not a recommendations feed.
    /// </summary>
    [Authorize(Policy = "Identified")]
    public async Task<SyncshellCommunityFeedPageDto> UserGetForYouFeed(
        FeedPageRequestDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto.Take));
        var take = Math.Clamp(dto.Take, 1, FeedPageSizeMax);
        var now = DateTime.UtcNow;

        var items = await CollectForYouCommunityAsync().ConfigureAwait(false);
        var authors = await FollowingAuthorUidsAsync().ConfigureAwait(false);
        var authorSet = authors.ToHashSet(StringComparer.Ordinal);
        await CollectForYouFollowingBlogsAsync(authors, now, items)
            .ConfigureAwait(false);

        // A followed author's post can surface through both the related blog
        // collector and the followed-author slice - dedupe on content key.
        items = items.GroupBy(i => (i.Kind, i.ItemId ?? i.Id))
            .Select(g => g.First()).ToList();

        var names = await ResolveFeedActorNamesAsync(items).ConfigureAwait(false);
        items = items.Select(i => i.ActorUid != null
                && names.TryGetValue(i.ActorUid, out var n)
            ? i with { ActorDisplayName = n } : i).ToList();
        items = await FillFeedEngagementAsync(items).ConfigureAwait(false);

        var page = items.OrderByDescending(i => ForYouScore(i, now, authorSet))
            .ThenByDescending(i => i.TimestampUtc)
            .ThenBy(i => (int)i.Kind)
            .ThenByDescending(i => i.ItemId ?? i.Id)
            .Take(take).ToList();
        return new SyncshellCommunityFeedPageDto(page, null);
    }

    /// <summary>Community content candidates for For You - member + followed
    /// shells' news/events/photos and related authors' blogs, newest-bounded
    /// per source (the ranking window, not a page).</summary>
    private async Task<List<SyncshellCommunityFeedItemDto>> CollectForYouCommunityAsync()
    {
        const int perSource = 20;
        var gids = await DbContext.GroupPairs.AsNoTracking()
            .Where(gp => gp.GroupUserUID == UserUID)
            .Select(gp => gp.GroupGID).ToListAsync().ConfigureAwait(false);
        var followed = await FollowedNonMemberGidsAsync(gids).ConfigureAwait(false);
        var allGids = gids.Union(followed, StringComparer.Ordinal).ToList();

        var items = new List<SyncshellCommunityFeedItemDto>(perSource * 4);
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

            var baseItems = (await CollectCommunityFeedItems(gids, accents,
                    aliases, null, perSource).ConfigureAwait(false))
                .Concat(await CollectFollowedFeedEventsAsync(followed, accents,
                    aliases, null, perSource).ConfigureAwait(false));
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
        }
        await CollectFeedBlogsAsync(gids, null, perSource, items).ConfigureAwait(false);
        return items;
    }

    /// <summary>Followed authors' public Chronicles - the same visibility
    /// gate the Following feed applies, bounded to the newest candidates.</summary>
    private async Task CollectForYouFollowingBlogsAsync(List<string> authors,
        DateTime now, List<SyncshellCommunityFeedItemDto> sink)
    {
        if (authors.Count == 0) return;
        const int perSource = 20;
        var rows = await DbContext.UserProfileBlogPosts.AsNoTracking()
            .Where(b => authors.Contains(b.UserUID)
                && b.VisibilityScope == (int)BlogVisibilityScope.Public
                && (b.Status == (int)ContentPublishStatus.Published
                    || (b.Status == (int)ContentPublishStatus.Scheduled
                        && b.ScheduledForUtc <= now)))
            .OrderByDescending(b => b.CreatedAt).ThenByDescending(b => b.Id)
            .Take(perSource)
            .Select(b => new { b.Id, b.UserUID, b.Title, b.Body, b.CreatedAt,
                b.CoverMediaHash, b.CommentCount, b.ViewCount })
            .ToListAsync().ConfigureAwait(false);
        var nsfwAuthors = (await DbContext.UserProfileData.AsNoTracking()
            .Where(p => authors.Contains(p.UserUID) && p.IsNSFW)
            .Select(p => p.UserUID).ToListAsync().ConfigureAwait(false))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var b in rows)
        {
            sink.Add(new SyncshellCommunityFeedItemDto(
                string.Empty, null, null,
                SyncshellFeedItemKind.Blog, b.Id, b.Title, Snip(b.Body),
                b.CreatedAt, Section: null, ItemId: b.Id, ActorUid: b.UserUID,
                ThumbnailMediaHash: b.CoverMediaHash, TargetOwnerUid: b.UserUID,
                OwnerIsNsfw: nsfwAuthors.Contains(b.UserUID),
                CommentCount: b.CommentCount, ViewCount: b.ViewCount));
        }
    }

    /// <summary>Rank = (1 + engagement) x exp(-ageHours/48) x follow boost.
    /// ActivityUtc (publication) is the honest freshness anchor - events
    /// carry their future StartUtc as TimestampUtc, which would let a
    /// far-out date fake recency.</summary>
    private static double ForYouScore(SyncshellCommunityFeedItemDto i,
        DateTime now, HashSet<string> followedAuthors)
    {
        var anchor = i.ActivityUtc ?? i.TimestampUtc;
        var ageHours = Math.Max(0d, (now - anchor).TotalHours);
        var engagement = 1d + 3d * (i.CommentCount ?? 0)
            + 2d * (i.ReactionCount ?? 0) + 0.1d * (i.ViewCount ?? 0);
        var boost = i.ActorUid != null && followedAuthors.Contains(i.ActorUid)
            ? 1.5d : 1d;
        return engagement * Math.Exp(-ageHours / 48d) * boost;
    }

    /// <summary>Followed targets currently allowed to contribute to the
    /// caller's Following feed - sharing on, not disabled/flagged, blog
    /// section not field-hidden, no block in either direction.</summary>
    private async Task<List<string>> FollowingAuthorUidsAsync()
    {
        var blocked = await CallerBlockSetAsync().ConfigureAwait(false);
        return await DbContext.UserFollows.AsNoTracking()
            .Where(f => f.FollowerUID == UserUID)
            .Join(DbContext.UserProfileData.AsNoTracking()
                .Where(p => p.PublicProfileOptIn && !p.ProfileDisabled && !p.FlaggedForReport
                    && (p.ProfileVisibility & (int)ProfileVisibility.HideBlog) == 0),
                f => f.TargetUID, p => p.UserUID, (f, p) => f.TargetUID)
            .Where(u => !blocked.Contains(u))
            .ToListAsync().ConfigureAwait(false);
    }
}

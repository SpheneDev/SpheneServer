using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Sphene.API.Data.Enum;
using Sphene.API.Dto.Group;
using SpheneShared.Data;

namespace SpheneServer.Services;

/// <summary>
/// Single server-side parse+resolve point for entity reference tokens inside
/// community markdown (<c>@uid</c>, <c>#gid</c>, <c>event:{id}</c>,
/// <c>post:{id}</c>, <c>blog:{uid}:{id}</c>, <c>announcement:{id}</c>).
///
/// The client NEVER decides mention semantics or reference visibility:
/// resolution happens here with the caller's rights - private, draft or
/// foreign-group targets resolve to a generic "unavailable" card so no
/// existence or content information leaks.
/// </summary>
public sealed partial class EntityReferenceService
{
    private readonly IDbContextFactory<SpheneDbContext> _dbFactory;
    private readonly ILogger<EntityReferenceService> _logger;

    public EntityReferenceService(IDbContextFactory<SpheneDbContext> dbFactory,
        ILogger<EntityReferenceService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    /// <summary>One parsed token - kind plus the identifier it carries.</summary>
    public sealed record RefToken(EntityRefKind Kind, string Token, string Identifier, string? SecondaryId);

    // @uid-or-alias · allow UID/alias charset (letters, digits, - _ .)
    [GeneratedRegex(@"(?<![\w/])@([A-Za-z0-9_.\-]{2,64})", RegexOptions.Compiled)]
    private static partial Regex UserTokenRegex();

    // #gid-or-alias
    [GeneratedRegex(@"(?<![\w/])#([A-Za-z0-9_.\-]{2,64})", RegexOptions.Compiled)]
    private static partial Regex GroupTokenRegex();

    // kind:{id} or blog:{uid}:{id} - the structured refs
    [GeneratedRegex(@"(?<![\w/])(event|post|blog|announcement):([A-Za-z0-9_\-]{1,64})(?::(\d{1,19}))?", RegexOptions.Compiled)]
    private static partial Regex StructuredTokenRegex();

    /// <summary>
    /// Scans a markdown body for reference tokens, skipping fenced code
    /// blocks and inline code spans. Returns distinct tokens, order-stable.
    /// </summary>
    public static List<RefToken> ParseTokens(string? body)
    {
        var result = new List<RefToken>();
        if (string.IsNullOrWhiteSpace(body)) return result;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var span = StripCode(body);
        void Collect(Regex re, Func<Match, RefToken?> map)
        {
            foreach (Match m in re.Matches(span))
            {
                var tok = map(m);
                if (tok != null && seen.Add(tok.Token))
                    result.Add(tok);
            }
        }

        Collect(UserTokenRegex(), m => new RefToken(EntityRefKind.User, "@" + m.Groups[1].Value, m.Groups[1].Value, null));
        Collect(GroupTokenRegex(), m => new RefToken(EntityRefKind.Syncshell, "#" + m.Groups[1].Value, m.Groups[1].Value, null));
        Collect(StructuredTokenRegex(), m =>
        {
            var kind = m.Groups[1].Value switch
            {
                "event" => EntityRefKind.Event,
                "post" => EntityRefKind.NewsPost,
                "blog" => EntityRefKind.BlogPost,
                _ => EntityRefKind.Announcement,
            };
            var ident = m.Groups[2].Value;
            // blog:{uid}:{id} carries uid in group 2 and id in group 3
            if (kind == EntityRefKind.BlogPost && !m.Groups[3].Success)
                return null; // blog ref requires both parts
            var token = kind == EntityRefKind.BlogPost
                ? $"blog:{ident}:{m.Groups[3].Value}"
                : $"{m.Groups[1].Value}:{ident}";
            return new RefToken(kind, token, ident, m.Groups[3].Success ? m.Groups[3].Value : null);
        });
        return result;
    }

    /// <summary>Distinct <c>@mention</c> identifiers inside a body.</summary>
    public static List<string> ParseMentions(string? body)
        => ParseTokens(body)
            .Where(t => t.Kind == EntityRefKind.User)
            .Select(t => t.Identifier)
            .Distinct(StringComparer.Ordinal).ToList();

    /// <summary>
    /// Masks fenced code blocks and inline code spans so tokens inside them
    /// never resolve - referencing syntax in code samples stays inert.
    /// </summary>
    private static string StripCode(string body)
    {
        var sb = new StringBuilder(body.Length);
        var i = 0;
        var inFence = false;
        while (i < body.Length)
        {
            if (body.AsSpan(i).StartsWith("```"))
            {
                inFence = !inFence;
                sb.Append("   ");
                i += 3;
                continue;
            }
            if (!inFence && body[i] == '`')
            {
                var close = body.IndexOf('`', i + 1);
                if (close > i)
                {
                    sb.Append(' ', close - i + 1);
                    i = close + 1;
                    continue;
                }
            }
            sb.Append(inFence ? ' ' : body[i]);
            i++;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Batch-resolves tokens for a caller. <paramref name="canViewGroup"/>
    /// supplies the caller's membership/staff check per GID -
    /// <paramref name="canViewBlog"/> the profile-visibility check per owner UID.
    /// Unresolvable or invisible targets return a kind-only placeholder card.
    /// </summary>
    public async Task<List<EntityCardDto>> ResolveAsync(string callerUid,
        IEnumerable<string> tokens,
        Func<string, Task<bool>> canViewGroup,
        Func<string, Task<bool>> canViewBlog)
    {
        var parsed = new List<RefToken>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in tokens)
        {
            foreach (var t in ParseTokens(raw))
            {
                if (seen.Add(t.Token)) parsed.Add(t);
            }
        }

        var cards = new List<EntityCardDto>(parsed.Count);
        await using var ctx = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);
        foreach (var t in parsed)
        {
            cards.Add(await ResolveOneAsync(ctx, callerUid, t, canViewGroup, canViewBlog).ConfigureAwait(false));
        }
        return cards;
    }

    private async Task<EntityCardDto> ResolveOneAsync(SpheneDbContext ctx, string callerUid,
        RefToken t, Func<string, Task<bool>> canViewGroup, Func<string, Task<bool>> canViewBlog)
    {
        static EntityCardDto Unavail(RefToken t) => new(t.Kind, t.Token, false, null, null, null);

        switch (t.Kind)
        {
            case EntityRefKind.User:
            {
                var u = await ctx.Users.AsNoTracking()
                    .Where(u => u.UID == t.Identifier || u.Alias == t.Identifier)
                    .Select(u => new { u.UID, u.Alias, u.DisplayName })
                    .SingleOrDefaultAsync().ConfigureAwait(false);
                if (u == null) return Unavail(t);
                var disabled = await ctx.UserProfileData.AsNoTracking()
                    .AnyAsync(p => p.UserUID == u.UID && p.ProfileDisabled).ConfigureAwait(false);
                if (disabled) return Unavail(t);
                var name = !string.IsNullOrWhiteSpace(u.DisplayName) ? u.DisplayName
                    : !string.IsNullOrWhiteSpace(u.Alias) ? u.Alias : u.UID;
                return new(t.Kind, t.Token, true, name, null, $"user:{u.UID}");
            }

            case EntityRefKind.Syncshell:
            {
                var g = await ctx.Groups.AsNoTracking()
                    .Where(g => g.GID == t.Identifier || g.Alias == t.Identifier)
                    .Select(g => new { g.GID, g.Alias })
                    .SingleOrDefaultAsync().ConfigureAwait(false);
                if (g == null) return Unavail(t);
                var listed = await ctx.SyncshellWelcomePages.AsNoTracking()
                    .Where(w => w.GroupGID == g.GID)
                    .Select(w => w.IsListedPublicly)
                    .SingleOrDefaultAsync().ConfigureAwait(false);
                if (!listed && !await canViewGroup(g.GID).ConfigureAwait(false))
                    return Unavail(t);
                return new(t.Kind, t.Token, true, g.Alias ?? g.GID, null, $"community:{g.GID}");
            }

            case EntityRefKind.Event:
            case EntityRefKind.NewsPost:
            {
                if (!long.TryParse(t.Identifier, out var id)) return Unavail(t);
                var gid = t.Kind == EntityRefKind.Event
                    ? await ctx.SyncshellCommunityEvents.AsNoTracking()
                        .Where(e => e.Id == id).Select(e => new { e.GroupGID, e.Title })
                        .SingleOrDefaultAsync().ConfigureAwait(false)
                    : await ctx.SyncshellCommunityNews.AsNoTracking()
                        .Where(n => n.Id == id && PublishingService.IsVisible(n.Status, n.ScheduledForUtc, DateTime.UtcNow))
                        .Select(n => new { n.GroupGID, n.Title })
                        .SingleOrDefaultAsync().ConfigureAwait(false);
                if (gid == null || !await canViewGroup(gid.GroupGID).ConfigureAwait(false))
                    return Unavail(t);
                var nav = t.Kind == EntityRefKind.Event ? $"event:{id}" : $"post:{id}";
                return new(t.Kind, t.Token, true, gid.Title, null, $"community:{gid.GroupGID}:{nav}");
            }

            case EntityRefKind.BlogPost:
            {
                if (!long.TryParse(t.SecondaryId, out var id)) return Unavail(t);
                var post = await ctx.UserProfileBlogPosts.AsNoTracking()
                    .Where(b => b.Id == id && b.UserUID == t.Identifier
                        && PublishingService.IsVisible(b.Status, b.ScheduledForUtc, DateTime.UtcNow))
                    .Select(b => new { b.Title, b.VisibilityScope })
                    .SingleOrDefaultAsync().ConfigureAwait(false);
                if (post == null) return Unavail(t);
                if (post.VisibilityScope != (int)BlogVisibilityScope.Public
                    && !await canViewBlog(t.Identifier).ConfigureAwait(false))
                    return Unavail(t);
                return new(t.Kind, t.Token, true, post.Title, null, $"blog:{t.Identifier}:{id}");
            }

            case EntityRefKind.Announcement:
            {
                if (!long.TryParse(t.Identifier, out var id)) return Unavail(t);
                var title = await ctx.Announcements.AsNoTracking()
                    .Where(a => a.Id == id && !a.IsDraft && a.DeletedAt == null)
                    .Select(a => a.Title).SingleOrDefaultAsync().ConfigureAwait(false);
                return title == null ? Unavail(t) : new(t.Kind, t.Token, true, title, null, $"announcement:{id}");
            }

            default:
                return Unavail(t);
        }
    }
}

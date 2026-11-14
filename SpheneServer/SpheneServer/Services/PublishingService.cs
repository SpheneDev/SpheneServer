using System.Text.Json;
using System.Text.Json.Serialization;
using Sphene.API.Data.Enum;
using Sphene.API.Dto.Group;
using SpheneShared.Models;

namespace SpheneServer.Services;

/// <summary>
/// Central publishing state machine for community content. Owns:
/// <list type="bullet">
///   <item>the <see cref="ContentPublishStatus"/> transition matrix - who may
///   move an item between which states,</item>
///   <item>per-shell <see cref="SyncshellPublishMode"/> resolution - which
///   write intents a caller may execute under the community's policy,</item>
///   <item>scheduled-publish materialization (Scheduled+due → live) and the
///   legacy IsDraft mirror.</item>
/// </list>
/// Hub methods call <see cref="ResolveWrite"/> BEFORE applying an upsert and
/// <see cref="ApplyStatus"/> to stamp the result - a DTO never carries an
/// authoritative status, so members cannot bypass the review queue by
/// crafting a status themselves.
/// </summary>
public sealed class PublishingService
{
    private readonly ILogger<PublishingService> _logger;

    public PublishingService(ILogger<PublishingService> logger)
    {
        _logger = logger;
    }

    /// <summary>Who is attempting a write - decides the allowed intents.</summary>
    public enum WriterRole
    {
        /// <summary>Not even a member of the owning group.</summary>
        Outsider = 0,
        /// <summary>Plain member (no relevant staff bit).</summary>
        Member = 1,
        /// <summary>Author or co-author of the specific item.</summary>
        Author = 2,
        /// <summary>Owner / moderator / holder of the content-kind's manage bit.</summary>
        Moderator = 3,
    }

    /// <summary>Outcome of a write-path resolution.</summary>
    public enum WriteVerdict
    {
        /// <summary>Call rejected entirely.</summary>
        Denied,
        /// <summary>Write proceeds with the requested intent.</summary>
        Allowed,
        /// <summary>Write proceeds but the status is forced (queue, draft-clamp).</summary>
        AllowedClamped,
    }

    /// <summary>Resolved target status for one upsert call.</summary>
    public sealed record WriteResolution(
        WriteVerdict Verdict,
        ContentPublishStatus EffectiveStatus,
        string? DenialReason = null);

    // ------------------------------------------------------------------
    //  Status helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// Public-read visibility: published, or scheduled-and-due. Mirrors the
    /// legacy <c>!IsDraft</c> filters while <see cref="ContentPublishStatus"/>
    /// becomes the source of truth.
    /// </summary>
    public static bool IsVisible(int status, DateTime? scheduledForUtc, DateTime utcNow)
        => status == (int)ContentPublishStatus.Published
            || (status == (int)ContentPublishStatus.Scheduled
                && scheduledForUtc.HasValue && scheduledForUtc.Value <= utcNow);

    /// <summary>Same as <see cref="IsVisible"/> for the typed enum.</summary>
    public static bool IsVisible(ContentPublishStatus status, DateTime? scheduledForUtc, DateTime utcNow)
        => IsVisible((int)status, scheduledForUtc, utcNow);

    /// <summary>
    /// Materializes a due scheduled publish into Published - call before
    /// reading status-sensitive surfaces or writing the row.
    /// </summary>
    /// <returns>True when the status flipped.</returns>
    public static bool MaterializeSchedule(ref int status, DateTime? scheduledForUtc, DateTime utcNow)
    {
        if (status == (int)ContentPublishStatus.Scheduled
            && scheduledForUtc.HasValue && scheduledForUtc.Value <= utcNow)
        {
            status = (int)ContentPublishStatus.Published;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Stamps status + the legacy IsDraft mirror + PublishedAt on a news post.
    /// PublishedAt only moves when the post becomes (or stays) visible -
    /// scheduling stamps the scheduled instant so sort order stays sane.
    /// </summary>
    public void ApplyNewsStatus(SyncshellCommunityNewsPost post, ContentPublishStatus status, DateTime utcNow)
    {
        var wasVisible = IsVisible(post.Status, post.ScheduledForUtc, utcNow);
        post.Status = (int)status;
        post.IsDraft = status == ContentPublishStatus.Draft;
        var nowVisible = IsVisible(post.Status, post.ScheduledForUtc, utcNow);
        if (!wasVisible && nowVisible)
        {
            post.PublishedAt = status == ContentPublishStatus.Scheduled && post.ScheduledForUtc.HasValue
                ? post.ScheduledForUtc.Value
                : utcNow;
        }
        if (status != ContentPublishStatus.Scheduled)
        {
            post.ScheduledForUtc = null;
        }
    }

    /// <summary>
    /// Legal author-driven transitions (the writer, not a moderator).
    /// Authors may save drafts, submit/retract for review, and revive rejects.
    /// </summary>
    public static bool AuthorCanTransition(ContentPublishStatus from, ContentPublishStatus to) => to switch
    {
        ContentPublishStatus.Draft =>
            from is ContentPublishStatus.Draft or ContentPublishStatus.PendingApproval
                or ContentPublishStatus.Rejected or ContentPublishStatus.Archived,
        ContentPublishStatus.PendingApproval =>
            from is ContentPublishStatus.Draft or ContentPublishStatus.PendingApproval,
        _ => false, // authors never publish/schedule/archive directly through this path
    };

    /// <summary>
    /// Legal moderator-driven transitions - superset of the author's.
    /// </summary>
    public static bool ModeratorCanTransition(ContentPublishStatus from, ContentPublishStatus to) => to switch
    {
        ContentPublishStatus.Draft => from is not ContentPublishStatus.Published,
        ContentPublishStatus.PendingApproval =>
            from is ContentPublishStatus.Draft or ContentPublishStatus.PendingApproval,
        ContentPublishStatus.Published =>
            from is ContentPublishStatus.Draft or ContentPublishStatus.PendingApproval
                or ContentPublishStatus.Scheduled or ContentPublishStatus.Archived,
        ContentPublishStatus.Scheduled =>
            from is ContentPublishStatus.Draft or ContentPublishStatus.PendingApproval
                or ContentPublishStatus.Scheduled,
        ContentPublishStatus.Rejected => from == ContentPublishStatus.PendingApproval,
        ContentPublishStatus.Archived => true,
        _ => false,
    };

    // ------------------------------------------------------------------
    //  Policy loading
    // ------------------------------------------------------------------

    /// <summary>Deserialize the welcome-page policy; unknown/broken JSON → safe defaults.</summary>
    public static SyncshellPublishingPolicy ParsePolicy(string? json)
    {
        var fallback = new SyncshellPublishingPolicy();
        if (string.IsNullOrWhiteSpace(json)) return fallback;
        try
        {
            var parsed = JsonSerializer.Deserialize<SyncshellPublishingPolicy>(json);
            return parsed is { Version: >= 1 } ? parsed : fallback;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }

    /// <summary>Serialize a policy DTO into the storage form (strips the group ref).</summary>
    public static string SerializePolicy(SyncshellPublishingPolicyDto dto) => JsonSerializer.Serialize(
        new SyncshellPublishingPolicy
        {
            Version = Math.Max(1, dto.Version),
            NewsMode = dto.NewsMode,
            GalleryMode = dto.GalleryMode,
            EventsMode = dto.EventsMode,
            CommentsMode = dto.CommentsMode,
            DirectPublishBits = dto.DirectPublishBits,
        });

    /// <summary>The publish mode for a content kind under a policy.</summary>
    public static SyncshellPublishMode ModeFor(SyncshellPublishingPolicy policy, string contentKind)
        => contentKind switch
        {
            "news" => policy.NewsMode,
            "gallery" => policy.GalleryMode,
            "events" => policy.EventsMode,
            _ => SyncshellPublishMode.ApprovalRequired,
        };

    // ------------------------------------------------------------------
    //  Write-path resolution
    // ------------------------------------------------------------------

    /// <summary>
    /// Resolves a member/community write under the shell policy.
    /// <paramref name="requestedIntent"/> is the client-requested
    /// <see cref="ContentPublishStatus"/> (Published by default).
    /// </summary>
    public WriteResolution ResolveWrite(
        SyncshellPublishingPolicy policy, string contentKind,
        WriterRole callerRole, long callerStaffBits,
        ContentPublishStatus requestedIntent,
        ContentPublishStatus? currentStatus)
    {
        if (callerRole == WriterRole.Outsider)
        {
            return new(WriteVerdict.Denied, ContentPublishStatus.Draft, "not-a-member");
        }

        // Moderators write unconstrained - full transition matrix applies.
        if (callerRole == WriterRole.Moderator)
        {
            var intent = currentStatus.HasValue
                ? (ModeratorCanTransition(currentStatus.Value, requestedIntent) ? requestedIntent
                    : currentStatus.Value == requestedIntent ? requestedIntent : ContentPublishStatus.Draft)
                : requestedIntent;
            return new(WriteVerdict.Allowed, intent);
        }

        var mode = ModeFor(policy, contentKind);
        if (mode == SyncshellPublishMode.StaffOnly)
        {
            return new(WriteVerdict.Denied, ContentPublishStatus.Draft, "staff-only");
        }

        // Review bypass: the shell may grant certain staff bits direct publish
        // even inside an approval-required mode.
        var bypasses = (callerStaffBits & policy.DirectPublishBits) != 0;
        if (mode == SyncshellPublishMode.Direct || bypasses)
        {
            return new(WriteVerdict.Allowed, requestedIntent);
        }

        // ApprovalRequired: the writer can only ever produce Draft or
        // PendingApproval - a publish/schedule intent is clamped into the queue.
        var clamped = requestedIntent is ContentPublishStatus.Draft
            ? ContentPublishStatus.Draft
            : ContentPublishStatus.PendingApproval;
        return new(WriteVerdict.AllowedClamped, clamped);
    }

    /// <summary>
    /// For plain blog posts there is no community policy - only author rights.
    /// Co-authors share the author's write privileges; delete stays owner-only.
    /// </summary>
    public WriteResolution ResolveBlogWrite(bool callerIsOwner, bool callerIsCoAuthor,
        ContentPublishStatus requestedIntent, ContentPublishStatus? currentStatus)
    {
        if (!callerIsOwner && !callerIsCoAuthor)
        {
            return new(WriteVerdict.Denied, ContentPublishStatus.Draft, "not-an-author");
        }

        // Authors (and co-authors) self-publish blogs - no moderation layer.
        var intent = requestedIntent;
        if (intent == ContentPublishStatus.PendingApproval || intent == ContentPublishStatus.Rejected)
        {
            intent = ContentPublishStatus.Draft; // meaningless states on self-published content
        }
        if (currentStatus.HasValue
            && currentStatus.Value == ContentPublishStatus.Archived
            && intent == ContentPublishStatus.Published)
        {
            intent = ContentPublishStatus.Published; // un-archiving is allowed for the owner
        }
        return new(WriteVerdict.Allowed, intent);
    }

    /// <summary>True when the caller may edit this item's body fields at all.</summary>
    public static bool IsAuthorOf(string callerUid, string? authorUid, IReadOnlyCollection<string>? coAuthorUids)
        => string.Equals(callerUid, authorUid, StringComparison.Ordinal)
            || (coAuthorUids != null && coAuthorUids.Contains(callerUid, StringComparer.Ordinal));

    /// <summary>Parse co-author JSON; malformed data degrades to an empty set.</summary>
    public static List<string> ParseCoAuthors(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

/// <summary>Storage form of the per-shell publishing policy (welcome-page JSON).</summary>
public sealed class SyncshellPublishingPolicy
{
    [JsonPropertyName("v")]
    public int Version { get; set; } = 1;
    [JsonPropertyName("news")]
    public SyncshellPublishMode NewsMode { get; set; } = SyncshellPublishMode.ApprovalRequired;
    [JsonPropertyName("gallery")]
    public SyncshellPublishMode GalleryMode { get; set; } = SyncshellPublishMode.ApprovalRequired;
    /// <summary>Member-created events default to StaffOnly - preserving the
    /// pre-Phase-3 staff-managed event surface unless a shell opts in.</summary>
    [JsonPropertyName("events")]
    public SyncshellPublishMode EventsMode { get; set; } = SyncshellPublishMode.StaffOnly;
    [JsonPropertyName("directBits")]
    public long DirectPublishBits { get; set; }
    /// <summary>Comment write policy - defaults to Open; unknown JSON payloads
    /// (pre-Phase-4 rows) deserialize to the default automatically.</summary>
    [JsonPropertyName("comments")]
    public CommunityCommentsMode CommentsMode { get; set; } = CommunityCommentsMode.Open;
}

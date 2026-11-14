using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Sphene.API.Data.Enum;
using Sphene.API.Dto.User;
using Sphene.API.SignalR;
using SpheneServer.Hubs;
using SpheneServer.Utils;
using SpheneShared.Data;
using SpheneShared.Models;

namespace SpheneServer.Services;

/// <summary>
/// Single write path for durable user notifications - same role for
/// <see cref="UserNotification"/> that <see cref="AnnouncementWriter"/> plays
/// for announcements. Producers (membership, comments, mentions, events,
/// reactions) never insert rows or push clients directly: they call
/// <see cref="PushAsync"/>/<see cref="PushManyAsync"/> so validation, mute
/// checks, dedupe and delivery stay in one place.
/// </summary>
public class NotificationWriter
{
    private const int TitleMaxLength = 200;
    private const int SnippetMaxLength = 500;

    private readonly IDbContextFactory<SpheneDbContext> _dbContextFactory;
    private readonly IHubContext<SpheneHub, ISpheneHub> _hubContext;
    private readonly ILogger<NotificationWriter> _logger;

    public NotificationWriter(IDbContextFactory<SpheneDbContext> dbContextFactory,
        IHubContext<SpheneHub, ISpheneHub> hubContext, ILogger<NotificationWriter> logger)
    {
        _dbContextFactory = dbContextFactory;
        _hubContext = hubContext;
        _logger = logger;
    }

    /// <summary>
    /// Persists a notification for one recipient and pushes it to their
    /// connection. Skipped (returns null) when the recipient is unknown, has
    /// muted <paramref name="kind"/>, or an unread row with the same
    /// <paramref name="dedupeKey"/> already exists.
    /// </summary>
    public async Task<UserNotification?> PushAsync(SpheneDbContext dbContext, string recipientUid,
        UserNotificationKind kind, string title, string? snippet = null, string? actorUid = null,
        string? groupGid = null, string? contentKind = null, string? contentId = null,
        string? dedupeKey = null)
    {
        var recipient = await dbContext.Users
            .SingleOrDefaultAsync(u => u.UID == recipientUid).ConfigureAwait(false);
        if (recipient == null)
        {
            _logger.LogDebug("Notification skipped · unknown recipient {uid}", recipientUid);
            return null;
        }
        if ((recipient.NotificationDisabledKinds & (1L << (int)kind)) != 0) return null;

        if (dedupeKey != null && await dbContext.UserNotifications.AnyAsync(n =>
                n.RecipientUID == recipientUid && n.DedupeKey == dedupeKey).ConfigureAwait(false))
        {
            return null;
        }

        var actor = actorUid == null ? null : await dbContext.Users
            .SingleOrDefaultAsync(u => u.UID == actorUid).ConfigureAwait(false);

        var notification = new UserNotification
        {
            RecipientUID = recipientUid,
            Kind = kind,
            ActorUID = actorUid,
            GroupGID = groupGid,
            ContentKind = contentKind,
            ContentId = contentId,
            Title = title.Length > TitleMaxLength ? title[..TitleMaxLength] : title,
            Snippet = snippet?.Length > SnippetMaxLength ? snippet[..SnippetMaxLength] : snippet,
            DedupeKey = dedupeKey,
            CreatedAt = DateTime.UtcNow
        };
        dbContext.UserNotifications.Add(notification);
        await dbContext.SaveChangesAsync().ConfigureAwait(false);

        try
        {
            await _hubContext.Clients.User(recipientUid)
                .Client_UserNotification(ToDto(notification, actor)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Notification push failed for {uid} · row {id} is persisted",
                recipientUid, notification.Id);
        }
        return notification;
    }

    /// <summary>
    /// Fan-out variant for staff/member notifications. One context, one save -
    /// each recipient still gets their own mute/dedupe check and push.
    /// </summary>
    public async Task PushManyAsync(IEnumerable<string> recipientUids, UserNotificationKind kind,
        string title, string? snippet = null, string? actorUid = null, string? groupGid = null,
        string? contentKind = null, string? contentId = null,
        Func<string, string?>? dedupeKeyFor = null)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);
        foreach (var uid in recipientUids.Distinct(StringComparer.Ordinal))
        {
            await PushAsync(dbContext, uid, kind, title, snippet, actorUid, groupGid,
                contentKind, contentId, dedupeKeyFor?.Invoke(uid)).ConfigureAwait(false);
        }
    }

    public static UserNotificationDto ToDto(UserNotification n, User? actor) => new(
        n.Id, n.Kind, actor?.ToUserData(), n.GroupGID, n.ContentKind, n.ContentId,
        n.Title, n.Snippet, n.IsRead, n.CreatedAt);
}

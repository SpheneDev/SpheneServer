using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Sphene.API.Dto.User;
using SpheneServer.Services;
using SpheneServer.Utils;
using SpheneShared.Models;

namespace SpheneServer.Hubs;

public partial class SpheneHub
{
    /// <summary>
    /// Newest-first durable notifications for the caller · reconnect sync for the
    /// inbox. Take is clamped to a sane page size.
    /// </summary>
    [Authorize(Policy = "Identified")]
    public async Task<List<UserNotificationDto>> UserGetNotifications(int take = 50)
    {
        take = Math.Clamp(take, 1, 100);

        var rows = await DbContext.UserNotifications.AsNoTracking()
            .Where(n => n.RecipientUID == UserUID)
            .OrderByDescending(n => n.CreatedAt)
            .Take(take)
            .ToListAsync().ConfigureAwait(false);

        var actorUids = rows.Where(r => r.ActorUID != null)
            .Select(r => r.ActorUID!).Distinct(StringComparer.Ordinal).ToList();
        var actors = actorUids.Count == 0
            ? new Dictionary<string, User>(StringComparer.Ordinal)
            : await DbContext.Users.AsNoTracking()
                .Where(u => actorUids.Contains(u.UID))
                .ToDictionaryAsync(u => u.UID, StringComparer.Ordinal).ConfigureAwait(false);

        var result = new List<UserNotificationDto>(rows.Count);
        foreach (var row in rows)
        {
            actors.TryGetValue(row.ActorUID ?? string.Empty, out var actor);
            result.Add(NotificationWriter.ToDto(row, actor));
        }
        return result;
    }

    /// <summary>Marks the caller's own notifications read · foreign ids are ignored.</summary>
    [Authorize(Policy = "Identified")]
    public async Task UserMarkNotificationsRead(List<long> ids)
    {
        if (ids.Count == 0) return;
        var clamped = ids.Count > 200 ? ids.Take(200).ToList() : ids;

        var rows = await DbContext.UserNotifications
            .Where(n => n.RecipientUID == UserUID && clamped.Contains(n.Id) && !n.IsRead)
            .ToListAsync().ConfigureAwait(false);
        if (rows.Count == 0) return;

        foreach (var row in rows) row.IsRead = true;
        await DbContext.SaveChangesAsync().ConfigureAwait(false);
    }

    /// <summary>Deletes all of the caller's notifications.</summary>
    [Authorize(Policy = "Identified")]
    public async Task UserClearNotifications()
    {
        var rows = await DbContext.UserNotifications
            .Where(n => n.RecipientUID == UserUID)
            .ToListAsync().ConfigureAwait(false);
        if (rows.Count == 0) return;

        DbContext.UserNotifications.RemoveRange(rows);
        await DbContext.SaveChangesAsync().ConfigureAwait(false);
    }
}

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Sphene.API.Data.Enum;
using SpheneShared.Data;
using SpheneShared.Models;

namespace SpheneServer.Services;

/// <summary>
/// Durable event reminder sweep - polls upcoming published events with a
/// <see cref="SyncshellCommunityEvent.RemindersJson"/> offset list and pushes
/// one <see cref="UserNotificationKind.EventReminder"/> per
/// (event, occurrence, offset, attendee) through
/// <see cref="NotificationWriter"/>.
///
/// Restart-safety is carried entirely by the persisted dedupe key
/// (<c>event-rem:{id}:{occurrenceTicks}:{offset}:{uid}</c>): the same due
/// reminder can be scanned a hundred times and notifies once, so no separate
/// dispatch table or in-memory state is needed. A sweep also flips
/// <see cref="EventLifecycleStatus.Scheduled"/> events whose end passed to
/// <see cref="EventLifecycleStatus.Completed"/>.
/// </summary>
public sealed class EventReminderHostedService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ErrorBackoff = TimeSpan.FromMinutes(5);

    /// <summary>How far ahead the sweep looks for the next occurrence -
    /// matches the server-side reminder cap (4 weeks).</summary>
    private static readonly TimeSpan ScanHorizon = TimeSpan.FromDays(30);

    private readonly IDbContextFactory<SpheneDbContext> _dbFactory;
    private readonly NotificationWriter _notificationWriter;
    private readonly ILogger<EventReminderHostedService> _logger;

    public EventReminderHostedService(IDbContextFactory<SpheneDbContext> dbFactory,
        NotificationWriter notificationWriter,
        ILogger<EventReminderHostedService> logger)
    {
        _dbFactory = dbFactory;
        _notificationWriter = notificationWriter;
        _logger = logger;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Event reminder sweep failed - retrying after backoff");
                await Task.Delay(ErrorBackoff, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>One pass over reminder-bearing events; public for tests.</summary>
    public async Task<int> SweepAsync(CancellationToken token = default)
    {
        var now = DateTime.UtcNow;
        var horizon = now + ScanHorizon;
        await using var ctx = await _dbFactory.CreateDbContextAsync(token).ConfigureAwait(false);

        var candidates = await ctx.SyncshellCommunityEvents
            .Where(e => e.RemindersJson != null
                && e.PublishStatus == (int)ContentPublishStatus.Published
                && e.EventStatus == (int)EventLifecycleStatus.Scheduled
                && (e.StartUtc <= horizon || e.RecurrenceRule != null))
            .ToListAsync(token).ConfigureAwait(false);

        var delivered = 0;
        foreach (var evt in candidates)
        {
            delivered += await DeliverDueRemindersAsync(ctx, evt, now, token).ConfigureAwait(false);
        }

        delivered += await CompleteElapsedEventsAsync(ctx, now, token).ConfigureAwait(false);
        return delivered;
    }

    /// <summary>Fires every due offset of one event's next occurrence;
    /// reminders whose occurrence already started are skipped (a reminder
    /// after the fact is noise, not signal).</summary>
    private async Task<int> DeliverDueRemindersAsync(SpheneDbContext ctx,
        SyncshellCommunityEvent evt, DateTime now, CancellationToken token)
    {
        var offsets = ParseReminders(evt.RemindersJson);
        if (offsets.Count == 0) return 0;

        var next = EventRecurrenceService.NextOccurrence(evt, now);
        if (next == null || next.OccurrenceStartUtc <= now) return 0;

        // Occurrence-level cancellations suppress reminders - NextOccurrence
        // expands the raw rule and does not consult exception rows.
        var cancelled = await ctx.CommunityEventExceptions.AsNoTracking()
            .AnyAsync(x => x.EventId == evt.Id
                && x.OccurrenceStartUtc == next.OccurrenceStartUtc
                && x.Cancelled, token).ConfigureAwait(false);
        if (cancelled) return 0;

        var dueOffsets = offsets
            .Where(m => next.OccurrenceStartUtc - TimeSpan.FromMinutes(m) <= now)
            .ToList();
        if (dueOffsets.Count == 0) return 0;

        var recipients = await ctx.CommunityEventParticipants.AsNoTracking()
            .Where(p => p.EventId == evt.Id
                && (p.Status == (int)EventRsvpStatus.Attending
                    || p.Status == (int)EventRsvpStatus.Interested))
            .Select(p => p.UserUID)
            .ToListAsync(token).ConfigureAwait(false);
        if (recipients.Count == 0) return 0;

        var occTicks = next.OccurrenceStartUtc.Ticks;
        foreach (var offset in dueOffsets)
        {
            // Dedupe/mute filtering lives inside PushAsync - the returned
            // count is "attempted", sweep bookkeeping only.
            await _notificationWriter.PushManyAsync(recipients,
                UserNotificationKind.EventReminder,
                $"Event reminder · {evt.Title}",
                FormatReminderSnippet(evt, next.OccurrenceStartUtc, offset),
                null, evt.GroupGID, "event", evt.Id.ToString(),
                uid => $"event-rem:{evt.Id}:{occTicks}:{offset}:{uid}").ConfigureAwait(false);
        }
        return dueOffsets.Count * recipients.Count;
    }

    /// <summary>Scheduled events whose end (or start for point events)
    /// passed are marked Completed - keeps the calendar honest without any
    /// staff action.</summary>
    private async Task<int> CompleteElapsedEventsAsync(SpheneDbContext ctx,
        DateTime now, CancellationToken token)
    {
        var elapsed = await ctx.SyncshellCommunityEvents
            .Where(e => e.EventStatus == (int)EventLifecycleStatus.Scheduled
                && e.RecurrenceRule == null
                && (e.EndUtc ?? e.StartUtc) < now)
            .ToListAsync(token).ConfigureAwait(false);
        foreach (var e in elapsed)
        {
            e.EventStatus = (int)EventLifecycleStatus.Completed;
            e.UpdatedAt = now;
        }
        if (elapsed.Count > 0)
        {
            await ctx.SaveChangesAsync(token).ConfigureAwait(false);
        }
        return elapsed.Count;
    }

    private static List<int> ParseReminders(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<int>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string FormatReminderSnippet(SyncshellCommunityEvent evt,
        DateTime occStartUtc, int offsetMinutes) => offsetMinutes switch
    {
        < 120 => $"Starts in {offsetMinutes} min · {evt.Location ?? evt.Title}",
        < 2880 => $"Starts in {offsetMinutes / 60} h · {evt.Location ?? evt.Title}",
        _ => $"Starts {occStartUtc:yyyy-MM-dd HH:mm} UTC · {evt.Location ?? evt.Title}",
    };
}

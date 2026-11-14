using Microsoft.EntityFrameworkCore;
using Sphene.API.Data.Enum;
using SpheneShared.Data;
using SpheneShared.Models;

namespace SpheneServer.Services;

/// <summary>
/// Transactional RSVP/capacity state machine for
/// <see cref="SyncshellCommunityEvent"/> - the single write path for
/// participant rows and the denormalized seat counters.
///
/// Concurrency: on relational providers the event row is locked
/// (<c>FOR UPDATE</c>) inside a transaction, so two parallel
/// <see cref="ApplyRsvpAsync"/> calls for the last free seat serialize -
/// the loser deterministically lands on the waitlist instead of
/// overbooking. Non-relational test providers skip the lock (callers run
/// sequentially there anyway); the state machine itself is identical.
///
/// Waitlist ordering uses monotonic <see cref="SyncshellCommunityEventParticipant.WaitlistPosition"/>
/// tickets - never renumbered - so promotion is stable FIFO.
/// </summary>
public static class CommunityEventService
{
    /// <summary>Outcome of one RSVP write - the hub maps it onto the
    /// participants DTO and fires the right notifications.</summary>
    public sealed record RsvpOutcome(
        bool Success,
        string? Error = null,
        EventRsvpStatus? EffectiveStatus = null,
        int? WaitlistPosition = null,
        bool Changed = false,
        List<string>? PromotedUids = null);

    /// <summary>
    /// Applies one RSVP transition for <paramref name="uid"/> on
    /// <paramref name="eventId"/>. <paramref name="requested"/> ==
    /// <see cref="EventRsvpStatus.Waitlist"/> is coerced to
    /// <see cref="EventRsvpStatus.Attending"/> (waitlist is server-assigned);
    /// <paramref name="remove"/> withdraws the row entirely. Re-requesting
    /// Attending while already attending or waitlisted is a no-op apart from
    /// the note - the call is idempotent by design.
    /// </summary>
    public static async Task<RsvpOutcome> ApplyRsvpAsync(
        SpheneDbContext db, long eventId, string uid,
        EventRsvpStatus requested, string? note, bool remove, DateTime utcNow)
    {
        if (db.Database.IsRelational())
        {
            await using var tx = await db.Database.BeginTransactionAsync().ConfigureAwait(false);
            // Serialize all RSVP writers for this event on its row lock.
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM syncshell_community_events WHERE id = {eventId} FOR UPDATE")
                .ConfigureAwait(false);
            var outcome = await ApplyLockedAsync(db, eventId, uid, requested, note,
                remove, utcNow).ConfigureAwait(false);
            if (outcome.Success)
            {
                await tx.CommitAsync().ConfigureAwait(false);
            }
            return outcome;
        }
        return await ApplyLockedAsync(db, eventId, uid, requested, note,
            remove, utcNow).ConfigureAwait(false);
    }

    /// <summary>
    /// Fills free seats from the waitlist head - called after a seat frees
    /// and by the upsert path when staff raises
    /// <see cref="SyncshellCommunityEvent.MaxAttendees"/>.
    /// </summary>
    /// <returns>Promoted UIDs in promotion order.</returns>
    public static List<string> FillOpenSeats(
        IReadOnlyCollection<SyncshellCommunityEventParticipant> participants,
        int? maxAttendees, DateTime utcNow)
    {
        var promoted = new List<string>();
        // Unbounded capacity → nobody has a reason to wait; promote everyone.
        while (maxAttendees == null || Attending(participants) < maxAttendees.Value)
        {
            var head = participants
                .Where(p => p.Status == (int)EventRsvpStatus.Waitlist)
                .OrderBy(p => p.WaitlistPosition ?? int.MaxValue)
                .FirstOrDefault();
            if (head == null) break;
            head.Status = (int)EventRsvpStatus.Attending;
            head.WaitlistPosition = null;
            head.PromotedAt = utcNow;
            head.UpdatedAt = utcNow;
            promoted.Add(head.UserUID);
        }
        return promoted;
    }

    /// <summary>Recounts the denormalized counters from live participant rows
    /// and bumps the optimistic-concurrency token.</summary>
    public static void SyncCounters(SpheneDbContext db, SyncshellCommunityEvent evt,
        IEnumerable<SyncshellCommunityEventParticipant> participants)
    {
        var attending = 0;
        var waitlisted = 0;
        foreach (var p in participants)
        {
            if (db.Entry(p).State == EntityState.Deleted) continue;
            if (p.Status == (int)EventRsvpStatus.Attending) attending++;
            else if (p.Status == (int)EventRsvpStatus.Waitlist) waitlisted++;
        }
        evt.AttendingCount = attending;
        evt.WaitlistCount = waitlisted;
        evt.RsvpVersion++;
        evt.UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>Tickets are monotonic - the highest position ever handed out
    /// plus one; seats are never reused while their holder still waits.</summary>
    public static int NextWaitlistTicket(
        IEnumerable<SyncshellCommunityEventParticipant> participants)
        => participants.Max(p => (int?)p.WaitlistPosition ?? 0) + 1;

    /// <summary>Read-side helper - counts Interested participants (the third
    /// counter the summary DTO exposes but the row does not denormalize).</summary>
    public static int InterestedCount(
        IEnumerable<SyncshellCommunityEventParticipant> participants)
        => participants.Count(p => p.Status == (int)EventRsvpStatus.Interested);

    private static int Attending(IEnumerable<SyncshellCommunityEventParticipant> participants)
        => participants.Count(p => p.Status == (int)EventRsvpStatus.Attending);

    private static async Task<RsvpOutcome> ApplyLockedAsync(
        SpheneDbContext db, long eventId, string uid,
        EventRsvpStatus requested, string? note, bool remove, DateTime utcNow)
    {
        var evt = await db.SyncshellCommunityEvents
            .SingleOrDefaultAsync(e => e.Id == eventId).ConfigureAwait(false);
        if (evt == null) return new RsvpOutcome(false, "event-not-found");

        // Direct query - never the nav collection. Include on an
        // already-tracked entity can append duplicate rows on some
        // providers; this list is the single source of truth in here.
        var participants = await db.CommunityEventParticipants
            .Where(p => p.EventId == eventId).ToListAsync().ConfigureAwait(false);
        var row = participants.SingleOrDefault(p => p.UserUID == uid);
        var promoted = new List<string>();

        if (remove)
        {
            if (row == null)
            {
                return new RsvpOutcome(true, EffectiveStatus: null, Changed: false);
            }
            var freedSeat = row.Status == (int)EventRsvpStatus.Attending;
            db.CommunityEventParticipants.Remove(row);
            participants.Remove(row);
            if (freedSeat)
            {
                promoted.AddRange(FillOpenSeats(participants, evt.MaxAttendees, utcNow));
            }
            SyncCounters(db, evt, participants);
            await db.SaveChangesAsync().ConfigureAwait(false);
            return new RsvpOutcome(true, EffectiveStatus: null,
                Changed: true, PromotedUids: promoted);
        }

        if (requested == EventRsvpStatus.Waitlist)
        {
            requested = EventRsvpStatus.Attending; // waitlist is server-assigned
        }

        if (row == null)
        {
            row = new SyncshellCommunityEventParticipant
            {
                EventId = evt.Id,
                UserUID = uid,
                Status = (int)EventRsvpStatus.NotAttending,
                CreatedAt = utcNow
            };
            await db.CommunityEventParticipants.AddAsync(row).ConfigureAwait(false);
            participants.Add(row);
        }

        var previous = (EventRsvpStatus)row.Status;
        switch (requested)
        {
            case EventRsvpStatus.Attending:
                if (previous is EventRsvpStatus.Attending or EventRsvpStatus.Waitlist)
                {
                    break; // idempotent - already seated or already queued
                }
                if (evt.MaxAttendees == null || Attending(participants) < evt.MaxAttendees.Value)
                {
                    row.Status = (int)EventRsvpStatus.Attending;
                    row.WaitlistPosition = null;
                }
                else
                {
                    row.Status = (int)EventRsvpStatus.Waitlist;
                    row.WaitlistPosition = NextWaitlistTicket(participants);
                }
                break;

            case EventRsvpStatus.Interested:
            case EventRsvpStatus.NotAttending:
                if (previous == EventRsvpStatus.Waitlist)
                {
                    row.WaitlistPosition = null; // free the ticket; others keep theirs
                }
                var freedAttendingSeat = previous == EventRsvpStatus.Attending;
                row.Status = (int)requested;
                if (freedAttendingSeat)
                {
                    promoted.AddRange(FillOpenSeats(participants, evt.MaxAttendees, utcNow));
                }
                break;

            default:
                return new RsvpOutcome(false, "unknown-status");
        }

        row.Note = note;
        row.UpdatedAt = utcNow;
        SyncCounters(db, evt, participants);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new RsvpOutcome(true,
            EffectiveStatus: (EventRsvpStatus)row.Status,
            WaitlistPosition: row.WaitlistPosition,
            Changed: previous != (EventRsvpStatus)row.Status,
            PromotedUids: promoted);
    }
}

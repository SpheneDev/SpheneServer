using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Sphene.API.Data;
using Sphene.API.Data.Enum;
using Sphene.API.Dto.Group;
using SpheneServer.Services;
using SpheneServer.Utils;
using SpheneShared.Models;
using System.Text.Json;

namespace SpheneServer.Hubs;

/// <summary>
/// Phase-3 community events - event CRUD (extended for capacity, visibility,
/// lifecycle, timezone, recurrence, reminders, organizers), the RSVP surface,
/// participant administration, the expanded calendar read and per-occurrence
/// exceptions. RSVP state changes go exclusively through
/// <see cref="CommunityEventService"/> (row-locked on relational providers);
/// every fan-out uses <see cref="NotificationWriter"/> and
/// <c>Client_CommunityChanged</c>.
/// </summary>
public partial class SpheneHub
{
    private const int MaxEventReminders = 8;
    private const int MaxReminderMinutes = 40_320; // 4 weeks
    private const int MaxCalendarWindowDays = 370;
    private const int MaxCalendarOccurrences = 400;

    /// <summary>
    /// Staff/organizer event upsert. Staff (<see cref="SyncshellStaffPermission.ManageEvents"/>)
    /// own governance fields - capacity, visibility, organizer list, publish
    /// status; the author + organizers can edit content fields and lifecycle
    /// but never moderation state. <see cref="SyncshellEventUpdateDto.OccurrenceStartUtc"/>
    /// routes the call to a single-occurrence exception edit instead.
    /// </summary>
    [Authorize(Policy = "Identified")]
    public async Task<SyncshellEventDto?> GroupUpsertCommunityEvent(SyncshellEventUpdateDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));

        var isStaff = await HasCommunityRights(dto.Group.GID,
            SyncshellStaffPermission.ManageEvents).ConfigureAwait(false);

        var entity = dto.Id == 0
            ? null
            : await DbContext.SyncshellCommunityEvents
                .SingleOrDefaultAsync(e => e.Id == dto.Id && e.GroupGID == dto.Group.GID)
                .ConfigureAwait(false);

        if (entity == null)
        {
            if (!isStaff) return null;
            entity = new SyncshellCommunityEvent
            {
                GroupGID = dto.Group.GID,
                AuthorUid = UserUID,
                PublishStatus = (int)ContentPublishStatus.Draft,
                CreatedAt = DateTime.UtcNow
            };
            await DbContext.SyncshellCommunityEvents.AddAsync(entity).ConfigureAwait(false);
        }
        else if (!isStaff && !PublishingService.IsAuthorOf(UserUID, entity.AuthorUid,
            PublishingService.ParseCoAuthors(entity.OrganizerUidsJson)))
        {
            return null;
        }

        // Single-occurrence edit of a recurring series - writes an exception
        // row instead of touching the series definition.
        if (dto.OccurrenceStartUtc.HasValue)
        {
            var ok = await UpsertEventExceptionCoreAsync(entity, dto.OccurrenceStartUtc.Value,
                dto.Title, dto.StartUtc, dto.EndUtc, dto.Location, dto.Description,
                cancelled: false, remove: false, destination: dto.Destination).ConfigureAwait(false);
            return ok ? MapEvent(entity, await ViewerRsvpAsync(entity.Id).ConfigureAwait(false),
                await OrganizerUserMapAsync(entity).ConfigureAwait(false)) : null;
        }

        var nowUtc = DateTime.UtcNow;
        var prevLifecycle = (EventLifecycleStatus)entity.EventStatus;
        var prevPublish = (ContentPublishStatus)entity.PublishStatus;
        var destinationJson = SerializeDestinationJson(dto.Destination);
        var materialChange = entity.Title != dto.Title || entity.Location != dto.Location
            || entity.StartUtc != dto.StartUtc || entity.EndUtc != dto.EndUtc
            || entity.DestinationJson != destinationJson;

        entity.Title = dto.Title;
        entity.Description = dto.Description;
        entity.StartUtc = dto.StartUtc;
        entity.EndUtc = dto.EndUtc;
        entity.Location = dto.Location;
        entity.Url = dto.Url;
        entity.DestinationJson = destinationJson;
        entity.TimezoneId = SanitizeTimezoneId(dto.TimezoneId);
        entity.RecurrenceRule = EventRecurrenceService.ParseRule(dto.RecurrenceRule) == null
            ? null : dto.RecurrenceRule;
        entity.RemindersJson = SerializeJson(SanitizeReminders(dto.Reminders));
        entity.UpdatedAt = nowUtc;

        if (dto.ImageMediaHash != null)
        {
            entity.ImageMediaHash = string.IsNullOrEmpty(dto.ImageMediaHash)
                ? null : dto.ImageMediaHash;
            entity.ImageBase64 = null;
            if (entity.ImageMediaHash != null)
            {
                await _communityMedia.RecordMediaAsync(DbContext, dto.ImageMediaHash,
                    UserUID, null, null).ConfigureAwait(false);
            }
        }
        else
        {
            entity.ImageBase64 = dto.ImageBase64;
        }
        if (dto.PreviewMediaHash != null)
        {
            entity.PreviewMediaHash = string.IsNullOrEmpty(dto.PreviewMediaHash)
                ? null : dto.PreviewMediaHash;
            if (entity.PreviewMediaHash != null)
            {
                await _communityMedia.RecordMediaAsync(DbContext, dto.PreviewMediaHash,
                    UserUID, null, null).ConfigureAwait(false);
            }
        }

        if (Enum.IsDefined((EventLifecycleStatus)dto.EventStatus))
        {
            entity.EventStatus = dto.EventStatus;
        }

        var capacityRaised = false;
        if (isStaff)
        {
            capacityRaised = dto.MaxAttendees > entity.MaxAttendees
                || (entity.MaxAttendees.HasValue && dto.MaxAttendees == null);
            entity.MaxAttendees = dto.MaxAttendees is null or >= 1 ? dto.MaxAttendees : entity.MaxAttendees;
            if (Enum.IsDefined((EventVisibility)dto.Visibility))
            {
                entity.Visibility = dto.Visibility;
            }
            if (dto.OrganizerUids != null)
            {
                var organizers = dto.OrganizerUids
                    .Where(u => !string.IsNullOrWhiteSpace(u))
                    .Distinct(StringComparer.Ordinal).ToList();
                if (entity.AuthorUid != null && !organizers.Contains(entity.AuthorUid))
                {
                    organizers.Insert(0, entity.AuthorUid);
                }
                entity.OrganizerUidsJson = SerializeJson(organizers);
            }
            var intent = dto.StatusIntent.HasValue
                ? (ContentPublishStatus)dto.StatusIntent.Value
                : prevPublish == ContentPublishStatus.Draft
                    ? ContentPublishStatus.Published
                    : prevPublish;
            entity.PublishStatus = (int)(PublishingService.ModeratorCanTransition(prevPublish, intent)
                ? intent : prevPublish);
        }

        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        // A raised (or removed) seat cap immediately promotes queued attendees.
        List<string> promoted = [];
        if (capacityRaised)
        {
            var parts = await DbContext.CommunityEventParticipants
                .Where(p => p.EventId == entity.Id).ToListAsync().ConfigureAwait(false);
            promoted = CommunityEventService.FillOpenSeats(parts, entity.MaxAttendees, nowUtc);
            if (promoted.Count > 0)
            {
                CommunityEventService.SyncCounters(DbContext, entity, parts);
                await DbContext.SaveChangesAsync().ConfigureAwait(false);
                await NotifyWaitlistPromotionsAsync(entity, promoted).ConfigureAwait(false);
            }
        }

        var newPublish = (ContentPublishStatus)entity.PublishStatus;
        if (prevPublish != ContentPublishStatus.Published
            && newPublish == ContentPublishStatus.Published)
        {
            await NotifyEventInvitationsAsync(entity).ConfigureAwait(false);
            await PushMentionNotificationsAsync(dto.Group.GID, "event", entity.Id,
                entity.Title, entity.Description).ConfigureAwait(false);
        }
        else if (entity.EventStatus == (int)EventLifecycleStatus.Cancelled
            && prevLifecycle != EventLifecycleStatus.Cancelled)
        {
            await NotifyEventParticipantsAsync(entity,
                [EventRsvpStatus.Attending, EventRsvpStatus.Interested, EventRsvpStatus.Waitlist],
                UserNotificationKind.EventCancelled,
                $"Event cancelled · {entity.Title}", entity.Location,
                $"cancel:{entity.UpdatedAt.Ticks}").ConfigureAwait(false);
        }
        else if (materialChange && newPublish == ContentPublishStatus.Published)
        {
            await NotifyEventParticipantsAsync(entity,
                [EventRsvpStatus.Attending, EventRsvpStatus.Interested],
                UserNotificationKind.EventChanged,
                $"Event updated · {entity.Title}", entity.Location,
                $"change:{entity.UpdatedAt.Ticks}").ConfigureAwait(false);
        }

        await PushCommunityChangedAsync(dto.Group.GID, SyncshellCommunitySection.Events,
            entity.Id).ConfigureAwait(false);
        return MapEvent(entity, await ViewerRsvpAsync(entity.Id).ConfigureAwait(false),
            await OrganizerUserMapAsync(entity).ConfigureAwait(false));
    }

    /// <summary>UserData map for an event's organizer list - used by the
    /// read projections that include <c>OrganizerUsers</c>.</summary>
    private Task<Dictionary<string, UserData>> OrganizerUserMapAsync(SyncshellCommunityEvent e)
        => UserDataByUidAsync(
            DeserializeJson<List<string>>(e.OrganizerUidsJson) ?? (IEnumerable<string>)[]);

    /// <summary>Staff delete - attendees and interested members get a
    /// cancellation notice before the row (and its participants via cascade)
    /// disappears.</summary>
    [Authorize(Policy = "Identified")]
    public async Task<bool> GroupDeleteCommunityEvent(SyncshellCommunityItemDeleteDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));

        var entity = await DbContext.SyncshellCommunityEvents
            .Include(e => e.Participants)
            .SingleOrDefaultAsync(e => e.Id == dto.Id && e.GroupGID == dto.Group.GID)
            .ConfigureAwait(false);
        if (entity == null)
        {
            return await DeleteCommunityItem<SyncshellCommunityEvent>(dto,
                SyncshellStaffPermission.ManageEvents, SyncshellCommunitySection.Events)
                .ConfigureAwait(false);
        }
        if (!await HasCommunityRights(dto.Group.GID, SyncshellStaffPermission.ManageEvents)
                .ConfigureAwait(false))
        {
            return false;
        }

        await NotifyEventParticipantsAsync(entity,
            [EventRsvpStatus.Attending, EventRsvpStatus.Interested, EventRsvpStatus.Waitlist],
            UserNotificationKind.EventCancelled,
            $"Event removed · {entity.Title}", null, $"delete:{entity.Id}").ConfigureAwait(false);

        DbContext.SyncshellCommunityEvents.Remove(entity);
        await DbContext.SaveChangesAsync().ConfigureAwait(false);
        await PushCommunityChangedAsync(dto.Group.GID, SyncshellCommunitySection.Events,
            dto.Id).ConfigureAwait(false);
        return true;
    }

    /// <summary>Member RSVP - self-targeted. The server coerces a full
    /// Attending request into a waitlist ticket; the returned DTO tells the
    /// caller which they actually got.</summary>
    [Authorize(Policy = "Identified")]
    public async Task<EventParticipantsDto> GroupSetEventRsvp(EventRsvpDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));

        var (isMember, _) = await TryValidateUserInGroup(dto.Group.GID).ConfigureAwait(false);
        var isStaff = await HasCommunityRights(dto.Group.GID,
            SyncshellStaffPermission.ManageEvents).ConfigureAwait(false);
        if (!isMember && !isStaff)
        {
            return EmptyParticipants(dto.Group, dto.EventId);
        }

        var evt = await LoadEventForViewerAsync(dto.EventId, dto.Group.GID,
            isStaff).ConfigureAwait(false);
        if (evt == null)
        {
            return EmptyParticipants(dto.Group, dto.EventId);
        }

        // Closed lifecycle: only withdrawal / decline stay possible.
        var lifecycle = (EventLifecycleStatus)evt.EventStatus;
        var requested = (EventRsvpStatus)dto.Status;
        if (lifecycle != EventLifecycleStatus.Scheduled
            && !dto.Remove && requested == EventRsvpStatus.Attending)
        {
            return await BuildParticipantsAsync(evt, isStaff).ConfigureAwait(false);
        }

        var outcome = await CommunityEventService.ApplyRsvpAsync(DbContext,
            evt.Id, UserUID, requested, dto.Note, dto.Remove, DateTime.UtcNow)
            .ConfigureAwait(false);
        if (!outcome.Success || !outcome.Changed)
        {
            return await BuildParticipantsAsync(
                await ReloadEventAsync(evt.Id).ConfigureAwait(false) ?? evt, isStaff)
                .ConfigureAwait(false);
        }

        if (outcome.PromotedUids is { Count: > 0 })
        {
            await NotifyWaitlistPromotionsAsync(evt, outcome.PromotedUids).ConfigureAwait(false);
        }
        await PushCommunityChangedAsync(dto.Group.GID, SyncshellCommunitySection.Events,
            evt.Id).ConfigureAwait(false);

        return await BuildParticipantsAsync(
            await ReloadEventAsync(evt.Id).ConfigureAwait(false) ?? evt, isStaff)
            .ConfigureAwait(false);
    }

    /// <summary>Participant read - staff gets the full roster; members get
    /// counts, the public attendee list and always their own row.</summary>
    [Authorize(Policy = "Identified")]
    public async Task<EventParticipantsDto> GroupGetEventParticipants(EventRsvpDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));

        var (isMember, _) = await TryValidateUserInGroup(dto.Group.GID).ConfigureAwait(false);
        var isStaff = await HasCommunityRights(dto.Group.GID,
            SyncshellStaffPermission.ManageEvents).ConfigureAwait(false);
        if (!isMember && !isStaff)
        {
            return EmptyParticipants(dto.Group, dto.EventId);
        }

        var evt = await LoadEventForViewerAsync(dto.EventId, dto.Group.GID,
            isStaff).ConfigureAwait(false);
        if (evt == null)
        {
            return EmptyParticipants(dto.Group, dto.EventId);
        }
        return await BuildParticipantsAsync(evt, isStaff).ConfigureAwait(false);
    }

    /// <summary>Staff override on another member's RSVP (or removal) - the
    /// target must be a group member; they get notified of the change.</summary>
    [Authorize(Policy = "Identified")]
    public async Task<EventParticipantsDto> GroupUpdateEventParticipant(EventRsvpDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));

        var isStaff = await HasCommunityRights(dto.Group.GID,
            SyncshellStaffPermission.ManageEvents).ConfigureAwait(false);
        if (!isStaff)
        {
            return EmptyParticipants(dto.Group, dto.EventId);
        }

        var targetUid = dto.TargetUid ?? UserUID;
        var (targetIsMember, _) = await TryValidateUserInGroup(dto.Group.GID, targetUid)
            .ConfigureAwait(false);
        if (!targetIsMember)
        {
            return EmptyParticipants(dto.Group, dto.EventId);
        }

        var evt = await DbContext.SyncshellCommunityEvents
            .SingleOrDefaultAsync(e => e.Id == dto.EventId && e.GroupGID == dto.Group.GID)
            .ConfigureAwait(false);
        if (evt == null)
        {
            return EmptyParticipants(dto.Group, dto.EventId);
        }

        var outcome = await CommunityEventService.ApplyRsvpAsync(DbContext,
            evt.Id, targetUid, (EventRsvpStatus)dto.Status, dto.Note, dto.Remove,
            DateTime.UtcNow).ConfigureAwait(false);
        if (outcome.Success && outcome.Changed)
        {
            if (!string.Equals(targetUid, UserUID, StringComparison.Ordinal))
            {
                await _notificationWriter.PushAsync(DbContext, targetUid,
                    UserNotificationKind.EventChanged,
                    $"RSVP updated · {evt.Title}",
                    dto.Remove ? "Removed by event staff" : null,
                    UserUID, dto.Group.GID, "event", evt.Id.ToString(),
                    $"staff-rsvp:{evt.Id}:{targetUid}:{outcome.EffectiveStatus}:{outcome.WaitlistPosition}")
                    .ConfigureAwait(false);
            }
            if (outcome.PromotedUids is { Count: > 0 })
            {
                await NotifyWaitlistPromotionsAsync(evt, outcome.PromotedUids).ConfigureAwait(false);
            }
            await PushCommunityChangedAsync(dto.Group.GID, SyncshellCommunitySection.Events,
                evt.Id).ConfigureAwait(false);
        }

        return await BuildParticipantsAsync(
            await ReloadEventAsync(evt.Id).ConfigureAwait(false) ?? evt, isStaff: true)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Expanded occurrence feed for one shell in [FromUtc, ToUtc] - recurring
    /// series materialize server-side (bounded), exception rows mark cancelled
    /// or overridden instances. Member-gated.
    /// </summary>
    [Authorize(Policy = "Identified")]
    public async Task<List<SyncshellEventOccurrenceDto>> GroupGetEventCalendar(
        SyncshellEventCalendarRequestDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));

        var (isMember, _) = await TryValidateUserInGroup(dto.Group.GID).ConfigureAwait(false);
        var isStaff = await HasCommunityRights(dto.Group.GID,
            SyncshellStaffPermission.ManageEvents).ConfigureAwait(false);
        if (!isMember && !isStaff) return [];

        var fromUtc = dto.FromUtc;
        var toUtc = dto.ToUtc <= dto.FromUtc
            ? dto.FromUtc.AddDays(MaxCalendarWindowDays)
            : dto.ToUtc;
        if (toUtc - fromUtc > TimeSpan.FromDays(MaxCalendarWindowDays))
        {
            toUtc = fromUtc.AddDays(MaxCalendarWindowDays);
        }

        var nowUtc = DateTime.UtcNow;
        var events = await DbContext.SyncshellCommunityEvents.AsNoTracking()
            .Where(e => e.GroupGID == dto.Group.GID && e.StartUtc < toUtc
                && (isStaff || e.AuthorUid == UserUID
                    || e.PublishStatus == (int)ContentPublishStatus.Published))
            .ToListAsync().ConfigureAwait(false);
        if (events.Count == 0) return [];

        var ids = events.Select(e => e.Id).ToList();
        var exceptions = await DbContext.CommunityEventExceptions.AsNoTracking()
            .Where(x => ids.Contains(x.EventId)
                && x.OccurrenceStartUtc >= fromUtc && x.OccurrenceStartUtc < toUtc)
            .ToListAsync().ConfigureAwait(false);
        var exceptionMap = exceptions
            .GroupBy(x => x.EventId)
            .ToDictionary(g => g.Key, g => g.ToDictionary(x => x.OccurrenceStartUtc));

        var result = new List<SyncshellEventOccurrenceDto>(MaxCalendarOccurrences);
        foreach (var e in events)
        {
            var occs = EventRecurrenceService.Expand(e, fromUtc, toUtc);
            exceptionMap.TryGetValue(e.Id, out var exMap);
            var seriesDestination = DeserializeDestinationJson(e.DestinationJson);
            foreach (var occ in occs)
            {
                if (result.Count >= MaxCalendarOccurrences) break;
                var ex = exMap != null && exMap.TryGetValue(occ.OccurrenceStartUtc, out var x)
                    ? x : null;
                result.Add(new SyncshellEventOccurrenceDto(
                    e.Id, e.GroupGID,
                    occ.OccurrenceStartUtc,
                    ex?.StartUtc.HasValue == true || ex?.EndUtc.HasValue == true
                        ? ex?.EndUtc : occ.OccurrenceEndUtc,
                    ex?.Title ?? e.Title,
                    ex?.Location ?? e.Location,
                    occ.SeriesIndex,
                    ex?.Cancelled == true || e.EventStatus == (int)EventLifecycleStatus.Cancelled,
                    ex != null,
                    ResolveOccurrenceDestination(ex, seriesDestination),
                    ResolveOccurrenceDestinationOverride(ex)));
            }
        }
        return result.OrderBy(o => o.OccurrenceStartUtc).ToList();
    }

    /// <summary>Staff/organizer write of a per-occurrence exception -
    /// cancellation or field overrides; <c>Remove</c> restores the series
    /// default for that instance.</summary>
    [Authorize(Policy = "Identified")]
    public async Task<bool> GroupUpsertEventException(SyncshellEventExceptionDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));

        var isStaff = await HasCommunityRights(dto.Group.GID,
            SyncshellStaffPermission.ManageEvents).ConfigureAwait(false);

        var entity = await DbContext.SyncshellCommunityEvents
            .SingleOrDefaultAsync(e => e.Id == dto.EventId && e.GroupGID == dto.Group.GID)
            .ConfigureAwait(false);
        if (entity == null) return false;
        if (!isStaff && !PublishingService.IsAuthorOf(UserUID, entity.AuthorUid,
            PublishingService.ParseCoAuthors(entity.OrganizerUidsJson)))
        {
            return false;
        }

        return await UpsertEventExceptionCoreAsync(entity, dto.OccurrenceStartUtc,
            dto.Title, dto.StartUtc, dto.EndUtc, dto.Location, dto.Description,
            dto.Cancelled, dto.Remove, dto.Destination).ConfigureAwait(false);
    }

    /// <summary>
    /// Member event submission - the member-facing counterpart of
    /// <see cref="GroupUpsertCommunityEvent"/>. The author controls content
    /// fields (incl. capacity) on their own event; governance fields
    /// (visibility, organizer list) stay staff-only, and the publish intent
    /// resolves through the shell policy - members never self-publish when
    /// the community runs an approval mode.
    /// </summary>
    [Authorize(Policy = "Identified")]
    public async Task<SyncshellEventDto?> GroupSubmitCommunityEvent(SyncshellEventUpdateDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));

        var (isMember, _) = await TryValidateUserInGroup(dto.Group.GID).ConfigureAwait(false);
        if (!isMember) return null;

        var isStaff = await HasCommunityRights(dto.Group.GID,
            SyncshellStaffPermission.ManageEvents).ConfigureAwait(false);
        var staffBits = await GetStaffPermissionBits(dto.Group.GID, UserUID).ConfigureAwait(false);

        var entity = dto.Id == 0
            ? null
            : await DbContext.SyncshellCommunityEvents
                .SingleOrDefaultAsync(e => e.Id == dto.Id && e.GroupGID == dto.Group.GID)
                .ConfigureAwait(false);

        if (entity != null && !isStaff && !PublishingService.IsAuthorOf(UserUID,
            entity.AuthorUid, PublishingService.ParseCoAuthors(entity.OrganizerUidsJson)))
        {
            return null;
        }

        var role = isStaff ? PublishingService.WriterRole.Moderator
            : entity == null || string.Equals(entity.AuthorUid, UserUID, StringComparison.Ordinal)
                ? PublishingService.WriterRole.Member
                : PublishingService.WriterRole.Author;

        var policy = await LoadPolicyAsync(dto.Group.GID).ConfigureAwait(false);
        var intent = dto.StatusIntent.HasValue
            ? (ContentPublishStatus)dto.StatusIntent.Value
            : ContentPublishStatus.Published;
        var resolution = _publishing.ResolveWrite(policy, "events", role, staffBits,
            intent, entity == null ? null : (ContentPublishStatus)entity.PublishStatus);
        if (resolution.Verdict == PublishingService.WriteVerdict.Denied)
        {
            _logger.LogCallWarning(SpheneHubLogger.Args(dto, resolution.DenialReason));
            return null;
        }

        if (entity == null)
        {
            entity = new SyncshellCommunityEvent
            {
                GroupGID = dto.Group.GID,
                AuthorUid = UserUID,
                PublishStatus = (int)ContentPublishStatus.Draft,
                CreatedAt = DateTime.UtcNow
            };
            await DbContext.SyncshellCommunityEvents.AddAsync(entity).ConfigureAwait(false);
        }

        var prevPublish = (ContentPublishStatus)entity.PublishStatus;
        var prevLifecycle = (EventLifecycleStatus)entity.EventStatus;

        entity.Title = dto.Title;
        entity.Description = dto.Description;
        entity.StartUtc = dto.StartUtc;
        entity.EndUtc = dto.EndUtc;
        entity.Location = dto.Location;
        entity.Url = dto.Url;
        entity.DestinationJson = SerializeDestinationJson(dto.Destination);
        entity.TimezoneId = SanitizeTimezoneId(dto.TimezoneId);
        entity.RecurrenceRule = EventRecurrenceService.ParseRule(dto.RecurrenceRule) == null
            ? null : dto.RecurrenceRule;
        entity.RemindersJson = SerializeJson(SanitizeReminders(dto.Reminders));
        entity.MaxAttendees = dto.MaxAttendees is null or >= 1
            ? dto.MaxAttendees : entity.MaxAttendees;
        if (Enum.IsDefined((EventLifecycleStatus)dto.EventStatus))
        {
            entity.EventStatus = dto.EventStatus;
        }
        entity.UpdatedAt = DateTime.UtcNow;
        if (dto.ImageMediaHash != null)
        {
            entity.ImageMediaHash = string.IsNullOrEmpty(dto.ImageMediaHash)
                ? null : dto.ImageMediaHash;
            entity.ImageBase64 = null;
            if (entity.ImageMediaHash != null)
            {
                await _communityMedia.RecordMediaAsync(DbContext, dto.ImageMediaHash,
                    UserUID, null, null).ConfigureAwait(false);
            }
        }
        else
        {
            entity.ImageBase64 = dto.ImageBase64;
        }
        if (dto.PreviewMediaHash != null)
        {
            entity.PreviewMediaHash = string.IsNullOrEmpty(dto.PreviewMediaHash)
                ? null : dto.PreviewMediaHash;
            if (entity.PreviewMediaHash != null)
            {
                await _communityMedia.RecordMediaAsync(DbContext, dto.PreviewMediaHash,
                    UserUID, null, null).ConfigureAwait(false);
            }
        }
        if (isStaff)
        {
            if (Enum.IsDefined((EventVisibility)dto.Visibility))
            {
                entity.Visibility = dto.Visibility;
            }
            if (dto.OrganizerUids != null)
            {
                entity.OrganizerUidsJson = SerializeJson(dto.OrganizerUids
                    .Where(u => !string.IsNullOrWhiteSpace(u))
                    .Distinct(StringComparer.Ordinal).ToList());
            }
        }
        entity.PublishStatus = (int)resolution.EffectiveStatus;

        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        if (resolution.EffectiveStatus == ContentPublishStatus.PendingApproval
            && prevPublish != ContentPublishStatus.PendingApproval)
        {
            var reviewers = await CollectReviewerUidsAsync(dto.Group.GID,
                SyncshellStaffPermission.ManageEvents).ConfigureAwait(false);
            await _notificationWriter.PushManyAsync(reviewers,
                UserNotificationKind.ContentPendingReview,
                $"Event pending review · {entity.Title}",
                entity.Location, UserUID, dto.Group.GID, "event", entity.Id.ToString(),
                uid => $"review:event:{entity.Id}:{uid}").ConfigureAwait(false);
        }

        if (prevPublish != ContentPublishStatus.Published
            && resolution.EffectiveStatus == ContentPublishStatus.Published)
        {
            await NotifyEventInvitationsAsync(entity).ConfigureAwait(false);
            await PushMentionNotificationsAsync(dto.Group.GID, "event", entity.Id,
                entity.Title, entity.Description).ConfigureAwait(false);
        }
        else if (entity.EventStatus == (int)EventLifecycleStatus.Cancelled
            && prevLifecycle != EventLifecycleStatus.Cancelled)
        {
            await NotifyEventParticipantsAsync(entity,
                [EventRsvpStatus.Attending, EventRsvpStatus.Interested, EventRsvpStatus.Waitlist],
                UserNotificationKind.EventCancelled,
                $"Event cancelled · {entity.Title}", entity.Location,
                $"cancel:{entity.UpdatedAt.Ticks}").ConfigureAwait(false);
        }

        await PushCommunityChangedAsync(dto.Group.GID, SyncshellCommunitySection.Events,
            entity.Id).ConfigureAwait(false);
        return MapEvent(entity, await ViewerRsvpAsync(entity.Id).ConfigureAwait(false),
            await OrganizerUserMapAsync(entity).ConfigureAwait(false));
    }

    /// <summary>Staff decision on a queued member-submitted event - approve
    /// publishes (invites + mentions fire), reject returns it to the author
    /// with the note attached.</summary>
    [Authorize(Policy = "Identified")]
    public async Task<bool> GroupDecideCommunityEvent(CommunityReviewDecisionDto dto)
    {
        _logger.LogCallInfo(SpheneHubLogger.Args(dto));

        var hasRights = await HasCommunityRights(dto.Group.GID,
            SyncshellStaffPermission.ManageEvents).ConfigureAwait(false);
        if (!hasRights) return false;

        var entity = await DbContext.SyncshellCommunityEvents
            .SingleOrDefaultAsync(e => e.Id == dto.ContentId && e.GroupGID == dto.Group.GID)
            .ConfigureAwait(false);
        if (entity == null || entity.PublishStatus != (int)ContentPublishStatus.PendingApproval)
        {
            return false;
        }

        entity.ReviewedByUid = UserUID;
        entity.ReviewedAt = DateTime.UtcNow;
        entity.ReviewNote = dto.Note;
        entity.UpdatedAt = DateTime.UtcNow;
        entity.PublishStatus = (int)(dto.Approve
            ? ContentPublishStatus.Published : ContentPublishStatus.Rejected);
        await DbContext.SaveChangesAsync().ConfigureAwait(false);

        if (entity.AuthorUid != null)
        {
            await _notificationWriter.PushAsync(DbContext, entity.AuthorUid,
                dto.Approve ? UserNotificationKind.EventInvitation : UserNotificationKind.ContentRejected,
                dto.Approve ? $"Event approved · {entity.Title}" : $"Event declined · {entity.Title}",
                dto.Note, UserUID, dto.Group.GID, "event", entity.Id.ToString(),
                $"decision:event:{entity.Id}:{dto.Approve}").ConfigureAwait(false);
        }

        if (dto.Approve)
        {
            await NotifyEventInvitationsAsync(entity).ConfigureAwait(false);
            await PushMentionNotificationsAsync(dto.Group.GID, "event", entity.Id,
                entity.Title, entity.Description).ConfigureAwait(false);
        }

        await PushCommunityChangedAsync(dto.Group.GID, SyncshellCommunitySection.Events,
            entity.Id).ConfigureAwait(false);
        return true;
    }

    // ------------------------------------------------------------------
    //  helpers
    // ------------------------------------------------------------------

    /// <summary>Writes/removes one occurrence exception; the occurrence key
    /// must match a real expansion of the series so typos can't create phantom
    /// instances. Returns false on invalid occurrence keys.</summary>
    private async Task<bool> UpsertEventExceptionCoreAsync(SyncshellCommunityEvent entity,
        DateTime occurrenceStartUtc, string? title, DateTime? startUtc, DateTime? endUtc,
        string? location, string? description, bool cancelled, bool remove,
        EventDestinationDto? destination = null)
    {
        if (EventRecurrenceService.ParseRule(entity.RecurrenceRule) == null)
        {
            return false; // exceptions only exist on recurring series
        }
        var window = EventRecurrenceService.Expand(entity,
            occurrenceStartUtc.AddDays(-2), occurrenceStartUtc.AddDays(2), cap: 8);
        if (!window.Any(o => o.OccurrenceStartUtc == occurrenceStartUtc))
        {
            return false;
        }

        var ex = await DbContext.CommunityEventExceptions
            .SingleOrDefaultAsync(x => x.EventId == entity.Id
                && x.OccurrenceStartUtc == occurrenceStartUtc).ConfigureAwait(false);
        if (remove)
        {
            if (ex != null)
            {
                DbContext.CommunityEventExceptions.Remove(ex);
                await DbContext.SaveChangesAsync().ConfigureAwait(false);
                await PushCommunityChangedAsync(entity.GroupGID,
                    SyncshellCommunitySection.Events, entity.Id).ConfigureAwait(false);
            }
            return true;
        }

        ex ??= new SyncshellCommunityEventException
        {
            EventId = entity.Id,
            OccurrenceStartUtc = occurrenceStartUtc
        };
        ex.Cancelled = cancelled;
        ex.Title = title;
        ex.StartUtc = startUtc;
        ex.EndUtc = endUtc;
        ex.Location = location;
        ex.Description = description;
        // null → inherit the series; Kind.None → explicit "no destination"
        // sentinel; anything else → validated override.
        ex.DestinationJson = destination == null
            ? null
            : (EventDestinationKind)destination.Kind == EventDestinationKind.None
                ? ExplicitNoneDestinationJson
                : SerializeDestinationJson(destination);
        ex.UpdatedAt = DateTime.UtcNow;
        if (DbContext.Entry(ex).State == EntityState.Detached)
        {
            await DbContext.CommunityEventExceptions.AddAsync(ex).ConfigureAwait(false);
        }
        entity.UpdatedAt = DateTime.UtcNow;
        await DbContext.SaveChangesAsync().ConfigureAwait(false);
        await PushCommunityChangedAsync(entity.GroupGID, SyncshellCommunitySection.Events,
            entity.Id).ConfigureAwait(false);
        return true;
    }

    /// <summary>Loads an event only when the caller may see it - staff and
    /// the author always do; members see published rows (incl. cancelled -
    /// cancellation is information, not absence).</summary>
    private async Task<SyncshellCommunityEvent?> LoadEventForViewerAsync(long eventId,
        string gid, bool isStaff)
    {
        return await DbContext.SyncshellCommunityEvents
            .SingleOrDefaultAsync(e => e.Id == eventId && e.GroupGID == gid
                && (isStaff || e.AuthorUid == UserUID
                    || e.PublishStatus == (int)ContentPublishStatus.Published))
            .ConfigureAwait(false);
    }

    private async Task<SyncshellCommunityEvent?> ReloadEventAsync(long eventId)
        => await DbContext.SyncshellCommunityEvents
            .Include(e => e.Participants)
            .SingleOrDefaultAsync(e => e.Id == eventId).ConfigureAwait(false);

    /// <summary>Caller's own RSVP on one event - null when not participating.</summary>
    private async Task<int?> ViewerRsvpAsync(long eventId)
        => await DbContext.CommunityEventParticipants.AsNoTracking()
            .Where(p => p.EventId == eventId && p.UserUID == UserUID)
            .Select(p => (int?)p.Status).SingleOrDefaultAsync().ConfigureAwait(false);

    /// <summary>
    /// Builds the participant payload with visibility rules applied: staff
    /// see every row incl. notes; members see counts, the attendee list and
    /// their own row (whatever its status). Waitlist order beyond the
    /// caller's own position stays server-side.
    /// </summary>
    private async Task<EventParticipantsDto> BuildParticipantsAsync(
        SyncshellCommunityEvent evt, bool isStaff)
    {
        // Fresh read - the caller's DbContext may already track a stale or
        // partially-populated Participants collection.
        var participants = await DbContext.CommunityEventParticipants.AsNoTracking()
            .Where(p => p.EventId == evt.Id).ToListAsync().ConfigureAwait(false);

        var viewer = participants.SingleOrDefault(p => p.UserUID == UserUID);
        var interested = CommunityEventService.InterestedCount(participants);

        var rows = isStaff
            ? participants.OrderBy(p => p.CreatedAt).ToList()
            : participants
                .Where(p => p.Status == (int)EventRsvpStatus.Attending || p == viewer)
                .OrderBy(p => p.CreatedAt).ToList();

        var users = await UserDataByUidAsync(rows.Select(p => p.UserUID))
            .ConfigureAwait(false);
        var dtos = rows.Select(p => new EventParticipantDto(
            evt.Id, p.UserUID, p.Status,
            isStaff || p.UserUID == UserUID ? p.WaitlistPosition : null,
            isStaff ? p.Note : (p.UserUID == UserUID ? p.Note : null),
            p.UpdatedAt,
            users.TryGetValue(p.UserUID, out var u) ? u : new UserData(p.UserUID, string.Empty))).ToList();

        return new EventParticipantsDto(
            new GroupData(evt.GroupGID), evt.Id, evt.MaxAttendees,
            evt.AttendingCount, interested, evt.WaitlistCount,
            viewer?.Status, viewer?.WaitlistPosition, dtos);
    }

    private static EventParticipantsDto EmptyParticipants(GroupData group, long eventId)
        => new(group, eventId, null, 0, 0, 0, null, null, []);

    /// <summary>First-publish fan-out: every group member gets a durable
    /// invitation, deduped so re-publishes never re-notify.</summary>
    private async Task NotifyEventInvitationsAsync(SyncshellCommunityEvent evt)
    {
        var members = await DbContext.GroupPairs.AsNoTracking()
            .Where(p => p.GroupGID == evt.GroupGID)
            .Select(p => p.GroupUserUID).ToListAsync().ConfigureAwait(false);
        members.RemoveAll(u => string.Equals(u, UserUID, StringComparison.Ordinal));
        if (members.Count == 0) return;
        await _notificationWriter.PushManyAsync(members,
            UserNotificationKind.EventInvitation,
            $"New event · {evt.Title}", evt.Location, UserUID, evt.GroupGID,
            "event", evt.Id.ToString(),
            uid => $"event-invite:{evt.Id}:{uid}").ConfigureAwait(false);
    }

    /// <summary>Durable fan-out to participant rows of the given statuses -
    /// actor excluded; <paramref name="dedupeSuffix"/> carries the change
    /// version so each distinct change notifies exactly once.</summary>
    private async Task NotifyEventParticipantsAsync(SyncshellCommunityEvent evt,
        IReadOnlyCollection<EventRsvpStatus> statuses, UserNotificationKind kind,
        string title, string? snippet, string dedupeSuffix)
    {
        var targets = await DbContext.CommunityEventParticipants.AsNoTracking()
            .Where(p => p.EventId == evt.Id
                && statuses.Contains((EventRsvpStatus)p.Status))
            .Select(p => p.UserUID).ToListAsync().ConfigureAwait(false);
        targets = targets
            .Where(u => !string.Equals(u, UserUID, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal).ToList();
        if (targets.Count == 0) return;
        await _notificationWriter.PushManyAsync(targets, kind, title, snippet,
            UserUID, evt.GroupGID, "event", evt.Id.ToString(),
            uid => $"event:{evt.Id}:{dedupeSuffix}:{uid}").ConfigureAwait(false);
    }

    /// <summary>"A seat opened for you" - one durable notice per promotion,
    /// keyed to the promotion instant so a later demote→re-promote cycle can
    /// notify again.</summary>
    private async Task NotifyWaitlistPromotionsAsync(SyncshellCommunityEvent evt,
        IEnumerable<string> promotedUids)
    {
        var stamp = DateTime.UtcNow.Ticks;
        await _notificationWriter.PushManyAsync(promotedUids,
            UserNotificationKind.EventWaitlistPromoted,
            $"Waitlist spot opened · {evt.Title}", null, UserUID, evt.GroupGID,
            "event", evt.Id.ToString(),
            uid => $"event-promote:{evt.Id}:{uid}:{stamp}").ConfigureAwait(false);
    }

    private static string? SanitizeTimezoneId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(id);
            return id;
        }
        catch (TimeZoneNotFoundException)
        {
            return null;
        }
        catch (InvalidTimeZoneException)
        {
            return null;
        }
    }

    private static List<int>? SanitizeReminders(List<int>? reminders)
    {
        if (reminders == null || reminders.Count == 0) return null;
        var clean = reminders.Where(m => m is > 0 and <= MaxReminderMinutes)
            .Distinct().Take(MaxEventReminders).ToList();
        return clean.Count == 0 ? null : clean;
    }

    /// <summary>Event → DTO incl. the viewer's own RSVP (null when the read
    /// has no member context, e.g. the public page preview).</summary>
    private static SyncshellEventDto MapEvent(SyncshellCommunityEvent e, int? viewerRsvp = null,
        IReadOnlyDictionary<string, UserData>? users = null) => new(
        e.Id, e.GroupGID, e.Title, e.Description, e.StartUtc, e.EndUtc,
        e.Location, e.Url, e.ImageBase64, e.CreatedAt, e.UpdatedAt,
        e.ImageMediaHash,
        DeserializeJson<List<string>>(e.OrganizerUidsJson), e.AuthorUid,
        e.MaxAttendees, e.Visibility, e.EventStatus, e.PublishStatus,
        e.TimezoneId, e.RecurrenceRule,
        DeserializeJson<List<int>>(e.RemindersJson),
        e.AttendingCount, e.WaitlistCount, viewerRsvp,
        e.ReviewedByUid, e.ReviewNote, e.CommentCount, e.CommentsLocked,
        DeserializeDestinationJson(e.DestinationJson),
        OrganizerUsers: OrganizerUsersFor(e, users),
        PreviewMediaHash: e.PreviewMediaHash);

    /// <summary>Ordered <see cref="UserData"/> projection of an event's
    /// organizer list - mirrors <c>OrganizerUids</c> order (first = creator);
    /// unresolved uids carry an empty alias so the client shows the neutral
    /// fallback instead of a raw identifier.</summary>
    private static List<UserData>? OrganizerUsersFor(SyncshellCommunityEvent e,
        IReadOnlyDictionary<string, UserData>? users)
    {
        var uids = DeserializeJson<List<string>>(e.OrganizerUidsJson);
        if (uids == null || uids.Count == 0) return null;
        return uids.Select(uid => users != null && users.TryGetValue(uid, out var u)
            ? u : new UserData(uid, string.Empty)).ToList();
    }

    /// <summary>Loads <see cref="UserData"/> for a uid set in one query -
    /// empty input short-circuits.</summary>
    private async Task<Dictionary<string, UserData>> UserDataByUidAsync(IEnumerable<string> uids)
    {
        var set = uids.Where(u => !string.IsNullOrWhiteSpace(u))
            .Distinct(StringComparer.Ordinal).ToList();
        if (set.Count == 0)
        {
            return new Dictionary<string, UserData>(StringComparer.Ordinal);
        }
        var users = await DbContext.Users.AsNoTracking()
            .Where(u => set.Contains(u.UID)).ToListAsync().ConfigureAwait(false);
        return users.ToDictionary(u => u.UID, u => u.ToUserData(), StringComparer.Ordinal);
    }

    /// <summary>Serialized <see cref="EventDestinationKind.None"/> marker -
    /// stored on exception rows to mean "explicitly no destination for this
    /// date", distinct from null (inherit the series).</summary>
    private const string ExplicitNoneDestinationJson = "{\"Kind\":0}";

    /// <summary>Sanitizes + serializes a destination for storage - null DTOs,
    /// <see cref="EventDestinationKind.None"/> and invalid input all normalize
    /// to null (no stored destination).</summary>
    private static string? SerializeDestinationJson(EventDestinationDto? dto)
        => EventDestinationValidator.Sanitize(dto) is { } clean
            ? JsonSerializer.Serialize(clean) : null;

    /// <summary>Parses a stored destination - corrupt payloads read as "no
    /// destination" rather than poisoning the event row.</summary>
    private static EventDestinationDto? DeserializeDestinationJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var dto = JsonSerializer.Deserialize<EventDestinationDto>(json);
            // The explicit-none sentinel resolves to no destination.
            if (dto == null || (EventDestinationKind)dto.Kind != EventDestinationKind.HousingAddress
                && (EventDestinationKind)dto.Kind != EventDestinationKind.PublicLocation)
            {
                return null;
            }
            return EventDestinationValidator.Sanitize(dto);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Resolves an occurrence's effective destination: no exception →
    /// series value; exception without a destination override → series value;
    /// explicit-none sentinel → null; override → the override.</summary>
    private static EventDestinationDto? ResolveOccurrenceDestination(
        SyncshellCommunityEventException? ex, EventDestinationDto? seriesDestination)
    {
        if (ex == null || string.IsNullOrWhiteSpace(ex.DestinationJson))
        {
            return seriesDestination;
        }
        return DeserializeDestinationJson(ex.DestinationJson);
    }

    /// <summary>The exception row's raw destination payload for editors:
    /// null when absent (inherit) or corrupt; a <see cref="EventDestinationKind.None"/>
    /// DTO for the explicit-clear sentinel; the override otherwise.</summary>
    private static EventDestinationDto? ResolveOccurrenceDestinationOverride(
        SyncshellCommunityEventException? ex)
    {
        if (ex == null || string.IsNullOrWhiteSpace(ex.DestinationJson))
        {
            return null;
        }
        try
        {
            var dto = JsonSerializer.Deserialize<EventDestinationDto>(ex.DestinationJson);
            if (dto == null) return null;
            return (EventDestinationKind)dto.Kind == EventDestinationKind.None
                ? new EventDestinationDto { Kind = (int)EventDestinationKind.None }
                : EventDestinationValidator.Sanitize(dto);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

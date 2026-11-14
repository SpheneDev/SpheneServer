using System.ComponentModel.DataAnnotations;

namespace SpheneShared.Models;

public class SyncshellCommunityEvent : ICommentThreadHost
{
    [Key]
    public long Id { get; set; }

    [MaxLength(40)]
    public string GroupGID { get; set; } = string.Empty;
    public Group Group { get; set; } = null!;

    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(4000)]
    public string? Description { get; set; }

    public DateTime StartUtc { get; set; }
    public DateTime? EndUtc { get; set; }

    [MaxLength(200)]
    public string? Location { get; set; }

    [MaxLength(500)]
    public string? Url { get; set; }

    public string? ImageBase64 { get; set; }

    /// <summary>Hash-addressed flyer media - preferred over base64 when set.</summary>
    [MaxLength(40)]
    public string? ImageMediaHash { get; set; }

    /// <summary>Hash-addressed 16:9 preview image for list/card contexts -
    /// distinct from <see cref="ImageMediaHash"/> (the 3:1 detail banner).</summary>
    [MaxLength(40)]
    public string? PreviewMediaHash { get; set; }

    /// <summary>Event creator - mirrors AuthorUid on news posts.</summary>
    [MaxLength(10)]
    public string? AuthorUid { get; set; }

    /// <summary>JSON List&lt;string&gt; - creator first; organizers are the
    /// event's co-author set (edit rights, never review rights).</summary>
    public string? OrganizerUidsJson { get; set; }

    /// <summary>null = unbounded; attendee seat cap.</summary>
    public int? MaxAttendees { get; set; }

    /// <summary><see cref="Sphene.API.Data.Enum.EventVisibility"/> as int.</summary>
    public int Visibility { get; set; }

    /// <summary><see cref="Sphene.API.Data.Enum.EventLifecycleStatus"/> as int.</summary>
    public int EventStatus { get; set; }

    /// <summary><see cref="Sphene.API.Data.Enum.ContentPublishStatus"/> as int.</summary>
    public int PublishStatus { get; set; }

    /// <summary>TimeZoneInfo.Id - display + DST-correct recurrence anchoring.</summary>
    [MaxLength(64)]
    public string? TimezoneId { get; set; }

    /// <summary>Strict RRULE subset - see EventRecurrenceService.</summary>
    [MaxLength(200)]
    public string? RecurrenceRule { get; set; }

    /// <summary>JSON List&lt;int&gt; - minutes-before-start reminder offsets.</summary>
    public string? RemindersJson { get; set; }

    /// <summary>Denormalized seat counters - maintained inside RSVP txns only.</summary>
    public int AttendingCount { get; set; }
    public int WaitlistCount { get; set; }

    /// <summary>Monotonic RSVP-write counter - diagnostics/versioning; the
    /// row lock, not this field, serializes concurrent writers.</summary>
    public int RsvpVersion { get; set; }

    /// <summary>Denormalized comment count - maintained under comment txns.</summary>
    public int CommentCount { get; set; }
    /// <summary>Thread lock - non-staff comment writes rejected while set.</summary>
    public bool CommentsLocked { get; set; }

    /// <summary>Optional structured travel destination - serialized
    /// <see cref="Sphene.API.Dto.Group.EventDestinationDto"/>; null = none.</summary>
    public string? DestinationJson { get; set; }

    /// <summary>Review queue metadata (same shape as news).</summary>
    [MaxLength(10)]
    public string? ReviewedByUid { get; set; }
    public DateTime? ReviewedAt { get; set; }
    [MaxLength(500)]
    public string? ReviewNote { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public List<SyncshellCommunityEventParticipant> Participants { get; set; } = [];
    public List<SyncshellCommunityEventException> Exceptions { get; set; } = [];
}

/// <summary>
/// RSVP row per (event, user). <see cref="WaitlistPosition"/> is a monotonic
/// ticket - promotion picks the minimum, so ordering stays deterministic FIFO
/// even after removals.
/// </summary>
public class SyncshellCommunityEventParticipant
{
    public long EventId { get; set; }
    public SyncshellCommunityEvent Event { get; set; } = null!;

    [MaxLength(10)]
    public string UserUID { get; set; } = string.Empty;
    public User User { get; set; } = null!;

    /// <summary><see cref="Sphene.API.Data.Enum.EventRsvpStatus"/> as int.</summary>
    public int Status { get; set; }

    public int? WaitlistPosition { get; set; }

    /// <summary>Optional member note - staff-visible only.</summary>
    [MaxLength(200)]
    public string? Note { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? PromotedAt { get; set; }
}

/// <summary>
/// Per-occurrence exception of a recurring series - cancelled instance or
/// field overrides; null fields inherit from the series.
/// </summary>
public class SyncshellCommunityEventException
{
    public long EventId { get; set; }
    public SyncshellCommunityEvent Event { get; set; } = null!;

    /// <summary>The canonical occurrence start this exception overrides.</summary>
    public DateTime OccurrenceStartUtc { get; set; }

    public bool Cancelled { get; set; }

    [MaxLength(200)]
    public string? Title { get; set; }
    public DateTime? StartUtc { get; set; }
    public DateTime? EndUtc { get; set; }
    [MaxLength(200)]
    public string? Location { get; set; }
    [MaxLength(4000)]
    public string? Description { get; set; }

    /// <summary>Occurrence destination override - serialized
    /// <see cref="Sphene.API.Dto.Group.EventDestinationDto"/>; null inherits the
    /// series, Kind None explicitly clears it for this date.</summary>
    public string? DestinationJson { get; set; }

    public DateTime UpdatedAt { get; set; }
}

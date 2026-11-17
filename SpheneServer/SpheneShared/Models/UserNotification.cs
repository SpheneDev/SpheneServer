using Sphene.API.Data.Enum;
using System.ComponentModel.DataAnnotations;

namespace SpheneShared.Models;

/// <summary>
/// One durable inbox notification for a user. <see cref="ContentKind"/>/
/// <see cref="ContentId"/> are the generic link target resolved by the
/// client ("event", "post", "comment", "application"). <see cref="DedupeKey"/>
/// optionally suppresses duplicate rows for the same logical event
/// (e.g. being mentioned twice in one post edit).
/// </summary>
public class UserNotification
{
    [Key]
    public long Id { get; set; }

    [MaxLength(10)]
    public string RecipientUID { get; set; } = string.Empty;
    public User Recipient { get; set; } = null!;

    public UserNotificationKind Kind { get; set; }

    [MaxLength(10)]
    public string? ActorUID { get; set; }

    [MaxLength(20)]
    public string? GroupGID { get; set; }

    /// <summary>Generic target type: "event" | "post" | "comment" | "application" | null.</summary>
    [MaxLength(32)]
    public string? ContentKind { get; set; }

    /// <summary>Generic target id (numeric ids stringified).</summary>
    [MaxLength(64)]
    public string? ContentId { get; set; }

    /// <summary>Inner target inside <see cref="ContentId"/> - e.g. the comment
    /// id when <see cref="ContentId"/> holds the post it was left on.</summary>
    [MaxLength(64)]
    public string? SubContentId { get; set; }

    /// <summary>Uid of the content owner hosting the discussion - for blog
    /// targets the profile whose Chronicles tab carries the thread.</summary>
    [MaxLength(10)]
    public string? TargetUid { get; set; }

    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? Snippet { get; set; }

    public bool IsRead { get; set; }

    /// <summary>Optional producer-chosen dedup token; unique per recipient when set.</summary>
    [MaxLength(128)]
    public string? DedupeKey { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

using System.ComponentModel.DataAnnotations;

namespace SpheneShared.Models;

public class SyncshellCommunityNewsPost : ICommentThreadHost
{
    [Key]
    public long Id { get; set; }

    [MaxLength(40)]
    public string GroupGID { get; set; } = string.Empty;
    public Group Group { get; set; } = null!;

    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(8000)]
    public string Body { get; set; } = string.Empty;

    public bool IsPinned { get; set; }
    /// <summary>Legacy draft flag - kept in sync with <see cref="Status"/> for
    /// backward compatibility; <see cref="Status"/> is the source of truth.</summary>
    public bool IsDraft { get; set; }
    public DateTime PublishedAt { get; set; }

    /// <summary><see cref="Sphene.API.Data.Enum.ContentPublishStatus"/> as int.</summary>
    public int Status { get; set; } = 3;
    /// <summary>Go-live instant when <see cref="Status"/> is Scheduled.</summary>
    public DateTime? ScheduledForUtc { get; set; }

    /// <summary>Creator UID - original author, distinct from whoever edits.</summary>
    [MaxLength(64)]
    public string? AuthorUid { get; set; }
    /// <summary>JSON list of additional author UIDs allowed to edit.</summary>
    public string? CoAuthorUidsJson { get; set; }

    /// <summary>UID of the staff member who last decided a review.</summary>
    [MaxLength(64)]
    public string? ReviewedByUid { get; set; }
    public DateTime? ReviewedAt { get; set; }
    /// <summary>Optional note attached to the last review decision.</summary>
    [MaxLength(500)]
    public string? ReviewNote { get; set; }

    /// <summary>Denormalized visible+deleted comment count - maintained under
    /// the comment row lock; authoritative for the UI badge.</summary>
    public int CommentCount { get; set; }
    /// <summary>When set, non-staff comment writes are rejected (thread lock).</summary>
    public bool CommentsLocked { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

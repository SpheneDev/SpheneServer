using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SpheneShared.Models;

/// <summary>Immutable audit record for every moderation action - comment
/// state changes, thread locks, report resolutions. <see cref="ActorScope"/>
/// records WHICH hat the actor wore (1 blog owner · 2 community staff ·
/// 3 platform moderator) so restore authority never depends on the comment's
/// current state alone.</summary>
public class CommunityModerationLog
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [Required]
    [MaxLength(64)]
    public string ActorUid { get; set; } = string.Empty;

    /// <summary>1 blog owner · 2 community staff · 3 platform moderator.</summary>
    public int ActorScope { get; set; }

    /// <summary>hide | restore | remove | lock | unlock | report_resolve |
    /// report_dismiss | report_create</summary>
    [MaxLength(32)]
    public string Action { get; set; } = string.Empty;

    /// <summary><see cref="Sphene.API.Data.Enum.CommentTargetKind"/> as int.</summary>
    public int TargetKind { get; set; }
    public long TargetId { get; set; }
    /// <summary>Comment the action applied to, when relevant.</summary>
    public long? CommentId { get; set; }
    /// <summary>Report the action applied to, when relevant.</summary>
    public long? ReportId { get; set; }

    [MaxLength(40)]
    public string? GroupGID { get; set; }
    [MaxLength(64)]
    public string? OwnerUid { get; set; }

    [MaxLength(500)]
    public string? Reason { get; set; }

    public DateTime CreatedAt { get; set; }
}

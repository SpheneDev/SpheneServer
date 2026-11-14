using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SpheneShared.Models;

/// <summary>One content report. Community-scoped reports (group_gid set)
/// resolve via the shell moderation queue; blog reports (owner_uid set)
/// resolve via the platform queue (User.IsModerator).</summary>
public class CommunityReport
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    /// <summary><see cref="Sphene.API.Data.Enum.ReportTargetKind"/> as int.</summary>
    public int TargetKind { get; set; }
    public long TargetId { get; set; }

    [MaxLength(40)]
    public string? GroupGID { get; set; }
    public Group? Group { get; set; }

    [MaxLength(64)]
    public string? OwnerUid { get; set; }
    public User? Owner { get; set; }

    [Required]
    [MaxLength(64)]
    public string ReporterUid { get; set; } = string.Empty;
    public User Reporter { get; set; } = null!;

    /// <summary><see cref="Sphene.API.Data.Enum.ReportReason"/> as int.</summary>
    public int Reason { get; set; }

    [MaxLength(1000)]
    public string? Details { get; set; }

    /// <summary><see cref="Sphene.API.Data.Enum.ReportState"/> as int.</summary>
    public int State { get; set; }

    [MaxLength(64)]
    public string? ResolvedByUid { get; set; }
    public DateTime? ResolvedAt { get; set; }

    [MaxLength(500)]
    public string? ResolutionNote { get; set; }

    public DateTime CreatedAt { get; set; }
}

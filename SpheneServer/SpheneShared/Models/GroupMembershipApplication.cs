using System.ComponentModel.DataAnnotations;
using Sphene.API.Data.Enum;

namespace SpheneShared.Models;

/// <summary>
/// A user's request to join an approval-gated syncshell. One row per
/// (group, applicant) - re-applying after a decision resets the same row to
/// Pending so the unique key doubles as the duplicate guard.
/// </summary>
public class GroupMembershipApplication
{
    public long Id { get; set; }

    public Group Group { get; set; }
    [MaxLength(20)]
    public string GroupGID { get; set; }

    public User Applicant { get; set; }
    [MaxLength(10)]
    public string ApplicantUID { get; set; }

    public MembershipApplicationStatus Status { get; set; } = MembershipApplicationStatus.Pending;

    /// <summary>Optional note the applicant attached to their request.</summary>
    [MaxLength(500)]
    public string? Message { get; set; }

    public User? ReviewedBy { get; set; }
    [MaxLength(10)]
    public string? ReviewedByUID { get; set; }

    /// <summary>Optional staff note stored with the decision.</summary>
    [MaxLength(300)]
    public string? ReviewNote { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ReviewedAt { get; set; }
}

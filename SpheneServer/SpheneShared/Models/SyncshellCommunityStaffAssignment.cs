using System.ComponentModel.DataAnnotations;

namespace SpheneShared.Models;

/// <summary>
/// Links a group member to a staff role - members with an assignment count as
/// staff even when they are not moderators. One row per (member, role) pair;
/// a member may hold several jobs and their permission flags union.
/// </summary>
public class SyncshellCommunityStaffAssignment
{
    [Key]
    public long Id { get; set; }

    [MaxLength(40)]
    public string GroupGID { get; set; } = string.Empty;
    public Group Group { get; set; } = null!;

    [MaxLength(40)]
    public string UserUID { get; set; } = string.Empty;

    public long RoleId { get; set; }
}

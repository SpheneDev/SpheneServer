using System.ComponentModel.DataAnnotations;

namespace SpheneShared.Models;

/// <summary>
/// Custom staff job on a syncshell community (Barkeeper, Photographer, …).
/// Carries the capability flags that gate the community write endpoints, so an
/// owner can delegate e.g. event management without granting moderation.
/// </summary>
public class SyncshellCommunityStaffRole
{
    [Key]
    public long Id { get; set; }

    [MaxLength(40)]
    public string GroupGID { get; set; } = string.Empty;
    public Group Group { get; set; } = null!;

    [MaxLength(60)]
    public string Name { get; set; } = string.Empty;

    /// <summary>ARGB badge color shown on staff rows.</summary>
    public uint ColorArgb { get; set; }

    [MaxLength(40)]
    public string? Icon { get; set; }

    /// <summary>Bitfield of <c>SyncshellStaffPermission</c> (API enum).</summary>
    public long Permissions { get; set; }

    public int SortOrder { get; set; }

    public DateTime CreatedAt { get; set; }
}

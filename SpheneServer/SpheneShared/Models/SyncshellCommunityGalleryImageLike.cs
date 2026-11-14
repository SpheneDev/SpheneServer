using System.ComponentModel.DataAnnotations;

namespace SpheneShared.Models;

/// <summary>
/// One member's like on a community gallery image - keyed by (ImageId, UserUID)
/// so a member can like a photo exactly once. GroupGID rides along for cheap
/// per-shell cleanup queries.
/// </summary>
public class SyncshellCommunityGalleryImageLike
{
    public long ImageId { get; set; }
    public SyncshellCommunityGalleryImage Image { get; set; } = null!;

    [MaxLength(64)]
    public string UserUID { get; set; } = string.Empty;

    [MaxLength(40)]
    public string GroupGID { get; set; } = string.Empty;
    public Group Group { get; set; } = null!;

    public DateTime CreatedAt { get; set; }
}

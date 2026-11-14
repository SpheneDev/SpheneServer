using System.ComponentModel.DataAnnotations;

namespace SpheneShared.Models;

/// <summary>
/// Named photo album on a syncshell community page. May be linked to a
/// community event so its photos surface as that event's memories.
/// </summary>
public class SyncshellCommunityGalleryAlbum
{
    [Key]
    public long Id { get; set; }

    [MaxLength(40)]
    public string GroupGID { get; set; } = string.Empty;
    public Group Group { get; set; } = null!;

    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? Description { get; set; }

    /// <summary>Community event this album collects memories for, if any.</summary>
    public long? LinkedEventId { get; set; }

    /// <summary>Gallery image used as the album cover, if any.</summary>
    public long? CoverImageId { get; set; }

    public int SortOrder { get; set; }

    /// <summary><see cref="Sphene.API.Data.Enum.SyncshellGalleryAudience"/> as int -
    /// who may see this album. Photos keep their own audience.</summary>
    public int Audience { get; set; }

    /// <summary>JSON list of member UIDs for <c>SelectedMembers</c> audiences.</summary>
    public string? AllowedUidsJson { get; set; }

    public DateTime CreatedAt { get; set; }
}

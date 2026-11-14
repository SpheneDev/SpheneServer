using System.ComponentModel.DataAnnotations;

namespace SpheneShared.Models;

public class SyncshellCommunityGalleryImage
{
    [Key]
    public long Id { get; set; }

    [MaxLength(40)]
    public string GroupGID { get; set; } = string.Empty;
    public Group Group { get; set; } = null!;

    public string? ImageBase64 { get; set; }

    /// <summary>Hash-addressed image media - preferred over base64 when set.</summary>
    [MaxLength(40)]
    public string? ImageMediaHash { get; set; }

    [MaxLength(100)]
    public string? FileName { get; set; }
    [MaxLength(50)]
    public string? ContentType { get; set; }
    public long? Size { get; set; }

    [MaxLength(200)]
    public string? Caption { get; set; }

    public int SortOrder { get; set; }

    /// <summary>Album this image belongs to, if any.</summary>
    public long? AlbumId { get; set; }

    /// <summary>UID of the uploader - staff uploads and member submissions alike.</summary>
    [MaxLength(64)]
    public string? UploadedByUid { get; set; }

    /// <summary><see cref="Sphene.API.Data.Enum.SyncshellGalleryImageStatus"/> as int.</summary>
    public int Status { get; set; }

    /// <summary><see cref="Sphene.API.Data.Enum.SyncshellGalleryAudience"/> as int -
    /// who may see this photo beyond staff and the uploader.</summary>
    public int Audience { get; set; }

    /// <summary>JSON list of member UIDs for <c>SelectedMembers</c> audiences.</summary>
    public string? AllowedUidsJson { get; set; }

    public DateTime CreatedAt { get; set; }
}

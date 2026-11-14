using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SpheneShared.Models;

/// <summary>
/// One like on a profile gallery photo - composite key (ImageId, LikerUID)
/// keeps every viewer at exactly one like per photo.
/// </summary>
public class UserProfileGalleryImageLike
{
    public long ImageId { get; set; }
    public UserProfileGalleryImage Image { get; set; } = null!;

    [Required]
    [MaxLength(64)]
    public string LikerUID { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
}

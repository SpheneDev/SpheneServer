using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SpheneShared.Models;

/// <summary>A named photo album inside a user's profile gallery.</summary>
public class UserProfileGalleryAlbum
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [Required]
    [MaxLength(64)]
    [ForeignKey(nameof(User))]
    public string UserUID { get; set; } = string.Empty;
    public User User { get; set; } = null!;

    [Required]
    [MaxLength(80)]
    public string Name { get; set; } = string.Empty;

    public int SortOrder { get; set; }

    public DateTime CreatedAt { get; set; }

    public List<UserProfileGalleryImage> Images { get; set; } = [];
}

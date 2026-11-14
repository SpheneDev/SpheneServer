using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SpheneShared.Models;

public class UserProfileGalleryImage
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }

    [Required]
    [MaxLength(64)]
    [ForeignKey(nameof(User))]
    public string UserUID { get; set; }
    public User User { get; set; }

    public string? ImageBase64 { get; set; }

    /// <summary>Hash-addressed image media - preferred over base64 when set.</summary>
    [MaxLength(40)]
    public string? ImageMediaHash { get; set; }

    [MaxLength(100)]
    public string? FileName { get; set; }
    [MaxLength(50)]
    public string? ContentType { get; set; }
    public long? Size { get; set; }

    [MaxLength(280)]
    public string? Caption { get; set; }

    [ForeignKey(nameof(Album))]
    public long? AlbumId { get; set; }
    public UserProfileGalleryAlbum? Album { get; set; }

    public int SortOrder { get; set; }

    public DateTime CreatedAt { get; set; }
}

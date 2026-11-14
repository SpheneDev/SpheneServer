using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SpheneShared.Models;

/// <summary>
/// Ownership/audit record for hash-addressed community media. The binary
/// lives in the existing <see cref="FileCache"/>/file-server pipeline - this
/// row only tracks which community upload produced which hash so uploads
/// dedupe by content and a later GC pass can find orphans.
/// </summary>
public class CommunityMedia
{
    /// <summary>40-char content hash - same scheme/width as FileCache.Hash.</summary>
    [Key]
    [MaxLength(40)]
    public string Hash { get; set; } = string.Empty;

    [Required]
    [MaxLength(64)]
    [ForeignKey(nameof(Uploader))]
    public string UploaderUID { get; set; } = string.Empty;
    public User Uploader { get; set; } = null!;

    [MaxLength(50)]
    public string? ContentType { get; set; }
    public long Size { get; set; }

    /// <summary>Live references counted at last sweep - GC anchor for Phase 6.</summary>
    public int RefCount { get; set; } = 1;

    public DateTime CreatedAt { get; set; }
}

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Sphene.API.Dto.Files;

namespace SpheneShared.Models;

public class PendingFileTransfer
{
    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(20)]
    public string RecipientUID { get; set; } = string.Empty;

    [Required]
    [MaxLength(20)]
    public string SenderUID { get; set; } = string.Empty;

    public string? SenderAlias { get; set; }

    [Required]
    public string Hash { get; set; } = string.Empty;

    public string? ModFolderName { get; set; }

    [Column(TypeName = "jsonb")]
    public List<ModInfoDto>? ModInfo { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

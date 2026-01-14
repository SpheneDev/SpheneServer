using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Sphene.API.Dto.Files;

namespace SpheneShared.Models;

public class PenumbraModBackup
{
    [Key]
    public Guid BackupId { get; set; }

    [MaxLength(10)]
    public string UserUID { get; set; } = string.Empty;

    public string BackupName { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public bool IsComplete { get; set; }

    public int ModCount { get; set; }

    [Column(TypeName = "jsonb")]
    public List<PenumbraModBackupEntryDto> Mods { get; set; } = new();
}

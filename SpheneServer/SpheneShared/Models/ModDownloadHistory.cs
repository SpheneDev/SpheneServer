using System.ComponentModel.DataAnnotations;

namespace SpheneShared.Models;

public class ModDownloadHistory
{
    [Key]
    public long Id { get; set; }

    [MaxLength(10)]
    public string UserUID { get; set; } = string.Empty;

    [MaxLength(40)]
    public string Hash { get; set; } = string.Empty;

    public DateTime DownloadedAt { get; set; } = DateTime.UtcNow;
}


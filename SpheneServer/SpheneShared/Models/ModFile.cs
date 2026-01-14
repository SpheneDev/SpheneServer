using System.ComponentModel.DataAnnotations;

namespace SpheneShared.Models;

public class ModFile
{
    [Key]
    public string Hash { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Author { get; set; }
    public string? Version { get; set; }
    public string? Description { get; set; }
    public string? Website { get; set; }
    public DateTime UploadedDate { get; set; } = DateTime.UtcNow;
}

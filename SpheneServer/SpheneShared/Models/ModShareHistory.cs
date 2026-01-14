using System.ComponentModel.DataAnnotations;

namespace SpheneShared.Models;

public class ModShareHistory
{
    [Key]
    public long Id { get; set; }

    [MaxLength(10)]
    public string SenderUID { get; set; } = string.Empty;

    [MaxLength(10)]
    public string RecipientUID { get; set; } = string.Empty;

    [MaxLength(40)]
    public string Hash { get; set; } = string.Empty;

    public DateTime SharedAt { get; set; } = DateTime.UtcNow;
}

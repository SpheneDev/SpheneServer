using System.ComponentModel.DataAnnotations;

namespace SpheneShared.Models;

public class BannedRegistrations
{
    [Key]
    [MaxLength(100)]
    public string DiscordIdOrLodestoneAuth { get; set; }
}

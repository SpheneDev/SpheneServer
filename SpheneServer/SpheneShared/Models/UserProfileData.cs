using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SpheneShared.Models;

public class UserProfileData
{
    public string Base64ProfileImage { get; set; }
    public bool FlaggedForReport { get; set; }
    public bool IsNSFW { get; set; }
    public bool ProfileDisabled { get; set; }
    public User User { get; set; }

    public string UserDescription { get; set; }

    public string StatusMessage { get; set; }

    public string ProfileTags { get; set; }

    public string Base64BannerImage { get; set; }

    public string Base64RowImage { get; set; }

    public string ProfileTheme { get; set; }

    public int ProfileVisibility { get; set; }

    [Required]
    [Key]
    [ForeignKey(nameof(User))]
    public string UserUID { get; set; }
}
